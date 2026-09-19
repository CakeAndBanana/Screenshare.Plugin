using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

internal sealed class LobbyView(RoomSessionService roomSession, IdentityChip identity, Action disconnect)
{
    private const string CreatePopupId = "Create a room###createRoom";
    private const string JoinPopupId = "Join by code###joinByCode";
    private const string PasswordPopupId = "Password needed###roomPassword";

    private string relayUrlInput = ServiceCollector.Config.RelayUrl;
    private string displayNameInput = ServiceCollector.Config.DisplayName;

    private string createName = string.Empty;
    private int createVisibility; // 0 public, 1 private
    private string createPassword = string.Empty;
    private int createMaxViewers = 4;

    private string joinCode = string.Empty;
    private string joinPassword = string.Empty;

    private string? passwordRoomId;
    private string passwordRoomName = string.Empty;
    private string passwordInput = string.Empty;

    private bool openCreate;
    private bool openJoin;
    private bool openPassword;

    // ---------------------------------------------------------------- connect

    public void DrawConnect()
    {
        float width = UiTheme.Px(300);

        UiTheme.Heading("Screen Share");
        ImGui.TextDisabled("Connect to a relay to create or join rooms.");
        ImGui.Dummy(new Vector2(width, UiTheme.Px(6)));

        ImGui.TextUnformatted("Relay URL");
        ImGui.SetNextItemWidth(width);
        bool submitted = ImGui.InputTextWithHint("##relayUrl", "Insert relay URL", ref relayUrlInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);

        bool hasRelay = RelayUrl.TryNormalize(relayUrlInput, out string normalizedUrl);
        if (!hasRelay && relayUrlInput.Trim().Length > 0)
            ImGui.TextColored(UiTheme.WarnColor, "That doesn't look like a relay address.");

        ImGui.Spacing();
        ImGui.BeginDisabled(!hasRelay);
        ImGui.TextUnformatted("Display name");
        ImGui.SetNextItemWidth(width);
        submitted |= ImGui.InputTextWithHint(
            "##displayName", hasRelay ? "Choose a display name" : "Enter a relay URL first",
            ref displayNameInput, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.EndDisabled();

        bool canConnect = hasRelay && displayNameInput.Trim().Length > 0 && !roomSession.IsConnecting;

        ImGui.Dummy(new Vector2(0, UiTheme.Px(4)));
        ImGui.BeginDisabled(!canConnect);
        bool clicked = UiTheme.ColoredButton(
            roomSession.IsConnecting ? "Connecting...##connect" : "Connect##connect",
            UiTheme.PublicGreen,
            new Vector2(width, ImGui.GetFrameHeight() * 1.3f));
        ImGui.EndDisabled();

        if (canConnect && (clicked || submitted))
            Connect(normalizedUrl);

        if (roomSession.LastError is { } error)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(UiTheme.ErrorColor, $"Couldn't connect: {error}");
            ImGui.PopTextWrapPos();
        }
    }

    private void Connect(string normalizedUrl)
    {
        Configuration config = ServiceCollector.Config;
        relayUrlInput = normalizedUrl;
        displayNameInput = displayNameInput.Trim();
        config.RelayUrl = normalizedUrl;
        config.DisplayName = displayNameInput;
        ServiceCollector.PI.SavePluginConfig(config);
        roomSession.Connect(normalizedUrl);
    }

    // ---------------------------------------------------------------- lobby

    public void DrawLobby()
    {
        relayUrlInput = ServiceCollector.Config.RelayUrl;
        displayNameInput = ServiceCollector.Config.DisplayName;

        roomSession.RefreshDirectory();

        DrawTopBar();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCreateJoinButtons();
        ImGui.Spacing();
        DrawRoomTabs();

        DrawCreatePopup();
        DrawJoinPopup();
        DrawPasswordPopup();

        if (roomSession.LastError is { } error)
            ImGui.TextColored(UiTheme.ErrorColor, error);
    }

    private void DrawTopBar()
    {
        identity.Draw();

        float disconnectWidth = UiTheme.IconButtonWidth(FontAwesomeIcon.SignOutAlt);
        string host = Uri.TryCreate(ServiceCollector.Config.RelayUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : ServiceCollector.Config.RelayUrl;
        float hostWidth = ImGui.CalcTextSize(host).X;
        float spacing = ImGui.GetStyle().ItemSpacing.X;

        ImGui.SameLine(ImGui.GetContentRegionMax().X - disconnectWidth - hostWidth - spacing);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(host);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(ServiceCollector.Config.RelayUrl);

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##disconnect", FontAwesomeIcon.SignOutAlt))
            disconnect();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Disconnect from this relay");
    }

    private void DrawCreateJoinButtons()
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float half = (ImGui.GetContentRegionAvail().X - spacing) / 2;
        var size = new Vector2(half, ImGui.GetFrameHeight() * 1.4f);

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Create room", UiTheme.PublicGreen with { W = 0.55f }, UiTheme.PublicGreen, UiTheme.PublicGreen with { W = 0.8f }, size))
        {
            createName = string.Empty;
            createPassword = string.Empty;
            openCreate = true;
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Hashtag, "Join by code", size))
        {
            joinCode = string.Empty;
            joinPassword = string.Empty;
            openJoin = true;
        }
    }

    private void DrawRoomTabs()
    {
        IReadOnlyList<RoomListing> publicRooms = roomSession.PublicRooms;
        IReadOnlyList<RecentRoom> recentRooms = roomSession.RecentRooms;

        if (!ImGui.BeginTabBar("##lobbyTabs"))
            return;

        if (ImGui.TabItemButton(roomSession.IsRefreshingDirectory ? "...##refresh" : "Refresh##refresh", ImGuiTabItemFlags.Trailing))
            roomSession.RefreshDirectory(force: true);

        if (ImGui.BeginTabItem($"Public rooms ({publicRooms.Count})###publicRooms"))
        {
            DrawPublicRooms(publicRooms);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Recent ({recentRooms.Count})###recentRooms"))
        {
            DrawRecentRooms(recentRooms);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawPublicRooms(IReadOnlyList<RoomListing> rooms)
    {
        if (!roomSession.HasDirectory)
        {
            ImGui.TextDisabled(roomSession.DirectoryError is { } error ? $"Couldn't load rooms: {error}" : "Loading rooms...");
            return;
        }

        if (rooms.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No public rooms right now. Create one, or join a private room by its code.");
            return;
        }

        foreach (RoomListing room in rooms)
        {
            RecentRoom? remembered = roomSession.RecentRooms.FirstOrDefault(r => r.RoomId == room.RoomId);
            DrawRoomRow(room.RoomId, room.Name, room, remembered);
        }
    }

    private void DrawRecentRooms(IReadOnlyList<RecentRoom> rooms)
    {
        if (rooms.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Rooms you create or join on this relay show up here.");
        }

        foreach (RecentRoom room in rooms)
        {
            RoomListing? live = roomSession.FindListing(room.RoomId);
            DrawRoomRow(room.RoomId, room.Name ?? $"Room {room.RoomId}", live, room);
        }

        ImGui.Spacing();
        bool remember = ServiceCollector.Config.RememberRoomPasswords;
        if (ImGui.Checkbox("Remember room passwords", ref remember))
            roomSession.SetRememberRoomPasswords(remember);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stores room passwords in the plugin's config file, so rejoining doesn't ask.");
    }

    private void DrawRoomRow(string roomId, string name, RoomListing? live, RecentRoom? recent)
    {
        bool closed = live is null && roomSession.HasDirectory;
        RoomKind kind = closed
            ? RoomKind.Closed
            : live is not null ? UiTheme.KindOf(live.IsPublic, live.HasPassword) : UiTheme.KindOf(recent!.IsPublic, recent.HasPassword);

        ImGui.PushID(roomId);

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float rowHeight = ImGui.GetFrameHeight() + UiTheme.Px(8);
        float buttonOffset = (rowHeight - ImGui.GetFrameHeight()) / 2;

        string count = live is not null ? $"{live.MemberNames.Count}/{live.MaxViewers + 1}" : string.Empty;
        float countWidth = UiTheme.Px(44);
        float joinWidth = UiTheme.Px(56);
        float forgetWidth = recent is not null ? UiTheme.IconButtonWidth(FontAwesomeIcon.Times) + spacing : 0;
        float plateWidth = MathF.Max(UiTheme.Px(60), ImGui.GetContentRegionAvail().X - countWidth - joinWidth - forgetWidth - (spacing * 2));

        float rowY = ImGui.GetCursorPosY();
        string? badge = live is { StreamerNames.Count: > 0 } ? "LIVE" : null;
        UiTheme.RoomPlate("##plate", name, kind, new Vector2(plateWidth, rowHeight), out bool hovered, badge);
        if (hovered)
            DrawRoomTooltip(roomId, name, kind, live, recent);

        ImGui.SameLine();
        ImGui.SetCursorPosY(rowY + buttonOffset);
        ImGui.AlignTextToFramePadding();
        float countX = ImGui.GetCursorPosX();
        ImGui.TextDisabled(count);
        ImGui.SameLine(countX + countWidth);

        ImGui.SetCursorPosY(rowY + buttonOffset);
        ImGui.BeginDisabled(closed);
        if (ImGui.Button("Join", new Vector2(joinWidth, 0)))
            Join(roomId, name, live?.HasPassword ?? recent?.HasPassword ?? false, recent?.Password);
        ImGui.EndDisabled();

        if (closed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("This room has closed.");

        if (recent is not null)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY + buttonOffset);
            if (ImGuiComponents.IconButton("##forget", FontAwesomeIcon.Times))
                roomSession.ForgetRoom(roomId);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove from Recent");
        }

        ImGui.SetCursorPosY(rowY + rowHeight + ImGui.GetStyle().ItemSpacing.Y);
        ImGui.Dummy(Vector2.Zero);
        ImGui.PopID();
    }

    private static void DrawRoomTooltip(string roomId, string name, RoomKind kind, RoomListing? live, RecentRoom? recent)
    {
        ImGui.BeginTooltip();
        ImGui.TextColored(UiTheme.ColorOf(kind), name);

        if (live is null)
        {
            ImGui.TextDisabled(kind == RoomKind.Closed ? "Closed" : "Checking...");
        }
        else
        {
            ImGui.TextDisabled(UiTheme.Describe(live.IsPublic, live.HasPassword));
            ImGui.Separator();

            ImGui.TextDisabled("Host");
            ImGui.SameLine();
            ImGui.TextUnformatted(live.HostName);

            ImGui.TextDisabled($"Members ({live.MemberNames.Count}/{live.MaxViewers + 1})");
            foreach (string member in live.MemberNames)
            {
                bool streaming = live.StreamerNames.Contains(member);
                ImGui.Bullet();
                if (streaming)
                {
                    ImGui.TextColored(UiTheme.StreamingGreen, member);
                    ImGui.SameLine();
                    ImGui.TextColored(UiTheme.LiveRed, "LIVE");
                }
                else
                {
                    ImGui.TextUnformatted(member);
                }
            }

            ImGui.TextDisabled($"Open for {UiTheme.DescribeAge(DateTime.UtcNow - live.CreatedUtc).Replace(" ago", string.Empty)}");
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"Code {roomId}");
        if (recent is not null)
            ImGui.TextDisabled($"You {(recent.WasHost ? "hosted" : "joined")} {UiTheme.DescribeAge(DateTime.UtcNow - recent.LastJoinedUtc)}");

        ImGui.EndTooltip();
    }

    private void Join(string roomId, string name, bool hasPassword, string? rememberedPassword)
    {
        if (!hasPassword)
        {
            roomSession.JoinRoom(roomId, string.Empty);
            return;
        }

        if (!string.IsNullOrEmpty(rememberedPassword))
        {
            roomSession.JoinRoom(roomId, rememberedPassword);
            return;
        }

        passwordRoomId = roomId;
        passwordRoomName = name;
        passwordInput = string.Empty;
        openPassword = true;
    }

    // ---------------------------------------------------------------- popups

    private void DrawCreatePopup()
    {
        if (openCreate)
        {
            ImGui.OpenPopup(CreatePopupId);
            openCreate = false;
        }

        ImGui.SetNextWindowSize(new Vector2(UiTheme.Px(340), 0));
        if (!ImGui.BeginPopupModal(CreatePopupId, ImGuiWindowFlags.NoResize))
            return;

        float width = ImGui.GetContentRegionAvail().X;
        string defaultName = $"{ServiceCollector.Config.DisplayName}'s room";
        bool isPublic = createVisibility == 0;
        bool hasPassword = createPassword.Length > 0;
        RoomKind kind = UiTheme.KindOf(isPublic, hasPassword);

        UiTheme.RoomPlate("##createPreview", createName.Trim().Length > 0 ? createName.Trim() : defaultName, kind, new Vector2(width, ImGui.GetFrameHeight() + UiTheme.Px(8)), out _);
        ImGui.Spacing();

        ImGui.TextUnformatted("Name");
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##createName", defaultName, ref createName, 40);

        ImGui.Spacing();
        ImGui.TextUnformatted("Visibility");
        ImGui.RadioButton("Public", ref createVisibility, 0);
        ImGui.SameLine();
        ImGui.RadioButton("Private", ref createVisibility, 1);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextDisabled(isPublic
            ? "Listed for everyone on this relay."
            : "Not listed. Share the room code to invite people.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.TextUnformatted("Password");
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##createPassword", "Optional", ref createPassword, 128, ImGuiInputTextFlags.Password);
        ImGui.TextDisabled(hasPassword ? "Locked: people need the password to join." : "Open: anyone who finds the room can join.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Max viewers");
        ImGui.SetNextItemWidth(width);
        ImGui.SliderInt("##maxViewers", ref createMaxViewers, 1, 16);

        ImGui.Spacing();
        ImGui.Separator();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonSize = new Vector2((width - spacing) / 2, ImGui.GetFrameHeight() * 1.2f);
        if (UiTheme.ColoredButton("Create", UiTheme.ColorOf(kind), buttonSize))
        {
            roomSession.CreateRoom(createName.Trim(), isPublic, createPassword, Math.Clamp(createMaxViewers, 1, 16));
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", buttonSize))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawJoinPopup()
    {
        if (openJoin)
        {
            ImGui.OpenPopup(JoinPopupId);
            openJoin = false;
        }

        ImGui.SetNextWindowSize(new Vector2(UiTheme.Px(280), 0));
        if (!ImGui.BeginPopupModal(JoinPopupId, ImGuiWindowFlags.NoResize))
            return;

        float width = ImGui.GetContentRegionAvail().X;
        ImGui.TextUnformatted("Room code");
        ImGui.SetNextItemWidth(width);
        bool submitted = ImGui.InputTextWithHint("##joinCode", "123456", ref joinCode, 16, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.TextUnformatted("Password");
        ImGui.SetNextItemWidth(width);
        submitted |= ImGui.InputTextWithHint("##joinPassword", "Only if the room has one", ref joinPassword, 128, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.Spacing();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonSize = new Vector2((width - spacing) / 2, ImGui.GetFrameHeight() * 1.2f);
        bool hasCode = joinCode.Trim().Length > 0;

        ImGui.BeginDisabled(!hasCode);
        if (ImGui.Button("Join", buttonSize) || (submitted && hasCode))
        {
            roomSession.JoinRoom(joinCode.Trim(), joinPassword);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", buttonSize))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawPasswordPopup()
    {
        if (openPassword)
        {
            ImGui.OpenPopup(PasswordPopupId);
            openPassword = false;
        }

        ImGui.SetNextWindowSize(new Vector2(UiTheme.Px(280), 0));
        if (!ImGui.BeginPopupModal(PasswordPopupId, ImGuiWindowFlags.NoResize))
            return;

        float width = ImGui.GetContentRegionAvail().X;
        ImGui.TextColored(UiTheme.LockedBlue, passwordRoomName);
        ImGui.TextDisabled("This room is password protected.");

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.SetNextItemWidth(width);
        bool submitted = ImGui.InputTextWithHint("##roomPassword", "Password", ref passwordInput, 128, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.Spacing();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonSize = new Vector2((width - spacing) / 2, ImGui.GetFrameHeight() * 1.2f);
        if ((ImGui.Button("Join", buttonSize) || submitted) && passwordRoomId is { } roomId)
        {
            roomSession.JoinRoom(roomId, passwordInput);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", buttonSize))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }
}
