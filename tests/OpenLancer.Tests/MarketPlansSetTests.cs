using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The market change of one base (section C.5 of the bases design): the rows, the copy of another base, the replace,
/// the price factor, the remove, the later lines that replace lower lines, and the buy-only lines of the ship packages.
/// The data is made up (see <see cref="BaseWorkspace"/>).
/// </summary>
public sealed class MarketPlansSetTests
{
    /// <summary>The ship market of the planet with the buy-only form of vanilla: it sells ol_ship_package, and it lists
    /// ol_ship2_package with a buy-only line.</summary>
    private const string BuyOnlyShips = """
        [BaseGood]
        base = Ol01_02_Base
        marketgood = ol_ship_package, 0, -1, 1, 1, 0, 1, 1
        marketgood = ol_ship2_package, 0, -1, 0, 0, 1, 1, 1
        """;

    /// <summary>The ship market of the planet without buy-only lines, as HD Edition 0.7.1 has.</summary>
    private const string SoldShipsOnly = """
        [BaseGood]
        base = Ol01_02_Base
        marketgood = ol_ship_package, 0, -1, 1, 1, 0, 1, 1
        """;

    /// <summary>The lines of a good at a base, as "file: values after the good", in load order.</summary>
    private static string[] Lines(EditSession session, string baseNickname, string good) =>
        session.Items.BaseMarketLines(baseNickname).Where(l => l.Values[0].ToString() == good)
            .Select(l => $"{FileName(l.Section.Path)}: {string.Join(", ", l.Values.Skip(1))}").ToArray();

    private static string FileName(string path) => path[(path.LastIndexOf('\\') + 1)..];

    /// <summary>Plans a change, checks that it can run and that each text of the plan is STE, and runs it.</summary>
    private static MarketPlan Run(EditSession session, string baseNickname, IReadOnlyList<MarketRow> rows,
        IReadOnlyList<string>? remove = null, string? copyFrom = null, bool replace = false, MarketPriceScale? scale = null)
    {
        var plan = MarketPlans.Set(session, baseNickname, rows, remove ?? [], copyFrom, replace, scale);
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        AssertSte(plan);
        session.Execute(plan.Command!);
        return plan;
    }

    private static void AssertSte(MarketPlan plan)
    {
        foreach (var text in plan.Warnings.Concat(plan.Refusals).Select(d => d.Message).Concat(plan.Fixes.Select(f => f.Text)))
            Assert.True(SteLint.Problems(text).Count == 0, $"{text}: {string.Join(", ", SteLint.Problems(text))}");
    }

    /// <summary>A row of a good that the base data sells at the base goes into a new section of the active mod in the
    /// same market file. The games keep that later line. A second change of the good changes that line.</summary>
    [Fact]
    public void ARowOfALowerGoodWritesALaterLineInItsMarketFile()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, BaseWorkspace.Station, [new MarketRow("ol_gun") { PriceMultiplier = 2, MaxStock = 20 }]);

        Assert.Equal(["market_misc.ini: 0, -1, 10, 10, 0, 1", "market_misc.ini: 0, -1, 10, 20, 0, 2"],
            Lines(session, BaseWorkspace.Station, "ol_gun"));
        var added = Assert.Single(plan.Adds);
        Assert.Equal(new SectionIdentity("BaseGood", null, null, 2), added.Id);
        Assert.True(session.Get(added)!.IsNew);
        Assert.Equal("Set market of Ol01_01_Base", session.UndoLabel);

        Run(session, BaseWorkspace.Station, [new MarketRow("ol_gun") { Rank = 3 }]);
        Assert.Equal(["market_misc.ini: 0, -1, 10, 10, 0, 1", "market_misc.ini: 3, -1, 10, 20, 0, 2"],
            Lines(session, BaseWorkspace.Station, "ol_gun"));

        // A row that changes nothing gives no edit.
        var same = MarketPlans.Set(session, BaseWorkspace.Station, [new MarketRow("ol_gun") { Rank = 3 }], []);
        Assert.False(same.CanRun);
        Assert.Equal(MarketPlans.NoChange, Assert.Single(same.Refusals).Rule);
    }

    /// <summary>A row that gives a line in which each value after the good has its default value is refused. The games
    /// skip such a line (fork commit OL-0081), so the line would not stop the sale of the good.</summary>
    [Fact]
    public void ARowThatGivesALineWithOnlyDefaultValuesIsRefused()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var plan = MarketPlans.Set(session, BaseWorkspace.Station,
            [new MarketRow("ol_water"), new MarketRow("ol_gun") { MinStock = 0, MaxStock = 0 }], []);

        Assert.False(plan.CanRun);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal(MissionErrors.FieldValue, refusal.Rule);
        Assert.Equal("goods[1]", plan.RefusedPart);
        Assert.StartsWith("The games skip the line 'marketgood = ol_gun, 0, -1, 0, 0, 0, 1' of Ol01_01_Base. ",
            refusal.Message);
        AssertSte(plan);
    }

    /// <summary>A new good goes into a section of the active mod of the base, else into a new section in the file in
    /// which the base sells goods of its category. A new line gets the values of a new line. A buy-only row gets the
    /// stock 0 and 0.</summary>
    [Fact]
    public void ARowOfANewGoodGetsTheValuesOfANewLine()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        Run(session, BaseWorkspace.Pirate,
        [
            new MarketRow("ol_water"),
            new MarketRow("ol_ship_package") { Transaction = MarketTransaction.BuyOnly },
        ]);

        Assert.Equal(["market_misc.ini: 0, -1, 10, 10, 0, 1"], Lines(session, BaseWorkspace.Pirate, "ol_water"));
        Assert.Equal(["market_misc.ini: 0, -1, 0, 0, 1, 1"], Lines(session, BaseWorkspace.Pirate, "ol_ship_package"));
    }

    /// <summary>A remove of a lower line writes the buy-only form with the old price multiplier. A remove of a line of
    /// the active mod deletes the line, and its section goes. A remove of a good that the base does not have warns.</summary>
    [Fact]
    public void ARemoveWritesTheBuyOnlyFormOrDeletesTheLineOfTheActiveMod()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        Run(session, BaseWorkspace.Pirate, [], ["ol_gun"]);
        Assert.Equal(["market_misc.ini: 0, -1, 5, 5, 0, 1.5", "market_misc.ini: 0, -1, 0, 0, 1, 1.5"],
            Lines(session, BaseWorkspace.Pirate, "ol_gun"));

        Run(session, BaseWorkspace.Pirate, [new MarketRow("ol_water")]);
        var plan = Run(session, BaseWorkspace.Pirate, [], ["ol_water", "ol_ship3_package"]);
        Assert.Empty(Lines(session, BaseWorkspace.Pirate, "ol_water"));
        var warning = Assert.Single(plan.Warnings);
        Assert.Equal("Ol02_01_Base does not sell or buy ol_ship3_package.", warning.Message);
        // The section of the active mod keeps the buy-only line of ol_gun, so it stays.
        Assert.Empty(plan.Deletes);
    }

    /// <summary>The copy of another base gives the last line of each good, in the market file of that line. The replace
    /// removes the goods that the copy and the rows do not give. The price factor of a category scales the copied and
    /// named goods of that category.</summary>
    [Fact]
    public void ACopyWithReplaceAndAPriceFactorGivesTheMarketOfTheOtherBase()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        Run(session, BaseWorkspace.Pirate, [new MarketRow("ol_ship_package")]);

        Run(session, BaseWorkspace.Pirate, [new MarketRow("ol_water") { MinStock = 50 }], copyFrom: BaseWorkspace.Station,
            replace: true, scale: new MarketPriceScale(2, "commodity"));

        Assert.Equal(["market_commodities.ini: 0, -1, 50, 500, 0, 2"], Lines(session, BaseWorkspace.Pirate, "ol_water"));
        Assert.Equal(["market_misc.ini: 0, -1, 5, 5, 0, 1.5", "market_misc.ini: 0, -1, 10, 10, 0, 1"],
            Lines(session, BaseWorkspace.Pirate, "ol_gun"));
        // The replace deletes the ship line of the active mod. No lower layer sells the ship at the base.
        Assert.Empty(Lines(session, BaseWorkspace.Pirate, "ol_ship_package"));
    }

    /// <summary>
    /// Review finding of step 7: a line of a ship package does not change the price of the ship. The price factor does
    /// not scale the line of a ship package, and a factor of the ship category warns. A row that gives the price
    /// multiplier of a ship package warns and names the hull good.
    /// </summary>
    [Fact]
    public void ThePriceOfAShipDoesNotComeFromTheLineOfItsPackage()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        Run(session, BaseWorkspace.Pirate, [new MarketRow("ol_ship_package"), new MarketRow("ol_water")]);

        var plan = Run(session, BaseWorkspace.Pirate, [new MarketRow("ol_ship_package"), new MarketRow("ol_water")],
            scale: new MarketPriceScale(2));

        Assert.Empty(plan.Warnings);
        Assert.Equal(["market_misc.ini: 0, -1, 10, 10, 0, 2"], Lines(session, BaseWorkspace.Pirate, "ol_water"));
        Assert.Equal(["market_misc.ini: 0, -1, 1, 1, 0, 1, 1"], Lines(session, BaseWorkspace.Pirate, "ol_ship_package"));

        var factor = MarketPlans.Set(session, BaseWorkspace.Pirate, [new MarketRow("ol_ship_package")], [],
            priceScale: new MarketPriceScale(2, "ship"));
        Assert.Contains(factor.Warnings, w => w.Message == "The price factor does not change the price of a ship. The " +
                                              "price of a ship at a base comes from the market line of its hull good.");

        plan = Run(session, BaseWorkspace.Pirate, [new MarketRow("ol_ship_package") { PriceMultiplier = 3, Rank = 2 }]);
        Assert.Equal("A line of a ship package does not change the price of the ship. The price of the ship at a base " +
                     "comes from the market line of its hull good ol_ship_hull. To set that price, add a line of " +
                     "ol_ship_hull at the base.", Assert.Single(plan.Warnings).Message);
    }

    /// <summary>With the buy-only form of vanilla, a base that sells a ship after the change gets a buy-only line for
    /// each ship package without a line. The fix names them.</summary>
    [Fact]
    public void WithTheBuyOnlyFormABaseThatSellsAShipGetsALineForEachShipPackage()
    {
        using var ws = BaseSaveGateTests.ShipWorkspace(BuyOnlyShips);
        var session = ws.Session("act");

        var plan = Run(session, BaseWorkspace.Station, [new MarketRow("ol_ship_package")]);

        Assert.Equal(["market_misc.ini: 0, -1, 1, 1, 0, 1, 1"], Lines(session, BaseWorkspace.Station, "ol_ship_package"));
        Assert.Equal(["market_misc.ini: 0, -1, 0, 0, 1, 1, 1"], Lines(session, BaseWorkspace.Station, "ol_ship2_package"));
        Assert.Equal(["market_misc.ini: 0, -1, 0, 0, 1, 1, 1"], Lines(session, BaseWorkspace.Station, "ol_ship3_package"));
        var fix = Assert.Single(plan.Fixes);
        Assert.Equal("Ol01_01_Base sells ships, so it gets a buy-only line for 2 ship packages: ol_ship2_package, " +
                     "ol_ship3_package. The ship markets of the game list each ship package at each base that sells ships.",
            fix.Text);
        Assert.Equal(Assert.Single(plan.Adds), fix.Section);
        Assert.Empty(MarketPlans.MissingShipLines(session, session.Base(BaseWorkspace.Station)!));
    }

    /// <summary>Without buy-only ship lines in the data of the game, as HD Edition 0.7.1 has, the base lists only the
    /// ships that it sells.</summary>
    [Fact]
    public void WithoutTheBuyOnlyFormTheBaseGetsNoShipLines()
    {
        using var ws = BaseSaveGateTests.ShipWorkspace(SoldShipsOnly);
        var session = ws.Session("act");

        var plan = Run(session, BaseWorkspace.Station, [new MarketRow("ol_ship_package")]);

        Assert.Empty(plan.Fixes);
        Assert.Empty(Lines(session, BaseWorkspace.Station, "ol_ship2_package"));
    }

    /// <summary>The fix of the completeness report adds the missing ship lines of a market that the active mod
    /// changes.</summary>
    [Fact]
    public void TheMarketStepOffersTheShipLinesAndTheFixAddsThem()
    {
        using var ws = BaseSaveGateTests.ShipWorkspace(BuyOnlyShips);
        var session = ws.Session("act");
        // A line of the Inspector sells the ship, without the ship lines that a market change adds.
        session.Execute(MarketPlans.AddToBase(session, "ol_ship_package", BaseWorkspace.Station,
            MarketPlans.DefaultValues("ship")));

        var report = BaseCompleteness.Of(session, BaseWorkspace.Station)!;
        var market = report.Steps.Single(s => s.Name == "Market");
        Assert.Equal((CompletenessState.Missing, BaseCompleteness.ShipLinesFix), (market.State, market.Fix));

        var fix = MarketPlans.ShipLines(session, BaseWorkspace.Station);
        Assert.True(fix.CanRun);
        AssertSte(fix);
        session.Execute(fix.Command!);
        Assert.Equal("Add the ship lines of Ol01_01_Base", session.UndoLabel);
        Assert.Empty(MarketPlans.MissingShipLines(session, session.Base(BaseWorkspace.Station)!));
        Assert.Null(BaseCompleteness.Of(session, BaseWorkspace.Station)!.Steps.Single(s => s.Name == "Market").Fix);
        Assert.False(MarketPlans.ShipLines(session, BaseWorkspace.Station).CanRun);
    }

    public static TheoryData<IReadOnlyList<MarketRow>, IReadOnlyList<string>, string?, MarketPriceScale?, string, string?,
        string> BadRequests => new()
    {
        { [new MarketRow("nothing")], [], null, null, MissionErrors.ItemNotFound, "goods[0].good",
            "No [Good] of the goods files has the nickname nothing. Get the goods from list_values with the list good." },
        { [new MarketRow("ol_gun"), new MarketRow("OL_GUN")], [], null, null, MissionErrors.FieldValue, "goods[1].good",
            "The good OL_GUN is in more than one row. Give each good one row." },
        { [new MarketRow("ol_gun")], ["ol_gun"], null, null, MissionErrors.FieldValue, "remove[0]",
            "The good ol_gun is in a row and in the goods to remove. Give it only one place." },
        { [new MarketRow("ol_gun") { Reputation = 2 }], [], null, null, MissionErrors.FieldValue, "goods[0].reputation",
            "The reputation must be a number from -1 to 1." },
        { [new MarketRow("ol_gun") { MaxStock = -1 }], [], null, null, MissionErrors.FieldValue, "goods[0].max_stock",
            "A stock value must be 0 or more." },
        { [], [], "Ol09_09_Base", null, MissionErrors.ItemNotFound, "copy_from",
            "No [Base] of universe.ini has the nickname Ol09_09_Base. Get the bases from list_bases." },
        { [], [], null, new MarketPriceScale(0), MissionErrors.FieldValue, "price_scale.factor",
            "The price factor must be a number above 0." },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public void ABadRequestIsRefused(IReadOnlyList<MarketRow> rows, IReadOnlyList<string> remove, string? copyFrom,
        MarketPriceScale? scale, string detail, string? part, string message)
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var plan = MarketPlans.Set(session, BaseWorkspace.Station, rows, remove, copyFrom, false, scale);

        Assert.False(plan.CanRun);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal((detail, part, message), (refusal.Rule, plan.RefusedPart, refusal.Message));
        Assert.Empty(SteLint.Problems(message));
    }

    /// <summary>A market file that the active mod replaces with its own file takes no edits, so a change of its lines
    /// refuses with the reason.</summary>
    [Fact]
    public void AMarketFileThatTakesNoEditsRefusesTheChange()
    {
        using var ws = BaseWorkspace.Create(new Dictionary<string, string>
        {
            [@"files\" + BaseWorkspace.MiscMarketPath] = "[BaseGood]\nbase = Ol01_01_Base\nmarketgood = ol_gun, 0, -1, 10, 10, 0, 1\n",
        });
        var session = ws.Session("act");
        var line = session.Items.BaseMarketLines(BaseWorkspace.Station).Single(l => l.Values[0].ToString() == "ol_gun");
        Assert.False(MarketPlans.CanChange(session, line));

        var plan = MarketPlans.Set(session, BaseWorkspace.Station, [new MarketRow("ol_gun") { PriceMultiplier = 2 }], []);

        Assert.False(plan.CanRun);
        Assert.StartsWith("market_misc.ini: ", Assert.Single(plan.Refusals).Message);
    }

    [Fact]
    public void AnUnknownBaseIsRefused()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var plan = MarketPlans.Set(session, "Ol09_09_Base", [new MarketRow("ol_gun")], []);

        Assert.Equal((MissionErrors.ItemNotFound, "base"), (Assert.Single(plan.Refusals).Rule, plan.RefusedPart));
    }

    /// <summary>A change that writes a line and deletes a section of the same file gives the final references of the
    /// sections, and one Undo restores each market file.</summary>
    [Fact]
    public void OneUndoRestoresEachMarketFile()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        Run(session, BaseWorkspace.Station, [new MarketRow("ol_ship_package")]);
        string[] paths = [BaseWorkspace.MiscMarketPath, BaseWorkspace.ShipMarketPath, BaseWorkspace.CommodityMarketPath];
        string Dump() => string.Join("\n", paths.SelectMany(p => session.Sections(p).Select(s =>
            $"{s}: {string.Join("; ", session.Get(s)!.Lines("marketgood").Select(l => string.Join(", ", l.Values)))}")));
        var before = Dump();

        var plan = Run(session, BaseWorkspace.Station, [new MarketRow("ol_water") { PriceMultiplier = 3 }],
            ["ol_ship_package"]);

        var deleted = Assert.Single(plan.Deletes);
        Assert.Equal(BaseWorkspace.MiscMarketPath, deleted.Section.Path, ignoreCase: true);
        var added = Assert.Single(plan.Adds);
        Assert.Equal(BaseWorkspace.CommodityMarketPath, added.Path, ignoreCase: true);
        Assert.Equal("ol_water", session.Get(added)!.Lines("marketgood").Single().Values[0].ToString());
        session.Undo();
        Assert.Equal(before, Dump());
    }
}
