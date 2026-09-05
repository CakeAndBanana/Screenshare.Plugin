using System.Diagnostics;
using ScreenShare.Plugin.Capture.ScreenCapture;
using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Picks the video encoder for a hosting session: Media Foundation where the machine has a
/// hardware MFT for the chosen codec, ffmpeg otherwise.
///
/// The fallback is automatic and silent to the user because it has to be. Async MFTs behave
/// differently between NVIDIA, AMD and Intel, and HEVC in particular depends on codecs that may
/// simply not be installed - ffmpeg bundles its own, so it works where MF does not. ffmpeg is
/// still shipped for decoding regardless, so keeping it as the encode fallback costs nothing.
/// </summary>
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
                // Warning rather than error: the stream is about to start anyway, on a path that
                // has been in use far longer than this one.
                ServiceCollector.PluginLog.Warning(
                    ex, $"ScreenShare could not start a Media Foundation encoder for {codec}; falling back to ffmpeg.");
            }
        }

        var fallback = new H264Encoder(
            gameAdapterDevice, capture, ffmpegExePath, sessionClock, targetHeight, bitrateKbps, fps, codec);

        ServiceCollector.PluginLog.Information($"ScreenShare is encoding {codec} with {fallback.EncoderName}.");
        return fallback;
    }
}
