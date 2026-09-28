using System.Diagnostics;

namespace ScreenShare.Plugin.Encoding;

/// <summary>
/// Ffmpeg process runner that manages the lifecycle of an ffmpeg process.
/// </summary>
public sealed class FfmpegProcessRunner : IDisposable
{
    private const int MaxStderrTailChars = 4096;

    private readonly Process process;
    private readonly Task stderrDrainTask;
    private readonly System.Text.StringBuilder stderrTail = new();
    private readonly Lock stderrLock = new();

    public Stream StandardInput => process.StandardInput.BaseStream;

    public Stream StandardOutput => process.StandardOutput.BaseStream;

    public Exception? LastError { get; private set; }

    /// <summary>
    /// FfmpegProcessRunner constructor that starts an ffmpeg process with the specified executable path and arguments.
    /// </summary>
    /// <param name="ffmpegExePath">The path to the ffmpeg executable.</param>
    /// <param name="arguments">The arguments for the ffmpeg process.</param>
    /// <exception cref="InvalidOperationException">Thrown when the ffmpeg process fails to start</exception>
    public FfmpegProcessRunner(string ffmpegExePath, string arguments)
    {
        // Configure the process start info to redirect standard input, output, and error streams.
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

        // Starts the process and throws an exception if it fails to start.
        process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{ffmpegExePath}'.");

        stderrDrainTask = Task.Run(DrainStderrAsync);
    }

    /// <summary>
    /// Drains the standard error stream of the ffmpeg process asynchronously, capturing the last few characters for error reporting.
    /// </summary>
    /// <returns>The task representing the asynchronous operation.</returns>
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

    /// <summary>
    /// Throws an exception if the ffmpeg process has failed, either due to an error in draining stderr or a non-zero exit code.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the ffmpeg process has failed.</exception>
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

    /// <summary>
    /// Disposes of the ffmpeg process runner.
    /// </summary>
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
        }

        stderrDrainTask.Wait(TimeSpan.FromSeconds(1));
        process.Dispose();
    }
}
