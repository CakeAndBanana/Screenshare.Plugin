using System.Collections.Concurrent;
using System.Diagnostics;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Encoding.H264;

/// <summary>
/// H.264 video encoder that uses ffmpeg for encoding and Direct3D11 for GPU acceleration. 
/// It captures frames from a source, converts them to NV12 format, and feeds them to the ffmpeg process for encoding. 
/// The encoded chunks are emitted via the ChunkEncoded event.
/// </summary>
public sealed class H264Encoder : IVideoEncoder
{
    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly ICaptureSource source;
    private readonly Nv12Converter converter;
    private readonly ShaderResourceView sourceSrv;
    private readonly AsyncTextureReadback readback;
    private readonly FfmpegProcessRunner encoderProcess;
    private readonly Thread writerThread;
    private readonly Thread readerThread;
    private readonly int fps;

    private readonly ConcurrentQueue<long> captureTimestamps = new();
    private readonly int maxPendingTimestamps;

    private volatile bool running;
    private volatile bool disposed;
    private int errorRecorded;

    private readonly Stopwatch sessionClock;

    private long framesEncoded;
    private long framesSkipped;
    private long encodedBytes;
    private long lastCaptureTimestampMs;

    public int Width { get; }

    public int Height { get; }

    public GpuVendor Vendor { get; }

    public string EncoderName => $"ffmpeg ({Vendor})";

    public bool SupportsLiveBitrate => false;

    public void SetBitrate(int bitrateKbps)
    {
    }

    public long FramesEncoded => Interlocked.Read(ref framesEncoded);

    public long FramesSkipped => Interlocked.Read(ref framesSkipped);

    public long EncodedBytes => Interlocked.Read(ref encodedBytes);

    public double AverageFps => FramesEncoded / Math.Max(sessionClock.Elapsed.TotalSeconds, 0.001);

    public Exception? LastError { get; private set; }

    public event Action<long, byte[]>? ChunkEncoded;

    public SupportedVideoCodec Codec { get; }

    /// <summary>
    /// Constructs a new H264Encoder instance, initializing the necessary resources for capturing, converting, and encoding video frames.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device for GPU acceleration.</param>
    /// <param name="source">The capture source for obtaining video frames.</param>
    /// <param name="ffmpegExePath">The path to the ffmpeg executable.</param>
    /// <param name="sessionClock">The stopwatch for measuring session duration.</param>
    /// <param name="targetHeight">The target height for the encoded video.</param>
    /// <param name="bitrateKbps">The target bitrate for the encoded video.</param>
    /// <param name="fps">The frames per second for the encoded video.</param>
    /// <param name="codec">The video codec to use for encoding.</param>
    public H264Encoder(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource source,
        string ffmpegExePath,
        Stopwatch sessionClock,
        int targetHeight = 720,
        int bitrateKbps = 1500,
        int fps = 15,
        SupportedVideoCodec codec = SupportedVideoCodec.H264)
    {
        // Initialize fields and validate parameters.
        this.gameAdapterDevice = gameAdapterDevice;
        this.source = source;
        this.sessionClock = sessionClock;
        this.fps = Math.Max(1, fps);
        maxPendingTimestamps = Math.Max(8, fps * 2);
        Codec = codec;

        // Compute the scaled width and height based on the source dimensions and target height, ensuring even dimensions for NV12 format.
        (Width, Height) = ComputeScaledSize(source.Width, source.Height, targetHeight);

        // Initialize the GPU device and detect the vendor for encoder selection.
        Device device = gameAdapterDevice.Device;
        Vendor = GpuVendorDetector.Detect(device);

        // Create the NV12 converter, shader resource view for the source texture, and async readback for capturing frames.
        converter = new Nv12Converter(device, Width, Height);
        sourceSrv = new ShaderResourceView(device, source.CaptureTexture);
        readback = new AsyncTextureReadback(device, Width, Height, Format.NV12);

        // Build the ffmpeg encoder arguments based on the selected codec, bitrate, and frame rate.
        string encoderArgs = VideoEncoderSelector.BuildEncoderArgs(
            Vendor, codec, bitrateKbps, bitrateKbps * 4 / 3, bitrateKbps * 2, fps);
        string rawFormat = VideoEncoderSelector.RawFormatName(codec);

        // Construct the ffmpeg command-line arguments for encoding, specifying input format, pixel format, resolution, frame rate, and output format.
        string encodeArgs =
            $"-hide_banner -loglevel error -f rawvideo -pix_fmt nv12 -s {Width}x{Height} -r {fps} -i - " +
            $"-an {encoderArgs} {VideoEncoderSelector.BuildOutputPixelFormatArgs(Vendor)} -f {rawFormat} -";

        encoderProcess = new FfmpegProcessRunner(ffmpegExePath, encodeArgs);

        // Start the writer and reader threads for capturing frames and reading encoded output.
        running = true;
        writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "ScreenShare-H264EncWrite" };
        readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "ScreenShare-H264EncRead" };
        writerThread.Start();
        readerThread.Start();
    }

    /// <summary>
    /// Computes the scaled width and height for the encoded video based on the source dimensions and target height, ensuring that both dimensions are even numbers for NV12 format compatibility.  
    /// </summary>
    /// <param name="sourceWidth">The width of the source video.</param>
    /// <param name="sourceHeight">The height of the source video.</param>
    /// <param name="targetHeight">The target height for the encoded video.</param>
    /// <returns>A tuple containing the scaled width and height.</returns>
    private static (int Width, int Height) ComputeScaledSize(int sourceWidth, int sourceHeight, int targetHeight)
    {
        int height = Math.Min(targetHeight, sourceHeight);
        int width = (int)Math.Round(sourceWidth * (height / (double)sourceHeight));
        width -= width % 2;
        height -= height % 2;
        return (Math.Max(2, width), Math.Max(2, height));
    }

    /// <summary>
    /// Writes frames from the capture source to the ffmpeg encoder process. It captures frames, converts them to NV12 format, and writes them to the encoder's standard input. 
    /// The method also handles pacing to maintain the desired frame rate and records timestamps for each captured frame. If an error occurs during the process, it is logged and recorded.
    /// </summary>
    private void WriteLoop()
    {
        byte[] frame = new byte[readback.FrameSize];
        var clock = Stopwatch.StartNew();
        long frameIndex = 0;

        // Use PrecisionSleep to maintain accurate timing for frame capture and encoding.
        using var pacer = new PrecisionSleep();
        try
        {
            // Loop until the encoder is stopped, capturing frames and writing them to the encoder process.
            while (running)
            {
                bool gotFrame;
                long capturedAtMs;
                lock (gameAdapterDevice.ImmediateContextLock)
                {
                    converter.Convert(gameAdapterDevice.Device.ImmediateContext, sourceSrv, source.Width, source.Height);

                    gotFrame = readback.TryReadPrevious(
                        converter.Target, sessionClock.ElapsedMilliseconds, frame, out capturedAtMs);
                }

                // If a frame was successfully captured, enqueue its timestamp and write the frame to the encoder's standard input. Otherwise, increment the skipped frames counter.
                if (gotFrame)
                {
                    captureTimestamps.Enqueue(capturedAtMs);
                    TrimPendingTimestamps();

                    encoderProcess.StandardInput.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref framesEncoded);
                }
                else
                {
                    Interlocked.Increment(ref framesSkipped);
                }

                // Increment the frame index and wait for the appropriate time to maintain the desired frame rate.
                frameIndex++;

                pacer.Wait(frameIndex * 1000 / fps - clock.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                RecordError("WriteLoop (capture -> encoder stdin)", ex);
        }
        finally
        {
            TryClose(encoderProcess.StandardInput);
        }
    }

    /// <summary>
    /// Reads encoded data from the ffmpeg encoder process's standard output, splits it into access units, and emits them via the ChunkEncoded event.
    /// </summary>
    private void ReaderLoop()
    {
        byte[] buffer = new byte[64 * 1024];

        // Use AnnexBAccessUnitSplitter to split the encoded data into access units for H.264 or HEVC.
        var splitter = new AnnexBAccessUnitSplitter(Codec);
        try
        {
            // Read from the encoder's standard output until the encoder is stopped or there is no more data to read.
            int read;
            while (running && (read = encoderProcess.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
            {
                Interlocked.Add(ref encodedBytes, read);
                splitter.Append(buffer, 0, read, EmitAccessUnit);
            }

            if (running)
            {
                if (splitter.Flush() is { } tail)
                    EmitAccessUnit(tail);

                encoderProcess.ThrowIfFailed();
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                RecordError("ReaderLoop (encoder stdout -> callback)", ex);
        }
    }

    /// <summary>
    /// Emits an access unit (encoded video chunk) by invoking the ChunkEncoded event with the corresponding capture timestamp.
    /// </summary>
    /// <param name="accessUnit"></param>
    private void EmitAccessUnit(byte[] accessUnit) =>
        ChunkEncoded?.Invoke(NextCaptureTimestampMs(), accessUnit);

    /// <summary>
    /// NextCaptureTimestampMs retrieves the next capture timestamp from the queue of pending timestamps. 
    /// If there are no pending timestamps, it returns the last recorded capture timestamp. 
    /// This method ensures that each emitted access unit is associated with the correct capture timestamp.
    /// </summary>
    /// <returns></returns>
    private long NextCaptureTimestampMs()
    {
        if (!captureTimestamps.TryDequeue(out long capturedAtMs))
            return Interlocked.Read(ref lastCaptureTimestampMs);

        Interlocked.Exchange(ref lastCaptureTimestampMs, capturedAtMs);
        return capturedAtMs;
    }

    /// <summary>
    /// Trims the queue of pending capture timestamps to ensure it does not exceed the maximum allowed number of pending timestamps.
    /// </summary>
    private void TrimPendingTimestamps()
    {
        while (captureTimestamps.Count > maxPendingTimestamps)
            captureTimestamps.TryDequeue(out _);
    }

    /// <summary>
    /// Records an error that occurred during the encoding process, logs it, and sets the LastError property.
    /// </summary>
    /// <param name="stage">The stage where the error occurred.</param>
    /// <param name="ex">The exception that occurred.</param>
    private void RecordError(string stage, Exception ex)
    {
        ServiceCollector.PluginLog.Error(ex, $"ScreenShare H264Encoder error in {stage}");
        if (Interlocked.CompareExchange(ref errorRecorded, 1, 0) == 0)
            LastError = ex;
        running = false;
    }

    /// <summary>
    /// Tries to close a stream, catching and ignoring any exceptions that may occur during the close operation.
    /// </summary>
    /// <param name="stream"></param>
    private static void TryClose(Stream stream)
    {
        try
        {
            stream.Close();
        }
        catch
        {
            // Ignore any exceptions that occur during stream closure to prevent crashes during disposal.
        }
    }

    /// <summary>
    /// Disposes of the H264Encoder, stopping the encoding process, joining the writer and reader threads, and releasing all associated resources.
    /// </summary>
    public void Dispose()
    {
        disposed = true;
        running = false;
        bool writerExited = writerThread.Join(1000);
        readerThread.Join(1000);
        encoderProcess.Dispose();

        if (!writerExited)
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare H264Encoder's capture thread didn't exit within 1s (likely a stalled/hung GPU) - " +
                "leaking its GPU resources rather than risking a use-after-dispose crash.");
            return;
        }

        readback.Dispose();
        sourceSrv.Dispose();
        converter.Dispose();
    }
}
