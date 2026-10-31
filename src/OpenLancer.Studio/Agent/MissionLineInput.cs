using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
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

    /// <summary>True when a JSON value has the form of a line: a key, and optional values by name or texts by
    /// position, with no other field. A line with both has no form, because the layer uses the texts and drops the
    /// values.</summary>
    public static bool IsLine(JsonNode? node) =>
        node is JsonObject line && line["key"] is JsonValue key && key.GetValueKind() == JsonValueKind.String &&
        key.GetValue<string>() is { Length: > 0 and <= TextParam.MaxLength } &&
        !(line.ContainsKey("values") && line.ContainsKey("texts")) &&
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

    /// <summary>The value text of a line whose key has no values, as the game data and the mission studio write
    /// it.</summary>
    private const string NoParams = "no_params";

    /// <summary>
    /// The line of a trigger in an argument, after the check of the schema of the missions for its place: the key, and
    /// each value by its kind. The command checks the line again when it runs. This check names the argument of the
    /// line in an error, for example <c>actions[2]</c>.
    /// </summary>
    /// <remarks>A line without values and without texts whose key has no values gets the text <see cref="NoParams"/>,
    /// as the first line of such a key in the mission studio does. Explicit texts stay as they are.</remarks>
    /// <param name="node">A value that fits <see cref="IsLine"/>.</param>
    /// <param name="action">True for an action, false for a condition.</param>
    /// <exception cref="EditException">The key is not a condition or an action of the place, or a value is not valid.
    /// The error names the argument.</exception>
    public static LineSpec TriggerLine(JsonNode node, string field, bool action) =>
        Line(node, field, "Trigger", action ? LinePlace.Action : LinePlace.Condition);

    /// <summary>An order of an order list in an argument: a line of one of the 23 directives, checked as
    /// <see cref="TriggerLine"/> checks a line of a trigger.</summary>
    /// <exception cref="EditException">The key is not a directive, or a value is not valid. The error names the
    /// argument.</exception>
    public static LineSpec OrderLine(JsonNode node, string field) => Line(node, field, "ObjList", LinePlace.Order);

    private static LineSpec Line(JsonNode node, string field, string sectionType, LinePlace place)
    {
        var line = node.Deserialize<LineSpec>(MissionJson.Options)!;
        var name = place == LinePlace.Order ? MissionSchema.Retail.Directive(line.Key) : MissionSchema.Retail.Name(line.Key);
        if (line is { Values.Count: 0, Texts: null } && name is { Values.Count: 0 })
            line = line with { Texts = [NoParams] };
        Check(sectionType, place, line, field);
        return line;
    }

    /// <summary>
    /// The checks of a line before the command runs: the check of the schema of the missions for its place, and the
    /// check that a patch line of a text file holds its values, for example a number that a float can hold. So the error
    /// names the argument of the line. The command checks the line again when it runs.
    /// </summary>
    /// <remarks>A binary script can refuse more values, for example a large whole number that it cannot store exactly.
    /// The command then gives the error.</remarks>
    /// <exception cref="EditException">The line is not valid. The error names <paramref name="field"/>.</exception>
    private static void Check(string sectionType, LinePlace place, LineSpec line, string field) => EditTools.At(field, () =>
    {
        var built = MissionValues.Build(MissionSchema.Retail, sectionType, place, line);
        MissionValues.Entry(new Section("Line"), built.Key, built.Texts, null, binary: false);
    });

    // ---- Fields of mission items ----

    /// <summary>The name of the form of a field value with its texts by position.</summary>
    private const string TextsForm = "texts";

    /// <summary>The forms of a field value of a mission item (see <see cref="FieldLines"/>), for the error of a value
    /// that has no such form.</summary>
    public const string FieldForms =
        "a value, an object of values by name, an object with texts, a list of these for a key that repeats, or null";

    /// <summary>The schema of a field value of a mission item (see <see cref="FieldLines"/>).</summary>
    public static JsonObject FieldSchema() => new()
    {
        ["type"] = new JsonArray("number", "string", "boolean", "array", "object", "null"),
    };

    /// <summary>True when a JSON value has a form of a field value of a mission item (see
    /// <see cref="FieldLines"/>).</summary>
    public static bool IsField(JsonNode? node) =>
        node is not JsonArray list ? node == null || IsForm(node) : list.All(item => item != null && IsForm(item));

    /// <summary>True when a JSON value is the form of one line: a value, an object of values by name, or an object with
    /// only the texts by position.</summary>
    private static bool IsForm(JsonNode node) => node is JsonObject named
        ? TextsOf(named) != null || named.All(value => IsValue(value.Value))
        : IsValue(node);

    /// <summary>The texts of the form <c>{"texts": [...]}</c>, or null for another form.</summary>
    private static IReadOnlyList<string>? TextsOf(JsonObject form) =>
        form.Count == 1 && form[TextsForm] is JsonArray texts && texts.All(t => t?.GetValueKind() == JsonValueKind.String)
            ? texts.Select(t => t!.GetValue<string>()).ToList()
            : null;

    /// <summary>
    /// The lines of one key of a mission item, from a field value in one of these forms:
    /// <list type="bullet">
    /// <item>a value: a number, a text, true or false, or a list of these for a position, a rotation or a value that
    /// repeats in one line. It is the value of a key with one value.</item>
    /// <item>an object of values by name, for example <c>{"heading": 90, "anchor": "my_station"}</c>.</item>
    /// <item>an object with only <c>texts</c>: the value texts by position. The layer checks only their form. An empty
    /// list is a line without values.</item>
    /// <item>for a key that repeats, a list of these forms: one line for each form. For a key with one position or
    /// rotation, a list of values is one line, for example <c>[0, 0, 100]</c>.</item>
    /// <item><c>null</c> or an empty list: no line. So <c>set_fields</c> removes the key.</item>
    /// </list>
    /// Each line gets the check of the schema of the missions, so an error names the field, for example
    /// <c>fields.label[1]</c>. The command checks each line again when it runs.
    /// </summary>
    /// <param name="sectionType">The section type of the item, for example <c>MsnShip</c> or <c>NPCShipArch</c>.</param>
    /// <param name="node">A value that fits <see cref="IsField"/>.</param>
    /// <param name="field">The name of the field in an error, for example <c>fields.label</c>.</param>
    /// <exception cref="EditException">The key is not a key of the section, the form does not fit the key, or a value is
    /// not valid. The error names the field.</exception>
    public static IReadOnlyList<LineSpec> FieldLines(string sectionType, string key, JsonNode? node, string field)
    {
        var (name, specs) = EditTools.At(field, () =>
            MissionValues.Key(MissionSchema.Retail, sectionType, LinePlace.Section, key));
        if (node is null or JsonArray { Count: 0 })
            return [];
        var repeats = SectionSpec(sectionType)?.Key(name)?.List is { Values: false };
        // A list of values for a key with one position or rotation is the value of one line, not a list of lines.
        var forms = repeats && node is JsonArray list && !(specs is [{ Width: > 1 }] && list.Count > 0 && IsValue(list))
            ? list.Select((form, i) => (Form: form!, Field: $"{field}[{i}]")).ToList()
            : [(Form: node, Field: field)];
        return forms.Select(f => FieldLine(sectionType, name, specs, f.Form, f.Field)).ToList();
    }

    /// <summary>The keys of a section type in the schema of the missions, or null for a type that it does not
    /// know.</summary>
    public static MissionSectionSpec? SectionSpec(string sectionType) => MissionIndex.Is(sectionType, "NPCShipArch")
        ? MissionSchema.Retail.ShipBuild
        : MissionSchema.Retail.Section(sectionType);

    /// <summary>The line of one form of a field value (see <see cref="FieldLines"/>).</summary>
    private static LineSpec FieldLine(string sectionType, string key, IReadOnlyList<MissionValueSpec> specs, JsonNode form,
        string field)
    {
        var line = form switch
        {
            // An empty list of texts is a line without values. The schema then gives each default, or refuses a value
            // that the key needs.
            JsonObject named when TextsOf(named) is { Count: 0 } => new LineSpec(key),
            JsonObject named when TextsOf(named) is { } texts => LineSpec.Raw(key, [.. texts]),
            JsonObject named => new LineSpec(key)
            {
                Values = named.ToDictionary(v => v.Key, v => Value(v.Value!), StringComparer.OrdinalIgnoreCase),
            },
            _ when !IsValue(form) => throw Refuse(field, AgentTexts.Error("mission_field_list", ("key", key))),
            _ when specs.Count == 1 => LineSpec.Of(key, (specs[0].Name, Value(form))),
            // A key that the schema does not know, in a section that reads keys of another grammar, has no value names.
            _ when specs.Count == 0 =>
                LineSpec.Raw(key, [.. (form is JsonArray items ? items.ToList() : [form]).Select(v => Value(v!).Scalar!)]),
            _ => throw Refuse(field, AgentTexts.Error("mission_field_named", ("key", key),
                ("names", string.Join(", ", specs.Select(s => s.Name))))),
        };
        Check(sectionType, LinePlace.Section, line, field);
        return line;
    }

    /// <summary>The error of a field value whose form does not fit its key.</summary>
    private static EditException Refuse(string field, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument, message) { Field = field });

    /// <summary>The named value of a value that fits <see cref="IsValue"/>.</summary>
    public static MissionValue Value(JsonNode node) => node.Deserialize<MissionValue>(MissionJson.Options)!;
}

/// <summary>A mission line (see <see cref="MissionLineInput"/>). The schema names the shared definition of the
/// tool.</summary>
internal sealed class LineParam(string name, bool required) : ToolParam(name, required)
{
    public override JsonObject Schema(string tool) => new() { ["$ref"] = MissionLineInput.LineRef };

    public override string Expected => "a line: an object with a key, and values by name or texts by position, not both";

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
        $"a list of at most {maxItems} lines, each an object with a key, and values by name or texts by position, not both";

    public override bool Fits(JsonNode value) =>
        value is JsonArray lines && lines.Count <= maxItems && lines.All(MissionLineInput.IsLine);
}

/// <summary>The fields of a mission item by the keys of its section, for example the fields of
/// <c>mission_add_item</c>. Each value has one of the forms of <see cref="MissionLineInput.FieldLines"/>.</summary>
internal sealed class MissionFieldsParam(string name) : ToolParam(name, false)
{
    public override JsonObject Schema(string tool) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = MissionLineInput.FieldSchema(),
    };

    public override string Expected => "an object of fields by key, each " + MissionLineInput.FieldForms;

    public override bool Fits(JsonNode value) =>
        value is JsonObject fields && fields.All(field => MissionLineInput.IsField(field.Value));
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
