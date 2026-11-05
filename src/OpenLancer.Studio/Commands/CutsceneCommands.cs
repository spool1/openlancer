using System.Collections.Generic;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;

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
/// hold refuses with the detail <see cref="MissionErrors.FieldValue"/>.</para>
/// </remarks>
public static class CutsceneCommands
{
    /// <summary>
    /// Plans a value at a path of an entity or an event. Each missing record on the path is made. See
    /// <see cref="ThnEdits.Set"/>.
    /// </summary>
    public static CutscenePlan SetValue(EditSession session, ThnRef target, ThnPath path, ThnValue value)
    {
        var label = $"Set {path}";
        var script = ScriptPath(session, target.Path);
        if (session.CutsceneReadOnlyReason(target) is { } reason)
            return CutscenePlan.Refused(label, script, reason);
        return CutscenePlan.Of(label, script, () => ThnEdits.Set(target, path, value), changed: [target]);
    }

    /// <summary>
    /// Plans a move of events in time and a change of their length (<c>props.duration</c>), as one entry. The new times
    /// and lengths round to the 1 ms grid. A change of 0 and 0 gives a plan that changes nothing. See
    /// <see cref="ThnEdits.MoveEvents"/>.
    /// </summary>
    /// <remarks>The plan checks the values of the events in W before it runs (see <see cref="ThnEdits.MoveRefusal"/>).
    /// A time below 0, a negative length, or a time or a length to change that is not a number refuses with the detail
    /// <see cref="MissionErrors.FieldValue"/>.</remarks>
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
        if (plan.CanRun && ThnEdits.MoveRefusal(session, events, timeDelta, lengthDelta) is { } refusal)
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

    /// <summary>The points of the curve of an event in W, or null when the event has no curve with a list of
    /// points.</summary>
    internal static IReadOnlyList<ThnCurvePoint>? CurvePoints(EditSession session, ThnRef ev) =>
        CurveOf(session, ev) is { } curve ? ThnCurve.Points(curve) : null;

    /// <summary>The reason why a curve point cannot change, when the curve of the event has no point at the
    /// index.</summary>
    internal static string NoCurvePoint(int index) => $"The curve of the event has no point {index + 1}.";

    /// <summary>The curve record of an event in W, or null.</summary>
    private static ThnTable? CurveOf(EditSession session, ThnRef ev) =>
        session.Cutscene(ev.Path)?.Element(ThnElementKind.Event, ev.Id)?.Table is { } table &&
        ThnCurve.Path.ValueIn(ThnElementKind.Event, table) is ThnTable { IsRecord: true } curve
            ? curve
            : null;

    /// <summary>The install path of a script in the form that the session stores, or the given path when the session
    /// cannot open the script. So a plan names a script in one form, whatever the letter case or the separators of the
    /// caller.</summary>
    private static string ScriptPath(EditSession session, string path) => session.CutsceneDocument(path)?.Path ?? path;
}
