using System;
using System.Collections.Generic;

namespace OpenLancer.Data.Missions.Run;

/// <summary>
/// The least time between the runs of an approximate replay, for the timer rule of the replay (issue #46). The timeline
/// reads the Ran changes of the replay in order, and it keeps the newest start of each trigger as the machine does. A
/// start of an active trigger does nothing. A stop removes an active trigger and a start of the same action list. A
/// stop, then a start in one action list starts the trigger again. The times are in heartbeats.
/// </summary>
/// <remarks>
/// The least time of a run from an anchor (the start of a waiting timer):
/// <list type="number">
/// <item>A run whose starter is at or after the anchor comes at least at the time of its starter. A timer comes at least
/// its shortest time after its starter. This applies to the runs of the path and to the runs of the rules.</item>
/// <item>A run of the path also comes at or after the run of the path before it. It also comes at or after each run with
/// a known time since then.</item>
/// <item>A run of the path can have a delay: it comes at least a number of heartbeats after an earlier run of the path.
/// An arrival of jumpers after the player has such a delay.</item>
/// <item>Each other run has no known time.</item>
/// </list>
/// </remarks>
internal sealed class StartTimeline
{
    /// <summary>The start of a trigger that the first starts of the replay made: the start set or the load of the
    /// root.</summary>
    public const int First = -1;

    /// <summary>No start of the trigger.</summary>
    private const int NoStart = -2;

    /// <summary>No known time.</summary>
    private const int Unknown = -1;

    private readonly MachineProgram program;
    private readonly List<Run> runs = [];

    /// <summary>The newest start of each waiting trigger by slot: the index of the run that started it, or
    /// <see cref="First"/>.</summary>
    private readonly Dictionary<int, int> starts = [];

    /// <summary>The run of each position of the path that ran.</summary>
    private readonly Dictionary<int, int> pathRuns = [];

    /// <summary>The position of the path of each run of the path.</summary>
    private readonly Dictionary<int, int> positions = [];

    /// <summary>The number of runs of each trigger by slot.</summary>
    private readonly Dictionary<int, int> numbers = [];

    private readonly Dictionary<int, (int From, int Heartbeats)> delays = [];

    /// <summary>The least times from each anchor, until the timeline changes.</summary>
    private readonly Dictionary<int, (int[] Times, int Since)> cache = [];

    /// <summary>The number of changes that the timeline read.</summary>
    private int read;

    /// <param name="first">The slots of the first starts of the replay.</param>
    public StartTimeline(MachineProgram program, IEnumerable<int> first)
    {
        this.program = program;
        foreach (var slot in first)
            starts[slot] = First;
    }

    /// <summary>Reads the new changes of the replay.</summary>
    public void Sync(IReadOnlyList<MachineChange> changes)
    {
        for (; read < changes.Count; read++)
        {
            if (changes[read].Kind == MachineChangeKind.Ran)
                Add(read, changes[read].Trigger);
        }
    }

    /// <summary>Removes the runs of the changes from a count on, when the replay goes back.</summary>
    public void Cut(int count)
    {
        while (runs.Count > 0 && runs[^1].Change >= count)
        {
            var at = runs.Count - 1;
            var undo = runs[at].Undo;
            for (var i = undo.Count - 1; i >= 0; i--)
                Set(undo[i].Slot, undo[i].Before);
            if (positions.Remove(at, out var position))
                pathRuns.Remove(position);
            if (runs[at].Number == 1)
                numbers.Remove(runs[at].Slot);
            else
                numbers[runs[at].Slot] = runs[at].Number - 1;
            runs.RemoveAt(at);
            cache.Clear();
        }
        read = Math.Min(read, count);
    }

    /// <summary>Marks the newest run of a trigger as the run of a position of the path. The replay marks each position
    /// once, after its trigger ran.</summary>
    public void MarkPath(int position, MissionTrigger trigger)
    {
        var slot = program.SlotOf(trigger);
        for (var at = runs.Count - 1; at >= 0; at--)
        {
            if (runs[at].Slot != slot)
                continue;
            pathRuns[position] = at;
            positions[at] = position;
            cache.Clear();
            return;
        }
    }

    /// <summary>Adds a delay: the run of a position of the path comes at least a number of heartbeats after the run of
    /// an earlier position. The first delay of a position stays.</summary>
    public void Delay(int position, int from, int heartbeats)
    {
        if (from >= 0 && from < position && delays.TryAdd(position, (from, heartbeats)))
            cache.Clear();
    }

    /// <summary>The newest start of a waiting trigger: the index of the run that started it, or <see cref="First"/>.
    /// False when the timeline knows no start.</summary>
    public bool Started(MissionTrigger trigger, out int start) =>
        starts.TryGetValue(program.SlotOf(trigger), out start);

    /// <summary>The index of the change of a start, or -1 for <see cref="First"/>.</summary>
    public int ChangeOf(int start) => start == First ? -1 : runs[start].Change;

    /// <summary>The source of a start, in words that stay the same in each pass of the replay. It is the slot of the
    /// trigger whose run made the start, and the number of that run of the trigger. <see cref="First"/> gives (-1,
    /// 0).</summary>
    public (int Starter, int Run) SourceOf(int start) => start == First ? (-1, 0) : (runs[start].Slot, runs[start].Number);

    /// <summary>The position of the path where the time of a start begins: the position of the run of the path that made
    /// it. <see cref="First"/> gives -1, because the first starts also start the root, at position 0. Null when a run of
    /// a rule made the start, because the game can run such a trigger earlier than the replay does.</summary>
    public int? PositionOf(int start) =>
        start == First ? -1 : positions.TryGetValue(start, out var position) ? position : null;

    /// <summary>
    /// The least time of the next run of the path from an anchor. It is the largest known time since the last run of the
    /// path. A timer of the path comes at least its shortest time after its own start, and a delay of its position also
    /// counts. The time is 0 when the timeline knows no time.
    /// </summary>
    /// <param name="anchor">A start, as <see cref="Started"/> gives it.</param>
    /// <param name="position">The position of the next trigger of the path.</param>
    public int Bound(int anchor, int position, MissionTrigger next)
    {
        var (times, bound) = TimesFrom(anchor);
        var slot = program.SlotOf(next);
        if (slot >= 0 && program.Kinds[slot] == ConditionKind.Timer && starts.TryGetValue(slot, out var start) &&
            TimeOf(anchor, start, times) is var basis and not Unknown)
            bound = Math.Max(bound, basis + program.TimerShortest[slot]);
        if (Delayed(anchor, position, times) is var delayed and not Unknown)
            bound = Math.Max(bound, delayed);
        return bound;
    }

    /// <summary>The least time of each run from an anchor. Also the largest known time since the last run of the path, or
    /// 0.</summary>
    private (int[] Times, int Since) TimesFrom(int anchor)
    {
        if (cache.TryGetValue(anchor, out var known))
            return known;
        var times = new int[runs.Count];
        Array.Fill(times, Unknown);
        var since = 0;
        for (var at = Math.Max(0, anchor + 1); at < runs.Count; at++)
        {
            var run = runs[at];
            var time = Unknown;
            if (TimeOf(anchor, run.By, times) is var basis and not Unknown)
                time = basis + (program.Kinds[run.Slot] == ConditionKind.Timer ? program.TimerShortest[run.Slot] : 0);
            if (positions.TryGetValue(at, out var position))
            {
                // A run of the path comes after each known run since the run of the path before it.
                time = Math.Max(Math.Max(time, since), Delayed(anchor, position, times));
                since = time;
            }
            else if (time != Unknown)
            {
                since = Math.Max(since, time);
            }
            times[at] = time;
        }
        return cache[anchor] = (times, since);
    }

    /// <summary>The least time of a start from an anchor: 0 at the anchor, the time of a later run, else
    /// <see cref="Unknown"/>.</summary>
    private static int TimeOf(int anchor, int start, int[] times) =>
        start == anchor ? 0 : start > anchor ? times[start] : Unknown;

    /// <summary>The least time of a run of the path from its delay, or <see cref="Unknown"/>.</summary>
    private int Delayed(int anchor, int position, int[] times) =>
        delays.TryGetValue(position, out var delay) && pathRuns.TryGetValue(delay.From, out var from) &&
        TimeOf(anchor, from, times) is var basis and not Unknown
            ? basis + delay.Heartbeats
            : Unknown;

    private void Add(int change, MissionTrigger trigger)
    {
        // The machine runs only the triggers of its program, so each run has a slot.
        var slot = program.SlotOf(trigger);
        var undo = new List<(int Slot, int Before)>();
        var index = runs.Count;
        var by = starts.GetValueOrDefault(slot, NoStart);
        // A repeatable trigger starts again at its run.
        Change(slot, trigger.Repeats ? index : NoStart, undo);
        var started = new List<int>();
        foreach (var action in program.Actions[slot])
        {
            if (action.Target < 0)
                continue;
            if (action.Kind == ActionKind.Start && !started.Contains(action.Target))
                started.Add(action.Target);
            else if (action.Kind == ActionKind.Stop)
            {
                started.Remove(action.Target);
                Change(action.Target, NoStart, undo);
            }
        }
        foreach (var target in started)
        {
            if (!starts.ContainsKey(target))
                Change(target, index, undo);
        }
        var number = numbers.GetValueOrDefault(slot) + 1;
        numbers[slot] = number;
        runs.Add(new Run(change, slot, number, by, undo));
        cache.Clear();
    }

    private void Change(int slot, int start, List<(int Slot, int Before)> undo)
    {
        undo.Add((slot, starts.GetValueOrDefault(slot, NoStart)));
        Set(slot, start);
    }

    private void Set(int slot, int start)
    {
        if (start == NoStart)
            starts.Remove(slot);
        else
            starts[slot] = start;
    }

    /// <summary>A run of the timeline. <c>Number</c> counts the runs of its trigger, from 1. <c>By</c> is the start that
    /// it used: a run index, <see cref="First"/> or <see cref="NoStart"/>. <c>Undo</c> holds the changes of the starts that
    /// undo the run.</summary>
    private sealed record Run(int Change, int Slot, int Number, int By, List<(int Slot, int Before)> Undo);
}
