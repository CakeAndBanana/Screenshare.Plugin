using ScreenShare.Plugin.Encoding;

namespace ScreenShare.Plugin.Tests.Encoding;

public class AnnexBAccessUnitSplitterTests
{
    // H.264 NAL headers: the low five bits are the type, the rest is nal_ref_idc.
    private const byte H264Sps = 0x67;          // type 7
    private const byte H264Pps = 0x68;          // type 8
    private const byte H264Sei = 0x06;          // type 6
    private const byte H264Aud = 0x09;          // type 9
    private const byte H264IdrSlice = 0x65;     // type 5
    private const byte H264Slice = 0x41;        // type 1
    private const byte H264Filler = 0x0C;       // type 12

    // first_mb_in_slice is the leading ue(v) of a slice header: a set top bit is the value 0,
    // which is what marks a slice as opening a picture rather than continuing one.
    private const byte FirstMbZero = 0x88;
    private const byte FirstMbNonZero = 0x34;

    // HEVC NAL headers are two bytes, with the type in bits 1-6 of the first.
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

    private static List<byte[]> Split(VideoCodec codec, byte[] stream, int chunkSize)
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

        List<byte[]> units = Split(VideoCodec.H264, Concat(first, second), int.MaxValue);

        // Only one: the second unit has no following start code to end it yet, which is the state
        // a live stream is always in.
        Assert.Equal([first], units);
    }

    [Fact]
    public void Flush_AfterTheLastAccessUnit_ReturnsIt()
    {
        byte[] first = H264KeyAccessUnit();
        byte[] second = H264DeltaAccessUnit(0xAA);
        byte[] stream = Concat(first, second);
        var splitter = new AnnexBAccessUnitSplitter(VideoCodec.H264);
        var units = new List<byte[]>();

        splitter.Append(stream, 0, stream.Length, units.Add);
        byte[]? tail = splitter.Flush();

        Assert.Equal([first], units);
        Assert.Equal(second, tail);
    }

    [Fact]
    public void Flush_WithNothingBuffered_ReturnsNull()
    {
        Assert.Null(new AnnexBAccessUnitSplitter(VideoCodec.H264).Flush());
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
        // The whole point of the class: ffmpeg hands over whatever bytes its pipe happened to
        // hold, so boundaries land mid-NAL and mid-start-code. Every chunking of the same stream
        // has to produce the same units, including the sizes that cut a start code in half.
        byte[][] expected =
        [
            H264KeyAccessUnit(),
            H264DeltaAccessUnit(0x11),
            H264DeltaAccessUnit(0x22),
            H264DeltaAccessUnit(0x33),
        ];

        List<byte[]> units = Split(VideoCodec.H264, Concat(expected), chunkSize);

        Assert.Equal(expected[..^1], units);
    }

    [Fact]
    public void Append_OverAWholeStream_LosesAndDuplicatesNothing()
    {
        byte[] stream = Concat(
            H264KeyAccessUnit(),
            H264DeltaAccessUnit(0x11),
            H264DeltaAccessUnit(0x22));
        var splitter = new AnnexBAccessUnitSplitter(VideoCodec.H264);
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
        // A picture split into several slices is still ONE access unit - only the slice with
        // first_mb_in_slice == 0 opens a new one. Cutting on every slice would hand a decoder
        // fragments of a picture and call each one a frame.
        byte[] multiSlice = Concat(
            Nal(StartCode4, H264Aud, 0x10),
            Nal(StartCode3, H264IdrSlice, FirstMbZero, 0x01),
            Nal(StartCode3, H264Slice, FirstMbNonZero, 0x02),
            Nal(StartCode3, H264Slice, FirstMbNonZero, 0x03));

        List<byte[]> units = Split(VideoCodec.H264, Concat(multiSlice, H264DeltaAccessUnit(0x44)), 5);

        Assert.Equal([multiSlice], units);
    }

    [Fact]
    public void Append_TrailingFiller_StaysWithThePictureItFollows()
    {
        // NVENC's CBR pads with filler NALs after the slice data. They belong to the unit already
        // built, not to the next one.
        byte[] withFiller = Concat(
            Nal(StartCode4, H264Sei, 0x05, 0x80),
            Nal(StartCode3, H264IdrSlice, FirstMbZero, 0x01),
            Nal(StartCode3, H264Filler, 0xFF, 0xFF, 0x80));

        List<byte[]> units = Split(VideoCodec.H264, Concat(withFiller, H264DeltaAccessUnit(0x55)), 3);

        Assert.Equal([withFiller], units);
    }

    [Fact]
    public void Append_ParameterSetsBeforeAKeyframe_LeadTheirOwnAccessUnit()
    {
        // SPS/PPS describe the picture that FOLLOWS them, so a mid-stream keyframe's parameter
        // sets have to travel in the same unit as its slice - a decoder handed the slice without
        // them cannot start on it.
        byte[] delta = H264DeltaAccessUnit(0x11);
        byte[] key = H264KeyAccessUnit();

        List<byte[]> units = Split(VideoCodec.H264, Concat(delta, key, H264DeltaAccessUnit(0x22)), 6);

        Assert.Equal([delta, key], units);
    }

    [Fact]
    public void Append_Hevc_SplitsOnPictureBoundariesAndKeepsSuffixSeiWithItsPicture()
    {
        // HEVC's suffix SEI (40) trails the picture it describes, unlike prefix SEI (39) which
        // opens the next one - so only the latter may cut.
        byte[] first = Concat(
            Nal(StartCode4, Hevc(35), 0x01, 0x50),                // AUD
            Nal(StartCode3, Hevc(32), 0x01, 0x0C, 0x01),          // VPS
            Nal(StartCode3, Hevc(33), 0x01, 0x42, 0x01),          // SPS
            Nal(StartCode3, Hevc(34), 0x01, 0xC1, 0x73),          // PPS
            Nal(StartCode3, Hevc(19), 0x01, FirstMbZero, 0xAA),   // IDR_W_RADL
            Nal(StartCode3, Hevc(40), 0x01, 0x84, 0x80));         // suffix SEI
        byte[] second = Concat(
            Nal(StartCode4, Hevc(39), 0x01, 0x01, 0x80),          // prefix SEI
            Nal(StartCode3, Hevc(1), 0x01, FirstMbZero, 0xBB));   // TRAIL_R

        List<byte[]> units = Split(VideoCodec.Hevc, Concat(first, second, first), 9);

        Assert.Equal([first, second], units);
    }

    [Fact]
    public void Pending_ReportsWhatIsStillHeldMidUnit()
    {
        byte[] key = H264KeyAccessUnit();
        byte[] delta = H264DeltaAccessUnit(0x11);
        byte[] stream = Concat(key, delta);
        var splitter = new AnnexBAccessUnitSplitter(VideoCodec.H264);

        splitter.Append(stream, 0, stream.Length, _ => { });

        Assert.Equal(delta.Length, splitter.Pending);
    }
}
