using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

public static class SolidColorTexture
{
    public static RawTextureWrap Create(Device device, uint bgra, int reportedWidth, int reportedHeight)
    {
        var desc = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        };

        GCHandle pixel = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using Texture2D texture = new(device, desc, new DataRectangle(pixel.AddrOfPinnedObject(), sizeof(uint)));
            using ShaderResourceView srv = new(device, texture);

            Marshal.AddRef(srv.NativePointer);
            return new RawTextureWrap(srv.NativePointer, reportedWidth, reportedHeight);
        }
        finally
        {
            pixel.Free();
        }
    }
}
