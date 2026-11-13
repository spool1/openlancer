using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.GraphTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The model of the mission graph (sections 6.1 to 6.5 and 18.1 of the M6a design) on a synthetic mission. The tests
/// check the frames and their collapse, the nodes of each lens, the links, the pins, the rows and the detail level.
/// </summary>
public sealed class GraphModelTests
{
    private static readonly MissionAnalysis Mission = Analysis();

    private static MissionGraph Graph(GraphLens lens = GraphLens.Beats, bool showLines = false,
        Dictionary<string, bool>? collapse = null, bool showBaseGame = false, MissionAnalysis? analysis = null) =>
        MissionGraph.Build(analysis ?? Mission, new GraphOptions(lens, showLines, collapse, ShowBaseGame: showBaseGame));

    private static string KeyOf(MissionGraph graph, string trigger) =>
        MissionGraph.KeyOf(graph.Index.Trigger(trigger)!);

    private static IEnumerable<string> Titles(MissionGraph graph) => graph.Nodes.Select(n => n.Title);

    [Fact]
    public void TheActsAndTheFramesFollowTheMainPath()
    {
        var graph = Graph(GraphLens.Logic);

        Assert.Equal(["Act 1: sys_a", "Act 2: sys_b", "Not started by the mission (1)"], graph.Acts.Select(a => a.Title));
        Assert.Equal("5 triggers, 0 checkpoints", graph.Acts[0].Detail);
        Assert.Equal("3 triggers, 1 checkpoint", graph.Acts[1].Detail);
        var prefabs = graph.Frames.Where(f => f.Kind == GraphFrameKind.Prefab).ToList();
        Assert.Equal(["Checkpoint", "Spawn wave", "Mission success"], prefabs.Select(f => f.Title));
        Assert.Equal(["t_save", "t_restore"], prefabs[0].Triggers.Select(t => t.Nickname));
        Assert.Equal("a:2", prefabs[0].Parent!.Key);
        Assert.Equal(["t_spawn", "t_kill"], prefabs[1].Triggers.Select(t => t.Nickname));
        Assert.Equal("a:1", prefabs[1].Parent!.Key);
        Assert.Equal(["t_lonely"], graph.Acts[2].Triggers.Select(t => t.Nickname));
    }

    [Fact]
    public void TheBeatsLensCollapsesThePrefabFramesAndTheNotStartedFrame()
    {
        var graph = Graph();

        Assert.Equal(["t_start", "t_wait", "t_after", "Not started by the mission (1)", "Checkpoint", "Spawn wave",
            "Mission success", "Missing: t_missing"], Titles(graph));
        Assert.All(graph.Frames.Where(f => f.Kind == GraphFrameKind.Act), f => Assert.False(f.Collapsed));
        Assert.True(graph.Acts[2].Collapsed);
        Assert.Equal(graph.Block("p:spawnWave|t_spawn"), graph.NodeOf(graph.Index.Trigger("t_kill")!)!.Frame);
    }

    /// <summary>The node of a missing trigger has the message of rule M05 (section 6.3 of the M6a design). The tooltip
    /// of each field of a collapsed prefab gives each value on its own line (section 6.4).</summary>
    [Fact]
    public void TheMissingNodeHasTheMessageOfItsRuleAndAPrefabFieldListsItsValues()
    {
        var graph = Graph();
        var missing = graph.Nodes.Single(n => n.Kind == GraphNodeKind.Missing);
        var wave = graph.Nodes.Single(n => n.Title == "Spawn wave");

        Assert.Contains("names trigger 't_missing'. This script has no such trigger.", missing.Sentence);
        Assert.Equal(wave.Fields.Count, wave.FieldTips.Count);
        var endsWhen = wave.FieldTips[wave.Fields.ToList().FindIndex(f => f.StartsWith("Ends when"))];
        Assert.StartsWith("Ends when:\n", endsWhen);
    }

    [Fact]
    public void TheLogicLensOpensAllFrames()
    {
        var graph = Graph(GraphLens.Logic);

        Assert.All(graph.Frames, f => Assert.False(f.Collapsed));
        Assert.Equal(10, graph.Nodes.Count);
        Assert.Equal(9, graph.Nodes.Count(n => n.Kind == GraphNodeKind.Trigger));
    }

    [Fact]
    public void ACollapseOfTheUserWinsOverTheLens()
    {
        var open = Graph(collapse: new Dictionary<string, bool> { ["p:spawnWave|t_spawn"] = false, ["a:0"] = false });
        var closed = Graph(GraphLens.Logic, collapse: new Dictionary<string, bool> { ["a:1"] = true });

        Assert.Contains("t_spawn", Titles(open));
        Assert.Contains("t_kill", Titles(open));
        Assert.Contains("t_lonely", Titles(open));
        Assert.DoesNotContain("Spawn wave", Titles(open));
        Assert.Contains("Act 1: sys_a", Titles(closed));
        Assert.DoesNotContain("t_start", Titles(closed));
        Assert.Equal("Act 1: sys_a", closed.NodeOf(closed.Index.Trigger("t_spawn")!)!.Title);
    }

    [Fact]
    public void TheLinksEndAtThePinsOfTheirKind()
    {
        var graph = Graph(GraphLens.Logic);
        string Of(GraphLink link) => $"{link.From.Title}.{link.FromPin.Kind}>{link.To.Title}.{link.ToPin.Kind}";

        Assert.Equal(
        [
            "t_start.Starts>t_spawn.Start", "t_start.Starts>t_wait.Start", "t_spawn.Starts>t_after.Start",
            "t_kill.Starts>t_save.Start", "t_wait.Starts>t_kill.Start", "t_after.Starts>Missing: t_missing.Start",
            "t_save.Saves>t_restore.Load", "t_save.Starts>t_end.Start", "t_restore.Starts>t_end.Start",
            "t_save.State>t_end.Watches", "t_lonely.Starts>t_end.Start",
        ], graph.Links.Select(Of));
        Assert.Equal("Is active", graph.Links.Single(l => l.Kind == MissionLinkKind.Watch).State);
    }

    [Fact]
    public void TheLinksOfACollapsedFrameEndAtItsNodeAndTheLinksInsideItGo()
    {
        var graph = Graph();
        string Of(GraphLink link) => $"{link.From.Title}.{link.FromPin.Kind}>{link.To.Title}.{link.ToPin.Kind}";

        Assert.Equal(
        [
            "t_start.Starts>Spawn wave.Start", "t_start.Starts>t_wait.Start", "Spawn wave.Starts>t_after.Start",
            "Spawn wave.Starts>Checkpoint.Start", "t_wait.Starts>Spawn wave.Start",
            "t_after.Starts>Missing: t_missing.Start", "Checkpoint.Starts>Mission success.Start",
            "Checkpoint.State>Mission success.Watches", "Not started by the mission (1).Starts>Mission success.Start",
        ], graph.Links.Select(Of));
        // The start lines of t_save and t_restore share their pins, so one link stands for both.
        Assert.Equal(2, graph.Links.Single(l => l.From.Title == "Checkpoint" && l.Kind == MissionLinkKind.Start).Sources.Count);
    }

    [Fact]
    public void ASummaryNodeHasTheSentenceTheChipsAndThePinsOfItsGroups()
    {
        var graph = Graph(GraphLens.Logic);
        var start = (GraphNode)graph.Block(KeyOf(graph, "t_start"))!;
        var end = (GraphNode)graph.Block(KeyOf(graph, "t_end"))!;

        Assert.False(start.Lines);
        Assert.Equal(["Start", "sys_a"], start.HeaderChips);
        Assert.Equal("Starts 2", Assert.Single(start.Chips).Text);
        Assert.Contains("The order comes from the trigger names", start.Chips[0].Tooltip);
        Assert.Equal([GraphPinKind.Start, GraphPinKind.Stop, GraphPinKind.Starts], start.Pins.Select(p => p.Kind));
        Assert.Equal(new Vector2(MissionGraph.SummaryWidth, MissionGraph.HeaderHeight + 2 * MissionGraph.LineHeight +
            MissionGraph.BottomPadding), start.Size);
        Assert.Equal("story", end.EventGroup);
        Assert.Equal([GraphPinKind.Start, GraphPinKind.Stop, GraphPinKind.Watches], end.Pins.Where(p => p.IsInput).Select(p => p.Kind));
        Assert.StartsWith("Watches: ", end.Pin(GraphPinKind.Watches)!.Tooltip);
        Assert.Equal("End mission: success", Assert.Single(end.Chips).Tooltip);
    }

    [Fact]
    public void TheMarksOfATriggerFollowItsStartsAndItsActions()
    {
        var graph = Graph(GraphLens.Logic, analysis: Analysis(Script + "\n\n[Trigger]\nnickname = t_idle\nCnd_True = no_params\n"));

        Assert.True(((GraphNode)graph.Block(KeyOf(graph, "t_lonely"))!).NeverStarts);
        Assert.False(((GraphNode)graph.Block(KeyOf(graph, "t_start"))!).NeverStarts);
        Assert.True(((GraphNode)graph.Block(KeyOf(graph, "t_idle"))!).WatchedState);
    }

    [Fact]
    public void TheBaseGameProblemsOfANodeFollowTheToggle()
    {
        var hidden = Graph(GraphLens.Logic);
        var shown = Graph(GraphLens.Logic, showBaseGame: true);
        var lonely = KeyOf(hidden, "t_lonely");

        // No mod changes the script, so its problems are base-game problems.
        Assert.Empty(((GraphNode)hidden.Block(lonely)!).Problems);
        var node = (GraphNode)shown.Block(lonely)!;
        Assert.Equal((0, 0, 1), (node.Errors, node.Warnings, node.Muted));
    }

    [Fact]
    public void TheLinesLevelShowsTheIgnoredLinesTheConditionAndTheActions()
    {
        var text = Script.Replace("[Trigger]\nnickname = t_wait\nsystem = sys_a\nCnd_Timer = 5",
            "[Trigger]\nrepeatable = true\nnickname = t_wait\nsystem = sys_a\nCnd_True = no_params\nCnd_Timer = 5");
        var graph = Graph(GraphLens.Logic, showLines: true, analysis: Analysis(text));
        var wait = (GraphNode)graph.Block(KeyOf(graph, "t_wait"))!;

        Assert.True(wait.Lines);
        Assert.Equal([GraphRowKind.BeforeNickname, GraphRowKind.IgnoredCondition, GraphRowKind.Condition,
            GraphRowKind.Action], wait.Rows.Select(r => r.Kind));
        // Each row names its line by the plain name rule that the checks and the source text of a start place also use.
        Assert.Equal(["Repeats: true", "At once", "After a delay: 5", "Start trigger: t_kill"], wait.Rows.Select(r => r.Text));
        Assert.All(wait.Rows, r => Assert.StartsWith(r.Line.Plain, r.Text, StringComparison.Ordinal));
        var pin = wait.Rows[3].Pin!;
        Assert.Equal((GraphPinKind.Row, 4), (pin.Kind, pin.Slot));
        Assert.Contains(pin, wait.Pins);
        Assert.Equal(MissionGraph.LinesWidth, wait.Size.X);
        Assert.Contains(graph.Links, l => l.FromPin == pin && l.To.Title == "t_kill");
    }

    [Fact]
    public void ANodeShowsAtMostTwentyRowsUntilItShowsAll()
    {
        var many = string.Concat(Enumerable.Range(0, 25).Select(i => $"Act_SetTitle = {1000 + i}\n"));
        var analysis = Analysis(Script + "\n\n[Trigger]\nnickname = t_many\nCnd_True = no_params\n" + many);
        var key = MissionGraph.KeyOf(analysis.Index.Trigger("t_many")!);
        var limited = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic, ShowLines: true));
        var all = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic, ShowLines: true,
            AllRowsOf: new HashSet<string> { key }));

        var node = (GraphNode)limited.Block(key)!;
        Assert.Equal((19, 7, 20), (node.Rows.Count, node.HiddenRows, node.BodyLines));
        // The lines that the limit hides start their links at the row "Show {n} more lines".
        Assert.Equal(20, node.Pin(GraphPinKind.More)!.Slot);
        Assert.Equal((26, 0), (((GraphNode)all.Block(key)!).Rows.Count, ((GraphNode)all.Block(key)!).HiddenRows));
    }

    [Fact]
    public void OneNodeCanShowItsLinesAtTheSummaryLevel()
    {
        var key = KeyOf(Graph(), "t_start");
        var graph = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Logic, LinesOf: new HashSet<string> { key }));

        Assert.True(((GraphNode)graph.Block(key)!).Lines);
        Assert.False(((GraphNode)graph.Block(KeyOf(graph, "t_wait"))!).Lines);
    }

    [Fact]
    public void TheFlowChipsShowTheRestartTheCancelAndTheSelfStart()
    {
        var text = Script + """


            [Trigger]
            nickname = t_flow
            Cnd_True = no_params
            Act_DeactTrig = t_wait
            Act_ActTrig = t_wait
            Act_ActTrig = t_after
            Act_DeactTrig = t_after
            Act_ActTrig = t_flow
            """;
        var graph = Graph(GraphLens.Logic, analysis: Analysis(text));
        var chips = ((GraphNode)graph.Block(KeyOf(graph, "t_flow"))!).Chips;

        Assert.Equal(["Starts 3", "Stops 2", "Restarts t_wait", "Starts and stops t_after", "Starts itself"],
            chips.Select(c => c.Text));
        Assert.True(chips[3].Warning);
        Assert.DoesNotContain(graph.Links, l => l.From == l.To);
    }

    [Fact]
    public void TheLensesFadeTheTriggersOutsideTheirSubject()
    {
        var battle = Graph(GraphLens.Battle);
        var objectives = Graph(GraphLens.Objectives);

        // The Battle lens keeps the spawn wave open.
        Assert.Equal(["t_spawn", "t_kill"], battle.Nodes.Where(n => !n.Faded).Select(n => n.Title).Where(t => t.StartsWith("t_")));
        Assert.Contains("t_kill", Titles(battle));
        Assert.True(battle.Nodes.Single(n => n.Title == "t_start").Faded);
        Assert.False(battle.Nodes.Single(n => n.Kind == GraphNodeKind.Missing).Faded);
        Assert.All(objectives.Nodes.Where(n => n.Kind != GraphNodeKind.Missing), n => Assert.True(n.Faded));
        Assert.All(Graph(GraphLens.Logic).Nodes, n => Assert.False(n.Faded));
    }

    [Fact]
    public void TheCutsceneLensListsTheCutscenesInPathOrder()
    {
        var text = Script.Replace("Act_ActTrig = t_after", "Act_ActTrig = t_after\nAct_CallThorn = scripts\\a.thn")
            .Replace("Act_Save = t_restore",
                "Act_Save = t_restore\nAct_CallThorn = scripts\\b.thn\nAct_RevertCam = no_params\nAct_AddRTC = scenes\\c.ini");
        var graph = Graph(GraphLens.Cutscene, analysis: Analysis(text));

        // The side list gives the cutscenes: the space cutscenes and the base scenes. A camera line is not a cutscene.
        Assert.Equal(["Play space cutscene: scripts\\a.thn", "Play space cutscene: scripts\\b.thn", "Add base scene: scenes\\c.ini"],
            graph.SideList.Select(i => i.Text));
        Assert.Empty(Graph(GraphLens.Beats, analysis: Analysis(text)).SideList);
    }

    [Fact]
    public void TheObjectivesSideListGivesTheTextIdsOfEachLine()
    {
        var text = Script.Replace("Act_ActTrig = t_wait", "Act_ActTrig = t_wait\nAct_SetTitle = 2001\nAct_SetNNObj = obj1")
            .Replace("Act_Save = t_restore", "Act_Save = t_restore\nAct_NNIds = 2003, HISTORY") +
            "\n\n[NNObjective]\nnickname = obj1\nstate = HIDDEN\ntype = ids, 2002\n";
        var graph = Graph(GraphLens.Objectives, analysis: Analysis(text));

        Assert.Equal(["t_start", "t_start", "t_save"], graph.SideList.Select(i => i.Trigger.Nickname));
        Assert.Equal(["2001", "2002", "2003"], graph.SideList.Select(i => string.Join(",", i.TextIds)));
    }

    [Theory]
    [InlineData(0, 0, 1.0f, GraphDetail.Full)]
    [InlineData(0, 0, 0.6f, GraphDetail.Full)]
    [InlineData(0, 0, 0.5f, GraphDetail.Header)]
    [InlineData(5000, 0, 1.0f, GraphDetail.Stub)]
    [InlineData(-400, 0, 1.0f, GraphDetail.Stub)]
    public void TheDetailOfANodeFollowsTheZoomAndTheView(float x, float y, float scale, GraphDetail expected)
    {
        var min = new Vector2(x, y);
        var max = min + new Vector2(MissionGraph.SummaryWidth, 80);

        Assert.Equal(expected, MissionGraph.DetailOf(min, max, new Vector2(-100, -100), new Vector2(1500, 900), scale));
    }

    [Fact]
    public void ANodeKeepsItsIdWhenTheGraphIsBuiltAgain()
    {
        var ids = new GraphIds();
        var first = MissionGraph.Build(Mission, new GraphOptions(Ids: ids));
        var second = MissionGraph.Build(Mission, new GraphOptions(GraphLens.Logic, true, Ids: ids));

        Assert.Equal(first.Block(KeyOf(first, "t_start"))!.Id, second.Block(KeyOf(second, "t_start"))!.Id);
        var all = second.Nodes.Select(n => n.Id).Concat(second.Nodes.SelectMany(n => n.Pins).Select(p => p.Id))
            .Concat(second.Links.Select(l => l.Id)).Concat(second.Frames.Select(f => f.Id)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
