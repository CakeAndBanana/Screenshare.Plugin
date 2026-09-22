using System.Numerics;
using Dalamud.Bindings.ImGui;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui.Helpers;

namespace ScreenShare.Plugin.Ui;

/// <summary>Every control for the in-world screen, drawn wherever it is wanted. It lives in its own
/// window and also inside the stream window, so it exists once as a component rather than as two
/// copies of the same layout drifting apart.</summary>
internal sealed class WorldScreenPanel
{
    private readonly string idPrefix;
    private readonly RoomSessionService roomSession;
    private readonly Func<WorldScreenController> worldScreen;
    private readonly Func<RawTextureWrap?> getPreviewTexture;

    private ScreenPlacementInfo? sliderSource;
    private bool slidersActive;
    private float sliderWidth;
    private float sliderHeight;
    private float sliderYaw;
    private float sliderPitch;
    private float sliderSpread;
    private float sliderFalloffStart;
    private float sliderFalloffEnd;
    private float nudgeStepMeters = 0.5f;

    private float opacity = ServiceCollector.Config.WorldScreenOpacity;
    private bool opacityActive;

    /// <param name="idPrefix">Distinct per instance. Widget ids are scoped per window by ImGui, but
    /// the click-to-type slot on <see cref="UiTheme.ValueSlider(string,string,ref float,float,float,float,int,string,float,string)"/>
    /// is keyed by id alone - so without this, clicking a value in one copy would open the text box
    /// in both, and the two would fight over the one edit buffer.</param>
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
        // Re-read from config unless this panel's own slider is mid-drag. Two copies of this panel
        // can be on screen at once, and a value mirrored into a field would leave whichever copy
        // you are not touching showing a stale number.
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

        // Straight off config rather than mirrored into a field: a checkbox commits the instant it
        // is clicked, so there is no drag state to protect, and both copies stay in agreement.
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
                "on top of it, instead of the screen covering them. Needs a hook into the game's " +
                "renderer, so it may stop working after a game patch - untick it if anything looks wrong.");
        }

        if (drawUnderUi && !worldScreen().IsDrawingUnderUi)
            ImGui.TextColored(UiTheme.WarnColor, "The render hook isn't running - drawing over the game UI instead.");

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
                "None of this frame's passes bound a depth buffer, so there is nowhere to draw the " +
                "screen underneath the interface. Untick \"Draw underneath the game UI\" to draw " +
                "it over the interface instead.");
        }

        if (OverlayDetector.Detected is { } overlay)
        {
            ImGui.TextColored(UiTheme.WarnColor,
                $"{overlay} is loaded. If the in-world screen flickers while the popout window " +
                "doesn't, turn off \"Draw under the game UI\" - the panel then draws over the HUD " +
                "but stays steady.");
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

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "One setting, because the depth conversion and the comparison direction are two " +
                "consequences of the same fact - setting them separately can hide the panel entirely. " +
                "Leave this off unless occlusion comes out inverted: the game renders reversed depth.");
        }

        string[] diagnosticNames = ["Off", "Show depth buffer", "Show depth comparison", "Show occlusion test"];
        int diagnostic = config.WorldScreenDiagnostic;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Diagnostic view", ref diagnostic, diagnosticNames, diagnosticNames.Length))
        {
            config.WorldScreenDiagnostic = diagnostic;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Depth buffer: paints the game's depth onto the panel. A recognisable scene means\n" +
                "there is real data to test against.\n\n" +
                "Depth comparison: scene depth in RED, the panel's own depth in GREEN. Yellow where\n" +
                "they nearly agree. A flatly red or flatly green panel means the two are on\n" +
                "different scales, and no comparison direction will work.\n\n" +
                "Occlusion test: green where the panel would be kept, red where it would be hidden,\n" +
                "without actually hiding anything. All green means the test never concludes -\n" +
                "which is why flipping the sign appears to do nothing.");
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

        // Kept after the fix rather than removed: a depth of 0.00000 here is exactly the symptom
        // that took several rounds to identify, and it costs nothing to leave it visible. At ~9m
        // this should read about 0.011 for the reversed-Z infinite-far projection the game uses.
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
            ? "Dragging - the room sees it when you let go."
            : "Drag the handle on the screen. Roll and non-uniform stretch aren't stored, so they aren't offered.");
    }

    private void DrawPlacementSliders(ScreenPlacementInfo placement)
    {
#if DEBUG
        // The panel's real size in metres and how far away it is. From inside an oversized panel
        // every failure looks the same, so these separate "it's huge" from "it isn't drawing".
        if (getPreviewTexture() is { } sizeTexture && GameSceneCamera.TryGetView() is { } sizeView)
        {
            PanelFrame sized = WorldScreenController.BuildFrame(placement, sizeTexture.Width, sizeTexture.Height);
            float distance = (sized.Center - sizeView.CameraPosition).Length();
            ImGui.TextDisabled(
                $"Panel {sized.RightHalf.Length() * 2f:F1}m x {sized.UpHalf.Length() * 2f:F1}m, {distance:F1}m from the camera");
        }
#endif

        // Re-seed only when the stored placement has actually changed AND nothing is mid-drag.
        // See the field declarations for why doing this unconditionally broke every slider.
        if (!slidersActive && !ReferenceEquals(placement, sliderSource))
            SeedSliders(placement);

        // Nudges are buttons rather than sliders: they move the panel along its OWN axes, so an
        // absolute slider would need an anchor that shifts every time it's used, and would snap
        // back to centre on release. Buttons just apply a step, which is what "it's not quite in
        // the right place" actually wants.
        ImGui.TextUnformatted("Nudge:");
        ImGui.SameLine();
        if (ImGui.Button("Left")) ApplyPlacement(WorldScreenController.Nudge(placement, -nudgeStepMeters, 0f));
        ImGui.SameLine();
        if (ImGui.Button("Right")) ApplyPlacement(WorldScreenController.Nudge(placement, nudgeStepMeters, 0f));
        ImGui.SameLine();
        if (ImGui.Button("Forward")) ApplyPlacement(WorldScreenController.Nudge(placement, 0f, nudgeStepMeters));
        ImGui.SameLine();
        if (ImGui.Button("Back")) ApplyPlacement(WorldScreenController.Nudge(placement, 0f, -nudgeStepMeters));

        // One column across both groups below, so the geometry and audio rows line up as one grid
        // rather than two that happen to sit above each other.
        float labelWidth = UiTheme.LabelColumn(
            "step (m)", "Width (m)", "Height (m)", "Yaw", "Tilt", "Speaker spread (m)",
            "Audio full volume within (m)", "Audio silent beyond (m)");

        // Its own row rather than trailing the buttons: with a value box beside it now, keeping it
        // on that line pushed the row past the window's width.
        UiTheme.ValueSlider(Id("nudgeStep"), "step (m)", ref nudgeStepMeters, 0.1f, 2f, labelWidth, decimals: 1);

        SliderEdit edit = UiTheme.ValueSlider(
            Id("placeWidth"), "Width (m)", ref sliderWidth, 1f, 40f, labelWidth, decimals: 1);
        bool changed = edit.Committed;
        bool active = edit.Active;

        // Ranges are anchored to the seeded height, not the live one, so the scale doesn't shift
        // underneath the handle while it's being dragged.
        //
        // Guarded against a non-finite value because a slider whose BOUNDS are NaN throws inside
        // ImGui, taking the whole window's draw with it. The placement reaching here is not
        // necessarily one this client produced - it can arrive from the room, from another client's
        // build - so the value cannot simply be trusted to be sane.
        float heightAnchor = sliderSource?.Y ?? placement.Y;
        if (!float.IsFinite(heightAnchor))
            heightAnchor = 0f;
        edit = UiTheme.ValueSlider(
            Id("placeHeight"), "Height (m)", ref sliderHeight, heightAnchor - 10f, heightAnchor + 10f,
            labelWidth, decimals: 1);
        changed |= edit.Committed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(Id("placeYaw"), "Yaw", ref sliderYaw, -180f, 180f, labelWidth, decimals: 0);
        changed |= edit.Committed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(Id("placeTilt"), "Tilt", ref sliderPitch, -60f, 60f, labelWidth, decimals: 0);
        changed |= edit.Committed;
        active |= edit.Active;

        UiTheme.SectionLabel("Audio");

        edit = UiTheme.ValueSlider(
            Id("placeSpread"), "Speaker spread (m)", ref sliderSpread, 0f, 15f, labelWidth, decimals: 1);
        changed |= edit.Committed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(
            Id("placeFalloffStart"), "Audio full volume within (m)", ref sliderFalloffStart, 0f, 50f,
            labelWidth, decimals: 1);
        changed |= edit.Committed;
        active |= edit.Active;

        edit = UiTheme.ValueSlider(
            Id("placeFalloffEnd"), "Audio silent beyond (m)", ref sliderFalloffEnd, 1f, 120f,
            labelWidth, decimals: 1);
        changed |= edit.Committed;
        active |= edit.Active;

        slidersActive = active;

        if (!changed)
            return;

        ScreenPlacementInfo updated = placement with
        {
            Y = sliderHeight,
            Yaw = sliderYaw * MathF.PI / 180f,
            Pitch = sliderPitch * MathF.PI / 180f,
            WidthMeters = sliderWidth,
            SpeakerSpread = sliderSpread,
            FalloffStart = sliderFalloffStart,

            // Keeping end strictly past start avoids a zero-width falloff band, where gain would
            // step from full to silent at one exact distance and click as you crossed it.
            FalloffEnd = MathF.Max(sliderFalloffEnd, sliderFalloffStart + 1f),
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Adopt the committed value as the new seed straight away. Waiting for the relay's echo
        // would leave a window where the next frame re-seeds from the pre-edit placement and the
        // handle visibly snaps back before jumping forward again.
        SeedSliders(updated);
        ApplyPlacement(updated);
    }

    private void SeedSliders(ScreenPlacementInfo placement)
    {
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
