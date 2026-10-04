namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Synced buffer for playback, which maintains a list of playback segments with their start byte, presentation timestamp (PTS), and silence status.
/// </summary>
public sealed partial class SyncedPlaybackBuffer
{
    private readonly record struct PlaybackSegment(long StartHandedByte, long PtsMs, bool IsSilence);
}
