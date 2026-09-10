using System.Numerics;
using ScreenShare.Plugin.Services;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using RenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace ScreenShare.Plugin.Rendering;

public readonly record struct SceneView(
    Matrix4x4 ViewProjection,

    Matrix4x4 View,
    Matrix4x4 Projection,
    Vector3 CameraPosition,
    Vector3 CameraForward,
    bool StandardZ,
    float NearPlane,
    float FarPlane);

public readonly record struct SceneDepthBuffer(nint ShaderResourceView, int Width, int Height);

public static unsafe class GameSceneCamera
{
    private static bool loggedViewFailure;
    private static bool loggedDepthFailure;

    public static SceneView? TryGetView()
    {
        SceneCameraManager* manager = SceneCameraManager.Instance();
        if (manager is null || manager->CurrentCamera is null)
            return null;

        RenderCamera* render = manager->CurrentCamera->RenderCamera;
        if (render is null)
            return null;

        Matrix4x4 view = render->ViewMatrix;
        Matrix4x4 projection = render->ProjectionMatrix;

        view.M14 = 0f;
        view.M24 = 0f;
        view.M34 = 0f;
        view.M44 = 1f;

        if (projection.M44 == 0f && projection.M34 == 0f)
        {
            if (!loggedViewFailure)
            {
                loggedViewFailure = true;
                ServiceCollector.PluginLog.Warning("ScreenShare: game projection matrix looks uninitialized; in-world screen skipped.");
            }

            return null;
        }

        var forward = -new Vector3(view.M13, view.M23, view.M33);
        forward = forward.LengthSquared() > 0f ? Vector3.Normalize(forward) : new Vector3(0f, 0f, 1f);

        return new SceneView(
            view * projection,
            view,
            projection,
            new Vector3(render->Origin.X, render->Origin.Y, render->Origin.Z),
            forward,
            render->StandardZ,
            render->NearPlane,
            render->FarPlane);
    }

    public static SceneDepthBuffer? TryGetSceneDepth()
    {
        RenderTargetManager* manager = RenderTargetManager.Instance();
        if (manager is null || manager->DepthStencil is null)
            return null;

        var depth = manager->DepthStencil;
        nint srv = (nint)depth->D3D11ShaderResourceView;
        if (srv == nint.Zero)
        {
            if (!loggedDepthFailure)
            {
                loggedDepthFailure = true;
                ServiceCollector.PluginLog.Warning(
                    "ScreenShare: the game's depth buffer has no shader resource view; the in-world screen will draw without occlusion.");
            }

            return null;
        }

        return new SceneDepthBuffer(srv, (int)depth->ActualWidth, (int)depth->ActualHeight);
    }
}
