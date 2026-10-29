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
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.OltoolExportTests;

namespace OpenLancer.Tests;

/// <summary>
/// <c>oltool mcp</c> (section 6.2 and section 13, items 1.1, 2.6 and 3 of the MCP1 design). The tests cover the
/// snapshot of the tool list and the end-to-end tests of the server without a window. A test starts <c>oltool mcp serve</c> as a child
/// process through the stdio client of the SDK. It calls the tools, stops the server, and replays the recorded calls
/// with <c>oltool mcp replay</c>.
/// </summary>
public sealed class OltoolMcpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Tool => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oltool.exe" : "oltool");

    [Fact]
    public void TheToolListIsTheSnapshot()
    {
        var (exit, output, errors) = Run("mcp", "tools", "--json");

        Assert.True(exit == 0, errors);
        var snapshot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AgentTools.snapshot.json"));
        // When a tool changes on purpose, write the snapshot again: oltool mcp tools --json > AgentTools.snapshot.json.
        Assert.Equal(snapshot.ReplaceLineEndings("\n").Trim(), output.ReplaceLineEndings("\n").Trim());
        Assert.Equal(AgentTools.All.Select(t => t.Name),
            JsonNode.Parse(output)!["tools"]!.AsArray().Select(t => (string)t!["name"]!));
    }

    [Fact]
    public void TheTextToolListNamesEachTool()
    {
        var (exit, output, _) = Run("mcp", "tools");

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
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var errors = process.StandardError.ReadToEndAsync(Token);

        await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromMinutes(1), Token);
        Assert.Equal(2, process.ExitCode);
        Assert.Empty(await output);
        Assert.Contains("Do not use localhost.", await errors);
    }

    [Fact]
    public void ServeAndReplayCheckTheirArguments()
    {
        using var ws = SceneWorkspace.Create();

        var noMod = Run("mcp", "serve", ws.ProfilePath, "--read-only");
        var unknown = Run("mcp", "serve", ws.ProfilePath, "--mod", "none");
        var both = Run("mcp", "serve", ws.ProfilePath, "--mod", "act", "--restore-recovery", "--discard-recovery");
        var readOnly = Run("mcp", "serve", ws.ProfilePath, "--mod", "act", "--read-only", "--discard-recovery");
        var twice = Run("mcp", "serve", ws.ProfilePath, "--mod", "act", "--no-save", "--no-save");
        var replay = Run("mcp", "replay", ws.ProfilePath, "--mod", "act");

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
            errors = process.StandardError.ReadToEndAsync(Token);
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

        /// <summary>Ends the server without the end of its standard input, as a client that ends it at once does.</summary>
        public void Kill()
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(30));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    await Client.DisposeAsync();
                    process.StandardInput.Close();
                    Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "oltool mcp serve did not end at the end of stdin.");
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

    /// <summary>The test makes an edit through the server, waits 3 seconds and ends the process without the end of stdin
    /// (section 13.2, item 6 of the MCP1 design). The recovery file then has the edit, and a new session finds
    /// it.</summary>
    /// <remarks>The server writes the file 2 seconds after the edit. A cold process under the load of the parallel tests
    /// can need more time for its first write, so the test waits for the file before it ends the process. The file
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
            server.Kill();
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
        var calls = new List<(string Tool, string Arguments, ToolOutcome Outcome)>();

        await using (var server = await Server.Start(ws.ProfilePath))
        {
            var client = server.Client;

            async Task<JsonElement> Call(string tool, string arguments = "{}")
            {
                // The server takes at most 10 calls each second (section 8.4 of the MCP1 design).
                await Task.Delay(TimeSpan.FromMilliseconds(150), Token);
                var result = await client.CallToolAsync(tool, Arguments(arguments), cancellationToken: Token);
                var content = result.StructuredContent!.Value;
                calls.Add((tool, arguments, result.IsError == true
                    ? new ToolOutcome(ToolOutcome.Error, content.GetProperty("code").GetString(),
                        content.GetProperty("detail").GetString())
                    : new ToolOutcome(ToolOutcome.Ok)));
                Assert.True(result.IsError != true, $"{tool}: {content.GetRawText()}");
                return content;
            }

            string First(JsonElement page) => page.GetProperty("rows")[0].GetProperty("value").GetString()!;

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
            Assert.Contains("no edits: Another editor has mod 'act' open", await second.Errors);
        }

        var system = ws.ReadModPatch(SceneWorkspace.Li01Path);
        Assert.Contains("path_label = my_patrol, 1", system);
        Assert.Contains("path_label = my_patrol, 2", system);
        Assert.Contains("nickname = my_wreck_1", system);
        Assert.Contains("nickname = my_wreck_1_loadout", ws.ReadModPatch(BuilderWorkspace.Loadout("my_wreck_1_loadout").Path));
        Assert.False(File.Exists(RecoveryFile.PathOf(ws.ModFolder("act"))));

        var log = Path.Combine(copy.Root, "calls.jsonl");
        File.WriteAllLines(log, calls.Select(c => new JsonObject
        {
            ["tool"] = c.Tool,
            ["arguments"] = JsonNode.Parse(c.Arguments),
            ["result"] = AuditLog.Outcome(c.Outcome),
        }.ToJsonString()));
        var replay = Run("mcp", "replay", copy.ProfilePath, "--mod", "act", log);

        Assert.True(replay.Exit == 0, replay.Errors);
        Assert.Equal(TestWorkspace.Files(ws.ModFolder("act")), TestWorkspace.Files(copy.ModFolder("act")));
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
        var errors = server.StandardError.ReadToEndAsync(Token);
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
            Assert.True(server.WaitForExit(TimeSpan.FromSeconds(30)), "oltool mcp serve did not end at the end of stdin.");
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
        var replay = Run("mcp", "replay", ws.ProfilePath, "--mod", "act", log);

        Assert.True(replay.Exit == 0, replay.Errors);
        var lines = replay.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(calls.Select(c => c.Tool), lines.Select(l => (string)l["tool"]!));
        Assert.Equal("ok", (string?)lines[1]["result"]!["status"]);
        Assert.Equal(2, (int)lines[1]["content"]!["total"]!);

        File.WriteAllLines(log, [
            """{"tool": "studio_status", "arguments": {}}""",
            """{"tool": "get_item", "arguments": {"ref": "obj:Li01_01"}, "result": {"status": "error", "code": "not_found"}}""",
        ]);
        var differs = Run("mcp", "replay", ws.ProfilePath, "--mod", "act", log);
        File.WriteAllLines(log, ["""{"tool": "get_item", "arguments": {"ref": "obj:none"}}"""]);
        var unrecorded = Run("mcp", "replay", ws.ProfilePath, "--mod", "act", log);
        File.WriteAllLines(log, ["not json"]);
        var broken = Run("mcp", "replay", ws.ProfilePath, "--mod", "act", log);

        Assert.Equal(1, differs.Exit);
        Assert.Contains("Line 2: get_item gave ok, and the log has error not_found.", differs.Errors);
        Assert.Equal(1, unrecorded.Exit);
        Assert.Contains("Line 1: get_item gave error not_found", unrecorded.Errors);
        Assert.Equal(2, broken.Exit);
    }
}
