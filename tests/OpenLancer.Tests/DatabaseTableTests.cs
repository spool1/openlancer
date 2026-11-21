using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor.Database;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

public sealed class DatabaseTableTests
{
    // The data columns: nickname, name, type and price. Only the price is numeric.
    private static readonly bool[] Numeric = [false, false, false, true];

    private static readonly ObjectKind Gun = new("Gun", EditorIcons.Bullseye, EditorTheme.Text);

    private static DatabaseRow Row(int index, string nickname, string name, string type, double price) =>
        new(index, nickname, type == "Gun" ? Gun : ObjectKinds.AnyEquipment,
            [nickname, name, type, price.ToString(System.Globalization.CultureInfo.InvariantCulture)], [0, 0, 0, price]);

    private static List<DatabaseRow> Rows() =>
    [
        Row(0, "li_gun01", "Justice", "Gun", 500),
        Row(1, "br_gun01", "Ripper", "Gun", 3710),
        Row(2, "li_shield01", "Shield", "Shield", 500),
        Row(3, "Ku_gun01", "Kunai", "Gun", 1200),
        Row(4, "no_price", "", "Commodity", double.NaN),
    ];

    private static string[] Nicknames(IEnumerable<DatabaseRow> rows) => rows.Select(r => r.Nickname).ToArray();

    [Fact]
    public void SortsATextColumnWithoutCase()
    {
        var rows = Rows();
        DatabaseTable.Sort(rows, [new SortKey(0, false)], Numeric);
        Assert.Equal(["br_gun01", "Ku_gun01", "li_gun01", "li_shield01", "no_price"], Nicknames(rows));
    }

    [Fact]
    public void SortsANumericColumnByItsNumberAndPutsAMissingNumberLastWhenDescending()
    {
        var rows = Rows();
        DatabaseTable.Sort(rows, [new SortKey(3, true)], Numeric);
        // "3710" sorts before "500" as text, and after it as a number. The ties keep the order of the game data.
        Assert.Equal(["br_gun01", "Ku_gun01", "li_gun01", "li_shield01", "no_price"], Nicknames(rows));
    }

    [Fact]
    public void UsesTheSecondKeyOnlyForRowsWithTheSameFirstKey()
    {
        var rows = Rows();
        DatabaseTable.Sort(rows, [new SortKey(2, false), new SortKey(3, true)], Numeric);
        Assert.Equal(["no_price", "br_gun01", "Ku_gun01", "li_gun01", "li_shield01"], Nicknames(rows));
    }

    [Fact]
    public void KeepsTheGameDataOrderForRowsWithTheSameKeys()
    {
        var rows = Rows();
        rows.Reverse();
        DatabaseTable.Sort(rows, [new SortKey(3, false)], Numeric);
        // The two rows with price 500 are in the order of their index, not in the order of the list.
        Assert.Equal(["no_price", "li_gun01", "li_shield01", "Ku_gun01", "br_gun01"], Nicknames(rows));
    }

    [Fact]
    public void FilterMatchesTheNicknameOrTheNameWithoutCase()
    {
        Assert.Equal(["li_gun01", "li_shield01"], Nicknames(DatabaseTable.Filter(Rows(), "LI_", null)));
        Assert.Equal(["br_gun01"], Nicknames(DatabaseTable.Filter(Rows(), "ripp", null)));
        Assert.Equal(5, DatabaseTable.Filter(Rows(), "", null).Count());
        Assert.Empty(DatabaseTable.Filter(Rows(), "xyz", null));
    }

    [Fact]
    public void FilterKeepsOnlyTheRowsOfTheKind()
    {
        var any = ObjectKinds.AnyEquipment;
        Assert.Equal(["li_shield01", "no_price"], Nicknames(DatabaseTable.Filter(Rows(), "", any)));
        Assert.Equal(["no_price"], Nicknames(DatabaseTable.Filter(Rows(), "price", any)));
    }

    [Fact]
    public void ARowOfAMarkedItemHasTheMark()
    {
        var table = DatabaseTable.Create("##test", "items", ["a", "b", "c"],
            [new Column<string>("Nickname", t => t, 100), new Column<string>("Name", t => t.ToUpperInvariant(), 100)],
            _ => Gun, marked: t => t == "b");

        Assert.Equal([false, true, false], table.Rows.Select(r => r.Marked));
        Assert.Equal(["A", "B", "C"], table.Rows.Select(r => r.Name));
    }

    /// <summary>A system of the loaded game data of a test, without contents.</summary>
    private static StarSystem Loaded(string nickname) => new() { Nickname = nickname, SourceFile = "" };

    /// <summary>The Systems table of a session, as the Database makes it, with the systems of the loaded game data of a
    /// test.</summary>
    private static DatabaseTable Systems(EditSession session, params StarSystem[] loaded)
    {
        var texts = new ItemTexts(session, _ => "", _ => "");
        return DatabasePanel.SystemsTable(
            DatabasePanel.SystemRows(session, n => loaded.FirstOrDefault(s => s.Nickname.Equals(n, StringComparison.OrdinalIgnoreCase))),
            texts.NameOf, null);
    }

    /// <summary>
    /// The Systems table reads the <c>[system]</c> sections of the edit view. So a new system shows at once, before
    /// the save, with the dot, the tooltip that tells to save, its name from the text of the mod and empty counts. Its
    /// row selects its <c>[system]</c>. A row of a system of the loaded game data selects that system, so its full page
    /// shows, and it has no dot.
    /// </summary>
    [Fact]
    public void ANewSystemShowsInSystemsWithTheMarkBeforeTheSave()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        StarSystem[] loaded = [Loaded("Li01"), Loaded("Li02")];
        NewSystemWorkspace.CreateAv01(session);

        var table = Systems(session, loaded);

        Assert.Equal(["Li01", "Li02", "Av01"], table.Rows.Select(r => r.Nickname));
        Assert.Equal([false, false, true], table.Rows.Select(r => r.Marked));
        Assert.Equal(DatabasePanel.NewSystemMark, table.MarkTip);
        Assert.Same(loaded[0], table.Rows[0].Item);
        Assert.Equal("0", table.Rows[0].Texts[2]);
        var av01 = table.Rows[2];
        Assert.Equal(NewSystemWorkspace.System("Av01"), av01.Item);
        Assert.Equal("Avalon", av01.Name);
        Assert.Equal(["", "", "", ""], av01.Texts[2..]);
        Assert.All(av01.Values[2..5], v => Assert.True(double.IsNaN(v)));
        Assert.Empty(SteLint.Problems(DatabasePanel.NewSystemMark));
    }

    /// <summary>
    /// The Database makes the Systems table again after each edit while it shows. A table of the edit view after an
    /// edit has a second new system, a renamed system and the rows after an undo. After the save, the loaded game data
    /// has the new system, so its row has no dot and selects the system.
    /// </summary>
    [Fact]
    public void TheSystemsTableFollowsEachEditBeforeAndAfterTheSave()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        StarSystem[] loaded = [Loaded("Li01"), Loaded("Li02")];
        NewSystemWorkspace.CreateAv01(session);
        Assert.Equal(["Li01", "Li02", "Av01"], Systems(session, loaded).Rows.Select(r => r.Nickname));

        var second = SystemPlans.Create(session, "Av02", "Li02", (5, 5), "Bravo", "A second system.");
        session.Execute(second.Command!);
        var table = Systems(session, loaded);
        Assert.Equal(["Li01", "Li02", "Av01", "Av02"], table.Rows.Select(r => r.Nickname));
        Assert.Equal(["", "", "Avalon", "Bravo"], table.Rows.Select(r => r.Name));
        Assert.Equal([false, false, true, true], table.Rows.Select(r => r.Marked));

        session.Execute(SystemPlans.Rename(session, "Av02", "Av03").Command!);
        Assert.Equal(["Li01", "Li02", "Av01", "Av03"], Systems(session, loaded).Rows.Select(r => r.Nickname));
        session.Undo();
        session.Undo();
        Assert.Equal(["Li01", "Li02", "Av01"], Systems(session, loaded).Rows.Select(r => r.Nickname));

        Assert.True(session.Save().Saved);
        var saved = Loaded("Av01");
        table = Systems(session, [.. loaded, saved]);
        Assert.Equal([false, false, false], table.Rows.Select(r => r.Marked));
        Assert.Same(saved, table.Rows[2].Item);
        Assert.Equal("Avalon", table.Rows[2].Name);
    }

    /// <summary>A nickname that two <c>[system]</c> sections share gets one row, because the engine uses only one of
    /// them.</summary>
    [Fact]
    public void TwoSystemsWithOneNicknameGiveOneRow()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");

        var rows = DatabasePanel.SystemRows(session, _ => null);

        Assert.Single(rows, r => r.Nickname == "twice");
        Assert.Equal(session.SystemSections().Select(s => s.Id.Key).Distinct(StringComparer.OrdinalIgnoreCase),
            rows.Select(r => r.Nickname));
        Assert.All(rows, r => Assert.True(r.IsNew));
    }
}
