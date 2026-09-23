# Cake's Screenshare

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that streams your screen or a single window, with its audio, to everyone in a room. People in the room watch it in a pop-out window, or on a screen you place in the game world. Streams travel through a relay server.

## Share your screen your way

1. Create a room (public or private, with an optional password) or join one.
2. In Settings, choose a monitor or a window, plus either all system audio or just one app. Then press **Go live**.
3. Press **Place screen here** to put the screen in the world where you're standing. Everyone in the room sees it in the same spot automatically.

Viewers can also click your name to watch in a pop-out window. The video is encoded on your graphics card where possible, with Media Foundation or libavcodec (NVENC, AMF or QSV).

## Installation

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories** and add:
   ```
   https://raw.githubusercontent.com/CakeAndBanana/Screenshare/main/repo.json
   ```
2. Open `/xlplugins`, search for **ScreenShare** and install it.
3. Open `/screenshare`, enter the relay address you were given and a display name, then connect.

## Building from source

See [BUILDING.md](BUILDING.md).

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
