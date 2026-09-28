using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// SolidColorTexture is a utility class that creates a 1x1 texture filled with a specified solid color. 
/// This texture can be used in rendering scenarios where a uniform color is needed, such as for background fills or simple color overlays.
/// </summary>
public static class SolidColorTexture
{
    public static RawTextureWrap Create(Device device, uint bgra, int reportedWidth, int reportedHeight)
    {
        // Generates an 1x1 black texture to show on the preview of the in-game screen
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
