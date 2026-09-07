using System.Numerics;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Tests.Services;

public class WorldScreenGeometryTests
{
    private const float Tolerance = 0.001f;

    private static ScreenPlacementInfo Placement(
        float yaw = 0f,
        float pitch = 0f,
        float width = 6f,
        float spread = 1f)
        => new(
            TerritoryType: 345,
            X: 10f,
            Y: 2f,
            Z: -30f,
            Yaw: yaw,
            Pitch: pitch,
            WidthMeters: width,
            SpeakerSpread: spread,
            FalloffStart: 8f,
            FalloffEnd: 25f,
            Version: 1);

    private static void AssertClose(float expected, float actual, string because)
        => Assert.True(MathF.Abs(expected - actual) < Tolerance, $"{because}: expected ~{expected}, got {actual}");

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1024, 768)]
    public void BuildFrame_DerivesHeightFromTheStreamAspect(int textureWidth, int textureHeight)
    {
        PanelFrame frame = WorldScreenController.BuildFrame(Placement(width: 6f), textureWidth, textureHeight);

        float expectedHalfHeight = 3f * ((float)textureHeight / textureWidth);
        AssertClose(3f, frame.RightHalf.Length(), "half width");
        AssertClose(expectedHalfHeight, frame.UpHalf.Length(), "half height follows the source aspect");
    }

    [Fact]
    public void BuildFrame_ZeroSizedTexture_FallsBackTo16By9RatherThanDividingByZero()
    {
        PanelFrame frame = WorldScreenController.BuildFrame(Placement(width: 16f), 0, 0);

        AssertClose(8f * (9f / 16f), frame.UpHalf.Length(), "fallback aspect");
        Assert.False(float.IsNaN(frame.UpHalf.Y), "must not be NaN");
    }

    [Fact]
    public void BuildFrame_ZeroYawAndPitch_GivesTheWorldAxes()
    {
        PanelFrame frame = WorldScreenController.BuildFrame(Placement(), 1920, 1080);

        AssertClose(1f, Vector3.Dot(Vector3.Normalize(frame.RightHalf), Vector3.UnitX), "right is +X");
        AssertClose(1f, Vector3.Dot(Vector3.Normalize(frame.UpHalf), Vector3.UnitY), "up is +Y");
    }

    [Fact]
    public void BuildFrame_CentreIsThePlacementPosition()
    {
        PanelFrame frame = WorldScreenController.BuildFrame(Placement(), 1920, 1080);

        Assert.Equal(new Vector3(10f, 2f, -30f), frame.Center);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.2f)]
    [InlineData(-2.5f)]
    public void BuildFrame_AxesStayPerpendicularAndUnitScaledAtAnyOrientation(float yaw)
    {
        PanelFrame frame = WorldScreenController.BuildFrame(Placement(yaw: yaw, pitch: 0.4f, width: 6f), 1920, 1080);

        AssertClose(0f, Vector3.Dot(Vector3.Normalize(frame.RightHalf), Vector3.Normalize(frame.UpHalf)), "axes stay square");
        AssertClose(3f, frame.RightHalf.Length(), "rotation must not rescale the panel");
    }

    [Fact]
    public void BuildFrame_ZeroWidth_IsClampedToSomethingDrawable()
    {
        PanelFrame frame = WorldScreenController.BuildFrame(Placement(width: 0f), 1920, 1080);

        Assert.True(frame.RightHalf.Length() > 0f, "the panel must keep some width");
    }

    // --- Speakers ---

    [Fact]
    public void SpeakerPositions_FlankTheScreenSymmetrically()
    {
        ScreenPlacementInfo placement = Placement(width: 6f, spread: 1f);
        PanelFrame frame = WorldScreenController.BuildFrame(placement, 1920, 1080);

        (Vector3 left, Vector3 right) = WorldScreenController.SpeakerPositions(placement, frame);

        Vector3 midpoint = (left + right) * 0.5f;
        AssertClose(0f, (midpoint - frame.Center).Length(), "speakers straddle the centre");

        // Half-width 3 plus 1 of spread, on each side.
        AssertClose(8f, (right - left).Length(), "separation is width plus twice the spread");
    }

    [Fact]
    public void SpeakerPositions_ZeroSpread_SitsOnThePanelEdges()
    {
        ScreenPlacementInfo placement = Placement(width: 6f, spread: 0f);
        PanelFrame frame = WorldScreenController.BuildFrame(placement, 1920, 1080);

        (Vector3 left, Vector3 right) = WorldScreenController.SpeakerPositions(placement, frame);

        AssertClose(0f, (left - (frame.Center + frame.RightHalf)).Length(), "left channel on the +X edge");
        AssertClose(0f, (right - (frame.Center - frame.RightHalf)).Length(), "right channel on the -X edge");
    }

    // --- Nudging ---

    [Theory]
    [InlineData(0f)]
    [InlineData(1.2f)]
    [InlineData(-2.5f)]
    public void Nudge_MovesAlongThePanelsOwnAxes_AtAnyRotation(float yaw)
    {
        ScreenPlacementInfo placement = Placement(yaw: yaw);
        PanelFrame frame = WorldScreenController.BuildFrame(placement, 1920, 1080);

        ScreenPlacementInfo moved = WorldScreenController.Nudge(placement, sidewaysMeters: 2f, forwardMeters: 0f);
        var delta = new Vector3(moved.X - placement.X, 0f, moved.Z - placement.Z);

        AssertClose(2f, delta.Length(), "moves by the requested distance");

        // Negated against the panel's +X for the same handedness reason as the speakers: "right"
        // from the viewer's seat is the panel's -X.
        AssertClose(-1f, Vector3.Dot(Vector3.Normalize(delta), Vector3.Normalize(frame.Right)), "'right' moves along -X");
    }

    [Fact]
    public void Nudge_ForwardMovesAlongTheFacingAxisAndLeavesHeightAlone()
    {
        ScreenPlacementInfo placement = Placement(yaw: 0.8f);
        PanelFrame frame = WorldScreenController.BuildFrame(placement, 1920, 1080);

        ScreenPlacementInfo moved = WorldScreenController.Nudge(placement, 0f, forwardMeters: 3f);
        var delta = new Vector3(moved.X - placement.X, 0f, moved.Z - placement.Z);

        AssertClose(3f, delta.Length(), "moves by the requested distance");
        AssertClose(1f, Vector3.Dot(Vector3.Normalize(delta), Vector3.Normalize(frame.Forward)), "along the facing axis");
        Assert.Equal(placement.Y, moved.Y);
    }

    [Fact]
    public void Nudge_AdvancesTheVersion()
    {
        ScreenPlacementInfo placement = Placement();

        ScreenPlacementInfo moved = WorldScreenController.Nudge(placement, 1f, 0f);

        Assert.True(moved.Version > placement.Version, "version must advance");
    }

    [Fact]
    public void SpeakerPositions_FollowTheScreensRotation()
    {
        ScreenPlacementInfo placement = Placement(yaw: MathF.PI / 2f, width: 6f, spread: 1f);
        PanelFrame frame = WorldScreenController.BuildFrame(placement, 1920, 1080);

        (Vector3 left, Vector3 right) = WorldScreenController.SpeakerPositions(placement, frame);

        Vector3 separation = Vector3.Normalize(right - left);
        AssertClose(0f, Vector3.Dot(separation, Vector3.UnitX), "a quarter turn puts the speakers off the X axis");
        AssertClose(8f, (right - left).Length(), "separation is unchanged by rotation");
    }

    [Fact]
    public void SpeakerPositions_NegativeSpread_IsTreatedAsZero()
    {
        ScreenPlacementInfo placement = Placement(width: 6f, spread: -5f);
        PanelFrame frame = WorldScreenController.BuildFrame(placement, 1920, 1080);

        (Vector3 left, Vector3 right) = WorldScreenController.SpeakerPositions(placement, frame);

        AssertClose(6f, (right - left).Length(), "speakers never cross over each other");
    }
}
