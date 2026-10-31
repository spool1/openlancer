using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The start plan of a play from a trigger (section 5.3 of the M6c design, build step 2.2): the replay, the active set,
/// the story state, the start place and the setup triggers. The scripts are synthetic, and all names are invented.
/// </summary>
public sealed class StartPlanTests
{
    /// <summary>A slot of the tests: start stage 4, accept stage 5.</summary>
    internal static readonly StorySlot Slot = new("M99", "Mission_99", @"MISSIONS\TEST\test.ini", 4, 5,
        new NextMission(NextMissionKind.AtOnce, 0), 0, 0, []);

    /// <summary>
    /// The offer at a base, the accept, the launch with two spawns, a kill that a later trigger waits for, a base scene
    /// and the chosen timer. A checkpoint of the launch restores at the next launch with a place line. A fail watcher
    /// waits beside the kill. One timer has no path.
    /// </summary>
    internal const string Script = """
        [Mission]

        [MsnShip]
        nickname = ol_ship_a
        system = ol_sys_a
        position = 100, 0, 200

        [MsnShip]
        nickname = ol_ship_c
        system = ol_sys_a
        position = 300, 0, 400

        [NNObjective]
        nickname = ol_obj_1
        type = ids, 500100

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_offer

        [Trigger]
        nickname = t_offer
        Cnd_BaseEnter = ol_base_a
        Act_SetTitle = 500001
        Act_SendComm = Player, Player, ol_line_1
        Act_ActTrig = t_accept

        [Trigger]
        nickname = t_accept
        Cnd_MsnResponse = accept
        Act_SetRep = Player, ol_faction_a, -0.9
        Act_ActTrig = t_launch

        [Trigger]
        nickname = t_launch
        Cnd_SpaceEnter = ol_sys_a
        Act_SpawnShip = ol_ship_a
        Act_SpawnShip = ol_ship_c
        Act_PlayMusic = ol_music_1
        Act_ActTrig = t_kill
        Act_ActTrig = t_fail
        Act_Save = t_restore, 1

        [Trigger]
        nickname = t_kill
        Cnd_Destroyed = ol_ship_a
        Act_PlayMusic = ol_music_2
        Act_SetNNObj = ol_obj_1
        Act_AddRTC = missions\ol\ol_scene.ini
        Act_AddRTC = missions\ol\ol_other.ini
        Act_ActTrig = t_scene

        [Trigger]
        nickname = t_scene
        Cnd_RTCDone = missions\ol\ol_scene.ini
        Act_ActTrig = t_chosen

        [Trigger]
        nickname = t_fail
        Cnd_Destroyed = ol_ship_c
        Act_ChangeState = FAIL, 1

        [Trigger]
        nickname = t_chosen
        Cnd_Timer = 5
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_restore
        Cnd_PlayerLaunch = no_params
        Act_SetInitialPlayerPos = 10, 20, 30, 1, 0, 0, 0
        Act_ActTrig = t_fail

        [Trigger]
        nickname = t_orphan
        Cnd_Timer = 2
        """;

    private static StartPlan Plan(string text, string? trigger, StartPlaceChoice place = StartPlaceChoice.Suggested,
        string? dock = null) =>
        StartPlan.Make(Index(text), Slot, trigger == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(trigger),
            place, dock);

    /// <summary>The lines of a setup trigger as text.</summary>
    private static List<string> Lines(StartPlan plan, int setup) =>
        plan.Setup[setup].Lines.Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

    /// <summary>
    /// A path with a launch and a dock, and waiting triggers that the path shows ran: a launch watcher, a Cnd_InSpace
    /// trigger, and a timer that leads to the objective that the dock completes. A watcher of a launch in another system,
    /// and a watcher that the launch itself starts, wait for a later launch.
    /// </summary>
    private const string Waiting = """
        [Trigger]
        nickname = r_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_SetNNObj = r_obj_land
        Act_ActTrig = r_draw
        Act_ActTrig = r_plch
        Act_ActTrig = r_other_launch
        Act_ActTrig = r_launch
        {START}

        [Trigger]
        nickname = r_draw
        Cnd_InSpace = true
        Act_NNPath = 1, 1, r_base, r_sys_a

        [Trigger]
        nickname = r_plch
        Cnd_PlayerLaunch = no_params
        Act_SpawnSolar = r_station

        [Trigger]
        nickname = r_other_launch
        Cnd_SpaceEnter = r_sys_b
        Act_SpawnSolar = r_tower

        [Trigger]
        nickname = r_launch
        system = r_sys_a
        Cnd_SpaceEnter = no_params
        Act_SetNNObj = r_obj_fly
        Act_ActTrig = r_delay
        Act_ActTrig = r_after_launch
        Act_ActTrig = r_dock

        [Trigger]
        nickname = r_after_launch
        Cnd_PlayerLaunch = no_params

        [Trigger]
        nickname = r_delay
        Cnd_Timer = 3
        Act_ActTrig = r_switch

        [Trigger]
        nickname = r_switch
        Cnd_DistShip = outside, Player, r_ring, 8000
        Act_SetNNState = r_obj_fly, COMPLETE
        Act_SetNNObj = r_obj_dock

        [Trigger]
        nickname = r_dock
        Cnd_BaseEnter = r_base
        Act_SetNNState = r_obj_dock, COMPLETE
        Act_SetNNObj = r_obj_bar
        Act_ActTrig = r_chosen

        [Trigger]
        nickname = r_chosen
        Cnd_Timer = 5

        {TRIGGERS}
        """;

    /// <summary>The replay runs the waiting triggers that the path shows ran in the game (fix of the review of
    /// 2026-10-28). The launch runs its watcher, the player in space runs the Cnd_InSpace trigger, and the dock that
    /// completes an objective runs the timer and the trigger that made the objective active. So the last objective of the
    /// path is the objective of the dock, and none of these triggers waits at the start.</summary>
    [Fact]
    public void TheReplayRunsTheWaitingTriggersThatThePathShowsRan()
    {
        var text = Waiting.Replace("{START}", "").Replace("{TRIGGERS}", "");
        var replay = StartReplay.Run(Index(text), MissionStartPoint.At("r_chosen"));

        Assert.Equal(
        [
            "Act_SetNNObj = r_obj_land", "Act_SpawnSolar = r_station", "Act_SetNNObj = r_obj_fly",
            "Act_NNPath = 1, 1, r_base, r_sys_a", "Act_SetNNState = r_obj_fly, COMPLETE", "Act_SetNNObj = r_obj_dock",
            "Act_SetNNState = r_obj_dock, COMPLETE", "Act_SetNNObj = r_obj_bar",
        ], replay.Effects.Select(e => $"{e.Line.Key.Trim()} = {string.Join(", ", e.Line.Line.Select(OpenLancer.Data.Export.RetailText.Of))}"));
        Assert.Equal(["r_after_launch", "r_other_launch", "r_chosen"],
            replay.ActiveSet.Select(t => t.Nickname).OrderBy(n => n == "r_chosen").ThenBy(n => n, StringComparer.Ordinal));

        var plan = Plan(text, "r_chosen");
        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("Act_SetNNObj = r_obj_bar", Lines(plan, 0));
        Assert.DoesNotContain(Lines(plan, 0), l => l.Contains("r_obj_dock", StringComparison.Ordinal) ||
                                                   l.Contains("r_obj_fly", StringComparison.Ordinal));
    }

    /// <summary>When a dock is the first line of the path that tells where the player is, the player was in space
    /// before it. So the Cnd_InSpace trigger runs before the dock, and the objective of the dock stays the last
    /// objective.</summary>
    [Fact]
    public void ADockShowsThatThePlayerWasInSpaceBefore()
    {
        const string text = """
            [Trigger]
            nickname = r_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_SetNNObj = r_obj_a
            Act_ActTrig = r_draw
            Act_ActTrig = r_dock

            [Trigger]
            nickname = r_draw
            Cnd_InSpace = true
            Act_NNPath = 1, 1, r_base, r_sys_a

            [Trigger]
            nickname = r_dock
            Cnd_BaseEnter = r_base
            Act_SetNNObj = r_obj_b
            Act_ActTrig = r_chosen

            [Trigger]
            nickname = r_chosen
            Cnd_Timer = 5
            """;

        var replay = StartReplay.Run(Index(text), MissionStartPoint.At("r_chosen"));

        Assert.Equal(["Act_SetNNObj", "Act_NNPath", "Act_SetNNObj"], replay.Effects.Select(e => e.Line.Key.Trim()));
        Assert.Contains("Act_SetNNObj = r_obj_b", Lines(Plan(text, "r_chosen"), 0));
        Assert.DoesNotContain(replay.ActiveSet, t => t.Nickname == "r_draw");
    }

    /// <summary>A log line after the last objective line does not remove that objective. Only the last log line
    /// stays.</summary>
    [Fact]
    public void ALogLineDoesNotRemoveTheLastObjective()
    {
        var text = Waiting.Replace("{START}", "").Replace("{TRIGGERS}", "").Replace("Act_SetNNObj = r_obj_bar",
            "Act_SetNNObj = r_obj_bar" + "\n" + "Act_NNIds = 7, HISTORY" + "\n" + "Act_NNIds = 8, HISTORY");

        var lines = Lines(Plan(text, "r_chosen"), 0);

        Assert.Contains("Act_SetNNObj = r_obj_bar", lines);
        Assert.Equal(["Act_NNIds = 8, HISTORY"], lines.Where(l => l.StartsWith("Act_NNIds", StringComparison.Ordinal)));
    }

    /// <summary>A waiting trigger that the objective rule runs can wait for a kill. Its condition shows that the ship
    /// died, so the setup does not spawn the ship.</summary>
    [Fact]
    public void AWaitingKillThatThePathShowsRanRemovesTheSpawn()
    {
        var text = Waiting.Replace("{START}", "Act_SpawnShip = r_ship\nAct_ActTrig = r_kill").Replace("{TRIGGERS}", """
            [MsnShip]
            nickname = r_ship
            position = 100, 0, 100

            [Trigger]
            nickname = r_kill
            Cnd_Destroyed = r_ship
            Act_SetNNObj = r_obj_kill
            """).Replace("Act_SetNNState = r_obj_dock, COMPLETE", "Act_SetNNState = r_obj_dock, COMPLETE\nAct_SetNNState = r_obj_kill, COMPLETE");

        var plan = Plan(text, "r_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.DoesNotContain("r_kill", plan.ActiveSet);
        Assert.DoesNotContain(plan.Setup.SelectMany(t => t.Lines), l => l.Values.Contains("r_ship"));
    }

    /// <summary>A waiting trigger whose run would stop a trigger of the rest of the path does not run. The replay goes
    /// back to the state before it, and the trigger waits at the start. A line of the replay that already made the
    /// objective active means that no waiting trigger has to run for it.</summary>
    [Fact]
    public void TheReplayDoesNotRunAWaitingTriggerThatBreaksThePath()
    {
        var breaker = Waiting.Replace("{START}", "Act_ActTrig = r_breaker").Replace("{TRIGGERS}", """
            [Trigger]
            nickname = r_breaker
            Cnd_PlayerLaunch = no_params
            Act_DeactTrig = r_launch
            """);
        var replay = StartReplay.Run(Index(breaker), MissionStartPoint.At("r_chosen"));
        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Contains(replay.ActiveSet, t => t.Nickname == "r_breaker");
        Assert.Contains(replay.ActiveSet, t => t.Nickname == "r_chosen");

        var known = Waiting.Replace("{START}", "Act_SetNNObj = r_obj_dock").Replace("{TRIGGERS}", "");
        var plain = StartReplay.Run(Index(known), MissionStartPoint.At("r_chosen"));
        Assert.Contains(plain.ActiveSet, t => t.Nickname == "r_delay");
        Assert.DoesNotContain(plain.Effects, e => MissionIndex.Is(e.Line.Key, "Act_SetNNState") &&
                                                  e.Line.Value(0) == "r_obj_fly");
    }

    /// <summary>
    /// A path with a jump from j_sys_a to j_sys_b (fix of issue 45). The launch starts a fail watcher of a point in
    /// j_sys_a, a trigger near the gate that starts the stop of the watcher at the exit, a log at the exit, and
    /// triggers that wait for other exits and entries. {LAUNCH} and {TRIGGERS} add lines.
    /// </summary>
    private const string Jump = """
        [Trigger]
        nickname = j_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = j_launch

        [Trigger]
        nickname = j_launch
        system = j_sys_a
        Cnd_SpaceEnter = j_sys_a
        Act_ActTrig = j_too_far
        Act_ActTrig = j_near_gate
        Act_ActTrig = j_log
        Act_ActTrig = j_other_exit
        Act_ActTrig = j_entry
        Act_ActTrig = j_other_entry
        Act_ActTrig = j_jump_end
        Act_ActTrig = j_arrive
        {LAUNCH}

        [Trigger]
        nickname = j_too_far
        system = j_sys_a
        Cnd_DistVec = outside, Player, 0, 0, 0, 12000
        Act_ChangeState = FAIL, 1

        [Trigger]
        nickname = j_near_gate
        system = j_sys_a
        Cnd_DistVec = inside, Player, 5000, 0, 0, 800
        Act_LockDock = Player, j_gate, unlock
        Act_ActTrig = j_leave

        [Trigger]
        nickname = j_leave
        system = j_sys_a
        Cnd_SystemExit = j_sys_a
        Act_DeactTrig = j_too_far

        [Trigger]
        nickname = j_log
        Cnd_SystemExit = j_sys_a, j_sys_b
        Act_NNIds = 7, HISTORY

        [Trigger]
        nickname = j_other_exit
        Cnd_SystemExit = j_sys_a, j_sys_c
        Act_NNIds = 8, HISTORY

        [Trigger]
        nickname = j_entry
        Cnd_SystemEnter = j_sys_b, j_sys_a
        Act_SpawnSolar = j_buoy

        [Trigger]
        nickname = j_other_entry
        Cnd_SystemEnter = j_sys_c
        Act_SpawnSolar = j_tower

        [Trigger]
        nickname = j_jump_end
        Cnd_JumpInComplete = j_sys_b
        Act_SpawnSolar = j_scene

        [Trigger]
        nickname = j_arrive
        system = j_sys_b
        Cnd_SystemEnter = j_sys_b
        Act_ActTrig = j_chosen

        [Trigger]
        nickname = j_chosen
        system = j_sys_b
        Cnd_Timer = 5

        {TRIGGERS}
        """;

    private static StartReplay JumpReplay(string trigger, string launch = "", string triggers = "") =>
        StartReplay.Run(Index(Jump.Replace("{LAUNCH}", launch).Replace("{TRIGGERS}", triggers)),
            MissionStartPoint.At(trigger));

    private static List<string> Names(IEnumerable<MissionTrigger> triggers) => triggers.Select(t => t.Nickname).ToList();

    /// <summary>A waiting Cnd_DistVec fail watcher of a point in the old system and a Cnd_SystemExit trigger that stops
    /// it wait before a jump of the path. The path shows the player in the new system, so the game sent the exit, and the
    /// stop ran. The watcher does not wait at the start, and the play does not fail at the spawn (issue 45).</summary>
    [Fact]
    public void TheExitOfASystemRunsTheTriggerThatStopsAWatcherOfThatSystem()
    {
        var replay = JumpReplay("j_chosen", launch: "Act_ActTrig = j_leave");

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.DoesNotContain("j_too_far", Names(replay.ActiveSet));
        Assert.DoesNotContain("j_leave", Names(replay.ActiveSet));
        var ran = Names(replay.Changes.Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger));
        Assert.True(ran.IndexOf("j_leave") < ran.IndexOf("j_arrive"), string.Join(" ", ran));
        Assert.DoesNotContain("j_too_far", ran);
    }

    /// <summary>A link of a chain can run in the pass of the link before it, for example a link with Cnd_True. That run
    /// counts, so the objective rule and the exit rule still run the whole chain. Retail M11 and M08 have such chains to
    /// a trigger of the exit, and M12 has one to an objective.</summary>
    [Fact]
    public void AChainWithACndTrueLinkStillRuns()
    {
        const string relay = """
            [Trigger]
            nickname = {NAME}
            Cnd_True = no_params
            Act_ActTrig = {NEXT}
            """;
        var objective = Waiting.Replace("Act_ActTrig = r_switch", "Act_ActTrig = r_relay").Replace("{START}", "")
            .Replace("{TRIGGERS}", relay.Replace("{NAME}", "r_relay").Replace("{NEXT}", "r_switch"));
        var replay = StartReplay.Run(Index(objective), MissionStartPoint.At("r_chosen"));

        Assert.Contains(replay.Effects, e => e.Line.Key.Trim() == "Act_SetNNObj" && e.Line.Value(0) == "r_obj_dock");
        Assert.DoesNotContain(replay.ActiveSet, t => t.Nickname is "r_delay" or "r_relay" or "r_switch");

        var exit = Jump.Replace("Act_ActTrig = j_leave", "Act_ActTrig = j_relay").Replace("{LAUNCH}", "")
            .Replace("{TRIGGERS}", relay.Replace("{NAME}", "j_relay").Replace("{NEXT}", "j_leave"));
        var jump = StartReplay.Run(Index(exit), MissionStartPoint.At("j_chosen"));

        Assert.Equal(["j_start", "j_launch", "j_near_gate", "j_relay", "j_leave", "j_log", "j_entry", "j_arrive"],
            Names(jump.Changes.Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger)));
        Assert.DoesNotContain("j_too_far", Names(jump.ActiveSet));
    }

    /// <summary>A waiting trigger near the gate starts the stop of the watcher. The jump shows that it ran before the
    /// exit, so the replay runs it, then the stop. The exit runs the triggers that wait for the exit to the new system and
    /// the entry from the old system. Triggers of other exits and entries, and a trigger that waits for the end of the
    /// jump, still wait. The replay marks the arrival after the exit and before the entry.</summary>
    [Fact]
    public void TheExitRunsTheChainToItsTriggersAndTheEntryRunsItsTriggers()
    {
        var replay = JumpReplay("j_chosen");

        var ran = Names(replay.Changes.Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger));
        Assert.Equal(["j_start", "j_launch", "j_near_gate", "j_leave", "j_log", "j_entry", "j_arrive"], ran);
        var arrival = Assert.Single(replay.Jumps);
        Assert.Equal(["j_start", "j_launch", "j_near_gate", "j_leave", "j_log"],
            Names(replay.Changes.Take(arrival).Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger)));
        Assert.Equal(["j_chosen", "j_jump_end", "j_other_entry", "j_other_exit"],
            Names(replay.ActiveSet).OrderBy(n => n != "j_chosen").ThenBy(n => n, StringComparer.Ordinal));

        var plan = StartPlan.Make(Index(Jump.Replace("{LAUNCH}", "").Replace("{TRIGGERS}", "")), Slot,
            MissionStartPoint.At("j_chosen"));
        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("Act_LockDock = Player, j_gate, unlock", Lines(plan, 0));
        Assert.Contains("Act_SpawnSolar = j_buoy", Lines(plan, 1));
        Assert.DoesNotContain(Lines(plan, 1), l => l.Contains("j_too_far", StringComparison.Ordinal));
    }

    /// <summary>A chosen trigger that waits for the jump starts before the jump, so nothing of the exit runs. A trigger
    /// of the exit that would stop the trigger of the jump on the path does not run, and it waits at the
    /// start.</summary>
    [Fact]
    public void TheExitRunsNothingBeforeTheJumpOrAgainstThePath()
    {
        var before = JumpReplay("j_arrive");
        Assert.Contains("j_too_far", Names(before.ActiveSet));
        Assert.Contains("j_near_gate", Names(before.ActiveSet));
        Assert.Contains("j_log", Names(before.ActiveSet));

        var against = JumpReplay("j_chosen", launch: "Act_ActTrig = j_bad_exit", triggers: """
            [Trigger]
            nickname = j_bad_exit
            Cnd_SystemExit = ANY
            Act_DeactTrig = j_arrive
            """);
        Assert.Equal(StartRefusal.None, against.Refusal);
        Assert.Contains("j_bad_exit", Names(against.ActiveSet));
        Assert.Contains("j_chosen", Names(against.ActiveSet));
    }

    /// <summary>Only a condition shows a change of the system. Before the first condition with a system, the newest
    /// system key gives the old system, so the exit from k_sys_a runs at the arrival in k_sys_b. A system key after that
    /// condition does not show a jump: the entry into k_sys_a does not run at k_wrong_key. The next arrival shows the
    /// exit from k_sys_b, the system of the last condition, and not from the newest system key.</summary>
    [Fact]
    public void TheSystemKeyGivesOnlyTheOldSystemBeforeTheFirstCondition()
    {
        const string text = """
            [Trigger]
            nickname = k_start
            system = k_sys_a
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = k_leave
            Act_ActTrig = k_arrive

            [Trigger]
            nickname = k_leave
            Cnd_SystemExit = k_sys_a
            Act_NNIds = 7, HISTORY

            [Trigger]
            nickname = k_arrive
            Cnd_SystemEnter = k_sys_b
            Act_ActTrig = k_leave_b
            Act_ActTrig = k_enter_a
            Act_ActTrig = k_wrong_key

            [Trigger]
            nickname = k_leave_b
            Cnd_SystemExit = k_sys_b
            Act_NNIds = 8, HISTORY

            [Trigger]
            nickname = k_enter_a
            Cnd_SystemEnter = k_sys_a
            Act_NNIds = 9, HISTORY

            [Trigger]
            nickname = k_wrong_key
            system = k_sys_a
            Cnd_Timer = 1
            Act_ActTrig = k_arrive_c

            [Trigger]
            nickname = k_arrive_c
            Cnd_SystemEnter = k_sys_c
            Act_ActTrig = k_chosen

            [Trigger]
            nickname = k_chosen
            Cnd_Timer = 5
            """;

        var replay = StartReplay.Run(Index(text), MissionStartPoint.At("k_chosen"));

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["k_start", "k_leave", "k_arrive", "k_wrong_key", "k_leave_b", "k_arrive_c"],
            Names(replay.Changes.Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger)));
        Assert.Equal(["k_chosen", "k_enter_a"], Names(replay.ActiveSet).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>A dock and a launch in the system of the condition before them show no new system, so a trigger of the
    /// exit from that system still waits.</summary>
    [Fact]
    public void ADockAndALaunchInTheSameSystemShowNoExit()
    {
        const string text = """
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_launch

            [Trigger]
            nickname = s_launch
            Cnd_SpaceEnter = s_sys_a
            Act_ActTrig = s_leave
            Act_ActTrig = s_dock

            [Trigger]
            nickname = s_leave
            Cnd_SystemExit = s_sys_a
            Act_NNIds = 7, HISTORY

            [Trigger]
            nickname = s_dock
            Cnd_BaseEnter = s_base_a
            Act_ActTrig = s_launch_again

            [Trigger]
            nickname = s_launch_again
            Cnd_SpaceEnter = s_sys_a
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 5
            """;
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["s_sys_a"],
            [MissionValueKind.Base] = ["s_base_a"],
        }, null, new Dictionary<string, string> { ["s_base_a"] = "s_sys_a" });

        var replay = StartReplay.Run(Index(text, world), MissionStartPoint.At("s_chosen"));

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Contains("s_leave", Names(replay.ActiveSet));
        Assert.DoesNotContain(replay.Changes, c => c.Kind == MachineChangeKind.Ran && c.Trigger.Nickname == "s_leave");
    }

    /// <summary>The entry runs a trigger only when its second system is the system before the jump or ANY. A trigger
    /// that waits for an entry from a third system still waits.</summary>
    [Fact]
    public void TheEntryComparesTheSystemBeforeTheJump()
    {
        var replay = JumpReplay("j_chosen", launch: "Act_ActTrig = j_entry_from_c", triggers: """
            [Trigger]
            nickname = j_entry_from_c
            Cnd_SystemEnter = j_sys_b, j_sys_c
            Act_NNIds = 9, HISTORY
            """);

        Assert.Contains("j_entry_from_c", Names(replay.ActiveSet));
        Assert.Contains(replay.Changes, c => c.Kind == MachineChangeKind.Ran && c.Trigger.Nickname == "j_entry");
    }

    /// <summary>The chosen trigger only starts in the replay, so its condition does not show a new system. A chosen
    /// trigger that docks at a base of another system does not run the exit from the old system.</summary>
    [Fact]
    public void TheConditionOfTheChosenTriggerShowsNoNewSystem()
    {
        const string text = """
            [Trigger]
            nickname = d_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = d_launch

            [Trigger]
            nickname = d_launch
            Cnd_SpaceEnter = d_sys_a
            Act_ActTrig = d_leave
            Act_ActTrig = d_chosen

            [Trigger]
            nickname = d_leave
            Cnd_SystemExit = d_sys_a
            Act_NNIds = 7, HISTORY

            [Trigger]
            nickname = d_chosen
            Cnd_BaseEnter = d_base_b
            """;
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["d_sys_a", "d_sys_b"],
            [MissionValueKind.Base] = ["d_base_b"],
        }, null, new Dictionary<string, string> { ["d_base_b"] = "d_sys_b" });

        var replay = StartReplay.Run(Index(text, world), MissionStartPoint.At("d_chosen"));

        Assert.Equal(StartRefusal.None, replay.Refusal);
        Assert.Equal(["d_chosen", "d_leave"], Names(replay.ActiveSet).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void AStartTriggerGivesAMissionStartWithoutATriggerSave()
    {
        var plan = Plan(Script, "t_start");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.MissionStart, plan.Kind);
        Assert.Empty(plan.SavedTriggers);
        Assert.Empty(plan.Setup);
        Assert.Equal(["t_offer"], plan.ActiveSet);
        Assert.Equal((4, false), (plan.Stage, plan.Accepted));
        Assert.Equal("Mission start (t_start)", plan.StartName);
    }

    [Fact]
    public void TheMissionStartGivesTheStartSet()
    {
        var plan = Plan(Script, null);

        Assert.Equal((StartKind.MissionStart, "Mission start"), (plan.Kind, plan.StartName));
        Assert.Equal(["t_offer"], plan.ActiveSet);
        Assert.Empty(plan.SavedTriggers);
    }

    [Fact]
    public void ARestoreTriggerGivesACheckpointStartWithOneTriggerSave()
    {
        var plan = Plan(Script, "t_restore");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.Checkpoint, plan.Kind);
        Assert.Equal(["t_restore"], plan.SavedTriggers);
        Assert.Empty(plan.Setup);
        Assert.Equal("Checkpoint t_restore", plan.StartName);
        // The story path reaches the checkpoint only through its Act_Save line, after the accept.
        Assert.Equal((5, true), (plan.Stage, plan.Accepted));
    }

    /// <summary>
    /// A saved game keeps more state than the triggers, for example the objective, the dock rules and the reputations.
    /// The start save keeps only the triggers, so a checkpoint start and an approximate start whose path starts at a
    /// checkpoint warn about it (review fix of M6c-2). A start from a start trigger has no such warning.
    /// </summary>
    [Fact]
    public void ACheckpointStartWarnsThatTheStateOfASavedGameIsNotInTheStart()
    {
        var checkpoint = Plan(Script, "t_restore");
        var fromCheckpoint = Plan(Script, "t_fail");

        Assert.Contains(StartPlan.CheckpointStateWarning("t_restore"), checkpoint.Warnings);
        Assert.Equal(["t_restore", "t_fail"], fromCheckpoint.Path);
        Assert.Contains(StartPlan.CheckpointStateWarning("t_restore"), fromCheckpoint.Warnings);
        Assert.DoesNotContain(Plan(Script, "t_chosen").Warnings, w => w.StartsWith("A saved game", StringComparison.Ordinal));
        Assert.DoesNotContain(Plan(Script, null).Warnings, w => w.StartsWith("A saved game", StringComparison.Ordinal));
        Assert.Empty(SteLint.Problems(StartPlan.CheckpointStateWarning("t_restore")));
    }

    /// <summary>The kind of a start, for the chip of the toolbar and for the plan: one rule.</summary>
    [Fact]
    public void TheKindOfAStartIsTheKindOfThePlan()
    {
        var index = Index(Script);
        foreach (var start in new[] { null, "t_start", "t_restore", "t_chosen", "t_gone" })
        {
            var point = start == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(start);
            var expected = start == "t_gone" ? StartKind.Approximate : StartPlan.Make(index, Slot, point).Kind;
            Assert.Equal(expected, StartPlan.KindOf(index, Slot, point));
            Assert.Equal(StartKind.TestScript, StartPlan.KindOf(index, null, point));
        }
    }

    [Fact]
    public void ARestoreTriggerWithActSetInitialPlayerPosGivesASpaceStartAtItsPoint()
    {
        var place = Plan(Script, "t_restore").Place;

        Assert.Equal(StartPlaceKind.Space, place.Kind);
        Assert.Equal(new Vector3(10, 20, 30), place.Position);
        Assert.Equal(Quaternion.Identity, place.Orientation);
        // The line has no system. The story at the step that the restore trigger starts gives it.
        Assert.Equal("ol_sys_a", place.System);
        Assert.Equal("Act_SetInitialPlayerPos of t_restore. The system comes from Cnd_SpaceEnter of t_launch.", place.Source);
    }

    [Fact]
    public void ARestoreTriggerWhoseConditionSavesAStateGivesAOneLineSetup()
    {
        var text = Script.Replace("Cnd_PlayerLaunch = no_params", "Cnd_Timer = 10");

        var plan = Plan(text, "t_restore");

        Assert.Equal(StartKind.Checkpoint, plan.Kind);
        var setup = Assert.Single(plan.Setup);
        Assert.Equal("ol_play_setup", setup.Nickname);
        Assert.Equal(["Cnd_True = no_params", "Act_ActTrig = t_restore"], Lines(plan, 0));
        Assert.Equal(["ol_play_setup"], plan.SavedTriggers);
    }

    [Fact]
    public void TheReplayRunsThePathAndStopsWhenTheChosenTriggerStarts()
    {
        var plan = Plan(Script, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Equal(StartKind.Approximate, plan.Kind);
        // The start trigger ran by itself, a heartbeat started the pending offer, and each active trigger of the path
        // ran by force.
        Assert.Equal(["t_start", "t_offer", "t_accept", "t_launch", "t_kill", "t_scene", "t_chosen"], plan.Path);
        // The fail watcher waits beside the chosen trigger.
        Assert.Equal(["t_fail", "t_chosen"], plan.ActiveSet);
        Assert.Equal("t_chosen (approximate)", plan.StartName);
    }

    [Fact]
    public void AnApproximateStartHoldsOnlyWorldAndStateActionsInReplayOrder()
    {
        var plan = Plan(Script, "t_chosen");

        Assert.Equal(2, plan.Setup.Count);
        // The state lines and the start of the world. The comm line, the starts and the save do not go.
        Assert.Equal(
        [
            "Cnd_True = no_params", "Act_SetRep = Player, ol_faction_a, -0.9", "Act_SetNNObj = ol_obj_1",
            "Act_ActTrig = ol_play_world",
        ], Lines(plan, 0));
        // The world waits for the launch, which the game also sends when a start in space spawns the player. The spawn
        // of the ship that the path destroys goes, and only the last music stays. Then the world starts the whole active
        // set, so its lines find the objects of the world.
        Assert.Equal("ol_play_world", plan.Setup[1].Nickname);
        Assert.Equal(
        [
            "Cnd_PlayerLaunch = no_params", "Act_SpawnShip = ol_ship_c", "Act_PlayMusic = ol_music_2", "Act_ActTrig = t_fail",
            "Act_ActTrig = t_chosen",
        ], Lines(plan, 1));
        Assert.Equal(["ol_play_setup"], plan.SavedTriggers);
    }

    [Fact]
    public void TheSaveKeysAreTheNetResultWithoutASceneThatThePathPlayed()
    {
        var state = Plan(Script, "t_chosen").State;

        Assert.Equal(500001, state.Title);
        Assert.Equal(0, state.Offer);
        // The path waits for the end of ol_scene, so it does not play again.
        Assert.Equal([(@"missions\ol\ol_other.ini", false)], state.Scenes);
    }

    /// <summary>
    /// The base scenes and the ambient scripts compare their paths as the game compares them (review fix of M6c-2). Each
    /// add adds one entry. Act_RemoveRTC compares with letter case and removes each match. Act_RemoveAmbient compares
    /// without letter case and removes the first match. No compare reads / as \.
    /// </summary>
    [Fact]
    public void TheSaveKeysComparePathsAsTheGameDoes()
    {
        const string text = """
            [Trigger]
            nickname = s_lines
            Cnd_Timer = 1
            Act_AddRTC = missions\ol\a.ini
            Act_AddRTC = missions\ol\a.ini
            Act_AddRTC = missions\ol\b.ini
            Act_AddRTC = missions\ol\c.ini
            Act_AddRTC = missions\ol\d.ini
            Act_RemoveRTC = MISSIONS\OL\B.INI
            Act_RemoveRTC = missions/ol/c.ini
            Act_RemoveRTC = missions\ol\d.ini
            Act_AddAmbient = scripts\ol\amb.thn, ol_room, ol_base_a
            Act_AddAmbient = scripts\ol\amb.thn, ol_room_2, ol_base_a
            Act_RemoveAmbient = SCRIPTS\OL\AMB.THN
            """;
        var index = Index(text);

        var state = StartSetup.StateOf(index, index.Trigger("s_lines")!.Actions, []);

        Assert.Equal([@"missions\ol\a.ini", @"missions\ol\a.ini", @"missions\ol\b.ini", @"missions\ol\c.ini"],
            state.Scenes.Select(s => s.File));
        Assert.Equal([(@"scripts\ol\amb.thn", "ol_room_2", "ol_base_a")], state.Ambients);
    }

    /// <summary>An ambient script of the path whose base the game data does not have refuses the start, because the
    /// game cannot load such a line of the start save (review fix of M6c-2).</summary>
    [Fact]
    public void AnAmbientScriptInABaseThatTheGameDataDoesNotHaveRefuses()
    {
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.Base] = ["ol_base_a"],
        }, null);
        string Text(string dock) => Script.Replace("Act_ActTrig = t_scene",
            $"Act_AddAmbient = scripts\\ol\\amb.thn, ol_room, {dock}\nAct_ActTrig = t_scene");

        var known = StartPlan.Make(Index(Text("ol_base_a"), world), Slot, MissionStartPoint.At("t_chosen"));
        var unknown = StartPlan.Make(Index(Text("ol_base_x"), world), Slot, MissionStartPoint.At("t_chosen"));

        Assert.True(known.CanStart, known.Reason);
        Assert.Equal([(@"scripts\ol\amb.thn", "ol_room", "ol_base_a")], known.State.Ambients);
        Assert.Equal(StartRefusal.SaveProblem, unknown.Refusal);
        Assert.Equal(@"The path adds the ambient script 'scripts\ol\amb.thn' in base 'ol_base_x', which is not in the game " +
                     "data. The game cannot load the start save with it. Correct the Act_AddAmbient line, or pick another " +
                     "trigger.", unknown.Reason);
        Assert.Empty(SteLint.Problems(unknown.Reason!));
    }

    [Fact]
    public void AnAcceptOnTheStoryPathGivesTheAcceptStageAndMissionAccepted()
    {
        Assert.Equal((5, true), (Plan(Script, "t_chosen").Stage, Plan(Script, "t_chosen").Accepted));
        // The accept trigger itself waits for the accept.
        Assert.Equal((4, false), (Plan(Script, "t_accept").Stage, Plan(Script, "t_accept").Accepted));
    }

    /// <summary>The launch gives the system, and the first spawn after the launch gives the point. Section 5.3, step 7
    /// of the M6c design has the rule (fix of 2026-10-27).</summary>
    [Fact]
    public void TheLaunchOfTheStoryPathGivesASpaceStartNearTheFirstSpawnAfterIt()
    {
        var place = Plan(Script, "t_chosen").Place;

        Assert.Equal(StartPlaceKind.Space, place.Kind);
        Assert.Equal("ol_sys_a", place.System);
        Assert.Equal(new Vector3(100, 0, 200 + StartPlaces.Offset), place.Position);
        Assert.Equal("Cnd_SpaceEnter of t_launch. The point is 1,000 units from the spawn of ol_ship_a.", place.Source);
    }

    /// <summary>A launch without a system is still a launch, so the start is not at the base of the offer. The base of the
    /// launch gives the system when the world knows it. Else the start is at that base, and the launch fires the
    /// triggers of the path again.</summary>
    [Fact]
    public void CndSpaceEnterWithNoParamsIsALaunchFromTheBaseOfTheOffer()
    {
        var text = Script.Replace("Cnd_SpaceEnter = ol_sys_a", "Cnd_SpaceEnter = no_params");
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>(), null,
            new Dictionary<string, string> { ["ol_base_a"] = "ol_sys_a" });

        var known = StartPlan.Make(Index(text, world), Slot, MissionStartPoint.At("t_chosen")).Place;
        var unknown = Plan(text, "t_chosen").Place;

        Assert.Equal((StartPlaceKind.Space, "ol_sys_a"), (known.Kind, known.System));
        Assert.Equal(new Vector3(100, 0, 200 + StartPlaces.Offset), known.Position);
        Assert.Equal("Cnd_SpaceEnter of t_launch. The system comes from the base of Cnd_BaseEnter of t_offer. The point " +
                     "is 1,000 units from the spawn of ol_ship_a.", known.Source);
        Assert.Equal((StartPlaceKind.Base, "ol_base_a"), (unknown.Kind, unknown.Base));
        Assert.Equal("Cnd_BaseEnter of t_offer. No line of the path names the system of Cnd_SpaceEnter of t_launch.",
            unknown.Source);
    }

    [Fact]
    public void ABaseStartRebuildsTheWorldAtTheLaunch()
    {
        var plan = Plan(Script, "t_chosen", StartPlaceChoice.Base, "ol_base_b");

        Assert.Equal((StartPlaceKind.Base, "ol_base_b"), (plan.Place.Kind, plan.Place.Base));
        Assert.Equal("Cnd_PlayerLaunch = no_params", Lines(plan, 1)[0]);
    }

    [Fact]
    public void TheCameraChoiceGivesTheCamera()
    {
        Assert.Equal(StartPlaceKind.Camera, Plan(Script, "t_chosen", StartPlaceChoice.Camera).Place.Kind);
    }

    [Fact]
    public void AChosenTriggerWithCndTrueIsInTheActiveSet()
    {
        var text = Script.Replace("""
            nickname = t_chosen
            Cnd_Timer = 5
            """, """
            nickname = t_chosen
            Cnd_True = no_params
            """);

        var plan = Plan(text, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("t_chosen", plan.ActiveSet);
        Assert.Equal("Act_ActTrig = t_chosen", Lines(plan, 1)[^1]);
    }

    [Fact]
    public void ATriggerThatAStopOnAnotherBranchRemovesRefusesWithItsReason()
    {
        const string text = """
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

        var plan = Plan(text, "s_chosen");

        Assert.False(plan.CanStart);
        Assert.Equal(StartRefusal.NotActive, plan.Refusal);
        Assert.Equal("Trigger 's_mid' on the path to 's_chosen' is not active at that point, because another trigger " +
                     "stops it first. Pick another trigger.", plan.Reason);
    }

    /// <summary>When the path does not start the chosen trigger, the refusal names it once (review fix of M6c-2). Here a
    /// trigger that starts first in id order stops the chosen trigger before it starts.</summary>
    [Fact]
    public void AChosenTriggerThatThePathDoesNotStartRefusesWithItsOwnReason()
    {
        var killer = Enumerable.Range(0, 100).Select(n => $"s_kill{n}")
            .First(n => FLHash.CreateID(n) < FLHash.CreateID("s_chosen"));
        var text = $"""
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_mid

            [Trigger]
            nickname = s_mid
            Cnd_Timer = 3
            Act_ActTrig = {killer}
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = {killer}
            Cnd_True = no_params
            Act_DeactTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 3
            """;

        var plan = Plan(text, "s_chosen");

        Assert.Equal(StartRefusal.NotActive, plan.Refusal);
        Assert.Equal("The path to trigger 's_chosen' does not start it, because another line stops it or cancels its " +
                     "start. Pick another trigger.", plan.Reason);
    }

    [Fact]
    public void APathThatPassesATriggerWithoutAConditionRefuses()
    {
        const string text = """
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_empty

            [Trigger]
            nickname = s_empty
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 3
            """;

        var plan = Plan(text, "s_chosen");

        Assert.Equal(StartRefusal.NoCondition, plan.Refusal);
        Assert.Equal("The path to trigger 's_chosen' passes trigger 's_empty', which has no condition. The game stops at " +
                     "that trigger. Add a condition to it.", plan.Reason);
    }

    [Fact]
    public void AnEmptyPathGivesOnlyTheChosenTriggerAndTheWarning()
    {
        var plan = Plan(Script, "t_orphan");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.Approximate, plan.Kind);
        Assert.Equal(["t_orphan"], plan.ActiveSet);
        Assert.Empty(plan.Path);
        Assert.Contains(StartPlan.NoPathWarning("t_orphan"), plan.Warnings);
        Assert.Single(plan.Setup);
        Assert.Equal(["Cnd_True = no_params", "Act_ActTrig = t_orphan"], Lines(plan, 0));
        // Without a path, nothing names a place.
        Assert.Equal(StartPlaceKind.Camera, plan.Place.Kind);
    }

    [Fact]
    public void ASetupNameThatTheScriptHasByNameOrByGameIdGetsASuffix()
    {
        var byName = Script + """


            [Trigger]
            nickname = OL_PLAY_SETUP
            Cnd_Timer = 1
            """;
        Assert.Equal("ol_play_setup_2", Plan(byName, "t_chosen").Setup[0].Nickname);

        // Two names with one game id: the game keeps only the first trigger of the id (TR4).
        var (first, second) = SameId();
        var byId = Script + $"""


            [Trigger]
            nickname = {first}
            Cnd_Timer = 1
            """;
        Assert.Equal($"{second}_2", StartSetup.FreeName(Index(byId), second, []));
        Assert.Equal("ol_free", StartSetup.FreeName(Index(byId), "ol_free", []));
        Assert.Equal("ol_free_2", StartSetup.FreeName(Index(byId), "ol_free", ["ol_free"]));
    }

    /// <summary>Two names of trigger form with one game id, found by a search.</summary>
    private static (string First, string Second) SameId()
    {
        var seen = new Dictionary<uint, string>();
        for (var i = 0; ; i++)
        {
            var name = $"ol_t{i}";
            if (seen.TryGetValue(FLHash.CreateID(name), out var other))
                return (other, name);
            seen[FLHash.CreateID(name)] = name;
        }
    }

    [Fact]
    public void TheSetupPassesTheMissionChecksWithoutAnError()
    {
        var plan = Plan(Script, "t_chosen");
        var document = Document(Script + "\r\n\r\n" + plan.SetupPatch.Replace("ol.new = true\r\n", ""));

        var errors = MissionChecks.Run(MissionIndex.Build(document, MissionSchema.Retail, MissionWorld.Empty,
                FullCoverage.Instance))
            .Where(p => p.Severity == DiagnosticSeverity.Error);

        Assert.Empty(errors);
    }

    [Fact]
    public void ASetupLineWithANewErrorRefusesTheStartWithItsMessage()
    {
        var index = Index(Script);
        var plan = Plan(Script, "t_chosen");
        var broken = plan with { Setup = [new SetupTrigger("ol_play_setup", [("Cnd_True", ["no_params"]), ("Act_SetNNObj", ["ol_obj_none"])])] };

        var checkedPlan = StartSetup.Check(broken, index);

        Assert.Equal(StartRefusal.SetupProblem, checkedPlan.Refusal);
        Assert.StartsWith("A setup trigger has a problem: ", checkedPlan.Reason);
        Assert.Contains("ol_obj_none", checkedPlan.Reason);
    }

    [Fact]
    public void ASetupLineWithTheErrorOfItsSourceLineDoesNotRefuse()
    {
        // The objective of the path does not exist. The game ignores the line and its copy in the same way.
        var text = Script.Replace("nickname = ol_obj_1", "nickname = ol_obj_other");

        var plan = Plan(text, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("Act_SetNNObj = ol_obj_1", Lines(plan, 0));
    }

    /// <summary>A plan without the mission checks of the setup triggers (the Play buttons of the graph, note of 2026-10-27
    /// in section 8.1 of the M6c design) equals the full plan for each trigger of the script.</summary>
    [Fact]
    public void APlanWithoutTheSetupCheckEqualsTheFullPlan()
    {
        var index = Index(Script);

        foreach (var trigger in index.Triggers)
        {
            var start = MissionStartPoint.At(trigger.Nickname);
            var full = StartPlan.Make(index, Slot, start);
            var fast = StartPlan.Make(index, Slot, start, checkSetup: false);

            Assert.Equal((full.Kind, full.Refusal, full.Reason, full.Place), (fast.Kind, fast.Refusal, fast.Reason, fast.Place));
            Assert.Equal(full.ActiveSet, fast.ActiveSet);
            Assert.Equal(full.SavedTriggers, fast.SavedTriggers);
            Assert.Equal(full.SetupPatch, fast.SetupPatch);
            Assert.Equal(full.State.Title, fast.State.Title);
            Assert.Equal(full.State.Scenes, fast.State.Scenes);
            Assert.Equal(full.Warnings, fast.Warnings);
        }
    }

    /// <summary>
    /// Only the full plan runs the setup check (the Play buttons of the graph, note of 2026-10-27 in section 8.1 of the
    /// M6c design). The check refuses a setup line with a new error. The plan without the check keeps the line and can
    /// start. A line of the same script has the error of its source line, which the check accepts, so the replay of the
    /// test gets a state line of another script.
    /// </summary>
    [Fact]
    public void ThePlanWithoutTheSetupCheckSkipsTheCheck()
    {
        var index = Index(Script);
        var start = MissionStartPoint.At("t_chosen");
        var replay = StartReplay.Run(index, start);
        var other = Index(Script.Replace("Act_SetNNObj = ol_obj_1", "Act_SetNNObj = ol_obj_none"));
        var bad = other.Lines.Single(l => MissionIndex.Is(l.Key, "Act_SetNNObj"));
        var broken = replay with
        {
            Changes = [.. replay.Changes, new MachineChange(MachineChangeKind.Effect, replay.Chosen!, bad)],
        };
        var plan = StartPlan.Make(index, Slot, start, checkSetup: false);
        var story = StartPlan.StoryPath(index, replay.Chosen);

        var full = StartSetup.Apply(plan, index, broken, story);
        var fast = StartSetup.Apply(plan, index, broken, story, check: false);

        Assert.Equal(StartRefusal.SetupProblem, full.Refusal);
        Assert.Contains("ol_obj_none", full.Reason);
        Assert.True(fast.CanStart, fast.Reason);
        Assert.Contains("Act_SetNNObj = ol_obj_none", Lines(fast, 0));
        Assert.DoesNotContain("Act_SetNNObj = ol_obj_1", Lines(fast, 0));
    }

    [Fact]
    public void AScriptWithoutASlotRefuses()
    {
        var plan = StartPlan.Make(Index(Script), null, MissionStartPoint.At("t_chosen"));

        Assert.Equal((StartKind.TestScript, StartRefusal.NoSlot), (plan.Kind, plan.Refusal));
        Assert.Equal(StartPlan.NoSlotReason, plan.Reason);
    }

    [Fact]
    public void AMissingTriggerRefuses()
    {
        Assert.Equal(StartRefusal.NoTrigger, Plan(Script, "t_none").Refusal);
    }

    [Fact]
    public void TheSetupPatchHasTheTwoNewSectionsAtTheEnd()
    {
        var patch = Plan(Script, "t_chosen").SetupPatch;

        Assert.StartsWith("[Trigger]\r\nnickname = ol_play_setup\r\nol.new = true\r\nCnd_True = no_params\r\n", patch);
        Assert.Contains("\r\n\r\n[Trigger]\r\nnickname = ol_play_world\r\nol.new = true\r\nCnd_PlayerLaunch = no_params\r\n",
            patch);
    }

    [Fact]
    public void ThePlanIsTheSameForTheSameIndex()
    {
        var index = Index(Script);
        var a = StartPlan.Make(index, Slot, MissionStartPoint.At("t_chosen"));
        var b = StartPlan.Make(index, Slot, MissionStartPoint.At("T_CHOSEN"));

        Assert.Equal(a.ActiveSet, b.ActiveSet);
        Assert.Equal(a.SetupPatch, b.SetupPatch);
        Assert.Equal(a.Start, b.Start);
    }

    /// <summary>The plans of one index share one graph of its links and its trigger names. So the Play buttons of the
    /// graph view do not read all links again for each trigger (fix of the review of 2026-10-28). A new index gets a new
    /// graph.</summary>
    [Fact]
    public void ThePlansOfOneIndexShareOneGraphOfTheLinks()
    {
        var index = Index(Script);
        var graph = StartGraph.Of(index);
        IEnumerable<string> Names(IReadOnlyList<MissionTrigger> path) => path.Select(t => t.Nickname);

        Assert.Same(graph, StartGraph.Of(index));
        Assert.NotSame(graph, StartGraph.Of(Index(Script)));
        Assert.Equal(["t_start", "t_offer", "t_accept", "t_launch", "t_kill", "t_scene", "t_chosen"],
            Names(graph.StoryPath(index.Trigger("t_chosen")!)));
        Assert.Equal(6, graph.Depth(index.Trigger("t_chosen")!));
        Assert.Equal(-1, graph.Depth(index.Trigger("t_orphan")!));
        // The story path reaches t_fail from the start. The replay path starts at the restore trigger, a nearer root.
        Assert.Equal(["t_start", "t_offer", "t_accept", "t_launch", "t_fail"], Names(graph.StoryPath(index.Trigger("t_fail")!)));
        Assert.Equal(["t_restore", "t_fail"], Names(graph.ReplayPath(index.Trigger("t_fail")!)));
        Assert.Empty(graph.ReplayPath(index.Trigger("t_orphan")!));
        Assert.True(graph.IsCheckpoint(index.Trigger("t_restore")!));
        Assert.True(graph.IsTaken("T_CHOSEN"));
        Assert.False(graph.IsTaken("ol_free"));
    }

    [Fact]
    public void AStartPointFollowsARenameWithoutLetterCase()
    {
        var start = MissionStartPoint.At("t_chosen");

        Assert.Equal(MissionStartPoint.At("t_new"), start.Renamed("T_CHOSEN", "t_new"));
        Assert.Same(start, start.Renamed("t_other", "t_new"));
        Assert.Same(MissionStartPoint.MissionStart, MissionStartPoint.MissionStart.Renamed("t_chosen", "t_new"));
        Assert.Throws<ArgumentException>(() => MissionStartPoint.At(" "));
    }
}
