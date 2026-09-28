namespace ScreenShare.Plugin.Networking.Models;

public sealed record RoomMemberInfo(string ConnectionId, string DisplayName, bool IsHost, bool CanStream, string UserId);
