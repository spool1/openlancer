using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Runtime.Missions;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// A resource of the agent server (section 9.3 of the MCP1 design): a text that the agent can read on request. The
/// resources hold OpenLancer's own texts, the rule data and, for a resource that reads the session, the data of the
/// user.
/// </summary>
/// <param name="Uri">The URI, for example <c>openlancer://guide/refs</c>.</param>
/// <param name="MimeType"><c>text/plain</c> or <c>application/json</c>.</param>
/// <param name="Session">True when <paramref name="Read"/> reads the host, so it must run on the thread that owns the
/// session (see <see cref="ToolQueue.ReadAsync"/>). A resource without it reads only the rule data and runs on any
/// thread.</param>
/// <param name="Read">Makes the text of the resource.</param>
public sealed record AgentResource(string Uri, string Name, string Title, string Description, string MimeType,
    bool Session, Func<AgentHost, string> Read);

/// <summary>A condition, an action or an order of the schema of the missions, for the resources
/// <c>openlancer://schema/mission-names</c> and <c>openlancer://schema/objlist-directives</c>. It has the fields of a
/// row of <c>describe_mission_name</c> that need no session.</summary>
/// <param name="Group">The action group of an action, the event of a condition, or <c>orders</c> for an order.</param>
/// <param name="Mark">What OpenLancer does with a line of the name.</param>
/// <param name="NoRetailExample">True when no retail script has the name (rule M28).</param>
/// <param name="Checked">False when no reader checked the meaning of the name.</param>
internal sealed record MissionNameRow(string Name, string Plain, MissionNameKind Kind, string Group,
    IReadOnlyList<MissionValueView> Values, EngineMark Mark, bool NoRetailExample, bool Checked);

/// <summary>An action group or an event of the conditions: its id and its short label.</summary>
internal sealed record MissionGroupRow(string Id, string Label);

/// <summary>The resource <c>openlancer://schema/mission-names</c>: the conditions and the actions, with the labels of
/// their events and action groups.</summary>
internal sealed record MissionNamesView(IReadOnlyList<MissionNameRow> Names, IReadOnlyList<MissionGroupRow> Events,
    IReadOnlyList<MissionGroupRow> Groups);

/// <summary>The resource <c>openlancer://schema/objlist-directives</c>: the orders of an order list.</summary>
internal sealed record MissionDirectivesView(IReadOnlyList<MissionNameRow> Directives);

/// <summary>A key of a mission section type.</summary>
/// <param name="Values">The values of a line of the key. A key with one value has one value with the name of the
/// key.</param>
/// <param name="Repeats">True for a key that has one line for each row, for example <c>label</c>.</param>
/// <param name="Unused">True for a key that the game reads but does not use.</param>
/// <param name="ShipKey">True for a ship key that must come before the formation and ship lines.</param>
internal sealed record MissionKeyRow(string Key, string Label, MissionValueKind Kind, IReadOnlyList<MissionValueView> Values,
    string? Help, bool Repeats, bool Unused, bool ShipKey)
{
    /// <summary>The values of each form of a key whose first value picks the form, for example the type of an objective,
    /// or null. <see cref="Values"/> are the values of the default form.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MissionValueView>>? Forms { get; init; }
}

/// <summary>A mission section type with its keys.</summary>
/// <param name="Kind">The kind of <c>mission_add_item</c> that adds a section of the type, or null.</param>
/// <param name="OpenKeys">True when the section also takes keys that the list does not have.</param>
/// <param name="Directives">True for an order list: its orders are in
/// <c>openlancer://schema/objlist-directives</c>.</param>
/// <param name="OrderSensitive">True when the game reads the lines in order.</param>
internal sealed record MissionSectionRow(string Type, string? Kind, IReadOnlyList<MissionKeyRow> Keys, bool OpenKeys,
    bool Directives, bool OrderSensitive);

/// <summary>The resource <c>openlancer://schema/mission-sections</c>: the section types of a mission script and the
/// ship build of the mission ship files.</summary>
internal sealed record MissionSectionsView(IReadOnlyList<MissionSectionRow> Sections, MissionSectionRow ShipBuild);

/// <summary>A template of Create in the Scene: <c>scene_create_object</c> takes its id as <c>template</c>.</summary>
/// <param name="Types">The <c>[Solar]</c> types of the archetypes of the template.</param>
/// <param name="Needs">A key that an archetype of the template must have, or null.</param>
internal sealed record SceneTemplateRow(string Id, string Label, IReadOnlyList<string> Types, string? Needs);

/// <summary>A builder of the Scene: a tool that makes a set of items in one step.</summary>
internal sealed record BuilderRow(string Tool, string Title, string Description);

/// <summary>The resource <c>openlancer://catalog/prefabs</c>: the behavior prefabs, the templates of Create, the
/// builders and the prop groups. Without a profile, it has the built-in prefabs and the rule data only.</summary>
internal sealed record PrefabCatalogView(IReadOnlyList<MissionPrefabView> Prefabs,
    IReadOnlyList<SceneTemplateRow> SceneTemplates, IReadOnlyList<BuilderRow> Builders,
    IReadOnlyList<PropGroupRow> PropGroups);

/// <summary>The resources of this build: the schema of the missions, the catalogs and the guides (section 9.3 of the
/// MCP1 design). The texts of each resource are in <c>agent-tools.json</c>.</summary>
public static class AgentResources
{
    private static readonly JsonSerializerOptions Compact = new(EditJson.Options) { WriteIndented = false };

    /// <summary>The tools of the builders of the catalog of the prefabs.</summary>
    private static readonly string[] Builders = ["scene_place_wreck", "scene_place_patrol_route"];

    /// <summary>The resources that the server makes from data, by URI, and if they read the session. Each other
    /// resource is the text of <c>agent-tools.json</c>.</summary>
    private static readonly Dictionary<string, (bool Session, Func<AgentHost, string> Read)> Data = new()
    {
        ["openlancer://schema/mission-names"] = (false, _ => MissionNames()),
        ["openlancer://schema/mission-sections"] = (false, _ => MissionSections()),
        ["openlancer://schema/objlist-directives"] = (false, _ => Directives()),
        ["openlancer://catalog/prefabs"] = (true, PrefabCatalog),
        ["openlancer://catalog/lists"] = (false, _ => ListCatalog()),
    };

    /// <summary>The resources in the order of <c>resources/list</c>.</summary>
    public static IReadOnlyList<AgentResource> All { get; } = AgentTexts.Resources.Select(r =>
        Data.TryGetValue(r.Key, out var data)
            ? new AgentResource(r.Key, r.Value.Name, r.Value.Title, r.Value.Description, "application/json",
                data.Session, data.Read)
            : new AgentResource(r.Key, r.Value.Name, r.Value.Title, r.Value.Description, "text/plain", false,
                _ => string.Join("\n", r.Value.Text))).ToList();

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

    /// <summary>The 129 conditions and actions of the schema of the missions, with their values and the marks of the
    /// mission runtime of OpenLancer (the rows of <c>describe_mission_name</c> without the counts of the base
    /// game).</summary>
    private static string MissionNames()
    {
        var schema = MissionSchema.Retail;
        return Json(new MissionNamesView([.. schema.Conditions.Concat(schema.Actions).Select(Row)],
            [.. schema.Events.OrderBy(e => e.Order).Select(e => new MissionGroupRow(e.Id, e.Label))],
            [.. schema.Groups.OrderBy(g => g.Order).Select(g => new MissionGroupRow(g.Id, g.Chip))]));
    }

    /// <summary>The 23 orders of an order list (<c>[ObjList]</c>).</summary>
    private static string Directives() => Json(new MissionDirectivesView([.. MissionSchema.Retail.Directives.Select(Row)]));

    /// <summary>The 12 section types of a mission script and the ship build, with the keys of each one.</summary>
    private static string MissionSections()
    {
        var schema = MissionSchema.Retail;
        var kinds = MissionEditTools.ItemKinds.ToDictionary(MissionEditTools.SectionTypeOf, k => k,
            StringComparer.OrdinalIgnoreCase);
        MissionSectionRow Section(MissionSectionSpec spec) => new(spec.Type, kinds.GetValueOrDefault(spec.Type),
            [.. spec.Keys.Select(k => new MissionKeyRow(k.Key, k.Label, k.Kind,
                [.. k.ValueSpecs.Select(v => MissionQueries.ValueView(v, schema))], k.Help, k.List is { Values: false },
                k.Unused, k.ShipKey)
            {
                Forms = k.Forms?.ToDictionary(f => f.Key,
                    f => (IReadOnlyList<MissionValueView>)[.. f.Value.Select(v => MissionQueries.ValueView(v, schema))]),
            })],
            spec.OpenKeys, spec.Directives, spec.OrderSensitive);
        return Json(new MissionSectionsView([.. schema.Sections.Select(Section)], Section(schema.ShipBuild)));
    }

    /// <summary>The behavior prefabs of the library, the templates of Create, the builders of the Scene and the prop
    /// groups of the profile. Without a profile, it has the built-in prefabs and the rule data only. It reads the
    /// session, so it runs on the thread that owns the session.</summary>
    private static string PrefabCatalog(AgentHost host)
    {
        var session = host.Session;
        var templates = ListRules.Retail.SceneTemplates.Select(t => new SceneTemplateRow(t.Id, t.Label, t.Types, t.Needs));
        var builders = Builders.Select(b => new BuilderRow(b, AgentTexts.Tool(b).Title, AgentTexts.Tool(b).Description));
        return Json(new PrefabCatalogView(
            session?.Missions.Queries.ListPrefabs() ?? MissionQueries.ListBuiltinPrefabs(), [.. templates], [.. builders],
            session == null ? [] : [.. ItemQueries.Groups(session).Select(g => ItemQueries.GroupRow(session, g))]));
    }

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
        return new JsonObject { ["lists"] = lists }.ToJsonString(Compact);
    }

    /// <summary>A name of the schema with the mark of the mission runtime of OpenLancer, as the editor loads it.</summary>
    private static MissionNameRow Row(MissionName name) => new(name.Name, name.Plain, name.Kind, name.Group,
        [.. name.Values.Select(v => MissionQueries.ValueView(v, MissionSchema.Retail))],
        EngineCoverage.Instance.Of(name.Name), name.NoRetailExample, name.Checked);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Compact);
}
