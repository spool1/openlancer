using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The replay rules of a play from a trigger (issue #46): the replay runs the waiting triggers that the path shows ran
/// in the game. The scripts are synthetic, and all names are invented.
/// </summary>
public sealed class StartReplayRulesTests
{
    private static StartReplay Replay(string text, string chosen) =>
        StartReplay.Run(Index(text), MissionStartPoint.At(chosen));

    private static List<string> Ran(StartReplay replay) =>
        replay.Changes.Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger.Nickname).ToList();

    private static List<string> Waiting(StartReplay replay) => replay.ActiveSet.Select(t => t.Nickname).ToList();

    /// <summary>
    /// A start that starts two timers: the shorter t_short and the longer t_long. t_long is on the path, so at least its
    /// time passed before the trigger after it. The shorter timer ran first in the game. So it does not wait at the start,
    /// and its run comes before the run of t_long. This is the first case of issue #46.
    /// </summary>
    [Fact]
    public void AShorterSiblingOfATimerOfThePathRunsBeforeIt()
    {
        const string text = """
            [Trigger]
            nickname = t_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = t_short
            Act_ActTrig = t_long

            [Trigger]
            nickname = t_short
            Cnd_Timer = 3
            Act_SpawnSolar = t_buoy

            [Trigger]
            nickname = t_long
            Cnd_Timer = 7
            Act_ActTrig = t_chosen

            [Trigger]
            nickname = t_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "t_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.DoesNotContain("t_short", Waiting(replay));
        Assert.Equal(["t_start", "t_short", "t_long"], Ran(replay));
    }

    /// <summary>
    /// Timers of the path that follow each other add their times. c_wait waits 10 s from the start, and the path timers
    /// c_first and c_second take at least 3 s and 7 s. So 10 s passed before the chosen trigger, and c_wait ran: a tie
    /// counts as a run. c_late waits 11 s, so it still waits. A range of a path timer counts with its shortest time, and
    /// a range of a waiting timer with its longest time.
    /// </summary>
    [Fact]
    public void PathTimersInAChainAddTheirTimesAndATieRuns()
    {
        const string text = """
            [Trigger]
            nickname = c_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = c_wait
            Act_ActTrig = c_late
            Act_ActTrig = c_range
            Act_ActTrig = c_first

            [Trigger]
            nickname = c_wait
            Cnd_Timer = 10

            [Trigger]
            nickname = c_late
            Cnd_Timer = 11

            [Trigger]
            nickname = c_range
            Cnd_Timer = 2, 10.5

            [Trigger]
            nickname = c_first
            Cnd_Timer = 3, 30
            Act_ActTrig = c_second

            [Trigger]
            nickname = c_second
            Cnd_Timer = 7
            Act_ActTrig = c_chosen

            [Trigger]
            nickname = c_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "c_chosen");

        Assert.DoesNotContain("c_wait", Waiting(replay));
        Assert.Contains("c_late", Waiting(replay));
        Assert.Contains("c_range", Waiting(replay));
        Assert.True(Ran(replay).IndexOf("c_wait") < Ran(replay).IndexOf("c_second"), string.Join(" ", Ran(replay)));
    }

    /// <summary>
    /// A path that jumps from n_sys_a to n_sys_b, then waits for ships to arrive with the player. Retail brings each
    /// jumper into the new system 3 s after the player. So the arrival of jumpers on the path shows 3 s after the
    /// trigger before the jump, and the 3 s timer ran. The 4 s timer still waits. A ship that is not a jumper gives no
    /// time, also when an Act_Jumper line of the path turns its flag off. A condition that also waits for a ship that is
    /// not a jumper gives no time either, because that ship can arrive later.
    /// </summary>
    [Theory]
    [InlineData("n_wing", "", true)]
    [InlineData("n_slow", "", false)]
    [InlineData("n_wing, n_slow", "", false)]
    [InlineData("n_wing", "Act_Jumper = n_wing, false", false)]
    [InlineData("n_slow", "Act_Jumper = n_slow, true", true)]
    public void AnArrivalOfJumpersOnThePathShowsThreeSeconds(string ships, string launch, bool runs)
    {
        var text = """
            [MsnShip]
            nickname = n_wing
            jumper = true

            [MsnShip]
            nickname = n_slow

            [Trigger]
            nickname = n_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = n_launch

            [Trigger]
            nickname = n_launch
            Cnd_SpaceEnter = n_sys_a
            {LAUNCH}
            Act_ActTrig = n_three
            Act_ActTrig = n_four
            Act_ActTrig = n_arrive

            [Trigger]
            nickname = n_three
            Cnd_Timer = 3

            [Trigger]
            nickname = n_four
            Cnd_Timer = 4

            [Trigger]
            nickname = n_arrive
            Cnd_SystemEnter = n_sys_b
            Act_ActTrig = n_ships_in

            [Trigger]
            nickname = n_ships_in
            Cnd_NPCSystemEnter = n_sys_b, {SHIPS}
            Act_ActTrig = n_chosen

            [Trigger]
            nickname = n_chosen
            Cnd_Timer = 5
            """.Replace("{SHIPS}", ships).Replace("{LAUNCH}", launch);

        var replay = Replay(text, "n_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(!runs, Waiting(replay).Contains("n_three"));
        Assert.Contains("n_four", Waiting(replay));
    }

    /// <summary>
    /// A run of a rule with a known time also shows time for the path. The path trigger k_done completes the objective
    /// k_goal, so the objective rule runs k_activate (2 s) before it. So k_done comes at least 2 s after the start, and
    /// the sibling timer k_sibling (2 s) ran too. Without the objective, the path shows no time, and both timers wait.
    /// Pass 2 runs k_sibling at the first timer call of the step, before the objective rule. The game runs both timers
    /// on the same heartbeat, so either order is right.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARunOfARuleWithAKnownTimeShowsTimeToo(bool completes)
    {
        var text = """
            [Trigger]
            nickname = k_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = k_activate
            Act_ActTrig = k_sibling
            Act_ActTrig = k_done

            [Trigger]
            nickname = k_activate
            Cnd_Timer = 2
            Act_SetNNObj = k_goal

            [Trigger]
            nickname = k_sibling
            Cnd_Timer = 2
            Act_SpawnSolar = k_buoy

            [Trigger]
            nickname = k_done
            Cnd_BaseEnter = k_base
            {COMPLETE}
            Act_ActTrig = k_chosen

            [Trigger]
            nickname = k_chosen
            Cnd_Timer = 5
            """.Replace("{COMPLETE}", completes ? "Act_SetNNState = k_goal, COMPLETE" : "");

        var replay = Replay(text, "k_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(completes, Ran(replay).Contains("k_activate"));
        Assert.Equal(!completes, Waiting(replay).Contains("k_sibling"));
        if (completes)
            Assert.Equal(["k_start", "k_sibling", "k_activate", "k_done"], Ran(replay));
    }

    /// <summary>
    /// Two waiting timers are due before the same trigger of the path. p_early started first, so it runs first, and its
    /// run stops p_late. In the game, p_early runs at 4 s and p_late at 5 s or later. The rule runs the timers in the
    /// order of their starts, not in the order of their times.
    /// </summary>
    [Fact]
    public void TheTimerOfTheEarlierStartRunsFirst()
    {
        const string text = """
            [Trigger]
            nickname = p_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = p_early
            Act_ActTrig = p_first

            [Trigger]
            nickname = p_early
            Cnd_Timer = 4
            Act_DeactTrig = p_late

            [Trigger]
            nickname = p_first
            Cnd_Timer = 3
            Act_ActTrig = p_late
            Act_ActTrig = p_second

            [Trigger]
            nickname = p_late
            Cnd_Timer = 2
            Act_DeactTrig = p_early

            [Trigger]
            nickname = p_second
            Cnd_Timer = 5
            Act_ActTrig = p_chosen

            [Trigger]
            nickname = p_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "p_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["p_start", "p_first", "p_early", "p_second"], Ran(replay));
        Assert.DoesNotContain("p_late", Waiting(replay));
        Assert.DoesNotContain("p_early", Waiting(replay));
    }

    /// <summary>
    /// The path trigger s_first stops s_stopped before its time passed. The game never ran s_stopped. Without the stop,
    /// the path shows 12 s before the chosen trigger, so the stop decides.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATimerThatThePathStopsBeforeItsTimeDoesNotRun(bool stops)
    {
        var text = """
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_stopped
            Act_ActTrig = s_first

            [Trigger]
            nickname = s_stopped
            Cnd_Timer = 10

            [Trigger]
            nickname = s_first
            Cnd_Timer = 3
            {STOP}
            Act_ActTrig = s_second

            [Trigger]
            nickname = s_second
            Cnd_Timer = 9
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 5
            """.Replace("{STOP}", stops ? "Act_DeactTrig = s_stopped" : "");

        var replay = Replay(text, "s_chosen");

        Assert.Equal(!stops, Ran(replay).Contains("s_stopped"));
        Assert.DoesNotContain("s_stopped", Waiting(replay));
    }

    /// <summary>
    /// A waiting trigger can stop the timer, but only an event of the player runs it. The rule still runs the timer (the
    /// one-way stopper case of issue #46). A timer whose run would stop the next trigger of the path does not run, and
    /// the replay goes back to the state before it.
    /// </summary>
    [Fact]
    public void AWaitingStopperDoesNotKeepATimerButARunAgainstThePathGoesBack()
    {
        const string text = """
            [Trigger]
            nickname = o_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = o_timer
            Act_ActTrig = o_stopper
            Act_ActTrig = o_breaker
            Act_ActTrig = o_wait

            [Trigger]
            nickname = o_timer
            Cnd_Timer = 3
            Act_SpawnSolar = o_buoy

            [Trigger]
            nickname = o_stopper
            Cnd_BaseEnter = o_base
            Act_DeactTrig = o_timer

            [Trigger]
            nickname = o_breaker
            Cnd_Timer = 2
            Act_DeactTrig = o_wait

            [Trigger]
            nickname = o_wait
            Cnd_Timer = 5
            Act_ActTrig = o_chosen

            [Trigger]
            nickname = o_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "o_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.DoesNotContain("o_timer", Waiting(replay));
        Assert.Contains("o_stopper", Waiting(replay));
        Assert.Contains("o_breaker", Waiting(replay));
        Assert.DoesNotContain("o_breaker", Ran(replay));
        Assert.Contains("o_wait", Ran(replay));
    }

    /// <summary>
    /// A timer that counts only in space does not count while the player is docked. The path docks the player after the
    /// start of i_space, so the path does not show its time, and it waits. The same timer without the token ran.
    /// </summary>
    [Theory]
    [InlineData("Cnd_Timer = 3, 3, RUN_IN_SPACE_ONLY", true)]
    [InlineData("Cnd_Timer = 3", false)]
    public void ATimerThatCountsOnlyInSpaceWaitsAfterADock(string condition, bool waits)
    {
        var text = """
            [Trigger]
            nickname = i_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = i_space
            Act_ActTrig = i_dock

            [Trigger]
            nickname = i_space
            {CONDITION}

            [Trigger]
            nickname = i_dock
            Cnd_BaseEnter = i_base
            Act_ActTrig = i_wait

            [Trigger]
            nickname = i_wait
            Cnd_Timer = 5
            Act_ActTrig = i_chosen

            [Trigger]
            nickname = i_chosen
            Cnd_Timer = 5
            """.Replace("{CONDITION}", condition);

        var replay = Replay(text, "i_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(waits, Waiting(replay).Contains("i_space"));
    }

    /// <summary>
    /// The path shows the 15 s of e_old only at e_second, so pass 1 runs it late, after the lines of e_first. Pass 2 runs
    /// it at its early step, before the base entry e_dock, because the path does not show that e_dock came first. So the
    /// objective line and the destroy line of e_first come after the lines of e_old, as in the game.
    /// </summary>
    [Fact]
    public void PassTwoRunsAnOldTimerAtItsEarlyStep()
    {
        const string text = """
            [Trigger]
            nickname = e_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = e_old
            Act_ActTrig = e_dock

            [Trigger]
            nickname = e_old
            Cnd_Timer = 15
            Act_SetNNObj = e_obj_old
            Act_SpawnSolar = e_buoy

            [Trigger]
            nickname = e_dock
            Cnd_BaseEnter = e_base
            Act_ActTrig = e_first

            [Trigger]
            nickname = e_first
            Cnd_Timer = 10
            Act_SetNNObj = e_obj_new
            Act_Destroy = e_buoy
            Act_ActTrig = e_second

            [Trigger]
            nickname = e_second
            Cnd_Timer = 10
            Act_ActTrig = e_chosen

            [Trigger]
            nickname = e_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "e_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["e_start", "e_old", "e_dock", "e_first", "e_second"], Ran(replay));
        Assert.Equal(["Act_SetNNObj = e_obj_old", "Act_SpawnSolar = e_buoy", "Act_SetNNObj = e_obj_new",
            "Act_Destroy = e_buoy"], replay.Effects.Select(e => $"{e.Line.Key.Trim()} = {e.Line.Value(0)}"));
    }

    /// <summary>
    /// Pass 1 runs r_timer before r_first. Then the base entry r_dock starts it again, and the path shows no time after
    /// that start. Pass 2 knows a timer by its name and its start. So the run for the first start does not run the timer
    /// again for the new start, and it waits at the start.
    /// </summary>
    [Fact]
    public void PassTwoDoesNotRunATimerOfANewStart()
    {
        const string text = """
            [Trigger]
            nickname = r_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = r_timer
            Act_ActTrig = r_first

            [Trigger]
            nickname = r_timer
            Cnd_Timer = 5
            Act_SpawnSolar = r_buoy

            [Trigger]
            nickname = r_first
            Cnd_Timer = 10
            Act_ActTrig = r_dock

            [Trigger]
            nickname = r_dock
            Cnd_BaseEnter = r_base
            Act_ActTrig = r_timer
            Act_ActTrig = r_comm

            [Trigger]
            nickname = r_comm
            Cnd_CommComplete = r_line
            Act_ActTrig = r_chosen

            [Trigger]
            nickname = r_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "r_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["r_start", "r_timer", "r_first", "r_dock", "r_comm"], Ran(replay));
        Assert.Contains("r_timer", Waiting(replay));
    }

    /// <summary>
    /// The path timer k_first takes at most 5 s, and k_old waits 15 s from the same start. So k_first is known to come
    /// before the time of k_old, and pass 2 runs k_old at the next step, before the base entry k_dock. When k_first can
    /// take 15 s, the path does not show that it came first, and k_old runs before it.
    /// </summary>
    [Theory]
    [InlineData("Cnd_Timer = 5", new[] { "k_start", "k_first", "k_old", "k_dock", "k_second" })]
    [InlineData("Cnd_Timer = 5, 15", new[] { "k_start", "k_old", "k_first", "k_dock", "k_second" })]
    public void APathTriggerThatIsKnownToComeBeforeKeepsTheTimerWaiting(string first, string[] ran)
    {
        var text = """
            [Trigger]
            nickname = k_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = k_old
            Act_ActTrig = k_first

            [Trigger]
            nickname = k_old
            Cnd_Timer = 15
            Act_SpawnSolar = k_buoy

            [Trigger]
            nickname = k_first
            {FIRST}
            Act_ActTrig = k_dock

            [Trigger]
            nickname = k_dock
            Cnd_BaseEnter = k_base
            Act_ActTrig = k_second

            [Trigger]
            nickname = k_second
            Cnd_Timer = 20
            Act_ActTrig = k_chosen

            [Trigger]
            nickname = k_chosen
            Cnd_Timer = 5
            """.Replace("{FIRST}", first);

        var replay = Replay(text, "k_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(ran, Ran(replay));
    }

    /// <summary>
    /// The first starts of the replay also start the time of the root. s_root (5 s) and s_old (12 s) are both in the
    /// start set, and s_root is the root of the path. So s_root is known to come before the time of s_old, and s_old
    /// runs after it, before s_next. So the objective line of s_old comes last, as in the game.
    /// </summary>
    [Fact]
    public void TheRootIsKnownToComeBeforeALongerTimerOfTheStartSet()
    {
        const string text = """
            [Trigger]
            nickname = s_root
            InitState = ACTIVE
            Cnd_Timer = 5
            Act_SetNNObj = s_obj_root
            Act_ActTrig = s_next

            [Trigger]
            nickname = s_old
            InitState = ACTIVE
            Cnd_Timer = 12
            Act_SetNNObj = s_obj_old

            [Trigger]
            nickname = s_next
            Cnd_Timer = 10
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "s_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["s_root", "s_old", "s_next"], Ran(replay));
    }

    /// <summary>
    /// The path timer q_done takes at most 2 s, so its own time is known to come before the 10 s of q_old. But q_done
    /// completes the objective q_goal, so the objective rule runs q_activate (10 s) before it. So q_done comes at least
    /// 10 s after the start, and pass 1 runs q_old before q_done. Pass 2 never runs a timer at a later trigger of the
    /// path than pass 1. So q_old does not wait at the start.
    /// </summary>
    [Fact]
    public void PassTwoNeverRunsATimerAtALaterTriggerThanPassOne()
    {
        const string text = """
            [Trigger]
            nickname = q_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = q_activate
            Act_ActTrig = q_old
            Act_ActTrig = q_done

            [Trigger]
            nickname = q_activate
            Cnd_Timer = 10
            Act_SetNNObj = q_goal

            [Trigger]
            nickname = q_old
            Cnd_Timer = 10
            Act_SpawnSolar = q_buoy

            [Trigger]
            nickname = q_done
            Cnd_Timer = 2
            Act_SetNNState = q_goal, COMPLETE
            Act_ActTrig = q_chosen

            [Trigger]
            nickname = q_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "q_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["q_start", "q_old", "q_activate", "q_done"], Ran(replay));
        Assert.DoesNotContain("q_old", Waiting(replay));
    }

    /// <summary>
    /// The timeline keeps the newest start of each trigger as the machine does. A start of an active trigger keeps the
    /// old start. A stop, then a start in one action list gives a new start. A start, then a stop in one action list
    /// gives no start. Two timers of the path in a chain give the sum of their shortest times.
    /// </summary>
    [Fact]
    public void TheTimelineKeepsTheStartsAsTheMachineDoes()
    {
        var index = Index("""
            [Trigger]
            nickname = l_root
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = l_timer
            Act_ActTrig = l_first

            [Trigger]
            nickname = l_timer
            Cnd_Timer = 20

            [Trigger]
            nickname = l_again
            Cnd_True = no_params
            Act_ActTrig = l_timer

            [Trigger]
            nickname = l_restart
            Cnd_True = no_params
            Act_DeactTrig = l_timer
            Act_ActTrig = l_timer

            [Trigger]
            nickname = l_cancel
            Cnd_True = no_params
            Act_ActTrig = l_other
            Act_DeactTrig = l_other

            [Trigger]
            nickname = l_other
            Cnd_Timer = 1

            [Trigger]
            nickname = l_first
            Cnd_Timer = 3, 6
            Act_ActTrig = l_second

            [Trigger]
            nickname = l_second
            Cnd_Timer = 7
            Act_ActTrig = l_next

            [Trigger]
            nickname = l_next
            Cnd_BaseEnter = l_base
            """);
        var program = MachineProgram.Of(index);
        var trigger = (string name) => index.Trigger(name)!;
        var timeline = new StartTimeline(program, program.StartSet);
        var changes = new List<MachineChange>();
        void Run(string name)
        {
            changes.Add(new MachineChange(MachineChangeKind.Ran, trigger(name)));
            timeline.Sync(changes);
        }

        Run("l_root");
        timeline.MarkPath(0, trigger("l_root"));
        Run("l_again");
        Assert.True(timeline.Started(trigger("l_timer"), out var start));
        Assert.Equal(0, start);
        Run("l_restart");
        Assert.True(timeline.Started(trigger("l_timer"), out start));
        Assert.Equal(2, start);
        // The source of a start names the trigger of the run and the number of that run, not the index of the run.
        Assert.Equal((program.SlotOf("l_restart"), 1), timeline.SourceOf(start));
        Assert.Equal((-1, 0), timeline.SourceOf(StartTimeline.First));
        // The time of a start begins at a run of the path, or before the root for the first starts. A run of another
        // trigger gives no position.
        Assert.Equal(0, timeline.PositionOf(0));
        Assert.Equal(-1, timeline.PositionOf(StartTimeline.First));
        Assert.Null(timeline.PositionOf(start));
        Run("l_cancel");
        Assert.False(timeline.Started(trigger("l_other"), out _));

        Run("l_first");
        timeline.MarkPath(1, trigger("l_first"));
        Run("l_second");
        timeline.MarkPath(2, trigger("l_second"));
        // 3 s and 7 s from the run of the root, in heartbeats.
        Assert.True(timeline.Started(trigger("l_next"), out _));
        Assert.Equal(300, timeline.Bound(0, 3, trigger("l_next")));
        // The restart of l_timer comes before the run of l_first, but after its start. So only the 7 s of l_second
        // count from the restart.
        Assert.Equal(210, timeline.Bound(2, 3, trigger("l_next")));

        // When the replay goes back, the timeline goes back with it.
        changes.RemoveRange(3, changes.Count - 3);
        timeline.Cut(3);
        Assert.False(timeline.Started(trigger("l_next"), out _));
        Assert.True(timeline.Started(trigger("l_first"), out start));
        Assert.Equal(0, start);
        Assert.True(timeline.Started(trigger("l_timer"), out start));
        Assert.Equal(2, start);
        // The cut also removes the numbers of the runs, so the next run of l_first is its first run again.
        Run("l_first");
        Assert.True(timeline.Started(trigger("l_second"), out start));
        Assert.Equal((program.SlotOf("l_first"), 1), timeline.SourceOf(start));
    }
}
