namespace ScreenShare.Plugin.Networking;

/// <param name="Region">A region code to place the room in, or null for Automatic. Older relays ignore it.</param>
/// <param name="PreferredRegion">For Automatic, the player's own region: tried first, other regions only when it's full. Older relays ignore it.</param>
public sealed record CreateRoomRequest(string Name, bool IsPublic, string? Password, int MaxViewers, string? Region = null, string? PreferredRegion = null);

public sealed record CreateRoomResult(bool Success, string? RoomId, string? HostToken, string? InstanceId, string? Error, string? RoomName = null, string? RedirectUrl = null);

public sealed record JoinRoomResult(
    bool Success,
    string? InstanceId,
    string? RedirectUrl,
    string? Error,
    string? RoomName = null,
    bool IsPublic = false,
    bool HasPassword = false);

public sealed record ActiveStreamerInfo(string ConnectionId, string DisplayName);

public sealed record WatcherInfo(string WatcherConnectionId, string StreamerConnectionId);

public sealed record RoomMemberInfo(string ConnectionId, string DisplayName, bool IsHost, bool CanStream, string UserId);

public sealed record RoomListing(
    string RoomId,
    string Name,
    bool IsPublic,
    bool HasPassword,
    string HostName,
    IReadOnlyList<string> MemberNames,
    IReadOnlyList<string> StreamerNames,
    int MaxViewers,
    DateTime CreatedUtc);

/// <param name="Regions">Regions a new room can be placed in; null from relays that predate regions.</param>
public sealed record RoomDirectory(IReadOnlyList<RoomListing> Rooms, IReadOnlyList<string> BannedRoomIds, IReadOnlyList<string>? Regions = null);

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
