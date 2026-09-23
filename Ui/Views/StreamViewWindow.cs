using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Encoding;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui.Helpers;

namespace ScreenShare.Plugin.Ui;

public sealed class StreamViewWindow : Window
{
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.7f, 0.2f, 1f);

    private readonly RoomSessionService roomSession;
    private readonly MediaSessionService mediaSession;
    private readonly Func<RawTextureWrap?> getPreviewTexture;
    private readonly Func<WorldScreenController> worldScreen;
    private readonly Action onClosed;

    private readonly WorldScreenPanel worldScreenPanel;

    private float volume = ServiceCollector.Config.ViewerVolume;
    private bool volumeActive;
    private int avSyncOffsetMs = ServiceCollector.Config.AvSyncOffsetMs;
    private int playbackLatencyMs = ServiceCollector.Config.PlaybackLatencyMs;

    private DateTime rateSampledAt;
    private long rateUnderrunBaseMs;
    private long rateDroppedBaseMs;
    private long underrunPerMinute;
    private long droppedPerMinute;

    public StreamViewWindow(
        RoomSessionService roomSession,
        MediaSessionService mediaSession,
        Func<RawTextureWrap?> getPreviewTexture,
        Func<WorldScreenController> worldScreen,
        Action onClosed)
        : base("Watching###ScreenShareStreamView")
    {
        this.roomSession = roomSession;
        this.mediaSession = mediaSession;
        this.getPreviewTexture = getPreviewTexture;
        this.worldScreen = worldScreen;
        this.onClosed = onClosed;
        worldScreenPanel = new WorldScreenPanel("streamView", roomSession, worldScreen, getPreviewTexture);
        Size = new Vector2(640, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnClose()
    {
        if (worldScreen().IsProjecting)
            return;

        onClosed();
    }

    public override void PreDraw()
    {
        string? watchingId = mediaSession.ViewingStreamerConnectionId;
        string displayName = roomSession.Members.FirstOrDefault(m => m.ConnectionId == watchingId)?.DisplayName ?? "someone";
        WindowName = $"Watching {displayName}###ScreenShareStreamView";
    }

    public override void Draw()
    {
        if (mediaSession.ViewerLastError is not null)
            ImGui.TextColored(ErrorColor, $"Media stream error: {mediaSession.ViewerLastError}");

        RawTextureWrap? texture = getPreviewTexture();
        if (texture is null)
        {
            ImGui.TextUnformatted("Connecting...");
        }
        else
        {
            Vector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Min(available.X / texture.Width, available.Y / texture.Height);
            scale = Math.Min(scale, 1f);
            Vector2 displaySize = texture.Size * Math.Max(scale, 0.01f);
            ImGui.Image(texture.Handle, displaySize);
        }

        ImGui.Separator();
        ImGui.TextUnformatted(
            $"{mediaSession.ViewerFramesDecoded} frames decoded ({mediaSession.ViewerAverageFps:F1} fps avg), " +
            $"{mediaSession.ViewerAudioPackets} audio packets");

        if (mediaSession.ViewerDecoderName is { } decoderName)
            ImGui.TextDisabled($"Decoding with {decoderName}.");
        ImGui.TextUnformatted("Audio level:");
        ImGui.SameLine();
        ImGui.ProgressBar(Math.Clamp(mediaSession.ViewerAudioPeak, 0f, 1f), new Vector2(200, 0), "");

        if (mediaSession.ViewerAudioPackets > 0)
        {
            ImGui.TextDisabled(
                $"Audio buffer {mediaSession.ViewerAudioBufferedMs}/{mediaSession.ViewerAudioTargetMs}ms, " +
                $"{mediaSession.ViewerAudioUnderrunMs}ms silence played, " +
                $"{mediaSession.ViewerAudioDroppedMs}ms dropped to hold latency");

            UpdateAudioHealthRates(mediaSession.ViewerAudioUnderrunMs, mediaSession.ViewerAudioDroppedMs);
            if (rateSampledAt != default)
            {
                bool unhealthy = underrunPerMinute > 200 || droppedPerMinute > 200;
                string rateText = $"Recent rate: {underrunPerMinute}ms silence/min, {droppedPerMinute}ms dropped/min";
                if (unhealthy)
                    ImGui.TextColored(WarnColor, rateText + " - still happening, raise the playback buffer");
                else
                    ImGui.TextDisabled(rateText);
            }

            long drift = mediaSession.ViewerAvDriftMs;
            string driftLabel = Math.Abs(drift) < 50
                ? "in sync"
                : drift > 0 ? "video ahead of audio" : "video behind audio";
            string offsetNote = avSyncOffsetMs != 0 ? $", A/V sync offset {avSyncOffsetMs:+0;-0}ms applied" : string.Empty;
            ImGui.TextDisabled($"A/V drift {drift:+0;-0;0}ms ({driftLabel}){offsetNote}");

            if (mediaSession.ViewerBehindLiveMs is { } behind)
            {
                bool runaway = behind > playbackLatencyMs + 400;
                string behindText = $"Behind live {behind}ms" + (runaway ? " - climbing, not just buffered" : string.Empty);
                if (runaway)
                    ImGui.TextColored(WarnColor, behindText);
                else
                    ImGui.TextDisabled(behindText);
            }
        }

        if (mediaSession.ViewerDecodeDrops > 0)
        {
            ImGui.TextColored(WarnColor,
                $"{mediaSession.ViewerDecodeDrops} chunks dropped before decoding - the decoder can't keep up in real time.");
        }

        if (mediaSession.ViewerDisplayDrops > 0)
        {
            ImGui.TextColored(WarnColor,
                $"{mediaSession.ViewerDisplayDrops} decoded frames dropped before display - " +
                "playback isn't draining the buffer as fast as frames arrive.");
        }

        if (mediaSession.ViewerVideoError is { } viewerVideoError)
            ImGui.TextColored(ErrorColor, $"Video decoder died mid-stream: {viewerVideoError.Message}");

        DrawPlaybackControls();
        DrawWorldScreenSection();
    }

    private void UpdateAudioHealthRates(long underrunMs, long droppedMs)
    {
        DateTime now = DateTime.UtcNow;
        bool restarted = underrunMs < rateUnderrunBaseMs || droppedMs < rateDroppedBaseMs;

        if (rateSampledAt == default || restarted)
        {
            rateSampledAt = now;
            rateUnderrunBaseMs = underrunMs;
            rateDroppedBaseMs = droppedMs;
            return;
        }

        double minutes = (now - rateSampledAt).TotalMinutes;
        if (minutes < 0.25)
            return;

        underrunPerMinute = (long)((underrunMs - rateUnderrunBaseMs) / minutes);
        droppedPerMinute = (long)((droppedMs - rateDroppedBaseMs) / minutes);
        rateSampledAt = now;
        rateUnderrunBaseMs = underrunMs;
        rateDroppedBaseMs = droppedMs;
    }

    private void DrawPlaybackControls()
    {
        Configuration config = ServiceCollector.Config;
        float labelWidth = UiTheme.LabelColumn("Volume", "Playback buffer (ms)", "A/V sync (ms)");

        if (!volumeActive)
            volume = config.ViewerVolume;

        int volumePercent = (int)MathF.Round(volume * 100f);
        SliderEdit volumeEdit = UiTheme.ValueSlider(
            "##viewerVolume", "Volume", ref volumePercent, 0, 100, labelWidth, unit: "%");
        volumeActive = volumeEdit.Active;
        if (volumeEdit.Changed)
        {
            volume = Math.Clamp(volumePercent / 100f, 0f, 1f);
            config.ViewerVolume = volume;
        }

        if (volumeEdit.Committed)
            ServiceCollector.PI.SavePluginConfig(config);

        SliderEdit bufferEdit = UiTheme.ValueSlider(
            "##playbackBuffer", "Playback buffer (ms)", ref playbackLatencyMs,
            AudioPlaybackDecoder.MinLatencyBudgetMs, AudioPlaybackDecoder.MaxLatencyBudgetMs, labelWidth,
            tooltip: "How much audio is held before playing. This sets the delay for BOTH streams." +
                     "The streams would be audio and video stream.");
        if (bufferEdit.Changed)
            config.PlaybackLatencyMs = playbackLatencyMs;

        if (bufferEdit.Committed)
            ServiceCollector.PI.SavePluginConfig(config);

        SliderEdit syncEdit = UiTheme.ValueSlider(
            "##avSync", "A/V sync (ms)", ref avSyncOffsetMs, -300, 300, labelWidth,
            tooltip: "Holds video back to line it up with audio.");
        if (syncEdit.Changed)
            config.AvSyncOffsetMs = avSyncOffsetMs;

        if (syncEdit.Committed)
            ServiceCollector.PI.SavePluginConfig(config);
    }

    private void DrawWorldScreenSection()
    {
        if (!ServiceCollector.Config.ShowWorldScreen || !ImGui.CollapsingHeader("In-world screen"))
            return;

        worldScreenPanel.Draw();
    }
}
