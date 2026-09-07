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
        windowSystem.AddWindow(shareWindow.Settings);

        ServiceCollector.Commands.AddHandler("/screenshare",
            new Dalamud.Game.Command.CommandInfo((_, _) => shareWindow.IsOpen = true)
            { HelpMessage = "Open the ScreenShare window" });

        // The in-world screen is a 3D object in the world, not part of the interface, so hiding
        // the HUD should no more remove it than it removes a piece of furniture. Dalamud disagrees
        // by default: it stops calling Draw entirely while the user has the game UI hidden, which
        // took the panel with it - and, because Draw is also where the render hook's per-frame
        // bookkeeping happens, left that hook drawing one frozen frame until the UI came back.
        //
        // Opting out means Dalamud no longer hides anything for us, so the windows below now have
        // to hide themselves. That is DrawWindows.
        ServiceCollector.PI.UiBuilder.DisableUserUiHide = true;

        // Before the window system: TickFrame refreshes the decoded texture and submits the
        // in-world panel, and both have to happen regardless of which windows are open (see
        // ShareWindow.TickFrame), with the texture current before anything draws it.
        ServiceCollector.PI.UiBuilder.Draw += shareWindow.TickFrame;
        ServiceCollector.PI.UiBuilder.Draw += DrawWindows;
        ServiceCollector.PI.UiBuilder.OpenMainUi += () => shareWindow.IsOpen = true;

        // Without this, Dalamud's plugin installer flags the plugin ("does not register a config
        // UI callback") since it has no way to know the streaming-settings window even exists.
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
