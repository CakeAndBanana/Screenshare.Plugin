namespace ScreenShare.Plugin.Networking.Enums
{
    public enum MediaMessageType : byte
    {
        Video = 0,
        Audio = 1,
        VideoMetadata = 2,
        AudioMetadata = 3,

        // Viewer to relay: the newest media PTS received. Lets the relay bound how much a slow
        // viewer has in flight; a relay that predates it reads and ignores it.
        Ack = 4,
    }
}
