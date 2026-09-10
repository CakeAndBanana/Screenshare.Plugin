using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

public static class SharedTextureBridge
{
    public static RawTextureWrap OpenOnGameDevice(Device gameDevice, nint sharedHandle, int width, int height)
    {
        using Texture2D gameSideTexture = gameDevice.OpenSharedResource<Texture2D>(sharedHandle);
        using ShaderResourceView srv = new(gameDevice, gameSideTexture);

        Marshal.AddRef(srv.NativePointer);
        return new RawTextureWrap(srv.NativePointer, width, height);
    }
}
