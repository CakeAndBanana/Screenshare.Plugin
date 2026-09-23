using System.Diagnostics;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

public static class VideoEncoderFactory
{
    public static IVideoEncoder Create(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource capture,
        string ffmpegExePath,
        Stopwatch sessionClock,
        int targetHeight,
        int bitrateKbps,
        int fps,
        VideoCodec codec,
        bool preferMediaFoundation = true)
    {
        if (preferMediaFoundation)
        {
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

        return CreateWithoutMediaFoundation(
            gameAdapterDevice, capture, ffmpegExePath, sessionClock, targetHeight, bitrateKbps, fps, codec);
    }

    public static IVideoEncoder CreateWithoutMediaFoundation(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource capture,
        string ffmpegExePath,
        Stopwatch sessionClock,
        int targetHeight,
        int bitrateKbps,
        int fps,
        VideoCodec codec)
    {
        try
        {
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

        var fallback = new H264Encoder(
            gameAdapterDevice, capture, ffmpegExePath, sessionClock, targetHeight, bitrateKbps, fps, codec);

        ServiceCollector.PluginLog.Information($"ScreenShare is encoding {codec} with {fallback.EncoderName}.");
        return fallback;
    }
}
