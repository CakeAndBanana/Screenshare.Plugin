using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Services;

/// <summary>
/// Named screen placements kept in the local config (<see cref="Configuration.ScreenPresets"/>).
/// Callers save the config after a change.
/// </summary>
public static class ScreenPresetStore
{
    public static IReadOnlyList<ScreenPreset> ForTerritory(IEnumerable<ScreenPreset> presets, uint territoryType) =>
        [.. presets
            .Where(p => p.TerritoryType == territoryType)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    public static ScreenPreset? Find(IEnumerable<ScreenPreset> presets, uint territoryType, string name)
    {
        string trimmed = name.Trim();
        return presets.FirstOrDefault(p =>
            p.TerritoryType == territoryType && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a preset, or overwrites the one with the same name in the same zone.</summary>
    public static ScreenPreset? Save(List<ScreenPreset> presets, string name, ScreenPlacementInfo placement)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            return null;

        var preset = ScreenPreset.From(trimmed, placement);
        int existing = presets.FindIndex(p =>
            p.TerritoryType == placement.TerritoryType && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
            presets[existing] = preset;
        else
            presets.Add(preset);

        return preset;
    }

    public static bool Delete(List<ScreenPreset> presets, ScreenPreset preset) => presets.Remove(preset);
}
