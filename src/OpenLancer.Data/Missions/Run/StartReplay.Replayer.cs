using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

internal sealed partial record StartReplay
{
    /// <summary>
    /// The machine of an approximate replay, with the waiting triggers that the path shows ran in the game (fix of the
    /// review of 2026-10-28). The replay has no world, so a trigger that waits for the world stays active, also when the
    /// game ran it long before the chosen trigger. These rules run such a trigger at the point where the path shows its
    /// event:
    /// <list type="bullet">
    /// <item>A launch of the path ran each trigger that waited for that launch: Cnd_PlayerLaunch, Cnd_SpaceEnter without
    /// a system or with the system of the launch, Cnd_LaunchComplete of the player, and Cnd_BaseExit without a base or
    /// with the base of the launch.</item>
    /// <item>The game tests Cnd_InSpace at each heartbeat. When the path shows the player in space, each trigger that
    /// waits for Cnd_InSpace = true ran. When the path shows the player docked, each trigger that waits for
    /// Cnd_InSpace = false ran. Before the first such line, a dock shows that the player was in space, and a launch
    /// shows that the player was docked.</item>
    /// <item>A trigger of the path that completes an objective shows that the objective was active. When no line of the
    /// replay made it active, the waiting trigger that makes it active ran before. The replay runs that trigger, and the
    /// triggers that it starts on the way to the objective.</item>
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
    /// </list>
    /// Such a run must not stop the replay, end the mission, run or stop a trigger of the rest of the path, or start the
    /// chosen trigger. Else the replay goes back to the state before it. A repeatable trigger does not run in this way.
    /// </summary>
    private sealed class Replayer
    {
        private readonly MissionIndex index;
        private readonly MissionTrigger chosen;
        private readonly ReplayWorld world = new();
        private readonly HashSet<string> activated = new(StringComparer.OrdinalIgnoreCase);
        private int read;

        /// <summary>True while the path shows the player in space, false while docked, null when no line tells.</summary>
        private bool? inSpace;

        /// <summary>The system where the conditions of the path show the player, or null when no condition tells.</summary>
        private string? system;

        /// <summary>The newest system key of the path before the first condition that names a system, or null. It gives
        /// the old system of the first change.</summary>
        private string? hinted;

        public Replayer(MissionIndex index, MissionTrigger chosen, MissionTrigger root)
        {
            this.index = index;
            this.chosen = chosen;
            Machine = new TriggerMachine(index, world, new MachineOptions { StopBefore = chosen });
            Changes.AddRange(Kept((root.StartsActive ? Machine.StartMission() : Machine.Load([root])).Changes));
            inSpace = root.StartsActive ? null : StartPlaces.InSpaceAt(root.Condition);
            system = StartPlaces.SystemAt(index, root.Condition);
        }

        public TriggerMachine Machine { get; private set; }

        public List<MachineChange> Changes { get; } = [];

        /// <summary>The jumps that the system rule finds, as <see cref="StartReplay.Jumps"/> tells.</summary>
        public List<int> Jumps { get; } = [];

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

        /// <summary>The objective rule, before a trigger of the path runs.</summary>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        public void Objectives(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
            Note();
            var completed = trigger.Actions.Where(a => MissionIndex.Is(a.Key, "Act_SetNNState") &&
                                                       MissionIndex.Is(a.Value(1), "COMPLETE"))
                .Select(a => a.Value(0)?.Trim()).OfType<string>().Where(o => o.Length > 0 && !activated.Contains(o))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var objective in completed)
            {
                if (Chain(t => Activates(t, objective), rest) is { } chain)
                    TryRun(chain, rest);
                Note();
            }
        }

        /// <summary>The launch rule, before a trigger of the path with a launch condition runs. The game gives the launch
        /// to each trigger that waits for it in the same pass.</summary>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        public void Launch(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
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
        public void Systems(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
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
        public void BeforeInSpace(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
            if (inSpace != null || StartPlaces.InSpaceAt(trigger.Condition) is not { } next)
                return;
            inSpace = !next;
            RunInSpace(rest);
        }

        /// <summary>The Cnd_InSpace rule, after a trigger of the path ran.</summary>
        /// <param name="rest">The rest of the path, with the chosen trigger.</param>
        public void InSpace(MissionTrigger trigger, IReadOnlyList<MissionTrigger> rest)
        {
            inSpace = StartPlaces.InSpaceAt(trigger.Condition) ?? inSpace;
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
        /// pass the chosen trigger, a trigger of the rest of the path or a repeatable trigger.</summary>
        private List<MissionTrigger>? Chain(Func<MissionTrigger, bool> goal, IReadOnlyList<MissionTrigger> rest)
        {
            var next = StartGraph.Of(index).Starts;
            var parent = new Dictionary<MissionTrigger, MissionTrigger?>();
            var queue = new Queue<MissionTrigger>();
            foreach (var trigger in Machine.Active.Concat(Machine.Pending).Where(t => Free(t, rest)))
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
                foreach (var child in next[trigger].Where(c => c != chosen && !c.Repeats && !rest.Contains(c)))
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
                    line.Value(0)?.Trim() is { Length: > 0 } name)
                    activated.Add(name);
            }
        }

        /// <summary>Runs a chain of waiting triggers in order. When a run breaks a rule of <see cref="Replayer"/>, the
        /// replay goes back to the state before the chain.</summary>
        private void TryRun(IReadOnlyList<MissionTrigger> chain, IReadOnlyList<MissionTrigger> rest)
        {
            var state = Machine.Snapshot();
            var count = Changes.Count;
            var before = rest.Distinct().Select(t => (Trigger: t, Alive: Alive(t), Machine.StateOf(t).Runs)).ToList();
            var runs = chain.Select(t => Machine.StateOf(t).Runs).ToList();
            var ok = true;
            for (var i = 0; ok && i < chain.Count; i++)
            {
                // A link can run in the pass of the link before it, for example a link with Cnd_True. That run counts.
                var ran = Machine.StateOf(chain[i]).Runs > runs[i] || Reach(chain[i], again: true) == Reached.Ran;
                ok = ran && Machine.Halt == null && Machine.End == null;
            }
            ok = ok && before.All(b => Machine.StateOf(b.Trigger).Runs == b.Runs && (!b.Alive || Alive(b.Trigger)));
            if (ok)
                return;
            Machine = TriggerMachine.From(state, index, world);
            Changes.RemoveRange(count, Changes.Count - count);
            read = Math.Min(read, count);
        }

        private bool Alive(MissionTrigger trigger) => Machine.Active.Contains(trigger) || Machine.Pending.Contains(trigger);

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
