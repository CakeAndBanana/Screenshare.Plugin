using System.Runtime.InteropServices;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Capture.ScreenCapture.Interop;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;
using SizeInt32 = ScreenShare.Plugin.Capture.ScreenCapture.Interop.WindowsGraphicsCaptureInterop.SizeInt32;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

public sealed class WindowCapture : ICaptureSource
{
    private readonly Device device;
    private readonly Lock contextLock;
    private readonly WindowsGraphicsCaptureInterop.IDirect3D11CaptureFramePool framePool;
    private readonly WindowsGraphicsCaptureInterop.IGraphicsCaptureSession session;
    private WindowsGraphicsCaptureInterop.IGraphicsCaptureSession2? cursorSession;
    private readonly nint targetWindow;
    private readonly Texture2D targetTexture;
    private readonly RenderTargetView targetView;
    private readonly GpuDownscaler downscaler;
    private readonly nint direct3DDevicePtr;
    private readonly Thread captureThread;
    private volatile bool running;
    private long framesCaptured;
    private bool cursorCaptured = true;

    private int poolWidth;
    private int poolHeight;

    public int Width { get; }

    public int Height { get; }

    public Texture2D CaptureTexture => targetTexture;

    public nint SharedHandle { get; }

    public Exception? LastError { get; private set; }

    public long FramesCaptured => Interlocked.Read(ref framesCaptured);

    public WindowCapture(GameAdapterDevice gameAdapterDevice, nint hwnd)
    {
        device = gameAdapterDevice.Device;
        contextLock = gameAdapterDevice.ImmediateContextLock;
        targetWindow = hwnd;

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

            direct3DDevicePtr = CreateDirect3DDeviceFromSharpDXDevice(device);
            nint framePoolPtr = CreateFreeThreadedFramePool(direct3DDevicePtr, Width, Height);

            framePool = (WindowsGraphicsCaptureInterop.IDirect3D11CaptureFramePool)Marshal.GetObjectForIUnknown(framePoolPtr);
            Marshal.Release(framePoolPtr);

            Marshal.ThrowExceptionForHR(framePool.CreateCaptureSession(itemPtr, out nint sessionPtr));
            session = (WindowsGraphicsCaptureInterop.IGraphicsCaptureSession)Marshal.GetObjectForIUnknown(sessionPtr);
            Marshal.Release(sessionPtr);

            // Null before Windows 10 2004; the cursor then stays captured whenever it's inside the window.
            cursorSession = session as WindowsGraphicsCaptureInterop.IGraphicsCaptureSession2;
            UpdateCursorCapture();

            Marshal.ThrowExceptionForHR(session.StartCapture());
        }
        finally
        {
            Marshal.Release(itemPtr);
        }

        poolWidth = Width;
        poolHeight = Height;

        targetTexture = SharedTextureFactory.CreateEmpty(device, Width, Height, renderTarget: true);
        SharedHandle = SharedTextureFactory.GetSharedHandle(targetTexture);
        targetView = new RenderTargetView(device, targetTexture);
        downscaler = new GpuDownscaler(device);

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
                UpdateCursorCapture();

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

    // Windows draws the cursor whenever it's inside the window's rectangle, even while another
    // window covering it is the one actually under the cursor - so only capture it when it's ours.
    private void UpdateCursorCapture()
    {
        if (cursorSession is null)
            return;

        bool overTarget = IsCursorOverTargetWindow();
        if (overTarget == cursorCaptured)
            return;

        int hr = cursorSession.SetIsCursorCaptureEnabled(overTarget);
        if (hr < 0)
        {
            cursorSession = null;
            ServiceCollector.PluginLog.Warning(
                $"ScreenShare couldn't toggle cursor capture (0x{hr:X8}); the cursor will show whenever it's inside the shared window.");
            return;
        }

        cursorCaptured = overTarget;
    }

    private bool IsCursorOverTargetWindow()
    {
        if (!WindowsGraphicsCaptureInterop.GetCursorPos(out WindowsGraphicsCaptureInterop.Point point))
            return false;

        nint hit = WindowsGraphicsCaptureInterop.WindowFromPoint(point);
        if (hit == 0)
            return false;

        // Root owner keeps the cursor visible over the shared app's own menus and dropdowns.
        return WindowsGraphicsCaptureInterop.GetAncestor(hit, WindowsGraphicsCaptureInterop.GaRoot) == targetWindow
            || WindowsGraphicsCaptureInterop.GetAncestor(hit, WindowsGraphicsCaptureInterop.GaRootOwner) == targetWindow;
    }

    private void ProcessFrame(nint framePtr)
    {
        var frame = (WindowsGraphicsCaptureInterop.IDirect3D11CaptureFrame)Marshal.GetObjectForIUnknown(framePtr);
        try
        {
            Marshal.ThrowExceptionForHR(frame.GetContentSize(out SizeInt32 contentSize));
            int contentWidth = Math.Max(2, contentSize.Width);
            int contentHeight = Math.Max(2, contentSize.Height);

            if (contentWidth != poolWidth || contentHeight != poolHeight)
            {
                RecreateFramePool(contentWidth, contentHeight);
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

                    Texture2DDescription description = frameTexture.Description;

                    if (description.Width == Width && description.Height == Height)
                    {
                        lock (contextLock)
                            device.ImmediateContext.CopyResource(frameTexture, targetTexture);
                    }
                    else
                    {
                        BlitFitted(frameTexture, description.Width, description.Height);
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
            ((WindowsGraphicsCaptureInterop.IWinRTClosable)frame).Close();
            Marshal.ReleaseComObject(frame);
        }
    }

    private void RecreateFramePool(int width, int height)
    {
        var size = new SizeInt32 { Width = width, Height = height };
        Marshal.ThrowExceptionForHR(framePool.Recreate(
            direct3DDevicePtr, WindowsGraphicsCaptureInterop.PixelFormatB8G8R8A8UIntNormalized, 2, size));

        poolWidth = width;
        poolHeight = height;

        LastError = null;
        ServiceCollector.PluginLog.Information(
            $"ScreenShare's captured window resized to {width}x{height}; scaling it into the {Width}x{Height} stream.");
    }

    private void BlitFitted(Texture2D frameTexture, int sourceWidth, int sourceHeight)
    {
        using var sourceView = new ShaderResourceView(device, frameTexture);

        double scale = Math.Min(Width / (double)sourceWidth, Height / (double)sourceHeight);
        int fittedWidth = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, Width);
        int fittedHeight = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, Height);

        lock (contextLock)
        {
            DeviceContext context = device.ImmediateContext;

            context.ClearRenderTargetView(targetView, new RawColor4(0f, 0f, 0f, 1f));
            downscaler.BlitToViewport(
                context, sourceView, targetView,
                (Width - fittedWidth) / 2, (Height - fittedHeight) / 2, fittedWidth, fittedHeight);
        }
    }

    public void Dispose()
    {
        running = false;
        captureThread.Join(1000);

        try
        {
            ((WindowsGraphicsCaptureInterop.IWinRTClosable)session).Close();
        }
        catch
        {
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

        if (direct3DDevicePtr != nint.Zero)
            Marshal.Release(direct3DDevicePtr);

        downscaler.Dispose();
        targetView.Dispose();
        targetTexture.Dispose();
    }
}
