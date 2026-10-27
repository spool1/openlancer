using System;
using System.Numerics;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Editor.Scene;

/// <summary>A problem of a drag of the gizmo, for the Console.</summary>
/// <param name="Target">The item of the drag (see <see cref="IGizmoTarget.Key"/>).</param>
internal sealed record GizmoProblem(ConsoleSeverity Severity, string Message, object Target);

/// <summary>
/// The drag of the gizmo from frame to frame (sections 10.2 and 10.4 of the design). When ImGuizmo starts to drag, it
/// opens a gesture of the command layer (see <see cref="CommandRunner.Gesture"/>) and the drag of its target in it (see
/// <see cref="IGizmoTarget.Begin"/>). It gives the drag the raw matrix of each frame. It ends the drag on the release of
/// the left button, on Esc or the right button, or when its item is not the selection any more. The Scene drags a
/// section, and the Cutscene view drags an element of a cutscene. It has no ImGui call, so a test can
/// script the frames. <see cref="TransformGizmo"/> gives it what ImGui and ImGuizmo report in each frame, and clears
/// the drag state of ImGuizmo when a method returns true.
/// </summary>
/// <remarks>
/// The gizmo gets a shadow matrix in each frame: the matrix of the target before a drag, and the raw result of the last
/// frame during a drag. The snapped values go to W and never back into the gizmo, because a snapped matrix would stop
/// the turn of ImGuizmo at the step.
/// </remarks>
internal sealed class GizmoDragFlow
{
    private IGizmoDrag? drag;
    private Matrix4x4 shadow;

    // The target, the tool and the space of the gizmo of this frame, from Prepare.
    private (IGizmoTarget Target, GizmoTool Tool, GizmoSpace Space)? prepared;

    public GizmoDragState State { get; private set; }

    /// <summary>The item of the open drag, or of the gizmo of this frame when no drag is open (see
    /// <see cref="IGizmoTarget.Key"/>). Null when the gizmo does not show.</summary>
    public object? Key { get; private set; }

    /// <summary>The ImGuizmo operation of the gizmo of this frame.</summary>
    public GuizmoOperation Operation { get; private set; }

    /// <summary>The ImGuizmo mode of the gizmo of this frame.</summary>
    public GuizmoMode Mode { get; private set; }

    /// <summary>The matrix for the <c>Manipulate</c> call of this frame. <c>Manipulate</c> changes it in
    /// place.</summary>
    public ref Matrix4x4 Shadow => ref shadow;

    /// <summary>The ImGuizmo operation of a tool. A scale shows only the handles of the axes that the size of the zone
    /// can change, for example one X handle for a sphere (decision D29).</summary>
    public static GuizmoOperation OperationOf(GizmoTool tool, ZoneSizeAxes? axes) => tool switch
    {
        GizmoTool.Move => GuizmoOperation.TRANSLATE,
        // One drag turns about one axis, so the snap of the angle is clear.
        GizmoTool.Rotate => GuizmoOperation.ROTATE_AXIS,
        _ => (axes?.FreeX == true ? GuizmoOperation.SCALE_X : 0) | (axes?.FreeY == true ? GuizmoOperation.SCALE_Y : 0) |
             (axes?.FreeZ == true ? GuizmoOperation.SCALE_Z : 0),
    };

    /// <summary>
    /// Step 1 of the frame: ends, cancels or keeps the drag before the frame reads the mouse (see
    /// <see cref="GizmoInput.Frame"/>). Returns true when the editor must clear the drag state of ImGuizmo.
    /// </summary>
    /// <param name="isUsing"><c>ImGuizmo.IsUsing()</c> before the <c>Manipulate</c> call of this frame.</param>
    /// <param name="cancel">Esc or the right button was pressed in this frame.</param>
    /// <param name="selected">The item of the selection (see <see cref="IGizmoTarget.Key"/>), or null.</param>
    public bool Start(bool isUsing, bool leftDown, bool cancel, object? selected)
    {
        var frame = GizmoInput.Frame(State, isUsing, leftDown, cancel, !Equals(selected, Key));
        switch (frame.Step)
        {
            case GizmoFrameStep.End:
                Finish(commit: true);
                break;
            case GizmoFrameStep.Cancel:
                Finish(commit: false);
                break;
        }

        State = frame.State;
        return frame.Reset;
    }

    /// <summary>
    /// Sets the matrix, the operation and the mode of the gizmo of this frame. During a drag they stay as they were at
    /// its start. Returns false when the gizmo does not show: no drag is open, and the target is null or does not take
    /// the tool.
    /// </summary>
    public bool Prepare(IGizmoTarget? target, GizmoTool tool, GizmoSpace space)
    {
        if (State == GizmoDragState.Open)
        {
            return true;
        }

        prepared = null;
        Key = null;
        if (target == null || target.Tools[tool] != null)
        {
            return false;
        }

        prepared = (target, tool, space);
        Key = target.Key;
        shadow = target.Matrix(tool);
        Operation = OperationOf(tool, target.Axes);
        Mode = GizmoInput.SpaceOf(tool, space) == GizmoSpace.World ? GuizmoMode.WORLD : GuizmoMode.LOCAL;
        return true;
    }

    /// <summary>
    /// Step 7 of the frame, after the <c>Manipulate</c> call (see <see cref="GizmoInput.After"/>): opens a drag, gives
    /// the raw matrix to the open drag, or asks to clear ImGuizmo. Returns true when the editor must clear the drag
    /// state of ImGuizmo.
    /// </summary>
    /// <param name="isUsing"><c>ImGuizmo.IsUsing()</c> after the <c>Manipulate</c> call.</param>
    /// <param name="commands">Opens the gesture of a drag. It refuses the gesture when the editor takes no edits
    /// now.</param>
    /// <param name="snap">The snap of this frame, with Ctrl (see <see cref="GizmoSnap.WithCtrl"/>).</param>
    /// <param name="problem">Why a drag did not open or stopped, or null.</param>
    public bool After(bool isUsing, bool leftDown, CommandRunner commands, GizmoSnap snap, out GizmoProblem? problem)
    {
        problem = null;
        switch (GizmoInput.After(State, isUsing, leftDown))
        {
            case GizmoAfter.Begin when prepared is { } gizmo:
                var (target, tool, space) = gizmo;
                try
                {
                    // The runner refuses the gesture when the editor takes no edits. When the drag cannot start, also
                    // after an exception of another type, the runner cancels the gesture.
                    drag = commands.Gesture(target.DragLabel(tool),
                        gesture => target.Begin(gesture, tool, GizmoInput.SpaceOf(tool, space), noEditReason: null));
                }
                catch (InvalidOperationException e)
                {
                    problem = new GizmoProblem(ConsoleSeverity.Warning, $"The gizmo did not start a drag: {e.Message}",
                        target.Key);
                    return true;
                }

                State = GizmoDragState.Open;
                return false;
            case GizmoAfter.Update:
                try
                {
                    drag!.Update(shadow, snap);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    problem = new GizmoProblem(ConsoleSeverity.Error, $"The drag of the gizmo stopped: {ErrorText.Of(e)}",
                        Key!);
                    Finish(commit: false);
                    State = GizmoDragState.Ended;
                    return true;
                }

                return false;
            case GizmoAfter.None:
                return false;
            default:
                // ImGuizmo drags, but the editor opens no drag: after a cancel, or without a gizmo in this frame.
                return true;
        }
    }

    /// <summary>
    /// A forced end (section 10.4 of the design): commits an open drag with the line of its last frame. The scene calls
    /// this before a system closes, and the window before it writes the recovery file at the close. The editor then
    /// clears ImGuizmo.
    /// </summary>
    public void End()
    {
        Finish(commit: true);
        State = GizmoDragState.None;
        prepared = null;
        Key = null;
    }

    /// <summary>Commits or cancels the open drag. Does nothing when no drag is open.</summary>
    private void Finish(bool commit)
    {
        if (drag is not { } ending)
        {
            return;
        }

        drag = null;
        if (commit)
        {
            ending.End();
        }
        else
        {
            ending.Cancel();
        }
    }
}
