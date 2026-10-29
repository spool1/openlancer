using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using OpenLancer.Data.Editing.Commands;
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
    /// edit tools of section 7.2, the Scene and data tools of section 7.3, the tools of section 7.6 with the play tools
    /// at their end, and the view tools of section 7.7.</summary>
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

    /// <summary>The kind and the hints of each tool (section 7.0 of the MCP1 design, review finding R7 of MCP1-2). A
    /// query changes nothing. <c>open_view</c> and <c>select_items</c> change what the user sees, so they are not
    /// read-only. An edit that can change or remove data is destructive, and only <c>set_fields</c> is idempotent,
    /// because MCP has one hint for each tool. The Console, the play tools and the view tools need the window of the
    /// editor. Pause refuses each tool that changes something, except play_stop.</summary>
    [Fact]
    public void EachToolHasTheKindAndTheHintsOfWhatItChanges()
    {
        string[] views = ["open_view", "select_items", "render_scene", "render_cutscene", "capture_panel"];
        string[] editor = ["get_console", "play", "play_stop", .. views];
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
            .ToList();

        Assert.Empty(problems);
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
                     "Use play_stop to stop each kind of play.", editor[^1]);
        Assert.DoesNotContain(edits, i => i.Contains("play_stop"));
        Assert.Equal("This server only reads the data. It changes no file.", reads[^1]);
        Assert.DoesNotContain(edits, i => i.Contains("only reads"));
        Assert.Contains("Use preview: true for a large edit or a delete.", edits);
        Assert.Contains("Call check before save. Correct each error.", edits);
        Assert.Contains("Do not save until the user asks, unless the user told you to save.", edits);
        Assert.DoesNotContain(noSave, i => i.Contains("save"));
    }

    /// <summary>The edit tools, the tools of the session and the play tools of this build in the order of the table,
    /// with their kinds and hints. The tools of section 7.3 follow the tools of section 7.2, and the tools of section
    /// 7.6 follow them. A tool that adds data is not destructive, and a tool that can change or remove data is (review
    /// finding R7 of MCP1-2). play can save the mod, so it is destructive (section 7.0). play_stop removes no
    /// data.</summary>
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
        ["prop_group_place"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["prop_group_save"] = (ToolKind.AssetWrite, new ToolHints(false, false, false)),
        ["prop_group_rename"] = (ToolKind.AssetWrite, new ToolHints(false, true, false)),
        ["prop_group_delete"] = (ToolKind.AssetWrite, new ToolHints(false, true, false)),
        ["create_loadout"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["create_from_template"] = (ToolKind.Edit, new ToolHints(false, false, false)),
        ["save"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["undo"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["redo"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["edit_batch"] = (ToolKind.Session, new ToolHints(false, false, false)),
        ["export_mod"] = (ToolKind.Session, new ToolHints(false, true, false)),
        ["publish_checklist"] = (ToolKind.Session, new ToolHints(true, false, true)),
        ["play"] = (ToolKind.Play, new ToolHints(false, true, false)),
        ["play_stop"] = (ToolKind.Play, new ToolHints(false, false, false)),
    };

    private static IEnumerable<string> EditTools => Edits.Keys;

    private static string[] Problems(string where, string text, int words = SteLint.DescriptionWords) =>
        SteLint.Problems(text, words).Select(p => $"{where}: {p}: {text}").ToArray();

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
