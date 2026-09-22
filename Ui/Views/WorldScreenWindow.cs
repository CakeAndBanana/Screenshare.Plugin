using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

/// <summary>The in-world screen's own window, so its placement can be adjusted without opening a
/// stream first - the panel inside is the same one the stream window shows.</summary>
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

    public override void Draw() => panel.Draw();
}
