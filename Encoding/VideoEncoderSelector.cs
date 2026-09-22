using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Encoding;

public static class VideoEncoderSelector
{
    public static string EncoderName(GpuVendor vendor, VideoCodec codec) => (vendor, codec) switch
    {
        (GpuVendor.Nvidia, VideoCodec.H264) => "h264_nvenc",
        (GpuVendor.Nvidia, VideoCodec.Hevc) => "hevc_nvenc",
        (GpuVendor.Amd, VideoCodec.H264) => "h264_amf",
        (GpuVendor.Amd, VideoCodec.Hevc) => "hevc_amf",
        (GpuVendor.Intel, VideoCodec.H264) => "h264_qsv",
        (GpuVendor.Intel, VideoCodec.Hevc) => "hevc_qsv",
        (_, VideoCodec.H264) => "libx264",
        (_, VideoCodec.Hevc) => "libx265",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    // Encoder-private options, shared by the in-process libavcodec encoder (av_opt_set) and the
    // ffmpeg.exe command line, so the two can't drift apart.
    public static IReadOnlyList<(string Name, string Value)> EncoderOptions(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => [("preset", "p1"), ("tune", "ll"), ("rc", "cbr")],
        GpuVendor.Amd => [("usage", "lowlatency"), ("quality", "speed"), ("rc", "cbr")],
        GpuVendor.Intel => [("preset", "veryfast")],
        _ => [("preset", "ultrafast"), ("tune", "zerolatency")],
    };

    // Whether libavcodec's wrapper for this encoder picks up a changed bit_rate between frames.
    // NVENC (when the driver reports dynamic bitrate support) and libx264 do; the AMF and QSV
    // wrappers are left out because they couldn't be verified on real hardware.
    public static bool SupportsLiveBitrate(string encoderName) =>
        encoderName is "h264_nvenc" or "hevc_nvenc" or "libx264";

    public static string BuildEncoderArgs(GpuVendor vendor, VideoCodec codec, int bitrateKbps, int maxrateKbps, int bufsizeKbps, int gopSize)
    {
        string options = string.Join(" ", EncoderOptions(vendor).Select(option => $"-{option.Name} {option.Value}"));

        return $"-c:v {EncoderName(vendor, codec)} {options} -b:v {bitrateKbps}k -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k -g {gopSize}";
    }

    // Frames arrive as NV12, which nvenc/amf/qsv consume natively. The software encoders get
    // an explicit planar yuv420p, which is only a cheap chroma de-interleave for swscale.
    public static string BuildOutputPixelFormatArgs(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel => "",
        _ => "-pix_fmt yuv420p",
    };

    public static string RawFormatName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.Hevc => "hevc",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}
