using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

public sealed class StreamViewWindow : Window
{
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.7f, 0.2f, 1f);

    private readonly RoomSessionService roomSession;
    private readonly MediaSessionService mediaSession;
    private readonly Func<RawTextureWrap?> getPreviewTexture;
    private readonly Func<WorldScreenController> worldScreen;
    private readonly Action onClosed;

    // ImGui widgets need a by-ref local to bind to. These mirror config rather than replacing it -
    // config stays the source of truth, these are just what the widgets write into first.
    private bool projectInWorld = ServiceCollector.Config.ProjectInWorld;
    private bool spatialAudio = ServiceCollector.Config.SpatialAudio;
    private bool standardDepth = ServiceCollector.Config.WorldScreenStandardDepth;
    private bool drawUnderUi = ServiceCollector.Config.WorldScreenDrawUnderUi;
    private int diagnostic = ServiceCollector.Config.WorldScreenDiagnostic;
    private float opacity = ServiceCollector.Config.WorldScreenOpacity;
    private float volume = ServiceCollector.Config.ViewerVolume;
    private int avSyncOffsetMs = ServiceCollector.Config.AvSyncOffsetMs;
    private int playbackLatencyMs = ServiceCollector.Config.PlaybackLatencyMs;

    // Placement slider state. These MUST persist across frames rather than being re-read from the
    // placement each time. ImGui hands a slider's live value back through the ref it was given, so
    // re-seeding every frame overwrites the in-progress drag - and since the commit happens on
    // release, the value committed was the one from BEFORE the drag. That's why resizing the
    // screen appeared to do nothing at all. They're re-seeded only when the stored placement
    // actually changes and nothing is currently being dragged.
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

    // Rolling rate for the audio health counters. The underlying numbers are cumulative totals, and
    // over a long session those cannot distinguish "went badly wrong at startup and has been fine
    // since" from "still going wrong every second" - the two need completely different responses.
    private DateTime rateSampledAt;
    private long rateUnderrunBaseMs;
    private long rateDroppedBaseMs;
    private long underrunPerMinute;
    private long droppedPerMinute;

    public StreamViewWindow(
        RoomSessionService roomSession,
        MediaSessionService mediaSession,
        Func<RawTextureWrap?> getPreviewTexture,
        Func<WorldScreenController> worldScreen,
        Action onClosed)
        : base("Watching###ScreenShareStreamView")
    {
        this.roomSession = roomSession;
        this.mediaSession = mediaSession;
        this.getPreviewTexture = getPreviewTexture;
        this.worldScreen = worldScreen;
        this.onClosed = onClosed;
        Size = new Vector2(640, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnClose()
    {
        if (worldScreen().IsProjecting)
            return;

        onClosed();
    }

    public override void PreDraw()
    {
        string? watchingId = mediaSession.ViewingStreamerConnectionId;
        string displayName = roomSession.Members.FirstOrDefault(m => m.ConnectionId == watchingId)?.DisplayName ?? "someone";
        WindowName = $"Watching {displayName}###ScreenShareStreamView";
    }

    public override void Draw()
    {
        if (mediaSession.ViewerLastError is not null)
            ImGui.TextColored(ErrorColor, $"Media stream error: {mediaSession.ViewerLastError}");

        RawTextureWrap? texture = getPreviewTexture();
        if (texture is null)
        {
            ImGui.TextUnformatted("Connecting...");
        }
        else
        {
            Vector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Min(available.X / texture.Width, available.Y / texture.Height);
            scale = Math.Min(scale, 1f);
            Vector2 displaySize = texture.Size * Math.Max(scale, 0.01f);
            ImGui.Image(texture.Handle, displaySize);
        }

        ImGui.Separator();
        ImGui.TextUnformatted(
            $"{mediaSession.ViewerFramesDecoded} frames decoded ({mediaSession.ViewerAverageFps:F1} fps avg), " +
            $"{mediaSession.ViewerAudioPackets} audio packets");

        if (mediaSession.ViewerDecoderName is { } decoderName)
            ImGui.TextDisabled($"Decoding with {decoderName}.");
        ImGui.TextUnformatted("Audio level:");
        ImGui.SameLine();
        ImGui.ProgressBar(Math.Clamp(mediaSession.ViewerAudioPeak, 0f, 1f), new Vector2(200, 0), "");

        // Audio is the clock video syncs to, so its health IS the sync story - worth showing next
        // to the video counters rather than leaving a desync to be guessed at.
        if (mediaSession.ViewerAudioPackets > 0)
        {
            ImGui.TextDisabled(
                $"Audio buffer {mediaSession.ViewerAudioBufferedMs}/{mediaSession.ViewerAudioTargetMs}ms, " +
                $"{mediaSession.ViewerAudioUnderrunMs}ms silence played, " +
                $"{mediaSession.ViewerAudioDroppedMs}ms dropped to hold latency");

            UpdateAudioHealthRates(mediaSession.ViewerAudioUnderrunMs, mediaSession.ViewerAudioDroppedMs);
            if (rateSampledAt != default)
            {
                bool unhealthy = underrunPerMinute > 200 || droppedPerMinute > 200;
                string rateText = $"Recent rate: {underrunPerMinute}ms silence/min, {droppedPerMinute}ms dropped/min";
                if (unhealthy)
                    ImGui.TextColored(WarnColor, rateText + " - still happening, raise the playback buffer");
                else
                    ImGui.TextDisabled(rateText);
            }

            // The number that separates "out of sync" from "laggy but in sync" - two complaints
            // that feel identical and have completely different fixes.
            long drift = mediaSession.ViewerAvDriftMs;
            string driftLabel = Math.Abs(drift) < 50
                ? "in sync"
                : drift > 0 ? "video ahead of audio" : "video behind audio";
            string offsetNote = avSyncOffsetMs != 0 ? $", A/V sync offset {avSyncOffsetMs:+0;-0}ms applied" : string.Empty;
            ImGui.TextDisabled($"A/V drift {drift:+0;-0;0}ms ({driftLabel}){offsetNote}");

            // Total viewer-side latency, measured. A steady value is buffering working as
            // configured; a climbing one is a clock-rate mismatch, which no buffer setting fixes.
            if (mediaSession.ViewerBehindLiveMs is { } behind)
            {
                bool runaway = behind > playbackLatencyMs + 400;
                string behindText = $"Behind live {behind}ms" + (runaway ? " - climbing, not just buffered" : string.Empty);
                if (runaway)
                    ImGui.TextColored(WarnColor, behindText);
                else
                    ImGui.TextDisabled(behindText);
            }
        }

        if (mediaSession.ViewerDecodeDrops > 0)
        {
            ImGui.TextColored(WarnColor,
                $"{mediaSession.ViewerDecodeDrops} chunks dropped before decoding - the decoder can't keep up in real time.");
        }

        if (mediaSession.ViewerDisplayDrops > 0)
        {
            ImGui.TextColored(WarnColor,
                $"{mediaSession.ViewerDisplayDrops} decoded frames dropped before display - " +
                "playback isn't draining the buffer as fast as frames arrive.");
        }

        if (mediaSession.ViewerVideoError is { } viewerVideoError)
            ImGui.TextColored(ErrorColor, $"Video decoder died mid-stream: {viewerVideoError.Message}");

        DrawPlaybackControls();
        DrawWorldScreenSection();
    }

    private void UpdateAudioHealthRates(long underrunMs, long droppedMs)
    {
        DateTime now = DateTime.UtcNow;
        bool restarted = underrunMs < rateUnderrunBaseMs || droppedMs < rateDroppedBaseMs;

        if (rateSampledAt == default || restarted)
        {
            rateSampledAt = now;
            rateUnderrunBaseMs = underrunMs;
            rateDroppedBaseMs = droppedMs;
            return;
        }

        double minutes = (now - rateSampledAt).TotalMinutes;
        if (minutes < 0.25)
            return;

        underrunPerMinute = (long)((underrunMs - rateUnderrunBaseMs) / minutes);
        droppedPerMinute = (long)((droppedMs - rateDroppedBaseMs) / minutes);
        rateSampledAt = now;
        rateUnderrunBaseMs = underrunMs;
        rateDroppedBaseMs = droppedMs;
    }

    private void DrawPlaybackControls()
    {
        Configuration config = ServiceCollector.Config;

        ImGui.SetNextItemWidth(200);
        int volumePercent = (int)MathF.Round(volume * 100f);
        if (ImGui.SliderInt("Volume", ref volumePercent, 0, 100, "%d%%"))
            volume = Math.Clamp(volumePercent / 100f, 0f, 1f);

        // Applied live while dragging - unlike the placement sliders, this one is only ever local,
        // so there's no hub call to throttle and hearing the change as you drag is the point.
        ServiceCollector.Config.ViewerVolume = volume;
        if (ImGui.IsItemDeactivatedAfterEdit())
            ServiceCollector.PI.SavePluginConfig(config);

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Playback buffer (ms)", ref playbackLatencyMs, AudioPlaybackDecoder.MinLatencyBudgetMs, AudioPlaybackDecoder.MaxLatencyBudgetMs))
            config.PlaybackLatencyMs = playbackLatencyMs;

        if (ImGui.IsItemDeactivatedAfterEdit())
            ServiceCollector.PI.SavePluginConfig(config);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "How much audio is held before playing. This sets the delay for BOTH streams." +
                "The streams would be audio and video stream.");
        }

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("A/V sync (ms)", ref avSyncOffsetMs, -300, 300))
            config.AvSyncOffsetMs = avSyncOffsetMs;

        if (ImGui.IsItemDeactivatedAfterEdit())
            ServiceCollector.PI.SavePluginConfig(config);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Holds video back to line it up with audio.");
        }
    }

    private void DrawWorldScreenSection()
    {
        if (!ImGui.CollapsingHeader("In-world screen"))
            return;

        Configuration config = ServiceCollector.Config;

        if (ImGui.Checkbox("Project in world", ref projectInWorld))
        {
            config.ProjectInWorld = projectInWorld;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        ScreenPlacementInfo? placement = roomSession.ScreenPlacement ?? config.LocalScreenPlacement;

        // Outside a room there's nobody to ask, so the local fallback placement is always editable.
        bool canPlace = WorldScreenController.CanPlace(roomSession);

        ImGui.BeginDisabled(!canPlace);
        if (ImGui.Button(placement is null ? "Place here" : "Move here"))
            ApplyPlacement(WorldScreenController.PlaceInFrontOfPlayer(placement));

        if (placement is not null)
        {
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

        ImGui.EndDisabled();

        if (!canPlace)
            ImGui.TextColored(WarnColor, "Only the host, or someone they've allowed to stream, can move the screen.");

        if (placement is null)
            ImGui.TextUnformatted("No screen placed yet.");
        else if (placement.TerritoryType != ServiceCollector.ClientState.TerritoryType)
            ImGui.TextColored(WarnColor, "The screen is placed in another zone, so it isn't being drawn here.");
        else
        {
            DrawGizmoControls(canPlace);
            DrawPlacementSliders(placement, canPlace);
        }

        ImGui.Separator();

        // Below here is per-viewer taste and diagnostics - never shared, never permission-gated.
        ImGui.SliderFloat("Opacity", ref opacity, 0.05f, 1f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.WorldScreenOpacity = opacity;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.Checkbox("Positional audio", ref spatialAudio))
        {
            config.SpatialAudio = spatialAudio;
            ServiceCollector.PI.SavePluginConfig(config);
        }

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
            ImGui.TextColored(WarnColor, "The render hook isn't running - drawing over the game UI instead.");

        // Deliberately NOT compiled out of Release, unlike the depth diagnostics below. Which
        // Which pass to draw in is worked out from the frame itself rather than configured. It was
        // a setting once, and could not be one: the right value differs per machine, moves with the
        // interface, and both ways of getting it wrong look like a bug in the feature rather than a
        // number that needs tuning - the panel either hides behind the world or covers the HUD.
        if (drawUnderUi)
        {
            int passes = worldScreen().LastFrameBackBufferSwitches;
            int scenePasses = worldScreen().LastFrameSceneSwitches;
            ImGui.TextDisabled($"Render passes last frame: {passes} ({scenePasses} drawing the 3D scene)");

            // The game binds the back buffer once per HUD element, so on some machines this is over
            // a hundred rather than a handful. Drawing in every one of those means the LAST draw
            // lands after the whole interface, which puts the screen on top of it - the exact
            // opposite of what "draw underneath the game UI" is for. Worth saying outright, because
            // from the player's side it just looks like the setting does nothing.
            // The panel is drawn in the passes that draw the 3D scene, so none of them means it
            // cannot be drawn at all. Worth saying outright, because from the player's side that is
            // indistinguishable from the feature simply being off.
            if (scenePasses == 0 && passes > 0)
            {
                ImGui.TextColored(WarnColor,
                    "None of this frame's passes bound a depth buffer, so there is nowhere to draw the " +
                    "screen underneath the interface. Untick \"Draw underneath the game UI\" to draw " +
                    "it over the interface instead.");
            }

            // The in-world panel is drawn between the game's own passes; an overlay that inserts
            // passes of its own can paint over it on the frames those passes run. The popout window
            // is drawn at present time and is never affected, which is why the two behave
            // differently for the same person.
            if (OverlayDetector.Detected is { } overlay)
            {
                ImGui.TextColored(WarnColor,
                    $"{overlay} is loaded. If the in-world screen flickers while the popout window " +
                    "doesn't, turn off \"Draw under the game UI\" - the panel then draws over the HUD " +
                    "but stays steady.");
            }

            // Shown when the hook has corrected itself, so someone who changed nothing still learns
            // why - silent self-healing is fine until somebody wonders what happened.
            if (worldScreen().SceneHookFallbackReason is { } fallbackReason)
                ImGui.TextDisabled($"Adjusted automatically: {fallbackReason}.");
        }

#if DEBUG

        // Development-only. These exist to diagnose the depth pipeline (they're what identified a
        // view matrix arriving with a zeroed w column), and none of it means anything to someone
        // just watching a stream - so it's compiled out of Release rather than merely hidden.
        DrawDepthDiagnostics(config);
#endif
    }

#if DEBUG
    private void DrawDepthDiagnostics(Configuration config)
    {
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

        // Facts rather than inference: these are the two values the whole occlusion path hinges
        // on, and both are read from reverse-engineered game structures.
        if (GameSceneCamera.TryGetSceneDepth() is { } sceneDepth)
        {
            Vector2 display = ImGui.GetIO().DisplaySize;
            ImGui.TextDisabled($"Depth buffer {sceneDepth.Width}x{sceneDepth.Height}, backbuffer {(int)display.X}x{(int)display.Y}");
        }
        else
        {
            ImGui.TextColored(WarnColor, "No depth buffer available - the panel cannot be occluded.");
        }

        if (GameSceneCamera.TryGetView() is { } view)
        {
            ImGui.TextDisabled($"Depth convention reported by the game: {(view.StandardZ ? "standard Z (near = 0)" : "reversed Z (near = 1)")}");

            // The mode the renderer will actually use this frame, resolved by the same code the
            // renderer uses - not re-derived here, so the two can't drift apart and send us
            // chasing a setting that was never in effect.
            float sign = WorldScreenController.DepthSign(config);
            ImGui.TextDisabled(
                $"Active panel mode: {WorldScreenController.ResolveDepthMode(config)}, " +
                $"sign {sign:+0;-0}, scene treated as {(WorldScreenController.SceneUsesReversedZ(config) ? "reversed Z" : "standard Z")}");
            DrawDepthNumbers(view);
        }
    }

    private void DrawDepthNumbers(in SceneView view)
    {
        ScreenPlacementInfo? placement = roomSession.ScreenPlacement ?? ServiceCollector.Config.LocalScreenPlacement;
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

    private void DrawGizmoControls(bool canPlace)
    {
        WorldScreenController controller = worldScreen();

        ImGui.BeginDisabled(!canPlace);

        bool editMode = controller.EditMode;
        if (ImGui.Checkbox("Edit with handles on the screen", ref editMode))
            controller.EditMode = editMode;

        if (controller.EditMode)
        {
            ImGui.SameLine();
            int operation = (int)controller.GizmoOperation;
            ImGui.SetNextItemWidth(140f);
            if (ImGui.Combo("##gizmoOperation", ref operation, "Move\0Turn\0Tilt\0Resize\0"))
                controller.GizmoOperation = (GizmoOperation)operation;

            ImGui.TextDisabled(controller.IsDraggingGizmo
                ? "Dragging - the room sees it when you let go."
                : "Drag the handle on the screen. Roll and non-uniform stretch aren't stored, so they aren't offered.");
        }

        ImGui.EndDisabled();
    }

    private void DrawPlacementSliders(ScreenPlacementInfo placement, bool canPlace)
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

        ImGui.BeginDisabled(!canPlace);

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
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.SliderFloat("step (m)", ref nudgeStepMeters, 0.1f, 2f);

        ImGui.SliderFloat("Width (m)", ref sliderWidth, 1f, 40f);
        bool changed = ImGui.IsItemDeactivatedAfterEdit();
        bool active = ImGui.IsItemActive();

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
        ImGui.SliderFloat("Height (m)", ref sliderHeight, heightAnchor - 10f, heightAnchor + 10f);
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        active |= ImGui.IsItemActive();

        ImGui.SliderFloat("Yaw", ref sliderYaw, -180f, 180f);
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        active |= ImGui.IsItemActive();

        ImGui.SliderFloat("Tilt", ref sliderPitch, -60f, 60f);
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        active |= ImGui.IsItemActive();

        ImGui.SliderFloat("Speaker spread (m)", ref sliderSpread, 0f, 15f);
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        active |= ImGui.IsItemActive();

        ImGui.SliderFloat("Audio full volume within (m)", ref sliderFalloffStart, 0f, 50f);
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        active |= ImGui.IsItemActive();

        ImGui.SliderFloat("Audio silent beyond (m)", ref sliderFalloffEnd, 1f, 120f);
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        active |= ImGui.IsItemActive();

        ImGui.EndDisabled();

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
