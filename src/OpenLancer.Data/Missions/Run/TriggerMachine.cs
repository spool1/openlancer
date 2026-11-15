using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

/// <summary>The state of a trigger in a run.</summary>
public enum TriggerStatus
{
    Inactive,
    Active,

    /// <summary>The trigger ran and did not start again (CN-15).</summary>
    Complete,
}

/// <summary>The state of one trigger in a run.</summary>
/// <param name="Runs">How many times the trigger ran.</param>
/// <param name="ActiveSince">The heartbeat of the last start of the trigger, or -1 when it never started.</param>
public readonly record struct TriggerRunState(TriggerStatus Status, int Runs, int ActiveSince);

/// <summary>Which bound of a timer range the run uses (decision D756). The game picks a value in the range when the
/// mission loads (TR12).</summary>
public enum TimerBound
{
    Shortest,
    Longest,
}

/// <summary>The options of a machine (section 3.1 of the M6c design).</summary>
public sealed record MachineOptions
{
    /// <summary>The options of the dry run: the shortest timer, and the chain limit of 10,000 starts.</summary>
    public static readonly MachineOptions Default = new();

    /// <summary>The bound of a <c>Cnd_Timer</c> range.</summary>
    public TimerBound Timers { get; init; } = TimerBound.Shortest;

    /// <summary>The most starts in one pass. A pass with more starts stops, because the chain does not end.</summary>
    public int ChainLimit { get; init; } = 10_000;

    /// <summary>
    /// A trigger whose start stops the pass, before its test at the start, or null. A start of the trigger while it is
    /// active does nothing, as in the game, so it does not stop the pass. The stop keeps the starts that the pass did
    /// not make yet in <see cref="TriggerMachine.Pending"/>: the rest of the start set, of the load or of the drain. The
    /// start plan of M6c-2 uses it.
    /// </summary>
    public MissionTrigger? StopBefore { get; init; }
}

/// <summary>How a mission ends (MSD section 2.1).</summary>
public enum MissionEndKind
{
    Success,
    Fail,
}

/// <summary>The end of a mission: the kind, the trigger of the <c>Act_ChangeState</c> line, and the fail text id or
/// null.</summary>
public sealed record MissionEnd(MissionEndKind Kind, MissionTrigger Trigger, string? TextId);

/// <summary>Why a run stops before the mission ends.</summary>
public enum MachineHaltKind
{
    /// <summary>The trigger of <see cref="MachineOptions.StopBefore"/> started.</summary>
    StopPoint,

    /// <summary>A trigger without a condition started (TR6). The game stops here.</summary>
    NoCondition,

    /// <summary>A pass made more starts than the chain limit. The chain does not end.</summary>
    EndlessChain,
}

/// <summary>A stop of a run before the mission ends, with the trigger that caused it.</summary>
public sealed record MachineHalt(MachineHaltKind Kind, MissionTrigger Trigger)
{
    /// <summary>The text of the stop. A stop at a trigger without a condition gives the text of rule M01.</summary>
    public string Message => Kind switch
    {
        MachineHaltKind.NoCondition => TriggerRules.NoConditionMessage(Trigger.Nickname),
        MachineHaltKind.EndlessChain => TriggerMachine.ChainMessage(Trigger.Nickname),
        _ => $"The run stops when trigger '{Trigger.Nickname}' starts.",
    };
}

/// <summary>The kind of a change of a pass (section 3.1 of the M6c design).</summary>
public enum MachineChangeKind
{
    /// <summary>A trigger became active.</summary>
    Started,

    /// <summary>A trigger ran: its condition was met, and it did its actions.</summary>
    Ran,

    /// <summary>A repeatable trigger became active again after it ran (TR10).</summary>
    Rearmed,

    /// <summary>A stop removed an active trigger from the active list.</summary>
    Stopped,

    /// <summary>A stop removed a pending start of a trigger (TR9).</summary>
    Cancelled,

    /// <summary>A start or a stop names a trigger that the script does not have (TR13).</summary>
    Ignored,

    /// <summary>An <c>Act_Save</c> line with its restore trigger.</summary>
    Checkpoint,

    /// <summary>An action without flow, for example a spawn.</summary>
    Effect,

    /// <summary>The mission ends (<see cref="TriggerMachine.End"/>).</summary>
    End,

    /// <summary>The run stops before the mission ends (<see cref="TriggerMachine.Halt"/>).</summary>
    Stop,
}

/// <summary>One change of a pass.</summary>
/// <param name="Trigger">The trigger that changed, or the trigger of the line of an Ignored, a Checkpoint, an Effect or
/// an End change.</param>
/// <param name="Line">The action line of an Ignored, a Checkpoint, an Effect or an End change, else null.</param>
/// <param name="Target">The restore trigger of a Checkpoint change, or null when the script does not have it.</param>
/// <param name="Name">The trigger name of an Ignored or a Checkpoint change, as the line writes it, else null.</param>
public readonly record struct MachineChange(MachineChangeKind Kind, MissionTrigger Trigger, MissionLine? Line = null,
    MissionTrigger? Target = null, string? Name = null);

/// <summary>The ordered changes of one pass of the machine (section 3.1 of the M6c design).</summary>
public sealed class MachinePass
{
    internal static readonly MachinePass Refused = new([], refused: true);

    internal static readonly MachinePass Empty = new([]);

    internal MachinePass(IReadOnlyList<MachineChange> changes, bool refused = false)
    {
        Changes = changes;
        IsRefused = refused;
    }

    /// <summary>The changes in the order of the game.</summary>
    public IReadOnlyList<MachineChange> Changes { get; }

    /// <summary>True when the machine refused the pass, because the mission ended or the run stopped.</summary>
    public bool IsRefused { get; }
}

/// <summary>A copy of the state of a machine (<see cref="TriggerMachine.Snapshot"/>). It never changes.</summary>
public sealed class MachineState
{
    internal MachineState(MachineProgram program, MachineOptions options)
    {
        Program = program;
        Options = options;
    }

    internal MachineProgram Program { get; }

    internal MachineOptions Options { get; }

    internal TriggerStatus[] Status { get; init; } = [];

    internal int[] Runs { get; init; } = [];

    internal int[] Since { get; init; } = [];

    internal int[] Ticks { get; init; } = [];

    internal int[] Counts { get; init; } = [];

    internal int[] Active { get; init; } = [];

    internal uint[] Pending { get; init; } = [];

    internal int Heartbeats { get; init; }

    internal bool Started { get; init; }

    internal MissionEnd? End { get; init; }

    internal MachineHalt? Halt { get; init; }
}

/// <summary>
/// The trigger rule machine of OpenLancer (section 3 of the M6c design, decisions D743 and D549 b). It runs the retail
/// trigger rules on a mission index alone: the active list, the pending starts in id order, the trigger states and the
/// heartbeat. The index reads the lines of each trigger through <see cref="TriggerRules"/> (TR1, TR2), and the machine
/// adds the run rules TR3 to TR13. It has no world: <see cref="ITriggerWorld"/> gives it the events and the facts. It
/// makes no ImGui, engine or file call. The dry run and the start plan use it.
/// </summary>
/// <remarks>
/// <para>The retail engine, in plain words. A start of a trigger that is active does nothing. Else the trigger becomes
/// active, its condition resets, and the machine tests it once (TR8). A met condition runs the trigger: its actions run
/// in file order, and a repeatable trigger becomes active again (TR10). <c>Act_ActTrig</c> puts its target into the
/// pending set, and the set starts its triggers after the pass, smallest id first (TR7). <c>Act_DeactTrig</c> stops its
/// target at once and removes its pending start (TR9).</para>
/// <para>The machine runs on one thread. It is not safe for two threads.</para>
/// </remarks>
public sealed class TriggerMachine
{
    private readonly MachineProgram program;
    private readonly ITriggerWorld world;
    private readonly TriggerStatus[] status;
    private readonly int[] runs;
    private readonly int[] since;
    private readonly int[] ticks;
    private readonly int[] counts;
    private readonly int[] rearmedIn;
    private readonly bool[] listed;
    private readonly List<int> active = [];
    private readonly SortedSet<uint> pending = [];
    private readonly List<int> polling = [];
    private int[]? activeCopy;
    private int[]? pollingCopy;
    private IReadOnlyList<MissionTrigger>? activeView;
    private List<MachineChange>? changes;
    private MissionEnd? pendingEnd;
    private int pass;
    private int starts;
    private bool started;

    /// <summary>A new machine for an index. No trigger is active until <see cref="StartMission"/> or
    /// <see cref="Load"/>.</summary>
    public TriggerMachine(MissionIndex index, ITriggerWorld world, MachineOptions? options = null)
        : this(MachineProgram.Of(index), world, options ?? MachineOptions.Default)
    {
    }

    private TriggerMachine(MachineProgram program, ITriggerWorld world, MachineOptions options)
    {
        this.program = program;
        this.world = world;
        Options = options;
        var count = program.Triggers.Count;
        status = new TriggerStatus[count];
        runs = new int[count];
        since = Enumerable.Repeat(-1, count).ToArray();
        ticks = new int[count];
        counts = new int[count];
        rearmedIn = new int[count];
        listed = new bool[count];
    }

    /// <summary>The text of a chain that does not end (section 3.4 of the M6c design).</summary>
    public static string ChainMessage(string trigger) =>
        $"Trigger '{trigger}' starts a chain that does not end. The game can stop responding here.";

    public MissionIndex Index => program.Index;

    public MachineOptions Options { get; }

    /// <summary>The active list, in the order in which the triggers became active.</summary>
    public IReadOnlyList<MissionTrigger> Active => activeView ??= active.Select(s => program.Triggers[s]).ToArray();

    /// <summary>The pending starts in the order of the next drain: smallest id first. After a stop at the stop point,
    /// the starts that the pass did not make (<see cref="MachineOptions.StopBefore"/>).</summary>
    public IReadOnlyList<MissionTrigger> Pending => pending.Select(id => program.Triggers[program.SlotById(id)]).ToList();

    /// <summary>The heartbeats since the start of the run.</summary>
    public int Heartbeats { get; private set; }

    /// <summary>The end of the mission, after the pass that sets it, or null.</summary>
    public MissionEnd? End { get; private set; }

    /// <summary>The stop of the run before the end of the mission, or null.</summary>
    public MachineHalt? Halt { get; private set; }

    /// <summary>True after the end of the mission or a stop. The machine then refuses new passes.</summary>
    public bool IsOver => End != null || Halt != null;

    /// <summary>True after <see cref="StartMission"/> or <see cref="Load"/>.</summary>
    public bool Started => started;

    /// <summary>True when a heartbeat can change the run: a trigger waits in the pending set, or an active trigger tests
    /// its condition at each heartbeat. A run without such work can skip heartbeats with
    /// <see cref="SkipIdle"/>.</summary>
    public bool HeartbeatsMatter => pending.Count > 0 || polling.Count > 0;

    /// <summary>The state of a trigger. A trigger that the game does not keep (TR4) is always inactive.</summary>
    public TriggerRunState StateOf(MissionTrigger trigger) =>
        program.SlotOf(trigger) is >= 0 and var slot
            ? new TriggerRunState(status[slot], runs[slot], since[slot])
            : new TriggerRunState(TriggerStatus.Inactive, 0, -1);

    /// <summary>The matches of a counting condition of an active trigger so far, and the matches that it needs (section
    /// 4.3 of the M6c design). Null for another trigger.</summary>
    public (int Count, int Needed)? CountOf(MissionTrigger trigger) =>
        program.SlotOf(trigger) is >= 0 and var slot && program.CountNeeded[slot] > 1 && status[slot] == TriggerStatus.Active
            ? (counts[slot], program.CountNeeded[slot])
            : null;

    /// <summary>The heartbeats that a timer of an active trigger still needs, or null for another trigger. A timer
    /// that counts only in space needs the same while the player is not in space.</summary>
    internal int? TimerLeft(MissionTrigger trigger) =>
        program.SlotOf(trigger) is >= 0 and var slot && program.Kinds[slot] == ConditionKind.Timer &&
        status[slot] == TriggerStatus.Active && listed[slot]
            ? Math.Max(0, TimerNeeded(slot) - ticks[slot])
            : null;

    /// <summary>
    /// Starts the mission (section 3.3 item 5): the start triggers start in id order (TR7). Their own
    /// <c>Act_ActTrig</c> targets stay pending until the next pass. A machine starts once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The machine started before.</exception>
    public MachinePass StartMission() => StartSet(program.StartSet);

    /// <summary>
    /// Loads a saved game (section 3.3 item 6, TR14): the saved triggers replace the start set. They start in id order,
    /// as the start set does, and their own <c>Act_ActTrig</c> targets stay pending until the next pass. A load uses a
    /// new machine, as a load of a saved game makes a new script.
    /// </summary>
    /// <exception cref="InvalidOperationException">The machine started before.</exception>
    public MachinePass Load(IReadOnlyList<MissionTrigger> saved) =>
        StartSet(saved.Select(program.SlotOf).Where(s => s >= 0).Distinct().OrderBy(s => program.Ids[s]).ToArray());

    /// <summary>One event pass (section 3.3 item 3): each active trigger whose condition matches the event runs, in the
    /// order of the active list. Then the pending set drains.</summary>
    public MachinePass Dispatch(WorldEvent worldEvent)
    {
        if (IsOver)
            return MachinePass.Refused;
        Begin();
        foreach (var slot in activeCopy ??= active.ToArray())
        {
            if (!Waits(slot) || program.Kinds[slot] is not (ConditionKind.Event or ConditionKind.StartTest or
                    ConditionKind.PollAndEvent))
                continue;
            var trigger = program.Triggers[slot];
            if (!world.Matches(trigger, trigger.Condition!, worldEvent))
                continue;
            // A counting condition counts its matches (section 3.5 of the M6c design). A count that the game never
            // reaches never runs the trigger.
            var needed = program.CountNeeded[slot];
            if (needed != MachineProgram.Never && ++counts[slot] >= needed)
                Run(slot);
        }
        Drain();
        return Finish();
    }

    /// <summary>One heartbeat of 1/30 s (section 3.3 item 4): the timers count and the poll conditions test (TR11).
    /// Then the pending set drains.</summary>
    public MachinePass Heartbeat()
    {
        if (IsOver)
            return MachinePass.Refused;
        Begin();
        Heartbeats++;
        if (polling.Count > 0)
        {
            // Only the triggers that test at each heartbeat, in the order of the active list.
            foreach (var slot in pollingCopy ??= polling.ToArray())
            {
                if (Waits(slot) && Polls(slot))
                    Run(slot);
            }
        }
        Drain();
        return Finish();
    }

    /// <summary>Skips heartbeats in which nothing can change: no start is pending, and no active trigger tests its
    /// condition at each heartbeat. Returns the number of skipped heartbeats, 0 when a heartbeat matters.</summary>
    public int SkipIdle(int count)
    {
        if (IsOver || HeartbeatsMatter || count <= 0)
            return 0;
        Heartbeats += count;
        return count;
    }

    /// <summary>A forced run, for the dry run and the start plan only (section 3.3 item 7): an active trigger runs as if
    /// its condition matched now. Then the pending set drains.</summary>
    /// <exception cref="ArgumentException">The trigger is not active.</exception>
    public MachinePass Force(MissionTrigger trigger)
    {
        if (IsOver)
            return MachinePass.Refused;
        var slot = program.SlotOf(trigger);
        if (slot < 0 || !listed[slot] || status[slot] != TriggerStatus.Active)
            throw new ArgumentException($"Trigger '{trigger.Nickname}' is not active.", nameof(trigger));
        Begin();
        Run(slot);
        Drain();
        return Finish();
    }

    /// <summary>
    /// Stops triggers without their actions, as <c>Act_DeactTrig</c> stops them (TR9): each active trigger leaves the
    /// active list and becomes inactive, and each pending start of the triggers goes. The start plan uses it at the end
    /// of its replay, so it also works after the stop at the stop point. The machine refuses it after the end of the
    /// mission or after another stop.
    /// </summary>
    /// <returns>The Stopped and the Cancelled changes.</returns>
    internal MachinePass StopTriggers(IEnumerable<MissionTrigger> triggers)
    {
        if (End != null || Halt is { Kind: not MachineHaltKind.StopPoint })
            return MachinePass.Refused;
        Begin();
        foreach (var slot in triggers.Select(program.SlotOf).Where(s => s >= 0).Distinct())
            Deactivate(slot);
        return Finish();
    }

    /// <summary>A copy of the state, for the snapshots of the dry run.</summary>
    public MachineState Snapshot() => new(program, Options)
    {
        Status = (TriggerStatus[])status.Clone(),
        Runs = (int[])runs.Clone(),
        Since = (int[])since.Clone(),
        Ticks = (int[])ticks.Clone(),
        Counts = (int[])counts.Clone(),
        Active = active.ToArray(),
        Pending = pending.ToArray(),
        Heartbeats = Heartbeats,
        Started = started,
        End = End,
        Halt = Halt,
    };

    /// <summary>A machine with the state of a snapshot. It gives the same passes as the machine of the snapshot.</summary>
    /// <exception cref="ArgumentException">The snapshot is of another index.</exception>
    public static TriggerMachine From(MachineState state, MissionIndex index, ITriggerWorld world)
    {
        if (!ReferenceEquals(state.Program.Index, index))
            throw new ArgumentException("The snapshot is of another mission index.", nameof(state));
        var machine = new TriggerMachine(state.Program, world, state.Options)
        {
            Heartbeats = state.Heartbeats,
            End = state.End,
            Halt = state.Halt,
        };
        state.Status.CopyTo(machine.status, 0);
        state.Runs.CopyTo(machine.runs, 0);
        state.Since.CopyTo(machine.since, 0);
        state.Ticks.CopyTo(machine.ticks, 0);
        state.Counts.CopyTo(machine.counts, 0);
        foreach (var slot in state.Active)
            machine.Join(slot);
        machine.pending.UnionWith(state.Pending);
        machine.started = state.Started;
        return machine;
    }

    // ---- The passes.

    private MachinePass StartSet(int[] slots)
    {
        if (started)
            throw new InvalidOperationException("The machine started before. A start or a load uses a new machine.");
        started = true;
        Begin();
        for (var i = 0; i < slots.Length; i++)
        {
            if (Halt != null)
            {
                // A stop at the stop point keeps the rest of the set, because the game starts it in the same pass.
                if (Halt.Kind == MachineHaltKind.StopPoint)
                    pending.UnionWith(slots[i..].Select(s => program.Ids[s]));
                break;
            }
            Start(slots[i]);
        }
        return Finish();
    }

    private void Begin()
    {
        pass++;
        starts = 0;
        changes = null;
        pendingEnd = null;
    }

    /// <summary>Applies the end of the pass, as the game applies a success or a fail after each message (MSD section
    /// 2.2).</summary>
    private MachinePass Finish()
    {
        if (pendingEnd != null && Halt == null)
        {
            End = pendingEnd;
            Record(new MachineChange(MachineChangeKind.End, End.Trigger));
        }
        var result = changes is { Count: > 0 } ? new MachinePass(changes) : MachinePass.Empty;
        changes = null;
        return result;
    }

    // Dispatch and Heartbeat walk a copy of the active list, so a run that stops or completes a trigger in the pass
    // does not change the walk. They check Waits for each trigger of the copy. The copies stay until the list changes.

    /// <summary>True for an active trigger in the list that did not re-arm in this pass (TR10).</summary>
    private bool Waits(int slot) =>
        Halt == null && listed[slot] && status[slot] == TriggerStatus.Active && rearmedIn[slot] != pass;

    /// <summary>The test of a heartbeat (TR11).</summary>
    private bool Polls(int slot)
    {
        switch (program.Kinds[slot])
        {
            case ConditionKind.True:
                return true;
            case ConditionKind.Timer:
                // The game tests the place of the player first: a timer that counts only in space is false on a base.
                if (!TimerCounts(slot))
                    return false;
                ticks[slot]++;
                return ticks[slot] >= TimerNeeded(slot);
            case ConditionKind.Watch:
                return Watched(slot);
            case ConditionKind.Poll or ConditionKind.PollAndEvent:
                var trigger = program.Triggers[slot];
                return world.Poll(trigger, trigger.Condition!);
            default:
                return false;
        }
    }

    private int TimerNeeded(int slot) =>
        Options.Timers == TimerBound.Longest ? program.TimerLongest[slot] : program.TimerShortest[slot];

    /// <summary>True when a timer counts now: always, or only in space for a timer with
    /// <c>RUN_IN_SPACE_ONLY</c>.</summary>
    private bool TimerCounts(int slot) => !program.InSpaceOnly[slot] || world.InSpace;

    /// <summary>The test of a <c>Cnd_WatchTrigger</c> line: the state of the watched trigger (CN-15).</summary>
    private bool Watched(int slot)
    {
        var target = program.WatchTarget[slot];
        if (target < 0)
            return false;
        var state = status[target];
        return program.WatchStates[slot] switch
        {
            WatchState.Active => state == TriggerStatus.Active,
            WatchState.NotActive => state != TriggerStatus.Active,
            WatchState.Inactive => state == TriggerStatus.Inactive,
            WatchState.Complete => state == TriggerStatus.Complete,
            _ => false,
        };
    }

    /// <summary>
    /// The start of a trigger (section 3.3 item 1). A trigger that is in the active list does not change. Else it
    /// becomes active, its condition resets, and the machine tests the condition once (TR8). A true test runs it.
    /// </summary>
    private void Start(int slot)
    {
        if (listed[slot])
            return;
        var trigger = program.Triggers[slot];
        if (Options.StopBefore is { } stop && program.SlotOf(stop) == slot)
        {
            Stop(MachineHaltKind.StopPoint, trigger);
            return;
        }
        if (program.Kinds[slot] == ConditionKind.None)
        {
            Stop(MachineHaltKind.NoCondition, trigger);
            return;
        }
        if (++starts > Options.ChainLimit)
        {
            Stop(MachineHaltKind.EndlessChain, trigger);
            return;
        }
        status[slot] = TriggerStatus.Active;
        since[slot] = Heartbeats;
        ticks[slot] = 0;
        counts[slot] = 0;
        Record(new MachineChange(MachineChangeKind.Started, trigger));
        if (TestAtStart(slot))
            Run(slot);
        // A trigger that is complete after its run does not join the list. A repeatable trigger joined at its re-arm.
        if (status[slot] == TriggerStatus.Active && !listed[slot])
            Join(slot);
    }

    /// <summary>
    /// The one test at the start of a trigger (TR8). The game tests each condition once without an event. The machine
    /// knows the result of the conditions that need no world: <c>Cnd_True</c> is true, a <c>Cnd_WatchTrigger</c> is
    /// true when the watched state holds now, and a <c>Cnd_Timer</c> of 0 s or less is true where the timer counts. The
    /// world gives the result of <c>Cnd_SystemEnter</c>. Each other condition waits for its event or for the next
    /// heartbeat.
    /// </summary>
    private bool TestAtStart(int slot) => program.Kinds[slot] switch
    {
        ConditionKind.True => true,
        ConditionKind.Watch => Watched(slot),
        ConditionKind.Timer => TimerNeeded(slot) <= 0 && TimerCounts(slot),
        ConditionKind.StartTest => world.TestAtStart(program.Triggers[slot], program.Triggers[slot].Condition!),
        _ => false,
    };

    /// <summary>The run of a trigger: it becomes complete, and its actions run in file order. A repeatable trigger then
    /// becomes active again with a reset condition (TR10). A complete trigger leaves the list.</summary>
    private void Run(int slot)
    {
        var trigger = program.Triggers[slot];
        status[slot] = TriggerStatus.Complete;
        runs[slot]++;
        Record(new MachineChange(MachineChangeKind.Ran, trigger));
        foreach (var action in program.Actions[slot])
        {
            switch (action.Kind)
            {
                case ActionKind.Start when action.Target < 0:
                case ActionKind.Stop when action.Target < 0:
                    Record(new MachineChange(MachineChangeKind.Ignored, trigger, action.Line, Name: action.Name));
                    break;
                case ActionKind.Start:
                    pending.Add(program.Ids[action.Target]);
                    break;
                case ActionKind.Stop:
                    Deactivate(action.Target);
                    break;
                case ActionKind.End:
                    EndBy(trigger, action.Line);
                    break;
                case ActionKind.Checkpoint:
                    Record(new MachineChange(MachineChangeKind.Checkpoint, trigger, action.Line,
                        action.Target >= 0 ? program.Triggers[action.Target] : null, action.Name));
                    break;
                default:
                    world.Effect(trigger, action.Line);
                    Record(new MachineChange(MachineChangeKind.Effect, trigger, action.Line));
                    break;
            }
        }
        if (trigger.Repeats)
        {
            status[slot] = TriggerStatus.Active;
            ticks[slot] = 0;
            counts[slot] = 0;
            rearmedIn[slot] = pass;
            Record(new MachineChange(MachineChangeKind.Rearmed, trigger));
            if (!listed[slot])
                Join(slot);
        }
        else if (listed[slot])
        {
            Leave(slot);
        }
    }

    /// <summary>The stop of a trigger (section 3.3 item 2, TR9). The trigger leaves the active list and becomes
    /// inactive, unless it is complete. Each pending start of the trigger goes.</summary>
    private void Deactivate(int slot)
    {
        var trigger = program.Triggers[slot];
        if (listed[slot] && status[slot] == TriggerStatus.Active)
        {
            status[slot] = TriggerStatus.Inactive;
            Leave(slot);
            Record(new MachineChange(MachineChangeKind.Stopped, trigger));
        }
        if (pending.Remove(program.Ids[slot]))
            Record(new MachineChange(MachineChangeKind.Cancelled, trigger));
    }

    /// <summary>The end of an <c>Act_ChangeState</c> line. The machine applies it after the pass.</summary>
    private void EndBy(MissionTrigger trigger, MissionLine line)
    {
        var state = line.Value(0)?.ToUpperInvariant();
        pendingEnd = state switch
        {
            "SUCCEED" => new MissionEnd(MissionEndKind.Success, trigger, null),
            "FAIL" => new MissionEnd(MissionEndKind.Fail, trigger, line.Value(1)),
            _ => pendingEnd,
        };
    }

    /// <summary>The drain of the pending set: the start with the smallest id first, until the set is empty (TR7). A start
    /// in the drain can add more starts.</summary>
    private void Drain()
    {
        while (pending.Count > 0 && Halt == null)
        {
            var id = pending.Min;
            pending.Remove(id);
            Start(program.SlotById(id));
        }
    }

    /// <summary>Stops the run. A stop at the stop point keeps the pending set, so the caller can read the starts that
    /// the pass did not make. The game stops at each other stop, so its pending set goes.</summary>
    private void Stop(MachineHaltKind kind, MissionTrigger trigger)
    {
        Halt = new MachineHalt(kind, trigger);
        if (kind != MachineHaltKind.StopPoint)
            pending.Clear();
        Record(new MachineChange(MachineChangeKind.Stop, trigger));
    }

    private void Join(int slot)
    {
        listed[slot] = true;
        active.Add(slot);
        if (program.PollsAt(slot))
            polling.Add(slot);
        Changed();
    }

    private void Leave(int slot)
    {
        listed[slot] = false;
        active.Remove(slot);
        if (program.PollsAt(slot))
            polling.Remove(slot);
        Changed();
    }

    private void Changed()
    {
        activeCopy = null;
        pollingCopy = null;
        activeView = null;
    }

    private void Record(MachineChange change) => (changes ??= []).Add(change);
}
