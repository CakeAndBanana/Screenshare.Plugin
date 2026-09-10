using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Rendering;

public enum GizmoOperation
{
    Move,
    Turn,
    Tilt,
    Resize,
}

public static class WorldScreenGizmo
{
    public readonly record struct GizmoResult(ScreenPlacementInfo Placement, bool IsDragging, bool Finished);

    private static bool wasDragging;

    private static bool contextShared;

    private static void EnsureContextShared()
    {
        if (contextShared)
            return;

        contextShared = true;

        try
        {
            ImGuizmo.SetImGuiContext(ImGui.GetCurrentContext());
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Warning(
                ex, "ScreenShare could not hand ImGuizmo the ImGui context; the placement handles may not respond.");
        }
    }

    public static GizmoResult Draw(
        SceneView scene, ScreenPlacementInfo placement, float aspect, GizmoOperation operation, Vector2 displaySize)
    {
        EnsureContextShared();

        Matrix4x4 world = ToMatrix(placement);

        ImGuizmo.BeginFrame();
        ImGuizmo.Enable(true);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetDrawlist(ImGui.GetBackgroundDrawList());
        ImGuizmo.SetRect(0f, 0f, displaySize.X, displaySize.Y);

        Matrix4x4 view = scene.View;
        Matrix4x4 projection = GizmoProjection(scene);

        unsafe
        {
            ImGuizmo.Manipulate(
                (float*)&view,
                (float*)&projection,
                ToImGuizmoOperation(operation),
                ToImGuizmoMode(operation),
                (float*)&world,
                null,
                null,
                null,
                null);
        }

        bool dragging = ImGuizmo.IsUsing();

        if (dragging || ImGuizmo.IsOver())
            ImGui.GetIO().WantCaptureMouse = true;

        bool finished = wasDragging && !dragging;
        wasDragging = dragging;

        ScreenPlacementInfo edited = dragging || finished
            ? ToPlacement(world, placement, aspect)
            : placement;

        return new GizmoResult(edited, dragging, finished);
    }

    private static Matrix4x4 GizmoProjection(SceneView scene)
    {
        float near = scene.NearPlane > 0f ? scene.NearPlane : 0.1f;
        float far = scene.FarPlane > near ? scene.FarPlane : near + 1000f;

        return new Matrix4x4(
            scene.Projection.M11, 0f, 0f, 0f,
            0f, scene.Projection.M22, 0f, 0f,
            0f, 0f, far / (near - far), -1f,
            0f, 0f, near * far / (near - far), 0f);
    }

    public static void Reset() => wasDragging = false;

    private static ImGuizmoOperation ToImGuizmoOperation(GizmoOperation operation) => operation switch
    {
        GizmoOperation.Turn => ImGuizmoOperation.RotateY,
        GizmoOperation.Tilt => ImGuizmoOperation.RotateX,
        GizmoOperation.Resize => ImGuizmoOperation.Scaleu,
        _ => ImGuizmoOperation.Translate,
    };

    private static ImGuizmoMode ToImGuizmoMode(GizmoOperation operation) =>
        operation == GizmoOperation.Tilt ? ImGuizmoMode.Local : ImGuizmoMode.World;

    internal static Matrix4x4 ToMatrix(ScreenPlacementInfo placement)
    {
        float width = MathF.Max(placement.WidthMeters, MinWidthMeters);

        return Matrix4x4.CreateScale(width)
             * Matrix4x4.CreateFromYawPitchRoll(placement.Yaw, placement.Pitch, 0f)
             * Matrix4x4.CreateTranslation(placement.X, placement.Y, placement.Z);
    }

    internal static ScreenPlacementInfo ToPlacement(Matrix4x4 world, ScreenPlacementInfo original, float aspect)
    {
        if (!Matrix4x4.Decompose(world, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            return original;

        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        if (forward.LengthSquared() > 0f)
            forward = Vector3.Normalize(forward);

        float yaw = MathF.Atan2(forward.X, forward.Z);
        float pitch = -MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));

        float width = Math.Clamp((scale.X + scale.Y + scale.Z) / 3f, MinWidthMeters, MaxWidthMeters);

        if (!IsFinite(translation) || !float.IsFinite(yaw) || !float.IsFinite(pitch) || !float.IsFinite(width))
            return original;

        return original with
        {
            X = translation.X,
            Y = translation.Y,
            Z = translation.Z,
            Yaw = yaw,
            Pitch = pitch,
            WidthMeters = width,
        };
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private const float MinWidthMeters = 0.1f;

    private const float MaxWidthMeters = 200f;
}
