using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Thn;
using LibreLancer.Thn.Events;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The edits of the Timeline without ImGui (section 7.3 of the M8 design): each drag is one entry of the history, its
/// changes are on the 1 ms grid, Ctrl snaps to the playhead and to the edges of other events, and the copy, the paste,
/// the duplicate, Add event and the delete of an entity with its events are one entry each. A drag is a gesture of the
/// command layer (decision D766), so the runner opens it and refuses it when the editor takes no edits.
/// </summary>
public sealed class TimelineDragTests
{
    /// <summary>The indices of the events of <see cref="ThnSessions.Script"/>.</summary>
    private const int Cut = 0, Psys = 1, Motion = 3, Sound = 4, Attach = 5;

    private static TimelineModel Model(EditSession session) =>
        TimelineModel.Build(ScriptPath, W(session), Evaluator.Symbols, ScriptPlay.Normal);

    private static TimelineEvent Ev(EditSession session, int index) => Model(session).Event(W(session).Events[index].Id)!;

    /// <summary>The runner of the gestures of a session.</summary>
    private static CommandRunner Commands(EditSession session, string? noEditReason = null) =>
        FakeCommandHost.Runner(session, noEditReason);

    private static float Time(EditSession session, ThnRef ev) => ((ThnNumber)Value(session, ev, "time")!).Value;

    private static float? Length(EditSession session, ThnRef ev) => (Value(session, ev, "props.duration") as ThnNumber)?.Value;

    /// <summary>The tables of the last events of W: the events that a paste or a duplicate added.</summary>
    private static List<ThnTable> Added(EditSession session, int count) =>
        W(session).Events.TakeLast(count).Select(e => e.Table).ToList();

    /// <summary>An event with another time.</summary>
    private static ThnTable At(ThnTable ev, float time) => new(ev.List.SetItem(0, N(time)), ev.Record);

    /// <summary>
    /// A drag of 100 frames is one transaction: the edit view follows each frame, and the release adds one entry. Each
    /// change is on the 1 ms grid. The undo puts back the time, and no entry stays.
    /// </summary>
    [Fact]
    public void ADragOf100FramesIsOneEntryOnThe1MsGrid()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var motion = Ev(session, Motion);
        var changes = 0;
        session.Changed += _ => changes++;

        using (var drag = TimelineDrag.Begin(Commands(session), [motion], TimelineDragKind.Move, 6.2, []))
        {
            for (var step = 1; step <= 100; step++)
            {
                Assert.Null(drag.Drag(6.2 + step * 0.0123456, snap: false, tolerance: 0));
                Assert.Equal(ThnEdits.OnGrid(6 + Math.Round(step * 0.0123456, 3)), Time(session, motion.Ref));
            }

            drag.End();
        }

        Assert.Equal(new ThnNumber(7.235f), Value(session, motion.Ref, "time"));
        Assert.Equal(("Move event", EditOrigin.User), (session.UndoLabel, session.UndoOrigin));
        Assert.Equal(101, changes);
        session.Undo();
        Assert.Equal(6, Time(session, motion.Ref));
        Assert.False(session.CanUndo);
    }

    /// <summary>A drag back to the start puts back the edit view as it was, so it adds no entry. A cancel (Escape)
    /// adds no entry either.</summary>
    [Fact]
    public void ADragBackToTheStartOrACancelAddsNoEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var motion = Ev(session, Motion);

        var back = TimelineDrag.Begin(Commands(session), [motion], TimelineDragKind.Move, 6, []);
        back.Drag(7.5, false, 0);
        back.Drag(6.0004, false, 0);
        back.End();
        var cancelled = TimelineDrag.Begin(Commands(session), [motion], TimelineDragKind.Move, 6, []);
        cancelled.Drag(9, false, 0);
        Assert.Equal(9, Time(session, motion.Ref));
        cancelled.Cancel();

        Assert.False(session.CanUndo);
        Assert.Equal(6, Time(session, motion.Ref));
        Assert.Throws<InvalidOperationException>(() => cancelled.Drag(8, false, 0));
        Assert.Throws<InvalidOperationException>(() => cancelled.End());
    }

    /// <summary>
    /// A step of a drag that does not apply gives the reason, and W holds the state before the drag. The Timeline then
    /// stops the drag with an error in the Console. Here a patch cannot change the event, because the lower event has
    /// no key: its second target is not a text.
    /// </summary>
    [Fact]
    public void ARefusedStepLeavesTheStateBeforeTheDrag()
    {
        using var ws = Workspace(script: Script.Replace("{ \"Cam_A\", \"char_trent\" }", "{ \"Cam_A\", 7 }"));
        var session = ws.Session("act");
        var keyless = Ev(session, Attach);
        var motion = Ev(session, Motion);
        var before = W(session);

        var drag = TimelineDrag.Begin(Commands(session), [motion, keyless], TimelineDragKind.Move, 6, []);
        Assert.StartsWith("A patch cannot change this event of the lower cutscene", drag.Drag(7, false, 0));
        Assert.Same(before, W(session));
        Assert.Equal((0.0, 0.0), (drag.TimeDelta, drag.LengthDelta));
        drag.Cancel();

        Assert.Same(before, W(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// The runner refuses to open a drag when the editor takes no edits, and the message is the reason. A cutscene that
    /// takes no edits refuses the drag too. No gesture stays open, so the next drag opens.
    /// </summary>
    [Fact]
    public void ADragDoesNotOpenWhileTheEditorOrTheCutsceneTakesNoEdits()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var motion = Ev(session, Motion);
        const string reason = "The game runs from this profile.";

        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            TimelineDrag.Begin(Commands(session, reason), [motion], TimelineDragKind.Move, 6, [])).Message);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            TimelineDrag.BeginDuration(Commands(session, reason), ScriptPath, 10, 10, [])).Message);
        TimelineDrag.Begin(Commands(session), [motion], TimelineDragKind.Move, 6, []).Cancel();

        var viewer = ws.SecondSession(null);
        Assert.Equal("There is no active mod. Select a mod to edit.", Assert.Throws<InvalidOperationException>(() =>
            TimelineDrag.BeginDuration(Commands(viewer), ScriptPath, 10, 10, [])).Message);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A cutscene that is read-only by itself refuses to open a drag, also when the editor and the session take edits.
    /// The message is the reason of the cutscene, so the Timeline shows the warning that the drag did not start. No
    /// gesture stays open, and nothing changes.
    /// </summary>
    [Fact]
    public void ADragDoesNotOpenOnACutsceneThatIsReadOnlyByItself()
    {
        using var ws = Workspace(new() { [PatchFile] = BrokenPatch });
        var session = ws.Session("act");
        var commands = Commands(session);
        var motion = Ev(session, Motion);
        var reason = session.CutsceneReadOnlyReason(ScriptPath);
        var before = W(session);
        Assert.Null(session.ReadOnlyReason);
        Assert.NotNull(reason);

        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            TimelineDrag.Begin(commands, [motion], TimelineDragKind.Move, 6, [])).Message);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            TimelineDrag.Begin(commands, [Ev(session, Psys)], TimelineDragKind.End, 7, [])).Message);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            TimelineDrag.BeginDuration(commands, ScriptPath, 10, 10, [])).Message);

        commands.Gesture("No gesture is open").Cancel();
        Assert.Same(before, W(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>A drag of the start of a bar moves its time and keeps its end. It stops at 0 and at the end of the bar.
    /// A drag of the end changes the length, and stops at the start.</summary>
    [Fact]
    public void AStartDragKeepsTheEndAndAnEndDragChangesTheLength()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var psys = Ev(session, Psys);

        var start = TimelineDrag.Begin(Commands(session), [psys], TimelineDragKind.Start, 5, []);
        start.Drag(5.5, false, 0);
        Assert.Equal((5.5f, 1.5f), (Time(session, psys.Ref), Length(session, psys.Ref)!.Value));
        start.Drag(9, false, 0);
        Assert.Equal((7f, 0f), (Time(session, psys.Ref), Length(session, psys.Ref)!.Value));
        start.Drag(-3, false, 0);
        Assert.Equal((0f, 7f), (Time(session, psys.Ref), Length(session, psys.Ref)!.Value));
        start.Drag(3.25, false, 0);
        start.End();
        Assert.Equal((3.25f, 3.75f), (Time(session, psys.Ref), Length(session, psys.Ref)!.Value));
        Assert.Equal("Change the start of event", session.UndoLabel);

        var end = TimelineDrag.Begin(Commands(session), [Ev(session, Psys)], TimelineDragKind.End, 7, []);
        end.Drag(8.125, false, 0);
        Assert.Equal((3.25f, 4.875f), (Time(session, psys.Ref), Length(session, psys.Ref)!.Value));
        end.Drag(0, false, 0);
        end.End();
        Assert.Equal((3.25f, 0f), (Time(session, psys.Ref), Length(session, psys.Ref)!.Value));
        Assert.Equal("Change the length of event", session.UndoLabel);
        session.Undo();
        session.Undo();
        Assert.Equal((5f, 2f), (Time(session, psys.Ref), Length(session, psys.Ref)!.Value));
        Assert.False(session.CanUndo);
        Assert.Throws<ArgumentException>(() =>
            TimelineDrag.Begin(Commands(session), [Ev(session, Cut)], TimelineDragKind.End, 0, []));
    }

    /// <summary>A move of a set moves each event by the same change, in one entry. It stops when the earliest event is
    /// at 0: here the mouse goes 8 s back, and the events go 5 s back.</summary>
    [Fact]
    public void AMoveOfASetMovesEachEventAndStopsAt0()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var (psys, motion) = (Ev(session, Psys), Ev(session, Motion));

        using var drag = TimelineDrag.Begin(Commands(session), [motion, psys], TimelineDragKind.Move, 6, []);
        drag.Drag(-2, false, 0);
        Assert.Equal(-5, drag.TimeDelta);
        drag.End();

        Assert.Equal((0f, 1f), (Time(session, psys.Ref), Time(session, motion.Ref)));
        Assert.Equal("Move 2 events", session.UndoLabel);
        session.Undo();
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// With Ctrl, the moved edge that is nearest to a snap point within the snap distance snaps to it: here the start of
    /// the motion snaps to the end of the sound at 7.5 s, and not its end to the playhead at 9.45 s. Without Ctrl, or out
    /// of the distance, the drag does not snap. The snap points are the playhead and the edges of the other events.
    /// </summary>
    [Fact]
    public void CtrlSnapsTheNearestMovedEdgeToThePlayheadOrToAnEdgeOfAnotherEvent()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var model = Model(session);
        var motion = Ev(session, Motion);
        var snaps = TimelineDrag.SnapPoints(model, [motion.Ref], 9.45);
        Assert.Equal([0, 5, 6, 7, 7.5, 8, 9.45], snaps);

        using var drag = TimelineDrag.Begin(Commands(session), [motion], TimelineDragKind.Move, 6.5, snaps);
        drag.Drag(8.04, snap: true, tolerance: 0.05);
        Assert.Equal((7.5f, 7.5), (Time(session, motion.Ref), drag.SnappedTo!.Value));
        drag.Drag(8.04, snap: false, tolerance: 0.05);
        Assert.Equal(7.54f, Time(session, motion.Ref));
        Assert.Null(drag.SnappedTo);
        drag.Drag(8.04, snap: true, tolerance: 0.02);
        Assert.Equal(7.54f, Time(session, motion.Ref));
        drag.Drag(7.97, snap: true, tolerance: 0.05);
        Assert.Equal(7.45f, Time(session, motion.Ref));
        Assert.Equal(9.45, drag.SnappedTo);
        drag.End();
    }

    /// <summary>A drag of the end handle sets the duration in one entry, on the 1 ms grid, and stops at 0.</summary>
    [Fact]
    public void ADragOfTheEndHandleSetsTheDurationInOneEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        using (var drag = TimelineDrag.BeginDuration(Commands(session), ScriptPath, 10, 10, []))
        {
            drag.Drag(12.34567, false, 0);
            Assert.Equal(12.346f, W(session).Duration);
            drag.Drag(-4, false, 0);
            Assert.Equal(0f, W(session).Duration);
            drag.Drag(11.5, false, 0);
            drag.End();
        }

        Assert.Equal(11.5f, W(session).Duration);
        Assert.Equal(("Set duration", EditOrigin.User), (session.UndoLabel, session.UndoOrigin));
        session.Undo();
        Assert.Equal(10f, W(session).Duration);
        Assert.False(session.CanUndo);
    }

    /// <summary>A press near an edge of a bar drags that edge. In the middle of the bar, and on a diamond, it moves
    /// the event.</summary>
    [Fact]
    public void APressNearAnEdgeOfABarDragsTheEdge()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var psys = Ev(session, Psys);

        Assert.Equal(TimelineDragKind.Start, TimelineDrag.KindAt(psys, 5.05, 0.1));
        Assert.Equal(TimelineDragKind.Start, TimelineDrag.KindAt(psys, 4.95, 0.1));
        Assert.Equal(TimelineDragKind.End, TimelineDrag.KindAt(psys, 6.95, 0.1));
        Assert.Equal(TimelineDragKind.End, TimelineDrag.KindAt(psys, 7.08, 0.1));
        Assert.Equal(TimelineDragKind.Move, TimelineDrag.KindAt(psys, 6, 0.1));
        Assert.Equal(TimelineDragKind.Move, TimelineDrag.KindAt(Ev(session, Cut), 0, 0.1));
    }

    /// <summary>
    /// A SET_CAMERA event has no length, so the Timeline draws it as a diamond, also when hand-written or mod data stores
    /// a length for it. A press at the end of that length moves the event, and the move applies. The edge handles of a
    /// bar would change the length, which the move of events refuses for such an event.
    /// </summary>
    [Fact]
    public void ACutWithAStoredLengthIsADiamondThatMoves()
    {
        using var ws = Workspace(script: Script.Replace("{ 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }",
            "{ 1, SET_CAMERA, { \"Monitor\", \"Cam_A\" }, { duration = 2 } }", StringComparison.Ordinal));
        var session = ws.Session("act");
        var cut = Ev(session, Cut);

        Assert.Equal((false, 0f), (cut.IsBar, cut.Length));
        Assert.Equal(TimelineDragKind.Move, TimelineDrag.KindAt(cut, 3, 0.05));
        using (var drag = TimelineDrag.Begin(Commands(session), [cut], TimelineDragKind.Move, 1, []))
        {
            Assert.Null(drag.Drag(1.5, snap: false, tolerance: 0));
            drag.End();
        }

        Assert.Equal(1.5f, Time(session, cut.Ref));
        Assert.Equal(2f, Length(session, cut.Ref));
        Assert.Equal("Move event", session.UndoLabel);
    }

    /// <summary>A box select takes the bars that overlap the time range and the diamonds in it. The two bars of Fx
    /// overlap, so each is on its own sub-row, and a box over both lines takes both (decision D131).</summary>
    [Fact]
    public void ABoxTakesTheBarsThatOverlapItAndTheDiamondsInIt()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var model = Model(session);
        var lines = model.Lines(new HashSet<TimelineGroupKind>(), false);
        var fx = lines.Where(l => l.Row?.Name == "Fx").ToList();
        var cam = lines.Single(l => l.Row?.Name == "Cam_A");
        var monitor = lines.Single(l => l.Lane?.Name == "Monitor");

        Assert.Equal([0, 1], fx.Select(l => l.SubRow));
        IEnumerable<TimelineEvent> Both(double from, double to) =>
            fx.SelectMany(l => TimelineModel.EventsIn(l, from, to, 0.01));
        Assert.Equal(2, Both(6.5, 7.5).Count());
        Assert.Single(TimelineModel.EventsIn(fx[1], 6.5, 7.5, 0.01));
        Assert.Single(Both(7.1, 9));
        Assert.Empty(Both(8.1, 9));
        Assert.Single(TimelineModel.EventsIn(cam, 5.995, 6.1, 0.01));
        Assert.Single(TimelineModel.EventsIn(monitor, 1, 0.005, 0.01));
    }

    /// <summary>
    /// A copy and a paste at a time is one entry: the earliest event goes to the time and the others keep their offset.
    /// A paste onto another row gives the events of the row of the copy the entity of that row. A paste at a playhead
    /// between two milliseconds puts the events on the 1 ms grid.
    /// </summary>
    [Fact]
    public void APasteMovesTheClipToTheTimeAndOntoTheRowInOneEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var refs = new[] { Event(session, Motion), Event(session, Sound), Event(session, Psys) };
        var clip = TimelineEdits.Copy(W(session), refs);
        Assert.Equal(["START_PSYS", "START_MOTION", "START_SOUND"], clip.Events.Select(e => ((ThnSymbol)e.List[1]).Name));
        Assert.Equal((5f, 8f, "Fx"), (clip.Start, clip.End, clip.Row));

        session.Execute(TimelineEdits.Paste(ScriptPath, clip, 2.5, "Voice_other"));

        Assert.Equal("Paste 3 events", session.UndoLabel);
        var added = Added(session, 3);
        Assert.Equal([2.5f, 3.5f, 3.5f], added.Select(TimelineClip.TimeOf));
        Assert.Equal(["Voice_other", "Char_Trent", "Voice_trent"], added.Select(e => ((ThnText)((ThnTable)e.List[2]).List[0]).Value));
        Assert.Equal(clip.Events.Select(e => e.List[3]), added.Select(e => e.List[3]));

        session.Execute(TimelineEdits.Paste(ScriptPath, clip, 13.65666, null));
        Assert.Equal([13.657f, 14.657f, 14.657f], Added(session, 3).Select(TimelineClip.TimeOf));
        session.Undo();
        session.Undo();
        Assert.Equal(6, W(session).Events.Count);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A duplicate adds the copies right after the events, in one entry: the earliest copy starts at the end of the
    /// event that ends last, and the copies keep their offsets. Events without a length move their copies one frame
    /// (17 ms) later. So no copy has the time, the type and the targets of its source, and a later mod that names the
    /// source by that key still finds one event.
    /// </summary>
    [Fact]
    public void ADuplicateAddsTheCopiesRightAfterTheEvents()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var (psys, motion, diamond) = (W(session).Events[Psys].Table, W(session).Events[Motion].Table, W(session).Events[Attach].Table);
        session.Execute(TimelineEdits.Duplicate(ScriptPath, TimelineEdits.Copy(W(session), [Event(session, Motion), Event(session, Psys)])));
        Assert.Equal("Duplicate 2 events", session.UndoLabel);
        Assert.Equal([At(psys, 8), At(motion, 9)], Added(session, 2));

        session.Execute(TimelineEdits.Duplicate(ScriptPath, TimelineEdits.Copy(W(session), [Event(session, Attach)])));
        Assert.Equal("Duplicate event", session.UndoLabel);
        Assert.Equal(At(diamond, 6.017f), W(session).Events[^1].Table);
        session.Undo();
        session.Undo();
        Assert.Equal(6, W(session).Events.Count);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// The second target of a new event is the first entity that fits its role. It is never the entity of the row, an
    /// earlier entity that a later entity with the same name hides, a MONITOR or the SCENE entity. Without such an
    /// entity, the choice has the reason.
    /// </summary>
    [Fact]
    public void TheSecondTargetOfANewEventIsAnotherEntityThatTheEngineUses()
    {
        const string script = """
            duration = 1
            entities = {
                { entity_name = "Char_A", type = DEFORMABLE },
                { entity_name = "Scene", type = SCENE },
                { entity_name = "Monitor", type = MONITOR },
                { entity_name = "Path", type = MOTION_PATH },
                { entity_name = "PATH", type = MARKER },
                { entity_name = "Char_B", type = DEFORMABLE }
            }
            events = {}
            """;
        using var ws = Workspace(script: script);
        var session = ws.Session("act");

        var choices = TimelineEdits.AddChoices(W(session), "Char_A", Evaluator.Symbols).ToDictionary(c => c.Role.Type);
        Assert.Equal(("PATH", (string?)null), (choices["ATTACH_ENTITY"].Second, choices["ATTACH_ENTITY"].Refusal));
        Assert.Equal("PATH", choices["START_IK"].Second);
        Assert.Equal("START_PATH_ANIMATION needs a second target, and no other entity of this script is a MOTION_PATH.",
            choices["START_PATH_ANIMATION"].Refusal);
        // The SCENE entity takes only a fog animation, and an attachment never names the SCENE as its first target.
        Assert.Equal(["START_FOG_PROP_ANIM"], TimelineEdits.AddChoices(W(session), "Scene", Evaluator.Symbols).Select(c => c.Role.Type));
        Assert.Equal("Char_A", TimelineEdits.AddChoices(W(session), "Char_B", Evaluator.Symbols)
            .Single(c => c.Role.Type == "START_IK").Second);
    }

    /// <summary>
    /// Add event offers the types whose first target fits the entity, in the order of the menu. An event that needs a
    /// second target takes the first entity that fits it, or the choice has the reason. Each new event loads in the
    /// engine with the usual props of its type. A new animation of props has no value, so the engine changes nothing
    /// until an edit sets one: the animation moves from the values at its time, which earlier events can have changed.
    /// </summary>
    [Fact]
    public void AddEventOffersTheTypesOfTheEntityAndEachNewEventLoadsInTheEngine()
    {
        using var ws = Workspace(script: Script.Replace("""{ entity_name = "Fx", type = PSYS, psysprops = {} }""",
            """
            { entity_name = "Fx", type = PSYS, psysprops = {} },
                { entity_name = "Lamp", type = LIGHT, lightprops = { type = L_POINT }, spatialprops = { pos = { 0, 0, 0 } } },
                { entity_name = "Scene", type = SCENE }
            """));
        var session = ws.Session("act");
        Assert.Equal(8, W(session).Entities.Count);

        var monitor = Assert.Single(TimelineEdits.AddChoices(W(session), "Monitor", Evaluator.Symbols));
        Assert.Equal(("SET_CAMERA", "Cam_A", (string?)null), (monitor.Role.Type, monitor.Second, monitor.Refusal));
        Assert.Equal(["ATTACH_ENTITY", "START_SPATIAL_PROP_ANIM", "START_PATH_ANIMATION", "START_PSYS", "START_PSYS_PROP_ANIM"],
            TimelineEdits.AddChoices(W(session), "Fx", Evaluator.Symbols).Select(c => c.Role.Type));
        var character = TimelineEdits.AddChoices(W(session), "char_trent", Evaluator.Symbols);
        Assert.Equal(["START_MOTION", "START_IK", "ATTACH_ENTITY", "START_SPATIAL_PROP_ANIM", "START_PATH_ANIMATION",
            "START_FLR_HEIGHT_ANIM"], character.Select(c => c.Role.Type));
        var path = character.Single(c => c.Role.Type == "START_PATH_ANIMATION");
        Assert.Equal("START_PATH_ANIMATION needs a second target, and no other entity of this script is a MOTION_PATH.",
            path.Refusal);
        Assert.False(CutsceneCommands.AddEvent(session, ScriptPath, path.Role.Type, 1, ["Char_trent"]).CanRun);

        // The commands of the plans of the menu, in one entry.
        var commands = new List<EditCommand>();
        foreach (var name in new[] { "Monitor", "Cam_A", "Char_trent", "Voice_trent", "Fx", "Lamp", "Scene" })
        {
            commands.AddRange(TimelineEdits.AddChoices(W(session), name, Evaluator.Symbols).Where(c => c.Refusal == null)
                .Select(c => CutsceneCommands.AddEvent(session, ScriptPath, c.Role.Type, 1.2346,
                    c.Second == null ? [name] : [name, c.Second]).Command!));
        }

        session.Execute(EditCommand.Group("Add all", commands));
        var script = new ThnScript(Encoding.ASCII.GetBytes(ThnWriter.Write(W(session).ToGlobals())), _ => null!, "all.thn");
        Assert.Equal(6 + commands.Count, script.Events.Count);
        Assert.Equal(commands.Count, script.Events.Count(e => e.Time == 1.235f));
        var motion = Assert.Single(script.Events.OfType<StartMotionEvent>(), e => e.Time == 1.235f);
        Assert.Equal((1f, 1f), (motion.TimeScale, motion.Duration));

        // Each animation of props, on each entity type that takes it, sets no value (for example
        // StartSpatialPropAnimEvent.cs:67-101).
        Assert.Equal(5, script.Events.OfType<StartSpatialPropAnimEvent>().Count());
        Assert.All(script.Events.OfType<StartSpatialPropAnimEvent>(),
            e => Assert.Equal(StartSpatialPropAnimEvent.AnimVars.Nothing, e.SetFlags));
        Assert.Equal(StartCameraPropAnimEvent.AnimFlags.Nothing,
            Assert.Single(script.Events.OfType<StartCameraPropAnimEvent>()).SetFlags);
        Assert.Equal(StartLightPropAnimEvent.AnimVars.Nothing, Assert.Single(script.Events.OfType<StartLightPropAnimEvent>()).SetFlags);
        Assert.Equal(StartFogPropAnimEvent.AnimVars.Nothing, Assert.Single(script.Events.OfType<StartFogPropAnimEvent>()).SetFlags);
        Assert.Equal(StartAudioPropAnimEvent.AnimFlags.Nothing,
            Assert.Single(script.Events.OfType<StartAudioPropAnimEvent>()).SetFlags);
        Assert.False(Assert.Single(script.Events.OfType<StartPSysPropAnimEvent>()).Set);
        AssertSaved(session.Save());
    }

    /// <summary>The delete of an entity with the events that name it is one entry, and the undo puts back all of
    /// them.</summary>
    [Fact]
    public void TheDeleteOfAnEntityWithItsEventsIsOneEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var before = W(session);
        var trent = Entity(session, "Char_trent");
        var events = TimelineEdits.EventsNaming(ScriptPath, W(session), "CHAR_TRENT");
        Assert.Equal([Event(session, Motion), Event(session, Attach)], events);

        session.Execute(TimelineEdits.DeleteWithEvents(trent, "Char_trent", events));

        Assert.Equal("Delete Char_trent and 2 events", session.UndoLabel);
        Assert.Equal(5, W(session).Entities.Count);
        Assert.Equal(4, W(session).Events.Count);
        session.Undo();
        Assert.Equal(before, W(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>A Shift+click adds an event to the selection or removes it. One event is a reference, more are a set,
    /// and two sets with the same events are equal.</summary>
    [Fact]
    public void AShiftClickAddsOrRemovesAnEventOfTheSelection()
    {
        var path = ScriptPath;
        ThnRef Ev(long id) => new(path, ThnElementKind.Event, new ThnElementId(id));

        var one = ThnEventSet.Toggle(null, Ev(1));
        Assert.Equal(Ev(1), one);
        var two = Assert.IsType<ThnEventSet>(ThnEventSet.Toggle(one, Ev(2)));
        Assert.Equal(new ThnEventSet(path, [new ThnElementId(2), new ThnElementId(1)]), two);
        Assert.Equal(Ev(2), ThnEventSet.Toggle(two, Ev(1)));
        Assert.Null(ThnEventSet.Toggle(Ev(1), Ev(1)));
        Assert.Equal([Ev(1), Ev(2)], ThnEventSet.EventsOf(two));
        Assert.Empty(ThnEventSet.EventsOf(new ThnRef(path, ThnElementKind.Entity, new ThnElementId(1))));
    }
}
