using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Hierarchy;

/// <summary>
/// The edits of the tree of a mission (sections 8.4 and 8.9 of the M6b design): the "+" menu of the toolbar, F2, Ctrl+D
/// and Delete on the rows, the row menu items, the drags of rows onto formations, labels and acts, and the drop of a
/// ship tile of the Assets panel on the group "Cast". <see cref="MissionTreeEdits"/> makes each command.
/// </summary>
internal sealed partial class HierarchyPanel
{
    private readonly RenamePopup missionRename = new("##mission-rename");
    private MissionRef? missionRenaming;

    // A ship tile that dropped on the group "Cast", until the user picks the faction.
    private AssetRow? droppedShip;
    private bool openFaction;
    private string factionFilter = "";

    /// <summary>True while the rename popup of a row of a mission is open. An agent call waits for it (section 5.5, item 3
    /// of the MCP1 design).</summary>
    internal bool MissionRenaming => missionRename.IsOpen;

    /// <summary>True when the session takes edits of the open mission, or of the template of prefab mode.</summary>
    private bool MissionEditable => window.Missions.ShownPath is { } path && window.Missions.NoEditReason(path) == null;

    private void RunMission(MissionBatch? batch) => window.Missions.Run(batch);

    /// <summary>The "+" button of the toolbar in Mission mode and its menu (section 8.4 of the M6b design). The new item
    /// is selected.</summary>
    private void MissionCreateButton()
    {
        if (ToolbarItems.Button("create-mission", EditorIcons.Plus, "Add a trigger, a mission ship or another item of the mission",
                MissionEditable && window.Missions.Shown != null))
        {
            ImGui.OpenPopup("##create-mission");
        }

        ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##create-mission"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        if (window.Missions.Shown is { } analysis)
        {
            foreach (var (kind, label) in MissionTreeEdits.CreateKindsOf(analysis))
            {
                if (MenuItems.Item(label, MissionTreeEdits.CreateRefusal(analysis, kind)))
                {
                    RunMission(MissionTreeEdits.Create(analysis, kind));
                }
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The edit items of the menu of a row of a mission: Rename, Duplicate and Delete.</summary>
    private void MissionEditMenu(MissionRef item)
    {
        if (!MissionEditable || window.Missions.Shown is not { } analysis)
        {
            return;
        }

        var acts = window.Missions.ActsOf(analysis);
        var delete = MissionTreeEdits.Delete(analysis, item, acts);
        var duplicate = MissionTreeEdits.Duplicate(analysis, item);
        var renames = MissionTreeEdits.CanRename(analysis, item, acts);
        if (delete == null && duplicate == null && !renames)
        {
            return;
        }

        ImGui.Separator();
        if (MenuItems.Item(EditorIcons.Pen, "Rename", renames ? null : NoMissionRename))
        {
            StartMissionRename(item);
        }

        if (MenuItems.Item(EditorIcons.Clone, "Duplicate", duplicate == null ? NoMissionDuplicate : null))
        {
            RunMission(duplicate);
        }

        if (MenuItems.Item(EditorIcons.TrashAlt, "Delete", delete == null ? NoMissionDelete : null))
        {
            RunMission(delete);
        }
    }

    // The reasons of the edit items of a row of a mission that is not such an item.
    private const string NoMissionRename = "Rename changes only a trigger, an act or an item of the script with a nickname.";
    private const string NoMissionDuplicate = "Duplicate copies only a trigger, an item of the script or a prefab instance.";
    private const string NoMissionDelete = "Delete removes only a trigger, an item of the script, an act or a prefab instance.";

    private void StartMissionRename(MissionRef item)
    {
        missionRenaming = item;
        missionRename.Open(missionModel?.NodeOf(item)?.Name ?? "");
    }

    /// <summary>The keys of the tree of a mission: F2 renames, Ctrl+D duplicates and Delete deletes the selected row,
    /// through the same commands as the graph (section 8.9 of the M6b design).</summary>
    private void MissionKeys()
    {
        if (!MissionEditable || window.Missions.Shown is not { } analysis || window.Selection is not MissionRef item)
        {
            return;
        }

        var io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
        {
            RunMission(MissionTreeEdits.Duplicate(analysis, item));
        }

        if (io.KeyCtrl || io.KeyAlt)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            RunMission(MissionTreeEdits.Delete(analysis, item, window.Missions.ActsOf(analysis)));
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F2, false) && MissionTreeEdits.CanRename(analysis, item, window.Missions.ActsOf(analysis)))
        {
            StartMissionRename(item);
        }
    }

    /// <summary>The popup of a rename of a row of a mission. Enter renames, and Esc closes it.</summary>
    private void MissionRenamePopup()
    {
        if (missionRename.Draw() is { } name && missionRenaming is { } item && window.Missions.Shown is { } analysis)
        {
            RunMission(MissionTreeEdits.Rename(analysis, item, name, window.Missions.ActsOf(analysis)));
        }
    }

    /// <summary>
    /// The drags of a row of a mission (section 8.9 of the M6b design): a ship, a formation, a solar or a trigger row is
    /// a drag source. A formation, a label or an act row takes the drop. The group "Cast" takes a ship tile of the Assets
    /// panel.
    /// </summary>
    private void MissionDragAndDrop(HierarchyNode node)
    {
        if (!MissionEditable || window.Missions.Shown is not { } analysis)
        {
            return;
        }

        if (node.Item is MissionRef { Kind: MissionItemKind.Ship or MissionItemKind.Formation or MissionItemKind.Solar or
                MissionItemKind.Trigger } source && ImGui.BeginDragDropSource())
        {
            MissionDrag.Source(MissionDrag.TreeRowType, source, $"{node.Kind.Icon}  {node.Name}");
            ImGui.EndDragDropSource();
        }

        if (node.Item is MissionRef { Kind: MissionItemKind.Formation or MissionItemKind.Label or MissionItemKind.Act } target &&
            MissionDrag.Payload(MissionDrag.TreeRowType) is MissionRef dragged && ImGui.BeginDragDropTarget())
        {
            if (MissionTreeEdits.DropRefusal(analysis, dragged, target) is { } refusal)
            {
                ImGui.SetTooltip(refusal);
            }
            else if (ImGui.AcceptDragDropPayload(MissionDrag.TreeRowType) is { Delivery: true })
            {
                RunMission(MissionTreeEdits.Drop(analysis, dragged, target, window.Missions.ActsOf(analysis)));
            }

            ImGui.EndDragDropTarget();
        }

        if (node.Id is "cast" or "cast/ships")
        {
            window.AssetDrag.Cast(row =>
            {
                droppedShip = row;
                openFaction = true;
            });
        }
    }

    /// <summary>The menu of the faction of a dropped ship tile: the pick adds the mission ship as one undo entry.</summary>
    private void FactionMenu()
    {
        if (openFaction)
        {
            factionFilter = "";
            openFaction = false;
            ImGui.OpenPopup("##faction");
        }

        if (!ImGui.BeginPopup("##faction"))
        {
            droppedShip = null;
            return;
        }

        EditorTheme.PushMenuColors();
        ImGui.TextDisabled("Faction of the pilot");
        ImGui.SetNextItemWidth(240 * ImGuiHelper.Scale);
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputTextWithHint("##filter", "Search", ref factionFilter, 64);
        var session = window.Session;
        var factions = session?.Edits.Lists.List("faction")?.Entries ?? [];
        if (ImGui.BeginChild("##factions", new Vector2(240, 260) * ImGuiHelper.Scale))
        {
            foreach (var faction in factions.Where(f => factionFilter.Length == 0 ||
                                                        f.Value.Contains(factionFilter, StringComparison.OrdinalIgnoreCase) ||
                                                        (f.Section != null && session?.SectionName(f.Section) is { } name &&
                                                         name.Contains(factionFilter, StringComparison.OrdinalIgnoreCase))))
            {
                var label = faction.Section != null && session?.SectionName(faction.Section) is { Length: > 0 } text
                    ? $"{text} ({faction.Value})"
                    : faction.Value;
                if (ImGui.Selectable(label) && droppedShip is { } ship && window.Missions.Shown is { } analysis)
                {
                    var loadout = session?.Edits.Lists.List("loadout")?.Entries
                        .FirstOrDefault(e => string.Equals(e.Detail, ship.Asset.Id, StringComparison.OrdinalIgnoreCase))?.Value;
                    var pilot = session?.Edits.Lists.List("pilot")?.Entries.FirstOrDefault()?.Value;
                    RunMission(MissionTreeEdits.AddShip(analysis, ship.Asset.Id, loadout, pilot, faction.Value));
                    droppedShip = null;
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        ImGui.EndChild();
        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }
}
