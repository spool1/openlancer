using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Database;

/// <summary>
/// Tables of the game data. A category list at the left selects the systems, the ships, the goods, all the equipment
/// or one type of equipment. The table at the right shows the items of the category.
/// </summary>
/// <remarks>
/// The systems and the goods come from the edit view, not from the game data, so they show each edit at once. A dot
/// marks a good that the active mod adds or changes, and a new system that the mod has not saved. A good row selects its
/// section. A system row selects the system, or the <c>[system]</c> of a new system.
/// </remarks>
internal sealed class DatabasePanel(EditorWindow window)
{
    private const string SystemsId = "systems";
    private const string GoodsId = "goods";

    /// <summary>The empty state of a table whose rows the filter hides (#56). The next action is a link.</summary>
    internal static readonly EmptyState NoRowMatch = new($"No rows match '{EmptyState.TextPlace}'.", "Clear filter");

    // Sizes in unscaled points.
    private const float CategoryMin = 120;
    private const float CategoryMaxFraction = 0.4f;
    private const float SplitterGap = 4;
    private const float ListPadding = 4;
    private const float FilterMinWidth = 120;
    private const float NicknameWidth = 160;
    private const float NameWidth = 200;
    private const float NumberWidth = 80;

    private GameDataManager? loadedData;
    private Category[] categories = [];
    private Category? current;
    private string filter = "";

    // True when the category list must scroll to the current category, after a data load.
    private bool revealCurrent;

    /// <summary>One entry of the category list.</summary>
    /// <param name="Id">The id that the settings keep, for example "equipment/Gun".</param>
    /// <param name="RowKind">The kind of the rows that show, or null for all the rows of the table.</param>
    /// <param name="Depth">The indent level in the list.</param>
    /// <param name="Remake">Makes the category again, for a category whose rows come from the edit view. Null for a
    /// category of the loaded game data.</param>
    /// <param name="Version">The version of the session that the rows of a category from the edit view show.</param>
    private sealed record Category(string Id, string Name, ObjectKind Kind, DatabaseTable Table, ObjectKind? RowKind,
        int Depth, int Count, Func<EditorSession, Category>? Remake = null, int Version = 0);

    /// <summary>Forgets the tables of the game data. Call it when the profile closes.</summary>
    public void Reset()
    {
        loadedData = null;
        categories = [];
        current = null;
    }

    /// <summary>Draws the items of the toolbar strip: the filter field and the row count at the right.</summary>
    public void Toolbar()
    {
        // The strip draws before the body. The tables are made here, so the first frame with the data already has
        // the count and the field width of the frames after it.
        var table = window.Session?.GameData is { } data ? CurrentCategory(data).Table : null;
        var scale = ImGuiHelper.Scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var count = "";
        if (table != null)
        {
            // The filter applies here, so the count of this frame matches the text in the field.
            ApplyView();
            count = filter.Length == 0
                ? $"{DatabaseTable.Count(table.Total)} {table.Noun}"
                : $"{DatabaseTable.Count(table.Shown)} of {DatabaseTable.Count(table.Total)}";
        }

        ImGui.PushFont(null, EditorTheme.CaptionSize);
        var countSize = ImGui.CalcTextSize(count);
        ImGui.PopFont();
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + ImGui.GetContentRegionAvail().X;
        var fieldWidth = Math.Max(FilterMinWidth * scale, right - start.X - countSize.X - 2 * spacing);
        ToolbarItems.SearchField("dbfilter", "Filter by nickname or name", ref filter, fieldWidth);
        if (count.Length == 0)
        {
            return;
        }

        // The count is smaller than the field text. It draws at its own position, centered on the field. As an item
        // after the field, it would get the text baseline of the field and sit too low.
        var position = new Vector2(Math.Max(start.X + fieldWidth + spacing, right - countSize.X),
            start.Y + MathF.Truncate((ImGui.GetFrameHeight() - countSize.Y) / 2));
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        ImGui.GetWindowDrawList().AddText(position, ImGui.GetColorU32(ImGuiCol.TextDisabled), count);
        ImGui.PopFont();
    }

    public void Draw(GameDataManager? data)
    {
        if (data == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        CurrentCategory(data);
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        var scale = ImGuiHelper.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var gap = MathF.Truncate(SplitterGap * scale);
        var settings = window.Settings;
        var maxWidth = Math.Max(CategoryMin, CategoryMaxFraction * avail.X / scale);
        var listWidth = MathF.Truncate(Math.Clamp(settings.DatabaseCategoryWidth, CategoryMin, maxWidth) * scale);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(MathF.Truncate(ListPadding * scale)));
        var visible = ImGui.BeginChild("##categories", new Vector2(listWidth, avail.Y),
            ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (visible)
        {
            Categories(focused);
        }

        ImGui.EndChild();

        // A line in the middle of the gap separates the list from the table. A drag on the gap moves it.
        PixelLine.Vertical(ImGui.GetWindowDrawList(), origin.X + listWidth + gap / 2, origin.Y, origin.Y + avail.Y,
            ImGui.GetColorU32(EditorTheme.Border));
        var shown = listWidth / scale;
        var value = shown;
        if (Splitter.Draw("##split-categories", origin + new Vector2(listWidth, 0), new Vector2(gap, avail.Y), true,
                ref value, 1, CategoryMin, maxWidth, EditorSettings.DefaultDatabaseCategoryWidth))
        {
            settings.Save();
        }

        if (value != shown)
        {
            settings.DatabaseCategoryWidth = value;
        }

        // A click in the list can change the category, so the view applies again here.
        ApplyView();
        var table = current!.Table;
        ImGui.SetCursorScreenPos(origin + new Vector2(listWidth + gap, 0));
        if (table.Shown == 0 && filter.Length > 0)
        {
            if (PanelStates.MessageWithLink(NoRowMatch.With(filter)))
            {
                filter = "";
            }

            return;
        }

        table.Draw(window, focused);
    }

    /// <summary>
    /// The category that shows. The tables and the category list are made once for each data load. The tables of the
    /// systems and the goods come from the edit view, so they are made again after an edit, while they show.
    /// </summary>
    private Category CurrentCategory(GameDataManager data)
    {
        if (!ReferenceEquals(data, loadedData))
        {
            Build(data);
        }

        if (current!.Remake is { } remake && window.Session is { } session && session.Version != current.Version)
        {
            var made = remake(session);
            categories[Array.FindIndex(categories, c => c.Id == made.Id)] = made;
            current = made;
        }

        return current!;
    }

    /// <summary>The systems category: the <c>[system]</c> sections of the edit view, with the counts of the loaded game
    /// data. A new system shows at once, with a dot and empty counts.</summary>
    private Category SystemsCategory(EditorSession session)
    {
        var table = SystemsTable(SystemEntry.All(session.Edits, nickname => session.GameData?.Items.Systems.Get(nickname)),
            session.SectionName, OpenSystem);
        return new Category(SystemsId, "Systems", ObjectKinds.StarSystem, table, null, 0, table.RowCount,
            SystemsCategory, session.Version);
    }

    /// <summary>The Systems table of the systems of the edit view. A new system has the dot, the tooltip
    /// <see cref="SystemEntry.NewMark"/> and empty counts, because the loaded game data does not have it.</summary>
    /// <param name="name">The name of a <c>[system]</c> in the edit view.</param>
    /// <param name="open">Opens the item of a row, or null.</param>
    internal static DatabaseTable SystemsTable(IReadOnlyList<SystemEntry> rows, Func<SectionRef, string> name,
        Action<object>? open)
    {
        var byItem = rows.ToDictionary(r => r.Item);
        return DatabaseTable.Create<object>("##db-systems", "systems", rows.Select(r => r.Item),
            SystemColumns(item => byItem[item], name), _ => ObjectKinds.StarSystem, open, "Open system",
            marked: item => byItem[item].IsNew, markTip: SystemEntry.NewMark);
    }

    /// <summary>Opens the system of a row in the Scene. A new system shows the save card of the Scene.</summary>
    private void OpenSystem(object item)
    {
        switch (item)
        {
            case StarSystem system:
                window.OpenSystem(system);
                break;
            case SectionRef { Id.Key: { } nickname }:
                window.OpenSystem(nickname, null);
                break;
        }
    }

    /// <summary>The goods category: the [Good] sections of the edit view. A ship package has the row action Use for
    /// Play (decision D15).</summary>
    private Category GoodsCategory(EditorSession session)
    {
        var rows = new Dictionary<SectionRef, (string Category, string Name, double Price, bool Changed, bool Package)>();
        foreach (var section in session.Edits.List(ItemFileKind.Goods))
        {
            if (section.Id.Type.Equals("Good", StringComparison.OrdinalIgnoreCase) && session.Edits.Get(section) is { } view)
            {
                var price = view.Lines("price") is [{ Values: [var value, ..] }, ..] && value.TryToSingle(out var number)
                    ? number
                    : double.NaN;
                rows[section] = (First(view, "category") ?? "", session.NameOf(view), price, view.IsChanged,
                    PlayShips.IsPackage(section, view));
            }
        }

        var table = DatabaseTable.Create<SectionRef>("##db-goods", "goods", rows.Keys,
        [
            new("Nickname", s => s.Id.Key ?? "", NicknameWidth),
            new("Name", s => rows[s].Name, NameWidth),
            new("Category", s => rows[s].Category, 100),
            new("Price", s => DatabaseTable.Number(rows[s].Price), NumberWidth, s => rows[s].Price),
        ], _ => ObjectKinds.Good, marked: s => rows[s].Changed,
            action: new RowAction<SectionRef>(EditorIcons.Play, "Use for Play", s => rows[s].Package && s.Id.Key != null,
                s => window.UseForPlay(s.Id.Key!)));
        return new Category(GoodsId, "Goods", ObjectKinds.Good, table, null, 0, table.RowCount, GoodsCategory,
            session.Version);
    }

    private static string? First(SectionView view, string name) =>
        view.Lines(name) is [{ Values: [var value, ..] }, ..] ? FieldEdit.FormatValue(value) : null;

    /// <summary>Makes the tables and the category list for newly loaded game data.</summary>
    private void Build(GameDataManager data)
    {
        loadedData = data;
        var session = window.Session!;
        var items = data.Items;
        var ships = DatabaseTable.Create("##db-ships", "ships", items.Ships, ShipColumns(session.Name, items),
            _ => ObjectKinds.Ship);
        var equipment = DatabaseTable.Create<Equipment>("##db-equipment", "equipment", items.Equipment,
            EquipmentColumns(session.Name), ObjectKinds.Of);

        var list = new List<Category>
        {
            SystemsCategory(session),
            new("ships", "Ships", ObjectKinds.Ship, ships, null, 0, ships.RowCount),
            GoodsCategory(session),
        };
        var types = equipment.Kinds.OrderBy(k => k.Kind.Label, StringComparer.OrdinalIgnoreCase).ToList();
        list.Add(new Category("equipment", "Equipment", ObjectKinds.AnyEquipment, equipment, null, 0,
            equipment.RowCount));
        list.AddRange(types.Select(t =>
            new Category($"equipment/{t.Kind.Label}", t.Kind.Label, t.Kind, equipment, t.Kind, 1, t.Count)));
        categories = list.ToArray();
        current = categories.FirstOrDefault(c => c.Id == window.Settings.DatabaseCategory) ?? categories[0];
        revealCurrent = true;
    }

    /// <summary>Applies the filter and the category to the table of the category.</summary>
    private void ApplyView() => current?.Table.SetView(filter, current.RowKind);

    /// <summary>Draws the category list: one row with a glyph, a name and a count for each category.</summary>
    private void Categories(bool focused)
    {
        var style = ImGui.GetStyle();
        ListRow.PushStyle(focused);
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        foreach (var category in categories)
        {
            var min = ImGui.GetCursorScreenPos();
            var selected = ReferenceEquals(category, current);
            // The selection keeps its color under the mouse, so the hover does not hide it.
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ListRow.SelectionColor(focused));
            }

            if (ImGui.Selectable($"##{category.Id}", selected, ImGuiSelectableFlags.None,
                    new Vector2(0, ListRow.Height)) && !selected)
            {
                current = category;
                window.Settings.DatabaseCategory = category.Id;
                window.Settings.Save();
            }

            if (selected)
            {
                ImGui.PopStyleColor();
                if (revealCurrent)
                {
                    ImGui.SetScrollHereY(0.5f);
                    revealCurrent = false;
                }
            }

            ListRow.Text(min, min.X + style.FramePadding.X + category.Depth * style.IndentSpacing, right,
                category.Kind.Icon, category.Kind.Abgr, category.Name, "", DatabaseTable.Count(category.Count));
        }

        ListRow.PopStyle();
    }

    /// <summary>The columns of the Systems table. The counts and the faction come from the loaded game data, so a new
    /// system has empty cells.</summary>
    /// <param name="row">The row of an item of the table.</param>
    private static Column<object>[] SystemColumns(Func<object, SystemEntry> row, Func<SectionRef, string> name) =>
    [
        new("Nickname", i => row(i).Nickname, NicknameWidth),
        new("Name", i => name(row(i).Section), NameWidth),
        LoadedCount("Objects", row, s => s.Objects.Count),
        LoadedCount("Zones", row, s => s.Zones.Count),
        LoadedCount("Lights", row, s => s.LightSources.Count),
        new("Local faction", i => row(i).Loaded?.LocalFaction?.Nickname ?? "", 140),
    ];

    /// <summary>A column of a count of the loaded game data. A new system has an empty cell, which sorts as a missing
    /// number.</summary>
    private static Column<object> LoadedCount(string header, Func<object, SystemEntry> row, Func<StarSystem, int> count) =>
        new(header, i => row(i).Loaded is { } system ? DatabaseTable.Count(count(system)) : "", NumberWidth,
            i => row(i).Loaded is { } system ? count(system) : double.NaN);

    private static Column<Ship>[] ShipColumns(Func<int, string> name, GameItemDb items) =>
    [
        new("Nickname", s => s.Nickname, NicknameWidth),
        new("Name", s => name(s.IdsName), NameWidth),
        new("Type", s => s.ShipType.ToString(), 90),
        new("Class", s => DatabaseTable.Count(s.Class), 60, s => s.Class),
        new("Hit points", s => DatabaseTable.Number(s.Hitpoints), NumberWidth, s => s.Hitpoints),
        new("Mass", s => DatabaseTable.Number(s.Mass), NumberWidth, s => s.Mass),
        new("Hold", s => DatabaseTable.Count(s.HoldSize), 60, s => s.HoldSize),
        new("Price", s => DatabaseTable.Count(items.GetShipPrice(s)), NumberWidth, s => items.GetShipPrice(s)),
        new("Max batteries", s => DatabaseTable.Count(s.MaxShieldBatteries), 100, s => s.MaxShieldBatteries),
        new("Max kits", s => DatabaseTable.Count(s.MaxRepairKits), NumberWidth, s => s.MaxRepairKits),
    ];

    private static Column<Equipment>[] EquipmentColumns(Func<int, string> name) =>
    [
        new("Nickname", e => e.Nickname, NicknameWidth),
        new("Name", e => name(e.IdsName), NameWidth),
        new("Type", e => ObjectKinds.Of(e).Label, 110),
        new("Hardpoint type", e => e.HpType ?? "", 140),
        // Equipment without a good has no price.
        new("Price", e => DatabaseTable.Number(Price(e)), NumberWidth, Price),
        new("Hit points", e => DatabaseTable.Number(e.Hitpoints), NumberWidth, e => e.Hitpoints),
        new("Volume", e => DatabaseTable.Number(e.Volume), NumberWidth, e => e.Volume),
    ];

    private static double Price(Equipment equipment) => equipment.Good?.Ini.Price ?? double.NaN;
}
