using System.Numerics;
using Dalamud.Bindings.ImGui;
using ScreenShare.Plugin.Networking.Models;
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
    private RawTextureWrap? blackTexture;
    private bool blackTextureFailed;

    public int LastFrameBackBufferSwitches => sceneHook?.LastFrameBackBufferSwitches ?? 0;

    public int LastFrameSceneSwitches => sceneHook?.LastFrameSceneSwitches ?? 0;

    public string? SceneHookFallbackReason => sceneHook?.FallbackReason;

    public bool IsDrawingUnderUi => sceneHook?.IsActive == true;

    /// <summary>
    /// ReShade/GShade reorder the frame's passes, which makes the in-scene draw flicker, so drawing
    /// under the UI is skipped while one is loaded. The saved setting is left alone.
    /// </summary>
    public static string? UnderUiBlockedBy => OverlayDetector.Detected;

    private static bool WantsDrawUnderUi(Configuration config) => config.WorldScreenDrawUnderUi && UnderUiBlockedBy is null;

    public bool EditMode { get; set; }

    public GizmoOperation GizmoOperation { get; set; } = GizmoOperation.Move;

    public bool IsDraggingGizmo { get; private set; }

    public bool IsProjecting { get; private set; }

    /// <summary>
    /// A placement being edited (gizmo drag or slider drag) that only this client draws; the room
    /// gets the final placement once the edit is released.
    /// </summary>
    public ScreenPlacementInfo? PreviewPlacement { get; set; }

    public void Update(
        GameAdapterDevice? device,
        RawTextureWrap? videoTexture,
        RoomSessionService roomSession,
        MediaSessionService mediaSession)
    {
        Configuration config = ServiceCollector.Config;
        IsProjecting = false;

        mediaSession.SetViewerVolume(config.ViewerVolume);

        if (!config.ShowWorldScreen)
        {
            if (IsDraggingGizmo)
            {
                WorldScreenGizmo.Reset();
                IsDraggingGizmo = false;
            }

            PreviewPlacement = null;
            ReleaseDevice();
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);
            return;
        }

        sceneHook?.EndFrame();

        if (!TryResolvePlacement(roomSession, out ScreenPlacementInfo? placement) || device is null)
        {
            PreviewPlacement = null;
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);
            return;
        }

        // A preview keeps the version it was started from; drop it once the room's placement moves on.
        if (PreviewPlacement is { } preview && preview.Version == placement!.Version && CanPlace(roomSession))
            placement = preview;
        else
            PreviewPlacement = null;

        bool isLive = mediaSession.IsViewing && videoTexture is not null;
        RawTextureWrap? panelTexture = isLive ? videoTexture : TryGetBlackTexture(device);
        if (panelTexture is null)
        {
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);
            return;
        }

        SceneView? view = GameSceneCamera.TryGetView();
        if (view is not { } scene)
        {
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);
            return;
        }

        placement = ApplyGizmo(roomSession, placement!, panelTexture, scene);

        PanelFrame frame = BuildFrame(placement!, panelTexture.Width, panelTexture.Height);

        if (isLive)
            UpdateAudio(config, placement!, frame, scene, mediaSession);
        else
            mediaSession.SetViewerSpeakerGains(SpeakerGains.Identity);

        SubmitDraw(config, frame, scene, panelTexture, device);
        IsProjecting = isLive;
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
                PreviewPlacement = null;
            }

            return placement;
        }

        float aspect = videoTexture.Width > 0 ? (float)videoTexture.Height / videoTexture.Width : 9f / 16f;

        WorldScreenGizmo.GizmoResult result = WorldScreenGizmo.Draw(
            scene, placement, aspect, GizmoOperation, ImGui.GetIO().DisplaySize);

        IsDraggingGizmo = result.IsDragging;

        if (result.IsDragging)
        {
            // Fed back in next frame: ImGuizmo's rotate applies per-frame deltas to the matrix it is given.
            PreviewPlacement = result.Placement;
        }
        else if (result.Finished)
        {
            PreviewPlacement = null;
            ApplyPlacement(roomSession, Stamp(result.Placement));
        }

        return result.Placement;
    }

    private static ScreenPlacementInfo Stamp(ScreenPlacementInfo placement) =>
        placement with { Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

    public static bool CanPlace(RoomSessionService roomSession) => roomSession.InRoom && roomSession.CanPlaceScreen;

    public static void ApplyPlacement(RoomSessionService roomSession, ScreenPlacementInfo? placement)
    {
        if (CanPlace(roomSession))
            roomSession.UpdateScreenPlacement(placement);
    }

    private static bool TryResolvePlacement(RoomSessionService roomSession, out ScreenPlacementInfo? placement)
    {
        placement = roomSession.InRoom ? roomSession.ScreenPlacement : null;

        return placement is not null && IsPlacedHere(placement);
    }

    /// <summary>The world the local player is standing on, or 0 while that isn't known (loading screens, title).</summary>
    public static uint CurrentWorldId => ServiceCollector.Objects.LocalPlayer?.CurrentWorld.RowId ?? 0;

    /// <summary>
    /// Whether the placement is in this zone on this world. A placement without a world (from an older
    /// client or relay) shows on every world.
    /// </summary>
    public static bool IsPlacedHere(ScreenPlacementInfo placement)
    {
        if (placement.TerritoryType != ServiceCollector.ClientState.TerritoryType)
            return false;

        return placement.WorldId == 0 || placement.WorldId == CurrentWorldId;
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

        SceneDepthBuffer? depth = GameSceneCamera.TryGetSceneDepth();

        bool drawsIntoScene = WantsDrawUnderUi(config)
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

        if (WantsDrawUnderUi(config) && TryGetSceneHook(device, out ScenePanelHook? hook) && hook!.IsActive)
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

    private RawTextureWrap? TryGetBlackTexture(GameAdapterDevice device)
    {
        if (blackTexture is not null || blackTextureFailed)
            return blackTexture;

        try
        {
            blackTexture = SolidColorTexture.Create(device.GameDevice, 0xFF000000, 16, 9);
        }
        catch (Exception ex)
        {
            blackTextureFailed = true;
            ServiceCollector.PluginLog.Error(ex, "ScreenShare could not create the idle in-world screen texture; the screen will only appear while a stream is live.");
        }

        return blackTexture;
    }

    public void ReleaseDevice()
    {
        blackTexture?.Dispose();
        blackTexture = null;
        blackTextureFailed = false;
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

        float facing = player.Rotation;
        var forward = new Vector3(MathF.Sin(facing), 0f, MathF.Cos(facing));
        Vector3 position = player.Position + (forward * PlaceDistanceMeters);

        return new ScreenPlacementInfo(
            ServiceCollector.ClientState.TerritoryType,
            position.X,
            player.Position.Y + PlaceHeightMeters,
            position.Z,
            facing,
            Pitch: 0f,
            WidthMeters: existing?.WidthMeters ?? 6f,
            SpeakerSpread: existing?.SpeakerSpread ?? 1f,
            FalloffStart: existing?.FalloffStart ?? 8f,
            FalloffEnd: existing?.FalloffEnd ?? 25f,
            Version: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            WorldId: player.CurrentWorld.RowId);
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
