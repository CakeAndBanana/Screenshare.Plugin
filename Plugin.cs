using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui;

namespace ScreenShare.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    public static string Name => "ScreenShare";

    private readonly WindowSystem windowSystem = new("SCREENSHARE_WINDOWS");
    private readonly ShareWindow shareWindow;

    public Plugin(IDalamudPluginInterface pi)
    {
        pi.Create<ServiceCollector>();
        ServiceCollector.Config = pi.GetPluginConfig() as Configuration ?? new Configuration();

        // Unconditional, not just on the next explicit save (e.g. clicking "Connect") - a
        // freshly-generated ClientId needs to actually reach disk on first load, or it would be
        // silently regenerated every session and the durable-ban guarantee it exists for would
        // never actually hold.
        pi.SavePluginConfig(ServiceCollector.Config);

        shareWindow = new ShareWindow();
        windowSystem.AddWindow(shareWindow);
        windowSystem.AddWindow(shareWindow.StreamView);

        ServiceCollector.Commands.AddHandler("/screenshare",
            new Dalamud.Game.Command.CommandInfo((_, _) => shareWindow.IsOpen = true)
            { HelpMessage = "Open the ScreenShare window" });

        ServiceCollector.PI.UiBuilder.Draw += windowSystem.Draw;
        ServiceCollector.PI.UiBuilder.OpenMainUi += () => shareWindow.IsOpen = true;

        ServiceCollector.PluginLog.Information("ScreenShare plugin loaded.");
    }

    public void Dispose()
    {
        ServiceCollector.PI.UiBuilder.Draw -= windowSystem.Draw;
        ServiceCollector.Commands.RemoveHandler("/screenshare");
        windowSystem.RemoveAllWindows();
        shareWindow.Dispose();
    }
}
