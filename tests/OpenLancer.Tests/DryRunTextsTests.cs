using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The UI texts of the dry run (section 8 of the M6c design) pass the STE lint of the M6a tests: the sentence
/// length, no semicolons and no contractions (section 12.2 of the M6c design).</summary>
public sealed class DryRunTextsTests
{
    private static IEnumerable<(string Name, string Text)> Texts()
    {
        yield return ("toggle", MissionView.DryRunTip);
        yield return ("node menu", GraphView.RunThisTip);
        yield return ("not waiting", GraphView.NotWaitingReason);
        yield return ("empty", RunPanel.EmptyText);
        yield return ("help", RunPanel.Help.WhatItIs);
        yield return ("help writes", RunPanel.Help.WhatItWrites);
        yield return ("page help", MissionPageModel.RunHelp.WhatItIs);
        foreach (var (name, text) in new[]
                 {
                     ("start", RunPanel.StartTip), ("load", RunPanel.LoadTip), ("run", RunPanel.RunTip),
                     ("heartbeat", RunPanel.HeartbeatTip), ("next timer", RunPanel.NextTimerTip), ("back", RunPanel.BackTip),
                     ("reset", RunPanel.ResetTip), ("timers", RunPanel.TimersTip), ("player", RunPanel.PlayerTip),
                     ("system", RunPanel.SystemTip), ("space", RunPanel.SpaceTip), ("waiting", RunPanel.WaitingTip),
                     ("happens", RunPanel.HappensTip), ("is true", RunPanel.IsTrueTip), ("cause", RunPanel.CauseTip),
                     ("follow", RunPanel.FollowTip), ("log", RunPanel.LogTip), ("over tip", RunPanel.OverTip),
                     ("idle fact", RunPanel.IdleFactNote), ("over", DryRun.OverText), ("no wait", DryRun.NoWaitText),
                     ("waits", RunOverlay.WaitsTip), ("ran", RunOverlay.RanTip), ("has run", RunOverlay.HasRunTip),
                     ("stopped", RunOverlay.StoppedTip), ("starts next", RunOverlay.StartsNextTip),
                     ("stall", RunOverlay.StallTip(12)), ("frame", RunOverlay.FrameChip(2).Tooltip),
                     ("chain", TriggerMachine.ChainMessage("ol_trigger")),
                 })
            yield return (name, text);
    }

    /// <summary>"Run this trigger (dry run)" of the node menu gives the state of the run or of the trigger as the
    /// reason why it is off, not its tooltip (#56).</summary>
    [Fact]
    public void RunThisTriggerTellsWhyItIsOff()
    {
        Assert.Equal(RunPanel.OverTip, GraphView.RunThisReason(true, TriggerStatus.Active));
        Assert.Equal(GraphView.NotWaitingReason, GraphView.RunThisReason(false, TriggerStatus.Inactive));
        Assert.Equal(GraphView.NotWaitingReason, GraphView.RunThisReason(false, TriggerStatus.Complete));
        Assert.Null(GraphView.RunThisReason(false, TriggerStatus.Active));
        Assert.StartsWith("This trigger does not wait now.", GraphView.NotWaitingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void EachTextPassesTheSteLint()
    {
        var problems = Texts().SelectMany(t => SteLint.Problems(t.Text).Select(p => $"{t.Name}: {p}")).ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void TheLogRowsHaveTheTextsOfTheDesign()
    {
        var trace = new MissionTrace();
        trace.Append(new TraceStep(1, "Start mission",
        [
            new TraceChange(TraceChangeKind.Ran, "t_a"), new TraceChange(TraceChangeKind.Started, "t_b"),
            new TraceChange(TraceChangeKind.Stopped, "t_c"), new TraceChange(TraceChangeKind.Ignored, "t_a") { Name = "t_x" },
            new TraceChange(TraceChangeKind.Checkpoint, "t_a") { Name = "t_r" },
            new TraceChange(TraceChangeKind.Note, "") { Text = "No trigger waits for this event." },
        ]));

        Assert.Equal("Step 1: Start mission", trace.Steps[0].Title);
        Assert.Equal(["Ran t_a", "Started t_b", "Stopped t_c", "Ignored: no trigger 't_x'", "Checkpoint: t_r",
            "No trigger waits for this event."], trace.Steps[0].Changes.Select(c => c.LogText));
        Assert.All(trace.Steps[0].Changes.Select(c => c.LogText!), text => Assert.Empty(SteLint.Problems(text)));
    }

    /// <summary>A trigger of a prefab instance shows the name of the prefab first in each row of the Run log: the
    /// changes, the notes and the step causes (UI rule 5). A nickname that is a part of a word of the row does not
    /// change the word.</summary>
    [Fact]
    public void TheLogRowsOfAPrefabTriggerShowThePrefabName()
    {
        // "ar" is a part of "Started". The Spawn wave prefab instance of the graph test data holds the trigger.
        var analysis = GraphTestData.Analysis(GraphTestData.Script.Replace("t_spawn", "ar"));
        var trace = DryRun.Fold(analysis.Index, [DryRunInput.StartMission(), DryRunInput.Heartbeats(1),
            DryRunInput.RunTrigger("ar")]).Trace;
        Assert.NotNull(analysis.Prefabs?.Of(analysis.Index.Trigger("ar")!));

        var rows = RunPanel.LogRows(analysis, trace).Select(r => r.Text).ToList();

        Assert.Contains("Ran Spawn wave: ar", rows);
        Assert.Contains("Step 3: Run trigger Spawn wave: ar", rows);
        Assert.Contains("Not active: Spawn wave: ar", rows);
        Assert.DoesNotContain(rows, r => r.Contains("StSpawn", System.StringComparison.Ordinal));
        Assert.Equal("Ran Spawn wave: ar", MissionPageModel.RunSection(trace, analysis.Index.Trigger("ar")!, analysis)
            .Fields.Single(f => f.Label == "Last change").Value);
    }
}
