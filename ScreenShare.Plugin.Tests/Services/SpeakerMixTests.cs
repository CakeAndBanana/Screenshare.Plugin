using System.Numerics;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Tests.Services;

public class SpeakerMixTests
{
    private const float Tolerance = 0.001f;

    // A listener standing back from a screen whose speakers flank it, facing it head on. +Z is
    // "forward" here, matching the convention SpeakerMix falls back to.
    private static readonly Vector3 Forward = new(0f, 0f, 1f);
    private static readonly Vector3 SpeakerLeft = new(-3f, 0f, 10f);
    private static readonly Vector3 SpeakerRight = new(3f, 0f, 10f);

    private static void AssertClose(float expected, float actual, string because)
        => Assert.True(MathF.Abs(expected - actual) < Tolerance, $"{because}: expected ~{expected}, got {actual}");

    // --- DistanceGain ---

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    [InlineData(8f)]
    public void DistanceGain_AtOrInsideFalloffStart_IsFull(float distance)
        => AssertClose(1f, SpeakerMix.DistanceGain(distance, 8f, 25f), "inside the full-volume radius");

    [Theory]
    [InlineData(25f)]
    [InlineData(40f)]
    public void DistanceGain_AtOrBeyondFalloffEnd_IsSilent(float distance)
        => AssertClose(0f, SpeakerMix.DistanceGain(distance, 8f, 25f), "beyond the silence radius");

    [Fact]
    public void DistanceGain_BetweenTheRadii_FallsOffMonotonically()
    {
        float previous = 1f;
        for (float distance = 8f; distance <= 25f; distance += 0.5f)
        {
            float gain = SpeakerMix.DistanceGain(distance, 8f, 25f);
            Assert.True(gain <= previous + Tolerance, $"gain rose at {distance}m: {previous} then {gain}");
            Assert.InRange(gain, 0f, 1f);
            previous = gain;
        }
    }

    [Theory]
    [InlineData(8f, 8f)]
    [InlineData(8f, 2f)]
    public void DistanceGain_DegenerateRange_IsAHardCutoffRatherThanNaN(float start, float end)
    {
        AssertClose(1f, SpeakerMix.DistanceGain(start, start, end), "at the cutoff");
        AssertClose(0f, SpeakerMix.DistanceGain(start + 0.1f, start, end), "past the cutoff");
    }

    // --- Compute ---

    [Fact]
    public void Compute_FacingTheScreen_KeepsTheStereoImageTheRightWayRound()
    {
        SpeakerGains gains = SpeakerMix.Compute(Vector3.Zero, Forward, SpeakerLeft, SpeakerRight, 20f, 40f);

        Assert.True(gains.LeftToLeft > gains.LeftToRight, "the left speaker should favour the left ear");
        Assert.True(gains.RightToRight > gains.RightToLeft, "the right speaker should favour the right ear");
    }

    [Fact]
    public void Compute_FacingAwayFromTheScreen_SwapsTheChannels()
    {
        SpeakerGains gains = SpeakerMix.Compute(Vector3.Zero, -Forward, SpeakerLeft, SpeakerRight, 20f, 40f);

        Assert.True(gains.LeftToRight > gains.LeftToLeft, "the left speaker is now on the listener's right");
        Assert.True(gains.RightToLeft > gains.RightToRight, "the right speaker is now on the listener's left");
    }

    [Fact]
    public void Compute_BeyondFalloff_IsFullySilent()
    {
        SpeakerGains gains = SpeakerMix.Compute(new Vector3(0f, 0f, -500f), Forward, SpeakerLeft, SpeakerRight, 8f, 25f);

        AssertClose(0f, gains.LeftToLeft, "far away");
        AssertClose(0f, gains.LeftToRight, "far away");
        AssertClose(0f, gains.RightToLeft, "far away");
        AssertClose(0f, gains.RightToRight, "far away");
        AssertClose(0f, gains.MonoGain, "far away");
    }

    [Fact]
    public void Compute_WithinFullVolume_PreservesPowerAcrossThePan()
    {
        for (float x = -20f; x <= 20f; x += 2f)
        {
            SpeakerGains gains = SpeakerMix.Compute(new Vector3(x, 0f, 0f), Forward, SpeakerLeft, SpeakerRight, 100f, 200f);

            float leftPower = (gains.LeftToLeft * gains.LeftToLeft) + (gains.LeftToRight * gains.LeftToRight);
            AssertClose(1f, leftPower, $"left speaker power at x={x}");

            float rightPower = (gains.RightToLeft * gains.RightToLeft) + (gains.RightToRight * gains.RightToRight);
            AssertClose(1f, rightPower, $"right speaker power at x={x}");
        }
    }

    [Fact]
    public void Compute_ListenerExactlyOnASpeaker_ProducesNoNaN()
    {
        SpeakerGains gains = SpeakerMix.Compute(SpeakerLeft, Forward, SpeakerLeft, SpeakerRight, 8f, 25f);

        Assert.False(float.IsNaN(gains.LeftToLeft) || float.IsNaN(gains.LeftToRight), "left channel");
        Assert.False(float.IsNaN(gains.RightToLeft) || float.IsNaN(gains.RightToRight), "right channel");
        Assert.False(float.IsNaN(gains.MonoGain), "mono");
    }

    [Fact]
    public void Compute_ZeroFacing_FallsBackInsteadOfProducingNaN()
    {
        SpeakerGains gains = SpeakerMix.Compute(Vector3.Zero, Vector3.Zero, SpeakerLeft, SpeakerRight, 20f, 40f);

        Assert.False(float.IsNaN(gains.LeftToLeft) || float.IsNaN(gains.RightToRight), "gains should be finite");
    }

    [Fact]
    public void Compute_PurelyVerticalOffset_StillCentresRatherThanFailing()
    {
        SpeakerGains gains = SpeakerMix.Compute(new Vector3(0f, 30f, 10f), Forward, SpeakerLeft, SpeakerRight, 100f, 200f);

        Assert.False(float.IsNaN(gains.LeftToLeft), "gains should be finite");
        Assert.True(gains.MonoGain > 0f, "still audible");
    }

    [Fact]
    public void MonoGain_IsDistanceOnly_AndUnaffectedByFacing()
    {
        SpeakerGains facing = SpeakerMix.Compute(Vector3.Zero, Forward, SpeakerLeft, SpeakerRight, 8f, 25f);
        SpeakerGains away = SpeakerMix.Compute(Vector3.Zero, -Forward, SpeakerLeft, SpeakerRight, 8f, 25f);

        AssertClose(facing.MonoGain, away.MonoGain, "turning around changes pan, not distance");
    }

    [Fact]
    public void Identity_IsUntouchedStereo()
    {
        SpeakerGains identity = SpeakerGains.Identity;

        Assert.Equal(1f, identity.LeftToLeft);
        Assert.Equal(0f, identity.LeftToRight);
        Assert.Equal(0f, identity.RightToLeft);
        Assert.Equal(1f, identity.RightToRight);
        Assert.Equal(1f, identity.MonoGain);
    }
}
