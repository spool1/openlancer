using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
}
