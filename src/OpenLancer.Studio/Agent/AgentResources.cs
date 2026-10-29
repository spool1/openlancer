using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// A resource of the agent server (section 9.3 of the MCP1 design): a text that the agent can read on request. The
/// resources hold OpenLancer's own texts and the rule data only, so they need no session.
/// </summary>
/// <param name="Uri">The URI, for example <c>openlancer://guide/refs</c>.</param>
/// <param name="MimeType"><c>text/plain</c> or <c>application/json</c>.</param>
public sealed record AgentResource(string Uri, string Name, string Title, string Description, string MimeType,
    Func<string> Read);

/// <summary>The resources of this build: the data lists and the guides. The mission resources come with the mission
/// tools (section 12.5 of the MCP1 design).</summary>
public static class AgentResources
{
    private const string Lists = "openlancer://catalog/lists";

    /// <summary>The resources in the order of <c>resources/list</c>.</summary>
    public static IReadOnlyList<AgentResource> All { get; } = AgentTexts.Resources.Select(r =>
        new AgentResource(r.Key, r.Value.Name, r.Value.Title, r.Value.Description,
            r.Key == Lists ? "application/json" : "text/plain",
            r.Key == Lists ? ListCatalog : () => string.Join("\n", r.Value.Text))).ToList();

    /// <summary>The server instructions of a host with a tool set, one step on each line (section 9.2 of the MCP1
    /// design). A host with edit tools gets the steps of the edits. A host without them gets the step that the server
    /// only reads. A host with the tool <c>save</c> also gets the steps of the save. A host that can run a play tool
    /// also gets the step of the play tools. The host without a window lists them, but it cannot play.</summary>
    public static string InstructionsFor(IEnumerable<StudioTool> tools, AgentHostKind host)
    {
        var list = tools.ToList();
        var runs = host == AgentHostKind.Editor ? ToolHosts.Editor : ToolHosts.Headless;
        return string.Join("\n", AgentTexts.InstructionsFor(list.Any(t => t.Kind == ToolKind.Edit),
            list.Any(t => t.Name == "save"), list.Any(t => t.Kind == ToolKind.Play && (t.Hosts & runs) != 0)));
    }

    /// <summary>The resource with a URI, or null.</summary>
    public static AgentResource? Find(string uri) => All.FirstOrDefault(r => r.Uri == uri);

    /// <summary>The data lists of the rule data: the id, the label, the section types that give the values, and if
    /// the list holds the items of one system or of one mission script. <c>list_values</c> then needs the argument
    /// <c>system</c> or <c>script</c>.</summary>
    private static string ListCatalog()
    {
        var lists = new JsonArray();
        foreach (var list in ListRules.Retail.Lists.Values.OrderBy(l => l.Id, StringComparer.Ordinal))
        {
            lists.Add(new JsonObject
            {
                ["id"] = list.Id,
                ["label"] = list.Label,
                ["sections"] = new JsonArray((list.Source?.Sections ?? []).Select(s => (JsonNode)s).ToArray()),
                ["one_system"] = list.Source is { Scoped: true, Role: null },
                ["one_script"] = list.Source is { Scoped: true, Role: not null },
                ["fixed_values"] = list.Enum != null,
            });
        }
        return new JsonObject { ["lists"] = lists }.ToJsonString(new JsonSerializerOptions(EditJson.Options) { WriteIndented = false });
    }
}
