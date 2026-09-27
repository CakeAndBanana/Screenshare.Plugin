using System.Diagnostics;
using System.Threading.Channels;

namespace ScreenShare.Plugin.Encoding;

internal sealed class PresentationScheduler<TFrame> : IDisposable
{
    private const long SyncToleranceMs = 40;
    private const long MaxFrameHoldMs = 100;

    // Upper bound on how far the audio clock can trail live: its buffer widens on underruns up to
    // 3x a 500ms maximum, plus burst headroom and the output device. Video must be able to wait
    // that long, or frames due soon get evicted.
    private const int MaxBufferedMs = 2000;

    private readonly Channel<(long PtsMs, TFrame Frame)> queue;
    private readonly int baseCapacity;
    private readonly int maxCapacity;
    private readonly int burstFrames;
    private readonly int prefillFrames;
    private volatile int capacity;
    private readonly int frameIntervalMs;
    private readonly Func<long?> getAudioPtsMs;
    private readonly Func<int> getSyncOffsetMs;
    private readonly Action<TFrame> present;
    private readonly Action<TFrame>? discard;
    private readonly Action<string, Exception> onError;
    private readonly Thread displayThread;

    private volatile bool running;
    private volatile bool displayStarted;
    private long avDriftMs;
    private long queueDrops;

    public event Action? ReadyToDisplay;

    public long AvDriftMs => Interlocked.Read(ref avDriftMs);

    public long QueueDrops => Interlocked.Read(ref queueDrops);

    public PresentationScheduler(
        int fps,
        int prefillFrames,
        Func<long?> getAudioPtsMs,
        Func<int> getSyncOffsetMs,
        Action<TFrame> present,
        Action<TFrame>? discard,
        Action<string, Exception> onError,
        string threadName)
    {
        // Prefill to the target depth only; the headroom above it absorbs arrival bursts
        // (relay fan-out, TCP catch-up) instead of evicting frames that were due soon.
        this.prefillFrames = prefillFrames;
        burstFrames = BurstFramesFor(prefillFrames);
        baseCapacity = prefillFrames + burstFrames;
        maxCapacity = MaxFramesFor(fps, prefillFrames);
        capacity = baseCapacity;
        this.getAudioPtsMs = getAudioPtsMs;
        this.getSyncOffsetMs = getSyncOffsetMs;
        this.present = present;
        this.discard = discard;
        this.onError = onError;

        frameIntervalMs = Math.Max(1, 1000 / Math.Max(1, fps));

        // Unbounded because the limit moves with the audio clock; Enqueue enforces it. Enqueue
        // also reads (to evict), so this is not single-reader.
        queue = Channel.CreateUnbounded<(long, TFrame)>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });

        running = true;
        displayThread = new Thread(DisplayLoop) { IsBackground = true, Name = threadName };
        displayThread.Start();
    }

    private static int BurstFramesFor(int prefillFrames) => Math.Max(4, prefillFrames / 2);

    /// <summary>The most frames the queue can ever hold, for callers that pre-size frame storage.</summary>
    public static int MaxFramesFor(int fps, int prefillFrames) =>
        Math.Max(prefillFrames + BurstFramesFor(prefillFrames), Math.Max(1, fps) * MaxBufferedMs / 1000);

    public void StartDisplay() => displayStarted = true;

    public void Enqueue(long ptsMs, TFrame frame)
    {
        capacity = CapacityFor(ptsMs);

        while (queue.Reader.Count >= capacity && queue.Reader.TryRead(out (long PtsMs, TFrame Frame) evicted))
        {
            // Before display starts, keeping only the freshest frames is intended, not a drop.
            if (displayStarted)
                Interlocked.Increment(ref queueDrops);
            discard?.Invoke(evicted.Frame);
        }

        if (!queue.Writer.TryWrite((ptsMs, frame)))
            discard?.Invoke(frame);
    }

    // Enough frames to span from the frame now due (at the audio clock) to this new one, plus
    // burst headroom. The audio buffer widens after underruns, so this grows with it.
    private int CapacityFor(long newestPtsMs)
    {
        if (!displayStarted || getAudioPtsMs() is not { } audioPtsMs)
            return baseCapacity;

        long dueSpanMs = newestPtsMs - (audioPtsMs - getSyncOffsetMs());
        long needed = (dueSpanMs / frameIntervalMs) + burstFrames;
        return (int)Math.Clamp(needed, baseCapacity, maxCapacity);
    }

    private void DisplayLoop()
    {
        using var pacer = new PrecisionSleep();

        try
        {
            while (running && queue.Reader.Count < prefillFrames)
                Thread.Sleep(5);

            if (!running)
                return;

            ReadyToDisplay?.Invoke();

            while (running && !displayStarted)
                Thread.Sleep(5);

            var clock = Stopwatch.StartNew();
            long frameIndex = 0;

            while (running)
            {
                if (!queue.Reader.TryRead(out (long PtsMs, TFrame Frame) entry))
                {
                    Thread.Sleep(frameIntervalMs);
                    continue;
                }

                long? audioPtsMs = getAudioPtsMs();
                if (audioPtsMs is not { } rawAudioPts)
                {
                    Show(entry.Frame);
                    frameIndex++;
                    pacer.Wait(frameIndex * frameIntervalMs - clock.ElapsedMilliseconds);
                    continue;
                }

                long syncOffsetMs = getSyncOffsetMs();
                long currentAudioPts = rawAudioPts - syncOffsetMs;

                while (queue.Reader.TryPeek(out (long PtsMs, TFrame Frame) next)
                       && next.PtsMs <= currentAudioPts
                       && queue.Reader.TryRead(out (long PtsMs, TFrame Frame) fresher))
                {
                    discard?.Invoke(entry.Frame);
                    entry = fresher;
                }

                long driftMs = entry.PtsMs - currentAudioPts;

                long reportedDriftMs = entry.PtsMs - rawAudioPts;
                Interlocked.Exchange(ref avDriftMs, ((Interlocked.Read(ref avDriftMs) * 7) + reportedDriftMs) / 8);

                if (driftMs > SyncToleranceMs)
                {
                    pacer.Wait(MaxHoldFor(driftMs, syncOffsetMs));
                    Show(entry.Frame);
                    frameIndex = 0;
                    clock.Restart();
                }
                else if (driftMs < -SyncToleranceMs)
                {
                    Show(entry.Frame);
                    frameIndex = 0;
                    clock.Restart();
                }
                else
                {
                    Show(entry.Frame);
                    frameIndex++;

                    pacer.Wait(frameIndex * frameIntervalMs - clock.ElapsedMilliseconds);
                }
            }
        }
        catch (Exception ex) when (running)
        {
            onError("DisplayLoop (presentation buffer -> shared texture)", ex);
        }
    }

    private long MaxHoldFor(long driftMs, long syncOffsetMs)
    {
        long roomMs = Math.Max(0, capacity - queue.Reader.Count) * (long)frameIntervalMs;

        long errorBudgetMs = MaxFrameHoldMs + Math.Max(0, syncOffsetMs);

        return Math.Min(Math.Min(driftMs, errorBudgetMs), roomMs);
    }

    private void Show(TFrame frame)
    {
        try
        {
            present(frame);
        }
        finally
        {
            discard?.Invoke(frame);
        }
    }

    public void Stop()
    {
        running = false;
        queue.Writer.TryComplete();
    }

    public bool Join(int millisecondsTimeout) => displayThread.Join(millisecondsTimeout);

    public void Dispose()
    {
        Stop();

        while (queue.Reader.TryRead(out (long PtsMs, TFrame Frame) leftover))
            discard?.Invoke(leftover.Frame);
    }
}
