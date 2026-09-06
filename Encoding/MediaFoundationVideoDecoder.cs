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

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Hardware video decoder built on Media Foundation, replacing the ffmpeg child process on the
/// viewer's side.
///
/// The cost this removes is larger than the encoder's was. The ffmpeg decoder has the child process
/// write raw BGRA down a pipe - roughly 110 MB/s at 720p30, and four times that at 1080p60 - which
/// is then read back into managed memory, buffered, and uploaded to the GPU with UpdateSubresource.
/// Every one of those bytes came off the GPU's own decoder and is going straight back to it. Here
/// the decoded frame is a D3D11 NV12 texture from the start: it is copied into a buffered slot,
/// converted to BGRA by a shader when its time comes, and never enters system memory at all.
///
/// Jitter buffering and audio-clock pacing are NOT reimplemented - they live in
/// <see cref="PresentationScheduler{TFrame}"/> and are shared verbatim with the ffmpeg decoder.
/// What differs is only what a buffered frame IS: a slot index into a ring of NV12 textures rather
/// than a managed byte array.
///
/// Unlike the encoder, hardware video decoder MFTs are SYNCHRONOUS: input goes in and output is
/// pulled until the transform says it needs more. Two of its error codes are ordinary control flow
/// on that path - "need more input" every frame, and "stream change" once the decoder has parsed
/// the real stream parameters, which routinely differ from what it was told up front.
/// </summary>
public sealed class MediaFoundationVideoDecoder : IVideoDecoder
{
    private const int FeedChannelCapacity = 256;

    /// <summary>Spare NV12 slots beyond what the jitter buffer can hold: one for the frame being
    /// presented and one being decoded into, so the decoder never has to stall waiting for the
    /// display to hand a slot back.</summary>
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

        // Same buffer depth reasoning as the ffmpeg decoder, and it has to stay the same: audio is
        // the master clock, so if the clock may sit 500ms behind live this buffer must still hold
        // frames that old or the display asks for frames already discarded.
        int bufferedMs = Math.Max(H264Decoder.PresentationBufferMilliseconds, audioLatencyBudgetMs)
                         + Math.Max(0, getSyncOffsetMs?.Invoke() ?? 0);

        // Capped lower than the ffmpeg path's 45. These slots are VRAM rather than system memory:
        // NV12 is 1.5 bytes per pixel, so 30 slots is about 41MB at 720p and 93MB at 1080p, which
        // is the point where holding more video back stops being free.
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

            // Input type first here, the opposite of the encoder: a decoder derives what it can
            // produce from what it is being given, not the other way round.
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

    /// <summary>
    /// Picks a decoder MFT for the codec.
    /// </summary>
    /// <remarks>
    /// Both hardware and synchronous software MFTs are considered, and the distinction matters less
    /// than it sounds: Microsoft's own H.264 decoder MFT is registered as software but decodes on
    /// the GPU through DXVA once it is handed a D3D manager. What actually decides whether a frame
    /// stays on the GPU is MF_SA_D3D11_AWARE, which is checked here - a decoder without it would
    /// hand back system-memory frames and leave the CPU round trip exactly where it is, so it is
    /// rejected in favour of falling back to ffmpeg.
    ///
    /// HEVC is where this genuinely may find nothing: H.264 decode ships with Windows, HEVC depends
    /// on the Store's "HEVC Video Extensions" being installed. That is a per-codec fallback, not a
    /// reason to refuse the stream.
    /// </remarks>
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
            // The attribute simply not being present means the same thing as it being zero.
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

    /// <summary>
    /// Picks the decoder's NV12 output from the types it offers, rather than describing one.
    ///
    /// Asking for a constructed type gets refused: a decoder's output type carries fields only it
    /// knows - stride, the real frame size once the stream's parameters are parsed, interlace mode -
    /// and a type built here would disagree on at least one. Enumerating and choosing is also what
    /// makes the stream-change path work, since the same call re-runs against the new list.
    /// </summary>
    private void SelectNv12OutputType()
    {
        for (int index = 0; transform.TryGetOutputAvailableType(0, index, out MediaType candidate); index++)
        {
            using (candidate)
            {
                if (candidate.Get<Guid>(MediaTypeAttributeKeys.Subtype) != VideoFormatNv12)
                    continue;

                transform.SetOutputType(0, candidate, 0);
                return;
            }
        }

        throw new NotSupportedException("The Media Foundation decoder does not offer NV12 output.");
    }

    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    /// <summary>Never blocks - safe to call from the network receive thread.</summary>
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

    /// <summary>Pulls every frame the decoder has ready. A decoder can emit more than one output
    /// for a single input (or none at all), so this drains rather than assuming a 1:1 exchange.</summary>
    private void DrainDecodedFrames()
    {
        while (running)
        {
            int hr = TryProcessOutput(transform.NativePointer, out nint samplePtr);

            if (hr == MfETransformNeedMoreInput)
                return;

            if (hr == MfETransformStreamChange)
            {
                // The decoder has parsed the stream and its output format is not what it first
                // advertised. Renegotiating is mandatory - it emits nothing at all until this is
                // done - and is normal at stream start, not a failure.
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

    /// <summary>Copies the decoded texture into a buffered slot and queues it. The copy is what
    /// lets the decoder's own surface go straight back to it - a hardware decoder has only a
    /// handful, and holding them for the length of the jitter buffer would starve it within a few
    /// frames.</summary>
    private void CaptureFrame(Sample sample)
    {
        if (!freeSlots.TryTake(out int slot))
        {
            // Every slot is queued or on screen. Dropping the newest frame is the least-bad option:
            // the alternative is stalling the decode thread, which backs up into the network queue.
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

                // Two things measured on a real decoder, both of which break a naive copy:
                //
                // - The output texture is PADDED. Asking for 640x360 gets a 640x368 surface, the
                //   height rounded up to the codec's macroblock alignment, with the real picture in
                //   the top-left. Copying the whole surface into a 640x360 slot is an invalid copy,
                //   so the region is stated explicitly rather than passing null.
                // - It is a texture ARRAY - eleven slices here - and the subresource index is which
                //   frame this is. Copying slice 0 every time yields whatever happens to be in the
                //   first slot, which is not the frame just decoded.
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

            // The decoder carries the PTS through from the input sample, but not every decoder sets
            // one; the most recently fed chunk is the same fallback the ffmpeg path uses.
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

    /// <summary>Converts one buffered NV12 slot into the shared BGRA texture the viewer samples.
    /// This is the only place the format changes, and it happens once per frame actually shown -
    /// not once per frame decoded.</summary>
    private void PresentSlot(int slot)
    {
        lock (gameAdapterDevice.ImmediateContextLock)
        {
            DeviceContext context = gameAdapterDevice.Device.ImmediateContext;
            converter.Convert(context, slotLuma[slot], slotChroma[slot], decodedView, Width, Height);

            // decodedTexture is a classic (non-keyed-mutex) shared resource opened on a SEPARATE
            // device by the game's own render thread, so without an explicit Flush this draw can
            // sit queued in the driver's command buffer indefinitely, invisible to that device
            // until something unrelated triggers a flush. Same reasoning as the ffmpeg path's
            // UpdateSubresource, and the same symptom if it is missing: frames arrive sometimes.
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

    /// <summary>See the matching comment on H264Decoder.Dispose - same reasoning. The display
    /// thread is the one that touches the shared texture, so its exit specifically gates whether
    /// releasing these GPU resources is safe; leaking them when it is still stuck is much better
    /// than a native use-after-dispose crash.</summary>
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
            // Already torn down, or in a bad state - it is being released either way.
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
