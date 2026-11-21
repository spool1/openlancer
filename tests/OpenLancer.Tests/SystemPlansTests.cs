using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A synthetic install for the new star systems, on top of <see cref="SceneWorkspace"/>. Li02 is a template system
/// with each header section, two lights, a sun away from the center with its death zone, and a nav map scale. Li01 has
/// no sun. A mission script names the system Av01, which the tests make. All names and values are invented.
/// </summary>
internal static class NewSystemWorkspace
{
    public const string MissionPath = @"DATA\MISSIONS\OL_SYS\ol_sys.ini";

    public const string Li02Text = """
        [SystemInfo]
        space_color = 0, 0, 0
        local_faction = li_n_grp
        [Archetype]
        solar = sun1
        [TexturePanels]
        file = universe\heavens\ol_panels.ini
        [Dust]
        spacedust = ol_dust
        [Music]
        space = ol_space
        danger = ol_danger
        battle = ol_battle
        [Ambient]
        color = 30, 30, 50
        [Background]
        basic_stars = solar\ol_stars.cmp
        [LightSource]
        nickname = Li02_light_1
        pos = 0, 0, 0
        color = 255, 255, 255
        range = 100000
        type = DIRECTIONAL
        atten_curve = curve1
        [LightSource]
        nickname = Li02_light_2
        pos = 10, 0, 0
        color = 200, 200, 255
        range = 50000
        type = POINT
        atten_curve = curve1
        [EncounterParameters]
        nickname = ol_scout
        filename = missions\encounters\ol_scout.ini
        [Object]
        nickname = Li02_to_Li01
        archetype = jumpgate
        goto = Li01, Li01_to_Li02, gate_tunnel_bretonia
        [Object]
        nickname = Li02_sun
        ids_name = 400100
        pos = 100, 0, 0
        ambient_color = 255, 255, 255
        archetype = sun1
        star = sun1
        atmosphere_range = 9000
        burn_color = 255, 200, 100
        ids_info = 400200
        visit = 128
        [Object]
        nickname = Li02_01
        archetype = station1
        dock_with = Li01_01_Base
        [zone]
        nickname = Zone_li02
        pos = 0, 0, 0
        shape = SPHERE
        size = 100
        [zone]
        nickname = Zone_Li02_sun_death
        pos = 100, 0, 0
        shape = SPHERE
        size = 8000
        damage = 10000
        sort = 99.5
        density = 0
        relief_time = 0
        """;

    public static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null)
    {
        var ws = SceneWorkspace.Create(lowerFiles);
        ws.WriteBase(ListWorkspace.UniversePath, """
            [Base]
            nickname = Li01_01_Base
            system = Li01
            [Base]
            nickname = Li02_01_Base
            system = Li02
            [system]
            nickname = Li01
            file = systems\Li01\li01.ini
            pos = 7, 8
            [system]
            nickname = Li02
            file = systems\Li02\li02.ini
            pos = 6, 9
            NavMapScale = 2.5
            """);
        ws.WriteBase(ListWorkspace.Li02Path, Li02Text);
        ws.WriteBase(MissionPath, """
            [Mission]

            [Trigger]
            nickname = ol_enter
            InitState = ACTIVE
            Cnd_SystemEnter = Av01
            Act_ChangeState = SUCCEED
            """);
        return ws;
    }

    public static SectionRef System(string nickname, int ordinal = 0) =>
        new(ListWorkspace.UniversePath, new SectionIdentity("system", nickname, Ordinal: ordinal));

    public static readonly string NewPath = ContentPaths.NewSystemFile("act", "Av01");

    public static SectionRef New(string type, string key) => new(NewPath, new SectionIdentity(type, key));

    /// <summary>Plans Av01 like Li02 at 4, 5 with the name Avalon, and runs the plan.</summary>
    public static UniversePlan CreateAv01(EditSession session, SystemOptions? options = null)
    {
        var plan = SystemPlans.Create(session, "Av01", "Li02", (4, 5), "Avalon", "A quiet border system.", options);
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        return plan;
    }
}

/// <summary>
/// The plans of the star systems: a new system with its universe lines, its file, its sun and the death zone of the
/// sun, and the rename of a system that the active mod adds. Each plan is one entry of the edit history. The data is
/// invented.
/// </summary>
public sealed class SystemPlansTests
{
    private static readonly SectionRef Av01 = NewSystemWorkspace.System("Av01");

    private static string Text(EditSession session, StringKind kind, string key) =>
        session.Text(new StringPlaceholder(kind, key))!;

    /// <summary>
    /// The new <c>[system]</c> has its lines in the retail order without <c>msg_id_prefix</c>. Its <c>file</c> value
    /// goes up from the folder of <c>universe.ini</c> to the folder of the mod, and the map of the system files resolves
    /// it to the new file. The texts go into the text of the active mod, and one undo takes everything back.
    /// </summary>
    [Fact]
    public void ANewSystemHasItsLinesInOrderAndItsFileInTheFolderOfTheMod()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");

        var plan = NewSystemWorkspace.CreateAv01(session);

        var view = session.Get(Av01)!;
        Assert.True(view.IsNew);
        Assert.Equal(["nickname", "file", "pos", "visit", "strid_name", "ids_info", "NavMapScale"],
            view.Entries.Select(e => e.Name));
        Assert.Equal([@"..\MODS\act\SYSTEMS\Av01\Av01.ini"], EditSessionTests.Values(session, Av01, "file"));
        Assert.Equal(["4, 5"], EditSessionTests.Values(session, Av01, "pos"));
        Assert.Equal(["0"], EditSessionTests.Values(session, Av01, "visit"));
        Assert.Equal(["$str:av01"], EditSessionTests.Values(session, Av01, "strid_name"));
        Assert.Equal(["$ifc:av01.info"], EditSessionTests.Values(session, Av01, "ids_info"));
        Assert.Equal(["2.5"], EditSessionTests.Values(session, Av01, "NavMapScale"));
        Assert.Equal(NewSystemWorkspace.NewPath, session.SystemPath("Av01"), ignoreCase: true);
        Assert.True(session.OpenDocument(NewSystemWorkspace.NewPath)!.IsWholeFile);
        Assert.Equal("Avalon", Text(session, StringKind.String, "av01"));
        Assert.Equal(ItemEdits.ToInfocard("A quiet border system."), Text(session, StringKind.Infocard, "av01.info"));

        Assert.Equal(Av01, plan.System);
        Assert.Equal(NewSystemWorkspace.NewPath, plan.File);
        Assert.Equal(NewSystemWorkspace.New("Object", "Av01_sun"), plan.Sun);
        Assert.Equal([Av01, plan.Sun!, NewSystemWorkspace.New("zone", "zone_Av01_sun_death")], plan.Adds);
        Assert.Contains(plan.Warnings, w => w.Message == "No jump gate or jump hole leads to Av01 yet.");
        Assert.False(plan.NeedsConfirmation);

        Assert.Equal("Create system Av01", session.UndoLabel);
        session.Undo();
        Assert.Null(session.Get(Av01));
        Assert.False(session.OpenDocument(NewSystemWorkspace.NewPath)!.State.Exists);
        Assert.Null(session.Text(new StringPlaceholder(StringKind.String, "av01")));
        Assert.Null(session.Text(new StringPlaceholder(StringKind.String, "av01_sun")));
        Assert.False(session.IsDirty);
        session.Redo();
        Assert.NotNull(session.Get(plan.Sun!));
    }

    /// <summary>The new file copies the header sections of the template in the order of the design, then the sun and
    /// its death zone. A light keeps its nickname, because a light name is unique only in its file.</summary>
    [Fact]
    public void TheNewFileCopiesTheHeaderSectionsOfTheTemplate()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");

        NewSystemWorkspace.CreateAv01(session);

        Assert.Equal(["SystemInfo", "Archetype", "TexturePanels", "Dust", "Music", "Ambient", "Background", "LightSource",
                "LightSource", "EncounterParameters", "Object", "zone"],
            session.Sections(NewSystemWorkspace.NewPath).Select(s => s.Id.Type));
        var info = session.Sections(NewSystemWorkspace.NewPath)[0];
        Assert.Equal(["li_n_grp"], EditSessionTests.Values(session, info, "local_faction"));
        Assert.Equal(["ol_space"], EditSessionTests.Values(session, session.Sections(NewSystemWorkspace.NewPath)[4], "space"));
        Assert.Equal(["100000"], EditSessionTests.Values(session, NewSystemWorkspace.New("LightSource", "Li02_light_1"), "range"));
        Assert.Equal([@"missions\encounters\ol_scout.ini"],
            EditSessionTests.Values(session, NewSystemWorkspace.New("EncounterParameters", "ol_scout"), "filename"));
    }

    /// <summary>
    /// The sun copies the first sun of the template, with its numeric infocard and a new name, at the center. It leaves
    /// out the lines that the design does not name, for example <c>visit</c>. The death zone copies the zone at the
    /// sun of the template, at the center.
    /// </summary>
    [Fact]
    public void TheSunCopiesTheFirstSunOfTheTemplateAndItsDeathZone()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");

        var plan = NewSystemWorkspace.CreateAv01(session);

        var sun = session.Get(plan.Sun!)!;
        Assert.Equal(["nickname", "ids_name", "pos", "ambient_color", "archetype", "star", "atmosphere_range", "burn_color",
            "ids_info"], sun.Entries.Select(e => e.Name));
        Assert.Equal(["$str:av01_sun"], EditSessionTests.Values(session, plan.Sun!, "ids_name"));
        Assert.Equal("Avalon Sun", Text(session, StringKind.String, "av01_sun"));
        Assert.Equal(["0, 0, 0"], EditSessionTests.Values(session, plan.Sun!, "pos"));
        Assert.Equal(["400200"], EditSessionTests.Values(session, plan.Sun!, "ids_info"));
        Assert.Equal(["9000"], EditSessionTests.Values(session, plan.Sun!, "atmosphere_range"));
        var zone = NewSystemWorkspace.New("zone", "zone_Av01_sun_death");
        Assert.Equal(["nickname", "pos", "shape", "size", "damage", "sort", "density", "relief_time"],
            session.Get(zone)!.Entries.Select(e => e.Name));
        Assert.Equal(["0, 0, 0"], EditSessionTests.Values(session, zone, "pos"));
        Assert.Equal(["8000"], EditSessionTests.Values(session, zone, "size"));
    }

    /// <summary>
    /// A template without a sun needs the archetype and the star of a sun. The sun then has only the lines of the row,
    /// and the death zone takes the defaults of the rule data. A sun archetype of another type refuses.
    /// </summary>
    [Fact]
    public void ATemplateWithoutASunNeedsASunRowAndTheZoneTakesTheDefaults()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");

        var refused = SystemPlans.Create(session, "Av01", "Li01", (4, 5), "Avalon", null);
        var wrongType = SystemPlans.Create(session, "Av01", "Li01", (4, 5), "Avalon", null,
            new SystemOptions { Sun = new SunRow("station1", "sun1") });
        var unknown = SystemPlans.Create(session, "Av01", "Li01", (4, 5), "Avalon", null,
            new SystemOptions { Sun = new SunRow("no_such_sun", "sun1") });
        var plan = SystemPlans.Create(session, "Av01", "Li01", (4, 5), "Avalon", null,
            new SystemOptions { Sun = new SunRow("sun1", "sun1", "Avalon Prime") });

        Assert.Equal(SystemPlans.LikeNoSun, Assert.Single(refused.Refusals).Rule);
        Assert.Equal("The system Li01 has no sun. Give the archetype and the star of the sun, or pick another system.",
            refused.Refusals[0].Message);
        Assert.Equal((SystemPlans.SunType, "The archetype station1 is a STATION. The sun needs an archetype of the type SUN."),
            (wrongType.Refusals[0].Rule, wrongType.Refusals[0].Message));
        Assert.StartsWith("No solar archetype has the name no_such_sun.", unknown.Refusals[0].Message);
        session.Execute(plan.Command!);
        Assert.Equal(["nickname", "ids_name", "pos", "archetype", "star"],
            session.Get(plan.Sun!)!.Entries.Select(e => e.Name));
        Assert.Equal("Avalon Prime", Text(session, StringKind.String, "av01_sun"));
        // Without an infocard text, the infocard shows the name.
        Assert.Equal(ItemEdits.ToInfocard("Avalon"), Text(session, StringKind.Infocard, "av01.info"));
        var zone = NewSystemWorkspace.New("zone", "zone_Av01_sun_death");
        Assert.Equal(["nickname", "pos", "shape", "size", "damage", "sort", "density", "relief_time"],
            session.Get(zone)!.Entries.Select(e => e.Name));
        Assert.Equal(["SPHERE", "10000", "10000", "99.5", "0", "0"],
            new[] { "shape", "size", "damage", "sort", "density", "relief_time" }
                .Select(k => Assert.Single(EditSessionTests.Values(session, zone, k))));
        // Li01 has no nav map scale, so the new system has none.
        Assert.Empty(EditSessionTests.Values(session, Av01, "NavMapScale"));
    }

    /// <summary>The radius of a default death zone is the atmosphere range of the sun less the margin of the rule
    /// data, as 25 of 27 retail death zones have it. Without a range, or with a range below the margin, it is the
    /// default radius.</summary>
    [Fact]
    public void TheDefaultDeathZoneFollowsTheAtmosphereRangeOfTheSun()
    {
        var rule = ListRules.Retail.Builders.SunZone!;

        Assert.Equal(8000, rule.RadiusFor(9000));
        Assert.Equal(10000, rule.RadiusFor(null));
        Assert.Equal(10000, rule.RadiusFor(500));
    }

    /// <summary>The option copy leaves out the parts that it does not name, the option local_faction replaces the
    /// faction of the template, and the other options set the visit flag and the nav map scale.</summary>
    [Fact]
    public void TheOptionsChooseThePartsTheFactionTheVisitAndTheScale()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");

        NewSystemWorkspace.CreateAv01(session, new SystemOptions
        {
            Copy = new HashSet<SystemPart> { SystemPart.Lights },
            LocalFaction = "li_p_grp",
            OnMapAtStart = true,
            NavMapScale = 3,
        });

        Assert.Equal(["SystemInfo", "LightSource", "LightSource", "Object", "zone"],
            session.Sections(NewSystemWorkspace.NewPath).Select(s => s.Id.Type));
        Assert.Equal(["li_p_grp"],
            EditSessionTests.Values(session, session.Sections(NewSystemWorkspace.NewPath)[0], "local_faction"));
        Assert.Equal(["0, 0, 0"],
            EditSessionTests.Values(session, session.Sections(NewSystemWorkspace.NewPath)[0], "space_color"));
        Assert.Equal(["1"], EditSessionTests.Values(session, Av01, "visit"));
        Assert.Equal(["3"], EditSessionTests.Values(session, Av01, "NavMapScale"));
    }

    /// <summary>
    /// The plan refuses a nickname that breaks the rule, a nickname whose FLHash another system has, a taken file, a
    /// place off the nav map, a template without a file and a zoom of 0. Each refusal names the problem.
    /// </summary>
    [Fact]
    public void ThePlanRefusesBadNicknamesTakenNamesAndFilesAndBadPlaces()
    {
        using var ws = NewSystemWorkspace.Create();
        ws.WriteBase(ContentPaths.NewSystemFile("act", "Av02"), "[SystemInfo]\nspace_color = 0, 0, 0\n");
        var session = ws.Session("act");
        UniversePlan Plan(string nickname, string like = "Li02", int x = 4, SystemOptions? options = null) =>
            SystemPlans.Create(session, nickname, like, (x, 5), "Avalon", null, options);

        foreach (var bad in new[] { "1Av", "A", "Av_0123456789012", "Av-1", "" })
            Assert.Equal(MissionErrors.NameInvalid, Assert.Single(Plan(bad).Refusals).Rule);
        Assert.True(Plan("Av_01234567890").CanRun);
        var taken = Plan("LI01");
        Assert.Equal((MissionErrors.NameTaken, "The system nickname LI01 is taken: Li01 has the same nickname or the same " +
                                               "FLHash."), (taken.Refusals[0].Rule, taken.Refusals[0].Message));
        Assert.Equal(NewSystemWorkspace.System("Li01"), taken.Refusals[0].Target);
        var file = Plan("Av02");
        Assert.Equal(MissionErrors.PathInUse, file.Refusals[0].Rule);
        Assert.StartsWith(@"The file DATA\MODS\act\SYSTEMS\Av02\Av02.ini exists already", file.Refusals[0].Message);
        Assert.Equal(SystemPlans.MapPosRange, Plan("Av01", x: 16).Refusals[0].Rule);
        Assert.Equal(SystemPlans.MapPosRange, Plan("Av01", x: -1).Refusals[0].Rule);
        Assert.Equal((SystemPlans.LikeNoFile, "The system Xx01 has no file in the edit view. Pick another system."),
            (Plan("Av01", "Xx01").Refusals[0].Rule, Plan("Av01", "Xx01").Refusals[0].Message));
        Assert.Equal(MissionErrors.FieldValue, Plan("Av01", options: new SystemOptions { NavMapScale = 0 }).Refusals[0].Rule);
        Assert.True(Plan("Av01", x: 15).CanRun);
    }

    /// <summary>
    /// The real flow: a save writes the new system file into the folder of the mod and the <c>[system]</c> into the
    /// patch of <c>universe.ini</c>. After a new open, the file is editable, and a second save has no error.
    /// </summary>
    [Fact]
    public void ASaveWritesTheNewSystemAndASecondSaveAfterANewOpenHasNoError()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        var saved = session.Save();

        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.DoesNotContain(saved.Problems, p => p.Severity == DiagnosticSeverity.Error);
        var file = Path.Combine(ws.ModFolder("act"), "files", "DATA", "MODS", "act", "SYSTEMS", "Av01", "Av01.ini");
        Assert.True(File.Exists(file));
        Assert.Contains("nickname = Av01_sun", File.ReadAllText(file));
        Assert.Contains(@"file = ..\MODS\act\SYSTEMS\Av01\Av01.ini", ws.ReadModPatch(ListWorkspace.UniversePath));
        session.Dispose();

        var again = ws.Session("act");
        Assert.Null(again.OpenDocument(NewSystemWorkspace.NewPath)!.ReadOnlyReason);
        Assert.Equal(NewSystemWorkspace.NewPath, again.SystemPath("Av01"), ignoreCase: true);
        again.Execute(EditCommand.SetLinesFromText(NewSystemWorkspace.New("Object", "Av01_sun"), "atmosphere_range", ["8000"]));
        var second = again.Save();
        Assert.True(second.Saved, string.Join("\n", second.Problems));
        Assert.DoesNotContain(second.Problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.Contains("atmosphere_range = 8000", File.ReadAllText(file));
    }

    /// <summary>The recovery file gives an unsaved new system back, with its file and its texts.</summary>
    [Fact]
    public void TheRecoveryFileGivesAnUnsavedNewSystemBack()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        Assert.Empty(session.WriteRecovery());
        session.Dispose();

        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        Assert.Empty(restored.RestoreRecovery());

        Assert.NotNull(restored.Get(Av01));
        Assert.NotNull(restored.Get(NewSystemWorkspace.New("Object", "Av01_sun")));
        Assert.Equal("Avalon", Text(restored, StringKind.String, "av01"));
        Assert.True(restored.Save().Saved);
    }

    /// <summary>
    /// A rename of a new system changes value 0 of each goto that names it, the system line of its bases and the mission
    /// lines, in one entry. The file keeps its path. The plan warns about save games, and it asks before it runs,
    /// because it changes other files.
    /// </summary>
    [Fact]
    public void ARenameChangesTheGotoLinesTheBasesAndTheMissionLines()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        var gate = ListWorkspace.Object(ListWorkspace.Li02Path, "Li02_to_Av01");
        var home = NewSystemWorkspace.New("Object", "Av01_to_Li02");
        var station = new SectionRef(ListWorkspace.UniversePath, new SectionIdentity("Base", "Av01_01_Base"));
        session.Execute(EditCommand.Group("Connect Av01",
        [
            EditCommand.AddSection(ListWorkspace.Li02Path, "Object",
            [
                ListWorkspace.Line("nickname", "Li02_to_Av01"), ListWorkspace.Line("archetype", "jumpgate"),
                ListWorkspace.Line("goto", "Av01", "Av01_to_Li02", "gate_tunnel_bretonia"),
            ]),
            EditCommand.AddSection(NewSystemWorkspace.NewPath, "Object",
            [
                ListWorkspace.Line("nickname", "Av01_to_Li02"), ListWorkspace.Line("archetype", "jumpgate"),
                ListWorkspace.Line("goto", "Li02", "Li02_to_Av01", "gate_tunnel_bretonia"),
            ]),
            EditCommand.AddSection(ListWorkspace.UniversePath, "Base",
                [ListWorkspace.Line("nickname", "Av01_01_Base"), ListWorkspace.Line("system", "Av01")]),
        ]));

        var plan = SystemPlans.Rename(session, "Av01", "Av02");
        session.Execute(plan.Command!);

        var renamed = NewSystemWorkspace.System("Av02");
        Assert.Equal(renamed, plan.System);
        Assert.NotNull(session.Get(renamed));
        Assert.Null(session.Get(Av01));
        Assert.Equal(["Av02, Av01_to_Li02, gate_tunnel_bretonia"], EditSessionTests.Values(session, gate, "goto"));
        Assert.Equal(["Li02, Li02_to_Av01, gate_tunnel_bretonia"], EditSessionTests.Values(session, home, "goto"));
        Assert.Equal(["Av02"], EditSessionTests.Values(session, station, "system"));
        var mission = session.OpenDocument(NewSystemWorkspace.MissionPath)!;
        Assert.Contains(mission.Working[1], e => e.Name == "Cnd_SystemEnter" && e[0].ToString() == "Av02");
        Assert.Equal(NewSystemWorkspace.NewPath, session.SystemPath("Av02"), ignoreCase: true);
        Assert.Equal(NewSystemWorkspace.NewPath, plan.File, ignoreCase: true);
        Assert.Equal($"A save game in Av01 does not find the system after the rename. The system file keeps its path " +
                     $"{session.SystemPath("Av02")}.", plan.Warnings[0].Message);
        Assert.True(plan.NeedsConfirmation);
        Assert.Contains(gate, plan.Changes);
        Assert.Contains(station, plan.Changes);
        Assert.Equal("Rename Av01 to Av02", session.UndoLabel);

        session.Undo();
        Assert.NotNull(session.Get(Av01));
        Assert.Equal(["Av01, Av01_to_Li02, gate_tunnel_bretonia"], EditSessionTests.Values(session, gate, "goto"));
        Assert.Equal(["Av01"], EditSessionTests.Values(session, station, "system"));
    }

    /// <summary>A rename that changes only <c>universe.ini</c> warns about save games, but it does not ask.</summary>
    [Fact]
    public void ARenameOfASystemWithoutReferencesDoesNotAsk()
    {
        using var ws = NewSystemWorkspace.Create();
        ws.WriteBase(NewSystemWorkspace.MissionPath, "[Mission]\n");
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        var plan = SystemPlans.Rename(session, "Av01", "Av02");

        Assert.True(plan.CanRun);
        Assert.False(plan.NeedsConfirmation);
        Assert.Single(plan.Warnings);
    }

    /// <summary>The editor renames only a system that the active mod adds. A system of the game or of a lower mod, a
    /// taken nickname, the same nickname and a nickname that breaks the rule refuse.</summary>
    [Fact]
    public void ARenameRefusesASystemOfTheGameOrOfALowerModAndBadNicknames()
    {
        using var ws = NewSystemWorkspace.Create(lowerFiles: new()
        {
            [@"patches\" + ListWorkspace.UniversePath] = "[system]\nol.new = true\nnickname = Lw01\nfile = systems\\Li01\\li01.ini\n",
        });
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        var game = SystemPlans.Rename(session, "Li01", "Li09");
        var lower = SystemPlans.Rename(session, "Lw01", "Lw09");

        Assert.Equal((SystemPlans.GameSystem, "Li01 is a system of the game or of a lower mod. This version renames only " +
                                              "systems that the active mod adds."),
            (game.Refusals[0].Rule, game.Refusals[0].Message));
        Assert.Equal(SystemPlans.GameSystem, lower.Refusals[0].Rule);
        Assert.Equal(MissionErrors.NameTaken, SystemPlans.Rename(session, "Av01", "li02").Refusals[0].Rule);
        Assert.Equal(MissionErrors.NameInvalid, SystemPlans.Rename(session, "Av01", "AV01").Refusals[0].Rule);
        Assert.Equal(MissionErrors.NameInvalid, SystemPlans.Rename(session, "Av01", "9x").Refusals[0].Rule);
        Assert.Equal(MissionErrors.ItemNotFound, SystemPlans.Rename(session, "Xx01", "Xx02").Refusals[0].Rule);
    }

    /// <summary>Plans a jump connection between Av01 and another system, and runs it.</summary>
    private static ConnectionPlan Connect(EditSession session, ConnectionKind kind, string other)
    {
        var plan = ConnectionPlans.CreatePair(session, kind, new ConnectionEnd("Av01"), new ConnectionEnd(other));
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        return plan;
    }

    /// <summary>
    /// A delete of a system that the active mod adds deletes the [system], the system file and each partner in another
    /// system, with the traffic zone of the partner. It always asks, and its deletes and its warning name the partners
    /// and the file. The mission line that names the system shows "Missing". One undo gives everything back.
    /// </summary>
    [Fact]
    public void ADeleteOfASystemDeletesItsFileAndThePartnersInTwoOtherSystems()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        var gate = Connect(session, ConnectionKind.Gate, "Li01");
        var hole = Connect(session, ConnectionKind.Hole, "Li03");

        var plan = SystemPlans.Delete(session, "av01");

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.True(plan.NeedsConfirmation);
        Assert.Equal("Delete system Av01", plan.Label);
        Assert.Equal(Av01, plan.System);
        Assert.Equal(NewSystemWorkspace.NewPath, plan.File);
        Assert.Equal([(Av01, DeleteReason.Target, null),
                (gate.Ends[1].Object, DeleteReason.Cascade, "goto = Av01, Av01_to_Li01"),
                (hole.Ends[1].Object, DeleteReason.Cascade, "goto = Av01, Av01_to_Li03_hole"),
                (gate.TrafficZones[1], DeleteReason.Cascade, "traffic zone of Li01_to_Av01"),
                (hole.TrafficZones[1], DeleteReason.Cascade, "traffic zone of Li03_to_Av01_hole")],
            plan.Deletes.Select(d => (d.Section, d.Why, d.Cause)));
        Assert.Equal("A save game in Av01 does not find the system after the delete. The system file " +
                     $"{NewSystemWorkspace.NewPath} goes too.", plan.Warnings[0].Message);
        Assert.Contains(plan.MissionMissing, m => m.Key == "Cnd_SystemEnter");

        session.Execute(plan.Command!);
        Assert.Null(session.Get(Av01));
        Assert.False(session.OpenDocument(NewSystemWorkspace.NewPath)?.State.Exists ?? false);
        Assert.All([gate.Ends[1].Object, hole.Ends[1].Object, gate.TrafficZones[1], hole.TrafficZones[1]],
            s => Assert.Null(session.Get(s)));
        Assert.Null(session.Routes.Graph.System("Av01"));
        Assert.Equal(plan.Label, session.UndoLabel);

        session.Undo();
        Assert.NotNull(session.Get(Av01));
        Assert.True(session.OpenDocument(NewSystemWorkspace.NewPath)!.State.Exists);
        Assert.All([gate.Ends[0].Object, gate.Ends[1].Object, hole.Ends[0].Object, hole.Ends[1].Object],
            s => Assert.NotNull(session.Get(s)));
    }

    /// <summary>A system with a base refuses the delete, because this version has no delete of a base. A system of the
    /// game and a system that does not exist refuse too.</summary>
    [Fact]
    public void ADeleteRefusesASystemWithABaseAndASystemOfTheGame()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        session.Execute(EditCommand.AddSection(ListWorkspace.UniversePath, "Base",
            [ListWorkspace.Line("nickname", "Av01_01_Base"), ListWorkspace.Line("system", "Av01")]));

        var withBase = SystemPlans.Delete(session, "Av01");
        var game = SystemPlans.Delete(session, "Li01");

        Assert.Equal((SystemPlans.HasBases, "Av01 has the base Av01_01_Base. This version cannot delete a system with " +
                                            "bases."), (withBase.Refusals[0].Rule, withBase.Refusals[0].Message));
        Assert.False(withBase.CanRun);
        Assert.Equal((SystemPlans.GameSystem, "Li01 is a system of the game or of a lower mod. This version deletes only " +
                                              "systems that the active mod adds."),
            (game.Refusals[0].Rule, game.Refusals[0].Message));
        Assert.Equal(MissionErrors.ItemNotFound, SystemPlans.Delete(session, "Xx01").Refusals[0].Rule);
    }

    /// <summary>A new [system] that names a file of the game deletes only its [system], and the warning tells why the
    /// file stays. A new [system] without a file deletes only its [system] too.</summary>
    [Fact]
    public void ADeleteKeepsAFileThatTheModDoesNotAdd()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(ListWorkspace.UniversePath, "system",
            [ListWorkspace.Line("nickname", "Xw01"), ListWorkspace.Line("file", @"systems\Li01\li01.ini")]));
        session.Execute(EditCommand.AddSection(ListWorkspace.UniversePath, "system",
            [ListWorkspace.Line("nickname", "Xw02"), ListWorkspace.Line("file", @"systems\Xw02\xw02.ini")]));

        var shared = SystemPlans.Delete(session, "Xw01");
        var missing = SystemPlans.Delete(session, "Xw02");

        Assert.True(shared.CanRun, string.Join("\n", shared.Refusals.Select(r => r.Message)));
        Assert.Null(shared.File);
        Assert.Equal([NewSystemWorkspace.System("Xw01")], shared.Deletes.Select(d => d.Section));
        Assert.Equal("A save game in Xw01 does not find the system after the delete. The file " +
                     $@"{ListWorkspace.Li01Path} stays, because it is no file that the active mod adds in its folder " +
                     @"DATA\MODS\act.", shared.Warnings[0].Message);
        Assert.Equal("A save game in Xw02 does not find the system after the delete.", missing.Warnings[0].Message);
        Assert.Null(missing.File);
        session.Execute(shared.Command!);
        Assert.Null(session.Get(NewSystemWorkspace.System("Xw01")));
        Assert.True(session.OpenDocument(ListWorkspace.Li01Path)!.State.Exists);
        Assert.NotNull(session.Get(SceneWorkspace.Object("Li01_to_Li02")));
    }

    /// <summary>A delete of a saved system, then a save, removes the file from the mod and the [system] from the patch.
    /// An undo of the delete and a save write both again.</summary>
    [Fact]
    public void ASaveAfterTheDeleteRemovesTheFileAndASaveAfterTheUndoWritesItAgain()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        Assert.True(session.Save().Saved);
        var file = Path.Combine(ws.ModFolder("act"), "files", "DATA", "MODS", "act", "SYSTEMS", "Av01", "Av01.ini");
        Assert.True(File.Exists(file));

        var plan = SystemPlans.Delete(session, "Av01");
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        var deleted = session.Save();

        Assert.True(deleted.Saved, string.Join("\n", deleted.Problems));
        Assert.False(File.Exists(file));
        var patch = Path.Combine(ws.ModFolder("act"), OpenLancer.Data.Mods.Mod.PatchesFolder, ListWorkspace.UniversePath);
        Assert.False(File.Exists(patch) && File.ReadAllText(patch).Contains("Av01", StringComparison.Ordinal));

        session.Undo();
        var restored = session.Save();
        Assert.True(restored.Saved, string.Join("\n", restored.Problems));
        Assert.DoesNotContain(restored.Problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.True(File.Exists(file));
        Assert.Contains(@"file = ..\MODS\act\SYSTEMS\Av01\Av01.ini", ws.ReadModPatch(ListWorkspace.UniversePath));
    }
}
