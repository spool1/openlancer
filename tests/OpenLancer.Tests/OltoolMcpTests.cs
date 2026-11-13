using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.OltoolExportTests;

namespace OpenLancer.Tests;

/// <summary>
/// <c>oltool mcp</c> (section 6.2 and section 13, items 1.1, 2.6 and 3 of the MCP1 design). The tests cover the
/// snapshot of the tool list and the end-to-end tests of the server without a window, also workflows 2, 3 and 4 of
/// section 9.5. A test starts <c>oltool mcp serve</c> as a child
/// process through the stdio client of the SDK. It calls the tools, stops the server, and replays the recorded calls
/// with <c>oltool mcp replay</c>.
/// </summary>
public sealed class OltoolMcpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Tool => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oltool.exe" : "oltool");

    [Fact]
    public async Task TheToolListIsTheSnapshot()
    {
        var (exit, output, errors) = await RunAsync("mcp", "tools", "--json");

        Assert.True(exit == 0, errors);
        var snapshot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AgentTools.snapshot.json"));
        // When a tool changes on purpose, write the snapshot again: oltool mcp tools --json > AgentTools.snapshot.json.
        Assert.Equal(snapshot.ReplaceLineEndings("\n").Trim(), output.ReplaceLineEndings("\n").Trim());
        Assert.Equal(AgentTools.All.Select(t => t.Name),
            JsonNode.Parse(output)!["tools"]!.AsArray().Select(t => (string)t!["name"]!));
    }

    [Fact]
    public async Task TheTextToolListNamesEachTool()
    {
        var (exit, output, _) = await RunAsync("mcp", "tools");

        Assert.Equal(0, exit);
        Assert.All(AgentTools.All, t => Assert.Contains($"{t.Name}: {t.Title}", output));
        Assert.Contains("get_console: Read the Console (editor only)", output);
    }

    /// <summary>The token helper exits 2 and prints nothing on stdout for an address that is not 127.0.0.1 (section
    /// 13.2, item 4 of the MCP1 design). It refuses such an address before it reads a discovery file, so the test does
    /// not depend on an editor of the user.</summary>
    [Theory]
    [InlineData("http://localhost:47815/mcp")]
    [InlineData("http://[::1]:47815/mcp")]
    [InlineData("http://example.com:47815/mcp")]
    [InlineData("https://127.0.0.1:47815/mcp")]
    public async Task TheTokenHelperExits2ForAnAddressThatIsNot127001(string url)
    {
        var info = new ProcessStartInfo(Tool)
        {
            ArgumentList = { "mcp", "token" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["CLAUDE_CODE_MCP_SERVER_URL"] = url;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndOnOwnThread();
        var errors = process.StandardError.ReadToEndOnOwnThread();

        await process.ExitAsync(TimeSpan.FromMinutes(1), "oltool mcp token did not end.");
        Assert.Equal(2, process.ExitCode);
        Assert.Empty(await output);
        Assert.Contains("Do not use localhost.", await errors);
    }

    [Fact]
    public async Task ServeAndReplayCheckTheirArguments()
    {
        using var ws = SceneWorkspace.Create();

        var noMod = await RunAsync("mcp", "serve", ws.ProfilePath, "--read-only");
        var unknown = await RunAsync("mcp", "serve", ws.ProfilePath, "--mod", "none");
        var both = await RunAsync("mcp", "serve", ws.ProfilePath, "--mod", "act", "--restore-recovery", "--discard-recovery");
        var readOnly = await RunAsync("mcp", "serve", ws.ProfilePath, "--mod", "act", "--read-only", "--discard-recovery");
        var twice = await RunAsync("mcp", "serve", ws.ProfilePath, "--mod", "act", "--no-save", "--no-save");
        var replay = await RunAsync("mcp", "replay", ws.ProfilePath, "--mod", "act");

        Assert.Equal(2, noMod.Exit);
        Assert.Contains("The server needs the active mod.", noMod.Errors);
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("has no mod 'none'", unknown.Errors);
        Assert.Equal(2, both.Exit);
        Assert.Contains("Give --restore-recovery or --discard-recovery, not both.", both.Errors);
        Assert.Equal(2, readOnly.Exit);
        Assert.Contains("A server with --read-only does not read the recovery file.", readOnly.Errors);
        Assert.Equal(2, twice.Exit);
        Assert.Equal(2, replay.Exit);
        Assert.Empty(noMod.Output + unknown.Output + both.Output + readOnly.Output + twice.Output + replay.Output);
    }

    /// <summary>A server on stdio and its client. Disposal closes the client and the standard input, and waits for the
    /// end of the server.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;

        private Server(Process process, McpClient client)
        {
            this.process = process;
            Client = client;
            errors = process.StandardError.ReadToEndOnOwnThread();
        }

        public McpClient Client { get; }

        /// <summary>The exit code after <see cref="DisposeAsync"/>.</summary>
        public int ExitCode => process.ExitCode;

        /// <summary>The standard error of the server after <see cref="DisposeAsync"/>.</summary>
        public Task<string> Errors => errors;

        /// <summary>Starts <c>oltool mcp serve</c> on the mod <c>act</c> of a profile. The test starts the child process
        /// itself, because the stdio transport of the SDK starts a command through cmd.exe on Windows. cmd.exe splits a
        /// command path with a space, and the test folder can have one.</summary>
        public static async Task<Server> Start(string profile, params string[] options)
        {
            var info = new ProcessStartInfo(Tool)
            {
                ArgumentList = { "mcp", "serve", profile, "--mod", "act" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var option in options)
                info.ArgumentList.Add(option);
            var process = Process.Start(info)!;
            try
            {
                var client = await McpClient.CreateAsync(
                    new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
                    cancellationToken: Token);
                return new Server(process, client);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        /// <summary>Ends the server without the end of its standard input, as a client that ends it at once does. Then
        /// it waits until the lock of the mod is free.</summary>
        /// <remarks>On a busy computer, Windows can release the handle of the lock a moment after the exit of the
        /// process. A session that opens before that is read-only.</remarks>
        public async Task KillAsync(string modFolder)
        {
            process.Kill(entireProcessTree: true);
            await process.ExitAsync(TimeSpan.FromSeconds(30), "oltool mcp serve did not end after the kill.");
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (ModLock.HeldBy(modFolder, "act") != null)
            {
                Assert.True(DateTime.UtcNow < end, "The lock of mod 'act' stayed 10 s after the exit of the server.");
                await Task.Delay(50, Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    await Client.DisposeAsync();
                    process.StandardInput.Close();
                    await process.ExitAsync(TimeSpan.FromSeconds(60), "oltool mcp serve did not end at the end of stdin.");
                }
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
    }

    /// <summary>The arguments of a call from JSON text, for the client of the SDK and for the log.</summary>
    private static Dictionary<string, object?> Arguments(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

    /// <summary>The first value of a page of <c>list_values</c>.</summary>
    private static string First(JsonElement page) => page.GetProperty("rows")[0].GetProperty("value").GetString()!;

    /// <summary>A text as a JSON string, with the quotes.</summary>
    private static string Json(string text) => JsonSerializer.Serialize(text);

    /// <summary>The text of a property of a result.</summary>
    private static string? Text(JsonElement element, string property) => element.GetProperty(property).GetString();

    /// <summary>The texts of an array of a result.</summary>
    private static string[] Texts(JsonElement array) => array.EnumerateArray().Select(a => a.GetString()!).ToArray();

    /// <summary>The calls of a test through the client of a server, with their results, as the audit log records them.
    /// The replay runs them again on a fresh copy of the profile.</summary>
    private sealed class CallLog
    {
        private readonly List<(string Tool, string Arguments, ToolOutcome Outcome)> calls = [];

        /// <summary>Calls a tool and asserts that it gives no error. Gives the structured content of the
        /// result.</summary>
        public async Task<JsonElement> Call(McpClient client, string tool, string arguments = "{}")
        {
            var (content, error) = await Send(client, tool, arguments);
            Assert.True(!error, $"{tool}: {content.GetRawText()}");
            return content;
        }

        /// <summary>Calls a tool and asserts that it gives an error with the code. Gives the structured content of the
        /// error.</summary>
        public async Task<JsonElement> Error(McpClient client, string tool, string arguments, string code)
        {
            var (content, error) = await Send(client, tool, arguments);
            Assert.True(error, $"{tool}: {content.GetRawText()}");
            Assert.Equal(code, content.GetProperty("code").GetString());
            return content;
        }

        private async Task<(JsonElement Content, bool Error)> Send(McpClient client, string tool, string arguments)
        {
            // The server takes at most 10 calls each second (section 8.4 of the MCP1 design).
            await Task.Delay(TimeSpan.FromMilliseconds(150), Token);
            var result = await client.CallToolAsync(tool, Arguments(arguments), cancellationToken: Token);
            var content = result.StructuredContent!.Value;
            var error = result.IsError == true;
            calls.Add((tool, arguments, error
                ? new ToolOutcome(ToolOutcome.Error, content.GetProperty("code").GetString(),
                    content.GetProperty("detail").GetString())
                : new ToolOutcome(ToolOutcome.Ok)));
            return (content, error);
        }

        /// <summary>Writes the calls as an audit log into the folder of a fresh copy of the profile, and runs
        /// <c>oltool mcp replay</c> of the log on the copy.</summary>
        public async Task<(int Exit, string Output, string Errors)> ReplayAsync(TestWorkspace copy)
        {
            var log = Path.Combine(copy.Root, "calls.jsonl");
            File.WriteAllLines(log, calls.Select(c => new JsonObject
            {
                ["tool"] = c.Tool,
                ["arguments"] = JsonNode.Parse(c.Arguments),
                ["result"] = AuditLog.Outcome(c.Outcome),
            }.ToJsonString()));
            return await RunAsync("mcp", "replay", copy.ProfilePath, "--mod", "act", log);
        }
    }

    /// <summary>The test makes an edit through the server, waits 3 seconds and ends the process without the end of stdin
    /// (section 13.2, item 6 of the MCP1 design). The recovery file then has the edit, and a new session finds
    /// it.</summary>
    /// <remarks>The server writes the file 2 seconds after the edit. A cold process under the load of the parallel tests
    /// can need more time for its first write. So the test waits for the file before it ends the process. The file
    /// still comes without the end of stdin.</remarks>
    [Fact]
    public async Task AnEditStaysInTheRecoveryFileWhenTheServerEndsWithoutTheEndOfStdin()
    {
        using var ws = SceneWorkspace.Create();
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));

        await using (var server = await Server.Start(ws.ProfilePath))
        {
            var edit = await server.Client.CallToolAsync("set_fields",
                Arguments("""{ "ref": "obj:Li01_01", "fields": { "pos": [10, 20, 30] } }"""), cancellationToken: Token);
            Assert.NotEqual(true, edit.IsError);
            await Task.Delay(TimeSpan.FromSeconds(3), Token);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!File.Exists(recovery) && DateTime.UtcNow < deadline)
                await Task.Delay(100, Token);
            await server.KillAsync(ws.ModFolder("act"));
        }
        var session = ws.Session("act");

        Assert.True(File.Exists(recovery));
        Assert.True(session.HasRecovery);
        Assert.Empty(session.RestoreRecovery());
        Assert.Equal("10, 20, 30", AgentEditToolTests.Line(session, SceneWorkspace.Object("Li01_01"), "pos"));
    }

    /// <summary>
    /// The end-to-end test of section 13.3 of the MCP1 design. The server without a window runs workflow 2 of section 9.5
    /// without the Play step, then saves. The patch files then have the zones of the route, the wreck and its loadout. A
    /// second server on the same mod gives <c>read_only</c>. A replay of the recorded calls on a fresh copy of the
    /// profile exits 0 and gives the same files of the mod.
    /// </summary>
    [Fact]
    public async Task WorkflowTwoThroughTheServerSavesAndTheReplayGivesTheSameFiles()
    {
        using var ws = BuilderWorkspace.Create();
        using var copy = BuilderWorkspace.Create();
        var calls = new CallLog();

        await using (var server = await Server.Start(ws.ProfilePath))
        {
            var client = server.Client;
            Task<JsonElement> Call(string tool, string arguments = "{}") => calls.Call(client, tool, arguments);

            var tools = await client.ListToolsAsync(cancellationToken: Token);
            var status = await Call("studio_status");
            await Call("list_systems");
            await Call("list_items", """{ "system": "Li01", "kinds": ["zone"] }""");
            var faction = First(await Call("list_values", """{ "list": "faction", "text": "li_p" }"""));
            var encounter = First(await Call("list_values", """{ "list": "encounter_of_system", "system": "Li01" }"""));
            await Call("scene_place_patrol_route", $$"""
                {
                  "system": "Li01", "name": "my_patrol", "points": [[0, 0, 0], [15000, 0, 0], [15000, 0, 15000]],
                  "radius": 750, "encounters": [{ "encounter": "{{encounter}}", "level": 4, "chance": 0.4,
                  "factions": [{ "faction": "{{faction}}", "weight": 1 }] }]
                }
                """);
            var archetype = First(await Call("list_values", """{ "list": "solar_archetype", "text": "MISSION_SATELLITE" }"""));
            var ship = First(await Call("list_values", """{ "list": "ship" }"""));
            await Call("scene_place_wreck", $$"""
                {
                  "system": "Li01", "position": [8000, 500, 2000], "archetype": "{{archetype}}",
                  "ship_archetype": "{{ship}}", "loot": [{ "good": "commodity_gold", "count": 20 }],
                  "nickname": "my_wreck_1", "name": "Lost Freighter"
                }
                """);
            await Call("check");
            var saved = await Call("save");

            Assert.Contains(tools, t => t.Name == "save");
            Assert.EndsWith("Do not save until the user asks, unless the user told you to save.", client.ServerInstructions);
            Assert.True(status.GetProperty("can_edit").GetBoolean());
            Assert.True(saved.GetProperty("saved").GetBoolean());
            Assert.Equal(1, saved.GetProperty("number").GetInt32());

            // A second server on the same mod takes no edits, because the first one holds the lock of the mod.
            var second = await Server.Start(ws.ProfilePath);
            await using (second)
            {
                var secondStatus = await second.Client.CallToolAsync("studio_status", cancellationToken: Token);
                var refused = await second.Client.CallToolAsync("set_fields",
                    Arguments("""{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }"""), cancellationToken: Token);
                Assert.False(secondStatus.StructuredContent!.Value.GetProperty("can_edit").GetBoolean());
                Assert.True(refused.IsError);
                Assert.Equal("read_only", refused.StructuredContent!.Value.GetProperty("code").GetString());
            }
            var errors = await second.Errors;
            Assert.Contains("no edits: Another editor has mod 'act' open", errors);
            Assert.Contains(". The server reads the MCP messages on stdin.", errors);
            Assert.DoesNotContain(".. The server reads", errors);
        }

        var system = ws.ReadModPatch(SceneWorkspace.Li01Path);
        Assert.Contains("path_label = my_patrol, 1", system);
        Assert.Contains("path_label = my_patrol, 2", system);
        Assert.Contains("nickname = my_wreck_1", system);
        Assert.Contains("nickname = my_wreck_1_loadout", ws.ReadModPatch(BuilderWorkspace.Loadout("my_wreck_1_loadout").Path));
        Assert.False(File.Exists(RecoveryFile.PathOf(ws.ModFolder("act"))));

        var replay = await calls.ReplayAsync(copy);

        Assert.True(replay.Exit == 0, replay.Errors);
        Assert.Equal(TestWorkspace.Files(ws.ModFolder("act")), TestWorkspace.Files(copy.ModFolder("act")));
    }

    /// <summary>The story script of workflow 3: the script of the slot M02, with a ship build file. All names are
    /// invented.</summary>
    private const string Story = @"DATA\MISSIONS\M02\m02.ini";

    private const string StoryShips = @"DATA\MISSIONS\M02\npcships.ini";

    /// <summary>The start trigger starts the launch. The new part of workflow 3 starts after the launch. Nothing starts
    /// the end trigger yet.</summary>
    private const string StoryText = """
        [Mission]
        npc_ship_file = missions\m02\npcships.ini

        [Trigger]
        nickname = my_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = my_launch

        [Trigger]
        nickname = my_launch
        Cnd_SpaceEnter = Li02
        Act_SetTitle = 500100

        [Trigger]
        nickname = my_end
        Cnd_True = no_params
        Act_ChangeState = SUCCEED
        """;

    private const string StoryShipsText = """
        [NPCShipArch]
        nickname = my_escort_build
        ship_archetype = ship1
        loadout = lo_shared
        level = d1
        """;

    /// <summary>The builder workspace with the story script of workflow 3. The mod changes the script before the test,
    /// so <c>list_missions</c> gives the state changed, as workflow 3 step 1 asks for. The change goes through the
    /// mission commands and the save of the editor.</summary>
    private static TestWorkspace StoryWorkspace()
    {
        var ws = BuilderWorkspace.Create();
        ws.WriteBase(Story, StoryText);
        ws.WriteBase(StoryShips, StoryShipsText);
        // The save gate stops a ship build whose loadout has no engine, so the pirate gets a loadout with an engine.
        File.AppendAllText(Path.Combine(ws.Base, ListWorkspace.EquipmentPath), "\n[Engine]\nnickname = my_engine\n");
        File.AppendAllText(Path.Combine(ws.Base, ListWorkspace.ShipLoadoutsPath),
            "\n[Loadout]\nnickname = my_pirate_loadout\narchetype = ship1\nequip = my_engine\n");
        var session = ws.Session("act");
        var reply = new MissionJsonHost(session).Edit($$"""
            { "label": "Launch in Li01", "commands": [ { "kind": "set_condition_value",
              "trigger": {{Json($"trg:{Story}#my_launch")}}, "name": "system", "value": "Li01" } ] }
            """);
        Assert.False(reply.IsError, reply.Json);
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        session.Dispose();
        return ws;
    }

    /// <summary>
    /// The end-to-end test of workflow 3 of section 9.5 of the MCP1 design, through the server without a window (step
    /// 9 of MCP1-3). The agent adds a ship build, an NPC, a ship and an objective to a story script that the mod
    /// changes. It adds the pirate ambush in one edit_batch, links the reward to the end, runs the dry run, plans the
    /// start, checks and saves. A new script without a story slot gives a refused plan as data. The strict check
    /// refuses an action that starts a trigger that does not exist, and the inverse of an edit reverts it after a later
    /// edit. A new server reads the mission back. A replay of the recorded calls on a fresh copy exits 0 and gives the
    /// same files of the mod.
    /// </summary>
    [Fact]
    public async Task WorkflowThreeThroughTheServerSavesAndTheReplayGivesTheSameFiles()
    {
        using var ws = StoryWorkspace();
        using var copy = StoryWorkspace();
        var calls = new CallLog();
        const string test = @"DATA\MISSIONS\MY_TEST\my_test.ini";
        var script = Json($"msn:{Story}");
        string Trigger(string name) => Json($"trg:{Story}#{name}");

        await using (var server = await Server.Start(ws.ProfilePath))
        {
            var client = server.Client;
            Task<JsonElement> Call(string tool, string arguments = "{}") => calls.Call(client, tool, arguments);

            // 1. A new script has no story slot, so its plan cannot start. The plan is data, not an error.
            await Call("studio_status");
            await Call("mission_edit", $$"""{ "commands": [ { "kind": "create_script", "path": {{Json(test)}} } ] }""");
            var noSlot = (await Call("mission_start_plan", $$"""{ "script": {{Json($"msn:{test}")}} }"""))
                .GetProperty("plan");

            // Workflow 3, steps 1 and 2: pick the slot script that the mod changes, and read it.
            var scripts = (await Call("list_missions")).GetProperty("scripts").EnumerateArray().ToList();
            var picked = scripts.First(s => s.GetProperty("slot").ValueKind == JsonValueKind.String &&
                                            s.GetProperty("state").GetString() == "changed");
            await Call("get_mission", $$"""{ "script": {{script}}, "level": "triggers" }""");

            // 2. Steps 3 to 5 and the objective: the values come from list_values.
            var shipArch = First(await Call("list_values", """{ "list": "ship" }"""));
            var loadout = First(await Call("list_values", """{ "list": "loadout", "text": "my_pirate" }"""));
            var faction = First(await Call("list_values", """{ "list": "faction", "text": "li_p" }"""));
            var voice = First(await Call("list_values", """{ "list": "voice" }"""));
            var system = First(await Call("list_values", """{ "list": "system", "text": "Li01" }"""));
            var good = First(await Call("list_values", """{ "list": "equipment", "text": "commodity_gold" }"""));
            var build = await Call("mission_add_item", $$"""
                { "script": {{script}}, "kind": "ship_build", "nickname": "my_pirate_build",
                  "fields": { "ship_archetype": {{Json(shipArch)}}, "loadout": {{Json(loadout)}},
                  "state_graph": "FIGHTER", "level": "d5", "npc_class": ["unlawful", "d5"] } }
                """);
            await Call("mission_add_item", $$"""
                { "script": {{script}}, "kind": "npc", "nickname": "my_pirate_npc",
                  "fields": { "npc_ship_arch": "my_pirate_build", "affiliation": {{Json(faction)}},
                  "voice": {{Json(voice)}} } }
                """);
            await Call("mission_add_item", $$"""
                { "script": {{script}}, "kind": "ship", "nickname": "my_pirate",
                  "fields": { "npc": "my_pirate_npc", "system": {{Json(system)}}, "position": [8000, 0, -2000],
                  "label": ["my_pirates"] } }
                """);
            await Call("mission_add_item", $$"""
                { "script": {{script}}, "kind": "objective", "nickname": "my_goal",
                  "fields": { "type": { "texts": ["ids", "500101"] }, "state": "HIDDEN" } }
                """);

            // 3. Step 6: the two triggers, their actions, the start link from the launch and the loot in one batch.
            var batch = await Call("edit_batch", $$"""
                { "label": "Add the pirate ambush", "steps": [
                  { "tool": "mission_add_trigger", "arguments": { "script": {{script}}, "nickname": "my_ambush",
                    "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } },
                    "actions": [ { "key": "Act_SpawnShip", "values": { "ship": "my_pirate" } },
                                 { "key": "Act_ActTrig", "values": { "trigger": "my_reward" } } ],
                    "started_by": [ {{Trigger("my_launch")}} ] } },
                  { "tool": "mission_add_trigger", "arguments": { "script": {{script}}, "nickname": "my_reward",
                    "condition": { "key": "Cnd_Destroyed", "values": { "target": "my_pirate" } },
                    "actions": [ { "key": "Act_SpawnLoot", "values": { "loot": "my_crate" } } ] } },
                  { "tool": "mission_add_item", "arguments": { "script": {{script}}, "kind": "loot",
                    "nickname": "my_crate", "fields": { "archetype": {{Json(good)}}, "equip_amount": 1,
                    "rel_pos_obj": "my_pirate" } } } ] }
                """);

            // 4. The reward starts the end.
            await Call("mission_link_triggers",
                $$"""{ "from": {{Trigger("my_reward")}}, "to": "my_end", "kind": "start" }""");

            // 5. Step 7: the dry run. A timer of 3 seconds needs 90 heartbeats.
            var run = await Call("mission_dry_run", $$"""
                { "script": {{script}}, "inputs": [ { "kind": "start_at", "trigger": {{Trigger("my_launch")}} },
                  { "kind": "run_trigger", "trigger": {{Trigger("my_launch")}} }, { "kind": "heartbeats", "count": 100 },
                  { "kind": "event", "line": { "key": "Cnd_Destroyed", "values": { "target": "my_pirate" } } },
                  { "kind": "heartbeats", "count": 1 } ] }
                """);

            // 6. The slot script can start at the new trigger.
            var ready = (await Call("mission_start_plan",
                $$"""{ "script": {{script}}, "start": {{Trigger("my_ambush")}} }""")).GetProperty("plan");

            // 7. The strict check refuses a new error, and strict mode cannot go off.
            var nowhere = await calls.Error(client, "mission_add_action", $$"""
                { "trigger": {{Trigger("my_end")}},
                  "action": { "key": "Act_ActTrig", "values": { "trigger": "my_nowhere" } } }
                """, "new_errors");
            var lax = await calls.Error(client, "mission_edit", $$"""
                { "options": { "reject_new_errors": false }, "commands": [ { "kind": "set_condition_value",
                  "trigger": {{Trigger("my_ambush")}}, "name": "seconds", "value": 4 } ] }
                """, "invalid_argument");

            // 8. The inverse of an edit reverts it after a later edit of another trigger.
            string Timer(string trigger, int seconds) => $$"""
                { "trigger": {{Trigger(trigger)}}, "condition": { "key": "Cnd_Timer", "values": { "seconds": {{seconds}} } } }
                """;
            var wait = await Call("mission_set_condition", Timer("my_ambush", 5));
            await Call("mission_set_condition", Timer("my_end", 2));
            await Call("mission_edit", $$"""{ "commands": [ {{wait.GetProperty("inverse").GetRawText()}} ] }""");

            // 9. Step 8: check, then save. The scope mission gives the problems of the mission checks. The scope all
            // also gives the errors of the save gate, for example a ship build whose loadout has no engine.
            var check = await Call("check", $$"""{ "scope": "mission", "ref": {{script}} }""");
            var gate = await Call("check");
            var saved = await Call("save");

            Assert.Equal(("refused", "test_script", StartPlan.NoSlotReason),
                (Text(noSlot, "status"), Text(noSlot, "kind"), Text(noSlot, "reason")));
            Assert.Equal(($"msn:{Story}", "M02", "act"), (Text(picked, "ref"), Text(picked, "slot"), Text(picked, "mod_id")));
            Assert.Equal([$"sec:{StoryShips}#NPCShipArch/my_pirate_build"], Texts(build.GetProperty("adds")));
            Assert.Equal("Agent: Add the pirate ambush", Text(batch, "undo_label"));
            Assert.Contains($"trg:{Story}#my_reward", Texts(batch.GetProperty("adds")));
            var changes = run.GetProperty("steps").EnumerateArray()
                .SelectMany(s => s.GetProperty("changes").EnumerateArray())
                .Select(c => (Text(c, "kind"), c.TryGetProperty("trigger", out var t) ? t.GetString() : null)).ToList();
            Assert.Contains(("ran", $"trg:{Story}#my_ambush"), changes);
            Assert.Contains(("ran", $"trg:{Story}#my_reward"), changes);
            Assert.Contains(("ran", $"trg:{Story}#my_end"), changes);
            Assert.Equal("success", Text(run.GetProperty("state").GetProperty("end"), "kind"));
            Assert.Equal(("ready", "approximate"), (Text(ready, "status"), Text(ready, "kind")));
            Assert.Contains("my_nowhere", Text(nowhere, "message"));
            Assert.Equal("options.reject_new_errors", Text(lax, "field"));
            Assert.Equal(0, check.GetProperty("errors").GetInt32());
            Assert.Equal((0, false), (gate.GetProperty("errors").GetInt32(), gate.GetProperty("save_blocked").GetBoolean()));
            Assert.True(saved.GetProperty("saved").GetBoolean(), saved.GetRawText());
        }

        // The patch files have the triggers, the ship, the NPC, the objective, the loot and the build.
        var patch = ws.ReadModPatch(Story);
        foreach (var line in new[] { "nickname = my_ambush", "nickname = my_reward", "nickname = my_pirate",
                     "nickname = my_pirate_npc", "nickname = my_goal", "nickname = my_crate", "Act_ActTrig = my_ambush",
                     "Act_ActTrig = my_end", "Cnd_Timer = 3" })
            Assert.Contains(line, patch);
        Assert.DoesNotContain("my_nowhere", patch);
        Assert.Contains("nickname = my_pirate_build", ws.ReadModPatch(StoryShips));
        Assert.True(File.Exists(Path.Combine(ws.ModFolder("act"), "files", test)));
        Assert.False(File.Exists(RecoveryFile.PathOf(ws.ModFolder("act"))));

        // 10. A new server on the same mod reads the mission back.
        await using (var again = await Server.Start(ws.ProfilePath))
        {
            var mission = await again.Client.CallToolAsync("get_mission",
                Arguments($$"""{ "script": {{script}}, "level": "triggers" }"""), cancellationToken: Token);
            var trigger = await again.Client.CallToolAsync("get_trigger",
                Arguments($$"""{ "ref": {{Trigger("my_ambush")}} }"""), cancellationToken: Token);

            Assert.NotEqual(true, mission.IsError);
            Assert.Equal(["my_start", "my_launch", "my_end", "my_ambush", "my_reward"], mission.StructuredContent!.Value
                .GetProperty("triggers").GetProperty("rows").EnumerateArray().Select(t => Text(t, "nickname")));
            Assert.NotEqual(true, trigger.IsError);
            var lines = trigger.StructuredContent!.Value.GetProperty("lines").EnumerateArray().ToList();
            Assert.Equal(["nickname", "Cnd_Timer", "Act_SpawnShip", "Act_ActTrig"], lines.Select(l => Text(l, "key")));
            Assert.Equal("3", Text(lines[1].GetProperty("values"), "seconds"));
        }

        // 11. The replay of the recorded calls on a fresh copy gives the same files of the mod.
        var replay = await calls.ReplayAsync(copy);

        Assert.True(replay.Exit == 0, replay.Errors);
        Assert.Equal(TestWorkspace.Files(ws.ModFolder("act")), TestWorkspace.Files(copy.ModFolder("act")));
    }

    /// <summary>The new cutscene script of workflow 4 in the active mod <c>act</c>.</summary>
    private const string Intro = @"DATA\MODS\act\CUTSCENES\my_intro.thn";

    /// <summary>The workspace of workflow 4: the files of <see cref="ThnSessions"/>, and a costume file that
    /// <c>freelancer.ini</c> names, for the list <c>costume</c>. All names are invented.</summary>
    private static TestWorkspace CutsceneWorkspace()
    {
        var ws = ThnSessions.Workspace();
        ws.WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Data]\ncostumes = characters\\costumes.ini\n\n" +
                                            TestWorkspace.ResourcesSection);
        ws.WriteBase(@"DATA\CHARACTERS\costumes.ini", "[Costume]\nnickname = my_costume\nbody = my_body\nhead = my_head\n");
        return ws;
    }

    /// <summary>The entities and the events of a <c>get_cutscene</c> result as lines of text, to compare two
    /// reads.</summary>
    private static string[] CutsceneLines(JsonElement view) =>
    [
        $"duration {view.GetProperty("duration").GetDouble()}",
        .. view.GetProperty("entities").EnumerateArray().Select(e =>
            $"{Text(e, "type")} {Text(e, "name")} {(e.TryGetProperty("template", out var t) ? t.GetString() : null)}"),
        .. view.GetProperty("events").EnumerateArray().Select(e =>
            $"{e.GetProperty("time").GetDouble()} {Text(e, "type")} {string.Join(", ", Texts(e.GetProperty("targets")))}"),
    ];

    /// <summary>
    /// The end-to-end test of workflow 4 of section 9.5 of the MCP1 design, through the server without a window (step 7
    /// of MCP1-4). The agent makes a room cutscene of 8 s with a second camera, a character with a costume of the
    /// data and a marker. It adds a camera move with its end position and a cut to the second camera. A camera as the
    /// target of a motion gives <c>refused</c> with the types that fit, and a stale event reference gives
    /// <c>stale_revision</c>. The agent moves the cut, undoes a wrong duration, checks and saves. A new server reads
    /// the cutscene back. A replay of the recorded calls on a fresh copy exits 0 and gives the same files of the mod.
    /// The step <c>render_cutscene</c> needs the editor, so the manual check covers it.
    /// </summary>
    [Fact]
    public async Task WorkflowFourThroughTheServerSavesAndTheReplayGivesTheSameFiles()
    {
        using var ws = CutsceneWorkspace();
        using var copy = CutsceneWorkspace();
        var calls = new CallLog();
        var script = Json($"thn:{Intro}");
        string Event(int index) => $"thn:{Intro}#event/{index}";
        string[] read;

        await using (var server = await Server.Start(ws.ProfilePath))
        {
            var client = server.Client;
            Task<JsonElement> Call(string tool, string arguments = "{}") => calls.Call(client, tool, arguments);

            // 1. and 2. A new script of 8 s.
            var status = await Call("studio_status");
            var created = await Call("cutscene_create", """{ "name": "my_intro", "duration": 8 }""");

            // 3. A second camera, a character with a costume of the data, and a marker.
            var costume = First(await Call("list_values", """{ "list": "costume" }"""));
            var camera = await Call("cutscene_add_entity",
                $$"""{ "cutscene": {{script}}, "type": "CAMERA", "name": "my_cam_2", "position": [30, 10, 0] }""");
            var character = await Call("cutscene_add_entity", $$"""
                { "cutscene": {{script}}, "type": "DEFORMABLE", "name": "my_char", "template": {{Json(costume)}},
                  "position": [0, 0, -20] }
                """);
            await Call("cutscene_add_entity",
                $$"""{ "cutscene": {{script}}, "type": "MARKER", "name": "my_mark", "position": [0, 0, -25] }""");

            // 4. A camera move of 4 s, then the end position on the new event, with the guards of the event.
            var move = await Call("cutscene_add_event", $$"""
                { "cutscene": {{script}}, "type": "START_SPATIAL_PROP_ANIM", "time": 0, "targets": ["Camera_01"], "length": 4 }
                """);
            var end = await Call("set_fields", $$"""
                { "ref": {{Json(Event(1))}}, "expect_type": "START_SPATIAL_PROP_ANIM", "expect_time": 0,
                  "fields": { "props.spatialprops.pos": [0, 5, -10] } }
                """);

            // 5. The cut to the second camera at 4 s.
            var cut = await Call("cutscene_add_event",
                $$"""{ "cutscene": {{script}}, "type": "SET_CAMERA", "time": 4, "targets": ["Monitor", "my_cam_2"] }""");

            // 6. A camera does not play a motion. The error names the types that fit.
            var motion = await calls.Error(client, "cutscene_add_event",
                $$"""{ "cutscene": {{script}}, "type": "START_MOTION", "time": 1, "targets": ["my_cam_2"] }""", "refused");

            // 7. A guard with another time refuses the event reference, and nothing changes.
            var stale = await calls.Error(client, "cutscene_move_events", $$"""
                { "events": [ { "ref": {{Json(Event(2))}}, "expect_type": "SET_CAMERA", "expect_time": 3 } ], "time_delta": 0.5 }
                """, "stale_revision");

            // 8. The cut moves 0.5 s after the end of the camera move.
            var moved = await Call("cutscene_move_events", $$"""
                { "events": [ { "ref": {{Json(Event(2))}}, "expect_type": "SET_CAMERA", "expect_time": 4 } ], "time_delta": 0.5 }
                """);

            // 9. A wrong duration and its undo. W has the duration 8 again, and the move stays.
            await Call("cutscene_set_duration", $$"""{ "cutscene": {{script}}, "seconds": 3 }""");
            var undone = await Call("undo");
            var view = await Call("get_cutscene", $$"""{ "ref": {{script}} }""");

            // 10. Check, then save.
            var check = await Call("check", $$"""{ "scope": "cutscene", "ref": {{script}} }""");
            var saved = await Call("save");

            Assert.True(status.GetProperty("can_edit").GetBoolean());
            Assert.Equal([$"thn:{Intro}"], Texts(created.GetProperty("adds")));
            Assert.Equal([$"thn:{Intro}#entity/my_cam_2"], Texts(camera.GetProperty("adds")));
            Assert.Equal([$"thn:{Intro}#entity/my_char"], Texts(character.GetProperty("adds")));
            Assert.Empty(character.GetProperty("new_problems").EnumerateArray());
            Assert.Equal([Event(1)], Texts(move.GetProperty("adds")));
            Assert.Equal([Event(1)], Texts(end.GetProperty("changes")));
            Assert.Equal([Event(2)], Texts(cut.GetProperty("adds")));
            Assert.Equal("TargetType", Text(motion, "detail"));
            Assert.Equal("START_MOTION needs a DEFORMABLE or COMPOUND as target 1. The entity my_cam_2 is a CAMERA.",
                Text(motion, "message"));
            Assert.Equal(("RowChanged", "events[0].ref"), (Text(stale, "detail"), Text(stale, "field")));
            Assert.Equal([Event(2)], Texts(moved.GetProperty("changes")));
            Assert.Equal("Agent: Set duration", Text(undone, "label"));
            read = CutsceneLines(view);
            Assert.Equal([
                "duration 8",
                "SCENE Scene ", "MONITOR Monitor ", "CAMERA Camera_01 ", "CAMERA my_cam_2 ", $"DEFORMABLE my_char {costume}",
                "MARKER my_mark ",
                "0 SET_CAMERA Monitor, Camera_01", "0 START_SPATIAL_PROP_ANIM Camera_01", "4.5 SET_CAMERA Monitor, my_cam_2",
            ], read);
            var cameraMove = view.GetProperty("events")[1];
            Assert.Equal(4, cameraMove.GetProperty("length").GetDouble());
            Assert.Equal([0.0, 5, -10], cameraMove.GetProperty("fields").GetProperty("props.spatialprops.pos").EnumerateArray()
                .Select(v => v.GetDouble()));
            Assert.Equal(0, check.GetProperty("errors").GetInt32());
            Assert.Empty(check.GetProperty("problems").EnumerateArray());
            Assert.True(saved.GetProperty("saved").GetBoolean(), saved.GetRawText());
        }

        // The mod has the new script as a whole file in its cutscene folder, and no recovery file.
        Assert.True(File.Exists(Path.Combine(ws.ModFolder("act"), "files", Intro)));
        Assert.False(File.Exists(RecoveryFile.PathOf(ws.ModFolder("act"))));

        // 11. A new server on the same mod reads the cutscene back as the agent saved it.
        await using (var again = await Server.Start(ws.ProfilePath))
        {
            var view = await again.Client.CallToolAsync("get_cutscene", Arguments($$"""{ "ref": {{script}} }"""),
                cancellationToken: Token);

            Assert.NotEqual(true, view.IsError);
            Assert.Equal(read, CutsceneLines(view.StructuredContent!.Value));
        }

        // 12. The replay of the recorded calls on a fresh copy gives the same files of the mod.
        var replay = await calls.ReplayAsync(copy);

        Assert.True(replay.Exit == 0, replay.Errors);
        Assert.Equal(TestWorkspace.Files(ws.ModFolder("act")), TestWorkspace.Files(copy.ModFolder("act")));
    }

    /// <summary>The server makes a new cutscene and ends without a save and without the end of stdin. The recovery
    /// file then has the new script, and a new session finds it (section 13.2, item 6 of the MCP1 design, for a
    /// cutscene). The mod folder has no script file, because nothing saved.</summary>
    /// <remarks>The test waits for the file before it ends the process, as
    /// <see cref="AnEditStaysInTheRecoveryFileWhenTheServerEndsWithoutTheEndOfStdin"/> does.</remarks>
    [Fact]
    public async Task ANewCutsceneStaysInTheRecoveryFileWhenTheServerEndsWithoutASave()
    {
        using var ws = CutsceneWorkspace();
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));

        await using (var server = await Server.Start(ws.ProfilePath))
        {
            var created = await server.Client.CallToolAsync("cutscene_create",
                Arguments("""{ "name": "my_intro", "duration": 8 }"""), cancellationToken: Token);
            Assert.NotEqual(true, created.IsError);
            await Task.Delay(TimeSpan.FromSeconds(3), Token);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!File.Exists(recovery) && DateTime.UtcNow < deadline)
                await Task.Delay(100, Token);
            await server.KillAsync(ws.ModFolder("act"));
        }
        var session = ws.Session("act");

        Assert.True(File.Exists(recovery));
        Assert.False(File.Exists(Path.Combine(ws.ModFolder("act"), "files", Intro)));
        Assert.True(session.HasRecovery);
        Assert.Empty(session.RestoreRecovery());
        Assert.Equal([Intro], session.NewCutscenes);
        Assert.Equal(8, session.Cutscene(Intro)!.Duration);
    }

    [Fact]
    public async Task TheServerAnswersOnStdioAndTheReplayGivesTheSameResults()
    {
        using var ws = SceneWorkspace.Create();
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        var calls = new List<(string Tool, Dictionary<string, object?> Arguments, ToolOutcome Outcome)>();

        // The test starts the child process itself. On Windows, the stdio transport of the SDK starts a command
        // through cmd.exe, which splits a command path with a space, and the test folder can have one.
        using var server = Process.Start(new ProcessStartInfo(Tool)
        {
            ArgumentList = { "mcp", "serve", ws.ProfilePath, "--mod", "act", "--read-only" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var errors = server.StandardError.ReadToEndOnOwnThread();
        try
        {
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(server.StandardInput.BaseStream, server.StandardOutput.BaseStream),
                cancellationToken: Token);
            await using (client)
            {
                // The server opens the profile only to read, so it lists only the read-only tools.
                var tools = await client.ListToolsAsync(cancellationToken: Token);
                Assert.Equal(AgentTools.ReadOnly.Select(t => t.Name), tools.Select(t => t.Name));
                Assert.EndsWith("This server only reads the data. It changes no file.", client.ServerInstructions);

                async Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments)
                {
                    var result = await client.CallToolAsync(tool, arguments, cancellationToken: Token);
                    var content = result.StructuredContent!.Value;
                    calls.Add((tool, arguments, result.IsError == true
                        ? new ToolOutcome(ToolOutcome.Error, content.GetProperty("code").GetString(),
                            content.GetProperty("detail").GetString())
                        : new ToolOutcome(ToolOutcome.Ok)));
                    return content;
                }

                var status = await Call("studio_status", []);
                var systems = await Call("list_systems", []);
                var items = await Call("list_items", new() { ["system"] = "Li01", ["kinds"] = new[] { "zone" } });
                var missing = await Call("get_item", new() { ["ref"] = "obj:none" });
                var console = await Call("get_console", []);

                Assert.Equal("headless", status.GetProperty("host").GetString());
                Assert.False(status.GetProperty("can_edit").GetBoolean());
                Assert.Equal(2, systems.GetProperty("total").GetInt32());
                Assert.Equal("zone:Zone_ring", items.GetProperty("rows")[0].GetProperty("ref").GetString());
                Assert.Equal("not_found", missing.GetProperty("code").GetString());
                Assert.Equal("not_available", console.GetProperty("code").GetString());
            }
            server.StandardInput.Close();
            await server.ExitAsync(TimeSpan.FromSeconds(30), "oltool mcp serve did not end at the end of stdin.");
            Assert.Equal(0, server.ExitCode);
            Assert.Contains("read-only", await errors);
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
        }

        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
        Assert.False(Directory.Exists(Path.Combine(ws.ModFolder("act"), SaveTransaction.FolderName)));

        var log = Path.Combine(ws.Root, "calls.jsonl");
        File.WriteAllLines(log, calls.Select(c => new JsonObject
        {
            ["tool"] = c.Tool,
            ["arguments"] = JsonSerializer.SerializeToNode(c.Arguments),
            ["result"] = AuditLog.Outcome(c.Outcome),
        }.ToJsonString()));
        var replay = await RunAsync("mcp", "replay", ws.ProfilePath, "--mod", "act", log);

        Assert.True(replay.Exit == 0, replay.Errors);
        var lines = replay.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(calls.Select(c => c.Tool), lines.Select(l => (string)l["tool"]!));
        Assert.Equal("ok", (string?)lines[1]["result"]!["status"]);
        Assert.Equal(2, (int)lines[1]["content"]!["total"]!);

        File.WriteAllLines(log, [
            """{"tool": "studio_status", "arguments": {}}""",
            """{"tool": "get_item", "arguments": {"ref": "obj:Li01_01"}, "result": {"status": "error", "code": "not_found"}}""",
        ]);
        var differs = await RunAsync("mcp", "replay", ws.ProfilePath, "--mod", "act", log);
        File.WriteAllLines(log, ["""{"tool": "get_item", "arguments": {"ref": "obj:none"}}"""]);
        var unrecorded = await RunAsync("mcp", "replay", ws.ProfilePath, "--mod", "act", log);
        File.WriteAllLines(log, ["not json"]);
        var broken = await RunAsync("mcp", "replay", ws.ProfilePath, "--mod", "act", log);

        Assert.Equal(1, differs.Exit);
        Assert.Contains("Line 2: get_item gave ok, and the log has error not_found.", differs.Errors);
        Assert.Equal(1, unrecorded.Exit);
        Assert.Contains("Line 1: get_item gave error not_found", unrecorded.Errors);
        Assert.Equal(2, broken.Exit);
    }
}
