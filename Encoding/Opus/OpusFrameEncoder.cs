using Concentus;
using Concentus.Enums;

namespace ScreenShare.Plugin.Encoding.Opus;

/// <summary>
/// Opus frame encoder that accumulates samples and encodes them into Opus packets. It raises an event when a packet is encoded.
/// </summary>
public sealed class OpusFrameEncoder
{
    private const int MaxPacketBytes = 4000;

    private readonly IOpusEncoder encoder;
    private readonly float[] accumulator;
    private int accumulatedSamples;

    public int FrameSizePerChannel { get; }

    public int Channels { get; }

    public int Bitrate
    {
        get => encoder.Bitrate;
        set => encoder.Bitrate = value;
    }

    public event Action<long, byte[]>? PacketEncoded;

    /// <summary>
    /// Contructs an OpusFrameEncoder with the specified sample rate, number of channels, frame duration in milliseconds, and bitrate.
    /// </summary>
    /// <param name="sampleRate">The sample rate of the audio.</param>
    /// <param name="channels">The number of channels.</param>
    /// <param name="frameMilliseconds">The frame duration in milliseconds.</param>
    /// <param name="bitrate">The bitrate of the encoded audio.</param>
    public OpusFrameEncoder(int sampleRate, int channels, int frameMilliseconds = 20, int bitrate = 32000)
    {
        Channels = channels;
        FrameSizePerChannel = sampleRate * frameMilliseconds / 1000;

        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = bitrate;

        accumulator = new float[FrameSizePerChannel * channels];
    }

    /// <summary>
    /// Feeds interleaved audio samples into the encoder.
    /// </summary>
    /// <param name="timestampMs">The timestamp of the audio samples.</param>
    /// <param name="interleavedSamples">The interleaved audio samples.</param>
    public void Feed(long timestampMs, ReadOnlySpan<float> interleavedSamples)
    {
        int offset = 0;
        while (offset < interleavedSamples.Length)
        {
            // Copy as many samples as we can into the accumulator.
            int need = accumulator.Length - accumulatedSamples;
            int take = Math.Min(need, interleavedSamples.Length - offset);
            interleavedSamples.Slice(offset, take).CopyTo(accumulator.AsSpan(accumulatedSamples));
            accumulatedSamples += take;
            offset += take;

            // If we have enough samples to encode a frame, encode it and raise the PacketEncoded event.
            if (accumulatedSamples == accumulator.Length)
            {
                byte[] packet = new byte[MaxPacketBytes];
                int length = encoder.Encode(accumulator, FrameSizePerChannel, packet, packet.Length);
                PacketEncoded?.Invoke(timestampMs, packet[..length]);
                accumulatedSamples = 0;
            }
        }
    }
}
