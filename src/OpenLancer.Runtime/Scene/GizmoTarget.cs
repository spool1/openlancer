using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Runtime.Scene;

/// <summary>A tool of the gizmo. Each tool changes one key of the section.</summary>
public enum GizmoTool
{
    /// <summary>Changes <c>pos</c>.</summary>
    Move,

    /// <summary>Changes <c>rotate</c>.</summary>
    Rotate,

    /// <summary>Changes <c>size</c> of a zone.</summary>
    Scale,
}

/// <summary>
/// An item that the gizmo can change: the tools that it takes, the matrix that a drag starts from, and the drag. The
/// Scene has <see cref="GizmoTarget"/>, and the Cutscene view has the target of a cutscene element.
/// </summary>
public interface IGizmoTarget
{
    /// <summary>The item. A drag ends when the selection is not this item any more.</summary>
    object Key { get; }

    /// <summary>Each tool, and why the item does not take it. A tool with null takes the item.</summary>
    IReadOnlyDictionary<GizmoTool, string?> Tools { get; }

    /// <summary>The scale handles of a zone, or null.</summary>
    ZoneSizeAxes? Axes { get; }

    /// <summary>The matrix that the gizmo starts from with a tool.</summary>
    /// <exception cref="InvalidOperationException">The item does not take the tool.</exception>
    Matrix4x4 Matrix(GizmoTool tool);

    /// <summary>The label of the history entry of a drag of the item with a tool, for example <c>Move Li01_14</c>. The
    /// editor opens the gesture of the drag with it.</summary>
    string DragLabel(GizmoTool tool);

    /// <summary>
    /// Starts a drag of the item in a gesture of the command layer: one entry of the edit history. The drag owns the
    /// gesture: its end commits the gesture, and its cancel cancels it. When the drag cannot start, the gesture stays
    /// open, and the caller cancels it.
    /// </summary>
    /// <param name="gesture">The open gesture of the drag, with the label of <see cref="DragLabel"/>.</param>
    /// <param name="noEditReason">Why the editor takes no edits now, or null.</param>
    /// <exception cref="InvalidOperationException">The drag cannot start. Nothing changes.</exception>
    IGizmoDrag Begin(CommandTransaction gesture, GizmoTool tool, GizmoSpace space, string? noEditReason);
}

/// <summary>One drag of the gizmo as one entry of the edit history.</summary>
public interface IGizmoDrag : IDisposable
{
    /// <summary>Takes the raw matrix of the gizmo for this frame.</summary>
    /// <param name="snap">The snap of this frame.</param>
    /// <exception cref="ArgumentException">The frame gives no valid value, for example a snap step of 0.</exception>
    /// <exception cref="InvalidOperationException">The drag has ended, or the edit of the frame does not apply.</exception>
    void Update(Matrix4x4 raw, GizmoSnap snap);

    /// <summary>Ends the drag with the values that the edit view holds: one entry, or none when nothing
    /// changed.</summary>
    void End();

    /// <summary>Ends the drag and puts back the edit view as it was before the drag.</summary>
    void Cancel();
}

/// <summary>
/// A section of a system file in W that the gizmo can change: an object, a zone or a light. It has the tools that the
/// section takes, and the values that a drag starts from. The values come from the last line of each key in W, which
/// is the line that the engine reads (see <see cref="SceneBuild"/>).
/// </summary>
/// <remarks>
/// <para>An object takes the move and rotate tools, a zone takes all three, and a light takes only the move tool,
/// because the engine reads no rotation and no size for a light (GameItemDb.cs:1466-1516).</para>
/// <para>A target holds the values of W at the time of <see cref="Of"/>, and each call makes a new target. To find the
/// same section again, compare <see cref="Section"/>.</para>
/// </remarks>
public sealed class GizmoTarget : IGizmoTarget
{
    private readonly IReadOnlyList<ValueBase>? pos;
    private readonly IReadOnlyList<ValueBase>? rotate;
    private readonly IReadOnlyList<ValueBase>? size;
    private readonly Matrix4x4 rotation;

    private GizmoTarget(SectionRef section, SceneSection kind, SectionView view)
    {
        Section = section;
        Kind = kind;
        pos = SceneLines.Last(view, "pos");
        Position = SceneBuild.Position(pos);
        if (kind == SceneSection.LightSource)
        {
            // The engine reads no rotation for a light, so its local axes are the world axes.
            Rotation = Quaternion.Identity;
            rotation = Matrix4x4.Identity;
        }
        else
        {
            rotate = SceneLines.Last(view, "rotate");
            Degrees = SceneBuild.Vector(rotate);
            if (kind == SceneSection.Object)
            {
                // A world object holds a quaternion (GameItemDb.cs:2633-2636).
                Rotation = SceneBuild.ObjectRotation(rotate);
                rotation = Matrix4x4.CreateFromQuaternion(Rotation);
            }
            else
            {
                // A zone holds a matrix (GameItemDb.cs:1647-1661).
                rotation = SceneBuild.ZoneRotation(rotate, out _);
                Rotation = Quaternion.CreateFromRotationMatrix(rotation);
            }
        }
        if (kind == SceneSection.Zone)
        {
            size = SceneLines.Last(view, "size");
            Shape = SceneBuild.ZoneShape(SceneLines.Last(view, "shape"));
            Axes = ZoneSizeAxes.Of(Shape.Value, size);
        }
        Tools = new Dictionary<GizmoTool, string?>
        {
            [GizmoTool.Move] = null,
            [GizmoTool.Rotate] = kind == SceneSection.LightSource ? "The engine reads no rotation for a light." : null,
            [GizmoTool.Scale] = kind switch
            {
                SceneSection.Zone => Axes!.Disabled,
                SceneSection.Object => "The engine reads no size for an object.",
                _ => "The engine reads no size for a light.",
            },
        };
    }

    public SectionRef Section { get; }

    object IGizmoTarget.Key => Section;

    /// <inheritdoc/>
    public string DragLabel(GizmoTool tool) => TransformPlans.Label(tool, Section);

    IGizmoDrag IGizmoTarget.Begin(CommandTransaction gesture, GizmoTool tool, GizmoSpace space, string? noEditReason) =>
        GizmoDrag.Begin(gesture, this, tool, space, noEditReason);

    /// <summary><see cref="SceneSection.Object"/>, <see cref="SceneSection.Zone"/> or
    /// <see cref="SceneSection.LightSource"/>.</summary>
    public SceneSection Kind { get; }

    /// <summary>The shape of a zone, or null for an object or a light.</summary>
    public ShapeKind? Shape { get; }

    /// <summary>Each tool, and why the section does not take it. A tool with null takes the section.</summary>
    public IReadOnlyDictionary<GizmoTool, string?> Tools { get; }

    /// <summary>The position that the engine reads from <c>pos</c>.</summary>
    internal Vector3 Position { get; }

    /// <summary>The rotation that the engine reads from <c>rotate</c>: the local axes of the section.</summary>
    internal Quaternion Rotation { get; }

    /// <summary>The angles in degrees that the engine reads from <c>rotate</c>.</summary>
    internal Vector3 Degrees { get; }

    /// <summary>The scale handles of a zone, or null for an object or a light.</summary>
    public ZoneSizeAxes? Axes { get; }

    /// <summary>
    /// The section as a target of the gizmo, or null when the gizmo cannot change it. The gizmo cannot change a section
    /// when the editor takes no edits, when the section is read-only, when it is not an object, a zone or a light, or
    /// when the edit checks give an error for a line that the target reads. Such a line has no valid start value, and
    /// the scene keeps the last valid state for it (see <see cref="SceneBuild"/>). An error on another key does not
    /// stop the gizmo.
    /// </summary>
    /// <param name="noEditReason">Why the editor takes no edits now, or null. The editor can refuse edits that the
    /// session takes.</param>
    /// <param name="reason">Why the result is null, or null.</param>
    public static GizmoTarget? Of(EditSession session, SectionRef section, string? noEditReason, out string? reason)
    {
        var view = session.Get(section);
        var kind = SceneFields.SectionOf(section.Id.Type, universe: false);
        reason = noEditReason ?? Refusal(session, section, view, kind);
        return reason == null ? new GizmoTarget(section, kind!.Value, view!) : null;
    }

    /// <summary>
    /// The matrix that the gizmo starts from with a tool: the rotation and the position of the section. For the scale
    /// tool of a zone, the scale of the zone comes first (see <see cref="ZoneSizeAxes.GizmoScale"/>), as LancerEdit
    /// builds it (SystemEditorTab.cs:1829-1830).
    /// </summary>
    /// <exception cref="InvalidOperationException">The section does not take the tool (see <see cref="Tools"/>). For
    /// example, the scale of a zone that the scale tool cannot change can be 0 on an axis, and ImGuizmo cannot invert
    /// such a matrix.</exception>
    public Matrix4x4 Matrix(GizmoTool tool)
    {
        if (Tools[tool] is { } disabled)
            throw new InvalidOperationException(disabled);
        return (tool == GizmoTool.Scale ? Matrix4x4.CreateScale(Axes!.GizmoScale) * rotation : rotation) *
               Matrix4x4.CreateTranslation(Position);
    }

    /// <summary>The key that a tool changes.</summary>
    internal static string KeyOf(GizmoTool tool) => tool switch
    {
        GizmoTool.Move => "pos",
        GizmoTool.Rotate => "rotate",
        _ => "size",
    };

    /// <summary>The values of the line that a tool starts from, or null when the section has no line of the
    /// key.</summary>
    internal IReadOnlyList<ValueBase>? StartLine(GizmoTool tool) => tool switch
    {
        GizmoTool.Move => pos,
        GizmoTool.Rotate => rotate,
        _ => size,
    };

    /// <summary>Why the gizmo cannot change a section of the edit view, or null.</summary>
    /// <param name="view">The section, or null when the edit view has no such section.</param>
    /// <param name="kind">The scene section of the type of the section, or null.</param>
    private static string? Refusal(EditSession session, SectionRef section, SectionView? view, SceneSection? kind)
    {
        if (view == null)
            return $"The edit view has no section {section.Id}.";
        if (kind is not (SceneSection.Object or SceneSection.Zone or SceneSection.LightSource))
            return "The gizmo changes only an object, a zone or a light.";
        if (view.ReadOnlyReason != null)
            return view.ReadOnlyReason;
        return SceneBuild.TransformError(session.Check(section), kind.Value) is { } error
            ? $"{error.Message} Correct it, then use the gizmo."
            : null;
    }
}
