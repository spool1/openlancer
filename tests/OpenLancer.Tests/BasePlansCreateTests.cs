using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A new base from a template base (section C.5 of the bases design): the entry in <c>universe.ini</c>, the base file,
/// the copies of room files, the <c>[MBase]</c> with its characters and their name texts, the news items, the dock
/// objects and the shared name. One Undo restores each file and the text. The data is made up (see
/// <see cref="BaseWorkspace"/>).
/// </summary>
public sealed class BasePlansCreateTests
{
    private const string New = "Ol01_05_Base";
    private static readonly string NewFile = ContentPaths.NewBaseFile("act", New);

    /// <summary>Three news items: the first and the third name the station, the second names only the pirate
    /// base.</summary>
    private const string News = """
        [NewsItem]
        rank = mission_end, mission_end
        icon = world
        logo = ol_news_logo
        headline = 2001
        text = 2002
        base = Ol01_01_Base
        base = Ol02_01_Base
        [NewsItem]
        rank = mission_end, mission_end
        icon = critical
        logo = ol_news_logo
        headline = 2003
        text = 2004
        base = Ol02_01_Base
        [NewsItem]
        rank = mission_end, mission_end
        icon = world
        logo = ol_news_logo
        headline = 2005
        text = 2006
        base = ol01_01_base
        """;

    /// <summary>The system file of Ol01 with the lines of a retail station on the station, a planet without a base,
    /// and lines of a retail ring on the ring.</summary>
    private const string Ol01Full = """
        [Object]
        nickname = Ol01_01
        ids_name = 1001
        pos = 0, 0, 0
        archetype = ol_station
        base = Ol01_01_Base
        dock_with = Ol01_01_Base
        reputation = ol_grp
        voice = ol_voice_atc
        space_costume = ol_head, ol_body
        pilot = ol_pilot
        behavior = NOTHING
        difficulty_level = 2
        loadout = ol_station_loadout
        visit = 0
        [Object]
        nickname = Ol01_02
        ids_name = 1002
        pos = 10000, 0, 0
        archetype = ol_planet
        base = Ol01_02_Base
        [Object]
        nickname = Dock_Ring_Ol01_02
        ids_name = 1099
        pos = 10000, 0, 2500
        archetype = ol_ring
        dock_with = Ol01_02_Base
        behavior = NOTHING
        loadout = ol_ring_loadout
        reputation = ol_grp
        [Object]
        nickname = Ol01_03
        ids_name = 1003
        pos = -20000, 0, 0
        archetype = ol_planet
        """;

    /// <summary>The solar archetypes of the tests: the archetypes of the fixture, a second station, a bigger planet,
    /// a station without a docking sphere and a sun.</summary>
    private const string Solars = """
        [Solar]
        nickname = ol_station
        type = STATION
        docking_sphere = berth, HpDockMountA, 50
        [Solar]
        nickname = ol_planet
        type = PLANET
        solar_radius = 2000
        [Solar]
        nickname = ol_ring
        type = DOCKING_RING
        docking_sphere = ring, HpDockMountA, 30
        [Solar]
        nickname = ol_station_b
        type = STATION
        docking_sphere = berth, HpDockMountA, 50
        [Solar]
        nickname = ol_planet_big
        type = PLANET
        solar_radius = 3000
        [Solar]
        nickname = ol_station_c
        type = STATION
        [Solar]
        nickname = ol_sun
        type = SUN
        """;

    /// <summary>The station workspace of <see cref="BaseWorkspace"/> with news, more solar archetypes and the full
    /// system file of Ol01.</summary>
    private static TestWorkspace Workspace(bool fullSystem = false)
    {
        var ws = BaseWorkspace.Create();
        ws.WriteBase(BaseWorkspace.NewsPath, News);
        ws.WriteBase(@"DATA\SOLAR\solararch.ini", Solars);
        if (fullSystem)
            ws.WriteBase(BaseWorkspace.Ol01Path, Ol01Full);
        return ws;
    }

    private static BaseSpec Spec(string template = BaseWorkspace.Station) => new(New, "Ol01", template, "Harbor Rock");

    private static BaseSpec Station(string template = BaseWorkspace.Station) =>
        Spec(template) with { Dock = new DockSpec { Position = new Vector3(5000, 0, -3000.4f) }, Info = "A rock with a harbor." };

    /// <summary>Plans a base, checks that it can run and that each text of the plan is STE, and runs it.</summary>
    private static BasePlan Create(EditSession session, BaseSpec spec)
    {
        var plan = BasePlans.Create(session, spec);
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        AssertSte(plan);
        session.Execute(plan.Command!);
        return plan;
    }

    private static void AssertSte(BasePlan plan)
    {
        foreach (var text in plan.Warnings.Concat(plan.Refusals).Select(d => d.Message).Concat(plan.Fixes.Select(f => f.Text)))
            Assert.True(SteLint.Problems(text).Count == 0, $"{text}: {string.Join(", ", SteLint.Problems(text))}");
    }

    /// <summary>The names of the lines of a section in order.</summary>
    private static string[] Names(EditSession session, SectionRef section) =>
        session.Get(section)!.Entries.Select(e => e.Name).ToArray();

    private static string Value(EditSession session, SectionRef section, string key) =>
        Assert.Single(EditSessionTests.Values(session, section, key));

    private static string? Text(EditSession session, StringKind kind, string key) =>
        session.Text(new StringPlaceholder(kind, key));

    /// <summary>The sections of a file of W with their lines as text, to compare two states.</summary>
    private static string Dump(EditSession session, string path) => string.Join("\n", session.Sections(path).Select(s =>
        $"{s.Id}: " + string.Join("; ", session.Get(s)!.Entries.Select(e => $"{e.Name} = {string.Join(", ", e.Values)}"))));

    /// <summary>The names of the game for the tests: a bartender with three lines, a trader with one line, and a
    /// faction.</summary>
    private static readonly Dictionary<string, string> GameTexts = new()
    {
        ["1100"] = "Old Guild\nTest Station Bartender\nAda Vance",
        ["1101"] = "Kim Trader",
        ["3001"] = "Order of Tests",
    };

    /// <summary>A text source of the host that knows <see cref="GameTexts"/>.</summary>
    private static Func<SectionRef, string, string?> Texts(EditSession session) => (section, key) =>
        session.Get(section) is { } view && SceneSections.Last(view, key) is { } value &&
        GameTexts.TryGetValue(value, out var text)
            ? text
            : null;

    [Fact]
    public void TheEntryGoesBeforeTheFirstSystemWithTheRunByAndTerrainLinesOfTheTemplate()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, Spec(BaseWorkspace.Planet));

        var entry = new SectionRef(BaseWorkspace.UniversePath, new SectionIdentity("Base", New));
        Assert.Equal("Create base Ol01_05_Base", plan.Label);
        Assert.Equal(entry, plan.Base);
        Assert.Equal(["Base Ol01_01_Base", "Base Ol01_02_Base", "Base Ol02_01_Base", "Base Ol01_05_Base", "system Ol01",
            "system Ol02"], session.Sections(BaseWorkspace.UniversePath).Select(s => $"{s.Id.Type} {s.Id.Key}"));
        Assert.Equal(["nickname", "system", "strid_name", "file", "BGCS_base_run_by", "terrain_tiny", "terrain_sml"],
            Names(session, entry));
        Assert.Equal("Ol01", Value(session, entry, "system"));
        Assert.Equal("$str:ol01_05_base", Value(session, entry, "strid_name"));
        Assert.Equal(@"MODS\act\BASES\Ol01_05_Base.ini", Value(session, entry, "file"));
        Assert.Equal("ol_run_by_b", Value(session, entry, "BGCS_base_run_by"));
        Assert.Equal("ol_rock_tiny", Value(session, entry, "terrain_tiny"));
        Assert.Equal("Harbor Rock", Text(session, StringKind.String, "ol01_05_base"));
        Assert.Equal(plan.Base, plan.SelectAfter);
        Assert.Equal(
        [
            "The template Ol01_02_Base has no [MBase], so the new base has no bar characters and no dealers.",
            "No object docks with Ol01_05_Base yet. The player cannot dock there until an object of Ol01 docks with it.",
        ], plan.Warnings.Select(w => w.Message));
    }

    [Fact]
    public void TheBaseFileHasTheStartRoomTheValuesOfTheTemplateAndARoomForEachKeptRoom()
    {
        using var ws = Workspace();
        ws.WriteBase(BaseWorkspace.StationPath, """
            [BaseInfo]
            nickname = Ol01_01_Base
            start_room = Deck
            price_variance = 0.15
            ship_repair_cost = 0.5
            [Room]
            nickname = Deck
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_01_deck.ini
            [Room]
            nickname = Bar
            file = Universe\Systems\Ol01\Bases\Rooms\ol_shared_bar.ini
            """);
        var session = ws.Session("act");

        var plan = Create(session, Spec());

        Assert.Equal(NewFile, plan.File);
        Assert.Equal([NewFile], plan.Files);
        Assert.Empty(plan.Fixes);
        Assert.True(session.OpenDocument(NewFile)!.IsWholeFile);
        Assert.Equal(["BaseInfo Ol01_05_Base", "Room Deck", "Room Bar"],
            session.Sections(NewFile).Select(s => $"{s.Id.Type} {s.Id.Key}"));
        var info = session.Sections(NewFile)[0];
        Assert.Equal(["nickname", "start_room", "price_variance", "ship_repair_cost"], Names(session, info));
        Assert.Equal((New, "Deck", "0.15", "0.5"), (Value(session, info, "nickname"), Value(session, info, "start_room"),
            Value(session, info, "price_variance"), Value(session, info, "ship_repair_cost")));
        Assert.Equal(@"Universe\Systems\Ol01\Bases\Rooms\ol_shared_bar.ini",
            Value(session, session.Sections(NewFile)[2], "file"));

        var view = session.Base(New)!;
        Assert.Equal((BaseKind.Station, "Deck"), (view.Kind, view.StartRoom));
        Assert.Equal(["Deck", "Bar"], view.Rooms.Select(r => r.Nickname));
        Assert.All(view.Rooms, r => Assert.True(r.Shared));
        Assert.Equal([new SectionRef(BaseWorkspace.UniversePath, new SectionIdentity("Base", New)),
                new SectionRef(NewFile, new SectionIdentity("BaseInfo", New)),
                new SectionRef(NewFile, new SectionIdentity("Room", "Deck")),
                new SectionRef(NewFile, new SectionIdentity("Room", "Bar"))],
            plan.Adds.Take(4));
    }

    /// <summary>A file at the place of the new base file gives the base file the next free path.</summary>
    [Fact]
    public void TheBaseFileTakesTheNextFreePath()
    {
        using var ws = Workspace();
        ws.WriteBase(NewFile, "[BaseInfo]\nnickname = other\n");
        var session = ws.Session("act");

        var plan = Create(session, Spec());

        Assert.Equal(ContentPaths.NewBaseFile("act", "Ol01_05_Base_2"), plan.File);
        Assert.Equal(@"MODS\act\BASES\Ol01_05_Base_2.ini", Value(session, plan.Base!, "file"));
    }

    /// <summary>A kept room with a door to a room of the template that the base does not keep gets a copy of its room
    /// file without that door. The plan lists the copy in its fixes. The kept rooms take the letter case of the
    /// template.</summary>
    [Fact]
    public void ARoomWithADoorToARoomThatTheBaseDoesNotKeepGetsACopyWithoutTheDoor()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, Spec() with { Rooms = ["deck"] });

        var copy = ContentPaths.NewRoomFile("act", New, "Deck");
        Assert.Equal([NewFile, copy], plan.Files);
        Assert.True(session.OpenDocument(copy)!.IsWholeFile);
        Assert.Equal(["Room_Info", "PlayerShipPlacement", "Hotspot", "Hotspot", "Hotspot", "Hotspot"],
            session.Sections(copy).Select(s => s.Id.Type));
        Assert.DoesNotContain(session.Sections(copy), s => EditSessionTests.Values(session, s, "name").Contains("IDS_HOTSPOT_BAR"));
        var room = new SectionRef(NewFile, new SectionIdentity("Room", "Deck"));
        Assert.Equal(["Room Deck"], session.Sections(NewFile).Skip(1).Select(s => $"{s.Id.Type} {s.Id.Key}"));
        Assert.Equal(@"MODS\act\BASES\ROOMS\Ol01_05_Base_Deck.ini", Value(session, room, "file"));
        var fix = Assert.Single(plan.Fixes);
        Assert.Equal((room, "file"), (fix.Section, fix.Key));
        Assert.Equal("The room Deck uses a copy of its room file without the door to Bar, because the base does not keep " +
                     $"that room. The copy is {copy}.", fix.Text);

        // The bartender of the bar and the crowd of the bar stay out. The trader of the deck comes.
        Assert.Equal(["Ol01_05_Base_trader"], plan.Characters.Select(c => c.Id.Key));
        Assert.Equal(["MVendor", "BaseFaction", "GF_NPC", "MRoom Deck"],
            session.Base(New)!.MBaseParts.Select(p => p.Id.Key == null || p.Id.Type == "GF_NPC" ? p.Id.Type : $"{p.Id.Type} {p.Id.Key}"));
        Assert.Empty(EditSessionTests.Values(session, BaseWorkspace.MBasePart(session, New, "BaseFaction"), "npc"));
        Assert.DoesNotContain(BaseChecks.Problems(session, New), p => p.Message.StartsWith("The door", StringComparison.Ordinal));
    }

    /// <summary>The <c>[MBase]</c> starts with its nickname, has no <c>msg_id_prefix</c>, and its children follow in the
    /// retail order. A copy of a character keeps only its look, name, faction, voice and room. A faction row loses its
    /// mission lines.</summary>
    [Fact]
    public void TheMBaseHasItsNicknameFirstAndItsChildrenInTheRetailOrder()
    {
        using var ws = Workspace();
        ws.WriteBase(BaseWorkspace.MBasesPath, BaseWorkspace.MBases
            .Replace("diff = 3\n", "diff = 3\nmsg_id_prefix = gcs_refer_base_ol01_01\n")
            .Replace("weight = 100\n", "weight = 100\noffers_missions = true\nmission_type = DestroyMission, 0.1, 0.5, 30\n")
            .Replace("individual_name = 1100\n", "individual_name = 1100\nbribe = ol_grp, 1000, 5004\n" +
                                                 "rumor = mission_end, mission_end, 1, 5001\nknow = 5002, 5003, 1, 0\n" +
                                                 "misn = DestroyMission, 0.1, 0.5\n"));
        var session = ws.Session("act");

        var plan = Create(session, Spec());

        var mbase = plan.MBase!;
        Assert.Equal(new SectionRef(BaseWorkspace.MBasesPath, new SectionIdentity("MBase", New)), mbase);
        Assert.Equal(mbase, session.Sections(BaseWorkspace.MBasesPath).Last(s => s.Id.Type == "MBase"));
        Assert.Equal(["nickname", "local_faction", "diff"], Names(session, mbase));
        Assert.Equal(("ol_grp", "3"), (Value(session, mbase, "local_faction"), Value(session, mbase, "diff")));
        var view = session.Base(New)!;
        Assert.Equal(mbase, view.MBase);
        Assert.Equal(["MVendor", "BaseFaction", "GF_NPC", "GF_NPC", "GF_NPC", "MRoom", "MRoom"],
            view.MBaseParts.Select(p => p.Id.Type));
        Assert.Equal(view.MBaseParts, plan.Adds.Where(a => a.Id.Parent != null));
        Assert.Equal("2, 4", Value(session, view.MBaseParts[0], "num_offers"));
        Assert.Equal(["faction", "weight", "npc"], Names(session, view.MBaseParts[1]));
        Assert.Equal("Ol01_05_Base_npc_1", Value(session, view.MBaseParts[1], "npc"));
        Assert.Equal([
                ("Ol01_05_Base_bartender", "bartender", "Bar"), ("Ol01_05_Base_trader", "trader", "Deck"),
                ("Ol01_05_Base_npc_1", null, "Bar"),
            ],
            view.Characters.Select(c => (c.Nickname, c.Role, c.Room)));
        Assert.Equal(view.Characters.Select(c => c.Section), plan.Characters);
        Assert.Equal(["nickname", "body", "head", "lefthand", "righthand", "individual_name", "affiliation", "voice"],
            Names(session, plan.Characters[0]));
        Assert.Equal(["nickname", "body", "head", "lefthand", "righthand", "individual_name", "affiliation", "voice", "room"],
            Names(session, plan.Characters[2]));
        Assert.Equal(["nickname", "character_density", "fixture"], Names(session, view.MBaseParts[5]));
        Assert.Equal(@"Ol01_05_Base_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_bartender_fidget.thn, bartender",
            Value(session, view.MBaseParts[5], "fixture"));
        Assert.Empty(BaseChecks.Problems(session, New).Where(p => p.Message.Contains("[MBase]", StringComparison.Ordinal) ||
                                                              p.Message.StartsWith("The fixture", StringComparison.Ordinal) ||
                                                              p.Message.StartsWith("The faction", StringComparison.Ordinal) ||
                                                              p.Message.StartsWith("The character", StringComparison.Ordinal)));
    }

    /// <summary><see cref="BaseCharacters.Dealers"/> copies the characters of the fixtures. The <c>npc</c> line of the
    /// crowd goes with the crowd.</summary>
    [Fact]
    public void DealersCopiesTheFixturesAndLeavesOutTheCrowd()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, Spec() with { Characters = BaseCharacters.Dealers });

        Assert.Equal(["Ol01_05_Base_bartender", "Ol01_05_Base_trader"], plan.Characters.Select(c => c.Id.Key));
        Assert.Equal(["faction", "weight"], Names(session, BaseWorkspace.MBasePart(session, New, "BaseFaction")));
    }

    /// <summary><see cref="BaseCharacters.None"/> copies no character, and no fixture line names one.</summary>
    [Fact]
    public void NoneCopiesNoCharacters()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, Spec() with { Characters = BaseCharacters.None });

        Assert.Empty(plan.Characters);
        var view = session.Base(New)!;
        Assert.Equal(["MVendor", "BaseFaction", "MRoom", "MRoom"], view.MBaseParts.Select(p => p.Id.Type));
        Assert.All(view.MBaseParts.Skip(2), r => Assert.Empty(EditSessionTests.Values(session, r, "fixture")));
    }

    /// <summary>A bartender with a costume and no body keeps the costume.</summary>
    [Fact]
    public void ACharacterWithACostumeKeepsItsCostume()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, new BaseSpec("Ol02_05_Base", "Ol02", BaseWorkspace.Pirate, "Smugglers Den"));

        var bartender = Assert.Single(plan.Characters);
        Assert.Equal("Ol02_05_Base_bartender", bartender.Id.Key);
        Assert.Equal(["nickname", "base_appr", "individual_name", "affiliation", "voice"], Names(session, bartender));
        Assert.Equal(("ol_costume", "ol_pirates"), (Value(session, bartender, "base_appr"), Value(session, plan.MBase!, "local_faction")));
    }

    /// <summary>The faction of the request is the faction of the <c>[MBase]</c> and the reputation of the
    /// station.</summary>
    [Fact]
    public void TheFactionOfTheRequestReplacesTheFactionOfTheTemplate()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, Station() with { LocalFaction = "ol_pirates" });

        Assert.Equal("ol_pirates", Value(session, plan.MBase!, "local_faction"));
        Assert.Equal(["nickname", "local_faction", "diff"], Names(session, plan.MBase!));
        Assert.Equal("ol_pirates", Value(session, plan.DockObject!, "reputation"));
    }

    /// <summary>Each copied fixture character gets a name text of the mod with three lines: its faction, the name of the
    /// new base with the word of its role, and the person of the text of the template (the third line, else the last
    /// line). The crowd keeps its text.</summary>
    [Fact]
    public void EachFixtureGetsANameWithItsFactionTheTitleOfTheBaseAndThePerson()
    {
        using var ws = Workspace();
        ws.WriteBase(@"DATA\initialworld.ini", "[Group]\nnickname = ol_grp\nids_name = 3001\n[Group]\nnickname = ol_pirates\n");
        var session = ws.Session("act");

        var plan = Create(session, Spec() with { Text = Texts(session) });

        var (bartender, trader, crowd) = (plan.Characters[0], plan.Characters[1], plan.Characters[2]);
        Assert.Equal("$str:ol01_05_base_bartender.individual_name", Value(session, bartender, "individual_name"));
        Assert.Equal("Order of Tests\nHarbor Rock Bartender\nAda Vance",
            Text(session, StringKind.String, "ol01_05_base_bartender.individual_name"));
        Assert.Equal("Order of Tests\nHarbor Rock Commodity Trader\nKim Trader",
            Text(session, StringKind.String, "ol01_05_base_trader.individual_name"));
        Assert.Equal("1102", Value(session, crowd, "individual_name"));
        Assert.DoesNotContain(plan.Warnings, w => w.Message.Contains("cannot read", StringComparison.Ordinal));
    }

    /// <summary>Without a name of the faction, a text of the template with three lines gives the first line. A text
    /// with one line gives no faction line.</summary>
    [Fact]
    public void WithoutANameOfTheFactionTheFirstLineOfTheTemplateStays()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        Create(session, Spec() with { Text = Texts(session) });

        Assert.Equal("Old Guild\nHarbor Rock Bartender\nAda Vance",
            Text(session, StringKind.String, "ol01_05_base_bartender.individual_name"));
        Assert.Equal("Harbor Rock Commodity Trader\nKim Trader",
            Text(session, StringKind.String, "ol01_05_base_trader.individual_name"));
    }

    /// <summary>When the plan cannot read the text of the template, the character keeps the name text of the template,
    /// and the plan warns.</summary>
    [Fact]
    public void WithoutTheTextOfTheTemplateACharacterKeepsItsNameAndThePlanWarns()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, Spec());

        Assert.Equal("1100", Value(session, plan.Characters[0], "individual_name"));
        Assert.Equal("The editor cannot read the name of the character ol01_01_bartender of Ol01_01_Base. So its copy keeps " +
                     "that name text, and the game shows the title of Ol01_01_Base for it.",
            plan.Warnings.First(w => w.Message.Contains("cannot read", StringComparison.Ordinal)).Message);
        Assert.Equal(2, plan.Warnings.Count(w => w.Message.Contains("cannot read", StringComparison.Ordinal)));
    }

    /// <summary>A text of the mod in the template gives the name too, without a host.</summary>
    [Fact]
    public void ANameTextOfAModGivesTheNameWithoutAHost()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(ItemEdits.SetText(session, BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC",
            "ol01_01_bartender"), "individual_name", "Guild\nOld Bartender\nRay Ono"));

        var plan = Create(session, Spec());

        Assert.Equal("Guild\nHarbor Rock Bartender\nRay Ono",
            Text(session, StringKind.String, "ol01_05_base_bartender.individual_name"));
        Assert.Single(plan.Warnings, w => w.Message.Contains("cannot read", StringComparison.Ordinal));
    }

    /// <summary>A fixture character without a name text keeps none.</summary>
    [Fact]
    public void AFixtureWithoutANameKeepsNone()
    {
        using var ws = Workspace();
        ws.WriteBase(BaseWorkspace.MBasesPath, BaseWorkspace.MBases.Replace("individual_name = 1100\n", ""));
        var session = ws.Session("act");

        var plan = Create(session, Spec() with { Text = Texts(session) });

        Assert.Empty(EditSessionTests.Values(session, plan.Characters[0], "individual_name"));
        Assert.Null(Text(session, StringKind.String, "ol01_05_base_bartender.individual_name"));
    }

    /// <summary>Each news item of the template gets a copy at the end of news.ini with the new base in place of its base
    /// lines. A news item of another base does not.</summary>
    [Fact]
    public void EachNewsItemOfTheTemplateGetsACopyForTheNewBase()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var plan = Create(session, Spec());

        var items = session.Sections(BaseWorkspace.NewsPath);
        Assert.Equal(5, items.Count);
        Assert.Equal(items.Skip(3), plan.News);
        Assert.Equal(plan.News, plan.Adds.Where(a => a.Id.Type == "NewsItem"));
        Assert.All(plan.News, n =>
        {
            Assert.Equal(["rank", "icon", "logo", "headline", "text", "base"], Names(session, n));
            Assert.Equal(New, Value(session, n, "base"));
        });
        Assert.Equal(["2001", "2005"], plan.News.Select(n => Value(session, n, "headline")));
    }

    /// <summary>A save writes the news items as patch sections. The merge adds each one, because a news item has no
    /// key.</summary>
    [Fact]
    public void ASaveWritesTheNewsItemsAndTheMergeAddsThem()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Create(session, Spec());

        Assert.True(session.Save().Saved);
        session.Dispose();

        var patch = ws.ReadModPatch(BaseWorkspace.NewsPath);
        Assert.Equal(2, patch.Split("[NewsItem]").Length - 1);
        Assert.DoesNotContain("ol.", patch);
        var reopened = ws.Session("act");
        Assert.Equal(5, reopened.EditViewSections(BaseWorkspace.NewsPath)!.Count);
        Assert.Equal(New, reopened.EditViewSections(BaseWorkspace.NewsPath)![4]["base"]![0].ToString());
        Assert.False(reopened.IsDirty);
    }

    /// <summary>A hand-written patch section of news.ini adds a news item. Before news items became sections that a
    /// patch always adds, the merge refused such a patch, because it found no nickname. A delete of a news item
    /// stays an error.</summary>
    [Fact]
    public void APatchWithANewsItemAddsItAndCannotDeleteOne()
    {
        using var ws = Workspace();
        var mod = ws.Mod("news", new()
        {
            [@"patches\" + BaseWorkspace.NewsPath] = "[NewsItem]\nrank = mission_end, mission_end\nheadline = 2007\n" +
                                                     "text = 2008\nbase = Ol01_01_Base\n",
        });
        var broken = ws.Mod("news_delete", new()
        {
            [@"patches\" + BaseWorkspace.NewsPath] = "[NewsItem]\nol.delete = true\n",
        });

        var overlay = ws.Build(mod);
        var failed = ws.Build(broken);

        Assert.DoesNotContain(overlay.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var items = TestWorkspace.Sections(overlay, BaseWorkspace.NewsPath);
        Assert.Equal(4, items.Count);
        Assert.Equal("2007", items[3]["headline"]![0].ToString());
        Assert.Contains(failed.Diagnostics, d => d.Severity == DiagnosticSeverity.Error &&
                                                 d.Message.Contains("[NewsItem] sections can only be added", StringComparison.Ordinal));
    }

    /// <summary>A new station gets the base line, the dock line, the name of the base, its infocard and the lines of the
    /// station of the template, in the retail order. The games link the base to it.</summary>
    [Fact]
    public void AStationGetsTheBaseTheNameTheInfocardAndTheLinesOfTheTemplateStation()
    {
        using var ws = Workspace(fullSystem: true);
        var session = ws.Session("act");

        var plan = Create(session, Station() with { Dock = new DockSpec { Position = new Vector3(5000, 0, -3000.4f),
            Rotation = new Vector3(0, 90.4f, 0) } });

        var station = new SectionRef(BaseWorkspace.Ol01Path, new SectionIdentity("Object", "ol01_station_1"));
        Assert.Equal(station, plan.DockObject);
        Assert.Equal(station, plan.SelectAfter);
        Assert.Null(plan.Ring);
        Assert.Contains(station, plan.Adds);
        Assert.Equal(["nickname", "ids_name", "pos", "rotate", "archetype", "ids_info", "base", "dock_with", "loadout",
            "reputation", "voice", "space_costume", "pilot", "behavior", "difficulty_level"], Names(session, station));
        Assert.Equal(("5000, 0, -3000", "0, 90, 0", "ol_station"),
            (Value(session, station, "pos"), Value(session, station, "rotate"), Value(session, station, "archetype")));
        Assert.Equal(Value(session, plan.Base!, "strid_name"), Value(session, station, "ids_name"));
        Assert.Equal("$ifc:ol01_station_1.info", Value(session, station, "ids_info"));
        Assert.Equal(ItemEdits.ToInfocard("A rock with a harbor."), Text(session, StringKind.Infocard, "ol01_station_1.info"));
        Assert.Equal((New, New, "ol_station_loadout", "ol_grp", "ol_head, ol_body"),
            (Value(session, station, "base"), Value(session, station, "dock_with"), Value(session, station, "loadout"),
                Value(session, station, "reputation"), Value(session, station, "space_costume")));
        var view = session.Base(New)!;
        Assert.Equal(station, view.NamedObject);
        Assert.Equal(station, Assert.Single(view.DockObjects).Section);
        Assert.DoesNotContain(plan.Warnings, w => w.Message.StartsWith("No object docks", StringComparison.Ordinal));
    }

    /// <summary>A loadout names the hardpoints of an archetype, so a station of another archetype gets no loadout.</summary>
    [Fact]
    public void AStationOfAnotherArchetypeGetsNoLoadout()
    {
        using var ws = Workspace(fullSystem: true);
        var session = ws.Session("act");

        var plan = Create(session, Station() with { Dock = new DockSpec { Position = Vector3.Zero, Archetype = "ol_station_b",
            Nickname = "ol01_harbor" } });

        Assert.Equal("ol01_harbor", plan.DockObject!.Id.Key);
        Assert.Equal("ol_station_b", Value(session, plan.DockObject, "archetype"));
        Assert.Empty(EditSessionTests.Values(session, plan.DockObject, "loadout"));
        Assert.Equal("ol_voice_atc", Value(session, plan.DockObject, "voice"));
    }

    /// <summary>A new planet gets the base line and the name of the base. Its new ring docks with the base, keeps the
    /// name and the lines of the ring of the template, and goes at the height of that ring above the bigger planet, in
    /// the same direction. Retail rings have no base line.</summary>
    [Fact]
    public void ANewPlanetGetsTheBaseAndARingAtTheHeightOfTheTemplateRing()
    {
        using var ws = Workspace(fullSystem: true);
        var session = ws.Session("act");

        var plan = Create(session, Spec(BaseWorkspace.Planet) with
        {
            Dock = new DockSpec { Archetype = "ol_planet_big", Position = new Vector3(0, 0, -40000) },
        });

        var planet = new SectionRef(BaseWorkspace.Ol01Path, new SectionIdentity("Object", "ol01_planet_1"));
        var ring = new SectionRef(BaseWorkspace.Ol01Path, new SectionIdentity("Object", "dock_ring_ol01_planet_1"));
        Assert.Equal((planet, ring), (plan.DockObject, plan.Ring));
        Assert.Equal(["nickname", "ids_name", "pos", "archetype", "ids_info", "base"], Names(session, planet));
        Assert.Equal((New, "$str:ol01_05_base"), (Value(session, planet, "base"), Value(session, planet, "ids_name")));
        Assert.Equal(["nickname", "ids_name", "pos", "archetype", "dock_with", "behavior", "loadout", "reputation"],
            Names(session, ring));
        Assert.Equal(("1099", "0, 0, -36500", New), (Value(session, ring, "ids_name"), Value(session, ring, "pos"),
            Value(session, ring, "dock_with")));
        Assert.Equal("ol_ring_loadout", Value(session, ring, "loadout"));
        var view = session.Base(New)!;
        Assert.Equal(planet, view.NamedObject);
        Assert.Equal([planet, ring], view.DockObjects.Select(d => d.Section));
    }

    /// <summary>An existing planet without a base gets the base line, the name and the infocard of the base. The plan
    /// lists it as a change. The ring of the template comes to the planet.</summary>
    [Fact]
    public void AnExistingPlanetGetsTheBaseTheNameAndARing()
    {
        using var ws = Workspace(fullSystem: true);
        var session = ws.Session("act");

        var plan = Create(session, Spec(BaseWorkspace.Planet) with
        {
            Info = "A green world.", Dock = new DockSpec { Planet = "ol01_03" },
        });

        var planet = BaseWorkspace.Section(session, BaseWorkspace.Ol01Path, "Object", "Ol01_03");
        Assert.Equal([planet], plan.Changes);
        Assert.Equal(planet, plan.DockObject);
        Assert.Equal((New, "$str:ol01_05_base", "$ifc:ol01_03.info"),
            (Value(session, planet, "base"), Value(session, planet, "ids_name"), Value(session, planet, "ids_info")));
        Assert.Equal("-20000, 0, 2500", Value(session, plan.Ring!, "pos"));
        Assert.Equal(plan.Ring, Assert.Single(plan.Adds.Where(a => a.Id.Type == "Object")));
    }

    public static IEnumerable<object?[]> Refusals()
    {
        var dock = new DockSpec { Position = Vector3.Zero };
        object?[] Row(BaseSpec spec, string detail, string? part, string message) => [spec, detail, part, message];
        yield return Row(Spec() with { Nickname = "1base" }, "NameInvalid", "nickname",
            "The base nickname '1base' is not valid. Use 1 to 31 letters, digits or '_', and start with a letter. A " +
            "mission condition holds at most 31 characters of a base nickname.");
        yield return Row(Spec() with { Nickname = new string('a', 32) }, "NameInvalid", "nickname",
            $"The base nickname '{new string('a', 32)}' is not valid. Use 1 to 31 letters, digits or '_', and start with " +
            "a letter. A mission condition holds at most 31 characters of a base nickname.");
        yield return Row(Spec() with { Nickname = "Intro_harbor" }, "NameInvalid", "nickname",
            "A base nickname cannot start with 'intro'. The OpenLancer engine shows such a base in the main menu.");
        yield return Row(Spec() with { Nickname = "OL01_01_BASE" }, "NameTaken", "nickname",
            "The base nickname OL01_01_BASE is taken by the base Ol01_01_Base.");
        yield return Row(Spec() with { Nickname = "ol_hash_iehbialbib" }, "NameTaken", "nickname",
            "The base nickname ol_hash_iehbialbib has the FLHash of the base ol_hash_fialaeodje. The game keeps only one " +
            "base with an id.");
        yield return Row(Spec() with { Nickname = "Ol09_01_Base" }, "NameTaken", "nickname",
            "The base nickname Ol09_01_Base is taken by an [MBase] of mBases.ini.");
        yield return Row(Spec() with { System = "Ol09" }, "ItemNotFound", "system",
            "No [system] of universe.ini has the nickname Ol09. Get the systems from list_values with the list system.");
        yield return Row(Spec() with { Name = " " }, "FieldValue", "name",
            "The base needs a name. The game shows it in the base menus, in the trade lists and on the dock object.");
        yield return Row(Spec("Ol01_08_Base"), "ItemNotFound", "template",
            "No [Base] of universe.ini has the nickname Ol01_08_Base. Get the bases from list_values with the list base.");
        yield return Row(Spec("Intro1_Base"), "IntroTemplate", "template",
            "The base Intro1_Base is a scene of the main menu. Pick another template.");
        yield return Row(Spec("Ol01_09_Base"), "NoBaseFile", "template",
            "The base Ol01_09_Base has no base file with rooms. Pick a template with rooms.");
        yield return Row(Spec() with { Rooms = ["Hangar"] }, "RoomUnknown", "rooms",
            "The template Ol01_01_Base has no room Hangar. Its rooms are Deck, Bar.");
        yield return Row(Spec() with { Rooms = ["Bar"] }, "StartRoomNotKept", "start_room",
            "The start room Deck is not one of the rooms that the base keeps.");
        yield return Row(Spec() with { System = "Ol03", Dock = dock }, "NoSystemFile", "system",
            "The system 'Ol03' has no file in the edit view.");
        yield return Row(Spec() with { Dock = dock with { Archetype = "ol_none" } }, "DockArchetype", "dock_object.archetype",
            "No solar archetype has the name ol_none. Get the archetypes from list_values with the list solar_archetype.");
        yield return Row(Spec() with { Dock = dock with { Archetype = "ol_station_c" } }, "DockArchetype",
            "dock_object.archetype", "The archetype ol_station_c has no docking sphere, so the player cannot dock with it. " +
                                     "Pick a station archetype with a docking sphere.");
        yield return Row(Spec() with { Dock = dock with { Archetype = "ol_sun" } }, "DockArchetype", "dock_object.archetype",
            "The archetype ol_sun is a SUN. A dock object needs a station archetype with a docking sphere, or a planet.");
        yield return Row(Spec() with { Dock = new DockSpec { Planet = "Ol01_01" } }, "NotAPlanet", "dock_object.planet",
            "Ol01_01 is not a planet of Ol01.");
        yield return Row(Spec() with { Dock = new DockSpec { Planet = "Ol01_02" } }, "NotAPlanet", "dock_object.planet",
            "The planet Ol01_02 is the dock object of the base Ol01_02_Base. Pick a planet without a base.");
        yield return Row(Spec() with { Dock = new DockSpec() }, "DockPlace", "dock_object.position",
            "Give the position of the new station.");
        yield return Row(Spec() with { Dock = new DockSpec { Planet = "Ol01_03", Ring = new RingSpec() } }, "DockArchetype",
            "dock_object.ring.archetype", "Give the archetype of the docking ring. The template Ol01_01_Base has no docking " +
                                          "ring.");
        yield return Row(Spec() with { Dock = new DockSpec { Planet = "Ol01_03", Ring = new RingSpec("ol_ring") } },
            "DockPlace", "dock_object.ring.position", "Give the position of the docking ring.");
        yield return Row(Spec() with { Dock = dock with { Nickname = "ol01_01" } }, "NameTaken", "dock_object.nickname",
            "The nickname ol01_01 is taken by Ol01_01 in ol01.ini.");
    }

    /// <summary>Each refusal has its detail code, the part of the request and an STE text. The plan changes
    /// nothing.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ABadRequestIsRefused(BaseSpec spec, string detail, string? part, string message)
    {
        using var ws = Workspace(fullSystem: true);
        ws.WriteBase(BaseWorkspace.UniversePath, BaseWorkspace.Universe.Replace("[system]\nnickname = Ol01", """
            [Base]
            nickname = ol_hash_fialaeodje
            system = Ol01
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
        ws.WriteBase(BaseWorkspace.MBasesPath, BaseWorkspace.MBases + "\n[MBase]\nnickname = Ol09_01_Base\n");
        var session = ws.Session("act");

        var plan = BasePlans.Create(session, spec);

        Assert.False(plan.CanRun);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal((detail, part, message), (refusal.Rule, plan.RefusedPart, refusal.Message));
        Assert.Empty(SteLint.Problems(message));
        Assert.False(session.IsDirty);
    }

    /// <summary>One Undo removes the base file, the copies of room files, the patches of each file and the texts.</summary>
    [Fact]
    public void OneUndoRestoresEveryFileAndTheText()
    {
        using var ws = Workspace(fullSystem: true);
        var session = ws.Session("act");
        string[] paths = [BaseWorkspace.UniversePath, BaseWorkspace.MBasesPath, BaseWorkspace.NewsPath, BaseWorkspace.Ol01Path];
        var before = paths.Select(p => Dump(session, p)).ToList();

        var plan = Create(session, Station() with { Rooms = ["Deck"], Text = Texts(session) });

        Assert.Equal(2, plan.Files.Count);
        Assert.Equal("Create base Ol01_05_Base", session.UndoLabel);
        session.Undo();
        Assert.Equal(before, paths.Select(p => Dump(session, p)));
        Assert.All(plan.Files, f => Assert.False(session.OpenDocument(f)?.State.Exists == true));
        Assert.Null(Text(session, StringKind.String, "ol01_05_base"));
        Assert.Null(Text(session, StringKind.Infocard, "ol01_station_1.info"));
        Assert.Null(Text(session, StringKind.String, "ol01_05_base_trader.individual_name"));
        Assert.Null(session.Base(New));
        Assert.False(session.IsDirty);
        session.Redo();
        Assert.NotNull(session.Base(New));
    }

    /// <summary>A new base with a station has no error of the base checks, and the save gate takes it. After the save,
    /// the overlay of F5, the build of a retail target and the drop-in zip have the new <c>[Base]</c> before the first
    /// <c>[system]</c>, the new <c>[MBase]</c> with its nickname first and its children after it, and the new news
    /// items at the end of news.ini.</summary>
    [Fact]
    public void TheSavedBaseHasTheOrderOfTheGameInEachForm()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Create(session, Station());
        Assert.DoesNotContain(BaseChecks.Problems(session, New), p => p.Severity == DiagnosticSeverity.Error);

        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.DoesNotContain(result.Problems, p => p.Severity == DiagnosticSeverity.Error);
        var act = Mod.Load(ws.ModFolder("act"));
        var overlay = ws.Build(Mod.Load(ws.ModFolder("lower")), act);
        AssertOrder(path => TestWorkspace.Sections(overlay, path));
        var files = TargetBuild.Run(GameBase.Standard(ws.Base), [act], ModExport.DllName(act.Id),
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);
        AssertOrder(path => files.File(path)!.Merged!.Sections);
        var package = DropInPackage.Write(files, act.Manifest, ExportTarget.Vanilla);
        using var zip = new ZipArchive(new MemoryStream(package.Bytes!));
        AssertOrder(path =>
        {
            var entry = zip.Entries.Single(e => string.Equals(e.FullName, path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            using var stream = new MemoryStream();
            using (var read = entry.Open())
                read.CopyTo(stream);
            return IniDocument.Parse(path, stream.ToArray()).Sections;
        });
        Assert.Contains(zip.Entries, e => string.Equals(e.FullName, NewFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Checks the order of the new sections in the merged files.</summary>
    private static void AssertOrder(Func<string, IReadOnlyList<Section>> sections)
    {
        var universe = SectionIdentity.Identify(sections(BaseWorkspace.UniversePath));
        Assert.Equal(["Base Ol01_01_Base", "Base Ol01_02_Base", "Base Ol02_01_Base", "Base Ol01_05_Base", "system Ol01",
            "system Ol02"], universe.Select(s => $"{s.Type} {s.Key}"));
        var mbases = sections(BaseWorkspace.MBasesPath);
        var at = mbases.ToList().FindIndex(s => s.Name == "MBase" && s["nickname"]?[0].ToString() == New);
        Assert.Equal(mbases.Count - 8, at);
        Assert.Equal("nickname", mbases[at][0].Name);
        Assert.Equal(["MBase", "MVendor", "BaseFaction", "GF_NPC", "GF_NPC", "GF_NPC", "MRoom", "MRoom"],
            mbases.Skip(at).Select(s => s.Name));
        var news = sections(BaseWorkspace.NewsPath);
        Assert.Equal(5, news.Count);
        Assert.Equal([New, New], news.Skip(3).Select(s => s["base"]![0].ToString()));
    }

    /// <summary>The command of the editor and the agent gives the name texts from the texts of the game.</summary>
    [Fact]
    public void TheCommandTakesTheNamesFromTheTextsOfTheGame()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var texts = new ItemTexts(session, id => GameTexts.GetValueOrDefault(id.ToString(), ""), _ => "");

        var plan = BaseCommands.Create(session, Spec(), texts);

        Assert.True(plan.CanRun);
        session.Execute(plan.Command!);
        Assert.Equal("Old Guild\nHarbor Rock Bartender\nAda Vance",
            Text(session, StringKind.String, "ol01_05_base_bartender.individual_name"));
    }
}
