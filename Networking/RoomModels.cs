namespace ScreenShare.Plugin.Networking;

// Mirrors ScreenShare.Relay.Hubs.RoomModels - SignalR (de)serializes by property name via JSON,
// so this doesn't need to reference the relay project directly.
public sealed record CreateRoomResult(bool Success, string? RoomId, string? HostToken, string? InstanceId, string? Error);

/// <summary><see cref="RedirectUrl"/> is set when this relay wasn't the one that actually owns the
/// room (multi-instance/Redis mode only) - the caller is expected to reconnect its SignalR
/// connection to that URL and retry JoinRoom there. RoomHubClient.JoinRoomAsync handles this
/// automatically (reconnects and retries once) rather than pushing it onto every call site.</summary>
public sealed record JoinRoomResult(bool Success, string? InstanceId, string? RedirectUrl, string? Error);

public sealed record ActiveStreamerInfo(string ConnectionId, string DisplayName);

public sealed record RoomMemberInfo(string ConnectionId, string DisplayName, bool IsHost, bool CanStream);

/// <summary>Response from the relay's GET /rooms/{roomId}/location endpoint - lets a client
/// discover which instance owns a room before opening a SignalR connection to it. Currently only
/// used indirectly (JoinRoomResult.RedirectUrl handles routing reactively), kept for symmetry with
/// the relay's own contract and as the pre-flight check a future optimization could use to avoid
/// the extra round trip a reactive redirect costs.</summary>
public sealed record RoomLocationResult(bool Found, string? InstanceId, string? RedirectUrl);
