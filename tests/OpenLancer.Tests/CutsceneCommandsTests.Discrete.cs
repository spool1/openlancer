using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The discrete cutscene commands of the editor (milestone MCP1-4): New cutscene, the reverts, Remove, the flags, a
/// target, the rename, the deletes, paste, duplicate, Move before, Set camera from view and Add event. Each one gives
/// the W and the label of the session command that it wraps, and each refusal has its text and its detail code.
/// </summary>
public sealed partial class CutsceneCommandsTests
{
    /// <summary>The indices of the events of <see cref="ThnSessions.Script"/>.</summary>
    private const int Cut = 0, Psys = 1, Motion = 3, Sound = 4, Attach = 5;

    private const string NewPath = @"DATA\MODS\act\CUTSCENES\my_cut.thn";

    private static readonly ThnPath Targets = new("targets");

    /// <summary>The flags row of ATTACH_ENTITY in the THN schema.</summary>
    private static readonly ThnKeyRow AttachFlags =
        ThnSchema.Find(ThnElementKind.Event, "ATTACH_ENTITY", new ThnPath("props.flags"))!;

    /// <summary>The edit that the two runs of a revert start from: a new lens and a deleted event.</summary>
    private static void Changed(EditSession session)
    {
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(25)));
        session.Execute(ThnEdits.Delete(Event(session, Sound)));
    }

    [Fact]
    public void CreateGivesTheWOfTheNewCutscene()
    {
        var plan = AssertSameW(s => CutsceneCommands.Create(s, "my_cut"),
            _ => ThnEdits.NewCutscene(NewPath, ThnTemplates.NewScript()),
            state: s => s.Cutscene(NewPath) is { } w ? ThnWriter.Write(w.ToGlobals()) : "", path: NewPath);

        Assert.Equal("New cutscene my_cut", plan.Label);
        Assert.Empty(plan.Changed);
    }

    /// <summary>
    /// A path in use gives the detail PathInUse in any letter case, a name with other characters or with more than 64
    /// characters gives NameInvalid, and a session without an active mod gives NotEditable. Nothing changes.
    /// </summary>
    [Fact]
    public void CreateRefusesWithTheDetailOfEachReason()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var viewer = ws.SecondSession(null);
        var created = FakeCommandHost.Runner(session).Run(CutsceneCommands.Create(session, "my_cut"));
        Assert.Equal(EditStatus.Applied, created.Status);

        AssertRefused(session, CutsceneCommands.Create(session, "MY_CUT"), ThnEdits.PathInUse, MissionErrors.PathInUse);
        AssertRefused(session, CutsceneCommands.Create(session, "my cut"), ThnEdits.BadName, MissionErrors.NameInvalid);
        AssertRefused(session, CutsceneCommands.Create(session, ""), "Type a name for the cutscene.",
            MissionErrors.NameInvalid);
        AssertRefused(session, CutsceneCommands.Create(session, new string('a', ThnEdits.MaxNameLength + 1)),
            "Use a name of at most 64 characters.", MissionErrors.NameInvalid);
        Assert.True(CutsceneCommands.Create(session, new string('a', ThnEdits.MaxNameLength)).CanRun);
        var folder = CutsceneCommands.Create(session, @"..\x");
        Assert.Equal(MissionErrors.NameInvalid, Assert.Single(folder.Report(session).Errors).Detail);
        AssertRefused(viewer, CutsceneCommands.Create(viewer, "my_cut"), ThnEdits.NoActiveMod, MissionErrors.NotEditable);
        Assert.Equal("New cutscene my_cut", session.UndoLabel);
        session.Undo();
        Assert.False(session.CanUndo);
    }

    /// <summary>A revert of the script and a revert of one value give the W of the session command.</summary>
    [Fact]
    public void TheRevertsGiveTheWOfTheRevert()
    {
        var file = AssertSameW(s => CutsceneCommands.RevertFile(s, ScriptPath), _ => ThnEdits.Revert(ScriptPath),
            setup: Changed);
        Assert.Equal("Revert cutscene", file.Label);

        var value = AssertSameW(s => CutsceneCommands.Revert(s, Entity(s, "Cam_A"), Fovh),
            s => ThnEdits.Revert(ScriptPath, Entity(s, "Cam_A"), Fovh), setup: Changed);
        Assert.Equal(("Revert cameraprops.fovh", ThnElementKind.Entity), (value.Label, Assert.Single(value.Changed).Kind));
    }

    /// <summary>Remove gives the W of the session command. A path that each event must have refuses before the
    /// run.</summary>
    [Fact]
    public void RemoveValueGivesTheWOfTheRemove()
    {
        AssertSameW(s => CutsceneCommands.RemoveValue(s, Entity(s, "Cam_A"), Fovh),
            s => ThnEdits.Remove(Entity(s, "Cam_A"), Fovh));

        using var ws = Workspace();
        var session = ws.Session("act");
        var time = CutsceneCommands.RemoveValue(session, Event(session, Psys), new ThnPath("time"));
        Assert.False(time.CanRun);
        Assert.Equal(EditPlan.PlanRefused, Assert.Single(time.Report(session).Errors).Detail);
    }

    [Fact]
    public void SetFlagsGivesTheWOfTheFlags()
    {
        var hidden = new HashSet<string> { ThnFlags.Hidden };
        var plan = AssertSameW(s => CutsceneCommands.SetFlags(s, Entity(s, "Cam_A"), hidden),
            s => ThnEdits.SetFlags(Entity(s, "Cam_A"), hidden));

        Assert.Equal("Set flags", plan.Label);
    }

    /// <summary>
    /// The bits of a flags key give the W of the Set of the value that <see cref="ThnFlags.WriteBits"/> gives. Bits that
    /// do not change on a key without a value remove the key, which changes nothing. A name that is not a bit of the
    /// row refuses with the detail FieldValue, and a value that the engine cannot read refuses with the reason.
    /// </summary>
    [Fact]
    public void SetFlagBitsGivesTheWOfTheBits()
    {
        var symbols = Evaluator.Symbols;
        var defaults = ThnFlags.BitsOn(null, AttachFlags, symbols);
        var bits = new HashSet<string>(defaults) { AttachFlags.Bits.First(b => !defaults.Contains(b.Name)).Name };
        var plan = AssertSameW(s => CutsceneCommands.SetFlagBits(s, Event(s, Attach), AttachFlags, bits),
            s => ThnEdits.Set(Event(s, Attach), AttachFlags.Path, ThnFlags.WriteBits(null, AttachFlags, bits, symbols)!));
        Assert.Equal("Set props.flags", plan.Label);

        using var ws = Workspace(script: Script.Replace("""{ 6, ATTACH_ENTITY, { "Cam_A", "char_trent" } }""",
            """{ 6, ATTACH_ENTITY, { "Cam_A", "char_trent" } }, { 7, ATTACH_ENTITY, { "Fx", "Cam_A" }, { flags = "x" } }"""));
        var session = ws.Session("act");
        var same = CutsceneCommands.SetFlagBits(session, Event(session, Attach), AttachFlags, defaults);
        Assert.Equal("Remove props.flags", same.Label);
        Assert.Equal(EditStatus.NoChange, FakeCommandHost.Runner(session).Run(same).Status);
        AssertRefused(session, CutsceneCommands.SetFlagBits(session, Event(session, Attach), AttachFlags,
            new HashSet<string> { "NOT_A_BIT" }), "'NOT_A_BIT' is not a bit of props.flags.", MissionErrors.FieldValue);
        var unread = CutsceneCommands.SetFlagBits(session, Event(session, 6), AttachFlags, bits);
        Assert.Equal(EditPlan.PlanRefused, Assert.Single(unread.Report(session).Errors).Detail);
        Assert.False(session.CanUndo);
    }

    /// <summary>A target gives the W of the Set of the targets, with the name in the letter case of the
    /// entity.</summary>
    [Fact]
    public void SetTargetGivesTheWOfTheTargets()
    {
        var plan = AssertSameW(s => CutsceneCommands.SetTarget(s, Event(s, Attach), 1, "FX"),
            s => ThnEdits.Set(Event(s, Attach), Targets, List(T("Cam_A"), T("Fx"))));

        Assert.Equal("Set targets", plan.Label);
    }

    /// <summary>
    /// A target must fit the role of the event, as the target list of the Inspector offers it: an entity of another type
    /// refuses with the detail TargetType and the types that fit, and a name without an entity refuses with
    /// ItemNotFound.
    /// </summary>
    [Fact]
    public void SetTargetRefusesAnEntityThatDoesNotFit()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        AssertRefused(session, CutsceneCommands.SetTarget(session, Event(session, Cut), 1, "Fx"),
            "SET_CAMERA needs a CAMERA as target 2. The entity Fx is a PSYS.", MissionErrors.TargetType);
        AssertRefused(session, CutsceneCommands.SetTarget(session, Event(session, Motion), 0, "cam_a"),
            "START_MOTION needs a DEFORMABLE or COMPOUND as target 1. The entity Cam_A is a CAMERA.",
            MissionErrors.TargetType);
        AssertRefused(session, CutsceneCommands.SetTarget(session, Event(session, Cut), 1, "Nobody"),
            "No entity of this script has the name Nobody.", MissionErrors.ItemNotFound);
        AssertRefused(session, CutsceneCommands.SetTarget(session, Event(session, Psys), 1, "Fx"),
            "The event has no target 2.", EditPlan.PlanRefused);
        AssertRefused(session, CutsceneCommands.SetTarget(session, Entity(session, "Fx"), 0, "Fx"),
            ThnEdits.NoSuchElement(ThnElementKind.Event), EditPlan.PlanRefused);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A rename gives the W of the session command. Uses of the old name in other data make the plan ask first
    /// (decision D242). A name of another entity refuses with the detail NameTaken, in any letter case, and the entity
    /// can take its own name in another letter case.
    /// </summary>
    [Fact]
    public void RenameEntityGivesTheWOfTheRenameAndAsksWhenOtherDataUsesTheName()
    {
        var plan = AssertSameW(s => CutsceneCommands.RenameEntity(s, Entity(s, "Char_trent"), "Char_hero", []),
            s => ThnEdits.RenameEntity(Entity(s, "Char_trent"), "Char_hero"));
        Assert.False(plan.NeedsConfirmation);

        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");
        var used = CutsceneCommands.RenameEntity(session, trent, "Char_hero",
            [new ThnNameUse(@"DATA\UNIVERSE\bar.ini", "bar.ini [Camera] name = Char_trent")]);
        Assert.True(used.CanRun && used.NeedsConfirmation);
        AssertRefused(session, CutsceneCommands.RenameEntity(session, trent, "MONITOR", []),
            "Another entity has the name MONITOR.", MissionErrors.NameTaken);
        Assert.True(CutsceneCommands.RenameEntity(session, trent, "CHAR_TRENT", []).CanRun);
    }

    /// <summary>A delete of events is one entry with the W of the deletes, and each event is in it one time.</summary>
    [Fact]
    public void DeleteEventsGivesTheWOfTheDeletes()
    {
        var plan = AssertSameW(
            s => CutsceneCommands.DeleteEvents(s, [Event(s, Psys), Event(s, Motion), Event(s, Psys)]),
            s => EditCommand.Group("Delete 2 events",
                [ThnEdits.Delete(Event(s, Psys)), ThnEdits.Delete(Event(s, Motion))]));

        Assert.Equal(2, plan.Deleted.Count);
        Assert.Equal("Delete event", AssertSameW(s => CutsceneCommands.DeleteEvents(s, [Event(s, Cut)]),
            s => ThnEdits.Delete(Event(s, Cut))).Label);
    }

    /// <summary>
    /// A delete refuses an entity and events of two cutscenes. Each script counts its element ids, so an event of
    /// another script with the same id is not a duplicate: the plan refuses it too, and W does not change.
    /// </summary>
    [Fact]
    public void DeleteEventsRefusesAnEntityAndEventsOfTwoCutscenes()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var psys = Event(session, Psys);
        var before = Text(session);

        AssertRefused(session, CutsceneCommands.DeleteEvents(session, []), "There is no event to delete.",
            EditPlan.PlanRefused);
        AssertRefused(session, CutsceneCommands.DeleteEvents(session, [psys, Entity(session, "Fx")]),
            "Each element of the delete must be an event.", EditPlan.PlanRefused);
        var other = Event(session, Cut) with { Path = @"DATA\y.thn" };
        AssertRefused(session, CutsceneCommands.DeleteEvents(session, [psys, other]), ThnEdits.OneCutscene,
            EditPlan.PlanRefused);
        var sameId = CutsceneCommands.DeleteEvents(session, [psys, psys with { Path = @"DATA\y.thn" }]);
        AssertRefused(session, sameId, ThnEdits.OneCutscene, EditPlan.PlanRefused);
        Assert.Equal(EditStatus.Refused, FakeCommandHost.Runner(session).Run(sameId).Status);
        Assert.Equal(before, Text(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A delete of an entity takes the events that name it in any letter case, in one entry, and asks first because the
    /// engine cannot play an event whose target is gone. An entity that no event names goes at once. The events do not
    /// find an earlier entity that a later entity with the same name hides, so it goes alone.
    /// </summary>
    [Fact]
    public void DeleteEntityTakesTheEventsThatNameItAndAsksFirst()
    {
        var plan = AssertSameW(s => CutsceneCommands.DeleteEntity(s, Entity(s, "Char_trent")),
            s => TimelineEdits.DeleteWithEvents(Entity(s, "Char_trent"), [Event(s, Motion), Event(s, Attach)]));
        Assert.True(plan.NeedsConfirmation);
        Assert.Equal("Delete entity and 2 events", plan.Label);
        Assert.Equal([ThnElementKind.Event, ThnElementKind.Event, ThnElementKind.Entity], plan.Deleted.Select(d => d.Kind));

        using var ws = Workspace(script: Script.Replace("{ entity_name = \"Fx\"",
            "{ entity_name = \"MONITOR\", type = MARKER },\n    { entity_name = \"Fx\""));
        var session = ws.Session("act");
        var alone = CutsceneCommands.DeleteEntity(session, Entity(session, "Voice_other"));
        Assert.Equal(("Delete entity", false), (alone.Label, alone.NeedsConfirmation));
        // The MARKER named MONITOR comes after the MONITOR named Monitor, so the events find the marker.
        var hidden = CutsceneCommands.DeleteEntity(session, Entity(session, "Monitor"));
        Assert.Equal((true, false), (hidden.CanRun, hidden.NeedsConfirmation));
        Assert.Equal(EditStatus.Applied, FakeCommandHost.Runner(session).Run(hidden).Status);
        Assert.Equal(6, W(session).Events.Count);
    }

    /// <summary>A paste and a duplicate give the W of the session commands, and the plans count the new
    /// events.</summary>
    [Fact]
    public void PasteAndDuplicateGiveTheWOfTheCopies()
    {
        var paste = AssertSameW(
            s => CutsceneCommands.Paste(s, ScriptPath, TimelineEdits.Copy(W(s), [Event(s, Motion), Event(s, Sound)]), 2.5,
                "Voice_other"),
            s => TimelineEdits.Paste(ScriptPath, TimelineEdits.Copy(W(s), [Event(s, Motion), Event(s, Sound)]), 2.5,
                "Voice_other"));
        Assert.Equal(("Paste 2 events", 2), (paste.Label, paste.NewEvents));

        var duplicate = AssertSameW(s => CutsceneCommands.Duplicate(s, [Event(s, Motion), Event(s, Psys)]),
            s => TimelineEdits.Duplicate(ScriptPath, TimelineEdits.Copy(W(s), [Event(s, Motion), Event(s, Psys)])));
        Assert.Equal(("Duplicate 2 events", 2), (duplicate.Label, duplicate.NewEvents));
    }

    [Fact]
    public void PasteAndDuplicateRefuseWithoutEvents()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        AssertRefused(session, CutsceneCommands.Paste(session, ScriptPath, new TimelineClip([]), 1, null),
            "Copy one or more events first.", EditPlan.PlanRefused);
        var clip = TimelineEdits.Copy(W(session), [Event(session, Psys)]);
        AssertRefused(session, CutsceneCommands.Paste(session, ScriptPath, clip, -1, null),
            "The time of a paste is a number from 0.", EditPlan.PlanRefused);
        AssertRefused(session, CutsceneCommands.Duplicate(session, []), "There is no event to duplicate.",
            EditPlan.PlanRefused);
        AssertRefused(session, CutsceneCommands.Duplicate(session, [Entity(session, "Fx")]),
            ThnEdits.NoSuchElement(ThnElementKind.Event), EditPlan.PlanRefused);
        Assert.False(session.CanUndo);
    }

    /// <summary>Move before gives the W of the session command, and the reasons of
    /// <see cref="ThnEdits.ReorderRefusal"/> refuse it.</summary>
    [Fact]
    public void ReorderGivesTheWOfMoveBefore()
    {
        var plan = AssertSameW(s => CutsceneCommands.Reorder(s, Event(s, Attach), Event(s, Motion)),
            s => ThnEdits.Reorder(Event(s, Attach), Event(s, Motion)));
        Assert.Equal("Move event before", plan.Label);

        using var ws = Workspace();
        var session = ws.Session("act");
        AssertRefused(session, CutsceneCommands.Reorder(session, Event(session, Motion), Event(session, Psys)),
            "Move before changes the order of the events of one time. These events have different times.",
            EditPlan.PlanRefused);
        AssertRefused(session, CutsceneCommands.Reorder(session, Entity(session, "Fx"), Event(session, Psys)),
            "The element must be an event.", EditPlan.PlanRefused);
    }

    [Fact]
    public void SetStartPoseGivesTheWOfSetCameraFromView()
    {
        var pose = new ThnPoseValue(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f));

        var plan = AssertSameW(s => CutsceneCommands.SetStartPose(s, Entity(s, "Cam_A"), pose, "Set camera from view"),
            s => ThnGizmoTarget.SetStartPose(Entity(s, "Cam_A"), pose, "Set camera from view"));

        Assert.Equal(ThnElementKind.Entity, Assert.Single(plan.Changed).Kind);
    }

    /// <summary>Add event gives the W of the new event of the template, on the 1 ms grid, with the names in the letter
    /// case of the entities.</summary>
    [Fact]
    public void AddEventGivesTheWOfTheNewEvent()
    {
        var psys = AssertSameW(s => CutsceneCommands.AddEvent(s, ScriptPath, "START_PSYS", 1.2346, ["fx"]),
            _ => ThnEdits.AddEvent(ScriptPath, ThnTemplates.NewEvent("START_PSYS", 1.235f, "Fx", null)));
        Assert.Equal(("Add START_PSYS", 1), (psys.Label, psys.NewEvents));

        AssertSameW(s => CutsceneCommands.AddEvent(s, ScriptPath, "SET_CAMERA", 2, ["monitor", "CAM_A"]),
            _ => ThnEdits.AddEvent(ScriptPath, ThnTemplates.NewEvent("SET_CAMERA", 2, "Monitor", "Cam_A")));
    }

    /// <summary>
    /// A type that the editor cannot add, a time below 0 and a time that is not finite refuse with the detail
    /// FieldValue. A wrong number of targets refuses, and each target refuses as for <see cref="CutsceneCommands.SetTarget"/>.
    /// </summary>
    [Fact]
    public void AddEventRefusesATypeATimeAndTargetsThatDoNotFit()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        CutscenePlan Add(string type, double time, params string[] targets) =>
            CutsceneCommands.AddEvent(session, ScriptPath, type, time, targets);

        AssertRefused(session, Add("NO_SUCH_EVENT", 1, "Fx"), "NO_SUCH_EVENT is not an event type that the editor can add.",
            MissionErrors.FieldValue);
        AssertRefused(session, Add("START_PSYS", -0.001, "Fx"), ThnEdits.StartsBeforeZero, MissionErrors.FieldValue);
        AssertRefused(session, Add("START_PSYS", double.NaN, "Fx"), ThnNumber.NotFiniteMessage, MissionErrors.FieldValue);
        AssertRefused(session, Add("START_PSYS", 1), "START_PSYS takes one target.", EditPlan.PlanRefused);
        AssertRefused(session, Add("START_PSYS", 1, "Fx", "Cam_A"), "START_PSYS takes one target.", EditPlan.PlanRefused);
        AssertRefused(session, Add("SET_CAMERA", 1, "Monitor"), "SET_CAMERA needs two targets.", EditPlan.PlanRefused);
        AssertRefused(session, Add("START_SPATIAL_PROP_ANIM", 1), "START_SPATIAL_PROP_ANIM takes one or two targets.",
            EditPlan.PlanRefused);
        AssertRefused(session, Add("START_MOTION", 1, "Cam_A"),
            "START_MOTION needs a DEFORMABLE or COMPOUND as target 1. The entity Cam_A is a CAMERA.",
            MissionErrors.TargetType);
        AssertRefused(session, Add("SET_CAMERA", 1, "Monitor", "Nobody"), "No entity of this script has the name Nobody.",
            MissionErrors.ItemNotFound);
        // The time -0.0004 rounds to 0 on the 1 ms grid.
        Assert.True(Add("START_PSYS", -0.0004, "Fx").CanRun);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A session that takes no edits refuses each discrete command with its reason, and an entity that a later entity
    /// with the same name hides refuses each command that changes it. A delete of such an entity runs (see
    /// <see cref="DeleteEntityTakesTheEventsThatNameItAndAsksFirst"/>).
    /// </summary>
    [Fact]
    public void ADiscreteCommandOfAReadOnlyCutsceneRefusesWithItsReason()
    {
        using var ws = Workspace(script: Script.Replace("{ entity_name = \"Fx\"",
            "{ entity_name = \"MONITOR\", type = MARKER },\n    { entity_name = \"Fx\""));
        var session = ws.Session("act");
        var viewer = ws.SecondSession(null);
        const string noMod = "There is no active mod. Select a mod to edit.";
        var camera = Entity(viewer, "Cam_A");
        var pose = new ThnPoseValue(Vector3.One, Quaternion.Identity);
        var clip = TimelineEdits.Copy(W(viewer), [Event(viewer, Psys)]);
        var flags = new HashSet<string> { ThnFlags.Hidden };

        CutscenePlan[] plans =
        [
            CutsceneCommands.RevertFile(viewer, ScriptPath),
            CutsceneCommands.Revert(viewer, camera, Fovh),
            CutsceneCommands.RemoveValue(viewer, camera, Fovh),
            CutsceneCommands.SetFlags(viewer, camera, flags),
            CutsceneCommands.SetFlagBits(viewer, Event(viewer, Attach), AttachFlags, new HashSet<string>()),
            CutsceneCommands.SetTarget(viewer, Event(viewer, Attach), 1, "Fx"),
            CutsceneCommands.RenameEntity(viewer, camera, "Cam_B", []),
            CutsceneCommands.DeleteEvents(viewer, [Event(viewer, Psys)]),
            CutsceneCommands.DeleteEntity(viewer, camera),
            CutsceneCommands.Paste(viewer, ScriptPath, clip, 1, null),
            CutsceneCommands.Duplicate(viewer, [Event(viewer, Psys)]),
            CutsceneCommands.Reorder(viewer, Event(viewer, Attach), Event(viewer, Motion)),
            CutsceneCommands.SetStartPose(viewer, camera, pose, "Set camera from view"),
            CutsceneCommands.AddEvent(viewer, ScriptPath, "START_PSYS", 1, ["Fx"]),
        ];
        Assert.All(plans, plan => AssertRefused(viewer, plan, noMod, EditPlan.PlanRefused));

        var hidden = Entity(session, "Monitor");
        foreach (var plan in new[]
                 {
                     CutsceneCommands.SetFlags(session, hidden, flags),
                     CutsceneCommands.RemoveValue(session, hidden, new ThnPath("type")),
                     CutsceneCommands.RenameEntity(session, hidden, "Marker_A", []),
                     CutsceneCommands.SetStartPose(session, hidden, pose, "Set camera from view"),
                 })
            AssertRefused(session, plan, ThnDocument.HiddenEntityMessage, EditPlan.PlanRefused);
        Assert.False(session.CanUndo);
    }
}
