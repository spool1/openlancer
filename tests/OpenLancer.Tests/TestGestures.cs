using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Tests;

/// <summary>
/// Opens a drag of the gizmo as the editor does (see <c>GizmoDragFlow</c>): the gesture of the command layer with the
/// label of the drag, then the drag in it. When the drag cannot start, the gesture cancels, so no transaction stays
/// open. The unit tests and the retail tests share this file.
/// </summary>
internal static class TestGestures
{
    /// <summary>A drag of a section of a system file.</summary>
    public static GizmoDrag Drag(EditSession session, GizmoTarget target, GizmoTool tool, GizmoSpace space,
        string? noEditReason = null)
    {
        var gesture = CommandTransaction.Begin(session, target.DragLabel(tool));
        try
        {
            return GizmoDrag.Begin(gesture, target, tool, space, noEditReason);
        }
        catch
        {
            gesture.Cancel();
            throw;
        }
    }

    /// <summary>A drag of an element of a cutscene.</summary>
    public static ThnGizmoDrag Drag(EditSession session, ThnGizmoTarget target, GizmoTool tool, GizmoSpace space,
        string? noEditReason = null)
    {
        var gesture = CommandTransaction.Begin(session, target.DragLabel(tool));
        try
        {
            return ThnGizmoDrag.Begin(gesture, target, tool, space, noEditReason);
        }
        catch
        {
            gesture.Cancel();
            throw;
        }
    }
}
