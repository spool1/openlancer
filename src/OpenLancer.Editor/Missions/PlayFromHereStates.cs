using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>The state of the Play button of one trigger card, from a pass of <see cref="PlayFromHereStates"/>.</summary>
/// <param name="CanStart">True when the plan of the trigger can start. The plan has no setup check.</param>
/// <param name="Place">The start place of the plan.</param>
/// <param name="Tip">The tooltip of the button without the line of a running play, or empty when the plan cannot
/// start.</param>
/// <param name="Reason">Why the plan cannot start, or null.</param>
internal sealed record TriggerStart(bool CanStart, StartPlace Place, string Tip, string? Reason);

/// <summary>
/// The Play buttons of the trigger cards of the open mission (note of 2026-10-27 in section 8.1 of the M6c design). For
/// each trigger, the model knows if "Play from here" can start now, and it has the texts of the button and of the node
/// menu item. One pass makes the plan of each trigger without the setup check. A pass starts for each new model of the
/// script and for each new place choice of its play start, on a worker thread. The states of the last pass of the
/// script stay until the next pass ends, so the buttons do not flicker after an edit. The class makes no ImGui call, so
/// the tests check it.
/// </summary>
/// <remarks>Each member except <see cref="Make"/> runs on the UI thread.</remarks>
/// <param name="runner">Runs a pass. Null runs it on the thread pool. The tests give a runner that runs at once, or one
/// that holds the pass.</param>
internal sealed class PlayFromHereStates(Func<Func<PlayFromHereStates.Pass>, Task<PlayFromHereStates.Pass>>? runner = null)
{
    /// <summary>The reason of a trigger whose nickname an earlier trigger has (TR4).</summary>
    public const string DuplicateReason = "Another trigger has the same nickname, and the game uses only the first one. " +
                                          "Rename this trigger, then play from it.";

    /// <summary>The last line of the tooltip while a mission play runs (D751).</summary>
    public const string PlayRunsLine = "A mission play runs. The game starts the mission again at this trigger.";

    // The kind sentences of the tooltip.
    public const string ExactStart = "The start is exact.";
    public const string ExactTriggers = "The triggers are exact.";
    public const string ApproximateStart = "Setup triggers rebuild the state of the path. Some state can differ, for example " +
                                           "damage.";

    private static readonly TriggerStart Duplicate = new(false, StartPlace.Camera(""), "", DuplicateReason);

    private readonly Func<Func<Pass>, Task<Pass>> run = runner ?? (pass => Task.Run(pass));

    // The key of the last pass that started: the script, the index of its model and the place choice of its play start.
    // The trigger of the play start is not in the key, because no plan of a trigger reads it.
    private (string Script, MissionIndex Index, string Place)? key;
    private (Task<Pass> Task, CancellationTokenSource Cancel)? running;
    private Pass? last;

    /// <summary>The result of one pass: its key and the state of each trigger that is not a duplicate, by nickname
    /// without letter case.</summary>
    internal sealed record Pass(string Script, MissionIndex Index, string Place, IReadOnlyDictionary<string, TriggerStart> States);

    /// <summary>True when the synced script has a story slot. Each trigger card then keeps the space of the button, also
    /// without a button, so the titles do not move when a pass changes the states.</summary>
    public bool KeepsSpace { get; private set; }

    /// <summary>The number of passes that started, for the tests.</summary>
    public int Passes { get; private set; }

    /// <summary>
    /// Takes the result of a pass that ended, and starts a pass when the script, its model or the place choice of its
    /// play start changed. A new pass cancels the running pass. While a drag or a typed text is open, no pass starts:
    /// the pass starts at the end of the transaction. Another script drops the states at once. The graph calls it in each
    /// frame for the open mission outside prefab mode.
    /// </summary>
    /// <param name="inTransaction">True while a drag or a typed text is open (section 4.11 of the M6b design).</param>
    public void Sync(MissionAnalysis analysis, PlayStartSetting setting, bool inTransaction)
    {
        Take();
        var script = analysis.Script.Path;
        if (last != null && !VfsPath.Comparer.Equals(last.Script, script))
            last = null;
        KeepsSpace = analysis.Script.Slot != null;
        if (inTransaction || key is var (keyScript, keyIndex, keyPlace) && VfsPath.Comparer.Equals(keyScript, script) &&
            ReferenceEquals(keyIndex, analysis.Index) && keyPlace == setting.Place)
            return;
        Cancel();
        key = (script, analysis.Index, setting.Place);
        var cancel = new CancellationTokenSource();
        var (index, slot, token) = (analysis.Index, analysis.Script.Slot, cancel.Token);
        running = (run(() => new Pass(script, index, setting.Place, Make(index, slot, setting, token))), cancel);
        Passes++;
        Take();
    }

    /// <summary>The state of a trigger, or null until a pass of the script ends. A duplicate gets the duplicate reason. It
    /// allocates nothing, so each card calls it in each frame.</summary>
    public TriggerStart? Of(MissionTrigger trigger) =>
        last == null ? null : trigger.IsDuplicate ? Duplicate : last.States.GetValueOrDefault(trigger.Nickname);

    /// <summary>Cancels the running pass and forgets the states, for example when the profile closes.</summary>
    public void Clear()
    {
        Cancel();
        key = null;
        last = null;
        KeepsSpace = false;
    }

    private void Cancel()
    {
        if (running is not var (_, cancel))
            return;
        cancel.Cancel();
        cancel.Dispose();
        running = null;
    }

    /// <summary>Takes the result of the running pass when it ended. A pass that failed gives nothing. The pass of an old
    /// key cannot come here, because a new key cancels and forgets it.</summary>
    private void Take()
    {
        if (running is not var (task, cancel) || !task.IsCompleted)
            return;
        running = null;
        cancel.Dispose();
        if (task.IsCompletedSuccessfully)
            last = task.Result;
    }

    /// <summary>
    /// The pass: the state of each trigger that is not a duplicate, in file order. Each plan uses the place choice of the
    /// play start and skips the setup check, so a pass over the biggest retail script takes a fraction of a second. The
    /// pass checks the cancel token before each trigger.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token cancelled the pass.</exception>
    internal static IReadOnlyDictionary<string, TriggerStart> Make(MissionIndex index, StorySlot? slot,
        PlayStartSetting setting, CancellationToken token)
    {
        var (place, dock) = setting.PlaceChoice;
        var states = new Dictionary<string, TriggerStart>(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in index.Triggers)
        {
            token.ThrowIfCancellationRequested();
            if (trigger.IsDuplicate)
                continue;
            var plan = StartPlan.Make(index, slot, MissionStartPoint.At(trigger.Nickname), place, dock, checkSetup: false);
            states[trigger.Nickname] = new TriggerStart(plan.CanStart, plan.Place, plan.CanStart ? Tip(plan, setting) : "",
                plan.Reason);
        }

        return states;
    }

    /// <summary>True when "Play from here" can start: the plan can start and its start place has no problem. While the
    /// first pass of the script runs, the state is null, and the node menu item stays on.</summary>
    internal static bool CanPlay(TriggerStart? start, string? placeProblem) =>
        start == null || start.CanStart && placeProblem == null;

    /// <summary>True when a card shows the Play button: a trigger card that is not a duplicate, outside prefab mode, whose
    /// plan can start at a start place without a problem.</summary>
    internal static bool Shows(GraphNode node, bool prefabMode, TriggerStart? start, string? placeProblem) =>
        !prefabMode && node.Kind == GraphNodeKind.Trigger && node.Trigger is { IsDuplicate: false } && start != null &&
        CanPlay(start, placeProblem);

    /// <summary>The tooltip of the Play button and of the node menu item "Play from here": the tooltip of the state, with
    /// the line of a running play, or the prefix of a refusal and the reason. Without a state, the tooltip of section
    /// 8.1. Only a hover makes it.</summary>
    internal static string Tooltip(TriggerStart? start, string? placeProblem, bool playRuns) =>
        start == null ? GraphView.PlayFromHereTip
        : (start.CanStart ? placeProblem : start.Reason) is { } reason ? StartPlan.RefusalPrefix + reason
        : playRuns ? $"{start.Tip}\n{PlayRunsLine}"
        : start.Tip;

    /// <summary>The tooltip of a plan that can start: the action, the start and its kind, the start place with the words
    /// of the Play start page, the source of a suggested place, and each warning of the plan.</summary>
    internal static string Tip(StartPlan plan, PlayStartSetting setting)
    {
        var kind = plan.Kind switch
        {
            StartKind.MissionStart => ExactStart,
            StartKind.Checkpoint => ExactTriggers,
            _ => ApproximateStart,
        };
        var lines = new List<string>
        {
            GraphView.PlayFromHereTip,
            $"Start: {plan.StartName}. {kind}",
            $"Start place: {PlayStartPageModel.PlaceTextOf(setting, plan)}",
        };
        if (setting.PlaceChoice.Place == StartPlaceChoice.Suggested)
            lines.Add($"Place source: {plan.Place.Source}");
        foreach (var warning in plan.Warnings)
            lines.Add($"{EditorIcons.ExclamationTriangle}  {warning}");
        return string.Join("\n", lines);
    }
}
