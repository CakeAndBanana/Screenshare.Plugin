using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Precision sleep utility that uses a high-resolution waitable timer to achieve more accurate sleep durations than Thread.Sleep.
/// </summary>
internal sealed class PrecisionSleep : IDisposable
{
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

    /// <summary>
    /// Constructor that initializes a new instance of the PrecisionSleep class and creates a high-resolution waitable timer.
    /// </summary>
    public PrecisionSleep()
    {
        // Creates a high-resolution waitable timer. If the creation fails, the timer will be set to 0, indicating that high-resolution sleep is not available.
        timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    public bool IsPrecise => timer != 0;

    /// <summary>
    /// Waiting for the specified number of milliseconds. Will fall back to Thread.Sleep if not available.
    /// </summary>
    /// <param name="milliseconds">Milliseconds to sleep.</param>
    public void Wait(long milliseconds)
    {
        if (milliseconds <= 0)
            return;

        if (timer == 0)
        {
            Thread.Sleep((int)Math.Min(milliseconds, int.MaxValue));
            return;
        }

        long dueTime = -milliseconds * 10_000;

        // Set the waitable timer to trigger after the specified due time. If successful, wait for the timer to signal. If not, fall back to Thread.Sleep.
        if (SetWaitableTimer(timer, in dueTime, 0, 0, 0, false))
            _ = WaitForSingleObject(timer, Infinite);
        else
            Thread.Sleep((int)Math.Min(milliseconds, int.MaxValue));
    }

    /// <summary>
    /// Disposes of the PrecisionSleep instance, closing the waitable timer handle if it was created successfully.
    /// </summary>
    public void Dispose()
    {
        if (timer != 0)
            CloseHandle(timer);
    }
}
