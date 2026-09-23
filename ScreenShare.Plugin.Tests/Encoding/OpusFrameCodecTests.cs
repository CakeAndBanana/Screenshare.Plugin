using ScreenShare.Plugin.Encoding;

namespace ScreenShare.Plugin.Tests.Encoding;

public class OpusFrameCodecTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    [Fact]
    public void Feed_ExactlyOneFrame_RaisesPacketEncodedExactlyOnce()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels);
        int packetCount = 0;
        encoder.PacketEncoded += (_, _) => packetCount++;
        float[] oneFrame = new float[encoder.FrameSizePerChannel * Channels];

        encoder.Feed(0, oneFrame);

        Assert.Equal(1, packetCount);
    }

    [Fact]
    public void Feed_LessThanOneFrame_DoesNotRaisePacketEncoded()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels);
        int packetCount = 0;
        encoder.PacketEncoded += (_, _) => packetCount++;
        float[] partialFrame = new float[(encoder.FrameSizePerChannel * Channels) - 1];

        encoder.Feed(0, partialFrame);

        Assert.Equal(0, packetCount);
    }

    [Fact]
    public void Feed_TwoAndAHalfFrames_RaisesPacketEncodedTwiceAndBuffersRemainder()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels);
        int packetCount = 0;
        encoder.PacketEncoded += (_, _) => packetCount++;
        int frameLength = encoder.FrameSizePerChannel * Channels;
        float[] twoAndHalfFrames = new float[(frameLength * 2) + (frameLength / 2)];

        encoder.Feed(0, twoAndHalfFrames);

        Assert.Equal(2, packetCount);
    }

    [Fact]
    public void Feed_AcrossMultipleCallsSpanningAFrameBoundary_StillRaisesPacketEncoded()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels);
        int packetCount = 0;
        encoder.PacketEncoded += (_, _) => packetCount++;
        int frameLength = encoder.FrameSizePerChannel * Channels;

        encoder.Feed(0, new float[frameLength - 10]);
        Assert.Equal(0, packetCount);
        encoder.Feed(0, new float[20]);

        Assert.Equal(1, packetCount);
    }

    [Fact]
    public void Bitrate_SetThenGet_ReflectsTheNewValue()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels, bitrate: 32000);

        encoder.Bitrate = 64000;

        Assert.Equal(64000, encoder.Bitrate);
    }

    [Fact]
    public void EncodeDecodeRoundTrip_Silence_DecodesToNearZeroPeak()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels);
        var decoder = new OpusFrameDecoder(SampleRate, Channels);
        byte[]? packet = null;
        encoder.PacketEncoded += (_, p) => packet = p;

        encoder.Feed(0, new float[encoder.FrameSizePerChannel * Channels]);

        Assert.NotNull(packet);
        float[] decoded = decoder.Decode(packet);
        Assert.Equal(encoder.FrameSizePerChannel * Channels, decoded.Length);
        Assert.All(decoded, sample => Assert.True(Math.Abs(sample) < 0.05f, $"expected near-silence, got {sample}"));
    }

    [Fact]
    public void EncodeDecodeRoundTrip_FullScaleSquareWave_DecodesToRoughlyFullScalePeak()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels);
        var decoder = new OpusFrameDecoder(SampleRate, Channels);
        byte[]? packet = null;
        encoder.PacketEncoded += (_, p) => packet = p;

        float[] squareWave = new float[encoder.FrameSizePerChannel * Channels];
        for (int i = 0; i < squareWave.Length; i++)
            squareWave[i] = (i / Channels) % 20 < 10 ? 0.9f : -0.9f;
        encoder.Feed(0, squareWave);

        Assert.NotNull(packet);
        float[] decoded = decoder.Decode(packet);
        float peak = decoded.Max(Math.Abs);
        Assert.True(peak > 0.5f, $"expected a strong signal to survive lossy round trip, peak was {peak}");
    }

    [Fact]
    public void EncodeDecodeRoundTrip_CompressesRawPcmSignificantly()
    {
        var encoder = new OpusFrameEncoder(SampleRate, Channels, bitrate: 32000);
        byte[]? packet = null;
        encoder.PacketEncoded += (_, p) => packet = p;

        float[] frame = new float[encoder.FrameSizePerChannel * Channels];
        for (int i = 0; i < frame.Length; i++)
            frame[i] = MathF.Sin(i * 0.1f);
        encoder.Feed(0, frame);

        Assert.NotNull(packet);
        int rawBytes = frame.Length * sizeof(float);
        Assert.True(packet.Length < rawBytes / 4, $"expected substantial compression, raw={rawBytes} encoded={packet.Length}");
    }
}
