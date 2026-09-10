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

        pi.SavePluginConfig(ServiceCollector.Config);

        shareWindow = new ShareWindow();
        windowSystem.AddWindow(shareWindow);
        windowSystem.AddWindow(shareWindow.StreamView);
        windowSystem.AddWindow(shareWindow.Settings);

        ServiceCollector.Commands.AddHandler("/screenshare",
            new Dalamud.Game.Command.CommandInfo((_, _) => shareWindow.IsOpen = true)
            { HelpMessage = "Open the ScreenShare window" });

        ServiceCollector.PI.UiBuilder.DisableUserUiHide = true;
        ServiceCollector.PI.UiBuilder.Draw += shareWindow.TickFrame;
        ServiceCollector.PI.UiBuilder.Draw += DrawWindows;
        ServiceCollector.PI.UiBuilder.OpenMainUi += () => shareWindow.IsOpen = true;
        ServiceCollector.PI.UiBuilder.OpenConfigUi += () =>
        {
            shareWindow.Settings.IsOpen = true;
            shareWindow.Settings.BringToFront();
        };

        ServiceCollector.PluginLog.Information("ScreenShare plugin loaded.");
    }

    private void DrawWindows()
    {
        if (ServiceCollector.GameGui.GameUiHidden)
            return;

        windowSystem.Draw();
    }

    public void Dispose()
    {
        ServiceCollector.PI.UiBuilder.Draw -= shareWindow.TickFrame;
        ServiceCollector.PI.UiBuilder.Draw -= DrawWindows;
        ServiceCollector.Commands.RemoveHandler("/screenshare");
        windowSystem.RemoveAllWindows();
        shareWindow.Dispose();
    }
}
