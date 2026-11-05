using System;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The cutscene commands of the command layer (section 4.0.8 of the M6b design, decision D766): each command gives the
/// W of the session command that it wraps, a cutscene that takes no edits refuses with its reason, and a value that the
/// data cannot hold refuses with the detail FieldValue. A command never throws for a rule of the edit.
/// </summary>
public sealed class CutsceneCommandsTests
{
    /// <summary>The script of <see cref="ThnSessions.Script"/> with a curve of three points on the first START_PSYS of
    /// Fx.</summary>
    private static readonly string CurveScript = Script.Replace("{ 5, START_PSYS, { \"Fx\" }, { duration = 2 } }",
        "{ 5, START_PSYS, { \"Fx\" }, { duration = 2, param_curve = { CLSID = \"FreeFormPCurve\", points = { " +
        "{ 0, 0, 0, 0 }, { 0.5, 0.25, 0, 0 }, { 1, 1, 0, 0 } } } } }");

    private static readonly ThnPath Fovh = new("cameraprops.fovh");

    /// <summary>The text of W, to compare two states of a script.</summary>
    private static string Text(EditSession session) => ThnWriter.Write(W(session).ToGlobals());

    /// <summary>
    /// Runs the session command and then, after its undo, the plan of the command of the layer through the runner of
    /// the editor. Both give the same W, and the plan is one entry with its label.
    /// </summary>
    private static CutscenePlan AssertSameW(Func<EditSession, CutscenePlan> plan, Func<EditSession, EditCommand> raw,
        string script = Script)
    {
        using var ws = Workspace(script: script);
        var session = ws.Session("act");
        var before = Text(session);
        session.Execute(raw(session));
        var expected = Text(session);
        Assert.NotEqual(before, expected);
        session.Undo();

        var made = plan(session);
        var result = FakeCommandHost.Runner(session).Run(made);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(expected, Text(session));
        Assert.Equal((made.Label, EditOrigin.User), (session.UndoLabel, session.UndoOrigin));
        Assert.Equal(ScriptPath, made.Path);
        return made;
    }

    private static void AssertRefused(EditSession session, EditPlan plan, string message, string detail)
    {
        Assert.False(plan.CanRun);
        var error = Assert.Single(plan.Report(session).Errors);
        Assert.Equal((EditErrorKind.Refused, detail, message), (error.Kind, error.Detail, error.Message));
    }

    [Fact]
    public void SetValueGivesTheWOfTheSet()
    {
        var plan = AssertSameW(s => CutsceneCommands.SetValue(s, Entity(s, "Cam_A"), Fovh, N(25)),
            s => ThnEdits.Set(Entity(s, "Cam_A"), Fovh, N(25)));

        Assert.Equal("Set cameraprops.fovh", plan.Label);
        Assert.Equal(ThnElementKind.Entity, Assert.Single(plan.Changed).Kind);
    }

    /// <summary>A move and a change of the length give the W of the session command, on the 1 ms grid. Each event is
    /// one changed element, also when the request names it twice.</summary>
    [Fact]
    public void MoveEventsGivesTheWOfTheMove()
    {
        var plan = AssertSameW(
            s => CutsceneCommands.MoveEvents(s, [Event(s, 1), Event(s, 3), Event(s, 1)], 1.23456f, 0.5f),
            s => ThnEdits.MoveEvents([Event(s, 1), Event(s, 3), Event(s, 1)], 1.23456f, 0.5f));
        Assert.Equal("Move 3 events", plan.Label);
        Assert.Equal(2, plan.Changed.Count);

        var length = AssertSameW(s => CutsceneCommands.MoveEvents(s, [Event(s, 1)], 0, -2),
            s => ThnEdits.MoveEvents([Event(s, 1)], 0, -2));
        Assert.Equal("Change the length of event", length.Label);
    }

    [Fact]
    public void SetDurationGivesTheWOfTheDuration()
    {
        var plan = AssertSameW(s => CutsceneCommands.SetDuration(s, ScriptPath, 12.5f),
            _ => ThnEdits.SetDuration(ScriptPath, 12.5f));

        Assert.Equal("Set duration", plan.Label);
        Assert.Empty(plan.Changed);
    }

    /// <summary>A curve point gives the W of the Set of the curve with that point, so the other points keep their
    /// tables.</summary>
    [Fact]
    public void SetCurvePointGivesTheWOfTheCurveWithThePoint()
    {
        var point = new ThnCurvePoint(0.75f, 0.5f, 1, 2);
        var plan = AssertSameW(s => CutsceneCommands.SetCurvePoint(s, Event(s, 1), 1, point),
            s => ThnEdits.Set(Event(s, 1), ThnCurve.Path,
                ThnCurve.WithPoint((ThnTable)Value(s, Event(s, 1), ThnCurve.Path.Text)!, 1, point)), CurveScript);

        Assert.Equal("Move point 2 of the curve", plan.Label);
        Assert.Equal(ThnElementKind.Event, Assert.Single(plan.Changed).Kind);
    }

    /// <summary>A move of 0 and 0 gives a plan that changes nothing: it adds no entry. A move without events
    /// refuses.</summary>
    [Fact]
    public void AMoveOf0And0ChangesNothingAndAMoveWithoutEventsRefuses()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var before = W(session);

        var none = CutsceneCommands.MoveEvents(session, [Event(session, 1)], 0, 0);
        Assert.True(none.CanRun);
        Assert.Equal(EditStatus.NoChange, FakeCommandHost.Runner(session).Run(none).Status);

        Assert.Same(before, W(session));
        Assert.False(session.CanUndo);
        AssertRefused(session, CutsceneCommands.MoveEvents(session, [], 1, 0), "There is no event to move.",
            EditPlan.PlanRefused);
    }

    /// <summary>
    /// A cutscene that takes no edits refuses each command with its reason, and nothing changes: here a session without
    /// an active mod, and an entity that a later entity with the same name hides.
    /// </summary>
    [Fact]
    public void AReadOnlyCutsceneRefusesWithItsReason()
    {
        using var ws = Workspace(script: CurveScript.Replace("{ entity_name = \"Fx\"",
            "{ entity_name = \"MONITOR\", type = MARKER },\n    { entity_name = \"Fx\""));
        var session = ws.Session("act");
        var viewer = ws.SecondSession(null);
        const string noMod = "There is no active mod. Select a mod to edit.";

        AssertRefused(viewer, CutsceneCommands.SetValue(viewer, Entity(viewer, "Cam_A"), Fovh, N(1)), noMod,
            EditPlan.PlanRefused);
        AssertRefused(viewer, CutsceneCommands.MoveEvents(viewer, [Event(viewer, 1)], 1, 0), noMod,
            EditPlan.PlanRefused);
        AssertRefused(viewer, CutsceneCommands.SetDuration(viewer, ScriptPath, 4), noMod, EditPlan.PlanRefused);
        var point = new ThnCurvePoint(0, 1, 0, 0);
        AssertRefused(viewer, CutsceneCommands.SetCurvePoint(viewer, Event(viewer, 1), 0, point), noMod,
            EditPlan.PlanRefused);
        AssertRefused(session, CutsceneCommands.SetValue(session, Entity(session, "Monitor"), Fovh, N(1)),
            ThnDocument.HiddenEntityMessage, EditPlan.PlanRefused);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A move that puts an event before 0 or gives it a negative length refuses before it runs, with the detail
    /// FieldValue and the text of the session command. So do a negative duration and a number that is not finite. W
    /// does not change.
    /// </summary>
    [Fact]
    public void ANegativeTimeLengthOrDurationRefusesWithTheDetailFieldValue()
    {
        using var ws = Workspace(script: CurveScript);
        var session = ws.Session("act");
        var psys = Event(session, 1);
        var before = W(session);
        const string value = MissionErrors.FieldValue;

        AssertRefused(session, CutsceneCommands.MoveEvents(session, [Event(session, 3), psys], -5.001f, 0),
            ThnEdits.StartsBeforeZero, value);
        AssertRefused(session, CutsceneCommands.MoveEvents(session, [psys], 0, -2.001f), ThnEdits.NegativeLength,
            value);
        AssertRefused(session, CutsceneCommands.MoveEvents(session, [psys], float.NaN, 0), ThnNumber.NotFiniteMessage,
            value);
        AssertRefused(session, CutsceneCommands.SetDuration(session, ScriptPath, -0.5f), ThnEdits.NegativeDuration,
            value);
        AssertRefused(session, CutsceneCommands.SetDuration(session, ScriptPath, float.PositiveInfinity),
            ThnNumber.NotFiniteMessage, value);
        var notFinite = new ThnCurvePoint(0.5f, float.NaN, 0, 0);
        AssertRefused(session, CutsceneCommands.SetCurvePoint(session, psys, 1, notFinite), ThnNumber.NotFiniteMessage,
            value);
        // The time 5 moves to -0.0004, which rounds to 0 on the 1 ms grid.
        Assert.True(CutsceneCommands.MoveEvents(session, [psys], -5.0004f, 0).CanRun);

        Assert.Same(before, W(session));
    }

    /// <summary>A move of a length that is not a number refuses before it runs, with the detail FieldValue and the
    /// reason of the session command. A move of the time of that event applies.</summary>
    [Fact]
    public void AMoveOfAValueThatIsNotANumberRefusesBeforeItRuns()
    {
        using var ws = Workspace(script: Script.Replace("{ duration = 3 }", "{ duration = \"3\" }"));
        var session = ws.Session("act");
        var textLength = Event(session, 2);
        const string notANumber = "The length of the event (props.duration) is not a number.";

        Assert.Equal(notANumber, ThnEdits.MoveRefusal(session, [textLength], 0, 1));
        AssertRefused(session, CutsceneCommands.MoveEvents(session, [textLength], 0, 1), notANumber,
            MissionErrors.FieldValue);
        Assert.True(CutsceneCommands.MoveEvents(session, [textLength], 1, 0).CanRun);
    }

    /// <summary>
    /// A plan names its script in the form that the session stores, and each element one time. This is also true when
    /// the caller writes the path of the script in another letter case or with other separators.
    /// </summary>
    [Fact]
    public void APlanNamesTheScriptAndEachElementInOneForm()
    {
        using var ws = Workspace(script: CurveScript);
        var session = ws.Session("act");
        var psys = Event(session, 1);
        var camera = Entity(session, "Cam_A");
        const string other = "data/scripts/X.THN";

        var move = CutsceneCommands.MoveEvents(session, [psys, psys with { Path = other }], 1, 0);
        Assert.Equal(ScriptPath, move.Path);
        Assert.Equal(psys, Assert.Single(move.Changed));
        var set = CutsceneCommands.SetValue(session, camera with { Path = other }, Fovh, N(25));
        Assert.Equal((ScriptPath, camera), (set.Path, Assert.Single(set.Changed)));
        var point = CutsceneCommands.SetCurvePoint(session, psys with { Path = other }, 1, new(0.5f, 1, 0, 0));
        Assert.Equal((ScriptPath, psys), (point.Path, Assert.Single(point.Changed)));
        Assert.Equal(ScriptPath, CutsceneCommands.SetDuration(session, other, 4).Path);

        Assert.Equal(EditStatus.Applied, FakeCommandHost.Runner(session).Run(move).Status);
        Assert.Equal(N(6), Value(session, psys, "time"));
    }

    /// <summary>A curve point that the event does not have refuses with the number of the point, also on an event
    /// without a curve.</summary>
    [Fact]
    public void ACurvePointThatTheEventDoesNotHaveRefuses()
    {
        using var ws = Workspace(script: CurveScript);
        var session = ws.Session("act");
        var point = new ThnCurvePoint(0.5f, 1, 0, 0);

        AssertRefused(session, CutsceneCommands.SetCurvePoint(session, Event(session, 1), 3, point),
            "The curve of the event has no point 4.", EditPlan.PlanRefused);
        AssertRefused(session, CutsceneCommands.SetCurvePoint(session, Event(session, 0), 0, point),
            "The curve of the event has no point 1.", EditPlan.PlanRefused);
        Assert.Equal(3, CutsceneCommands.CurvePoints(session, Event(session, 1))!.Count);
        Assert.Null(CutsceneCommands.CurvePoints(session, Event(session, 0)));
    }

    /// <summary>The time field of an event refuses a value that is not a number with the detail FieldValue, and a time
    /// below 0 with the text of the move.</summary>
    [Fact]
    public void TheTimeFieldRefusesAValueThatIsNotANumber()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var psys = Event(session, 1);
        var time = ThnFieldCommand.Time(psys, 5);

        AssertRefused(session, time.Make(session, T("5")), "The value must be a number.", MissionErrors.FieldValue);
        AssertRefused(session, ThnFieldCommand.Length(psys, 2).Make(session, T("1")), "The value must be a number.",
            MissionErrors.FieldValue);
        AssertRefused(session, time.Make(session, N(-1)), ThnEdits.StartsBeforeZero, MissionErrors.FieldValue);
        Assert.True(time.Make(session, N(6)).CanRun);
    }

    /// <summary>
    /// The new elements of a plan exist only after the run, so <see cref="CutscenePlan.Added"/> gives the last entities
    /// and the last events of W after the run, entities first. A plan that adds nothing gives no element.
    /// </summary>
    [Fact]
    public void AddedGivesTheNewElementsAfterTheRun()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var entity = Record(F("entity_name", T("Mark")), F("type", S("MARKER")));
        var ev = List(N(1), S("ATTACH_ENTITY"), List(T("Mark"), T("Cam_A")));

        var plan = CutscenePlan.Of("Add a marker", ScriptPath, () => EditCommand.Group("Add a marker",
            [ThnEdits.AddEntity(ScriptPath, entity), ThnEdits.AddEvent(ScriptPath, ev)]), newEntities: 1, newEvents: 1);
        Assert.Equal(EditStatus.Applied, FakeCommandHost.Runner(session).Run(plan).Status);

        Assert.Equal([
            new ThnRef(ScriptPath, ThnElementKind.Entity, W(session).Entities[^1].Id),
            new ThnRef(ScriptPath, ThnElementKind.Event, W(session).Events[^1].Id),
        ], plan.Added(session));
        Assert.Equal(entity, W(session).Entities[^1].Table);
        Assert.Empty(CutsceneCommands.SetDuration(session, ScriptPath, 3).Added(session));
    }
}
