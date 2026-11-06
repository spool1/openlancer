using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Scene;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The THN edit commands through an edit session: each command, then undo and redo. The rename keeps
/// <c>userprops.Actor</c>, the flags give the flags that the engine reads, a drag is one entry of the history, and
/// Reorder refuses to write a delete and a new event for a repeated key.
/// </summary>
public sealed class ThnEditsTests
{
    private static readonly ThnPath Fovh = new("cameraprops.fovh");

    [Fact]
    public void SetChangesOneValueAndUndoAndRedoPutItBack()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;
        var before = W(session);
        var cam = Entity(session, "Cam_A");

        session.Execute(ThnEdits.Set(cam, Fovh, N(20)));

        Assert.Equal(N(20), Value(session, cam, "cameraprops.fovh"));
        Assert.Equal("Set cameraprops.fovh", session.UndoLabel);
        // Only the changed entity is a new table. The other elements stay the same objects.
        var after = W(session);
        Assert.All(after.Entities.Skip(1), e => Assert.Same(before.Entities.Single(b => b.Id == e.Id).Table, e.Table));
        Assert.All(after.Events, e => Assert.Same(before.Events.Single(b => b.Id == e.Id).Table, e.Table));
        var change = Assert.Single(changes);
        Assert.Empty(change.Sections);
        Assert.Equal([ScriptPath], change.Cutscenes);

        session.Undo();
        Assert.Equal(before, W(session));
        session.Redo();
        Assert.Equal(N(20), Value(session, cam, "cameraprops.fovh"));
        Assert.Equal([[ScriptPath], [ScriptPath]], changes.Skip(1).Select(c => c.Cutscenes));
    }

    [Fact]
    public void SetMakesTheMissingRecordsOfItsPath()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cut = Event(session, 0);

        session.Execute(ThnEdits.Set(cut, new ThnPath("props.duration"), N(1)));
        session.Execute(ThnEdits.Set(Entity(session, "Monitor"), new ThnPath("userprops.Priority.level"), T("main")));

        Assert.Equal(Evaluate("x = { 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" }, { duration = 1 } }")["x"], Table(session, cut));
        Assert.Equal(T("main"), Value(session, Entity(session, "Monitor"), "userprops.Priority.level"));
    }

    [Fact]
    public void RemoveRemovesAKeyAndAMissingKeyGivesNoEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        session.Execute(ThnEdits.Remove(cam, new ThnPath("cameraprops.hvaspect")));
        Assert.Null(Value(session, cam, "cameraprops.hvaspect"));
        session.Execute(ThnEdits.Remove(cam, new ThnPath("cameraprops.missing")));

        Assert.Equal("Remove cameraprops.hvaspect", session.UndoLabel);
        session.Undo();
        Assert.False(session.CanUndo);
        Assert.Equal(N(1.5f), Value(session, cam, "cameraprops.hvaspect"));
        session.Redo();
        Assert.Null(Value(session, cam, "cameraprops.hvaspect"));
    }

    /// <summary>A command refuses a value or a key of a path that a save cannot write, so the edit view never holds
    /// a cutscene that a save or the recovery file cannot write.</summary>
    [Fact]
    public void ACommandRefusesAValueOrAKeyThatASaveCannotWrite()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        var key = Assert.Throws<ArgumentException>(() => ThnEdits.Set(cam, new ThnPath("userprops.\u00fc"), T("x")));
        Assert.StartsWith(ThnWriter.TextProblem("userprops.\u00fc")!, key.Message);
        Assert.Throws<ArgumentException>(() => ThnEdits.Set(Event(session, 0), new ThnPath("props.a\u0000"), N(1)));
        Assert.Throws<ArgumentException>(() => ThnEdits.Remove(cam, new ThnPath("userprops.\u00fc")));
        Assert.Throws<ArgumentException>(() => ThnEdits.Revert(ScriptPath, cam, new ThnPath("userprops.\u00fc")));
        Assert.Throws<ArgumentException>(() => ThnEdits.Set(cam, new ThnPath("userprops.note"), T("café")));
        Assert.Throws<ArgumentException>(() => ThnEdits.Set(cam, new ThnPath("entity_name"), T("Cam_B")));
        Assert.Throws<ArgumentException>(() => ThnEdits.Set(Event(session, 0), new ThnPath("time.x"), N(1)));
        var nan = Assert.Throws<ArgumentException>(() => ThnEdits.SetDuration(ScriptPath, float.NaN));
        Assert.StartsWith(ThnNumber.NotFiniteMessage, nan.Message);
        Assert.Throws<ArgumentException>(() => ThnEdits.MoveEvents([Event(session, 1)], float.PositiveInfinity, 0));
        Assert.Throws<ArgumentException>(() => ThnEdits.SetDuration(ScriptPath, -1));
        Assert.Throws<ArgumentException>(() => ThnEdits.RenameEntity(cam, ""));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void SetDurationSetsTheGlobal()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(ThnEdits.SetDuration(ScriptPath, 12.5f));

        Assert.Equal(12.5f, W(session).Duration);
        session.Undo();
        Assert.Equal(10, W(session).Duration);
        session.Redo();
        Assert.Equal(12.5f, W(session).Duration);
    }

    [Fact]
    public void AddEntityAndAddEventAddAtTheEndOfTheirLists()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var marker = Record(F("entity_name", T("Mark_new")), F("type", S("MARKER")));
        var ev = List(N(2), S("START_PSYS"), List(T("Fx")), Record(F("duration", N(1))));

        session.Execute(ThnEdits.AddEntity(ScriptPath, marker));
        session.Execute(ThnEdits.AddEvent(ScriptPath, ev));

        Assert.Equal(marker, W(session).Entities[^1].Table);
        Assert.Equal(-1, W(session).Entities[^1].Origin);
        Assert.Equal(ev, W(session).Events[^1].Table);
        Assert.Equal("Add START_PSYS", session.UndoLabel);
        var duplicate = ThnEdits.AddEntity(ScriptPath, Record(F("entity_name", T("MARK_NEW")), F("type", S("MARKER"))));
        Assert.Equal("Another entity has the name MARK_NEW.", Assert.Throws<InvalidOperationException>(() => session.Execute(duplicate)).Message);
        Assert.Throws<ArgumentException>(() => ThnEdits.AddEvent(ScriptPath, List(N(2), S("START_PSYS"))));
        Assert.Throws<ArgumentException>(() => ThnEdits.AddEntity(ScriptPath, Record(F("type", S("MARKER")))));
        var added = (W(session).Entities[^1].Id, W(session).Events[^1].Id);
        session.Undo();
        session.Undo();
        Assert.Equal(6, W(session).Entities.Count);
        Assert.Equal(6, W(session).Events.Count);
        session.Redo();
        session.Redo();
        Assert.Equal((added, marker, ev), ((W(session).Entities[^1].Id, W(session).Events[^1].Id), W(session).Entities[^1].Table,
            W(session).Events[^1].Table));
    }

    [Fact]
    public void DeleteRemovesAnElementAndUndoPutsItBack()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var fx = Entity(session, "Fx");
        var before = W(session);

        session.Execute(ThnEdits.Delete(fx));

        Assert.DoesNotContain(W(session).Entities, e => e.Id == fx.Id);
        // The events that name the entity stay. The editor asks first and deletes them with their own commands.
        Assert.Equal(6, W(session).Events.Count);
        Assert.Throws<ArgumentException>(() => session.Execute(ThnEdits.Set(fx, Fovh, N(1))));
        session.Undo();
        Assert.Equal(before, W(session));
        session.Redo();
        Assert.Equal(before.Entities.RemoveAt(5), W(session).Entities);
    }

    /// <summary>
    /// The rename changes the name, each event target and each <c>userprops.speaker</c> that names the entity in any
    /// letter case, as the engine finds them. It keeps <c>userprops.Actor</c>, also when the Actor value is the name of
    /// the entity, and it keeps other user props.
    /// </summary>
    [Fact]
    public void RenameEntityChangesTheTargetsAndTheSpeakersAndKeepsTheActor()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");

        session.Execute(ThnEdits.RenameEntity(trent, "Char_hero"));

        Assert.Equal("Rename to Char_hero", session.UndoLabel);
        var expected = Evaluate(Script
            .Replace("entity_name = \"Char_trent\"", "entity_name = \"Char_hero\"")
            .Replace("speaker = \"char_TRENT\"", "speaker = \"Char_hero\"")
            .Replace("Speaker = \"Char_trent\"", "Speaker = \"Char_hero\"")
            .Replace("{ \"Char_Trent\" }", "{ \"Char_hero\" }")
            .Replace("\"Cam_A\", \"char_trent\"", "\"Cam_A\", \"Char_hero\""));
        Assert.Null(ThnValues.FirstDifference(expected, W(session).ToGlobals()));
        Assert.Equal(T("Char_trent"), Value(session, trent, "userprops.Actor"));
        Assert.Equal(T("Char_trent"), Value(session, Entity(session, "Voice_other"), "userprops.note"));
        // The engine reads the result: the targets and the speakers name the renamed entity.
        var script = new ThnScript(Encoding.ASCII.GetBytes(ThnWriter.Write(W(session).ToGlobals())), _ => null!, "x.thn");
        Assert.Equal("Char_hero", script.Entities["Voice_trent"].Speaker);
        Assert.Equal("Char_trent", script.Entities["Char_hero"].Actor);

        session.Undo();
        Assert.Null(ThnValues.FirstDifference(Evaluate(Script), W(session).ToGlobals()));
        session.Redo();
        Assert.Null(ThnValues.FirstDifference(expected, W(session).ToGlobals()));
    }

    [Fact]
    public void RenameEntityRefusesTheNameOfAnotherEntity()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var e = Assert.Throws<InvalidOperationException>(() =>
            session.Execute(ThnEdits.RenameEntity(Entity(session, "Char_trent"), "monitor")));

        Assert.Equal("Another entity has the name monitor.", e.Message);
        session.Execute(ThnEdits.RenameEntity(Entity(session, "Char_trent"), "CHAR_TRENT"));
        Assert.Equal(T("CHAR_TRENT"), Value(session, Entity(session, "CHAR_TRENT"), "entity_name"));
    }

    /// <summary>
    /// A reference that names the old name keeps its lower text when that text names the new name. So a rename to
    /// another letter case changes only the entity, and a rename back gives the lower cutscene.
    /// </summary>
    [Fact]
    public void ARenameBackGivesTheLowerTextsOfTheReferences()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");
        var lower = session.LowerCutscene(ScriptPath)!;

        session.Execute(ThnEdits.RenameEntity(trent, "CHAR_TRENT"));
        Assert.Equal(lower.Events, W(session).Events);
        Assert.Equal(lower.Entities.RemoveAt(2), W(session).Entities.RemoveAt(2));
        session.Execute(ThnEdits.RenameEntity(trent, "Char_hero"));
        session.Execute(ThnEdits.RenameEntity(trent, "Char_trent"));

        Assert.Equal(lower, W(session));
        AssertSaved(session.Save());
        Assert.Null(PatchText(ws));
    }

    /// <summary>A rename does not change an entity that a later entity with the same name hides (decision D64), also
    /// when its speaker names the renamed entity. The later entity with that name changes.</summary>
    [Fact]
    public void ARenameKeepsTheSpeakerOfAHiddenEntity()
    {
        using var ws = Workspace(script: Script.Replace("{ entity_name = \"Fx\"",
            "{ entity_name = \"Voice_other\", type = SOUND, userprops = { speaker = \"Char_trent\" } },\n    { entity_name = \"Fx\""));
        var session = ws.Session("act");
        var (hidden, shown) = (W(session).Entities[4].Id, W(session).Entities[5].Id);

        session.Execute(ThnEdits.RenameEntity(Entity(session, "Char_trent"), "Char_hero"));

        Assert.Equal(T("Char_trent"), Value(session, new ThnRef(ScriptPath, ThnElementKind.Entity, hidden), "userprops.Speaker"));
        Assert.Equal(T("Char_hero"), Value(session, new ThnRef(ScriptPath, ThnElementKind.Entity, shown), "userprops.speaker"));
    }

    /// <summary>
    /// A revert of a renamed entity puts back its name and the references to it in their lower letter case, as a
    /// rename back does. So the cutscene is the lower cutscene again. The revert refuses a name that another entity
    /// has now.
    /// </summary>
    [Fact]
    public void ARevertOfARenamedEntityPutsBackTheReferencesToIt()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");
        session.Execute(ThnEdits.RenameEntity(trent, "Char_hero"));
        session.Execute(ThnEdits.Set(trent, new ThnPath("userprops.category"), T("Hero")));

        session.Execute(ThnEdits.Revert(ScriptPath, trent));

        Assert.Null(ThnValues.FirstDifference(Evaluate(Script), W(session).ToGlobals()));
        Assert.Equal(session.LowerCutscene(ScriptPath), W(session));
        var script = new ThnScript(Encoding.ASCII.GetBytes(ThnWriter.Write(W(session).ToGlobals())), _ => null!, "x.thn");
        Assert.Equal("char_TRENT", script.Entities["Voice_trent"].Speaker);
        Assert.Equal(["Char_Trent"], script.Events.Single(e => e.Type == EventTypes.StartMotion).Targets);
        session.Undo();
        Assert.Equal(T("Char_hero"), Value(session, Entity(session, "Voice_trent"), "userprops.speaker"));
        session.Redo();
        Assert.Equal(session.LowerCutscene(ScriptPath), W(session));

        session.Undo();
        session.Execute(ThnEdits.AddEntity(ScriptPath, Record(F("entity_name", T("char_trent")), F("type", S("MARKER")))));
        var e = Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Revert(ScriptPath, trent)));
        Assert.Equal("Another entity has the name Char_trent.", e.Message);
    }

    /// <summary>The new times and lengths are on the 1 ms grid. The float is the float that the compiler gives for the
    /// decimal text, as for a retail time.</summary>
    [Fact]
    public void MoveEventsRoundsTimesAndLengthsTo1Ms()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var first = Event(session, 1);
        var second = Event(session, 3);

        session.Execute(ThnEdits.MoveEvents([first, second, first], 1.2345f, 0.0016f));

        Assert.Equal("Move 3 events", session.UndoLabel);
        Assert.Equal(ThnNumber(6.235), Value(session, first, "time"));
        Assert.Equal(ThnNumber(2.002), Value(session, first, "props.duration"));
        Assert.Equal(ThnNumber(7.235), Value(session, second, "time"));
        session.Undo();
        Assert.Equal(N(5), Value(session, first, "time"));
        Assert.Equal(N(2), Value(session, first, "props.duration"));
        Assert.Equal(N(6), Value(session, second, "time"));
        session.Redo();
        Assert.Equal(ThnNumber(6.235), Value(session, first, "time"));
        Assert.Equal(ThnNumber(7.235), Value(session, second, "time"));
        // A time of 0 cannot move back.
        Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.MoveEvents([Event(session, 0)], -0.5f, 0)));
        session.Execute(ThnEdits.MoveEvents([Event(session, 0)], 0, 0.25f));
        Assert.Equal(ThnNumber(0.25), Value(session, Event(session, 0), "props.duration"));
        Assert.Equal("Change the length of event", session.UndoLabel);

        static ThnNumber ThnNumber(double text) => new((float)text);
    }

    /// <summary>
    /// An event without <c>props.duration</c> has the length 0. So a length that goes back to 0 on an event whose lower
    /// event has no length removes the key, and the props that the move made. The patch then has no entry for the
    /// event. A lower length goes to the value 0.
    /// </summary>
    [Fact]
    public void ALengthBackTo0LeavesNoValueThatTheLowerEventDoesNotHave()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var (cut, psys) = (Event(session, 0), Event(session, 1));

        session.Execute(ThnEdits.MoveEvents([cut], 0, 0.5f));
        Assert.Equal(N(0.5f), Value(session, cut, "props.duration"));
        session.Execute(ThnEdits.MoveEvents([cut], 0, -0.5f));
        session.Execute(ThnEdits.MoveEvents([psys], 0, -2));

        Assert.Equal(session.LowerCutscene(ScriptPath)!.Events[0].Table, Table(session, cut));
        Assert.Equal(N(0), Value(session, psys, "props.duration"));
        AssertSaved(session.Save());
        var entry = Assert.Single(Patch(ws).Events);
        Assert.Equal(("props.duration", (ThnValue)N(0)), (Assert.Single(entry.Sets).Path.Text, entry.Sets[0].Value));
    }

    /// <summary>
    /// MoveRefusal gives the reason that the run of the move gives, in the same order, and null when the run applies.
    /// Here an event has a time that is not a number, and another event has a length that is not a number. The script
    /// is a whole file of the active mod, because a patch cannot change an event whose time is not a number.
    /// </summary>
    [Fact]
    public void MoveRefusalGivesTheReasonOfTheRunOfTheMove()
    {
        var script = Script.Replace("{ duration = 3 }", "{ duration = \"3\" }")
            .Replace("{ 6, START_SOUND", "{ \"6\", START_SOUND");
        using var ws = Workspace(new() { [WholeFile] = script });
        var session = ws.Session("act");
        var (cut, psys, textLength, sound) = (Event(session, 0), Event(session, 1), Event(session, 2), Event(session, 4));
        const string timeNotANumber = "The time of the event is not a number.";
        const string lengthNotANumber = "The length of the event (props.duration) is not a number.";

        AssertSameReason([psys], -5.001f, 0, ThnEdits.StartsBeforeZero);
        // The time 5 moves to -0.0004, which rounds to 0 on the 1 ms grid.
        AssertSameReason([psys], -5.0004f, 0, null);
        AssertSameReason([psys], 0, -2.001f, ThnEdits.NegativeLength);
        // An event without props.duration has the length 0.
        AssertSameReason([cut], 0, -0.001f, ThnEdits.NegativeLength);
        AssertSameReason([textLength], 0, 1, lengthNotANumber);
        AssertSameReason([textLength], 1, 0, null);
        AssertSameReason([sound], 1, 0, timeNotANumber);
        AssertSameReason([sound], 0, 1, null);
        AssertSameReason([psys, sound], -6, 0, ThnEdits.StartsBeforeZero);
        AssertSameReason([sound, psys], -6, 0, timeNotANumber);
        AssertSameReason([psys], -6, -3, ThnEdits.StartsBeforeZero);
        Assert.False(session.CanUndo);

        void AssertSameReason(ThnRef[] events, float timeDelta, float lengthDelta, string? expected)
        {
            var refusal = ThnEdits.MoveRefusal(session, events, timeDelta, lengthDelta);
            Assert.Equal(expected, refusal);
            if (expected == null)
            {
                session.Execute(ThnEdits.MoveEvents(events, timeDelta, lengthDelta));
                session.Undo();
                return;
            }
            var before = W(session);
            Assert.Equal(expected, Assert.Throws<InvalidOperationException>(() =>
                session.Execute(ThnEdits.MoveEvents(events, timeDelta, lengthDelta))).Message);
            Assert.Same(before, W(session));
        }
    }

    /// <summary>A drag is one transaction: each step takes the place of the step before, and the commit adds one
    /// entry.</summary>
    [Fact]
    public void OneTransactionWith100ApplyStepsGivesOneEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cut = Event(session, 4);
        var changes = 0;
        session.Changed += _ => changes++;

        using (var drag = session.Begin("Drag events"))
        {
            for (var step = 1; step <= 100; step++)
                drag.Apply(ThnEdits.MoveEvents([cut], step * 0.01f, 0));
            drag.Commit();
        }

        Assert.Equal(new ThnNumber(7), Value(session, cut, "time"));
        Assert.Equal("Drag events", session.UndoLabel);
        Assert.Equal(101, changes);
        session.Undo();
        Assert.Equal(new ThnNumber(6), Value(session, cut, "time"));
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// In a patch, Reorder writes a delete and a new event for each event that must run after the moved event. It
    /// refuses that for an event whose key repeats in the lower cutscene (decision D72).
    /// </summary>
    [Fact]
    public void ReorderRefusalGivesAReasonInsideARepeatedKeyAndReorderThrowsThere()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var (first, second) = (Event(session, 1), Event(session, 2));

        var refusal = ThnEdits.ReorderRefusal(session, second, first);

        Assert.Equal("In a patch, Move before deletes the event { 5, START_PSYS, { \"Fx\" } } and adds it again. The key " +
                     "of that event repeats 2 times in the lower cutscene, so a later mod that names one of these events by " +
                     "its place (ol_nth) could then name another event.", refusal);
        var e = Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Reorder(second, first)));
        Assert.Equal(refusal, e.Message);
        Assert.Equal("The event is already just before that event.", ThnEdits.ReorderRefusal(session, first, second));
        Assert.Equal("Move before changes the order of the events of one time. These events have different times.",
            ThnEdits.ReorderRefusal(session, Event(session, 0), first));
        Assert.False(session.CanUndo);
    }

    /// <summary>A Reorder at a time without a repeated key gives the order in the engine, and the patch deletes and adds
    /// the events that run after the moved event.</summary>
    [Fact]
    public void ReorderGivesTheNewOrderInTheEngineThroughAPatch()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var (motion, sound, attach) = (Event(session, 3), Event(session, 4), Event(session, 5));

        Assert.Null(ThnEdits.ReorderRefusal(session, attach, motion));
        session.Execute(ThnEdits.Reorder(attach, motion));

        Assert.Equal([attach.Id, motion.Id, sound.Id], W(session).Events.Where(e => e.Table.List[0] == new ThnNumber(6)).Select(e => e.Id));
        Assert.Equal([-1, -1], W(session).Events.TakeLast(2).Select(e => e.Origin));
        session.Undo();
        Assert.Equal(session.LowerCutscene(ScriptPath), W(session));
        session.Redo();
        Assert.Equal([attach.Id, motion.Id, sound.Id], W(session).Events.Where(e => e.Table.List[0] == new ThnNumber(6)).Select(e => e.Id));
        AssertSaved(session.Save());
        var patch = Patch(ws);
        Assert.Equal(2, patch.Events.Count(e => e.Delete));
        Assert.Equal(2, patch.Events.Count(e => e.New != null));
        var merged = new ThnScript(session.EditView.CreateFileSystem().ReadAllBytes(ScriptPath), _ => null!, ScriptPath);
        Assert.Equal([EventTypes.AttachEntity, EventTypes.StartMotion, EventTypes.StartSound],
            merged.Events.Where(e => e.Time == 6).Select(e => e.Type));
        // The ids stay, so the selection of the moved events survives the save.
        Assert.Equal([attach.Id, motion.Id, sound.Id], W(session).Events.Where(e => e.Table.List[0] == new ThnNumber(6)).Select(e => e.Id));
    }

    /// <summary>
    /// In a patch, Reorder deletes a lower event and adds it again, so the lower cutscene has no values for that event
    /// in the patch. Its revert gives the reason and points to the revert of the cutscene, also in a new session that
    /// reads the saved patch. That revert gives the lower events and their order back.
    /// </summary>
    [Fact]
    public void ARevertOfAnEventThatReorderAddsAgainGivesTheReason()
    {
        const string reason = "The patch deletes the lower event and adds it again at the end, as Move before does. So the " +
                              "lower cutscene has no values for this event in the patch. Revert the whole cutscene to put " +
                              "back the lower events and their order.";
        using var ws = Workspace();
        var session = ws.Session("act");
        var (motion, attach) = (Event(session, 3), Event(session, 5));
        session.Execute(ThnEdits.Reorder(attach, motion));

        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Revert(ScriptPath, motion))).Message);
        // The event keeps its id when a move changes its key.
        session.Execute(ThnEdits.MoveEvents([motion], 0.5f, 0));
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Revert(ScriptPath, motion))).Message);
        session.Undo();
        AssertSaved(session.Save());
        session.Dispose();
        var fresh = ws.Session("act");
        var moved = new ThnRef(ScriptPath, ThnElementKind.Event, W(fresh).Events.Single(e => e.Table.List[1] == S("START_MOTION")).Id);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => fresh.Execute(ThnEdits.Revert(ScriptPath, moved))).Message);
        // An event that the mod adds has no lower event.
        fresh.Execute(ThnEdits.AddEvent(ScriptPath, List(N(8), S("START_PSYS"), List(T("Fx")))));
        var added = Assert.Throws<InvalidOperationException>(() => fresh.Execute(ThnEdits.Revert(ScriptPath, Event(fresh, 6))));
        Assert.Equal("The active mod adds this event, so the lower cutscene has no values for it. Delete it instead.", added.Message);

        fresh.Execute(ThnEdits.Revert(ScriptPath));

        Assert.Same(fresh.LowerCutscene(ScriptPath), W(fresh));
    }

    /// <summary>In a whole file, Reorder moves the event in the list, also inside a repeated key, and the save writes
    /// the new order.</summary>
    [Fact]
    public void ReorderInAWholeFileMovesTheEventInTheList()
    {
        using var ws = Workspace(new() { [WholeFile] = Script });
        var session = ws.Session("act");
        var ids = W(session).Events.Select(e => e.Id).ToArray();
        var (cut, first, second, motion, sound, attach) = (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]);

        Assert.Null(ThnEdits.ReorderRefusal(session, Ref(second), Ref(first)));
        session.Execute(ThnEdits.Reorder(Ref(second), Ref(first)));
        session.Execute(ThnEdits.Reorder(Ref(attach), Ref(motion)));

        Assert.Equal([cut, second, first, attach, motion, sound], W(session).Events.Select(e => e.Id));
        AssertSaved(session.Save());
        var saved = new ThnScript(File.ReadAllBytes(Path.Combine(ws.ModFolder("act"), WholeFile)), _ => null!, ScriptPath);
        Assert.Equal([3f, 2f], saved.Events.Where(e => e.Time == 5).Select(e => e.Duration));
        Assert.Equal([EventTypes.AttachEntity, EventTypes.StartMotion, EventTypes.StartSound],
            saved.Events.Where(e => e.Time == 6).Select(e => e.Type));

        static ThnRef Ref(ThnElementId id) => new(ScriptPath, ThnElementKind.Event, id);
    }

    /// <summary>A patch finds a lower event by its key. A lower event without a key, here with a target that is not a
    /// text, takes no edit and no delete. In a whole file, the same event takes edits.</summary>
    [Fact]
    public void ALowerEventWithoutAKeyTakesNoEditInAPatch()
    {
        var script = Script.Replace("{ \"Cam_A\", \"char_trent\" }", "{ \"Cam_A\", 7 }");
        using var ws = Workspace(script: script);
        var session = ws.Session("act");
        var keyless = Event(session, 5);
        const string reason = "A patch cannot change this event of the lower cutscene, because it has no key: a time, a type " +
                              "that the engine knows, and a list of text targets.";

        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            session.Execute(ThnEdits.Set(keyless, new ThnPath("props.duration"), N(1)))).Message);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Delete(keyless))).Message);
        Assert.False(session.CanUndo);

        using var whole = Workspace(new() { [WholeFile] = script }, script);
        var wholeSession = whole.Session("act");
        wholeSession.Execute(ThnEdits.Delete(Event(wholeSession, 5)));
        Assert.Equal(5, W(wholeSession).Events.Count);
    }

    /// <summary>The engine uses the last entity with a name (ThnScript.cs:132-136). The earlier entity takes only a
    /// delete (decision D64).</summary>
    [Fact]
    public void AnEntityThatALaterEntityHidesTakesOnlyADelete()
    {
        using var ws = Workspace(script: Script.Replace("{ entity_name = \"Fx\"", "{ entity_name = \"MONITOR\", type = MARKER },\n    { entity_name = \"Fx\""));
        var session = ws.Session("act");
        var hidden = Entity(session, "Monitor");

        Assert.Equal(ThnDocument.HiddenEntityMessage, session.CutsceneReadOnlyReason(hidden));
        Assert.Null(session.CutsceneReadOnlyReason(Entity(session, "MONITOR")));
        var e = Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Set(hidden, Fovh, N(1))));
        Assert.Equal(ThnDocument.HiddenEntityMessage, e.Message);
        Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.RenameEntity(hidden, "Other")));
        session.Execute(ThnEdits.Delete(hidden));
        Assert.DoesNotContain(W(session).Entities, x => x.Id == hidden.Id);
    }

    /// <summary>
    /// SetFlags for each state of the flags that the Inspector shows, on each entity type, from a name, from a number
    /// and without the key. A name of another group with the integer type of the flags is a start too: the engine casts
    /// it to its number. The written script gives the flags of the engine: the chosen flags, and each other flag as
    /// before. A stored number stays a number, and one flag from a name or from no key is a name.
    /// </summary>
    [Fact]
    public void SetFlagsGivesTheObjectFlagsOfTheEngineForEachStateOfEachType()
    {
        string[] types = ["CAMERA", "PSYS", "MONITOR", "SCENE", "MARKER", "COMPOUND", "LIGHT", "MOTION_PATH", "DEFORMABLE", "SOUND"];
        var entities = new List<(string Name, string Type, string? Start)>();
        foreach (var type in types)
        {
            var sound = type == "SOUND";
            // CAMERA is 3: REFERENCE and LIT_DYNAMIC. LIGHT is 5: REFERENCE and LIT_AMBIENT, because a SOUND number cannot
            // hold LIT_DYNAMIC.
            var starts = new List<string?> { null, "0", "10", sound ? "1048577" : "SPATIAL", sound ? "2" : "1", sound ? "LIGHT" : "CAMERA" };
            starts.AddRange(ThnFlags.Shown(sound));
            entities.AddRange(starts.Select((s, i) => ($"{type}_{i}", type, s)));
        }
        var script = "duration = 1\nentities = {\n" + string.Join(",\n", entities.Select(e =>
            $"{{ entity_name = \"{e.Name}\", type = {e.Type}{(e.Start == null ? "" : $", flags = {e.Start}")} }}")) + "\n}\nevents = {}\n";
        using var ws = Workspace(script: script);
        var session = ws.Session("act");
        var engine = Engine(W(session));
        var start = W(session);
        var compared = 0;

        for (var mask = 0; mask < 16; mask++)
        {
            var commands = new List<EditCommand>();
            var chosen = new Dictionary<string, HashSet<string>>();
            foreach (var (name, type, _) in entities)
            {
                var shown = ThnFlags.Shown(type == "SOUND");
                chosen[name] = shown.Where((_, bit) => (mask & (1 << bit)) != 0).ToHashSet();
                commands.Add(ThnEdits.SetFlags(Entity(session, name), chosen[name]));
            }
            session.Execute(EditCommand.Group($"Flags {mask}", commands));

            var result = Engine(W(session));
            foreach (var (name, type, startText) in entities)
            {
                var shownFlags = ThnFlags.Shown(type == "SOUND").Aggregate(ThnObjectFlags.None, (f, n) => f | Flag(n));
                var expected = (engine[name] & ~shownFlags) | chosen[name].Aggregate(ThnObjectFlags.None, (f, n) => f | Flag(n));
                Assert.True(expected == result[name], $"{name} (flags = {startText ?? "none"}), mask {mask}: {result[name]}, not {expected}");
                var stored = Table(session, Entity(session, name))["flags"];
                var before = start.Entities.Single(e => e.Table["entity_name"] == new ThnText(name)).Table["flags"];
                if (expected == engine[name])
                    Assert.Equal(before, stored);
                else if (before is ThnNumber)
                    Assert.IsType<ThnNumber>(stored);
                else if (BitOperations.PopCount((uint)expected) == 1)
                    Assert.Equal(new ThnSymbol(Name(expected)), stored);
                else
                    Assert.IsType<ThnNumber>(stored);
                compared++;
            }
            session.Undo();
        }

        Assert.Equal(16 * 100, compared);
        Assert.Equal(start, W(session));
        session.Redo();
        // The last state has all four flags, so an entity without flags gets the number 1 + 2 + 4 + 16.
        Assert.Equal(N(23), Table(session, Entity(session, "CAMERA_0"))["flags"]);
        session.Undo();
        Assert.Equal(start, W(session));
        // A number on a SOUND cannot hold LIT_DYNAMIC, which a name can.
        using var soundWs = Workspace(script: "duration = 1\nentities = { { entity_name = \"S\", type = SOUND, flags = LIT_DYNAMIC } }\nevents = {}\n");
        var soundSession = soundWs.Session("act");
        Assert.Throws<InvalidOperationException>(() =>
            soundSession.Execute(ThnEdits.SetFlags(Entity(soundSession, "S"), new HashSet<string> { "HIDDEN" })));
        Assert.Throws<InvalidOperationException>(() =>
            soundSession.Execute(ThnEdits.SetFlags(Entity(soundSession, "S"), new HashSet<string> { "LIT_DYNAMIC" })));

        static ThnObjectFlags Flag(string name) => (ThnObjectFlags)ThnScript.ThnEnv[name];

        static string Name(ThnObjectFlags flags) => ThnScript.ThnEnv.Single(e => e.Value is ThnObjectFlags f && f == flags).Key;

        static Dictionary<string, ThnObjectFlags> Engine(ThnScriptModel model) =>
            new ThnScript(Encoding.ASCII.GetBytes(ThnWriter.Write(model.ToGlobals())), _ => null!, "flags.thn")
                .Entities.ToDictionary(e => e.Key, e => e.Value.ObjectFlags);
    }

    /// <summary>
    /// A gizmo rotation gives -0 in the orient matrix. Here the gizmo turns the camera -90 degrees about Y. The angles
    /// of <c>EulerNear</c> give the rotation of the engine, and its matrix has -0 in two places. The number rule makes
    /// each -0 a +0, so the save check passes and the save writes no -0.
    /// </summary>
    [Fact]
    public void AGizmoOrientEditWithMinusZeroSaves()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        var turn = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2));
        var degrees = TransformValues.EulerNear(turn, Vector3.Zero);
        var matrix = Matrix4x4.CreateFromQuaternion(MathHelper.QuatFromEulerDegrees(degrees));
        float[][] rows = [[matrix.M11, matrix.M12, matrix.M13], [matrix.M21, matrix.M22, matrix.M23], [matrix.M31, matrix.M32, matrix.M33]];
        Assert.Equal(new Vector3(0, -90, 0), degrees);
        Assert.Equal(2, rows.SelectMany(r => r).Count(v => BitConverter.SingleToInt32Bits(v) == int.MinValue));

        session.Execute(ThnEdits.Set(cam, new ThnPath("spatialprops.orient"),
            List([.. rows.Select(r => (ThnValue)List([.. r.Select(v => (ThnValue)N(v))]))])));

        // The save check evaluates the saved text, which the compiler gives +0 for -0, and compares it with W bit for
        // bit. So without the number rule, this save stops.
        AssertSaved(session.Save());
        Assert.All(((ThnTable)Value(session, cam, "spatialprops.orient")!).List.Cast<ThnTable>().SelectMany(r => r.List),
            v => Assert.NotEqual(int.MinValue, BitConverter.SingleToInt32Bits(((ThnNumber)v).Value)));
        Assert.DoesNotContain("-0,", PatchText(ws)!.Replace(" ", ""));
        Assert.DoesNotContain("-0}", PatchText(ws)!.Replace(" ", ""));
    }

    /// <summary>A change of a cutscene lists the cutscene and no section, so the Scene does not catch up.</summary>
    [Fact]
    public void AGroupOfAnIniEditAndAThnEditIsOneEntryThatListsBoth()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        session.Execute(EditCommand.Group("Both", [
            EditSessionTests.Set(EditSessionTests.Good("g1"), "price", new LibreLancer.Data.Ini.Int32Value(5)),
            ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(30)),
        ]));
        session.Undo();

        Assert.False(session.CanUndo);
        Assert.Equal(new ThnNumber(10), Value(session, Entity(session, "Cam_A"), "cameraprops.fovh"));
        Assert.Equal(["100"], EditSessionTests.Values(session, EditSessionTests.Good("g1"), "price"));
        Assert.All(changes, c =>
        {
            Assert.Equal([EditSessionTests.Good("g1")], c.Sections);
            Assert.Equal([ScriptPath], c.Cutscenes);
        });
    }
}
