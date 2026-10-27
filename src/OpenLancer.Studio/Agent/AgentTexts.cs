using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenLancer.Studio.Agent;

/// <summary>The texts of one tool.</summary>
/// <param name="Fields">The help of the arguments of this tool only, by argument name.</param>
internal sealed record ToolTexts(string Title, string Description, IReadOnlyDictionary<string, string> Fields);

/// <summary>The texts of one resource.</summary>
/// <param name="Text">The lines of a text resource, or none for a resource that the server makes from data.</param>
internal sealed record ResourceTexts(string Name, string Title, string Description, IReadOnlyList<string> Text);

/// <summary>
/// The texts of the agent tools in <c>agent-tools.json</c> (decision D726 of the MCP1 design): the server
/// instructions, the title and the description of each tool, the help of each argument, the texts of the host errors
/// and the texts of the resources. A unit test checks each text with the STE rules of AGENTS.md. The texts use invented
/// names only, so no retail text gets into the repository.
/// </summary>
internal static class AgentTexts
{
    private const string ResourceName = "OpenLancer.Studio.Agent.agent-tools.json";

    private static readonly Lazy<Data> Loaded = new(Load);

    private sealed record Data(IReadOnlyDictionary<string, IReadOnlyList<string>> Instructions,
        IReadOnlyDictionary<string, string> Fields,
        IReadOnlyDictionary<string, ToolTexts> Tools, IReadOnlyDictionary<string, string> Errors,
        IReadOnlyDictionary<string, ResourceTexts> Resources, IReadOnlyDictionary<string, string> Results);

    /// <summary>The server instructions: the order of work in short STE steps (section 9.2 of the MCP1 design). The
    /// group <c>all</c> is for each host, <c>edit</c> for a host with edit tools, and <c>read_only</c> for a host
    /// without them.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Instructions => Loaded.Value.Instructions;

    /// <summary>The server instructions of a host, in order.</summary>
    /// <param name="edits">True for a host with edit tools.</param>
    public static IReadOnlyList<string> InstructionsFor(bool edits) =>
        [.. Instructions["all"], .. Instructions[edits ? "edit" : "read_only"]];

    /// <summary>The help of the arguments that many tools share, by argument name.</summary>
    public static IReadOnlyDictionary<string, string> SharedFields => Loaded.Value.Fields;

    /// <summary>The texts of each tool, by tool name.</summary>
    public static IReadOnlyDictionary<string, ToolTexts> Tools => Loaded.Value.Tools;

    /// <summary>The texts of the host errors, by key.</summary>
    public static IReadOnlyDictionary<string, string> Errors => Loaded.Value.Errors;

    /// <summary>The texts of each resource, by URI.</summary>
    public static IReadOnlyDictionary<string, ResourceTexts> Resources => Loaded.Value.Resources;

    /// <summary>The help of the properties of the results, by JSON name, or by the type name and the JSON name, for
    /// example <c>RawLineMark.line</c>.</summary>
    public static IReadOnlyDictionary<string, string> Results => Loaded.Value.Results;

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
            Strings(root.GetProperty("results")));
    }

    private static Dictionary<string, string> Strings(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
}
