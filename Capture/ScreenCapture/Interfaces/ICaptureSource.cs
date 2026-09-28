using SharpDX.Direct3D11;

namespace ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;

/// <summary>
/// Capture source interface for screen capture, providing properties and methods to access captured frames and related information.
/// </summary>
public interface ICaptureSource : IDisposable
{
    int Width { get; }

    int Height { get; }

    Texture2D CaptureTexture { get; }

    nint SharedHandle { get; }

    Exception? LastError { get; }

    long FramesCaptured { get; }
}
