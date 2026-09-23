using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui.Helpers;

internal static class PluginFonts
{
    public const float HeadingScale = 1.5f;
    public const float ButtonLabelScale = 1.25f;

    private static readonly Dictionary<float, IFontHandle> Handles = [];

    public static void Warm()
    {
        Get(HeadingScale);
        Get(ButtonLabelScale);
    }

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
