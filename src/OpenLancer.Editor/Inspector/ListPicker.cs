using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>The kind of a row of the list picker.</summary>
internal enum ListRowKind
{
    /// <summary>"None": no line. Only a key that is not required has it.</summary>
    None,

    /// <summary>The value of the field, which is outside the list.</summary>
    Missing,

    /// <summary>The value of the field, which is outside the list of files but names a file that exists in the edit
    /// view. A rule with the flag exists accepts it (milestone M22).</summary>
    Other,

    /// <summary>An entry of the list.</summary>
    Entry,
}

/// <summary>One row of the list picker.</summary>
/// <param name="Value">The value that the row gives the field: empty for "None".</param>
/// <param name="Name">The display name: the text of the <c>ids_name</c> of the entry, or the nickname.</param>
/// <param name="Entry">The entry of an entry row, or null.</param>
internal sealed record ListPickerRow(ListRowKind Kind, string Value, string Name, ListEntry? Entry)
{
    /// <summary>The group of the row, or null. The list shows a header above the first visible row of each
    /// group.</summary>
    public string? Group { get; init; }

    /// <summary>A dim text at the right of the row, for example a short label. The search also finds it.</summary>
    public string Note { get; init; } = "";

    /// <summary>The tag of an entry of the active mod: "new" for an entry that is not saved, else "mod".</summary>
    public string Tag => Entry is { Origin: ValueOrigin.ActiveMod } entry ? entry.Unsaved ? "new" : "mod" : "";

    /// <summary>The text of the row: the name, or "None", "Missing: value" and "value (other folder)".</summary>
    public string Text => Kind switch
    {
        ListRowKind.None => "None",
        ListRowKind.Missing => $"Missing: {Value}",
        ListRowKind.Other => $"{Value} (other folder)",
        _ => Name,
    };
}

/// <summary>
/// The rows of the list picker, without ImGui (section 12.2 of the M21 design): "None" for a key that is not required,
/// the "Missing" value, and the entries sorted by name and then by nickname. Each row has an index, so repeated names
/// never clash as ImGui ids.
/// </summary>
internal sealed class ListPickerModel
{
    /// <param name="entries">The entries with their display names.</param>
    /// <param name="none">True when the key can have no line: the first row is "None".</param>
    /// <param name="current">The value of the field: empty when the key has no line.</param>
    /// <param name="currentExists">True when the value of the field names a file that exists, for a rule with the flag
    /// exists. A value outside the list then gets an "other folder" row, not a "Missing" row.</param>
    public ListPickerModel(IEnumerable<(ListEntry Entry, string Name)> entries, bool none, string current,
        bool currentExists = false)
        : this(entries.Select(e => new ListPickerRow(ListRowKind.Entry, e.Entry.Value,
                    string.IsNullOrWhiteSpace(e.Name) ? e.Entry.Value : e.Name, e.Entry))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
                .ToList(), none, current.Trim(), currentExists)
    {
    }

    /// <summary>The rows of a list that the caller orders and groups, for example the animation clips of a cutscene
    /// event. A value outside the list gets the "Missing" row first. The list has no "None".</summary>
    /// <param name="entries">The entry rows, in the order of the list.</param>
    /// <param name="current">The value of the field: empty when the field has no value. The value keeps its spaces, so a
    /// value with a space at the start or at the end gets the "Missing" row.</param>
    public ListPickerModel(IReadOnlyList<ListPickerRow> entries, string current)
        : this(entries, false, current, false)
    {
    }

    private ListPickerModel(IReadOnlyList<ListPickerRow> entries, bool none, string current, bool currentExists)
    {
        var rows = new List<ListPickerRow>(entries.Count + 2);
        if (none)
            rows.Add(new ListPickerRow(ListRowKind.None, "", "None", null));
        if (current.Length > 0 && !entries.Any(r => Is(r.Value, current)))
            rows.Add(new ListPickerRow(currentExists ? ListRowKind.Other : ListRowKind.Missing, current, current, null));
        rows.AddRange(entries);
        Rows = rows;
        Current = current.Length == 0
            ? rows.FindIndex(r => r.Kind == ListRowKind.None)
            : rows.FindIndex(r => r.Kind != ListRowKind.None && Is(r.Value, current));
    }

    public IReadOnlyList<ListPickerRow> Rows { get; }

    /// <summary>The row of the value of the field, or -1. The list scrolls to it when it opens.</summary>
    public int Current { get; }

    /// <summary>
    /// The indices of the rows that the filter shows: each entry whose name, nickname or note contains the text, without
    /// letter case. "None" and "Missing" show only without a filter, so Enter after a filter takes an entry.
    /// </summary>
    /// <param name="shows">A second filter of the entries, for example the body part of a clip, or null.</param>
    public List<int> Visible(string filter, Func<ListPickerRow, bool>? shows = null)
    {
        filter = filter.Trim();
        var result = new List<int>();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (row.Kind == ListRowKind.Entry && shows != null && !shows(row))
                continue;
            if (filter.Length == 0 || row.Kind == ListRowKind.Entry &&
                (row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                 row.Value.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                 row.Note.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                result.Add(i);
        }
        return result;
    }

    /// <summary>The row after a move of the selection by <paramref name="delta"/> in the visible rows. A hidden
    /// selection moves to the first or the last visible row.</summary>
    public static int Step(IReadOnlyList<int> visible, int selected, int delta)
    {
        if (visible.Count == 0)
            return selected;
        var position = IndexOf(visible, selected);
        if (position < 0)
            return delta > 0 ? visible[0] : visible[^1];
        return visible[Math.Clamp(position + delta, 0, visible.Count - 1)];
    }

    /// <summary>The row that Enter assigns: the selection, or the first visible row when the filter hides the
    /// selection. -1 when no row is visible.</summary>
    public static int EnterRow(IReadOnlyList<int> visible, int selected) =>
        IndexOf(visible, selected) >= 0 ? selected : visible.Count > 0 ? visible[0] : -1;

    /// <summary>
    /// The footer lines of a row: the name and the nickname, the kind, the file, the layer, the number of lines that
    /// name it and the detail keys of the list. A row of a list that the caller makes has no entry, so it gets only the
    /// name and the kind.
    /// </summary>
    /// <param name="kind">The label of the kind, for example "Loadout".</param>
    /// <param name="usedBy">The number of lines that name an entry.</param>
    public static IReadOnlyList<string> Footer(ListPickerRow row, string kind, Func<ListEntry, int> usedBy)
    {
        switch (row.Kind)
        {
            case ListRowKind.None:
                return ["No line. The engine uses its default."];
            case ListRowKind.Missing:
                return [$"{row.Value}: no {kind.ToLowerInvariant()} has this name."];
            case ListRowKind.Other:
                return [$"{row.Value}: the file exists, in another folder than the {kind.ToLowerInvariant()} list. The " +
                        "game reads it."];
        }
        var name = Is(row.Name, row.Value) ? row.Value : $"{row.Name} ({row.Value})";
        if (row.Entry is not { } entry)
            return [name, kind];
        var lines = new List<string>
        {
            name,
            entry.Section == null ? kind : $"{kind} in {entry.Section.Path}",
            entry.Origin switch
            {
                ValueOrigin.ActiveMod => entry.Unsaved
                    ? $"The active mod '{entry.ModId}' adds it. It is not saved."
                    : $"The active mod '{entry.ModId}' adds it.",
                ValueOrigin.LowerMod => $"Mod '{entry.ModId}' gives it.",
                _ => "The base data gives it.",
            },
            $"Used by {usedBy(entry)} lines",
        };
        if (entry.Detail.Length > 0)
            lines.Add(entry.Detail);
        if (entry.Problem != null)
            lines.Add(entry.Problem);
        return lines;
    }

    /// <summary>The dim text at the right of a row. It has the nickname when the row shows another name, the note and
    /// the tag, with one space between them.</summary>
    public static string Note(ListPickerRow row)
    {
        var nickname = row.Kind == ListRowKind.Entry && !Is(row.Name, row.Value) ? row.Value : "";
        var tag = row.Tag.Length > 0 ? $"[{row.Tag}]" : "";
        return string.Join(" ", new[] { nickname, row.Note, tag }.Where(text => text.Length > 0));
    }

    private static int IndexOf(IReadOnlyList<int> visible, int row)
    {
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i] == row)
                return i;
        }
        return -1;
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The side pane of a list picker, in place of the footer, for example the clip preview of the animation picker. It
/// shows the row that the mouse hovered last, or the row that the arrow keys selected last.
/// </summary>
/// <param name="Width">The width of the pane, in unscaled points.</param>
/// <param name="Height">The height of the pane and of the rows, in unscaled points.</param>
/// <param name="Filters">Draws more filters below the search field, or null. It returns true when a filter changed in
/// this frame, so the list filters its rows again.</param>
/// <param name="Shows">The filter of the entries that <paramref name="Filters"/> sets, or null.</param>
/// <param name="Draw">Draws the pane for a row, or for no row.</param>
internal sealed record ListPickerPane(float Width, float Height, Func<bool>? Filters, Func<ListPickerRow, bool>? Shows,
    Action<ListPickerRow?> Draw);

/// <summary>
/// The rows that the filters of an open list picker show, and the lines of the list, without ImGui. It filters the rows
/// again only for new rows, for example after an opening. It also filters them again when the search text or a filter
/// of the pane changes.
/// </summary>
internal sealed class ListPickerFilter
{
    private ListPickerModel? model;
    private string text = "";
    private (List<int> Rows, List<int> Lines) visible;

    /// <summary>The rows that the filters show, and the lines of the list (see <see cref="ListPicker.Lines"/>).</summary>
    /// <param name="filter">The search text.</param>
    /// <param name="filtersChanged">True when a filter of the pane changed in this frame.</param>
    /// <param name="shows">The filter of the pane, or null.</param>
    public (List<int> Rows, List<int> Lines) Get(ListPickerModel rows, string filter, bool filtersChanged,
        Func<ListPickerRow, bool>? shows)
    {
        if (filtersChanged || !ReferenceEquals(rows, model) || filter != text)
        {
            var shown = rows.Visible(filter, shows);
            visible = (shown, ListPicker.Lines(rows.Rows, shown));
            model = rows;
            text = filter;
        }

        return visible;
    }
}

/// <summary>
/// The popup of the list picker, as the Unity object picker (decision D113): a search field with the focus, "None", the
/// "Missing" value and the entries, and a footer with the hovered or selected entry. A click on a row and the arrow keys
/// assign the value at once. Enter assigns the selected row and closes. A double-click and a click outside close and keep
/// the value. Esc closes and puts back the value of the opening. One opening is one field edit, so one entry of the edit
/// history (see <see cref="PopupSession"/>).
/// </summary>
/// <remarks>The popup filters the rows again only when the search text or a filter of the pane changes, and it draws
/// only the rows in view. A row with a group gets a header when the row above it is in another group. A side pane takes
/// the place of the footer.</remarks>
internal sealed class ListPicker
{
    // Sizes in unscaled points.
    private const float PopupWidth = 360;
    private const int VisibleRows = 14;

    private readonly PopupSession session = new();
    private readonly ListPickerFilter visible = new();
    private string filter = "";
    private int selected = -1;
    private int hovered = -1;
    private ListPickerModel? model;

    // The row of the pane: the row that the mouse hovered or the keys selected last.
    private int paneRow = -1;

    /// <summary>The popup of a field. Call it in each frame for each field with a list, after the field draws, in the
    /// ImGui id scope of the field.</summary>
    /// <param name="open">Opens the popup in this frame, for example after a click on the field.</param>
    /// <param name="makeModel">Makes the rows when the popup opens.</param>
    /// <param name="kind">The kind of the entries.</param>
    /// <param name="usedBy">The number of lines that name an entry, for the footer.</param>
    /// <param name="owns">True when the open field edit belongs to the popup with an ImGui id.</param>
    /// <param name="route">Gives the events of the popup and the value of the selected row to the field edits.</param>
    /// <param name="pane">The side pane in place of the footer, or null.</param>
    public void Draw(string id, bool open, Func<ListPickerModel> makeModel, ObjectKind kind, Func<ListEntry, int> usedBy,
        Func<uint, bool> owns, Action<uint, FieldEvents, string> route, ListPickerPane? pane = null)
    {
        var popup = ImGui.GetID(id);
        var style = ImGui.GetStyle();
        var width = MathF.Round(PopupWidth * ImGuiHelper.Scale);
        if (pane != null)
        {
            width += MathF.Round(pane.Width * ImGuiHelper.Scale) + style.ItemSpacing.X + 2 * style.WindowPadding.X;
        }

        if (open)
        {
            ImGui.OpenPopup(id);
            // A popup with a pane is wide and high, so it moves left and up to stay inside the window.
            var height = pane == null
                ? 0
                : MathF.Round(pane.Height * ImGuiHelper.Scale) + 3 * ImGui.GetFrameHeightWithSpacing() +
                  2 * style.WindowPadding.Y;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(PopupPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y),
                new Vector2(width, height), viewport.WorkPos, viewport.WorkPos + viewport.WorkSize));
        }

        ImGui.SetNextWindowSize(new Vector2(width, 0));
        var mine = session.IsOpenFor(popup);
        var shown = ImGui.BeginPopup(id);
        if (!shown && !mine)
        {
            return;
        }

        var frame = ImGui.GetFrameCount();
        if (shown && !mine)
        {
            // The opening goes to the field edits first, so the edit starts from the value of the opening.
            model = makeModel();
            filter = "";
            selected = model.Current;
            paneRow = selected;
            route(popup, session.Frame(popup, frame, true, false, false, owns(popup)), Value());
            Content(kind, usedBy, pane, false);
            ImGui.EndPopup();
            return;
        }

        // A dropdown of the pane is a popup in this popup. Read it before the content, because the dropdown can close
        // in the content.
        var dropdown = shown && ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        var (changed, close) = shown ? Content(kind, usedBy, pane, dropdown) : (false, false);
        var escape = Cancels(ImGui.IsKeyPressed(ImGuiKey.Escape, false), dropdown);
        // Enter and a double-click assign the row in this frame and close the popup. The next frame ends the edit and
        // keeps the value.
        var events = session.Frame(popup, frame, shown, escape, changed, owns(popup));
        if (shown)
        {
            if (close || !session.IsOpenFor(popup))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        route(popup, events, Value());
    }

    /// <summary>The position of a popup below a field, inside a work area: the popup moves left and up when it does not
    /// fit. A popup that is larger than the area starts at the top left corner of the area.</summary>
    /// <param name="anchor">The bottom left corner of the field.</param>
    /// <param name="size">The size of the popup. A height of 0 does not move the popup up.</param>
    internal static Vector2 PopupPos(Vector2 anchor, Vector2 size, Vector2 min, Vector2 max) =>
        new(Math.Max(min.X, Math.Min(anchor.X, max.X - size.X)), Math.Max(min.Y, Math.Min(anchor.Y, max.Y - size.Y)));

    /// <summary>
    /// The row of the side pane after a frame. The hovered row takes the pane only when the mouse moved or the wheel
    /// turned. ImGui also gives the hovered row under a mouse that does not move. So without this rule, the row under
    /// the mouse takes back the pane after each arrow key.
    /// </summary>
    /// <param name="paneRow">The row of the pane before the rows draw. An arrow key sets it to the selection.</param>
    /// <param name="hovered">The row under the mouse, or -1.</param>
    /// <param name="mouseMoved">True when the mouse moved or the wheel turned in this frame.</param>
    internal static int PaneRow(int paneRow, int hovered, bool mouseMoved) =>
        hovered >= 0 && mouseMoved ? hovered : paneRow;

    /// <summary>True when Esc cancels the pick. While a dropdown of the pane is open, Esc closes only the
    /// dropdown.</summary>
    /// <param name="dropdown">True when a popup in the list popup was open at the start of the frame.</param>
    internal static bool Cancels(bool escape, bool dropdown) => escape && !dropdown;

    /// <summary>The step of the selection for the arrow keys: 1 for Down, -1 for Up and 0 for no step. While a dropdown
    /// of the pane is open, the arrow keys belong to the dropdown, so the selection does not move.</summary>
    /// <param name="dropdown">True when a popup in the list popup was open at the start of the frame.</param>
    internal static int ArrowStep(bool down, bool up, bool dropdown) => dropdown ? 0 : down ? 1 : up ? -1 : 0;

    /// <summary>
    /// The lines of the list for the visible rows: a row index, or the complement of a row index for the header of the
    /// group of that row. A row with a group gets a header when the row above it is in another group.
    /// </summary>
    internal static List<int> Lines(IReadOnlyList<ListPickerRow> rows, IReadOnlyList<int> visible)
    {
        var lines = new List<int>(visible.Count + 4);
        string? group = null;
        foreach (var index in visible)
        {
            var row = rows[index];
            if (row.Group != null && row.Group != group)
            {
                lines.Add(~index);
            }

            group = row.Group;
            lines.Add(index);
        }

        return lines;
    }

    /// <summary>The value of the selected row.</summary>
    private string Value() => model != null && selected >= 0 && selected < model.Rows.Count ? model.Rows[selected].Value : "";

    /// <summary>Draws the search field, the rows, and the footer or the pane. Returns whether the selection changed, and
    /// whether the popup closes.</summary>
    /// <param name="dropdown">True when a popup in the list popup was open at the start of the frame.</param>
    private (bool Changed, bool Close) Content(ObjectKind kind, Func<ListEntry, int> usedBy, ListPickerPane? pane,
        bool dropdown)
    {
        var rows = model!;
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputTextWithHint("##filter", $"{EditorIcons.MagnifyingGlass}  Search", ref filter, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var filtersChanged = pane?.Filters?.Invoke() == true;
        var (visibleRows, lines) = visible.Get(rows, filter, filtersChanged, pane?.Shows);
        var changed = false;
        var close = false;
        var scroll = ImGui.IsWindowAppearing() ? selected : -1;
        var step = ArrowStep(ImGui.IsKeyPressed(ImGuiKey.DownArrow), ImGui.IsKeyPressed(ImGuiKey.UpArrow), dropdown);
        if (step != 0)
        {
            var next = ListPickerModel.Step(visibleRows, selected, step);
            changed = next != selected;
            selected = next;
            scroll = selected;
            paneRow = selected;
        }

        if (enter && ListPickerModel.EnterRow(visibleRows, selected) is >= 0 and var row)
        {
            changed |= row != selected;
            selected = row;
            close = true;
        }

        var height = pane != null
            ? MathF.Round(pane.Height * ImGuiHelper.Scale)
            : Math.Min(Math.Max(lines.Count, 1), VisibleRows) * ImGui.GetTextLineHeightWithSpacing() +
              ImGui.GetStyle().WindowPadding.Y;
        ImGui.BeginChild("##rows", new Vector2(pane == null ? 0 : MathF.Round(PopupWidth * ImGuiHelper.Scale), height));
        if (lines.Count == 0)
        {
            ImGui.TextDisabled("Nothing matches.");
        }

        hovered = -1;
        var clipper = new ImGuiListClipper();
        clipper.Begin(lines.Count);
        var scrollPosition = scroll < 0 ? -1 : lines.IndexOf(scroll);
        if (scrollPosition >= 0)
        {
            clipper.IncludeItemByIndex(scrollPosition);
        }

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (lines[i] < 0)
                {
                    // A header has the height of a row, so the clipper can skip the lines out of view.
                    ImGui.TextDisabled(rows.Rows[~lines[i]].Group);
                    continue;
                }

                var index = lines[i];
                var item = rows.Rows[index];
                var color = item.Kind == ListRowKind.Missing ? EditorTheme.Error
                    : item.Kind == ListRowKind.None ? EditorTheme.TextDim : EditorTheme.Text;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                var icon = item.Kind == ListRowKind.Entry ? $"{kind.Icon}  " : "    ";
                if (ImGui.Selectable($"{icon}{item.Text}##row{index}", index == selected,
                        ImGuiSelectableFlags.AllowDoubleClick))
                {
                    changed |= index != selected;
                    selected = index;
                    close |= ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                }

                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    hovered = index;
                }

                if (index == scroll)
                {
                    ImGui.SetScrollHereY();
                }

                var note = ListPickerModel.Note(item);
                if (note.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(note);
                }
            }
        }

        clipper.End();
        ImGui.EndChild();
        if (pane != null)
        {
            var io = ImGui.GetIO();
            paneRow = PaneRow(paneRow, hovered, io.MouseDelta != Vector2.Zero || io.MouseWheel != 0);
            ImGui.SameLine();
            ImGui.BeginChild("##pane", new Vector2(MathF.Round(pane.Width * ImGuiHelper.Scale), height),
                ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            pane.Draw(paneRow >= 0 && paneRow < rows.Rows.Count ? rows.Rows[paneRow] : null);
            ImGui.EndChild();
            return (changed, close);
        }

        ImGui.Separator();
        var footer = hovered >= 0 ? hovered : selected;
        if (footer >= 0 && footer < rows.Rows.Count)
        {
            ImGui.PushFont(null, EditorTheme.CaptionSize);
            foreach (var line in ListPickerModel.Footer(rows.Rows[footer], kind.Label, usedBy))
            {
                ImGui.TextWrapped(line);
            }

            ImGui.PopFont();
        }

        return (changed, close);
    }
}
