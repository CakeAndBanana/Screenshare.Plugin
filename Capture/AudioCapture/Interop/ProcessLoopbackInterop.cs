using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Capture.AudioCapture.Interop;

/// <summary>
/// Raw COM interop for Windows' "process loopback" audio capture (Windows 10 build 20348+):
/// activating a virtual audio-render endpoint scoped to one process (and, by default, its child
/// process tree) instead of the whole system mix - what lets this plugin capture "just the
/// browser" instead of everything currently playing.
///
/// NAudio 2.3.0 (this project's existing WASAPI dependency, used unchanged by
/// <see cref="SystemAudioCapture"/>) already has equivalent interop internally - decompiled and
/// confirmed identical GUIDs/signatures below - but marked `internal`, so it can't be reused
/// directly. Rather than pull in NAudio 3 (a prerelease with sweeping breaking changes across the
/// whole WASAPI surface) just for this, these are re-declared here, verified three ways before
/// being trusted: (1) decompiling NAudio 2.3.0's own internal equivalents (same GUIDs), (2)
/// Microsoft's official audioclientactivationparams.h reference docs, (3) NAudio 3's real,
/// merged (June 2026) implementation of the same feature. <see cref="IAudioClient"/>,
/// <see cref="NAudio.CoreAudioApi.AudioClientShareMode"/>, <see cref="NAudio.CoreAudioApi.AudioClientStreamFlags"/>,
/// <see cref="NAudio.CoreAudioApi.AudioClientBufferFlags"/>, and NAudio's own public
/// <see cref="NAudio.CoreAudioApi.Interfaces.PropVariant"/>/<see cref="NAudio.CoreAudioApi.Interfaces.Blob"/>
/// are reused directly instead of redeclaring those too, since they're already public on the
/// package this project depends on.
/// </summary>
internal static class ProcessLoopbackInterop
{
    /// <summary>The special virtual-device path activated instead of a real device ID to request
    /// process-loopback rather than default-device loopback.</summary>
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    /// <summary>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.</summary>
    public const int ActivationTypeProcessLoopback = 1;

    /// <summary>PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE - captures the target process
    /// AND its children, which matters for browsers specifically: the actual audio usually
    /// renders in a child/renderer process, not the main one.</summary>
    public const int ProcessLoopbackModeIncludeTargetProcessTree = 0;

    /// <summary>
    /// AUDIOCLIENT_ACTIVATION_PARAMS with its DUMMYUNIONNAME collapsed to its one real member
    /// (AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS) - Microsoft's union only ever has this one arm today,
    /// so a flat sequential struct is layout-identical to the real (union-typed) one: 4-byte
    /// ActivationType, then the 4-byte TargetProcessId/4-byte ProcessLoopbackMode pair that make up
    /// AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS, 12 bytes total, no padding (every field is 4-byte
    /// aligned already).
    /// </summary>
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

    /// <summary>Not [PreserveSig] - a failing HRESULT throws automatically, matching how NAudio's
    /// own internal copy of this same P/Invoke declaration is used.</summary>
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
