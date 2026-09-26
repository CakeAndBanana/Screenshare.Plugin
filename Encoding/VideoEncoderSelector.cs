using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Encoding;

public static class VideoEncoderSelector
{
    public static string EncoderName(GpuVendor vendor, SupportedVideoCodec codec) => (vendor, codec) switch
    {
        (GpuVendor.Nvidia, SupportedVideoCodec.H264) => "h264_nvenc",
        (GpuVendor.Nvidia, SupportedVideoCodec.Hevc) => "hevc_nvenc",
        (GpuVendor.Amd, SupportedVideoCodec.H264) => "h264_amf",
        (GpuVendor.Amd, SupportedVideoCodec.Hevc) => "hevc_amf",
        (GpuVendor.Intel, SupportedVideoCodec.H264) => "h264_qsv",
        (GpuVendor.Intel, SupportedVideoCodec.Hevc) => "hevc_qsv",
        (_, SupportedVideoCodec.H264) => "libx264",
        (_, SupportedVideoCodec.Hevc) => "libx265",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    public static IReadOnlyList<(string Name, string Value)> EncoderOptions(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => [("preset", "p1"), ("tune", "ll"), ("rc", "cbr")],
        GpuVendor.Amd => [("usage", "lowlatency"), ("quality", "speed"), ("rc", "cbr")],
        GpuVendor.Intel => [("preset", "veryfast")],
        _ => [("preset", "ultrafast"), ("tune", "zerolatency")],
    };

    public static bool SupportsLiveBitrate(string encoderName) =>
        encoderName is "h264_nvenc" or "hevc_nvenc" or "libx264";

    public static string BuildEncoderArgs(GpuVendor vendor, SupportedVideoCodec codec, int bitrateKbps, int maxrateKbps, int bufsizeKbps, int gopSize)
    {
        string options = string.Join(" ", EncoderOptions(vendor).Select(option => $"-{option.Name} {option.Value}"));

        return $"-c:v {EncoderName(vendor, codec)} {options} -b:v {bitrateKbps}k -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k -g {gopSize}";
    }

    public static string BuildOutputPixelFormatArgs(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel => "",
        _ => "-pix_fmt yuv420p",
    };

    public static string RawFormatName(SupportedVideoCodec codec) => codec switch
    {
        SupportedVideoCodec.H264 => "h264",
        SupportedVideoCodec.Hevc => "hevc",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}
