using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Tests.Services;

public class BitrateAdaptationTests
{
    private const int Configured = 2000;

    [Fact]
    public void Next_BacksOffImmediatelyOnDrops()
    {
        BitrateDecision decision = BitrateAdaptation.Next(
            currentKbps: 2000, Configured, newDrops: 1, cleanWindows: 0, supportsLiveBitrate: false);

        Assert.True(decision.ShouldApply);
        Assert.Equal(1500, decision.TargetKbps);
    }

    /// <summary>A single clean window must not undo a backoff - probing upward that eagerly just
    /// re-congests the link and oscillates.</summary>
    [Fact]
    public void Next_HoldsSteadyUntilTheCleanWindowStreakIsLongEnough()
    {
        int current = 1500;
        int cleanWindows = 0;

        for (int window = 1; window < BitrateAdaptation.CleanWindowsBeforeProbe; window++)
        {
            BitrateDecision decision = BitrateAdaptation.Next(
                current, Configured, newDrops: 0, cleanWindows, supportsLiveBitrate: false);

            Assert.False(decision.ShouldApply);
            Assert.Equal(current, decision.TargetKbps);
            cleanWindows = decision.CleanWindows;
        }

        BitrateDecision probe = BitrateAdaptation.Next(
            current, Configured, newDrops: 0, cleanWindows, supportsLiveBitrate: false);

        Assert.True(probe.ShouldApply);
        Assert.Equal(1724, probe.TargetKbps);   // 1500 * 1.15, truncated.
        Assert.Equal(0, probe.CleanWindows);
    }

    /// <summary>Drops reset the streak, so congestion during a clean run doesn't leave the next
    /// clean window one step away from probing upward again.</summary>
    [Fact]
    public void Next_DropsResetTheCleanStreak()
    {
        BitrateDecision decision = BitrateAdaptation.Next(
            currentKbps: 1500, Configured, newDrops: 3, cleanWindows: 4, supportsLiveBitrate: false);

        Assert.Equal(0, decision.CleanWindows);
    }

    [Fact]
    public void Next_NeverProbesAboveTheConfiguredBitrate()
    {
        BitrateDecision decision = BitrateAdaptation.Next(
            currentKbps: 1900,
            Configured,
            newDrops: 0,
            cleanWindows: BitrateAdaptation.CleanWindowsBeforeProbe - 1,
            supportsLiveBitrate: true);

        Assert.Equal(Configured, decision.TargetKbps);
    }

    [Fact]
    public void Next_NeverBacksOffBelowTheFloor()
    {
        BitrateDecision decision = BitrateAdaptation.Next(
            currentKbps: BitrateAdaptation.MinBitrateKbps,
            Configured,
            newDrops: 1,
            cleanWindows: 0,
            supportsLiveBitrate: false);

        Assert.Equal(BitrateAdaptation.MinBitrateKbps, decision.TargetKbps);
        Assert.False(decision.ShouldApply);
    }

    /// <summary>The headline difference the Media Foundation encoder buys: a step too small to be
    /// worth an ffmpeg restart is still worth making when the encoder retunes in place.</summary>
    [Fact]
    public void Next_AppliesSmallStepsOnlyWhenTheEncoderRetunesInPlace()
    {
        // 350 -> 300 is the floor-clamped backoff, a 14% change: over the live threshold, under
        // the restart one.
        BitrateDecision live = BitrateAdaptation.Next(
            currentKbps: 330, Configured, newDrops: 1, cleanWindows: 0, supportsLiveBitrate: true);
        BitrateDecision restart = BitrateAdaptation.Next(
            currentKbps: 330, Configured, newDrops: 1, cleanWindows: 0, supportsLiveBitrate: false);

        Assert.Equal(BitrateAdaptation.MinBitrateKbps, live.TargetKbps);
        Assert.Equal(live.TargetKbps, restart.TargetKbps);

        Assert.True(live.ShouldApply);
        Assert.False(restart.ShouldApply);
    }

    /// <summary>A backoff always clears both thresholds, so congestion is never ignored on either
    /// encoder - the two paths differ in how finely they adapt, never in whether they react.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Next_AlwaysActsOnCongestionAboveTheFloor(bool supportsLiveBitrate)
    {
        for (int current = BitrateAdaptation.MinBitrateKbps + 100; current <= 8000; current += 100)
        {
            BitrateDecision decision = BitrateAdaptation.Next(
                current, Configured, newDrops: 1, cleanWindows: 0, supportsLiveBitrate);

            Assert.True(decision.ShouldApply, $"congestion ignored at {current}kbps");
            Assert.True(decision.TargetKbps < current);
        }
    }
}
