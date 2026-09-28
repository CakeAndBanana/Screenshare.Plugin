using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Syncs playback buffer that manages audio chunks and provides a synchronized playback experience.
/// </summary>
/// <param name="waveFormat"></param>
/// <param name="maxBufferedMilliseconds"></param>
public sealed partial class SyncedPlaybackBuffer(WaveFormat waveFormat, int maxBufferedMilliseconds) : IWaveProvider
{
    private const int MaxRetainedSegments = 8192;

    private const int WidenStepMs = 60;
    private const int NarrowStepMs = 10;
    private const int NarrowIntervalMs = 5000;

    internal const int BurstHeadroomMs = 120;

    private readonly Lock gate = new();
    private readonly Queue<PlaybackChunk> chunks = new();
    private readonly Queue<PlaybackSegment> upcoming = new();
    private readonly double bytesPerMillisecond = waveFormat.AverageBytesPerSecond / 1000.0;
        
    private readonly long baselineBytes =
        (long)(waveFormat.AverageBytesPerSecond / 1000.0 * maxBufferedMilliseconds);
    private long targetBytes = (long)(waveFormat.AverageBytesPerSecond / 1000.0 * maxBufferedMilliseconds);
    private long cleanBytesSinceUnderrun;

    private readonly float[] history = new float[waveFormat.SampleRate * waveFormat.Channels / 10];
    private int historyWritten;
    private int concealedFrames;

    private PlaybackSegment? current;
    private PlaybackSegment? lastRecorded;
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

    /// <summary>
    /// Adds a new audio chunk to the playback buffer with the specified presentation timestamp (PTS) in milliseconds and PCM data.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp in milliseconds</param>
    /// <param name="pcm">The PCM data</param>
    public void Add(long ptsMs, byte[] pcm)
    {
        lock (gate)
        {
            chunks.Enqueue(new PlaybackChunk(ptsMs, pcm));
            bufferedBytes += pcm.Length;
            TrimLocked(targetBytes + (long)(bytesPerMillisecond * BurstHeadroomMs));
        }
    }

    /// <summary>
    /// Reads audio samples from the playback buffer into the specified buffer, starting at the given offset and reading up to the specified count of bytes. 
    /// If there are not enough samples available, it will fill the remaining space with concealed audio (silence or extrapolated audio).
    /// </summary>
    /// <param name="buffer">The buffer to read audio samples into</param>
    /// <param name="offset">The offset in the buffer to start reading</param>
    /// <param name="count">The number of bytes to read</param>
    /// <returns>The number of bytes read</returns>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            int written = 0;

            // Read available chunks and fill the buffer
            while (written < count && chunks.Count > 0)
            {
                // Get the next chunk and determine how many bytes to take from it
                PlaybackChunk chunk = chunks.Peek();
                int take = Math.Min(chunk.Remaining, count - written);

                RecordSegment(handedBytes + written, PtsAtChunkCursor(chunk), isSilence: false);

                // Copy the PCM data from the chunk to the output buffer
                Array.Copy(chunk.Pcm, chunk.Offset, buffer, offset + written, take);
                RememberForConcealmentLocked(buffer, offset + written, take);
                chunk.Offset += take;
                bufferedBytes -= take;
                written += take;
                contentEndPtsMs = PtsAtChunkCursor(chunk);

                if (chunk.Remaining == 0)
                    chunks.Dequeue();
            }

            // If we didn't fill the requested count, we need to conceal the missing audio
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

    /// <summary>
    /// Gets the presentation timestamp (PTS) in milliseconds corresponding to the specified raw played bytes.
    /// </summary>
    /// <param name="rawPlayedBytes">The raw played bytes</param>
    /// <returns>The presentation timestamp in milliseconds, or null if not found</returns>
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

    /// <summary>
    /// Unwraps the raw played bytes to account for potential wrap-around in the 32-bit representation of played bytes. 
    /// This ensures that the played bytes are correctly interpreted even if they have wrapped around due to overflow.
    /// </summary>
    /// <param name="rawPlayedBytes">The raw played bytes</param>
    /// <returns>The unwrapped played bytes</returns>
    private long UnwrapPlayedBytesLocked(long rawPlayedBytes)
    {
        const long Period = 1L << 32;

        long offset = handedBytes - rawPlayedBytes + (Period / 2);
        long wraps = offset >= 0
            ? offset / Period
            : -((-offset + Period - 1) / Period);

        return rawPlayedBytes + (wraps * Period);
    }

    /// <summary>
    /// Provides the presentation timestamp (PTS) in milliseconds at the current cursor position of the specified playback chunk.
    /// </summary>
    /// <param name="chunk">The playback chunk.</param>
    /// <returns>The presentation timestamp in milliseconds.</returns>
    private long PtsAtChunkCursor(PlaybackChunk chunk) => chunk.PtsMs + (long)(chunk.Offset / bytesPerMillisecond);

    /// <summary>
    /// Widens the target buffer size to allow for more buffered audio, up to a maximum of three times the baseline size.
    /// This is typically called when an underrun occurs, indicating that more audio data is needed to maintain smooth playback.
    /// </summary>
    private void WidenTargetLocked()
    {
        long step = (long)(bytesPerMillisecond * WidenStepMs);
        targetBytes = Math.Min(targetBytes + step, baselineBytes * 3);
        cleanBytesSinceUnderrun = 0;
    }

    /// <summary>
    /// Narrows the target buffer size to reduce latency, but not below the baseline size.
    /// </summary>
    /// <param name="cleanBytes">The number of clean bytes</param>
    private void NarrowTargetLocked(int cleanBytes)
    {
        cleanBytesSinceUnderrun += cleanBytes;
        if (cleanBytesSinceUnderrun < (long)(bytesPerMillisecond * NarrowIntervalMs))
            return;

        cleanBytesSinceUnderrun = 0;
        targetBytes = Math.Max(targetBytes - (long)(bytesPerMillisecond * NarrowStepMs), baselineBytes);
    }

    /// <summary>
    /// Remembers the most recent audio samples for potential concealment in case of underrun. 
    /// This method stores the last few samples in a history buffer, which can be used to generate smooth audio when there is a lack of new audio data.
    /// </summary>
    /// <param name="buffer">The buffer containing the audio data.</param>
    /// <param name="byteOffset">The offset within the buffer.</param>
    /// <param name="byteCount">The number of bytes to store.</param>
    private void RememberForConcealmentLocked(byte[] buffer, int byteOffset, int byteCount)
    {
        // Convert the byte buffer to a span of float samples and store the most recent samples in the history buffer
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(byteOffset, byteCount));
        if (samples.Length >= history.Length)
        {
            samples[^history.Length..].CopyTo(history);
            historyWritten = history.Length;
            return;
        }

        // If the history buffer is full, shift the existing samples to make room for the new samples
        int keep = Math.Min(historyWritten, history.Length - samples.Length);
        Array.Copy(history, historyWritten - keep, history, 0, keep);
        samples.CopyTo(history.AsSpan(keep));
        historyWritten = keep + samples.Length;
    }

    /// <summary>
    /// Conceals missing audio samples by generating audio based on the previously stored history.
    /// </summary>
    /// <param name="buffer">The buffer to store the concealed audio data.</param>
    /// <param name="byteOffset">The offset within the buffer.</param>
    /// <param name="byteCount">The number of bytes to store.</param>
    private void ConcealLocked(byte[] buffer, int byteOffset, int byteCount)
    {
        // If there is not enough history to generate concealed audio, fill the output with silence
        Span<float> output = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(byteOffset, byteCount));
        int channels = Math.Max(waveFormat.Channels, 1);
        int loopFrames = Math.Min(historyWritten / channels, waveFormat.SampleRate / 50);

        // If there are not enough frames in the history to create a loop, clear the output and update the concealed frames count
        if (loopFrames < 2)
        {
            output.Clear();
            concealedFrames += output.Length / channels;
            return;
        }

        // Create a loop from the most recent history and apply a fade-out effect to the concealed audio
        int loopSamples = loopFrames * channels;
        ReadOnlySpan<float> loop = history.AsSpan(historyWritten - loopSamples, loopSamples);
        int fadeFrames = Math.Max(waveFormat.SampleRate / 12, 1);

        // Fill the output buffer with the looped audio, applying a fade-out effect based on the number of concealed frames
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

    /// <summary>
    /// Trims the buffered audio chunks to ensure that the total buffered bytes do not exceed the specified target bytes.
    /// </summary>
    /// <param name="targetBytes">The target number of bytes to keep in the buffer.</param>
    private void TrimLocked(long targetBytes)
    {
        // Remove excess buffered audio chunks to maintain the target buffer size
        while (bufferedBytes > targetBytes && chunks.Count > 0)
        {
            // Get the front chunk and calculate the excess bytes to drop
            PlaybackChunk front = chunks.Peek();
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

    /// <summary>
    /// Records a playback segment with the specified starting byte position, presentation timestamp (PTS) in milliseconds, and whether it represents silence.
    /// </summary>
    /// <param name="startHandedByte">The starting byte position of the segment.</param>
    /// <param name="ptsMs">The presentation timestamp (PTS) in milliseconds.</param>
    /// <param name="isSilence">A value indicating whether the segment represents silence.</param>
    private void RecordSegment(long startHandedByte, long ptsMs, bool isSilence)
    {
        // If the last recorded segment has the same silence state, check if it can be continued instead of creating a new segment
        if (lastRecorded is { } last && last.IsSilence == isSilence)
        {
            long continued = isSilence
                ? last.PtsMs
                : last.PtsMs + (long)((startHandedByte - last.StartHandedByte) / bytesPerMillisecond);

            if (Math.Abs(continued - ptsMs) <= 1)
                return;
        }

        // Create a new playback segment and update the current and upcoming segments accordingly
        var segment = new PlaybackSegment(startHandedByte, ptsMs, isSilence);
        lastRecorded = segment;

        if (current is null)
            current = segment;
        else
            upcoming.Enqueue(segment);

        if (upcoming.Count > MaxRetainedSegments)
            current = upcoming.Dequeue();
    }
}
