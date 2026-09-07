using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

public sealed class DesktopDuplicationCapture : ICaptureSource
{
    private readonly Device device;
    private readonly Lock contextLock;
    private readonly Texture2D targetTexture;
    private readonly Thread captureThread;
    private readonly int outputIndex;

    private readonly Lock duplicationLock = new();

    private OutputDuplication duplication;

    private volatile bool running;

    public int Width { get; }

    public int Height { get; }

    public nint SharedHandle { get; }

    public Texture2D CaptureTexture => targetTexture;

    public Exception? LastError { get; private set; }

    public long FramesCaptured => Interlocked.Read(ref framesCaptured);
    private long framesCaptured;

    // Cross-reload stop flag. See DuplicationRegistry: a capture stranded by a plugin reload holds
    // its monitor for the rest of the game session, and polling this is how it learns to let go.
    private readonly Guid registrationId;
    private readonly System.Runtime.CompilerServices.StrongBox<bool> stopRequested;

    public DesktopDuplicationCapture(GameAdapterDevice gameAdapterDevice, int outputIndex = 0)
    {
        device = gameAdapterDevice.Device;
        contextLock = gameAdapterDevice.ImmediateContextLock;
        this.outputIndex = outputIndex;

        // Before duplicating anything, ask any capture stranded by a previous plugin load to let
        // go of its monitor. Without this, one reload that skipped Dispose blocks monitor capture
        // until the game restarts - and the failure names the monitor, not the cause.
        int stranded = DuplicationRegistry.RequestStopAll();
        if (stranded > 0)
        {
            ServiceCollector.PluginLog.Information(
                $"ScreenShare asked {stranded} capture(s) left by a previous plugin load to stop before duplicating.");

            // Their loops poll at frame cadence and release on the way out; a short wait here is
            // the difference between this attempt succeeding and failing for the same old reason.
            Thread.Sleep(250);
        }

        (registrationId, stopRequested) = DuplicationRegistry.Register();

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
        while (running && !stopRequested.Value)
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
        DuplicationRegistry.Unregister(registrationId);
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
