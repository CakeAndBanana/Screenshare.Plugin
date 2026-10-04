using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Networking.Models;

namespace ScreenShare.Plugin.Networking;

/// <summary>
/// SignalR client for connecting to a room hub on the relay server. This class manages the connection, handles events, and provides methods for interacting with rooms and members.
/// </summary>
public sealed class RoomHubClient : IAsyncDisposable
{
    private HubConnection? connection;
    private string? relayUrl;

    public HubConnectionState State => connection?.State ?? HubConnectionState.Disconnected;

    public string? ConnectionId => connection?.ConnectionId;

    public string? RelayUrl => relayUrl;

    public string? InstanceId { get; private set; }

    public event Action<RoomMemberInfo>? MemberJoined;

    public event Action<string>? MemberLeft;

    public event Action<IReadOnlyCollection<RoomMemberInfo>>? RoomMembersSnapshot;

    public event Action? Kicked;

    public event Action? RoomClosed;

    public event Action<string>? ShareModeChanged;

    public event Action<IReadOnlyCollection<ActiveStreamerInfo>>? ActiveStreamersChanged;

    public event Action<IReadOnlyCollection<WatcherInfo>>? WatchersChanged;

    public event Action<string, bool>? ViewerStreamPermissionChanged;

    public event Action<ScreenPlacementInfo?>? ScreenPlacementChanged;

    public event Action<string, string>? MemberRenamed;

    public event Action? Reconnecting;

    public event Action? Reconnected;

    public event Action? Closed;

    public async Task ConnectAsync(string relayUrl)
    {
        HubConnection? previous = connection;
        connection = null;
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);

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
        newConnection.On<ScreenPlacementInfo?>("ScreenPlacementChanged", placement => ScreenPlacementChanged?.Invoke(placement));
        newConnection.On<string, string>("MemberRenamed", (id, name) => MemberRenamed?.Invoke(id, name));

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
        newConnection.Closed += _ =>
        {
            if (ReferenceEquals(connection, newConnection))
                Closed?.Invoke();
            return Task.CompletedTask;
        };

        await newConnection.StartAsync().ConfigureAwait(false);
        connection = newConnection;
        this.relayUrl = relayUrl;
    }

    public bool IsSwitchingRelay { get; private set; }

    public async Task SwitchRelayAsync(string hubUrl)
    {
        IsSwitchingRelay = true;
        try
        {
            await ConnectAsync(hubUrl).ConfigureAwait(false);
        }
        finally
        {
            IsSwitchingRelay = false;
        }
    }

    private Task FollowRedirectAsync(string redirectUrl)
    {
        if (!Networking.RelayUrl.TryNormalize(redirectUrl, out string hubUrl))
            throw new InvalidOperationException($"The relay redirected to an invalid address: {redirectUrl}");

        return SwitchRelayAsync(hubUrl);
    }

    public async Task DisconnectAsync()
    {
        if (connection is null)
            return;

        HubConnection closing = connection;
        connection = null;
        relayUrl = null;
        InstanceId = null;
        await closing.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest request, string clientId, string displayName)
    {
        CreateRoomResult result = await RequireConnection()
            .InvokeAsync<CreateRoomResult>("CreateRoom", request, clientId, displayName).ConfigureAwait(false);

        if (result.RedirectUrl is { } redirectUrl)
        {
            await FollowRedirectAsync(redirectUrl).ConfigureAwait(false);
            result = await RequireConnection()
                .InvokeAsync<CreateRoomResult>("CreateRoom", request, clientId, displayName).ConfigureAwait(false);
        }

        if (result.Success)
            InstanceId = result.InstanceId;

        return result;
    }

    public async Task<JoinRoomResult> JoinRoomAsync(string roomId, string password, string clientId, string displayName)
    {
        JoinRoomResult result = await RequireConnection()
            .InvokeAsync<JoinRoomResult>("JoinRoom", roomId, password, clientId, displayName).ConfigureAwait(false);

        if (result.RedirectUrl is { } redirectUrl)
        {
            await FollowRedirectAsync(redirectUrl).ConfigureAwait(false);
            result = await RequireConnection()
                .InvokeAsync<JoinRoomResult>("JoinRoom", roomId, password, clientId, displayName).ConfigureAwait(false);
        }

        if (result.Success)
            InstanceId = result.InstanceId;

        return result;
    }

    public Task<RoomDirectory> ListRoomsAsync(string clientId)
        => RequireConnection().InvokeAsync<RoomDirectory>("ListRooms", clientId);

    public Task UpdateDisplayNameAsync(string roomId, string displayName)
        => RequireConnection().InvokeAsync("UpdateDisplayName", roomId, displayName);

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

    public Task UpdateScreenPlacementAsync(string roomId, ScreenPlacementInfo? placement)
        => RequireConnection().InvokeAsync("UpdateScreenPlacement", roomId, placement);

    private HubConnection RequireConnection()
        => connection ?? throw new InvalidOperationException("Not connected to the relay.");

    public async ValueTask DisposeAsync()
    {
        HubConnection? closing = connection;
        connection = null;
        if (closing is not null)
            await closing.DisposeAsync().ConfigureAwait(false);
    }
}
