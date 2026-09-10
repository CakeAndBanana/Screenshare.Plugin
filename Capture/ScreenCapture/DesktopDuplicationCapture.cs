using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
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

    private readonly Guid registrationId;
    private readonly System.Runtime.CompilerServices.StrongBox<bool> stopRequested;

    public DesktopDuplicationCapture(GameAdapterDevice gameAdapterDevice, int outputIndex = 0)
    {
        device = gameAdapterDevice.Device;
        contextLock = gameAdapterDevice.ImmediateContextLock;
        this.outputIndex = outputIndex;

        int stranded = DuplicationRegistry.RequestStopAll();
        if (stranded > 0)
        {
            ServiceCollector.PluginLog.Information(
                $"ScreenShare asked {stranded} capture(s) left by a previous plugin load to stop before duplicating.");

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
            using (Adapter adapter = dxgiDevice.Adapter)
                luid = adapter.Description.Luid;

            using var factory = new Factory1();
            for (int i = 0; i < factory.GetAdapterCount1(); i++)
            {
                using Adapter1 adapter = factory.GetAdapter1(i);
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
        using Adapter gameAdapter = dxgiDevice.Adapter;

        try
        {
            long luid = gameAdapter.Description.Luid;
            using var factory = new Factory1();

            for (int i = 0; i < factory.GetAdapterCount1(); i++)
            {
                using var adapter = factory.GetAdapter1(i);
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

    private static Output GetOutputOrThrow(Adapter adapter, int outputIndex)
    {
        ArgumentNullException.ThrowIfNull(adapter);
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
            using Adapter adapter = dxgiDevice.Adapter;

            return $"Capture runs on {adapter.Description.Description.Trim()} " +
                   $"(LUID {adapter.Description.Luid}, feature level {device.FeatureLevel}).";
        }
        catch (Exception ex)
        {
            return $"(could not describe the capture device: {ex.Message})";
        }
    }

    private static string ProbeObjectCombinations(Device gameDevice, string deviceName)
    {
        Factory1? factory = null;
        Adapter1? ownAdapter = null;
        Output? ownOutput = null;
        Adapter? gameAdapter = null;
        Output? gameOutput = null;
        Device? ownDevice = null;

        try
        {
            using (DxgiDevice dxgiDevice = gameDevice.QueryInterface<DxgiDevice>())
                gameAdapter = dxgiDevice.Adapter;

            long luid = gameAdapter.Description.Luid;
            gameOutput = FindOutput(gameAdapter, deviceName);

            factory = new Factory1();
            for (int i = 0; i < factory.GetAdapterCount1() && ownAdapter is null; i++)
            {
                Adapter1 candidate = factory.GetAdapter1(i);
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

    private static Output? FindOutput(Adapter adapter, string deviceName)
    {
        for (int i = 0; i < adapter.GetOutputCount(); i++)
        {
            Output output = adapter.GetOutput(i);
            if (output.Description.DeviceName == deviceName)
                return output;

            output.Dispose();
        }

        return null;
    }

    private static string TryPair(Output output, Device device)
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
            using Adapter ownAdapter = dxgiDevice.Adapter;
            long ownLuid = ownAdapter.Description.Luid;
            string ownName = ownAdapter.Description.Description;

            using var factory = new Factory1();
            for (int i = 0; i < factory.GetAdapterCount1(); i++)
            {
                using Adapter1 adapter = factory.GetAdapter1(i);
                foreach (Output output in adapter.Outputs)
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
            return $"(could not determine which GPU owns it: {ex.Message})";
        }
    }

    private static string ProbeEveryOutput(Device device, string targetDeviceName)
    {
        try
        {
            using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
            using Adapter adapter = dxgiDevice.Adapter;

            var results = new List<string>();
            string attachment = "unknown";

            foreach (Output output in adapter.Outputs)
            {
                using (output)
                {
                    OutputDescription description = output.Description;
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

        lock (duplicationLock)
        {
            duplication.Dispose();
        }

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

        if (ownsCaptureDevice)
            captureDevice.Dispose();
    }
}
