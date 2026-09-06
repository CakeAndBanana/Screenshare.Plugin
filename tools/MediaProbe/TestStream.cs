using System.Diagnostics;

namespace MediaProbe;

/// <summary>
/// Makes and slices the H.264 the harness is fed.
///
/// Generated with the bundled ffmpeg rather than committed, so there is no binary in the repo and
/// the stream always matches the encoder settings the plugin actually streams with.
/// </summary>
internal static class TestStream
{
    /// <summary>
    /// Encodes a test pattern to Annex-B H.264, reusing the file if it already exists.
    /// </summary>
    /// <remarks>
    /// <c>sliced-threads=0:slices=1</c> is not decoration. libx264's default threading emits several
    /// slice NALs per frame, and the splitter below then yields one "access unit" per slice - five
    /// times as many, each a fifth the size. A benchmark fed those measures something that is not a
    /// frame rate, which is a mistake this harness has already made once.
    /// </remarks>
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

    /// <summary>
    /// Splits an Annex-B stream into access units, one per frame - the shape the Media Foundation
    /// encoder emits and the decoder is fed on the wire.
    ///
    /// A new unit starts at the first slice NAL (type 1 or 5) once the current one already holds a
    /// slice; parameter sets and SEI attach to the unit that follows them.
    /// </summary>
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
