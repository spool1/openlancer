using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The one runner of the editor (section 5.2, item 5 of the MCP1 design): a plan of a gesture of the user runs in a
/// batch with the edit origin User, and the Console gets each refusal and each warning with the source "Edit".
/// </summary>
public sealed class CommandRunnerTests
{
    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");

    [Fact]
    public void APlanRunsAsOneEntryWithTheOriginUser()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var host = new FakeCommandHost(session);
        var runner = new CommandRunner(host);

        var result = runner.Run(SetPos(10));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Set pos", result.UndoLabel);
        Assert.Equal(["obj:Li01_depot"], result.Changes);
        Assert.Equal("Set pos", session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.Null(session.OpenBatch);
        Assert.Empty(host.Log.Entries);
    }

    /// <summary>Each refusal of a plan goes to the Console as an error with its target. Nothing changes.</summary>
    [Fact]
    public void ARefusalGoesToTheConsoleAsAnError()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var host = new FakeCommandHost(session);

        var result = new CommandRunner(host).Run(CommandPlan.Refused("Move Li01_depot", "The plan refuses.", Depot));

        Assert.Equal(EditStatus.Refused, result.Status);
        var entry = Assert.Single(host.Log.Entries);
        Assert.Equal(ConsoleSeverity.Error, entry.Severity);
        Assert.Equal(EditController.Source, entry.Source);
        Assert.Equal("The plan refuses.", entry.Message);
        Assert.Equal(Depot, entry.Target);
        Assert.False(session.CanUndo);
    }

    /// <summary>When the editor takes no edits, the runner gives a warning with the reason and runs nothing. A gesture
    /// throws with the reason, as the session does.</summary>
    [Fact]
    public void AReasonOfTheEditorGivesAWarningAndNothingRuns()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var host = new FakeCommandHost(session, "The editor waits for a confirmation.");
        var runner = new CommandRunner(host);

        var result = runner.Run(SetPos(10));
        var made = runner.Run(_ => throw new InvalidOperationException("The plan is not made."));

        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.Equal(EditErrorKind.ReadOnly, Assert.Single(result.Errors).Kind);
        Assert.Equal(EditStatus.Refused, made.Status);
        Assert.All(host.Log.Entries, e => Assert.Equal((ConsoleSeverity.Warning, "The editor waits for a confirmation."),
            (e.Severity, e.Message)));
        Assert.Equal(2, host.Log.Entries.Count);
        Assert.False(session.CanUndo);
        var error = Assert.Throws<InvalidOperationException>(() => runner.Gesture("Move Li01_depot"));
        Assert.Equal("The editor waits for a confirmation.", error.Message);
        Assert.False(session.InTransaction);
        Assert.False(runner.Execute(EditCommand.SetLinesFromText(Depot, "pos", ["1, 2, 3"])));
        Assert.Null(runner.Apply(new Data.Missions.Editing.MissionBatch("Nothing", [])));
        Assert.False(session.CanUndo);
    }

    /// <summary>The warnings of a plan go to the Console after the edit.</summary>
    [Fact]
    public void TheWarningsOfAPlanGoToTheConsole()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var host = new FakeCommandHost(session);
        var plan = SetPos(10);
        plan.Warnings = [Diagnostic.Warning(SystemWorkspace.Li01Path, "The depot moves far.") with { Target = Depot }];

        new CommandRunner(host).Run(plan);

        var entry = Assert.Single(host.Log.Entries);
        Assert.Equal((ConsoleSeverity.Warning, EditController.Source, "The depot moves far.", (SectionRef?)Depot),
            (entry.Severity, entry.Source, entry.Message, entry.Target));
        Assert.Equal("Set pos", session.UndoLabel);
    }

    /// <summary>A plan that cannot be made and a command that does not apply give an error without the parameter text
    /// of .NET. Nothing changes, and no batch stays open.</summary>
    [Fact]
    public void APlanThatCannotBeMadeOrRunGivesAnError()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var host = new FakeCommandHost(session);
        var runner = new CommandRunner(host);

        var made = runner.Run(_ => throw new ArgumentException("The value is not valid.", "value"));
        var run = runner.Run(new CommandPlan(EditCommand.SetLinesFromText(Depot, "nickname", ["x"])));

        Assert.Equal(EditStatus.Refused, made.Status);
        Assert.Equal(EditStatus.Refused, run.Status);
        Assert.Equal(
            ["The value is not valid.", "'nickname' is the key of [Object] nickname = Li01_depot. Use Rename to change it."],
            host.Log.Entries.Select(e => e.Message).ToArray());
        Assert.All(host.Log.Entries, e => Assert.Equal(ConsoleSeverity.Error, e.Severity));
        Assert.False(session.CanUndo);
        Assert.Null(session.OpenBatch);
    }

    /// <summary>A gesture of the runner is a gesture of the command layer with the label.</summary>
    [Fact]
    public void AGestureOfTheRunnerIsOneEntry()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var runner = FakeCommandHost.Runner(session);

        using (var gesture = runner.Gesture("Move Li01_depot"))
        {
            gesture.Apply(_ => SetPos(10));
            gesture.Apply(_ => SetPos(20));
            gesture.Commit();
        }

        Assert.Equal("Move Li01_depot", session.UndoLabel);
        session.Undo();
        Assert.False(session.CanUndo);
        Assert.Throws<InvalidOperationException>(() => FakeCommandHost.Runner(null).Gesture("Nothing"));
    }

    /// <summary>Without a profile, nothing runs and the Console stays empty, as before the command layer.</summary>
    [Fact]
    public void WithoutAProfileNothingRuns()
    {
        var host = new FakeCommandHost(null);
        var runner = new CommandRunner(host);

        Assert.Equal(EditStatus.Refused, runner.Run(CommandPlan.Refused("Nothing", "No.")).Status);
        Assert.False(runner.Execute(EditCommand.SetLinesFromText(Depot, "pos", ["1, 2, 3"])));
        Assert.Empty(host.Log.Entries);
    }

    private static CommandPlan SetPos(int x) =>
        new(EditCommand.SetLinesFromText(Depot, "pos", [$"{x}, 0, 0"]), [Depot]);
}
