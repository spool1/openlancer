using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The mission lines and values in the arguments of the mission tools (section 7.0 of the MCP1 design, decision D729).
/// A line is <c>{key, values, texts}</c> with the value names of <c>mission-schema.json</c>, as a line of
/// <c>mission_edit</c> is. The input schema of a tool with line arguments names the shared definition
/// <c>#/$defs/line</c> (section 7.8 of the MCP1 design).
/// </summary>
internal static class MissionLineInput
{
    /// <summary>The <c>$ref</c> of the shared definition of a line.</summary>
    public const string LineRef = "#/$defs/line";

    /// <summary>The definitions of a tool with line arguments: the shared definition <c>line</c>.</summary>
    public static JsonObject Definitions() => new()
    {
        ["line"] = new JsonObject
        {
            ["description"] = AgentTexts.SharedFields["$defs.line"],
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = TextParam.MaxLength,
                    ["description"] = AgentTexts.SharedFields["$defs.line.key"],
                },
                ["values"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = ValueSchema(),
                    ["description"] = AgentTexts.SharedFields["$defs.line.values"],
                },
                ["texts"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = AgentTexts.SharedFields["$defs.line.texts"],
                },
            },
            ["required"] = new JsonArray("key"),
            ["additionalProperties"] = false,
        },
    };

    /// <summary>The schema of one named value: a number, a text, a boolean, or a list of these for a position or a value
    /// that repeats. It is the schema of a value of <c>mission_edit</c>.</summary>
    public static JsonObject ValueSchema() => new()
    {
        ["type"] = new JsonArray("number", "string", "boolean", "array"),
        ["items"] = new JsonObject { ["type"] = new JsonArray("number", "string", "boolean") },
    };

    /// <summary>True when a JSON value has the form of a line: a key, and optional values by name and texts by
    /// position, with no other field.</summary>
    public static bool IsLine(JsonNode? node) =>
        node is JsonObject line && line["key"] is JsonValue key && key.GetValueKind() == JsonValueKind.String &&
        key.GetValue<string>() is { Length: > 0 and <= TextParam.MaxLength } &&
        line.All(field => field.Key switch
        {
            "key" => true,
            "values" => field.Value is JsonObject values && values.All(value => IsValue(value.Value)),
            "texts" => field.Value is JsonArray texts && texts.All(text => text?.GetValueKind() == JsonValueKind.String),
            _ => false,
        });

    /// <summary>True when a JSON value has the form of a named value (see <see cref="ValueSchema"/>).</summary>
    public static bool IsValue(JsonNode? node) => node is JsonArray items ? items.All(IsScalar) : IsScalar(node);

    private static bool IsScalar(JsonNode? node) =>
        node?.GetValueKind() is JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False;

    /// <summary>
    /// The line of a trigger in an argument, after the check of the schema of the missions for its place: the key, and
    /// each value by its kind. The command checks the line again when it runs. This check names the argument of the
    /// line in an error, for example <c>actions[2]</c>.
    /// </summary>
    /// <param name="node">A value that fits <see cref="IsLine"/>.</param>
    /// <param name="action">True for an action, false for a condition.</param>
    /// <exception cref="EditException">The key is not a condition or an action of the place, or a value is not valid.
    /// The error names the argument.</exception>
    public static LineSpec TriggerLine(JsonNode node, string field, bool action)
    {
        var line = node.Deserialize<LineSpec>(MissionJson.Options)!;
        EditTools.At(field, () => MissionValues.Build(MissionSchema.Retail, "Trigger",
            action ? LinePlace.Action : LinePlace.Condition, line));
        return line;
    }

    /// <summary>The named value of a value that fits <see cref="IsValue"/>.</summary>
    public static MissionValue Value(JsonNode node) => node.Deserialize<MissionValue>(MissionJson.Options)!;
}

/// <summary>A mission line (see <see cref="MissionLineInput"/>). The schema names the shared definition of the
/// tool.</summary>
internal sealed class LineParam(string name, bool required) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool) => new() { ["$ref"] = MissionLineInput.LineRef };

    public override string Expected => "a line: an object with a key, and values by name or texts by position";

    public override bool Fits(JsonNode value) => MissionLineInput.IsLine(value);
}

/// <summary>A list of mission lines, for example the actions of a new trigger, with at most
/// <paramref name="maxItems"/> lines.</summary>
internal sealed class LinesParam(string name, int maxItems) : ToolParam(name, false)
{
    public override JsonObject Schema(string tool) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["$ref"] = MissionLineInput.LineRef },
        ["maxItems"] = maxItems,
    };

    public override string Expected =>
        $"a list of at most {maxItems} lines, each an object with a key, and values by name or texts by position";

    public override bool Fits(JsonNode value) =>
        value is JsonArray lines && lines.Count <= maxItems && lines.All(MissionLineInput.IsLine);
}

/// <summary>Named values of a mission line, with at least one value, for example the values of
/// <c>mission_set_action</c>.</summary>
internal sealed class MissionValuesParam(string name, bool required) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool) => new()
    {
        ["type"] = "object",
        ["minProperties"] = 1,
        ["additionalProperties"] = MissionLineInput.ValueSchema(),
    };

    public override string Expected =>
        "an object with at least one value by name: a number, a text, true, false or a list of these";

    public override bool Fits(JsonNode value) =>
        value is JsonObject { Count: > 0 } values && values.All(v => MissionLineInput.IsValue(v.Value));
}
