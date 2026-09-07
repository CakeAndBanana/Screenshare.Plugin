using NAudio.Wave;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

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
    private const int DefaultOutputDeviceLatencyMilliseconds = 100;

    // 3 buffers of ~33ms rather than NAudio's default 2: same total latency, but the device is
    // refilled more often, so a late refill has somewhere to fall back to instead of underrunning.
    private const int OutputDeviceBuffers = 3;

    // What is left of the budget once the device has taken its share. Doubles as the prebuffer
    // threshold - there is no point starting playback with less in hand than steady state needs.
    private static (int DeviceMs, int BufferMs) SplitLatencyBudget(int totalMs)
    {
        int total = Math.Clamp(totalMs, MinLatencyBudgetMs, MaxLatencyBudgetMs);

        // Everything above the device's share goes to the jitter buffer, not the device. A deeper
        // DEVICE buffer adds latency without absorbing burstiness any better - the device drains at
        // a fixed rate either way - whereas the jitter buffer is precisely the thing that soaks up
        // uneven arrival. So the device stays at its default ceiling and the rest accumulates where
        // it does some good.
        int device = Math.Clamp(total / 2, 40, DefaultOutputDeviceLatencyMilliseconds);
        return (device, Math.Max(total - device, 40));
    }

    internal const int MinLatencyBudgetMs = 80;
    internal const int MaxLatencyBudgetMs = 600;

    private readonly Lock playbackLock = new();
    private readonly OpusFrameDecoder decoder;
    private readonly SyncedPlaybackBuffer playbackBuffer;
    private readonly int maxBufferedMilliseconds;
    private long newestFedPtsMs;
    private readonly SpeakerMixWaveProvider speakerMix;
    private readonly WaveOutEvent output;
    private bool isReady;
    private bool isPlaying;

    private long decodedPackets;
    private float decodedPeak;

    public long DecodedPackets => Interlocked.Read(ref decodedPackets);

    public float DecodedPeak => Volatile.Read(ref decodedPeak);

    public int BufferedMilliseconds => playbackBuffer.BufferedMilliseconds;

    public long UnderrunMilliseconds => playbackBuffer.UnderrunMilliseconds;

    public int TargetBufferedMilliseconds => playbackBuffer.TargetMilliseconds;

    public long DroppedMilliseconds => playbackBuffer.DroppedMilliseconds;

    public Exception? LastError { get; private set; }

    public long? BehindLiveMs
    {
        get
        {
            if (EstimatedCurrentPtsMs is not { } playing)
                return null;

            return Interlocked.Read(ref newestFedPtsMs) - playing;
        }
    }

    public event Action? ReadyToPlay;

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

    public AudioPlaybackDecoder(int sampleRate, int channels, int latencyBudgetMs = H264Decoder.PresentationBufferMilliseconds)
    {
        decoder = new OpusFrameDecoder(sampleRate, channels);

        (int deviceMs, int bufferMs) = SplitLatencyBudget(latencyBudgetMs);
        maxBufferedMilliseconds = bufferMs;

        WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        playbackBuffer = new SyncedPlaybackBuffer(waveFormat, bufferMs);

        output = new WaveOutEvent
        {
            DesiredLatency = deviceMs,
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

    public void SetSpeakerGains(SpeakerGains gains) => speakerMix.SetGains(gains);

    public void SetVolume(float volume) => speakerMix.SetVolume(volume);

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

    public void Feed(long ptsMs, byte[] opusPacket)
    {
        try
        {
            // Monotonic: a late-arriving out-of-order packet must not drag the "newest received"
            // mark backwards and make the measurement briefly report negative latency.
            long previousNewest = Interlocked.Read(ref newestFedPtsMs);
            if (ptsMs > previousNewest)
                Interlocked.Exchange(ref newestFedPtsMs, ptsMs);

            float[] samples = decoder.Decode(opusPacket);
            byte[] bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            // Nothing drains the buffer until StartPlayback, and that waits for video - which can
            // take seconds. No trimming is needed for that case: Add's own cap already leaves the
            // newest audio in hand rather than the oldest, so playback starts at the live edge
            // however long the wait turns out to be.
            playbackBuffer.Add(ptsMs, bytes);

            if (!isReady && playbackBuffer.BufferedMilliseconds >= maxBufferedMilliseconds)
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
