using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ScreenShare.Plugin.Services;

namespace ScreenShare.Plugin.Ui.Helpers;

internal enum RoomKind
{
    Public,
    Locked,
    Private,
    Closed,
}

internal readonly record struct SliderEdit(bool Changed, bool Committed, bool Active);

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

    public static void Heading(string text, float scale = PluginFonts.HeadingScale, Vector4? color = null)
    {
        // A font actually built at this size, rather than the old Dummy-plus-AddText-at-a-bigger-
        // size trick: that stretched glyphs baked at the default size and came out blurry. With a
        // real font pushed, this is just ordinary text.
        using (PluginFonts.Push(scale))
        {
            if (color is { } c)
                ImGui.TextColored(c, text);
            else
                ImGui.TextUnformatted(text);
        }
    }

    /// <summary>A large icon-and-label action button.
    ///
    /// Drawn by hand rather than with Dalamud's ImGuiComponents.IconButtonWithText, which pushes
    /// its own font and draws the label through the draw list at a size it computes from the frame
    /// height - so it ignores a font pushed around it, and stretches glyphs baked at the default
    /// size. That is the blur; a font actually built at the label size is the fix.</summary>
    public static bool BigActionButton(
        string id, FontAwesomeIcon icon, string label, Vector2 size,
        Vector4? color = null, float labelScale = PluginFonts.ButtonLabelScale)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        bool clicked = color is { } accent
            ? ColoredButton($"##{id}", accent, size)
            : ImGui.Button($"##{id}", size);

        Vector2 iconSize = IconSize(icon);
        Vector2 labelSize;
        using (PluginFonts.Push(labelScale))
            labelSize = ImGui.CalcTextSize(label);

        // Measured with the fonts that actually draw, so the icon and label centre as one unit.
        float gap = Px(8);
        float left = min.X + MathF.Max(Px(6), (size.X - iconSize.X - gap - labelSize.X) / 2);
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, min + size, true);
        DrawIcon(drawList, new Vector2(left, min.Y + ((size.Y - iconSize.Y) / 2)), icon, textColor);

        using (PluginFonts.Push(labelScale))
        {
            // The pushed font's OWN size. Passing a multiple of it here is precisely the stretch
            // this method exists to avoid.
            drawList.AddText(
                ImGui.GetFont(), ImGui.GetFontSize(),
                new Vector2(left + iconSize.X + gap, min.Y + ((size.Y - labelSize.Y) / 2)),
                textColor, label);
        }

        drawList.PopClipRect();
        return clicked;
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


    private const float ValueCellWidth = 56f;
    private const int ValueEditMaxLength = 16;
    private const ImGuiSliderFlags ValueSliderFlags = ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput;
    private static string? editingValueId;
    private static string valueEditBuffer = string.Empty;
    private static bool valueEditFocusPending;
    private static int valueEditDrawnFrame = -1;

    public static float LabelColumn(params string[] labels)
    {
        float widest = 0f;
        foreach (string label in labels)
            widest = MathF.Max(widest, ImGui.CalcTextSize(label).X);

        return widest + Px(12);
    }

    public static void RowLabel(string label, float labelWidth)
    {
        if (labelWidth <= 0f || label.Length == 0)
            return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(labelWidth);
    }

    public static void EndFrame()
    {
        if (editingValueId is not null && valueEditDrawnFrame < ImGui.GetFrameCount() - 1)
            CancelValueEdit();
    }

    public static SliderEdit ValueSlider(
        string id, string label, ref int value, int min, int max, float labelWidth,
        string unit = "", float trackWidth = 0f, string? tooltip = null)
    {
        RowLabel(label, labelWidth);

        ImGui.SetNextItemWidth(TrackWidth(trackWidth));
        bool changed = ImGui.SliderInt(id, ref value, min, max, " ", ValueSliderFlags);
        bool committed = ImGui.IsItemDeactivatedAfterEdit();
        bool active = ImGui.IsItemActive();
        SliderTooltip(tooltip);

        string? typed = DrawValueCell(
            id, NumericEntry.Format(value, unit), NumericEntry.Format(value, string.Empty), tooltip, out bool editing);
        if (typed is not null && NumericEntry.TryParseClamped(typed, min, max, out int parsed) && parsed != value)
        {
            value = parsed;
            changed = true;
            committed = true;
        }

        return new SliderEdit(changed, committed, active || editing);
    }

    public static SliderEdit ValueSlider(
        string id, string label, ref float value, float min, float max, float labelWidth,
        int decimals = 2, string unit = "", float trackWidth = 0f, string? tooltip = null)
    {
        RowLabel(label, labelWidth);

        ImGui.SetNextItemWidth(TrackWidth(trackWidth));
        bool changed = ImGui.SliderFloat(id, ref value, min, max, " ", ValueSliderFlags);
        bool committed = ImGui.IsItemDeactivatedAfterEdit();
        bool active = ImGui.IsItemActive();
        SliderTooltip(tooltip);

        string? typed = DrawValueCell(
            id, NumericEntry.Format(value, decimals, unit), NumericEntry.Format(value, decimals, string.Empty),
            tooltip, out bool editing);
        if (typed is not null && NumericEntry.TryParseClamped(typed, min, max, out float parsed) && parsed != value)
        {
            value = parsed;
            changed = true;
            committed = true;
        }

        return new SliderEdit(changed, committed, active || editing);
    }

    private static float TrackWidth(float requested)
    {
        if (requested > 0f)
            return requested;

        float remaining = ImGui.GetContentRegionAvail().X - Px(ValueCellWidth) - ImGui.GetStyle().ItemSpacing.X;
        return MathF.Max(Px(60), remaining);
    }

    private static void SliderTooltip(string? tooltip)
    {
        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static string? DrawValueCell(string id, string display, string editSeed, string? tooltip, out bool editing)
    {
        ImGui.SameLine();

        float width = Px(ValueCellWidth);
        editing = editingValueId == id;

        if (!editing)
            return DrawValueDisplay(id, display, editSeed, tooltip, width, ref editing);

        valueEditDrawnFrame = ImGui.GetFrameCount();

        if (valueEditFocusPending)
        {
            ImGui.SetKeyboardFocusHere();
            valueEditFocusPending = false;
        }

        ImGui.SetNextItemWidth(width);
        bool submitted = ImGui.InputText($"{id}##valueEdit", ref valueEditBuffer, ValueEditMaxLength,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CharsDecimal);

        if (ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            CancelValueEdit();
            editing = false;
            return null;
        }

        if (!submitted && !ImGui.IsItemDeactivated())
            return null;

        string typed = valueEditBuffer;
        CancelValueEdit();
        editing = false;
        return typed;
    }

    private static string? DrawValueDisplay(
        string id, string display, string editSeed, string? tooltip, float width, ref bool editing)
    {
        var size = new Vector2(width, ImGui.GetFrameHeight());
        Vector2 min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"{id}##valueCell", size);
        bool hovered = ImGui.IsItemHovered();

        Vector2 textSize = ImGui.CalcTextSize(display);
        var at = new Vector2(min.X + size.X - textSize.X, min.Y + ((size.Y - textSize.Y) / 2));
        uint color = ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled);

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddText(at, color, display);

        if (hovered)
        {
            var underlineFrom = new Vector2(at.X, at.Y + textSize.Y);
            drawList.AddLine(underlineFrom, underlineFrom with { X = at.X + textSize.X }, color);
            ImGui.SetTooltip(tooltip is null
                ? "Click to type an exact value"
                : $"{tooltip}\n\nClick to type an exact value");
        }

        if (clicked)
        {
            editingValueId = id;
            valueEditBuffer = editSeed;
            valueEditFocusPending = true;
            valueEditDrawnFrame = ImGui.GetFrameCount();
            editing = true;
        }

        return null;
    }

    private static void CancelValueEdit()
    {
        editingValueId = null;
        valueEditBuffer = string.Empty;
        valueEditFocusPending = false;
    }
}
