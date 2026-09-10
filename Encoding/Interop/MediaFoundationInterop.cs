using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Encoding.Interop;

internal static class MediaFoundationInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid Subtype;
    }

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

    internal static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref startupCalled, 1) != 0)
            return;

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

    internal static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    internal static readonly Guid VideoDecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    internal static readonly Guid MajorTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");

    internal static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    internal static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    internal static readonly Guid VideoFormatHevc = new("43564548-0000-0010-8000-00AA00389B71");

    internal static readonly Guid MftFriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    internal static List<nint> EnumerateEncoders(Guid outputSubtype, MftEnumFlags flags) =>
        EnumerateTransforms(VideoEncoderCategory, VideoFormatNv12, outputSubtype, flags);

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
            if (activates != nint.Zero)
                Marshal.FreeCoTaskMem(activates);
        }

        return result;
    }

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

    internal static nint ProcessOutput(nint transform)
    {
        int hr = TryProcessOutput(transform, out nint sample);
        if (hr < 0)
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"ProcessOutput failed (0x{hr:X8}).");

        return sample;
    }

    internal static int TryProcessOutput(nint transform, out nint sample)
    {
        nint vtable = Marshal.ReadIntPtr(transform);
        nint function = Marshal.ReadIntPtr(vtable, 25 * nint.Size);
        var call = Marshal.GetDelegateForFunctionPointer<ProcessOutputFn>(function);

        var buffer = new MftOutputDataBuffer();
        int hr = call(transform, 0, 1, ref buffer, out _);

        if (buffer.Events != nint.Zero)
            Marshal.Release(buffer.Events);

        sample = buffer.Sample;
        return hr;
    }

    internal const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);

    internal const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

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

    internal static readonly Guid MfSaD3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

    // CODECAPI_* properties. Only the handful the encoder actually sets.
    internal static readonly Guid AvEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    internal static readonly Guid AvEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    internal static readonly Guid AvEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    internal static readonly Guid AvEncMpvGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid AvLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid AvEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    internal const uint RateControlModeCbr = 0;

    internal static bool TrySetCodecProperty(ICodecApi codecApi, Guid property, object value)
    {
        if (codecApi.IsSupported(ref property) < 0)
            return false;

        object boxed = value;
        return codecApi.SetValue(ref property, ref boxed) >= 0;
    }
}
