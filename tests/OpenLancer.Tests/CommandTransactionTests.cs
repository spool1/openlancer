using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A gesture of the command layer with many steps (section 5.2, item 5 of the MCP1 design). Each step puts back the
/// state before the gesture and makes its plan then. The gesture keeps only its last step, and the commit is one
/// entry.
/// </summary>
public sealed class CommandTransactionTests
{
    private const string DepotStart = "-38675, 0, -27705";
    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");

    [Fact]
    public void OnlyTheLastStepStays()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        using var gesture = CommandTransaction.Begin(session, "Move Li01_depot");

        foreach (var x in new[] { 10, 20, 30 })
        {
            var result = gesture.Apply(_ => SetPos(x));
            Assert.Equal(EditStatus.Applied, result.Status);
            Assert.Equal([$"{x}, 0, 0"], Lines(session, "pos"));
        }

        Assert.False(session.CanUndo);
        var commit = gesture.Commit();
        Assert.Equal(EditStatus.Applied, commit.Status);
        Assert.Equal("Move Li01_depot", commit.UndoLabel);
        Assert.Equal("Move Li01_depot", session.UndoLabel);
        Assert.False(gesture.IsOpen);

        session.Undo();
        Assert.Equal([DepotStart], Lines(session, "pos"));
        Assert.False(session.CanUndo);
    }

    /// <summary>A plan that refuses, a plan that throws and a command that throws each leave the state before the
    /// gesture, not the state of the step before. The gesture stays open, and the result tells why without the
    /// parameter text of .NET.</summary>
    [Fact]
    public void ARefusedStepLeavesTheStartStateAndTheGestureStaysOpen()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var document = session.OpenDocument(SystemWorkspace.Li01Path)!;
        var before = document.State;
        using var gesture = CommandTransaction.Begin(session, "Move Li01_depot");

        foreach (var (step, message) in new (Func<EditSession, EditPlan> Step, string Message)[]
                 {
                     (_ => CommandPlan.Refused("Move Li01_depot", "The plan refuses."), "The plan refuses."),
                     (_ => throw new ArgumentException("The value is not valid.", "value"), "The value is not valid."),
                     (_ => new CommandPlan(EditCommand.SetLines(Depot, "nickname", [[new StringValue("x")]])),
                         "'nickname' is the key of [Object] nickname = Li01_depot. Use Rename to change it."),
                     (_ => throw new EditException(new EditError(EditErrorKind.InvalidValue, "Detail", "The edit refuses.")),
                         "The edit refuses."),
                 })
        {
            Assert.Equal(EditStatus.Applied, gesture.Apply(_ => SetPos(10)).Status);
            var refused = gesture.Apply(step);

            Assert.Equal(EditStatus.Refused, refused.Status);
            Assert.Equal(message, Assert.Single(refused.Errors).Message);
            Assert.Same(before, document.State);
            Assert.True(gesture.IsOpen);
        }

        gesture.Apply(_ => SetPos(40));
        gesture.Commit();
        Assert.Equal(["40, 0, 0"], Lines(session, "pos"));
        Assert.Equal("Move Li01_depot", session.UndoLabel);
    }

    [Fact]
    public void ResetPutsBackTheStartState()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var document = session.OpenDocument(SystemWorkspace.Li01Path)!;
        var before = document.State;
        using var gesture = CommandTransaction.Begin(session, "Move Li01_depot");

        gesture.Apply(_ => SetPos(10));
        gesture.Reset();
        Assert.Same(before, document.State);
        Assert.True(gesture.IsOpen);

        var commit = gesture.Commit();
        Assert.Equal(EditStatus.NoChange, commit.Status);
        Assert.Null(commit.UndoLabel);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    /// <summary>Each step raises one change, so the views show it. The commit raises one change and adds one entry with
    /// the label of the gesture, which can change until the commit.</summary>
    [Fact]
    public void ACommitAddsOneEntryWithTheLabelAndRaisesOneChange()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;
        var revision = session.Revision;
        using var gesture = CommandTransaction.Begin(session, "Move Li01_depot");

        gesture.Apply(_ => SetPos(10));
        gesture.Apply(_ => SetPos(20));
        Assert.Equal(2, changes.Count);
        Assert.Equal(revision, session.Revision);
        gesture.Label = "Move the depot";
        var commit = gesture.Commit();

        Assert.Equal(3, changes.Count);
        Assert.Equal([Depot], changes[^1].Sections);
        Assert.Equal(EditStatus.Applied, commit.Status);
        Assert.Equal("Move the depot", commit.UndoLabel);
        Assert.Equal(["obj:Li01_depot"], commit.Changes);
        Assert.Equal(revision + 1, commit.Revision);
        Assert.Equal("Move the depot", session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.Throws<InvalidOperationException>(() => gesture.Apply(_ => SetPos(30)));
        Assert.Throws<InvalidOperationException>(() => gesture.Commit());
    }

    /// <summary>A step that runs gives the status, the label of its plan and the revision, without the report of the
    /// plan. A drag runs a step in each frame, and the report can read the names of all systems again (review finding
    /// of MCP1-2 step 1). The commit gives the report of the last step.</summary>
    [Fact]
    public void OnlyTheCommitGivesTheReportOfThePlan()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        using var gesture = CommandTransaction.Begin(session, "Move Li01_depot");

        var step = gesture.Apply(_ => SetPos(10));

        Assert.Equal(EditStatus.Applied, step.Status);
        Assert.Equal("Set pos", step.Label);
        Assert.Equal(session.Revision, step.Revision);
        Assert.Empty(step.Changes);
        var commit = gesture.Commit();
        Assert.Equal(["obj:Li01_depot"], commit.Changes);
    }

    /// <summary>A cancel after a step puts back the exact states and raises one change with the undo change, so the
    /// views go back. A cancel without a step raises nothing. A dispose without a commit cancels.</summary>
    [Fact]
    public void ACancelPutsBackTheExactStates()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var document = session.OpenDocument(SystemWorkspace.Li01Path)!;
        var before = document.State;
        var changes = new List<EditChange>();
        session.Changed += changes.Add;
        var revision = session.Revision;

        var gesture = CommandTransaction.Begin(session, "Move Li01_depot");
        gesture.Apply(_ => SetPos(10));
        gesture.Cancel();
        Assert.Same(before, document.State);
        Assert.Equal(2, changes.Count);
        Assert.Equal([Depot], changes[1].Sections);
        Assert.False(gesture.IsOpen);
        gesture.Cancel();

        using (CommandTransaction.Begin(session, "Nothing"))
        {
        }

        Assert.Equal(2, changes.Count);
        using (var disposed = CommandTransaction.Begin(session, "Move Li01_depot"))
            disposed.Apply(_ => SetPos(20));
        Assert.Same(before, document.State);
        Assert.Equal(revision, session.Revision);
        Assert.False(session.CanUndo);
        Assert.False(session.InTransaction);
    }

    /// <summary>
    /// A step makes its plan after the restore of the state before the gesture, also when the plan reads a cache. A
    /// create takes the first free nickname of the system. The second step takes the same nickname, because the first
    /// step is gone, also after a read of the names between the steps (review finding R2 of MCP1-2).
    /// </summary>
    [Fact]
    public void APlanOfAStepReadsTheStartStateAlsoThroughACache()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        using var gesture = CommandTransaction.Begin(session, "Create");

        var plans = new List<EditPlan>();
        EditPlan Create(EditSession s)
        {
            plans.Add(ScenePlans.Create(s, "li01", SceneTemplate.Zone, null, Vector3.Zero, null));
            return plans[^1];
        }

        Assert.Equal(EditStatus.Applied, gesture.Apply(Create).Status);
        var added = Assert.Single(plans[0].Adds);
        Assert.NotNull(session.Nicknames.Taken(added.Id.Key!));
        Assert.Equal(EditStatus.Applied, gesture.Apply(Create).Status);

        Assert.Equal([added], plans[1].Adds);
        var commit = gesture.Commit();
        Assert.Equal(["zone:" + added.Id.Key], commit.Adds);
        Assert.Single(session.Sections(SystemWorkspace.Li01Path), s => s.Id.Key == added.Id.Key);
    }

    [Fact]
    public void AGestureCannotOpenWhileATransactionIsOpenOrTheSessionTakesNoEdits()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        using (CommandTransaction.Begin(session, "First"))
            Assert.Throws<InvalidOperationException>(() => CommandTransaction.Begin(session, "Second"));
        using (session.BeginBatch())
            Assert.Throws<InvalidOperationException>(() => CommandTransaction.Begin(session, "Second"));
        session.Dispose();
        Assert.Throws<InvalidOperationException>(() => CommandTransaction.Begin(session, "Closed"));
    }

    [Fact]
    public void APlanCanRunOnlyWithASessionCommand()
    {
        Assert.True(new CommandPlan(EditCommand.Group("Nothing", [])).CanRun);
        var refused = CommandPlan.Refused("Nothing", "The plan refuses.", Depot);
        Assert.False(refused.CanRun);
        Assert.Equal(Depot, Assert.Single(refused.Refusals).Target);
    }

    private static CommandPlan SetPos(int x) =>
        new(EditCommand.SetLinesFromText(Depot, "pos", [$"{x}, 0, 0"]), [Depot]);

    private static string[] Lines(EditSession session, string key) =>
        session.Get(Depot)!.Lines(key).Select(l => string.Join(", ", l.Values.Select(v => v.ToString()))).ToArray();
}
