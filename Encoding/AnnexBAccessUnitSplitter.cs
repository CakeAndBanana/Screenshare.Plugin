using ScreenShare.Plugin.Encoding.Enums;

namespace ScreenShare.Plugin.Encoding;

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
        int needed = isHevc ? 3 : 2;

        while (true)
        {
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
