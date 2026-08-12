using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

/// <summary>
/// Streaming settings (capture source, resolution/bitrate/fps/codec, audio source/bitrate) as
/// their own window, separate from the room UI - previously a collapsed section inside
/// ShareWindow's room screen, which meant it was only reachable while already in a room. Moved out
/// so it's reachable regardless of connection/room state, and so it can be registered as the
/// plugin's actual config UI (see Plugin.cs's UiBuilder.OpenConfigUi wiring - Dalamud's plugin
/// installer flags a plugin that never registers one).
///
/// Deliberately does NOT own display name / relay URL - those stay on ShareWindow's Connect screen,
/// since they're tied directly to the Connect action rather than being "streaming settings" in the
/// sense the room screen's collapsed section used to mean.
///
/// ShareWindow still owns the actual capture/audio session objects and starts streaming/watching
/// with whatever this window's properties currently hold - this class only owns the UI state and
/// picker enumeration, not the session lifecycle. The three constructor delegates are the only
/// coupling back to ShareWindow: whether a capture/audio session is currently running (to disable
/// the relevant pickers, matching the "changes apply next time you start" pattern used throughout
/// this plugin) and how to apply a live audio bitrate change to an already-running capture.
/// </summary>
public sealed class SettingsWindow : Window, IDisposable
{
    private readonly Func<bool> isStreaming;
    private readonly Func<bool> isAudioCapturing;
    private readonly Action<int> applyLiveAudioBitrate;

    // Monitor enumeration needs a live D3D11 device on the same adapter DesktopDuplicationCapture
    // will actually use, so indices line up - but this is a SEPARATE device instance from
    // ShareWindow's own (which is used for real capture/playback), since this window can be open
    // independent of any active session. Creating more than one D3D11 device against the same
    // adapter is normal/supported; this one exists purely for enumeration and is thrown away and
    // recreated on a device-removed error rather than needing the full recovery dance ShareWindow's
    // capture/playback device does.
    private GameAdapterDevice? enumerationDevice;

    private int monitorIndex = ServiceCollector.Config.MonitorIndex;
    private List<(int Index, string Label)> monitorCandidates = [];

    private bool captureWindowMode = ServiceCollector.Config.CaptureWindowMode;
    private nint selectedWindowHandle;
    private List<(nint Handle, string Label)> windowCandidates = [];
    private DateTime lastWindowListRefresh = DateTime.MinValue;

    private int audioBitrateKbps = ServiceCollector.Config.AudioBitrateKbps;
    private int? audioSourceProcessId;
    private List<(int Pid, string Label)> audioSourceCandidates = [];
    private DateTime lastAudioSourceRefresh = DateTime.MinValue;

    private static readonly string[] VideoResolutionLabels = ["480p", "720p", "1080p", "Native"];
    private static readonly int[] VideoResolutionHeights = [480, 720, 1080, int.MaxValue];
    private int videoResolutionIndex = ServiceCollector.Config.VideoResolutionIndex;
    private int videoBitrateKbps = ServiceCollector.Config.VideoBitrateKbps;
    private int videoFpsTarget = ServiceCollector.Config.VideoFpsTarget;
    private static readonly string[] VideoCodecLabels = ["H.264", "HEVC (H.265)"];
    private int videoCodecIndex = ServiceCollector.Config.VideoCodecIndex;

    public int MonitorIndex => monitorIndex;

    public bool CaptureWindowMode => captureWindowMode;

    public nint SelectedWindowHandle => selectedWindowHandle;

    public int TargetHeight => VideoResolutionHeights[videoResolutionIndex];

    public int VideoBitrateKbps => videoBitrateKbps;

    public int VideoFpsTarget => videoFpsTarget;

    public VideoCodec Codec => (VideoCodec)videoCodecIndex;

    public int AudioBitrateKbps => audioBitrateKbps;

    public int? AudioSourceProcessId => audioSourceProcessId;

    public SettingsWindow(Func<bool> isStreaming, Func<bool> isAudioCapturing, Action<int> applyLiveAudioBitrate)
        : base("Screen Share Settings###ScreenShareSettingsWindow")
    {
        this.isStreaming = isStreaming;
        this.isAudioCapturing = isAudioCapturing;
        this.applyLiveAudioBitrate = applyLiveAudioBitrate;
        Size = new Vector2(420, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Number fields use InputInt (type a value, or use the +/- buttons) rather than
    /// sliders - a drag bar was hard to land on an exact value with. Each one saves to disk via
    /// <see cref="IsItemDeactivatedAfterEdit"/> so typing doesn't write on every keystroke, only
    /// once you tab/click away with a finished value.</summary>
    public override void Draw()
    {
        DrawCaptureModePicker();

        ImGui.SetNextItemWidth(100);
        ImGui.Combo("Resolution", ref videoResolutionIndex, VideoResolutionLabels, VideoResolutionLabels.Length);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveStreamingSettings();

        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Video bitrate (kbps)", ref videoBitrateKbps, 100, 500))
            videoBitrateKbps = Math.Clamp(videoBitrateKbps, 500, 6000);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveStreamingSettings();

        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Target FPS", ref videoFpsTarget, 1, 5))
            videoFpsTarget = Math.Clamp(videoFpsTarget, 5, 60);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveStreamingSettings();

        ImGui.SetNextItemWidth(150);
        ImGui.Combo("Codec", ref videoCodecIndex, VideoCodecLabels, VideoCodecLabels.Length);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveStreamingSettings();

        ImGui.SetNextItemWidth(150);
        if (ImGui.InputInt("Audio bitrate (kbps)", ref audioBitrateKbps, 1, 8))
        {
            audioBitrateKbps = Math.Clamp(audioBitrateKbps, 8, 64);
            if (isAudioCapturing())
                applyLiveAudioBitrate(audioBitrateKbps * 1000);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveStreamingSettings();

        DrawAudioSourcePicker();

        if (isStreaming())
            ImGui.TextUnformatted("(resolution/bitrate/fps/codec changes apply next time you start streaming)");
    }

    /// <summary>Lets audio capture target one specific process (and its child-process tree - the
    /// actual audio in a browser usually renders in a child process, not the main one) instead of
    /// the whole system mix. Disabled while capture is already running - switching source means
    /// stopping and starting again, same as changing video settings mid-stream.</summary>
    private void DrawAudioSourcePicker()
    {
        string currentLabel = "System audio (all)";
        if (audioSourceProcessId is { } selectedPid)
        {
            currentLabel = audioSourceCandidates.Find(c => c.Pid == selectedPid).Label
                ?? $"PID {selectedPid} (no longer running)";
        }

        ImGui.BeginDisabled(isAudioCapturing());
        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo("Audio source", currentLabel))
        {
            RefreshAudioSourceCandidates();

            if (ImGui.Selectable("System audio (all)", audioSourceProcessId is null))
                audioSourceProcessId = null;

            foreach ((int pid, string label) in audioSourceCandidates)
            {
                if (ImGui.Selectable(label, audioSourceProcessId == pid))
                    audioSourceProcessId = pid;
            }

            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        if (isAudioCapturing())
            ImGui.TextUnformatted("(stop audio capture to change source)");
    }

    /// <summary>Throttled - process enumeration is cheap but not free, and this only needs to be
    /// fresh while the picker is actually open.</summary>
    private void RefreshAudioSourceCandidates()
    {
        if (DateTime.UtcNow - lastAudioSourceRefresh < TimeSpan.FromSeconds(2))
            return;
        lastAudioSourceRefresh = DateTime.UtcNow;

        var candidates = new List<(int Pid, string Label)>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string title = process.MainWindowTitle;
                if (!string.IsNullOrWhiteSpace(title))
                    candidates.Add((process.Id, $"{process.ProcessName} - {title}"));
            }
            catch
            {
                // Some processes (protected/elevated/etc.) throw on property access - just skip.
            }
            finally
            {
                process.Dispose();
            }
        }

        candidates.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        audioSourceCandidates = candidates;
    }

    /// <summary>Switches between capturing a whole monitor and capturing one specific window.
    /// Disabled while already streaming, same as every picker here - switching means stop and
    /// start again.</summary>
    private void DrawCaptureModePicker()
    {
        ImGui.BeginDisabled(isStreaming());
        bool changed = false;
        if (ImGui.RadioButton("Monitor##captureModeMonitor", !captureWindowMode))
        {
            captureWindowMode = false;
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Window##captureModeWindow", captureWindowMode))
        {
            captureWindowMode = true;
            changed = true;
        }
        ImGui.EndDisabled();

        if (changed)
            SaveStreamingSettings();

        if (isStreaming())
            ImGui.TextUnformatted("(stop streaming to change capture mode)");

        if (captureWindowMode)
            DrawWindowPicker();
        else
            DrawMonitorPicker();
    }

    /// <summary>Lets a host pick one specific open window to share instead of a whole monitor -
    /// useful when you only want to show a browser or a single app, not your whole desktop.
    /// Disabled while already streaming, same as the monitor picker.</summary>
    private void DrawWindowPicker()
    {
        string currentLabel = selectedWindowHandle == 0
            ? "(no window selected)"
            : windowCandidates.Find(c => c.Handle == selectedWindowHandle).Label ?? "(window no longer open)";

        ImGui.BeginDisabled(isStreaming());
        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo("Window", currentLabel))
        {
            RefreshWindowCandidates();

            foreach ((nint handle, string label) in windowCandidates)
            {
                if (ImGui.Selectable(label, selectedWindowHandle == handle))
                    selectedWindowHandle = handle;
            }

            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        if (isStreaming())
            ImGui.TextUnformatted("(stop streaming to change window)");
    }

    /// <summary>Throttled the same way the audio source picker's process list is - cheap, but not
    /// so cheap it should re-enumerate every frame while the dropdown happens to be open.</summary>
    private void RefreshWindowCandidates()
    {
        if (DateTime.UtcNow - lastWindowListRefresh < TimeSpan.FromSeconds(2))
            return;
        lastWindowListRefresh = DateTime.UtcNow;

        var candidates = new List<(nint Handle, string Label)>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string title = process.MainWindowTitle;
                nint handle = process.MainWindowHandle;
                if (!string.IsNullOrWhiteSpace(title) && handle != 0)
                    candidates.Add((handle, $"{process.ProcessName} - {title}"));
            }
            catch
            {
                // Some processes (protected/elevated/etc.) throw on property access - just skip.
            }
            finally
            {
                process.Dispose();
            }
        }

        candidates.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        windowCandidates = candidates;
    }

    /// <summary>Lets a host with multiple monitors pick which one to share instead of always
    /// capturing whichever Windows currently treats as the desktop primary. Disabled while
    /// already streaming - matches every other streaming setting here (changes apply next time
    /// you start).</summary>
    private void DrawMonitorPicker()
    {
        string currentLabel = monitorCandidates.Find(c => c.Index == monitorIndex).Label
            ?? $"Monitor {monitorIndex + 1}";

        ImGui.BeginDisabled(isStreaming());
        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo("Monitor", currentLabel))
        {
            RefreshMonitorCandidates();

            foreach ((int index, string label) in monitorCandidates)
            {
                if (ImGui.Selectable(label, monitorIndex == index))
                {
                    monitorIndex = index;
                    SaveStreamingSettings();
                }
            }

            ImGui.EndCombo();
        }
        ImGui.EndDisabled();

        if (isStreaming())
            ImGui.TextUnformatted("(stop streaming to change monitor)");
    }

    /// <summary>Outputs are enumerated from the same D3D11 adapter DesktopDuplicationCapture will
    /// actually use (via the lazily-created <see cref="enumerationDevice"/>), so the indices shown
    /// here line up exactly with the outputIndex it's given. The label prefers the monitor's real
    /// friendly name (e.g. "Dell U2720Q") over DXGI's own DeviceName, which is only ever a cryptic
    /// "\\.\DISPLAYn" path - see MonitorNameHelper for how that's resolved. Falls back to the raw
    /// DeviceName if the lookup can't find (or fails to find at all) a friendly name for it.</summary>
    private void RefreshMonitorCandidates()
    {
        try
        {
            enumerationDevice ??= new GameAdapterDevice(ServiceCollector.PI.UiBuilder);
            using SharpDX.DXGI.Device dxgiDevice = enumerationDevice.Device.QueryInterface<SharpDX.DXGI.Device>();
            using SharpDX.DXGI.Adapter adapter = dxgiDevice.Adapter;

            Dictionary<string, string> friendlyNames = MonitorNameHelper.TryGetFriendlyNames();

            var candidates = new List<(int Index, string Label)>();
            SharpDX.DXGI.Output[] outputs = adapter.Outputs;
            for (int i = 0; i < outputs.Length; i++)
            {
                using SharpDX.DXGI.Output output = outputs[i];
                SharpDX.DXGI.OutputDescription description = output.Description;
                int width = description.DesktopBounds.Right - description.DesktopBounds.Left;
                int height = description.DesktopBounds.Bottom - description.DesktopBounds.Top;
                string name = friendlyNames.GetValueOrDefault(description.DeviceName, description.DeviceName);
                candidates.Add((i, $"Monitor {i + 1} - {name} ({width}x{height})"));
            }

            monitorCandidates = candidates;
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Error(ex, "ScreenShare settings failed to enumerate monitors");

            // No need for the full DeviceRemovedReason/shared-recovery dance ShareWindow's real
            // capture/playback device does - this device only ever exists for enumeration, so
            // just drop it and let the next attempt recreate it fresh.
            enumerationDevice?.Dispose();
            enumerationDevice = null;
        }
    }

    private void SaveStreamingSettings()
    {
        ServiceCollector.Config.VideoResolutionIndex = videoResolutionIndex;
        ServiceCollector.Config.VideoBitrateKbps = videoBitrateKbps;
        ServiceCollector.Config.VideoFpsTarget = videoFpsTarget;
        ServiceCollector.Config.VideoCodecIndex = videoCodecIndex;
        ServiceCollector.Config.AudioBitrateKbps = audioBitrateKbps;
        ServiceCollector.Config.MonitorIndex = monitorIndex;
        ServiceCollector.Config.CaptureWindowMode = captureWindowMode;
        ServiceCollector.PI.SavePluginConfig(ServiceCollector.Config);
    }

    public void Dispose()
    {
        enumerationDevice?.Dispose();
        enumerationDevice = null;
    }
}
