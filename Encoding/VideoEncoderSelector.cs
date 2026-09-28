using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Video encoder selector that provides encoder names and options based on GPU vendor and supported video codec
/// </summary>
public static class VideoEncoderSelector
{
    /// <summary>
    /// Encoder name based on GPU vendor and supported video codec
    /// </summary>
    /// <param name="vendor">The GPU vendor</param>
    /// <param name="codec">The supported video codec</param>
    /// <returns>The encoder name</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the codec is not supported</exception>
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

    /// <summary>
    /// Encoder options based on GPU vendor. Returns a list of name-value pairs representing encoder options.
    /// </summary>
    /// <param name="vendor">The GPU vendor</param>
    /// <returns>The encoder options</returns>
    public static IReadOnlyList<(string Name, string Value)> EncoderOptions(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => [("preset", "p1"), ("tune", "ll"), ("rc", "cbr")],
        GpuVendor.Amd => [("usage", "lowlatency"), ("quality", "speed"), ("rc", "cbr")],
        GpuVendor.Intel => [("preset", "veryfast")],
        _ => [("preset", "ultrafast"), ("tune", "zerolatency")],
    };

    /// <summary>
    /// Supports live bitrate adjustment for the given encoder name. Returns true if the encoder supports live bitrate adjustment, false otherwise.
    /// </summary>
    /// <param name="encoderName">The encoder name</param>
    /// <returns>True if the encoder supports live bitrate adjustment, false otherwise</returns>
    public static bool SupportsLiveBitrate(string encoderName) =>
        encoderName is "h264_nvenc" or "hevc_nvenc" or "libx264";

    /// <summary>
    /// Builds the encoder arguments based on GPU vendor, supported video codec, bitrate, maxrate, bufsize, and gop size. Returns a string representing the encoder arguments.
    /// </summary>
    /// <param name="vendor">The GPU vendor</param>
    /// <param name="codec">The supported video codec</param>
    /// <param name="bitrateKbps">The bitrate in kbps</param>
    /// <param name="maxrateKbps">The maximum rate in kbps</param>
    /// <param name="bufsizeKbps">The buffer size in kbps</param>
    /// <param name="gopSize">The GOP size</param>
    /// <returns>The encoder arguments</returns>
    public static string BuildEncoderArgs(GpuVendor vendor, SupportedVideoCodec codec, int bitrateKbps, int maxrateKbps, int bufsizeKbps, int gopSize)
    {
        string options = string.Join(" ", EncoderOptions(vendor).Select(option => $"-{option.Name} {option.Value}"));

        return $"-c:v {EncoderName(vendor, codec)} {options} -b:v {bitrateKbps}k -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k -g {gopSize}";
    }

    /// <summary>
    /// Builds the output pixel format arguments based on GPU vendor. Returns a string representing the output pixel format arguments.
    /// </summary>
    /// <param name="vendor">The GPU vendor</param>
    /// <returns>The output pixel format arguments</returns>
    public static string BuildOutputPixelFormatArgs(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel => "",
        _ => "-pix_fmt yuv420p",
    };

    /// <summary>
    /// Gives out the raw format name for the given supported video codec. Returns a string representing the raw format name.
    /// </summary>
    /// <param name="codec">The supported video codec</param>
    /// <returns>The raw format name</returns>
    /// <exception cref="ArgumentOutOfRangeException">The given video codec is not supported</exception>
    public static string RawFormatName(SupportedVideoCodec codec) => codec switch
    {
        SupportedVideoCodec.H264 => "h264",
        SupportedVideoCodec.Hevc => "hevc",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}
