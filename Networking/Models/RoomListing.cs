namespace ScreenShare.Plugin.Networking.Models;

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
