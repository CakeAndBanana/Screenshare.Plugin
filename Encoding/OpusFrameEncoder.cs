using Concentus.Enums;
using Concentus.Structs;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Buffers arbitrary-length interleaved float PCM (as WASAPI delivers it) into Opus's
/// fixed-size frames and encodes each complete frame as it fills up.
/// </summary>
public sealed class OpusFrameEncoder
{
    private const int MaxPacketBytes = 4000;

    private readonly OpusEncoder encoder;
    private readonly float[] accumulator;
    private int accumulatedSamples;

    public int FrameSizePerChannel { get; }

    public int Channels { get; }

    /// <summary>Live-adjustable - Opus supports changing bitrate mid-stream without recreating
    /// the encoder, so a UI slider can just set this directly.</summary>
    public int Bitrate
    {
        get => encoder.Bitrate;
        set => encoder.Bitrate = value;
    }

    public event Action<byte[]>? PacketEncoded;

    public OpusFrameEncoder(int sampleRate, int channels, int frameMilliseconds = 20, int bitrate = 32000)
    {
        Channels = channels;
        FrameSizePerChannel = sampleRate * frameMilliseconds / 1000;

        // Deliberately using the obsolete pure-managed constructor rather than
        // OpusCodecFactory's native-accelerated path - after the CEF native-hosting saga in
        // this same plugin, Concentus's zero-native-dependency guarantee is worth more here
        // than the performance win.
        encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO)
        {
            Bitrate = bitrate,
        };

        accumulator = new float[FrameSizePerChannel * channels];
    }

    public void Feed(ReadOnlySpan<float> interleavedSamples)
    {
        int offset = 0;
        while (offset < interleavedSamples.Length)
        {
            int need = accumulator.Length - accumulatedSamples;
            int take = Math.Min(need, interleavedSamples.Length - offset);
            interleavedSamples.Slice(offset, take).CopyTo(accumulator.AsSpan(accumulatedSamples));
            accumulatedSamples += take;
            offset += take;

            if (accumulatedSamples == accumulator.Length)
            {
                byte[] packet = new byte[MaxPacketBytes];
                int length = encoder.Encode(accumulator, FrameSizePerChannel, packet, packet.Length);
                PacketEncoded?.Invoke(packet[..length]);
                accumulatedSamples = 0;
            }
        }
    }
}
