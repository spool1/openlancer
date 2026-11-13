using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The commands of cutscene scripts (section 4.0.8 of the M6b design, the row of ThnEdits, ThnFieldEdit and
/// TimelineEdits). The editor UI and the agent tools call them, and each one gives the plan that its caller runs. A
/// gesture with many steps, for example a drag on the Timeline, calls a command in each step (decision D766).
/// </summary>
/// <remarks>
/// <para>Each command calls the session commands of <see cref="ThnEdits"/> and does not copy their rules. A plan reads
/// the edit view when it is made, so make it and run it at once.</para>
/// <para>A command never throws for a rule of the edit. It gives a plan that refuses, with the reason. A cutscene that
/// takes no edits refuses with <see cref="EditSession.CutsceneReadOnlyReason(ThnRef)"/>. A value that the data cannot
/// hold refuses with the detail <see cref="MissionErrors.FieldValue"/>. Some rules of the session commands apply only
/// when they run, for example a revert of an element that the active mod adds. The run then refuses, and nothing
/// changes.</para>
/// <para>The editor asks before it runs a plan that needs a confirmation (see
/// <see cref="EditPlan.NeedsConfirmation"/>), and the editor selects after the run as Unity does (decision
/// D249).</para>
/// </remarks>
public static class CutsceneCommands
{
    /// <summary>
    /// Plans a value at a path of an entity or an event. Each missing record on the path is made. See
    /// <see cref="ThnEdits.Set"/>.
    /// </summary>
    /// <remarks>The engine runs the path text of an entity of each type as Lua (see
    /// <see cref="ThnMotionPath.ScriptProblem"/>). So a value that gives an entity a new path text with a
    /// <see cref="ThnMotionPath.TextProblem"/> refuses with the detail <see cref="MissionErrors.FieldValue"/>, for
    /// each entity type and each path of the edit, for example a whole <c>pathprops</c> record. A path text that the
    /// edit does not change keeps its value.</remarks>
    public static CutscenePlan SetValue(EditSession session, ThnRef target, ThnPath path, ThnValue value)
    {
        var label = $"Set {path}";
        var script = ScriptPath(session, target.Path);
        if (session.CutsceneReadOnlyReason(target) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        if (NewPathProblem(session, target, path, value) is { } problem)
            return CutscenePlan.Refused(label, script, problem, MissionErrors.FieldValue);
        return CutscenePlan.Of(label, script, () => ThnEdits.Set(target, path, value), changed: [target]);
    }

    /// <summary>
    /// Plans a move of events in time and a change of their length (<c>props.duration</c>), as one entry. The new times
    /// and lengths round to the 1 ms grid. A change of 0 and 0 gives a plan that changes nothing. See
    /// <see cref="ThnEdits.MoveEvents"/>.
    /// </summary>
    /// <remarks>The plan checks the values of the events in W before it runs (see <see cref="ThnEdits.MoveRefusal"/>).
    /// A time below 0, a negative length, or a time or a length to change that is not a number refuses with the detail
    /// <see cref="MissionErrors.FieldValue"/>. A change of the length of an event whose type has no length refuses with
    /// that detail too (see <see cref="ThnSchema.HasLength"/>). The Inspector does not show the length of such an
    /// event.</remarks>
    /// <param name="events">Events of one cutscene.</param>
    /// <param name="timeDelta">The change of each time, in seconds.</param>
    /// <param name="lengthDelta">The change of each length, in seconds. An event without a length has the length
    /// 0.</param>
    public static CutscenePlan MoveEvents(EditSession session, IReadOnlyList<ThnRef> events, float timeDelta,
        float lengthDelta)
    {
        var label = ThnEdits.MoveLabel(events.Count, timeDelta, lengthDelta);
        var path = events.Count > 0 ? ScriptPath(session, events[0].Path) : "";
        if (!float.IsFinite(timeDelta) || !float.IsFinite(lengthDelta))
            return CutscenePlan.Refused(label, path, ThnNumber.NotFiniteMessage, MissionErrors.FieldValue);
        foreach (var ev in events)
        {
            if (session.CutsceneReadOnlyReason(ev) is { } reason)
                return CutscenePlan.Refused(label, path, reason);
        }

        var plan = CutscenePlan.Of(label, path, () =>
        {
            // The session command checks the events also for a change of 0 and 0.
            var move = ThnEdits.MoveEvents(events, timeDelta, lengthDelta);
            return timeDelta == 0 && lengthDelta == 0 ? EditCommand.Group(move.Label, []) : move;
        }, changed: events);
        if (plan.CanRun && (NoLengthRefusal(session, events, lengthDelta) ??
                            ThnEdits.MoveRefusal(session, events, timeDelta, lengthDelta)) is { } refusal)
            return CutscenePlan.Refused(label, path, refusal, MissionErrors.FieldValue);
        return plan;
    }

    /// <summary>Plans the <c>duration</c> of a script: its length in seconds. The value does not round. See
    /// <see cref="ThnEdits.SetDuration"/>.</summary>
    /// <remarks>A negative value or a value that is not finite refuses with the detail
    /// <see cref="MissionErrors.FieldValue"/>.</remarks>
    /// <param name="path">The install path of the script.</param>
    public static CutscenePlan SetDuration(EditSession session, string path, float seconds)
    {
        const string label = "Set duration";
        path = ScriptPath(session, path);
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        if (!float.IsFinite(seconds))
            return CutscenePlan.Refused(label, path, ThnNumber.NotFiniteMessage, MissionErrors.FieldValue);
        if (seconds < 0)
            return CutscenePlan.Refused(label, path, ThnEdits.NegativeDuration, MissionErrors.FieldValue);
        return CutscenePlan.Of(label, path, () => ThnEdits.SetDuration(path, seconds));
    }

    /// <summary>
    /// Plans one point of the curve of an event (<c>props.param_curve</c>). The other points keep their tables (see
    /// <see cref="ThnCurve.WithPoint"/>). The caller keeps the time of the point between the times of its neighbours,
    /// because the engine finds the segment of a time in the order of the points.
    /// </summary>
    /// <remarks>A number of the point that is not finite refuses with the detail
    /// <see cref="MissionErrors.FieldValue"/>.</remarks>
    /// <param name="index">The index of the point in the curve.</param>
    public static CutscenePlan SetCurvePoint(EditSession session, ThnRef ev, int index, ThnCurvePoint point)
    {
        var label = $"Move point {index + 1} of the curve";
        var script = ScriptPath(session, ev.Path);
        if (session.CutsceneReadOnlyReason(ev) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        if (!float.IsFinite(point.Time) || !float.IsFinite(point.Value) || !float.IsFinite(point.In) ||
            !float.IsFinite(point.Out))
            return CutscenePlan.Refused(label, script, ThnNumber.NotFiniteMessage, MissionErrors.FieldValue);
        if (CurveOf(session, ev) is not { } curve || ThnCurve.Points(curve) is not { } points || index < 0 ||
            index >= points.Count)
            return CutscenePlan.Refused(label, script, NoCurvePoint(index));
        return CutscenePlan.Of(label, script,
            () => ThnEdits.Set(ev, ThnCurve.Path, ThnCurve.WithPoint(curve, index, point)), changed: [ev]);
    }

    /// <summary>
    /// Plans a new cutscene of the active mod: the script of <see cref="ThnTemplates.NewScript"/> at
    /// <see cref="EditSession.NewCutscenePath"/> of the name. A save writes it as a whole file. See
    /// <see cref="ThnEdits.NewCutscene"/>.
    /// </summary>
    /// <remarks>The reasons of <see cref="ThnEdits.NewCutsceneRefusal"/> refuse the plan. A path in use gives the
    /// detail <see cref="MissionErrors.PathInUse"/>, and a name with other characters gives
    /// <see cref="MissionErrors.NameInvalid"/>. A session without an active mod, a read-only session and a mod in the
    /// OpenLancer repo give <see cref="MissionErrors.NotEditable"/>.</remarks>
    /// <param name="name">The name of the file without <c>.thn</c>.</param>
    public static CutscenePlan Create(EditSession session, string name)
    {
        var label = $"New cutscene {name}";
        if (session.NewCutscenePath(name) is not { } path)
            return CutscenePlan.Refused(label, "", ThnEdits.NoActiveMod, MissionErrors.NotEditable);
        if (ThnEdits.NewCutsceneProblem(session, path) is { } problem)
            return CutscenePlan.Refused(label, path, problem.Message, problem.Detail);
        return CutscenePlan.Of(label, path, () => ThnEdits.NewCutscene(path, ThnTemplates.NewScript()));
    }

    /// <summary>
    /// Plans a revert of a whole script: W gets the values of the lower cutscene B, and the next save deletes the patch
    /// of the active mod. See <see cref="ThnEdits.Revert"/>.
    /// </summary>
    /// <remarks>A whole file of the active mod has no lower values. The run then refuses, and nothing changes. The plan
    /// names no element, because a revert of the script can change, add and delete each element.</remarks>
    /// <param name="path">The install path of the script.</param>
    public static CutscenePlan RevertFile(EditSession session, string path)
    {
        const string label = "Revert cutscene";
        path = ScriptPath(session, path);
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        return CutscenePlan.Of(label, path, () => ThnEdits.Revert(path));
    }

    /// <summary>
    /// Plans a revert of the value at a path of an entity or an event: the value of the lower cutscene B comes back.
    /// See <see cref="ThnEdits.Revert"/>.
    /// </summary>
    /// <remarks>An element that the active mod adds has no lower values. The run then refuses, and nothing
    /// changes.</remarks>
    public static CutscenePlan Revert(EditSession session, ThnRef target, ThnPath path)
    {
        var label = $"Revert {path}";
        var script = ScriptPath(session, target.Path);
        if (session.CutsceneReadOnlyReason(target) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        return CutscenePlan.Of(label, script, () => ThnEdits.Revert(target.Path, target, path), changed: [target]);
    }

    /// <summary>Plans the remove of the value at a path of an entity or an event. See
    /// <see cref="ThnEdits.Remove"/>.</summary>
    public static CutscenePlan RemoveValue(EditSession session, ThnRef target, ThnPath path)
    {
        var label = $"Remove {path}";
        var script = ScriptPath(session, target.Path);
        if (session.CutsceneReadOnlyReason(target) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        return CutscenePlan.Of(label, script, () => ThnEdits.Remove(target, path), changed: [target]);
    }

    /// <summary>Plans the flags of an entity that the Inspector shows. See <see cref="ThnEdits.SetFlags"/>.</summary>
    /// <remarks>The plan checks the flags against the entity in W before it runs. A name that is not a flag of the
    /// entity type refuses with the detail <see cref="MissionErrors.FieldValue"/>, for example SPATIAL on an entity
    /// that is not a SOUND. A stored value that the engine cannot read as flags refuses with the reason.</remarks>
    /// <param name="flags">The flags that are on after the edit.</param>
    public static CutscenePlan SetFlags(EditSession session, ThnRef entity, IReadOnlySet<string> flags)
    {
        const string label = "Set flags";
        var script = ScriptPath(session, entity.Path);
        if (session.CutsceneReadOnlyReason(entity) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        if (entity.Kind == ThnElementKind.Entity && Table(session, entity) is { } table)
        {
            var symbols = session.ThnSymbols;
            try
            {
                ThnFlags.Write(table[ThnFlags.Key], ThnFlags.IsSound(table, symbols), flags, symbols);
            }
            catch (ArgumentException e)
            {
                return CutscenePlan.Refused(label, script, ErrorText.Of(e), MissionErrors.FieldValue);
            }
            catch (InvalidOperationException e)
            {
                return CutscenePlan.Refused(label, script, ErrorText.Of(e));
            }
        }

        return CutscenePlan.Of(label, script, () => ThnEdits.SetFlags(entity, flags), changed: [entity]);
    }

    /// <summary>
    /// Plans the bits of a flags key of an event, with the write rules of <see cref="ThnFlags.WriteBits"/>. The bits
    /// that the row does not list keep their state. When the key has no value and the bits do not change, the plan
    /// removes the key, as the checkboxes of the Inspector do.
    /// </summary>
    /// <remarks>A name that is not a bit of the row refuses with the detail <see cref="MissionErrors.FieldValue"/>. A
    /// value in W that the engine cannot read as bits refuses with the reason.</remarks>
    /// <param name="row">The row of the flags key in the THN schema.</param>
    /// <param name="bits">The names of the bits that are on after the edit.</param>
    public static CutscenePlan SetFlagBits(EditSession session, ThnRef target, ThnKeyRow row, IReadOnlySet<string> bits)
    {
        var label = $"Set {row.Path}";
        var script = ScriptPath(session, target.Path);
        if (session.CutsceneReadOnlyReason(target) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        var stored = Table(session, target) is { } table ? row.Path.ValueIn(target.Kind, table) : null;
        ThnValue? value;
        try
        {
            value = ThnFlags.WriteBits(stored, row, bits, session.ThnSymbols);
        }
        catch (ArgumentException e)
        {
            return CutscenePlan.Refused(label, script, ErrorText.Of(e), MissionErrors.FieldValue);
        }
        catch (InvalidOperationException e)
        {
            return CutscenePlan.Refused(label, script, ErrorText.Of(e));
        }

        return value is { } written
            ? CutscenePlan.Of(label, script, () => ThnEdits.Set(target, row.Path, written), changed: [target])
            : CutscenePlan.Of($"Remove {row.Path}", script, () => ThnEdits.Remove(target, row.Path), changed: [target]);
    }

    /// <summary>
    /// Plans a target of an event: the entity of W with a name. The entity must fit the role of the target, as the
    /// target list of the Inspector offers it (see <see cref="ThnEventRole.Fits"/>). The event gets the name in the
    /// letter case of the entity.
    /// </summary>
    /// <remarks>A name that no entity of W has refuses with the detail <see cref="MissionErrors.ItemNotFound"/>. An
    /// entity of a type that does not fit refuses with the detail <see cref="MissionErrors.TargetType"/> and the types
    /// that fit.</remarks>
    /// <param name="index">The index of the target, from 0.</param>
    public static CutscenePlan SetTarget(EditSession session, ThnRef ev, int index, string name)
    {
        var label = $"Set {TargetsPath}";
        var script = ScriptPath(session, ev.Path);
        if (session.CutsceneReadOnlyReason(ev) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        if (ev.Kind != ThnElementKind.Event || session.Cutscene(ev.Path) is not { } working ||
            Table(session, ev) is not { } table)
            return CutscenePlan.Refused(label, script, ThnEdits.NoSuchElement(ThnElementKind.Event));
        if (TargetsPath.ValueIn(ThnElementKind.Event, table) is not ThnTable targets || index < 0 ||
            index >= targets.List.Length)
            return CutscenePlan.Refused(label, script, $"The event has no target {index + 1}.");
        var symbols = session.ThnSymbols;
        var role = ThnTemplates.Role(ThnSchema.TypeOf(ThnElementKind.Event, table, symbols));
        if (TargetRefusal(working, role, index + 1, name, symbols) is { } refusal)
            return CutscenePlan.Refused(label, script, refusal.Message, refusal.Detail);
        var value = ThnFieldEdit.TargetsValue(targets, index, EntityName(working, name));
        return CutscenePlan.Of(label, script, () => ThnEdits.Set(ev, TargetsPath, value), changed: [ev]);
    }

    /// <summary>
    /// Plans a new name of an entity. The event targets and the speakers of the script that name the entity get the new
    /// name too. See <see cref="ThnEdits.RenameEntity"/>.
    /// </summary>
    /// <remarks>
    /// <para>A name that a save cannot write (see <see cref="ThnWriter.TextProblem"/>) refuses with the detail
    /// <see cref="MissionErrors.NameInvalid"/>. A name of another entity, without regard to letter case, refuses with
    /// the detail <see cref="MissionErrors.NameTaken"/>. Other data that uses the old name keeps it. So the plan needs a
    /// confirmation when the caller gives such uses (decision D242).</para>
    /// <para><see cref="CutscenePlan.Changed"/> names only the entity. The events and the speakers that the rename
    /// changes too are not in it. The plan keeps the uses (<see cref="CutscenePlan.Uses"/>) for the question.</para>
    /// </remarks>
    /// <param name="uses">The uses of the old name in other data (see <see cref="CutsceneRename.Uses"/>).</param>
    internal static CutscenePlan RenameEntity(EditSession session, ThnRef entity, string newName,
        IReadOnlyList<ThnNameUse> uses)
    {
        var label = $"Rename to {newName}";
        var script = ScriptPath(session, entity.Path);
        if (session.CutsceneReadOnlyReason(entity) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        if (ThnWriter.TextProblem(newName) is { } badName)
            return CutscenePlan.Refused(label, script, badName, MissionErrors.NameInvalid);
        if (session.Cutscene(entity.Path) is { } working &&
            ThnEdits.NameTakenRefusal(working, newName, entity.Id) is { } taken)
            return CutscenePlan.Refused(label, script, taken, MissionErrors.NameTaken);
        return CutscenePlan.Of(label, script, () => ThnEdits.RenameEntity(entity, newName), changed: [entity],
            needsConfirmation: uses.Count > 0, uses: uses);
    }

    /// <summary>Plans a delete of events of one cutscene, as one entry. See <see cref="ThnEdits.Delete"/>.</summary>
    public static CutscenePlan DeleteEvents(EditSession session, IReadOnlyList<ThnRef> events)
    {
        // Each script counts its element ids, so two scripts can have the same id. Thus the check for one cutscene
        // uses the list as given, and only the elements of one script lose their duplicates.
        var oneCutscene = ThnEdits.InOneCutscene(events);
        IReadOnlyList<ThnRef> unique = oneCutscene ? [.. events.DistinctBy(e => e.Id)] : events;
        var label = unique.Count == 1 ? "Delete event" : $"Delete {unique.Count} events";
        var path = unique.Count > 0 ? ScriptPath(session, unique[0].Path) : "";
        if (unique.Count == 0)
            return CutscenePlan.Refused(label, path, "There is no event to delete.");
        if (unique.Any(e => e.Kind != ThnElementKind.Event))
            return CutscenePlan.Refused(label, path, "Each element of the delete must be an event.");
        if (!oneCutscene)
            return CutscenePlan.Refused(label, path, ThnEdits.OneCutscene);
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        return CutscenePlan.Of(label, path, () => EditCommand.Group(label, [.. unique.Select(ThnEdits.Delete)]),
            deleted: unique);
    }

    /// <summary>
    /// Plans a delete of an entity with the events that name it, as one entry (section 7.3 of the M8 design). The
    /// engine cannot play an event whose target is gone. So the plan needs a confirmation when events name the
    /// entity.
    /// </summary>
    /// <remarks>The events are the events of W that name the entity without regard to letter case, as the engine finds
    /// a target (see <see cref="TimelineEdits.EventsNaming"/>). The events do not find an earlier entity that a later
    /// entity with the same name hides, so such an entity goes alone.</remarks>
    public static CutscenePlan DeleteEntity(EditSession session, ThnRef entity)
    {
        var path = ScriptPath(session, entity.Path);
        var working = session.Cutscene(path);
        var element = working?.Element(ThnElementKind.Entity, entity.Id);
        var name = element == null ? null : ThnScriptModel.NameOf(element.Table);
        IReadOnlyList<ThnRef> events = name != null && ReferenceEquals(working!.EntityNamed(name), element)
            ? TimelineEdits.EventsNaming(path, working, name)
            : [];
        var label = TimelineEdits.DeleteEntityLabel(name, events.Count);
        if (entity.Kind != ThnElementKind.Entity)
            return CutscenePlan.Refused(label, path, "The element of the delete must be an entity.");
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        return CutscenePlan.Of(label, path, () => TimelineEdits.DeleteWithEvents(entity, name, events),
            deleted: [.. events, entity], needsConfirmation: events.Count > 0);
    }

    /// <summary>
    /// Plans a paste of copied events at a time, onto the row of an entity or onto their own rows, as one entry. See
    /// <see cref="TimelineEdits.Paste"/>.
    /// </summary>
    /// <param name="time">The start of the pasted events, for example the playhead.</param>
    /// <param name="row">The name of the entity of the row of the paste, or null to keep the targets.</param>
    internal static CutscenePlan Paste(EditSession session, string path, TimelineClip clip, double time, string? row)
    {
        var label = TimelineEdits.ClipLabel("Paste", clip.Events.Count);
        path = ScriptPath(session, path);
        if (clip.Events.Count == 0)
            return CutscenePlan.Refused(label, path, "Copy one or more events first.");
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        return CutscenePlan.Of(label, path, () => TimelineEdits.Paste(path, clip, time, row),
            newEvents: clip.Events.Count);
    }

    /// <summary>
    /// Plans a copy of events of one cutscene right after the events, as Duplicate of Unity does, in one entry. See
    /// <see cref="TimelineEdits.Duplicate"/>.
    /// </summary>
    public static CutscenePlan Duplicate(EditSession session, IReadOnlyList<ThnRef> events) =>
        Duplicate(session, events, null);

    /// <summary>
    /// Plans a copy of events of one cutscene, each copy at the time of its event plus an offset, in one entry. The
    /// times round to the 1 ms grid. See <see cref="TimelineEdits.Paste"/>.
    /// </summary>
    /// <remarks>A copy at the time of its event has the time, the type and the targets of the event, so a later mod
    /// that names the event by that key finds two events (see <see cref="TimelineEdits.Duplicate"/>). Such an offset,
    /// for example 0, refuses with the detail <see cref="MissionErrors.FieldValue"/>. A copy before 0 and a copy time
    /// that is not a finite float refuse with that detail too.</remarks>
    /// <param name="timeOffset">The seconds from the time of each event to the time of its copy.</param>
    public static CutscenePlan Duplicate(EditSession session, IReadOnlyList<ThnRef> events, double timeOffset) =>
        Duplicate(session, events, (double?)timeOffset);

    /// <summary>A copy of events after the events, or at an offset from them.</summary>
    private static CutscenePlan Duplicate(EditSession session, IReadOnlyList<ThnRef> events, double? timeOffset)
    {
        var path = events.Count > 0 ? ScriptPath(session, events[0].Path) : "";
        var clip = session.Cutscene(path) is { } working ? TimelineEdits.Copy(working, events) : new TimelineClip([]);
        var label = TimelineEdits.ClipLabel("Duplicate", clip.Events.Count);
        if (events.Count == 0)
            return CutscenePlan.Refused(label, path, "There is no event to duplicate.");
        if (!ThnEdits.InOneCutscene(events))
            return CutscenePlan.Refused(label, path, ThnEdits.OneCutscene);
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        if (clip.Events.Count == 0)
            return CutscenePlan.Refused(label, path, ThnEdits.NoSuchElement(ThnElementKind.Event));
        if (timeOffset is not { } offset)
            return CutscenePlan.Of(label, path, () => TimelineEdits.Duplicate(path, clip), newEvents: clip.Events.Count);
        // A time is a float, so an offset that is finite as a double can still give a copy time that is not finite.
        if (clip.Events.Any(e => !float.IsFinite((float)(TimelineClip.TimeOf(e) + offset))))
            return CutscenePlan.Refused(label, path, ThnNumber.NotFiniteMessage, MissionErrors.FieldValue);
        var start = clip.Start + offset;
        if (start < 0)
            return CutscenePlan.Refused(label, path, ThnEdits.StartsBeforeZero, MissionErrors.FieldValue);
        if (clip.Events.Any(e => ThnEdits.OnGrid(TimelineClip.TimeOf(e) + offset) == TimelineClip.TimeOf(e)))
            return CutscenePlan.Refused(label, path, SameKeyCopy, MissionErrors.FieldValue);
        return CutscenePlan.Of(label, path, () => TimelineEdits.Paste(path, clip, start, null, "Duplicate"),
            newEvents: clip.Events.Count);
    }

    /// <summary>The reason why a copy cannot have the time of its event.</summary>
    internal const string SameKeyCopy = "A copy at the time of its event has the key of the event, so a mod that names " +
                                        "the event finds two events. Use an offset of 1 ms or more.";

    /// <summary>Plans a move of an event before another event of the same time (Move before). See
    /// <see cref="ThnEdits.Reorder"/>.</summary>
    /// <remarks>The reasons of <see cref="ThnEdits.ReorderRefusal"/> refuse the plan.</remarks>
    public static CutscenePlan Reorder(EditSession session, ThnRef ev, ThnRef before)
    {
        const string label = "Move event before";
        var path = ScriptPath(session, ev.Path);
        string? refusal;
        try
        {
            refusal = ThnEdits.ReorderRefusal(session, ev, before);
        }
        catch (ArgumentException e)
        {
            return CutscenePlan.Refused(label, path, ErrorText.Of(e));
        }

        return refusal != null
            ? CutscenePlan.Refused(label, path, refusal)
            : CutscenePlan.Of(label, path, () => ThnEdits.Reorder(ev, before), changed: [ev]);
    }

    /// <summary>Plans the start pose of an entity: its position and its rotation, in one entry, for example Set camera
    /// from view. See <see cref="ThnGizmoTarget.SetStartPose"/>.</summary>
    /// <param name="label">The label of the entry of the edit history.</param>
    public static CutscenePlan SetStartPose(EditSession session, ThnRef entity, ThnPoseValue pose, string label)
    {
        var path = ScriptPath(session, entity.Path);
        if (session.CutsceneReadOnlyReason(entity) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        return CutscenePlan.Of(label, path, () => ThnGizmoTarget.SetStartPose(entity, pose, label), changed: [entity]);
    }

    /// <summary>
    /// Plans a new entity at the end of the entities: the entity of <see cref="ThnTemplates.NewEntity"/>, with the
    /// records that the engine needs for its type. See <see cref="ThnEdits.AddEntity"/>.
    /// </summary>
    /// <remarks>
    /// <para>A type that is not in <see cref="ThnTemplates.EntityTypes"/> refuses with the detail
    /// <see cref="MissionErrors.FieldValue"/>. An empty name, and a name that a save cannot write (see
    /// <see cref="ThnWriter.TextProblem"/>), refuse with <see cref="MissionErrors.NameInvalid"/>. A template that a save
    /// cannot write refuses with <see cref="MissionErrors.FieldValue"/>. A name that another entity has, without regard
    /// to letter case, refuses with <see cref="MissionErrors.NameTaken"/>, because the engine finds an entity by its name
    /// without letter case.</para>
    /// <para>A position or a rotation for a type without a place (see <see cref="ThnTemplates.HasPlace"/>) refuses with
    /// <see cref="MissionErrors.NoPlace"/>. No retail script has two SCENE entities. So a second SCENE refuses with
    /// <see cref="MissionErrors.OneScene"/>.</para>
    /// <para>The checks of the session do not read the game data. So a template that the game data does not have is
    /// not a refusal. The save validation of the editor and the check of a cutscene with game data report it.</para>
    /// </remarks>
    /// <param name="path">The install path of the script.</param>
    /// <param name="type">The THN name of the entity type, for example <c>CAMERA</c>.</param>
    /// <param name="name">The name of the entity.</param>
    /// <param name="template">The template, for example the costume of a DEFORMABLE, or null for none.</param>
    /// <param name="position">The position, or null for the origin.</param>
    /// <param name="rotation">The rotation, or null for no turn.</param>
    public static CutscenePlan AddEntity(EditSession session, string path, string type, string name, string? template,
        Vector3? position, Matrix4x4? rotation)
    {
        var label = $"Add {name}";
        path = ScriptPath(session, path);
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        if (ThnTemplates.NewEntityProblem(type, name, template, position != null || rotation != null) is { } problem)
            return CutscenePlan.Refused(label, path, problem.Message, problem.Detail);
        var working = session.Cutscene(path)!;
        if (ThnEdits.NameTakenRefusal(working, name) is { } taken)
            return CutscenePlan.Refused(label, path, taken, MissionErrors.NameTaken);
        if (type == SceneType &&
            working.Entities.Any(e => ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, session.ThnSymbols) == SceneType))
            return CutscenePlan.Refused(label, path, OneScene, MissionErrors.OneScene);
        return CutscenePlan.Of(label, path,
            () => ThnEdits.AddEntity(path, ThnTemplates.NewEntity(type, name, template, position, rotation)),
            newEntities: 1);
    }

    /// <summary>The reason why a script cannot get a second SCENE entity.</summary>
    internal const string OneScene = "The script has a SCENE entity. No retail script has two SCENE entities.";

    private const string SceneType = "SCENE";

    /// <summary>
    /// Plans a new event of a type at a time, with the usual props of the type (see
    /// <see cref="ThnTemplates.NewEvent"/>). The time rounds to the 1 ms grid. Each target names an entity of W that
    /// fits the role of the target, and the event gets the name in the letter case of the entity. The engine runs the
    /// new event after the events of the same time.
    /// </summary>
    /// <remarks>
    /// <para>A type that is not in <see cref="ThnTemplates.Roles"/>, a time below 0 and a time that is not finite
    /// refuse with the detail <see cref="MissionErrors.FieldValue"/>. Another number of targets than the type takes
    /// refuses with <see cref="MissionErrors.TargetCount"/>. A target refuses as in <see cref="SetTarget"/>.</para>
    /// <para>A length rounds to the 1 ms grid, as the length field of the Inspector does. A length for a type without
    /// a length (see <see cref="ThnSchema.HasLength"/>), a negative length and a length that is not finite refuse with
    /// <see cref="MissionErrors.FieldValue"/>.</para>
    /// </remarks>
    /// <param name="path">The install path of the script.</param>
    /// <param name="type">The THN name of the event type, for example <c>START_PSYS</c>.</param>
    /// <param name="time">The time of the event, in seconds.</param>
    /// <param name="targets">The names of the entities of the targets: one, or two for a type with a second
    /// target.</param>
    /// <param name="length">The length of the event in seconds (<c>props.duration</c>), or null for the length of the
    /// template.</param>
    public static CutscenePlan AddEvent(EditSession session, string path, string type, double time,
        IReadOnlyList<string> targets, double? length = null)
    {
        var label = $"Add {type}";
        path = ScriptPath(session, path);
        if (session.CutsceneReadOnlyReason(path) is { } reason)
            return CutscenePlan.Refused(label, path, reason);
        if (ThnTemplates.Role(type) is not { } role)
        {
            return CutscenePlan.Refused(label, path, $"{type} is not an event type that the editor can add. The types " +
                                                     $"are {string.Join(", ", ThnTemplates.Roles.Select(r => r.Type))}.",
                MissionErrors.FieldValue);
        }
        // A time is a float, so a finite double can still give a time that is not finite.
        var onGrid = ThnEdits.OnGrid(time);
        if (!float.IsFinite(onGrid))
            return CutscenePlan.Refused(label, path, ThnNumber.NotFiniteMessage, MissionErrors.FieldValue);
        if (onGrid < 0)
            return CutscenePlan.Refused(label, path, ThnEdits.StartsBeforeZero, MissionErrors.FieldValue);
        float? newLength = null;
        if (length is { } seconds)
        {
            if (!ThnSchema.HasLength(type))
                return CutscenePlan.Refused(label, path, $"A {type} event has no length.", MissionErrors.FieldValue);
            newLength = ThnEdits.OnGrid(seconds);
            if (!float.IsFinite(newLength.Value))
                return CutscenePlan.Refused(label, path, ThnNumber.NotFiniteMessage, MissionErrors.FieldValue);
            if (newLength < 0)
                return CutscenePlan.Refused(label, path, ThnEdits.NegativeLength, MissionErrors.FieldValue);
        }
        if (targets.Count == 0 || targets.Count > (role.Has(2) ? 2 : 1) || role.SecondRequired && targets.Count < 2)
            return CutscenePlan.Refused(label, path, TargetCount(role), MissionErrors.TargetCount);
        var working = session.Cutscene(path)!;
        var symbols = session.ThnSymbols;
        for (var i = 0; i < targets.Count; i++)
        {
            if (TargetRefusal(working, role, i + 1, targets[i], symbols) is { } refusal)
                return CutscenePlan.Refused(label, path, refusal.Message, refusal.Detail);
        }

        var first = EntityName(working, targets[0]);
        var second = targets.Count > 1 ? EntityName(working, targets[1]) : null;
        return CutscenePlan.Of(label, path, () =>
        {
            var ev = ThnTemplates.NewEvent(type, onGrid, first, second);
            if (newLength is { } value)
                ev = ThnElementPaths.Set(ThnElementKind.Event, ev, LengthPath, new ThnNumber(value));
            return ThnEdits.AddEvent(path, ev);
        }, newEvents: 1);
    }

    /// <summary>The points of the curve of an event in W, or null when the event has no curve with a list of
    /// points.</summary>
    internal static IReadOnlyList<ThnCurvePoint>? CurvePoints(EditSession session, ThnRef ev) =>
        CurveOf(session, ev) is { } curve ? ThnCurve.Points(curve) : null;

    /// <summary>The reason why a curve point cannot change, when the curve of the event has no point at the
    /// index.</summary>
    internal static string NoCurvePoint(int index) => $"The curve of the event has no point {index + 1}.";

    /// <summary>The path of the targets of an event.</summary>
    private static readonly ThnPath TargetsPath = new(ThnElementPaths.EventSlots[2]);

    /// <summary>The path of the length of an event.</summary>
    private static readonly ThnPath LengthPath = new("props.duration");

    /// <summary>
    /// Why an entity of W with a name cannot be a target of an event of a role, with the detail code of the error, or
    /// null. The engine finds a target by its name without letter case (Cutscene.cs:41), so the entity is the entity
    /// that <see cref="ThnScriptModel.EntityNamed"/> gives.
    /// </summary>
    /// <param name="role">The role of the event type, or null for a type that the editor does not know. Then each
    /// entity fits.</param>
    /// <param name="target">The place of the target, from 1.</param>
    private static (string Message, string Detail)? TargetRefusal(ThnScriptModel working, ThnEventRole? role,
        int target, string name, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        if (working.EntityNamed(name) is not { } entity)
        {
            var names = working.Entities.Select(e => ThnScriptModel.NameOf(e.Table)).OfType<string>();
            return ($"No entity of this script has the name {name}.{ItemRefs.Hint(name, names)}", MissionErrors.ItemNotFound);
        }
        if (role == null)
            return null;
        if (!role.Has(target))
            return ($"{role.Type} has no target {target}.", MissionErrors.TargetType);
        var type = ThnSchema.TypeOf(ThnElementKind.Entity, entity.Table, symbols);
        if (role.Fits(target, type))
            return null;
        var fits = string.Join(" or ", target == 1 ? role.First : role.Second);
        var actual = type == null ? "has no type" : $"is a {type}";
        return ($"{role.Type} needs a {fits} as target {target}. The entity {EntityName(working, name)} {actual}.",
            MissionErrors.TargetType);
    }

    /// <summary>
    /// Why a move cannot change the length of the events, or null: an event of W has a type without a length (see
    /// <see cref="ThnSchema.HasLength"/>). A change of 0 gives null. The message names the first such event by its
    /// index in W.
    /// </summary>
    private static string? NoLengthRefusal(EditSession session, IReadOnlyList<ThnRef> events, float lengthDelta)
    {
        if (lengthDelta == 0 || events.Count == 0 || session.Cutscene(events[0].Path) is not { } working)
            return null;
        foreach (var ev in events)
        {
            var index = working.Events.FindIndex(e => e.Id == ev.Id);
            if (index >= 0 && ThnSchema.TypeOf(ThnElementKind.Event, working.Events[index].Table, session.ThnSymbols) is
                    { } type && !ThnSchema.HasLength(type))
                return $"Event {index} is a {type} event. An event of this type has no length.";
        }
        return null;
    }

    /// <summary>The reason why an event of a role cannot have the number of targets of a request.</summary>
    private static string TargetCount(ThnEventRole role) =>
        role.SecondRequired ? $"{role.Type} needs two targets."
        : role.Has(2) ? $"{role.Type} takes one or two targets."
        : $"{role.Type} takes one target.";

    /// <summary>The name of the entity of W that the engine finds for a name, in the letter case of the entity, or the
    /// name when W has no such entity.</summary>
    private static string EntityName(ThnScriptModel working, string name) =>
        working.EntityNamed(name) is { } entity ? ThnScriptModel.NameOf(entity.Table) ?? name : name;

    /// <summary>
    /// Why a value at a path of an entity gives the entity a path text that the engine must not run, or null. The
    /// engine reads <see cref="ThnMotionPath.Path"/> of an entity of each type, also from a <c>pathprops</c> table with
    /// list items. So the check reads the entity after the edit as the engine does (see
    /// <see cref="ThnMotionPath.DataOf"/>), whatever the path of the edit is. A path that the session command refuses
    /// gives null, because the plan then refuses with the reason of the command.
    /// </summary>
    private static string? NewPathProblem(EditSession session, ThnRef target, ThnPath path, ThnValue value)
    {
        if (target.Kind != ThnElementKind.Entity || Table(session, target) is not { } table)
            return null;
        ThnTable after;
        try
        {
            after = ThnElementPaths.Set(ThnElementKind.Entity, table, path, value);
        }
        catch (Exception e) when (e is ArgumentException or ThnPathException)
        {
            return null;
        }
        return ThnMotionPath.DataOf(after) is ThnText text && !text.Equals(ThnMotionPath.DataOf(table)) &&
               ThnMotionPath.TextProblem(text.Value) is { } problem
            ? $"The engine reads {ThnMotionPath.Path} of an entity of each type as a path. {problem}"
            : null;
    }

    /// <summary>The table of an element in W, or null.</summary>
    private static ThnTable? Table(EditSession session, ThnRef element) =>
        session.Cutscene(element.Path)?.Element(element.Kind, element.Id)?.Table;

    /// <summary>The curve record of an event in W, or null.</summary>
    private static ThnTable? CurveOf(EditSession session, ThnRef ev) =>
        session.Cutscene(ev.Path)?.Element(ThnElementKind.Event, ev.Id)?.Table is { } table &&
        ThnCurve.Path.ValueIn(ThnElementKind.Event, table) is ThnTable { IsRecord: true } curve
            ? curve
            : null;

    /// <summary>The install path of a script in the form that the session stores, or the given path when the session
    /// cannot open the script. So a plan names a script in one form, whatever the letter case or the separators of the
    /// caller.</summary>
    private static string ScriptPath(EditSession session, string path) => session.CutscenePath(path) ?? path;
}
