using System.Runtime.InteropServices;

namespace ScreenShare.Plugin.Encoding;

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

    public PrecisionSleep()
    {
        // Zero on anything older than Windows 10 1803, which is below what the game itself needs -
        // but falling back rather than throwing keeps this a pacing improvement and not a new way
        // for a stream to fail to start.
        timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

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
