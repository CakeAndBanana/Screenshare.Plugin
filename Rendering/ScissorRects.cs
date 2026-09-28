using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// ScissorRects is a structure that captures and restores the scissor rectangles state of a Direct3D 11 device context. 
/// It allows saving the current scissor rectangles and restoring them later, which is useful for managing rendering states in graphics applications.
/// </summary>
internal unsafe struct ScissorRects
{
    private const int MaxRects = 16;

    private const int RSSetScissorRectsSlot = 45;
    private const int RSGetScissorRectsSlot = 96;

    private fixed int rects[MaxRects * 4];
    private uint count;

    /// <summary>
    /// Captures the current scissor rectangles from the specified device context and returns a ScissorRects structure containing the captured state.
    /// </summary>
    /// <param name="context">The device context from which to capture scissor rectangles.</param>
    /// <returns>A ScissorRects structure containing the captured state.</returns>
    public static ScissorRects Capture(DeviceContext context)
    {
        var saved = default(ScissorRects);
        var get = (delegate* unmanaged[Stdcall]<nint, uint*, RawRectangle*, void>)VTable(context)[RSGetScissorRectsSlot];

        uint count = 0;
        get(context.NativePointer, &count, null);
        count = Math.Min(count, MaxRects);

        if (count > 0)
            get(context.NativePointer, &count, (RawRectangle*)saved.rects);

        saved.count = count;
        return saved;
    }

    /// <summary>
    /// Restores the scissor rectangles state to the specified device context using the captured state in this ScissorRects structure.
    /// </summary>
    /// <param name="context">The device context to which to restore the scissor rectangles state.</param>
    public void Restore(DeviceContext context)
    {
        var set = (delegate* unmanaged[Stdcall]<nint, uint, RawRectangle*, void>)VTable(context)[RSSetScissorRectsSlot];

        fixed (int* data = rects)
            set(context.NativePointer, count, count > 0 ? (RawRectangle*)data : null);
    }

    /// <summary>
    /// VTable returns a pointer to the virtual function table (vtable) of the specified device context. 
    /// This is used to access the methods for getting and setting scissor rectangles.
    /// </summary>
    /// <param name="context">The device context for which to retrieve the vtable.</param>
    /// <returns>A pointer to the vtable.</returns>
    private static void** VTable(DeviceContext context) => *(void***)context.NativePointer;
}
