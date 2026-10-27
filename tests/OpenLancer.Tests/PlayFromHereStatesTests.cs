using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The Play buttons of the trigger cards (note of 2026-10-27 in section 8.1 of the M6c design): which cards show the
/// button, the pass for each model and place choice, the stale states, the disabled node menu item with its reason, and
/// the tooltips. The script is the synthetic script of the start plan tests, and all names are invented.
/// </summary>
public sealed class PlayFromHereStatesTests
{
    private const string OtherPath = @"DATA\MISSIONS\TEST\other.ini";

    /// <summary>A stop on another branch removes s_mid from the path to s_chosen.</summary>
    private const string StoppedBranch = """
        [Trigger]
        nickname = s_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = s_branch
        Act_ActTrig = s_mid

        [Trigger]
        nickname = s_branch
        Cnd_True = no_params
        Act_DeactTrig = s_mid

        [Trigger]
        nickname = s_mid
        Cnd_Timer = 3
        Act_ActTrig = s_chosen

        [Trigger]
        nickname = s_chosen
        Cnd_Timer = 3
        """;

    private static readonly PlayStartSetting Suggested = PlayStartSetting.Default;

    /// <summary>An analysis of a script text: a new index for each call, as after each edit.</summary>
    private static MissionAnalysis Analysis(string text, StorySlot? slot, string path = Path) =>
        new(new MissionScriptInfo(path, slot, MissionScriptState.BaseGame, null, false), Encoding.ASCII.GetBytes(text),
            Document(text, path), Index(text), [], null);

    /// <summary>A model whose passes run at once on the test thread.</summary>
    private static PlayFromHereStates AtOnce() => new(pass => Task.FromResult(pass()));

    private static TriggerStart StateOf(PlayFromHereStates states, MissionAnalysis analysis, string trigger) =>
        states.Of(analysis.Index.Trigger(trigger)!)!;

    [Fact]
    public void ThePassGivesEachTriggerTheStateOfItsPlan()
    {
        var index = Index(StartPlanTests.Script);

        var states = PlayFromHereStates.Make(index, StartPlanTests.Slot, Suggested, CancellationToken.None);

        Assert.Equal(index.Triggers.Count, states.Count);
        foreach (var trigger in index.Triggers)
        {
            var plan = StartPlan.Make(index, StartPlanTests.Slot, MissionStartPoint.At(trigger.Nickname), checkSetup: false);
            var state = states[trigger.Nickname.ToUpperInvariant()];
            Assert.Equal((plan.CanStart, plan.Place, plan.Reason), (state.CanStart, state.Place, state.Reason));
        }

        Assert.All(states.Values, s => Assert.True(s.CanStart, s.Reason));
    }

    [Fact]
    public void ATriggerOnAStoppedBranchShowsNoButtonAndTheMenuItemGivesTheReason()
    {
        var analysis = Analysis(StoppedBranch, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var chosen = analysis.Index.Trigger("s_chosen")!;

        var state = states.Of(chosen)!;

        Assert.False(state.CanStart);
        Assert.False(PlayFromHereStates.Shows(graph.NodeOf(chosen)!, false, state, null));
        Assert.False(PlayFromHereStates.CanPlay(state, null));
        Assert.Equal(StartPlan.RefusalPrefix + "Trigger 's_mid' on the path to 's_chosen' is not active at that point, " +
                     "because another trigger stops it first. Pick another trigger.",
            PlayFromHereStates.Tooltip(state, null, false));
        Assert.True(PlayFromHereStates.Shows(graph.NodeOf(analysis.Index.Trigger("s_mid")!)!, false,
            StateOf(states, analysis, "s_mid"), null));
    }

    [Fact]
    public void AScriptWithoutAStorySlotShowsNoButtonAndKeepsNoSpace()
    {
        var analysis = Analysis(StartPlanTests.Script, null);
        var states = AtOnce();

        states.Sync(analysis, Suggested, false);
        var state = StateOf(states, analysis, "t_chosen");

        Assert.False(states.KeepsSpace);
        Assert.False(state.CanStart);
        Assert.Equal(StartPlan.NoSlotReason, state.Reason);
        Assert.Equal(StartPlan.RefusalPrefix + StartPlan.NoSlotReason, PlayFromHereStates.Tooltip(state, null, false));
        states.Sync(Analysis(StartPlanTests.Script, StartPlanTests.Slot), Suggested, false);
        Assert.True(states.KeepsSpace);
    }

    [Fact]
    public void AStartPlaceProblemHidesTheButtonAndTurnsTheMenuItemOff()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, new PlayStartSetting("", "camera"), false);
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var trigger = analysis.Index.Trigger("t_chosen")!;
        var state = states.Of(trigger)!;

        Assert.True(state.CanStart);
        Assert.Equal(StartPlaceKind.Camera, state.Place.Kind);
        Assert.True(PlayFromHereStates.Shows(graph.NodeOf(trigger)!, false, state, null));
        Assert.False(PlayFromHereStates.Shows(graph.NodeOf(trigger)!, false, state, MissionPlay.NoCameraSystem));
        Assert.False(PlayFromHereStates.CanPlay(state, MissionPlay.NoCameraSystem));
        Assert.Equal(StartPlan.RefusalPrefix + MissionPlay.NoCameraSystem,
            PlayFromHereStates.Tooltip(state, MissionPlay.NoCameraSystem, false));
    }

    [Fact]
    public void ADuplicateTriggerShowsNoButtonAndGivesTheDuplicateReason()
    {
        var text = StartPlanTests.Script + """


            [Trigger]
            nickname = t_chosen
            Cnd_Timer = 9
            """;
        var analysis = Analysis(text, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var duplicate = analysis.Index.Triggers.Single(t => t.IsDuplicate);

        var state = states.Of(duplicate)!;

        Assert.Equal((false, PlayFromHereStates.DuplicateReason), (state.CanStart, state.Reason));
        Assert.Equal(StartPlan.RefusalPrefix + PlayFromHereStates.DuplicateReason, PlayFromHereStates.Tooltip(state, null, false));
        Assert.False(PlayFromHereStates.Shows(graph.NodeOf(duplicate)!, false, state, null));
        Assert.True(StateOf(states, analysis, "t_chosen").CanStart);
    }

    [Fact]
    public void OnlyATriggerCardOutsidePrefabModeShowsTheButton()
    {
        var text = StartPlanTests.Script.Replace("Act_ActTrig = t_accept", "Act_ActTrig = t_accept\nAct_ActTrig = t_none");
        var analysis = Analysis(text, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));
        var trigger = graph.NodeOf(analysis.Index.Trigger("t_chosen")!)!;
        var state = states.Of(trigger.Trigger!);

        Assert.True(PlayFromHereStates.Shows(trigger, false, state, null));
        Assert.False(PlayFromHereStates.Shows(trigger, true, state, null));
        Assert.False(PlayFromHereStates.Shows(trigger, false, null, null));
        var missing = Assert.Single(graph.Nodes, n => n.Kind == GraphNodeKind.Missing);
        Assert.False(PlayFromHereStates.Shows(missing, false, state, null));
        var frames = graph.Frames.Select(f => f.Node).ToList();
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.False(PlayFromHereStates.Shows(f, false, state, null)));
    }

    [Fact]
    public void OnePassStartsForEachScriptModelAndPlaceChoice()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var states = AtOnce();

        for (var i = 0; i < 10; i++)
            states.Sync(analysis, Suggested, false);
        Assert.Equal(1, states.Passes);

        // The trigger of the play start is not in the key: no plan of a trigger reads it.
        states.Sync(analysis, Suggested with { Trigger = "t_kill" }, false);
        Assert.Equal(1, states.Passes);

        states.Sync(analysis, new PlayStartSetting("", "camera"), false);
        Assert.Equal(2, states.Passes);

        var edited = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(edited, new PlayStartSetting("", "camera"), false);
        Assert.Equal(3, states.Passes);

        states.Sync(Analysis(StartPlanTests.Script, StartPlanTests.Slot, OtherPath), Suggested, false);
        Assert.Equal(4, states.Passes);

        // A drag or a typed text starts no pass. The pass starts at the end of the transaction.
        var dragged = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(dragged, Suggested, true);
        Assert.Equal(4, states.Passes);
        states.Sync(dragged, Suggested, false);
        Assert.Equal(5, states.Passes);
    }

    [Fact]
    public void ANewKeyCancelsTheRunningPassAndALatePassChangesNothing()
    {
        var held = new HeldPasses();
        var states = new PlayFromHereStates(held.Run);
        var first = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var second = Analysis(StoppedBranch, StartPlanTests.Slot);

        states.Sync(first, Suggested, false);
        states.Sync(second, Suggested, false);

        Assert.Throws<OperationCanceledException>(() => held.Passes[0].Make());
        var old = new PlayFromHereStates.Pass(Path, first.Index, Suggested.Place,
            new Dictionary<string, TriggerStart>(StringComparer.OrdinalIgnoreCase)
            {
                ["s_chosen"] = new(true, StartPlace.Camera(""), "old", null),
            });
        held.Passes[0].Done.SetResult(old);
        states.Sync(second, Suggested, false);
        Assert.Null(states.Of(second.Index.Trigger("s_chosen")!));

        held.End(1);
        states.Sync(second, Suggested, false);
        Assert.False(StateOf(states, second, "s_chosen").CanStart);
    }

    [Fact]
    public void TheLastStatesStayUntilTheNextPassOfTheScriptEnds()
    {
        var held = new HeldPasses();
        var states = new PlayFromHereStates(held.Run);
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(analysis, Suggested, false);
        Assert.Null(states.Of(analysis.Index.Trigger("t_chosen")!));
        held.End(0);
        states.Sync(analysis, Suggested, false);
        var before = StateOf(states, analysis, "t_chosen");

        // An edit gives a new model. The states of the last pass stay while the next pass runs.
        var edited = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(edited, Suggested, false);
        Assert.Same(before, StateOf(states, edited, "t_chosen"));
        held.End(1);
        states.Sync(edited, Suggested, false);
        Assert.NotSame(before, StateOf(states, edited, "t_chosen"));

        // Another script drops the states at once, and Clear forgets them.
        var other = Analysis(StartPlanTests.Script, StartPlanTests.Slot, OtherPath);
        states.Sync(other, Suggested, false);
        Assert.Null(states.Of(other.Index.Trigger("t_chosen")!));
        held.End(2);
        states.Sync(other, Suggested, false);
        Assert.NotNull(states.Of(other.Index.Trigger("t_chosen")!));
        states.Clear();
        Assert.Null(states.Of(other.Index.Trigger("t_chosen")!));
        Assert.False(states.KeepsSpace);
    }

    [Fact]
    public void TheTooltipNamesTheStartThePlaceAndItsSource()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        string[] Lines(string trigger, bool playRuns = false) =>
            PlayFromHereStates.Tooltip(StateOf(states, analysis, trigger), null, playRuns).Split('\n');

        Assert.Equal([GraphView.PlayFromHereTip, "Start: Mission start (t_start). The start is exact.",
            "Start place: Suggested: At the Scene camera", "Place source: No line of the path gives a place."], Lines("t_start"));

        var checkpoint = Lines("t_restore");
        Assert.Equal("Start: Checkpoint t_restore. The triggers are exact.", checkpoint[1]);
        Assert.Equal("Start place: Suggested: In ol_sys_a at 10, 20, 30", checkpoint[2]);
        Assert.Equal("Place source: Act_SetInitialPlayerPos of t_restore", checkpoint[3]);
        Assert.Contains($"{EditorIcons.ExclamationTriangle}  {StartPlan.CheckpointStateWarning("t_restore")}", checkpoint);

        var approximate = Lines("t_chosen");
        Assert.Equal("Start: t_chosen (approximate). Setup triggers rebuild the state of the path. Some state can differ, " +
                     "for example damage.", approximate[1]);
        var page = PlayStartPageModel.Of(analysis, Suggested with { Trigger = "t_chosen" });
        Assert.Equal($"Start place: {page.PlaceText}", approximate[2]);
        Assert.StartsWith("Start place: Suggested: In ol_sys_a at ", approximate[2]);
        Assert.Equal($"Place source: {page.Plan.Place.Source}", approximate[3]);

        var running = Lines("t_chosen", playRuns: true);
        Assert.Equal([.. approximate, PlayFromHereStates.PlayRunsLine], running);
        Assert.Equal(GraphView.PlayFromHereTip, PlayFromHereStates.Tooltip(null, null, false));
    }

    [Fact]
    public void TheTooltipOfABaseChoiceHasNoPlaceSource()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var states = AtOnce();
        var setting = new PlayStartSetting("", "base:ol_base_a");

        states.Sync(analysis, setting, false);
        var lines = PlayFromHereStates.Tooltip(StateOf(states, analysis, "t_chosen"), null, false).Split('\n');

        Assert.Equal("Start place: At base ol_base_a", lines[2]);
        Assert.Equal(PlayStartPageModel.Of(analysis, setting with { Trigger = "t_chosen" }).PlaceText, lines[2]["Start place: ".Length..]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Place source:", StringComparison.Ordinal));
    }

    /// <summary>A runner that holds each pass until the test ends it.</summary>
    private sealed class HeldPasses
    {
        public List<(Func<PlayFromHereStates.Pass> Make, TaskCompletionSource<PlayFromHereStates.Pass> Done)> Passes { get; } = [];

        public Task<PlayFromHereStates.Pass> Run(Func<PlayFromHereStates.Pass> pass)
        {
            var done = new TaskCompletionSource<PlayFromHereStates.Pass>();
            Passes.Add((pass, done));
            return done.Task;
        }

        public void End(int pass) => Passes[pass].Done.SetResult(Passes[pass].Make());
    }
}
