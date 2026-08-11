using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Creates D3D11 textures on our own device with the Shared option so their handle can be
/// opened on the game's device via <see cref="SharedTextureBridge"/>.
/// </summary>
public static class SharedTextureFactory
{
    public static Texture2D CreateEmpty(Device device, int width, int height, Format format = Format.B8G8R8A8_UNorm)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.Shared,
        };

        return new Texture2D(device, desc);
    }

    /// <summary>A plain (non-shared) render-target-capable texture - used as the destination of
    /// a GPU-side downscale blit, never opened on another device, so it doesn't need the Shared
    /// option flag CreateEmpty's textures carry.</summary>
    public static Texture2D CreateRenderTarget(Device device, int width, int height, Format format = Format.B8G8R8A8_UNorm)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        };

        return new Texture2D(device, desc);
    }

    public static nint GetSharedHandle(Texture2D texture)
    {
        using SharpDX.DXGI.Resource dxgiResource = texture.QueryInterface<SharpDX.DXGI.Resource>();
        return dxgiResource.SharedHandle;
    }
}
