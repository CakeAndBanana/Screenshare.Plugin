using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Networking;

/// <summary>
/// Decides, as the host's send loop reaches each queued video message, whether it still goes out.
/// When the upload can't keep up, the send queue fills long before it overflows (512 messages is
/// ~8s at 60fps), and every viewer ends up that far behind live. Instead, once the oldest message
/// has waited longer than <c>maxAgeMs</c>, video is discarded up to the next fresh resync point -
/// the same flush the relay does for a slow viewer - and each discard counts as a drop, which is
/// what makes the bitrate adaptation back off.
/// </summary>
internal sealed class StaleVideoGate(long maxAgeMs)
{
    // Framed video message: type byte, 8-byte PTS, then the Annex-B access unit.
    private const int PayloadOffset = 1 + sizeof(long);

    private bool waitingForResync;

    public SupportedVideoCodec Codec { get; set; }

    /// <summary>True when the message should be sent; false when it is discarded as stale.</summary>
    public bool Admit(long ageMs, byte[] framedMessage)
    {
        bool stale = ageMs > maxAgeMs;
        if (stale)
            waitingForResync = true;

        if (!waitingForResync)
            return true;

        // Resume only on a resync point that is itself fresh: a keyframe that waited behind the
        // backlog is still late, and sending it would put viewers straight back behind live.
        if (stale || !IsResyncPoint(framedMessage, Codec))
            return false;

        waitingForResync = false;
        return true;
    }

    // Parameter sets count too, because they may arrive as their own chunk just before the IDR;
    // this mirrors the relay's RoomMediaChannel.IsResyncPoint.
    internal static bool IsResyncPoint(byte[] framedMessage, SupportedVideoCodec codec)
    {
        if (framedMessage.Length <= PayloadOffset)
            return false;

        ReadOnlySpan<byte> data = framedMessage.AsSpan(PayloadOffset);
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                continue;

            byte header = data[i + 3];
            if (codec == SupportedVideoCodec.Hevc)
            {
                int type = (header >> 1) & 0x3F;
                if (type is >= 16 and <= 21 or 32 or 33)
                    return true;
            }
            else
            {
                int type = header & 0x1F;
                if (type is 5 or 7)
                    return true;
            }

            i += 2;
        }

        return false;
    }
}
