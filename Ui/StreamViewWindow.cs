using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

/// <summary>
/// Popout window showing whichever room member ShareWindow is currently watching - separate from
/// the main room UI so it can be moved/resized/left open independently. Owns none of the actual
/// watch-session lifecycle itself (that stays entirely in ShareWindow, the single owner of
/// RoomSessionService/MediaSessionService/the decoded texture); this class only renders what it's
/// given and reports back when the user closes it. That seam is what the "In-world screen" section
/// below grew into: it only edits where the panel hangs, while this window stays the thing that
/// starts and stops watching, so projecting in the world never became a second way to control the
/// session.
/// </summary>
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
    private bool depthTest = ServiceCollector.Config.WorldScreenDepthTest;
    private bool spatialAudio = ServiceCollector.Config.SpatialAudio;
    private bool standardDepth = ServiceCollector.Config.WorldScreenStandardDepth;
    private bool drawUnderUi = ServiceCollector.Config.WorldScreenDrawUnderUi;
    private int hookOccurrence = ServiceCollector.Config.WorldScreenHookOccurrence;
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

    /// <summary>Fires for every way this window can close - the X button, Esc, or ShareWindow
    /// setting IsOpen = false itself (e.g. the watched stream died, or the room was left) - since
    /// the underlying Dalamud Window only ever calls this once per open-to-close transition
    /// regardless of which triggered it. Closing this window is the only thing that stops the
    /// watch session; there's no separate "Stop watching" button duplicating that.</summary>
    public override void OnClose() => onClosed();

    /// <summary>Set here rather than in Draw() so it's current before ImGui.Begin uses it, not a
    /// frame behind.</summary>
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
                $"{mediaSession.ViewerDisplayDrops} decoded frames dropped before display - decode is bursting ahead of playback.");
        }

        if (mediaSession.ViewerVideoError is { } viewerVideoError)
            ImGui.TextColored(ErrorColor, $"Video decoder died mid-stream: {viewerVideoError.Message}");

        DrawPlaybackControls();
        DrawWorldScreenSection();
    }

    /// <summary>Converts the cumulative underrun/drop totals into a per-minute rate over a rolling
    /// window, so an ongoing problem is distinguishable from one that happened once at startup.
    /// Resets when a counter goes backwards, which means a new watch session began.</summary>
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

    /// <summary>Volume and A/V sync for the stream being watched. Deliberately outside the in-world
    /// section and above it: these apply to any way of watching, and burying playback volume inside
    /// a projection feature nobody has enabled would make it unfindable.</summary>
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
                "How much audio is held before playing. This sets the delay for BOTH streams - " +
                "video is synchronised to audio, so it can never be shown sooner than the audio it " +
                "belongs with. Lower is more responsive but more likely to gap when the network " +
                "stutters. Applied when a stream starts, so restart watching after changing it.");
        }

        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("A/V sync (ms)", ref avSyncOffsetMs, -300, 300))
            config.AvSyncOffsetMs = avSyncOffsetMs;

        if (ImGui.IsItemDeactivatedAfterEdit())
            ServiceCollector.PI.SavePluginConfig(config);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Holds video back to line it up with audio. Raise it if the audio sounds late " +
                "against the picture. How much is needed depends on your output device's own " +
                "latency, so there is no single right value. " +
                "Large values need a deeper frame buffer, which is sized when a stream starts - " +
                "stop and restart watching after a big change.");
        }
    }

    /// <summary>Controls for projecting the stream onto a panel in the game world. Where the panel
    /// hangs is room state rather than personal preference, so those controls are disabled for
    /// anyone the room hasn't granted streaming rights to - the relay re-checks the same
    /// permission, making this the readable half of the guard rather than the enforcing half.</summary>
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
        bool canPlace = !roomSession.InRoom || roomSession.CanPlaceScreen;

        ImGui.BeginDisabled(!canPlace);
        if (ImGui.Button(placement is null ? "Place here" : "Move here"))
            ApplyPlacement(WorldScreenController.PlaceInFrontOfPlayer(placement));

        if (placement is not null)
        {
            ImGui.SameLine();

            // A way back from a placement that's been dragged somewhere unusable - a 40m panel a
            // few metres from your face fills the whole view, and from inside it there's no visual
            // cue that size is the problem rather than the projection being broken.
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
            DrawPlacementSliders(placement, canPlace);

        ImGui.Separator();

        // Below here is per-viewer taste and diagnostics - never shared, never permission-gated.
        ImGui.SliderFloat("Opacity", ref opacity, 0.05f, 1f);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.WorldScreenOpacity = opacity;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.Checkbox("Hide behind walls", ref depthTest))
        {
            config.WorldScreenDepthTest = depthTest;
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

#if DEBUG
        if (drawUnderUi)
        {
            // Which back-buffer switch to attach to depends on the game's pass structure, so it's
            // dialled in against a live frame count rather than hardcoded.
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Hook occurrence", ref hookOccurrence))
            {
                hookOccurrence = Math.Max(hookOccurrence, 0);
                config.WorldScreenHookOccurrence = hookOccurrence;
                ServiceCollector.PI.SavePluginConfig(config);
            }

            ImGui.TextDisabled($"Back-buffer switches last frame: {worldScreen().LastFrameBackBufferSwitches}");
        }

        // Development-only. These exist to diagnose the depth pipeline (they're what identified a
        // view matrix arriving with a zeroed w column), and none of it means anything to someone
        // just watching a stream - so it's compiled out of Release rather than merely hidden.
        DrawDepthDiagnostics(config);
#endif
    }

#if DEBUG
    /// <summary>The occlusion test has two halves that can each be wrong independently - whether
    /// the depth buffer holds usable data, and whether its values share a scale and direction with
    /// our own fragment depth. These surface both, so a panel that refuses to hide behind walls
    /// can be narrowed down here instead of by guesswork.</summary>
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

    /// <summary>The actual numbers behind the occlusion test, because colour is a hopeless readout
    /// when both values sit near the bottom of the 0-1 range - a panel depth of 0.02 and one of
    /// 0.0000 look identical on screen and behave completely differently.
    ///
    /// The panel's depth is computed here exactly as the vertex shader and rasterizer compute it
    /// (transform by the view-projection, then divide by w), so this is the same number the
    /// comparison uses rather than a re-derivation of it. The projection's depth-row elements are
    /// shown alongside because they identify the convention outright - which beats inferring it.</summary>
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

    /// <summary>Every slider commits on IsItemDeactivatedAfterEdit rather than per-frame - the same
    /// idiom the config saves already use. It matters more here than there: without it, dragging a
    /// slider would fire a hub call and a room-wide broadcast on every rendered frame.</summary>
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
        float heightAnchor = sliderSource?.Y ?? placement.Y;
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

    /// <summary>Routes a placement edit to the room when there is one, and to local config
    /// otherwise, so the in-world screen still works while watching outside a room.</summary>
    private void ApplyPlacement(ScreenPlacementInfo? placement)
    {
        if (roomSession.InRoom)
        {
            roomSession.UpdateScreenPlacement(placement);
            return;
        }

        ServiceCollector.Config.LocalScreenPlacement = placement;
        ServiceCollector.PI.SavePluginConfig(ServiceCollector.Config);
    }
}
