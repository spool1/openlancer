using System;
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// The look of the editor: the palette tokens, the font sizes and the ImGui style.
/// </summary>
/// <remarks>
/// The LibreLancer theme sets only 16 of the 58 ImGui colors. When the UI scale changes, it also restores its own
/// style and scales it (<see cref="Theme.Apply"/>). So this class writes all 58 colors and the absolute sizes again
/// after each scale change. It does not call ScaleAllSizes, because that would scale a second time each size that
/// this class does not write. Editor code takes its colors only from these tokens.
/// </remarks>
internal static class EditorTheme
{
    // Surfaces, from dark to light.
    public static readonly Vector4 Backdrop = Rgb(0x191919);
    public static readonly Vector4 Strip = Rgb(0x282828);
    public static readonly Vector4 Panel = Rgb(0x383838);
    public static readonly Vector4 Toolbar = Rgb(0x3C3C3C);
    public static readonly Vector4 Section = Rgb(0x3E3E3E);
    public static readonly Vector4 SectionHover = Rgb(0x474747);
    public static readonly Vector4 SectionLine = Rgb(0x1A1A1A);
    public static readonly Vector4 Field = Rgb(0x2A2A2A);
    public static readonly Vector4 Border = Rgb(0x232323);
    public static readonly Vector4 InnerLine = Rgb(0x303030);

    /// <summary>The hover fill of a flat button in a toolbar strip.</summary>
    public static readonly Vector4 ToolbarHover = Rgb(0xFFFFFF, 26 / 255f);

    /// <summary>No color, for example the fill of a flat button that the mouse is not over.</summary>
    public static readonly Vector4 Transparent = Vector4.Zero;

    // Text. TextDim has a contrast of at least 4.5:1 on Panel, Toolbar and Section.
    public static readonly Vector4 Text = Rgb(0xD2D2D2);
    public static readonly Vector4 Label = Rgb(0xC4C4C4);
    public static readonly Vector4 TextDim = Rgb(0xA8A8A8);

    /// <summary>The text of a selected item that must stand out, for example the selected Scene label.</summary>
    public static readonly Vector4 TextBright = Rgb(0xFFFFFF);

    /// <summary>The drop shadow below a glyph that draws over the scene.</summary>
    public static readonly Vector4 Shadow = Rgb(0x000000, 150 / 255f);

    // The accent and the states.
    public static readonly Vector4 Accent = Rgb(0x3A79BB);
    public static readonly Vector4 AccentStrong = Rgb(0x2C5D87);
    public static readonly Vector4 AccentText = Rgb(0x75A7E6);
    public static readonly Vector4 Pressed = Rgb(0x46607C);

    /// <summary>A toggle that is on, under the mouse. It stays blue, so the hover does not hide the state.</summary>
    public static readonly Vector4 PressedHover = Rgb(0x52708F);
    public static readonly Vector4 SelectionUnfocused = Rgb(0x4D4D4D);
    public static readonly Vector4 Error = Rgb(0xF0524A);
    public static readonly Vector4 Warning = Rgb(0xF4BC02);

    /// <summary>
    /// A check that passed. The palette has it for the states of the editor, but no view of this read-only editor
    /// shows a passed check yet.
    /// </summary>
    public static readonly Vector4 Success = Rgb(0x6BBF59);

    // The syntax colors of the raw INI editor (section 11.5 of the M6a design). The keywords use AccentText, and the
    // section headers use TextBright.
    /// <summary>A known key of a section type.</summary>
    public static readonly Vector4 SyntaxKey = Rgb(0x9ECCDB);

    /// <summary>A number.</summary>
    public static readonly Vector4 SyntaxNumber = Rgb(0xADCC9E);

    /// <summary>A text placeholder, for example <c>$str:</c>.</summary>
    public static readonly Vector4 SyntaxString = Rgb(0xDBB385);

    /// <summary>The items of the open mission in the Scene (decision D557 of the M6a design). Its hue differs by 25
    /// degrees or more from each other color of the theme and of the kinds, so no other meaning uses it.</summary>
    public static readonly Vector4 MissionItem = Rgb(0xF07AB4);

    /// <summary>The mark of a prefab override (section 8.6 of the M6b design, UI rule 7): the violet bar at the left edge
    /// of the label cell, and the dot of a collapsed linked instance with overrides. Its hue differs by 25 degrees or
    /// more from the colors of the states, and its contrast is 3:1 or more on the panels.</summary>
    public static readonly Vector4 Override = Rgb(0xA087F5);

    /// <summary>The run states of the dry run and of the live view in the mission graph (decision D761 of the M6c
    /// design): the outline of a trigger that waits, the flash of a trigger that ran and of its start links. Its hue
    /// differs by 25 degrees or more from the colors of a selection, an error, a warning, a success, the mission items
    /// and the prefab overrides. Each run state also has a glyph and a word, so no state depends on color alone.</summary>
    public static readonly Vector4 MissionRun = Rgb(0x3CCFA5);

    /// <summary>The Play button of a trigger card in the mission graph (note of 2026-10-27 in section 8.1 of the M6c
    /// design). The button shows a start whose checks passed, so it has the green of a passed check. Its hue differs by
    /// 25 degrees or more from the run states, a selection, an error, a warning, the mission items and the prefab
    /// overrides. The Play glyph and the tooltip also give its meaning.</summary>
    public static readonly Vector4 Play = Success;

    /// <summary>The brackets and lines of an item under the mouse, and of the items that the cross-links of the Mission
    /// tab name. It is a light grey, so it does not look like the blue of the selection (UI rule 7).</summary>
    public static readonly Vector4 Highlight = Label;

    // The mission graph (section 6 of the M6a design). The links use neutral greys only (decision D553).
    public static readonly Vector4 GraphNode = Rgb(0x2E2E2E);
    public static readonly Vector4 GraphNodeHeader = Rgb(0x3A3A3A);
    public static readonly Vector4 GraphNodeBorder = Rgb(0x555555);
    public static readonly Vector4 GraphFrame = Rgb(0xFFFFFF, 10 / 255f);
    public static readonly Vector4 GraphFrameBorder = Rgb(0xFFFFFF, 50 / 255f);
    public static readonly Vector4 GraphPrefabFrame = Rgb(0xFFFFFF, 18 / 255f);
    public static readonly Vector4 GraphLinkLight = Rgb(0xC8C8C8);
    public static readonly Vector4 GraphLinkMid = Rgb(0x8C8C8C);

    // The scene.
    public static readonly Vector4 AxisX = Rgb(0xE06C6C);
    public static readonly Vector4 AxisY = Rgb(0x7CC46A);
    public static readonly Vector4 AxisZ = Rgb(0x6C9BE0);
    public static readonly Vector4 GridLine = Rgb(0x808080, 0.45f);

    // Font sizes in unscaled points. Use them with ImGui.PushFont(null, size). ImDrawListPtr.AddText takes the final
    // pixel size, so multiply the size by ImGuiHelper.Scale for it.
    public const float CaptionSize = 13;
    public const float BodySize = 15;
    public const float TitleSize = 17;
    public const float HeroSize = 22;

    // This field must come after the tokens, because static fields initialize in the order of the text.
    private static readonly Vector4[] Palette = BuildPalette();
    private static float lastScale = -1;

    /// <summary>
    /// Writes the style when the UI scale changed. Call it each frame, right after <see cref="ImGuiHelper.NewFrame"/>,
    /// because that call restores the LibreLancer style when the scale changes.
    /// </summary>
    public static void ApplyIfScaleChanged()
    {
        var scale = ImGuiHelper.Scale;
        if (scale != lastScale)
        {
            Apply(ImGui.GetStyle(), scale);
            lastScale = scale;
        }

        CheckColors();
    }

    /// <summary>
    /// Writes the style again at a scale. The node editor of the engine restores the LibreLancer style when its canvas
    /// starts, ends, stops or goes on. Its canvas draws at the scale 1. So the mission graph calls this after each of
    /// these calls, with 1 in the canvas and the UI scale outside it.
    /// </summary>
    public static void Reapply(float scale) => Apply(ImGui.GetStyle(), scale);

    /// <summary>
    /// Pushes the menu colors. The normal hover color (white at alpha 16) is almost invisible on the popup background,
    /// so a hovered menu item uses the selection color. Call <see cref="PopMenuColors"/> after the menu.
    /// </summary>
    public static void PushMenuColors() => ImGui.PushStyleColor(ImGuiCol.HeaderHovered, AccentStrong);

    public static void PopMenuColors() => ImGui.PopStyleColor();

    /// <summary>
    /// Pushes the colors of flat buttons: no fill until the mouse is over a button. The toolbar strips and the small
    /// buttons in panels use them. Call <see cref="PopFlatButtonColors"/> after the buttons.
    /// </summary>
    public static void PushFlatButtonColors()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Transparent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ToolbarHover);
    }

    public static void PopFlatButtonColors() => ImGui.PopStyleColor(2);

    /// <summary>
    /// Pushes the colors of the primary button: the accent fill and white text. A page or a dialog has at most one
    /// primary button, for the action that the user does most. Call <see cref="PopPrimaryButtonColors"/> after it.
    /// </summary>
    public static void PushPrimaryButtonColors()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, AccentStrong);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Pressed);
        ImGui.PushStyleColor(ImGuiCol.Text, TextBright);
    }

    public static void PopPrimaryButtonColors() => ImGui.PopStyleColor(4);

    private static void Apply(ImGuiStylePtr style, float scale)
    {
        Vector2 Size(float x, float y) => new(MathF.Truncate(x * scale), MathF.Truncate(y * scale));
        float Length(float value) => MathF.Truncate(value * scale);

        style.WindowPadding = Size(8, 8);
        style.WindowRounding = 0;
        style.WindowMinSize = Size(32, 32);
        style.ChildRounding = 0;
        style.PopupRounding = Length(4);
        style.FramePadding = Size(6, 3);
        style.FrameRounding = Length(3);
        style.ItemSpacing = Size(6, 3);
        style.ItemInnerSpacing = Size(4, 4);
        style.CellPadding = Size(6, 2);
        style.IndentSpacing = Length(16);
        style.ScrollbarSize = Length(12);
        style.ScrollbarRounding = Length(6);
        style.GrabMinSize = Length(10);
        style.GrabRounding = Length(3);
        style.TabRounding = Length(3);
        style.TabBarOverlineSize = Length(2);
        style.ImageBorderSize = 0;

        // Border sizes stay 1 px at each scale. ImGuiStyle.ScaleAllSizes does not scale them either.
        style.WindowBorderSize = 0;
        style.ChildBorderSize = 1;
        style.PopupBorderSize = 1;
        style.FrameBorderSize = 1;
        style.TabBorderSize = 0;
        style.TabBarBorderSize = 1;
        style.SeparatorTextBorderSize = 1;

        style.DisabledAlpha = 0.5f;
        style.TreeLinesFlags = ImGuiTreeNodeFlags.DrawLinesNone;
        style.SelectableTextAlign = new Vector2(0, 0.5f);

        Palette.CopyTo(style.Colors);
    }

    /// <summary>
    /// Checks in debug builds that each of the 58 ImGui color slots has its theme value. A different value means that
    /// some code changed the style without a push and a pop, or that the LibreLancer colors came back.
    /// </summary>
    [Conditional("DEBUG")]
    private static void CheckColors()
    {
        var colors = ImGui.GetStyle().Colors;
        for (var i = 0; i < Palette.Length; i++)
        {
            if (colors[i] != Palette[i])
            {
                throw new InvalidOperationException(
                    $"The ImGui color {(ImGuiCol)i} is {colors[i]}, but the theme value is {Palette[i]}.");
            }
        }
    }

    /// <summary>
    /// The value for each ImGui color slot. A slot that <see cref="ColorOf"/> does not know stops the build of the
    /// table, so the table always has all 58 colors.
    /// </summary>
    private static Vector4[] BuildPalette()
    {
        var palette = new Vector4[(int)ImGuiCol.COUNT];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = ColorOf((ImGuiCol)i);
        }

        return palette;
    }

    private static Vector4 ColorOf(ImGuiCol slot) => slot switch
    {
        ImGuiCol.Text or ImGuiCol.InputTextCursor or ImGuiCol.CheckMark => Text,
        ImGuiCol.TextDisabled => TextDim,
        ImGuiCol.WindowBg => Panel,
        ImGuiCol.ChildBg or ImGuiCol.BorderShadow or ImGuiCol.ResizeGrip or ImGuiCol.TableRowBg or
            ImGuiCol.TabDimmedSelectedOverline => Transparent,
        ImGuiCol.PopupBg => Rgb(0x2E2E2E),
        ImGuiCol.Border or ImGuiCol.Separator or ImGuiCol.TableBorderStrong => Border,
        ImGuiCol.FrameBg => Field,
        ImGuiCol.FrameBgHovered or ImGuiCol.TableBorderLight => InnerLine,
        ImGuiCol.FrameBgActive => Rgb(0x242424),
        ImGuiCol.TitleBg or ImGuiCol.TitleBgActive => Strip,
        ImGuiCol.TitleBgCollapsed => Strip with { W = 128 / 255f },
        ImGuiCol.MenuBarBg => Backdrop,
        ImGuiCol.ScrollbarBg => Rgb(0x000000, 13 / 255f),
        ImGuiCol.ScrollbarGrab => Rgb(0x5F5F5F),
        ImGuiCol.ScrollbarGrabHovered => Rgb(0x686868),
        ImGuiCol.ScrollbarGrabActive => Rgb(0x7A7A7A),
        ImGuiCol.SliderGrab => Rgb(0x999999),
        ImGuiCol.Button => Rgb(0x585858),
        ImGuiCol.ButtonHovered => Rgb(0x676767),
        ImGuiCol.ButtonActive => Pressed,
        ImGuiCol.Header or ImGuiCol.HeaderActive => AccentStrong,
        ImGuiCol.HeaderHovered => Rgb(0xFFFFFF, 16 / 255f),
        ImGuiCol.SeparatorHovered or ImGuiCol.ResizeGripHovered => Accent with { W = 128 / 255f },
        ImGuiCol.SliderGrabActive or ImGuiCol.SeparatorActive or ImGuiCol.ResizeGripActive or
            ImGuiCol.TabSelectedOverline or ImGuiCol.PlotLinesHovered or ImGuiCol.PlotHistogram or
            ImGuiCol.DragDropTarget or ImGuiCol.NavCursor => Accent,
        ImGuiCol.Tab or ImGuiCol.TabDimmed => Rgb(0x2D2D2D),
        ImGuiCol.TabHovered => SectionHover,
        // The selected tab has the toolbar color, so that it joins the toolbar strip below it.
        ImGuiCol.TabSelected or ImGuiCol.TabDimmedSelected or ImGuiCol.TableHeaderBg => Toolbar,
        ImGuiCol.PlotLines => Rgb(0x9C9C9C),
        ImGuiCol.PlotHistogramHovered => Rgb(0x4F8FD1),
        ImGuiCol.TableRowBgAlt => Rgb(0xFFFFFF, 9 / 255f),
        ImGuiCol.TextLink => AccentText,
        ImGuiCol.TextSelectedBg => Accent with { W = 90 / 255f },
        ImGuiCol.TreeLines => Rgb(0x4D4D4D),
        ImGuiCol.NavWindowingHighlight => Rgb(0xFFFFFF, 179 / 255f),
        ImGuiCol.NavWindowingDimBg => Rgb(0x000000, 51 / 255f),
        ImGuiCol.ModalWindowDimBg => Rgb(0x000000, 140 / 255f),
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "The theme has no value for this ImGui color."),
    };

    /// <summary>A color from a hex value such as 0x3A79BB, and an alpha from 0 to 1.</summary>
    internal static Vector4 Rgb(uint rgb, float alpha = 1) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    /// <summary>
    /// The color as an ImGui ABGR value for a draw list. It rounds each channel as ImGui does. It needs no ImGui
    /// context and ignores the style alpha.
    /// </summary>
    internal static uint ToAbgr(Vector4 color)
    {
        static uint Channel(float value) => (uint)(Math.Clamp(value, 0, 1) * 255 + 0.5f);
        return Channel(color.W) << 24 | Channel(color.Z) << 16 | Channel(color.Y) << 8 | Channel(color.X);
    }
}
