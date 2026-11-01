using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Database;

/// <summary>One column of a Database table.</summary>
/// <param name="Name">The header text.</param>
/// <param name="Text">The cell text.</param>
/// <param name="Width">The start width, in unscaled points.</param>
/// <param name="Value">
/// The number of a numeric column, or null for a text column. A numeric column is right-aligned, sorts by this number
/// and sorts from high to low on the first click. A text column sorts by its text. NaN is a missing number.
/// </param>
internal sealed record Column<T>(string Name, Func<T, string> Text, float Width, Func<T, double>? Value = null);

/// <summary>One more item of the context menu of a row, for example "Use for Play" of a ship package.</summary>
/// <param name="Applies">True for an item whose row has the menu item.</param>
internal sealed record RowAction<T>(char Icon, string Label, Func<T, bool> Applies, Action<T> Run);

/// <summary>One row of a Database table, with all its cell texts and numbers made once for each data load.</summary>
/// <param name="Index">The position of the row in the game data. Rows with the same sort keys keep this order.</param>
/// <param name="Marked">True for a row with a dot next to its glyph, for example a good that the active mod
/// changes.</param>
internal sealed record DatabaseRow(int Index, object Item, ObjectKind Kind, string[] Texts, double[] Values, bool Marked = false)
{
    public string Nickname => Texts[0];
    public string Name => Texts[1];
}

/// <summary>A sort key of a table: the index of a data column and the direction.</summary>
internal readonly record struct SortKey(int Column, bool Descending);

/// <summary>
/// A sortable table of game data items with a filter. The first column has the kind glyph. The second column is the
/// nickname, and the third is the name. The user can resize, move, hide and sort the columns, and ImGui keeps these
/// settings under the table id.
/// </summary>
/// <remarks>
/// The rows are made once for each data load. The filter applies again only when the filter text or the kind changes.
/// The rows sort again only when the user changes the sort or the filter applies again.
/// </remarks>
internal sealed class DatabaseTable
{
    // Sizes in unscaled points.
    private const float IconWidth = 20;
    private const float MarkRadius = 2.5f;

    private const ImGuiTableFlags Flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                                          ImGuiTableFlags.Hideable | ImGuiTableFlags.Sortable |
                                          ImGuiTableFlags.SortMulti | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollX |
                                          ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit |
                                          ImGuiTableFlags.ContextMenuInBody;

    private readonly string id;
    private readonly (string Name, float Width, bool Numeric)[] columns;
    private readonly DatabaseRow[] rows;
    private readonly Action<object>? open;
    private readonly string openLabel;
    private readonly RowAction<object>? action;
    private readonly List<DatabaseRow> shown = [];
    private string filter = "";
    private ObjectKind? kind;
    private bool filtered;

    // The sort of the shown rows. Null when the rows must sort again with the sort of the table.
    private SortKey[]? sortKeys;

    private DatabaseTable(string id, string noun, (string, float, bool)[] columns, DatabaseRow[] rows,
        Action<object>? open, string openLabel, RowAction<object>? action)
    {
        this.id = id;
        Noun = noun;
        this.columns = columns;
        this.rows = rows;
        this.open = open;
        this.openLabel = openLabel;
        this.action = action;
    }

    /// <summary>The word for the items in the count of the toolbar strip, for example "ships".</summary>
    public string Noun { get; }

    /// <summary>The number of rows of the kind that shows, before the filter.</summary>
    public int Total { get; private set; }

    /// <summary>The number of rows that the filter keeps.</summary>
    public int Shown => shown.Count;

    /// <summary>The number of rows of all kinds.</summary>
    public int RowCount => rows.Length;

    /// <summary>The rows of all kinds, in the order of the items.</summary>
    internal IReadOnlyList<DatabaseRow> Rows => rows;

    /// <summary>The number of rows of each kind.</summary>
    public IEnumerable<(ObjectKind Kind, int Count)> Kinds =>
        rows.CountBy(r => r.Kind).Select(c => (c.Key, c.Value));

    /// <summary>Makes a table of the items. The first column must be the nickname and the second the name.</summary>
    /// <param name="id">The ImGui id. ImGui keeps the column settings under it, so it must not change.</param>
    /// <param name="open">
    /// Opens an item, on a double-click and from the context menu. Null for items that do not open.
    /// </param>
    /// <param name="openLabel">The context menu text of <paramref name="open"/>.</param>
    /// <param name="marked">True for an item whose row has a dot next to its glyph, or null for no dots.</param>
    /// <param name="action">One more item of the context menu of the rows, or null.</param>
    public static DatabaseTable Create<T>(string id, string noun, IEnumerable<T> items,
        IReadOnlyList<Column<T>> columns, Func<T, ObjectKind> kind, Action<T>? open = null, string openLabel = "Open",
        Func<T, bool>? marked = null, RowAction<T>? action = null)
        where T : notnull
    {
        var rows = items.Select((item, index) => new DatabaseRow(index, item, kind(item),
                columns.Select(c => c.Text(item)).ToArray(),
                columns.Select(c => c.Value?.Invoke(item) ?? double.NaN).ToArray(),
                marked?.Invoke(item) ?? false))
            .ToArray();
        return new DatabaseTable(id, noun, columns.Select(c => (c.Name, c.Width, c.Value != null)).ToArray(), rows,
            open == null ? null : item => open((T)item), openLabel,
            action == null
                ? null
                : new RowAction<object>(action.Icon, action.Label, item => action.Applies((T)item),
                    item => action.Run((T)item)));
    }

    /// <summary>
    /// Sets the filter text and the kind of the rows that show. Null shows all kinds. The filter matches the nickname
    /// or the name, and case does not matter.
    /// </summary>
    public void SetView(string text, ObjectKind? rowKind)
    {
        if (filtered && text == filter && rowKind == kind)
        {
            return;
        }

        (filter, kind, filtered) = (text, rowKind, true);
        shown.Clear();
        shown.AddRange(Filter(rows, text, rowKind));
        Total = rowKind == null ? rows.Length : rows.Count(r => r.Kind == rowKind);
        sortKeys = null;
    }

    /// <summary>The rows of the kind (null for all kinds) whose nickname or name contains the text.</summary>
    internal static IEnumerable<DatabaseRow> Filter(IEnumerable<DatabaseRow> rows, string text, ObjectKind? kind) =>
        rows.Where(r => (kind == null || r.Kind == kind) &&
                        (text.Length == 0 || r.Nickname.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                         r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Sorts the rows by the keys, the first key first. A numeric column sorts by its number and a text column by its
    /// text, without case. Rows with the same keys keep the order of the game data.
    /// </summary>
    /// <param name="numeric">True for each data column that is numeric.</param>
    internal static void Sort(List<DatabaseRow> rows, IReadOnlyList<SortKey> keys, IReadOnlyList<bool> numeric)
    {
        rows.Sort((a, b) =>
        {
            foreach (var (column, descending) in keys)
            {
                var order = numeric[column]
                    ? a.Values[column].CompareTo(b.Values[column])
                    : string.Compare(a.Texts[column], b.Texts[column], StringComparison.OrdinalIgnoreCase);
                if (order != 0)
                {
                    return descending ? -order : order;
                }
            }

            return a.Index.CompareTo(b.Index);
        });
    }

    /// <summary>Draws the table in the free space of the current window.</summary>
    /// <param name="focused">True when the panel has the focus. The selected row is then blue, and grey if not.</param>
    public void Draw(EditorWindow window, bool focused)
    {
        var scale = ImGuiHelper.Scale;
        if (!ImGui.BeginTable(id, columns.Length + 1, Flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        // The glyph and the nickname stay in view while the user scrolls to the right.
        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize |
                                         ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoSort |
                                         ImGuiTableColumnFlags.NoReorder | ImGuiTableColumnFlags.NoHeaderLabel,
            MathF.Truncate(IconWidth * scale));
        for (var c = 0; c < columns.Length; c++)
        {
            var (name, width, numeric) = columns[c];
            var flags = ImGuiTableColumnFlags.WidthFixed;
            if (c == 0)
            {
                flags |= ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.DefaultSort;
            }

            if (numeric)
            {
                flags |= ImGuiTableColumnFlags.PreferSortDescending;
            }

            // The user id of a data column is its index plus 1, so the sort keys can find the column.
            ImGui.TableSetupColumn(name, flags, MathF.Truncate(width * scale), (uint)(c + 1));
        }

        // ImGui draws the context menu of the header (column visibility, size to fit) when it makes the table layout.
        // A request for the sort specs makes the layout here, so the menu gets the menu colors. The header cells
        // draw after the pop and keep their own hover color.
        EditorTheme.PushMenuColors();
        ImGui.TableGetSortSpecs();
        EditorTheme.PopMenuColors();
        ImGui.TableHeadersRow();
        ApplySort();

        var clipper = new ImGuiListClipper();
        clipper.Begin(shown.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                Row(window, shown[i], focused);
            }
        }

        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>Sorts the shown rows when the user changed the sort, or when the filter applied again.</summary>
    private unsafe void ApplySort()
    {
        if (ImGui.TableGetSortSpecs() is not { } specs)
        {
            return;
        }

        if (specs.SpecsDirty || sortKeys == null)
        {
            // With SortMulti, the table has one spec for each sort column. Shift-click adds a column.
            var keys = new SortKey[specs.SpecsCount];
            var spec = (ImGuiTableColumnSortSpecs*)specs.Specs.Handle;
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = new SortKey((int)spec[i].ColumnUserID - 1,
                    spec[i].SortDirection == ImGuiSortDirection.Descending);
            }

            sortKeys = keys;
            specs.SpecsDirty = false;
            Sort(shown, keys, columns.Select(c => c.Numeric).ToArray());
        }
    }

    private void Row(EditorWindow window, DatabaseRow row, bool focused)
    {
        var scale = ImGuiHelper.Scale;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(row.Index);
        // A section reference is a value, so a row of a new goods table stays selected.
        var selected = Equals(window.Selection, row.Item);
        var cell = ImGui.GetCursorScreenPos();
        var cellWidth = ImGui.GetContentRegionAvail().X;

        // The selection keeps its color under the mouse, so the hover does not hide it.
        var selection = ListRow.SelectionColor(focused);
        ImGui.PushStyleColor(ImGuiCol.Header, selection);
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, selection);
        }

        if (ImGui.Selectable("##row", selected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            window.Selection = row.Item;
            if (open != null && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                open(row.Item);
            }
        }

        ImGui.PopStyleColor(selected ? 2 : 1);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            window.Selection = row.Item;
        }

        if (ImGui.BeginPopupContextItem("##row-menu"))
        {
            EditorTheme.PushMenuColors();
            ContextMenu(row);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // The glyph is centered in its column. A dot at its top right marks the row.
        var icon = row.Kind.Icon.ToString();
        var iconWidth = ImGui.CalcTextSize(icon).X;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(new Vector2(cell.X + MathF.Truncate((cellWidth - iconWidth) / 2), cell.Y), row.Kind.Abgr, icon);
        if (row.Marked)
        {
            var radius = MarkRadius * scale;
            drawList.AddCircleFilled(new Vector2(cell.X + cellWidth - radius, cell.Y + radius), radius,
                ImGui.GetColorU32(EditorTheme.AccentText));
        }

        for (var c = 0; c < columns.Length; c++)
        {
            // A column that is hidden or out of view draws nothing.
            if (!ImGui.TableSetColumnIndex(c + 1))
            {
                continue;
            }

            var text = row.Texts[c];
            if (c == 0)
            {
                ImGui.PushFont(ImGuiHelper.SystemMonospace, 0);
                ImGui.Text(text);
                ImGui.PopFont();
            }
            else if (columns[c].Numeric)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() +
                                    Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X));
                ImGui.Text(text);
            }
            else
            {
                ImGui.Text(text);
            }
        }

        ImGui.PopID();
    }

    private void ContextMenu(DatabaseRow row)
    {
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy nickname", true))
        {
            ImGui.SetClipboardText(row.Nickname);
        }

        if (MenuItems.Item(EditorIcons.Copy, "Copy name", row.Name.Length == 0 ? MenuItems.NoName : null))
        {
            ImGui.SetClipboardText(row.Name);
        }

        if (Theme.IconMenuItem(EditorIcons.Table, "Copy row", true))
        {
            ImGui.SetClipboardText(string.Join('\t', row.Texts));
        }

        if (open != null)
        {
            ImGui.Separator();
            if (Theme.IconMenuItem(EditorIcons.Open, openLabel, true))
            {
                open(row.Item);
            }
        }

        if (action != null && action.Applies(row.Item))
        {
            ImGui.Separator();
            if (Theme.IconMenuItem(action.Icon, action.Label, true))
            {
                action.Run(row.Item);
            }
        }
    }

    /// <summary>A whole number with group separators, for example "12,345".</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A number with group separators and up to two decimals, for example "1,234.5".</summary>
    public static string Number(double value) =>
        double.IsNaN(value) ? "" : value.ToString("#,0.##", CultureInfo.InvariantCulture);
}
