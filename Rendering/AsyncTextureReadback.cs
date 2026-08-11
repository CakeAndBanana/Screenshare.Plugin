using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Reads a GPU texture back to CPU memory without ever blocking the calling thread.
///
/// The earlier <see cref="TextureReadback"/> did CopyResource immediately followed by
/// Map(MapMode.Read), which forces the CPU to wait for the GPU to finish ALL pending work up to
/// that copy - a real, synchronous stall. Doing this repeatedly from a background thread, on a
/// large texture, while the game is also driving that same GPU continuously, is the leading
/// theory for a DXGI_ERROR_DEVICE_REMOVED (Windows TDR) crash hit during testing.
///
/// This version fixes it two ways: (1) a ring of staging textures (depth 3 by default), so each
/// Map() targets a copy queued multiple ticks ago rather than the one just issued, making a wait
/// rarely necessary in the first place; (2) MapFlags.DoNotWait, which makes the Map itself
/// incapable of blocking at all - if the GPU genuinely isn't done yet, it throws WasStillDrawing
/// instead of stalling, and that tick is just skipped (dropping a frame is fine for live video;
/// freezing the game is not).
///
/// Depth was bumped from 2 to 3 after real network streaming testing (game + relay + audio
/// encode + WS I/O all running at once) showed a ~47% skip rate, well above the ~10-30% seen in
/// the earlier local-only round-trip test - the extra tick of margin trades a bit more latency
/// for headroom against that additional system-wide contention. The real fix (downscaling on the
/// GPU before readback, so the transfer size matches the chosen output resolution instead of the
/// full native screen) is still the documented follow-up if this isn't enough on its own.
/// </summary>
public sealed class AsyncTextureReadback : IDisposable
{
    private const int BufferDepth = 3;

    private readonly Device device;
    private readonly Texture2D[] stagingTextures;
    private int writeIndex;
    private int framesQueued;

    public int Width { get; }

    public int Height { get; }

    public AsyncTextureReadback(Device device, int width, int height)
    {
        this.device = device;
        Width = width;
        Height = height;
        stagingTextures = new Texture2D[BufferDepth];
        for (int i = 0; i < BufferDepth; i++)
            stagingTextures[i] = CreateStaging(device, width, height);
    }

    private static Texture2D CreateStaging(Device device, int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        };

        return new Texture2D(device, desc);
    }

    /// <summary>Queues a copy of <paramref name="source"/> for this tick, and - if a copy queued
    /// BufferDepth-1 ticks ago is ready - fills destination with it and returns true. Never
    /// blocks; a false return just means try again next tick. Caller must ensure no other thread
    /// touches this device's ImmediateContext concurrently.</summary>
    public bool TryReadPrevious(Texture2D source, byte[] destination)
    {
        device.ImmediateContext.CopyResource(source, stagingTextures[writeIndex]);

        // The slot BufferDepth-1 ticks older than the one just written is exactly the slot that
        // will be overwritten NEXT tick - i.e. the next index forward in the ring.
        int readIndex = (writeIndex + 1) % stagingTextures.Length;
        bool gotFrame = framesQueued >= stagingTextures.Length && TryMapAndCopy(stagingTextures[readIndex], destination);

        if (framesQueued < stagingTextures.Length)
            framesQueued++;
        writeIndex = readIndex;
        return gotFrame;
    }

    private bool TryMapAndCopy(Texture2D staging, byte[] destination)
    {
        DataBox box;
        try
        {
            box = device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.DoNotWait);
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.WasStillDrawing)
        {
            return false;
        }

        // SharpDX's Resource-typed MapSubresource overload special-cases DoNotWait +
        // WasStillDrawing: rather than throwing (which the catch above is for), it returns an
        // empty DataBox (DataPointer == IntPtr.Zero) instead - confirmed by decompiling
        // SharpDX.Direct3D11.DeviceContext. That means the exception path above never actually
        // fires for this overload, and without this check a "GPU isn't done yet" tick would
        // Marshal.Copy from a null pointer instead of just being skipped like it should be.
        if (box.IsEmpty)
            return false;

        try
        {
            int rowBytes = Width * 4;
            if (box.RowPitch == rowBytes)
            {
                Marshal.Copy(box.DataPointer, destination, 0, rowBytes * Height);
            }
            else
            {
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(box.DataPointer + y * box.RowPitch, destination, y * rowBytes, rowBytes);
            }

            return true;
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(staging, 0);
        }
    }

    public void Dispose()
    {
        foreach (Texture2D texture in stagingTextures)
            texture.Dispose();
    }
}
