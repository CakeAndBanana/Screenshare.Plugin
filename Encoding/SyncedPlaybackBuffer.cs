using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ScreenShare.Plugin.Encoding;

public sealed class SyncedPlaybackBuffer(WaveFormat waveFormat, int maxBufferedMilliseconds) : IWaveProvider
{
    private const int MaxRetainedSegments = 8192;

    // How the adaptive band moves: a decisive step up after an underrun, a slow creep back down.
    private const int WidenStepMs = 60;
    private const int NarrowStepMs = 10;
    private const int NarrowIntervalMs = 5000;

    private readonly Lock gate = new();
    private readonly Queue<Chunk> chunks = new();
    private readonly Queue<Segment> upcoming = new();
    private readonly double bytesPerMillisecond = waveFormat.AverageBytesPerSecond / 1000.0;

    // Adaptive depth. The cap starts at the configured budget and moves within a band around it:
    // it steps up after an underrun, because an underrun is direct evidence the current depth is
    // too shallow for this link, and creeps back down while playback stays clean. A fixed depth has
    // to be sized for the worst burst permanently - latency everyone pays all the time, for an
    // event that happens occasionally.
    private readonly long baselineBytes =
        (long)(waveFormat.AverageBytesPerSecond / 1000.0 * maxBufferedMilliseconds);
    private long targetBytes = (long)(waveFormat.AverageBytesPerSecond / 1000.0 * maxBufferedMilliseconds);
    private long cleanBytesSinceUnderrun;

    // Recently played audio, kept so an underrun can be concealed with something resembling the
    // signal rather than with digital silence. A tenth of a second is ample.
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
            TrimLocked(targetBytes);
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

                // Still recorded as silence even though it is no longer silent: the clock must hold
                // through concealed audio exactly as it did through digital silence, because none of
                // it is real content, and letting the clock advance would march video past audio
                // that was never sent.
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

    public long? GetPtsAtPlayedBytes(long playedBytes)
    {
        lock (gate)
        {
            while (upcoming.Count > 0 && upcoming.Peek().StartHandedByte <= playedBytes)
                current = upcoming.Dequeue();

            if (current is not { } segment)
                return null;

            if (segment.IsSilence || playedBytes <= segment.StartHandedByte)
                return segment.PtsMs;

            return segment.PtsMs + (long)((playedBytes - segment.StartHandedByte) / bytesPerMillisecond);
        }
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
        targetBytes = Math.Max(targetBytes - (long)(bytesPerMillisecond * NarrowStepMs), baselineBytes / 2);
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

        // Slide what is kept back and append. A ring index would avoid the copy, but this runs once
        // per device buffer over at most 100ms of audio, and a straight run is far easier to read
        // back out of than a wrapped one.
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

        // Nothing has played yet (an underrun before the first packet), so there is nothing to
        // repeat and silence is the only honest answer.
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
                // Partial drop. Advancing the cursor rather than rewriting the chunk keeps its PTS
                // correct on its own terms - PtsAtChunkCursor derives the timestamp from wherever
                // the cursor now sits, so dropped audio can never desync what follows it.
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

            // 1ms of slack absorbs the truncation in that division; anything H264Decoder reacts to
            // is 40x larger.
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
