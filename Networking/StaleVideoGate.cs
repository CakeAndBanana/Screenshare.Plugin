using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Networking;

/// <summary>
/// Gates a gate that discards stale video messages and waits for a resync point before resuming transmission.
/// </summary>
/// <param name="maxAgeMs"></param>
internal sealed class StaleVideoGate(long maxAgeMs)
{
    private const int PayloadOffset = 1 + sizeof(long);

    private bool waitingForResync;

    public SupportedVideoCodec Codec { get; set; }

    /// <summary>
    /// Admits a framed video message if it is not stale, or if it is a resync point after a stale message.
    /// </summary>
    /// <param name="ageMs">The age of the message in milliseconds.</param>
    /// <param name="framedMessage">The framed video message to evaluate.</param>
    /// <returns></returns>
    public bool Admit(long ageMs, byte[] framedMessage)
        => Admit(ageMs, framedMessage, isResyncPoint: false);

    /// <summary>
    /// Admits a video message whose resync status was worked out before it was framed - for an encrypted
    /// room, where the queued bytes are ciphertext.
    /// </summary>
    public bool Admit(long ageMs, bool isResyncPoint) => Admit(ageMs, null, isResyncPoint);

    // The resync scan only runs while waiting for one, and only for a cleartext message.
    private bool Admit(long ageMs, byte[]? framedMessage, bool isResyncPoint)
    {
        bool stale = ageMs > maxAgeMs;
        if (stale)
            waitingForResync = true;

        if (!waitingForResync)
            return true;

        // If we are waiting for a resync point, only admit the message if it is a resync point.
        if (stale || !(framedMessage is null ? isResyncPoint : IsResyncPoint(framedMessage, Codec)))
            return false;

        waitingForResync = false;
        return true;
    }

    /// <summary>
    /// Is the framed message a resync point for the given codec?
    /// </summary>
    /// <param name="framedMessage">The framed video message to evaluate.</param>
    /// <param name="codec">The video codec to check against.</param>
    /// <returns>True if the message is a resync point for the given codec; otherwise, false.</returns>
    internal static bool IsResyncPoint(byte[] framedMessage, SupportedVideoCodec codec)
    {
        if (framedMessage.Length <= PayloadOffset)
            return false;

        return IsResyncChunk(framedMessage.AsSpan(PayloadOffset), codec);
    }

    /// <summary>
    /// Is the raw Annex-B chunk (no framing) a resync point for the given codec?
    /// </summary>
    internal static bool IsResyncChunk(ReadOnlySpan<byte> data, SupportedVideoCodec codec)
    {
        // Look for the start code prefix (0x000001) in the data.
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1)
                continue;

            byte header = data[i + 3];
            // Check the NAL unit type for the given codec.
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
