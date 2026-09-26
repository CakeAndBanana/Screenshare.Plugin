using NAudio.Wave;
using ScreenShare.Plugin.Encoding.H264;
using ScreenShare.Plugin.Encoding.Opus;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

public sealed class AudioPlaybackDecoder : IDisposable
{
    private const int DefaultOutputDeviceLatencyMilliseconds = 100;

    private const int OutputDeviceBuffers = 3;

    private static (int DeviceMs, int BufferMs) SplitLatencyBudget(int totalMs)
    {
        int total = Math.Clamp(totalMs, MinLatencyBudgetMs, MaxLatencyBudgetMs);

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
        speakerMix = new SpeakerMixWaveProvider(playbackBuffer);

        output.Init(speakerMix);
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
            long previousNewest = Interlocked.Read(ref newestFedPtsMs);
            if (ptsMs > previousNewest)
                Interlocked.Exchange(ref newestFedPtsMs, ptsMs);

            float[] samples = decoder.Decode(opusPacket);
            byte[] bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
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
