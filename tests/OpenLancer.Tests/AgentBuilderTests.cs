using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The builder tools of section 7.3 of the MCP1 design (section 13.1, item 13): <c>create_loadout</c>,
/// <c>scene_place_wreck</c>, <c>scene_place_patrol_route</c> and <c>scene_set_encounters</c> (decisions D730, D731 and
/// D732). Each call is one entry with the edit origin Agent. The tests also run workflow 2 of section 9.5 on a
/// synthetic profile.
/// </summary>
public sealed class AgentBuilderTests
{
    /// <summary>The builder tools of this build.</summary>
    internal static readonly string[] Tools =
        ["scene_place_wreck", "scene_place_patrol_route", "scene_set_encounters", "create_loadout"];

    private static readonly SectionRef Pop = SceneWorkspace.Zone("Zone_pop");

    private static (TestWorkspace Ws, EditSession Session, TestAgentHost Host) Builders()
    {
        var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");
        return (ws, session, new TestAgentHost(session));
    }

    /// <summary>A wreck is one entry. The entry adds a new loadout with the ship archetype, the equip rows and the cargo
    /// rows. It also adds a new object with the archetype, the loadout line, the lines of the wreck rule, its name and
    /// its infocard. The preview gives the same adds and changes nothing.</summary>
    [Fact]
    public void AWreckIsOneEntryWithOneLoadoutOfTheShipAndOneObject()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;
        var texts = new ItemTexts(session, _ => "", _ => "");
        const string wreck = """
            "system": "Li01", "position": [8000, 500, 2000], "archetype": "wreck_freighter", "ship_archetype": "ship1",
            "loot": [{ "good": "commodity_gold", "count": 20 }], "equipment": [{ "equipment": "gun1", "hardpoint": "HpWeapon01" }],
            "nickname": "my_wreck_1", "name": "Lost Freighter"
            """;

        var preview = Ok(host, "scene_place_wreck", $$"""{ {{wreck}}, "preview": true }""");
        var result = Ok(host, "scene_place_wreck", $$"""{ {{wreck}} }""");

        var obj = SceneWorkspace.Object("my_wreck_1");
        var loadout = BuilderWorkspace.Loadout("my_wreck_1_loadout");
        string[] adds = ["obj:my_wreck_1", LoadoutRef(session, "my_wreck_1_loadout")];
        Assert.Equal(adds, Texts(preview["adds"]));
        Assert.Equal(adds, Texts(result["adds"]));
        Assert.Empty(result["changes"]!.AsArray());
        Assert.Equal(("Agent: Place wreck my_wreck_1", "1", "obj:my_wreck_1"),
            ((string?)result["undo_label"], (string?)result["rev"], (string?)result["select"]));
        Assert.Empty(result["new_problems"]!.AsArray());
        Assert.Equal(("wreck_freighter", "my_wreck_1_loadout", "16", "8000, 500, 2000"),
            (Line(session, obj, "archetype"), Line(session, obj, "loadout"), Line(session, obj, "visit"),
                Line(session, obj, "pos")));
        Assert.Equal(("ship1", "gun1, HpWeapon01", "commodity_gold, 20"),
            (Line(session, loadout, "archetype"), Line(session, loadout, "equip"), Line(session, loadout, "cargo")));
        Assert.Equal("Lost Freighter", texts.TextOf(obj, "ids_name"));
        Assert.Contains("Shoot the wreck to free its cargo.", texts.TextOf(obj, "ids_info"));
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);

        session.Undo();
        Assert.Null(session.Get(obj));
        Assert.Null(session.Get(loadout));
        Assert.False(session.CanUndo);
    }

    /// <summary>The values of a wreck. An archetype of another type gives invalid_value with NotAWreck. Each row whose
    /// list does not have its value gives invalid_value with the row. A nickname in use gives name_taken. A wreck
    /// without a name gets the name of the wreck rule.</summary>
    [Fact]
    public void TheValuesOfAWreckComeFromTheirListsAndTheRuleData()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;
        var texts = new ItemTexts(session, _ => "", _ => "");

        var type = Error(host, "scene_place_wreck",
            """{ "system": "Li01", "position": [0, 0, 0], "archetype": "station1", "ship_archetype": "ship1" }""");
        var rows = Error(host, "scene_place_wreck", """
            { "system": "Li01", "position": [0, 0, 0], "archetype": "wreck_freighter", "ship_archetype": "ship1",
              "loot": [{ "good": "commodity_gold" }, { "good": "goldd" }], "equipment": [{ "equipment": "gun2" }] }
            """);
        var taken = Error(host, "scene_place_wreck",
            """{ "system": "Li01", "position": [0, 0, 0], "archetype": "wreck_freighter", "ship_archetype": "ship1", "nickname": "Li01_01" }""");
        var made = Ok(host, "scene_place_wreck",
            """{ "system": "Li01", "position": [0, 0, 0], "archetype": "WRECK_FREIGHTER", "ship_archetype": "SHIP1" }""");

        Assert.Equal(("invalid_value", WreckCommands.NotAWreck), (type.Code, type.Detail));
        Assert.Equal(("invalid_value", "FieldValue"), (rows.Code, rows.Detail));
        Assert.StartsWith("equipment[0].equipment: No equipment has the name 'gun2'.", rows.Message);
        Assert.Contains("loot[1].good: No equipment has the name 'goldd'.", rows.Message);
        Assert.Equal("name_taken", taken.Code);
        var obj = SceneWorkspace.Object("li01_wreck_1");
        Assert.Equal(["obj:li01_wreck_1", LoadoutRef(session, "li01_wreck_1_loadout")],
            Texts(made["adds"]));
        Assert.Equal(("wreck_freighter", "Wreck"), (Line(session, obj, "archetype"), texts.TextOf(obj, "ids_name")));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>
    /// A route has one cylinder zone for each leg, with the leg numbers in order (section 7.3 of the MCP1 design). Each
    /// zone has its center at the middle of its leg, its local Y axis along the leg and the size radius, length. So the
    /// end of each leg is the start of the next leg. The fields of the request go to each zone.
    /// </summary>
    [Fact]
    public void ARouteHasOneZoneForEachLegAndTheEndsOfNextLegsMeet()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;
        Vector3[] points = [new(0, 0, 0), new(15000, 0, 0), new(15000, 2000, 15000), new(-3333.3f, 100, 7777.7f)];
        var json = string.Join(", ", points.Select(p => $"[{Number(p.X)}, {Number(p.Y)}, {Number(p.Z)}]"));

        var result = Ok(host, "scene_place_patrol_route", $$"""
            {
              "system": "Li01", "name": "my_patrol", "points": [{{json}}], "radius": 500,
              "encounters": [{ "encounter": "patrol_a", "level": 4, "chance": 0.4, "factions": [{ "faction": "li_n_grp", "weight": 1 }] }],
              "fields": { "toughness": 9 }
            }
            """);

        string[] zones = ["zone:li01_path_my_patrol_1", "zone:li01_path_my_patrol_2", "zone:li01_path_my_patrol_3"];
        Assert.Equal(zones, Texts(result["adds"]));
        Assert.Equal(("Agent: Place route my_patrol", "zone:li01_path_my_patrol_1"),
            ((string?)result["undo_label"], (string?)result["select"]));
        Assert.Empty(result["new_problems"]!.AsArray());
        for (var leg = 1; leg <= zones.Length; leg++)
        {
            var zone = SceneWorkspace.Zone($"li01_path_my_patrol_{leg}");
            var (start, end) = Ends(session, zone);
            Assert.Equal(($"my_patrol, {leg}", "patrol", "CYLINDER", "9"),
                (Line(session, zone, "path_label"), Line(session, zone, "usage"), Line(session, zone, "shape"),
                    Line(session, zone, "toughness")));
            Assert.StartsWith("500, ", Line(session, zone, "size"));
            Assert.Equal(("patrol_a, 4, 0.4", "li_n_grp, 1"), (Line(session, zone, "encounter"), Line(session, zone, "faction")));
            // The editor rounds a position and a length to whole meters, so an end moves by less than 2 meters.
            Assert.True(Vector3.Distance(start, points[leg - 1]) < 2, $"leg {leg}: start {start}");
            Assert.True(Vector3.Distance(end, points[leg]) < 2, $"leg {leg}: end {end}");
            if (leg > 1)
                Assert.True(Vector3.Distance(Ends(session, SceneWorkspace.Zone($"li01_path_my_patrol_{leg - 1}")).End, start) < 3);
        }
        Assert.Equal(1, session.Revision);
    }

    /// <summary>A path label that the system has gives name_taken. A route with more legs than the limit gives limit,
    /// and the limit is a setting (decision D724 of the MCP1 design). A trade route takes the template of its
    /// usage.</summary>
    [Fact]
    public void ARouteRefusesAPathLabelInUseAndMoreLegsThanTheLimit()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;
        string Points(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"[{i * 1000}, 0, 0]"));

        var taken = Error(host, "scene_place_patrol_route",
            $$"""{ "system": "Li01", "name": "old_route", "points": [{{Points(2)}}] }""");
        var many = Error(host, "scene_place_patrol_route",
            $$"""{ "system": "Li01", "name": "long_route", "points": [{{Points(52)}}] }""");
        var fifty = Ok(host, "scene_place_patrol_route",
            $$"""{ "system": "Li01", "name": "long_route", "points": [{{Points(51)}}], "usage": "trade", "preview": true }""");
        host.TestLimits = new AgentLimits { RouteLegs = 2 };
        var setting = Error(host, "scene_place_patrol_route",
            $$"""{ "system": "Li01", "name": "long_route", "points": [{{Points(4)}}] }""");
        var trade = Ok(host, "scene_place_patrol_route",
            $$"""{ "system": "Li01", "name": "trade_route", "points": [{{Points(3)}}], "usage": "trade" }""");

        Assert.Equal(("name_taken", MissionErrors.NameTaken), (taken.Code, taken.Detail));
        Assert.Equal(("limit", BuilderTools.RouteLegs, "One route can have at most 50 legs, and this route has 51. Split the route."),
            many);
        Assert.Equal(50, fifty["adds"]!.AsArray().Count);
        Assert.Equal(("limit", "One route can have at most 2 legs, and this route has 3. Split the route."),
            (setting.Code, setting.Message));
        var zone = SceneWorkspace.Zone("li01_path_trade_route_1");
        Assert.Equal(("trade", "trade_path", "750, 1000"),
            (Line(session, zone, "usage"), Line(session, zone, "pop_type"), Line(session, zone, "size")));
        Assert.Equal(2, trade["adds"]!.AsArray().Count);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>Each coordinate of a point of a builder is at most the far distance of the Scene camera. So the length
    /// and the angles of a leg stay finite. The argument fields cannot give a key that the route tool writes itself.
    /// The error names the key and the argument that gives it.</summary>
    [Fact]
    public void ARouteRefusesFarPointsAndAKeyThatItWritesInFields()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;
        const string route = """ "system": "Li01", "name": "my_patrol", "points": [[0, 0, 0], [5000, 0, 0]] """;

        var far = Call(host, "scene_place_patrol_route",
            """{ "system": "Li01", "name": "far", "points": [[-3e38, 0, 0], [3e38, 0, 0]] }""");
        var wreck = Call(host, "scene_place_wreck", """
            { "system": "Li01", "position": [20000000, 0, 0], "archetype": "wreck_freighter", "ship_archetype": "ship1" }
            """);
        var shape = Call(host, "scene_place_patrol_route", $$"""{ {{route}}, "fields": { "toughness": 5, "shape": "SPHERE" } }""");
        var usage = Call(host, "scene_place_patrol_route", $$"""{ {{route}}, "fields": { "USAGE": "trade" } }""");

        Assert.Equal(("invalid_argument", "points"), ((string?)far.Content["code"], (string?)far.Content["field"]));
        Assert.Equal("The argument 'points' needs a list of 2 or more points, each three numbers from -10000000 to 10000000.",
            (string?)far.Content["message"]);
        Assert.Equal(("invalid_argument", "position"), ((string?)wreck.Content["code"], (string?)wreck.Content["field"]));
        Assert.Equal(("invalid_argument", ToolInput.BadArgument, "fields.shape",
                "The tool writes the key shape of each zone of the route. Remove the key from fields."),
            ((string?)shape.Content["code"], (string?)shape.Content["detail"], (string?)shape.Content["field"],
                (string?)shape.Content["message"]));
        Assert.Equal(("fields.USAGE", "The tool writes the key USAGE of each zone of the route. Remove the key from " +
                                      "fields, and use the argument usage."),
            ((string?)usage.Content["field"], (string?)usage.Content["message"]));
        Assert.Equal(0, session.Revision);
    }

    /// <summary>
    /// The encounters of a zone (decision D732 of the MCP1 design). Each faction_weight and density_restriction line
    /// comes before the first encounter line. Each faction line comes after its encounter line. An encounter that
    /// only another system has adds a copy of its [EncounterParameters] section, as a fix of the plan.
    /// </summary>
    [Fact]
    public void TheEncountersOfAZoneKeepEachFactionLineAfterItsEncounterLine()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;

        var result = Ok(host, "scene_set_encounters", """
            {
              "zone": "zone:Zone_pop",
              "encounters": [
                { "encounter": "patrol_b", "level": 2, "chance": 0.5, "factions": [{ "faction": "li_p_grp", "weight": 1 }] },
                { "encounter": "patrol_a", "level": 6, "chance": 1, "factions": [{ "faction": "li_n_grp", "weight": 2 }, { "faction": "li_p_grp", "weight": 1 }] }
              ],
              "faction_weights": [{ "faction": "li_n_grp", "weight": 3 }, { "faction": "li_p_grp", "weight": 1 }],
              "density_restrictions": [{ "count": 1, "class": "lawful" }]
            }
            """);

        var names = session.Get(Pop)!.Entries.Select(e => e.Name).ToList();
        var first = names.IndexOf("encounter");
        Assert.Equal(["faction_weight", "faction_weight", "density_restriction", "encounter", "faction", "encounter", "faction",
            "faction"], names.Skip(first - 3).Take(8));
        Assert.Equal("toughness", names[^1]);
        Assert.Equal(["zone:Zone_pop"], Texts(result["changes"]));
        var parameters = ItemRefs.Canonical(session, SceneWorkspace.Li01("EncounterParameters", "patrol_b"));
        Assert.Equal([parameters], Texts(result["adds"]));
        Assert.Equal(parameters, (string?)result["fixes"]![0]!["ref"]);
        Assert.Equal("Agent: Set the encounters of Zone_pop", (string?)result["undo_label"]);
        Assert.Empty(result["new_problems"]!.AsArray());

        session.Undo();
        Assert.Equal(["patrol_a, 3, 0.5"], session.Get(Pop)!.Lines("encounter").Select(l => FieldEdit.FormatLine(l.Values)));
    }

    /// <summary>The errors of scene_set_encounters name their row. A list that the call does not give keeps its lines,
    /// and an empty list of encounter groups removes them.</summary>
    [Fact]
    public void TheEncounterErrorsNameTheirRowAndAMissingListKeepsItsLines()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;

        var faction = Error(host, "scene_set_encounters", """
            { "zone": "zone:Zone_pop", "encounters": [{ "encounter": "patrol_a", "level": 1, "chance": 0.5, "factions": [{ "faction": "li_q_grp", "weight": 1 }] }] }
            """);
        var chance = Error(host, "scene_set_encounters", """
            { "zone": "zone:Zone_pop", "encounters": [{ "encounter": "patrol_a", "level": 1, "chance": 2, "factions": [{ "faction": "li_n_grp", "weight": 1 }] }] }
            """);
        var notZone = Error(host, "scene_set_encounters", """{ "zone": "obj:Li01_01", "encounters": [] }""");
        Ok(host, "scene_set_encounters", """{ "zone": "zone:Zone_pop", "encounters": [] }""");

        Assert.Equal(("invalid_value", "FieldValue"), (faction.Code, faction.Detail));
        Assert.StartsWith("encounters[0].factions[0].faction: No faction has the name 'li_q_grp'.", faction.Message);
        Assert.Equal(("invalid_argument", ToolInput.BadArgument), (chance.Code, chance.Detail));
        Assert.Equal("refused", notZone.Code);
        Assert.Equal(["faction_weight", "density_restriction"],
            session.Get(Pop)!.Entries.Select(e => e.Name).Where(n => n.StartsWith("faction") || n.StartsWith("density") ||
                                                                     n.StartsWith("encounter")));
    }

    [Fact]
    public void ALoadoutIsOneEntryAndANicknameInUseGivesNameTaken()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;

        var result = Ok(host, "create_loadout", """
            { "nickname": "my_loadout", "archetype": "ship1", "equipment": [{ "equipment": "gun1", "hardpoint": "HpWeapon02" }],
              "cargo": [{ "good": "commodity_gold" }] }
            """);
        var taken = Error(host, "create_loadout", """{ "nickname": "lo_station", "archetype": "ship1" }""");
        var row = Error(host, "create_loadout",
            """{ "nickname": "my_other", "archetype": "ship1", "equipment": [{ "equipment": "gun1" }, { "equipment": "x1" }] }""");

        var loadout = BuilderWorkspace.Loadout("my_loadout");
        Assert.Equal([LoadoutRef(session, "my_loadout")], Texts(result["adds"]));
        Assert.Equal("Agent: Create my_loadout", (string?)result["undo_label"]);
        Assert.Equal(("ship1", "gun1, HpWeapon02", "commodity_gold, 1"),
            (Line(session, loadout, "archetype"), Line(session, loadout, "equip"), Line(session, loadout, "cargo")));
        Assert.Equal(("name_taken", MissionErrors.NameTaken), (taken.Code, taken.Detail));
        Assert.Equal(("invalid_value", "FieldValue"), (row.Code, row.Detail));
        Assert.StartsWith("equipment[1].equipment: No equipment has the name 'x1'.", row.Message);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>
    /// Each builder tool (section 13.1, items 5 and 6 of the MCP1 design). Its preview changes nothing and gives the adds
    /// and the changes of the real call. The real call adds one entry with the edit origin Agent. One undo puts back the
    /// states of each file before the call.
    /// </summary>
    [Fact]
    public void EachBuilderPreviewsAndIsOneAgentEntryThatOneUndoReverses()
    {
        (string Tool, string Arguments)[] calls =
        [
            ("scene_place_wreck", """{ "system": "Li01", "position": [1, 2, 3], "archetype": "wreck_freighter", "ship_archetype": "ship1", "loot": [{ "good": "commodity_gold", "count": 2 }] }"""),
            ("scene_place_patrol_route", """{ "system": "Li01", "name": "p1", "points": [[0, 0, 0], [0, 0, 5000]], "encounters": [{ "encounter": "patrol_b", "level": 1, "chance": 1, "factions": [{ "faction": "li_n_grp", "weight": 1 }] }] }"""),
            ("scene_set_encounters", """{ "zone": "zone:Zone_pop", "encounters": [], "density_restrictions": [] }"""),
            ("create_loadout", """{ "nickname": "lo_new", "archetype": "station1" }"""),
        ];
        Assert.Equal(Tools, calls.Select(c => c.Tool));

        foreach (var (tool, arguments) in calls)
        {
            var (ws, session, host) = Builders();
            using var _ = ws;
            var paths = new[] { SceneWorkspace.Li01Path, ListWorkspace.ShipLoadoutsPath };
            var before = paths.Select(p => AgentSceneToolTests.W(session, p)).ToList();

            var preview = Ok(host, tool, arguments.TrimEnd('}') + """, "preview": true }""");
            Assert.Equal(("previewed", "0", false), ((string?)preview["status"], (string?)preview["rev"], session.CanUndo));
            Assert.Equal(before, paths.Select(p => AgentSceneToolTests.W(session, p)));
            var result = Ok(host, tool, arguments);

            Assert.Equal((tool, "applied", "1"), (tool, (string?)result["status"], (string?)result["rev"]));
            Assert.Equal((EditOrigin.Agent, (string?)result["undo_label"]), (session.UndoOrigin, session.UndoLabel));
            Assert.Equal(Texts(preview["adds"]), Texts(result["adds"]));
            Assert.Equal(Texts(preview["changes"]), Texts(result["changes"]));
            session.Undo();
            Assert.Equal(before, paths.Select(p => AgentSceneToolTests.W(session, p)));
            Assert.False(session.CanUndo);
        }
    }

    /// <summary>The builders can be steps of edit_batch, so a route and a wreck are one entry. A later step names the
    /// wreck of an earlier step. create_loadout is a data tool of the group Studio, and the other builders are in the
    /// group Scene.</summary>
    [Fact]
    public void TheBuildersAreStepsOfABatchAndKeepTheirGroups()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;

        var result = Ok(host, "edit_batch", """
            {
              "label": "Add a patrol and a wreck",
              "steps": [
                { "tool": "scene_place_patrol_route", "arguments": { "system": "Li01", "name": "p1", "points": [[0, 0, 0], [0, 0, 5000]] } },
                { "tool": "scene_place_wreck", "arguments": { "system": "Li01", "position": [0, 0, 2500], "archetype": "wreck_freighter", "ship_archetype": "ship1", "nickname": "w1" } },
                { "tool": "set_fields", "arguments": { "ref": "obj:w1", "fields": { "visit": 0 } } }
              ]
            }
            """);
        host.Hidden.Add(ToolGroup.Scene);
        var hidden = Error(host, "scene_place_wreck",
            """{ "system": "Li01", "position": [0, 0, 0], "archetype": "wreck_freighter", "ship_archetype": "ship1" }""");

        Assert.Equal("Agent: Add a patrol and a wreck", (string?)result["undo_label"]);
        Assert.Equal("0", Line(session, SceneWorkspace.Object("w1"), "visit"));
        Assert.Equal(1, session.Revision);
        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Equal(ToolGroup.Studio, AgentTools.Find("create_loadout")!.Group);
        Assert.All(Tools.Where(t => t.StartsWith("scene_")), t => Assert.Equal(ToolGroup.Scene, AgentTools.Find(t)!.Group));
    }

    /// <summary>
    /// Workflow 2 of section 9.5 of the MCP1 design, steps 1 to 7, on a synthetic profile. The agent finds a free area,
    /// a faction and an encounter, and places a patrol route. Then it finds a wreck archetype and a ship, and places a
    /// wreck. Each
    /// edit is one Undo step, and check gives no new error.
    /// </summary>
    [Fact]
    public void WorkflowTwoRunsThroughTheToolsAndCheckGivesNoNewError()
    {
        var (ws, session, host) = Builders();
        using var _ = ws;
        string First(JsonObject page) => (string)page["rows"]![0]!["value"]!;
        var errors = (int)Ok(host, "check")["errors"]!;

        Ok(host, "list_items", """{ "system": "Li01", "kinds": ["zone"] }""");
        var faction = First(Ok(host, "list_values", """{ "list": "faction", "text": "li_p" }"""));
        var encounter = First(Ok(host, "list_values", """{ "list": "encounter_of_system", "system": "Li01" }"""));
        Ok(host, "scene_place_patrol_route", $$"""
            {
              "system": "Li01", "name": "my_patrol", "points": [[0, 0, 0], [15000, 0, 0], [15000, 0, 15000]], "radius": 750,
              "encounters": [{ "encounter": {{Json(encounter)}}, "level": 4, "chance": 0.4, "factions": [{ "faction": {{Json(faction)}}, "weight": 1 }] }]
            }
            """);
        var archetype = First(Ok(host, "list_values", """{ "list": "solar_archetype", "text": "MISSION_SATELLITE" }"""));
        var ship = First(Ok(host, "list_values", """{ "list": "ship" }"""));
        Ok(host, "scene_place_wreck", $$"""
            {
              "system": "Li01", "position": [8000, 500, 2000], "archetype": {{Json(archetype)}}, "ship_archetype": {{Json(ship)}},
              "loot": [{ "good": "commodity_gold", "count": 20 }], "nickname": "my_wreck_1", "name": "Lost Freighter"
            }
            """);

        Assert.Equal(("li_p_grp", "patrol_a", "wreck_freighter", "ship1"), (faction, encounter, archetype, ship));
        Assert.Equal(2, session.Revision);
        Assert.Equal(errors, (int)Ok(host, "check")["errors"]!);
    }

    /// <summary>The reference of a loadout of the first loadout file, as a result gives it: the install path as the
    /// session reads it from freelancer.ini.</summary>
    private static string LoadoutRef(EditSession session, string nickname) => ItemRefs.Canonical(session,
        new SectionRef(LoadoutCommands.File(session)!, new SectionIdentity("Loadout", nickname)));

    /// <summary>The end points of the leg of a route zone, from its lines.</summary>
    private static (Vector3 Start, Vector3 End) Ends(EditSession session, SectionRef zone)
    {
        Vector3 Vector(string key) => Parse(Line(session, zone, key)!);
        var length = float.Parse(Line(session, zone, "size")!.Split(", ")[1], CultureInfo.InvariantCulture);
        return PatrolCommands.Ends(Vector("pos"), Vector("rotate"), length);
    }

    private static Vector3 Parse(string text)
    {
        var values = text.Split(", ").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return new Vector3(values[0], values[1], values[2]);
    }

    private static string Number(float value) => value.ToString(CultureInfo.InvariantCulture);
}
