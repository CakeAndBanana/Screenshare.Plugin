namespace ScreenShare.Plugin.Encoding;

public sealed partial class SyncedPlaybackBuffer
{
    private sealed class PlaybackChunk(long ptsMs, byte[] pcm)
    {
        public long PtsMs { get; } = ptsMs;

        public byte[] Pcm { get; } = pcm;

        public int Offset { get; set; }

        public int Remaining => Pcm.Length - Offset;
    }
}
