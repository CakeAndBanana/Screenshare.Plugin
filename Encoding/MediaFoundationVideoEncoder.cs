using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenShare.Plugin.Capture.ScreenCapture;
using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using SharpDX.MediaFoundation;
using static ScreenShare.Plugin.Encoding.Interop.MediaFoundationInterop;
using Device = SharpDX.Direct3D11.Device;

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

            // Async MFTs reject everything until unlocked, and every hardware encoder is async.
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
            // None of this reaches the caller if the constructor throws, so without an explicit
            // teardown it would leak a live hardware encoder and four GPU textures for the rest of
            // the session - and the fallback path deliberately keeps going after a failure here.
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

        // MFTEnumEx sorted them best-first, so the first is the one to use and the rest are released.
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
            // An optional attribute; not carrying one is no reason to refuse the encoder.
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

            // Logged rather than silently attempted. Which of these an encoder accepts varies by
            // vendor, and the difference is not cosmetic: without rate control and mean bitrate the
            // MFT encodes to its own default, which can sit far above the configured budget and
            // congest the uplink - a failure that looks like the network or the decoder struggling,
            // nowhere near the encoder that caused it.
            var accepted = new List<string>();
            var refused = new List<string>();

            void Apply(string name, Guid property, object value) =>
                (TrySetCodecProperty(api, property, value) ? accepted : refused).Add(name);

            Apply("CBR", AvEncCommonRateControlMode, RateControlModeCbr);
            Apply($"{bitrateKbps}kbps", AvEncCommonMeanBitRate, (uint)(bitrateKbps * 1000));
            Apply("low-latency", AvLowLatencyMode, true);

            // A keyframe every two seconds, so a viewer joining mid-stream gets a picture within
            // that rather than waiting on whatever default the vendor happened to choose.
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

    public void SetBitrate(int bitrateKbps)
    {
        if (codecApi is null)
            return;

        try
        {
            TrySetCodecProperty(codecApi, AvEncCommonMeanBitRate, (uint)(bitrateKbps * 1000));
        }
        catch (Exception ex)
        {
            // Deliberately not fatal: the stream continuing at the old bitrate is a far better
            // outcome than dropping it because one adaptation step failed.
            ServiceCollector.PluginLog.Warning(ex, "ScreenShare could not retune the encoder bitrate.");
        }
    }

    private void PumpLoop()
    {
        var clock = Stopwatch.StartNew();

        // Capture pacing has the same problem display pacing does: Thread.Sleep rounds to the system
        // timer, so frames get grabbed at uneven intervals and that unevenness is baked into their
        // timestamps, where no amount of viewer-side pacing can take it back out.
        using var pacer = new PrecisionSleep();

        try
        {
            while (true)
            {
                using MediaEvent mediaEvent = events.GetEvent(true);
                MediaEventTypes type = mediaEvent.TypeInfo;

                if (type == MediaEventTypes.TransformDrainComplete)
                    break;

                // While stopping, keep draining what the encoder has already produced so the drain
                // can finish and this loop can exit - but stop feeding it. Feeding after
                // NotifyEndOfStream is an error, and Dispose sends that before this thread has
                // necessarily noticed.
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
            // An exception escaping a background thread takes the game down with it, so a failure
            // here surfaces through LastError and the host's UI instead. While stopping it is
            // expected - the encoder is being torn down underneath this loop.
            if (!running)
                return;

            LastError = ex;
            running = false;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare Media Foundation encoder stopped");
        }
    }

    private void FeedFrame(Stopwatch clock, PrecisionSleep pacer)
    {
        // Paced against elapsed time rather than sleeping a fixed interval each iteration, for the
        // same reason H264Encoder's WriteLoop is: conversion and submission take real time, and
        // sleeping a whole interval on top of that drifts the capture rate below the target.
        long dueMs = nextFrameIndex * frameIntervalMs;
        long nowMs = clock.ElapsedMilliseconds;

        if (nowMs > dueMs + frameIntervalMs)
        {
            // Behind schedule. Abandon the slots that have already passed rather than encoding them
            // all late, which would only push the stream further behind, and count them - a
            // climbing skip count is exactly the signal that capture cannot hold the target rate.
            long missed = (nowMs - dueMs) / frameIntervalMs;
            nextFrameIndex += missed;
            Interlocked.Add(ref framesSkipped, missed);
        }
        else
        {
            pacer.Wait(dueMs - nowMs);
        }

        nextFrameIndex++;

        // Timestamped here, at conversion, not when the encoder later emits it. Audio is stamped at
        // capture on this same clock, and video labelled with its completion time instead would sit
        // late against the audio it belongs with by however long the encoder took.
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
            // Without this the buffer reports zero bytes, and an encoder handed an empty sample
            // emits nothing at all and reports no error.
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
                return sampleTime / 10_000;   // MF counts in 100ns units; the wire carries ms.
        }
        catch (Exception)
        {
            // This sample carries no timestamp.
        }

        return Interlocked.Read(ref lastFedPtsMs);
    }

    public void Dispose()
    {
        running = false;

        // GetEvent blocks, so the pump thread has to be woken rather than merely asked to stop.
        // Draining generates events, which is what lets it reach TransformDrainComplete and return.
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
