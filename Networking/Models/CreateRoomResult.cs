namespace ScreenShare.Plugin.Networking.Models;

public sealed record CreateRoomResult(bool Success, string? RoomId, string? HostToken, string? InstanceId, string? Error, string? RoomName = null, string? RedirectUrl = null);
