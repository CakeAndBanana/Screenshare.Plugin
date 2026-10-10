using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Encoding.Enums;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui.Helpers;

namespace ScreenShare.Plugin.Ui.Views;

public sealed class SettingsWindow : Window, IDisposable
{
    private const int MinVideoBitrateKbps = 500;
    private const int MaxVideoBitrateKbps = 6000;
    private const int MinVideoFps = 5;
    private const int MaxVideoFps = 60;
    private const int MinAudioBitrateKbps = 8;
    private const int MaxAudioBitrateKbps = 256;
    private const int MaxVolumePercent = (int)(Configuration.MaxViewerVolume * 100f);

    private readonly Func<bool> isStreaming;
    private readonly Func<bool> isAudioCapturing;
    private readonly Action<int> applyLiveAudioBitrate;
    private readonly Func<bool> canApplyLiveVideoBitrate;
    private readonly Func<int, bool> applyLiveVideoBitrate;
    private readonly Func<WorldScreenController> worldScreen;

    private GameAdapterDevice? enumerationDevice;

    private int monitorIndex = ServiceCollector.Config.MonitorIndex;
    private List<(int Index, string Label)> monitorCandidates = [];

    private bool monitorsEnumerated;

    private bool captureWindowMode = ServiceCollector.Config.CaptureWindowMode;
    private nint selectedWindowHandle;
    private int? selectedWindowProcessId;
    private List<(int Pid, nint Handle, string Label)> windowCandidates = [];
    private DateTime lastWindowListRefresh = DateTime.MinValue;

    private bool refreshSourcesOnNextDraw = true;

    private bool selectStreamingTabOnNextDraw;

    private int audioBitrateKbps = ServiceCollector.Config.AudioBitrateKbps;
    private bool autoStartAudioWithStream = ServiceCollector.Config.AutoStartAudioWithStream;
    private int? audioSourceProcessId;
    private bool matchAudioToVideoSource = ServiceCollector.Config.MatchAudioToVideoSource;
    private List<(int Pid, string Label)> audioSourceCandidates = [];
    private DateTime lastAudioSourceRefresh = DateTime.MinValue;

    private static readonly string[] VideoResolutionLabels = ["480p", "720p", "1080p", "Native"];
    private static readonly int[] VideoResolutionHeights = [480, 720, 1080, int.MaxValue];
    private int videoResolutionIndex = ServiceCollector.Config.VideoResolutionIndex;
    private int videoBitrateKbps = ServiceCollector.Config.VideoBitrateKbps;
    private int videoFpsTarget = ServiceCollector.Config.VideoFpsTarget;
    private static readonly string[] VideoCodecLabels = ["H.264", "HEVC (H.265)"];
    private int videoCodecIndex = ServiceCollector.Config.VideoCodecIndex;

    private float volume = ServiceCollector.Config.ViewerVolume;
    private bool volumeActive;
    private float opacity = ServiceCollector.Config.WorldScreenOpacity;
    private bool opacityActive;

    public int MonitorIndex => monitorIndex;

    public bool CaptureWindowMode => captureWindowMode;

    public nint SelectedWindowHandle => selectedWindowHandle;

    public int TargetHeight => VideoResolutionHeights[videoResolutionIndex];

    public int VideoBitrateKbps => videoBitrateKbps;

    public int VideoFpsTarget => videoFpsTarget;

    public SupportedVideoCodec Codec => (SupportedVideoCodec)videoCodecIndex;

    public int AudioBitrateKbps => audioBitrateKbps;

    public int? AudioSourceProcessId => matchAudioToVideoSource
        ? captureWindowMode ? selectedWindowProcessId : null
        : audioSourceProcessId;

    public bool AutoStartAudioWithStream => autoStartAudioWithStream;

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
        Func<int, bool> applyLiveVideoBitrate,
        Func<WorldScreenController> worldScreen)
        : base("Screen Share Settings###ScreenShareSettingsWindow")
    {
        this.isStreaming = isStreaming;
        this.isAudioCapturing = isAudioCapturing;
        this.applyLiveAudioBitrate = applyLiveAudioBitrate;
        this.canApplyLiveVideoBitrate = canApplyLiveVideoBitrate;
        this.applyLiveVideoBitrate = applyLiveVideoBitrate;
        this.worldScreen = worldScreen;
        Size = new Vector2(460, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public string DescribeCaptureSource()
    {
        if (captureWindowMode)
            return WindowLabel;

        if (!monitorsEnumerated)
            RefreshMonitorCandidates();

        return MonitorLabel;
    }

    public override void OnOpen() => refreshSourcesOnNextDraw = true;

    public void SelectStreamingTab() => selectStreamingTabOnNextDraw = true;

    public override void Draw()
    {
        if (refreshSourcesOnNextDraw)
            RefreshSources();

        if (!ImGui.BeginTabBar("##settingsTabs"))
            return;

        ImGuiTabItemFlags streamingFlags = selectStreamingTabOnNextDraw ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectStreamingTabOnNextDraw = false;

        if (ImGui.BeginTabItem("General"))
        {
            DrawGeneralSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Streaming", streamingFlags))
        {
            DrawStreamingSettings();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawGeneralSettings()
    {
        Configuration config = ServiceCollector.Config;
        float labelWidth = UiTheme.LabelColumn("Opacity");

        UiTheme.SectionLabel("Watching");

        bool autoWatch = config.AutoWatchStreams;
        if (ImGui.Checkbox("Start watching automatically when someone streams", ref autoWatch))
        {
            config.AutoWatchStreams = autoWatch;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("On joining a room where someone is already live, or when someone goes live while you're in it.\n" +
                             "Doesn't switch away from a stream you're already watching, or start while you're streaming yourself.");
        }

        DrawListeningSettings(config);

        UiTheme.SectionLabel("In-world screen");

        bool showWorldScreen = config.ShowWorldScreen;
        if (ImGui.Checkbox("Show the in-world screen", ref showWorldScreen))
        {
            config.ShowWorldScreen = showWorldScreen;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When off, the screen placed in the world never appears for you - streams play in the popout window only.");

        DrawWorldScreenDisplaySettings(config, labelWidth);

        UiTheme.SectionLabel("Compatibility");

        bool useMediaFoundation = config.UseMediaFoundation;
        if (ImGui.Checkbox("Use Windows hardware video encoding/decoding", ref useMediaFoundation))
        {
            config.UseMediaFoundation = useMediaFoundation;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Uses Media Foundation for streaming and watching. Turn this off if the game crashes when you start.");
        }
    }

    private void DrawListeningSettings(Configuration config)
    {
        float labelWidth = UiTheme.LabelColumn("Volume", "Playback buffer (ms)", "A/V sync (ms)");

        UiTheme.SectionLabel("Audio");
        DrawPlaybackSettings(config, labelWidth);

        bool spatialAudio = config.SpatialAudio;
        if (ImGui.Checkbox("Positional audio", ref spatialAudio))
        {
            config.SpatialAudio = spatialAudio;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Plays the stream's audio from the in-world screen's position, fading with distance.");
    }

    private void DrawPlaybackSettings(Configuration config, float labelWidth)
    {
        if (!volumeActive)
            volume = config.ViewerVolume;

        int volumePercent = (int)MathF.Round(volume * 100f);
        SliderEdit volumeEdit = UiTheme.ValueSlider(
            "##viewerVolume", "Volume", ref volumePercent, 0, MaxVolumePercent, labelWidth, unit: "%");
        volumeActive = volumeEdit.Active;
        if (volumeEdit.Changed)
        {
            volume = Math.Clamp(volumePercent / 100f, 0f, Configuration.MaxViewerVolume);
            config.ViewerVolume = volume;
        }

        if (volumeEdit.Committed)
            ServiceCollector.PI.SavePluginConfig(config);

        int playbackLatencyMs = config.PlaybackLatencyMs;
        SliderEdit bufferEdit = UiTheme.ValueSlider(
            "##playbackBuffer", "Playback buffer (ms)", ref playbackLatencyMs,
            AudioPlaybackDecoder.MinLatencyBudgetMs, AudioPlaybackDecoder.MaxLatencyBudgetMs, labelWidth,
            tooltip: "How much audio is held before playing. This sets the delay for BOTH streams." +
                     "The streams would be audio and video stream.");
        if (bufferEdit.Changed)
            config.PlaybackLatencyMs = playbackLatencyMs;

        if (bufferEdit.Committed)
            ServiceCollector.PI.SavePluginConfig(config);

        int avSyncOffsetMs = config.AvSyncOffsetMs;
        SliderEdit syncEdit = UiTheme.ValueSlider(
            "##avSync", "A/V sync (ms)", ref avSyncOffsetMs, -300, 300, labelWidth,
            tooltip: "Holds video back to line it up with audio.");
        if (syncEdit.Changed)
            config.AvSyncOffsetMs = avSyncOffsetMs;

        if (syncEdit.Committed)
            ServiceCollector.PI.SavePluginConfig(config);
    }

    private void DrawWorldScreenDisplaySettings(Configuration config, float labelWidth)
    {
        if (!opacityActive)
            opacity = config.WorldScreenOpacity;

        SliderEdit opacityEdit = UiTheme.ValueSlider("##worldScreenOpacity", "Opacity", ref opacity, 0.05f, 1f, labelWidth);
        opacityActive = opacityEdit.Active;

        if (opacityEdit.Committed)
        {
            config.WorldScreenOpacity = opacity;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (WorldScreenController.UnderUiBlockedBy is { } blockingOverlay)
        {
            ImGui.TextDisabled(
                $"Drawing underneath the game UI is off because {blockingOverlay} is loaded; " +
                "it makes the in-world screen flicker.");
        }
        else
        {
            DrawUnderUiOption(config);
        }
    }

    private void DrawUnderUiOption(Configuration config)
    {
        bool drawUnderUi = config.WorldScreenDrawUnderUi;
        if (ImGui.Checkbox("Draw underneath the game UI", ref drawUnderUi))
        {
            config.WorldScreenDrawUnderUi = drawUnderUi;
            ServiceCollector.PI.SavePluginConfig(config);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Draws the screen inside the game's own render pass so your hotbars and windows sit " +
                "on top of it; untick it if anything looks wrong.");
        }

        if (!drawUnderUi)
            return;

        WorldScreenController controller = worldScreen();
        if (!controller.IsDrawingUnderUi)
            ImGui.TextColored(UiTheme.WarnColor, "The render hook isn't running.");

        int passes = controller.LastFrameBackBufferSwitches;
        int scenePasses = controller.LastFrameSceneSwitches;
        ImGui.TextDisabled($"Render passes last frame: {passes} ({scenePasses} drawing the 3D scene)");

        if (scenePasses == 0 && passes > 0)
            ImGui.TextColored(UiTheme.WarnColor, "None of this frame's passes bound a depth buffer.");

        if (controller.SceneHookFallbackReason is { } fallbackReason)
            ImGui.TextDisabled($"Adjusted automatically: {fallbackReason}.");
    }

    private void DrawStreamingSettings()
    {
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

        lastWindowListRefresh = DateTime.MinValue;

        if (captureWindowMode)
            RefreshWindowCandidates();
        else
            RefreshMonitorCandidates();
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

        if (ImGui.Checkbox("Capture audio automatically when streaming starts", ref autoStartAudioWithStream))
            SaveStreamingSettings();
    }

    private void DrawAudioSourcePicker(float labelWidth, float fieldWidth)
    {
        if (ImGui.Checkbox("Match audio source to what's being shared", ref matchAudioToVideoSource))
            SaveStreamingSettings();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Sharing a window captures that window's own audio.\n" +
                             "Sharing a monitor captures all system audio.");
        }

        string currentLabel = DescribeAudioSource();

        UiTheme.RowLabel("Audio source", labelWidth);
        ImGui.BeginDisabled(isAudioCapturing() || matchAudioToVideoSource);
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

    private string DescribeAudioSource()
    {
        if (matchAudioToVideoSource)
        {
            if (!captureWindowMode)
                return "System audio (all) - matches monitor";

            return selectedWindowHandle == 0 ? "The shared window (none selected yet)" : $"{WindowLabel} - matches window";
        }

        if (audioSourceProcessId is not { } selectedPid)
            return "System audio (all)";

        return audioSourceCandidates.Find(c => c.Pid == selectedPid).Label
            ?? $"PID {selectedPid} (no longer running)";
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

            foreach ((int pid, nint handle, string label) in windowCandidates)
            {
                if (ImGui.Selectable(label, selectedWindowHandle == handle))
                {
                    selectedWindowHandle = handle;
                    selectedWindowProcessId = pid;
                }
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
            .ToList();
    }

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
        ServiceCollector.Config.MatchAudioToVideoSource = matchAudioToVideoSource;
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
