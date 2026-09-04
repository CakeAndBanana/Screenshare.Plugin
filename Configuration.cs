using Dalamud.Configuration;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string RelayUrl { get; set; } = "http://localhost:5178/hubs/room";

    /// <summary>Self-asserted, locally-persisted identity sent to the relay on CreateRoom/JoinRoom
    /// - separate from the SignalR ConnectionId (which changes on every reconnect), this is what
    /// lets a kick actually stick even after the kicked person reconnects. The default initializer
    /// generates a value on both a brand-new install AND an existing config missing this field
    /// after a partial JSON deserialize - Plugin.cs then saves it unconditionally on load so it
    /// actually reaches disk rather than being silently regenerated every session.</summary>
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "Guest";

    // Streaming settings - persisted so they don't reset to defaults every plugin reload/game
    // restart. Defaults match ShareWindow's original hardcoded field initializers.
    public int VideoResolutionIndex { get; set; } = 1; // 720p

    public int VideoBitrateKbps { get; set; } = 1500;

    public int VideoFpsTarget { get; set; } = 30;

    public int VideoCodecIndex { get; set; } // H264

    public int AudioBitrateKbps { get; set; } = 32;

    /// <summary>Whether starting a stream should also start audio capture, using whichever audio
    /// source is currently selected (system-wide, or one process - see
    /// <see cref="MonitorIndex"/>'s neighbours in SettingsWindow). Audio started this way is
    /// stopped again when streaming stops; audio the user turned on by hand is left running,
    /// since that was an explicit choice rather than a side effect of streaming.</summary>
    public bool AutoStartAudioWithStream { get; set; } = true;

    /// <summary>Which DXGI output (monitor) to capture, indexed the same way DesktopDuplicationCapture's
    /// own outputIndex parameter is - 0 is whichever monitor Windows currently treats as having the
    /// desktop primary.</summary>
    public int MonitorIndex { get; set; }

    /// <summary>true = capture one specific window (WindowCapture) instead of a whole monitor
    /// (DesktopDuplicationCapture). The specific window itself isn't persisted - a window handle
    /// only means anything within the process that opened it, so there's nothing meaningful to
    /// restore across a session; only the mode preference is remembered.</summary>
    public bool CaptureWindowMode { get; set; }

    // In-world screen ("beamer"). Every field here is new with a default, so a config written
    // before this feature existed deserializes unchanged and Version stays at 1 - there's nothing
    // to migrate, only fields to start absent.

    /// <summary>Whether the stream being watched is projected onto a panel in the game world in
    /// addition to the popout window. Per-client, not part of the room's shared placement: whether
    /// you want to see it is your business, where it hangs is the room's.</summary>
    public bool ProjectInWorld { get; set; }

    public float WorldScreenOpacity { get; set; } = 1f;

    /// <summary>Whether the panel is hidden behind world geometry it's actually behind. Turning
    /// this off falls back to an unoccluded overlay, which is also the automatic behaviour when the
    /// game's depth buffer can't be reached at all - see GameSceneCamera.TryGetSceneDepth.</summary>
    public bool WorldScreenDepthTest { get; set; } = true;

    /// <summary>Which diagnostic view the panel paints instead of the video: 0 off, 1 the game's
    /// depth buffer, 2 the two sides of the occlusion comparison. Not everyday settings so much as
    /// a way to answer, on a machine that isn't this one, whether the depth buffer has usable data
    /// and whether it shares a scale with our own fragment depth.</summary>
    public int WorldScreenDiagnostic { get; set; }

    /// <summary>Playback volume for a stream you're watching, 0-1. Per-viewer and local: it is
    /// nobody else's business how loud you have someone's stream.</summary>
    public float ViewerVolume { get; set; } = 1f;

    /// <summary>Milliseconds to hold video back so it lines up with audio. Audio is the master
    /// clock (see SyncedPlaybackBuffer), so this shifts the clock video is reconciled against
    /// rather than delaying anything: positive values push video later, for when audio sounds late
    /// against the picture. The residual offset depends on the output device's own latency, which
    /// varies per machine, so it's a setting rather than a constant.</summary>
    public int AvSyncOffsetMs { get; set; }

    /// <summary>Draw the panel inside the game's render pass rather than on top of it, so the
    /// game's HUD covers the screen instead of the other way round. On by default now that it's
    /// confirmed working; it needs a hook into the game's renderer, which is the most patch-fragile
    /// thing here, so the ImGui path stays as an automatic fallback and this stays a visible
    /// toggle.</summary>
    public bool WorldScreenDrawUnderUi { get; set; } = true;

    /// <summary>Which back-buffer target switch the in-scene draw happens after. Depends on the
    /// game's pass structure, so it was found by trying them against a running game rather than
    /// derived: 4 is the first one that lands after the scene is composited and before the UI is
    /// drawn. A game patch that reshapes the render passes would change it - the symptom would be
    /// the panel vanishing or drawing over the HUD again, not a crash.</summary>
    public int WorldScreenHookOccurrence { get; set; } = 4;

    /// <summary>Escape hatch for the scene's depth convention, which decides BOTH how our own
    /// fragment depth is converted and which way the occlusion comparison runs - they are two
    /// consequences of one fact, not independent settings.
    ///
    /// Default (false) means reversed Z: near maps to 1, far to 0. That was confirmed by eye
    /// against a live depth buffer, and it is what modern engines do. Note it disagrees with
    /// Render.Camera.StandardZ, which reported standard Z on the same machine - so that flag is
    /// either mislabelled or means something other than what its name suggests, and is reported in
    /// the UI for information rather than used to drive behaviour.</summary>
    public bool WorldScreenStandardDepth { get; set; }

    /// <summary>Whether playback is panned and attenuated by the virtual speakers flanking the
    /// in-world screen. Off means plain stereo at full volume, exactly as before this existed.</summary>
    public bool SpatialAudio { get; set; } = true;

    /// <summary>Fallback placement used when there's no room-supplied one - watching outside a
    /// room, or in a room where nobody has placed the screen yet. The room's placement always wins
    /// when it exists, so this can't fight it.</summary>
    public ScreenPlacementInfo? LocalScreenPlacement { get; set; }
}
