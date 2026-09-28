using NAudio.Wave;
using ScreenShare.Plugin.Encoding.H264;
using ScreenShare.Plugin.Encoding.Opus;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Audio playback decoder that decodes Opus audio packets and plays them back with low latency.
/// </summary>
public sealed class AudioPlaybackDecoder : IDisposable
{
    private const int DefaultOutputDeviceLatencyMilliseconds = 100;

    private const int OutputDeviceBuffers = 3;

    /// <summary>
    /// Splits the total latency budget into device latency and buffer latency, ensuring that both are within reasonable bounds.
    /// </summary>
    /// <param name="totalMs">The total latency budget in milliseconds.</param>
    /// <returns>A tuple containing the device latency and buffer latency.</returns>
    private static (int DeviceMs, int BufferMs) SplitLatencyBudget(int totalMs)
    {
        int total = Math.Clamp(totalMs, MinLatencyBudgetMs, MaxLatencyBudgetMs);

        int device = Math.Clamp(total / 2, 40, DefaultOutputDeviceLatencyMilliseconds);
        return (device, Math.Max(total - device, 40));
    }

    // The minimum and maximum latency budget in milliseconds for audio playback.
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

    /// <summary>
    /// Behind live in milliseconds, calculated as the difference between the newest fed PTS and the estimated current PTS.
    /// </summary>
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

    /// <summary>
    /// Estimated current presentation timestamp in milliseconds, based on the playback buffer and the output device's current position.
    /// </summary>
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

    /// <summary>
    /// Constructs a new AudioPlaybackDecoder with the specified sample rate, number of channels, and latency budget in milliseconds.
    /// </summary>
    /// <param name="sampleRate">The sample rate of the audio.</param>
    /// <param name="channels">The number of audio channels.</param>
    /// <param name="latencyBudgetMs">The latency budget in milliseconds.</param>
    public AudioPlaybackDecoder(int sampleRate, int channels, int latencyBudgetMs = H264Decoder.PresentationBufferMilliseconds)
    {
        decoder = new OpusFrameDecoder(sampleRate, channels);

        // Split the latency budget into device latency and buffer latency.
        (int deviceMs, int bufferMs) = SplitLatencyBudget(latencyBudgetMs);
        maxBufferedMilliseconds = bufferMs;

        // Create a WaveFormat for the audio playback, using IEEE float format.
        WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        playbackBuffer = new SyncedPlaybackBuffer(waveFormat, bufferMs);

        // Initialize the WaveOutEvent for audio playback with the specified device latency and number of buffers.
        output = new WaveOutEvent
        {
            DesiredLatency = deviceMs,
            NumberOfBuffers = OutputDeviceBuffers,
        };
        speakerMix = new SpeakerMixWaveProvider(playbackBuffer);

        // Initialize the WaveOutEvent with the SpeakerMixWaveProvider as the source.
        output.Init(speakerMix);
    }

    /// <summary>
    /// Sets the speaker gains for the audio playback.
    /// </summary>
    /// <param name="gains">The speaker gains to set.</param>
    public void SetSpeakerGains(SpeakerGains gains) => speakerMix.SetGains(gains);

    /// <summary>
    /// Sets the volume for the audio playback.
    /// </summary>
    /// <param name="volume">The volume to set.</param>
    public void SetVolume(float volume) => speakerMix.SetVolume(volume);

    /// <summary>
    /// Starts the playback of the decoded audio.
    /// </summary>
    public void StartPlayback()
    {
        // Ensure that playback is only started once, using a lock to prevent race conditions.
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
            // The output device has been disposed.
        }
    }

    /// <summary>
    /// Feeds an Opus audio packet into the decoder and adds the decoded samples to the playback buffer.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp of the audio packet in milliseconds.</param>
    /// <param name="opusPacket">The Opus audio packet to feed.</param>
    public void Feed(long ptsMs, byte[] opusPacket)
    {
        try
        {
            // Update the newest fed PTS if the current PTS is greater than the previous newest.
            long previousNewest = Interlocked.Read(ref newestFedPtsMs);
            if (ptsMs > previousNewest)
                Interlocked.Exchange(ref newestFedPtsMs, ptsMs);

            // Decode the Opus packet into PCM float samples.
            float[] samples = decoder.Decode(opusPacket);
            byte[] bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            playbackBuffer.Add(ptsMs, bytes);

            // Check if the playback buffer has reached the maximum buffered milliseconds and invoke the ReadyToPlay event if it has.
            if (!isReady && playbackBuffer.BufferedMilliseconds >= maxBufferedMilliseconds)
            {
                isReady = true;
                ReadyToPlay?.Invoke();
            }

            // Update the decoded packets count and peak sample value for monitoring purposes.
            Interlocked.Increment(ref decodedPackets);

            float peak = 0f;
            foreach (float sample in samples)
            {
                float abs = Math.Abs(sample);
                if (abs > peak)
                    peak = abs;
            }

            // Update the decoded peak value using Volatile.Write to ensure thread safety.
            Volatile.Write(ref decodedPeak, peak);
        }
        catch (Exception ex)
        {
            LastError = ex;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare audio playback decode error");
        }
    }

    /// <summary>
    /// Disposes of the AudioPlaybackDecoder.
    /// </summary>
    public void Dispose()
    {
        output.Stop();
        output.Dispose();
    }
}
