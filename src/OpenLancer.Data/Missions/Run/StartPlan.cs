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
/// <param name="Source">Where the place comes from, in plain words, for example "Act_MovePlayer of t_wave.".</param>
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
        var root = path[0];
        var run = new Replayer(index, chosen, root);
        var replay = new StartReplay { Kind = StartKind.Approximate, Chosen = chosen, Root = root, Path = path };
        // Each trigger of the path runs in order: a trigger that ran goes on, a pending trigger starts at the next
        // heartbeat, and an active trigger runs as if its condition is met now.
        for (var k = 0; k < path.Count; k++)
        {
            var trigger = path[k];
            if (run.Machine.Halt is { Kind: MachineHaltKind.StopPoint })
                break;
            var rest = path.Skip(k).Append(chosen).ToList();
            run.Objectives(trigger, rest);
            run.BeforeInSpace(trigger, rest);
            run.Systems(trigger, rest);
            run.Launch(trigger, rest);
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
            run.InSpace(trigger, path.Skip(k + 1).Append(chosen).ToList());
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
