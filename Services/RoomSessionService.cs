using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Networking;

namespace ScreenShare.Plugin.Services;

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

    // Cached so a silent SignalR reconnect (see the Reconnected handler below) can replay the
    // exact same JoinRoom call - the relay's own reconnect-detection path (RoomState.
    // TryReconnectParticipant) skips the password check for a genuine reconnect, but the RPC
    // signature still requires passing something, and there's no other place this client keeps
    // the password around long enough to survive past the initial Create/Join button click.
    private string? lastRoomPassword;

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

    public string? LastError { get; private set; }

    public string? LastEvent { get; private set; }

    /// <summary>Every current room member, host included - <see cref="RoomMemberInfo.IsHost"/>
    /// and <see cref="RoomMemberInfo.CanStream"/> tell the UI which icon(s)/actions apply to
    /// each row.</summary>
    public IReadOnlyList<RoomMemberInfo> Members
    {
        get
        {
            lock (membersLock)
                return members.ToArray();
        }
    }

    public IReadOnlyList<ActiveStreamerInfo> ActiveStreamers
    {
        get
        {
            lock (activeStreamersLock)
                return activeStreamers.ToArray();
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
                return watchers.ToArray();
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
            LastEvent = $"{member.DisplayName} joined";
        };

        client.MemberLeft += id =>
        {
            string? displayName;
            lock (membersLock)
            {
                displayName = members.FirstOrDefault(m => m.ConnectionId == id)?.DisplayName;
                members.RemoveAll(m => m.ConnectionId == id);
            }
            LastEvent = displayName is not null ? $"{displayName} left" : "A member left";
        };

        client.Kicked += () =>
        {
            LastEvent = "You were removed from the room by the host.";
            ResetRoomState();
        };

        client.RoomClosed += () =>
        {
            LastEvent = "Room closed (host disconnected).";
            ResetRoomState();
        };

        client.ActiveStreamersChanged += streamers =>
        {
            lock (activeStreamersLock)
                activeStreamers = streamers.ToList();
        };

        client.WatchersChanged += newWatchers =>
        {
            lock (watchersLock)
                watchers = newWatchers.ToList();
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
                LastEvent = "Reconnected to the relay.";
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
                LastEvent = $"Room created: {result.RoomId}";
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
                LastEvent = $"Joined room {roomId}";
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

    private void ResetRoomState()
    {
        RoomId = null;
        IsHost = false;
        CanStream = false;
        lastRoomPassword = null;
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
