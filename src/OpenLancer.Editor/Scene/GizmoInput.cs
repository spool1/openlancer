using System;
using System.Linq;
using ImGuiNET;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Editor.Scene;

/// <summary>The drag of the gizmo, as the scene view sees it.</summary>
internal enum GizmoDragState
{
    /// <summary>No drag is open.</summary>
    None,

    /// <summary>A drag is open (see <see cref="GizmoDrag"/>).</summary>
    Open,

    /// <summary>
    /// The drag ended while the left button is still down: a cancel, or a forced end. The gizmo keeps the mouse until
    /// the left button is up, so the camera does not get the rest of the motion.
    /// </summary>
    Ended,
}

/// <summary>What gets the mouse in the scene rectangle in one frame.</summary>
internal enum SceneMouse
{
    /// <summary>The scene button: the camera, the picker and the axis widget.</summary>
    Button,

    /// <summary>The gizmo, for its drag. The camera gets no input.</summary>
    Drag,

    /// <summary>The gizmo, because the mouse is over one of its handles, so that a click starts a drag. The view reads
    /// the wheel and F itself.</summary>
    Handle,
}

/// <summary>What the drag does at the start of a frame.</summary>
internal enum GizmoFrameStep
{
    None,

    /// <summary>Commit the drag.</summary>
    End,

    /// <summary>Put back the edit view as it was before the drag.</summary>
    Cancel,
}

/// <summary>The action of the drag at the start of a frame.</summary>
/// <param name="Reset">True when the editor must clear the drag state of ImGuizmo (<c>Enable(false)</c>, then
/// <c>Enable(true)</c>).</param>
/// <param name="State">The state of the drag after the action.</param>
internal readonly record struct GizmoFrame(GizmoFrameStep Step, bool Reset, GizmoDragState State);

/// <summary>What the gizmo does after its <c>Manipulate</c> call.</summary>
internal enum GizmoAfter
{
    None,

    /// <summary>ImGuizmo started a drag: open a <see cref="GizmoDrag"/>.</summary>
    Begin,

    /// <summary>Give the raw matrix of the frame to the open drag.</summary>
    Update,

    /// <summary>ImGuizmo drags while the editor has no drag open: clear its drag state.</summary>
    Reset,
}

/// <summary>
/// The rules for the mouse and the keys of the gizmo (sections 10.2 and 10.3 of the design). They have no ImGui call,
/// so a test can check each rule.
/// </summary>
/// <remarks>
/// The editor ends a drag on its own release of the left button, not when <c>ImGuizmo.IsUsing()</c> turns false. In
/// three cases <c>IsUsing()</c> stays true after the release: a rotate or a scale that ends with the mouse over another
/// window, an origin behind the camera, and a frame without a <c>Manipulate</c> call. Then the editor clears the drag
/// state of ImGuizmo, so that ImGuizmo applies no step after the release and takes the next click.
/// </remarks>
internal static class GizmoInput
{
    /// <summary>The key that changes between Local and World.</summary>
    public const ImGuiKey SpaceKey = ImGuiKey.X;

    /// <summary>The key of each tool: W moves, E rotates and R scales.</summary>
    public static readonly (GizmoTool Tool, ImGuiKey Key)[] ToolKeys =
        [(GizmoTool.Move, ImGuiKey.W), (GizmoTool.Rotate, ImGuiKey.E), (GizmoTool.Scale, ImGuiKey.R)];

    /// <summary>The input that changes the snap of a drag (see <see cref="GizmoSnap.WithCtrl"/>).</summary>
    public const string SnapInput = "Ctrl, while dragging a handle";

    /// <summary>The rows of the gizmo keys for the help popover of the Scene and Help &gt; Keys: the tool keys, the
    /// space key and the snap input.</summary>
    public static readonly (string Keys, string Action)[] KeyRows =
    [
        .. ToolKeys.Select(t => (KeyChord.KeyName(t.Key), $"Select the {ToolName(t.Tool)} tool of the gizmo.")),
        (KeyChord.KeyName(SpaceKey), "Change the gizmo between Local and World. The scale tool always uses Local."),
        (SnapInput, "Snap when the snap is off, or do not snap when it is on."),
    ];

    /// <summary>The name of a gizmo tool for the user: move, rotate or scale.</summary>
    public static string ToolName(GizmoTool tool) => tool.ToString().ToLowerInvariant();

    /// <summary>
    /// What gets the mouse in this frame. An open or ended drag keeps it. A button that was active in the last frame
    /// keeps it, because ImGui ends an active item that a frame does not draw. The gizmo gets it when the mouse is over
    /// a handle that showed in the last frame for the same target. Alt, the right button and the middle button give it
    /// to the button, so that the orbit, the fly mode and the pan win over a handle.
    /// </summary>
    /// <param name="buttonWasActive">The scene button was active in the last frame: an orbit, a pan, a fly or a click
    /// is in progress.</param>
    /// <param name="overHandle"><c>ImGuizmo.IsOver()</c>: the mouse is over a handle of the last frame.</param>
    /// <param name="shownForTarget">The gizmo showed in the last frame for the target of this frame.</param>
    public static SceneMouse Owner(GizmoDragState state, bool buttonWasActive, bool overHandle, bool shownForTarget,
        bool alt, bool rightDown, bool middleDown)
    {
        if (state != GizmoDragState.None)
        {
            return SceneMouse.Drag;
        }

        var handle = !buttonWasActive && overHandle && shownForTarget && !alt && !rightDown && !middleDown;
        return handle ? SceneMouse.Handle : SceneMouse.Button;
    }

    /// <summary>
    /// True when the view reads the wheel (zoom) and F (focus) in this frame. The button reads them while it is
    /// hovered. Over a handle, the view reads them when the scene window is hovered and the mouse is in the scene
    /// rectangle. During a drag, it does not read them.
    /// </summary>
    public static bool ReadsViewKeys(SceneMouse owner, bool buttonHovered, bool windowHovered, bool mouseInScene) =>
        owner switch
        {
            SceneMouse.Button => buttonHovered,
            SceneMouse.Handle => windowHovered && mouseInScene,
            _ => false,
        };

    /// <summary>
    /// The action of the drag at the start of a frame. An open drag ends on the release of the left button, and the
    /// editor clears ImGuizmo when it still drags. Esc or the right button cancels an open drag. A drag whose target is
    /// not the selection any more ends. After a cancel or such an end, the gizmo keeps the mouse until the left button
    /// is up. When no drag is open and ImGuizmo drags with the left button up, the editor clears ImGuizmo.
    /// </summary>
    /// <param name="isUsing"><c>ImGuizmo.IsUsing()</c> before the <c>Manipulate</c> call of this frame.</param>
    /// <param name="cancel">Esc or the right button was pressed in this frame.</param>
    /// <param name="targetLost">The section of the open drag is not the section of the selection any more.</param>
    public static GizmoFrame Frame(GizmoDragState state, bool isUsing, bool leftDown, bool cancel, bool targetLost)
    {
        if (state != GizmoDragState.Open)
        {
            return new GizmoFrame(GizmoFrameStep.None, isUsing && !leftDown, leftDown ? state : GizmoDragState.None);
        }

        if (!leftDown)
        {
            return new GizmoFrame(GizmoFrameStep.End, isUsing, GizmoDragState.None);
        }

        if (cancel)
        {
            return new GizmoFrame(GizmoFrameStep.Cancel, true, GizmoDragState.Ended);
        }

        return targetLost
            ? new GizmoFrame(GizmoFrameStep.End, true, GizmoDragState.Ended)
            : new GizmoFrame(GizmoFrameStep.None, false, GizmoDragState.Open);
    }

    /// <summary>
    /// What the gizmo does after its <c>Manipulate</c> call. An open drag takes the raw matrix. When ImGuizmo drags and
    /// no drag is open, a new drag opens if the state is <see cref="GizmoDragState.None"/> and the left button is down.
    /// Otherwise the editor clears ImGuizmo, for example after a cancel while the left button is still down.
    /// </summary>
    /// <param name="isUsing"><c>ImGuizmo.IsUsing()</c> after the <c>Manipulate</c> call.</param>
    public static GizmoAfter After(GizmoDragState state, bool isUsing, bool leftDown) => state switch
    {
        GizmoDragState.Open => GizmoAfter.Update,
        _ when !isUsing => GizmoAfter.None,
        GizmoDragState.None when leftDown => GizmoAfter.Begin,
        _ => GizmoAfter.Reset,
    };

    /// <summary>
    /// True when W, E, R and X select a tool or the space. The scene must be hovered or have the focus, and no text
    /// field takes the keys. While the right button is down, W, E and Q fly the camera. Ctrl and Alt give other
    /// commands. The tool and the space must not change while a drag is open or ended, because ImGuizmo reads them in
    /// each frame.
    /// </summary>
    public static bool ToolKeysEnabled(GizmoDragState state, bool sceneHoveredOrFocused, bool textInput, bool rightDown,
        bool ctrl, bool alt) =>
        state == GizmoDragState.None && sceneHoveredOrFocused && !textInput && !rightDown && !ctrl && !alt;

    /// <summary>
    /// The tool and the space after the tool keys of one frame. A key does what its toolbar button does, and nothing
    /// when that button is disabled. So W, E and R select only a tool that the target takes (see
    /// <see cref="ToolDisabled"/>), and X does nothing with the scale tool (see <see cref="HasSpace"/>).
    /// </summary>
    /// <param name="target">The target of the selection, or null.</param>
    /// <param name="pressed">True for a key that was pressed in this frame.</param>
    public static (GizmoTool Tool, GizmoSpace Space) Keys(GizmoTool tool, GizmoSpace space, IGizmoTarget? target,
        Func<ImGuiKey, bool> pressed)
    {
        foreach (var (next, key) in ToolKeys)
        {
            if (pressed(key) && ToolDisabled(target, next) == null)
            {
                tool = next;
            }
        }

        if (pressed(SpaceKey) && HasSpace(tool))
        {
            space = Other(space);
        }

        return (tool, space);
    }

    /// <summary>
    /// Why the toolbar button and the key of a tool cannot select it for a target, or null when they can. Without a
    /// target, each tool can be selected, so that the user can select a tool before an item.
    /// </summary>
    public static string? ToolDisabled(IGizmoTarget? target, GizmoTool tool) => target?.Tools[tool];

    /// <summary>True when the space of a tool can change. A scale always uses the axes of the item
    /// (ImGuizmo.cpp:2512-2513).</summary>
    public static bool HasSpace(GizmoTool tool) => tool != GizmoTool.Scale;

    /// <summary>The space of a drag with a tool (see <see cref="HasSpace"/>).</summary>
    public static GizmoSpace SpaceOf(GizmoTool tool, GizmoSpace space) => HasSpace(tool) ? space : GizmoSpace.Local;

    /// <summary>World for Local, and Local for World.</summary>
    public static GizmoSpace Other(GizmoSpace space) =>
        space == GizmoSpace.World ? GizmoSpace.Local : GizmoSpace.World;
}
