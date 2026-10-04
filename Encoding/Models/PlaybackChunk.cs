namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Model representing a chunk of audio data with its presentation timestamp (PTS) in milliseconds and the corresponding PCM byte array.
/// </summary>
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
