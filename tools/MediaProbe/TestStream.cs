using System.Diagnostics;

namespace MediaProbe;

internal static class TestStream
{
    public static string Ensure(string ffmpegExePath, string path, int width, int height, int seconds, int bitrateKbps)
    {
        if (File.Exists(path))
            return path;

        string arguments =
            $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size={width}x{height}:rate=30 " +
            $"-t {seconds} -threads 1 -c:v libx264 -preset veryfast -x264-params sliced-threads=0:slices=1 " +
            $"-b:v {bitrateKbps}k -g 60 -pix_fmt yuv420p -f h264 \"{path}\"";

        using var process = Process.Start(new ProcessStartInfo(ffmpegExePath, arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not start {ffmpegExePath}.");

        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 || !File.Exists(path))
            throw new InvalidOperationException($"ffmpeg could not build the test stream: {errors}");

        return path;
    }

    public static List<byte[]> SplitAccessUnits(byte[] stream)
    {
        var starts = new List<int>();
        for (int i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
                starts.Add(i);
        }

        var units = new List<byte[]>();
        int unitStart = starts.Count > 0 ? starts[0] : 0;
        bool sawSlice = false;

        foreach (int nalStart in starts)
        {
            int type = stream[nalStart + 3] & 0x1F;
            bool isSlice = type is 1 or 5;

            if (isSlice && sawSlice)
            {
                units.Add(stream[unitStart..nalStart]);
                unitStart = nalStart;
                sawSlice = false;
            }

            if (isSlice)
                sawSlice = true;
        }

        if (unitStart < stream.Length)
            units.Add(stream[unitStart..]);

        return units;
    }
}
