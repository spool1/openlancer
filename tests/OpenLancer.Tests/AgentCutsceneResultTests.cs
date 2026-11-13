using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The cutscene part of the result of an agent call (section 5.6 and decision D767 of the MCP1 design). The references
/// of adds and changes hold the index of an event after the call, and a delete holds the index before the call. The
/// commit checks give the cutscene errors that the call adds and removes.
/// </summary>
public sealed class AgentCutsceneResultTests
{
    private const string Ref = "thn:" + ScriptPath;

    /// <summary>A script whose SET_CAMERA has a REFERENCE monitor as its second target. The scene does not make a
    /// REFERENCE entity, so the check gives no error, until the flag goes.</summary>
    private const string ReferenceMonitorScript = """
        duration = 10
        entities = {
            { entity_name = "Cam_A", type = CAMERA, cameraprops = { fovh = 10, hvaspect = 1.5, nearplane = 1, farplane = 100 }, spatialprops = { pos = { 0, 0, 0 }, orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } } },
            { entity_name = "Monitor", type = MONITOR, flags = REFERENCE }
        }
        events = {
            { 0, SET_CAMERA, { "Cam_A", "Monitor" } }
        }
        """;

    /// <summary>Runs a body as an agent call, and gives the result of the call.</summary>
    private static EditResult Call(EditSession session, Action<EditScope> body, bool preview = false)
    {
        EditResult? result = null;
        var host = new TestAgentHost(session);
        var call = new ToolCall(host, "test", ToolInput.None.Read("test", null));
        EditTools.Commit(call, null, preview, withPatch: false, confirm: true, body, r =>
        {
            result = r;
            return EditTools.Output(r);
        });
        return result!;
    }

    [Fact]
    public void AnAddGivesTheIndexAfterTheCallAndADeleteTheIndexBeforeIt()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var preview = Call(session, Body, preview: true);
        var result = Call(session, Body);

        // The new event is the seventh event, and the delete of the first event moves it to index 5.
        Assert.Equal([$"{Ref}#event/5"], result.Adds);
        Assert.Equal([new ResultDelete($"{Ref}#event/0", DeleteReason.Target, null)], result.Deletes);
        Assert.Empty(result.Changes);
        Assert.Equal($"{Ref}#event/5", result.Select);
        Assert.Equal(result.Adds, preview.Adds);
        Assert.Equal(result.Deletes, preview.Deletes);
        Assert.Equal(result.Select, preview.Select);
        Assert.Equal(EditStatus.Previewed, preview.Status);
        Assert.Equal("Agent: Add START_PSYS", session.UndoLabel);
        return;

        static void Body(EditScope scope)
        {
            scope.Run(CutsceneCommands.AddEvent(scope.Session, ScriptPath, "START_PSYS", 1, ["Fx"]));
            scope.Run(CutsceneCommands.DeleteEvents(scope.Session, [Event(scope.Session, 0)]));
        }
    }

    [Fact]
    public void AnElementThatTheCallAddsAndDeletesIsNotInTheResult()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var result = Call(session, scope =>
        {
            var add = CutsceneCommands.AddEvent(scope.Session, ScriptPath, "START_PSYS", 1, ["Fx"]);
            scope.Run(add);
            scope.Run(CutsceneCommands.DeleteEvents(scope.Session, add.Added(scope.Session)));
        });

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.Empty(result.Adds);
        Assert.Empty(result.Deletes);
        Assert.Null(result.Select);
        Assert.False(session.CanUndo);
    }

    /// <summary>A move gives each event that it changes, with its index after the call. A delete of an entity gives the
    /// events that go with it as a cascade with the targets line that names the entity.</summary>
    [Fact]
    public void AMoveGivesTheChangedEventsAndAnEntityDeleteGivesItsEvents()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var moved = Call(session, scope => scope.Run(CutsceneCommands.MoveEvents(scope.Session,
            [Event(scope.Session, 1), Event(scope.Session, 2)], 0.5f, 0)));
        var deleted = Call(session, scope => scope.Run(CutsceneCommands.DeleteEntity(scope.Session,
            Entity(scope.Session, "Fx"))));

        Assert.Equal([$"{Ref}#event/1", $"{Ref}#event/2"], moved.Changes);
        Assert.Null(moved.Select);
        Assert.Equal(
        [
            new ResultDelete($"{Ref}#event/1", DeleteReason.Cascade, "targets = Fx"),
            new ResultDelete($"{Ref}#event/2", DeleteReason.Cascade, "targets = Fx"),
            new ResultDelete($"{Ref}#entity/Fx", DeleteReason.Target, null),
        ], deleted.Deletes);
    }

    /// <summary>A move of 0 and 0 changes nothing, so the call gives no change and no Undo step.</summary>
    [Fact]
    public void AMoveOfNothingGivesNoChange()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        var result = Call(session, scope => scope.Run(CutsceneCommands.MoveEvents(scope.Session, [Event(scope.Session, 1)], 0, 0)));

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.Empty(result.Changes);
    }

    /// <summary>A new script is one add and the select, also when the call changes its duration. A new duration of a
    /// script that exists is a change of the script.</summary>
    [Fact]
    public void ANewScriptIsAnAddAndANewDurationIsAChange()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        const string newRef = @"thn:DATA\MODS\act\CUTSCENES\my_intro.thn";

        var created = Call(session, scope =>
        {
            var plan = CutsceneCommands.Create(scope.Session, "my_intro");
            scope.Run(plan);
            scope.Run(CutsceneCommands.SetDuration(scope.Session, plan.Path, 8));
        });
        var duration = Call(session, scope => scope.Run(CutsceneCommands.SetDuration(scope.Session, ScriptPath, 4)));

        Assert.Equal([newRef], created.Adds);
        Assert.Empty(created.Changes);
        Assert.Equal(newRef, created.Select);
        Assert.Equal("Agent: New cutscene my_intro", created.UndoLabel);
        Assert.Equal([Ref], duration.Changes);
        Assert.Empty(duration.Adds);
        Assert.Null(duration.Select);
    }

    /// <summary>A path that a file has gives name_taken (detail PathInUse), and nothing changes.</summary>
    [Fact]
    public void ANewScriptAtAPathInUseGivesNameTaken()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Call(session, scope => scope.Run(CutsceneCommands.Create(scope.Session, "my_intro")));

        var error = Assert.Throws<EditException>(() =>
            Call(session, scope => scope.Run(CutsceneCommands.Create(scope.Session, "MY_INTRO")))).Error;

        Assert.Equal((EditErrorKind.NameTaken, "PathInUse"), (error.Kind, error.Detail));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>The commit checks give the cutscene errors that the call adds and removes, with the reference of the
    /// element of the error and the origin of the active mod (section 12.6, item 4 of the MCP1 design).</summary>
    [Fact]
    public void TheResultGivesTheCutsceneErrorsThatTheCallAddsAndRemoves()
    {
        using var ws = Workspace(script: ReferenceMonitorScript);
        var session = ws.Session("act");
        var monitor = Entity(session, "Monitor");

        var added = Call(session, scope =>
            scope.Run(CutsceneCommands.SetFlags(scope.Session, monitor, new System.Collections.Generic.HashSet<string>())));
        var removed = Call(session, scope =>
            scope.Run(CutsceneCommands.SetFlags(scope.Session, monitor,
                new System.Collections.Generic.HashSet<string> { ThnFlags.Reference })));

        var problem = Assert.Single(added.NewProblems);
        Assert.Equal($"{Ref}#event/0", problem.Ref);
        Assert.Equal((OriginKind.Mod, "act"), (problem.Origin, problem.ModId));
        Assert.Contains("SET_CAMERA needs a CAMERA there.", problem.Message);
        Assert.StartsWith(EditSession.CutsceneCrash, problem.Message);
        Assert.Empty(added.FixedProblems);
        Assert.Equal([problem.Message], removed.FixedProblems.Select(p => p.Message));
        Assert.Empty(removed.NewProblems);
        Assert.Equal([$"{Ref}#entity/Monitor"], added.Changes);
    }
}
