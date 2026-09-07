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

    // The device the duplication and the frame copy actually run on, and the texture the rest of
    // the plugin reads. See ResolveCaptureDevice for why this is not simply the plugin's device.
    private readonly Device captureDevice;
    private readonly bool ownsCaptureDevice;
    private readonly Texture2D sharedView;
    private readonly Thread captureThread;
    private readonly int outputIndex;

    private readonly Lock duplicationLock = new();

    private OutputDuplication duplication;

    private volatile bool running;

    public int Width { get; }

    public int Height { get; }

    public nint SharedHandle { get; }

    public Texture2D CaptureTexture => sharedView;

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

        (captureDevice, ownsCaptureDevice) = ResolveCaptureDevice(device);

        using Output output = ResolveOutput(device, outputIndex);
        using Output1 output1 = output.QueryInterface<Output1>();

        var bounds = output.Description.DesktopBounds;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        duplication = DuplicateOrThrow(output1, captureDevice, output.Description.DeviceName);

        try
        {
            targetTexture = SharedTextureFactory.CreateEmpty(captureDevice, Width, Height);
            SharedHandle = SharedTextureFactory.GetSharedHandle(targetTexture);
            sharedView = ownsCaptureDevice
                ? device.OpenSharedResource<Texture2D>(SharedHandle)
                : targetTexture;

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
            if (!ReferenceEquals(sharedView, targetTexture))
                sharedView?.Dispose();

            targetTexture?.Dispose();
            duplication.Dispose();

            if (ownsCaptureDevice)
                captureDevice.Dispose();

            throw;
        }
    }

    private static (Device Device, bool Owned) ResolveCaptureDevice(Device pluginDevice)
    {
        try
        {
            long luid;
            using (DxgiDevice dxgiDevice = pluginDevice.QueryInterface<DxgiDevice>())
            using (SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter)
                luid = adapter.Description.Luid;

            using var factory = new SharpDX.DXGI.Factory1();
            for (int i = 0; i < factory.GetAdapterCount1(); i++)
            {
                using SharpDX.DXGI.Adapter1 adapter = factory.GetAdapter1(i);
                if (adapter.Description1.Luid != luid)
                    continue;

                return (new Device(adapter, DeviceCreationFlags.BgraSupport), true);
            }
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(
                ex, "ScreenShare could not create its own capture device; falling back to the shared one.");
        }

        return (pluginDevice, false);
    }

    private static Output ResolveOutput(Device device, int outputIndex)
    {
        using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
        using SharpDX.DXGI.Adapter gameAdapter = dxgiDevice.Adapter;

        try
        {
            long luid = gameAdapter.Description.Luid;
            using var factory = new SharpDX.DXGI.Factory1();

            for (int i = 0; i < factory.GetAdapterCount1(); i++)
            {
                using SharpDX.DXGI.Adapter1 adapter = factory.GetAdapter1(i);
                if (adapter.Description1.Luid != luid || outputIndex >= adapter.GetOutputCount())
                    continue;

                return adapter.GetOutput(outputIndex);
            }
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(
                ex, "ScreenShare could not enumerate monitors from its own DXGI factory; using the game's adapter.");
        }

        return GetOutputOrThrow(gameAdapter, outputIndex);
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
                $"{ProbeEveryOutput(device, deviceName)} {ProbeObjectCombinations(device, deviceName)} " +
                "Sharing a window instead works regardless - it uses a different capture API.", ex);
        }
        catch (SharpDXException ex)
        {
            throw new InvalidOperationException(
                $"Windows refused to duplicate {deviceName}: {Describe(ex.ResultCode.Code)} " +
                $"(0x{ex.ResultCode.Code:X8}). {DescribeDevice(device)} " +
                $"{DescribeAdapterTopology(device, deviceName)} " +
                "Sharing a window instead works regardless - it uses a different capture API.", ex);
        }
    }

    private static string Describe(int hr) => hr switch
    {
        unchecked((int)0x887A0004) =>
            "the driver reported the capture unsupported in this process - most often exclusive " +
            "fullscreen, so switch the game to borderless windowed and try again",
        unchecked((int)0x887A0022) =>
            "another application already holds this monitor's duplication - only one at a time is allowed",
        unchecked((int)0x80070005) => "access was denied, which usually means protected content is on screen",
        unchecked((int)0x887A0026) => "the desktop changed underneath the call - try again",
        unchecked((int)0x887A0028) => "there is no interactive session to capture",
        _ => "the call failed",
    };

    private static string DescribeDevice(Device device)
    {
        try
        {
            using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
            using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;

            return $"Capture runs on {adapter.Description.Description.Trim()} " +
                   $"(LUID {adapter.Description.Luid}, feature level {device.FeatureLevel}).";
        }
        catch (Exception ex)
        {
            return $"(could not describe the capture device: {ex.Message})";
        }
    }

    // Two objects are under suspicion and every probe so far varied only one of them, so none could
    // tell them apart: the OUTPUT (enumerated from the adapter the game's D3D device handed us) and
    // the DEVICE. Testing all four combinations in one go is what makes the next log conclusive
    // instead of another round of narrowing.
    //
    // Measured outside the game, every one of these four duplicates happily on this hardware (see
    // tools/MediaProbe duplication), including output and device from different DXGI factories. So
    // whichever combinations fail here, the difference is something about this process.
    //
    //   own output + own device     fails => process-wide block; no object juggling will fix it.
    //   own output + game device    works => the game-derived OUTPUT is being rejected.
    //   game output + own device    works => the game-derived DEVICE is being rejected.
    //   all but "game+game" work    => capture must be built from our own factory throughout.
    private static string ProbeObjectCombinations(Device gameDevice, string deviceName)
    {
        SharpDX.DXGI.Factory1? factory = null;
        SharpDX.DXGI.Adapter1? ownAdapter = null;
        SharpDX.DXGI.Output? ownOutput = null;
        SharpDX.DXGI.Adapter? gameAdapter = null;
        SharpDX.DXGI.Output? gameOutput = null;
        Device? ownDevice = null;

        try
        {
            using (DxgiDevice dxgiDevice = gameDevice.QueryInterface<DxgiDevice>())
                gameAdapter = dxgiDevice.Adapter;

            long luid = gameAdapter.Description.Luid;
            gameOutput = FindOutput(gameAdapter, deviceName);

            factory = new SharpDX.DXGI.Factory1();
            for (int i = 0; i < factory.GetAdapterCount1() && ownAdapter is null; i++)
            {
                SharpDX.DXGI.Adapter1 candidate = factory.GetAdapter1(i);
                if (candidate.Description1.Luid == luid)
                    ownAdapter = candidate;
                else
                    candidate.Dispose();
            }

            if (ownAdapter is null)
                return "(our own DXGI factory does not report that GPU at all.)";

            ownOutput = FindOutput(ownAdapter, deviceName);
            if (ownOutput is null || gameOutput is null)
                return "(that monitor was not found on both adapter objects, so the combinations could not be compared.)";

            ownDevice = new Device(ownAdapter, DeviceCreationFlags.BgraSupport);

            return "Object combinations: " +
                   $"own output + own device = {TryPair(ownOutput, ownDevice)}; " +
                   $"own output + game device = {TryPair(ownOutput, gameDevice)}; " +
                   $"game output + own device = {TryPair(gameOutput, ownDevice)}; " +
                   $"game output + game device = {TryPair(gameOutput, gameDevice)}.";
        }
        catch (Exception ex)
        {
            return $"(could not compare object combinations: {ex.Message})";
        }
        finally
        {
            ownDevice?.Dispose();
            ownOutput?.Dispose();
            ownAdapter?.Dispose();
            gameOutput?.Dispose();
            gameAdapter?.Dispose();
            factory?.Dispose();
        }
    }

    private static SharpDX.DXGI.Output? FindOutput(SharpDX.DXGI.Adapter adapter, string deviceName)
    {
        for (int i = 0; i < adapter.GetOutputCount(); i++)
        {
            SharpDX.DXGI.Output output = adapter.GetOutput(i);
            if (output.Description.DeviceName == deviceName)
                return output;

            output.Dispose();
        }

        return null;
    }

    private static string TryPair(SharpDX.DXGI.Output output, Device device)
    {
        try
        {
            using Output1 output1 = output.QueryInterface<Output1>();
            using OutputDuplication probe = output1.DuplicateOutput(device);
            return "OK";
        }
        catch (SharpDXException ex)
        {
            return ex.Descriptor.ApiCode;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
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

    private static string ProbeWithFreshDevice(Adapter adapter, string targetDeviceName)
    {
        try
        {
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

                    if (ownsCaptureDevice)
                    {
                        // Our own device, and this thread is its only user, so the plugin's context
                        // lock does not apply. The Flush is not optional: targetTexture is a classic
                        // shared resource read by a DIFFERENT device, and without it the copy can sit
                        // in the driver's command buffer, invisible over there until something
                        // unrelated happens to flush - the same failure the decoder had.
                        captureDevice.ImmediateContext.CopyResource(frameTexture, targetTexture);
                        captureDevice.ImmediateContext.Flush();
                    }
                    else
                    {
                        lock (contextLock)
                            device.ImmediateContext.CopyResource(frameTexture, targetTexture);
                    }

                    Interlocked.Increment(ref framesCaptured);
                }
                finally
                {
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
                using Adapter adapter = dxgiDevice.Adapter;
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
        bool captureExited = captureThread.Join(1000);

        // Released first and unconditionally, before anything it was created from. Leaking a
        // duplication blocks every future capture of this monitor until the game restarts, which is
        // a worse outcome than any of the risks below. Under the same lock the capture thread swaps
        // it under, so a teardown landing in the middle of an AccessLost recovery disposes the
        // replacement rather than the object the recovery already threw away.
        lock (duplicationLock)
        {
            duplication.Dispose();
        }

        // Same reasoning as H264Encoder.Dispose: the join is bounded so teardown cannot hang, which
        // means it is not proof the capture thread has stopped touching these. It copies frames on
        // captureDevice's context, so releasing that device out from under a thread still inside a
        // GPU call crashes natively rather than throwing. Leaking them in that rare case is better.
        if (!captureExited)
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare's monitor capture thread didn't exit within 1s - leaking its device and " +
                "textures rather than risking a use-after-dispose crash.");
            return;
        }

        if (!ReferenceEquals(sharedView, targetTexture))
            sharedView.Dispose();

        targetTexture.Dispose();

        // Last: the duplication and both textures came from it.
        if (ownsCaptureDevice)
            captureDevice.Dispose();
    }
}
