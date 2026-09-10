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
