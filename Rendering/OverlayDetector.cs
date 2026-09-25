using System.Diagnostics;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Rendering;

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

    // Anything that hooks Present or reorders the frame's passes: upscalers, overlays, capture hooks.
    private static readonly string[] GraphicsModuleHints =
    [
        "nvngx", "dlss", "ffx", "fidelityfx", "xess", "optiscaler", "reshade", "gshade",
        "rtsshooks", "discordhook", "gameoverlayrenderer", "graphics-hook", "nvspcap", "overlay",
    ];

    // System dlls that injectors ship as proxies next to the game executable.
    private static readonly string[] ProxyDllNames = ["dxgi.dll", "d3d11.dll", "dinput8.dll", "winmm.dll", "version.dll"];

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
