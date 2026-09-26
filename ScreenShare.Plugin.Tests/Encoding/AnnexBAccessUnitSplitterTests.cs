using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Tests.Encoding;

public class AnnexBAccessUnitSplitterTests
{
    private const byte H264Sps = 0x67;
    private const byte H264Pps = 0x68;
    private const byte H264Sei = 0x06;
    private const byte H264Aud = 0x09;
    private const byte H264IdrSlice = 0x65;
    private const byte H264Slice = 0x41;
    private const byte H264Filler = 0x0C;

    private const byte FirstMbZero = 0x88;
    private const byte FirstMbNonZero = 0x34;

    private static byte Hevc(int type) => (byte)(type << 1);

    private static readonly byte[] StartCode4 = [0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] StartCode3 = [0x00, 0x00, 0x01];

    private static byte[] Nal(byte[] startCode, params byte[] body) => [.. startCode, .. body];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static byte[] H264KeyAccessUnit() => Concat(
        Nal(StartCode4, H264Aud, 0x10),
        Nal(StartCode3, H264Sps, 0x42, 0xC0, 0x1E),
        Nal(StartCode3, H264Pps, 0xCE, 0x3C, 0x80),
        Nal(StartCode3, H264IdrSlice, FirstMbZero, 0x11, 0x22, 0x33));

    private static byte[] H264DeltaAccessUnit(byte payload) => Concat(
        Nal(StartCode4, H264Aud, 0x30),
        Nal(StartCode3, H264Slice, FirstMbZero, payload, payload, payload));

    private static List<byte[]> Split(SupportedVideoCodec codec, byte[] stream, int chunkSize)
    {
        var splitter = new AnnexBAccessUnitSplitter(codec);
        var units = new List<byte[]>();

        for (int offset = 0; offset < stream.Length; offset += chunkSize)
            splitter.Append(stream, offset, Math.Min(chunkSize, stream.Length - offset), units.Add);

        return units;
    }

    [Fact]
    public void Append_TwoAccessUnits_EmitsTheFirstAndHoldsTheSecond()
    {
        byte[] first = H264KeyAccessUnit();
        byte[] second = H264DeltaAccessUnit(0xAA);

        List<byte[]> units = Split(SupportedVideoCodec.H264, Concat(first, second), int.MaxValue);

        Assert.Equal([first], units);
    }

    [Fact]
    public void Flush_AfterTheLastAccessUnit_ReturnsIt()
    {
        byte[] first = H264KeyAccessUnit();
        byte[] second = H264DeltaAccessUnit(0xAA);
        byte[] stream = Concat(first, second);
        var splitter = new AnnexBAccessUnitSplitter(SupportedVideoCodec.H264);
        var units = new List<byte[]>();

        splitter.Append(stream, 0, stream.Length, units.Add);
        byte[]? tail = splitter.Flush();

        Assert.Equal([first], units);
        Assert.Equal(second, tail);
    }

    [Fact]
    public void Flush_WithNothingBuffered_ReturnsNull()
    {
        Assert.Null(new AnnexBAccessUnitSplitter(SupportedVideoCodec.H264).Flush());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(1024)]
    public void Append_AtAnyChunkSize_ProducesIdenticalAccessUnits(int chunkSize)
    {
        byte[][] expected =
        [
            H264KeyAccessUnit(),
            H264DeltaAccessUnit(0x11),
            H264DeltaAccessUnit(0x22),
            H264DeltaAccessUnit(0x33),
        ];

        List<byte[]> units = Split(SupportedVideoCodec.H264, Concat(expected), chunkSize);

        Assert.Equal(expected[..^1], units);
    }

    [Fact]
    public void Append_OverAWholeStream_LosesAndDuplicatesNothing()
    {
        byte[] stream = Concat(
            H264KeyAccessUnit(),
            H264DeltaAccessUnit(0x11),
            H264DeltaAccessUnit(0x22));
        var splitter = new AnnexBAccessUnitSplitter(SupportedVideoCodec.H264);
        var units = new List<byte[]>();

        for (int i = 0; i < stream.Length; i++)
            splitter.Append(stream, i, 1, units.Add);

        if (splitter.Flush() is { } tail)
            units.Add(tail);

        Assert.Equal(stream, Concat([.. units]));
    }

    [Fact]
    public void Append_AdditionalSlicesOfTheSamePicture_StayInOneAccessUnit()
    {
        byte[] multiSlice = Concat(
            Nal(StartCode4, H264Aud, 0x10),
            Nal(StartCode3, H264IdrSlice, FirstMbZero, 0x01),
            Nal(StartCode3, H264Slice, FirstMbNonZero, 0x02),
            Nal(StartCode3, H264Slice, FirstMbNonZero, 0x03));

        List<byte[]> units = Split(SupportedVideoCodec.H264, Concat(multiSlice, H264DeltaAccessUnit(0x44)), 5);

        Assert.Equal([multiSlice], units);
    }

    [Fact]
    public void Append_TrailingFiller_StaysWithThePictureItFollows()
    {
        byte[] withFiller = Concat(
            Nal(StartCode4, H264Sei, 0x05, 0x80),
            Nal(StartCode3, H264IdrSlice, FirstMbZero, 0x01),
            Nal(StartCode3, H264Filler, 0xFF, 0xFF, 0x80));

        List<byte[]> units = Split(SupportedVideoCodec.H264, Concat(withFiller, H264DeltaAccessUnit(0x55)), 3);

        Assert.Equal([withFiller], units);
    }

    [Fact]
    public void Append_ParameterSetsBeforeAKeyframe_LeadTheirOwnAccessUnit()
    {
        byte[] delta = H264DeltaAccessUnit(0x11);
        byte[] key = H264KeyAccessUnit();

        List<byte[]> units = Split(SupportedVideoCodec.H264, Concat(delta, key, H264DeltaAccessUnit(0x22)), 6);

        Assert.Equal([delta, key], units);
    }

    [Fact]
    public void Append_Hevc_SplitsOnPictureBoundariesAndKeepsSuffixSeiWithItsPicture()
    {
        byte[] first = Concat(
            Nal(StartCode4, Hevc(35), 0x01, 0x50),
            Nal(StartCode3, Hevc(32), 0x01, 0x0C, 0x01),
            Nal(StartCode3, Hevc(33), 0x01, 0x42, 0x01),
            Nal(StartCode3, Hevc(34), 0x01, 0xC1, 0x73),
            Nal(StartCode3, Hevc(19), 0x01, FirstMbZero, 0xAA),
            Nal(StartCode3, Hevc(40), 0x01, 0x84, 0x80));
        byte[] second = Concat(
            Nal(StartCode4, Hevc(39), 0x01, 0x01, 0x80),
            Nal(StartCode3, Hevc(1), 0x01, FirstMbZero, 0xBB));

        List<byte[]> units = Split(SupportedVideoCodec.Hevc, Concat(first, second, first), 9);

        Assert.Equal([first, second], units);
    }

    [Fact]
    public void Pending_ReportsWhatIsStillHeldMidUnit()
    {
        byte[] key = H264KeyAccessUnit();
        byte[] delta = H264DeltaAccessUnit(0x11);
        byte[] stream = Concat(key, delta);
        var splitter = new AnnexBAccessUnitSplitter(SupportedVideoCodec.H264);

        splitter.Append(stream, 0, stream.Length, _ => { });

        Assert.Equal(delta.Length, splitter.Pending);
    }
}
