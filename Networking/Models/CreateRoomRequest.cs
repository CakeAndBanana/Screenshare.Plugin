namespace ScreenShare.Plugin.Networking.Models;

/// <param name="Region">A region code to place the room in, or null for Automatic. Older relays ignore it.</param>
/// <param name="PreferredRegion">For Automatic, the player's own region: tried first, other regions only when it's full. Older relays ignore it.</param>
public sealed record CreateRoomRequest(string Name, bool IsPublic, string? Password, int MaxViewers, string? Region = null, string? PreferredRegion = null);
