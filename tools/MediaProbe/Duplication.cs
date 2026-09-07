using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using DeviceCreationFlags = SharpDX.Direct3D11.DeviceCreationFlags;

namespace MediaProbe;

internal static class Duplication
{
    // DuplicateOutput's failure codes say different things and are routinely conflated:
    //
    //   E_INVALIDARG                       the device is not on the adapter that OWNS this output.
    //   DXGI_ERROR_NOT_CURRENTLY_AVAILABLE something else already holds the one duplication slot.
    //   E_ACCESSDENIED                     protected content, or a desktop switch in progress.
    //   DXGI_ERROR_UNSUPPORTED             the adapter genuinely cannot duplicate.
    //
    // The plugin reports E_INVALIDARG as "this GPU does not support it", which is the one reading
    // that code does NOT mean. It means the pairing is wrong, so this probe pairs every output with
    // a device on its own adapter and reports what actually comes back.
    private const int InvalidArg = unchecked((int)0x80070057);
    private const int NotCurrentlyAvailable = unchecked((int)0x887A0022);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int AccessLost = unchecked((int)0x887A0026);
    private const int Unsupported = unchecked((int)0x887A0004);
    private const int SessionDisconnected = unchecked((int)0x887A0028);

    public static int Run()
    {
        // Whether the game is running changes what a pass here MEANS, and it is too easy to forget
        // which way round a given run was done. Out of process with the game closed only proves the
        // hardware works; out of process with the game OPEN proves the game's presence is not
        // blocking the outputs, which is the question that actually matters.
        System.Diagnostics.Process[] game = System.Diagnostics.Process.GetProcessesByName("ffxiv_dx11");
        if (game.Length == 0)
            game = System.Diagnostics.Process.GetProcessesByName("ffxiv");

        bool gameRunning = game.Length > 0;
        Console.WriteLine(gameRunning
            ? $"FINAL FANTASY XIV is RUNNING (pid {game[0].Id}) while this probe runs."
            : "FINAL FANTASY XIV is NOT running - this only tests the hardware, not the game's effect on it.");

        foreach (System.Diagnostics.Process process in game)
            process.Dispose();

        Console.WriteLine();

        using var factory = new Factory1();
        int adapterCount = factory.GetAdapterCount1();

        Console.WriteLine($"{adapterCount} adapter(s)");
        Console.WriteLine();

        var outputs = new List<(int AdapterIndex, string AdapterName, long Luid, int OutputIndex, OutputDescription Description)>();

        for (int a = 0; a < adapterCount; a++)
        {
            using Adapter1 adapter = factory.GetAdapter1(a);
            AdapterDescription1 description = adapter.Description1;

            bool software = (description.Flags & AdapterFlags.Software) != 0;
            Console.WriteLine($"  [{a}] {description.Description.Trim()}  LUID {description.Luid}" +
                (software ? "  (software)" : string.Empty));

            int outputCount = adapter.GetOutputCount();
            if (outputCount == 0)
            {
                Console.WriteLine("        no outputs attached to this adapter");
                continue;
            }

            for (int o = 0; o < outputCount; o++)
            {
                using Output output = adapter.GetOutput(o);
                OutputDescription outputDescription = output.Description;
                var bounds = outputDescription.DesktopBounds;

                Console.WriteLine(
                    $"        output {o}: {outputDescription.DeviceName}  " +
                    $"{bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top} at ({bounds.Left},{bounds.Top})  " +
                    $"attached={outputDescription.IsAttachedToDesktop}");

                outputs.Add((a, description.Description.Trim(), description.Luid, o, outputDescription));
            }
        }

        if (outputs.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No outputs at all. Nothing can be duplicated.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Duplicating each output with a device on its OWN adapter:");
        Console.WriteLine();

        int succeeded = 0;

        // Every device-creation flag combination the plugin has used, because the plugin's device
        // is not created the way a minimal one is: MediaFoundation needs VideoSupport, and the
        // shared capture/encode threads need multithread protection. Either could in principle be
        // what duplication objects to, and guessing which is how this stayed unsolved.
        (string Label, DeviceCreationFlags Flags, bool Multithreaded)[] configurations =
        {
            ("bare", DeviceCreationFlags.None, false),
            ("BgraSupport", DeviceCreationFlags.BgraSupport, false),
            ("Bgra+Video", DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, false),
            ("Bgra+Video+MT", DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, true),
        };

        foreach (var entry in outputs)
        {
            foreach ((string label, DeviceCreationFlags flags, bool multithreaded) in configurations)
            {
                using Adapter1 adapter = factory.GetAdapter1(entry.AdapterIndex);
                using Output output = adapter.GetOutput(entry.OutputIndex);

                string result = TryDuplicate(adapter, output, flags, multithreaded, out bool ok);
                if (ok && label == "Bgra+Video+MT")
                    succeeded++;

                Console.WriteLine($"  {entry.Description.DeviceName}  {label,-14} {result}");
            }

            Console.WriteLine();
        }

        // The plugin does not create its device at the driver's best feature level - it copies the
        // GAME's, so that the shared textures it hands the game's device are compatible. Desktop
        // Duplication has its own feature-level floor, so a game running below it would make the
        // plugin's device unable to duplicate while an ordinary device on the same GPU can.
        Console.WriteLine("Same adapter, by feature level:");
        Console.WriteLine();

        var levels = new[]
        {
            SharpDX.Direct3D.FeatureLevel.Level_12_1,
            SharpDX.Direct3D.FeatureLevel.Level_12_0,
            SharpDX.Direct3D.FeatureLevel.Level_11_1,
            SharpDX.Direct3D.FeatureLevel.Level_11_0,
            SharpDX.Direct3D.FeatureLevel.Level_10_1,
            SharpDX.Direct3D.FeatureLevel.Level_10_0,
            SharpDX.Direct3D.FeatureLevel.Level_9_3,
            SharpDX.Direct3D.FeatureLevel.Level_9_1,
        };

        var first = outputs[0];
        foreach (SharpDX.Direct3D.FeatureLevel level in levels)
        {
            using Adapter1 adapter = factory.GetAdapter1(first.AdapterIndex);
            using Output output = adapter.GetOutput(first.OutputIndex);

            Console.WriteLine($"  {first.Description.DeviceName}  {level,-12} " +
                TryDuplicate(
                    adapter, output,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    multithreaded: true, out _, level));
        }

        // The decisive test for the in-game failure. The plugin does not enumerate outputs from its
        // own factory - it takes the adapter off the GAME's D3D device and enumerates from that, so
        // the IDXGIOutput belongs to the game's DXGI factory while the device belongs to another.
        // Same GPU, same LUID, different factory. This reproduces that pairing.
        Console.WriteLine("Output and device from DIFFERENT DXGI factories (same GPU):");
        Console.WriteLine();

        {
            var owner = outputs[0];
            using var factoryA = new Factory1();
            using var factoryB = new Factory1();
            using Adapter1 adapterA = factoryA.GetAdapter1(owner.AdapterIndex);
            using Adapter1 adapterB = factoryB.GetAdapter1(owner.AdapterIndex);
            using Output outputFromA = adapterA.GetOutput(owner.OutputIndex);

            Console.WriteLine($"  {owner.Description.DeviceName}  output from factory A, device from factory B: " +
                TryDuplicate(adapterB, outputFromA, DeviceCreationFlags.BgraSupport, multithreaded: false, out _));

            using Output outputFromB = adapterB.GetOutput(owner.OutputIndex);
            Console.WriteLine($"  {owner.Description.DeviceName}  output and device both from factory B: " +
                TryDuplicate(adapterB, outputFromB, DeviceCreationFlags.BgraSupport, multithreaded: false, out _));
        }

        // Cross-pairing on purpose: with more than one adapter, duplicating an output using the
        // WRONG adapter's device is what produces E_INVALIDARG. Showing it next to the correct
        // pairing above is what distinguishes "this machine cannot duplicate" from "the plugin is
        // asking with the wrong device", which is the whole question.
        if (factory.GetAdapterCount1() > 1)
        {
            Console.WriteLine();
            Console.WriteLine("Same outputs, but asked with a DIFFERENT adapter's device:");
            Console.WriteLine();

            foreach (var entry in outputs)
            {
                int otherIndex = (entry.AdapterIndex + 1) % adapterCount;
                using Adapter1 other = factory.GetAdapter1(otherIndex);
                using Adapter1 owner = factory.GetAdapter1(entry.AdapterIndex);
                using Output output = owner.GetOutput(entry.OutputIndex);

                string result = TryDuplicate(
                    other, output, DeviceCreationFlags.BgraSupport, multithreaded: false, out _);
                Console.WriteLine($"  {entry.Description.DeviceName} via [{otherIndex}] {other.Description1.Description.Trim()}: {result}");
            }
        }

        Console.WriteLine();
        if (succeeded == 0)
        {
            Console.WriteLine("No output could be duplicated even from a plain process, so this is not about the plugin.");
        }
        else if (gameRunning)
        {
            Console.WriteLine(
                $"{succeeded}/{outputs.Count} output(s) duplicated from a plain process WHILE THE GAME WAS RUNNING. " +
                "So the game's presence is not blocking the outputs, and nothing else holds them - which leaves the " +
                "objects the plugin duplicates WITH, inside the game process, as the only remaining difference.");
        }
        else
        {
            Console.WriteLine(
                $"{succeeded}/{outputs.Count} output(s) duplicated with the game closed. That only proves the hardware " +
                "is capable - run this again WHILE THE GAME IS OPEN to find out whether its presence is what blocks it.");
        }

        return succeeded > 0 ? 0 : 1;
    }

    private static string TryDuplicate(
        Adapter1 adapter, Output output, DeviceCreationFlags flags, bool multithreaded, out bool ok,
        SharpDX.Direct3D.FeatureLevel? featureLevel = null)
    {
        ok = false;

        Device? device = null;
        Output1? output1 = null;

        try
        {
            device = featureLevel is { } level
                ? new Device(adapter, flags, level)
                : new Device(adapter, flags);

            if (multithreaded)
            {
                using var multithread = device.QueryInterface<SharpDX.Direct3D11.Multithread>();
                multithread.SetMultithreadProtected(true);
            }

            output1 = output.QueryInterface<Output1>();

            using OutputDuplication duplication = output1.DuplicateOutput(device);
            ok = true;
            return "OK";
        }
        catch (SharpDX.SharpDXException ex)
        {
            return $"{Explain(ex.ResultCode.Code)} (0x{ex.ResultCode.Code:X8})";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            output1?.Dispose();
            device?.Dispose();
        }
    }

    private static string Explain(int hr) => hr switch
    {
        InvalidArg => "E_INVALIDARG - this device is not on the adapter that owns the output",
        NotCurrentlyAvailable => "NOT_CURRENTLY_AVAILABLE - something else already holds this output's duplication",
        AccessDenied => "E_ACCESSDENIED - protected content, or a desktop switch in progress",
        AccessLost => "ACCESS_LOST - the desktop changed underneath the call",
        Unsupported => "UNSUPPORTED - this adapter cannot duplicate at all",
        SessionDisconnected => "SESSION_DISCONNECTED - no interactive session",
        _ => "failed",
    };
}
