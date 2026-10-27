using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Dialogs;

/// <summary>
/// Mod &gt; New mod: asks for the id and the display name of a new mod, makes its folder in the mods folder of the
/// workspace and adds it to the end of the mod list of the profile. Without a profile, for example from the Mods window
/// without an open profile, the mod goes into the mods folder only (section 12.1 of the M11c design).
/// </summary>
internal sealed class NewModDialog : ModalDialog
{
    private string id = "";
    private string name = "";
    private string? error;
    private string modsFolder = "";
    private string? profilePath;
    private string? baseFolder;
    private IReadOnlyCollection<string> gameRoots = [];
    private Action<string>? created;

    protected override string Title => "New mod";

    /// <param name="modsFolder">The folder that gets the folder of the mod.</param>
    /// <param name="profilePath">The profile file that gets the mod, or null for a mod without a profile.</param>
    /// <param name="baseFolder">The base folder of the profile, or null. OpenLancer does not write into it (decision
    /// D204).</param>
    /// <param name="gameRoots">The game folders that the mods folder must be outside of, without a profile.</param>
    /// <param name="created">Gets the id of the new mod.</param>
    public void Show(string modsFolder, string? profilePath, string? baseFolder, IReadOnlyCollection<string> gameRoots,
        Action<string> created)
    {
        (this.modsFolder, this.profilePath, this.baseFolder, this.gameRoots, this.created) =
            (modsFolder, profilePath, baseFolder, gameRoots, created);
        (id, name, error) = ("", "", null);
        Open();
    }

    protected override void Body()
    {
        ImGui.TextWrapped(profilePath != null
            ? $"The mod gets a folder in {modsFolder}, and goes at the end of the mod list of the profile. It becomes " +
              "the active mod."
            : $"The mod gets a folder in {modsFolder}. No profile is open, so the mod is not in a profile.");
        ImGui.Spacing();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputTextWithHint("Mod id (also the folder name)", "my-mod", ref id, 64);
        ImGui.SetItemTooltip($"The internal name of the mod and the name of its folder. The package files use it. " +
                             ModManifest.IdRule);
        ImGui.InputTextWithHint("Display name", ModWorkspace.StarterModName, ref name, PublishLimits.NameLength);
        ImGui.SetItemTooltip("The name that players see on Nexus, in FLMM and in the README. You can change it later " +
                             "in the Mods window.");
        var idProblem = id.Length > 0 && !ModManifest.IsValidId(id) ? ModManifest.IdRule : null;
        Problem(idProblem ?? error);
        ImGui.Spacing();
        if (Button("Create", primary: true, enabled: id.Length > 0 && idProblem == null))
        {
            Create();
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    private void Create()
    {
        // The mod goes into the profile file, so a profile file in the game folder would change a file of the game
        // (decision D214).
        if (profilePath != null && baseFolder != null && GameFolders.Overlaps(profilePath, baseFolder))
        {
            error = GameTexts.ProfileInGame;
            return;
        }

        string folder;
        try
        {
            if (profilePath == null)
            {
                // The mods folder of the workspace must be outside each linked game (decision D587).
                ModWorkspace.CheckWorkspace(Path.GetDirectoryName(Path.GetFullPath(modsFolder))!, gameRoots);
            }

            folder = ModWorkspace.CreateMod(modsFolder, id, name, baseFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ErrorText.Of(e);
            return;
        }

        if (profilePath != null)
        {
            try
            {
                ModWorkspace.AddToProfile(profilePath, folder);
            }
            catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException)
            {
                // The profile does not list the mod, so its new folder goes again.
                Directory.Delete(folder, recursive: true);
                error = e.Message;
                return;
            }
        }

        Close();
        created?.Invoke(id);
    }
}
