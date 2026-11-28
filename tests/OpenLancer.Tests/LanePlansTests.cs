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
/// house li and a sun at the center with a death zone of 12000 m. It has a jump gate, a planet with a name and a planet
/// without a name. It also has a lane of four rings along the x axis with its lane zone. The archetypes have a
/// <c>solar_radius</c>. All names and values are invented.
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

    /// <summary>The solar archetypes of the lanes, after the archetypes of <see cref="SceneWorkspace.Solars"/>.</summary>
    public const string Solars = """

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
        """;

    public static TestWorkspace Create(string? li01Text = null)
    {
        var ws = SceneWorkspace.Create();
        ws.WriteBase(ListWorkspace.SolarPath, SceneWorkspace.Solars + Solars);
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
    /// A lane of 30000 m gets one ring each 7500 m, plus one: five rings. They have the retail lines in the retail order
    /// and the next free ring nicknames. They get the names of the rule data, and the loadout and the pilot of the house
    /// of the local faction. The end rings get the end names in the text of the mod. The lane zone is a box on the lane that meets
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

    /// <summary>A lane needs 2 to 100 rings with 1000 to 20000 m between two rings. The refusal gives the length of the
    /// lane, the spacing of the count and the counts that fit, or the distances that fit when no count fits. A spacing
    /// outside the retail range gives a warning, and the lane is made.</summary>
    [Fact]
    public void TheSpacingRefusesOutsideItsLimitsAndWarnsOutsideTheRetailRange()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        var (start, end) = (At(30000, 0, 30000, "A"), At(30000, 0, 60000, "B"));

        var far = Refusal(LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 2 }), LanePlans.LaneRings);
        var many = Refusal(LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 101 }), LanePlans.LaneRings);
        var tooShort = Refusal(LanePlans.Create(session, "Li01", start, At(30000, 0, 30500, "B")), LanePlans.LaneRings);
        var close = Run(session, LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 7 }));

        const string limits = "A trade lane needs 2 to 100 rings, with 1000 to 20000 m between two rings.";
        Assert.Equal($"{limits} The lane is 30000 m long, so 2 rings stand 30000 m apart. Give 3 to 31 rings.", far.Message);
        Assert.Equal($"{limits} The lane is 30000 m long, so 101 rings stand 300 m apart. Give 3 to 31 rings.", many.Message);
        Assert.Equal($"{limits} The lane is 500 m long, so 2 rings stand 500 m apart. Give two ends that are 1000 to " +
                     "1980000 m apart.", tooShort.Message);
        Assert.Equal("rings", LanePlans.Create(session, "Li01", start, end, new LaneOptions { Rings = 2 }).RefusedPart);
        Assert.Equal(7, close.Rings.Count);
        Assert.Equal("The rings of the lane are 5000 m apart. Retail lanes have 5165 to 9445 m between two rings. Give " +
                     "another number of rings, or move an end.", Assert.Single(close.Warnings).Message);
    }

    /// <summary>
    /// An end with an object and no point goes on the line toward the other end. Its distance from the object is the
    /// solar radius of the archetype of the object plus 2000 m. The end takes the name of the object.
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
        Assert.Equal(7, plan.Rings.Count);
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// The ring count is the length divided by 7500 m, rounded to the nearest whole number, plus one. A half rounds up.
    /// A length of 43600 m gives 5.81 spacings and 7 rings. A length of 32000 m gives 4.27 spacings and 5 rings.
    /// </summary>
    [Theory]
    [InlineData(18750, 4)]
    [InlineData(32000, 5)]
    [InlineData(43600, 7)]
    public void TheRingCountIsTheRoundedLengthBySpacingPlusOne(float length, int rings)
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.Create(session, "Li01", At(30000, 0, 30000, "A"), At(30000, 0, 30000 + length, "B")));

        Assert.Equal(rings, plan.Rings.Count);
        Assert.Equal(length / (rings - 1), plan.Spacing!.Value, 0.01f);
    }

    /// <summary>Two objects that stand closer than their two clearances give ends that pass each other. The start ring
    /// would then have the name of the end object at the far end. The plan refuses.</summary>
    [Fact]
    public void EndsThatPassEachOtherRefuse()
    {
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text + """

            [Object]
            nickname = Li01_buoy
            ids_name = 506
            pos = 63000, 0, 0
            archetype = lane_gate
            """);
        var session = ws.Session("act");
        var gate = new TradeLaneEnd { Near = SceneWorkspace.Object("Li01_to_Li02") };
        var buoy = new TradeLaneEnd { Near = SceneWorkspace.Object("Li01_buoy") };

        var plan = LanePlans.Create(session, "Li01", gate, buoy);

        Assert.Equal("The ends are too close for the clearance at their objects. The start ring passes the end ring. Give " +
                     "start and end as points, or pick objects that are farther apart.",
            Refusal(plan, LanePlans.LaneEnd).Message);
        Assert.Equal("end_near", plan.RefusedPart);
    }

    /// <summary>
    /// The rotation of each ring turns its front along the lane. A flat lane needs the yaw, and a lane with a height
    /// difference needs the yaw and the pitch. The rotation rule of the engine (<see cref="MathHelper.MatrixFromEulerDegrees(Vector3)"/>)
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
    /// The lane zone of a lane of two rings meets the lane zone rule, also for a short lane. It is 1000 m longer than
    /// the lane. Its width and height are at most 500 and at most one eleventh of its length.
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

    /// <summary>
    /// The game makes a lane only from objects with the ring archetype. A link line of the first ring can name a jump
    /// gate after a raw edit. The lane then ends at the first ring, so a delete of the lane leaves the gate and its pair.
    /// </summary>
    [Fact]
    public void ALaneDeleteTakesNoObjectThatIsNoRing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(1), "prev_ring", ["Li01_to_Li02"]));

        var lane = LanePlans.LaneOf(session, Ring(3))!;
        var plan = Run(session, LanePlans.Delete(session, Ring(3)));

        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], lane.Rings);
        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4), LaneWorkspace.Zone], plan.Deletes.Select(d => d.Section));
        Assert.NotNull(session.Get(SceneWorkspace.Object("Li01_to_Li02")));
        Assert.NotNull(session.Get(SceneWorkspace.Li02("Object", "Li02_to_Li01")));
    }

    /// <summary>
    /// A link to an object that is no ring ends the lane. A delete of the first ring then moves the start name to the
    /// next ring, and that ring gets no link to the object. A new ring after the last ring becomes the new end, and the
    /// object gets no link line.
    /// </summary>
    [Fact]
    public void ALinkToAnObjectThatIsNoRingEndsTheLane()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(1), "prev_ring", ["Li01_sun"]));
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_sun"]));

        Run(session, ScenePlans.Delete(session, [Ring(1)]));
        var insert = Run(session, LanePlans.InsertRing(session, Ring(4), LanePlace.After));

        Assert.Null(Line(session, Ring(2), "prev_ring"));
        Assert.Equal("504", Line(session, Ring(2), "tradelane_space_name"));
        Assert.Equal([Ring(2), Ring(3), Ring(4), insert.Ring!], insert.Rings);
        Assert.Equal(insert.Ring!.Id.Key, Line(session, Ring(4), "next_ring"));
        Assert.Null(Line(session, insert.Ring!, "next_ring"));
        Assert.Equal("505", Line(session, insert.Ring!, "tradelane_space_name"));
        Assert.Null(Line(session, SceneWorkspace.Object("Li01_sun"), "prev_ring"));
        Assert.Equal("30000, 0, -20000", Line(session, insert.Ring!, "pos"));
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

    /// <summary>
    /// A one-way link to a ring that goes refuses the delete. The stray ring stands off the line of the lane, so a repair
    /// refuses. The text names the fixes that work: a delete of the stray ring in the same delete, or a change in the
    /// raw INI block.
    /// </summary>
    [Fact]
    public void AOneWayLinkToARemovedRingRefuses()
    {
        using var ws = LaneWorkspace.Create(StrayText("50000, 0, 50000", "next_ring"));
        var session = ws.Session("act");
        var stray = SceneWorkspace.Object("Li01_stray_ring");

        var plan = ScenePlans.Delete(session, [Ring(3)]);

        Assert.Equal("Li01_stray_ring links to Li01_Trade_Lane_Ring_3 in its next_ring line, but Li01_Trade_Lane_Ring_3 does " +
                     "not link back to Li01_stray_ring. Delete Li01_stray_ring in the same delete, remove its next_ring " +
                     "line in the raw INI block, or keep Li01_Trade_Lane_Ring_3.", Assert.Single(plan.Refusals).Message);
        Assert.False(LanePlans.Repair(session, stray).CanRun);
    }

    /// <summary>
    /// The link of a stray ring to the third ring is no part of the lane. The third ring links both ways with the second
    /// ring. So a delete of the stray ring and the third ring, in each order, relinks the lane and takes no
    /// other ring. A delete of the stray ring alone does not change the lane. The walk of the HUD follows the stray link.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStrayRingIsNoPartOfTheLaneThatItLinksTo(bool strayFirst)
    {
        using var ws = LaneWorkspace.Create(StrayText("-1000, 0, -20000", "next_ring"));
        var session = ws.Session("act");
        var stray = SceneWorkspace.Object("Li01_stray_ring");

        var alone = ScenePlans.Delete(session, [stray]);
        var plan = Run(session, ScenePlans.Delete(session, strayFirst ? [stray, Ring(3)] : [Ring(3), stray]));

        Assert.Equal([stray], alone.Deletes.Select(d => d.Section));
        Assert.Empty(alone.Changes);
        Assert.Equal([Ring(1), Ring(2), Ring(4)], LanePlans.LaneOf(session, Ring(1))!.Rings);
        Assert.Equal(2, plan.Deletes.Count);
        Assert.Equal(("11250, 0, -20000", "500, 500, 23500"),
            (Line(session, LaneWorkspace.Zone, "pos"), Line(session, LaneWorkspace.Zone, "size")));
        session.Undo();
        Assert.Equal([stray], LanePlans.LaneOf(session, stray)!.Rings);
        Assert.Equal([stray, Ring(3), Ring(4)], LanePlans.LaneOf(session, stray, strays: true)!.Rings);
        Assert.Equal([stray], LanePlans.InsertRing(session, stray, LanePlace.After).Changes);
    }

    /// <summary>The text of the lane of Li01 with a stray ring at a position, with one link line to the third ring.</summary>
    private static string StrayText(string pos, string key) => LaneWorkspace.Li01Text + $"""

        [Object]
        nickname = Li01_stray_ring
        pos = {pos}
        archetype = Trade_Lane_Ring
        {key} = Li01_Trade_Lane_Ring_3
        """;

    /// <summary>A stray ring on the line of the lane can join the lane with a repair. So the text of the one-way link
    /// names the repair, and the delete runs after the repair.</summary>
    [Fact]
    public void AOneWayLinkFromARingOnTheLineNamesTheRepair()
    {
        using var ws = LaneWorkspace.Create(StrayText("30000, 0, -20000", "prev_ring"));
        var session = ws.Session("act");
        var stray = SceneWorkspace.Object("Li01_stray_ring");

        var plan = ScenePlans.Delete(session, [Ring(3)]);

        Assert.Equal("Li01_stray_ring links to Li01_Trade_Lane_Ring_3 in its prev_ring line, but Li01_Trade_Lane_Ring_3 does " +
                     "not link back to Li01_stray_ring. Use trade_lane_repair on Li01_stray_ring first, delete " +
                     "Li01_stray_ring in the same delete, or keep Li01_Trade_Lane_Ring_3.", Assert.Single(plan.Refusals).Message);
        Run(session, LanePlans.Repair(session, stray));
        Run(session, ScenePlans.Delete(session, [Ring(3)]));
        Assert.Equal([Ring(1), Ring(2), Ring(4), stray], LanePlans.LaneOf(session, Ring(1))!.Rings);
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

    /// <summary>
    /// A raw edit can delete a middle ring and leave its neighbors with links to a ring that is not there. A repair from
    /// either side finds the rings at the other side on the line of the lane, and it links them again.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ARepairJoinsTheRingsAtTheSidesOfARingThatARawEditDeleted(int from)
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(Ring(2)));

        var plan = Run(session, LanePlans.Repair(session, Ring(from)));

        Assert.Equal([Ring(1), Ring(3), Ring(4)], plan.Rings);
        Assert.Equal([Ring(1), Ring(3), Ring(4)], LanePlans.LaneOf(session, Ring(4))!.Rings);
        Assert.Equal(("Li01_Trade_Lane_Ring_3", "Li01_Trade_Lane_Ring_1"),
            (Line(session, Ring(1), "next_ring"), Line(session, Ring(3), "prev_ring")));
        Assert.Equal(("504", "505"), (Line(session, Ring(1), "tradelane_space_name"), Line(session, Ring(4), "tradelane_space_name")));
    }

    /// <summary>
    /// An end ring without links keeps its end name. It stands past the last ring of the rest of the lane. A repair of
    /// the lone ring or of the lane joins it at the end. A lone ring more than 20000 m past the end does not join.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ARepairJoinsALoneEndRingPastTheEndOfTheLane(int from)
    {
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text + """

            [Object]
            nickname = Li01_far_ring
            pos = 50000, 0, -20000
            archetype = Trade_Lane_Ring
            """);
        var session = ws.Session("act");
        var far = SceneWorkspace.Object("Li01_far_ring");
        session.Execute(EditCommand.RemoveName(Ring(4), "prev_ring"));
        session.Execute(EditCommand.RemoveName(Ring(3), "next_ring"));

        var plan = Run(session, LanePlans.Repair(session, Ring(from)));
        var alone = LanePlans.Repair(session, far);

        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], plan.Rings);
        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], LanePlans.LaneOf(session, Ring(2))!.Rings);
        Assert.Equal(("504", "505"), (Line(session, Ring(1), "tradelane_space_name"), Line(session, Ring(4), "tradelane_space_name")));
        Assert.True(alone.CanRun);
        Assert.Equal([far], alone.Rings);
        Assert.Empty(alone.Changes);
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

    /// <summary>The text of the lane of Li01 with a link line on the sun that names the first ring.</summary>
    internal static string SunLinkText() => LaneWorkspace.Li01Text.Replace("archetype = lane_sun\n",
        "archetype = lane_sun\nnext_ring = Li01_Trade_Lane_Ring_1\n", StringComparison.Ordinal);

    /// <summary>
    /// The game makes lanes only from objects with the ring archetype. So a sun with a link line is no ring: each lane
    /// plan refuses it, and the lane of a ring does not take it. A delete of the lane takes only the rings and the zone.
    /// A delete of the sun deletes only the sun.
    /// </summary>
    [Fact]
    public void AnObjectWithALinkLineThatIsNoRingIsNoPartOfTheLane()
    {
        var text = SunLinkText();
        Assert.Contains("next_ring = Li01_Trade_Lane_Ring_1\n[Object]\nnickname = Li01_to_Li02", text);
        using var ws = LaneWorkspace.Create(text);
        var session = ws.Session("act");
        var sun = SceneWorkspace.Object("Li01_sun");

        Assert.Equal("Li01_sun is not a ring of a trade lane.",
            Refusal(LanePlans.InsertRing(session, sun, LanePlace.After), LanePlans.NotLaneRing).Message);
        Refusal(LanePlans.InsertRing(session, sun, LanePlace.Before), LanePlans.NotLaneRing);
        Refusal(LanePlans.Repair(session, sun), LanePlans.NotLaneRing);
        Refusal(LanePlans.Delete(session, sun), LanePlans.NotLaneRing);
        Refusal(LanePlans.RemoveRings(session, [sun]), LanePlans.NotLaneRing);
        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], LanePlans.LaneOf(session, Ring(1))!.Rings);
        Assert.Equal([sun], Run(session, ScenePlans.Delete(session, [sun])).Deletes.Select(d => d.Section));
        Assert.Empty(session.Get(Ring(1))!.Lines("prev_ring"));
        session.Undo();

        var lane = Run(session, LanePlans.Delete(session, Ring(2)));

        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4), LaneWorkspace.Zone], lane.Deletes.Select(d => d.Section));
        Assert.NotNull(session.Get(sun));
        Assert.Null(Line(session, sun, "next_ring"));
    }

    /// <summary>
    /// The game reads a link line only on a ring. A link line of a jump gate that names a ring that goes does not stop
    /// the delete. The line goes with the ring, and the delete asks. The gate and its partner stay, and the lane links
    /// again.
    /// </summary>
    [Fact]
    public void ALinkLineOfAnObjectThatIsNoRingGoesWithTheRing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        var gate = SceneWorkspace.Object("Li01_to_Li02");
        session.Execute(EditCommand.SetLinesFromText(gate, "next_ring", ["Li01_Trade_Lane_Ring_2"]));

        var plan = Run(session, ScenePlans.Delete(session, [Ring(2)]));

        Assert.Equal([Ring(2)], plan.Deletes.Select(d => d.Section));
        Assert.True(plan.NeedsConfirmation);
        var fix = Assert.Single(plan.Fixes, f => f.Section.Equals(gate));
        Assert.Equal(("next_ring", "li01.ini: the 'next_ring' line of Li01_to_Li02", false), (fix.Key, fix.Text, fix.Part));
        Assert.All(plan.Fixes.Where(f => !f.Section.Equals(gate)), f => Assert.True(f.Part));
        Assert.Null(Line(session, gate, "next_ring"));
        Assert.Equal("Li02, Li02_to_Li01, gate_tunnel_bretonia", Line(session, gate, "goto"));
        Assert.NotNull(session.Get(SceneWorkspace.Li02("Object", "Li02_to_Li01")));
        Assert.Equal([Ring(1), Ring(3), Ring(4)], LanePlans.LaneOf(session, Ring(1))!.Rings);
        session.Undo();
        Assert.Equal("Li01_Trade_Lane_Ring_2", Line(session, gate, "next_ring"));
    }

    /// <summary>
    /// A new ring after the end at a place off the axis turns the end of the lane. The lane zone turns with the lane, so
    /// it still covers the lane and gives no warning.
    /// </summary>
    [Fact]
    public void AnInsertThatTurnsTheLaneTurnsItsZone()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, LanePlans.InsertRing(session, Ring(4), LanePlace.After, new Vector3(30000, 0, -10000),
            "Li01_turn"));

        Assert.Equal(LaneWorkspace.Zone, plan.Zone);
        Assert.Equal(("15000, 0, -15000", "0, -108.43, 0", "500, 500, 32623"), (Line(session, LaneWorkspace.Zone, "pos"),
            Line(session, LaneWorkspace.Zone, "rotate"), Line(session, LaneWorkspace.Zone, "size")));
        Assert.Equal(LaneWorkspace.Zone, LanePlans.ZoneOf(session, LanePlans.LaneOf(session, Ring(1))!));
        Assert.Empty(LaneChecks.Check(session, LaneWorkspace.Zone));
    }

    /// <summary>
    /// A lane zone keeps its width only while the zone meets the lane zone rule. A lane of 1500 m gets a zone of 2500 m,
    /// so its width and height become one eleventh of that. The zone stays the zone of the lane.
    /// </summary>
    [Fact]
    public void AShorterLaneMakesItsZoneNarrower()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        Run(session, LanePlans.InsertRing(session, Ring(1), LanePlace.After, new Vector3(1500, 0, -20000), "Li01_near"));

        Run(session, ScenePlans.Delete(session, [Ring(2), Ring(3), Ring(4)]));

        Assert.Equal(("750, 0, -20000", "227, 227, 2500"),
            (Line(session, LaneWorkspace.Zone, "pos"), Line(session, LaneWorkspace.Zone, "size")));
        var lane = LanePlans.LaneOf(session, Ring(1))!;
        Assert.Equal([Ring(1), SceneWorkspace.Object("Li01_near")], lane.Rings);
        Assert.Equal(LaneWorkspace.Zone, LanePlans.ZoneOf(session, lane));
        Assert.True(LaneZones.IsLaneZone(session, LaneWorkspace.Zone));
    }

    /// <summary>Of two lane zones that cover the lane, the zone nearer to the axis of the lane is its zone, also when the
    /// farther zone comes first in the file.</summary>
    [Fact]
    public void TheNearerOfTwoZonesIsTheZoneOfTheLane()
    {
        const string far = """
            [zone]
            nickname = Zone_Li01_Tradelane_0
            pos = 11250, 0, -19000
            rotate = 0, -90, 0
            shape = BOX
            size = 500, 500, 23500
            pop_type = major_tradelane

            """;
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text.Replace("[zone]\nnickname = Zone_Li01_Tradelane_1",
            far + "[zone]\nnickname = Zone_Li01_Tradelane_1", StringComparison.Ordinal));
        var session = ws.Session("act");
        var farZone = SceneWorkspace.Zone("Zone_Li01_Tradelane_0");
        Assert.True(session.Sections(SceneWorkspace.Li01Path).ToList().IndexOf(farZone) <
                    session.Sections(SceneWorkspace.Li01Path).ToList().IndexOf(LaneWorkspace.Zone));

        Assert.Equal(LaneWorkspace.Zone, LanePlans.ZoneOf(session, LanePlans.LaneOf(session, Ring(1))!));
    }

    /// <summary>A lane whose links go in a circle has no first ring, so before refuses also on the ring at the start of
    /// the walk.</summary>
    [Fact]
    public void BeforeRefusesOnALaneInACircle()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(1), "prev_ring", ["Li01_sun"]));
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_1"]));
        var lane = LanePlans.LaneOf(session, Ring(1))!;
        Assert.True(lane.Loops);
        Assert.Equal(Ring(1), lane.Rings[0]);

        Refusal(LanePlans.InsertRing(session, Ring(1), LanePlace.Before), LanePlans.BeforeNotFirst);
    }

    /// <summary>A delete of an end ring moves its end name only to a new end ring without a name. A neighbor with a name
    /// keeps its name.</summary>
    [Fact]
    public void ANewEndRingWithANameKeepsItsName()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(3), "tradelane_space_name", ["506"]));

        var plan = Run(session, ScenePlans.Delete(session, [Ring(4)]));

        Assert.Equal("506", Line(session, Ring(3), "tradelane_space_name"));
        Assert.DoesNotContain(plan.Fixes, f => f.Key == "tradelane_space_name");
    }

    /// <summary>
    /// A repair whose links go forward as often as back has no direction from the links. The ring without a prev_ring
    /// line then becomes the first ring, because the first ring of a lane has no prev_ring line.
    /// </summary>
    [Fact]
    public void ARepairOfLinksWithoutADirectionStartsAtTheRingWithoutAPreviousRing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(1), "next_ring"));
        session.Execute(EditCommand.SetLinesFromText(Ring(1), "prev_ring", ["Li01_Trade_Lane_Ring_2"]));
        session.Execute(EditCommand.RemoveName(Ring(2), "prev_ring"));
        session.Execute(EditCommand.RemoveName(Ring(3), "prev_ring"));
        session.Execute(EditCommand.RemoveName(Ring(3), "next_ring"));
        session.Execute(EditCommand.RemoveName(Ring(4), "prev_ring"));

        var plan = Run(session, LanePlans.Repair(session, Ring(1)));

        Assert.Equal([Ring(4), Ring(3), Ring(2), Ring(1)], plan.Rings);
        Assert.Equal([Ring(4), Ring(3), Ring(2), Ring(1)], LanePlans.LaneOf(session, Ring(2))!.Rings);
        Assert.Equal(("505", "504"), (Line(session, Ring(4), "tradelane_space_name"), Line(session, Ring(1), "tradelane_space_name")));
    }

    /// <summary>
    /// A ring without a position takes a new ring when the call gives the position. The rotation of the new ring then
    /// looks toward its next ring. Without a position, the plan refuses with a fix that works.
    /// </summary>
    [Fact]
    public void ARingWithoutAPositionTakesANewRingAtAGivenPosition()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(2), "pos"));

        var unplaced = LanePlans.InsertRing(session, Ring(2), LanePlace.After);
        var refused = Refusal(unplaced, MissionErrors.FieldValue);
        var plan = Run(session, LanePlans.InsertRing(session, Ring(2), LanePlace.After, new Vector3(11250, 0, -20000)));

        Assert.Equal("Li01_Trade_Lane_Ring_2 has no position. Give it a pos line, then insert the ring. An agent can also " +
                     "give the position of the new ring.", refused.Message);
        Assert.Equal("position", unplaced.RefusedPart);
        Assert.Equal([Ring(1), Ring(2), plan.Ring!, Ring(3), Ring(4)], plan.Rings);
        Assert.Equal(("11250, 0, -20000", "0, -90, 0"), (Line(session, plan.Ring!, "pos"), Line(session, plan.Ring!, "rotate")));
        Assert.Equal(plan.Ring!.Id.Key, Line(session, Ring(2), "next_ring"));
    }

    /// <summary>Two vectors that differ by at most a tolerance on each axis.</summary>
    private sealed class VectorNear(float tolerance) : System.Collections.Generic.IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) =>
            MathF.Abs(a.X - b.X) <= tolerance && MathF.Abs(a.Y - b.Y) <= tolerance && MathF.Abs(a.Z - b.Z) <= tolerance;

        public int GetHashCode(Vector3 v) => 0;
    }
}
