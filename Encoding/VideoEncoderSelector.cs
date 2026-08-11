using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Encoding;

public enum VideoCodec
{
    H264,
    Hevc,
}

/// <summary>
/// Picks ffmpeg's hardware encoder for the detected GPU vendor and chosen codec, falling back to
/// software libx264/libx265. Every option name here was verified against the actual bundled
/// ffmpeg build via `ffmpeg -h encoder=X` and a real local encode/decode test (including actual
/// NVENC H264 and HEVC encodes on an RTX 3080) - not assumed, since NVENC/AMF option names have
/// changed across ffmpeg/driver versions and a stale NuGet-distributed ffmpeg build failed
/// against a modern NVIDIA driver during earlier testing (SDK version mismatch), which is why
/// ffmpeg.exe is bundled directly from BtbN's current build rather than via NuGet.
///
/// HEVC uses the exact same option names as their H264 counterparts on every vendor tested here
/// (NVENC: preset/tune/rc: AMF: usage/quality/rc; QSV: preset) - confirmed via `ffmpeg -h
/// encoder=hevc_X` against the bundled binary rather than assumed from the H264 case.
/// </summary>
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

    /// <summary>The raw elementary-stream format name ffmpeg expects for -f on either side of
    /// the pipe for this codec - confirmed against the bundled binary's `-formats` listing.</summary>
    public static string RawFormatName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.Hevc => "hevc",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}
