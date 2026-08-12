using ScreenShare.Plugin.Capture.AudioCapture;
using ScreenShare.Plugin.Capture.ScreenCapture;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Services;

/// <summary>
/// Owns the media-plane WebSocket(s) for both the host-side encoders (capture -&gt; encode -&gt;
/// send) and the viewer-side decoders (receive -&gt; decode -&gt; display/play). Hosting and
/// viewing are two fully independent lifecycles - a room can have multiple simultaneous
/// streamers, and a client should be able to watch one of them while also streaming their own
/// feed, so each role gets its own MediaSocketClient connection (the relay ties one WebSocket to
/// one role) and its own start/stop/error state; nothing here couples the two together. Mirrors
/// RoomSessionService's design otherwise: every public method fires its async work and returns
/// immediately, safe to call directly from Draw(). Status is exposed via plain properties the UI
/// polls each frame.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    // If video's first frame hasn't shown up by the time audio is ready to play, don't hold
    // audio hostage forever waiting for it (a decode failure or unusually slow start shouldn't
    // mean the viewer hears nothing at all) - start it anyway after this, accepting whatever
    // desync results as strictly better than silence.
    private static readonly TimeSpan AudioVideoSyncTimeout = TimeSpan.FromSeconds(10);

    private MediaSocketClient? hostSocket;
    private H264Encoder? videoEncoder;
    private OpusFrameEncoder? audioEncoder;
    private IAudioSource? hostAudioCapture;

    private MediaSocketClient? viewerSocket;
    private H264Decoder? videoDecoder;
    private AudioPlaybackDecoder? audioDecoder;
    private GameAdapterDevice? viewerDevice;
    private string? viewerFfmpegExePath;
    private bool audioReadyToStart;
    private bool videoReadyToStart;

    public bool IsHosting { get; private set; }

    public bool IsViewing { get; private set; }

    /// <summary>Which streamer's feed <see cref="IsViewing"/> refers to, if any - lets the UI
    /// detect the watched streamer going away (dropping out of RoomSessionService.ActiveStreamers)
    /// so it can stop and clear the selection instead of hanging on a dead feed.</summary>
    public string? ViewingStreamerConnectionId { get; private set; }

    public string? HostLastError { get; private set; }

    /// <summary>Same failure as <see cref="HostLastError"/>, but as the real exception rather than
    /// just its message - needed so ShareWindow can pattern-match a DXGI device-removed failure
    /// the same way it already does for the synchronous preview-mode buttons. Without this, a
    /// device-removed failure on this async start path left ShareWindow's stale device reference
    /// in place forever, since TryRecoverFromDeviceRemoved was only ever reachable from
    /// synchronous catch blocks - confirmed by a real DeviceRemoved failure landing here instead.</summary>
    public Exception? HostLastException { get; private set; }

    public string? ViewerLastError { get; private set; }

    /// <summary>See <see cref="HostLastException"/> - same reasoning, kept separate from the host
    /// side now that hosting and viewing are independent connections; a failure on one side should
    /// never surface as the other role's error.</summary>
    public Exception? ViewerLastException { get; private set; }

    // Viewer-facing surface for ShareWindow to render/report.
    public nint? ViewerSharedHandle => videoDecoder?.SharedHandle;

    public int ViewerWidth => videoDecoder?.Width ?? 0;

    public int ViewerHeight => videoDecoder?.Height ?? 0;

    public long ViewerFramesDecoded => videoDecoder?.FramesDecoded ?? 0;

    /// <summary>See H264Decoder.AverageFps - directly comparable to HostAverageFps to see
    /// whether a stream's effective FPS is dropping at capture/encode or somewhere downstream.</summary>
    public double ViewerAverageFps => videoDecoder?.AverageFps ?? 0;

    public long ViewerAudioPackets => audioDecoder?.DecodedPackets ?? 0;

    public float ViewerAudioPeak => audioDecoder?.DecodedPeak ?? 0f;

    /// <summary>See H264Decoder.DroppedChunks - non-zero here means the decoder itself can't keep
    /// up with the incoming stream in real time (distinct from network drops, which would show up
    /// on the HOST's <see cref="HostQueueDrops"/> instead).</summary>
    public long ViewerDecodeDrops => videoDecoder?.DroppedChunks ?? 0;

    /// <summary>See H264Decoder.DisplayQueueDrops - distinct from <see cref="ViewerDecodeDrops"/>:
    /// non-zero here means decode itself is keeping up (or bursting ahead) but the presentation
    /// buffer that paces actual on-screen display is overflowing, not the decoder falling
    /// behind.</summary>
    public long ViewerDisplayDrops => videoDecoder?.DisplayQueueDrops ?? 0;

    // Host-facing surface.
    public long HostFramesEncoded => videoEncoder?.FramesEncoded ?? 0;

    public long HostFramesSkipped => videoEncoder?.FramesSkipped ?? 0;

    /// <summary>See H264Encoder.AverageFps - directly comparable to ViewerAverageFps.</summary>
    public double HostAverageFps => videoEncoder?.AverageFps ?? 0;

    public long HostEncodedBytes => videoEncoder?.EncodedBytes ?? 0;

    /// <summary>See MediaSocketClient.DroppedMessages - non-zero here means this connection's
    /// upload can't keep up with the configured VIDEO bitrate, distinct from HostFramesSkipped
    /// (which only measures whether GPU capture/readback is keeping up, not whether the network
    /// is) and from <see cref="HostQueueAudioDrops"/> (audio has its own queue now).</summary>
    public long HostQueueDrops => hostSocket?.DroppedMessages ?? 0;

    /// <summary>See MediaSocketClient.DroppedAudioMessages - audio has its own send queue,
    /// separate from video, specifically so video backpressure can't crowd it out; non-zero here
    /// means audio itself is being throttled (e.g. genuinely insufficient upload bandwidth for
    /// even the small audio stream), not just competing with video for the same queue.</summary>
    public long HostQueueAudioDrops => hostSocket?.DroppedAudioMessages ?? 0;

    public GpuVendor? HostVendor => videoEncoder?.Vendor;

    public VideoCodec? HostCodec => videoEncoder?.Codec;

    /// <summary>The video encoder's background threads can die mid-stream (GPU stall, ffmpeg
    /// crash, etc.) without StartHostingCoreAsync's try/catch ever seeing it - that only covers
    /// connection-time failures. Surfacing this separately is what makes a silent mid-stream
    /// death visible in the UI instead of just a counter that quietly stops climbing.</summary>
    public Exception? HostVideoError => videoEncoder?.LastError;

    public Exception? ViewerVideoError => videoDecoder?.LastError;

    public void StartHosting(
        string relayUrl,
        string roomId,
        string connectionId,
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource screenCapture,
        IAudioSource? audioCapture,
        string ffmpegExePath,
        int targetHeight,
        int bitrateKbps,
        int fps = 15,
        VideoCodec codec = VideoCodec.H264,
        int audioBitrateBps = 32000)
    {
        StopHosting();
        HostLastError = null;
        HostLastException = null;
        _ = StartHostingCoreAsync(relayUrl, roomId, connectionId, gameAdapterDevice, screenCapture, audioCapture, ffmpegExePath, targetHeight, bitrateKbps, fps, codec, audioBitrateBps);
    }

    private async Task StartHostingCoreAsync(
        string relayUrl,
        string roomId,
        string connectionId,
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource screenCapture,
        IAudioSource? audioCapture,
        string ffmpegExePath,
        int targetHeight,
        int bitrateKbps,
        int fps,
        VideoCodec codec,
        int audioBitrateBps)
    {
        try
        {
            var newSocket = new MediaSocketClient();
            await newSocket.ConnectAsync(relayUrl, roomId, connectionId, isStreamer: true).ConfigureAwait(false);
            hostSocket = newSocket;

            var encoder = new H264Encoder(gameAdapterDevice, screenCapture, ffmpegExePath, targetHeight, bitrateKbps, fps, codec);
            encoder.ChunkEncoded += chunk => hostSocket?.Send(MediaMessageType.Video, chunk);
            videoEncoder = encoder;
            newSocket.SendVideoMetadata(encoder.Width, encoder.Height, codec, fps);

            // A broken audio source (unsupported format, device gone, etc.) shouldn't take the
            // whole stream down with it - video is already live at this point, and killing it
            // over an audio-only failure would be a strictly worse outcome than just streaming
            // without audio. Caught locally rather than letting it propagate to the outer catch
            // below, which calls StopHosting() and would tear down the video encoder too.
            if (audioCapture is not null)
            {
                try
                {
                    WireAudio(newSocket, audioCapture, audioBitrateBps);
                }
                catch (Exception ex)
                {
                    HostLastError = $"Audio disabled: {ex.Message}";
                    HostLastException = ex;
                    ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to wire audio into the media stream - continuing video-only");
                }
            }

            IsHosting = true;
        }
        catch (Exception ex)
        {
            HostLastError = ex.Message;
            HostLastException = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to start hosting the media stream");
            StopHosting();
        }
    }

    private void OnHostSamplesAvailable(float[] samples) => audioEncoder?.Feed(samples);

    private void WireAudio(MediaSocketClient targetSocket, IAudioSource audioCapture, int bitrateBps)
    {
        int sampleRate = audioCapture.WaveFormat.SampleRate;
        int channels = audioCapture.WaveFormat.Channels;

        var opusEncoder = new OpusFrameEncoder(sampleRate, channels, bitrate: bitrateBps);
        opusEncoder.PacketEncoded += packet => hostSocket?.Send(MediaMessageType.Audio, packet);
        audioCapture.SamplesAvailable += OnHostSamplesAvailable;

        audioEncoder = opusEncoder;
        hostAudioCapture = audioCapture;
        targetSocket.SendAudioMetadata(sampleRate, channels);
    }

    private void UnwireAudio()
    {
        if (hostAudioCapture is not null)
            hostAudioCapture.SamplesAvailable -= OnHostSamplesAvailable;
        hostAudioCapture = null;
        audioEncoder = null;
    }

    /// <summary>Lets the host's "Start/Stop system audio capture" button take effect on an
    /// already-running stream without restarting video - confirmed by testing that audio only
    /// got wired in once, at StartHosting time, so toggling it mid-stream previously did nothing
    /// until the whole stream was stopped and restarted.</summary>
    public void UpdateAudioSource(IAudioSource? audioCapture, int audioBitrateBps = 32000)
    {
        if (!IsHosting || hostSocket is not { } activeSocket)
            return;

        UnwireAudio();
        if (audioCapture is not null)
            WireAudio(activeSocket, audioCapture, audioBitrateBps);
    }

    /// <summary>Watches one specific streamer's feed - <paramref name="streamerConnectionId"/>
    /// picks which one, since a room can have more than one active streamer at once and each
    /// gets an independent channel on the relay (see MediaRelay). Switching who you're watching
    /// means calling this again (via StopViewing() + StartViewing) rather than resubscribing an
    /// open connection - there's no protocol support for that on today's binary media socket.
    /// Only tears down an existing WATCH session, never an active hosting session - the two run
    /// on independent connections, so you can watch someone else while streaming your own
    /// feed.</summary>
    public void StartViewing(string relayUrl, string roomId, string connectionId, string streamerConnectionId, GameAdapterDevice gameAdapterDevice, string ffmpegExePath)
    {
        StopViewing();
        ViewerLastError = null;
        ViewerLastException = null;
        viewerDevice = gameAdapterDevice;
        viewerFfmpegExePath = ffmpegExePath;
        ViewingStreamerConnectionId = streamerConnectionId;
        _ = StartViewingCoreAsync(relayUrl, roomId, connectionId, streamerConnectionId);
    }

    private async Task StartViewingCoreAsync(string relayUrl, string roomId, string connectionId, string streamerConnectionId)
    {
        try
        {
            var newSocket = new MediaSocketClient();
            newSocket.VideoMetadataReceived += OnVideoMetadataReceived;
            newSocket.VideoChunkReceived += chunk => videoDecoder?.Feed(chunk);
            newSocket.AudioMetadataReceived += OnAudioMetadataReceived;
            newSocket.AudioPacketReceived += packet => audioDecoder?.Feed(packet);

            await newSocket.ConnectAsync(relayUrl, roomId, connectionId, isStreamer: false, streamerConnectionId).ConfigureAwait(false);
            viewerSocket = newSocket;

            IsViewing = true;
        }
        catch (Exception ex)
        {
            ViewerLastError = ex.Message;
            ViewerLastException = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to start viewing the media stream");
            StopViewing();
        }
    }

    /// <summary>Fires on the socket's background receive loop, not the render thread - texture
    /// creation is safe there (see GameAdapterDevice's doc comment: resource creation on a D3D11
    /// device is thread-safe by default, only the ImmediateContext itself needs external locking).</summary>
    private void OnVideoMetadataReceived(int width, int height, VideoCodec codec, int fps)
    {
        if (videoDecoder is not null || viewerDevice is null || viewerFfmpegExePath is null)
            return;

        try
        {
            var decoder = new H264Decoder(viewerDevice, viewerFfmpegExePath, width, height, fps, codec);
            decoder.ReadyToDisplay += OnVideoReadyToDisplay;
            videoDecoder = decoder;
        }
        catch (Exception ex)
        {
            ViewerLastError = ex.Message;
            ViewerLastException = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to create the viewer's H264Decoder");
        }
    }

    private void OnAudioMetadataReceived(int sampleRate, int channels)
    {
        if (audioDecoder is not null)
            return;

        try
        {
            var decoder = new AudioPlaybackDecoder(sampleRate, channels);
            decoder.ReadyToPlay += OnAudioReadyToPlay;
            audioDecoder = decoder;
        }
        catch (Exception ex)
        {
            ViewerLastError = ex.Message;
            ViewerLastException = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to create the viewer's AudioPlaybackDecoder");
        }
    }

    /// <summary>Both video (once its presentation buffer has enough margin to play smoothly - see
    /// H264Decoder.ReadyToDisplay) and audio (once its own small jitter buffer is full - see
    /// AudioPlaybackDecoder.ReadyToPlay) are held from actually starting until BOTH are ready, per
    /// the same reasoning that motivated video's own jitter buffer: starting each the instant its
    /// own buffer happened to fill, independent of the other, was producing an audible/visible
    /// desync right at the start of playback. Either side falls back to starting alone after
    /// <see cref="AudioVideoSyncTimeout"/> so a stuck decode/format failure on one side can't mean
    /// the other never plays at all.</summary>
    private void OnVideoReadyToDisplay()
    {
        videoReadyToStart = true;
        if (TryStartSyncedPlayback())
            return;

        // Captured locally (not read back through the videoDecoder field later) so a stale
        // timeout firing after a newer session has already replaced it can't accidentally
        // force-start the WRONG session's video; StartDisplay is idempotent regardless.
        H264Decoder? decoderAtTimeoutStart = videoDecoder;
        _ = Task.Delay(AudioVideoSyncTimeout).ContinueWith(
            _ => decoderAtTimeoutStart?.StartDisplay(),
            TaskScheduler.Default);
    }

    private void OnAudioReadyToPlay()
    {
        audioReadyToStart = true;
        if (TryStartSyncedPlayback())
            return;

        // Same reasoning as OnVideoReadyToDisplay's timeout above, mirrored for audio.
        AudioPlaybackDecoder? decoderAtTimeoutStart = audioDecoder;
        _ = Task.Delay(AudioVideoSyncTimeout).ContinueWith(
            _ => decoderAtTimeoutStart?.StartPlayback(),
            TaskScheduler.Default);
    }

    /// <summary>Video is always the primary stream when viewing, so its own readiness always
    /// gates playback; audio only gates it when actually present - a video-only stream (audio
    /// never enabled by the host) would otherwise wait the full AudioVideoSyncTimeout for an
    /// audio decoder that will never exist.</summary>
    private bool TryStartSyncedPlayback()
    {
        if (!videoReadyToStart || videoDecoder is null)
            return false;

        if (audioDecoder is not null && !audioReadyToStart)
            return false;

        videoDecoder.StartDisplay();
        audioDecoder?.StartPlayback();
        return true;
    }

    /// <summary>Tears down only the outgoing broadcast (encoders + host socket) - an active watch
    /// session on the independent viewer socket is left completely untouched.</summary>
    public void StopHosting()
    {
        UnwireAudio();

        videoEncoder?.Dispose();
        videoEncoder = null;

        IsHosting = false;

        MediaSocketClient? oldSocket = hostSocket;
        hostSocket = null;
        if (oldSocket is not null)
            _ = oldSocket.DisposeAsync().AsTask();
    }

    /// <summary>Tears down only the incoming watch session (decoders + viewer socket) - an active
    /// outgoing broadcast on the independent host socket is left completely untouched.</summary>
    public void StopViewing()
    {
        videoDecoder?.Dispose();
        videoDecoder = null;

        audioDecoder?.Dispose();
        audioDecoder = null;

        viewerDevice = null;
        viewerFfmpegExePath = null;
        ViewingStreamerConnectionId = null;
        audioReadyToStart = false;
        videoReadyToStart = false;

        IsViewing = false;

        MediaSocketClient? oldSocket = viewerSocket;
        viewerSocket = null;
        if (oldSocket is not null)
            _ = oldSocket.DisposeAsync().AsTask();
    }

    /// <summary>Full teardown of both roles - for leaving a room or disposing the plugin, where
    /// nothing about either session should survive. Role-specific start/stop (StartHosting,
    /// StartViewing, StopHosting, StopViewing) never calls this; each only ever touches its own
    /// side.</summary>
    public void Stop()
    {
        StopHosting();
        StopViewing();
    }

    public void Dispose() => Stop();
}
