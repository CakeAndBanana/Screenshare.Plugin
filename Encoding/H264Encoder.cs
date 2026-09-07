using System.Diagnostics;
using ScreenShare.Plugin.Capture.ScreenCapture;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Encoding;

public sealed class H264Encoder : IVideoEncoder
{
    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly GpuDownscaler downscaler;
    private readonly ShaderResourceView sourceSrv;
    private readonly Texture2D downscaledTexture;
    private readonly RenderTargetView downscaledRtv;
    private readonly AsyncTextureReadback readback;
    private readonly FfmpegProcessRunner encoderProcess;
    private readonly Thread writerThread;
    private readonly Thread readerThread;
    private readonly int frameIntervalMs;
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
        this.sessionClock = sessionClock;
        frameIntervalMs = 1000 / fps;
        Codec = codec;

        (Width, Height) = ComputeScaledSize(source.Width, source.Height, targetHeight);

        Device device = gameAdapterDevice.Device;
        Vendor = GpuVendorDetector.Detect(device);

        downscaler = new GpuDownscaler(device);
        sourceSrv = new ShaderResourceView(device, source.CaptureTexture);
        downscaledTexture = SharedTextureFactory.CreateRenderTarget(device, Width, Height);
        downscaledRtv = new RenderTargetView(device, downscaledTexture);
        readback = new AsyncTextureReadback(device, Width, Height);

        string encoderArgs = VideoEncoderSelector.BuildEncoderArgs(
            Vendor, codec, bitrateKbps, bitrateKbps * 4 / 3, bitrateKbps * 2, fps * 2);
        string rawFormat = VideoEncoderSelector.RawFormatName(codec);

        // No -vf scale here - the frame arriving on stdin is already at Width x Height, scaled
        // on the GPU before this process ever sees it.
        string encodeArgs =
            $"-hide_banner -loglevel error -f rawvideo -pix_fmt bgra -s {Width}x{Height} -r {fps} -i - " +
            $"-an {encoderArgs} -pix_fmt yuv420p -f {rawFormat} -";

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
        byte[] frame = new byte[Width * Height * 4];
        var clock = Stopwatch.StartNew();
        long frameIndex = 0;

        // See PrecisionSleep: plain Thread.Sleep paces to the system timer, which grabs frames at
        // uneven intervals even when the average rate is right.
        using var pacer = new PrecisionSleep();
        try
        {
            while (running)
            {
                bool gotFrame;
                long capturedAtMs;
                lock (gameAdapterDevice.ImmediateContextLock)
                {
                    downscaler.Blit(gameAdapterDevice.Device.ImmediateContext, sourceSrv, downscaledRtv, Width, Height);

                    // The clock is read here, at the moment the frame is COPIED, and handed to the
                    // readback so it can be carried alongside that frame through the ring. What
                    // comes back is the timestamp of the frame actually returned, which was queued
                    // a couple of ticks ago.
                    gotFrame = readback.TryReadPrevious(
                        downscaledTexture, sessionClock.ElapsedMilliseconds, frame, out capturedAtMs);
                }

                if (gotFrame)
                {
                    // Deliberately the frame's own capture time rather than "now". Stamping "now"
                    // labelled every frame a couple of ticks newer than the content it held, while
                    // audio was stamped accurately at capture - so video was presented that much
                    // later than the audio it belonged with, and no viewer-side clock could correct
                    // it because the timestamps were self-consistently wrong.
                    Interlocked.Exchange(ref lastCaptureTimestampMs, capturedAtMs);
                    encoderProcess.StandardInput.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref framesEncoded);
                }
                else
                {
                    Interlocked.Increment(ref framesSkipped);
                }

                frameIndex++;

                // A non-positive wait returns immediately, so a burst of slow iterations catches
                // back up rather than the shortfall compounding.
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
        try
        {
            int read;
            while (running && (read = encoderProcess.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
            {
                Interlocked.Add(ref encodedBytes, read);
                ChunkEncoded?.Invoke(Interlocked.Read(ref lastCaptureTimestampMs), buffer[..read]);
            }

            if (running)
                encoderProcess.ThrowIfFailed();
        }
        catch (Exception ex) when (running)
        {
            RecordError("ReaderLoop (encoder stdout -> callback)", ex);
        }
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
            // other side of the pipe may already be gone
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
        downscaledRtv.Dispose();
        downscaledTexture.Dispose();
        sourceSrv.Dispose();
        downscaler.Dispose();
    }
}
