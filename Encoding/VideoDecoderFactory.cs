using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.H264;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Encoding.MediaFoundation;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Video decoder factory that creates an appropriate decoder based on the codec and system capabilities
/// </summary>
public static class VideoDecoderFactory
{
    /// <summary>
    /// Creates a video decoder for the specified codec, using Media Foundation if available and preferred, otherwise falling back to ffmpeg.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device</param>
    /// <param name="ffmpegExePath">The path to the ffmpeg executable</param>
    /// <param name="width">The width of the video</param>
    /// <param name="height">The height of the video</param>
    /// <param name="fps">The frames per second of the video</param>
    /// <param name="codec">The video codec</param>
    /// <param name="getAudioPtsMs">A function to get the audio presentation time stamp in milliseconds</param>
    /// <param name="getSyncOffsetMs">A function to get the synchronization offset in milliseconds</param>
    /// <param name="audioLatencyBudgetMs">The audio latency budget in milliseconds</param>
    /// <param name="preferMediaFoundation">A value indicating whether to prefer Media Foundation</param>
    /// <returns>The created video decoder</returns>
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
        // Attempt to create a Media Foundation decoder if preferred
        if (preferMediaFoundation)
        {
            // Ensure Media Foundation is started and create the decoder
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

        // If Media Foundation is not preferred or failed, create an ffmpeg-based decoder
        var fallback = new H264Decoder(
            gameAdapterDevice, ffmpegExePath, width, height, fps, codec,
            getAudioPtsMs, getSyncOffsetMs, audioLatencyBudgetMs);

        ServiceCollector.PluginLog.Information($"ScreenShare is decoding {codec} with {fallback.DecoderName}.");
        return fallback;
    }
}
