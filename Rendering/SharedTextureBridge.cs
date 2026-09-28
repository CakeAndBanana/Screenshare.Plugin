using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// SharedTextureBridge provides a method to open a shared texture on the game device and return a RawTextureWrap that can be used for rendering.
/// </summary>
public static class SharedTextureBridge
{
    /// <summary>
    /// Opens a shared texture on the game device and returns a RawTextureWrap that can be used for rendering.
    /// </summary>
    /// <param name="gameDevice">The game device on which to open the shared texture.</param>
    /// <param name="sharedHandle">The handle to the shared texture.</param>
    /// <param name="width">The width of the texture.</param>
    /// <param name="height">The height of the texture.</param>
    /// <returns>A RawTextureWrap that can be used for rendering.</returns>
    public static RawTextureWrap OpenOnGameDevice(Device gameDevice, nint sharedHandle, int width, int height)
    {
        // Open the shared texture on the game device and create a ShaderResourceView for it
        using Texture2D gameSideTexture = gameDevice.OpenSharedResource<Texture2D>(sharedHandle);
        using ShaderResourceView srv = new(gameDevice, gameSideTexture);

        // Increment the reference count of the ShaderResourceView to ensure it remains valid
        Marshal.AddRef(srv.NativePointer);
        return new RawTextureWrap(srv.NativePointer, width, height);
    }
}
