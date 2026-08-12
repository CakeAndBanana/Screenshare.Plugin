using System.Diagnostics;
using NAudio.Wave;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Network media-plane counterpart to OpusFrameEncoder: decodes Opus packets fed via
/// <see cref="Feed"/> and plays them through the default output device via NAudio. Nothing in
/// this plugin had actually played audio out loud before this - the codec's original local
/// round-trip validation only decoded to compute a peak level, purely to prove the codec round
/// trip worked.
///
/// Uses WaveOutEvent specifically (not the classic WaveOut) because it doesn't need a Win32
/// message pump to drive playback callbacks - it runs its own thread internally, which matters
/// here since nothing in this plugin owns a message loop.
/// </summary>
public sealed class AudioPlaybackDecoder : IDisposable
{
    // Real-time audio needs SOME lead time between "packet arrived" and "sample played" to
    // absorb ordinary network timing variance (jitter) - feeding straight into playback with zero
    // buffer gives the playback pointer no margin against even minor arrival-timing variance, so
    // every small gap in delivery becomes an audible glitch. This is the standard "jitter buffer"
    // technique real-time audio/VoIP playback uses. Kept small deliberately - this used to also be
    // the ONLY thing controlling when playback started at all, which directly became an audio-vs-
    // video sync offset since video has its own, usually LONGER startup latency (spawning ffmpeg,
    // initializing hardware decode, waiting for a keyframe) with no coordination between the two.
    // Actual playback start is now synchronized with video explicitly (see MediaSessionService) -
    // this constant only controls how much margin exists once playback has actually begun.
    private const int PrebufferMilliseconds = 40;

    private readonly Lock playbackLock = new();
    private readonly OpusFrameDecoder decoder;
    private readonly BufferedWaveProvider buffer;
    private readonly WaveOutEvent output;
    private readonly Stopwatch playbackStopwatch = new();
    private bool isReady;
    private bool isPlaying;
    private long? firstPacketPtsMs;

    private long decodedPackets;
    private float decodedPeak;

    public long DecodedPackets => Interlocked.Read(ref decodedPackets);

    public float DecodedPeak => Volatile.Read(ref decodedPeak);

    public Exception? LastError { get; private set; }

    /// <summary>Fires exactly once, when PrebufferMilliseconds worth of audio has actually been
    /// buffered - NOT the same as playback starting (see <see cref="StartPlayback"/>), which the
    /// owner is expected to call once it decides this AND video are both ready, so the two start
    /// together rather than audio racing ahead just because its own buffer filled up first.</summary>
    public event Action? ReadyToPlay;

    /// <summary>Audio's own estimated current playback position, on the shared session-clock PTS
    /// timeline (see MediaSessionService/H264Encoder) - null until StartPlayback has actually been
    /// called. Computed as (PTS of the oldest content that was buffered when playback started) +
    /// (real elapsed time since then) rather than tracked packet-by-packet, because once playing,
    /// NAudio's WaveOutEvent consumes the buffer at exactly the hardware's real playback rate - a
    /// genuinely drift-free clock, unlike anything paced by Thread.Sleep. This is what
    /// H264Decoder.DisplayLoop treats as the master clock to reconcile video against.</summary>
    public long? EstimatedCurrentPtsMs
    {
        get
        {
            lock (playbackLock)
            {
                if (!isPlaying || firstPacketPtsMs is not { } startPtsMs)
                    return null;

                return startPtsMs + playbackStopwatch.ElapsedMilliseconds;
            }
        }
    }

    public AudioPlaybackDecoder(int sampleRate, int channels)
    {
        decoder = new OpusFrameDecoder(sampleRate, channels);

        WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        buffer = new BufferedWaveProvider(waveFormat)
        {
            // Absorbs normal network jitter without growing forever - a listener noticing
            // ~500ms of extra latency is far better than an unbounded memory buildup during a
            // rough patch of network delivery.
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
        };

        output = new WaveOutEvent();
        output.Init(buffer);
        // Deliberately NOT starting playback here - see Feed()/StartPlayback().
    }

    /// <summary>Actually starts audio output - safe to call more than once (only the first call
    /// does anything) and safe to call after this decoder has already been disposed (a stale
    /// caller from a superseded session, e.g. a delayed sync-timeout fallback racing a Stop()on
    /// the same session, is a real possible caller shape - not a hypothetical one - so this
    /// swallows ObjectDisposedException specifically rather than letting it surface as an
    /// unobserved background-task exception).</summary>
    public void StartPlayback()
    {
        lock (playbackLock)
        {
            if (isPlaying)
                return;
            isPlaying = true;
            playbackStopwatch.Start();
        }

        try
        {
            output.Play();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Safe to call from the network receive thread - Opus decode is fast, CPU-only
    /// work (no GPU, no I/O), and BufferedWaveProvider.AddSamples is documented as safe to call
    /// from a different thread than the one driving playback. <paramref name="ptsMs"/> is this
    /// packet's PTS on the shared session clock - only the very FIRST call's value is kept (see
    /// <see cref="EstimatedCurrentPtsMs"/>), since that's the packet that'll be at the front of
    /// the buffer whenever playback eventually starts.</summary>
    public void Feed(long ptsMs, byte[] opusPacket)
    {
        try
        {
            lock (playbackLock)
                firstPacketPtsMs ??= ptsMs;

            float[] samples = decoder.Decode(opusPacket);
            byte[] bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            buffer.AddSamples(bytes, 0, bytes.Length);

            if (!isReady && buffer.BufferedDuration >= TimeSpan.FromMilliseconds(PrebufferMilliseconds))
            {
                isReady = true;
                ReadyToPlay?.Invoke();
            }

            Interlocked.Increment(ref decodedPackets);

            float peak = 0f;
            foreach (float sample in samples)
            {
                float abs = Math.Abs(sample);
                if (abs > peak)
                    peak = abs;
            }

            Volatile.Write(ref decodedPeak, peak);
        }
        catch (Exception ex)
        {
            LastError = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare audio playback decode error");
        }
    }

    public void Dispose()
    {
        output.Stop();
        output.Dispose();
    }
}
