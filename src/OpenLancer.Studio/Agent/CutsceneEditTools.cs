using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The cutscene tools of section 7.5 of the MCP1 design (milestone MCP1-4). Each tool calls a command of
/// <see cref="CutsceneCommands"/>, as the Cutscene tab does, and runs its plan in the batch of the call. So each call
/// is one Undo step with the edit origin Agent, and the result gives the <c>thn:</c> references of the items that the
/// call adds, changes and deletes (see <see cref="CutsceneResults"/>).
/// </summary>
/// <remarks>
/// <para>An event reference holds the index of the event, with the optional guards <c>expect_type</c> and
/// <c>expect_time</c> (decision D767). A guard that does not match gives <c>stale_revision</c> with the detail
/// <c>RowChanged</c>, and nothing changes.</para>
/// <para>Each argument that names a cutscene item takes only its own kind of <c>thn:</c> reference: a script, an
/// entity or an event. Another reference gives <c>invalid_argument</c>.</para>
/// </remarks>
internal static class CutsceneEditTools
{
    /// <summary>The detail code of the events of more than one cutscene in one call.</summary>
    public const string OneCutscene = "OneCutscene";

    /// <summary>The detail code of a limit of the events of one call.</summary>
    public const string EditItems = "EditItems";

    /// <summary>The row of an event with its guards (decision D767 of the MCP1 design).</summary>
    public static readonly IReadOnlyList<ToolParam> EventRow =
    [
        new TextParam("ref", true, null, null),
        new TextParam("expect_type", false, null, null),
        new NumberParam("expect_time", false, double.NegativeInfinity),
    ];

    /// <summary>
    /// Makes a new cutscene of the active mod, as New cutscene of the Cutscenes tab does. The script goes into
    /// <c>DATA\MODS\&lt;mod id&gt;\CUTSCENES</c>, with a camera, a monitor and a cut to the camera at 0 (see
    /// <see cref="ThnTemplates.NewScript"/>). A duration other than the default sets the duration in the same Undo
    /// step.
    /// </summary>
    public static ToolStep Create(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var plan = CutsceneCommands.Create(session, call.Args.Text("name")!);
        Run(scope, plan, "name", "name");
        if (call.Args.Number("duration") is { } seconds)
            Run(scope, CutsceneCommands.SetDuration(session, plan.Path, (float)seconds), "duration", "duration");
    });

    /// <summary>Sets the duration of a script, as the duration field of the Inspector does. The value does not
    /// round.</summary>
    public static ToolStep SetDuration(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var path = Script(call, "cutscene");
        Run(scope, CutsceneCommands.SetDuration(scope.Session, path, (float)call.Args.Number("seconds")!.Value),
            "seconds", "cutscene");
    });

    /// <summary>Sets the flags of an entity that the Inspector shows. The flags that the list does not name go off,
    /// and the other bits of the value keep their state.</summary>
    public static ToolStep SetFlags(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var entity = Entity(call, "entity");
        var flags = call.Args.Texts("flags")!.ToHashSet(StringComparer.Ordinal);
        Run(scope, CutsceneCommands.SetFlags(scope.Session, entity, flags), "flags", "entity");
    });

    /// <summary>
    /// Moves events of one cutscene in time and changes their length, as a drag on the Timeline does. The new times and
    /// lengths round to the 1 ms grid. Each row can guard its event (decision D767).
    /// </summary>
    public static ToolStep MoveEvents(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var rows = call.Args.Rows("events")!;
        var limit = call.Limits.EditItems;
        if (rows.Count > limit)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, EditItems,
                AgentTexts.Error("limit_events", ("max", limit))) { Field = "events" });
        }
        var events = rows.Select((row, i) => Event(call, row, $"events[{i}]")).ToList();
        if (events.Select(e => e.Path).Distinct(VfsPath.Comparer).Count() > 1)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, OneCutscene,
                AgentTexts.Error("cutscene_events_one_cutscene")) { Field = "events" });
        }
        var time = (float)(call.Args.Number("time_delta") ?? 0);
        var length = (float)(call.Args.Number("length_delta") ?? 0);
        // Two rows can name one event. The move changes it once, so the label counts it once.
        var plan = CutsceneCommands.MoveEvents(scope.Session, events.Distinct().ToList(), time, length);
        var valueField = plan.Refusals.FirstOrDefault()?.Message == ThnEdits.NegativeLength ? "length_delta" : "time_delta";
        Run(scope, plan, valueField, "events");
    });

    /// <summary>
    /// The install path of the cutscene script that an argument names. The argument takes only a <c>thn:</c>
    /// reference of a script, without an entity or an event.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for another reference, and
    /// <see cref="EditErrorKind.NotFound"/> for a script that the edit view does not have.</exception>
    internal static string Script(ToolCall call, string field) => EditTools.At(field, () =>
    {
        var text = call.Args.Text(field)!;
        var reference = Kind(ItemRefs.Parse(text), ItemRefKind.Cutscene, text, "cutscene_script_ref");
        return ItemRefs.ResolveCutscene(call.Session, reference);
    });

    /// <summary>The entity of a cutscene that an argument names: the last entity with the name, as the engine uses
    /// it.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a reference that is not a
    /// reference of an entity, and <see cref="EditErrorKind.NotFound"/>.</exception>
    internal static ThnRef Entity(ToolCall call, string field) => EditTools.At(field, () =>
    {
        var text = call.Args.Text(field)!;
        var reference = Kind(ItemRefs.Parse(text), ItemRefKind.CutsceneEntity, text, "cutscene_entity_ref");
        return ItemRefs.ResolveCutsceneElement(call.Session, reference);
    });

    /// <summary>The event of a row with its guards (decision D767 of the MCP1 design).</summary>
    /// <param name="field">The argument of the row, for example <c>events[0]</c>.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a reference that is not a
    /// reference of an event, <see cref="EditErrorKind.NotFound"/>, and <see cref="EditErrorKind.StaleRevision"/> with
    /// the detail <c>RowChanged</c> for a guard that does not match.</exception>
    internal static ThnRef Event(ToolCall call, ToolArgs row, string field) => EditTools.At($"{field}.ref", () =>
    {
        var text = row.Text("ref")!;
        var reference = Kind(ItemRefs.Parse(text), ItemRefKind.CutsceneEvent, text, "cutscene_event_ref");
        var time = row.Number("expect_time");
        return ItemRefs.ResolveCutsceneElement(call.Session, reference, row.Text("expect_type"),
            time is { } seconds ? (float)seconds : null);
    });

    /// <summary>A reference of one kind of cutscene item.</summary>
    /// <param name="text">The key of the error text in <c>agent-tools.json</c>.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> with the detail
    /// <see cref="ItemRefs.BadReference"/> for a reference of another kind.</exception>
    private static ItemReference Kind(ItemReference reference, ItemRefKind kind, string given, string text) =>
        reference.Kind == kind
            ? reference
            : throw new EditException(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
                AgentTexts.Error(text, ("ref", given))) { Target = given });

    /// <summary>Runs a plan in the scope. A refusal of a value names the argument of the value, and another refusal
    /// names the argument of the item, for example the reason why a cutscene takes no edits.</summary>
    private static void Run(EditScope scope, CutscenePlan plan, string valueField, string itemField)
    {
        try
        {
            scope.Run(plan);
        }
        catch (EditException e) when (e.Error.Field == null)
        {
            throw new EditException(e.Error with
            {
                Field = e.Error.Detail == MissionErrors.FieldValue ? valueField : itemField,
            });
        }
    }
}
