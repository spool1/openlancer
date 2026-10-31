using System;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The query tools of section 7.1 of the MCP1 design on synthetic profiles (section 13.1, items 1, 8, 10, 11 and 12):
/// each result fits the output schema of its tool, each reference of a result resolves, and each refusal has its code.
/// </summary>
public sealed class AgentQueryTests
{
    private static (TestWorkspace Ws, TestAgentHost Host) Scene()
    {
        var ws = SceneWorkspace.Create();
        return (ws, new TestAgentHost(ws.Session("act")));
    }

    [Fact]
    public void AToolThatNeedsAProfileGivesNotAvailableWithoutOne()
    {
        Assert.Equal(("not_available", "NoProfile"), Code(Error(new TestAgentHost(null), "list_systems")));
    }

    [Fact]
    public void ListSystemsPagesAndFilters()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var all = Ok(host, "list_systems");
        var first = Ok(host, "list_systems", """{ "limit": 1 }""");
        var second = Ok(host, "list_systems", """{ "limit": 1, "cursor": "1" }""");
        var filtered = Ok(host, "list_systems", """{ "text": "li02" }""");

        Assert.Equal(["system:Li01", "system:Li02"], Refs(all));
        Assert.Equal(ListWorkspace.Li01Path, (string?)all["rows"]![0]!["file"], ignoreCase: true);
        Assert.Equal("base", (string?)all["rows"]![0]!["origin"]!["layer"]);
        Assert.Equal(2, (int)first["total"]!);
        Assert.Equal("1", (string?)first["next_cursor"]);
        Assert.Equal(["system:Li02"], Refs(second));
        Assert.Null(second["next_cursor"]);
        Assert.Equal(["system:Li02"], Refs(filtered));
    }

    [Fact]
    public void AListAboveTheLimitOrABadCursorIsRefused()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var limit = Error(host, "list_systems", """{ "limit": 201 }""");
        var smaller = Error(new TestAgentHost(host.Session) { TestLimits = new AgentLimits { MaxListRows = 1 } },
            "list_systems", """{ "limit": 2 }""");

        Assert.Equal(("limit", "ListRows"), Code(limit));
        Assert.Equal("One call can give at most 200 rows. Use a smaller limit and the cursor of the result.", limit.Message);
        Assert.Equal(("limit", "ListRows"), Code(smaller));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "list_systems", """{ "cursor": "x" }""")));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "list_systems", """{ "cursor": "9" }""")));
    }

    [Fact]
    public void GetSystemGivesCountsBasesConnectionsAndFields()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var system = Ok(host, "get_system", """{ "system": "li01" }""");
        var byRef = Ok(host, "get_system", """{ "system": "system:Li01" }""");
        var missing = Error(host, "get_system", """{ "system": "Li0x" }""");
        var other = Error(host, "get_system", """{ "system": "obj:Li01_01" }""");

        Assert.Equal("system:Li01", (string?)system["ref"]);
        Assert.Equal(ListWorkspace.Li01Path, (string?)system["file"], ignoreCase: true);
        Assert.Equal(4, (int)system["counts"]!["zone"]!);
        Assert.Equal(3, (int)system["counts"]!["light"]!);
        Assert.Equal(1, (int)system["object_kinds"]!["gate"]!);
        Assert.Equal(2, (int)system["object_kinds"]!["lane_ring"]!);
        Assert.Equal(["base:Li01_01_Base"], system["bases"]!.AsArray().Select(b => (string)b!));
        var gate = system["connections"]!.AsArray().Single(c => (string?)c!["kind"] == "gate")!;
        Assert.Equal(("obj:Li01_to_Li02", "system:Li02", "obj:Li02_to_Li01"),
            ((string?)gate["ref"], (string?)gate["to_system"], (string?)gate["to_object"]));
        Assert.All(system["connections"]!.AsArray().Where(c => (string?)c!["kind"] == "lane_ring"), ring =>
            Assert.All(new[] { ring!["previous"], ring["next"] }.OfType<JsonNode>(), r => Assert.StartsWith("obj:", (string?)r)));
        Assert.Equal(["nebula:Zone_cloud"], system["nebulae"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["field:Zone_cloud"], system["asteroid_fields"]!.AsArray().Select(n => (string)n!));
        var file = system["fields"]!.AsArray().Single(f => (string?)f!["key"] == "file")!;
        Assert.Equal("base", (string?)file["origin"]!["layer"]);
        Assert.Equal("system:Li01", (string?)byRef["ref"]);
        Assert.Equal(("not_found", ItemRefs.NotFound), Code(missing));
        Assert.Contains("Li01", missing.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), Code(other));
    }

    [Fact]
    public void ListItemsFiltersByKindObjectKindTextAndPlace()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var zones = Ok(host, "list_items", """{ "system": "Li01", "kinds": ["zone"] }""");
        var gates = Ok(host, "list_items", """{ "system": "Li01", "object_kinds": ["gate"] }""");
        var text = Ok(host, "list_items", """{ "system": "Li01", "text": "child" }""");
        var near = Ok(host, "list_items", """{ "system": "Li01", "kinds": ["zone"], "near": { "point": [100, 0, 0], "radius": 10 } }""");
        var station = Ok(host, "list_items", """{ "system": "Li01", "text": "Li01_01", "kinds": ["object"] }""")["rows"]![0]!;

        Assert.Equal(["zone:Zone_ring", "zone:Zone_cloud", "zone:Zone_hole", "zone:Zone_Tradelane_a"], Refs(zones));
        Assert.Equal(["obj:Li01_to_Li02"], Refs(gates));
        Assert.Equal(["obj:Li01_child", "obj:Li01_grandchild"], Refs(text));
        Assert.Equal(["zone:Zone_hole"], Refs(near));
        Assert.Equal("station", (string?)station["object_kind"]);
        Assert.Equal("station1", (string?)station["archetype"]);
        Assert.Contains("base_object", station["components"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(("invalid_argument", "BadArgument"),
            Code(Error(host, "list_items", """{ "system": "Li01", "kinds": ["planet"] }""")));
    }

    [Fact]
    public void EachReferenceOfAListResolvesWithGetItem()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var rows = Ok(host, "list_items", """{ "system": "Li01", "limit": 200 }""");

        foreach (var reference in Refs(rows))
        {
            var item = Ok(host, "get_item", $$"""{ "ref": {{Json(reference!)}} }""");
            Assert.Equal(reference, (string?)item["section"]!["ref"]);
        }
        Assert.Contains("light:Li01/light1", Refs(rows));
    }

    [Fact]
    public void GetItemGivesFieldsComponentsChildrenUsersAndRawText()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var item = Ok(host, "get_item", """{ "ref": "obj:Li01_01", "include_raw": true, "include_used_by": true }""")["section"]!;
        var plain = Ok(host, "get_item", """{ "ref": "obj:Li01_01" }""")["section"]!;

        Assert.Equal("Object", (string?)item["type"]);
        var archetype = item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "archetype")!;
        Assert.Equal("""[["station1"]]""", archetype["lines"]!.ToJsonString());
        Assert.Equal("Solar archetype", (string?)archetype["label"]);
        Assert.False((bool)archetype["locked"]!);
        Assert.True((bool)item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "nickname")!["locked"]!);
        Assert.Contains(item["components"]!.AsArray(), c => (string?)c!["kind"] == "dockable_station");
        Assert.Equal(["obj:Li01_child"], item["children"]!.AsArray().Select(c => (string)c!));
        Assert.StartsWith("[Object]\nnickname = Li01_01", (string?)item["raw"]);
        Assert.Contains(item["used_by"]!.AsArray(), r => (string?)r!["ref"] == "obj:Li01_child" && (string?)r["key"] == "parent");
        Assert.NotNull(item["mentions"]);
        Assert.Null(plain["raw"]);
        Assert.Null(plain["used_by"]);
    }

    /// <summary>A key is locked when the field commands cannot change it, as in the Inspector. The file of a system moves
    /// the contents of the system, so it is locked.</summary>
    [Fact]
    public void GetItemLocksTheKeysThatTheFieldCommandsCannotChange()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var system = Ok(host, "get_item", $$"""{ "ref": {{Json(@"sec:DATA\UNIVERSE\universe.ini#system/Li01")}} }""")
            ["section"]!["fields"]!.AsArray();

        Assert.True((bool)system.Single(f => (string?)f!["key"] == "file")!["locked"]!);
        Assert.True((bool)system.Single(f => (string?)f!["key"] == "nickname")!["locked"]!);
    }

    [Fact]
    public void GetItemMarksTheLinesOfAnUnsavedEdit()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var station = ListWorkspace.Li01("Li01_01");
        host.Session!.Execute(OpenLancer.Data.Editing.EditCommand.SetLinesFromText(station, "loadout", ["lo_other"]));

        var item = Ok(host, "get_item", """{ "ref": "obj:Li01_01" }""")["section"]!;
        var row = Ok(host, "list_items", """{ "system": "Li01", "text": "Li01_01", "kinds": ["object"] }""")["rows"]![0]!;

        Assert.True((bool)item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "loadout")!["origin"]!["unsaved"]!);
        Assert.False((bool)item["fields"]!.AsArray().Single(f => (string?)f!["key"] == "archetype")!["origin"]!["unsaved"]!);
        Assert.Equal("active_mod", (string?)row["origin"]!["layer"]);
        Assert.True((bool)row["origin"]!["unsaved"]!);
    }

    [Fact]
    public void FindReferencesTakesAReferenceOrAName()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var byRef = Ok(host, "find_references", """{ "ref": "obj:Li01_01" }""");
        var byName = Ok(host, "find_references", """{ "name": "li_n_grp" }""");

        Assert.Contains(byRef["lines"]!.AsArray(), l => (string?)l!["ref"] == "obj:Li01_child" && (string?)l["list"] == "object_of_system");
        Assert.Contains(byName["lines"]!.AsArray(), l => (string?)l!["ref"] == "obj:Li01_01" && (string?)l["key"] == "reputation");
        Assert.Equal(("invalid_argument", ItemRefs.BadReference),
            Code(Error(host, "find_references", """{ "ref": "obj:Li01_01", "name": "x" }""")));
        Assert.Equal(("invalid_argument", ItemRefs.BadReference), Code(Error(host, "find_references")));
    }

    /// <summary>A base has its name in strid_name, as a system has. So list_values and get_item give the name of a base,
    /// and the text of list_values finds a base by its name (final finding of MCP1-2).</summary>
    [Fact]
    public void ABaseGivesTheNameOfItsStridName()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(ListWorkspace.UniversePath, """
            [system]
            nickname = Li01
            file = systems\Li01\li01.ini
            [system]
            nickname = Li02
            file = systems\Li02\li02.ini
            [Base]
            nickname = Li01_01_Base
            system = Li01
            strid_name = 1234
            [Base]
            nickname = Li02_01_Base
            system = Li02
            """);
        var host = new TestAgentHost(ws.Session("act"));
        host.Names[1234] = "Ol Harbor";

        var rows = Ok(host, "list_values", """{ "list": "base", "text": "harbor" }""");
        var item = Ok(host, "get_item", """{ "ref": "base:Li01_01_Base" }""")["section"]!;

        Assert.Equal([("Li01_01_Base", "Ol Harbor")],
            rows["rows"]!.AsArray().Select(r => ((string?)r!["value"], (string?)r["label"])));
        Assert.Equal("Ol Harbor", (string?)item["name"]);
    }

    [Fact]
    public void ListValuesFindsTheDetailAndNeedsTheSystemOfAListOfOneSystem()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var stations = Ok(host, "list_values", """{ "list": "solar_archetype", "text": "DOCKING_RING" }""");
        var objects = Ok(host, "list_values", """{ "list": "object_of_system", "system": "Li02" }""");
        var byRef = Ok(host, "list_values", """{ "list": "object_of_system", "system": "system:Li02" }""");
        var unknown = Error(host, "list_values", """{ "list": "spaceships" }""");

        Assert.Equal(["dock_ring"], stations["rows"]!.AsArray().Select(r => (string)r!["value"]!));
        Assert.Equal($"sec:{ListWorkspace.SolarPath}#Solar/dock_ring", (string?)stations["rows"]![0]!["ref"]);
        Assert.Equal(["Li02_to_Li01", "Li02_01"], objects["rows"]!.AsArray().Select(r => (string)r!["value"]!));
        Assert.Equal(objects["rows"]!.ToJsonString(), byRef["rows"]!.ToJsonString());
        // The schema of the argument has the ids of the lists, so an unknown list gives the ids.
        Assert.Equal(("invalid_argument", "BadArgument"), Code(unknown));
        Assert.Contains("solar_archetype", unknown.Message);
        var noSystem = Error(host, "list_values", """{ "list": "object_of_system" }""");
        Assert.Equal(("invalid_argument", "BadArgument"), Code(noSystem));
        Assert.Equal("The list object_of_system holds the items of one system. Give the argument 'system'.", noSystem.Message);
        Assert.Equal(("invalid_argument", "BadArgument"),
            Code(Error(host, "list_values", """{ "list": "solar_archetype", "system": "Li01" }""")));
        Assert.Equal(("invalid_argument", "BadArgument"),
            Code(Error(host, "list_values", """{ "list": "object_of_system", "system": "Li01", "script": "msn:x.ini" }""")));
    }

    [Fact]
    public void SearchAssetsAndListPropGroups()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var solars = Ok(host, "search_assets", """{ "kind": "solar_archetype", "text": "planet" }""");
        var groups = Ok(host, "list_prop_groups");

        Assert.Contains("asset:solar_archetype/planet1", Refs(solars));
        var asset = Ok(host, "get_item", """{ "ref": "asset:solar_archetype/planet1" }""");
        Assert.Equal("planet1", (string?)asset["asset"]!["nickname"]);
        Assert.Empty(groups["groups"]!.AsArray());
        Assert.Equal(("invalid_argument", "UnknownCategory"), Code(Error(host, "search_assets", """{ "category": "none" }""")));
        Assert.Equal(("not_found", ItemRefs.NotFound), Code(Error(host, "get_item", """{ "ref": "group:none" }""")));
    }

    [Fact]
    public void TheMissionToolsReadTheQueriesOfTheMissionLayer()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var script = Json("msn:" + MissionEdits.Script);
        var trigger = Json($"trg:{MissionEdits.Script}#ol_wave");

        var missions = Ok(host, "list_missions");
        var summary = Ok(host, "get_mission", $$"""{ "script": {{script}} }""");
        var triggers = Ok(host, "get_mission", $$"""{ "script": {{script}}, "level": "triggers" }""");
        var wave = Ok(host, "get_trigger", $$"""{ "ref": {{trigger}} }""");
        var raw = Ok(host, "get_trigger", $$"""{ "ref": {{trigger}}, "include_lines": true }""");
        var timer = Ok(host, "describe_mission_name", """{ "key": "cnd_timer" }""");
        var item = Ok(host, "get_item", $$"""{ "ref": {{trigger}} }""");

        Assert.Contains("msn:" + MissionEdits.Script, missions["scripts"]!.AsArray().Select(s => (string?)s!["ref"]));
        Assert.Null(summary["triggers"]);
        Assert.Equal(["ol_start", "ol_wave", "ol_end"],
            triggers["triggers"]!["rows"]!.AsArray().Select(t => (string)t!["nickname"]!));
        Assert.Equal("ol_wave", (string?)wave["nickname"]);
        Assert.All(wave["lines"]!.AsArray(), l => Assert.Empty(l!["texts"]!.AsArray()));
        Assert.Contains(raw["lines"]!.AsArray(), l => l!["texts"]!.AsArray().Count > 0);
        Assert.Equal("Cnd_Timer", (string?)timer["name"]);
        Assert.Equal("trigger", (string?)item["kind"]);
        Assert.Equal(("not_found", "NameNotFound"), Code(Error(host, "describe_mission_name", """{ "key": "Cnd_Timr" }""")));
        Assert.Contains("Cnd_Timer", Error(host, "describe_mission_name", """{ "key": "Cnd_Timr" }""").Message);
    }

    /// <summary>The tool mission_dry_run calls the dry run query of the mission layer (section 7.2 of the M6c design).
    /// It changes nothing, and an input that does not fit gives its reason.</summary>
    [Fact]
    public void TheDryRunToolFoldsTheInputsOfTheCall()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var script = Json("msn:" + MissionEdits.Script);
        var revision = m.Session.Revision;

        var run = Ok(host, "mission_dry_run", $$"""
            { "script": {{script}}, "inputs": [ { "kind": "start_mission" }, { "kind": "heartbeats", "count": 1 },
              { "kind": "heartbeats", "until_timer": true },
              { "kind": "event", "line": { "key": "Cnd_Destroyed", "values": { "target": "ol_ship_1" } } } ],
              "since_step": 3 }
            """);
        var bad = Error(host, "mission_dry_run", $$"""{ "script": {{script}}, "inputs": [ { "kind": "jump" } ] }""");
        var field = Error(host, "mission_dry_run", $$"""{ "script": {{script}}, "inputs": [ { "kind": "heartbeats", "count": 0 } ] }""");

        Assert.Equal(4, (int)run["total_steps"]!);
        Assert.Equal(4, (int)run["steps"]![0]!["step"]!);
        Assert.Equal("success", (string?)run["state"]!["end"]!["kind"]);
        Assert.Equal(revision, m.Session.Revision);
        Assert.Equal(("invalid_argument", "BadJson"), Code(bad));
        Assert.Equal(("invalid_argument", "BadJson"), Code(field));
        Assert.Contains("count of heartbeats", field.Message);
    }

    /// <summary>The tool and the JSON host read a dry run request with one reader, so they give the same result.</summary>
    [Fact]
    public void TheDryRunToolAndTheJsonHostReadOneRequestInOneWay()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var request = $$"""
            { "script": {{Json("msn:" + MissionEdits.Script)}}, "timer_bound": "longest", "since_step": 1,
              "inputs": [ { "kind": "start_mission" }, { "kind": "heartbeats", "count": 1 }, { "kind": "start_mission" } ] }
            """;

        var tool = Ok(host, "mission_dry_run", request);
        var json = JsonNode.Parse(new MissionJsonHost(m.Session).DryRun(request).Json)!;

        // The second start removes the steps before it, so the run has one step, and since_step 1 gives none.
        Assert.Equal(1, (int)tool["total_steps"]!);
        Assert.Empty(tool["steps"]!.AsArray());
        Assert.Equal(json["total_steps"]!.ToJsonString(), tool["total_steps"]!.ToJsonString());
        Assert.Equal(json["state"]!["active"]!.ToJsonString(), tool["state"]!["active"]!.ToJsonString());
    }

    [Fact]
    public void TheCutsceneToolsGiveTheScriptAndItsElements()
    {
        using var ws = ThnSessions.Workspace();
        var host = new TestAgentHost(ws.Session("act"));
        var path = ThnSessions.ScriptPath;

        var list = Ok(host, "list_cutscenes", """{ "text": "x.thn" }""");
        var cutscene = Ok(host, "get_cutscene", $$"""{ "ref": {{Json("thn:" + path)}} }""");
        var camera = Ok(host, "get_item", $$"""{ "ref": {{Json($"thn:{path}#entity/Cam_A")}} }""")["cutscene_element"]!;
        var psys = Ok(host, "get_item", $$"""{ "ref": {{Json($"thn:{path}#event/1")}} }""")["cutscene_element"]!;

        Assert.Equal("thn:" + path, (string?)list["rows"]![0]!["ref"]);
        Assert.Equal(10, (float)list["rows"]![0]!["duration"]!);
        Assert.Equal("base", (string?)list["rows"]![0]!["kind"]);
        Assert.Equal(6, cutscene["entities"]!.AsArray().Count);
        Assert.Equal(6, cutscene["events"]!.AsArray().Count);
        Assert.Equal(("CAMERA", "Cam_A"), ((string?)camera["type"], (string?)camera["name"]));
        Assert.Equal(("START_PSYS", 5f, 2f), ((string?)psys["type"], (float)psys["time"]!, (float)psys["length"]!));
        Assert.Equal(["Fx"], psys["targets"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(("invalid_argument", ItemRefs.BadReference),
            Code(Error(host, "get_item", $$"""{ "ref": {{Json("thn:" + path)}} }""")));
        Assert.Equal(("not_found", ItemRefs.NotFound),
            Code(Error(host, "get_item", $$"""{ "ref": {{Json($"thn:{path}#event/9")}} }""")));
    }

    [Fact]
    public void GetRawIniGivesTheTextOfAFileOrASectionWithMarks()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var file = Ok(host, "get_raw_ini", $$"""{ "file": {{Json(@"data/universe/systems/li01/li01.ini")}} }""");
        var section = Ok(host, "get_raw_ini", """{ "ref": "obj:Li01_01" }""");

        Assert.Equal(ListWorkspace.Li01Path, (string?)file["file"]);
        Assert.Null(file["ref"]);
        Assert.StartsWith("[Object]\nnickname = Li01_01\n", (string?)file["text"]);
        Assert.Equal("obj:Li01_01", (string?)section["ref"]);
        Assert.StartsWith("[Object]", (string?)section["text"]);
        Assert.DoesNotContain("[zone]", (string?)section["text"]);
        Assert.Equal(("not_found", ItemRefs.NotFound),
            Code(Error(host, "get_raw_ini", $$"""{ "file": {{Json(@"DATA\none.ini")}} }""")));
    }

    [Theory]
    [InlineData(@"C:\x.ini")]
    [InlineData("C:x.ini")]
    [InlineData(@"\x.ini")]
    [InlineData("/x.ini")]
    [InlineData(@"\\server\share\x.ini")]
    [InlineData(@"DATA\x.ini:stream")]
    [InlineData(@"..\x.ini")]
    [InlineData(@"DATA\..\..\x.ini")]
    [InlineData("NUL")]
    [InlineData(@"DATA\con.ini")]
    public void APathThatIsNotAnInstallPathIsRefusedByEachToolThatTakesAPath(string path)
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        string Ref(string kind, string rest = "") => Json($"{kind}:{path}{rest}");
        var calls = new[]
        {
            ("get_raw_ini", $$"""{ "file": {{Json(path)}} }"""),
            ("get_raw_ini", $$"""{ "ref": {{Ref("sec", "#Object/x")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("sec", "#Object/x")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("msn")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("trg", "#x")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("line", "#Trigger/x/0")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("label", "#x")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("inst", "#x")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("thn", "#entity/x")}} }"""),
            ("get_item", $$"""{ "ref": {{Ref("thn", "#event/0")}} }"""),
            ("get_mission", $$"""{ "script": {{Ref("msn")}} }"""),
            ("get_trigger", $$"""{ "ref": {{Ref("trg", "#x")}} }"""),
            ("find_references", $$"""{ "ref": {{Ref("sec", "#Object/x")}} }"""),
            ("find_references", $$"""{ "ref": {{Ref("trg", "#x")}} }"""),
            ("find_references", $$"""{ "ref": {{Ref("line", "#Trigger/x/0")}} }"""),
            ("check", $$"""{ "scope": "item", "ref": {{Ref("sec", "#Object/x")}} }"""),
            ("check", $$"""{ "scope": "mission", "ref": {{Ref("msn")}} }"""),
            ("check", $$"""{ "scope": "cutscene", "ref": {{Ref("thn")}} }"""),
            ("get_cutscene", $$"""{ "ref": {{Ref("thn")}} }"""),
            ("list_values", $$"""{ "list": "mission_trigger", "script": {{Ref("msn")}} }"""),
        };

        foreach (var (tool, arguments) in calls)
        {
            Assert.Equal((tool, arguments, "invalid_argument", InstallPaths.NotInstallPath),
                (tool, arguments, Error(host, tool, arguments).Code, Error(host, tool, arguments).Detail));
        }
    }

    /// <summary>A text argument has at most 1024 characters, so one call cannot hold the thread of the session for a
    /// long time (section 8.4 of the MCP1 design). A long name that does not resolve gets no closest names.</summary>
    [Fact]
    public void ATextArgumentAboveTheLengthLimitIsRefused()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var longest = "obj:" + new string('x', TextParam.MaxLength - 4);

        var refused = Error(host, "get_item", $$"""{ "ref": {{Json(longest + "x")}} }""");
        var missing = Error(host, "get_item", $$"""{ "ref": {{Json(longest)}} }""");
        var unknown = Error(host, "get_system", $$"""{ "system": "Li01", {{Json(new string('a', 5000))}}: 1 }""");

        Assert.Equal(("invalid_argument", "BadArgument"), Code(refused));
        Assert.Equal($"The argument 'ref' needs a text of 1 to {TextParam.MaxLength} characters.", refused.Message);
        Assert.Equal(("not_found", ItemRefs.NotFound), Code(missing));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(unknown));
        Assert.Contains($"'{new string('a', 64)}...'", unknown.Message);
        Assert.True(unknown.Message!.Length < 200, unknown.Message);
        Assert.Equal(TextParam.MaxLength, (int)AgentTools.Find("get_item")!.InputSchema["properties"]!["ref"]!["maxLength"]!);
    }

    /// <summary>Each result gives the revision of the data that it read (section 8.6, item 3 of the MCP1 design), so an
    /// agent can read, then edit with the revision. A tool error gives no revision.</summary>
    [Fact]
    public void EachResultGivesTheRevisionOfTheDataThatItRead()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var before = Ok(host, "list_systems");
        host.Session!.Execute(EditCommand.SetLinesFromText(ListWorkspace.Li01("Li01_01"), "loadout", ["lo_other"]));
        var after = Ok(host, "get_item", """{ "ref": "obj:Li01_01" }""");
        var error = Call(host, "get_item", """{ "ref": "obj:none" }""");

        Assert.Equal("0", (string?)before["rev"]);
        Assert.Equal("1", (string?)after["rev"]);
        Assert.Equal((string?)Ok(host, "studio_status")["rev"], (string?)after["rev"]);
        Assert.True(error.IsError);
        Assert.Null(error.Content["rev"]);
    }

    /// <summary>The parser reads <c>nan</c> and <c>1e39</c> as numbers that JSON cannot hold. Such a position gives no
    /// position, and the list of the system still works.</summary>
    [Fact]
    public void APositionThatIsNotAFiniteNumberGivesNoPosition()
    {
        using var ws = SceneWorkspace.Create();
        // An edit of the editor cannot write such a number, but a file of the game data or of a mod can hold it.
        var text = System.Text.RegularExpressions.Regex.Replace(SceneWorkspace.Li01Text, @"nickname = Li01_01\r?\n",
            "nickname = Li01_01\npos = 1e39, 0, 0\n");
        ws.WriteBase(ListWorkspace.Li01Path, text.Replace("nickname = Li01_child", "nickname = Li01_child\npos = nan, 0, 0"));
        var host = new TestAgentHost(ws.Session("act"));

        var rows = Ok(host, "list_items", """{ "system": "Li01", "kinds": ["object"], "limit": 200 }""")["rows"]!.AsArray();
        var near = Ok(host, "list_items", """{ "system": "Li01", "near": { "point": [0, 0, 0], "radius": 1e30 } }""");

        Assert.Null(rows.Single(r => (string?)r!["ref"] == "obj:Li01_01")!["pos"]);
        Assert.Null(rows.Single(r => (string?)r!["ref"] == "obj:Li01_child")!["pos"]);
        Assert.DoesNotContain("obj:Li01_01", Refs(near));
    }

    /// <summary>A popular item has many users, so <c>get_item</c> gives the first users with their count, and
    /// <c>find_references</c> gives all of them in pages.</summary>
    [Fact]
    public void GetItemGivesTheFirstUsersAndTheirCount()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var small = new TestAgentHost(host.Session) { TestLimits = new AgentLimits { ListRows = 1 } };

        var item = Ok(small, "get_item", """{ "ref": "obj:Li01_01", "include_used_by": true }""")["section"]!;
        var all = Ok(host, "find_references", """{ "ref": "obj:Li01_01" }""");

        Assert.Single(item["used_by"]!.AsArray());
        Assert.Equal((int)all["total"]!, (int)item["used_by_total"]!);
        Assert.True((int)all["total"]! >= 1);
        Assert.Empty(item["mission_used_by"]!.AsArray());
    }

    [Fact]
    public void FindReferencesPagesTheLinesAndFindsTheMissionLinesOfAName()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var ship = Json($"sec:{MissionEdits.Script}#MsnShip/ol_ship_1");

        var station = Ok(host, "find_references", """{ "name": "ol_station" }""");
        var all = Ok(host, "find_references", $$"""{ "ref": {{ship}} }""");
        var first = Ok(host, "find_references", $$"""{ "ref": {{ship}}, "limit": 1 }""");
        var second = Ok(host, "find_references", $$"""{ "ref": {{ship}}, "limit": 1, "cursor": "1" }""");

        Assert.Contains(station["mission_lines"]!.AsArray(), l => (string?)l!["key"] == "Act_LockDock");
        Assert.Equal(station["lines"]!.AsArray().Count + station["mission_lines"]!.AsArray().Count, (int)station["total"]!);
        Assert.Null(station["next_cursor"]);
        Assert.True((int)all["total"]! >= 2);
        Assert.Single(first["mission_lines"]!.AsArray());
        Assert.Equal("1", (string?)first["next_cursor"]);
        Assert.Equal(all["mission_lines"]![1]!.ToJsonString(), second["mission_lines"]![0]!.ToJsonString());
    }

    [Fact]
    public void ListValuesGivesTheItemsOfOneMissionScript()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var script = Json("msn:" + MissionEdits.Script);

        var triggers = Ok(host, "list_values", $$"""{ "list": "mission_trigger", "script": {{script}} }""");
        var noScript = Error(host, "list_values", """{ "list": "mission_trigger" }""");
        var withSystem = Error(host, "list_values", $$"""{ "list": "mission_trigger", "script": {{script}}, "system": "x" }""");
        var missing = Error(host, "list_values", """{ "list": "mission_trigger", "script": "msn:DATA\\MISSIONS\\none.ini" }""");
        var catalog = JsonNode.Parse(AgentResources.Find("openlancer://catalog/lists")!.Read())!["lists"]!.AsArray();

        Assert.Equal(["ol_end", "ol_start", "ol_wave"], triggers["rows"]!.AsArray().Select(r => (string)r!["value"]!).Order());
        Assert.Equal(("invalid_argument", "BadArgument"), Code(noScript));
        Assert.Equal("The list mission_trigger holds the items of one mission script. Give the argument 'script'.",
            noScript.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), Code(withSystem));
        Assert.Equal(("not_found", ItemRefs.NotFound), Code(missing));
        var trigger = catalog.Single(l => (string?)l!["id"] == "mission_trigger")!;
        Assert.Equal((false, true), ((bool)trigger["one_system"]!, (bool)trigger["one_script"]!));
    }

    [Fact]
    public void GetMissionGivesTheCompletenessAndPagesTheTriggersInSummaryForm()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var script = Json("msn:" + MissionEdits.Script);
        string Trigger(string name) => $"trg:{MissionEdits.Script}#{name}";

        var summary = Ok(host, "get_mission", $$"""{ "script": {{script}} }""");
        var first = Ok(host, "get_mission", $$"""{ "script": {{script}}, "level": "triggers", "limit": 2 }""")["triggers"]!;
        var second = Ok(host, "get_mission",
            $$"""{ "script": {{script}}, "level": "triggers", "limit": 2, "cursor": "2" }""")["triggers"]!;
        var lines = Ok(host, "get_mission", $$"""{ "script": {{script}}, "level": "triggers", "include_lines": true }""")
            ["triggers"]!["rows"]!.AsArray();
        var row = Ok(host, "list_missions")["scripts"]!.AsArray()
            .Single(s => string.Equals((string?)s!["ref"], "msn:" + MissionEdits.Script, StringComparison.OrdinalIgnoreCase))!;

        var completeness = summary["completeness"]!;
        Assert.False(string.IsNullOrEmpty((string?)completeness["title"]));
        Assert.Equal(completeness["steps"]!.AsArray().Count, (int)completeness["total"]!);
        Assert.InRange((int)completeness["done"]!, 0, (int)completeness["total"]!);
        Assert.Equal(3, (int)first["total"]!);
        Assert.Equal(["ol_start", "ol_wave"], first["rows"]!.AsArray().Select(t => (string)t!["nickname"]!));
        Assert.Equal("2", (string?)first["next_cursor"]);
        Assert.Equal(["ol_end"], second["rows"]!.AsArray().Select(t => (string)t!["nickname"]!));
        var start = first["rows"]![0]!;
        Assert.True((bool)start["starts_with_mission"]!);
        Assert.Equal([Trigger("ol_wave"), Trigger("ol_end")], start["starts"]!.AsArray().Select(t => (string)t!),
            StringComparer.OrdinalIgnoreCase);
        var wave = first["rows"]![1]!;
        Assert.Equal(3, wave["actions"]!.AsArray().Sum(a => (int)a!["count"]!));
        Assert.False(string.IsNullOrEmpty((string?)wave["event"]));
        Assert.Null(wave["lines"]);
        Assert.All(lines, t => Assert.All(t!["lines"]!.AsArray(), l => Assert.StartsWith("line:", (string?)l!["ref"])));
        Assert.Contains(lines[1]!["lines"]!.AsArray(), l => (string?)l!["key"] == "Cnd_Timer" && (string?)l["texts"]![0] == "3");
        Assert.False(string.IsNullOrEmpty((string?)row["openlancer"]));
        Assert.NotNull(row["gaps"]);
    }

    [Fact]
    public void CheckPagesTheProblemsAndCountsAllOfThem()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var station = ListWorkspace.Li01("Li01_01");
        host.Session!.Execute(EditCommand.SetLinesFromText(station, "loadout", ["lo_missing"]));
        host.Session!.Execute(EditCommand.SetLinesFromText(station, "archetype", ["arch_missing"]));

        var all = Ok(host, "check", """{ "scope": "item", "ref": "obj:Li01_01" }""");
        var first = Ok(host, "check", """{ "scope": "item", "ref": "obj:Li01_01", "limit": 1 }""");
        var second = Ok(host, "check", """{ "scope": "item", "ref": "obj:Li01_01", "limit": 1, "cursor": "1" }""");

        Assert.True((int)all["total"]! >= 2);
        Assert.Equal((int)all["total"]!, all["problems"]!.AsArray().Count);
        Assert.Single(first["problems"]!.AsArray());
        Assert.Equal("1", (string?)first["next_cursor"]);
        Assert.Equal((int)all["errors"]!, (int)first["errors"]!);
        Assert.Equal(all["problems"]![1]!.ToJsonString(), second["problems"]![0]!.ToJsonString());
    }

    [Fact]
    public void TheConsoleGivesTheNextEntriesAfterSinceAndTellsWhenMoreMatch()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var editor = new TestAgentHost(host.Session, AgentHostKind.Editor);
        for (var i = 1; i <= 3; i++)
        {
            editor.ConsoleEntries.Add(new AgentConsoleEntry(i, new DateTime(2026, 1, 1), AgentSeverity.Info, "Editor",
                $"Entry {i}.", null, OriginKind.OpenLancer));
        }
        static long[] Sequences(JsonObject result) =>
            result["entries"]!.AsArray().Select(e => (long)e!["sequence"]!).ToArray();

        var next = Ok(editor, "get_console", """{ "since": 0, "limit": 2 }""");
        var rest = Ok(editor, "get_console", """{ "since": 2, "limit": 2 }""");
        var newest = Ok(editor, "get_console", """{ "limit": 2 }""");

        Assert.Equal([1L, 2L], Sequences(next));
        Assert.True((bool)next["more"]!);
        Assert.Equal([3L], Sequences(rest));
        Assert.False((bool)rest["more"]!);
        Assert.Equal([2L, 3L], Sequences(newest));
        Assert.True((bool)newest["more"]!);
    }

    [Fact]
    public void ATargetCheckThatCannotRunGivesNotAvailable()
    {
        using var m = MissionEdits.Create();
        var now = new TestAgentHost(m.Session)
        {
            Target = _ => throw new EditException(CheckQueries.TargetCheckError("The game folder is gone.")),
        };
        var later = new TestAgentHost(m.Session)
        {
            Target = _ => _ => throw new EditException(CheckQueries.TargetCheckError("The check stopped.")),
        };

        var first = Error(now, "check", """{ "target": "vanilla-1.1" }""");
        var second = Error(later, "check", """{ "target": "vanilla-1.1" }""");

        Assert.Equal(("not_available", CheckQueries.TargetCheckFailed), Code(first));
        Assert.StartsWith("The target check did not run. The game folder is gone. Make sure that", first.Message);
        Assert.Equal(("not_available", CheckQueries.TargetCheckFailed), Code(second));
    }

    /// <summary>An exception of a tool that is not a refusal is a fault of the server. The call gives a tool error with
    /// a code, so the agent can read it, and the queue writes its line of the audit log.</summary>
    [Fact]
    public void AFaultOfAToolGivesStepFailedWithTheReason()
    {
        using var m = MissionEdits.Create();
        var now = new TestAgentHost(m.Session) { Target = _ => throw new InvalidOperationException("A fault.") };
        var later = new TestAgentHost(m.Session) { Target = _ => _ => throw new InvalidOperationException("A late fault.") };

        var first = Error(now, "check", """{ "target": "vanilla-1.1" }""");
        var second = Error(later, "check", """{ "target": "vanilla-1.1" }""");

        Assert.Equal(("step_failed", "ToolFailed"), Code(first));
        Assert.Equal("The tool check stopped because of an error of the server: A fault. The tool changed nothing.",
            first.Message);
        Assert.Equal(("step_failed", "ToolFailed"), Code(second));
        Assert.Contains("A late fault.", second.Message);
    }

    [Fact]
    public void CheckGivesTheProblemsOfEachScope()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session)
        {
            Target = t => TargetNote("target " + t),
        };
        var script = Json("msn:" + MissionEdits.Script);

        var all = Ok(host, "check");
        var mission = Ok(host, "check", $$"""{ "scope": "mission", "ref": {{script}} }""");
        var target = Ok(host, "check", """{ "target": "vanilla-1.1" }""");

        Assert.Equal("all", (string?)all["scope"]);
        Assert.False((bool)all["save_blocked"]!);
        Assert.Equal("mission", (string?)mission["scope"]);
        Assert.EndsWith(" target vanilla-1.1", (string?)target["note"]);
        Assert.Equal("target", (string?)target["scope"]);
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "check", """{ "scope": "mission" }""")));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "check", $$"""{ "ref": {{script}} }""")));
        Assert.Equal(("invalid_argument", "BadArgument"),
            Code(Error(host, "check", """{ "target": "vanilla-1.1", "scope": "item" }""")));
        Assert.Equal(("invalid_argument", "BadArgument"), Code(Error(host, "check", """{ "target": "openlancer" }""")));
    }

    [Fact]
    public void CheckOfAnItemGivesTheProblemsOfItsSection()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        host.Session!.Execute(OpenLancer.Data.Editing.EditCommand.SetLinesFromText(ListWorkspace.Li01("Li01_01"), "loadout",
            ["lo_missing"]));

        var item = Ok(host, "check", """{ "scope": "item", "ref": "obj:Li01_01" }""");

        Assert.Contains(item["problems"]!.AsArray(), p => ((string?)p!["message"])!.Contains("lo_missing"));
        Assert.Equal("obj:Li01_01", (string?)item["problems"]![0]!["ref"]);
    }

    [Fact]
    public void TheConsoleIsOnlyInTheEditorHost()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        var editor = new TestAgentHost(host.Session, AgentHostKind.Editor);
        editor.ConsoleEntries.Add(new AgentConsoleEntry(1, new System.DateTime(2026, 1, 1), AgentSeverity.Info, "Editor", "Opened.", null,
            OpenLancer.Data.OriginKind.OpenLancer));
        editor.ConsoleEntries.Add(new AgentConsoleEntry(2, new System.DateTime(2026, 1, 1), AgentSeverity.Warning, "Agent", "x.", "obj:Li01_01",
            OpenLancer.Data.OriginKind.OpenLancer));

        var entries = Ok(editor, "get_console", """{ "since": 1 }""");

        Assert.Equal(("not_available", "EditorOnly"), Code(Error(host, "get_console")));
        Assert.Equal([2L], entries["entries"]!.AsArray().Select(e => (long)e!["sequence"]!));
        Assert.Equal("openlancer", (string?)entries["entries"]![0]!["origin"]);
    }

    [Fact]
    public void AHiddenGroupGivesNotAvailableAndTheStudioGroupStays()
    {
        var (ws, host) = Scene();
        using var _ = ws;
        host.Hidden.Add(ToolGroup.Scene);

        var hidden = Error(host, "list_systems");

        Assert.Equal(("not_available", "GroupHidden"), Code(hidden));
        Assert.Equal("The user hid the tool group 'scene'. Ask the user to show it in the agent settings.", hidden.Message);
        Assert.Equal(["studio", "missions", "cutscenes", "play"], Ok(host, "studio_status")["tool_groups"]!.AsArray().Select(g => (string)g!));
    }

    [Fact]
    public void AnArgumentThatDoesNotFitTheSchemaIsRefusedWithItsName()
    {
        var (ws, host) = Scene();
        using var _ = ws;

        var unknown = Error(host, "get_system", """{ "system": "Li01", "sytem": "x" }""");
        var missing = Error(host, "get_system");
        var type = Error(host, "list_systems", """{ "limit": "ten" }""");
        var nested = Error(host, "list_items", """{ "system": "Li01", "near": { "point": [1, 2] , "radius": 1 } }""");

        Assert.All(new[] { unknown, missing, type, nested }, e => Assert.Equal(("invalid_argument", "BadArgument"), Code(e)));
        Assert.Equal("The tool get_system has no argument 'sytem'. Its arguments are: system.", unknown.Message);
        Assert.Equal("The argument 'system' is necessary. Give it, then call the tool again.", missing.Message);
        Assert.Equal("The argument 'limit' needs a whole number from 1.", type.Message);
        Assert.Equal("The argument 'near.point' needs three numbers.", nested.Message);
    }

    private static string[] Refs(JsonObject page) =>
        (page["rows"] ?? page["groups"])!.AsArray().Select(r => (string)r!["ref"]!).ToArray();

    private static (string?, string?) Code((string? Code, string? Detail, string? Message) error) => (error.Code, error.Detail);
}
