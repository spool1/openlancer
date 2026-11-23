using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The base tools of the agent server (section E of the bases design): <c>base_create</c>, <c>get_base</c> and
/// <c>list_bases</c>, the base input of <c>list_values</c>, the bases of <c>get_system</c>, the refusal of
/// <c>duplicate_items</c> and the resources of the bases. The install is the station workspace of the create tests
/// (see <see cref="BasePlansCreateTests.Workspace"/>). The data is made up.
/// </summary>
public sealed class AgentBaseToolTests
{
    /// <summary>The arguments of a new base Ol01_05_Base in Ol01 from the station Ol01_01_Base, with a new
    /// station.</summary>
    private const string Harbor = """
        "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_01_Base", "name": "Harbor Rock",
        "info": "A rock with a harbor.", "dock_object": { "position": [5000, 0, -3000] }
        """;

    private const string NewBase = "Ol01_05_Base";

    private static readonly string NewFile = ContentPaths.NewBaseFile("act", NewBase);

    /// <param name="setup">More files before the session opens, or null.</param>
    private static (TestWorkspace Ws, EditSession Session, TestAgentHost Host) Bases(bool fullSystem = true,
        System.Action<TestWorkspace>? setup = null)
    {
        var ws = BasePlansCreateTests.Workspace(fullSystem);
        setup?.Invoke(ws);
        var session = ws.Session("act");
        return (ws, session, new TestAgentHost(session));
    }

    private static string[] Texts(JsonNode? array) => array!.AsArray().Select(t => (string)t!).ToArray();

    private static string[] Messages(JsonNode? problems) =>
        problems?.AsArray().Select(p => (string)p!["message"]!).ToArray() ?? [];

    private static SectionRef Entry(string nickname) =>
        new(BaseWorkspace.UniversePath, new SectionIdentity("Base", nickname));

    /// <summary>
    /// base_create makes the base, its base file, its bar data, its news, its market, its name and its station as one
    /// entry with the edit origin Agent. The result gives the base, the file and the station, and selects the station.
    /// One undo of the agent takes everything back.
    /// </summary>
    [Fact]
    public void BaseCreateMakesTheBaseAndItsStationInOneUndoStep()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        var result = Ok(host, "base_create", $$"""{ {{Harbor}} }""");

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal(("base:Ol01_05_Base", "obj:ol01_station_1"), ((string?)result["base"], (string?)result["dock_object"]));
        Assert.Equal(NewFile, (string?)result["file"], ignoreCase: true);
        Assert.Null(result["ring"]);
        Assert.Equal(("Agent: Create base Ol01_05_Base", "obj:ol01_station_1"),
            ((string?)result["undo_label"], (string?)result["select"]));
        var adds = Texts(result["adds"]);
        Assert.Equal("base:Ol01_05_Base", adds[0]);
        Assert.Contains("obj:ol01_station_1", adds);
        Assert.Contains(adds, a => a.Contains("MBase/Ol01_05_Base"));
        Assert.NotNull(session.Get(Entry(NewBase)));
        Assert.True(session.HasFile(NewFile));
        Assert.True(Data.Strings.StringPlaceholder.TryParse(session.Base(NewBase)!.Name!, out var name));
        Assert.Equal("Harbor Rock", session.Text(name));
        Assert.Single(host.Edits);

        var undo = Ok(host, "undo");

        Assert.Equal("Agent: Create base Ol01_05_Base", (string?)undo["label"]);
        Assert.Null(session.Get(Entry(NewBase)));
        Assert.False(session.HasFile(NewFile));
        Assert.False(session.IsDirty);
    }

    /// <summary>
    /// The fields of dock_object go to the new station after the lines of the template, in the same Undo step. One undo
    /// takes back the base, the station and the fields.
    /// </summary>
    [Fact]
    public void TheFieldsOfTheDockObjectGoToTheNewStationInTheSameUndoStep()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;
        var station = new SectionRef(BaseWorkspace.Ol01Path, new SectionIdentity("Object", "ol01_station_1"));

        Ok(host, "base_create", """
            { "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_01_Base", "name": "Harbor Rock",
              "dock_object": { "position": [5000, 0, -3000], "fields": { "difficulty_level": 5, "visit": 16 } } }
            """);

        Assert.Equal("5", Assert.Single(EditSessionTests.Values(session, station, "difficulty_level")));
        Assert.Equal("16", Assert.Single(EditSessionTests.Values(session, station, "visit")));
        Assert.Equal(NewBase, Assert.Single(EditSessionTests.Values(session, station, "dock_with")));
        Assert.Single(host.Edits);

        Ok(host, "undo");

        Assert.Null(session.Get(station));
        Assert.Null(session.Get(Entry(NewBase)));
        Assert.False(session.IsDirty);
    }

    /// <summary>A base without a dock object gives one warning of the fault: the problem of the base checks in
    /// new_problems. The plan does not warn about it a second time.</summary>
    [Fact]
    public void ABaseWithoutADockObjectGivesOneWarningOfTheFault()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        var result = Ok(host, "base_create", """
            { "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_01_Base", "name": "Harbor Rock" }
            """);

        Assert.Equal(["No object docks with Ol01_05_Base. The player cannot dock there."],
            Messages(result["warnings"]).Concat(Messages(result["new_problems"]))
                .Where(m => m.StartsWith("No object docks", System.StringComparison.Ordinal)));
    }

    /// <summary>A preview with with_patch changes nothing and gives the text that the save writes for each file. The
    /// new base file is a whole file of the mod, so its text is the whole file.</summary>
    [Fact]
    public void APreviewWithThePatchShowsTheWholeBaseFile()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        var preview = Ok(host, "base_create", $$"""{ {{Harbor}}, "preview": true, "with_patch": true }""");

        Assert.Equal(("previewed", "base:Ol01_05_Base"), ((string?)preview["status"], (string?)preview["base"]));
        Assert.Equal(0, session.Revision);
        Assert.Null(session.Get(Entry(NewBase)));
        var patch = preview["patch"]!.AsObject();
        var file = (string?)patch.Single(p => p.Key.Equals(NewFile, System.StringComparison.OrdinalIgnoreCase)).Value;
        Assert.StartsWith("[BaseInfo]", file!.TrimStart());
        Assert.Contains("nickname = Ol01_05_Base", file);
        Assert.Contains("start_room = Deck", file);
        Assert.Contains("[Room]", file);
        Assert.Contains(patch, p => p.Key.EndsWith("mBases.ini", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(patch, p => p.Key.EndsWith("news.ini", System.StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<object?[]> Refusals()
    {
        object?[] Row(string arguments, string code, string? detail, string? field) => [arguments, code, detail, field];
        const string Base = """ "system": "Ol01", "template": "Ol01_01_Base", "name": "Harbor Rock" """;
        yield return Row($$"""{ "nickname": "Intro_harbor", {{Base}} }""", "invalid_value", "NameInvalid", "nickname");
        yield return Row($$"""{ "nickname": "1base", {{Base}} }""", "invalid_argument", "BadArgument", "nickname");
        yield return Row($$"""{ "nickname": "OL01_01_BASE", {{Base}} }""", "name_taken", "NameTaken", "nickname");
        yield return Row("""{ "nickname": "Ol01_05_Base", "system": "Ol09", "template": "Ol01_01_Base", "name": "x" }""",
            "not_found", ItemRefs.NotFound, "system");
        yield return Row("""{ "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_08_Base", "name": "x" }""",
            "not_found", ItemRefs.NotFound, "template");
        yield return Row("""{ "nickname": "Ol01_05_Base", "system": "Ol01", "template": "system:Ol01", "name": "x" }""",
            "invalid_argument", "BadArgument", "template");
        yield return Row("""{ "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Intro1_Base", "name": "x" }""",
            "refused", BasePlans.IntroTemplate, "template");
        yield return Row("""{ "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_09_Base", "name": "x" }""",
            "refused", BasePlans.NoBaseFile, "template");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "rooms": ["Hangar"] }""", "invalid_value",
            BasePlans.RoomUnknown, "rooms");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "rooms": ["Bar"] }""", "invalid_value",
            BasePlans.StartRoomNotKept, "start_room");
        yield return Row("""
            { "nickname": "Ol01_05_Base", "system": "Ol03", "template": "Ol01_01_Base", "name": "x",
              "dock_object": { "position": [0, 0, 0] } }
            """, "refused", BasePlans.NoSystemFile, "system");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "dock_object": { "position": [0, 0, 0], "archetype": "ol_station_c" } }""",
            "invalid_value", BasePlans.DockArchetype, "dock_object.archetype");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "dock_object": { "planet": "Ol01_01" } }""",
            "invalid_value", BasePlans.NotAPlanet, "dock_object.planet");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "dock_object": {} }""",
            "invalid_argument", BasePlans.DockPlace, "dock_object.position");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "local_faction": "ol_nobody" }""",
            "invalid_value", MissionErrors.FieldValue, "local_faction");
        yield return Row("""{ "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_01_Base", "name": "   " }""",
            "invalid_value", MissionErrors.FieldValue, "name");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "dock_object": { "position": [0, 0, 0], "fields": { "pos": "x" } } }""",
            "invalid_value", null, "dock_object.fields.pos");
        yield return Row($$"""{ "nickname": "Ol01_05_Base", {{Base}}, "market": "Ol09_09_Base" }""",
            "not_found", ItemRefs.NotFound, "market");
    }

    /// <summary>Each refusal of base_create has its error kind, its detail and the argument of its cause. The message
    /// is the text of the plan, in STE. Nothing changes.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ABadRequestGivesTheErrorOfItsCause(string arguments, string code, string? detail, string? field)
    {
        using var ws = BasePlansCreateTests.Workspace(fullSystem: true);
        ws.WriteBase(BaseWorkspace.UniversePath, BaseWorkspace.Universe.Replace("[system]\nnickname = Ol01", """
            [Base]
            nickname = Intro1_Base
            system = Ol01
            file = Universe\Systems\Ol01\Bases\ol01_01_base.ini
            [Base]
            nickname = Ol01_09_Base
            system = Ol01
            file = Universe\Systems\Ol01\Bases\ol01_09_base.ini
            [system]
            nickname = Ol03
            file = systems\Ol03\ol03.ini
            [system]
            nickname = Ol01
            """));
        var session = ws.Session("act");
        var host = new TestAgentHost(session);

        var result = Call(host, "base_create", arguments);

        Assert.True(result.IsError, result.Text);
        Assert.Equal((code, field), ((string?)result.Content["code"], (string?)result.Content["field"]));
        if (detail != null)
            Assert.Equal(detail, (string?)result.Content["detail"]);
        Assert.Empty(SteLint.Problems((string)result.Content["message"]!));
        Assert.False(session.IsDirty);
    }

    /// <summary>A planet base takes an existing planet of the system: the planet gets the base and its name, and the
    /// result names it as a change and as the dock object. The new docking ring is in the result too.</summary>
    [Fact]
    public void APlanetBaseGivesTheExistingPlanetTheBaseAndANewRing()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        var result = Ok(host, "base_create", """
            { "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_02_Base", "name": "Green Hold",
              "dock_object": { "planet": "Ol01_03" } }
            """);

        Assert.Equal("obj:Ol01_03", (string?)result["dock_object"]);
        Assert.Contains("obj:Ol01_03", Texts(result["changes"]));
        Assert.NotNull((string?)result["ring"]);
        Assert.Contains((string)result["ring"]!, Texts(result["adds"]));
    }

    /// <summary>
    /// edit_batch runs system_create and then base_create in the new system. The batch is one undo step of the agent,
    /// so one undo takes back the system and the base.
    /// </summary>
    [Fact]
    public void ABatchMakesASystemAndABaseInItWithOneUndo()
    {
        var (ws, session, host) = Bases(fullSystem: false, setup: w => w.WriteBase(BaseWorkspace.Ol01Path,
            BaseWorkspace.Ol01 + """

                [Object]
                nickname = Ol01_sun
                pos = 0, 0, 0
                archetype = ol_sun
                star = ol_star
                """));
        using var _ = ws;

        var result = Ok(host, "edit_batch", """
            { "label": "New system with a base", "steps": [
              { "tool": "system_create", "arguments": { "nickname": "Ol07", "name": "Haven", "like": "Ol01", "map_pos": [6, 6] } },
              { "tool": "base_create", "arguments": { "nickname": "Ol07_01_Base", "system": "Ol07",
                "template": "Ol01_01_Base", "name": "Haven Dock", "dock_object": { "position": [3000, 0, 0] } } }
            ] }
            """);

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Contains(result["steps"]!.AsArray()[1]!["adds"]!.AsArray(), a => (string?)a == "base:Ol07_01_Base");
        Assert.NotNull(session.Get(Entry("Ol07_01_Base")));
        Assert.Equal("Ol07", session.Base("Ol07_01_Base")!.System);
        Assert.NotNull(session.Base("Ol07_01_Base")!.NamedObject);

        Ok(host, "undo");

        Assert.Null(session.Get(Entry("Ol07_01_Base")));
        Assert.Null(session.SystemSection("Ol07"));
        Assert.False(session.CanUndo);
    }

    /// <summary>mBases.ini and news.ini are in the group Scene. So a host that hides the group Missions still makes a
    /// base with its bar data and its news. A host that hides the group Scene does not have the tool.</summary>
    [Fact]
    public void AHostThatHidesMissionsStillMakesABase()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;
        host.Hidden.Add(ToolGroup.Missions);

        Ok(host, "base_create", $$"""{ {{Harbor}} }""");

        Assert.NotNull(session.Base(NewBase)!.MBase);
        Assert.Contains(session.Sections(BaseWorkspace.NewsPath), s => session.Get(s)!.Lines("base")
            .Any(l => l.Values[0].ToString() == NewBase));
        host.Hidden.Add(ToolGroup.Scene);
        Assert.Equal(("not_available", "GroupHidden"),
            Error(host, "base_create", $$"""{ {{Harbor}} }""".Replace("Ol01_05", "Ol01_06")) is var (code, detail, _)
                ? (code, detail)
                : default);
    }

    /// <summary>list_bases gives the bases of W in the order of universe.ini without the intro bases. The filters are
    /// the system, the text, the rooms, the kind and the bases that the active mod adds. A new base shows before the
    /// save.</summary>
    [Fact]
    public void ListBasesFiltersTheBases()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;
        string[] Names(string arguments) =>
            Ok(host, "list_bases", arguments)["rows"]!.AsArray().Select(r => (string)r!["nickname"]!).ToArray();

        var all = Ok(host, "list_bases")["rows"]!.AsArray();

        Assert.Equal([BaseWorkspace.Station, BaseWorkspace.Planet, BaseWorkspace.Pirate],
            all.Select(r => (string)r!["nickname"]!));
        var station = all[0]!;
        Assert.Equal(("base:Ol01_01_Base", "system:Ol01", "station"),
            ((string?)station["ref"], (string?)station["system"], (string?)station["kind"]));
        Assert.Equal(["Deck", "Bar"], Texts(station["rooms"]));
        Assert.Equal((3, 2, 1), ((int)station["characters"]!, (int)station["goods"]!, (int)station["dock_objects"]!));
        // The planet has the base line and the ring docks with the base. Only the ring counts.
        Assert.Equal(1, (int)all[1]!["dock_objects"]!);
        Assert.Equal([BaseWorkspace.Pirate], Names("""{ "system": "Ol02" }"""));
        Assert.Equal([BaseWorkspace.Planet], Names("""{ "text": "ol01_02" }"""));
        Assert.Equal([BaseWorkspace.Planet], Names("""{ "has_rooms": ["shipdealer"] }"""));
        Assert.Equal([BaseWorkspace.Station, BaseWorkspace.Pirate], Names("""{ "has_rooms": ["Deck", "Bar"] }"""));
        Assert.Empty(Names("""{ "has_rooms": ["Deck", "ShipDealer"] }"""));
        Assert.Equal([BaseWorkspace.Planet], Names("""{ "kind": "planet" }"""));
        Assert.Empty(Names("""{ "active_mod_only": true }"""));

        Ok(host, "base_create", $$"""{ {{Harbor}} }""");

        Assert.Equal([NewBase], Names("""{ "active_mod_only": true }"""));
        Assert.Equal([NewBase], Names("""{ "text": "harbor" }"""));
        // The counts read the new bar data and the new market lines, not the state before the edit.
        var made = Ok(host, "list_bases", """{ "active_mod_only": true }""")["rows"]![0]!;
        Assert.Equal(("Harbor Rock", 3, 2, 1), ((string?)made["name"], (int)made["characters"]!, (int)made["goods"]!,
            (int)made["dock_objects"]!));

        Ok(host, "undo");

        Assert.Empty(Names("""{ "active_mod_only": true }"""));
        Assert.Equal(3, Ok(host, "list_bases")["rows"]!.AsArray().Count);
    }

    /// <summary>The intro bases of the main menu are not in the list of the bases, and not in the bases of their
    /// system.</summary>
    [Fact]
    public void ListBasesAndGetSystemLeaveOutTheIntroBases()
    {
        using var ws = BasePlansCreateTests.Workspace();
        ws.WriteBase(BaseWorkspace.UniversePath, BaseWorkspace.Universe.Replace("[system]\nnickname = Ol01",
            "[Base]\nnickname = Intro1_Base\nsystem = Ol01\nfile = Universe\\Systems\\Ol01\\Bases\\ol01_01_base.ini\n" +
            "[system]\nnickname = Ol01"));
        var host = new TestAgentHost(ws.Session("act"));

        var rows = Ok(host, "list_bases")["rows"]!.AsArray();
        var system = Ok(host, "get_system", """{ "system": "Ol01" }""");

        Assert.DoesNotContain(rows, r => (string?)r!["nickname"] == "Intro1_Base");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["base:Ol01_01_Base", "base:Ol01_02_Base"], Texts(system["bases"]));
    }

    /// <summary>
    /// get_base gives the parts of a base: the rooms with their dealers, doors and scripts, the bar data with the
    /// characters, the market lines of each market file, the dock objects and the named object. It also gives the
    /// seven steps of the completeness and the problems of the base checks.
    /// </summary>
    [Fact]
    public void GetBaseGivesThePartsTheCompletenessAndTheProblems()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        var station = Ok(host, "get_base", """{ "base": "base:ol01_01_base" }""");

        Assert.Equal(("base:Ol01_01_Base", "Ol01_01_Base", "station", "Deck"),
            ((string?)station["ref"], (string?)station["nickname"], (string?)station["kind"], (string?)station["start_room"]));
        Assert.Equal(BaseWorkspace.StationPath, (string?)station["file"], ignoreCase: true);
        var rooms = station["rooms"]!.AsArray();
        Assert.Equal(["Deck", "Bar"], rooms.Select(r => (string)r!["nickname"]!));
        var deck = rooms[0]!;
        Assert.Equal(["commodity_trader", "equipment_dealer"], Texts(deck["dealers"]));
        Assert.Equal(["Trader", "Equipment"], Texts(deck["virtual_rooms"]));
        Assert.Equal(("IDS_HOTSPOT_BAR", "Bar"), ((string?)deck["doors"]![0]!["name"], (string?)deck["doors"]![0]!["to"]));
        Assert.Equal(@"DATA\SCRIPTS\BASES\ol_deck_set.thn", Assert.Single(Texts(deck["scripts"])), ignoreCase: true);
        Assert.True((bool)rooms[1]!["shared"]!);
        Assert.Equal(("ol_grp", 3f), ((string?)station["local_faction"], (float)station["difficulty"]!));
        var characters = station["characters"]!.AsArray();
        Assert.Equal(["bartender", "trader", null], characters.Select(c => (string?)c!["role"]));
        Assert.Equal(("Bar", "ol_grp"), ((string?)characters[2]!["room"], (string?)characters[2]!["faction"]));
        var market = station["market"]!.AsArray();
        Assert.Equal(["ol_gun", "ol_water"], market.Select(m => (string)m!["good"]!));
        Assert.Equal(("equipment", "buy_and_sell", 10, true), ((string?)market[0]!["category"], (string?)market[0]!["transaction"],
            (int)market[0]!["max_stock"]!, (bool)market[0]!["kept"]!));
        Assert.Equal("base", (string?)market[0]!["origin"]!["layer"]);
        Assert.Equal("obj:Ol01_01", (string?)station["named_object"]);
        Assert.Contains(station["dock_objects"]!.AsArray(), d => (string?)d!["ref"] == "obj:Ol01_01" && (bool)d["docks_with"]!);
        var completeness = station["completeness"]!;
        Assert.Equal(7, (int)completeness["total"]!);
        Assert.Equal(["Universe entry", "Base file", "Start room", "Rooms", "Characters", "Market", "Dock object"],
            completeness["steps"]!.AsArray().Select(s => (string)s!["name"]!));
        Assert.Empty(Messages(station["problems"]).Where(m => m.Contains("crash")));

        var planet = Ok(host, "get_base", """{ "base": "Ol01_02_Base" }""");
        Assert.Equal(["ol_rock_tiny", "ol_rock_sml"], planet["terrain"]!.AsArray().Select(t => (string)t!["archetype"]!));
        Assert.Null(planet["bar_data"]);
        Assert.Equal("hidden", (string?)planet["completeness"]!["steps"]!.AsArray()
            .Single(s => (string?)s!["name"] == "Characters")!["state"]);
    }

    /// <summary>A new base without a dock object misses the step of the dock object. get_base gives the step with its
    /// text and the system as the place to go. A base that W does not have gives not_found.</summary>
    [Fact]
    public void GetBaseOfANewBaseWithoutADockObjectGivesTheMissingStep()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;
        Ok(host, "base_create", """
            { "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_01_Base", "name": "Harbor Rock" }
            """);

        var made = Ok(host, "get_base", """{ "base": "Ol01_05_Base" }""");

        Assert.Equal("active_mod", (string?)made["origin"]!["layer"]);
        Assert.True((bool)made["origin"]!["unsaved"]!);
        var dock = made["completeness"]!["steps"]!.AsArray().Single(s => (string?)s!["name"] == "Dock object")!;
        Assert.Equal(("missing", "system:Ol01"), ((string?)dock["state"], (string?)dock["go_to"]));
        Assert.Contains(NewBase, (string?)dock["detail"]);
        Assert.Equal(6, (int)made["completeness"]!["done"]!);
        Assert.Equal(("not_found", ItemRefs.NotFound), Error(host, "get_base", """{ "base": "Ol09_09_Base" }""") is var (c, d, _)
            ? (c, d)
            : default);
        Assert.Equal("invalid_argument", Error(host, "get_base", """{ "base": "obj:Ol01_01" }""").Code);
    }

    /// <summary>get_system gives each base whose system line names the system, also a new base before its dock object,
    /// and each base that an object of the system names.</summary>
    [Fact]
    public void GetSystemGivesTheBasesOfTheSystemLinesAndOfTheObjects()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        Ok(host, "base_create", """
            { "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_01_Base", "name": "Harbor Rock" }
            """);
        var system = Ok(host, "get_system", """{ "system": "Ol01" }""");

        Assert.Equal(["base:Ol01_01_Base", "base:Ol01_02_Base", "base:Ol01_05_Base"], Texts(system["bases"]));
    }

    /// <summary>get_system also gives a base whose system line names another system, when an object of the system
    /// names it. Such a base comes after the bases of the system lines.</summary>
    [Fact]
    public void GetSystemGivesABaseThatOnlyAnObjectOfTheSystemNames()
    {
        var (ws, session, host) = Bases(fullSystem: false, setup: w => w.WriteBase(BaseWorkspace.Ol01Path,
            BaseWorkspace.Ol01 + """

                [Object]
                nickname = Ol01_outpost
                pos = 30000, 0, 0
                archetype = ol_station
                base = Ol02_01_Base
                """));
        using var _ = ws;

        var system = Ok(host, "get_system", """{ "system": "Ol01" }""");

        Assert.Equal(["base:Ol01_01_Base", "base:Ol01_02_Base", "base:Ol02_01_Base"], Texts(system["bases"]));
    }

    /// <summary>
    /// get_base marks the line that the games keep for each good: the last line of the good. The earlier line of the
    /// same good is not kept. A [BaseGood] whose base line has another letter case is a market of the same base.
    /// </summary>
    [Fact]
    public void GetBaseKeepsOnlyTheLastLineOfAGood()
    {
        var (ws, session, host) = Bases(setup: w => w.WriteBase(BaseWorkspace.CommodityMarketPath, """
            [BaseGood]
            base = Ol01_01_Base
            marketgood = ol_water, 0, -1, 150, 500, 0, 1
            [BaseGood]
            base = OL01_01_BASE
            marketgood = ol_water, 0, -1, 100, 300, 0, 1.2
            """));
        using var _ = ws;

        var market = Ok(host, "get_base", """{ "base": "Ol01_01_Base" }""")["market"]!.AsArray();
        var row = Ok(host, "list_bases", """{ "text": "ol01_01" }""")["rows"]![0]!;

        Assert.Equal([("ol_gun", true, 10), ("ol_water", false, 500), ("ol_water", true, 300)],
            market.Select(m => ((string)m!["good"]!, (bool)m["kept"]!, (int)m["max_stock"]!)));
        Assert.Equal(2, (int)row["goods"]!);
    }

    /// <summary>
    /// list_values gives the rooms and the characters of one base with the argument base. A list of the parts of a base
    /// without the argument, and another list with it, give invalid_argument. The catalog of the lists marks the lists
    /// of one base.
    /// </summary>
    [Fact]
    public void ListValuesGivesThePartsOfOneBase()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;
        string[] Values(JsonObject page) => page["rows"]!.AsArray().Select(r => (string)r!["value"]!).ToArray();

        var rooms = Ok(host, "list_values", """{ "list": "room_of_base", "base": "Ol01_01_Base" }""");
        var characters = Ok(host, "list_values", """{ "list": "character_of_base", "base": "base:Ol01_01_Base" }""");
        var noBase = Error(host, "list_values", """{ "list": "room_of_base" }""");
        var other = Error(host, "list_values", """{ "list": "faction", "base": "Ol01_01_Base" }""");
        var withSystem = Error(host, "list_values", """{ "list": "character_of_base", "base": "Ol01_01_Base", "system": "Ol01" }""");
        var missing = Error(host, "list_values", """{ "list": "room_of_base", "base": "Ol09_09_Base" }""");
        var catalog = JsonNode.Parse(AgentResources.Find("openlancer://catalog/lists")!.Read(host))!["lists"]!.AsArray();

        Assert.Equal(["Deck", "Bar"], Values(rooms));
        Assert.Equal(["ol01_01_bartender", "ol01_01_trader", "ol01_01_npc_1"], Values(characters));
        Assert.Equal(["bartender", "trader", "crowd"],
            characters["rows"]!.AsArray().Select(r => (string)r!["detail"]!));
        Assert.Equal(("invalid_argument", "BadArgument", "The list room_of_base holds the parts of one base. Give the " +
                                                         "argument 'base'."), noBase);
        Assert.Equal(("invalid_argument", "BadArgument"), (other.Code, other.Detail));
        Assert.Equal(("invalid_argument", "BadArgument"), (withSystem.Code, withSystem.Detail));
        Assert.Equal(("not_found", ItemRefs.NotFound), (missing.Code, missing.Detail));
        Assert.All(catalog.Where(l => (string?)l!["id"] is "room_of_base" or "character_of_base"),
            l => Assert.True((bool)l!["one_base"]!));
        Assert.Equal(2, catalog.Count(l => (bool)l!["one_base"]!));
        Assert.Equal(catalog.Select(l => (string)l!["id"]!).Order(System.StringComparer.Ordinal),
            catalog.Select(l => (string)l!["id"]!));
    }

    /// <summary>duplicate_items refuses a base: base_create makes a new base from a template base.</summary>
    [Fact]
    public void DuplicateItemsRefusesABase()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        var (code, detail, message) = Error(host, "duplicate_items", """{ "refs": ["base:Ol01_01_Base"] }""");

        Assert.Equal(("refused", BaseTools.BaseCopy), (code, detail));
        Assert.Equal("duplicate_items does not copy the base base:Ol01_01_Base. Use base_create with the base as the " +
                     "template.", message);
        Assert.False(session.IsDirty);
    }

    /// <summary>The catalog of the template bases reads the open profile: each base with a base file and rooms, except
    /// the intro bases, with its rooms, dealers, terrain, counts and the archetype of its dock object. Without a profile,
    /// it is empty.</summary>
    [Fact]
    public void TheCatalogOfTheTemplatesGivesTheBasesWithRooms()
    {
        var (ws, session, host) = Bases();
        using var _ = ws;

        var resource = AgentResources.Find("openlancer://catalog/base-templates")!;
        var templates = JsonNode.Parse(resource.Read(host))!["templates"]!.AsArray();
        var empty = JsonNode.Parse(resource.Read(new TestAgentHost(null)))!["templates"]!.AsArray();

        Assert.True(resource.Session);
        Assert.Equal([BaseWorkspace.Station, BaseWorkspace.Planet, BaseWorkspace.Pirate],
            templates.Select(t => (string)t!["base"]!));
        var station = templates[0]!;
        Assert.Equal(("station", "Deck", "ol_station", 3), ((string?)station["kind"], (string?)station["start_room"],
            (string?)station["dock_archetype"], (int)station["characters"]!));
        Assert.Equal((1, 1), ((int)station["goods"]!["equipment"]!, (int)station["goods"]!["commodity"]!));
        Assert.Equal(["commodity_trader", "equipment_dealer"], Texts(station["rooms"]![0]!["dealers"]));
        Assert.Equal(["terrain_tiny", "terrain_sml"], Texts(templates[1]!["terrain"]));
        Assert.Empty(empty);
    }

    /// <summary>find_references on a base gives the lines of the INI files and the lines of the mission scripts that
    /// name the base.</summary>
    [Fact]
    public void FindReferencesOnABaseGivesTheMissionLines()
    {
        using var ws = BasePlansCreateTests.Workspace(fullSystem: true);
        ws.WriteBase(@"DATA\MISSIONS\OL_TEST\ol_test.ini", """
            [Mission]
            mission_title = 0
            [Trigger]
            nickname = ol_start
            InitState = ACTIVE
            Cnd_BaseEnter = Ol01_01_Base
            Act_ChangeState = SUCCEED
            """);
        var host = new TestAgentHost(ws.Session("act"));

        var references = Ok(host, "find_references", """{ "ref": "base:Ol01_01_Base" }""");

        Assert.Contains(references["lines"]!.AsArray(), l => ((string?)l!["ref"])!.Contains("Ol01_01"));
        Assert.Contains(references["mission_lines"]!.AsArray(), l => (string?)l!["key"] == "Cnd_BaseEnter");
    }
}
