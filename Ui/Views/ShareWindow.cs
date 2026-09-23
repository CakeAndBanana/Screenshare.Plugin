using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Capture.AudioCapture;
using ScreenShare.Plugin.Capture.AudioCapture.Interfaces;
using ScreenShare.Plugin.Capture.ScreenCapture;
using ScreenShare.Plugin.Capture.ScreenCapture.Interfaces;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;
using ScreenShare.Plugin.Ui.Helpers;
using ScreenShare.Plugin.Ui.Views;

namespace ScreenShare.Plugin.Ui;

public sealed class ShareWindow : Window, IDisposable
{
    private enum Screen
    {
        Connect,
        Lobby,
        Room,
    }

    private static readonly Vector2 ConnectedSize = new(560, 540);

    private GameAdapterDevice? device;
    private ICaptureSource? screenCapture;

    private readonly Lock audioCaptureLock = new();
    private int audioCaptureGeneration;
    private RawTextureWrap? previewTexture;
    private string? streamSetupError;

    private IAudioSource? audioCapture;
    private string? audioError;

    private readonly RoomSessionService roomSession = new();
    private readonly MediaSessionService mediaSession = new();

    private readonly WorldScreenController worldScreen = new();
    private readonly IdentityChip identity;
    private readonly LobbyView lobby;
    private readonly ConcurrentQueue<(ActiveStreamerInfo Streamer, bool JustStarted)> wentLive = new();
    private Screen lastScreen = Screen.Connect;
    private bool releaseForcedSize;
    private bool wasViewerInRoom;
    private nint? lastOpenedViewerVideoHandle;
    private Exception? lastHandledHostMediaException;
    private Exception? lastHandledViewerMediaException;

    public StreamViewWindow StreamView { get; }

    public SettingsWindow Settings { get; }

    public WorldScreenWindow WorldScreen { get; }

    public ShareWindow() : base("Screen Share###ScreenShareMainWindow")
    {
        StreamView = new StreamViewWindow(roomSession, mediaSession, () => previewTexture, () => worldScreen, StopWatching);
        WorldScreen = new WorldScreenWindow(roomSession, () => worldScreen, () => previewTexture);
        Settings = new SettingsWindow(
            isStreaming: () => screenCapture is not null,
            isAudioCapturing: () => audioCapture is not null,
            applyLiveAudioBitrate: bps =>
            {
                if (audioCapture is not null)
                    mediaSession.UpdateAudioSource(audioCapture, bps);
            },
            canApplyLiveVideoBitrate: () => mediaSession.HostSupportsLiveVideoBitrate,
            applyLiveVideoBitrate: kbps => mediaSession.UpdateVideoBitrate(kbps));

        identity = new IdentityChip(roomSession, () => mediaSession.IsHosting);
        lobby = new LobbyView(roomSession, identity, Disconnect);
        roomSession.StreamerWentLive += streamer => wentLive.Enqueue((streamer, true));
        roomSession.StreamerAlreadyLive += streamer => wentLive.Enqueue((streamer, false));

        TitleBarButtons =
        [
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Cog,
                IconOffset = new Vector2(2, 1),
                Click = _ => OpenSettings(),
                ShowTooltip = () => ImGui.SetTooltip("Settings"),
            },
        ];
    }

    public void OpenSettings()
    {
        Settings.IsOpen = true;
        Settings.BringToFront();
    }

    private void OpenStreamingSettings()
    {
        Settings.SelectStreamingTab();
        OpenSettings();
    }

    public void OpenWorldScreen()
    {
        WorldScreen.IsOpen = true;
        WorldScreen.BringToFront();
    }

    public void TickFrame()
    {
        HandleViewerMediaLifecycle();

        while (wentLive.TryDequeue(out (ActiveStreamerInfo Streamer, bool JustStarted) live))
        {
            if (TryAutoWatch(live.Streamer.ConnectionId))
                continue;

            if (live.JustStarted)
                NotifyWentLive(live.Streamer);
        }
    }

    private bool TryAutoWatch(string streamerId)
    {
        if (!ServiceCollector.Config.AutoWatchStreams || mediaSession.ViewingStreamerConnectionId is not null || mediaSession.IsHosting)
            return false;

        if (!roomSession.InRoom || !roomSession.ActiveStreamers.Any(s => s.ConnectionId == streamerId))
            return false;

        StartWatching(streamerId);
        return mediaSession.ViewingStreamerConnectionId == streamerId;
    }

    private Screen CurrentScreen => roomSession.State is HubConnectionState.Connected or HubConnectionState.Reconnecting || roomSession.IsSwitchingRelay
        ? roomSession.InRoom ? Screen.Room : Screen.Lobby
        : Screen.Connect;

    public override void PreDraw()
    {
        Screen screen = CurrentScreen;

        WindowName = screen == Screen.Room && roomSession.RoomName is { } roomName
            ? $"Screen Share · {roomName}{(mediaSession.IsHosting ? " · LIVE" : string.Empty)}###ScreenShareMainWindow"
            : "Screen Share###ScreenShareMainWindow";

        if (screen == Screen.Connect)
            Flags |= ImGuiWindowFlags.AlwaysAutoResize;
        else
            Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;

        if (releaseForcedSize)
        {
            Size = null;
            releaseForcedSize = false;
        }

        if (lastScreen == Screen.Connect && screen != Screen.Connect)
        {
            Size = ConnectedSize;
            SizeCondition = ImGuiCond.Always;
            releaseForcedSize = true;
        }

        lastScreen = screen;
    }

    public override void Draw()
    {
        switch (CurrentScreen)
        {
            case Screen.Connect:
                lobby.DrawConnect();
                break;
            case Screen.Lobby:
                lobby.DrawLobby();
                break;
            default:
                DrawRoomScreen();
                break;
        }
    }

    private void Disconnect()
    {
        if (roomSession.InRoom)
            LeaveRoom();

        roomSession.Disconnect();
    }

    private void NotifyWentLive(ActiveStreamerInfo streamer)
    {
        if (!roomSession.InRoom)
            return;

        string streamerId = streamer.ConnectionId;
        IActiveNotification toast = ServiceCollector.Notifications.AddNotification(new Notification
        {
            Title = $"{streamer.DisplayName} is live",
            Content = $"Streaming in {roomSession.RoomName ?? "your room"}. Click to watch.",
            Type = NotificationType.Info,
            InitialDuration = TimeSpan.FromSeconds(12),
        });

        toast.Click += args =>
        {
            WatchFromNotification(streamerId);
            args.Notification.DismissNow();
        };

        toast.DrawActions += args =>
        {
            if (!ImGui.Button("Watch"))
                return;

            WatchFromNotification(streamerId);
            args.Notification.DismissNow();
        };
    }

    private void WatchFromNotification(string streamerId)
    {
        if (!roomSession.InRoom || !roomSession.ActiveStreamers.Any(s => s.ConnectionId == streamerId))
            return;

        IsOpen = true;
        if (mediaSession.IsViewing && mediaSession.ViewingStreamerConnectionId == streamerId)
        {
            StreamView.IsOpen = true;
            StreamView.BringToFront();
            return;
        }

        StartWatching(streamerId);
    }

    private void DrawRoomScreen()
    {
        DrawRoomHeader();

        if (!ImGui.BeginTabBar("##roomTabs"))
            return;

        if (ImGui.BeginTabItem("Room"))
        {
            DrawRoomTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Events"))
        {
            DrawEventLog();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawRoomHeader()
    {
        RoomKind kind = UiTheme.KindOf(roomSession.RoomIsPublic, roomSession.RoomHasPassword);
        Vector4 color = UiTheme.ColorOf(kind);
        ImGuiStylePtr style = ImGui.GetStyle();

        float width = ImGui.GetContentRegionAvail().X;
        float lineHeight = ImGui.GetFrameHeight();
        float padding = UiTheme.Px(6);
        float secondLineY = padding + lineHeight + style.ItemSpacing.Y;
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(width, secondLineY + lineHeight + padding);

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint strong = UiTheme.Color(color, 0.5f);
        uint faint = UiTheme.Color(color, 0.04f);
        drawList.AddRectFilledMultiColor(min, max, strong, faint, faint, strong);
        drawList.AddRectFilled(min, new Vector2(min.X + UiTheme.Px(4), max.Y), UiTheme.Color(color));

        float left = min.X + UiTheme.Px(14);
        ImGui.SetCursorScreenPos(new Vector2(left, min.Y + padding));
        ImGui.AlignTextToFramePadding();
        UiTheme.Icon(UiTheme.IconOf(kind), color);
        ImGui.SameLine();
        ImGui.TextUnformatted(roomSession.RoomName ?? $"Room {roomSession.RoomId}");

        ImGui.SetCursorScreenPos(new Vector2(left, min.Y + secondLineY));
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"#{roomSession.RoomId}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Room code. Click to copy it.");
        if (ImGui.IsItemClicked() && roomSession.RoomId is { } roomId)
            ImGui.SetClipboardText(roomId);

        ImGui.SameLine();
        UiTheme.Pill(roomSession.RoomIsPublic ? "Public" : "Private", roomSession.RoomIsPublic ? UiTheme.PublicGreen : UiTheme.PrivatePurple);
        if (roomSession.RoomHasPassword)
        {
            ImGui.SameLine();
            UiTheme.Pill("Password", UiTheme.LockedBlue);
        }

        if (mediaSession.IsHosting)
        {
            ImGui.SameLine();
            UiTheme.Pill("You're live", UiTheme.LiveRed);
        }

        float right = max.X - padding;
        ImGui.SetCursorScreenPos(new Vector2(right - identity.Width, min.Y + padding));
        identity.Draw();

        float leaveWidth = UiTheme.Px(100);
        ImGui.SetCursorScreenPos(new Vector2(right - leaveWidth, min.Y + secondLineY));
        if (UiTheme.ColoredButton("Leave room", UiTheme.LiveRed, new Vector2(leaveWidth, 0)))
            LeaveRoom();

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y + style.ItemSpacing.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawRoomTab()
    {
        DrawWatchingStatus();
        DrawMemberList();

        if (roomSession.CanStartStreaming || mediaSession.IsHosting)
            DrawStreamingSection();

        if (roomSession.CanPlaceScreen)
            DrawScreenPlacement();

        DrawMediaStreamStatus();

        if (roomSession.LastError is not null)
            ImGui.TextColored(UiTheme.ErrorColor, $"Relay error: {roomSession.LastError}");
    }

    private void DrawEventLog()
    {
        IReadOnlyList<RoomEvent> events = roomSession.Events;

        ImGui.BeginDisabled(events.Count == 0);
        if (ImGui.Button("Clear"))
            roomSession.ClearEvents();
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(events.Count == 1 ? "1 event" : $"{events.Count} events");
        ImGui.Separator();

        if (ImGui.BeginChild("##eventLog", Vector2.Zero, false))
        {
            if (events.Count == 0)
            {
                ImGui.TextDisabled("Nothing has happened in this room yet.");
            }
            else
            {
                foreach (RoomEvent entry in events)
                {
                    ImGui.TextDisabled(entry.TimestampLocal.ToString("HH:mm:ss"));
                    ImGui.SameLine();
                    ImGui.TextUnformatted(entry.Message);
                }

                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                    ImGui.SetScrollHereY(1f);
            }
        }

        ImGui.EndChild();
    }

    private void DrawMemberList()
    {
        IReadOnlyList<RoomMemberInfo> members = roomSession.Members;
        IReadOnlyList<ActiveStreamerInfo> streamers = roomSession.ActiveStreamers;
        IReadOnlyList<WatcherInfo> watchers = roomSession.Watchers;

        UiTheme.SectionLabel($"Members · {members.Count}");

        IEnumerable<RoomMemberInfo> ordered = members
            .OrderByDescending(m => streamers.Any(s => s.ConnectionId == m.ConnectionId))
            .ThenByDescending(m => m.IsHost);

        foreach (RoomMemberInfo member in ordered)
            DrawMemberRow(member, members, streamers, watchers);
    }

    private void DrawMemberRow(
        RoomMemberInfo member,
        IReadOnlyList<RoomMemberInfo> members,
        IReadOnlyList<ActiveStreamerInfo> streamers,
        IReadOnlyList<WatcherInfo> watchers)
    {
        bool isSelf = member.ConnectionId == roomSession.ConnectionId;
        bool isStreaming = streamers.Any(s => s.ConnectionId == member.ConnectionId);
        bool isWatchingThis = isStreaming && mediaSession.IsViewing && mediaSession.ViewingStreamerConnectionId == member.ConnectionId;
        string? watchingName = watchers.FirstOrDefault(w => w.WatcherConnectionId == member.ConnectionId) is { } watching
            ? members.FirstOrDefault(m => m.ConnectionId == watching.StreamerConnectionId)?.DisplayName ?? "someone"
            : null;
        bool showHostActions = roomSession.IsHost && !isSelf;

        ImGui.PushID(member.ConnectionId);

        ImGuiStylePtr style = ImGui.GetStyle();
        float rowHeight = ImGui.GetFrameHeight() + UiTheme.Px(6);
        float width = ImGui.GetContentRegionAvail().X;
        float centerOffset = (rowHeight - ImGui.GetFrameHeight()) / 2;
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(width, rowHeight);

        if (isStreaming)
        {
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(min, max, UiTheme.Color(UiTheme.LiveRed, 0.12f), UiTheme.Px(4));
            drawList.AddRectFilled(min, new Vector2(min.X + UiTheme.Px(3), max.Y), UiTheme.Color(UiTheme.LiveRed));
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X + UiTheme.Px(10), min.Y + centerOffset));
        ImGui.AlignTextToFramePadding();
        DrawMemberIcons(member, isStreaming, watchingName is not null);

        string label = isSelf ? $"{member.DisplayName} (you)" : member.DisplayName;
        if (isStreaming)
            ImGui.TextColored(UiTheme.StreamingGreen, label);
        else
            ImGui.TextUnformatted(label);

        if (ImGui.IsItemHovered())
            DrawMemberTooltip(member, isStreaming, watchingName);

        if (isStreaming)
        {
            ImGui.SameLine();
            UiTheme.Pill("LIVE", UiTheme.LiveRed);
            ImGui.SameLine();
            DrawWatchButton(member, isSelf, isWatchingThis);
        }

        if (showHostActions)
        {
            FontAwesomeIcon streamIcon = member.CanStream ? FontAwesomeIcon.VideoSlash : FontAwesomeIcon.Video;
            float actionsWidth = UiTheme.IconButtonWidth(streamIcon) + UiTheme.IconButtonWidth(FontAwesomeIcon.UserSlash) + style.ItemSpacing.X + UiTheme.Px(6);
            ImGui.SetCursorScreenPos(new Vector2(max.X - actionsWidth, min.Y + centerOffset));
            DrawHostActions(member, streamIcon);
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(width, rowHeight));
        ImGui.PopID();
    }

    private void DrawHostActions(RoomMemberInfo member, FontAwesomeIcon streamIcon)
    {
        if (ImGuiComponents.IconButton("##streamPermission", streamIcon))
        {
            if (member.CanStream)
                roomSession.RevokeStreamPermission(member.ConnectionId);
            else
                roomSession.GrantStreamPermission(member.ConnectionId);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(member.CanStream ? "Take away streaming" : "Allow streaming");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##kick", FontAwesomeIcon.UserSlash))
            ImGui.OpenPopup("##confirmKick");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remove from the room");

        if (!ImGui.BeginPopup("##confirmKick"))
            return;

        ImGui.TextUnformatted($"Remove {member.DisplayName}?");
        ImGui.TextDisabled("They won't be able to rejoin this room.");
        if (UiTheme.ColoredButton("Remove", UiTheme.LiveRed))
        {
            roomSession.KickViewer(member.ConnectionId);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private static void DrawMemberIcons(RoomMemberInfo member, bool isStreaming, bool isWatching)
    {
        if (member.IsHost)
        {
            UiTheme.Icon(FontAwesomeIcon.Crown, UiTheme.CrownColor);
            ImGui.SameLine(0, UiTheme.Px(6));
        }
        else if (member.CanStream && !isStreaming)
        {
            UiTheme.Icon(FontAwesomeIcon.UserCheck, UiTheme.PublicGreen);
            ImGui.SameLine(0, UiTheme.Px(6));
        }

        if (isWatching)
        {
            UiTheme.Icon(FontAwesomeIcon.Eye, UiTheme.WatchingColor);
            ImGui.SameLine(0, UiTheme.Px(6));
        }
    }

    private void DrawWatchButton(RoomMemberInfo member, bool isSelf, bool isWatchingThis)
    {
        if (isWatchingThis)
        {
            if (ImGui.Button("Stop watching##watch"))
                StopWatching();
        }
        else if (UiTheme.ColoredButton("Start watching##watch", UiTheme.WatchingColor))
        {
            StartWatching(member.ConnectionId);
        }

        if (isSelf && !isWatchingThis && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Preview your own stream.\n" +
                "Turn audio capture off first - system loopback would record\n" +
                "this preview's own playback and feed it back into the stream.");
        }
    }

    private void DrawMemberTooltip(RoomMemberInfo member, bool isStreaming, string? watchingName)
    {
        ImGui.BeginTooltip();
        if (isStreaming)
            ImGui.TextColored(UiTheme.StreamingGreen, member.DisplayName);
        else
            ImGui.TextUnformatted(member.DisplayName);

        ImGui.TextDisabled(member.IsHost ? "Host" : member.CanStream ? "Allowed to stream" : "Viewer");
        ImGui.Separator();
        ImGui.TextDisabled("ID");
        ImGui.TextUnformatted(member.UserId);

        if (isStreaming)
            ImGui.TextColored(UiTheme.LiveRed, "Live now");
        if (watchingName is not null)
            ImGui.TextDisabled($"Watching {watchingName}");

        ImGui.EndTooltip();
    }

    private void DrawWatchingStatus()
    {
        if (!mediaSession.IsViewing)
            return;

        string displayName = roomSession.Members.FirstOrDefault(m => m.ConnectionId == mediaSession.ViewingStreamerConnectionId)?.DisplayName ?? "someone";

        ImGui.AlignTextToFramePadding();
        UiTheme.Icon(FontAwesomeIcon.Eye, UiTheme.WatchingColor);
        ImGui.SameLine();

        ImGui.TextUnformatted(worldScreen.IsProjecting && !StreamView.IsOpen
            ? $"Watching {displayName} on the in-world screen."
            : $"Watching {displayName} in a popout window.");

        ImGui.SameLine();
        if (ImGui.Button("Show##showStreamView"))
        {
            StreamView.IsOpen = true;
            StreamView.BringToFront();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop watching##stopFromStatus"))
            StopWatching();

        DrawWatchingVolume();
    }

    private static void DrawWatchingVolume()
    {
        Configuration config = ServiceCollector.Config;

        ImGui.SameLine();
        UiTheme.Icon(config.ViewerVolume <= 0f ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp, UiTheme.WatchingColor);
        ImGui.SameLine();

        int volumePercent = (int)MathF.Round(config.ViewerVolume * 100f);
        ImGui.SetNextItemWidth(MathF.Max(UiTheme.Px(80), MathF.Min(UiTheme.Px(140), ImGui.GetContentRegionAvail().X)));
        if (ImGui.SliderInt("##watchingVolume", ref volumePercent, 0, 100, "%d%%"))
            config.ViewerVolume = Math.Clamp(volumePercent / 100f, 0f, 1f);

        if (ImGui.IsItemDeactivatedAfterEdit())
            ServiceCollector.PI.SavePluginConfig(config);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Volume of the stream you're watching.");
    }

    private void DrawStreamingSection()
    {
        UiTheme.SectionLabel("Your stream");

        bool live = mediaSession.IsHosting;
        var size = new Vector2(UiTheme.Px(150), ImGui.GetFrameHeight() * 1.3f);
        Vector4 liveColor = live ? UiTheme.LiveRed : UiTheme.PublicGreen;
        if (ImGuiComponents.IconButtonWithText(
                live ? FontAwesomeIcon.Stop : FontAwesomeIcon.BroadcastTower,
                live ? "Stop streaming" : "Go live",
                liveColor with { W = 0.6f },
                liveColor,
                liveColor with { W = 0.85f },
                size))
        {
            ToggleMediaStreaming();
        }

        ImGui.SameLine();
        bool audioOn = audioCapture is not null;
        if (ImGuiComponents.IconButtonWithText(audioOn ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute, audioOn ? "Stop audio" : "Capture audio", size))
            ToggleAudioCapture();

        if (audioCapture is not null)
        {
            ImGui.SameLine();
            ImGui.ProgressBar(Math.Clamp(audioCapture.CurrentPeak, 0f, 1f), new Vector2(UiTheme.Px(90), size.Y), string.Empty);
            if (ImGui.IsItemHovered())
            {
                NAudio.Wave.WaveFormat format = audioCapture.WaveFormat;
                ImGui.SetTooltip($"Audio level - {format.SampleRate} Hz, {format.Channels}ch");
            }
        }

        DrawCaptureSourceLine();

        if (audioError is not null)
            ImGui.TextColored(UiTheme.ErrorColor, $"Audio capture failed: {audioError}");
        if (streamSetupError is not null)
            ImGui.TextColored(UiTheme.ErrorColor, streamSetupError);
        if (audioCapture?.LastError is { } audioCaptureError)
            ImGui.TextColored(UiTheme.WarnColor, $"Audio capture error: {audioCaptureError.Message}");

        DrawScreenCaptureStatus();
    }

    private void DrawCaptureSourceLine()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Sharing:");
        ImGui.SameLine();

        if (Settings.HasCaptureSource)
            ImGui.TextDisabled(Settings.DescribeCaptureSource());
        else
            ImGui.TextColored(UiTheme.WarnColor, "nothing selected");

        ImGui.SameLine();
        if (ImGui.SmallButton("Change##captureSource"))
            OpenStreamingSettings();
    }

    private void DrawScreenCaptureStatus()
    {
        if (screenCapture is null)
            return;

        if (screenCapture.LastError is { } captureError)
            ImGui.TextColored(UiTheme.WarnColor, $"Screen capture error: {captureError.Message}");

        if (screenCapture.FramesCaptured == 0)
        {
            ImGui.TextColored(UiTheme.WarnColor,
                "No frames captured yet - if this doesn't move, the selected monitor isn't producing frames.");
        }
    }

    private void DrawScreenPlacement()
    {
        UiTheme.SectionLabel("In-world screen");

        ScreenPlacementInfo? placement = roomSession.ScreenPlacement;
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Tv, placement is null ? "Place screen here" : "Move here")
            && WorldScreenController.PlaceInFrontOfPlayer(placement) is { } placed)
        {
            WorldScreenController.ApplyPlacement(roomSession, placed);
        }

        if (placement is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove screen"))
                WorldScreenController.ApplyPlacement(roomSession, null);
        }

        ImGui.SameLine();
        if (ImGui.Button("Adjust##openWorldScreen"))
            OpenWorldScreen();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Position, size, tilt and audio falloff for the in-world screen.");

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        if (!ServiceCollector.Config.ShowWorldScreen)
            ImGui.TextColored(UiTheme.WarnColor, "You've turned the in-world screen off in settings, so you won't see it yourself.");
        ImGui.TextDisabled(placement is null
            ? "Everyone watching sees it once it's placed."
            : placement.TerritoryType != ServiceCollector.ClientState.TerritoryType
                ? "Placed in another zone."
                : "Shown to everyone watching. Adjust fine-tunes its position and size.");
        ImGui.PopTextWrapPos();
    }

    private void HandleViewerMediaLifecycle()
    {
        bool isViewerInRoom = roomSession.InRoom && !roomSession.IsHost;
        if (!isViewerInRoom && wasViewerInRoom)
            StopWatching();

        wasViewerInRoom = isViewerInRoom;

        if (!roomSession.InRoom && mediaSession.IsHosting)
        {
            mediaSession.StopHosting();
            DisposeStreamCapture();
            StopAudioCapture();
        }

        if (mediaSession.IsViewing && mediaSession.ViewingStreamerConnectionId is { } watchingId
            && !roomSession.ActiveStreamers.Any(s => s.ConnectionId == watchingId))
        {
            StopWatching();
        }

        if (mediaSession.IsViewing && device is not null
            && mediaSession.ViewerSharedHandle is { } handle && handle != lastOpenedViewerVideoHandle)
        {
            previewTexture?.Dispose();
            previewTexture = SharedTextureBridge.OpenOnGameDevice(device.GameDevice, handle, mediaSession.ViewerWidth, mediaSession.ViewerHeight);
            lastOpenedViewerVideoHandle = handle;
        }

        if (mediaSession.HostLastException is { } hostMediaEx && !ReferenceEquals(hostMediaEx, lastHandledHostMediaException))
        {
            lastHandledHostMediaException = hostMediaEx;
            TryRecoverFromDeviceRemoved(hostMediaEx);
        }

        if (mediaSession.ViewerLastException is { } viewerMediaEx && !ReferenceEquals(viewerMediaEx, lastHandledViewerMediaException))
        {
            lastHandledViewerMediaException = viewerMediaEx;
            TryRecoverFromDeviceRemoved(viewerMediaEx);
        }

        EnsureDeviceForPlacedScreen();
        worldScreen.Update(device, previewTexture, roomSession, mediaSession);
    }

    private void EnsureDeviceForPlacedScreen()
    {
        if (device is not null || !roomSession.InRoom || roomSession.ScreenPlacement is not { } placement
            || placement.TerritoryType != ServiceCollector.ClientState.TerritoryType)
        {
            return;
        }

        try
        {
            device = new GameAdapterDevice(ServiceCollector.PI.UiBuilder);
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Verbose(ex, "ScreenShare could not create the game device for the idle in-world screen yet.");
        }
    }

    private void StartWatching(string streamerConnectionId)
    {
        try
        {
            device ??= new GameAdapterDevice(ServiceCollector.PI.UiBuilder);
            string ffmpegPath = Path.Combine(ServiceCollector.PI.AssemblyLocation.DirectoryName!, "ffmpeg", "ffmpeg.exe");
            mediaSession.StartViewing(roomSession.RelayUrl ?? ServiceCollector.Config.RelayUrl, roomSession.RoomId!, roomSession.ConnectionId!, streamerConnectionId, device, ffmpegPath);
            previewTexture?.Dispose();
            previewTexture = null;
            lastOpenedViewerVideoHandle = null;
            StreamView.IsOpen = true;
            StreamView.BringToFront();
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Error(ex, "ScreenShare failed to start viewing session");
            TryRecoverFromDeviceRemoved(ex);
        }
    }

    private void StopWatching()
    {
        mediaSession.StopViewing();
        previewTexture?.Dispose();
        previewTexture = null;
        lastOpenedViewerVideoHandle = null;
        StreamView.IsOpen = false;
    }

    private void DrawMediaStreamStatus()
    {
        if (mediaSession.HostLastError is not null)
            ImGui.TextColored(UiTheme.ErrorColor, $"Media stream error: {mediaSession.HostLastError}");

        if (mediaSession.IsHosting)
        {
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            UiTheme.Pill("LIVE", UiTheme.LiveRed);
            ImGui.SameLine();
            string resolution = screenCapture is { FramesCaptured: > 0 } capture ? $"{capture.Width}x{capture.Height} · " : string.Empty;
            ImGui.TextDisabled(
                $"{resolution}{mediaSession.HostAverageFps:F1} fps · {mediaSession.HostAverageBitrateKbps:F0} kbps · {mediaSession.HostEncoderName}");

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"{mediaSession.HostFramesEncoded} frames encoded, {mediaSession.HostFramesSkipped} skipped\n" +
                    $"{mediaSession.HostEncodedBytes} bytes sent\n" +
                    $"Bitrate target {mediaSession.HostCurrentBitrateKbps} kbps\n" +
                    $"{mediaSession.HostEncoderName}, {mediaSession.HostCodec}");
            }

            if (mediaSession.HostBitrateReduced)
            {
                ImGui.TextColored(UiTheme.WarnColor,
                    $"Bitrate reduced to {mediaSession.HostCurrentBitrateKbps}kbps - the upload can't carry the configured rate.");
            }

            if (mediaSession.HostQueueDrops > 0)
            {
                ImGui.TextColored(UiTheme.WarnColor,
                    $"{mediaSession.HostQueueDrops} video chunks dropped before sending - upload can't keep up with the configured bitrate.");
            }

            if (mediaSession.HostQueueAudioDrops > 0)
            {
                ImGui.TextColored(UiTheme.WarnColor,
                    $"{mediaSession.HostQueueAudioDrops} audio packets dropped before sending - upload can't keep up even with audio's own queue.");
            }

            if (mediaSession.HostVideoError is { } hostVideoError)
                ImGui.TextColored(UiTheme.ErrorColor, $"Video encoder died mid-stream: {hostVideoError.Message}");
        }
    }

    private void LeaveRoom()
    {
        mediaSession.Stop();
        previewTexture?.Dispose();
        previewTexture = null;
        lastOpenedViewerVideoHandle = null;
        DisposeStreamCapture();
        StopAudioCapture();
        roomSession.LeaveRoom();
    }

    private void ToggleAudioCapture()
    {
        if (audioCapture is not null)
        {
            StopAudioCapture();
            return;
        }

        StartAudioCapture();
    }

    private void StartAudioCapture()
    {
        audioError = null;

        int generation;
        lock (audioCaptureLock)
            generation = ++audioCaptureGeneration;

        if (Settings.AudioSourceProcessId is { } targetProcessId)
            _ = StartProcessAudioCaptureAsync(targetProcessId, generation);
        else
            StartSystemAudioCapture();
    }

    private void StopAudioCapture()
    {
        ReleaseAudioCapture();
        mediaSession.UpdateAudioSource(null);
    }

    private void ReleaseAudioCapture()
    {
        IAudioSource? oldCapture;
        lock (audioCaptureLock)
        {
            // Invalidates any process capture still activating, so it disposes itself instead of going live after a stop.
            audioCaptureGeneration++;
            oldCapture = audioCapture;
            audioCapture = null;
        }

        oldCapture?.Dispose();
    }

    private void StartSystemAudioCapture()
    {
        try
        {
            audioCapture = new SystemAudioCapture();
            mediaSession.UpdateAudioSource(audioCapture, Settings.AudioBitrateKbps * 1000);
        }
        catch (Exception ex)
        {
            audioError = DescribeAudioCaptureError(ex);
            ServiceCollector.PluginLog.Error(ex, "ScreenShare audio capture init failed");
        }
    }

    private async Task StartProcessAudioCaptureAsync(int processId, int generation)
    {
        try
        {
            ProcessAudioCapture capture = await ProcessAudioCapture.CreateAsync((uint)processId).ConfigureAwait(false);
            lock (audioCaptureLock)
            {
                if (generation == audioCaptureGeneration)
                {
                    audioCapture = capture;
                    mediaSession.UpdateAudioSource(capture, Settings.AudioBitrateKbps * 1000);
                    return;
                }
            }

            capture.Dispose();
        }
        catch (Exception ex)
        {
            audioError = DescribeAudioCaptureError(ex);
            ServiceCollector.PluginLog.Error(ex, "ScreenShare process audio capture init failed");
        }
    }

    private static string DescribeAudioCaptureError(Exception ex)
    {
        if (ex is InvalidCastException)
        {
            return "Audio capture failed to initialize, likely because the plugin was reloaded " +
                   "without a full game restart (a stale copy of an audio library is still loaded). " +
                   $"Try fully restarting the game. ({ex.Message})";
        }

        return ex.Message;
    }

    private void ToggleMediaStreaming()
    {
        if (mediaSession.IsHosting)
        {
            mediaSession.StopHosting();
            DisposeStreamCapture();
            StopAudioCapture();
            return;
        }

        if (roomSession.RoomId is not { } roomId || roomSession.ConnectionId is not { } connectionId)
            return;

        if (!Settings.HasCaptureSource)
        {
            streamSetupError = "Pick a window to capture first.";
            OpenStreamingSettings();
            return;
        }

        try
        {
            streamSetupError = null;
            device ??= new GameAdapterDevice(ServiceCollector.PI.UiBuilder);

            DisposeStreamCapture();
            screenCapture = Settings.CaptureWindowMode
                ? new WindowCapture(device, Settings.SelectedWindowHandle)
                : new DesktopDuplicationCapture(device, Settings.MonitorIndex);
        }
        catch (Exception ex)
        {
            streamSetupError = $"Screen capture init failed: {ex.Message}";
            ServiceCollector.PluginLog.Error(ex, "ScreenShare screen capture init failed");

            if (!TryRecoverFromDeviceRemoved(ex))
                OpenStreamingSettings();

            return;
        }

        string ffmpegPath = Path.Combine(ServiceCollector.PI.AssemblyLocation.DirectoryName!, "ffmpeg", "ffmpeg.exe");
        mediaSession.StartHosting(
            roomSession.RelayUrl ?? ServiceCollector.Config.RelayUrl, roomId, connectionId, device, screenCapture, audioCapture, ffmpegPath,
            Settings.TargetHeight, Settings.VideoBitrateKbps, Settings.VideoFpsTarget, Settings.Codec, Settings.AudioBitrateKbps * 1000);

        if (Settings.AutoStartAudioWithStream && audioCapture is null)
            StartAudioCapture();
    }

    private bool TryRecoverFromDeviceRemoved(Exception ex)
    {
        if (ex is not SharpDX.SharpDXException sdx)
            return false;

        if (sdx.ResultCode != SharpDX.DXGI.ResultCode.DeviceRemoved
            && sdx.ResultCode != SharpDX.DXGI.ResultCode.DeviceReset
            && sdx.ResultCode != SharpDX.DXGI.ResultCode.DeviceHung)
            return false;

        if (device is not null)
        {
            SharpDX.ResultDescriptor reason = SharpDX.ResultDescriptor.Find(device.Device.DeviceRemovedReason);
            ServiceCollector.PluginLog.Warning($"ScreenShare GPU device removed reason: {reason}");
        }

        ServiceCollector.PluginLog.Warning(ex, "ScreenShare GPU device removed/reset - resetting so the next action recreates it.");
        previewTexture?.Dispose();
        previewTexture = null;
        lastOpenedViewerVideoHandle = null;
        DisposeStreamCapture();

        worldScreen.ReleaseDevice();
        device?.Dispose();
        device = null;
        return true;
    }

    private void DisposeStreamCapture()
    {
        screenCapture?.Dispose();
        screenCapture = null;
    }

    public void Dispose()
    {
        DisposeStep(roomSession.Dispose, nameof(roomSession));
        DisposeStep(mediaSession.Dispose, nameof(mediaSession));
        DisposeStep(Settings.Dispose, nameof(Settings));

        DisposeStep(
            () =>
            {
                previewTexture?.Dispose();
                previewTexture = null;
            },
            nameof(previewTexture));

        DisposeStep(DisposeStreamCapture, nameof(screenCapture));

        DisposeStep(ReleaseAudioCapture, nameof(audioCapture));

        DisposeStep(worldScreen.Dispose, nameof(worldScreen));

        DisposeStep(
            () =>
            {
                device?.Dispose();
                device = null;
            },
            nameof(device));
    }

    private static void DisposeStep(Action dispose, string what)
    {
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            ServiceCollector.PluginLog.Error(ex, $"ScreenShare failed to dispose {what} during unload - continuing with the rest");
        }
    }
}
