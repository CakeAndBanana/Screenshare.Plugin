namespace ScreenShare.Plugin.Encoding;

public interface IVideoDecoder : IDisposable
{
    int Width { get; }

    int Height { get; }

    nint SharedHandle { get; }

    string DecoderName { get; }

    long FramesDecoded { get; }

    double AverageFps { get; }

    long DroppedChunks { get; }

    long DisplayQueueDrops { get; }

    long AvDriftMs { get; }

    Exception? LastError { get; }

    event Action? ReadyToDisplay;

    void Feed(long ptsMs, byte[] chunk);

    void StartDisplay();
}
