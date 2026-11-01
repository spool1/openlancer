using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// One drag of a point of the curve of an event in the curve editor, as one entry of the edit history (section 7.5 of
/// the M8 design). Each frame gives the time and the value of the point. The drag runs
/// <see cref="CutsceneCommands.SetCurvePoint"/> in each step of one gesture (<see cref="CommandTransaction"/>, decision
/// D766), and the release of the mouse commits it. It has no ImGui call, so a test can script it.
/// </summary>
/// <remarks>The time of a point stays between the times of the points before and after it, and from 0 to 1, because
/// the engine finds the segment of a time in the order of the points (ParameterCurve.cs:120-129).</remarks>
internal sealed class CurvePointDrag : IDisposable
{
    private readonly CommandTransaction gesture;
    private readonly ThnRef ev;
    private readonly IReadOnlyList<ThnCurvePoint> points;
    private readonly int index;
    private ThnCurvePoint applied;

    private CurvePointDrag(CommandTransaction gesture, ThnRef ev, IReadOnlyList<ThnCurvePoint> points, int index)
    {
        this.gesture = gesture;
        this.ev = ev;
        this.points = points;
        this.index = index;
        applied = points[index];
    }

    /// <summary>The point that the edit view holds now.</summary>
    public ThnCurvePoint Applied => applied;

    /// <summary>Opens the gesture of a drag of a point of the curve of an event and starts the drag in it, from the
    /// curve that W holds now.</summary>
    /// <param name="commands">Opens the gesture. It refuses when the editor takes no edits now.</param>
    /// <exception cref="InvalidOperationException">The editor takes no edits, a transaction or a batch is open, the
    /// cutscene is read-only, or the event has no such point. No gesture stays open, and nothing changes.</exception>
    public static CurvePointDrag Begin(CommandRunner commands, ThnRef ev, int index) =>
        commands.Gesture($"Move point {index + 1} of the curve", gesture =>
        {
            if (gesture.Session.CutsceneReadOnlyReason(ev) is { } reason)
                throw new InvalidOperationException(reason);
            if (CutsceneCommands.CurvePoints(gesture.Session, ev) is not { } points || index < 0 ||
                index >= points.Count)
                throw new InvalidOperationException(CutsceneCommands.NoCurvePoint(index));
            return new CurvePointDrag(gesture, ev, points, index);
        });

    /// <summary>Takes the time and the value of the point in this frame, and runs the step when they changed. The
    /// start point puts back the edit view as it was.</summary>
    /// <param name="time">The time as a part of the period.</param>
    /// <returns>Why the step did not apply, or null. After a refusal, the edit view holds the state before the drag,
    /// and the caller cancels the drag.</returns>
    /// <exception cref="InvalidOperationException">The drag has ended.</exception>
    public string? Move(float time, float value)
    {
        ThrowIfEnded();
        if (!float.IsFinite(time) || !float.IsFinite(value))
            return null;
        var low = index > 0 ? points[index - 1].Time : 0;
        var high = index < points.Count - 1 ? points[index + 1].Time : 1;
        var point = points[index] with { Time = Math.Clamp(time, Math.Min(low, high), Math.Max(low, high)), Value = value };
        if (point == applied)
            return null;
        if (point == points[index])
        {
            gesture.Reset();
            applied = point;
            return null;
        }

        var result = gesture.Apply(session => CutsceneCommands.SetCurvePoint(session, ev, index, point));
        if (result.Status == EditStatus.Refused)
        {
            applied = points[index];
            return result.Errors.FirstOrDefault()?.Message ?? $"The step of '{gesture.Label}' did not apply.";
        }

        applied = point;
        return null;
    }

    /// <summary>Ends the drag with the point that the edit view holds: one entry, or none for no change.</summary>
    /// <exception cref="InvalidOperationException">The drag has ended.</exception>
    public void End()
    {
        ThrowIfEnded();
        gesture.Commit();
    }

    /// <summary>Ends the drag and puts back the edit view as it was before it. Does nothing after the end.</summary>
    public void Cancel() => gesture.Cancel();

    public void Dispose() => Cancel();

    private void ThrowIfEnded()
    {
        if (!gesture.IsOpen)
            throw new InvalidOperationException("The drag has ended.");
    }
}
