# ScreenShare.Plugin

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that lets you share your screen (or just one window) and audio with other players, rendered directly inside their game client - no external stream, no separate app to alt-tab to.

Create or join a password-protected room, then either watch someone else's stream in a popout window or start streaming yourself. Needs a running [ScreenShare.Relay](https://github.com/CakeAndBanana/Screenshare.Relay) instance to connect to - self-host your own, or use one someone's shared with you.

## Features

- **Password-protected rooms** - create a room with a password and a viewer cap, or join one with a room code
- **Multi-streamer rooms** - the room owner can grant other members permission to stream too, simultaneously; viewers pick which stream to watch (like changing a channel) rather than everyone always watching the owner
- **Durable kicks** - a kick survives the kicked player reconnecting, relaunching the game, or reloading the plugin, for as long as the room exists
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

Everything except one binary restores from NuGet (SharpDX, NAudio, Concentus for Opus, the SignalR client). That one binary is `ffmpeg.exe` - not committed to this repo (it's ~132MB, over GitHub's 100MB limit for a plain git push) and needed before building:

1. Download a Windows build of ffmpeg with NVENC/AMF/QSV support compiled in. This project was built and tested against [BtbN's ffmpeg-builds](https://github.com/BtbN/FFmpeg-Builds/releases) - grab the latest `ffmpeg-*-win64-gpl.zip` (not `-lgpl`, which lacks some encoders this needs).
2. Place `ffmpeg.exe` from that archive at `ffmpeg/ffmpeg.exe` in this repo.
3. `dotnet build ScreenShare.Plugin.slnx -c Release`

`ffmpeg.exe` is used both for hardware-accelerated encoding (it's what NVENC/AMF/QSV actually get invoked through) and for decoding on the viewer side.

## Using it

1. Open the plugin window (`/screenshare`), enter a display name and your relay's URL, and connect.
2. **Create a room** (pick a password and a viewer cap) or **join one** with a room code and password.
3. In the room, everyone's listed with icon badges: 👑 the owner, 📷 whoever's currently live, and a person-check badge for anyone else who's been granted permission to stream.
4. **To watch someone**, click their name - their stream opens in its own popout window. Click a different streaming member's name to switch.
5. **To stream**, open the collapsed "Streaming settings" panel to pick your capture source (monitor or window), audio source (system or one app), resolution/bitrate/fps/codec, then hit Start.
6. As the owner, grant/revoke streaming permission or kick people directly from the member list.

## Configuration

Everything is set from the plugin window itself and saved automatically - there's no config file to hand-edit. Relay URL, display name, and every streaming setting persist across sessions.

## License

AGPL-3.0-or-later - see [LICENSE](LICENSE).
