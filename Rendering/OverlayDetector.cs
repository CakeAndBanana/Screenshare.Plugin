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
