using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Tests.Encoding;

public class VideoEncoderSelectorTests
{
    [Theory]
    [InlineData(GpuVendor.Nvidia, SupportedVideoCodec.H264, "h264_nvenc")]
    [InlineData(GpuVendor.Nvidia, SupportedVideoCodec.Hevc, "hevc_nvenc")]
    [InlineData(GpuVendor.Amd, SupportedVideoCodec.H264, "h264_amf")]
    [InlineData(GpuVendor.Amd, SupportedVideoCodec.Hevc, "hevc_amf")]
    [InlineData(GpuVendor.Intel, SupportedVideoCodec.H264, "h264_qsv")]
    [InlineData(GpuVendor.Intel, SupportedVideoCodec.Hevc, "hevc_qsv")]
    [InlineData(GpuVendor.Unknown, SupportedVideoCodec.H264, "libx264")]
    [InlineData(GpuVendor.Unknown, SupportedVideoCodec.Hevc, "libx265")]
    public void BuildEncoderArgs_PicksCorrectEncoderForVendorAndCodec(GpuVendor vendor, SupportedVideoCodec codec, string expectedEncoder)
    {
        string args = VideoEncoderSelector.BuildEncoderArgs(vendor, codec, 1500, 2000, 3000, 60);

        Assert.Contains($"-c:v {expectedEncoder}", args);
    }

    [Fact]
    public void BuildEncoderArgs_IncludesBitrateMaxrateBufsizeAndGop()
    {
        string args = VideoEncoderSelector.BuildEncoderArgs(GpuVendor.Nvidia, SupportedVideoCodec.H264, 1500, 2000, 3000, 60);

        Assert.Contains("-b:v 1500k", args);
        Assert.Contains("-maxrate 2000k", args);
        Assert.Contains("-bufsize 3000k", args);
        Assert.Contains("-g 60", args);
    }

    [Fact]
    public void BuildEncoderArgs_NvencH264UsesLowLatencyTuneAndConstantBitrate()
    {
        string args = VideoEncoderSelector.BuildEncoderArgs(GpuVendor.Nvidia, SupportedVideoCodec.H264, 1500, 2000, 3000, 60);

        Assert.Contains("-preset p1", args);
        Assert.Contains("-tune ll", args);
        Assert.Contains("-rc cbr", args);
    }

    [Fact]
    public void BuildEncoderArgs_UnrecognizedVendor_FallsBackToSoftwareEncoderNotHardware()
    {
        string args = VideoEncoderSelector.BuildEncoderArgs((GpuVendor)999, SupportedVideoCodec.H264, 1500, 2000, 3000, 60);

        Assert.Contains("-c:v libx264", args);
    }

    [Theory]
    [InlineData(GpuVendor.Nvidia, SupportedVideoCodec.H264, "h264_nvenc")]
    [InlineData(GpuVendor.Amd, SupportedVideoCodec.Hevc, "hevc_amf")]
    [InlineData(GpuVendor.Intel, SupportedVideoCodec.H264, "h264_qsv")]
    [InlineData(GpuVendor.Unknown, SupportedVideoCodec.Hevc, "libx265")]
    [InlineData((GpuVendor)999, SupportedVideoCodec.H264, "libx264")]
    public void EncoderName_MatchesTheEncoderTheCommandLineSelects(GpuVendor vendor, SupportedVideoCodec codec, string expected)
    {
        Assert.Equal(expected, VideoEncoderSelector.EncoderName(vendor, codec));
        Assert.Contains($"-c:v {expected} ", VideoEncoderSelector.BuildEncoderArgs(vendor, codec, 1500, 2000, 3000, 60));
    }

    [Fact]
    public void EncoderOptions_NvencUsesTheVerifiedLowLatencyCbrSettings()
    {
        Assert.Equal(
            [("preset", "p1"), ("tune", "ll"), ("rc", "cbr")],
            VideoEncoderSelector.EncoderOptions(GpuVendor.Nvidia));
    }

    [Theory]
    [InlineData("h264_nvenc", true)]
    [InlineData("hevc_nvenc", true)]
    [InlineData("libx264", true)]
    [InlineData("h264_amf", false)]
    [InlineData("hevc_qsv", false)]
    [InlineData("libx265", false)]
    public void SupportsLiveBitrate_OnlyForEncodersVerifiedToRetune(string encoderName, bool expected)
    {
        Assert.Equal(expected, VideoEncoderSelector.SupportsLiveBitrate(encoderName));
    }

    [Theory]
    [InlineData(GpuVendor.Nvidia, "")]
    [InlineData(GpuVendor.Amd, "")]
    [InlineData(GpuVendor.Intel, "")]
    [InlineData(GpuVendor.Unknown, "-pix_fmt yuv420p")]
    [InlineData((GpuVendor)999, "-pix_fmt yuv420p")]
    public void BuildOutputPixelFormatArgs_PassesNv12ThroughOnlyForHardwareEncoders(GpuVendor vendor, string expected)
    {
        Assert.Equal(expected, VideoEncoderSelector.BuildOutputPixelFormatArgs(vendor));
    }

    [Theory]
    [InlineData(SupportedVideoCodec.H264, "h264")]
    [InlineData(SupportedVideoCodec.Hevc, "hevc")]
    public void RawFormatName_ReturnsFfmpegFormatNameForCodec(SupportedVideoCodec codec, string expected)
    {
        Assert.Equal(expected, VideoEncoderSelector.RawFormatName(codec));
    }

    [Fact]
    public void RawFormatName_InvalidCodecValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoEncoderSelector.RawFormatName((SupportedVideoCodec)999));
    }
}
