namespace ScreenShare.Plugin.Networking;

// Mirrors ScreenShare.Relay.Hubs.RoomModels - SignalR (de)serializes by property name via JSON,
// so this doesn't need to reference the relay project directly.
public sealed record CreateRoomResult(bool Success, string? RoomId, string? HostToken, string? InstanceId, string? Error);

public sealed record JoinRoomResult(bool Success, string? InstanceId, string? RedirectUrl, string? Error);

public sealed record ActiveStreamerInfo(string ConnectionId, string DisplayName);

public sealed record WatcherInfo(string WatcherConnectionId, string StreamerConnectionId);

public sealed record RoomMemberInfo(string ConnectionId, string DisplayName, bool IsHost, bool CanStream);

public sealed record RoomLocationResult(bool Found, string? InstanceId, string? RedirectUrl);

public sealed record ScreenPlacementInfo(
    uint TerritoryType,
    float X,
    float Y,
    float Z,
    float Yaw,
    float Pitch,
    float WidthMeters,
    float SpeakerSpread,
    float FalloffStart,
    float FalloffEnd,
    long Version);
