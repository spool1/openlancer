using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The SDK binding and the server without a window in the test process (section 13.2, items 5 and 7 of the MCP1
/// design): an MCP client and the server talk through two pipes. The calls of the client run one after the other on
/// the session thread, a cancel removes a call that did not start, and the tools and the resources have the form of
/// the protocol.
/// </summary>
public sealed class McpServerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A server on a session thread and a client that talks to it through pipes.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task server;
        private readonly Pipe toServer = new();
        private readonly Pipe toClient = new();

        public Connection(AgentHost host)
        {
            Thread = SessionThread.Start(() => host);
            server = McpServe.ServeAsync(Thread, toServer.Reader.AsStream(), toClient.Writer.AsStream(), stop.Token);
        }

        public SessionThread Thread { get; }

        /// <summary>A client of the SDK on the pipes.</summary>
        public Task<McpClient> Client() => McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: Token);

        /// <summary>The pipes as text, for a test that writes the JSON-RPC lines itself.</summary>
        public (StreamWriter Writer, StreamReader Reader) Raw() =>
            (new StreamWriter(toServer.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" },
                new StreamReader(toClient.Reader.AsStream()));

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try
            {
                await server.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                // The server stops with the token.
            }
            Thread.Dispose();
            stop.Dispose();
        }
    }

    [Fact]
    public async Task TheToolListHasTheTableWithSchemasAndHints()
    {
        await using var connection = new Connection(new TestAgentHost(null));
        var client = await connection.Client();

        var tools = await client.ListToolsAsync(cancellationToken: Token);

        Assert.Equal(AgentTools.All.Select(t => t.Name), tools.Select(t => t.Name));
        var status = tools[0].ProtocolTool;
        Assert.Equal("Get the status of the studio", status.Title);
        Assert.True(status.Annotations!.ReadOnlyHint);
        Assert.False(status.Annotations.DestructiveHint);
        Assert.False(status.Annotations.OpenWorldHint);
        Assert.Equal("object", status.OutputSchema!.Value.GetProperty("type").GetString());
        Assert.Equal(McpBinding.ServerName, client.ServerInfo.Name);
        Assert.StartsWith("Call studio_status first.", client.ServerInstructions);
    }

    [Fact]
    public async Task ACallGivesTheJsonAsStructuredContentAndAsText()
    {
        await using var connection = new Connection(new TestAgentHost(null));
        var client = await connection.Client();

        var status = await client.CallToolAsync("studio_status", cancellationToken: Token);
        var refused = await client.CallToolAsync("list_systems", new Dictionary<string, object?> { ["limit"] = 2 },
            cancellationToken: Token);

        Assert.NotEqual(true, status.IsError);
        Assert.Equal("headless", status.StructuredContent!.Value.GetProperty("host").GetString());
        var text = Assert.IsType<TextContentBlock>(Assert.Single(status.Content));
        Assert.Equal(status.StructuredContent.Value.GetRawText(), JsonDocument.Parse(text.Text).RootElement.GetRawText());
        Assert.True(refused.IsError);
        Assert.Equal("not_available", refused.StructuredContent!.Value.GetProperty("code").GetString());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("launch_rocket", cancellationToken: Token).AsTask());
    }

    /// <summary>A host that only reads lists only the read-only tools and gets the instruction that the server only
    /// reads (section 6.2 of the MCP1 design). A call of an edit tool is then a protocol error, as a call of an unknown
    /// tool is (review finding R10 of MCP1-2).</summary>
    [Fact]
    public async Task AHostThatOnlyReadsDoesNotListAnEditToolAndRefusesItsCall()
    {
        using var ws = SceneWorkspace.Create();
        await using var connection = new Connection(HeadlessHost.OpenReadOnly(ws.ProfilePath, "act"));
        var client = await connection.Client();

        var tools = await client.ListToolsAsync(cancellationToken: Token);

        Assert.DoesNotContain(tools, t => t.Name == "set_fields");
        Assert.Equal(AgentTools.ReadOnly.Select(t => t.Name), tools.Select(t => t.Name));
        Assert.EndsWith("This server only reads the data. It changes no file.", client.ServerInstructions);
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("set_fields",
            new Dictionary<string, object?> { ["ref"] = "obj:Li01_01", ["fields"] = new Dictionary<string, object?> { ["pos"] = 1 } },
            cancellationToken: Token).AsTask());
    }

    /// <summary>A host with edit tools lists them and gets the instructions of the edits. An edit call gives the result
    /// of the command layer as structured content.</summary>
    [Fact]
    public async Task AnEditCallGivesTheResultOfTheCommandLayer()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        await using var connection = new Connection(new TestAgentHost(session));
        var client = await connection.Client();

        var tools = await client.ListToolsAsync(cancellationToken: Token);
        var result = await client.CallToolAsync("set_fields", new Dictionary<string, object?>
        {
            ["ref"] = "obj:Li01_01",
            ["fields"] = new Dictionary<string, object?> { ["pos"] = new[] { 1, 2, 3 } },
        }, cancellationToken: Token);

        Assert.Contains(tools, t => t.Name == "set_fields" && t.ProtocolTool.Annotations!.DestructiveHint == true);
        Assert.Contains("Use preview: true for a large edit or a delete.", client.ServerInstructions);
        Assert.DoesNotContain("only reads", client.ServerInstructions);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal("applied", result.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal("Agent: Set pos", result.StructuredContent.Value.GetProperty("undo_label").GetString());
    }

    [Fact]
    public async Task TheResourcesGiveTheListsAndTheGuides()
    {
        await using var connection = new Connection(new TestAgentHost(null));
        var client = await connection.Client();

        var resources = await client.ListResourcesAsync(cancellationToken: Token);
        var lists = await client.ReadResourceAsync("openlancer://catalog/lists", cancellationToken: Token);
        var refs = await client.ReadResourceAsync("openlancer://guide/refs", cancellationToken: Token);

        Assert.Equal(["openlancer://catalog/lists", "openlancer://guide/rules", "openlancer://guide/refs"],
            resources.Select(r => r.Uri));
        var catalog = JsonNode.Parse(Assert.IsType<TextResourceContents>(Assert.Single(lists.Contents)).Text)!;
        Assert.Equal(59, catalog["lists"]!.AsArray().Count);
        Assert.Contains(catalog["lists"]!.AsArray(), l => (string?)l!["id"] == "object_of_system" && (bool)l["one_system"]!);
        Assert.Contains("thn:<install path>#event/<index>", Assert.IsType<TextResourceContents>(Assert.Single(refs.Contents)).Text);
        await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync("openlancer://none", cancellationToken: Token).AsTask());
    }

    [Fact]
    public async Task CallsAtTheSameTimeRunOneAfterTheOtherOnTheSessionThread()
    {
        var threads = new ConcurrentBag<int>();
        var inside = 0;
        var overlap = false;
        var host = new TestAgentHost(null)
        {
            Probe = () =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                    overlap = true;
                threads.Add(Environment.CurrentManagedThreadId);
                Thread.Sleep(20);
                Interlocked.Decrement(ref inside);
            },
        };
        await using var connection = new Connection(host);
        var client = await connection.Client();

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.CallToolAsync("studio_status", cancellationToken: Token).AsTask()));

        Assert.All(results, r => Assert.NotEqual(true, r.IsError));
        Assert.False(overlap);
        Assert.Equal(10, threads.Count);
        Assert.Single(threads.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, threads.First());
    }

    /// <summary>The client of the SDK sends no cancel notification, so the test writes the JSON-RPC lines of a client
    /// that does, as Claude Code does (section 4, item 4 of the MCP1 design).</summary>
    [Fact]
    public async Task ACancelNotificationRemovesACallThatDidNotStart()
    {
        using var release = new ManualResetEventSlim();
        var runs = 0;
        var host = new TestAgentHost(null)
        {
            Probe = () =>
            {
                Interlocked.Increment(ref runs);
                release.Wait(TimeSpan.FromSeconds(10));
            },
        };
        await using var connection = new Connection(host);
        var (writer, reader) = connection.Raw();

        await writer.WriteLineAsync(Request(1, "initialize",
            """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}"""));
        Assert.Equal(1, (int)JsonNode.Parse((await reader.ReadLineAsync(Token))!)!["id"]!);
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await writer.WriteLineAsync(Request(2, "tools/call", """{"name":"studio_status"}"""));
        await WaitUntil(() => Volatile.Read(ref runs) == 1);
        await writer.WriteLineAsync(Request(3, "tools/call", """{"name":"studio_status"}"""));
        await WaitUntil(() => connection.Thread.Queue.Waiting == 1);
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""");

        await WaitUntil(() => connection.Thread.Queue.Waiting == 0);
        release.Set();
        var answer = JsonNode.Parse((await reader.ReadLineAsync(Token))!)!;
        Assert.Equal(2, (int)answer["id"]!);
        Assert.Equal("headless", (string?)answer["result"]!["structuredContent"]!["host"]);
        await Task.Delay(100, Token);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    /// <summary>A call whose request has a progress token gets <c>notifications/progress</c> of its tail (section 5.3 of
    /// the MCP1 design), for example of an export. Each notification tells the step of the export.</summary>
    [Fact]
    public async Task AnExportSendsProgressNotificationsToAClientThatAsksForThem()
    {
        using var game = new ExportGame();
        var (_, session) = AgentPublishToolTests.Ready(game);
        using var _ = session;
        await using var connection = new Connection(new TestAgentHost(session));
        var client = await connection.Client();
        var reports = new ConcurrentQueue<ProgressNotificationValue>();

        var result = await client.CallToolAsync("export_mod",
            new Dictionary<string, object?> { ["formats"] = new[] { "drop_in" } }, new Reports(reports),
            cancellationToken: Token);
        await WaitUntil(() => reports.Count == 4);

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("succeeded").GetBoolean());
        // The server sends the notifications in their order. The client of the SDK can give them to the progress in
        // another order, and the progress value tells the order.
        var sorted = reports.OrderBy(r => r.Progress).ToList();
        Assert.Equal([0f, 1f, 2f, 3f], sorted.Select(r => r.Progress));
        Assert.All(sorted, r => Assert.Equal(3f, r.Total));
        Assert.Equal(["The export checks the mod.", "The export writes the packages.",
            "The export records the packages in the publish history.", null], sorted.Select(r => r.Message));
    }

    /// <summary>Keeps each progress notification of a call, on the thread that gets it.</summary>
    private sealed class Reports(ConcurrentQueue<ProgressNotificationValue> reports) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => reports.Enqueue(value);
    }

    private static string Request(int id, string method, string parameters) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""";

    [Fact]
    public void TheHeadlessHostOpensTheProfileOnlyToReadAndWritesNoFile()
    {
        using var ws = SceneWorkspace.Create();
        var before = TestWorkspace.Files(ws.ModFolder("act"));

        using (var host = HeadlessHost.OpenReadOnly(ws.ProfilePath, "act"))
        using (var editor = ws.Session("act"))
        {
            var status = AgentCalls.Ok(host, "studio_status");
            Assert.False((bool)status["can_edit"]!);
            Assert.Equal("The server without a window opened the mod act only to read (--read-only). It takes no edits.",
                (string?)status["reason"]);
            Assert.True(editor.CanEdit);
            Assert.Null(host.RepoReason);
        }

        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
        Assert.False(Directory.Exists(Path.Combine(ws.ModFolder("act"), SaveTransaction.FolderName)));
    }

    [Fact]
    public void TheHeadlessHostRunsATargetCheckInItsProcess()
    {
        using var ws = SceneWorkspace.Create();
        using var host = HeadlessHost.OpenReadOnly(ws.ProfilePath, "act");

        var check = AgentCalls.Ok(host, "check", """{ "target": "vanilla-1.1" }""");

        Assert.Equal("target", (string?)check["scope"]);
        // The synthetic base folder holds no game, so the check refuses the target with a problem.
        Assert.Equal(1, (int)check["errors"]!);
        Assert.StartsWith("The target check reads the saved files of the mods.", (string?)check["note"]);
    }

    [Fact]
    public void ATextDllThatDoesNotLoadGivesNoNames()
    {
        using var ws = SceneWorkspace.Create();
        using var host = HeadlessHost.OpenReadOnly(ws.ProfilePath, "act");

        // The synthetic base folder holds text that is not a DLL.
        Assert.Equal("", host.NameText(65537));
        Assert.Equal("", host.InfoText(65537));
        Assert.Equal(2, (int)AgentCalls.Ok(host, "list_systems")["total"]!);
    }

    [Fact]
    public async Task AClosedQueueCancelsEachNewCall()
    {
        var queue = new ToolQueue(new TestAgentHost(null));
        var waiting = queue.CallAsync("studio_status", null, null, Token);

        queue.Dispose();
        var late = queue.CallAsync("studio_status", null, null, Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
    }

    [Fact]
    public void TheHeadlessHostNeverEditsAModOfTheRepo()
    {
        using var ws = SceneWorkspace.Create();
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "");

        using var host = HeadlessHost.OpenReadOnly(ws.ProfilePath, "act");

        Assert.Equal("The mod act is part of the OpenLancer repo. The server without a window cannot change it. Use the " +
                     "agent server of the editor.", host.RepoReason);
        Assert.NotNull(host.NoEditReason);
    }

    [Fact]
    public void AReadOnlySessionTakesNoLockAndFindsNoRecovery()
    {
        using var ws = SceneWorkspace.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(RecoveryFile.PathOf(ws.ModFolder("act")))!);
        File.WriteAllText(RecoveryFile.PathOf(ws.ModFolder("act")), "x");

        using var session = EditSession.OpenReadOnly(ModProfile.Load(ws.ProfilePath), "act", TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator, "Only to read.");
        using var editor = ws.Session("act");

        Assert.Equal("Only to read.", session.ReadOnlyReason);
        Assert.Equal("act", session.ActiveMod!.Id);
        Assert.False(session.HasRecovery);
        Assert.False(session.HoldsLock);
        Assert.True(editor.HoldsLock);
        Assert.Equal("x", File.ReadAllText(RecoveryFile.PathOf(ws.ModFolder("act"))));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "The condition did not come true in 10 seconds.");
            await Task.Delay(10, Token);
        }
    }
}
