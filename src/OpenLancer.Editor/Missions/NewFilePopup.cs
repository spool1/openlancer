using System;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The popup of the item "New file..." of a dropdown of a mission page (#18), in the form of New cutscene: the field
/// "Name", the install path under it, the note of the item, the live refusal, and Create and Cancel. Create is disabled
/// while the name has a refusal, and its tooltip gives the refusal. Create runs the batch of the item.
/// </summary>
/// <param name="id">The id of the popup in its window.</param>
internal sealed class NewFilePopup(string id)
{
    private readonly DrawnFrame drawn = new();
    private NewFileItem? item;
    private string name = "";
    private bool open;

    // True when the popup showed in its last draw.
    private bool shown;

    /// <summary>True while the popup is open or opens in the next draw. An agent call waits for it, because Create
    /// applies to the script of its start. A popup whose window does not draw any more is not open.</summary>
    public bool IsOpen => (open || shown) && drawn.Recent;

    /// <summary>Opens the popup of an item in the next draw, with the default name of the item in its field.</summary>
    public void Open(NewFileItem newFile)
    {
        item = newFile;
        name = newFile.DefaultName;
        open = true;
        drawn.Drew();
    }

    /// <summary>Closes the popup without a change, for example when the page shows another item.</summary>
    public void Close()
    {
        item = null;
        open = false;
    }

    /// <summary>Draws the popup. Call it in each frame in the ImGui id scope of its window.</summary>
    /// <param name="run">Runs the batch of Create. Returns true when the batch changed W.</param>
    public void Draw(Func<MissionBatch, bool> run)
    {
        drawn.Drew();
        if (open)
        {
            ImGui.OpenPopup(id);
            open = false;
        }

        shown = ImGui.BeginPopup(id);
        if (!shown)
        {
            return;
        }

        if (item is not { } current)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var scale = ImGuiHelper.Scale;
        ImGui.Text(current.Title);
        ImGui.Separator();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(260 * scale);
        // The buffer of the field also holds the zero at the end of the text.
        var enter = ImGui.InputText("Name", ref name, ThnEdits.MaxNameLength + 1, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.TextDisabled(current.PathOf(name));
        ImGui.SetItemTooltip("The install path of the new file. A save writes it as a whole file into the files folder " +
                             "of the active mod.");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360 * scale);
        ImGui.TextDisabled(current.Note);
        var refusal = current.Refusal(name);
        if (refusal != null)
        {
            ImGui.TextColored(EditorTheme.Error, refusal);
        }

        ImGui.PopTextWrapPos();
        ImGui.BeginDisabled(refusal != null);
        var create = ImGui.Button("Create") || enter && refusal == null;
        ImGui.EndDisabled();
        ImGui.SetItemTooltip(refusal ?? "Makes the file and names it in the line.");
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            Close();
            ImGui.CloseCurrentPopup();
        }
        else if (create && refusal == null && run(current.Make(name)))
        {
            Close();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
