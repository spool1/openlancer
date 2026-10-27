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
/// <c>oltool mcp</c> (section 6.2 and section 13, items 1.1 and 3 of the MCP1 design): the snapshot of the tool list,
/// and the end-to-end test of the server without a window. The test starts <c>oltool mcp serve</c> as a child process
/// through the stdio client of the SDK, calls the query tools, stops the server, and replays the recorded calls with
/// <c>oltool mcp replay</c>.
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
    public void ServeNeedsTheModAndReadOnlyInThisBuild()
    {
        using var ws = SceneWorkspace.Create();

        var noMod = Run("mcp", "serve", ws.ProfilePath, "--read-only");
        var noReadOnly = Run("mcp", "serve", ws.ProfilePath, "--mod", "act");
        var unknown = Run("mcp", "serve", ws.ProfilePath, "--mod", "none", "--read-only");
        var flag = Run("mcp", "serve", ws.ProfilePath, "--mod", "act", "--read-only", "--no-save");

        Assert.Equal(2, noMod.Exit);
        Assert.Equal(2, noReadOnly.Exit);
        Assert.Contains("only reads the data. Add --read-only.", noReadOnly.Errors);
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("has no mod 'none'", unknown.Errors);
        Assert.Equal(2, flag.Exit);
        Assert.Empty(noReadOnly.Output);
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
