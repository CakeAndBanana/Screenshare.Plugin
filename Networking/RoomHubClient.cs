using Microsoft.AspNetCore.SignalR.Client;

namespace ScreenShare.Plugin.Networking;

/// <summary>
/// Thin wrapper around the SignalR connection to ScreenShare.Relay's control-plane hub. Every
/// method here does network I/O and must only ever be awaited from an async context - never
/// synchronously (.Result/.Wait()/.GetAwaiter().GetResult()) from the game's render thread. That
/// exact mistake (a blocking call reachable from Draw()) is what deadlocked the game earlier in
/// this project's audio pipeline; this class is designed so callers can't easily repeat it.
/// </summary>
public sealed class RoomHubClient : IAsyncDisposable
{
    private HubConnection? connection;
    private string? relayUrl;

    public HubConnectionState State => connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>This connection's own SignalR connection id - the same id the relay recorded as
    /// the room's host or a viewer during CreateRoom/JoinRoom, needed to authorize the separate
    /// media-plane WebSocket (see MediaEndpoints on the relay).</summary>
    public string? ConnectionId => connection?.ConnectionId;

    /// <summary>The relay URL this connection is currently attached to - starts as whatever was
    /// passed to <see cref="ConnectAsync"/>, but changes if <see cref="JoinRoomAsync"/> had to
    /// follow a cross-instance redirect. Lets the UI show which physical relay instance it's
    /// actually talking to right now, not just what the user originally typed in.</summary>
    public string? RelayUrl => relayUrl;

    /// <summary>Which relay instance most recently answered a CreateRoom/JoinRoom call - always
    /// populated on success (even a single-instance deployment reports its one instance id), null
    /// until the first successful call.</summary>
    public string? InstanceId { get; private set; }

    /// <summary>Fired for a new arrival - everyone already in the room gets this, but not the
    /// arrival themselves (they get <see cref="RoomMembersSnapshot"/> instead, which tells them
    /// about everyone already present).</summary>
    public event Action<RoomMemberInfo>? MemberJoined;

    public event Action<string>? MemberLeft;

    /// <summary>Sent once, right after a successful join, with every current room member
    /// (host included) - lets the joiner render the full room immediately instead of building it
    /// up one MemberJoined at a time.</summary>
    public event Action<IReadOnlyCollection<RoomMemberInfo>>? RoomMembersSnapshot;

    public event Action? Kicked;

    public event Action? RoomClosed;

    public event Action<string>? ShareModeChanged;

    public event Action<IReadOnlyCollection<ActiveStreamerInfo>>? ActiveStreamersChanged;

    /// <summary>Sent to a caller right after they join (current state), and to the whole room
    /// whenever it changes - mirrors ActiveStreamersChanged's own pattern.</summary>
    public event Action<IReadOnlyCollection<WatcherInfo>>? WatchersChanged;

    public event Action<string, bool>? ViewerStreamPermissionChanged;

    /// <summary>Fired when the underlying transport drops and SignalR's automatic reconnect is
    /// actively trying to re-establish it - purely informational (e.g. for a "Reconnecting..."
    /// status label), nothing to act on here.</summary>
    public event Action? Reconnecting;

    /// <summary>Fired once the transport has a new live connection - critically, this is NOT the
    /// same as "back in the room": automatic reconnect gives this connection a brand new
    /// ConnectionId and does nothing on its own to restore server-side state tied to the old one
    /// (SignalR group membership, this relay's own room/host tracking). Without a caller
    /// explicitly re-running JoinRoom here, a reconnected client looks "Connected" but silently
    /// stops receiving any room broadcasts at all - see RoomHub.JoinRoom's reconnect-detection
    /// path, which this is designed to trigger.</summary>
    public event Action? Reconnected;

    public async Task ConnectAsync(string relayUrl)
    {
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);

        HubConnection newConnection = new HubConnectionBuilder()
            .WithUrl(relayUrl)
            .WithAutomaticReconnect()
            .Build();

        newConnection.On<RoomMemberInfo>("MemberJoined", member => MemberJoined?.Invoke(member));
        newConnection.On<string>("MemberLeft", id => MemberLeft?.Invoke(id));
        newConnection.On<IReadOnlyCollection<RoomMemberInfo>>("RoomMembersSnapshot", members => RoomMembersSnapshot?.Invoke(members));
        newConnection.On("Kicked", () => Kicked?.Invoke());
        newConnection.On("RoomClosed", () => RoomClosed?.Invoke());
        newConnection.On<string>("ShareModeChanged", mode => ShareModeChanged?.Invoke(mode));
        newConnection.On<IReadOnlyCollection<ActiveStreamerInfo>>("ActiveStreamersChanged", streamers => ActiveStreamersChanged?.Invoke(streamers));
        newConnection.On<IReadOnlyCollection<WatcherInfo>>("WatchersChanged", watchers => WatchersChanged?.Invoke(watchers));
        newConnection.On<string, bool>("ViewerStreamPermissionChanged", (id, granted) => ViewerStreamPermissionChanged?.Invoke(id, granted));

        newConnection.Reconnecting += _ =>
        {
            Reconnecting?.Invoke();
            return Task.CompletedTask;
        };
        newConnection.Reconnected += _ =>
        {
            Reconnected?.Invoke();
            return Task.CompletedTask;
        };

        await newConnection.StartAsync().ConfigureAwait(false);
        connection = newConnection;
        this.relayUrl = relayUrl;
    }

    public async Task<CreateRoomResult> CreateRoomAsync(string password, int maxViewers, string clientId, string displayName)
    {
        CreateRoomResult result = await RequireConnection()
            .InvokeAsync<CreateRoomResult>("CreateRoom", password, maxViewers, clientId, displayName).ConfigureAwait(false);
        if (result.Success)
            InstanceId = result.InstanceId;

        return result;
    }

    /// <summary>Follows at most one cross-instance redirect: if the relay this connection is
    /// currently attached to doesn't own <paramref name="roomId"/> (multi-instance/Redis mode
    /// only), reconnects to the owning instance's URL and retries once there. A second redirect
    /// is treated as a failure rather than chased indefinitely - a healthy deployment should never
    /// produce one, so chasing further would more likely mean a misconfiguration/routing loop than
    /// genuine progress.</summary>
    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string password, string clientId, string displayName)
    {
        JoinRoomResult result = await RequireConnection()
            .InvokeAsync<JoinRoomResult>("JoinRoom", roomId, password, clientId, displayName).ConfigureAwait(false);

        if (result.RedirectUrl is { } redirectUrl)
        {
            await ConnectAsync(redirectUrl).ConfigureAwait(false);
            result = await RequireConnection()
                .InvokeAsync<JoinRoomResult>("JoinRoom", roomId, password, clientId, displayName).ConfigureAwait(false);
        }

        if (result.Success)
            InstanceId = result.InstanceId;

        return result;
    }

    public Task LeaveRoomAsync(string roomId)
        => RequireConnection().InvokeAsync("LeaveRoom", roomId);

    public Task KickViewerAsync(string roomId, string viewerConnectionId)
        => RequireConnection().InvokeAsync("KickViewer", roomId, viewerConnectionId);

    public Task GrantStreamPermissionAsync(string roomId, string targetConnectionId)
        => RequireConnection().InvokeAsync("GrantStreamPermission", roomId, targetConnectionId);

    public Task RevokeStreamPermissionAsync(string roomId, string targetConnectionId)
        => RequireConnection().InvokeAsync("RevokeStreamPermission", roomId, targetConnectionId);

    public Task UpdateShareModeAsync(string roomId, string mode)
        => RequireConnection().InvokeAsync("UpdateShareMode", roomId, mode);

    private HubConnection RequireConnection()
        => connection ?? throw new InvalidOperationException("Not connected to the relay.");

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
    }
}
