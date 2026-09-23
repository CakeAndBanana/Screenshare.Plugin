# Building ScreenShare

Developer notes: how to build the plugin and how to diagnose its media pipeline outside the game. For what the plugin does and how to install it, see the [README](README.md).

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
