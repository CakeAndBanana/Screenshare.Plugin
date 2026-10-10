using ScreenShare.Plugin.Networking.Models;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Tests.Services;

public class ScreenPresetStoreTests
{
    private static ScreenPlacementInfo Placement(uint territory = 345, float x = 10f) => new(
        TerritoryType: territory,
        X: x,
        Y: 2f,
        Z: -30f,
        Yaw: 1.2f,
        Pitch: -0.3f,
        WidthMeters: 8f,
        SpeakerSpread: 2.5f,
        FalloffStart: 6f,
        FalloffEnd: 30f,
        Version: 1);

    [Fact]
    public void ToPlacement_KeepsEveryFieldAndStampsAFreshVersion()
    {
        ScreenPlacementInfo original = Placement() with { WorldId = 73 };

        ScreenPlacementInfo restored = ScreenPreset.From("Stage", original).ToPlacement(73);

        Assert.Equal(original with { Version = restored.Version }, restored);
        Assert.True(restored.Version > original.Version);
    }

    [Fact]
    public void Save_SameNameInSameZone_Overwrites()
    {
        var presets = new List<ScreenPreset>();

        ScreenPresetStore.Save(presets, "Stage", Placement(x: 1f));
        ScreenPresetStore.Save(presets, " stage ", Placement(x: 2f));

        ScreenPreset preset = Assert.Single(presets);
        Assert.Equal("stage", preset.Name);
        Assert.Equal(2f, preset.X);
    }

    [Fact]
    public void Save_SameNameInOtherZone_AddsSeparatePreset()
    {
        var presets = new List<ScreenPreset>();

        ScreenPresetStore.Save(presets, "Stage", Placement(territory: 345));
        ScreenPresetStore.Save(presets, "Stage", Placement(territory: 346));

        Assert.Equal(2, presets.Count);
    }

    [Fact]
    public void Save_BlankName_IsIgnored()
    {
        var presets = new List<ScreenPreset>();

        Assert.Null(ScreenPresetStore.Save(presets, "   ", Placement()));
        Assert.Empty(presets);
    }

    [Fact]
    public void ForTerritory_FiltersByZoneAndSortsByName()
    {
        var presets = new List<ScreenPreset>();
        ScreenPresetStore.Save(presets, "bar", Placement(territory: 345));
        ScreenPresetStore.Save(presets, "Zeta", Placement(territory: 345));
        ScreenPresetStore.Save(presets, "Alpha", Placement(territory: 345));
        ScreenPresetStore.Save(presets, "Elsewhere", Placement(territory: 999));

        IReadOnlyList<ScreenPreset> inZone = ScreenPresetStore.ForTerritory(presets, 345);

        Assert.Equal(["Alpha", "bar", "Zeta"], inZone.Select(p => p.Name));
    }

    [Fact]
    public void Delete_RemovesOnlyThatPreset()
    {
        var presets = new List<ScreenPreset>();
        ScreenPreset keep = ScreenPresetStore.Save(presets, "Keep", Placement())!;
        ScreenPreset drop = ScreenPresetStore.Save(presets, "Drop", Placement())!;

        Assert.True(ScreenPresetStore.Delete(presets, drop));

        Assert.Same(keep, Assert.Single(presets));
    }
}
