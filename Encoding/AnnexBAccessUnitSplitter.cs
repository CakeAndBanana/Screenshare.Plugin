using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Access unit splitter for Annex B formatted video streams (H.264 and HEVC).
/// </summary>
/// <param name="codec">The video codec of the stream. Must be SupportedVideoCodecs</param>
public sealed class AnnexBAccessUnitSplitter(SupportedVideoCodec codec)
{
    private const int InitialCapacity = 64 * 1024;

    private const int MaxAccessUnitBytes = 8 * 1024 * 1024;

    private readonly bool isHevc = codec == SupportedVideoCodec.Hevc;

    private byte[] buffer = new byte[InitialCapacity];
    private int length;
    private int scan;
    private bool haveVcl;

    public int Pending => length;

    /// <summary>
    /// Append data to the splitter. 
    /// </summary>
    /// <param name="data">The data to append.</param>
    /// <param name="offset">The offset within the data array.</param>
    /// <param name="count">The number of bytes to append.</param>
    /// <param name="onAccessUnit">The action to invoke when an access unit is found.</param>
    public void Append(byte[] data, int offset, int count, Action<byte[]> onAccessUnit)
    {
        if (count <= 0)
            return;

        // Ensure the buffer has enough capacity to hold the new data.
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
    /// Flushing the remaining data in the buffer and returning it as a byte array. 
    /// </summary>
    /// <returns>Flushed data</returns>
    public byte[]? Flush()
    {
        if (length == 0)
            return null;

        byte[] tail = buffer[..length];
        Reset();
        return tail;
    }

    /// <summary>
    /// Resets the splitter state.
    /// </summary>
    private void Reset()
    {
        length = 0;
        scan = 0;
        haveVcl = false;
    }

    /// <summary>
    /// Splits the buffer into access units and invokes the provided action for each access unit found.
    /// </summary>
    /// <param name="onAccessUnit">The action to invoke when an access unit is found.</param>
    private void Split(Action<byte[]> onAccessUnit)
    {
        int needed = isHevc ? 3 : 2;

        while (true)
        {
            // Find the start code in the buffer starting from the current scan position.
            int start = FindStartCode(scan, out int payload);
            if (start < 0)
            {
                scan = Math.Max(scan, length - 2);
                return;
            }

            if (payload + needed > length)
            {
                scan = start;
                return;
            }

            // Determine if the current NAL unit is a VCL (Video Coding Layer) unit and whether it opens a new access unit.
            bool vcl = IsVcl(payload);
            bool opensAccessUnit = vcl ? IsFirstSliceOfPicture(payload) : IsAccessUnitPrefix(payload);

            // If the current NAL unit opens a new access unit and we have already seen a VCL unit, invoke the action with the current access unit and adjust the buffer accordingly.
            if (opensAccessUnit && haveVcl)
            {
                onAccessUnit(buffer[..start]);
                Buffer.BlockCopy(buffer, start, buffer, 0, length - start);
                length -= start;
                payload -= start;
                haveVcl = false;
            }

            if (vcl)
                haveVcl = true;

            scan = payload;
        }
    }

    /// <summary>
    /// Finds the start code in the buffer.
    /// </summary>
    /// <param name="from">The index to start searching from.</param>
    /// <param name="payload">The index of the payload following the start code.</param>
    /// <returns>The index of the start code, or -1 if not found.</returns>
    private int FindStartCode(int from, out int payload)
    {
        for (int i = Math.Max(from, 0); i + 2 < length; i++)
        {
            if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 1)
                continue;

            payload = i + 3;

            return i > 0 && buffer[i - 1] == 0 ? i - 1 : i;
        }

        payload = -1;
        return -1;
    }

    /// <summary>
    /// Is the NAL unit a VCL (Video Coding Layer) unit.
    /// </summary>
    /// <param name="payload">The index of the payload following the start code.</param>
    /// <returns>True if the NAL unit is a VCL unit; otherwise, false.</returns>
    private bool IsVcl(int payload)
    {
        if (isHevc)
            return NalType(payload) <= 31;

        int type = NalType(payload);
        return type is >= 1 and <= 5;
    }

    /// <summary>
    /// NalType returns the NAL unit type for the given payload index.
    /// </summary>
    /// <param name="payload">The index of the payload following the start code.</param>
    /// <returns>The NAL unit type.</returns>
    private int NalType(int payload) =>
        isHevc ? (buffer[payload] >> 1) & 0x3F : buffer[payload] & 0x1F;

    /// <summary>
    /// First slice of picture is determined by checking the first bit of the second byte of the payload for HEVC or the first byte for H.264.
    /// </summary>
    /// <param name="payload">The index of the payload following the start code.</param>
    /// <returns>True if the NAL unit is the first slice of a picture; otherwise, false.</returns>
    private bool IsFirstSliceOfPicture(int payload) =>
        (buffer[payload + (isHevc ? 2 : 1)] & 0x80) != 0;

    /// <summary>
    /// Is the NAL unit a prefix NAL unit that indicates the start of a new access unit. For HEVC, these are types 32-35 and 39. For H.264, these are types 6-9.
    /// </summary>
    /// <param name="payload">The index of the payload following the start code.</param>
    /// <returns>True if the NAL unit is a prefix NAL unit; otherwise, false.</returns>
    private bool IsAccessUnitPrefix(int payload)
    {
        int type = NalType(payload);

        return isHevc
            ? type is 32 or 33 or 34 or 35 or 39
            : type is 6 or 7 or 8 or 9;
    }

    /// <summary>
    /// Ensure the buffer has enough capacity to hold the required number of bytes.
    /// </summary>
    /// <param name="required">The required capacity.</param>
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
