using System.Runtime.InteropServices;
using NAudio.Wave;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Speakers mix wave provider that applies per-channel gains and volume control to an audio stream. 
/// </summary>
public sealed class SpeakerMixWaveProvider : IWaveProvider
{
    private const float RampSeconds = 0.01f;

    private readonly IWaveProvider source;
    private readonly int channels;
    private readonly float rampAlpha;

    private volatile float targetLeftToLeft = 1f;
    private volatile float targetLeftToRight;
    private volatile float targetRightToLeft;
    private volatile float targetRightToRight = 1f;
    private volatile float targetMono = 1f;

    private volatile float targetVolume = 1f;

    private float leftToLeft = 1f;
    private float leftToRight;
    private float rightToLeft;
    private float rightToRight = 1f;
    private float mono = 1f;
    private float volume = 1f;

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>
    /// Constructs a new SpeakerMixWaveProvider that wraps the specified source wave provider.
    /// </summary>
    /// <param name="source">The source wave provider to wrap.</param>
    public SpeakerMixWaveProvider(IWaveProvider source)
    {
        this.source = source;
        channels = source.WaveFormat.Channels;

        int sampleRate = source.WaveFormat.SampleRate;

        rampAlpha = sampleRate > 0
            ? 1f - MathF.Exp(-1f / (RampSeconds * sampleRate))
            : 1f;
    }

    /// <summary>
    /// Sets volume for the output audio. The value is clamped between 0 and Configuration.MaxViewerVolume.
    /// </summary>
    /// <param name="value">The volume level to set.</param>
    public void SetVolume(float value) => targetVolume = Math.Clamp(value, 0f, Configuration.MaxViewerVolume);

    /// <summary>
    /// Sets the gains for each speaker channel.
    /// </summary>
    /// <param name="gains">The gains to set.</param>
    public void SetGains(SpeakerGains gains)
    {
        targetLeftToLeft = gains.LeftToLeft;
        targetLeftToRight = gains.LeftToRight;
        targetRightToLeft = gains.RightToLeft;
        targetRightToRight = gains.RightToRight;
        targetMono = gains.MonoGain;
    }

    /// <summary>
    /// Reads audio samples from the source wave provider, applies the configured gains and volume, and writes the processed samples to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write the processed samples to.</param>
    /// <param name="offset">The offset in the buffer to start writing.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <returns>The number of bytes read.</returns>
    public int Read(byte[] buffer, int offset, int count)
    {
        // Read raw audio samples from the source wave provider.
        int read = source.Read(buffer, offset, count);
        if (read <= 0)
            return read;

        Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, read));

        // Apply the appropriate mixing method based on the number of channels.
        if (channels == 2)
            MixStereo(samples);
        else
            ApplyScalarGain(samples);

        return read;
    }

    /// <summary>
    /// Mixes stereo audio samples by applying the configured gains and volume to each channel.
    /// </summary>
    /// <param name="samples"></param>
    private void MixStereo(Span<float> samples)
    {
        int frames = samples.Length / 2;

        // Smoothly ramp the gains and volume towards their target values over time.
        for (int frame = 0; frame < frames; frame++)
        {
            float left = samples[frame * 2];
            float right = samples[(frame * 2) + 1];

            leftToLeft += (targetLeftToLeft - leftToLeft) * rampAlpha;
            leftToRight += (targetLeftToRight - leftToRight) * rampAlpha;
            rightToLeft += (targetRightToLeft - rightToLeft) * rampAlpha;
            rightToRight += (targetRightToRight - rightToRight) * rampAlpha;
            volume += (targetVolume - volume) * rampAlpha;

            samples[frame * 2] = Limit(((left * leftToLeft) + (right * rightToLeft)) * volume);
            samples[(frame * 2) + 1] = Limit(((left * leftToRight) + (right * rightToRight)) * volume);
        }
    }

    /// <summary>
    /// Limits the magnitude of the audio sample to prevent clipping, applying a soft knee compression if the volume exceeds 1.0.
    /// </summary>
    /// <param name="sample">The audio sample to limit.</param>
    /// <returns>The limited audio sample.</returns>
    private float Limit(float sample)
    {
        if (volume <= 1f)
            return sample;

        // Apply soft knee compression to limit the sample magnitude.
        const float knee = 0.8f;
        float magnitude = MathF.Abs(sample);
        if (magnitude <= knee)
            return sample;

        // Apply a soft knee compression curve for samples exceeding the knee threshold.
        float limited = knee + ((1f - knee) * MathF.Tanh((magnitude - knee) / (1f - knee)));
        return MathF.CopySign(limited, sample);
    }

    /// <summary>
    /// Applies the configured mono gain and volume to the audio samples.
    /// </summary>
    /// <param name="samples">The audio samples to process.</param>
    private void ApplyScalarGain(Span<float> samples)
    {
        int perFrame = Math.Max(channels, 1);
        int frames = samples.Length / perFrame;

        // Smoothly ramp the mono gain and volume towards their target values over time.
        for (int frame = 0; frame < frames; frame++)
        {
            mono += (targetMono - mono) * rampAlpha;
            volume += (targetVolume - volume) * rampAlpha;

            int start = frame * perFrame;
            for (int channel = 0; channel < perFrame; channel++)
                samples[start + channel] = Limit(samples[start + channel] * mono * volume);
        }
    }
}
