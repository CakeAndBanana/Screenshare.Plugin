using Dalamud.Configuration;

namespace ScreenShare.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public const string LegacyDefaultRelayUrl = "http://localhost:5178/hubs/room";

    public int Version { get; set; } = 2;

    public string RelayUrl { get; set; } = string.Empty;

    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = string.Empty;

    public int VideoResolutionIndex { get; set; } = 1;

    public int VideoBitrateKbps { get; set; } = 1500;

    public int VideoFpsTarget { get; set; } = 30;

    public int VideoCodecIndex { get; set; }

    public int AudioBitrateKbps { get; set; } = 32;

    public bool AutoStartAudioWithStream { get; set; } = true;

    public bool MatchAudioToVideoSource { get; set; } = true;

    public int MonitorIndex { get; set; }

    public bool CaptureWindowMode { get; set; }

    public float WorldScreenOpacity { get; set; } = 1f;


    public int WorldScreenDiagnostic { get; set; }

    public List<RecentRoom> RecentRooms { get; set; } = [];

    public bool RememberRoomPasswords { get; set; }

    public int PlaybackLatencyMs { get; set; } = 500;

    public float ViewerVolume { get; set; } = 1f;

    public int AvSyncOffsetMs { get; set; }

    public bool WorldScreenDrawUnderUi { get; set; } = true;


    public bool WorldScreenStandardDepth { get; set; }

    public bool SpatialAudio { get; set; } = true;

    public bool AutoWatchStreams { get; set; }

    public bool ShowWorldScreen { get; set; } = true;

    public bool Migrate()
    {
        if (Version >= 2)
            return false;

        if (string.Equals(RelayUrl, LegacyDefaultRelayUrl, StringComparison.OrdinalIgnoreCase))
            RelayUrl = string.Empty;

        Version = 2;
        return true;
    }
}

public sealed class RecentRoom
{
    public string RelayUrl { get; set; } = string.Empty;

    public string RoomId { get; set; } = string.Empty;

    public string? Name { get; set; }

    public bool IsPublic { get; set; }

    public bool HasPassword { get; set; } = true;

    public string? Password { get; set; }

    public bool WasHost { get; set; }

    public DateTime LastJoinedUtc { get; set; }
}
