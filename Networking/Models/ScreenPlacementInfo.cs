namespace ScreenShare.Plugin.Networking.Models;

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
    long Version,
    uint WorldId = 0);
