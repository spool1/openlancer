using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreLancer;
using OpenLancer.Studio.Agent;

using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The query tools of the agent server on the vanilla data (section 13.3, retail variant, and section 13.4 of the MCP1
/// design): each tool runs without an exception, each result fits the output schema of its tool and the size limit of
/// a result, and the session part of each list and get tool is fast enough for one frame of the editor. The tests
/// count and time. They hold no retail text: the names of the calls come from the data at run time.
/// </summary>
public sealed class AgentRetailTests(ITestOutputHelper output)
{
    /// <summary>The cutscene tools read Lua text with the native compiler of the engine. So the test adds the folder of
    /// the native libraries to the search path, as the editor does when it starts.</summary>
    static AgentRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The time of one frame at 60 frames each second (section 13.4 of the MCP1 design).</summary>
    private const double FrameMilliseconds = 16;

    /// <summary>The calls of each query tool, with arguments from the data: the largest system, its first station,
    /// the first story script and its first trigger, and the first cutscene.</summary>
    private static List<(string Tool, string Arguments)> Calls(AgentHost host)
    {
        static string Json(string text) => JsonSerializer.Serialize(text);
        JsonObject Ok(string tool, string arguments = "{}")
        {
            var step = AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject());
            Assert.NotNull(step.Result);
            Assert.False(step.Result!.IsError, step.Result.Text);
            return step.Result.Content;
        }

        var session = host.Session!;
        var systems = Ok("list_systems", """{ "limit": 200 }""")["rows"]!.AsArray()
            .Select(r => ((string)r!["nickname"]!, (string?)r["file"])).Where(r => r.Item2 != null).ToList();
        var largest = systems.MaxBy(s => session.Sections(s.Item2!).Count).Item1;
        var stationRow = Ok("list_items", $$"""{ "system": {{Json(largest)}}, "object_kinds": ["station"] }""")
            ["rows"]![0]!;
        var station = (string)stationRow["ref"]!;
        var stationName = (string)stationRow["nickname"]!;
        var script = (string)Ok("list_missions")["scripts"]!.AsArray().First(s => s!["slot"] != null)!["ref"]!;
        var trigger = (string)Ok("get_mission", $$"""{ "script": {{Json(script)}} }""")["summary"]!["start_triggers"]![0]!;
        var cutscene = (string)Ok("list_cutscenes", """{ "limit": 1 }""")["rows"]![0]!["ref"]!;
        return
        [
            ("studio_status", "{}"),
            ("list_systems", "{}"),
            ("get_system", $$"""{ "system": {{Json(largest)}} }"""),
            ("list_items", $$"""{ "system": {{Json(largest)}} }"""),
            ("get_item", $$"""{ "ref": {{Json(station)}}, "include_raw": true }"""),
            ("get_item", $$"""{ "ref": {{Json(station)}}, "include_used_by": true }"""),
            ("find_references", $$"""{ "ref": {{Json(station)}} }"""),
            ("find_references", $$"""{ "name": {{Json(stationName)}} }"""),
            ("list_values", """{ "list": "solar_archetype", "text": "MISSION_SATELLITE" }"""),
            ("list_values", $$"""{ "list": "object_of_system", "system": {{Json(largest)}} }"""),
            ("search_assets", """{ "kind": "ship" }"""),
            ("list_prop_groups", "{}"),
            ("list_missions", "{}"),
            ("get_mission", $$"""{ "script": {{Json(script)}} }"""),
            ("get_mission", $$"""{ "script": {{Json(script)}}, "level": "triggers" }"""),
            ("list_values", $$"""{ "list": "mission_trigger", "script": {{Json(script)}} }"""),
            ("get_trigger", $$"""{ "ref": {{Json(trigger)}} }"""),
            ("describe_mission_name", """{ "key": "Cnd_Timer" }"""),
            ("mission_start_plan", $$"""{ "script": {{Json(script)}} }"""),
            ("mission_start_plan", $$"""{ "script": {{Json(script)}}, "start": {{Json(trigger)}} }"""),
            ("list_cutscenes", "{}"),
            ("get_cutscene", $$"""{ "ref": {{Json(cutscene)}} }"""),
            ("get_raw_ini", $$"""{ "ref": {{Json(station)}} }"""),
            ("check", $$"""{ "scope": "item", "ref": {{Json(station)}} }"""),
            ("check", "{}"),
        ];
    }

    [Fact]
    public void EachQueryToolRunsOnTheVanillaDataAndFitsItsSchema()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1agent");
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);

        var results = Calls(host).Select(c =>
        {
            var step = AgentTools.Run(host, c.Tool, JsonNode.Parse(c.Arguments)!.AsObject());
            return (c.Tool, Result: step.Result!);
        }).ToList();

        foreach (var (tool, result) in results)
        {
            Assert.False(result.IsError, $"{tool}: {result.Text}");
            Assert.False(result.Limited(host.Limits).IsError, $"{tool}: {result.Bytes} bytes");
            Assert.Empty(OpenLancer.Tests.SchemaCheck.Problems(AgentTools.Find(tool)!.OutputSchema, result.Content));
            output.WriteLine($"{tool}: {result.Bytes} bytes");
        }
        Assert.Contains(results, r => r.Tool == "list_values" && r.Result.Content["total"]!.GetValue<int>() > 0);
    }

    /// <summary>
    /// The gate of section 13.4 is for a call after the first one. The first call of a tool fills the caches of the
    /// session, for example the models of all mission scripts, as the first open of the tab of the editor does. The
    /// test writes the time of each first call, in the order of the tool table on a new session, and the time of
    /// <c>check</c> with the scope all, without a gate.
    /// </summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void TheSessionPartOfEachListAndGetToolFitsInOneFrame()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1bench");
        List<(string Tool, string Arguments)> calls;
        using (var first = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId))
            calls = Calls(first);
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);

        foreach (var (tool, arguments) in calls)
        {
            var start = Stopwatch.GetTimestamp();
            AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject());
            output.WriteLine($"{Stopwatch.GetElapsedTime(start).TotalMilliseconds,8:0.00} ms  first call: {tool} {arguments}");
        }
        // find_references and get_item with include_used_by read each line of the data and of the mission scripts that
        // names the item. They give their time in the log, with no gate. The gate of each other call takes the median of
        // five runs as one try. The first calls warmed up the code, so the gates can try again. All gates of the test
        // share one patience (see TimingGates).
        var gates = new TimingGates();
        var times = calls.Where(c => c.Tool.StartsWith("list_") || c.Tool.StartsWith("get_") || c.Tool.StartsWith("find_"))
            .Select(c =>
            {
                var arguments = JsonNode.Parse(c.Arguments)!.AsObject();
                TimeSpan Median()
                {
                    var runs = Enumerable.Range(0, 5).Select(_ =>
                    {
                        var call = (JsonObject)arguments.DeepClone();
                        return Timing.Measure(() => AgentTools.Run(host, c.Tool, call));
                    }).Order().ToList();
                    return runs[2];
                }
                var gated = !c.Tool.StartsWith("find_") && !c.Arguments.Contains("include_used_by");
                var median = gated ? gates.Gate(TimeSpan.FromMilliseconds(FrameMilliseconds), Median) : Median();
                return (Call: $"{c.Tool} {c.Arguments}", Gated: gated, Median: median.TotalMilliseconds);
            }).ToList();

        foreach (var (call, _, median) in times)
            output.WriteLine($"{median,8:0.00} ms  {call}");
        Assert.All(times.Where(t => t.Gated),
            t => Assert.True(t.Median < FrameMilliseconds, $"{t.Call}: {t.Median:0.00} ms"));
    }

    /// <summary>
    /// A preview of a delete of a retail station gives the plan of the Scene delete (section 13.3, retail variant, of
    /// the MCP1 design). The plan has the targets, the cascade, the fixes and the "Missing" lines. The preview changes
    /// nothing. The station is the first object of the largest system that has a cascade.
    /// </summary>
    [Fact]
    public void APreviewDeleteOfARetailStationGivesThePlanOfTheSceneDelete()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1delete");
        var session = workspace.Open();
        var host = new OpenLancer.Tests.TestAgentHost(session);
        var systems = session.Systems.Files.OrderByDescending(f => session.Sections(f).Count).ToList();
        var station = systems.SelectMany(f => session.Sections(f))
            .Where(s => OpenLancer.Data.Editing.Scene.SceneSections.KindOf(session, s) ==
                        OpenLancer.Data.Editing.Scene.SceneKind.Object && s.Id.Key != null)
            .First(s => OpenLancer.Data.Editing.Scene.ScenePlans.Delete(session, [s]) is { CanRun: true } plan &&
                        plan.Deletes.Count > 1);
        var expected = OpenLancer.Data.Editing.Scene.ScenePlans.Delete(session, [station]);
        string Ref(OpenLancer.Data.Editing.SectionRef section) =>
            OpenLancer.Data.Editing.Commands.ItemRefs.Canonical(session, section);

        var step = AgentTools.Run(host, "delete_items", new JsonObject
        {
            ["refs"] = new JsonArray(Ref(station)),
            ["preview"] = true,
        });

        var result = step.Result!;
        Assert.False(result.IsError, result.Text);
        Assert.Equal("previewed", (string?)result.Content["status"]);
        Assert.Equal("0", (string?)result.Content["rev"]);
        Assert.Equal(expected.Deletes.Select(d => Ref(d.Section)),
            result.Content["deletes"]!.AsArray().Select(d => (string)d!["ref"]!));
        Assert.Equal(expected.Fixes.Count, result.Content["fixes"]!.AsArray().Count);
        Assert.Equal(expected.Missing.Count + expected.MissionMissing.Count, result.Content["missing"]!.AsArray().Count);
        Assert.Equal(expected.NeedsConfirmation, (bool)result.Content["needs_confirmation"]!);
        Assert.False(session.CanUndo);
        Assert.Null(session.OpenBatch);
        output.WriteLine($"{Ref(station)}: {expected.Deletes.Count} deletes, {expected.Fixes.Count} fixes, " +
                         $"{expected.Missing.Count + expected.MissionMissing.Count} missing lines");
    }

    /// <summary>
    /// The gate of section 13.4 of the MCP1 design. An edit_batch of 100 scene_create_zone steps in the largest retail
    /// system takes less than 500 ms, because the batch runs in one frame of the editor. Each try undoes its batch, so
    /// the next try makes the same zones. A first try warms up the code and the caches, and the gate does not measure
    /// it.
    /// </summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void ABatchOfAHundredZonesInTheLargestSystemFitsTheGate()
    {
        const double limit = 500;
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1zones");
        var session = workspace.Open();
        var host = new OpenLancer.Tests.TestAgentHost(session);
        var largest = session.Systems.Files.MaxBy(f => session.Sections(f).Count)!;
        var system = session.SystemNicknameOf(largest)!;
        // The arguments come from JSON text, as the arguments of a client do.
        var batch = JsonNode.Parse("{ \"steps\": [" + string.Join(", ", Enumerable.Range(0, 100).Select(i =>
            $$"""{ "tool": "scene_create_zone", "arguments": { "system": {{JsonSerializer.Serialize(system)}}, "position": [{{i * 1000}}, 0, 250000] } }""")) +
                                    "] }")!.AsObject();

        TimeSpan Try()
        {
            var start = Stopwatch.GetTimestamp();
            var result = AgentTools.Run(host, "edit_batch", (JsonObject)batch.DeepClone()).Result!;
            var time = Stopwatch.GetElapsedTime(start);
            Assert.False(result.IsError, result.Text);
            Assert.Equal(100, result.Content["adds"]!.AsArray().Count);
            session.Undo();
            return time;
        }

        var first = Try();
        var best = new TimingGates().Gate(TimeSpan.FromMilliseconds(limit), Try, 3);
        output.WriteLine($"{system} ({session.Sections(largest).Count} sections): first batch {first.TotalMilliseconds:0} ms, " +
                         $"best batch {best.TotalMilliseconds:0} ms");
        Assert.True(best.TotalMilliseconds < limit, $"{best.TotalMilliseconds:0.0} ms");
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// Workflow 1 of section 9.5 of the MCP1 design, steps 3, 5 and 6, in the largest retail system with one of its
    /// bases. The agent finds a station archetype with a docking sphere, previews the station, then creates it and a
    /// planet. Each create is one Undo step, and the data gets no new error.
    /// </summary>
    [Fact]
    public void TheCreatesOfWorkflowOneRunInARetailSystem()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1outpost");
        var session = workspace.Open();
        var host = new OpenLancer.Tests.TestAgentHost(session);
        static string Json(string text) => JsonSerializer.Serialize(text);
        JsonObject Ok(string tool, string arguments)
        {
            var result = AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject()).Result!;
            Assert.False(result.IsError, $"{tool}: {result.Text}");
            return result.Content;
        }

        var largest = session.Systems.Files.MaxBy(f => session.Sections(f).Count)!;
        var system = session.SystemNicknameOf(largest)!;
        var baseRef = (string)Ok("get_system", $$"""{ "system": {{Json(system)}} }""")["bases"]![0]!;
        var archetype = Ok("list_values", """{ "list": "solar_archetype", "text": "STATION", "limit": 200 }""")["rows"]!
            .AsArray().Select(r => (string)r!["value"]!)
            .First(a => OpenLancer.Data.Editing.Scene.ScenePlans.AsksForBase(session,
                OpenLancer.Data.Editing.Scene.SceneTemplate.Station, a));
        var create = $$"""
            { "system": {{Json(system)}}, "template": "station", "archetype": {{Json(archetype)}}, "position": [0, 0, 300000],
              "nickname": "ol_test_station_1", "name": "Test Station", "base": {{Json(baseRef)}}
            """;

        var preview = Ok("scene_create_object", create + """, "preview": true }""");
        var made = Ok("scene_create_object", create + "}");
        var planet = Ok("scene_create_object", $$"""
            { "system": {{Json(system)}}, "template": "planet", "archetype": {{Json(PlanetArchetype(session))}},
              "position": [20000, 0, 300000], "nickname": "ol_test_planet_1" }
            """);

        Assert.Equal(["obj:ol_test_station_1"], preview["adds"]!.AsArray().Select(a => (string)a!));
        Assert.Equal("Agent: Create ol_test_station_1", (string?)made["undo_label"]);
        Assert.Empty(made["new_problems"]!.AsArray());
        Assert.Equal("Agent: Create ol_test_planet_1", (string?)planet["undo_label"]);
        Assert.Equal(2, session.Revision);
        session.Undo();
        session.Undo();
        Assert.False(session.CanUndo);
        output.WriteLine($"{system}: a station of {archetype} with the base {baseRef}");
    }

    /// <summary>The first archetype of the planet template in the list of the solar archetypes.</summary>
    private static string PlanetArchetype(OpenLancer.Data.Editing.EditSession session) =>
        OpenLancer.Data.Editing.Scene.ScenePlans.Archetypes(session, OpenLancer.Data.Editing.Scene.SceneTemplate.Planet)[0]
            .Nickname;

    /// <summary>A page of the triggers of each retail script, also with the lines, fits the size limit of a result
    /// (section 8.4 of the MCP1 design). So the agent can read each script in pages.</summary>
    [Fact]
    public void APageOfTheTriggersOfEachRetailScriptFitsTheSizeLimit()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1triggers");
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);
        var scripts = AgentTools.Run(host, "list_missions", null).Result!.Content["scripts"]!.AsArray()
            .Select(s => (string)s!["ref"]!).ToList();

        var largest = (Bytes: 0, Call: "");
        foreach (var script in scripts)
        {
            foreach (var lines in new[] { false, true })
            {
                var arguments = new JsonObject { ["script"] = script, ["level"] = "triggers", ["include_lines"] = lines };
                var result = AgentTools.Run(host, "get_mission", arguments).Result!;
                Assert.False(result.IsError, $"{script}: {result.Text}");
                Assert.False(result.Limited(host.Limits).IsError, $"{script}, lines {lines}: {result.Bytes} bytes");
                if (result.Bytes > largest.Bytes)
                    largest = (result.Bytes, $"{script}, lines {lines}");
            }
        }
        Assert.NotEmpty(scripts);
        output.WriteLine($"The largest page of triggers: {largest.Bytes} bytes, {largest.Call}");
    }

    /// <summary>Each page of <c>get_cutscene</c> with the default limit fits the size limit of a result (section 8.4 of
    /// the MCP1 design), for each retail cutscene. So the agent can read each script in pages, also the largest story
    /// scripts. The test applies the limit of the queue (<see cref="ToolResult.Limited"/>), as a client sees it.</summary>
    [Fact]
    public void EachPageOfEachRetailCutsceneFitsTheSizeLimit()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1cutscenes");
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);
        var scripts = new List<string>();
        string? cursor = null;
        do
        {
            var arguments = new JsonObject { ["limit"] = 200 };
            if (cursor != null)
                arguments["cursor"] = cursor;
            var page = Ok(host, "list_cutscenes", arguments);
            scripts.AddRange(page["rows"]!.AsArray().Select(r => (string)r!["ref"]!));
            cursor = (string?)page["next_cursor"];
        }
        while (cursor != null);

        var largest = (Bytes: 0, Call: "");
        var pages = 0;
        foreach (var script in scripts)
        {
            cursor = null;
            do
            {
                var arguments = new JsonObject { ["ref"] = script };
                if (cursor != null)
                    arguments["cursor"] = cursor;
                var result = AgentTools.Run(host, "get_cutscene", arguments).Result!;
                Assert.False(result.IsError, $"{script}: {result.Text}");
                Assert.False(result.Limited(host.Limits).IsError, $"{script}, cursor {cursor}: {result.Bytes} bytes");
                if (result.Bytes > largest.Bytes)
                    largest = (result.Bytes, $"{script}, cursor {cursor}");
                cursor = (string?)result.Content["next_cursor"];
                pages++;
            }
            while (cursor != null);
        }
        Assert.True(scripts.Count > 1000, $"{scripts.Count} scripts");
        output.WriteLine($"{scripts.Count} scripts, {pages} pages. The largest page: {largest.Bytes} bytes, {largest.Call}");
    }

    /// <summary>The time of one agent mission edit (section 13.4 of the MCP1 design and section 11.4 of the M6b
    /// design).</summary>
    private const double MissionEditMilliseconds = 50;

    /// <summary>Runs a tool and asserts that it gives no error and that the result fits the output schema of the
    /// tool. Gives the content of the result. The arguments go through JSON text, as the arguments of a client
    /// do.</summary>
    private static JsonObject Ok(AgentHost host, string tool, JsonObject arguments)
    {
        var result = AgentTools.Run(host, tool, JsonNode.Parse(arguments.ToJsonString())!.AsObject()).Result!;
        Assert.False(result.IsError, $"{tool} {arguments.ToJsonString()}: {result.Text}");
        Assert.Empty(OpenLancer.Tests.SchemaCheck.Problems(AgentTools.Find(tool)!.OutputSchema, result.Content));
        return result.Content;
    }

    /// <summary>A host without a window that edits the mod of a workspace. It writes no recovery file.</summary>
    private static HeadlessHost OpenToEdit(RetailWorkspace workspace) =>
        HeadlessHost.Open(workspace.ProfilePath, workspace.ModId, new HeadlessOptions { WritesRecovery = false });

    /// <summary>The msn: references of the retail story scripts, from list_missions.</summary>
    private static List<string> StoryScripts(AgentHost host) =>
        Ok(host, "list_missions", new JsonObject())["scripts"]!.AsArray().Where(s => s!["slot"] != null)
            .Select(s => (string)s!["ref"]!).ToList();

    /// <summary>The trigger rows of a script from get_mission, page by page.</summary>
    private static List<JsonNode> TriggerRows(AgentHost host, string script)
    {
        var rows = new List<JsonNode>();
        string? cursor = null;
        do
        {
            // The arguments come from JSON text, as the arguments of a client do.
            var arguments = JsonNode.Parse(
                $$"""{ "script": {{JsonSerializer.Serialize(script)}}, "level": "triggers", "limit": 200 }""")!.AsObject();
            if (cursor != null)
                arguments["cursor"] = cursor;
            var page = Ok(host, "get_mission", arguments)["triggers"]!;
            rows.AddRange(page["rows"]!.AsArray().Select(r => r!));
            cursor = (string?)page["next_cursor"];
        }
        while (cursor != null);
        return rows;
    }

    /// <summary>
    /// The mission read tools and mission_start_plan run over each retail story script (step 9 of MCP1-3). Each call
    /// gives a result, not an error, and the result fits the output schema of its tool. The test reads each trigger,
    /// and plans the start of the mission and a start at each trigger. A plan that cannot start is data: the status
    /// refused and a reason, not a tool error.
    /// </summary>
    [Fact]
    public void TheMissionToolsRunOverEachRetailStoryScript()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1stories");
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);
        var scripts = StoryScripts(host);
        var plans = new Dictionary<string, int>();

        foreach (var script in scripts)
        {
            Ok(host, "get_mission", new JsonObject { ["script"] = script });
            var starts = new List<string> { "mission_start" };
            foreach (var row in TriggerRows(host, script))
            {
                var reference = (string)row["ref"]!;
                Ok(host, "get_trigger", new JsonObject { ["ref"] = reference });
                starts.Add(reference);
            }
            foreach (var start in starts)
            {
                var arguments = new JsonObject { ["script"] = script, ["start"] = start };
                var plan = Ok(host, "mission_start_plan", arguments)["plan"]!;
                var status = (string)plan["status"]!;
                if (status == "refused")
                    Assert.False(string.IsNullOrWhiteSpace((string?)plan["reason"]), $"{script} {start}: no reason");
                else
                    Assert.Equal("ready", status);
                plans[status] = plans.GetValueOrDefault(status) + 1;
            }
        }

        Assert.Equal(14, scripts.Count);
        output.WriteLine($"{scripts.Count} story scripts: " +
                         string.Join(", ", plans.OrderBy(p => p.Key).Select(p => $"{p.Value} plans {p.Key}")));
    }

    /// <summary>A preview of mission_add_trigger on a retail story script gives the status previewed and the result of
    /// the add, and changes nothing: the revision, the version of the script, the unsaved state and the history stay
    /// (section 13.3, retail variant, of the MCP1 design). The new trigger waits 3 seconds and the first start trigger
    /// starts it.</summary>
    [Fact]
    public void APreviewOfANewTriggerInARetailStoryScriptChangesNothing()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1preview");
        using var host = OpenToEdit(workspace);
        var session = host.Session!;
        var script = StoryScripts(host)[0];
        var path = script["msn:".Length..];
        var version = session.Missions.Version(path);

        var preview = Ok(host, "mission_add_trigger", AddTrigger(host, script, preview: true));

        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Equal("0", (string?)preview["rev"]);
        Assert.Contains($"trg:{path}#ol_agent_trigger", preview["adds"]!.AsArray().Select(a => (string?)a),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(version, (long?)preview["versions"]![path]);
        Assert.Equal((0, false, false), (session.Revision, session.IsDirty, session.CanUndo));
        Assert.Equal(version, session.Missions.Version(path));
        Assert.Null(session.OpenBatch);
    }

    /// <summary>The arguments of a mission_add_trigger call on a script: a new trigger that waits 3 seconds and starts
    /// the second trigger of the script. The first start trigger of the script starts it.</summary>
    private static JsonObject AddTrigger(AgentHost host, string script, bool preview = false)
    {
        var summary = Ok(host, "get_mission", new JsonObject { ["script"] = script })["summary"]!;
        var start = (string)summary["start_triggers"]![0]!;
        var next = (string)TriggerRows(host, script)[1]["nickname"]!;
        static string Json(string text) => JsonSerializer.Serialize(text);
        return JsonNode.Parse($$"""
            { "script": {{Json(script)}}, "nickname": "ol_agent_trigger",
              "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } },
              "actions": [ { "key": "Act_ActTrig", "values": { "trigger": {{Json(next)}} } } ],
              "started_by": [ {{Json(start)}} ], "preview": {{(preview ? "true" : "false")}} }
            """)!.AsObject();
    }

    /// <summary>
    /// The mission gate of section 13.4 of the MCP1 design (the gate of section 11.4 of the M6b design) through the
    /// agent path: one mission_add_trigger call on the largest retail story script takes less than 50 ms. The call runs
    /// the command in the shared agent batch with the strict check, the inverse and the result. A first call warms up
    /// the code and the caches, and the gate does not measure it. A try takes the median of five calls, and an undo
    /// after each call brings back the state before it (see <see cref="TimingGates"/>).
    /// </summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void AnAgentTriggerAddOnTheLargestRetailStoryScriptFitsTheGate()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1missiongate");
        using var host = OpenToEdit(workspace);
        var session = host.Session!;
        var script = StoryScripts(host).MaxBy(s => session.OpenDocument(s["msn:".Length..])!.Working.Count)!;
        var arguments = AddTrigger(host, script);

        TimeSpan Call()
        {
            var call = (JsonObject)arguments.DeepClone();
            ToolResult? result = null;
            var time = Timing.Measure(() => result = AgentTools.Run(host, "mission_add_trigger", call).Result);
            Assert.False(result!.IsError, result.Text);
            session.Undo();
            return time;
        }

        var first = Call();
        var tries = new List<List<double>>();
        var best = new TimingGates().Gate(TimeSpan.FromMilliseconds(MissionEditMilliseconds), () =>
        {
            var runs = Enumerable.Range(0, 5).Select(_ => Call().TotalMilliseconds).Order().ToList();
            tries.Add(runs);
            return TimeSpan.FromMilliseconds(runs[2]);
        });

        var sections = session.OpenDocument(script["msn:".Length..])!.Working.Count;
        var runs = tries.Select(t => $"median {t[2]:0.0} ms of {string.Join(", ", t.Select(r => r.ToString("0.0")))}");
        output.WriteLine($"{script} ({sections} sections): first call {first.TotalMilliseconds:0.0} ms, tries " +
                         string.Join("; ", runs));
        Assert.True(best.TotalMilliseconds < MissionEditMilliseconds,
            $"The median of one call took {best.TotalMilliseconds:0.0} ms.");
        Assert.False(session.CanUndo);
    }

    /// <summary>The thn: references of the retail cutscene scripts of a folder group, from list_cutscenes, page by
    /// page.</summary>
    private static List<string> Cutscenes(AgentHost host, string category)
    {
        var refs = new List<string>();
        string? cursor = null;
        do
        {
            var arguments = new JsonObject { ["category"] = category, ["limit"] = 200 };
            if (cursor != null)
                arguments["cursor"] = cursor;
            var page = Ok(host, "list_cutscenes", arguments);
            refs.AddRange(page["rows"]!.AsArray().Select(r => (string)r!["ref"]!));
            cursor = (string?)page["next_cursor"];
        }
        while (cursor != null);
        return refs;
    }

    /// <summary>The text of W of a script, to compare two states.</summary>
    private static string ScriptText(OpenLancer.Data.Editing.EditSession session, string script) =>
        OpenLancer.Data.Thn.ThnWriter.Write(session.Cutscene(script["thn:".Length..])!.ToGlobals());

    /// <summary>
    /// Previews of the cutscene edits on a retail base script give a result and change nothing (section 13.3, retail
    /// variant, of the MCP1 design, for MCP1-4). The script is the first base script that the editor can change, with
    /// an event and a camera. The previews move the first event with its guards, set its time and a number of the
    /// camera, add a camera move and rename the camera. The revision, the history, the unsaved state, the new
    /// cutscenes and W stay. The test writes the time of the first and the second rename_item, because the first
    /// rename of an entity reads the uses of the name in the INI files on the session thread.
    /// </summary>
    [Fact]
    public void PreviewsOfTheCutsceneEditsOnARetailBaseScriptChangeNothing()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1thnpreview");
        using var host = OpenToEdit(workspace);
        var session = host.Session!;
        var (script, view) = Cutscenes(host, "bases")
            .Select(s => (Ref: s, View: WholeCutscene(host, s)))
            .First(s => s.View["read_only_reason"] == null && s.View["events"]!.AsArray().Count > 0 &&
                        s.View["entities"]!.AsArray().Any(e => (string?)e!["type"] == "CAMERA"));
        var before = ScriptText(session, script);
        var ev = view["events"]![0]!;
        var camera = view["entities"]!.AsArray().First(e => (string?)e!["type"] == "CAMERA")!;
        var number = camera["fields"]!.AsObject().First(f => f.Value!.GetValueKind() == JsonValueKind.Number);
        static double Number(JsonNode node) => double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);
        JsonObject Guarded() => new()
        {
            ["ref"] = (string)ev["ref"]!, ["expect_type"] = (string?)ev["type"], ["expect_time"] = ev["time"]!.DeepClone(),
        };
        var setTime = Guarded();
        setTime["fields"] = new JsonObject { ["time"] = Number(ev["time"]!) + 1 };
        var calls = new List<(string Tool, JsonObject Arguments, string Items)>
        {
            ("cutscene_move_events", new JsonObject { ["events"] = new JsonArray(Guarded()), ["time_delta"] = 0.5 }, "changes"),
            ("set_fields", setTime, "changes"),
            ("set_fields", new JsonObject
            {
                ["ref"] = (string)camera["ref"]!, ["fields"] = new JsonObject { [number.Key] = Number(number.Value!) + 1 },
            }, "changes"),
            ("cutscene_add_event", new JsonObject
            {
                ["cutscene"] = script, ["type"] = "START_SPATIAL_PROP_ANIM", ["time"] = 0,
                ["targets"] = new JsonArray((string)camera["name"]!), ["length"] = 2,
            }, "adds"),
        };

        foreach (var (tool, arguments, items) in calls)
        {
            arguments["preview"] = true;
            arguments["with_patch"] = true;
            var result = Ok(host, tool, arguments);
            Assert.Equal("previewed", (string?)result["status"]);
            Assert.NotEmpty(result[items]!.AsArray());
            Assert.False(string.IsNullOrEmpty((string?)result["patch"]![script["thn:".Length..]]), tool);
        }
        var rename = new JsonObject
        {
            ["ref"] = (string)camera["ref"]!, ["new_name"] = (string)camera["name"]! + "_ol", ["preview"] = true,
        };
        var times = Enumerable.Range(0, 2).Select(_ =>
        {
            JsonObject? result = null;
            var time = Timing.Measure(() => result = Ok(host, "rename_item", (JsonObject)rename.DeepClone()));
            Assert.Equal("previewed", (string?)result!["status"]);
            Assert.Single(result["renames"]!.AsArray());
            return time.TotalMilliseconds;
        }).ToList();

        Assert.Equal((0, false, false), (session.Revision, session.IsDirty, session.CanUndo));
        Assert.Null(session.OpenBatch);
        Assert.Empty(session.NewCutscenes);
        Assert.Equal(before, ScriptText(session, script));
        output.WriteLine($"{view["entities"]!.AsArray().Count} entities, {view["events"]!.AsArray().Count} events: " +
                         $"{calls.Count + 2} previews. rename_item: first call {times[0]:0} ms with the scan of the uses, " +
                         $"second call {times[1]:0.0} ms");
    }

    /// <summary>The sample of <see cref="EachFieldOfARetailBaseScriptGoesBackIntoSetFields"/> takes each fifth base
    /// script.</summary>
    private const int SampleStep = 5;

    /// <summary>The view of get_cutscene with the entities and the events of all pages.</summary>
    private static JsonObject WholeCutscene(AgentHost host, string script)
    {
        var view = Ok(host, "get_cutscene", new JsonObject { ["ref"] = script, ["limit"] = 200 });
        var cursor = (string?)view["next_cursor"];
        while (cursor != null)
        {
            var page = Ok(host, "get_cutscene", new JsonObject { ["ref"] = script, ["limit"] = 200, ["cursor"] = cursor });
            foreach (var list in new[] { "entities", "events" })
            {
                foreach (var element in page[list]!.AsArray())
                    view[list]!.AsArray().Add(element!.DeepClone());
            }
            cursor = (string?)page["next_cursor"];
        }
        Assert.Equal(((int)view["entity_count"]!, (int)view["event_count"]!),
            (view["entities"]!.AsArray().Count, view["events"]!.AsArray().Count));
        return view;
    }

    /// <summary>
    /// On a sample of the retail base scripts, each field of each element that get_cutscene gives goes back into a
    /// preview of set_fields, and the preview gives no_change. So an agent can read a retail cutscene, change one value
    /// and write the fields back. Some retail scripts have two entities with one name. The earlier entity gives no
    /// fields, because its reference names the later entity. The sample is each fifth script in the order of
    /// list_cutscenes. The Lua text of each element of the sample is one value for the check of a value text (see
    /// <see cref="OpenLancer.Studio.Commands.ThnValueText"/>), so an agent can send a stored value back as Lua text. The
    /// test writes counts only.
    /// </summary>
    [Fact]
    public void EachFieldOfARetailBaseScriptGoesBackIntoSetFields()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1thnfields");
        using var host = OpenToEdit(workspace);
        var session = host.Session!;
        var scripts = Cutscenes(host, "bases").Where((_, i) => i % SampleStep == 0).ToList();
        var (elements, fields, readOnly, hidden, tables) = (0, 0, 0, 0, 0);
        var changed = new List<string>();

        foreach (var script in scripts)
        {
            var view = WholeCutscene(host, script);
            if (view["read_only_reason"] != null)
            {
                readOnly++;
                continue;
            }
            foreach (var table in session.Cutscene(script["thn:".Length..])!.Entities
                         .Concat(session.Cutscene(script["thn:".Length..])!.Events).Select(e => e.Table))
            {
                var text = OpenLancer.Data.Thn.ThnWriter.Value(table);
                Assert.True(OpenLancer.Studio.Commands.ThnValueText.Problem(text, session.ThnSymbols) == null,
                    $"{script}: {OpenLancer.Studio.Commands.ThnValueText.Problem(text, session.ThnSymbols)}");
                tables++;
            }
            foreach (var element in view["entities"]!.AsArray().Concat(view["events"]!.AsArray()))
            {
                var values = element!["fields"]!.AsObject();
                // A later entity with the same name hides the entity, and its reference names the later entity. So
                // the view gives no fields for it.
                if (element["read_only_reason"] != null)
                {
                    hidden++;
                    Assert.Empty(values);
                }
                if (values.Count == 0)
                    continue;
                var result = Ok(host, "set_fields", new JsonObject
                {
                    ["ref"] = (string)element["ref"]!, ["fields"] = values.DeepClone(), ["preview"] = true,
                });
                elements++;
                fields += values.Count;
                if ((string?)result["status"] != "no_change")
                    changed.Add((string)element["ref"]!);
            }
        }

        Assert.NotEmpty(scripts);
        Assert.True(changed.Count == 0, $"{changed.Count} elements changed, the first is {changed.FirstOrDefault()}");
        Assert.Equal((0, false, false), (session.Revision, session.IsDirty, session.CanUndo));
        output.WriteLine($"{scripts.Count} base scripts ({readOnly} read-only), {elements} elements, {fields} fields: " +
                         $"each preview gave no_change. {hidden} entities that a later entity hides gave no fields. " +
                         $"The Lua text of each of {tables} elements is one value.");
    }
}
