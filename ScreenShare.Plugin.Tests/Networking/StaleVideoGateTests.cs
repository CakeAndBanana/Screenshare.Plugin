using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Tests.Networking;

public class StaleVideoGateTests
{
    private const long MaxAgeMs = 500;

    // Framed video message: type byte, 8-byte PTS, then an Annex-B start code and NAL header.
    private static byte[] Frame(byte nalHeader) => [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, nalHeader, 0xAA];

    private static readonly byte[] H264Delta = Frame(0x41);
    private static readonly byte[] H264Idr = Frame(0x65);
    private static readonly byte[] H264Sps = Frame(0x67);

    [Fact]
    public void Admit_FreshMessages_AreAllSent()
    {
        var gate = new StaleVideoGate(MaxAgeMs);

        Assert.True(gate.Admit(0, H264Idr));
        Assert.True(gate.Admit(100, H264Delta));
        Assert.True(gate.Admit(MaxAgeMs, H264Delta));
    }

    [Fact]
    public void Admit_StaleMessage_DiscardsUntilAFreshKeyframe()
    {
        var gate = new StaleVideoGate(MaxAgeMs);

        Assert.False(gate.Admit(MaxAgeMs + 1, H264Delta));
        Assert.False(gate.Admit(10, H264Delta));
        Assert.True(gate.Admit(10, H264Idr));
        Assert.True(gate.Admit(10, H264Delta));
    }

    [Fact]
    public void Admit_KeyframeThatIsItselfStale_IsDiscardedToo()
    {
        var gate = new StaleVideoGate(MaxAgeMs);

        Assert.False(gate.Admit(MaxAgeMs + 1, H264Delta));
        Assert.False(gate.Admit(MaxAgeMs + 1, H264Idr));
        Assert.False(gate.Admit(10, H264Delta));
        Assert.True(gate.Admit(10, H264Idr));
    }

    [Fact]
    public void Admit_ParameterSetChunk_CountsAsAResyncPoint()
    {
        var gate = new StaleVideoGate(MaxAgeMs);

        Assert.False(gate.Admit(MaxAgeMs + 1, H264Delta));
        Assert.True(gate.Admit(10, H264Sps));
    }

    [Theory]
    [InlineData((byte)(19 << 1), true)]  // IDR_W_RADL
    [InlineData((byte)(21 << 1), true)]  // CRA
    [InlineData((byte)(32 << 1), true)]  // VPS
    [InlineData((byte)(1 << 1), false)]  // TRAIL_R
    public void IsResyncPoint_Hevc_RecognisesIrapAndParameterSets(byte nalHeader, bool expected)
    {
        Assert.Equal(expected, StaleVideoGate.IsResyncPoint(Frame(nalHeader), SupportedVideoCodec.Hevc));
    }

    [Fact]
    public void Admit_Hevc_ResumesOnIrap()
    {
        var gate = new StaleVideoGate(MaxAgeMs) { Codec = SupportedVideoCodec.Hevc };

        Assert.False(gate.Admit(MaxAgeMs + 1, Frame(1 << 1)));
        Assert.False(gate.Admit(10, H264Idr)); // 0x65 isn't an IRAP under HEVC's header layout
        Assert.True(gate.Admit(10, Frame(19 << 1)));
    }
}
