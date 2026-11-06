using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>The texts of one tool.</summary>
/// <param name="Fields">The help of the arguments of this tool only, by argument name.</param>
internal sealed record ToolTexts(string Title, string Description, IReadOnlyDictionary<string, string> Fields);

/// <summary>The texts of one resource.</summary>
/// <param name="Text">The lines of a text resource, or none for a resource that the server makes from data.</param>
internal sealed record ResourceTexts(string Name, string Title, string Description, IReadOnlyList<string> Text);

/// <summary>The texts of one prompt.</summary>
/// <param name="Arguments">The help of each argument, in order. Each argument of a prompt is required.</param>
/// <param name="OptionalTools">The tools that the lines name, but that a host does not need to list the prompt. The
/// lines tell what to do without such a tool.</param>
/// <param name="Text">The lines of the message of the prompt. Each <c>{name}</c> is the value of an argument.</param>
internal sealed record PromptTexts(string Title, string Description, IReadOnlyList<(string Name, string Help)> Arguments,
    IReadOnlyList<string> OptionalTools, IReadOnlyList<string> Text);

/// <summary>The help of one mission command of the input schema of <c>mission_edit</c>.</summary>
/// <param name="Fields">The help of the fields of this command only, by JSON name.</param>
internal sealed record MissionCommandTexts(string Description, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// The texts of the agent tools in <c>agent-tools.json</c> (decision D726 of the MCP1 design): the server
/// instructions, the title and the description of each tool, the help of each argument, the texts of the host errors,
/// the texts of the resources, the texts of the prompts and the help of the mission commands. A unit test checks each
/// text with the STE rules of AGENTS.md. The texts use invented
/// names only, so no retail text gets into the repository.
/// </summary>
internal static class AgentTexts
{
    private const string ResourceName = "OpenLancer.Studio.Agent.agent-tools.json";

    private static readonly Lazy<Data> Loaded = new(Load);

    private sealed record Data(IReadOnlyDictionary<string, IReadOnlyList<string>> Instructions,
        IReadOnlyDictionary<string, string> Fields,
        IReadOnlyDictionary<string, ToolTexts> Tools, IReadOnlyDictionary<string, string> Errors,
        IReadOnlyDictionary<string, ResourceTexts> Resources, IReadOnlyDictionary<string, PromptTexts> Prompts,
        IReadOnlyDictionary<string, string> Results, IReadOnlyDictionary<string, string> MissionFields,
        IReadOnlyDictionary<string, MissionCommandTexts> MissionCommands,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MissionParts);

    /// <summary>The server instructions: the order of work in short STE steps (section 9.2 of the MCP1 design). The
    /// group <c>all</c> is for each host, <c>edit</c> for a host with edit tools, and <c>read_only</c> for a host
    /// without them. The group <c>save</c> is for a host with the tool <c>save</c>, and the group <c>play</c> for a host
    /// that can play.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Instructions => Loaded.Value.Instructions;

    /// <summary>The server instructions of a host, in order.</summary>
    /// <param name="edits">True for a host with edit tools.</param>
    /// <param name="save">True for a host with the tool <c>save</c>.</param>
    /// <param name="play">True for a host with a play tool that it can run.</param>
    public static IReadOnlyList<string> InstructionsFor(bool edits, bool save, bool play) =>
    [
        .. Instructions["all"], .. Instructions[edits ? "edit" : "read_only"], .. save ? Instructions["save"] : [],
        .. play ? Instructions["play"] : [],
    ];

    /// <summary>The help of the arguments that many tools share, by argument name.</summary>
    public static IReadOnlyDictionary<string, string> SharedFields => Loaded.Value.Fields;

    /// <summary>The texts of each tool, by tool name.</summary>
    public static IReadOnlyDictionary<string, ToolTexts> Tools => Loaded.Value.Tools;

    /// <summary>The texts of the host errors, by key.</summary>
    public static IReadOnlyDictionary<string, string> Errors => Loaded.Value.Errors;

    /// <summary>The texts of each resource, by URI.</summary>
    public static IReadOnlyDictionary<string, ResourceTexts> Resources => Loaded.Value.Resources;

    /// <summary>The texts of each prompt, by prompt name, in the order of the file.</summary>
    public static IReadOnlyDictionary<string, PromptTexts> Prompts => Loaded.Value.Prompts;

    /// <summary>The help of the properties of the results, by JSON name, or by the type name and the JSON name, for
    /// example <c>RawLineMark.line</c>.</summary>
    public static IReadOnlyDictionary<string, string> Results => Loaded.Value.Results;

    /// <summary>The help of the fields that many mission commands share, by JSON name (section 9.1 of the MCP1
    /// design).</summary>
    public static IReadOnlyDictionary<string, string> MissionFields => Loaded.Value.MissionFields;

    /// <summary>The help of each mission command of the input schema of <c>mission_edit</c>, by kind, for example
    /// <c>add_trigger</c>.</summary>
    public static IReadOnlyDictionary<string, MissionCommandTexts> MissionCommands => Loaded.Value.MissionCommands;

    /// <summary>The help of the fields of the objects in the mission commands, by type name and then by JSON name, for
    /// example <c>RowRef</c> and <c>expect_key</c>. The type <c>MissionCommand</c> gives the help of the field
    /// <c>kind</c> of each command.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MissionParts => Loaded.Value.MissionParts;

    /// <summary>
    /// The help of a type of the mission commands, or of one of its JSON fields, for the input schema of
    /// <c>mission_edit</c> (see <c>MissionJson.Schema</c>). A command type gives the description of its command without a
    /// field, and the help of the command, else the shared help, for a field. Another type gives the help of
    /// <see cref="MissionParts"/>. Null when the file has no help.
    /// </summary>
    public static string? MissionHelp(Type type, string? field)
    {
        if (type != typeof(MissionCommand) &&
            typeof(MissionCommand).IsAssignableFrom(type))
        {
            if (!MissionCommands.TryGetValue(MissionJson.KindOf(type), out var command))
                return null;
            return field == null ? command.Description
                : command.Fields.TryGetValue(field, out var own) ? own
                : field == "kind" ? MissionHelp(typeof(MissionCommand), field)
                : MissionFields.GetValueOrDefault(field);
        }
        return field != null && MissionParts.TryGetValue(TypeName(type), out var part) ? part.GetValueOrDefault(field) : null;
    }

    /// <summary>The texts of a tool.</summary>
    /// <exception cref="KeyNotFoundException">The file has no texts for the tool.</exception>
    public static ToolTexts Tool(string name) => Tools.TryGetValue(name, out var texts)
        ? texts
        : throw new KeyNotFoundException($"agent-tools.json has no texts for the tool {name}.");

    /// <summary>The help of an argument: the help of the tool, else the shared help.</summary>
    /// <exception cref="KeyNotFoundException">The file has no help for the argument.</exception>
    public static string Field(string tool, string field) =>
        Tool(tool).Fields.TryGetValue(field, out var own) ? own
        : SharedFields.TryGetValue(field, out var shared) ? shared
        : throw new KeyNotFoundException($"agent-tools.json has no help for the argument {field} of {tool}.");

    /// <summary>The help of a property of a result: the help of the property of its type, else the help of the JSON
    /// name, or null when the file has neither.</summary>
    /// <param name="owner">The type that declares the property, or null for a property of each result.</param>
    public static string? Result(Type? owner, string name)
    {
        if (owner != null && Results.TryGetValue(TypeName(owner) + "." + name, out var own))
            return own;
        return Results.GetValueOrDefault(name);
    }

    /// <summary>The name of a type without the arity of a generic type, for example <c>Page</c>.</summary>
    public static string TypeName(Type type) => type.Name.Split('`')[0];

    /// <summary>The text of a host error, with each <c>{name}</c> replaced by its value.</summary>
    /// <exception cref="KeyNotFoundException">The file has no text with the key.</exception>
    public static string Error(string key, params (string Name, object Value)[] values)
    {
        if (!Errors.TryGetValue(key, out var text))
            throw new KeyNotFoundException($"agent-tools.json has no error text {key}.");
        foreach (var (name, value) in values)
            text = text.Replace("{" + name + "}", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        return text;
    }

    private static Data Load()
    {
        using var stream = typeof(AgentTexts).Assembly.GetManifestResourceStream(ResourceName) ??
                           throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        using var json = JsonDocument.Parse(reader.ReadToEnd());
        var root = json.RootElement;
        return new Data(
            root.GetProperty("instructions").EnumerateObject().ToDictionary(p => p.Name,
                p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(e => e.GetString()!).ToList()),
            Strings(root.GetProperty("fields")),
            root.GetProperty("tools").EnumerateObject().ToDictionary(p => p.Name, p => new ToolTexts(
                p.Value.GetProperty("title").GetString()!,
                p.Value.GetProperty("description").GetString()!,
                p.Value.TryGetProperty("fields", out var fields) ? Strings(fields) : new Dictionary<string, string>())),
            Strings(root.GetProperty("errors")),
            root.GetProperty("resources").EnumerateObject().ToDictionary(p => p.Name, p => new ResourceTexts(
                p.Value.GetProperty("name").GetString()!,
                p.Value.GetProperty("title").GetString()!,
                p.Value.GetProperty("description").GetString()!,
                p.Value.GetProperty("text").EnumerateArray().Select(e => e.GetString()!).ToList())),
            root.GetProperty("prompts").EnumerateObject().ToDictionary(p => p.Name, p => new PromptTexts(
                p.Value.GetProperty("title").GetString()!,
                p.Value.GetProperty("description").GetString()!,
                p.Value.GetProperty("arguments").EnumerateObject().Select(a => (a.Name, a.Value.GetString()!)).ToList(),
                p.Value.TryGetProperty("optional_tools", out var optional)
                    ? optional.EnumerateArray().Select(e => e.GetString()!).ToList()
                    : [],
                p.Value.GetProperty("text").EnumerateArray().Select(e => e.GetString()!).ToList())),
            Strings(root.GetProperty("results")),
            Strings(root.GetProperty("mission_fields")),
            root.GetProperty("mission_commands").EnumerateObject().ToDictionary(p => p.Name, p => new MissionCommandTexts(
                p.Value.GetProperty("description").GetString()!,
                p.Value.TryGetProperty("fields", out var fields) ? Strings(fields) : new Dictionary<string, string>())),
            root.GetProperty("mission_parts").EnumerateObject().ToDictionary(p => p.Name,
                p => (IReadOnlyDictionary<string, string>)Strings(p.Value)));
    }

    private static Dictionary<string, string> Strings(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
}
