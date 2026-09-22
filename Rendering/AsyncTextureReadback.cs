using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

public sealed class AsyncTextureReadback : IDisposable
{
    private const int BufferDepth = 2;

    private readonly Device device;
    private readonly Format format;
    private readonly Texture2D[] stagingTextures;
    private readonly long[] stagingTimestamps;
    private int writeIndex;
    private int framesQueued;

    public int Width { get; }

    public int Height { get; }

    // Tightly packed size of one frame in the destination buffer: 4 bytes per pixel for BGRA,
    // or a full-size Y plane followed by a half-height interleaved UV plane for NV12.
    public int FrameSize => format == Format.NV12 ? Width * Height * 3 / 2 : Width * Height * 4;

    public AsyncTextureReadback(Device device, int width, int height, Format format = Format.B8G8R8A8_UNorm)
    {
        if (format != Format.B8G8R8A8_UNorm && format != Format.NV12)
            throw new ArgumentOutOfRangeException(nameof(format), format, "Only BGRA and NV12 readback are supported.");

        this.device = device;
        this.format = format;
        Width = width;
        Height = height;
        stagingTextures = new Texture2D[BufferDepth];
        stagingTimestamps = new long[BufferDepth];
        for (int i = 0; i < BufferDepth; i++)
            stagingTextures[i] = CreateStaging(device, width, height, format);
    }

    private static Texture2D CreateStaging(Device device, int width, int height, Format format)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        };

        return new Texture2D(device, desc);
    }

    public bool TryReadPrevious(Texture2D source, long sourceTimestampMs, byte[] destination, out long frameTimestampMs)
    {
        device.ImmediateContext.CopyResource(source, stagingTextures[writeIndex]);
        stagingTimestamps[writeIndex] = sourceTimestampMs;

        device.ImmediateContext.Flush();

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

        if (box.IsEmpty)
            return false;

        try
        {
            if (format == Format.NV12)
            {
                // A mapped NV12 surface is one allocation: the UV plane starts straight after
                // Height rows of the Y plane and shares its row pitch.
                CopyPlane(box.DataPointer, box.RowPitch, destination, 0, Width, Height);
                CopyPlane(box.DataPointer + box.RowPitch * Height, box.RowPitch, destination, Width * Height, Width, Height / 2);
            }
            else
            {
                CopyPlane(box.DataPointer, box.RowPitch, destination, 0, Width * 4, Height);
            }

            return true;
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(staging, 0);
        }
    }

    private static void CopyPlane(IntPtr source, int rowPitch, byte[] destination, int destinationOffset, int rowBytes, int rows)
    {
        if (rowPitch == rowBytes)
        {
            Marshal.Copy(source, destination, destinationOffset, rowBytes * rows);
            return;
        }

        for (int y = 0; y < rows; y++)
            Marshal.Copy(source + y * rowPitch, destination, destinationOffset + y * rowBytes, rowBytes);
    }

    public void Dispose()
    {
        foreach (Texture2D texture in stagingTextures)
            texture.Dispose();
    }
}
