using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>The colors of a chip.</summary>
/// <param name="Border">The color of the outline, or a transparent color for no outline.</param>
internal readonly record struct ChipLook(Vector4 Fill, Vector4 Border, Vector4 Text)
{
    /// <summary>The look of a fact without a state, for example a flag of the Inspector or a target game.</summary>
    public static ChipLook Grey => new(EditorTheme.SelectionUnfocused, EditorTheme.Transparent, EditorTheme.Text);

    /// <summary>A grey chip with text in the warning color, for example a chip of a line with a warning.</summary>
    public static ChipLook GreyWarning => Grey with { Text = EditorTheme.Warning };

    /// <summary>A filled chip in a state color, for example blue for unsaved edits or red for an error.</summary>
    public static ChipLook Filled(Vector4 color) =>
        new(color with { W = 0.3f }, color with { W = 0.7f }, EditorTheme.TextBright);

    /// <summary>A chip with an outline only, for example the active mod.</summary>
    public static ChipLook Outline(Vector4 color) => new(EditorTheme.Transparent, color, color);
}

/// <summary>One chip of a row of chips.</summary>
/// <param name="Tooltip">The tooltip of the chip, or null.</param>
/// <param name="Clickable">True for a chip that does something on a click, for example a system that opens. It gets the
/// accent outline under the mouse.</param>
internal readonly record struct ChipItem(string Text, ChipLook Look, string? Tooltip = null, bool Clickable = false);

/// <summary>
/// A chip: a short text in a small rounded box (decision D579 of the M11c design). The flags rows of the Inspector and
/// the state chips of the Mods window use it, so both look the same.
/// </summary>
internal static class Chip
{
    // Sizes in unscaled points.
    private const float Gap = 4;
    private const float Inset = 2;
    private const float PaddingX = 6;
    private const float Rounding = 3;

    /// <summary>
    /// Draws the chips from left to right in the caption size. They wrap at the right edge of the content region. The
    /// area of the chips is one item with the width of its widest row, so a table column that fits its content grows
    /// to the chips, and a tooltip of the caller can follow the item. A chip with a tooltip shows it on hover. Returns the
    /// index of a clickable chip that the user clicks in this frame, or -1.
    /// </summary>
    public static int Flow(IReadOnlyList<ChipItem> chips)
    {
        var scale = ImGuiHelper.Scale;
        var font = ImGui.GetFont();
        var fontSize = MathF.Round(EditorTheme.CaptionSize * scale);
        var available = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight();
        var inset = MathF.Truncate(Inset * scale);
        var padding = MathF.Truncate(PaddingX * scale);
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var sizes = new List<Vector2>(chips.Count);
        var widths = new List<float>(chips.Count);
        foreach (var chip in chips)
        {
            var size = font.CalcTextSizeA(fontSize, float.MaxValue, 0, chip.Text);
            sizes.Add(size);
            widths.Add(MathF.Ceiling(size.X) + 2 * padding);
        }

        var places = Layout(widths, available, MathF.Truncate(Gap * scale));
        var rows = 0;
        string? tooltip = null;
        var hovered = -1;
        var mouse = ImGui.GetMousePos();
        for (var i = 0; i < chips.Count; i++)
        {
            var (x, row) = places[i];
            rows = Math.Max(rows, row + 1);
            var min = start + new Vector2(x, row * height + inset);
            var max = min + new Vector2(widths[i], height - 2 * inset);
            var look = chips[i].Look;
            var over = mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y;
            if (over)
            {
                hovered = i;
            }

            drawList.AddRectFilled(min, max, ImGui.GetColorU32(look.Fill), Rounding * scale);
            var border = chips[i].Clickable && over && ImGui.IsWindowHovered() ? EditorTheme.AccentText : look.Border;
            if (border.W > 0)
            {
                drawList.AddRect(min, max, ImGui.GetColorU32(border), Rounding * scale);
            }

            drawList.AddText(font, fontSize,
                new Vector2(min.X + padding, min.Y + MathF.Truncate((max.Y - min.Y - sizes[i].Y) / 2)),
                ImGui.GetColorU32(look.Text), chips[i].Text);
            if (chips[i].Tooltip != null && over)
            {
                tooltip = chips[i].Tooltip;
            }
        }

        ImGui.Dummy(new Vector2(MathF.Max(1, Width(places, widths)), Math.Max(1, rows) * height));
        if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(tooltip);
        }

        return hovered >= 0 && chips[hovered].Clickable && ImGui.IsItemClicked() ? hovered : -1;
    }

    /// <summary>
    /// Paints one chip in a box on a draw list, for a view that places its chips itself. An example is the canvas of
    /// the mission graph. The chip has the look and the rounding of <see cref="Flow"/>. A clickable chip under the
    /// mouse gets the accent outline. The text ends with "..." when it does not fit.
    /// </summary>
    /// <param name="scale">The pixels of one point of the place, for example 1 in the canvas of the graph.</param>
    public static void Paint(ImDrawListPtr drawList, ImFontPtr font, float fontSize, Vector2 min, Vector2 max, string text,
        ChipLook look, float scale = 1, bool hovered = false, bool clickable = false)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(look.Fill), Rounding * scale);
        var border = clickable && hovered ? EditorTheme.AccentText : look.Border;
        if (border.W > 0)
        {
            drawList.AddRect(min, max, ImGui.GetColorU32(border), Rounding * scale);
        }

        var padding = PaddingX * scale;
        var y = min.Y + MathF.Truncate((max.Y - min.Y - fontSize) / 2);
        TextFit.AddText(drawList, font, fontSize, new Vector2(min.X + padding, y), ImGui.GetColorU32(look.Text), text,
            max.X - min.X - 2 * padding);
    }

    /// <summary>The width of a chip of a text in a font size, with its padding.</summary>
    /// <param name="scale">The pixels of one point of the place.</param>
    public static float WidthOf(ImFontPtr font, float fontSize, string text, float scale = 1) =>
        font.CalcTextSizeA(fontSize, float.MaxValue, 0, text).X + 2 * PaddingX * scale;

    /// <summary>The width of the widest row of the chips, in pixels.</summary>
    /// <param name="places">The places of <see cref="Layout"/>.</param>
    /// <param name="widths">The width of each chip, in pixels.</param>
    internal static float Width(IReadOnlyList<(float X, int Row)> places, IReadOnlyList<float> widths)
    {
        var width = 0f;
        for (var i = 0; i < places.Count; i++)
        {
            width = MathF.Max(width, places[i].X + widths[i]);
        }

        return width;
    }

    /// <summary>
    /// The place of each chip: its left edge from the start of the row, and its row from 0. A chip goes on the row of
    /// the chip before it when it fits. A chip that is wider than the space starts its own row.
    /// </summary>
    /// <param name="widths">The width of each chip, in pixels.</param>
    /// <param name="available">The width of the space, in pixels.</param>
    /// <param name="gap">The space between two chips of a row, in pixels.</param>
    internal static IReadOnlyList<(float X, int Row)> Layout(IReadOnlyList<float> widths, float available, float gap)
    {
        var places = new List<(float, int)>(widths.Count);
        var x = 0f;
        var row = 0;
        foreach (var width in widths)
        {
            if (x > 0 && x + width > available)
            {
                x = 0;
                row++;
            }

            places.Add((x, row));
            x += width + gap;
        }

        return places;
    }
}
