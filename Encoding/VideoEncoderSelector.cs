using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Encoding;

public enum VideoCodec
{
    H264,
    Hevc,
}

public static class VideoEncoderSelector
{
    public static string BuildEncoderArgs(GpuVendor vendor, VideoCodec codec, int bitrateKbps, int maxrateKbps, int bufsizeKbps, int gopSize)
    {
        string codecArgs = (vendor, codec) switch
        {
            (GpuVendor.Nvidia, VideoCodec.H264) => "-c:v h264_nvenc -preset p1 -tune ll -rc cbr",
            (GpuVendor.Nvidia, VideoCodec.Hevc) => "-c:v hevc_nvenc -preset p1 -tune ll -rc cbr",
            (GpuVendor.Amd, VideoCodec.H264) => "-c:v h264_amf -usage lowlatency -quality speed -rc cbr",
            (GpuVendor.Amd, VideoCodec.Hevc) => "-c:v hevc_amf -usage lowlatency -quality speed -rc cbr",
            (GpuVendor.Intel, VideoCodec.H264) => "-c:v h264_qsv -preset veryfast",
            (GpuVendor.Intel, VideoCodec.Hevc) => "-c:v hevc_qsv -preset veryfast",
            (_, VideoCodec.H264) => "-c:v libx264 -preset ultrafast -tune zerolatency",
            (_, VideoCodec.Hevc) => "-c:v libx265 -preset ultrafast -tune zerolatency",
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };

        return $"{codecArgs} -b:v {bitrateKbps}k -maxrate {maxrateKbps}k -bufsize {bufsizeKbps}k -g {gopSize}";
    }

    public static string RawFormatName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.Hevc => "hevc",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}
