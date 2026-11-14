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
/// <param name="System">The system of a space start. A suggested Scene camera can also have the system where the
/// player is, so the camera must be in that system. Else null.</param>
/// <param name="Base">The base of a base start, else null.</param>
/// <param name="Position">The point of a space start.</param>
/// <param name="Orientation">The heading of a space start.</param>
/// <param name="Source">Where the place comes from, in plain words, for example "'Move player' of t_wave.".</param>
public sealed record StartPlace(StartPlaceKind Kind, string? System, string? Base, Vector3 Position,
    Quaternion Orientation, string Source)
{
    /// <summary>The Scene camera, in any system or in a system of the suggestion.</summary>
    public static StartPlace Camera(string source, string? system = null) =>
        new(StartPlaceKind.Camera, system, null, Vector3.Zero, Quaternion.Identity, source);

    /// <summary>The trigger whose line gives the place, or null. The Play start page links to it.</summary>
    public string? Trigger { get; init; }

    /// <summary>The place in the words of the Play start page: "In {system} at {x}, {y}, {z}", "At base {base}", "At
    /// the Scene camera" or "At the Scene camera in {system}".</summary>
    public string Text => Kind switch
    {
        StartPlaceKind.Space => string.Create(CultureInfo.InvariantCulture,
            $"In {System} at {Position.X:0}, {Position.Y:0}, {Position.Z:0}"),
        StartPlaceKind.Base => $"At base {Base}",
        _ when System != null => $"At the Scene camera in {System}",
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

    /// <summary>The start place of the place choice.</summary>
    public StartPlace Place { get; init; } = StartPlace.Camera("No rule gives a place.");

    /// <summary>The place that the plan suggests, also while the place choice is a base or the Scene camera.</summary>
    public StartPlace Suggested { get; init; } = StartPlace.Camera("No rule gives a place.");

    /// <summary>The triggers of the replay path, from the root to the chosen trigger. Empty when no root reaches the
    /// trigger.</summary>
    public IReadOnlyList<string> Path { get; init; } = [];

    /// <summary>The warnings of the Play start page.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The system whose world the setup trigger of the world makes, or null when the start system is not
    /// known. Then the setup keeps each world line of the path, for example for a start at the Scene camera before the
    /// play.</summary>
    public string? StartSystem { get; init; }

    /// <summary>The objects that the setup of the world leaves out, because they are in another system at the chosen
    /// trigger, and the fuse lines that it removes.</summary>
    public IReadOnlyList<LeftOutObject> LeftOut { get; init; } = [];

    /// <summary>The ships and formations that travel with the player at the chosen trigger, in the order of the setup
    /// lines.</summary>
    public IReadOnlyList<StartCompanion> Companions { get; init; } = [];

    /// <summary>
    /// True for a combat start (issue #48): the play starts in space, and the world of the start system has a fight. The
    /// setup makes a ship that the path makes hostile to the player. Or a trigger that waits at the start has a condition
    /// of the event group combat on an object that the setup makes. That object must not be a companion or friendly to
    /// the player. Only an approximate start whose start system is known has a setup of the world. A mission start and
    /// a checkpoint start make no objects before their triggers run, and the player is docked at a base start. So these
    /// starts are never combat starts.
    /// </summary>
    public bool Combat { get; init; }

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

    /// <summary>The limit text of a start from a checkpoint, also of an approximate start whose path starts at a
    /// checkpoint (#8). A saved game of the retail game keeps more than the triggers. The start save keeps only them.
    /// The Play start page and the tooltip of the Play button of a node show it as a warning.</summary>
    public static string CheckpointStateWarning(string trigger) =>
        $"This version restores only the triggers of checkpoint '{trigger}'. A saved game also keeps the objective, " +
        "the dock and lane rules, the reputations and other state from before the save. This version does not " +
        "restore that state.";

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
        : StartGraph.Of(index).IsCheckpoint(trigger) ? StartKind.Checkpoint
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
    /// <param name="play">The place of the play after the start check, or null. The setup of the world makes the objects
    /// of its system and puts the companions next to it. Without it, a start at the Scene camera keeps each world line of
    /// the path.</param>
    public static StartPlan Make(MissionIndex index, StorySlot? slot, MissionStartPoint start,
        StartPlaceChoice place = StartPlaceChoice.Suggested, string? placeBase = null, bool checkSetup = true,
        StartSavePlace? play = null)
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
        var picked = place switch
        {
            StartPlaceChoice.Base when placeBase != null => new StartPlace(StartPlaceKind.Base, null, placeBase.Trim(),
                Vector3.Zero, Quaternion.Identity, "The base that you picked."),
            StartPlaceChoice.Camera => StartPlace.Camera("The Scene camera."),
            _ => null,
        };
        var suggestion = StartPlaces.Suggest(index, replay, picked);
        plan = plan with
        {
            Stage = accepted ? slot.AcceptStage : slot.StartStage,
            Accepted = accepted,
            Warnings = [.. plan.Warnings, .. restored, .. notes, .. suggestion.Warnings],
            Place = suggestion.Place,
            Suggested = suggestion.Suggested,
        };
        return StartSetup.Apply(plan, index, replay, story, checkSetup, StartAnchor.Of(index, plan.Place, play));
    }

    /// <summary>
    /// The story path of a trigger (section 5.3, step 5 of the M6c design): the shortest path of Start and Load links
    /// (<c>Act_ActTrig</c> and <c>Act_Save</c>) from a start trigger to the trigger. A checkpoint is reached only
    /// through its <c>Act_Save</c> line, so the path needs the Load links. Empty when no start trigger reaches the
    /// trigger, and only the start trigger for a start trigger.
    /// </summary>
    internal static IReadOnlyList<MissionTrigger> StoryPath(MissionIndex index, MissionTrigger? chosen) =>
        chosen == null ? [] : StartGraph.Of(index).StoryPath(chosen);

    /// <summary>True for a trigger whose condition waits for the accept of the mission
    /// (<c>Cnd_MsnResponse = accept</c>).</summary>
    private static bool IsAccept(MissionTrigger trigger) =>
        trigger.ConditionIs("Cnd_MsnResponse") && MissionIndex.Is(trigger.Condition!.Value(0), "accept");
}

/// <summary>
/// The replay of a start plan (section 5.3, steps 2 to 4 of the M6c design): a new machine runs the path to the chosen
/// trigger and stops when the chosen trigger starts. The dry run input Start at trigger uses the same replay.
/// </summary>
internal sealed partial record StartReplay
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

    /// <summary>The jumps of the player that the conditions of the path show, in order. Each jump is the number of
    /// <see cref="Changes"/> before the arrival: the triggers of the exit ran before that point, and the triggers of the
    /// entry after it.</summary>
    public IReadOnlyList<int> Jumps { get; init; } = [];

    private readonly IReadOnlyList<MachineChange> changes = [];

    /// <summary>The runs and the actions without flow of the replay, in the order of the game (the Ran and the Effect
    /// changes of its passes). A run without an effect still marks the time of its condition, for example an
    /// arrival.</summary>
    public IReadOnlyList<MachineChange> Changes
    {
        get => changes;
        init
        {
            changes = value;
            Effects = value.Where(c => c is { Kind: MachineChangeKind.Effect, Line: not null })
                .Select(c => (c.Trigger, c.Line!)).ToList();
        }
    }

    /// <summary>The actions without flow of the replay, in the order of the game. <see cref="Changes"/> gives
    /// them.</summary>
    public IReadOnlyList<(MissionTrigger Trigger, MissionLine Line)> Effects { get; private init; } = [];

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
        var path = StartGraph.Of(index).ReplayPath(chosen);
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
        var machine = new TriggerMachine(index, new ReplayWorld());
        var pass = kind == StartKind.Checkpoint ? machine.Load([chosen!]) : machine.StartMission();
        var replay = new StartReplay
        {
            Kind = kind,
            Chosen = chosen,
            Root = chosen,
            Path = chosen == null ? [] : [chosen],
            ActiveSet = [.. machine.Active, .. machine.Pending.Where(t => !machine.Active.Contains(t))],
            Changes = Kept(pass.Changes).ToList(),
        };
        return machine.Halt is { } halt ? replay with { Refusal = RefusalOf(halt, chosen), Reason = ReasonOf(halt, chosen) } : replay;
    }

    /// <summary>The replay of an approximate start (D748): the start set or the load of the root, then each trigger of
    /// the path in order, until the chosen trigger starts. The replay also runs the waiting triggers that the path shows
    /// ran in the game (<see cref="Replayer"/>).</summary>
    private static StartReplay Approximate(MissionIndex index, MissionTrigger chosen, IReadOnlyList<MissionTrigger> path)
    {
        var run = new Replayer(index, chosen, path);
        var replay = new StartReplay { Kind = StartKind.Approximate, Chosen = chosen, Root = path[0], Path = path };
        // Each trigger of the path runs in order: a trigger that ran goes on, a pending trigger starts at the next
        // heartbeat, and an active trigger runs as if its condition is met now.
        for (var k = 0; k < path.Count; k++)
        {
            var trigger = path[k];
            if (run.Machine.Halt is { Kind: MachineHaltKind.StopPoint })
                break;
            run.Before(k);
            switch (run.Reach(trigger))
            {
                case Reached.Halt:
                    return replay with { Refusal = RefusalOf(run.Machine.Halt!, chosen), Reason = ReasonOf(run.Machine.Halt!, chosen) };
                case Reached.End:
                    return replay with
                    {
                        Refusal = StartRefusal.RunStops,
                        Reason = $"The mission ends at trigger '{run.Machine.End!.Trigger.Nickname}' before trigger " +
                                 $"'{chosen.Nickname}' starts. Pick another trigger.",
                    };
                case Reached.NotActive:
                    return NotActive(replay, trigger, chosen);
            }
            if (run.Machine.Halt is { Kind: MachineHaltKind.StopPoint })
                break;
            run.After(k);
        }
        var machine = run.Machine;
        if (machine.Halt is not { Kind: MachineHaltKind.StopPoint })
            return NotActive(replay, chosen, chosen);
        // The game starts the rest of the start set or of the drain in the same pass as the chosen trigger.
        var active = new List<MissionTrigger>(machine.Active) { chosen };
        active.AddRange(machine.Pending.Where(t => !active.Contains(t)));
        return replay with { ActiveSet = active, Changes = run.Changes, Jumps = run.Jumps };
    }

    /// <summary>How a run of one trigger of the path ends.</summary>
    private enum Reached
    {
        Ran,
        Halt,
        End,
        NotActive,
    }

    /// <summary>The Ran and the Effect changes of a pass, in order.</summary>
    private static IEnumerable<MachineChange> Kept(IReadOnlyList<MachineChange> changes) =>
        changes.Where(c => c.Kind is MachineChangeKind.Ran or MachineChangeKind.Effect);

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

    /// <summary>The world of a replay: no event and no fact comes, and the player is in space. The passes of the machine
    /// record the actions.</summary>
    private sealed class ReplayWorld : ITriggerWorld
    {
        public bool InSpace => true;

        public bool Matches(MissionTrigger trigger, MissionLine condition, WorldEvent worldEvent) => false;

        public bool Poll(MissionTrigger trigger, MissionLine condition) => false;

        public bool TestAtStart(MissionTrigger trigger, MissionLine condition) => false;

        public void Effect(MissionTrigger trigger, MissionLine action)
        {
        }
    }
}
