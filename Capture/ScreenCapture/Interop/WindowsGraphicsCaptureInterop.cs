using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Capture.ScreenCapture.Interop;

/// <summary>
/// Windows Graphics Capture interop definitions for interacting with Windows Runtime APIs related to screen capture and graphics capture.
/// Microsoft's Windows.Graphics.Capture namespace provides APIs for capturing the contents of a window or monitor, and this interop class defines the necessary COM interfaces, structures, and constants to facilitate that interaction from .NET code.
/// Documentation: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture?view=winrt-22621
/// </summary>
internal static class WindowsGraphicsCaptureInterop
{
    public const string Direct3D11CaptureFramePoolClassName = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";

    public const int PixelFormatB8G8R8A8UIntNormalized = 87;

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeInt32
    {
        public int Width;
        public int Height;
    }


    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);

        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    public static readonly Guid IGraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItem
    {
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int GetDisplayName(out nint hstring);
        [PreserveSig] int GetSize(out SizeInt32 size);
    }

    public static readonly Guid IDirect3D11CaptureFramePoolStatics2Iid = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");

    [ComImport]
    [Guid("589B103F-6BBC-5DF5-A991-02E28B3B66D5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3D11CaptureFramePoolStatics2
    {
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int CreateFreeThreaded([In] nint device, [In] int pixelFormat, [In] int numberOfBuffers, [In] SizeInt32 size, out nint framePool);
    }

    [ComImport]
    [Guid("24EB6D22-1975-422E-82E7-780DBD8DDF24")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3D11CaptureFramePool
    {
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int Recreate([In] nint device, [In] int pixelFormat, [In] int numberOfBuffers, [In] SizeInt32 size);
        [PreserveSig] int TryGetNextFrame(out nint frame);
        [PreserveSig] int AddFrameArrived_NotUsed(nint handler, out long token);
        [PreserveSig] int RemoveFrameArrived_NotUsed(long token);
        [PreserveSig] int CreateCaptureSession([In] nint item, out nint session);
    }

    [ComImport]
    [Guid("814E42A9-F70F-4AD7-939B-FDDCC6EB880D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureSession
    {
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int StartCapture();
    }

    [ComImport]
    [Guid("2C39AE40-7D2E-5044-804E-8B6799D4CF9E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureSession2
    {
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int GetIsCursorCaptureEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int SetIsCursorCaptureEnabled([MarshalAs(UnmanagedType.U1)] bool value);
    }

    [ComImport]
    [Guid("FA50C623-38DA-4B32-ACF3-FA9734AD800E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3D11CaptureFrame
    {
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int GetSurface(out nint surface);
        [PreserveSig] int GetSystemRelativeTime_NotUsed(out long time);
        [PreserveSig] int GetContentSize(out SizeInt32 size);
    }

    [ComImport]
    [Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWinRTClosable
    {
        [PreserveSig] int GetIids(out uint iidCount, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int Close();
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }

    public static readonly Guid Id3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    public static extern uint CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory", ExactSpelling = true)]
    public static extern int RoGetActivationFactory(nint activatableClassId, [In] ref Guid iid, out nint factory);

    [DllImport("combase.dll", EntryPoint = "WindowsCreateString", ExactSpelling = true)]
    public static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

    [DllImport("combase.dll", EntryPoint = "WindowsDeleteString", ExactSpelling = true)]
    public static extern int WindowsDeleteString(nint hstring);

    public const uint GaRoot = 2;
    public const uint GaRootOwner = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    public const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
