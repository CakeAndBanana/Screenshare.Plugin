using System.Runtime.InteropServices;
using Dalamud.Interface;
using SharpDX.Direct3D11;
using DxgiDevice = SharpDX.DXGI.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Exposes two devices: <see cref="Device"/> is our own D3D11 device created on the same
/// adapter as the game, safe to use off the game's render thread for capture/encode work.
/// <see cref="GameDevice"/> is a ref-counted handle to the game's actual device, needed to open
/// shared textures so their SRVs are valid for the game's own renderer to bind - and, since the
/// in-world screen landed, to issue our own draw calls during ImGui's render pass (see
/// WorldPanelRenderer). Those draws happen on the game's immediate context from the render thread
/// only, which is why they need no equivalent of <see cref="ImmediateContextLock"/>: the render
/// thread is already the only thread touching that context.
/// </summary>
public sealed class GameAdapterDevice : IDisposable
{
    public Device Device { get; }

    public Device GameDevice { get; }

    /// <summary>D3D11 immediate contexts are not safe to call concurrently from multiple
    /// threads. Every consumer that issues calls against <see cref="Device"/>'s ImmediateContext
    /// from a background thread (capture loops, texture readback, frame uploads) must take this
    /// lock first - it is the single shared guard for the whole ImmediateContext, not per-resource.</summary>
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

        Device = new Device(adapter, DeviceCreationFlags.BgraSupport, GameDevice.FeatureLevel);
    }

    public void Dispose()
    {
        Device.Dispose();
        GameDevice.Dispose();
    }
}
