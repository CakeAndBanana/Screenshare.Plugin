using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Capture.ScreenCapture.Interop;

/// <summary>
/// Raw COM interop for the Windows.Graphics.Capture WinRT API (Windows 10 1903+), used to capture
/// one specific top-level window instead of a whole monitor - what DXGI Desktop Duplication
/// (<see cref="DesktopDuplicationCapture"/>) can't do, since it only ever captures a full output.
///
/// Deliberately classic <c>[ComImport]</c> interop rather than a CsWinRT-projected reference:
/// tried adding the <c>Microsoft.Windows.SDK.Contracts</c> NuGet package first (the route
/// Microsoft's own official samples use for plain desktop apps), but the modern .NET 5+ SDK
/// hard-rejects any direct <c>.winmd</c> reference (NETSDK1130, no override) unless the project's
/// TargetFramework carries a specific Windows-version suffix (e.g. <c>net10.0-windows10.0.19041.0</c>)
/// - a repo-wide change this project's TFM shouldn't take on just for one capture mode. WinRT
/// interfaces are ABI-compatible with classic COM (IInspectable extends IUnknown by exactly 3
/// methods - GetIids/GetRuntimeClassName/GetTrustLevel - before an interface's own methods begin),
/// so they can be declared and called the same way as any other COM interface; those 3 methods are
/// declared below purely as vtable-slot placeholders and never actually invoked.
///
/// Every GUID and method order here was cross-verified against three independent sources before
/// being trusted, not derived from memory: (1) Microsoft's own official C# sample
/// (Composition.WindowsRuntimeHelpers/CaptureHelper.cs and Direct3D11Helper.cs, from the
/// microsoft/Windows.UI.Composition-Win32-Samples repo) for IGraphicsCaptureItemInterop and
/// IDirect3DDxgiInterfaceAccess - those two interfaces are declared exactly as that sample
/// declares them, verbatim, rather than re-derived; (2) Microsoft's own Learn docs for
/// RoGetActivationFactory; (3) a real, independent, non-CsWinRT interop implementation
/// (github.com/TKMAX777/winapi's Go winrt package) which declares full raw vtable structs for
/// every other interface used here (IGraphicsCaptureItem, IDirect3D11CaptureFramePool,
/// IDirect3D11CaptureFramePoolStatics2, IGraphicsCaptureSession, IDirect3D11CaptureFrame) - used
/// specifically to confirm exact method ORDER, which is the one thing docs alone don't reliably
/// give and which is fatal to get wrong (wrong vtable slot = calling the wrong method = crash).
/// For everything not taken verbatim from a proven C# declaration, every method is declared with
/// explicit <c>[PreserveSig]</c> + an <c>int</c> HRESULT return + explicit <c>out</c> parameters
/// matching the real native signature, rather than relying on .NET's "trailing out-param becomes
/// the C# return value" auto-conversion convention for signatures nobody has already verified.
/// </summary>
internal static class WindowsGraphicsCaptureInterop
{
    public const string Direct3D11CaptureFramePoolClassName = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";

    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM's numeric value - DirectXPixelFormat's values are
    /// documented to correspond 1:1 with DXGI_FORMAT, confirmed against the Go reference's own
    /// enum (which lists the same value under the same name).</summary>
    public const int PixelFormatB8G8R8A8UIntNormalized = 87;

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeInt32
    {
        public int Width;
        public int Height;
    }

    // --- IGraphicsCaptureItemInterop (Win32-side bridge, classic IUnknown - not WinRT itself).
    // Declared verbatim from Microsoft's own CaptureHelper.cs sample, including its choice not to
    // use [PreserveSig] - the trailing "out void** result" native parameter becomes the C# return
    // value under that convention, proven by that shipped sample rather than assumed here. ---

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);

        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    // --- IGraphicsCaptureItem (WinRT) ---

    public static readonly Guid IGraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItem
    {
        // IInspectable
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        // IGraphicsCaptureItem itself
        [PreserveSig] int GetDisplayName(out nint hstring); // unused - reserves this vtable slot
        [PreserveSig] int GetSize(out SizeInt32 size);
    }

    // --- IDirect3D11CaptureFramePoolStatics2 (activation factory - CreateFreeThreaded) ---

    public static readonly Guid IDirect3D11CaptureFramePoolStatics2Iid = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");

    [ComImport]
    [Guid("589B103F-6BBC-5DF5-A991-02E28B3B66D5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3D11CaptureFramePoolStatics2
    {
        // IInspectable
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int CreateFreeThreaded([In] nint device, [In] int pixelFormat, [In] int numberOfBuffers, [In] SizeInt32 size, out nint framePool);
    }

    // --- IDirect3D11CaptureFramePool (instance interface) ---

    [ComImport]
    [Guid("24EB6D22-1975-422E-82E7-780DBD8DDF24")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3D11CaptureFramePool
    {
        // IInspectable
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        // IDirect3D11CaptureFramePool itself
        [PreserveSig] int Recreate([In] nint device, [In] int pixelFormat, [In] int numberOfBuffers, [In] SizeInt32 size);
        [PreserveSig] int TryGetNextFrame(out nint frame);
        [PreserveSig] int AddFrameArrived_NotUsed(nint handler, out long token); // add_FrameArrived placeholder - we poll instead of subscribing
        [PreserveSig] int RemoveFrameArrived_NotUsed(long token); // remove_FrameArrived placeholder
        [PreserveSig] int CreateCaptureSession([In] nint item, out nint session);
    }

    // --- IGraphicsCaptureSession (instance interface) ---

    [ComImport]
    [Guid("814E42A9-F70F-4AD7-939B-FDDCC6EB880D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureSession
    {
        // IInspectable
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int StartCapture();
    }

    // --- IDirect3D11CaptureFrame (instance interface) ---

    [ComImport]
    [Guid("FA50C623-38DA-4B32-ACF3-FA9734AD800E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3D11CaptureFrame
    {
        // IInspectable
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        // IDirect3D11CaptureFrame itself
        [PreserveSig] int GetSurface(out nint surface); // a fresh IDirect3DSurface* reference we own
        [PreserveSig] int GetSystemRelativeTime_NotUsed(out long time); // placeholder
        [PreserveSig] int GetContentSize(out SizeInt32 size);
    }

    // --- IClosable (WinRT's IDisposable - separate vtable, must be QI'd for explicitly) ---

    [ComImport]
    [Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWinRTClosable
    {
        // IInspectable
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int Close();
    }

    // --- IDirect3DDxgiInterfaceAccess (Win32-side bridge back from a WinRT surface to a real D3D11
    // object). Declared verbatim from Microsoft's own Direct3D11Helper.cs sample (also independently
    // matching a Silk.NET maintainer's own declaration of the same interface). ---

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }

    public static readonly Guid Id3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // --- Free functions ---

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    public static extern uint CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory", ExactSpelling = true)]
    public static extern int RoGetActivationFactory(nint activatableClassId, [In] ref Guid iid, out nint factory);

    [DllImport("combase.dll", EntryPoint = "WindowsCreateString", ExactSpelling = true)]
    public static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

    [DllImport("combase.dll", EntryPoint = "WindowsDeleteString", ExactSpelling = true)]
    public static extern int WindowsDeleteString(nint hstring);
}
