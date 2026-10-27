using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Scene;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The mouse and key rules of the gizmo (<see cref="GizmoInput"/>, sections 10.2 and 10.3 of the design), and the
/// drag from frame to frame (<see cref="GizmoDragFlow"/>). The flow tests script the frames as the scene view runs
/// them: what ImGui and ImGuizmo report at the start of the frame, the matrix that <c>Manipulate</c> gives, and what
/// they report after it.
/// </summary>
public sealed class GizmoInputTests
{
    private static readonly GizmoSnap Off = new(false, 100, 15, 100);
    private static readonly SectionRef Depot = GizmoSystem.Section("Object", "Li01_depot");
    private static readonly SectionRef Gate = GizmoSystem.Section("Object", "Li01_gate");
    private static readonly SectionRef Cylinder = GizmoSystem.Section("Zone", "Zone_cylinder");
    private static readonly SectionRef Light = GizmoSystem.Section("LightSource", "Light_main");
    private const string DepotStart = "-38675, 0, -27705";

    [Fact]
    public void AnOpenOrEndedDragOwnsTheMouse()
    {
        foreach (var state in new[] { GizmoDragState.Open, GizmoDragState.Ended })
        foreach (var inputs in Combinations(6))
        {
            Assert.Equal(SceneMouse.Drag, GizmoInput.Owner(state, inputs[0], inputs[1], inputs[2], inputs[3], inputs[4],
                inputs[5]));
        }
    }

    /// <summary>ImGui ends an active item that a frame does not draw, so an orbit, a pan, a fly or a click keeps the
    /// button also over a handle.</summary>
    [Fact]
    public void AnActiveButtonKeepsTheMouseOverAHandle()
    {
        Assert.Equal(SceneMouse.Button, Owner(buttonWasActive: true));
        Assert.Equal(SceneMouse.Handle, Owner());
    }

    [Fact]
    public void AltTheRightButtonAndTheMiddleButtonKeepTheButtonOverAHandle()
    {
        Assert.Equal(SceneMouse.Button, Owner(alt: true));
        Assert.Equal(SceneMouse.Button, Owner(rightDown: true));
        Assert.Equal(SceneMouse.Button, Owner(middleDown: true));
    }

    /// <summary>The hover of ImGuizmo uses the matrices of the last frame, so it counts only when the gizmo showed in
    /// the last frame for the target of this frame.</summary>
    [Fact]
    public void OnlyAHandleThatShowedForTheTargetTakesTheMouse()
    {
        Assert.Equal(SceneMouse.Button, Owner(overHandle: false));
        Assert.Equal(SceneMouse.Button, Owner(shownForTarget: false));
    }

    /// <summary>Over a handle the scene button does not draw, so the view reads the wheel and F from the scene window
    /// and the scene rectangle. During a drag the view reads neither.</summary>
    [Fact]
    public void AHoverOverAHandleStillReadsTheWheelAndF()
    {
        Assert.True(GizmoInput.ReadsViewKeys(SceneMouse.Handle, false, true, true));
        Assert.False(GizmoInput.ReadsViewKeys(SceneMouse.Handle, false, false, true));
        Assert.False(GizmoInput.ReadsViewKeys(SceneMouse.Handle, false, true, false));
        Assert.True(GizmoInput.ReadsViewKeys(SceneMouse.Button, true, false, false));
        Assert.False(GizmoInput.ReadsViewKeys(SceneMouse.Button, false, true, true));
        foreach (var inputs in Combinations(3))
        {
            Assert.False(GizmoInput.ReadsViewKeys(SceneMouse.Drag, inputs[0], inputs[1], inputs[2]));
        }
    }

    /// <summary>
    /// The drag ends on the release of the left button, also when ImGuizmo still drags: a rotate or a scale released
    /// over another window, or an origin behind the camera. Then the editor clears ImGuizmo. The drag does not end
    /// while the button is down, also when ImGuizmo stops to drag.
    /// </summary>
    [Fact]
    public void TheDragEndsOnTheReleaseNotOnIsUsing()
    {
        Assert.Equal(new GizmoFrame(GizmoFrameStep.End, true, GizmoDragState.None),
            GizmoInput.Frame(GizmoDragState.Open, isUsing: true, leftDown: false, cancel: false, targetLost: false));
        Assert.Equal(new GizmoFrame(GizmoFrameStep.End, false, GizmoDragState.None),
            GizmoInput.Frame(GizmoDragState.Open, isUsing: false, leftDown: false, cancel: false, targetLost: false));
        Assert.Equal(new GizmoFrame(GizmoFrameStep.None, false, GizmoDragState.Open),
            GizmoInput.Frame(GizmoDragState.Open, isUsing: false, leftDown: true, cancel: false, targetLost: false));
        // A release in the frame of a cancel or a lost target ends the drag with its last line.
        Assert.Equal(new GizmoFrame(GizmoFrameStep.End, true, GizmoDragState.None),
            GizmoInput.Frame(GizmoDragState.Open, isUsing: true, leftDown: false, cancel: true, targetLost: true));
    }

    /// <summary>Esc or the right button cancels the drag and clears ImGuizmo. The gizmo keeps the mouse until the left
    /// button is up.</summary>
    [Fact]
    public void ACancelKeepsTheMouseUntilTheRelease()
    {
        Assert.Equal(new GizmoFrame(GizmoFrameStep.Cancel, true, GizmoDragState.Ended),
            GizmoInput.Frame(GizmoDragState.Open, isUsing: true, leftDown: true, cancel: true, targetLost: false));
        Assert.Equal(new GizmoFrame(GizmoFrameStep.None, false, GizmoDragState.Ended),
            GizmoInput.Frame(GizmoDragState.Ended, isUsing: false, leftDown: true, cancel: true, targetLost: true));
        Assert.Equal(new GizmoFrame(GizmoFrameStep.None, false, GizmoDragState.None),
            GizmoInput.Frame(GizmoDragState.Ended, isUsing: false, leftDown: false, cancel: false, targetLost: false));
    }

    [Fact]
    public void ADragWhoseTargetIsNotTheSelectionEnds()
    {
        Assert.Equal(new GizmoFrame(GizmoFrameStep.End, true, GizmoDragState.Ended),
            GizmoInput.Frame(GizmoDragState.Open, isUsing: true, leftDown: true, cancel: false, targetLost: true));
    }

    /// <summary>When no drag is open and ImGuizmo drags with the left button up, the editor clears ImGuizmo, for any
    /// path that the other rules do not name.</summary>
    [Fact]
    public void AStaleDragOfImGuizmoIsCleared()
    {
        Assert.Equal(new GizmoFrame(GizmoFrameStep.None, true, GizmoDragState.None),
            GizmoInput.Frame(GizmoDragState.None, isUsing: true, leftDown: false, cancel: false, targetLost: true));
        Assert.Equal(new GizmoFrame(GizmoFrameStep.None, true, GizmoDragState.None),
            GizmoInput.Frame(GizmoDragState.Ended, isUsing: true, leftDown: false, cancel: false, targetLost: false));
        Assert.Equal(new GizmoFrame(GizmoFrameStep.None, false, GizmoDragState.None),
            GizmoInput.Frame(GizmoDragState.None, isUsing: false, leftDown: false, cancel: true, targetLost: true));
    }

    [Fact]
    public void AfterManipulateADragOpensOnlyFromNoDragWithTheButtonDown()
    {
        Assert.Equal(GizmoAfter.Begin, GizmoInput.After(GizmoDragState.None, isUsing: true, leftDown: true));
        Assert.Equal(GizmoAfter.Reset, GizmoInput.After(GizmoDragState.None, isUsing: true, leftDown: false));
        Assert.Equal(GizmoAfter.Reset, GizmoInput.After(GizmoDragState.Ended, isUsing: true, leftDown: true));
        Assert.Equal(GizmoAfter.None, GizmoInput.After(GizmoDragState.None, isUsing: false, leftDown: true));
        Assert.Equal(GizmoAfter.None, GizmoInput.After(GizmoDragState.Ended, isUsing: false, leftDown: true));
        foreach (var inputs in Combinations(2))
        {
            Assert.Equal(GizmoAfter.Update, GizmoInput.After(GizmoDragState.Open, inputs[0], inputs[1]));
        }
    }

    /// <summary>
    /// W, E, R and X need the scene hovered or focused and no text input. The right button flies the camera with W and
    /// E, Ctrl and Alt give other commands, and the tool must not change during a drag (finding 10 of the step 3
    /// review).
    /// </summary>
    [Fact]
    public void ToolKeysFollowEachConditionOfSection10Point3()
    {
        Assert.True(GizmoInput.ToolKeysEnabled(GizmoDragState.None, true, false, false, false, false));
        Assert.False(GizmoInput.ToolKeysEnabled(GizmoDragState.None, false, false, false, false, false));
        Assert.False(GizmoInput.ToolKeysEnabled(GizmoDragState.None, true, true, false, false, false));
        Assert.False(GizmoInput.ToolKeysEnabled(GizmoDragState.None, true, false, true, false, false));
        Assert.False(GizmoInput.ToolKeysEnabled(GizmoDragState.None, true, false, false, true, false));
        Assert.False(GizmoInput.ToolKeysEnabled(GizmoDragState.None, true, false, false, false, true));
        Assert.False(GizmoInput.ToolKeysEnabled(GizmoDragState.Open, true, false, false, false, false));
        Assert.False(GizmoInput.ToolKeysEnabled(GizmoDragState.Ended, true, false, false, false, false));
        Assert.Equal([GizmoTool.Move, GizmoTool.Rotate, GizmoTool.Scale], GizmoInput.ToolKeys.Select(k => k.Tool));
        Assert.Equal("W E R X",
            string.Join(" ", GizmoInput.ToolKeys.Select(k => k.Key.ToString()).Append(GizmoInput.SpaceKey.ToString())));
    }

    [Fact]
    public void AScaleAlwaysUsesTheAxesOfTheItem()
    {
        Assert.Equal(GizmoSpace.Local, GizmoInput.SpaceOf(GizmoTool.Scale, GizmoSpace.World));
        Assert.Equal(GizmoSpace.World, GizmoInput.SpaceOf(GizmoTool.Move, GizmoSpace.World));
        Assert.Equal(GizmoSpace.Local, GizmoInput.SpaceOf(GizmoTool.Rotate, GizmoSpace.Local));
    }

    /// <summary>
    /// A key does what its toolbar button does, and nothing when that button is disabled: E and R on a light, and X
    /// with the scale tool. Without a target, each tool can be selected. When R and X come in one frame, X does nothing,
    /// because the scale tool has no space.
    /// </summary>
    [Fact]
    public void AToolKeyDoesWhatItsToolbarButtonDoes()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var light = GizmoSystem.Target(session, Light);
        var cylinder = GizmoSystem.Target(session, Cylinder);
        const GizmoTool move = GizmoTool.Move, rotate = GizmoTool.Rotate, scale = GizmoTool.Scale;
        const GizmoSpace world = GizmoSpace.World, local = GizmoSpace.Local;

        Assert.Equal((rotate, world), Keys(move, world, null, ImGuiKey.E));
        Assert.Equal((move, world), Keys(move, world, light, ImGuiKey.E));
        Assert.Equal((move, world), Keys(move, world, light, ImGuiKey.R));
        Assert.Equal((move, local), Keys(move, world, light, ImGuiKey.X));
        Assert.Equal((scale, world), Keys(move, world, cylinder, ImGuiKey.R));
        Assert.Equal((scale, local), Keys(scale, local, cylinder, ImGuiKey.X));
        Assert.Equal((scale, world), Keys(move, world, cylinder, ImGuiKey.R, ImGuiKey.X));
        Assert.Equal((move, world), Keys(scale, local, cylinder, ImGuiKey.W, ImGuiKey.X));
        Assert.Equal((scale, world), Keys(rotate, world, null, ImGuiKey.R));
        Assert.Equal((rotate, local), Keys(rotate, local, cylinder));
        Assert.Equal("The engine reads no rotation for a light.", GizmoInput.ToolDisabled(light, rotate));
        Assert.Null(GizmoInput.ToolDisabled(null, scale));
    }

    /// <summary>The scale shows only the handles of the axes that the size can change: one X handle for a sphere
    /// (decision D29), radius and height for a cylinder. The rotate tool turns about one axis.</summary>
    [Fact]
    public void EachToolShowsItsHandles()
    {
        Assert.Equal(GuizmoOperation.TRANSLATE, GizmoDragFlow.OperationOf(GizmoTool.Move, null));
        Assert.Equal(GuizmoOperation.ROTATE_AXIS, GizmoDragFlow.OperationOf(GizmoTool.Rotate, null));
        Assert.Equal(GuizmoOperation.SCALE_X, GizmoDragFlow.OperationOf(GizmoTool.Scale, Axes(ShapeKind.Sphere, 4100)));
        Assert.Equal(GuizmoOperation.SCALE_X | GuizmoOperation.SCALE_Y,
            GizmoDragFlow.OperationOf(GizmoTool.Scale, Axes(ShapeKind.Cylinder, 750, 13088)));
        Assert.Equal(GuizmoOperation.SCALE,
            GizmoDragFlow.OperationOf(GizmoTool.Scale, Axes(ShapeKind.Ring, 3000, 700, 200)));
    }

    /// <summary>
    /// One drag as the scene view runs it: the click frame opens the drag, each frame gives the raw matrix, and the
    /// release ends it with one entry. The release frame applies no new step, and the editor clears ImGuizmo, because
    /// it still drags at the start of that frame.
    /// </summary>
    [Fact]
    public void AClickFramesAndAReleaseMakeOneEntry()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var depot = GizmoSystem.Target(session, Depot);

        Assert.False(Frame(flow, session, depot, isUsing: false, leftDown: false, delta: null));
        Assert.Equal(depot.Matrix(GizmoTool.Move), flow.Shadow);
        Assert.False(Frame(flow, session, depot, isUsing: false, leftDown: true, delta: null, usingAfter: true));
        Assert.Equal(GizmoDragState.Open, flow.State);
        Assert.Equal(Depot, flow.Key);
        foreach (var x in new[] { 300f, 400f, 300f })
        {
            Assert.False(Frame(flow, session, depot, isUsing: true, leftDown: true, delta: new Vector3(x, 0, 0)));
        }

        Assert.Equal(["-37675, 0, -27705"], Lines(session, Depot));
        Assert.False(session.CanUndo);
        // The release: the drag ends before Manipulate, so the move of this frame does not count.
        Assert.True(flow.Start(isUsing: true, leftDown: false, cancel: false, Depot));
        Assert.Equal(GizmoDragState.None, flow.State);
        Assert.True(flow.Prepare(GizmoSystem.Target(session, Depot), GizmoTool.Move, GizmoSpace.World));
        flow.Shadow = flow.Shadow * Matrix4x4.CreateTranslation(500, 0, 0);
        Assert.False(flow.After(isUsing: false, leftDown: false, FakeCommandHost.Runner(session), Off, out _));

        Assert.Equal(["-37675, 0, -27705"], Lines(session, Depot));
        Assert.Equal("Move Li01_depot", session.UndoLabel);
        session.Undo();
        Assert.Equal([DepotStart], Lines(session, Depot));
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// An origin behind the camera: Manipulate gives back its matrix, frame after frame, and ImGuizmo still drags after
    /// the release (ImGuizmo.cpp:2521-2527). The release commits the last applied line and clears ImGuizmo. A rotate
    /// released over another window is the same case.
    /// </summary>
    [Fact]
    public void AReleaseBehindTheCameraCommitsTheLastAppliedLineAndClearsImGuizmo()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var changes = 0;
        session.Changed += _ => changes++;
        var flow = new GizmoDragFlow();
        var depot = GizmoSystem.Target(session, Depot);
        Click(flow, session, depot);
        Frame(flow, session, depot, isUsing: true, leftDown: true, delta: new Vector3(250, 0, -40));
        for (var i = 0; i < 20; i++)
        {
            Assert.False(Frame(flow, session, depot, isUsing: true, leftDown: true, delta: Vector3.Zero));
        }

        Assert.True(flow.Start(isUsing: true, leftDown: false, cancel: false, Depot));
        // The next frame: ImGuizmo is clear, and nothing asks for a reset.
        Assert.False(flow.Start(isUsing: false, leftDown: false, cancel: false, Depot));

        Assert.Equal(GizmoDragState.None, flow.State);
        Assert.Equal(2, changes);
        Assert.Equal(["-38425, 0, -27745"], Lines(session, Depot));
        Assert.Equal("Move Li01_depot", session.UndoLabel);
        session.Undo();
        Assert.Equal([DepotStart], Lines(session, Depot));
    }

    /// <summary>
    /// Esc or a right click cancels the drag: the edit view goes back, no entry is added, and the editor clears
    /// ImGuizmo. Until the release the gizmo keeps the mouse and opens no new drag.
    /// </summary>
    [Fact]
    public void ACancelPutsBackTheEditViewAndOpensNoDragUntilTheRelease()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var depot = GizmoSystem.Target(session, Depot);
        Click(flow, session, depot);
        Frame(flow, session, depot, isUsing: true, leftDown: true, delta: new Vector3(900, 0, 0));
        Assert.True(session.Get(Depot)!.IsChanged);

        Assert.True(flow.Start(isUsing: true, leftDown: true, cancel: true, Depot));
        Assert.Equal(GizmoDragState.Ended, flow.State);
        Assert.Equal([DepotStart], Lines(session, Depot));
        Assert.False(session.CanUndo);
        Assert.False(session.Get(Depot)!.IsChanged);
        Assert.Equal(SceneMouse.Drag, GizmoInput.Owner(flow.State, false, true, true, false, false, false));
        // ImGuizmo drags again while the button is still down: the editor clears it and opens no drag.
        Assert.True(Frame(flow, session, GizmoSystem.Target(session, Depot), isUsing: false, leftDown: true,
            delta: null, usingAfter: true));
        Assert.Equal(GizmoDragState.Ended, flow.State);

        Assert.False(flow.Start(isUsing: false, leftDown: false, cancel: false, Depot));
        Assert.Equal(GizmoDragState.None, flow.State);
        Assert.False(session.CanUndo);
    }

    /// <summary>A change of the selection during a drag commits the line of the last frame and clears ImGuizmo. The
    /// gizmo keeps the mouse until the release.</summary>
    [Fact]
    public void ASelectionChangeCommitsTheDrag()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var depot = GizmoSystem.Target(session, Depot);
        Click(flow, session, depot);
        Frame(flow, session, depot, isUsing: true, leftDown: true, delta: new Vector3(0, 75, 0));

        Assert.True(flow.Start(isUsing: true, leftDown: true, cancel: false, Gate));

        Assert.Equal(GizmoDragState.Ended, flow.State);
        Assert.Equal(["-38675, 75, -27705"], Lines(session, Depot));
        Assert.Equal("Move Li01_depot", session.UndoLabel);
    }

    /// <summary>A forced end (a system change, a profile close or the window close) commits the line of the last
    /// frame.</summary>
    [Fact]
    public void AForcedEndCommitsTheLastLine()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var depot = GizmoSystem.Target(session, Depot);
        Click(flow, session, depot);
        Frame(flow, session, depot, isUsing: true, leftDown: true, delta: new Vector3(0, 0, 10));

        flow.End();

        Assert.Equal(GizmoDragState.None, flow.State);
        Assert.Null(flow.Key);
        Assert.Equal(["-38675, 0, -27695"], Lines(session, Depot));
        Assert.Equal("Move Li01_depot", session.UndoLabel);
        flow.End();
        Assert.Equal("Move Li01_depot", session.UndoLabel);
    }

    /// <summary>When the drag cannot open, for example while a field edit holds a transaction or the editor takes no
    /// edits, the editor clears ImGuizmo and the Console gives the reason. Nothing changes.</summary>
    [Fact]
    public void ABeginThatFailsClearsImGuizmoAndGivesTheReason()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var depot = GizmoSystem.Target(session, Depot);

        Assert.True(flow.Prepare(depot, GizmoTool.Move, GizmoSpace.World));
        Assert.True(flow.After(isUsing: true, leftDown: true,
            FakeCommandHost.Runner(session, "The editor waits for a confirmation."), Off, out var refused));
        Assert.Equal(new GizmoProblem(ConsoleSeverity.Warning,
            "The gizmo did not start a drag: The editor waits for a confirmation.", Depot), refused);
        Assert.Equal(GizmoDragState.None, flow.State);

        using (session.Begin("Set pos"))
        {
            Assert.True(flow.Prepare(depot, GizmoTool.Move, GizmoSpace.World));
            Assert.True(flow.After(isUsing: true, leftDown: true, FakeCommandHost.Runner(session), Off, out var busy));
            Assert.Equal(ConsoleSeverity.Warning, busy!.Severity);
            Assert.StartsWith("The gizmo did not start a drag: ", busy.Message);
        }

        Assert.Equal(GizmoDragState.None, flow.State);
        Assert.False(session.CanUndo);
        Assert.Equal([DepotStart], Lines(session, Depot));
    }

    /// <summary>
    /// When the drag of the target cannot start after the runner opened the gesture, the flow cancels the gesture. So no
    /// transaction stays open, and later edits, undo and save still run. Here the section goes after the prepare of the
    /// frame. A drag start that throws an exception of another type also cancels the gesture, and the exception goes to
    /// the caller.
    /// </summary>
    [Fact]
    public void ADragStartThatFailsAfterTheGestureOpenedLeavesNoTransaction()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        Assert.True(flow.Prepare(GizmoSystem.Target(session, Depot), GizmoTool.Move, GizmoSpace.World));
        session.Execute(ScenePlans.Delete(session, [Depot]).Command!);

        Assert.True(flow.After(isUsing: true, leftDown: true, FakeCommandHost.Runner(session), Off, out var problem));

        Assert.Equal(ConsoleSeverity.Warning, problem!.Severity);
        Assert.StartsWith("The gizmo did not start a drag: ", problem.Message);
        Assert.Equal(GizmoDragState.None, flow.State);
        Assert.False(session.InTransaction);
        Assert.Equal("Delete Li01_depot", session.UndoLabel);

        Assert.True(flow.Prepare(new ThrowingTarget(), GizmoTool.Move, GizmoSpace.World));
        Assert.Throws<ArgumentException>(() =>
            flow.After(isUsing: true, leftDown: true, FakeCommandHost.Runner(session), Off, out _));
        Assert.False(session.InTransaction);
        session.Undo();
        Assert.Equal([DepotStart], Lines(session, Depot));
    }

    /// <summary>A frame whose edit does not apply stops the drag: the edit view goes back, the editor clears ImGuizmo,
    /// the Console gives the reason, and the gizmo keeps the mouse until the release.</summary>
    [Fact]
    public void AFrameThatCannotApplyStopsTheDragWithoutAChange()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var depot = GizmoSystem.Target(session, Depot);
        Click(flow, session, depot);
        Frame(flow, session, depot, isUsing: true, leftDown: true, delta: new Vector3(300, 0, 0));
        Assert.True(session.Get(Depot)!.IsChanged);

        Assert.True(flow.Prepare(depot, GizmoTool.Move, GizmoSpace.World));
        flow.Shadow = ImGuizmoResults.Move(flow.Shadow, new Vector3(50, 0, 0));
        // A snap step of 0 makes GizmoDrag.Update throw.
        Assert.True(flow.After(isUsing: true, leftDown: true, FakeCommandHost.Runner(session),
            Off with { On = true, Move = 0 }, out var problem));

        Assert.Equal(ConsoleSeverity.Error, problem!.Severity);
        Assert.StartsWith("The drag of the gizmo stopped: ", problem.Message);
        Assert.Equal(Depot, problem.Target);
        Assert.Equal(GizmoDragState.Ended, flow.State);
        Assert.Equal([DepotStart], Lines(session, Depot));
        Assert.False(session.CanUndo);
        Assert.False(flow.Start(isUsing: false, leftDown: false, cancel: false, Depot));
        Assert.Equal(GizmoDragState.None, flow.State);
    }

    /// <summary>The gizmo does not show for a tool that the target does not take, or without a target. A drag of an
    /// enabled tool starts from the matrix of the target for that tool, in the space of the tool.</summary>
    [Fact]
    public void TheGizmoShowsOnlyForATargetThatTakesTheTool()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var light = GizmoSystem.Target(session, Light);
        var cylinder = GizmoSystem.Target(session, Cylinder);

        Assert.False(flow.Prepare(light, GizmoTool.Rotate, GizmoSpace.World));
        Assert.Null(flow.Key);
        Assert.False(flow.Prepare(null, GizmoTool.Move, GizmoSpace.World));
        Assert.True(flow.Prepare(light, GizmoTool.Move, GizmoSpace.Local));
        Assert.Equal(GuizmoMode.LOCAL, flow.Mode);
        Assert.True(flow.Prepare(cylinder, GizmoTool.Scale, GizmoSpace.World));
        Assert.Equal(cylinder.Matrix(GizmoTool.Scale), flow.Shadow);
        Assert.Equal(GuizmoOperation.SCALE_X | GuizmoOperation.SCALE_Y, flow.Operation);
        Assert.Equal(GuizmoMode.LOCAL, flow.Mode);
        Assert.Equal(Cylinder, flow.Key);
    }

    /// <summary>The tool and the space stay the same from the click to the end, because ImGuizmo reads them in each
    /// frame (finding 10 of the step 3 review).</summary>
    [Fact]
    public void TheToolAndTheSpaceStayDuringADrag()
    {
        using var ws = GizmoSystem.Create();
        var session = ws.Session("act");
        var flow = new GizmoDragFlow();
        var gate = GizmoSystem.Target(session, Gate);
        Click(flow, session, gate);

        Assert.True(flow.Prepare(GizmoSystem.Target(session, Gate), GizmoTool.Rotate, GizmoSpace.Local));

        Assert.Equal(GuizmoOperation.TRANSLATE, flow.Operation);
        Assert.Equal(GuizmoMode.WORLD, flow.Mode);
        flow.Shadow = flow.Shadow * Matrix4x4.CreateTranslation(10, 0, 0);
        Assert.False(flow.After(isUsing: true, leftDown: true, FakeCommandHost.Runner(session), Off, out _));
        Assert.Equal(["80642, 0, 12299"], Lines(session, Gate));
        Assert.Equal(["0, 105, 0"], session.Get(Gate)!.Lines("rotate").Select(Text).ToArray());
    }

    /// <summary>A target whose drag start throws an exception that is not an <see cref="InvalidOperationException"/>.</summary>
    private sealed class ThrowingTarget : IGizmoTarget
    {
        public object Key => "throwing";

        public IReadOnlyDictionary<GizmoTool, string?> Tools { get; } =
            new Dictionary<GizmoTool, string?> { [GizmoTool.Move] = null, [GizmoTool.Rotate] = null, [GizmoTool.Scale] = null };

        public ZoneSizeAxes? Axes => null;

        public Matrix4x4 Matrix(GizmoTool tool) => Matrix4x4.Identity;

        public string DragLabel(GizmoTool tool) => "Move throwing";

        public IGizmoDrag Begin(CommandTransaction gesture, GizmoTool tool, GizmoSpace space, string? noEditReason) =>
            throw new ArgumentException("The target refuses the drag.");
    }

    /// <summary>The click frame of a move with the World space: ImGuizmo starts to drag and gives back its
    /// matrix.</summary>
    private static void Click(GizmoDragFlow flow, EditSession session, GizmoTarget target)
    {
        Assert.False(Frame(flow, session, target, isUsing: false, leftDown: true, delta: null, usingAfter: true));
        Assert.Equal(GizmoDragState.Open, flow.State);
    }

    /// <summary>
    /// One frame of the scene view for the move tool in the World space. Returns true when a step of the frame asks
    /// to clear ImGuizmo.
    /// </summary>
    /// <param name="isUsing">What <c>ImGuizmo.IsUsing()</c> reports at the start of the frame.</param>
    /// <param name="delta">The move that <c>Manipulate</c> gives in this frame, or null when it changes
    /// nothing.</param>
    /// <param name="usingAfter">What <c>ImGuizmo.IsUsing()</c> reports after <c>Manipulate</c>. Null: the same as at
    /// the start.</param>
    private static bool Frame(GizmoDragFlow flow, EditSession session, GizmoTarget target, bool isUsing, bool leftDown,
        Vector3? delta, bool? usingAfter = null)
    {
        var reset = flow.Start(isUsing, leftDown, cancel: false, target.Section);
        if (!flow.Prepare(target, GizmoTool.Move, GizmoSpace.World))
        {
            return reset;
        }

        if (delta is { } move)
        {
            flow.Shadow = ImGuizmoResults.Move(flow.Shadow, move);
        }

        reset |= flow.After(usingAfter ?? isUsing, leftDown, FakeCommandHost.Runner(session), Off, out var problem);
        Assert.Null(problem);
        return reset;
    }

    /// <summary>The tool and the space after a frame in which the keys were pressed.</summary>
    private static (GizmoTool, GizmoSpace) Keys(GizmoTool tool, GizmoSpace space, GizmoTarget? target,
        params ImGuiKey[] pressed) =>
        GizmoInput.Keys(tool, space, target, key => pressed.Contains(key));

    private static SceneMouse Owner(bool buttonWasActive = false, bool overHandle = true, bool shownForTarget = true,
        bool alt = false, bool rightDown = false, bool middleDown = false) =>
        GizmoInput.Owner(GizmoDragState.None, buttonWasActive, overHandle, shownForTarget, alt, rightDown, middleDown);

    private static ZoneSizeAxes Axes(ShapeKind shape, params int[] size) =>
        ZoneSizeAxes.Of(shape, size.Select(v => (ValueBase)new Int32Value(v)).ToArray());

    /// <summary>Each combination of a number of flags.</summary>
    private static bool[][] Combinations(int count) => Enumerable.Range(0, 1 << count)
        .Select(bits => Enumerable.Range(0, count).Select(i => (bits & (1 << i)) != 0).ToArray()).ToArray();

    private static string[] Lines(EditSession session, SectionRef section) =>
        session.Get(section)!.Lines("pos").Select(Text).ToArray();

    private static string Text(EntryView line) => string.Join(", ", line.Values.Select(v => v.ToString()));
}
