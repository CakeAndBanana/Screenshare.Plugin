using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;

namespace ScreenShare.Plugin.Capture.ScreenCapture;

/// <summary>
/// Desktop duplication capture source that captures the desktop using the Desktop Duplication API.
/// </summary>
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

    /// <summary>
    /// Creates a new instance of the DesktopDuplicationCapture class.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device to use for capture.</param>
    /// <param name="outputIndex">The index of the output to capture.</param>
    public DesktopDuplicationCapture(GameAdapterDevice gameAdapterDevice, int outputIndex = 0)
    {
        // initialize the capture device and context lock from the game adapter device
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

        // Register this capture instance with the duplication registry to ensure only one capture is active at a time
        (registrationId, stopRequested) = DuplicationRegistry.Register();

        (captureDevice, ownsCaptureDevice) = ResolveCaptureDevice(device);

        using Output output = ResolveOutput(device, outputIndex);
        using Output1 output1 = output.QueryInterface<Output1>();

        var bounds = output.Description.DesktopBounds;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        duplication = DuplicateOrThrow(output1, captureDevice, output.Description.DeviceName);

        // Create the target texture and shared view for capturing the desktop
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

    /// <summary>
    /// Resolves the capture device to use for desktop duplication. 
    /// If the plugin's device is not suitable, it creates a new device on the same adapter.
    /// </summary>
    /// <param name="pluginDevice">The plugin's device to use for capture.</param>
    /// <returns>A tuple containing the capture device and a boolean indicating whether it was owned.</returns>
    private static (Device Device, bool Owned) ResolveCaptureDevice(Device pluginDevice)
    {
        // Attempt to create a new device on the same adapter as the plugin's device to avoid potential conflicts with the game's rendering context
        try
        {
            long luid;
            using (DxgiDevice dxgiDevice = pluginDevice.QueryInterface<DxgiDevice>())
            using (Adapter adapter = dxgiDevice.Adapter)
                luid = adapter.Description.Luid;

            // Create a new device on the same adapter as the plugin's device
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
                ex, "ScreenShare could not create its own capture device!");
        }

        return (pluginDevice, false);
    }

    /// <summary>
    /// Resolves the output to capture based on the provided device and output index. 
    /// It attempts to find the output on the same adapter as the plugin's device, and if not found, it falls back to using the game's adapter.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="outputIndex"></param>
    /// <returns></returns>
    private static Output ResolveOutput(Device device, int outputIndex)
    {
        // Attempt to find the output on the same adapter as the plugin's device
        using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
        using Adapter gameAdapter = dxgiDevice.Adapter;

        try
        {
            // Get the LUID of the game adapter to find the corresponding adapter in the plugin's factory
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

    /// <summary>
    /// Gets output from the adapter or throws an InvalidOperationException if the output is not found.
    /// </summary>
    /// <param name="adapter">The adapter from which to get the output.</param>
    /// <param name="outputIndex">The index of the output to get.</param>
    /// <returns>The output at the specified index.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the output is not found.</exception>
    private static Output GetOutputOrThrow(Adapter adapter, int outputIndex)
    {
        // Ensure the adapter is not null before proceeding
        ArgumentNullException.ThrowIfNull(adapter);
        try
        {
            // Attempt to get the output at the specified index from the adapter
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

    /// <summary>
    /// Duplicates the output for the specified device, or throws an InvalidOperationException with detailed information if duplication fails.
    /// </summary>
    /// <param name="output1">The output to duplicate.</param>
    /// <param name="device">The device for which to duplicate the output.</param>
    /// <param name="deviceName">The name of the device.</param>
    /// <returns>The duplicated output.</returns>
    /// <exception cref="InvalidOperationException">Thrown when duplication fails.</exception>
    private static OutputDuplication DuplicateOrThrow(Output1 output1, Device device, string deviceName)
    {
        try
        {
            // Attempt to duplicate the output for the specified device
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

    /// <summary>
    /// Descibes the error code returned by the duplication API, providing a human-readable explanation of the failure.
    /// </summary>
    /// <param name="hr">The error code to describe.</param>
    /// <returns>A human-readable explanation of the error.</returns>
    private static string Describe(int hr) => hr switch
    {
        unchecked((int)0x887A0004) =>
            "the driver reported the capture unsupported in this process",
        unchecked((int)0x887A0022) =>
            "another application already holds this monitor's duplication",
        unchecked((int)0x80070005) => "access was denied, which usually means protected content is on screen",
        unchecked((int)0x887A0026) => "the desktop changed underneath the call",
        unchecked((int)0x887A0028) => "there is no interactive session to capture",
        _ => "the call failed",
    };

    /// <summary>
    /// Describes the device being used for capture, including its adapter description and feature level.
    /// </summary>
    /// <param name="device">The device to describe.</param>
    /// <returns>A human-readable description of the device.</returns>
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

    /// <summary>
    /// Probes the combinations of outputs and devices to determine if any pairing can successfully duplicate the output, providing detailed information about the results.
    /// </summary>
    /// <param name="gameDevice">The game device to probe.</param>
    /// <param name="deviceName">The name of the device to probe.</param>
    /// <returns>A string describing the results of the probing.</returns>
    private static string ProbeObjectCombinations(Device gameDevice, string deviceName)
    {
        // Attempt to find the output on the same adapter as the game device, and then probe various combinations of outputs and devices to determine if any pairing can successfully duplicate the output.
        Factory1? factory = null;
        Adapter1? ownAdapter = null;
        Output? ownOutput = null;
        Adapter? gameAdapter = null;
        Output? gameOutput = null;
        Device? ownDevice = null;

        try
        {
            // Get the adapter for the game device
            using (DxgiDevice dxgiDevice = gameDevice.QueryInterface<DxgiDevice>())
                gameAdapter = dxgiDevice.Adapter;

            // Get the LUID of the game adapter to find the corresponding adapter in the plugin's factory
            long luid = gameAdapter.Description.Luid;
            gameOutput = FindOutput(gameAdapter, deviceName);

            // If the game output is not found, return an error message
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

            // Find the output on the same adapter as the plugin's device
            ownOutput = FindOutput(ownAdapter, deviceName);
            if (ownOutput is null || gameOutput is null)
                return "(that monitor was not found on both adapter objects, so the combinations could not be compared.)";

            // Create a new device on the same adapter as the plugin's device to test duplication
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
            // Dispose of all the resources that were created for probing
            ownDevice?.Dispose();
            ownOutput?.Dispose();
            ownAdapter?.Dispose();
            gameOutput?.Dispose();
            gameAdapter?.Dispose();
            factory?.Dispose();
        }
    }

    /// <summary>
    /// Finds the output on the specified adapter that matches the given device name.
    /// Returns null if not found.
    /// </summary>
    /// <param name="adapter">The adapter to search for the output.</param>
    /// <param name="deviceName">The name of the device to find.</param>
    /// <returns>The output if found, otherwise null.</returns>
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

    /// <summary>
    /// Tries to pair the specified output with the specified device for duplication, returning a string indicating success or the error code if it fails.
    /// </summary>
    /// <param name="output">The output to pair.</param>
    /// <param name="device">The device to pair with.</param>
    /// <returns>A string indicating the result of the pairing attempt.</returns>
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

    /// <summary>
    /// Describes the adapter topology for the specified device and device name, providing information about which GPU owns the monitor and whether the capture device is on the same GPU.
    /// </summary>
    /// <param name="device">The device for which to describe the topology.</param>
    /// <param name="deviceName">The name of the device to describe.</param>
    /// <returns>A string describing the adapter topology.</returns>
    private static string DescribeAdapterTopology(Device device, string deviceName)
    {
        try
        {
            // Get the LUID and name of the adapter that owns the capture device
            using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
            using Adapter ownAdapter = dxgiDevice.Adapter;
            long ownLuid = ownAdapter.Description.Luid;
            string ownName = ownAdapter.Description.Description;

            // Iterate through all adapters in the factory to find which one owns the specified device name
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
                               $"runs on {ownName} (the GPU the game renders on).";
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

    /// <summary>
    /// Probes every output on the adapter for the specified device to determine if any output can be successfully duplicated, providing detailed information about the results.
    /// </summary>
    /// <param name="device">The device for which to probe outputs.</param>
    /// <param name="targetDeviceName">The name of the device to probe.</param>
    /// <returns>A string describing the results of the probing.</returns>
    private static string ProbeEveryOutput(Device device, string targetDeviceName)
    {
        try
        {
            // Get the adapter for the specified device
            using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
            using Adapter adapter = dxgiDevice.Adapter;

            var results = new List<string>();
            string attachment = "unknown";

            // Iterate through all outputs on the adapter and attempt to duplicate each one, recording the results
            foreach (Output output in adapter.Outputs)
            {
                using (output)
                {
                    // Check if the output matches the target device name and record whether it is attached to the desktop
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

    /// <summary>
    /// Probes the specified adapter with a fresh device to determine if duplication can be successfully performed, providing detailed information about the results.
    /// </summary>
    /// <param name="adapter">The adapter to probe.</param>
    /// <param name="targetDeviceName">The name of the device to probe.</param>
    /// <returns>A string describing the results of the probing.</returns>
    private static string ProbeWithFreshDevice(Adapter adapter, string targetDeviceName)
    {
        try
        {
            using var freshDevice = new Device(adapter, DeviceCreationFlags.BgraSupport);

            // Iterate through all outputs on the adapter and attempt to duplicate the target output with the fresh device, returning the result
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

    /// <summary>
    /// Captures frames from the desktop duplication API in a loop until stopped, copying them to the target texture and incrementing the frame count.
    /// </summary>
    private void CaptureLoop()
    {
        // Capture frames from the desktop duplication API in a loop until stopped, copying them to the target texture and incrementing the frame count.
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

    /// <summary>
    /// Tries to recreate the desktop duplication object if it has been lost, returning true if successful and false otherwise.
    /// </summary>
    /// <returns></returns>
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

    /// <summary>
    /// Disposes of the DesktopDuplicationCapture instance, stopping the capture thread and releasing all resources.
    /// </summary>
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
                "ScreenShare's monitor capture thread didn't exit within 1s.");
            return;
        }

        if (!ReferenceEquals(sharedView, targetTexture))
            sharedView.Dispose();

        targetTexture.Dispose();

        if (ownsCaptureDevice)
            captureDevice.Dispose();
    }
}
