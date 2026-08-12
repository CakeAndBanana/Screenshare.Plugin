using System.Runtime.InteropServices;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

/// <summary>
/// Resolves a monitor's real friendly name (e.g. "Dell U2720Q") for the GDI device name DXGI
/// reports (e.g. "\\.\DISPLAY1") - DXGI's own OutputDescription only ever exposes that cryptic
/// device path, never anything human-readable. Windows' Connecting and Configuring Displays (CCD)
/// API is the actual purpose-built way to get this (it's what the Display Settings page itself
/// uses): QueryDisplayConfig enumerates active display "paths", each pairing a source (the GDI
/// device name) with a target (the physical monitor, whose EDID-derived friendly name is fetched
/// via a separate DisplayConfigGetDeviceInfo call) - joining the two by path gives exactly the
/// "\\.\DISPLAYn" -&gt; friendly-name mapping needed here.
///
/// Every struct field offset/size below was computed from Microsoft's own official struct
/// definitions (wingdi.h/winuser.h, fetched from learn.microsoft.com) rather than guessed, matching
/// this project's established practice for native interop. DISPLAYCONFIG_MODE_INFO's own contents
/// are never read (only used as an output buffer QueryDisplayConfig requires) - it's declared as an
/// opaque 64-byte blob rather than replicating its full internal union, since only its correct total
/// size matters for marshaling.
/// </summary>
internal static class MonitorNameHelper
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const int ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    // CCHDEVICENAME (32 WCHARs) - matches the GDI device name buffer size used throughout the
    // Win32 display APIs (e.g. DISPLAY_DEVICE.DeviceName).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        // DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS - a bitfield (friendlyNameFromEdid/
        // friendlyNameForced/edidIdsValid/reserved), not interpreted here. Whether
        // monitorFriendlyDeviceName is non-empty is a simpler, equally reliable signal for
        // whether it's actually usable, so the individual bits are never read - only this
        // field's overall 4-byte size matters for correct marshaling of what follows it.
        public uint flags;

        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx; // union with cloneGroupId/sourceModeInfoIdx bitfields - only the plain index form is used
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable; // BOOL
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO_OPAQUE
    {
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO_OPAQUE[] modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    /// <summary>Maps GDI device name (e.g. "\\.\DISPLAY1", matching SharpDX.DXGI.OutputDescription.
    /// DeviceName exactly) to the monitor's real friendly name - empty if the CCD API call failed
    /// for any reason (never throws; this is a purely cosmetic lookup, and the caller already has
    /// a perfectly functional fallback label).</summary>
    public static Dictionary<string, string> TryGetFriendlyNames()
    {
        var result = new Dictionary<string, string>();
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                return result;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO_OPAQUE[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, 0) != ERROR_SUCCESS)
                return result;

            for (int i = 0; i < pathCount; i++)
            {
                var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref sourceName) != ERROR_SUCCESS)
                    continue;

                var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref targetName) != ERROR_SUCCESS)
                    continue;

                if (!string.IsNullOrWhiteSpace(targetName.monitorFriendlyDeviceName))
                    result[sourceName.viewGdiDeviceName] = targetName.monitorFriendlyDeviceName;
            }
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(ex, "ScreenShare failed to resolve friendly monitor names - falling back to raw device names");
        }

        return result;
    }
}
