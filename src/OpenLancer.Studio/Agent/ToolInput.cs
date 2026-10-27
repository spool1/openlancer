using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// One argument of a tool: its JSON Schema and its check. The help text of the schema comes from
/// <c>agent-tools.json</c>. The check runs before the handler, so a handler reads only values that fit the schema
/// (section 4, item 5 of the MCP1 design: servers must check the inputs).
/// </summary>
internal abstract class ToolParam(string name, bool required)
{
    public string Name { get; } = name;

    public bool Required { get; } = required;

    /// <summary>The schema of the value, without the help text.</summary>
    public abstract JsonObject Schema(string tool);

    /// <summary>What the value must be, for the error text, for example "a whole number from 1".</summary>
    public abstract string Expected { get; }

    /// <summary>True when the value fits the argument.</summary>
    public abstract bool Fits(JsonNode value);
}

/// <summary>A text, optionally one of a fixed set of words. A free text has at most <see cref="MaxLength"/>
/// characters, or the length that the argument gives.</summary>
/// <param name="length">The most characters of a free text, for example of the text of an infocard.</param>
internal sealed class TextParam(string name, bool required, IReadOnlyList<string>? choices, string? fallback,
    int length = TextParam.MaxLength) : ToolParam(name, required)
{
    /// <summary>The most characters of a free text. A reference, a name or a filter of the data is much shorter. The
    /// limit keeps the work of one call on the thread of the session small, for example the search for the closest
    /// names of a name that does not resolve.</summary>
    public const int MaxLength = 1024;

    public IReadOnlyList<string>? Choices { get; } = choices;

    /// <summary>The value when the call does not give the argument, or null.</summary>
    public string? Default { get; } = fallback;

    public override JsonObject Schema(string tool)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (Choices != null)
        {
            schema["enum"] = new JsonArray(Choices.Select(c => (JsonNode)c).ToArray());
        }
        else
        {
            schema["minLength"] = 1;
            schema["maxLength"] = length;
        }
        if (Default != null)
            schema["default"] = Default;
        return schema;
    }

    public override string Expected => Choices == null
        ? $"a text of 1 to {length} characters"
        : "one of: " + string.Join(", ", Choices);

    public override bool Fits(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text &&
        (Choices == null ? text.Length <= length : Choices.Contains(text, StringComparer.Ordinal));
}

/// <summary>A whole number from a minimum.</summary>
/// <param name="fallback">The value when the call does not give the argument, for the schema and the help, or
/// null.</param>
internal sealed class WholeParam(string name, int minimum, int? fallback = null) : ToolParam(name, false)
{
    public int Minimum { get; } = minimum;

    /// <summary>The value when the call does not give the argument, or null.</summary>
    public int? Default { get; } = fallback;

    public override JsonObject Schema(string tool)
    {
        var schema = new JsonObject { ["type"] = "integer", ["minimum"] = Minimum };
        if (Default is { } value)
            schema["default"] = value;
        return schema;
    }

    public override string Expected => $"a whole number from {Minimum}";

    public override bool Fits(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue(out long number) && number >= Minimum;
}

/// <summary>A number from a minimum, and up to a maximum when the argument has one.</summary>
internal sealed class NumberParam(string name, bool required, double minimum, double maximum = double.PositiveInfinity)
    : ToolParam(name, required)
{
    public override JsonObject Schema(string tool)
    {
        var schema = new JsonObject { ["type"] = "number", ["minimum"] = minimum };
        if (double.IsFinite(maximum))
            schema["maximum"] = maximum;
        return schema;
    }

    public override string Expected => double.IsFinite(maximum)
        ? $"a number from {Text(minimum)} to {Text(maximum)}"
        : $"a number from {Text(minimum)}";

    public override bool Fits(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue(out double number) &&
        double.IsFinite(number) && number >= minimum && number <= maximum;

    private static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>True or false.</summary>
/// <param name="fallback">The value when the call does not give the argument.</param>
internal sealed class FlagParam(string name, bool fallback = false) : ToolParam(name, false)
{
    /// <summary>The value when the call does not give the argument.</summary>
    public bool Default { get; } = fallback;

    public override JsonObject Schema(string tool) => new() { ["type"] = "boolean", ["default"] = Default };

    public override string Expected => "true or false";

    public override bool Fits(JsonNode value) => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
}

/// <summary>A list of words from a fixed set, each word once.</summary>
internal sealed class ChoicesParam(string name, IReadOnlyList<string> choices) : ToolParam(name, false)
{
    public IReadOnlyList<string> Choices { get; } = choices;

    public override JsonObject Schema(string tool) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(Choices.Select(c => (JsonNode)c).ToArray()),
        },
        ["uniqueItems"] = true,
    };

    public override string Expected => "a list of: " + string.Join(", ", Choices);

    public override bool Fits(JsonNode value)
    {
        if (value is not JsonArray array)
            return false;
        var words = new HashSet<string>(StringComparer.Ordinal);
        return array.All(item => item?.GetValueKind() == JsonValueKind.String &&
                                 Choices.Contains(item.GetValue<string>(), StringComparer.Ordinal) &&
                                 words.Add(item.GetValue<string>()));
    }
}

/// <summary>A list of texts, for example item references, with at least one text. Each text has at most
/// <see cref="TextParam.MaxLength"/> characters.</summary>
internal sealed class TextListParam(string name, bool required) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = TextParam.MaxLength },
        ["minItems"] = 1,
    };

    public override string Expected => $"a list of texts of 1 to {TextParam.MaxLength} characters";

    public override bool Fits(JsonNode value) =>
        value is JsonArray { Count: > 0 } array && array.All(item =>
            item?.GetValueKind() == JsonValueKind.String && item.GetValue<string>() is { Length: > 0 and <= TextParam.MaxLength });
}

/// <summary>A point: three numbers, X, Y and Z in meters. Each number fits a <see cref="float"/>, and the argument can
/// give a bound for each number.</summary>
/// <param name="bound">The largest size of each number, or null for each number that a float holds.</param>
internal sealed class PointParam(string name, bool required, float? bound = null) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool)
    {
        var item = new JsonObject { ["type"] = "number" };
        if (bound is { } most)
        {
            item["minimum"] = -most;
            item["maximum"] = most;
        }
        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = item,
            ["minItems"] = 3,
            ["maxItems"] = 3,
        };
    }

    public override string Expected => bound is { } most
        ? $"three numbers from {(-most).ToString(CultureInfo.InvariantCulture)} to {most.ToString(CultureInfo.InvariantCulture)}"
        : "three numbers";

    public override bool Fits(JsonNode value) =>
        value is JsonArray { Count: 3 } array && array.All(n =>
            n?.GetValueKind() == JsonValueKind.Number && n.AsValue().TryGetValue(out double number) &&
            float.IsFinite((float)number) && Math.Abs(number) <= (bound ?? float.MaxValue));
}

/// <summary>
/// A list of objects with a <c>kind</c>, for example the inputs of <c>mission_dry_run</c>. The schema gives the kinds and
/// the fields of the items. The handler reads each item and gives the reason for an item that does not fit, because the
/// fields of an item depend on its kind.
/// </summary>
/// <param name="kinds">The words of <c>kind</c>.</param>
/// <param name="fields">The schema of each field of an item, without <c>kind</c>.</param>
internal sealed class ObjectsParam(string name, bool required, IReadOnlyCollection<string> kinds,
    IReadOnlyDictionary<string, JsonObject> fields) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool)
    {
        var properties = new JsonObject
        {
            ["kind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(kinds.Select(k => (JsonNode)k).ToArray()),
            },
        };
        foreach (var (field, schema) in fields)
            properties[field] = schema.DeepClone();
        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray("kind"),
                ["additionalProperties"] = false,
            },
        };
    }

    public override string Expected => "a list of objects, each with a kind";

    public override bool Fits(JsonNode value) => value is JsonArray array && array.All(item => item is JsonObject);
}

/// <summary>An object of field values by INI key, with at least one field, for example the fields of
/// <c>set_fields</c>. The command of the tool checks each value, so the schema takes each JSON value.</summary>
internal sealed class FieldsParam(string name, bool required) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool) => new() { ["type"] = "object", ["minProperties"] = 1 };

    public override string Expected => "an object with at least one field";

    public override bool Fits(JsonNode value) => value is JsonObject { Count: > 0 };
}

/// <summary>An object with arguments of its own.</summary>
internal sealed class GroupParam(string name, IReadOnlyList<ToolParam> members) : ToolParam(name, false)
{
    public IReadOnlyList<ToolParam> Members { get; } = members;

    public override JsonObject Schema(string tool) => ToolInput.ObjectSchema(tool, Members);

    public override string Expected => "an object with: " + string.Join(", ", Members.Select(m => m.Name));

    public override bool Fits(JsonNode value) => value is JsonObject;
}

/// <summary>
/// The arguments of a tool: the schema that <c>tools/list</c> gives, and the check of the arguments of a call. The
/// check gives <c>invalid_argument</c> with the detail <see cref="BadArgument"/> and the reason.
/// </summary>
internal sealed class ToolInput(params ToolParam[] parameters)
{
    /// <summary>The detail code of an argument that does not fit the schema.</summary>
    public const string BadArgument = "BadArgument";

    public IReadOnlyList<ToolParam> Parameters { get; } = parameters;

    /// <summary>A tool without arguments.</summary>
    public static readonly ToolInput None = new();

    /// <summary>The most characters of the name of an unknown argument in an error.</summary>
    private const int MaxShownName = 64;

    /// <summary>The schema of the arguments, with the help of each argument.</summary>
    public JsonObject Schema(string tool) => ObjectSchema(tool, Parameters);

    internal static JsonObject ObjectSchema(string tool, IReadOnlyList<ToolParam> parameters)
    {
        var properties = new JsonObject();
        foreach (var parameter in parameters)
        {
            var schema = parameter.Schema(tool);
            schema["description"] = AgentTexts.Field(tool, parameter.Name);
            properties[parameter.Name] = schema;
        }
        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (parameters.Any(p => p.Required))
            result["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray());
        return result;
    }

    /// <summary>Checks the arguments of a call. A null value counts as a missing argument.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> with the detail
    /// <see cref="BadArgument"/>.</exception>
    public ToolArgs Read(string tool, JsonObject? arguments) => Read(tool, Parameters, arguments, null);

    private static ToolArgs Read(string tool, IReadOnlyList<ToolParam> parameters, JsonObject? arguments, string? parent)
    {
        string Path(string name) => parent == null ? name : $"{parent}.{name}";
        var values = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var groups = new Dictionary<string, ToolArgs>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments ?? [])
        {
            var parameter = parameters.FirstOrDefault(p => p.Name == name);
            if (parameter == null)
            {
                // The name of an unknown argument can be long, so the error gives its start only.
                var shown = Path(name.Length > MaxShownName ? name[..MaxShownName] + "..." : name);
                throw Bad(shown, parameters.Count == 0
                    ? AgentTexts.Error("argument_none", ("tool", tool), ("name", shown))
                    : AgentTexts.Error("argument_unknown", ("tool", tool), ("name", shown),
                        ("names", string.Join(", ", parameters.Select(p => p.Name)))));
            }
            if (value == null)
                continue;
            if (!parameter.Fits(value))
                throw Bad(Path(name), AgentTexts.Error("argument_type", ("name", Path(name)), ("expected", parameter.Expected)));
            values[name] = value;
            if (parameter is GroupParam group)
                groups[name] = Read(tool, group.Members, (JsonObject)value, Path(name));
        }
        foreach (var parameter in parameters.Where(p => p.Required && !values.ContainsKey(p.Name)))
            throw Bad(Path(parameter.Name), AgentTexts.Error("argument_missing", ("name", Path(parameter.Name))));
        return new ToolArgs(parameters, values, groups);
    }

    /// <summary>The error of an argument that does not fit: <c>invalid_argument</c> with the detail
    /// <see cref="BadArgument"/> and the name of the argument.</summary>
    internal static EditException Bad(string field, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, BadArgument, message) { Field = field });
}

/// <summary>The checked arguments of a call. Each accessor gives the default of a missing argument.</summary>
internal sealed class ToolArgs(IReadOnlyList<ToolParam> parameters, IReadOnlyDictionary<string, JsonNode> values,
    IReadOnlyDictionary<string, ToolArgs> groups)
{
    /// <summary>True when the call gives the argument.</summary>
    public bool Has(string name) => values.ContainsKey(name);

    /// <summary>A text, the default of its argument, or null.</summary>
    public string? Text(string name) =>
        values.TryGetValue(name, out var value) ? value.GetValue<string>()
        : parameters.OfType<TextParam>().FirstOrDefault(p => p.Name == name)?.Default;

    /// <summary>A whole number, the default of its argument, or null.</summary>
    public long? Whole(string name) =>
        values.TryGetValue(name, out var value) ? value.GetValue<long>()
        : parameters.OfType<WholeParam>().FirstOrDefault(p => p.Name == name)?.Default;

    /// <summary>A number, or null.</summary>
    public double? Number(string name) => values.TryGetValue(name, out var value) ? value.GetValue<double>() : null;

    /// <summary>True or false. A missing argument gives the default of its argument.</summary>
    public bool Flag(string name) =>
        values.TryGetValue(name, out var value) ? value.GetValue<bool>()
        : parameters.OfType<FlagParam>().FirstOrDefault(p => p.Name == name)?.Default ?? false;

    /// <summary>The texts of a list, or null when the call does not give the list. Examples are the words of a
    /// <see cref="ChoicesParam"/> and the references of a <see cref="TextListParam"/>.</summary>
    public IReadOnlyList<string>? Texts(string name) =>
        values.TryGetValue(name, out var value) ? value.AsArray().Select(v => v!.GetValue<string>()).ToList() : null;

    /// <summary>A point, or null.</summary>
    public Vector3? Point(string name) => values.TryGetValue(name, out var value)
        ? new Vector3(value[0]!.GetValue<float>(), value[1]!.GetValue<float>(), value[2]!.GetValue<float>())
        : null;

    /// <summary>The JSON value of an argument, or null when the call does not give it.</summary>
    public JsonNode? Node(string name) => values.GetValueOrDefault(name);

    /// <summary>The arguments of an object argument, or null.</summary>
    public ToolArgs? Group(string name) => groups.GetValueOrDefault(name);
}
