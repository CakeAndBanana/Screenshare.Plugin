using System.Diagnostics;
using ScreenShare.Plugin.Encoding;
using Device = SharpDX.Direct3D11.Device;

namespace MediaProbe;

/// <summary>
/// Runs the decode path for as long as you like and watches the things that only go wrong slowly.
///
/// Built after a ~2.5 hour session lost a third of its frames. That turned out not to be a leak -
/// the display side was throttling itself - but the session took two and a half hours to say so, and
/// this compresses the same run into minutes of wall clock by decoding as fast as the hardware will
/// go rather than at 30fps.
///
/// Four things are sampled, and between them they separate the failures that look identical from
/// outside (frames stop arriving):
///
/// - free slots     - the pool running dry is the only way this loop loses a frame on its own.
/// - handles        - a COM reference leaked per frame shows here long before anything else breaks.
/// - private bytes  - the whole process, managed and native together.
/// - managed heap    - reported beside it, because the two answer different questions: private bytes
///                     climbing while the managed heap stays flat means COM objects or native
///                     buffers, whereas the two climbing together usually means nothing at all,
///                     since the GC is entitled to let a heap grow before collecting it.
/// - decode rate    - the summary number; a leak elsewhere still eventually shows up as this falling.
/// </summary>
internal static class Soak
{
    public static int Run(Device device, string ffmpegExePath, string workDirectory, int width, int height, int frames, int slotCount)
    {
        string streamPath = TestStream.Ensure(
            ffmpegExePath, Path.Combine(workDirectory, $"soak-{width}x{height}.h264"), width, height, 10, 1500);

        List<byte[]> units = TestStream.SplitAccessUnits(File.ReadAllBytes(streamPath));

        Console.WriteLine($"soak: {frames} frames ({TimeSpan.FromSeconds(frames / 30.0):hh\\:mm\\:ss} of 30fps video), " +
            $"{width}x{height}, {slotCount} slots");
        Console.WriteLine();

        using var harness = new DecodeHarness(device, width, height, slotCount, VideoCodec.H264);
        Console.WriteLine($"decoder: {harness.DecoderName}");

        Process self = Process.GetCurrentProcess();
        long baselineHandles = 0;
        long baselineMemory = 0;
        int decoded = 0;
        int fed = 0;
        var clock = Stopwatch.StartNew();
        var sinceSample = Stopwatch.StartNew();
        int decodedAtSample = 0;

        while (decoded < frames)
        {
            foreach (byte[] unit in units)
            {
                // The stream is replayed end to end; each pass restarts with its own parameter sets
                // and IDR, which a decoder handles as a new GOP.
                decoded += harness.SubmitAndDrain(unit, fed * 1000L / 30);
                fed++;

                if (decoded - decodedAtSample >= 3000)
                {
                    self.Refresh();
                    long handles = self.HandleCount;
                    long memoryMb = self.PrivateMemorySize64 / 1024 / 1024;

                    // Deliberately NOT forcing a collection first. A soak that collects before every
                    // reading hides the growth it exists to find; the question is whether the process
                    // grows without bound, not what it would shrink to if asked nicely.
                    long managedMb = GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024;

                    if (baselineHandles == 0)
                    {
                        // Taken at the first sample, not at startup: the first few thousand frames
                        // include one-off allocation that would read as a leak if counted as drift.
                        baselineHandles = handles;
                        baselineMemory = memoryMb;
                        Console.WriteLine($"output shape: {harness.OutputShape}");
                        Console.WriteLine();
                        Console.WriteLine(
                            "   frames    video | slots | starved | handles      | private     | managed | fps");
                    }

                    double fps = (decoded - decodedAtSample) / sinceSample.Elapsed.TotalSeconds;

                    Console.WriteLine(
                        $"  {decoded,7} {TimeSpan.FromSeconds(decoded / 30.0):hh\\:mm\\:ss} | " +
                        $"{harness.FreeSlots,2}/{harness.SlotCount,-2} | " +
                        $"{harness.SlotStarvations,7} | " +
                        $"{handles,5} ({handles - baselineHandles,+4}) | " +
                        $"{memoryMb,4}MB ({memoryMb - baselineMemory,+4}) | " +
                        $"{managedMb,4}MB | {fps,6:F0}");

                    decodedAtSample = decoded;
                    sinceSample.Restart();
                }

                if (decoded >= frames)
                    break;
            }
        }

        self.Refresh();
        long finalHandles = self.HandleCount;
        long finalMemory = self.PrivateMemorySize64 / 1024 / 1024;

        Console.WriteLine();
        Console.WriteLine($"decoded {decoded} frames in {clock.Elapsed:hh\\:mm\\:ss} " +
            $"({decoded / clock.Elapsed.TotalSeconds:F0} fps), {harness.StreamChanges} stream change(s)");

        // Thresholds, not equality: handle counts move a little on their own, and the GC returns
        // memory on its own schedule. What matters is whether they TREND, which over tens of
        // thousands of frames a real per-frame leak certainly would.
        bool slotsHealthy = harness.SlotStarvations == 0 && harness.FreeSlots == harness.SlotCount;
        bool handlesHealthy = finalHandles - baselineHandles < 200;
        bool memoryHealthy = finalMemory - baselineMemory < 100;

        Report("slot pool", slotsHealthy,
            $"{harness.FreeSlots}/{harness.SlotCount} free, {harness.SlotStarvations} starvations");
        Report("handles", handlesHealthy, $"{finalHandles} ({finalHandles - baselineHandles:+#;-#;0} since baseline)");
        Report("private bytes", memoryHealthy, $"{finalMemory}MB ({finalMemory - baselineMemory:+#;-#;0}MB since baseline)");

        return slotsHealthy && handlesHealthy && memoryHealthy ? 0 : 1;
    }

    private static void Report(string what, bool healthy, string detail) =>
        Console.WriteLine($"  {(healthy ? "OK  " : "FAIL")}  {what,-14} {detail}");
}
