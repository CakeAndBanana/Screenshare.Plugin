using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui.Helpers;
using ScreenShare.Plugin.Ui.Views;

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
        ServiceCollector.Config.Migrate();

        pi.SavePluginConfig(ServiceCollector.Config);
        PluginFonts.Warm();

        shareWindow = new ShareWindow();
        windowSystem.AddWindow(shareWindow);
        windowSystem.AddWindow(shareWindow.StreamView);
        windowSystem.AddWindow(shareWindow.Settings);
        windowSystem.AddWindow(shareWindow.WorldScreen);

        ServiceCollector.Commands.AddHandler("/screenshare",
            new Dalamud.Game.Command.CommandInfo((_, _) => shareWindow.IsOpen = true)
            { HelpMessage = "Open the ScreenShare window" });

        ServiceCollector.PI.UiBuilder.DisableUserUiHide = true;
        ServiceCollector.PI.UiBuilder.Draw += shareWindow.TickFrame;
        ServiceCollector.PI.UiBuilder.Draw += DrawWindows;
        ServiceCollector.PI.UiBuilder.OpenMainUi += () => shareWindow.IsOpen = true;
        ServiceCollector.PI.UiBuilder.OpenConfigUi += shareWindow.OpenSettings;

        ServiceCollector.PluginLog.Information("ScreenShare plugin loaded.");
    }

    private void DrawWindows()
    {
        UiTheme.EndFrame();

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

        PluginFonts.DisposeAll();
    }
}
