using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The Timeline tab (section 7.3 of the M8 design): the Preview controls, a ruler that scrubs and sets the loop range,
/// one lane of camera cuts for each monitor, and one track for each entity, grouped by type. An event with a length is
/// a bar, with the keys of its curve as ticks. An event without a length is a diamond.
/// </summary>
/// <remarks>
/// <para>The edits: a drag of a bar or a diamond moves the selected events, a drag of an edge of a bar changes its start
/// or its length, and a drag of the end handle of the ruler changes the duration. Each drag is one entry of the edit
/// history and ends on the release of the mouse (see <see cref="TimelineDrag"/>). Ctrl snaps to the playhead and to the
/// edges of the other events.</para>
/// <para>A click selects an event, Shift and a click add or remove one, and a box on empty track space selects the events
/// in it. Delete, Ctrl+C, Ctrl+V and Ctrl+D delete, copy, paste at the playhead and duplicate. The menu of a row adds an
/// event of a type that fits the entity, and the menu of an event moves it before another event of the same
/// time.</para>
/// </remarks>
internal sealed class TimelinePanel(EditorWindow window, CutsceneWorkspace workspace)
{
    /// <summary>The id of the panel. The layout reports its focus by this id.</summary>
    public const string PanelId = "timeline";

    // Sizes in unscaled points.
    private const float LabelWidth = 230;
    private const float LabelPadding = 6;
    private const float DiamondHalf = 5;
    private const float BarInset = 3;
    private const float KeyTick = 4;
    private const float MinTickGap = 70;
    private const float HitTolerance = 4;
    private const float ProgressHeight = 3;
    private const float EdgeGrab = 5;
    private const float SnapDistance = 8;
    private const float DragSlop = 3;

    /// <summary>The zoom of one wheel step with Ctrl.</summary>
    private const double WheelZoom = 1.25;

    private const string MenuId = "##timeline-menu";

    /// <summary>The steps of the ruler ticks, in seconds.</summary>
    private static readonly double[] TickSteps =
        [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300];

    private static readonly uint StripeColor = EditorTheme.ToAbgr(EditorTheme.Strip with { W = 0.35f });
    private static readonly uint HeaderColor = EditorTheme.ToAbgr(EditorTheme.Section);
    private static readonly uint SelectedRowColor = EditorTheme.ToAbgr(EditorTheme.AccentStrong with { W = 0.45f });
    private static readonly uint AccentColor = EditorTheme.ToAbgr(EditorTheme.Accent);
    private static readonly uint TextColor = EditorTheme.ToAbgr(EditorTheme.Text);
    private static readonly uint DimColor = EditorTheme.ToAbgr(EditorTheme.TextDim);
    private static readonly uint GreyFill = EditorTheme.ToAbgr(EditorTheme.TextDim with { W = 0.3f });
    private static readonly uint WarningFill = EditorTheme.ToAbgr(EditorTheme.Warning with { W = 0.55f });
    private static readonly uint WarningColor = EditorTheme.ToAbgr(EditorTheme.Warning);
    private static readonly uint LineColor = EditorTheme.ToAbgr(EditorTheme.Border);
    private static readonly uint KeyColor = EditorTheme.ToAbgr(EditorTheme.TextBright with { W = 0.8f });
    private static readonly uint PlayheadColor = EditorTheme.ToAbgr(EditorTheme.Error);
    private static readonly uint LoopFill = EditorTheme.ToAbgr(EditorTheme.Accent with { W = 0.22f });
    private static readonly uint LoopOffFill = EditorTheme.ToAbgr(EditorTheme.TextDim with { W = 0.15f });
    private static readonly uint BoxFill = EditorTheme.ToAbgr(EditorTheme.Accent with { W = 0.15f });

    private readonly Dictionary<string, TimelineView> views = new(VfsPath.Comparer);
    private readonly ScrubGesture scrub = new();
    private readonly LoopRangeGesture loopDrag = new();

    // The pan of a drag with the middle mouse button, in pixels since the last frame.
    private bool panning;

    // The open drag, a press on an event that becomes a drag when the mouse moves, and a box select.
    private TimelineDrag? drag;
    private Press? pending;
    private Box? box;

    // The lines of the last frame and where they are, for a box select.
    private Geometry? geometry;

    // The line, the event and the time of the last right click, for the menu.
    private MenuTarget? menu;
    private bool openMenu;

    /// <summary>True while a drag is open. Undo and redo wait for its end. A save ends it first.</summary>
    public bool IsDragging => drag != null;

    /// <summary>Forgets the views of the cutscenes. Call it when the profile closes.</summary>
    public void Reset()
    {
        EndDrag();
        views.Clear();
        (pending, box, geometry, menu) = (null, null, null, null);
    }

    /// <summary>A forced end of an open drag: it commits the change that the edit view holds, for example when the
    /// window closes.</summary>
    public void EndDrag()
    {
        drag?.End();
        drag = null;
    }

    /// <summary>The Preview controls, the event filter and the zoom.</summary>
    public void Toolbar()
    {
        PreviewControls.Draw(workspace.Transport, window.Settings);
        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        var view = workspace.Timeline is { } model ? View(model) : null;
        var filter = ToolbarItems.Toggle("filter", EditorIcons.Filter,
            "Only with events: hide the rows of entities without events, for example most markers.",
            view?.OnlyWithEvents ?? true, view != null);
        if (view != null)
        {
            view.OnlyWithEvents = filter;
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("fit", EditorIcons.ArrowsAltH,
                $"Show the whole cutscene, at most its first {TimelineView.MaxSpan:0} s. F shows the selection.",
                view != null))
        {
            view!.Fit(workspace.Timeline!.End, TrackWidth());
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        // A cutscene that takes no edits, for example without an active mod, takes no drag either.
        var readOnly = window.Session?.NoEditReason ??
                       (workspace.Path is { } path ? window.Session?.Edits.CutsceneReadOnlyReason(path) : null);
        ImGui.TextDisabled(readOnly == null
            ? "Drag to edit, Ctrl snaps. Ctrl+wheel zooms, Shift+wheel scrolls in time."
            : "Read-only. Ctrl+wheel zooms, Shift+wheel scrolls in time.");
        if (readOnly != null)
        {
            ImGui.SetItemTooltip(readOnly);
        }
    }

    /// <summary>Draws the ruler and the tracks, and reads the mouse and the keys over them.</summary>
    public void Draw()
    {
        if (window.Session?.GameData == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (workspace.Path == null)
        {
            PanelStates.Message("No cutscene is open.", "Open a cutscene in the Cutscenes tab.");
            return;
        }

        if (workspace.Timeline is not { } model)
        {
            PanelStates.Message($"{System.IO.Path.GetFileName(workspace.Path)} has no timeline.",
                workspace.Problem ?? "The editor cannot read the cutscene.");
            return;
        }

        var view = View(model);
        var scale = ImGuiHelper.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var labelWidth = MathF.Truncate(Math.Min(LabelWidth * scale, avail.X / 2));
        var left = origin.X + labelWidth;
        var width = Math.Max(1, avail.X - labelWidth);
        if (!view.IsFitted)
        {
            view.Fit(model.End, width);
        }

        view.Keep(model.End, width);
        var rowHeight = ImGui.GetFrameHeight();
        Ruler(model, view, origin, labelWidth, width, rowHeight);
        ImGui.SetCursorScreenPos(origin + new Vector2(0, rowHeight));
        Rows(model, view, left, width, labelWidth, rowHeight);
        Mouse(model, view, origin, left, width, avail.Y);
        Gestures(model, view, left);
        Keys(model, view, width);
        Menu(model);
    }

    /// <summary>The view of a cutscene. Each cutscene keeps its view while the profile is open.</summary>
    private TimelineView View(TimelineModel model)
    {
        if (!views.TryGetValue(model.Path, out var view))
        {
            view = new TimelineView();
            views[model.Path] = view;
        }

        return view;
    }

    /// <summary>The width of the tracks in the last layout, for the Fit button of the toolbar.</summary>
    private float TrackWidth() => Math.Max(1, ImGui.GetContentRegionAvail().X - LabelWidth * ImGuiHelper.Scale);

    /// <summary>
    /// The ruler: the playhead time at the left, the loop range, the ticks of the seconds, the end handle, the progress
    /// of a catch-up, and the playhead. A drag on it scrubs, a drag with Shift sets the loop range, and a drag of the
    /// end handle changes the duration.
    /// </summary>
    private void Ruler(TimelineModel model, TimelineView view, Vector2 origin, float labelWidth, float width,
        float height)
    {
        var drawList = ImGui.GetWindowDrawList();
        var left = origin.X + labelWidth;
        var max = new Vector2(left + width, origin.Y + height);
        drawList.AddRectFilled(origin, max, HeaderColor);
        var transport = workspace.Transport;
        var time = transport != null ? scrub.Shown(transport) : 0;
        var padding = LabelPadding * ImGuiHelper.Scale;
        var textY = origin.Y + (height - ImGui.GetTextLineHeight()) / 2;
        drawList.AddText(new Vector2(origin.X + padding, textY), TextColor,
            $"{Seconds(time)} s / {Seconds(model.Duration)} s");

        // The ruler takes the drag of the playhead, with Shift the drag of the loop range, and at the end handle the
        // drag of the duration.
        var endX = view.XOf(model.Duration, left);
        ImGui.SetCursorScreenPos(new Vector2(left, origin.Y));
        ImGui.InvisibleButton("##ruler", new Vector2(width, height));
        var mouse = ImGui.GetMousePos();
        var overEnd = Math.Abs(mouse.X - endX) <= EdgeGrab * ImGuiHelper.Scale;
        if (ImGui.IsItemHovered() && overEnd && drag == null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }

        if (ImGui.IsItemActivated() && overEnd && !ImGui.GetIO().KeyShift && drag == null)
        {
            BeginDurationDrag(model, view, left);
        }
        else if (transport != null && drag?.Kind != TimelineDragKind.Duration)
        {
            var mouseTime = Math.Clamp(view.TimeAt(mouse.X, left), 0, transport.End);
            if (ImGui.IsItemActivated())
            {
                loopDrag.Press(mouseTime, ImGui.GetIO().KeyShift);
            }

            if (loopDrag.Active)
            {
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    loopDrag.Drag(transport, mouseTime);
                }
                else if (ImGui.IsItemDeactivated())
                {
                    loopDrag.Release(transport);
                }
            }
            else if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                scrub.Drag(transport, mouseTime);
            }
            else if (ImGui.IsItemDeactivated())
            {
                scrub.Release(transport);
            }
        }

        ImGui.SetItemTooltip(workspace.Clock is { CatchingUp: true }
            ? workspace.UpdatingText
            : "Drag to move the playhead. A drag back builds the scene again when you release it.\n" +
              "Shift+drag sets the range of the loop and turns the loop on. Shift+click clears the range.\n" +
              "Drag the end handle to change the duration of the script.");

        drawList.PushClipRect(new Vector2(left, origin.Y), max, true);
        if (transport?.LoopRange is { } range)
        {
            // The loop range, under the ticks. It is dim while the loop is off.
            var (x0, x1) = (view.XOf(range.Start, left), view.XOf(range.End, left));
            drawList.AddRectFilled(new Vector2(x0, origin.Y), new Vector2(x1, max.Y),
                transport.Loop ? LoopFill : LoopOffFill);
            PixelLine.Vertical(drawList, x0, origin.Y, max.Y, transport.Loop ? AccentColor : DimColor);
            PixelLine.Vertical(drawList, x1, origin.Y, max.Y, transport.Loop ? AccentColor : DimColor);
        }

        var step = TickStep(view.PixelsPerSecond);
        // No ticks before time 0, in the margin of the view.
        var first = Math.Max(0, Math.Floor(view.Start / step) * step);
        var minor = step / 5;
        for (var t = first; t <= view.Start + width / view.PixelsPerSecond; t += minor)
        {
            var x = view.XOf(t, left);
            var major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            PixelLine.Vertical(drawList, x, max.Y - (major ? height / 2 : height / 5), max.Y, DimColor);
            if (major)
            {
                drawList.AddText(new Vector2(x + 2, origin.Y + 1), DimColor, Seconds(Math.Round(t / step) * step, step));
            }
        }

        // The end handle.
        PixelLine.Vertical(drawList, endX, origin.Y, max.Y, WarningColor);
        drawList.AddText(new Vector2(endX + 3, textY), WarningColor, $"end {Seconds(model.Duration)}");

        if (workspace.Clock is { CatchingUp: true } clock)
        {
            // The time of the player that catches up, and the wait.
            var progress = MathF.Max(1, MathF.Truncate(ProgressHeight * ImGuiHelper.Scale));
            drawList.AddRectFilled(new Vector2(view.XOf(0, left), max.Y - progress),
                new Vector2(view.XOf(clock.CatchUpTime, left), max.Y), AccentColor);
            var text = workspace.UpdatingText;
            drawList.AddText(new Vector2(max.X - ImGui.CalcTextSize(text).X - padding, textY), TextColor, text);
        }

        Playhead(drawList, view.XOf(time, left), origin.Y, max.Y);
        drawList.PopClipRect();
    }

    /// <summary>The group headers, the lanes and the rows, in a child that scrolls up and down.</summary>
    private void Rows(TimelineModel model, TimelineView view, float left, float width, float labelWidth,
        float rowHeight)
    {
        var lines = model.Lines(view.Collapsed, view.OnlyWithEvents);
        // No padding and no spacing, so that each line is exactly one row high and the clipper finds the rows.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.BeginChild("##rows", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (!visible)
        {
            ImGui.EndChild();
            return;
        }

        if (lines.Count == 0)
        {
            if (view.OnlyWithEvents)
            {
                PanelStates.Message("No entity of this cutscene has events.",
                    "The filter of the toolbar hides the entities without events. Turn it off to show all entities.");
            }
            else
            {
                PanelStates.Message("This cutscene has no entities and no events.", "There is nothing to show.");
            }

            ImGui.EndChild();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        // A scroll bar takes some of the width.
        width = Math.Max(1, ImGui.GetContentRegionAvail().X - labelWidth);

        // The wheel scrolls the rows, except with Ctrl or Shift, which zoom and scroll in time.
        var io = ImGui.GetIO();
        if (ImGui.IsWindowHovered() && io.MouseWheel != 0 && !io.KeyCtrl && !io.KeyShift)
        {
            ImGui.SetScrollY(ImGui.GetScrollY() - io.MouseWheel * rowHeight * 3);
        }

        var drawList = ImGui.GetWindowDrawList();
        var top = ImGui.GetCursorScreenPos().Y;
        var selection = new Selection(workspace.SelectedEvents.Select(e => e.Id).ToHashSet(),
            window.Selection as ThnRef is { Kind: ThnElementKind.Entity } entity ? entity : null);
        TimelineEvent? hovered = null;
        var hoveredY = 0f;
        // The stripe follows the row, not the line, so that the sub-rows of one row look like one row.
        var stripes = new bool[lines.Count];
        for (int i = 0, row = -1; i < lines.Count; i++)
        {
            if (lines[i].SubRow == 0)
            {
                row++;
            }

            stripes[i] = row % 2 == 1;
        }

        var clipper = new ImGuiListClipper();
        clipper.Begin(lines.Count, rowHeight);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ImGui.PushID(i);
                var hit = Line(model, view, lines[i], stripes[i], left, width, labelWidth, rowHeight, selection);
                if (hit != null)
                {
                    (hovered, hoveredY) = (hit, top + i * rowHeight + rowHeight / 2);
                }

                ImGui.PopID();
            }
        }

        clipper.End();
        ImGui.PopStyleVar();

        // The end of the script, the playhead, a snap and a box go over all rows.
        var visibleTop = ImGui.GetWindowPos().Y;
        var visibleBottom = visibleTop + ImGui.GetWindowHeight();
        geometry = new Geometry(lines, top, rowHeight, left, visibleTop, visibleBottom);
        drawList.PushClipRect(new Vector2(left, visibleTop), new Vector2(left + width, visibleBottom), true);
        PixelLine.Vertical(drawList, view.XOf(model.Duration, left), visibleTop, visibleBottom, WarningColor);
        if (workspace.Transport is { } transport)
        {
            Playhead(drawList, view.XOf(scrub.Shown(transport), left), visibleTop, visibleBottom);
        }

        if (drag?.SnappedTo is { } snapped)
        {
            PixelLine.Vertical(drawList, view.XOf(snapped, left), visibleTop, visibleBottom, AccentColor);
        }

        if (box is { } selecting)
        {
            var mouse = ImGui.GetMousePos();
            var min = Vector2.Min(selecting.Start, mouse);
            var max = Vector2.Max(selecting.Start, mouse);
            drawList.AddRectFilled(min, max, BoxFill);
            drawList.AddRect(min, max, AccentColor);
        }

        if (hovered?.SecondTarget is { } target && drag == null)
        {
            // A line from the hovered event to the row of its second target.
            var index = IndexOf(lines, target);
            var targetY = index < 0 ? (float?)null : top + index * rowHeight + rowHeight / 2;
            if (targetY is { } y)
            {
                var x = view.XOf(hovered.Time, left);
                drawList.AddLine(new Vector2(x, hoveredY), new Vector2(x, Math.Clamp(y, visibleTop, visibleBottom)),
                    AccentColor, 1.5f * ImGuiHelper.Scale);
                drawList.AddCircleFilled(new Vector2(x, Math.Clamp(y, visibleTop, visibleBottom)),
                    3 * ImGuiHelper.Scale, AccentColor);
            }
        }

        drawList.PopClipRect();
        ImGui.EndChild();
    }

    /// <summary>Draws one line and reads its clicks. Returns the event under the mouse, or null.</summary>
    /// <param name="stripe">True for the stripe color of every second row.</param>
    private TimelineEvent? Line(TimelineModel model, TimelineView view, TimelineLine line, bool stripe, float left,
        float width, float labelWidth, float rowHeight, Selection selection)
    {
        var scale = ImGuiHelper.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var rowMax = new Vector2(left + width, min.Y + rowHeight);
        var entity = line.Lane?.Monitor ?? line.Row?.Entity;
        if (line.IsHeader)
        {
            drawList.AddRectFilled(min, rowMax, HeaderColor);
        }
        else if (entity != null && entity.Equals(selection.Entity))
        {
            drawList.AddRectFilled(min, rowMax, SelectedRowColor);
        }
        else if (stripe)
        {
            drawList.AddRectFilled(min, rowMax, StripeColor);
        }

        // The label cell.
        ImGui.InvisibleButton("##label", new Vector2(labelWidth, rowHeight),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            if (line.IsHeader && !view.Collapsed.Remove(line.Group.Id))
            {
                view.Collapsed.Add(line.Group.Id);
            }
            else if (entity != null)
            {
                window.Selection = entity;
            }
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !line.IsHeader)
        {
            (menu, openMenu) = (new MenuTarget(line, null, workspace.Transport?.Time ?? 0), true);
        }

        if (LabelTooltip(line) is { } tooltip)
        {
            ImGui.SetItemTooltip(tooltip);
        }

        // The other sub-rows of a row have an empty label cell.
        if (line.SubRow == 0)
        {
            Label(drawList, line, view.Collapsed.Contains(line.Group.Id), min, labelWidth, rowHeight);
        }

        // The track cell.
        ImGui.SameLine(0, 0);
        ImGui.InvisibleButton("##track", new Vector2(width, rowHeight),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetMousePos();
        var mouseTime = view.TimeAt(mouse.X, left);
        var hit = hovered ? TimelineModel.HitTest(line, mouseTime, HitTolerance * scale / view.PixelsPerSecond) : null;
        var marks = TimelineModel.Diamonds(line, MarkWidth(view));
        if (ImGui.IsItemActivated() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !line.IsHeader)
        {
            PressTrack(line, hit, entity, view, mouseTime, mouse);
            if (hit != null && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && workspace.Transport is { } transport)
            {
                transport.Seek(hit.Time);
            }
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !line.IsHeader)
        {
            if (hit != null && !selection.Events.Contains(hit.Ref.Id))
            {
                window.Selection = hit.Ref;
            }

            (menu, openMenu) = (new MenuTarget(line, hit, Math.Max(0, mouseTime)), true);
        }

        if (hit != null && drag == null)
        {
            var kind = TimelineDrag.KindAt(hit, mouseTime, EdgeGrab * scale / view.PixelsPerSecond);
            if (kind != TimelineDragKind.Move)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            }

            // A mark of diamonds that overlap on the screen lists all of them.
            var mark = hit.IsBar ? null : marks.FirstOrDefault(m => m.Events.Contains(hit));
            var text = mark is { Events.Count: > 1 }
                ? $"{mark.Events.Count} events here:\n" + string.Join("\n\n", mark.Events.Select(TimelineModel.Describe)) +
                  "\n\nClick to select the last one, Shift+click to add it. Drag a box to select all of them."
                : TimelineModel.Describe(hit) + "\n\nClick to select it, Shift+click to add it.";
            ImGui.SetItemTooltip(text + " Drag to move it, or drag an edge to change its start or its length. " +
                                 "Double-click to move the playhead to it. Right-click for more.");
        }

        drawList.PushClipRect(new Vector2(left, min.Y), rowMax, true);
        if (line.Lane is { } lane)
        {
            Lane(drawList, view, lane, left, min.Y, rowHeight, selection, hit);
        }
        else if (line.Row is { } row)
        {
            foreach (var ev in line.Events.Where(e => e.IsBar))
            {
                Bar(drawList, view, row, ev, left, min.Y, rowHeight, selection.Events.Contains(ev.Ref.Id),
                    ReferenceEquals(ev, hit));
            }

            foreach (var mark in marks)
            {
                var last = mark.Events[^1];
                var outlined = mark.Events.Any(e => selection.Events.Contains(e.Ref.Id) || ReferenceEquals(e, hit));
                var x = view.XOf(mark.Time, left);
                Diamond(drawList, x, min.Y + rowHeight / 2, EventFill(row.Kind, last, 0.9f), outlined);
                if (mark.Events.Count > 1)
                {
                    drawList.AddText(new Vector2(x + (DiamondHalf + 2) * scale, min.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2),
                        TextColor, mark.Events.Count.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        drawList.PopClipRect();
        // The line under a row draws only under its last sub-row.
        if (line.Row == null || line.SubRow == line.Row.SubRows - 1)
        {
            PixelLine.Horizontal(drawList, rowMax.Y - 1, min.X, rowMax.X, LineColor);
        }

        return hit;
    }

    /// <summary>The width of a diamond in seconds at the zoom of a view: diamonds closer than this overlap on the
    /// screen.</summary>
    private static double MarkWidth(TimelineView view) => 2 * DiamondHalf * ImGuiHelper.Scale / view.PixelsPerSecond;

    /// <summary>
    /// A press of the left button on a track. On an event: Shift adds or removes it, and else it joins the selection
    /// and may become a drag when the mouse moves. On empty track space: the start of a box select.
    /// </summary>
    private void PressTrack(TimelineLine line, TimelineEvent? hit, ThnRef? entity, TimelineView view, double time,
        Vector2 mouse)
    {
        var shift = ImGui.GetIO().KeyShift;
        if (hit == null)
        {
            box = new Box(mouse, time, shift, entity);
            return;
        }

        if (shift)
        {
            window.Selection = ThnEventSet.Toggle(window.Selection, hit.Ref);
            return;
        }

        var selected = workspace.SelectedEvents.Any(e => e.Id == hit.Ref.Id);
        if (!selected)
        {
            window.Selection = hit.Ref;
        }

        pending = new Press(hit, TimelineDrag.KindAt(hit, time, EdgeGrab * ImGuiHelper.Scale / view.PixelsPerSecond),
            time, mouse, selected);
    }

    /// <summary>
    /// The gestures of the mouse after the rows drew: a press that becomes a drag, an open drag that follows the mouse
    /// and ends on the release, and a box select.
    /// </summary>
    private void Gestures(TimelineModel model, TimelineView view, float left)
    {
        var mouse = ImGui.GetMousePos();
        var down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var time = view.TimeAt(mouse.X, left);
        if (pending is { } press)
        {
            if (!down)
            {
                // A click on an event of a set selects only that event.
                if (press.WasSelected)
                {
                    window.Selection = press.Event.Ref;
                }

                pending = null;
            }
            else if (Vector2.Distance(mouse, press.Mouse) > DragSlop * ImGuiHelper.Scale)
            {
                pending = null;
                BeginDrag(model, press, view);
            }
        }

        if (drag != null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                drag.Cancel();
                drag = null;
            }
            else if (!down)
            {
                drag.End();
                drag = null;
            }
            else
            {
                Step(time, view);
            }
        }

        if (box is { } selecting && !down)
        {
            box = null;
            SelectBox(selecting, mouse, view, left);
        }
    }

    /// <summary>Opens a drag of the events of a press: the selected events for a move, or the event of the press for a
    /// change of its start or its length.</summary>
    private void BeginDrag(TimelineModel model, Press press, TimelineView view)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        var events = press.Kind == TimelineDragKind.Move
            ? workspace.SelectedEvents.Select(e => model.Event(e.Id)).OfType<TimelineEvent>()
                .OrderBy(e => e.Ref.Id == press.Event.Ref.Id ? 0 : 1).ToList()
            : [press.Event];
        if (events.Count == 0)
        {
            events = [press.Event];
        }

        try
        {
            if (session.NoEditReason is { } reason)
            {
                throw new InvalidOperationException(reason);
            }

            var snaps = TimelineDrag.SnapPoints(model, events.Select(e => e.Ref), workspace.Transport?.Time ?? 0);
            drag = TimelineDrag.Begin(session.Edits, events, press.Kind, press.Time, snaps);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            window.Log.Add(ConsoleSeverity.Warning, EditController.Source, $"The drag did not start: {ErrorText.Of(e)}");
        }
    }

    /// <summary>Opens a drag of the end handle of the ruler.</summary>
    private void BeginDurationDrag(TimelineModel model, TimelineView view, float left)
    {
        if (window.Session is not { } session || workspace.Path is not { } path)
        {
            return;
        }

        try
        {
            if (session.NoEditReason is { } reason)
            {
                throw new InvalidOperationException(reason);
            }

            var snaps = TimelineDrag.SnapPoints(model, [], workspace.Transport?.Time ?? 0);
            drag = TimelineDrag.BeginDuration(session.Edits, path, model.Duration,
                view.TimeAt(ImGui.GetMousePos().X, left), snaps);
        }
        catch (InvalidOperationException e)
        {
            window.Log.Add(ConsoleSeverity.Warning, EditController.Source, $"The drag did not start: {ErrorText.Of(e)}");
        }
    }

    /// <summary>One frame of an open drag. A change that does not apply stops the drag.</summary>
    private void Step(double time, TimelineView view)
    {
        try
        {
            drag!.Drag(time, ImGui.GetIO().KeyCtrl, SnapDistance * ImGuiHelper.Scale / view.PixelsPerSecond);
        }
        catch (InvalidOperationException e)
        {
            window.Log.Add(ConsoleSeverity.Error, EditController.Source, $"The drag stopped: {ErrorText.Of(e)}");
            drag!.Cancel();
            drag = null;
        }
    }

    /// <summary>The end of a box select: the events of the lines and the times in the box. A box without size selects
    /// the entity of the row of the press, or keeps the selection with Shift.</summary>
    private void SelectBox(Box selecting, Vector2 mouse, TimelineView view, float left)
    {
        if (Vector2.Distance(mouse, selecting.Start) <= DragSlop * ImGuiHelper.Scale)
        {
            if (!selecting.Add)
            {
                window.Selection = (object?)selecting.Entity ?? workspace.Root;
            }

            return;
        }

        if (geometry is not { } g || workspace.Path is not { } path)
        {
            return;
        }

        var (low, high) = (Math.Min(selecting.Start.Y, mouse.Y), Math.Max(selecting.Start.Y, mouse.Y));
        var tolerance = HitTolerance * ImGuiHelper.Scale / view.PixelsPerSecond;
        var picked = new List<ThnElementId>();
        for (var i = 0; i < g.Lines.Count; i++)
        {
            var y0 = g.Top + i * g.RowHeight;
            if (y0 + g.RowHeight < low || y0 > high)
            {
                continue;
            }

            picked.AddRange(TimelineModel.EventsIn(g.Lines[i], selecting.Time, view.TimeAt(mouse.X, left), tolerance,
                MarkWidth(view)).Select(e => e.Ref.Id));
        }

        var events = selecting.Add ? workspace.SelectedEvents.Select(e => e.Id).Concat(picked) : picked;
        workspace.SelectEvents(events);
    }

    /// <summary>The keys of the Timeline while it has the focus: Delete, Ctrl+C, Ctrl+V, Ctrl+D and F.</summary>
    private void Keys(TimelineModel model, TimelineView view, float width)
    {
        var io = ImGui.GetIO();
        if (!window.IsFocused(PanelId) || io.WantTextInput || drag != null)
        {
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F, false) && !io.KeyCtrl)
        {
            FrameSelection(model, view, width);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            workspace.DeleteSelection();
        }
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C, false))
        {
            workspace.Copy();
        }
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.V, false))
        {
            // A paste onto the row of a selected entity moves the events to that entity.
            var row = window.Selection is ThnRef { Kind: ThnElementKind.Entity } entity && workspace.Element(entity) is { } element
                ? ThnScriptModel.NameOf(element.Table)
                : null;
            workspace.Paste(workspace.Transport?.Time ?? 0, row);
        }
        else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
        {
            workspace.Duplicate();
        }
    }

    /// <summary>The menu of a right click: the menu of an event, or the menu of a row.</summary>
    private void Menu(TimelineModel model)
    {
        if (openMenu)
        {
            ImGui.OpenPopup(MenuId);
            openMenu = false;
        }

        if (menu is not { } target || !ImGui.BeginPopup(MenuId))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        if (target.Event is { } ev)
        {
            EventMenu(model, ev);
        }
        else
        {
            RowMenu(target);
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The menu of an event: copy, duplicate, delete, and Move before another event of the same time.</summary>
    private void EventMenu(TimelineModel model, TimelineEvent ev)
    {
        var session = window.Session;
        var editable = session is { NoEditReason: null } && session.Edits.CutsceneReadOnlyReason(ev.Ref) == null;
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy", true))
        {
            workspace.Copy();
        }

        if (Theme.IconMenuItem(EditorIcons.Clone, "Duplicate", editable))
        {
            workspace.Duplicate();
        }

        ImGui.SetItemTooltip("Adds a copy of the selected events right after them (Ctrl+D).");
        if (Theme.IconMenuItem(EditorIcons.TrashAlt, "Delete", editable))
        {
            workspace.DeleteSelection();
        }

        ImGui.Separator();
        var others = model.Events.Where(e => e.Time == ev.Time && e.Ref.Id != ev.Ref.Id).ToList();
        if (ImGui.BeginMenu($"{EditorIcons.Stream}   Move before", editable && others.Count > 0))
        {
            foreach (var other in others)
            {
                var refusal = ThnEdits.ReorderRefusal(session!.Edits, ev.Ref, other.Ref);
                var label = other.Targets.Count > 0 ? $"{other.Type} {other.Targets[0]}" : other.Type;
                if (ImGui.MenuItem($"{label}##{other.Ref.Id.Value}", "", false, refusal == null))
                {
                    window.Edits.Execute(ThnEdits.Reorder(ev.Ref, other.Ref));
                }

                ImGui.SetItemTooltip(refusal ?? $"Runs the event before {label}. The engine runs the events of one time " +
                                     "in the order of the file (ThnScript.cs:150-152).");
            }

            ImGui.EndMenu();
        }

        if (others.Count == 0)
        {
            ImGui.SetItemTooltip("No other event has the same time.");
        }
    }

    /// <summary>The menu of a row: Add event with the types that fit the entity, Paste here, and Delete entity.</summary>
    private void RowMenu(MenuTarget target)
    {
        var line = target.Line;
        var entity = line.Lane?.Monitor ?? line.Row?.Entity;
        var name = line.Lane?.Name ?? line.Row?.Name;
        var session = window.Session;
        var path = workspace.Path!;
        var editable = session is { NoEditReason: null } && session.Edits.CutsceneReadOnlyReason(path) == null;
        var working = workspace.Working;
        var choices = entity != null && name != null && working != null
            ? TimelineEdits.AddChoices(working, name, CutsceneWorkspace.Symbols)
            : [];
        if (ImGui.BeginMenu($"{EditorIcons.Plus}   Add event", editable && choices.Count > 0))
        {
            foreach (var choice in choices)
            {
                if (ImGui.MenuItem(choice.Role.Type, "", false, choice.Refusal == null))
                {
                    if (window.Edits.Execute(() => TimelineEdits.Add(path, choice, name!, target.Time)))
                    {
                        workspace.SelectEvents(TimelineEdits.Added(workspace.Working!, 1));
                    }
                }

                ImGui.SetItemTooltip(choice.Refusal ??
                                     $"Adds a {choice.Role.Type} at {Seconds(target.Time)} s" +
                                     (choice.Second != null ? $" with the second target {choice.Second}." : "."));
            }

            ImGui.EndMenu();
        }

        if (Theme.IconMenuItem(EditorIcons.Clone, "Paste here", editable && workspace.Clip != null))
        {
            workspace.Paste(target.Time, name);
        }

        ImGui.SetItemTooltip("Pastes the copied events at this time. The events of the row of the copy move to this row.");
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.TrashAlt, "Delete entity...", editable && entity != null))
        {
            workspace.DeleteEntity(entity!);
        }
    }

    /// <summary>The text of the label cell: the arrow and the name of a group, or the glyph and the name of a row.</summary>
    private static void Label(ImDrawListPtr drawList, TimelineLine line, bool collapsed, Vector2 min, float labelWidth,
        float rowHeight)
    {
        var scale = ImGuiHelper.Scale;
        var padding = LabelPadding * scale;
        var textY = min.Y + (rowHeight - ImGui.GetTextLineHeight()) / 2;
        var right = min.X + labelWidth - padding;
        drawList.PushClipRect(min, new Vector2(min.X + labelWidth, min.Y + rowHeight), true);
        if (line.IsHeader)
        {
            var group = line.Group;
            var count = group.Lanes.Count + group.Rows.Count;
            var text = $"{group.Label}  {count}";
            drawList.AddText(new Vector2(min.X + padding, textY), TextColor,
                (collapsed ? EditorIcons.CaretRight : EditorIcons.CaretDown).ToString());
            drawList.AddText(new Vector2(min.X + padding + 14 * scale, textY), TextColor, text);
        }
        else
        {
            var (kind, name, dim, mark) = line.Lane is { } lane
                ? (ObjectKinds.Thn.Monitor, lane.Main ? $"{lane.Name} (main)" : lane.Name, !lane.Applies, (string?)null)
                : (line.Row!.Kind, line.Row.Name, line.Row.Entity == null, line.Row.Mark);
            var x = min.X + padding + 14 * scale;
            drawList.AddText(new Vector2(x, textY), kind.Abgr, kind.Icon.ToString());
            x += 20 * scale;
            var markWidth = mark != null ? 18 * scale : 0;
            TextFit.AddText(drawList, ImGuiHelper.Roboto, ImGui.GetFontSize(), new Vector2(x, textY),
                dim ? DimColor : TextColor, name, right - x - markWidth);
            if (mark != null)
            {
                drawList.AddText(new Vector2(right - 14 * scale, textY), mark == TimelineModel.IgnoredMonitorMark
                    ? AccentColor : WarningColor, EditorIcons.InfoCircle.ToString());
            }
        }

        drawList.PopClipRect();
    }

    /// <summary>The tooltip of a label cell, or null.</summary>
    private static string? LabelTooltip(TimelineLine line) => line switch
    {
        { IsHeader: true } => "Click to open or close the group.",
        { Lane: { } lane } => $"Monitor {lane.Name}. {lane.Note}\nRight-click to add a cut.",
        { Row: { Mark: { } mark } row } => $"{row.Kind.Label} {row.Name}. {mark}",
        { Row: { } row } => $"{row.Kind.Label} {row.Name}. Click to select it. Right-click to add an event.",
        _ => null,
    };

    /// <summary>
    /// A monitor lane: each shot as a segment with the name of its camera, and each cut as a diamond. A cut that the
    /// game does not run in this context is grey with its shot: each cut of a lane that the engine ignores, and each
    /// cut of a set script or a fidget. A cut after the end has the warning color, as the other events have.
    /// </summary>
    private static void Lane(ImDrawListPtr drawList, TimelineView view, TimelineLane lane, float left, float top,
        float rowHeight, Selection selection, TimelineEvent? hit)
    {
        var inset = BarInset * ImGuiHelper.Scale;
        var camera = ObjectKinds.Thn.Camera.Color;
        for (var i = 0; i < lane.Cuts.Count; i++)
        {
            var cut = lane.Cuts[i];
            var x0 = view.XOf(cut.Event.Time, left);
            var x1 = view.XOf(cut.End, left);
            var runs = cut.Event.Ignored == null;
            var fill = runs ? EditorTheme.ToAbgr(camera with { W = i % 2 == 0 ? 0.45f : 0.3f }) : GreyFill;
            if (x1 > x0)
            {
                drawList.AddRectFilled(new Vector2(x0, top + inset), new Vector2(x1, top + rowHeight - inset), fill);
                TextFit.AddText(drawList, ImGuiHelper.Roboto, ImGui.GetFontSize(),
                    new Vector2(x0 + DiamondHalf * ImGuiHelper.Scale + 3, top + (rowHeight - ImGui.GetTextLineHeight()) / 2),
                    runs ? TextColor : DimColor, cut.Camera, x1 - x0 - DiamondHalf * ImGuiHelper.Scale - 6);
            }
        }

        foreach (var cut in lane.Cuts)
        {
            var color = cut.Event.Ignored != null ? DimColor
                : cut.Event.AfterEnd != null ? WarningColor
                : ObjectKinds.Thn.Camera.Abgr;
            Diamond(drawList, view.XOf(cut.Event.Time, left), top + rowHeight / 2, color,
                selection.Events.Contains(cut.Event.Ref.Id) || ReferenceEquals(cut.Event, hit));
        }
    }

    /// <summary>A bar from the start to the end of an event, with its type, its second target and its keys.</summary>
    private static void Bar(ImDrawListPtr drawList, TimelineView view, TimelineRow row, TimelineEvent ev, float left,
        float top, float rowHeight, bool selected, bool hovered)
    {
        var scale = ImGuiHelper.Scale;
        var inset = BarInset * scale;
        var min = new Vector2(view.XOf(ev.Time, left), top + inset);
        var max = new Vector2(Math.Max(view.XOf(ev.End, left), min.X + 2), top + rowHeight - inset);
        drawList.AddRectFilled(min, max, EventFill(row.Kind, ev, 0.5f), 3 * scale);
        if (selected || hovered)
        {
            drawList.AddRect(min, max, selected ? AccentColor : TextColor, 3 * scale, ImDrawFlags.None,
                selected ? 2 * scale : 1);
        }

        foreach (var key in ev.Keys)
        {
            var x = view.XOf(key, left);
            PixelLine.Vertical(drawList, x, max.Y - KeyTick * scale, max.Y, KeyColor);
        }

        var text = ev.SecondTarget != null ? $"{ev.Type} {EditorIcons.Link} {ev.SecondTarget}" : ev.Type;
        TextFit.AddText(drawList, ImGuiHelper.Roboto, ImGui.GetFontSize(),
            new Vector2(min.X + 4 * scale, top + (rowHeight - ImGui.GetTextLineHeight()) / 2),
            ev.Ignored != null ? DimColor : TextColor, text, max.X - min.X - 8 * scale);
    }

    /// <summary>A diamond at a time, with an outline when it is selected or under the mouse.</summary>
    private static void Diamond(ImDrawListPtr drawList, float x, float y, uint color, bool outlined)
    {
        var half = DiamondHalf * ImGuiHelper.Scale;
        var (a, b, c, d) = (new Vector2(x, y - half), new Vector2(x + half, y), new Vector2(x, y + half),
            new Vector2(x - half, y));
        drawList.AddQuadFilled(a, b, c, d, color);
        if (outlined)
        {
            drawList.AddQuad(a, b, c, d, AccentColor, 2 * ImGuiHelper.Scale);
        }
    }

    /// <summary>
    /// The fill of an event: grey when the game does not run it in this context, the warning color after the end, and
    /// else the color of the row.
    /// </summary>
    private static uint EventFill(ObjectKind kind, TimelineEvent ev, float alpha) =>
        ev.Ignored != null ? GreyFill
        : ev.AfterEnd != null ? WarningFill
        : EditorTheme.ToAbgr(kind.Color with { W = alpha });

    private static void Playhead(ImDrawListPtr drawList, float x, float top, float bottom) =>
        PixelLine.Vertical(drawList, x, top, bottom, PlayheadColor);

    /// <summary>
    /// The mouse over the tracks: Ctrl and the wheel zoom at the mouse, Shift and the wheel scroll in time, and the
    /// middle mouse button drags the time.
    /// </summary>
    private void Mouse(TimelineModel model, TimelineView view, Vector2 origin, float left, float width, float height)
    {
        var io = ImGui.GetIO();
        var mouse = ImGui.GetMousePos();
        var over = mouse.X >= left && mouse.X <= left + width && mouse.Y >= origin.Y && mouse.Y <= origin.Y + height &&
                   ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        if (over && io.MouseWheel != 0 && io.KeyCtrl)
        {
            view.ZoomAt(mouse.X, left, Math.Pow(WheelZoom, io.MouseWheel), model.End, width);
        }
        else if (over && (io.MouseWheel != 0 && io.KeyShift || io.MouseWheelH != 0))
        {
            var steps = io.MouseWheelH != 0 ? -io.MouseWheelH : -io.MouseWheel;
            view.Pan(steps * width / 10, model.End, width);
        }

        if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            panning = true;
        }

        if (panning && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            view.Pan(-io.MouseDelta.X, model.End, width);
        }
        else
        {
            panning = false;
        }
    }

    /// <summary>F: shows the selected events, or the events of the selected entity, or the whole cutscene.</summary>
    private void FrameSelection(TimelineModel model, TimelineView view, float width)
    {
        var selected = window.Selection as ThnRef;
        var events = workspace.SelectedEvents.Select(e => model.Event(e.Id)).OfType<TimelineEvent>().ToList();
        if (events.Count == 0 && selected is { Kind: ThnElementKind.Entity })
        {
            events = model.Groups.SelectMany(g => g.Rows).Where(r => selected.Equals(r.Entity)).SelectMany(r => r.Events)
                .Concat(model.Lanes.Where(l => selected.Equals(l.Monitor)).SelectMany(l => l.Cuts).Select(c => c.Event))
                .ToList();
        }

        if (events.Count == 0)
        {
            view.Fit(model.End, width);
            return;
        }

        view.Frame(events.Min(e => e.Time), events.Max(e => e.End), model.End, width);
    }

    /// <summary>The index of the lane or the row of a name in the lines, or -1. For a row with sub-rows, it is the
    /// index of the first sub-row.</summary>
    internal static int IndexOf(IReadOnlyList<TimelineLine> lines, string name)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if ((lines[i].Row?.Name ?? lines[i].Lane?.Name)?.Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                lines[i].Row?.Mark != TimelineModel.HiddenEntityMark)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The step of the major ticks, so that two labels are at least 70 points apart.</summary>
    private static double TickStep(double pixelsPerSecond)
    {
        var gap = MinTickGap * ImGuiHelper.Scale;
        foreach (var step in TickSteps)
        {
            if (step * pixelsPerSecond >= gap)
            {
                return step;
            }
        }

        return TickSteps[^1];
    }

    private static string Seconds(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>A tick label with as many decimals as the step needs.</summary>
    private static string Seconds(double seconds, double step) =>
        seconds.ToString(step >= 1 ? "0" : step >= 0.1 ? "0.0" : step >= 0.01 ? "0.00" : "0.000",
            CultureInfo.InvariantCulture);

    /// <summary>The selected events and the selected entity of the open cutscene.</summary>
    private sealed record Selection(IReadOnlySet<ThnElementId> Events, ThnRef? Entity);

    /// <summary>A press on an event that becomes a drag when the mouse moves.</summary>
    /// <param name="WasSelected">True when the event was selected before the press.</param>
    private sealed record Press(TimelineEvent Event, TimelineDragKind Kind, double Time, Vector2 Mouse, bool WasSelected);

    /// <summary>A box select from a press on empty track space.</summary>
    /// <param name="Add">True with Shift: the box adds to the selection.</param>
    /// <param name="Entity">The entity of the row of the press, which a click without a box selects.</param>
    private sealed record Box(Vector2 Start, double Time, bool Add, ThnRef? Entity);

    /// <summary>The lines of the last frame and where they were.</summary>
    private sealed record Geometry(IReadOnlyList<TimelineLine> Lines, float Top, float RowHeight, float Left,
        float VisibleTop, float VisibleBottom);

    /// <summary>The line, the event and the time of a right click.</summary>
    private sealed record MenuTarget(TimelineLine Line, TimelineEvent? Event, double Time);
}
