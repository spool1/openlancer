using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The tool <c>mission_place_prefab</c> (section 7.4 of the MCP1 design). It places a linked instance of a behavior
/// prefab with <see cref="PlacePrefabInstance"/> (M6b-3). The value of each field has the form of its field kind, with
/// named values, as the fields of the mission studio have (UI rule 3, section 7.0 of the MCP1 design). The tool writes
/// the rows in their text form (<see cref="PrefabRow"/>) only for the command.
/// </summary>
internal static partial class MissionEditTools
{
    public static ToolInput PlacePrefabInput { get; } = new(EditTools.Input(false,
        new TextParam("script", true, null, null),
        new TextParam("prefab", true, null, null),
        new TextParam("id", false, null, null, MissionNames.MaxName, NamePattern),
        new PrefabFieldsParam("fields"),
        new TextListsParam("outputs", false)).Parameters.ToArray());

    /// <summary>
    /// The tool <c>mission_place_prefab</c>: <see cref="PlacePrefabInstance"/> with the values of the fields and the
    /// triggers of the outputs, in one request. A field without a value takes the default of the prefab. The label is the
    /// label of the drop of a prefab on the graph.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a field or an output that the
    /// prefab does not have, a value in the wrong form, or a trigger of another script.
    /// <see cref="EditErrorKind.InvalidValue"/> for a value that the field refuses. Each error of the edit call, for
    /// example <see cref="EditErrorKind.NotFound"/> for a prefab that does not exist.</exception>
    public static ToolStep PlacePrefab(ToolCall call)
    {
        var args = call.Args;
        var script = EditTools.At("script", () => ScriptRef.Parse(args.Text("script")!));
        var prefab = EditTools.At("prefab", () => PrefabRef.Parse(args.Text("prefab")!));
        return EditTools.Apply(call, scope =>
        {
            // The prefab now, so a step of edit_batch sees a change of the template by a step before it.
            var asset = scope.Session.Missions.Prefab(prefab);
            var template = asset?.Template;
            var place = new PlacePrefabInstance(script, prefab)
            {
                Id = args.Text("id"),
                // Without a template, the command refuses with the reason: no such prefab, or a problem of its file.
                Values = template == null ? [] : FieldInputs(template, args.Node("fields")?.AsObject(), script.Path),
                Ports = template == null ? [] : Outputs(template, args.Node("outputs")?.AsObject(), script.Path),
            };
            scope.RunMission(new MissionBatch(asset == null ? "" : $"Place prefab {asset.Name}", [place]), error =>
                error.Detail switch
                {
                    MissionErrors.PrefabNotFound or MissionErrors.PrefabProblems => "prefab",
                    // The id gives the names of the members too.
                    MissionErrors.NameInvalid or MissionErrors.NameTaken or MissionErrors.NameIdTaken => "id",
                    // A line of the compile that the game does not read as its value, for example a point that is not
                    // whole, or a value that a line of a binary script cannot store. The value of a field gives the line.
                    MissionErrors.TemplateLine or MissionErrors.PatchText or MissionErrors.FieldValue
                        or MissionErrors.FieldNotFound => "fields",
                    MissionErrors.PortNotFound => "outputs",
                    _ => "script",
                });
        });
    }

    /// <summary>The values of the fields for the command, in the order of the arguments. Each value gets the check of
    /// the command first, so an error names the field, for example <c>fields.delay</c>.</summary>
    /// <exception cref="EditException">The prefab has no such field, or a value does not fit its field.</exception>
    private static List<PrefabFieldInput> FieldInputs(PrefabTemplate template, JsonObject? fields, string script)
    {
        var inputs = new List<PrefabFieldInput>();
        foreach (var (id, value) in fields ?? [])
        {
            var argument = $"fields.{id}";
            var field = template.Field(id.Trim()) ?? throw new EditException(
                MissionErrors.FieldNotFoundError(template.Name, id, template.Fields.Select(f => f.Id)) with { Field = argument });
            var rows = Rows(field, value, argument, script);
            if (PrefabValues.ValueProblem(field, rows.Select(r => PrefabRow.Parse(field.Kind, r)).ToList()) is { } problem)
                throw new EditException(MissionErrors.FieldValueError(field.Label, problem) with { Field = argument });
            inputs.Add(new PrefabFieldInput(field.Id, rows));
        }
        return inputs;
    }

    /// <summary>
    /// The rows of a field value in their text form, from the form of its field kind:
    /// <list type="bullet">
    /// <item>a value field: one value, or a list of numbers for a position or a rotation.</item>
    /// <item>a list field: a list of rows, each an object of values by column id.</item>
    /// <item>a trigger list: a list of triggers.</item>
    /// <item>a condition field: one condition line.</item>
    /// <item>a lines field: a list of action lines or order lines.</item>
    /// </list>
    /// <c>null</c> gives no rows. A trigger is a nickname, or a <c>trg:</c> reference of the script.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a value in another form, a column
    /// that the field does not have, or a trigger of another script. <see cref="EditErrorKind.InvalidValue"/> for a line or
    /// a text that is not valid.</exception>
    private static IReadOnlyList<string> Rows(PrefabFieldDef field, JsonNode? value, string argument, string script)
    {
        if (value == null)
            return [];
        switch (field.Kind)
        {
            case PrefabFieldKind.Value when IsValueOf(field.ValueKind, value):
                return [Row(Cell(field.ValueKind, value!, argument, script), argument)];
            case PrefabFieldKind.List when value is JsonArray rows && rows.All(r => r is JsonObject):
                return rows.Select((row, i) => ListRow(field, (JsonObject)row!, $"{argument}[{i}]", script)).ToList();
            case PrefabFieldKind.Triggers when value is JsonArray triggers && triggers.All(IsText):
                return triggers.Select((t, i) => PrefabTrigger(t!.GetValue<string>(), script, $"{argument}[{i}]")).ToList();
            case PrefabFieldKind.Condition when MissionLineInput.IsLine(value):
                return [LineRow(value!, argument, LinePlace.Condition)];
            case PrefabFieldKind.Lines when value is JsonArray lines && lines.All(MissionLineInput.IsLine):
                return lines.Select((line, i) => LineRow(line!, $"{argument}[{i}]",
                    MissionSchema.Retail.Directive(line!["key"]!.GetValue<string>()) != null ? LinePlace.Order : LinePlace.Action))
                    .ToList();
            default:
                throw ToolInput.Bad(argument, AgentTexts.Error("prefab_field_form", ("field", field.Id),
                    ("form", FormOf(field))));
        }
    }

    /// <summary>The form of the value of a field kind, for the error of a value in another form.</summary>
    private static string FormOf(PrefabFieldDef field) => field.Kind switch
    {
        PrefabFieldKind.List => AgentTexts.Error("prefab_form_list",
            ("columns", string.Join(", ", field.Columns.Select(c => c.Id)))),
        PrefabFieldKind.Triggers => AgentTexts.Error("prefab_form_triggers"),
        PrefabFieldKind.Condition => AgentTexts.Error("prefab_form_condition"),
        PrefabFieldKind.Lines => AgentTexts.Error("prefab_form_lines"),
        _ => AgentTexts.Error("prefab_form_value"),
    };

    /// <summary>A row of a list field: the values of its columns in the order of the columns.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a column that the field does not
    /// have, or a column without a value.</exception>
    private static string ListRow(PrefabFieldDef field, JsonObject row, string argument, string script)
    {
        var columns = string.Join(", ", field.Columns.Select(c => c.Id));
        if (row.Select(c => c.Key).FirstOrDefault(key => field.Columns.All(c => c.Id != key)) is { } unknown)
        {
            throw ToolInput.Bad($"{argument}.{unknown}", AgentTexts.Error("prefab_field_column", ("column", unknown),
                ("field", field.Id), ("columns", columns)));
        }
        var texts = new List<string>();
        foreach (var column in field.Columns)
        {
            var at = $"{argument}.{column.Id}";
            if (row[column.Id] is not { } cell || !IsValueOf(column.Kind, cell))
                throw ToolInput.Bad(at, AgentTexts.Error("prefab_field_cell", ("column", column.Id), ("columns", columns)));
            texts.AddRange(Cell(column.Kind, cell, at, script));
        }
        return Row(texts, argument);
    }

    /// <summary>True when a JSON value has the form of one value of a kind: one value, or a list of values only for a
    /// kind of more than one value, a position or a rotation.</summary>
    private static bool IsValueOf(MissionValueKind kind, JsonNode? value) =>
        MissionLineInput.IsValue(value) && (value is not JsonArray || PrefabRow.WidthOf(kind) > 1);

    /// <summary>The texts of one value of a kind (see <see cref="IsValueOf"/>): a trigger by its nickname, the numbers of
    /// a position or a rotation, or one text. The check of the field gives the reason for a value of the wrong
    /// kind.</summary>
    private static IReadOnlyList<string> Cell(MissionValueKind kind, JsonNode value, string argument, string script) =>
        value is JsonArray items ? items.Select(i => MissionLineInput.Value(i!).Scalar!).ToList()
        : kind == MissionValueKind.Trigger && IsText(value) ? [PrefabTrigger(value.GetValue<string>(), script, argument)]
        : [MissionLineInput.Value(value).Scalar!];

    /// <summary>The text form of a row of values. Each text gets the check of a patch line first, so a comma cannot move
    /// a value to the next column. Then the row reads back as a patch line of a text file, as the compile reads it. So a
    /// number that a float cannot hold, for example 1e300, names the argument too. An empty text gets the reason of the
    /// check of the field.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidValue"/>: a text holds a comma, a semicolon or a
    /// control character, or a patch line cannot hold a value.</exception>
    private static string Row(IReadOnlyList<string> texts, string argument)
    {
        foreach (var text in texts)
            EditTools.At(argument, () => MissionValues.CheckText(text));
        if (texts.Count > 0 && texts.All(t => t.Length > 0))
            EditTools.At(argument, () => MissionValues.Entry(new Section("Row"), "row", texts, null, binary: false));
        return PrefabRow.Of([.. texts]).Format();
    }

    /// <summary>The text form of a row of a condition field or a lines field: the line with the key of the schema and
    /// the texts of its values.</summary>
    private static string LineRow(JsonNode line, string argument, LinePlace place)
    {
        var (key, texts) = MissionLineInput.LineTexts(line, argument, place);
        return PrefabRow.Line(key, [.. texts]).Format();
    }

    /// <summary>The triggers of each output, in the order of the arguments.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for an output that the prefab does not
    /// have, or a trigger of another script.</exception>
    private static List<InstancePort> Outputs(PrefabTemplate template, JsonObject? outputs, string script)
    {
        var ports = new List<InstancePort>();
        foreach (var (name, triggers) in outputs ?? [])
        {
            var argument = $"outputs.{name}";
            var port = template.Port(name.Trim()) ?? throw new EditException(
                MissionErrors.PortNotFoundError(template.Name, name, template.Ports.Select(p => p.Id)) with { Field = argument });
            ports.AddRange(triggers!.AsArray().Select((t, i) =>
                new InstancePort(port.Id, PrefabTrigger(t!.GetValue<string>(), script, $"{argument}[{i}]"))));
        }
        return ports;
    }

    /// <summary>The nickname of a trigger of the script in a field or an output (see <see cref="NameIn"/>), after the
    /// check of a patch line.</summary>
    private static string PrefabTrigger(string text, string script, string argument)
    {
        var name = NameIn(text, script, argument);
        EditTools.At(argument, () => MissionValues.CheckText(name));
        return name;
    }

    private static bool IsText(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String;
}
