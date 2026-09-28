namespace ScreenShare.Plugin.Encoding;

public sealed partial class SyncedPlaybackBuffer
{
    private readonly record struct PlaybackSegment(long StartHandedByte, long PtsMs, bool IsSilence);
}
