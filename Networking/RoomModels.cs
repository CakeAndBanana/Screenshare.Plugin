namespace ScreenShare.Plugin.Networking;

// Mirrors ScreenShare.Relay.Hubs.RoomModels - SignalR (de)serializes by property name via JSON,
// so this doesn't need to reference the relay project directly.
public sealed record CreateRoomResult(bool Success, string? RoomId, string? HostToken, string? Error);

public sealed record JoinRoomResult(bool Success, string? Error);

public sealed record ActiveStreamerInfo(string ConnectionId, string DisplayName);

public sealed record RoomMemberInfo(string ConnectionId, string DisplayName, bool IsHost, bool CanStream);
