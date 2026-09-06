namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// What MediaSessionService needs from a video decoder, so the ffmpeg and Media Foundation
/// implementations are interchangeable.
///
/// Extracted from <see cref="H264Decoder"/>'s existing surface rather than designed fresh, for the
/// same reason <see cref="IVideoEncoder"/> was: the point is to let a second implementation slot
/// in, not to redesign a contract that already works.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>Handle to the BGRA texture the viewer samples - the popout window's image and the
    /// in-world panel both open this on the game's device. Both decoders present into one of these
    /// whatever their internal frame format is, which is what keeps everything downstream from
    /// caring which one is running.</summary>
    nint SharedHandle { get; }

    /// <summary>Which decoder is actually in use, for the viewer's status line.</summary>
    string DecoderName { get; }

    long FramesDecoded { get; }

    double AverageFps { get; }

    /// <summary>Incoming chunks discarded because decode could not keep up in real time. A climbing
    /// value means the decoder itself is the bottleneck, not the network or the encoder.</summary>
    long DroppedChunks { get; }

    /// <summary>Decoded frames evicted from the jitter buffer before ever being shown.</summary>
    long DisplayQueueDrops { get; }

    /// <summary>How far the frame being shown sits from the audio clock, smoothed. Negative means
    /// video is behind audio.</summary>
    long AvDriftMs { get; }

    Exception? LastError { get; }

    /// <summary>Fires once, when the jitter buffer has filled enough to start playback smoothly.
    /// Paired with <see cref="StartDisplay"/> so video and audio begin together rather than
    /// whichever buffer filled first.</summary>
    event Action? ReadyToDisplay;

    /// <summary>Hands over one compressed chunk with its PTS on the host's shared session clock.
    /// Never blocks - it is called from the network receive thread.</summary>
    void Feed(long ptsMs, byte[] chunk);

    void StartDisplay();
}
