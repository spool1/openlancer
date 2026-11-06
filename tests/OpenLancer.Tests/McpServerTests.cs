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
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using OpenLancer.Mcp;
using OpenLancer.Runtime.Missions;
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

        /// <param name="clock">The clock of the queue, or null for the clock of the system.</param>
        public Connection(AgentHost host, TimeProvider? clock = null)
        {
            Thread = SessionThread.Start(() => host, clock);
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
    /// reads (section 6.2 of the MCP1 design). It lists no prompt of an edit workflow. A call of an edit tool is then a protocol error, as a call of an unknown
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
        Assert.Empty(await client.ListPromptsAsync(cancellationToken: Token));
        await Assert.ThrowsAsync<McpProtocolException>(() => client.GetPromptAsync("two_trigger_mission",
            new Dictionary<string, object?> { ["script"] = "msn:x.ini" }, cancellationToken: Token).AsTask());
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

    /// <summary>The tool list gives mission_edit with the definitions of the mission batch at the root of its input
    /// schema. A preview through the client gives the status previewed, the inverse and the same revision (step 1 of
    /// MCP1-3).</summary>
    [Fact]
    public async Task AMissionEditPreviewGivesItsResultAndKeepsTheRevision()
    {
        using var m = MissionEdits.Create();
        await using var connection = new Connection(new TestAgentHost(m.Session));
        var client = await connection.Client();

        var tools = await client.ListToolsAsync(cancellationToken: Token);
        var result = await client.CallToolAsync("mission_edit", new Dictionary<string, object?>
        {
            ["commands"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["kind"] = "set_condition_value",
                    ["trigger"] = $"trg:{MissionEdits.Script}#ol_wave",
                    ["name"] = "seconds",
                    ["value"] = 9,
                },
            },
            ["preview"] = true,
        }, cancellationToken: Token);

        var schema = tools.Single(t => t.Name == "mission_edit").ProtocolTool.InputSchema;
        Assert.True(schema.GetProperty("$defs").TryGetProperty("line.Cnd_Timer", out _));
        Assert.Equal("#/$defs/command", schema.GetProperty("properties").GetProperty("commands").GetProperty("items")
            .GetProperty("$ref").GetString());
        Assert.NotEqual(true, result.IsError);
        var content = result.StructuredContent!.Value;
        Assert.Equal("previewed", content.GetProperty("status").GetString());
        Assert.Equal("0", content.GetProperty("rev").GetString());
        Assert.Equal("command_group", content.GetProperty("inverse").GetProperty("kind").GetString());
        Assert.Equal(0, m.Session.Revision);
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public async Task TheResourcesGiveTheListsAndTheGuides()
    {
        await using var connection = new Connection(new TestAgentHost(null));
        var client = await connection.Client();

        var resources = await client.ListResourcesAsync(cancellationToken: Token);
        var lists = await client.ReadResourceAsync("openlancer://catalog/lists", cancellationToken: Token);
        var refs = await client.ReadResourceAsync("openlancer://guide/refs", cancellationToken: Token);

        Assert.Equal([
            "openlancer://schema/mission-names", "openlancer://schema/mission-sections",
            "openlancer://schema/objlist-directives", "openlancer://catalog/prefabs", "openlancer://catalog/lists",
            "openlancer://catalog/thn-events", "openlancer://guide/rules", "openlancer://guide/refs",
        ], resources.Select(r => r.Uri));
        Assert.Equal(["application/json", "application/json", "application/json", "application/json", "application/json",
            "application/json", "text/plain", "text/plain"], resources.Select(r => r.MimeType));
        var catalog = JsonNode.Parse(Assert.IsType<TextResourceContents>(Assert.Single(lists.Contents)).Text)!;
        Assert.Equal(60, catalog["lists"]!.AsArray().Count);
        Assert.Contains(catalog["lists"]!.AsArray(), l => (string?)l!["id"] == "object_of_system" && (bool)l["one_system"]!);
        Assert.Contains(catalog["lists"]!.AsArray(), l => (string?)l!["id"] == "costume");
        var guide = Assert.IsType<TextResourceContents>(Assert.Single(refs.Contents)).Text;
        Assert.Contains("thn:<install path>#event/<index>", guide);
        // The catalog of the prefabs gives the references of both forms.
        Assert.Contains("prefab:builtin:<id>", guide);
        Assert.Contains("prefab:mod:<id>", guide);
        await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync("openlancer://none", cancellationToken: Token).AsTask());
    }

    /// <summary>The resources of the schema of the missions (section 9.3 of the MCP1 design): the 129 conditions and
    /// actions with the marks of the mission runtime, the 12 section types and the ship build, and the 23 orders. They
    /// need no profile.</summary>
    [Fact]
    public async Task TheMissionResourcesGiveTheSchemaOfTheMissions()
    {
        await using var connection = new Connection(new TestAgentHost(null));
        var client = await connection.Client();

        var names = await ReadJson(client, "openlancer://schema/mission-names");
        var sections = await ReadJson(client, "openlancer://schema/mission-sections");
        var directives = await ReadJson(client, "openlancer://schema/objlist-directives");

        var schema = MissionSchema.Retail;
        var rows = names["names"]!.AsArray();
        Assert.Equal(129, rows.Count);
        Assert.Equal(schema.Conditions.Concat(schema.Actions).Select(n => n.Name), rows.Select(r => (string?)r!["name"]));
        Assert.All(rows, r => Assert.Equal(Snake(EngineCoverage.Instance.Of((string)r!["name"]!)), (string?)r["mark"]));
        Assert.Contains(rows, r => (string?)r!["mark"] == "no_effect");
        var timer = rows.Single(r => (string?)r!["name"] == "Cnd_Timer")!;
        Assert.Equal(("condition", "seconds"), ((string?)timer["kind"], (string?)timer["values"]![0]!["name"]));
        Assert.Contains(names["events"]!.AsArray(), e => (string?)e!["id"] == (string?)timer["group"]);
        var spawn = rows.Single(r => (string?)r!["name"] == "Act_SpawnShip")!;
        Assert.Contains(names["groups"]!.AsArray(), g => (string?)g!["id"] == (string?)spawn["group"]);
        Assert.DoesNotContain("base_count", names.ToJsonString());

        var types = sections["sections"]!.AsArray();
        Assert.Equal(12, types.Count);
        Assert.Equal(schema.Sections.Select(t => t.Type), types.Select(t => (string?)t!["type"]));
        string? Kind(string type) => (string?)types.Single(t => (string?)t!["type"] == type)!["kind"];
        Assert.Equal(["ship", "formation", "npc", "loot", "order_list", "random_encounter", null, null],
            new[] { "MsnShip", "MsnFormation", "NPC", "MsnLoot", "ObjList", "MsnRandEnc", "Trigger", "Mission" }.Select(Kind));
        var ship = types.Single(t => (string?)t!["type"] == "MsnShip")!["keys"]!.AsArray();
        var label = ship.Single(k => (string?)k!["key"] == "label")!;
        Assert.True((bool)label["repeats"]!);
        var position = ship.Single(k => (string?)k!["key"] == "position")!;
        Assert.Equal((false, 3), ((bool)position["repeats"]!, (int)position["values"]![0]!["width"]!));
        Assert.True((bool)types.Single(t => (string?)t!["type"] == "ObjList")!["directives"]!);
        var build = sections["ship_build"]!;
        Assert.Equal(("NPCShipArch", "ship_build"), ((string?)build["type"], (string?)build["kind"]));
        Assert.Contains(build["keys"]!.AsArray(), k => (string?)k!["key"] == "npc_class");

        var orders = directives["directives"]!.AsArray();
        Assert.Equal(23, orders.Count);
        Assert.Equal(schema.Directives.Select(n => n.Name), orders.Select(r => (string?)r!["name"]));
        Assert.All(orders, r => Assert.Equal("directive", (string?)r!["kind"]));
    }

    /// <summary>The catalog of the prefabs (section 9.3 of the MCP1 design) gives the built-in prefabs, the prefab
    /// assets of the mods, the templates of Create, the builders and the prop groups of the profile. Without a profile,
    /// it gives the built-in prefabs and the rule data only.</summary>
    [Fact]
    public async Task TheCatalogOfThePrefabsGivesThePrefabsTheTemplatesTheBuildersAndThePropGroups()
    {
        using var m = MissionEdits.Create(modFiles: new Dictionary<string, string>
        {
            [@"prefabs\missions\ol_gate.ini"] = PrefabInstanceTests.Gate,
            [@"props\ol_props.ini"] = "[PropGroup]\nnickname = ol_props\nformat = 1\n[Object]\nmember = ol_member\narchetype = ol_arch\n",
        });
        await using (var connection = new Connection(new TestAgentHost(m.Session)))
        {
            var client = await connection.Client();
            var catalog = await ReadJson(client, "openlancer://catalog/prefabs");

            var prefabs = catalog["prefabs"]!.AsArray().Select(p => (string?)p!["ref"]).ToList();
            Assert.Equal(BuiltinPrefabs.Ids.Select(id => "prefab:builtin:" + id).Append("prefab:mod:ol_gate"), prefabs);
            var gate = catalog["prefabs"]!.AsArray().Single(p => (string?)p!["ref"] == "prefab:mod:ol_gate")!;
            Assert.Equal("act", (string?)gate["mod_id"]);
            Assert.Contains(gate["fields"]!.AsArray(), f => (string?)f!["id"] == "formation");
            Assert.Equal(["group:ol_props"], catalog["prop_groups"]!.AsArray().Select(g => (string?)g!["ref"]));
            AssertRuleData(catalog);
        }

        await using (var connection = new Connection(new TestAgentHost(null)))
        {
            var client = await connection.Client();
            var catalog = await ReadJson(client, "openlancer://catalog/prefabs");

            Assert.Equal(17, catalog["prefabs"]!.AsArray().Count);
            Assert.All(catalog["prefabs"]!.AsArray(), p => Assert.StartsWith("prefab:builtin:", (string?)p!["ref"]));
            Assert.Empty(catalog["prop_groups"]!.AsArray());
            AssertRuleData(catalog);
        }

        static void AssertRuleData(JsonNode catalog)
        {
            Assert.Equal(SceneEditTools.ObjectTemplates, catalog["scene_templates"]!.AsArray().Select(t => (string?)t!["id"]));
            var station = catalog["scene_templates"]![0]!;
            Assert.Equal(("docking_sphere", "station"), ((string?)station["needs"], (string?)station["types"]![0]));
            Assert.Equal(["scene_place_wreck", "scene_place_patrol_route"],
                catalog["builders"]!.AsArray().Select(b => (string?)b!["tool"]));
            Assert.Equal(AgentTexts.Tool("scene_place_wreck").Description, (string?)catalog["builders"]![0]!["description"]);
        }
    }

    /// <summary>The catalog reads the session, so its read waits in the queue of the calls and runs on the session
    /// thread (section 5.5 of the MCP1 design). A read of the schema needs no session and does not wait.</summary>
    [Fact]
    public async Task AReadOfTheCatalogWaitsForTheSessionThread()
    {
        using var release = new ManualResetEventSlim();
        using var m = MissionEdits.Create();
        var runs = 0;
        var host = new TestAgentHost(m.Session)
        {
            Probe = () =>
            {
                Interlocked.Increment(ref runs);
                release.Wait(TimeSpan.FromSeconds(10));
            },
        };
        await using var connection = new Connection(host);
        var client = await connection.Client();

        var call = client.CallToolAsync("studio_status", cancellationToken: Token).AsTask();
        await WaitUntil(() => Volatile.Read(ref runs) == 1);
        var catalog = client.ReadResourceAsync("openlancer://catalog/prefabs", cancellationToken: Token).AsTask();
        await WaitUntil(() => connection.Thread.Queue.Waiting == 1);
        var names = await ReadJson(client, "openlancer://schema/mission-names");

        Assert.False(catalog.IsCompleted);
        Assert.Equal(129, names["names"]!.AsArray().Count);
        release.Set();
        Assert.NotEqual(true, (await call).IsError);
        var text = Assert.IsType<TextResourceContents>(Assert.Single((await catalog).Contents)).Text;
        Assert.NotEmpty(JsonNode.Parse(text)!["prefabs"]!.AsArray());
        Assert.Equal(0, connection.Thread.Queue.Waiting);
    }

    /// <summary>The protocol has no tool error for a read. So a read of the catalog that the queue refuses gives a
    /// protocol error with the text of the error. The clock of the queue does not move, so the second read is above the
    /// calls each second. A read of the schema does not go into the queue, so it does not count.</summary>
    [Fact]
    public async Task AReadOfTheCatalogAboveTheRateGivesAProtocolErrorWithTheReason()
    {
        var host = new TestAgentHost(null) { TestLimits = new AgentLimits { CallsPerSecond = 1 } };
        await using var connection = new Connection(host, new FakeClock());
        var client = await connection.Client();

        var first = await ReadJson(client, "openlancer://catalog/prefabs");
        var refused = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.ReadResourceAsync("openlancer://catalog/prefabs", cancellationToken: Token).AsTask());
        var names = await ReadJson(client, "openlancer://schema/mission-names");

        Assert.NotEmpty(first["prefabs"]!.AsArray());
        Assert.Equal(McpErrorCode.InternalError, refused.ErrorCode);
        Assert.Contains(AgentTexts.Error("limit_calls", ("max", 1)), refused.Message);
        Assert.Equal(129, names["names"]!.AsArray().Count);
        Assert.Equal(0, connection.Thread.Queue.Waiting);
    }

    private static async Task<JsonNode> ReadJson(McpClient client, string uri)
    {
        var result = await client.ReadResourceAsync(uri, cancellationToken: Token);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("application/json", contents.MimeType);
        return JsonNode.Parse(contents.Text)!;
    }

    private static string Snake(EngineMark mark) => JsonSerializer.Serialize(mark, EditJson.Options).Trim('"');

    /// <summary>The prompt two_trigger_mission (sections 9.4 and 9.5 of the MCP1 design) gives the steps of workflow 3
    /// with the script of its argument. A get without the argument, or of a prompt that the server does not have, is a
    /// protocol error.</summary>
    [Fact]
    public async Task ThePromptOfTheTwoTriggerMissionGivesItsStepsWithTheScript()
    {
        await using var connection = new Connection(new TestAgentHost(null));
        var client = await connection.Client();
        const string script = @"msn:DATA\MISSIONS\my_mission.ini";

        var prompts = await client.ListPromptsAsync(cancellationToken: Token);
        var result = await client.GetPromptAsync("two_trigger_mission",
            new Dictionary<string, object?> { ["script"] = script }, cancellationToken: Token);

        Assert.Equal(["two_trigger_mission", "short_cutscene"], prompts.Select(p => p.Name));
        var prompt = prompts[0].ProtocolPrompt;
        Assert.Equal(("two_trigger_mission", AgentTexts.Prompts["two_trigger_mission"].Title), (prompt.Name, prompt.Title));
        var argument = Assert.Single(prompt.Arguments!);
        Assert.Equal(("script", true), (argument.Name, argument.Required));
        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);
        var text = Assert.IsType<TextContentBlock>(message.Content).Text;
        Assert.StartsWith($"Add a pirate ambush with two triggers to the mission script {script}.", text);
        Assert.DoesNotContain("{script}", text);
        Assert.Contains($"Give the script {script} to each mission tool and to each mission step of edit_batch.", text);
        Assert.Contains($"7. Call mission_dry_run with the script {script} and these four inputs:", text);
        Assert.Contains("6. Call edit_batch with the label \"Add the pirate ambush\" and these three steps:", text);
        // The scope mission does not give the errors of the other checks, so the prompt also asks for the scope all.
        Assert.Contains($"8. Call check with the scope mission and the ref {script}. Then call check with the scope all.", text);
        Assert.Equal(AgentTexts.Prompts["two_trigger_mission"].Description, result.Description);
        await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.GetPromptAsync("two_trigger_mission", cancellationToken: Token).AsTask());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.GetPromptAsync("two_trigger_mission",
            new Dictionary<string, object?> { ["script"] = " " }, cancellationToken: Token).AsTask());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.GetPromptAsync("build_rocket",
            new Dictionary<string, object?> { ["script"] = script }, cancellationToken: Token).AsTask());
    }

    /// <summary>The resource of the event types (section 9.3 of the MCP1 design) gives each role of the Add event menu
    /// with the entity types of its targets and its length, and each entity type that the editor adds with its place.
    /// It needs no profile, so its read does not wait for the session thread.</summary>
    [Fact]
    public async Task TheResourceOfTheEventTypesGivesEachRole()
    {
        await using var connection = new Connection(new TestAgentHost(null));
        var client = await connection.Client();

        var catalog = await ReadJson(client, "openlancer://catalog/thn-events");

        var events = catalog["events"]!.AsArray();
        Assert.Equal(ThnTemplates.Roles.Select(r => r.Type), events.Select(e => (string?)e!["type"]));
        foreach (var (role, row) in ThnTemplates.Roles.Zip(events))
        {
            Assert.Equal(role.First, row!["first"]!.AsArray().Select(t => (string?)t));
            Assert.Equal(role.Second, row["second"]!.AsArray().Select(t => (string?)t));
            Assert.Equal(role.SecondRequired, (bool)row["second_required"]!);
            Assert.Equal(ThnSchema.HasLength(role.Type), (bool)row["has_length"]!);
        }
        var camera = events.Single(e => (string?)e!["type"] == "SET_CAMERA")!;
        Assert.Equal("""{"type":"SET_CAMERA","first":["MONITOR"],"second":["CAMERA"],"second_required":true,"has_length":false}""",
            camera.ToJsonString());
        Assert.DoesNotContain("CAMERA", events.Single(e => (string?)e!["type"] == "START_MOTION")!["first"]!.AsArray()
            .Select(t => (string?)t));
        var entities = catalog["entities"]!.AsArray();
        Assert.Equal(ThnTemplates.EntityTypes, entities.Select(e => (string?)e!["type"]));
        Assert.Equal(["MONITOR", "SCENE"], entities.Where(e => !(bool)e!["has_place"]!).Select(e => (string?)e!["type"]));
        Assert.Equal(0, connection.Thread.Queue.Waiting);
    }

    /// <summary>The prompt short_cutscene (sections 9.4 and 9.5 of the MCP1 design) gives the steps of workflow 4 with the
    /// name of its argument. The editor host, the host without a window and the host without the save list it. The
    /// steps of the images and of the save tell what to do without these tools.</summary>
    [Fact]
    public async Task ThePromptOfTheShortCutsceneGivesItsStepsInEachHostWithTheEditTools()
    {
        using var ws = SceneWorkspace.Create();
        using var other = SceneWorkspace.Create();
        var headless = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { WritesRecovery = false });
        var noSave = HeadlessHost.Open(other.ProfilePath, "act",
            new HeadlessOptions { WritesRecovery = false, NoSave = true });
        await using (var connection = new Connection(new OpenLancer.Editor.Agent.EditorAgentHost(new FakeAgentEditor())))
        {
            var client = await connection.Client();

            var prompts = await client.ListPromptsAsync(cancellationToken: Token);
            var result = await client.GetPromptAsync("short_cutscene",
                new Dictionary<string, object?> { ["name"] = "my_intro" }, cancellationToken: Token);

            Assert.Equal(["two_trigger_mission", "short_cutscene"], prompts.Select(p => p.Name));
            var argument = Assert.Single(prompts[1].ProtocolPrompt.Arguments!);
            Assert.Equal(("name", true), (argument.Name, argument.Required));
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
            Assert.StartsWith("Make a short cutscene with the name my_intro.", text);
            Assert.Contains("2. Call cutscene_create with the name my_intro and the duration 8.", text);
            Assert.Contains("Call list_values with the list costume", text);
            Assert.Contains("the targets [Monitor, my_cam_2]", text);
            Assert.Contains("8. Call render_cutscene with the time 2, then with the time 6.", text);
            Assert.Contains("10. Call check with the scope cutscene", text);
            Assert.EndsWith("Do not call save until the user asks for it.", text);
            Assert.DoesNotContain("{name}", text);
            Assert.Equal(AgentTexts.Prompts["short_cutscene"].Description, result.Description);
        }
        Assert.DoesNotContain(noSave.Tools, t => t.Name == "save");
        // Each connection closes its host at its end.
        foreach (var host in new AgentHost[] { headless, noSave })
        {
            await using var connection = new Connection(host);
            var client = await connection.Client();
            Assert.Equal(["two_trigger_mission", "short_cutscene"],
                (await client.ListPromptsAsync(cancellationToken: Token)).Select(p => p.Name));
        }
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

    /// <summary>The client starts ten edit calls at the same time (section 13.2, item 5 of the MCP1 design). They run
    /// one after the other on the session thread of the host without a window. Each call is one Undo step, and each new
    /// zone gets its own nickname.</summary>
    [Fact]
    public async Task TenEditCallsAtTheSameTimeRunOneAfterTheOtherInTheHostWithoutAWindow()
    {
        using var ws = SceneWorkspace.Create();
        await using var connection = new Connection(HeadlessHost.Open(ws.ProfilePath, "act",
            new HeadlessOptions { WritesRecovery = false }));
        var client = await connection.Client();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => client.CallToolAsync("scene_create_zone",
            new Dictionary<string, object?> { ["system"] = "Li01", ["position"] = new[] { i * 1000, 0, 0 } },
            cancellationToken: Token).AsTask()));
        // The ten calls take the calls of one second (the rate limit of section 8.4 of the MCP1 design).
        await Task.Delay(TimeSpan.FromSeconds(1.1), Token);
        var status = await client.CallToolAsync("studio_status", cancellationToken: Token);

        Assert.All(results, r => Assert.NotEqual(true, r.IsError));
        Assert.Equal(10, results.Select(r => r.StructuredContent!.Value.GetProperty("adds")[0].GetString()).Distinct().Count());
        Assert.Equal("10", status.StructuredContent!.Value.GetProperty("rev").GetString());
        Assert.True(status.StructuredContent.Value.GetProperty("can_edit").GetBoolean());
    }

    /// <summary>A host with <c>--no-save</c> does not list <c>save</c> and <c>export_mod</c>, and a call of them is a
    /// protocol error. The server instructions then have no step of the save.</summary>
    [Fact]
    public async Task AHostWithoutSaveDoesNotListSaveAndExport()
    {
        using var ws = SceneWorkspace.Create();
        await using var connection = new Connection(HeadlessHost.Open(ws.ProfilePath, "act",
            new HeadlessOptions { NoSave = true, WritesRecovery = false }));
        var client = await connection.Client();

        var tools = await client.ListToolsAsync(cancellationToken: Token);

        Assert.DoesNotContain(tools, t => t.Name is "save" or "export_mod");
        Assert.Contains(tools, t => t.Name == "set_fields");
        Assert.DoesNotContain("save", client.ServerInstructions);
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("save", cancellationToken: Token).AsTask());
    }

    /// <summary>A call whose request has a progress token gets <c>notifications/progress</c> of its tail (section 5.3 of
    /// the MCP1 design), for example of an export. Each notification tells the step of the export. A notification
    /// refers to a call that still runs, so the server sends each one in its order and before the response. The test
    /// reads the lines of the server itself, because the client of the SDK can give the notifications to its progress
    /// in another order.</summary>
    [Fact]
    public async Task AnExportSendsItsProgressNotificationsInOrderBeforeItsResponse()
    {
        using var game = new ExportGame();
        var (_, session) = AgentPublishToolTests.Ready(game);
        using var _ = session;
        await using var connection = new Connection(new TestAgentHost(session));
        var (writer, reader) = connection.Raw();
        await writer.WriteLineAsync(Request(1, "initialize",
            """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}"""));
        Assert.Equal(1, (int)JsonNode.Parse((await reader.ReadLineAsync(Token))!)!["id"]!);
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        await writer.WriteLineAsync(Request(2, "tools/call",
            """{"name":"export_mod","arguments":{"formats":["drop_in"]},"_meta":{"progressToken":"export"}}"""));
        var before = new List<JsonNode>();
        JsonNode answer;
        while (true)
        {
            var line = JsonNode.Parse((await reader.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Token))!)!;
            if (line["id"] != null)
            {
                answer = line;
                break;
            }
            before.Add(line);
        }

        Assert.Equal(2, (int)answer["id"]!);
        Assert.True((bool)answer["result"]!["structuredContent"]!["succeeded"]!, answer.ToJsonString());
        Assert.All(before, n => Assert.Equal(("notifications/progress", "export"),
            ((string?)n["method"], (string?)n["params"]!["progressToken"])));
        var reports = before.Select(n => n["params"]!).ToList();
        Assert.Equal([0, 1, 2, 3], reports.Select(r => (int)r["progress"]!));
        Assert.All(reports, r => Assert.Equal(3, (int)r["total"]!));
        Assert.Equal(["The export checks the mod.", "The export writes the packages.",
            "The export records the packages in the publish history.", null], reports.Select(r => (string?)r["message"]));
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

        Assert.Equal("The mod 'act' is part of the OpenLancer repo, so its files are tracked files. The server without a " +
                     "window never changes this mod. Use the agent server of the editor.", host.RepoReason);
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
