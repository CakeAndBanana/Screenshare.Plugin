using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Services;

/// <summary>One entry in a room's event history - see <see cref="RoomSessionService.Events"/>.
/// Timestamped in local time because its only consumer is the UI, which shows it to a human
/// sitting at this machine.</summary>
public readonly record struct RoomEvent(DateTime TimestampLocal, string Message);

/// <summary>
/// Owns the relay connection and room state for the UI. Every public action method here fires
/// its async work and returns immediately - safe to call directly from Draw(). Status is
/// exposed via plain fields the UI polls each frame; a frame or two of staleness is invisible
/// to a human, so this favors simplicity over strict memory-visibility guarantees. The one thing
/// that must never happen is a blocking wait on the render thread, which this design avoids by
/// construction (no method here returns a Task the caller is expected to await).
/// </summary>
public sealed class RoomSessionService : IDisposable
{
    private readonly RoomHubClient client = new();
    private readonly Lock membersLock = new();
    private readonly List<RoomMemberInfo> members = [];
    private readonly Lock activeStreamersLock = new();
    private List<ActiveStreamerInfo> activeStreamers = [];
    private readonly Lock watchersLock = new();
    private List<WatcherInfo> watchers = [];

    private const int MaxLoggedEvents = 200;
    private readonly Lock eventsLock = new();
    private readonly List<RoomEvent> events = [];

    // Cached so a silent SignalR reconnect (see the Reconnected handler below) can replay the
    // exact same JoinRoom call - the relay's own reconnect-detection path (RoomState.
    // TryReconnectParticipant) skips the password check for a genuine reconnect, but the RPC
    // signature still requires passing something, and there's no other place this client keeps
    // the password around long enough to survive past the initial Create/Join button click.
    private string? lastRoomPassword;

    /// <summary>False until the first active-streamer list of this room has been seen - see
    /// <see cref="RecordStreamerChanges"/>. Reset when leaving so a later room starts fresh.</summary>
    private bool hasStreamerBaseline;

    /// <summary>The watcher-list equivalent of <see cref="hasStreamerBaseline"/>.</summary>
    private bool hasWatcherBaseline;

    public string ConnectionStatus { get; private set; } = "Disconnected";

    public HubConnectionState State => client.State;

    public string? ConnectionId => client.ConnectionId;

    /// <summary>Which relay instance this client is actually talking to right now - always
    /// populated once CreateRoom/JoinRoom has succeeded at least once (a single-instance
    /// deployment just always reports the same one). Surfaced in the UI so the user can see which
    /// physical relay process they ended up on, e.g. after a multi-instance redirect.</summary>
    public string? InstanceId => client.InstanceId;

    /// <summary>The relay URL this client is currently connected to - may differ from what the
    /// user originally typed into the Connect screen if JoinRoom had to follow a cross-instance
    /// redirect.</summary>
    public string? RelayUrl => client.RelayUrl;

    public string? RoomId { get; private set; }

    public bool IsHost { get; private set; }

    public bool InRoom => RoomId is not null;

    /// <summary>Whether THIS client has been granted permission to stream - distinct from
    /// <see cref="IsHost"/> (the owner can always stream) and from any other member's own
    /// permission (tracked per-entry in <see cref="Members"/>).</summary>
    public bool CanStream { get; private set; }

    public bool CanStartStreaming => IsHost || CanStream;

    /// <summary>Where the room's shared in-world screen is placed, or null if nobody has placed
    /// one. Read every frame by the render path, written from a SignalR callback thread, so it's a
    /// plain reference field holding an immutable record - a torn read is impossible and a frame of
    /// staleness is invisible, matching how the rest of this class exposes state.</summary>
    public ScreenPlacementInfo? ScreenPlacement { get; private set; }

    /// <summary>Whether this client may place or move the shared screen. Deliberately the same
    /// permission as streaming (the relay authorizes UpdateScreenPlacement against CanStream, not
    /// host-only) - arranging the screen is part of running a viewing session.</summary>
    public bool CanPlaceScreen => CanStartStreaming;

    public string? LastError { get; private set; }

    /// <summary>The most recent entry of <see cref="Events"/>, kept as its own property for the
    /// connect/lobby screens, which have no room for a history and only ever wanted the latest
    /// line.</summary>
    public string? LastEvent { get; private set; }

    /// <summary>The room's event history, oldest first, capped at <see cref="MaxLoggedEvents"/>.
    /// Snapshotted under a lock on every read for the same reason Members/Watchers are: entries
    /// are appended from SignalR callbacks on background threads and read from the render thread.</summary>
    public IReadOnlyList<RoomEvent> Events
    {
        get
        {
            lock (eventsLock)
                return [.. events];
        }
    }

    public void ClearEvents()
    {
        lock (eventsLock)
            events.Clear();
    }

    /// <summary>Single funnel for everything that reaches the event log, so a new event source
    /// can't update the latest line without also landing in the history.</summary>
    /// <summary>Turns the relay's full active-streamer list into "started"/"stopped" events.
    ///
    /// The relay broadcasts the whole list rather than a delta - and re-sends it on join and on
    /// reconnect - so who actually changed has to be worked out here. The first list after joining
    /// is treated as a baseline rather than a set of transitions: those people were already live
    /// before we arrived, and announcing them as having just started would be wrong.</summary>
    private void RecordStreamerChanges(List<ActiveStreamerInfo> previous, IReadOnlyCollection<ActiveStreamerInfo> current)
    {
        foreach (ActiveStreamerInfo streamer in current)
        {
            if (previous.Any(p => p.ConnectionId == streamer.ConnectionId))
                continue;

            RecordEvent(hasStreamerBaseline
                ? $"{streamer.DisplayName} started streaming"
                : $"{streamer.DisplayName} is already streaming");
        }

        foreach (ActiveStreamerInfo streamer in previous)
        {
            if (!current.Any(c => c.ConnectionId == streamer.ConnectionId))
                RecordEvent($"{streamer.DisplayName} stopped streaming");
        }

        hasStreamerBaseline = true;
    }

    /// <summary>Same shape as <see cref="RecordStreamerChanges"/>, over the watcher list: a full
    /// snapshot each time, so transitions are derived by diffing.
    ///
    /// Keyed on the WATCHER rather than the pair, so someone switching directly from one streamer
    /// to another reads as a single "started watching" line instead of a stop immediately followed
    /// by a start.</summary>
    private void RecordWatcherChanges(List<WatcherInfo> previous, IReadOnlyCollection<WatcherInfo> current)
    {
        foreach (WatcherInfo watcher in current)
        {
            WatcherInfo? before = previous.FirstOrDefault(p => p.WatcherConnectionId == watcher.WatcherConnectionId);
            if (before is not null && before.StreamerConnectionId == watcher.StreamerConnectionId)
                continue;

            string who = DisplayNameFor(watcher.WatcherConnectionId);
            string whom = DisplayNameFor(watcher.StreamerConnectionId);
            RecordEvent(hasWatcherBaseline
                ? $"{who} started watching {whom}"
                : $"{who} is already watching {whom}");
        }

        foreach (WatcherInfo watcher in previous)
        {
            if (!current.Any(c => c.WatcherConnectionId == watcher.WatcherConnectionId))
                RecordEvent($"{DisplayNameFor(watcher.WatcherConnectionId)} stopped watching");
        }

        hasWatcherBaseline = true;
    }

    /// <summary>Falls back to "someone" rather than showing a raw connection id - these broadcasts
    /// can arrive fractionally before the member list that explains who the id belongs to.</summary>
    private string DisplayNameFor(string connectionId)
    {
        lock (membersLock)
            return members.FirstOrDefault(m => m.ConnectionId == connectionId)?.DisplayName ?? "someone";
    }

    /// <summary>Adds a room to the recent list, newest first. Recorded only on SUCCESS - a code
    /// that was rejected or a password that was wrong is not worth offering back as a shortcut.
    ///
    /// Keyed on relay URL AND code together, because a code only means anything on the relay that
    /// issued it. Re-recording an existing entry moves it to the front rather than duplicating it.</summary>
    private static void RememberRoom(string roomId, string password, bool wasHost)
    {
        const int maxRemembered = 8;

        Configuration config = ServiceCollector.Config;
        string relayUrl = ServiceCollector.Config.RelayUrl;

        config.RecentRooms.RemoveAll(r =>
            string.Equals(r.RoomId, roomId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.RelayUrl, relayUrl, StringComparison.OrdinalIgnoreCase));

        config.RecentRooms.Insert(0, new RecentRoom
        {
            RelayUrl = relayUrl,
            RoomId = roomId,
            Password = config.RememberRoomPasswords ? password : null,
            WasHost = wasHost,
            LastJoinedUtc = DateTime.UtcNow,
        });

        if (config.RecentRooms.Count > maxRemembered)
            config.RecentRooms.RemoveRange(maxRemembered, config.RecentRooms.Count - maxRemembered);

        ServiceCollector.PI.SavePluginConfig(config);
    }

    private void RecordEvent(string message)
    {
        LastEvent = message;

        lock (eventsLock)
        {
            events.Add(new RoomEvent(DateTime.Now, message));

            // Trimmed from the front rather than left to grow: a long session in a busy room would
            // otherwise hold every join/leave it ever saw, for no benefit - the UI only scrolls
            // back so far anyway.
            if (events.Count > MaxLoggedEvents)
                events.RemoveRange(0, events.Count - MaxLoggedEvents);
        }
    }

    /// <summary>Every current room member, host included - <see cref="RoomMemberInfo.IsHost"/>
    /// and <see cref="RoomMemberInfo.CanStream"/> tell the UI which icon(s)/actions apply to
    /// each row.</summary>
    public IReadOnlyList<RoomMemberInfo> Members
    {
        get
        {
            lock (membersLock)
                return [.. members];
        }
    }

    public IReadOnlyList<ActiveStreamerInfo> ActiveStreamers
    {
        get
        {
            lock (activeStreamersLock)
                return [.. activeStreamers];
        }
    }

    /// <summary>Every current watcher-to-streamer pairing in the room - lets the UI show an eye
    /// icon next to whoever's currently watching a stream, and (via a lookup on WatcherConnectionId)
    /// which streamer's feed they're on.</summary>
    public IReadOnlyList<WatcherInfo> Watchers
    {
        get
        {
            lock (watchersLock)
                return [.. watchers];
        }
    }

    public RoomSessionService()
    {
        // Sent once, right after a successful join, with everyone already in the room (host
        // included) - CreateRoom doesn't get one (nothing to snapshot yet but yourself), so that
        // path seeds `members` itself instead, see CreateRoomCoreAsync.
        client.RoomMembersSnapshot += snapshot =>
        {
            lock (membersLock)
            {
                members.Clear();
                members.AddRange(snapshot);
            }
        };

        client.MemberJoined += member =>
        {
            lock (membersLock)
                members.Add(member);
            RecordEvent($"{member.DisplayName} joined");
        };

        client.MemberLeft += id =>
        {
            string? displayName;
            lock (membersLock)
            {
                displayName = members.FirstOrDefault(m => m.ConnectionId == id)?.DisplayName;
                members.RemoveAll(m => m.ConnectionId == id);
            }
            RecordEvent(displayName is not null ? $"{displayName} left" : "A member left");
        };

        client.Kicked += () =>
        {
            RecordEvent("You were removed from the room by the host.");
            ResetRoomState();
        };

        client.RoomClosed += () =>
        {
            RecordEvent("Room closed (host disconnected).");
            ResetRoomState();
        };

        client.ActiveStreamersChanged += streamers =>
        {
            List<ActiveStreamerInfo> previous;
            lock (activeStreamersLock)
            {
                previous = activeStreamers;
                activeStreamers = [.. streamers];
            }

            RecordStreamerChanges(previous, streamers);
        };

        client.WatchersChanged += newWatchers =>
        {
            List<WatcherInfo> previous;
            lock (watchersLock)
            {
                previous = watchers;
                watchers = [.. newWatchers];
            }

            RecordWatcherChanges(previous, newWatchers);
        };

        client.ViewerStreamPermissionChanged += (targetConnectionId, granted) =>
        {
            if (targetConnectionId == ConnectionId)
                CanStream = granted;

            lock (membersLock)
            {
                int index = members.FindIndex(m => m.ConnectionId == targetConnectionId);
                if (index >= 0)
                    members[index] = members[index] with { CanStream = granted };
            }
        };

        client.ScreenPlacementChanged += placement =>
        {
            // Drop a broadcast that lost a race against a newer local edit - two people dragging
            // the same slider at once would otherwise fight, each echo yanking the screen back to
            // the other's stale position. Equal versions still apply: that's the relay confirming
            // our own write, and taking it keeps us aligned with what the server actually stored.
            if (placement is not null && ScreenPlacement is { } current && placement.Version < current.Version)
                return;

            ScreenPlacement = placement;
            RecordEvent(placement is null ? "The in-world screen was cleared." : "The in-world screen was moved.");
        };

        client.Reconnecting += () => ConnectionStatus = "Reconnecting...";

        // SignalR's automatic reconnect only restores the raw transport - it gives this
        // connection a brand new ConnectionId and does nothing to re-register it with the room
        // server-side (SignalR group membership, this relay's own participant/host tracking).
        // Without explicitly rejoining here, this client would look "Connected" again but
        // silently stop receiving any room broadcasts at all (new members, permission changes,
        // etc.) - see RoomHub.JoinRoom's reconnect-detection path, which a JoinRoom call with the
        // same ClientId triggers automatically.
        client.Reconnected += () =>
        {
            ConnectionStatus = "Connected";
            if (RoomId is not null)
                _ = RejoinAfterReconnectAsync();
        };
    }

    private async Task RejoinAfterReconnectAsync()
    {
        if (RoomId is not { } roomId)
            return;

        try
        {
            JoinRoomResult result = await client.JoinRoomAsync(
                roomId, lastRoomPassword ?? string.Empty, ServiceCollector.Config.ClientId, ServiceCollector.Config.DisplayName).ConfigureAwait(false);

            if (result.Success)
            {
                RecordEvent("Reconnected to the relay.");
            }
            else
            {
                LastError = $"Lost connection and couldn't automatically rejoin the room: {result.Error}";
                ResetRoomState();
            }
        }
        catch (Exception ex)
        {
            LastError = $"Lost connection and couldn't automatically rejoin the room: {ex.Message}";
            ResetRoomState();
        }
    }

    public void Connect(string relayUrl)
    {
        LastError = null;
        ConnectionStatus = "Connecting...";
        _ = ConnectCoreAsync(relayUrl);
    }

    private async Task ConnectCoreAsync(string relayUrl)
    {
        try
        {
            await client.ConnectAsync(relayUrl).ConfigureAwait(false);
            ConnectionStatus = "Connected";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Disconnected";
            LastError = ex.Message;
        }
    }

    public void CreateRoom(string password, int maxViewers)
    {
        LastError = null;
        lastRoomPassword = password;
        _ = CreateRoomCoreAsync(password, maxViewers);
    }

    private async Task CreateRoomCoreAsync(string password, int maxViewers)
    {
        try
        {
            CreateRoomResult result = await client.CreateRoomAsync(
                password, maxViewers, ServiceCollector.Config.ClientId, ServiceCollector.Config.DisplayName).ConfigureAwait(false);
            if (result.Success)
            {
                RoomId = result.RoomId;
                IsHost = true;
                CanStream = true;
                lock (membersLock)
                {
                    members.Clear();
                    members.Add(new RoomMemberInfo(ConnectionId!, ServiceCollector.Config.DisplayName, IsHost: true, CanStream: true));
                }
                RecordEvent($"Room created: {result.RoomId}");
                RememberRoom(result.RoomId!, password, wasHost: true);
            }
            else
            {
                LastError = result.Error;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void JoinRoom(string roomId, string password)
    {
        LastError = null;
        lastRoomPassword = password;
        _ = JoinRoomCoreAsync(roomId, password);
    }

    private async Task JoinRoomCoreAsync(string roomId, string password)
    {
        try
        {
            JoinRoomResult result = await client.JoinRoomAsync(
                roomId, password, ServiceCollector.Config.ClientId, ServiceCollector.Config.DisplayName).ConfigureAwait(false);
            if (result.Success)
            {
                RoomId = roomId;
                IsHost = false;
                RecordEvent($"Joined room {roomId}");
                RememberRoom(roomId, password, wasHost: false);
            }
            else
            {
                LastError = result.Error;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void LeaveRoom()
    {
        if (RoomId is not { } roomId)
            return;

        _ = LeaveRoomCoreAsync(roomId);
        ResetRoomState();
    }

    private async Task LeaveRoomCoreAsync(string roomId)
    {
        try
        {
            await client.LeaveRoomAsync(roomId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void KickViewer(string viewerConnectionId)
    {
        if (RoomId is not { } roomId || !IsHost)
            return;

        _ = KickViewerCoreAsync(roomId, viewerConnectionId);
    }

    private async Task KickViewerCoreAsync(string roomId, string viewerConnectionId)
    {
        try
        {
            await client.KickViewerAsync(roomId, viewerConnectionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void GrantStreamPermission(string viewerConnectionId)
    {
        if (RoomId is not { } roomId || !IsHost)
            return;

        _ = GrantStreamPermissionCoreAsync(roomId, viewerConnectionId);
    }

    private async Task GrantStreamPermissionCoreAsync(string roomId, string viewerConnectionId)
    {
        try
        {
            await client.GrantStreamPermissionAsync(roomId, viewerConnectionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void RevokeStreamPermission(string viewerConnectionId)
    {
        if (RoomId is not { } roomId || !IsHost)
            return;

        _ = RevokeStreamPermissionCoreAsync(roomId, viewerConnectionId);
    }

    private async Task RevokeStreamPermissionCoreAsync(string roomId, string viewerConnectionId)
    {
        try
        {
            await client.RevokeStreamPermissionAsync(roomId, viewerConnectionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Places, moves, or (with null) clears the room's shared screen. No-ops rather than
    /// erroring when this client isn't permitted - the UI already disables the controls, and the
    /// relay re-checks anyway, so this is just the third layer of the same guard.</summary>
    public void UpdateScreenPlacement(ScreenPlacementInfo? placement)
    {
        if (RoomId is not { } roomId || !CanPlaceScreen)
            return;

        // Applied locally straight away rather than waiting for the relay's echo: this is driven
        // by a slider drag, and a round trip's worth of lag between moving the slider and the
        // screen moving would feel broken.
        ScreenPlacement = placement;
        _ = UpdateScreenPlacementCoreAsync(roomId, placement);
    }

    private async Task UpdateScreenPlacementCoreAsync(string roomId, ScreenPlacementInfo? placement)
    {
        try
        {
            await client.UpdateScreenPlacementAsync(roomId, placement).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void ResetRoomState()
    {
        RoomId = null;
        IsHost = false;
        CanStream = false;
        lastRoomPassword = null;
        ScreenPlacement = null;
        hasStreamerBaseline = false;
        hasWatcherBaseline = false;
        lock (membersLock)
            members.Clear();
        lock (activeStreamersLock)
            activeStreamers = [];
        lock (watchersLock)
            watchers = [];
    }

    public void Dispose()
    {
        _ = client.DisposeAsync().AsTask();
    }
}
