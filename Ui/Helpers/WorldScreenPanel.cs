using System.Numerics;
using Dalamud.Bindings.ImGui;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui.Helpers;

internal sealed class WorldScreenPanel
{
    private readonly string idPrefix;
    private readonly RoomSessionService roomSession;
    private readonly Func<WorldScreenController> worldScreen;
    private readonly Func<RawTextureWrap?> getPreviewTexture;

    private const float PositionRangeMeters = 15f;

    private ScreenPlacementInfo? sliderSource;
    private bool slidersActive;
    private float sliderWidth;
    private float sliderHeight;
    private float sliderYaw;
    private float sliderPitch;
    private float sliderSpread;
    private float sliderFalloffStart;
    private float sliderFalloffEnd;
    private ScreenPlacementInfo? positionAnchor;
    private float sliderSideways;
    private float sliderForward;

    private float opacity = ServiceCollector.Config.WorldScreenOpacity;
    private bool opacityActive;

    public WorldScreenPanel(
        string idPrefix,
        RoomSessionService roomSession,
        Func<WorldScreenController> worldScreen,
        Func<RawTextureWrap?> getPreviewTexture)
    {
        this.idPrefix = idPrefix;
        this.roomSession = roomSession;
        this.worldScreen = worldScreen;
        this.getPreviewTexture = getPreviewTexture;
    }

    private string Id(string name) => $"##{idPrefix}{name}";

    public void Draw()
    {
        Configuration config = ServiceCollector.Config;
        ScreenPlacementInfo? placement = roomSession.ScreenPlacement;
        bool canPlace = WorldScreenController.CanPlace(roomSession);

        UiTheme.SectionLabel("Placement");

        if (canPlace)
            DrawPlacementButtons(placement);

        if (placement is null)
        {
            ImGui.TextDisabled(canPlace
                ? "No screen placed yet. Everyone watching sees it as soon as you place one."
                : "No screen placed yet. It appears here by itself once the host places one.");
        }
        else if (placement.TerritoryType != ServiceCollector.ClientState.TerritoryType)
        {
            ImGui.TextColored(UiTheme.WarnColor, "The screen is placed in another zone, so it isn't being drawn here.");
        }
        else if (canPlace)
        {
            DrawGizmoControls();
            DrawPlacementSliders(placement);
        }
        else
        {
            ImGui.TextDisabled("Showing the room's screen in the world. The host and streamers decide where it goes.");
        }

        UiTheme.SectionLabel("Display");
        DrawDisplaySettings(config);
    }

    private void DrawDisplaySettings(Configuration config)
    {
        if (!opacityActive)
            opacity = config.WorldScreenOpacity;

        SliderEdit opacityEdit = UiTheme.ValueSlider(
            Id("opacity"), "Opacity", ref opacity, 0.05f, 1f, UiTheme.LabelColumn("Opacity"));
        opacityActive = opacityEdit.Active;

        if (opacityEdit.Committed)
        {
            config.WorldScreenOpacity = opacity;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        bool spatialAudio = config.SpatialAudio;
        if (ImGui.Checkbox("Positional audio", ref spatialAudio))
        {
            config.SpatialAudio = spatialAudio;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        bool drawUnderUi = config.WorldScreenDrawUnderUi;
        if (ImGui.Checkbox("Draw underneath the game UI", ref drawUnderUi))
        {
            config.WorldScreenDrawUnderUi = drawUnderUi;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Draws the screen inside the game's own render pass so your hotbars and windows sit " +
                "on top of it; untick it if anything looks wrong.");
        }

        if (drawUnderUi && !worldScreen().IsDrawingUnderUi)
            ImGui.TextColored(UiTheme.WarnColor, "The render hook isn't running.");

        if (drawUnderUi)
            DrawRenderPassNotes();

#if DEBUG

        DrawDepthDiagnostics(config);
#endif
    }

    private void DrawRenderPassNotes()
    {
        int passes = worldScreen().LastFrameBackBufferSwitches;
        int scenePasses = worldScreen().LastFrameSceneSwitches;
        ImGui.TextDisabled($"Render passes last frame: {passes} ({scenePasses} drawing the 3D scene)");

        if (scenePasses == 0 && passes > 0)
        {
            ImGui.TextColored(UiTheme.WarnColor,
                "None of this frame's passes bound a depth buffer.");
        }

        if (OverlayDetector.Detected is { } overlay)
        {
            ImGui.TextColored(UiTheme.WarnColor,
                $"{overlay} is loaded. If the in-world screen flickers while the popout window " +
                "doesn't, turn off \"Draw under the game UI\"");
        }

        if (worldScreen().SceneHookFallbackReason is { } fallbackReason)
            ImGui.TextDisabled($"Adjusted automatically: {fallbackReason}.");
    }

    private void DrawPlacementButtons(ScreenPlacementInfo? placement)
    {
        if (ImGui.Button(placement is null ? "Place here" : "Move here"))
            ApplyPlacement(WorldScreenController.PlaceInFrontOfPlayer(placement));

        if (placement is null)
            return;

        ImGui.SameLine();
        if (ImGui.Button("Reset shape"))
        {
            ScreenPlacementInfo reset = placement with
            {
                WidthMeters = 6f,
                Pitch = 0f,
                Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            SeedSliders(reset);
            ApplyPlacement(reset);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Back to a 6m wide, untilted screen, without moving it.");

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            ApplyPlacement(null);
    }

#if DEBUG
    private void DrawDepthDiagnostics(Configuration config)
    {
        bool standardDepth = config.WorldScreenStandardDepth;
        if (ImGui.Checkbox("Scene uses standard depth (near = 0)", ref standardDepth))
        {
            config.WorldScreenStandardDepth = standardDepth;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        string[] diagnosticNames = ["Off", "Show depth buffer", "Show depth comparison", "Show occlusion test"];
        int diagnostic = config.WorldScreenDiagnostic;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Diagnostic view", ref diagnostic, diagnosticNames, diagnosticNames.Length))
        {
            config.WorldScreenDiagnostic = diagnostic;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (GameSceneCamera.TryGetSceneDepth() is { } sceneDepth)
        {
            Vector2 display = ImGui.GetIO().DisplaySize;
            ImGui.TextDisabled($"Depth buffer {sceneDepth.Width}x{sceneDepth.Height}, backbuffer {(int)display.X}x{(int)display.Y}");
        }
        else
        {
            ImGui.TextColored(UiTheme.WarnColor, "No depth buffer available - the panel cannot be occluded.");
        }

        if (GameSceneCamera.TryGetView() is { } view)
        {
            ImGui.TextDisabled($"Depth convention reported by the game: {(view.StandardZ ? "standard Z (near = 0)" : "reversed Z (near = 1)")}");

            float sign = WorldScreenController.DepthSign(config);
            ImGui.TextDisabled(
                $"Active panel mode: {WorldScreenController.ResolveDepthMode(config)}, " +
                $"sign {sign:+0;-0}, scene treated as {(WorldScreenController.SceneUsesReversedZ(config) ? "reversed Z" : "standard Z")}");
            DrawDepthNumbers(view);
        }
    }

    private void DrawDepthNumbers(in SceneView view)
    {
        ScreenPlacementInfo? placement = roomSession.ScreenPlacement;
        if (placement is null || getPreviewTexture() is not { } texture)
            return;

        PanelFrame frame = WorldScreenController.BuildFrame(placement, texture.Width, texture.Height);
        float distance = (frame.Center - view.CameraPosition).Length();
        ImGui.TextDisabled($"Panel centre: {distance:F2}m away");

        Vector4 clip = Vector4.Transform(new Vector4(frame.Center, 1f), view.ViewProjection);
        float ndcZ = clip.W != 0f ? clip.Z / clip.W : float.NaN;
        ImGui.TextDisabled(
            $"Projected: w {clip.W:F3}, depth {ndcZ:F5} " +
            $"(M33 {view.Projection.M33:F5} M43 {view.Projection.M43:F5} M34 {view.Projection.M34:F5})");
        ImGui.TextDisabled($"Camera planes: near {view.NearPlane:F3}, far {view.FarPlane:F1}");
    }

#endif

    private void DrawGizmoControls()
    {
        WorldScreenController controller = worldScreen();

        bool editMode = controller.EditMode;
        if (ImGui.Checkbox("Edit with handles on the screen", ref editMode))
            controller.EditMode = editMode;

        if (!controller.EditMode)
            return;

        ImGui.SameLine();
        int operation = (int)controller.GizmoOperation;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.Combo("##gizmoOperation", ref operation, "Move\0Turn\0Tilt\0Resize\0"))
            controller.GizmoOperation = (GizmoOperation)operation;

        ImGui.TextDisabled(controller.IsDraggingGizmo
            ? "Dragging the room sees it when you let go."
            : "Drag the handle on the screen.");
    }

    private void DrawPlacementSliders(ScreenPlacementInfo placement)
    {
#if DEBUG
        if (getPreviewTexture() is { } sizeTexture && GameSceneCamera.TryGetView() is { } sizeView)
        {
            PanelFrame sized = WorldScreenController.BuildFrame(placement, sizeTexture.Width, sizeTexture.Height);
            float distance = (sized.Center - sizeView.CameraPosition).Length();
            ImGui.TextDisabled(
                $"Panel {sized.RightHalf.Length() * 2f:F1}m x {sized.UpHalf.Length() * 2f:F1}m, {distance:F1}m from the camera");
        }
#endif

        if (!slidersActive && !ReferenceEquals(placement, sliderSource))
            SeedSliders(placement);

        float labelWidth = UiTheme.LabelColumn(
            "Left / right (m)", "Forward / back (m)", "Width (m)", "Height (m)", "Rotate (left/right)", "Tilt (down/up)",
            "Speaker spread (m)", "Audio full volume within (m)", "Audio silent beyond (m)");

        SliderEdit edit = UiTheme.ValueSlider(
            Id("placeSideways"), "Left / right (m)", ref sliderSideways, -PositionRangeMeters, PositionRangeMeters,
            labelWidth, decimals: 1, tooltip: "Slides the screen sideways, along its own face.");
        bool changed = edit.Committed;
        bool edited = edit.Changed;
        bool active = edit.Active;

        edit = UiTheme.ValueSlider(
            Id("placeForward"), "Forward / back (m)", ref sliderForward, -PositionRangeMeters, PositionRangeMeters,
            labelWidth, decimals: 1, tooltip: "Pushes the screen away from, or pulls it towards, where it was placed from.");
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(
            Id("placeWidth"), "Width (m)", ref sliderWidth, 1f, 40f, labelWidth, decimals: 1);
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        float heightAnchor = sliderSource?.Y ?? placement.Y;
        if (!float.IsFinite(heightAnchor))
            heightAnchor = 0f;
        edit = UiTheme.ValueSlider(
            Id("placeHeight"), "Height (m)", ref sliderHeight, heightAnchor - 10f, heightAnchor + 10f,
            labelWidth, decimals: 1);
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(Id("placeYaw"), "Rotate (left/right)", ref sliderYaw, -180f, 180f, labelWidth, decimals: 0);
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(Id("placeTilt"), "Tilt (down/up)", ref sliderPitch, -60f, 60f, labelWidth, decimals: 0);
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        UiTheme.SectionLabel("Audio");

        edit = UiTheme.ValueSlider(
            Id("placeSpread"), "Speaker spread (m)", ref sliderSpread, 0f, 15f, labelWidth, decimals: 1);
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(
            Id("placeFalloffStart"), "Audio full volume within (m)", ref sliderFalloffStart, 0f, 50f,
            labelWidth, decimals: 1);
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(
            Id("placeFalloffEnd"), "Audio silent beyond (m)", ref sliderFalloffEnd, 1f, 120f,
            labelWidth, decimals: 1);
        changed |= edit.Committed;
        edited |= edit.Changed;
        active |= edit.Active;

        bool wasActive = slidersActive;
        slidersActive = active;

        if (!changed)
        {
            // Show slider drags on this client right away; the room gets the result on release.
            if (active && edited)
                worldScreen().PreviewPlacement = BuildSliderPlacement(placement) with { Version = placement.Version };
            else if (wasActive && !active)
                worldScreen().PreviewPlacement = null;

            return;
        }

        ScreenPlacementInfo anchor = positionAnchor ?? placement;
        ScreenPlacementInfo updated = BuildSliderPlacement(placement);
        bool turned = MathF.Abs(updated.Yaw - anchor.Yaw) > 0.0001f;

        worldScreen().PreviewPlacement = null;
        SeedSliders(updated, keepPositionAnchor: !turned);
        ApplyPlacement(updated);
    }

    private ScreenPlacementInfo BuildSliderPlacement(ScreenPlacementInfo placement)
    {
        ScreenPlacementInfo anchor = positionAnchor ?? placement;
        ScreenPlacementInfo moved = WorldScreenController.Nudge(anchor, sliderSideways, sliderForward);
        float yaw = sliderYaw * MathF.PI / 180f;

        return placement with
        {
            X = moved.X,
            Z = moved.Z,
            Y = sliderHeight,
            Yaw = yaw,
            Pitch = sliderPitch * MathF.PI / 180f,
            WidthMeters = sliderWidth,
            SpeakerSpread = sliderSpread,
            FalloffStart = sliderFalloffStart,

            FalloffEnd = MathF.Max(sliderFalloffEnd, sliderFalloffStart + 1f),
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private void SeedSliders(ScreenPlacementInfo placement, bool keepPositionAnchor = false)
    {
        keepPositionAnchor |= positionAnchor is not null && placement.Version == sliderSource?.Version;

        if (!keepPositionAnchor || positionAnchor is null)
        {
            positionAnchor = placement;
            sliderSideways = 0f;
            sliderForward = 0f;
        }

        sliderSource = placement;
        sliderWidth = placement.WidthMeters;
        sliderHeight = placement.Y;
        sliderYaw = placement.Yaw * 180f / MathF.PI;
        sliderPitch = placement.Pitch * 180f / MathF.PI;
        sliderSpread = placement.SpeakerSpread;
        sliderFalloffStart = placement.FalloffStart;
        sliderFalloffEnd = placement.FalloffEnd;
    }

    private void ApplyPlacement(ScreenPlacementInfo? placement) =>
        WorldScreenController.ApplyPlacement(roomSession, placement);
}
