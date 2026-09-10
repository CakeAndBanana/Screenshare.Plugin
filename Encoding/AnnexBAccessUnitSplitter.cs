using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Cuts a raw Annex-B elementary stream into whole access units - one per encoded picture.
///
/// Everything downstream of an encoder's ChunkEncoded assumes one chunk is one complete access
/// unit carrying its own timestamp: the send queue, the relay's per-viewer queue and the
/// viewer's feed queue are all drop-oldest on that basis, and the Media Foundation decoder wraps
/// each chunk it receives in a single MF sample stamped with the chunk's PTS. Media Foundation's
/// encoder satisfies that contract for free, because an MFT emits one sample per frame. ffmpeg
/// does not - its stdout is a byte stream, and a read off that pipe begins and ends wherever the
/// pipe happened to have bytes, mid-NAL essentially always. At a typical 6000kbps/30fps that is
/// roughly 2.6 pictures per 64KB read, so every chunk boundary fell inside a picture.
///
/// ffmpeg's own decoder tolerates that (its parser resynchronises on the next start code), which
/// is why an ffmpeg sender looked fine to an ffmpeg viewer and only broke for viewers decoding
/// with Media Foundation. This puts the ffmpeg path back on the same contract as the Media
/// Foundation one so all four sender/viewer combinations behave alike.
/// </summary>
public sealed class AnnexBAccessUnitSplitter
{
    private const int InitialCapacity = 64 * 1024;

    // A picture that has not ended after this many bytes means the stream is already corrupt
    // (a real 1080p IDR is a few hundred KB). Emitting what is held beats growing forever.
    private const int MaxAccessUnitBytes = 8 * 1024 * 1024;

    private readonly bool isHevc;

    // Holds exactly the access unit under construction: index 0 is always its first byte, so a
    // completed unit is a prefix of this buffer and the cut is a copy plus a compaction.
    private byte[] buffer = new byte[InitialCapacity];
    private int length;
    private int scan;
    private bool haveVcl;

    public AnnexBAccessUnitSplitter(VideoCodec codec) => isHevc = codec == VideoCodec.Hevc;

    /// <summary>Bytes held back waiting for the rest of their access unit.</summary>
    public int Pending => length;

    public void Append(byte[] data, int offset, int count, Action<byte[]> onAccessUnit)
    {
        if (count <= 0)
            return;

        EnsureCapacity(length + count);
        Buffer.BlockCopy(data, offset, buffer, length, count);
        length += count;

        Split(onAccessUnit);

        if (length > MaxAccessUnitBytes)
        {
            onAccessUnit(buffer[..length]);
            Reset();
        }
    }

    /// <summary>
    /// The last access unit of a stream has no following start code to end it, so it is still
    /// buffered when the encoder's stdout reaches EOF. Call once at that point.
    /// </summary>
    public byte[]? Flush()
    {
        if (length == 0)
            return null;

        byte[] tail = buffer[..length];
        Reset();
        return tail;
    }

    private void Reset()
    {
        length = 0;
        scan = 0;
        haveVcl = false;
    }

    private void Split(Action<byte[]> onAccessUnit)
    {
        // A NAL's type - and, for a slice, whether it opens a new picture - live in the bytes
        // immediately after the start code: the NAL header plus the first RBSP byte.
        int needed = isHevc ? 3 : 2;

        while (true)
        {
            int start = FindStartCode(scan, out int payload);
            if (start < 0)
            {
                // A start code can straddle an Append boundary, so the final two bytes are never
                // ruled out - the search resumes on them rather than past them.
                scan = Math.Max(scan, length - 2);
                return;
            }

            if (payload + needed > length)
            {
                // The start code is here but the bytes that classify its NAL are not yet.
                scan = start;
                return;
            }

            bool vcl = IsVcl(payload);
            bool opensAccessUnit = vcl ? IsFirstSliceOfPicture(payload) : IsAccessUnitPrefix(payload);

            if (opensAccessUnit && haveVcl)
            {
                onAccessUnit(buffer[..start]);
                Buffer.BlockCopy(buffer, start, buffer, 0, length - start);
                length -= start;
                payload -= start;
                start = 0;
                haveVcl = false;
            }

            if (vcl)
                haveVcl = true;

            scan = payload;
        }
    }

    /// <summary>
    /// Finds the next three-byte start code at or after <paramref name="from"/>, returning where
    /// to CUT (which is one byte earlier for a four-byte start code, so a completed unit does not
    /// end with a stray zero) and, via <paramref name="payload"/>, where its NAL header begins.
    /// </summary>
    private int FindStartCode(int from, out int payload)
    {
        for (int i = Math.Max(from, 0); i + 2 < length; i++)
        {
            if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 1)
                continue;

            payload = i + 3;

            // Emulation prevention caps a NAL's own payload at two consecutive zero bytes, so at
            // most one zero ahead of the pattern can belong to the start code rather than to the
            // NAL before it - backtracking further would eat real payload.
            return i > 0 && buffer[i - 1] == 0 ? i - 1 : i;
        }

        payload = -1;
        return -1;
    }

    private bool IsVcl(int payload)
    {
        if (isHevc)
            return NalType(payload) <= 31;

        int type = NalType(payload);
        return type is >= 1 and <= 5;
    }

    private int NalType(int payload) =>
        isHevc ? (buffer[payload] >> 1) & 0x3F : buffer[payload] & 0x1F;

    private bool IsFirstSliceOfPicture(int payload) =>
        (buffer[payload + (isHevc ? 2 : 1)] & 0x80) != 0;

    private bool IsAccessUnitPrefix(int payload)
    {
        int type = NalType(payload);

        return isHevc
            ? type is 32 or 33 or 34 or 35 or 39
            : type is 6 or 7 or 8 or 9;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= buffer.Length)
            return;

        int capacity = buffer.Length;
        while (capacity < required)
            capacity *= 2;

        Array.Resize(ref buffer, capacity);
    }
}
