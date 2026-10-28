using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A synthetic install for the builders of the agent tools (section 7.3 of the MCP1 design), on top of
/// <see cref="SceneWorkspace"/>. It adds a wreck archetype, a ship with hardpoint lines, a commodity and encounters of
/// Li01 and Li02. Li01 also gets a zone with encounter lines and a zone of a route.
/// </summary>
internal static class BuilderWorkspace
{
    public const string Li01Text = SceneWorkspace.Li01Text + """

        [EncounterParameters]
        nickname = patrol_a
        filename = missions\encounters\patrol_a.ini
        [zone]
        nickname = Zone_pop
        pos = 5000, 0, 0
        shape = SPHERE
        size = 3000
        sort = 99
        faction_weight = li_n_grp, 2
        density_restriction = 2, lawful
        encounter = patrol_a, 3, 0.5
        faction = li_n_grp, 1
        faction_weight = li_p_grp, 1
        toughness = 4
        [zone]
        nickname = Zone_old_route_1
        pos = 0, 0, 9000
        shape = CYLINDER
        size = 750, 2000
        path_label = old_route, 1
        usage = patrol
        """;

    public const string Li02Text = SceneWorkspace.Li02Text + """

        [EncounterParameters]
        nickname = patrol_b
        filename = missions\encounters\patrol_b.ini
        """;

    /// <summary>The scene workspace with the files of the builders.</summary>
    public static TestWorkspace Create()
    {
        var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li01Path, Li01Text);
        ws.WriteBase(SceneWorkspace.Li02Path, Li02Text);
        ws.WriteBase(ListWorkspace.SolarPath, SceneWorkspace.Solars + """

            [Solar]
            nickname = wreck_freighter
            type = MISSION_SATELLITE
            """);
        ws.WriteBase(ListWorkspace.ShipsPath, """
            [Ship]
            nickname = ship1
            hp_type = hp_gun, HpWeapon01, HpWeapon02
            """);
        ws.WriteBase(ListWorkspace.EquipmentPath, """
            [Munition]
            nickname = ammo1
            [Gun]
            nickname = gun1
            projectile_archetype = ammo1
            [Commodity]
            nickname = commodity_gold
            """);
        return ws;
    }

    public static SectionRef Loadout(string nickname) => ListWorkspace.Loadout(ListWorkspace.ShipLoadoutsPath, nickname);
}

/// <summary>
/// The builder commands of section 7.3 of the MCP1 design (decisions D730, D731 and D732). They make a new loadout, a
/// wreck, a route and the encounter groups of a zone. Each command gives one plan, and the plan is one entry of the edit history.
/// </summary>
public sealed class BuilderCommandsTests
{
    private static readonly SectionRef Pop = SceneWorkspace.Zone("Zone_pop");

    [Fact]
    public void ANewLoadoutGoesIntoTheFirstLoadoutFileWithTheValuesAsTheListsSpellThem()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var plan = LoadoutCommands.Create(session, "lo_new", "SHIP1",
            [new LoadoutEquipment("GUN1", "HpWeapon01"), new LoadoutEquipment("gun1", null)],
            [new LoadoutCargo("Commodity_Gold", 20)]);
        session.Execute(plan.Command!);

        var loadout = BuilderWorkspace.Loadout("lo_new");
        Assert.Equal("Create lo_new", plan.Label);
        Assert.Equal([loadout], plan.Adds);
        Assert.Equal(["nickname = lo_new", "archetype = ship1", "equip = gun1, HpWeapon01", "equip = gun1",
            "cargo = commodity_gold, 20"], Lines(session, loadout));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ALoadoutRefusesATakenNicknameAndGivesEachRowThatItsListDoesNotHave()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var taken = LoadoutCommands.Create(session, "LO_STATION", "ship1", [], []);
        var rows = Assert.Throws<EditException>(() => LoadoutCommands.Create(session, "lo_new", "ship1",
            [new LoadoutEquipment("gun1", "HpWeapon01"), new LoadoutEquipment("gun9", null)],
            [new LoadoutCargo("gold", 1)]));
        var archetype = Assert.Throws<EditException>(() => LoadoutCommands.Create(session, "lo_new", "ship9", [], []));
        var comma = Assert.Throws<EditException>(() => LoadoutCommands.Create(session, "lo_new", "ship1",
            [new LoadoutEquipment("gun1", "Hp, 2")], []));

        Assert.False(taken.CanRun);
        Assert.Equal(MissionErrors.NameTaken, taken.Refusals[0].Rule);
        Assert.Equal("The loadout lo_station exists in the data. Use another nickname.", taken.Refusals[0].Message);
        Assert.Equal((EditErrorKind.InvalidValue, MissionErrors.FieldValue, "equipment[1].equipment"),
            (rows.Error.Kind, rows.Error.Detail, rows.Error.Field));
        Assert.StartsWith("equipment[1].equipment: No equipment has the name 'gun9'. Pick a value from list_values with " +
                          "the list equipment.", rows.Error.Message);
        Assert.Contains("cargo[0].good: No equipment has the name 'gold'.", rows.Error.Message);
        Assert.Equal("archetype", archetype.Error.Field);
        Assert.Equal("equipment[0].hardpoint", comma.Error.Field);
        Assert.False(session.CanUndo);
    }

    /// <summary>The rule data has no list of hardpoints. The model of a ship can have more hardpoints than its hp_type
    /// lines name. So a hardpoint that these lines do not name gives a warning.</summary>
    [Fact]
    public void AHardpointThatTheShipLinesDoNotNameGivesAWarning()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var plan = LoadoutCommands.Create(session, "lo_new", "ship1", [new LoadoutEquipment("gun1", "HpTurret01")], []);

        Assert.True(plan.CanRun);
        Assert.Equal("equipment[0]: the hp_type lines of the ship ship1 do not name the hardpoint HpTurret01. Make sure " +
                     "that the model of the ship has it.", Assert.Single(plan.Warnings).Message);
    }

    [Fact]
    public void AWreckIsANewObjectAndANewLoadoutOfTheShipInOneEntry()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var plan = WreckCommands.Place(session, "Li01", new Vector3(8000.4f, 500, 2000), new Vector3(0, 90, 0),
            "wreck_freighter", "ship1", [new LoadoutCargo("commodity_gold", 20)],
            [new LoadoutEquipment("gun1", "HpWeapon01")], null);
        session.Execute(plan.Command!);

        var wreck = SceneWorkspace.Object("li01_wreck_1");
        var loadout = BuilderWorkspace.Loadout("li01_wreck_1_loadout");
        Assert.Equal("Place wreck li01_wreck_1", plan.Label);
        Assert.Equal([wreck, loadout], plan.Adds);
        Assert.Equal(wreck, plan.SelectAfter);
        Assert.Equal(["nickname = li01_wreck_1", "pos = 8000, 500, 2000", "rotate = 0, 90, 0",
            "archetype = wreck_freighter", "loadout = li01_wreck_1_loadout", "visit = 16"], Lines(session, wreck));
        Assert.Equal(["nickname = li01_wreck_1_loadout", "archetype = ship1", "equip = gun1, HpWeapon01",
            "cargo = commodity_gold, 20"], Lines(session, loadout));
        session.Undo();
        Assert.Null(session.Get(wreck));
        Assert.Null(session.Get(loadout));
    }

    [Fact]
    public void AWreckNeedsAnArchetypeOfAWreckTypeAndAShip()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");
        EditException Place(string archetype, string ship, string? nickname = null) =>
            Assert.Throws<EditException>(() => WreckCommands.Place(session, "Li01", Vector3.Zero, null, archetype, ship,
                [], [], nickname));

        var station = Place("station1", "ship1");
        var solar = Place("wreck_freighter", "station1");
        var taken = WreckCommands.Place(session, "Li01", Vector3.Zero, null, "wreck_freighter", "ship1", [], [], "Li01_01");

        Assert.Equal((EditErrorKind.InvalidValue, WreckCommands.NotAWreck, "archetype"),
            (station.Error.Kind, station.Error.Detail, station.Error.Field));
        Assert.Equal("The archetype station1 has the type station. A wreck needs an archetype of the type " +
                     "mission_satellite. Use list_values with the list solar_archetype and the text MISSION_SATELLITE.",
            station.Error.Message);
        Assert.Equal("ship_archetype", solar.Error.Field);
        Assert.Equal(MissionErrors.NameTaken, taken.Refusals[0].Rule);
    }

    /// <summary>The zone of a leg has its center at the middle of the leg. Its local Y axis goes along the leg, from the
    /// start to the end, as the engine turns it. So the ends of the zone are the points of the leg.</summary>
    [Fact]
    public void TheZoneOfALegHasItsEndsAtThePointsOfTheLeg()
    {
        var random = new Random(7);
        var legs = new List<(Vector3 From, Vector3 To)>
        {
            (Vector3.Zero, new Vector3(0, 1000, 0)),
            (Vector3.Zero, new Vector3(0, -1000, 0)),
            (Vector3.Zero, new Vector3(15000, 0, 0)),
            (new Vector3(15000, 0, 0), new Vector3(15000, 0, 15000)),
        };
        for (var i = 0; i < 200; i++)
            legs.Add((Point(random), Point(random)));

        foreach (var (from, to) in legs)
        {
            var (center, rotation, length) = PatrolCommands.Leg(from, to);
            var (start, end) = PatrolCommands.Ends(center, rotation, length);

            Assert.Equal((from + to) / 2, center);
            Assert.Equal(Vector3.Distance(from, to), length);
            // The angles have steps of 0.001 degree, so the ends move by less than 0.1 m for each 10 km.
            var tolerance = 0.05f + length * 1e-5f;
            Assert.True(Vector3.Distance(start, from) < tolerance, $"{from} to {to}: start {start}");
            Assert.True(Vector3.Distance(end, to) < tolerance, $"{from} to {to}: end {end}");
        }
        return;

        static Vector3 Point(Random r) => new((float)(r.NextDouble() - 0.5) * 2e5f, (float)(r.NextDouble() - 0.5) * 2e4f,
            (float)(r.NextDouble() - 0.5) * 2e5f);
    }

    [Fact]
    public void ARouteIsOneCylinderZoneForEachLegWithTheLinesOfItsTemplate()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var plan = PatrolCommands.Place(session, "Li01", "my_patrol", "patrol",
            [Vector3.Zero, new Vector3(15000, 0, 0), new Vector3(15000, 0, 15000)], null,
            [new EncounterGroup("patrol_a", 4, 0.4, [new FactionShare("li_n_grp", 1)])],
            [new FactionShare("li_p_grp", 0.5)], [new DensityRestriction(3, "unlawful")]);
        session.Execute(plan.Command!);

        var first = SceneWorkspace.Zone("li01_path_my_patrol_1");
        var second = SceneWorkspace.Zone("li01_path_my_patrol_2");
        Assert.Equal("Place route my_patrol", plan.Label);
        Assert.Equal([first, second], plan.Adds);
        Assert.Equal(first, plan.SelectAfter);
        Assert.Empty(plan.Fixes);
        Assert.Equal(["nickname = li01_path_my_patrol_1", "pos = 7500, 0, 0", "rotate = 0, 0, -90", "shape = CYLINDER",
            "size = 750, 15000", "sort = 99", "toughness = 7", "density = 3", "repop_time = 90", "max_battle_size = 4",
            "pop_type = attack_patrol", "relief_time = 30", "path_label = my_patrol, 1", "usage = patrol",
            "mission_eligible = true", "faction_weight = li_p_grp, 0.5", "density_restriction = 3, unlawful",
            "encounter = patrol_a, 4, 0.4", "faction = li_n_grp, 1"], Lines(session, first));
        Assert.Equal("path_label = my_patrol, 2", Lines(session, second).Single(l => l.StartsWith("path_label")));
        Assert.Empty(session.Lists.Problems(first).Where(p => p.IsError));
    }

    [Fact]
    public void ARouteRefusesAPathLabelThatTheSystemHasAndPointsAtOnePlace()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var taken = PatrolCommands.Place(session, "Li01", "OLD_ROUTE", "patrol", [Vector3.Zero, Vector3.UnitX * 100], null,
            [], [], []);
        var same = Assert.Throws<EditException>(() => PatrolCommands.Place(session, "Li01", "new_route", "patrol",
            [Vector3.Zero, Vector3.UnitX * 100, Vector3.UnitX * 100.5f], null, [], [], []));
        var bad = Assert.Throws<EditException>(() => PatrolCommands.Place(session, "Li01", "new,route", "patrol",
            [Vector3.Zero, Vector3.UnitX * 100], null, [], [], []));

        Assert.Equal((MissionErrors.NameTaken, SceneWorkspace.Zone("Zone_old_route_1")),
            (taken.Refusals[0].Rule, taken.Refusals[0].Target));
        Assert.Equal("The system Li01 has the route old_route: the zone Zone_old_route_1 has it. Use another name.",
            taken.Refusals[0].Message);
        Assert.Equal(("points[2]", "The points 1 and 2 are less than 1 meter apart. Move one of them."),
            (same.Error.Field, same.Error.Message));
        Assert.Equal("name", bad.Error.Field);
    }

    /// <summary>A leg without a finite length has no finite center and angles. So the command refuses it, and the error
    /// names the point at the end of the leg.</summary>
    [Fact]
    public void ARouteRefusesALegWithoutAFiniteLength()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var far = Assert.Throws<EditException>(() => PatrolCommands.Place(session, "Li01", "far", "patrol",
            [Vector3.Zero, new Vector3(1000, 0, 0), new Vector3(1e20f, 0, 0)], null, [], [], []));
        var nan = Assert.Throws<EditException>(() => PatrolCommands.Place(session, "Li01", "far", "patrol",
            [Vector3.Zero, new Vector3(float.NaN, 0, 0)], null, [], [], []));

        Assert.Equal((EditErrorKind.InvalidValue, "points[2]", "The leg from point 1 to point 2 has no finite length. " +
                                                              "Give points nearer to the center of the system."),
            (far.Error.Kind, far.Error.Field, far.Error.Message));
        Assert.Equal("points[1]", nan.Error.Field);
    }

    /// <summary>The encounter lines of a zone are one block (decision D732). The faction weights and the density
    /// restrictions come before the first encounter line. Then each encounter line comes with its faction lines. The
    /// other lines keep their place. A save writes the whole linked group, and a reload gives the same lines.</summary>
    [Fact]
    public void TheEncounterGroupsOfAZoneAreOneBlockThatTheSaveKeeps()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var plan = EncounterCommands.SetGroups(session, Pop,
            [
                new EncounterGroup("patrol_a", 5, 0.25, [new FactionShare("li_p_grp", 1), new FactionShare("li_n_grp", 2)]),
                new EncounterGroup("PATROL_A", 2, 1, [new FactionShare("li_n_grp", 1)]),
            ],
            null, [new DensityRestriction(4, "lawful")]);
        session.Execute(plan.Command!);

        string[] expected =
        [
            "nickname = Zone_pop", "pos = 5000, 0, 0", "shape = SPHERE", "size = 3000", "sort = 99",
            "faction_weight = li_n_grp, 2", "density_restriction = 4, lawful", "encounter = patrol_a, 5, 0.25",
            "faction = li_p_grp, 1", "faction = li_n_grp, 2", "encounter = patrol_a, 2, 1", "faction = li_n_grp, 1",
            "toughness = 4",
        ];
        Assert.Equal("Set the encounters of Zone_pop", plan.Label);
        Assert.Equal([Pop], plan.Changes);
        Assert.Empty(plan.Adds);
        Assert.Equal(expected, Lines(session, Pop));

        Assert.True(session.Save().Saved);
        var patch = ws.ReadModPatch(SceneWorkspace.Li01Path);
        Assert.Contains("faction_weight = li_n_grp, 2", patch);
        Assert.Contains("encounter = patrol_a, 2, 1", patch);
        session.Dispose();
        using var reloaded = ws.Session("act");
        Assert.Equal(expected, Lines(reloaded, Pop));
    }

    /// <summary>The engine reads a density_restriction line at each place in a zone. So a call without density
    /// restrictions keeps each of them, also a line after an encounter line. The kept line moves into the block. A save
    /// and a reload keep the order.</summary>
    [Fact]
    public void ACallWithoutDensityRestrictionsKeepsALineAfterTheEncounterLines()
    {
        using var ws = BuilderWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li01Path, BuilderWorkspace.Li01Text + LateZone);
        var session = ws.Session("act");

        session.Execute(EncounterCommands.SetGroups(session, Late,
            [new EncounterGroup("patrol_a", 2, 0.5, [new FactionShare("li_p_grp", 1)])], null, null).Command!);

        string[] expected =
        [
            "nickname = Zone_late", "pos = 0, 0, 5000", "shape = SPHERE", "size = 2000", "faction_weight = li_n_grp, 1",
            "density_restriction = 2, lawful", "encounter = patrol_a, 2, 0.5", "faction = li_p_grp, 1", "toughness = 3",
        ];
        Assert.Equal(expected, Lines(session, Late));
        Assert.True(session.Save().Saved);
        session.Dispose();
        using var reloaded = ws.Session("act");
        Assert.Equal(expected, Lines(reloaded, Late));
    }

    /// <summary>A call that changes only the density restrictions of a zone. The new line comes before the encounter
    /// lines that do not change. The save writes them too, so a reload keeps the order.</summary>
    [Fact]
    public void ANewDensityRestrictionBeforeTheEncounterLinesKeepsItsPlaceAfterAReload()
    {
        using var ws = BuilderWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li01Path, BuilderWorkspace.Li01Text + LateZone.Replace("density_restriction = 2, lawful\n", ""));
        var session = ws.Session("act");

        session.Execute(EncounterCommands.SetGroups(session, Late,
            [new EncounterGroup("patrol_a", 3, 0.5, [new FactionShare("li_n_grp", 1)])], null,
            [new DensityRestriction(4, "lawful")]).Command!);

        string[] expected =
        [
            "nickname = Zone_late", "pos = 0, 0, 5000", "shape = SPHERE", "size = 2000", "faction_weight = li_n_grp, 1",
            "density_restriction = 4, lawful", "encounter = patrol_a, 3, 0.5", "faction = li_n_grp, 1", "toughness = 3",
        ];
        Assert.Equal(expected, Lines(session, Late));
        Assert.True(session.Save().Saved);
        session.Dispose();
        using var reloaded = ws.Session("act");
        Assert.Equal(expected, Lines(reloaded, Late));
    }

    private static readonly SectionRef Late = SceneWorkspace.Zone("Zone_late");

    /// <summary>A zone with a faction weight, an encounter group, then a density restriction and a toughness line.</summary>
    private const string LateZone = """

        [zone]
        nickname = Zone_late
        pos = 0, 0, 5000
        shape = SPHERE
        size = 2000
        faction_weight = li_n_grp, 1
        encounter = patrol_a, 3, 0.5
        faction = li_n_grp, 1
        density_restriction = 2, lawful
        toughness = 3
        """;

    /// <summary>The system can lack the [EncounterParameters] section of an encounter that another system has. Then the
    /// same entry adds a copy of the section of that system, as a fix of the plan. An encounter of no system gives an
    /// error with the row.</summary>
    [Fact]
    public void AnEncounterOfAnotherSystemAddsItsParametersAsAFix()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var plan = EncounterCommands.SetGroups(session, Pop,
            [new EncounterGroup("patrol_b", 1, 0.5, [new FactionShare("li_n_grp", 1)])], [], []);
        var unknown = Assert.Throws<EditException>(() => EncounterCommands.SetGroups(session, Pop,
            [new EncounterGroup("patrol_a", 1, 0.5, [new FactionShare("li_x", 1)]),
                new EncounterGroup("patrol_z", 1, 0.5, [new FactionShare("li_n_grp", 1)])], null, null));
        session.Execute(plan.Command!);

        var parameters = SceneWorkspace.Li01("EncounterParameters", "patrol_b");
        Assert.Equal([parameters], plan.Adds);
        Assert.Equal(parameters, Assert.Single(plan.Fixes).Section);
        Assert.Equal("The system had no [EncounterParameters] section for the encounter patrol_b. The edit adds a copy of " +
                     "the section of the system Li02.", plan.Fixes[0].Text);
        Assert.Equal(["nickname = patrol_b", @"filename = missions\encounters\patrol_b.ini"], Lines(session, parameters));
        Assert.Equal(["encounter = patrol_b, 1, 0.5", "faction = li_n_grp, 1"],
            Lines(session, Pop).Where(l => l.StartsWith("encounter") || l.StartsWith("faction")));
        Assert.Empty(session.Lists.Problems(Pop).Where(p => p.IsError));
        Assert.Equal("encounters[0].factions[0].faction", unknown.Error.Field);
        Assert.Contains("encounters[1].encounter: No [EncounterParameters] section of a system has the encounter " +
                        "'patrol_z'.", unknown.Error.Message);
    }

    [Fact]
    public void OnlyAZoneOfASystemHasEncounters()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");

        var plan = EncounterCommands.SetGroups(session, SceneWorkspace.Object("Li01_01"), [], null, null);

        Assert.False(plan.CanRun);
        Assert.Equal("[Object] nickname = Li01_01: only a zone of a system has encounters.", plan.Refusals[0].Message);
    }

    /// <summary>The block command of the edit view. The lines of the names go to the place of the first line of any of
    /// the names. The other lines keep their order. An empty block removes the names.</summary>
    [Fact]
    public void ABlockOfLinesTakesThePlaceOfTheFirstLineOfItsNames()
    {
        using var ws = BuilderWorkspace.Create();
        var session = ws.Session("act");
        string[] names = ["faction", "encounter"];

        session.Execute(EditCommand.SetLineBlock(Pop, names,
            [ListWorkspace.Line("encounter", "patrol_a", "1", "1"), ListWorkspace.Line("faction", "li_n_grp", "1")], "Block"));
        var placed = Lines(session, Pop);
        session.Execute(EditCommand.SetLineBlock(Pop, names, [], "Block"));

        Assert.Equal(["nickname = Zone_pop", "pos = 5000, 0, 0", "shape = SPHERE", "size = 3000", "sort = 99",
            "faction_weight = li_n_grp, 2", "density_restriction = 2, lawful", "encounter = patrol_a, 1, 1",
            "faction = li_n_grp, 1", "faction_weight = li_p_grp, 1", "toughness = 4"], placed);
        Assert.DoesNotContain(Lines(session, Pop), l => l.StartsWith("encounter") || l.StartsWith("faction ="));
        Assert.Throws<ArgumentException>(() => EditCommand.SetLineBlock(Pop, names, [ListWorkspace.Line("sort", "1")], "x"));
        Assert.Throws<InvalidOperationException>(() =>
            session.Execute(EditCommand.SetLineBlock(Pop, ["nickname"], [], "x")));
    }

    private static string[] Lines(EditSession session, SectionRef section) =>
        session.Get(section)!.Entries.Select(e => $"{e.Name} = {FieldEdit.FormatLine(e.Values)}").ToArray();
}
