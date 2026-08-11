using Concentus.Structs;

namespace ScreenShare.Plugin.Encoding;

/// <summary>Decodes Opus packets produced by <see cref="OpusFrameEncoder"/> back to interleaved float PCM.</summary>
public sealed class OpusFrameDecoder
{
    private readonly OpusDecoder decoder;

    public int FrameSizePerChannel { get; }

    public int Channels { get; }

    public OpusFrameDecoder(int sampleRate, int channels, int frameMilliseconds = 20)
    {
        Channels = channels;
        FrameSizePerChannel = sampleRate * frameMilliseconds / 1000;
        // See OpusFrameEncoder for why we stick with the pure-managed (obsolete) constructor.
        decoder = new OpusDecoder(sampleRate, channels);
    }

    public float[] Decode(ReadOnlySpan<byte> packet)
    {
        float[] output = new float[FrameSizePerChannel * Channels];
        decoder.Decode(packet, output, FrameSizePerChannel, false);
        return output;
    }
}
