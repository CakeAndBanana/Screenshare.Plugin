using System.Diagnostics;

namespace ScreenShare.Plugin.Encoding;

public sealed class FfmpegProcessRunner : IDisposable
{
    // Bounds how much stderr text ThrowIfFailed's error message can quote - just needs enough to
    // show the actual ffmpeg error line(s), not the whole log.
    private const int MaxStderrTailChars = 4096;

    private readonly Process process;
    private readonly Task stderrDrainTask;
    private readonly System.Text.StringBuilder stderrTail = new();
    private readonly Lock stderrLock = new();

    public Stream StandardInput => process.StandardInput.BaseStream;

    public Stream StandardOutput => process.StandardOutput.BaseStream;

    public Exception? LastError { get; private set; }

    public FfmpegProcessRunner(string ffmpegExePath, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExePath,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{ffmpegExePath}'.");

        // ffmpeg writes continuously to stderr; if nobody reads it, the pipe buffer fills and
        // ffmpeg blocks. Drain it unconditionally on a background task - kept (bounded) rather
        // than discarded so ThrowIfFailed can actually explain WHY ffmpeg died, not just that it did.
        stderrDrainTask = Task.Run(DrainStderrAsync);
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            using StreamReader reader = process.StandardError;
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (stderrLock)
                {
                    stderrTail.Append(buffer, 0, read);
                    if (stderrTail.Length > MaxStderrTailChars)
                        stderrTail.Remove(0, stderrTail.Length - MaxStderrTailChars);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
    }

    public void ThrowIfFailed()
    {
        if (LastError is { } drainError)
            throw drainError;

        process.WaitForExit(2000);
        if (process.HasExited && process.ExitCode != 0)
        {
            string tail;
            lock (stderrLock)
                tail = stderrTail.ToString();
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {tail}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                    process.Kill();
            }
        }
        catch
        {
            // best-effort teardown
        }

        stderrDrainTask.Wait(TimeSpan.FromSeconds(1));
        process.Dispose();
    }
}
