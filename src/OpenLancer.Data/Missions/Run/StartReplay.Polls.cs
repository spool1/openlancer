using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Data.Missions.Run;

internal sealed partial record StartReplay
{
    /// <summary>The poll rule of the replay (issue #50). The summary of the class tells the rule.</summary>
    private sealed partial class Replayer
    {
        /// <summary>The slack of a place at the dock object of a base. The dock point of a planet or of a big station
        /// can be far from the center of the object. So this slack is larger than the slack of a jump object.</summary>
        private const float BaseSlack = 3000;

        /// <summary>The slack of a place at a jump gate or a jump hole. In both games, such an object has a radius of
        /// 600 units, and its dock sphere has a radius of at most 233 units. So at a jump, the player is within 833 units
        /// of the object. The slack rounds that up.</summary>
        private const float JumpSlack = 1000;

        /// <summary>The slack of a place at a trade lane ring. The player enters or leaves a trade lane near its
        /// ring.</summary>
        private const float LaneSlack = 1000;

        /// <summary>The polls of the condition of a trigger of the path, after the launch rule. The chosen trigger only
        /// starts in the replay, so its condition gives no place.</summary>
        /// <param name="position">The position of the trigger in the path.</param>
        /// <param name="rest">The trigger and the rest of the path, with the chosen trigger.</param>
        private void Polls(int position, IReadOnlyList<MissionTrigger> rest)
        {
            var trigger = path[position];
            if (trigger != chosen && PlaceOf(trigger) is { } place)
                RunPolls(place, rest);
        }

        /// <summary>Runs each waiting poll that a place shows on its true side.</summary>
        /// <param name="rest">The next trigger and the rest of the path, with the chosen trigger.</param>
        private void RunPolls(Place place, IReadOnlyList<MissionTrigger> rest)
        {
            foreach (var waiting in Machine.Active.Concat(Machine.Pending).Distinct().Where(t => Free(t, rest)).ToList())
            {
                // An earlier run of this loop can stop the trigger.
                if (Free(waiting, rest) && PollOf(waiting) is { } poll && Shows(poll, place))
                    TryRun([waiting], rest);
            }
        }

        /// <summary>The place of the player that the condition of a trigger of the path shows, or null.</summary>
        private Place? PlaceOf(MissionTrigger trigger)
        {
            if (trigger.Condition is not { } condition)
                return null;
            var world = index.World;
            var key = condition.Key;
            if (MissionIndex.Is(key, "Cnd_BaseEnter") || MissionIndex.Is(key, "Cnd_BaseExit") ||
                MissionIndex.Is(key, "Cnd_LocEnter"))
                return condition.ValueOf("base") is { } dock && world.DockOf(dock) is { } item
                    ? new Place(item.Position, BaseSlack, item.System)
                    : null;
            if (MissionIndex.Is(key, "Cnd_TLEntered") || MissionIndex.Is(key, "Cnd_TLExited"))
                return MissionIndex.Is(condition.ValueOf("ship"), "Player") && condition.ValueOf("ring") is { } ring &&
                       world.ObjectOf(ring) is { } lane
                    ? new Place(lane.Position, LaneSlack, lane.System)
                    : null;
            if (MissionIndex.Is(key, "Cnd_SystemEnter"))
                return condition.ValueOf("system") is { } into && condition.ValueOf("previous") is { } from &&
                       !MissionIndex.Is(from, "ANY")
                    ? OnlyJump(into, from)
                    : null;
            if (StartPlaces.DistanceOf(condition) is not { Inside: true, Radius: { } radius } distance)
                return null;
            if (distance.Point is { } point)
                return new Place(point, radius, StartPlaces.Hint(index, trigger));
            return Fixed(distance.Other) is { } near ? new Place(near.Position, radius, near.System) : null;
        }

        /// <summary>The place of the jump object of a system that leads to another system. Null when the system has no
        /// such object or more than one.</summary>
        private Place? OnlyJump(string system, string to) =>
            index.World.JumpsTo(system, to) is [var only] ? new Place(only.Position, JumpSlack, only.System) : null;

        /// <summary>The place where the player leaves the old system at a jump: the jump object of the old system that
        /// leads to the new system. When the old system has more such objects, the dock rules of the replay must allow
        /// exactly one of them.</summary>
        private Place? Departure(string old, string next)
        {
            var jumps = index.World.JumpsTo(old, next);
            var picked = jumps.Count == 1 ? jumps : jumps.Where(DockAllowed).ToList();
            return picked is [var only] ? new Place(only.Position, JumpSlack, only.System) : null;
        }

        /// <summary>True when the dock rules of the replay let the player dock with an object. The newest
        /// Act_PlayerCanDock line allows or blocks each dock, and its list holds the exceptions. An Act_LockDock line of
        /// the player locks or unlocks one object.</summary>
        private bool DockAllowed(WorldObject item)
        {
            var allowed = true;
            var locked = false;
            foreach (var change in Changes)
            {
                if (change is not { Kind: MachineChangeKind.Effect, Line: { } line })
                    continue;
                if (MissionIndex.Is(line.Key, "Act_PlayerCanDock"))
                {
                    var from = line.Name?.Value("exceptions")?.Index ?? line.Count;
                    var except = Enumerable.Range(from, Math.Max(0, line.Count - from))
                        .Any(i => MissionIndex.Is(line.Value(i), item.Nickname));
                    allowed = StartWorld.IsTrue(line.ValueOf("allowed")) != except;
                }
                else if (MissionIndex.Is(line.Key, "Act_LockDock") && MissionIndex.Is(line.ValueOf("who"), "Player") &&
                         MissionIndex.Is(line.ValueOf("gate"), item.Nickname))
                    locked = MissionIndex.Is(line.ValueOf("action"), "lock");
            }
            return allowed && !locked;
        }

        /// <summary>The poll of a waiting trigger, or null. A poll is a distance condition of the player with a radius
        /// and no hold time above 0. It is Cnd_DistVec, or Cnd_DistShip with an object of the system files.</summary>
        private Poll? PollOf(MissionTrigger trigger)
        {
            if (StartPlaces.DistanceOf(trigger.Condition) is not { Radius: { } radius, Hold: false } distance)
                return null;
            // The game tests the point of Cnd_DistVec in each system.
            if (distance.Point is { } point)
                return new Poll(point, radius, distance.Inside, null);
            return Fixed(distance.Other) is { } near
                ? new Poll(near.Position, radius, distance.Inside, near.System)
                : null;
        }

        /// <summary>The object of the system files with a name, or null for a name of an object of the script.</summary>
        private WorldObject? Fixed(string? name) =>
            name != null && !index.IsScriptObject(name) ? index.World.ObjectOf(name) : null;

        /// <summary>True when a place shows the player on the true side of a poll. A poll of an object of the system
        /// files needs a place in the same system. Each point within the slack of the place counts. For an outside poll,
        /// the nearest point must be outside the radius. For an inside poll, the farthest point must be inside
        /// it.</summary>
        private static bool Shows(Poll poll, Place place)
        {
            if (poll.System != null && (place.System == null || !MissionIndex.Is(poll.System, place.System)))
                return false;
            var distance = Vector3.Distance(poll.Center, place.Point);
            return poll.Inside ? distance + place.Slack < poll.Radius : distance - place.Slack > poll.Radius;
        }

        /// <summary>A place of the player: a point, the slack around it, and its system or null.</summary>
        private readonly record struct Place(Vector3 Point, float Slack, string? System);

        /// <summary>A poll of the distance of the player: the center, the radius, the side and the system of the object.
        /// The system is null for a point, because the game tests a point in each system.</summary>
        private readonly record struct Poll(Vector3 Center, float Radius, bool Inside, string? System);
    }
}
