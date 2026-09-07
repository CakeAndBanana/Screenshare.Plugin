using System.Diagnostics;
using System.Threading.Channels;

namespace ScreenShare.Plugin.Encoding;

internal sealed class PresentationScheduler<TFrame> : IDisposable
{
    // How far video's PTS can drift from audio's estimated current PTS before this stops treating
    // it as "close enough" and starts actively correcting - small drift is imperceptible and not
    // worth the jitter a correction itself would introduce.
    private const long SyncToleranceMs = 40;

    // Cap on how much EXTRA time a single frame gets held when video is running ahead of audio -
    // bounds how bad a single bad PTS estimate can make one frame look, since the next frame will
    // just get corrected again anyway. Note this is only ONE of the three limits on a hold; see
    // MaxHoldFor, where the buffer's remaining room is usually the binding one.
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
        // Owned by this thread - see PrecisionSleep, which is not shareable. Every pacing wait below
        // goes through it, because plain Thread.Sleep rounds to the system timer and turns steady
        // 33ms frames into an alternating 30/47ms stutter that still averages out to 30fps.
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
                    // No audio to reconcile against - fixed-interval pacing, same as before PTS sync.
                    Show(entry.Frame);
                    frameIndex++;
                    pacer.Wait(frameIndex * frameIntervalMs - clock.ElapsedMilliseconds);
                    continue;
                }

                // The user's deliberate offset shifts the clock video is measured against, so a
                // positive value makes every frame look "early" and get held back by that much.
                long syncOffsetMs = getSyncOffsetMs();
                long currentAudioPts = rawAudioPts - syncOffsetMs;

                // Drain to the NEWEST frame that is already due, rather than showing the oldest.
                //
                // The queue is primed to full before playback starts and then stays full, because
                // producer and consumer run at the same rate - and a permanently full FIFO is
                // permanent latency: video sits the queue's whole depth behind audio, forever. An
                // earlier rule only skipped ahead once a frame was already badly stale, so it did
                // not remove that backlog, it capped it - which is why measured drift sat pinned at
                // exactly the value of that constant.
                //
                // Draining every frame whose time has already passed keeps the standing offset near
                // one frame interval instead. Frames that are NOT yet due are still left alone, so
                // this cannot run ahead of the clock, and a positive A/V offset (which shifts the
                // clock earlier) still holds frames back exactly as before.
                while (queue.Reader.TryPeek(out (long PtsMs, TFrame Frame) next)
                       && next.PtsMs <= currentAudioPts
                       && queue.Reader.TryRead(out (long PtsMs, TFrame Frame) fresher))
                {
                    discard?.Invoke(entry.Frame);
                    entry = fresher;
                }

                long driftMs = entry.PtsMs - currentAudioPts;

                // Reported against the RAW audio clock, deliberately not the offset-shifted one the
                // pacing uses. Measuring against the shifted clock makes the offset invisible to the
                // very readout meant to detect a desync: a leftover offset delays video for real
                // while the number still reads zero, because both sides moved together.
                //
                // Smoothed, because the per-frame value swings by a frame interval or more as
                // pacing corrects and an unsmoothed readout is unreadable at 30fps.
                long reportedDriftMs = entry.PtsMs - rawAudioPts;
                Interlocked.Exchange(ref avDriftMs, ((Interlocked.Read(ref avDriftMs) * 7) + reportedDriftMs) / 8);

                if (driftMs > SyncToleranceMs)
                {
                    // Sleep BEFORE presenting, not after. Writing the frame first and then sleeping
                    // doesn't delay anything - the frame is already on screen, and the sleep only
                    // postpones the NEXT one. That is why the offset barely moved the picture
                    // however far it was pushed.
                    pacer.Wait(MaxHoldFor(driftMs, syncOffsetMs));
                    Show(entry.Frame);
                    frameIndex = 0;
                    clock.Restart();
                }
                else if (driftMs < -SyncToleranceMs)
                {
                    // Video behind audio - skip the normal pacing sleep entirely this iteration.
                    Show(entry.Frame);
                    frameIndex = 0;
                    clock.Restart();
                }
                else
                {
                    Show(entry.Frame);
                    frameIndex++;

                    // A non-positive wait means already behind schedule, and Wait returns straight
                    // away - the loop catches up rather than sleeping.
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
        // Each frame of headroom absorbs one frame interval of delay before anything is evicted.
        long roomMs = Math.Max(0, capacity - queue.Reader.Count) * (long)frameIntervalMs;

        // The cap rises with the offset because the two are different things sharing one branch:
        // MaxFrameHoldMs bounds how bad a single wrong PTS estimate can look, while the offset is a
        // deliberate delay the user asked for and must not be clamped to the error budget.
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

        // Anything still queued was never shown and still owns whatever the decoder handed it.
        while (queue.Reader.TryRead(out (long PtsMs, TFrame Frame) leftover))
            discard?.Invoke(leftover.Frame);
    }
}
