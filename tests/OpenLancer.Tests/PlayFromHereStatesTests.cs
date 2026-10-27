using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
/// The Play buttons of the trigger nodes (note of 2026-10-27 in section 8.1 of the M6c design): which nodes show the
/// button, the pass for each model and place choice, the stale states, a pass that fails, the check of each start place
/// in a frame, the disabled node menu item with its reason, the tooltips, the hidden parts of a header and the look of
/// the button. The script is the synthetic script of the start plan tests, and all names are invented.
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

    private static readonly PlayStartSetting AtCamera = new("", "camera");

    // The start place problem of the tests, and the Console lines of the failed passes.
    private Func<StartPlace, string?> problem = _ => null;
    private readonly List<string> console = [];

    /// <summary>An analysis of a script text: a new index for each call, as after each edit.</summary>
    private static MissionAnalysis Analysis(string text, StorySlot? slot, string path = Path) =>
        new(new MissionScriptInfo(path, slot, MissionScriptState.BaseGame, null, false), Encoding.ASCII.GetBytes(text),
            Document(text, path), Index(text), [], null);

    private static MissionGraph Graph(MissionAnalysis analysis) =>
        MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic));

    private PlayFromHereStates Model(Func<Func<PlayFromHereStates.Pass>, CancellationToken, Task<PlayFromHereStates.Pass>> runner) =>
        new(place => problem(place), console.Add, runner);

    /// <summary>A model whose passes run at once on the test thread.</summary>
    private PlayFromHereStates AtOnce() => Model((pass, _) => Task.FromResult(pass()));

    private static TriggerStart StateOf(PlayFromHereStates states, MissionAnalysis analysis, string trigger) =>
        states.Of(analysis.Index.Trigger(trigger)!)!;

    private static GraphNode NodeOf(MissionGraph graph, string trigger) => graph.NodeOf(graph.Index.Trigger(trigger)!)!;

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
        // Equal places of one pass are one object, so a frame checks each place once.
        Assert.All(states.Values, s => Assert.Same(states.Values.First(o => o.Place == s.Place).Place, s.Place));
        Assert.True(states.Values.Select(s => s.Place).Distinct().Count() < states.Count);
    }

    [Fact]
    public void ATriggerOnAStoppedBranchShowsNoButtonAndTheMenuItemGivesTheReason()
    {
        var analysis = Analysis(StoppedBranch, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        var graph = Graph(analysis);
        var chosen = analysis.Index.Trigger("s_chosen")!;

        Assert.False(states.Of(chosen)!.CanStart);
        Assert.False(states.Shows(graph.NodeOf(chosen)!));
        Assert.False(states.CanPlay(chosen));
        Assert.Equal(StartPlan.RefusalPrefix + "Trigger 's_mid' on the path to 's_chosen' is not active at that point, " +
                     "because another trigger stops it first. Pick another trigger.", states.Tooltip(chosen, false));
        Assert.True(states.Shows(NodeOf(graph, "s_mid")));
        Assert.True(states.CanPlay(analysis.Index.Trigger("s_mid")!));
    }

    [Fact]
    public void AScriptWithoutAStorySlotShowsNoButtonAndKeepsNoSpace()
    {
        var analysis = Analysis(StartPlanTests.Script, null);
        var states = AtOnce();

        states.Sync(analysis, Suggested, false);
        var trigger = analysis.Index.Trigger("t_chosen")!;

        Assert.False(states.KeepsSpace);
        Assert.False(states.Shows(NodeOf(Graph(analysis), "t_chosen")));
        Assert.Equal(StartPlan.NoSlotReason, states.Of(trigger)!.Reason);
        Assert.False(states.CanPlay(trigger));
        Assert.Equal(StartPlan.RefusalPrefix + StartPlan.NoSlotReason, states.Tooltip(trigger, false));
        states.Sync(Analysis(StartPlanTests.Script, StartPlanTests.Slot), Suggested, false);
        Assert.True(states.KeepsSpace);
    }

    [Fact]
    public void AStartPlaceProblemHidesTheButtonAndTurnsTheMenuItemOff()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var states = AtOnce();
        var node = NodeOf(Graph(analysis), "t_chosen");
        var trigger = node.Trigger!;
        states.Sync(analysis, AtCamera, false);

        Assert.Equal(StartPlaceKind.Camera, states.Of(trigger)!.Place.Kind);
        Assert.True(states.Shows(node));
        Assert.True(states.CanPlay(trigger));

        // The open system of the Scene closes. The next frame checks the place again.
        problem = place => place.Kind == StartPlaceKind.Camera ? MissionPlay.NoCameraSystem : null;
        states.Sync(analysis, AtCamera, false);

        Assert.True(states.Of(trigger)!.CanStart);
        Assert.False(states.Shows(node));
        Assert.False(states.CanPlay(trigger));
        Assert.Equal(StartPlan.RefusalPrefix + MissionPlay.NoCameraSystem, states.Tooltip(trigger, false));
        Assert.Equal(1, states.Passes);
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
        var graph = Graph(analysis);
        var duplicate = analysis.Index.Triggers.Single(t => t.IsDuplicate);

        var state = states.Of(duplicate)!;

        Assert.Equal((false, PlayFromHereStates.DuplicateReason), (state.CanStart, state.Reason));
        Assert.Equal(StartPlan.RefusalPrefix + PlayFromHereStates.DuplicateReason, states.Tooltip(duplicate, false));
        Assert.False(states.CanPlay(duplicate));
        Assert.False(states.Shows(graph.NodeOf(duplicate)!));
        Assert.True(StateOf(states, analysis, "t_chosen").CanStart);
    }

    /// <summary>Only a trigger node of the open mission shows the button. In prefab mode, the graph syncs without the
    /// open mission: no node shows a button and keeps its space, and the states stay for the return.</summary>
    [Fact]
    public void OnlyATriggerNodeOfTheOpenMissionShowsTheButton()
    {
        var text = StartPlanTests.Script.Replace("Act_ActTrig = t_accept", "Act_ActTrig = t_accept\nAct_ActTrig = t_none");
        var analysis = Analysis(text, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        var graph = Graph(analysis);
        var node = NodeOf(graph, "t_chosen");

        Assert.True(states.Shows(node));
        var missing = Assert.Single(graph.Nodes, n => n.Kind == GraphNodeKind.Missing);
        Assert.False(states.Shows(missing));
        var frames = graph.Frames.Select(f => f.Node).ToList();
        Assert.NotEmpty(frames);
        Assert.All(frames, f => Assert.False(states.Shows(f)));

        states.Sync(null, Suggested, false);

        Assert.False(states.KeepsSpace);
        Assert.False(states.Shows(node));
        Assert.NotNull(states.Of(node.Trigger!));
        states.Sync(analysis, Suggested, false);
        Assert.True(states.Shows(node));
        Assert.Equal(1, states.Passes);
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

        states.Sync(analysis, AtCamera, false);
        Assert.Equal(2, states.Passes);

        var edited = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(edited, AtCamera, false);
        Assert.Equal(3, states.Passes);

        states.Sync(Analysis(StartPlanTests.Script, StartPlanTests.Slot, OtherPath), Suggested, false);
        Assert.Equal(4, states.Passes);

        // A drag or a typed text starts no pass. The pass starts at the end of the transaction.
        var dragged = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(dragged, Suggested, true);
        Assert.Equal(4, states.Passes);
        states.Sync(dragged, Suggested, false);
        Assert.Equal(5, states.Passes);

        // Prefab mode starts no pass.
        states.Sync(null, AtCamera, false);
        Assert.Equal(5, states.Passes);
    }

    [Fact]
    public void ANewKeyCancelsTheRunningPassAndALatePassChangesNothing()
    {
        var held = new HeldPasses();
        var states = Model(held.Run);
        var first = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var second = Analysis(StoppedBranch, StartPlanTests.Slot);

        states.Sync(first, Suggested, false);
        states.Sync(second, Suggested, false);

        // The runner gets the cancel token of its pass, so a cancelled pass ends as cancelled, not as failed.
        Assert.True(held.Passes[0].Token.IsCancellationRequested);
        Assert.False(held.Passes[1].Token.IsCancellationRequested);
        var cancelled = Assert.Throws<OperationCanceledException>(() => held.Passes[0].Make());
        Assert.Equal(held.Passes[0].Token, cancelled.CancellationToken);
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
        Assert.Empty(console);
    }

    [Fact]
    public void TheLastStatesStayUntilTheNextPassOfTheScriptEnds()
    {
        var held = new HeldPasses();
        var states = Model(held.Run);
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

    /// <summary>Each state holds a start place of its place choice. So a new place choice drops the states at once: no
    /// node shows a button for the place of the old choice, and the menu item stays on until the pass ends.</summary>
    [Fact]
    public void ANewPlaceChoiceDropsTheStatesAtOnce()
    {
        var held = new HeldPasses();
        var states = Model(held.Run);
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var node = NodeOf(Graph(analysis), "t_chosen");
        states.Sync(analysis, Suggested, false);
        held.End(0);
        states.Sync(analysis, Suggested, false);
        Assert.True(states.Shows(node));

        problem = place => place.Kind == StartPlaceKind.Camera ? MissionPlay.NoCameraSystem : null;
        states.Sync(analysis, AtCamera, false);

        Assert.Null(states.Of(node.Trigger!));
        Assert.False(states.Shows(node));
        Assert.True(states.KeepsSpace);
        Assert.True(states.CanPlay(node.Trigger!));
        held.End(1);
        states.Sync(analysis, AtCamera, false);
        Assert.Equal(StartPlaceKind.Camera, states.Of(node.Trigger!)!.Place.Kind);
        Assert.False(states.CanPlay(node.Trigger!));
    }

    /// <summary>A pass that fails leaves no states, so no node shows a button of an old model. The Console gets one line
    /// with the reason. The next model starts a pass again.</summary>
    [Fact]
    public void AFailedPassDropsTheStatesAndWritesOneConsoleLine()
    {
        var held = new HeldPasses();
        var states = Model(held.Run);
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var node = NodeOf(Graph(analysis), "t_chosen");
        states.Sync(analysis, Suggested, false);
        held.End(0);
        states.Sync(analysis, Suggested, false);
        Assert.True(states.Shows(node));

        var edited = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(edited, Suggested, false);
        held.Passes[1].Done.SetException(new InvalidOperationException("The plan broke."));
        states.Sync(edited, Suggested, false);
        states.Sync(edited, Suggested, false);

        Assert.Null(states.Of(node.Trigger!));
        Assert.False(states.Shows(node));
        Assert.Equal([PlayFromHereStates.FailedLine("The plan broke.")], console);
        Assert.Equal(2, states.Passes);
        var next = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        states.Sync(next, Suggested, false);
        held.End(2);
        states.Sync(next, Suggested, false);
        Assert.True(states.Shows(node));
        Assert.Single(console);
    }

    /// <summary>The reads of a frame ask for the problem of each start place once, also when each place has a problem.
    /// The next frame asks again, because the open system and the game data can change.</summary>
    [Fact]
    public void AFrameChecksEachStartPlaceOnce()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var graph = Graph(analysis);
        var checks = 0;
        problem = place =>
        {
            checks++;
            return $"No place {place.Kind}.";
        };
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        var places = analysis.Index.Triggers.Select(t => states.Of(t)!.Place).Distinct().Count();

        for (var i = 0; i < 3; i++)
        {
            Assert.All(graph.Nodes, n => Assert.False(states.Shows(n)));
            Assert.All(analysis.Index.Triggers, t => Assert.False(states.CanPlay(t)));
        }

        Assert.Equal(places, checks);
        states.Sync(analysis, AtCamera, false);
        Assert.All(graph.Nodes, n => Assert.False(states.Shows(n)));
        Assert.Equal(places + 1, checks);
        states.Sync(analysis, AtCamera, false);
        foreach (var trigger in analysis.Index.Triggers)
            Assert.False(states.CanPlay(trigger));
        Assert.Equal(places + 2, checks);
    }

    [Fact]
    public void TheTooltipNamesTheStartThePlaceAndItsSource()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var states = AtOnce();
        states.Sync(analysis, Suggested, false);
        string[] Lines(string trigger, bool playRuns = false) =>
            states.Tooltip(analysis.Index.Trigger(trigger)!, playRuns).Split('\n');

        Assert.Equal([GraphView.PlayFromHereTip, "Start: Mission start (t_start). The start is exact.",
            "Start place: Suggested: At the Scene camera", "Place source: No line of the path gives a place."], Lines("t_start"));

        var checkpoint = Lines("t_restore");
        Assert.Equal("Start: Checkpoint t_restore. The state of the triggers is exact.", checkpoint[1]);
        Assert.Equal("Start place: Suggested: In ol_sys_a at 10, 20, 30", checkpoint[2]);
        Assert.Equal("Place source: Act_SetInitialPlayerPos of t_restore", checkpoint[3]);
        var warning = $"{EditorIcons.ExclamationTriangle}  {StartPlan.CheckpointStateWarning("t_restore")}";
        // The tooltip draws the warning lines in the warning color, as the Play start page does.
        Assert.Equal(warning, Assert.Single(checkpoint, ItemTooltip.IsWarning));

        var approximate = Lines("t_chosen");
        Assert.Equal("Start: t_chosen (approximate). Setup triggers rebuild the state of the path. Some state can differ, " +
                     "for example damage.", approximate[1]);
        var page = PlayStartPageModel.Of(analysis, Suggested with { Trigger = "t_chosen" });
        Assert.Equal($"Start place: {page.PlaceText}", approximate[2]);
        Assert.StartsWith("Start place: Suggested: In ol_sys_a at ", approximate[2]);
        Assert.Equal($"Place source: {page.Plan.Place.Source}", approximate[3]);

        var running = Lines("t_chosen", playRuns: true);
        Assert.Equal([.. approximate, PlayFromHereStates.PlayRunsLine], running);
        Assert.Equal("A mission play runs. The game starts again at this trigger.", running[^1]);
        Assert.Equal(GraphView.PlayFromHereTip, AtOnce().Tooltip(analysis.Index.Trigger("t_chosen")!, false));
    }

    [Fact]
    public void TheTooltipOfABaseChoiceHasNoPlaceSource()
    {
        var analysis = Analysis(StartPlanTests.Script, StartPlanTests.Slot);
        var states = AtOnce();
        var setting = new PlayStartSetting("", "base:ol_base_a");

        states.Sync(analysis, setting, false);
        var lines = states.Tooltip(analysis.Index.Trigger("t_chosen")!, false).Split('\n');

        Assert.Equal("Start place: At base ol_base_a", lines[2]);
        Assert.Equal(PlayStartPageModel.Of(analysis, setting with { Trigger = "t_chosen" }).PlaceText, lines[2]["Start place: ".Length..]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Place source:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, ItemTooltip.IsWarning);
    }

    /// <summary>The space of the Play button and the marks can cut the title and drop the header chips. A hover on the
    /// header then shows the full title and each hidden chip with its tooltip.</summary>
    [Fact]
    public void AHeaderTooltipShowsTheCutTitleAndTheHiddenChips()
    {
        Assert.Null(GraphView.HeaderTip("t_short", false, []));
        Assert.Equal("t_a_long_name", GraphView.HeaderTip("t_a_long_name", true, []));
        Assert.Equal($"t_name\nRepeats: {MissionPageModel.TriggerChipTip("Repeats")}\nol_sys_a: " +
                     MissionPageModel.TriggerChipTip("ol_sys_a"), GraphView.HeaderTip("t_name", false, ["Repeats", "ol_sys_a"]));
    }

    /// <summary>The look table of the button: no fill, a light fill under the mouse, and a solid fill with the dark glyph
    /// only while the mouse holds the button and is on it. All colors take the alpha of the node.</summary>
    [Fact]
    public void TheButtonLooksPressedOnlyWhileTheMouseIsOnIt()
    {
        var play = EditorTheme.Play with { W = 0.5f };

        Assert.Equal(((Vector4?)null, play), GraphView.PlayButtonLook(false, false, 0.5f));
        Assert.Equal(((Vector4?)(EditorTheme.Play with { W = 0.125f }), play), GraphView.PlayButtonLook(true, false, 0.5f));
        Assert.Equal(((Vector4?)play, EditorTheme.GraphNodeHeader with { W = 0.5f }), GraphView.PlayButtonLook(true, true, 0.5f));
        // A drag off the held button: no pressed look, because a release there does not play.
        Assert.Equal(((Vector4?)null, play), GraphView.PlayButtonLook(false, true, 0.5f));
    }

    /// <summary>The choice problem of the toolbar warning chip: without game data, each place choice has the problem. The
    /// retail test checks the choices with game data.</summary>
    [Fact]
    public void WithoutGameDataEachPlaceChoiceHasAProblem()
    {
        Assert.All(new[] { Suggested, AtCamera, new PlayStartSetting("", "base:ol_base_a") },
            setting => Assert.Equal(MissionPlay.NoGameData, MissionPlay.ChoiceProblem(null, true, setting)));
        Assert.Equal($"{MissionView.ChoiceProblemLead}\n{MissionPlay.NoCameraSystem}",
            MissionView.ChoiceProblemTip(MissionPlay.NoCameraSystem));
    }

    /// <summary>A runner that holds each pass until the test ends it.</summary>
    private sealed class HeldPasses
    {
        public List<(Func<PlayFromHereStates.Pass> Make, CancellationToken Token, TaskCompletionSource<PlayFromHereStates.Pass> Done)>
            Passes { get; } = [];

        public Task<PlayFromHereStates.Pass> Run(Func<PlayFromHereStates.Pass> pass, CancellationToken token)
        {
            var done = new TaskCompletionSource<PlayFromHereStates.Pass>();
            Passes.Add((pass, token, done));
            return done.Task;
        }

        public void End(int pass) => Passes[pass].Done.SetResult(Passes[pass].Make());
    }
}
