using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Networking.Enums;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace MediaProbe;

/// <summary>What <see cref="StreamMonitor"/> needs from a viewer's media connection.</summary>
internal interface IViewerSocket : IAsyncDisposable
{
    event Action<long, byte[]>? VideoChunkReceived;

    event Action<long, byte[]>? AudioPacketReceived;

    event Action<int, int, SupportedVideoCodec, int>? VideoMetadataReceived;

    event Action<int, int>? AudioMetadataReceived;

    bool IsConnected { get; }

    Exception? LastError { get; }

    Task ConnectAsync(string relayUrl, string roomId, string connectionId, string streamerConnectionId);
}

/// <summary>The plugin's own client, so a normal watch exercises exactly what a viewer runs.</summary>
internal sealed class PluginViewerSocket : IViewerSocket
{
    private readonly MediaSocketClient client = new();

    public event Action<long, byte[]>? VideoChunkReceived
    {
        add => client.VideoChunkReceived += value;
        remove => client.VideoChunkReceived -= value;
    }

    public event Action<long, byte[]>? AudioPacketReceived
    {
        add => client.AudioPacketReceived += value;
        remove => client.AudioPacketReceived -= value;
    }

    public event Action<int, int, SupportedVideoCodec, int>? VideoMetadataReceived
    {
        add => client.VideoMetadataReceived += value;
        remove => client.VideoMetadataReceived -= value;
    }

    public event Action<int, int>? AudioMetadataReceived
    {
        add => client.AudioMetadataReceived += value;
        remove => client.AudioMetadataReceived -= value;
    }

    public bool IsConnected => client.IsConnected;

    public Exception? LastError => client.LastError;

    public Task ConnectAsync(string relayUrl, string roomId, string connectionId, string streamerConnectionId) =>
        client.ConnectAsync(relayUrl, roomId, connectionId, isStreamer: false, streamerConnectionId);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

/// <summary>
/// A viewer on a slow line: reads the media socket no faster than <c>limitKbps</c>, so the
/// backlog builds in the relay's per-viewer queue the way it would for a real viewer whose
/// download can't keep up. The OS receive buffer is pinned small (which also switches off
/// Windows' receive-window auto-tuning); otherwise it would soak up megabytes of backlog and hide
/// it from the relay.
/// </summary>
internal sealed class ThrottledViewerSocket(int limitKbps, bool sendAcks) : IViewerSocket
{
    private const int ReceiveBufferBytes = 64 * 1024;
    private const int ReadSliceBytes = 4 * 1024;

    private readonly CancellationTokenSource cts = new();
    private ClientWebSocket? socket;
    private HttpMessageInvoker? invoker;
    private Task? receiveLoop;
    private Task? ackLoop;
    private long lastReceivedPtsMs = long.MinValue;

    public event Action<long, byte[]>? VideoChunkReceived;

    public event Action<long, byte[]>? AudioPacketReceived;

    public event Action<int, int, SupportedVideoCodec, int>? VideoMetadataReceived;

    public event Action<int, int>? AudioMetadataReceived;

    public bool IsConnected => socket?.State == WebSocketState.Open;

    public Exception? LastError { get; private set; }

    public async Task ConnectAsync(string relayUrl, string roomId, string connectionId, string streamerConnectionId)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    ReceiveBufferSize = ReceiveBufferBytes,
                };

                try
                {
                    await tcp.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(tcp, ownsSocket: true);
                }
                catch
                {
                    tcp.Dispose();
                    throw;
                }
            },
        };

        invoker = new HttpMessageInvoker(handler);
        socket = new ClientWebSocket();
        await socket.ConnectAsync(BuildViewerUri(relayUrl, roomId, connectionId, streamerConnectionId), invoker, cts.Token)
            .ConfigureAwait(false);

        receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, cts.Token));
        if (sendAcks)
            ackLoop = Task.Run(() => AckLoopAsync(socket, cts.Token));
    }

    // Same acks as MediaSocketClient's viewer side; without them the relay treats this as a
    // plugin that predates flow control.
    private async Task AckLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        long ackedPtsMs = long.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) && ws.State == WebSocketState.Open)
            {
                long receivedPtsMs = Interlocked.Read(ref lastReceivedPtsMs);
                if (receivedPtsMs <= ackedPtsMs)
                    continue;

                byte[] ack = [(byte)MediaMessageType.Ack, .. BitConverter.GetBytes(receivedPtsMs)];
                await ws.SendAsync(ack, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                ackedPtsMs = receivedPtsMs;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError ??= ex;
        }
    }

    private static Uri BuildViewerUri(string relayUrl, string roomId, string connectionId, string streamerConnectionId)
    {
        var hubUri = new Uri(relayUrl, UriKind.Absolute);
        return new UriBuilder(hubUri)
        {
            Scheme = string.Equals(hubUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = $"/media/{roomId}",
            Query = $"connectionId={Uri.EscapeDataString(connectionId)}&role=viewer" +
                $"&streamerConnectionId={Uri.EscapeDataString(streamerConnectionId)}",
        }.Uri;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReadSliceBytes];
        double bytesPerMs = limitKbps * 1000 / 8.0 / 1000;
        var clock = Stopwatch.StartNew();
        long received = 0;

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    message.Write(buffer, 0, result.Count);
                    received += result.Count;

                    // Hold the read back until the line would have delivered this much.
                    long aheadMs = (long)(received / bytesPerMs) - clock.ElapsedMilliseconds;
                    if (aheadMs > 5)
                        await Task.Delay(TimeSpan.FromMilliseconds(aheadMs), cancellationToken).ConfigureAwait(false);
                }
                while (!result.EndOfMessage);

                Dispatch(message.ToArray());
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

    // Same framing as MediaSocketClient's receive loop.
    private void Dispatch(byte[] message)
    {
        if (message.Length == 0)
            return;

        byte[] payload = message[1..];
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
                VideoMetadataReceived?.Invoke(BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4),
                    (SupportedVideoCodec)payload[8], BitConverter.ToUInt16(payload, 9));
                break;
            case MediaMessageType.AudioMetadata when payload.Length >= 8:
                AudioMetadataReceived?.Invoke(BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4));
                break;
        }
    }

    private void NoteReceivedPts(long ptsMs)
    {
        if (ptsMs > Interlocked.Read(ref lastReceivedPtsMs))
            Interlocked.Exchange(ref lastReceivedPtsMs, ptsMs);
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        if (ackLoop is not null)
        {
            try { await ackLoop.ConfigureAwait(false); } catch { }
        }

        if (socket is { State: WebSocketState.Open } ws)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (receiveLoop is not null)
        {
            try { await receiveLoop.ConfigureAwait(false); } catch { }
        }

        socket?.Dispose();
        invoker?.Dispose();
        cts.Dispose();
    }
}
