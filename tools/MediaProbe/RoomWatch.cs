using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Opus;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Networking.Models;
using System.Diagnostics;

namespace MediaProbe;

/// <summary>
/// Joins a live room as a headless viewer and reports what a real viewer would suffer: video
/// frames that never arrive, audio packets that never arrive or decode badly, and anything that
/// shows up too late for the playout buffer. Decodes audio (cheap, and where "sounds bad" lives)
/// but not video - a missing or late access unit is visible from its timestamp alone.
/// </summary>
internal static class RoomWatch
{
    private const int ReportSeconds = 5;

    public static async Task<int> RunAsync(string relayInput, string roomId, string password, string displayName,
        string? streamerName, TimeSpan? duration, int bufferMs, int? limitKbps, bool sendAcks, string? logPath)
    {
        if (!RelayUrl.TryNormalize(relayInput, out string hubUrl))
        {
            Console.Error.WriteLine($"'{relayInput}' is not a relay address.");
            return 2;
        }

        using var log = new WatchLog(logPath);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        if (duration is { } limit)
            stop.CancelAfter(limit);

        await using var hub = new RoomHubClient();
        var gate = new SemaphoreSlim(1, 1);
        StreamMonitor? monitor = null;
        RoomCrypto? crypto = null;
        var totals = new StreamTotals();
        string? endReason = null;

        async Task FollowAsync(IReadOnlyCollection<ActiveStreamerInfo> streamers)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ActiveStreamerInfo? target = streamerName is null
                    ? streamers.FirstOrDefault()
                    : streamers.FirstOrDefault(s => s.DisplayName.Contains(streamerName, StringComparison.OrdinalIgnoreCase));

                if (target?.ConnectionId == monitor?.StreamerConnectionId)
                    return;

                if (monitor is not null)
                {
                    log.Event($"stopped watching {monitor.StreamerName}");
                    totals.Add(await monitor.StopAsync().ConfigureAwait(false));
                    monitor = null;
                }

                if (target is null)
                {
                    log.Event("nobody is streaming; waiting");
                    return;
                }

                var next = new StreamMonitor(log, target.ConnectionId, target.DisplayName, bufferMs, limitKbps, sendAcks);
                await next.StartAsync(hub.RelayUrl!, roomId, hub.ConnectionId!, crypto).ConfigureAwait(false);
                monitor = next;
                log.Event($"watching {target.DisplayName}");
            }
            catch (Exception ex)
            {
                log.Event($"could not attach to the stream: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        hub.ActiveStreamersChanged += streamers => _ = FollowAsync(streamers);
        hub.Kicked += () => { endReason = "kicked from the room"; stop.Cancel(); };
        hub.RoomClosed += () => { endReason = "the room closed"; stop.Cancel(); };
        hub.Reconnecting += () => log.Event("control connection lost, reconnecting");
        hub.Reconnected += () => log.Event("control connection back");
        hub.Closed += () => { endReason = "the control connection closed"; stop.Cancel(); };

        log.Event($"connecting to {hubUrl}");
        await hub.ConnectAsync(hubUrl).ConfigureAwait(false);

        // Hold the monitor gate until the media key is in place, so a streamer announced right after the join
        // isn't watched without it.
        await gate.WaitAsync().ConfigureAwait(false);
        JoinRoomResult joined;
        try
        {
            // Same as the plugin: the relay checks a value derived from the password, never the password itself.
            string auth = password.Length == 0 ? string.Empty : RoomCrypto.DeriveAuth(password);
            joined = await hub.JoinRoomAsync(roomId, auth, $"probe-{Guid.NewGuid():N}", displayName).ConfigureAwait(false);
            if (!joined.Success)
            {
                Console.Error.WriteLine($"Could not join room {roomId}: {joined.Error}");
                return 2;
            }

            if (joined.KeySalt is { } keySalt)
            {
                if (!RoomCrypto.IsSupported)
                {
                    Console.Error.WriteLine($"Room {roomId} is end-to-end encrypted, but AES-GCM isn't available here.");
                    return 2;
                }

                crypto = RoomCrypto.Create(password, keySalt);
                log.Event("room is end-to-end encrypted; decrypting with the key from --password");
            }
        }
        finally
        {
            gate.Release();
        }

        log.Event($"joined \"{joined.RoomName}\" on {joined.InstanceId} ({hub.RelayUrl}) as \"{displayName}\"");
        log.Event($"treating anything more than {bufferMs}ms behind the earliest packet as late; Ctrl+C for the summary");
        if (limitKbps is { } downloadKbps)
            log.Event($"simulating a {downloadKbps}kbps download - reading the media socket no faster than that" +
                (sendAcks ? string.Empty : ", without acks (as a plugin that predates flow control)"));

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(ReportSeconds), stop.Token).ConfigureAwait(false);
                if (monitor is { } current)
                    log.Line(current.TakeWindowReport());
            }
        }
        catch (OperationCanceledException)
        {
        }

        if (endReason is not null)
            log.Event($"stopping: {endReason}");

        await gate.WaitAsync().ConfigureAwait(false);
        if (monitor is not null)
            totals.Add(await monitor.StopAsync().ConfigureAwait(false));

        try
        {
            await hub.LeaveRoomAsync(roomId).ConfigureAwait(false);
        }
        catch
        {
        }

        return totals.Report(log) ? 0 : 1;
    }
}

internal sealed class StreamMonitor(WatchLog log, string streamerConnectionId, string streamerName, int bufferMs, int? limitKbps, bool sendAcks)
{
    private const double ClipLevel = 0.999;
    private const float ClickJump = 0.6f;
    private const int AudioGapMs = 60;
    private const int AudioPacketMs = 20;
    private const int AudioSettlePackets = 50;

    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly IViewerSocket socket = limitKbps is { } limit
        ? new ThrottledViewerSocket(limit, sendAcks)
        : new PluginViewerSocket();
    private readonly StreamTotals totals = new();
    private Window window = new();

    private int fps;
    private SupportedVideoCodec codec;
    private long lastVideoPts = long.MinValue;
    private long lastKeyframePts = long.MinValue;
    private long videoTransitFloor = long.MaxValue;
    private bool videoGapPending;

    private OpusFrameDecoder? opus;
    private long lastAudioPts = long.MinValue;
    private long audioGapStartPts = long.MinValue;
    private long audioGapStep;
    private int audioPacketsSinceGap;
    private long audioTransitFloor = long.MaxValue;
    private float lastSample;
    private bool socketClosedReported;

    public string StreamerConnectionId => streamerConnectionId;

    public string StreamerName => streamerName;

    public async Task StartAsync(string relayUrl, string roomId, string selfConnectionId, RoomCrypto? crypto)
    {
        socket.VideoMetadataReceived += OnVideoMetadata;
        socket.AudioMetadataReceived += OnAudioMetadata;
        socket.VideoChunkReceived += OnVideo;
        socket.AudioPacketReceived += OnAudio;
        await socket.ConnectAsync(relayUrl, roomId, selfConnectionId, streamerConnectionId, crypto).ConfigureAwait(false);
    }

    public async Task<StreamTotals> StopAsync()
    {
        await socket.DisposeAsync().ConfigureAwait(false);
        lock (gate)
        {
            totals.Add(window);
            window = new Window();
            return totals;
        }
    }

    private void OnVideoMetadata(int width, int height, SupportedVideoCodec newCodec, int newFps)
    {
        lock (gate)
        {
            fps = newFps;
            codec = newCodec;
            lastVideoPts = long.MinValue;
        }

        log.Event($"video {width}x{height} {newCodec} @ {newFps}fps");
    }

    private void OnAudioMetadata(int sampleRate, int channels)
    {
        lock (gate)
        {
            opus = new OpusFrameDecoder(sampleRate, channels);
            lastAudioPts = long.MinValue;
            audioGapStartPts = long.MinValue;
        }

        log.Event($"audio {sampleRate}Hz x{channels} Opus");
    }

    private void OnVideo(long ptsMs, byte[] chunk)
    {
        long now = clock.ElapsedMilliseconds;
        lock (gate)
        {
            bool keyframe = IsKeyframe(chunk, codec);
            window.VideoFrames++;
            window.VideoBytes += chunk.Length;
            if (keyframe)
            {
                window.Keyframes++;
                if (lastKeyframePts != long.MinValue)
                    window.KeyframeIntervals.Add(ptsMs - lastKeyframePts);
                lastKeyframePts = ptsMs;
            }

            if (videoGapPending)
            {
                videoGapPending = false;
                if (keyframe)
                    window.Notes.Add("  (resumed on a keyframe - the relay's catch-up drop, or the encoder restarting)");
            }

            if (lastVideoPts != long.MinValue)
            {
                long step = ptsMs - lastVideoPts;
                double interval = 1000.0 / Math.Max(fps, 1);
                if (step < 0)
                {
                    window.VideoBackwards++;
                    Note($"video PTS went back {-step}ms");
                }
                else if (step > Math.Max(interval * 2.5, 50))
                {
                    int missing = (int)Math.Round(step / interval) - 1;
                    window.VideoGaps++;
                    window.VideoFramesMissing += missing;
                    videoGapPending = true;
                    Note($"video gap {step}ms (~{missing} frame(s) missing)");
                }
            }

            lastVideoPts = ptsMs;
            window.VideoDelays.Add(Delay(now, ptsMs, ref videoTransitFloor, ref window.VideoLate, "video"));
        }
    }

    private void OnAudio(long ptsMs, byte[] packet)
    {
        long now = clock.ElapsedMilliseconds;
        lock (gate)
        {
            window.AudioPackets++;

            if (lastAudioPts != long.MinValue)
            {
                long step = ptsMs - lastAudioPts;
                if (step < 0)
                {
                    window.AudioBackwards++;
                    Note($"audio PTS went back {-step}ms");
                }
                else if (step > AudioGapMs && audioGapStartPts == long.MinValue)
                {
                    audioGapStartPts = lastAudioPts;
                    audioGapStep = step;
                    audioPacketsSinceGap = 0;
                }
            }

            if (audioGapStartPts != long.MinValue && ++audioPacketsSinceGap >= AudioSettlePackets)
                ResolveAudioGap(ptsMs);

            lastAudioPts = ptsMs;
            window.AudioDelays.Add(Delay(now, ptsMs, ref audioTransitFloor, ref window.AudioLate, "audio"));

            if (opus is null)
                return;

            float[] pcm;
            try
            {
                pcm = opus.Decode(packet);
            }
            catch (Exception ex)
            {
                window.AudioDecodeErrors++;
                Note($"audio packet ({packet.Length}B) would not decode: {ex.Message}");
                return;
            }

            float peak = 0;
            bool silent = true;
            for (int i = 0; i < pcm.Length; i++)
            {
                float sample = pcm[i];
                if (float.IsNaN(sample) || float.IsInfinity(sample))
                {
                    window.AudioNaN++;
                    continue;
                }

                float magnitude = Math.Abs(sample);
                peak = Math.Max(peak, magnitude);
                if (magnitude >= ClipLevel)
                    window.AudioClipped++;
                if (magnitude > 1e-4f)
                    silent = false;
            }

            // First sample of the first channel against the last one of the previous packet: a
            // jump this large between consecutive 20ms packets is audible as a click.
            if (pcm.Length > 0 && Math.Abs(pcm[0] - lastSample) > ClickJump)
                window.AudioClicks++;

            if (pcm.Length >= opus.Channels)
                lastSample = pcm[^opus.Channels];

            window.AudioPeak = Math.Max(window.AudioPeak, peak);
            if (silent)
                window.AudioSilentPackets++;
        }
    }

    // A jump in audio PTS alone doesn't mean audio was lost: the host stamps packets with the
    // capture callback's time, so a capture stall shows as one big step followed by a batch of
    // packets that share a timestamp. A second of packets later, whatever the packet count still
    // falls short of the PTS span is what actually never arrived.
    private void ResolveAudioGap(long ptsMs)
    {
        long missingMs = (ptsMs - audioGapStartPts) - (audioPacketsSinceGap * AudioPacketMs);
        if (missingMs > AudioGapMs)
        {
            window.AudioGaps++;
            window.AudioMsMissing += missingMs;
            Note($"audio gap: ~{missingMs}ms of packets never arrived ({audioGapStep}ms PTS jump)");
        }
        else
        {
            window.AudioStalls++;
            Note($"audio PTS jumped {audioGapStep}ms, then caught up - delayed, nothing lost");
        }

        audioGapStartPts = long.MinValue;
    }

    // How far behind the fastest-ever packet this one arrived. The host's clock and ours never
    // agree, so the floor stands in for "no queueing anywhere"; what is above it is delay a
    // viewer's playout buffer has to absorb.
    private long Delay(long arrivalMs, long ptsMs, ref long transitFloor, ref int late, string kind)
    {
        long transit = arrivalMs - ptsMs;
        if (transit < transitFloor)
            transitFloor = transit;

        long delay = transit - transitFloor;
        if (delay > bufferMs)
        {
            late++;
            Note($"{kind} arrived {delay}ms behind - past the {bufferMs}ms buffer");
        }

        return delay;
    }

    private void Note(string message)
    {
        if (window.Notes.Count < 20)
            window.Notes.Add(message);
        else if (window.Notes.Count == 20)
            window.Notes.Add("  (more this window, not shown)");
    }

    public string TakeWindowReport()
    {
        Window closed;
        lock (gate)
        {
            closed = window;
            window = new Window();
            totals.Add(closed);
        }

        double seconds = closed.Elapsed.Elapsed.TotalSeconds;
        string line =
            $"video {closed.VideoFrames / seconds,5:F1}fps {closed.VideoBytes * 8 / seconds / 1000,5:F0}kbps " +
            $"key {closed.Keyframes} every {(closed.KeyframeIntervals.Count > 0 ? closed.KeyframeIntervals.Average() : 0),4:F0}ms gaps {closed.VideoGaps} late {closed.VideoLate} " +
            $"delay p95 {Percentile(closed.VideoDelays, 0.95),4}ms max {Max(closed.VideoDelays),4}ms | " +
            $"audio {closed.AudioPackets / seconds,4:F1}pk/s gaps {closed.AudioGaps} stalls {closed.AudioStalls} late {closed.AudioLate} " +
            $"bad {closed.AudioDecodeErrors + closed.AudioNaN} clip {closed.AudioClipped} clicks {closed.AudioClicks} " +
            $"peak {Dbfs(closed.AudioPeak)}";

        if (closed.VideoFrames == 0 && closed.AudioPackets == 0)
            line += "  <- nothing arrived";
        if (!socket.IsConnected && !socketClosedReported)
        {
            socketClosedReported = true;
            line += $"\n  media socket closed{(socket.LastError is { } error ? $": {error.Message}" : string.Empty)}";
        }

        foreach (string note in closed.Notes)
            line += "\n  ! " + note;

        return line;
    }

    private static bool IsKeyframe(byte[] chunk, SupportedVideoCodec codec)
    {
        for (int i = 0; i + 3 < chunk.Length; i++)
        {
            if (chunk[i] != 0 || chunk[i + 1] != 0 || chunk[i + 2] != 1)
                continue;

            byte header = chunk[i + 3];
            if (codec == SupportedVideoCodec.Hevc)
            {
                int type = (header >> 1) & 0x3F;
                if (type is >= 16 and <= 21)
                    return true;
            }
            else if ((header & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }

    internal static long Percentile(List<long> values, double fraction)
    {
        if (values.Count == 0)
            return 0;

        values.Sort();
        return values[(int)Math.Min(values.Count - 1, Math.Ceiling(values.Count * fraction) - 1)];
    }

    internal static long Max(List<long> values) => values.Count == 0 ? 0 : values.Max();

    internal static string Dbfs(float peak) => peak <= 0 ? "silent" : $"{20 * Math.Log10(peak):F0}dBFS";
}

internal sealed class Window
{
    public readonly Stopwatch Elapsed = Stopwatch.StartNew();
    public readonly List<string> Notes = [];
    public readonly List<long> VideoDelays = [];
    public readonly List<long> AudioDelays = [];
    public readonly List<long> KeyframeIntervals = [];
    public int VideoFrames, Keyframes, VideoGaps, VideoFramesMissing, VideoBackwards, VideoLate;
    public long VideoBytes;
    public int AudioPackets, AudioGaps, AudioStalls, AudioBackwards, AudioLate, AudioDecodeErrors, AudioNaN, AudioClicks, AudioSilentPackets;
    public long AudioMsMissing, AudioClipped;
    public float AudioPeak;
}

internal sealed class StreamTotals
{
    private readonly List<long> videoDelays = [];
    private readonly List<long> audioDelays = [];
    private TimeSpan watched;
    private int videoFrames, videoGaps, videoFramesMissing, videoBackwards, videoLate;
    private int audioPackets, audioGaps, audioStalls, audioBackwards, audioLate, audioDecodeErrors, audioNaN, audioClicks, audioSilentPackets;
    private long audioMsMissing, audioClipped;

    public void Add(Window window)
    {
        watched += window.Elapsed.Elapsed;
        videoDelays.AddRange(window.VideoDelays);
        audioDelays.AddRange(window.AudioDelays);
        videoFrames += window.VideoFrames;
        videoGaps += window.VideoGaps;
        videoFramesMissing += window.VideoFramesMissing;
        videoBackwards += window.VideoBackwards;
        videoLate += window.VideoLate;
        audioPackets += window.AudioPackets;
        audioGaps += window.AudioGaps;
        audioStalls += window.AudioStalls;
        audioBackwards += window.AudioBackwards;
        audioLate += window.AudioLate;
        audioDecodeErrors += window.AudioDecodeErrors;
        audioNaN += window.AudioNaN;
        audioClicks += window.AudioClicks;
        audioSilentPackets += window.AudioSilentPackets;
        audioMsMissing += window.AudioMsMissing;
        audioClipped += window.AudioClipped;
    }

    public void Add(StreamTotals other)
    {
        watched += other.watched;
        videoDelays.AddRange(other.videoDelays);
        audioDelays.AddRange(other.audioDelays);
        videoFrames += other.videoFrames;
        videoGaps += other.videoGaps;
        videoFramesMissing += other.videoFramesMissing;
        videoBackwards += other.videoBackwards;
        videoLate += other.videoLate;
        audioPackets += other.audioPackets;
        audioGaps += other.audioGaps;
        audioStalls += other.audioStalls;
        audioBackwards += other.audioBackwards;
        audioLate += other.audioLate;
        audioDecodeErrors += other.audioDecodeErrors;
        audioNaN += other.audioNaN;
        audioClicks += other.audioClicks;
        audioSilentPackets += other.audioSilentPackets;
        audioMsMissing += other.audioMsMissing;
        audioClipped += other.audioClipped;
    }

    // True when nothing a viewer would notice turned up.
    public bool Report(WatchLog log)
    {
        log.Line(string.Empty);
        log.Line($"watched {watched:hh\\:mm\\:ss}");

        if (videoFrames == 0 && audioPackets == 0)
        {
            log.Line("  no media arrived at all - nobody streamed while the probe was in the room");
            return true;
        }

        bool videoOk = videoGaps == 0 && videoBackwards == 0 && videoLate == 0;
        bool audioOk = audioGaps == 0 && audioBackwards == 0 && audioLate == 0 && audioDecodeErrors == 0 && audioNaN == 0;

        Report(log, "video", videoOk,
            $"{videoFrames} frames, {videoGaps} gap(s) (~{videoFramesMissing} frames missing), {videoBackwards} PTS reversal(s), " +
            $"{videoLate} late; delay p50 {StreamMonitor.Percentile(videoDelays, 0.5)}ms p99 {StreamMonitor.Percentile(videoDelays, 0.99)}ms " +
            $"max {StreamMonitor.Max(videoDelays)}ms");
        Report(log, "audio", audioOk,
            $"{audioPackets} packets, {audioGaps} gap(s) ({audioMsMissing}ms missing), " +
            $"{audioStalls} delayed batch(es) with nothing lost, {audioBackwards} PTS reversal(s), " +
            $"{audioLate} late, {audioDecodeErrors} undecodable, {audioNaN} NaN samples; delay p50 " +
            $"{StreamMonitor.Percentile(audioDelays, 0.5)}ms p99 {StreamMonitor.Percentile(audioDelays, 0.99)}ms " +
            $"max {StreamMonitor.Max(audioDelays)}ms");
        log.Line($"        {"audio level",-6} {audioClipped} clipped samples, {audioClicks} click-like jumps, " +
            $"{audioSilentPackets} silent packets (informational - these can be in the source itself)");

        return videoOk && audioOk;
    }

    private static void Report(WatchLog log, string what, bool healthy, string detail) =>
        log.Line($"  {(healthy ? "OK  " : "FAIL")}  {what,-6} {detail}");
}

internal sealed class WatchLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter? file;

    public WatchLog(string? path)
    {
        if (path is not null)
            file = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Event(string message) => Line($"{DateTime.Now:HH:mm:ss} * {message}");

    public void Line(string message)
    {
        string stamped = message.Length > 0 && !char.IsDigit(message[0]) && !message.StartsWith(' ')
            ? $"{DateTime.Now:HH:mm:ss}   {message}"
            : message;

        lock (gate)
        {
            Console.WriteLine(stamped);
            file?.WriteLine(stamped);
        }
    }

    public void Dispose() => file?.Dispose();
}
