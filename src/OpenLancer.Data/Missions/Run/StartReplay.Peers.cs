using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Missions.Run;

internal sealed partial record StartReplay
{
    /// <summary>The peer-event rule of the replay (issue #75). The summary of the class tells the rule.</summary>
    private sealed partial class Replayer
    {
        /// <summary>The peer-event rule, after the launch rule. The condition of a trigger of the path shows its event, so
        /// each waiting trigger of the same event ran. The chosen trigger only starts in the replay, so its condition
        /// gives no event.</summary>
        /// <param name="position">The position of the trigger in the path.</param>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        private void Peers(int position, IReadOnlyList<MissionTrigger> rest)
        {
            var trigger = path[position];
            if (trigger == chosen || trigger.Condition is not { } condition)
                return;
            var from = Changes.Count;
            // A dock removes the ship of the player from space before the base entry. So it sends the space exit first.
            if (inSpace != false && Docks(condition) is { } dock)
                RunPeers(position, rest, waits => LeavesSpace(waits, index.World.SystemOfBase(dock)));
            RunPeers(position, rest, waits => SameEvent(condition, waits));
            // The scene rule needs a later trigger of the path other than the chosen trigger, the last trigger of the path.
            if ((MissionIndex.Is(condition.Key, "Cnd_BaseEnter") || MissionIndex.Is(condition.Key, "Cnd_LocEnter")) &&
                position + 1 < path.Count - 1)
                EndScenes(from, rest);
        }

        /// <summary>Runs each waiting trigger that waits for an event and started before it, in the order of the
        /// game.</summary>
        /// <param name="position">The position of the trigger of the path that shows the event.</param>
        /// <param name="waitsFor">True for a condition that waits for the event.</param>
        private void RunPeers(int position, IReadOnlyList<MissionTrigger> rest, Func<MissionLine, bool> waitsFor)
        {
            foreach (var waiting in Machine.Active.Concat(Machine.Pending).Distinct().Where(t => Free(t, rest)).ToList())
            {
                timeline.Sync(Changes);
                // An earlier run of this loop can stop the trigger.
                if (Free(waiting, rest) && waitsFor(waiting.Condition!) && StartedBefore(waiting, position))
                    TryRun([waiting], rest);
            }
        }

        /// <summary>True when a waiting trigger started before the event of a trigger of the path. It started at the
        /// first starts of the replay, or in a run of an earlier trigger of the path. A start by a run of a rule does not
        /// count, because the path does not show when that run came in the game.</summary>
        private bool StartedBefore(MissionTrigger waiting, int position) =>
            timeline.Started(waiting, out var start) && timeline.PositionOf(start) is { } from && from < position;

        /// <summary>The scene rule. A base entry plays each base scene that the runs of its event started, also the runs
        /// of the space exit of the dock. The player acts again only after the end of the scene. So a later trigger of
        /// the path shows that each such scene played to its end, after each run of the event. Each waiting trigger of
        /// the end of such a scene ran.</summary>
        /// <param name="from">The number of changes before the runs of the event.</param>
        private void EndScenes(int from, IReadOnlyList<MissionTrigger> rest)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = from; i < Changes.Count; i++)
            {
                if (Changes[i] is { Kind: MachineChangeKind.Effect, Line: { } line } &&
                    MissionIndex.Is(line.Key, "Act_AddRTC") && line.ValueOf("file") is { } file)
                    files.Add(file);
            }
            if (files.Count > 0)
                RunWaiting(c => MissionIndex.Is(c.Key, "Cnd_RTCDone") && c.ValueOf("file") is { } file && files.Contains(file),
                    rest);
        }

        /// <summary>The base of a base entry of the path, or null. The base entry must name a base.</summary>
        private static string? Docks(MissionLine condition) =>
            MissionIndex.Is(condition.Key, "Cnd_BaseEnter") ? Word(condition, "base") : null;

        /// <summary>True when a waiting condition waits for the space exit of a dock: Cnd_SpaceExit of any system, or of
        /// the system of the base.</summary>
        /// <param name="system">The system of the base, or null when the world does not know it.</param>
        private static bool LeavesSpace(MissionLine waits, string? system) =>
            MissionIndex.Is(waits.Key, "Cnd_SpaceExit") &&
            (Word(waits, "system") is not { } exit || system != null && MissionIndex.Is(exit, system));

        /// <summary>True when a waiting condition waits for the event that a condition of the path shows. The game
        /// matches the values of each event in its own way.</summary>
        private static bool SameEvent(MissionLine path, MissionLine waits)
        {
            var key = path.Key.Trim();
            if (!MissionIndex.Is(waits.Key, key))
                return false;
            switch (key.ToLowerInvariant())
            {
                case "cnd_baseenter":
                    return Word(path, "base") != null && (Word(waits, "base") == null || Same(path, waits, "base"));
                case "cnd_spaceexit":
                    return Word(waits, "system") == null || Same(path, waits, "system");
                case "cnd_locenter":
                    return Same(path, waits, "room") && Same(path, waits, "base");
                case "cnd_tlentered":
                    return Same(path, waits, "ship") && Same(path, waits, "ring") && Same(path, waits, "otherRing");
                case "cnd_tlexited":
                    // A first ring of ANY and no second ring match each ring.
                    return Same(path, waits, "ship") &&
                           (MissionIndex.Is(waits.ValueOf("ring"), "ANY") || Same(path, waits, "ring")) &&
                           (waits.ValueOf("otherRing") == null || Same(path, waits, "otherRing"));
                case "cnd_commcomplete":
                    // A condition without a sender matches each sender.
                    return Same(path, waits, "line") && (waits.ValueOf("sender") == null || Same(path, waits, "sender"));
                default:
                    return false;
            }
        }

        /// <summary>True when a value of a waiting condition names the thing that the same value of the path condition
        /// names. The value of the path must name one thing, not ANY.</summary>
        private static bool Same(MissionLine path, MissionLine waits, string value) =>
            Word(path, value) is { } name && MissionIndex.Is(waits.ValueOf(value), name);
    }
}
