using System.Numerics;
using Dalamud.Bindings.ImGui;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Services;

public readonly record struct PanelFrame(Vector3 Center, Vector3 RightHalf, Vector3 UpHalf, Vector3 Right, Vector3 Forward);

public sealed class WorldScreenController : IDisposable
{
    private const float PlaceDistanceMeters = 4f;
    private const float PlaceHeightMeters = 2f;

    private WorldPanelDispatch? dispatch;
    private bool dispatchFailed;
    private ScenePanelHook? sceneHook;
    private bool sceneHookFailed;

    public int LastFrameBackBufferSwitches => sceneHook?.LastFrameBackBufferSwitches ?? 0;

    public int LastFrameSceneSwitches => sceneHook?.LastFrameSceneSwitches ?? 0;

    public string? SceneHookFallbackReason => sceneHook?.FallbackReason;

    public bool IsDrawingUnderUi => sceneHook?.IsActive == true;

    public bool EditMode { get; set; }

    public GizmoOperation GizmoOperation { get; set; } = GizmoOperation.Move;

    public bool IsDraggingGizmo { get; private set; }

    public bool IsProjecting { get; private set; }

    public void Update(
        GameAdapterDevice? device,
        RawTextureWrap? videoTexture,
        RoomSessionService roomSession,
        MediaSessionService mediaSession)
    {
        Configuration config = ServiceCollector.Config;
        IsProjecting = false;

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

        placement = ApplyGizmo(roomSession, placement!, videoTexture, scene);

        PanelFrame frame = BuildFrame(placement!, videoTexture.Width, videoTexture.Height);

        UpdateAudio(config, placement!, frame, scene, mediaSession);
        SubmitDraw(config, frame, scene, videoTexture, device);
        IsProjecting = true;
    }

    private ScreenPlacementInfo ApplyGizmo(
        RoomSessionService roomSession, ScreenPlacementInfo placement, RawTextureWrap videoTexture, SceneView scene)
    {
        if (!EditMode || !CanPlace(roomSession))
        {
            if (IsDraggingGizmo)
            {
                WorldScreenGizmo.Reset();
                IsDraggingGizmo = false;
            }

            return placement;
        }

        float aspect = videoTexture.Width > 0 ? (float)videoTexture.Height / videoTexture.Width : 9f / 16f;

        WorldScreenGizmo.GizmoResult result = WorldScreenGizmo.Draw(
            scene, placement, aspect, GizmoOperation, ImGui.GetIO().DisplaySize);

        IsDraggingGizmo = result.IsDragging;

        if (result.Finished)
            ApplyPlacement(roomSession, result.Placement);

        return result.Placement;
    }

    public static bool CanPlace(RoomSessionService roomSession) => !roomSession.InRoom || roomSession.CanPlaceScreen;

    public static void ApplyPlacement(RoomSessionService roomSession, ScreenPlacementInfo? placement)
    {
        if (roomSession.InRoom)
        {
            roomSession.UpdateScreenPlacement(placement);
            return;
        }

        ServiceCollector.Config.LocalScreenPlacement = placement;
        ServiceCollector.PI.SavePluginConfig(ServiceCollector.Config);
    }

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

        // Always asked for. Occlusion is what makes the panel read as an object standing in the
        // world rather than a picture pasted over it, so it is not a preference - the only reason
        // to go without is the game not handing over a usable depth buffer, which is handled below.
        SceneDepthBuffer? depth = GameSceneCamera.TryGetSceneDepth();

        // Falling back to an unoccluded overlay rather than refusing to draw: a panel that shows
        // through walls is a far better failure than no panel at all, and it's the same path the
        // depth-test toggle takes deliberately.
        // The scene hook now draws the panel INTO the scene with depth written, so the hardware
        // depth test decides occlusion and the shader must not also clip against a sampled copy -
        // at the point it draws, that copy is the previous frame's, and clipping against it would
        // punch holes wherever the world has since moved.
        //
        // The ImGui fallback still needs the shader test: it composites after the whole frame, long
        // past any chance of the depth buffer accepting a write.
        bool drawsIntoScene = config.WorldScreenDrawUnderUi
            && TryGetSceneHook(device, out ScenePanelHook? sceneHookForMode)
            && sceneHookForMode!.IsActive;

        PanelDepthMode mode = depth is null || (drawsIntoScene && config.WorldScreenDiagnostic == 0)
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
            hook.Submit(in draw);
            return;
        }

        panelDispatch!.Submit(in draw);
    }

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

    public static PanelDepthMode ResolveDepthMode(Configuration config) => config.WorldScreenDiagnostic switch
    {
        >= 3 => PanelDepthMode.ShowOcclusionTest,
        2 => PanelDepthMode.ShowDepthComparison,
        1 => PanelDepthMode.ShowDepthBuffer,
        _ => PanelDepthMode.Occlude,
    };

    public static bool SceneUsesReversedZ(Configuration config) => !config.WorldScreenStandardDepth;

    public static float DepthSign(Configuration config) => SceneUsesReversedZ(config) ? -1f : 1f;

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

    public void ReleaseDevice()
    {
        dispatch?.Dispose();
        dispatch = null;
        dispatchFailed = false;
        sceneHook?.Dispose();
        sceneHook = null;
        sceneHookFailed = false;
    }

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
