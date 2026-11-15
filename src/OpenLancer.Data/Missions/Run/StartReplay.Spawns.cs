using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

internal sealed partial record StartReplay
{
    /// <summary>The spawn rule of the replay (issue #51). The summary of the class tells the rule.</summary>
    private sealed partial class Replayer
    {
        /// <summary>The spawn rule, after the peer-event rule. A trigger of the path that waits for objects to leave space
        /// shows that the mission made them. When the replay made too few of them, and only one waiting trigger can
        /// make the rest, that trigger ran before. The chosen trigger only starts in the replay, so its condition needs
        /// no objects.</summary>
        /// <param name="position">The position of the trigger in the path.</param>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        private void Spawns(int position, IReadOnlyList<MissionTrigger> rest)
        {
            var trigger = path[position];
            if (trigger == chosen || StartSetup.GoneObject(trigger.Condition) is not { } target ||
                program.SlotOf(trigger) is not (>= 0 and var slot))
                return;
            var members = StartSetup.Members(index, target);
            // The count of the condition, or 1 for a count that waits for the last object or never runs.
            var need = Math.Min(Math.Max(1, program.CountNeeded[slot]), members.Count);
            var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in Changes)
            {
                if (change is { Kind: MachineChangeKind.Effect, Line: { } line })
                    made.UnionWith(Spawned(line).Where(members.Contains));
            }
            if (made.Count >= need)
                return;
            var spawners = Machine.Active.Concat(Machine.Pending).Distinct().Where(t => Free(t, rest) &&
                t.Actions.Any(a => Spawned(a).Any(m => members.Contains(m) && !made.Contains(m)))).ToList();
            if (spawners is [var only])
                TryRun([only], rest);
        }

        /// <summary>The objects that a line spawns, or none for a line that is not a spawn.</summary>
        private IEnumerable<string> Spawned(MissionLine line) =>
            StartSetup.IsSpawn(line) && line.Value(0) is { } name ? StartSetup.Members(index, name) : [];
    }
}
