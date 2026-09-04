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
///
/// The OutputDuplication this holds is a PROCESS-WIDE, SINGLE-OWNER resource: while one is alive,
/// every further DuplicateOutput call for the same monitor anywhere in this process fails, and it
/// stays that way until the game is restarted. Measured on an RTX 3080 / Windows 11, the failure
/// is E_INVALIDARG (0x80070057), NOT the DXGI_ERROR_NOT_CURRENTLY_AVAILABLE the docs suggest -
/// same result whether the second attempt uses the same device or a brand new one. That makes any
/// path that drops this object without disposing it a permanent, and extremely confusing,
/// "screen capture init failed: The parameter is incorrect" for the rest of the session, so both
/// the constructor and <see cref="Dispose"/> below are written to never leak it.
/// </summary>
public sealed class DesktopDuplicationCapture : ICaptureSource
{
    private readonly Device device;
    private readonly Lock contextLock;
    private readonly Texture2D targetTexture;
    private readonly Thread captureThread;
    private readonly int outputIndex;

    /// <summary>Guards swapping <see cref="duplication"/> out on AccessLost against Dispose
    /// disposing it - without it, a recreation racing teardown would strand the replacement, which
    /// is precisely the leak the class doc comment warns about.</summary>
    private readonly Lock duplicationLock = new();

    /// <summary>Not readonly: DXGI hands back AccessLost whenever the desktop changes underneath
    /// us (resolution change, fullscreen transition, UAC/secure-desktop prompt, driver update),
    /// and the documented cure is to build a fresh duplication rather than to give up.</summary>
    private OutputDuplication duplication;

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
        this.outputIndex = outputIndex;

        using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
        using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;
        using Output output = GetOutputOrThrow(adapter, outputIndex);
        using Output1 output1 = output.QueryInterface<Output1>();

        var bounds = output.Description.DesktopBounds;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        duplication = DuplicateOrThrow(output1, device, output.Description.DeviceName);

        // Everything from here on can throw (texture creation, shared-handle retrieval, even
        // thread start), and if it does, this instance never reaches the caller - so nobody can
        // ever call Dispose on it and the duplication above would be stranded for the lifetime of
        // the process, permanently breaking capture of this monitor. See the class doc comment.
        try
        {
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
        catch
        {
            targetTexture?.Dispose();
            duplication.Dispose();
            throw;
        }
    }

    /// <summary>Turns an out-of-range monitor index into something that names the actual problem.
    /// A stale saved MonitorIndex (a display was unplugged, or the picker was never reopened after
    /// the layout changed) otherwise surfaces as a bare DXGI_ERROR_NOT_FOUND.</summary>
    private static Output GetOutputOrThrow(SharpDX.DXGI.Adapter adapter, int outputIndex)
    {
        try
        {
            return adapter.GetOutput(outputIndex);
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.NotFound)
        {
            int available = adapter.GetOutputCount();
            throw new InvalidOperationException(
                $"Monitor {outputIndex + 1} no longer exists - this GPU currently has {available} " +
                "connected monitor(s). Pick the monitor again in the ScreenShare settings.", ex);
        }
    }

    /// <summary>Wraps the one failure that is worth explaining in full. E_INVALIDARG here almost
    /// always means this process is ALREADY duplicating that monitor and never released it (see
    /// the class doc comment) - the raw "The parameter is incorrect" gives no hint of that, and
    /// the only reliable cure once it happens is restarting the game.</summary>
    private static OutputDuplication DuplicateOrThrow(Output1 output1, Device device, string deviceName)
    {
        try
        {
            return output1.DuplicateOutput(device);
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.Result.InvalidArg)
        {
            throw new InvalidOperationException(
                $"Windows refused to duplicate {deviceName}: something in this process is still " +
                "holding a capture of that monitor. That usually means a previous capture was not " +
                "cleaned up (most often after reloading the plugin without restarting the game). " +
                "Restart the game, or share a different monitor.", ex);
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.Unsupported)
        {
            throw new InvalidOperationException(
                $"Windows does not support duplicating {deviceName} on this GPU. If the game is " +
                "running in exclusive fullscreen, switch it to borderless windowed and try again.", ex);
        }
    }

    private void CaptureLoop()
    {
        while (running)
        {
            SharpDX.DXGI.Resource? desktopResource = null;
            try
            {
                Result result = duplication.TryAcquireNextFrame(200, out OutputDuplicateFrameInformation _, out desktopResource);
                if (result == SharpDX.DXGI.ResultCode.WaitTimeout)
                    continue;

                // Checked BEFORE the null test, not after it: a failed acquire hands back a null
                // resource, so testing null first swallowed every genuine error here - including
                // AccessLost, which then never took its branch and left this thread spinning on a
                // permanently dead duplication as fast as the CPU allowed, silently, forever.
                if (result.Failure)
                {
                    LastError = new SharpDXException(result);
                    if (result == SharpDX.DXGI.ResultCode.AccessLost && !TryRecreateDuplication())
                        break;

                    continue;
                }

                if (desktopResource is null)
                    continue;

                try
                {
                    using Texture2D frameTexture = desktopResource.QueryInterface<Texture2D>();
                    lock (contextLock)
                    {
                        device.ImmediateContext.CopyResource(frameTexture, targetTexture);
                    }

                    Interlocked.Increment(ref framesCaptured);
                }
                finally
                {
                    // Has to happen even when the copy above throws. DXGI allows exactly one
                    // outstanding acquired frame, so skipping the release turns a single bad frame
                    // into DXGI_ERROR_INVALID_CALL on every subsequent acquire - capture freezes on
                    // the last good frame and never recovers.
                    duplication.ReleaseFrame();
                }
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

    /// <summary>Rebuilds the duplication after AccessLost, which DXGI raises routinely rather than
    /// exceptionally - any resolution change, fullscreen transition or secure-desktop prompt does
    /// it. This used to just break out of the capture loop, which left the stream frozen on its
    /// last frame for the rest of the session while still reporting itself as capturing. Returns
    /// false if the monitor is genuinely gone, which is the one case worth giving up on.</summary>
    private bool TryRecreateDuplication()
    {
        try
        {
            lock (duplicationLock)
            {
                if (!running)
                    return false;

                duplication.Dispose();

                using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
                using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;
                using Output output = GetOutputOrThrow(adapter, outputIndex);
                using Output1 output1 = output.QueryInterface<Output1>();

                duplication = DuplicateOrThrow(output1, device, output.Description.DeviceName);
                return true;
            }
        }
        catch (Exception ex)
        {
            LastError = ex;
            return false;
        }
    }

    public void Dispose()
    {
        running = false;
        captureThread.Join(1000);
        targetTexture.Dispose();

        // Under the same lock the capture thread swaps it under, so a teardown landing in the
        // middle of an AccessLost recovery disposes the replacement rather than the object the
        // recovery already threw away. Leaking one here would block every future capture of this
        // monitor until the game restarts - see the class doc comment.
        lock (duplicationLock)
        {
            duplication.Dispose();
        }
    }
}
