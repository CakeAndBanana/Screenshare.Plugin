using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ScreenShare.Plugin.Encoding;

public sealed class SyncedPlaybackBuffer(WaveFormat waveFormat, int maxBufferedMilliseconds) : IWaveProvider
{
    private const int MaxRetainedSegments = 8192;

    private const int WidenStepMs = 60;
    private const int NarrowStepMs = 10;
    private const int NarrowIntervalMs = 5000;

    internal const int BurstHeadroomMs = 120;

    private readonly Lock gate = new();
    private readonly Queue<Chunk> chunks = new();
    private readonly Queue<Segment> upcoming = new();
    private readonly double bytesPerMillisecond = waveFormat.AverageBytesPerSecond / 1000.0;
        
    private readonly long baselineBytes =
        (long)(waveFormat.AverageBytesPerSecond / 1000.0 * maxBufferedMilliseconds);
    private long targetBytes = (long)(waveFormat.AverageBytesPerSecond / 1000.0 * maxBufferedMilliseconds);
    private long cleanBytesSinceUnderrun;

    private readonly float[] history = new float[waveFormat.SampleRate * waveFormat.Channels / 10];
    private int historyWritten;
    private int concealedFrames;

    private Segment? current;
    private Segment? lastRecorded;
    private long handedBytes;
    private long bufferedBytes;
    private long droppedBytes;
    private long silenceBytes;
    private long? contentEndPtsMs;

    public WaveFormat WaveFormat => waveFormat;

    public int TargetMilliseconds
    {
        get
        {
            lock (gate)
                return (int)(targetBytes / bytesPerMillisecond);
        }
    }

    public int BufferedMilliseconds
    {
        get
        {
            lock (gate)
                return (int)(bufferedBytes / bytesPerMillisecond);
        }
    }

    public long DroppedMilliseconds
    {
        get
        {
            lock (gate)
                return (long)(droppedBytes / bytesPerMillisecond);
        }
    }

    public long UnderrunMilliseconds
    {
        get
        {
            lock (gate)
                return (long)(silenceBytes / bytesPerMillisecond);
        }
    }

    public void Add(long ptsMs, byte[] pcm)
    {
        lock (gate)
        {
            chunks.Enqueue(new Chunk(ptsMs, pcm));
            bufferedBytes += pcm.Length;
            TrimLocked(targetBytes + (long)(bytesPerMillisecond * BurstHeadroomMs));
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            int written = 0;

            while (written < count && chunks.Count > 0)
            {
                Chunk chunk = chunks.Peek();
                int take = Math.Min(chunk.Remaining, count - written);

                RecordSegment(handedBytes + written, PtsAtChunkCursor(chunk), isSilence: false);

                Array.Copy(chunk.Pcm, chunk.Offset, buffer, offset + written, take);
                RememberForConcealmentLocked(buffer, offset + written, take);
                chunk.Offset += take;
                bufferedBytes -= take;
                written += take;
                contentEndPtsMs = PtsAtChunkCursor(chunk);

                if (chunk.Remaining == 0)
                    chunks.Dequeue();
            }

            if (written < count)
            {
                int missing = count - written;
                ConcealLocked(buffer, offset + written, missing);

                RecordSegment(handedBytes + written, contentEndPtsMs ?? 0, isSilence: true);
                silenceBytes += missing;
                written = count;
                WidenTargetLocked();
            }
            else
            {
                concealedFrames = 0;
                NarrowTargetLocked(count);
            }

            handedBytes += count;
            return written;
        }
    }

    public long? GetPtsAtPlayedBytes(long rawPlayedBytes)
    {
        lock (gate)
        {
            long playedBytes = UnwrapPlayedBytesLocked(rawPlayedBytes);

            while (upcoming.Count > 0 && upcoming.Peek().StartHandedByte <= playedBytes)
                current = upcoming.Dequeue();

            if (current is not { } segment)
                return null;

            if (segment.IsSilence || playedBytes <= segment.StartHandedByte)
                return segment.PtsMs;

            return segment.PtsMs + (long)((playedBytes - segment.StartHandedByte) / bytesPerMillisecond);
        }
    }

    private long UnwrapPlayedBytesLocked(long rawPlayedBytes)
    {
        const long Period = 1L << 32;

        long offset = handedBytes - rawPlayedBytes + (Period / 2);
        long wraps = offset >= 0
            ? offset / Period
            : -((-offset + Period - 1) / Period);

        return rawPlayedBytes + (wraps * Period);
    }

    private long PtsAtChunkCursor(Chunk chunk) => chunk.PtsMs + (long)(chunk.Offset / bytesPerMillisecond);

    private void WidenTargetLocked()
    {
        long step = (long)(bytesPerMillisecond * WidenStepMs);
        targetBytes = Math.Min(targetBytes + step, baselineBytes * 3);
        cleanBytesSinceUnderrun = 0;
    }

    private void NarrowTargetLocked(int cleanBytes)
    {
        cleanBytesSinceUnderrun += cleanBytes;
        if (cleanBytesSinceUnderrun < (long)(bytesPerMillisecond * NarrowIntervalMs))
            return;

        cleanBytesSinceUnderrun = 0;
        targetBytes = Math.Max(targetBytes - (long)(bytesPerMillisecond * NarrowStepMs), baselineBytes);
    }

    private void RememberForConcealmentLocked(byte[] buffer, int byteOffset, int byteCount)
    {
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(byteOffset, byteCount));
        if (samples.Length >= history.Length)
        {
            samples.Slice(samples.Length - history.Length).CopyTo(history);
            historyWritten = history.Length;
            return;
        }

        int keep = Math.Min(historyWritten, history.Length - samples.Length);
        Array.Copy(history, historyWritten - keep, history, 0, keep);
        samples.CopyTo(history.AsSpan(keep));
        historyWritten = keep + samples.Length;
    }

    private void ConcealLocked(byte[] buffer, int byteOffset, int byteCount)
    {
        Span<float> output = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(byteOffset, byteCount));
        int channels = Math.Max(waveFormat.Channels, 1);
        int loopFrames = Math.Min(historyWritten / channels, waveFormat.SampleRate / 50);

        if (loopFrames < 2)
        {
            output.Clear();
            concealedFrames += output.Length / channels;
            return;
        }

        int loopSamples = loopFrames * channels;
        ReadOnlySpan<float> loop = history.AsSpan(historyWritten - loopSamples, loopSamples);
        int fadeFrames = Math.Max(waveFormat.SampleRate / 12, 1);

        for (int i = 0; i + channels <= output.Length; i += channels)
        {
            int frame = concealedFrames + (i / channels);
            float decay = Math.Max(0f, 1f - (frame / (float)fadeFrames));
            int loopOffset = (frame % loopFrames) * channels;

            for (int channel = 0; channel < channels; channel++)
                output[i + channel] = loop[loopOffset + channel] * decay;
        }

        concealedFrames += output.Length / channels;
    }

    private void TrimLocked(long targetBytes)
    {
        while (bufferedBytes > targetBytes && chunks.Count > 0)
        {
            Chunk front = chunks.Peek();
            long excess = bufferedBytes - targetBytes;

            if (excess >= front.Remaining)
            {
                bufferedBytes -= front.Remaining;
                droppedBytes += front.Remaining;
                chunks.Dequeue();
            }
            else
            {
                front.Offset += (int)excess;
                bufferedBytes -= excess;
                droppedBytes += excess;
            }
        }
    }

    private void RecordSegment(long startHandedByte, long ptsMs, bool isSilence)
    {
        if (lastRecorded is { } last && last.IsSilence == isSilence)
        {
            long continued = isSilence
                ? last.PtsMs
                : last.PtsMs + (long)((startHandedByte - last.StartHandedByte) / bytesPerMillisecond);

            if (Math.Abs(continued - ptsMs) <= 1)
                return;
        }

        var segment = new Segment(startHandedByte, ptsMs, isSilence);
        lastRecorded = segment;

        if (current is null)
            current = segment;
        else
            upcoming.Enqueue(segment);

        if (upcoming.Count > MaxRetainedSegments)
            current = upcoming.Dequeue();
    }

    private sealed class Chunk(long ptsMs, byte[] pcm)
    {
        public long PtsMs { get; } = ptsMs;

        public byte[] Pcm { get; } = pcm;

        public int Offset { get; set; }

        public int Remaining => Pcm.Length - Offset;
    }

    private readonly record struct Segment(long StartHandedByte, long PtsMs, bool IsSilence);
}
