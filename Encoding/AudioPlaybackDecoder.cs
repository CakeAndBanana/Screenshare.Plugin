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
///
/// Audio is also the master clock the video decoder syncs against, so
/// <see cref="EstimatedCurrentPtsMs"/> being honest matters as much as playback itself - see
/// <see cref="SyncedPlaybackBuffer"/> for what that takes and what it replaced.
/// </summary>
public sealed class AudioPlaybackDecoder : IDisposable
{
    // The latency budget, and why it is this small.
    //
    // Total audio latency is what the jitter buffer holds PLUS what the output device holds, and
    // both are paid before a sample is heard. Video cannot follow an audio clock that sits further
    // back than its own presentation queue reaches: that queue is bounded drop-oldest, so frames
    // older than PresentationBufferMilliseconds are already gone. Ask DisplayLoop to show one and
    // it can't - it holds the oldest frame it still has, waiting on a clock that is structurally
    // behind it and never catches up, which reads as video juddering while audio runs late.
    //
    // NAudio's own default device latency is 300ms, which blows the entire budget before the
    // jitter buffer holds anything at all. That default alone made this unfixable, and lowering it
    // is what buys room for a jitter buffer that still works.
    private const int OutputDeviceLatencyMilliseconds = 100;

    // 3 buffers of ~33ms rather than NAudio's default 2: same total latency, but the device is
    // refilled more often, so a late refill has somewhere to fall back to instead of underrunning.
    private const int OutputDeviceBuffers = 3;

    // What is left of the budget once the device has taken its share. Doubles as the prebuffer
    // threshold - there is no point starting playback with less in hand than steady state needs.
    private const int MaxBufferedMilliseconds =
        H264Decoder.PresentationBufferMilliseconds - OutputDeviceLatencyMilliseconds;

    private readonly Lock playbackLock = new();
    private readonly OpusFrameDecoder decoder;
    private readonly SyncedPlaybackBuffer playbackBuffer;
    private readonly SpeakerMixWaveProvider speakerMix;
    private readonly WaveOutEvent output;
    private bool isReady;
    private bool isPlaying;

    private long decodedPackets;
    private float decodedPeak;

    public long DecodedPackets => Interlocked.Read(ref decodedPackets);

    public float DecodedPeak => Volatile.Read(ref decodedPeak);

    /// <summary>How much audio is queued ahead of the output device right now. Steady state should
    /// hover near <see cref="MaxBufferedMilliseconds"/>; sitting near zero means playback is living
    /// hand to mouth and any hiccup will be audible.</summary>
    public int BufferedMilliseconds => playbackBuffer.BufferedMilliseconds;

    /// <summary>Total silence played because audio hadn't arrived in time.</summary>
    public long UnderrunMilliseconds => playbackBuffer.UnderrunMilliseconds;

    /// <summary>Total audio discarded to hold latency at the cap.</summary>
    public long DroppedMilliseconds => playbackBuffer.DroppedMilliseconds;

    public Exception? LastError { get; private set; }

    /// <summary>Fires exactly once, when <see cref="MaxBufferedMilliseconds"/> worth of audio has
    /// actually been buffered - NOT the same as playback starting (see <see cref="StartPlayback"/>),
    /// which the owner is expected to call once it decides this AND video are both ready, so the
    /// two start together rather than audio racing ahead just because its own buffer filled first.</summary>
    public event Action? ReadyToPlay;

    /// <summary>Audio's current playback position on the shared session-clock PTS timeline (see
    /// MediaSessionService/H264Encoder) - null until playback has actually started and real audio
    /// has reached the speakers. This is what H264Decoder.DisplayLoop treats as the master clock.
    ///
    /// Read from the device's own reported playback position rather than estimated from elapsed
    /// wall time. The wall-clock version this replaced was open-loop - it assumed playback began at
    /// the first packet ever received and had advanced at exactly real time ever since, and nothing
    /// ever checked. Every underrun, every discarded packet and the entire contents of the device's
    /// own buffers were invisible to it, and the errors only accumulated.</summary>
    public long? EstimatedCurrentPtsMs
    {
        get
        {
            lock (playbackLock)
            {
                if (!isPlaying)
                    return null;
            }

            try
            {
                return playbackBuffer.GetPtsAtPlayedBytes(output.GetPosition());
            }
            catch (Exception)
            {
                // The device can be torn down underneath a display thread that is still asking.
                // Null just puts H264Decoder back on fixed-interval pacing, which is the right
                // answer for "there is no audio clock any more" anyway.
                return null;
            }
        }
    }

    public AudioPlaybackDecoder(int sampleRate, int channels)
    {
        decoder = new OpusFrameDecoder(sampleRate, channels);

        WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        playbackBuffer = new SyncedPlaybackBuffer(waveFormat, MaxBufferedMilliseconds);

        output = new WaveOutEvent
        {
            DesiredLatency = OutputDeviceLatencyMilliseconds,
            NumberOfBuffers = OutputDeviceBuffers,
        };
        // Sits between the jitter buffer and the device so the in-world screen's virtual speakers
        // can pan and attenuate playback. Format-preserving by construction, which is what keeps
        // output.GetPosition() meaning the same thing to GetPtsAtPlayedBytes as it did before -
        // see SpeakerMixWaveProvider. Starts at identity, so audio is bit-identical to the
        // unspatialized path until something actually calls SetSpeakerGains.
        speakerMix = new SpeakerMixWaveProvider(playbackBuffer);

        output.Init(speakerMix);
        // Deliberately NOT starting playback here - see Feed()/StartPlayback().
    }

    /// <summary>Sets the channel mix the output ramps toward - see
    /// <see cref="SpeakerMixWaveProvider"/>. Called every frame from the render thread while the
    /// in-world screen is up, and with <see cref="SpeakerGains.Identity"/> whenever it isn't.</summary>
    public void SetSpeakerGains(SpeakerGains gains) => speakerMix.SetGains(gains);

    /// <summary>Master playback volume for this stream, 0-1.</summary>
    public void SetVolume(float volume) => speakerMix.SetVolume(volume);

    /// <summary>Actually starts audio output - safe to call more than once (only the first call
    /// does anything) and safe to call after this decoder has already been disposed (a stale
    /// caller from a superseded session, e.g. a delayed sync-timeout fallback racing a Stop() on
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
        }

        try
        {
            output.Play();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Safe to call from the network receive thread - Opus decode is fast, CPU-only work
    /// (no GPU, no I/O), and SyncedPlaybackBuffer takes its own lock. <paramref name="ptsMs"/> is
    /// this packet's PTS on the shared session clock, and unlike the previous implementation every
    /// packet's timestamp is kept, not just the first: the buffer carries each one through to
    /// playback so the master clock reports what is actually being heard.</summary>
    public void Feed(long ptsMs, byte[] opusPacket)
    {
        try
        {
            float[] samples = decoder.Decode(opusPacket);
            byte[] bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            // Nothing drains the buffer until StartPlayback, and that waits for video - which can
            // take seconds. No trimming is needed for that case: Add's own cap already leaves the
            // newest audio in hand rather than the oldest, so playback starts at the live edge
            // however long the wait turns out to be.
            playbackBuffer.Add(ptsMs, bytes);

            if (!isReady && playbackBuffer.BufferedMilliseconds >= MaxBufferedMilliseconds)
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
