using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Encoding.Interfaces;

/// <summary>
/// Interface for video encoders, providing methods and properties for encoding video frames into a specific codec format.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    int Width { get; }

    int Height { get; }

    string EncoderName { get; }

    SupportedVideoCodec Codec { get; }

    long FramesEncoded { get; }

    long FramesSkipped { get; }

    long EncodedBytes { get; }

    double AverageFps { get; }

    Exception? LastError { get; }

    event Action<long, byte[]>? ChunkEncoded;

    bool SupportsLiveBitrate { get; }

    void SetBitrate(int bitrateKbps);
}
