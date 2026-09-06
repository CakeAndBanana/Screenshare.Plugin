using System.Diagnostics;
using System.Threading.Channels;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Holds decoded frames briefly and shows them on a clock derived from audio, rather than the
/// instant each one finished decoding.
///
/// Extracted from H264Decoder unchanged so a second decoder can reuse it instead of growing a
/// second copy. This is the subtlest code in the viewer - the drain-to-due rule, the hold-before-
/// present ordering, and which clock the drift readout is measured against were each the fix for a
/// specific reported symptom - and two copies of it would drift apart in exactly the ways that took
/// the longest to find the first time.
///
/// Generic over the frame because the two decoders hold different things: the ffmpeg path buffers
/// raw BGRA in system memory, while the Media Foundation path buffers NV12 textures that never
/// leave the GPU. Everything about WHEN to show a frame is identical; only presenting it differs,
/// so that is the delegate.
/// </summary>
/// <typeparam name="TFrame">Whatever the decoder buffers. Handed back to <c>present</c> when its
/// time comes, or to <c>discard</c> if it is evicted unseen.</typeparam>
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

    /// <summary>Fires once, when the buffer has filled enough to start playback smoothly. Not the
    /// same moment as construction (which happens as soon as the stream's metadata arrives, well
    /// before anything has decoded) or as the first decoded frame (too early - there is no jitter
    /// margin yet). Actual display waits for <see cref="StartDisplay"/> on top of this, so video
    /// and audio can begin together rather than whichever buffer filled first.</summary>
    public event Action? ReadyToDisplay;

    /// <summary>How far the frame being shown sits from the audio clock, in milliseconds, smoothed
    /// over recent frames. Negative means video is behind audio, positive means ahead.
    ///
    /// Exposed because "the sync feels wrong" and "the whole stream feels laggy" are different
    /// problems with the same description, and only a number separates them: a drift near zero with
    /// everything still feeling late is end-to-end latency, not synchronisation, and no A/V offset
    /// will help.</summary>
    public long AvDriftMs => Interlocked.Read(ref avDriftMs);

    /// <summary>How many decoded frames were evicted before ever being shown, because the display
    /// drained the buffer more slowly than frames arrived.
    ///
    /// Should stay near zero. When it does not, compare it against the decoder's own frame rate
    /// before concluding anything: a decoder holding its target rate while this climbs means the
    /// DISPLAY is the slow side, not decode - which is exactly the case
    /// <see cref="MaxHoldFor"/> exists to prevent, and the one an earlier version of this readout
    /// misattributed to decode.</summary>
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

    /// <summary>Begins draining onto the display at the paced rate - held off until called so
    /// playback can start in lockstep with audio. Idempotent, so audio joining a session that is
    /// already playing is harmless.</summary>
    public void StartDisplay() => displayStarted = true;

    /// <summary>
    /// Queues a decoded frame. Never blocks.
    ///
    /// Evicts explicitly rather than leaving it to the channel's own drop-oldest: the channel drops
    /// silently, and a dropped frame here may own a GPU texture or a pooled buffer that has to be
    /// handed back. Reading the victim out first is the only way to get it.
    /// </summary>
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

    /// <summary>
    /// Drains the buffer onto the display, reconciling against audio's own estimated playback
    /// position (audio is the master clock - hardware-paced and drift-free, unlike anything
    /// software here) rather than just pacing at a fixed interval:
    ///
    /// - Within <see cref="SyncToleranceMs"/> of audio: display normally, paced against a Stopwatch
    ///   schedule (target = frameIndex * frameIntervalMs) so a slow iteration's real work can't
    ///   compound into drift over a session - a plain Thread.Sleep(frameIntervalMs) each iteration
    ///   does not do this correctly.
    /// - Video running ahead of audio: hold this one frame a bit longer so audio can catch up, then
    ///   resume normal pacing from a fresh schedule baseline. How long is <see cref="MaxHoldFor"/>,
    ///   and the buffer's remaining room is usually what decides it.
    /// - Video running behind audio: skip the normal pacing sleep for this iteration entirely,
    ///   catching up faster than one frame interval at a time would.
    ///
    /// With no audio (getAudioPtsMs returns null - never wired, or not playing yet) this falls back
    /// to fixed-interval pacing; there is nothing to reconcile against. On an underrun it holds the
    /// last frame and waits a tick rather than blocking or busy-spinning.
    /// </summary>
    private void DisplayLoop()
    {
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
                    long noAudioSleepMs = frameIndex * frameIntervalMs - clock.ElapsedMilliseconds;
                    if (noAudioSleepMs > 0)
                        Thread.Sleep((int)noAudioSleepMs);
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
                    long holdMs = MaxHoldFor(driftMs, syncOffsetMs);
                    if (holdMs > 0)
                        Thread.Sleep((int)holdMs);

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
                    long sleepMs = frameIndex * frameIntervalMs - clock.ElapsedMilliseconds;
                    if (sleepMs > 0)
                        Thread.Sleep((int)sleepMs);
                    // else already behind schedule - loop again immediately rather than sleeping.
                }
            }
        }
        catch (Exception ex) when (running)
        {
            onError("DisplayLoop (presentation buffer -> shared texture)", ex);
        }
    }

    /// <summary>
    /// How long a frame that is running ahead of audio may actually be held.
    ///
    /// Holding buys latency, and that latency has to be absorbed somewhere: frames keep arriving at
    /// the stream's rate while the display waits, and the buffer is the only place they can go. Once
    /// it is full, every further millisecond of holding costs a frame - which is a far worse artifact
    /// than the drift the hold was correcting.
    ///
    /// That is not hypothetical. Measured over a ~2.5 hour session: drift sat at +181ms, so every
    /// frame took this branch and slept the old flat 100ms cap, pinning the display near 10fps while
    /// decode held its full 30fps. 90,375 of 271,851 frames were evicted before ever being shown -
    /// a third of the stream - and the readout blamed decode for "bursting ahead" when decode was
    /// running at exactly its target rate and the display was the thing falling behind.
    ///
    /// So the buffer's remaining room is the real limit, and when there is none the answer is zero:
    /// show the frame now, let the drift stand, and let the drift readout report it. A visible 180ms
    /// offset is recoverable - the A/V slider exists for it - whereas a third of the frames are not.
    /// </summary>
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

    /// <summary>Presents a frame and then releases it, so a decoder holding pooled GPU textures
    /// gets every slot back regardless of which pacing branch showed it.</summary>
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

    /// <summary>Waits for the display thread to stop. The result gates whether the caller may
    /// dispose the GPU resources <c>present</c> touches - see H264Decoder.Dispose.</summary>
    public bool Join(int millisecondsTimeout) => displayThread.Join(millisecondsTimeout);

    public void Dispose()
    {
        Stop();

        // Anything still queued was never shown and still owns whatever the decoder handed it.
        while (queue.Reader.TryRead(out (long PtsMs, TFrame Frame) leftover))
            discard?.Invoke(leftover.Frame);
    }
}
