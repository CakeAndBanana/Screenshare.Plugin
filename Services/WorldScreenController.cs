using System.Numerics;
using Dalamud.Bindings.ImGui;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Services;

/// <summary>The panel's world-space frame: centre plus half-extent axes, which is exactly the form
/// <see cref="WorldPanelRenderer"/>'s vertex shader wants, and also what the speaker positions are
/// derived from.</summary>
public readonly record struct PanelFrame(Vector3 Center, Vector3 RightHalf, Vector3 UpHalf, Vector3 Right, Vector3 Forward);

/// <summary>
/// Per-frame glue for the in-world screen: turns a <see cref="ScreenPlacementInfo"/> plus the
/// decoded video's aspect ratio into panel geometry, hands that to the renderer, and points the
/// audio at the matching virtual speakers.
///
/// Owns the decision of whether the panel should be visible at all this frame. That decision has
/// to be one place rather than scattered, because audio depends on it too: any frame the panel
/// isn't drawn, playback must fall back to <see cref="SpeakerGains.Identity"/> rather than being
/// left attenuated by a screen that's no longer on screen.
/// </summary>
public sealed class WorldScreenController : IDisposable
{
    /// <summary>How far in front of the player "Place here" drops the screen, and how high its
    /// centre sits. A screen placed exactly on top of the player would be unusable, and one at
    /// ground level looks like a rug.</summary>
    private const float PlaceDistanceMeters = 4f;
    private const float PlaceHeightMeters = 2f;

    private WorldPanelDispatch? dispatch;
    private bool dispatchFailed;
    private ScenePanelHook? sceneHook;
    private bool sceneHookFailed;

    /// <summary>How many times the game switched to the back buffer last frame, for tuning which
    /// one the in-scene draw attaches to. Zero when the hook isn't running.</summary>
    public int LastFrameBackBufferSwitches => sceneHook?.LastFrameBackBufferSwitches ?? 0;

    /// <summary>Whether the panel is currently being drawn underneath the game's UI.</summary>
    public bool IsDrawingUnderUi => sceneHook?.IsActive == true;

    /// <summary>Draws the panel for this frame if it should be drawn, and sets the audio mix to
    /// match either way. Call once per frame from the render thread.</summary>
    public void Update(
        GameAdapterDevice? device,
        RawTextureWrap? videoTexture,
        RoomSessionService roomSession,
        MediaSessionService mediaSession)
    {
        Configuration config = ServiceCollector.Config;

        // Unconditional: the hook counts back-buffer switches every frame regardless of whether a
        // panel was queued, and that counter has to roll over even on frames that draw nothing.
        sceneHook?.EndFrame();

        // Also unconditional, and re-applied every frame rather than on change: the audio decoder
        // is created lazily when the stream's audio metadata arrives, which is long after the user
        // last touched the slider, so a one-shot apply would be lost for every session.
        mediaSession.SetViewerVolume(config.ViewerVolume);

        if (!TryResolveVisible(config, roomSession, mediaSession, videoTexture, out ScreenPlacementInfo? placement)
            || videoTexture is null
            || device is null)
        {
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);
            return;
        }

        SceneView? view = GameSceneCamera.TryGetView();
        if (view is not { } scene)
        {
            // No camera means a loading screen or character select - not an error, just nothing to
            // project onto. Audio still needs resetting, or it stays stuck at the last mix.
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);
            return;
        }

        PanelFrame frame = BuildFrame(placement!, videoTexture.Width, videoTexture.Height);

        UpdateAudio(config, placement!, frame, scene, mediaSession);
        SubmitDraw(config, frame, scene, videoTexture, device);
    }

    /// <summary>Whether a panel should be drawn this frame, and which placement it uses. The
    /// room's placement wins over the local fallback whenever it exists, so the two can't fight.</summary>
    private static bool TryResolveVisible(
        Configuration config,
        RoomSessionService roomSession,
        MediaSessionService mediaSession,
        RawTextureWrap? videoTexture,
        out ScreenPlacementInfo? placement)
    {
        placement = roomSession.ScreenPlacement ?? config.LocalScreenPlacement;

        if (!config.ProjectInWorld || !mediaSession.IsViewing || videoTexture is null || placement is null)
            return false;

        // A world position from another zone is meaningless here - the coordinates would land
        // somewhere arbitrary in whatever map the player is standing in now.
        return placement.TerritoryType == ServiceCollector.ClientState.TerritoryType;
    }

    /// <summary>Builds the panel's world frame. Height comes from the stream's own aspect ratio
    /// rather than being stored, so switching capture resolution mid-session resizes the panel
    /// instead of stretching the picture.</summary>
    public static PanelFrame BuildFrame(ScreenPlacementInfo placement, int textureWidth, int textureHeight)
    {
        float halfWidth = MathF.Max(placement.WidthMeters, 0.1f) * 0.5f;
        float aspect = textureWidth > 0 ? (float)textureHeight / textureWidth : 9f / 16f;
        float halfHeight = halfWidth * aspect;

        Matrix4x4 orientation = Matrix4x4.CreateFromYawPitchRoll(placement.Yaw, placement.Pitch, 0f);
        Vector3 right = Vector3.TransformNormal(Vector3.UnitX, orientation);
        Vector3 up = Vector3.TransformNormal(Vector3.UnitY, orientation);
        Vector3 forward = Vector3.TransformNormal(Vector3.UnitZ, orientation);

        return new PanelFrame(
            new Vector3(placement.X, placement.Y, placement.Z),
            right * halfWidth,
            up * halfHeight,
            right,
            forward);
    }

    /// <summary>Speakers flank the panel's edges, pushed out by the spread setting - derived from
    /// the placement every frame rather than stored, so they can never drift out of sync with the
    /// screen they belong to.
    ///
    /// The LEFT channel sits on the panel's +X side. That looks backwards written down, and is the
    /// same handedness that made the picture come out mirrored: the panel's +X axis points to the
    /// viewer's left when seen from the side it faces. Assigning by the axis name instead would put
    /// the left channel on the listener's right - the stereo image mirrored exactly as the video
    /// was.</summary>
    public static (Vector3 Left, Vector3 Right) SpeakerPositions(ScreenPlacementInfo placement, in PanelFrame frame)
    {
        Vector3 offset = frame.Right * (frame.RightHalf.Length() + MathF.Max(placement.SpeakerSpread, 0f));
        return (frame.Center + offset, frame.Center - offset);
    }

    private static void UpdateAudio(
        Configuration config,
        ScreenPlacementInfo placement,
        in PanelFrame frame,
        in SceneView scene,
        MediaSessionService mediaSession)
    {
        if (!config.SpatialAudio)
        {
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);
            return;
        }

        (Vector3 left, Vector3 right) = SpeakerPositions(placement, frame);

        // The camera, not the character, is the listener: it's what the player is looking through,
        // so panning that ignored a swung camera would disagree with what's on screen.
        SpeakerGains gains = SpeakerMix.Compute(
            scene.CameraPosition,
            scene.CameraForward,
            left,
            right,
            placement.FalloffStart,
            placement.FalloffEnd);

        mediaSession.SetViewerSpeakerGains(gains);
    }

    private void SubmitDraw(
        Configuration config,
        in PanelFrame frame,
        in SceneView scene,
        RawTextureWrap videoTexture,
        GameAdapterDevice device)
    {
        if (!TryGetDispatch(device, out WorldPanelDispatch? panelDispatch))
            return;

        bool wantsDepth = config.WorldScreenDepthTest || config.WorldScreenDiagnostic > 0;
        SceneDepthBuffer? depth = wantsDepth ? GameSceneCamera.TryGetSceneDepth() : null;

        // Falling back to an unoccluded overlay rather than refusing to draw: a panel that shows
        // through walls is a far better failure than no panel at all, and it's the same path the
        // depth-test toggle takes deliberately.
        PanelDepthMode mode = depth is null
            ? PanelDepthMode.None
            : ResolveDepthMode(config);

        var draw = new WorldPanelDraw(
            scene.ViewProjection,
            frame.Center,
            frame.RightHalf,
            frame.UpHalf,
            Math.Clamp(config.WorldScreenOpacity, 0.05f, 1f),
            mode,
            DepthSign(config),
            ImGui.GetIO().DisplaySize,
            (nint)videoTexture.Handle.Handle,
            depth?.ShaderResourceView ?? nint.Zero);

        // Under the UI when the hook is up and wanted, over it otherwise. The fallback matters:
        // a hook that failed to install, or disabled itself after a draw failure, must not take
        // the whole feature down with it.
        if (config.WorldScreenDrawUnderUi && TryGetSceneHook(device, out ScenePanelHook? hook) && hook!.IsActive)
        {
            hook.DrawAtOccurrence = config.WorldScreenHookOccurrence;
            hook.Submit(in draw);
            return;
        }

        panelDispatch!.Submit(in draw);
    }

    /// <summary>Builds the render hook on first use. A failure disables it for the session rather
    /// than being retried every frame - installing a hook is not something that becomes more likely
    /// to work on the tenth attempt, and the log line would repeat forever.</summary>
    private bool TryGetSceneHook(GameAdapterDevice device, out ScenePanelHook? hook)
    {
        hook = sceneHook;
        if (hook is not null)
            return true;

        if (sceneHookFailed)
            return false;

        try
        {
            sceneHook = hook = new ScenePanelHook(device.GameDevice);
            return true;
        }
        catch (Exception ex)
        {
            sceneHookFailed = true;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare could not create the in-scene panel renderer; drawing over the game UI instead.");
            return false;
        }
    }

    /// <summary>Which view the panel paints, given the current settings and a usable depth buffer.
    /// Public and static so the UI can report exactly what the renderer is doing rather than
    /// re-deriving it and risking the two disagreeing - the whole point of a diagnostic is that it
    /// can be trusted.</summary>
    public static PanelDepthMode ResolveDepthMode(Configuration config) => config.WorldScreenDiagnostic switch
    {
        >= 3 => PanelDepthMode.ShowOcclusionTest,
        2 => PanelDepthMode.ShowDepthComparison,
        1 => PanelDepthMode.ShowDepthBuffer,
        _ => config.WorldScreenDepthTest ? PanelDepthMode.Occlude : PanelDepthMode.None,
    };

    /// <summary>Whether the game's depth buffer runs near = 1, far = 0. Everything about the
    /// occlusion test follows from this one fact, which is why it is a single setting rather than
    /// separate knobs for the conversion and the comparison direction - those must always agree,
    /// and letting them be set independently produced a state where the panel vanished entirely.</summary>
    public static bool SceneUsesReversedZ(Configuration config) => !config.WorldScreenStandardDepth;

    /// <summary>Which way round the occlusion comparison runs. Standard Z puts near at 0, so a
    /// smaller depth is nearer and the panel survives where the scene is further away; reversed Z
    /// inverts that, and so does the sign. Derived from the same setting as the range conversion
    /// because the two are the same fact seen twice.</summary>
    public static float DepthSign(Configuration config) => SceneUsesReversedZ(config) ? -1f : 1f;

    /// <summary>Builds the renderer on first use - it needs the game device, which doesn't exist
    /// until a watch session has started. A construction failure (shader compile, out of memory)
    /// disables the in-world screen for the session rather than being retried every frame, which
    /// would mean one log line per frame forever.</summary>
    private bool TryGetDispatch(GameAdapterDevice device, out WorldPanelDispatch? panelDispatch)
    {
        panelDispatch = dispatch;
        if (panelDispatch is not null)
            return true;

        if (dispatchFailed)
            return false;

        try
        {
            dispatch = panelDispatch = new WorldPanelDispatch(device.GameDevice);
            return true;
        }
        catch (Exception ex)
        {
            dispatchFailed = true;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare could not create the in-world screen renderer; projection is disabled.");
            return false;
        }
    }

    /// <summary>Drops the renderer and its GPU resources - called when the device is being torn
    /// down (device-removed recovery, or plugin unload), after which the next frame that wants a
    /// panel rebuilds it against whatever device exists then.</summary>
    public void ReleaseDevice()
    {
        dispatch?.Dispose();
        dispatch = null;
        dispatchFailed = false;
        sceneHook?.Dispose();
        sceneHook = null;
        sceneHookFailed = false;
    }

    /// <summary>Builds the placement for "Place here": a screen standing in front of the player,
    /// turned to face them.</summary>
    public static ScreenPlacementInfo? PlaceInFrontOfPlayer(ScreenPlacementInfo? existing)
    {
        if (ServiceCollector.Objects.LocalPlayer is not { } player)
            return null;

        // FFXIV's object rotation is a yaw about Y with 0 facing +Z, so this is the direction the
        // player is looking.
        float facing = player.Rotation;
        var forward = new Vector3(MathF.Sin(facing), 0f, MathF.Cos(facing));
        Vector3 position = player.Position + (forward * PlaceDistanceMeters);

        return new ScreenPlacementInfo(
            ServiceCollector.ClientState.TerritoryType,
            position.X,
            player.Position.Y + PlaceHeightMeters,
            position.Z,
            // Yaw equal to the player's own rotation is what orients the panel's +X axis along the
            // player's right, which is what makes the picture read the right way round rather than
            // mirrored. Worth re-checking by eye if the game's handedness ever surprises us: the
            // symptom would be mirrored text on the stream, not a missing panel.
            facing,
            Pitch: 0f,
            WidthMeters: existing?.WidthMeters ?? 6f,
            SpeakerSpread: existing?.SpeakerSpread ?? 1f,
            FalloffStart: existing?.FalloffStart ?? 8f,
            FalloffEnd: existing?.FalloffEnd ?? 25f,
            Version: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Shifts a placement along the panel's OWN axes rather than world X/Z, so "left" and
    /// "forward" mean what they look like from in front of the screen however it happens to be
    /// rotated. Sideways is negated for the same handedness reason as the speakers and the picture:
    /// the panel's +X points to the viewer's left.</summary>
    public static ScreenPlacementInfo Nudge(ScreenPlacementInfo placement, float sidewaysMeters, float forwardMeters)
    {
        PanelFrame frame = BuildFrame(placement, 16, 9);
        Vector3 moved = new Vector3(placement.X, placement.Y, placement.Z)
                        + (frame.Right * -sidewaysMeters)
                        + (frame.Forward * forwardMeters);

        return placement with
        {
            X = moved.X,
            Z = moved.Z,
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    public void Dispose() => ReleaseDevice();
}
