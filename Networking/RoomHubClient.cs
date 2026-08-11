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

    public HubConnectionState State => connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>This connection's own SignalR connection id - the same id the relay recorded as
    /// the room's host or a viewer during CreateRoom/JoinRoom, needed to authorize the separate
    /// media-plane WebSocket (see MediaEndpoints on the relay).</summary>
    public string? ConnectionId => connection?.ConnectionId;

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

    public event Action<string, bool>? ViewerStreamPermissionChanged;

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
        newConnection.On<string, bool>("ViewerStreamPermissionChanged", (id, granted) => ViewerStreamPermissionChanged?.Invoke(id, granted));

        await newConnection.StartAsync().ConfigureAwait(false);
        connection = newConnection;
    }

    public Task<CreateRoomResult> CreateRoomAsync(string password, int maxViewers, string clientId, string displayName)
        => RequireConnection().InvokeAsync<CreateRoomResult>("CreateRoom", password, maxViewers, clientId, displayName);

    public Task<JoinRoomResult> JoinRoomAsync(string roomId, string password, string clientId, string displayName)
        => RequireConnection().InvokeAsync<JoinRoomResult>("JoinRoom", roomId, password, clientId, displayName);

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
