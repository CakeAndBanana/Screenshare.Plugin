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

    private bool autoStartedAudioCapture;
    private RawTextureWrap? previewTexture;
    private string? streamSetupError;

    private IAudioSource? audioCapture;
    private string? audioError;

    private readonly RoomSessionService roomSession = new();
    private readonly MediaSessionService mediaSession = new();

    private readonly WorldScreenController worldScreen = new();
    private readonly IdentityChip identity;
    private readonly LobbyView lobby;
    private readonly ConcurrentQueue<ActiveStreamerInfo> wentLive = new();
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
            });

        identity = new IdentityChip(roomSession, () => mediaSession.IsHosting);
        lobby = new LobbyView(roomSession, identity, Disconnect);
        roomSession.StreamerWentLive += streamer => wentLive.Enqueue(streamer);

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

    /// <summary>Brings the settings window up, from the title-bar cog, from Dalamud's own plugin
    /// gear, and from the two places a stream fails to start for want of a capture source.</summary>
    public void OpenSettings()
    {
        Settings.IsOpen = true;
        Settings.BringToFront();
    }

    /// <summary>Opens the in-world screen's own window, so its position can be adjusted without
    /// having to open a stream to get at the controls.</summary>
    public void OpenWorldScreen()
    {
        WorldScreen.IsOpen = true;
        WorldScreen.BringToFront();
    }

    public void TickFrame()
    {
        HandleViewerMediaLifecycle();

        // Raised on a SignalR thread; the toast and its click handler belong on this one.
        while (wentLive.TryDequeue(out ActiveStreamerInfo? streamer))
            NotifyWentLive(streamer);
    }

    // Reconnecting keeps the current screen: flipping to the connect card mid-room would shrink the
    // window for what is usually a couple of seconds. Same for hopping between the main relay and
    // the relay hosting a room, where the connection is down only for the hop itself.
    private Screen CurrentScreen => roomSession.State is HubConnectionState.Connected or HubConnectionState.Reconnecting || roomSession.IsSwitchingRelay
        ? roomSession.InRoom ? Screen.Room : Screen.Lobby
        : Screen.Connect;

    public override void PreDraw()
    {
        Screen screen = CurrentScreen;

        WindowName = screen == Screen.Room && roomSession.RoomName is { } roomName
            ? $"Screen Share · {roomName}{(mediaSession.IsHosting ? " · LIVE" : string.Empty)}###ScreenShareMainWindow"
            : "Screen Share###ScreenShareMainWindow";

        // The connect card sizes itself. Leaving it forces a normal size for one frame, then hands
        // the size back to the user.
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
        // The toast can outlive the stream, or the room.
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

    /// <summary>The room's name, code and visibility on a gradient in the room's colour, with who
    /// you are and Leave on the right. Sits above the tabs: it's navigation, not content.</summary>
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

        // mediaSession.IsHosting is included on its own (not just CanStartStreaming) so the
        // section never disappears out from under an already-running stream - e.g. if the host
        // revokes a co-streamer's permission mid-stream, that streamer can still see and click
        // "Stop", rather than losing the only way to stop what's still actively running.
        if (roomSession.CanStartStreaming || mediaSession.IsHosting)
            DrawStreamingSection();

        if (roomSession.CanPlaceScreen)
            DrawScreenPlacement();

        DrawMediaStreamStatus();

        // Errors stay on this tab rather than moving to the log with the events: they're the one
        // thing the user may need to act on, and burying them behind a tab switch would mean a
        // failed room action could go unnoticed entirely.
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

                // Follows new entries only while the user is already at the bottom, so scrolling
                // back through history isn't yanked away every time someone joins or leaves.
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

        // Whoever is live first - they're who people are looking for - then the host.
        IEnumerable<RoomMemberInfo> ordered = members
            .OrderByDescending(m => streamers.Any(s => s.ConnectionId == m.ConnectionId))
            .ThenByDescending(m => m.IsHost);

        foreach (RoomMemberInfo member in ordered)
            DrawMemberRow(member, members, streamers, watchers);
    }

    /// <summary>
    /// One member. A streamer's row gets the red line down its left edge and a red tint, their name
    /// turns green and carries a LIVE badge, and clicking the name watches them. Hovering the name
    /// shows their ID. The host's controls sit at the right end.
    /// </summary>
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
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.StreamingGreen);
            if (ImGui.Selectable($"{label}##name", isWatchingThis, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize(label).X, 0)))
            {
                if (isWatchingThis)
                    StopWatching();
                else
                    StartWatching(member.ConnectionId);
            }

            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextUnformatted(label);
        }

        if (ImGui.IsItemHovered())
            DrawMemberTooltip(member, isSelf, isStreaming, isWatchingThis, watchingName);

        if (isStreaming)
        {
            ImGui.SameLine();
            UiTheme.Pill("LIVE", UiTheme.LiveRed);
            if (isWatchingThis)
            {
                ImGui.SameLine();
                UiTheme.Pill("Watching", UiTheme.WatchingColor);
            }
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

        // A kick is a ban - they can't come back and the room disappears from their lists - so it
        // gets a second click rather than firing on a stray one.
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

    private void DrawMemberTooltip(RoomMemberInfo member, bool isSelf, bool isStreaming, bool isWatchingThis, string? watchingName)
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

        // Your own stream is watchable too, as a self-preview: it loops out to the relay and back
        // exactly like anyone else's, so it's the only way to see what you're actually sending.
        if (isStreaming)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(
                isWatchingThis ? "Click to stop watching."
                : isSelf ? "Click to preview your own stream.\n" +
                           "Turn audio capture off first - system loopback would record\n" +
                           "this preview's own playback and feed it back into the stream."
                : "Click to watch.");
        }

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

        // Says where the stream actually is, because with the popout closed and the panel in the
        // world this line is the only thing left explaining why audio is still playing.
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

    /// <summary>What "Go live" is about to share. Here rather than only in the settings window so
    /// an empty window pick is visible before the button is pressed, not after it fails.</summary>
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
            OpenSettings();
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

    /// <summary>For the host and anyone allowed to stream. Everyone else never places anything:
    /// the screen just appears for them once it's here.</summary>
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

        // Its own line under the buttons rather than trailing them: alongside, the longest of these
        // three messages ran the row past the window's width.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
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

        // Kicked, room closed, or the relay hosting the room went down - there's nothing left to
        // stream to, so don't keep capturing and encoding into a dead socket.
        if (!roomSession.InRoom && mediaSession.IsHosting)
        {
            mediaSession.StopHosting();
            DisposeStreamCapture();
            StopAutoStartedAudioCapture();
        }

        // The streamer currently being watched stopped, got kicked, or disconnected - don't hang
        // on a now-dead feed.
        if (mediaSession.IsViewing && mediaSession.ViewingStreamerConnectionId is { } watchingId
            && !roomSession.ActiveStreamers.Any(s => s.ConnectionId == watchingId))
        {
            StopWatching();
        }

        // The decoder's shared texture only exists once video metadata has round-tripped from
        // the streamer over the network, which happens well after StartViewing kicks off the
        // connection - so this has to be polled every frame rather than done once above.
        if (mediaSession.IsViewing && device is not null
            && mediaSession.ViewerSharedHandle is { } handle && handle != lastOpenedViewerVideoHandle)
        {
            previewTexture?.Dispose();
            previewTexture = SharedTextureBridge.OpenOnGameDevice(device.GameDevice, handle, mediaSession.ViewerWidth, mediaSession.ViewerHeight);
            lastOpenedViewerVideoHandle = handle;
        }

        // MediaSessionService's start-hosting/start-viewing failures happen on a background
        // Task, not synchronously from a button click, so they can never reach the catch blocks
        // in ToggleMediaStreaming that normally call TryRecoverFromDeviceRemoved - confirmed by a
        // real DeviceRemoved failure landing here instead of there, which would otherwise have
        // left the stale device in place until some unrelated button happened to trigger recovery
        // first. Checked separately for each independent role/connection - a device-removed
        // failure could in principle hit either the encoder or the decoder side.
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

        // Last, so it sees this frame's texture rather than the previous one's - and
        // unconditionally, because it also owns resetting the audio mix back to plain stereo on
        // every frame the panel ISN'T drawn.
        worldScreen.Update(device, previewTexture, roomSession, mediaSession);
    }

    private void StartWatching(string streamerConnectionId)
    {
        try
        {
            device ??= new GameAdapterDevice(ServiceCollector.PI.UiBuilder);
            string ffmpegPath = Path.Combine(ServiceCollector.PI.AssemblyLocation.DirectoryName!, "ffmpeg", "ffmpeg.exe");
            // Use roomSession.RelayUrl (the instance the SignalR connection is ACTUALLY on right
            // now), not the configured relay URL (what the user typed) - a cross-instance redirect
            // during JoinRoom can leave those pointing at different relays, and the media WebSocket
            // has to hit the same instance that owns the room's live connections.
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

            // Shown only when adaptation has actually pulled the bitrate down, so quality dropping
            // reads as a deliberate response to the uplink rather than as something going wrong.
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
        StopAutoStartedAudioCapture();
        roomSession.LeaveRoom();
    }

    private void ToggleAudioCapture()
    {
        if (audioCapture is not null)
        {
            StopAudioCapture();
            return;
        }

        // Started by hand, so a later "stop streaming" leaves it running.
        autoStartedAudioCapture = false;
        StartAudioCapture();
    }

    private void StartAudioCapture()
    {
        audioError = null;
        if (Settings.AudioSourceProcessId is { } targetProcessId)
            _ = StartProcessAudioCaptureAsync(targetProcessId);
        else
            StartSystemAudioCapture();
    }

    private void StopAudioCapture()
    {
        audioCapture?.Dispose();
        audioCapture = null;
        autoStartedAudioCapture = false;
        mediaSession.UpdateAudioSource(null);
    }

    private void StopAutoStartedAudioCapture()
    {
        if (autoStartedAudioCapture)
            StopAudioCapture();
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

    private async Task StartProcessAudioCaptureAsync(int processId)
    {
        try
        {
            ProcessAudioCapture capture = await ProcessAudioCapture.CreateAsync((uint)processId).ConfigureAwait(false);
            audioCapture = capture;
            mediaSession.UpdateAudioSource(capture, Settings.AudioBitrateKbps * 1000);
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
            StopAutoStartedAudioCapture();
            return;
        }

        if (roomSession.RoomId is not { } roomId || roomSession.ConnectionId is not { } connectionId)
            return;

        if (!Settings.HasCaptureSource)
        {
            // Put the picker in front of them rather than leaving a line of red text to act on -
            // window mode starts out unselected every session, so this is the ordinary first
            // "Go live" of a session, not an unusual failure.
            streamSetupError = "Pick a window to capture first.";
            OpenSettings();
            return;
        }

        try
        {
            streamSetupError = null;
            device ??= new GameAdapterDevice(ServiceCollector.PI.UiBuilder);

            // Always built fresh rather than reused. A capture only survives past a stream stopping
            // when a previous start FAILED after creating it - StartHosting below can throw - and
            // reusing that one silently ignores every capture setting changed since, so picking
            // Monitor after a failed Window attempt would carry on sharing the window.
            DisposeStreamCapture();
            screenCapture = Settings.CaptureWindowMode
                ? new WindowCapture(device, Settings.SelectedWindowHandle)
                : new DesktopDuplicationCapture(device, Settings.MonitorIndex);
        }
        catch (Exception ex)
        {
            streamSetupError = $"Screen capture init failed: {ex.Message}";
            ServiceCollector.PluginLog.Error(ex, "ScreenShare screen capture init failed");

            // A device-removed failure is about the GPU, and the reset above is the whole fix.
            // Anything else here is overwhelmingly the chosen monitor or window having gone away -
            // DesktopDuplicationCapture's message says as much - so open the picker.
            if (!TryRecoverFromDeviceRemoved(ex))
                OpenSettings();

            return;
        }

        string ffmpegPath = Path.Combine(ServiceCollector.PI.AssemblyLocation.DirectoryName!, "ffmpeg", "ffmpeg.exe");
        // See the matching comment in StartWatching - roomSession.RelayUrl reflects wherever the
        // control-plane connection actually ended up, which is what the media WebSocket must match.
        mediaSession.StartHosting(
            roomSession.RelayUrl ?? ServiceCollector.Config.RelayUrl, roomId, connectionId, device, screenCapture, audioCapture, ffmpegPath,
            Settings.TargetHeight, Settings.VideoBitrateKbps, Settings.VideoFpsTarget, Settings.Codec, Settings.AudioBitrateKbps * 1000);

        // Deliberately AFTER StartHosting. Starting audio first would hand StartHosting a snapshot
        // of the source taken moments earlier, and per-process capture activates asynchronously -
        // so whether the stream ended up with audio would come down to which of the two happened
        // to finish first. Coming in through UpdateAudioSource instead has no such ordering:
        // MediaSessionService records the source under a lock and wires it in as soon as the
        // stream's socket is up, whichever order the two arrive in.
        if (Settings.AutoStartAudioWithStream && audioCapture is null)
        {
            autoStartedAudioCapture = true;
            StartAudioCapture();
        }
    }

    private bool TryRecoverFromDeviceRemoved(Exception ex)
    {
        if (ex is not SharpDX.SharpDXException sdx)
            return false;

        if (sdx.ResultCode != SharpDX.DXGI.ResultCode.DeviceRemoved
            && sdx.ResultCode != SharpDX.DXGI.ResultCode.DeviceReset
            && sdx.ResultCode != SharpDX.DXGI.ResultCode.DeviceHung)
            return false;

        // The exception itself only says THAT some call failed because the device is gone, not
        // WHY it was removed in the first place. GetDeviceRemovedReason (still callable on an
        // already-removed device - that's its whole purpose) gives the real answer: DeviceHung
        // means a TDR timeout (a submitted command took too long, usually GPU overload), while
        // DriverInternalError/DeviceReset point at an actual driver-level fault rather than a
        // load problem. Logged here since this is the one place that sees every device-removed
        // failure regardless of which code path (sync button click or async media session
        // start) triggered it.
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

        // The panel renderer holds shaders and a constant buffer on the dead device; leaving them
        // behind would have the next frame draw against it and fail identically forever.
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

        DisposeStep(
            () =>
            {
                audioCapture?.Dispose();
                audioCapture = null;
            },
            nameof(audioCapture));

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
