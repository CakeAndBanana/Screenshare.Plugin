using Concentus;
using Concentus.Enums;

namespace ScreenShare.Plugin.Encoding;

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

    public OpusFrameEncoder(int sampleRate, int channels, int frameMilliseconds = 20, int bitrate = 32000)
    {
        Channels = channels;
        FrameSizePerChannel = sampleRate * frameMilliseconds / 1000;

        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = bitrate;

        accumulator = new float[FrameSizePerChannel * channels];
    }

    public void Feed(long timestampMs, ReadOnlySpan<float> interleavedSamples)
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
                PacketEncoded?.Invoke(timestampMs, packet[..length]);
                accumulatedSamples = 0;
            }
        }
    }
}
