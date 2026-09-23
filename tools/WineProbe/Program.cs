using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

int failures = 0;

Console.WriteLine("WineProbe - ScreenShare Linux/Wine assumption checks");
Console.WriteLine(new string('=', 62));

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

if (command is "help" or "-h" or "--help")
{
    Console.WriteLine("""

        Usage: wine WineProbe.exe [command]

          all                     Every check below. The default.
          wine                    Is this a Wine prefix, and which version.
          connect [host:port]     Can a process in the prefix reach a LINUX listener.
                                  Defaults to 127.0.0.1:47823.
          ffmpeg [path]           Does the bundled ffmpeg.exe run under Wine.
                                  Defaults to ffmpeg/ffmpeg.exe next to this binary.

        Before running 'connect' or 'all', start a listener on the Linux side:
          nc -l -p 47823          (or: nc -l 47823, depending on your netcat)

        Exits non-zero if any check fails, so it can gate the work that depends on it.
        """);
    return 0;
}

if (command is not ("all" or "wine" or "connect" or "ffmpeg"))
{
    Console.Error.WriteLine($"Unknown command '{command}'. Try 'help'.");
    return 2;
}

if (command is "all" or "wine")
    failures += CheckWine() ? 0 : 1;

if (command is "all" or "connect")
{
    string target = command == "connect" && args.Length > 1 ? args[1] : "127.0.0.1:47823";
    failures += CheckLoopback(target) ? 0 : 1;
}

if (command is "all" or "ffmpeg")
{
    string? explicitPath = command == "ffmpeg" && args.Length > 1 ? args[1] : null;
    failures += CheckFfmpeg(explicitPath) ? 0 : 1;
}

Console.WriteLine();
Console.WriteLine(new string('=', 62));
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED - see above.");

return failures == 0 ? 0 : 1;

static bool CheckWine()
{
    Section("Wine detection");

    nint ntdll = NativeMethods.GetModuleHandleW("ntdll.dll");
    if (ntdll == nint.Zero)
    {
        Fail("GetModuleHandleW(ntdll.dll) returned NULL, which should be impossible.");
        return false;
    }

    nint getVersion = NativeMethods.GetProcAddress(ntdll, "wine_get_version");
    if (getVersion == nint.Zero)
    {
        Line("wine_get_version", "not exported");
        Line("verdict", "NOT running under Wine (real Windows)");
        Console.WriteLine();
        Console.WriteLine("  Expected on a Windows dev box. Run this under Wine on Linux for the");
        Console.WriteLine("  rest of the checks to mean anything.");
        return true;
    }

    var wineGetVersion = Marshal.GetDelegateForFunctionPointer<NativeMethods.WineGetVersion>(getVersion);
    Line("wine_get_version", Marshal.PtrToStringAnsi(wineGetVersion()) ?? "(null)");

    nint getHost = NativeMethods.GetProcAddress(ntdll, "wine_get_host_version");
    if (getHost != nint.Zero)
    {
        var wineGetHostVersion =
            Marshal.GetDelegateForFunctionPointer<NativeMethods.WineGetHostVersion>(getHost);
        wineGetHostVersion(out nint sysname, out nint release);
        Line("host system", $"{Marshal.PtrToStringAnsi(sysname)} {Marshal.PtrToStringAnsi(release)}");
    }

    Line("OSVersion", Environment.OSVersion.ToString());
    Line("verdict", "under Wine - PlatformInfo.IsWine would be true");
    return true;
}

static bool CheckLoopback(string target)
{
    Section($"Loopback TCP to a Linux listener ({target})");

    Console.WriteLine("  The check the whole architecture rests on: the helper daemon runs as a");
    Console.WriteLine("  native Linux process OUTSIDE the prefix and the plugin reaches it over");
    Console.WriteLine("  loopback TCP. Unix sockets do not cross the boundary and the host has no");
    Console.WriteLine("  named pipes, so if this fails the design needs rethinking, not patching.");
    Console.WriteLine();

    (string host, int port) = ParseTarget(target);

    try
    {
        using var client = new TcpClient();

        var stopwatch = Stopwatch.StartNew();
        if (!client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(5)))
        {
            Fail($"connect to {host}:{port} timed out after 5s.");
            Console.WriteLine();
            Console.WriteLine("  Start a listener on the LINUX side first:  nc -l -p 47823");
            return false;
        }

        Line("connect", $"OK in {stopwatch.ElapsedMilliseconds}ms");

        byte[] payload = Encoding.UTF8.GetBytes("SCREENSHARE-WINEPROBE-HELLO\n");
        NetworkStream stream = client.GetStream();
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
        Line("write", $"OK, {payload.Length} bytes sent");
        Console.WriteLine("                     (the netcat window should now show SCREENSHARE-WINEPROBE-HELLO)");

        client.ReceiveTimeout = 2000;
        try
        {
            byte[] buffer = new byte[256];
            int read = stream.Read(buffer, 0, buffer.Length);
            Line("read back", read > 0
                ? $"{read} bytes - {Encoding.UTF8.GetString(buffer, 0, read).Trim()}"
                : "listener closed without replying (fine)");
        }
        catch (IOException)
        {
            Line("read back", "no reply within 2s (fine - plain netcat sends nothing)");
        }

        Line("verdict", "the prefix CAN reach a host listener");
        return true;
    }
    catch (Exception ex)
    {
        Fail($"{ex.GetType().Name}: {ex.Message}");
        Console.WriteLine();
        Console.WriteLine("  If no listener was running, that is the likely cause rather than Wine.");
        Console.WriteLine("  Start one on the LINUX side and re-run:  nc -l -p 47823");
        return false;
    }
}

static bool CheckFfmpeg(string? explicitPath)
{
    Section("Bundled ffmpeg.exe under Wine");

    Console.WriteLine("  The viewer path falls back to the bundled Windows ffmpeg build for decode,");
    Console.WriteLine("  so it has to run as a PE process inside the prefix. Hardware encoders are");
    Console.WriteLine("  NOT expected to work here - libx264/libx265 being present is the pass.");
    Console.WriteLine();

    string path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    if (!File.Exists(path))
    {
        Fail($"not found at {path}");
        Console.WriteLine(@"  Pass an explicit path:  wine WineProbe.exe ffmpeg C:\path\to\ffmpeg.exe");
        return false;
    }

    try
    {
        using var process = Process.Start(new ProcessStartInfo(path, "-hide_banner -version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Fail("ffmpeg did not exit within 30s.");
            return false;
        }

        string firstLine =
            FirstLine(stdout) ?? FirstLine(stderr) ?? "(no output)";

        Line("exit code", process.ExitCode.ToString());
        Line("version", firstLine);

        if (process.ExitCode != 0)
        {
            Fail("ffmpeg ran but exited non-zero.");
            if (stderr.Trim().Length > 0)
                Line("stderr", stderr.Trim());
            return false;
        }

        Line("verdict", "the bundled ffmpeg.exe runs under Wine");
        return true;
    }
    catch (Exception ex)
    {
        Fail($"{ex.GetType().Name}: {ex.Message}");
        return false;
    }
}

static string? FirstLine(string text) =>
    text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

static (string Host, int Port) ParseTarget(string target)
{
    int colon = target.LastIndexOf(':');
    if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out int port))
        return (target, 47823);

    return (target[..colon], port);
}

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"-- {title} " + new string('-', Math.Max(0, 58 - title.Length)));
}

static void Line(string label, string value) =>
    Console.WriteLine($"  {label.PadRight(17)}: {value}");

static void Fail(string message) => Line("FAILED", message);

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    internal static extern nint GetProcAddress(nint module, string procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint WineGetVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void WineGetHostVersion(out nint sysname, out nint release);
}
