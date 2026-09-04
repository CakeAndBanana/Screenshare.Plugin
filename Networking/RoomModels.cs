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

/// <summary>Broadcast payload for "who's watching whom in this room" - mirrors ActiveStreamerInfo.</summary>
public sealed record WatcherInfo(string WatcherConnectionId, string StreamerConnectionId);

public sealed record RoomMemberInfo(string ConnectionId, string DisplayName, bool IsHost, bool CanStream);

/// <summary>Response from the relay's GET /rooms/{roomId}/location endpoint - lets a client
/// discover which instance owns a room before opening a SignalR connection to it. Currently only
/// used indirectly (JoinRoomResult.RedirectUrl handles routing reactively), kept for symmetry with
/// the relay's own contract and as the pre-flight check a future optimization could use to avoid
/// the extra round trip a reactive redirect costs.</summary>
public sealed record RoomLocationResult(bool Found, string? InstanceId, string? RedirectUrl);

/// <summary>Mirrors ScreenShare.Relay.Hubs.ScreenPlacementInfo - where the room's shared in-world
/// screen sits, and how its virtual speakers fall off.
///
/// Height is deliberately absent: it's derived from <see cref="WidthMeters"/> and the decoded
/// stream's own aspect ratio (see WorldScreenController), so the panel can never stretch the video
/// when a streamer switches resolution mid-session.
///
/// <see cref="TerritoryType"/> scopes this to the zone it was placed in - and only to the zone:
/// two houses of the same type share a territory id, so a placement made in one also renders in
/// another of the same kind. <see cref="Version"/> is a unix-ms stamp used to drop a broadcast
/// that lost a race against a newer local edit.</summary>
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
