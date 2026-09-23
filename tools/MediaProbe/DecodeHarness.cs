using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using SharpDX.MediaFoundation;
using static ScreenShare.Plugin.Encoding.Interop.MediaFoundationInterop;
using Device = SharpDX.Direct3D11.Device;

namespace MediaProbe;

internal sealed class DecodeHarness : IDisposable
{
    private readonly Device device;
    private readonly Transform transform;
    private readonly DXGIDeviceManager deviceManager;
    private readonly Nv12ToBgraConverter converter;

    private readonly Texture2D[] slots;
    private readonly ShaderResourceView[] slotLuma;
    private readonly ShaderResourceView[] slotChroma;
    private readonly ConcurrentBag<int> freeSlots = new();

    private readonly Texture2D target;
    private readonly RenderTargetView targetView;

    public int Width { get; }

    public int Height { get; }

    public string DecoderName { get; }

    public int SlotCount => slots.Length;

    public int FreeSlots => freeSlots.Count;

    public long SlotStarvations { get; private set; }

    public long StreamChanges { get; private set; }

    public string OutputShape { get; private set; } = "(nothing decoded yet)";

    public DecodeHarness(Device device, int width, int height, int slotCount, VideoCodec codec)
    {
        this.device = device;
        Width = width;
        Height = height;

        EnsureStarted();

        converter = new Nv12ToBgraConverter(device);
        slots = new Texture2D[slotCount];
        slotLuma = new ShaderResourceView[slotCount];
        slotChroma = new ShaderResourceView[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            slots[i] = CreateNv12(device, width, height);
            (slotLuma[i], slotChroma[i]) = Nv12ToBgraConverter.CreateViews(device, slots[i]);
            freeSlots.Add(i);
        }

        target = new Texture2D(device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

        targetView = new RenderTargetView(device, target);

        transform = Activate(codec, out string name);
        DecoderName = name;

        deviceManager = new DXGIDeviceManager();
        deviceManager.ResetDevice(device);
        transform.ProcessMessage(TMessageType.SetD3DManager, deviceManager.NativePointer);

        using (var inputType = new MediaType())
        {
            inputType.Set(MediaTypeAttributeKeys.MajorType, MajorTypeVideo);
            inputType.Set(MediaTypeAttributeKeys.Subtype, codec == VideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, Pack(30, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            transform.SetInputType(0, inputType, 0);
        }

        SelectNv12Output();

        using (MediaAttributes attributes = transform.Attributes)
            attributes.Set(AvLowLatencyMode, 1);

        transform.ProcessMessage(TMessageType.NotifyBeginStreaming, nint.Zero);
        transform.ProcessMessage(TMessageType.NotifyStartOfStream, nint.Zero);
    }

    private static Texture2D CreateNv12(Device device, int width, int height) =>
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
        });

    private static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    private static Transform Activate(VideoCodec codec, out string name)
    {
        Guid subtype = codec == VideoCodec.Hevc ? VideoFormatHevc : VideoFormatH264;
        List<nint> activates = EnumerateDecoders(
            subtype, MftEnumFlags.SyncMft | MftEnumFlags.Hardware | MftEnumFlags.SortAndFilter);

        if (activates.Count == 0)
            throw new NotSupportedException($"No Media Foundation decoder for {codec} on this machine.");

        nint chosen = activates[0];
        for (int i = 1; i < activates.Count; i++)
            Marshal.Release(activates[i]);

        using var activate = new Activate(chosen);
        try
        {
            name = activate.Get<string>(MftFriendlyName);
        }
        catch (Exception)
        {
            name = $"{codec} decoder";
        }

        return activate.ActivateObject<Transform>(typeof(Transform).GUID);
    }

    private void SelectNv12Output()
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

        throw new NotSupportedException("The decoder offers no NV12 output.");
    }

    public int SubmitAndDrain(byte[] accessUnit, long ptsMs)
    {
        MediaBuffer buffer = MediaFactory.CreateMemoryBuffer(accessUnit.Length);
        using (buffer)
        {
            nint destination = buffer.Lock(out _, out _);
            Marshal.Copy(accessUnit, 0, destination, accessUnit.Length);
            buffer.Unlock();
            buffer.CurrentLength = accessUnit.Length;

            using Sample sample = MediaFactory.CreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsMs * 10_000;
            transform.ProcessInput(0, sample, 0);
        }

        int presented = 0;

        while (true)
        {
            int hr = TryProcessOutput(transform.NativePointer, out nint samplePtr);

            if (hr == MfETransformNeedMoreInput)
                break;

            if (hr == MfETransformStreamChange)
            {
                StreamChanges++;
                SelectNv12Output();
                continue;
            }

            if (hr < 0)
                throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"ProcessOutput failed (0x{hr:X8}).");

            if (samplePtr == nint.Zero)
                break;

            using var outSample = new Sample(samplePtr);

            if (!freeSlots.TryTake(out int slot))
            {
                SlotStarvations++;
                continue;
            }

            CopyIntoSlot(outSample, slot);

            converter.Convert(device.ImmediateContext, slotLuma[slot], slotChroma[slot], targetView, Width, Height);
            device.ImmediateContext.Flush();
            freeSlots.Add(slot);

            presented++;
        }

        return presented;
    }

    private void CopyIntoSlot(Sample sample, int slot)
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

            if (OutputShape[0] == '(')
            {
                OutputShape =
                    $"{description.Format} {description.Width}x{description.Height}, " +
                    $"array size {description.ArraySize}";
            }

            var region = new ResourceRegion(
                0, 0, 0, Math.Min(Width, description.Width), Math.Min(Height, description.Height), 1);

            device.ImmediateContext.CopySubresourceRegion(decodedFrame, (int)subresource, region, slots[slot], 0);
        }
        finally
        {
            Marshal.ReleaseComObject(dxgiBuffer);
        }
    }

    public void Dispose()
    {
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

        for (int i = 0; i < slots.Length; i++)
        {
            slotChroma[i].Dispose();
            slotLuma[i].Dispose();
            slots[i].Dispose();
        }

        targetView.Dispose();
        target.Dispose();
        converter.Dispose();
    }
}
