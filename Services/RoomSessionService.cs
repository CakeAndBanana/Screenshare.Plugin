using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Services;

public readonly record struct RoomEvent(DateTime TimestampLocal, string Message);

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

    private bool hasStreamerBaseline;

    private bool hasWatcherBaseline;

    public string ConnectionStatus { get; private set; } = "Disconnected";

    public HubConnectionState State => client.State;

    public string? ConnectionId => client.ConnectionId;

    public string? InstanceId => client.InstanceId;

    public string? RelayUrl => client.RelayUrl;

    public string? RoomId { get; private set; }

    public bool IsHost { get; private set; }

    public bool InRoom => RoomId is not null;

    public bool CanStream { get; private set; }

    public bool CanStartStreaming => IsHost || CanStream;

    public ScreenPlacementInfo? ScreenPlacement { get; private set; }

    public bool CanPlaceScreen => CanStartStreaming;

    public string? LastError { get; private set; }

    public string? LastEvent { get; private set; }

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

    private string DisplayNameFor(string connectionId)
    {
        lock (membersLock)
            return members.FirstOrDefault(m => m.ConnectionId == connectionId)?.DisplayName ?? "someone";
    }

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
