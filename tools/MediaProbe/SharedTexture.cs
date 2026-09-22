using ScreenShare.Plugin.Rendering;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace MediaProbe;

/// <summary>
/// Whether a D3D11 texture shared from one device can be opened on another, on this machine.
///
/// On Windows this is so reliable that it is not worth asking. Under Wine it is the open question
/// that decides how much work the viewer path needs: DXVK implements shared handles only partly,
/// and the plugin leans on them in a place it does not have to. <c>H264Decoder</c> creates its
/// decode texture with <see cref="ResourceOptionFlags.Shared"/> on the plugin's own device and
/// <c>ShareWindow</c> opens it on the game's, even though the ffmpeg path only ever fills that
/// texture with an UpdateSubresource from a managed array - which needs no second device at all.
///
/// So: if this passes under Wine, the viewer path collapses to "force the ffmpeg decoder" and the
/// whole single-device refactor is unnecessary. If it fails, that refactor is the work. Nothing
/// else distinguishes those two outcomes, and neither can be guessed from Windows.
/// </summary>
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

        // Mirrors GameAdapterDevice exactly: the game's device is an ordinary one, and the plugin
        // makes a SECOND device on the same adapter at the game's feature level with VideoSupport
        // (which exists only for Media Foundation). Probing any other pair would answer a question
        // nobody is asking.
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

        // A control. If the cross-device case fails but this passes, the failure is specifically
        // about sharing rather than about textures, UpdateSubresource or readback being broken -
        // which is the difference between "refactor the viewer path" and "nothing works here".
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
            // The plugin's own helper, not a copy of it, so a change to the real flags shows up here.
            shared = SharedTextureFactory.CreateEmpty(producer, Width, Height);
            Console.WriteLine("  create shared      : OK");

            producer.ImmediateContext.UpdateSubresource(pattern, shared, 0, Width * 4, 0);

            // A plain Shared texture carries no keyed mutex and no fence, so Flush only STARTS the
            // upload - it does not wait for it. Without this the consumer's CopyResource below can
            // read the texture before the write has retired: an early version of this probe did
            // exactly that and reported a corrupt round trip intermittently on a machine where
            // sharing works perfectly. That false FAIL is the worst outcome this probe can produce,
            // because it would send someone into the single-device refactor to fix a bug in here.
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

            // What SharedTextureBridge does with it. Reimplemented rather than called, because the
            // real one returns a RawTextureWrap and that drags in Dalamud, which cannot load here.
            using (var srv = new ShaderResourceView(consumer, opened))
            {
                if (srv.NativePointer == nint.Zero)
                {
                    Console.WriteLine("  ShaderResourceView : FAILED - got NULL");
                    return false;
                }
            }

            Console.WriteLine("  ShaderResourceView : OK");

            // Opening a handle can succeed while the pixels behind it are not actually the same
            // memory, which is the failure mode that looks like a working stream showing black.
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

    /// <summary>
    /// Blocks until everything already submitted on this device's immediate context has retired.
    /// An Event query signals exactly that, and is the only ordering guarantee available between
    /// two devices sharing a texture that has no keyed mutex.
    /// </summary>
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
            // A device that never signals is itself a finding, so bound the wait rather than
            // hanging the probe on it. Two seconds is far beyond a 256x128 upload.
            if (stopwatch.ElapsedMilliseconds > 2000)
                throw new TimeoutException("GPU did not signal completion within 2s.");

            Thread.Yield();
        }
    }

    private static byte[] BuildPattern()
    {
        // Varies along both axes so a transposed, stride-mangled or partially-copied result reads
        // as a mismatch rather than coincidentally matching.
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
            // Never assume the staging row pitch equals width*4 - drivers pad it, and reading as
            // though they do not is itself a bug that would masquerade as a share failure.
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
