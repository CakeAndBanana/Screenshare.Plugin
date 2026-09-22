using System.Runtime.InteropServices;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui.Helpers;

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
        public uint modeInfoIdx;
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
