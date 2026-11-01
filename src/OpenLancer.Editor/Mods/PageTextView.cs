using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// Draws a description in the format of <see cref="PageText"/> (section 8.4 of the M11c design): a large bold line for
/// <c>#</c>, a bold line for <c>##</c>, bullets, numbers, bold text and links. A click on a link opens the browser. A
/// BBCode tag shows as text with a tooltip, and a link that is not a web link shows a warning. The parser is the parser
/// of the export, so the preview and the files agree.
/// </summary>
internal static partial class PageTextView
{
    /// <summary>The tooltip of a BBCode tag in the preview.</summary>
    public const string TagTooltip = "Nexus reads this as a format code. The preview cannot show it.";

    /// <summary>The tooltip of a link that is not a web link.</summary>
    public const string BadLinkTooltip = "This link does not start with http:// or https://. The export writes it as text.";

    // Sizes in unscaled points.
    private const float ListIndent = 18;
    private const float ParagraphGap = 6;

    /// <summary>Draws the text in the width of the content region.</summary>
    public static void Draw(EditorWindow window, string text)
    {
        var blocks = PageText.Parse(text);
        if (blocks.Count == 0)
        {
            ImGui.TextDisabled("No description");
            return;
        }

        var indent = MathF.Truncate(ListIndent * ImGuiHelper.Scale);
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case PageBlockKind.Heading1:
                    ImGui.PushFont(null, EditorTheme.TitleSize);
                    Spans(window, block.Spans, bold: true, "", 0);
                    ImGui.PopFont();
                    break;
                case PageBlockKind.Heading2:
                    Spans(window, block.Spans, bold: true, "", 0);
                    break;
                case PageBlockKind.Bullet:
                    Spans(window, block.Spans, bold: false, "•", indent);
                    break;
                case PageBlockKind.Number:
                    Spans(window, block.Spans, bold: false, block.Marker, indent);
                    break;
                case PageBlockKind.Empty:
                    ImGui.Dummy(new Vector2(0, MathF.Truncate(ParagraphGap * ImGuiHelper.Scale)));
                    break;
                default:
                    Spans(window, block.Spans, bold: false, "", 0);
                    break;
            }
        }
    }

    /// <summary>
    /// Draws the parts of one line. The words wrap at the right edge of the content region. A line of a list has its
    /// marker in the indent.
    /// </summary>
    private static void Spans(EditorWindow window, IReadOnlyList<PageSpan> spans, bool bold, string marker, float indent)
    {
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + MathF.Max(indent + size, ImGui.GetContentRegionAvail().X);
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var boldOffset = MathF.Max(1, MathF.Round(ImGuiHelper.Scale));
        if (marker.Length > 0)
        {
            drawList.AddText(font, size, start, ImGui.GetColorU32(EditorTheme.TextDim), marker);
        }

        var x = start.X + indent;
        var y = start.Y;
        var hovers = new List<(Vector2 Min, Vector2 Max, PageSpan Span)>();
        foreach (var span in spans)
        {
            var (color, strong) = span.Kind switch
            {
                PageSpanKind.Bold => (EditorTheme.Text, true),
                PageSpanKind.Link => (EditorTheme.AccentText, bold),
                PageSpanKind.Tag => (EditorTheme.TextDim, bold),
                _ => (span.BadLink ? EditorTheme.Warning : EditorTheme.Text, bold),
            };
            foreach (Match word in WordPattern().Matches(span.Text))
            {
                var width = font.CalcTextSizeA(size, float.MaxValue, 0, word.Value).X;
                var inkWidth = font.CalcTextSizeA(size, float.MaxValue, 0, word.Value.TrimEnd()).X;
                if (x > start.X + indent && x + inkWidth > right)
                {
                    x = start.X + indent;
                    y += lineHeight;
                }

                var position = new Vector2(x, y);
                drawList.AddText(font, size, position, ImGui.GetColorU32(color), word.Value);
                if (strong)
                {
                    drawList.AddText(font, size, position + new Vector2(boldOffset, 0), ImGui.GetColorU32(color), word.Value);
                }

                if (span.Kind == PageSpanKind.Link)
                {
                    drawList.AddLine(new Vector2(x, y + size), new Vector2(x + inkWidth, y + size), ImGui.GetColorU32(color));
                }

                if (span.Kind is PageSpanKind.Link or PageSpanKind.Tag || span.BadLink)
                {
                    hovers.Add((position, position + new Vector2(inkWidth, size), span));
                }

                x += width;
            }
        }

        ImGui.Dummy(new Vector2(right - start.X, y + lineHeight - start.Y));
        Hover(window, hovers);
    }

    /// <summary>The tooltip of a link, a tag or a link that is not a web link, and the click on a link.</summary>
    private static void Hover(EditorWindow window, List<(Vector2 Min, Vector2 Max, PageSpan Span)> hovers)
    {
        if (!ImGui.IsWindowHovered())
        {
            return;
        }

        var mouse = ImGui.GetMousePos();
        foreach (var (min, max, span) in hovers)
        {
            if (mouse.X < min.X || mouse.Y < min.Y || mouse.X >= max.X || mouse.Y >= max.Y)
            {
                continue;
            }

            if (span.Kind == PageSpanKind.Link)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip($"Open {span.Url} in the browser.");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    FolderActions.OpenWebLink(window, ModsWindow.Source, span.Url!);
                }
            }
            else
            {
                ImGui.SetTooltip(span.Kind == PageSpanKind.Tag ? TagTooltip : BadLinkTooltip);
            }

            return;
        }
    }

    /// <summary>A word with the spaces after it, or a run of spaces at the start of a part.</summary>
    [GeneratedRegex(@"\S+\s*|\s+")]
    private static partial Regex WordPattern();
}
