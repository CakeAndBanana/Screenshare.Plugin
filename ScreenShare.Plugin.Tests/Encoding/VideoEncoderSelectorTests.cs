using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Tests.Encoding;

public class VideoEncoderSelectorTests
{
    [Theory]
    [InlineData(GpuVendor.Nvidia, VideoCodec.H264, "h264_nvenc")]
    [InlineData(GpuVendor.Nvidia, VideoCodec.Hevc, "hevc_nvenc")]
    [InlineData(GpuVendor.Amd, VideoCodec.H264, "h264_amf")]
    [InlineData(GpuVendor.Amd, VideoCodec.Hevc, "hevc_amf")]
    [InlineData(GpuVendor.Intel, VideoCodec.H264, "h264_qsv")]
    [InlineData(GpuVendor.Intel, VideoCodec.Hevc, "hevc_qsv")]
    [InlineData(GpuVendor.Unknown, VideoCodec.H264, "libx264")]
    [InlineData(GpuVendor.Unknown, VideoCodec.Hevc, "libx265")]
    public void BuildEncoderArgs_PicksCorrectEncoderForVendorAndCodec(GpuVendor vendor, VideoCodec codec, string expectedEncoder)
    {
        string args = VideoEncoderSelector.BuildEncoderArgs(vendor, codec, 1500, 2000, 3000, 60);

        Assert.Contains($"-c:v {expectedEncoder}", args);
    }

    [Fact]
    public void BuildEncoderArgs_IncludesBitrateMaxrateBufsizeAndGop()
    {
        string args = VideoEncoderSelector.BuildEncoderArgs(GpuVendor.Nvidia, VideoCodec.H264, 1500, 2000, 3000, 60);

        Assert.Contains("-b:v 1500k", args);
        Assert.Contains("-maxrate 2000k", args);
        Assert.Contains("-bufsize 3000k", args);
        Assert.Contains("-g 60", args);
    }

    [Fact]
    public void BuildEncoderArgs_NvencH264UsesLowLatencyTuneAndConstantBitrate()
    {
        // These specific option names/values were only trusted after verifying them against the
        // actual bundled ffmpeg binary via `ffmpeg -h encoder=X` and a real NVENC test - this
        // test exists to catch an accidental edit drifting away from what was verified, not to
        // re-verify ffmpeg itself.
        string args = VideoEncoderSelector.BuildEncoderArgs(GpuVendor.Nvidia, VideoCodec.H264, 1500, 2000, 3000, 60);

        Assert.Contains("-preset p1", args);
        Assert.Contains("-tune ll", args);
        Assert.Contains("-rc cbr", args);
    }

    [Fact]
    public void BuildEncoderArgs_UnrecognizedVendor_FallsBackToSoftwareEncoderNotHardware()
    {
        string args = VideoEncoderSelector.BuildEncoderArgs((GpuVendor)999, VideoCodec.H264, 1500, 2000, 3000, 60);

        Assert.Contains("-c:v libx264", args);
    }

    [Theory]
    [InlineData(VideoCodec.H264, "h264")]
    [InlineData(VideoCodec.Hevc, "hevc")]
    public void RawFormatName_ReturnsFfmpegFormatNameForCodec(VideoCodec codec, string expected)
    {
        Assert.Equal(expected, VideoEncoderSelector.RawFormatName(codec));
    }

    [Fact]
    public void RawFormatName_InvalidCodecValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoEncoderSelector.RawFormatName((VideoCodec)999));
    }
}
