using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.H264;
using ScreenShare.Plugin.Encoding.Interfaces;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.MediaFoundation;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using static ScreenShare.Plugin.Encoding.Interop.MediaFoundationInterop;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenShare.Plugin.Encoding.MediaFoundation;

/// <summary>
/// Media foundation video decoder that decodes H.264 or HEVC video streams into NV12 textures and converts them to BGRA for display.
/// </summary>
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

    /// <summary>
    /// Constructs a MediaFoundationVideoDecoder that decodes H.264 or HEVC video streams into NV12 textures and converts them to BGRA for display.
    /// </summary>
    /// <param name="gameAdapterDevice">The game adapter device.</param>
    /// <param name="width">The width of the video frame.</param>
    /// <param name="height">The height of the video frame.</param>
    /// <param name="fps">The frames per second of the video stream.</param>
    /// <param name="codec">The video codec to decode.</param>
    /// <param name="getAudioPtsMs">A function to get the audio presentation timestamp in milliseconds.</param>
    /// <param name="getSyncOffsetMs">A function to get the synchronization offset in milliseconds.</param>
    /// <param name="audioLatencyBudgetMs">The audio latency budget in milliseconds.</param>
    public MediaFoundationVideoDecoder(
        GameAdapterDevice gameAdapterDevice,
        int width,
        int height,
        int fps,
        SupportedVideoCodec codec,
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

    /// <summary>
    /// Create an NV12 texture with the specified width and height.
    /// </summary>
    /// <param name="device">The Direct3D device.</param>
    /// <param name="width">The width of the texture.</param>
    /// <param name="height">The height of the texture.</param>
    /// <returns>The created NV12 texture.</returns>
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

    /// <summary>
    /// Active a Media Foundation decoder for the specified codec.
    /// </summary>
    /// <param name="codec">The video codec.</param>
    /// <param name="name">The name of the decoder.</param>
    /// <returns>The activated decoder.</returns>
    /// <exception cref="NotSupportedException">Not supported codec.</exception>
    private static Transform ActivateDecoder(SupportedVideoCodec codec, out string name)
    {
        Guid subtype = codec == SupportedVideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264;
        List<nint> activates = EnumerateDecoders(
            subtype,
            MftEnumFlags.Hardware | MftEnumFlags.SyncMft | MftEnumFlags.SortAndFilter);

        if (activates.Count == 0)
            throw new NotSupportedException($"No Media Foundation decoder for {codec} on this machine.");

        nint chosen = activates[0];
        for (int i = 1; i < activates.Count; i++)
            Marshal.Release(activates[i]);

        using var activate = new Activate(chosen);

        // Get the friendly name of the decoder, or fall back to a generic name if not available.
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

    /// <summary>
    /// Configures the input and output types for the Media Foundation decoder.
    /// </summary>
    /// <param name="width">The width of the video.</param>
    /// <param name="height">The height of the video.</param>
    /// <param name="fps">The frame rate of the video.</param>
    /// <param name="codec">The video codec.</param>
    private void ConfigureTypes(int width, int height, int fps, SupportedVideoCodec codec)
    {
        using (var inputType = new MediaType())
        {
            // Set the input type for the decoder.
            inputType.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
            inputType.Set(MediaTypeAttributeKeys.Subtype, codec == SupportedVideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            transform.SetInputType(0, inputType, 0);
        }

        // Set the output type for the decoder to NV12.
        SelectNv12OutputType();
    }

    /// <summary>
    /// Selects the NV12 output type for the Media Foundation decoder.
    /// </summary>
    /// <exception cref="NotSupportedException">Not supported when the decoder does not offer NV12 output.</exception>
    private void SelectNv12OutputType()
    {
        // Enumerate the available output types and select NV12 if available.
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

    /// <summary>
    /// Packs two integers into a long value, with the high integer in the upper 32 bits and the low integer in the lower 32 bits.
    /// </summary>
    /// <param name="high">The high integer.</param>
    /// <param name="low">The low integer.</param>
    /// <returns>The packed long value.</returns>
    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    /// <summary>
    /// Feeds the decoder with a chunk of encoded video data and its corresponding presentation timestamp in milliseconds.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp in milliseconds.</param>
    /// <param name="chunk">The chunk of encoded video data.</param>
    public void Feed(long ptsMs, byte[] chunk)
    {
        if (feedChannel.Reader.Count >= FeedChannelCapacity)
            Interlocked.Increment(ref droppedChunks);

        feedChannel.Writer.TryWrite((ptsMs, chunk));
    }

    /// <summary>
    /// Starts the display of decoded frames.
    /// </summary>
    public void StartDisplay() => scheduler.StartDisplay();

    /// <summary>
    /// Decodes the video chunks fed into the decoder and processes the output frames.
    /// </summary>
    private void DecodeLoop()
    {
        try
        {
            // Wait for chunks to be available in the feed channel and process them.
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

    /// <summary>
    /// Submits a chunk of encoded video data to the Media Foundation decoder with the specified presentation timestamp in milliseconds.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp in milliseconds.</param>
    /// <param name="chunk">The chunk of encoded video data.</param>
    private void SubmitChunk(long ptsMs, byte[] chunk)
    {
        MediaBuffer buffer = MediaFactory.CreateMemoryBuffer(chunk.Length);
        using (buffer)
        {
            // Lock the buffer and copy the chunk data into it.
            nint destination = buffer.Lock(out _, out _);
            Marshal.Copy(chunk, 0, destination, chunk.Length);
            buffer.Unlock();
            buffer.CurrentLength = chunk.Length;

            // Create a sample and add the buffer to it, then set the sample time and submit it to the decoder. :D
            using Sample sample = MediaFactory.CreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsMs * 10_000;

            transform.ProcessInput(0, sample, 0);
        }
    }

    /// <summary>
    /// Drains the decoded frames from the Media Foundation decoder and captures them for display.
    /// </summary>
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

    /// <summary>
    /// Captures a decoded frame from the Media Foundation decoder and schedules it for display.
    /// </summary>
    /// <param name="sample">The decoded sample.</param>
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

            // Copy the decoded frame from the DXGI buffer to the corresponding slot texture.
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

    /// <summary>
    /// Reads the sample time from a Media Foundation sample and converts it to milliseconds.
    /// </summary>
    /// <param name="sample">The Media Foundation sample.</param>
    /// <returns>The sample time in milliseconds, or null if the time is not available.</returns>
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

    /// <summary>
    /// Presents a decoded frame from the specified slot by converting it from NV12 to BGRA and rendering it to the decoded view.
    /// </summary>
    /// <param name="slot">The slot containing the decoded frame.</param>
    private void PresentSlot(int slot)
    {
        lock (gameAdapterDevice.ImmediateContextLock)
        {
            DeviceContext context = gameAdapterDevice.Device.ImmediateContext;
            converter.Convert(context, slotLuma[slot], slotChroma[slot], decodedView, Width, Height);

            context.Flush();
        }
    }

    /// <summary>
    /// Releases slots back to the slot pool.
    /// </summary>
    /// <param name="slot">The slot to release.</param>
    private void ReleaseSlot(int slot) => freeSlots.Add(slot);

    /// <summary>
    /// Records an error that occurred during the decoding or display process, logs it, and stops the decoder from running.
    /// </summary>
    /// <param name="stage">The stage where the error occurred.</param>
    /// <param name="ex">The exception that occurred.</param>
    private void RecordError(string stage, Exception ex)
    {
        ServiceCollector.PluginLog.Error(ex, $"ScreenShare MediaFoundationVideoDecoder error in {stage}");
        if (Interlocked.CompareExchange(ref errorRecorded, 1, 0) == 0)
            LastError = ex;

        running = false;
    }

    /// <summary>
    /// Disposes of the MediaFoundationVideoDecoder
    /// </summary>
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

    /// <summary>
    /// Disposes of the gpu resources used by the MediaFoundationVideoDecoder.
    /// </summary>
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
