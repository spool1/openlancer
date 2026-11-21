using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The tool table of the agent server (section 13.1, items 1 to 3 of the MCP1 design): the names, the texts, the
/// schemas and the hints of each tool, and the STE lint of each text of <c>agent-tools.json</c>.
/// </summary>
public sealed class AgentToolTableTests
{
    /// <summary>The order follows the sections of the design. The query tools of section 7.1 come first. Then come the
    /// edit tools of section 7.2, the Scene and data tools of section 7.3, the mission tools of section 7.4, the
    /// cutscene tools of section 7.5, the tools of section 7.6 with the play tools at their end, and the view tools of
    /// section 7.7.</summary>
    [Fact]
    public void TheToolsHaveTheFixedOrderOfTheSectionsOfTheDesign()
    {
        Assert.Equal(
        [
            "studio_status", "list_systems", "get_system", "list_items", "get_item", "find_references", "list_values",
            "search_assets", "list_prop_groups", "list_missions", "get_mission", "get_trigger", "describe_mission_name",
            "mission_dry_run", "list_cutscenes", "get_cutscene", "get_raw_ini", "check", "get_console",
            .. EditTools,
            "open_view", "select_items", "render_scene", "render_cutscene", "capture_panel",
        ], AgentTools.All.Select(t => t.Name));
    }

    /// <summary>The table of the tools in the README and the help page of the agent server name each tool. Help >
    /// Documentation opens the README, and the editor sends users to the tools.</summary>
    [Fact]
    public void TheReadmeAndTheHelpPageNameEachTool()
    {
        var readme = System.IO.File.ReadAllText(RepoFiles.Of("README.md"));
        var help = System.IO.File.ReadAllText(RepoFiles.Of("docs", "MCP.md"));

        var names = AgentTools.All.Select(t => $"`{t.Name}`").ToList();
        Assert.DoesNotContain(names, name => !readme.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => !help.Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public void EachToolHasAValidNameATitleADescriptionAndObjectSchemas()
    {
        foreach (var tool in AgentTools.All)
        {
            Assert.Matches(new Regex("^[a-z][a-z0-9_]{0,127}$"), tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Title), tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);

            var input = tool.InputSchema;
            Assert.Empty(SchemaCheck.SchemaProblems(input));
            Assert.Equal("object", (string?)input["type"]);
            Assert.False((bool)input["additionalProperties"]!);
            foreach (var (name, property) in input["properties"]!.AsObject())
                Assert.False(string.IsNullOrWhiteSpace((string?)property!["description"]), $"{tool.Name}.{name}");

            var output = tool.OutputSchema;
            Assert.Empty(SchemaCheck.SchemaProblems(output));
            Assert.Contains("object", output["type"] is JsonArray types ? types.Select(t => (string?)t) : [(string?)output["type"]]);
        }
    }

    /// <summary>The schema check of the tests resolves a <c>$ref</c> as a JSON Pointer (RFC 6901): <c>~1</c> is a
    /// <c>/</c> and <c>~0</c> is a <c>~</c> in a name. A <c>$ref</c> that does not resolve is a problem of the schema.
    /// <c>oneOf</c> takes exactly one match, and <c>const</c> takes one value (open alignment item 4 of the MCP1
    /// design: the repository has no JSON Schema package).</summary>
    [Fact]
    public void TheSchemaCheckResolvesEachReferenceAndReadsOneOfAndConst()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "a": { "$ref": "#/$defs/x~1y" },
                "b": { "$ref": "#/$defs/x~0y" },
                "c": { "oneOf": [ { "type": "number" }, { "type": "integer" } ] }
              },
              "$defs": { "x/y": { "const": "slash" }, "x~y": { "const": "tilde" } }
            }
            """)!;
        var broken = JsonNode.Parse("""{ "properties": { "a": { "$ref": "#/$defs/x/y" } }, "$defs": { "x/y": {} } }""");

        Assert.Empty(SchemaCheck.SchemaProblems(schema));
        Assert.Empty(SchemaCheck.Problems(schema, JsonNode.Parse("""{ "a": "slash", "b": "tilde", "c": 1.5 }""")));
        Assert.NotEmpty(SchemaCheck.Problems(schema, JsonNode.Parse("""{ "a": "tilde" }""")));
        Assert.NotEmpty(SchemaCheck.Problems(schema, JsonNode.Parse("""{ "c": 2 }""")));
        Assert.Equal(["#/properties/a: the reference '#/$defs/x/y' does not resolve."], SchemaCheck.SchemaProblems(broken));
    }

    /// <summary>The kind and the hints of each tool (section 7.0 of the MCP1 design, review finding R7 of MCP1-2). A
    /// query changes nothing. <c>open_view</c> and <c>select_items</c> change what the user sees, so they are not
    /// read-only. An edit that can change or remove data is destructive. Only <c>set_fields</c> and the cutscene tools
    /// with an absolute value, <c>cutscene_set_duration</c> and <c>cutscene_set_flags</c>, are idempotent, because MCP
    /// has one hint for each tool. The Console, the play tools and the view tools need the window of the
    /// editor. Pause refuses each tool that changes something, except play_stop.</summary>
    [Fact]
    public void EachToolHasTheKindAndTheHintsOfWhatItChanges()
    {
        string[] views = ["open_view", "select_items", "render_scene", "render_cutscene", "capture_panel"];
        string[] editor = ["get_console", "mission_play", "mission_play_trace", "play_capture", "play", "play_stop", .. views];
        var query = new ToolHints(ReadOnly: true, Destructive: false, Idempotent: true);

        foreach (var tool in AgentTools.All)
        {
            var (kind, hints) = tool.Name switch
            {
                "open_view" or "select_items" => (ToolKind.View, new ToolHints(false, false, true)),
                "render_scene" or "render_cutscene" or "capture_panel" => (ToolKind.View, query),
                _ when Edits.TryGetValue(tool.Name, out var edit) => edit,
                _ => (ToolKind.Query, query),
            };
            Assert.Equal((kind, hints), (tool.Kind, tool.Hints));
            Assert.Equal(editor.Contains(tool.Name) ? ToolHosts.Editor : ToolHosts.Both, tool.Hosts);
            // A tool of the session that only reads, for example publish_checklist, and play_stop run during Pause.
            Assert.Equal(kind is not (ToolKind.Query or ToolKind.View) && !hints.ReadOnly && tool.Name != "play_stop",
                tool.Pausable);
            // The play tools, and only they, are in the group Play.
            Assert.Equal(kind == ToolKind.Play, tool.Group == ToolGroup.Play);
        }
    }

    /// <summary>A host that only reads has the read-only tools (section 6.2 of the MCP1 design). The editor tools among
    /// them stay, and give not_available in the host without a window.</summary>
    [Fact]
    public void AHostThatOnlyReadsHasTheReadOnlyTools()
    {
        Assert.Equal(AgentTools.All.Where(t => !(Edits.TryGetValue(t.Name, out var edit) && !edit.Hints.ReadOnly) &&
                                               t.Name is not ("open_view" or "select_items")),
            AgentTools.ReadOnly);
        Assert.Equal(AgentTools.All, new TestAgentHost(null).Tools);
    }

    [Fact]
    public void TheTextFileHasTheTextsOfEachToolAndNoOtherTool()
    {
        Assert.Equal(AgentTools.All.Select(t => t.Name).Order(), AgentTexts.Tools.Keys.Order());
    }

    [Fact]
    public void EachTextPassesTheSteLint()
    {
        var problems = AgentTexts.Instructions.Values.SelectMany(i => i)
            .SelectMany(t => Problems("instruction", t, SteLint.InstructionWords))
            .Concat(AgentTexts.SharedFields.SelectMany(f => Problems($"field {f.Key}", f.Value)))
            .Concat(AgentTexts.Tools.SelectMany(t => Problems($"{t.Key} title", t.Value.Title)
                .Concat(Problems($"{t.Key} description", t.Value.Description))
                .Concat(t.Value.Fields.SelectMany(f => Problems($"{t.Key}.{f.Key}", f.Value)))))
            .Concat(AgentTexts.Errors.SelectMany(e => Problems($"error {e.Key}", e.Value, SteLint.InstructionWords)))
            .Concat(AgentTexts.Results.SelectMany(r => Problems($"result {r.Key}", r.Value)))
            .Concat(AgentTexts.Resources.SelectMany(r => Problems($"{r.Key} title", r.Value.Title)
                .Concat(Problems($"{r.Key} description", r.Value.Description))
                .Concat(r.Value.Text.SelectMany(line => Problems(r.Key, line)))))
            .Concat(AgentTexts.Prompts.SelectMany(p => Problems($"prompt {p.Key} title", p.Value.Title)
                .Concat(Problems($"prompt {p.Key} description", p.Value.Description))
                .Concat(p.Value.Arguments.SelectMany(a => Problems($"prompt {p.Key}.{a.Name}", a.Help)))
                .Concat(p.Value.Text.SelectMany(line => Problems($"prompt {p.Key}", line, SteLint.InstructionWords)))))
            // The help of the mission commands is strict STE (section 9.1 of the MCP1 design).
            .Concat(AgentTexts.MissionFields.SelectMany(f => Problems($"mission field {f.Key}", f.Value, SteLint.InstructionWords)))
            .Concat(AgentTexts.MissionCommands.SelectMany(c => Problems($"command {c.Key}", c.Value.Description, SteLint.InstructionWords)
                .Concat(c.Value.Fields.SelectMany(f => Problems($"command {c.Key}.{f.Key}", f.Value, SteLint.InstructionWords)))))
            .Concat(AgentTexts.MissionParts.SelectMany(p => p.Value.SelectMany(f =>
                Problems($"mission part {p.Key}.{f.Key}", f.Value, SteLint.InstructionWords))))
            .ToList();

        Assert.Empty(problems);
    }

    /// <summary>The lint of the texts covers the prompts: a prompt line above the word limit of an instruction is a
    /// problem.</summary>
    [Fact]
    public void TheLintCoversThePromptTexts()
    {
        var lines = AgentTexts.Prompts["two_trigger_mission"].Text;

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Empty(SteLint.Problems(line, SteLint.InstructionWords)));
        Assert.NotEmpty(SteLint.Problems(string.Join(" ", Enumerable.Repeat("Call", 21)) + ".", SteLint.InstructionWords));
    }

    /// <summary>A prompt gives the steps of a workflow with the names of real tools (section 9.4 of the MCP1 design).
    /// Each <c>{name}</c> of its lines is an argument, and each argument shows in the lines. A host lists the prompt
    /// only when it has each tool that the steps need, so a host that only reads lists no prompt. An optional tool of a
    /// prompt shows in its lines, and a host without it still lists the prompt.</summary>
    [Fact]
    public void EachPromptNamesItsArgumentsAndTheToolsOfItsSteps()
    {
        foreach (var prompt in AgentPrompts.All)
        {
            var text = string.Join("\n", prompt.Lines);
            var placeholders = System.Text.RegularExpressions.Regex.Matches(text, @"\{(\w+)\}").Select(m => m.Groups[1].Value)
                .Distinct();
            Assert.Equal(prompt.Arguments.Select(a => a.Name).Order(), placeholders.Order());
            Assert.All(prompt.Tools, t => Assert.NotNull(AgentTools.Find(t)));
            Assert.All(prompt.OptionalTools, t =>
            {
                Assert.NotNull(AgentTools.Find(t));
                Assert.Matches($@"(?<!\w){t}(?!\w)", text);
                Assert.DoesNotContain(t, prompt.Tools);
            });
        }
        var mission = Assert.Single(AgentPrompts.All, p => p.Name == "two_trigger_mission");
        Assert.Equal(["studio_status", "list_values", "get_mission", "mission_dry_run", "check", "mission_add_trigger",
            "mission_add_item", "edit_batch"], mission.Tools);
        Assert.Equal(["script"], mission.Arguments.Select(a => a.Name));
        Assert.Empty(mission.OptionalTools);
        var cutscene = Assert.Single(AgentPrompts.All, p => p.Name == "short_cutscene");
        Assert.Equal(["studio_status", "list_values", "check", "set_fields", "cutscene_create", "cutscene_add_entity",
            "cutscene_add_event", "cutscene_move_events"], cutscene.Tools);
        Assert.Equal(["render_cutscene", "save"], cutscene.OptionalTools);
        Assert.Equal(["name"], cutscene.Arguments.Select(a => a.Name));
        Assert.Equal([mission, cutscene], AgentPrompts.For(AgentTools.All));
        Assert.Equal([mission, cutscene],
            AgentPrompts.For(AgentTools.All.Where(t => !HeadlessHost.SaveTools.Contains(t.Name) && t.Name != "render_cutscene")));
        Assert.Equal([mission], AgentPrompts.For(AgentTools.All.Where(t => !t.Name.StartsWith("cutscene_"))));
        Assert.Empty(AgentPrompts.For(AgentTools.ReadOnly));
        Assert.Contains(@"msn:DATA\MISSIONS\my_mission.ini 1", mission.Text(new Dictionary<string, string>
        {
            ["script"] = @"msn:DATA\MISSIONS\my_mission.ini 1",
        }));
    }

    /// <summary>Each property of each output schema has a help of <c>agent-tools.json</c> (section 9.1 of the MCP1
    /// design), so the agent can read what a field means. A help of the file that names no property is an error
    /// too.</summary>
    [Fact]
    public void EachPropertyOfEachResultHasAHelpAndEachHelpNamesAProperty()
    {
        var properties = new HashSet<(Type Owner, string Name)>();
        var options = new JsonSerializerOptions(EditJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        foreach (var type in AgentTools.All.Select(t => t.Output).Append(typeof(EditError)).Distinct())
        {
            JsonSchemaExporter.GetJsonSchemaAsNode(options, type, new JsonSchemaExporterOptions
            {
                TransformSchemaNode = (context, node) =>
                {
                    if (context.PropertyInfo is { DeclaringType: var owner, Name: var name })
                        properties.Add((owner, name));
                    return node;
                },
            });
        }
        var typed = properties.Select(p => $"{AgentTexts.TypeName(p.Owner)}.{p.Name}").ToHashSet();
        var shared = properties.Where(p => !AgentTexts.Results.ContainsKey($"{AgentTexts.TypeName(p.Owner)}.{p.Name}"))
            .Select(p => p.Name).ToHashSet();

        var missing = properties.Where(p => AgentTexts.Result(p.Owner, p.Name) == null)
            .Select(p => $"{AgentTexts.TypeName(p.Owner)}.{p.Name}").Order().ToList();
        var unused = AgentTexts.Results.Keys.Where(k => k.Contains('.') ? !typed.Contains(k)
            : !shared.Contains(k) && k is not ("result" or "error" or StudioTool.RevisionName)).Order().ToList();
        Assert.True(missing.Count == 0, "No help: " + string.Join(", ", missing));
        Assert.True(unused.Count == 0, "Help of no property: " + string.Join(", ", unused));
        foreach (var tool in AgentTools.All)
        {
            Assert.Empty(Undescribed(tool.OutputSchema, tool.Name));
            Assert.Equal([AgentTexts.Result(null, "result"), AgentTexts.Result(null, "error")],
                tool.OutputSchema["anyOf"]!.AsArray().Select(o => (string?)o!["description"]));
        }
    }

    /// <summary>
    /// Each mission command of the input schema of <c>mission_edit</c> has a help, and so does each of its fields, also
    /// each field of an object in a command (section 9.1 of the MCP1 design, user decision of 2026-11-01). The help is
    /// in <c>agent-tools.json</c>. Each text of the file names a command or a field of the schema.
    /// </summary>
    [Fact]
    public void EachMissionCommandAndEachOfItsFieldsHasAHelp()
    {
        var definitions = AgentTools.Find("mission_edit")!.InputSchema["$defs"]!.AsObject();
        var parts = new HashSet<(string Type, string Field)>();
        MissionJson.Schema((type, field) =>
        {
            var text = AgentTexts.MissionHelp(type, field);
            if (text != null && field != null && !typeof(MissionCommand).IsAssignableFrom(type))
                parts.Add((AgentTexts.TypeName(type), field));
            return text;
        });

        Assert.Equal(MissionJson.Kinds.Keys.Order(), AgentTexts.MissionCommands.Keys.Order());
        var missing = new List<string>();
        var shared = new HashSet<string>();
        foreach (var kind in MissionJson.Kinds.Keys)
        {
            var definition = definitions[kind]!.AsObject();
            if (definition["description"] == null)
                missing.Add(kind);
            missing.AddRange(UndescribedFields(definition, kind));
            var own = AgentTexts.MissionCommands[kind].Fields;
            var fields = definition["properties"]!.AsObject().Select(p => p.Key).ToList();
            Assert.Empty(own.Keys.Except(fields));
            shared.UnionWith(fields.Except(own.Keys));
        }
        Assert.True(missing.Count == 0, "No help: " + string.Join(", ", missing));
        Assert.Empty(AgentTexts.MissionFields.Keys.Except(shared));
        Assert.Equal(["kind"], AgentTexts.MissionParts["MissionCommand"].Keys);
        Assert.Empty(AgentTexts.MissionParts.Where(p => p.Key != "MissionCommand")
            .SelectMany(p => p.Value.Keys.Select(f => (p.Key, f))).Except(parts));
        Assert.Equal("The kind of the command.", (string?)definitions["add_trigger"]!["properties"]!["kind"]!["description"]);
        Assert.Equal(AgentTexts.MissionFields["row"], (string?)definitions["remove_line"]!["properties"]!["row"]!["description"]);
    }

    /// <summary>The structured content of a tool error fits the output schema of each tool (section 5.7 of the MCP1
    /// design). A client that checks the structured content of each call then gets the message of the error.</summary>
    [Fact]
    public void EachOutputSchemaTakesAToolErrorAndNotAnEmptyResult()
    {
        var error = ToolResult.Failure(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound, "The edit view has no x.")
        {
            Field = "ref",
        });

        foreach (var tool in AgentTools.All)
        {
            Assert.Empty(SchemaCheck.Problems(tool.OutputSchema, error.Content));
            Assert.Equal("string", (string?)tool.OutputSchema["properties"]![StudioTool.RevisionName]!["type"]);
            if (tool.OutputSchema["anyOf"]![0]!["required"] is JsonArray { Count: > 0 })
                Assert.NotEmpty(SchemaCheck.Problems(tool.OutputSchema, new JsonObject()));
        }
    }

    /// <summary>The server instructions follow the tools of the host (section 9.2 of the MCP1 design, review finding R10
    /// of MCP1-2). A host without edit tools tells that the server only reads. A host without the tool save gets no
    /// step of the save. Only the editor host gets the step of the play tools, because only it can play.</summary>
    [Fact]
    public void TheServerInstructionsAreShortStepsForTheToolsOfTheHost()
    {
        var editor = AgentTexts.InstructionsFor(edits: true, save: true, play: true);
        var edits = AgentTexts.InstructionsFor(edits: true, save: true, play: false);
        var noSave = AgentTexts.InstructionsFor(edits: true, save: false, play: false);
        var reads = AgentTexts.InstructionsFor(edits: false, save: false, play: false);

        Assert.InRange(editor.Count, 3, 14);
        Assert.StartsWith("Call studio_status first.", edits[0]);
        Assert.StartsWith("Call studio_status first.", reads[0]);
        Assert.Equal(string.Join("\n", editor), AgentResources.InstructionsFor(AgentTools.All, AgentHostKind.Editor));
        Assert.Equal(string.Join("\n", edits), AgentResources.InstructionsFor(AgentTools.All, AgentHostKind.Headless));
        Assert.Equal(string.Join("\n", noSave), AgentResources.InstructionsFor(
            AgentTools.All.Where(t => t.Name is not ("save" or "export_mod")), AgentHostKind.Headless));
        Assert.Equal(string.Join("\n", reads), AgentResources.InstructionsFor(AgentTools.ReadOnly, AgentHostKind.Headless));
        Assert.Equal(string.Join("\n", reads), AgentResources.InstructionsFor(AgentTools.ReadOnly, AgentHostKind.Editor));
        Assert.Equal("A play tool asks the user to save when the mod has unsaved edits. Do not play until the user asks. " +
                     "Use play_stop to stop each kind of play. play_capture gives an image of the running OpenLancer game.",
            editor[^1]);
        Assert.DoesNotContain(edits, i => i.Contains("play_stop"));
        Assert.Equal("This server only reads the data. It changes no file.", reads[^1]);
        Assert.DoesNotContain(edits, i => i.Contains("only reads"));
        Assert.Contains("Use preview: true for a large edit or a delete.", edits);
        Assert.Contains("Call check before save. Correct each error.", edits);
        Assert.Contains("Do not save until the user asks, unless the user told you to save.", edits);
        Assert.DoesNotContain(noSave, i => i.Contains("save"));
    }

    /// <summary>The edit tools, the tools of the session and the play tools of this build in the order of the table,
    /// with their kinds and hints. The tools of section 7.3 follow the tools of section 7.2. The mission tools of
    /// section 7.4 follow them, with the mission play, its live trace and play_capture at their end. The universe tools
    /// come after the builders: system_create adds data, connection_create can change an existing end, and
    /// connection_set_lock sets absolute values, so a second call changes nothing. The trade lane tools follow them:
    /// trade_lane_create adds data, an insert changes the links of the neighbors, and a second repair changes nothing.
    /// get_trade_lane follows the other read tools of the universe. The tools of section 7.6 come next. mission_edit can remove triggers and items, so it is destructive. A tool that adds data
    /// is not destructive, and a tool that can change or remove data is (review finding R7 of MCP1-2). So a mission
    /// tool that sets, moves or links lines is destructive. edit_batch can have a step that removes data, so it is
    /// destructive (final finding of MCP1-2). play and mission_play can save the mod, so
    /// they are destructive (section 7.0). play_stop removes no data. mission_play_trace only reads, and so does
    /// play_capture, which is in the group Play.</summary>
    private static readonly Dictionary<string, (ToolKind Kind, ToolHints Hints)> Edits = new()
    {
        ["set_fields"] = (ToolKind.Edit, new ToolHints(false, true, true)),
        ["set_text"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["rename_item"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["delete_items"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["duplicate_items"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["scene_create_object"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["scene_create_zone"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["scene_create_light"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["scene_spawn_asset"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["scene_transform"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["scene_set_component"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["scene_place_wreck"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["scene_place_patrol_route"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["scene_set_encounters"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["system_create"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["connection_create"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["connection_set_lock"] = (ToolKind.Edit, new ToolHints(false, true, true)),
        ["trade_lane_create"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["trade_lane_insert_ring"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["trade_lane_repair"] = (ToolKind.Edit, new ToolHints(false, true, true)),
        ["trade_lane_delete"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["get_universe"] = (ToolKind.Query, new ToolHints(true, false, true)),
        ["get_route"] = (ToolKind.Query, new ToolHints(true, false, true)),
        ["get_trade_lane"] = (ToolKind.Query, new ToolHints(true, false, true)),
        ["prop_group_place"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["prop_group_save"] = (ToolKind.AssetWrite, new ToolHints(false, false, false)),
        ["prop_group_rename"] = (ToolKind.AssetWrite, new ToolHints(false, true, false)),
        ["prop_group_delete"] = (ToolKind.AssetWrite, new ToolHints(false, true, false)),
        ["create_loadout"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["create_from_template"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["mission_edit"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["mission_add_trigger"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["mission_set_condition"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["mission_add_action"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["mission_set_action"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["mission_move_action"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["mission_link_triggers"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["mission_insert_trigger"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["mission_add_item"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["mission_place_prefab"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["mission_start_plan"] = (ToolKind.Query, new ToolHints(true, false, true)),
        ["mission_play"] = (ToolKind.Play, new ToolHints(false, true, false)),
        ["mission_play_trace"] = (ToolKind.Query, new ToolHints(true, false, true)),
        ["play_capture"] = (ToolKind.Play, new ToolHints(true, false, true)),
        ["cutscene_create"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["cutscene_add_entity"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["cutscene_add_event"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["cutscene_move_events"] = (ToolKind.Edit, new ToolHints(false, true, false)),
        ["cutscene_set_duration"] = (ToolKind.Edit, new ToolHints(false, true, true)),
        ["cutscene_set_flags"] = (ToolKind.Edit, new ToolHints(false, true, true)),
        ["save"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["undo"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["redo"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["edit_batch"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["export_mod"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["publish_checklist"] = (ToolKind.Session, new ToolHints(true, false, true)),
        ["play"] = (ToolKind.Play, new ToolHints(false, true, false)),
        ["play_stop"] = (ToolKind.Play, new ToolHints(false, false, false)),
    };

    private static IEnumerable<string> EditTools => Edits.Keys;

    private static string[] Problems(string where, string text, int words = SteLint.DescriptionWords) =>
        SteLint.Problems(text, words).Select(p => $"{where}: {p}: {text}").ToArray();

    /// <summary>The paths of the properties of a schema without a help, at each level. A property that refers to another
    /// schema (<c>$ref</c>) needs its own help too.</summary>
    private static IEnumerable<string> UndescribedFields(JsonNode? schema, string path)
    {
        if (schema is JsonArray array)
            return array.SelectMany((item, i) => UndescribedFields(item, $"{path}[{i}]"));
        if (schema is not JsonObject obj)
            return [];
        var own = obj["properties"] is JsonObject properties
            ? properties.Where(p => p.Value is not JsonObject { } property || property["description"] == null)
                .Select(p => $"{path}.{p.Key}")
            : [];
        return own.Concat(obj.SelectMany(p => UndescribedFields(p.Value, $"{path}/{p.Key}")));
    }

    /// <summary>The paths of the properties of a schema without a help. A property that only refers to another schema
    /// (<c>$ref</c>) gets the help of that schema.</summary>
    private static IEnumerable<string> Undescribed(JsonNode? schema, string path)
    {
        if (schema is JsonArray array)
            return array.SelectMany((item, i) => Undescribed(item, $"{path}[{i}]"));
        if (schema is not JsonObject obj)
            return [];
        var own = obj["properties"] is JsonObject properties
            ? properties.Where(p => p.Value is JsonObject property && !property.ContainsKey("$ref") &&
                                    property["description"] == null)
                .Select(p => $"{path}.{p.Key}")
            : [];
        return own.Concat(obj.SelectMany(p => Undescribed(p.Value, $"{path}/{p.Key}")));
    }
}
