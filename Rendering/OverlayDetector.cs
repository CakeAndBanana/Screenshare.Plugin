using System.Diagnostics;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Rendering;

/// <summary>
/// OverlayDetector scans the current process for known graphics modules that may indicate the presence of overlays, upscalers, or capture hooks. 
/// It provides a way to detect and describe these modules, which can be useful for debugging or ensuring compatibility with certain rendering scenarios.
/// </summary>
internal static class OverlayDetector
{
    private static string? detected;
    private static bool scanned;

    public static string? Detected
    {
        get
        {
            if (!scanned)
            {
                scanned = true;
                detected = Scan();
            }

            return detected;
        }
    }

    /// <summary>
    /// Graphics module hints that are commonly associated with overlays, upscalers, or capture hooks. 
    /// These hints are used to identify potentially intrusive graphics modules loaded in the current process.
    /// </summary>
    private static readonly string[] GraphicsModuleHints =
    [
        "nvngx", "dlss", "ffx", "fidelityfx", "xess", "optiscaler", "reshade", "gshade",
        "rtsshooks", "discordhook", "gameoverlayrenderer", "graphics-hook", "nvspcap", "overlay",
    ];

    /// <summary>
    /// Proxy DLL names that are commonly used to intercept graphics API calls.
    /// If these DLLs are loaded from a non-system directory, they may indicate the presence of a proxy or hook that could affect rendering behavior.
    /// </summary>
    private static readonly string[] ProxyDllNames = ["dxgi.dll", "d3d11.dll", "dinput8.dll", "winmm.dll", "version.dll"];

    /// <summary>
    /// Describes the graphics modules currently loaded in the process that match known hints or proxy DLL names.
    /// </summary>
    /// <returns></returns>
    public static string DescribeGraphicsModules()
    {
        try
        {
            string systemDir = Environment.SystemDirectory;
            var found = new List<string>();

            using Process self = Process.GetCurrentProcess();

            foreach (ProcessModule module in self.Modules)
            {
                using (module)
                {
                    string name = module.ModuleName;
                    bool isProxy = ProxyDllNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && !module.FileName.StartsWith(systemDir, StringComparison.OrdinalIgnoreCase);

                    if (isProxy || GraphicsModuleHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                        found.Add(isProxy ? $"{name} (proxy: {module.FileName})" : name);
                }
            }

            return found.Count == 0 ? "none" : string.Join(", ", found);
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Scans the current process for known graphics modules that may indicate the presence of overlays, upscalers, or capture hooks.
    /// </summary>
    /// <returns></returns>
    private static string? Scan()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();

            foreach (ProcessModule module in self.Modules)
            {
                using (module)
                {
                    string haystack = string.Join(
                        ' ',
                        module.ModuleName,
                        module.FileVersionInfo.FileDescription,
                        module.FileVersionInfo.ProductName);

                    if (haystack.Contains("reshade", StringComparison.OrdinalIgnoreCase))
                        return $"ReShade ({module.ModuleName})";

                    if (haystack.Contains("gshade", StringComparison.OrdinalIgnoreCase))
                        return $"GShade ({module.ModuleName})";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(ex, "ScreenShare could not check for post-processing overlays.");
            return null;
        }
    }
}
