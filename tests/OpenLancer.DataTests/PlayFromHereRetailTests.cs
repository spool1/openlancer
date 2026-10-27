using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using OpenLancer.Editor.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the Play buttons of the trigger nodes (note of 2026-10-27 in section 8.1 of the M6c design) over the
/// story scripts of the base game: the time of a pass and of the reads of one frame, and the agreement of the states with
/// the full plans. The tests count triggers and measure time. They hold no retail text.
/// </summary>
public sealed class PlayFromHereRetailTests(ITestOutputHelper output)
{
    /// <summary>M10 has the most triggers of the story scripts, so its pass is the slowest.</summary>
    private static MissionAnalysis Biggest()
    {
        var run = RetailMissions.Run();
        var biggest = run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!)
            .MaxBy(a => a.Index.Triggers.Count)!;
        Assert.Equal("M10", biggest.Script.Slot!.Slot);
        return biggest;
    }

    private static IReadOnlyDictionary<string, TriggerStart> Pass(MissionAnalysis analysis) =>
        PlayFromHereStates.Make(analysis.Index, analysis.Script.Slot, PlayStartSetting.Default, CancellationToken.None);

    /// <summary>Gate 1: the pass over the 408 triggers of M10 takes less than 500 ms, so the buttons follow an edit in
    /// half a second at most. Each of the 408 triggers can start.</summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void APassOverTheBiggestScriptTakesLessThanHalfASecond()
    {
        var m10 = Biggest();
        var limit = TimeSpan.FromMilliseconds(500);
        IReadOnlyDictionary<string, TriggerStart> states = new Dictionary<string, TriggerStart>();

        var best = new TimingGates().WarmGate(limit, 3, () => states = Pass(m10));

        output.WriteLine($"Pass over M10: {best.TotalMilliseconds:0} ms for {states.Count} triggers.");
        Assert.Equal((408, 408), (states.Count, states.Values.Count(s => s.CanStart)));
        Assert.True(best < limit, $"The pass over M10 took {best.TotalMilliseconds:0} ms.");
    }

    /// <summary>Gate 2: the reads of one frame take less than 2 ms: the sync of the frame, and for each node of M10 the
    /// state of its trigger and the check of its start place. One try is the median of 100 frames. The gate also holds
    /// when each start place has a problem, because a frame checks each place once and makes few texts.</summary>
    [Theory]
    [Trait(Timing.Category, Timing.Performance)]
    [InlineData(false, 408)]
    [InlineData(true, 0)]
    public void TheReadsOfOneFrameTakeLessThanTwoMilliseconds(bool eachPlaceHasAProblem, int buttons)
    {
        var m10 = Biggest();
        GameDataPaths.RequireBase();
        var items = RetailGame.Instance.Data.Items;
        // A place problem makes a new text, as a system or a base that the game data does not have does. Without a
        // problem, the Scene shows the system of each place, so a suggested Scene camera in a system can start too.
        Func<StartPlace, string?> problem = eachPlaceHasAProblem
            ? place => $"The start place names the system '{place.System}', which is not in the game data."
            : place => MissionPlay.PlaceProblem(items, place.System ?? "li01", place);
        var states = new PlayFromHereStates(problem, _ => { }, (pass, _) => Task.FromResult(pass()));
        states.Sync(m10, PlayStartSetting.Default, false);
        var graph = MissionGraph.Build(m10, new GraphOptions(GraphLens.Logic));
        var triggers = graph.Nodes.Count(n => n.Kind == GraphNodeKind.Trigger);
        var shown = 0;
        void Frame()
        {
            shown = 0;
            states.Sync(m10, PlayStartSetting.Default, false);
            foreach (var node in graph.Nodes)
            {
                if (states.Shows(node))
                    shown++;
            }
        }

        TimeSpan Median()
        {
            var times = new List<TimeSpan>();
            using (Timing.HighPriority())
            {
                for (var i = 0; i < 100; i++)
                {
                    var begin = Stopwatch.GetTimestamp();
                    Frame();
                    times.Add(Stopwatch.GetElapsedTime(begin));
                }
            }

            return times.Order().ElementAt(50);
        }

        Frame();
        var limit = TimeSpan.FromMilliseconds(2);
        var median = new TimingGates().Gate(limit, Median);

        output.WriteLine($"Reads of one frame for {triggers} trigger nodes: {median.TotalMilliseconds:0.000} ms, {shown} buttons.");
        Assert.Equal((408, buttons), (triggers, shown));
        Assert.True(median < limit, $"The reads of one frame took {median.TotalMilliseconds:0.000} ms.");
    }

    /// <summary>The states without the setup check agree with the full plans of Play: for every tenth trigger of M10 and
    /// M06, the state can start when the full plan can, and it has the place and the tooltip of the full plan. A full plan
    /// that refuses with a setup problem is the known limit of the buttons, so the test skips it. 408 of 408 triggers of
    /// M10 can start, and 181 of 333 of M06.</summary>
    [Theory]
    [InlineData("M10", 408, 408)]
    [InlineData("M06", 333, 181)]
    public void TheStatesAgreeWithTheFullPlans(string slot, int triggers, int canStart)
    {
        var analysis = RetailMissions.Slot(slot);
        var states = Pass(analysis);

        Assert.Equal((triggers, canStart), (states.Count, states.Values.Count(s => s.CanStart)));
        var compared = 0;
        foreach (var trigger in analysis.Index.Triggers.Where(t => !t.IsDuplicate).Where((_, i) => i % 10 == 0))
        {
            var full = StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.At(trigger.Nickname));
            if (full.Refusal == StartRefusal.SetupProblem)
                continue;
            var state = states[trigger.Nickname];
            Assert.Equal((full.CanStart, full.Place, full.Reason), (state.CanStart, state.Place, state.Reason));
            Assert.Equal(full.CanStart ? PlayFromHereStates.Tip(full, PlayStartSetting.Default) : "", state.Tip);
            compared++;
        }

        output.WriteLine($"{slot}: {compared} triggers compared.");
    }
}
