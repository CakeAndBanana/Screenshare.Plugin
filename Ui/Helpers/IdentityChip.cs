using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Microsoft.AspNetCore.SignalR.Client;
using ScreenShare.Plugin.Networking;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui.Helpers;

internal sealed class IdentityChip(RoomSessionService roomSession, Func<bool> isStreaming)
{
    private const int MaxNameLength = 32;

    private bool editing;
    private bool focusEditor;
    private string nameInput = string.Empty;

    private static string PublicId => ClientIdentity.ToPublicId(ServiceCollector.Config.ClientId);

    public float Width
    {
        get
        {
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float body = editing
                ? UiTheme.Px(170)
                : ImGui.CalcTextSize(ServiceCollector.Config.DisplayName).X;

            return UiTheme.Px(8) + spacing + body + spacing + UiTheme.IconButtonWidth(editing ? FontAwesomeIcon.Check : FontAwesomeIcon.Pen);
        }
    }

    public void Draw()
    {
        bool reconnecting = roomSession.State == HubConnectionState.Reconnecting;
        UiTheme.StatusDot(reconnecting ? UiTheme.PendingAmber : UiTheme.ConnectedGreen);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(reconnecting ? "Reconnecting to the relay..." : "Connected");

        ImGui.SameLine();

        if (editing)
        {
            DrawEditor();
            return;
        }

        string name = ServiceCollector.Config.DisplayName;
        ImGui.AlignTextToFramePadding();
        if (isStreaming())
            ImGui.TextColored(UiTheme.StreamingGreen, name);
        else
            ImGui.TextUnformatted(name);

        if (ImGui.IsItemHovered())
            DrawTooltip();

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.SetClipboardText(PublicId);

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##renameSelf", FontAwesomeIcon.Pen))
            BeginEdit();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Change your display name");
    }

    private void BeginEdit()
    {
        nameInput = ServiceCollector.Config.DisplayName;
        editing = true;
        focusEditor = true;
    }

    private void DrawEditor()
    {
        if (focusEditor)
        {
            ImGui.SetKeyboardFocusHere();
            focusEditor = false;
        }

        ImGui.SetNextItemWidth(UiTheme.Px(170));
        bool submitted = ImGui.InputTextWithHint(
            "##displayNameEdit", "Display name", ref nameInput, MaxNameLength,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        if (ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            editing = false;
            return;
        }

        bool lostFocus = ImGui.IsItemDeactivated() && !submitted;

        ImGui.SameLine();
        bool confirmed = ImGuiComponents.IconButton("##confirmRename", FontAwesomeIcon.Check);

        if (submitted || confirmed || lostFocus)
            Commit();
    }

    private void Commit()
    {
        editing = false;
        string trimmed = nameInput.Trim();
        if (trimmed.Length > 0)
            roomSession.UpdateDisplayName(trimmed);
    }

    private void DrawTooltip()
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(ServiceCollector.Config.DisplayName);
        ImGui.Separator();
        ImGui.TextDisabled("Your ID");
        ImGui.TextUnformatted(PublicId);
        ImGui.TextDisabled("Your display name can change; your ID can't.");
        ImGui.Spacing();
        ImGui.TextDisabled("Relay");
        ImGui.TextUnformatted(roomSession.RelayUrl ?? ServiceCollector.Config.RelayUrl);
        if (roomSession.InstanceId is { } instanceId)
            ImGui.TextDisabled($"Instance {instanceId}");

        ImGui.Spacing();
        ImGui.TextDisabled("Right-click to copy your ID.");
        ImGui.EndTooltip();
    }
}
