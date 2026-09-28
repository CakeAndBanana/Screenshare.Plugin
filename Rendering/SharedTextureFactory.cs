using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.DXGI.Resource;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// shared texture factory for creating Direct3D 11 textures that can be shared between processes
/// </summary>
public static class SharedTextureFactory
{
    /// <summary>
    /// Creates an empty Direct3D 11 texture with the specified width, height, format, and render target option. The texture is created with shared access, allowing it to be shared between processes.
    /// </summary>
    /// <param name="device">The Direct3D 11 device on which to create the texture.</param>
    /// <param name="width">The width of the texture.</param>
    /// <param name="height">The height of the texture.</param>
    /// <param name="format">The format of the texture.</param>
    /// <param name="renderTarget">Indicates whether the texture is a render target.</param>
    /// <returns>The created Texture2D instance.</returns>
    public static Texture2D CreateEmpty(
        Device device, int width, int height, Format format = Format.B8G8R8A8_UNorm, bool renderTarget = false)
    {
        // Create a texture description with the specified parameters
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = renderTarget ? BindFlags.ShaderResource | BindFlags.RenderTarget : BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.Shared,
        };

        return new Texture2D(device, desc);
    }

    /// <summary>
    /// Creates a Direct3D 11 texture that can be used as a render target with the specified width, height, and format. The texture is created with shared access, allowing it to be shared between processes.
    /// </summary>
    /// <param name="device">The Direct3D 11 device on which to create the texture.</param>
    /// <param name="width">The width of the texture.</param>
    /// <param name="height">The height of the texture.</param>
    /// <param name="format">The format of the texture.</param>
    /// <returns>The created Texture2D instance.</returns>
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

    /// <summary>
    /// Gets the shared handle of a Direct3D 11 texture, which can be used to share the texture between processes. The shared handle is obtained by querying the texture for the DXGI resource interface and retrieving its shared handle.
    /// </summary>
    /// <param name="texture"></param>
    /// <returns></returns>
    public static nint GetSharedHandle(Texture2D texture)
    {
        using Resource dxgiResource = texture.QueryInterface<Resource>();
        return dxgiResource.SharedHandle;
    }
}
