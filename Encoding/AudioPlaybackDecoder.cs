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
    // absorb ordinary network timing variance (jitter) - starting playback the instant the
    // buffer is constructed (as this used to) gives the playback pointer zero margin against
    // even minor arrival-timing variance, so every small gap in delivery becomes an audible
    // glitch (heard as "jittery" audio) - not because packets are being lost, just because
    // there's no cushion between when they arrive and when they're needed. This is the standard
    // "jitter buffer" technique real-time audio/VoIP playback uses; 150ms is a common, modest
    // choice for it - enough headroom to absorb ordinary variance without adding noticeable lag
    // for a "watch a stream" use case (unlike interactive voice chat, a bit more latency here is
    // a fully acceptable tradeoff for smoothness, the same tradeoff already made for video's ~2s
    // keyframe interval).
    private const int PrebufferMilliseconds = 150;

    private readonly OpusFrameDecoder decoder;
    private readonly BufferedWaveProvider buffer;
    private readonly WaveOutEvent output;
    private bool playbackStarted;

    private long decodedPackets;
    private float decodedPeak;

    public long DecodedPackets => Interlocked.Read(ref decodedPackets);

    public float DecodedPeak => Volatile.Read(ref decodedPeak);

    public Exception? LastError { get; private set; }

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
        // Deliberately NOT starting playback here - see Feed(), which starts it only once
        // PrebufferMilliseconds worth of audio is actually buffered.
    }

    /// <summary>Safe to call from the network receive thread - Opus decode is fast, CPU-only
    /// work (no GPU, no I/O), and BufferedWaveProvider.AddSamples is documented as safe to call
    /// from a different thread than the one driving playback.</summary>
    public void Feed(byte[] opusPacket)
    {
        try
        {
            float[] samples = decoder.Decode(opusPacket);
            byte[] bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            buffer.AddSamples(bytes, 0, bytes.Length);

            if (!playbackStarted && buffer.BufferedDuration >= TimeSpan.FromMilliseconds(PrebufferMilliseconds))
            {
                playbackStarted = true;
                output.Play();
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
