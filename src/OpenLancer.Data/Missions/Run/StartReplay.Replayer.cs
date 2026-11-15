using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

internal sealed partial record StartReplay
{
    /// <summary>
    /// The machine of an approximate replay, with the waiting triggers that the path shows ran in the game (review of
    /// 2026-10-28, issue #46). The replay has no world and no time. So a trigger that waits for the world or for a time
    /// stays active, also when the game ran it long before the chosen trigger. These rules run such a trigger at the point
    /// where the path shows its event. The rules run in this order before each trigger of the path: timers, objectives,
    /// the early space test, systems, launch, peer events, spawns, polls, and timers again. Then the trigger of the path
    /// runs, and the space test follows. After the last trigger of the path, the fail guard rule follows.
    /// <list type="bullet">
    /// <item>Timers (issue #46). A waiting timer ran when the path shows that at least its time passed since its start.
    /// All timers count on the same heartbeats.
    /// <list type="bullet">
    /// <item>The least time. A run comes at or after the run that started it. A timer comes at least its shortest time
    /// after its start. A trigger of the path comes after the trigger of the path before it. It also comes after each
    /// run with a known time since that trigger.</item>
    /// <item>The arrival. When the player jumps, retail brings each jumper into the new system 3 s after the player. So
    /// a trigger of the path that waits for jumpers to arrive comes at least 3 s after the arrival of the player. Each
    /// ship of its condition must be a jumper. The arrival of the player comes after the trigger of the path before
    /// it.</item>
    /// <item>The run test. A waiting timer runs when its longest time from its start is at or below the least time of
    /// the next trigger of the path. A tie counts, because one heartbeat runs both timers. When the path shows no time,
    /// no timer runs. A timer of 0 s runs at its start, so it never waits.</item>
    /// <item>The order and the exceptions. The timers run in the order of their starts, then in the order of their
    /// times. A timer that counts only in space does not run when the path showed the player docked after its start. A
    /// waiting trigger that can stop the timer does not keep it waiting.</item>
    /// <item>The two passes. The run test can find a timer only at a later trigger of the path than in the game. So the
    /// replay makes two passes. Pass 1 uses the run test. It records each timer that it runs, with its start and the
    /// next trigger of the path. Pass 2 does not use the run test. It runs each recorded timer before the first trigger
    /// of the path after its start, unless that trigger is known to come first. Pass 2 never runs a timer later in the
    /// path than pass 1. When the replay goes back from such a run, pass 2 tries the timer again in the next timer call.
    /// When pass 1 runs no timer, pass 2 gives the same result, so the replay skips it.</item>
    /// <item>The start of a timer. Pass 2 knows a timer by its name and its start. The start is the trigger whose run
    /// started the timer and the number of that run, or the first starts of the replay. So when a trigger starts the
    /// timer again, pass 2 does not run it for the old start.</item>
    /// <item>Known to come first. A trigger of the path can be known to come before the time of a timer. Then a trigger
    /// of the path or the first starts of the replay started the timer. The first starts also start the root, so the
    /// time of the root counts too. Only Cnd_Timer and Cnd_True triggers of the path come after that start. The longest
    /// times of these timers add up to less than the shortest time of the timer. A run of another rule does not show
    /// where the time starts, because the game can run that trigger earlier.</item>
    /// </list></item>
    /// <item>The game tests Cnd_InSpace at each heartbeat. When the path shows the player in space, each trigger that
    /// waits for Cnd_InSpace = true ran. When the path shows the player docked, each trigger that waits for
    /// Cnd_InSpace = false ran. Before the first such line, a dock shows that the player was in space, and a launch
    /// shows that the player was docked.</item>
    /// <item>A trigger of the path that completes an objective shows that the objective was active. When no line of the
    /// replay made it active, the waiting trigger that makes it active ran before. The replay runs that trigger, and the
    /// triggers that it starts on the way to the objective. Each link of a run of a rule shows the same, for example a
    /// trigger on the way to the exit from a system. The links of a chain of this rule do not get this rule
    /// again.</item>
    /// <item>A condition of the path can show the player in another system than the condition before it: an arrival,
    /// Cnd_SpaceEnter, or a base condition. Then the player left the old system. The game sends the exit from the old
    /// system, then the entry into the new system. So each trigger that waited for that exit (Cnd_SystemExit) ran. A
    /// waiting trigger that starts such a trigger ran before it, for example a trigger near the jump gate. Each trigger
    /// that waited for the entry (Cnd_SystemEnter) also ran. A trigger that waits for the end of the jump
    /// (Cnd_JumpInComplete) does not run, because it can start a scene of the arrival, and the path does not show the
    /// end of that scene. Only a condition shows a change, because the system key of a trigger can name another system
    /// than the system where it runs. Before the first condition that names a system, the newest system key of the path
    /// gives the old system. The condition of the chosen trigger does not count, because the chosen trigger only
    /// starts.</item>
    /// <item>A launch of the path ran each trigger that waited for that launch: Cnd_PlayerLaunch, Cnd_SpaceEnter without
    /// a system or with the system of the launch, Cnd_LaunchComplete of the player, and Cnd_BaseExit without a base or
    /// with the base of the launch.</item>
    /// <item>Peer events (issue #75). The game gives one event to each trigger that waits for it. So the condition of a
    /// trigger of the path shows its event, and each waiting trigger of the same event ran.
    /// <list type="bullet">
    /// <item>The events. A base entry must name a base. It matches Cnd_BaseEnter of that base or of any base. A space
    /// exit matches Cnd_SpaceExit of the same system or of any system. A room entry matches Cnd_LocEnter of the same room
    /// and base. A lane entry matches Cnd_TLEntered of the same ship and the same two rings. The end of a comm line
    /// matches Cnd_CommComplete of the same line.</item>
    /// <item>The lane exit and the sender. A lane exit matches Cnd_TLExited of the same ship. Its first ring is ANY or
    /// the first ring of the path. It has no second ring, or the second ring of the path. A sender of a waiting
    /// Cnd_CommComplete must be the sender of the path.</item>
    /// <item>The dock. A dock removes the ship of the player from space before the base entry. So a base entry of the
    /// path also shows the space exit from the system of the base. This is not true when the path already showed the
    /// player docked. The triggers of that exit run first.</item>
    /// <item>The start. The waiting trigger must start before the event. It must start at the first starts of the
    /// replay, or in a run of an earlier trigger of the path. A start by a run of a rule does not count, because the
    /// path does not show when that run came in the game.</item>
    /// <item>The scenes. A run at a base entry or a room entry can start a base scene, also a run of the space exit of a
    /// dock. The game plays such a scene at the base, and the player acts again only after its end. So when a later
    /// trigger of the path follows, other than the chosen trigger, each trigger that waits for the end of that scene ran.
    /// These triggers run after all runs of the event.</item>
    /// <item>The limits. Only the conditions of the path show events, not the runs of the rules. The chosen trigger only
    /// starts, so its condition shows no event.</item>
    /// </list></item>
    /// <item>Spawns (issue #51). The game counts a destroyed object or a picked-up loot only for an object that the
    /// mission made. So a trigger of the path that waits for Cnd_Destroyed or Cnd_LootAcquired shows that the mission
    /// made the objects of its target. The count of the condition gives the number of objects that it needs. A count below
    /// 1 or no count needs one object. The trigger never needs more objects than its target has. When the replay made
    /// fewer of them, and only one waiting trigger has a spawn line of a missing object, that trigger ran before.</item>
    /// <item>Polls (issue #50). The game tests a distance condition of the player at each heartbeat. So a waiting poll
    /// ran when the path shows the player on its true side.
    /// <list type="bullet">
    /// <item>The polls. A poll is Cnd_DistVec of the player, or Cnd_DistShip of the player and an object of the system
    /// files. A poll with a hold time above 0 does not count, because one place does not show that the player stayed.
    /// The game tests the point of Cnd_DistVec in each system. Cnd_DistShip needs the player and the object in one
    /// system.</item>
    /// <item>The places. The condition of a trigger of the path gives a place with a slack. A base entry, a launch from
    /// a base or a room entry gives the dock object of the base, with 3,000 units. A lane entry or exit of the player
    /// gives the ring, with 1,000 units. An inside distance condition of the player gives its point or its object,
    /// with its radius. Cnd_SystemEnter from a named system gives the jump object that leads back, with 1,000
    /// units.</item>
    /// <item>The jump places. A jump of the system rule gives two places with 1,000 units. Before the exit, the player
    /// was at the jump object of the old system that leads to the new system. When the old system has only one such
    /// object, the player jumped through it, also when the dock rules block it now. When the old system has more such
    /// objects, the dock rules of the replay must allow exactly one of them. These rules are the newest
    /// Act_PlayerCanDock line with its exceptions, and the Act_LockDock lines of the player. After the entry, the player
    /// was at the jump object of the new system that leads back.</item>
    /// <item>The run test. A jump object gives a place only when it is the only such object of its system, or the
    /// object that the dock rules allow. Each point within the slack of the place must be on the true side of the poll.
    /// So for an outside poll, the distance minus the slack is more than the radius. For an inside poll, the distance
    /// plus the slack is less than the radius. A waiting trigger that can stop the poll does not keep it
    /// waiting.</item>
    /// </list></item>
    /// <item>Fail guards (issue #46). After the last trigger of the path, a waiting timer can lead to a mission fail in
    /// a chain of at most two starts. When such a timer started before the last jump of the path, it stops without its
    /// actions. This is not a retail rule. It follows the intended play: the player left the system of the guard, and
    /// the mission went on. The chosen trigger and a repeatable timer stay. The rule looks only at the pass that gives
    /// the result.</item>
    /// </list>
    /// Such a run must not stop the replay, end the mission, run or stop a trigger of the rest of the path, or start the
    /// chosen trigger. Else the replay goes back to the state before it. A repeatable trigger does not run in this way.
    /// </summary>
    private sealed partial class Replayer
    {
        /// <summary>The least time between the arrival of the player and the arrival of the jumpers in the new
        /// system.</summary>
        private const int JumperDelay = 3 * MachineProgram.HeartbeatsPerSecond;

        private readonly MissionIndex index;
        private readonly MachineProgram program;
        private readonly MissionTrigger chosen;
        private readonly IReadOnlyList<MissionTrigger> path;
        private readonly ReplayWorld world = new();
        private readonly HashSet<string> activated = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The objectives of <see cref="activated"/> in the order of their lines, each with the index of its
        /// change. A run that goes back removes the objectives of its changes.</summary>
        private readonly List<(string Name, int Change)> activations = [];
        private readonly StartTimeline timeline;

        /// <summary>The timers that pass 2 runs at their early steps, as <see cref="TimerRuns"/> of pass 1 gives them.
        /// Null in pass 1.</summary>
        private readonly IReadOnlyDictionary<TimerKey, int>? early;

        private int read;

        /// <summary>The number of changes when the path last showed the player docked, or -1.</summary>
        private int docked = -1;

        /// <summary>The system of the last arrival of the player, and the position of the trigger of the path before it,
        /// or null.</summary>
        private (string System, int Before)? arrival;

        /// <summary>True while the path shows the player in space, false while docked, null when no line tells.</summary>
        private bool? inSpace;

        /// <summary>The system where the conditions of the path show the player, or null when no condition tells.</summary>
        private string? system;

        /// <summary>The newest system key of the path before the first condition that names a system, or null. It gives
        /// the old system of the first change.</summary>
        private string? hinted;

        /// <param name="path">The path from the root to the chosen trigger.</param>
        /// <param name="early">For pass 2: the <see cref="TimerRuns"/> of pass 1. Null for pass 1.</param>
        public Replayer(MissionIndex index, MissionTrigger chosen, IReadOnlyList<MissionTrigger> path,
            IReadOnlyDictionary<TimerKey, int>? early = null)
        {
            this.index = index;
            this.chosen = chosen;
            this.path = path;
            this.early = early;
            program = MachineProgram.Of(index);
            var root = path[0];
            Machine = new TriggerMachine(index, world, new MachineOptions { StopBefore = chosen });
            timeline = new StartTimeline(program, root.StartsActive ? program.StartSet : [program.SlotOf(root)]);
            Changes.AddRange(Kept((root.StartsActive ? Machine.StartMission() : Machine.Load([root])).Changes));
            inSpace = root.StartsActive ? null : StartPlaces.InSpaceAt(root.Condition);
            if (inSpace == false)
                docked = Changes.Count;
            system = StartPlaces.SystemAt(index, root.Condition);
        }

        public TriggerMachine Machine { get; private set; }

        public List<MachineChange> Changes { get; } = [];

        /// <summary>The jumps that the system rule finds, as <see cref="StartReplay.Jumps"/> tells.</summary>
        public List<int> Jumps { get; } = [];

        /// <summary>The timers that the timer rule ran in pass 1, each with its start. The value is the position of the
        /// next trigger of the path when the timer ran.</summary>
        public Dictionary<TimerKey, int> TimerRuns { get; } = [];

        /// <summary>The rules before a trigger of the path runs, in the order of the summary of the class.</summary>
        /// <param name="position">The position of the trigger in the path.</param>
        public void Before(int position)
        {
            var trigger = path[position];
            var rest = Rest(position);
            Timers(position, rest);
            Objectives(trigger, rest);
            BeforeInSpace(trigger, rest);
            Systems(position, rest);
            // Only the least time of pass 1 uses the arrival.
            if (early == null)
                Arrival(position);
            Launch(trigger, rest);
            Peers(position, rest);
            Spawns(position, rest);
            Polls(position, rest);
            // Again after the other rules: their runs can show more time.
            Timers(position, rest);
        }

        /// <summary>The rules after a trigger of the path ran.</summary>
        /// <param name="position">The position of the trigger in the path.</param>
        public void After(int position)
        {
            var trigger = path[position];
            timeline.Sync(Changes);
            timeline.MarkPath(position, trigger);
            InSpace(trigger, Rest(position + 1));
        }

        /// <summary>The triggers of the path from a position on, with the chosen trigger.</summary>
        private List<MissionTrigger> Rest(int position) => path.Skip(position).Append(chosen).ToList();

        /// <summary>Runs one trigger: a pending trigger starts at the next heartbeat, and an active trigger runs as if its
        /// condition is met now. The chosen trigger only starts.</summary>
        /// <param name="again">True for a waiting trigger that must run now, also when it ran before.</param>
        public Reached Reach(MissionTrigger trigger, bool again = false)
        {
            var runs = Machine.StateOf(trigger).Runs;
            for (var step = 0; ; step++)
            {
                if (Machine.Halt is { Kind: MachineHaltKind.StopPoint })
                    return Reached.Ran;
                if (Machine.Halt != null)
                    return Reached.Halt;
                if (Machine.End != null)
                    return Reached.End;
                var state = Machine.StateOf(trigger);
                if (trigger != chosen && (state.Runs > runs || !again && state.Runs > 0))
                    return Reached.Ran;
                if (step >= StepsPerTrigger)
                    return Reached.NotActive;
                if (Machine.Pending.Contains(trigger))
                    Changes.AddRange(Kept(Machine.Heartbeat().Changes));
                else if (trigger != chosen && state.Status == TriggerStatus.Active && Machine.Active.Contains(trigger))
                    Changes.AddRange(Kept(Machine.Force(trigger).Changes));
                else
                    return Reached.NotActive;
            }
        }

        /// <summary>The timer rule, before a trigger of the path runs (see the summary of the class). Pass 1 runs each
        /// waiting timer whose time the path shows. Pass 2 runs each timer of pass 1 at its early step. One call tries each
        /// waiting timer at most once.</summary>
        /// <param name="position">The position of the next trigger of the path.</param>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        private void Timers(int position, IReadOnlyList<MissionTrigger> rest)
        {
            var next = path[position];
            if (next == chosen)
                return;
            var tried = new HashSet<MissionTrigger>();
            while (true)
            {
                timeline.Sync(Changes);
                // The next trigger of the path ran in the pass of an earlier one, so no time passed before it.
                if (!timeline.Started(next, out _))
                    return;
                MissionTrigger? due = null;
                int dueStart = 0, dueTime = 0;
                foreach (var waiting in Machine.Active.Concat(Machine.Pending).Distinct())
                {
                    var slot = program.SlotOf(waiting);
                    if (slot < 0 || program.Kinds[slot] != ConditionKind.Timer || tried.Contains(waiting) ||
                        !Free(waiting, rest) || !timeline.Started(waiting, out var start))
                        continue;
                    var time = program.TimerLongest[slot];
                    if (due != null && (start > dueStart || start == dueStart && time >= dueTime))
                        continue;
                    if (program.InSpaceOnly[slot] && docked > timeline.ChangeOf(start))
                        continue;
                    // A timer of 0 s runs at its start. So a waiting timer has a time of at least one heartbeat, and a
                    // bound of 0 runs no timer.
                    if (early == null
                            ? time <= timeline.Bound(start, position, next)
                            : early.TryGetValue(KeyOf(slot, start), out var late) &&
                              (position >= late || !KnownBefore(position, slot, start)))
                        (due, dueStart, dueTime) = (waiting, start, time);
                }
                if (due == null)
                    return;
                tried.Add(due);
                // The run removes the start of the timer, so read the key first.
                var key = KeyOf(program.SlotOf(due), dueStart);
                if (TryRun([due], rest) && early == null)
                    TimerRuns.TryAdd(key, position);
            }
        }

        private TimerKey KeyOf(int slot, int start)
        {
            var (starter, run) = timeline.SourceOf(start);
            return new TimerKey(slot, starter, run);
        }

        /// <summary>True when the next trigger of the path is known to come before the time of a waiting timer. A run of
        /// the path or the first starts of the replay started the timer (<see cref="StartTimeline.PositionOf"/>). After
        /// that position, each trigger of the path up to the next trigger has Cnd_Timer or Cnd_True. The longest times of
        /// these timers add up to less than the shortest time of the waiting timer.</summary>
        /// <param name="position">The position of the next trigger of the path.</param>
        private bool KnownBefore(int position, int slot, int start)
        {
            if (timeline.PositionOf(start) is not { } from)
                return false;
            var sum = 0;
            for (var k = from + 1; k <= position; k++)
            {
                var step = program.SlotOf(path[k]);
                if (program.Kinds[step] == ConditionKind.Timer)
                    sum += program.TimerLongest[step];
                else if (program.Kinds[step] != ConditionKind.True)
                    return false;
            }
            return sum < program.TimerShortest[slot];
        }

        /// <summary>The arrival part of the timer rule. A trigger of the path can wait for ships to arrive in the system of
        /// the last arrival of the player. When each ship is a jumper, that trigger comes at least 3 s after the arrival.
        /// An <c>Act_Jumper</c> line of the replay changes the flag of its ship.</summary>
        /// <param name="position">The position of the trigger in the path.</param>
        private void Arrival(int position)
        {
            var trigger = path[position];
            if (trigger == chosen || arrival is not { } last || !trigger.ConditionIs("Cnd_NPCSystemEnter") ||
                !Names(trigger.Condition!.Value(0), last.System))
                return;
            var condition = trigger.Condition!;
            var ships = Enumerable.Range(1, Math.Max(0, condition.Count - 1)).Select(condition.Value)
                .Select(n => n?.Trim()).OfType<string>().Where(n => n.Length > 0 && !MissionIndex.Is(n, "Player"))
                .ToList();
            if (ships.Count == 0)
                return;
            var flags = new StartWorld.JumperFlags(index);
            foreach (var change in Changes)
            {
                if (change is { Kind: MachineChangeKind.Effect, Line: { } line } && MissionIndex.Is(line.Key, "Act_Jumper"))
                    flags.Apply(line);
            }
            if (ships.SelectMany(s => StartSetup.Members(index, s).DefaultIfEmpty(s)).All(flags.Of))
                timeline.Delay(position, last.Before, JumperDelay);
        }

        /// <summary>The objective rule, before a trigger of the path runs, and before each link of a run of a rule.
        /// The chain of the rule does not pass the trigger, because the trigger completes the objective after the chain.
        /// The links of the chain do not get the objective rule again.</summary>
        /// <param name="trigger">The trigger of the path or the link.</param>
        /// <param name="rest">The next trigger and the rest of the path, with the chosen trigger.</param>
        private void Objectives(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
            Note();
            // The rule runs before each link of each run of a rule, so it reads the lines without a query.
            var completed = new List<string>();
            foreach (var action in trigger.Actions)
            {
                if (MissionIndex.Is(action.Key, "Act_SetNNState") && MissionIndex.Is(action.Value(1), "COMPLETE") &&
                    action.Value(0)?.Trim() is { Length: > 0 } objective && !activated.Contains(objective) &&
                    !completed.Contains(objective, StringComparer.OrdinalIgnoreCase))
                    completed.Add(objective);
            }
            foreach (var objective in completed)
            {
                if (Chain(t => Activates(t, objective), rest, trigger) is { } chain)
                    TryRun(chain, rest, objectives: false);
                Note();
            }
        }

        /// <summary>The launch rule, before a trigger of the path with a launch condition runs. The game gives the launch
        /// to each trigger that waits for it in the same pass.</summary>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        private void Launch(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
            if (!StartPlaces.IsLaunch(trigger.Condition))
                return;
            foreach (var peer in Machine.Active.Concat(Machine.Pending).Distinct()
                         .Where(t => Free(t, rest) && SameLaunch(trigger, t.Condition!)).ToList())
                TryRun([peer], rest);
        }

        /// <summary>The system rule, before a trigger of the path runs. The chosen trigger only starts in the replay, so
        /// its condition does not show a new system.</summary>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        private void Systems(int position, IReadOnlyList<MissionTrigger> rest)
        {
            var trigger = path[position];
            if (trigger == chosen)
                return;
            var next = StartPlaces.SystemAt(index, trigger.Condition);
            if (next == null)
            {
                hinted = StartPlaces.Hint(index, trigger) ?? hinted;
                return;
            }
            var old = system ?? hinted;
            system = next;
            if (old == null || MissionIndex.Is(old, next))
                return;
            arrival = (next, position - 1);
            // The player was at the jump object of the old system before the exit.
            if (Departure(old, next) is { } departure)
                RunPolls(departure, rest);
            // The triggers on the way to a trigger of the exit ran before the jump, for example near the jump gate.
            var tried = new HashSet<MissionTrigger>();
            while (Chain(t => !tried.Contains(t) && !Alive(t) && t.Condition is { } c && Exits(c, old, next), rest) is
                   { } chain)
            {
                tried.Add(chain[^1]);
                TryRun(chain, rest);
            }
            // Then the exit ran each trigger that waited for it. The player arrived, and the entry did the same.
            RunWaiting(c => Exits(c, old, next), rest);
            Jumps.Add(Changes.Count);
            RunWaiting(c => Enters(c, next, old), rest);
            // The player arrived at the jump object of the new system that leads back.
            if (OnlyJump(next, old) is { } entry)
                RunPolls(entry, rest);
        }

        /// <summary>Runs each waiting trigger that a rule can run and whose condition matches, in the order of the
        /// game.</summary>
        private void RunWaiting(Func<MissionLine, bool> matches, IReadOnlyList<MissionTrigger> rest)
        {
            foreach (var peer in Machine.Active.Concat(Machine.Pending).Distinct()
                         .Where(t => Free(t, rest) && matches(t.Condition!)).ToList())
                TryRun([peer], rest);
        }

        /// <summary>True when a condition waits for the exit of the player from a system to another system:
        /// Cnd_SystemExit with that system or ANY, and with no second system, with ANY or with the other system.</summary>
        private static bool Exits(MissionLine condition, string from, string to) =>
            MissionIndex.Is(condition.Key, "Cnd_SystemExit") && Names(condition.Value(0), from) &&
            Names(condition.Value(1), to);

        /// <summary>True when a condition waits for the entry of the player into a system: Cnd_SystemEnter with that
        /// system or ANY, and with no system before it, with ANY or with the old system.</summary>
        private static bool Enters(MissionLine condition, string to, string from) =>
            MissionIndex.Is(condition.Key, "Cnd_SystemEnter") && Names(condition.Value(0), to) &&
            Names(condition.Value(1), from);

        /// <summary>True when a system value of a line names a system: the same name, ANY, or no value.</summary>
        private static bool Names(string? value, string system) =>
            value?.Trim() is not { Length: > 0 } name || MissionIndex.Is(name, "ANY") || MissionIndex.Is(name, system);

        /// <summary>The Cnd_InSpace rule, before a trigger of the path runs. When no line told the place of the player
        /// yet, a dock shows that the player was in space before it, and a launch shows that the player was docked
        /// before it.</summary>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        private void BeforeInSpace(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
            if (inSpace != null || StartPlaces.InSpaceAt(trigger.Condition) is not { } next)
                return;
            inSpace = !next;
            if (next)
                docked = Changes.Count;
            RunInSpace(rest);
        }

        /// <summary>The Cnd_InSpace rule, after a trigger of the path ran.</summary>
        /// <param name="rest">The rest of the path, with the chosen trigger.</param>
        private void InSpace(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
            if (StartPlaces.InSpaceAt(trigger.Condition) is { } next)
            {
                inSpace = next;
                if (!next)
                    docked = Changes.Count;
            }
            RunInSpace(rest);
        }

        private void RunInSpace(IReadOnlyList<MissionTrigger> rest)
        {
            if (inSpace is not { } space)
                return;
            foreach (var peer in Machine.Active.Concat(Machine.Pending).Distinct()
                         .Where(t => Free(t, rest) && t.ConditionIs("Cnd_InSpace") && StartPlaces.InSpaceAt(t.Condition) == space)
                         .ToList())
                TryRun([peer], rest);
        }

        /// <summary>True for a waiting trigger that the rules can run: active or pending, not the chosen trigger, not a
        /// trigger of the rest of the path, and not repeatable.</summary>
        private bool Free(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest) =>
            trigger != chosen && !trigger.Repeats && !rest.Contains(trigger) && trigger.Condition != null && Alive(trigger);

        /// <summary>True when a waiting launch condition matches the launch of a trigger of the path.</summary>
        private static bool SameLaunch(MissionTrigger launch, MissionLine waits)
        {
            if (!StartPlaces.IsLaunch(waits))
                return false;
            var own = launch.Condition!;
            if (MissionIndex.Is(waits.Key, "Cnd_SpaceEnter") && Word(waits, "system") is { } system)
            {
                var launchSystem = (MissionIndex.Is(own.Key, "Cnd_SpaceEnter") ? Word(own, "system") : null) ??
                                   Word(launch.SystemHint);
                return MissionIndex.Is(launchSystem, system);
            }
            if (MissionIndex.Is(waits.Key, "Cnd_BaseExit") && Word(waits, "base") is { } dock)
                return MissionIndex.Is(own.Key, "Cnd_BaseExit") && MissionIndex.Is(Word(own, "base"), dock);
            return true;
        }

        /// <summary>The shortest chain of starts from a waiting trigger to a trigger of a goal, or null. The chain does not
        /// pass the chosen trigger, a trigger of the rest of the path, a repeatable trigger or the trigger to skip.</summary>
        private List<MissionTrigger>? Chain(Func<MissionTrigger, bool> goal, IReadOnlyList<MissionTrigger> rest,
            MissionTrigger? skip = null)
        {
            var next = StartGraph.Of(index).Starts;
            var parent = new Dictionary<MissionTrigger, MissionTrigger?>();
            var queue = new Queue<MissionTrigger>();
            foreach (var trigger in Machine.Active.Concat(Machine.Pending).Where(t => t != skip && Free(t, rest)))
            {
                if (parent.TryAdd(trigger, null))
                    queue.Enqueue(trigger);
            }
            while (queue.Count > 0)
            {
                var trigger = queue.Dequeue();
                if (goal(trigger))
                {
                    var chain = new List<MissionTrigger>();
                    for (var at = trigger; at != null; at = parent[at])
                        chain.Add(at);
                    chain.Reverse();
                    return chain;
                }
                foreach (var child in next[trigger].Where(c => c != chosen && c != skip && !c.Repeats && !rest.Contains(c)))
                {
                    if (parent.TryAdd(child, trigger))
                        queue.Enqueue(child);
                }
            }
            return null;
        }

        /// <summary>True when a line of a trigger makes an objective active: Act_SetNNObj, or Act_SetNNState with
        /// ACTIVE.</summary>
        private static bool Activates(MissionTrigger trigger, string objective) =>
            trigger.Actions.Any(a => MissionIndex.Is(a.Value(0), objective) && MakesActive(a));

        private static bool MakesActive(MissionLine line) =>
            MissionIndex.Is(line.Key, "Act_SetNNObj") ||
            MissionIndex.Is(line.Key, "Act_SetNNState") && MissionIndex.Is(line.Value(1), "ACTIVE");

        /// <summary>Reads the objectives that the new effects of the replay make active.</summary>
        private void Note()
        {
            for (; read < Changes.Count; read++)
            {
                if (Changes[read] is { Kind: MachineChangeKind.Effect, Line: { } line } && MakesActive(line) &&
                    line.Value(0)?.Trim() is { Length: > 0 } name && activated.Add(name))
                    activations.Add((name, read));
            }
        }

        /// <summary>Runs a chain of waiting triggers in order. Before each link that did not run yet, the objective rule
        /// runs. When a run breaks a rule of <see cref="Replayer"/>, the replay goes back to the state before the
        /// chain.</summary>
        /// <param name="objectives">False for a chain of the objective rule. Its links do not get the objective rule
        /// again.</param>
        /// <returns>True when the chain ran, false when the replay went back.</returns>
        private bool TryRun(IReadOnlyList<MissionTrigger> chain, IReadOnlyList<MissionTrigger> rest, bool objectives = true)
        {
            var state = Machine.Snapshot();
            var count = Changes.Count;
            var before = rest.Distinct().Select(t => (Trigger: t, Alive: Alive(t), Machine.StateOf(t).Runs)).ToList();
            var runs = chain.Select(t => Machine.StateOf(t).Runs).ToList();
            var ok = true;
            for (var i = 0; ok && i < chain.Count; i++)
            {
                // A link that completes an objective shows that the objective was active before it.
                if (objectives && Machine.StateOf(chain[i]).Runs == runs[i])
                    Objectives(chain[i], rest);
                // A link can run in the pass of the link before it, for example a link with Cnd_True. That run counts.
                var ran = Machine.StateOf(chain[i]).Runs > runs[i] || Reach(chain[i], again: true) == Reached.Ran;
                ok = ran && Machine.Halt == null && Machine.End == null;
            }
            ok = ok && before.All(b => Machine.StateOf(b.Trigger).Runs == b.Runs && (!b.Alive || Alive(b.Trigger)));
            if (ok)
                return true;
            Machine = TriggerMachine.From(state, index, world);
            Changes.RemoveRange(count, Changes.Count - count);
            timeline.Cut(count);
            read = Math.Min(read, count);
            while (activations.Count > 0 && activations[^1].Change >= count)
            {
                activated.Remove(activations[^1].Name);
                activations.RemoveAt(activations.Count - 1);
            }
            return false;
        }

        private bool Alive(MissionTrigger trigger) => Machine.Active.Contains(trigger) || Machine.Pending.Contains(trigger);

        /// <summary>A timer with its start, in words that stay the same in each pass. It holds the slot of the timer, the
        /// slot of the trigger whose run started it and the number of that run. The first starts have the starter
        /// -1.</summary>
        public readonly record struct TimerKey(int Timer, int Starter, int Run);

        /// <summary>The name of a value of a line, or null for an empty value or a word such as no_params, ANY or
        /// ALL.</summary>
        private static string? Word(MissionLine line, string value) => Word(line.ValueOf(value));

        private static string? Word(string? text) =>
            text?.Trim() is { Length: > 0 } name && !MissionIndex.Is(name, "no_params") && !MissionIndex.Is(name, "ANY") &&
            !MissionIndex.Is(name, "ALL")
                ? name
                : null;
    }
}
