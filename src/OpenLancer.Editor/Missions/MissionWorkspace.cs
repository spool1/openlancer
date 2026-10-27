using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;
using OpenLancer.Runtime.Missions;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Missions;

/// <summary>A field that waits for "Pick in Scene": the next click in the Scene sets its position.</summary>
/// <param name="Key">The key of the field on its page, so two fields with one label do not both wait.</param>
/// <param name="Label">The label of the field.</param>
/// <param name="System">The system of the item of the field, or null when the item names no system.</param>
/// <param name="Owner">The selection of the page of the field. A pick ends when the selection changes.</param>
/// <param name="Set">Sets the position.</param>
internal sealed record ScenePick(string Key, string Label, string? System, object Owner,
    Action<System.Numerics.Vector3> Set)
{
    /// <summary>The hint in the Scene while the pick waits (section 8.11 of the M6b design).</summary>
    public string Hint => $"Click a point in the Scene to set {Label}. Esc cancels.";

    /// <summary>The hint in the Scene while it shows another system than the system of the item.</summary>
    public string OtherSystemHint(string shown) =>
        $"{Label} is a position in {System}. The Scene shows {shown}. Open {System} to pick a point. Esc cancels.";
}

/// <summary>
/// The mission scripts of the open profile and the open mission (section 4 of the M6a design): the last run of the
/// mission checks, the open mission, the selection of the Mission tab, and the requests of the raw view and of the
/// Mission page. One mission is open at a time. The edits of mission files go through the mission editor of the session
/// (section 4 of the M6b design).
/// </summary>
/// <remarks>
/// <para>A run of the checks starts after each load of the game data. The UI thread makes the snapshot, a worker task runs
/// the checks, and the UI thread takes the result. A run that a newer run replaced drops its result and its error
/// (section 12.3 of the M6a design). The mission editor takes the analyses of the run into its model cache.</para>
/// <para>After each change of a script, also an Undo or a Redo, the result takes the model of the script from the
/// mission editor, and the Console gets the problems again (section 6.11 of the M6b design). A new or deleted script, and
/// a change of a ship build file, start a new run.</para>
/// <para>The raw text of a script needs a parse of each section for the round trip check. A worker task makes it at the
/// first use. So the open of a mission does not stop the UI thread.</para>
/// </remarks>
internal sealed class MissionWorkspace
{
    /// <summary>The id of the Mission tab.</summary>
    public const string PanelId = "mission";

    private readonly EditorWindow window;
    private readonly SavePrefabDialog savePrefab;

    public MissionWorkspace(EditorWindow window)
    {
        this.window = window;
        savePrefab = new SavePrefabDialog(Run);
        DryRuns = new MissionDryRuns(Clock);
        Live = new MissionLive(Clock);
        Plays = new MissionPlay(window);
    }

    /// <summary>The clock of the dry run and the live view, in seconds.</summary>
    private static double Clock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // ---- Play from a trigger and the live view (sections 5 and 6 of the M6c design) ----

    /// <summary>The mission play request and the play starts of the scripts.</summary>
    public MissionPlay Plays { get; }

    /// <summary>The Play buttons of the trigger cards of the open mission (note of 2026-10-27 in section 8.1 of the M6c
    /// design).</summary>
    public PlayFromHereStates PlayHere { get; } = new();

    /// <summary>The live trace of the mission of the OpenLancer game.</summary>
    public MissionLive Live { get; }

    /// <summary>The source of the stall notes in the Console (section 6.5 of the M6c design).</summary>
    public const string RunSource = "Mission run";

    // The overlay of the live trace, and the time of the last check of the stalls.
    private (MissionGraph Graph, int Version, long Tick, RunOverlay Overlay)? liveOverlay;
    private double stallCheck = double.NegativeInfinity;

    /// <summary>True while a mission play of OpenLancer runs.</summary>
    public bool MissionPlayRuns => window.Play.Mission != null && window.Play.Status != PlayStatus.Idle;

    /// <summary>The script of the mission of the live trace, by its story slot, or null for a mission without a story
    /// slot.</summary>
    public string? LiveScript => Live.Mission is { } mission
        ? Result?.Scripts.FirstOrDefault(s => string.Equals(s.Slot?.Controller, mission, StringComparison.OrdinalIgnoreCase))?.Path
        : null;

    /// <summary>The live trace of a script while the graph shows it (section 6.3 of the M6c design): the game runs or ran
    /// the mission of the script, and the modder did not clear the trace. After the exit of the game, the dry run comes
    /// first while its toggle is on. Else null.</summary>
    public MissionTrace? ShownLive(string script) =>
        !ShowRaw && (MissionPlayRuns || !DryRuns.On) && Live.Mission != null &&
        string.Equals(LiveScript, script, StringComparison.OrdinalIgnoreCase)
            ? Live.Trace
            : null;

    /// <summary>
    /// The live trace query of the shared command layer (section 7.2 of the M6c design): the play number, the changes of
    /// the play after a sequence number, and the Play status. A request with another play number gets all changes of
    /// the current play. While only the game of a play copy runs, the query gives no changes, because the real game has
    /// no channel to the editor.
    /// </summary>
    /// <param name="play">The play number of the last result of the caller, or 0.</param>
    /// <param name="since">The last sequence number that the caller has, or 0.</param>
    public LiveTraceResult LiveTrace(int play, long since) =>
        Live.Result(play, since, window.Play.Status, window.GamePlay.GameRuns, LiveScript);

    /// <summary>The overlay of the live trace on a graph, with the chips "Rules differ" while a mission play runs. It
    /// stays for a quarter of a second, so the flashes and the stalls follow the time.</summary>
    public RunOverlay LiveOverlay(MissionGraph graph)
    {
        var tick = (long)(Live.Now * 4);
        if (liveOverlay is var (g, v, t, known) && ReferenceEquals(g, graph) && v == Live.Version && t == tick)
            return known;
        var made = RunOverlay.Of(graph, Live.Trace, Live.Now, MissionPlayRuns ? EngineCoverage.Instance : null);
        liveOverlay = (graph, Live.Version, tick, made);
        return made;
    }

    /// <summary>
    /// Takes a mission event of the game (section 6.3 of the M6c design). The Console gets the end note of a trigger that
    /// ends the mission (section 6.5). The trigger changes go to the Run log only.
    /// </summary>
    public void MissionChanged(PlayEvent.MissionChanges changes)
    {
        Live.Take(changes, window.Play.LoadingMission?.SetupTriggers);
        if (LiveScript is not { } script || Analysis(script) is not { } analysis)
            return;
        foreach (var note in LiveNotes.Ends(analysis, changes))
            window.Log.Add(ConsoleSeverity.Info, PlayController.Source, note);
    }

    /// <summary>
    /// The stall notes of the live view (section 6.5 of the M6c design): a trigger that waits for 10 minutes gives one
    /// warning in each play. The origin is the base game when no mod changes the script, else the last mod that changes
    /// it (decision D551 of the M6a design). The Mission tab calls it in each frame, and it looks once a second.
    /// </summary>
    public void CheckStalls()
    {
        var now = Clock();
        if (!MissionPlayRuns || now - stallCheck < 1)
            return;
        stallCheck = now;
        if (LiveScript is not { } script || Analysis(script) is not { } analysis)
            return;
        var origin = LiveNotes.StallOrigin(analysis.Script);
        foreach (var name in Live.NewStalls())
        {
            if (analysis.Index.Trigger(name) is { } trigger)
                window.Log.Add(ConsoleSeverity.Warning, RunSource, LiveNotes.Stall(analysis, trigger), trigger.Section.Ref,
                    origin: origin);
        }
    }

    // ---- The dry run (section 4 of the M6c design) ----

    /// <summary>The dry runs of the scripts of the session, the toggles "Dry run" and "Follow", and the fold after each
    /// new model of a script.</summary>
    public MissionDryRuns DryRuns { get; }

    /// <summary>The dry run of the open mission while the toggle "Dry run" is on, else null. Prefab mode has no dry
    /// run.</summary>
    public DryRun? DryRun =>
        DryRuns.On && !MissionPlayRuns && OpenPrefab == null && Open is { } analysis ? DryRuns.For(analysis) : null;

    /// <summary>The trace of the dry run of a script while the graph shows the run (section 4.5 of the M6c design): the
    /// toggle "Dry run" is on, the script is the open mission in the graph view, and the run started. Else null. The
    /// Trigger page shows the section "Run" only then.</summary>
    public MissionTrace? ShownRun(string script) =>
        ShownLive(script) ??
        (!ShowRaw && DryRun is { Started: true } run && string.Equals(OpenPath, script, StringComparison.OrdinalIgnoreCase)
            ? run.State.Trace
            : null);

    /// <summary>Folds the dry run of the open mission again when its model changed, for example after an edit, an Undo
    /// or a Redo. The Mission tab calls it in each frame.</summary>
    public void SyncDryRun()
    {
        if (DryRuns.On && OpenPrefab == null && Open is { } analysis && window.Session?.Edits is { } edits)
            DryRuns.Sync(analysis, edits.InTransaction);
    }

    /// <summary>Adds an input to the dry run of the open mission.</summary>
    public void AddDryRunInput(DryRunInput input)
    {
        if (DryRun != null && Open is { } analysis)
            DryRuns.Add(analysis, input);
    }

    /// <summary>The id of the Missions tab.</summary>
    public const string BrowserId = "missions";

    private static readonly Lazy<IniVocabulary> MissionVocabulary = new(() => new IniVocabulary(
        MissionSchema.Retail.Conditions.Concat(MissionSchema.Retail.Actions).Select(n => n.Name).ToList(),
        MissionSchema.Retail.Sections.SelectMany(s => s.Keys).Select(k => k.Key)
            .Concat(MissionSchema.Retail.Directives.Select(d => d.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

    private readonly Dictionary<string, Task<RawText>> rawTexts = new(VfsPath.Comparer);
    private readonly Dictionary<string, (RawText Text, IReadOnlyList<RawMark> Marks)> marks = new(VfsPath.Comparer);
    private readonly Dictionary<string, MissionRawDraft> drafts = new(StringComparer.OrdinalIgnoreCase);
    private bool marksShowBaseGame;
    private int latestRun;

    // The scripts that changed while the last run of the checks ran. Its result is older for them.
    private readonly HashSet<string> changedDuringRun = new(VfsPath.Comparer);

    // The edit session whose changes the workspace follows.
    private EditSession? followed;

    /// <summary>The names that the raw INI editors of mission scripts color: the conditions and actions, and the keys and
    /// orders of the section types.</summary>
    public static IniVocabulary Vocabulary => MissionVocabulary.Value;

    /// <summary>The result of the last run of the mission checks, or null before the first run.</summary>
    public MissionRunResult? Result { get; private set; }

    /// <summary>The install path of the open mission, or null.</summary>
    public string? OpenPath { get; private set; }

    /// <summary>The analysis of the open mission, or null.</summary>
    public MissionAnalysis? Open => OpenPath != null ? Result?.Of(OpenPath) : null;

    /// <summary>The selection of the open mission when nothing in it is selected, or null. In prefab mode, it is the
    /// Prefab page of the open prefab.</summary>
    public MissionRef? Root => OpenPrefab != null ? MissionRef.Of(OpenPrefab.Script)
        : OpenPath != null ? MissionRef.Of(OpenPath)
        : null;

    // ---- Prefab mode (section 8.6 of the M6b design) ----

    // The selection of the Mission tab before prefab mode opened. It comes back when prefab mode closes.
    private object? selectionBeforePrefab;

    /// <summary>The prefab that prefab mode shows, or null while the Mission tab shows the open mission.</summary>
    public PrefabRef? OpenPrefab { get; private set; }

    /// <summary>The prefab of prefab mode now: its asset, its template and the analysis of its template sections. Null
    /// outside prefab mode, and when no mod has the prefab now.</summary>
    public PrefabModeModel? PrefabMode =>
        OpenPrefab is { } prefab ? window.Session?.Edits.Missions.PrefabMode(prefab) : null;

    /// <summary>The analysis that the Mission tab, the Hierarchy and the graph show: the template of prefab mode, or the
    /// open mission.</summary>
    public MissionAnalysis? Shown => OpenPrefab != null ? PrefabMode?.Analysis : Open;

    /// <summary>The script that the Mission tab shows: the template of prefab mode, or the open mission.</summary>
    public string? ShownPath => OpenPrefab?.Script ?? OpenPath;

    /// <summary>
    /// Opens a prefab in prefab mode (section 8.6 of the M6b design): the graph shows its template sections, the Scene
    /// part closes, and the Inspector shows its Prefab page. Esc or the back arrow leaves prefab mode, and the selection
    /// of the mission comes back. A built-in prefab and a prefab of a lower mod open read-only.
    /// </summary>
    public void OpenPrefabMode(PrefabRef prefab)
    {
        if (window.Session?.Edits.Missions.PrefabMode(prefab) is not { } model)
        {
            window.Log.Add(ConsoleSeverity.Warning, MissionChecks.Source, $"No mod has the prefab '{prefab.Id}'.");
            return;
        }

        if (model.Analysis == null)
        {
            window.Log.Add(ConsoleSeverity.Warning, MissionChecks.Source,
                $"The file of prefab '{model.Asset.Name}' has a problem, so prefab mode cannot open it. " +
                string.Join(" ", model.Asset.Problems.Select(p => p.ToString())));
            return;
        }

        if (OpenPrefab == null)
        {
            selectionBeforePrefab = window.Selection;
        }

        OpenPrefab = prefab;
        ShowRaw = false;
        window.SelectInTab(CenterMode.Mission, Root!);
        window.ShowPanel(PanelId);
    }

    /// <summary>Leaves prefab mode. The selection of the mission before prefab mode comes back.</summary>
    public void LeavePrefabMode()
    {
        if (OpenPrefab == null)
        {
            return;
        }

        OpenPrefab = null;
        var back = Valid(selectionBeforePrefab) ?? Root;
        selectionBeforePrefab = null;
        if (back != null)
        {
            window.SelectInTab(CenterMode.Mission, back);
        }
    }

    /// <summary>Goes to a link of the prefab pages and of the frame menu of the graph (section 8.11 of the M6b design):
    /// prefab mode, the asset in the Assets panel, or the dialog "Save as prefab". Returns false for another
    /// link.</summary>
    public bool Follow(object link)
    {
        switch (link)
        {
            case PrefabModeLink prefab:
                OpenPrefabMode(prefab.Prefab);
                return true;
            case PrefabAssetLink asset:
                window.Assets.ShowPrefab(asset.Prefab);
                return true;
            case SavePrefabLink save:
                AskSaveAsPrefab(save.Instance, save.Name, save.Id);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Runs the batch of an action of a menu of the studio, or goes to its link (see <see cref="Follow"/>).</summary>
    public void Do(PageAction action)
    {
        if (action.Batch != null)
        {
            Run(action.Batch);
        }
        else if (action.Link != null)
        {
            Follow(action.Link);
        }
    }

    /// <summary>Opens the dialog "Save as prefab" of a matched instance (section 5.9 of the M6b design).</summary>
    public void AskSaveAsPrefab(InstanceRef instance, string name, string id) => savePrefab.Show(instance, name, id);

    /// <summary>True while the dialog "Save as prefab" is open.</summary>
    public bool DialogOpen => savePrefab.IsOpen;

    /// <summary>Draws the dialogs of the mission studio. The window calls it in each frame.</summary>
    public void DrawDialogs() => savePrefab.Draw();

    /// <summary>A line of a script that the raw view goes to when its text shows, or null. It is a section and the index
    /// of a line, or -1 for the header.</summary>
    public (SectionRef Target, int Entry)? PendingLine { get; set; }

    /// <summary>The key of a section of the Mission page that the page opens and scrolls to at its next draw, or
    /// null.</summary>
    public (string Script, string Section)? PendingSection { get; set; }

    /// <summary>True while the Mission tab shows the raw INI of the whole script in place of the graph. The view switch
    /// "Graph" and "Raw INI" sets it (section 11.5 of the M6a design).</summary>
    public bool ShowRaw { get; set; }

    /// <summary>True when the graph moves its view to the selection at its next draw. An example is a click on a
    /// Console message (section 12.4 of the M6a design).</summary>
    public bool PendingGraphFrame { get; set; }

    /// <summary>The item whose world items the Scene part frames at its next draw, or null (section 7.3 of the M6a
    /// design).</summary>
    public MissionRef? PendingSceneFrame { get; set; }

    /// <summary>Asks the Scene part to frame the world items of a mission item, for example after a double-click on a
    /// node.</summary>
    public void FrameInScene(MissionRef item) => PendingSceneFrame = item;

    // The acts of the graph file of each script, with the analysis and the graph file that gave them. Each script has
    // its own entry, so a read of another script does not make new acts (review finding 8 of M6b-2).
    private readonly Dictionary<string, (MissionAnalysis Analysis, GraphFile File, MissionActs? Acts)> acts =
        new(VfsPath.Comparer);

    /// <summary>The records of the graph file of a script in W (section 6.8 of the M6b design), or
    /// <see cref="GraphFile.Empty"/>. The same file gives the same record.</summary>
    public GraphFile GraphOf(string path) => window.Session?.Edits.Missions.Graph(path) ?? GraphFile.Empty;

    /// <summary>The acts of the graph file of an analysis after the first act edit, or null for the proposed acts
    /// (decision D679). The graph, the Hierarchy and the Act page use them.</summary>
    public MissionActs? ActsOf(MissionAnalysis analysis)
    {
        var path = analysis.Script.Path;
        var file = GraphOf(path);
        if (acts.TryGetValue(path, out var known) && ReferenceEquals(known.Analysis, analysis) &&
            ReferenceEquals(known.File, file))
        {
            return known.Acts;
        }

        var made = file.Acts.Count > 0 ? MissionActs.FromFile(analysis.Index, file.Acts) : null;
        acts[path] = (analysis, file, made);
        return made;
    }

    private ScenePick? pendingPick;

    /// <summary>The field that waits for "Pick in Scene", or null (section 8.9 of the M6b design). A pick ends when the
    /// selection changes, for example to another item or another mission.</summary>
    public ScenePick? PendingPick
    {
        get
        {
            if (pendingPick != null && !Equals(pendingPick.Owner, window.Selection))
            {
                pendingPick = null;
            }

            return pendingPick;
        }
    }

    /// <summary>Starts "Pick in Scene": the next click in the Scene sets the position of a field. Esc cancels.</summary>
    public void StartPick(ScenePick pick) => pendingPick = pick;

    /// <summary>Ends "Pick in Scene" without a change.</summary>
    public void CancelPick() => pendingPick = null;

    /// <summary>Ends "Pick in Scene" with a point: the field gets it.</summary>
    public void Picked(System.Numerics.Vector3 point)
    {
        var pick = PendingPick;
        pendingPick = null;
        pick?.Set(point);
    }

    // The systems that the user picked for items whose system the index cannot find. The Hierarchy menu "Show in
    // system..." picks them (section 9 of the M6a design). The choice lasts until the editor closes and writes nothing.
    private readonly Dictionary<SectionRef, string> systemOverrides = [];

    /// <summary>A number that changes with each choice of "Show in system...", so the mission layer reads its items
    /// again.</summary>
    public int OverridesVersion { get; private set; }

    /// <summary>The system that the user picked for an item, or null.</summary>
    public string? SystemOverride(SectionRef section) => systemOverrides.GetValueOrDefault(section);

    /// <summary>Shows an item of the group "System unknown" in a system, for the session only.</summary>
    public void ShowInSystem(SectionRef section, string system)
    {
        systemOverrides[section] = system;
        OverridesVersion++;
    }

    /// <summary>True while the base-game messages show (decision D527). One setting for the Console and the mission
    /// views.</summary>
    public bool ShowBaseGame => window.Settings.ShowBaseGameMessages;

    /// <summary>Starts a run of the mission checks for the open profile.</summary>
    public void Run()
    {
        if (window.Session is not { } current)
        {
            return;
        }

        MissionInputs inputs;
        try
        {
            Follow(current.Edits);
            current.Edits.Missions.Coverage = EngineCoverage.Instance;
            inputs = current.Edits.Missions.RunInputs();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, $"The mission checks did not start: {e.Message}");
            return;
        }

        latestRun = inputs.Generation;
        changedDuringRun.Clear();
        Task.Run(() =>
        {
            MissionRunResult? result = null;
            string? error = null;
            try
            {
                result = MissionRun.Execute(inputs);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                error = e.Message;
            }

            window.QueueUIThread(() => Done(current, inputs, result, error));
        });
    }

    /// <summary>Takes the result of a run on the UI thread. A result or an error of a closed session or of a replaced
    /// run is dropped.</summary>
    private void Done(EditorSession current, MissionInputs inputs, MissionRunResult? result, string? error)
    {
        if (window.Session != current || inputs.Generation != latestRun)
        {
            return;
        }

        if (result == null)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, $"The mission checks stopped: {error}");
            return;
        }

        if (!current.Edits.TakeMissionRun(result))
        {
            return;
        }

        var missions = current.Edits.Missions;
        missions.TakeRun(inputs, result);
        if (WithNewerModels(result, changedDuringRun, missions.ScriptPaths(), p => missions.Model(p).Analysis) is not { } newer)
        {
            // A new or deleted script needs the rows of a new run.
            Run();
            return;
        }

        changedDuringRun.Clear();
        Result = Linked(newer, missions);
        rawTexts.Clear();
        marks.Clear();
        window.Log.ReplaceSet(MissionChecks.Source, Result.Problems.ToList());
    }

    /// <summary>
    /// The result of a run with the model of each script that changed while the run ran (section 6.11 of the M6b
    /// design). That model is newer than the analysis of the run. Null when such a script was added or deleted: then a
    /// new run must give the rows of the Missions browser.
    /// </summary>
    /// <param name="scripts">The scripts of W now.</param>
    /// <param name="model">Gives the analysis of the model of a script now.</param>
    internal static MissionRunResult? WithNewerModels(MissionRunResult result, IReadOnlyCollection<string> changed,
        IReadOnlyList<string> scripts, Func<string, MissionAnalysis> model)
    {
        if (changed.Any(p => result.IsScript(p) != scripts.Contains(p, VfsPath.Comparer)))
        {
            return null;
        }

        var analyses = new Dictionary<string, MissionAnalysis>(result.Analyses, VfsPath.Comparer);
        foreach (var path in changed.Where(result.IsScript))
        {
            analyses[path] = model(path);
        }

        return result with { Analyses = analyses };
    }

    /// <summary>Follows the changes of the mission scripts of a session.</summary>
    private void Follow(EditSession edits)
    {
        if (ReferenceEquals(followed, edits))
        {
            return;
        }

        Unfollow();
        edits.Missions.ScriptsChanged += ScriptsChanged;
        edits.Changed += FilesChanged;
        followed = edits;
    }

    private void Unfollow()
    {
        if (followed != null)
        {
            followed.Missions.ScriptsChanged -= ScriptsChanged;
            followed.Changed -= FilesChanged;
            followed = null;
        }
    }

    /// <summary>Takes the model of each changed script into the result, and gives the Console its problems
    /// again.</summary>
    private void ScriptsChanged(MissionScriptsChange change)
    {
        // The dry run moves its inputs with the renames at its next fold (section 4.2 of the M6c design). The play start
        // follows the rename, also its Undo and its Redo (section 5.2).
        DryRuns.ScriptsChanged(change);
        Plays.Renamed(change);
        changedDuringRun.UnionWith(change.Versions.Keys);
        if (Result is not { } result || followed is not { } edits)
        {
            return;
        }

        var scripts = edits.Missions.ScriptPaths();
        var analyses = new Dictionary<string, MissionAnalysis>(result.Analyses, VfsPath.Comparer);
        foreach (var path in change.Versions.Keys)
        {
            rawTexts.Remove(path);
            marks.Remove(path);
            if (!result.IsScript(path) || !scripts.Contains(path, VfsPath.Comparer))
            {
                // The browser rows come from a run, so a new or deleted script needs one.
                Run();
                return;
            }

            analyses[path] = edits.Missions.Model(path).Analysis;
        }

        Result = result with { Analyses = analyses };
        window.Log.ReplaceSet(MissionChecks.Source, Result.Problems.ToList());
        // A rename, also its Undo and its Redo, keeps the selection on the renamed trigger (section 8.10 of the M6b
        // design).
        if (Followed(window.Selection, change.Renames.Select(r => (r.From.ToString(), r.To.ToString())).ToList(), []) is
            { } next)
        {
            window.Selection = next;
        }
    }

    /// <summary>Starts a run after a change of a ship build file, because the run reads the ship builds of all
    /// scripts. A change of a graph file or of a prefab asset links the prefab instances of each script again, and the
    /// Console gets the warnings P01 to P03 again (section 5 of the M6b design).</summary>
    private void FilesChanged(EditChange change)
    {
        if (followed is not { } edits)
        {
            return;
        }

        if (change.Files.Any(f => !IsScript(f) && edits.Missions.IsMissionFile(f)))
        {
            Run();
            return;
        }

        if (change.ModFiles.Count > 0 && Result is { } result)
        {
            Result = Linked(result, edits.Missions);
            window.Log.ReplaceSet(MissionChecks.Source, Result.Problems.ToList());
        }
    }

    /// <summary>A run result with the linked prefab instances of the graph files of the active mod in each analysis
    /// (section 5.1 of the M6b design).</summary>
    internal static MissionRunResult Linked(MissionRunResult result, MissionEditor missions) =>
        result with
        {
            Analyses = result.Analyses.ToDictionary(p => p.Key, p => missions.Linked(p.Value), VfsPath.Comparer),
        };

    /// <summary>
    /// Runs a raw edit of the whole text of a mission file, or of the INI block of a page (section 8.8 of the M6b
    /// design). A problem keeps nothing: the Console line holds the problem and the draft text, so the user can copy it
    /// back (decision D114).
    /// </summary>
    /// <param name="sections">The sections of the block in the file, or null for the whole file.</param>
    public void CommitRaw(string path, IReadOnlyList<SectionIdentity>? sections, string start, string end)
    {
        if (window.Session?.Edits is not { } edits)
        {
            return;
        }

        var plan = sections == null
            ? RawMissionEdit.PlanFile(edits, path, start, end)
            : RawMissionEdit.PlanBlock(edits, path, sections, start, end);
        if (plan.Problems.Count > 0)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, RawRefusal(plan.Problems.Select(p => p.ToString()), end));
            return;
        }

        foreach (var note in plan.Notes)
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, note);
        }

        if (plan.Batch != null)
        {
            window.Edits.Apply(plan.Batch);
        }
    }

    /// <summary>
    /// Runs the batch of a gesture of the studio through the mission editor of the session (section 4.0 of the M6b
    /// design). After a gesture that adds an item, the studio selects the item. Else the selection follows the renames
    /// and the row moves of the result (section 8.10 of the M6b design). A null batch does nothing.
    /// </summary>
    /// <returns>True when the batch changed W.</returns>
    /// <remarks>A batch that needs a confirmation, for example a change of a ship build of a shared file, asks in a
    /// dialog first (section 4.0.5 of the M6b design). "Confirm" runs it again with the confirmation.</remarks>
    public bool Run(MissionBatch? batch)
    {
        if (batch == null || window.Edits.Apply(batch) is not { } result)
        {
            return false;
        }

        if (result is { Status: EditStatus.Refused, NeedsConfirmation: true } && !batch.Confirm)
        {
            window.Choice.Show("Confirm the change", string.Join("\n\n", result.Errors.Select(e => e.Message)),
            [
                new Dialogs.Choice("Confirm", () => Run(batch with { Confirm = true })),
                new Dialogs.Choice("Cancel", () => { }) { Default = true },
            ]);
            return false;
        }

        if (result.Status != EditStatus.Applied)
        {
            return false;
        }

        // "Make variant" opens the new variant in prefab mode (section 8.6 of the M6b design). A rename of the prefab of
        // prefab mode keeps prefab mode on it.
        if (NewPrefabOf(result) is { } made)
        {
            OpenPrefabMode(made);
            return true;
        }

        if (OpenPrefab != null && RenamedPrefab(OpenPrefab, result.Renames) is { } renamed)
        {
            OpenPrefab = renamed;
            window.SelectInTab(CenterMode.Mission, Root!);
            return true;
        }

        var selection = window.Selection;
        var next = result.Select is { } select && Analysis(ScriptOf(select) ?? "") is { } analysis
            ? SelectionOf(analysis, select, PrefabRef.OfScript(analysis.Script.Path) == null ? ActsOf(analysis) : null)
            : null;
        next ??= Followed(selection, result.Renames.Select(r => (r.From, r.To)).ToList(), result.RowMoves);
        if (next != null && !Equals(next, selection))
        {
            window.Selection = next;
        }

        return true;
    }

    /// <summary>The prefab of the active mod that a result adds and selects, or null: the result of "Make variant". A
    /// variant of an instance selects the instance, so it stays in its mission.</summary>
    internal static PrefabRef? NewPrefabOf(MissionEditResult result) =>
        result.Select is { } select && PrefabRef.OfScript(select) is { Builtin: false } prefab &&
        result.Adds.Contains(select, StringComparer.OrdinalIgnoreCase)
            ? prefab
            : null;

    /// <summary>The new reference of a prefab that a rename of a result renamed, or null.</summary>
    internal static PrefabRef? RenamedPrefab(PrefabRef prefab, IEnumerable<ResultRename> renames) =>
        renames.Where(r => PrefabRef.OfScript(r.From)?.Equals(prefab) == true).Select(r => PrefabRef.OfScript(r.To)).FirstOrDefault();

    /// <summary>The install path of a reference of a mission item, or null.</summary>
    private static string? ScriptOf(string reference)
    {
        try
        {
            return ItemRefs.Parse(reference).Path;
        }
        catch (EditException)
        {
            return null;
        }
    }

    /// <summary>
    /// The selection of a reference of a result (section 4.0.7 of the M6b design) in an analysis: a trigger, a section,
    /// a line of a trigger, a label, an act or the script. Null for a reference that the analysis does not have.
    /// </summary>
    /// <param name="acts">The acts of the graph file, for the reference of an act, or null.</param>
    internal static MissionRef? SelectionOf(MissionAnalysis analysis, string reference, MissionActs? acts = null)
    {
        ItemReference parsed;
        try
        {
            parsed = ItemRefs.Parse(reference);
        }
        catch (EditException)
        {
            return null;
        }

        var script = analysis.Script.Path;
        if (parsed.Path is not { } path || !VfsPath.Comparer.Equals(path, script))
        {
            return null;
        }

        switch (parsed.Kind)
        {
            case ItemRefKind.Script:
                return MissionRef.Of(script);
            case ItemRefKind.Trigger when analysis.Index.Trigger(parsed.Name) is { } trigger:
                return new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref);
            case ItemRefKind.Section or ItemRefKind.Line when parsed.Section is { } id &&
                                                                analysis.Document.Find(new SectionRef(script, id)) is { } section:
                return MissionRef.OfSection(section.Ref, parsed.Kind == ItemRefKind.Line ? parsed.Row ?? -1 : -1);
            case ItemRefKind.Label:
                return new MissionRef(script, MissionItemKind.Label, Key: parsed.Name);
            case ItemRefKind.Act when acts?.Acts.FirstOrDefault(a => MissionIndex.Is(a.Nickname, parsed.Name)) is { } act:
                return new MissionRef(script, MissionItemKind.Act, Key: MissionActs.KeyOf(act));
            case ItemRefKind.Instance when analysis.Prefabs?.Instances.FirstOrDefault(i =>
                                                  i.Id.Equals(parsed.Name, StringComparison.OrdinalIgnoreCase)) is { } instance:
                return new MissionRef(script, MissionItemKind.PrefabInstance, Key: instance.Key);
            default:
                return null;
        }
    }

    /// <summary>
    /// A selection after renames and row moves: a selected item that a rename renamed gets its new name, and a selected
    /// line of a trigger keeps its line when the rows before it moved (section 8.10 of the M6b design). Null when nothing
    /// changes it.
    /// </summary>
    /// <param name="renames">The old and the new references of each renamed item.</param>
    internal static MissionRef? Followed(object? selection, IReadOnlyList<(string From, string To)> renames,
        IReadOnlyList<RowMove> moves)
    {
        if (selection is not MissionRef { Section: { } section } item)
        {
            return null;
        }

        foreach (var (from, to) in renames)
        {
            if (Identity(from) is not { } old || Identity(to) is not { } renamed ||
                !VfsPath.Comparer.Equals(old.Path, section.Path) || !old.Id.Equals(section.Id))
            {
                continue;
            }

            item = item with { Section = renamed };
            section = renamed;
        }

        if (item.Kind == MissionItemKind.Row && item.Row >= 0)
        {
            var move = moves.FirstOrDefault(m => VfsPath.Comparer.Equals(m.Trigger.Script, section.Path) &&
                                                 MissionIndex.Is(m.Trigger.Name, section.Id.Key ?? "") && m.From == item.Row);
            if (move != null)
            {
                item = item with { Row = move.To };
            }
        }

        return Equals(item, selection) ? null : item;
    }

    /// <summary>The section of a reference of a trigger or a section, or null.</summary>
    private static SectionRef? Identity(string reference)
    {
        try
        {
            var parsed = ItemRefs.Parse(reference);
            return parsed switch
            {
                { Kind: ItemRefKind.Trigger, Path: { } path } => new SectionRef(path, new SectionIdentity("Trigger", parsed.Name)),
                { Kind: ItemRefKind.Section, Path: { } path, Section: { } id } => new SectionRef(path, id),
                _ => null,
            };
        }
        catch (EditException)
        {
            return null;
        }
    }

    /// <summary>The Console text of a raw edit that the editor refused. It keeps the whole text of the user, so the user
    /// can copy it back (decision D114). The raw blocks of all files use it.</summary>
    public static string RawRefusal(IEnumerable<string> problems, string text) =>
        $"{string.Join(" ", problems)} The editor kept the lines before the edit. Your text:\n{text}";

    /// <summary>The marks of a draft text of a mission file while the user types: one object for each file or block,
    /// so its marks stay from frame to frame.</summary>
    /// <param name="block">The sections of the INI block of a page, or null for the whole file.</param>
    public IRawDraft Draft(string path, IReadOnlyList<SectionIdentity>? block)
    {
        var key = block == null ? path : $"{path}#{string.Join("|", block)}";
        if (!drafts.TryGetValue(key, out var draft))
        {
            drafts[key] = draft = new MissionRawDraft(window, path, block);
        }

        return draft;
    }

    /// <summary>Why the user cannot edit a mission file, or the template of a prefab in prefab mode, or null.</summary>
    public string? NoEditReason(string path) =>
        window.Session is not { } session ? "No profile is open."
        : session.NoEditReason ?? (PrefabRef.OfScript(path) is { } prefab
            ? session.Edits.ReadOnlyReason ?? (session.Edits.Missions.PrefabMode(prefab) is { } model
                ? model.ReadOnlyReason ?? (model.Template == null ? "The file of the prefab has a problem." : null)
                : $"No mod has the prefab '{prefab.Id}'.")
            : session.Edits.Missions.ReadOnlyReason(path));

    /// <summary>The note of the Mission tab and the mission pages while the session takes no edits, or null (section
    /// 8.1 of the M6b design).</summary>
    public string? ReadOnlyNote =>
        window.Session is { } session && (session.NoEditReason ?? session.Edits.ReadOnlyReason) is { } reason
            ? MissionErrors.NotEditableMessage(reason)
            : null;

    /// <summary>Forgets the result and the open mission, for example when the profile closes.</summary>
    public void Reset()
    {
        Unfollow();
        Result = null;
        OpenPath = null;
        OpenPrefab = null;
        selectionBeforePrefab = null;
        PendingLine = null;
        PendingSection = null;
        PendingSceneFrame = null;
        PendingGraphFrame = false;
        pendingPick = null;
        acts.Clear();
        DryRuns.Clear();
        Live.Clear();
        PlayHere.Clear();
        latestRun = 0;
        rawTexts.Clear();
        marks.Clear();
        drafts.Clear();
        changedDuringRun.Clear();
    }

    /// <summary>True when the path is a mission script of the last run (section 12.4 of the M6a design).</summary>
    public bool IsScript(string path) => Result?.IsScript(path) == true;

    /// <summary>The analysis of a script, or null. The script of a template in prefab mode gives the analysis of its
    /// template sections (section 8.6 of the M6b design). A prefab whose file has a problem gives an analysis without
    /// sections, so its Prefab page shows the problems.</summary>
    public MissionAnalysis? Analysis(string path) =>
        PrefabRef.OfScript(path) is { } prefab
            ? window.Session?.Edits.Missions.PrefabMode(prefab)?.PageAnalysis
            : Result?.Of(path);

    /// <summary>
    /// Opens a mission: the Mission tab shows it, and its root becomes the selection of the Mission tab. When no system
    /// is open, the Scene opens the system of the start trigger. Else it opens the system that most triggers name
    /// (section 7.1 of the M6a design).
    /// </summary>
    /// <param name="show">True to show the Mission tab.</param>
    public void OpenMission(string path, bool show)
    {
        // A mission opens outside prefab mode.
        OpenPrefab = null;
        selectionBeforePrefab = null;
        var opened = !string.Equals(OpenPath, VfsPath.Normalize(path) ?? path, StringComparison.OrdinalIgnoreCase);
        OpenPath = VfsPath.Normalize(path) ?? path;
        if (opened)
        {
            GraphNotes(OpenPath);
        }

        if (window.Scene.System == null && Analysis(OpenPath)?.Index is { } index &&
            MissionSummary.StartSystem(index) is { } nickname &&
            window.Session?.GameData?.Items.Systems.Get(nickname) is { } system)
        {
            // The open of a system clears the selection, so it comes before the selection of the mission.
            window.OpenSystem(system);
        }

        window.SelectInTab(CenterMode.Mission, Root!);
        if (show)
        {
            window.ShowPanel(PanelId);
        }
    }

    /// <summary>The Console notes of the graph file of a mission when it opens (section 6.8 of the M6b design): the lines
    /// that the editor cannot read, and the records of triggers that the script does not have.</summary>
    private void GraphNotes(string path)
    {
        if (window.Session?.Edits.Missions is not { } editor || Analysis(path) is not { } analysis)
        {
            return;
        }

        foreach (var problem in editor.GraphProblems(path))
        {
            window.Log.Add(ConsoleSeverity.Warning, MissionChecks.Source, problem);
        }

        foreach (var note in GraphOf(path).Notes(path, name => analysis.Index.Trigger(name) != null))
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, note);
        }
    }

    /// <summary>
    /// Opens the mission of a problem in the Mission tab and selects its line. The raw view goes to the line (section 12.4
    /// of the M6a design). Returns false and does nothing when the section is not in a mission script.
    /// </summary>
    /// <param name="entry">The index of the line in the section, or -1 for the whole section.</param>
    public bool GoTo(SectionRef target, int entry)
    {
        if (!IsScript(target.Path))
        {
            return false;
        }

        OpenMission(target.Path, show: true);
        window.SelectInTab(CenterMode.Mission, MissionRef.OfSection(target, entry));
        PendingLine = (target, entry);
        PendingGraphFrame = true;
        return true;
    }

    /// <summary>Opens a space cutscene that a mission line names in the Cutscene tab. The line holds a path below the
    /// data folder.</summary>
    public void OpenCutscene(string value)
    {
        if (MissionFiles.RelativeOf(value) is { } relative && window.Session?.Edits is { } edits &&
            edits.DataPath(relative) is { } path)
        {
            window.Cutscenes.OpenAndShow(path);
        }
    }

    /// <summary>Selects a mission and asks the Mission page to open one of its sections, for example "Voice lines"
    /// (section 5.2 of the M6a design).</summary>
    /// <param name="section">The id of the section of the page.</param>
    public void ShowSection(string path, string section)
    {
        var mission = MissionRef.Of(path);
        window.Selection = mission;
        PendingSection = (mission.Script, section);
    }

    /// <summary>
    /// A selection of the Mission tab when the last run has its script and its section, else null. Before the first
    /// run, each mission selection stays. An item of the template of prefab mode stays while prefab mode shows its
    /// prefab and the template has its section.
    /// </summary>
    public object? Valid(object? item)
    {
        if (item is not MissionRef { Script: var script } selection || PrefabRef.OfScript(script) is not { } prefab)
        {
            return Valid(Result, item);
        }

        if (!prefab.Equals(OpenPrefab) || PrefabMode?.Analysis is not { } analysis)
        {
            return null;
        }

        return selection.Section is not { } section || analysis.Document.Find(section) != null
            ? selection
            : MissionRef.Of(script);
    }

    /// <summary>
    /// A kept selection of the Mission tab for a run (section 4.2 rule 3 of the M6a design). The selection stays when the
    /// run has its script and its section. It becomes its mission when its section is gone, else null. Before the first
    /// run (a null run), each mission selection stays.
    /// </summary>
    internal static object? Valid(MissionRunResult? result, object? item)
    {
        if (item is not MissionRef selection)
        {
            return null;
        }

        if (result == null)
        {
            return selection;
        }

        if (result.Of(selection.Script) is not { } analysis)
        {
            return null;
        }

        return selection.Section is not { } section || analysis.Document.Find(section) != null
            ? selection
            : MissionRef.Of(selection.Script);
    }

    // The mission lines that name each object, base or zone, for the run of the last result.
    private readonly Dictionary<string, IReadOnlyList<MissionObjectUse>> objectUses = new(StringComparer.OrdinalIgnoreCase);
    private MissionRunResult? objectUsesRun;

    /// <summary>The lines of the mission scripts that name an object of a system, a base or a zone (section 10.18 of the
    /// M6a design). The pages ask in each frame, so the workspace keeps the lines until the next run.</summary>
    public IReadOnlyList<MissionObjectUse> UsesOf(string nickname)
    {
        if (Result is not { } result)
        {
            return [];
        }

        if (!ReferenceEquals(objectUsesRun, result))
        {
            objectUses.Clear();
            objectUsesRun = result;
        }

        if (!objectUses.TryGetValue(nickname, out var uses))
        {
            objectUses[nickname] = uses = MissionSummary.ObjectUses(result, nickname);
        }

        return uses;
    }

    /// <summary>The number of hidden base-game problems of all scripts, for the toggle.</summary>
    public int HiddenTotal => Result == null ? 0 : BaseGameMessages.Count(Result.Problems);

    /// <summary>
    /// The raw text of a script: the file text of a text script that no mod changes, else the canonical text (decision
    /// D529). Null while a worker task makes it, and when the run has no such script.
    /// </summary>
    public RawText? RawTextOf(string path)
    {
        if (RawTextTask(path) is not { } task)
        {
            return null;
        }

        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>Why the raw text of a script cannot show, or null while it loads or when it shows.</summary>
    public string? RawTextError(string path) =>
        RawTextTask(path) is { IsFaulted: true } task ? task.Exception?.GetBaseException().Message ?? "" : null;

    private Task<RawText>? RawTextTask(string path)
    {
        if (Result?.Of(path) is not { } analysis)
        {
            return null;
        }

        if (!rawTexts.TryGetValue(analysis.Script.Path, out var task))
        {
            rawTexts[analysis.Script.Path] = task = Task.Run(analysis.RawText);
        }

        return task;
    }

    /// <summary>The marks of the raw text of a script: the problems that show, the reasons of the read-only sections, and
    /// the notes of the lines of prefab members. The marks stay until the run, the text or the toggle changes, because
    /// the raw view reads them in each frame.</summary>
    public IReadOnlyList<RawMark> MarksOf(string path)
    {
        if (Result?.Of(path) is not { } analysis || RawTextOf(path) is not { } raw)
        {
            return [];
        }

        if (marksShowBaseGame != ShowBaseGame)
        {
            marks.Clear();
            marksShowBaseGame = ShowBaseGame;
        }

        if (!marks.TryGetValue(analysis.Script.Path, out var known) || !ReferenceEquals(known.Text, raw))
        {
            // The lines of the members of the linked prefab instances show dim, with their prefab (section 8.8 of the M6b
            // design).
            known = (raw, IniLint.WithNotes(IniLint.Marks(raw, analysis.Problems, ShowBaseGame), PrefabLinks.MemberLines(raw, analysis)));
            marks[analysis.Script.Path] = known;
        }

        return known.Marks;
    }

    /// <summary>Sets the base-game toggle. The Console and the mission views share it.</summary>
    public void SetShowBaseGame(bool value)
    {
        window.Settings.ShowBaseGameMessages = value;
        window.Settings.Save();
    }
}
