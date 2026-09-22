# WineProbe

Settles the assumptions Linux support is built on, from inside a Wine prefix, without the game.

Linux support is being planned without a Linux test rig, so a handful of load-bearing questions
can't be answered from a Windows dev box. This binary answers them in about two minutes, on
someone else's machine, with no FFXIV and no Dalamud involved.

It links **nothing** — not the plugin, not SharpDX, not Dalamud. Each of those is itself in
question under Wine, and a probe that can't start tells you nothing.

## What it checks

| Check | Question it settles |
|---|---|
| `wine` | Is `wine_get_version` exported from `ntdll`? This is the exact check `PlatformInfo.IsWine` will ship, so a pass here is evidence about the real thing. |
| `connect` | Can a process inside the prefix reach a listener running as a native Linux process? **The whole helper-daemon architecture rests on this.** Unix sockets don't cross the boundary and the host has no named pipes, so if this fails the design needs rethinking, not patching. |
| `ffmpeg` | Does the bundled Windows `ffmpeg.exe` run as a PE process inside the prefix? The viewer path falls back to it for decode. Hardware encoders are **not** expected to work — `libx264`/`libx265` being present is the pass. |

There is a fourth check, in the sibling tool, because it needs D3D11:

```
wine MediaProbe.exe sharedtex
```

Whether a D3D11 texture shared from one device opens on another. This one decides how much work
the **viewer** path needs — DXVK implements shared handles only partly, and `H264Decoder` leans on
them in a place it doesn't have to. A pass means the viewer path needs nothing more than forcing
the ffmpeg decoder; a fail means `H264Decoder` and `GameAdapterDevice` need a single-device mode.
`MediaProbe.exe duplication` is also worth running — under Wine it should fail, confirming why
monitor capture can't work in-prefix.

## Building it for a tester

A tester's prefix has Dalamud's private .NET runtime in it, not an installed one this could bind
to, so the binary has to carry its own:

```
dotnet publish tools/WineProbe -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

That produces a single `WineProbe.exe` (~37 MB). Send them that one file.

These settings are deliberately *not* in the `.csproj` — as project properties they'd apply to
plain `dotnet build` as well, pulling the whole win-x64 runtime pack into every solution build and
CI run for a diagnostic nobody builds on purpose.

For the `sharedtex` check, publish the sibling tool the same way:

```
dotnet publish tools/MediaProbe -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

That one is ~120 MB because it drags in SharpDX and the plugin. It runs without Dalamud: the
`sharedtex` and `duplication` paths touch only `SharedTextureFactory` and DXGI, and Media
Foundation startup was deliberately moved out of the shared entry point so those two commands
still work on a platform where MF does not exist.

## Running it

On the **Linux** side, start a listener first:

```
nc -l -p 47823        # or: nc -l 47823, depending on your netcat
```

Then, in the prefix:

```
wine WineProbe.exe
```

`ffmpeg.exe` isn't next to the published binary, so point the last check at the copy inside the
installed plugin folder:

```
wine WineProbe.exe ffmpeg 'C:\path\to\devPlugins\ScreenShare\ffmpeg\ffmpeg.exe'
```

Exits non-zero if any check fails, so it can gate the work that depends on it.

## What the results mean

- **All three pass** — the architecture holds; proceed with the helper daemon.
- **`connect` fails with a listener genuinely running** — stop and redesign. Nothing else in the
  Linux plan matters until this works.
- **`ffmpeg` fails** — the viewer path needs rethinking, but streaming (which encodes on the Linux
  side, outside the prefix) is unaffected.
- **`wine` reports "NOT running under Wine"** — you're on real Windows, which is the expected
  result on a dev box. The other checks still run but prove nothing about Wine.
