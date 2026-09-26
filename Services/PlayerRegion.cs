namespace ScreenShare.Plugin.Services;

internal static class PlayerRegion
{
    /// <summary>The relay region code of the logged-in character's home data center, or null when no
    /// character is loaded or its region has no code. Call on the framework thread.</summary>
    public static string? Current()
    {
        if (ServiceCollector.Objects.LocalPlayer is not { } player || !player.HomeWorld.IsValid)
            return null;

        return FromDataCenterRegion(player.HomeWorld.Value.DataCenter.ValueNullable?.Region.RowId ?? 0);
    }

    /// <summary>Maps a WorldRegionGroup row to the codes relays are configured with (RELAY_REGION).</summary>
    public static string? FromDataCenterRegion(uint region) => region switch
    {
        1 => "jp",
        2 => "na",
        3 => "eu",
        4 => "oce",
        _ => null,
    };
}
