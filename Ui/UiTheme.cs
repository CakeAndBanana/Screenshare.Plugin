using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui;

internal enum RoomKind
{
    Public,
    Locked,
    Private,
    Closed,
}

internal static class UiTheme
{
    public static readonly Vector4 PublicGreen = new(0.20f, 0.74f, 0.44f, 1f);
    public static readonly Vector4 LockedBlue = new(0.26f, 0.54f, 0.96f, 1f);
    public static readonly Vector4 PrivatePurple = new(0.62f, 0.42f, 0.92f, 1f);
    public static readonly Vector4 ClosedGrey = new(0.46f, 0.46f, 0.50f, 1f);
    public static readonly Vector4 LiveRed = new(0.94f, 0.27f, 0.27f, 1f);
    public static readonly Vector4 StreamingGreen = new(0.42f, 0.92f, 0.52f, 1f);
    public static readonly Vector4 ConnectedGreen = new(0.36f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 PendingAmber = new(1f, 0.72f, 0.22f, 1f);
    public static readonly Vector4 ErrorColor = new(1f, 0.42f, 0.42f, 1f);
    public static readonly Vector4 WarnColor = new(1f, 0.7f, 0.2f, 1f);
    public static readonly Vector4 CrownColor = new(1f, 0.85f, 0.2f, 1f);
    public static readonly Vector4 WatchingColor = new(0.4f, 0.75f, 1f, 1f);

    public static float Px(float value) => value * ImGuiHelpers.GlobalScale;

    public static RoomKind KindOf(bool isPublic, bool hasPassword)
        => hasPassword ? RoomKind.Locked : isPublic ? RoomKind.Public : RoomKind.Private;

    public static Vector4 ColorOf(RoomKind kind) => kind switch
    {
        RoomKind.Public => PublicGreen,
        RoomKind.Locked => LockedBlue,
        RoomKind.Private => PrivatePurple,
        _ => ClosedGrey,
    };

    public static FontAwesomeIcon IconOf(RoomKind kind) => kind switch
    {
        RoomKind.Public => FontAwesomeIcon.Globe,
        RoomKind.Locked => FontAwesomeIcon.Lock,
        RoomKind.Private => FontAwesomeIcon.EyeSlash,
        _ => FontAwesomeIcon.DoorClosed,
    };

    public static string Describe(bool isPublic, bool hasPassword)
        => (isPublic ? "Public" : "Private") + (hasPassword ? ", password protected" : ", open");

    public static uint Color(Vector4 color) => ImGui.GetColorU32(color);

    public static uint Color(Vector4 color, float alpha) => ImGui.GetColorU32(color with { W = color.W * alpha });

    public static Vector2 IconSize(FontAwesomeIcon icon)
    {
        using (ServiceCollector.PI.UiBuilder.IconFontHandle.Push())
            return ImGui.CalcTextSize(icon.ToIconString());
    }

    public static float IconButtonWidth(FontAwesomeIcon icon)
        => IconSize(icon).X + (ImGui.GetStyle().FramePadding.X * 2);

    public static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        using (ServiceCollector.PI.UiBuilder.IconFontHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
    }

    public static void DrawIcon(ImDrawListPtr drawList, Vector2 position, FontAwesomeIcon icon, uint color)
    {
        using (ServiceCollector.PI.UiBuilder.IconFontHandle.Push())
            drawList.AddText(position, color, icon.ToIconString());
    }

    public static bool RoomPlate(string id, string name, RoomKind kind, Vector2 size, out bool hovered, string? badge = null)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + size;
        bool clicked = ImGui.InvisibleButton(id, size);
        hovered = ImGui.IsItemHovered();

        Vector4 color = ColorOf(kind);
        bool dim = kind == RoomKind.Closed;
        float strength = dim ? 0.35f : hovered ? 0.85f : 0.6f;

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint left = Color(color, strength);
        uint right = Color(color, 0f);
        drawList.AddRectFilledMultiColor(min, max, left, right, right, left);
        drawList.AddRectFilled(min, new Vector2(min.X + Px(3), max.Y), Color(color, dim ? 0.6f : 1f));

        drawList.PushClipRect(min, max, true);

        uint textColor = ImGui.GetColorU32(dim ? ImGuiCol.TextDisabled : ImGuiCol.Text);
        float textY = min.Y + ((size.Y - ImGui.GetTextLineHeight()) / 2);
        float x = min.X + Px(10);

        FontAwesomeIcon icon = IconOf(kind);
        DrawIcon(drawList, new Vector2(x, textY), icon, textColor);
        x += IconSize(icon).X + Px(8);

        drawList.AddText(new Vector2(x, textY), textColor, name);

        if (badge is not null)
        {
            Vector2 badgeSize = PillSize(badge);
            var badgeMin = new Vector2(max.X - badgeSize.X - Px(8), min.Y + ((size.Y - badgeSize.Y) / 2));
            DrawPill(drawList, badgeMin, badge, LiveRed);
        }

        drawList.PopClipRect();
        return clicked;
    }

    public static Vector2 PillSize(string text)
        => ImGui.CalcTextSize(text) + new Vector2(Px(12), Px(2));

    public static void Pill(string text, Vector4 color)
    {
        Vector2 size = PillSize(text);
        float offset = MathF.Max(0f, (ImGui.GetFrameHeight() - size.Y) / 2);
        Vector2 min = ImGui.GetCursorScreenPos() + new Vector2(0, offset);

        ImGui.Dummy(new Vector2(size.X, ImGui.GetFrameHeight()));
        DrawPill(ImGui.GetWindowDrawList(), min, text, color);
    }

    private static void DrawPill(ImDrawListPtr drawList, Vector2 min, string text, Vector4 color)
    {
        Vector2 size = PillSize(text);
        float rounding = size.Y / 2;
        drawList.AddRectFilled(min, min + size, Color(color, 0.22f), rounding);
        drawList.AddRect(min, min + size, Color(color, 0.85f), rounding);
        drawList.AddText(min + new Vector2(Px(6), Px(1)), Color(color), text);
    }

    public static void StatusDot(Vector4 color)
    {
        float height = ImGui.GetFrameHeight();
        float radius = Px(4);
        Vector2 start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(radius * 2, height));
        ImGui.GetWindowDrawList().AddCircleFilled(start + new Vector2(radius, height / 2), radius, Color(color));
    }

    public static void SectionLabel(string text)
    {
        ImGui.Spacing();
        ImGui.TextDisabled(text.ToUpperInvariant());
    }

    public static void Heading(string text, float scale = 1.35f, Vector4? color = null)
    {
        float fontSize = ImGui.GetFontSize() * scale;
        Vector2 size = ImGui.CalcTextSize(text) * scale;
        Vector2 position = ImGui.GetCursorScreenPos();

        ImGui.Dummy(size);
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), fontSize, position, color is { } c ? Color(c) : ImGui.GetColorU32(ImGuiCol.Text), text);
    }

    public static bool ColoredButton(string label, Vector4 color, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color with { W = 0.7f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color with { W = 0.9f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        bool clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    public static string DescribeAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m ago",
        { TotalDays: < 1 } => $"{(int)age.TotalHours}h ago",
        _ => $"{(int)age.TotalDays}d ago",
    };
}
