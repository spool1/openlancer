using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

internal sealed partial record StartReplay
{
    /// <summary>The fail guard rule of the replay (issue #46). The summary of the class tells the rule.</summary>
    private sealed partial class Replayer
    {
        /// <summary>The most starts from a fail guard to the line that fails the mission.</summary>
        private const int GuardLinks = 2;

        /// <summary>The fail guard rule, after the last trigger of the path in the pass that gives the result. Each
        /// waiting timer that started before the last jump of the path and leads to a mission fail stops without its
        /// actions. The chosen trigger and a repeatable timer stay.</summary>
        public void StopFailGuards()
        {
            if (Jumps.Count == 0)
                return;
            timeline.Sync(Changes);
            var jump = Jumps[^1];
            var guards = Machine.Active.Concat(Machine.Pending).Distinct().Where(t =>
                t != chosen && !t.Repeats && program.SlotOf(t) is >= 0 and var slot &&
                program.Kinds[slot] == ConditionKind.Timer && Fails(slot) && timeline.Started(t, out var start) &&
                timeline.ChangeOf(start) < jump).ToList();
            // The world reads only the runs and the effects of the replay, so the stops do not go into the changes.
            if (guards.Count > 0)
                Machine.StopTriggers(guards);
        }

        /// <summary>True when a trigger fails the mission (Act_ChangeState = FAIL), or starts a trigger that does in a
        /// chain of at most <see cref="GuardLinks"/> starts.</summary>
        private bool Fails(int slot)
        {
            var seen = new HashSet<int> { slot };
            var level = new List<int> { slot };
            for (var links = 0; level.Count > 0; links++)
            {
                if (level.Any(s => program.Actions[s].Any(a =>
                        a.Kind == ActionKind.End && MissionIndex.Is(a.Line.ValueOf("state"), "FAIL"))))
                    return true;
                if (links == GuardLinks)
                    return false;
                level = level.SelectMany(s => program.Actions[s])
                    .Where(a => a.Kind == ActionKind.Start && a.Target >= 0 && seen.Add(a.Target)).Select(a => a.Target)
                    .ToList();
            }
            return false;
        }
    }
}
