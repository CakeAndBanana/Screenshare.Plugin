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
/// the earlier local-only round-trip test - the extra tick of margin traded latency for headroom
/// against that contention. It is back to 2 now that the documented "real fix" from that same
/// note is actually in place: H264Encoder downscales on the GPU first, so this only ever transfers
/// the encode resolution rather than the full native screen, and the contention that justified the
/// third slot is gone. Each slot costs one frame interval of latency (~33ms at 30fps) on the host,
/// paid by every viewer. If the host's "skipped" counter climbs again, this is the number to raise.
///
/// Each queued copy carries the timestamp of the moment it was made, and that timestamp comes back
/// out with the frame it belongs to. Stamping at READ time instead - which is what the caller used
/// to do - labels every frame with a moment BufferDepth-1 ticks after the content actually
/// happened, so video is presented that much later than the audio it belongs with. That desync is
/// invisible to any PTS-based check, because the timestamps are wrong together and stay internally
/// consistent.
/// </summary>
public sealed class AsyncTextureReadback : IDisposable
{
    private const int BufferDepth = 2;

    private readonly Device device;
    private readonly Texture2D[] stagingTextures;
    private readonly long[] stagingTimestamps;
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
        stagingTimestamps = new long[BufferDepth];
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
    /// touches this device's ImmediateContext concurrently.
    ///
    /// <paramref name="sourceTimestampMs"/> is when the frame being COPIED was captured;
    /// <paramref name="frameTimestampMs"/> comes back as when the frame being RETURNED was
    /// captured, which is several ticks earlier. Keeping those distinct is what stops video from
    /// being timestamped later than it happened - see the class summary.</summary>
    public bool TryReadPrevious(Texture2D source, long sourceTimestampMs, byte[] destination, out long frameTimestampMs)
    {
        device.ImmediateContext.CopyResource(source, stagingTextures[writeIndex]);
        stagingTimestamps[writeIndex] = sourceTimestampMs;

        // Submit it. D3D11 batches commands and flushes them lazily, so without this the copy can
        // sit in the command buffer unsubmitted - and Map(DoNotWait) then reports WasStillDrawing
        // for work the GPU has not even been asked to start yet, which this class counts as a
        // skipped frame.
        //
        // Whether that happened depended on whether something ELSE forced a flush, which is why
        // the symptom looked absurd: capture ran fine while the mouse was moving (the game was
        // rendering and flushing, dragging our copy along with it) and dropped roughly half its
        // frames the moment the scene went still. Nothing to do with focus.
        device.ImmediateContext.Flush();

        // The slot BufferDepth-1 ticks older than the one just written is exactly the slot that
        // will be overwritten NEXT tick - i.e. the next index forward in the ring.
        int readIndex = (writeIndex + 1) % stagingTextures.Length;
        bool gotFrame = framesQueued >= stagingTextures.Length && TryMapAndCopy(stagingTextures[readIndex], destination);
        frameTimestampMs = gotFrame ? stagingTimestamps[readIndex] : sourceTimestampMs;

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
