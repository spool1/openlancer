using System;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The trade lane tools of the agent server: <c>trade_lane_create</c>, <c>trade_lane_insert_ring</c>,
/// <c>trade_lane_repair</c>, <c>trade_lane_delete</c> and <c>get_trade_lane</c>, and the generic tools on a ring. Each
/// edit is one entry with the edit origin Agent. The install is <see cref="LaneWorkspace"/>: Li01 has a sun with a
/// death zone, a jump gate, two planets and a lane of four rings with its lane zone. The data is invented.
/// </summary>
public sealed class AgentLaneToolTests
{
    /// <summary>Li01 with a broken link: ring 2 has no next_ring line, and ring 3 still names ring 2.</summary>
    private static readonly string BrokenLink =
        LaneWorkspace.Li01Text.Replace("next_ring = Li01_Trade_Lane_Ring_3\n", "", StringComparison.Ordinal);

    private static (TestWorkspace Ws, EditSession Session, TestAgentHost Host) Lanes(string? li01 = null)
    {
        var ws = LaneWorkspace.Create(li01);
        var session = ws.Session("act");
        return (ws, session, new TestAgentHost(session));
    }

    private static SectionRef Ring(int n) => LaneWorkspace.Ring(n);

    /// <summary>The structured content of a call that must give a tool error.</summary>
    private static JsonObject Failure(AgentHost host, string tool, string arguments)
    {
        var result = Call(host, tool, arguments);
        Assert.True(result.IsError, result.Text);
        return result.Content;
    }

    /// <summary>The references of the deletes of a result.</summary>
    private static string[] Deletes(JsonNode result) =>
        result["deletes"]!.AsArray().Select(d => (string)d!["ref"]!).ToArray();

    private static string[] Messages(JsonNode? problems) =>
        problems!.AsArray().Select(p => (string)p!["message"]!).ToArray();

    /// <summary>
    /// trade_lane_create makes the rings and the lane zone as one entry. A lane of 30000 m gets five rings 7500 m
    /// apart. The result gives the rings in order, the zone and the spacing. The preview gives the same result with the
    /// patch, and changes nothing. Undo takes everything back.
    /// </summary>
    [Fact]
    public void TradeLaneCreateMakesTheRingsAndTheZoneInOneEntry()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;
        const string arguments = """
            "system": "Li01", "start": [30000, 0, 30000], "start_name": "Avalon Gate", "end": [30000, 0, 60000],
            "end_name": "Far Point"
            """;

        var preview = Ok(host, "trade_lane_create", $$"""{ {{arguments}}, "preview": true, "with_patch": true }""");
        Assert.Equal(0, session.Revision);
        Assert.Null(session.Get(Ring(5)));
        var result = Ok(host, "trade_lane_create", $$"""{ {{arguments}} }""");

        string[] rings = [.. Enumerable.Range(5, 5).Select(n => $"obj:Li01_Trade_Lane_Ring_{n}")];
        foreach (var made in new[] { preview, result })
        {
            Assert.Equal(rings, Texts(made["rings"]));
            Assert.Equal("zone:Zone_Li01_Tradelane_2", (string?)made["zone"]);
            Assert.Equal(7500, (double)made["spacing"]!);
            Assert.Equal([.. rings, "zone:Zone_Li01_Tradelane_2"], Texts(made["adds"]));
            Assert.Null(made["ring"]);
        }
        Assert.Contains(preview["patch"]!.AsObject(), p => p.Key.Equals(ListWorkspace.Li01Path, StringComparison.OrdinalIgnoreCase) &&
                                                         p.Value!.ToString().Contains("Li01_Trade_Lane_Ring_9", StringComparison.Ordinal));
        Assert.Equal(("previewed", "applied"), ((string?)preview["status"], (string?)result["status"]));
        Assert.Equal(("Agent: Create trade lane in Li01", "obj:Li01_Trade_Lane_Ring_5"),
            ((string?)result["undo_label"], (string?)result["select"]));
        Assert.Equal("Li01_Trade_Lane_Ring_6", Line(session, Ring(5), "next_ring"));
        Assert.Equal("Avalon Gate", ConnectionWorkspace.TextOf(session, Line(session, Ring(5), "tradelane_space_name")!));
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);

        Ok(host, "undo");
        Assert.Null(session.Get(Ring(5)));
        Assert.Null(session.Get(SceneWorkspace.Zone("Zone_Li01_Tradelane_2")));
    }

    /// <summary>
    /// An end at an object goes on the line toward the other end, at the solar_radius of the object plus the clearance.
    /// The end at the sun also clears its death zone of 12000 m, so no ring stands inside it, and the result has no
    /// placement warning. Each end takes the name of its object, and the rings take the faction, the loadout and the
    /// pilot of a like ring.
    /// </summary>
    [Fact]
    public void AnEndAtAnObjectClearsTheObjectAndTheDeathZoneOfTheSun()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;

        var result = Ok(host, "trade_lane_create", """
            { "system": "system:Li01", "start_near": "obj:Li01_to_Li02", "end_near": "obj:Li01_sun",
              "like": "obj:Li01_Trade_Lane_Ring_1", "zone": false }
            """);

        var rings = Texts(result["rings"]);
        Assert.Equal(7, rings.Length);
        Assert.Null(result["zone"]);
        var first = Ring(5);
        var last = Ring(11);
        Assert.Equal("57600, 0, 0", Line(session, first, "pos"));
        Assert.Equal("14000, 0, 0", Line(session, last, "pos"));
        Assert.Equal(("502", "501"),
            (Line(session, first, "tradelane_space_name"), Line(session, last, "tradelane_space_name")));
        Assert.Equal("trade_lane_ring_li_01", Line(session, first, "loadout"));
        Assert.DoesNotContain(Messages(result["warnings"]), m => m.Contains("stands inside", StringComparison.Ordinal));
    }

    /// <summary>Each refusal of the lane tools gives its error kind, its detail and its argument, and changes
    /// nothing.</summary>
    [Theory]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "end": [30000, 0, 30000], "start_name": "a", "end_name": "b", "rings": 101 }""",
        "invalid_value", LanePlans.LaneRings, "rings")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "end": [30000, 0, 30000], "start_name": "a", "end_name": "b", "rings": 4294967298 }""",
        "invalid_value", LanePlans.LaneRings, "rings")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "end": [30000, 0, 30000], "start_name": "a", "end_name": "b", "rings": 1 }""",
        "invalid_argument", ToolInput.BadArgument, "rings")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "start_name": "a", "end_name": "b" }""",
        "invalid_argument", LanePlans.LaneEnd, "end")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "end_near": "obj:Li01_planet" }""",
        "invalid_argument", LanePlans.LaneEndName, "start_name")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "start_name": "a", "end_near": "obj:Li01_rock" }""",
        "invalid_argument", LanePlans.LaneEndName, "end_name")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "end_near": "obj:Li01_planet", "start_name": "a", "like": "obj:Li01_planet" }""",
        "invalid_value", LanePlans.LaneArchetype, "like")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "end_near": "obj:Li02_01", "start_name": "a", "end_name": "b" }""",
        "invalid_argument", UniverseTools.EndOtherSystem, "end_near")]
    [InlineData("trade_lane_create", """{ "system": "Li01", "start": [0, 0, 30000], "end_near": "obj:Li01_planet", "start_name": "a", "faction": "ol_none" }""",
        "invalid_value", MissionErrors.FieldValue, "faction")]
    [InlineData("trade_lane_create", """{ "system": "Li99", "start": [0, 0, 30000], "end_near": "obj:Li01_planet", "start_name": "a" }""",
        "not_found", ItemRefs.NotFound, "system")]
    [InlineData("trade_lane_insert_ring", """{ "after": "obj:Li01_Trade_Lane_Ring_1", "before": "obj:Li01_Trade_Lane_Ring_1" }""",
        "invalid_argument", ToolInput.BadArgument, "before")]
    [InlineData("trade_lane_insert_ring", """{ "position": [0, 0, 0] }""", "invalid_argument", ToolInput.BadArgument, "after")]
    [InlineData("trade_lane_insert_ring", """{ "after": "obj:Li01_planet" }""", "invalid_value", LanePlans.NotLaneRing, "after")]
    [InlineData("trade_lane_insert_ring", """{ "before": "obj:Li01_Trade_Lane_Ring_2" }""", "invalid_value",
        LanePlans.BeforeNotFirst, "before")]
    [InlineData("trade_lane_insert_ring", """{ "after": "obj:Li01_Trade_Lane_Ring_2", "nickname": "Li01_sun" }""", "name_taken",
        MissionErrors.NameTaken, "nickname")]
    [InlineData("trade_lane_repair", """{ "ring": "obj:Li01_planet" }""", "invalid_value", LanePlans.NotLaneRing, "ring")]
    [InlineData("trade_lane_delete", """{ "ring": "obj:Li01_planet" }""", "invalid_value", LanePlans.NotLaneRing, "ring")]
    [InlineData("get_trade_lane", """{ "ring": "obj:Li01_planet" }""", "invalid_value", LanePlans.NotLaneRing, "ring")]
    [InlineData("get_trade_lane", """{ "ring": "zone:Zone_Li01_Tradelane_1" }""", "invalid_value", LanePlans.NotLaneRing, "ring")]
    public void EachRefusalOfTheLaneToolsNamesItsArgument(string tool, string arguments, string code, string detail,
        string field)
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;

        var error = Failure(host, tool, arguments);

        Assert.Equal((code, detail, field), ((string?)error["code"], (string?)error["detail"], (string?)error["field"]));
        Assert.Equal(0, session.Revision);
        Assert.False(session.CanUndo);
    }

    /// <summary>A repair of rings that do not stand on one line refuses with the argument ring. The texts of the
    /// refusals of the plans are the texts of the agent.</summary>
    [Fact]
    public void ARepairOfRingsOffTheLineRefuses()
    {
        var (ws, session, host) = Lanes(LaneWorkspace.Li01Text.Replace("pos = 15000, 0, -20000", "pos = 15000, 0, -14000",
            StringComparison.Ordinal));
        using var _ = ws;

        var error = Failure(host, "trade_lane_repair", """{ "ring": "obj:Li01_Trade_Lane_Ring_1" }""");
        var name = Failure(host, "trade_lane_create",
            """{ "system": "Li01", "start": [0, 0, 30000], "end": [30000, 0, 30000], "end_name": "b" }""");

        Assert.Equal(("invalid_value", LanePlans.LaneNotLine, "ring"),
            ((string?)error["code"], (string?)error["detail"], (string?)error["field"]));
        Assert.StartsWith("The rings of the lane of Li01_Trade_Lane_Ring_1 do not stand on one line.", (string?)error["message"]);
        Assert.Equal("Give start_name or start_near, and end_name or end_near. The HUD shows the two end names of a lane.",
            (string?)name["message"]);
        Assert.Equal(0, session.Revision);
    }

    /// <summary>
    /// trade_lane_insert_ring adds a ring in the middle of a lane, after its last ring and before its first ring. The new
    /// ring takes the links of its place. At an end, the end name moves to the new ring. The result gives the new ring,
    /// its place from 1 and the ring count. Each insert is one entry.
    /// </summary>
    [Fact]
    public void TradeLaneInsertRingAddsARingInTheMiddleAndAtEachEnd()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;

        var middle = Ok(host, "trade_lane_insert_ring", """{ "after": "obj:Li01_Trade_Lane_Ring_2" }""");
        var mid = SceneWorkspace.Object(((string)middle["ring"]!)["obj:".Length..]);
        Assert.Equal((3, 5), ((int)middle["place"]!, (int)middle["count"]!));
        Assert.Equal("11250, 0, -20000", Line(session, mid, "pos"));
        Assert.Equal(mid.Id.Key, Line(session, Ring(2), "next_ring"));
        Assert.Equal(mid.Id.Key, Line(session, Ring(3), "prev_ring"));
        Assert.Null(middle["rings"]);

        var end = Ok(host, "trade_lane_insert_ring", """{ "after": "obj:Li01_Trade_Lane_Ring_4", "position": [30000, 0, -20000] }""");
        var last = SceneWorkspace.Object(((string)end["ring"]!)["obj:".Length..]);
        Assert.Equal((6, 6), ((int)end["place"]!, (int)end["count"]!));
        Assert.Equal("505", Line(session, last, "tradelane_space_name"));
        Assert.Null(Line(session, Ring(4), "tradelane_space_name"));

        var start = Ok(host, "trade_lane_insert_ring", """{ "before": "obj:Li01_Trade_Lane_Ring_1", "nickname": "Li01_Lane_Start" }""");
        Assert.Equal(("obj:Li01_Lane_Start", 1, 7), ((string?)start["ring"], (int)start["place"]!, (int)start["count"]!));
        var first = SceneWorkspace.Object("Li01_Lane_Start");
        Assert.Equal("-7500, 0, -20000", Line(session, first, "pos"));
        Assert.Equal("Li01_Trade_Lane_Ring_1", Line(session, first, "next_ring"));
        Assert.Equal("504", Line(session, first, "tradelane_space_name"));
        Assert.Equal("Agent: Insert ring before Li01_Trade_Lane_Ring_1", session.UndoLabel);

        Ok(host, "undo");
        Assert.Null(session.Get(first));
        Assert.Equal("504", Line(session, Ring(1), "tradelane_space_name"));
    }

    /// <summary>trade_lane_repair writes a missing link again and gives the rings in order. A second repair finds good
    /// links and gives no_change. A call with the revision before the repair gives stale_revision.</summary>
    [Fact]
    public void TradeLaneRepairWritesTheLinksAgainAndASecondRepairChangesNothing()
    {
        var (ws, session, host) = Lanes(BrokenLink);
        using var _ = ws;
        var before = (string)Ok(host, "get_trade_lane", """{ "ring": "obj:Li01_Trade_Lane_Ring_1" }""")["rev"]!;

        var result = Ok(host, "trade_lane_repair", """{ "ring": "obj:Li01_Trade_Lane_Ring_1" }""");
        var again = Ok(host, "trade_lane_repair", """{ "ring": "obj:Li01_Trade_Lane_Ring_3" }""");
        var stale = Failure(host, "trade_lane_repair", $$"""{ "ring": "obj:Li01_Trade_Lane_Ring_3", "if_revision": "{{before}}" }""");

        Assert.Equal("stale_revision", (string?)stale["code"]);

        string[] rings = [.. Enumerable.Range(1, 4).Select(n => $"obj:Li01_Trade_Lane_Ring_{n}")];
        Assert.Equal(rings, Texts(result["rings"]));
        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal("Li01_Trade_Lane_Ring_3", Line(session, Ring(2), "next_ring"));
        Assert.Equal("no_change", (string?)again["status"]);
        Assert.Equal(rings, Texts(again["rings"]));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>
    /// trade_lane_delete always asks. The question names the lane by its first ring, the ring count and the lane zone
    /// that goes with the rings. With confirm: true, the rings and the lane zone go as one entry, and undo gives them
    /// back.
    /// </summary>
    [Fact]
    public void TradeLaneDeleteAsksAndTakesTheRingsAndTheZone()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;

        var asked = Failure(host, "trade_lane_delete", """{ "ring": "obj:Li01_Trade_Lane_Ring_3" }""");
        Assert.Equal("needs_confirmation", (string?)asked["code"]);
        Assert.StartsWith("This deletes the whole trade lane of Li01_Trade_Lane_Ring_1 with its 4 rings. It also removes " +
                          "zone:Zone_Li01_Tradelane_1.", (string?)asked["message"]);
        Assert.Equal(0, session.Revision);

        var result = Ok(host, "trade_lane_delete", """{ "ring": "obj:Li01_Trade_Lane_Ring_3", "confirm": true }""");

        Assert.Equal([.. Enumerable.Range(1, 4).Select(n => $"obj:Li01_Trade_Lane_Ring_{n}"), "zone:Zone_Li01_Tradelane_1"],
            Deletes(result).Order(StringComparer.Ordinal));
        Assert.Null(session.Get(Ring(2)));
        Assert.Null(session.Get(LaneWorkspace.Zone));
        Ok(host, "undo");
        Assert.NotNull(session.Get(Ring(2)));
        Assert.NotNull(session.Get(LaneWorkspace.Zone));
    }

    /// <summary>
    /// The question of trade_lane_delete on a lane without a lane zone names the lane and its ring count, and tells
    /// that no lane zone goes. The question of the generic delete would name nothing, because the call takes only the
    /// rings.
    /// </summary>
    [Fact]
    public void TradeLaneDeleteOfALaneWithoutAZoneTellsTheRings()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;
        var made = Ok(host, "trade_lane_create", """
            { "system": "Li01", "start": [0, 0, 30000], "start_name": "a", "end": [15000, 0, 30000], "end_name": "b",
              "rings": 3, "zone": false }
            """);
        var rings = Texts(made["rings"]);

        var asked = Failure(host, "trade_lane_delete", $$"""{ "ring": "{{rings[1]}}" }""");

        Assert.Equal("needs_confirmation", (string?)asked["code"]);
        Assert.StartsWith($"This deletes the whole trade lane of {rings[0][4..]} with its 3 rings. No lane zone goes with it. " +
                          "Use preview: true", (string?)asked["message"]);
        Assert.Equal(1, session.Revision);
        var result = Ok(host, "trade_lane_delete", $$"""{ "ring": "{{rings[1]}}", "confirm": true }""");
        Assert.Equal(rings.Order(StringComparer.Ordinal), Deletes(result).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// get_trade_lane gives the lane of any ring: the rings in order with their positions and the spacing to the next
    /// ring, the end names with their keys, the lane zone and the length. A lane with good links has no problems. A
    /// lane with a broken link gives the problems of the lane checks.
    /// </summary>
    [Fact]
    public void GetTradeLaneGivesTheRingsTheNamesTheZoneAndTheProblems()
    {
        var (ws, _, host) = Lanes();
        using var __ = ws;
        var (brokenWs, _, brokenHost) = Lanes(BrokenLink);
        using var ___ = brokenWs;

        var lane = Ok(host, "get_trade_lane", """{ "ring": "obj:Li01_Trade_Lane_Ring_3" }""");
        var broken = Ok(brokenHost, "get_trade_lane", """{ "ring": "obj:Li01_Trade_Lane_Ring_1" }""");

        Assert.Equal("system:Li01", (string?)lane["system"]);
        var rows = lane["rings"]!.AsArray();
        Assert.Equal(Enumerable.Range(1, 4).Select(n => $"obj:Li01_Trade_Lane_Ring_{n}"), rows.Select(r => (string?)r!["ref"]));
        Assert.Equal([0d, 0, -20000], rows[0]!["position"]!.AsArray().Select(v => (double)v!));
        Assert.Equal([7500d, 7500, 7500], rows.Take(3).Select(r => (double)r!["spacing_to_next"]!));
        Assert.Null(rows[3]!["spacing_to_next"]);
        Assert.Equal(("504", "505"), ((string?)lane["start_name"]!["key"], (string?)lane["end_name"]!["key"]));
        Assert.Equal("zone:Zone_Li01_Tradelane_1", (string?)lane["zone"]);
        Assert.Equal(22500, (double)lane["length"]!);
        Assert.Empty(lane["problems"]!.AsArray());

        Assert.Equal(["obj:Li01_Trade_Lane_Ring_1", "obj:Li01_Trade_Lane_Ring_2"],
            broken["rings"]!.AsArray().Select(r => (string?)r!["ref"]));
        Assert.Null(broken["end_name"]);
        Assert.Equal("zone:Zone_Li01_Tradelane_1", (string?)broken["zone"]);
        Assert.Contains(broken["problems"]!.AsArray(), p => (string?)p!["ref"] == "obj:Li01_Trade_Lane_Ring_2");
    }

    /// <summary>
    /// After a move of the end ring with set_fields, the lane zone does not cover the lane. get_trade_lane then gives no
    /// zone, and its problems give the warning of the lane zone, as check does on the zone.
    /// </summary>
    [Fact]
    public void GetTradeLaneGivesTheWarningOfALaneZoneThatMissesTheLane()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;
        Ok(host, "set_fields", """{ "ref": "obj:Li01_Trade_Lane_Ring_4", "fields": { "pos": [40000, 0, -20000] } }""");

        var lane = Ok(host, "get_trade_lane", """{ "ring": "obj:Li01_Trade_Lane_Ring_2" }""");

        Assert.Null(lane["zone"]);
        var problem = Assert.Single(lane["problems"]!.AsArray(), p => (string?)p!["ref"] == "zone:Zone_Li01_Tradelane_1");
        Assert.Equal("warning", (string?)problem!["severity"]);
        Assert.StartsWith("The lane zone Zone_Li01_Tradelane_1 does not cover the trade lane of Li01_Trade_Lane_Ring_1.",
            (string?)problem["message"]);
        Assert.Contains(LaneChecks.Check(session, LaneWorkspace.Zone), d => d.Message == (string?)problem["message"]);
    }

    /// <summary>
    /// delete_items on a middle ring relinks the lane without a question: the neighbors link to each other. On a lane of
    /// two rings, the whole lane goes with its zone, so the delete asks first.
    /// </summary>
    [Fact]
    public void DeleteItemsOnARingRelinksTheLaneAndAsksWhenTheLaneGoes()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;

        var middle = Ok(host, "delete_items", """{ "refs": ["obj:Li01_Trade_Lane_Ring_2"] }""");
        Assert.Equal("applied", (string?)middle["status"]);
        Assert.Equal("Li01_Trade_Lane_Ring_3", Line(session, Ring(1), "next_ring"));
        Assert.Equal("Li01_Trade_Lane_Ring_1", Line(session, Ring(3), "prev_ring"));

        var made = Ok(host, "trade_lane_create", """
            { "system": "Li01", "start": [0, 0, 30000], "start_name": "a", "end": [5000, 0, 30000], "end_name": "b", "rings": 2 }
            """);
        var two = Texts(made["rings"]);
        var asked = Failure(host, "delete_items", $$"""{ "refs": ["{{two[0]}}"] }""");
        Assert.Equal("needs_confirmation", (string?)asked["code"]);
        var gone = Ok(host, "delete_items", $$"""{ "refs": ["{{two[0]}}"], "confirm": true }""");
        Assert.Contains(two[1], Deletes(gone));
        Assert.Contains((string)made["zone"]!, Deletes(gone));
    }

    /// <summary>rename_item on a ring keeps the rename of the Scene: the link lines of the neighbors name the new
    /// nickname. Undo gives the old links back.</summary>
    [Fact]
    public void RenameItemOnARingChangesTheLinksOfItsNeighbors()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;

        var result = Ok(host, "rename_item", """{ "ref": "obj:Li01_Trade_Lane_Ring_2", "new_name": "Li01_Lane_Mid", "confirm": true }""");

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal("Li01_Lane_Mid", Line(session, Ring(1), "next_ring"));
        Assert.Equal("Li01_Lane_Mid", Line(session, Ring(3), "prev_ring"));
        Ok(host, "undo");
        Assert.Equal("Li01_Trade_Lane_Ring_2", Line(session, Ring(1), "next_ring"));
    }

    /// <summary>
    /// The generic tools keep the links of a lane: set_fields on prev_ring and next_ring gives FieldNotWritable with
    /// the reason that names the lane tools, and duplicate_items, scene_create_object and scene_spawn_asset refuse a
    /// ring. list_values with the text TRADELANE_RING gives the ring archetypes.
    /// </summary>
    [Fact]
    public void TheGenericToolsKeepTheLinksOfALane()
    {
        var (ws, session, host) = Lanes();
        using var _ = ws;

        var link = Failure(host, "set_fields", """{ "ref": "obj:Li01_Trade_Lane_Ring_2", "fields": { "prev_ring": "Li01_Trade_Lane_Ring_4" } }""");
        var copy = Failure(host, "duplicate_items", """{ "refs": ["obj:Li01_Trade_Lane_Ring_2"] }""");
        var create = Failure(host, "scene_create_object",
            """{ "system": "Li01", "template": "satellite", "archetype": "Trade_Lane_Ring", "position": [0, 0, 0] }""");
        var spawn = Failure(host, "scene_spawn_asset",
            """{ "system": "Li01", "asset": "asset:solar_archetype/Trade_Lane_Ring", "position": [0, 0, 0] }""");
        var values = Ok(host, "list_values", """{ "list": "solar_archetype", "text": "TRADELANE_RING" }""");

        Assert.Equal(MissionErrors.FieldNotWritable, (string?)link["detail"]);
        Assert.Contains(FieldBinding.LaneLinkReason, (string?)link["message"]);
        Assert.Contains("trade_lane_create and trade_lane_insert_ring", (string?)copy["message"]);
        Assert.EndsWith(ScenePlans.NoNewConnections, (string?)create["message"]);
        Assert.EndsWith(ScenePlans.NoNewConnections, (string?)spawn["message"]);
        Assert.Equal(["Trade_Lane_Ring", "ring"], values["rows"]!.AsArray().Select(r => (string?)r!["value"]).Order(StringComparer.Ordinal));
        Assert.Equal(0, session.Revision);
    }

    /// <summary>
    /// edit_batch runs system_create, connection_create and trade_lane_create as one entry. The lane starts at the new
    /// gate of the batch and ends at the new sun, so a later step names the items of the earlier steps. The ends take the
    /// names of the new gate and the new sun. One undo takes the whole batch back, also the new file.
    /// </summary>
    [Fact]
    public void ABatchMakesASystemAGatePairAndALaneFromTheNewGateInOneEntry()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ListWorkspace.SolarPath, SceneWorkspace.Solars + ConnectionWorkspace.Solars + LaneWorkspace.Solars);
        var session = ws.Session("act");
        var host = new TestAgentHost(session);

        var result = Ok(host, "edit_batch", """
            { "label": "New system Av01", "steps": [
                { "tool": "system_create", "arguments": { "nickname": "Av01", "name": "Avalon", "like": "Li02", "map_pos": [4, 5] } },
                { "tool": "connection_create", "arguments": { "kind": "jump_gate", "from": { "system": "Av01" }, "to": { "system": "Li02" } } },
                { "tool": "trade_lane_create", "arguments": { "system": "Av01", "start_near": "obj:Av01_to_Li02", "end_near": "obj:Av01_sun" } } ] }
            """);

        var adds = Texts(result["steps"]!.AsArray()[2]!["adds"]);
        Assert.Contains("obj:Av01_Trade_Lane_Ring_1", adds);
        Assert.Contains("zone:Zone_Av01_Tradelane_1", adds);
        var first = NewSystemWorkspace.New("Object", "Av01_Trade_Lane_Ring_1");
        var gate = NewSystemWorkspace.New("Object", "Av01_to_Li02");
        Assert.Equal(Line(session, gate, "ids_name"), Line(session, first, "tradelane_space_name"));
        Assert.Equal("Agent: New system Av01", session.UndoLabel);

        Ok(host, "undo");
        Assert.Null(session.Get(NewSystemWorkspace.System("Av01")));
        Assert.False(session.OpenDocument(NewSystemWorkspace.NewPath)!.State.Exists);
        Assert.False(session.CanUndo);
    }
}
