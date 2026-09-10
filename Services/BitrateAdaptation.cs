namespace ScreenShare.Plugin.Services;

public readonly record struct BitrateDecision(int TargetKbps, int CleanWindows, bool ShouldApply);

public static class BitrateAdaptation
{
    public const double BackoffFactor = 0.75;
    public const double ProbeFactor = 1.15;
    public const int CleanWindowsBeforeProbe = 5;
    public const int MinBitrateKbps = 300;

    public const int RestartMinChangePercent = 10;

    public const int LiveMinChangePercent = 2;

    public static BitrateDecision Next(
        int currentKbps,
        int configuredKbps,
        long newDrops,
        int cleanWindows,
        bool supportsLiveBitrate)
    {
        int target = currentKbps;

        if (newDrops > 0)
        {
            cleanWindows = 0;
            target = Math.Max((int)(currentKbps * BackoffFactor), MinBitrateKbps);
        }
        else if (++cleanWindows >= CleanWindowsBeforeProbe)
        {
            cleanWindows = 0;
            target = Math.Min((int)(currentKbps * ProbeFactor), configuredKbps);
        }

        int threshold = supportsLiveBitrate ? LiveMinChangePercent : RestartMinChangePercent;
        int changePercent = Math.Abs(target - currentKbps) * 100 / Math.Max(currentKbps, 1);

        return new BitrateDecision(target, cleanWindows, changePercent >= threshold);
    }
}
