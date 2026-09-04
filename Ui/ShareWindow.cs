using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Capture.AudioCapture;
using ScreenShare.Plugin.Capture.ScreenCapture;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

public sealed class ShareWindow : Window, IDisposable
{
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 WarnColor = new(1f, 0.7f, 0.2f, 1f);
    private static readonly Vector4 CrownColor = new(1f, 0.85f, 0.2f, 1f);
    private static readonly Vector4 StreamingColor = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 PermittedColor = new(0.4f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 WatchingColor = new(0.4f, 0.75f, 1f, 1f);

    private GameAdapterDevice? device;
    private ICaptureSource? screenCapture;

    /// <summary>True only while the CURRENT audio capture was started by streaming rather than by
    /// the user clicking the audio button. Stopping the stream undoes an auto-start, but must
    /// leave a hand-started capture alone - that was an explicit choice, not a side effect.</summary>
    private bool autoStartedAudioCapture;
    private RawTextureWrap? previewTexture;
    private string? streamSetupError;

    private IAudioSource? audioCapture;
    private string? audioError;

    private readonly RoomSessionService roomSession = new();
    private readonly MediaSessionService mediaSession = new();

    /// <summary>Projects the watched stream onto a panel in the game world, and points the audio
    /// at the virtual speakers flanking it. Lives here rather than in StreamViewWindow because it
    /// needs the device and decoded texture ShareWindow owns; StreamViewWindow only supplies the
    /// controls for it.</summary>
    private readonly WorldScreenController worldScreen = new();
    private string relayUrlInput = ServiceCollector.Config.RelayUrl;
    private string displayNameInput = ServiceCollector.Config.DisplayName;
    private string createPasswordInput = "";
    private int maxViewersInput = 4;
    private string joinRoomIdInput = "";
    private string joinPasswordInput = "";
    private bool wasViewerInRoom;
    private nint? lastOpenedViewerVideoHandle;
    private Exception? lastHandledHostMediaException;
    private Exception? lastHandledViewerMediaException;

    /// <summary>Watching a stream happens in a separate popout window rather than inline here, so
    /// it can stay visible (or be moved/resized) independently of the room UI - this is also the
    /// natural seam for an eventual in-world "beamer" projection to grow into later. ShareWindow
    /// still owns the whole watch-session lifecycle (device, decoded texture, MediaSessionService);
    /// StreamView just renders what it's given and reports back when the user closes it.</summary>
    public StreamViewWindow StreamView { get; }

    /// <summary>Streaming settings, as their own window rather than embedded in the room screen -
    /// reachable regardless of connection/room state, and registered as the plugin's config UI in
    /// Plugin.cs (Dalamud flags a plugin that never wires UiBuilder.OpenConfigUi). ShareWindow
    /// still owns the actual capture/audio session objects; this window only owns the settings UI
    /// and picker enumeration, consumed here via its public properties whenever a stream starts.</summary>
    public SettingsWindow Settings { get; }

    public ShareWindow() : base("Screen Share###ScreenShareMainWindow")
    {
        Size = new Vector2(560, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        StreamView = new StreamViewWindow(roomSession, mediaSession, () => previewTexture, () => worldScreen, StopWatching);
        Settings = new SettingsWindow(
            isStreaming: () => screenCapture is not null,
            isAudioCapturing: () => audioCapture is not null,
            applyLiveAudioBitrate: bps =>
            {
                if (audioCapture is not null)
                    mediaSession.UpdateAudioSource(audioCapture, bps);
            });
    }

    /// <summary>Per-frame work that must happen whether or not any of this plugin's windows are
    /// open, wired to UiBuilder.Draw in Plugin.cs rather than living in <see cref="Draw"/>.
    ///
    /// Dalamud only calls Draw() on an OPEN window, and "set the screen up, then close the UI and
    /// watch" is the normal way to use the in-world projection - so leaving this in Draw() meant
    /// the panel vanished and the audio mix froze the moment the user tidied the window away. It
    /// also keeps the popout's decoded texture current in that same situation, which it previously
    /// wasn't.</summary>
    public void TickFrame() => HandleViewerMediaLifecycle();

    public override void Draw()
    {
        if (ImGui.Button("Settings"))
        {
            Settings.IsOpen = true;
            Settings.BringToFront();
        }

        ImGui.Separator();

        if (roomSession.State != HubConnectionState.Connected)
        {
            DrawConnectScreen();
            return;
        }

        if (!roomSession.InRoom)
        {
            DrawLobbyScreen();
            return;
        }

        DrawRoomScreen();
    }

    private void DrawConnectScreen()
    {
        ImGui.TextUnformatted("Connect to a relay server to start or join a share.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Display name", ref displayNameInput, 64))
        {
            ServiceCollector.Config.DisplayName = displayNameInput;
            ServiceCollector.PI.SavePluginConfig(ServiceCollector.Config);
        }

        ImGui.SetNextItemWidth(320);
        ImGui.InputText("Relay URL", ref relayUrlInput, 256);

        if (ImGui.Button("Connect"))
        {
            ServiceCollector.Config.RelayUrl = relayUrlInput;
            ServiceCollector.PI.SavePluginConfig(ServiceCollector.Config);
            roomSession.Connect(relayUrlInput);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(roomSession.ConnectionStatus);

        if (roomSession.LastError is not null)
            ImGui.TextColored(ErrorColor, $"Relay error: {roomSession.LastError}");
    }

    private void DrawLobbyScreen()
    {
        ImGui.TextUnformatted($"Connected as \"{ServiceCollector.Config.DisplayName}\" - {relayUrlInput}");
        ImGui.Separator();

        ImGui.TextUnformatted("Create a room");
        ImGui.SetNextItemWidth(150);
        ImGui.InputText("Password##create", ref createPasswordInput, 128, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("Max viewers", ref maxViewersInput);
        ImGui.SameLine();
        if (ImGui.Button("Create Room"))
            roomSession.CreateRoom(createPasswordInput, Math.Max(1, maxViewersInput));

        ImGui.Spacing();
        ImGui.TextUnformatted("Join a room");
        ImGui.SetNextItemWidth(100);
        ImGui.InputText("Room code##join", ref joinRoomIdInput, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.InputText("Password##join", ref joinPasswordInput, 128, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        if (ImGui.Button("Join Room"))
            roomSession.JoinRoom(joinRoomIdInput, joinPasswordInput);

        if (roomSession.LastEvent is not null)
            ImGui.TextUnformatted($"Last event: {roomSession.LastEvent}");
        if (roomSession.LastError is not null)
            ImGui.TextColored(ErrorColor, $"Relay error: {roomSession.LastError}");
    }

    private void DrawRoomScreen()
    {
        // Room identity and Leave stay ABOVE the tab bar: they're navigation rather than content,
        // and putting "Leave Room" inside a tab would hide it behind whichever one is selected.
        ImGui.TextUnformatted($"Room {roomSession.RoomId} - {(roomSession.IsHost ? "Hosting" : "Viewing")}");
        ImGui.SameLine();
        if (ImGui.Button("Leave Room"))
            LeaveRoom();

        if (roomSession.InstanceId is { } instanceId)
            ImGui.TextDisabled($"Relay instance: {instanceId} ({roomSession.RelayUrl})");

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

    private void DrawRoomTab()
    {
        DrawMemberList();
        DrawWatchingStatus();
        ImGui.Separator();

        // mediaSession.IsHosting is included on its own (not just CanStartStreaming) so the
        // section never disappears out from under an already-running stream - e.g. if the host
        // revokes a co-streamer's permission mid-stream, that streamer can still see and click
        // "Stop", rather than losing the only way to stop what's still actively running.
        if (roomSession.CanStartStreaming || mediaSession.IsHosting)
        {
            DrawStreamingSection();
            ImGui.Separator();
        }

        DrawMediaStreamStatus();

        // Errors stay on this tab rather than moving to the log with the events: they're the one
        // thing the user may need to act on, and burying them behind a tab switch would mean a
        // failed room action could go unnoticed entirely.
        if (roomSession.LastError is not null)
            ImGui.TextColored(ErrorColor, $"Relay error: {roomSession.LastError}");
    }

    /// <summary>The room's event history, on its own tab. The room view used to end with a single
    /// "Last event:" line, which only ever showed the most recent one - anything that happened
    /// while you were looking at something else was simply gone by the time you looked back.</summary>
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

    /// <summary>Renders every current room member (host included) with icon badges - a crown for
    /// the owner, a camera for whoever is actively streaming right now, an eye for whoever is
    /// currently watching a stream (hovering shows which one), and a "permitted" badge for anyone
    /// else who's been granted streaming permission but isn't live at the moment. A member who's
    /// actively streaming (and isn't yourself) has a clickable name that opens <see cref="StreamView"/>
    /// on their feed - hosting and viewing run on independent MediaSessionService connections, so
    /// this stays clickable even while you're streaming your own feed. Kick/grant/revoke buttons
    /// only render for the host, and never against their own row.</summary>
    private void DrawMemberList()
    {
        ImGui.TextUnformatted("Members:");
        foreach (RoomMemberInfo member in roomSession.Members)
        {
            bool isSelf = member.ConnectionId == roomSession.ConnectionId;
            bool isStreaming = roomSession.ActiveStreamers.Any(s => s.ConnectionId == member.ConnectionId);

            // Your own stream is watchable too, as a self-preview: it loops out to the relay and
            // back exactly like anyone else's, so it's the only way to see what you're actually
            // sending - and the only way to exercise the whole viewer path (decode, popout,
            // in-world panel) without a second game client. The relay never restricted this; it
            // was only ever this line.
            bool isWatchable = isStreaming;

            string? watchingDisplayName = null;
            if (roomSession.Watchers.FirstOrDefault(w => w.WatcherConnectionId == member.ConnectionId) is { } watching)
            {
                watchingDisplayName = roomSession.Members.FirstOrDefault(m => m.ConnectionId == watching.StreamerConnectionId)?.DisplayName ?? "someone";
            }

            DrawMemberIcons(member, isStreaming, watchingDisplayName);

            if (isWatchable)
            {
                bool isWatchingThis = mediaSession.IsViewing && mediaSession.ViewingStreamerConnectionId == member.ConnectionId;
                if (ImGui.Selectable($"{member.DisplayName}##member_{member.ConnectionId}", isWatchingThis, ImGuiSelectableFlags.None, new Vector2(150, 0)))
                {
                    if (isWatchingThis)
                        StopWatching();
                    else
                        StartWatching(member.ConnectionId);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        isWatchingThis ? "Click to stop watching"
                        : isSelf ? "Click to preview your own stream.\n" +
                                   "Turn audio capture off first - system loopback would record " +
                                   "this preview's own playback and feed it back into the stream."
                        : "Click to watch");
                }
            }
            else
            {
                ImGui.TextUnformatted(member.DisplayName);
            }

            if (roomSession.IsHost && !isSelf)
            {
                ImGui.SameLine();
                if (ImGui.Button($"Kick##{member.ConnectionId}"))
                    roomSession.KickViewer(member.ConnectionId);

                ImGui.SameLine();
                if (member.CanStream)
                {
                    if (ImGui.Button($"Revoke streaming##{member.ConnectionId}"))
                        roomSession.RevokeStreamPermission(member.ConnectionId);
                }
                else if (ImGui.Button($"Allow streaming##{member.ConnectionId}"))
                {
                    roomSession.GrantStreamPermission(member.ConnectionId);
                }
            }
        }
    }

    private static void DrawMemberIcons(RoomMemberInfo member, bool isStreaming, string? watchingDisplayName)
    {
        bool any = false;
        using (ServiceCollector.PI.UiBuilder.IconFontHandle.Push())
        {
            if (member.IsHost)
            {
                ImGui.TextColored(CrownColor, FontAwesomeIcon.Crown.ToIconString());
                any = true;
            }

            if (isStreaming)
            {
                if (any)
                    ImGui.SameLine(0, 4);
                ImGui.TextColored(StreamingColor, FontAwesomeIcon.Camera.ToIconString());
                any = true;
            }
            else if (member.CanStream && !member.IsHost)
            {
                if (any)
                    ImGui.SameLine(0, 4);
                ImGui.TextColored(PermittedColor, FontAwesomeIcon.UserCheck.ToIconString());
                any = true;
            }

            if (watchingDisplayName is not null)
            {
                if (any)
                    ImGui.SameLine(0, 4);
                ImGui.TextColored(WatchingColor, FontAwesomeIcon.Eye.ToIconString());
                any = true;

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Watching: {watchingDisplayName}");
            }
        }

        if (any)
            ImGui.SameLine(0, 6);
    }

    /// <summary>One-line status shown while watching someone, mainly for discoverability -
    /// <see cref="StreamView"/> can end up behind the game window or minimized, so this gives a
    /// way to bring it back (or stop watching) without hunting for it.</summary>
    private void DrawWatchingStatus()
    {
        if (!mediaSession.IsViewing)
            return;

        string displayName = roomSession.Members.FirstOrDefault(m => m.ConnectionId == mediaSession.ViewingStreamerConnectionId)?.DisplayName ?? "someone";
        ImGui.TextUnformatted($"Watching {displayName} in a popout window.");
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

    /// <summary>Start/stop streaming and audio capture - the actual streaming SETTINGS
    /// (capture source, resolution/bitrate/fps/codec, audio source/bitrate) live in
    /// <see cref="Settings"/> now, reachable via the button at the top of this window regardless
    /// of room state, rather than a collapsed section buried in the room screen.</summary>
    private void DrawStreamingSection()
    {
        if (ImGui.Button(mediaSession.IsHosting ? "Stop streaming to room" : "Start streaming to room"))
            ToggleMediaStreaming();

        ImGui.SameLine();
        if (ImGui.Button(audioCapture is null ? "Start audio capture" : "Stop audio capture"))
            ToggleAudioCapture();

        if (audioError is not null)
            ImGui.TextColored(ErrorColor, $"Audio capture failed: {audioError}");
        if (streamSetupError is not null)
            ImGui.TextColored(ErrorColor, streamSetupError);

        DrawScreenCaptureStatus();
        DrawAudioStatus();
    }

    /// <summary>Surfaces failures from the capture THREAD, which streamSetupError above cannot
    /// reach - it only ever sees construction failures. A capture that starts cleanly and then dies
    /// (a monitor whose duplication is lost and can't be rebuilt, most often) previously produced
    /// no message at all: streaming reported itself as running, no frames flowed, and nothing in
    /// the UI said why. The frame count is shown alongside because zero frames with no error is
    /// itself the diagnosis - the capture is alive but the desktop isn't handing it anything.</summary>
    private void DrawScreenCaptureStatus()
    {
        if (screenCapture is null)
            return;

        if (screenCapture.LastError is { } captureError)
            ImGui.TextColored(WarnColor, $"Screen capture error: {captureError.Message}");

        if (screenCapture.FramesCaptured == 0)
        {
            ImGui.TextColored(WarnColor,
                "No frames captured yet - if this doesn't move, the selected monitor isn't producing frames.");
        }
        else
        {
            ImGui.TextDisabled($"{screenCapture.FramesCaptured} frames captured ({screenCapture.Width}x{screenCapture.Height})");
        }
    }

    private void DrawAudioStatus()
    {
        if (audioCapture is null)
            return;

        NAudio.Wave.WaveFormat format = audioCapture.WaveFormat;
        ImGui.TextUnformatted($"System audio: {format.SampleRate} Hz, {format.Channels}ch");
        ImGui.TextUnformatted("Level:");
        ImGui.SameLine();
        ImGui.ProgressBar(Math.Clamp(audioCapture.CurrentPeak, 0f, 1f), new Vector2(200, 0), "");

        if (audioCapture.LastError is not null)
            ImGui.TextColored(WarnColor, $"Audio capture error: {audioCapture.LastError.Message}");
    }

    /// <summary>Per-frame room/media bookkeeping for a viewer: stops watching when leaving the
    /// room, detects the currently-watched streamer going away, swaps in the decoded preview
    /// texture once it exists, and surfaces device-removed recovery. Does NOT auto-start viewing
    /// on join any more - a room can have more than one active streamer at once, so the viewer
    /// has to explicitly click a streaming member's name in <see cref="DrawMemberList"/> to watch,
    /// even when only one is live.</summary>
    private void HandleViewerMediaLifecycle()
    {
        bool isViewerInRoom = roomSession.InRoom && !roomSession.IsHost;
        if (!isViewerInRoom && wasViewerInRoom)
            StopWatching();

        wasViewerInRoom = isViewerInRoom;

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
            // now), not relayUrlInput (what the user originally typed) - a cross-instance redirect
            // during JoinRoom can leave those pointing at different relays, and the media WebSocket
            // has to hit the same instance that owns the room's live connections.
            mediaSession.StartViewing(roomSession.RelayUrl ?? relayUrlInput, roomSession.RoomId!, roomSession.ConnectionId!, streamerConnectionId, device, ffmpegPath);
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
            ImGui.TextColored(ErrorColor, $"Media stream error: {mediaSession.HostLastError}");

        if (mediaSession.IsHosting)
        {
            ImGui.TextUnformatted(
                $"Streaming: {mediaSession.HostFramesEncoded} encoded / {mediaSession.HostFramesSkipped} skipped " +
                $"({mediaSession.HostAverageFps:F1} fps avg), {mediaSession.HostEncodedBytes} bytes sent " +
                $"({mediaSession.HostVendor}, {mediaSession.HostCodec})");

            if (mediaSession.HostQueueDrops > 0)
            {
                ImGui.TextColored(WarnColor,
                    $"{mediaSession.HostQueueDrops} video chunks dropped before sending - upload can't keep up with the configured bitrate.");
            }

            if (mediaSession.HostQueueAudioDrops > 0)
            {
                ImGui.TextColored(WarnColor,
                    $"{mediaSession.HostQueueAudioDrops} audio packets dropped before sending - upload can't keep up even with audio's own queue.");
            }

            if (mediaSession.HostVideoError is { } hostVideoError)
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"Video encoder died mid-stream: {hostVideoError.Message}");
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

    /// <summary>Undoes an auto-start when streaming ends, and does nothing at all otherwise -
    /// including when auto-start is enabled but the user had already turned audio on themselves
    /// before hitting stream.</summary>
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

    /// <summary>Async because activating process-loopback capture goes through WASAPI's
    /// ActivateAudioInterfaceAsync, which is inherently asynchronous (a COM completion callback,
    /// not a blocking call) - firing this and returning immediately keeps the render thread from
    /// ever waiting on it, matching how every other network/media start already works here.</summary>
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

    /// <summary>NAudio's WASAPI/COM interop can throw an InvalidCastException where both the
    /// source and target type names in the message are IDENTICAL (e.g. "cannot cast
    /// MMDeviceEnumeratorComObject to MMDeviceEnumeratorComObject") - confirmed as a real
    /// occurrence, not hypothetical. This is the classic .NET signature for two separate copies
    /// of the same assembly being loaded into the process at once, most likely here from a
    /// dev-plugin reload leaving a stale AssemblyLoadContext around from a previous load (COM
    /// interop objects are a common reason a load context fails to fully unload). No amount of
    /// retrying or picking a different capture source fixes this - the stale assembly is either
    /// loaded or it isn't - so this exists purely to replace an extremely confusing raw exception
    /// message with the one thing that actually helps: restart the whole game, not just reload
    /// the plugin.</summary>
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

        if (Settings.CaptureWindowMode && Settings.SelectedWindowHandle == 0)
        {
            streamSetupError = "Pick a window to capture first.";
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
            TryRecoverFromDeviceRemoved(ex);
            return;
        }

        string ffmpegPath = Path.Combine(ServiceCollector.PI.AssemblyLocation.DirectoryName!, "ffmpeg", "ffmpeg.exe");
        // See the matching comment in StartWatching - roomSession.RelayUrl reflects wherever the
        // control-plane connection actually ended up, which is what the media WebSocket must match.
        mediaSession.StartHosting(
            roomSession.RelayUrl ?? relayUrlInput, roomId, connectionId, device, screenCapture, audioCapture, ffmpegPath,
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

    /// <summary>DXGI_ERROR_DEVICE_REMOVED (and Reset/Hung) mean the GPU device is permanently
    /// unusable - every further call against it fails identically. Without this, every button
    /// would keep failing the same way until the plugin was reloaded; instead we tear down
    /// everything tied to the dead device so the next action recreates it fresh.</summary>
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

    /// <summary>Every step is isolated deliberately. This used to be a bare sequence, so one
    /// throwing teardown (a network session tearing down mid-unload is the realistic one) skipped
    /// everything after it - and DisposeStreamCapture being one of those made the cost permanent
    /// rather than cosmetic: a stranded OutputDuplication blocks every future capture of that
    /// monitor for the whole game session, with no way back short of a restart. See
    /// DesktopDuplicationCapture's class doc comment.</summary>
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
