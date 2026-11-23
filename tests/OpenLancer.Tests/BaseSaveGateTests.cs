using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Validation;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The base checks of the save gate and of the retail targets (section D of the bases design). Each test triggers one
/// check on the made-up data of <see cref="BaseWorkspace"/>. An error comes only from a line or a file of the active mod,
/// and the same problem of the base game is a warning. The checks of the targets read the merged data of a target
/// game.
/// </summary>
public sealed partial class BaseSaveGateTests
{
    private const string New = "Ol01_03_Base";
    private const string NewFile = @"DATA\MODS\act\BASES\ol01_03_base.ini";
    private const string NewFileValue = @"MODS\act\BASES\ol01_03_base.ini";

    /// <summary>The rooms of the station Ol01_01_Base: the deck with two dealers, and the shared bar.</summary>
    private const string StationRooms = """
        [BaseInfo]
        nickname = Ol01_03_Base
        start_room = Deck
        [Room]
        nickname = Deck
        file = Universe\Systems\Ol01\Bases\Rooms\ol01_01_deck.ini
        [Room]
        nickname = Bar
        file = Universe\Systems\Ol01\Bases\Rooms\ol_shared_bar.ini
        """;

    /// <summary>A new <c>[Base]</c> of the active mod in Ol01, with the name 1004.</summary>
    private static EditCommand NewBase(string nickname = New, string? file = NewFileValue, params EntryView[] more) =>
        EditCommand.AddSection(BaseWorkspace.UniversePath, "Base",
        [
            ListWorkspace.Line("nickname", nickname),
            ListWorkspace.Line("system", "Ol01"),
            ListWorkspace.Line("strid_name", "1004"),
            .. file == null ? Array.Empty<EntryView>() : [ListWorkspace.Line("file", file)],
            .. more,
        ]);

    /// <summary>The base file of the new base, as a new file of the active mod.</summary>
    private static EditCommand NewBaseFile(string text = StationRooms) =>
        EditCommand.NewFile(NewFile, ContentPathsTests.Sections(text));

    /// <summary>A session with the new station of the active mod: its <c>[Base]</c> and its base file with the rooms of
    /// Ol01_01_Base.</summary>
    private static EditSession WithNewStation(TestWorkspace ws, params EntryView[] more)
    {
        var session = ws.Session("act");
        session.Execute(EditCommand.Group("New base", [NewBase(more: more), NewBaseFile()]));
        return session;
    }

    private static SectionRef Entry(EditSession session, string nickname = New) =>
        BaseWorkspace.Section(session, BaseWorkspace.UniversePath, "Base", nickname);

    private static IReadOnlyList<Diagnostic> Problems(EditSession session, string nickname = New) =>
        BaseChecks.Problems(session, nickname);

    /// <summary>The one problem of a base whose message starts with a text.</summary>
    private static Diagnostic One(EditSession session, string start, string nickname = New)
    {
        var problem = Assert.Single(Problems(session, nickname), p => p.Message.StartsWith(start, StringComparison.Ordinal));
        AssertText(problem);
        return problem;
    }

    /// <summary>The text is STE and names no check label.</summary>
    private static void AssertText(Diagnostic problem)
    {
        Assert.Empty(SteLint.Problems(problem.Message));
        Assert.DoesNotMatch(CheckLabel(), problem.Message);
    }

    [GeneratedRegex(@"\b(BA|B)\d{2}\b")]
    private static partial Regex CheckLabel();

    /// <summary>The fixture is valid data: no base has a problem, the save gate finds nothing, and each base of the
    /// retail form has a complete report.</summary>
    [Fact]
    public void TheBasesOfTheFixtureHaveNoProblem()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        foreach (var nickname in new[] { BaseWorkspace.Station, BaseWorkspace.Planet, BaseWorkspace.Pirate })
            Assert.Empty(Problems(session, nickname));
        Assert.Empty(session.SaveProblems());
        Assert.Empty(Problems(session, "no_such_base"));
    }

    [Fact]
    public void ABaseWithoutABaseFileIsAnError()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(NewBase(file: null));
        var error = One(session, "The base Ol01_03_Base names no base file.");
        Assert.Equal((DiagnosticSeverity.Error, Entry(session), "file"), (error.Severity, error.Target, error.Key));
        Assert.Equal("The base Ol01_03_Base names no base file. The game can crash when the player docks here.", error.Message);
        Assert.Contains(session.SaveProblems(), p => p.Message == error.Message);

        session.Undo();
        session.Execute(NewBase());
        error = One(session, "The base file");
        Assert.Equal("The base file MODS\\act\\BASES\\ol01_03_base.ini of Ol01_03_Base does not exist. The game can crash " +
                     "when the player docks here.", error.Message);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.False(session.Save().Saved);

        session.Execute(NewBaseFile());
        Assert.DoesNotContain(Problems(session), p => p.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>A base of the base game without its base file gives a warning only, also when the active mod changes
    /// another line of the base. The active mod does not cause the problem.</summary>
    [Fact]
    public void TheSameProblemOfTheBaseGameIsAWarning()
    {
        using var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.UniversePath, BaseWorkspace.Universe.Replace("[system]\nnickname = Ol01\n",
            "[Base]\nnickname = Ol01_09_Base\nsystem = Ol01\nfile = Universe\\Systems\\Ol01\\Bases\\missing.ini\n" +
            "[system]\nnickname = Ol01\n"));
        var session = ws.Session("act");

        var warning = One(session, "The base file", "Ol01_09_Base");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        session.Execute(EditCommand.SetLinesFromText(Entry(session, "Ol01_09_Base"), "strid_name", ["1009"]));
        Assert.Equal(DiagnosticSeverity.Warning, One(session, "The base file", "Ol01_09_Base").Severity);
        Assert.DoesNotContain(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
        // A file line of the active mod causes the problem.
        session.Execute(EditCommand.SetLinesFromText(Entry(session, "Ol01_09_Base"), "file",
            [@"Universe\Systems\Ol01\Bases\missing_2.ini"]));
        Assert.Equal(DiagnosticSeverity.Error, One(session, "The base file", "Ol01_09_Base").Severity);
    }

    [Fact]
    public void ABaseFileWithoutBaseInfoIsAnError()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.Group("New base", [NewBase(), NewBaseFile(StationRooms[StationRooms.IndexOf("[Room]")..])]));

        var error = One(session, "The base file MODS\\act\\BASES\\ol01_03_base.ini of Ol01_03_Base has no [BaseInfo]");
        Assert.Equal((DiagnosticSeverity.Error, "file"), (error.Severity, error.Key));
        Assert.Contains(session.SaveProblems(), p => p.Message == error.Message);
    }

    /// <summary>The list rule of <c>start_room</c> gives the error of a start room that the base does not have. The base
    /// checks add no second problem.</summary>
    [Fact]
    public void AStartRoomThatTheBaseDoesNotHaveIsAnErrorOfTheListRule()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.Group("New base",
            [NewBase(), NewBaseFile(StationRooms.Replace("start_room = Deck", "start_room = Cityscape"))]));

        var info = BaseWorkspace.Section(session, NewFile, "BaseInfo");
        Assert.Contains(session.SaveProblems(), p => p.Target == info && p.Key == "start_room");
        Assert.DoesNotContain(Problems(session), p => p.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ARoomWithoutANicknameOrARoomFileIsAnError()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.Group("New base", [NewBase(), NewBaseFile(StationRooms +
            "\n[Room]\nfile = Universe\\Systems\\Ol01\\Bases\\Rooms\\ol_shared_bar.ini\n[Room]\nnickname = Office\n")]));

        var noName = One(session, "A [Room] of the base file of Ol01_03_Base has no nickname.");
        var noFile = One(session, "The room Office of Ol01_03_Base names no room file.");

        Assert.Equal((DiagnosticSeverity.Error, "nickname"), (noName.Severity, noName.Key));
        Assert.Equal((DiagnosticSeverity.Error, "file"), (noFile.Severity, noFile.Key));
        Assert.Equal("The room Office of Ol01_03_Base names no room file. The game keeps a broken room, and it can crash.",
            noFile.Message);
        Assert.Equal("Room", noFile.Target!.Id.Type);
        var errors = session.SaveProblems();
        Assert.Contains(errors, p => p.Message == noName.Message);
        Assert.Contains(errors, p => p.Message == noFile.Message);
    }

    [Fact]
    public void ARoomFileThatDoesNotExistIsAnError()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.Group("New base", [NewBase(), NewBaseFile(StationRooms +
            "\n[Room]\nnickname = Office\nfile = Universe\\Systems\\Ol01\\Bases\\Rooms\\missing.ini\n")]));

        var error = One(session, "The room file");

        Assert.Equal("The room file Universe\\Systems\\Ol01\\Bases\\Rooms\\missing.ini of the room Office of Ol01_03_Base " +
                     "does not exist. The game can crash when the player enters this room.", error.Message);
        Assert.Equal((DiagnosticSeverity.Error, BaseWorkspace.Section(session, NewFile, "Room", "Office"), "file"),
            (error.Severity, error.Target, error.Key));
        Assert.Contains(session.SaveProblems(), p => p.Message == error.Message);
        // The section check of the [Room] shows it, and the [Base] does not.
        Assert.Contains(ComponentChecks.Check(session, error.Target!), p => p.Message == error.Message);
        Assert.DoesNotContain(ComponentChecks.Check(session, Entry(session)), p => p.Message == error.Message);
    }

    /// <summary>A door to a room that the base does not have is a warning of the gate. A door without
    /// <c>room_switch</c> gives no finding.</summary>
    [Fact]
    public void ADoorToARoomThatTheBaseDoesNotHaveIsAWarning()
    {
        using var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.BarPath, """
            [Room_Info]
            set_script = Scripts\Bases\ol_bar_set.thn
            [Hotspot]
            name = IDS_HOTSPOT_DECK
            behavior = ExitDoor
            room_switch = Deck
            [Hotspot]
            name = IDS_HOTSPOT_EXIT
            behavior = ExitDoor
            """);
        var session = ws.Session("act");
        session.Execute(EditCommand.Group("New base",
            [NewBase(), NewBaseFile(StationRooms[..StationRooms.LastIndexOf("[Room]")])]));

        var warning = One(session, "The door");

        Assert.Equal("The door IDS_HOTSPOT_BAR in the room Deck of Ol01_03_Base leads to Bar, but Ol01_03_Base has no room " +
                     "Bar. The retail game can crash when the player uses the door.", warning.Message);
        Assert.Equal((DiagnosticSeverity.Warning, BaseWorkspace.Section(session, NewFile, "Room", "Deck")),
            (warning.Severity, warning.Target));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message == warning.Message);
        Assert.Empty(Problems(session, BaseWorkspace.Station));
    }

    /// <summary>An <c>[MBase]</c> of the active mod that names no base is an error of the closed list rule (the fork
    /// does not use the block). The base checks add no problem for it.</summary>
    [Fact]
    public void AnMBaseThatNamesNoBaseIsAnErrorOfTheListRule()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(BaseWorkspace.MBasesPath, "MBase", [ListWorkspace.Line("nickname", "no_base")]));
        var mbase = BaseWorkspace.Section(session, BaseWorkspace.MBasesPath, "MBase", "no_base");

        Assert.Contains(session.SaveProblems(), p => p.Target == mbase && p.Key == "nickname");
        Assert.Empty(BaseChecks.Check(session, mbase));
    }

    [Fact]
    public void TheFirstLineOfAnMBaseMustBeItsNickname()
    {
        using var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.MBasesPath, BaseWorkspace.MBases.Replace(
            "nickname = Ol02_01_Base\nlocal_faction = ol_pirates", "local_faction = ol_pirates\nnickname = Ol02_01_Base"));
        var session = ws.Session("act");

        var warning = One(session, "The first line of [MBase]", BaseWorkspace.Pirate);

        Assert.Equal("The first line of [MBase] Ol02_01_Base must be its nickname line. The retail game reads the first " +
                     "line as the nickname.", warning.Message);
        Assert.Equal((DiagnosticSeverity.Warning, "MBase", "nickname"),
            (warning.Severity, warning.Target!.Id.Type, warning.Key));
        Assert.Contains(ComponentChecks.Check(session, warning.Target, onlyActiveMod: false), p => p.Message == warning.Message);
    }

    [Fact]
    public void ALongNicknameAndTheIntroPrefixAreWarnings()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var long64 = "Ol01_" + new string('x', 59);
        var long32 = "Ol01_" + new string('y', 27);

        session.Execute(NewBase(long64, file: null));
        session.Execute(NewBase(long32, file: null));
        session.Execute(NewBase("intro9_base", file: null));

        Assert.Equal(DiagnosticSeverity.Warning, One(session, $"The base nickname {long64} has more than 63", long64).Severity);
        Assert.Equal(DiagnosticSeverity.Warning, One(session, $"The base nickname {long64} has more than 31", long64).Severity);
        Assert.Equal($"The base nickname {long32} has more than 31 characters. A mission condition cannot name this base.",
            One(session, "The base nickname", long32).Message);
        Assert.Equal("The base nickname intro9_base starts with 'intro'. The OpenLancer engine also shows the rooms of the " +
                     "base in the main menu.", One(session, "The base nickname", "intro9_base").Message);
        Assert.Equal(32, long32.Length);
    }

    [Fact]
    public void AFixtureMustNameACharacterBeforeIt()
    {
        using var ws = BaseWorkspace.Create();
        // The [MRoom] of the pirate base comes before its bartender.
        var at = BaseWorkspace.MBases.IndexOf("[GF_NPC]\nnickname = ol02_01_bartender", StringComparison.Ordinal);
        ws.WriteBase(BaseWorkspace.MBasesPath, BaseWorkspace.MBases[..at] + """
            [MRoom]
            nickname = Bar
            fixture = ol02_01_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_bartender_fidget.thn, bartender
            [GF_NPC]
            nickname = ol02_01_bartender
            base_appr = ol_costume
            individual_name = 1200
            affiliation = ol_pirates
            voice = ol_voice
            """);
        var session = ws.Session("act");

        var warning = One(session, "The fixture", BaseWorkspace.Pirate);

        Assert.Equal("The fixture ol02_01_bartender in the room Bar of Ol02_01_Base names no [GF_NPC] before it in the " +
                     "same [MBase]. The retail game shows no character at this place.", warning.Message);
        Assert.Equal(("MRoom", "fixture", DiagnosticSeverity.Warning), (warning.Target!.Id.Type, warning.Key, warning.Severity));
    }

    [Fact]
    public void AFactionMustNameACharacterOfTheMBase()
    {
        using var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.MBasesPath, BaseWorkspace.MBases.Replace("npc = ol01_01_npc_1", "npc = ol01_01_npc_9"));
        var session = ws.Session("act");

        var warning = One(session, "The faction", BaseWorkspace.Station);

        Assert.Equal("The faction ol_grp of Ol01_01_Base names the character ol01_01_npc_9, but the [MBase] has no such " +
                     "character. The game ignores this line.", warning.Message);
        Assert.Equal(("BaseFaction", "npc"), (warning.Target!.Id.Type, warning.Key));
    }

    [Fact]
    public void TheRoomsOfTheBarDataMustBeRoomsOfTheBase()
    {
        using var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.MBasesPath, BaseWorkspace.MBases.Replace("room = Bar", "room = Lounge")
            .Replace("[MRoom]\nnickname = Deck", "[MRoom]\nnickname = Hangar"));
        var session = ws.Session("act");

        var problems = Problems(session, BaseWorkspace.Station);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p is { Key: "room", Target.Id.Type: "GF_NPC" } && p.Message ==
            "The room Lounge in the [MBase] of Ol01_01_Base is not a room of the base. The game ignores this line.");
        Assert.Contains(problems, p => p is { Key: "nickname", Target.Id.Type: "MRoom" } && p.Message.StartsWith(
            "The room Hangar in the [MBase] of Ol01_01_Base", StringComparison.Ordinal));
        Assert.All(problems, AssertText);
    }

    /// <summary>A new base without objects: no object docks with it, and no object of its system has its base line.
    /// One fault gives one warning.</summary>
    [Fact]
    public void ANewBaseWithoutObjectsGivesTheDockWarnings()
    {
        using var ws = BaseWorkspace.Create();
        var session = WithNewStation(ws);

        Assert.Equal("No object docks with Ol01_03_Base. The player cannot dock there.",
            One(session, "No object docks").Message);
        Assert.Equal("No object in Ol01 has 'base = Ol01_03_Base'. The navigation map does not list the base, its rooms " +
                     "show no planet, and the trade lists of the game skip it.", One(session, "No object in Ol01").Message);
        Assert.DoesNotContain(Problems(session), p => p.Message.StartsWith("No object with", StringComparison.Ordinal));
        Assert.All(ComponentChecks.Check(session, Entry(session)), p => Assert.Equal(DiagnosticSeverity.Warning, p.Severity));
    }

    /// <summary>An object with the base line of a new base, but without its name text, gives the warning of the trade
    /// lists. The same name text removes it.</summary>
    [Fact]
    public void TheObjectOfANewBaseMustShowItsName()
    {
        using var ws = BaseWorkspace.Create();
        var session = WithNewStation(ws);
        session.Execute(EditCommand.AddSection(BaseWorkspace.Ol01Path, "Object",
        [
            ListWorkspace.Line("nickname", "Ol01_03"), ListWorkspace.Line("ids_name", "1999"),
            ListWorkspace.Line("pos", "-10000, 0, 0"), ListWorkspace.Line("archetype", "ol_station"),
            ListWorkspace.Line("base", New), ListWorkspace.Line("dock_with", New),
        ]));

        var warning = One(session, "No object with 'base = Ol01_03_Base'");
        Assert.Equal("No object with 'base = Ol01_03_Base' shows the name of the base. The trade lists of the game then skip " +
                     "Ol01_03_Base.", warning.Message);
        Assert.Equal("strid_name", warning.Key);
        Assert.DoesNotContain(Problems(session), p => p.Message.StartsWith("No object docks", StringComparison.Ordinal));

        session.Execute(EditCommand.SetLinesFromText(BaseWorkspace.Section(session, BaseWorkspace.Ol01Path, "Object", "Ol01_03"),
            "ids_name", ["1004"]));
        Assert.DoesNotContain(Problems(session), p => p.Key == "strid_name");
    }

    /// <summary>An object that docks with a base of another system gives one warning on the object. The base has an
    /// object in its own system, so it gives no dock warning. The problems of the base hold the warning of the
    /// object.</summary>
    [Fact]
    public void AnObjectThatDocksWithABaseOfAnotherSystemGivesOneWarning()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(BaseWorkspace.Ol02Path, "Object",
        [
            ListWorkspace.Line("nickname", "Ol02_far"), ListWorkspace.Line("pos", "5000, 0, 0"),
            ListWorkspace.Line("archetype", "ol_station"), ListWorkspace.Line("dock_with", BaseWorkspace.Station),
        ]));
        var far = BaseWorkspace.Section(session, BaseWorkspace.Ol02Path, "Object", "Ol02_far");

        var warning = Assert.Single(ComponentChecks.Check(session, far));

        Assert.Equal("The object Ol02_far in Ol02 docks with Ol01_01_Base, but Ol01_01_Base is in Ol01. After a launch, the " +
                     "game puts the player in Ol01.", warning.Message);
        Assert.Equal((DiagnosticSeverity.Warning, "dock_with"), (warning.Severity, warning.Key));
        Assert.Equal(warning, Assert.Single(Problems(session, BaseWorkspace.Station)));
        Assert.Empty(ComponentChecks.Check(session, Entry(session, BaseWorkspace.Station), onlyActiveMod: false));
        Assert.Contains(AgentRows.DiagnosticsOf(session, far), p => p.Message == warning.Message);
        AssertText(warning);
    }

    /// <summary>The ship dealer room of the planet has two places for ships for sale. A third ship does not
    /// show.</summary>
    [Fact]
    public void MoreShipsThanPlacesIsAWarning()
    {
        using var ws = ShipWorkspace("""
            [BaseGood]
            base = Ol01_02_Base
            marketgood = ol_ship_package, 0, -1, 1, 1, 0, 1, 1
            marketgood = ol_ship2_package, 0, -1, 1, 1, 0, 1, 1
            marketgood = ol_ship3_package, 0, -1, 1, 1, 0, 1, 1
            """);
        var session = ws.Session("act");

        Assert.Equal("Ol01_02_Base sells 3 ships, but its rooms show at most 2. The game shows only the first 2.",
            One(session, "Ol01_02_Base sells 3 ships", BaseWorkspace.Planet).Message);
    }

    [Fact]
    public void GoodsWithoutTheirDealerIsAWarning()
    {
        using var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.CommodityMarketPath, """
            [BaseGood]
            base = Ol01_01_Base
            marketgood = ol_water, 0, -1, 150, 500, 0, 1
            [BaseGood]
            base = Ol02_01_Base
            marketgood = ol_water, 0, -1, 150, 500, 0, 1
            """);
        var session = ws.Session("act");

        Assert.Equal("Ol02_01_Base sells commodities, but no room of the base has a commodity trader. The player cannot buy " +
                     "them.", One(session, "Ol02_01_Base sells", BaseWorkspace.Pirate).Message);
    }

    /// <summary>The reverse of the check above (owner check of the spike base): a dealer whose category the market does
    /// not sell shows an empty list. A buy-only line sells nothing.</summary>
    [Fact]
    public void ADealerWithoutGoodsOfItsCategoryIsAWarning()
    {
        using var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.MiscMarketPath, """
            [BaseGood]
            base = Ol01_01_Base
            marketgood = ol_gun, 0, -1, 10, 10, 0, 1
            [BaseGood]
            base = Ol02_01_Base
            marketgood = ol_gun, 0, -1, 0, 0, 1, 1.5
            """);
        var session = ws.Session("act");

        Assert.Equal("Ol02_01_Base has an equipment dealer, but its market sells no equipment. The equipment dealer shows an " +
                     "empty list.", One(session, "Ol02_01_Base has an equipment dealer", BaseWorkspace.Pirate).Message);

        // A new station with the deck of Ol01_01_Base and no market gets the warning of each dealer.
        session.Execute(EditCommand.Group("New base", [NewBase(), NewBaseFile()]));
        One(session, "Ol01_03_Base has a commodity trader, but its market sells no commodities.");
        One(session, "Ol01_03_Base has an equipment dealer, but its market sells no equipment.");
    }

    [Fact]
    public void ANewBaseWithoutAnMBaseIsAWarning()
    {
        using var ws = BaseWorkspace.Create();
        var session = WithNewStation(ws);

        Assert.Equal("Ol01_03_Base has no [MBase]. Its bar has no characters, and it offers no missions.",
            One(session, "Ol01_03_Base has no [MBase]").Message);
        // The planet of the base game has no [MBase] either, and it gives no warning.
        Assert.Empty(Problems(session, BaseWorkspace.Planet));
    }

    /// <summary>With the buy-only pattern of vanilla, a base that sells a ship needs a line for each ship package. The
    /// check reads a market that the active mod adds or changes, and the warning names the packages without a line. The
    /// planet of the base game lists no ol_ship3_package, but the active mod does not change its market, so it gets no
    /// warning first.</summary>
    [Fact]
    public void WithTheBuyOnlyPatternABaseThatSellsAShipNeedsALineForEachShip()
    {
        using var ws = ShipWorkspace("""
            [BaseGood]
            base = Ol01_02_Base
            marketgood = ol_ship_package, 0, -1, 1, 1, 0, 1, 1
            marketgood = ol_ship2_package, 0, -1, 0, 0, 1, 1, 1
            """);
        var session = ws.Session("act");
        Assert.Empty(Problems(session, BaseWorkspace.Planet));

        session.Execute(SellShip2AtThePlanet());
        var warning = One(session, "Ol01_02_Base sells ships, but its market has no line", BaseWorkspace.Planet);

        Assert.Equal("Ol01_02_Base sells ships, but its market has no line for the ship package ol_ship3_package. The game " +
                     "gives 0 credits for the trade-in of such a ship.", warning.Message);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    /// <summary>Without buy-only ship lines in the data of the game (as HD Edition 0.7.1), a base lists only the ships
    /// that it sells, and the check gives no warning. A buy-only line of the active mod does not make the
    /// pattern.</summary>
    [Fact]
    public void WithoutTheBuyOnlyPatternTheTradeInCheckGivesNoWarning()
    {
        using var ws = ShipWorkspace("""
            [BaseGood]
            base = Ol01_02_Base
            marketgood = ol_ship_package, 0, -1, 1, 1, 0, 1, 1
            """);
        var session = ws.Session("act");
        session.Execute(SellShip2AtThePlanet());
        Assert.Empty(Problems(session, BaseWorkspace.Planet));

        session.Execute(EditCommand.AddSection(BaseWorkspace.ShipMarketPath, "BaseGood",
        [
            ListWorkspace.Line("base", BaseWorkspace.Station),
            ListWorkspace.Line("marketgood", "ol_ship3_package, 0, -1, 0, 0, 1, 1, 1"),
        ]));

        Assert.Empty(Problems(session, BaseWorkspace.Planet));
    }

    /// <summary>A market line of the active mod: the planet also sells ol_ship2_package.</summary>
    private static EditCommand SellShip2AtThePlanet() => EditCommand.AddSection(BaseWorkspace.ShipMarketPath, "BaseGood",
    [
        ListWorkspace.Line("base", BaseWorkspace.Planet),
        ListWorkspace.Line("marketgood", "ol_ship2_package, 0, -1, 1, 1, 0, 1, 1"),
    ]);

    [Fact]
    public void ATerrainTemplateWithoutItsLineIsAWarning()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.Group("New base", [NewBase(), NewBaseFile("""
            [BaseInfo]
            nickname = Ol01_03_Base
            start_room = Cityscape
            [Room]
            nickname = Cityscape
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_02_cityscape.ini
            [Room]
            nickname = ShipDealer
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_02_shipdealer.ini
            """)]));

        var warning = One(session, "The room Cityscape");

        Assert.Equal("The room Cityscape of Ol01_03_Base shows the template $terrain_tiny, but Ol01_03_Base has no " +
                     "terrain_tiny line. The room shows no model there.", warning.Message);
        Assert.Equal((Entry(session), "terrain_tiny"), (warning.Target, warning.Key));
        session.Execute(EditCommand.SetLinesFromText(Entry(session), "terrain_tiny", ["ol_rock_tiny"]));
        Assert.DoesNotContain(Problems(session), p => p.Message.StartsWith("The room Cityscape", StringComparison.Ordinal));
    }

    /// <summary>A character of the active mod without a voice and a name does not show in the game. A character of the
    /// base game is not checked.</summary>
    [Fact]
    public void ACharacterOfTheActiveModNeedsABodyAVoiceAndAName()
    {
        var copy = BaseWorkspace.MBases.Replace("individual_name = 1200\naffiliation = ol_pirates\nvoice = ol_voice\n",
            "affiliation = ol_pirates\n");
        using var ws = BaseWorkspace.Create(activeFiles: new() { [@"files\" + BaseWorkspace.MBasesPath] = copy });
        var session = ws.Session("act");

        var warning = One(session, "The character", BaseWorkspace.Pirate);

        Assert.Equal("The character ol02_01_bartender of Ol02_01_Base has no voice and no name. The game does not show it.",
            warning.Message);
        Assert.Equal(("GF_NPC", "voice"), (warning.Target!.Id.Type, warning.Key));
        Assert.DoesNotContain(Problems(session, BaseWorkspace.Station), p => p.Message.StartsWith("The character",
            StringComparison.Ordinal));
    }

    /// <summary>The save gate reads only the errors of the bases that the active mod adds or changes. A patch of a base
    /// file of the base game checks the base of the file. The pirate base shares the bar, and it does not change.</summary>
    [Fact]
    public void TheGateChecksTheBasesOfTheBaseFilesThatTheActiveModChanges()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        Assert.Empty(BaseChecks.SaveErrors(session));

        session.Execute(EditCommand.SetLinesFromText(BaseWorkspace.Section(session, BaseWorkspace.StationPath, "Room", "Bar"),
            "file", [@"Universe\Systems\Ol01\Bases\Rooms\missing_bar.ini"]));
        var error = Assert.Single(BaseChecks.SaveErrors(session));

        Assert.StartsWith(@"The room file Universe\Systems\Ol01\Bases\Rooms\missing_bar.ini of the room Bar of " +
                          "Ol01_01_Base does not exist.", error.Message);
        Assert.Contains(session.SaveProblems(), p => p.Message == error.Message);
        Assert.Empty(Problems(session, BaseWorkspace.Pirate));
    }

    /// <summary>A copy of the fixture with three ship packages, the second one and the third one without a market line
    /// at the planet unless <paramref name="shipMarket"/> has one.</summary>
    private static TestWorkspace ShipWorkspace(string shipMarket)
    {
        var ws = BaseWorkspace.Create();
        ws.WriteBase(@"DATA\EQUIPMENT\goods.ini", """
            [Good]
            nickname = ol_water
            category = commodity
            [Good]
            nickname = ol_gun
            equipment = ol_gun
            category = equipment
            [Good]
            nickname = ol_ship_hull
            category = shiphull
            ship = ol_ship
            [Good]
            nickname = ol_ship_package
            category = ship
            hull = ol_ship_hull
            [Good]
            nickname = ol_ship2_package
            category = ship
            hull = ol_ship_hull
            [Good]
            nickname = ol_ship3_package
            category = ship
            hull = ol_ship_hull
            """);
        ws.WriteBase(BaseWorkspace.ShipMarketPath, shipMarket);
        return ws;
    }
}
