using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Runtime.Thn;

/// <summary>What the gizmo of the Cutscene view changes (section 7.6 of the M8 design).</summary>
public enum ThnGizmoPart
{
    /// <summary>The start pose of an entity: <c>spatialprops.pos</c> and <c>spatialprops.orient</c>.</summary>
    StartPose,

    /// <summary>The target pose of a spatial animation with one target: <c>spatialprops.pos</c> and
    /// <c>q_orient</c> of its props.</summary>
    EventPose,

    /// <summary>A key of a motion path: a point and its orientation in <c>path_data</c>.</summary>
    PathKey,
}

/// <summary>The item of the gizmo of the Cutscene view: an element, and a key of a motion path.</summary>
/// <param name="PathKey">The index of the key of a MOTION_PATH entity, or null for its start pose.</param>
public readonly record struct ThnGizmoKey(ThnRef Element, int? PathKey = null);

/// <summary>
/// An element of a cutscene that the gizmo of the Cutscene view can change, with the pose that a drag starts from. The
/// pose comes from W, not from the preview: events can move an entity later, so the gizmo edits the start pose, and
/// the view draws it on a ghost of the entity while the playhead is not at 0.
/// </summary>
/// <remarks>
/// <para>A START_SPATIAL_PROP_ANIM with a second target takes the end pose from that target in each frame
/// (StartSpatialPropAnimEvent.cs:127-139, :178). So its own pose values only choose which parts move, and the gizmo
/// changes the start pose of the second target instead (see <see cref="Subject"/>).</para>
/// <para>The engine does not make an entity with the REFERENCE flag (ThnScriptInstance.cs:70-71), so the gizmo does
/// not change its pose (see <see cref="ThnPose.ReferencePoseNote"/>).</para>
/// </remarks>
public sealed class ThnGizmoTarget : IGizmoTarget
{
    private const string SpatialAnim = "START_SPATIAL_PROP_ANIM";
    private const string NoScale = "A cutscene entity has no size.";

    private readonly ThnPathKeys? path;
    private readonly ThnPoseValue frame;

    private ThnGizmoTarget(ThnGizmoKey key, ThnGizmoPart part, string label, ThnPoseValue pose, ThnPathKeys? path,
        ThnPoseValue frame, string? noRotation)
    {
        Key = key;
        Part = part;
        Label = label;
        Pose = pose;
        this.path = path;
        this.frame = frame;
        Tools = new Dictionary<GizmoTool, string?>
        {
            [GizmoTool.Move] = null,
            [GizmoTool.Rotate] = noRotation,
            [GizmoTool.Scale] = NoScale,
        };
    }

    public ThnGizmoKey Key { get; }

    object IGizmoTarget.Key => Key;

    public ThnGizmoPart Part { get; }

    /// <summary>The name of the item for the label of a drag, for example <c>Cam_A</c> or <c>key 2 of Path_A</c>.</summary>
    public string Label { get; }

    /// <summary>The pose of the gizmo in the scene.</summary>
    public ThnPoseValue Pose { get; }

    public IReadOnlyDictionary<GizmoTool, string?> Tools { get; }

    public ZoneSizeAxes? Axes => null;

    /// <summary>The element that the gizmo of a selection changes: an entity or a spatial animation with one target
    /// gives itself, and a spatial animation with a second target gives the entity of that target. Null when the
    /// gizmo cannot change the selection.</summary>
    /// <param name="reason">Why the result is null, or null.</param>
    public static ThnRef? Subject(ThnScriptModel working, ThnRef selection, IReadOnlyDictionary<string, ThnSymbolInfo> symbols,
        out string? reason)
    {
        reason = null;
        if (selection.Kind == ThnElementKind.Entity)
            return selection;
        var ev = working.Element(ThnElementKind.Event, selection.Id);
        if (ev == null)
        {
            reason = "The cutscene has no such event.";
            return null;
        }
        if (ThnSchema.TypeOf(ThnElementKind.Event, ev.Table, symbols) != SpatialAnim)
        {
            reason = "The gizmo moves an entity, the target pose of a START_SPATIAL_PROP_ANIM, or a key of a motion path.";
            return null;
        }
        if (Targets(ev.Table) is not [_, var second, ..])
            return selection;
        if (working.EntityNamed(second) is not { } entity)
        {
            reason = $"The second target {second} is not an entity of this script, so the gizmo cannot move it.";
            return null;
        }
        return new ThnRef(selection.Path, ThnElementKind.Entity, entity.Id);
    }

    /// <summary>The target of an item, or null with the reason when the gizmo cannot change it now. Each call reads W
    /// again.</summary>
    /// <param name="noEditReason">Why the editor takes no edits now, or null.</param>
    public static ThnGizmoTarget? Of(EditSession session, ThnGizmoKey key, string? noEditReason, out string? reason)
    {
        reason = noEditReason ?? session.CutsceneReadOnlyReason(key.Element);
        if (reason != null)
            return null;
        var element = key.Element;
        if (session.Cutscene(element.Path)?.Element(element.Kind, element.Id)?.Table is not { } table)
        {
            reason = "The cutscene has no such element. An undo can have removed it.";
            return null;
        }

        var evaluator = ThornEvaluator.Instance;
        var type = ThnSchema.TypeOf(element.Kind, table, evaluator.Symbols);
        var name = ThnScriptModel.NameOf(table) ?? "entity";
        if (element.Kind == ThnElementKind.Event)
        {
            if (type != SpatialAnim || Targets(table).Count != 1 || key.PathKey != null)
            {
                reason = "The gizmo moves only the target pose of a START_SPATIAL_PROP_ANIM with one target.";
                return null;
            }
            return Event(session, key, table);
        }
        if (type == "MONITOR")
        {
            reason = "A MONITOR has no place in the scene.";
            return null;
        }
        if (ThnFlags.IsReference(table, evaluator.Symbols))
        {
            reason = ThnPose.ReferencePoseNote;
            return null;
        }
        var start = ThnPose.StartPose(table);
        if (key.PathKey is not { } index)
            return new ThnGizmoTarget(key, ThnGizmoPart.StartPose, name, start, null, start, null);
        if (type != "MOTION_PATH" || ThnMotionPath.Path.ValueIn(ThnElementKind.Entity, table) is not ThnText text)
        {
            reason = "Only a MOTION_PATH with path_data has keys.";
            return null;
        }
        ThnPathKeys keys;
        try
        {
            keys = ThnMotionPath.Parse(text.Value, evaluator);
        }
        catch (FormatException e)
        {
            reason = e.Message;
            return null;
        }
        if (index < 0 || index >= keys.Keys.Length)
        {
            reason = $"The path has no key {index + 1}.";
            return null;
        }
        var pathKey = keys.Keys[index];
        var pose = new ThnPoseValue(start.Position + Vector3.Transform(pathKey.Point, start.Rotation),
            pathKey.Orientation is { } q ? Quaternion.Normalize(start.Rotation * q) : start.Rotation);
        return new ThnGizmoTarget(key, ThnGizmoPart.PathKey, $"key {index + 1} of {name}", pose, keys, start,
            keys.HasOrientation ? null : "The keys of this path have no orientations.");
    }

    /// <summary>The command that sets the start pose of an entity: its position and its rotation, in one entry.</summary>
    public static EditCommand SetStartPose(ThnRef entity, ThnPoseValue pose, string label) => EditCommand.Group(label,
    [
        ThnEdits.Set(entity, ThnPose.PositionPath, ThnPose.Vector(pose.Position)),
        ThnEdits.Set(entity, ThnPose.OrientPath, ThnPose.Orient(pose.Rotation)),
    ]);

    /// <summary>The matrix that the gizmo starts from: the rotation and the position of the pose.</summary>
    /// <exception cref="InvalidOperationException">The target does not take the tool.</exception>
    public Matrix4x4 Matrix(GizmoTool tool)
    {
        if (Tools[tool] is { } disabled)
            throw new InvalidOperationException(disabled);
        return Matrix4x4.CreateFromQuaternion(Pose.Rotation) * Matrix4x4.CreateTranslation(Pose.Position);
    }

    /// <inheritdoc/>
    public string DragLabel(GizmoTool tool) => $"{tool} {Label}";

    public IGizmoDrag Begin(CommandTransaction gesture, GizmoTool tool, GizmoSpace space, string? noEditReason) =>
        ThnGizmoDrag.Begin(gesture, this, tool, space, noEditReason);

    /// <summary>The command that gives the item a pose. A move writes the position, and a rotate the rotation.</summary>
    internal EditCommand Write(GizmoTool tool, ThnPoseValue pose)
    {
        var element = Key.Element;
        switch (Part)
        {
            case ThnGizmoPart.PathKey:
                var index = Key.PathKey!.Value;
                var inverse = Quaternion.Inverse(frame.Rotation);
                var old = path!.Keys[index];
                var key = tool == GizmoTool.Move
                    ? old with { Point = Vector3.Transform(pose.Position - frame.Position, inverse) }
                    : old with { Orientation = Quaternion.Normalize(inverse * pose.Rotation) };
                return ThnEdits.Set(element, ThnMotionPath.Path, new ThnText(ThnMotionPath.Write(path.With(index, key))));
            case ThnGizmoPart.EventPose:
                return tool == GizmoTool.Move
                    ? ThnEdits.Set(element, ThnPose.EventPositionPath, ThnPose.Vector(pose.Position))
                    : ThnEdits.Set(element, ThnPose.EventRotationPath, ThnPose.QOrient(pose.Rotation));
            default:
                return tool == GizmoTool.Move
                    ? ThnEdits.Set(element, ThnPose.PositionPath, ThnPose.Vector(pose.Position))
                    : ThnEdits.Set(element, ThnPose.OrientPath, ThnPose.Orient(pose.Rotation));
        }
    }

    /// <summary>
    /// The target pose of a spatial animation with one target: <c>spatialprops.pos</c> and <c>q_orient</c>. A missing
    /// value starts at the start pose of the entity of the target, and a drag adds it. A rotate always writes
    /// <c>q_orient</c>: the engine cannot load the <c>orient</c> key of the event (see its row in
    /// <see cref="ThnSchema"/>).
    /// </summary>
    private static ThnGizmoTarget Event(EditSession session, ThnGizmoKey key, ThnTable ev)
    {
        var working = session.Cutscene(key.Element.Path)!;
        var target = Targets(ev)[0];
        var start = working.EntityNamed(target) is { } entity
            ? ThnPose.StartPose(entity.Table)
            : new ThnPoseValue(Vector3.Zero, Quaternion.Identity);
        var pose = new ThnPoseValue(
            ThnPose.Vector(ThnPose.EventPositionPath.ValueIn(ThnElementKind.Event, ev)) ?? start.Position,
            ThnPose.QOrient(ThnPose.EventRotationPath.ValueIn(ThnElementKind.Event, ev)) ?? start.Rotation);
        return new ThnGizmoTarget(key, ThnGizmoPart.EventPose, $"the target pose of {target}", pose, null, pose, null);
    }

    /// <summary>The names of the targets of an event.</summary>
    private static IReadOnlyList<string> Targets(ThnTable ev) =>
        ev.List.Length > 2 && ev.List[2] is ThnTable { Record.IsEmpty: true } targets
            ? targets.List.Select(t => (t as ThnText)?.Value ?? "").ToList()
            : [];
}

/// <summary>
/// One drag of the gizmo of the Cutscene view as one entry of the edit history: a gesture of the command layer (the
/// pattern of <see cref="GizmoDrag"/>). Each step runs the cutscene edit of the frame as a <see cref="CommandPlan"/>. It
/// has no ImGui call, so a test can script it with matrices.
/// </summary>
/// <remarks>
/// The drag starts from the pose of W. Each frame gives the raw matrix of the gizmo, and the drag writes the new pose
/// when it changed. A pose that equals the start pose puts back the edit view as it was, so a drag back to the start
/// adds no entry. A move writes only the position, and a rotate only the rotation. -0 becomes 0 (see
/// <see cref="ThnNumber"/>), so the save check passes for each rotation.
/// </remarks>
public sealed class ThnGizmoDrag : IGizmoDrag
{
    private readonly ThnGizmoTarget target;
    private readonly GizmoTool tool;
    private readonly GizmoSpace space;
    private readonly CommandTransaction gesture;
    private ThnPoseValue applied;
    private bool ended;

    private ThnGizmoDrag(ThnGizmoTarget target, GizmoTool tool, GizmoSpace space, CommandTransaction gesture)
    {
        this.target = target;
        this.tool = tool;
        this.space = space;
        this.gesture = gesture;
        applied = target.Pose;
    }

    /// <summary>Reads the target from W again, and starts a drag in a gesture. The gesture gets the label of the tool and
    /// the item of W now, for example <c>Move Cam_A</c>. The drag owns the gesture after the start.</summary>
    /// <param name="gesture">The open gesture of the drag. When the drag cannot start, the gesture stays open, and the
    /// caller cancels it.</param>
    /// <exception cref="InvalidOperationException">The editor or the session takes no edits, the gizmo cannot change the
    /// item now, or the item does not take the tool. Nothing changes.</exception>
    public static ThnGizmoDrag Begin(CommandTransaction gesture, ThnGizmoTarget target, GizmoTool tool,
        GizmoSpace space, string? noEditReason)
    {
        var now = ThnGizmoTarget.Of(gesture.Session, target.Key, noEditReason, out var reason) ??
                  throw new InvalidOperationException(reason);
        if (now.Tools[tool] is { } disabled)
            throw new InvalidOperationException(disabled);
        gesture.Label = now.DragLabel(tool);
        return new ThnGizmoDrag(now, tool, space, gesture);
    }

    /// <summary>The pose that the edit view holds now.</summary>
    public ThnPoseValue Applied => applied;

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">The snap is on, and the step of the tool is 0 or less.</exception>
    public void Update(Matrix4x4 raw, GizmoSnap snap)
    {
        ThrowIfEnded();
        if (!IsFinite(raw))
            return;
        var start = target.Pose;
        ThnPoseValue pose;
        if (tool == GizmoTool.Move)
        {
            var position = snap.On
                ? TransformValues.SnapTranslation(start.Position, raw.Translation, snap.Move,
                    space == GizmoSpace.Local ? start.Rotation : null)
                : raw.Translation;
            pose = start with { Position = position };
        }
        else
        {
            if (!Matrix4x4.Decompose(raw, out _, out var rotation, out _))
                return;
            if (snap.On)
                rotation = TransformValues.SnapRotation(start.Rotation, rotation, snap.RotateDegrees);
            pose = start with { Rotation = TransformValues.SameRotation(rotation, start.Rotation) ? start.Rotation : rotation };
        }
        if (pose == applied)
            return;
        if (pose == start)
        {
            gesture.Reset();
        }
        else if (gesture.Apply(_ => new CommandPlan(target.Write(tool, pose))) is { Status: EditStatus.Refused } refused)
        {
            applied = start;
            throw new InvalidOperationException(refused.Errors[0].Message);
        }
        applied = pose;
    }

    public void End()
    {
        ThrowIfEnded();
        ended = true;
        gesture.Commit();
    }

    public void Cancel()
    {
        if (ended)
            return;
        ended = true;
        gesture.Cancel();
    }

    public void Dispose() => Cancel();

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
