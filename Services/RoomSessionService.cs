using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Services;

public readonly record struct RoomEvent(DateTime TimestampLocal, string Message);

public sealed class RoomSessionService : IDisposable
{
    private const int MaxRememberedRooms = 20;
    private static readonly TimeSpan DirectoryRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly RoomHubClient client = new();
    private readonly Lock recentRoomsLock = new();
    private readonly ConcurrentDictionary<string, byte> bannedRoomIds = new(StringComparer.OrdinalIgnoreCase);
    private RoomDirectory? directory;
    private DateTime lastDirectoryRefreshUtc;
    private int directoryRefreshInFlight;
    private readonly Lock membersLock = new();
    private readonly List<RoomMemberInfo> members = [];
    private readonly Lock activeStreamersLock = new();
    private List<ActiveStreamerInfo> activeStreamers = [];
    private readonly Lock watchersLock = new();
    private List<WatcherInfo> watchers = [];

    private const int MaxLoggedEvents = 200;
    private readonly Lock eventsLock = new();
    private readonly List<RoomEvent> events = [];
    private string? lastRoomPassword;

    private bool hasStreamerBaseline;

    private bool hasWatcherBaseline;

    public string ConnectionStatus { get; private set; } = "Disconnected";

    public HubConnectionState State => client.State;

    public string? ConnectionId => client.ConnectionId;

    public string? InstanceId => client.InstanceId;

    public string? RelayUrl => client.RelayUrl;

    public bool IsSwitchingRelay => client.IsSwitchingRelay;

    public string? RoomId { get; private set; }

    public string? RoomName { get; private set; }

    public bool RoomIsPublic { get; private set; }

    public bool RoomHasPassword { get; private set; }

    public bool IsHost { get; private set; }

    public bool InRoom => RoomId is not null;

    public bool CanStream { get; private set; }

    public bool CanStartStreaming => IsHost || CanStream;

    public ScreenPlacementInfo? ScreenPlacement { get; private set; }

    public bool CanPlaceScreen => CanStartStreaming;

    public string? LastError { get; private set; }

    public string? LastEvent { get; private set; }

    public string? DirectoryError { get; private set; }

    public bool HasDirectory => directory is not null;

    public bool IsRefreshingDirectory => Volatile.Read(ref directoryRefreshInFlight) == 1;

    /// <summary>How long until the room list refreshes itself again, for the lobby to count down
    /// on its Refresh tab. Zero when a refresh is due, has never run, or cannot run because the
    /// connection is down - all cases where there is no next time worth naming.</summary>
    public TimeSpan TimeUntilDirectoryRefresh
    {
        get
        {
            if (lastDirectoryRefreshUtc == default || State != HubConnectionState.Connected)
                return TimeSpan.Zero;

            TimeSpan remaining = DirectoryRefreshInterval - (DateTime.UtcNow - lastDirectoryRefreshUtc);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public event Action<ActiveStreamerInfo>? StreamerWentLive;

    public IReadOnlyList<RoomListing> PublicRooms
        => directory is { } current
            ? [.. current.Rooms.Where(r => r.IsPublic && !IsBannedFrom(r.RoomId))]
            : [];

    public RoomListing? FindListing(string roomId)
    {
        if (IsBannedFrom(roomId) || directory is not { } current)
            return null;

        return current.Rooms.FirstOrDefault(r => string.Equals(r.RoomId, roomId, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsBannedFrom(string roomId) => bannedRoomIds.ContainsKey(roomId);

    public IReadOnlyList<RecentRoom> RecentRooms
    {
        get
        {
            string relayUrl = ServiceCollector.Config.RelayUrl;
            lock (recentRoomsLock)
            {
                return [.. ServiceCollector.Config.RecentRooms.Where(r =>
                    string.Equals(r.RelayUrl, relayUrl, StringComparison.OrdinalIgnoreCase) && !IsBannedFrom(r.RoomId))];
            }
        }
    }

    public void ForgetRoom(string roomId)
    {
        Configuration config = ServiceCollector.Config;
        lock (recentRoomsLock)
        {
            config.RecentRooms.RemoveAll(r =>
                string.Equals(r.RoomId, roomId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.RelayUrl, config.RelayUrl, StringComparison.OrdinalIgnoreCase));
        }

        ServiceCollector.PI.SavePluginConfig(config);
    }

    public void SetRememberRoomPasswords(bool remember)
    {
        Configuration config = ServiceCollector.Config;
        lock (recentRoomsLock)
        {
            config.RememberRoomPasswords = remember;
            if (!remember)
            {
                foreach (RecentRoom room in config.RecentRooms)
                    room.Password = null;
            }
        }

        ServiceCollector.PI.SavePluginConfig(config);
    }

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

            if (hasStreamerBaseline && streamer.ConnectionId != ConnectionId)
                StreamerWentLive?.Invoke(streamer);
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

    private void RememberRoom(string roomId, string password, bool wasHost)
    {
        Configuration config = ServiceCollector.Config;
        string relayUrl = config.RelayUrl;

        lock (recentRoomsLock)
        {
            config.RecentRooms.RemoveAll(r =>
                string.Equals(r.RoomId, roomId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.RelayUrl, relayUrl, StringComparison.OrdinalIgnoreCase));

            config.RecentRooms.Insert(0, new RecentRoom
            {
                RelayUrl = relayUrl,
                RoomId = roomId,
                Name = RoomName,
                IsPublic = RoomIsPublic,
                HasPassword = RoomHasPassword,
                Password = config.RememberRoomPasswords && RoomHasPassword ? password : null,
                WasHost = wasHost,
                LastJoinedUtc = DateTime.UtcNow,
            });

            if (config.RecentRooms.Count > MaxRememberedRooms)
                config.RecentRooms.RemoveRange(MaxRememberedRooms, config.RecentRooms.Count - MaxRememberedRooms);
        }

        ServiceCollector.PI.SavePluginConfig(config);
    }

    private void MarkBanned(IEnumerable<string> roomIds)
    {
        bool forgotAny = false;
        Configuration config = ServiceCollector.Config;

        foreach (string roomId in roomIds)
        {
            bannedRoomIds[roomId] = 0;
            lock (recentRoomsLock)
            {
                forgotAny |= config.RecentRooms.RemoveAll(r =>
                    string.Equals(r.RoomId, roomId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.RelayUrl, config.RelayUrl, StringComparison.OrdinalIgnoreCase)) > 0;
            }
        }

        if (forgotAny)
            ServiceCollector.PI.SavePluginConfig(config);
    }

    private void RecordEvent(string message)
    {
        LastEvent = message;

        lock (eventsLock)
        {
            events.Add(new RoomEvent(DateTime.Now, message));

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
            if (RoomId is { } kickedFrom)
                MarkBanned([kickedFrom]);
            ResetRoomState();
            ReturnToLobby();
        };

        client.MemberRenamed += (connectionId, displayName) =>
        {
            string? previousName = null;
            lock (membersLock)
            {
                int index = members.FindIndex(m => m.ConnectionId == connectionId);
                if (index >= 0)
                {
                    previousName = members[index].DisplayName;
                    members[index] = members[index] with { DisplayName = displayName };
                }
            }

            lock (activeStreamersLock)
                activeStreamers = [.. activeStreamers.Select(s => s.ConnectionId == connectionId ? s with { DisplayName = displayName } : s)];

            if (previousName is not null && previousName != displayName)
                RecordEvent($"{previousName} is now {displayName}");
        };

        client.RoomClosed += () =>
        {
            RecordEvent("Room closed (host disconnected).");
            ResetRoomState();
            ReturnToLobby();
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
            if (placement is not null && ScreenPlacement is { } current && placement.Version < current.Version)
                return;

            ScreenPlacement = placement;
            RecordEvent(placement is null ? "The in-world screen was cleared." : "The in-world screen was moved.");
        };

        client.Reconnecting += () => ConnectionStatus = "Reconnecting...";

        client.Reconnected += () =>
        {
            ConnectionStatus = "Connected";
            if (RoomId is not null)
                _ = RejoinAfterReconnectAsync();
        };

        // Automatic reconnect gave up. If that was a room's relay rather than the main one, the
        // main relay may well still be up - drop back to its lobby instead of the connect card.
        client.Closed += () =>
        {
            bool wasInRoom = InRoom;
            ResetRoomState();
            ConnectionStatus = "Disconnected";
            LastError = wasInRoom ? "Lost the connection to this room's relay." : "Lost the connection to the relay.";
            ReturnToLobby();
        };
    }

    /// <summary>Creating or joining a room can move this client from the main relay (the one the
    /// user entered) onto the relay hosting that room. Once the room is over, move back, so the
    /// next Create goes through the main relay's placement again rather than landing wherever
    /// the last room happened to be. A no-op when already on the main relay.</summary>
    private void ReturnToLobby()
    {
        string lobbyUrl = ServiceCollector.Config.RelayUrl;
        if (client.RelayUrl is not { } currentUrl || string.Equals(currentUrl, lobbyUrl, StringComparison.OrdinalIgnoreCase))
            return;

        IsConnecting = true;

        // Off the SignalR callback that got us here: switching disposes the connection that
        // callback is running on.
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SwitchRelayAsync(lobbyUrl).ConfigureAwait(false);
                ConnectionStatus = "Connected";
                RefreshDirectory(force: true);
            }
            catch (Exception ex)
            {
                ConnectionStatus = "Disconnected";
                LastError = ex.Message;
            }
            finally
            {
                IsConnecting = false;
            }
        });
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

    public bool IsConnecting { get; private set; }

    public void Connect(string relayUrl)
    {
        LastError = null;
        ConnectionStatus = "Connecting...";
        IsConnecting = true;
        ResetRoomState();
        ClearDirectory();
        _ = ConnectCoreAsync(relayUrl);
    }

    private async Task ConnectCoreAsync(string relayUrl)
    {
        try
        {
            await client.ConnectAsync(relayUrl).ConfigureAwait(false);
            ConnectionStatus = "Connected";
            RefreshDirectory(force: true);
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Disconnected";
            LastError = ex.Message;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public void Disconnect()
    {
        LastError = null;
        ResetRoomState();
        ClearDirectory();
        ConnectionStatus = "Disconnected";
        _ = client.DisconnectAsync();
    }

    private void ClearDirectory()
    {
        directory = null;
        DirectoryError = null;
        bannedRoomIds.Clear();
        lastDirectoryRefreshUtc = default;
    }

    public void RefreshDirectory(bool force = false)
    {
        if (State != HubConnectionState.Connected)
            return;

        if (!force && DateTime.UtcNow - lastDirectoryRefreshUtc < DirectoryRefreshInterval)
            return;

        if (Interlocked.Exchange(ref directoryRefreshInFlight, 1) == 1)
            return;

        lastDirectoryRefreshUtc = DateTime.UtcNow;
        _ = RefreshDirectoryCoreAsync();
    }

    private async Task RefreshDirectoryCoreAsync()
    {
        try
        {
            RoomDirectory result = await client.ListRoomsAsync(ServiceCollector.Config.ClientId).ConfigureAwait(false);
            if (result.BannedRoomIds.Count > 0)
                MarkBanned(result.BannedRoomIds);

            directory = result;
            DirectoryError = null;
        }
        catch (Exception ex)
        {
            DirectoryError = ex.Message;
        }
        finally
        {
            Volatile.Write(ref directoryRefreshInFlight, 0);
        }
    }

    /// <summary>Saves the new name and, in a room, tells everyone there. Your ID doesn't change.</summary>
    public void UpdateDisplayName(string displayName)
    {
        Configuration config = ServiceCollector.Config;
        if (displayName == config.DisplayName)
            return;

        config.DisplayName = displayName;
        ServiceCollector.PI.SavePluginConfig(config);

        if (RoomId is not { } roomId || ConnectionId is not { } self)
            return;

        lock (membersLock)
        {
            int index = members.FindIndex(m => m.ConnectionId == self);
            if (index >= 0)
                members[index] = members[index] with { DisplayName = displayName };
        }

        RecordEvent($"You are now {displayName}");
        _ = UpdateDisplayNameCoreAsync(roomId, displayName);
    }

    private async Task UpdateDisplayNameCoreAsync(string roomId, string displayName)
    {
        try
        {
            await client.UpdateDisplayNameAsync(roomId, displayName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    public void CreateRoom(string name, bool isPublic, string password, int maxViewers)
    {
        LastError = null;
        lastRoomPassword = password;
        _ = CreateRoomCoreAsync(new CreateRoomRequest(name, isPublic, string.IsNullOrEmpty(password) ? null : password, maxViewers));
    }

    private async Task CreateRoomCoreAsync(CreateRoomRequest request)
    {
        try
        {
            Configuration config = ServiceCollector.Config;
            CreateRoomResult result = await client.CreateRoomAsync(request, config.ClientId, config.DisplayName).ConfigureAwait(false);
            if (result.Success)
            {
                RoomName = result.RoomName ?? request.Name;
                RoomIsPublic = request.IsPublic;
                RoomHasPassword = request.Password is not null;
                RoomId = result.RoomId;
                IsHost = true;
                CanStream = true;

                hasStreamerBaseline = true;
                hasWatcherBaseline = true;
                lock (membersLock)
                {
                    members.Clear();
                    members.Add(new RoomMemberInfo(ConnectionId!, config.DisplayName, IsHost: true, CanStream: true, ClientIdentity.ToPublicId(config.ClientId)));
                }
                RecordEvent($"Room created: {RoomName} ({result.RoomId})");
                RememberRoom(result.RoomId!, request.Password ?? string.Empty, wasHost: true);
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
                RoomName = result.RoomName ?? $"Room {roomId}";
                RoomIsPublic = result.IsPublic;
                RoomHasPassword = result.HasPassword;
                RoomId = roomId;
                IsHost = false;
                RecordEvent($"Joined {RoomName} ({roomId})");
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

        ReturnToLobby();
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
        RoomName = null;
        RoomIsPublic = false;
        RoomHasPassword = false;
        IsHost = false;
        CanStream = false;
        lastRoomPassword = null;
        ScreenPlacement = null;
        hasStreamerBaseline = false;
        hasWatcherBaseline = false;
        lastDirectoryRefreshUtc = default;
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
