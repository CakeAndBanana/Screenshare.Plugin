using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ScreenShare.Plugin.Rendering;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

/// <summary>
/// Popout window showing whichever room member ShareWindow is currently watching - separate from
/// the main room UI so it can be moved/resized/left open independently. Owns none of the actual
/// watch-session lifecycle itself (that stays entirely in ShareWindow, the single owner of
/// RoomSessionService/MediaSessionService/the decoded texture); this class only renders what it's
/// given and reports back when the user closes it. Deliberately just an ImGui window for now - a
/// natural spot to grow into an in-world "beamer" projection later without reworking how watching
/// gets triggered.
/// </summary>
public sealed class StreamViewWindow : Window
{
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);

    private readonly RoomSessionService roomSession;
    private readonly MediaSessionService mediaSession;
    private readonly Func<RawTextureWrap?> getPreviewTexture;
    private readonly Action onClosed;

    public StreamViewWindow(RoomSessionService roomSession, MediaSessionService mediaSession, Func<RawTextureWrap?> getPreviewTexture, Action onClosed)
        : base("Watching###ScreenShareStreamView")
    {
        this.roomSession = roomSession;
        this.mediaSession = mediaSession;
        this.getPreviewTexture = getPreviewTexture;
        this.onClosed = onClosed;
        Size = new Vector2(640, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Fires for every way this window can close - the X button, Esc, or ShareWindow
    /// setting IsOpen = false itself (e.g. the watched stream died, or the room was left) - since
    /// the underlying Dalamud Window only ever calls this once per open-to-close transition
    /// regardless of which triggered it. Closing this window is the only thing that stops the
    /// watch session; there's no separate "Stop watching" button duplicating that.</summary>
    public override void OnClose() => onClosed();

    /// <summary>Set here rather than in Draw() so it's current before ImGui.Begin uses it, not a
    /// frame behind.</summary>
    public override void PreDraw()
    {
        string? watchingId = mediaSession.ViewingStreamerConnectionId;
        string displayName = roomSession.Members.FirstOrDefault(m => m.ConnectionId == watchingId)?.DisplayName ?? "someone";
        WindowName = $"Watching {displayName}###ScreenShareStreamView";
    }

    public override void Draw()
    {
        if (mediaSession.LastError is not null)
            ImGui.TextColored(ErrorColor, $"Media stream error: {mediaSession.LastError}");

        RawTextureWrap? texture = getPreviewTexture();
        if (texture is null)
        {
            ImGui.TextUnformatted("Connecting...");
        }
        else
        {
            Vector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Min(available.X / texture.Width, available.Y / texture.Height);
            scale = Math.Min(scale, 1f);
            Vector2 displaySize = texture.Size * Math.Max(scale, 0.01f);
            ImGui.Image(texture.Handle, displaySize);
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"{mediaSession.ViewerFramesDecoded} frames decoded, {mediaSession.ViewerAudioPackets} audio packets");
        ImGui.TextUnformatted("Audio level:");
        ImGui.SameLine();
        ImGui.ProgressBar(Math.Clamp(mediaSession.ViewerAudioPeak, 0f, 1f), new Vector2(200, 0), "");

        if (mediaSession.ViewerVideoError is { } viewerVideoError)
            ImGui.TextColored(ErrorColor, $"Video decoder died mid-stream: {viewerVideoError.Message}");
    }
}
