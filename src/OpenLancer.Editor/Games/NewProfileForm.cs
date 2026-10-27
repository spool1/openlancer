using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Games;

/// <summary>
/// The fields of a new profile for a game: the game, the profile name and the workspace folder (section 7.3 of the M11a
/// design). The first-run screen and the New profile dialog show it. Create makes the profile with an empty mod, links
/// the game, and opens the profile, as a new Unity project opens ready to edit (decisions D194 and D216).
/// </summary>
internal sealed class NewProfileForm(EditorWindow window)
{
    // Sizes in unscaled points.
    private const float LabelWidth = 110;
    private const float FieldWidth = 300;

    private IReadOnlyList<GameReport> games = [];
    private int selected;
    private string name = "";
    private string? error;

    // The name of the last check whether a profile with the name exists. The check reads the disk, so it runs only when
    // the name or the workspace changes.
    private (string Name, string Workspace, bool Taken) taken = ("", "", false);

    /// <summary>The game of the new profile.</summary>
    public GameReport? Game => selected < games.Count ? games[selected] : null;

    /// <summary>Starts a new form for one of the games. The form leaves out each game that the editor cannot use as a
    /// base, for example a source build (section 8.5 of the M11 design).</summary>
    public void Reset(IReadOnlyList<GameReport> choices)
    {
        games = choices.Where(g => g.CanBeBase).ToList();
        selected = 0;
        error = null;
        name = Game == null ? "" : ModWorkspace.FreeProfileName(window.Settings.WorkspaceOrDefault, Game.ProfileName);
    }

    /// <summary>True when the fields are valid.</summary>
    public bool CanCreate => Game != null && name.Length > 0 && Problem() == null;

    /// <summary>Draws the fields, with the labels in a column on the left, as the Inspector does.</summary>
    public void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.TextWrapped("OpenLancer makes a profile for this game. A profile names the game folder and your mods.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        if (games.Count > 1)
        {
            Field("Game");
            ImGui.SetNextItemWidth(InputWidth());
            if (ImGui.BeginCombo("##game", Game?.Name ?? ""))
            {
                for (var i = 0; i < games.Count; i++)
                {
                    if (ImGui.Selectable($"{games[i].Name}##{i}", i == selected))
                    {
                        selected = i;
                        name = ModWorkspace.FreeProfileName(window.Settings.WorkspaceOrDefault, games[i].ProfileName);
                    }

                    ImGui.SetItemTooltip(games[i].Root);
                }

                ImGui.EndCombo();
            }
        }

        Field("Profile name");
        ImGui.SetNextItemWidth(InputWidth());
        if (ImGui.InputText("##name", ref name, 64))
        {
            error = null;
        }

        ImGui.SetItemTooltip(GameTexts.ProfileNameRule);
        Field("Mods folder");
        const string change = "Change...";
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var mods = Path.Combine(window.Settings.WorkspaceOrDefault, ModWorkspace.ModsFolder);
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(change).X - gap);
        ImGui.TextDisabled(TextFit.CutLeft(mods, width, t => ImGui.CalcTextSize(t).X));
        ImGui.SetItemTooltip($"OpenLancer puts your mods in {mods}.");
        ImGui.SameLine(0, gap);
        if (ImGui.TextLink(change))
        {
            ChangeWorkspace();
        }

        ImGui.SetItemTooltip("Select the folder for new profiles and mods. The profiles that exist stay where they are.");
        ImGui.Spacing();
        CardLayout.Dim($"OpenLancer also makes an empty mod, {ModWorkspace.StarterModId}, for your changes.");
        if ((Problem() ?? error) is { } problem)
        {
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {problem}");
        }
    }

    /// <summary>The label of a field in the left column. The field goes right of it, on the same line.</summary>
    private static void Field(string label)
    {
        var x = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.Text(label);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.SetCursorPosX(x + MathF.Truncate(LabelWidth * ImGuiHelper.Scale));
    }

    /// <summary>The width of an input field: the free width of the line, up to a limit.</summary>
    private static float InputWidth() =>
        Math.Min(ImGui.GetContentRegionAvail().X, MathF.Truncate(FieldWidth * ImGuiHelper.Scale));

    /// <summary>
    /// Makes the profile and its empty mod, links the game when it is not linked, sets the mod as the active mod and
    /// opens the profile. Returns false and shows the error when a step fails. The profile is made first, so a failure
    /// links nothing.
    /// </summary>
    public bool Create()
    {
        if (Game is not { Root: { } root } game || !CanCreate)
        {
            return false;
        }

        try
        {
            var (path, mod) = ModWorkspace.CreateProfile(window.Settings.WorkspaceOrDefault, name, root,
                window.Links.Games.Select(g => g.Path).ToList());
            if (window.Links.Find(root) == null)
            {
                window.Links.Add(game);
            }

            window.Settings.SetActiveMod(path, mod);
            window.Settings.Save();
            window.Log.Add(ConsoleSeverity.Info, GameLinks.Source,
                $"Made the profile {path} for {game.Name}, with the empty mod '{mod}'.");
            window.OpenProfile(path);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or ModLoadException)
        {
            error = ErrorText.Of(e);
            return false;
        }
    }

    /// <summary>The problem of the name, or null.</summary>
    private string? Problem()
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (!ModManifest.IsValidId(name))
        {
            return GameTexts.ProfileNameRule;
        }

        var workspace = window.Settings.WorkspaceOrDefault;
        if (taken.Name != name || taken.Workspace != workspace)
        {
            taken = (name, workspace, ModWorkspace.FreeProfileName(workspace, name) != name);
        }

        return taken.Taken ? GameTexts.ProfileNameExists : null;
    }

    /// <summary>Asks for a new workspace folder. A folder whose profiles or mods folder is in a game folder is refused
    /// (decision D204). The new folder applies to new profiles only (decision D216).</summary>
    private void ChangeWorkspace() => FolderActions.Choose(window, folder =>
    {
        try
        {
            var roots = window.Links.Games.Select(g => g.Path).ToList();
            if (Game?.Root is { } root)
            {
                roots.Add(root);
            }

            ModWorkspace.CheckWorkspace(folder, roots);
            window.Settings.WorkspaceFolder = Path.GetFullPath(folder);
            window.Settings.Save();
            error = null;
            if (Game != null)
            {
                name = ModWorkspace.FreeProfileName(window.Settings.WorkspaceOrDefault, Game.ProfileName);
            }
        }
        catch (IOException e)
        {
            error = e.Message;
        }
    });
}
