using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// One drag of the gizmo as one transaction (section 10.4 of the design). The tests script each drag with the matrices
/// that ImGuizmo gives (see <see cref="ImGuizmoResults"/>), on a synthetic binary system file.
/// </summary>
public sealed class GizmoDragTests
{
    private const string DepotStart = "-38675, 0, -27705";

    /// <summary>Each tool has another step, so a test can see which step a snap uses.</summary>
    private static readonly GizmoSnap Off = new(false, 100, 15, 50);

    private static readonly GizmoSnap On = Off with { On = true };
    private static readonly SectionRef Depot = GizmoSystem.Section("Object", "Li01_depot");
    private static readonly SectionRef Gate = GizmoSystem.Section("Object", "Li01_gate");
    private static readonly SectionRef Cylinder = GizmoSystem.Section("Zone", "Zone_cylinder");
    private static readonly SectionRef Light = GizmoSystem.Section("LightSource", "Light_main");

    /// <summary>Each frame shows in the edit view at once. The end adds one entry, and one undo takes the whole drag
    /// back.</summary>
    [Fact]
    public void ADragIsOneEntryOfTheHistory()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        using var drag = Begin(session, Depot, GizmoTool.Move, GizmoSpace.World);
        Assert.Equal(Matrix4x4.CreateTranslation(-38675, 0, -27705), drag.Start);

        var frame = drag.Start;
        foreach (var (step, line) in new[] { (120f, "-38555, 0, -27705"), (250.5f, "-38304.5, 0, -27705"),
                     (400f, "-37904.5, 0, -27705") })
        {
            frame = ImGuizmoResults.Move(frame, new Vector3(step, 0, 0));
            drag.Update(frame, Off);
            Assert.Equal([line], Lines(session, Depot, "pos"));
        }
        Assert.False(session.CanUndo);
        drag.End();

        Assert.Equal("Move Li01_depot", session.UndoLabel);
        session.Undo();
        Assert.False(session.CanUndo);
        Assert.Equal([DepotStart], Lines(session, Depot, "pos"));
        session.Redo();
        Assert.Equal(["-37904.5, 0, -27705"], Lines(session, Depot, "pos"));
        // Rule 3 of section 9.3: a value that did not change keeps its value object.
        var values = Values(session, Depot, "pos");
        Assert.IsType<SingleValue>(values[0]);
        Assert.IsType<Int32Value>(values[1]);
        Assert.IsType<Int32Value>(values[2]);
    }

    /// <summary>
    /// The drag starts from the matrix that the gizmo holds at the click: the matrix of the target for the tool. A
    /// scale result of ImGuizmo is relative to that matrix (ImGuizmo.cpp:2246), so a drag with another start matrix
    /// would write another size.
    /// </summary>
    [Fact]
    public void ADragStartsFromTheMatrixOfTheTargetForItsTool()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var cylinder = GizmoSystem.Target(session, Cylinder);
        // The scale matrix of a zone has the size of the zone. The other matrices do not.
        Assert.NotEqual(cylinder.Matrix(GizmoTool.Move), cylinder.Matrix(GizmoTool.Scale));

        foreach (var (section, tools) in new (SectionRef, GizmoTool[])[]
                 {
                     (Gate, [GizmoTool.Move, GizmoTool.Rotate]),
                     (Cylinder, [GizmoTool.Move, GizmoTool.Rotate, GizmoTool.Scale]),
                     (GizmoSystem.Section("Zone", "Zone_sphere"), [GizmoTool.Scale]),
                     (Light, [GizmoTool.Move]),
                 })
        {
            var target = GizmoSystem.Target(session, section);
            foreach (var tool in tools)
            {
                using var drag = TestGestures.Drag(session, target, tool, GizmoSpace.World, null);
                Assert.Equal(target.Matrix(tool), drag.Start);
            }
        }
        Assert.False(session.CanUndo);
    }

    /// <summary>A frame changes the edit view only when its line differs from the line that the edit view holds, also
    /// with snapping.</summary>
    [Fact]
    public void ADragAppliesALineOnlyWhenItChanges()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var changes = 0;
        session.Changed += _ => changes++;
        using var drag = Begin(session, Depot, GizmoTool.Move, GizmoSpace.World);

        // Each frame, and the number of changes after it.
        var frames = new (float X, GizmoSnap Snap, int Changes)[]
        {
            (0, Off, 0), (30, On, 0), (70, On, 1), (120, On, 1), (70, On, 1), (70, Off, 2), (70, Off, 2),
        };
        foreach (var (x, snap, expected) in frames)
        {
            drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(x, 0, 0)), snap);
            Assert.Equal(expected, changes);
        }
        Assert.Equal(["-38605, 0, -27705"], Lines(session, Depot, "pos"));
        drag.End();
        Assert.Equal(3, changes);
    }

    /// <summary>A drag back to its start puts back the section of the lower layer, so the end adds no entry and the
    /// session has no unsaved edit (rules 6 and 7 of section 9.3).</summary>
    [Fact]
    public void ADragThatEndsAtItsStartAddsNoEntry()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        using var drag = Begin(session, Depot, GizmoTool.Move, GizmoSpace.World);

        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(500, 0, 0)), Off);
        Assert.True(session.Get(Depot)!.IsChanged);
        // Less than half a step from the start snaps to the start.
        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(30, 0, 0)), On);
        Assert.False(session.Get(Depot)!.IsChanged);
        drag.End();

        Assert.Equal([DepotStart], Lines(session, Depot, "pos"));
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    /// <summary>
    /// When the origin goes behind the camera, ImGuizmo gives back the matrix that it got, frame after frame
    /// (ImGuizmo.cpp:2521-2527). The end then commits the last applied line.
    /// </summary>
    [Fact]
    public void WhenTheMatricesStopChangingTheEndCommitsTheLastAppliedLine()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var changes = 0;
        session.Changed += _ => changes++;
        using var drag = Begin(session, Depot, GizmoTool.Move, GizmoSpace.World);

        var frame = ImGuizmoResults.Move(drag.Start, new Vector3(250, 0, -40));
        for (var i = 0; i < 6; i++)
            drag.Update(frame, Off);
        drag.End();

        Assert.Equal(2, changes);
        Assert.Equal(["-38425, 0, -27745"], Lines(session, Depot, "pos"));
        session.Undo();
        Assert.Equal([DepotStart], Lines(session, Depot, "pos"));
    }

    /// <summary>A cancel puts back the edit view and adds no entry. A drag that is disposed without an end
    /// cancels.</summary>
    [Fact]
    public void CancelPutsBackTheEditView()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var target = GizmoSystem.Target(session, Depot);

        var drag = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null);
        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(250, 0, 0)), Off);
        drag.Cancel();

        Assert.Equal([DepotStart], Lines(session, Depot, "pos"));
        Assert.False(session.Get(Depot)!.IsChanged);
        Assert.False(session.CanUndo);
        Assert.Throws<InvalidOperationException>(() => drag.Update(drag.Start, Off));
        Assert.Throws<InvalidOperationException>(() => drag.End());
        drag.Dispose();

        using (var disposed = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null))
            disposed.Update(ImGuizmoResults.Move(disposed.Start, new Vector3(0, 0, 900)), Off);
        Assert.Equal([DepotStart], Lines(session, Depot, "pos"));
        Assert.False(session.CanUndo);
        // No transaction stays open.
        session.Execute(EditCommand.SetLines(Depot, "pos", [[new Int32Value(1), new Int32Value(2), new Int32Value(3)]]));
    }

    /// <summary>
    /// Begin reads W again. A target that the gizmo cannot change now, a tool that the section does not take, or an
    /// editor that takes no edits makes Begin throw, and the session stays as it was.
    /// </summary>
    [Fact]
    public void BeginThrowsForASectionThatTheGizmoCannotChangeNowAndChangesNothing()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var depot = GizmoSystem.Target(session, Depot);
        var light = GizmoSystem.Target(session, Light);
        var flatBox = GizmoSystem.Target(session, GizmoSystem.Section("Zone", "Zone_flat_box"));

        AssertBeginThrows(session, depot, GizmoTool.Move, "The editor waits for a confirmation.",
            "The editor waits for a confirmation.");
        AssertBeginThrows(session, light, GizmoTool.Rotate, null, "The engine reads no rotation for a light.");
        AssertBeginThrows(session, flatBox, GizmoTool.Scale, null,
            "The size has two values, so the engine gives Z the size 0. Give it three values first.");
        session.Execute(EditCommand.SetLinesFromText(Depot, "pos", ["abc, 0, 0"]));
        AssertBeginThrows(session, depot, GizmoTool.Move, null,
            "[Object] nickname = Li01_depot: 'abc' in a 'pos' line is not a number. The game reads 0 in its place. Correct it, " +
            "then use the gizmo.");
        Assert.Equal(["abc, 0, 0"], Lines(session, Depot, "pos"));
        Assert.Equal("Set pos", session.UndoLabel);
        session.Dispose();
        AssertBeginThrows(session, light, GizmoTool.Move, null, "The edit session is closed. Open the profile again.");
    }

    /// <summary>A target holds the lines of W at the time when it was made. Begin starts from the lines that W holds
    /// at the click, also when they changed after the target.</summary>
    [Fact]
    public void BeginStartsFromTheLinesThatWHoldsNow()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var old = GizmoSystem.Target(session, Depot);
        session.Execute(EditCommand.SetLines(Depot, "pos",
            [[new Int32Value(-38000), new Int32Value(0), new Int32Value(-27705)]]));
        var now = Values(session, Depot, "pos");

        using var drag = TestGestures.Drag(session, old, GizmoTool.Move, GizmoSpace.World, null);
        Assert.Equal(Matrix4x4.CreateTranslation(-38000, 0, -27705), drag.Start);
        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(10, 0, 0)), Off);
        drag.End();

        Assert.Equal(["-37990, 0, -27705"], Lines(session, Depot, "pos"));
        Assert.Same(now[1], Values(session, Depot, "pos")[1]);
        Assert.Same(now[2], Values(session, Depot, "pos")[2]);
    }

    /// <summary>A drag starts from the last line of its key, which is the line that the engine reads, and writes one
    /// line in place of all lines of the key.</summary>
    [Fact]
    public void ADragStartsFromTheLastLineOfItsKey()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var moved = GizmoSystem.Section("Object", "Li01_moved");
        var last = session.Get(moved)!.Lines("pos")[^1].Values;

        using var drag = Begin(session, moved, GizmoTool.Move, GizmoSpace.World);
        Assert.Equal(Matrix4x4.CreateTranslation(100, 0, 200), drag.Start);
        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(10, 0, 0)), Off);
        drag.End();

        Assert.Equal(["110, 0, 200"], Lines(session, moved, "pos"));
        Assert.Same(last[1], Values(session, moved, "pos")[1]);
        Assert.Same(last[2], Values(session, moved, "pos")[2]);
    }

    [Fact]
    public void BeginWithAnOpenTransactionThrowsAndChangesNothing()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var target = GizmoSystem.Target(session, Depot);

        using (session.Begin("Set pos"))
        {
            Assert.Throws<InvalidOperationException>(() =>
                TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null));
        }

        Assert.False(session.CanUndo);
        using var drag = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World, null);
        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(10, 0, 0)), Off);
        drag.End();
        Assert.Equal(["-38665, 0, -27705"], Lines(session, Depot, "pos"));
    }

    /// <summary>
    /// A forced end (a change of selection or of the system, a profile close or the window close) commits the line of
    /// the last frame while ImGuizmo still drags. Frames after the end cannot apply.
    /// </summary>
    [Fact]
    public void AForcedEndCommitsAndLaterFramesCannotApply()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        using var drag = Begin(session, Depot, GizmoTool.Move, GizmoSpace.World);
        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(0, 75, 0)), Off);

        drag.End();

        Assert.Throws<InvalidOperationException>(() =>
            drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(0, 300, 0)), Off));
        drag.Dispose();
        Assert.Equal(["-38675, 75, -27705"], Lines(session, Depot, "pos"));
        Assert.Equal("Move Li01_depot", session.UndoLabel);
    }

    /// <summary>
    /// A rotate writes the angles nearest to the start angles. A world turn of 17 degrees snaps to 15 and gives
    /// (0, 120, 0) as integers. A local turn snaps about the local axis of the item.
    /// </summary>
    [Fact]
    public void ARotateSnapsAboutTheAxisOfItsSpace()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var target = GizmoSystem.Target(session, Gate);
        var start = Values(session, Gate, "rotate");

        using (var world = TestGestures.Drag(session, target, GizmoTool.Rotate, GizmoSpace.World, null))
        {
            world.Update(ImGuizmoResults.WorldTurn(target.Matrix(GizmoTool.Rotate), Vector3.UnitY, 17), On);
            world.End();
        }
        Assert.Equal(["0, 120, 0"], Lines(session, Gate, "rotate"));
        var turned = Values(session, Gate, "rotate");
        Assert.Same(start[0], turned[0]);
        Assert.IsType<Int32Value>(turned[1]);
        Assert.Same(start[2], turned[2]);
        Assert.Equal(["80632, 0, 12299"], Lines(session, Gate, "pos"));
        session.Undo();

        using (var local = TestGestures.Drag(session, target, GizmoTool.Rotate, GizmoSpace.Local, null))
        {
            local.Update(ImGuizmoResults.LocalTurn(target.Matrix(GizmoTool.Rotate), Vector3.UnitX, -38), On);
            local.End();
        }
        var expected = Matrix4x4.CreateFromAxisAngle(Vector3.UnitX, -45 * MathF.PI / 180) *
                       MathHelper.MatrixFromEulerDegrees(0, 105, 0);
        AssertRotation(expected, Values(session, Gate, "rotate"));
    }

    /// <summary>
    /// A zone turns from the rotation of its matrix. A world yaw of 17 degrees of <c>Zone_cylinder</c>
    /// (<c>rotate = -29, 16, 3</c>) snaps to 15 degrees: the engine matrix of the new angles is the zone matrix times
    /// a yaw of 15.
    /// </summary>
    [Fact]
    public void AZoneTurnSnapsFromTheRotationOfTheZone()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var target = GizmoSystem.Target(session, Cylinder);

        using (var drag = TestGestures.Drag(session, target, GizmoTool.Rotate, GizmoSpace.World, null))
        {
            drag.Update(ImGuizmoResults.WorldTurn(target.Matrix(GizmoTool.Rotate), Vector3.UnitY, 17), On);
            drag.End();
        }

        var expected = MathHelper.MatrixFromEulerDegrees(-29, 16, 3) *
                       Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, 15 * MathF.PI / 180);
        AssertRotation(expected, Values(session, Cylinder, "rotate"));
    }

    /// <summary>A Local move snaps along the axes of the item (decision D35). For the gate, one step along X changes X
    /// and Z by fractions, so they become Singles.</summary>
    [Fact]
    public void ALocalMoveSnapsAlongTheAxesOfTheItem()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        using var drag = Begin(session, Gate, GizmoTool.Move, GizmoSpace.Local);
        var along = Vector3.Transform(new Vector3(130, 0, 0), MathHelper.QuatFromEulerDegrees(0, 105, 0));

        drag.Update(ImGuizmoResults.Move(drag.Start, along), On);
        drag.End();

        var pos = Values(session, Gate, "pos");
        Assert.Equal(80632 - 25.882f, Assert.IsType<SingleValue>(pos[0]).ToSingle(), 0.01f);
        Assert.Equal("0", Assert.IsType<Int32Value>(pos[1]).ToString());
        Assert.Equal(12299 - 96.593f, Assert.IsType<SingleValue>(pos[2]).ToSingle(), 0.01f);
    }

    /// <summary>
    /// A Local move of a zone snaps along the axes of the zone matrix. ImGuizmo moves along a row of the matrix
    /// (ImGuizmo.cpp:2119), so 130 along the X row of <c>Zone_cylinder</c> snaps to 100 along that row.
    /// </summary>
    [Fact]
    public void ALocalMoveOfAZoneSnapsAlongTheAxesOfTheZone()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var target = GizmoSystem.Target(session, Cylinder);
        var click = target.Matrix(GizmoTool.Move);
        var row = new Vector3(click.M11, click.M12, click.M13);

        using (var drag = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.Local, null))
        {
            drag.Update(ImGuizmoResults.Move(click, row * 130), On);
            drag.End();
        }

        var pos = Values(session, Cylinder, "pos");
        var moved = new Vector3(pos[0].ToSingle(), pos[1].ToSingle(), pos[2].ToSingle());
        Assert.True(Vector3.Distance(row * 100, moved) < 0.01f, moved.ToString());
    }

    /// <summary>The engine reads no rotation for a light, so a Local move of a light snaps along the world axes, also
    /// when the light has a <c>rotate</c> line.</summary>
    [Fact]
    public void ALocalMoveOfALightSnapsAlongTheWorldAxes()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        using var drag = Begin(session, Light, GizmoTool.Move, GizmoSpace.Local);

        drag.Update(ImGuizmoResults.Move(drag.Start, new Vector3(130, 0, 0)), On);
        drag.End();

        Assert.Equal(["-33420, 0, -30946"], Lines(session, Light, "pos"));
    }

    /// <summary>
    /// A scale writes only the axes that changed, through the size rules of the shape. ImGuizmo measures the axes in
    /// floats, so a rotated axis that the drag did not change comes back with noise. It keeps its value object. A
    /// snapped scale uses the size step.
    /// </summary>
    [Fact]
    public void AScaleWritesOnlyTheAxesThatChanged()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var sphere = GizmoSystem.Section("Zone", "Zone_sphere");
        var start = Values(session, Cylinder, "size");

        Scale(session, Cylinder, new Vector3(1, 1.25f, 1), Off);
        Assert.Equal(["750, 16360"], Lines(session, Cylinder, "size"));
        Assert.Same(start[0], Values(session, Cylinder, "size")[0]);
        Assert.IsType<Int32Value>(Values(session, Cylinder, "size")[1]);

        Scale(session, sphere, new Vector3(1.5f, 1, 1), Off);
        Assert.Equal(["6150"], Lines(session, sphere, "size"));
        Assert.IsType<Int32Value>(Values(session, sphere, "size")[0]);
        session.Undo();
        // 6232 is 43 steps of 50 from 4100 (42.64 steps). With the move step of 100, it would be 6200.
        Scale(session, sphere, new Vector3(1.52f, 1, 1), On);
        Assert.Equal(["6250"], Lines(session, sphere, "size"));
        Assert.Equal(["0, 0, 0"], Lines(session, Cylinder, "pos"));
    }

    /// <summary>A frame with a value that is not finite changes nothing. For a rotate, neither does a frame that is not
    /// a scale, a rotation and a translation, for example a shear.</summary>
    [Fact]
    public void AFrameWithoutAValidMatrixChangesNothing()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var changes = 0;
        session.Changed += _ => changes++;
        var shear = Matrix4x4.Identity with { M12 = 1 };

        using (var move = Begin(session, Depot, GizmoTool.Move, GizmoSpace.World))
        {
            move.Update(move.Start with { M41 = float.NaN }, Off);
            move.Update(move.Start with { M43 = float.PositiveInfinity }, Off);
        }
        using (var rotate = Begin(session, Gate, GizmoTool.Rotate, GizmoSpace.World))
        {
            rotate.Update(ImGuizmoResults.WorldTurn(rotate.Start, Vector3.UnitY, 30) with { M11 = float.NaN }, Off);
            rotate.Update(shear * ImGuizmoResults.WorldTurn(rotate.Start, Vector3.UnitY, 30), Off);
        }

        Assert.Equal(0, changes);
        Assert.False(session.CanUndo);
    }

    /// <summary>A scale drag in one frame. The frame comes from the matrix of the target, which the gizmo holds at the
    /// click (ImGuizmo.cpp:2246), and the drag must start from the same matrix.</summary>
    private static void Scale(EditSession session, SectionRef zone, Vector3 factor, GizmoSnap snap)
    {
        var target = GizmoSystem.Target(session, zone);
        var click = target.Matrix(GizmoTool.Scale);
        using var drag = TestGestures.Drag(session, target, GizmoTool.Scale, GizmoSpace.Local, null);
        Assert.Equal(click, drag.Start);
        drag.Update(ImGuizmoResults.Scale(click, click, factor), snap);
        drag.End();
    }

    /// <summary>Begins a drag of a section that the gizmo can change, while the editor takes edits.</summary>
    private static GizmoDrag Begin(EditSession session, SectionRef section, GizmoTool tool, GizmoSpace space) =>
        TestGestures.Drag(session, GizmoSystem.Target(session, section), tool, space, null);

    private static void AssertBeginThrows(EditSession session, GizmoTarget target, GizmoTool tool, string? noEditReason,
        string reason)
    {
        var undo = session.UndoLabel;
        var error = Assert.Throws<InvalidOperationException>(() =>
            TestGestures.Drag(session, target, tool, GizmoSpace.World, noEditReason));
        Assert.Equal(reason, error.Message);
        Assert.Equal(undo, session.UndoLabel);
    }

    /// <summary>Asserts that the engine matrix of the angles of a <c>rotate</c> line is a rotation, within
    /// 5e-5.</summary>
    private static void AssertRotation(Matrix4x4 expected, IReadOnlyList<ValueBase> rotate)
    {
        var angles = new Vector3(rotate[0].ToSingle(), rotate[1].ToSingle(), rotate[2].ToSingle());
        Assert.True(MaxDifference(MathHelper.MatrixFromEulerDegrees(angles), expected) < 5e-5f, angles.ToString());
    }

    /// <summary>The value text of each line of a key.</summary>
    private static string[] Lines(EditSession session, SectionRef section, string key) =>
        session.Get(section)!.Lines(key).Select(l => string.Join(", ", l.Values.Select(v => v.ToString()))).ToArray();

    /// <summary>The values of the one line of a key.</summary>
    private static IReadOnlyList<ValueBase> Values(EditSession session, SectionRef section, string key) =>
        Assert.Single(session.Get(section)!.Lines(key)).Values;

    private static float MaxDifference(Matrix4x4 a, Matrix4x4 b)
    {
        var largest = 0f;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            largest = MathF.Max(largest, MathF.Abs(a[row, column] - b[row, column]));
        return largest;
    }
}
