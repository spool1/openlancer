using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// A small popup with the field "New name" for F2 and the menu item "Rename". Enter gives the new name, and Esc closes
/// the popup without a change. The Inspector and the Hierarchy use it for the items of a mission.
/// </summary>
/// <param name="id">The id of the popup in its window.</param>
internal sealed class RenamePopup(string id)
{
    private readonly DrawnFrame drawn = new();
    private string text = "";
    private bool open;

    // True when the popup showed in its last draw.
    private bool shown;

    /// <summary>True while the popup is open or opens in the next draw. An agent call waits for it, because its rename
    /// applies to the item of its start (section 5.5, item 3 of the MCP1 design). A popup whose window does not draw any
    /// more is not open.</summary>
    public bool IsOpen => (open || shown) && drawn.Recent(ImGui.GetFrameCount());

    /// <summary>Opens the popup in the next draw, with the name now in its field.</summary>
    public void Open(string name)
    {
        text = name;
        open = true;
        drawn.Drew(ImGui.GetFrameCount());
    }

    /// <summary>Draws the popup. Call it in each frame in the ImGui id scope of its window. Returns the new name after
    /// Enter, or null.</summary>
    public string? Draw()
    {
        drawn.Drew(ImGui.GetFrameCount());
        if (open)
        {
            ImGui.OpenPopup(id);
            open = false;
        }

        shown = ImGui.BeginPopup(id);
        if (!shown)
        {
            return null;
        }

        ImGui.Text("New name");
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        string? name = null;
        if (ImGui.InputText("##rename-text", ref text, 128, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            name = text;
            ImGui.CloseCurrentPopup();
            shown = false;
        }

        ImGui.EndPopup();
        return name;
    }
}
