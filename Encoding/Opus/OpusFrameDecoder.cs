using Concentus;

namespace ScreenShare.Plugin.Encoding.Opus;

/// <summary>
/// Opus frame decoder that decodes Opus packets into PCM float samples
/// </summary>
public sealed class OpusFrameDecoder
{
    private readonly IOpusDecoder decoder;

    public int FrameSizePerChannel { get; }

    public int Channels { get; }

    /// <summary>
    /// Constructs a new OpusFrameDecoder with the specified sample rate, number of channels, and frame size in milliseconds.
    /// </summary>
    /// <param name="sampleRate">The sample rate of the audio.</param>
    /// <param name="channels">The number of channels.</param>
    /// <param name="frameMilliseconds">The frame size in milliseconds.</param>
    public OpusFrameDecoder(int sampleRate, int channels, int frameMilliseconds = 20)
    {
        Channels = channels;
        FrameSizePerChannel = sampleRate * frameMilliseconds / 1000;

        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
    }

    /// <summary>
    /// Decodes an Opus packet into PCM float samples.
    /// </summary>
    /// <param name="packet">The Opus packet to decode.</param>
    /// <returns>The decoded PCM float samples.</returns>
    public float[] Decode(ReadOnlySpan<byte> packet)
    {
        float[] output = new float[FrameSizePerChannel * Channels];
        decoder.Decode(packet, output, FrameSizePerChannel, false);
        return output;
    }
}
