using System.Diagnostics;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.H264;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Encoding.LibavCodec;
using ScreenShare.Plugin.Encoding.MediaFoundation;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Creates an appropriate video encoder based on the codec and system capabilities
/// </summary>
public static class VideoEncoderFactory
{
    /// <summary>
    /// Creates a video encoder for the specified codec, using Media Foundation if available and preferred, otherwise falling back to ffmpeg.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device</param>
    /// <param name="capture">The capture source</param>
    /// <param name="ffmpegExePath">The path to the ffmpeg executable</param>
    /// <param name="sessionClock">The session clock</param>
    /// <param name="targetHeight">The target height of the video</param>
    /// <param name="bitrateKbps">The bitrate in kbps</param>
    /// <param name="fps">The frames per second of the video</param>
    /// <param name="codec">The video codec</param>
    /// <param name="preferMediaFoundation">A value indicating whether to prefer Media Foundation</param>
    /// <returns>The created video encoder</returns>
    public static IVideoEncoder Create(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource capture,
        string ffmpegExePath,
        Stopwatch sessionClock,
        int targetHeight,
        int bitrateKbps,
        int fps,
        SupportedVideoCodec codec,
        bool preferMediaFoundation = true)
    {
        // If Media Foundation is preferred, try to create a Media Foundation encoder first
        if (preferMediaFoundation)
        {
            // Ensure Media Foundation is started and create the encoder
            try
            {
                MediaFoundationInterop.EnsureStarted();
                var encoder = new MediaFoundationVideoEncoder(
                    gameAdapterDevice, capture, sessionClock, targetHeight, bitrateKbps, fps, codec);

                ServiceCollector.PluginLog.Information(
                    $"ScreenShare is encoding {codec} with Media Foundation: {encoder.EncoderName} " +
                    $"(live bitrate {(encoder.SupportsLiveBitrate ? "supported" : "unavailable")}).");

                return encoder;
            }
            catch (Exception ex)
            {
                ServiceCollector.PluginLog.Warning(
                    ex, $"ScreenShare could not start a Media Foundation encoder for {codec}; falling back to ffmpeg.");
            }
        }

        // If Media Foundation is not preferred or failed, create a fallback encoder using libavcodec or ffmpeg
        return CreateWithoutMediaFoundation(
            gameAdapterDevice, capture, ffmpegExePath, sessionClock, targetHeight, bitrateKbps, fps, codec);
    }

    /// <summary>
    /// Creates a video encoder for the specified codec without using Media Foundation, falling back to ffmpeg if libavcodec fails.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device</param>
    /// <param name="capture">The capture source</param>
    /// <param name="ffmpegExePath">The path to the ffmpeg executable</param>
    /// <param name="sessionClock">The session clock</param>
    /// <param name="targetHeight">The target height of the video</param>
    /// <param name="bitrateKbps">The bitrate in kbps</param>
    /// <param name="fps">The frames per second of the video</param>
    /// <param name="codec">The video codec</param>
    /// <returns>The created video encoder</returns>
    public static IVideoEncoder CreateWithoutMediaFoundation(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource capture,
        string ffmpegExePath,
        Stopwatch sessionClock,
        int targetHeight,
        int bitrateKbps,
        int fps,
        SupportedVideoCodec codec)
    {
        // Try to create a libavcodec encoder first
        try
        {
            // Ensure libavcodec is started and create the encoder
            var encoder = new LibavVideoEncoder(
                gameAdapterDevice, capture, Path.GetDirectoryName(ffmpegExePath)!, sessionClock,
                targetHeight, bitrateKbps, fps, codec);

            ServiceCollector.PluginLog.Information(
                $"ScreenShare is encoding {codec} with {encoder.EncoderName} " +
                $"(live bitrate {(encoder.SupportsLiveBitrate ? "supported" : "unavailable")}).");

            return encoder;
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(
                ex, $"ScreenShare could not start an in-process libavcodec encoder for {codec}; falling back to ffmpeg.exe.");
        }

        // If libavcodec fails, create a fallback encoder using ffmpeg.exe
        var fallback = new H264Encoder(
            gameAdapterDevice, capture, ffmpegExePath, sessionClock, targetHeight, bitrateKbps, fps, codec);

        ServiceCollector.PluginLog.Information($"ScreenShare is encoding {codec} with {fallback.EncoderName}.");
        return fallback;
    }
}
