using Dalamud.Configuration;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string RelayUrl { get; set; } = "http://localhost:5178/hubs/room";

    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "Guest";

    // Streaming settings - persisted so they don't reset to defaults every plugin reload/game
    // restart. Defaults match ShareWindow's original hardcoded field initializers.
    public int VideoResolutionIndex { get; set; } = 1; // 720p

    public int VideoBitrateKbps { get; set; } = 1500;

    public int VideoFpsTarget { get; set; } = 30;

    public int VideoCodecIndex { get; set; } // H264

    public int AudioBitrateKbps { get; set; } = 32;

    public bool AutoStartAudioWithStream { get; set; } = true;

    public int MonitorIndex { get; set; }

    public bool CaptureWindowMode { get; set; }

    // In-world screen ("beamer"). Every field here is new with a default, so a config written
    // before this feature existed deserializes unchanged and Version stays at 1 - there's nothing
    // to migrate, only fields to start absent.

    public bool ProjectInWorld { get; set; }

    public float WorldScreenOpacity { get; set; } = 1f;

    public bool WorldScreenDepthTest { get; set; } = true;

    public int WorldScreenDiagnostic { get; set; }

    public List<RecentRoom> RecentRooms { get; set; } = [];

    public bool RememberRoomPasswords { get; set; }

    public int PlaybackLatencyMs { get; set; } = 500;

    public float ViewerVolume { get; set; } = 1f;

    public int AvSyncOffsetMs { get; set; }

    public bool WorldScreenDrawUnderUi { get; set; } = true;

    public int WorldScreenHookOccurrence { get; set; } = 4;

    public bool WorldScreenStandardDepth { get; set; }

    public bool SpatialAudio { get; set; } = true;

    public ScreenPlacementInfo? LocalScreenPlacement { get; set; }
}

public sealed class RecentRoom
{
    public string RelayUrl { get; set; } = string.Empty;

    public string RoomId { get; set; } = string.Empty;

    public string? Password { get; set; }

    public bool WasHost { get; set; }

    public DateTime LastJoinedUtc { get; set; }
}
