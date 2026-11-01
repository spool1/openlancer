using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// The header of the detail page of the Mods window (section 10.3 of the M11c design): the display name in the large
/// font, the id, the version and the authors, and the chips of the state. The primary button Export... is at the top
/// right, with Play and the "..." menu next to it.
/// </summary>
internal sealed class ModHeader(EditorWindow window, ModsWindow mods)
{
    /// <summary>The look of a chip of a color of the catalog.</summary>
    internal static ChipLook LookOf(ChipColor color) => color switch
    {
        ChipColor.Blue => ChipLook.Filled(EditorTheme.Accent),
        ChipColor.Red => ChipLook.Filled(EditorTheme.Error),
        ChipColor.Green => ChipLook.Filled(EditorTheme.Success),
        ChipColor.Yellow => ChipLook.Filled(EditorTheme.Warning),
        ChipColor.WhiteOutline => ChipLook.Outline(EditorTheme.TextBright),
        // The mark "published". Violet stays for prefab overrides.
        ChipColor.GreenOutline => ChipLook.Outline(EditorTheme.Success),
        _ => ChipLook.Grey,
    };

    public void Draw(ModRow row)
    {
        ImGui.PushID("##header");
        var buttonsWidth = ButtonsWidth();
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(1, ImGui.GetContentRegionAvail().X - buttonsWidth));
        ImGui.PushFont(null, EditorTheme.HeroSize);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        CardLayout.Bold(row.Name);
        ImGui.PopStyleColor();
        ImGui.PopFont();
        var line = row.Manifest is { } manifest
            ? $"{row.Id} . {manifest.Version}" + (row.Settings.Authors.Count > 0 ? $" . by {string.Join(", ", row.Settings.Authors)}" : "")
            : row.Id;
        CardLayout.Dim(line);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        Buttons(row);
        ImGui.Spacing();
        Chip.Flow([.. row.Chips.Select(c => new ChipItem(c.Text, LookOf(c.Color), c.Tooltip))]);
        ImGui.PopID();
    }

    /// <summary>The width of the buttons at the right of the header.</summary>
    private static float ButtonsWidth()
    {
        var style = ImGui.GetStyle();
        return CardLayout.ButtonWidth("Export...") + ImGui.CalcTextSize($"{EditorIcons.Play}  Play {EditorIcons.CaretDown}").X +
               ImGui.CalcTextSize("...").X + 4 * style.FramePadding.X + 3 * style.ItemSpacing.X;
    }

    /// <summary>Export..., the Play menu and the "..." menu. A mod that does not load has Open folder only.</summary>
    private void Buttons(ModRow row)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - ButtonsWidth()));
        if (row.Manifest is not { } manifest)
        {
            if (ImGui.Button("Open folder"))
            {
                FolderActions.Open(window, row.Folder);
            }

            return;
        }

        var exportBlock = window.ExportBlock;
        if (CardLayout.Button(new CardButton("Export...", Primary: true, Enabled: exportBlock == null)))
        {
            mods.Export(row.Folder);
        }

        ItemTooltip.Show(exportBlock ??
                         "Make the packages of this mod for its target games, and the texts of the Nexus page.");
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Play}  Play {EditorIcons.CaretDown}"))
        {
            ImGui.OpenPopup("##play");
        }

        ImGui.SetItemTooltip("Play this mod alone in a linked game, without a package.");
        if (ImGui.BeginPopup("##play"))
        {
            PlayMenu(row, manifest);
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("..."))
        {
            ImGui.OpenPopup("##more");
        }

        if (ImGui.BeginPopup("##more"))
        {
            MoreMenu(row);
            ImGui.EndPopup();
        }
    }

    /// <summary>One item "Play this mod alone in &lt;game&gt;" for each play game of the mod (decision D577).</summary>
    private void PlayMenu(ModRow row, Data.Mods.ModManifest manifest)
    {
        var games = mods.PlayGames(manifest);
        if (games.Count == 0)
        {
            MenuItems.Note("No linked game can play this mod",
                "Play needs a linked game folder of a target game of the mod, with its game program. Link the game " +
                "folder in File > Game link.");
            return;
        }

        var block = mods.PlayBlock();
        foreach (var game in games)
        {
            if (MenuItems.Item($"Play this mod alone in {game.Name}", block,
                    $"Install the build of this mod for {game.Name} into the play copy of {game.Root}, and start the " +
                    "game. The play copy then holds this mod alone."))
            {
                mods.PlayAlone(row, game);
            }
        }
    }

    /// <summary>The "..." menu: Make active mod, Duplicate, Rename, the profile, the folders and the Console.</summary>
    private void MoreMenu(ModRow row)
    {
        var session = window.Session;
        var inProfile = row.Group == ModGroup.Profile;
        var active = session?.Edits.ActiveMod is { } current && GameFolders.Same(current.Folder, row.Folder);
        if (MenuItems.Item("Make active mod",
                !inProfile ? "Add the mod to the profile first. The active mod is a mod of the open profile."
                : active ? "This mod is the active mod."
                : !window.CanChangeProfile ? EditorWindow.ProfileLoading
                : null,
                "The editor saves your edits into the active mod.", selected: active))
        {
            window.SetActiveMod(row.Id);
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Duplicate..."))
        {
            mods.Duplicate(row);
        }

        ImGui.SetItemTooltip("Make a copy of the mod with a new id and a new display name. The copy is not in a profile.");
        if (MenuItems.Item("Rename...", mods.RenameBlock(row), "Give the mod a new id and a new folder name."))
        {
            mods.Rename(row);
        }

        ImGui.Separator();
        if (session != null && mods.ProfileNeedsAllowance)
        {
            if (ImGui.MenuItem("Allow edits of this repo profile"))
            {
                mods.AllowProfile();
            }

            ItemTooltip.Show("The profile file is a tracked file of the OpenLancer repo. Allow Add to profile and " +
                             "Remove from profile for this file until the editor closes.");
        }

        var changeBlock = mods.ProfileBlock();
        if (inProfile)
        {
            if (MenuItems.Item("Remove from profile", changeBlock,
                    "Remove the mod from the profile. The folder of the mod stays."))
            {
                mods.RemoveFromProfile(row);
            }
        }
        else
        {
            if (MenuItems.Item("Add to profile",
                    changeBlock ?? (row.Manifest == null ? row.LoadError ?? "The mod file did not load." : null),
                    "Add the mod to the end of the load order of the profile."))
            {
                mods.AddToProfile(row);
            }
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Open folder"))
        {
            FolderActions.Open(window, row.Folder);
        }

        if (ImGui.MenuItem("Open export folder"))
        {
            mods.OpenExportFolder(row);
        }

        if (ImGui.MenuItem("Show in Console"))
        {
            mods.ShowProblems(row);
        }

        ImGui.SetItemTooltip("Write the problems of the last check of the mod to the Console.");
    }
}
