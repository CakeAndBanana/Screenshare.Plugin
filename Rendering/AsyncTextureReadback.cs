using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// Asyncronous texture readback utility that allows reading back frames from a GPU texture to CPU memory without blocking the GPU.
/// It uses a double-buffering approach to ensure that the GPU can continue rendering while the CPU reads back the previous frame. 
/// </summary>
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

    public int FrameSize => format == Format.NV12 ? Width * Height * 3 / 2 : Width * Height * 4;

    /// <summary>
    /// Creates a new instance of the AsyncTextureReadback class with the specified device, width, height, and format.
    /// </summary>
    /// <param name="device">The Direct3D device.</param>
    /// <param name="width">The width of the texture.</param>
    /// <param name="height">The height of the texture.</param>
    /// <param name="format">The format of the texture.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the format is not supported.</exception>
    public AsyncTextureReadback(Device device, int width, int height, Format format = Format.B8G8R8A8_UNorm)
    {
        // Validate the format to ensure it is either BGRA or NV12, as these are the only supported formats for readback.
        if (format != Format.B8G8R8A8_UNorm && format != Format.NV12)
            throw new ArgumentOutOfRangeException(nameof(format), format, "Only BGRA and NV12 readback are supported.");

        // Initialize the device, format, width, height, and create staging textures for readback.
        this.device = device;
        this.format = format;
        Width = width;
        Height = height;
        stagingTextures = new Texture2D[BufferDepth];
        stagingTimestamps = new long[BufferDepth];
        for (int i = 0; i < BufferDepth; i++)
            stagingTextures[i] = CreateStaging(device, width, height, format);
    }

    /// <summary>
    /// Creates a staging texture with the specified device, width, height, and format. Staging textures are used for readback operations from the GPU to the CPU.
    /// </summary>
    /// <param name="device">The Direct3D device.</param>
    /// <param name="width">The width of the texture.</param>
    /// <param name="height">The height of the texture.</param>
    /// <param name="format">The format of the texture.</param>
    /// <returns>The created staging texture.</returns>
    private static Texture2D CreateStaging(Device device, int width, int height, Format format)
    {
        // Create a description for the staging texture, specifying its properties such as width, height, format, usage, and CPU access flags.
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

        // Create and return the staging texture using the specified device and description.
        return new Texture2D(device, desc);
    }

    /// <summary>
    /// Tries to read the previous frame from the GPU texture to the CPU memory. 
    /// It copies the current source texture to a staging texture and attempts to read back the previous frame into the destination buffer.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="sourceTimestampMs"></param>
    /// <param name="destination"></param>
    /// <param name="frameTimestampMs"></param>
    /// <returns></returns>
    public bool TryReadPrevious(Texture2D source, long sourceTimestampMs, byte[] destination, out long frameTimestampMs)
    {
        // Copy the current source texture to the staging texture at the current write index and store the timestamp.
        device.ImmediateContext.CopyResource(source, stagingTextures[writeIndex]);
        stagingTimestamps[writeIndex] = sourceTimestampMs;

        // Flush the immediate context to ensure that the copy operation is submitted to the GPU.
        device.ImmediateContext.Flush();

        // Calculate the read index for the previous frame based on the current write index and the number of frames queued.
        int readIndex = (writeIndex + 1) % stagingTextures.Length;
        bool gotFrame = framesQueued >= stagingTextures.Length && TryMapAndCopy(stagingTextures[readIndex], destination);
        frameTimestampMs = gotFrame ? stagingTimestamps[readIndex] : sourceTimestampMs;

        // Increment the frames queued count if it is less than the number of staging textures, and update the write index to the read index for the next operation.
        if (framesQueued < stagingTextures.Length)
            framesQueued++;
        writeIndex = readIndex;
        return gotFrame;
    }

    /// <summary>
    /// Tries to map the staging texture to CPU memory and copy its contents to the destination buffer.
    /// </summary>
    /// <param name="staging">The staging texture to map.</param>
    /// <param name="destination">The destination buffer to copy the contents to.</param>
    /// <returns>true if the operation was successful; otherwise, false.</returns>
    private bool TryMapAndCopy(Texture2D staging, byte[] destination)
    {
        // Attempt to map the staging texture to CPU memory for reading. If the GPU is still drawing, catch the exception and return false.
        DataBox box;
        try
        {
            box = device.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.DoNotWait);
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.WasStillDrawing)
        {
            return false;
        }

        // Check if the mapped DataBox is empty, which indicates that the mapping failed or the resource is not available for reading.
        if (box.IsEmpty)
            return false;

        // Copy the contents of the mapped DataBox to the destination buffer based on the texture format (NV12 or BGRA).
        try
        {
            if (format == Format.NV12)
            {
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

    /// <summary>
    /// Copies a plane of pixel data from the source pointer to the destination byte array, taking into account the row pitch and row bytes.
    /// </summary>
    /// <param name="source">The source pointer.</param>
    /// <param name="rowPitch">The row pitch.</param>
    /// <param name="destination">The destination byte array.</param>
    /// <param name="destinationOffset">The offset in the destination array.</param>
    /// <param name="rowBytes">The number of bytes per row.</param>
    /// <param name="rows">The number of rows.</param>
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

    /// <summary>
    /// Disposes of the staging textures used for readback, releasing their resources. This method should be called when the AsyncTextureReadback instance is no longer needed to free up GPU memory.
    /// </summary>
    public void Dispose()
    {
        foreach (Texture2D texture in stagingTextures)
            texture.Dispose();
    }
}
