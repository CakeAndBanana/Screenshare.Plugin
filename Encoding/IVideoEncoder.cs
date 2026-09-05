namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// What MediaSessionService needs from a video encoder, so the ffmpeg and Media Foundation
/// implementations are interchangeable.
///
/// Extracted from <see cref="H264Encoder"/>'s existing surface rather than designed fresh - the
/// point is to let a second implementation slot in, not to redesign the contract that already
/// works.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Encode resolution, which is not the capture resolution - the source is scaled down
    /// to the configured target height first.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>Human-readable name of the encoder actually in use, for the host UI. Media
    /// Foundation reports the MFT's own name ("NVIDIA H.264 Encoder MFT"), which says more than the
    /// GPU vendor the ffmpeg path had to infer.</summary>
    string EncoderName { get; }

    VideoCodec Codec { get; }

    long FramesEncoded { get; }

    /// <summary>Frames the capture side had nothing new for, or that could not be submitted in
    /// time. A steadily climbing count means capture cannot keep up with the target frame rate.</summary>
    long FramesSkipped { get; }

    long EncodedBytes { get; }

    double AverageFps { get; }

    /// <summary>Set when the encoder dies mid-stream, which happens on a background thread and so
    /// cannot surface as an exception to whoever started it.</summary>
    Exception? LastError { get; }

    /// <summary>One encoded access unit, timestamped on the shared session clock at the moment the
    /// frame was CAPTURED - not when the encoder finished with it. Video and audio PTS are only
    /// comparable because they share that clock and that convention.</summary>
    event Action<long, byte[]>? ChunkEncoded;

    /// <summary>Whether <see cref="SetBitrate"/> works on this encoder without a restart.
    ///
    /// False for ffmpeg, which takes bitrate as a spawn argument and must be rebuilt - the reason
    /// bitrate adaptation there costs a visible hitch, and the reason MediaSessionService still
    /// keeps its encoder-rebuild path.</summary>
    bool SupportsLiveBitrate { get; }

    /// <summary>Retunes the running encoder. No-ops where <see cref="SupportsLiveBitrate"/> is
    /// false, so callers do not have to branch before asking.</summary>
    void SetBitrate(int bitrateKbps);
}
