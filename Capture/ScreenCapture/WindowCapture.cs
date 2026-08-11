using System.Runtime.InteropServices;
using ScreenShare.Plugin.Capture.ScreenCapture.Interop;
using ScreenShare.Plugin.Rendering;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;
using SizeInt32 = ScreenShare.Plugin.Capture.ScreenCapture.Interop.WindowsGraphicsCaptureInterop.SizeInt32;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

/// <summary>
/// Captures one specific top-level window via Windows.Graphics.Capture, into a persistent shared
/// texture on our own device, refreshed continuously on a background thread - the window-capture
/// counterpart to <see cref="DesktopDuplicationCapture"/> (which only ever captures a whole
/// monitor). See <see cref="WindowsGraphicsCaptureInterop"/> for the raw interop this is built on.
///
/// Known, accepted limitation: if the captured window is resized mid-stream, frames at the new
/// size are skipped (not crashed on) rather than dynamically resizing the whole downstream
/// pipeline (H264Encoder's ffmpeg args are fixed at construction time) - restarting the stream
/// picks up the new size. DesktopDuplicationCapture has the same practical limitation for a
/// monitor resolution change, so this isn't a new gap relative to what already ships.
/// </summary>
public sealed class WindowCapture : ICaptureSource
{
    private readonly Device device;
    private readonly Lock contextLock;
    private readonly WindowsGraphicsCaptureInterop.IDirect3D11CaptureFramePool framePool;
    private readonly WindowsGraphicsCaptureInterop.IGraphicsCaptureSession session;
    private readonly Texture2D targetTexture;
    private readonly Thread captureThread;
    private volatile bool running;
    private long framesCaptured;

    public int Width { get; }

    public int Height { get; }

    public Texture2D CaptureTexture => targetTexture;

    public Exception? LastError { get; private set; }

    public long FramesCaptured => Interlocked.Read(ref framesCaptured);

    public WindowCapture(GameAdapterDevice gameAdapterDevice, nint hwnd)
    {
        device = gameAdapterDevice.Device;
        contextLock = gameAdapterDevice.ImmediateContextLock;

        nint itemPtr = CreateItemForWindow(hwnd);
        try
        {
            var item = (WindowsGraphicsCaptureInterop.IGraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr);
            try
            {
                Marshal.ThrowExceptionForHR(item.GetSize(out SizeInt32 size));
                Width = Math.Max(2, size.Width);
                Height = Math.Max(2, size.Height);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }

            nint direct3DDevicePtr = CreateDirect3DDeviceFromSharpDXDevice(device);
            nint framePoolPtr;
            try
            {
                framePoolPtr = CreateFreeThreadedFramePool(direct3DDevicePtr, Width, Height);
            }
            finally
            {
                Marshal.Release(direct3DDevicePtr);
            }

            framePool = (WindowsGraphicsCaptureInterop.IDirect3D11CaptureFramePool)Marshal.GetObjectForIUnknown(framePoolPtr);
            Marshal.Release(framePoolPtr);

            Marshal.ThrowExceptionForHR(framePool.CreateCaptureSession(itemPtr, out nint sessionPtr));
            session = (WindowsGraphicsCaptureInterop.IGraphicsCaptureSession)Marshal.GetObjectForIUnknown(sessionPtr);
            Marshal.Release(sessionPtr);

            Marshal.ThrowExceptionForHR(session.StartCapture());
        }
        finally
        {
            Marshal.Release(itemPtr);
        }

        targetTexture = SharedTextureFactory.CreateEmpty(device, Width, Height);

        running = true;
        captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ScreenShare-WindowCapture",
        };
        captureThread.Start();
    }

    private const string GraphicsCaptureItemClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    private static nint CreateItemForWindow(nint hwnd)
    {
        nint factoryPtr = GetActivationFactory(
            GraphicsCaptureItemClassName,
            typeof(WindowsGraphicsCaptureInterop.IGraphicsCaptureItemInterop).GUID);
        try
        {
            var interop = (WindowsGraphicsCaptureInterop.IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                Guid iid = WindowsGraphicsCaptureInterop.IGraphicsCaptureItemIid;
                return interop.CreateForWindow(hwnd, ref iid);
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
    }

    private static nint CreateDirect3DDeviceFromSharpDXDevice(Device d3dDevice)
    {
        using DxgiDevice dxgiDevice = d3dDevice.QueryInterface<DxgiDevice>();
        uint hr = WindowsGraphicsCaptureInterop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint graphicsDevicePtr);
        Marshal.ThrowExceptionForHR((int)hr);
        return graphicsDevicePtr;
    }

    private static nint CreateFreeThreadedFramePool(nint direct3DDevicePtr, int width, int height)
    {
        nint factoryPtr = GetActivationFactory(
            WindowsGraphicsCaptureInterop.Direct3D11CaptureFramePoolClassName,
            typeof(WindowsGraphicsCaptureInterop.IDirect3D11CaptureFramePoolStatics2).GUID);
        try
        {
            var statics = (WindowsGraphicsCaptureInterop.IDirect3D11CaptureFramePoolStatics2)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                var size = new SizeInt32 { Width = width, Height = height };
                Marshal.ThrowExceptionForHR(statics.CreateFreeThreaded(
                    direct3DDevicePtr, WindowsGraphicsCaptureInterop.PixelFormatB8G8R8A8UIntNormalized, 2, size, out nint framePoolPtr));
                return framePoolPtr;
            }
            finally
            {
                Marshal.ReleaseComObject(statics);
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }
    }

    private static nint GetActivationFactory(string runtimeClassName, Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsGraphicsCaptureInterop.WindowsCreateString(runtimeClassName, runtimeClassName.Length, out nint hstring));
        try
        {
            Marshal.ThrowExceptionForHR(WindowsGraphicsCaptureInterop.RoGetActivationFactory(hstring, ref iid, out nint factory));
            return factory;
        }
        finally
        {
            WindowsGraphicsCaptureInterop.WindowsDeleteString(hstring);
        }
    }

    private void CaptureLoop()
    {
        while (running)
        {
            try
            {
                if (framePool.TryGetNextFrame(out nint framePtr) != 0 || framePtr == 0)
                {
                    Thread.Sleep(16);
                    continue;
                }

                try
                {
                    ProcessFrame(framePtr);
                }
                finally
                {
                    // Releases our own reference from TryGetNextFrame's [out] parameter -
                    // separate from (and in addition to) ProcessFrame releasing its own RCW.
                    Marshal.Release(framePtr);
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
                Thread.Sleep(200);
            }
        }
    }

    private void ProcessFrame(nint framePtr)
    {
        var frame = (WindowsGraphicsCaptureInterop.IDirect3D11CaptureFrame)Marshal.GetObjectForIUnknown(framePtr);
        try
        {
            Marshal.ThrowExceptionForHR(frame.GetContentSize(out SizeInt32 contentSize));
            if (contentSize.Width != Width || contentSize.Height != Height)
            {
                // See class doc comment - resize mid-stream is a known, accepted limitation.
                LastError = new InvalidOperationException(
                    $"Captured window resized ({contentSize.Width}x{contentSize.Height} vs {Width}x{Height}) - restart the stream to pick up the new size.");
                return;
            }

            Marshal.ThrowExceptionForHR(frame.GetSurface(out nint surfacePtr));
            try
            {
                var access = (WindowsGraphicsCaptureInterop.IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfacePtr);
                try
                {
                    Guid textureIid = WindowsGraphicsCaptureInterop.Id3D11Texture2DIid;
                    nint texturePtr = access.GetInterface(ref textureIid);
                    using var frameTexture = new Texture2D(texturePtr);

                    lock (contextLock)
                    {
                        device.ImmediateContext.CopyResource(frameTexture, targetTexture);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(access);
                }
            }
            finally
            {
                Marshal.Release(surfacePtr);
            }

            Interlocked.Increment(ref framesCaptured);
        }
        finally
        {
            // Close() lives on IClosable, a separate interface/vtable from IDirect3D11CaptureFrame
            // itself - QI'd via the direct interface-to-interface cast, same underlying RCW.
            // Returns the frame's buffer to the pool for reuse; must happen before releasing frame.
            ((WindowsGraphicsCaptureInterop.IWinRTClosable)frame).Close();
            Marshal.ReleaseComObject(frame);
        }
    }

    public void Dispose()
    {
        running = false;
        captureThread.Join(1000);

        try
        {
            // Close() lives on IClosable, a separate interface/vtable from IGraphicsCaptureSession
            // and IDirect3D11CaptureFramePool themselves - QI for it explicitly on each, same as
            // the per-frame Close() in the capture loop.
            ((WindowsGraphicsCaptureInterop.IWinRTClosable)session).Close();
        }
        catch
        {
            // best-effort - may already be torn down (e.g. the window closed mid-stream)
        }

        try
        {
            ((WindowsGraphicsCaptureInterop.IWinRTClosable)framePool).Close();
        }
        catch
        {
        }

        Marshal.ReleaseComObject(session);
        Marshal.ReleaseComObject(framePool);
        targetTexture.Dispose();
    }
}
