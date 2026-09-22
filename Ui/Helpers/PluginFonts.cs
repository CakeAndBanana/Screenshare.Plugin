using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui.Helpers;

/// <summary>Larger copies of the default font, rasterized at the size they are drawn at.
///
/// The two ways of getting big text without one of these - ImGui.SetWindowFontScale, and passing a
/// larger size to ImDrawList.AddText - both stretch glyphs that were baked into the atlas at the
/// default size, so the result is bilinear-filtered and looks soft. Building the font at the real
/// size is the only way to get crisp large text.</summary>
internal static class PluginFonts
{
    public const float HeadingScale = 1.5f;
    public const float ButtonLabelScale = 1.25f;

    // Keyed by scale: each distinct size is a separate font baked into the atlas, so callers share
    // these rather than asking for arbitrary sizes.
    private static readonly Dictionary<float, IFontHandle> Handles = [];

    /// <summary>Builds the sizes used by the UI up front. Creating a handle asks the atlas to
    /// rebuild asynchronously, and until it finishes the text falls back to the default size - done
    /// at load, that settles before any window is opened rather than popping mid-session.</summary>
    public static void Warm()
    {
        Get(HeadingScale);
        Get(ButtonLabelScale);
    }

    /// <summary>Pushes the font for <paramref name="scale"/> for the lifetime of the returned
    /// scope. Falls back to the current font while the atlas is still building, which is the first
    /// frames after load and after a font-settings change.</summary>
    public static IDisposable Push(float scale)
    {
        IFontHandle handle = Get(scale);
        return handle.Available ? handle.Push() : NoScope.Instance;
    }

    public static void DisposeAll()
    {
        foreach (IFontHandle handle in Handles.Values)
            handle.Dispose();

        Handles.Clear();
    }

    private static IFontHandle Get(float scale)
    {
        if (Handles.TryGetValue(scale, out IFontHandle? existing))
            return existing;

        // Sized from the unscaled default: this atlas applies the global (DPI) scale itself during
        // the build, so multiplying an already-scaled size in here would scale it twice.
        float sizePx = UiBuilder.DefaultFontSizePx * scale;
        IFontHandle handle = ServiceCollector.PI.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(sizePx)));

        Handles[scale] = handle;
        return handle;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
