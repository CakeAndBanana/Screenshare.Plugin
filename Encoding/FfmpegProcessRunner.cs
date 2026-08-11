using System.Diagnostics;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Runs ffmpeg as a child process with redirected stdin/stdout. Chromium's native crashes in
/// this same plugin took the whole game down with it; ffmpeg as a separate OS process cannot -
/// worst case it exits and this wrapper surfaces that as an error, the game keeps running.
/// </summary>
public sealed class FfmpegProcessRunner : IDisposable
{
    private readonly Process process;
    private readonly Task stderrDrainTask;

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
        // ffmpeg blocks. Drain it unconditionally on a background task.
        stderrDrainTask = Task.Run(DrainStderrAsync);
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            using StreamReader reader = process.StandardError;
            char[] buffer = new char[4096];
            while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
                // Discarded - ffmpeg's stderr is progress/log noise, not needed for the local
                // validation round trip. Surface actual failures via process exit code instead.
            }
        }
        catch (Exception ex)
        {
            LastError = ex;
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
