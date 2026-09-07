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

    // Written from the render thread, read from the output device's thread. Deliberately plain
    // volatile floats rather than a swapped-in immutable object: these are only ever ramp TARGETS,
    // never the gains actually multiplied into samples, so the worst a torn read can do is aim one
    // buffer's ramp at a half-updated destination - inaudible, and corrected on the next frame.
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

        // Guarded against a zero sample rate so a malformed metadata packet can't turn into a NaN
        // that then propagates into every sample from here on.
        rampAlpha = sampleRate > 0
            ? 1f - MathF.Exp(-1f / (RampSeconds * sampleRate))
            : 1f;
    }

    public void SetVolume(float value) => targetVolume = Math.Clamp(value, 0f, 1f);

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

        // The buffer is 32-bit IEEE float throughout (see AudioPlaybackDecoder's WaveFormat), so
        // the bytes the device is about to receive can be rewritten as samples in place.
        Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, read));

        if (channels == 2)
            MixStereo(samples);
        else
            ApplyScalarGain(samples);

        return read;
    }

    private void MixStereo(Span<float> samples)
    {
        // A trailing odd sample would mean the source handed back a partial frame, which
        // SyncedPlaybackBuffer never does; dropping it from the loop is simply safe indexing.
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

            samples[frame * 2] = ((left * leftToLeft) + (right * rightToLeft)) * volume;
            samples[(frame * 2) + 1] = ((left * leftToRight) + (right * rightToRight)) * volume;
        }
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
                samples[start + channel] *= mono * volume;
        }
    }
}
