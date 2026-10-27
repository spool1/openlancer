using System;
using System.IO;
using ImGuiNET;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// Duplicate... of the Mods window (decision D573, section 12.2 of the M11c design): a copy of the mod folder with a new
/// id, a new display name and no page link. The copy is not in a profile, and the window selects it.
/// </summary>
internal sealed class DuplicateModDialog(EditorWindow window, ModsWindow mods) : ModalDialog
{
    private ModRow? row;
    private string id = "";
    private string name = "";
    private string? error;

    protected override string Title => "Duplicate mod###duplicatemod";

    public void Show(ModRow mod)
    {
        row = mod;
        id = ModDuplicate.FreeId(mods.ModsFolder, mod.Id);
        name = ModDuplicate.DefaultName(mod.Name);
        error = null;
        Open();
    }

    protected override void Body()
    {
        if (row == null)
        {
            Close();
            return;
        }

        ImGui.TextWrapped($"Copy the mod {row.Name} ({row.Id}) to a new mod in {mods.ModsFolder}. The copy is not in a " +
                          "profile, and it has no page link. The publish history stays with the original mod.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputText("Mod id (also the folder name)", ref id, 64);
        ImGui.SetItemTooltip(ModManifest.IdRule);
        ImGui.InputText("Display name", ref name, PublishLimits.NameLength);
        ImGui.SetItemTooltip("The name that players see on Nexus, in FLMM and in the README.");
        var problem = !ModManifest.IsValidId(id) ? ModManifest.IdRule : PublishDraft.DisplayNameProblem(name);
        Problem(problem ?? error);
        ImGui.Spacing();
        if (Button("Duplicate", primary: true, enabled: problem == null))
        {
            Duplicate(row);
            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    private void Duplicate(ModRow source)
    {
        string folder;
        try
        {
            folder = ModDuplicate.Run(source.Folder, mods.ModsFolder, id, name, mods.GameRoots());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            error = ErrorText.Of(e);
            return;
        }

        Close();
        window.Log.Add(ConsoleSeverity.Info, ModsWindow.Source, $"Made {id} in {folder}, a copy of {source.Id}.");
        mods.Created(folder);
    }
}
