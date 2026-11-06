using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The guards of an event reference (decision D767 of the MCP1 design): the type and the time that the agent
/// read. A guard that is null does not check.</summary>
/// <param name="Field">The argument of the guards, for the error of a reference that is not an event.</param>
internal sealed record EventGuard(string? Type, float? Time, string Field);

internal static partial class ItemEditTools
{
    /// <summary>The path of the time of an event.</summary>
    private const string TimeKey = "time";

    /// <summary>The path of the length of an event.</summary>
    private const string LengthKey = "props.duration";

    /// <summary>The path of the targets of an event.</summary>
    private const string TargetsKey = "targets";

    /// <summary>The path of the type of an entity and of an event.</summary>
    private const string TypeKey = "type";

    /// <summary>The guards of <c>set_fields</c>: the arguments <c>expect_type</c> and <c>expect_time</c>, or null when
    /// the call gives neither.</summary>
    private static EventGuard? GuardOf(ToolCall call)
    {
        var type = call.Args.Text("expect_type");
        var time = call.Args.Number("expect_time");
        if (type == null && time == null)
            return null;
        return new EventGuard(type, time is { } seconds ? (float)seconds : null, type != null ? "expect_type" : "expect_time");
    }

    /// <summary>
    /// The element of a <c>thn:</c> reference of a generic edit tool (section 7.2 of the MCP1 design). Each tool takes
    /// some kinds of cutscene items: <c>set_fields</c> and <c>delete_items</c> take entities and events,
    /// <c>rename_item</c> takes an entity, and <c>duplicate_items</c> takes events. Another kind refuses, and the text
    /// names the tool for it. An item of the hidden group Cutscenes gives <c>not_available</c> (decision D725).
    /// </summary>
    /// <param name="guard">The guards of the event, or null.</param>
    /// <exception cref="EditException">The tool does not take the kind, the element does not exist, or a guard does
    /// not match.</exception>
    private static ThnRef CutsceneTarget(ToolCall call, ItemReference reference, string text, EventGuard? guard)
    {
        if (call.Host.HiddenGroups.Contains(ToolGroup.Cutscenes))
            throw new EditException(AgentTools.GroupHidden(ToolGroup.Cutscenes) with { Target = text });
        var kind = reference.Kind;
        if (call.Tool == "set_fields" && kind == ItemRefKind.Cutscene)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, EditTools.CutsceneItem,
                AgentTexts.Error("cutscene_ref_set_fields", ("ref", text))) { Target = text });
        }
        if (call.Tool == "duplicate_items" && kind == ItemRefKind.CutsceneEntity)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, EditTools.CutsceneItem,
                AgentTexts.Error("cutscene_duplicate_entity", ("ref", text))) { Target = text });
        }
        var takes = call.Tool switch
        {
            "set_fields" or "delete_items" => kind is ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent,
            "rename_item" => kind == ItemRefKind.CutsceneEntity,
            "duplicate_items" => kind == ItemRefKind.CutsceneEvent,
            _ => false,
        };
        if (!takes)
        {
            var key = call.Tool is "set_text" or "rename_item" or "delete_items" or "duplicate_items"
                ? $"cutscene_ref_{call.Tool}"
                : "edit_cutscene_item";
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, EditTools.CutsceneItem,
                AgentTexts.Error(key, ("ref", text), ("tool", call.Tool))) { Target = text });
        }
        return ItemRefs.ResolveCutsceneElement(call.Session, reference, guard?.Type, guard?.Time);
    }

    /// <summary>The refusal of guards for a reference that is not a cutscene event.</summary>
    private static EditException GuardRefusal(EventGuard guard, string text) =>
        new(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
            AgentTexts.Error("cutscene_guard_ref", ("ref", text))) { Target = text, Field = guard.Field });

    /// <summary>
    /// The guards of the events of <c>refs</c> of <c>delete_items</c> and <c>duplicate_items</c>: the rows of
    /// <c>expect_events</c>. Each row resolves its event with its guards, and the event must be an event of
    /// <c>refs</c>.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.StaleRevision"/> with the detail <c>RowChanged</c>
    /// for a guard that does not match, and <see cref="EditErrorKind.InvalidArgument"/> for a row whose event is not
    /// in <c>refs</c>.</exception>
    private static void CheckGuards(ToolCall call, IReadOnlyList<(int Index, ThnRef Element)> targets)
    {
        if (call.Args.Rows("expect_events") is not { } rows)
            return;
        EditTools.RequireItemLimit(call, rows.Count, "limit_guards", "expect_events");
        for (var i = 0; i < rows.Count; i++)
        {
            var field = $"expect_events[{i}]";
            var guarded = CutsceneEditTools.Event(call, rows[i], field);
            if (targets.All(t => t.Element != guarded))
            {
                var text = rows[i].Text("ref")!;
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
                    AgentTexts.Error("cutscene_guard_unknown", ("ref", text))) { Target = text, Field = $"{field}.ref" });
            }
        }
    }

    // ---- set_fields ----

    /// <summary>
    /// Sets values of a cutscene entity or event by their THN paths, as the fields of the Inspector do. Each value has
    /// the JSON form of <see cref="ThnJson"/>, and <c>null</c> removes the key. The time and the length of an event move
    /// it as the time and length fields do (see <see cref="ThnFieldCommand"/>), so they round to the 1 ms grid. A
    /// target goes through <see cref="CutsceneCommands.SetTarget"/>, which checks that the entity fits the role of the
    /// target.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a path that is not a path of
    /// the element, or a record. <see cref="EditErrorKind.Refused"/> for the name, the type and the flags of an entity,
    /// which have their own tools. <see cref="EditErrorKind.InvalidValue"/> for a value of the wrong form.</exception>
    internal static void SetCutsceneFields(EditScope scope, ThnRef element, JsonObject fields)
    {
        var session = scope.Session;
        if (fields.Count > 1)
        {
            var working = session.Cutscene(element.Path)!;
            var table = working.Element(element.Kind, element.Id)!.Table;
            var name = element.Kind == ThnElementKind.Entity
                ? ThnScriptModel.NameOf(table) ?? "entity"
                : $"event {working.Events.FindIndex(e => e.Id == element.Id)}";
            scope.Batch.Label ??= $"Set {fields.Count} fields of {name}";
        }
        foreach (var (key, value) in fields)
            EditTools.At($"fields.{key}", () => SetCutsceneField(scope, element, key, value));
    }

    /// <summary>Sets or removes one value of a cutscene element (see <see cref="SetCutsceneFields"/>).</summary>
    private static void SetCutsceneField(EditScope scope, ThnRef element, string key, JsonNode? value)
    {
        var session = scope.Session;
        var symbols = session.ThnSymbols;
        if (ThnPath.Problem(key) != null)
            throw FieldError(EditErrorKind.InvalidArgument, ItemRefs.BadReference, AgentTexts.Error("cutscene_path", ("path", key)));
        var path = new ThnPath(key);
        var kind = element.Kind;
        if (ThnElementPaths.Problem(kind, path, value == null) is { } problem)
            throw FieldError(EditErrorKind.InvalidArgument, ItemRefs.BadReference, problem);
        var table = session.Cutscene(element.Path)!.Element(kind, element.Id)!.Table;
        var type = ThnSchema.TypeOf(kind, table, symbols);
        var row = ThnSchema.Find(kind, type, path);
        if (key == TypeKey || kind == ThnElementKind.Entity && key == ThnScriptModel.EntityNameKey ||
            row?.Kind == ThnKeyKind.EntityFlags)
        {
            var text = key == TypeKey ? "cutscene_field_type"
                : row?.Kind == ThnKeyKind.EntityFlags ? "cutscene_field_flags"
                : "cutscene_field_name";
            throw FieldError(EditErrorKind.Refused, EditTools.CutsceneItem, AgentTexts.Error(text));
        }
        if (row != null)
            path = ThnSchema.KeyIn(row, kind, table);
        var stored = path.ValueIn(kind, table);
        if (kind == ThnElementKind.Event && key == TimeKey)
        {
            Run(scope, ThnFieldCommand.Time(element, stored is ThnNumber time ? time.Value : 0).Make(session, NumberOf(key, value)));
            return;
        }
        if (kind == ThnElementKind.Event && key == TargetsKey)
        {
            SetTargets(scope, element, stored, value);
            return;
        }
        if (value == null)
        {
            Run(scope, CutsceneCommands.RemoveValue(session, element, path));
            return;
        }
        if (kind == ThnElementKind.Event && key == LengthKey)
        {
            Run(scope, ThnFieldCommand.Length(element, stored is ThnNumber length ? length.Value : 0)
                .Make(session, NumberOf(key, value)));
            return;
        }
        if (row?.Kind == ThnKeyKind.Record || row == null && stored is ThnTable { IsRecord: true, Record.IsEmpty: false })
            throw FieldError(EditErrorKind.InvalidArgument, ItemRefs.BadReference, AgentTexts.Error("cutscene_field_record", ("path", key)));
        var input = ThnJson.Read(row, path.Text, value, stored, symbols, session.ThnEvaluator);
        if (input.Error != null)
            throw FieldError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, input.Error);
        Run(scope, input.Bits is { } bits
            ? CutsceneCommands.SetFlagBits(session, element, row!, bits)
            : CutsceneCommands.SetValue(session, element, path, input.Value!));
    }

    /// <summary>The targets of an event: each name that differs from the target at its place, without letter case,
    /// goes through <see cref="CutsceneCommands.SetTarget"/>. The list keeps the number of targets.</summary>
    private static void SetTargets(EditScope scope, ThnRef ev, ThnValue? stored, JsonNode? value)
    {
        var current = stored is ThnTable list ? list.List.Select(t => (t as ThnText)?.Value).ToList() : [];
        if (value is not JsonArray names || names.Any(n => n?.GetValueKind() != JsonValueKind.String))
            throw FieldError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, AgentTexts.Error("cutscene_field_targets"));
        if (names.Count != current.Count)
        {
            throw FieldError(EditErrorKind.InvalidValue, MissionErrors.FieldValue,
                AgentTexts.Error("cutscene_field_target_count", ("count", current.Count)));
        }
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i]!.GetValue<string>();
            if (!string.Equals(name, current[i], StringComparison.OrdinalIgnoreCase))
                Run(scope, CutsceneCommands.SetTarget(scope.Session, ev, i, name));
        }
    }

    /// <summary>The number of the time or the length of an event.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidValue"/> for another value.</exception>
    private static ThnNumber NumberOf(string key, JsonNode? value)
    {
        if (value is JsonValue json && json.GetValueKind() == JsonValueKind.Number && json.TryGetValue(out double number) &&
            float.IsFinite((float)number))
            return new ThnNumber((float)number);
        throw FieldError(EditErrorKind.InvalidValue, MissionErrors.FieldValue,
            AgentTexts.Error("cutscene_field_number", ("path", key)));
    }

    private static EditException FieldError(EditErrorKind kind, string detail, string message) =>
        new(new EditError(kind, detail, message));

    // ---- rename_item ----

    /// <summary>
    /// Renames a cutscene entity with <see cref="CutsceneCommands.RenameEntity"/>, as the name field of the Inspector
    /// does. The events and the speakers of the script that name the entity get the new name too. Other data that uses
    /// the old name keeps it, so the call needs <c>confirm: true</c> when such uses exist (decision D242). The tool
    /// finds the uses in the edit view (see <see cref="CutsceneRename.Uses(EditSession, string, string)"/>).
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NameTaken"/> for a name of another entity, and
    /// <see cref="EditErrorKind.Refused"/> for an empty name or a cutscene that takes no edits.</exception>
    private static void RenameCutsceneEntity(EditScope scope, ThnRef entity, string name)
    {
        var session = scope.Session;
        var newName = name.Trim();
        if (newName.Length == 0)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, MissionErrors.NameInvalid,
                AgentTexts.Error("cutscene_name_empty")) { Field = "new_name" });
        }
        var table = session.Cutscene(entity.Path)!.Element(ThnElementKind.Entity, entity.Id)!.Table;
        var uses = CutsceneRename.Uses(session, entity.Path, ThnScriptModel.NameOf(table) ?? "");
        Run(scope, CutsceneCommands.RenameEntity(session, entity, newName, uses),
            error => error.Detail == MissionErrors.NameTaken ? "new_name" : "ref");
    }

    // ---- delete_items ----

    /// <summary>
    /// Deletes cutscene items, as Delete in the Timeline and in the Hierarchy does: the events of each script in one
    /// plan, then each entity with the events that name it (see <see cref="CutsceneCommands.DeleteEntity"/>). An entity
    /// that events name needs <c>confirm: true</c>, as the editor asks. The events go first, so an event that the call
    /// names goes once.
    /// </summary>
    private static void DeleteCutsceneItems(EditScope scope, IReadOnlyList<(int Index, ThnRef Element)> items)
    {
        var session = scope.Session;
        foreach (var script in items.GroupBy(t => t.Element.Path, VfsPath.Comparer))
        {
            var events = script.Where(t => t.Element.Kind == ThnElementKind.Event).Select(t => t.Element).Distinct().ToList();
            if (events.Count > 0)
                Run(scope, CutsceneCommands.DeleteEvents(session, events), _ => "refs");
            foreach (var (index, entity) in script.Where(t => t.Element.Kind == ThnElementKind.Entity).DistinctBy(t => t.Element))
                Run(scope, CutsceneCommands.DeleteEntity(session, entity), _ => $"refs[{index}]");
        }
    }

    // ---- duplicate_items ----

    /// <summary>
    /// Copies cutscene events, one plan for each script. Without an offset, the copies go after the events, as Duplicate
    /// of Unity and of the Timeline does (see <see cref="TimelineEdits.Duplicate"/>). With an offset, each copy is at
    /// the time of its event plus the offset. An event has no position, so the argument <c>offset</c> does not move a
    /// copy, and the result warns about each copy, as for a mission copy.
    /// </summary>
    /// <param name="timeOffset">The argument <c>time_offset</c> in seconds, or null.</param>
    /// <param name="offset">True when the call has the argument <c>offset</c>.</param>
    private static void DuplicateCutsceneEvents(EditScope scope, IReadOnlyList<(int Index, ThnRef Element)> items,
        double? timeOffset, bool offset)
    {
        var session = scope.Session;
        foreach (var script in items.GroupBy(t => t.Element.Path, VfsPath.Comparer))
        {
            var events = script.Select(t => t.Element).Distinct().ToList();
            var plan = timeOffset is { } seconds
                ? CutsceneCommands.Duplicate(session, events, seconds)
                : CutsceneCommands.Duplicate(session, events);
            Run(scope, plan, error => error.Detail == MissionErrors.FieldValue ? "time_offset" : "refs");
            if (!offset)
                continue;
            scope.Report(new EditResult(EditStatus.Applied)
            {
                Warnings = plan.Added(session).Select(copy => ItemRefs.Of(session, copy)).Select(copy =>
                    new Problem(DiagnosticSeverity.Warning, AgentTexts.Error("cutscene_copy_not_moved", ("ref", copy)))
                    {
                        Ref = copy,
                    }).ToList(),
            });
        }
    }

    /// <summary>Runs a cutscene plan in the scope. A refusal names the argument that <paramref name="fieldOf"/> gives,
    /// when the error names no argument.</summary>
    private static void Run(EditScope scope, EditPlan plan, Func<EditError, string>? fieldOf = null)
    {
        try
        {
            scope.Run(plan);
        }
        catch (EditException e) when (e.Error.Field == null && fieldOf != null)
        {
            throw new EditException(e.Error with { Field = fieldOf(e.Error) });
        }
    }
}
