using System.Runtime.InteropServices;
using MediaProbe;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Interop;
using SharpDX.Direct3D11;
using static ScreenShare.Plugin.Encoding.Interop.MediaFoundationInterop;
using Device = SharpDX.Direct3D11.Device;

// Diagnostics for the plugin's Media Foundation paths, outside the game.
//
// Everything here exists because a media pipeline's failures look identical from the outside - no
// picture, or frames going missing - and each of these separates a different cause. Every finding
// they have produced so far was invisible to reading the code: encoder MFT flags that were silently
// wrong, decoder output textures that are padded and array-backed, an ffmpeg stdin write that never
// left its buffer.

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

if (command is "help" or "-h" or "--help")
{
    Console.WriteLine("""
        MediaProbe - Media Foundation diagnostics for ScreenShare.Plugin

          mft                              What encoder and decoder MFTs this machine has.
          duplication                      Which monitors Desktop Duplication will actually capture.
          sharedtex [adapter]              Whether a D3D11 texture shared from one device opens on
                                           another. Only interesting under Wine, where it decides
                                           how much work the viewer path needs. Default adapter 0.
          soak [frames] [WxH] [slots]      Decode for a long time; watch slots, handles, memory.
                                           Default 54000 frames (30 min of video), 1280x720, 17 slots.

        Examples:
          dotnet run --project ScreenShare.Plugin/tools/MediaProbe -- mft
          dotnet run --project ScreenShare.Plugin/tools/MediaProbe -- soak
          dotnet run --project ScreenShare.Plugin/tools/MediaProbe -- soak 200000 1920x1080
          wine MediaProbe.exe sharedtex

        Exits non-zero when a soak finds something, so it can gate a release.
        """);
    return 0;
}

// Deliberately NOT started before the dispatch below. Media Foundation is one of the things that
// does not exist under Wine, so starting it up front would take the commands that have nothing to
// do with it - duplication, sharedtex - down with it on exactly the platform they were added to
// investigate. Each command that needs it starts it itself.
if (command == "duplication")
    return Duplication.Run();

if (command == "sharedtex")
{
    int adapterIndex = args.Length > 1 && int.TryParse(args[1], out int parsedAdapter) ? parsedAdapter : 0;
    return SharedTexture.Run(adapterIndex);
}

MediaFoundationInterop.EnsureStarted();

if (command == "mft")
{
    ReportMfts();
    return 0;
}

if (command != "soak")
{
    Console.Error.WriteLine($"Unknown command '{command}'. Try 'help'.");
    return 2;
}

int frames = args.Length > 1 && int.TryParse(args[1], out int parsedFrames) ? parsedFrames : 54000;
(int width, int height) = args.Length > 2 ? ParseSize(args[2]) : (1280, 720);
int slots = args.Length > 3 && int.TryParse(args[3], out int parsedSlots) ? parsedSlots : 17;

string ffmpegExePath = FindFfmpeg();
string workDirectory = Path.Combine(Path.GetTempPath(), "ScreenShareMediaProbe");
Directory.CreateDirectory(workDirectory);

// VideoSupport and multithread protection both matter: the plugin's device carries them, and a
// decoder bound to a device without them behaves differently enough that soaking one would not be
// soaking the other.
using var device = new Device(
    SharpDX.Direct3D.DriverType.Hardware,
    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);

using (var multithread = device.QueryInterface<Multithread>())
    multithread.SetMultithreadProtected(true);

return Soak.Run(device, ffmpegExePath, workDirectory, width, height, frames, slots);

static (int Width, int Height) ParseSize(string value)
{
    string[] parts = value.ToLowerInvariant().Split('x');
    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
        return (w, h);

    throw new ArgumentException($"Could not read '{value}' as a size; expected something like 1280x720.");
}

static string FindFfmpeg()
{
    for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        string candidate = Path.Combine(directory.FullName, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(candidate))
            return candidate;
    }

    throw new FileNotFoundException(
        "Could not find the bundled ffmpeg\\ffmpeg.exe. See the README for where to download it.");
}

static void ReportMfts()
{
    Console.WriteLine("Encoders (NV12 in):");
    ReportCategory(isEncoder: true, "H.264", VideoFormatH264);
    ReportCategory(isEncoder: true, "HEVC ", VideoFormatHevc);

    Console.WriteLine();
    Console.WriteLine("Decoders (NV12 out):");
    ReportCategory(isEncoder: false, "H.264", VideoFormatH264);
    ReportCategory(isEncoder: false, "HEVC ", VideoFormatHevc);

    Console.WriteLine();
    Console.WriteLine("""
        A codec with no decoder falls back to the ffmpeg decoder, which is correct rather than
        broken - HEVC decode ships in the Microsoft Store's "HEVC Video Extensions", not in Windows,
        so a machine without it will list nothing here and still play HEVC through ffmpeg.
        """);
}

static void ReportCategory(bool isEncoder, string label, Guid codecSubtype)
{
    // Encoders are async and hardware-registered; decoders are synchronous, and Microsoft's own
    // H.264 decoder is registered as software even though it decodes on the GPU through DXVA once
    // it has a D3D manager. Asking either for the other's flags returns nothing at all - which is
    // exactly the bug this report was written to make visible.
    MftEnumFlags flags = isEncoder
        ? MftEnumFlags.Hardware | MftEnumFlags.AsyncMft | MftEnumFlags.SortAndFilter
        : MftEnumFlags.Hardware | MftEnumFlags.SyncMft | MftEnumFlags.SortAndFilter;

    List<nint> found = isEncoder
        ? EnumerateEncoders(codecSubtype, flags)
        : EnumerateDecoders(codecSubtype, flags);

    if (found.Count == 0)
    {
        Console.WriteLine($"  {label}: none");
        return;
    }

    var names = new List<string>();
    foreach (nint pointer in found)
    {
        using var activate = new SharpDX.MediaFoundation.Activate(pointer);
        string name;
        try
        {
            name = activate.Get<string>(MftFriendlyName);
        }
        catch (Exception)
        {
            name = "(unnamed)";
        }

        names.Add(isEncoder ? name : name + (IsD3D11Aware(activate) ? " [D3D11]" : " [system memory only]"));
    }

    Console.WriteLine($"  {label}: {string.Join(", ", names)}");
}

static bool IsD3D11Aware(SharpDX.MediaFoundation.Activate activate)
{
    SharpDX.MediaFoundation.Transform? transform = null;
    try
    {
        transform = activate.ActivateObject<SharpDX.MediaFoundation.Transform>(
            typeof(SharpDX.MediaFoundation.Transform).GUID);

        using SharpDX.MediaFoundation.MediaAttributes attributes = transform.Attributes;
        return attributes.Get<int>(MfSaD3D11Aware) != 0;
    }
    catch (Exception)
    {
        // Missing attribute, or an MFT that will not instantiate here - both mean "not usable for
        // GPU decode", which is what the caller is asking.
        return false;
    }
    finally
    {
        transform?.Dispose();
    }
}
