using ScreenShare.Plugin.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

/// <summary>
/// Captures a monitor via the DXGI Desktop Duplication API into a persistent shared texture on
/// our own device, refreshed continuously on a background thread. Does not work under exclusive
/// fullscreen (DuplicateOutput throws) - callers should surface that as a clear message.
/// </summary>
public sealed class DesktopDuplicationCapture : ICaptureSource
{
    private readonly Device device;
    private readonly Lock contextLock;
    private readonly OutputDuplication duplication;
    private readonly Texture2D targetTexture;
    private readonly Thread captureThread;
    private volatile bool running;

    public int Width { get; }

    public int Height { get; }

    public nint SharedHandle { get; }

    /// <summary>The live capture texture itself (native resolution), on the same device passed
    /// to the constructor. Exposed so callers on that same device (e.g. H264Encoder's GPU
    /// downscale) can sample it directly via a ShaderResourceView instead of paying for another
    /// CopyResource - safe since it already carries the ShaderResource bind flag (see
    /// SharedTextureFactory.CreateEmpty). Callers must still go through ImmediateContextLock for
    /// any GPU work against it.</summary>
    public Texture2D CaptureTexture => targetTexture;

    public Exception? LastError { get; private set; }

    public long FramesCaptured => Interlocked.Read(ref framesCaptured);
    private long framesCaptured;

    public DesktopDuplicationCapture(GameAdapterDevice gameAdapterDevice, int outputIndex = 0)
    {
        device = gameAdapterDevice.Device;
        contextLock = gameAdapterDevice.ImmediateContextLock;

        using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
        using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;
        using Output output = adapter.GetOutput(outputIndex);
        using Output1 output1 = output.QueryInterface<Output1>();

        var bounds = output.Description.DesktopBounds;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        duplication = output1.DuplicateOutput(device);

        targetTexture = SharedTextureFactory.CreateEmpty(device, Width, Height);
        SharedHandle = SharedTextureFactory.GetSharedHandle(targetTexture);

        running = true;
        captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ScreenShare-DesktopDuplication",
        };
        captureThread.Start();
    }

    private void CaptureLoop()
    {
        while (running)
        {
            SharpDX.DXGI.Resource? desktopResource = null;
            try
            {
                Result result = duplication.TryAcquireNextFrame(200, out OutputDuplicateFrameInformation _, out desktopResource);
                if (result == SharpDX.DXGI.ResultCode.WaitTimeout || desktopResource is null)
                    continue;

                if (result.Failure)
                {
                    LastError = new SharpDXException(result);
                    if (result == SharpDX.DXGI.ResultCode.AccessLost)
                        break; // caller must recreate capture (e.g. after a mode switch)

                    continue;
                }

                using (Texture2D frameTexture = desktopResource.QueryInterface<Texture2D>())
                {
                    lock (contextLock)
                    {
                        device.ImmediateContext.CopyResource(frameTexture, targetTexture);
                    }
                }

                Interlocked.Increment(ref framesCaptured);
                duplication.ReleaseFrame();
            }
            catch (SharpDXException ex)
            {
                LastError = ex;
                Thread.Sleep(200);
            }
            finally
            {
                desktopResource?.Dispose();
            }
        }
    }

    /// <summary>Reads the latest captured frame into <paramref name="readback"/>'s CPU buffer,
    /// without blocking (see AsyncTextureReadback) - returns false if no new frame was ready
    /// yet. Safe to call concurrently with the capture loop - shares the same device
    /// ImmediateContext, so both sides take <see cref="contextLock"/>.</summary>
    public bool ReadLatestFrameInto(AsyncTextureReadback readback, byte[] destination)
    {
        lock (contextLock)
        {
            return readback.TryReadPrevious(targetTexture, destination);
        }
    }

    public void Dispose()
    {
        running = false;
        captureThread.Join(1000);
        targetTexture.Dispose();
        duplication.Dispose();
    }
}
