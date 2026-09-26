using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.H264;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Encoding.MediaFoundation;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

public static class VideoDecoderFactory
{
    public static IVideoDecoder Create(
        GameAdapterDevice gameAdapterDevice,
        string ffmpegExePath,
        int width,
        int height,
        int fps,
        SupportedVideoCodec codec,
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
