using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;

namespace OpenLancer.Data.Missions.Run;

/// <summary>The point where a play of a mission starts: the mission start, or a trigger (section 5.2 of the M6c
/// design). Two points with the same trigger name are equal without letter case.</summary>
public sealed class MissionStartPoint : IEquatable<MissionStartPoint>
{
    private MissionStartPoint(string? trigger) => Trigger = trigger;

    /// <summary>The start of the mission: the start triggers start.</summary>
    public static MissionStartPoint MissionStart { get; } = new(null);

    /// <summary>The nickname of the trigger of the start, or null for the mission start.</summary>
    public string? Trigger { get; }

    public bool IsMissionStart => Trigger == null;

    /// <summary>A start at a trigger.</summary>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public static MissionStartPoint At(string trigger) => trigger.Trim() is { Length: > 0 } name
        ? new MissionStartPoint(name)
        : throw new ArgumentException("A start at a trigger needs the name of the trigger.", nameof(trigger));

    /// <summary>The point after a rename of its trigger, or the same point.</summary>
    public MissionStartPoint Renamed(string from, string to) =>
        Trigger != null && MissionIndex.Is(Trigger, from.Trim()) ? At(to) : this;

    public bool Equals(MissionStartPoint? other) =>
        other is not null && string.Equals(Trigger, other.Trigger, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as MissionStartPoint);

    public override int GetHashCode() => Trigger == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Trigger);

    public override string ToString() => Trigger ?? "mission start";
}

/// <summary>The kind of a start (section 5.1 of the M6c design).</summary>
public enum StartKind
{
    /// <summary>The start triggers start, as at the start of the mission. Exact.</summary>
    MissionStart,

    /// <summary>The restore trigger of a checkpoint starts, as a load of a saved game does. Exact.</summary>
    Checkpoint,

    /// <summary>Any other trigger. Setup triggers rebuild the state of the path to it.</summary>
    Approximate,

    /// <summary>A script without a story slot. Only the linked game can start it (milestone M6c-3).</summary>
    TestScript,
}

/// <summary>Why a plan does not start (section 8.4 of the M6c design).</summary>
public enum StartRefusal
{
    /// <summary>The plan can start.</summary>
    None,

    /// <summary>The script has no story slot.</summary>
    NoSlot,

    /// <summary>The script has no such trigger.</summary>
    NoTrigger,

    /// <summary>The path passes a trigger without a condition (TR6).</summary>
    NoCondition,

    /// <summary>A stop on another branch removes a trigger of the path.</summary>
    NotActive,

    /// <summary>The mission ends, or a chain does not end, before the trigger starts.</summary>
    RunStops,

    /// <summary>A setup trigger has an error of the mission checks.</summary>
    SetupProblem,

    /// <summary>A key of the start save names data that the game data does not have, so the game cannot load the
    /// save.</summary>
    SaveProblem,
}

/// <summary>Where the user wants the player to start (section 5.3, step 7 of the M6c design).</summary>
public enum StartPlaceChoice
{
    /// <summary>The place that the plan suggests.</summary>
    Suggested,

    /// <summary>Docked at a base that the user picks.</summary>
    Base,

    /// <summary>At the Scene camera of the editor.</summary>
    Camera,
}

/// <summary>The kind of a start place.</summary>
public enum StartPlaceKind
{
    Space,
    Base,

    /// <summary>The Scene camera of the editor. The editor gives the point when the play starts.</summary>
    Camera,
}

/// <summary>The start place of a plan.</summary>
/// <param name="System">The system of a space start, or null when the plan knows none.</param>
/// <param name="Base">The base of a base start, else null.</param>
/// <param name="Position">The point of a space start.</param>
/// <param name="Orientation">The heading of a space start.</param>
/// <param name="Source">Where the place comes from, in plain words, for example "Act_MovePlayer of t_wave".</param>
public sealed record StartPlace(StartPlaceKind Kind, string? System, string? Base, Vector3 Position,
    Quaternion Orientation, string Source)
{
    /// <summary>The Scene camera.</summary>
    public static StartPlace Camera(string source) =>
        new(StartPlaceKind.Camera, null, null, Vector3.Zero, Quaternion.Identity, source);

    /// <summary>The place in the words of the Play start page: "In {system} at {x}, {y}, {z}", "At base {base}" or "At
    /// the Scene camera".</summary>
    public string Text => Kind switch
    {
        StartPlaceKind.Space => string.Create(CultureInfo.InvariantCulture,
            $"In {System} at {Position.X:0}, {Position.Y:0}, {Position.Z:0}"),
        StartPlaceKind.Base => $"At base {Base}",
        _ => "At the Scene camera",
    };
}

/// <summary>A setup trigger of an approximate start (section 5.3, step 9 of the M6c design): its nickname, its
/// condition and its actions, as lines of text.</summary>
/// <param name="Lines">The condition line first, then the actions, each as a key and its value texts.</param>
public sealed record SetupTrigger(string Nickname, IReadOnlyList<(string Key, IReadOnlyList<string> Values)> Lines);

/// <summary>The keys of <c>[MissionState]</c> of a start save that come from the path (section 5.3, step 9): the net
/// result of the actions of the role "save".</summary>
/// <param name="Title">The text id of <c>msn_title</c>, or 0.</param>
/// <param name="Offer">The text id of <c>msn_offer</c>, or 0.</param>
/// <param name="Scenes">The base scenes of <c>rtc</c> lines, with the repeat flag.</param>
/// <param name="Ambients">The ambient scripts of <c>ambi_scene</c> lines, with the room and the base.</param>
public sealed record StartMissionState(int Title, int Offer, IReadOnlyList<(string File, bool Repeats)> Scenes,
    IReadOnlyList<(string File, string Room, string Base)> Ambients)
{
    public static readonly StartMissionState Empty = new(0, 0, [], []);
}

/// <summary>
/// The start plan of a play from a trigger (section 5.3 of the M6c design, decisions D748 and D749): the kind of the
/// start, the story stage, the accept, the triggers that wait at the start, the triggers of the start save, the setup
/// triggers and the start place. A plan that cannot start is data: <see cref="Refusal"/> and <see cref="Reason"/> say
/// why.
/// </summary>
public sealed record StartPlan
{
    /// <summary>The prefix of a refusal in the Console (section 8.4 of the M6c design).</summary>
    public const string RefusalPrefix = "Play from here is not available: ";

    /// <summary>The install path of the script.</summary>
    public required string Script { get; init; }

    public required MissionStartPoint Start { get; init; }

    public StartKind Kind { get; init; }

    /// <summary>The story slot of the script, or null.</summary>
    public StorySlot? Slot { get; init; }

    public StartRefusal Refusal { get; init; }

    /// <summary>Why the plan cannot start, in the strict form of section 8.4, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>True when the plan can start.</summary>
    public bool CanStart => Refusal == StartRefusal.None;

    /// <summary>The story stage of the start save (<c>MissionNum</c>).</summary>
    public int Stage { get; init; }

    /// <summary>True when the start comes after the accept of the mission.</summary>
    public bool Accepted { get; init; }

    /// <summary>The triggers that wait at the start, as at this point of the mission.</summary>
    public IReadOnlyList<string> ActiveSet { get; init; } = [];

    /// <summary>The triggers of the <c>[TriggerSave]</c> sections of the start save. A mission start has none.</summary>
    public IReadOnlyList<string> SavedTriggers { get; init; } = [];

    /// <summary>The setup triggers, in file order: the trigger of the start, then the trigger of the world. A mission
    /// start and most checkpoints have none.</summary>
    public IReadOnlyList<SetupTrigger> Setup { get; init; } = [];

    /// <summary>The keys of <c>[MissionState]</c> that come from the path.</summary>
    public StartMissionState State { get; init; } = StartMissionState.Empty;

    /// <summary>The start place.</summary>
    public StartPlace Place { get; init; } = StartPlace.Camera("No rule gives a place.");

    /// <summary>The triggers of the replay path, from the root to the chosen trigger. Empty when no root reaches the
    /// trigger.</summary>
    public IReadOnlyList<string> Path { get; init; } = [];

    /// <summary>The warnings of the Play start page.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The name of the start in the UI texts (section 8.1 of the M6c design): "Mission start", "Checkpoint
    /// {trigger}", "{trigger} (approximate)" or "Test script".</summary>
    public string StartName => NameOf(Kind, Start);

    /// <summary>The setup triggers as the INI text of a patch: each section with <c>ol.new = true</c> (section 5.4 of
    /// the M6c design). Empty when the plan has no setup trigger.</summary>
    public string SetupPatch => StartSetup.Patch(Setup);

    /// <summary>The name of a start: "Mission start", "Checkpoint {trigger}", "{trigger} (approximate)" or "Test
    /// script".</summary>
    public static string NameOf(StartKind kind, MissionStartPoint start) => kind switch
    {
        StartKind.MissionStart when start.Trigger is { } trigger => $"Mission start ({trigger})",
        StartKind.MissionStart => "Mission start",
        StartKind.Checkpoint => $"Checkpoint {start.Trigger}",
        StartKind.TestScript => "Test script",
        _ => $"{start.Trigger} (approximate)",
    };

    /// <summary>The text of the warning of a trigger that no root reaches (section 8.3 of the M6c design).</summary>
    public static string NoPathWarning(string trigger) =>
        $"No start trigger and no checkpoint leads to trigger '{trigger}'. The play starts only this trigger.";

    /// <summary>The reason of a script without a story slot (section 8.4 of the M6c design). The sentence about Ctrl+F5
    /// comes with the start in the linked game (milestone M6c-3).</summary>
    public const string NoSlotReason = "This mission script has no story slot. OpenLancer can start only the scripts of " +
                                       "the 14 story slots.";

    /// <summary>The reason of a start at a trigger that the script does not have.</summary>
    public static string NoTriggerReason(string trigger) =>
        $"The script has no trigger '{trigger}'. Pick a trigger of the script.";

    /// <summary>The warning of a start from a checkpoint, also of an approximate start whose path starts at a
    /// checkpoint. A saved game of the retail game keeps more than the triggers. The start save keeps only them.</summary>
    public static string CheckpointStateWarning(string trigger) =>
        $"A saved game at checkpoint '{trigger}' also keeps the objective, the dock and lane rules, the reputations and " +
        "other state from before the save. The start save of this play keeps only the triggers, so that state can differ.";

    /// <summary>The kind of a start: a script without a story slot gives a test script, the mission start and a start
    /// trigger give the mission start, a restore trigger gives a checkpoint, and each other trigger gives an
    /// approximate start. A trigger that the script does not have gives an approximate start too.</summary>
    public static StartKind KindOf(MissionIndex index, StorySlot? slot, MissionStartPoint start) =>
        slot == null ? StartKind.TestScript
        : start.Trigger is not { } name ? StartKind.MissionStart
        : index.Trigger(name) is { IsDuplicate: false } trigger ? KindOf(index, trigger)
        : StartKind.Approximate;

    /// <summary>The kind of a start at a trigger of a script with a story slot.</summary>
    internal static StartKind KindOf(MissionIndex index, MissionTrigger trigger) =>
        index.StartTriggers.Contains(trigger) ? StartKind.MissionStart
        : DryRun.Checkpoints(index).Contains(trigger) ? StartKind.Checkpoint
        : StartKind.Approximate;

    /// <summary>
    /// Makes the plan of a start (section 5.3 of the M6c design). The plan reads only the index, so it is pure: the same
    /// index, start and place choice give the same plan.
    /// </summary>
    /// <param name="slot">The story slot of the script, or null.</param>
    /// <param name="place">The place choice of the user.</param>
    /// <param name="placeBase">The base of <see cref="StartPlaceChoice.Base"/>, else null.</param>
    /// <param name="checkSetup">False skips the mission checks of the setup triggers (section 5.3, step 11). They build
    /// the index again for each plan, so the Play buttons of the graph make their plans without them. Play makes the
    /// full plan.</param>
    public static StartPlan Make(MissionIndex index, StorySlot? slot, MissionStartPoint start,
        StartPlaceChoice place = StartPlaceChoice.Suggested, string? placeBase = null, bool checkSetup = true)
    {
        var plan = new StartPlan { Script = index.Document.Path, Start = start, Slot = slot };
        if (slot == null)
            return plan with { Kind = StartKind.TestScript, Refusal = StartRefusal.NoSlot, Reason = NoSlotReason };
        var replay = StartReplay.Run(index, start);
        plan = plan with
        {
            Kind = replay.Kind,
            Stage = slot.StartStage,
            ActiveSet = replay.ActiveSet.Select(t => t.Nickname).ToList(),
            Path = replay.Path.Select(t => t.Nickname).ToList(),
            Warnings = replay.Warnings,
        };
        if (replay.Refusal != StartRefusal.None)
            return plan with { Refusal = replay.Refusal, Reason = replay.Reason };

        var story = StoryPath(index, replay.Chosen);
        var accepted = story.Any(t => t != replay.Chosen && IsAccept(t));
        var notes = slot.Notes.Select(n => MissionSchema.Retail.SlotNotes.GetValueOrDefault(n)).OfType<string>();
        // A restore trigger at the root of the replay rebuilds the triggers of a checkpoint, but not the rest of the
        // state of a saved game.
        IReadOnlyList<string> restored = replay.Kind != StartKind.MissionStart && replay.Root is { StartsActive: false } root
            ? [CheckpointStateWarning(root.Nickname)]
            : [];
        plan = plan with
        {
            Stage = accepted ? slot.AcceptStage : slot.StartStage,
            Accepted = accepted,
            Warnings = [.. plan.Warnings, .. restored, .. notes],
            Place = place switch
            {
                StartPlaceChoice.Base when placeBase != null => new StartPlace(StartPlaceKind.Base, null, placeBase.Trim(),
                    Vector3.Zero, Quaternion.Identity, "The base that you picked."),
                StartPlaceChoice.Camera => StartPlace.Camera("The Scene camera."),
                _ => StartPlaces.Suggest(index, replay.Chosen, story, replay.Effects),
            },
        };
        return StartSetup.Apply(plan, index, replay, story, checkSetup);
    }

    /// <summary>
    /// The story path of a trigger (section 5.3, step 5 of the M6c design): the shortest path of Start and Load links
    /// (<c>Act_ActTrig</c> and <c>Act_Save</c>) from a start trigger to the trigger. A checkpoint is reached only
    /// through its <c>Act_Save</c> line, so the path needs the Load links. Empty when no start trigger reaches the
    /// trigger, and only the start trigger for a start trigger.
    /// </summary>
    internal static IReadOnlyList<MissionTrigger> StoryPath(MissionIndex index, MissionTrigger? chosen) =>
        chosen == null ? [] : ShortestPath(index, index.StartTriggers, chosen, MissionLinkKind.Start, MissionLinkKind.Load);

    /// <summary>The shortest path of links of some kinds from the first root that reaches the target, in the order of
    /// the roots. Empty when no root reaches it. A start that a stop after it cancels in the same trigger is not a link,
    /// because the game does not start its target (TR9).</summary>
    internal static IReadOnlyList<MissionTrigger> ShortestPath(MissionIndex index, IEnumerable<MissionTrigger> roots,
        MissionTrigger target, params MissionLinkKind[] kinds)
    {
        var cancelled = index.Cancels.Where(c => c.Target != null).Select(c => (c.Source, c.Target!)).ToHashSet();
        var next = index.Links.Where(l => kinds.Contains(l.Kind) && l.Target is { IsDuplicate: false } && !l.Source.IsDuplicate &&
                                          !(l.Kind == MissionLinkKind.Start && cancelled.Contains((l.Source, l.Target))))
            .ToLookup(l => l.Source, l => l.Target!);
        var parent = new Dictionary<MissionTrigger, MissionTrigger?>();
        var queue = new Queue<MissionTrigger>();
        foreach (var root in roots.Where(r => !r.IsDuplicate))
        {
            if (parent.TryAdd(root, null))
                queue.Enqueue(root);
        }
        while (queue.Count > 0)
        {
            var trigger = queue.Dequeue();
            if (trigger == target)
            {
                var path = new List<MissionTrigger>();
                for (MissionTrigger? at = trigger; at != null; at = parent[at])
                    path.Add(at);
                path.Reverse();
                return path;
            }
            foreach (var child in next[trigger])
            {
                if (parent.TryAdd(child, trigger))
                    queue.Enqueue(child);
            }
        }
        return [];
    }

    /// <summary>True for a trigger whose condition waits for the accept of the mission
    /// (<c>Cnd_MsnResponse = accept</c>).</summary>
    private static bool IsAccept(MissionTrigger trigger) =>
        trigger.ConditionIs("Cnd_MsnResponse") && MissionIndex.Is(trigger.Condition!.Value(0), "accept");
}

/// <summary>
/// The replay of a start plan (section 5.3, steps 2 to 4 of the M6c design): a new machine runs the path to the chosen
/// trigger and stops when the chosen trigger starts. The dry run input Start at trigger uses the same replay.
/// </summary>
internal sealed record StartReplay
{
    /// <summary>The most steps of a replay: a heartbeat or a forced run for each trigger of the path.</summary>
    private const int StepsPerTrigger = 4;

    public StartKind Kind { get; init; }

    /// <summary>The trigger of the start, or null for the mission start.</summary>
    public MissionTrigger? Chosen { get; init; }

    /// <summary>The root of the path: a start trigger or a restore trigger, or null.</summary>
    public MissionTrigger? Root { get; init; }

    public IReadOnlyList<MissionTrigger> Path { get; init; } = [];

    /// <summary>The triggers that wait when the replay stops: the active list, the chosen trigger and the pending
    /// starts.</summary>
    public IReadOnlyList<MissionTrigger> ActiveSet { get; init; } = [];

    /// <summary>The actions without flow of the replay, in the order of the game.</summary>
    public IReadOnlyList<(MissionTrigger Trigger, MissionLine Line)> Effects { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public StartRefusal Refusal { get; init; }

    public string? Reason { get; init; }

    /// <summary>Runs the replay of a start.</summary>
    public static StartReplay Run(MissionIndex index, MissionStartPoint start)
    {
        if (start.Trigger is not { } name)
            return Exact(index, null, StartKind.MissionStart);
        if (index.Trigger(name) is not { IsDuplicate: false } chosen)
            return new StartReplay { Refusal = StartRefusal.NoTrigger, Reason = StartPlan.NoTriggerReason(name) };
        if (StartPlan.KindOf(index, chosen) is var kind and not StartKind.Approximate)
            return Exact(index, chosen, kind);
        var roots = index.StartTriggers.Concat(DryRun.Checkpoints(index));
        var path = StartPlan.ShortestPath(index, roots, chosen, MissionLinkKind.Start);
        if (path.Count == 0)
            return new StartReplay
            {
                Kind = StartKind.Approximate,
                Chosen = chosen,
                ActiveSet = [chosen],
                Warnings = [StartPlan.NoPathWarning(chosen.Nickname)],
            };
        return Approximate(index, chosen, path);
    }

    /// <summary>An exact start: the start set, or the load of the restore trigger. The replay records the actions of the
    /// triggers that run at once, for the start place.</summary>
    private static StartReplay Exact(MissionIndex index, MissionTrigger? chosen, StartKind kind)
    {
        var world = new ReplayWorld();
        var machine = new TriggerMachine(index, world);
        if (kind == StartKind.Checkpoint)
            machine.Load([chosen!]);
        else
            machine.StartMission();
        var replay = new StartReplay
        {
            Kind = kind,
            Chosen = chosen,
            Root = chosen,
            Path = chosen == null ? [] : [chosen],
            ActiveSet = [.. machine.Active, .. machine.Pending.Where(t => !machine.Active.Contains(t))],
            Effects = world.Effects,
        };
        return machine.Halt is { } halt ? replay with { Refusal = RefusalOf(halt, chosen), Reason = ReasonOf(halt, chosen) } : replay;
    }

    /// <summary>The replay of an approximate start (D748): the start set or the load of the root, then each trigger of
    /// the path in order, until the chosen trigger starts.</summary>
    private static StartReplay Approximate(MissionIndex index, MissionTrigger chosen, IReadOnlyList<MissionTrigger> path)
    {
        var world = new ReplayWorld();
        var machine = new TriggerMachine(index, world, new MachineOptions { StopBefore = chosen });
        var root = path[0];
        if (root.StartsActive)
            machine.StartMission();
        else
            machine.Load([root]);
        var replay = new StartReplay { Kind = StartKind.Approximate, Chosen = chosen, Root = root, Path = path };
        // Each trigger of the path runs in order: a trigger that ran goes on, a pending trigger starts at the next
        // heartbeat, and an active trigger runs as if its condition is met now.
        foreach (var trigger in path)
        {
            for (var step = 0; ; step++)
            {
                if (machine.Halt is { Kind: MachineHaltKind.StopPoint })
                    break;
                if (machine.Halt is { } halt)
                    return replay with { Refusal = RefusalOf(halt, chosen), Reason = ReasonOf(halt, chosen) };
                if (machine.End is { } end)
                    return replay with
                    {
                        Refusal = StartRefusal.RunStops,
                        Reason = $"The mission ends at trigger '{end.Trigger.Nickname}' before trigger '{chosen.Nickname}' " +
                                 "starts. Pick another trigger.",
                    };
                var state = machine.StateOf(trigger);
                if (trigger != chosen && state.Runs > 0)
                    break;
                if (step >= StepsPerTrigger)
                    return NotActive(replay, trigger, chosen);
                if (machine.Pending.Contains(trigger))
                    machine.Heartbeat();
                else if (trigger != chosen && state.Status == TriggerStatus.Active && machine.Active.Contains(trigger))
                    machine.Force(trigger);
                else
                    return NotActive(replay, trigger, chosen);
            }
            if (machine.Halt is { Kind: MachineHaltKind.StopPoint })
                break;
        }
        if (machine.Halt is not { Kind: MachineHaltKind.StopPoint })
            return NotActive(replay, chosen, chosen);
        // The game starts the rest of the start set or of the drain in the same pass as the chosen trigger.
        var active = new List<MissionTrigger>(machine.Active) { chosen };
        active.AddRange(machine.Pending.Where(t => !active.Contains(t)));
        return replay with { ActiveSet = active, Effects = world.Effects };
    }

    /// <summary>The refusal of a trigger of the path that does not start or run at its point (section 8.4 of the M6c
    /// design). The chosen trigger gets its own text, so the text does not name it twice.</summary>
    private static StartReplay NotActive(StartReplay replay, MissionTrigger other, MissionTrigger chosen) => replay with
    {
        Refusal = StartRefusal.NotActive,
        Reason = other == chosen
            ? $"The path to trigger '{chosen.Nickname}' does not start it, because another line stops it or cancels its " +
              "start. Pick another trigger."
            : $"Trigger '{other.Nickname}' on the path to '{chosen.Nickname}' is not active at that point, because " +
              "another trigger stops it first. Pick another trigger.",
    };

    private static StartRefusal RefusalOf(MachineHalt halt, MissionTrigger? chosen) =>
        halt.Kind == MachineHaltKind.NoCondition ? StartRefusal.NoCondition : StartRefusal.RunStops;

    /// <summary>The reason of a stop of the replay (section 8.4 of the M6c design).</summary>
    private static string ReasonOf(MachineHalt halt, MissionTrigger? chosen)
    {
        var target = chosen?.Nickname ?? halt.Trigger.Nickname;
        return halt.Kind == MachineHaltKind.NoCondition
            ? $"The path to trigger '{target}' passes trigger '{halt.Trigger.Nickname}', which has no condition. The game " +
              "stops at that trigger. Add a condition to it."
            : halt.Message;
    }

    /// <summary>The world of a replay: no event and no fact comes, and the player is in space. It records each action
    /// without flow in order.</summary>
    private sealed class ReplayWorld : ITriggerWorld
    {
        public List<(MissionTrigger Trigger, MissionLine Line)> Effects { get; } = [];

        public bool InSpace => true;

        public bool Matches(MissionTrigger trigger, MissionLine condition, WorldEvent worldEvent) => false;

        public bool Poll(MissionTrigger trigger, MissionLine condition) => false;

        public bool TestAtStart(MissionTrigger trigger, MissionLine condition) => false;

        public void Effect(MissionTrigger trigger, MissionLine action) => Effects.Add((trigger, action));
    }
}

/// <summary>The rules of the suggested start place (section 5.3, step 7 of the M6c design).</summary>
internal static class StartPlaces
{
    /// <summary>The offset of a start from a spawn position or the system center, in meters.</summary>
    public const float Offset = 1000;

    private static readonly string[] PlaceActions = ["Act_MovePlayer", "Act_SetInitialPlayerPos"];

    private static readonly string[] SpawnActions =
        ["Act_SpawnShip", "Act_SpawnFormation", "Act_SpawnSolar", "Act_SpawnLoot", "Act_RelocateShip", "Act_RelocateForm"];

    /// <summary>The suggested place: the first rule that gives a place.</summary>
    /// <param name="chosen">The trigger of the start, or null for the mission start.</param>
    /// <param name="story">The story path of the trigger.</param>
    /// <param name="effects">The actions without flow of the replay, in order.</param>
    public static StartPlace Suggest(MissionIndex index, MissionTrigger? chosen, IReadOnlyList<MissionTrigger> story,
        IReadOnlyList<(MissionTrigger Trigger, MissionLine Line)> effects)
    {
        var earlier = story.Where(t => t != chosen).Reverse().ToList();
        // A space start without a system takes the system of the last system condition on the story path, then the
        // system hint of the trigger, then the system of the first spawn of the trigger, of the replay, or of the story
        // path from its end. An exact start has no replay of the path, and a restore trigger rebuilds its scene with
        // its own spawns.
        string? SystemOfPoint() =>
            earlier.Select(t => SpaceSystem(t.Condition)).FirstOrDefault(s => s != null) ?? chosen?.SystemHint ??
            (chosen?.Actions ?? []).Concat(effects.Select(e => e.Line)).Concat(earlier.SelectMany(t => t.Actions))
            .Select(line => SpawnSystem(index, line)).FirstOrDefault(s => s != null);

        // 1. A place line of the chosen trigger.
        if (chosen?.Actions.FirstOrDefault(IsPlaceLine) is { } own && Point(own) is var (point, turn))
            return Space(SystemOfPoint(), point, turn, $"{own.Key} of {chosen.Nickname}");
        // 2. The condition of the chosen trigger.
        if (chosen != null && PlaceOf(index, chosen, effects) is { } place)
            return place;
        // 3. The last place condition on the story path.
        foreach (var trigger in earlier)
        {
            if (PlaceOf(index, trigger, effects) is { } before)
                return before;
        }
        // 4. The last place line of the replay.
        if (effects.LastOrDefault(e => IsPlaceLine(e.Line)) is { Line: { } last } effect && Point(last) is var (at, heading))
            return Space(SystemOfPoint(), at, heading, $"{last.Key} of {effect.Trigger.Nickname}");
        // 5. The system hint of the trigger, at the first spawn of the replay in that system.
        if (chosen?.SystemHint is { } hint &&
            effects.FirstOrDefault(e => MissionIndex.Is(SpawnSystem(index, e.Line), hint)) is { Line: { } spawn } &&
            SpawnPoint(index, spawn) is { } spawnPoint)
            return Space(hint, spawnPoint + new Vector3(0, 0, Offset), Quaternion.Identity,
                $"The system hint of {chosen.Nickname}, near {spawn.Key}");
        // 6. The Scene camera.
        return StartPlace.Camera("No line of the path gives a place.");
    }

    /// <summary>The place that the condition of a trigger gives, or null: a system of Cnd_SystemEnter,
    /// Cnd_JumpInComplete or Cnd_SpaceEnter, or a base of Cnd_BaseEnter, Cnd_LocEnter or Cnd_CharSelect.</summary>
    private static StartPlace? PlaceOf(MissionIndex index, MissionTrigger trigger,
        IReadOnlyList<(MissionTrigger Trigger, MissionLine Line)> effects)
    {
        var condition = trigger.Condition;
        if (SpaceSystem(condition) is { } system)
        {
            // A space start without a point uses the last spawn position of the replay in the system, or the system
            // center, with an offset.
            var near = effects.LastOrDefault(e => MissionIndex.Is(SpawnSystem(index, e.Line), system));
            var point = near.Line != null && SpawnPoint(index, near.Line) is { } spawn ? spawn : Vector3.Zero;
            return Space(system, point + new Vector3(0, 0, Offset), Quaternion.Identity,
                $"{condition!.Key} of {trigger.Nickname}");
        }
        if (condition != null && BaseOf(condition) is { } dock)
            return new StartPlace(StartPlaceKind.Base, null, dock, Vector3.Zero, Quaternion.Identity,
                $"{condition.Key} of {trigger.Nickname}");
        return null;
    }

    /// <summary>The system of a condition that gives a space start, or null. Cnd_SpaceEnter without a system gives
    /// none.</summary>
    private static string? SpaceSystem(MissionLine? condition) =>
        condition?.Name?.Name is "Cnd_SystemEnter" or "Cnd_JumpInComplete" or "Cnd_SpaceEnter"
            ? Named(condition, "system")
            : null;

    /// <summary>The base of a condition that gives a base start, or null.</summary>
    private static string? BaseOf(MissionLine condition) =>
        condition.Name?.Name is "Cnd_BaseEnter" or "Cnd_LocEnter" or "Cnd_CharSelect" ? Named(condition, "base") : null;

    /// <summary>The name of a value of a line, or null when the value is empty or a word of the value, for example ANY
    /// or no_params.</summary>
    private static string? Named(MissionLine line, string value)
    {
        if (line.Name?.Value(value) is not { } spec || line.Value(spec.Index) is not { } text)
            return null;
        var reference = line.References.FirstOrDefault(r => r.ValueIndex == spec.Index);
        if (reference is { State: MissionReferenceState.Token or MissionReferenceState.Empty })
            return null;
        return MissionIndex.Is(text, "ANY") || MissionIndex.Is(text, "no_params") ? null : text;
    }

    private static bool IsPlaceLine(MissionLine line) => PlaceActions.Any(a => MissionIndex.Is(line.Key, a));

    /// <summary>The point and the heading of a place line. <c>Act_SetInitialPlayerPos</c> holds its heading as w, x,
    /// y, z after the point.</summary>
    private static (Vector3 Point, Quaternion Turn)? Point(MissionLine line)
    {
        if (Numbers(line, 0, 3) is not { } p)
            return null;
        var turn = MissionIndex.Is(line.Key, "Act_SetInitialPlayerPos") && Numbers(line, 3, 4) is { } q &&
                   new Quaternion(q[1], q[2], q[3], q[0]) is var rotation && rotation.Length() > 1e-3f
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;
        return (new Vector3(p[0], p[1], p[2]), turn);
    }

    /// <summary>The system of the item that a spawn or a move line names, or null.</summary>
    private static string? SpawnSystem(MissionIndex index, MissionLine line)
    {
        if (!SpawnActions.Any(a => MissionIndex.Is(line.Key, a)) || line.Value(0) is not { } name)
            return null;
        return ItemOf(index, name) is { } item ? index.SystemOf(item) : null;
    }

    /// <summary>The position of a spawn or a move line: its own point, or the <c>position</c> of the item.</summary>
    private static Vector3? SpawnPoint(MissionIndex index, MissionLine line)
    {
        var at = line.Name?.Value("position");
        if (at != null && Numbers(line, at.Index, 3) is { } own)
            return new Vector3(own[0], own[1], own[2]);
        if (line.Value(0) is not { } name || ItemOf(index, name) is not { } item ||
            item.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "position") && e.Count >= 3) is not { } entry)
            return null;
        var values = Enumerable.Range(0, 3).Select(i => Parse(MissionIndex.Text(entry[i]))).ToList();
        return values.All(v => v != null) ? new Vector3(values[0]!.Value, values[1]!.Value, values[2]!.Value) : null;
    }

    private static MissionSection? ItemOf(MissionIndex index, string name) =>
        index.Item(MissionSectionKind.Ship, name) ?? index.Item(MissionSectionKind.Formation, name) ??
        index.Item(MissionSectionKind.Solar, name) ?? index.Item(MissionSectionKind.Loot, name);

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

    /// <summary>A space start. Without a system, the play cannot start in space, so the place is the Scene
    /// camera.</summary>
    private static StartPlace Space(string? system, Vector3 point, Quaternion turn, string source) => system != null
        ? new StartPlace(StartPlaceKind.Space, system, null, point, turn, source)
        : StartPlace.Camera($"No line of the path names the system of {source}.");
}
