namespace ScreenShare.Plugin.Services;

/// <summary>What one adaptation window concluded.</summary>
/// <param name="TargetKbps">The bitrate to encode at from here on.</param>
/// <param name="CleanWindows">The clean-window count to carry into the next window.</param>
/// <param name="ShouldApply">False when the change is too small to be worth making.</param>
public readonly record struct BitrateDecision(int TargetKbps, int CleanWindows, bool ShouldApply);

/// <summary>
/// The bitrate adaptation rule, kept separate from <see cref="MediaSessionService"/> so it can be
/// exercised without a socket, an encoder or a GPU - it is the part most likely to be wrong in a
/// way that only shows up as "quality dropped and never came back" after several minutes of real
/// streaming.
/// </summary>
public static class BitrateAdaptation
{
    // Down hard and immediately, up gently and only after a sustained clean spell - the classic
    // asymmetry, because re-congesting a link costs more than under-using it.
    public const double BackoffFactor = 0.75;
    public const double ProbeFactor = 1.15;
    public const int CleanWindowsBeforeProbe = 5;
    public const int MinBitrateKbps = 300;

    /// <summary>How far the target has to move before an ffmpeg encoder is rebuilt for it. The
    /// threshold exists purely so a restart hitch is never paid for a change too small to see.</summary>
    public const int RestartMinChangePercent = 10;

    /// <summary>An encoder that retunes in place pays nothing for a change, so it adapts in much
    /// finer steps - the headline difference the Media Foundation encoder buys.</summary>
    public const int LiveMinChangePercent = 2;

    /// <param name="newDrops">Send-queue drops observed since the previous window. Any at all mean
    /// the uplink could not carry the current rate.</param>
    /// <param name="supportsLiveBitrate">See <see cref="Encoding.IVideoEncoder.SupportsLiveBitrate"/>
    /// - decides only how small a change is worth making, not which direction to move.</param>
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
