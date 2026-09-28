namespace ScreenShare.Plugin.Services;

public readonly record struct BitrateDecision(
    int TargetKbps, int CleanWindows, bool ShouldApply, int CeilingKbps = 0, int CeilingWindowsLeft = 0);

public static class BitrateAdaptation
{
    public const double BackoffFactor = 0.75;
    public const double ProbeFactor = 1.15;
    public const int CleanWindowsBeforeProbe = 5;
    public const int MinBitrateKbps = 300;

    // After congestion, probing stays below this share of the bitrate that congested, for this
    // many windows (60s at the 3s adapt interval). Without it the probe climbs straight back into
    // the same limit: on a capped upload that measured as a flush-and-recover every ~40s.
    public const double CeilingFactor = 0.9;
    public const int CeilingHoldWindows = 20;

    public const int RestartMinChangePercent = 10;

    public const int LiveMinChangePercent = 2;

    public static BitrateDecision Next(
        int currentKbps,
        int configuredKbps,
        long newDrops,
        int cleanWindows,
        bool supportsLiveBitrate,
        int ceilingKbps = 0,
        int ceilingWindowsLeft = 0)
    {
        int target = currentKbps;

        if (newDrops > 0)
        {
            cleanWindows = 0;
            target = Math.Max((int)(currentKbps * BackoffFactor), MinBitrateKbps);
            ceilingKbps = Math.Max((int)(currentKbps * CeilingFactor), MinBitrateKbps);
            ceilingWindowsLeft = CeilingHoldWindows;
        }
        else
        {
            if (ceilingWindowsLeft > 0)
                ceilingWindowsLeft--;

            if (++cleanWindows >= CleanWindowsBeforeProbe)
            {
                cleanWindows = 0;
                target = Math.Min((int)(currentKbps * ProbeFactor), configuredKbps);

                // Held under the ceiling, but never pushed below where it already is.
                if (ceilingWindowsLeft > 0)
                    target = Math.Min(target, Math.Max(ceilingKbps, currentKbps));
            }
        }

        int threshold = supportsLiveBitrate ? LiveMinChangePercent : RestartMinChangePercent;
        int changePercent = Math.Abs(target - currentKbps) * 100 / Math.Max(currentKbps, 1);

        return new BitrateDecision(target, cleanWindows, changePercent >= threshold, ceilingKbps, ceilingWindowsLeft);
    }
}
