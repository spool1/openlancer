using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>The tooltip of the last item, also when the item is disabled, so that a disabled item tells why the user
/// cannot use it. It also draws the tooltips that wrap at a readable width.</summary>
internal static class ItemTooltip
{
    /// <summary>The wrap width of <see cref="Wrapped"/>, in font sizes.</summary>
    private const float WrapWidth = 30;

    /// <summary>Shows the text as the tooltip of the last item while the mouse is on it, after the usual delay of a
    /// tooltip.</summary>
    public static void Show(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(text);
        }
    }

    /// <summary>Draws a tooltip now, in screen space, that wraps at a readable width. A line that starts with the warning
    /// glyph draws in the warning color, as the warnings of the Play start page do.</summary>
    public static void Wrapped(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * WrapWidth);
        if (text.Contains(EditorIcons.ExclamationTriangle))
        {
            foreach (var line in text.Split('\n'))
            {
                if (IsWarning(line))
                {
                    ImGui.TextColored(EditorTheme.Warning, line);
                }
                else
                {
                    ImGui.Text(line);
                }
            }
        }
        else
        {
            ImGui.Text(text);
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>True for a line of a tooltip that starts with the warning glyph. <see cref="Wrapped"/> draws it in the
    /// warning color.</summary>
    internal static bool IsWarning(string line) => line.StartsWith(EditorIcons.ExclamationTriangle);
}
