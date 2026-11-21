using System;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The universe tools of the agent server: <c>system_create</c>, <c>connection_create</c>, <c>connection_set_lock</c>,
/// <c>get_universe</c> and <c>get_route</c>, and the parts of <c>get_system</c> and <c>list_items</c> for the
/// universe. Each edit is one entry with the edit origin Agent. The install is <see cref="ConnectionWorkspace"/>: Li01
/// and Li02 have a jump gate pair with locks, and Li03 has a jump gate and a jump hole without a goto line. The data is
/// invented.
/// </summary>
public sealed class AgentUniverseToolTests
{
    /// <summary>The arguments of Av01 like Li02 at 4, 5 with the name Avalon.</summary>
    private const string Av01 =
        """ "nickname": "Av01", "name": "Avalon", "like": "Li02", "map_pos": [4, 5], "infocard": "A quiet border system." """;

    private static readonly SectionRef Av01System = NewSystemWorkspace.System("Av01");

    private static (TestWorkspace Ws, EditSession Session, TestAgentHost Host) Universe(
        AgentHostKind kind = AgentHostKind.Headless, Action<TestWorkspace>? setup = null)
    {
        var ws = ConnectionWorkspace.Create();
        setup?.Invoke(ws);
        var session = ws.Session("act");
        return (ws, session, new TestAgentHost(session, kind));
    }

    /// <summary>The structured content of a call that must give a tool error.</summary>
    private static JsonObject Failure(AgentHost host, string tool, string arguments)
    {
        var result = Call(host, tool, arguments);
        Assert.True(result.IsError, result.Text);
        return result.Content;
    }

    private static string[] Messages(JsonNode? problems) =>
        problems!.AsArray().Select(p => (string)p!["message"]!).ToArray();

    /// <summary>
    /// system_create makes the [system], the file, the sun and its death zone as one entry. The result gives the new
    /// system, its file and its sun, and the warning that no connection leads to it. The preview gives the same result
    /// and changes nothing. The host without a window gives no warning about the Scene. Undo takes everything back.
    /// </summary>
    [Fact]
    public void SystemCreateMakesTheSystemItsFileAndItsSunInOneEntry()
    {
        var (ws, session, host) = Universe();
        using var _ = ws;

        var preview = Ok(host, "system_create", $$"""{ {{Av01}}, "preview": true }""");
        Assert.Equal(0, session.Revision);
        Assert.Null(session.Get(Av01System));
        var result = Ok(host, "system_create", $$"""{ {{Av01}} }""");

        foreach (var made in new[] { preview, result })
        {
            Assert.Equal(("system:Av01", "obj:Av01_sun"), ((string?)made["system"], (string?)made["sun"]));
            Assert.Equal(NewSystemWorkspace.NewPath, (string?)made["file"], ignoreCase: true);
            Assert.Equal(["system:Av01", "obj:Av01_sun", "zone:zone_Av01_sun_death"], Texts(made["adds"]));
            Assert.Contains("No jump gate or jump hole leads to Av01 yet.", Messages(made["warnings"]));
            Assert.DoesNotContain(AgentTexts.Error("system_save_to_open"), Messages(made["warnings"]));
        }
        Assert.Equal(("previewed", "applied"), ((string?)preview["status"], (string?)result["status"]));
        Assert.Equal(("Agent: Create system Av01", "system:Av01"), ((string?)result["undo_label"], (string?)result["select"]));
        Assert.NotNull(session.Get(NewSystemWorkspace.New("Object", "Av01_sun")));
        Assert.Equal(["4, 5"], EditSessionTests.Values(session, Av01System, "pos"));
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);

        Ok(host, "undo");
        Assert.Null(session.Get(Av01System));
        Assert.False(session.OpenDocument(NewSystemWorkspace.NewPath)!.State.Exists);
    }

    /// <summary>The options of system_create reach the plan: the zoom, the flag of the nav map, the parts to copy, the
    /// faction as its list spells it, and the name of the sun.</summary>
    [Fact]
    public void SystemCreateTakesTheOptionsOfTheNewSystem()
    {
        var (ws, session, host) = Universe();
        using var _ = ws;

        Ok(host, "system_create", """
            { "nickname": "Av01", "name": "Avalon", "like": "system:Li02", "map_pos": [4, 5], "nav_map_scale": 1.5,
              "on_map_at_start": true, "copy": ["lights"], "local_faction": "LI_P_GRP", "sun": { "name": "Avalon Star" } }
            """);

        Assert.Equal(["1"], EditSessionTests.Values(session, Av01System, "visit"));
        Assert.Equal(["1.5"], EditSessionTests.Values(session, Av01System, "NavMapScale"));
        var types = session.Sections(NewSystemWorkspace.NewPath).Select(s => s.Id.Type).ToList();
        Assert.Contains("LightSource", types);
        Assert.DoesNotContain("Music", types);
        var info = session.Sections(NewSystemWorkspace.NewPath).Single(s => s.Id.Type == "SystemInfo");
        Assert.Equal("li_p_grp", Line(session, info, "local_faction"));
        var sun = NewSystemWorkspace.New("Object", "Av01_sun");
        Assert.Equal("Avalon Star", ConnectionWorkspace.TextOf(session, Line(session, sun, "ids_name")!));
    }

    /// <summary>In the editor host, the result of system_create tells that the Scene opens the new system after the
    /// save.</summary>
    [Fact]
    public void TheEditorHostTellsThatTheSceneOpensTheNewSystemAfterTheSave()
    {
        var (ws, _, host) = Universe(AgentHostKind.Editor);
        using var __ = ws;

        var result = Ok(host, "system_create", $$"""{ {{Av01}} }""");

        var warning = Assert.Single(result["warnings"]!.AsArray(),
            w => (string?)w!["message"] == "Save the mod to open the new system in the Scene.")!;
        Assert.Equal("system:Av01", (string?)warning["ref"]);
    }

    /// <summary>Each refusal of system_create gives its error kind, its detail and its argument, and changes
    /// nothing.</summary>
    [Theory]
    [InlineData("""{ "nickname": "Li01", "name": "x", "like": "Li02", "map_pos": [4, 5] }""", "name_taken",
        MissionErrors.NameTaken, "nickname")]
    [InlineData("""{ "nickname": "Av02", "name": "x", "like": "Li02", "map_pos": [4, 5] }""", "invalid_value",
        MissionErrors.PathInUse, "nickname")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li02", "map_pos": [20, 5] }""", "invalid_value",
        SystemPlans.MapPosRange, "map_pos")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li02", "map_pos": [1.5, 5] }""", "invalid_argument",
        ToolInput.BadArgument, "map_pos")]
    [InlineData("""{ "nickname": "1x", "name": "x", "like": "Li02", "map_pos": [4, 5] }""", "invalid_argument",
        ToolInput.BadArgument, "nickname")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li09", "map_pos": [4, 5] }""", "invalid_value",
        SystemPlans.LikeNoFile, "like")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li01", "map_pos": [4, 5] }""", "invalid_value",
        SystemPlans.LikeNoSun, "sun")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li02", "map_pos": [4, 5], "sun": { "archetype": "station1" } }""",
        "invalid_value", SystemPlans.SunType, "sun.archetype")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li02", "map_pos": [4, 5], "sun": { "archetype": "ol_none" } }""",
        "invalid_value", MissionErrors.FieldValue, "sun.archetype")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li02", "map_pos": [4, 5], "local_faction": "ol_none" }""",
        "invalid_value", MissionErrors.FieldValue, "local_faction")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li02", "map_pos": [4, 5], "nav_map_scale": 0 }""",
        "invalid_value", MissionErrors.FieldValue, "nav_map_scale")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "obj:Li01_01", "map_pos": [4, 5] }""", "invalid_argument",
        ToolInput.BadArgument, "like")]
    [InlineData("""{ "nickname": "Av01", "name": "x", "like": "Li99", "map_pos": [4, 5] }""", "not_found",
        ItemRefs.NotFound, "like")]
    public void EachRefusalOfSystemCreateNamesItsArgument(string arguments, string code, string detail, string field)
    {
        var (ws, session, host) = Universe(setup: w =>
        {
            w.WriteBase(ContentPaths.NewSystemFile("act", "Av02"), "[SystemInfo]\n");
            w.WriteBase(ListWorkspace.UniversePath, """
                [system]
                nickname = Li01
                file = systems\Li01\li01.ini
                pos = 7, 8
                [system]
                nickname = Li02
                file = systems\Li02\li02.ini
                pos = 6, 9
                [system]
                nickname = Li09
                pos = 1, 1
                """);
        });
        using var _ = ws;

        var error = Failure(host, "system_create", arguments);

        Assert.Equal((code, detail, field), ((string?)error["code"], (string?)error["detail"], (string?)error["field"]));
        Assert.Equal(0, session.Revision);
        Assert.False(session.CanUndo);
    }

    /// <summary>The texts of the refusals of the plan are the texts of the agent: a taken nickname names the system
    /// that has it.</summary>
    [Fact]
    public void ATakenNicknameNamesTheSystemThatHasIt()
    {
        var (ws, _, host) = Universe();
        using var __ = ws;

        var error = Failure(host, "system_create", """{ "nickname": "LI01", "name": "x", "like": "Li02", "map_pos": [4, 5] }""");

        Assert.Equal("The system nickname LI01 is taken: Li01 has the same nickname or the same FLHash.",
            (string?)error["message"]);
        Assert.Equal("system:Li01", (string?)error["target"]);
    }

    /// <summary>
    /// connection_create with an existing end writes only its goto line. The new end in the other system gets the
    /// loadout and the pilot that the gates of its faction have most often, and a traffic zone. The result gives the
    /// pair, the traffic zone, the chosen values for each end, the change of the route table and the jump objects. Li03
    /// then reaches Li02 and Li01, and Li02 reaches Li03: three new routes.
    /// </summary>
    [Fact]
    public void ConnectionCreatePairsAnExistingEndWithANewGate()
    {
        var (ws, session, host) = Universe();
        using var _ = ws;

        var result = Ok(host, "connection_create", """
            { "kind": "jump_gate", "from": { "system": "Li03", "object": "obj:Li03_gate" }, "to": { "system": "system:Li02" } }
            """);

        Assert.Equal(["obj:Li03_gate", "obj:Li02_to_Li03"], Texts(result["pair"]));
        Assert.Equal(["zone:Zone_Li02_to_Li03_traffic"], Texts(result["traffic_zones"]));
        Assert.Equal((null, "jumpgate_li_test"), ((string?)result["loadout"]!["from"], (string?)result["loadout"]!["to"]));
        Assert.Equal((null, "pilot_test"), ((string?)result["pilot"]!["from"], (string?)result["pilot"]!["to"]));
        Assert.Equal((3, 0, 0, 0), ((int)result["routes"]!["new"]!, (int)result["routes"]!["shorter"]!,
            (int)result["routes"]!["longer"]!, (int)result["routes"]!["lost"]!));
        Assert.Equal((1, 10, 2, 10), ((int)result["jump_objects"]!["from"]!["used"]!, (int)result["jump_objects"]!["from"]!["max"]!,
            (int)result["jump_objects"]!["to"]!["used"]!, (int)result["jump_objects"]!["to"]!["max"]!));
        Assert.StartsWith("Li02, Li02_to_Li03, ", Line(session, ConnectionWorkspace.Li03("Object", "Li03_gate"), "goto"));
        Assert.Equal(["obj:Li02_to_Li03", "zone:Zone_Li02_to_Li03_traffic"], Texts(result["adds"]));
        Assert.Contains("obj:Li03_gate", Texts(result["changes"]));
        Assert.Equal("Agent: Create jump gate pair Li03 to Li02", (string?)result["undo_label"]);
    }

    /// <summary>A preview of connection_create gives the pair and the patch of each system file that the edit changes,
    /// and changes nothing. A hole pair inside one system gives two ends in one file.</summary>
    [Fact]
    public void APreviewOfAHolePairGivesThePatchOfEachFile()
    {
        var (ws, session, host) = Universe();
        using var _ = ws;

        var cross = Ok(host, "connection_create", """
            { "kind": "jump_hole", "from": { "system": "Li01" }, "to": { "system": "Li03" }, "preview": true, "with_patch": true }
            """);
        var inside = Ok(host, "connection_create",
            """{ "kind": "jump_hole", "from": { "system": "Li03" }, "to": { "system": "Li03" }, "preview": true }""");

        Assert.Equal(["obj:Li01_to_Li03_hole", "obj:Li03_to_Li01_hole"], Texts(cross["pair"]));
        var files = cross["patch"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains(files, f => f.Equals(ListWorkspace.Li01Path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.Equals(ConnectionWorkspace.Li03Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["obj:Li03_to_Li03_hole", "obj:Li03_to_Li03_hole_1"], Texts(inside["pair"]));
        Assert.Equal((2, 2), ((int)inside["jump_objects"]!["from"]!["used"]!, (int)inside["jump_objects"]!["to"]!["used"]!));
        Assert.Equal(0, session.Revision);
    }

    /// <summary>Each refusal of connection_create gives its error kind, its detail and its argument, and changes
    /// nothing.</summary>
    [Theory]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li03" }, "to": { "system": "Li03" } }""", "invalid_value",
        ConnectionPlans.GateSameSystem, "to.system")]
    [InlineData("""{ "kind": "jump_hole", "from": { "system": "Li03" }, "to": { "system": "Li02" }, "archetype": "jumpgate" }""",
        "invalid_value", ConnectionPlans.KindMismatch, "archetype")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li02", "object": "Li02_to_Li01" }, "to": { "system": "Li03" } }""",
        "invalid_value", ConnectionPlans.EndPaired, "from.object")]
    [InlineData("""{ "kind": "jump_hole", "from": { "system": "Li03" }, "to": { "system": "Li03", "object": "Li03_gate" } }""",
        "invalid_value", ConnectionPlans.KindMismatch, "to.object")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li03", "nickname": "Li01_01" }, "to": { "system": "Li02" } }""",
        "name_taken", MissionErrors.NameTaken, "from.nickname")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li03", "object": "ol_none" }, "to": { "system": "Li02" } }""",
        "not_found", MissionErrors.ItemNotFound, "from.object")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li03", "object": "obj:Li02_01" }, "to": { "system": "Li02" } }""",
        "invalid_argument", UniverseTools.EndOtherSystem, "from.object")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li03", "object": "Li03_gate", "position": [0, 0, 0] }, "to": { "system": "Li02" } }""",
        "invalid_argument", ToolInput.BadArgument, "from.position")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li03" }, "to": { "system": "Li02" }, "faction": "ol_none" }""",
        "invalid_value", MissionErrors.FieldValue, "faction")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li03" } }""", "invalid_argument", ToolInput.BadArgument, "to")]
    [InlineData("""{ "kind": "jump_gate", "from": { "system": "Li99" }, "to": { "system": "Li02" } }""", "not_found",
        ItemRefs.NotFound, "from.system")]
    public void EachRefusalOfConnectionCreateNamesItsArgument(string arguments, string code, string detail, string field)
    {
        var (ws, session, host) = Universe();
        using var _ = ws;

        var error = Failure(host, "connection_create", arguments);

        Assert.Equal((code, detail, field), ((string?)error["code"], (string?)error["detail"], (string?)error["field"]));
        Assert.Equal(0, session.Revision);
    }

    /// <summary>A system can have 10 jump objects and not 11, because the game crashes with more than 10. A hole pair
    /// inside one system counts 2, so with 9 jump objects it refuses with the reason. One more gate to another system
    /// still works.</summary>
    [Fact]
    public void ASystemTakesTenJumpObjectsAndNotEleven()
    {
        var holes = string.Concat(Enumerable.Range(1, 9).Select(i => $"""

            [Object]
            nickname = Li03_h{i}
            archetype = jumphole
            goto = Li02, Li02_01, gate_tunnel_bretonia
            """));
        var (ws, session, host) = Universe(setup: w => w.WriteBase(ConnectionWorkspace.Li03Path,
            "[SystemInfo]\nlocal_faction = br_n_grp\n" + holes));
        using var _ = ws;

        var pair = Failure(host, "connection_create",
            """{ "kind": "jump_hole", "from": { "system": "Li03" }, "to": { "system": "Li03" } }""");
        var tenth = Ok(host, "connection_create", """{ "kind": "jump_gate", "from": { "system": "Li03" }, "to": { "system": "Li02" } }""");
        var eleventh = Failure(host, "connection_create",
            """{ "kind": "jump_hole", "from": { "system": "Li03" }, "to": { "system": "Li01" } }""");

        Assert.Equal(("refused", ConnectionPlans.JumpLimit, null), ((string?)pair["code"], (string?)pair["detail"], (string?)pair["field"]));
        Assert.Equal("Li03 would have 11 jump objects. The game crashes with more than 10 in one system. Delete a jump gate " +
                     "or a jump hole of Li03 first.", (string?)pair["message"]);
        Assert.Equal(10, (int)tenth["jump_objects"]!["from"]!["used"]!);
        Assert.Equal(ConnectionPlans.JumpLimit, (string?)eleventh["detail"]);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>
    /// edit_batch runs system_create, then connection_create that names the new system, then connection_set_lock on a
    /// new end, as one entry. The pair crosses the new file and the file of Li02, and the player lock gives the warning
    /// about the route tables for each end. One undo takes the whole batch back, also the new file.
    /// </summary>
    [Fact]
    public void ABatchMakesASystemAndAGatePairToItInOneEntry()
    {
        var (ws, session, host) = Universe();
        using var _ = ws;

        var result = Ok(host, "edit_batch", $$"""
            { "label": "New system Av01", "steps": [
                { "tool": "system_create", "arguments": { {{Av01}} } },
                { "tool": "connection_create", "arguments": { "kind": "jump_gate", "from": { "system": "Av01" },
                  "to": { "system": "Li02" }, "locked": "player" } },
                { "tool": "connection_set_lock", "arguments": { "object": "obj:Av01_to_Li02", "locked": "both" } } ] }
            """);

        var steps = result["steps"]!.AsArray();
        Assert.Equal(["obj:Av01_to_Li02", "obj:Li02_to_Av01", "zone:Zone_Av01_to_Li02_traffic", "zone:Zone_Li02_to_Av01_traffic"],
            Texts(steps[1]!["adds"]));
        Assert.Equal(2, Messages(steps[1]!["warnings"]).Count(m => m.StartsWith("The route tables leave out", StringComparison.Ordinal)));
        Assert.Equal("Agent: New system Av01", session.UndoLabel);
        var gate = NewSystemWorkspace.New("Object", "Av01_to_Li02");
        Assert.StartsWith("Li02, Li02_to_Av01, ", Line(session, gate, "goto"));
        Assert.Contains(FLHash.CreateID("Av01_to_Li02"), ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Contains(FLHash.CreateID("Li02_to_Av01"), ConnectionWorkspace.LockLines(session, "npc_locked_gate"));

        Ok(host, "undo");
        Assert.Null(session.Get(Av01System));
        Assert.False(session.OpenDocument(NewSystemWorkspace.NewPath)!.State.Exists);
        Assert.Null(session.Get(new SectionRef(ListWorkspace.Li02Path, new SectionIdentity("Object", "Li02_to_Av01"))));
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// connection_set_lock locks a gate of the game and its partner. The player lock removes the route from Li02 to
    /// Li01, and each end gets the warning that the story can depend on the lock. A second call with the same lock
    /// changes nothing. An object that is no gate and no hole gives invalid_value with the argument object.
    /// </summary>
    [Fact]
    public void ConnectionSetLockLocksAGateAndItsPartner()
    {
        var (ws, session, host) = Universe();
        using var _ = ws;

        var result = Ok(host, "connection_set_lock", """{ "object": "obj:Li02_to_Li01", "locked": "player" }""");
        var again = Ok(host, "connection_set_lock", """{ "object": "obj:Li02_to_Li01", "locked": "player" }""");
        var station = Failure(host, "connection_set_lock", """{ "object": "obj:Li01_01", "locked": "npc" }""");

        Assert.Equal((0, 0, 0, 1), ((int)result["routes"]!["new"]!, (int)result["routes"]!["shorter"]!,
            (int)result["routes"]!["longer"]!, (int)result["routes"]!["lost"]!));
        Assert.Equal(2, Messages(result["warnings"]).Count(m => m.EndsWith("The story can depend on this lock.", StringComparison.Ordinal)));
        Assert.Contains(FLHash.CreateID("Li02_to_Li01"), ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Equal(("Agent: Lock Li02_to_Li01", "no_change"), ((string?)result["undo_label"], (string?)again["status"]));
        Assert.Equal(("invalid_value", ConnectionPlans.NotConnection, "object"),
            ((string?)station["code"], (string?)station["detail"], (string?)station["field"]));
        Assert.Equal("Li01_01 is not a jump gate or a jump hole.", (string?)station["message"]);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>
    /// get_universe gives each system with its place, its jump objects and if a connection reaches it, and the
    /// connections with their partners and locks. The data without mods has the same routes, so the build writes no
    /// route tables. A new hole pair changes that. A filter gives one system and its connections, and a page gives the
    /// connections of the systems of the page.
    /// </summary>
    [Fact]
    public void GetUniverseGivesTheSystemsAndTheirConnections()
    {
        var (ws, _, host) = Universe();
        using var __ = ws;

        var all = Ok(host, "get_universe");
        var li02 = Ok(host, "get_universe", """{ "system": "Li02" }""");
        var page = Ok(host, "get_universe", """{ "limit": 1 }""");
        Ok(host, "connection_create", """{ "kind": "jump_hole", "from": { "system": "Li01" }, "to": { "system": "Li03" } }""");
        var after = Ok(host, "get_universe", """{ "system": "Li03" }""");

        var systems = all["systems"]!.AsArray();
        Assert.Equal(["system:Li01", "system:Li02", "system:Li03"], systems.Select(s => (string)s!["ref"]!));
        Assert.Equal([7f, 8f], systems[0]!["map_pos"]!.AsArray().Select(v => (float)v!));
        Assert.Equal(2.5f, (float)systems[1]!["nav_map_scale"]!);
        Assert.Equal([1, 1, 0], systems.Select(s => (int)s!["jump_objects"]!["used"]!));
        Assert.Equal([true, true, false], systems.Select(s => (bool)s!["reachable"]!));
        Assert.Equal(ConnectionWorkspace.Li03Path, (string?)systems[2]!["file"], ignoreCase: true);
        var connections = all["connections"]!.AsArray();
        Assert.Equal(2, connections.Count);
        Assert.Equal(("obj:Li01_to_Li02", "jump_gate", "system:Li01", "system:Li02", "obj:Li02_to_Li01", true, "both"),
            ((string?)connections[0]!["ref"], (string?)connections[0]!["kind"], (string?)connections[0]!["system"],
                (string?)connections[0]!["to_system"], (string?)connections[0]!["to_object"], (bool)connections[0]!["pair_ok"]!,
                (string?)connections[0]!["locked"]));
        Assert.Equal("npc", (string?)connections[1]!["locked"]);
        Assert.False((bool)all["route_files_change"]!);
        Assert.Equal((3, null), ((int)all["total"]!, (string?)all["next_cursor"]));

        Assert.Equal(["system:Li02"], li02["systems"]!.AsArray().Select(s => (string)s!["ref"]!));
        Assert.Equal(["obj:Li02_to_Li01"], li02["connections"]!.AsArray().Select(c => (string)c!["ref"]!));
        Assert.Equal(["system:Li01"], page["systems"]!.AsArray().Select(s => (string)s!["ref"]!));
        Assert.Equal(["obj:Li01_to_Li02"], page["connections"]!.AsArray().Select(c => (string)c!["ref"]!));
        Assert.Equal("1", (string?)page["next_cursor"]);

        Assert.True((bool)after["route_files_change"]!);
        Assert.True((bool)after["systems"]![0]!["reachable"]!);
        var hole = Assert.Single(after["connections"]!.AsArray())!;
        Assert.Equal(("jump_hole", true, "none"), ((string?)hole["kind"], (bool)hole["pair_ok"]!, (string?)hole["locked"]));
    }

    /// <summary>
    /// get_route gives the shortest routes from one system of a route table of W, and the systems that the table does
    /// not reach. The player lock of Li01 to Li02 leaves Li01 without a route. The table of the holes has no route
    /// here. A target gives only its route.
    /// </summary>
    [Fact]
    public void GetRouteGivesTheShortestRoutesOfATable()
    {
        var (ws, _, host) = Universe();
        using var __ = ws;

        var li02 = Ok(host, "get_route", """{ "from": "Li02" }""");
        var li01 = Ok(host, "get_route", """{ "from": "system:Li01", "kind": "gates" }""");
        var holes = Ok(host, "get_route", """{ "from": "Li02", "kind": "holes" }""");
        var to = Ok(host, "get_route", """{ "from": "Li02", "to": "Li03" }""");
        var unknown = Failure(host, "get_route", """{ "from": "Li99" }""");

        var route = Assert.Single(li02["routes"]!.AsArray())!;
        Assert.Equal(("system:Li01", 1), ((string?)route["to"], (int)route["jumps"]!));
        Assert.Equal(["system:Li02", "system:Li01"], Texts(route["systems"]));
        Assert.Equal(["system:Li03"], Texts(li02["unreachable"]));
        Assert.Empty(li01["routes"]!.AsArray());
        Assert.Equal(["system:Li02", "system:Li03"], Texts(li01["unreachable"]));
        Assert.Equal(["system:Li01", "system:Li03"], Texts(holes["unreachable"]));
        Assert.Empty(to["routes"]!.AsArray());
        Assert.Equal(["system:Li03"], Texts(to["unreachable"]));
        Assert.Equal("not_found", (string?)unknown["code"]);
    }

    /// <summary>get_system gives the place on the nav map, the zoom and the jump objects of a system. Its connection
    /// rows and the object rows of list_items give the solar radius of the archetype, or nothing when the archetype has
    /// none.</summary>
    [Fact]
    public void GetSystemAndListItemsGiveTheMapAndTheRadius()
    {
        var (ws, _, host) = Universe(setup: w => w.WriteBase(ListWorkspace.SolarPath,
            SceneWorkspace.Solars.Replace("type = JUMP_GATE", "type = JUMP_GATE\nsolar_radius = 1500", StringComparison.Ordinal) +
            "\n[Solar]\nnickname = jumphole\ntype = JUMP_HOLE\n"));
        using var __ = ws;

        var system = Ok(host, "get_system", """{ "system": "Li02" }""");
        var items = Ok(host, "list_items", """{ "system": "Li02", "kinds": ["object"] }""");

        Assert.Equal([6f, 9f], system["map_pos"]!.AsArray().Select(v => (float)v!));
        Assert.Equal(2.5f, (float)system["nav_map_scale"]!);
        Assert.Equal((1, 10), ((int)system["jump_objects"]!["used"]!, (int)system["jump_objects"]!["max"]!));
        var gate = Assert.Single(system["connections"]!.AsArray())!;
        Assert.Equal(1500f, (float)gate["radius"]!);
        var rows = items["rows"]!.AsArray();
        Assert.Equal(1500f, (float)rows.Single(r => (string?)r!["ref"] == "obj:Li02_to_Li01")!["radius"]!);
        Assert.Null(rows.Single(r => (string?)r!["ref"] == "obj:Li02_sun")!["radius"]);
    }

    /// <summary>list_values gives the jump archetypes for the text JUMP_GATE, and its list of the systems reads W, so it
    /// has a new system before the save.</summary>
    [Fact]
    public void ListValuesGivesTheJumpArchetypesAndTheNewSystems()
    {
        var (ws, _, host) = Universe();
        using var __ = ws;

        var gates = Ok(host, "list_values", """{ "list": "solar_archetype", "text": "JUMP_GATE" }""");
        var holes = Ok(host, "list_values", """{ "list": "solar_archetype", "text": "JUMP_HOLE" }""");
        Ok(host, "system_create", $$"""{ {{Av01}} }""");
        var systems = Ok(host, "list_values", """{ "list": "system" }""");

        Assert.Equal(["jumpgate"], gates["rows"]!.AsArray().Select(r => (string)r!["value"]!));
        Assert.Equal(["jumphole"], holes["rows"]!.AsArray().Select(r => (string)r!["value"]!));
        Assert.Contains("Av01", systems["rows"]!.AsArray().Select(r => (string)r!["value"]!));
    }
}
