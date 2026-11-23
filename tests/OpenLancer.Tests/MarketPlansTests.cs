using System;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>The edits of the "Markets" section: a base that sells a good, and the lines of the active mod.</summary>
public sealed class MarketPlansTests
{
    private const string ShipMarketPath = @"DATA\EQUIPMENT\market_ships.ini";

    /// <summary>
    /// The item workspace with a second market file before market_misc.ini in the load order. Base b1 sells the ship
    /// package there, and the gun in market_misc.ini.
    /// </summary>
    private static TestWorkspace TwoMarkets()
    {
        var ws = Create();
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni.Replace(@"markets = equipment\market_misc.ini",
            "markets = equipment\\market_ships.ini\nmarkets = equipment\\market_misc.ini"));
        ws.WriteBase(ShipMarketPath, """
            [BaseGood]
            base = b1
            marketgood = s1_package, 0, -1, 1, 1, 0, 1, 1
            """);
        ws.WriteBase(MarketPath, """
            [BaseGood]
            base = b1
            marketgood = gun1, 0, -1, 10, 10, 0, 1
            [BaseGood]
            base = b2
            marketgood = eng1, 0, -1, 10, 10, 0, 1
            """);
        return ws;
    }

    /// <summary>All values of a line after the good.</summary>
    private static string Values(MarketLine line) => FieldEdit.FormatLine(line.Values.Skip(1).ToList());

    private static string[] Bases(EditSession session, string good) =>
        session.MarketLines(good).Select(l => $"{l.Base}: {Values(l)}").ToArray();

    /// <summary>The field of the value at a place of <see cref="MarketLine.Values"/>.</summary>
    private static MarketValue Field(int index) => MarketPlans.Fields.Single(f => f.Index == index);

    /// <summary>Adds a line that sells the good at base b1 with the values, and returns the line.</summary>
    private static MarketLine AddedLine(EditSession session, string good, string values)
    {
        session.Execute(MarketPlans.AddToBase(session, good, "b1", values));
        return session.MarketLines(good).Single(l => l.Base == "b1");
    }

    [Fact]
    public void ABaseSellsAGoodWithANewSectionInTheMarketFileOfTheSameCategory()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        session.Execute(MarketPlans.AddToBase(session, "eng1", "b1", MarketPlans.DefaultValues("equipment")));

        // The first file of b1 sells a ship, so the engine goes into the file in which b1 sells equipment.
        var line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal(MarketPath, line.Section.Path, ignoreCase: true);
        Assert.Equal(new SectionIdentity("BaseGood", null, null, 2), line.Section.Id);
        Assert.Equal(["b2: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 10, 10, 0, 1"], Bases(session, "eng1"));
        Assert.True(MarketPlans.CanChange(session, line));
        // A lower line changes through a later line of the active mod.
        Assert.True(MarketPlans.CanChange(session, session.MarketLines("eng1")[0]));
        Assert.Equal("Sell eng1 at b1", session.UndoLabel);
    }

    [Fact]
    public void AShipPackageGoesIntoTheMarketFileThatSellsShipsAtTheBase()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        session.Execute(MarketPlans.AddToBase(session, "s1_package", "b2", MarketPlans.DefaultValues("ship")));

        // b2 is only in market_misc.ini.
        var line = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        Assert.Equal(MarketPath, line.Section.Path, ignoreCase: true);
        Assert.Equal("0, -1, 1, 1, 0, 1, 1", Values(line));
    }

    [Fact]
    public void ASecondGoodGoesIntoTheSectionThatTheActiveModAdds()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        session.Execute(MarketPlans.AddToBase(session, "eng1", "b1", "0, -1, 5, 5, 0, 1"));

        session.Execute(MarketPlans.AddToBase(session, "s1_package", "b2", "0, -1, 1, 1, 0, 1, 1"));
        session.Execute(MarketPlans.AddToBase(session, "gun1", "b2", "0, -1, 3, 3, 0, 1"));

        var added = session.MarketLines("gun1").Single(l => l.Base == "b2").Section;
        Assert.Equal(session.MarketLines("s1_package").Single(l => l.Base == "b2").Section, added);
        Assert.Equal(2, session.Get(added)!.Lines("marketgood").Count);
        Assert.True(session.Save().Saved);
        var patch = File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", MarketPath));
        Assert.Contains("base = b2", patch);
        Assert.Contains("marketgood = gun1, 0, -1, 3, 3, 0, 1", patch);
    }

    [Fact]
    public void ABaseThatSellsTheGoodIsRefused()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        var error = Assert.Throws<InvalidOperationException>(() => MarketPlans.AddToBase(session, "gun1", "B1", "0"));

        Assert.Contains("already sells gun1", error.Message);
    }

    [Fact]
    public void ALineOfTheActiveModChangesAndGoes()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        session.Execute(MarketPlans.AddToBase(session, "eng1", "b1", "0, -1, 5, 5, 0, 1"));
        var line = session.MarketLines("eng1").Single(l => l.Base == "b1");

        session.Execute(MarketPlans.SetLine(session, line, "1, -1, 7, 7, 0, 1.5"));
        Assert.Equal("b1: 1, -1, 7, 7, 0, 1.5", Bases(session, "eng1")[1]);

        line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        session.Execute(MarketPlans.SetLine(session, line, null));
        // The section had no other line, so it goes.
        Assert.Null(session.Get(line.Section));
        Assert.Equal(["b2: 0, -1, 10, 10, 0, 1"], Bases(session, "eng1"));
    }

    /// <summary>
    /// A line of a lower layer changes through a later line of the active mod in its market file. The games and the
    /// OpenLancer engine keep the last line of a good (fork commit OL-0080).
    /// </summary>
    [Fact]
    public void ALineOfALowerLayerChangesThroughALaterLine()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var lower = session.MarketLines("gun1")[0];

        session.Execute(MarketPlans.SetLine(session, lower, "0, -1, 1, 1, 0, 2"));

        var lines = session.MarketLines("gun1");
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 1, 1, 0, 2"], Bases(session, "gun1"));
        Assert.Equal(MarketPath, lines[1].Section.Path, ignoreCase: true);
        Assert.True(session.Get(lines[1].Section)!.IsNew);
        var change = MarketPlans.Override(session, lines[1])!;
        Assert.Equal((lines[1].Section, lower.Section, false), (change.Line.Section, change.Lower.Section, change.Removed));
        Assert.Equal("Changed: the base uses this line, not the line of the game", change.Text);
        Assert.Contains("marketgood = gun1, 0, -1, 1, 1, 0, 2", change.Tip);
        Assert.Null(MarketPlans.Override(session, lower));

        // A second edit of the lower line changes the later line, and does not add one more.
        session.Execute(MarketPlans.SetValue(session, lower, Field(MarketLine.PriceIndex), "3"));
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 1, 1, 0, 3"], Bases(session, "gun1"));

        // The revert removes the later line, and its section goes.
        session.Execute(MarketPlans.Revert(session, session.MarketLines("gun1")[1]));
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1"], Bases(session, "gun1"));
        Assert.Throws<InvalidOperationException>(() => MarketPlans.Revert(session, lower));
    }

    /// <summary>A remove of a lower line writes the buy-only form with the old price multiplier. A remove of the later
    /// line keeps the buy-only form, so the lower line does not count again.</summary>
    [Fact]
    public void ARemoveOfALowerLineWritesTheBuyOnlyForm()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        session.Execute(MarketPlans.SetLine(session, session.MarketLines("gun1")[0], "0, -1, 10, 10, 0, 1.5"));

        session.Execute(MarketPlans.SetLine(session, session.MarketLines("gun1")[1], null));

        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 0, 0, 1, 1.5"], Bases(session, "gun1"));
        var change = MarketPlans.Override(session, session.MarketLines("gun1")[1])!;
        Assert.True(change.Removed);
        Assert.Equal("Removed: the base only buys it", change.Text);
        Assert.Equal("Remove gun1 at b1", session.UndoLabel);

        // A remove of the lower line itself also writes the buy-only form, and a remove of a removed line is refused.
        session.Undo();
        session.Undo();
        session.Execute(MarketPlans.SetLine(session, session.MarketLines("gun1")[0], null));
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 0, 0, 1, 1"], Bases(session, "gun1"));
        Assert.Throws<InvalidOperationException>(() =>
            MarketPlans.SetLine(session, session.MarketLines("gun1")[1], null));
    }

    /// <summary>
    /// Each value of a line has its own field. An edit of a field changes only its value, so the transaction type and
    /// the values after the price multiplier stay in their places.
    /// </summary>
    [Fact]
    public void EachFieldChangesOnlyItsValue()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        AddedLine(session, "eng1", "0, -1, 5, 5, 2, 1");
        MarketLine Line() => session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal(["0", "-1", "5", "5", "1"], MarketPlans.Fields.Select(f => MarketPlans.FieldText(Line(), f)));

        session.Execute(MarketPlans.SetValue(session, Line(), Field(MarketLine.RankIndex), "1"));
        session.Execute(MarketPlans.SetValue(session, Line(), Field(MarketLine.ReputationIndex), " 0.5 "));
        session.Execute(MarketPlans.SetValue(session, Line(), Field(MarketLine.MinimumIndex), "7"));
        session.Execute(MarketPlans.SetValue(session, Line(), Field(MarketLine.MaximumIndex), "8"));
        session.Execute(MarketPlans.SetValue(session, Line(), Field(MarketLine.PriceIndex), "1.5"));
        Assert.Equal("1, 0.5, 7, 8, 2, 1.5", Values(Line()));
        Assert.Equal(MarketTransaction.SellOnly, Line().Transaction);

        session.Execute(MarketPlans.AddToBase(session, "s1_package", "b2", MarketPlans.DefaultValues("ship")));
        var ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        session.Execute(MarketPlans.SetValue(session, ship, Field(MarketLine.PriceIndex), "2"));
        Assert.Equal("0, -1, 1, 1, 0, 2, 1", Values(session.MarketLines("s1_package").Single(l => l.Base == "b2")));
    }

    /// <summary>
    /// Review finding of fix S2: a price multiplier on a line without the transaction type went into the place of the
    /// type. The engine ignores a line without the price multiplier, so an edit of a shorter line also writes the values
    /// of a new line in the missing places. Each value keeps its place, and the type is 0: the base buys and sells the
    /// good.
    /// </summary>
    [Fact]
    public void APriceOnALineWithoutTheTransactionTypeKeepsItsPlace()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var line = AddedLine(session, "eng1", "0, -1, 5, 5");
        Assert.Equal(["0", "-1", "5", "5", ""], MarketPlans.Fields.Select(f => MarketPlans.FieldText(line, f)));

        session.Execute(MarketPlans.SetValue(session, line, Field(MarketLine.PriceIndex), "2"));

        line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal("0, -1, 5, 5, 0, 2", Values(line));
        Assert.Equal(MarketTransaction.BuyAndSell, line.Transaction);
        Assert.Equal("2", MarketPlans.FieldText(line, Field(MarketLine.PriceIndex)));

        // A ship line gets the values of a new ship line. A line of only "0, -1" is a line that the games skip, so this
        // line has the rank 2.
        session.Execute(MarketPlans.AddToBase(session, "s1_package", "b2", "2, -1"));
        var ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        session.Execute(MarketPlans.SetValue(session, ship, Field(MarketLine.RankIndex), "3"));
        Assert.Equal("3, -1, 1, 1, 0, 1", Values(session.MarketLines("s1_package").Single(l => l.Base == "b2")));
    }

    /// <summary>
    /// Review finding of fix S2: a values field with fewer values dropped the transaction type, and a later edit put a
    /// price multiplier into the place of the type. A field takes only a number of the kind that the engine reads, so
    /// no edit of a field can remove a value.
    /// </summary>
    [Fact]
    public void AFieldTakesOnlyANumberOfItsKind()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var line = AddedLine(session, "eng1", "0, -1, 5, 5, 1, 1");

        var empty = Assert.Throws<InvalidOperationException>(() =>
            MarketPlans.SetValue(session, line, Field(MarketLine.MaximumIndex), ""));
        Assert.Equal("Maximum stock must be a whole number, for example 3.", empty.Message);
        var fraction = Assert.Throws<InvalidOperationException>(() =>
            MarketPlans.SetValue(session, line, Field(MarketLine.MinimumIndex), "1.5"));
        Assert.Equal("Minimum stock must be a whole number, for example 3.", fraction.Message);
        var list = Assert.Throws<InvalidOperationException>(() =>
            MarketPlans.SetValue(session, line, Field(MarketLine.PriceIndex), "1, 2"));
        Assert.Equal("Price multiplier must be a number, for example 1.5.", list.Message);
        Assert.Throws<InvalidOperationException>(() =>
            MarketPlans.SetValue(session, line, Field(MarketLine.ReputationIndex), "1e3"));

        session.Execute(MarketPlans.SetValue(session, line, Field(MarketLine.PriceIndex), "1.5"));
        line = session.MarketLines("eng1").Single(l => l.Base == "b1");
        Assert.Equal("0, -1, 5, 5, 1, 1.5", Values(line));
        Assert.Equal(MarketTransaction.BuyOnly, line.Transaction);
    }

    /// <summary>Each value of a line, except the transaction type, has a field with a plain name, in the order of the
    /// line.</summary>
    [Fact]
    public void EachValueHasAFieldWithItsOwnName()
    {
        Assert.Equal([1, 2, 3, 4, 6], MarketPlans.Fields.Select(f => f.Index));
        Assert.Equal(["Rank", "Reputation", "Minimum stock", "Maximum stock", "Price multiplier"],
            MarketPlans.Fields.Select(f => f.Name));
        Assert.All(MarketPlans.Fields, f => Assert.NotEmpty(f.Tip));
    }

    /// <summary>The dropdown changes only the sixth value. A ship line keeps its last value too.</summary>
    [Fact]
    public void TheTransactionTypeChangesOnlyTheSixthValue()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var gun = AddedLine(session, "eng1", "0, -1, 5, 5, 0, 1");
        Assert.Equal(MarketTransaction.BuyAndSell, gun.Transaction);

        session.Execute(MarketPlans.SetTransaction(session, gun, MarketTransaction.BuyOnly));
        session.Execute(MarketPlans.AddToBase(session, "s1_package", "b2", MarketPlans.DefaultValues("ship")));
        var ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        session.Execute(MarketPlans.SetTransaction(session, ship, MarketTransaction.SellOnly));

        gun = session.MarketLines("eng1").Single(l => l.Base == "b1");
        ship = session.MarketLines("s1_package").Single(l => l.Base == "b2");
        Assert.Equal("0, -1, 5, 5, 1, 1", Values(gun));
        Assert.Equal(MarketTransaction.BuyOnly, gun.Transaction);
        Assert.Equal("0, -1, 1, 1, 2, 1, 1", Values(ship));
        Assert.Equal("Only sells", MarketSection.TransactionText(ship));
    }

    /// <summary>
    /// A line without the sixth value gets it from the dropdown, with the values of a new line in the other missing
    /// places. A value that the game does not know shows as it is.
    /// </summary>
    [Fact]
    public void ALineWithoutAKnownTransactionTypeShowsWhy()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        // A line of only "0, -1" is a line that the games skip, so this short line has the rank 1.
        var shortLine = AddedLine(session, "eng1", "1, -1");
        Assert.Null(shortLine.Transaction);
        Assert.Equal("Not set", MarketSection.TransactionText(shortLine));

        session.Execute(MarketPlans.SetTransaction(session, shortLine, MarketTransaction.BuyOnly));
        Assert.Equal("1, -1, 10, 10, 1, 1", Values(session.MarketLines("eng1").Single(l => l.Base == "b1")));

        var unknown = AddedLine(session, "s1_hull", "0, -1, 5, 5, 7, 1");
        Assert.Null(unknown.Transaction);
        Assert.Equal("Unknown type 7", MarketSection.TransactionText(unknown));
    }

    /// <summary>Each transaction type has its own plain name in the dropdown.</summary>
    [Fact]
    public void EachTransactionTypeHasItsOwnName()
    {
        var names = Enum.GetValues<MarketTransaction>().Select(MarketSection.Name).ToList();

        Assert.Equal(["Buys and sells", "Only buys", "Only sells"], names);
    }

    [Fact]
    public void TheCategoryOfAGoodComesFromItsSection()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");

        Assert.Equal("ship", MarketPlans.Category(session, "s1_package"));
        Assert.Equal("equipment", MarketPlans.Category(session, "eng1"));
        Assert.Null(MarketPlans.Category(session, "nothing"));
        Assert.Equal("0, -1, 1, 1, 0, 1, 1", MarketPlans.DefaultValues("SHIP"));
        Assert.Equal("0, -1, 10, 10, 0, 1", MarketPlans.DefaultValues(null));
    }
    /// <summary>
    /// The "Markets" section of a good shows the last line of each base, which the games keep. A line that replaces a
    /// lower line shows its result in plain words, and its tooltip names the line that the save writes. Each text of the
    /// section is Simplified Technical English.
    /// </summary>
    [Fact]
    public void TheMarketsSectionShowsTheLastLineOfEachBaseAndItsChange()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        session.Execute(MarketPlans.SetLine(session, session.MarketLines("gun1")[0], "0, -1, 10, 10, 0, 2"));

        var shown = MarketSection.LastLines(session.MarketLines("gun1"));

        var line = Assert.Single(shown);
        Assert.True(session.Get(line.Section)!.IsNew);
        var change = MarketPlans.Override(session, line)!;
        Assert.Equal(new SectionIdentity("BaseGood", null, null, 2), line.Section.Id);
        Assert.Equal("The save writes the line 'marketgood = gun1, 0, -1, 10, 10, 0, 2' in a [BaseGood] of b1 in " +
                     "market_misc.ini. It replaces the line 'marketgood = gun1, 0, -1, 10, 10, 0, 1' of the game. The " +
                     "games keep the last line of a good at a base.", change.Tip);
        string[] texts =
        [
            change.Text, change.Tip, MarketSection.LowerLineTip, MarketSection.OverrideNote, MarketSection.RevertTip,
            MarketSection.RemoveTip(line, lower: true), MarketSection.RemoveTip(line, lower: false),
            .. MarketPlans.Fields.Select(f => f.Tip),
        ];
        Assert.All(texts, t => Assert.True(SteLint.Problems(t).Count == 0, $"{t}: {string.Join(", ", SteLint.Problems(t))}"));
    }

    /// <summary>
    /// Review finding of step 7: the games skip a line in which each value after the good has its default value (fork
    /// commit OL-0081). Two field edits gave such a line, and the section showed it as a change. Now the edit that gives
    /// such a line is refused with the reason, and the later line keeps the first edit. A remove writes the buy-only form.
    /// </summary>
    [Fact]
    public void AnEditThatGivesALineWithOnlyDefaultValuesIsRefused()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        MarketLine Shown() => Assert.Single(MarketSection.LastLines(session.MarketLines("gun1")));
        session.Execute(MarketPlans.SetValue(session, Shown(), Field(MarketLine.MaximumIndex), "0"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            MarketPlans.SetValue(session, Shown(), Field(MarketLine.MinimumIndex), "0"));

        Assert.Equal("The games skip the line 'marketgood = gun1, 0, -1, 0, 0, 0, 1' of b1. Each value after the good " +
                     "has its default value: rank 0, reputation -1, minimum and maximum stock 0, type 0 and price " +
                     "multiplier 1. Change one more value, or remove the line to stop the sale of the good.", error.Message);
        Assert.Empty(SteLint.Problems(error.Message));
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 10, 0, 0, 1"], Bases(session, "gun1"));
        Assert.Throws<InvalidOperationException>(() => MarketPlans.SetLine(session, Shown(), "0, -1, 0, 0, 0, 1"));

        session.Execute(MarketPlans.SetLine(session, Shown(), null));
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 0, 0, 1, 1"], Bases(session, "gun1"));
    }

    /// <summary>
    /// A line of the active mod that the games skip counts as no line. The section shows the line of the game, and that
    /// line shows no change. An edit of the line of the game changes the skipped line, and "Add to base" changes the
    /// skipped line of a base that does not sell the good in the games.
    /// </summary>
    [Fact]
    public void ALineOfTheActiveModWithOnlyDefaultValuesCountsAsNoLine()
    {
        using var ws = TwoMarkets();
        ws.Mod("act", new()
        {
            [@"patches\" + MarketPath] = "[BaseGood]\nol.new = true\nbase = b1\nmarketgood = gun1, 0, -1, 0, 0, 0, 1\n" +
                                         "[BaseGood]\nol.new = true\nbase = b2\nmarketgood = gun1, 0, -1\n",
        });
        var session = ws.Session("act");

        var shown = Assert.Single(MarketSection.LastLines(session.MarketLines("gun1")));
        Assert.False(session.Get(shown.Section)!.IsNew);
        Assert.Null(MarketPlans.Override(session, shown));
        Assert.All(session.MarketLines("gun1").Where(l => session.Get(l.Section)!.IsNew),
            l => Assert.Null(MarketPlans.Override(session, l)));

        session.Execute(MarketPlans.SetValue(session, shown, Field(MarketLine.PriceIndex), "2"));
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 10, 10, 0, 2", "b2: 0, -1"], Bases(session, "gun1"));

        session.Execute(MarketPlans.AddToBase(session, "gun1", "b2", "0, -1, 3, 3, 0, 1"));
        Assert.Equal(["b1: 0, -1, 10, 10, 0, 1", "b1: 0, -1, 10, 10, 0, 2", "b2: 0, -1, 3, 3, 0, 1"],
            Bases(session, "gun1"));
    }

    /// <summary>
    /// Review finding of step 7: the price multiplier of a line of a ship package has no effect, because the price of a
    /// ship at a base comes from the market line of its hull good (fork commit OL-0080). The field is read-only, and its
    /// tooltip names the hull good. The other fields and the lines of other goods take edits.
    /// </summary>
    [Fact]
    public void ThePriceMultiplierOfAShipPackageHasNoEffect()
    {
        using var ws = TwoMarkets();
        var session = ws.Session("act");
        var ship = session.MarketLines("s1_package").Single(l => l.Base == "b1");
        var gun = session.MarketLines("gun1").Single(l => l.Base == "b1");

        var reason = MarketPlans.NoEffectReason(session, ship, Field(MarketLine.PriceIndex));

        Assert.Equal("A line of a ship package does not change the price of the ship. The price of the ship at a base " +
                     "comes from the market line of its hull good s1_hull. Set that price in the Markets section of " +
                     "s1_hull.", reason);
        Assert.Empty(SteLint.Problems(reason!));
        Assert.All(MarketPlans.Fields.Where(f => f.Index != MarketLine.PriceIndex),
            f => Assert.Null(MarketPlans.NoEffectReason(session, ship, f)));
        Assert.All(MarketPlans.Fields, f => Assert.Null(MarketPlans.NoEffectReason(session, gun, f)));
        // A line of the hull good sets the price of the ship, so its price multiplier takes edits.
        var hull = AddedLine(session, "s1_hull", "0, -1, 0, 0, 1, 0.9");
        Assert.Null(MarketPlans.NoEffectReason(session, hull, Field(MarketLine.PriceIndex)));
    }

    /// <summary>A line of a lower mod names the mod in its change.</summary>
    [Fact]
    public void TheChangeOfALineOfALowerModNamesTheMod()
    {
        using var ws = TwoMarkets();
        ws.Mod("lower", new()
        {
            [@"patches\" + MarketPath] = "[BaseGood]\nol.new = true\nbase = b2\nmarketgood = gun1, 0, -1, 4, 4, 0, 1\n",
        });
        ws.WriteProfile("lower", "act");
        var session = ws.Session("act");
        var lower = session.MarketLines("gun1").Single(l => l.Base == "b2");

        session.Execute(MarketPlans.SetLine(session, lower, null));

        var change = MarketPlans.Override(session, session.MarketLines("gun1").Last())!;
        Assert.Equal(("Removed: the base only buys it", true), (change.Text, change.Removed));
        Assert.Contains("of the mod lower.", change.Tip);
        Assert.EndsWith("The minimum stock 0, the maximum stock 0 and the type 1 stop the sale of the good.", change.Tip);
        Assert.Empty(SteLint.Problems(change.Tip));
    }
}
