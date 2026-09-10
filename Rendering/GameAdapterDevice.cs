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

        Marshal.AddRef(gameDeviceHandle);
        GameDevice = new Device(gameDeviceHandle);

        using DxgiDevice dxgiDevice = GameDevice.QueryInterface<DxgiDevice>();
        using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;

        Device = new Device(adapter, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, GameDevice.FeatureLevel);

        using var multithread = Device.QueryInterface<Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    public void Dispose()
    {
        Device.Dispose();
        GameDevice.Dispose();
    }
}
