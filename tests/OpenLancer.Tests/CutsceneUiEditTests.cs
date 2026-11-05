using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The UI path of the discrete cutscene edits (milestone MCP1-4): the editor runs the plan of a cutscene command
/// through its command runner, and then selects as Unity does (decision D249). A paste, a duplicate and an Add event
/// select the new events, a delete selects the cutscene, and each other edit keeps the selection.
/// </summary>
/// <remarks>The Cutscene workspace needs an editor window, so the tests call its static selection rule
/// (<see cref="CutsceneWorkspace.SelectionAfter"/>) after a run through <see cref="FakeCommandHost.Runner"/>.</remarks>
public sealed class CutsceneUiEditTests
{
    /// <summary>The indices of the events of <see cref="ThnSessions.Script"/>.</summary>
    private const int Psys = 1, Motion = 3, Sound = 4;

    /// <summary>Runs a plan as the editor does, and gives the selection after it.</summary>
    private static object? RunAndSelect(EditSession session, CutscenePlan plan, object? current, string path = ScriptPath)
    {
        Assert.Equal(EditStatus.Applied, FakeCommandHost.Runner(session).Run(plan).Status);
        Assert.Equal((plan.Label, EditOrigin.User), (session.UndoLabel, session.UndoOrigin));
        return CutsceneWorkspace.SelectionAfter(plan, session, path, current);
    }

    private static ThnRef LastEvent(EditSession session) =>
        new(ScriptPath, ThnElementKind.Event, W(session).Events[^1].Id);

    [Fact]
    public void APasteADuplicateAndAnAddEventSelectTheNewEvents()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var root = new CutsceneRoot(ScriptPath);

        var clip = TimelineEdits.Copy(W(session), [Event(session, Motion), Event(session, Sound)]);
        var pasted = RunAndSelect(session, CutsceneCommands.Paste(session, ScriptPath, clip, 2.5, null), root);
        Assert.Equal(new ThnEventSet(ScriptPath, W(session).Events.TakeLast(2).Select(e => e.Id)), pasted);

        var duplicated = RunAndSelect(session, CutsceneCommands.Duplicate(session, [Event(session, Psys)]), pasted);
        Assert.Equal(LastEvent(session), duplicated);

        var added = RunAndSelect(session,
            CutsceneCommands.AddEvent(session, ScriptPath, "START_SOUND", 3, ["Voice_other"]), duplicated);
        Assert.Equal(LastEvent(session), added);
        Assert.Equal(10, W(session).Events.Count);
    }

    /// <summary>A delete of events and a delete of an entity select the cutscene. A rename and a move keep the
    /// selection, and so does an edit of another cutscene.</summary>
    [Fact]
    public void ADeleteSelectsTheCutsceneAndOtherEditsKeepTheSelection()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");
        var psys = Event(session, Psys);

        Assert.Equal(trent, RunAndSelect(session, CutsceneCommands.RenameEntity(session, trent, "Char_hero", []), trent));
        Assert.Equal(psys, RunAndSelect(session, CutsceneCommands.MoveEvents(session, [psys], 1, 0), psys));
        Assert.Equal(new CutsceneRoot(ScriptPath),
            RunAndSelect(session, CutsceneCommands.DeleteEvents(session, [psys]), psys));
        Assert.Equal(new CutsceneRoot(ScriptPath),
            RunAndSelect(session, CutsceneCommands.DeleteEntity(session, trent), trent));

        var other = CutsceneCommands.Duplicate(session, [Event(session, Psys)]);
        Assert.Equal(psys, RunAndSelect(session, other, psys, @"DATA\SCRIPTS\other.thn"));
    }

    /// <summary>The selection names the open cutscene in the form of the workspace, also when the session stores the
    /// path in another letter case.</summary>
    [Fact]
    public void TheSelectionNamesTheCutsceneInTheFormOfTheWorkspace()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var open = ScriptPath.ToLowerInvariant();

        var selected = RunAndSelect(session, CutsceneCommands.Duplicate(session, [Event(session, Psys)]), null, open);

        Assert.Equal(new ThnRef(open, ThnElementKind.Event, W(session).Events[^1].Id), selected);
        Assert.Equal(new CutsceneRoot(open),
            RunAndSelect(session, CutsceneCommands.DeleteEvents(session, [Event(session, Psys)]), selected, open));
    }

    /// <summary>
    /// A rename that other data names asks first: the dialog lists each use and what the rename does, and the plan that
    /// waited for the dialog runs as one entry. A delete of an entity that events name asks first, and its dialog lists
    /// the events of the plan.
    /// </summary>
    [Fact]
    public void TheDialogsListTheUsesOfARenameAndTheEventsOfADelete()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");
        ThnNameUse[] uses =
        [
            new(@"DATA\UNIVERSE\bar.ini", "bar.ini [Camera] name = Char_trent"),
            new(@"DATA\SCRIPTS\enter.thn", "enter.thn: the REFERENCE entity Char_trent"),
        ];

        var message = CutsceneRename.Message("Char_trent", "Char_hero", uses);
        Assert.Contains("\n  bar.ini [Camera] name = Char_trent\n  enter.thn: the REFERENCE entity Char_trent\n", message);
        Assert.EndsWith("Rename Char_trent to Char_hero in this file?", message);
        var rename = CutsceneCommands.RenameEntity(session, trent, "Char_hero", uses);
        Assert.True(rename.NeedsConfirmation);
        Assert.Equal(trent, RunAndSelect(session, rename, trent));
        session.Undo();

        var delete = CutsceneCommands.DeleteEntity(session, trent);
        Assert.True(delete.NeedsConfirmation);
        Assert.Equal(TimelineEdits.EventsNaming(ScriptPath, W(session), "Char_trent"),
            delete.Deleted.Where(e => e.Kind == ThnElementKind.Event));
    }

    /// <summary>When the editor takes no edits, the runner refuses a cutscene plan with a warning, and W does not
    /// change.</summary>
    [Fact]
    public void ACutscenePlanDoesNotRunWhileTheEditorTakesNoEdits()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var host = new FakeCommandHost(session, "The editor waits for a confirmation.");
        var before = W(session);

        var result = new CommandRunner(host).Run(CutsceneCommands.DeleteEvents(session, [Event(session, Psys)]));

        Assert.Equal((EditStatus.Refused, EditErrorKind.ReadOnly), (result.Status, Assert.Single(result.Errors).Kind));
        Assert.Equal((ConsoleSeverity.Warning, "The editor waits for a confirmation."),
            (Assert.Single(host.Log.Entries).Severity, host.Log.Entries[0].Message));
        Assert.Same(before, W(session));
    }

    /// <summary>A refused plan goes to the Console as an error with the reason, and W does not change.</summary>
    [Fact]
    public void ARefusedCutscenePlanGivesAnErrorInTheConsole()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var host = new FakeCommandHost(session);
        var before = W(session);

        var result = new CommandRunner(host).Run(CutsceneCommands.SetTarget(session, Event(session, 0), 1, "Fx"));

        Assert.Equal(EditStatus.Refused, result.Status);
        var entry = Assert.Single(host.Log.Entries);
        Assert.Equal((ConsoleSeverity.Error, "SET_CAMERA needs a CAMERA as target 2. The entity Fx is a PSYS."),
            (entry.Severity, entry.Message));
        Assert.Same(before, W(session));
    }
}
