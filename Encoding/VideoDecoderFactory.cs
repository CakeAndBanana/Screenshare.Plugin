using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Picks the video decoder for a watch session: Media Foundation where the machine can decode the
/// codec into D3D11 textures, ffmpeg otherwise.
///
/// The fallback is per codec and genuinely load-bearing here, more so than on the encode side.
/// H.264 decode ships with Windows; HEVC decode does not - it depends on the Microsoft Store's
/// "HEVC Video Extensions", whose free "from Device Manufacturer" edition only installs on some
/// hardware. ffmpeg bundles its own decoder for both, so a viewer without those extensions still
/// watches an HEVC stream; it just pays the CPU round trip while doing it.
/// </summary>
public static class VideoDecoderFactory
{
    public static IVideoDecoder Create(
        GameAdapterDevice gameAdapterDevice,
        string ffmpegExePath,
        int width,
        int height,
        int fps,
        VideoCodec codec,
        Func<long?> getAudioPtsMs,
        Func<int>? getSyncOffsetMs,
        int audioLatencyBudgetMs,
        bool preferMediaFoundation = true)
    {
        if (preferMediaFoundation)
        {
            try
            {
                MediaFoundationInterop.EnsureStarted();
                var decoder = new MediaFoundationVideoDecoder(
                    gameAdapterDevice, width, height, fps, codec,
                    getAudioPtsMs, getSyncOffsetMs, audioLatencyBudgetMs);

                ServiceCollector.PluginLog.Information(
                    $"ScreenShare is decoding {codec} with Media Foundation: {decoder.DecoderName}. " +
                    "Frames stay on the GPU.");

                return decoder;
            }
            catch (Exception ex)
            {
                // Warning, not error: for HEVC on a machine without the Store extensions this is
                // the expected outcome, and the stream is about to play regardless.
                ServiceCollector.PluginLog.Warning(
                    ex, $"ScreenShare could not start a Media Foundation decoder for {codec}; falling back to ffmpeg.");
            }
        }

        var fallback = new H264Decoder(
            gameAdapterDevice, ffmpegExePath, width, height, fps, codec,
            getAudioPtsMs, getSyncOffsetMs, audioLatencyBudgetMs);

        ServiceCollector.PluginLog.Information($"ScreenShare is decoding {codec} with {fallback.DecoderName}.");
        return fallback;
    }
}
