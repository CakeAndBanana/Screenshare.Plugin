using System.Diagnostics;
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

    // How often the send queue is sampled. The rule applied to each sample lives in
    // BitrateAdaptation, where it can be tested without a socket or an encoder.
    private const int AdaptIntervalMs = 3000;

    private MediaSocketClient? hostSocket;
    private IVideoEncoder? videoEncoder;
    private OpusFrameEncoder? audioEncoder;
    private IAudioSource? hostAudioCapture;

    /// <summary>The audio source the host stream should be using, which is NOT always the one it
    /// is currently wired to: a source can finish becoming ready while StartHostingCoreAsync is
    /// still connecting its socket. Held under <see cref="audioWiringLock"/> together with
    /// <see cref="IsHosting"/>'s transition to true, so those two can never interleave.</summary>
    private readonly Lock audioWiringLock = new();
    private IAudioSource? pendingAudioCapture;
    private int pendingAudioBitrateBps = 32000;

    /// <summary>Single clock shared by the video encoder and every audio sample batch for the
    /// whole hosting session, so their PTS values are directly comparable on the viewer's side -
    /// two independently-started clocks (even started moments apart) would make "video chunk PTS
    /// 5000ms" and "audio packet PTS 5000ms" refer to different real moments, defeating the whole
    /// point. Created once per StartHostingCoreAsync call, before either encoder exists.</summary>
    private Stopwatch? hostSessionClock;

    /// <summary>Everything needed to rebuild the video encoder without re-running the whole
    /// hosting setup, so the bitrate can be changed mid-stream - see
    /// <see cref="AdaptBitrateLoopAsync"/>. ffmpeg takes its bitrate as a command-line argument at
    /// spawn, so there is no way to change it on a running encoder; the process has to be replaced.
    /// Kept for that fallback path only - a Media Foundation encoder is retuned in place and never
    /// rebuilt, so none of this is touched when one is in use.</summary>
    private sealed record HostEncodeSettings(
        GameAdapterDevice Device,
        ICaptureSource Capture,
        string FfmpegExePath,
        int TargetHeight,
        int Fps,
        VideoCodec Codec,
        int ConfiguredBitrateKbps);

    private HostEncodeSettings? hostEncodeSettings;
    private CancellationTokenSource? adaptCts;
    private int currentBitrateKbps;
    private long lastSeenQueueDrops;
    private int cleanAdaptWindows;

    /// <summary>The bitrate actually being encoded at right now, which drops below the configured
    /// value while the uplink cannot carry it. Surfaced so the host can see the stream has been
    /// throttled rather than wondering why quality fell.</summary>
    public int HostCurrentBitrateKbps => currentBitrateKbps;

    /// <summary>True while congestion has forced the encoder below the configured bitrate.</summary>
    public bool HostBitrateReduced => hostEncodeSettings is { } settings && currentBitrateKbps < settings.ConfiguredBitrateKbps;

    private MediaSocketClient? viewerSocket;
    private IVideoDecoder? videoDecoder;
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

    /// <summary>Adaptive buffer depth currently in use by the viewer's audio path.</summary>
    public int ViewerAudioTargetMs => audioDecoder?.TargetBufferedMilliseconds ?? 0;

    /// <summary>How far viewer playback sits behind the newest data received - see
    /// AudioPlaybackDecoder.BehindLiveMs. Null before playback starts.</summary>
    public long? ViewerBehindLiveMs => audioDecoder?.BehindLiveMs;

    /// <summary>Measured audio/video drift for the stream being watched - see H264Decoder.</summary>
    public long ViewerAvDriftMs => videoDecoder?.AvDriftMs ?? 0;

    /// <summary>Points the viewer's audio at the in-world screen's virtual speakers. No-ops before
    /// audio metadata has arrived (the decoder doesn't exist yet), which is normal for the first
    /// second or so of a session rather than an error worth reporting. Callers pass
    /// <see cref="SpeakerGains.Identity"/> whenever the screen isn't being projected, so audio
    /// never depends on the in-world feature being on.</summary>
    public void SetViewerSpeakerGains(SpeakerGains gains) => audioDecoder?.SetSpeakerGains(gains);

    /// <summary>Master playback volume for the stream being watched, 0-1. No-ops before audio
    /// metadata has arrived, which is normal for the first second of a session.</summary>
    public void SetViewerVolume(float volume) => audioDecoder?.SetVolume(volume);

    public long ViewerFramesDecoded => videoDecoder?.FramesDecoded ?? 0;

    /// <summary>See H264Decoder.AverageFps - directly comparable to HostAverageFps to see
    /// whether a stream's effective FPS is dropping at capture/encode or somewhere downstream.</summary>
    public double ViewerAverageFps => videoDecoder?.AverageFps ?? 0;

    public long ViewerAudioPackets => audioDecoder?.DecodedPackets ?? 0;

    public float ViewerAudioPeak => audioDecoder?.DecodedPeak ?? 0f;

    /// <summary>How much audio is queued ahead of the output device. Together with the two counters
    /// below this is what makes an A/V sync complaint diagnosable instead of guesswork: audio is
    /// the master clock video reconciles against, so if playback is starved or is dropping to hold
    /// its latency cap, video follows it into the same trouble.</summary>
    public int ViewerAudioBufferedMs => audioDecoder?.BufferedMilliseconds ?? 0;

    /// <summary>Silence played because audio hadn't arrived in time - climbing means the jitter
    /// buffer is too small for this connection.</summary>
    public long ViewerAudioUnderrunMs => audioDecoder?.UnderrunMilliseconds ?? 0;

    /// <summary>Audio discarded to hold the latency cap. A few hundred ms at stream start is
    /// expected (it is the backlog that built up while playback waited for video); climbing
    /// steadily after that means the sender's clock is running faster than this machine's.</summary>
    public long ViewerAudioDroppedMs => audioDecoder?.DroppedMilliseconds ?? 0;

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

    /// <summary>What the encoder is ACTUALLY producing, averaged over the session - not what it was
    /// asked for. The two diverge when an encoder ignores the bitrate it was configured with, which
    /// congests the uplink and then shows up as the network or the viewer's decoder struggling,
    /// nowhere near the encoder that caused it. Cheap to show, and the only way to tell those apart
    /// from the host's side.</summary>
    public double HostAverageBitrateKbps =>
        hostSessionClock is { } clock && videoEncoder is { } encoder
            ? encoder.EncodedBytes * 8 / 1000.0 / Math.Max(clock.Elapsed.TotalSeconds, 0.001)
            : 0;

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

    /// <summary>Which encoder is actually running - the MFT's own name ("NVIDIA H.264 Encoder
    /// MFT") on the Media Foundation path, or the GPU vendor on the ffmpeg one, which is as
    /// specific as that path can be. Worth showing because the two paths are chosen automatically:
    /// this line is how the host can tell which one they got.</summary>
    public string? HostEncoderName => videoEncoder?.EncoderName;

    public VideoCodec? HostCodec => videoEncoder?.Codec;

    /// <summary>The video encoder's background threads can die mid-stream (GPU stall, ffmpeg
    /// crash, etc.) without StartHostingCoreAsync's try/catch ever seeing it - that only covers
    /// connection-time failures. Surfacing this separately is what makes a silent mid-stream
    /// death visible in the UI instead of just a counter that quietly stops climbing.</summary>
    public Exception? HostVideoError => videoEncoder?.LastError;

    public Exception? ViewerVideoError => videoDecoder?.LastError;

    /// <summary>Which decoder is actually running - the MFT's own name when frames are staying on
    /// the GPU, or ffmpeg when they are not. Worth showing because the two are chosen automatically
    /// and per codec: an HEVC stream can fall back on a machine where H.264 would not.</summary>
    public string? ViewerDecoderName => videoDecoder?.DecoderName;

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

        lock (audioWiringLock)
        {
            pendingAudioCapture = audioCapture;
            pendingAudioBitrateBps = audioBitrateBps;
        }

        _ = StartHostingCoreAsync(relayUrl, roomId, connectionId, gameAdapterDevice, screenCapture, ffmpegExePath, targetHeight, bitrateKbps, fps, codec);
    }

    private async Task StartHostingCoreAsync(
        string relayUrl,
        string roomId,
        string connectionId,
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource screenCapture,
        string ffmpegExePath,
        int targetHeight,
        int bitrateKbps,
        int fps,
        VideoCodec codec)
    {
        try
        {
            var newSocket = new MediaSocketClient();
            await newSocket.ConnectAsync(relayUrl, roomId, connectionId, isStreamer: true).ConfigureAwait(false);
            hostSocket = newSocket;

            var sessionClock = Stopwatch.StartNew();
            hostSessionClock = sessionClock;

            hostEncodeSettings = new HostEncodeSettings(
                gameAdapterDevice, screenCapture, ffmpegExePath, targetHeight, fps, codec, bitrateKbps);
            currentBitrateKbps = bitrateKbps;
            lastSeenQueueDrops = 0;
            cleanAdaptWindows = 0;

            IVideoEncoder encoder = VideoEncoderFactory.Create(
                gameAdapterDevice, screenCapture, ffmpegExePath, sessionClock, targetHeight, bitrateKbps, fps, codec);
            encoder.ChunkEncoded += (ptsMs, chunk) => hostSocket?.SendVideoChunk(ptsMs, chunk);
            videoEncoder = encoder;
            newSocket.SendVideoMetadata(encoder.Width, encoder.Height, codec, fps);

            adaptCts = new CancellationTokenSource();
            _ = AdaptBitrateLoopAsync(adaptCts.Token);

            // Read here rather than taken as a parameter, and under the same lock that publishes
            // IsHosting: the connect above takes long enough that an audio source started in the
            // same frame (auto-start does exactly that, and process-loopback activation typically
            // finishes FIRST) would otherwise land in UpdateAudioSource while IsHosting was still
            // false, be dropped, and leave the stream silent for its whole run.
            lock (audioWiringLock)
            {
                IsHosting = true;
                if (pendingAudioCapture is { } readyAudioCapture)
                    TryWireAudio(newSocket, readyAudioCapture, pendingAudioBitrateBps);
            }
        }
        catch (Exception ex)
        {
            HostLastError = ex.Message;
            HostLastException = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to start hosting the media stream");
            StopHosting();
        }
    }

    private void OnHostSamplesAvailable(float[] samples)
    {
        if (hostSessionClock is { } clock)
            audioEncoder?.Feed(clock.ElapsedMilliseconds, samples);
    }

    /// <summary>A broken audio source (unsupported format, device gone, etc.) shouldn't take the
    /// whole stream down with it - video is live by this point, and killing it over an audio-only
    /// failure would be a strictly worse outcome than just streaming without audio. Swallowed here
    /// rather than propagating to StartHostingCoreAsync's outer catch, which calls StopHosting()
    /// and would tear down the video encoder too. UpdateAudioSource shares this for the same
    /// reason: a mid-stream source swap that throws shouldn't escape onto whichever thread
    /// happened to be doing the swapping.</summary>
    private void TryWireAudio(MediaSocketClient targetSocket, IAudioSource audioCapture, int bitrateBps)
    {
        try
        {
            WireAudio(targetSocket, audioCapture, bitrateBps);
        }
        catch (Exception ex)
        {
            HostLastError = $"Audio disabled: {ex.Message}";
            HostLastException = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to wire audio into the media stream - continuing video-only");
        }
    }

    /// <summary>
    /// Watches the send queue and moves the encode bitrate to fit what the uplink can actually
    /// carry.
    ///
    /// The signal is the media socket's own dropped-message count. That queue is bounded and
    /// drop-oldest, so anything discarded there is video the uplink could not carry at the
    /// configured bitrate - a direct measurement of congestion, host-side, needing no feedback
    /// from the viewer and no round trip to observe.
    ///
    /// Down fast and up slow, deliberately: congestion is already hurting when it is detected, so
    /// backing off hard is cheap, whereas probing upward too eagerly just re-congests the link and
    /// oscillates. See <see cref="BitrateAdaptation"/> for the rule itself.
    ///
    /// What a change COSTS depends on the encoder. Media Foundation retunes in place, so
    /// adaptation is invisible and can move in small steps; ffmpeg has to be restarted (see
    /// <see cref="RestartEncoderAsync"/>), so its changes have to clear a much larger threshold to
    /// be worth the hitch they cause.
    /// </summary>
    private async Task AdaptBitrateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AdaptIntervalMs, cancellationToken).ConfigureAwait(false);

                if (hostEncodeSettings is not { } settings || hostSocket is null || !IsHosting)
                    continue;

                long drops = hostSocket.DroppedMessages;
                long newDrops = drops - lastSeenQueueDrops;
                lastSeenQueueDrops = drops;

                bool live = videoEncoder is { SupportsLiveBitrate: true };
                BitrateDecision decision = BitrateAdaptation.Next(
                    currentBitrateKbps, settings.ConfiguredBitrateKbps, newDrops, cleanAdaptWindows, live);

                cleanAdaptWindows = decision.CleanWindows;
                if (!decision.ShouldApply)
                    continue;

                if (live)
                {
                    // The whole point of the Media Foundation encoder: the running encoder is
                    // retuned, so adaptation costs nothing visible and can happen in small steps
                    // instead of the coarse, hitchy jumps a rebuild forces.
                    videoEncoder!.SetBitrate(decision.TargetKbps);
                    currentBitrateKbps = decision.TargetKbps;
                    ServiceCollector.PluginLog.Information(
                        $"ScreenShare retuned video bitrate to {decision.TargetKbps}kbps " +
                        $"(configured {settings.ConfiguredBitrateKbps}kbps).");
                    continue;
                }

                await RestartEncoderAsync(settings, decision.TargetKbps, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Hosting stopped - the ordinary way out of this loop.
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Error(ex, "ScreenShare bitrate adaptation stopped after an error; the stream continues at its current bitrate.");
        }
    }

    /// <summary>
    /// Replaces the video encoder with one at a different bitrate, reusing the ORIGINAL session
    /// clock.
    ///
    /// That clock is the whole reason this is safe to do mid-stream: video and audio PTS are only
    /// comparable because they share it, so a fresh Stopwatch here would restart video timestamps
    /// at zero and destroy synchronisation for the rest of the session. Everything else is rebuilt
    /// through the same constructor the initial setup uses, rather than mutating a running encoder
    /// - the encode pipeline's pacing and readback behaviour are subtle enough that a second code
    /// path through them would be its own source of bugs.
    ///
    /// The socket is deliberately left alone. It stays connected, so the viewer sees a brief gap
    /// followed by a fresh keyframe, which its decoder handles as a normal in-band parameter-set
    /// change rather than as a new stream.
    /// </summary>
    private async Task RestartEncoderAsync(HostEncodeSettings settings, int bitrateKbps, CancellationToken cancellationToken)
    {
        if (hostSessionClock is not { } sessionClock)
            return;

        IVideoEncoder? oldEncoder = videoEncoder;

        try
        {
            // Deliberately the ffmpeg encoder rather than the factory: this path only runs when the
            // encoder in use cannot retune, which means Media Foundation was already unavailable or
            // declined. Going back through the factory would retry it on every adaptation step.
            var encoder = new H264Encoder(
                settings.Device, settings.Capture, settings.FfmpegExePath, sessionClock,
                settings.TargetHeight, bitrateKbps, settings.Fps, settings.Codec);
            encoder.ChunkEncoded += (ptsMs, chunk) => hostSocket?.SendVideoChunk(ptsMs, chunk);

            videoEncoder = encoder;
            currentBitrateKbps = bitrateKbps;

            ServiceCollector.PluginLog.Information(
                $"ScreenShare adapted video bitrate to {bitrateKbps}kbps (configured {settings.ConfiguredBitrateKbps}kbps).");
        }
        catch (Exception ex)
        {
            // Keep the old encoder running rather than leaving the stream with none at all.
            HostLastError = ex.Message;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare could not restart the encoder at a new bitrate; continuing at the previous one.");
            return;
        }

        if (oldEncoder is not null)
            await Task.Run(() => DisposeSafely(oldEncoder, "H264Encoder (bitrate change)"), cancellationToken).ConfigureAwait(false);
    }

    private void WireAudio(MediaSocketClient targetSocket, IAudioSource audioCapture, int bitrateBps)
    {
        int sampleRate = audioCapture.WaveFormat.SampleRate;
        int channels = audioCapture.WaveFormat.Channels;

        var opusEncoder = new OpusFrameEncoder(sampleRate, channels, bitrate: bitrateBps);
        opusEncoder.PacketEncoded += (ptsMs, packet) => hostSocket?.SendAudioPacket(ptsMs, packet);
        audioCapture.SamplesAvailable += OnHostSamplesAvailable;

        audioEncoder = opusEncoder;
        hostAudioCapture = audioCapture;
        targetSocket.SendAudioMetadata(sampleRate, channels);
    }

    private void UnwireAudio()
    {
        hostAudioCapture?.SamplesAvailable -= OnHostSamplesAvailable;
        hostAudioCapture = null;
        audioEncoder = null;
    }

    /// <summary>Lets the host's "Start/Stop system audio capture" button take effect on an
    /// already-running stream without restarting video - confirmed by testing that audio only
    /// got wired in once, at StartHosting time, so toggling it mid-stream previously did nothing
    /// until the whole stream was stopped and restarted.</summary>
    public void UpdateAudioSource(IAudioSource? audioCapture, int audioBitrateBps = 32000)
    {
        lock (audioWiringLock)
        {
            // Recorded even when there's nothing to wire it into yet - see pendingAudioCapture.
            pendingAudioCapture = audioCapture;
            pendingAudioBitrateBps = audioBitrateBps;

            if (!IsHosting || hostSocket is not { } activeSocket)
                return;

            UnwireAudio();
            if (audioCapture is not null)
                TryWireAudio(activeSocket, audioCapture, audioBitrateBps);
        }
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
            newSocket.VideoChunkReceived += (ptsMs, chunk) => videoDecoder?.Feed(ptsMs, chunk);
            newSocket.AudioMetadataReceived += OnAudioMetadataReceived;
            newSocket.AudioPacketReceived += (ptsMs, packet) => audioDecoder?.Feed(ptsMs, packet);

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
            // The raw audio clock plus the user's offset as a separate provider - the decoder needs
            // the offset itself, not just a pre-shifted clock, because it also sizes the
            // presentation queue and the frame-hold cap from it (see H264Decoder). Both read per
            // call rather than captured, so the slider takes effect on the current stream.
            IVideoDecoder decoder = VideoDecoderFactory.Create(
                viewerDevice, viewerFfmpegExePath, width, height, fps, codec,
                () => audioDecoder?.EstimatedCurrentPtsMs,
                () => ServiceCollector.Config.AvSyncOffsetMs,
                ServiceCollector.Config.PlaybackLatencyMs);
            decoder.ReadyToDisplay += OnVideoReadyToDisplay;
            videoDecoder = decoder;
        }
        catch (Exception ex)
        {
            ViewerLastError = ex.Message;
            ViewerLastException = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to create the viewer's video decoder");
        }
    }

    private void OnAudioMetadataReceived(int sampleRate, int channels)
    {
        if (audioDecoder is not null)
            return;

        try
        {
            // The user's latency budget, read at construction: it sets both the device buffer and
            // the jitter buffer, neither of which can be resized on a live output device.
            var decoder = new AudioPlaybackDecoder(sampleRate, channels, ServiceCollector.Config.PlaybackLatencyMs);
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
        IVideoDecoder? decoderAtTimeoutStart = videoDecoder;
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
    /// session on the independent viewer socket is left completely untouched.
    ///
    /// StartHosting/StopHosting/StartViewing/StopViewing are all called directly from ShareWindow's
    /// Draw() (a button click), so anything synchronous here runs on the game's own render thread.
    /// H264Encoder.Dispose() joins its background threads with a bounded-but-still-real 1s timeout
    /// each - blocking the render thread for up to that long is a real, reported stutter every time
    /// streaming is stopped, not just a rare edge case. Fields are still nulled and IsHosting
    /// flipped synchronously (so the UI reflects "stopped" immediately), but the actual encoder
    /// disposal is fired onto a background task, mirroring the socket's own DisposeAsync fire-and-
    /// forget pattern below.</summary>
    public void StopHosting()
    {
        lock (audioWiringLock)
        {
            UnwireAudio();

            // Dropped rather than kept: the caller owns this source and is free to dispose it once
            // the stream is stopped, so holding it would leave a disposed source to be wired into
            // the next stream.
            pendingAudioCapture = null;
        }

        adaptCts?.Cancel();
        adaptCts?.Dispose();
        adaptCts = null;
        hostEncodeSettings = null;

        IVideoEncoder? oldEncoder = videoEncoder;
        videoEncoder = null;
        if (oldEncoder is not null)
            _ = Task.Run(() => DisposeSafely(oldEncoder, "H264Encoder"));

        hostSessionClock = null;
        IsHosting = false;

        MediaSocketClient? oldSocket = hostSocket;
        hostSocket = null;
        if (oldSocket is not null)
            _ = oldSocket.DisposeAsync().AsTask();
    }

    /// <summary>Tears down only the incoming watch session (decoders + viewer socket) - an active
    /// outgoing broadcast on the independent host socket is left completely untouched. Disposal is
    /// backgrounded for the same reason as StopHosting above - H264Decoder.Dispose() has the same
    /// shape of bounded-but-blocking thread joins.</summary>
    public void StopViewing()
    {
        IVideoDecoder? oldVideoDecoder = videoDecoder;
        videoDecoder = null;
        if (oldVideoDecoder is not null)
            _ = Task.Run(() => DisposeSafely(oldVideoDecoder, "H264Decoder"));

        AudioPlaybackDecoder? oldAudioDecoder = audioDecoder;
        audioDecoder = null;
        if (oldAudioDecoder is not null)
            _ = Task.Run(() => DisposeSafely(oldAudioDecoder, "AudioPlaybackDecoder"));

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

    /// <summary>Full teardown of both roles - for leaving a room, where nothing about either
    /// session should survive but the render thread still shouldn't block (this runs from a
    /// button click during normal play, same as StopHosting/StopViewing individually). Role-
    /// specific start/stop never calls this; each only ever touches its own side. NOT used by
    /// <see cref="Dispose"/> - see its own doc comment for why plugin-unload teardown deliberately
    /// stays synchronous instead.</summary>
    public void Stop()
    {
        StopHosting();
        StopViewing();
    }

    /// <summary>Runs a disposal off the render thread (see StopHosting/StopViewing) and logs
    /// rather than lets any failure become an unobserved background-task exception - Dispose()
    /// isn't expected to throw, but it's no longer running somewhere a normal try/catch around the
    /// call site would ever see it either way.</summary>
    private static void DisposeSafely(IDisposable disposable, string typeName)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Error(ex, $"ScreenShare failed to dispose a stopped {typeName}");
        }
    }

    /// <summary>Plugin-unload teardown - deliberately synchronous, unlike StopHosting/StopViewing/
    /// Stop() above (all backgrounded to avoid blocking the render thread during normal play).
    /// Dalamud expects a plugin's Dispose to have fully released everything before its
    /// AssemblyLoadContext unloads; a background thread still running past that point risks the
    /// exact "stale assembly still loaded" class of problem this project already hit once with
    /// audio capture. Socket disposal stays fire-and-forget even here, matching this class's
    /// original design from before today's hosting/viewing split - only the encoder/decoder
    /// threads (the ones with real Join-based shutdown) need to actually finish before this
    /// returns.</summary>
    public void Dispose()
    {
        UnwireAudio();

        videoEncoder?.Dispose();
        videoEncoder = null;

        videoDecoder?.Dispose();
        videoDecoder = null;

        audioDecoder?.Dispose();
        audioDecoder = null;

        IsHosting = false;
        IsViewing = false;

        MediaSocketClient? oldHostSocket = hostSocket;
        hostSocket = null;
        if (oldHostSocket is not null)
            _ = oldHostSocket.DisposeAsync().AsTask();

        MediaSocketClient? oldViewerSocket = viewerSocket;
        viewerSocket = null;
        if (oldViewerSocket is not null)
            _ = oldViewerSocket.DisposeAsync().AsTask();
    }
}
