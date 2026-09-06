using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Encoding.Interop;

/// <summary>
/// The two pieces of Media Foundation that SharpDX.MediaFoundation does not usably provide, and
/// which the hardware encoder path cannot do without.
///
/// 1. <c>MFTEnumEx</c>. SharpDX marks its wrapper internal, and its public <c>MFTEnum2</c> has a
///    generated signature that does not match the native allocation semantics - passing it a
///    pre-sized array access-violates rather than failing cleanly. Measured, not assumed.
/// 2. <c>ICodecAPI</c>, which is not wrapped at all. It is the entire reason for moving off the
///    ffmpeg encoder: it changes bitrate on a RUNNING encoder, where ffmpeg needs the process
///    respawned and a visible hitch.
///
/// Everything else - IMFTransform, IMFMediaType, IMFSample, IMFDXGIDeviceManager - comes from
/// SharpDX.MediaFoundation as normal.
/// </summary>
internal static class MediaFoundationInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid Subtype;
    }

    /// <summary>
    /// MFT_ENUM_FLAG, matching mftransform.h.
    ///
    /// These were wrong here until a decoder probe found it: several values were transposed, so
    /// "hardware async" actually asked for hardware plus field-of-use. The encoder path happened to
    /// work anyway - hardware encoders are async, and the extra flag only widens the set - which is
    /// exactly why it went unnoticed. The decoder path does not survive it: asking for the wrong
    /// flags there returns nothing at all.
    /// </summary>
    [Flags]
    internal enum MftEnumFlags : uint
    {
        SyncMft = 0x00000001,
        AsyncMft = 0x00000002,
        Hardware = 0x00000004,
        FieldOfUse = 0x00000008,
        LocalMft = 0x00000010,
        TranscodeOnly = 0x00000020,
        SortAndFilter = 0x00000040,
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    private static int startupCalled;

    /// <summary>
    /// Initialises the Media Foundation platform, which every other call here requires.
    ///
    /// Never balanced with MFShutdown. MFStartup is reference-counted per process and the game
    /// keeps running long after a stream ends, so shutting down would only risk pulling the
    /// platform out from under something else in the process; leaving it initialised costs
    /// nothing. Idempotent, because a plugin reload starts a fresh assembly load context and would
    /// otherwise call it again from scratch.
    /// </summary>
    internal static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref startupCalled, 1) != 0)
            return;

        // MF_VERSION as the SDK headers define it, with MFSTARTUP_FULL.
        int hr = MFStartup(0x00020070, 0);
        if (hr < 0)
        {
            Interlocked.Exchange(ref startupCalled, 0);
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"MFStartup failed (0x{hr:X8}).");
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        Guid category,
        MftEnumFlags flags,
        ref MftRegisterTypeInfo inputType,
        ref MftRegisterTypeInfo outputType,
        out nint activates,
        out uint count);

    /// <summary>MFT_CATEGORY_VIDEO_ENCODER.</summary>
    internal static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    /// <summary>MFT_CATEGORY_VIDEO_DECODER.</summary>
    internal static readonly Guid VideoDecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    /// <summary>MFMediaType_Video.</summary>
    internal static readonly Guid MajorTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");

    internal static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    internal static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    internal static readonly Guid VideoFormatHevc = new("43564548-0000-0010-8000-00AA00389B71");

    /// <summary>MFT_FRIENDLY_NAME_Attribute - reported in the UI so the host can see which encoder
    /// is actually in use, which is more informative than the GPU vendor we used to guess at.</summary>
    internal static readonly Guid MftFriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    /// <summary>
    /// Enumerates encoder MFTs producing <paramref name="outputSubtype"/> from NV12, best first.
    ///
    /// Returns raw IMFActivate pointers with a reference held; the caller owns them and must
    /// Release each one. Deliberately not wrapped in SharpDX ComObjects here - the caller activates
    /// exactly one and releases the rest, and pairing that with SharpDX's own lifetime handling
    /// would make ownership harder to follow, not easier.
    /// </summary>
    internal static List<nint> EnumerateEncoders(Guid outputSubtype, MftEnumFlags flags) =>
        EnumerateTransforms(VideoEncoderCategory, VideoFormatNv12, outputSubtype, flags);

    /// <summary>Decoder MFTs taking <paramref name="inputSubtype"/> and producing NV12, best first.
    /// Same ownership contract as <see cref="EnumerateEncoders"/>.</summary>
    internal static List<nint> EnumerateDecoders(Guid inputSubtype, MftEnumFlags flags) =>
        EnumerateTransforms(VideoDecoderCategory, inputSubtype, VideoFormatNv12, flags);

    private static List<nint> EnumerateTransforms(
        Guid category, Guid inputSubtype, Guid outputSubtype, MftEnumFlags flags)
    {
        var input = new MftRegisterTypeInfo { MajorType = MajorTypeVideo, Subtype = inputSubtype };
        var output = new MftRegisterTypeInfo { MajorType = MajorTypeVideo, Subtype = outputSubtype };

        int hr = MFTEnumEx(category, flags, ref input, ref output, out nint activates, out uint count);
        if (hr < 0)
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"MFTEnumEx failed (0x{hr:X8}).");

        var result = new List<nint>((int)count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                nint activate = Marshal.ReadIntPtr(activates, i * nint.Size);
                if (activate != nint.Zero)
                    result.Add(activate);
            }
        }
        finally
        {
            // The array itself came from CoTaskMemAlloc; the entries it held are now owned by the
            // caller through the list above.
            if (activates != nint.Zero)
                Marshal.FreeCoTaskMem(activates);
        }

        return result;
    }

    /// <summary>
    /// Codec configuration on a live encoder.
    ///
    /// Every method before <see cref="SetValue"/> has to be declared even though only two are
    /// called: a COM interface is a vtable, and the slots must line up. They are declared with
    /// their real signatures rather than as placeholders so a future caller cannot invoke one and
    /// corrupt the stack.
    /// </summary>
    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICodecApi
    {
        [PreserveSig] int IsSupported(ref Guid api);

        [PreserveSig] int IsModifiable(ref Guid api);

        [PreserveSig] int GetParameterRange(
            ref Guid api,
            [MarshalAs(UnmanagedType.Struct)] out object min,
            [MarshalAs(UnmanagedType.Struct)] out object max,
            [MarshalAs(UnmanagedType.Struct)] out object step);

        [PreserveSig] int GetParameterValues(ref Guid api, out nint values, out uint count);

        [PreserveSig] int GetDefaultValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig] int GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig] int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MftOutputDataBuffer
    {
        public int StreamId;
        public nint Sample;
        public int Status;
        public nint Events;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ProcessOutputFn(nint self, int flags, int count, ref MftOutputDataBuffer buffers, out int status);

    /// <summary>
    /// Calls IMFTransform::ProcessOutput directly, returning the encoder's output sample.
    ///
    /// SharpDX cannot be used for this one call: it marshals MFT_OUTPUT_DATA_BUFFER INTO the
    /// transform but never back out, so the sample pointer the encoder writes lands in a native
    /// struct that is then discarded and the managed PSample is always null. Measured against a
    /// real NVIDIA encoder MFT, which reported PROVIDES_SAMPLES and then appeared to emit nothing
    /// at all - the frames were being produced and thrown away.
    ///
    /// Slot 25 is three IUnknown methods followed by the twenty-two IMFTransform ones, with
    /// ProcessOutput last. Returns zero when the transform had nothing ready.
    /// </summary>
    internal static nint ProcessOutput(nint transform)
    {
        int hr = TryProcessOutput(transform, out nint sample);
        if (hr < 0)
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"ProcessOutput failed (0x{hr:X8}).");

        return sample;
    }

    /// <summary>
    /// ProcessOutput returning its HRESULT instead of throwing.
    ///
    /// The decoder needs this because two of its failure codes are not failures at all - they are
    /// how a synchronous MFT says "feed me more" and "my output format changed". Turning those into
    /// exceptions would mean throwing several times per second through the normal decode path.
    /// </summary>
    internal static int TryProcessOutput(nint transform, out nint sample)
    {
        nint vtable = Marshal.ReadIntPtr(transform);
        nint function = Marshal.ReadIntPtr(vtable, 25 * nint.Size);
        var call = Marshal.GetDelegateForFunctionPointer<ProcessOutputFn>(function);

        var buffer = new MftOutputDataBuffer();
        int hr = call(transform, 0, 1, ref buffer, out _);

        // The events collection is optional and almost always absent, but it is an out-reference
        // when present and leaking one per frame would be a slow drip for the whole session.
        if (buffer.Events != nint.Zero)
            Marshal.Release(buffer.Events);

        sample = buffer.Sample;
        return hr;
    }

    /// <summary>The transform has consumed everything it was given and has nothing to emit yet -
    /// the ordinary state of a decoder between frames, not an error.</summary>
    internal const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);

    /// <summary>The transform's output format changed and must be renegotiated before it will emit
    /// anything more. A decoder raises this once it has parsed the stream's real parameters, which
    /// may not match what it was told up front.</summary>
    internal const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

    /// <summary>
    /// Reaches the D3D11 texture inside a decoder's output sample, without copying it to system
    /// memory the way IMFSample::ConvertToContiguousBuffer would - which is the entire point of
    /// decoding on the GPU in the first place.
    ///
    /// <c>GetSubresourceIndex</c> matters: hardware decoders hand back slices of a texture ARRAY,
    /// so the texture alone does not identify the frame. Copying subresource 0 unconditionally
    /// yields whichever frame happens to live in the first slice.
    /// </summary>
    [ComImport]
    [Guid("e7174cfa-1c9e-48b1-8866-626226bfc258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMfDxgiBuffer
    {
        [PreserveSig] int GetResource(ref Guid riid, out nint resource);

        [PreserveSig] int GetSubresourceIndex(out uint subresource);

        [PreserveSig] int GetUnknown(ref Guid guid, ref Guid riid, out nint unknown);

        [PreserveSig] int SetUnknown(ref Guid guid, nint unknown);
    }

    /// <summary>MF_SA_D3D11_AWARE - set on decoders that can be bound to a D3D11 device and will
    /// then decode into GPU textures. A decoder without it works, but only in system memory, which
    /// would leave the CPU round trip exactly where it is.</summary>
    internal static readonly Guid MfSaD3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

    // CODECAPI_* properties. Only the handful the encoder actually sets.
    internal static readonly Guid AvEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    internal static readonly Guid AvEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    internal static readonly Guid AvEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    internal static readonly Guid AvEncMpvGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid AvLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid AvEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    /// <summary>eAVEncCommonRateControlMode_CBR - constant bitrate, which is what a live stream on a
    /// fixed uplink budget wants; the variable modes save bytes on easy scenes and then overshoot on
    /// hard ones, which is exactly the wrong shape for a link that is already the bottleneck.</summary>
    internal const uint RateControlModeCbr = 0;

    /// <summary>Sets a codec property, returning false if the encoder does not support it rather
    /// than throwing. Support genuinely varies between vendors, and a missing optional property is
    /// not a reason to fail the whole stream - the caller logs and carries on.</summary>
    internal static bool TrySetCodecProperty(ICodecApi codecApi, Guid property, object value)
    {
        if (codecApi.IsSupported(ref property) < 0)
            return false;

        object boxed = value;
        return codecApi.SetValue(ref property, ref boxed) >= 0;
    }
}
