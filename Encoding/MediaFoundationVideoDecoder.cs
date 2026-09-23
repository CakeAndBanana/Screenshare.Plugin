using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScreenShare.Plugin.Encoding.Interop;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using SharpDX.MediaFoundation;
using static ScreenShare.Plugin.Encoding.Interop.MediaFoundationInterop;
using Device = SharpDX.Direct3D11.Device;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Encoding;

public sealed class MediaFoundationVideoDecoder : IVideoDecoder
{
    private const int FeedChannelCapacity = 256;

    private const int SpareSlots = 2;

    private readonly GameAdapterDevice gameAdapterDevice;
    private readonly Transform transform;
    private readonly DXGIDeviceManager deviceManager;
    private readonly Channel<(long PtsMs, byte[] Chunk)> feedChannel;
    private readonly PresentationScheduler<int> scheduler;
    private readonly Thread decodeThread;

    private readonly Nv12ToBgraConverter converter;
    private readonly Texture2D decodedTexture;
    private readonly RenderTargetView decodedView;

    private readonly Texture2D[] slots;
    private readonly ShaderResourceView[] slotLuma;
    private readonly ShaderResourceView[] slotChroma;
    private readonly ConcurrentBag<int> freeSlots = new();

    private volatile bool running;
    private int errorRecorded;
    private long framesDecoded;
    private long droppedChunks;
    private long lastFedPtsMs;

    private readonly Stopwatch sessionStopwatch = Stopwatch.StartNew();

    public int Width { get; }

    public int Height { get; }

    public nint SharedHandle { get; }

    public string DecoderName { get; }

    public long FramesDecoded => Interlocked.Read(ref framesDecoded);

    public double AverageFps => FramesDecoded / Math.Max(sessionStopwatch.Elapsed.TotalSeconds, 0.001);

    public long DroppedChunks => Interlocked.Read(ref droppedChunks);

    public long DisplayQueueDrops => scheduler.QueueDrops;

    public long AvDriftMs => scheduler.AvDriftMs;

    public Exception? LastError { get; private set; }

    public event Action? ReadyToDisplay
    {
        add => scheduler.ReadyToDisplay += value;
        remove => scheduler.ReadyToDisplay -= value;
    }

    public MediaFoundationVideoDecoder(
        GameAdapterDevice gameAdapterDevice,
        int width,
        int height,
        int fps,
        VideoCodec codec,
        Func<long?> getAudioPtsMs,
        Func<int>? getSyncOffsetMs = null,
        int audioLatencyBudgetMs = H264Decoder.PresentationBufferMilliseconds)
    {
        this.gameAdapterDevice = gameAdapterDevice;
        Width = width;
        Height = height;

        Device device = gameAdapterDevice.Device;

        int bufferedMs = Math.Max(H264Decoder.PresentationBufferMilliseconds, audioLatencyBudgetMs)
                         + Math.Max(0, getSyncOffsetMs?.Invoke() ?? 0);

        int capacity = Math.Clamp(fps * bufferedMs / 1000, 2, 30);

        decodedTexture = SharedTextureFactory.CreateEmpty(device, width, height, renderTarget: true);
        SharedHandle = SharedTextureFactory.GetSharedHandle(decodedTexture);
        decodedView = new RenderTargetView(device, decodedTexture);
        converter = new Nv12ToBgraConverter(device);

        int slotCount = capacity + SpareSlots;
        slots = new Texture2D[slotCount];
        slotLuma = new ShaderResourceView[slotCount];
        slotChroma = new ShaderResourceView[slotCount];

        feedChannel = Channel.CreateBounded<(long, byte[])>(new BoundedChannelOptions(FeedChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        try
        {
            for (int i = 0; i < slotCount; i++)
            {
                slots[i] = CreateNv12Texture(device, width, height);
                (slotLuma[i], slotChroma[i]) = Nv12ToBgraConverter.CreateViews(device, slots[i]);
                freeSlots.Add(i);
            }

            transform = ActivateDecoder(codec, out string name);
            DecoderName = name;

            deviceManager = new DXGIDeviceManager();
            deviceManager.ResetDevice(device);
            transform.ProcessMessage(TMessageType.SetD3DManager, deviceManager.NativePointer);

            ConfigureTypes(width, height, fps, codec);

            using (MediaAttributes attributes = transform.Attributes)
                attributes.Set(AvLowLatencyMode, 1);

            transform.ProcessMessage(TMessageType.NotifyBeginStreaming, nint.Zero);
            transform.ProcessMessage(TMessageType.NotifyStartOfStream, nint.Zero);
        }
        catch
        {
            DisposeGpuResources();
            transform?.Dispose();
            deviceManager?.Dispose();
            throw;
        }

        scheduler = new PresentationScheduler<int>(
            fps,
            capacity,
            getAudioPtsMs,
            getSyncOffsetMs ?? (static () => 0),
            PresentSlot,
            ReleaseSlot,
            RecordError,
            "ScreenShare-MediaFoundationDisplay");

        running = true;
        decodeThread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "ScreenShare-MediaFoundationDecode",
        };
        decodeThread.Start();
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
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });

    private static Transform ActivateDecoder(VideoCodec codec, out string name)
    {
        Guid subtype = codec == VideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264;
        List<nint> activates = EnumerateDecoders(
            subtype,
            MftEnumFlags.Hardware | MftEnumFlags.SyncMft | MftEnumFlags.SortAndFilter);

        if (activates.Count == 0)
            throw new NotSupportedException($"No Media Foundation decoder for {codec} on this machine.");

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
            friendly = $"{codec} decoder";
        }

        name = friendly;
        var decoder = activate.ActivateObject<Transform>(typeof(Transform).GUID);

        try
        {
            using MediaAttributes attributes = decoder.Attributes;
            if (attributes.Get<int>(MfSaD3D11Aware) == 0)
                throw new NotSupportedException($"{friendly} cannot decode into D3D11 textures.");
        }
        catch (NotSupportedException)
        {
            decoder.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            decoder.Dispose();
            throw new NotSupportedException($"{friendly} did not report D3D11 support ({ex.Message}).");
        }

        return decoder;
    }

    private void ConfigureTypes(int width, int height, int fps, VideoCodec codec)
    {
        using (var inputType = new MediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
            inputType.Set(MediaTypeAttributeKeys.Subtype, codec == VideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            transform.SetInputType(0, inputType, 0);
        }

        SelectNv12OutputType();
    }

    private void SelectNv12OutputType()
    {
        for (int index = 0; transform.TryGetOutputAvailableType(0, index, out MediaType candidate); index++)
        {
            using (candidate)
            {
                if (candidate.Get(MediaTypeAttributeKeys.Subtype) != VideoFormatNv12)
                    continue;

                transform.SetOutputType(0, candidate, 0);
                return;
            }
        }

        throw new NotSupportedException("The Media Foundation decoder does not offer NV12 output.");
    }

    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    public void Feed(long ptsMs, byte[] chunk)
    {
        if (feedChannel.Reader.Count >= FeedChannelCapacity)
            Interlocked.Increment(ref droppedChunks);

        feedChannel.Writer.TryWrite((ptsMs, chunk));
    }

    public void StartDisplay() => scheduler.StartDisplay();

    private void DecodeLoop()
    {
        try
        {
            while (running && feedChannel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (running && feedChannel.Reader.TryRead(out (long PtsMs, byte[] Chunk) entry))
                {
                    Interlocked.Exchange(ref lastFedPtsMs, entry.PtsMs);
                    SubmitChunk(entry.PtsMs, entry.Chunk);
                    DrainDecodedFrames();
                }
            }
        }
        catch (Exception ex) when (running)
        {
            RecordError("DecodeLoop (network -> Media Foundation decoder)", ex);
        }
    }

    private void SubmitChunk(long ptsMs, byte[] chunk)
    {
        MediaBuffer buffer = MediaFactory.CreateMemoryBuffer(chunk.Length);
        using (buffer)
        {
            nint destination = buffer.Lock(out _, out _);
            Marshal.Copy(chunk, 0, destination, chunk.Length);
            buffer.Unlock();
            buffer.CurrentLength = chunk.Length;

            using Sample sample = MediaFactory.CreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsMs * 10_000;

            transform.ProcessInput(0, sample, 0);
        }
    }

    private void DrainDecodedFrames()
    {
        while (running)
        {
            int hr = TryProcessOutput(transform.NativePointer, out nint samplePtr);

            if (hr == MfETransformNeedMoreInput)
                return;

            if (hr == MfETransformStreamChange)
            {
                SelectNv12OutputType();
                continue;
            }

            if (hr < 0)
                throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"ProcessOutput failed (0x{hr:X8}).");

            if (samplePtr == nint.Zero)
                return;

            using var sample = new Sample(samplePtr);
            CaptureFrame(sample);
        }
    }

    private void CaptureFrame(Sample sample)
    {
        if (!freeSlots.TryTake(out int slot))
        {
            Interlocked.Increment(ref droppedChunks);
            return;
        }

        bool queued = false;
        try
        {
            using MediaBuffer buffer = sample.GetBufferByIndex(0);
            var dxgiBuffer = (IMfDxgiBuffer)Marshal.GetObjectForIUnknown(buffer.NativePointer);

            try
            {
                Guid textureIid = typeof(Texture2D).GUID;
                Marshal.ThrowExceptionForHR(dxgiBuffer.GetResource(ref textureIid, out nint texturePtr));
                Marshal.ThrowExceptionForHR(dxgiBuffer.GetSubresourceIndex(out uint subresource));

                using var decodedFrame = new Texture2D(texturePtr);
                Texture2DDescription description = decodedFrame.Description;

                var region = new ResourceRegion(
                    0, 0, 0, Math.Min(Width, description.Width), Math.Min(Height, description.Height), 1);

                lock (gameAdapterDevice.ImmediateContextLock)
                {
                    gameAdapterDevice.Device.ImmediateContext.CopySubresourceRegion(
                        decodedFrame, (int)subresource, region, slots[slot], 0);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(dxgiBuffer);
            }

            Interlocked.Increment(ref framesDecoded);

            long ptsMs = ReadSampleTime(sample) ?? Interlocked.Read(ref lastFedPtsMs);

            scheduler.Enqueue(ptsMs, slot);
            queued = true;
        }
        finally
        {
            if (!queued)
                freeSlots.Add(slot);
        }
    }

    private static long? ReadSampleTime(Sample sample)
    {
        try
        {
            long sampleTime = sample.SampleTime;
            return sampleTime > 0 ? sampleTime / 10_000 : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void PresentSlot(int slot)
    {
        lock (gameAdapterDevice.ImmediateContextLock)
        {
            DeviceContext context = gameAdapterDevice.Device.ImmediateContext;
            converter.Convert(context, slotLuma[slot], slotChroma[slot], decodedView, Width, Height);

            context.Flush();
        }
    }

    private void ReleaseSlot(int slot) => freeSlots.Add(slot);

    private void RecordError(string stage, Exception ex)
    {
        ServiceCollector.PluginLog.Error(ex, $"ScreenShare MediaFoundationVideoDecoder error in {stage}");
        if (Interlocked.CompareExchange(ref errorRecorded, 1, 0) == 0)
            LastError = ex;

        running = false;
    }

    public void Dispose()
    {
        running = false;
        feedChannel.Writer.TryComplete();
        scheduler.Stop();
        decodeThread.Join(1000);

        bool displayExited = scheduler.Join(1000);
        scheduler.Dispose();

        try
        {
            transform.ProcessMessage(TMessageType.NotifyEndOfStream, nint.Zero);
            transform.ProcessMessage(TMessageType.NotifyEndStreaming, nint.Zero);
        }
        catch (Exception)
        {
        }

        transform.Dispose();
        deviceManager.Dispose();

        if (!displayExited)
        {
            ServiceCollector.PluginLog.Warning(
                "ScreenShare's Media Foundation display thread didn't exit within 1s (likely a stalled GPU) - " +
                "leaking its textures rather than risking a use-after-dispose crash.");
            return;
        }

        DisposeGpuResources();
    }

    private void DisposeGpuResources()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            slotChroma[i]?.Dispose();
            slotLuma[i]?.Dispose();
            slots[i]?.Dispose();
        }

        converter.Dispose();
        decodedView.Dispose();
        decodedTexture.Dispose();
    }
}
