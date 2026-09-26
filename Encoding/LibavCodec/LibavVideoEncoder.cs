using System.Diagnostics;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Encoding.LibavCodec;

/// <summary>
/// Livab video encoder that uses libavcodec to encode frames from a capture source.
/// </summary>
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

    public SupportedVideoCodec Codec { get; }

    public long FramesEncoded => Interlocked.Read(ref framesEncoded);

    public long FramesSkipped => Interlocked.Read(ref framesSkipped);

    public long EncodedBytes => Interlocked.Read(ref encodedBytes);

    public double AverageFps => FramesEncoded / Math.Max(sessionClock.Elapsed.TotalSeconds, 0.001);

    public Exception? LastError { get; private set; }

    public event Action<long, byte[]>? ChunkEncoded;

    public bool SupportsLiveBitrate => session.SupportsLiveBitrate;

    /// <summary>
    /// Constructor for the libav video encoder.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device.</param>
    /// <param name="source">The capture source.</param>
    /// <param name="ffmpegDirectory">The directory containing the FFmpeg libraries.</param>
    /// <param name="sessionClock">The stopwatch for measuring session time.</param>
    /// <param name="targetHeight">The target height for the encoded video.</param>
    /// <param name="bitrateKbps">The target bitrate in kilobits per second.</param>
    /// <param name="fps">The frames per second.</param>
    /// <param name="codec">The video codec.</param>
    public LibavVideoEncoder(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource source,
        string ffmpegDirectory,
        Stopwatch sessionClock,
        int targetHeight,
        int bitrateKbps,
        int fps,
        SupportedVideoCodec codec)
    {
        // Validate parameters
        this.gameAdapterDevice = gameAdapterDevice;
        this.source = source;
        this.sessionClock = sessionClock;

        // Calculate frame interval in milliseconds, ensuring it's at least 1 ms
        frameIntervalMs = Math.Max(1, 1000 / Math.Max(1, fps));
        Codec = codec;

        LibavEncoderSession.EnsureLoaded(ffmpegDirectory);

        // Calculate the scaled width and height based on the source dimensions and target height
        (int width, int height) = ComputeScaledSize(source.Width, source.Height, targetHeight);

        Device device = gameAdapterDevice.Device;
        GpuVendor vendor = GpuVendorDetector.Detect(device);
        string encoderName = VideoEncoderSelector.EncoderName(vendor, codec);
        IReadOnlyList<(string Name, string Value)> options = VideoEncoderSelector.EncoderOptions(vendor);

        // Initialize the NV12 converter for converting frames to NV12 format
        converter = new Nv12Converter(device, width, height);
        // Initialize the source shader resource view and encoder session
        try
        {
            // Create a shader resource view for the source capture texture
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

    /// <summary>
    /// Calculates the scaled width and height for the encoded video based on the source dimensions and target height.
    /// </summary>
    /// <param name="sourceWidth">The width of the source video.</param>
    /// <param name="sourceHeight">The height of the source video.</param>
    /// <param name="targetHeight">The target height for the encoded video.</param>
    /// <returns>The scaled width and height for the encoded video.</returns>
    private static (int Width, int Height) ComputeScaledSize(int sourceWidth, int sourceHeight, int targetHeight)
    {
        int height = Math.Min(targetHeight, sourceHeight);
        int width = (int)Math.Round(sourceWidth * (height / (double)sourceHeight));
        width -= width % 2;
        height -= height % 2;
        return (Math.Max(2, width), Math.Max(2, height));
    }


    /// <summary>
    /// SurfaceInputWorks checks if the encoder can take frames directly from the GPU surface without needing to read them back to the CPU.
    /// </summary>
    /// <param name="encoderName">The name of the encoder.</param>
    /// <param name="options">The options for the encoder.</param>
    /// <param name="codec">The video codec.</param>
    /// <param name="width">The width of the encoded video.</param>
    /// <param name="height">The height of the encoded video.</param>
    /// <param name="fps">The frames per second for the encoded video.</param>
    /// <param name="bitrateKbps">The bitrate for the encoded video.</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">Thrown when the encoder cannot take frames directly from the GPU surface.</exception>
    private bool SurfaceInputWorks(
        string encoderName, IReadOnlyList<(string Name, string Value)> options, SupportedVideoCodec codec,
        int width, int height, int fps, int bitrateKbps)
    {
        try
        {
            // Create a temporary encoder session to test if it can take frames directly from the GPU surface
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

    /// <summary>
    /// Sets the birate of the encoder session.
    /// </summary>
    /// <param name="bitrateKbps">Bitrate in Kpbs</param>
    public void SetBitrate(int bitrateKbps) => session.SetBitrate(bitrateKbps);

    /// <summary>
    /// Encoding loop for the encoder thread. 
    /// It continuously captures frames from the source, encodes them, and emits access units until the encoder is stopped.
    /// </summary>
    private void EncodeLoop()
    {
        byte[] frame = readback is null ? [] : new byte[readback.FrameSize];
        var clock = Stopwatch.StartNew();
        long frameIndex = 0;

        // Use PrecisionSleep to wait for the next frame interval, ensuring accurate timing for frame capture and encoding
        using var pacer = new PrecisionSleep();
        try
        {
            while (running)
            {
                // Capture and encode frames at the specified frame interval
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

    /// <summary>
    /// Encode the frames from the GPU surface directly.
    /// </summary>
    /// <returns>True if the frame was encoded successfully, false otherwise.</returns>
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

    /// <summary>
    /// Encodes frames from the readback buffer.
    /// </summary>
    /// <param name="readback">The async texture readback instance.</param>
    /// <param name="frame">The frame data buffer.</param>
    /// <returns>True if the frame was encoded successfully, false otherwise.</returns>
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

    /// <summary>
    /// Emit the encoded access unit to the ChunkEncoded event and update the encoded bytes count.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp in milliseconds.</param>
    /// <param name="accessUnit">The encoded access unit.</param>
    private void EmitAccessUnit(long ptsMs, byte[] accessUnit)
    {
        Interlocked.Add(ref encodedBytes, accessUnit.Length);
        ChunkEncoded?.Invoke(ptsMs, accessUnit);
    }

    /// <summary>
    /// Disposes of the encoder.
    /// </summary>
    public void Dispose()
    {
        running = false;

        if (!encodeThread.Join(1000))
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare's libavcodec encoder thread didn't exit within 1s (likely a stalled/hung GPU).");
            return;
        }

        session.Dispose();
        readback?.Dispose();
        sourceSrv.Dispose();
        converter.Dispose();
    }
}
