namespace ScreenShare.Plugin.Networking;

public sealed record CreateRoomRequest(string Name, bool IsPublic, string? Password, int MaxViewers);

public sealed record CreateRoomResult(bool Success, string? RoomId, string? HostToken, string? InstanceId, string? Error, string? RoomName = null);

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

public sealed record RoomDirectory(IReadOnlyList<RoomListing> Rooms, IReadOnlyList<string> BannedRoomIds);

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
