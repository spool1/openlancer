using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Games;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The Missions tab (section 5 of the M6a design): the 14 story slots in slot order with their rules, states, counts and
/// OpenLancer gaps, then the other mission scripts. A row opens into its bundle. Unity: the Project window in list mode,
/// with the story chain as the scene list of Build Settings.
/// </summary>
internal sealed class MissionsBrowser(EditorWindow window, MissionWorkspace missions)
{
    /// <summary>The empty state of the Missions tab (#56).</summary>
    internal static readonly EmptyState NoScripts = new("The game data of this profile has no mission scripts.",
        "Open a profile of a game with mission scripts in DATA\\MISSIONS.");

    private const ImGuiTableFlags Flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
                                          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                                          ImGuiTableFlags.SizingFixedFit;

    private static readonly float[] Widths = [60, 200, 70, 80, 150, 140, 60, 50, 70, 150];

    private readonly MissionsBrowserModel model = new();
    private readonly ValuePicker systemPicker = new();
    private string search = "";
    private bool filterErrors;
    private bool filterWarnings;
    private bool filterGaps;
    private string? filterSystem;
    private string? selected;
    private bool pickSystem;

    // The search whose matching rows opened. A new search opens the rows with matching triggers once.
    private string openedSearch = "";

    // The game kind of the base folder of the profile, for the base label.
    private (string? Folder, Task<GameReport>? Report) game;

    /// <summary>Forgets the state of the profile.</summary>
    public void Reset()
    {
        selected = null;
        filterSystem = null;
        game = default;
        model.Reset();
    }

    public void Toolbar()
    {
        var scale = ImGuiHelper.Scale;
        ToolbarItems.SearchField("search", "Search missions, triggers and values", ref search, 260 * scale);
        ImGui.SameLine();
        if (ToolbarItems.Button("filter", EditorIcons.Filter, "Show only the missions that match."))
        {
            ImGui.OpenPopup("##filter");
        }

        SystemPicker();
        FilterMenu();
        ImGui.SameLine();
        BaseGameToggle(window, missions);
        ImGui.SameLine();
        ImGui.TextDisabled(MissionSummary.BaseLabel(GameReport()));
        ImGui.SetItemTooltip("The game folder of this profile.");
        ImGui.SameLine();
        InfoIcon.Draw("help", MissionSchema.Retail.Help["missionsTab"]);
    }

    /// <summary>The base-game toggle of the mission views: the Console count toggle "base-game messages", with the
    /// number of hidden mission problems (decision D527).</summary>
    internal static void BaseGameToggle(EditorWindow window, MissionWorkspace missions)
    {
        var count = missions.HiddenTotal;
        var showing = missions.ShowBaseGame;
        var tip = Logs.ConsolePanel.BaseGameToggleTip(count, showing);
        var value = ToolbarItems.CountToggle("basegame", EditorIcons.Lock, EditorTheme.TextDim,
            count.ToString("N0", CultureInfo.InvariantCulture), tip, showing);
        if (value != showing)
        {
            missions.SetShowBaseGame(value);
        }
    }

    /// <summary>True when the OpenLancer column and filter show: the active mod has no targets list without OpenLancer
    /// (AGENTS.md UI rule 4).</summary>
    private bool ShowsOpenLancer => window.Session?.Edits.HidesOpenLancer != true;

    private void FilterMenu()
    {
        if (!ImGui.BeginPopup("##filter"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        ImGui.MenuItem("Has errors", "", ref filterErrors);
        ImGui.MenuItem("Has warnings", "", ref filterWarnings);
        if (ShowsOpenLancer)
        {
            ImGui.MenuItem("Has OpenLancer gaps", "", ref filterGaps);
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Uses system...", filterSystem ?? ""))
        {
            pickSystem = true;
        }

        ImGui.SetItemTooltip("Pick a system. The tab then shows only the missions that use it.");
        if (filterSystem != null && ImGui.MenuItem("Any system"))
        {
            filterSystem = null;
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The system picker of the filter "Uses system..." (section 5.1 of the M6a design). It is the list picker
    /// of M21 with the systems that the missions name.</summary>
    private void SystemPicker()
    {
        var open = pickSystem;
        pickSystem = false;
        if (missions.Result is not { } result || window.Session is not { } session)
        {
            return;
        }

        var picked = systemPicker.Draw("##system-picker", open, filterSystem ?? "", () =>
        {
            var used = model.Systems(result).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return session.Edits.Lists.List("system")?.Entries.Where(e => used.Contains(e.Value))
                .Select(e => (e, Name(session, e))).ToList() ?? [];
        }, none: false, ObjectKinds.StarSystem);
        if (picked != null)
        {
            filterSystem = picked.Length == 0 ? null : picked;
        }
    }

    /// <summary>The display name of a list entry: the text of its name, or its nickname.</summary>
    private static string Name(EditorSession session, ListEntry entry) =>
        entry.Section != null && session.SectionName(entry.Section) is { Length: > 0 } name ? name : entry.Name ?? entry.Value;

    public void Draw()
    {
        if (window.Session?.GameData == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (missions.Result is not { } result)
        {
            PanelStates.Loading("Checking the mission scripts");
            return;
        }

        if (result.Scripts.Count == 0)
        {
            PanelStates.Message(NoScripts);
            return;
        }

        var term = search.Trim();
        var openMatches = term.Length > 0 && term != openedSearch;
        openedSearch = term;
        var rows = model.Rows(result, missions.ShowBaseGame).Where(r => Shows(result, r, term)).ToList();
        var columns = ShowsOpenLancer ? Widths.Length : MissionsBrowserModel.OpenLancerColumn;
        if (!ImGui.BeginTable("##missions", columns, Flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        ImGui.TableSetupScrollFreeze(1, 1);
        for (var c = 0; c < columns; c++)
        {
            ImGui.TableSetupColumn(MissionsBrowserModel.Columns[c].Name, ImGuiTableColumnFlags.WidthFixed, Widths[c] * scale);
        }

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var c = 0; c < columns; c++)
        {
            ImGui.TableSetColumnIndex(c);
            ImGui.TableHeader(MissionsBrowserModel.Columns[c].Name);
            ImGui.SetItemTooltip(MissionsBrowserModel.Columns[c].Tooltip);
        }

        var story = rows.Where(r => r.Script.Slot != null).ToList();
        var other = rows.Where(r => r.Script.Slot == null).ToList();
        Group("story", $"Story missions ({story.Count})", "The 14 story slots of the game code, in their fixed order.",
            story, result, defaultOpen: true, term, openMatches);
        if (other.Count > 0)
        {
            Group("other", $"Other mission scripts ({other.Count})",
                "Mission scripts that no story slot loads, for example test scripts and scripts of mods. The game can load " +
                "such a script in another way, for example from a new-game file.", other, result, defaultOpen: false, term,
                openMatches);
        }

        ImGui.EndTable();
    }

    private bool Shows(MissionRunResult result, MissionRow row, string term) =>
        model.Matches(result, row, term) &&
        (!filterErrors || row.Errors > 0) && (!filterWarnings || row.Warnings > 0) &&
        (!filterGaps || !ShowsOpenLancer || row.HasGaps) &&
        (filterSystem == null || row.Systems.Contains(filterSystem));

    /// <summary>The triggers of a row that match the search, or none.</summary>
    private IReadOnlyList<MissionTrigger> Matching(MissionRunResult result, MissionRow row, string term) =>
        result.Of(row.Script.Path) is { } analysis ? model.MatchingTriggers(result, analysis, term) : [];

    /// <param name="term">The search text without blanks at its ends.</param>
    /// <param name="openMatches">True in the first frame of a new search: the group and the rows with matching triggers
    /// open (section 5.1 of the M6a design).</param>
    private void Group(string id, string title, string tooltip, IReadOnlyList<MissionRow> rows, MissionRunResult result,
        bool defaultOpen, string term, bool openMatches)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (openMatches && rows.Any(r => Matching(result, r, term).Count > 0))
        {
            ImGui.SetNextItemOpen(true);
        }

        var open = ImGui.TreeNodeEx($"{title}###{id}",
            ImGuiTreeNodeFlags.SpanAllColumns | (defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : 0));
        ImGui.SetItemTooltip(tooltip);
        if (!open)
        {
            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            Row(rows[i], result, $"{id}{i}", term, openMatches);
        }

        ImGui.TreePop();
    }

    private void Row(MissionRow row, MissionRunResult result, string id, string term, bool openMatches)
    {
        ImGui.PushID(id);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var path = row.Script.Path;
        var analysis = result.Of(path);
        var matching = Matching(result, row, term);
        var isSelected = string.Equals(selected, path, StringComparison.OrdinalIgnoreCase);
        var flags = ImGuiTreeNodeFlags.SpanAllColumns | ImGuiTreeNodeFlags.OpenOnArrow |
                    (isSelected ? ImGuiTreeNodeFlags.Selected : 0) | (analysis == null ? ImGuiTreeNodeFlags.Leaf : 0);
        if (openMatches && matching.Count > 0)
        {
            ImGui.SetNextItemOpen(true);
        }

        var open = ImGui.TreeNodeEx($"{row.Slot}###row", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            selected = path;
            // A click selects the mission in each center tab, so the Inspector shows the Mission page (decision D548).
            if (analysis != null)
            {
                window.Selection = MissionRef.Of(path);
            }
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && analysis != null)
        {
            missions.OpenMission(path, show: true);
        }

        ItemTooltip.Show(row.Script.ModId is { } mod ? $"{row.ScriptText}\nMod: {mod}" : row.ScriptText);
        Cell(1, row.ScriptText, null);
        Cell(2, row.StartStage, row.StartTooltip);
        Cell(3, row.AcceptStage, row.AcceptTooltip);
        Cell(4, row.NextMission, row.NextTooltip);
        Cell(5, row.State, row.StateTooltip);
        Cell(6, row.Triggers.ToString(CultureInfo.InvariantCulture), null);
        Count(7, row.Errors, 0, EditorTheme.Error);
        Count(8, row.Warnings, row.Hidden, EditorTheme.Warning);
        if (ShowsOpenLancer)
        {
            Cell(MissionsBrowserModel.OpenLancerColumn, row.OpenLancer.Text, row.OpenLancer.Tooltip);
        }

        if (open)
        {
            if (analysis != null)
            {
                Triggers(matching);
                Bundle(result, analysis);
            }

            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void Cell(int column, string text, string? tooltip)
    {
        if (!ImGui.TableSetColumnIndex(column))
        {
            return;
        }

        ImGui.Text(text);
        if (tooltip != null)
        {
            ItemTooltip.Show(tooltip);
        }
    }

    /// <summary>A count cell: the count, and a dim count of the hidden base-game messages in brackets.</summary>
    private static void Count(int column, int count, int hidden, Vector4 color)
    {
        if (!ImGui.TableSetColumnIndex(column))
        {
            return;
        }

        if (count > 0)
        {
            ImGui.TextColored(color, count.ToString("N0", CultureInfo.InvariantCulture));
        }
        else
        {
            ImGui.Text("0");
        }

        if (hidden > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture, $"({hidden:N0})"));
            ItemTooltip.Show(BaseGameMessages.HiddenLine(hidden)!);
        }
    }

    /// <summary>The triggers that match the search, under their mission. A double-click opens the mission and selects
    /// the trigger.</summary>
    private void Triggers(IReadOnlyList<MissionTrigger> matching)
    {
        foreach (var trigger in matching)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TreeNodeEx($"{EditorIcons.Bolt}  {trigger.Nickname}###t{trigger.Section.Index}",
                ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAllColumns);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                missions.GoTo(trigger.Section.Ref, -1);
            }

            ItemTooltip.Show(trigger.Condition?.Sentence() ?? "The trigger has no condition.");
        }
    }

    private void Bundle(MissionRunResult result, MissionAnalysis analysis)
    {
        var rows = model.Bundle(result, analysis);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TreeNodeEx($"{row.Name}###b{i}",
                ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAllColumns);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                Act(row, analysis);
            }

            var tooltip = BundleTooltip(row);
            ItemTooltip.Show(tooltip);

            // Only a row that opens files has a menu of its files (AGENTS.md UI rule 4).
            if (row.Action == MissionBundleAction.OpenCutscene && row.Files.Count > 1 &&
                ImGui.BeginPopupContextItem($"##bundle{i}"))
            {
                EditorTheme.PushMenuColors();
                foreach (var file in row.Files)
                {
                    if (ImGui.MenuItem(file))
                    {
                        missions.OpenCutscene(file);
                    }
                }

                EditorTheme.PopMenuColors();
                ImGui.EndPopup();
            }

            Cell(1, row.Value, tooltip);
        }
    }

    /// <summary>The tooltip of a bundle row: what it holds, and what a double-click does.</summary>
    internal static string BundleTooltip(MissionBundleRow row)
    {
        if (row.ActionPhrase is not { } action)
        {
            return row.Tooltip;
        }

        var pick = row.Action == MissionBundleAction.OpenCutscene && row.Files.Count > 1 ? " Right-click to pick a file." : "";
        return $"{row.Tooltip}\nDouble-click to {action}.{pick}";
    }

    private void Act(MissionBundleRow row, MissionAnalysis analysis)
    {
        switch (row.Action)
        {
            case MissionBundleAction.OpenMission:
                missions.OpenMission(analysis.Script.Path, show: true);
                break;
            case MissionBundleAction.OpenCutscene when row.Files.Count > 0:
                missions.OpenCutscene(row.Files[0]);
                break;
            case MissionBundleAction.ShowSection when row.Section != null:
                missions.ShowSection(analysis.Script.Path, row.Section);
                break;
        }
    }

    /// <summary>The report of the base folder of the profile: the report of the game link, or a detect in a worker
    /// task.</summary>
    private GameReport? GameReport()
    {
        if (window.Session?.Profile.BaseFolder is not { } folder)
        {
            return null;
        }

        if (window.Links.ReportOf(folder) is { } linked)
        {
            return linked;
        }

        if (!string.Equals(game.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            game = (folder, Task.Run(() => GameDetector.Detect(folder)));
        }

        return game.Report is { IsCompletedSuccessfully: true } done ? done.Result : null;
    }
}
