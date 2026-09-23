using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui.Helpers;

namespace ScreenShare.Plugin.Ui;

public sealed class WorldScreenWindow : Window
{
    private readonly WorldScreenPanel panel;

    public WorldScreenWindow(
        RoomSessionService roomSession,
        Func<WorldScreenController> worldScreen,
        Func<RawTextureWrap?> getPreviewTexture)
        : base("In-world screen###ScreenShareWorldScreenWindow")
    {
        panel = new WorldScreenPanel("worldWindow", roomSession, worldScreen, getPreviewTexture);
        Size = new Vector2(440, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ServiceCollector.Config.ShowWorldScreen)
        {
            ImGui.TextWrapped("The in-world screen is turned off in the general settings, so it isn't shown to you. " +
                              "Placing it still decides where everyone else sees it.");
            ImGui.Separator();
        }

        panel.Draw();
    }
}
