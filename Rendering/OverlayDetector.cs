using System.Diagnostics;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Rendering;

// Whether a post-processing overlay is loaded into the game.
//
// It matters for one specific failure: the in-world panel is drawn by hooking the game's
// render-target switches, and an overlay that inserts its OWN passes changes that structure
// underneath the hook. A pass that binds the back buffer and then draws a full-screen quad over it
// paints the panel out again, and if that pass only runs on some frames the panel flickers - which
// is exactly what has been reported, only for the in-world panel and never for the popout window
// (which draws through ImGui at present time and cannot be overwritten this way).
//
// Reported rather than acted on. Two users saying "it flickers" and "I have no overlay" is not
// evidence about the same cause until one of them can check.
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
                    // Both usually load under a proxy name - dxgi.dll, d3d11.dll, opengl32.dll - so
                    // the filename says nothing. The version resource is what identifies them.
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
            // A diagnostic must never become the failure it was added to explain.
            ServiceCollector.PluginLog.Warning(ex, "ScreenShare could not check for post-processing overlays.");
            return null;
        }
    }
}
