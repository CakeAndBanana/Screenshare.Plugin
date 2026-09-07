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

    [PluginService] internal static IClientState ClientState { get; private set; } = null!;

    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;

    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    internal static Configuration Config { get; set; } = null!;
}
