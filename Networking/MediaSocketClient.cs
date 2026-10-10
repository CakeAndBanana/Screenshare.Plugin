using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Networking.Enums;

namespace ScreenShare.Plugin.Networking;

/// <summary>
/// Media socket client that connects to a media relay server and handles sending and receiving media messages.
/// </summary>
public sealed class MediaSocketClient : IAsyncDisposable
{
    private const int SendQueueCapacity = 512;
    private const int AudioQueueCapacity = 256;
    private const long MaxVideoQueueAgeMs = 500;

    private readonly StaleVideoGate staleVideoGate = new(MaxVideoQueueAgeMs);
    private const int StreamerSendBufferBytes = 128 * 1024;
    private static readonly TimeSpan AckInterval = TimeSpan.FromMilliseconds(50);

    private readonly Channel<QueuedVideo> sendQueue = Channel.CreateBounded<QueuedVideo>(new BoundedChannelOptions(SendQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly Channel<byte[]> audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AudioQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly Channel<byte[]> reliableQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private ClientWebSocket? socket;
    private HttpMessageInvoker? invoker;
    private Task? sendLoopTask;
    private Task? receiveLoopTask;
    private Task? ackLoopTask;
    private long lastReceivedPtsMs = long.MinValue;
    private CancellationTokenSource? cts;
    private long droppedMessages;
    private long droppedAudioMessages;
    private long decryptFailures;
    private long decryptedMessages;

    // Set for an end-to-end encrypted room: media is sealed before it is queued and opened as it arrives.
    private RoomCrypto? crypto;

    // In an encrypted room, the byte after the PTS (or after the type, for metadata) carries flags in the clear.
    private const byte KeyframeFlag = 0x01;

    public long DroppedMessages => Interlocked.Read(ref droppedMessages);

    public long DroppedAudioMessages => Interlocked.Read(ref droppedAudioMessages);

    /// <summary>Messages that failed to decrypt: a wrong key, or something altered them on the way.</summary>
    public long DecryptFailures => Interlocked.Read(ref decryptFailures);

    public long DecryptedMessages => Interlocked.Read(ref decryptedMessages);

    public bool IsEncrypted => crypto is not null;

    public event Action<long, byte[]>? VideoChunkReceived;

    public event Action<long, byte[]>? AudioPacketReceived;

    public event Action<int, int, SupportedVideoCodec, int>? VideoMetadataReceived;

    public event Action<int, int>? AudioMetadataReceived;

    public Exception? LastError { get; private set; }

    public bool IsConnected => socket?.State == WebSocketState.Open;

    /// <summary>
    /// Connects to the media relay server with the specified parameters and starts the send and receive loops.
    /// </summary>
    /// <param name="relayUrl">The URL of the media relay server.</param>
    /// <param name="roomId">The ID of the room to connect to.</param>
    /// <param name="connectionId">The ID of the connection.</param>
    /// <param name="isStreamer">A value indicating whether the client is a streamer.</param>
    /// <param name="streamerConnectionId">The ID of the streamer connection (if applicable).</param>
    /// <param name="roomCrypto">The room's media key for an end-to-end encrypted room, or null for an unencrypted one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ConnectAsync(
        string relayUrl, string roomId, string connectionId, bool isStreamer,
        string? streamerConnectionId = null, RoomCrypto? roomCrypto = null, CancellationToken cancellationToken = default)
    {
        crypto = roomCrypto;
        Uri mediaUri = BuildMediaUri(relayUrl, roomId, connectionId, isStreamer, streamerConnectionId);

        // Create a new ClientWebSocket and connect to the media relay server. If the client is a streamer, use a custom HttpMessageInvoker with a small send buffer size for the connection.
        var newSocket = new ClientWebSocket();
        if (isStreamer)
        {
            invoker = new HttpMessageInvoker(new SocketsHttpHandler { ConnectCallback = ConnectWithSmallSendBufferAsync });
            await newSocket.ConnectAsync(mediaUri, invoker, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await newSocket.ConnectAsync(mediaUri, cancellationToken).ConfigureAwait(false);
        }

        // Start the send and receive loops for the connected socket, and if the client is not a streamer, start the acknowledgment loop as well.
        socket = newSocket;
        cts = new CancellationTokenSource();
        sendLoopTask = Task.Run(() => SendLoopAsync(newSocket, cts.Token), cancellationToken);
        receiveLoopTask = Task.Run(() => ReceiveLoopAsync(newSocket, cts.Token), cancellationToken);
        if (!isStreamer)
            ackLoopTask = Task.Run(() => AckLoopAsync(cts.Token), cancellationToken);
    }

    /// <summary>
    /// Connects to the media relay server with a small send buffer size for the socket. 
    /// </summary>
    /// <param name="context">The socket connection context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async ValueTask<Stream> ConnectWithSmallSendBufferAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        // Create a new TCP socket with the specified send buffer size and no delay option. This is used to connect to the media relay server.
        var tcp = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
        {
            NoDelay = true,
            SendBufferSize = StreamerSendBufferBytes,
        };

        // Connect to the media relay server using the provided context and return a NetworkStream for the connected socket. If the connection fails, dispose of the socket and rethrow the exception.
        try
        {
            await tcp.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(tcp, ownsSocket: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tells the relay the newest PTS received, so it can keep what a slow viewer has in flight
    /// bounded (see the relay's ViewerFlowControl). Only sent when something new has arrived.
    /// </summary>
    private async Task AckLoopAsync(CancellationToken cancellationToken)
    {
        // Keep track of the last acknowledged PTS to avoid sending duplicate acknowledgments.
        long ackedPtsMs = long.MinValue;
        using var timer = new PeriodicTimer(AckInterval);
        try
        {
            // Loop that periodically checks for new received PTS values and sends acknowledgments to the media relay server if there are any new PTS values since the last acknowledgment.
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long receivedPtsMs = Interlocked.Read(ref lastReceivedPtsMs);
                if (receivedPtsMs <= ackedPtsMs)
                    continue;

                // Create an acknowledgment message with the new received PTS and send it to the media relay server using the reliable queue.
                byte[] ack = new byte[1 + sizeof(long)];
                ack[0] = (byte)MediaMessageType.Ack;
                BitConverter.GetBytes(receivedPtsMs).CopyTo(ack, 1);
                reliableQueue.Writer.TryWrite(ack);
                ackedPtsMs = receivedPtsMs;
            }
        }
        catch (OperationCanceledException)
        {
            // The loop was canceled, which is expected when the client is disposed or disconnected. No action is needed.
        }
    }

    private void NoteReceivedPts(long ptsMs)
    {
        if (ptsMs > Interlocked.Read(ref lastReceivedPtsMs))
            Interlocked.Exchange(ref lastReceivedPtsMs, ptsMs);
    }

    /// <summary>
    /// Builds the media URI for connecting to the media relay server based on the provided parameters.
    /// </summary>
    /// <param name="relayUrl">The URL of the media relay server.</param>
    /// <param name="roomId">The ID of the room to connect to.</param>
    /// <param name="connectionId">The ID of the connection.</param>
    /// <param name="isStreamer">A value indicating whether the client is a streamer.</param>
    /// <param name="streamerConnectionId">The ID of the streamer connection (if applicable).</param>
    /// <returns>The media URI.</returns>
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

    /// <summary>
    /// Frames a media message: <c>type || pts? || body</c>, or for an encrypted room
    /// <c>type || pts? || flags || nonce || ciphertext || tag</c> with the cleartext prefix authenticated.
    /// </summary>
    /// <param name="type">The type of the media message.</param>
    /// <param name="ptsMs">The presentation timestamp, for video and audio; null for metadata.</param>
    /// <param name="flags">Cleartext flags for the relay (encrypted rooms only).</param>
    /// <param name="body">The message body.</param>
    /// <returns>The framed message.</returns>
    private byte[] Frame(MediaMessageType type, long? ptsMs, byte flags, byte[] body)
    {
        int ptsLength = ptsMs is null ? 0 : sizeof(long);
        int headerLength = 1 + ptsLength + (crypto is null ? 0 : 1);
        Span<byte> header = stackalloc byte[headerLength];
        header[0] = (byte)type;
        if (ptsMs is { } pts)
            BitConverter.TryWriteBytes(header[1..], pts);

        if (crypto is not null)
        {
            header[^1] = flags;
            return crypto.Seal(header, body);
        }

        byte[] framed = new byte[headerLength + body.Length];
        header.CopyTo(framed);
        Buffer.BlockCopy(body, 0, framed, headerLength, body.Length);
        return framed;
    }

    /// <summary>
    /// Queues a framed media message for sending to the media relay server.
    /// </summary>
    /// <param name="type">The type of the media message.</param>
    /// <param name="framed">The framed message.</param>
    /// <param name="isResyncPoint">For encrypted video, whether it is a keyframe; cleartext video is inspected when needed.</param>
    private void Enqueue(MediaMessageType type, byte[] framed, bool isResyncPoint = false)
    {
        // For video and audio metadata, we use the reliable queue to ensure they are sent reliably.
        if (type is MediaMessageType.VideoMetadata or MediaMessageType.AudioMetadata)
        {
            reliableQueue.Writer.TryWrite(framed);
            return;
        }

        // For audio messages, we use a separate audio queue to prioritize audio over video.
        if (type == MediaMessageType.Audio)
        {
            if (audioQueue.Reader.Count >= AudioQueueCapacity)
                Interlocked.Increment(ref droppedAudioMessages);

            audioQueue.Writer.TryWrite(framed);
            return;
        }

        // For video messages, we use the main send queue. If the queue is full, we increment the dropped messages counter.
        if (sendQueue.Reader.Count >= SendQueueCapacity)
            Interlocked.Increment(ref droppedMessages);

        sendQueue.Writer.TryWrite(new QueuedVideo(Stopwatch.GetTimestamp(), framed, isResyncPoint));
    }

    private readonly record struct QueuedVideo(long EnqueuedAt, byte[] Message, bool IsResyncPoint);

    /// <summary>
    /// Sends a video chunk with the specified presentation timestamp (PTS) in milliseconds and the given chunk data to the media relay server.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp (PTS) in milliseconds.</param>
    /// <param name="chunk">The video chunk data.</param>
    public void SendVideoChunk(long ptsMs, byte[] chunk)
    {
        // Once encrypted the chunk can't be inspected, so the keyframe check happens here, on the plaintext.
        bool isResyncPoint = crypto is not null && StaleVideoGate.IsResyncChunk(chunk, staleVideoGate.Codec);
        Enqueue(MediaMessageType.Video, Frame(MediaMessageType.Video, ptsMs, isResyncPoint ? KeyframeFlag : (byte)0, chunk), isResyncPoint);
    }

    /// <summary>
    /// Sends an audio packet with the specified presentation timestamp (PTS) in milliseconds and the given packet data to the media relay server.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp (PTS) in milliseconds.</param>
    /// <param name="packet">The audio packet data.</param>
    public void SendAudioPacket(long ptsMs, byte[] packet)
        => Enqueue(MediaMessageType.Audio, Frame(MediaMessageType.Audio, ptsMs, 0, packet));

    /// <summary>
    /// Sends video metadata with the specified width, height, codec, and frames per second (FPS) to the media relay server.
    /// </summary>
    /// <param name="width">The width of the video.</param>
    /// <param name="height">The height of the video.</param>
    /// <param name="codec">The video codec.</param>
    /// <param name="fps">The frames per second (FPS).</param>
    public void SendVideoMetadata(int width, int height, SupportedVideoCodec codec, int fps)
    {
        // Create a payload with the specified width, height, codec, and FPS
        byte[] payload = new byte[11];
        BitConverter.GetBytes(width).CopyTo(payload, 0);
        BitConverter.GetBytes(height).CopyTo(payload, 4);

        // Ensure the codec value is within the valid range of the SupportedVideoCodec enum
        payload[8] = (byte)codec;
        staleVideoGate.Codec = codec;
        BitConverter.GetBytes((ushort)Math.Clamp(fps, 1, ushort.MaxValue)).CopyTo(payload, 9);
        Enqueue(MediaMessageType.VideoMetadata, Frame(MediaMessageType.VideoMetadata, null, 0, payload));
    }

    /// <summary>
    /// Sends audio metadata with the specified sample rate and number of channels to the media relay server.
    /// </summary>
    /// <param name="sampleRate">The sample rate of the audio.</param>
    /// <param name="channels">The number of channels of the audio.</param>
    public void SendAudioMetadata(int sampleRate, int channels)
    {
        // Create a payload with the specified sample rate and number of channels
        byte[] payload = new byte[8];
        BitConverter.GetBytes(sampleRate).CopyTo(payload, 0);
        BitConverter.GetBytes(channels).CopyTo(payload, 4);
        Enqueue(MediaMessageType.AudioMetadata, Frame(MediaMessageType.AudioMetadata, null, 0, payload));
    }

    /// <summary>
    /// Loop that continuously sends messages from the reliable, audio, and send queues to the media relay server using the provided WebSocket.
    /// It prioritizes reliable messages first, then audio messages, and finally other messages. 
    /// The loop runs until the WebSocket is closed or the cancellation token is triggered.
    /// </summary>
    /// <param name="ws">The WebSocket to send messages through.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns> A task representing the asynchronous operation.</returns>
    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
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

                if (sendQueue.Reader.TryRead(out QueuedVideo video))
                {
                    if (ws.State != WebSocketState.Open)
                        return;

                    long ageMs = (long)Stopwatch.GetElapsedTime(video.EnqueuedAt).TotalMilliseconds;
                    bool admitted = crypto is null
                        ? staleVideoGate.Admit(ageMs, video.Message)
                        : staleVideoGate.Admit(ageMs, video.IsResyncPoint);
                    if (!admitted)
                    {
                        // Counted as a drop so the bitrate adaptation backs off.
                        Interlocked.Increment(ref droppedMessages);
                        continue;
                    }

                    await ws.SendAsync(video.Message, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

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

    /// <summary>
    /// Receives messages from the media relay server using the provided WebSocket and raises events for video chunks, audio packets, and metadata.
    /// </summary>
    /// <param name="ws">The WebSocket to receive messages through.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                // Read the incoming message in chunks until the entire message is received. The message is stored in a MemoryStream for further processing.
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

                // Convert the received message to a byte array and check if it has any content. If the message is empty, continue to the next iteration of the loop.
                byte[] message = messageStream.ToArray();
                if (message.Length == 0)
                    continue;

                // The first byte of the message indicates the type of media message, and the rest is the payload.
                // In an encrypted room the payload is opened first and comes back in the cleartext layout.
                if (!TryGetPayload(message, out byte[] payload))
                    continue;

                switch ((MediaMessageType)message[0])
                {
                    case MediaMessageType.Video when payload.Length >= 8:
                        NoteReceivedPts(BitConverter.ToInt64(payload, 0));
                        VideoChunkReceived?.Invoke(BitConverter.ToInt64(payload, 0), payload[8..]);
                        break;
                    case MediaMessageType.Audio when payload.Length >= 8:
                        NoteReceivedPts(BitConverter.ToInt64(payload, 0));
                        AudioPacketReceived?.Invoke(BitConverter.ToInt64(payload, 0), payload[8..]);
                        break;
                    case MediaMessageType.VideoMetadata when payload.Length >= 11:
                        VideoMetadataReceived?.Invoke(BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4), (SupportedVideoCodec)payload[8], BitConverter.ToUInt16(payload, 9));
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

    /// <summary>
    /// Extracts the payload (everything after the type byte) of a received message, decrypting it for an
    /// encrypted room. The decrypted payload is rebuilt as <c>pts? || body</c>, the same as a cleartext one.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="payload">The payload, if it could be extracted.</param>
    /// <returns>False for a message that failed to decrypt, which is dropped.</returns>
    private bool TryGetPayload(byte[] message, out byte[] payload)
    {
        if (crypto is null)
        {
            payload = message[1..];
            return true;
        }

        bool opened = TryOpenPayload(crypto, message, out payload);
        Interlocked.Increment(ref opened ? ref decryptedMessages : ref decryptFailures);
        return opened;
    }

    /// <summary>
    /// Decrypts a received message from an encrypted room into the cleartext payload layout (<c>pts? || body</c>).
    /// </summary>
    /// <param name="roomCrypto">The room's media key.</param>
    /// <param name="message">The received message, type byte included.</param>
    /// <param name="payload">The decrypted payload.</param>
    /// <returns>False for a message that is malformed, altered, or sealed with a different key.</returns>
    internal static bool TryOpenPayload(RoomCrypto roomCrypto, byte[] message, out byte[] payload)
    {
        payload = [];
        var type = (MediaMessageType)message[0];
        int ptsLength = type is MediaMessageType.Video or MediaMessageType.Audio ? sizeof(long) : 0;
        int headerLength = 1 + ptsLength + 1;
        if (!roomCrypto.TryOpen(message, headerLength, out byte[] body))
            return false;

        payload = new byte[ptsLength + body.Length];
        Buffer.BlockCopy(message, 1, payload, 0, ptsLength);
        Buffer.BlockCopy(body, 0, payload, ptsLength, body.Length);
        return true;
    }

    /// <summary>
    /// Disposes of the MediaSocketClient by canceling the send and receive loops, completing the queues, closing the WebSocket connection, and disposing of resources.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        // Cancel the send and receive loops and complete the queues to stop further processing.
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
            }
        }

        if (sendLoopTask is not null)
        {
            try { await sendLoopTask.ConfigureAwait(false); } catch { }
        }

        if (receiveLoopTask is not null)
        {
            try { await receiveLoopTask.ConfigureAwait(false); } catch { }
        }

        if (ackLoopTask is not null)
        {
            try { await ackLoopTask.ConfigureAwait(false); } catch { }
        }

        socket?.Dispose();
        invoker?.Dispose();
        cts?.Dispose();
    }
}
