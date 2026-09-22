using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui.Helpers;

namespace ScreenShare.Plugin.Ui.Views;

public sealed class SettingsWindow : Window, IDisposable
{
    // Named rather than inline with each clamp, because the same numbers are now also the slider
    // bounds - two copies would drift the moment one was changed.
    private const int MinVideoBitrateKbps = 500;
    private const int MaxVideoBitrateKbps = 6000;
    private const int MinVideoFps = 5;
    private const int MaxVideoFps = 60;
    private const int MinAudioBitrateKbps = 8;
    private const int MaxAudioBitrateKbps = 64;

    private readonly Func<bool> isStreaming;
    private readonly Func<bool> isAudioCapturing;
    private readonly Action<int> applyLiveAudioBitrate;
    private readonly Func<bool> canApplyLiveVideoBitrate;
    private readonly Func<int, bool> applyLiveVideoBitrate;

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

    // Enumeration can fail, and it leaves the list empty when it does. Without this, the main
    // window's capture-source line - which enumerates on demand when the list is empty - would
    // build and throw away a D3D11 device every frame, logging an error each time. Opening this
    // window refreshes unconditionally, so a transient failure is still recoverable.
    private bool monitorsEnumerated;

    private bool captureWindowMode = ServiceCollector.Config.CaptureWindowMode;
    private nint selectedWindowHandle;
    private List<(nint Handle, string Label)> windowCandidates = [];
    private DateTime lastWindowListRefresh = DateTime.MinValue;

    // Set when the window opens, consumed by the next Draw. Not done in OnOpen itself: IsOpen is
    // flipped from the plugin installer's gear and from a chat command, neither of which is
    // necessarily the draw thread, and refreshing monitors builds a D3D11 device.
    private bool refreshSourcesOnNextDraw = true;

    private int audioBitrateKbps = ServiceCollector.Config.AudioBitrateKbps;
    private bool autoStartAudioWithStream = ServiceCollector.Config.AutoStartAudioWithStream;
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

    public bool AutoStartAudioWithStream => autoStartAudioWithStream;

    /// <summary>Whether starting a stream right now has something to capture. Monitor mode always
    /// does - index 0 is the default and always exists - so this only ever says no in window mode,
    /// where the pick is not persisted and so starts empty every session.</summary>
    public bool HasCaptureSource => !captureWindowMode || selectedWindowHandle != 0;

    private string MonitorLabel => monitorCandidates.Find(c => c.Index == monitorIndex).Label
        ?? $"Monitor {monitorIndex + 1}";

    private string WindowLabel => selectedWindowHandle == 0
        ? "(no window selected)"
        : windowCandidates.Find(c => c.Handle == selectedWindowHandle).Label ?? "(window no longer open)";

    public SettingsWindow(
        Func<bool> isStreaming,
        Func<bool> isAudioCapturing,
        Action<int> applyLiveAudioBitrate,
        Func<bool> canApplyLiveVideoBitrate,
        Func<int, bool> applyLiveVideoBitrate)
        : base("Screen Share Settings###ScreenShareSettingsWindow")
    {
        this.isStreaming = isStreaming;
        this.isAudioCapturing = isAudioCapturing;
        this.applyLiveAudioBitrate = applyLiveAudioBitrate;
        this.canApplyLiveVideoBitrate = canApplyLiveVideoBitrate;
        this.applyLiveVideoBitrate = applyLiveVideoBitrate;
        Size = new Vector2(460, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>The current capture source in words, for the main window to show next to "Go live"
    /// so nobody has to open this window to find out what they are about to share.</summary>
    public string DescribeCaptureSource()
    {
        if (captureWindowMode)
            return WindowLabel;

        // The main window asks for this before these settings have ever been opened, and the
        // monitor list is only built when something wants it - without this the line would read
        // "Monitor 1" rather than naming the actual display.
        if (!monitorsEnumerated)
            RefreshMonitorCandidates();

        return MonitorLabel;
    }

    public override void OnOpen() => refreshSourcesOnNextDraw = true;

    public override void Draw()
    {
        if (refreshSourcesOnNextDraw)
            RefreshSources();

        // One label column and one field width for the whole window, so every row lines up instead
        // of each control picking its own width.
        float labelWidth = UiTheme.LabelColumn(
            "Mode", "Monitor", "Window", "Resolution", "Video bitrate (kbps)",
            "Target FPS", "Codec", "Audio bitrate (kbps)", "Audio source");
        float fieldWidth = MathF.Max(UiTheme.Px(140), ImGui.GetContentRegionAvail().X - labelWidth);

        UiTheme.SectionLabel("Capture");
        DrawCaptureModePicker(labelWidth, fieldWidth);

        UiTheme.SectionLabel("Video");
        DrawVideoSettings(labelWidth, fieldWidth);

        UiTheme.SectionLabel("Audio");
        DrawAudioSettings(labelWidth, fieldWidth);

        if (isStreaming())
        {
            ImGui.Spacing();
            ImGui.TextDisabled(canApplyLiveVideoBitrate()
                ? "(resolution/fps/codec changes apply next time you start streaming)"
                : "(resolution/bitrate/fps/codec changes apply next time you start streaming)");
        }
    }

    private void RefreshSources()
    {
        refreshSourcesOnNextDraw = false;

        // The throttle exists to keep the per-frame combo-open path cheap. Opening this window, or
        // switching capture mode, is a deliberate act, so it gets a fresh list.
        lastWindowListRefresh = DateTime.MinValue;

        if (captureWindowMode)
            RefreshWindowCandidates();
        else
            RefreshMonitorCandidates();

        // The audio source list stays lazy on purpose: it is a second full sweep of every process
        // on the machine, which would be paid on every open for a picker most people leave alone.
    }

    private void DrawVideoSettings(float labelWidth, float fieldWidth)
    {
        UiTheme.RowLabel("Resolution", labelWidth);
        ImGui.SetNextItemWidth(fieldWidth);
        ImGui.Combo("##resolution", ref videoResolutionIndex, VideoResolutionLabels, VideoResolutionLabels.Length);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveStreamingSettings();

        if (UiTheme.ValueSlider("##videoBitrate", "Video bitrate (kbps)", ref videoBitrateKbps,
                MinVideoBitrateKbps, MaxVideoBitrateKbps, labelWidth).Committed)
        {
            // On release rather than every drag step, unlike audio: each video retune is a real
            // rate-control change in the encoder, not just a number the next packet picks up.
            if (isStreaming())
                applyLiveVideoBitrate(videoBitrateKbps);

            SaveStreamingSettings();
        }

        if (UiTheme.ValueSlider("##videoFps", "Target FPS", ref videoFpsTarget,
                MinVideoFps, MaxVideoFps, labelWidth).Committed)
        {
            SaveStreamingSettings();
        }

        UiTheme.RowLabel("Codec", labelWidth);
        ImGui.SetNextItemWidth(fieldWidth);
        ImGui.Combo("##codec", ref videoCodecIndex, VideoCodecLabels, VideoCodecLabels.Length);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveStreamingSettings();
    }

    private void DrawAudioSettings(float labelWidth, float fieldWidth)
    {
        SliderEdit bitrate = UiTheme.ValueSlider("##audioBitrate", "Audio bitrate (kbps)", ref audioBitrateKbps,
            MinAudioBitrateKbps, MaxAudioBitrateKbps, labelWidth);

        if (bitrate.Changed && isAudioCapturing())
            applyLiveAudioBitrate(audioBitrateKbps * 1000);

        if (bitrate.Committed)
            SaveStreamingSettings();

        DrawAudioSourcePicker(labelWidth, fieldWidth);

        // Deliberately NOT disabled while streaming, unlike the pickers above: this only decides
        // what the NEXT "start streaming" does, so there's nothing about an in-flight stream that
        // toggling it could invalidate.
        if (ImGui.Checkbox("Capture audio automatically when streaming starts", ref autoStartAudioWithStream))
            SaveStreamingSettings();
    }

    private void DrawAudioSourcePicker(float labelWidth, float fieldWidth)
    {
        string currentLabel = "System audio (all)";
        if (audioSourceProcessId is { } selectedPid)
        {
            currentLabel = audioSourceCandidates.Find(c => c.Pid == selectedPid).Label
                ?? $"PID {selectedPid} (no longer running)";
        }

        UiTheme.RowLabel("Audio source", labelWidth);
        ImGui.BeginDisabled(isAudioCapturing());
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.BeginCombo("##audioSource", currentLabel))
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
            ImGui.TextDisabled("(stop audio capture to change source)");
    }

    private void RefreshAudioSourceCandidates()
    {
        if (DateTime.UtcNow - lastAudioSourceRefresh < TimeSpan.FromSeconds(2))
            return;
        lastAudioSourceRefresh = DateTime.UtcNow;

        audioSourceCandidates = EnumerateWindowedProcesses()
            .Select(p => (p.Pid, p.Label))
            .ToList();
    }

    private void DrawCaptureModePicker(float labelWidth, float fieldWidth)
    {
        UiTheme.RowLabel("Mode", labelWidth);
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
        {
            // The list for the mode just switched to has never been built, or is left over from an
            // earlier visit - fetch it now rather than waiting for the combo below to be opened.
            refreshSourcesOnNextDraw = true;
            SaveStreamingSettings();
        }

        if (isStreaming())
            ImGui.TextDisabled("(stop streaming to change capture mode)");

        if (captureWindowMode)
            DrawWindowPicker(labelWidth, fieldWidth);
        else
            DrawMonitorPicker(labelWidth, fieldWidth);
    }

    private void DrawWindowPicker(float labelWidth, float fieldWidth)
    {
        UiTheme.RowLabel("Window", labelWidth);
        ImGui.BeginDisabled(isStreaming());
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.BeginCombo("##windowPicker", WindowLabel))
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
            ImGui.TextDisabled("(stop streaming to change window)");
    }

    private void RefreshWindowCandidates()
    {
        if (DateTime.UtcNow - lastWindowListRefresh < TimeSpan.FromSeconds(2))
            return;
        lastWindowListRefresh = DateTime.UtcNow;

        windowCandidates = EnumerateWindowedProcesses()
            .Where(p => p.Handle != 0)
            .Select(p => (p.Handle, p.Label))
            .ToList();
    }

    /// <summary>Every process with a visible main window, carrying both its PID and its window
    /// handle - the audio picker wants the first and the window picker the second, and sweeping
    /// every process on the machine is the expensive part, so it is done once for both.</summary>
    private static List<(int Pid, nint Handle, string Label)> EnumerateWindowedProcesses()
    {
        var candidates = new List<(int Pid, nint Handle, string Label)>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string title = process.MainWindowTitle;
                if (!string.IsNullOrWhiteSpace(title))
                    candidates.Add((process.Id, process.MainWindowHandle, $"{process.ProcessName} - {title}"));
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
        return candidates;
    }

    private void DrawMonitorPicker(float labelWidth, float fieldWidth)
    {
        UiTheme.RowLabel("Monitor", labelWidth);
        ImGui.BeginDisabled(isStreaming());
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.BeginCombo("##monitorPicker", MonitorLabel))
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
            ImGui.TextDisabled("(stop streaming to change monitor)");
    }

    private void RefreshMonitorCandidates()
    {
        monitorsEnumerated = true;

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
        ServiceCollector.Config.AutoStartAudioWithStream = autoStartAudioWithStream;
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
