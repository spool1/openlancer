using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The batches of the mission editor (sections 4.0.4, 4.0.5 and 4.5 of the M6b design).</summary>
public sealed class MissionBatchTests
{
    [Fact]
    public void ARefusalAtCommandKChangesNothingAndAddsNoHistoryEntry()
    {
        using var m = Create();
        var before = m.State();
        var revision = m.Session.Revision;

        var result = m.Missions.Apply(new MissionBatch("Two edits",
            [new AddTrigger(TestScript), new RemoveTrigger(T("ol_nowhere"))]));

        Assert.Equal(EditStatus.Refused, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal((EditErrorKind.NotFound, MissionErrors.TriggerNotFound, 1), (error.Kind, error.Detail, error.Index));
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
        Assert.Equal(revision, m.Session.Revision);
        Assert.Null(m.Session.OpenBatch);
    }

    [Fact]
    public void ABatchIsOneHistoryEntryAndALaterCommandSeesAnEarlierOne()
    {
        using var m = Create();
        var before = m.State();

        var result = m.Missions.Apply(new MissionBatch("Add a wave",
        [
            new AddTrigger(TestScript) { Nickname = "ol_wave_2", Condition = LineSpec.Of("Cnd_True") },
            new AddAction(T("ol_wave_2"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_end"))),
            new RenameTrigger(T("ol_wave_2"), "ol_wave_3"),
        ]));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Add a wave", m.Session.UndoLabel);
        Assert.Equal(["nickname = ol_wave_3", "Cnd_True =", "Act_ActTrig = ol_end"], m.Lines("ol_wave_3"));
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>The result of a batch is the net result of its commands (section 5.6 of the MCP1 design). Each command
    /// is a part, and each command of a group is a part too. So an item that the batch adds and then removes is not in
    /// the result. The select follows a later rename. A later remove of the selected item gives no select.</summary>
    [Fact]
    public void TheResultOfABatchIsTheNetResultOfItsCommands()
    {
        using var m = Create();
        AddTrigger New(string name) => new(TestScript) { Nickname = name, Condition = LineSpec.Of("Cnd_True") };

        var gone = m.Missions.Apply(new MissionBatch("Add and remove",
            [New("ol_probe"), new RemoveTrigger(T("ol_probe"))]));
        var grouped = m.Missions.Apply(new MissionBatch("Group",
            [new CommandGroup([New("ol_probe"), new RemoveTrigger(T("ol_probe"))])]));
        var renamed = m.Missions.Apply(new MissionBatch("Add and rename",
            [New("ol_probe"), new RenameTrigger(T("ol_probe"), "ol_probe_2")]));
        var removed = m.Missions.Apply(new MissionBatch("Rename and remove",
        [
            new RenameTrigger(T("ol_end"), "ol_x"),
            new RemoveTrigger(T("ol_x")) { References = ReferencesMode.Remove },
        ]));

        Assert.All([gone, grouped], result =>
        {
            Assert.NotEqual(EditStatus.Refused, result.Status);
            Assert.Empty(result.Adds);
            Assert.Empty(result.Deletes);
            Assert.Null(result.Select);
        });
        Assert.Equal(EditStatus.Applied, renamed.Status);
        Assert.Equal([T("ol_probe_2").ToString()], renamed.Adds);
        Assert.Empty(renamed.Renames);
        Assert.Equal(T("ol_probe_2").ToString(), renamed.Select);
        Assert.Equal(EditStatus.Applied, removed.Status);
        Assert.Equal([T("ol_end").ToString()], removed.Deletes.Select(d => d.Ref));
        Assert.Empty(removed.Renames);
        Assert.Null(removed.Select);
    }

    [Fact]
    public void ABatchWithoutAChangeGivesNoChangeAndNoEntry()
    {
        using var m = Create();

        var result = m.Missions.Apply(new MissionBatch("Nothing", [new SetTriggerStart(T("ol_start"), true)]));

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.False(m.Session.CanUndo);
        Assert.Null(result.Inverse);
    }

    [Fact]
    public void StrictModeRefusesANewErrorAndKeepsW()
    {
        using var m = Create();
        var before = m.State();

        var result = m.Missions.Apply(new MissionBatch("Start nothing",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere")))])
        {
            Options = MissionEditOptions.Strict,
        });

        Assert.Equal(EditStatus.Refused, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal((EditErrorKind.NewErrors, MissionErrors.NewErrors), (error.Kind, error.Detail));
        Assert.StartsWith("The edit adds 1 error, so the editor did not apply it. 'Start trigger' names trigger 'ol_nowhere'.",
            error.Message);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void TheDefaultModeAppliesANewErrorAndReportsIt()
    {
        using var m = Create();

        var result = m.Apply(new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere"))));

        var problem = Assert.Single(result.NewProblems, p => p.Rule == "M05");
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal(OriginKind.Mod, problem.Origin);
        Assert.Equal(Row("ol_wave", 6).Reference, problem.Ref);
        m.Session.Undo();
        var removed = m.Apply(new RemoveAction(Row("ol_wave", 3)));
        Assert.DoesNotContain(removed.NewProblems, p => p.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void RejectNewWarningsRefusesANewWarning()
    {
        using var m = Create();

        var result = m.Missions.Apply(new MissionBatch("Lonely", [new AddTrigger(TestScript) { Condition = LineSpec.Of("Cnd_True") }])
        {
            Options = new MissionEditOptions(RejectNewWarnings: true),
        });

        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.Contains("Nothing starts trigger 'ol_test_trigger_1'.", result.Errors[0].Message);
    }

    [Fact]
    public void AnExpectedVersionThatDiffersRefusesTheBatch()
    {
        using var m = Create();
        m.Apply(new SetTriggerStart(T("ol_wave"), true));

        var ok = m.Missions.Apply(new MissionBatch("A", [new SetTriggerStart(T("ol_end"), true)])
        {
            ExpectVersions = new Dictionary<string, long> { [Script.ToLowerInvariant().Replace('\\', '/')] = 2 },
        });
        var stale = m.Missions.Apply(new MissionBatch("B", [new SetTriggerStart(T("ol_end"), false)])
        {
            ExpectVersions = new Dictionary<string, long> { [Script] = 2 },
        });

        Assert.Equal(EditStatus.Applied, ok.Status);
        Assert.Equal(3, ok.Versions[Script]);
        var error = Assert.Single(stale.Errors);
        Assert.Equal((EditErrorKind.StaleRevision, MissionErrors.ScriptChanged), (error.Kind, error.Detail));
        Assert.Equal($"Script {Script} changed after you read it. Read the script again, then try again.", error.Message);
        var revision = m.Missions.Apply(new MissionBatch("C", [new SetTriggerStart(T("ol_end"), false)]) { IfRevision = 1 });
        Assert.Equal(EditSession.RevisionChanged, revision.Errors[0].Detail);
    }

    [Fact]
    public void AnAgentBatchHasTheAgentLabelAndAConfirmationForMissingLines()
    {
        using var m = Create();

        var unconfirmed = m.Missions.Apply(new MissionBatch("Remove the end", [new RemoveTrigger(T("ol_end"))])
        {
            Origin = EditOrigin.Agent,
        });
        var confirmed = m.Missions.Apply(new MissionBatch("Remove the end", [new RemoveTrigger(T("ol_end"))])
        {
            Origin = EditOrigin.Agent, Confirm = true,
        });

        Assert.Equal(EditStatus.Refused, unconfirmed.Status);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.Equal((EditErrorKind.NeedsConfirmation, MissionErrors.MissingLines),
            (unconfirmed.Errors[0].Kind, unconfirmed.Errors[0].Detail));
        Assert.Equal(EditStatus.Applied, confirmed.Status);
        Assert.Equal("Agent: Remove the end", confirmed.UndoLabel);
        Assert.Equal(EditOrigin.Agent, m.Session.UndoOrigin);
        // The studio does not ask before a delete (section 4.10 of the M6b design).
        m.Session.Undo();
        Assert.Equal(EditStatus.Applied, m.Missions.Apply(new RemoveTrigger(T("ol_end"))).Status);
    }

    [Fact]
    public void ABatchJoinsTheOpenBatchOfACallerAndItsStrictCheckRunsAtTheCommit()
    {
        using var m = Create();
        var applied = new List<MissionEditResult>();
        m.Missions.Applied += applied.Add;

        var batch = m.Session.BeginBatch("Caller");
        var first = m.Missions.Apply(new MissionBatch("Name a later trigger",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_later")))]) { Options = MissionEditOptions.Strict });
        m.Missions.Apply(new MissionBatch("Add it", [new AddTrigger(TestScript) { Nickname = "ol_later", Condition = LineSpec.Of("Cnd_True") }]));
        Assert.Empty(applied);
        var result = batch.Commit();

        Assert.Equal(EditStatus.Applied, first.Status);
        Assert.NotNull(first.Inverse);
        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(2, applied.Count);
        Assert.Equal("Caller", m.Session.UndoLabel);

        var second = m.Session.BeginBatch("Caller 2");
        m.Missions.Apply(new MissionBatch("Name nothing",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere")))]) { Options = MissionEditOptions.Strict });
        var refused = second.Commit();
        Assert.Equal(EditStatus.Refused, refused.Status);
        Assert.Equal(MissionErrors.NewErrors, refused.Errors[0].Detail);
        Assert.Equal("Caller", m.Session.UndoLabel);
    }

    [Fact]
    public void APreviewLeavesTheRevisionTheVersionsAndTheHistory()
    {
        using var m = Create();
        var before = m.State();
        var revision = m.Session.Revision;

        var result = m.Missions.Preview(new MissionBatch("Preview",
            [new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_nowhere")))]));

        Assert.Equal(EditStatus.Previewed, result.Status);
        Assert.Contains(result.NewProblems, p => p.Rule == "M05");
        Assert.NotNull(result.Inverse);
        Assert.Equal(before, m.State());
        Assert.Equal(revision, m.Session.Revision);
        Assert.Equal(1, m.Missions.Version(Script));
        Assert.False(m.Session.CanUndo);
        Assert.False(m.Session.IsDirty);
    }

    [Fact]
    public void ARefusedCommandChangesNoRevision()
    {
        using var m = Create();
        var revision = m.Session.Revision;

        m.Refuse(new RenameTrigger(T("ol_wave"), "ol_end"), MissionErrors.NameTaken);

        Assert.Equal(revision, m.Session.Revision);
        Assert.Equal(1, m.Missions.Version(Script));
    }

    [Fact]
    public void AReadOnlySessionAndAnOpenTransactionRefuse()
    {
        using var m = Create();
        using (var transaction = m.Session.Begin("Drag"))
            Assert.Equal(MissionErrors.Busy, m.Missions.Apply(new SetTriggerStart(T("ol_wave"), true)).Errors[0].Detail);
        var second = m.Ws.SecondSession("act");

        var result = second.Missions.Apply(new SetTriggerStart(T("ol_wave"), true));

        Assert.Equal((EditErrorKind.ReadOnly, MissionErrors.NotEditable), (result.Errors[0].Kind, result.Errors[0].Detail));
        Assert.StartsWith("The editor cannot change missions now. ", result.Errors[0].Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task ATransactionStepRunsNoAnalysisAndTheCommitIsOneEntry()
    {
        using var m = Create();
        m.Missions.Model(Script);
        var analyses = m.Missions.Cache.Count;

        using var transaction = m.Missions.Begin("Drag the delay");
        for (var seconds = 1; seconds <= 5; seconds++)
            Assert.Equal(EditStatus.Applied, transaction.Apply(new MissionBatch("Drag",
                [new SetConditionValue(T("ol_wave"), "seconds", seconds)])).Status);
        Assert.Equal(analyses, m.Missions.Cache.Count);
        var work = transaction.AnalysisWork();
        var analysis = Assert.Single(await System.Threading.Tasks.Task.Run(work, TestContext.Current.CancellationToken));
        Assert.Equal("Cnd_Timer = 5", MissionEdits.Line(analysis.Index.Trigger("ol_wave")!.Condition!.Line));
        var result = transaction.Commit();

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Drag the delay", m.Session.UndoLabel);
        Assert.Equal("Cnd_Timer = 5", m.Lines("ol_wave")![2]);
        m.Session.Undo();
        Assert.Equal("Cnd_Timer = 3", m.Lines("ol_wave")![2]);
    }

    [Fact]
    public void CommandsThatCancelEachOtherGiveNoChangeAndNoEntry()
    {
        using var m = Create();
        var revision = m.Session.Revision;
        var changes = new List<MissionScriptsChange>();
        m.Missions.ScriptsChanged += changes.Add;

        var result = m.Missions.Apply(new MissionBatch("Add and remove",
        [
            new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_end"))),
            new RemoveAction(Row("ol_wave", 6)),
        ]));

        Assert.Equal(EditStatus.NoChange, result.Status);
        Assert.Null(result.Inverse);
        Assert.False(m.Session.CanUndo);
        Assert.False(m.Session.IsDirty);
        Assert.Equal(revision, m.Session.Revision);
        Assert.Equal(1, m.Missions.Version(Script));
        Assert.Empty(changes);
    }

    [Fact]
    public void ARefusalBeforeTheRunCancelsTheOpenBatchOfTheCaller()
    {
        using var m = Create();
        var before = m.State();

        var batch = m.Session.BeginBatch("Caller");
        m.Missions.Apply(new SetTriggerStart(T("ol_wave"), true));
        var stale = m.Missions.Apply(new MissionBatch("Stale", [new SetTriggerStart(T("ol_end"), true)]) { IfRevision = 99 });

        Assert.Equal(EditSession.RevisionChanged, stale.Errors[0].Detail);
        Assert.False(batch.IsOpen);
        Assert.Null(m.Session.OpenBatch);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);

        var second = m.Session.BeginBatch("Caller 2");
        m.Missions.Apply(new SetTriggerStart(T("ol_wave"), true));
        var preview = m.Missions.Preview(new MissionBatch("Preview", [new SetTriggerStart(T("ol_end"), true)]));

        Assert.Equal(MissionErrors.Busy, preview.Errors[0].Detail);
        Assert.False(second.IsOpen);
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void AnAgentPreviewOfADeleteTellsTheConfirmationAndDoesNotRefuse()
    {
        using var m = Create();
        var before = m.State();

        var result = m.Missions.Preview(new MissionBatch("Remove the end", [new RemoveTrigger(T("ol_end"))])
        {
            Origin = EditOrigin.Agent,
        });

        Assert.Equal(EditStatus.Previewed, result.Status);
        Assert.True(result.NeedsConfirmation);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Missing.Count);
        Assert.Contains(result.Warnings, w => w.Message.StartsWith("After this edit, 3 lines name an item that is gone."));
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void TheConfirmationFollowsTheOriginOfTheBatchOfTheCallerAndGivesThePlan()
    {
        using var m = Create();
        var before = m.State();

        // The step does not set an origin. The batch of the caller is an agent batch.
        var batch = m.Session.BeginBatch("Agent call", EditOrigin.Agent);
        var result = m.Missions.Apply(new RemoveTrigger(T("ol_end")));

        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.True(result.NeedsConfirmation);
        Assert.Equal((EditErrorKind.NeedsConfirmation, MissionErrors.MissingLines), (result.Errors[0].Kind, result.Errors[0].Detail));
        Assert.Single(result.Deletes);
        Assert.Equal(3, result.Missing.Count);
        Assert.False(batch.IsOpen);
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void StrictModeCountsANewLineThatNamesADeletedName()
    {
        using var m = Create();

        var refused = m.Missions.Apply(new MissionBatch("Remove and name",
        [
            new RemoveTrigger(T("ol_end")),
            new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_end"))),
        ]) { Options = MissionEditOptions.Strict });
        // The lines that named the trigger before the batch are "Missing" lines, so strict mode does not count them.
        var applied = m.Missions.Apply(new MissionBatch("Remove", [new RemoveTrigger(T("ol_end"))]) { Options = MissionEditOptions.Strict });

        Assert.Equal(EditStatus.Refused, refused.Status);
        Assert.Equal(MissionErrors.NewErrors, refused.Errors[0].Detail);
        Assert.StartsWith("The edit adds 1 error, so the editor did not apply it.", refused.Errors[0].Message);
        Assert.Equal(EditStatus.Applied, applied.Status);
    }

    [Fact]
    public void RejectNewWarningsCountsTheWarningsAsWarnings()
    {
        using var m = Create();

        var result = m.Missions.Apply(new MissionBatch("Lonely", [new AddTrigger(TestScript) { Condition = LineSpec.Of("Cnd_True") }])
        {
            Options = new MissionEditOptions(RejectNewWarnings: true),
        });

        Assert.StartsWith("The edit adds 1 warning, so the editor did not apply it.", result.Errors[0].Message);
    }

    [Fact]
    public void TheKeysOfTheExpectedVersionsAreInstallPathsOfScripts()
    {
        using var m = Create();

        var bad = m.Missions.Apply(new MissionBatch("A", [new SetTriggerStart(T("ol_end"), true)])
        {
            ExpectVersions = new Dictionary<string, long> { [@"C:\x.ini"] = 1 },
        });
        var unknown = m.Missions.Apply(new MissionBatch("B", [new SetTriggerStart(T("ol_end"), true)])
        {
            ExpectVersions = new Dictionary<string, long> { [@"DATA\MISSIONS\OL_NONE\none.ini"] = 1 },
        });
        var model = Assert.Throws<EditException>(() => m.Missions.Model(@"C:\x"));

        Assert.Equal((EditErrorKind.InvalidArgument, InstallPaths.NotInstallPath), (bad.Errors[0].Kind, bad.Errors[0].Detail));
        Assert.Equal((EditErrorKind.NotFound, MissionErrors.ScriptNotFound), (unknown.Errors[0].Kind, unknown.Errors[0].Detail));
        Assert.Equal(EditErrorKind.InvalidArgument, model.Error.Kind);
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void ATransactionKeepsTheGuardsAndTheConfirmationsAndItsCommitGivesTheInverse()
    {
        using var m = Create();
        var before = m.State();
        var shared = ItemRef.Of(SharedShips, "NPCShipArch", "ol_shared_build");

        using var transaction = m.Missions.Begin("Drag the delay");
        var stale = transaction.Apply(new MissionBatch("Drag", [new SetConditionValue(T("ol_wave"), "seconds", 4)]) { IfRevision = 9 });
        var shared1 = transaction.Apply(new MissionBatch("Drag",
            [new SetItemKey(shared, "loadout", [LineSpec.Raw("loadout", "ol_loadout_2")])]));
        Assert.Equal(EditSession.RevisionChanged, stale.Errors[0].Detail);
        Assert.True(shared1.NeedsConfirmation);
        Assert.Equal(MissionErrors.SharedBuildChange, shared1.Errors[0].Detail);
        Assert.Equal(before, m.State());
        transaction.Apply(new MissionBatch("Drag", [new SetConditionValue(T("ol_wave"), "seconds", 7)]));
        var result = transaction.Commit();

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.NotNull(result.Inverse);
        m.Apply(result.Inverse!);
        Assert.Equal(before, m.State());
    }
}
