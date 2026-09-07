using Concentus;

namespace ScreenShare.Plugin.Encoding;

public sealed class OpusFrameDecoder
{
    private readonly IOpusDecoder decoder;

    public int FrameSizePerChannel { get; }

    public int Channels { get; }

    public OpusFrameDecoder(int sampleRate, int channels, int frameMilliseconds = 20)
    {
        Channels = channels;
        FrameSizePerChannel = sampleRate * frameMilliseconds / 1000;
        // See OpusFrameEncoder for why AttemptToUseNativeLibrary is forced off - same reasoning
        // applies here.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        decoder = OpusCodecFactory.CreateDecoder(sampleRate, channels);
    }

    public float[] Decode(ReadOnlySpan<byte> packet)
    {
        float[] output = new float[FrameSizePerChannel * Channels];
        decoder.Decode(packet, output, FrameSizePerChannel, false);
        return output;
    }
}
