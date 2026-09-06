using System.Diagnostics;
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

    /// <summary>The GPU vendor, which is as specific as this path can be: ffmpeg picks its encoder
    /// from the vendor-specific argument set we build, and never tells us which one it chose. The
    /// Media Foundation path reports the actual MFT's name.</summary>
    public string EncoderName => $"ffmpeg ({Vendor})";

    /// <summary>False: bitrate is a spawn argument here, so changing it means respawning ffmpeg -
    /// which is what MediaSessionService.RestartEncoderAsync exists to do, and why adaptation on
    /// this path costs a visible hitch.</summary>
    public bool SupportsLiveBitrate => false;

    /// <summary>No-op, so the adaptation loop can call it unconditionally. Callers that need the
    /// bitrate to actually change check <see cref="SupportsLiveBitrate"/> and rebuild instead.</summary>
    public void SetBitrate(int bitrateKbps)
    {
    }

    public long FramesEncoded => Interlocked.Read(ref framesEncoded);

    public long FramesSkipped => Interlocked.Read(ref framesSkipped);

    public long EncodedBytes => Interlocked.Read(ref encodedBytes);

    /// <summary>Average encode rate since this encoder was constructed - directly comparable to
    /// H264Decoder.AverageFps on the viewer's side, so the two can be checked side by side to see
    /// exactly where a stream's effective FPS actually drops below its configured target: at
    /// capture/encode (this number), or somewhere downstream (network, decode, or the viewer's
    /// own render loop) if this number looks healthy but playback still looks choppy.</summary>
    public double AverageFps => FramesEncoded / Math.Max(sessionClock.Elapsed.TotalSeconds, 0.001);

    public Exception? LastError { get; private set; }

    /// <summary>Raised on the internal reader thread - handlers must not block (e.g. hand the
    /// bytes to MediaSocketClient.SendVideoChunk, which itself never blocks). The long is a PTS
    /// in milliseconds on the shared sessionClock passed into this encoder's constructor - the
    /// most recently CAPTURED frame's timestamp as of when this chunk was read from ffmpeg's
    /// stdout (chunks aren't frame-aligned, so this is an approximation, but ffmpeg's low-latency
    /// tuning keeps the gap small enough for A/V sync purposes). Comparable directly against
    /// audio's own PTS on the viewer side only because both encoders share the exact same clock -
    /// see MediaSessionService.StartHostingCoreAsync.</summary>
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

    /// <summary>Paced against a Stopwatch schedule (target = frameIndex * frameIntervalMs) rather
    /// than a plain Thread.Sleep(frameIntervalMs) each iteration - the latter doesn't account for
    /// how long the GPU blit/readback/stdin-write themselves take (real, non-zero work under
    /// ImmediateContextLock), so each loop iteration actually took slightly LONGER than
    /// frameIntervalMs and the error compounded every frame, silently capturing/encoding at a rate
    /// below the configured target regardless of what the target was set to. Very likely the real
    /// explanation for an earlier, previously-unresolved report of the viewer consistently
    /// receiving close to half of whatever target FPS was configured (e.g. 60 -> ~30, 30 -> ~16):
    /// if the per-iteration work time happens to land in the same ballpark as frameIntervalMs
    /// itself, an open-loop Sleep-per-iteration produces almost exactly that ratio. Scheduling
    /// against elapsed time instead means a slow iteration just eats into the next frame's sleep
    /// rather than permanently pushing every future frame later.</summary>
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

    /// <summary>The 1s Join timeout is bounded on purpose (Dispose must not hang forever), but
    /// that means it's only a best-effort wait, not a guarantee writerThread has actually stopped
    /// touching GPU resources - if the GPU is genuinely stalled/removed, a Map/blit call can stay
    /// blocked well past 1s. Disposing downscaledTexture/sourceSrv/downscaler/readback regardless
    /// of that would be a real use-after-dispose race: if the stuck call eventually returns (once
    /// the driver unstalls, or mid-TDR-reset), it touches COM objects that were already released,
    /// which crashes natively rather than throwing a catchable .NET exception - a real, reported
    /// "rare crash when stopping a stream that had a stalled GPU" traced back to exactly this.
    /// Skipping the GPU-resource disposal (intentionally leaking them) in that rare case is a far
    /// better outcome than risking that crash.</summary>
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
