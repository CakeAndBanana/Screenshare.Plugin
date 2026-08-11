using Dalamud.Configuration;

namespace ScreenShare.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string RelayUrl { get; set; } = "http://localhost:5178/hubs/room";

    /// <summary>Self-asserted, locally-persisted identity sent to the relay on CreateRoom/JoinRoom
    /// - separate from the SignalR ConnectionId (which changes on every reconnect), this is what
    /// lets a kick actually stick even after the kicked person reconnects. The default initializer
    /// generates a value on both a brand-new install AND an existing config missing this field
    /// after a partial JSON deserialize - Plugin.cs then saves it unconditionally on load so it
    /// actually reaches disk rather than being silently regenerated every session.</summary>
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = "Guest";

    // Streaming settings - persisted so they don't reset to defaults every plugin reload/game
    // restart. Defaults match ShareWindow's original hardcoded field initializers.
    public int VideoResolutionIndex { get; set; } = 1; // 720p

    public int VideoBitrateKbps { get; set; } = 1500;

    public int VideoFpsTarget { get; set; } = 30;

    public int VideoCodecIndex { get; set; } // H264

    public int AudioBitrateKbps { get; set; } = 32;

    /// <summary>Which DXGI output (monitor) to capture, indexed the same way DesktopDuplicationCapture's
    /// own outputIndex parameter is - 0 is whichever monitor Windows currently treats as having the
    /// desktop primary.</summary>
    public int MonitorIndex { get; set; }

    /// <summary>true = capture one specific window (WindowCapture) instead of a whole monitor
    /// (DesktopDuplicationCapture). The specific window itself isn't persisted - a window handle
    /// only means anything within the process that opened it, so there's nothing meaningful to
    /// restore across a session; only the mode preference is remembered.</summary>
    public bool CaptureWindowMode { get; set; }
}
