using System.Runtime.InteropServices;
using NAudio.Wave;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Applies the in-world screen's <see cref="SpeakerGains"/> to decoded audio on its way to the
/// sound card. Wraps <see cref="SyncedPlaybackBuffer"/> and is handed to WaveOutEvent in its place.
///
/// Format-preserving, and that is a hard requirement rather than a convenience. The master clock
/// in <see cref="AudioPlaybackDecoder.EstimatedCurrentPtsMs"/> maps the output device's own
/// reported byte position back onto content PTS via
/// <see cref="SyncedPlaybackBuffer.GetPtsAtPlayedBytes"/>, so device bytes and buffer bytes have to
/// mean the same thing. Changing the channel count here (upmixing mono to stereo, say) would keep
/// audio sounding fine while silently putting a constant factor into the clock that video is
/// synchronized against - a desync with no obvious cause. So this rewrites samples in place,
/// consumes exactly what it produces, buffers nothing, and reports the source's own byte count.
///
/// The consequence is that a MONO stream gets distance attenuation but no panning: there is only
/// one output channel, and inventing a second one is exactly the format change that isn't allowed.
///
/// Gains ramp toward their targets rather than snapping. Targets are recomputed once per rendered
/// frame, and stepping a gain discontinuously between frames puts a step edge in the waveform -
/// audible as a click, or as continuous zipper noise while walking.
/// </summary>
public sealed class SpeakerMixWaveProvider : IWaveProvider
{
    /// <summary>Time constant for the gain ramp. Long enough that per-frame gain changes smooth
    /// into inaudibility, short enough that the pan still tracks the camera rather than visibly
    /// lagging behind it.</summary>
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

    /// <summary>Master playback volume, applied on top of the speaker mix. Ramped through the same
    /// path as the gains so dragging the slider doesn't step the waveform.</summary>
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

    /// <summary>Sets the master playback volume, 0-1. Separate from the speaker mix because it
    /// survives the in-world screen being off, where the mix falls back to identity.</summary>
    public void SetVolume(float value) => targetVolume = Math.Clamp(value, 0f, 1f);

    /// <summary>Sets the mix the output ramps toward. Safe to call every frame from the render
    /// thread. Pass <see cref="SpeakerGains.Identity"/> to return to untouched playback - which is
    /// what happens whenever the in-world screen is off, unplaced, or in another zone, so turning
    /// the feature off never costs the user their audio.</summary>
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

    /// <summary>Mono (or any unexpected channel count): distance only, applied uniformly. Ramped
    /// per FRAME rather than per sample so the ramp runs at the same rate whatever the channel
    /// count is.</summary>
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
