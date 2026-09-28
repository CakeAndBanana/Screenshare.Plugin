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
        Assert.Equal(1724, probe.TargetKbps);
        Assert.Equal(0, probe.CleanWindows);
    }

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

    [Fact]
    public void Next_AppliesSmallStepsOnlyWhenTheEncoderRetunesInPlace()
    {
        BitrateDecision live = BitrateAdaptation.Next(
            currentKbps: 330, Configured, newDrops: 1, cleanWindows: 0, supportsLiveBitrate: true);
        BitrateDecision restart = BitrateAdaptation.Next(
            currentKbps: 330, Configured, newDrops: 1, cleanWindows: 0, supportsLiveBitrate: false);

        Assert.Equal(BitrateAdaptation.MinBitrateKbps, live.TargetKbps);
        Assert.Equal(live.TargetKbps, restart.TargetKbps);

        Assert.True(live.ShouldApply);
        Assert.False(restart.ShouldApply);
    }

    [Fact]
    public void Next_Congestion_SetsACeilingJustBelowTheRateThatCongested()
    {
        BitrateDecision decision = BitrateAdaptation.Next(
            currentKbps: 3000, configuredKbps: 3500, newDrops: 2, cleanWindows: 0, supportsLiveBitrate: true);

        Assert.Equal(2250, decision.TargetKbps);
        Assert.Equal(2700, decision.CeilingKbps);
        Assert.Equal(BitrateAdaptation.CeilingHoldWindows, decision.CeilingWindowsLeft);
    }

    [Fact]
    public void Next_WhileTheCeilingHolds_ProbesStopBelowIt()
    {
        int current = 2250, clean = 0, ceiling = 2700, left = BitrateAdaptation.CeilingHoldWindows;

        for (int window = 0; window < BitrateAdaptation.CeilingHoldWindows - 1; window++)
        {
            BitrateDecision decision = BitrateAdaptation.Next(
                current, 3500, newDrops: 0, clean, supportsLiveBitrate: true, ceiling, left);
            (current, clean, ceiling, left) = (decision.TargetKbps, decision.CleanWindows, decision.CeilingKbps, decision.CeilingWindowsLeft);

            Assert.True(current <= 2700, $"probed to {current}kbps while the ceiling held");
        }

        Assert.Equal(2700, current);
    }

    [Fact]
    public void Next_OnceTheCeilingExpires_ProbesUpToTheConfiguredRateAgain()
    {
        BitrateDecision decision = BitrateAdaptation.Next(
            currentKbps: 2700, configuredKbps: 3500, newDrops: 0,
            cleanWindows: BitrateAdaptation.CleanWindowsBeforeProbe - 1, supportsLiveBitrate: true,
            ceilingKbps: 2700, ceilingWindowsLeft: 1);

        Assert.Equal(0, decision.CeilingWindowsLeft);
        Assert.Equal((int)(2700 * BitrateAdaptation.ProbeFactor), decision.TargetKbps);
        Assert.True(decision.TargetKbps > 2700);
    }

    [Fact]
    public void Next_CeilingBelowTheCurrentRate_NeverLowersItOnAProbe()
    {
        BitrateDecision decision = BitrateAdaptation.Next(
            currentKbps: 2000, configuredKbps: 3500, newDrops: 0,
            cleanWindows: BitrateAdaptation.CleanWindowsBeforeProbe - 1, supportsLiveBitrate: true,
            ceilingKbps: 1500, ceilingWindowsLeft: 10);

        Assert.Equal(2000, decision.TargetKbps);
        Assert.False(decision.ShouldApply);
    }

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
