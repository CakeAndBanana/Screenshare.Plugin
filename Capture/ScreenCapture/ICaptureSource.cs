using SharpDX.Direct3D11;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

/// <summary>
/// Common surface shared by <see cref="DesktopDuplicationCapture"/> (a whole monitor) and
/// <see cref="WindowCapture"/> (one specific window) - lets H264Encoder/MediaSessionService/
/// ShareWindow work with either one identically without caring which is active.
/// </summary>
public interface ICaptureSource : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>The live capture texture (native resolution), on the same device the capture
    /// source was constructed with. Callers on that same device may sample it directly via a
    /// ShaderResourceView; any GPU work against it must go through the owning device's
    /// ImmediateContextLock.</summary>
    Texture2D CaptureTexture { get; }

    Exception? LastError { get; }

    long FramesCaptured { get; }
}
