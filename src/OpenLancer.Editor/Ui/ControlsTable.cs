using System.Collections.Generic;
using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>The table of a help popover: one row for each input and its action, for example the controls of the
/// Scene. The action wraps at a readable width.</summary>
internal static class ControlsTable
{
    /// <summary>The wrap width of an action, in font sizes.</summary>
    private const float ActionWidth = 28;

    /// <summary>Draws a title, a separator and the rows.</summary>
    public static void Draw(string title, IEnumerable<(string Input, string Action)> rows)
    {
        ImGui.Text(title);
        ImGui.Separator();
        if (!ImGui.BeginTable("##controls", 2, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        foreach (var (input, action) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(input);
            ImGui.TableNextColumn();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetFontSize() * ActionWidth);
            ImGui.TextDisabled(action);
            ImGui.PopTextWrapPos();
        }

        ImGui.EndTable();
    }
}
