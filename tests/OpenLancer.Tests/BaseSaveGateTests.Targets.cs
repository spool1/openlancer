using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The base checks of the retail targets on the merged data of a target game, the gate and target levels of one fault in
/// a session, and the completeness report of a base.
/// </summary>
public sealed partial class BaseSaveGateTests
{
    private const string TargetUniverse = @"DATA\UNIVERSE\universe.ini";
    private const string TargetMBases = @"DATA\MISSIONS\mBases.ini";
    private const string TargetDeck = @"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ROOMS\ol01_01_deck.ini";

    /// <summary>Two names of bases with one FLHash.</summary>
    private const string HashA = "ol_hash_fialaeodje";
    private const string HashB = "ol_hash_iehbialbib";

    /// <summary>The ship market of a target game with buy-only lines, as vanilla has them.</summary>
    private const string BuyOnlyShips = """
        [BaseGood]
        base = Ol01_01_Base
        marketgood = ol_p1, 0, -1, 1, 1, 0, 1, 1
        marketgood = ol_p2, 0, -1, 0, 0, 1, 1, 1
        marketgood = ol_p3, 0, -1, 0, 0, 1, 1, 1
        """;

    /// <summary>The ship market of a target game without buy-only lines, as HD Edition 0.7.1 has it.</summary>
    private const string SoldShipsOnly = """
        [BaseGood]
        base = Ol01_01_Base
        marketgood = ol_p1, 0, -1, 1, 1, 0, 1, 1
        """;

    /// <summary>A target game with one station base, its two rooms, its bar data, three ship packages and a ship
    /// market. The data is made up.</summary>
    private static ExportGame TargetGame(string shipMarket = BuyOnlyShips, string extraBases = "")
    {
        var game = new ExportGame();
        game.Game.Write(TargetUniverse, $"""
            [Base]
            nickname = Ol01_01_Base
            system = Ol01
            strid_name = 1001
            file = Universe\Systems\Ol01\Bases\ol01_01_base.ini
            {extraBases}
            [system]
            nickname = Ol01
            file = systems\Ol01\ol01.ini
            """);
        game.Game.Write(@"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ol01_01_base.ini", """
            [BaseInfo]
            nickname = Ol01_01_Base
            start_room = Deck
            [Room]
            nickname = Deck
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_01_deck.ini
            [Room]
            nickname = Bar
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_01_bar.ini
            """);
        game.Game.Write(TargetDeck, "[Hotspot]\nname = IDS_HOTSPOT_BAR\nbehavior = ExitDoor\nroom_switch = Bar\n");
        game.Game.Write(@"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ROOMS\ol01_01_bar.ini",
            "[Hotspot]\nname = IDS_HOTSPOT_DECK\nbehavior = ExitDoor\nroom_switch = Deck\n");
        game.Game.Write(TargetMBases, """
            [MBase]
            nickname = Ol01_01_Base
            local_faction = ol_grp
            [GF_NPC]
            nickname = ol01_01_bartender
            body = ol_body
            voice = ol_voice
            individual_name = 1100
            [MRoom]
            nickname = Bar
            fixture = ol01_01_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_fidget.thn, bartender
            """);
        game.Game.Write(ExportGame.GoodsPath, """
            [Good]
            nickname = ol_p1
            category = ship
            [Good]
            nickname = ol_p2
            category = ship
            [Good]
            nickname = ol_p3
            category = ship
            """);
        game.Game.Write(ExportGame.MarketPath, shipMarket);
        return game;
    }

    /// <summary>The patch of <c>universe.ini</c> that adds a base of the mod with its own base file.</summary>
    private static string NewTargetBase(string nickname = "Ol01_02_Base", string modId = "m") =>
        $"[Base]\nol.new = true\nnickname = {nickname}\nsystem = Ol01\nstrid_name = 1002\n" +
        $"file = MODS\\{modId}\\BASES\\{nickname}.ini\n";

    /// <summary>The base file of a base of the mod, with the deck of Ol01_01_Base, and with its bar unless
    /// <paramref name="bar"/> is false.</summary>
    private static string TargetBaseFile(string nickname = "Ol01_02_Base", bool bar = true) =>
        $"[BaseInfo]\nnickname = {nickname}\nstart_room = Deck\n" +
        "[Room]\nnickname = Deck\nfile = Universe\\Systems\\Ol01\\Bases\\Rooms\\ol01_01_deck.ini\n" +
        (bar ? "[Room]\nnickname = Bar\nfile = Universe\\Systems\\Ol01\\Bases\\Rooms\\ol01_01_bar.ini\n" : "");

    /// <summary>The findings of the target checks of a mod, merged on a game. The merge gives no error.</summary>
    private static IReadOnlyList<Diagnostic> TargetFindings(ExportGame game, Dictionary<string, string> files)
    {
        var target = game.Build(game.Mod("m", files));
        Assert.DoesNotContain(target.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var findings = TargetChecks.Run(target);
        Assert.All(findings, f => Assert.Empty(SteLint.Problems(f.Message)));
        Assert.All(findings, f => Assert.DoesNotMatch(CheckLabel(), f.Message));
        return findings;
    }

    private static Dictionary<string, string> NewBaseMod(bool bar = true) => new()
    {
        [@"patches\" + TargetUniverse] = NewTargetBase(),
        [@"files\DATA\MODS\m\BASES\Ol01_02_Base.ini"] = TargetBaseFile(bar: bar),
    };

    /// <summary>A new base with the shared rooms of the target is valid data, and the target checks give no
    /// finding.</summary>
    [Fact]
    public void ANewBaseOfAModGivesNoTargetFinding()
    {
        using var game = TargetGame();

        Assert.Empty(TargetFindings(game, NewBaseMod()));
    }

    /// <summary>A door to a room that the base does not have is an error of the target checks: the game can
    /// crash.</summary>
    [Fact]
    public void ADoorToARoomThatTheBaseDoesNotHaveIsATargetError()
    {
        using var game = TargetGame();

        var error = Assert.Single(TargetFindings(game, NewBaseMod(bar: false)));

        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("Base 'Ol01_02_Base': the door IDS_HOTSPOT_BAR in the room Deck leads to Bar, but the base has no room " +
                     "Bar. The game can crash when the player uses the door.", error.Message);
        Assert.Equal(("Room", "file"), (error.Target!.Id.Type, error.Key));
    }

    /// <summary>A new door in a room file of the target game checks each base that uses the room file.</summary>
    [Fact]
    public void ANewDoorOfARoomFileIsCheckedForTheBasesOfTheFile()
    {
        using var game = TargetGame();

        var error = Assert.Single(TargetFindings(game, new()
        {
            [@"files\" + TargetDeck] = "[Hotspot]\nname = IDS_HOTSPOT_BAR\nbehavior = ExitDoor\nroom_switch = Bar\n" +
                                       "[Hotspot]\nname = IDS_HOTSPOT_OFFICE\nbehavior = ExitDoor\nroom_switch = Office\n",
        }));

        Assert.StartsWith("Base 'Ol01_01_Base': the door IDS_HOTSPOT_OFFICE in the room Deck leads to Office", error.Message);
    }

    /// <summary>A problem that the target game has gives no finding, because the checks report only what the mods
    /// add.</summary>
    [Fact]
    public void ADoorProblemOfTheTargetGameGivesNoFinding()
    {
        using var game = TargetGame();
        game.Game.Write(TargetDeck, "[Hotspot]\nname = IDS_HOTSPOT_OFFICE\nbehavior = ExitDoor\nroom_switch = Office\n");

        Assert.Empty(TargetFindings(game, new()
        {
            [@"patches\" + TargetUniverse] = "[Base]\nnickname = Ol01_01_Base\nstrid_name = 1009\n",
        }));
    }

    [Fact]
    public void AnMBaseThatNamesNoBaseIsATargetError()
    {
        using var game = TargetGame();

        var error = Assert.Single(TargetFindings(game, new()
        {
            [@"patches\" + TargetMBases] = "[MBase]\nol.new = true\nnickname = no_base\nlocal_faction = ol_grp\n",
        }));

        Assert.Equal("[MBase] no_base: no base has this nickname. The game stops reading the bar data of the bases after " +
                     "this block.", error.Message);
        Assert.Equal((DiagnosticSeverity.Error, "MBase", "nickname"), (error.Severity, error.Target!.Id.Type, error.Key));
    }

    /// <summary>
    /// Final finding of V1-C4: the bar data checks also run when the mods change only <c>universe.ini</c>. A mod that
    /// deletes a base leaves the <c>[MBase]</c> of the base without a base, and the game then stops reading the bar data
    /// of all bases after that block.
    /// </summary>
    [Fact]
    public void DeletingABaseGivesTheBarDataErrorOfItsMBase()
    {
        using var game = TargetGame();

        var findings = TargetFindings(game, new Dictionary<string, string>
        {
            [@"patches\" + TargetUniverse] = "[Base]\nnickname = Ol01_01_Base\nol.delete = true\n",
        });

        Assert.Contains(findings, f => f.Severity == DiagnosticSeverity.Error &&
                                       f.Message.StartsWith("[MBase] Ol01_01_Base: no base has this nickname."));
    }

    [Fact]
    public void TheFirstLineOfAnMBaseIsATargetError()
    {
        using var game = TargetGame();
        var files = NewBaseMod();
        files[@"patches\" + TargetMBases] = "[MBase]\nol.new = true\nlocal_faction = ol_grp\nnickname = Ol01_02_Base\n";

        var error = Assert.Single(TargetFindings(game, files));

        Assert.Equal("[MBase] Ol01_02_Base: the first line is not the nickname line. The game reads the first line as the " +
                     "nickname. Put the nickname line first.", error.Message);
    }

    [Fact]
    public void AFixtureBeforeItsCharacterIsATargetError()
    {
        using var game = TargetGame();
        var files = NewBaseMod();
        files[@"patches\" + TargetMBases] = """
            [MBase]
            ol.new = true
            nickname = Ol01_02_Base
            local_faction = ol_grp

            [MRoom]
            nickname = Bar
            fixture = ol01_02_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_fidget.thn, bartender

            [GF_NPC]
            nickname = ol01_02_bartender
            body = ol_body
            voice = ol_voice
            individual_name = 1100
            """;

        var error = Assert.Single(TargetFindings(game, files));

        Assert.Equal("[MBase] Ol01_02_Base: the fixture ol01_02_bartender in the room Bar names no [GF_NPC] before it. The " +
                     "game shows no character at this place.", error.Message);
        Assert.Equal(("MRoom", "fixture"), (error.Target!.Id.Type, error.Key));
    }

    /// <summary>A fixture that names a character of an earlier <c>[MBase]</c> is a target error. The game finds the
    /// character of a fixture only in the same block.</summary>
    [Fact]
    public void AFixtureWithACharacterOfAnotherMBaseIsATargetError()
    {
        using var game = TargetGame();
        var files = NewBaseMod();
        files[@"patches\" + TargetMBases] = """
            [MBase]
            ol.new = true
            nickname = Ol01_02_Base
            local_faction = ol_grp

            [MRoom]
            nickname = Bar
            fixture = ol01_01_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_fidget.thn, bartender
            """;

        var error = Assert.Single(TargetFindings(game, files));

        Assert.Equal("[MBase] Ol01_02_Base: the fixture ol01_01_bartender in the room Bar names no [GF_NPC] before it. The " +
                     "game shows no character at this place.", error.Message);
        Assert.Equal((DiagnosticSeverity.Error, "MRoom"), (error.Severity, error.Target!.Id.Type));
    }

    [Fact]
    public void ANicknameOfMoreThan63CharactersIsATargetError()
    {
        using var game = TargetGame();
        var name = "Ol01_" + new string('x', 59);

        var errors = TargetFindings(game, new()
        {
            [@"patches\" + TargetUniverse] = $"[Base]\nol.new = true\nnickname = {name}\nsystem = Ol01\n",
        });

        var error = Assert.Single(errors);
        Assert.Equal($"Base '{name}': the nickname has more than 63 characters. The game then ignores the bar data of the " +
                     "base. Give the base a shorter nickname.", error.Message);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    /// <summary>Two nicknames with one FLHash give one base id. The game of the target has the first one, and the mod
    /// adds the second one.</summary>
    [Fact]
    public void ABaseWithTheIdOfABaseOfTheTargetGameIsATargetError()
    {
        Assert.Equal(FLHash.CreateID(HashA), FLHash.CreateID(HashB));
        using var game = TargetGame(extraBases: $"[Base]\nnickname = {HashA}\nsystem = Ol01\n");

        var error = Assert.Single(TargetFindings(game, new()
        {
            [@"patches\" + TargetUniverse] = $"[Base]\nol.new = true\nnickname = {HashB}\nsystem = Ol01\n",
        }));

        Assert.Equal($"Base '{HashB}': the game already has the base '{HashA}' with the same nickname or the same id. The " +
                     "game drops one of the two bases. Give the base another nickname.", error.Message);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    /// <summary>A new base that sells one ship and lists no other ship gets the warning of the ship lines on a target whose ship
    /// markets have buy-only lines (vanilla), also when the mod was made on data without them (HD Edition). A target
    /// without buy-only lines gives no warning.</summary>
    [Theory]
    [InlineData(BuyOnlyShips, true)]
    [InlineData(SoldShipsOnly, false)]
    public void TheShipLinesFollowTheShipMarketsOfTheTarget(string shipMarket, bool warns)
    {
        using var game = TargetGame(shipMarket);
        var files = NewBaseMod();
        files[@"patches\" + ExportGame.MarketPath] =
            "[BaseGood]\nol.new = true\nbase = Ol01_02_Base\nmarketgood = ol_p1, 0, -1, 1, 1, 0, 1, 1\n";

        var findings = TargetFindings(game, files);

        if (!warns)
        {
            Assert.Empty(findings);
            return;
        }
        var warning = Assert.Single(findings);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("Base 'Ol01_02_Base': the base sells ships, but its market has no line for 2 ship packages: ol_p2, " +
                     "ol_p3. The ship markets of this game list each ship package at each base that sells ships. Add " +
                     "these lines only to match that form. They do not change the price or the trade-in of a ship.",
            warning.Message);
    }

    /// <summary>A new ship package of a mod gives no finding of the ship lines for the bases whose markets the mod does not
    /// change.</summary>
    [Fact]
    public void ANewShipPackageGivesNoShipLinesFindingForTheBasesOfTheGame()
    {
        using var game = TargetGame();

        Assert.Empty(TargetFindings(game, new()
        {
            [@"patches\" + ExportGame.GoodsPath] = "[Good]\nol.new = true\nnickname = ol_p4\ncategory = ship\n",
        }));
    }

    /// <summary>
    /// One fault in a session: a door of a new base to a room that the base does not have. For a mod without a retail
    /// target, it is a warning of the base checks, and the save continues. For a data mod with a retail target, the
    /// target checks of the save gate give the error, and the save stops.
    /// </summary>
    [Theory]
    [InlineData("""{ "id": "act", "formatVersion": 2, "tier": 2, "targets": [ "openlancer" ] }""", false)]
    [InlineData("""{ "id": "act", "formatVersion": 2, "tier": 1, "targets": [ "vanilla-1.1" ] }""", true)]
    public void ADoorToAMissingRoomIsATargetErrorOnlyForARetailTarget(string manifest, bool blocks)
    {
        using var ws = BaseWorkspace.Create();
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), ModManifest.FileName), manifest);
        var session = ws.Session("act");
        session.Execute(EditCommand.Group("New base",
            [NewBase(), NewBaseFile(StationRooms[..StationRooms.LastIndexOf("[Room]")])]));

        Assert.Equal(DiagnosticSeverity.Warning, One(session, "The door").Severity);
        var target = session.SaveProblems()
            .Where(p => p.Message.StartsWith("Base 'Ol01_03_Base': the door", StringComparison.Ordinal)).ToList();
        if (blocks)
            Assert.Equal(DiagnosticSeverity.Error, Assert.Single(target).Severity);
        else
            Assert.Empty(target);
    }

    /// <summary>A base of the fixture has all seven steps.</summary>
    [Fact]
    public void TheReportOfAValidBaseIsComplete()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var report = BaseCompleteness.Of(session, BaseWorkspace.Station)!;

        Assert.Equal(["Universe entry", "Base file", "Start room", "Rooms", "Characters", "Market", "Dock object"],
            report.Steps.Select(s => s.Name));
        Assert.True(report.Complete);
        Assert.Equal("All 7 steps complete.", report.Summary);
        Assert.All(report.Steps, s => Assert.Empty(SteLint.Problems(s.Reason)));
        Assert.Null(BaseCompleteness.Of(session, "no_such_base"));
    }

    /// <summary>A new station without an <c>[MBase]</c>, a market and a dock object misses three steps. Each missing step
    /// tells what is missing and goes to its place: the dock object step goes to the system.</summary>
    [Fact]
    public void TheReportOfANewStationNamesItsMissingSteps()
    {
        using var ws = BaseWorkspace.Create();
        var session = WithNewStation(ws);

        var report = BaseCompleteness.Of(session, New)!;

        Assert.Equal("4 of 7 steps complete", report.Summary);
        Assert.Equal(["Characters", "Market", "Dock object"], report.Missing.Select(s => s.Name));
        var dock = report.Missing.Last();
        Assert.Equal("No object docks with Ol01_03_Base. The player cannot dock there.", dock.Detail);
        Assert.Equal(session.SystemSection("Ol01"), dock.GoTo);
        Assert.Equal("Ol01_03_Base has no [MBase]. Its bar has no characters, and it offers no missions.",
            report.Missing.First().Detail);
        Assert.False(report.HasError);
    }

    /// <summary>A missing base file is an error of the step Base file. The start room step misses its
    /// <c>[BaseInfo]</c>.</summary>
    [Fact]
    public void TheReportShowsTheErrorOfAMissingBaseFile()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(NewBase());

        var report = BaseCompleteness.Of(session, New)!;

        var file = report.Steps.Single(s => s.Name == "Base file");
        Assert.Equal((CompletenessState.Missing, true), (file.State, file.IsError));
        Assert.StartsWith("The base file MODS\\act\\BASES\\ol01_03_base.ini of Ol01_03_Base does not exist.", file.Detail);
        Assert.Equal(CompletenessState.Missing, report.Steps.Single(s => s.Name == "Start room").State);
        Assert.True(report.HasError);
    }

    /// <summary>The planet of the base game has no <c>[MBase]</c>. The base game misses the step too, so it counts as
    /// done and hides.</summary>
    [Fact]
    public void AStepThatTheBaseGameMissesIsHidden()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var report = BaseCompleteness.Of(session, BaseWorkspace.Planet)!;

        Assert.Equal(CompletenessState.Hidden, report.Steps.Single(s => s.Name == "Characters").State);
        Assert.True(report.Complete);
        Assert.Equal(1, report.HiddenCount);
    }
}
