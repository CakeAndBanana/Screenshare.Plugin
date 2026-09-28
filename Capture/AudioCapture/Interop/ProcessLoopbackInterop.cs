using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Capture.AudioCapture.Interop;

/// <summary>
/// Internal static class that provides interop definitions for process loopback audio capture using the Windows Core Audio APIs.
/// Microsoft's documentation on process loopback capture can be found at: https://learn.microsoft.com/en-us/windows/win32/coreaudio/process-loopback-capture
/// </summary>
internal static class ProcessLoopbackInterop
{
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const int ActivationTypeProcessLoopback = 1;

    public const int ProcessLoopbackModeIncludeTargetProcessTree = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        int GetBuffer(out nint dataBuffer, out int numFramesToRead, out NAudio.CoreAudioApi.AudioClientBufferFlags bufferFlags, out long devicePosition, out long qpcPosition);

        int ReleaseBuffer(int numFramesRead);

        int GetNextPacketSize(out int numFramesInNextPacket);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activateInterface);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
