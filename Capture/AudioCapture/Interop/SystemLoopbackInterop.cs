using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ScreenShare.Plugin.Capture.AudioCapture.Interop;

/// <summary>
/// Interop definitions for the Windows Core Audio API, specifically for loopback audio capture. 
/// This class provides the necessary COM interfaces and GUIDs to interact with the system's audio devices and capture audio streams from the loopback interface.
/// Microsoft documentation: https://learn.microsoft.com/en-us/windows/win32/coreaudio/core-audio-apis-in-windows-vista
/// </summary>
internal static class SystemLoopbackInterop
{
    public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public const int ClsCtxInprocServer = 0x1;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(DataFlow dataFlow, DeviceState stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice endpoint);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        int RegisterEndpointNotificationCallback(nint client);

        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        int Activate(in Guid id, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        int OpenPropertyStore(int stgmAccess, out nint properties);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetState(out DeviceState state);
    }
}
