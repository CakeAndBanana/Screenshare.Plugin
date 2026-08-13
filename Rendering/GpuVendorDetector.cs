using Device = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;

namespace ScreenShare.Plugin.Rendering;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

public static class GpuVendorDetector
{
    // Standard PCI vendor IDs.
    private const int NvidiaVendorId = 0x10DE;
    private const int AmdVendorId = 0x1002;
    private const int IntelVendorId = 0x8086;

    public static GpuVendor Detect(Device device)
    {
        using DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>();
        using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;

        return adapter.Description.VendorId switch
        {
            NvidiaVendorId => GpuVendor.Nvidia,
            AmdVendorId => GpuVendor.Amd,
            IntelVendorId => GpuVendor.Intel,
            _ => GpuVendor.Unknown,
        };
    }
}
