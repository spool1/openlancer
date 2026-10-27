using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The commands of the fields of a section (section 4.0.8 of the M6b design). They change the lines of a key, revert a
/// key or a section, and set the text of a text key. <see cref="SetFields"/> takes the request of an agent. The Inspector and the
/// agent tools call them, and each one gives the plan that its caller runs.
/// </summary>
/// <remarks>
/// <para>Each command wraps the session command of today and does not copy its rules. A command never throws for a
/// rule of the edit. It gives a plan that refuses, with the reason. A plan reads the edit view when it is made, so make
/// it and run it at once.</para>
/// <para>A field gesture of the Inspector runs one of these commands in each step of its
/// <see cref="CommandTransaction"/>, so the gesture keeps only its last step.</para>
/// </remarks>
public static class FieldCommands
{
    /// <summary>Why a text key cannot get an empty text.</summary>
    public const string EmptyText = "A text cannot be empty. To remove the line, use the INI rows of the section.";

    /// <summary>
    /// Plans the lines of a key from the text of their values, for example <c>1, 2.5, li_gun01</c> for one line. An
    /// empty list removes the key (see <see cref="RemoveKey"/>). Each line gets the values that a patch line with that
    /// text gives (see <see cref="EditCommand.SetLinesFromText"/>). A text that a patch line cannot hold refuses with
    /// the detail <see cref="MissionErrors.FieldValue"/>, for example a text with ';'.
    /// </summary>
    /// <param name="name">The INI key, as the row of the Inspector spells it.</param>
    /// <param name="lines">The value text of each line, with the values separated by ", ".</param>
    public static EditPlan SetLines(EditSession session, SectionRef section, string name, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return RemoveKey(session, section, name);
        var plan = CommandPlan.Of($"Set {name}", () => EditCommand.SetLinesFromText(section, name, lines), [section]);
        return plan.CanRun && session.LineTextError(section, name, lines) is { } error
            ? CommandPlan.Refused(plan.Label, error, section, MissionErrors.FieldValue)
            : plan;
    }

    /// <summary>
    /// Plans one line of a key from its values, for example the numbers of a label drag or a color picker. The line
    /// takes the place of the lines of the key (see <see cref="EditCommand.SetLines"/>).
    /// </summary>
    public static EditPlan SetLine(EditSession session, SectionRef section, string name, IReadOnlyList<ValueBase> values) =>
        CommandPlan.Of($"Set {name}", () => EditCommand.SetLines(section, name, [values]), [section]);

    /// <summary>
    /// Plans the lines of a key from the text of a field: one text line for each INI line, with the values separated
    /// by ", ". The schema of the key checks the text first (see <see cref="FieldEdit.Parse"/>). An empty text removes
    /// the key.
    /// </summary>
    public static EditPlan SetLinesFromText(EditSession session, SectionRef section, string name, string text)
    {
        var key = session.Schema(section, name);
        name = key?.Name ?? name;
        var parse = FieldEdit.Parse(key, text);
        return parse.Lines == null
            ? CommandPlan.Refused($"Set {name}", parse.Error!, section, MissionErrors.FieldValue)
            : SetLines(session, section, name, parse.Lines);
    }

    /// <summary>Plans the remove of all lines of a key. The engine then uses its default.</summary>
    public static EditPlan RemoveKey(EditSession session, SectionRef section, string name) =>
        CommandPlan.Of($"Remove {name}", () => EditCommand.RemoveName(section, name), [section]);

    /// <summary>Plans the revert of a key: it gets back the lines of the base data and the mods that load before the
    /// active mod (see <see cref="EditCommand.Revert"/>).</summary>
    public static EditPlan RevertKey(EditSession session, SectionRef section, string name) =>
        CommandPlan.Of($"Revert {name}", () => EditCommand.Revert(section, name), [section]);

    /// <summary>
    /// Plans the revert of a whole section: it gets back the lines of the base data and the mods that load before the
    /// active mod. The revert refuses while the active mod sets the file of a system, because that line moves the
    /// contents of the system (see <see cref="FieldBinding.RevertLock"/>).
    /// </summary>
    public static EditPlan RevertSection(EditSession session, SectionRef section)
    {
        var label = $"Revert {section.Id}";
        return FieldBinding.RevertLock(session, section) is { } reason
            ? CommandPlan.Refused(label, reason, section, MissionErrors.FieldNotWritable)
            : CommandPlan.Of(label, () => EditCommand.Revert(section), [section]);
    }

    /// <summary>
    /// Plans the text of a text key of a section, for example <c>ids_name</c>. The text goes into the strings.json of the
    /// active mod, and the line of the section gets its placeholder (see <see cref="ItemEdits.SetText"/>). An infocard
    /// key gets the text as RDL XML, one paragraph for each line (see <see cref="ItemEdits.ToInfocard"/>).
    /// </summary>
    /// <param name="text">The plain text.</param>
    /// <param name="also">Other sections that show the same text and get the new text too, for example the good of a
    /// gun (see <see cref="ItemEdits.SharedText"/>), or null.</param>
    public static EditPlan SetText(EditSession session, SectionRef section, string name, string text,
        IReadOnlyList<SectionRef>? also = null)
    {
        var label = $"Set {name} of {section.Id.Key}";
        if (SchemaCatalog.TextKind(section.Id.Type, name) is not { } kind)
            return CommandPlan.Refused(label, $"'{name}' does not hold the id of a text.", section, MissionErrors.NotTextId);
        var value = kind == StringKind.Infocard ? ItemEdits.ToInfocard(text) : text;
        if (value.Trim().Length == 0)
            return CommandPlan.Refused(label, EmptyText, section, MissionErrors.FieldValue);
        return CommandPlan.Of(label, () => ItemEdits.SetText(session, section, name, value, also),
            [section, .. (also ?? []).Where(s => !s.Equals(section))]);
    }

    /// <summary>
    /// Plans the request of an agent that sets fields of a section (tool <c>set_fields</c>, section 7.2 of the MCP1
    /// design). Each field is an INI key with a JSON value. All keys change in one entry of the edit history.
    /// </summary>
    /// <remarks>
    /// <para>The value forms: <c>null</c> or an empty array removes the key. A string, a number or a boolean is one line
    /// with one value. An array of values is one line, for example a vector <c>[x, y, z]</c>. An array of arrays has one
    /// line for each array, for example the <c>equip</c> rows of a loadout. A value never holds ',', so no field is a
    /// comma-separated text (AGENTS.md UI rule 3).</para>
    /// <para>The schema of the key checks each value, as a field of the Inspector does. A patch line must also hold
    /// the text of each line (see <see cref="SetLines"/>). A value that the list checks refuse goes into the edit
    /// view, and the result gives the new problem, as the Inspector does.</para>
    /// <para>A key that the Inspector cannot change refuses (see <see cref="Lock"/>). Examples are the key line (use the
    /// rename), a key of a group of linked lines, the file of a system and a read-only section.</para>
    /// </remarks>
    /// <exception cref="EditException">A value has a form that the tool does not take
    /// (<see cref="EditErrorKind.InvalidArgument"/>). Or the section and its schema do not have the key
    /// (<see cref="EditErrorKind.InvalidArgument"/>, <see cref="MissionErrors.UnknownField"/>). Or the schema of the key
    /// or a patch line refuses a value (<see cref="EditErrorKind.InvalidValue"/>).</exception>
    public static EditPlan SetFields(EditSession session, SectionRef section,
        IReadOnlyList<KeyValuePair<string, JsonNode?>> fields)
    {
        if (fields.Count == 0)
            throw Argument(MissionErrors.ValueMissing, null, "Give at least one field.");
        var label = fields.Count == 1 ? $"Set {fields[0].Key}" : $"Set {fields.Count} fields of {Name(section)}";
        if (session.Get(section) is not { } view)
            return CommandPlan.Refused(label, $"The edit view has no section {section.Id}.", section, MissionErrors.ItemNotFound);
        if (FieldBinding.SectionLock(session, section, view) is { } sectionLock)
            return CommandPlan.Refused(label, sectionLock, section, MissionErrors.ReadOnlySection);
        if (fields.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw Argument(MissionErrors.ValueKind, twice.Key, $"The request names the field '{twice.Key}' more than one time.");

        var plans = new List<EditPlan>();
        foreach (var (field, value) in fields)
        {
            var key = session.Schema(section, field);
            var lines = view.Lines(field);
            if (key == null && lines.Count == 0)
            {
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.UnknownField,
                    $"{section.Id} has no field '{field}', and the editor does not know it. Use get_item to see the " +
                    "fields of the item.") { Field = field });
            }

            if (Lock(session, section, view, field) is var (detail, reason))
                return CommandPlan.Refused(label, $"'{field}': {reason}", section, detail);
            var name = key?.Name ?? lines.FirstOrDefault()?.Name ?? field;
            var texts = LineTexts(field, value);
            var parse = FieldEdit.Parse(key, string.Join("\n", texts));
            if (parse.Lines == null)
            {
                throw new EditException(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, parse.Error!)
                    { Field = field });
            }

            if (parse.Lines.SequenceEqual(lines.Select(l => FieldEdit.FormatLine(l.Values)), StringComparer.Ordinal))
                continue;
            var plan = SetLines(session, section, name, parse.Lines);
            if (!plan.CanRun)
                throw Value(field, plan.Refusals[0].Message);
            plans.Add(plan);
        }

        // One field keeps the label of its command, for example "Remove hold_size", as the Inspector gives it.
        return fields.Count == 1 && plans.Count == 1
            ? plans[0]
            : new CommandPlan(EditCommand.Group(label, plans.Select(p => p.Command!).ToList()), [section]);
    }

    /// <summary>
    /// True when <see cref="SetFields"/> cannot change a key of a section. Examples are the key line, a key of a group of
    /// linked lines, the file of a system and a key of a read-only section. The tool <c>get_item</c>
    /// marks such a key as locked.
    /// </summary>
    internal static bool IsLocked(EditSession session, SectionRef section, SectionView view, string name) =>
        Lock(session, section, view, name) != null;

    /// <summary>
    /// The key line of a section: the key entry of its schema. Without one, it is <c>zone</c> for a nebula or a field,
    /// and <c>nickname</c> for the other types. A rename changes it, and a field edit cannot.
    /// </summary>
    internal static string KeyLine(EditSession session, SectionRef section, SectionView view) =>
        session.Schema(section).FirstOrDefault(k => k.IsKeyEntry)?.Name ?? view.Type.ToLowerInvariant() switch
        {
            "nebula" or "asteroids" => "zone",
            _ => "nickname",
        };

    /// <summary>The detail code and the reason why a field edit cannot change a key, or null. The rules are the rules of
    /// the rows of the Inspector (see <see cref="FieldBinding.SectionLock"/> and <see cref="FieldBinding.KeyLock"/>),
    /// with a reason for an agent at the key line.</summary>
    private static (string Detail, string Reason)? Lock(EditSession session, SectionRef section, SectionView view,
        string name)
    {
        if (FieldBinding.SectionLock(session, section, view) is { } reason)
            return (MissionErrors.ReadOnlySection, reason);
        if (name.Equals(KeyLine(session, section, view), StringComparison.OrdinalIgnoreCase))
            return (MissionErrors.KeyLine, "This key finds the section. Use rename_item to give the item a new name.");
        return FieldBinding.KeyLock(session, section, view, session.Schema(section, name), name) is { } locked
            ? (MissionErrors.FieldNotWritable, locked)
            : null;
    }

    /// <summary>The value text of each line of a JSON value of <see cref="SetFields"/>.</summary>
    private static List<string> LineTexts(string field, JsonNode? value)
    {
        if (value is not JsonArray array)
            return value == null ? [] : [ValueText(field, value)];
        if (array.Count > 0 && array.All(v => v is JsonArray))
            return array.Select(line => LineText(field, line!.AsArray())).ToList();
        return array.Count == 0 ? [] : [LineText(field, array)];
    }

    /// <summary>The text of one line: its values, separated by ", ".</summary>
    private static string LineText(string field, JsonArray values)
    {
        if (values.Count == 0)
            throw Argument(MissionErrors.ValueMissing, field, $"A line of '{field}' has no value. Give at least one value.");
        return string.Join(", ", values.Select(v => ValueText(field, v)));
    }

    /// <summary>The text of one value: a string, a number or a boolean.</summary>
    private static string ValueText(string field, JsonNode? value)
    {
        switch (value?.GetValueKind())
        {
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                var number = value.GetValue<double>();
                return number == Math.Floor(number) && Math.Abs(number) < 1e15
                    ? ((long)number).ToString(CultureInfo.InvariantCulture)
                    : number.ToString("R", CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                var text = value.GetValue<string>().Trim();
                if (text.Length == 0)
                    throw Value(field, $"A value of '{field}' is empty. Give a value, or null to remove the field.");
                if (text.IndexOfAny([',', '\n', '\r']) >= 0)
                {
                    throw Value(field, $"A value of '{field}' holds ',' or a line break. Give the values of a line as " +
                                       "an array, for example [1, 2, 3], and each line as an array of its values.");
                }
                return text;
            default:
                throw Argument(MissionErrors.ValueKind, field,
                    $"A value of '{field}' is not a string, a number or a boolean. Give a value, an array of values for " +
                    "one line, or an array of lines.");
        }
    }

    /// <summary>The name of a section in a label: its key, or its type.</summary>
    private static string Name(SectionRef section) => section.Id.Key ?? section.Id.Type;

    private static EditException Argument(string detail, string? field, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, detail, message) { Field = field });

    private static EditException Value(string field, string message) =>
        new(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, message) { Field = field });
}
