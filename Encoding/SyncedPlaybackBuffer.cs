using NAudio.Wave;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// The viewer's audio jitter buffer, and the source of the master clock <see cref="H264Decoder"/>
/// reconciles video against. Replaces NAudio's BufferedWaveProvider, which could not do the second
/// job at all - three measured reasons, each of which desynced audio on its own:
///
/// 1. The output device holds audio that has been handed over but not yet heard. Measured on a
///    default WaveOutEvent: 176-289ms in flight at any moment. BufferedWaveProvider.BufferedBytes
///    cannot see any of it, so a clock built on that alone is a quarter-second wrong and, worse,
///    wrong by an amount that swings ~110ms as the device cycles its buffers - nearly 3x
///    H264Decoder's 40ms sync tolerance, so video visibly hunts even when nothing else is wrong.
///    This class instead maps the device's OWN reported playback position (waveOutGetPosition,
///    measured drift-free against wall clock to a constant 16ms offset) back onto content PTS.
/// 2. On underrun BufferedWaveProvider zero-fills (ReadFully defaults to true) and says nothing.
///    A wall-clock position estimate keeps advancing through audio nobody heard, so every underrun
///    shifted real audio behind the estimate, cumulatively and uncorrected. Here a zero-filled run
///    is recorded as silence and HOLDS the clock instead.
/// 3. DiscardOnBufferOverflow discards INCOMING audio and keeps the oldest (verified: feeding
///    1000ms into a 500ms buffer leaves packet #1 at the front). For a live stream that is exactly
///    backwards - it pins playback to the oldest audio and throws away the newest. This class
///    drops the OLDEST instead, which is what bounds latency rather than growing it.
/// </summary>
public sealed class SyncedPlaybackBuffer(WaveFormat waveFormat, int maxBufferedMilliseconds) : IWaveProvider
{
    /// <summary>Only reached when nothing is asking for the clock (no video decoder attached, or it
    /// died), since <see cref="GetPtsAtPlayedBytes"/> retires segments as the play position passes
    /// them. Bounds memory in that case, at the cost of precision for positions nobody will ask
    /// about.</summary>
    private const int MaxRetainedSegments = 8192;

    private readonly Lock gate = new();
    private readonly Queue<Chunk> chunks = new();
    private readonly Queue<Segment> upcoming = new();
    private readonly double bytesPerMillisecond = waveFormat.AverageBytesPerSecond / 1000.0;
    private readonly long maxBufferedBytes =
        (long)(waveFormat.AverageBytesPerSecond / 1000.0 * maxBufferedMilliseconds);

    private Segment? current;
    private Segment? lastRecorded;
    private long handedBytes;
    private long bufferedBytes;
    private long droppedBytes;
    private long silenceBytes;
    private long? contentEndPtsMs;

    public WaveFormat WaveFormat => waveFormat;

    /// <summary>How much audio is waiting to be handed to the device - the part of end-to-end
    /// latency this class controls (the device holds its own on top of it).</summary>
    public int BufferedMilliseconds
    {
        get
        {
            lock (gate)
                return (int)(bufferedBytes / bytesPerMillisecond);
        }
    }

    /// <summary>Audio dropped to keep latency bounded - see <see cref="Add"/>. Climbing steadily
    /// means the sender is running faster than this machine's output clock, or that delivery is
    /// arriving in bursts larger than the buffer.</summary>
    public long DroppedMilliseconds
    {
        get
        {
            lock (gate)
                return (long)(droppedBytes / bytesPerMillisecond);
        }
    }

    /// <summary>Silence handed to the device because nothing had arrived in time. Climbing means
    /// the jitter buffer is too small for this connection, and is what a listener hears as gaps.</summary>
    public long UnderrunMilliseconds
    {
        get
        {
            lock (gate)
                return (long)(silenceBytes / bytesPerMillisecond);
        }
    }

    /// <summary>Queues one decoded packet, dropping the OLDEST audio if that would push the buffer
    /// past its cap.
    ///
    /// Dropping the oldest is what makes latency bounded rather than unbounded: a sender even
    /// slightly faster than this machine's output clock would otherwise grow the backlog forever,
    /// and every millisecond of backlog is a millisecond audio arrives late. It is also what keeps
    /// playback starting at the LIVE EDGE - nothing drains this buffer until the owner starts
    /// playback, and it waits for video, which can take seconds (ffmpeg spawn, hardware decoder
    /// init, then a full presentation queue). Capping here means that wait leaves the newest audio
    /// in hand rather than the audio that happened to arrive when the stream first came up.</summary>
    public void Add(long ptsMs, byte[] pcm)
    {
        lock (gate)
        {
            chunks.Enqueue(new Chunk(ptsMs, pcm));
            bufferedBytes += pcm.Length;
            TrimLocked(maxBufferedBytes);
        }
    }

    /// <summary>Called by the output device on its own thread. Always fills the whole request (the
    /// device must never be starved of bytes) but records which of those bytes carried real content
    /// and which were silence, so <see cref="GetPtsAtPlayedBytes"/> can tell them apart.</summary>
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
                chunk.Offset += take;
                bufferedBytes -= take;
                written += take;
                contentEndPtsMs = PtsAtChunkCursor(chunk);

                if (chunk.Remaining == 0)
                    chunks.Dequeue();
            }

            if (written < count)
            {
                Array.Clear(buffer, offset + written, count - written);
                RecordSegment(handedBytes + written, contentEndPtsMs ?? 0, isSilence: true);
                silenceBytes += count - written;
                written = count;
            }

            handedBytes += count;
            return written;
        }
    }

    /// <summary>Maps a device playback position (bytes the hardware reports as actually played)
    /// onto the content timestamp being heard right now. Null until real content has been played.
    ///
    /// Inside a silence run the answer deliberately does not advance: nothing was heard, so the
    /// clock holds and video holds with it, rather than both marching past audio that never
    /// played.</summary>
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

    /// <summary>Records where a run of output bytes starts and what it carries, collapsing runs
    /// that simply continue the previous one - so the queue grows with genuine discontinuities
    /// (underruns, drops, packets lost upstream) rather than with every Read call.</summary>
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

        /// <summary>How far into <see cref="Pcm"/> the next unread byte is - moved by reading and
        /// by trimming alike, which is why the PTS is always derived from it rather than stored.</summary>
        public int Offset { get; set; }

        public int Remaining => Pcm.Length - Offset;
    }

    /// <summary>A run of bytes handed to the device, and the content PTS its first byte carried.</summary>
    private readonly record struct Segment(long StartHandedByte, long PtsMs, bool IsSilence);
}
