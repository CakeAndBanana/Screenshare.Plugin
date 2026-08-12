using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ScreenShare.Plugin.Capture.AudioCapture.Interop;

/// <summary>
/// Raw COM interop for WASAPI's device-enumeration APIs (IMMDeviceEnumerator/IMMDevice) - needed
/// to get the default render device to loopback-capture from. NAudio 2.3.0's own equivalents
/// (MMDeviceEnumeratorComObject/IMMDeviceEnumerator/IMMDevice, all in
/// NAudio.CoreAudioApi.Interfaces) are `internal`, so <see cref="SystemAudioCapture"/> couldn't
/// use them even indirectly via NAudio's own public MMDeviceEnumerator/MMDevice wrapper classes
/// (whose constructors/methods route through those internal interface types).
///
/// This isn't just "can't reference an internal type" tidiness - a real, confirmed failure was
/// traced to NAudio's own <c>new MMDeviceEnumeratorComObject()</c> COM activation throwing
/// InvalidCastException with the SAME type name on both sides of the cast ("cannot cast
/// MMDeviceEnumeratorComObject to MMDeviceEnumeratorComObject"), reproducing consistently even
/// after a full PC restart (ruling out a stale-reload explanation) - the classic .NET signature
/// of an assembly-identity mismatch for a `[ComImport]` class specifically, apparently not fully
/// reliable in this plugin's hosting environment for reasons not fully root-caused. Declaring
/// these interfaces here and activating the COM class via Type.GetTypeFromCLSID +
/// Activator.CreateInstance, then casting to OUR OWN interface declaration, sidesteps that
/// failure mode entirely: COM interface casting is resolved via QueryInterface, keyed by the
/// interface's GUID - not by .NET assembly/type identity - so it doesn't matter which assembly
/// declared the interface type being cast to, only that the GUID matches.
///
/// GUIDs and method order verified by decompiling NAudio 2.3.0's own internal declarations
/// (identical) before being trusted, matching how <see cref="ProcessLoopbackInterop"/> was
/// verified earlier. Reuses NAudio's existing public types wherever possible rather than
/// redeclaring them: <see cref="DataFlow"/>, <see cref="Role"/>, <see cref="DeviceState"/>,
/// <see cref="ProcessLoopbackInterop.IAudioCaptureClient"/>, and
/// <see cref="NAudio.CoreAudioApi.Interfaces.IAudioClient"/>/<see cref="AudioClientShareMode"/>/
/// <see cref="AudioClientStreamFlags"/> are all already public on this package.
/// </summary>
internal static class SystemLoopbackInterop
{
    /// <summary>CLSID_MMDeviceEnumerator - the Windows COM class that implements
    /// IMMDeviceEnumerator, activated directly via Type.GetTypeFromCLSID rather than through
    /// NAudio's own internal [ComImport] class declaration for it.</summary>
    public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>CLSCTX_INPROC_SERVER. Plain int rather than a named enum - NAudio doesn't expose
    /// one for this either (its own internal IMMDevice.Activate signature takes a bare integer
    /// too, confirmed by decompile), and the marshaled value is identical either way.</summary>
    public const int ClsCtxInprocServer = 0x1;

    /// <summary>Only GetDefaultAudioEndpoint is ever actually called - the other four members
    /// exist purely to keep this interface's vtable layout identical to the real
    /// IMMDeviceEnumerator (COM vtables are positional, so every slot before and after the one
    /// method we use still has to exist with the right calling shape), so their parameter/return
    /// types are simplified to nint/plain values rather than fully modeled.</summary>
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

    /// <summary>Only Activate is ever actually called - see IMMDeviceEnumerator's doc comment for
    /// why the other members are still declared (vtable layout) but simplified.</summary>
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
