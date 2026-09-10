using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Encoding.Interfaces;

public interface IVideoEncoder : IDisposable
{
    int Width { get; }

    int Height { get; }

    string EncoderName { get; }

    VideoCodec Codec { get; }

    long FramesEncoded { get; }

    long FramesSkipped { get; }

    long EncodedBytes { get; }

    double AverageFps { get; }

    Exception? LastError { get; }

    event Action<long, byte[]>? ChunkEncoded;

    bool SupportsLiveBitrate { get; }

    void SetBitrate(int bitrateKbps);
}
