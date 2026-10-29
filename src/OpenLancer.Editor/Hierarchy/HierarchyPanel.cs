using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Hierarchy;

/// <summary>
/// The tree of the open system, or of the open cutscene while the Cutscene tab shows (section 7.4 of the M8 design).
/// The tree of a system has the system row, then the objects, zones and lights in groups by kind. The tree of a
/// cutscene has the cutscene row, then the entities in the groups of the Timeline. A click selects an item, and a
/// double-click frames it in the Scene or in the Free mode of the Cutscene view. The arrow keys move the selection
/// along the visible rows.
/// </summary>
/// <remarks>
/// The rows of a system take a tile of the Assets panel, and a row drags onto the prop groups of the Assets panel
/// (section 8.1 of the M23 design). The rows of a cutscene take no drop (review finding R20).
/// </remarks>
internal sealed partial class HierarchyPanel(EditorWindow window)
{
    /// <summary>The id of the panel. The layout reports its focus by this id.</summary>
    public const string PanelId = "hierarchy";

    private const string SearchTip =
        "Search by name or nickname. A t: word shows one type, for example t:station.\n" +
        "Types: sun, planet, station, ring, gate, hole, lane, satellite, platform, zone, field, nebula, light.";

    private const string CutsceneSearchTip =
        "Search by name. A t: word shows one type, for example t:camera.\n" +
        "Types: camera, monitor, character, prop, light, effect, sound, marker, path, scene, other.";

    private const string MissionSearchTip =
        "Search by nickname, name or value. A t: word shows one type, for example t:ship.\n" +
        "Types: trigger, ship, formation, solar, loot, npc, label, orders, objective, conversation, encounter, prefab.";

    private HierarchyModel? model;
    private HierarchyModel? cutsceneModel;
    private HierarchyModel? missionModel;
    private string search = "";
    private string cutsceneSearch = "";
    private string missionSearch = "";

    // The selection at the last frame. A different selection that another panel made opens the tree to its row.
    private object? lastSeenSelection;

    // The item of the row that shows the selection in this frame, for example the trigger of a selected line.
    private object? rowSelection;

    // The row to scroll to in the next frame, and true to put it in the middle of the panel.
    private (object Item, bool Center)? scrollRequest;

    /// <summary>Draws the items of the toolbar strip: the Create button of a system (decision D255 of the M3 design), and
    /// the search field with a clear button in it.</summary>
    public void Toolbar()
    {
        if (window.CenterMode == CenterMode.Cutscene)
        {
            ToolbarItems.SearchField("search-cutscene", "Search (t:camera)", ref cutsceneSearch, -1, CutsceneSearchTip);
            return;
        }

        if (window.CenterMode == CenterMode.Mission)
        {
            MissionCreateButton();
            ImGui.SameLine();
            ToolbarItems.SearchField("search-mission", "Search (t:ship)", ref missionSearch, -1, MissionSearchTip);
            return;
        }

        if (ToolbarItems.Button("create", EditorIcons.Plus, "Create an object, a zone or a light",
                window.SceneEdits.Unavailable(Data.Editing.Scene.SceneAction.Create, null) == null))
        {
            ImGui.OpenPopup("##create");
        }

        // The menu opens below the button.
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("##create"))
        {
            EditorTheme.PushMenuColors();
            window.SceneEdits.CreateMenu(null);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ToolbarItems.SearchField("search", "Search (t:station)", ref search, -1, SearchTip);
    }

    /// <summary>Sets the text of the search field of the system tree, for example "t:zone" to show only the
    /// zones.</summary>
    public void SetSearch(string text) => search = text;

    /// <summary>Forgets the trees of the last system and cutscene. Call it when the profile closes.</summary>
    public void Reset()
    {
        model = null;
        cutsceneModel = null;
        missionModel = null;
        lastSeenSelection = null;
        scrollRequest = null;
    }

    public void Draw()
    {
        var session = window.Session;
        if (session?.GameData == null)
        {
            PanelStates.NoGameData(session);
            return;
        }

        HierarchyModel tree;
        if (window.CenterMode == CenterMode.Cutscene)
        {
            if (window.Cutscenes.Timeline is not { } timeline)
            {
                PanelStates.Message("No cutscene is open.", "Open a cutscene in the Cutscenes tab.");
                return;
            }

            tree = HierarchyModel.ForCutscene(timeline, cutsceneModel);
            Show(tree, cutsceneModel);
            cutsceneModel = tree;
            tree.SetSearch(cutsceneSearch);
        }
        else if (window.CenterMode == CenterMode.Mission)
        {
            // In Mission mode, the tree shows the open mission (section 9 of the M6a design). In prefab mode, it shows the
            // fields, the ports and the sections of the template (section 8.6 of the M6b design).
            if (window.Missions.PrefabMode is { Analysis: not null } prefab)
            {
                tree = HierarchyModel.ForPrefab(prefab, window.Missions.ShowBaseGame, missionModel);
            }
            else if (window.Missions.Open is not { } analysis)
            {
                PanelStates.Message("No mission is open.", "Double-click a mission in the Missions tab.");
                return;
            }
            else
            {
                tree = HierarchyModel.ForMission(analysis, id => session.Name(id), window.Missions.ShowBaseGame, missionModel,
                    session.SystemName, window.Missions.ActsOf(analysis));
            }

            Show(tree, missionModel);
            missionModel = tree;
            tree.SetSearch(missionSearch);
        }
        else
        {
            if (window.Scene.Index is not { } index)
            {
                PanelStates.Message("No system is open.", "Select a system in the Scene toolbar.");
                return;
            }

            // A new index or a new name builds the tree again. The tree of the same system keeps its open groups.
            tree = HierarchyModel.For(index, window.Scene.NamesVersion, session.TextOf, model);
            Show(tree, model);
            model = tree;
            tree.SetSearch(search);
        }

        var selection = window.Selection;
        rowSelection = tree.RowItemOf(selection);
        if (!Equals(selection, lastSeenSelection))
        {
            lastSeenSelection = selection;
            if (tree.Reveal(rowSelection))
            {
                scrollRequest = (rowSelection!, true);
            }
        }

        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        if (IsScene)
        {
            FollowRename(tree);
        }

        Rows(tree, focused);
        // The empty space below the rows takes a tile: the asset spawns at the camera pivot.
        if (IsScene && ImGui.GetContentRegionAvail().Y > 1)
        {
            ImGui.Dummy(new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), ImGui.GetContentRegionAvail().Y));
            window.AssetDrag.Hierarchy(null);
        }

        if (tree.IsSearching && tree.Root.Matches == 0)
        {
            PanelStates.Message(
                window.CenterMode == CenterMode.Cutscene ? "No entity matches the search."
                : window.CenterMode == CenterMode.Mission ? "No item matches the search."
                : "No object matches the search.",
                "Esc in the search field clears it.");
        }

        // A right click on empty space opens the Create menu (decision D255 of the M3 design).
        if (IsScene &&
            ImGui.BeginPopupContextWindow("##hierarchy-create", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            EditorTheme.PushMenuColors();
            window.SceneEdits.CreateMenu(null);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        if (window.CenterMode == CenterMode.Mission)
        {
            MissionRenamePopup();
            FactionMenu();
        }

        if (focused && !ImGui.GetIO().WantTextInput)
        {
            Keys(tree);
            if (IsScene)
            {
                window.SceneEdits.Keys(window.Selection);
            }
            else if (window.CenterMode == CenterMode.Mission)
            {
                MissionKeys();
            }
        }
    }

    /// <summary>True while the tree shows the open system: the edits of the Scene apply to its rows.</summary>
    private bool IsScene => window.CenterMode == CenterMode.Scene;

    /// <summary>
    /// Opens the tree to the row of the rename and scrolls to it. A rename of an item that the scene does not show
    /// ends, because it has no row.
    /// </summary>
    private void FollowRename(HierarchyModel tree)
    {
        if (window.SceneEdits.Renaming is not { } section || !window.SceneEdits.RenameStarted)
        {
            return;
        }

        if (window.Scene.ItemOf(section) is not { } item || item is StarSystem)
        {
            if (!window.Scene.IsBehind)
            {
                window.SceneEdits.CancelRename();
            }

            return;
        }

        if (tree.Reveal(item))
        {
            scrollRequest = (item, true);
        }
        else if (tree.NodeOf(item) != null)
        {
            // The search hides the row, so the field cannot show.
            window.SceneEdits.CancelRename();
        }
    }

    /// <summary>A tree of another system or cutscene than the tree before forgets the selection that it saw.</summary>
    private void Show(HierarchyModel tree, HierarchyModel? before)
    {
        if (!Equals(tree.Identity, before?.Identity))
        {
            lastSeenSelection = null;
            scrollRequest = null;
        }
    }

    /// <summary>
    /// Draws the visible rows. The clipper skips the rows outside the view. A row that must scroll into view is drawn
    /// also when it is outside the view.
    /// </summary>
    private void Rows(HierarchyModel tree, bool focused)
    {
        var rows = tree.Rows;
        var scrollIndex = scrollRequest is { } request ? tree.IndexOf(request.Item) : -1;
        var center = scrollRequest is { Center: true };
        scrollRequest = null;

        ListRow.PushStyle(focused);
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        if (scrollIndex >= 0)
        {
            clipper.IncludeItemByIndex(scrollIndex);
        }

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                Row(tree, rows[i], right);
                if (i == scrollIndex)
                {
                    ListRow.ScrollToRow(center);
                }
            }
        }

        clipper.End();
        ListRow.PopStyle();
    }

    private void Row(HierarchyModel tree, HierarchyRow row, float right)
    {
        var node = row.Node;
        var style = ImGui.GetStyle();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + row.Depth * style.IndentSpacing);
        var start = ImGui.GetCursorScreenPos();

        // A group opens and closes with a click on its row. The system row selects the system with a click, so it
        // opens and closes only with its arrow.
        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding |
                    ImGuiTreeNodeFlags.NoTreePushOnOpen;
        var isGroup = node.Children.Count > 0;
        if (isGroup)
        {
            ImGui.SetNextItemOpen(tree.IsOpen(node), ImGuiCond.Always);
        }
        else
        {
            flags |= ImGuiTreeNodeFlags.Leaf;
        }

        if (node == tree.Root)
        {
            flags |= ImGuiTreeNodeFlags.OpenOnArrow;
        }

        if (node.Item != null && node.Item.Equals(rowSelection))
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        var open = ImGui.TreeNodeEx(node.Label, flags);
        if (isGroup && open != tree.IsOpen(node))
        {
            tree.SetOpen(node, open);
        }

        if (node.Item is { } item && !ImGui.IsItemToggledOpen())
        {
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) || ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                Select(item);
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                Frame(item);
            }
        }

        if (node.Tooltip != null)
        {
            ImGui.SetItemTooltip(node.Tooltip);
        }

        if (IsScene)
        {
            DragAndDrop(node);
        }
        else if (window.CenterMode == CenterMode.Mission)
        {
            MissionDragAndDrop(node);
        }

        if (ImGui.BeginPopupContextItem())
        {
            EditorTheme.PushMenuColors();
            if (node.Item != null)
            {
                ContextMenu(node.Item);
            }
            else if (IsScene)
            {
                // A group row opens the Create menu (decision D255 of the M3 design).
                window.SceneEdits.CreateMenu(null);
            }

            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // The text starts where ImGui puts the label of a tree node: after the arrow.
        var textStart = start.X + ImGui.GetFontSize() + 2 * style.FramePadding.X;
        var section = IsScene ? window.SceneEdits.SectionOf(node.Item) : null;
        if (section != null && section.Equals(window.SceneEdits.Renaming))
        {
            RenameField(start, textStart, right);
            return;
        }

        // The robot glyph of an agent edit takes the right end of the row, so the text ends before it.
        var agentMarked = section != null && window.AgentMarks.Marked(section);
        var textRight = agentMarked ? right - AgentMarkWidth() : right;
        ListRow.Text(start, textStart, textRight, node.Kind.Icon, node.Kind.Abgr, node.Name, node.Detail, node.CountText);
        ProblemMark(node.Problem, start, right);
        if (section != null)
        {
            Mark(HierarchyModel.MarkOf(window.Session?.Edits.Get(section)), start, textStart);
        }

        if (agentMarked)
        {
            AgentMark(start, right);
        }
    }

    /// <summary>The tooltip of the robot glyph of a row.</summary>
    internal const string AgentMarkTip =
        "An agent edit changed this item after the last save. An undo of the agent edit or a save removes the mark.";

    /// <summary>The width of the robot glyph and the gap before it, in pixels.</summary>
    private static float AgentMarkWidth() =>
        ImGui.CalcTextSize(EditorIcons.Robot.ToString()).X + ImGui.GetStyle().FramePadding.X + ImGui.GetStyle().ItemSpacing.X;

    /// <summary>
    /// The mark of a row that an agent edit changed after the last save (section 8.5 of the MCP1 design). It is the robot
    /// glyph in the agent color at the right end of the row. The blue bar of the unsaved edit stays, so the row shows
    /// both marks.
    /// </summary>
    private static void AgentMark(Vector2 start, float right)
    {
        var text = EditorIcons.Robot.ToString();
        var size = ImGui.CalcTextSize(text);
        var height = ImGui.GetFrameHeight();
        var min = new Vector2(right - size.X - ImGui.GetStyle().FramePadding.X, start.Y + (height - size.Y) / 2);
        ImGui.GetWindowDrawList().AddText(min, ImGui.GetColorU32(EditorTheme.Agent), text);
        if (ImGui.IsMouseHoveringRect(min, min + size))
        {
            ImGui.SetTooltip(AgentMarkTip);
        }
    }

    /// <summary>
    /// The mark of a row of a mission with a problem (section 9 of the M6a design). It is the error or warning glyph at
    /// the right end of the row. A base-game problem has the muted lock glyph of a base-game problem while the
    /// base-game messages show.
    /// </summary>
    private static void ProblemMark(RowProblem problem, Vector2 start, float right)
    {
        if (problem == RowProblem.None)
        {
            return;
        }

        var (glyph, color, tip) = problem switch
        {
            RowProblem.Error => (EditorIcons.TimesCircle, EditorTheme.Error, "This item has an error. Its page lists it."),
            RowProblem.Warning => (EditorIcons.ExclamationTriangle, EditorTheme.Warning,
                "This item has a warning. Its page lists it."),
            _ => (EditorIcons.Lock, EditorTheme.TextDim, "This item has a base-game message. Its page lists it."),
        };
        var text = glyph.ToString();
        var size = ImGui.CalcTextSize(text);
        var height = ImGui.GetFrameHeight();
        var min = new Vector2(right - size.X - ImGui.GetStyle().FramePadding.X, start.Y + (height - size.Y) / 2);
        ImGui.GetWindowDrawList().AddText(min, ImGui.GetColorU32(color), text);
        if (ImGui.IsMouseHoveringRect(min, min + size))
        {
            ImGui.SetTooltip(tip);
        }
    }

    /// <summary>
    /// The drag and drop of a row of a system (section 8.1 of the M23 design): a row of an item drags onto the prop
    /// groups of the Assets panel, as a GameObject drags into the Unity Project window. Each row takes a tile of the
    /// Assets panel: an object row makes a child or gets a loadout or a faction, and another row spawns at the pivot.
    /// The drag starts only after the drag threshold, so the click and the double-click of the row keep their actions.
    /// </summary>
    private void DragAndDrop(HierarchyNode node)
    {
        if (node.Item is { } item && window.SceneEdits.SectionOf(item) is { } section &&
            ImGui.BeginDragDropSource())
        {
            window.AssetDrag.SourceOf(section, node.Name, node.Kind);
            ImGui.EndDragDropSource();
        }

        window.AssetDrag.Hierarchy(node.Item);
    }

    /// <summary>
    /// The mark of a row whose section the active mod changes or adds, as Unity marks the overrides of a prefab
    /// (decision D245 of the M3 design): a blue bar at the left of the row, and a "+" on the glyph of an added item.
    /// </summary>
    private static void Mark(RowMark mark, Vector2 start, float textStart)
    {
        if (mark == RowMark.None)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var height = ImGui.GetFrameHeight();
        var scale = ImGuiHelper.Scale;
        var left = ImGui.GetWindowPos().X + MathF.Max(1, scale);
        drawList.AddRectFilled(new Vector2(left, start.Y + 1), new Vector2(left + MathF.Max(2, 2 * scale), start.Y + height - 1),
            ImGui.GetColorU32(EditorTheme.Accent));
        if (mark == RowMark.Added)
        {
            var font = ImGui.GetFont();
            var size = ImGui.GetFontSize() * 0.75f;
            drawList.AddText(font, size, new Vector2(textStart - size * 0.35f, start.Y), ImGui.GetColorU32(EditorTheme.Accent), "+");
        }

        if (ImGui.IsMouseHoveringRect(new Vector2(left, start.Y), new Vector2(textStart, start.Y + height)))
        {
            ImGui.SetTooltip(mark == RowMark.Added ? "The active mod adds this item." : "The active mod changes this item.");
        }
    }

    /// <summary>
    /// The rename field of a row (decision D242 of the M3 design), as Unity renames in the Hierarchy. The field checks
    /// the text while you type and shows the problem below the row. Enter runs the rename, and a click outside does too.
    /// Esc ends the field without a rename.
    /// </summary>
    private void RenameField(Vector2 start, float left, float right)
    {
        var edits = window.SceneEdits;
        var after = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(left, start.Y));
        ImGui.SetNextItemWidth(Math.Max(40, right - left));
        if (edits.RenameStarted)
        {
            ImGui.SetKeyboardFocusHere();
            edits.RenameStarted = false;
        }

        var enter = ImGui.InputText("##rename", ref edits.RenameText, 128,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        var escape = ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        var deactivated = ImGui.IsItemDeactivated();
        var problem = edits.RenameProblem();
        if (problem != null)
        {
            // The problem shows below the field, over the next row.
            var drawList = ImGui.GetForegroundDrawList();
            var min = new Vector2(left, ImGui.GetItemRectMax().Y);
            var size = ImGui.CalcTextSize(problem);
            drawList.AddRectFilled(min, min + size + new Vector2(8, 4), ImGui.GetColorU32(EditorTheme.Strip));
            drawList.AddText(min + new Vector2(4, 2), ImGui.GetColorU32(EditorTheme.Error), problem);
        }

        if (escape)
        {
            edits.CancelRename();
        }
        else if ((enter || deactivated) && problem == null)
        {
            edits.CommitRename();
        }
        else if (enter)
        {
            // Enter with a problem keeps the field, so the user can correct the text.
            edits.RenameStarted = true;
        }
        else if (deactivated)
        {
            edits.CancelRename();
        }

        ImGui.SetCursorScreenPos(after);
    }

    /// <summary>
    /// Reads the keys while the panel has the focus: Up and Down move the selection, F and Enter frame it, and Ctrl+C
    /// copies its nickname.
    /// </summary>
    private void Keys(HierarchyModel tree)
    {
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
        {
            Move(tree, 1);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            Move(tree, -1);
        }

        if (window.Selection is not { } selection)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F, false) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
        {
            Frame(selection);
        }

        if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.C)) && CopyText(tree, selection) is { } copy)
        {
            ImGui.SetClipboardText(copy);
        }
    }

    /// <summary>Selects the next row with an item above or below the selected row.</summary>
    private void Move(HierarchyModel tree, int step)
    {
        var rows = tree.Rows;
        var current = tree.IndexOf(tree.RowItemOf(window.Selection));
        var start = current >= 0 ? current + step : step > 0 ? 0 : rows.Count - 1;
        for (var i = start; i >= 0 && i < rows.Count; i += step)
        {
            if (rows[i].Node.Item is { } item)
            {
                Select(item);
                scrollRequest = (item, false);
                return;
            }
        }
    }

    /// <summary>
    /// Selects an item from this panel. The panel does not scroll, because the row is where the user is.
    /// </summary>
    private void Select(object item)
    {
        window.Selection = item;
        lastSeenSelection = item;
    }

    /// <summary>
    /// Moves the Scene camera to an item, or frames the whole system for the system row. An entity of a cutscene shows
    /// in the Free mode of the Cutscene view.
    /// </summary>
    private void Frame(object item)
    {
        switch (item)
        {
            case StarSystem:
                window.Scene.FrameSystem();
                break;
            case ThnRef { Kind: ThnElementKind.Entity } entity:
                window.FrameInCutscene(entity);
                break;
            case CutsceneRoot or ThnRef:
                break;
            case MissionRef mission:
                // A double-click frames the item in the graph and in the Scene part (section 9 of the M6a design).
                window.Selection = mission;
                window.Missions.PendingGraphFrame = true;
                window.Missions.FrameInScene(mission);
                break;
            default:
                window.Scene.Focus(item);
                break;
        }
    }

    /// <summary>The text that Ctrl+C copies: the nickname of an item of a system, or the name of an entity or of the
    /// cutscene. A nebula or a field gives the nickname of its zone. Null for another item.</summary>
    private static string? CopyText(HierarchyModel tree, object item) => item switch
    {
        CutsceneRoot root => root.Path,
        ThnRef or MissionRef => tree.NodeOf(item)?.Name,
        _ => SceneItems.Nickname(item),
    };

    private void ContextMenu(object item)
    {
        if (item is ThnRef or CutsceneRoot)
        {
            CutsceneMenu(item);
            return;
        }

        if (item is MissionRef mission)
        {
            MissionMenu(mission);
            MissionEditMenu(mission);
            return;
        }

        // The actions of the item first, as in the Unity Hierarchy (decision D255 of the M3 design). Create on an object
        // row gives the new object the position and the parent of the row.
        if (window.SceneEdits.SectionOf(item) is { } section)
        {
            if (ImGui.BeginMenu($"{EditorIcons.Plus}   Create"))
            {
                window.SceneEdits.CreateMenu(item is SystemObject ? section : null);
                ImGui.EndMenu();
            }

            window.SceneEdits.ItemMenu(item);
            window.SceneEdits.SaveGroupMenuItem(item);
            ImGui.Separator();
        }

        if (Theme.IconMenuItem(EditorIcons.CompressArrowsAlt, "Frame in Scene", true))
        {
            Frame(item);
        }

        ImGui.Separator();
        if (SceneItems.Nickname(item) is { } nickname && Theme.IconMenuItem(EditorIcons.Copy, "Copy nickname", true))
        {
            ImGui.SetClipboardText(nickname);
        }

        var name = window.Session?.ItemName(item) ?? "";
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy name", name.Length > 0))
        {
            ImGui.SetClipboardText(name);
        }

        if (Position(item) is { } position &&
            Theme.IconMenuItem(EditorIcons.Copy, "Copy position as INI", true))
        {
            ImGui.SetClipboardText(string.Create(CultureInfo.InvariantCulture,
                $"pos = {position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}"));
        }

        if (item is not SystemObject { Dock: { } dock })
        {
            return;
        }

        if (dock.Kind == DockKinds.Jump)
        {
            ImGui.Separator();
            var target = dock.Target == null ? null : window.Session?.GameData?.Items.Systems.Get(dock.Target);
            if (Theme.IconMenuItem(EditorIcons.Map, "Open destination system", target != null))
            {
                window.OpenSystem(target!);
            }

            if (Theme.IconMenuItem(EditorIcons.MousePointer, "Open and select exit",
                    target != null && dock.Exit != null))
            {
                window.OpenSystem(target!);
                if (window.Scene.Index?.ObjectsByNickname.GetValueOrDefault(dock.Exit!) is { } exit)
                {
                    window.Selection = exit;
                    window.Scene.Focus(exit);
                }
            }
        }
        else if (dock.Kind == DockKinds.Tradelane && window.Scene.Index is { } index)
        {
            ImGui.Separator();
            var next = dock.Target == null ? null : index.ObjectsByNickname.GetValueOrDefault(dock.Target);
            var previous = dock.TargetLeft == null ? null : index.ObjectsByNickname.GetValueOrDefault(dock.TargetLeft);
            if (Theme.IconMenuItem(EditorIcons.ArrowRight, "Select next ring", next != null))
            {
                window.Selection = next;
            }

            if (Theme.IconMenuItem(EditorIcons.ArrowLeft, "Select previous ring", previous != null))
            {
                window.Selection = previous;
            }
        }
    }

    /// <summary>The menu of a row of a mission: frame it, copy its name. An item without a system also has "Show in
    /// system..." (section 9 of the M6a design).</summary>
    private void MissionMenu(MissionRef item)
    {
        if (Theme.IconMenuItem(EditorIcons.CompressArrowsAlt, "Frame in the graph and the Scene", true))
        {
            Frame(item);
        }

        var name = missionModel?.NodeOf(item)?.Name;
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy nickname", name != null))
        {
            ImGui.SetClipboardText(name!);
        }

        // The tree keeps the items of the group "System unknown", so the open menu does not look for them in each frame.
        if (item.Section is not { } section || missionModel?.SystemUnknown.Contains(section) != true)
        {
            return;
        }

        ImGui.Separator();
        if (!Theme.BeginIconMenu(EditorIcons.Map, "Show in system..."))
        {
            return;
        }

        ImGui.SetItemTooltip("Pick the system where the Scene shows this item. The choice lasts until you close the editor.");
        var current = window.Missions.SystemOverride(section);
        foreach (var system in window.Session?.GameData?.Items.Systems.OrderBy(s => s.Nickname,
                     StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<StarSystem>())
        {
            var label = window.Session!.SystemName(system.Nickname) is { Length: > 0 } systemName
                ? $"{systemName} ({system.Nickname})"
                : system.Nickname;
            if (ImGui.MenuItem(label, "", string.Equals(current, system.Nickname, StringComparison.OrdinalIgnoreCase)))
            {
                window.Missions.ShowInSystem(section, system.Nickname);
            }
        }

        ImGui.EndMenu();
    }

    /// <summary>The menu of the cutscene row and of an entity row.</summary>
    private void CutsceneMenu(object item)
    {
        if (item is ThnRef entity && Theme.IconMenuItem(EditorIcons.CompressArrowsAlt, "Frame in Free view", true))
        {
            Frame(entity);
        }

        var text = item is CutsceneRoot root ? root.Path : cutsceneModel?.NodeOf(item)?.Name;
        if (Theme.IconMenuItem(EditorIcons.Copy, item is CutsceneRoot ? "Copy path" : "Copy name", text != null))
        {
            ImGui.SetClipboardText(text!);
        }
    }

    /// <summary>The position of an item in its system, or null for an item without a position. A nebula or a field
    /// has the position of its zone.</summary>
    private static Vector3? Position(object item) => item switch
    {
        SystemObject obj => obj.Position,
        LightSource light => light.Light.Position,
        _ => SceneItems.ZoneOf(item)?.Position,
    };
}
