using ScreenShare.Plugin.Capture.ScreenCapture;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Capture -> GPU-encode pipeline for the network media plane. Reads frames from a running
/// ICaptureSource (either a whole monitor via DesktopDuplicationCapture, or one specific window
/// via WindowCapture), encodes them with a hardware encoder (see VideoEncoderSelector),
/// and raises <see cref="ChunkEncoded"/> for each block of raw encoder output - no local decode.
/// Uses the same non-blocking GPU readback (AsyncTextureReadback) and hardware-encoder-selection
/// logic proven stable in-game during this pipeline's original local round-trip validation.
///
/// Downscales on the GPU (see GpuDownscaler) before the CPU readback rather than after - the
/// readback transfer cost scales with whatever resolution it's reading, and reading native screen
/// resolution regardless of the chosen encode resolution was confirmed to be the dominant cost
/// behind a severe skip-rate cliff once a 30fps target was requested. Reading back only the
/// already-downscaled frame is the actual fix.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly ICaptureSource source;
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

    private long framesEncoded;
    private long framesSkipped;
    private long encodedBytes;

    public int Width { get; }

    public int Height { get; }

    public GpuVendor Vendor { get; }

    public long FramesEncoded => Interlocked.Read(ref framesEncoded);

    public long FramesSkipped => Interlocked.Read(ref framesSkipped);

    public long EncodedBytes => Interlocked.Read(ref encodedBytes);

    public Exception? LastError { get; private set; }

    /// <summary>Raised on the internal reader thread - handlers must not block (e.g. hand the
    /// bytes to MediaSocketClient.Send, which itself never blocks).</summary>
    public event Action<byte[]>? ChunkEncoded;

    public VideoCodec Codec { get; }

    public H264Encoder(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource source,
        string ffmpegExePath,
        int targetHeight = 720,
        int bitrateKbps = 1500,
        int fps = 15,
        VideoCodec codec = VideoCodec.H264)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        this.source = source;
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
        try
        {
            while (running)
            {
                bool gotFrame;
                lock (gameAdapterDevice.ImmediateContextLock)
                {
                    downscaler.Blit(gameAdapterDevice.Device.ImmediateContext, sourceSrv, downscaledRtv, Width, Height);
                    gotFrame = readback.TryReadPrevious(downscaledTexture, frame);
                }

                if (gotFrame)
                {
                    encoderProcess.StandardInput.Write(frame, 0, frame.Length);
                    Interlocked.Increment(ref framesEncoded);
                }
                else
                {
                    Interlocked.Increment(ref framesSkipped);
                }

                Thread.Sleep(frameIntervalMs);
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
                ChunkEncoded?.Invoke(buffer[..read]);
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
        writerThread.Join(1000);
        readerThread.Join(1000);
        encoderProcess.Dispose();
        readback.Dispose();
        downscaledRtv.Dispose();
        downscaledTexture.Dispose();
        sourceSrv.Dispose();
        downscaler.Dispose();
    }
}
