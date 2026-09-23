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

namespace ScreenShare.Plugin.Encoding;

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
    private readonly int frameIntervalMs;

    private readonly ConcurrentQueue<long> captureTimestamps = new();
    private readonly int maxPendingTimestamps;

    private volatile bool running;
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

    public VideoCodec Codec { get; }

    public H264Encoder(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource source,
        string ffmpegExePath,
        Stopwatch sessionClock,
        int targetHeight = 720,
        int bitrateKbps = 1500,
        int fps = 15,
        VideoCodec codec = VideoCodec.H264)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        this.source = source;
        this.sessionClock = sessionClock;
        frameIntervalMs = 1000 / fps;
        maxPendingTimestamps = Math.Max(8, fps * 2);
        Codec = codec;

        (Width, Height) = ComputeScaledSize(source.Width, source.Height, targetHeight);

        Device device = gameAdapterDevice.Device;
        Vendor = GpuVendorDetector.Detect(device);

        converter = new Nv12Converter(device, Width, Height);
        sourceSrv = new ShaderResourceView(device, source.CaptureTexture);
        readback = new AsyncTextureReadback(device, Width, Height, Format.NV12);

        string encoderArgs = VideoEncoderSelector.BuildEncoderArgs(
            Vendor, codec, bitrateKbps, bitrateKbps * 4 / 3, bitrateKbps * 2, fps * 2);
        string rawFormat = VideoEncoderSelector.RawFormatName(codec);

        string encodeArgs =
            $"-hide_banner -loglevel error -f rawvideo -pix_fmt nv12 -s {Width}x{Height} -r {fps} -i - " +
            $"-an {encoderArgs} {VideoEncoderSelector.BuildOutputPixelFormatArgs(Vendor)} -f {rawFormat} -";

        encoderProcess = new FfmpegProcessRunner(ffmpegExePath, encodeArgs);

        running = true;
        writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "ScreenShare-H264EncWrite" };
        readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "ScreenShare-H264EncRead" };
        writerThread.Start();
        readerThread.Start();
    }

    private static (int Width, int Height) ComputeScaledSize(int sourceWidth, int sourceHeight, int targetHeight)
    {
        int height = Math.Min(targetHeight, sourceHeight);
        int width = (int)Math.Round(sourceWidth * (height / (double)sourceHeight));
        width -= width % 2;
        height -= height % 2;
        return (Math.Max(2, width), Math.Max(2, height));
    }

    private void WriteLoop()
    {
        byte[] frame = new byte[readback.FrameSize];
        var clock = Stopwatch.StartNew();
        long frameIndex = 0;

        using var pacer = new PrecisionSleep();
        try
        {
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

                frameIndex++;

                pacer.Wait(frameIndex * frameIntervalMs - clock.ElapsedMilliseconds);
            }
        }
        catch (Exception ex) when (running)
        {
            RecordError("WriteLoop (capture -> encoder stdin)", ex);
        }
        finally
        {
            TryClose(encoderProcess.StandardInput);
        }
    }

    private void ReaderLoop()
    {
        byte[] buffer = new byte[64 * 1024];

        var splitter = new AnnexBAccessUnitSplitter(Codec);
        try
        {
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
        catch (Exception ex) when (running)
        {
            RecordError("ReaderLoop (encoder stdout -> callback)", ex);
        }
    }

    private void EmitAccessUnit(byte[] accessUnit) =>
        ChunkEncoded?.Invoke(NextCaptureTimestampMs(), accessUnit);

    private long NextCaptureTimestampMs()
    {
        if (!captureTimestamps.TryDequeue(out long capturedAtMs))
            return Interlocked.Read(ref lastCaptureTimestampMs);

        Interlocked.Exchange(ref lastCaptureTimestampMs, capturedAtMs);
        return capturedAtMs;
    }

    private void TrimPendingTimestamps()
    {
        while (captureTimestamps.Count > maxPendingTimestamps)
            captureTimestamps.TryDequeue(out _);
    }

    private void RecordError(string stage, Exception ex)
    {
        ServiceCollector.PluginLog.Error(ex, $"ScreenShare H264Encoder error in {stage}");
        if (Interlocked.CompareExchange(ref errorRecorded, 1, 0) == 0)
            LastError = ex;
        running = false;
    }

    private static void TryClose(Stream stream)
    {
        try
        {
            stream.Close();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
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
