using Dalamud.Configuration;
using ScreenShare.Plugin.Networking;

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

    public const float MaxViewerVolume = 2f;

    public float ViewerVolume { get; set; } = 1f;

    public int AvSyncOffsetMs { get; set; }

    public bool WorldScreenDrawUnderUi { get; set; } = true;


    public bool WorldScreenStandardDepth { get; set; }

    public bool SpatialAudio { get; set; } = true;

    public bool AutoWatchStreams { get; set; }

    public bool ShowWorldScreen { get; set; } = true;

    public List<ScreenPreset> ScreenPresets { get; set; } = [];

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

public sealed class ScreenPreset
{
    public string Name { get; set; } = string.Empty;

    public uint TerritoryType { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    public float WidthMeters { get; set; }

    public float SpeakerSpread { get; set; }

    public float FalloffStart { get; set; }

    public float FalloffEnd { get; set; }

    public DateTime SavedUtc { get; set; }

    public static ScreenPreset From(string name, ScreenPlacementInfo placement) => new()
    {
        Name = name,
        TerritoryType = placement.TerritoryType,
        X = placement.X,
        Y = placement.Y,
        Z = placement.Z,
        Yaw = placement.Yaw,
        Pitch = placement.Pitch,
        WidthMeters = placement.WidthMeters,
        SpeakerSpread = placement.SpeakerSpread,
        FalloffStart = placement.FalloffStart,
        FalloffEnd = placement.FalloffEnd,
        SavedUtc = DateTime.UtcNow,
    };

    public ScreenPlacementInfo ToPlacement() => new(
        TerritoryType,
        X,
        Y,
        Z,
        Yaw,
        Pitch,
        WidthMeters,
        SpeakerSpread,
        FalloffStart,
        FalloffEnd,
        Version: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
