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

public sealed class LibavVideoEncoder : IVideoEncoder
{
    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly ICaptureSource source;
    private readonly Stopwatch sessionClock;
    private readonly int frameIntervalMs;

    private readonly Nv12Converter converter;
    private readonly ShaderResourceView sourceSrv;
    private readonly AsyncTextureReadback? readback;
    private readonly LibavEncoderSession session;
    private readonly Thread encodeThread;

    private volatile bool running;

    private long framesEncoded;
    private long framesSkipped;
    private long encodedBytes;

    public int Width => session.Width;

    public int Height => session.Height;

    public string EncoderName => $"libavcodec {session.EncoderName} ({(session.UsesD3D11Surfaces ? "d3d11" : "readback")})";

    public VideoCodec Codec { get; }

    public long FramesEncoded => Interlocked.Read(ref framesEncoded);

    public long FramesSkipped => Interlocked.Read(ref framesSkipped);

    public long EncodedBytes => Interlocked.Read(ref encodedBytes);

    public double AverageFps => FramesEncoded / Math.Max(sessionClock.Elapsed.TotalSeconds, 0.001);

    public Exception? LastError { get; private set; }

    public event Action<long, byte[]>? ChunkEncoded;

    public bool SupportsLiveBitrate => session.SupportsLiveBitrate;

    public LibavVideoEncoder(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource source,
        string ffmpegDirectory,
        Stopwatch sessionClock,
        int targetHeight,
        int bitrateKbps,
        int fps,
        VideoCodec codec)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        this.source = source;
        this.sessionClock = sessionClock;
        frameIntervalMs = Math.Max(1, 1000 / Math.Max(1, fps));
        Codec = codec;

        LibavEncoderSession.EnsureLoaded(ffmpegDirectory);

        (int width, int height) = ComputeScaledSize(source.Width, source.Height, targetHeight);

        Device device = gameAdapterDevice.Device;
        GpuVendor vendor = GpuVendorDetector.Detect(device);
        string encoderName = VideoEncoderSelector.EncoderName(vendor, codec);
        IReadOnlyList<(string Name, string Value)> options = VideoEncoderSelector.EncoderOptions(vendor);

        converter = new Nv12Converter(device, width, height);
        try
        {
            sourceSrv = new ShaderResourceView(device, source.CaptureTexture);

            LibavEncoderSession? surfaceSession = null;
            if (SurfaceInputWorks(encoderName, options, codec, width, height, fps, bitrateKbps))
                surfaceSession = new LibavEncoderSession(encoderName, options, codec, width, height, fps, bitrateKbps, device);

            if (surfaceSession is not null)
            {
                session = surfaceSession;
            }
            else
            {
                session = new LibavEncoderSession(encoderName, options, codec, width, height, fps, bitrateKbps);
                readback = new AsyncTextureReadback(device, width, height, Format.NV12);
            }
        }
        catch
        {
            session?.Dispose();
            readback?.Dispose();
            sourceSrv?.Dispose();
            converter.Dispose();
            throw;
        }

        running = true;
        encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "ScreenShare-LibavEncode" };
        encodeThread.Start();
    }

    private static (int Width, int Height) ComputeScaledSize(int sourceWidth, int sourceHeight, int targetHeight)
    {
        int height = Math.Min(targetHeight, sourceHeight);
        int width = (int)Math.Round(sourceWidth * (height / (double)sourceHeight));
        width -= width % 2;
        height -= height % 2;
        return (Math.Max(2, width), Math.Max(2, height));
    }

    private bool SurfaceInputWorks(
        string encoderName, IReadOnlyList<(string Name, string Value)> options, VideoCodec codec,
        int width, int height, int fps, int bitrateKbps)
    {
        try
        {
            using var probe = new LibavEncoderSession(
                encoderName, options, codec, width, height, fps, bitrateKbps, gameAdapterDevice.Device);

            bool copied;
            lock (gameAdapterDevice.ImmediateContextLock)
            {
                DeviceContext context = gameAdapterDevice.Device.ImmediateContext;
                converter.Convert(context, sourceSrv, source.Width, source.Height);
                copied = probe.TryCopyToSurface(context, converter.Target);
            }

            if (!copied)
                throw new InvalidOperationException("The fresh surface pool had no free surface.");

            probe.SendSurface(0, (_, _) => { });
            probe.Flush((_, _) => { });
            return true;
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Information(
                $"ScreenShare: {encoderName} can't take frames straight from the GPU here ({ex.Message}); " +
                "reading them back to the CPU instead.");
            return false;
        }
    }

    public void SetBitrate(int bitrateKbps) => session.SetBitrate(bitrateKbps);

    private void EncodeLoop()
    {
        byte[] frame = readback is null ? [] : new byte[readback.FrameSize];
        var clock = Stopwatch.StartNew();
        long frameIndex = 0;

        using var pacer = new PrecisionSleep();
        try
        {
            while (running)
            {
                bool sent = readback is null ? EncodeFromSurface() : EncodeFromReadback(readback, frame);

                if (sent)
                    Interlocked.Increment(ref framesEncoded);
                else
                    Interlocked.Increment(ref framesSkipped);

                frameIndex++;

                pacer.Wait(frameIndex * frameIntervalMs - clock.ElapsedMilliseconds);
            }
        }
        catch (Exception ex) when (running)
        {
            ServiceCollector.PluginLog.Error(ex, "ScreenShare libavcodec encoder stopped");
            LastError = ex;
            running = false;
        }
    }

    private bool EncodeFromSurface()
    {
        bool copied;
        long capturedAtMs;
        lock (gameAdapterDevice.ImmediateContextLock)
        {
            DeviceContext context = gameAdapterDevice.Device.ImmediateContext;
            converter.Convert(context, sourceSrv, source.Width, source.Height);
            copied = session.TryCopyToSurface(context, converter.Target);
            capturedAtMs = sessionClock.ElapsedMilliseconds;
        }

        if (!copied)
            return false;

        session.SendSurface(capturedAtMs, EmitAccessUnit);
        return true;
    }

    private bool EncodeFromReadback(AsyncTextureReadback readback, byte[] frame)
    {
        bool gotFrame;
        long capturedAtMs;
        lock (gameAdapterDevice.ImmediateContextLock)
        {
            converter.Convert(gameAdapterDevice.Device.ImmediateContext, sourceSrv, source.Width, source.Height);

            gotFrame = readback.TryReadPrevious(
                converter.Target, sessionClock.ElapsedMilliseconds, frame, out capturedAtMs);
        }

        if (!gotFrame)
            return false;

        session.SendNv12(frame, capturedAtMs, EmitAccessUnit);
        return true;
    }

    private void EmitAccessUnit(long ptsMs, byte[] accessUnit)
    {
        Interlocked.Add(ref encodedBytes, accessUnit.Length);
        ChunkEncoded?.Invoke(ptsMs, accessUnit);
    }

    public void Dispose()
    {
        running = false;

        if (!encodeThread.Join(1000))
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare's libavcodec encoder thread didn't exit within 1s (likely a stalled/hung GPU) - " +
                "leaking its encoder and GPU resources rather than risking a use-after-dispose crash.");
            return;
        }

        session.Dispose();
        readback?.Dispose();
        sourceSrv.Dispose();
        converter.Dispose();
    }
}
