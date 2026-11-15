using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    private static StartReplay Replay(string text, string chosen, MissionWorld? world = null) =>
        StartReplay.Run(Index(text, world), MissionStartPoint.At(chosen));

    /// <summary>A world with some objects of the system files and the system of each base. It knows the systems of the
    /// objects and of the bases, and the bases.</summary>
    private static MissionWorld World(IReadOnlyList<WorldObject> objects, params (string Base, string System)[] bases) =>
        new(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] =
                objects.Select(o => o.System).Concat(bases.Select(b => b.System)).Distinct().ToList(),
            [MissionValueKind.Base] = bases.Select(b => b.Base).ToList(),
        }, null, bases.ToDictionary(b => b.Base, b => b.System), objects);

    /// <summary>An object of the system files at a point on the x axis, with the system that its jump leads to or
    /// null.</summary>
    private static WorldObject Thing(string name, string system, float x, string? jumpTo = null) =>
        new(name, system, new Vector3(x, 0, 0), Quaternion.Identity, jumpTo);

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
    /// A Cnd_True trigger of the path runs at once, so it adds no time. b_hop comes between the start and the path timer
    /// b_first (5 s). So b_first is known to come before the 15 s of b_old, and b_old runs at the next step, before the
    /// base entry b_dock.
    /// </summary>
    [Fact]
    public void ACndTrueTriggerOfThePathAddsNoTime()
    {
        const string text = """
            [Trigger]
            nickname = b_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = b_old
            Act_ActTrig = b_hop

            [Trigger]
            nickname = b_old
            Cnd_Timer = 15
            Act_SpawnSolar = b_buoy

            [Trigger]
            nickname = b_hop
            Cnd_True = no_params
            Act_ActTrig = b_first

            [Trigger]
            nickname = b_first
            Cnd_Timer = 5
            Act_ActTrig = b_dock

            [Trigger]
            nickname = b_dock
            Cnd_BaseEnter = b_base
            Act_ActTrig = b_last

            [Trigger]
            nickname = b_last
            Cnd_Timer = 20
            Act_ActTrig = b_chosen

            [Trigger]
            nickname = b_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "b_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["b_start", "b_hop", "b_first", "b_old", "b_dock", "b_last"], Ran(replay));
    }

    /// <summary>
    /// The objective rule runs c_activate before the path trigger c_first, and c_activate starts c_old. The game can run
    /// such a trigger earlier than the replay does. So the step of its run does not show when the time of c_old starts,
    /// and c_first is not known to come first. Pass 2 runs c_old at the first timer call after its start.
    /// </summary>
    [Fact]
    public void ARunOfARuleDoesNotShowWhereTheTimeOfATimerStarts()
    {
        const string text = """
            [Trigger]
            nickname = c_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = c_activate
            Act_ActTrig = c_first

            [Trigger]
            nickname = c_activate
            Cnd_BaseEnter = c_base
            Act_SetNNObj = c_goal
            Act_ActTrig = c_old

            [Trigger]
            nickname = c_old
            Cnd_Timer = 15
            Act_SpawnSolar = c_buoy

            [Trigger]
            nickname = c_first
            Cnd_Timer = 5
            Act_SetNNState = c_goal, COMPLETE
            Act_ActTrig = c_dock

            [Trigger]
            nickname = c_dock
            Cnd_BaseEnter = c_base_two
            Act_ActTrig = c_last

            [Trigger]
            nickname = c_last
            Cnd_Timer = 20
            Act_ActTrig = c_chosen

            [Trigger]
            nickname = c_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "c_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["c_start", "c_activate", "c_old", "c_first", "c_dock", "c_last"], Ran(replay));
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
    /// The Cnd_True trigger a_starter runs two times: on the path, and again when a_dock starts it. Each run starts
    /// a_timer. Pass 1 runs a_timer for the first start only, because the path shows no time after the second start. The
    /// start of a timer holds the number of the run of its starter, so pass 2 does not run a_timer for the second start.
    /// </summary>
    [Fact]
    public void PassTwoKnowsTheRunOfTheStarterOfATimer()
    {
        const string text = """
            [Trigger]
            nickname = a_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = a_starter

            [Trigger]
            nickname = a_starter
            Cnd_True = no_params
            Act_ActTrig = a_timer
            Act_ActTrig = a_first

            [Trigger]
            nickname = a_timer
            Cnd_Timer = 5
            Act_SpawnSolar = a_buoy

            [Trigger]
            nickname = a_first
            Cnd_Timer = 10
            Act_ActTrig = a_dock

            [Trigger]
            nickname = a_dock
            Cnd_BaseEnter = a_base
            Act_ActTrig = a_starter
            Act_ActTrig = a_comm

            [Trigger]
            nickname = a_comm
            Cnd_CommComplete = a_line
            Act_ActTrig = a_chosen

            [Trigger]
            nickname = a_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "a_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["a_start", "a_starter", "a_timer", "a_first", "a_dock", "a_starter", "a_comm"], Ran(replay));
        Assert.Contains("a_timer", Waiting(replay));
    }

    /// <summary>
    /// Pass 1 runs d_old before d_last. Pass 2 tries it at its early step, before the base entry d_dock. There its run
    /// starts d_link, which runs the path trigger d_hop before its point, so the replay goes back. Pass 2 tries the timer
    /// again in the next timer call, and it runs before d_last.
    /// </summary>
    [Fact]
    public void PassTwoTriesATimerAgainAfterARunThatWentBack()
    {
        const string text = """
            [Trigger]
            nickname = d_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = d_old
            Act_ActTrig = d_dock

            [Trigger]
            nickname = d_old
            Cnd_Timer = 15
            Act_ActTrig = d_link

            [Trigger]
            nickname = d_link
            Cnd_True = no_params
            Act_ActTrig = d_hop

            [Trigger]
            nickname = d_dock
            Cnd_BaseEnter = d_base
            Act_ActTrig = d_hop

            [Trigger]
            nickname = d_hop
            Cnd_True = no_params
            Act_ActTrig = d_last

            [Trigger]
            nickname = d_last
            Cnd_Timer = 20
            Act_ActTrig = d_chosen

            [Trigger]
            nickname = d_chosen
            Cnd_Timer = 5
            """;

        var replay = Replay(text, "d_chosen");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["d_start", "d_dock", "d_hop", "d_old", "d_link", "d_hop", "d_last"], Ran(replay));
        Assert.DoesNotContain("d_old", Waiting(replay));
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

    /// <summary>
    /// The game tests a distance condition of the player at each heartbeat (issue #50). The path docks the player at
    /// w_base. When its dock object is far outside the sphere of w_poll, the player left that sphere before the dock, so
    /// w_poll ran before it. Each point within 3,000 units of the dock object counts. So a dock object 3,500 units from
    /// the center does not show the player outside 1,000 units, and w_poll waits. The poll w_held waits in both cases,
    /// because one place does not show how long the player stayed outside.
    /// </summary>
    [Theory]
    [InlineData(20000, true)]
    [InlineData(3500, false)]
    public void APathDockFarOutsideTheSphereOfAPollRunsThePoll(float dock, bool runs)
    {
        const string text = """
            [Trigger]
            nickname = w_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = w_poll
            Act_ActTrig = w_held
            Act_ActTrig = w_dock

            [Trigger]
            nickname = w_poll
            Cnd_DistVec = outside, Player, 0, 0, 0, 1000

            [Trigger]
            nickname = w_held
            Cnd_DistVec = outside, Player, 0, 0, 0, 1000, 5

            [Trigger]
            nickname = w_dock
            Cnd_BaseEnter = w_base
            Act_ActTrig = w_chosen

            [Trigger]
            nickname = w_chosen
            Cnd_Timer = 5
            """;
        var world = World([Thing("w_station", "w_sys", dock) with { DockWith = "w_base" }], ("w_base", "w_sys"));

        var replay = Replay(text, "w_chosen", world);

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(runs ? ["w_start", "w_poll", "w_dock"] : ["w_start", "w_dock"], Ran(replay));
        Assert.Equal(!runs, Waiting(replay).Contains("w_poll"));
        Assert.Contains("w_held", Waiting(replay));
    }

    /// <summary>
    /// A launch from a base and a room entry also show the player at the dock object of the base, with 3,000 units. A
    /// dock object 20,000 units from the center of the outside poll x_poll shows the player outside its 1,000 units, so
    /// x_poll ran. At 4,000 units, the nearest point within the slack is exactly 1,000 units from the center. That point
    /// is not outside the sphere, so x_poll waits.
    /// </summary>
    [Theory]
    [InlineData("Cnd_BaseExit = x_base", 20000, true)]
    [InlineData("Cnd_BaseExit = x_base", 4000, false)]
    [InlineData("Cnd_LocEnter = x_bar, x_base", 20000, true)]
    [InlineData("Cnd_LocEnter = x_bar, x_base", 4000, false)]
    public void ALaunchOrARoomEntryShowsTheDockObjectOfTheBase(string place, float dock, bool runs)
    {
        var text = """
            [Trigger]
            nickname = x_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = x_poll
            Act_ActTrig = x_place

            [Trigger]
            nickname = x_poll
            Cnd_DistVec = outside, Player, 0, 0, 0, 1000

            [Trigger]
            nickname = x_place
            {PLACE}
            Act_ActTrig = x_chosen

            [Trigger]
            nickname = x_chosen
            Cnd_Timer = 5
            """.Replace("{PLACE}", place);
        var world = World([Thing("x_station", "x_sys", dock) with { DockWith = "x_base" }], ("x_base", "x_sys"));

        var replay = Replay(text, "x_chosen", world);

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(runs ? ["x_start", "x_poll", "x_place"] : ["x_start", "x_place"], Ran(replay));
    }

    /// <summary>
    /// A launch of the path runs the waiting trigger l_peer, which waits for the same launch. The run of l_peer starts
    /// the outside poll l_poll. The poll rule comes after the launch rule. So the launch also shows the player at the
    /// dock object, 20,000 units from the center of l_poll, and l_poll ran before the launch of the path.
    /// </summary>
    [Fact]
    public void APollThatTheLaunchRuleStartsRunsAtTheBaseOfTheLaunch()
    {
        const string text = """
            [Trigger]
            nickname = l_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = l_peer
            Act_ActTrig = l_launch

            [Trigger]
            nickname = l_peer
            Cnd_BaseExit = l_base
            Act_ActTrig = l_poll

            [Trigger]
            nickname = l_poll
            Cnd_DistVec = outside, Player, 0, 0, 0, 1000

            [Trigger]
            nickname = l_launch
            Cnd_BaseExit = l_base
            Act_ActTrig = l_chosen

            [Trigger]
            nickname = l_chosen
            Cnd_Timer = 5
            """;
        var world = World([Thing("l_station", "l_sys", 20000) with { DockWith = "l_base" }], ("l_base", "l_sys"));

        var replay = Replay(text, "l_chosen", world);

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["l_start", "l_peer", "l_poll", "l_launch"], Ran(replay));
    }

    /// <summary>
    /// The game tests the point of Cnd_DistVec in each system, but Cnd_DistShip needs the player and the object in one
    /// system. The path docks the player at b_base in b_sys_a. So the point poll b_vec ran, although its system key names
    /// another system. The object poll b_same ran, with the player as the other ship. The object poll b_other waits,
    /// because its object is in b_sys_b. The object poll b_script waits, because b_rock_s is also an object of the
    /// script. The replay does not know where the script puts such an object.
    /// </summary>
    [Fact]
    public void AnObjectPollNeedsTheSameSystemAndAPointPollCountsInEachSystem()
    {
        const string text = """
            [MsnSolar]
            nickname = b_rock_s

            [Trigger]
            nickname = b_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = b_vec
            Act_ActTrig = b_same
            Act_ActTrig = b_other
            Act_ActTrig = b_script
            Act_ActTrig = b_dock

            [Trigger]
            nickname = b_vec
            system = b_sys_b
            Cnd_DistVec = outside, Player, 50000, 0, 0, 1000

            [Trigger]
            nickname = b_same
            Cnd_DistShip = outside, b_rock_a, Player, 1000

            [Trigger]
            nickname = b_other
            Cnd_DistShip = outside, Player, b_rock_b, 1000

            [Trigger]
            nickname = b_script
            Cnd_DistShip = outside, Player, b_rock_s, 1000

            [Trigger]
            nickname = b_dock
            Cnd_BaseEnter = b_base
            Act_ActTrig = b_chosen

            [Trigger]
            nickname = b_chosen
            Cnd_Timer = 5
            """;
        var world = World(
        [
            Thing("b_station", "b_sys_a", 0) with { DockWith = "b_base" }, Thing("b_rock_a", "b_sys_a", 50000),
            Thing("b_rock_b", "b_sys_b", 50000), Thing("b_rock_s", "b_sys_a", 50000),
        ], ("b_base", "b_sys_a"));

        var replay = Replay(text, "b_chosen", world);

        Assert.Equal(StartRefusal.None, replay.Refusal);
        // The polls run in the order of the active list of the machine, before the dock.
        Assert.Equal("b_dock", Ran(replay)[^1]);
        Assert.Equal(["b_dock", "b_same", "b_start", "b_vec"], Ran(replay).Order().ToList());
        Assert.Contains("b_other", Waiting(replay));
        Assert.Contains("b_script", Waiting(replay));
    }

    /// <summary>
    /// A jump of the path from c_sys_a to c_sys_b. Before the exit, the player was at the jump object of c_sys_a that
    /// leads to c_sys_b. The system c_sys_a has two such objects: the gate at 0 and the hole at 40,000. The dock rules
    /// of the replay must allow exactly one of them. The hole shows the player inside the sphere of c_poll, and the gate
    /// does not. When the dock rules allow both objects or neither object, the place is not known, and c_poll waits. Only
    /// the newest Act_PlayerCanDock line counts.
    /// </summary>
    [Theory]
    [InlineData("Act_PlayerCanDock = false, c_hole", true)]
    [InlineData("Act_LockDock = Player, c_gate, lock", true)]
    [InlineData("Act_PlayerCanDock = false, c_gate", false)]
    [InlineData("Act_PlayerCanDock = true", false)]
    [InlineData("Act_PlayerCanDock = false, c_hole\nAct_LockDock = Player, c_hole, lock", false)]
    [InlineData("Act_LockDock = Player, c_gate, lock\nAct_LockDock = Player, c_gate, unlock", false)]
    [InlineData("Act_PlayerCanDock = false, c_gate\nAct_PlayerCanDock = false, c_hole", true)]
    [InlineData("Act_PlayerCanDock = false, c_hole\nAct_PlayerCanDock = false, c_gate", false)]
    public void TheDockRulesPickTheJumpObjectOfADeparture(string rules, bool runs)
    {
        var world = World(
        [
            Thing("c_gate", "c_sys_a", 0, "c_sys_b"), Thing("c_hole", "c_sys_a", 40000, "c_sys_b"),
            Thing("c_back", "c_sys_b", -90000, "c_sys_a"),
        ]);

        var replay = Replay(DepartureScript.Replace("{RULES}", rules), "c_chosen", world);

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(!runs, Waiting(replay).Contains("c_poll"));
    }

    /// <summary>
    /// The same jump, but c_sys_a has only one jump object to c_sys_b: the hole. So the player jumped through the hole,
    /// also when the dock rules of the replay block it now. The hole shows the player inside the sphere of c_poll, and
    /// c_poll ran.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Act_PlayerCanDock = false")]
    [InlineData("Act_PlayerCanDock = false, c_gate")]
    [InlineData("Act_LockDock = Player, c_hole, lock")]
    public void TheOnlyJumpObjectOfADepartureShowsThePlayerAlsoWhenTheDockRulesBlockIt(string rules)
    {
        var world = World([Thing("c_hole", "c_sys_a", 40000, "c_sys_b"), Thing("c_back", "c_sys_b", -90000, "c_sys_a")]);

        var replay = Replay(DepartureScript.Replace("{RULES}", rules), "c_chosen", world);

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.DoesNotContain("c_poll", Waiting(replay));
    }

    /// <summary>A path with a jump from c_sys_a to c_sys_b. The text {RULES} marks the place of the dock rules before the
    /// jump. The inside poll c_poll waits around the point 40,000 on the x axis.</summary>
    private const string DepartureScript = """
        [Trigger]
        nickname = c_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = c_poll
        Act_ActTrig = c_launch

        [Trigger]
        nickname = c_poll
        Cnd_DistVec = inside, Player, 40000, 0, 0, 2000

        [Trigger]
        nickname = c_launch
        Cnd_SpaceEnter = c_sys_a
        {RULES}
        Act_ActTrig = c_arrive

        [Trigger]
        nickname = c_arrive
        Cnd_SystemEnter = c_sys_b
        Act_ActTrig = c_chosen

        [Trigger]
        nickname = c_chosen
        Cnd_Timer = 5
        """;

    /// <summary>
    /// The jump places of a jump of the path from h_sys_a to h_sys_b. Before the exit, the player was at h_out, the only
    /// object of h_sys_a that leads to h_sys_b. So h_leave ran before the exit watcher h_exit. After the entry, the
    /// player was at h_in, the only object of h_sys_b that leads back. So h_come ran after the entry watcher h_entry.
    /// When h_sys_b has a second object that leads back, the arrival place is not known, and h_come waits.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AJumpGivesAPlaceBeforeTheExitAndAPlaceAfterTheEntry(bool twoWaysBack)
    {
        const string text = """
            [Trigger]
            nickname = h_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = h_leave
            Act_ActTrig = h_come
            Act_ActTrig = h_exit
            Act_ActTrig = h_entry
            Act_ActTrig = h_launch

            [Trigger]
            nickname = h_leave
            Cnd_DistVec = inside, Player, 50000, 0, 0, 2000

            [Trigger]
            nickname = h_come
            Cnd_DistVec = inside, Player, 0, 0, 0, 2000

            [Trigger]
            nickname = h_exit
            Cnd_SystemExit = h_sys_a

            [Trigger]
            nickname = h_entry
            Cnd_SystemEnter = h_sys_b

            [Trigger]
            nickname = h_launch
            Cnd_SpaceEnter = h_sys_a
            Act_ActTrig = h_arrive

            [Trigger]
            nickname = h_arrive
            Cnd_SystemEnter = h_sys_b
            Act_ActTrig = h_chosen

            [Trigger]
            nickname = h_chosen
            Cnd_Timer = 5
            """;
        List<WorldObject> objects = [Thing("h_out", "h_sys_a", 50000, "h_sys_b"), Thing("h_in", "h_sys_b", 0, "h_sys_a")];
        if (twoWaysBack)
            objects.Add(Thing("h_in_2", "h_sys_b", -90000, "h_sys_a"));

        var replay = Replay(text, "h_chosen", World(objects));

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(twoWaysBack
            ? ["h_start", "h_launch", "h_leave", "h_exit", "h_entry", "h_arrive"]
            : ["h_start", "h_launch", "h_leave", "h_exit", "h_entry", "h_come", "h_arrive"], Ran(replay));
    }

    /// <summary>
    /// A path condition of an entry into g_sys_b from g_sys_a shows the player at g_back, the only object of g_sys_b
    /// that leads to g_sys_a. So the poll g_poll near g_back ran. An entry from ANY or from no named system does not tell
    /// the object. When g_sys_b has two objects that lead to g_sys_a, the place is not known either.
    /// </summary>
    [Theory]
    [InlineData(", g_sys_a", false, true)]
    [InlineData(", ANY", false, false)]
    [InlineData("", false, false)]
    [InlineData(", g_sys_a", true, false)]
    public void AnEntryFromANamedSystemShowsTheJumpObjectThatLeadsBack(string from, bool twoWaysBack, bool runs)
    {
        var text = """
            [Trigger]
            nickname = g_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = g_poll
            Act_ActTrig = g_in

            [Trigger]
            nickname = g_poll
            Cnd_DistVec = inside, Player, 0, 0, 0, 2000

            [Trigger]
            nickname = g_in
            Cnd_SystemEnter = g_sys_b{FROM}
            Act_ActTrig = g_chosen

            [Trigger]
            nickname = g_chosen
            Cnd_Timer = 5
            """.Replace("{FROM}", from);
        List<WorldObject> objects = [Thing("g_back", "g_sys_b", 0, "g_sys_a"), Thing("g_far", "g_sys_a", 0, "g_sys_b")];
        if (twoWaysBack)
            objects.Add(Thing("g_back_2", "g_sys_b", -90000, "g_sys_a"));

        var replay = Replay(text, "g_chosen", World(objects));

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(!runs, Waiting(replay).Contains("g_poll"));
    }

    /// <summary>
    /// A path condition that waits for the player inside a sphere shows the player within its radius of its center. The
    /// sphere of d_near has a radius of 500. So the inside poll d_in ran: its center is 100 units away, and 100 + 500 is
    /// less than its radius of 1,000. The inside poll d_edge waits: 600 + 500 is more than 1,000. The inside poll d_tie
    /// also waits: 500 + 500 is exactly 1,000, and each point within the slack must be inside the sphere. The outside
    /// poll d_out ran: 5,000 - 500 is more than 1,000. A Cnd_DistShip of the player and an object of the system files
    /// gives the same place.
    /// </summary>
    [Theory]
    [InlineData("Cnd_DistVec = inside, Player, 0, 0, 0, 500")]
    [InlineData("Cnd_DistShip = inside, d_buoy, Player, 500")]
    public void AnInsideConditionOfThePathShowsThePlayerWithinItsRadius(string near)
    {
        var text = """
            [Trigger]
            nickname = d_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = d_in
            Act_ActTrig = d_edge
            Act_ActTrig = d_tie
            Act_ActTrig = d_out
            Act_ActTrig = d_near

            [Trigger]
            nickname = d_in
            Cnd_DistVec = inside, Player, 100, 0, 0, 1000

            [Trigger]
            nickname = d_edge
            Cnd_DistVec = inside, Player, 600, 0, 0, 1000

            [Trigger]
            nickname = d_tie
            Cnd_DistVec = inside, Player, 500, 0, 0, 1000

            [Trigger]
            nickname = d_out
            Cnd_DistVec = outside, Player, 5000, 0, 0, 1000

            [Trigger]
            nickname = d_near
            {NEAR}
            Act_ActTrig = d_chosen

            [Trigger]
            nickname = d_chosen
            Cnd_Timer = 5
            """.Replace("{NEAR}", near);

        var replay = Replay(text, "d_chosen", World([Thing("d_buoy", "d_sys", 0)]));

        Assert.Equal(StartRefusal.None, replay.Refusal);
        // The polls run in the order of the active list of the machine, before d_near.
        Assert.Equal("d_near", Ran(replay)[^1]);
        Assert.Equal(["d_in", "d_near", "d_out", "d_start"], Ran(replay).Order().ToList());
        Assert.Contains("d_edge", Waiting(replay));
        Assert.Contains("d_tie", Waiting(replay));
    }

    /// <summary>
    /// A lane entry or a lane exit of the player shows the player within 1,000 units of the ring. The outside poll f_far
    /// ran: its center is 3,000 units from the ring, and 3,000 - 1,000 is more than its radius of 1,500. The outside poll
    /// f_edge waits: 2,500 - 1,000 is not more than 1,500. A lane entry of another ship does not show the place of the
    /// player. A lane exit at ANY ring does not tell the ring.
    /// </summary>
    [Theory]
    [InlineData("Cnd_TLEntered = Player, f_ring_1, f_ring_2", true)]
    [InlineData("Cnd_TLExited = Player, f_ring_1, f_ring_2", true)]
    [InlineData("Cnd_TLExited = Player, f_ring_1", true)]
    [InlineData("Cnd_TLEntered = f_wing, f_ring_1, f_ring_2", false)]
    [InlineData("Cnd_TLExited = Player, ANY", false)]
    public void ALaneEntryOrExitOfThePlayerShowsThePlayerNearTheRing(string lane, bool runs)
    {
        var text = """
            [Trigger]
            nickname = f_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = f_far
            Act_ActTrig = f_edge
            Act_ActTrig = f_lane

            [Trigger]
            nickname = f_far
            Cnd_DistVec = outside, Player, 3000, 0, 0, 1500

            [Trigger]
            nickname = f_edge
            Cnd_DistVec = outside, Player, 2500, 0, 0, 1500

            [Trigger]
            nickname = f_lane
            {LANE}
            Act_ActTrig = f_chosen

            [Trigger]
            nickname = f_chosen
            Cnd_Timer = 5
            """.Replace("{LANE}", lane);
        var world = World([Thing("f_ring_1", "f_sys", 0), Thing("f_ring_2", "f_sys", 20000)]);

        var replay = Replay(text, "f_chosen", world);

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(!runs, Waiting(replay).Contains("f_far"));
        Assert.Contains("f_edge", Waiting(replay));
    }
}
