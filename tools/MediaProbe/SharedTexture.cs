using ScreenShare.Plugin.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace MediaProbe;

internal static class SharedTexture
{
    private const int Width = 256;
    private const int Height = 128;

    public static int Run(int adapterIndex)
    {
        using var factory = new Factory1();

        if (adapterIndex >= factory.GetAdapterCount1())
        {
            Console.Error.WriteLine($"No adapter {adapterIndex}; this machine has {factory.GetAdapterCount1()}.");
            return 2;
        }

        using Adapter1 adapter = factory.GetAdapter1(adapterIndex);
        Console.WriteLine($"Adapter [{adapterIndex}] {adapter.Description1.Description.Trim()}");
        Console.WriteLine();

        using var gameDevice = new Device(adapter, DeviceCreationFlags.BgraSupport);
        using var pluginDevice = new Device(
            adapter,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            gameDevice.FeatureLevel);

        using (var multithread = pluginDevice.QueryInterface<Multithread>())
            multithread.SetMultithreadProtected(true);

        Console.WriteLine($"  game-side device   : feature level {gameDevice.FeatureLevel}");
        Console.WriteLine($"  plugin-side device : feature level {pluginDevice.FeatureLevel}, Bgra+Video, MT-protected");
        Console.WriteLine();

        byte[] pattern = BuildPattern();

        bool crossDevice = Attempt(
            "cross-device share (the real question)",
            pluginDevice, gameDevice, pattern);

        bool sameDevice = Attempt(
            "same-device control (should always pass)",
            pluginDevice, pluginDevice, pattern);

        Console.WriteLine();
        Console.WriteLine(new string('-', 70));

        if (crossDevice)
        {
            Console.WriteLine("PASS - shared handles work across devices on this machine.");
            Console.WriteLine();
            Console.WriteLine("  If this ran under Wine: the viewer path needs only that the ffmpeg");
            Console.WriteLine("  decoder be forced instead of Media Foundation. The single-device");
            Console.WriteLine("  refactor of H264Decoder/GameAdapterDevice is NOT needed.");
            return 0;
        }

        Console.WriteLine("FAIL - a texture shared from one device could not be used on another.");
        Console.WriteLine();
        if (!sameDevice)
        {
            Console.WriteLine("  The same-device control ALSO failed, so this is not about sharing.");
            Console.WriteLine("  Something more basic is wrong; read the errors above before drawing");
            Console.WriteLine("  any conclusion about the viewer path.");
            return 1;
        }

        Console.WriteLine("  The same-device control passed, so this is specifically shared handles.");
        Console.WriteLine("  If this ran under Wine: H264Decoder must create its decode texture on");
        Console.WriteLine("  the game's device with OptionFlags.None and upload from the render");
        Console.WriteLine("  thread, because the game's device is not multithread-protected.");
        return 1;
    }

    private static bool Attempt(string label, Device producer, Device consumer, byte[] pattern)
    {
        Console.WriteLine($"-- {label} " + new string('-', Math.Max(0, 52 - label.Length)));

        Texture2D? shared = null;
        Texture2D? opened = null;
        Texture2D? staging = null;

        try
        {
            shared = SharedTextureFactory.CreateEmpty(producer, Width, Height);
            Console.WriteLine("  create shared      : OK");

            producer.ImmediateContext.UpdateSubresource(pattern, shared, 0, Width * 4, 0);

            WaitForGpu(producer);
            Console.WriteLine("  UpdateSubresource  : OK");

            nint handle = SharedTextureFactory.GetSharedHandle(shared);
            if (handle == nint.Zero)
            {
                Console.WriteLine("  shared handle      : FAILED - got NULL");
                return false;
            }

            Console.WriteLine($"  shared handle      : OK (0x{handle:X})");

            opened = consumer.OpenSharedResource<Texture2D>(handle);
            Console.WriteLine("  OpenSharedResource : OK");

            using (var srv = new ShaderResourceView(consumer, opened))
            {
                if (srv.NativePointer == nint.Zero)
                {
                    Console.WriteLine("  ShaderResourceView : FAILED - got NULL");
                    return false;
                }
            }

            Console.WriteLine("  ShaderResourceView : OK");

            staging = new Texture2D(consumer, new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None,
            });

            consumer.ImmediateContext.CopyResource(opened, staging);
            WaitForGpu(consumer);

            DataBox box = consumer.ImmediateContext.MapSubresource(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                int mismatches = CountMismatches(box, pattern);
                if (mismatches > 0)
                {
                    Console.WriteLine($"  content round trip : FAILED - {mismatches} mismatched pixel(s)");
                    return false;
                }

                Console.WriteLine("  content round trip : OK - every pixel survived");
            }
            finally
            {
                consumer.ImmediateContext.UnmapSubresource(staging, 0);
            }

            Console.WriteLine();
            return true;
        }
        catch (SharpDXException ex)
        {
            Console.WriteLine($"  FAILED             : 0x{ex.ResultCode.Code:X8} {ex.Message.Split('\n')[0].Trim()}");
            Console.WriteLine();
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAILED             : {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine();
            return false;
        }
        finally
        {
            staging?.Dispose();
            opened?.Dispose();
            shared?.Dispose();
        }
    }

    private static void WaitForGpu(Device device)
    {
        using var query = new Query(device, new QueryDescription
        {
            Type = QueryType.Event,
            Flags = QueryFlags.None,
        });

        device.ImmediateContext.End(query);
        device.ImmediateContext.Flush();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!device.ImmediateContext.IsDataAvailable(query))
        {
            if (stopwatch.ElapsedMilliseconds > 2000)
                throw new TimeoutException("GPU did not signal completion within 2s.");

            Thread.Yield();
        }
    }

    private static byte[] BuildPattern()
    {
        byte[] pattern = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                pattern[i + 0] = (byte)x;
                pattern[i + 1] = (byte)y;
                pattern[i + 2] = (byte)(x ^ y);
                pattern[i + 3] = 0xFF;
            }
        }

        return pattern;
    }

    private static int CountMismatches(DataBox box, byte[] expected)
    {
        int mismatches = 0;
        byte[] row = new byte[Width * 4];

        for (int y = 0; y < Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                box.DataPointer + (y * box.RowPitch), row, 0, row.Length);

            for (int x = 0; x < row.Length; x++)
            {
                if (row[x] != expected[(y * Width * 4) + x])
                    mismatches++;
            }
        }

        return mismatches;
    }
}
