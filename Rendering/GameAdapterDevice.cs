using System.Runtime.InteropServices;
using Dalamud.Interface;
using SharpDX.Direct3D11;
using DxgiDevice = SharpDX.DXGI.Device;

namespace ScreenShare.Plugin.Rendering;

public sealed class GameAdapterDevice : IDisposable
{
    public Device Device { get; }

    public Device GameDevice { get; }

    public Lock ImmediateContextLock { get; } = new();

    public GameAdapterDevice(IUiBuilder uiBuilder)
    {
        nint gameDeviceHandle = uiBuilder.DeviceHandle;
        if (gameDeviceHandle == nint.Zero)
            throw new InvalidOperationException("Game D3D11 device is not available yet.");

        // DeviceHandle is a borrowed reference (not AddRef'd for us), so add our own ref before
        // wrapping it; SharpDX's Dispose() on GameDevice below balances this.
        Marshal.AddRef(gameDeviceHandle);
        GameDevice = new Device(gameDeviceHandle);

        using DxgiDevice dxgiDevice = GameDevice.QueryInterface<DxgiDevice>();
        using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;

        // VideoSupport is required before Media Foundation will accept this device through an
        // IMFDXGIDeviceManager, which is what lets a hardware encoder MFT take our textures
        // directly instead of us reading every frame back to the CPU first.
        Device = new Device(adapter, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, GameDevice.FeatureLevel);

        // Not optional once MF is in the picture. MF drives this device from its own worker
        // threads, in parallel with the capture loop and the encoder, and D3D11 devices are only
        // safe under that if multithread protection is switched on. Without it the failure is
        // sporadic corruption rather than a clean error, which is the worst kind to debug - and it
        // would look like a bug in whichever innocent code happened to be running at the time.
        using (var multithread = Device.QueryInterface<Multithread>())
            multithread.SetMultithreadProtected(true);
    }

    public void Dispose()
    {
        Device.Dispose();
        GameDevice.Dispose();
    }
}
