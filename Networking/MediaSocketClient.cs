using System.Net.WebSockets;
using System.Threading.Channels;
using ScreenShare.Plugin.Encoding;

namespace ScreenShare.Plugin.Networking;

public enum MediaMessageType : byte
{
    Video = 0,
    Audio = 1,

    /// <summary>Sent once by the host at stream start: int32 width + int32 height + 1-byte
    /// VideoCodec (little-endian). A viewer has no other way to know the host's chosen
    /// capture/encode resolution or codec before the first video chunk arrives, and needs both
    /// up front to size the decoded-frame buffer/texture and pick the right ffmpeg decode format.</summary>
    VideoMetadata = 2,

    /// <summary>Sent once by the host at stream start: int32 sample rate + int32 channel count
    /// (little-endian) - the Opus decoder must be constructed with the exact same values the
    /// encoder used.</summary>
    AudioMetadata = 3,
}

/// <summary>
/// Thin wrapper around a raw WebSocket connection to ScreenShare.Relay's media-plane endpoint -
/// deliberately separate from <see cref="RoomHubClient"/>'s SignalR connection, mirroring the
/// relay's own control-plane/media-plane split (see RoomHub's doc comment) so a large media
/// stream can never delay a control message like "kick".
///
/// <see cref="Send"/> is safe to call from any thread - including encoder callback threads - and
/// never blocks: it just enqueues onto a bounded, drop-oldest channel that a single background
/// task drains into the socket. ClientWebSocket does not allow concurrent SendAsync calls, so
/// funneling every send through one queue/task is required, not just a nicety. Dropping under
/// backpressure mirrors the same tradeoff already made for the local encoder/decoder relay in
/// H264RoundTripValidator: an occasional skip is fine for live video, blocking a capture/encode
/// thread on network I/O is not.
/// </summary>
public sealed class MediaSocketClient : IAsyncDisposable
{
    private const int SendQueueCapacity = 512;
    private const int AudioQueueCapacity = 256;

    // Video chunks - the bulk of outbound traffic by byte volume, and the one queue that's meant
    // to absorb backpressure by dropping under load (that's the acceptable, by-design tradeoff
    // for video: a dropped chunk is comparatively unnoticeable, recoverable by the next frame).
    private readonly Channel<byte[]> sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    // Audio packets - kept in their OWN queue, separate from video, and drained with priority
    // over it. They used to share sendQueue with video chunks, which are far larger/more frequent
    // by byte volume - under real network backpressure, video traffic could (and did, in
    // practice) crowd out and evict audio packets via DropOldest, and a dropped audio packet is a
    // far more perceptible glitch than a dropped video chunk. Still bounded/drop-oldest itself
    // (audio CAN tolerate occasional loss, unlike metadata below) - just no longer competing with
    // video for the same limited queue capacity.
    private readonly Channel<byte[]> audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AudioQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    // VideoMetadata/AudioMetadata are small, sent at most a couple of times per session, and MUST
    // NOT be lost - unlike a dropped chunk/packet (the next one arrives shortly and nothing is
    // permanently broken), losing the one-time metadata message means the receiving side can
    // never construct a decoder at all, no matter how many chunks/packets arrive afterward. This
    // queue is unbounded and drained with priority over both queues above so that can't happen:
    // metadata never waits behind, or gets evicted by, a backlog of routine traffic.
    private readonly Channel<byte[]> reliableQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private ClientWebSocket? socket;
    private Task? sendLoopTask;
    private Task? receiveLoopTask;
    private CancellationTokenSource? cts;
    private long droppedMessages;
    private long droppedAudioMessages;

    /// <summary>How many outbound VIDEO chunks this socket has had to drop because sendQueue
    /// filled up faster than SendLoopAsync could push them over the wire - i.e. the network
    /// couldn't keep up with encode rate. Does not include audio (see <see cref="DroppedAudioMessages"/>,
    /// tracked separately since the two no longer share a queue).</summary>
    public long DroppedMessages => Interlocked.Read(ref droppedMessages);

    /// <summary>How many outbound AUDIO packets got dropped from audioQueue specifically - tracked
    /// separately from <see cref="DroppedMessages"/> now that audio has its own queue, so a
    /// stuttery-audio report can be attributed to the right side of the pipeline.</summary>
    public long DroppedAudioMessages => Interlocked.Read(ref droppedAudioMessages);

    public event Action<byte[]>? VideoChunkReceived;

    public event Action<byte[]>? AudioPacketReceived;

    public event Action<int, int, VideoCodec>? VideoMetadataReceived;

    public event Action<int, int>? AudioMetadataReceived;

    public Exception? LastError { get; private set; }

    public bool IsConnected => socket?.State == WebSocketState.Open;

    /// <summary>
    /// <paramref name="isStreamer"/> selects the role query value the relay authorizes via
    /// RoomState.CanStream (owner or someone the owner granted permission to - "streamer" rather
    /// than "host" now that streaming isn't owner-exclusive). A viewer must also pass
    /// <paramref name="streamerConnectionId"/>, since a room can have more than one active
    /// streamer at once and the relay routes each streamer's bytes through an independent
    /// per-streamer channel (see MediaRelay on the relay) - a viewer subscribes to exactly the
    /// one they picked to watch.
    /// </summary>
    public async Task ConnectAsync(
        string relayUrl, string roomId, string connectionId, bool isStreamer,
        string? streamerConnectionId = null, CancellationToken cancellationToken = default)
    {
        Uri mediaUri = BuildMediaUri(relayUrl, roomId, connectionId, isStreamer, streamerConnectionId);

        var newSocket = new ClientWebSocket();
        await newSocket.ConnectAsync(mediaUri, cancellationToken).ConfigureAwait(false);

        socket = newSocket;
        cts = new CancellationTokenSource();
        sendLoopTask = Task.Run(() => SendLoopAsync(newSocket, cts.Token));
        receiveLoopTask = Task.Run(() => ReceiveLoopAsync(newSocket, cts.Token));
    }

    /// <summary>Derives the media endpoint from the control-plane hub URL (same origin, ws(s)
    /// scheme, /media/{roomId} path) rather than asking callers to track two separate URLs.</summary>
    private static Uri BuildMediaUri(string relayUrl, string roomId, string connectionId, bool isStreamer, string? streamerConnectionId)
    {
        var hubUri = new Uri(relayUrl, UriKind.Absolute);
        string scheme = string.Equals(hubUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        string role = isStreamer ? "streamer" : "viewer";

        string query = $"connectionId={Uri.EscapeDataString(connectionId)}&role={role}";
        if (!isStreamer && streamerConnectionId is not null)
            query += $"&streamerConnectionId={Uri.EscapeDataString(streamerConnectionId)}";

        var builder = new UriBuilder(hubUri)
        {
            Scheme = scheme,
            Path = $"/media/{roomId}",
            Query = query,
        };
        return builder.Uri;
    }

    /// <summary>Never blocks - see the class doc comment for why. Safe to call from encoder
    /// callback threads. Routes each message type onto its own queue - metadata (never-drop,
    /// highest priority), audio (drop-oldest but isolated from video), video (drop-oldest, the
    /// one meant to absorb backpressure) - see each queue field's own doc comment for why.</summary>
    public void Send(MediaMessageType type, byte[] payload)
    {
        byte[] framed = new byte[payload.Length + 1];
        framed[0] = (byte)type;
        Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);

        if (type is MediaMessageType.VideoMetadata or MediaMessageType.AudioMetadata)
        {
            reliableQueue.Writer.TryWrite(framed);
            return;
        }

        if (type == MediaMessageType.Audio)
        {
            if (audioQueue.Reader.Count >= AudioQueueCapacity)
                Interlocked.Increment(ref droppedAudioMessages);

            audioQueue.Writer.TryWrite(framed);
            return;
        }

        // DropOldest's Writer.TryWrite always succeeds (it evicts to make room), so it can't
        // itself signal that a drop happened - checking the queue depth just before writing is
        // the cheap approximation.
        if (sendQueue.Reader.Count >= SendQueueCapacity)
            Interlocked.Increment(ref droppedMessages);

        sendQueue.Writer.TryWrite(framed);
    }

    public void SendVideoMetadata(int width, int height, VideoCodec codec)
    {
        byte[] payload = new byte[9];
        BitConverter.GetBytes(width).CopyTo(payload, 0);
        BitConverter.GetBytes(height).CopyTo(payload, 4);
        payload[8] = (byte)codec;
        Send(MediaMessageType.VideoMetadata, payload);
    }

    public void SendAudioMetadata(int sampleRate, int channels)
    {
        byte[] payload = new byte[8];
        BitConverter.GetBytes(sampleRate).CopyTo(payload, 0);
        BitConverter.GetBytes(channels).CopyTo(payload, 4);
        Send(MediaMessageType.AudioMetadata, payload);
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                // Strict priority order: reliableQueue (metadata) fully drains before a single
                // audio packet goes out, and audioQueue fully drains before a single video chunk
                // goes out. Video is the only queue that ever actually backs up in practice (it's
                // by far the highest byte volume), so in the common/degraded case this reduces to
                // "audio and metadata always go out immediately, video absorbs the backpressure" -
                // exactly the intended tradeoff.
                while (reliableQueue.Reader.TryRead(out byte[]? reliableMessage))
                {
                    if (ws.State != WebSocketState.Open)
                        return;

                    await ws.SendAsync(reliableMessage, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                }

                if (audioQueue.Reader.TryRead(out byte[]? audioMessage))
                {
                    if (ws.State != WebSocketState.Open)
                        return;

                    await ws.SendAsync(audioMessage, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (sendQueue.Reader.TryRead(out byte[]? message))
                {
                    if (ws.State != WebSocketState.Open)
                        return;

                    await ws.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Nothing ready on any queue right now - wait for whichever gets an item first.
                // DisposeAsync always cancels cancellationToken before completing any channel
                // writer, so shutdown surfaces as an OperationCanceledException here rather than
                // this loop ever seeing all three channels merely "completed".
                Task<bool> reliableWait = reliableQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();
                Task<bool> audioWait = audioQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();
                Task<bool> sendWait = sendQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();
                await (await Task.WhenAny(reliableWait, audioWait, sendWait).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    messageStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                byte[] message = messageStream.ToArray();
                if (message.Length == 0)
                    continue;

                byte[] payload = message[1..];
                switch ((MediaMessageType)message[0])
                {
                    case MediaMessageType.Video:
                        VideoChunkReceived?.Invoke(payload);
                        break;
                    case MediaMessageType.Audio:
                        AudioPacketReceived?.Invoke(payload);
                        break;
                    case MediaMessageType.VideoMetadata when payload.Length >= 9:
                        VideoMetadataReceived?.Invoke(BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4), (VideoCodec)payload[8]);
                        break;
                    case MediaMessageType.AudioMetadata when payload.Length >= 8:
                        AudioMetadataReceived?.Invoke(BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts?.Cancel();
        sendQueue.Writer.TryComplete();
        audioQueue.Writer.TryComplete();
        reliableQueue.Writer.TryComplete();

        if (socket is { State: WebSocketState.Open } ws)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best-effort - the other side may already be gone
            }
        }

        if (sendLoopTask is not null)
        {
            try { await sendLoopTask.ConfigureAwait(false); } catch { /* loop logs its own faults */ }
        }

        if (receiveLoopTask is not null)
        {
            try { await receiveLoopTask.ConfigureAwait(false); } catch { /* loop logs its own faults */ }
        }

        socket?.Dispose();
        cts?.Dispose();
    }
}
