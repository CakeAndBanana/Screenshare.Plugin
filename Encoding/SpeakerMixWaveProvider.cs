using System.Runtime.InteropServices;
using NAudio.Wave;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

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

    public SpeakerMixWaveProvider(IWaveProvider source)
    {
        this.source = source;
        channels = source.WaveFormat.Channels;

        int sampleRate = source.WaveFormat.SampleRate;

        rampAlpha = sampleRate > 0
            ? 1f - MathF.Exp(-1f / (RampSeconds * sampleRate))
            : 1f;
    }

    public void SetVolume(float value) => targetVolume = Math.Clamp(value, 0f, Configuration.MaxViewerVolume);

    public void SetGains(SpeakerGains gains)
    {
        targetLeftToLeft = gains.LeftToLeft;
        targetLeftToRight = gains.LeftToRight;
        targetRightToLeft = gains.RightToLeft;
        targetRightToRight = gains.RightToRight;
        targetMono = gains.MonoGain;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        if (read <= 0)
            return read;

        Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, read));

        if (channels == 2)
            MixStereo(samples);
        else
            ApplyScalarGain(samples);

        return read;
    }

    private void MixStereo(Span<float> samples)
    {
        int frames = samples.Length / 2;

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

    // Boosting past 100% can push peaks beyond full scale; bend them smoothly toward
    // 1.0 instead of letting the output device hard-clip. Untouched at or below 100%.
    private float Limit(float sample)
    {
        if (volume <= 1f)
            return sample;

        const float knee = 0.8f;
        float magnitude = MathF.Abs(sample);
        if (magnitude <= knee)
            return sample;

        float limited = knee + ((1f - knee) * MathF.Tanh((magnitude - knee) / (1f - knee)));
        return MathF.CopySign(limited, sample);
    }

    private void ApplyScalarGain(Span<float> samples)
    {
        int perFrame = Math.Max(channels, 1);
        int frames = samples.Length / perFrame;

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
