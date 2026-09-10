using System.Diagnostics;
using System.Threading.Channels;

namespace ScreenShare.Plugin.Encoding;

internal sealed class PresentationScheduler<TFrame> : IDisposable
{
    private const long SyncToleranceMs = 40;
    private const long MaxFrameHoldMs = 100;

    private readonly Channel<(long PtsMs, TFrame Frame)> queue;
    private readonly int capacity;
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
        int capacity,
        Func<long?> getAudioPtsMs,
        Func<int> getSyncOffsetMs,
        Action<TFrame> present,
        Action<TFrame>? discard,
        Action<string, Exception> onError,
        string threadName)
    {
        this.capacity = capacity;
        this.getAudioPtsMs = getAudioPtsMs;
        this.getSyncOffsetMs = getSyncOffsetMs;
        this.present = present;
        this.discard = discard;
        this.onError = onError;

        frameIntervalMs = Math.Max(1, 1000 / Math.Max(1, fps));

        queue = Channel.CreateBounded<(long, TFrame)>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        running = true;
        displayThread = new Thread(DisplayLoop) { IsBackground = true, Name = threadName };
        displayThread.Start();
    }

    public void StartDisplay() => displayStarted = true;

    public void Enqueue(long ptsMs, TFrame frame)
    {
        while (queue.Reader.Count >= capacity && queue.Reader.TryRead(out (long PtsMs, TFrame Frame) evicted))
        {
            Interlocked.Increment(ref queueDrops);
            discard?.Invoke(evicted.Frame);
        }

        if (!queue.Writer.TryWrite((ptsMs, frame)))
            discard?.Invoke(frame);
    }

    private void DisplayLoop()
    {
        using var pacer = new PrecisionSleep();

        try
        {
            while (running && queue.Reader.Count < capacity)
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
