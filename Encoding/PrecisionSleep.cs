using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// A sleep that actually sleeps for as long as it was asked to.
///
/// <see cref="Thread.Sleep(int)"/> resolves to the Windows system timer, whose granularity is a
/// process-global setting other software moves around - typically 15.6ms unless something has asked
/// for better. Measured on a normal desktop, a loop pacing 33ms frames with Thread.Sleep achieved:
///
///     mean 33.0ms   min 29.6   p50 31.0   p95 46.2   max 47.5   spread 18.0ms
///
/// The MEAN is exactly right, which is why this hides from any average-rate readout - the schedule
/// corrects itself, so 30fps still reads as 30fps. What a viewer sees is the spread: a frame held
/// 47ms is 21fps for that moment, and a stream alternating 30ms and 47ms frames reads as uneven
/// rather than as 30fps. It also explains why it comes and goes - when any other process raises the
/// system timer to 1ms, the same code paces perfectly.
///
/// A high-resolution waitable timer fixes it outright, on the same machine and the same loop:
///
///     mean 33.0ms   min 32.0   p50 33.2   p95 33.5   max 33.7   spread  1.7ms
///
/// Deliberately NOT timeBeginPeriod, which would raise the timer resolution for the whole game
/// process to fix one thread in a plugin, and not a spin-wait, which would burn most of a core.
///
/// NOT thread-safe: one waitable timer cannot serve two waiters. Each pacing loop owns its own.
/// </summary>
internal sealed class PrecisionSleep : IDisposable
{
    /// <summary>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION - Windows 10 1803 and later.</summary>
    private const uint CreateWaitableTimerHighResolution = 0x00000002;

    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        nint timer, in long dueTime, int period, nint completionRoutine, nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private readonly nint timer;

    public PrecisionSleep()
    {
        // Zero on anything older than Windows 10 1803, which is below what the game itself needs -
        // but falling back rather than throwing keeps this a pacing improvement and not a new way
        // for a stream to fail to start.
        timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    /// <summary>True when the high-resolution timer was available; false means this is a plain
    /// <see cref="Thread.Sleep(int)"/> and the jitter above still applies.</summary>
    public bool IsPrecise => timer != 0;

    public void Wait(long milliseconds)
    {
        if (milliseconds <= 0)
            return;

        if (timer == 0)
        {
            Thread.Sleep((int)Math.Min(milliseconds, int.MaxValue));
            return;
        }

        // Negative means relative rather than absolute, in 100ns units.
        long dueTime = -milliseconds * 10_000;

        if (SetWaitableTimer(timer, in dueTime, 0, 0, 0, false))
            WaitForSingleObject(timer, Infinite);
        else
            Thread.Sleep((int)Math.Min(milliseconds, int.MaxValue));
    }

    public void Dispose()
    {
        if (timer != 0)
            CloseHandle(timer);
    }
}
