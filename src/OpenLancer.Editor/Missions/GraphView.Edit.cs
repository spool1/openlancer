using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.ImUI;
using LibreLancer.ImUI.NodeEditor;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The edit gestures of the mission graph (section 8.2 of the M6b design): the "+" pin, the link drags, the link menu,
/// the background menu, the edit items of the node menu, the keys, the rename in the header, the row drags and the drops
/// of library items. The view only draws and reads the gestures. <see cref="GraphEditModel"/> makes each command, and the
/// mission editor of the session runs it.
/// </summary>
internal sealed partial class GraphView
{
    private const string AddPinTip = "Drag to a trigger to start it or to stop it.";

    private GraphEditModel? edit;
    private bool editable;
    private readonly Dictionary<int, GraphLink> linksById = [];
    private readonly List<GraphBlock> selectedBlocks = [];
    private readonly List<GraphLink> selectedLinks = [];
    private readonly QuickAdd quickAdd = new();

    // The trigger node that the user renames in its header: its key, its text, and true in the frame that focuses it.
    private (string Node, string Text, bool Focus)? renaming;

    // True when the rename field drew in this frame. A rename whose node does not draw ends.
    private bool renameDrawn;

    // The two ends of a drag to a Watches pin, until the user picks the state.
    private (GraphEnd A, GraphEnd B)? watchDrag;
    private bool openWatch;
    private GraphLink? menuLink;
    private double insertDelay = MissionLibrary.DefaultDelay;
    private string commFilter = "";

    /// <summary>The library strip of the Mission tab. Quick add searches its items.</summary>
    public LibraryStrip? Library { get; set; }

    /// <summary>True while the user renames a node, or quick add is open. The keys of the graph then wait.</summary>
    public bool IsEditingText => renaming != null || quickAdd.IsOpen;

    /// <summary>True while the user renames a node, quick add is open, or the user drags a node or a link. An agent call
    /// waits for it (section 5.5, item 3 of the MCP1 design). A graph that does not draw any more has no gesture, for
    /// example a rename in a hidden tab.</summary>
    public bool InGesture => gesture.Open(IsEditingText);

    /// <summary>Follows the press of the left button on a node or a pin. Call it after the canvas, while the node editor
    /// is current.</summary>
    private void TrackPress()
    {
        var over = (IntPtr)NodeEditor.GetHoveredNode() != IntPtr.Zero || (IntPtr)NodeEditor.GetHoveredPin() != IntPtr.Zero;
        gesture.Press(ImGui.IsMouseClicked(ImGuiMouseButton.Left), over, ImGui.IsMouseDown(ImGuiMouseButton.Left));
    }

    /// <summary>The gesture model of the graph of this frame, or null before the first draw.</summary>
    internal GraphEditModel? EditModel => edit;

    private IReadOnlyList<LibrarySection> LibrarySections =>
        Library?.Sections ?? MissionLibrary.Sections(null);

    /// <summary>The trigger that the editor selection names in the open script: a trigger, or the trigger of a
    /// line.</summary>
    private MissionTrigger? SelectedTrigger(string script) =>
        window.Selection is MissionRef { Kind: MissionItemKind.Trigger or MissionItemKind.Row, Section: { } section } item &&
        string.Equals(item.Script, script, StringComparison.OrdinalIgnoreCase) && Graph != null
            ? Graph.Index.Triggers.FirstOrDefault(t => t.Section.Ref.Equals(section))
            : null;

    /// <summary>The model of the gestures and the links by id, after a build of the graph.</summary>
    private void BuildEdit()
    {
        edit = new GraphEditModel(Graph!);
        linksById.Clear();
        foreach (var link in Graph!.Links)
        {
            linksById[link.Id] = link;
        }

        selectedLinks.Clear();
        selectedBlocks.Clear();
    }

    /// <summary>Runs the batch of a gesture through the mission editor. Returns true when the batch changed W.</summary>
    private bool Run(MissionBatch? batch) => missions.Run(batch);

    // ---- In the canvas ----

    /// <summary>The "+" pin in the header of a trigger node, left of the info icon (section 8.2 of the M6b
    /// design).</summary>
    private void AddPin(GraphNode node, Vector2 position)
    {
        if (edit == null)
        {
            return;
        }

        ImGui.SetCursorScreenPos(position);
        NodeEditor.BeginPin(edit.AddPinId(node), PinKind.Output);
        NodeEditor.PinPivotAlignment(new Vector2(1, 0.5f));
        ImGui.Dummy(new Vector2(ChipHeight));
        NodeEditor.EndPin();
        var hovered = ImGui.IsMouseHoveringRect(position, position + new Vector2(ChipHeight));
        Text(ImGui.GetWindowDrawList(), ImGuiHelper.Roboto, GlyphSize, position + new Vector2(2, 1),
            hovered ? EditorTheme.AccentText : EditorTheme.TextDim, EditorIcons.Plus.ToString());
        if (hovered)
        {
            pendingTooltip = AddPinTip;
        }
    }

    /// <summary>The text field of a rename in place in the header (the key F2). Enter renames, and Esc or a click
    /// outside cancels. Returns true when the field draws in place of the title.</summary>
    private bool RenameField(GraphNode node, Vector2 position, float width)
    {
        if (renaming is not var (key, text, focus) || key != node.Key || node.Trigger is not { } trigger)
        {
            return false;
        }

        renameDrawn = true;
        ImGui.SetCursorScreenPos(position);
        ImGui.SetNextItemWidth(Math.Max(60, width));
        if (focus)
        {
            ImGui.SetKeyboardFocusHere();
        }

        var enter = ImGui.InputText("##rename", ref text, 128,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        renaming = (key, text, false);
        if (enter)
        {
            renaming = null;
            Run(GraphEditModel.Rename(trigger, text));
        }
        else if (!focus && (ImGui.IsKeyPressed(ImGuiKey.Escape) || !ImGui.IsItemActive()))
        {
            renaming = null;
        }

        return true;
    }

    /// <summary>Ends a rename whose node did not draw in this frame, for example a node that left the view. Call it after
    /// the nodes draw.</summary>
    private void EndLostRename()
    {
        if (renaming != null && !renameDrawn)
        {
            renaming = null;
        }

        renameDrawn = false;
    }

    /// <summary>A row drag of an action at the lines level. The drop goes to <see cref="DropTarget"/>.</summary>
    private void RowDragSource(GraphRow row)
    {
        if (!editable || row.Kind != GraphRowKind.Action ||
            !ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoPreviewTooltip))
        {
            return;
        }

        MissionDrag.Source(MissionDrag.RowType, row.Line, row.Text);
        ImGui.EndDragDropSource();
    }

    /// <summary>
    /// The link drags of the node editor (section 8.2 of the M6b design). A valid target gets the accept mark in grey. A
    /// target that is not valid gets the reject mark in the error color, and the tooltip gives the reason. The data
    /// changes only when the drag ends. A drag from an output pin to an empty place opens quick add.
    /// </summary>
    private void CreateLinks()
    {
        if (edit == null)
        {
            return;
        }

        if (NodeEditor.BeginCreate(new Color4(EditorTheme.GraphLinkLight), 1.5f))
        {
            gesture.DraggedLink();
            if (NodeEditor.QueryNewLink(out var startId, out var endId) &&
                edit.End((int)(IntPtr)startId) is { } a && edit.End((int)(IntPtr)endId) is { } b)
            {
                var gesture = edit.Link(a, b);
                if (gesture.Valid)
                {
                    if (NodeEditor.AcceptNewItem(new Color4(EditorTheme.TextDim), 2))
                    {
                        if (gesture.AsksState)
                        {
                            watchDrag = (a, b);
                            openWatch = true;
                        }
                        else
                        {
                            Run(gesture.Batch);
                        }
                    }
                }
                else
                {
                    pendingTooltip = gesture.Reason;
                    NodeEditor.RejectNewItem(new Color4(EditorTheme.Error), 2);
                }
            }

            if (NodeEditor.QueryNewNode(out var pinId) && edit.End((int)(IntPtr)pinId) is { } from)
            {
                if (edit.NewNodeRefusal(from) is { } reason)
                {
                    pendingTooltip = reason;
                    NodeEditor.RejectNewItem(new Color4(EditorTheme.Error), 2);
                }
                else if (NodeEditor.AcceptNewItem(new Color4(EditorTheme.TextDim), 2))
                {
                    quickAddAt = (Placed(new QuickAddContext(from, null, QuickAddFilter.Triggers), ImGui.GetMousePos()),
                        ImGui.GetMousePos());
                }
            }
        }

        NodeEditor.EndCreate();
    }

    // A quick add that opens after the canvas, with its place and its screen point.
    private (QuickAddContext Context, Vector2 At)? quickAddAt;

    /// <summary>The selected nodes and links of the node editor, for the keys. Call it while the editor is
    /// current.</summary>
    private void CaptureSelection()
    {
        selectedBlocks.Clear();
        selectedLinks.Clear();
        var count = NodeEditor.GetSelectedObjectCount();
        if (count <= 0)
        {
            return;
        }

        var nodes = new NodeId[count];
        var n = NodeEditor.GetSelectedNodes(nodes);
        for (var i = 0; i < n; i++)
        {
            if (blocks.TryGetValue((int)(IntPtr)nodes[i], out var block))
            {
                selectedBlocks.Add(block);
            }
        }

        var links = new LinkId[count];
        var l = NodeEditor.GetSelectedLinks(links);
        for (var i = 0; i < l; i++)
        {
            if (linksById.TryGetValue((int)(IntPtr)links[i], out var link))
            {
                selectedLinks.Add(link);
            }
        }
    }

    // ---- Menus, in screen space ----

    /// <summary>The link menu, the background menu and the watch menu (section 8.11 of the M6b design).</summary>
    private void EditMenus(string script)
    {
        if (!editable || edit == null)
        {
            return;
        }

        if (NodeEditor.ShowLinkContextMenu(out var linkId) && linksById.TryGetValue((int)(IntPtr)linkId, out var link))
        {
            menuLink = link;
            insertDelay = MissionLibrary.DefaultDelay;
            ImGui.OpenPopup("##link-menu");
        }

        if (NodeEditor.ShowBackgroundContextMenu())
        {
            ImGui.OpenPopup("##background-menu");
        }

        if (openWatch)
        {
            ImGui.OpenPopup("##watch-menu");
            openWatch = false;
        }

        LinkMenu();
        BackgroundMenu(script);
        WatchMenu();
    }

    private void LinkMenu()
    {
        if (menuLink is not { } link || edit == null || !ImGui.BeginPopup("##link-menu"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        if (MenuItems.BeginMenu("Insert trigger", GraphEditModel.InsertRefusal(link)))
        {
            var place = InsertPlace(link);
            if (ImGui.BeginMenu("After a delay"))
            {
                ImGui.SetNextItemWidth(100 * ImGuiHelper.Scale);
                ImGui.InputDouble("Seconds", ref insertDelay, 1, 5, "%.1f");
                insertDelay = Math.Max(0, insertDelay);
                if (ImGui.Button("Insert"))
                {
                    Run(edit.InsertOnLink(link, GraphEditModel.InsertCondition(InsertEvent.Delay, insertDelay), place));
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("At once"))
            {
                Run(edit.InsertOnLink(link, GraphEditModel.InsertCondition(InsertEvent.AtOnce), place));
            }

            if (ImGui.BeginMenu("After a comm line..."))
            {
                // The picker of the comm lines: the new trigger waits until the picked comm line ends.
                if (CommLinePicker() is { } line)
                {
                    Run(edit.InsertOnLink(link, GraphEditModel.InsertCondition(InsertEvent.CommLine, commLine: line), place));
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndMenu();
            }

            ImGui.EndMenu();
        }

        if (MenuItems.Item("Remove link", GraphEditModel.UnlinkRefusal(link), shortcut: "Delete"))
        {
            Run(edit.Unlink(link));
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The search field and the list of the comm lines of the game data in a menu. Returns the comm line of a
    /// click, or null.</summary>
    private string? CommLinePicker()
    {
        if (ImGui.IsWindowAppearing())
        {
            commFilter = "";
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(240 * ImGuiHelper.Scale);
        ImGui.InputTextWithHint("##comm-filter", "Search the comm lines", ref commFilter, 128);
        var list = MissionSchema.ListOf(MissionValueKind.CommLine) ?? "comm_line";
        var entries = window.Session?.Edits.Lists.List(list)?.Entries ?? [];
        var shown = 0;
        string? picked = null;
        foreach (var entry in entries.Where(e => commFilter.Length == 0 ||
                                                 e.Value.Contains(commFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (++shown > 40)
            {
                ImGui.TextDisabled("Type more of the name to see the other comm lines.");
                break;
            }

            if (ImGui.Selectable($"{entry.Value}##comm{shown}"))
            {
                picked = entry.Value;
            }
        }

        if (shown == 0)
        {
            ImGui.TextDisabled("No comm line matches the search.");
        }

        return picked;
    }

    /// <summary>The place of a new trigger on a link: between the two nodes of the link (section 8.10 of the M6b
    /// design), or null before the first layout.</summary>
    private GraphPoint? InsertPlace(GraphLink link)
    {
        if (Layout == null)
        {
            return null;
        }

        var (fromMin, fromMax) = Layout.RectOf(link.From);
        var (toMin, _) = Layout.RectOf(link.To);
        return new GraphPoint(Math.Round((fromMax.X + toMin.X - MissionGraph.SummaryWidth) / 2),
            Math.Round(Math.Max(fromMax.Y, toMin.Y) + GraphLayout.BlockGap));
    }

    private void BackgroundMenu(string script)
    {
        if (edit == null || !ImGui.BeginPopup("##background-menu"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        if (ImGui.MenuItem("Add...", "Space"))
        {
            quickAddAt = (Placed(new QuickAddContext(null, SelectedTrigger(script), QuickAddFilter.All), ImGui.GetMousePos()),
                ImGui.GetMousePos());
        }

        ImGui.SetItemTooltip("Add a trigger, a prefab, an action or a condition. Shortcut: Space.");
        if (ImGui.MenuItem("Paste", "Ctrl+V"))
        {
            Paste();
        }

        // A template in prefab mode has no acts (UI rule 4).
        if (edit.HasActs)
        {
            var newAct = edit.NewAct(selectedBlocks);
            if (MenuItems.Item("New act from selection", newAct == null ? "Select one or more triggers first." : null,
                    "Put the selected triggers into a new act."))
            {
                Run(newAct);
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The watch menu after a drag from a State pin to a Watches pin.</summary>
    private void WatchMenu()
    {
        if (!ImGui.BeginPopup("##watch-menu"))
        {
            // A click outside the menu closes it without a change.
            watchDrag = null;
            return;
        }

        EditorTheme.PushMenuColors();
        ImGui.TextDisabled("Start the watcher when this trigger:");
        ImGui.Separator();
        foreach (var (word, label, tip) in GraphEditModel.WatchStates)
        {
            if (ImGui.MenuItem(label) && watchDrag is var (a, b) && edit != null)
            {
                Run(edit.Watch(a, b, word));
                watchDrag = null;
            }

            ImGui.SetItemTooltip(tip);
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The edit items of the node menu (section 8.11 of the M6b design).</summary>
    private void EditNodeMenu(GraphNode node, MissionTrigger trigger)
    {
        if (!editable || edit == null)
        {
            return;
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Starts with the mission", "", trigger.StartsActive))
        {
            Run(edit.SetStart(trigger, !trigger.StartsActive));
        }

        if (ImGui.MenuItem("Repeats", "", trigger.Repeats))
        {
            Run(edit.SetRepeats(trigger, !trigger.Repeats));
        }

        if (ImGui.MenuItem("Add action..."))
        {
            quickAddAt = (new QuickAddContext(null, trigger, QuickAddFilter.Actions), ImGui.GetMousePos());
        }

        if (ImGui.MenuItem("Set condition..."))
        {
            quickAddAt = (new QuickAddContext(null, trigger, QuickAddFilter.Conditions), ImGui.GetMousePos());
        }

        if (ImGui.MenuItem("Rename", "F2"))
        {
            renaming = (node.Key, trigger.Nickname, true);
        }

        if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
        {
            Run(DuplicateSelection(selectedBlocks.Contains(node) ? selectedBlocks : [node]));
        }

        if (ImGui.MenuItem("Delete", "Delete"))
        {
            Run(edit.Delete(selectedBlocks.Contains(node) ? selectedBlocks : [node]));
        }

        if (ImGui.MenuItem("Copy", "Ctrl+C"))
        {
            Copy(selectedBlocks.Contains(node) ? selectedBlocks : [node]);
        }

        if (ImGui.MenuItem("Paste", "Ctrl+V"))
        {
            Paste();
        }
    }

    /// <summary>
    /// The items of the frame menu of a prefab instance (section 8.11 of the M6b design): "Open prefab", "Select prefab
    /// asset", "Unpack", "Save as prefab...", "Duplicate" and "Delete instance". <see cref="GraphEditModel"/> makes the
    /// items, and the links go through the workspace.
    /// </summary>
    private void PrefabFrameMenu(GraphFrame frame)
    {
        if (frame.Instance is not { } instance || edit == null || window.Session?.Edits.Missions is not { } editor)
        {
            return;
        }

        var actions = edit.FrameActions(instance, editor.FreePrefabId, editable, edit.DuplicateInstance(frame, BesidePlace(frame.Node)));
        if (actions.Count == 0)
        {
            return;
        }

        ImGui.Separator();
        foreach (var action in actions)
        {
            if (ImGui.MenuItem(action.Label))
            {
                missions.Do(action);
            }

            if (action.Tooltip != null)
            {
                ImGui.SetItemTooltip(action.Tooltip);
            }
        }
    }

    // The radius of the dot of the overrides, and the width that the title leaves for it, in canvas points.
    private const float OverridesDotRadius = 3.5f;
    private const float OverridesDotWidth = 2 * OverridesDotRadius + 6;

    /// <summary>The violet dot after the title of a collapsed linked instance with overrides (section 8.6 of the M6b
    /// design), with the tooltip "This instance has {n} overrides." The title leaves <see cref="OverridesDotWidth"/> for
    /// it.</summary>
    private void OverridesDot(int overrides, ImDrawListPtr drawList, ref float x, Vector2 origin)
    {
        if (overrides <= 0)
        {
            return;
        }

        var center = new Vector2(x + OverridesDotRadius, origin.Y + MissionGraph.HeaderHeight / 2);
        drawList.AddCircleFilled(center, OverridesDotRadius, EditorTheme.ToAbgr(EditorTheme.Override));
        if (ImGui.IsMouseHoveringRect(center - new Vector2(OverridesDotRadius + 3), center + new Vector2(OverridesDotRadius + 3)))
        {
            pendingTooltip = MissionPageModel.OverridesDotTip(overrides);
        }

        x += OverridesDotWidth;
    }

    /// <summary>The batch of Ctrl+D: a copy of each selected trigger and of each selected prefab instance, in one undo
    /// entry.</summary>
    private MissionBatch? DuplicateSelection(IReadOnlyList<GraphBlock> nodes)
    {
        if (edit == null)
        {
            return null;
        }

        var batches = new List<MissionBatch>();
        if (edit.Duplicate(Copies(nodes)) is { } triggers)
        {
            batches.Add(triggers);
        }

        foreach (var frame in nodes.Select(b => b as GraphFrame ?? (b as GraphNode)?.Frame).OfType<GraphFrame>()
                     .Where(f => f.Kind == GraphFrameKind.Prefab).Distinct())
        {
            if (edit.DuplicateInstance(frame, BesidePlace(frame.Node)) is { } copy)
            {
                batches.Add(copy);
            }
        }

        return batches.Count switch
        {
            0 => null,
            1 => batches[0],
            _ => new MissionBatch("Duplicate", batches.SelectMany(b => b.Commands).ToList()),
        };
    }

    // ---- After the canvas ----

    /// <summary>The drop target of library items and row drags over the graph, and quick add. Call it after the
    /// canvas, in the window of the graph.</summary>
    private void EditOverlays()
    {
        if (!editable || edit == null)
        {
            quickAddAt = null;
            return;
        }

        DropTarget();
        if (quickAddAt is var (context, at))
        {
            quickAdd.Open(context, at);
            quickAddAt = null;
        }

        if (quickAdd.Draw(LibrarySections) is var (place, item))
        {
            Run(edit.QuickAdd(place, item));
        }
    }

    /// <summary>
    /// The graph as a drop target while a library item or an action row is dragged (section 8.2 of the M6b design). An
    /// action goes to the trigger under the mouse, and at the lines level before the row under the mouse. A condition
    /// becomes the condition of that trigger. An event or a condition on an empty place makes a new trigger. A row moves
    /// to the trigger under the mouse, or with Ctrl gets a copy there.
    /// </summary>
    private void DropTarget()
    {
        var item = MissionDrag.Payload(MissionDrag.ItemType) as LibraryItem;
        var row = MissionDrag.Payload(MissionDrag.RowType) as MissionLine;
        if (item == null && row == null || edit == null)
        {
            return;
        }

        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(Screen.Min);
        ImGui.InvisibleButton("##graph-drop", Vector2.Max(Screen.Max - Screen.Min, Vector2.One));
        ImGui.SetCursorScreenPos(cursor);
        if (!ImGui.BeginDragDropTarget())
        {
            return;
        }

        var (node, before) = Under(ImGui.GetMousePos());
        MissionBatch? batch = null;
        string? refusal = null;
        var type = item != null ? MissionDrag.ItemType : MissionDrag.RowType;
        if (item?.Kind == LibraryKind.Prefab && node != null)
        {
            refusal = GraphEditModel.PrefabOnNode;
        }
        else if (node is { Kind: GraphNodeKind.Missing })
        {
            refusal = GraphEditModel.MissingNode;
        }
        else if (node is { Kind: not GraphNodeKind.Trigger })
        {
            refusal = GraphEditModel.OpenFrame;
        }
        else if (item != null)
        {
            var place = Placed(new QuickAddContext(null, null, QuickAddFilter.All), ImGui.GetMousePos());
            batch = node != null
                ? edit.DropOnNode(item, node, item.Kind == LibraryKind.Action ? before : null)
                : edit.DropOnEmpty(item, place.Place, place.Act);
            refusal = node == null ? GraphEditModel.EmptyDropRefusal(item) : null;
        }
        else if (node?.Trigger is { } target)
        {
            var copy = ImGui.GetIO().KeyCtrl;
            batch = edit.MoveRow(row!, target, before, copy);
            ImGui.SetTooltip(copy ? $"Copy to {target.Nickname}" : $"Move to {target.Nickname}. Hold Ctrl to copy.");
        }
        else
        {
            refusal = "Drop the line on a trigger.";
        }

        if (refusal != null)
        {
            ImGui.SetTooltip(refusal);
        }
        else if (ImGui.AcceptDragDropPayload(type) is { Delivery: true })
        {
            Run(batch);
        }

        ImGui.EndDragDropTarget();
    }

    /// <summary>The node under a screen point, and at the lines level the action row that a drop goes before, or null
    /// for the end of the trigger.</summary>
    private (GraphNode? Node, int? Before) Under(Vector2 mouse)
    {
        if (Graph == null || Layout == null || CanvasOf(mouse) is not { } canvas)
        {
            return (null, null);
        }

        // The last node of the list draws on top, so it takes the point first.
        foreach (var node in Graph.Nodes.Reverse())
        {
            var (min, max) = Layout.RectOf(node);
            if (canvas.X < min.X || canvas.X > max.X || canvas.Y < min.Y || canvas.Y > max.Y)
            {
                continue;
            }

            int? before = null;
            if (node.Trigger is { } trigger)
            {
                foreach (var graphRow in node.Rows.Where(r => r.Kind == GraphRowKind.Action))
                {
                    if (!rowRects.TryGetValue((node.Key, graphRow.Line.Entry), out var rect) || mouse.Y < rect.Min.Y ||
                        mouse.Y > rect.Max.Y)
                    {
                        continue;
                    }

                    var entry = graphRow.Line.Entry;
                    var next = trigger.Actions.FirstOrDefault(a => a.Entry > entry)?.Entry;
                    before = mouse.Y < (rect.Min.Y + rect.Max.Y) / 2 ? entry : next;
                    break;
                }
            }

            return (node, before);
        }

        return (null, null);
    }

    // ---- Keys and the toolbar ----

    /// <summary>
    /// The edit keys of the graph while it has the focus (section 8.2 of the M6b design): Delete, Ctrl+D, F2, Ctrl+C,
    /// Ctrl+V and Space. The node editor shortcuts stay off (decision D558), so the view handles the keys, as the Scene
    /// does.
    /// </summary>
    public void EditKeys(string script)
    {
        if (edit == null || IsEditingText)
        {
            return;
        }

        var io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C, false))
        {
            Copy(selectedBlocks.Count > 0 ? selectedBlocks : SelectedNode(script) is { } one ? [one] : []);
        }

        if (!editable)
        {
            return;
        }

        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.V, false))
        {
            Paste();
        }

        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
        {
            Run(DuplicateSelection(selectedBlocks.Count > 0 ? selectedBlocks
                : SelectedNode(script) is { } one ? [one] : []));
        }

        if (io.KeyCtrl || io.KeyAlt)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            if (selectedLinks.Count > 0 && selectedBlocks.Count == 0)
            {
                var commands = selectedLinks.Select(edit.Unlink).OfType<MissionBatch>().SelectMany(b => b.Commands).ToList();
                // A watch link is the condition of its watcher, so Delete does not remove it. The Console tells why.
                if (selectedLinks.Select(GraphEditModel.UnlinkRefusal).OfType<string>().FirstOrDefault() is { } reason)
                {
                    window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, reason);
                }

                Run(commands.Count == 0 ? null : new MissionBatch("Remove link", commands));
            }
            else
            {
                Run(edit.Delete(selectedBlocks.Count > 0 ? selectedBlocks : SelectedNode(script) is { } one ? [one] : []));
            }
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F2, false) && SelectedNode(script) is { Trigger: { } renamed } node)
        {
            renaming = (node.Key, renamed.Nickname, true);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
        {
            OpenQuickAdd(script, ImGui.GetMousePos());
        }
    }

    /// <summary>The trigger node of the editor selection, or null.</summary>
    private GraphNode? SelectedNode(string script) =>
        SelectedTrigger(script) is { } trigger ? Graph?.TriggerNode(trigger) : null;

    /// <summary>Opens quick add with all items at a screen point: the toolbar button "Add..." and Space.</summary>
    public void OpenQuickAdd(string script, Vector2 at)
    {
        if (editable)
        {
            quickAddAt = (Placed(new QuickAddContext(null, SelectedTrigger(script), QuickAddFilter.All), at), at);
        }
    }

    /// <summary>The canvas point of a screen point of the graph, or null outside the graph.</summary>
    private Vector2? CanvasOf(Vector2 screen)
    {
        var span = View.Max - View.Min;
        var size = Screen.Max - Screen.Min;
        if (span.X <= 0 || size.X <= 0 || size.Y <= 0 || screen.X < Screen.Min.X || screen.Y < Screen.Min.Y ||
            screen.X > Screen.Max.X || screen.Y > Screen.Max.Y)
        {
            return null;
        }

        return View.Min + (screen - Screen.Min) * span / size;
    }

    /// <summary>A quick add context with the place of a new node at a screen point, and the act frame there (section 8.2
    /// of the M6b design).</summary>
    private QuickAddContext Placed(QuickAddContext context, Vector2 screen)
    {
        if (CanvasOf(screen) is not { } canvas || edit == null)
        {
            return context;
        }

        return context with
        {
            Place = new GraphPoint(canvas.X, canvas.Y),
            Act = ActAt(canvas)?.Act is { } act ? edit.ActRefOf(act) : null,
        };
    }

    /// <summary>The triggers of nodes for Ctrl+D, each with the place of its copy beside its node.</summary>
    private IEnumerable<(MissionTrigger Trigger, GraphPoint? Place)> Copies(IEnumerable<GraphBlock> nodes) =>
        nodes.OfType<GraphNode>().Where(n => n.Trigger != null).Select(n => (n.Trigger!, BesidePlace(n)));

    /// <summary>The place of a copy beside its original node: below it, with the gap of the layout.</summary>
    private GraphPoint? BesidePlace(GraphNode node)
    {
        if (Layout == null)
        {
            return null;
        }

        var (min, max) = Layout.RectOf(node);
        return new GraphPoint(min.X, max.Y + GraphLayout.BlockGap);
    }

    /// <summary>A double-click on a library item (section 8.3 of the M6b design): an action or a condition goes to the
    /// selected trigger. An event or a condition without a selected trigger makes a new trigger.</summary>
    public void AddFromLibrary(string script, LibraryItem item)
    {
        if (!editable || edit == null)
        {
            return;
        }

        if (SelectedNode(script) is { } node && item.Kind is not (LibraryKind.Event or LibraryKind.Prefab))
        {
            Run(edit.DropOnNode(item, node));
        }
        else if (item.MakesTrigger || item.Kind == LibraryKind.Prefab)
        {
            // A double-click places the item at the center of the view.
            var center = (Screen.Min + Screen.Max) / 2;
            var place = Placed(new QuickAddContext(null, null, QuickAddFilter.All), center);
            Run(edit.DropOnEmpty(item, place.Place, place.Act));
        }
        else
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, "Select a trigger first. Then add the action.");
        }
    }

    private void Copy(IEnumerable<GraphBlock> nodes)
    {
        if (edit == null)
        {
            return;
        }

        var text = edit.Copy(nodes.OfType<GraphNode>().Select(n => n.Trigger).OfType<MissionTrigger>());
        if (text.Length > 0)
        {
            ImGui.SetClipboardText(text);
        }
    }

    private void Paste()
    {
        if (edit == null)
        {
            return;
        }

        var batch = edit.Paste(ImGui.GetClipboardText() ?? "", out var problem);
        if (problem != null)
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, problem);
        }

        Run(batch);
    }
}
