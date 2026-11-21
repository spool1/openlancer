using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Missions.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A synthetic install for the trade lanes, on top of <see cref="SceneWorkspace"/>. Li01 has a local faction of the
/// house li, a sun at the center with a death zone of 12000 m, a jump gate, a planet with a name, a planet without a
/// name, and a lane of four rings along the x axis with its lane zone. The archetypes have a <c>solar_radius</c>. All
/// names and values are invented.
/// </summary>
internal static class LaneWorkspace
{
    public const string Li01Text = """
        [SystemInfo]
        local_faction = li_n_grp
        [Object]
        nickname = Li01_sun
        ids_name = 501
        pos = 0, 0, 0
        archetype = lane_sun
        [Object]
        nickname = Li01_to_Li02
        ids_name = 502
        pos = 60000, 0, 0
        archetype = lane_gate
        goto = Li02, Li02_to_Li01, gate_tunnel_bretonia
        [Object]
        nickname = Li01_planet
        ids_name = 503
        pos = 0, 0, 40000
        archetype = lane_planet
        [Object]
        nickname = Li01_rock
        pos = 0, 0, -60000
        archetype = lane_planet
        [zone]
        nickname = Zone_Li01_sun_death
        pos = 0, 0, 0
        shape = SPHERE
        size = 12000
        damage = 10000
        [Object]
        nickname = Li01_Trade_Lane_Ring_1
        ids_name = 60245
        pos = 0, 0, -20000
        rotate = 0, -90, 0
        archetype = Trade_Lane_Ring
        ids_info = 66170
        next_ring = Li01_Trade_Lane_Ring_2
        behavior = NOTHING
        tradelane_space_name = 504
        reputation = li_n_grp
        difficulty_level = 10
        loadout = trade_lane_ring_li_01
        pilot = pilot_solar_hard
        [Object]
        nickname = Li01_Trade_Lane_Ring_2
        ids_name = 60245
        pos = 7500, 0, -20000
        rotate = 0, -90, 0
        archetype = Trade_Lane_Ring
        ids_info = 66170
        prev_ring = Li01_Trade_Lane_Ring_1
        next_ring = Li01_Trade_Lane_Ring_3
        behavior = NOTHING
        reputation = li_n_grp
        [Object]
        nickname = Li01_Trade_Lane_Ring_3
        ids_name = 60245
        pos = 15000, 0, -20000
        rotate = 0, -90, 0
        archetype = Trade_Lane_Ring
        ids_info = 66170
        prev_ring = Li01_Trade_Lane_Ring_2
        next_ring = Li01_Trade_Lane_Ring_4
        behavior = NOTHING
        reputation = li_n_grp
        [Object]
        nickname = Li01_Trade_Lane_Ring_4
        ids_name = 60245
        pos = 22500, 0, -20000
        rotate = 0, -90, 0
        archetype = Trade_Lane_Ring
        ids_info = 66170
        prev_ring = Li01_Trade_Lane_Ring_3
        behavior = NOTHING
        tradelane_space_name = 505
        reputation = li_n_grp
        [zone]
        nickname = Zone_Li01_Tradelane_1
        pos = 11250, 0, -20000
        rotate = 0, -90, 0
        shape = BOX
        size = 500, 500, 23500
        pop_type = major_tradelane
        """;

    public static TestWorkspace Create(string? li01Text = null)
    {
        var ws = SceneWorkspace.Create();
        ws.WriteBase(ListWorkspace.SolarPath, SceneWorkspace.Solars + """

            [Solar]
            nickname = Trade_Lane_Ring
            type = TRADELANE_RING
            solar_radius = 300
            [Solar]
            nickname = lane_sun
            type = SUN
            solar_radius = 2000
            [Solar]
            nickname = lane_gate
            type = JUMP_GATE
            solar_radius = 400
            [Solar]
            nickname = lane_planet
            type = PLANET
            solar_radius = 1500
            """);
        ws.WriteBase(SceneWorkspace.Li01Path, li01Text ?? Li01Text);
        return ws;
    }

    public static SectionRef Ring(int n) => SceneWorkspace.Object($"Li01_Trade_Lane_Ring_{n}");

    public static SectionRef Zone => SceneWorkspace.Zone("Zone_Li01_Tradelane_1");

    /// <summary>The last value text of a key of a section, or null.</summary>
    public static string? Line(EditSession session, SectionRef section, string key) =>
        session.Get(section)?.Lines(key).LastOrDefault() is { } line
            ? string.Join(", ", line.Values.Select(v => v.ToString()))
            : null;

    public static Vector3 Vector(string text) =>
        text.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)) is var values &&
        values.ToArray() is [var x, var y, var z]
            ? new Vector3(x, y, z)
            : throw new FormatException(text);
}

/// <summary>
/// The plans of the trade lanes: a new lane with its rings and its zone, a new ring, the remove of rings, the repair of
/// the links and the delete of a lane. Each plan is one entry of the edit history. The data is invented.
/// </summary>
public sealed class LanePlansTests
{
    private static SectionRef Ring(int n) => LaneWorkspace.Ring(n);

    private static string? Line(EditSession session, SectionRef section, string key) =>
        LaneWorkspace.Line(session, section, key);

    private static T Run<T>(EditSession session, T plan) where T : EditPlan
    {
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        return plan;
    }

    private static Diagnostic Refusal(EditPlan plan, string detail)
    {
        Assert.False(plan.CanRun);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal(detail, refusal.Rule);
        return refusal;
    }

    private static TradeLaneEnd At(float x, float y, float z, string? name = null) =>
        new() { Point = new Vector3(x, y, z), Name = name };

    /// <summary>
    /// A lane of 30000 m gets one ring each 7500 m, plus one: five rings with the retail lines in the retail order, the
    /// next free ring nicknames, the names of the rule data and the faction, loadout and pilot of the house of the local
    /// faction. The end rings get the end names in the text of the mod. The lane zone is a box on the lane that meets
    /// the lane zone rule. One undo takes back the rings, the zone and the texts.
    /// </summary>
    [Fact]
    public void ALaneGetsOneRingEachSpacingAndItsZoneInOneUndoStep()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.Create(session, "li01", At(30000, 0, 30000, "Avalon Gate"),
            At(30000, 0, 60000, "Far Point")));

        Assert.Equal(Enumerable.Range(5, 5).Select(n => Ring(n)), plan.Rings);
        Assert.Equal(7500, plan.Spacing);
        Assert.Equal("Create trade lane in Li01", session.UndoLabel);
        Assert.Empty(plan.Warnings);
        var first = ConnectionWorkspace.Lines(session, Ring(5));
        Assert.Equal(["nickname", "ids_name", "pos", "rotate", "archetype", "ids_info", "next_ring", "behavior",
            "tradelane_space_name", "reputation", "difficulty_level", "loadout", "pilot"], first.Select(l => l.Name));
        Assert.Equal([("ids_name", "60245"), ("pos", "30000, 0, 30000"), ("rotate", "0, 180, 0"),
            ("archetype", "Trade_Lane_Ring"), ("ids_info", "66170"), ("next_ring", "Li01_Trade_Lane_Ring_6"),
            ("behavior", "NOTHING")], first.Skip(1).Take(7));
        Assert.Equal([("reputation", "li_n_grp"), ("difficulty_level", "10"), ("loadout", "trade_lane_ring_li_01"),
            ("pilot", "pilot_solar_hard")], first.Skip(9));
        Assert.Equal("Avalon Gate", ConnectionWorkspace.TextOf(session, first[8].Text));
        var middle = ConnectionWorkspace.Lines(session, Ring(7));
        Assert.Equal(("pos", "30000, 0, 45000"), middle[2]);
        Assert.Equal([("prev_ring", "Li01_Trade_Lane_Ring_6"), ("next_ring", "Li01_Trade_Lane_Ring_8")], middle.Skip(6).Take(2));
        Assert.DoesNotContain(middle, l => l.Name == "tradelane_space_name");
        Assert.Equal("Far Point", ConnectionWorkspace.TextOf(session, Line(session, Ring(9), "tradelane_space_name")!));
        Assert.Null(Line(session, Ring(9), "next_ring"));

        var zone = SceneWorkspace.Zone("Zone_Li01_Tradelane_2");
        Assert.Equal(zone, plan.Zone);
        Assert.Equal([("nickname", "Zone_Li01_Tradelane_2"), ("pos", "30000, 0, 45000"), ("rotate", "0, 180, 0"),
            ("shape", "BOX"), ("size", "500, 500, 31000"), ("lane_id", "2"), ("tradelane_down", "10"), ("sort", "2"),
            ("toughness", "10"), ("density", "6"), ("repop_time", "15"), ("max_battle_size", "4"),
            ("pop_type", "major_tradelane"), ("relief_time", "15")], ConnectionWorkspace.Lines(session, zone));
        Assert.Equal(zone, LanePlans.ZoneOf(session, LanePlans.LaneOf(session, Ring(7))!));
        Assert.Equal([.. plan.Rings, zone], plan.Adds);

        session.Undo();
        Assert.All(plan.Adds, a => Assert.Null(session.Get(a)));
        Assert.Null(ConnectionWorkspace.TextOf(session, first[8].Text));
    }

    /// <summary>A lane needs 2 to 100 rings with 1000 to 20000 m between two rings. A spacing outside the retail range
    /// gives a warning, and the lane is made.</summary>
    [Fact]
    public void TheSpacingRefusesOutsideItsLimitsAndWarnsOutsideTheRetailRange()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        var (start, end) = (At(30000, 0, 30000, "A"), At(30000, 0, 60000, "B"));

        var far = Refusal(LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 2 }), LanePlans.LaneRings);
        Refusal(LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 101 }), LanePlans.LaneRings);
        Refusal(LanePlans.Create(session, "Li01", start, At(30000, 0, 30500, "B")), LanePlans.LaneRings);
        var close = Run(session, LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 7 }));

        Assert.Equal("A trade lane needs 2 to 100 rings, with 1000 to 20000 m between two rings. This lane has 30000 m.",
            far.Message);
        Assert.Equal("rings", LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 2 }).RefusedPart);
        Assert.Equal(7, close.Rings.Count);
        Assert.Equal("The rings of the lane are 5000 m apart. Retail lanes have 5165 to 9445 m between two rings. Give " +
                     "another number of rings, or move an end.", Assert.Single(close.Warnings).Message);
    }

    /// <summary>
    /// An end with an object and no point goes on the line toward the other end, at the solar radius of the archetype
    /// of the object plus 2000 m. The end takes the name of the object.
    /// </summary>
    [Fact]
    public void AnEndAtAnObjectKeepsItsClearanceAndTakesItsName()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        var gate = SceneWorkspace.Object("Li01_to_Li02");
        var planet = SceneWorkspace.Object("Li01_planet");

        var plan = Run(session, LanePlans.Create(session, "Li01", new TradeLaneEnd { Near = gate },
            new TradeLaneEnd { Near = planet }));

        var first = LaneWorkspace.Vector(Line(session, plan.Rings[0], "pos")!);
        var last = LaneWorkspace.Vector(Line(session, plan.Rings[^1], "pos")!);
        Assert.Equal(2400, Vector3.Distance(first, new Vector3(60000, 0, 0)), 1f);
        Assert.Equal(3500, Vector3.Distance(last, new Vector3(0, 0, 40000)), 1f);
        Assert.Equal("502", Line(session, plan.Rings[0], "tradelane_space_name"));
        Assert.Equal("503", Line(session, plan.Rings[^1], "tradelane_space_name"));
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// The first lane of a new system goes from its gate toward the sun. The place at the solar radius of the sun plus
    /// the clearance is inside the death zone of 12000 m. So the end ring goes past the edge of the zone, plus the
    /// clearance, and it gets no placement warning.
    /// </summary>
    [Fact]
    public void AnEndAtASunStandsOutsideItsDeathZone()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.Create(session, "Li01", new TradeLaneEnd { Near = SceneWorkspace.Object("Li01_to_Li02") },
            new TradeLaneEnd { Near = SceneWorkspace.Object("Li01_sun") }));

        var last = LaneWorkspace.Vector(Line(session, plan.Rings[^1], "pos")!);
        Assert.Equal(new Vector3(14000, 0, 0), last, new VectorNear(2));
        Assert.Equal("501", Line(session, plan.Rings[^1], "tradelane_space_name"));
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// The rotation of each ring turns its front along the lane: the yaw for a flat lane, and the yaw and the pitch for a
    /// lane with a height difference. The rotation rule of the engine (<see cref="MathHelper.MatrixFromEulerDegrees(Vector3)"/>)
    /// turns the front axis of the ring, its negative z axis, to the direction of the lane.
    /// </summary>
    [Theory]
    [InlineData(30000, 0, 30000, 60000, 0, 30000, "0, -90, 0")]
    [InlineData(30000, 0, 60000, 30000, 0, 30000, "0, 0, 0")]
    [InlineData(30000, 0, 30000, 40000, 10000, 50000, null)]
    [InlineData(30000, 5000, 30000, 25000, -20000, 20000, null)]
    public void TheRingsLookAlongTheLane(float x1, float y1, float z1, float x2, float y2, float z2, string? rotate)
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.Create(session, "Li01", At(x1, y1, z1, "A"), At(x2, y2, z2, "B")));

        var direction = Vector3.Normalize(new Vector3(x2 - x1, y2 - y1, z2 - z1));
        foreach (var ring in plan.Rings)
        {
            var text = Line(session, ring, "rotate")!;
            if (rotate != null)
                Assert.Equal(rotate, text);
            var front = Vector3.TransformNormal(-Vector3.UnitZ, MathHelper.MatrixFromEulerDegrees(LaneWorkspace.Vector(text)));
            Assert.Equal(direction, front, new VectorNear(0.001f));
        }
        Assert.Equal(Line(session, plan.Rings[0], "rotate"), Line(session, plan.Zone!, "rotate"));
    }

    /// <summary>An end without a point and an object refuses, and so does an end without a name. An object without a
    /// name gives no name. A template that is no ring refuses.</summary>
    [Fact]
    public void AnEndNeedsAPlaceAndANameAndATemplateMustBeARing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var noEnd = Refusal(LanePlans.Create(session, "Li01", new TradeLaneEnd { Name = "A" }, At(0, 0, 30000, "B")),
            LanePlans.LaneEnd);
        var noName = Refusal(LanePlans.Create(session, "Li01", At(30000, 0, 30000), At(30000, 0, 60000, "B")),
            LanePlans.LaneEndName);
        Refusal(LanePlans.Create(session, "Li01", At(30000, 0, 30000, "A"),
            new TradeLaneEnd { Near = SceneWorkspace.Object("Li01_rock") }), LanePlans.LaneEndName);
        var like = Refusal(LanePlans.Create(session, "Li01", At(30000, 0, 30000, "A"), At(30000, 0, 60000, "B"),
            new LaneOptions { Like = SceneWorkspace.Object("Li01_planet") }), LanePlans.LaneArchetype);
        Refusal(LanePlans.Create(session, "Li01", new TradeLaneEnd { Near = SceneWorkspace.Li02("Object", "Li02_01") },
            At(30000, 0, 60000, "B")), MissionErrors.ItemNotFound);

        Assert.Equal("Give start or start_near, and end or end_near.", noEnd.Message);
        Assert.Equal("Give start_name or start_near, and end_name or end_near. The HUD shows the two end names of a lane.",
            noName.Message);
        Assert.Equal("Li01_planet is not a trade lane ring. Pick a ring with the archetype type TRADELANE_RING.", like.Message);
        Assert.Equal(0, session.Revision);
    }

    /// <summary>A template ring gives its lines to the new rings. Without a loadout of the template, the faction gives the
    /// loadout of its house.</summary>
    [Fact]
    public void ATemplateRingGivesItsLinesAndAFactionGivesTheLoadoutOfItsHouse()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(2), "difficulty_level", ["4"]));

        var liked = Run(session, LanePlans.Create(session, "Li01", At(30000, 0, 30000, "A"), At(30000, 0, 45000, "B"),
            new LaneOptions { Like = Ring(2), Zone = false, Nickname = "Li01_lane" }));
        var bretonia = Run(session, LanePlans.Create(session, "Li01", At(-30000, 0, 30000, "A"), At(-30000, 0, 45000, "B"),
            new LaneOptions { Faction = "br_n_grp", Zone = false }));

        Assert.Equal(SceneWorkspace.Object("Li01_lane_1"), liked.Rings[0]);
        Assert.Null(liked.Zone);
        Assert.Equal(("4", "li_n_grp", "trade_lane_ring_li_01"), (Line(session, liked.Rings[0], "difficulty_level"),
            Line(session, liked.Rings[0], "reputation"), Line(session, liked.Rings[0], "loadout")));
        Assert.Equal(("br_n_grp", "trade_lane_ring_br_02", "pilot_solar_easiest"),
            (Line(session, bretonia.Rings[0], "reputation"), Line(session, bretonia.Rings[0], "loadout"),
                Line(session, bretonia.Rings[0], "pilot")));
    }

    /// <summary>
    /// The lane zone of a lane of two rings meets the lane zone rule, also for a short lane: it is 1000 m longer than
    /// the lane, and its width and height are at most 500 and at most one eleventh of its length.
    /// </summary>
    [Theory]
    [InlineData(1000, "181, 181, 2000")]
    [InlineData(8000, "500, 500, 9000")]
    public void TheZoneOfALaneOfTwoRingsMeetsTheLaneZoneRule(float length, string size)
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.Create(session, "Li01", At(30000, 0, 30000, "A"), At(30000, 0, 30000 + length, "B"),
            new LaneOptions { Rings = 2 }));

        Assert.Equal(size, Line(session, plan.Zone!, "size"));
        var values = LaneWorkspace.Vector(size);
        Assert.True(LaneZones.IsLaneZone(plan.Zone!.Id.Key!, ShapeKind.Box, values));
        Assert.Equal(plan.Zone, LanePlans.ZoneOf(session, LanePlans.LaneOf(session, plan.Rings[0])!));
    }

    /// <summary>A ring of a new lane inside a zone with damage or inside an object gets a warning, and the zone counts
    /// first.</summary>
    [Fact]
    public void ARingInsideAZoneWithDamageOrAnObjectGivesAWarning()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var planet = Run(session, LanePlans.Create(session, "Li01", At(-30000, 0, 40000, "A"), At(30000, 0, 40000, "B")));
        var sun = LanePlans.Create(session, "Li01", At(-30000, 0, 0, "A"), At(30000, 0, 0, "B"));

        Assert.Equal("Li01_Trade_Lane_Ring_9 stands inside Li01_planet. Ships on the lane can take damage or hit it. Move " +
                     "the ring.", Assert.Single(planet.Warnings).Message);
        Assert.Equal(("pos", Ring(9)), (planet.Warnings[0].Key, planet.Warnings[0].Target));
        Assert.Equal(3, sun.Warnings.Count);
        Assert.All(sun.Warnings, w => Assert.EndsWith("stands inside Zone_Li01_sun_death. Ships on the lane can take damage " +
                                                      "or hit it. Move the ring.", w.Message));
    }

    /// <summary>A new ring after a middle ring goes to the middle between its neighbors and takes the links of its
    /// place. It copies the lines of the ring before it. The lane zone does not change.</summary>
    [Fact]
    public void ARingInTheMiddleTakesTheLinksOfItsPlace()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.InsertRing(session, Ring(2), LanePlace.After));

        Assert.Equal((Ring(5), 3, 5), (plan.Ring, plan.Index, plan.Count));
        Assert.Equal([Ring(1), Ring(2), Ring(5), Ring(3), Ring(4)], plan.Rings);
        Assert.Equal([Ring(2), Ring(3)], plan.Changes);
        Assert.Null(plan.Zone);
        Assert.Equal([("nickname", "Li01_Trade_Lane_Ring_5"), ("ids_name", "60245"), ("pos", "11250, 0, -20000"),
            ("rotate", "0, -90, 0"), ("archetype", "Trade_Lane_Ring"), ("ids_info", "66170"),
            ("prev_ring", "Li01_Trade_Lane_Ring_2"), ("next_ring", "Li01_Trade_Lane_Ring_3"), ("behavior", "NOTHING"),
            ("reputation", "li_n_grp")], ConnectionWorkspace.Lines(session, Ring(5)));
        Assert.Equal("Li01_Trade_Lane_Ring_5", Line(session, Ring(2), "next_ring"));
        Assert.Equal("Li01_Trade_Lane_Ring_5", Line(session, Ring(3), "prev_ring"));
        Assert.Equal([Ring(1), Ring(2), Ring(5), Ring(3), Ring(4)], LanePlans.LaneOf(session, Ring(4))!.Rings);
        Assert.Equal("Insert ring after Li01_Trade_Lane_Ring_2", session.UndoLabel);

        session.Undo();
        Assert.Null(session.Get(Ring(5)));
        Assert.Equal("Li01_Trade_Lane_Ring_3", Line(session, Ring(2), "next_ring"));
        Assert.Equal("Li01_Trade_Lane_Ring_2", Line(session, Ring(3), "prev_ring"));
    }

    /// <summary>A new ring after the last ring goes one spacing beyond the end. It becomes the last ring with the end
    /// name, and the lane zone follows the new end with its margin.</summary>
    [Fact]
    public void ARingAfterTheEndMakesTheLaneLongerAndTheZoneFollows()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.InsertRing(session, Ring(4), LanePlace.After, nickname: "Li01_new_end"));

        var added = SceneWorkspace.Object("Li01_new_end");
        Assert.Equal((added, 5, 5), (plan.Ring, plan.Index, plan.Count));
        Assert.Equal("30000, 0, -20000", Line(session, added, "pos"));
        Assert.Equal(("Li01_Trade_Lane_Ring_4", null, "505"),
            (Line(session, added, "prev_ring"), Line(session, added, "next_ring"), Line(session, added, "tradelane_space_name")));
        Assert.Null(Line(session, Ring(4), "tradelane_space_name"));
        Assert.Equal(LaneWorkspace.Zone, plan.Zone);
        Assert.Equal(("15000, 0, -20000", "500, 500, 31000"),
            (Line(session, LaneWorkspace.Zone, "pos"), Line(session, LaneWorkspace.Zone, "size")));
        Assert.Equal("0, -90, 0", Line(session, LaneWorkspace.Zone, "rotate"));
        Assert.Equal(LaneWorkspace.Zone, LanePlans.ZoneOf(session, LanePlans.LaneOf(session, added)!));
    }

    /// <summary>A new ring before the first ring makes the lane longer at its start, with the start name. A ring before a
    /// middle ring and an object that is no ring refuse.</summary>
    [Fact]
    public void ARingBeforeTheStartTakesTheStartName()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var middle = Refusal(LanePlans.InsertRing(session, Ring(2), LanePlace.Before), LanePlans.BeforeNotFirst);
        var sun = Refusal(LanePlans.InsertRing(session, SceneWorkspace.Object("Li01_sun"), LanePlace.After),
            LanePlans.NotLaneRing);
        var plan = Run(session, LanePlans.InsertRing(session, Ring(1), LanePlace.Before));

        Assert.Equal("before takes only the first ring of a lane. Use after for another ring.", middle.Message);
        Assert.Equal("Li01_sun is not a ring of a trade lane.", sun.Message);
        Assert.Equal((Ring(5), 1, 5), (plan.Ring, plan.Index, plan.Count));
        Assert.Equal(("-7500, 0, -20000", null, "Li01_Trade_Lane_Ring_1", "504"),
            (Line(session, Ring(5), "pos"), Line(session, Ring(5), "prev_ring"), Line(session, Ring(5), "next_ring"),
                Line(session, Ring(5), "tradelane_space_name")));
        Assert.Equal("Li01_Trade_Lane_Ring_5", Line(session, Ring(1), "prev_ring"));
        Assert.Null(Line(session, Ring(1), "tradelane_space_name"));
        Assert.Equal("7500, 0, -20000", Line(session, LaneWorkspace.Zone, "pos"));
    }

    /// <summary>A delete of a middle ring links its neighbors to each other. The new links are parts of the lane, so
    /// the editor does not ask, and the zone does not change. Undo gives each link back.</summary>
    [Fact]
    public void ARemovedMiddleRingLinksItsNeighbors()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.RemoveRings(session, [Ring(2)]));

        Assert.Equal([Ring(2)], plan.Deletes.Select(d => d.Section));
        Assert.False(plan.NeedsConfirmation);
        Assert.Equal([Ring(1), Ring(3)], plan.Changes);
        Assert.Equal(["li01.ini: the next_ring line of Li01_Trade_Lane_Ring_1 names Li01_Trade_Lane_Ring_3",
            "li01.ini: the prev_ring line of Li01_Trade_Lane_Ring_3 names Li01_Trade_Lane_Ring_1"],
            plan.Fixes.Select(f => f.Text));
        Assert.Equal("Li01_Trade_Lane_Ring_3", Line(session, Ring(1), "next_ring"));
        Assert.Equal("Li01_Trade_Lane_Ring_1", Line(session, Ring(3), "prev_ring"));
        Assert.Equal("11250, 0, -20000", Line(session, LaneWorkspace.Zone, "pos"));
        Assert.Equal([Ring(1), Ring(3), Ring(4)], LanePlans.LaneOf(session, Ring(1))!.Rings);

        session.Undo();
        Assert.NotNull(session.Get(Ring(2)));
        Assert.Equal("Li01_Trade_Lane_Ring_2", Line(session, Ring(1), "next_ring"));
        Assert.Equal("Li01_Trade_Lane_Ring_2", Line(session, Ring(3), "prev_ring"));
    }

    /// <summary>A delete of an end ring makes its neighbor the new end with the end name, and the lane zone follows the
    /// new end.</summary>
    [Fact]
    public void ARemovedEndRingMovesTheEndNameAndTheZone()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, ScenePlans.Delete(session, [Ring(4)]));

        Assert.False(plan.NeedsConfirmation);
        Assert.Null(Line(session, Ring(3), "next_ring"));
        Assert.Equal("505", Line(session, Ring(3), "tradelane_space_name"));
        Assert.Equal(("7500, 0, -20000", "500, 500, 16000"),
            (Line(session, LaneWorkspace.Zone, "pos"), Line(session, LaneWorkspace.Zone, "size")));
        Assert.Contains(LaneWorkspace.Zone, plan.Changes);
    }

    /// <summary>A delete of two neighbor rings in one call links the rings at their two sides.</summary>
    [Fact]
    public void TwoRemovedNeighborRingsLinkTheRingsAtTheirSides()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        Run(session, LanePlans.RemoveRings(session, [Ring(2), Ring(3)]));

        Assert.Equal("Li01_Trade_Lane_Ring_4", Line(session, Ring(1), "next_ring"));
        Assert.Equal("Li01_Trade_Lane_Ring_1", Line(session, Ring(4), "prev_ring"));
        Assert.Equal([Ring(1), Ring(4)], LanePlans.LaneOf(session, Ring(4))!.Rings);
    }

    /// <summary>When one ring or none stays, the whole lane goes with its lane zone, and the editor asks. A delete of
    /// each ring of the lane also takes the zone.</summary>
    [Fact]
    public void ALaneWithOneRingLeftGoesWithItsZone()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var three = LanePlans.RemoveRings(session, [Ring(2), Ring(3), Ring(4)]);
        var all = ScenePlans.Delete(session, [Ring(1), Ring(2), Ring(3), Ring(4)]);

        Assert.True(three.NeedsConfirmation);
        Assert.Equal([(Ring(1), "the trade lane of Li01_Trade_Lane_Ring_2"), (LaneWorkspace.Zone, "the trade lane of Li01_Trade_Lane_Ring_2")],
            three.Deletes.Where(d => d.Why == DeleteReason.Cascade).Select(d => (d.Section, d.Cause!)));
        Assert.True(all.NeedsConfirmation);
        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4), LaneWorkspace.Zone], all.Deletes.Select(d => d.Section));
        Run(session, all);
        Assert.Null(session.Get(LaneWorkspace.Zone));
        session.Undo();
        Assert.NotNull(session.Get(LaneWorkspace.Zone));
        Assert.Equal("Li01_Trade_Lane_Ring_2", Line(session, Ring(1), "next_ring"));
    }

    /// <summary>A delete of the lane of a ring takes all rings and the lane zone, and it always asks. An object that is
    /// no ring refuses.</summary>
    [Fact]
    public void ADeleteOfALaneTakesAllRingsAndTheZoneAndAsks()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var gate = Refusal(LanePlans.Delete(session, SceneWorkspace.Object("Li01_to_Li02")), LanePlans.NotLaneRing);
        Refusal(LanePlans.RemoveRings(session, [SceneWorkspace.Object("Li01_to_Li02")]), LanePlans.NotLaneRing);
        var plan = Run(session, LanePlans.Delete(session, Ring(3)));

        Assert.Equal("Li01_to_Li02 is not a ring of a trade lane.", gate.Message);
        Assert.True(plan.NeedsConfirmation);
        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4), LaneWorkspace.Zone], plan.Deletes.Select(d => d.Section));
        Assert.All(plan.Deletes, d => Assert.Null(session.Get(d.Section)));
    }

    /// <summary>A lane zone that the editor cannot change stays, and the delete warns and asks.</summary>
    [Fact]
    public void AZoneThatTheEditorCannotChangeStaysWithAWarning()
    {
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text + """

            [zone]
            nickname = Zone_Li01_Tradelane_1
            pos = 0, 0, 90000
            shape = SPHERE
            size = 10
            """);
        var session = ws.Session("act");

        var end = Run(session, LanePlans.RemoveRings(session, [Ring(4)]));

        Assert.True(end.NeedsConfirmation);
        Assert.StartsWith("The lane zone Zone_Li01_Tradelane_1 stays as it is. The editor cannot change it:",
            Assert.Single(end.Warnings).Message);
        Assert.Equal("505", Line(session, Ring(3), "tradelane_space_name"));
        session.Undo();
        var all = LanePlans.Delete(session, Ring(1));
        Assert.Equal(4, all.Deletes.Count);
        Assert.StartsWith("The lane zone Zone_Li01_Tradelane_1 stays. The editor cannot delete it:", Assert.Single(all.Warnings).Message);
    }

    /// <summary>A delete of a ring of a lane that goes in a circle cuts the circle at that ring. Its neighbors become
    /// the ends of the lane.</summary>
    [Fact]
    public void ADeleteCutsALaneThatGoesInACircle()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_1"]));
        session.Execute(EditCommand.SetLinesFromText(Ring(1), "prev_ring", ["Li01_Trade_Lane_Ring_4"]));
        var loop = LanePlans.LaneOf(session, Ring(2))!;
        Assert.True(loop.Loops);
        Assert.Equal(4, loop.Rings.Count);
        Assert.Null(LanePlans.ZoneOf(session, loop));

        Run(session, ScenePlans.Delete(session, [Ring(1)]));

        var lane = LanePlans.LaneOf(session, Ring(3))!;
        Assert.False(lane.Loops);
        Assert.Equal([Ring(2), Ring(3), Ring(4)], lane.Rings);
    }

    /// <summary>A one-way link to a ring that goes refuses the delete, and the text names the repair tool.</summary>
    [Fact]
    public void AOneWayLinkToARemovedRingRefuses()
    {
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text + """

            [Object]
            nickname = Li01_stray_ring
            pos = 50000, 0, 50000
            archetype = Trade_Lane_Ring
            next_ring = Li01_Trade_Lane_Ring_3
            """);
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Ring(3)]);

        Assert.Equal("Li01_stray_ring links to Li01_Trade_Lane_Ring_3 in its next_ring line, but Li01_Trade_Lane_Ring_3 does " +
                     "not link back to Li01_stray_ring. Use trade_lane_repair on Li01_stray_ring first, or keep " +
                     "Li01_Trade_Lane_Ring_3.", Assert.Single(plan.Refusals).Message);
    }

    /// <summary>A repair writes a broken link again from the order of the rings on the line. A lane with good links
    /// changes nothing.</summary>
    [Fact]
    public void ARepairWritesABrokenLinkAgain()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(2), "next_ring"));

        var plan = Run(session, LanePlans.Repair(session, Ring(4)));
        var again = LanePlans.Repair(session, Ring(1));

        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], plan.Rings);
        Assert.Equal([Ring(2)], plan.Changes);
        Assert.Equal("Li01_Trade_Lane_Ring_3", Line(session, Ring(2), "next_ring"));
        Assert.Equal("Repair trade lane of Li01_Trade_Lane_Ring_4", session.UndoLabel);
        Assert.True(again.CanRun);
        Assert.Empty(again.Changes);
    }

    /// <summary>
    /// A ring without links on the line of a lane joins the lane in the order of the line, from a repair of any ring of
    /// the lane or of the ring itself. The end names move to the end rings.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ARepairJoinsALoneRingOnTheLine(int from)
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(3), "prev_ring"));
        session.Execute(EditCommand.RemoveName(Ring(3), "next_ring"));
        session.Execute(EditCommand.SetLinesFromText(Ring(2), "next_ring", ["Li01_Trade_Lane_Ring_4"]));
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "prev_ring", ["Li01_Trade_Lane_Ring_2"]));
        session.Execute(EditCommand.RemoveName(Ring(4), "tradelane_space_name"));
        session.Execute(EditCommand.SetLinesFromText(Ring(2), "tradelane_space_name", ["505"]));

        var plan = Run(session, LanePlans.Repair(session, Ring(from)));

        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], plan.Rings);
        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], LanePlans.LaneOf(session, Ring(1))!.Rings);
        Assert.Equal(("504", null, null, "505"), (Line(session, Ring(1), "tradelane_space_name"),
            Line(session, Ring(2), "tradelane_space_name"), Line(session, Ring(3), "tradelane_space_name"),
            Line(session, Ring(4), "tradelane_space_name")));
    }

    /// <summary>A repair keeps the positions, so rings off one line refuse.</summary>
    [Fact]
    public void ARepairOfRingsOffOneLineRefuses()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(3), "pos", ["15000, 0, -14000"]));

        var plan = Refusal(LanePlans.Repair(session, Ring(1)), LanePlans.LaneNotLine);

        Assert.Equal("The rings of the lane of Li01_Trade_Lane_Ring_1 do not stand on one line. Move the rings, or delete " +
                     "the lane and make it again.", plan.Message);
        Assert.Equal(Ring(3), plan.Target);
    }

    /// <summary>The lane zone covers the first and the last ring. A lane whose end ring moves away has no zone, and a
    /// lane of one ring has none.</summary>
    [Fact]
    public void ZoneOfFindsTheZoneThatCoversTheEnds()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal(LaneWorkspace.Zone, LanePlans.ZoneOf(session, LanePlans.LaneOf(session, Ring(2))!));
        Assert.Null(LanePlans.ZoneOf(session, LanePlans.LaneOf(session, SceneWorkspace.Object("Li01_sun"))!));
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "pos", ["40000, 0, -20000"]));
        Assert.Null(LanePlans.ZoneOf(session, LanePlans.LaneOf(session, Ring(2))!));
        Assert.Null(LanePlans.LaneOf(session, SceneWorkspace.Zone("Zone_Li01_sun_death")));
    }

    /// <summary>The rotation of a ring turns the front of the ring along the direction, also with a height
    /// difference.</summary>
    [Fact]
    public void TheRotationRuleTurnsTheFrontAlongTheDirection()
    {
        foreach (var direction in new[] { new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(-3, 4, 2), new Vector3(1, -7, -1) })
        {
            var front = Vector3.TransformNormal(-Vector3.UnitZ, MathHelper.MatrixFromEulerDegrees(LanePlans.RotationOf(direction)));
            Assert.Equal(Vector3.Normalize(direction), front, new VectorNear(1e-5f));
        }
        Assert.Equal("0, 180, 0", LanePlans.RotationText(LanePlans.RotationOf(new Vector3(0, 0, 1))));
        Assert.Equal("36.87, -90, 0", LanePlans.RotationText(LanePlans.RotationOf(new Vector3(4, 3, 0))));
    }

    /// <summary>Two vectors that differ by at most a tolerance on each axis.</summary>
    private sealed class VectorNear(float tolerance) : System.Collections.Generic.IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) =>
            MathF.Abs(a.X - b.X) <= tolerance && MathF.Abs(a.Y - b.Y) <= tolerance && MathF.Abs(a.Z - b.Z) <= tolerance;

        public int GetHashCode(Vector3 v) => 0;
    }
}
