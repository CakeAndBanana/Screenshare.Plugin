# ScreenShare.Plugin

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that lets you share your screen (or just one window) and audio with other players, rendered directly inside their game client - no external stream, no separate app to alt-tab to.

Create or join a room - public or private, with or without a password - then either watch someone else's stream in a popout window or start streaming yourself. Needs a running [ScreenShare.Relay](https://github.com/CakeAndBanana/Screenshare.Relay) instance to connect to - self-host your own, or use one someone's shared with you.

## Features

- **Public and private rooms** - give a room a name and make it public (listed for everyone on the relay) or private (joined by its code); either can have a password. Public rooms show in green, password-protected ones in blue, private ones in purple - hover a room to see its host and who's in it
- **Recent rooms** - every room you've been in on the relay, live or closed, one click to rejoin
- **Go-live notifications** - when someone in your room starts streaming you get a notification; click it to watch
- **Multi-streamer rooms** - the room owner can grant other members permission to stream too, simultaneously; viewers pick which stream to watch (like changing a channel) rather than everyone always watching the owner
- **Durable kicks** - a kick survives the kicked player reconnecting, relaunching the game, or reloading the plugin, for as long as the room exists - and the room disappears from their room lists
- **Flexible capture** - share a specific monitor, or one specific window instead of your whole screen
- **Flexible audio** - share your whole system's audio, or just one application's (and its child processes - useful for browsers, where audio usually renders in a subprocess)
- **Hardware video encoding** - NVENC (NVIDIA) / AMF (AMD) / QSV (Intel) with automatic detection, H.264 or HEVC, falling back to software encoding if no hardware encoder is available
- **Popout viewer window** - click a streaming member's name to open their feed in its own movable/resizable window, independent of the main room UI
- Persisted settings - relay URL, display name, and all streaming settings survive a plugin reload or game restart

## Requirements

- FFXIV with [XIVLauncher](https://goatcorp.github.io/) and Dalamud
- Windows 10 (window-specific capture requires Windows 10 1903 or later; monitor capture works on any Dalamud-supported Windows version)
- A running [ScreenShare.Relay](https://github.com/CakeAndBanana/Screenshare.Relay) instance to connect to

## Installing

This isn't on the official Dalamud plugin listing, so it's installed as a **dev plugin**:

1. Build it (see below), or grab a built copy.
2. In-game, run `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the folder containing `ScreenShare.Plugin.dll` and `ScreenShare.Plugin.json`.
3. Open the plugin installer (`/xlplugins`) → **Dev Plugins** tab → load ScreenShare from there.

## Building from source

Everything except ffmpeg restores from NuGet (SharpDX, NAudio, Concentus for Opus, the SignalR client, FFmpeg.AutoGen). ffmpeg is not committed to this repo (~156MB) and is needed before building:

1. Download BtbN's **shared** n7.1 Windows build with NVENC/AMF/QSV support from [BtbN's ffmpeg-builds](https://github.com/BtbN/FFmpeg-Builds/releases) - an `ffmpeg-n7.1*-win64-gpl-shared-7.1.zip`. It has to be the shared build (the plugin loads its DLLs in-process), on the 7.1 line (FFmpeg.AutoGen 7.1.1 binds to `avcodec-61`), and not `-lgpl` (which lacks some encoders this needs). The release workflow's `DEFAULT_FFMPEG_URL` is the exact build this was tested against.
2. Copy `ffmpeg.exe` and every `*.dll` from that archive's `bin` folder into `ffmpeg/` in this repo.
3. `dotnet build ScreenShare.Plugin.slnx -c Release`

Video normally goes through Media Foundation, which keeps frames on the GPU. ffmpeg takes over automatically per codec when the machine has no suitable hardware transform - most often for HEVC decode, which ships in the Microsoft Store's "HEVC Video Extensions" rather than in Windows. For encoding, the fallback calls the same NVENC/AMF/QSV/x264 encoders in-process through the ffmpeg DLLs - NVENC and AMF take the frames as D3D11 textures without them leaving the GPU, the others via a CPU readback - and only drops to running `ffmpeg.exe` if the DLLs won't load; decoding still runs `ffmpeg.exe`.

## MediaProbe

`tools/MediaProbe` is a console app for diagnosing the Media Foundation paths outside the game, where a media pipeline's failures all look the same from the outside - no picture, or frames going missing.

```
dotnet run --project tools/MediaProbe -- mft
```

Lists the encoder and decoder transforms this machine has, and whether each decoder can decode into D3D11 textures. A codec showing `none` will fall back to ffmpeg, which is expected rather than broken.

```
dotnet run --project tools/MediaProbe -- soak [frames] [WxH] [slots]
```

Decodes for a long time as fast as the hardware allows - 250,000 frames is about two and a half hours of 30fps video in around three minutes of wall clock - and watches the things that only go wrong slowly: the frame slot pool, handle count, private bytes against the managed heap, and the decode rate. Exits non-zero if any of them trend, so it can gate a release.

It generates its own test stream with the bundled ffmpeg, and calls the plugin's own Media Foundation interop rather than a copy of it, so a change to that code shows up here. The decode loop around it is necessarily reimplemented, since the real decoder is built around a device that only exists inside the game - keep the two in step, or the soak is exercising something the plugin no longer does.

```
dotnet run --project tools/MediaProbe -- encode [frames] [WxH]
```

Runs the in-process fallback encoder (the plugin's own `LibavEncoderSession`) for every vendor and codec, both ways the plugin feeds it - NV12 read back to the CPU, and for the hardware encoders NV12 textures handed over on the GPU (`d3d11`): whether it opens on this machine, how fast it encodes, whether a mid-stream bitrate drop takes effect for the encoders that claim live bitrate, and whether ffmpeg decodes every frame back. Encoders for hardware the machine doesn't have show as unavailable, which is expected; an encoder that opens but misbehaves makes it exit non-zero.

## Using it

1. Open the plugin window (`/screenshare`), enter your relay's URL (just the address is enough, e.g. `relay.example.com`), then a display name, and connect. Hover your name later to see your ID and the relay you're on; the pencil renames you.
2. **Create a room** (name, public or private, optional password, viewer cap), pick one from **Public rooms** or **Recent**, or **join by code**.
3. In the room, everyone's listed with badges: 👑 the owner, a person-check for anyone allowed to stream, and a red line with a LIVE badge (and a green name) for whoever's streaming. Hover a name to see that person's ID.
4. **To watch someone**, click their name - their stream opens in its own popout window. Click a different streaming member's name to switch.
5. **To stream**, pick your capture source, audio and quality in Settings (the cog in the window's title bar), then hit **Go live**. The owner and anyone allowed to stream can place the in-world screen; everyone watching sees it there automatically.
6. As the owner, grant/revoke streaming permission or kick people directly from the member list.

## Configuration

Everything is set from the plugin window itself and saved automatically - there's no config file to hand-edit. Relay URL, display name, and every streaming setting persist across sessions.

## License

AGPL-3.0-or-later - see [LICENSE](LICENSE).
