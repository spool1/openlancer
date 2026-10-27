using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Data.Missions.Run;

/// <summary>
/// The rules of the suggested start place (section 5.3, step 7 of the M6c design, with the fix of 2026-10-27 and its
/// reviews). The suggestion is where the player is at the chosen trigger in the retail game. A walk reads the place
/// facts of the timeline from the newest to the oldest. The newest fact gives the kind of the start. A base fact gives a
/// base start. A launch, an arrival or a space fact gives a space start. A chosen trigger that waits at a base for the
/// launch starts docked. A chosen trigger that waits for a jump starts before the jump. The facts of the segment give
/// the system and the point. The segment is the part of the walk in one place in space, back to the base of the launch
/// or to the arrival. The center of a system is never a start point, because most suns are there.
/// </summary>
internal static class StartPlaces
{
    /// <summary>The offset of a start from a spawn position or from the point of a Cnd_DistVec line, in units.</summary>
    public const float Offset = 1000;

    private static readonly string[] PlaceActions = ["Act_MovePlayer", "Act_SetInitialPlayerPos"];

    /// <summary>The spawns that give a point near an object of the mission. A move of a ship that exists is no spawn,
    /// because scripts also park ships with it.</summary>
    private static readonly string[] SpawnActions =
        ["Act_SpawnShip", "Act_SpawnFormation", "Act_SpawnSolar", "Act_SpawnLoot"];

    private static readonly string[] BaseConditions = ["Cnd_BaseEnter", "Cnd_LocEnter", "Cnd_CharSelect"];

    private static readonly string[] LaunchConditions =
        ["Cnd_SpaceEnter", "Cnd_PlayerLaunch", "Cnd_LaunchComplete", "Cnd_BaseExit"];

    private static readonly string[] ArrivalConditions = ["Cnd_SystemEnter", "Cnd_JumpInComplete", "Cnd_NPCSystemEnter"];

    /// <summary>The conditions that retail tests only while the player is in space.</summary>
    private static readonly string[] SpaceConditions =
        ["Cnd_DistVec", "Cnd_DistShip", "Cnd_DistCircle", "Cnd_InZone", "Cnd_CmpToPlane", "Cnd_TLEntered", "Cnd_TLExited"];

    /// <summary>The suggested place of a start, and the warnings of the Play start page about it.</summary>
    public sealed record Suggestion(StartPlace Place, IReadOnlyList<string> Warnings);

    /// <summary>The suggested place of the start of a replay.</summary>
    public static Suggestion Suggest(MissionIndex index, StartReplay replay) => new Walk(index, replay).Suggest();

    /// <summary>The warning of a trigger that waits for the end of a launch that the path already made. A start in
    /// space never ends a launch, so the trigger does not run.</summary>
    public static string LaunchWatcherWarning(string trigger) =>
        $"Trigger '{trigger}' waits for the end of the launch. The play starts in space after the launch, so this " +
        "trigger does not run. Pick a base as the start place to run it.";

    /// <summary>The warning of a trigger that waits for the player to enter space in another system than the system of
    /// the start.</summary>
    public static string OtherSystemWarning(string trigger, string system, string start) =>
        $"Trigger '{trigger}' waits for the player to enter space in {system}. The play starts in {start}, so the linked " +
        "game does not run this trigger. OpenLancer does not compare the system and runs it.";

    /// <summary>True for <c>Act_RelocateShip</c> of the player. Retail scripts move the player with it, for example after
    /// a scene in space. It is a place line, so the setup triggers do not copy it.</summary>
    internal static bool IsPlayerMove(MissionLine line) =>
        MissionIndex.Is(line.Key, "Act_RelocateShip") && MissionIndex.Is(line.Value(0), "Player");

    private static bool IsPlaceLine(MissionLine line) => IsAny(line.Key, PlaceActions) || IsPlayerMove(line);

    /// <summary>The point and the heading of a place line. <c>Act_SetInitialPlayerPos</c> and <c>Act_RelocateShip</c>
    /// hold the heading as w, x, y, z after the point.</summary>
    private static (Vector3 Point, Quaternion Turn)? PointOf(MissionLine line)
    {
        var from = IsPlayerMove(line) ? 1 : 0;
        if (Numbers(line, from, 3) is not { } p)
            return null;
        var turn = !MissionIndex.Is(line.Key, "Act_MovePlayer") && Numbers(line, from + 3, 4) is { } q &&
                   new Quaternion(q[1], q[2], q[3], q[0]) is var rotation && rotation.Length() > 1e-3f
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;
        return (new Vector3(p[0], p[1], p[2]), turn);
    }

    /// <summary>The point of a Cnd_DistVec line that waits for the player inside its sphere, with the offset, or
    /// null.</summary>
    private static Vector3? NearPoint(MissionLine condition) =>
        MissionIndex.Is(condition.Key, "Cnd_DistVec") && MissionIndex.Is(condition.ValueOf("side"), "inside") &&
        condition.Name?.Value("point") is { } spec && Numbers(condition, spec.Index, 3) is { } p
            ? new Vector3(p[0], p[1], p[2] + Offset)
            : null;

    /// <summary>True when the chosen trigger waits for the player at another place: an arrival in a system, or a place
    /// in space. Its own lines then tell where the player will be, not where the player is.</summary>
    private static bool WaitsToArrive(MissionLine? condition) =>
        IsArrival(condition) || condition != null && IsAny(condition.Key, SpaceConditions) && NamesPlayer(condition);

    /// <summary>True for a condition of an arrival in a system after a jump. Its system and its system hint name the
    /// next system.</summary>
    private static bool IsArrival(MissionLine? condition) =>
        condition != null && IsAny(condition.Key, ArrivalConditions);

    private static bool NamesPlayer(MissionLine line) =>
        Enumerable.Range(0, line.Count).Any(i => MissionIndex.Is(line.Value(i), "Player"));

    private static bool IsAny(string key, string[] keys) => keys.Any(k => MissionIndex.Is(key, k));

    /// <summary>The line in words: "{key} of {trigger}".</summary>
    private static string Of(MissionLine line, MissionTrigger trigger) => $"{line.Key.Trim()} of {trigger.Nickname}";

    /// <summary>The numbers of a line from a value index, or null when one of them is not a finite number.</summary>
    private static float[]? Numbers(MissionLine line, int from, int count)
    {
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (Parse(line.Value(from + i)) is not { } value)
                return null;
            result[i] = value;
        }
        return result;
    }

    private static float? Parse(string? text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value
            : null;

    /// <summary>The timeline: the triggers in time order up to the chosen trigger. A replay from a restore trigger
    /// adds the story path to that restore trigger first. A trigger that no root reaches has only itself. The mission
    /// start has the triggers that ran at once.</summary>
    private static List<MissionTrigger> Timeline(MissionIndex index, StartReplay replay)
    {
        if (replay.Chosen is not { } chosen)
            return replay.Effects.Select(e => e.Trigger).Distinct().ToList();
        if (replay.Path.Count == 0)
            return [chosen];
        var timeline = new List<MissionTrigger>();
        if (replay.Path[0] is { StartsActive: false } root)
            timeline.AddRange(StartPlan.StoryPath(index, root).SkipLast(1));
        timeline.AddRange(replay.Path);
        return timeline;
    }

    private enum FactKind
    {
        /// <summary>The player is docked at a base. A fact without a base docks at a base that no line names.</summary>
        Base,

        /// <summary>The player is in space after a launch.</summary>
        Launch,

        /// <summary>The player is in space in a system after a jump.</summary>
        Arrival,

        /// <summary>The player is in space, for example near a point or after a place line.</summary>
        Space,
    }

    /// <summary>A fact of the walk about where the player is.</summary>
    /// <param name="At">The index in the timeline of the trigger of the fact.</param>
    /// <param name="Source">The line of the fact in words, for example "Cnd_SpaceEnter of t_launch".</param>
    private sealed record Fact(FactKind Kind, MissionTrigger Trigger, int At, string Source)
    {
        public string? System { get; init; }

        /// <summary>The line that gives the system when another trigger gives it, in words, else null.</summary>
        public string? SystemFrom { get; init; }

        public string? Base { get; init; }

        public Vector3? Point { get; init; }

        public Quaternion Turn { get; init; } = Quaternion.Identity;

        /// <summary>True for the place line of a jump-here trigger. Such a fact gives a point, but it does not decide the
        /// kind of the start.</summary>
        public bool Jump { get; init; }
    }

    /// <summary>The walk over the timeline of one start.</summary>
    private sealed class Walk
    {
        private readonly MissionIndex index;
        private readonly StartReplay replay;
        private readonly MissionTrigger? chosen;
        private readonly List<MissionTrigger> timeline;
        private readonly HashSet<MissionTrigger> onTimeline;
        private readonly HashSet<MissionTrigger> checkpoints;
        private readonly ILookup<MissionTrigger, MissionTrigger> starts;
        private Dictionary<MissionTrigger, int>? depths;

        public Walk(MissionIndex index, StartReplay replay)
        {
            this.index = index;
            this.replay = replay;
            chosen = replay.Chosen;
            timeline = Timeline(index, replay);
            onTimeline = [.. timeline];
            checkpoints = [.. DryRun.Checkpoints(index)];
            starts = index.Links.Where(l => l.Kind == MissionLinkKind.Start && l.Target is { IsDuplicate: false })
                .ToLookup(l => l.Source, l => l.Target!);
        }

        /// <summary>The number of earlier triggers of the timeline. The chosen trigger is the last trigger. The mission
        /// start has no chosen trigger.</summary>
        private int Earlier => chosen != null ? timeline.Count - 1 : timeline.Count;

        public Suggestion Suggest()
        {
            var facts = Facts().ToList();
            var place = PlaceOf(facts);
            if (place.Kind == StartPlaceKind.Base)
                return new Suggestion(place, [.. OtherSystem(place)]);
            // A start in space never ends a launch, also for a chosen trigger that waits for it without a base to dock at.
            IEnumerable<string> own = chosen != null && WaitsForLaunchEnd(chosen.Condition)
                ? [LaunchWatcherWarning(chosen.Nickname)]
                : [];
            return new Suggestion(place, [.. own, .. LaunchWatchers(facts), .. OtherSystem(place)]);
        }

        /// <summary>The warning of a chosen trigger whose Cnd_SpaceEnter names another system than the system of the
        /// start. Retail compares the system, and OpenLancer does not.</summary>
        private IEnumerable<string> OtherSystem(StartPlace place)
        {
            var at = place.System ?? (place.Base is { } dock ? BaseSystemOf(dock) : null);
            if (OwnSystem() is { } waits && at != null && !MissionIndex.Is(waits, at))
                yield return OtherSystemWarning(chosen!.Nickname, waits, at);
        }

        /// <summary>The place facts of the timeline, from the newest to the oldest (rule 1).</summary>
        private IEnumerable<Fact> Facts()
        {
            if (chosen != null)
            {
                foreach (var fact in OwnFacts(timeline.Count - 1))
                    yield return fact;
            }
            for (var k = Earlier - 1; k >= 0; k--)
            {
                var trigger = timeline[k];
                foreach (var action in trigger.Actions.Reverse())
                {
                    if (PlaceFact(trigger, action, k) is { } moved)
                        yield return moved;
                    else if (MissionIndex.Is(action.Key, "Act_ForceLand"))
                        yield return new Fact(FactKind.Base, trigger, k, Of(action, trigger))
                        {
                            Base = Named(action, "base", MissionValueKind.Base),
                        };
                }
                if (k + 1 < timeline.Count)
                {
                    foreach (var jump in JumpsTo(timeline[k + 1], k + 1))
                        yield return jump;
                }
                if (PastCondition(trigger, k) is { } fact)
                    yield return fact;
            }
            if (timeline.Count > 0)
            {
                foreach (var jump in JumpsTo(timeline[0], 0))
                    yield return jump;
            }
        }

        /// <summary>
        /// The facts of the chosen trigger (rule 2):
        /// <list type="bullet">
        /// <item>its place line</item>
        /// <item>for a restore trigger, the place line of a Cnd_True trigger that it starts (both run at the load)</item>
        /// <item>the base of Cnd_BaseEnter, Cnd_LocEnter, Cnd_CharSelect or Cnd_BaseExit</item>
        /// </list>
        /// A trigger that waits for an arrival or for a place of the player moves the player only after that. So its
        /// place line gives no fact.
        /// </summary>
        private IEnumerable<Fact> OwnFacts(int at)
        {
            var trigger = chosen!;
            var condition = trigger.Condition;
            if (!WaitsToArrive(condition) && trigger.Actions.LastOrDefault(IsPlaceLine) is { } line &&
                PlaceFact(trigger, line, at) is { } own)
                yield return own;
            if (checkpoints.Contains(trigger))
            {
                foreach (var step in starts[trigger].Distinct().Where(t => t.ConditionIs("Cnd_True")))
                {
                    if (step.Actions.LastOrDefault(IsPlaceLine) is { } stepLine &&
                        PlaceFact(step, stepLine, at) is { } moved)
                        yield return moved with { Source = $"{Of(stepLine, step)}, which {trigger.Nickname} starts" };
                }
            }
            if (condition != null &&
                (IsAny(condition.Key, BaseConditions) || MissionIndex.Is(condition.Key, "Cnd_BaseExit")) &&
                Named(condition, "base", MissionValueKind.Base) is { } dock)
                yield return new Fact(FactKind.Base, trigger, at, Of(condition, trigger)) { Base = dock };
        }

        /// <summary>The fact of the condition of an earlier trigger of the timeline, or null.</summary>
        private Fact? PastCondition(MissionTrigger trigger, int at)
        {
            if (trigger.Condition is not { } condition)
                return null;
            var key = condition.Key;
            var fact = new Fact(FactKind.Space, trigger, at, Of(condition, trigger));
            if (IsAny(key, BaseConditions))
                return fact with { Kind = FactKind.Base, Base = Named(condition, "base", MissionValueKind.Base) };
            // The player leaves space at a dock. Cnd_SpaceExit and Cnd_InSpace = false name no base.
            if (MissionIndex.Is(key, "Cnd_InSpace"))
                return MissionIndex.Is(condition.Value(0), "false") || condition.Value(0) == "0"
                    ? fact with { Kind = FactKind.Base }
                    : fact;
            if (MissionIndex.Is(key, "Cnd_SpaceExit"))
                return fact with { Kind = FactKind.Base };
            if (IsAny(key, LaunchConditions))
                return MissionIndex.Is(key, "Cnd_LaunchComplete") && !NamesPlayer(condition)
                    ? null
                    : fact with { Kind = FactKind.Launch, System = ConditionSystem(condition) };
            if (IsAny(key, ArrivalConditions))
                return fact with { Kind = FactKind.Arrival, System = ConditionSystem(condition) };
            return IsAny(key, SpaceConditions) && NamesPlayer(condition)
                ? fact with { Point = NearPoint(condition) }
                : null;
        }

        /// <summary>The fact of a place line, or null for another line.</summary>
        /// <param name="target">The trigger of the timeline that a jump-here trigger starts, else null.</param>
        private Fact? PlaceFact(MissionTrigger trigger, MissionLine line, int at, MissionTrigger? target = null)
        {
            if (!IsPlaceLine(line) || PointOf(line) is not var (point, turn))
                return null;
            var source = target == null ? Of(line, trigger) : $"{Of(line, trigger)}, which starts {target.Nickname}";
            var (system, from) = PlaceSystem(trigger, target);
            return new Fact(FactKind.Space, trigger, at, source)
            {
                System = system,
                SystemFrom = from,
                Point = point,
                Turn = turn,
                Jump = target != null,
            };
        }

        /// <summary>
        /// The jump-here triggers of a trigger of the timeline: a trigger that is not on the timeline, has a place line
        /// and starts the trigger. It counts when it is a restore trigger or no trigger starts it, and when the trigger
        /// is its latest step. A restore trigger comes first.
        /// </summary>
        private IEnumerable<Fact> JumpsTo(MissionTrigger target, int at) =>
            index.StartsOf(target).Select(l => l.Source).Distinct()
                .Where(j => j != target && !j.IsDuplicate && !onTimeline.Contains(j) &&
                            (checkpoints.Contains(j) || index.NothingStarts(j)) && IsLatestStep(j, target))
                .OrderBy(j => checkpoints.Contains(j) ? 0 : 1)
                .Select(j => j.Actions.LastOrDefault(IsPlaceLine) is { } line ? PlaceFact(j, line, at, target) : null)
                .OfType<Fact>();

        /// <summary>True when no other trigger that a jump-here trigger starts has a longer story path than the target.
        /// So a debug jump to a later step does not count for a watcher that it also starts.</summary>
        private bool IsLatestStep(MissionTrigger jump, MissionTrigger target) =>
            starts[jump].All(t => Depth(t) <= Depth(target));

        /// <summary>The length of the story path of a trigger, or -1 when no start trigger reaches it.</summary>
        private int Depth(MissionTrigger trigger) => (depths ??= Depths()).GetValueOrDefault(trigger, -1);

        /// <summary>The length of the story path of each trigger that a start trigger reaches through Start and Load
        /// links.</summary>
        private Dictionary<MissionTrigger, int> Depths()
        {
            var next = index.Links
                .Where(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Load && l.Target is { IsDuplicate: false })
                .ToLookup(l => l.Source, l => l.Target!);
            var depth = new Dictionary<MissionTrigger, int>();
            var queue = new Queue<MissionTrigger>();
            foreach (var root in index.StartTriggers.Where(r => !r.IsDuplicate))
            {
                if (depth.TryAdd(root, 0))
                    queue.Enqueue(root);
            }
            while (queue.Count > 0)
            {
                var trigger = queue.Dequeue();
                foreach (var child in next[trigger])
                {
                    if (depth.TryAdd(child, depth[trigger] + 1))
                        queue.Enqueue(child);
                }
            }
            return depth;
        }

        /// <summary>
        /// The system of a place line (rule 5), and the line of another trigger that gives it. The order is:
        /// <list type="number">
        /// <item>the system of the condition of its trigger</item>
        /// <item>the Cnd_SpaceEnter system of a trigger that it starts (the second stage of a checkpoint)</item>
        /// <item>its system hint</item>
        /// <item>the system of the story at a step that it starts, the target first, then the deepest step</item>
        /// </list>
        /// Retail never runs the condition of a trigger that no trigger starts. So for such a trigger, the hint comes
        /// first.
        /// </summary>
        private (string? System, string? From) PlaceSystem(MissionTrigger trigger, MissionTrigger? target)
        {
            var next = starts[trigger].Distinct().OrderBy(t => t == target ? 0 : 1).ThenByDescending(Depth).ToList();
            (string? System, string? From) own = (ConditionSystem(trigger.Condition), null);
            if (own.System == null && next.FirstOrDefault(t => t.ConditionIs("Cnd_SpaceEnter") &&
                    Named(t.Condition!, "system", MissionValueKind.System) != null) is { } stage)
                own = (Named(stage.Condition!, "system", MissionValueKind.System), Of(stage.Condition!, stage));
            (string? System, string? From) hint = (Hint(trigger), null);
            var first = index.NothingStarts(trigger) ? hint.System != null ? hint : own : own.System != null ? own : hint;
            return first.System != null
                ? first
                : next.Select(StepSystem).FirstOrDefault(s => s.System != null);
        }

        /// <summary>
        /// The system of the story at a step that a place line starts, and its line. It is the newest system of the
        /// story path of the step, from the step back. Each trigger of the path gives the system of its condition, else
        /// its system hint. The condition can be Cnd_SpaceEnter, an arrival or a base condition. A step that waits for an
        /// arrival names the next system, so its own lines do not count.
        /// </summary>
        private (string? System, string? From) StepSystem(MissionTrigger step)
        {
            var path = StartPlan.StoryPath(index, step) is { Count: > 0 } story ? story : [step];
            for (var k = path.Count - 1; k >= 0; k--)
            {
                var trigger = path[k];
                if (trigger.Condition is not { } condition)
                    continue;
                if (trigger == step && IsArrival(condition))
                    continue;
                if (ConditionSystem(condition) is { } system)
                    return (system, Of(condition, trigger));
                if (BaseSystem(condition) is { } dock)
                    return (dock, $"the base of {Of(condition, trigger)}");
                if (Hint(trigger) is { } hint)
                    return (hint, $"the system hint of {trigger.Nickname}");
            }
            return (null, null);
        }

        /// <summary>The place of the facts (rules 3, 4, 6 and 7).</summary>
        private StartPlace PlaceOf(List<Fact> facts)
        {
            // A trigger that waits for the end of a launch runs only after a launch from a dock (rule 2).
            if (chosen != null && WaitsForLaunchEnd(chosen.Condition) &&
                facts.FirstOrDefault(f => f is { Kind: FactKind.Base, Base: not null }) is { } launch)
                return Docked(launch, $"{launch.Source}. Trigger {chosen.Nickname} waits for the end of a launch, so the " +
                                      "play starts docked.");
            // A chosen trigger that waits for an arrival needs the jump. An arrival fact in that system that is newer than
            // each other fact is the same jump, so the start is before it (rule 2).
            if (ArrivalSystem() is { } next && facts.FindIndex(f => !f.Jump) is var newest and >= 0 &&
                facts[newest] is { Kind: FactKind.Arrival } arrival && MissionIndex.Is(arrival.System, next))
                facts = facts.Skip(newest + 1).ToList();
            var decide = facts.FirstOrDefault(f => !f.Jump) ?? facts.FirstOrDefault();
            if (decide == null)
                return NoFact();
            if (decide.Kind == FactKind.Base)
                return decide.Base != null
                    ? Docked(decide, $"{decide.Source}.")
                    : StartPlace.Camera($"{decide.Source}. The player is docked at a base that no line names.") with
                    {
                        Trigger = decide.Trigger.Nickname,
                    };

            // The segment ends at the newest base fact, the base of the launch. It also ends at the newest arrival.
            var segment = new List<Fact>();
            Fact? launchBase = null;
            var from = 0;
            foreach (var fact in facts)
            {
                if (fact.Kind == FactKind.Base)
                {
                    launchBase = fact.Base != null ? fact : null;
                    from = fact.At;
                    break;
                }
                segment.Add(fact);
                if (fact.Kind == FactKind.Arrival)
                {
                    from = fact.At;
                    break;
                }
            }

            var system = decide.System;
            var systemFrom = decide.SystemFrom;
            if (system == null &&
                segment.SkipWhile(f => f != decide).Skip(1).FirstOrDefault(f => f is { Jump: false, System: not null }) is
                    { } older)
                (system, systemFrom) = (older.System, older.Source);
            if (system == null && launchBase != null && BaseSystemOf(launchBase.Base!) is { } baseSystem)
                (system, systemFrom) = (baseSystem, $"the base of {launchBase.Source}");
            if (system == null && OwnSystem() is { } waits)
                (system, systemFrom) = (waits, Of(chosen!.Condition!, chosen));
            if (system == null && HintOf(decide, from) is { } hinted)
                (system, systemFrom) = (Hint(hinted), $"the system hint of {hinted.Nickname}");
            var systemText = systemFrom != null ? $" The system comes from {systemFrom}." : "";
            if (system == null)
                return launchBase != null
                    ? Docked(launchBase, $"{launchBase.Source}. No line of the path names the system of {decide.Source}.")
                    : StartPlace.Camera($"No line of the path names the system of {decide.Source}.") with
                    {
                        Trigger = decide.Trigger.Nickname,
                    };

            var point = decide.Point;
            var turn = decide.Turn;
            var pointText = "";
            if (point == null && segment.FirstOrDefault(f => f.Point != null && InSystem(f, system)) is { } near)
                (point, turn, pointText) = (near.Point, near.Turn, $" The point comes from {near.Source}.");
            if (point == null && SpawnIn(system, from) is var (spawn, ship))
                (point, pointText) = (spawn + new Vector3(0, 0, Offset),
                    $" The point is {Offset.ToString("N0", CultureInfo.InvariantCulture)} units from the spawn of {ship}.");
            if (point == null)
                return launchBase != null
                    ? Docked(launchBase, $"{launchBase.Source}. No line of the path gives a point in {system} after " +
                                         $"{decide.Source}.")
                    : StartPlace.Camera($"{decide.Source}.{systemText} No line of the path gives a point in {system}.",
                        system) with { Trigger = decide.Trigger.Nickname };
            return new StartPlace(StartPlaceKind.Space, system, null, point.Value, turn,
                $"{decide.Source}.{systemText}{pointText}")
            {
                Trigger = decide.Trigger.Nickname,
            };
        }

        /// <summary>The place when the walk finds no fact: the Scene camera. When the chosen trigger names its system, in
        /// Cnd_SpaceEnter or in its system hint, the camera must be in that system.</summary>
        private StartPlace NoFact()
        {
            const string none = "No line of the path gives a place.";
            if (OwnSystem() is { } waits)
                return StartPlace.Camera($"{none} The system comes from {Of(chosen!.Condition!, chosen)}.", waits);
            if (chosen != null && !IsArrival(chosen.Condition) && Hint(chosen) is { } hint)
                return StartPlace.Camera($"{none} The system comes from the system hint of {chosen.Nickname}.", hint);
            return StartPlace.Camera(none);
        }

        private static StartPlace Docked(Fact fact, string source) =>
            new(StartPlaceKind.Base, null, fact.Base, Vector3.Zero, Quaternion.Identity, source)
            {
                Trigger = fact.Trigger.Nickname,
            };

        /// <summary>True when the point of a fact can be in the system. The fact, else the system hint of its trigger,
        /// names that system or no system.</summary>
        private bool InSystem(Fact fact, string system) =>
            (fact.System ?? Hint(fact.Trigger)) is not { } own || MissionIndex.Is(own, system);

        /// <summary>The trigger of the nearest system hint (rule 4). The trigger of the fact comes first. Then each
        /// trigger from the chosen trigger back to the start of the segment comes. A chosen trigger that waits for an
        /// arrival has the hint of the next system, so it does not count.</summary>
        private MissionTrigger? HintOf(Fact decide, int from)
        {
            if (Hint(decide.Trigger) != null)
                return decide.Trigger;
            for (var k = timeline.Count - 1; k >= from; k--)
            {
                var trigger = timeline[k];
                if (Hint(trigger) != null && !(trigger == chosen && IsArrival(trigger.Condition)))
                    return trigger;
            }
            return null;
        }

        /// <summary>The first spawn in the system with a position (rule 6). The spawns of the segment before the chosen
        /// trigger come first, then the spawns of the chosen trigger, then the last spawn of the replay. A move of a ship
        /// that exists is no spawn, because scripts also park ships with it.</summary>
        private (Vector3 Point, string Ship)? SpawnIn(string system, int from)
        {
            var lines = timeline.Skip(from).Take(Math.Max(0, Earlier - from)).SelectMany(t => t.Actions.Select(l => (t, l)))
                .Concat(chosen == null ? [] : chosen.Actions.Select(l => (chosen, l)))
                .Concat(replay.Effects.Reverse().Select(e => (e.Trigger, e.Line)));
            foreach (var (trigger, line) in lines)
            {
                if (IsAny(line.Key, SpawnActions) && line.Value(0) is { } name &&
                    MissionIndex.Is(SpawnSystem(trigger, name), system) && SpawnPoint(line, name) is { } point)
                    return (point, name);
            }
            return null;
        }

        /// <summary>The system of a spawn: the system of the condition or the system hint of its trigger, else the
        /// <c>system</c> line of the item.</summary>
        private string? SpawnSystem(MissionTrigger trigger, string name) =>
            ConditionSystem(trigger.Condition) ?? Hint(trigger) ??
            Known(ItemOf(name)?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "system") && e.Count > 0) is { } line
                ? MissionIndex.Text(line[0])
                : null, MissionValueKind.System);

        /// <summary>The position of a spawn line: its own point, or the <c>position</c> of the item.</summary>
        private Vector3? SpawnPoint(MissionLine line, string name)
        {
            if (line.Name?.Value("position") is { } at && Numbers(line, at.Index, 3) is { } own)
                return new Vector3(own[0], own[1], own[2]);
            if (ItemOf(name)?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "position") && e.Count >= 3) is not
                { } entry)
                return null;
            var values = Enumerable.Range(0, 3).Select(i => Parse(MissionIndex.Text(entry[i]))).ToList();
            return values.All(v => v != null) ? new Vector3(values[0]!.Value, values[1]!.Value, values[2]!.Value) : null;
        }

        private MissionSection? ItemOf(string name) =>
            index.Item(MissionSectionKind.Ship, name) ?? index.Item(MissionSectionKind.Formation, name) ??
            index.Item(MissionSectionKind.Solar, name) ?? index.Item(MissionSectionKind.Loot, name);

        /// <summary>
        /// The warnings about the triggers that wait for the end of the newest launch of the timeline (Cnd_LaunchComplete
        /// of the player). A trigger of the timeline up to the launch, or a Cnd_True trigger right after it, started
        /// them. In the game, the end of the launch ran them. A start in space never ends a launch, so they wait. The
        /// replay runs only the triggers of the path, so the user must know.
        /// </summary>
        private IReadOnlyList<string> LaunchWatchers(List<Fact> facts)
        {
            var launch = facts.Where(f => f.Kind == FactKind.Launch).Select(f => f.At).DefaultIfEmpty(-1).Max();
            if (launch < 0)
                return [];
            // The Cnd_True triggers right after the launch run at the same moment, before the launch ends.
            while (launch + 1 < Earlier && timeline[launch + 1].ConditionIs("Cnd_True"))
                launch++;
            return replay.ActiveSet
                .Where(t => t != chosen && t.ConditionIs("Cnd_LaunchComplete") && NamesPlayer(t.Condition!) &&
                            index.StartsOf(t).Any(l => timeline.IndexOf(l.Source) is >= 0 and var at && at <= launch))
                .Select(t => LaunchWatcherWarning(t.Nickname)).ToList();
        }

        /// <summary>True when the chosen trigger waits for the end of a launch: Cnd_LaunchComplete of the player, or
        /// Cnd_BaseExit without a base. A start in space sends neither.</summary>
        private bool WaitsForLaunchEnd(MissionLine? condition) =>
            condition != null &&
            (MissionIndex.Is(condition.Key, "Cnd_LaunchComplete") && NamesPlayer(condition) ||
             MissionIndex.Is(condition.Key, "Cnd_BaseExit") && Named(condition, "base", MissionValueKind.Base) == null);

        /// <summary>The system of Cnd_SpaceEnter of the chosen trigger, or null. The chosen trigger waits for a launch
        /// there.</summary>
        private string? OwnSystem() =>
            chosen?.Condition is { } condition && MissionIndex.Is(condition.Key, "Cnd_SpaceEnter")
                ? Named(condition, "system", MissionValueKind.System)
                : null;

        /// <summary>The system of a condition that puts the player in a system: Cnd_SpaceEnter or an arrival.</summary>
        private string? ConditionSystem(MissionLine? condition) =>
            MissionIndex.Is(condition?.Key, "Cnd_SpaceEnter") || IsArrival(condition)
                ? Named(condition!, "system", MissionValueKind.System)
                : null;

        /// <summary>The system of the base of a base condition, or null.</summary>
        private string? BaseSystem(MissionLine? condition) =>
            condition != null && IsAny(condition.Key, BaseConditions) &&
            Named(condition, "base", MissionValueKind.Base) is { } dock
                ? BaseSystemOf(dock)
                : null;

        /// <summary>The system of a base that the world knows, or null.</summary>
        private string? BaseSystemOf(string dock) => Known(index.World.SystemOfBase(dock), MissionValueKind.System);

        /// <summary>The system that the chosen trigger waits to arrive in, or null.</summary>
        private string? ArrivalSystem() =>
            IsArrival(chosen?.Condition) ? Named(chosen!.Condition!, "system", MissionValueKind.System) : null;

        private string? Hint(MissionTrigger trigger) => Known(trigger.SystemHint, MissionValueKind.System);

        /// <summary>The name of a value of a line, or null when the value is empty or a word, for example ANY or
        /// no_params.</summary>
        private string? Named(MissionLine line, string value, MissionValueKind kind)
        {
            if (line.Name?.Value(value) is not { } spec || line.Value(spec.Index) is not { } text)
                return null;
            var reference = line.References.FirstOrDefault(r => r.ValueIndex == spec.Index);
            return reference is { State: MissionReferenceState.Token or MissionReferenceState.Empty }
                ? null
                : Known(text, kind);
        }

        /// <summary>A name of a system or a base, or null for a word such as ANY or no_params. A world that knows the
        /// names of the kind also drops a name that it does not have, for example the system hint ALL. Then the next
        /// rule gives the place.</summary>
        private string? Known(string? name, MissionValueKind kind) =>
            name?.Trim() is { Length: > 0 } text && !MissionIndex.Is(text, "ANY") && !MissionIndex.Is(text, "no_params") &&
            (!index.World.Knows(kind) || index.World.Has(kind, text))
                ? text
                : null;
    }
}
