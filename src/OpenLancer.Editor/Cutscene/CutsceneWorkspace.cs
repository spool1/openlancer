using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.IO;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The open cutscene as a selection: the Inspector then shows the cutscene page (section 7.5 of the M8
/// design).</summary>
/// <param name="Path">The install path of the cutscene, in normal form.</param>
internal sealed record CutsceneRoot(string Path);

/// <summary>The plan of a cutscene for an image of the agent, with the bytes of its scripts at the time of the call (see
/// <see cref="CutsceneWorkspace.ShotPlan"/>).</summary>
/// <param name="Path">The install path of the cutscene, in normal form.</param>
/// <param name="Context">The context of the plan.</param>
/// <param name="Scripts">The bytes of each script of the plan, by install path.</param>
/// <param name="Data">The game data that the plan read.</param>
internal sealed record CutsceneShotPlan(string Path, PreviewContext Context, PreviewPlan Plan,
    IReadOnlyDictionary<string, byte[]> Scripts, GameDataManager Data);

/// <summary>
/// The open cutscene of the editor: its path, its preview context and plan, its timeline, and the clock of the
/// preview. One cutscene is open at a time (decision D67). A build reads the open script from W, so an unsaved edit
/// shows at once (section 8.1 of the M8 design).
/// </summary>
/// <remarks>The Hierarchy and the Inspector follow the shown tab of the center group (section 7.1): the Scene shows the
/// system and its selection, and the Cutscene tab shows the open cutscene and its selection. So each of the two keeps
/// its selection while the other one shows (see <see cref="CenterSelections"/>).</remarks>
internal sealed partial class CutsceneWorkspace(EditorWindow window) : IDisposable
{
    /// <summary>The time budget of the catch-up in a frame while the user drags or types, and at other times (section
    /// 8.4).</summary>
    private static readonly TimeSpan GestureBudget = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan IdleBudget = TimeSpan.FromMilliseconds(40);

    /// <summary>The first viewport of a build, before the view gives its size.</summary>
    private static readonly Rectangle DefaultViewport = new(0, 0, 1280, 720);

    private EditorSession? session;
    private CutsceneClock? clock;

    // The scan of the INI files of the game data that the references came from.
    private (GameDataManager? Data, ThnReferences? References) scan;

    // The space lines of the game data (decision D641).
    private (GameDataManager? Data, SpaceScenes? Scenes) spaces;

    // The star system behind a space script, kept between scripts (decision D646), and the system of a load that
    // failed, with the banner text. The next frame does not load that system of that data again.
    private readonly StageCache<SpaceStage> stages = new();
    private (string System, GameDataManager Data)? failedStage;
    private string? stageProblem;

    // The game data that the plan of the context read.
    private GameDataManager? planData;

    // The banner of a system with unsaved edits (decision D648).
    private string? unsavedSystem;

    // The cutscene scripts of the game data, and the categories of the scripts and of the new unsaved cutscenes.
    private (GameDataManager? Data, IReadOnlyList<string> Paths) scripts = (null, []);
    private readonly CategoriesCache categories = new();

    // A change of a gesture invalidated the clock, and the end of the gesture must build.
    private bool gesture;

    // The player that showed in the last frame, and the catch-up that shows the progress.
    private ICutscenePlayer? shown;
    private double catchUpWall;
    private double catchUpStart;

    // The problem of the clock that the Console has. The clock keeps one exception object until the next invalidate,
    // so each new object gives one Console line.
    private Exception? reported;

    // The timeline of W, made again when W or the context changes, and at the end of a gesture, which pinned the
    // sub-rows of the bars.
    private (ThnScriptModel? Working, ScriptPlay Play, TimelineModel? Model, bool Pinned) timeline;

    // The sound of the preview (section 5.3 of the M8b design): the engine output, the scheduler, and the timeline
    // whose cues the scheduler has. A null timeline makes the next frame set the cues and start their decode.
    private readonly CutsceneSound sound = new(window);
    private TimelineAudio? audio;
    private TimelineModel? cueModel;

    /// <summary>The install path of the open cutscene, in normal form, or null.</summary>
    public string? Path { get; private set; }

    /// <summary>The preview context, or null when no cutscene is open.</summary>
    public PreviewContext? Context { get; private set; }

    /// <summary>The contexts that fit the open cutscene, the suggested context first.</summary>
    public IReadOnlyList<PreviewContext> Choices { get; private set; } = [];

    /// <summary>The plan of the context, or null when the plan failed.</summary>
    public PreviewPlan? Plan { get; private set; }

    /// <summary>Why the open cutscene has no preview, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>The Preview controls, or null without a preview.</summary>
    public PreviewTransport? Transport { get; private set; }

    /// <summary>The star system that the open context plays in, or null. The builds of the context use its
    /// world.</summary>
    public SpaceStage? Stage =>
        Plan?.System is { } system && session?.GameData is { } data && stages.Holds(system, data)
            ? stages.Stage
            : null;

    /// <summary>The banners of the plan of the open context, in the order of <see cref="BannersOf"/>.</summary>
    public IEnumerable<string> Banners() => Plan == null
        ? []
        : BannersOf(Plan, Visible is { InHostWorld: true } visible ? visible.MainObjectNote : null, unsavedSystem,
            stageProblem);

    /// <summary>
    /// The banners of a plan (section 3.3 of the cutscene fixes plan), in order. First come the note of the plan and
    /// the main object. Then come the position of the mission line, a system with unsaved edits, and a system that did
    /// not load. The banner of a system that did not load replaces the note, because the note says that the preview
    /// shows the system.
    /// </summary>
    /// <param name="mainObject">The banner of the main object of a player in the stage, or null.</param>
    /// <param name="unsaved">The banner of a system with unsaved edits, or null.</param>
    /// <param name="stageFailed">The banner of a system that did not load, or null.</param>
    internal static IEnumerable<string> BannersOf(PreviewPlan plan, string? mainObject, string? unsaved,
        string? stageFailed)
    {
        if (plan.Note is { } note && stageFailed == null)
        {
            yield return note;
        }

        if (mainObject != null)
        {
            yield return mainObject;
        }

        if (plan.ScriptPosition != null)
        {
            yield return ScriptPositionLine;
        }

        if (unsaved != null)
        {
            yield return unsaved;
        }

        if (stageFailed != null)
        {
            yield return stageFailed;
        }
    }

    /// <summary>The banner of a mission line of the form <c>script, x, y, z</c> (decision D650).</summary>
    internal const string ScriptPositionLine =
        "The mission line gives a position for the script. The game moves the script to that position. The preview " +
        "does not.";

    /// <summary>The clock, or null without a preview.</summary>
    public CutsceneClock? Clock => clock;

    /// <summary>The player that the view draws, or null.</summary>
    public CutscenePlayer? Visible => clock?.Visible as CutscenePlayer;

    /// <summary>The steps of the players of the open cutscene.</summary>
    public PreviewMeter Meter { get; private set; } = new();

    /// <summary>The time of the last build that ended.</summary>
    public TimeSpan LastBuild { get; private set; }

    /// <summary>The builds of the open context.</summary>
    public int Builds { get; private set; }

    /// <summary>The THN camera right after the last build, before a step, or null when no scene object is the
    /// camera.</summary>
    public string? CameraAfterBuild { get; private set; }

    /// <summary>The time of the last scan of the references.</summary>
    public TimeSpan LastScan { get; private set; }

    /// <summary>The wall time that the catch-up still needs, from the rate of the catch-up so far, or null.</summary>
    public TimeSpan? CatchUpLeft { get; private set; }

    /// <summary>
    /// True when the shown frame comes from a catch-up and no play ran since. The particle effects then show their
    /// first state at the playhead (section 8.6).
    /// </summary>
    public bool EffectsStartAtPlayhead { get; private set; }

    /// <summary>The viewport of the last draw. The next build starts with it.</summary>
    public Rectangle Viewport { get; set; } = DefaultViewport;

    /// <summary>The duration of W, or 0 when the session cannot read the cutscene.</summary>
    public double Duration => Path != null && session?.Edits.Cutscene(Path) is { } working ? working.Duration : 0;

    /// <summary>The timeline of W in the context, or null when no cutscene is open or the session cannot read
    /// it. The sub-rows of the bars stay where they can, and they stay pinned while a gesture is open (decision
    /// D131).</summary>
    public TimelineModel? Timeline
    {
        get
        {
            if (Path == null || session?.Edits.Cutscene(Path) is not { } working)
            {
                return null;
            }

            var play = Plan?.PlayOf(Path) ?? ScriptPlay.Normal;
            var pin = window.IsEditing;
            if (!ReferenceEquals(timeline.Working, working) || timeline.Play != play || timeline.Model == null ||
                timeline.Pinned && !pin)
            {
                timeline = (working, play, TimelineModel.Build(Path, working, ThornEvaluator.Instance.Symbols, play,
                    timeline.Model, pin), pin);
            }

            return timeline.Model;
        }
    }

    /// <summary>The selected element of the open cutscene, or null.</summary>
    public ThnRef? Selected => window.Selection is ThnRef selected && VfsPath.Comparer.Equals(selected.Path, Path)
        ? selected
        : null;

    /// <summary>The selection of the open cutscene when nothing in it is selected, or null without a cutscene.</summary>
    public CutsceneRoot? Root => Path != null ? new CutsceneRoot(Path) : null;

    /// <summary>The wait of a catch-up, from the measured step rate (section 7.7).</summary>
    public string UpdatingText => CatchUpLeft is { } left
        ? $"Updating: about {Math.Ceiling(left.TotalSeconds):0} s left"
        : "Updating";

    /// <summary>
    /// Opens a cutscene of the edit view with the suggested context. Returns false when the session cannot read it.
    /// <see cref="Problem"/> then tells why.
    /// </summary>
    public bool Open(string path)
    {
        Close();
        session = window.Session;
        Path = VfsPath.Normalize(path) ?? path;
        if (session?.GameData is not { } data)
        {
            Problem = "The game data is not loaded.";
            return false;
        }

        if (session.Edits.Cutscene(Path) == null)
        {
            Problem = session.Edits.CutsceneReadOnlyReason(Path) ?? $"The edit view has no cutscene {Path}.";
            return false;
        }

        session.Edits.Changed += Changed;
        UseSound(data);
        Choices = MakeChoices(data);
        SetContext(Choices[0]);
        SelectRoot();
        // The line goes to the Console and to the log file, so that the bench records it too.
        var line = OpenedLine(Path, Duration, Context!.Label, Timeline?.Summary);
        window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource, line);
        FLLog.Info(EditorWindow.EditorSource, line);
        return true;
    }

    /// <summary>The line of an open: the path, the duration, the context and the counts of the Timeline. The numbers
    /// do not depend on the culture of the user.</summary>
    internal static string OpenedLine(string path, double duration, string context, string? timeline) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Opened cutscene {path} ({duration:0.###} s). Context: {context}. {timeline}").TrimEnd();

    /// <summary>The Console line of a build or a step of the preview that failed (decision D657). The time does not
    /// depend on the culture of the user.</summary>
    /// <param name="time">The script time of the work that failed, in seconds.</param>
    internal static string StoppedLine(string path, double time, Exception problem) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The preview of {path} stopped at {time:0.000} s: {ErrorText.Of(problem)} The view keeps the last frame.");

    /// <summary>The Console line of a draw of the preview that failed (decision D657).</summary>
    /// <param name="time">The script time of the frame that did not draw, in seconds.</param>
    internal static string DrawFailedLine(string path, double time, Exception problem) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The preview of {path} cannot draw the frame at {time:0.000} s: {ErrorText.Of(problem)}");

    /// <summary>Writes one Console error for a draw of the open cutscene that failed. The view calls it one time for
    /// each failure, outside the render callback.</summary>
    public void ReportDrawFailure(Exception problem, double time)
    {
        if (Path != null)
        {
            Report(DrawFailedLine(Path, time, problem), problem);
        }
    }

    /// <summary>Writes a Console error, and the line with the full exception text to the log file. The log file gets an
    /// info line, because FLLog sends each warning and error line to the Console too.</summary>
    private void Report(string line, Exception problem)
    {
        window.Log.Add(ConsoleSeverity.Error, EditorWindow.EditorSource, line);
        FLLog.Info(EditorWindow.EditorSource, $"{line}{Environment.NewLine}{problem}");
    }

    /// <summary>
    /// Keeps a cutscene selection valid in each frame, before the panels draw: when an undo removed the element or some
    /// events of a set, or another cutscene opened, the selection becomes the valid part, or the root of the open
    /// cutscene while the Cutscene tab shows. The center group keeps one selection for each tab (see
    /// <see cref="CenterSelections"/>).
    /// </summary>
    /// <param name="shown">True when the Cutscene tab is the shown tab of the center group.</param>
    public void ValidateSelection(bool shown)
    {
        if (window.Selection is { } current && IsCutsceneItem(current) && !Equals(Valid(current), current))
        {
            window.Selection = Valid(current) ?? (shown ? Root : null);
        }
    }

    /// <summary>True for a selection that the Cutscene tab shows: an element, events or the root of a
    /// cutscene.</summary>
    internal static bool IsCutsceneItem(object? item) => item is ThnRef or ThnEventSet or CutsceneRoot;

    /// <summary>
    /// A cutscene selection when it is an element or the root of the open cutscene, else null. Of a set of events, the
    /// events that W still has: a set, one event, or null.
    /// </summary>
    public object? Valid(object? item) => item switch
    {
        CutsceneRoot root when VfsPath.Comparer.Equals(root.Path, Path) => root,
        ThnEventSet set when VfsPath.Comparer.Equals(set.Path, Path) && Timeline is { } model => Kept(set, model),
        ThnRef element when VfsPath.Comparer.Equals(element.Path, Path) && Timeline is { } model &&
                            (element.Kind == ThnElementKind.Event
                                ? model.Event(element.Id) != null
                                : model.KindOf(element.Id) != null) => element,
        _ => null,
    };

    /// <summary>The events of a set that W still has: the set itself, a smaller set, one event, or null.</summary>
    private static object? Kept(ThnEventSet set, TimelineModel model)
    {
        var kept = set.Events.Where(e => model.Event(e.Id) != null).Select(e => e.Id).ToList();
        return kept.Count == set.Events.Length ? set : ThnEventSet.Of(set.Path, kept);
    }

    /// <summary>The kind of a cutscene selection, or null for another item.</summary>
    public ObjectKind? KindOf(object? item) => item switch
    {
        CutsceneRoot => ObjectKinds.Thn.Script,
        ThnRef { Kind: ThnElementKind.Event } or ThnEventSet => ObjectKinds.Thn.Event,
        ThnRef element when Valid(element) != null => Timeline!.KindOf(element.Id),
        _ => null,
    };

    /// <summary>
    /// The title and the identifier of a cutscene selection for the header of the Inspector: the file name and the
    /// install path of a cutscene, the name of an entity, or the type and the time of an event and its first target.
    /// Null for another item.
    /// </summary>
    public (string Title, string Identifier)? Describe(object? item)
    {
        switch (item)
        {
            case CutsceneRoot root:
                return (System.IO.Path.GetFileNameWithoutExtension(root.Path), root.Path);
            case ThnEventSet set:
                return ($"{set.Events.Length} events", System.IO.Path.GetFileNameWithoutExtension(set.Path));
            case ThnRef { Kind: ThnElementKind.Event } element when Timeline?.Event(element.Id) is { } ev:
                return ($"{ev.Type} at {ev.Time.ToString("0.000", CultureInfo.InvariantCulture)} s",
                    ev.Targets.Count > 0 ? ev.Targets[0] : TimelineModel.NoTarget);
            case ThnRef { Kind: ThnElementKind.Entity } element when Element(element) is { } entity:
                var name = TimelineModel.EntityName(entity.Table);
                return (name, name);
            default:
                return null;
        }
    }

    /// <summary>The section of the edit view of the session that holds an INI line that names a cutscene, or
    /// null (see <see cref="SectionOf(EditSession, ThnReference)"/>).</summary>
    public SectionRef? SectionOf(ThnReference reference) =>
        window.Session?.Edits is { } edits ? SectionOf(edits, reference) : null;

    /// <summary>
    /// The section of W that holds an INI line that names a cutscene: the section at the place of the line when it has
    /// the type, the key and the value of the line, or else the first section that has them. So a click opens the
    /// right one of two sections with the same line, and a place that an edit of the file made stale still finds the
    /// line. Null when no section has them, for example after an edit that removed the line.
    /// </summary>
    internal static SectionRef? SectionOf(EditSession edits, ThnReference reference)
    {
        var sections = edits.Sections(reference.File);
        bool Holds(SectionRef section) =>
            section.Id.Type.Equals(reference.Section, StringComparison.OrdinalIgnoreCase) &&
            edits.Get(section) is { } view && view.Lines(reference.Key)
                .Any(l => l.Values.Any(v => string.Equals(v.ToString(), reference.Value, StringComparison.Ordinal)));
        return reference.SectionIndex < sections.Count && Holds(sections[reference.SectionIndex])
            ? sections[reference.SectionIndex]
            : sections.FirstOrDefault(Holds);
    }

    /// <summary>The element of W that a selection names, or null.</summary>
    public ThnElement? Element(ThnRef element) =>
        Valid(element) != null && session?.Edits.Cutscene(Path!) is { } working
            ? working.Elements(element.Kind).Find(e => e.Id == element.Id)
            : null;

    /// <summary>
    /// The keys of the preview (section 7.7) while the Timeline or the Cutscene panel has the focus: Space, Home, End,
    /// and Left and Right with or without Shift.
    /// </summary>
    public void Keys(bool focused)
    {
        if (Transport is not { } transport)
        {
            return;
        }

        var io = ImGui.GetIO();
        switch (PreviewKeys.Read(focused, io.WantTextInput, k => ImGui.IsKeyPressed(k), io.KeyShift))
        {
            case PreviewKey.PlayPause:
                transport.TogglePlay();
                break;
            case PreviewKey.Start:
                transport.Seek(0);
                break;
            case PreviewKey.End:
                transport.Seek(transport.End);
                break;
            case PreviewKey.FrameBack:
                transport.StepFrames(-1);
                break;
            case PreviewKey.FrameForward:
                transport.StepFrames(1);
                break;
            case PreviewKey.SecondBack:
                transport.StepSeconds(-1);
                break;
            case PreviewKey.SecondForward:
                transport.StepSeconds(1);
                break;
        }
    }

    /// <summary>Selects the open cutscene in the Cutscene tab: at once when it shows, else when it shows next.</summary>
    private void SelectRoot() => Select(Root!);

    /// <summary>Selects an item of the open cutscene in the Cutscene tab: at once when it shows, else when it shows
    /// next. The Scene keeps its selection.</summary>
    private void Select(object item) => window.SelectInTab(CenterMode.Cutscene, item);

    /// <summary>Opens a cutscene, and shows it in the Cutscene tab and in the Timeline. When it does not open, the
    /// Console tells why.</summary>
    public bool OpenAndShow(string path)
    {
        if (!OpenOrWarn(path))
        {
            return false;
        }

        ShowPanels();
        return true;
    }

    /// <summary>Opens a cutscene (see <see cref="Open"/>). When it does not open, the Console tells why.</summary>
    public bool OpenOrWarn(string path)
    {
        if (Open(path))
        {
            return true;
        }

        window.Log.Add(ConsoleSeverity.Warning, EditorWindow.EditorSource, $"The cutscene {path} did not open: {Problem}");
        return false;
    }

    /// <summary>
    /// Shows the place of a problem (section 9 of the M8 design): opens the cutscene when it is not the open one, shows
    /// it, and selects the element (see <see cref="SelectionAt"/>).
    /// </summary>
    public void Show(ThnLocation place)
    {
        if (ToOpen(Path, place) is { } path)
        {
            if (!OpenAndShow(path))
            {
                return;
            }
        }
        else
        {
            ShowPanels();
        }

        Select(SelectionAt(Path!, session?.Edits.Cutscene(Path!), place));
    }

    /// <summary>The cutscene that a click on a problem opens, in normal form, or null when the problem is about the
    /// open cutscene.</summary>
    /// <param name="open">The install path of the open cutscene, in normal form, or null.</param>
    internal static string? ToOpen(string? open, ThnLocation place)
    {
        var path = VfsPath.Normalize(place.Path) ?? place.Path;
        return VfsPath.Comparer.Equals(path, open) ? null : path;
    }

    /// <summary>
    /// The selection that shows the place of a problem in the open cutscene: the element of W that the place names (see
    /// <see cref="ThnLocation.ElementIn"/>), or else the cutscene. An edit after the save can delete the element, or
    /// make its key name no element or more than one element.
    /// </summary>
    /// <param name="path">The install path of the open cutscene, in normal form.</param>
    /// <param name="working">W of the open cutscene, or null when the session cannot read it.</param>
    internal static object SelectionAt(string path, ThnScriptModel? working, ThnLocation place) =>
        working != null && place.Kind is { } kind && place.ElementIn(working) is { } element
            ? new ThnRef(path, kind, element.Id)
            : new CutsceneRoot(path);

    private void ShowPanels()
    {
        window.ShowPanel(CutsceneView.PanelId);
        window.ShowPanel(TimelinePanel.PanelId);
    }

    /// <summary>Previews the open cutscene in another context. The playhead and the Preview controls stay.</summary>
    public void SetContext(PreviewContext context)
    {
        if (Path == null || session?.GameData is not { } data)
        {
            return;
        }

        var previous = Transport;
        DropPreview();
        // The context decides which events run, so the cues change.
        audio?.StopAll();
        cueModel = null;
        Context = context;
        try
        {
            Plan = context.Plan(Path, data.Items, References(), session.Edits.Cutscene(Path));
        }
        catch (ArgumentException e)
        {
            Problem = ErrorText.Of(e);
            return;
        }

        // The next frame loads the stage of the plan, when the session does not load the data.
        (failedStage, stageProblem, planData) = (null, null, data);
        UpdateUnsavedSystem();
        if (context is { Kind: PreviewContextKind.Space, System: null })
        {
            window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource, NoSystemLine(Path));
        }

        clock = new CutsceneClock(Build);
        Transport = new PreviewTransport(clock, () => Duration);
        if (previous != null)
        {
            (Transport.Speed, Transport.Loop, Transport.PreRoll, Transport.LoopRange) =
                (previous.Speed, previous.Loop, previous.PreRoll, previous.LoopRange);
            Transport.Seek(previous.Time);
        }
    }

    /// <summary>
    /// Runs the clock for one editor frame, then the sound of the preview. While the game data loads again, nothing
    /// builds or steps, because a build loads models with the resources of the session, and no sound plays.
    /// </summary>
    public void Frame(double elapsed)
    {
        if (Transport == null || clock == null || session is not { GameData: not null, IsBusy: false })
        {
            // Else the sound plays on while the picture waits for the reload after a save.
            audio?.StopAll();
            return;
        }

        if (gesture && !window.IsEditing)
        {
            gesture = false;
            clock.EndGesture();
        }

        if (Transport.Playing)
        {
            EffectsStartAtPlayhead = false;
        }

        // The stage loads before a build can use it, and never while the session loads the data again (rule S-3).
        LoadStage(session.GameData);
        Transport.Frame(elapsed, window.IsEditing ? GestureBudget : IdleBudget);
        Stage?.Update(clock.DrawSeconds);
        if (clock.Problem is { } problem && !ReferenceEquals(problem, reported))
        {
            reported = problem;
            Report(StoppedLine(Path!, clock.ProblemTime, problem), problem);
        }

        if (!ReferenceEquals(shown, clock.Visible))
        {
            shown = clock.Visible;
            EffectsStartAtPlayhead = shown is { Time: > 0 } && !Transport.Playing;
        }

        Progress(elapsed);
        Sound();
    }

    /// <summary>
    /// Loads the star system of a space plan into the stage (decision D646). The stage of another system or of older
    /// game data goes first. A load that fails keeps a banner and a Console warning, and the builds then play the
    /// script without the system.
    /// </summary>
    private void LoadStage(GameDataManager data)
    {
        // New data that the context did not read yet: the reload of the context disposes the clock first.
        if (!ReferenceEquals(planData, data))
        {
            return;
        }

        // The clock of other data is gone: a reload of the data builds the context again.
        FreeStageOfOtherData(data);
        if (Plan?.System is not { } system || stages.Holds(system, data) ||
            failedStage is { } failed && failed.System == system && ReferenceEquals(failed.Data, data))
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        // The engine lines of the load form a batch of the Console, as the lines of a system open in the Scene view.
        var batch = window.BeginConsoleBatch();
        try
        {
            var source = data.Items.Systems.Get(system) ??
                         throw new InvalidOperationException("The game data has no such system.");
            stages.Get(system, data, () => SpaceStage.Load(source, data, session!.Resources, window));
            var line = StageLoadedLine(system, Stopwatch.GetElapsedTime(start));
            window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource, line);
            FLLog.Info(EditorWindow.EditorSource, line);
        }
        catch (Exception e)
        {
            failedStage = (system, data);
            stageProblem = StageFailedLine(system, e);
            window.Log.Add(ConsoleSeverity.Warning, EditorWindow.EditorSource, stageProblem);
            FLLog.Info(EditorWindow.EditorSource, $"{stageProblem}{Environment.NewLine}{e}");
        }
        finally
        {
            ConsoleLog.EndBatch(batch);
        }
    }

    /// <summary>The Console line of a stage that loaded. The time does not depend on the culture of the user.</summary>
    internal static string StageLoadedLine(string system, TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The cutscene preview loaded the system {system} in {time.TotalSeconds:0.00} s.");

    /// <summary>The Console line of a space script whose mission lines give no system.</summary>
    internal static string NoSystemLine(string script) =>
        $"No mission line names the system of {script}. The preview shows the script without a system.";

    /// <summary>The banner and the Console line of a stage that did not load.</summary>
    internal static string StageFailedLine(string system, Exception problem) =>
        $"The preview cannot load the system {system}: {ErrorText.Of(problem)} The preview shows the script without " +
        "the system.";

    /// <summary>The banner of decision D648 when the file of the system of the plan has unsaved edits, else
    /// null.</summary>
    private void UpdateUnsavedSystem()
    {
        unsavedSystem = Plan?.System is { } system && session?.Edits is { IsDirty: true } edits &&
                        edits.SystemPath(system) is { } file && edits.EditedPaths.Contains(file, VfsPath.Comparer)
            ? UnsavedSystemLine(session.ItemName(session.GameData?.Items.Systems.Get(system)) is { Length: > 0 } name
                ? name
                : system)
            : null;
    }

    /// <summary>The banner of a system with unsaved edits (decision D648).</summary>
    internal static string UnsavedSystemLine(string name) =>
        $"The system {name} has unsaved edits. The Cutscene tab shows the saved system. Save to see the edits here.";

    /// <summary>
    /// A frame in which the preview does not run, because neither the Cutscene tab nor the Timeline shows. The clock
    /// waits, so the sound stops too. Else it plays on without the picture. The next frame that runs starts the active
    /// sounds again at the playhead.
    /// </summary>
    public void Wait() => audio?.StopAll();

    /// <summary>
    /// The sound of one frame (decision D132): the cues of the timeline, and a play that goes on or starts again. The
    /// sound plays only while the preview plays forward: not in the pre-roll, not during a catch-up and not during a
    /// drag of the playhead, and only while the Sound toggle is on.
    /// </summary>
    private void Sound()
    {
        audio ??= new TimelineAudio(sound);
        var model = Timeline;
        if (!ReferenceEquals(model, cueModel))
        {
            var first = cueModel == null;
            cueModel = model;
            var cues = model != null && session?.Edits.Cutscene(Path!) is { } working
                ? TimelineAudio.Cues(model, working)
                : [];
            audio.SetCues(cues);
            if (first)
            {
                sound.Preload(cues);
            }
        }

        audio.Frame(Transport!.Time, Transport.Jumps, Transport.PlaysSound && window.Settings.PreviewSound,
            Transport.Speed);
    }

    /// <summary>Plays the sound of a SOUND entity of the open cutscene once from its start, for the Preview row of the
    /// Inspector. Returns false when the entity has no sound to play.</summary>
    public bool PlaySound(ThnRef entity)
    {
        if (Element(entity) is not { } element || TimelineAudio.CueOf(element.Table, element.Id) is not { } cue)
        {
            return false;
        }

        sound.PlayOnce(cue);
        return true;
    }

    /// <summary>Stops the sound of the Preview row of the Inspector.</summary>
    public void StopSound() => sound.StopSingle();

    /// <summary>The sound uses a load of the game data. A new load gets a new sound manager, so the next play reads the
    /// sounds of the new data.</summary>
    private void UseSound(GameDataManager data)
    {
        audio?.StopAll();
        sound.Use(data);
        sound.Scene = name => Visible?.VoiceOf(name);
        cueModel = null;
    }

    /// <summary>
    /// The game data loaded again after a save. The sound gets a new sound manager, the contexts that fit the cutscene
    /// come again from the INI files, the plan reads the new data, and the clock builds again. So the room of a room
    /// line that the save wrote is a choice now. The chosen context stays when it is still a choice. The stage of the
    /// old data goes, also when no cutscene is open.
    /// </summary>
    public void DataReplaced()
    {
        if (window.Session?.GameData is not { } data)
        {
            return;
        }

        if (Path != null)
        {
            UseSound(data);
            if (Context != null)
            {
                Choices = MakeChoices(data);
                SetContext(ContextAfterReload(Choices, Context));
            }
        }

        // SetContext disposed the clock of the old data, so no player uses the old stage now.
        FreeStageOfOtherData(data);
    }

    /// <summary>Disposes a stage of other game data, and forgets a failed load of other game data. Call it only when no
    /// clock of that data exists.</summary>
    private void FreeStageOfOtherData(GameDataManager data)
    {
        stages.ForgetOtherData(data);
        if (failedStage is { } failed && !ReferenceEquals(failed.Data, data))
        {
            failedStage = null;
        }
    }

    /// <summary>The context after a reload of the game data: the chosen context when the new choices still have it,
    /// else the first choice.</summary>
    internal static PreviewContext ContextAfterReload(IReadOnlyList<PreviewContext> choices, PreviewContext chosen) =>
        choices.FirstOrDefault(c => PreviewContext.Same(c, chosen)) ?? choices[0];

    /// <summary>The contexts that fit the open cutscene in the game data, with the ship package of Play.</summary>
    private List<PreviewContext> MakeChoices(GameDataManager data) => ChoicesOf(Path!, session!, data);

    /// <summary>The contexts that fit a cutscene in the game data, the suggested context first, with the ship package of
    /// Play.</summary>
    private List<PreviewContext> ChoicesOf(string path, EditorSession current, GameDataManager data)
    {
        var package = window.Settings.PlayPackageOf(current.Profile.FilePath);
        return PreviewContext.Choices(path, References(), data.Items, SpaceScenes())
            .Select(c => c with { ShipPackage = package }).ToList();
    }

    /// <summary>
    /// The plan of a cutscene of W for an image of the agent (section 7.7 of the MCP1 design): the plan of the context of
    /// the Cutscene tab when the tab shows the cutscene, else the plan of the suggested context. The plan keeps the
    /// bytes of each of its scripts now, so a later build shows W at the time of the call. The preview of the user does
    /// not change.
    /// </summary>
    /// <param name="path">The install path of a cutscene of W.</param>
    /// <exception cref="ArgumentException">The context does not fit the cutscene. The message tells why.</exception>
    /// <exception cref="InvalidOperationException">No game data is loaded, or the session cannot read a script of the
    /// plan.</exception>
    internal CutsceneShotPlan ShotPlan(string path)
    {
        var current = window.Session ?? throw new InvalidOperationException("No profile is open.");
        var data = current.GameData ?? throw new InvalidOperationException("The game data is not loaded.");
        var working = current.Edits.Cutscene(path) ?? throw new InvalidOperationException(
            current.Edits.CutsceneReadOnlyReason(path) ?? $"The edit view has no cutscene {path}.");
        var context = VfsPath.Comparer.Equals(path, Path) && Context != null ? Context : ChoicesOf(path, current, data)[0];
        var plan = context.Plan(path, data.Items, References(), working);
        var scripts = new[] { plan.SetScript, plan.Fidget }.Concat(plan.Scripts).OfType<string>()
            .Distinct(VfsPath.Comparer)
            .ToDictionary(p => p, p => ScriptBytes(current.Edits, data.Items.VFS, p, path), VfsPath.Comparer);
        return new CutsceneShotPlan(path, context, plan, scripts, data);
    }

    /// <summary>
    /// The space lines of the loaded game data (decision D641). They build once for each load of the data, from the
    /// same view as the references, and each mission file is read once.
    /// </summary>
    public SpaceScenes SpaceScenes()
    {
        var data = window.Session?.GameData ?? throw new InvalidOperationException("The game data is not loaded.");
        if (!ReferenceEquals(spaces.Data, data) || spaces.Scenes == null)
        {
            spaces = (data, new SpaceScenes(data.Items.VFS, References(), SpaceFacts.From(data.Items)));
        }

        return spaces.Scenes;
    }

    /// <summary>
    /// The references of the INI files of the loaded game data. The scan runs once for each load of the data, because
    /// the INI files change only with a save, and a save loads the data again.
    /// </summary>
    public ThnReferences References()
    {
        var data = window.Session?.GameData ?? throw new InvalidOperationException("The game data is not loaded.");
        if (!ReferenceEquals(scan.Data, data) || scan.References == null)
        {
            var start = Stopwatch.GetTimestamp();
            var references = ThnReferences.Scan(data.Items.VFS);
            LastScan = Stopwatch.GetElapsedTime(start);
            foreach (var problem in references.Problems)
            {
                window.Log.Add(problem);
            }

            scan = (data, references);
        }

        return scan.References;
    }

    /// <summary>
    /// The install paths of the cutscene scripts of the loaded game data, sorted by folder and name. The workspace
    /// makes the list once for each load of the data. The cutscene list of the toolbar and the Cutscenes tab use it.
    /// </summary>
    public IReadOnlyList<string> Scripts()
    {
        var data = window.Session?.GameData ?? throw new InvalidOperationException("The game data is not loaded.");
        if (!ReferenceEquals(scripts.Data, data))
        {
            scripts = (data, VfsFiles.Find(data.Items.VFS, ".thn").Select(p => VfsPath.Normalize(p) ?? p).ToList());
        }

        return scripts.Paths;
    }

    /// <summary>
    /// The categories of the scripts of the loaded game data and of the new unsaved cutscenes of the session. Section
    /// 4.5 of the cutscene categories design tells how they build. They build again for new game data and for another
    /// set of new unsaved cutscenes. The first call after a load needs the references, so it can start their scan.
    /// </summary>
    public CutsceneCategories Categories()
    {
        var current = window.Session ?? throw new InvalidOperationException("No profile is open.");
        var data = current.GameData ?? throw new InvalidOperationException("The game data is not loaded.");
        var mods = current.Profile.Mods;

        string ModName(string id) =>
            mods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Manifest.Name ?? id;

        return categories.Get(data, current.Version,
            () => current.Edits.NewCutscenes.Where(p => !data.Items.VFS.FileExists(p)),
            unsaved => CutsceneCategories.Build(Scripts(),
                CutsceneFacts.From(data.Items, References(), current.Name, ModName, unsaved)));
    }

    /// <summary>
    /// The categories of one load of the game data (section 4.5 of the cutscene categories design). They build again
    /// for other game data and for another set of new unsaved cutscenes. Only an edit, an undo or a redo changes that
    /// set, and each one changes the edit version. So the cache reads the set only for another version.
    /// </summary>
    internal sealed class CategoriesCache
    {
        private (object? Data, int Version, string[] Unsaved, CutsceneCategories? Model) state = (null, 0, [], null);

        /// <summary>The categories of the data, from the cache or from a new build.</summary>
        /// <param name="data">The loaded game data. Other data builds again.</param>
        /// <param name="version">The edit version of the session.</param>
        /// <param name="unsaved">Gives the install paths of the new unsaved cutscenes.</param>
        /// <param name="build">Builds the categories with the new unsaved cutscenes, in normal form.</param>
        public CutsceneCategories Get(object data, int version, Func<IEnumerable<string>> unsaved,
            Func<string[], CutsceneCategories> build)
        {
            var sameData = state.Model != null && ReferenceEquals(state.Data, data);
            if (sameData && state.Version == version)
            {
                return state.Model!;
            }

            var paths = unsaved().Select(p => VfsPath.Normalize(p) ?? p).ToArray();
            var model = sameData && paths.SequenceEqual(state.Unsaved, VfsPath.Comparer) ? state.Model! : build(paths);
            state = (data, version, paths, model);
            return model;
        }

        /// <summary>Forgets the categories, for example when the profile closes.</summary>
        public void Reset() => state = (null, 0, [], null);
    }

    /// <summary>Closes the open cutscene.</summary>
    public void Close()
    {
        if (session != null)
        {
            session.Edits.Changed -= Changed;
        }

        DropPreview();
        audio?.StopAll();
        sound.StopSingle();
        cueModel = null;
        Context = null;
        Path = null;
        session = null;
        Choices = [];
        timeline = default;
    }

    /// <summary>Closes the cutscene, for example when the profile closes. Forgets the references, the space lines, the
    /// script list, the categories and the sound manager of the game data. Frees the stage after the clock.</summary>
    public void Reset()
    {
        Close();
        stages.Reset();
        failedStage = null;
        sound.Use(null);
        scan = default;
        spaces = default;
        scripts = (null, []);
        categories.Reset();
    }

    /// <summary>Closes the cutscene and frees the audio of the window.</summary>
    public void Dispose()
    {
        Reset();
        sound.Dispose();
    }

    /// <summary>Disposes the clock and forgets the preview of the context.</summary>
    private void DropPreview()
    {
        clock?.Dispose();
        clock = null;
        Transport = null;
        Plan = null;
        Problem = null;
        Meter = new PreviewMeter();
        LastBuild = default;
        Builds = 0;
        CameraAfterBuild = null;
        shown = null;
        reported = null;
        CatchUpLeft = null;
        catchUpWall = 0;
        EffectsStartAtPlayhead = false;
        gesture = false;
    }

    /// <summary>
    /// Builds a player at script time 0 from the current W, context and game data. The clock calls it.
    /// </summary>
    private ICutscenePlayer Build()
    {
        var data = session!.GameData!;
        var start = Stopwatch.GetTimestamp();
        var player = CutscenePlayer.Build(Plan!, Script, data, session.Resources, window, Viewport, Meter, Stage?.World);
        LastBuild = Stopwatch.GetElapsedTime(start);
        CameraAfterBuild = player.CameraName;
        Builds++;
        return player;
    }

    /// <summary>
    /// The script of an install path: the text that the writer makes from W for the open cutscene and for each other
    /// open cutscene of the session, else the bytes of the edit view. So no build reads the open file on disk.
    /// </summary>
    private ThnScript Script(string path)
    {
        var items = session!.GameData!.Items;
        return new ThnScript(ScriptBytes(session.Edits, items.VFS, path, Path), items.ThornReadCallback, path);
    }

    /// <summary>
    /// The bytes of a script for a build: the text that the writer makes from W for a cutscene and for each other open
    /// cutscene of the session, else the bytes of the edit view.
    /// </summary>
    /// <param name="files">The file system of the game data.</param>
    /// <param name="cutscene">The cutscene of the build, or null.</param>
    internal static byte[] ScriptBytes(EditSession edits, FileSystem files, string path, string? cutscene)
    {
        if (VfsPath.Comparer.Equals(path, cutscene) || edits.OpenCutscenes.Contains(path, VfsPath.Comparer))
        {
            var working = edits.Cutscene(path) ?? throw new InvalidOperationException(
                edits.CutsceneReadOnlyReason(path) ?? $"No cutscene {path}.");
            return Encoding.ASCII.GetBytes(ThnWriter.Write(working.ToGlobals()));
        }

        return files.ReadAllBytes(path);
    }

    /// <summary>
    /// An edit, an undo or a redo changed W. A change of a script of the plan builds the scene again. A change while a
    /// field edit or a drag is open is a step of a gesture, which builds once at its end (section 8.4).
    /// </summary>
    private void Changed(EditChange change)
    {
        UpdateUnsavedSystem();
        // An undo of New cutscene takes the open cutscene away, so the clock cannot build it.
        if (Path != null && change.Cutscenes.Contains(Path, VfsPath.Comparer) && session?.Edits.Cutscene(Path) == null)
        {
            window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource,
                $"Closed cutscene {Path}, because it does not exist now.");
            Close();
            return;
        }

        if (clock == null || Plan == null || !change.Cutscenes.Any(UsedByPlan))
        {
            return;
        }

        gesture = window.IsEditing;
        clock.Invalidate(gesture);
    }

    private bool UsedByPlan(string path) =>
        VfsPath.Comparer.Equals(path, Path) || VfsPath.Comparer.Equals(path, Plan!.SetScript) ||
        VfsPath.Comparer.Equals(path, Plan.Fidget) || Plan.Scripts.Contains(path, VfsPath.Comparer);

    /// <summary>The wall time that the catch-up still needs, from its rate since it started.</summary>
    private void Progress(double elapsed)
    {
        if (clock is not { CatchingUp: true })
        {
            CatchUpLeft = null;
            catchUpWall = 0;
            return;
        }

        if (catchUpWall == 0)
        {
            catchUpStart = clock.CatchUpTime;
        }

        catchUpWall += elapsed;
        var rate = (clock.CatchUpTime - catchUpStart) / catchUpWall;
        CatchUpLeft = rate > 0 ? TimeSpan.FromSeconds(Math.Max(0, clock.Target - clock.CatchUpTime) / rate) : null;
    }
}
