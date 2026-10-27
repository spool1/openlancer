using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The program that serves the agent tools (section 5.4 of the MCP1 design).</summary>
public enum AgentHostKind
{
    /// <summary>The agent server of the editor. It shares the session of the open profile with the user.</summary>
    Editor,

    /// <summary><c>oltool mcp serve</c>. It has its own session and no window.</summary>
    Headless,
}

/// <summary>The hosts that have a tool. "E" in the tool catalog of the MCP1 design marks an editor tool.</summary>
[Flags]
public enum ToolHosts
{
    Editor = 1,
    Headless = 2,
    Both = Editor | Headless,
}

/// <summary>
/// The groups of the tools (decision D725 of the MCP1 design). The user can hide each group except
/// <see cref="Studio"/>. A tool of a hidden group stays in the tool list and gives <c>not_available</c>.
/// </summary>
public enum ToolGroup
{
    /// <summary>The tools of the session, the checks and the reads of each kind of item. The user cannot hide
    /// them.</summary>
    Studio,

    /// <summary>The star systems, their items, the assets and the prop groups.</summary>
    Scene,

    /// <summary>The mission scripts.</summary>
    Missions,

    /// <summary>The cutscene scripts.</summary>
    Cutscenes,
}

/// <summary>
/// What a tool does with the session (review finding R8 of MCP1-2). Pause, the steps of <c>edit_batch</c>, the tool set
/// of a host that only reads, the Console hooks of the editor and the hints read it.
/// </summary>
public enum ToolKind
{
    /// <summary>The tool reads and changes nothing, for example <c>list_items</c>.</summary>
    Query,

    /// <summary>The tool changes what the editor shows or gives an image, and changes no data, for example
    /// <c>open_view</c>.</summary>
    View,

    /// <summary>The tool changes the data in one entry of the edit history, for example <c>set_fields</c>. Only such a
    /// tool can be a step of <c>edit_batch</c>.</summary>
    Edit,

    /// <summary>The tool writes files at once, outside the edit history, for example the save of a prop
    /// group.</summary>
    AssetWrite,

    /// <summary>The tool works on the session as a whole: the save, the undo, the redo, the batch, the export and the
    /// publish checklist.</summary>
    Session,

    /// <summary>The tool starts or stops a play.</summary>
    Play,
}

/// <summary>The hints of a tool for the client (section 7.0 of the MCP1 design). No tool reaches outside the profile,
/// so the hint "open world" is always false.</summary>
/// <param name="ReadOnly">True when the tool changes nothing.</param>
/// <param name="Destructive">True when the tool can remove data or write files.</param>
/// <param name="Idempotent">True when a second call with the same arguments changes nothing more.</param>
public sealed record ToolHints(bool ReadOnly, bool Destructive, bool Idempotent)
{
    /// <summary>The hints of a query tool.</summary>
    public static readonly ToolHints Query = new(true, false, true);

    /// <summary>The hints of a tool that changes what the editor shows and changes no data, for example
    /// <c>open_view</c>.</summary>
    public static readonly ToolHints View = new(false, false, true);

    /// <summary>The hints of a tool that can change or remove data, for example <c>set_fields</c>. A second call with
    /// the same arguments changes nothing more.</summary>
    public static readonly ToolHints ChangingIdempotent = new(false, true, true);

    /// <summary>The hints of a tool that can change or remove data.</summary>
    public static readonly ToolHints Changing = new(false, true, false);

    /// <summary>The hints of a tool that only adds data.</summary>
    public static readonly ToolHints Adding = new(false, false, false);
}

/// <summary>
/// One row of the agent tool table (section 5.3 of the MCP1 design). The row has no type of the MCP SDK, so a change
/// of the protocol touches only the SDK binding (risk R1). The title, the description and the help of each argument
/// come from <c>agent-tools.json</c> (decision D726).
/// </summary>
public sealed class StudioTool
{
    private JsonObject? inputSchema;
    private JsonObject? outputSchema;

    /// <param name="output">The type of the structured result. The output schema comes from it.</param>
    /// <param name="run">The session part of a call. It runs on the thread that owns the session.</param>
    internal StudioTool(string name, ToolKind kind, ToolGroup group, ToolHosts hosts, ToolHints hints, ToolInput input,
        Type output, Func<ToolCall, ToolStep> run)
    {
        Name = name;
        Kind = kind;
        Group = group;
        Hosts = hosts;
        Hints = hints;
        Input = input;
        Output = output;
        Run = run;
    }

    /// <summary>The name, for example <c>list_items</c>. The client adds its own prefix.</summary>
    public string Name { get; }

    public ToolKind Kind { get; }

    /// <summary>True when Pause refuses the tool (decision D734 of the MCP1 design): a tool that changes the data, writes
    /// files or starts a play. A query and a view tool still run.</summary>
    public bool Pausable => Kind is ToolKind.Edit or ToolKind.AssetWrite or ToolKind.Session or ToolKind.Play;

    /// <summary>True when the tool changes the data or the files of the mod, or starts a play. The Console of the
    /// editor shows each refusal of such a tool.</summary>
    public bool Changes => Kind is not (ToolKind.Query or ToolKind.View);

    public ToolGroup Group { get; }

    public ToolHosts Hosts { get; }

    public ToolHints Hints { get; }

    /// <summary>The title for a person, for example "List the items of a system".</summary>
    public string Title => AgentTexts.Tool(Name).Title;

    /// <summary>What the tool does, in strict STE (section 9.1 of the MCP1 design).</summary>
    public string Description => AgentTexts.Tool(Name).Description;

    /// <summary>The JSON Schema of the arguments.</summary>
    public JsonObject InputSchema => (JsonObject)(inputSchema ??= Input.Schema(Name)).DeepClone();

    /// <summary>The JSON Schema of the structured content: the result of the tool, or a tool error (see
    /// <see cref="ResultSchema"/>).</summary>
    public JsonObject OutputSchema => (JsonObject)(outputSchema ??= ResultSchema(Output)).DeepClone();

    internal ToolInput Input { get; }

    internal Type Output { get; }

    internal Func<ToolCall, ToolStep> Run { get; }

    /// <summary>
    /// The output schema of a tool (section 5.7 of the MCP1 design). The structured content of each call fits it: the
    /// result of the tool, or a tool error. The specification says that structured content must fit the output schema,
    /// and some clients check it also for a tool error. So the schema has the properties of both objects, and its
    /// <c>anyOf</c> asks for the necessary properties of one of them. The properties stay at the root, so each
    /// <c>$ref</c> of the result schema stays valid. A result also gives <c>rev</c> (section 8.6, item 3).
    /// </summary>
    /// <exception cref="InvalidOperationException">The result type has a property of the error object.</exception>
    internal static JsonObject ResultSchema(Type output)
    {
        var schema = SchemaOf(output);
        var properties = (JsonObject)schema["properties"]!;
        properties[RevisionName] ??= Described(new JsonObject { ["type"] = "string" }, null, RevisionName);
        var result = schema["required"] is JsonArray required ? new JsonObject { ["required"] = required.DeepClone() } : new JsonObject();
        schema.Remove("required");
        var error = SchemaOf(typeof(EditError));
        foreach (var (name, property) in (JsonObject)error["properties"]!)
        {
            if (properties.ContainsKey(name))
                throw new InvalidOperationException($"The result {output.Name} has the property {name} of a tool error.");
            properties[name] = property!.DeepClone();
        }
        result["description"] = AgentTexts.Result(null, "result");
        schema["anyOf"] = new JsonArray(result, new JsonObject
        {
            ["description"] = AgentTexts.Result(null, "error"),
            ["required"] = error["required"]!.DeepClone(),
        });
        return schema;
    }

    /// <summary>The name of the revision in a result.</summary>
    internal const string RevisionName = "rev";

    /// <summary>
    /// The JSON Schema of a result type, in the JSON form of the command layer (snake case, enums as text). The
    /// serializer and the schema use the same options, so the schema fits each result. The command layer leaves out a
    /// null value (<see cref="EditJson.Options"/>), so a property that can be null is not required. The schema has no
    /// <c>format</c>, because a client can refuse a format that its validator does not know. A time is text in the ISO
    /// 8601 form. Each property gets its help from <c>agent-tools.json</c> (decision D726). A whole number that the
    /// command layer writes as text, for example the revision, is a text in the schema too.
    /// </summary>
    internal static JsonObject SchemaOf(Type type)
    {
        var options = new JsonSerializerOptions(EditJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.MakeReadOnly();
        var schema = (JsonObject)JsonSchemaExporter.GetJsonSchemaAsNode(options, type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (context.PropertyInfo is not { } property)
                    return node;
                if (property.CustomConverter is EditJson.LongAsText)
                    node = new JsonObject { ["type"] = "string" };
                return node is JsonObject obj && !obj.ContainsKey("$ref")
                    ? Described(obj, property.DeclaringType, property.Name)
                    : node;
            },
        });
        OptionalNulls(schema);
        return schema;
    }

    /// <summary>A schema with the help of a property of <c>agent-tools.json</c>, when the file has it. A unit test
    /// checks that each property of each output schema has a help.</summary>
    private static JsonObject Described(JsonObject schema, Type? owner, string name)
    {
        if (AgentTexts.Result(owner, name) is { } help)
            schema["description"] = help;
        return schema;
    }

    /// <summary>Removes each property that can be null from the <c>required</c> list of its object, and each
    /// <c>format</c>, in the whole schema.</summary>
    private static void OptionalNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("format");
                if (obj["required"] is JsonArray required && obj["properties"] is JsonObject properties)
                {
                    foreach (var name in required.Select(r => (string)r!).ToList())
                    {
                        if (properties[name]?["type"] is JsonArray types && types.Any(t => (string?)t == "null"))
                            required.Remove(required.First(r => (string?)r == name));
                    }
                    if (required.Count == 0)
                        obj.Remove("required");
                }
                foreach (var (_, child) in obj.ToList())
                    OptionalNulls(child);
                break;
            case JsonArray array:
                foreach (var child in array)
                    OptionalNulls(child);
                break;
        }
    }
}
