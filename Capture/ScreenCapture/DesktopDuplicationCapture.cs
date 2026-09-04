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

    /// <summary>Wraps the one failure worth explaining in full. E_INVALIDARG here has two quite
    /// different causes and the raw "The parameter is incorrect" hints at neither: either this
    /// process is already duplicating that monitor and never released it (see the class doc
    /// comment), or the game is holding that monitor in exclusive fullscreen, which Desktop
    /// Duplication cannot capture at all. The second is easy to mistake for a plugin bug, because
    /// window capture keeps working perfectly - it goes through Windows.Graphics.Capture, which
    /// has no such restriction. DXGI does not distinguish the two, so the message names both.</summary>
    private static OutputDuplication DuplicateOrThrow(Output1 output1, Device device, string deviceName)
    {
        try
        {
            return output1.DuplicateOutput(device);
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.Result.InvalidArg)
        {
            throw new InvalidOperationException(
                $"Windows refused to duplicate {deviceName}. {DescribeAdapterTopology(device, deviceName)} " +
                $"{ProbeEveryOutput(device, deviceName)} " +
                "Sharing a window instead works regardless - it uses a different capture API.", ex);
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.Unsupported)
        {
            throw new InvalidOperationException(
                $"Windows does not support duplicating {deviceName} on this GPU. If the game is " +
                "running in exclusive fullscreen, switch it to borderless windowed and try again.", ex);
        }
    }

    /// <summary>Names which GPU each monitor is really attached to, for the error above.
    ///
    /// Desktop Duplication requires the D3D11 device to be on the adapter that genuinely drives the
    /// output, and E_INVALIDARG is what DXGI returns when it isn't. That mismatch happens routinely
    /// on hybrid-graphics machines: the discrete GPU enumerates monitors that are physically wired
    /// through the integrated one, so the output looks available on this adapter and then refuses
    /// to duplicate. Nothing in the raw HRESULT hints at any of that, and it is indistinguishable
    /// by eye from the other causes - so the message says outright where the monitor actually
    /// lives and where we tried to capture it from.</summary>
    private static string DescribeAdapterTopology(Device device, string deviceName)
    {
        try
        {
            using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
            using SharpDX.DXGI.Adapter ownAdapter = dxgiDevice.Adapter;
            long ownLuid = ownAdapter.Description.Luid;
            string ownName = ownAdapter.Description.Description;

            using var factory = new SharpDX.DXGI.Factory1();
            for (int i = 0; i < factory.GetAdapterCount1(); i++)
            {
                using SharpDX.DXGI.Adapter1 adapter = factory.GetAdapter1(i);
                foreach (SharpDX.DXGI.Output output in adapter.Outputs)
                {
                    using (output)
                    {
                        if (output.Description.DeviceName != deviceName)
                            continue;

                        if (adapter.Description1.Luid == ownLuid)
                            return $"It is on this GPU ({ownName}), so the GPU pairing is not the problem.";

                        return $"That monitor is driven by {adapter.Description1.Description}, but capture " +
                               $"runs on {ownName} (the GPU the game renders on). Desktop Duplication cannot " +
                               "cross GPUs, which is why this monitor fails while others may work.";
                    }
                }
            }

            return $"No GPU reported owning it, while capture runs on {ownName}.";
        }
        catch (Exception ex)
        {
            // Diagnostics must never replace the real failure with one of their own.
            return $"(could not determine which GPU owns it: {ex.Message})";
        }
    }

    /// <summary>Tries to duplicate every output on this adapter and reports which ones actually
    /// work, plus whether the target is attached to the desktop at all.
    ///
    /// This is the measurement that separates the remaining explanations, which are otherwise
    /// indistinguishable from a single failing HRESULT. If EVERY output refuses, the problem is
    /// process-wide or device-wide and nothing about this particular monitor matters. If only this
    /// one refuses while others succeed, something owns this specific output - another application
    /// duplicating it, or the display not being in a duplicable state. Guessing between those from
    /// the outside is what made this bug take several rounds; the probe answers it directly.
    ///
    /// Each probe is released immediately. A duplication left behind here would itself become the
    /// permanent, process-wide failure this class's doc comment warns about - the diagnostic would
    /// cause the disease it is trying to identify.</summary>
    private static string ProbeEveryOutput(Device device, string targetDeviceName)
    {
        try
        {
            using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
            using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;

            var results = new List<string>();
            string attachment = "unknown";

            foreach (Output output in adapter.Outputs)
            {
                using (output)
                {
                    SharpDX.DXGI.OutputDescription description = output.Description;
                    if (description.DeviceName == targetDeviceName)
                        attachment = description.IsAttachedToDesktop ? "attached to the desktop" : "NOT attached to the desktop";

                    using Output1 output1 = output.QueryInterface<Output1>();
                    try
                    {
                        using OutputDuplication probe = output1.DuplicateOutput(device);
                        results.Add($"{description.DeviceName}=ok");
                    }
                    catch (SharpDXException probeEx)
                    {
                        results.Add($"{description.DeviceName}={probeEx.Descriptor.ApiCode}");
                    }
                }
            }

            return $"The monitor is {attachment}. Duplication probe across every output: " +
                   $"{string.Join(", ", results)}. {ProbeWithFreshDevice(adapter, targetDeviceName)}";
        }
        catch (Exception ex)
        {
            return $"(output probe failed: {ex.Message})";
        }
    }

    /// <summary>Repeats the duplication attempt with a brand-new, independently created D3D11
    /// device on the same adapter.
    ///
    /// Splits the last two explanations apart. If a fresh device succeeds where ours fails, the
    /// fault is in how the capture device is built - it is created pinned to the game device's
    /// feature level, which is not necessarily a level Desktop Duplication accepts. If a fresh
    /// device fails identically, nothing this plugin does is responsible: something outside the
    /// process holds the outputs, or duplication is unavailable machine-wide, and the fix is not
    /// in this codebase at all.</summary>
    private static string ProbeWithFreshDevice(SharpDX.DXGI.Adapter adapter, string targetDeviceName)
    {
        try
        {
            // Deliberately no feature-level argument: let D3D pick, which is exactly the variable
            // being tested against the pinned level GameAdapterDevice uses.
            using var freshDevice = new Device(adapter, DeviceCreationFlags.BgraSupport);

            foreach (Output output in adapter.Outputs)
            {
                using (output)
                {
                    if (output.Description.DeviceName != targetDeviceName)
                        continue;

                    using Output1 output1 = output.QueryInterface<Output1>();
                    try
                    {
                        using OutputDuplication probe = output1.DuplicateOutput(freshDevice);
                        return $"A FRESH device duplicates it fine (feature level {freshDevice.FeatureLevel}), " +
                               "so the capture device itself is what Windows is rejecting.";
                    }
                    catch (SharpDXException probeEx)
                    {
                        return $"A fresh device fails too ({probeEx.Descriptor.ApiCode}, feature level " +
                               $"{freshDevice.FeatureLevel}), so something outside this plugin is holding " +
                               "the display - another capture application, or a machine-wide restriction.";
                    }
                }
            }

            return "(the target output vanished between probes)";
        }
        catch (Exception ex)
        {
            return $"(fresh-device probe failed: {ex.Message})";
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
