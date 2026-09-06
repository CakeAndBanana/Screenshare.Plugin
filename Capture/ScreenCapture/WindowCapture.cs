using System.Runtime.InteropServices;
using ScreenShare.Plugin.Capture.ScreenCapture.Interop;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
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
/// Resizing the window mid-stream is handled rather than skipped, which is what makes a browser
/// going fullscreen work: Windows.Graphics.Capture hands back frames at the frame pool's size, so
/// the pool is recreated at the window's new size and the frame is then scaled into the SAME
/// fixed-size texture this capture has always exposed.
///
/// Keeping that texture fixed is the point. Everything downstream is built around the size this
/// reported at construction - the encoder holds a ShaderResourceView over this exact texture, its
/// encode resolution was derived from it, and the viewer was told that resolution once when the
/// stream started. Recreating the texture would dangle that view; renegotiating the resolution
/// would mean restarting the stream, which is precisely the interruption being avoided. Scaling
/// into a fixed target costs one GPU blit and nothing downstream notices.
///
/// The scale preserves aspect ratio and centres, so a window that changes shape gets bars rather
/// than a stretched picture. DesktopDuplicationCapture still has the older limitation for a
/// monitor resolution change.
/// </summary>
public sealed class WindowCapture : ICaptureSource
{
    private readonly Device device;
    private readonly Lock contextLock;
    private readonly WindowsGraphicsCaptureInterop.IDirect3D11CaptureFramePool framePool;
    private readonly WindowsGraphicsCaptureInterop.IGraphicsCaptureSession session;
    private readonly Texture2D targetTexture;
    private readonly RenderTargetView targetView;
    private readonly GpuDownscaler downscaler;
    private readonly nint direct3DDevicePtr;
    private readonly Thread captureThread;
    private volatile bool running;
    private long framesCaptured;

    /// <summary>The frame pool's current size, which tracks the WINDOW - unlike <see cref="Width"/>
    /// and <see cref="Height"/>, which stay fixed for the life of this capture. Touched only by the
    /// capture thread.</summary>
    private int poolWidth;
    private int poolHeight;

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

            // Retained for the life of the capture rather than released here: recreating the frame
            // pool on a window resize needs this same device.
            direct3DDevicePtr = CreateDirect3DDeviceFromSharpDXDevice(device);
            nint framePoolPtr = CreateFreeThreadedFramePool(direct3DDevicePtr, Width, Height);

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

        poolWidth = Width;
        poolHeight = Height;

        targetTexture = SharedTextureFactory.CreateEmpty(device, Width, Height, renderTarget: true);
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
            int contentWidth = Math.Max(2, contentSize.Width);
            int contentHeight = Math.Max(2, contentSize.Height);

            if (contentWidth != poolWidth || contentHeight != poolHeight)
            {
                // This frame still came out of the old pool, so it is dropped and the next one
                // arrives at the new size - one or two frames at a resize, not a dead stream.
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

                    // The texture's own description rather than the size the pool was asked for -
                    // this is what is actually being sampled, and CopyResource is only legal when
                    // it matches the destination exactly.
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
            // Close() lives on IClosable, a separate interface/vtable from IDirect3D11CaptureFrame
            // itself - QI'd via the direct interface-to-interface cast, same underlying RCW.
            // Returns the frame's buffer to the pool for reuse; must happen before releasing frame.
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

        // A resize used to be reported as a failure and is no longer one, so an older one must not
        // be left standing in the UI.
        LastError = null;
        ServiceCollector.PluginLog.Information(
            $"ScreenShare's captured window resized to {width}x{height}; scaling it into the {Width}x{Height} stream.");
    }

    /// <summary>Scales a differently-sized frame into the fixed target, preserving aspect ratio and
    /// centring it - a window that changes shape gets bars rather than a stretched picture.</summary>
    private void BlitFitted(Texture2D frameTexture, int sourceWidth, int sourceHeight)
    {
        using var sourceView = new ShaderResourceView(device, frameTexture);

        double scale = Math.Min(Width / (double)sourceWidth, Height / (double)sourceHeight);
        int fittedWidth = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, Width);
        int fittedHeight = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, Height);

        lock (contextLock)
        {
            DeviceContext context = device.ImmediateContext;

            // Cleared every frame, not just the first after a resize: the bars fall outside the
            // blit's viewport, so nothing else ever writes them.
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

        if (direct3DDevicePtr != nint.Zero)
            Marshal.Release(direct3DDevicePtr);

        downscaler.Dispose();
        targetView.Dispose();
        targetTexture.Dispose();
    }
}
