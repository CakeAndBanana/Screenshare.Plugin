using SharpDX.Direct3D11;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

public interface ICaptureSource : IDisposable
{
    int Width { get; }

    int Height { get; }

    Texture2D CaptureTexture { get; }

    Exception? LastError { get; }

    long FramesCaptured { get; }
}
