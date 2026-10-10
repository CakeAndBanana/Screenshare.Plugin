using System.Diagnostics;
using ScreenShare.Plugin.Capture.AudioCapture.Interfaces;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Encoding.Opus;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Services;

public sealed class MediaSessionService : IDisposable
{
    private static readonly TimeSpan AudioVideoSyncTimeout = TimeSpan.FromSeconds(10);

    private const int AdaptIntervalMs = 3000;

    private MediaSocketClient? hostSocket;
    private IVideoEncoder? videoEncoder;
    private OpusFrameEncoder? audioEncoder;
    private IAudioSource? hostAudioCapture;

    private readonly Lock audioWiringLock = new();
    private IAudioSource? pendingAudioCapture;
    private int pendingAudioBitrateBps = 128000;
    private int hostGeneration;

    private Stopwatch? hostSessionClock;

    private sealed record HostEncodeSettings(
        GameAdapterDevice Device,
        ICaptureSource Capture,
        string FfmpegExePath,
        int TargetHeight,
        int Fps,
        SupportedVideoCodec Codec,
        int ConfiguredBitrateKbps);

    private HostEncodeSettings? hostEncodeSettings;
    private CancellationTokenSource? adaptCts;
    private int currentBitrateKbps;
    private long lastSeenQueueDrops;
    private int cleanAdaptWindows;
    private int bitrateCeilingKbps;
    private int bitrateCeilingWindowsLeft;

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

    // A few failures are enough to tell; one stray message mid-stream isn't.
    private const long DecryptFailuresBeforeWarning = 10;

    /// <summary>
    /// Set when an encrypted stream keeps failing to decrypt and nothing has decrypted yet: the key doesn't match
    /// the streamer's, which only happens if the relay handed out a wrong salt or altered the media.
    /// </summary>
    public string? ViewerDecryptError => viewerSocket is { IsEncrypted: true, DecryptedMessages: 0 } socket
        && socket.DecryptFailures >= DecryptFailuresBeforeWarning
            ? "Can't decrypt this stream. The relay may have tampered with it."
            : null;

    public Exception? ViewerLastException { get; private set; }

    private readonly record struct LocalPreviewSource(nint SharedHandle, int Width, int Height);

    // Set while the streamer watches their own stream: the preview shows the capture texture directly
    // instead of pulling the stream back down from the relay, so it costs no download or decode.
    private LocalPreviewSource? localPreview;

    public bool IsLocalPreview => localPreview is not null;

    public nint? ViewerSharedHandle => localPreview is { } local ? local.SharedHandle : videoDecoder?.SharedHandle;

    public int ViewerWidth => localPreview?.Width ?? videoDecoder?.Width ?? 0;

    public int ViewerHeight => localPreview?.Height ?? videoDecoder?.Height ?? 0;

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

    public SupportedVideoCodec? HostCodec => videoEncoder?.Codec;

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
        SupportedVideoCodec codec = SupportedVideoCodec.H264,
        int audioBitrateBps = 128000,
        RoomCrypto? crypto = null)
    {
        StopHosting();
        HostLastError = null;
        HostLastException = null;

        int generation;
        lock (audioWiringLock)
        {
            pendingAudioCapture = audioCapture;
            pendingAudioBitrateBps = audioBitrateBps;
            generation = hostGeneration;
        }

        _ = StartHostingCoreAsync(generation, relayUrl, roomId, connectionId, gameAdapterDevice, screenCapture, ffmpegExePath, targetHeight, bitrateKbps, fps, codec, crypto);
    }

    private async Task StartHostingCoreAsync(
        int generation,
        string relayUrl,
        string roomId,
        string connectionId,
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource screenCapture,
        string ffmpegExePath,
        int targetHeight,
        int bitrateKbps,
        int fps,
        SupportedVideoCodec codec,
        RoomCrypto? crypto)
    {
        // Everything is built into locals and only published under the lock if no StopHosting ran while connecting;
        // otherwise a stop issued mid-connect would be overwritten and the stream would go live anyway.
        var newSocket = new MediaSocketClient();
        IVideoEncoder? encoder = null;

        try
        {
            await newSocket.ConnectAsync(relayUrl, roomId, connectionId, isStreamer: true, roomCrypto: crypto).ConfigureAwait(false);

            if (Volatile.Read(ref hostGeneration) == generation)
            {
                var sessionClock = Stopwatch.StartNew();

                encoder = VideoEncoderFactory.Create(
                    gameAdapterDevice, screenCapture, ffmpegExePath, sessionClock, targetHeight, bitrateKbps, fps, codec);
                WireVideoChunks(encoder);
                newSocket.SendVideoMetadata(encoder.Width, encoder.Height, codec, fps);

                lock (audioWiringLock)
                {
                    if (hostGeneration == generation)
                    {
                        hostSocket = newSocket;
                        hostSessionClock = sessionClock;
                        hostEncodeSettings = new HostEncodeSettings(
                            gameAdapterDevice, screenCapture, ffmpegExePath, targetHeight, fps, codec, bitrateKbps);
                        currentBitrateKbps = bitrateKbps;
                        lastSeenQueueDrops = 0;
                        cleanAdaptWindows = 0;
                        bitrateCeilingWindowsLeft = 0;
                        videoEncoder = encoder;

                        adaptCts = new CancellationTokenSource();
                        _ = AdaptBitrateLoopAsync(adaptCts.Token);

                        IsHosting = true;
                        if (pendingAudioCapture is { } readyAudioCapture)
                            TryWireAudio(newSocket, readyAudioCapture, pendingAudioBitrateBps);

                        return;
                    }
                }
            }

            ServiceCollector.PluginLog.Information("ScreenShare hosting was stopped while connecting; discarding the new media stream.");
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref hostGeneration) == generation)
            {
                HostLastError = ex.Message;
                HostLastException = ex;
                ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to start hosting the media stream");
                StopHosting();
            }
            else
            {
                ServiceCollector.PluginLog.Verbose(ex, "ScreenShare hosting start failed after it had already been stopped");
            }
        }

        if (encoder is not null)
            _ = Task.Run(() => DisposeSafely(encoder, "H264Encoder (stopped while connecting)"));

        await newSocket.DisposeAsync().ConfigureAwait(false);
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
                    currentBitrateKbps, settings.ConfiguredBitrateKbps, newDrops, cleanAdaptWindows, live,
                    bitrateCeilingKbps, bitrateCeilingWindowsLeft);

                cleanAdaptWindows = decision.CleanWindows;
                bitrateCeilingKbps = decision.CeilingKbps;
                bitrateCeilingWindowsLeft = decision.CeilingWindowsLeft;
                if (!decision.ShouldApply)
                    continue;

                if (live)
                {
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
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Error(ex, "ScreenShare bitrate adaptation stopped after an error; the stream continues at its current bitrate.");
        }
    }

    public bool HostSupportsLiveVideoBitrate => videoEncoder is { SupportsLiveBitrate: true };

    public bool UpdateVideoBitrate(int bitrateKbps)
    {
        if (hostEncodeSettings is not { } settings || videoEncoder is not { SupportsLiveBitrate: true } encoder)
            return false;

        bool reduced = currentBitrateKbps < settings.ConfiguredBitrateKbps;
        int targetKbps = reduced ? Math.Min(currentBitrateKbps, bitrateKbps) : bitrateKbps;

        hostEncodeSettings = settings with { ConfiguredBitrateKbps = bitrateKbps };
        cleanAdaptWindows = 0;

        encoder.SetBitrate(targetKbps);
        currentBitrateKbps = targetKbps;

        ServiceCollector.PluginLog.Information(
            $"ScreenShare video bitrate setting changed to {bitrateKbps}kbps mid-stream; now encoding at {targetKbps}kbps.");
        return true;
    }

    private async Task RestartEncoderAsync(HostEncodeSettings settings, int bitrateKbps, CancellationToken cancellationToken)
    {
        if (hostSessionClock is not { } sessionClock)
            return;

        IVideoEncoder? oldEncoder = videoEncoder;

        try
        {
            IVideoEncoder encoder = VideoEncoderFactory.CreateWithoutMediaFoundation(
                settings.Device, settings.Capture, settings.FfmpegExePath, sessionClock,
                settings.TargetHeight, bitrateKbps, settings.Fps, settings.Codec);
            WireVideoChunks(encoder);

            videoEncoder = encoder;
            currentBitrateKbps = bitrateKbps;

            ServiceCollector.PluginLog.Information(
                $"ScreenShare adapted video bitrate to {bitrateKbps}kbps (configured {settings.ConfiguredBitrateKbps}kbps).");
        }
        catch (Exception ex)
        {
            HostLastError = ex.Message;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare could not restart the encoder at a new bitrate; continuing at the previous one.");
            return;
        }

        if (oldEncoder is not null)
            await Task.Run(() => DisposeSafely(oldEncoder, "H264Encoder (bitrate change)"), cancellationToken).ConfigureAwait(false);
    }

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

    public void UpdateAudioSource(IAudioSource? audioCapture, int audioBitrateBps = 128000)
    {
        lock (audioWiringLock)
        {
            pendingAudioCapture = audioCapture;
            pendingAudioBitrateBps = audioBitrateBps;

            if (!IsHosting || hostSocket is not { } activeSocket)
                return;

            UnwireAudio();
            if (audioCapture is not null)
                TryWireAudio(activeSocket, audioCapture, audioBitrateBps);
        }
    }

    public void StartViewing(string relayUrl, string roomId, string connectionId, string streamerConnectionId, GameAdapterDevice gameAdapterDevice, string ffmpegExePath, RoomCrypto? crypto = null)
    {
        StopViewing();
        ViewerLastError = null;
        ViewerLastException = null;
        viewerDevice = gameAdapterDevice;
        viewerFfmpegExePath = ffmpegExePath;
        ViewingStreamerConnectionId = streamerConnectionId;
        _ = StartViewingCoreAsync(relayUrl, roomId, connectionId, streamerConnectionId, crypto);
    }

    public void StartLocalPreview(string selfConnectionId, ICaptureSource capture)
    {
        StopViewing();
        ViewerLastError = null;
        ViewerLastException = null;
        localPreview = new LocalPreviewSource(capture.SharedHandle, capture.Width, capture.Height);
        ViewingStreamerConnectionId = selfConnectionId;
        IsViewing = true;
    }

    private async Task StartViewingCoreAsync(string relayUrl, string roomId, string connectionId, string streamerConnectionId, RoomCrypto? crypto)
    {
        try
        {
            var newSocket = new MediaSocketClient();
            newSocket.VideoMetadataReceived += OnVideoMetadataReceived;
            newSocket.VideoChunkReceived += (ptsMs, chunk) => videoDecoder?.Feed(ptsMs, chunk);
            newSocket.AudioMetadataReceived += OnAudioMetadataReceived;
            newSocket.AudioPacketReceived += (ptsMs, packet) => audioDecoder?.Feed(ptsMs, packet);

            await newSocket.ConnectAsync(relayUrl, roomId, connectionId, isStreamer: false, streamerConnectionId, crypto).ConfigureAwait(false);
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

    private void OnVideoMetadataReceived(int width, int height, SupportedVideoCodec codec, int fps)
    {
        if (videoDecoder is not null || viewerDevice is null || viewerFfmpegExePath is null)
            return;

        try
        {
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
            hostGeneration++;
            UnwireAudio();

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

        localPreview = null;
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
        lock (audioWiringLock)
        {
            hostGeneration++;
            UnwireAudio();
        }

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
