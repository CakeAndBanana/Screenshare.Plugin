namespace ScreenShare.Plugin.Networking.Models;

/// <param name="Regions">Regions a new room can be placed in; null from relays that predate regions.</param>
/// <param name="RegionProbes">A server in each region to ping for the nearest one; null from relays that predate it.</param>
public sealed record RoomDirectory(IReadOnlyList<RoomListing> Rooms, IReadOnlyList<string> BannedRoomIds, IReadOnlyList<string>? Regions = null, IReadOnlyList<RegionProbe>? RegionProbes = null);

public sealed record RegionProbe(string Region, string Url);
