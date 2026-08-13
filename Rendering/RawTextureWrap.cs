using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Wraps a raw ID3D11ShaderResourceView* as an IDalamudTextureWrap so it can be drawn with
/// ImGui.Image(). Owns one COM reference to the view for the lifetime of this instance.
/// </summary>
public sealed class RawTextureWrap(nint shaderResourceView, int width, int height) : IDalamudTextureWrap
{
    private readonly nint handle = shaderResourceView;
    private bool disposed;

    public ImTextureID Handle => new(handle);

    public int Width { get; } = width;

    public int Height { get; } = height;

    public Vector2 Size => new(Width, Height);

    public IDalamudTextureWrap CreateWrapSharingLowLevelResource()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Marshal.AddRef(handle);
        return new RawTextureWrap(handle, Width, Height);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Marshal.Release(handle);
    }
}
