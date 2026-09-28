using System.Runtime.InteropServices;
using Dalamud.Interface;
using SharpDX.Direct3D11;
using DxgiDevice = SharpDX.DXGI.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// GameAdapterDevice is a wrapper around a Direct3D11 device that is created from the game's D3D11 device.
/// It allows for rendering to the game's swap chain and provides a lock for thread-safe access to the immediate context.
/// </summary>
public sealed class GameAdapterDevice : IDisposable
{
    public Device Device { get; }

    public Device GameDevice { get; }

    public Lock ImmediateContextLock { get; } = new();

    /// <summary>
    /// Creates a new instance of the GameAdapterDevice class using the provided IUiBuilder to obtain the game's D3D11 device handle.
    /// </summary>
    /// <param name="uiBuilder">The UI builder instance.</param>
    /// <exception cref="InvalidOperationException">Thrown when the game's D3D11 device is not available.</exception>
    public GameAdapterDevice(IUiBuilder uiBuilder)
    {
        // Get the game's D3D11 device handle from the UI builder. If it's not available, throw an exception.
        nint gameDeviceHandle = uiBuilder.DeviceHandle;
        if (gameDeviceHandle == nint.Zero)
            throw new InvalidOperationException("Game D3D11 device is not available yet.");

        // Add a reference to the game's D3D11 device to ensure it is not released while this instance is using it.
        Marshal.AddRef(gameDeviceHandle);
        GameDevice = new Device(gameDeviceHandle);

        // Query the DXGI device and adapter from the game's D3D11 device to create a new Direct3D11 device with the same adapter.
        using DxgiDevice dxgiDevice = GameDevice.QueryInterface<DxgiDevice>();
        using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;

        // Create a new Direct3D11 device with the same adapter, enabling BGRA support and video support.
        Device = new Device(adapter, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, GameDevice.FeatureLevel);

        using var multithread = Device.QueryInterface<Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    /// <summary>
    /// Disposes the GameAdapterDevice and releases the underlying Direct3D11 devices.
    /// </summary>
    public void Dispose()
    {
        Device.Dispose();
        GameDevice.Dispose();
    }
}
