using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Export;

namespace OpenLancer.Data.Missions.Run;

/// <summary>The kind of an object of the setup of the world.</summary>
public enum StartObjectKind
{
    Ship,
    Formation,
    Solar,
    Loot,

    /// <summary>An <c>Act_LightFuse</c> line on an object.</summary>
    Fuse,
}

/// <summary>An object that the setup leaves out, because it is in another system at the chosen trigger, or a fuse line
/// that the setup removes (section 5.3, note of 2026-10-28 after step 7 of the M6c design).</summary>
/// <param name="Object">The nickname of the object, or the target of a fuse.</param>
/// <param name="System">The system of the object at the chosen trigger, or null when no line makes the target of a
/// fuse.</param>
/// <param name="Trigger">The trigger of the spawn line of the object, or of the fuse line.</param>
public sealed record LeftOutObject(string Object, StartObjectKind Kind, string? System, string Trigger)
{
    /// <summary>The fuse of a fuse line, else null.</summary>
    public string? Fuse { get; init; }

    /// <summary>The kind of the target of a fuse line when the script has the target, else null: an object of a system
    /// file.</summary>
    public StartObjectKind? TargetKind { get; init; }
}

/// <summary>Why a ship travels with the player at the chosen trigger.</summary>
[Flags]
public enum CompanionSigns
{
    None = 0,

    /// <summary>Its last own orders follow, trail or dock with the player or with another companion.</summary>
    Orders = 1,

    /// <summary>A FollowPlayer list of the player names it.</summary>
    PlayerFormation = 2,

    /// <summary>It jumped with the player, and no later line placed it.</summary>
    Jump = 4,

    /// <summary>A nag keeps the player near it, or a waiting trigger watches the tether of it.</summary>
    Escort = 8,
}

/// <summary>Where a companion starts.</summary>
public enum CompanionPoint
{
    /// <summary>Its point is near the start anchor already. The setup does not move it.</summary>
    Near,

    /// <summary>A jump-here trigger of the script places it near the player. The setup keeps that offset.</summary>
    JumpHere,

    /// <summary>The offset of its Follow order.</summary>
    Follow,

    /// <summary>A slot to the side of the start anchor.</summary>
    Slot,

    /// <summary>The game data does not give the dock of the base of the start, so the point does not change.</summary>
    Kept,
}

/// <summary>A ship or a formation that travels with the player at the chosen trigger (a companion), and where it
/// starts.</summary>
/// <param name="Object">The nickname of the ship or the formation.</param>
/// <param name="Trigger">The trigger of its spawn line.</param>
public sealed record StartCompanion(string Object, StartObjectKind Kind, CompanionSigns Signs, string Trigger,
    CompanionPoint Point)
{
    /// <summary>The system that it jumped from with the player, or null.</summary>
    public string? JumpedFrom { get; init; }

    /// <summary>The jump-here trigger of <see cref="CompanionPoint.JumpHere"/>, else null.</summary>
    public string? PointTrigger { get; init; }

    /// <summary>The point where it starts, or null when no line gives one.</summary>
    public Vector3? Position { get; init; }

    /// <summary>True when the setup moves it next to the player.</summary>
    public bool Moved => Point is CompanionPoint.JumpHere or CompanionPoint.Follow or CompanionPoint.Slot;
}

/// <summary>Where the setup builds the world: the start system, and the point and the heading next to which the
/// companions start (the start anchor).</summary>
/// <param name="Point">The start anchor, or null when the game data does not give the dock of the base of a base
/// start.</param>
/// <param name="Base">The base of a base start, else null.</param>
internal sealed record StartAnchor(string System, Vector3? Point, Quaternion Turn, string? Base = null)
{
    /// <summary>The anchor of a start, or null when the start system is not known. A play gives the place after the
    /// start check, also for the Scene camera. Without it, a space start uses the suggested point, a base start uses the
    /// dock of its base, and a start at the Scene camera has no anchor.</summary>
    public static StartAnchor? Of(MissionIndex index, StartPlace place, StartSavePlace? play)
    {
        if (play != null)
            return play.Base is { } dock
                ? Docked(index, play.System, dock)
                : new StartAnchor(play.System.Trim(), play.Position, play.Orientation);
        return place.Kind switch
        {
            StartPlaceKind.Space when place.System != null => new StartAnchor(place.System, place.Position, place.Orientation),
            StartPlaceKind.Base when place.Base != null && index.World.SystemOfBase(place.Base) is { } system =>
                Docked(index, system, place.Base),
            _ => null,
        };
    }

    /// <summary>The anchor of a base start: <see cref="StartPlaces.Offset"/> units in front of the dock object of the
    /// base, with its heading.</summary>
    private static StartAnchor Docked(MissionIndex index, string system, string dock)
    {
        if (index.World.DockOf(dock) is not { } item)
            return new StartAnchor(system.Trim(), null, Quaternion.Identity, dock.Trim());
        var (point, turn) = StartPlaces.InFront(item);
        return new StartAnchor(system.Trim(), point, turn, dock.Trim());
    }
}

/// <summary>
/// The world of the start system (section 5.3, note of 2026-10-28 after step 7 of the M6c design). The setup trigger of
/// the world makes only the objects of the start system. A pass over the replay follows each object: its system, its
/// point, its jumper flag and its last own orders. The objects of other systems stay out, with each line that names
/// only such objects. The ships that travel with the player (the companions) start next to the player. Each other object
/// keeps the point of its lines.
/// </summary>
internal static class StartWorld
{
    /// <summary>The distance of "near the player": the default distance of a Follow order.</summary>
    public const float NearDistance = 2500;

    /// <summary>The largest offset from the player that a jump-here trigger gives to a companion.</summary>
    public const float JumpHereDistance = 5000;

    /// <summary>The step between two side slots.</summary>
    public const float SlotStep = 300;

    private static readonly string[] SpawnKeys =
        ["Act_SpawnShip", "Act_SpawnShipRel", "Act_SpawnFormation", "Act_SpawnSolar", "Act_SpawnLoot"];

    private static readonly string[] MoveKeys = ["Act_RelocateShip", "Act_RelocateForm"];

    private static readonly MissionValueKind[] ObjectKinds =
    [
        MissionValueKind.Object, MissionValueKind.Group, MissionValueKind.Ship, MissionValueKind.Formation,
        MissionValueKind.Solar, MissionValueKind.Loot,
    ];

    /// <summary>The world lines of the setup, the objects that it leaves out, the companions and the warnings.</summary>
    public sealed record Result(IReadOnlyList<(string Key, IReadOnlyList<string> Values)> Lines,
        IReadOnlyList<LeftOutObject> LeftOut, IReadOnlyList<StartCompanion> Companions, IReadOnlyList<string> Warnings);

    /// <summary>The warning of a waiting trigger whose condition names only objects that the setup leaves out.</summary>
    public static string WaitingWarning(string trigger, string item, string system) =>
        $"Trigger '{trigger}' waits for {item}. At this trigger, {item} is in {system}, so the setup does not make it. " +
        "This trigger does not run in this play.";

    /// <summary>The warning of a waiting trigger whose condition names a label with members that the setup leaves
    /// out.</summary>
    public static string LabelWarning(string trigger, string label, int count) =>
        $"Trigger '{trigger}' waits for label {label}. The setup does not make {count.ToString(CultureInfo.InvariantCulture)} " +
        "of its ships, because they are in another system. This trigger can run before it runs in the game.";

    /// <summary>The warning of a start at the Scene camera, before the play gives the point of the camera.</summary>
    public const string CameraWarning = "The start is at the Scene camera. When the play starts, the setup makes only the " +
                                        "objects of the camera system. It puts the ships that travel with the player next to " +
                                        "the camera.";

    /// <summary>The warning of a base start whose dock object the game data does not give.</summary>
    public static string NoDockWarning(string dock) =>
        $"The game data does not give the dock of base {dock}. The ships that travel with the player start at the points " +
        "of their lines.";

    /// <summary>Builds the world lines of the setup.</summary>
    /// <param name="keep">For each effect of the replay, true when the setup keeps its line.</param>
    public static Result Build(MissionIndex index, StartReplay replay, IReadOnlyList<bool> keep, StartAnchor anchor) =>
        new Builder(index, replay, keep, anchor).Build();

    /// <summary>A kept world line of the replay, with the system of the player when it ran.</summary>
    private sealed class Entry(int at, MissionTrigger trigger, MissionLine line, string? system)
    {
        public int At { get; } = at;

        public MissionTrigger Trigger { get; } = trigger;

        public MissionLine Line { get; } = line;

        public string? System { get; } = system;

        public bool Removed { get; set; }

        /// <summary>New values of the line, or null.</summary>
        public IReadOnlyList<string>? Values { get; set; }

        /// <summary>The lines that the setup adds after this line.</summary>
        public List<(string Key, IReadOnlyList<string> Values)> After { get; } = [];

        public bool Is(string key) => MissionIndex.Is(Line.Key, key);

        public bool IsAny(string[] keys) => keys.Any(Is);
    }

    /// <summary>A step of the pass: a kept world line, or a jump of the player.</summary>
    private readonly record struct Step(Entry? Entry, string? From, string? To);

    /// <summary>One object of the setup: a ship, a formation, a solar or a loot, with its track.</summary>
    private sealed class Unit(string name, StartObjectKind kind, IReadOnlySet<string> members, Entry spawn)
    {
        public string Name { get; } = name;

        public StartObjectKind Kind { get; } = kind;

        public IReadOnlySet<string> Members { get; } = members;

        public Entry Spawn { get; } = spawn;

        public Entry Placing { get; set; } = spawn;

        public string? System { get; set; }

        public Vector3? Point { get; set; }

        public Quaternion? Turn { get; set; }

        public string? JumpedFrom { get; set; }

        /// <summary>The order list of its last own orders, and the index of the line that gave them, or -1 for the spawn
        /// line or the record.</summary>
        public (string List, int At)? Orders { get; set; }

        public bool Destroyed { get; set; }
    }

    private sealed class Builder(MissionIndex index, StartReplay replay, IReadOnlyList<bool> keep, StartAnchor anchor)
    {
        private readonly StartPlaces.WorldFacts facts = StartPlaces.World(index, replay);
        private readonly List<Entry> entries = [];
        private readonly List<Step> steps = [];
        private readonly List<Unit> units = [];
        private readonly Dictionary<Entry, Unit> spawned = [];
        private readonly Dictionary<string, Unit> byMember = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> jumpers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string List, int At)> playerLists = [];
        private readonly HashSet<string> leftOutMembers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> madeMembers = new(StringComparer.OrdinalIgnoreCase);

        public Result Build()
        {
            Steps();
            DoubleSpawns();
            Track();
            var companions = Companions();
            var leftOut = units.Where(u => !u.Destroyed && !InStart(u.System)).ToList();
            foreach (var unit in leftOut)
                leftOutMembers.UnionWith(unit.Members.Append(unit.Name));
            var rows = new List<LeftOutObject>();
            rows.AddRange(leftOut.Select(u => new LeftOutObject(u.Name, u.Kind, u.System, u.Spawn.Trigger.Nickname)));
            Remove(leftOut.ToHashSet(), rows);
            var warnings = new List<string>();
            var placed = Place(companions, warnings);
            warnings.AddRange(Waiting());
            var lines = new List<(string Key, IReadOnlyList<string> Values)>();
            foreach (var entry in entries.Where(e => !e.Removed))
            {
                lines.Add((entry.Line.Key.Trim(), entry.Values ?? entry.Line.Line.Select(RetailText.Of).ToList()));
                lines.AddRange(entry.After);
            }
            return new Result(lines, rows, placed, warnings);
        }

        /// <summary>The kept world lines and the jumps of the player, in the order of the replay. After the last run, the
        /// player goes to the start system.</summary>
        private void Steps()
        {
            string? current = null;
            var effect = 0;
            for (var i = 0; i < replay.Changes.Count; i++)
            {
                var change = replay.Changes[i];
                var system = facts.Systems[i];
                if (change.Kind == MachineChangeKind.Ran)
                {
                    if (current != null && system != null && !MissionIndex.Is(current, system))
                        steps.Add(new Step(null, current, system));
                    current = system ?? current;
                    continue;
                }
                if (change is not { Kind: MachineChangeKind.Effect, Line: { } line })
                    continue;
                if (effect < keep.Count && keep[effect] && line.Name?.Start == StartRole.World)
                {
                    var entry = new Entry(entries.Count, change.Trigger, line, system);
                    entries.Add(entry);
                    steps.Add(new Step(entry, null, null));
                }
                effect++;
            }
            if (current != null && !MissionIndex.Is(current, anchor.System))
                steps.Add(new Step(null, current, anchor.System));
        }

        /// <summary>
        /// Rule 4: a second spawn line of an object makes a new object. The replay has no world, so it does not see that
        /// the first object left space. The earlier spawn line goes, with each line between the two spawn lines that
        /// names the object. A line that names it only through a label stays.
        /// </summary>
        private void DoubleSpawns()
        {
            var last = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!entry.IsAny(SpawnKeys) || entry.Line.Value(0) is not { } name)
                    continue;
                if (last.TryGetValue(name, out var earlier) && !earlier.Removed)
                {
                    var members = Members(name);
                    var between = entries.Skip(earlier.At + 1).Take(entry.At - earlier.At - 1).ToList();
                    if (!between.Any(e => !e.Removed && e.Is("Act_Destroy") && e.Line.Value(0) is { } target &&
                                          Members(target).Overlaps(members)))
                    {
                        earlier.Removed = true;
                        foreach (var line in between.Where(e => NamesDirectly(e.Line, name, members)))
                            line.Removed = true;
                    }
                }
                last[name] = entry;
            }
        }

        /// <summary>True when a line names an object by its own name or by the name of a member, not through a
        /// label.</summary>
        private static bool NamesDirectly(MissionLine line, string name, IReadOnlySet<string> members) =>
            Names(line).Any(n => MissionIndex.Is(n, name) || members.Contains(n.Trim()));

        /// <summary>
        /// Rule 3: the track of each object. A spawn line makes it in the system of its record, else in the system of the
        /// player. A solar and a loot always go to the system of the player. <c>Act_RelocateShip</c> moves it into the
        /// system of the player, and makes it when it does not exist, as the fork does. At a jump of the player, each
        /// object in the old system whose jumper flag is on goes to the new system without a point.
        /// </summary>
        private void Track()
        {
            foreach (var step in steps)
            {
                if (step.Entry is not { Removed: false } entry)
                {
                    if (step.Entry == null)
                        Jump(step.From!, step.To!);
                    continue;
                }
                var line = entry.Line;
                var name = line.Value(0);
                if (name == null || MissionIndex.Is(name, "Player") && !entry.Is("Act_GiveObjList"))
                    continue;
                if (entry.IsAny(SpawnKeys))
                    Spawn(entry, name);
                else if (entry.IsAny(MoveKeys))
                    Move(entry, name);
                else if (entry.Is("Act_Jumper"))
                {
                    var on = line.Value(1) is { } flag && (MissionIndex.Is(flag, "true") || flag == "1");
                    foreach (var member in Members(name))
                        jumpers[member] = on;
                }
                else if (entry.Is("Act_GiveObjList") && line.Value(1) is { } list)
                {
                    if (MissionIndex.Is(name, "Player"))
                        playerLists.Add((list, entry.At));
                    else
                    {
                        foreach (var unit in UnitsOf(name))
                            unit.Orders = (list, entry.At);
                    }
                }
                else if (entry.Is("Act_Destroy"))
                {
                    foreach (var unit in UnitsOf(name))
                        unit.Destroyed = true;
                }
            }
        }

        private void Spawn(Entry entry, string name)
        {
            var line = entry.Line;
            var kind = KindOf(entry, name);
            var members = Members(name);
            var record = ItemOf(name);
            var unit = new Unit(name, kind, members, entry)
            {
                System = kind is StartObjectKind.Ship or StartObjectKind.Formation
                    ? Known(Text(record, "system")) ?? entry.System
                    : entry.System,
                Point = entry.Is("Act_SpawnShipRel") ? null : Numbers(line, "position") ?? RecordPoint(record),
                Turn = Turn(line),
                Orders = entry.Is("Act_SpawnShip") && line.Value(1) is { } own && !MissionIndex.Is(own, "no_ol")
                    ? (own, -1)
                    : InitialOrders(members),
            };
            units.Add(unit);
            spawned[entry] = unit;
            foreach (var member in members.Append(name))
                byMember[member] = unit;
            madeMembers.UnionWith(members.Append(name));
        }

        private void Move(Entry entry, string name)
        {
            var targets = UnitsOf(name).ToList();
            if (targets.Count == 0)
            {
                // The fork makes a mission ship that does not exist at the point of the line.
                var unit = new Unit(name, StartObjectKind.Ship, Members(name), entry) { Orders = InitialOrders(Members(name)) };
                units.Add(unit);
                spawned[entry] = unit;
                foreach (var member in unit.Members.Append(name))
                    byMember[member] = unit;
                madeMembers.UnionWith(unit.Members.Append(name));
                targets.Add(unit);
            }
            foreach (var unit in targets)
            {
                unit.System = entry.System;
                unit.Point = Numbers(entry.Line, "position");
                unit.Turn = Turn(entry.Line) ?? unit.Turn;
                unit.JumpedFrom = null;
                unit.Placing = entry;
            }
        }

        /// <summary>A jump of the player: each object in the old system whose jumper flag is on goes with the
        /// player.</summary>
        private void Jump(string from, string to)
        {
            foreach (var unit in units.Where(u => !u.Destroyed && u.System != null && MissionIndex.Is(u.System, from) &&
                                                  IsJumper(u)))
            {
                unit.System = to;
                unit.Point = null;
                unit.JumpedFrom = from;
            }
        }

        private bool IsJumper(Unit unit) =>
            unit.Kind is StartObjectKind.Ship or StartObjectKind.Formation &&
            unit.Members.Append(unit.Name).Any(m => jumpers.TryGetValue(m, out var on) ? on : IsTrue(Text(ItemOf(m), "jumper")));

        /// <summary>
        /// Rule 7: the companions. A ship or a formation of the start system that the path does not make hostile is a
        /// companion when one sign is true: its orders keep it with the player or with another companion, the formation
        /// of the player names it, it jumped with the player, or the player must stay near it.
        /// </summary>
        private List<(Unit Unit, CompanionSigns Signs)> Companions()
        {
            var candidates = units.Where(u => !u.Destroyed && u.Kind is StartObjectKind.Ship or StartObjectKind.Formation &&
                                              InStart(u.System) && !u.Members.Append(u.Name).Any(facts.Hostile.Contains))
                .ToList();
            var signs = new Dictionary<Unit, CompanionSigns>();
            var nags = Nags();
            foreach (var unit in candidates)
            {
                var sign = CompanionSigns.None;
                if (OrderLines(unit).Any(l => FollowsPlayer(l)))
                    sign |= CompanionSigns.Orders;
                if (InPlayerFormation(unit))
                    sign |= CompanionSigns.PlayerFormation;
                if (unit is { JumpedFrom: not null, Point: null })
                    sign |= CompanionSigns.Jump;
                if (nags.Overlaps(unit.Members.Append(unit.Name)))
                    sign |= CompanionSigns.Escort;
                if (sign != CompanionSigns.None)
                    signs[unit] = sign;
            }
            // A ship that follows a companion travels with the player too.
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var unit in candidates.Where(u => !signs.ContainsKey(u)))
                {
                    if (!OrderLines(unit).Any(l => FollowTarget(l) is { } target &&
                                                   UnitsOf(target).Any(signs.ContainsKey)))
                        continue;
                    signs[unit] = CompanionSigns.Orders;
                    changed = true;
                }
            }
            return units.Where(signs.ContainsKey).OrderBy(u => u.Placing.At).Select(u => (u, signs[u])).ToList();
        }

        /// <summary>The objects that a nag keeps the player near (<c>Act_NagDistLeaving</c> without a later
        /// <c>Act_NagOff</c>), and the objects whose tether a waiting trigger watches.</summary>
        private HashSet<string> Nags()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = entries.Where(e => !e.Removed).ToList();
            foreach (var nag in kept.Where(e => e.Is("Act_NagDistLeaving")))
            {
                if (nag.Line.Value(0) is not { } id || nag.Line.Value(2) is not { } target ||
                    kept.Any(e => e.At > nag.At && e.Is("Act_NagOff") && MissionIndex.Is(e.Line.Value(0), id)))
                    continue;
                result.UnionWith(Members(target).Append(target.Trim()));
            }
            foreach (var condition in replay.ActiveSet.Select(t => t.Condition).OfType<MissionLine>()
                         .Where(c => MissionIndex.Is(c.Key, "Cnd_TetherBroke")))
            {
                foreach (var name in new[] { condition.Value(0), condition.Value(1) }.OfType<string>())
                    result.UnionWith(Members(name).Append(name));
            }
            return result;
        }

        /// <summary>True when a FollowPlayer list of the player names a member, after the last spawn line and the last
        /// own orders of the object.</summary>
        private bool InPlayerFormation(Unit unit)
        {
            var after = Math.Max(unit.Spawn.At, unit.Orders?.At ?? -1);
            return playerLists.Any(p => p.At > after && OrderLinesOf(p.List).Any(l =>
                MissionIndex.Is(l.Key, "FollowPlayer") &&
                Enumerable.Range(1, Math.Max(0, l.Count - 1)).Any(i => l.Value(i) is { } ship && unit.Members.Contains(ship))));
        }

        /// <summary>True for an order that keeps a ship with the player: Follow, Trail or Dock with the player, or
        /// FollowPlayer.</summary>
        private static bool FollowsPlayer(MissionLine order) =>
            MissionIndex.Is(order.Key, "FollowPlayer") || MissionIndex.Is(FollowTarget(order), "Player");

        /// <summary>The target of a Follow, a Trail or a Dock order, or null.</summary>
        private static string? FollowTarget(MissionLine order) =>
            MissionIndex.Is(order.Key, "Follow") || MissionIndex.Is(order.Key, "Trail") || MissionIndex.Is(order.Key, "Dock")
                ? order.Value(0)
                : null;

        private IEnumerable<MissionLine> OrderLines(Unit unit) =>
            unit.Orders is { } orders ? OrderLinesOf(orders.List) : [];

        private IEnumerable<MissionLine> OrderLinesOf(string list) =>
            index.Item(MissionSectionKind.OrderList, list) is { } section
                ? index.LinesOf(section).Where(l => l.Name != null)
                : [];

        /// <summary>The <c>init_objectives</c> of the first member that has one.</summary>
        private (string List, int At)? InitialOrders(IEnumerable<string> members) =>
            members.Select(m => Text(index.Item(MissionSectionKind.Ship, m), "init_objectives"))
                .FirstOrDefault(l => l != null && !MissionIndex.Is(l, "no_ol")) is { } list
                ? (list, -1)
                : null;

        /// <summary>
        /// Rules 5, 6 and 10: removes the spawn lines of the left-out objects and each line that names an object outside
        /// the start system, a fuse on an object that no line makes, and a move of a member of a companion formation.
        /// </summary>
        private void Remove(HashSet<Unit> leftOut, List<LeftOutObject> rows)
        {
            var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Where(e => !e.Removed))
            {
                var line = entry.Line;
                if (entry.Is("Act_LightFuse") && line.Value(0) is { } target &&
                    (Outside(target) || !alive.Contains(target) && !InStartFiles(target)))
                {
                    entry.Removed = true;
                    rows.Add(new LeftOutObject(target, StartObjectKind.Fuse, SystemOf(target), entry.Trigger.Nickname)
                    {
                        Fuse = line.Value(1),
                        TargetKind = ItemKindOf(target),
                    });
                    continue;
                }
                if (spawned.TryGetValue(entry, out var unit) && leftOut.Contains(unit) || NamesOutside(line))
                {
                    entry.Removed = true;
                    continue;
                }
                if (spawned.TryGetValue(entry, out var made))
                    alive.UnionWith(made.Members.Append(made.Name));
            }
        }

        /// <summary>True when an object of a system file of the start system has the name, or when the world does not
        /// know the objects of the systems and the script does not have the name.</summary>
        private bool InStartFiles(string name) =>
            !index.IsScriptObject(name) &&
            (index.World.ObjectOf(name) is { } item ? MissionIndex.Is(item.System, anchor.System)
                : !index.World.Knows(MissionValueKind.SystemObject));

        /// <summary>The system of an object at the chosen trigger, or null.</summary>
        private string? SystemOf(string name) =>
            UnitsOf(name).Select(u => u.System).FirstOrDefault(s => s != null) ??
            (index.IsScriptObject(name) ? null : index.World.ObjectOf(name)?.System);

        /// <summary>True when a line names an object outside the start system: an object that the setup leaves out, a
        /// label whose made members are all left out, or an object of a system file of another system.</summary>
        private bool NamesOutside(MissionLine line) => Names(line).Any(Outside);

        private bool Outside(string name)
        {
            name = name.Trim();
            if (MissionIndex.Is(name, "Player"))
                return false;
            if (IsLabel(name))
            {
                var made = Members(name).Where(madeMembers.Contains).ToList();
                return made.Count > 0 && made.All(leftOutMembers.Contains);
            }
            if (leftOutMembers.Contains(name) || Members(name).Any(leftOutMembers.Contains))
                return true;
            return !index.IsScriptObject(name) && index.World.ObjectOf(name) is { } item &&
                   !MissionIndex.Is(item.System, anchor.System);
        }

        private bool IsLabel(string name) =>
            index.Labels.ContainsKey(name) && index.Item(MissionSectionKind.Ship, name) == null &&
            index.Item(MissionSectionKind.Formation, name) == null;

        /// <summary>The names of the objects in a line, without the player. The target of <c>Act_NagDistTowards</c> with
        /// the form OBJ is an object too, but its spec cannot tell it.</summary>
        private static IEnumerable<string> Names(MissionLine line)
        {
            var names = line.References.Where(r => ObjectKinds.Contains(r.Kind)).Select(r => r.Name);
            if (MissionIndex.Is(line.Key, "Act_NagDistTowards") && MissionIndex.Is(line.Value(0), "OBJ") &&
                line.Value(3) is { } target)
                names = names.Append(target);
            return names.Where(n => !MissionIndex.Is(n, "Player"));
        }

        /// <summary>
        /// Rule 9: where each companion starts. A companion near the anchor keeps its point. Else a jump-here trigger of
        /// the script gives its offset from the player, else the offset of its Follow order, else a slot to the side.
        /// Two companions never get the same offset. Rule 10: a ship gets <c>Act_RelocateShip</c> after its last line
        /// that places it. A formation gets the point and the heading in its last <c>Act_SpawnFormation</c>, and a later
        /// move of a member goes.
        /// </summary>
        private List<StartCompanion> Place(List<(Unit Unit, CompanionSigns Signs)> companions, List<string> warnings)
        {
            var result = new List<StartCompanion>();
            var used = new List<Vector3>();
            var slot = 0;
            if (anchor.Point == null && companions.Count > 0 && anchor.Base != null)
                warnings.Add(NoDockWarning(anchor.Base));
            foreach (var (unit, signs) in companions)
            {
                var row = new StartCompanion(unit.Name, unit.Kind, signs, unit.Spawn.Trigger.Nickname, CompanionPoint.Kept)
                {
                    JumpedFrom = unit.JumpedFrom,
                    Position = unit.Point,
                };
                if (anchor.Point is not { } origin)
                {
                    result.Add(row);
                    continue;
                }
                if (unit.Point is { } point && Vector3.Distance(point, origin) <= NearDistance)
                {
                    used.Add(point - origin);
                    result.Add(row with { Point = CompanionPoint.Near });
                    continue;
                }
                var rule = CompanionPoint.Slot;
                Vector3? offset = null;
                var turn = anchor.Turn;
                string? trigger = null;
                if (JumpHere(unit) is { } jump)
                    (rule, offset, turn, trigger) = (CompanionPoint.JumpHere, jump.Offset, jump.Turn ?? anchor.Turn,
                        jump.Trigger.Nickname);
                else if (FollowOffset(unit) is { } follow)
                    (rule, offset) = (CompanionPoint.Follow, Vector3.Transform(follow, anchor.Turn));
                if (offset is not { } chosen || used.Any(u => Vector3.Distance(u, chosen) < 1))
                {
                    do
                    {
                        var side = (slot % 2 == 0 ? -1 : 1) * SlotStep * (slot / 2 + 1);
                        chosen = Vector3.Transform(new Vector3(side, 0, 0), anchor.Turn);
                        slot++;
                    } while (used.Any(u => Vector3.Distance(u, chosen) < 1));
                    (rule, turn, trigger) = (CompanionPoint.Slot, anchor.Turn, null);
                }
                used.Add(chosen);
                var at = origin + chosen;
                Write(unit, at, turn);
                result.Add(row with { Point = rule, PointTrigger = trigger, Position = at });
            }
            return result;
        }

        /// <summary>The offset of a companion from the player point of the newest jump-here trigger that places it, at
        /// most <see cref="JumpHereDistance"/> units, with the heading of its line.</summary>
        private (Vector3 Offset, Quaternion? Turn, MissionTrigger Trigger)? JumpHere(Unit unit)
        {
            foreach (var (trigger, player) in facts.JumpHere)
            {
                foreach (var line in trigger.Actions.Reverse())
                {
                    var isSpawn = MissionIndex.Is(line.Key, "Act_SpawnShip") || MissionIndex.Is(line.Key, "Act_SpawnFormation");
                    if (!isSpawn && !MissionIndex.Is(line.Key, "Act_RelocateShip") || line.Value(0) is not { } name ||
                        !(MissionIndex.Is(name, unit.Name) || unit.Kind == StartObjectKind.Ship && unit.Members.Contains(name)) ||
                        Numbers(line, "position") is not { } point)
                        continue;
                    if (Vector3.Distance(point, player) <= JumpHereDistance)
                        return (point - player, Turn(line), trigger);
                }
            }
            return null;
        }

        /// <summary>The offset of a <c>Follow = Player</c> order of the last own orders, when it is not zero.</summary>
        private Vector3? FollowOffset(Unit unit)
        {
            foreach (var order in OrderLines(unit).Where(l => MissionIndex.Is(l.Key, "Follow") &&
                                                            MissionIndex.Is(l.Value(0), "Player")))
            {
                if (Numbers(order, "offset") is { } offset && offset != Vector3.Zero)
                    return offset;
            }
            return null;
        }

        /// <summary>Writes the new point of a companion into the setup lines.</summary>
        private void Write(Unit unit, Vector3 point, Quaternion turn)
        {
            var place = new[] { Number(point.X), Number(point.Y), Number(point.Z) }
                .Concat(new[] { turn.W, turn.X, turn.Y, turn.Z }.Select(Heading)).ToList();
            if (unit.Kind == StartObjectKind.Formation && entries.LastOrDefault(e =>
                    !e.Removed && e.Is("Act_SpawnFormation") && MissionIndex.Is(e.Line.Value(0), unit.Name)) is { } spawn)
            {
                var line = new List<string> { unit.Name };
                line.AddRange(place);
                spawn.Values = line;
                foreach (var move in entries.Where(e => e.At > spawn.At && !e.Removed && e.IsAny(MoveKeys) &&
                                                        e.Line.Value(0) is { } m && unit.Members.Contains(m)))
                    move.Removed = true;
                return;
            }
            var values = new List<string> { unit.Name };
            values.AddRange(place);
            unit.Placing.After.Add(("Act_RelocateShip", values));
        }

        /// <summary>The warnings of the triggers that wait for objects that the setup leaves out.</summary>
        private IEnumerable<string> Waiting()
        {
            foreach (var trigger in replay.ActiveSet)
            {
                if (trigger.Condition is not { } condition)
                    continue;
                var names = Names(condition).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count > 0 && names.All(Outside))
                {
                    yield return WaitingWarning(trigger.Nickname, names[0], SystemOf(names[0]) ??
                        Members(names[0]).Select(SystemOf).FirstOrDefault(s => s != null) ?? "another system");
                    continue;
                }
                foreach (var label in names.Where(IsLabel))
                {
                    var count = Members(label).Count(leftOutMembers.Contains);
                    if (count > 0 && !Outside(label))
                        yield return LabelWarning(trigger.Nickname, label, count);
                }
            }
        }

        /// <summary>True for a system that is the start system, or for an unknown system.</summary>
        private bool InStart(string? system) => system == null || MissionIndex.Is(system, anchor.System);

        private IEnumerable<Unit> UnitsOf(string name) =>
            Members(name).Append(name.Trim()).Select(m => byMember.GetValueOrDefault(m)).OfType<Unit>()
                .Where(u => !u.Destroyed).Distinct();

        private IReadOnlySet<string> Members(string name) => StartSetup.Members(index, name);

        private StartObjectKind KindOf(Entry entry, string name) => entry switch
        {
            _ when entry.Is("Act_SpawnSolar") => StartObjectKind.Solar,
            _ when entry.Is("Act_SpawnLoot") => StartObjectKind.Loot,
            _ when index.Item(MissionSectionKind.Ship, name) != null && !entry.Is("Act_SpawnFormation") =>
                StartObjectKind.Ship,
            _ when index.Item(MissionSectionKind.Formation, name) != null => StartObjectKind.Formation,
            _ => StartObjectKind.Ship,
        };

        /// <summary>The kind of an object of the script, or null for another name.</summary>
        private StartObjectKind? ItemKindOf(string name) =>
            index.Item(MissionSectionKind.Ship, name) != null ? StartObjectKind.Ship
            : index.Item(MissionSectionKind.Formation, name) != null ? StartObjectKind.Formation
            : index.Item(MissionSectionKind.Solar, name) != null ? StartObjectKind.Solar
            : index.Item(MissionSectionKind.Loot, name) != null ? StartObjectKind.Loot
            : null;

        private MissionSection? ItemOf(string name) =>
            index.Item(MissionSectionKind.Ship, name) ?? index.Item(MissionSectionKind.Formation, name) ??
            index.Item(MissionSectionKind.Solar, name) ?? index.Item(MissionSectionKind.Loot, name);

        /// <summary>A system name, or null for an empty name or a name that the world does not have.</summary>
        private string? Known(string? name) =>
            name?.Trim() is { Length: > 0 } text &&
            (!index.World.Knows(MissionValueKind.System) || index.World.Has(MissionValueKind.System, text))
                ? text
                : null;
    }

    private static bool IsTrue(string? text) => text != null && (MissionIndex.Is(text, "true") || text.Trim() == "1");

    /// <summary>The first value of a key of a section, or null.</summary>
    private static string? Text(MissionSection? section, string key) =>
        section?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, key) && e.Count > 0) is { } entry
            ? MissionIndex.Text(entry[0]) is { Length: > 0 } text ? text : null
            : null;

    /// <summary>The <c>position</c> of a record, or null.</summary>
    private static Vector3? RecordPoint(MissionSection? section)
    {
        if (section?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "position") && e.Count >= 3) is not { } entry)
            return null;
        var values = Enumerable.Range(0, 3).Select(i => Parse(MissionIndex.Text(entry[i]))).ToList();
        return values.All(v => v != null) ? new Vector3(values[0]!.Value, values[1]!.Value, values[2]!.Value) : null;
    }

    /// <summary>The three numbers of a value spec of a line, or null.</summary>
    private static Vector3? Numbers(MissionLine line, string spec)
    {
        if (line.Name?.Value(spec) is not { } at)
            return null;
        var values = Enumerable.Range(at.Index, 3).Select(i => Parse(line.Value(i))).ToList();
        return values.All(v => v != null) ? new Vector3(values[0]!.Value, values[1]!.Value, values[2]!.Value) : null;
    }

    /// <summary>The heading of the <c>rotation</c> of a line (w, x, y, z), or null.</summary>
    private static Quaternion? Turn(MissionLine line)
    {
        if (line.Name?.Value("rotation") is not { } at)
            return null;
        var values = Enumerable.Range(at.Index, 4).Select(i => Parse(line.Value(i))).ToList();
        if (values.Any(v => v == null))
            return null;
        var turn = new Quaternion(values[1]!.Value, values[2]!.Value, values[3]!.Value, values[0]!.Value);
        return turn.Length() > 1e-3f ? Quaternion.Normalize(turn) : null;
    }

    private static float? Parse(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value
            : null;

    /// <summary>A coordinate with at most two decimals.</summary>
    private static string Number(float value) => Clean(Math.Round(value, 2)).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A part of a heading with at most four decimals.</summary>
    private static string Heading(float value) => Clean(Math.Round(value, 4)).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The value without a negative zero.</summary>
    private static double Clean(double value) => value == 0 ? 0 : value;
}
