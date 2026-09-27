using ScreenShare.Plugin.Encoding;

namespace ScreenShare.Plugin.Tests.Encoding;

public class PresentationSchedulerTests
{
    private const int Fps = 60;
    private const int FrameMs = 16;
    private const int PrefillFrames = 30;

    // Holds the display thread inside its first present, so the test controls the queue depth.
    private sealed class StalledDisplay : IDisposable
    {
        private readonly ManualResetEventSlim presenting = new();
        private readonly ManualResetEventSlim release = new();

        public PresentationScheduler<int> Scheduler { get; }

        public StalledDisplay(long audioPtsMs)
        {
            Scheduler = new PresentationScheduler<int>(
                Fps,
                PrefillFrames,
                () => audioPtsMs,
                static () => 0,
                present: _ =>
                {
                    presenting.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                },
                discard: null,
                onError: static (_, _) => { },
                "test-display");
            Scheduler.ReadyToDisplay += Scheduler.StartDisplay;
        }

        public bool WaitUntilPresenting() => presenting.Wait(TimeSpan.FromSeconds(5));

        public void Dispose()
        {
            release.Set();
            Scheduler.Dispose();
            Scheduler.Join(1000);
        }
    }

    [Fact]
    public void Enqueue_AudioClockTrailingPastTheBaseQueue_KeepsFramesInsteadOfEvicting()
    {
        // Audio plays pts ~0 while video keeps arriving: ~1.3s due-span, well past the 45-frame base.
        using var display = new StalledDisplay(audioPtsMs: 0);

        long pts = 0;
        for (int i = 0; i < PrefillFrames; i++, pts += FrameMs)
            display.Scheduler.Enqueue(pts, i);
        Assert.True(display.WaitUntilPresenting());

        for (int i = 0; i < 50; i++, pts += FrameMs)
            display.Scheduler.Enqueue(pts, i);

        Assert.Equal(0, display.Scheduler.QueueDrops);
    }

    [Fact]
    public void Enqueue_BeyondTheMaximumSpan_StillEvicts()
    {
        using var display = new StalledDisplay(audioPtsMs: 0);

        long pts = 0;
        for (int i = 0; i < PrefillFrames; i++, pts += FrameMs)
            display.Scheduler.Enqueue(pts, i);
        Assert.True(display.WaitUntilPresenting());

        int max = PresentationScheduler<int>.MaxFramesFor(Fps, PrefillFrames);
        for (int i = 0; i < max + 20; i++, pts += FrameMs)
            display.Scheduler.Enqueue(pts, i);

        Assert.True(display.Scheduler.QueueDrops > 0);
    }
}
