using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ScreenShare.Plugin.Services;

internal class ServiceCollector
{
    [PluginService] internal static IDalamudPluginInterface PI { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;

    /// <summary>Needed by the in-world screen only: the local player's position/facing to place
    /// the panel, and the current TerritoryType to decide whether a placement made elsewhere
    /// belongs in the zone we're standing in now.</summary>
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    /// <summary>Only for the local player's position and facing, which "Place here" needs. Lives
    /// on IObjectTable rather than IClientState as of Dalamud API 15.</summary>
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;

    /// <summary>Needed only by ScenePanelHook, which hooks the game's render pipeline so the
    /// in-world screen can be drawn underneath the game's own UI.</summary>
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    internal static Configuration Config { get; set; } = null!;
}
