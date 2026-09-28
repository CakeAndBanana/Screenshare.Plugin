using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Represents a raw texture wrap that wraps a low-level shader resource view handle. 
/// This class implements the IDalamudTextureWrap interface and provides functionality for managing the lifetime of the underlying resource.
/// </summary>
/// <param name="shaderResourceView"></param>
/// <param name="width"></param>
/// <param name="height"></param>
public sealed class RawTextureWrap(nint shaderResourceView, int width, int height) : IDalamudTextureWrap
{
    private readonly nint handle = shaderResourceView;
    private bool disposed;

    public ImTextureID Handle => new(handle);

    public int Width { get; } = width;

    public int Height { get; } = height;

    public Vector2 Size => new(Width, Height);

    /// <summary>
    /// Creates a new IDalamudTextureWrap that shares the underlying low-level resource.
    /// This method increments the reference count of the shader resource view handle to ensure that the resource remains valid while the new wrap is in use.
    /// </summary>
    /// <returns></returns>
    public IDalamudTextureWrap CreateWrapSharingLowLevelResource()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Marshal.AddRef(handle);
        return new RawTextureWrap(handle, Width, Height);
    }

    /// <summary>
    /// Disposes the RawTextureWrap instance and releases the underlying low-level resource.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Marshal.Release(handle);
    }
}
