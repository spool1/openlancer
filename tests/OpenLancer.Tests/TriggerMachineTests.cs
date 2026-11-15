using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The trigger rule machine (section 3 of the M6c design, build step 1.1): one case for each run rule of section 3.4,
/// the order of the start set and of a load, the chain limit and the snapshots. The scripts are synthetic, and all names
/// are invented.
/// </summary>
public sealed class TriggerMachineTests
{
    /// <summary>A world of the tests: an event matches a condition line with the same text, a fact is a set of line
    /// texts, and the player is in one system.</summary>
    internal sealed class TestWorld : ITriggerWorld
    {
        public HashSet<string> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? System { get; set; }

        public bool InSpace { get; set; } = true;

        public List<string> Effects { get; } = [];

        /// <summary>The event of a line text, for example <c>Cnd_CommComplete = line_a</c>.</summary>
        public static WorldEvent Event(string line) => new(Normal(line));

        public bool Matches(MissionTrigger trigger, MissionLine condition, WorldEvent worldEvent) =>
            KeyOf(condition) == worldEvent.Key;

        public bool Poll(MissionTrigger trigger, MissionLine condition) => Facts.Contains(KeyOf(condition));

        public bool TestAtStart(MissionTrigger trigger, MissionLine condition) =>
            System != null && MissionIndex.Is(condition.Value(0), System);

        public void Effect(MissionTrigger trigger, MissionLine action) => Effects.Add($"{trigger.Nickname}:{action.Key}");

        public static string KeyOf(MissionLine line) =>
            Normal($"{line.Key} = {string.Join(", ", Enumerable.Range(0, line.Count).Select(i => line.Value(i) ?? ""))}");

        private static string Normal(string text) => text.Replace(" ", "").ToLowerInvariant();
    }

    private static TriggerMachine Machine(string text, TestWorld? world = null, MachineOptions? options = null) =>
        new(Index(text), world ?? new TestWorld(), options);

    private static MissionTrigger T(TriggerMachine machine, string name) => machine.Index.Trigger(name)!;

    /// <summary>The nicknames of the triggers of the changes of one kind, in order.</summary>
    private static List<string> Of(MachinePass pass, MachineChangeKind kind) =>
        pass.Changes.Where(c => c.Kind == kind).Select(c => c.Trigger.Nickname).ToList();

    private static TriggerStatus Status(TriggerMachine machine, string name) => machine.StateOf(T(machine, name)).Status;

    /// <summary>Names in the order of their game id, smallest first.</summary>
    private static string[] ById(params string[] names) => names.OrderBy(FLHash.CreateID).ToArray();

    private static string Trigger(string name, string condition, params string[] actions)
    {
        var text = new StringBuilder($"[Trigger]\nnickname = {name}\n{condition}\n");
        foreach (var action in actions)
            text.Append(action).Append('\n');
        return text.ToString();
    }

    private static string Start(string name, string condition, params string[] actions) =>
        Trigger(name, "InitState = ACTIVE\n" + condition, actions);

    // ---- TR1 to TR13 (section 3.4 of the M6c design).

    [Fact]
    public void TR1_OnlyTheLastConditionLineDecides()
    {
        var machine = Machine(Start("t_wait", "Cnd_CommComplete = line_a\nCnd_CommComplete = line_b"));
        machine.StartMission();

        var first = machine.Dispatch(TestWorld.Event("Cnd_CommComplete = line_a"));
        var last = machine.Dispatch(TestWorld.Event("Cnd_CommComplete = line_b"));

        Assert.Empty(Of(first, MachineChangeKind.Ran));
        Assert.Equal(["t_wait"], Of(last, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR2_RepeatableBeforeTheNicknameDoesNotRearm()
    {
        var machine = Machine("[Trigger]\nrepeatable = true\nnickname = t_once\nInitState = ACTIVE\nCnd_True = no_params\n");

        var pass = machine.StartMission();
        machine.Heartbeat();

        Assert.Equal(["t_once"], Of(pass, MachineChangeKind.Ran));
        Assert.Empty(Of(pass, MachineChangeKind.Rearmed));
        Assert.Equal(TriggerStatus.Complete, Status(machine, "t_once"));
        Assert.Equal(1, machine.StateOf(T(machine, "t_once")).Runs);
    }

    [Fact]
    public void TR3_TheSystemOfATriggerDoesNotCount()
    {
        var world = new TestWorld { System = "ol_sys_b" };
        var machine = Machine(Start("t_far", "system = ol_sys_a\nCnd_BaseEnter = ol_base"), world);
        machine.StartMission();

        var pass = machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base"));

        Assert.Equal(["t_far"], Of(pass, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR4_AStartOfARepeatedNameStartsTheFirstSection()
    {
        var machine = Machine(
            Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_twin") +
            Trigger("t_twin", "Cnd_BaseEnter = ol_base_1") +
            Trigger("t_twin", "Cnd_BaseEnter = ol_base_2"));
        machine.StartMission();

        machine.Heartbeat();

        var active = Assert.Single(machine.Active);
        Assert.Same(machine.Index.Triggers.First(t => t.Nickname == "t_twin"), active);
        Assert.Equal("Cnd_BaseEnter", active.Condition!.Key);
        Assert.Equal(TriggerStatus.Inactive, machine.StateOf(machine.Index.Triggers.Last()).Status);
    }

    [Fact]
    public void TR5_AnUnknownActionIsAnEffectAndDoesNotStopThePass()
    {
        var world = new TestWorld();
        var machine = Machine(
            Start("t_go", "Cnd_True = no_params", "Act_NoSuchAction = 1, 2, 3", "Act_ActTrig = t_next, extra, values") +
            Trigger("t_next", "Cnd_True = no_params"), world);

        var start = machine.StartMission();
        var next = machine.Heartbeat();

        Assert.Equal(["t_go:Act_NoSuchAction"], world.Effects);
        Assert.Single(start.Changes, c => c.Kind == MachineChangeKind.Effect);
        Assert.Equal(["t_next"], Of(next, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR6_ATriggerWithoutAConditionStopsTheRunWhenItStarts()
    {
        var machine = Machine(
            Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_empty", "Act_ActTrig = t_unknown") +
            Trigger("t_empty", "") + Trigger("t_unknown", "Cnd_NoSuchCondition = 1"));
        machine.StartMission();

        var pass = machine.Heartbeat();

        var stop = Assert.Single(pass.Changes, c => c.Kind == MachineChangeKind.Stop);
        Assert.Equal(MachineHaltKind.NoCondition, machine.Halt!.Kind);
        Assert.Same(stop.Trigger, machine.Halt.Trigger);
        Assert.Contains(machine.Halt.Trigger.Nickname, machine.Halt.Message);
        Assert.Equal(TriggerRules.NoConditionMessage(machine.Halt.Trigger.Nickname), machine.Halt.Message);
        Assert.True(machine.Heartbeat().IsRefused);
    }

    [Fact]
    public void TR6_AnUnknownConditionNameAloneStopsTheRun()
    {
        var machine = Machine(Start("t_odd", "Cnd_NoSuchCondition = 1"));

        machine.StartMission();

        Assert.Equal(MachineHaltKind.NoCondition, machine.Halt!.Kind);
        Assert.Equal("t_odd", machine.Halt.Trigger.Nickname);
    }

    [Fact]
    public void TR7_ThreeSiblingsRunInIdOrder()
    {
        var names = ById("ol_sib_one", "ol_sib_two", "ol_sib_three");
        var machine = Machine(Start("t_go", "Cnd_True = no_params",
                                    Enumerable.Reverse(names).Select(n => $"Act_ActTrig = {n}").ToArray()) +
                              string.Concat(names.Select(n => Trigger(n, "Cnd_True = no_params"))));
        machine.StartMission();

        var pass = machine.Heartbeat();

        Assert.Equal(names, Of(pass, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR7_AChildWithASmallerIdRunsBeforeAnOlderSibling()
    {
        var (first, child, last) = ById("ol_kin_a", "ol_kin_b", "ol_kin_c") is [var a, var b, var c] ? (a, b, c) : default;
        var machine = Machine(
            Start("t_go", "Cnd_True = no_params", $"Act_ActTrig = {last}", $"Act_ActTrig = {first}") +
            Trigger(first, "Cnd_True = no_params", $"Act_ActTrig = {child}") +
            Trigger(child, "Cnd_True = no_params") +
            Trigger(last, "Cnd_True = no_params"));
        machine.StartMission();

        var pass = machine.Heartbeat();

        Assert.Equal([first, child, last], Of(pass, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR7_TwoStartsOfOneTriggerInOnePassGiveOneStart()
    {
        var machine = Machine(
            Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_next", "Act_ActTrig = t_next") +
            Trigger("t_next", "Cnd_True = no_params"));
        machine.StartMission();

        Assert.Single(machine.Pending);
        Assert.Equal(["t_next"], Of(machine.Heartbeat(), MachineChangeKind.Started));
    }

    [Fact]
    public void TR8_CndTrueChainsRunInOnePass()
    {
        var machine = Machine(
            Start("t_go", "Cnd_BaseEnter = ol_base", "Act_ActTrig = t_one") +
            Trigger("t_one", "Cnd_True = no_params", "Act_ActTrig = t_two") +
            Trigger("t_two", "Cnd_True = no_params", "Act_ActTrig = t_three") +
            Trigger("t_three", "Cnd_True = no_params"));
        machine.StartMission();

        var pass = machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base"));

        Assert.Equal(["t_go", "t_one", "t_two", "t_three"], Of(pass, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR8_CndSystemEnterIsTrueAtTheStartInThePlayerSystem()
    {
        var script = Start("t_here", "Cnd_SystemEnter = ol_sys_a") + Start("t_there", "Cnd_SystemEnter = ol_sys_b");
        var machine = Machine(script, new TestWorld { System = "ol_sys_a" });

        var pass = machine.StartMission();

        Assert.Equal(["t_here"], Of(pass, MachineChangeKind.Ran));
        Assert.Equal(TriggerStatus.Active, Status(machine, "t_there"));
        // Then it waits for its event, as a world event condition does.
        Assert.Equal(["t_there"], Of(machine.Dispatch(TestWorld.Event("Cnd_SystemEnter = ol_sys_b")), MachineChangeKind.Ran));
    }

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("COMPLETE")]
    [InlineData("OFF")]
    public void TR8_AWatchWhoseStateHoldsRunsAtItsStart(string state)
    {
        // t_target is active (ACTIVE), complete (COMPLETE) or never started (OFF) when t_watch starts.
        var target = state switch
        {
            "ACTIVE" => Start("t_target", "Cnd_BaseEnter = ol_base"),
            "COMPLETE" => Start("t_target", "Cnd_True = no_params"),
            _ => Trigger("t_target", "Cnd_BaseEnter = ol_base"),
        };
        var machine = Machine(target + Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_watch") +
                              Trigger("t_watch", $"Cnd_WatchTrigger = t_target, {state}", "Act_ActTrig = t_after") +
                              Trigger("t_after", "Cnd_True = no_params"));
        machine.StartMission();

        var pass = machine.Heartbeat();

        // The watch runs in the pass that starts it, so its own start comes in the same drain.
        Assert.Equal(["t_watch", "t_after"], Of(pass, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR8_ATimerOfZeroRunsAtItsStartWhereItCounts()
    {
        var world = new TestWorld { InSpace = false };
        var machine = Machine(Start("t_now", "Cnd_Timer = 0") + Start("t_negative", "Cnd_Timer = -1") +
                              Start("t_space", "Cnd_Timer = 0, 0, RUN_IN_SPACE_ONLY"), world);

        var start = machine.StartMission();
        var base_ = machine.Heartbeat();
        world.InSpace = true;
        var space = machine.Heartbeat();

        Assert.Equal(ById("t_now", "t_negative"), Of(start, MachineChangeKind.Ran));
        // A timer that counts only in space does not run on a base, also at a time of 0.
        Assert.Empty(base_.Changes);
        Assert.Equal(["t_space"], Of(space, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR8_APollConditionTestsAtTheNextHeartbeat()
    {
        var world = new TestWorld();
        world.Facts.Add(TestWorld.Event("Cnd_InSpace = true").Key);
        var machine = Machine(Start("t_space", "Cnd_InSpace = true"), world);

        var start = machine.StartMission();
        var beat = machine.Heartbeat();

        Assert.Empty(Of(start, MachineChangeKind.Ran));
        Assert.Equal(["t_space"], Of(beat, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR9_TheRestartPairRestartsWithAResetTimer()
    {
        var machine = Machine(
            Start("t_timer", "Cnd_Timer = 10") +
            Start("t_reset", "Cnd_BaseEnter = ol_base", "Act_DeactTrig = t_timer", "Act_ActTrig = t_timer"));
        machine.StartMission();
        for (var i = 0; i < 100; i++)
            machine.Heartbeat();

        var pass = machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base"));

        Assert.Equal(["t_timer"], Of(pass, MachineChangeKind.Stopped));
        Assert.Equal(["t_timer"], Of(pass, MachineChangeKind.Started));
        Assert.Equal(100, machine.StateOf(T(machine, "t_timer")).ActiveSince);
        Assert.Equal(300, machine.TimerLeft(T(machine, "t_timer")));
    }

    [Fact]
    public void TR9_TheCancelPairStartsNothing()
    {
        var machine = Machine(
            Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_next", "Act_DeactTrig = t_next") +
            Trigger("t_next", "Cnd_True = no_params"));

        var start = machine.StartMission();
        var beat = machine.Heartbeat();

        Assert.Equal(["t_next"], Of(start, MachineChangeKind.Cancelled));
        Assert.Empty(machine.Pending);
        Assert.Empty(beat.Changes);
        Assert.Equal(TriggerStatus.Inactive, Status(machine, "t_next"));
    }

    [Fact]
    public void TR9_AStopSkipsACompleteTriggerAndAStartStartsItAgain()
    {
        var machine = Machine(
            Start("t_done", "Cnd_True = no_params") +
            Start("t_stop", "Cnd_BaseEnter = ol_base_1", "Act_DeactTrig = t_done") +
            Start("t_again", "Cnd_BaseEnter = ol_base_2", "Act_ActTrig = t_done"));
        machine.StartMission();
        Assert.Equal(TriggerStatus.Complete, Status(machine, "t_done"));

        var stop = machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base_1"));
        Assert.Empty(Of(stop, MachineChangeKind.Stopped));
        Assert.Equal(TriggerStatus.Complete, Status(machine, "t_done"));

        var again = machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base_2"));
        Assert.Equal(["t_done"], Of(again, MachineChangeKind.Started));
        Assert.Equal(2, machine.StateOf(T(machine, "t_done")).Runs);
    }

    [Fact]
    public void TR10_ARepeatableTimerOfOneSecondRunsAt30And60Heartbeats()
    {
        var machine = Machine(Start("t_tick", "repeatable = true\nCnd_Timer = 1"));
        machine.StartMission();
        var runs = new List<int>();

        for (var i = 1; i <= 75; i++)
        {
            if (Of(machine.Heartbeat(), MachineChangeKind.Ran).Count > 0)
                runs.Add(i);
        }

        Assert.Equal([30, 60], runs);
        Assert.Equal(TriggerStatus.Active, Status(machine, "t_tick"));
    }

    [Fact]
    public void TR10_ARepeatableCndTrueTriggerRunsOnceInEachHeartbeat()
    {
        var machine = Machine(Start("t_each", "repeatable = true\nCnd_True = no_params"));

        var start = machine.StartMission();
        var beats = Enumerable.Range(0, 5).Select(_ => Of(machine.Heartbeat(), MachineChangeKind.Ran).Count).ToList();

        Assert.Equal(["t_each"], Of(start, MachineChangeKind.Ran));
        Assert.Equal([1, 1, 1, 1, 1], beats);
        Assert.Equal(6, machine.StateOf(T(machine, "t_each")).Runs);
    }

    [Fact]
    public void TR11_ATimerOfOneSecondRunsAtHeartbeat30Not29()
    {
        var machine = Machine(Start("t_tick", "Cnd_Timer = 1"));
        machine.StartMission();

        for (var i = 1; i < 30; i++)
            Assert.Empty(machine.Heartbeat().Changes);
        var pass = machine.Heartbeat();

        Assert.Equal(30, machine.Heartbeats);
        Assert.Equal(["t_tick"], Of(pass, MachineChangeKind.Ran));
    }

    [Fact]
    public void TR11_ATimerInSpaceOnlyDoesNotCountOnABase()
    {
        var world = new TestWorld { InSpace = false };
        var machine = Machine(Start("t_space", "Cnd_Timer = 1, 1, RUN_IN_SPACE_ONLY"), world);
        machine.StartMission();

        for (var i = 0; i < 60; i++)
            machine.Heartbeat();
        Assert.Equal(TriggerStatus.Active, Status(machine, "t_space"));
        world.InSpace = true;
        for (var i = 0; i < 30; i++)
            machine.Heartbeat();

        Assert.Equal(TriggerStatus.Complete, Status(machine, "t_space"));
    }

    [Theory]
    [InlineData(TimerBound.Shortest, 60)]
    [InlineData(TimerBound.Longest, 150)]
    public void TR12_ATimerRangeUsesTheBoundOfTheRun(TimerBound bound, int heartbeat)
    {
        var machine = Machine(Start("t_range", "Cnd_Timer = 2, 5"), options: new MachineOptions { Timers = bound });
        machine.StartMission();
        var ran = 0;

        for (var i = 1; i <= 200 && ran == 0; i++)
        {
            if (Of(machine.Heartbeat(), MachineChangeKind.Ran).Count > 0)
                ran = i;
        }

        Assert.Equal(heartbeat, ran);
    }

    [Fact]
    public void TR13_AStartOrAStopOfAMissingNameIsIgnored()
    {
        var machine = Machine(Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_none", "Act_DeactTrig = t_gone"));

        var pass = machine.StartMission();

        var ignored = pass.Changes.Where(c => c.Kind == MachineChangeKind.Ignored).ToList();
        Assert.Equal(["t_none", "t_gone"], ignored.Select(c => c.Name));
        Assert.All(ignored, c => Assert.Equal("t_go", c.Trigger.Nickname));
        Assert.Null(machine.Halt);
    }

    // ---- CN-15: the watched states.

    [Theory]
    [InlineData("ON", new[] { false, true, false })]
    [InlineData("ACTIVE", new[] { false, true, false })]
    [InlineData("OFF", new[] { true, false, true })]
    [InlineData("INACTIVE", new[] { true, false, false })]
    [InlineData("COMPLETE", new[] { false, false, true })]
    public void TheWatchedStatesFollowTheTarget(string state, bool[] expected)
    {
        // Inactive, then active, then complete. The watch starts after the change of the target, so its test at the
        // start reads the state (TR8).
        var script = Trigger("t_target", "Cnd_BaseEnter = ol_base_2") +
                     Start("t_arm", "Cnd_BaseEnter = ol_base_1", "Act_ActTrig = t_target") +
                     Trigger("t_watch", $"Cnd_WatchTrigger = t_target, {state}") +
                     Start("t_start_watch", "Cnd_CommComplete = line_w", "Act_ActTrig = t_watch");
        var seen = new List<bool>();
        foreach (var step in new[] { 0, 1, 2 })
        {
            var machine = Machine(script);
            machine.StartMission();
            if (step >= 1)
                machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base_1"));
            if (step >= 2)
                machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base_2"));
            machine.Dispatch(TestWorld.Event("Cnd_CommComplete = line_w"));
            seen.Add(Status(machine, "t_watch") == TriggerStatus.Complete);
        }

        Assert.Equal(expected, seen);
    }

    [Fact]
    public void AWatchThatWaitsRunsAtTheHeartbeatAfterTheChange()
    {
        var machine = Machine(Trigger("t_target", "Cnd_BaseEnter = ol_base_2") +
                              Start("t_arm", "Cnd_BaseEnter = ol_base_1", "Act_ActTrig = t_target") +
                              Start("t_watch", "Cnd_WatchTrigger = t_target, ACTIVE"));
        machine.StartMission();
        Assert.Equal(TriggerStatus.Active, Status(machine, "t_watch"));

        var change = machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base_1"));
        var beat = machine.Heartbeat();

        Assert.DoesNotContain("t_watch", Of(change, MachineChangeKind.Ran));
        Assert.Equal(["t_watch"], Of(beat, MachineChangeKind.Ran));
    }

    // ---- The start set, the load, the end, the chain limit and the snapshots.

    [Fact]
    public void TheStartSetStartsInIdOrderAndItsTargetsWaitForTheNextPass()
    {
        var names = ById("ol_root_x", "ol_root_y", "ol_root_z");
        var script = string.Concat(Enumerable.Reverse(names).Select(n => Start(n, "Cnd_True = no_params", "Act_ActTrig = t_child"))) +
                     Trigger("t_child", "Cnd_True = no_params");
        var machine = Machine(script);

        var start = machine.StartMission();

        Assert.Equal(names, Of(start, MachineChangeKind.Ran));
        Assert.DoesNotContain("t_child", Of(start, MachineChangeKind.Started));
        Assert.Equal(["t_child"], machine.Pending.Select(t => t.Nickname));
        Assert.Equal(["t_child"], Of(machine.Heartbeat(), MachineChangeKind.Ran));
    }

    [Fact]
    public void ALoadStartsTheSavedTriggersInIdOrderAndTheirTargetsWait()
    {
        var names = ById("ol_save_p", "ol_save_q", "ol_save_r");
        var script = Start("t_go", "Cnd_True = no_params") +
                     string.Concat(names.Select(n => Trigger(n, "Cnd_True = no_params", "Act_ActTrig = t_after"))) +
                     Trigger("t_after", "Cnd_True = no_params");
        var machine = Machine(script);
        var saved = Enumerable.Reverse(names).Select(n => machine.Index.Trigger(n)!).ToList();

        var load = machine.Load(saved);

        Assert.Equal(names, Of(load, MachineChangeKind.Ran));
        Assert.Equal(TriggerStatus.Inactive, Status(machine, "t_go"));
        Assert.Equal(["t_after"], machine.Pending.Select(t => t.Nickname));
        Assert.Throws<InvalidOperationException>(() => machine.StartMission());
    }

    [Fact]
    public void TenSiblingsRunInIdOrder()
    {
        var names = ById(Enumerable.Range(0, 10).Select(i => $"ol_ten_{i}").ToArray());
        var machine = Machine(Start("t_go", "Cnd_True = no_params", names.Select(n => $"Act_ActTrig = {n}").ToArray()) +
                              string.Concat(names.Select(n => Trigger(n, "Cnd_True = no_params"))));
        machine.StartMission();

        Assert.Equal(names, Of(machine.Heartbeat(), MachineChangeKind.Ran));
    }

    [Fact]
    public void AMissionEndAppliesAfterThePassAndStopsThePasses()
    {
        var machine = Machine(
            Start("t_go", "Cnd_BaseEnter = ol_base", "Act_ChangeState = FAIL, 501", "Act_ActTrig = t_after") +
            Trigger("t_after", "Cnd_True = no_params"));
        machine.StartMission();

        var pass = machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base"));

        // The rest of the pass runs, then the end applies.
        Assert.Equal(["t_go", "t_after"], Of(pass, MachineChangeKind.Ran));
        Assert.Equal(MachineChangeKind.End, pass.Changes[^1].Kind);
        Assert.Equal(new MissionEnd(MissionEndKind.Fail, T(machine, "t_go"), "501"), machine.End);
        Assert.True(machine.Heartbeat().IsRefused);
        Assert.True(machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base")).IsRefused);
    }

    [Fact]
    public void AChainOf10001StartsStopsWithItsText()
    {
        var machine = Machine(Start("t_loop", "Cnd_True = no_params", "Act_ActTrig = t_loop"));
        machine.StartMission();

        var pass = machine.Heartbeat();

        Assert.Equal(MachineHaltKind.EndlessChain, machine.Halt!.Kind);
        Assert.Equal(10_000, Of(pass, MachineChangeKind.Started).Count);
        Assert.Equal("Trigger 't_loop' starts a chain that does not end. The game can stop responding here.",
            machine.Halt.Message);
        Assert.True(machine.Heartbeat().IsRefused);
    }

    [Fact]
    public void ACheckpointRecordsItsRestoreTrigger()
    {
        var machine = Machine(Start("t_go", "Cnd_True = no_params", "Act_Save = t_restore, 1") +
                              Trigger("t_restore", "Cnd_SpaceEnter = no_params"));

        var pass = machine.StartMission();

        var checkpoint = Assert.Single(pass.Changes, c => c.Kind == MachineChangeKind.Checkpoint);
        Assert.Equal("t_restore", checkpoint.Target!.Nickname);
        Assert.Empty(machine.Pending);
    }

    [Fact]
    public void ACountingConditionNeedsItsCount()
    {
        var machine = Machine(Start("t_kills", "Cnd_Destroyed = ol_wing, 3"));
        machine.StartMission();
        var kill = TestWorld.Event("Cnd_Destroyed = ol_wing, 3");

        machine.Dispatch(kill);
        machine.Dispatch(kill);
        Assert.Equal((2, 3), machine.CountOf(T(machine, "t_kills"))!.Value);
        var third = machine.Dispatch(kill);

        Assert.Equal(["t_kills"], Of(third, MachineChangeKind.Ran));
    }

    [Theory]
    [InlineData("Cnd_Destroyed = ol_wing, 0", false)]
    [InlineData("Cnd_LootAcquired = ol_loot, ol_ship, 0", false)]
    [InlineData("Cnd_LootAcquired = ol_loot, ol_ship, -2", false)]
    [InlineData("Cnd_Destroyed = ol_wing, -2", true)]
    [InlineData("Cnd_ProjHit = ol_wing, 0", true)]
    [InlineData("Cnd_ProjHitShipToLbl = ol_wing, -1", true)]
    public void ACountThatTheGameNeverReachesNeverRuns(string condition, bool runs)
    {
        // The game runs a kill or a loot count only at the count, and a hit count at the count or above it.
        var machine = Machine(Start("t_count", condition));
        machine.StartMission();
        var line = TestWorld.Event(condition);

        var ran = Enumerable.Range(0, 5).Sum(_ => Of(machine.Dispatch(line), MachineChangeKind.Ran).Count);

        Assert.Equal(runs ? 1 : 0, ran);
        Assert.Null(machine.CountOf(T(machine, "t_count")));
    }

    // ---- The stop point of the start plan (MachineOptions.StopBefore).

    [Fact]
    public void AStopPointStopsBeforeItsTestAndKeepsTheRestOfTheDrain()
    {
        var (p, q, r) = ById("ol_stop_p", "ol_stop_q", "ol_stop_r") is [var a, var b, var c] ? (a, b, c) : default;
        var script = Start("t_go", "Cnd_True = no_params", $"Act_ActTrig = {r}", $"Act_ActTrig = {q}", $"Act_ActTrig = {p}") +
                     Trigger(p, "Cnd_BaseEnter = ol_base") + Trigger(q, "Cnd_True = no_params") +
                     Trigger(r, "Cnd_BaseEnter = ol_base");
        var index = Index(script);
        var machine = new TriggerMachine(index, new TestWorld(), new MachineOptions { StopBefore = index.Trigger(q) });
        machine.StartMission();

        var pass = machine.Heartbeat();

        Assert.Equal(MachineHaltKind.StopPoint, machine.Halt!.Kind);
        Assert.Equal(q, machine.Halt.Trigger.Nickname);
        // The stop point did not test its condition, so it did not run.
        Assert.Empty(Of(pass, MachineChangeKind.Ran));
        Assert.Equal([p], machine.Active.Select(t => t.Nickname));
        // The game starts r in the same drain, so the stop keeps it.
        Assert.Equal([r], machine.Pending.Select(t => t.Nickname));
        Assert.True(machine.Heartbeat().IsRefused);
    }

    [Fact]
    public void AStopPointInTheStartSetKeepsTheRestOfTheSet()
    {
        var (p, q, r) = ById("ol_root_p", "ol_root_q", "ol_root_r") is [var a, var b, var c] ? (a, b, c) : default;
        var script = Start(p, "Cnd_True = no_params", "Act_ActTrig = t_child") + Start(q, "Cnd_BaseEnter = ol_base") +
                     Start(r, "Cnd_BaseEnter = ol_base") + Trigger("t_child", "Cnd_BaseEnter = ol_base");
        var index = Index(script);
        var machine = new TriggerMachine(index, new TestWorld(), new MachineOptions { StopBefore = index.Trigger(q) });

        machine.StartMission();

        Assert.Equal(MachineHaltKind.StopPoint, machine.Halt!.Kind);
        Assert.Equal(new[] { "t_child", r }.OrderBy(FLHash.CreateID), machine.Pending.Select(t => t.Nickname));
    }

    [Fact]
    public void AStopForAnotherReasonClearsThePendingSet()
    {
        // The trigger without a condition starts first, so the other start is still pending at the stop.
        var (empty, next) = ById("ol_halt_a", "ol_halt_b") is [var a, var b] ? (a, b) : default;
        var machine = Machine(Start("t_go", "Cnd_True = no_params", $"Act_ActTrig = {next}", $"Act_ActTrig = {empty}") +
                              Trigger(empty, "") + Trigger(next, "Cnd_True = no_params"));
        machine.StartMission();

        machine.Heartbeat();

        Assert.Equal(MachineHaltKind.NoCondition, machine.Halt!.Kind);
        Assert.Equal(empty, machine.Halt.Trigger.Nickname);
        Assert.Empty(machine.Pending);
    }

    /// <summary>A stop without actions removes an active trigger, as Act_DeactTrig does. The trigger does not run, so
    /// its lines do nothing. A trigger that ran stays complete.</summary>
    [Fact]
    public void AStopWithoutActionsStopsActiveAndPendingTriggers()
    {
        var world = new TestWorld();
        var machine = Machine(Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_wait", "Act_ActTrig = t_later") +
                              Trigger("t_wait", "Cnd_Timer = 100", "Act_SpawnSolar = ol_buoy", "Act_ActTrig = t_fail") +
                              Trigger("t_later", "Cnd_CommComplete = line_a") +
                              Trigger("t_fail", "Cnd_True = no_params", "Act_ChangeState = FAIL"), world);
        machine.StartMission();
        machine.Heartbeat();
        machine.Force(T(machine, "t_later"));
        world.Effects.Clear();

        var pass = machine.StopTriggers([T(machine, "t_wait"), T(machine, "t_later")]);

        Assert.False(pass.IsRefused);
        Assert.Equal(["t_wait"], Of(pass, MachineChangeKind.Stopped));
        Assert.Empty(Of(pass, MachineChangeKind.Ran));
        Assert.Empty(world.Effects);
        Assert.Equal(TriggerStatus.Inactive, Status(machine, "t_wait"));
        Assert.Equal(TriggerStatus.Complete, Status(machine, "t_later"));
        Assert.Empty(machine.Active);
        Assert.Null(machine.End);
    }

    /// <summary>After the stop at the stop point, the machine refuses each pass. A stop without actions still works,
    /// so the start plan can stop triggers at the end of its replay. It removes an active trigger and a start that the
    /// stop kept. After another stop or after the end of the mission, the machine refuses it.</summary>
    [Fact]
    public void AStopWithoutActionsWorksAfterTheStopPointOnly()
    {
        var (p, q, r) = ById("ol_guard_p", "ol_guard_q", "ol_guard_r") is [var a, var b, var c] ? (a, b, c) : default;
        var script = Start("t_go", "Cnd_True = no_params", $"Act_ActTrig = {r}", $"Act_ActTrig = {q}", $"Act_ActTrig = {p}") +
                     Trigger(p, "Cnd_Timer = 100") + Trigger(q, "Cnd_True = no_params") + Trigger(r, "Cnd_Timer = 100");
        var index = Index(script);
        var machine = new TriggerMachine(index, new TestWorld(), new MachineOptions { StopBefore = index.Trigger(q) });
        machine.StartMission();
        machine.Heartbeat();
        Assert.True(machine.IsOver);
        Assert.Equal([p], machine.Active.Select(t => t.Nickname));
        Assert.Equal([r], machine.Pending.Select(t => t.Nickname));

        var pass = machine.StopTriggers([index.Trigger(p)!, index.Trigger(r)!]);

        Assert.False(pass.IsRefused);
        Assert.Equal([p], Of(pass, MachineChangeKind.Stopped));
        Assert.Equal([r], Of(pass, MachineChangeKind.Cancelled));
        Assert.Empty(machine.Active);
        Assert.Empty(machine.Pending);
        Assert.Equal(MachineHaltKind.StopPoint, machine.Halt!.Kind);

        var ended = Machine(Start("t_end", "Cnd_True = no_params", "Act_ChangeState = SUCCEED") +
                            Start("t_wait", "Cnd_Timer = 100"));
        ended.StartMission();
        Assert.True(ended.StopTriggers([T(ended, "t_wait")]).IsRefused);
        var halted = Machine(Start("t_go", "Cnd_True = no_params", "Act_ActTrig = t_empty") + Trigger("t_empty", "") +
                             Start("t_wait", "Cnd_Timer = 100"));
        halted.StartMission();
        halted.Heartbeat();
        Assert.Equal(MachineHaltKind.NoCondition, halted.Halt!.Kind);
        Assert.True(halted.StopTriggers([T(halted, "t_wait")]).IsRefused);
    }

    [Fact]
    public void ForceRunsAnActiveTriggerAndRefusesAnInactiveOne()
    {
        var machine = Machine(Start("t_wait", "Cnd_BaseEnter = ol_base", "Act_ActTrig = t_next") +
                              Trigger("t_next", "Cnd_True = no_params"));
        machine.StartMission();

        var pass = machine.Force(T(machine, "t_wait"));

        Assert.Equal(["t_wait", "t_next"], Of(pass, MachineChangeKind.Ran));
        Assert.Throws<ArgumentException>(() => machine.Force(T(machine, "t_wait")));
    }

    [Fact]
    public void AMachineFromASnapshotGivesTheSamePasses()
    {
        var script = Start("t_tick", "repeatable = true\nCnd_Timer = 0.5", "Act_ActTrig = t_child") +
                     Trigger("t_child", "Cnd_CommComplete = line_a", "Act_DeactTrig = t_tick") +
                     Start("t_kills", "Cnd_Destroyed = ol_wing, 2");
        var original = Machine(script);
        original.StartMission();
        for (var i = 0; i < 20; i++)
            original.Heartbeat();
        original.Dispatch(TestWorld.Event("Cnd_Destroyed = ol_wing, 2"));
        var copy = TriggerMachine.From(original.Snapshot(), original.Index, new TestWorld());

        string Trace(TriggerMachine machine)
        {
            var text = new StringBuilder();
            for (var i = 0; i < 40; i++)
            {
                foreach (var change in machine.Heartbeat().Changes)
                    text.Append($"{i}:{change.Kind}:{change.Trigger.Nickname} ");
            }
            foreach (var change in machine.Dispatch(TestWorld.Event("Cnd_CommComplete = line_a")).Changes)
                text.Append($"{change.Kind}:{change.Trigger.Nickname} ");
            foreach (var change in machine.Dispatch(TestWorld.Event("Cnd_Destroyed = ol_wing, 2")).Changes)
                text.Append($"{change.Kind}:{change.Trigger.Nickname} ");
            return text.ToString();
        }

        Assert.Equal(Trace(original), Trace(copy));
        Assert.Equal(original.Heartbeats, copy.Heartbeats);
        Assert.Equal(original.Active, copy.Active);
        Assert.Throws<ArgumentException>(() => TriggerMachine.From(original.Snapshot(), Index(script), new TestWorld()));
    }

    [Fact]
    public void IdleHeartbeatsSkipOnlyWhenNothingTests()
    {
        var machine = Machine(Start("t_wait", "Cnd_BaseEnter = ol_base", "Act_ActTrig = t_tick") +
                              Trigger("t_tick", "Cnd_Timer = 1"));
        machine.StartMission();

        Assert.Equal(100, machine.SkipIdle(100));
        machine.Dispatch(TestWorld.Event("Cnd_BaseEnter = ol_base"));

        Assert.Equal(0, machine.SkipIdle(100));
        Assert.Equal(100, machine.Heartbeats);
    }
}
