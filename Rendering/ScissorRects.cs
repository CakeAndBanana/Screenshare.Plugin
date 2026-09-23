using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;

namespace ScreenShare.Plugin.Rendering;

internal unsafe struct ScissorRects
{
    private const int MaxRects = 16;

    private const int RSSetScissorRectsSlot = 45;
    private const int RSGetScissorRectsSlot = 96;

    private fixed int rects[MaxRects * 4];
    private uint count;

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

    public void Restore(DeviceContext context)
    {
        var set = (delegate* unmanaged[Stdcall]<nint, uint, RawRectangle*, void>)VTable(context)[RSSetScissorRectsSlot];

        fixed (int* data = rects)
            set(context.NativePointer, count, count > 0 ? (RawRectangle*)data : null);
    }

    private static void** VTable(DeviceContext context) => *(void***)context.NativePointer;
}
