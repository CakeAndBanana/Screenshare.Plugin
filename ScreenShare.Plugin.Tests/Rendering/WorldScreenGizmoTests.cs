using System.Numerics;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;

namespace ScreenShare.Plugin.Tests.Rendering;

public class WorldScreenGizmoTests
{
    private static ScreenPlacementInfo Placement(float yaw, float pitch, float width = 6f) =>
        new(TerritoryType: 1, X: 3f, Y: -2f, Z: 11f,
            Yaw: yaw, Pitch: pitch, WidthMeters: width,
            SpeakerSpread: 1f, FalloffStart: 5f, FalloffEnd: 30f, Version: 1);

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;

    private static float Degrees(float radians) => radians * 180f / MathF.PI;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 0)]
    [InlineData(-45, 0)]
    [InlineData(0, 30)]
    [InlineData(0, -30)]
    [InlineData(120, 25)]
    [InlineData(-160, -40)]
    [InlineData(179, 55)]
    public void ToPlacement_RoundTripsYawAndPitch(float yawDegrees, float pitchDegrees)
    {
        ScreenPlacementInfo original = Placement(Radians(yawDegrees), Radians(pitchDegrees));

        ScreenPlacementInfo result = WorldScreenGizmo.ToPlacement(
            WorldScreenGizmo.ToMatrix(original), original, aspect: 9f / 16f);

        Assert.Equal(yawDegrees, Degrees(result.Yaw), 2);
        Assert.Equal(pitchDegrees, Degrees(result.Pitch), 2);
    }

    [Fact]
    public void ToPlacement_RoundTripsPositionAndWidth()
    {
        ScreenPlacementInfo original = Placement(Radians(70f), Radians(-20f), width: 12.5f);

        ScreenPlacementInfo result = WorldScreenGizmo.ToPlacement(
            WorldScreenGizmo.ToMatrix(original), original, aspect: 9f / 16f);

        Assert.Equal(original.X, result.X, 3);
        Assert.Equal(original.Y, result.Y, 3);
        Assert.Equal(original.Z, result.Z, 3);
        Assert.Equal(original.WidthMeters, result.WidthMeters, 3);
    }

    [Theory]
    [InlineData(30, 15, 40)]
    [InlineData(-100, -25, -70)]
    [InlineData(0, 0, 90)]
    public void ToPlacement_DropsRollWithoutMovingTheFacing(float yawDegrees, float pitchDegrees, float rollDegrees)
    {
        ScreenPlacementInfo original = Placement(Radians(yawDegrees), Radians(pitchDegrees));

        Matrix4x4 rolled =
            Matrix4x4.CreateScale(original.WidthMeters)
            * Matrix4x4.CreateFromYawPitchRoll(Radians(yawDegrees), Radians(pitchDegrees), Radians(rollDegrees))
            * Matrix4x4.CreateTranslation(original.X, original.Y, original.Z);

        ScreenPlacementInfo result = WorldScreenGizmo.ToPlacement(rolled, original, aspect: 9f / 16f);

        Assert.Equal(yawDegrees, Degrees(result.Yaw), 2);
        Assert.Equal(pitchDegrees, Degrees(result.Pitch), 2);
    }

    [Fact]
    public void ToPlacement_RefusesNonFiniteTransforms()
    {
        ScreenPlacementInfo original = Placement(0f, 0f);

        Matrix4x4 broken = WorldScreenGizmo.ToMatrix(original);
        broken.M41 = float.PositiveInfinity;

        Assert.Equal(original, WorldScreenGizmo.ToPlacement(broken, original, aspect: 9f / 16f));
    }
}
