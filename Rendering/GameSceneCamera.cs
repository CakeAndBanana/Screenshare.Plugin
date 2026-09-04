using System.Numerics;
using ScreenShare.Plugin.Services;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using RenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace ScreenShare.Plugin.Rendering;

/// <summary>The game's current camera, in the form the world panel's vertex shader needs.
/// <see cref="StandardZ"/> reports the depth convention: true means near maps to 0 and far to 1,
/// false means the reversed-Z arrangement, which flips the direction of every depth comparison.</summary>
public readonly record struct SceneView(
    Matrix4x4 ViewProjection,
    Matrix4x4 Projection,
    Vector3 CameraPosition,
    Vector3 CameraForward,
    bool StandardZ,
    float NearPlane,
    float FarPlane);

/// <summary>The game's scene depth buffer as something bindable. <see cref="Width"/>/<see cref="Height"/>
/// are the DEPTH texture's own size, which is not the backbuffer's whenever the player has the 3D
/// resolution scale off 100% - the shader samples by normalized UV precisely so that mismatch
/// doesn't matter, and these are carried only for diagnostics.</summary>
public readonly record struct SceneDepthBuffer(nint ShaderResourceView, int Width, int Height);

/// <summary>
/// The one and only place this plugin touches FFXIVClientStructs. Kept deliberately narrow: those
/// struct layouts are reverse-engineered and can shift on any game patch, so containing every
/// pointer walk here means a patch break disables the in-world screen instead of taking the plugin
/// (capture, encode, relay, the ImGui window) down with it.
///
/// Every accessor returns null rather than throwing. That isn't defensive padding - loading
/// screens, character select, cutscene transitions and the frames right after a device reset all
/// legitimately have no current camera or no allocated depth buffer, and they happen constantly.
/// </summary>
public static unsafe class GameSceneCamera
{
    private static bool loggedViewFailure;
    private static bool loggedDepthFailure;

    /// <summary>Must only be called from the render thread (UiBuilder.Draw or an ImGui draw
    /// callback) - it dereferences live game state with no synchronization of its own.</summary>
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

        // The game hands back a view matrix whose fourth column is zeroed, so a homogeneous point
        // loses its w on the way into view space. X, Y and W survive that - they are built from the
        // rotation and translation rows - but clip Z does not, and this projection carries its
        // ENTIRE depth term in M43, which is multiplied by exactly that w. Measured: a panel 9.33m
        // away produced clip w 9.332 (correct) and clip z 0.0 (should be 0.1), so the panel drew in
        // the right place with a depth of zero at every distance and could never be occluded.
        // Restoring the column an affine view matrix is defined to have costs nothing and fixes it.
        view.M14 = 0f;
        view.M24 = 0f;
        view.M34 = 0f;
        view.M44 = 1f;

        // A camera mid-initialization can hand back an all-zero matrix, which would collapse the
        // panel to a point rather than simply not drawing it.
        if (projection.M44 == 0f && projection.M34 == 0f)
        {
            if (!loggedViewFailure)
            {
                loggedViewFailure = true;
                ServiceCollector.PluginLog.Warning("ScreenShare: game projection matrix looks uninitialized; in-world screen skipped.");
            }

            return null;
        }

        // Row-vector convention throughout (HLSL side does mul(float4(p,1), ViewProj)), matching
        // how the game builds these itself. The camera's world-space axes are the COLUMNS of the
        // view matrix's rotation block: a view matrix is the inverse of the camera's world
        // transform, and for an orthonormal rotation that inverse is its transpose.
        //
        // Negated because the projection's M34 is -1, which means view space is right-handed with
        // the camera looking down -Z. The third column is therefore the direction BEHIND the
        // camera; taking it unnegated pointed the audio panning backwards.
        var forward = -new Vector3(view.M13, view.M23, view.M33);
        forward = forward.LengthSquared() > 0f ? Vector3.Normalize(forward) : new Vector3(0f, 0f, 1f);

        return new SceneView(
            view * projection,
            projection,
            new Vector3(render->Origin.X, render->Origin.Y, render->Origin.Z),
            forward,
            render->StandardZ,
            render->NearPlane,
            render->FarPlane);
    }

    /// <summary>The scene depth buffer for the frame currently being presented. Whether it still
    /// holds this frame's depth at the point ImGui renders is the one thing about the in-world
    /// screen that can't be settled by reading code - see the depth-buffer diagnostic in
    /// SettingsWindow, which draws it to screen so it can be confirmed by eye.</summary>
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
