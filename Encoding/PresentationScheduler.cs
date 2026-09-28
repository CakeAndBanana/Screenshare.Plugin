using System.Diagnostics;
using System.Threading.Channels;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// A scheduler for managing the presentation of frames with precise timing.
/// </summary>
/// <typeparam name="TFrame"></typeparam>
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
    private readonly int fps;
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

    /// <summary>
    /// Constructor of the PresentationScheduler class.
    /// </summary>
    /// <param name="fps">The frames per second for the presentation scheduler.</param>
    /// <param name="prefillFrames">The number of frames to prefill the queue with.</param>
    /// <param name="getAudioPtsMs">A function to get the audio presentation time in milliseconds.</param>
    /// <param name="getSyncOffsetMs">A function to get the synchronization offset in milliseconds.</param>
    /// <param name="present">An action to present a frame.</param>
    /// <param name="discard">An action to discard a frame.</param>
    /// <param name="onError">An action to handle errors.</param>
    /// <param name="threadName">The name of the display thread.</param>
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
        // Ensure that the prefillFrames is at least 1 to avoid division by zero or negative capacity.
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

        // Pacing and frame counts go through fps directly; frameIntervalMs is rounded and only
        // good for idle polling, where 16ms instead of 16.7 at 60fps doesn't add up to anything.
        this.fps = Math.Max(1, fps);
        frameIntervalMs = Math.Max(1, 1000 / this.fps);

        // Create an unbounded channel for the frame queue with single writer and multiple readers.
        queue = Channel.CreateUnbounded<(long, TFrame)>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });

        // Start the display loop in a background thread.
        running = true;
        displayThread = new Thread(DisplayLoop) { IsBackground = true, Name = threadName };
        displayThread.Start();
    }

    /// <summary>
    /// Burst frames for the given prefill frames.
    /// </summary>
    /// <param name="prefillFrames">The number of frames to prefill the queue with.</param>
    /// <returns>The number of burst frames.</returns>
    private static int BurstFramesFor(int prefillFrames) => Math.Max(4, prefillFrames / 2);

    /// <summary>
    /// Max frames for the given fps and prefill frames.
    /// </summary>
    /// <param name="fps">The frames per second for the presentation scheduler.</param>
    /// <param name="prefillFrames">The number of frames to prefill the queue with.</param>
    /// <returns>The maximum number of frames the queue can hold.</returns>
    public static int MaxFramesFor(int fps, int prefillFrames) =>
        Math.Max(prefillFrames + BurstFramesFor(prefillFrames), Math.Max(1, fps) * MaxBufferedMs / 1000);

    /// <summary>
    /// Starts the display loop, allowing frames to be presented
    /// </summary>
    public void StartDisplay() => displayStarted = true;

    /// <summary>
    /// Enqueues a frame with the given presentation timestamp in milliseconds.
    /// </summary>
    /// <param name="ptsMs">The presentation timestamp in milliseconds.</param>
    /// <param name="frame">The frame to enqueue.</param>
    public void Enqueue(long ptsMs, TFrame frame)
    {
        // Calculate the current capacity based on the newest presentation timestamp.
        capacity = CapacityFor(ptsMs);

        while (queue.Reader.Count >= capacity && queue.Reader.TryRead(out (long PtsMs, TFrame Frame) evicted))
        {
            // If the display has started, increment the queue drops counter.
            if (displayStarted)
                Interlocked.Increment(ref queueDrops);
            discard?.Invoke(evicted.Frame);
        }

        if (!queue.Writer.TryWrite((ptsMs, frame)))
            discard?.Invoke(frame);
    }

    /// <summary>
    /// Capacity for the given newest presentation timestamp in milliseconds.
    /// </summary>
    /// <param name="newestPtsMs">The newest presentation timestamp in milliseconds.</param>
    /// <returns>The capacity for the given timestamp.</returns>
    private int CapacityFor(long newestPtsMs)
    {
        if (!displayStarted || getAudioPtsMs() is not { } audioPtsMs)
            return baseCapacity;

        long dueSpanMs = newestPtsMs - (audioPtsMs - getSyncOffsetMs());
        long needed = (dueSpanMs * fps / 1000) + burstFrames;
        return (int)Math.Clamp(needed, baseCapacity, maxCapacity);
    }

    /// <summary>
    /// Display loop that continuously presents frames based on their presentation timestamps and the audio clock.
    /// </summary>
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

            // Main display loop
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
                    pacer.Wait(frameIndex * 1000 / fps - clock.ElapsedMilliseconds);
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

                    pacer.Wait(frameIndex * 1000 / fps - clock.ElapsedMilliseconds);
                }
            }
        }
        catch (Exception ex) when (running)
        {
            onError("DisplayLoop (presentation buffer -> shared texture)", ex);
        }
    }

    /// <summary>
    /// Max hold for the given drift and sync offset in milliseconds.
    /// </summary>
    /// <param name="driftMs">The drift in milliseconds.</param>
    /// <param name="syncOffsetMs">The sync offset in milliseconds.</param>
    /// <returns>The maximum hold time in milliseconds.</returns>
    private long MaxHoldFor(long driftMs, long syncOffsetMs)
    {
        long roomMs = Math.Max(0, capacity - queue.Reader.Count) * 1000L / fps;

        long errorBudgetMs = MaxFrameHoldMs + Math.Max(0, syncOffsetMs);

        return Math.Min(Math.Min(driftMs, errorBudgetMs), roomMs);
    }

    /// <summary>
    /// Shows the given frame by invoking the present action and ensures that the discard action is called afterwards.
    /// </summary>
    /// <param name="frame">The frame to show.</param>
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

    /// <summary>
    /// Stops the presentation scheduler, preventing further frames from being presented and completing the queue.
    /// </summary>
    public void Stop()
    {
        running = false;
        queue.Writer.TryComplete();
    }

    /// <summary>
    /// Joins the display thread, blocking until it has completed or the specified timeout has elapsed.
    /// </summary>
    /// <param name="millisecondsTimeout">The timeout in milliseconds.</param>
    /// <returns>True if the thread completed within the timeout; otherwise, false.</returns>
    public bool Join(int millisecondsTimeout) => displayThread.Join(millisecondsTimeout);

    /// <summary>
    /// Disposes of the PresentationScheduler, stopping the display loop and discarding any remaining frames in the queue.
    /// </summary>
    public void Dispose()
    {
        Stop();

        while (queue.Reader.TryRead(out (long PtsMs, TFrame Frame) leftover))
            discard?.Invoke(leftover.Frame);
    }
}
