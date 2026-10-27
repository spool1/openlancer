using System;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer.Thn;
using LibreLancer.Thn.Events;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The gizmo of the Cutscene view (section 7.6 of the M8 design): a drag edits the start pose from W in one entry, a
/// spatial animation with one target gives its target pose, a spatial animation with a second target gives the gizmo
/// to that target, and a key of a motion path moves in the frame of the path. Set camera from view writes the pose in
/// one entry.
/// </summary>
public sealed class ThnGizmoDragTests
{
    private const string Script = """
        duration = 10
        entities = {
            { entity_name = "Cam_A", type = CAMERA, cameraprops = { fovh = 10 }, spatialprops = { pos = { 0, 0, 0 }, orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } } },
            { entity_name = "Mk_B", type = MARKER, spatialprops = { pos = { 5, 0, 0 }, orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } } },
            { entity_name = "Path_A", type = MOTION_PATH, spatialprops = { pos = { 10, 0, 0 }, orient = { { 0, 0, -1 }, { 0, 1, 0 }, { 1, 0, 0 } } }, pathprops = { path_type = "CV_CROrientationSplinePath", path_data = "OPEN, {0,0,0}, {1,0,0,0}, {1,0,0}, {1,0,0,0}, " } },
            { entity_name = "Monitor", type = MONITOR }
        }
        events = {
            { 0, SET_CAMERA, { "Monitor", "Cam_A" } },
            { 1, START_SPATIAL_PROP_ANIM, { "Cam_A" }, { duration = 1, spatialprops = { pos = { 1, 2, 3 }, q_orient = { 1, 0, 0, 0 } } } },
            { 2, START_SPATIAL_PROP_ANIM, { "Cam_A", "mk_b" }, { duration = 1, spatialprops = { pos = { 0, 0, 0 } } } }
        }
        """;

    private static readonly GizmoSnap Off = new(false, 1, 15, 1);

    private static ThnGizmoTarget Target(EditSession session, ThnRef element, int? key = null) =>
        ThnGizmoTarget.Of(session, new ThnGizmoKey(element, key), null, out var reason) ??
        throw new InvalidOperationException(reason);

    private static ThnScript Engine(EditSession session) =>
        new(Encoding.ASCII.GetBytes(ThnWriter.Write(W(session).ToGlobals())), _ => null!, "gizmo.thn");

    /// <summary>True when two quaternions give the same rotation, within float noise.</summary>
    private static bool SameRotation(Quaternion a, Quaternion b) =>
        MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))) > 0.99999f;

    /// <summary>
    /// A move of the start pose of an entity: 100 frames are one entry, and the move writes only the position. The
    /// rotation stays the same object, so the patch gets no value for it.
    /// </summary>
    [Fact]
    public void AMoveOfTheStartPoseIsOneEntryThatWritesOnlyThePosition()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        var orient = Value(session, cam, "spatialprops.orient");
        var target = Target(session, cam);
        Assert.Equal((ThnGizmoPart.StartPose, Vector3.Zero), (target.Part, target.Pose.Position));

        using (var drag = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null))
        {
            for (var i = 1; i < 100; i++)
                drag.Update(target.Matrix(GizmoTool.Move) * Matrix4x4.CreateTranslation(i * 0.01f, 0, i * -0.02f), Off);
            drag.Update(target.Matrix(GizmoTool.Move) * Matrix4x4.CreateTranslation(1, 0, -2), Off);
            drag.End();
        }

        Assert.Equal(ThnPose.Vector(new Vector3(1, 0, -2)), Value(session, cam, "spatialprops.pos"));
        Assert.Same(orient, Value(session, cam, "spatialprops.orient"));
        Assert.Equal("Move Cam_A", session.UndoLabel);
        session.Undo();
        Assert.Equal(ThnPose.Vector(Vector3.Zero), Value(session, cam, "spatialprops.pos"));
        Assert.False(session.CanUndo);
    }

    /// <summary>A rotate writes the rotation matrix of three rows, and the engine reads the rotation of the drag from it.
    /// The save check passes, also for the -0 values of the matrix.</summary>
    [Fact]
    public void ARotateWritesTheOrientMatrixThatTheEngineReads()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        var pos = Value(session, cam, "spatialprops.pos");
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

        using (var drag = TestGestures.Drag(session, Target(session, cam), GizmoTool.Rotate, GizmoSpace.Local, null))
        {
            drag.Update(Matrix4x4.CreateFromQuaternion(turn), Off);
            drag.End();
        }

        Assert.Same(pos, Value(session, cam, "spatialprops.pos"));
        Assert.Equal("Rotate Cam_A", session.UndoLabel);
        var rotation = Engine(session).Entities["Cam_A"].Rotation;
        Assert.True(MathF.Abs(Quaternion.Dot(turn, rotation)) > 0.99999f, rotation.ToString());
        AssertSaved(session.Save());
    }

    /// <summary>A drag that ends at the start pose adds no entry, also when the rotation of the gizmo has float
    /// noise.</summary>
    [Fact]
    public void ADragThatEndsAtTheStartPoseAddsNoEntry()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var target = Target(session, Entity(session, "Mk_B"));

        var move = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null);
        move.Update(target.Matrix(GizmoTool.Move) * Matrix4x4.CreateTranslation(3, 0, 0), Off);
        move.Update(target.Matrix(GizmoTool.Move), Off);
        move.End();
        var rotate = TestGestures.Drag(session, target, GizmoTool.Rotate, GizmoSpace.World, null);
        var noise = Quaternion.Normalize(new Quaternion(1e-7f, -1e-7f, 1e-7f, 1));
        rotate.Update(Matrix4x4.CreateFromQuaternion(noise) * Matrix4x4.CreateTranslation(5, 0, 0), Off);
        rotate.End();

        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A spatial animation with one target: the gizmo edits its target pose, the position and <c>q_orient</c> as
    /// <c>{ w, x, y, z }</c>, which the engine reads as the rotation of the drag.
    /// </summary>
    [Fact]
    public void ASpatialAnimationWithOneTargetEditsItsTargetPose()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var anim = Event(session, 1);
        Assert.Equal(anim, ThnGizmoTarget.Subject(W(session), anim, Evaluator.Symbols, out _));
        var target = Target(session, anim);
        Assert.Equal((ThnGizmoPart.EventPose, new Vector3(1, 2, 3)), (target.Part, target.Pose.Position));
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);

        var move = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null);
        move.Update(target.Matrix(GizmoTool.Move) * Matrix4x4.CreateTranslation(0, 0, 1), Off);
        move.End();
        var rotate = TestGestures.Drag(session, Target(session, anim), GizmoTool.Rotate, GizmoSpace.World, null);
        rotate.Update(Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(1, 2, 4), Off);
        var applied = rotate.Applied.Rotation;
        rotate.End();

        Assert.Equal(ThnPose.Vector(new Vector3(1, 2, 4)), Value(session, anim, "props.spatialprops.pos"));
        Assert.True(MathF.Abs(Quaternion.Dot(turn, applied)) > 0.99999f, applied.ToString());
        Assert.Equal(ThnPose.QOrient(applied), Value(session, anim, "props.spatialprops.q_orient"));
        var engine = Assert.IsType<StartSpatialPropAnimEvent>(Engine(session).Events.Single(e => e.Time == 1));
        Assert.Equal(applied, engine.Q_Orient);
        Assert.Equal(new Vector3(1, 2, 4), engine.Pos);
    }

    /// <summary>
    /// A spatial animation with a second target takes its end pose from that target in each frame. So the gizmo goes to
    /// the entity of the second target, which the engine finds without letter case. Another event gets no gizmo.
    /// </summary>
    [Fact]
    public void ASpatialAnimationWithASecondTargetGivesTheGizmoToThatTarget()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");

        Assert.Equal(Entity(session, "Mk_B"), ThnGizmoTarget.Subject(W(session), Event(session, 2), Evaluator.Symbols, out _));
        Assert.Null(ThnGizmoTarget.Subject(W(session), Event(session, 0), Evaluator.Symbols, out var reason));
        Assert.Equal("The gizmo moves an entity, the target pose of a START_SPATIAL_PROP_ANIM, or a key of a motion path.",
            reason);
        Assert.Null(ThnGizmoTarget.Of(session, new ThnGizmoKey(Event(session, 2)), null, out reason));
        Assert.Equal("The gizmo moves only the target pose of a START_SPATIAL_PROP_ANIM with one target.", reason);
        Assert.Null(ThnGizmoTarget.Of(session, new ThnGizmoKey(Entity(session, "Monitor")), null, out reason));
        Assert.Equal("A MONITOR has no place in the scene.", reason);
        Assert.Equal("A cutscene entity has no size.", Target(session, Entity(session, "Cam_A")).Tools[GizmoTool.Scale]);
    }

    /// <summary>
    /// A key of a motion path moves in the frame of the path entity: the gizmo shows the key in the scene, and a move
    /// writes the point in the frame of the path. The other key keeps its values, and the engine reads the new point.
    /// A path without orientations takes no rotate.
    /// </summary>
    [Fact]
    public void AKeyOfAMotionPathMovesInTheFrameOfThePath()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var path = Entity(session, "Path_A");
        var target = Target(session, path, 1);
        Assert.Equal(ThnGizmoPart.PathKey, target.Part);
        Assert.True(Vector3.Distance(new Vector3(10, 0, -1), target.Pose.Position) < 1e-6f, target.Pose.Position.ToString());

        var drag = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null);
        drag.Update(Matrix4x4.CreateTranslation(10, 1, -1), Off);
        drag.End();

        Assert.Equal("Move key 2 of Path_A", session.UndoLabel);
        var keys = ThnMotionPath.Parse(((ThnText)Value(session, path, "pathprops.path_data")!).Value, Evaluator);
        Assert.Equal(new ThnPathKey(Vector3.Zero, Quaternion.Identity), keys.Keys[0]);
        Assert.True(Vector3.Distance(new Vector3(1, 1, 0), keys.Keys[1].Point) < 1e-6f, keys.Keys[1].Point.ToString());
        var engine = Engine(session).Entities["Path_A"].Path!;
        Assert.True(Vector3.Distance(new Vector3(1, 1, 0), engine.GetPosition(1)) < 1e-5f);
        Assert.Null(ThnGizmoTarget.Of(session, new ThnGizmoKey(path, 2), null, out var reason));
        Assert.Equal("The path has no key 3.", reason);

        // The engine needs orientations (MotionPath.cs:66-103), but a path text can lack them. Then a key takes no
        // rotate.
        using var plain = Workspace(script: "duration = 1\nentities = { { entity_name = \"P\", type = MOTION_PATH, " +
                                            "pathprops = { path_data = \"OPEN, {0,0,0}, {0,0,4}, \" } } }\nevents = {}\n");
        var plainSession = plain.Session("act");
        Assert.Equal("The keys of this path have no orientations.",
            Target(plainSession, Entity(plainSession, "P"), 0).Tools[GizmoTool.Rotate]);
    }

    /// <summary>
    /// A rotate of a key of a motion path writes the orientation of the key in the frame of the path. The start pose of
    /// Path_A turns 90 degrees about Y, so the gizmo starts at that turn, and the written key is the inverse of that
    /// turn times the turn of the drag. The engine then turns an object on the path by the turn of the drag
    /// (ThnSceneObject.cs:230).
    /// </summary>
    [Fact]
    public void ARotateOfAKeyOfAMotionPathWritesTheOrientationInTheFrameOfThePath()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var path = Entity(session, "Path_A");
        var frame = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var target = Target(session, path, 1);
        Assert.True(SameRotation(frame, target.Pose.Rotation), target.Pose.Rotation.ToString());
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);

        using (var drag = TestGestures.Drag(session, target, GizmoTool.Rotate, GizmoSpace.World, null))
        {
            drag.Update(Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(target.Pose.Position), Off);
            drag.End();
        }

        Assert.Equal("Rotate key 2 of Path_A", session.UndoLabel);
        var keys = ThnMotionPath.Parse(((ThnText)Value(session, path, "pathprops.path_data")!).Value, Evaluator);
        Assert.Equal(new ThnPathKey(Vector3.Zero, Quaternion.Identity), keys.Keys[0]);
        Assert.Equal(new Vector3(1, 0, 0), keys.Keys[1].Point);
        var written = keys.Keys[1].Orientation!.Value;
        Assert.True(SameRotation(Quaternion.Inverse(frame) * turn, written), written.ToString());
        var engine = Engine(session).Entities["Path_A"];
        var onPath = engine.Rotation * engine.Path!.GetOrientation(1);
        Assert.True(SameRotation(turn, onPath), onPath.ToString());
    }

    /// <summary>
    /// The engine does not make an entity with the REFERENCE flag, as a name or in a number. It uses the object with the
    /// same name that another script makes (ThnScriptInstance.cs:70-71). So the gizmo, its ghost and Set camera from view
    /// do not take such an entity, also as the second target of a spatial animation, and the reason says why.
    /// </summary>
    [Fact]
    public void TheGizmoDoesNotTakeAReferenceEntity()
    {
        const string script = """
            duration = 1
            entities = {
                { entity_name = "Camera_0", type = CAMERA, flags = REFERENCE, spatialprops = { pos = { 1, 2, 3 } } },
                { entity_name = "Mk_A", type = MARKER, flags = 17 },
                { entity_name = "Mk_B", type = MARKER, flags = HIDDEN }
            }
            events = { { 0, START_SPATIAL_PROP_ANIM, { "Mk_B", "camera_0" }, { duration = 1 } } }
            """;
        using var ws = Workspace(script: script);
        var session = ws.Session("act");
        var engine = new ThnScript(Encoding.ASCII.GetBytes(script), _ => null!, "reference.thn");

        foreach (var name in new[] { "Camera_0", "Mk_A" })
        {
            Assert.True(engine.Entities[name].ObjectFlags.HasFlag(ThnObjectFlags.Reference));
            Assert.Null(ThnGizmoTarget.Of(session, new ThnGizmoKey(Entity(session, name)), null, out var reason));
            Assert.Equal(ThnPose.ReferencePoseNote, reason);
        }

        var second = ThnGizmoTarget.Subject(W(session), Event(session, 0), Evaluator.Symbols, out _)!;
        Assert.Equal(Entity(session, "Camera_0"), second);
        Assert.Null(ThnGizmoTarget.Of(session, new ThnGizmoKey(second), null, out var secondReason));
        Assert.Equal(ThnPose.ReferencePoseNote, secondReason);
        Assert.Equal(ThnGizmoPart.StartPose, Target(session, Entity(session, "Mk_B")).Part);
    }

    /// <summary>
    /// The engine cannot load the <c>orient</c> key of a spatial animation (see <see cref="ThnSchemaTests"/>). So the
    /// gizmo starts from <c>q_orient</c>, and a rotate writes <c>q_orient</c>, also when the event has <c>orient</c>.
    /// The <c>orient</c> value stays as it is.
    /// </summary>
    [Fact]
    public void ARotateOfATargetPoseWritesQOrientAlsoWhenTheEventHasOrient()
    {
        const string script = """
            duration = 1
            entities = { { entity_name = "Mk_A", type = MARKER } }
            events = { { 0, START_SPATIAL_PROP_ANIM, { "Mk_A" }, { duration = 1, spatialprops = { orient = { { 0, 0, -1 }, { 0, 1, 0 }, { 1, 0, 0 } } } } } }
            """;
        using var ws = Workspace(script: script);
        var session = ws.Session("act");
        var anim = Event(session, 0);
        var orient = Value(session, anim, "props.spatialprops.orient");
        var target = Target(session, anim);
        Assert.Equal(Quaternion.Identity, target.Pose.Rotation);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f);

        using (var drag = TestGestures.Drag(session, target, GizmoTool.Rotate, GizmoSpace.World, null))
        {
            drag.Update(Matrix4x4.CreateFromQuaternion(turn), Off);
            drag.End();
        }

        Assert.Same(orient, Value(session, anim, "props.spatialprops.orient"));
        var written = ThnPose.QOrient(Value(session, anim, "props.spatialprops.q_orient"));
        Assert.NotNull(written);
        Assert.True(SameRotation(turn, written.Value), written.ToString());
    }

    /// <summary>The ghost of the start pose reads W through the target, so it follows each frame of a drag.</summary>
    [Fact]
    public void TheTargetOfTheGhostFollowsAnOpenDrag()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var key = new ThnGizmoKey(Entity(session, "Mk_B"));
        var target = Target(session, key.Element);

        using var drag = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null);
        drag.Update(target.Matrix(GizmoTool.Move) * Matrix4x4.CreateTranslation(0, 2, 0), Off);

        Assert.Equal(new Vector3(5, 2, 0), ThnGizmoTarget.Of(session, key, null, out _)!.Pose.Position);
        drag.Cancel();
        Assert.Equal(new Vector3(5, 0, 0), ThnGizmoTarget.Of(session, key, null, out _)!.Pose.Position);
    }

    /// <summary>Set camera from view writes the position and the rotation of the free camera as the start pose of a
    /// camera, in one entry. The save check passes for the -0 values of a turn of 90 degrees.</summary>
    [Fact]
    public void SetCameraFromViewWritesThePoseInOneEntry()
    {
        using var ws = Workspace(script: Script);
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        var view = new ThnPoseValue(new Vector3(1.5f, 2, -3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2));

        session.Execute(ThnGizmoTarget.SetStartPose(cam, view, "Set camera from view"));

        Assert.Equal("Set camera from view", session.UndoLabel);
        Assert.Equal(ThnPose.Vector(view.Position), Value(session, cam, "spatialprops.pos"));
        Assert.Equal(ThnPose.Orient(view.Rotation), Value(session, cam, "spatialprops.orient"));
        var engine = Engine(session).Entities["Cam_A"];
        Assert.True(MathF.Abs(Quaternion.Dot(view.Rotation, engine.Rotation)) > 0.99999f);
        AssertSaved(session.Save());
        session.Undo();
        Assert.Equal(ThnPose.Vector(Vector3.Zero), Value(session, cam, "spatialprops.pos"));
        Assert.False(session.CanUndo);
    }
}
