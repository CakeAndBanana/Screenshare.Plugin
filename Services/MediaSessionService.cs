using System.Diagnostics;
using ScreenShare.Plugin.Capture.AudioCapture.Interfaces;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Services;

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

    private readonly Lock audioWiringLock = new();
    private IAudioSource? pendingAudioCapture;
    private int pendingAudioBitrateBps = 32000;

    private Stopwatch? hostSessionClock;

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

    public int HostCurrentBitrateKbps => currentBitrateKbps;

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

    public string? ViewingStreamerConnectionId { get; private set; }

    public string? HostLastError { get; private set; }

    public Exception? HostLastException { get; private set; }

    public string? ViewerLastError { get; private set; }

    public Exception? ViewerLastException { get; private set; }

    // Viewer-facing surface for ShareWindow to render/report.
    public nint? ViewerSharedHandle => videoDecoder?.SharedHandle;

    public int ViewerWidth => videoDecoder?.Width ?? 0;

    public int ViewerHeight => videoDecoder?.Height ?? 0;

    public int ViewerAudioTargetMs => audioDecoder?.TargetBufferedMilliseconds ?? 0;

    public long? ViewerBehindLiveMs => audioDecoder?.BehindLiveMs;

    public long ViewerAvDriftMs => videoDecoder?.AvDriftMs ?? 0;

    public void SetViewerSpeakerGains(SpeakerGains gains) => audioDecoder?.SetSpeakerGains(gains);

    public void SetViewerVolume(float volume) => audioDecoder?.SetVolume(volume);

    public long ViewerFramesDecoded => videoDecoder?.FramesDecoded ?? 0;

    public double ViewerAverageFps => videoDecoder?.AverageFps ?? 0;

    public long ViewerAudioPackets => audioDecoder?.DecodedPackets ?? 0;

    public float ViewerAudioPeak => audioDecoder?.DecodedPeak ?? 0f;

    public int ViewerAudioBufferedMs => audioDecoder?.BufferedMilliseconds ?? 0;

    public long ViewerAudioUnderrunMs => audioDecoder?.UnderrunMilliseconds ?? 0;

    public long ViewerAudioDroppedMs => audioDecoder?.DroppedMilliseconds ?? 0;

    public long ViewerDecodeDrops => videoDecoder?.DroppedChunks ?? 0;

    public long ViewerDisplayDrops => videoDecoder?.DisplayQueueDrops ?? 0;

    // Host-facing surface.
    public long HostFramesEncoded => videoEncoder?.FramesEncoded ?? 0;

    public long HostFramesSkipped => videoEncoder?.FramesSkipped ?? 0;

    public double HostAverageFps => videoEncoder?.AverageFps ?? 0;

    public long HostEncodedBytes => videoEncoder?.EncodedBytes ?? 0;

    public double HostAverageBitrateKbps =>
        hostSessionClock is { } clock && videoEncoder is { } encoder
            ? encoder.EncodedBytes * 8 / 1000.0 / Math.Max(clock.Elapsed.TotalSeconds, 0.001)
            : 0;

    public long HostQueueDrops => hostSocket?.DroppedMessages ?? 0;

    public long HostQueueAudioDrops => hostSocket?.DroppedAudioMessages ?? 0;

    public string? HostEncoderName => videoEncoder?.EncoderName;

    public VideoCodec? HostCodec => videoEncoder?.Codec;

    public Exception? HostVideoError => videoEncoder?.LastError;

    public Exception? ViewerVideoError => videoDecoder?.LastError;

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
            WireVideoChunks(encoder);
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
            WireVideoChunks(encoder);

            // The handover itself: WireVideoChunks mutes whichever encoder is not this field's
            // value, so the outgoing one stops putting bytes on the wire at exactly the assignment
            // that makes the new one start. Deliberately still BEFORE the old encoder is disposed,
            // so a construction failure above leaves the previous encoder both alive and live.
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

    /// <summary>
    /// Sends an encoder's output on the wire for exactly as long as it is the session's encoder.
    ///
    /// The identity check is not defensive tidiness. Bitrate adaptation on an encoder that cannot
    /// retune (ffmpeg always; a Media Foundation encoder whose ICodecAPI was unavailable) replaces
    /// it outright, and the outgoing encoder keeps producing until its process actually dies -
    /// which is well after the replacement has started. Without this, two INDEPENDENT streams,
    /// each with its own SPS/PPS and its own frame numbering, interleave on the one connection for
    /// the length of the swap, and a viewer's decoder resolves references against whichever
    /// stream's frames it happened to keep.
    /// </summary>
    private void WireVideoChunks(IVideoEncoder encoder)
    {
        encoder.ChunkEncoded += (ptsMs, chunk) =>
        {
            if (!ReferenceEquals(videoEncoder, encoder))
                return;

            hostSocket?.SendVideoChunk(ptsMs, chunk);
        };
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

    public void Stop()
    {
        StopHosting();
        StopViewing();
    }

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
