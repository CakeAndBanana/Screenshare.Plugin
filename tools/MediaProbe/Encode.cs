using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Encoding.LibavCodec;
using ScreenShare.Plugin.Rendering;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace MediaProbe;

internal static class Encode
{
    private const int Fps = 30;
    private const int StartKbps = 6000;
    private const int AdaptedKbps = 1500;

    private sealed record Source(byte[][] Bytes, Texture2D[]? Textures, Device? Device, Lock ContextLock);

    public static int Run(string ffmpegExePath, string workDirectory, int width, int height, int frames)
    {
        LibavEncoderSession.EnsureLoaded(Path.GetDirectoryName(ffmpegExePath)!);

        FFmpeg.AutoGen.ffmpeg.av_log_set_level(FFmpeg.AutoGen.ffmpeg.AV_LOG_ERROR);

        Console.WriteLine($"encode: {frames} frames per encoder, {width}x{height}@{Fps}, " +
            $"{StartKbps}kbps, adapted down to {AdaptedKbps}kbps halfway through");
        Console.WriteLine();

        byte[][] bytes = BuildFrames(width, height, count: Fps);

        using var device = new Device(
            SharpDX.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        using (var multithread = device.QueryInterface<Multithread>())
            multithread.SetMultithreadProtected(true);

        Texture2D[] textures = UploadFrames(device, bytes, width, height);
        var source = new Source(bytes, textures, device, new Lock());
        int failures = 0;

        try
        {
            foreach (GpuVendor vendor in new[] { GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel, GpuVendor.Unknown })
            {
                foreach (SupportedVideoCodec codec in new[] { SupportedVideoCodec.H264, SupportedVideoCodec.Hevc })
                {
                    bool hardware = vendor != GpuVendor.Unknown;
                    foreach (bool surfaces in hardware ? new[] { false, true } : new[] { false })
                    {
                        string name = VideoEncoderSelector.EncoderName(vendor, codec);
                        string label = $"{name} {(surfaces ? "d3d11" : "readback")}";
                        string outputPath = Path.Combine(workDirectory,
                            $"encode-{name}-{(surfaces ? "d3d11" : "readback")}.{VideoEncoderSelector.RawFormatName(codec)}");

                        try
                        {
                            failures += RunOne(vendor, codec, name, label, source, surfaces, width, height, frames, ffmpegExePath, outputPath) ? 0 : 1;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"{label,-20} unavailable: {ex.Message}");
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (Texture2D texture in textures)
                texture.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "Every encoder that opened produced a decodable stream."
            : $"{failures} encoder(s) opened but misbehaved - see above.");
        return failures == 0 ? 0 : 1;
    }

    private static bool RunOne(
        GpuVendor vendor, SupportedVideoCodec codec, string name, string label, Source source, bool surfaces,
        int width, int height, int frames, string ffmpegExePath, string outputPath)
    {
        using var session = new LibavEncoderSession(
            name, VideoEncoderSelector.EncoderOptions(vendor), codec, width, height, Fps, StartKbps,
            surfaces ? source.Device : null);

        long firstHalfBytes = 0;
        long secondHalfBytes = 0;
        int units = 0;
        int half = frames / 2;

        using var output = File.Create(outputPath);
        void OnAccessUnit(long ptsMs, byte[] accessUnit)
        {
            units++;
            output.Write(accessUnit);

            if (ptsMs < half * 1000L / Fps)
                firstHalfBytes += accessUnit.Length;
            else
                secondHalfBytes += accessUnit.Length;
        }

        var clock = Stopwatch.StartNew();
        for (int sent = 0; sent < frames; sent++)
        {
            if (sent == half)
                session.SetBitrate(AdaptedKbps);

            long ptsMs = sent * 1000L / Fps;
            if (!surfaces)
            {
                session.SendNv12(source.Bytes[sent % source.Bytes.Length], ptsMs, OnAccessUnit);
                continue;
            }

            bool copied;
            lock (source.ContextLock)
                copied = session.TryCopyToSurface(source.Device!.ImmediateContext, source.Textures![sent % source.Textures.Length]);

            if (!copied)
                throw new InvalidOperationException($"surface pool exhausted at frame {sent}");

            session.SendSurface(ptsMs, OnAccessUnit);
        }

        session.Flush(OnAccessUnit);
        double encodeFps = frames / clock.Elapsed.TotalSeconds;
        output.Dispose();

        double firstKbps = firstHalfBytes * 8.0 / (half / (double)Fps) / 1000;
        double secondKbps = secondHalfBytes * 8.0 / ((frames - half) / (double)Fps) / 1000;
        int decoded = CountDecodedFrames(ffmpegExePath, outputPath);

        bool decodedAll = decoded == frames && units == frames;
        bool bitrateMoved = secondKbps < firstKbps / 2;

        string liveBitrate = session.SupportsLiveBitrate
            ? (bitrateMoved ? "live bitrate OK" : "LIVE BITRATE IGNORED")
            : "live bitrate not claimed";

        Console.WriteLine(
            $"{label,-20} {encodeFps,6:0} fps  {firstKbps,5:0} -> {secondKbps,5:0} kbps  " +
            $"{liveBitrate,-24} decoded {decoded}/{frames}{(decodedAll ? "" : "  MISMATCH")}");

        return decodedAll && (!session.SupportsLiveBitrate || bitrateMoved);
    }

    private static byte[][] BuildFrames(int width, int height, int count)
    {
        var frames = new byte[count][];
        uint state = 0x9E3779B9;

        for (int f = 0; f < count; f++)
        {
            byte[] nv12 = new byte[width * height * 3 / 2];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    nv12[y * width + x] = (byte)(((x + y + f * 8) & 0xFF) / 2 + (state & 0x7F));
                }
            }

            for (int i = width * height; i < nv12.Length; i++)
                nv12[i] = (byte)(96 + ((i + f * 4) & 0x3F));

            frames[f] = nv12;
        }

        return frames;
    }

    private static Texture2D[] UploadFrames(Device device, byte[][] frames, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None,
        };

        using var staging = new Texture2D(device, description);

        description.Usage = ResourceUsage.Default;
        description.CpuAccessFlags = CpuAccessFlags.None;
        description.BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource;

        var textures = new Texture2D[frames.Length];
        for (int f = 0; f < frames.Length; f++)
        {
            SharpDX.DataBox box = device.ImmediateContext.MapSubresource(staging, 0, MapMode.Write, MapFlags.None);
            try
            {
                for (int y = 0; y < height * 3 / 2; y++)
                    Marshal.Copy(frames[f], y * width, box.DataPointer + y * box.RowPitch, width);
            }
            finally
            {
                device.ImmediateContext.UnmapSubresource(staging, 0);
            }

            textures[f] = new Texture2D(device, description);
            device.ImmediateContext.CopyResource(staging, textures[f]);
        }

        return textures;
    }

    private static int CountDecodedFrames(string ffmpegExePath, string streamPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExePath,
            Arguments = $"-hide_banner -nostats -v error -i \"{streamPath}\" -f framecrc -",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(startInfo)!;
        int count = 0;
        while (process.StandardOutput.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
                count++;
        }

        process.WaitForExit();
        return count;
    }
}
