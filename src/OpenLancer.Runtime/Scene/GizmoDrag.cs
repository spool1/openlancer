using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Runtime.Scene;

/// <summary>
/// The axes of a move or a rotate: the axes of the item, or the world axes. A scale always uses the axes of the item.
/// A drag snaps a move along the axes of its space. A turn snaps about its own axis in both spaces (see
/// <see cref="TransformValues.SnapRotation"/>).
/// </summary>
public enum GizmoSpace
{
    Local,
    World,
}

/// <summary>The snap of a drag (decision D27): on or off, and the step of each tool.</summary>
/// <param name="Move">The step of a move along each axis.</param>
/// <param name="RotateDegrees">The step of a turn, in degrees.</param>
/// <param name="Size">The step of a size on each axis.</param>
public readonly record struct GizmoSnap(bool On, float Move, float RotateDegrees, float Size)
{
    /// <summary>The snap of one frame of a drag: Ctrl reverses the snap toggle.</summary>
    public GizmoSnap WithCtrl(bool ctrl) => this with { On = On != ctrl };
}

/// <summary>
/// One drag of the gizmo as one entry of the edit history: a gesture of the command layer (see
/// <see cref="CommandTransaction"/>). The drag changes one key of one section of W: <c>pos</c> for a move,
/// <c>rotate</c> for a rotate and <c>size</c> for a scale. It has no ImGui call, so a test can script it with
/// matrices.
/// </summary>
/// <remarks>
/// <para>The drag starts from the lines of W, not from the world object (rule 2 of section 9.3 of the design). Each
/// frame gives the raw matrix of the gizmo. The drag makes the value of the tool from it, and the line of the key from
/// the value with the rules of <see cref="TransformPlans"/>. When the line changed, a step of the gesture runs the plan
/// of <see cref="TransformPlans"/>, so the transform command of the command layer and the drag use one rule. A line
/// that equals the start line puts back the edit view as it was, so a drag back to the start removes no pin and adds no
/// entry.</para>
/// <para>The snap acts on the values that go to W. The gizmo keeps its raw matrix, because a snapped matrix would stop
/// the turn of ImGuizmo at the step.</para>
/// <para>The tool and the space stay the same from <see cref="Begin"/> to the end. ImGuizmo reads its operation and its
/// mode in each frame, so the editor must not change them while a drag is open.</para>
/// </remarks>
public sealed class GizmoDrag : IGizmoDrag
{
    /// <summary>
    /// The scale of an axis is its start size times the ratio of the lengths of the axis in the raw matrix and in the
    /// start matrix. The ratio rounds to this number of decimals. ImGuizmo measures and normalizes the axes in floats,
    /// so an axis that the drag did not change comes back with a ratio about 1e-7 from 1 (ImGuizmo.cpp:2297-2303). With
    /// the rounding, such an axis keeps its start size, and a drag of an exact ratio gives an exact size.
    /// </summary>
    private const int ScaleRatioDigits = 5;

    private readonly GizmoTarget target;
    private readonly GizmoTool tool;
    private readonly GizmoSpace space;
    private readonly CommandTransaction gesture;

    /// <summary>The values of the line at the start, or an empty list when the section had no line.</summary>
    private readonly IReadOnlyList<ValueBase> start;

    /// <summary>The line that the edit view holds now.</summary>
    private IReadOnlyList<ValueBase> applied;

    private bool ended;

    private GizmoDrag(GizmoTarget target, GizmoTool tool, GizmoSpace space, CommandTransaction gesture)
    {
        this.target = target;
        this.tool = tool;
        this.space = space;
        this.gesture = gesture;
        start = target.StartLine(tool) ?? [];
        applied = start;
        Start = target.Matrix(tool);
    }

    /// <summary>The matrix that the gizmo starts from (see <see cref="GizmoTarget.Matrix"/>).</summary>
    public Matrix4x4 Start { get; }

    /// <summary>
    /// Reads the start lines of a section from W again, and starts a drag in a gesture. The editor opens the gesture with
    /// the label of the tool and the nickname (see <see cref="GizmoTarget.DragLabel"/>), for example
    /// <c>Move Li01_14_depot_water</c>. The drag owns the gesture after the start.
    /// </summary>
    /// <param name="gesture">The open gesture of the drag. When the drag cannot start, the gesture stays open, and the
    /// caller cancels it.</param>
    /// <param name="target">The section to drag. The drag starts from the lines that W holds now, also when they
    /// changed after <see cref="GizmoTarget.Of"/>.</param>
    /// <param name="noEditReason">Why the editor takes no edits now, or null (see <see cref="GizmoTarget.Of"/>).</param>
    /// <exception cref="InvalidOperationException">The editor or the session takes no edits, the gizmo cannot change the
    /// section now (see <see cref="GizmoTarget.Of"/>), or the section does not take the tool. Nothing changes.</exception>
    public static GizmoDrag Begin(CommandTransaction gesture, GizmoTarget target, GizmoTool tool, GizmoSpace space,
        string? noEditReason)
    {
        var now = GizmoTarget.Of(gesture.Session, target.Section, noEditReason, out var reason) ??
                  throw new InvalidOperationException(reason);
        if (now.Tools[tool] is { } disabled)
            throw new InvalidOperationException(disabled);
        return new GizmoDrag(now, tool, space, gesture);
    }

    /// <summary>
    /// Takes the raw matrix of the gizmo for this frame. The drag makes the line of its key from the matrix and applies
    /// it when it differs from the line that the edit view holds. A matrix with a value that is not finite changes
    /// nothing. For a rotate, neither does a matrix that is not a scale, a rotation and a translation
    /// (<see cref="Matrix4x4.Decompose"/> fails).
    /// </summary>
    /// <param name="snap">The snap of this frame (see <see cref="GizmoSnap.WithCtrl"/>).</param>
    /// <exception cref="InvalidOperationException">The drag has ended, or the plan of the frame refuses. The edit view is
    /// then as it was before the drag.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The snap is on, and the step of the tool is 0 or less.</exception>
    public void Update(Matrix4x4 raw, GizmoSnap snap)
    {
        ThrowIfEnded();
        if (ValueOf(raw, snap) is not { } value)
            return;
        var line = TransformPlans.Line(tool, start, target.Axes, value);
        if (SceneLines.Same(line, applied))
            return;
        if (SceneLines.Same(line, start))
        {
            gesture.Reset();
        }
        else if (gesture.Apply(session => TransformPlans.Plan(session, target.Section, tool, value)) is
                 { Status: EditStatus.Refused } refused)
        {
            applied = start;
            throw new InvalidOperationException(refused.Errors[0].Message);
        }
        applied = line;
    }

    /// <summary>Ends the drag with the line that the edit view holds: one entry of the edit history, or none when the
    /// line is the start line.</summary>
    /// <exception cref="InvalidOperationException">The drag has ended.</exception>
    public void End()
    {
        ThrowIfEnded();
        ended = true;
        gesture.Commit();
    }

    /// <summary>Ends the drag and puts back the edit view as it was before the drag. Does nothing after the
    /// end.</summary>
    public void Cancel()
    {
        if (ended)
            return;
        ended = true;
        gesture.Cancel();
    }

    /// <summary>Cancels the drag when it did not end.</summary>
    public void Dispose() => Cancel();

    /// <summary>The value of the tool after a frame, or null when the frame gives no value: the position, the angles in
    /// degrees, or the scale of the gizmo.</summary>
    private Vector3? ValueOf(Matrix4x4 raw, GizmoSnap snap)
    {
        if (!IsFinite(raw))
            return null;
        switch (tool)
        {
            case GizmoTool.Move:
                return snap.On
                    ? TransformValues.SnapTranslation(target.Position, raw.Translation, snap.Move,
                        space == GizmoSpace.Local ? target.Rotation : null)
                    : raw.Translation;
            case GizmoTool.Rotate:
                if (!Matrix4x4.Decompose(raw, out _, out var rotation, out _))
                    return null;
                if (snap.On)
                    rotation = TransformValues.SnapRotation(target.Rotation, rotation, snap.RotateDegrees);
                return TransformValues.EulerNear(Matrix4x4.CreateFromQuaternion(rotation), target.Degrees);
            default:
                var axes = target.Axes!;
                var scale = new Vector3(Size(raw, 0, axes.GizmoScale.X), Size(raw, 1, axes.GizmoScale.Y),
                    Size(raw, 2, axes.GizmoScale.Z));
                if (snap.On)
                {
                    scale = new Vector3(TransformValues.SnapSize(axes.GizmoScale.X, scale.X, snap.Size),
                        TransformValues.SnapSize(axes.GizmoScale.Y, scale.Y, snap.Size),
                        TransformValues.SnapSize(axes.GizmoScale.Z, scale.Z, snap.Size));
                }
                return scale;
        }
    }

    /// <summary>The size of an axis after a frame (see <see cref="ScaleRatioDigits"/>).</summary>
    /// <param name="axis">0 for X, 1 for Y, 2 for Z: the row of the matrix.</param>
    private float Size(Matrix4x4 raw, int axis, float startSize) =>
        (float)(startSize * Math.Round(Length(raw, axis) / Length(Start, axis), ScaleRatioDigits,
            MidpointRounding.AwayFromZero));

    /// <summary>The length of a row of the rotation and scale part of a matrix.</summary>
    private static double Length(Matrix4x4 matrix, int row)
    {
        double x = matrix[row, 0], y = matrix[row, 1], z = matrix[row, 2];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static bool IsFinite(Matrix4x4 matrix)
    {
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
        {
            if (!float.IsFinite(matrix[row, column]))
                return false;
        }
        return true;
    }

    private void ThrowIfEnded()
    {
        if (ended)
            throw new InvalidOperationException("The drag has ended.");
    }
}
