using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The HTTP host of the agent server of the editor (section 13.2, items 2 to 4 of the MCP1 design): the bind on
/// 127.0.0.1 also with the variables of ASP.NET Core in the environment, the token, the request checks, the fixed port,
/// the discovery file and the Console messages. The host runs in the test process. A session thread of a test host
/// takes the calls, as the UI thread of the editor does.
/// </summary>
[Collection(nameof(AgentServerTests))]
public sealed class AgentServerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A port that no program uses now.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A running server on a free port, its session thread, and an HTTP client.</summary>
    private sealed class Running : IAsyncDisposable
    {
        public Running(AgentLimits? limits = null)
        {
            Thread = SessionThread.Start(() => new TestAgentHost(null, AgentHostKind.Editor));
            var start = AgentServer.StartAsync(FreePort(), Thread.Queue, limits ?? new AgentLimits()).GetAwaiter().GetResult();
            Assert.Null(start.Problem);
            Server = start.Server!;
        }

        public SessionThread Thread { get; }

        public AgentServer Server { get; }

        public HttpClient Http { get; } = new();

        public string Url(string path = AgentServer.Endpoint) => $"http://127.0.0.1:{Server.Port}{path}";

        /// <summary>A request with the token, unless the test leaves it out.</summary>
        public HttpRequestMessage Request(HttpMethod method, string? path = null, string? token = "", string body = "{}")
        {
            var request = new HttpRequestMessage(method, Url(path ?? AgentServer.Endpoint));
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (token != null)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (token.Length == 0 ? Server.Token : token));
            return request;
        }

        public async Task<HttpStatusCode> Send(HttpRequestMessage request)
        {
            using var response = await Http.SendAsync(request, Token);
            Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            return response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Server.DisposeAsync();
            Thread.Dispose();
        }
    }

    [Fact]
    public async Task TheServerListensOnLoopbackOnlyAlsoWithTheVariablesOfAspNetCore()
    {
        var names = new[] { "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "Kestrel__Endpoints__X__Url", "URLS" };
        var old = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "0");
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__X__Url", "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable("URLS", "http://0.0.0.0:0");
            await using var running = new Running();

            Assert.Equal([$"http://127.0.0.1:{running.Server.Port}"], running.Server.Addresses);
            Assert.Equal(64, running.Server.Token.Length);
        }
        finally
        {
            foreach (var (name, value) in old)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task EachRequestNeedsTheTokenAndPassesTheChecks()
    {
        await using var running = new Running(new AgentLimits { RequestMegabytes = 1 });

        HttpRequestMessage With(string header, string value)
        {
            var request = running.Request(HttpMethod.Post);
            request.Headers.TryAddWithoutValidation(header, value);
            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await running.Send(running.Request(HttpMethod.Post, token: null)));
        Assert.Equal(HttpStatusCode.Unauthorized, await running.Send(running.Request(HttpMethod.Post, token: new string('0', 64))));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("Origin", "http://example.com")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("X-Forwarded-For", "10.0.0.1")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("Forwarded", "for=10.0.0.1")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("X-Real-IP", "10.0.0.1")));
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(With("Tailscale-User-Login", "someone")));
        var host = running.Request(HttpMethod.Post);
        host.Headers.Host = $"example.com:{running.Server.Port}";
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(host));
        var port = running.Request(HttpMethod.Post);
        port.Headers.Host = "127.0.0.1:1";
        Assert.Equal(HttpStatusCode.Forbidden, await running.Send(port));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await running.Send(running.Request(HttpMethod.Get)));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await running.Send(running.Request(HttpMethod.Delete)));
        Assert.Equal(HttpStatusCode.NotFound, await running.Send(running.Request(HttpMethod.Post, "/other")));
        Assert.Equal(HttpStatusCode.NotFound, await running.Send(running.Request(HttpMethod.Get, "/")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            await running.Send(running.Request(HttpMethod.Post, body: new string(' ', 1024 * 1024 + 1))));
        // A chunked body has no length in its headers, so Kestrel stops it at the limit while it reads it. Kestrel can
        // also close the connection while the client still sends. The call never reaches the tools.
        var chunked = running.Request(HttpMethod.Post, body: new string(' ', 1024 * 1024 + 1) +
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"studio_status"}}""");
        chunked.Headers.TransferEncodingChunked = true;
        chunked.Headers.Accept.ParseAdd("application/json");
        chunked.Headers.Accept.ParseAdd("text/event-stream");
        try
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await running.Send(chunked));
        }
        catch (HttpRequestException)
        {
            // The server closed the connection before the client read the status.
        }
        Assert.Null(running.Thread.Queue.LastCall);
        var localhost = running.Request(HttpMethod.Post, body:
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        localhost.Headers.Host = $"localhost:{running.Server.Port}";
        localhost.Headers.Accept.ParseAdd("application/json");
        localhost.Headers.Accept.ParseAdd("text/event-stream");
        Assert.Equal(HttpStatusCode.OK, await running.Send(localhost));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task AClientWithTheTokenCallsTheToolsOverHttp(string? protocol)
    {
        await using var running = new Running();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(running.Url()),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + running.Server.Token },
        });
        var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocol },
            cancellationToken: Token);
        await using (client)
        {
            var tools = await client.ListToolsAsync(cancellationToken: Token);
            var status = await client.CallToolAsync("studio_status", cancellationToken: Token);

            Assert.Equal(AgentTools.All.Count, tools.Count);
            Assert.Equal("editor", status.StructuredContent!.Value.GetProperty("host").GetString());
            Assert.StartsWith("Call studio_status first.", client.ServerInstructions);
        }
    }

    /// <summary>The token is a header of the request, so the audit log, which holds the arguments and the results,
    /// never holds it (section 8.7, item 3 of the MCP1 design).</summary>
    [Fact]
    public async Task TheAuditLogOfACallOverHttpHoldsNoToken()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "agent.jsonl");
        try
        {
            string token;
            using (var audit = new AuditLog(file))
            using (var thread = SessionThread.Start(() => new TestAgentHost(null, AgentHostKind.Editor), audit: audit))
            {
                var start = await AgentServer.StartAsync(FreePort(), thread.Queue, new AgentLimits());
                await using var server = start.Server!;
                token = server.Token;
                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://127.0.0.1:{server.Port}{AgentServer.Endpoint}"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                });
                var client = await McpClient.CreateAsync(transport, cancellationToken: Token);
                await using (client)
                {
                    await client.CallToolAsync("studio_status", cancellationToken: Token);
                    await client.CallToolAsync("list_systems", cancellationToken: Token);
                }
            }

            var text = File.ReadAllText(file);
            Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain("Bearer", text);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ASecondServerOnAPortInUseDoesNotStartAndTakesNoOtherPort()
    {
        await using var first = new Running();
        using var thread = SessionThread.Start(() => new TestAgentHost(null, AgentHostKind.Editor));

        var second = await AgentServer.StartAsync(first.Server.Port, thread.Queue, new AgentLimits());

        Assert.Null(second.Server);
        Assert.True(second.PortInUse);
        Assert.Equal($"Port {first.Server.Port} is in use.", second.Problem);
    }

    [Fact]
    public void OnlyAnAddressOf127001IsALoopbackAddress()
    {
        Assert.True(AgentServer.IsLoopbackAddress("http://127.0.0.1:47815"));
        Assert.False(AgentServer.IsLoopbackAddress("http://0.0.0.0:47815"));
        Assert.False(AgentServer.IsLoopbackAddress("http://[::]:47815"));
        Assert.False(AgentServer.IsLoopbackAddress("http://localhost:47815"));
    }
}

/// <summary>The editor side of the agent server: the controller with the discovery file and the Console, the waits of
/// the dispatcher, the token helper, the Connect text and the settings.</summary>
[Collection(nameof(AgentServerTests))]
public sealed class AgentControllerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    private string Agent => Path.Combine(folder, "agent");

    /// <summary>The discovery file of this test process.</summary>
    private string Discovery => AgentDiscovery.PathOf(Agent, Environment.ProcessId);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }

    [Fact]
    public void StartWritesTheDiscoveryFileAndStopDeletesIt()
    {
        var editor = new FakeAgentEditor();
        editor.Settings.Port = FreePort();
        var ui = new QueuedUIThread();
        using var controller = new AgentController(editor, ui, () => default, Discovery, folder);

        controller.Start();
        ui.RunUntil(() => controller.State == AgentServerState.On, "the start of the server");
        var info = AgentDiscovery.Read(Discovery)!;
        var headers = AgentDiscovery.Headers(Agent, $"http://127.0.0.1:{editor.Settings.Port}/mcp");
        controller.Stop();

        Assert.Equal(editor.Settings.Port, info.Port);
        Assert.Equal(Environment.ProcessId, info.ProcessId);
        Assert.Equal(64, info.Token.Length);
        Assert.Equal($$"""{"Authorization":"Bearer {{info.Token}}"}""", headers.Headers);
        Assert.False(File.Exists(Discovery));
        Assert.Equal(AgentServerState.Off, controller.State);
        Assert.Equal("Agent server: off", controller.StatusText);
        Assert.Equal([$"The agent server listens on port {editor.Settings.Port}.", "The agent server stopped."],
            editor.Log.Entries.Where(e => e.Source == AgentController.Source).Select(e => e.Message));
        Assert.Single(Directory.GetFiles(folder, "agent-*.jsonl"));
    }

    [Fact]
    public void APortInUseStopsTheStartAndTellsTheUser()
    {
        var editor = new FakeAgentEditor();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            editor.Settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var ui = new QueuedUIThread();
            using var controller = new AgentController(editor, ui, () => default, Discovery, folder);

            controller.Start();
            ui.RunUntil(() => controller.State != AgentServerState.Starting, "the end of the start");

            Assert.Equal(AgentServerState.PortInUse, controller.State);
            Assert.Equal($"Agent server: port {editor.Settings.Port} is in use", controller.StatusText);
            var entry = editor.Log.Entries.Single(e => e.Source == AgentController.Source);
            Assert.Equal(ConsoleSeverity.Error, entry.Severity);
            Assert.StartsWith($"Port {editor.Settings.Port} is in use, so the agent server did not start.", entry.Message);
            Assert.False(File.Exists(Discovery));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void TheTokenHelperGivesTheTokenOnlyToTheRunningServer()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var file = AgentDiscovery.PathOf(Agent, 42);
        DateTime? Running(int id) => id == 42 ? now.AddMilliseconds(300) : null;
        AgentDiscovery.Write(file, new AgentDiscoveryInfo(47815, "abc", 42, now, "test", "act"), Running);

        Assert.Equal("editor-42.json", Path.GetFileName(file));
        Assert.Equal("""{"Authorization":"Bearer abc"}""", AgentDiscovery.Headers(Agent, null, Running).Headers);
        Assert.Equal("""{"Authorization":"Bearer abc"}""",
            AgentDiscovery.Headers(Agent, "http://127.0.0.1:47815/mcp", Running).Headers);
        // A client can connect to localhost on the IPv6 address, where another program can listen on the same port.
        var localhost = AgentDiscovery.Headers(Agent, "http://localhost:47815/mcp", Running);
        Assert.Null(localhost.Headers);
        Assert.EndsWith("Use the address http://127.0.0.1:<port>/mcp in .mcp.json. Do not use localhost.", localhost.Problem);
        Assert.Null(AgentDiscovery.Headers(Agent, "http://[::1]:47815/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Agent, "http://127.0.0.1:47816/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Agent, "http://example.com:47815/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Agent, "https://127.0.0.1:47815/mcp", Running).Headers);
        Assert.Null(AgentDiscovery.Headers(Agent, null, _ => null).Headers);
        Assert.Null(AgentDiscovery.Headers(Agent, null, _ => now.AddMinutes(5)).Headers);
        Assert.Equal("No agent server of the editor runs. Start it in the Agent menu of the editor.",
            AgentDiscovery.Headers(Path.Combine(folder, "none"), null, Running).Problem);

        AgentDiscovery.Delete(file, "other");
        Assert.True(File.Exists(file));
        AgentDiscovery.Delete(file, "abc");
        Assert.False(File.Exists(file));
    }

    /// <summary>Each editor has its own discovery file. So a second editor on another port does not hide the first
    /// one, and the stop of one editor does not delete the file of the other.</summary>
    [Fact]
    public void EachEditorHasItsOwnDiscoveryFileAndTheHelperPicksThePort()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        DateTime? Running(int id) => id is 41 or 42 ? now : null;
        AgentDiscovery.Write(AgentDiscovery.PathOf(Agent, 43), new AgentDiscoveryInfo(47817, "crashed", 43, now, "c", null),
            _ => now);
        AgentDiscovery.Write(AgentDiscovery.PathOf(Agent, 41), new AgentDiscoveryInfo(47815, "first", 41, now, "a", null),
            _ => now);
        AgentDiscovery.Write(AgentDiscovery.PathOf(Agent, 42), new AgentDiscoveryInfo(47816, "second", 42, now, "b", null),
            _ => now);

        Assert.Equal("""{"Authorization":"Bearer first"}""",
            AgentDiscovery.Headers(Agent, "http://127.0.0.1:47815/mcp", Running).Headers);
        Assert.Equal("""{"Authorization":"Bearer second"}""",
            AgentDiscovery.Headers(Agent, "http://127.0.0.1:47816/mcp", Running).Headers);
        Assert.Equal("No agent server of the editor runs on port 47817. Use the port of the agent settings of the editor " +
                     "in .mcp.json.", AgentDiscovery.Headers(Agent, "http://127.0.0.1:47817/mcp", Running).Problem);
        Assert.StartsWith("2 editors run an agent server.", AgentDiscovery.Headers(Agent, null, Running).Problem);

        AgentDiscovery.Delete(AgentDiscovery.PathOf(Agent, 41), "first");
        Assert.Equal("""{"Authorization":"Bearer second"}""", AgentDiscovery.Headers(Agent, null, Running).Headers);
        // A write deletes the file of an editor that does not run, and keeps the file of an editor that runs.
        Assert.True(File.Exists(AgentDiscovery.PathOf(Agent, 43)));
        AgentDiscovery.Write(AgentDiscovery.PathOf(Agent, 41), new AgentDiscoveryInfo(47815, "again", 41, now, "a", null),
            Running);
        Assert.False(File.Exists(AgentDiscovery.PathOf(Agent, 43)));
        Assert.True(File.Exists(AgentDiscovery.PathOf(Agent, 42)));
    }

    /// <summary>A client of the protocol revision 2025-11-25 on a stateless server gives its name only in the
    /// initialize request. The status bar then shows the agent without its name.</summary>
    [Fact]
    public async Task ACallWithoutTheNameOfItsClientShowsTheAgentInTheStatusBar()
    {
        var editor = new FakeAgentEditor();
        editor.Settings.Port = FreePort();
        var ui = new QueuedUIThread();
        using var controller = new AgentController(editor, ui, () => default, Discovery, folder);
        controller.Start();
        ui.RunUntil(() => controller.State == AgentServerState.On, "the start of the server");
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{editor.Settings.Port}{AgentServer.Endpoint}")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"studio_status","arguments":{}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + AgentDiscovery.Read(Discovery)!.Token);
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var call = http.SendAsync(request, TestContext.Current.CancellationToken);
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!call.IsCompleted && DateTime.UtcNow < end)
        {
            ui.RunQueued();
            controller.Frame();
            Thread.Sleep(5);
        }
        using var response = await call;
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"host\":\"editor\"", body.Replace(" ", ""));
        Assert.Equal($"Agent: {AgentController.UnnamedClient}, 0 edits", controller.StatusText);
        controller.Paused = true;
        Assert.Equal("Agent paused", controller.StatusText);
        controller.Stop();
    }

    [Fact]
    public void TheEditorHostGivesTheReasonOfTheOpenProfile()
    {
        using var ws = SceneWorkspace.Create();
        var editor = new FakeAgentEditor { Edits = ws.Session("act") };
        var host = new EditorAgentHost(editor);

        var open = AgentCalls.Ok(host, "studio_status");
        editor.EditReason = "The user did not allow the edits of this mod.";
        var blocked = AgentCalls.Ok(host, "studio_status");
        editor.Edits = null;
        var none = AgentCalls.Ok(host, "studio_status");

        Assert.Equal(("editor", "test", true), ((string?)open["host"], (string?)open["profile"], (bool)open["can_edit"]!));
        Assert.Null(open["reason"]);
        Assert.False((bool)blocked["can_edit"]!);
        Assert.Equal("The user did not allow the edits of this mod.", (string?)blocked["reason"]);
        Assert.False((bool)none["can_edit"]!);
        Assert.Equal("No profile is open. Open a profile in the editor, then try again.", (string?)none["reason"]);
        Assert.Equal("Test Name", host.NameText(1));
    }

    /// <summary>A mod in the OpenLancer repo whose edits the user did not allow gives read_only with the repo-mod text of
    /// section 10 of the MCP1 design. The text tells where the user allows the edits. A reason of the session comes
    /// first.</summary>
    [Fact]
    public void AnEditOfARepoModThatTheUserDidNotAllowGivesTheRepoModText()
    {
        using var ws = SceneWorkspace.Create();
        const string repo = "The mod 'act' is part of the OpenLancer repo, so its files are tracked files.";
        var editor = new FakeAgentEditor { Edits = ws.Session("act"), RepoReason = repo };
        var host = new EditorAgentHost(editor);
        const string move = """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""";

        var waits = AgentCalls.Error(host, "set_fields", move);
        editor.EditReason = "Another editor has the mod act open.";
        var locked = AgentCalls.Error(host, "set_fields", move);

        Assert.Equal(("read_only", repo + " Allow edits of this mod in the editor first."), (waits.Code, waits.Message));
        Assert.Equal(("read_only", "Another editor has the mod act open."), (locked.Code, locked.Message));
        Assert.False(editor.Edits!.CanUndo);
    }

    /// <summary>The save of the editor host runs the save of the editor (section 7.6 of the MCP1 design). The validation
    /// of the save is the tail of the call. The tail waits for the result of its save, also from a later save that
    /// replaced it, and removes its handler.</summary>
    [Fact]
    public async Task TheEditorSaveWaitsForTheValidationOfItsSave()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var editor = new FakeAgentEditor { Edits = session, Saving = () => SaveOutcome.Of(session.Save(), 3) };
        var host = new EditorAgentHost(editor);
        AgentCalls.Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");

        var step = AgentTools.Run(host, "save", null);
        var tail = step.Tail!(Token, ToolProgress.None);
        editor.Validate(new ValidationState(2, false, new ValidationResult(2, [], null)));
        var waited = tail.IsCompleted;
        var problem = Diagnostic.Error("mods/act/mod.json", "The mod has a problem.");
        editor.Validate(new ValidationState(3, false, new ValidationResult(3, [problem, problem with
        {
            Severity = DiagnosticSeverity.Warning,
        }], null)));
        var result = await tail.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Null(step.Result);
        Assert.False(waited);
        Assert.False(result.IsError, result.Text);
        Assert.Equal(3, (int)result.Content["number"]!);
        var validation = result.Content["validation"]!;
        Assert.Equal((3, 1, 1), ((int)validation["save"]!, (int)validation["errors"]!, (int)validation["warnings"]!));
        Assert.Equal("The mod has a problem.", (string?)validation["problems"]![0]!["message"]);
        Assert.Equal(0, editor.ValidatedHandlers);
    }

    /// <summary>A validation that ended during the save, for example because oltool did not start, ends the tail at
    /// once. A save that changed no file has no tail. A tail that the client or the time limit stops still tells that
    /// the save wrote its files.</summary>
    [Fact]
    public async Task TheEditorSaveTellsTheSaveAlsoWithoutTheResultOfTheValidation()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var number = 1;
        var editor = new FakeAgentEditor { Edits = session, Saving = () => SaveOutcome.Of(session.Save(), number) };
        var host = new EditorAgentHost(editor);
        AgentCalls.Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");
        editor.Validation = new ValidationState(1, false, new ValidationResult(1, null, "oltool did not start."));

        var done = await AgentTools.Run(host, "save", null).Tail!(Token, ToolProgress.None).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, editor.ValidatedHandlers);
        number = 0;
        var unchanged = AgentTools.Run(host, "save", null);
        number = 2;
        AgentCalls.Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [4, 5, 6] } }""");
        using var cancel = new CancellationTokenSource();
        var stopped = AgentTools.Run(host, "save", null).Tail!(cancel.Token, ToolProgress.None);
        Assert.Equal(1, editor.ValidatedHandlers);
        await cancel.CancelAsync();
        var late = await stopped.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal("oltool did not start.", (string?)done.Content["validation"]!["failure"]);
        Assert.Null(unchanged.Tail);
        Assert.Null(unchanged.Result!.Content["validation"]);
        Assert.True((bool)late.Content["saved"]!);
        Assert.Equal("The validation did not end before the time limit of the call. The Console of the editor shows its " +
                     "result.", (string?)late.Content["validation"]!["failure"]);
        Assert.Equal(0, editor.ValidatedHandlers);
    }

    /// <summary>The errors of the save of the editor host. The gate gives <c>save_blocked</c>. A change of the files of
    /// the mod outside the editor gives <c>changed_on_disk</c>, and the editor asks the user. A save that did not start
    /// gives <c>refused</c>.</summary>
    [Fact]
    public void TheEditorSaveGivesTheErrorsOfTheSaveOfTheEditor()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var editor = new FakeAgentEditor { Edits = session };
        var host = new EditorAgentHost(editor);
        AgentCalls.Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "loadout": "lo_missing" } }""");
        editor.Saving = () => SaveOutcome.Blocked with { Result = session.GateRefusal() };
        var blocked = AgentCalls.Error(host, "save");
        editor.Saving = () => SaveOutcome.Of(new SaveResult(false, [], [], [],
            [Path.Combine(ws.ModFolder("act"), "patches", "file.ini")]), 0);
        var changed = AgentCalls.Error(host, "save");
        editor.Saving = null;
        var none = AgentCalls.Error(host, "save");

        Assert.Equal(("save_blocked", "Gate"), (blocked.Code, blocked.Detail));
        Assert.Equal(("changed_on_disk", "Files of the mod changed outside the editor: patches\\file.ini. The editor asks " +
                                         "the user to replace them or to keep them. The save wrote nothing."),
            (changed.Code, changed.Message));
        Assert.Equal(("refused", "SaveNotStarted"), (none.Code, none.Detail));
    }

    /// <summary>While an export of the Export dialog or of the agent runs, the save of the editor host gives
    /// <c>busy</c>. It does not start the save of the editor, because the export reads the saved files.</summary>
    [Fact]
    public void TheEditorSaveGivesBusyWhileAnExportRuns()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var saves = 0;
        var editor = new FakeAgentEditor
        {
            Edits = session,
            Saving = () => SaveOutcome.Of(session.Save(), ++saves),
            ExportBlock = "An export runs now. Wait until it ends.",
        };
        var host = new EditorAgentHost(editor);
        AgentCalls.Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");

        var busy = AgentCalls.Error(host, "save");

        Assert.Equal(("busy", "ExportRuns", "An export runs now. Wait until it ends."), busy);
        Assert.Equal(0, saves);
        Assert.True(session.IsDirty);
    }

    /// <summary>A later save stops the validation of an earlier save, and its validation replaces it. So the tail of
    /// the earlier save ends with the validation of the later save.</summary>
    [Fact]
    public async Task TheEditorSaveTakesTheValidationOfALaterSave()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var editor = new FakeAgentEditor { Edits = session, Saving = () => SaveOutcome.Of(session.Save(), 3) };
        var host = new EditorAgentHost(editor);
        AgentCalls.Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");

        var tail = AgentTools.Run(host, "save", null).Tail!(Token, ToolProgress.None);
        editor.Validate(new ValidationState(4, true, null));
        var running = tail.IsCompleted;
        editor.Validate(new ValidationState(4, false, new ValidationResult(4, [], null)));
        var result = await tail.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.False(running);
        Assert.Equal(3, (int)result.Content["number"]!);
        Assert.Equal(4, (int)result.Content["validation"]!["save"]!);
        Assert.Equal(0, editor.ValidatedHandlers);
    }

    /// <summary>An export of the Export dialog stops an export of the agent in the editor host. The host shares the
    /// export of the controller, so an export of an earlier start of the server also counts (review finding R12 of
    /// MCP1-2).</summary>
    [Fact]
    public void TheEditorHostWaitsForTheExportDialogAndKeepsTheExportOfTheController()
    {
        var editor = new FakeAgentEditor { ExportBlock = "An export runs now. Wait until it ends." };
        editor.Settings.Port = FreePort();
        using var controller = new AgentController(editor, new QueuedUIThread(), () => default, Discovery, folder);
        var host = new EditorAgentHost(editor, controller.Exports);

        var dialog = host.ExportBusy;
        editor.ExportBlock = null;
        var none = host.ExportBusy;
        var run = controller.Exports.Begin()!;
        var agent = host.ExportBusy;
        var second = controller.Exports.Begin();
        run.Cancel();

        Assert.Same(controller.Exports, host.Exports);
        Assert.Equal("An export runs now. Wait until it ends.", dialog);
        Assert.Null(none);
        Assert.Equal("An export runs now. Wait until it ends.", agent);
        Assert.Null(second);
        Assert.False(controller.Exports.Running);
    }

    [Fact]
    public async Task ACallWaitsWhileTheUserEditsAndGivesBusyAfterTheLimit()
    {
        var clock = new FakeClock();
        var editing = true;
        using var dispatcher = new AgentDispatcher(new EditorAgentHost(new FakeAgentEditor()), () => new EditorActivity(editing, false,
            false, false), null, clock);

        var call = dispatcher.Queue.CallAsync("studio_status", null, null, TestContext.Current.CancellationToken);
        Assert.False(dispatcher.Frame());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(dispatcher.Frame());
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var next = dispatcher.Queue.CallAsync("studio_status", null, null, TestContext.Current.CancellationToken);
        editing = false;
        Assert.True(dispatcher.Frame());

        Assert.Equal("busy", (string?)result.Content["code"]);
        Assert.StartsWith("The user edits a field or drags an item now.", (string?)result.Content["message"]);
        var status = await next.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("system:Li01", (string?)status.Content["view"]!["system"]);
        Assert.Equal("No profile is open. Open a profile in the editor, then try again.", (string?)status.Content["reason"]);
    }

    [Fact]
    public void EachActivityHasItsWaitReason()
    {
        Assert.Null(AgentDispatcher.WaitReason(default));
        Assert.Equal("A dialog of the editor is open.", AgentDispatcher.WaitReason(new EditorActivity(false, true, false, false)));
        Assert.Equal("The editor loads the game data now.", AgentDispatcher.WaitReason(new EditorActivity(false, false, true, false)));
        Assert.Equal("An edit of the user runs now.", AgentDispatcher.WaitReason(new EditorActivity(false, false, false, true)));
        Assert.Equal("The user renames an item in the Hierarchy now.",
            AgentDispatcher.WaitReason(new EditorActivity(false, false, false, false, Renaming: true)));
    }

    /// <summary>An agent call waits while the rename field of the Hierarchy is open. The rename of the field applies to
    /// the item of its start, so an agent edit must not change that item first (section 5.5, item 3 of the MCP1
    /// design).</summary>
    [Fact]
    public async Task ACallWaitsWhileTheRenameFieldIsOpen()
    {
        var renaming = true;
        using var dispatcher = new AgentDispatcher(new EditorAgentHost(new FakeAgentEditor()),
            () => new EditorActivity(false, false, false, false, renaming), null, new FakeClock());

        var call = dispatcher.Queue.CallAsync("studio_status", null, null, TestContext.Current.CancellationToken);
        Assert.False(dispatcher.Frame());
        renaming = false;
        Assert.True(dispatcher.Frame());

        Assert.False((await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsError);
    }

    /// <summary>A state of a view counts only when the view drew in this frame or in the frame before. An agent call runs
    /// before the panels draw, so it reads the draw of the frame before. A view that does not draw any more, for example
    /// a hidden tab, has no open gesture.</summary>
    [Fact]
    public void AViewCountsOnlyWhileItDraws()
    {
        var frame = 1;
        var drawn = new OpenLancer.Editor.Ui.DrawnFrame(() => frame);

        Assert.False(drawn.Recent);
        frame = 5;
        drawn.Drew();
        Assert.True(drawn.Recent);
        frame = 6;
        Assert.True(drawn.Recent);
        frame = 7;
        Assert.False(drawn.Recent);
    }

    /// <summary>A press of the left button on a node or a pin of the mission graph lasts until the release, also when the
    /// mouse leaves the node. A drag of nodes or of a link runs only during it. A click on the background is not a press.
    /// A link drag counts in the draw that runs it.</summary>
    [Fact]
    public void AGraphGestureLastsUntilTheRelease()
    {
        var gesture = new GraphGesture(() => 1);
        gesture.Drew();

        gesture.Press(clicked: true, overItem: false, down: true);
        var background = gesture.Open(false);
        gesture.Press(clicked: true, overItem: true, down: true);
        var pressed = gesture.Open(false);
        gesture.Press(clicked: false, overItem: false, down: true);
        var dragged = gesture.Open(false);
        gesture.Press(clicked: false, overItem: true, down: false);
        var released = gesture.Open(false);
        gesture.DraggedLink();
        var link = gesture.Open(false);
        gesture.Drew();

        Assert.Equal([false, true, true, false, true], [background, pressed, dragged, released, link]);
        Assert.False(gesture.Open(false));
        Assert.True(gesture.Open(true));
    }

    /// <summary>
    /// An agent call waits while a gesture of the graph of the mission studio is open, and runs after it ends (section
    /// 5.5, item 3 of the MCP1 design, step 2 of MCP1-3). The gestures are a rename of a node, a drag of a node and a drag
    /// of a link. The test uses the gesture state of the graph with a fake frame count. It runs the frames as the editor
    /// runs them: the call before the panels, then the draws of the panels. A rename in a graph that does not draw any
    /// more does not keep the call waiting.
    /// </summary>
    [Fact]
    public async Task ACallWaitsWhileAGraphGestureIsOpenAndRunsAfterIt()
    {
        var frames = new GestureFrames();
        var graph = new GraphGesture(() => frames.Now);
        var nodeRename = false;
        using var dispatcher = frames.Dispatcher(() => new MissionGestures(false, graph.Open(nodeRename), false, false, false));

        // The user renames a node of the graph, then presses Enter.
        nodeRename = true;
        graph.Drew();
        var rename = frames.Call(dispatcher);
        Assert.False(frames.Step(dispatcher, graph.Drew));
        Assert.False(frames.Step(dispatcher, () =>
        {
            nodeRename = false;
            graph.Drew();
        }));
        Assert.True(frames.Step(dispatcher));
        Assert.True(await GestureFrames.Ran(rename));

        // The user renames a node, then shows another tab. The graph does not draw any more.
        nodeRename = true;
        graph.Drew();
        var hidden = frames.Call(dispatcher);
        Assert.False(frames.Step(dispatcher));
        Assert.True(frames.Step(dispatcher));
        Assert.True(await GestureFrames.Ran(hidden));
        nodeRename = false;

        // The user presses the button on a node, drags it off the node and lets the button go.
        graph.Drew();
        graph.Press(clicked: true, overItem: true, down: true);
        var drag = frames.Call(dispatcher);
        Assert.False(frames.Step(dispatcher, () =>
        {
            graph.Drew();
            graph.Press(clicked: false, overItem: false, down: true);
        }));
        Assert.False(frames.Step(dispatcher, () =>
        {
            graph.Drew();
            graph.Press(clicked: false, overItem: false, down: false);
        }));
        Assert.True(frames.Step(dispatcher, graph.Drew));
        Assert.True(await GestureFrames.Ran(drag));

        // The user drags a link from a pin, and drops it.
        graph.DraggedLink();
        var link = frames.Call(dispatcher);
        Assert.False(frames.Step(dispatcher, () =>
        {
            graph.Drew();
            graph.DraggedLink();
        }));
        Assert.False(frames.Step(dispatcher, graph.Drew));
        Assert.True(frames.Step(dispatcher, graph.Drew));
        Assert.True(await GestureFrames.Ran(link));

        const string editing = "The user edits a field or drags an item now.";
        Assert.Equal([editing, editing, null, editing, null, editing, editing, null, editing, editing, null], frames.Reasons);
    }

    /// <summary>
    /// An agent call waits while a popup of the mission studio is open (section 5.5, item 3 of the MCP1 design, step 2 of
    /// MCP1-3): the rename popup of a row of a mission in the Hierarchy, quick add of the Mission page and the rename
    /// popup of a prefab in the Assets panel. The rename popup of the Hierarchy gives the wait of a rename, the others
    /// give the wait of an edit. The test uses the popups of the editor with a fake frame count. Only ImGui draws a
    /// popup, so each popup here stays in a tab that does not draw any more, and the call then runs.
    /// </summary>
    [Fact]
    public async Task ACallWaitsWhileAPopupOfTheMissionStudioIsOpen()
    {
        var frames = new GestureFrames();
        var treeRename = new OpenLancer.Editor.Ui.RenamePopup("##mission-rename", () => frames.Now);
        var pageQuickAdd = new QuickAdd(() => frames.Now);
        var prefabRename = new OpenLancer.Editor.Ui.RenamePopup("##prefab-rename", () => frames.Now);
        using var dispatcher = frames.Dispatcher(() =>
            new MissionGestures(treeRename.IsOpen, false, false, pageQuickAdd.IsOpen, prefabRename.IsOpen));
        Action[] opens =
        [
            () => treeRename.Open("ol_wave"),
            () => pageQuickAdd.Open(new QuickAddContext(null, null, QuickAddFilter.All), System.Numerics.Vector2.Zero),
            () => prefabRename.Open("ol_gate"),
        ];

        foreach (var open in opens)
        {
            open();
            var call = frames.Call(dispatcher);
            Assert.False(frames.Step(dispatcher));
            Assert.True(frames.Step(dispatcher));
            Assert.True(await GestureFrames.Ran(call));
        }

        const string editing = "The user edits a field or drags an item now.";
        Assert.Equal(["The user renames an item in the Hierarchy now.", null, editing, null, editing, null], frames.Reasons);
    }

    /// <summary>The frames of the editor for the tests of the mission gestures: a fake frame count, and the wait reason of
    /// each frame.</summary>
    private sealed class GestureFrames
    {
        private Func<EditorActivity> activity = () => default;

        public int Now { get; private set; } = 1;

        public List<string?> Reasons { get; } = [];

        /// <summary>A dispatcher that waits for the gestures.</summary>
        public AgentDispatcher Dispatcher(Func<MissionGestures> gestures)
        {
            activity = () => gestures().AddTo(default);
            return new AgentDispatcher(new EditorAgentHost(new FakeAgentEditor()), activity, null, new FakeClock());
        }

        /// <summary>Puts a call into the queue.</summary>
        public Task<ToolResult> Call(AgentDispatcher dispatcher) =>
            dispatcher.Queue.CallAsync("studio_status", null, null, TestContext.Current.CancellationToken);

        /// <summary>One frame of the editor: the agent call before the panels, then the draws of the panels.</summary>
        /// <returns>True when the call ran.</returns>
        public bool Step(AgentDispatcher dispatcher, Action? panels = null)
        {
            Now++;
            Reasons.Add(AgentDispatcher.WaitReason(activity()));
            var ran = dispatcher.Frame();
            panels?.Invoke();
            return ran;
        }

        /// <summary>True when the call ran without an error.</summary>
        public static async Task<bool> Ran(Task<ToolResult> call) =>
            !(await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).IsError;
    }

    [Fact]
    public void TheEditorHostGivesTheConsoleWithItsFilters()
    {
        var editor = new FakeAgentEditor();
        editor.Log.Add(ConsoleSeverity.Info, "Editor", "Opened.");
        editor.Log.Add(ConsoleSeverity.Warning, AgentController.Source, "A warning.");
        editor.Log.Add(ConsoleSeverity.Error, "Mods", "An error.");
        var host = new EditorAgentHost(editor);

        var all = host.Console(new ConsoleFilter(null, AgentSeverity.Info, false, null));
        var warnings = host.Console(new ConsoleFilter(null, AgentSeverity.Warning, false, null));
        var agent = host.Console(new ConsoleFilter("agent", AgentSeverity.Info, false, null));
        var since = host.Console(new ConsoleFilter(null, AgentSeverity.Info, false, all[1].Sequence));

        Assert.Equal(["Opened.", "A warning.", "An error."], all.Select(e => e.Message));
        Assert.Equal([AgentSeverity.Warning, AgentSeverity.Error], warnings.Select(e => e.Severity));
        Assert.Equal(["A warning."], agent.Select(e => e.Message));
        Assert.Equal(["An error."], since.Select(e => e.Message));
        Assert.Equal(OpenLancer.Data.OriginKind.OpenLancer, all[0].Origin);
    }

    [Fact]
    public void TheConnectTextHasThePortAndTheTokenHelper()
    {
        var text = System.Text.Json.Nodes.JsonNode.Parse(AgentMenu.McpJson(47816, @"D:\Some Folder\oltool.exe"))!;
        var server = text["mcpServers"]!["openlancer"]!;

        Assert.Equal("http", (string?)server["type"]);
        Assert.Equal("http://127.0.0.1:47816/mcp", (string?)server["url"]);
        Assert.Equal("\"D:\\Some Folder\\oltool.exe\" mcp token", (string?)server["headersHelper"]);
    }

    [Fact]
    public void TheAgentSettingsKeepTheirValuesInRange()
    {
        var settings = EditorSettings.Parse("""
            { "Agent": { "Port": 0, "StartWithEditor": true, "HiddenGroups": ["Missions", "Studio", "Unknown"],
                         "Limits": { "MaxListRows": 99999, "CallsPerSecond": 0 } } }
            """, DateTime.UtcNow);

        Assert.Equal(AgentSettings.DefaultPort, settings.Agent.Port);
        Assert.True(settings.Agent.StartWithEditor);
        Assert.Equal([ToolGroup.Missions], settings.Agent.HiddenToolGroups);
        Assert.Equal(1000, settings.Agent.Limits.MaxListRows);
        Assert.Equal(1, settings.Agent.Limits.CallsPerSecond);
        settings.Agent.SetHidden(ToolGroup.Missions, false);
        settings.Agent.SetHidden(ToolGroup.Cutscenes, true);
        Assert.Equal([ToolGroup.Cutscenes], settings.Agent.HiddenToolGroups);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
