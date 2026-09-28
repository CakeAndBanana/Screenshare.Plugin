namespace ScreenShare.Plugin.Networking.Models;

public sealed record RoomLocationResult(bool Found, string? InstanceId, string? RedirectUrl);
