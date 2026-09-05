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

/// <summary>
/// Hardware video encoder built on Media Foundation, replacing the ffmpeg child process.
///
/// Two things it does that the ffmpeg path structurally cannot:
///
/// - **Retune bitrate on a running encoder** through ICodecAPI. ffmpeg takes bitrate as a spawn
///   argument, so adapting meant respawning the process and wearing a visible hitch every time.
/// - **Take the frame straight off the GPU.** The encoder is bound to our D3D11 device through an
///   IMFDXGIDeviceManager and accepts the NV12 texture directly, so a frame is never read back to
///   CPU memory, written down a pipe, and parsed out of a byte stream. That removes a frame
///   interval of latency and the entire class of bug where a queued copy sat unsubmitted.
///
/// The sequence here was validated standalone against a real NVIDIA encoder MFT before being
/// written into the plugin, which turned up three failures that all look identical from the
/// outside - the encoder silently producing nothing:
///
/// 1. MFTEnumEx via SharpDX access-violates, so enumeration is hand-rolled (see the interop file).
/// 2. A DXGI surface buffer arrives with CurrentLength zero. An encoder handed a zero-length
///    sample encodes nothing and reports no error.
/// 3. SharpDX marshals MFT_OUTPUT_DATA_BUFFER into ProcessOutput but never back out, so the
///    sample the encoder produced is discarded and PSample is always null.
/// </summary>
public sealed class MediaFoundationVideoEncoder : IVideoEncoder
{
    /// <summary>
    /// How many NV12 textures rotate between the converter and the encoder.
    ///
    /// The converter owns exactly one target and overwrites it every frame, so handing that
    /// texture straight to the MFT would let the next frame's conversion land in it while the
    /// encoder was still reading - intermittent tearing that shows up only under load and would be
    /// miserable to trace back to here. Each frame is copied into the next slot instead: a
    /// GPU-to-GPU copy of about 1.3MB at 720p, nothing like a readback. Three slots is far more
    /// headroom than a hardware encoder needs, since it holds an input surface for well under a
    /// frame.
    /// </summary>
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

    /// <summary>True whenever ICodecAPI was obtained. An encoder that refuses it is rebuilt for a
    /// new bitrate, exactly as the ffmpeg path is.</summary>
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

    /// <summary>Matches H264Encoder's sizing exactly, including the rounding to even - NV12's
    /// chroma plane is half size in both axes and has no representation for an odd dimension.</summary>
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

    /// <summary>Output type first, then input: the encoder derives which inputs it will accept from
    /// what it has been asked to produce, so the reverse order is rejected.</summary>
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

    /// <summary>MFVideoInterlace_Progressive.</summary>
    private const int ProgressiveInterlaceMode = 2;

    /// <summary>MF packs paired values - width/height, numerator/denominator - into a single 64-bit
    /// attribute, high word first.</summary>
    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    /// <summary>Optional configuration. Every property is attempted and none is required - vendors
    /// differ on what they expose, and a missing one is not a reason to refuse to stream.</summary>
    private ICodecApi? TryGetCodecApi(int bitrateKbps, int fps)
    {
        try
        {
            var api = (ICodecApi)Marshal.GetObjectForIUnknown(transform.NativePointer);

            TrySetCodecProperty(api, AvEncCommonRateControlMode, RateControlModeCbr);
            TrySetCodecProperty(api, AvEncCommonMeanBitRate, (uint)(bitrateKbps * 1000));
            TrySetCodecProperty(api, AvLowLatencyMode, true);

            // A keyframe every two seconds, so a viewer joining mid-stream gets a picture within
            // that rather than waiting on whatever default the vendor happened to choose.
            TrySetCodecProperty(api, AvEncMpvGopSize, (uint)(fps * 2));

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

    /// <summary>
    /// The async MFT contract: the transform says when it wants input and when output is ready,
    /// through its event generator. Driving ProcessInput/ProcessOutput on our own schedule - how a
    /// synchronous MFT works - simply fails on hardware encoders.
    /// </summary>
    private void PumpLoop()
    {
        var clock = Stopwatch.StartNew();

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
                        FeedFrame(clock);
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

    private void FeedFrame(Stopwatch clock)
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
        else if (dueMs > nowMs)
        {
            Thread.Sleep((int)(dueMs - nowMs));
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

    /// <summary>
    /// The capture-time PTS in milliseconds, which MF carries from the input sample through to the
    /// output one - so what reaches the viewer is when the frame was grabbed, not when the encoder
    /// finished with it.
    ///
    /// Falls back to the most recently fed frame's timestamp for an encoder that emits a sample
    /// with no timestamp set at all, where reading it throws. Output order matches input order
    /// here because low-latency CBR produces no B-frames, so that fallback is off by at most the
    /// encoder's own latency rather than being meaningless.
    /// </summary>
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

    /// <summary>See the matching comment on H264Encoder.Dispose - same reasoning, mirrored here.
    /// The bounded Join is best-effort, not a guarantee the pump thread has stopped touching the
    /// encoder and its textures, and releasing those COM objects out from under a thread still
    /// inside GetEvent or a GPU call crashes natively rather than throwing something catchable.
    /// Leaking them in that rare case is much the better outcome.</summary>
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
