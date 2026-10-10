namespace ScreenShare.Plugin.Networking.Models;

public sealed record JoinRoomResult(
    bool Success,
    string? InstanceId,
    string? RedirectUrl,
    string? Error,
    string? RoomName = null,
    bool IsPublic = false,
    bool HasPassword = false,
    string? KeySalt = null);
