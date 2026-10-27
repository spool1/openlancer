using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>The state of the Play button of one trigger node, from a pass of <see cref="PlayFromHereStates"/>.</summary>
/// <param name="CanStart">True when the plan of the trigger can start. The plan has no setup check.</param>
/// <param name="Place">The start place of the plan. Equal places of one pass are one object.</param>
/// <param name="Tip">The tooltip of the button without the line of a running play, or empty when the plan cannot
/// start.</param>
/// <param name="Reason">Why the plan cannot start, or null.</param>
internal sealed record TriggerStart(bool CanStart, StartPlace Place, string Tip, string? Reason);

/// <summary>
/// The Play buttons of the trigger nodes of the open mission (note of 2026-10-27 in section 8.1 of the M6c design). For
/// each trigger, the model knows if "Play from here" can start now, and it has the texts of the button and of the node
/// menu item. One pass makes the plan of each trigger without the setup check. A pass starts for each new model of the
/// script and for each new place choice of its play start, on a worker thread. The states of the last pass of the
/// script stay until the next pass ends, so the buttons do not flicker after an edit. A new place choice drops them at
/// once, because each state holds a start place of the old choice. The model also keeps the click of a button until
/// the graph plays it after the canvas. The class makes no ImGui call, so the tests check it.
/// </summary>
/// <remarks>Each member except <see cref="Make"/> runs on the UI thread.</remarks>
/// <param name="placeProblem">Why a mission play cannot start at a start place now, or null (see
/// <see cref="MissionPlay.PlaceProblem"/>). The model asks once for each place in a frame.</param>
/// <param name="failed">Writes the Console line of a pass that failed.</param>
/// <param name="runner">Runs a pass with its cancel token. Null runs it on the thread pool. The tests give a runner that
/// runs at once, or one that holds the pass.</param>
internal sealed class PlayFromHereStates(
    Func<StartPlace, string?> placeProblem,
    Action<string> failed,
    Func<Func<PlayFromHereStates.Pass>, CancellationToken, Task<PlayFromHereStates.Pass>>? runner = null)
{
    /// <summary>The reason of a trigger whose nickname an earlier trigger has (TR4).</summary>
    public const string DuplicateReason = "Another trigger has the same nickname, and the game uses only the first one. " +
                                          "Rename this trigger, then play from it.";

    /// <summary>The last line of the tooltip while a mission play runs (D751). The play can be of another mission, so
    /// the line does not name the mission.</summary>
    public const string PlayRunsLine = "A mission play runs. The game starts again at this trigger.";

    // The kind sentences of the tooltip.
    public const string ExactStart = "The start is exact.";
    public const string ExactTriggers = "The state of the triggers is exact.";
    public const string ApproximateStart = "Setup triggers rebuild the state of the path. Some state can differ, for example " +
                                           "damage.";

    private static readonly TriggerStart Duplicate = new(false, StartPlace.Camera(""), "", DuplicateReason);

    private readonly Func<Func<Pass>, CancellationToken, Task<Pass>> run =
        runner ?? ((pass, token) => Task.Run(pass, token));

    // The key of the last pass that started: the script, the index of its model and the place choice of its play start.
    // The trigger of the play start is not in the key, because no plan of a trigger reads it.
    private (string Script, MissionIndex Index, string Place)? key;
    private (Task<Pass> Task, CancellationTokenSource Cancel)? running;
    private Pass? last;

    // The start place problems of this frame, by place object. Sync clears them, because the open system of the Scene
    // and the game data can change between two frames.
    private readonly Dictionary<StartPlace, string?> problems = new(ReferenceEqualityComparer.Instance);

    // The trigger of a click on a Play button in this frame, or null.
    private MissionTrigger? clicked;

    /// <summary>The result of one pass: its key and the state of each trigger that is not a duplicate, by nickname
    /// without letter case.</summary>
    internal sealed record Pass(string Script, MissionIndex Index, string Place, IReadOnlyDictionary<string, TriggerStart> States);

    /// <summary>True while the graph shows the open mission outside prefab mode, and its script has a story slot. Each
    /// trigger node then keeps the space of the button, also without a button, so the titles do not move when a pass
    /// changes the states.</summary>
    public bool KeepsSpace { get; private set; }

    /// <summary>The number of passes that started, for the tests.</summary>
    public int Passes { get; private set; }

    /// <summary>
    /// The graph calls it at the start of each frame. It takes the result of a pass that ended, and starts a pass when
    /// the script, its model or the place choice of its play start changed. A new pass cancels the running pass. While a
    /// drag or a typed text is open, no pass starts: the pass starts at the end of the transaction. Another script and
    /// another place choice drop the states at once.
    /// </summary>
    /// <param name="open">The analysis of the open mission while the graph shows it outside prefab mode, else null. With
    /// null, no node shows a button, and the states stay for the return to the mission.</param>
    /// <param name="inTransaction">True while a drag or a typed text is open (section 4.11 of the M6b design).</param>
    public void Sync(MissionAnalysis? open, PlayStartSetting setting, bool inTransaction)
    {
        Take();
        problems.Clear();
        clicked = null;
        KeepsSpace = open?.Script.Slot != null;
        if (open == null)
            return;
        var script = open.Script.Path;
        if (last != null && (!VfsPath.Comparer.Equals(last.Script, script) || last.Place != setting.Place))
            last = null;
        if (inTransaction || key is var (keyScript, keyIndex, keyPlace) && VfsPath.Comparer.Equals(keyScript, script) &&
            ReferenceEquals(keyIndex, open.Index) && keyPlace == setting.Place)
            return;
        Cancel();
        key = (script, open.Index, setting.Place);
        var cancel = new CancellationTokenSource();
        var (index, slot, token) = (open.Index, open.Script.Slot, cancel.Token);
        running = (run(() => new Pass(script, index, setting.Place, Make(index, slot, setting, token)), token), cancel);
        Passes++;
        Take();
    }

    /// <summary>The state of a trigger, or null until a pass of the script ends. A duplicate gets the duplicate reason. It
    /// allocates nothing.</summary>
    internal TriggerStart? Of(MissionTrigger trigger) =>
        last == null ? null : trigger.IsDuplicate ? Duplicate : last.States.GetValueOrDefault(trigger.Nickname);

    /// <summary>True when a node shows the Play button (rules P1 and P2): a trigger node of the open mission outside
    /// prefab mode that is not a duplicate, and whose plan can start at a start place without a problem. After the first
    /// read of each place in a frame it allocates nothing, so each node calls it in each frame.</summary>
    public bool Shows(GraphNode node) =>
        KeepsSpace && node is { Kind: GraphNodeKind.Trigger, Trigger: { } trigger } &&
        Of(trigger) is { CanStart: true } start && ProblemOf(start.Place) == null;

    /// <summary>True when "Play from here" in the node menu is on: the plan can start and its start place has no
    /// problem. While the first pass of the script runs, the state is not known, and the item stays on (rule P6).</summary>
    public bool CanPlay(MissionTrigger trigger) =>
        Of(trigger) is not { } start || start.CanStart && ProblemOf(start.Place) == null;

    /// <summary>The tooltip of the Play button and of the node menu item "Play from here": the tooltip of the state with
    /// the line of a running play, or the prefix of a refusal and the reason. Without a state, the tooltip of section
    /// 8.1. Only a hover makes it.</summary>
    public string Tooltip(MissionTrigger trigger, bool playRuns) =>
        Of(trigger) is not { } start ? GraphView.PlayFromHereTip
        : (start.CanStart ? ProblemOf(start.Place) : start.Reason) is { } reason ? StartPlan.RefusalPrefix + reason
        : playRuns ? $"{start.Tip}\n{PlayRunsLine}"
        : start.Tip;

    /// <summary>A click on the Play button of a node (rule P4). The release that ends a double-click does not count, so a
    /// double-click plays once and does not start the game a second time.</summary>
    /// <param name="clicks">The number of clicks in a row of the release: 1 for a click, 2 for the second click of a
    /// double-click.</param>
    public void Click(MissionTrigger trigger, int clicks)
    {
        if (clicks < 2)
            clicked = trigger;
    }

    /// <summary>"Play from here" for the trigger of the click of this frame: it sets the play start and plays. The graph
    /// calls it after the canvas, so the request runs outside the node editor. Returns the request, or null without a
    /// click.</summary>
    public Task<PlayRequestResult>? PlayClick(MissionPlay plays, string script)
    {
        if (clicked is not { } trigger)
            return null;
        clicked = null;
        return plays.PlayFromHere(script, trigger.Nickname);
    }

    /// <summary>Cancels the running pass and forgets the states and the click, for example when the profile
    /// closes.</summary>
    public void Clear()
    {
        Cancel();
        key = null;
        last = null;
        clicked = null;
        problems.Clear();
        KeepsSpace = false;
    }

    /// <summary>The Console line of a pass that failed.</summary>
    internal static string FailedLine(string message) =>
        $"The editor could not find the triggers that Play from here can start: {message} No node shows a Play button " +
        "until the next edit.";

    /// <summary>Why a mission play cannot start at a start place now, or null. The places of a pass are shared, so a
    /// frame asks once for each place and makes few texts, also when each place has a problem.</summary>
    private string? ProblemOf(StartPlace place)
    {
        if (!problems.TryGetValue(place, out var problem))
            problems[place] = problem = placeProblem(place);
        return problem;
    }

    private void Cancel()
    {
        if (running is not var (_, cancel))
            return;
        cancel.Cancel();
        cancel.Dispose();
        running = null;
    }

    /// <summary>Takes the result of the running pass when it ended. The pass of an old key cannot come here, because a
    /// new key cancels and forgets it. A pass that failed leaves no states, so no node shows a button of an old model,
    /// and the Console gives the reason.</summary>
    private void Take()
    {
        if (running is not var (task, cancel) || !task.IsCompleted)
            return;
        running = null;
        cancel.Dispose();
        if (task.IsCompletedSuccessfully)
        {
            last = task.Result;
            return;
        }

        last = null;
        if (task.Exception?.GetBaseException() is { } error)
            failed(FailedLine(error.Message));
    }

    /// <summary>
    /// The pass: the state of each trigger that is not a duplicate, in file order. Each plan uses the place choice of the
    /// play start and skips the setup check, so a pass over the biggest retail script takes a fraction of a second. The
    /// pass checks the cancel token before each trigger. Equal start places become one object.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token cancelled the pass.</exception>
    internal static IReadOnlyDictionary<string, TriggerStart> Make(MissionIndex index, StorySlot? slot,
        PlayStartSetting setting, CancellationToken token)
    {
        var (place, dock) = setting.PlaceChoice;
        var states = new Dictionary<string, TriggerStart>(StringComparer.OrdinalIgnoreCase);
        var places = new Dictionary<StartPlace, StartPlace>();
        foreach (var trigger in index.Triggers)
        {
            token.ThrowIfCancellationRequested();
            if (trigger.IsDuplicate)
                continue;
            var plan = StartPlan.Make(index, slot, MissionStartPoint.At(trigger.Nickname), place, dock, checkSetup: false);
            if (!places.TryGetValue(plan.Place, out var shared))
                places[plan.Place] = shared = plan.Place;
            states[trigger.Nickname] = new TriggerStart(plan.CanStart, shared, plan.CanStart ? Tip(plan, setting) : "",
                plan.Reason);
        }

        return states;
    }

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
