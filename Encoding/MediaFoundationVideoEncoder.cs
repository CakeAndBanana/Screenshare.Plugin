using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using SharpDX.MediaFoundation;
using static ScreenShare.Plugin.Encoding.Interop.MediaFoundationInterop;
using Device = SharpDX.Direct3D11.Device;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;

namespace ScreenShare.Plugin.Encoding;

public sealed class MediaFoundationVideoEncoder : IVideoEncoder
{
    private const int FrameRingLength = 3;

    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly ICaptureSource capture;
    private readonly Stopwatch sessionClock;
    private readonly int frameIntervalMs;

    private readonly Transform transform;
    private readonly DXGIDeviceManager deviceManager;
    private readonly MediaEventGenerator events;
    private readonly ICodecApi? codecApi;
    private readonly Nv12Converter converter;
    private readonly ShaderResourceView sourceSrv;
    private readonly Texture2D[] frameRing;
    private readonly Thread pumpThread;

    private volatile bool running;
    private int ringIndex;
    private long nextFrameIndex;
    private long lastFedPtsMs;
    private long framesEncoded;
    private long framesSkipped;
    private long encodedBytes;
    private int pendingBitrateKbps;

    public int Width => converter.Width;

    public int Height => converter.Height;

    public string EncoderName { get; } = "Media Foundation";

    public VideoCodec Codec { get; }

    public long FramesEncoded => Interlocked.Read(ref framesEncoded);

    public long FramesSkipped => Interlocked.Read(ref framesSkipped);

    public long EncodedBytes => Interlocked.Read(ref encodedBytes);

    public double AverageFps => FramesEncoded / Math.Max(sessionClock.Elapsed.TotalSeconds, 0.001);

    public Exception? LastError { get; private set; }

    public event Action<long, byte[]>? ChunkEncoded;

    public bool SupportsLiveBitrate => codecApi is not null;

    public MediaFoundationVideoEncoder(
        GameAdapterDevice gameAdapterDevice,
        ICaptureSource capture,
        Stopwatch sessionClock,
        int targetHeight,
        int bitrateKbps,
        int fps,
        VideoCodec codec)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        this.capture = capture;
        this.sessionClock = sessionClock;
        Codec = codec;
        frameIntervalMs = Math.Max(1, 1000 / Math.Max(1, fps));

        (int width, int height) = ComputeScaledSize(capture.Width, capture.Height, targetHeight);

        Device device = gameAdapterDevice.Device;
        converter = new Nv12Converter(device, width, height);
        frameRing = new Texture2D[FrameRingLength];

        ShaderResourceView? srv = null;
        Transform? encoder = null;
        DXGIDeviceManager? manager = null;
        MediaEventGenerator? generator = null;

        try
        {
            srv = new ShaderResourceView(device, capture.CaptureTexture);
            for (int i = 0; i < frameRing.Length; i++)
                frameRing[i] = CreateNv12Texture(device, width, height);

            encoder = ActivateEncoder(codec, out string name);
            EncoderName = name;

            using (MediaAttributes attributes = encoder.Attributes)
                attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1);

            manager = new DXGIDeviceManager();
            manager.ResetDevice(device);
            encoder.ProcessMessage(TMessageType.SetD3DManager, manager.NativePointer);

            transform = encoder;
            ConfigureTypes(width, height, fps, bitrateKbps);
            codecApi = TryGetCodecApi(bitrateKbps, fps);

            encoder.ProcessMessage(TMessageType.NotifyBeginStreaming, nint.Zero);
            encoder.ProcessMessage(TMessageType.NotifyStartOfStream, nint.Zero);

            generator = encoder.QueryInterface<MediaEventGenerator>();

            deviceManager = manager;
            events = generator;
            sourceSrv = srv;

            running = true;
            pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "ScreenShare-MediaFoundationEncode",
            };
            pumpThread.Start();
        }
        catch
        {
            generator?.Dispose();
            encoder?.Dispose();
            manager?.Dispose();
            foreach (Texture2D? texture in frameRing)
                texture?.Dispose();
            srv?.Dispose();
            converter.Dispose();
            throw;
        }
    }

    private static (int Width, int Height) ComputeScaledSize(int sourceWidth, int sourceHeight, int targetHeight)
    {
        int height = Math.Min(targetHeight, sourceHeight);
        int width = (int)Math.Round(sourceWidth * (height / (double)sourceHeight));
        width -= width % 2;
        height -= height % 2;
        return (Math.Max(2, width), Math.Max(2, height));
    }

    private static Texture2D CreateNv12Texture(Device device, int width, int height) =>
        new(device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });

    private static Transform ActivateEncoder(VideoCodec codec, out string name)
    {
        Guid subtype = codec == VideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264;
        List<nint> activates = EnumerateEncoders(
            subtype,
            MftEnumFlags.Hardware | MftEnumFlags.AsyncMft | MftEnumFlags.SortAndFilter);

        if (activates.Count == 0)
            throw new NotSupportedException($"No hardware Media Foundation encoder for {codec} on this machine.");

        nint chosen = activates[0];
        for (int i = 1; i < activates.Count; i++)
            Marshal.Release(activates[i]);

        using var activate = new Activate(chosen);

        string friendly;
        try
        {
            friendly = activate.Get<string>(MftFriendlyName);
        }
        catch (Exception)
        {
            friendly = $"{codec} hardware encoder";
        }

        name = friendly;
        return activate.ActivateObject<Transform>(typeof(Transform).GUID);
    }

    private void ConfigureTypes(int width, int height, int fps, int bitrateKbps)
    {
        Guid subtype = Codec == VideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264;

        using (var outputType = new MediaType())
        {
            outputType.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
            outputType.Set(MediaTypeAttributeKeys.Subtype, subtype);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrateKbps * 1000);
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, ProgressiveInterlaceMode);
            outputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            outputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            transform.SetOutputType(0, outputType, 0);
        }

        using var inputType = new MediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatNv12);
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, ProgressiveInterlaceMode);
        inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
        inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
        inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        transform.SetInputType(0, inputType, 0);
    }

    private const int ProgressiveInterlaceMode = 2;

    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    private ICodecApi? TryGetCodecApi(int bitrateKbps, int fps)
    {
        try
        {
            var api = (ICodecApi)Marshal.GetObjectForIUnknown(transform.NativePointer);

            var accepted = new List<string>();
            var refused = new List<string>();

            void Apply(string name, Guid property, object value) =>
                (TrySetCodecProperty(api, property, value) ? accepted : refused).Add(name);

            Apply("CBR", AvEncCommonRateControlMode, RateControlModeCbr);
            Apply($"{bitrateKbps}kbps", AvEncCommonMeanBitRate, (uint)(bitrateKbps * 1000));
            Apply("low-latency", AvLowLatencyMode, true);

            Apply($"GOP {fps * 2}", AvEncMpvGopSize, (uint)(fps * 2));

            ServiceCollector.PluginLog.Information(
                $"ScreenShare encoder settings on {EncoderName}: applied [{string.Join(", ", accepted)}]" +
                (refused.Count > 0 ? $", refused [{string.Join(", ", refused)}]" : string.Empty));

            return api;
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(
                $"ScreenShare: {EncoderName} does not expose ICodecAPI ({ex.Message}); bitrate changes will rebuild it instead.");
            return null;
        }
    }

    // Only records the request; the pump thread applies it before its next frame. ICodecAPI can't
    // be called from just any thread: its RCW belongs to the MTA it was created in, and the MFT
    // isn't marshallable, so a call from the game's UI thread (STA) - the settings slider - fails
    // with E_NOINTERFACE. The pump thread is MTA.
    public void SetBitrate(int bitrateKbps)
    {
        if (codecApi is not null)
            Interlocked.Exchange(ref pendingBitrateKbps, bitrateKbps);
    }

    private void ApplyPendingBitrate()
    {
        int bitrateKbps = Interlocked.Exchange(ref pendingBitrateKbps, 0);
        if (bitrateKbps <= 0 || codecApi is null)
            return;

        try
        {
            if (!TrySetCodecProperty(codecApi, AvEncCommonMeanBitRate, (uint)(bitrateKbps * 1000)))
                ServiceCollector.PluginLog.Warning($"ScreenShare: {EncoderName} refused a bitrate change to {bitrateKbps}kbps.");
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(ex, "ScreenShare could not retune the encoder bitrate.");
        }
    }

    private void PumpLoop()
    {
        var clock = Stopwatch.StartNew();

        using var pacer = new PrecisionSleep();

        try
        {
            while (true)
            {
                using MediaEvent mediaEvent = events.GetEvent(true);
                MediaEventTypes type = mediaEvent.TypeInfo;

                if (type == MediaEventTypes.TransformDrainComplete)
                    break;

                if (!running)
                {
                    if (type == MediaEventTypes.TransformHaveOutput)
                        DrainOutput();

                    continue;
                }

                switch (type)
                {
                    case MediaEventTypes.TransformNeedInput:
                        FeedFrame(clock, pacer);
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        DrainOutput();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!running)
                return;

            LastError = ex;
            running = false;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare Media Foundation encoder stopped");
        }
    }

    private void FeedFrame(Stopwatch clock, PrecisionSleep pacer)
    {
        long dueMs = nextFrameIndex * frameIntervalMs;
        long nowMs = clock.ElapsedMilliseconds;

        if (nowMs > dueMs + frameIntervalMs)
        {
            long missed = (nowMs - dueMs) / frameIntervalMs;
            nextFrameIndex += missed;
            Interlocked.Add(ref framesSkipped, missed);
        }
        else
        {
            pacer.Wait(dueMs - nowMs);
        }

        nextFrameIndex++;

        ApplyPendingBitrate();

        long ptsMs = sessionClock.ElapsedMilliseconds;
        Interlocked.Exchange(ref lastFedPtsMs, ptsMs);

        Texture2D slot = frameRing[ringIndex];
        ringIndex = (ringIndex + 1) % frameRing.Length;

        lock (gameAdapterDevice.ImmediateContextLock)
        {
            DeviceContext context = gameAdapterDevice.Device.ImmediateContext;
            converter.Convert(context, sourceSrv, capture.Width, capture.Height);
            context.CopyResource(converter.Target, slot);
        }

        MediaFactory.CreateDXGISurfaceBuffer(typeof(Texture2D).GUID, slot, 0, false, out MediaBuffer buffer);
        using (buffer)
        {
            buffer.CurrentLength = buffer.MaxLength;

            using Sample sample = MediaFactory.CreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsMs * 10_000;
            sample.SampleDuration = frameIntervalMs * 10_000L;

            transform.ProcessInput(0, sample, 0);
        }
    }

    private void DrainOutput()
    {
        nint samplePtr = ProcessOutput(transform.NativePointer);
        if (samplePtr == nint.Zero)
            return;

        using var sample = new Sample(samplePtr);
        using MediaBuffer buffer = sample.ConvertToContiguousBuffer();

        nint data = buffer.Lock(out _, out int length);
        try
        {
            if (length <= 0)
                return;

            byte[] chunk = new byte[length];
            Marshal.Copy(data, chunk, 0, length);

            Interlocked.Increment(ref framesEncoded);
            Interlocked.Add(ref encodedBytes, length);
            ChunkEncoded?.Invoke(ReadOutputPts(sample), chunk);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private long ReadOutputPts(Sample sample)
    {
        try
        {
            long sampleTime = sample.SampleTime;
            if (sampleTime > 0)
                return sampleTime / 10_000;
        }
        catch (Exception)
        {
            // Ignore; fall back to last fed PTS.
        }

        return Interlocked.Read(ref lastFedPtsMs);
    }

    public void Dispose()
    {
        running = false;

        try
        {
            transform.ProcessMessage(TMessageType.NotifyEndOfStream, nint.Zero);
            transform.ProcessMessage(TMessageType.CommandDrain, nint.Zero);
        }
        catch (Exception)
        {
            // Already torn down, or in a bad state; the Join below still bounds the wait.
        }

        if (!pumpThread.Join(2000))
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare's Media Foundation encoder thread didn't exit within 2s - leaking its " +
                "encoder and GPU resources rather than risking a use-after-dispose crash.");
            return;
        }

        try
        {
            transform.ProcessMessage(TMessageType.NotifyEndStreaming, nint.Zero);
        }
        catch (Exception)
        {
            // Best effort; the encoder is being released either way.
        }

        events.Dispose();
        deviceManager.Dispose();
        transform.Dispose();

        foreach (Texture2D texture in frameRing)
            texture.Dispose();

        sourceSrv.Dispose();
        converter.Dispose();
    }
}
