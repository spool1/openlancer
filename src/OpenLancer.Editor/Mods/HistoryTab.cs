using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImGuiNET;
using OpenLancer.Data.Export;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// The History tab of the Mods window (section 10.6 of the M11c design), as the Version History tab of a Unity package:
/// a table of the exports, newest first, with the state of their files, and the test plays below it. A version can get
/// the mark "published" here.
/// </summary>
internal sealed class HistoryTab(EditorWindow window, ModsWindow mods)
{
    // The states of the files of the exports of the shown history. A state reads the SHA-256 of each package, so it
    // comes from a worker thread once for each scan.
    private PublishHistory? statesOf;
    private Task<Dictionary<DateTime, ExportFilesState>>? states;

    public void Draw(ModRow row)
    {
        var history = row.History;
        if (statesOf != history)
        {
            statesOf = history;
            var exports = history.Exports.ToList();
            states = Task.Run(() => exports.GroupBy(e => e.Time).ToDictionary(g => g.Key, g => PublishHistory.StateOf(g.First())));
        }

        if (history.Exports.Count == 0)
        {
            CardLayout.Dim(row.Chips.FirstOrDefault(c => c.Kind == Data.Publishing.ChipKind.ExportedBeforeHistory)?.Tooltip ??
                           "The history has no export of this mod yet. The history starts with the next export.");
        }
        else
        {
            Exports(row);
        }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.SeparatorText("Test plays");
        ImGui.PopStyleColor();
        Plays(row);
    }

    private void Exports(ModRow row)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit |
                                      ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("##exports", 6, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("Version");
        ImGui.TableSetupColumn("Date");
        ImGui.TableSetupColumn("Targets", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Packages");
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableHeadersRow();
        var known = states is { IsCompletedSuccessfully: true } done ? done.Result : null;
        foreach (var entry in row.History.Exports.OrderByDescending(e => e.Time))
        {
            ImGui.PushID(entry.Time.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(entry.Version);
            if (row.History.IsPublished(entry.Version))
            {
                ImGui.SameLine();
                Chip.Flow([new ChipItem("Published", ChipLook.Outline(EditorTheme.Success),
                    row.History.Published.FirstOrDefault(m => ModVersion.TextComparer.Equals(m.Version, entry.Version))?.Page)]);
            }

            ImGui.TableNextColumn();
            ImGui.Text(PublishMessages.Time(entry.Time));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(string.Join(", ", entry.Packages.Select(p => p.Target).Distinct()
                .Select(t => ExportTarget.Find(t)?.Name ?? t)));
            ImGui.TableNextColumn();
            Packages(entry);
            ImGui.TableNextColumn();
            ImGui.Text(known?.TryGetValue(entry.Time, out var state) == true ? StateText(entry, state) : "Checking...");
            ImGui.TableNextColumn();
            Actions(row, entry);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>The count of the packages. The node opens to show each file name, its SHA-256 and the folder.</summary>
    private static void Packages(HistoryExport entry)
    {
        if (!ImGui.TreeNodeEx(entry.Packages.Count == 1 ? "1 package" : $"{entry.Packages.Count} packages"))
        {
            return;
        }

        foreach (var package in entry.Packages)
        {
            ImGui.Text(package.File);
            ImGui.TextDisabled(package.Sha256);
        }

        ImGui.TextDisabled(entry.Folder);
        ImGui.TreePop();
    }

    /// <summary>Open folder, Copy hashes, and the menu with Mark as published and Remove the mark.</summary>
    private void Actions(ModRow row, HistoryExport entry)
    {
        var exists = Directory.Exists(entry.Folder);
        ImGui.BeginDisabled(!exists);
        if (CardLayout.IconButton(EditorIcons.FolderOpen, exists ? $"Open {entry.Folder}" : $"{entry.Folder} does not exist."))
        {
            FolderActions.Open(window, ModsWindow.Source, entry.Folder);
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (CardLayout.IconButton(EditorIcons.Copy, "Copy the file name and the SHA-256 of each package."))
        {
            ImGui.SetClipboardText(string.Join("\r\n", entry.Packages.Select(p => $"{p.File}\t{p.Sha256}")));
        }

        ImGui.SameLine();
        if (CardLayout.IconButton(EditorIcons.EllipsisV, "More actions"))
        {
            ImGui.OpenPopup("##entry");
        }

        if (!ImGui.BeginPopup("##entry"))
        {
            return;
        }

        var published = row.History.IsPublished(entry.Version);
        if (ImGui.MenuItem("Mark as published..."))
        {
            mods.ShowMarkDialog(row, entry.Version);
        }

        ImGui.SetItemTooltip("Record that this version is on Nexus. A version on Nexus never gets other game files.");
        if (MenuItems.Item("Remove the mark", published ? null : "This version has no mark."))
        {
            mods.Mark(row, entry.Version, null);
        }

        ImGui.EndPopup();
    }

    /// <summary>The plays of the history, newest first: the date, the game, alone or with other mods, and the time in
    /// the game. A play of older content is dim.</summary>
    private static void Plays(ModRow row)
    {
        if (row.History.Plays.Count == 0)
        {
            CardLayout.Dim("No test play yet. Play the mod in a linked game for one minute or more, or mark it as tested.");
            return;
        }

        foreach (var play in row.History.Plays.OrderByDescending(p => p.Time))
        {
            var older = play.Content != row.Content;
            ImGui.PushStyleColor(ImGuiCol.Text, older ? EditorTheme.TextDim : EditorTheme.Text);
            ImGui.TextWrapped(PlayText(play) + (older ? " (older content)" : ""));
            ImGui.PopStyleColor();
        }
    }

    /// <summary>The line of a play, for example "2026-10-19 11:30: HD Edition 0.7.1, alone, 6 min 52 s".</summary>
    internal static string PlayText(HistoryPlay play)
    {
        var time = PublishMessages.Time(play.Time);
        if (play.Manual)
        {
            return $"{time}: manual";
        }

        var game = play.Target is { } target ? ExportTarget.Find(target)?.Name ?? target : "unknown game";
        var length = play.Seconds >= 60 ? $"{play.Seconds / 60} min {play.Seconds % 60} s" : $"{play.Seconds} s";
        return $"{time}: {game}, {(play.Alone ? "alone" : "with other mods")}, {length}";
    }

    /// <summary>The state of the files of an export for the State column.</summary>
    internal static string StateText(HistoryExport entry, ExportFilesState state) => state switch
    {
        ExportFilesState.InPlace => "Files in place",
        ExportFilesState.NotFound => "Files not found",
        ExportFilesState.OtherFiles => "Other files",
        _ => $"Replaced on {PublishMessages.Time(entry.ReplacedBy ?? entry.Time)}",
    };
}
