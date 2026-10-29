using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Dialogs;
using LibreLancer.Graphics;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.Shaders;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Assets;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Database;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Mods;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;
using OpenLancer.Runtime;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor;

/// <summary>
/// The OpenLancer editor window: a menu bar, the workspace panels (Hierarchy, Scene and Cutscene, Inspector, and
/// Assets, Database, Console, Timeline and Cutscenes at the bottom) and a status bar. While no profile is open, the Welcome
/// screen replaces the panels.
/// </summary>
/// <remarks>The edits of the active mod and their menus are in <c>EditorWindow.Editing.cs</c>. Play is in
/// <c>EditorWindow.Play.cs</c> and <see cref="PlayMenu"/>.</remarks>
internal sealed partial class EditorWindow : Game
{
    private const string AppTitle = "OpenLancer Editor";
    private const string DefaultSystem = "li01";

    /// <summary>The Console source of the messages of the editor itself.</summary>
    internal const string EditorSource = "Editor";
    private static readonly FileDialogFilters ProfileFilters = new(new FileFilter("OpenLancer profile", "json"));
    private static readonly float[] UiScales = [1, 1.25f, 1.5f, 1.75f, 2];

    private readonly EditorOptions options;
    private ImGuiHelper gui = null!;
    private EditorLayout layout = null!;
    private ConsolePanel console = null!;
    private HierarchyPanel hierarchy = null!;
    private InspectorPanel inspector = null!;
    private DatabasePanel database = null!;
    private AssetsPanel assets = null!;
    private WelcomeScreen welcome = null!;
    private StatusBar statusBar = null!;
    private CutsceneView cutsceneView = null!;
    private TimelinePanel timeline = null!;
    private CutsceneBrowser cutsceneBrowser = null!;
    private MissionsBrowser missionsBrowser = null!;
    private MissionView missionView = null!;
    private CutsceneBench? bench;
    private MissionBench? missionBench;
    private AgentRenderBench? agentBench;
    private readonly PlayMenu playMenu;

    // The base run of the Console: the engine lines of the base folder without mods (decision D516 of the console
    // filter design).
    private readonly BaseRunRunner baseRun = new();

    // The time since the last frame. The Scene panel reads it while the layout draws.
    private double frameElapsed;

    // The focus of the window in the last frame. When the window gets the focus, the prop groups read their folders
    // again, as Unity refreshes its assets (section 9.1 of the M23 design).
    private bool wasFocused;

    // True after a reload of the game data while a section of the open system waits for it (decision D282 of the M23
    // design). The system opens again when no field edit or drag runs.
    private bool reopenAfterReload;

    // The lists of the File menu. They come from the disk, so they are read when their submenu opens, not each frame.
    private List<string> menuProfiles = [];
    private HashSet<string> missingRecentProfiles = [];

    /// <param name="options">The command line.</param>
    public EditorWindow(EditorOptions options) : base(1600, 900, true)
    {
        this.options = options;
        // The bench must not change the settings of the user, so it does not read or write them.
        Settings = options.Bench ? EditorSettings.Transient() : EditorSettings.Load();
        // The bench checks no game folder and links nothing.
        Links = new GameLinks(this, enabled: !options.Bench);
        gameLinkWindow = new GameLinkWindow(this);
        newProfileDialog = new NewProfileDialog(this);
        Cutscenes = new CutsceneWorkspace(this);
        Missions = new MissionWorkspace(this);
        centerSelections = new CenterSelections(OwnerOf);
        Commands = new CommandRunner(this);
        Edits = new EditController(this);
        SceneEdits = new SceneEdits(this);
        AssetDrag = new AssetDrag(this);
        closePrompt = new ClosePrompt(this);
        Play = new PlayController(this, PlayProcess.Command, () => playClock.Elapsed);
        GamePlay = new GamePlay(this, GameProcess.Starter(this), DllStringResourceWriter.Instance, ThornEvaluator.Instance,
            () => playClock.Elapsed);
        playMenu = new PlayMenu(this);
        Mods = new ModsWindow(this);
        MakeAgent();
        // A test play changes the publish checklist of its mods.
        GamePlay.PlaysRecorded += Mods.Refresh;
        // The window gets this title when it is made. The shaders compile before the first frame, and the window
        // must not show the engine name during that time.
        Title = AppTitle;
    }

    public EditorSession? Session { get; private set; }
    public SceneView Scene { get; private set; } = null!;

    /// <summary>The open cutscene and its preview.</summary>
    public CutsceneWorkspace Cutscenes { get; }

    /// <summary>The mission scripts, the checks and the open mission (milestone M6a).</summary>
    public MissionWorkspace Missions { get; }

    // One kept selection for each tab of the center group (decision D550 of the M6a design).
    private readonly CenterSelections centerSelections;

    public EditorSettings Settings { get; }

    /// <summary>The exit code of the process: 1 after a bench with a failed script, else 0.</summary>
    public int ExitCode { get; set; }

    /// <summary>The messages of the Console.</summary>
    public ConsoleLog Log { get; } = new();

    /// <summary>
    /// The selected item: a system object, zone, light, ship, equipment, system, a section of an item file
    /// (<see cref="SectionRef"/>), or the open cutscene or an element of it (<see cref="CutsceneRoot"/> and
    /// <see cref="OpenLancer.Data.Thn.ThnRef"/>), or an item of a mission (<see cref="MissionRef"/>). The Scene, the
    /// Cutscene and the Mission tab each keep their own selection while another one shows (see
    /// <see cref="CenterSelections"/>).
    /// </summary>
    public object? Selection { get; set; }

    /// <summary>
    /// The shown tab of the center group: Scene, Cutscene or Mission (decision D550 of the M6a design). It is set at the
    /// start of each frame. In Cutscene mode, the Hierarchy shows the open cutscene (section 7.4 of the M8 design).
    /// </summary>
    public CenterMode CenterMode { get; private set; }

    /// <summary>The edits of the open profile, the saves and what follows a save.</summary>
    public EditController Edits { get; }

    /// <summary>The Mods window (milestone M11c): the mods of the profile and the workspace, with their publish
    /// settings, checks and exports.</summary>
    public ModsWindow Mods { get; }

    /// <summary>Create, Duplicate, Rename and Delete of the items of the open system (milestone M3).</summary>
    public SceneEdits SceneEdits { get; }

    /// <summary>The Assets panel (milestone M23).</summary>
    public AssetsPanel Assets => assets;

    /// <summary>The drag and drop of the tiles of the Assets panel and of the Hierarchy rows (milestone M23).</summary>
    public AssetDrag AssetDrag { get; }

    /// <summary>The folder of the cache of the thumbnails, in the folder of the product in LocalAppData. No setting
    /// changes it, so the cache never goes into a repository (decision D273 of the M23 design).</summary>
    private static string ThumbnailFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product.Name, "thumbnails");

    /// <summary>True while a field edit of the Inspector, a drag of a gizmo, a drag on the Timeline or a drag of a curve
    /// point is open. Undo, redo, the recovery file and the model loads of the scene wait for its end. A save ends it
    /// first (see <see cref="SaveCommand"/>).</summary>
    public bool IsEditing => inspector.IsEditing || Scene.IsDragging || timeline.IsDragging || cutsceneView.IsDragging ||
                             missionView.IsEditing;

    /// <summary>The dialog for the questions of the editor, for example Save, Discard or Cancel.</summary>
    public ChoiceDialog Choice { get; } = new();

    /// <summary>
    /// False while the game data loads or loads again after a save. The load uses the session resources, so the
    /// profile cannot close or change until the load ends.
    /// </summary>
    public bool CanChangeProfile => Session is not { IsBusy: true };

    /// <summary>The reason of an action that waits while <see cref="CanChangeProfile"/> is false.</summary>
    internal const string ProfileLoading = "The game data of the open profile is loading.";

    /// <summary>The error of the last profile that did not open, or null. The Welcome screen shows it.</summary>
    public string? OpenError { get; private set; }

    protected override void Load()
    {
        // The engine warnings and errors go to the Console from the start. FLLog gives each line to this window on the
        // UI thread. A later EngineLogCapture gives its lines back to this listener when it ends.
        FLLog.UIThread = this;
        FLLog.AppendLine = Log.AddEngineLine;
        AllShaders.Compile(RenderContext);
        ProgramIcon.Apply(this);
        gui = new ImGuiHelper(this, Math.Clamp(Settings.UiScale, UiScales[0], UiScales[^1]));
        // ImGui reads its state before the first frame. A settings file from a first start or from an older editor
        // has no state, and ImGui cannot read null text.
        if (!string.IsNullOrEmpty(Settings.ImGuiState))
        {
            ImGui.LoadIniSettingsFromMemory(Settings.ImGuiState);
        }

        var fonts = new FontManager();
        fonts.ConstructDefaultFonts();
        Services.Add(fonts);
        Services.Add(new CommandBuffer(RenderContext));
        Services.Add(new Billboards(RenderContext));
        Services.Add(new EditorRenderSettings());
        Scene = new SceneView(this);
        hierarchy = new HierarchyPanel(this);
        inspector = new InspectorPanel(this);
        database = new DatabasePanel(this);
        assets = new AssetsPanel(this, ThumbnailFolder);
        console = new ConsolePanel(this);
        cutsceneView = new CutsceneView(this, Cutscenes);
        timeline = new TimelinePanel(this, Cutscenes);
        cutsceneBrowser = new CutsceneBrowser(this, Cutscenes);
        missionsBrowser = new MissionsBrowser(this, Missions);
        missionView = new MissionView(this, Missions);
        // An older cimgui.dll lacks the exports of the raw INI editor. The Console then gets one info entry.
        RawIniEditor.FallbackShown += () => Log.Add(ConsoleSeverity.Info, EditorSource, RawIniEditor.NeedsNewNatives);
        layout = new EditorLayout(Settings,
            new EditorPanel(HierarchyPanel.PanelId, "Hierarchy", EditorIcons.List, hierarchy.Draw, hierarchy.Toolbar),
            new EditorPanelGroup("center",
            [
                new EditorPanel(SceneView.PanelId, "Scene", EditorIcons.Globe, () => Scene.Draw(frameElapsed), Scene.Toolbar,
                    padding: false),
                new EditorPanel(CutsceneView.PanelId, "Cutscene", EditorIcons.Film,
                    () => cutsceneView.Draw(frameElapsed), cutsceneView.Toolbar, padding: false),
                new EditorPanel(MissionWorkspace.PanelId, "Mission", EditorIcons.Route,
                    () => missionView.Draw(frameElapsed), missionView.Toolbar, padding: false,
                    tooltip: "The graph of the open mission, with the Scene beside it."),
            ]),
            new EditorPanel(InspectorPanel.PanelId, "Inspector", EditorIcons.InfoCircle, () => inspector.Draw(Selection),
                padding: false),
            new EditorPanelGroup("bottom",
            [
                new EditorPanel(AssetsPanel.PanelId, "Assets", EditorIcons.Images, assets.Draw, assets.Toolbar,
                    padding: false,
                    tooltip: "The assets of the game data and the mods, as the Unity Project window shows them."),
                new EditorPanel("database", "Database", EditorIcons.Database, () => database.Draw(Session?.GameData),
                    database.Toolbar, padding: false),
                new EditorPanel(ConsolePanel.PanelId, "Console", EditorIcons.Terminal, console.Draw, console.Toolbar,
                    padding: false, badges: console.Badges),
                new EditorPanel(TimelinePanel.PanelId, "Timeline", EditorIcons.Stream, timeline.Draw, timeline.Toolbar,
                    padding: false),
                new EditorPanel(CutsceneBrowser.PanelId, "Cutscenes", EditorIcons.Film, cutsceneBrowser.Draw,
                    cutsceneBrowser.Toolbar, padding: false),
                new EditorPanel(MissionWorkspace.BrowserId, "Missions", EditorIcons.Route, missionsBrowser.Draw,
                    missionsBrowser.Toolbar, padding: false,
                    tooltip: "The mission scripts of the game data and the mods. Double-click a mission to open it."),
            ]));
        statusBar = new StatusBar(this);
        welcome = new WelcomeScreen(this);

        if (options.Bench)
        {
            StartBench();
            return;
        }

        StartAgentWithEditor();

        // The first-run screen shows when the user has no game, no profile and no command line (decision D193). The
        // linked games get their check in the background.
        firstRunAtStart = GameLinks.FirstRunApplies(options, Settings, File.Exists);
        Links.CheckAll();

        // A wrong command line opens no profile, so that the Welcome screen shows the problem.
        if (options.Problem is { } problem)
        {
            OpenError = problem;
            Log.Add(ConsoleSeverity.Error, EditorSource, problem);
            return;
        }

        // The profile of the command line opens first, then the newest recent profile. Without either, the Welcome
        // screen shows.
        var newest = Settings.RecentProfiles.Count > 0 ? Settings.RecentProfiles[0].Path : null;
        var path = options.Profile ?? (File.Exists(newest) ? newest : null);
        if (path != null)
        {
            OpenProfile(path);
        }
    }

    /// <summary>
    /// Starts a bench: it opens the profile of the command line without an active mod, so the bench takes no lock and
    /// writes no file of a mod.
    /// </summary>
    private void StartBench()
    {
        if (options.BenchAgentRender is { } system)
        {
            agentBench = new AgentRenderBench(this, agentViews, system);
        }
        else if (options.BenchMissions.Count > 0)
        {
            missionBench = new MissionBench(this, missionView, options.BenchMissions);
        }
        else
        {
            bench = new CutsceneBench(this, Cutscenes, options.BenchCutscenes);
        }

        if (options.Profile == null)
        {
            FLLog.Error(CutsceneBench.Component, "The bench needs a profile file on the command line.");
            return;
        }

        var full = Path.GetFullPath(options.Profile);
        Settings.SetActiveMod(full, null);
        OpenProfile(full);
    }

    protected override void Cleanup()
    {
        // The agent calls run on this thread, so the agent server stops first. Its images that wait end then.
        agentController.Dispose();
        agentRender.Dispose();
        // The game shows the data of this window, so it ends with the window (decision D48).
        Play.SessionEnding(windowClose: true);
        Play.Dispose();
        // The game and the test server of the play copy end with the editor, through their jobs (decision D313).
        GamePlay.Dispose();
        // After a crash in Load, the ImGui context can be missing.
        if (gui != null)
        {
            Settings.ImGuiState = ImGui.SaveIniSettingsToMemory();
        }

        Settings.Save();
        // A close of the window asks about unsaved edits first (see ClosePrompt, roadmap decision D78). A crash comes
        // here without the question (see Game.Crashed). Then the unsaved edits go into the recovery file, and the next
        // open of the mod offers to restore them (decision D8). An open field edit or drag ends first, so that the
        // file keeps it. When no unsaved edits remain, for example after an undo, the write deletes the file.
        if (Session is { } session)
        {
            EndOpenEdits();
            if (session.RecoveryOutdated)
            {
                Edits.WriteRecovery(session);
            }
        }

        Edits.Dispose();
        Mods.Dispose();
        inspector?.Dispose();
        missionView?.Dispose();
        baseRun.Dispose();
        Cutscenes.Dispose();
        assets?.Dispose();
        Scene.Dispose();
        // A close of the window waits for the running load (see ClosePrompt). A crash, or "Close now" in the dialog of
        // that wait, can end the window while a load runs. The load fills the resources of the session, so the session
        // is not disposed then. The end of the process frees it, and closes the lock of the active mod as after a
        // crash.
        if (Session is { IsBusy: false } idle)
        {
            idle.Dispose();
        }
    }

    /// <summary>The user closes the window: the close button, Alt+F4 or the taskbar. With unsaved edits, the window
    /// stays open and the close prompt asks first (OL-0027). While the game data loads, the window closes when the load
    /// ends (see <see cref="ClosePrompt"/>).</summary>
    /// <remarks>A running test server asks first, so that FLServer can save its characters (decision D336). While the
    /// close waits for the load, a second close changes nothing, because the dialog of the wait offers "Close
    /// now".</remarks>
    protected override bool OnCloseRequested() =>
        !closePrompt.Waits && GamePlay.CloseRequested(() => closePrompt.Request()) && closePrompt.Request();

    /// <summary>Ends an open field edit or drag and keeps its change, before the window closes.</summary>
    private void EndOpenEdits()
    {
        inspector.EndEdit();
        Scene.EndDrag();
        timeline.EndDrag();
        cutsceneView.EndDrag();
        missionView.EndEdit();
    }

    /// <summary>Asks for a profile file and opens it.</summary>
    public void OpenProfileDialog()
    {
        FileDialog.Open(path =>
        {
            if (path != null)
            {
                OpenProfile(path);
            }
        }, ProfileFilters);
    }

    /// <summary>Hides the error of the last open on the Welcome screen.</summary>
    public void DismissOpenError()
    {
        OpenError = null;
        OpenErrorMissingBase = null;
        missingBaseProfile = null;
    }

    /// <summary>Sets the Hierarchy search, for example "t:zone" to show the zones of the open system.</summary>
    public void ShowInHierarchy(string search) => hierarchy.SetSearch(search);

    /// <summary>Makes the tab of a panel the selected tab of its group.</summary>
    public void ShowPanel(string panelId) => layout.Show(panelId);

    /// <summary>True when a panel shows in its group and the group has the focus.</summary>
    public bool IsFocused(string panelId) => layout.IsFocused(panelId);

    /// <summary>Shows an entity of the open cutscene in the Free mode of the Cutscene tab.</summary>
    public void FrameInCutscene(ThnRef entity)
    {
        layout.Show(CutsceneView.PanelId);
        cutsceneView.Frame(entity);
    }

    /// <summary>
    /// Shows the Console tab and selects an entry in it. The Console is part of the workspace, which shows only while
    /// a profile is open. Without a profile, this does nothing.
    /// </summary>
    public void ShowConsole(ConsoleEntry entry)
    {
        if (Session == null)
        {
            return;
        }

        console.Select(entry);
        layout.Show(ConsolePanel.PanelId);
    }

    /// <summary>
    /// Closes the open profile and opens another one. The engine data loads in the background. While the data of the
    /// open profile loads, this does nothing (see <see cref="CanChangeProfile"/>). With unsaved edits, a dialog asks
    /// first. When the profile does not open, <see cref="OpenError"/> tells why.
    /// </summary>
    public void OpenProfile(string path)
    {
        if (CanChangeProfile)
        {
            Leave("closing the profile", () => OpenNow(path));
        }
    }

    /// <summary>
    /// Opens a profile with the active mod of the settings: the mod that the user selected for it last time, or its
    /// last mod. The game data loads from the edit view: the base data with the mods up to the active mod (decision
    /// D2).
    /// </summary>
    private void OpenNow(string path)
    {
        if (!CanChangeProfile)
        {
            return;
        }

        CloseNow();
        DismissOpenError();
        // The edit session holds the lock of the active mod. A path that does not give it to a new EditorSession
        // disposes it, so that the lock does not stay until the garbage collector runs.
        EditSession? edits = null;
        try
        {
            var full = Path.GetFullPath(path);
            var profile = ModProfile.Load(full);
            var active = Settings.ActiveModOf(full, profile.Mods.Select(m => m.Id).ToList());
            edits = EditSession.Open(profile, active, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
            edits.TargetGame = TargetGameOf;
            foreach (var d in edits.OpenProblems.Concat(edits.EditView.Diagnostics))
            {
                Log.Add(d with { Origin = d.Origin ?? edits.MergeProblemOrigin(d) });
            }

            if (edits.EditView.HasErrors)
            {
                OpenError = $"The profile {profile.Name} has merge errors. It did not open. The Console shows the " +
                            "errors.";
                Log.Add(ConsoleSeverity.Error, EditorSource, OpenError);
                return;
            }

            Settings.AddRecentProfile(full, DateTime.UtcNow);
            Settings.Save();
            var fileSystem = edits.EditView.CreateFileSystem();
            var repo = edits.ActiveMod == null ? null : RepoCheck.RepoRoot(edits.ActiveMod.Folder);
            var session = new EditorSession(edits, new GameResourceManager(this, fileSystem), repo, this);
            Session = session;
            edits = null;
            Log.Origins = new EngineLineOrigins(session.Edits);
            Log.Add(ConsoleSeverity.Info, EditorSource,
                $"Opened profile {profile.Name} ({profile.Mods.Count} mods). " +
                (session.Edits.ActiveMod is not { } activeMod
                    ? "No mod is active, so the profile is read-only. "
                    : $"Active mod: {activeMod.Id}. ") +
                "Loading game data...");
            ModWarnings(session);
            Links.ProfileOpened(profile);
            Edits.OfferRecovery();
            // The engine lines of the load form a batch, which the Console compares with the base run.
            var batch = BeginConsoleBatch();
            session.Loads.Load(() =>
            {
                try
                {
                    var data = new GameDataManager(new GameItemDb(fileSystem), session.Resources);
                    data.LoadData(this);
                    return () => DataLoaded(session, data);
                }
                catch (Exception e)
                {
                    return () =>
                    {
                        session.LoadEnded(null);
                        Log.Add(ConsoleSeverity.Error, EditorSource, $"The game data did not load: {e.Message}");
                    };
                }
                finally
                {
                    ConsoleLog.EndBatch(batch);
                }
            });
        }
        catch (ModLoadException e) when (e.MissingBaseFolder is { } missingBase)
        {
            // The game folder moved or its drive is not there. Relink in the Game link window fixes the profile and opens
            // it again (decision D203).
            OpenError = $"OpenLancer cannot find the game folder of profile {Path.GetFileNameWithoutExtension(e.Path)}: " +
                        $"{missingBase}. If you moved the game, open Game link and click Relink.";
            OpenErrorMissingBase = missingBase;
            missingBaseProfile = e.Path;
            Log.Add(ConsoleSeverity.Error, EditorSource, OpenError);
            Links.Check(missingBase);
        }
        catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            OpenError = e.Message;
            Log.Add(ConsoleSeverity.Error, EditorSource, e.Message);
        }
        finally
        {
            edits?.Dispose();
        }
    }

    /// <summary>
    /// Closes the open profile and its system. While the data loads, this does nothing (see
    /// <see cref="CanChangeProfile"/>). With unsaved edits, a dialog asks first.
    /// </summary>
    public void CloseProfile()
    {
        if (Session != null && CanChangeProfile)
        {
            Leave("closing the profile", CloseNow);
        }
    }

    private void CloseNow()
    {
        if (Session == null || !CanChangeProfile)
        {
            return;
        }

        Edits.Stop();
        baseRun.Stop();
        // The game shows the data of one profile and one active mod, so it cannot follow another session (decision
        // D48). A change of the active mod and a new mod open the profile again, so they close it here too.
        Play.SessionEnding(windowClose: false);
        // The images of the agent and the system that they keep use the resources of the session, so they end before
        // the panels close.
        agentRender.Reset();
        // The panels forget the game data of the profile, so that it does not stay in memory while no profile is
        // open. The panels do not draw while the Welcome screen shows, so they cannot do this themselves.
        Scene.Close();
        Cutscenes.Reset();
        cutsceneView.Reset();
        timeline.Reset();
        cutsceneBrowser.Reset();
        missionsBrowser.Reset();
        missionView.Reset();
        Missions.Reset();
        centerSelections.Reset();
        hierarchy.Reset();
        inspector.Reset();
        database.Reset();
        assets.Reset();
        reopenAfterReload = false;
        Selection = null;
        Session.Dispose();
        Log.Origins = null;
        Log.Add(ConsoleSeverity.Info, EditorSource, $"Closed profile {Session.Profile.Name}.");
        Session = null;
    }

    /// <summary>
    /// Starts a batch of the engine lines of a known load of the open profile: the open, a reload after a save, or the
    /// load of a system in the Scene (section 3.4 of the console filter design). The Console compares the lines of the
    /// batch with the base run, so the base run starts first when the batch needs it and no run gave its result yet.
    /// End the batch with <see cref="ConsoleLog.EndBatch"/> after the load.
    /// </summary>
    internal string BeginConsoleBatch()
    {
        if (Session is { } session && Log.Origins is { NeedsBaseRun: true, CanResolve: false } && !baseRun.Running)
        {
            baseRun.Start(session.Profile.BaseFolder, this, result => BaseRunDone(session, result));
        }

        return ConsoleLog.BeginBatch();
    }

    /// <summary>Gives the result of the base run to the Console. A result of a session that closed is dropped.</summary>
    private void BaseRunDone(EditorSession session, BaseRunResult result)
    {
        if (Session != session)
        {
            return;
        }

        if (result.Counts is { } counts)
        {
            Log.BaseRunEnded(counts);
        }
        else
        {
            Log.BaseRunFailed(result.Failure!);
            Log.Add(ConsoleSeverity.Warning, EditorSource, BaseRunRunner.FailedText(result.Failure!));
        }
    }

    public void OpenSystem(StarSystem system)
    {
        if (Session?.GameData == null)
        {
            return;
        }

        // A model load reads the material map, which a reload makes again (review finding F5).
        if (Session.IsReloading)
        {
            Log.Add(ConsoleSeverity.Info, EditorSource, "The game data loads again after a save. Open the system after the load.");
            return;
        }

        Selection = null;
        Scene.Open(system, Session);
        Log.Add(ConsoleSeverity.Info, EditorSource, $"Opened system {Session.ItemName(system)} ({system.Nickname}).");
    }

    private void DataLoaded(EditorSession session, GameDataManager data)
    {
        session.LoadEnded(data);
        if (Session != session)
        {
            return;
        }

        Log.Add(ConsoleSeverity.Info, EditorSource,
            $"Game data loaded: {data.Items.Systems.Count()} systems, {data.Items.Ships.Count()} ships, " +
            $"{data.Items.Equipment.Count()} equipment.");
        // A bench opens no system of its own choice: the preview bench and the mission bench measure their views
        // alone, and the render bench opens the system of its command line.
        if (!options.Bench && data.Items.Systems.Get(DefaultSystem) is { } system)
        {
            OpenSystem(system);
        }

        // The checks of the mission scripts run after each load (section 12.3 of the M6a design). The cutscene bench
        // measures the preview alone, so it runs no check.
        if (!options.Bench || options.BenchMissions.Count > 0)
        {
            Missions.Run();
        }
    }

    protected override void Update(double elapsed) => Scene.Update(elapsed);

    /// <summary>When the window gets the focus, the prop groups and the Mods window read their folders again, as Unity
    /// refreshes its assets. So a group or a mod that another program wrote shows (section 9.1 of the M23 design and
    /// section 10.2 of the M11c design).</summary>
    private void RefreshOnFocus()
    {
        var focused = Focused;
        if (focused && !wasFocused)
        {
            if (Session is { } session)
            {
                PropGroups.Refresh(session.Edits);
            }

            Mods.Refresh();
        }

        wasFocused = focused;
    }

    /// <summary>
    /// Opens the open system again after a reload of the game data, when a section of it waited for the reload, for
    /// example an object of a new static prop archetype (decision D282 of the M23 design). The open waits until no
    /// field edit and no drag runs. The camera pose and the selection stay.
    /// </summary>
    private void ReopenAfterReload()
    {
        if (!reopenAfterReload || IsEditing || AssetDrag.IsDragging ||
            Session is not { IsBusy: false, GameData: { } data } session || Scene.System is not { } open)
        {
            return;
        }

        reopenAfterReload = false;
        if (data.Items.Systems.Get(open.Nickname) is not { } system)
        {
            return;
        }

        // A selected item of the old scene becomes its section, which the new scene maps to its new item. A selected
        // section of an object that waited for the reload, for example of a new static prop, gets its item too.
        var section = Selection switch
        {
            SystemObject or Zone or LightSource or Nebula or AsteroidField => Scene.SectionOf(Selection),
            SectionRef selected when selected.Path.Equals(Session.Edits.SystemPath(open.Nickname),
                StringComparison.OrdinalIgnoreCase) => selected,
            _ => null,
        };
        Scene.Open(system, session, keepCamera: true);
        if (section != null)
        {
            Scene.SelectWhenShown(section);
        }

        Log.Add(ConsoleSeverity.Info, EditorSource,
            $"Opened {system.Nickname} again with the game data of the save, so the Scene shows its new archetypes.");
    }

    protected override void Draw(double elapsed)
    {
        var frameStart = Stopwatch.GetTimestamp();
        RenderContext.ReplaceViewport(0, 0, Width, Height);
        RenderContext.ClearColor = EditorTheme.Backdrop;
        RenderContext.ClearAll();
        gui.NewFrame(elapsed);
        EditorTheme.ApplyIfScaleChanged();
        ImGui.PushFont(ImGuiHelper.Roboto, 0);

        RefreshOnFocus();
        ReopenAfterReload();
        // The marks follow the session of the open profile before an agent call can change it.
        AgentMarks.Track(Session?.Edits);
        AgentFollow.Track(Session?.Edits);
        // The session part of at most one agent call runs before the panels draw (decision D708 of the MCP1 design).
        agentController.Frame();
        agentRender.Frame();
        agentBench?.Update();
        // Before the panels draw, so that the Scene can apply the edits for Play also while it does not show.
        Play.BeforePanels(IsEditing);
        var menuHeight = MainMenu();
        var statusHeight = MathF.Truncate(StatusBar.Height * ImGuiHelper.Scale);
        frameElapsed = elapsed;
        CenterFrame(elapsed);
        var position = new Vector2(0, menuHeight);
        var size = new Vector2(Width, Height - menuHeight - statusHeight);
        var selectionBefore = Selection;
        var poseBefore = Scene.ViewPose;
        if (Session == null)
        {
            welcome.Draw(position, size);
        }
        else if (layout.Draw(position, size))
        {
            Settings.Save();
        }

        // A selection or a camera input of the user in the panels stops follow mode of the agent edits.
        WatchFollow(selectionBefore, poseBefore);

        statusBar.Draw(new Vector2(0, Height - statusHeight), Width);
        gameLinkWindow.Draw();
        Mods.Draw();
        DrawDialogs();
        Shortcuts();
        Edits.Update(IsEditing);
        // After the layout, so that a click on Play gets the edit of a field that the click ended (section 9.1).
        Play.Update(IsEditing, DialogOpen);
        // The stall notes of the live view come also while another tab shows (section 6.5 of the M6c design).
        Missions.CheckStalls();
        GamePlay.Update(IsEditing, DialogOpen);
        bench?.Update();

        ImGui.PopFont();
        gui.Render(RenderContext);
        // The mission bench measures the work of the frame, without the wait for the screen.
        missionBench?.Update(Stopwatch.GetElapsedTime(frameStart));

        // ImGui has no .ini file here, so it asks the editor to save its state, a few seconds after a change.
        var io = ImGui.GetIO();
        if (io.WantSaveIniSettings)
        {
            Settings.ImGuiState = ImGui.SaveIniSettingsToMemory();
            io.WantSaveIniSettings = false;
            Settings.Save();
        }

        // The title names the profile and the system. Set it only when it changes.
        var title = WindowTitle();
        if (title != Title)
        {
            Title = title;
        }
    }

    /// <summary>
    /// The center part of a frame, before the panels draw, with the tabs that showed in the last frame: the selection
    /// follows the center tab (decision D550 of the M6a design), and while the Cutscene tab or the Timeline shows, the
    /// preview reads its keys and runs its clock. So the Timeline plays the preview also while the Scene tab shows.
    /// While neither tab shows, the preview waits, and its sound stops.
    /// </summary>
    private void CenterFrame(double elapsed)
    {
        CenterMode = Session == null ? CenterMode.Scene
            : layout.IsShown(CutsceneView.PanelId) ? CenterMode.Cutscene
            : layout.IsShown(MissionWorkspace.PanelId) ? CenterMode.Mission
            : CenterMode.Scene;
        Cutscenes.ValidateSelection(CenterMode == CenterMode.Cutscene);
        Selection = centerSelections.Follow(CenterMode, Selection, ValidIn);
        if (CenterMode == CenterMode.Cutscene || layout.IsShown(TimelinePanel.PanelId))
        {
            Cutscenes.Keys(IsFocused(CutsceneView.PanelId) || IsFocused(TimelinePanel.PanelId));
            Cutscenes.Frame(elapsed);
        }
        else
        {
            Cutscenes.Wait();
        }
    }

    /// <summary>The tab of the center group that owns a selection: the Cutscene tab for the elements of a cutscene, the
    /// Mission tab for the items of a mission, or null for each other selection.</summary>
    private static CenterMode? OwnerOf(object? item) => item switch
    {
        _ when CutsceneWorkspace.IsCutsceneItem(item) => CenterMode.Cutscene,
        MissionRef => CenterMode.Mission,
        _ => null,
    };

    /// <summary>The kept selection of a tab when it comes back. The Cutscene tab gets a valid cutscene item or the root
    /// of the open cutscene. The Mission tab gets a valid mission item or the root of the open mission. The Scene gets
    /// its item. An item that no tab owns stays, for example a section of the Database.</summary>
    private object? ValidIn(CenterMode mode, object? item)
    {
        if (item != null && OwnerOf(item) != mode)
        {
            return item;
        }

        return mode switch
        {
            CenterMode.Cutscene => Cutscenes.Valid(item) ?? Cutscenes.Root,
            CenterMode.Mission => Missions.Valid(item) ?? Missions.Root,
            _ => item,
        };
    }

    /// <summary>
    /// Selects an item of a center tab: at once when the tab shows or the selection is an item of the tab, else when the
    /// tab shows next. The other tabs keep their selections.
    /// </summary>
    public void SelectInTab(CenterMode mode, object item)
    {
        if (CenterMode == mode || OwnerOf(Selection) == mode)
        {
            Selection = item;
        }

        centerSelections.Park(mode, item);
    }

    /// <summary>
    /// "OpenLancer Editor - sample [proof]* - New York (Li01)", without the parts that are not open. The brackets name
    /// the active mod, and the star shows unsaved edits.
    /// </summary>
    private string WindowTitle()
    {
        var title = AppTitle;
        if (Session != null)
        {
            var mod = Session.Edits.ActiveMod?.Id ?? "read-only";
            title += $" - {Session.Profile.Name} [{mod}]{(Session.Edits.IsDirty ? "*" : "")}";
        }

        if (Scene.System is { } system)
        {
            var name = Session?.ItemName(system);
            title += string.IsNullOrEmpty(name) ? $" - {system.Nickname}" : $" - {name} ({system.Nickname})";
        }

        return title;
    }

    private float MainMenu()
    {
        ImGui.BeginMainMenuBar();
        EditorTheme.PushMenuColors();
        if (ImGui.BeginMenu("File"))
        {
            FileMenu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            EditMenu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Mod"))
        {
            ModMenu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Play"))
        {
            playMenu.Menu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Agent"))
        {
            agentMenu.Menu();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            ViewMenu();
            ImGui.EndMenu();
        }

        playMenu.Buttons();
        EditorTheme.PopMenuColors();
        var height = ImGui.GetWindowHeight();
        ImGui.EndMainMenuBar();
        return height;
    }

    private void FileMenu()
    {
        var loading = !CanChangeProfile;
        if (Theme.BeginIconMenu(EditorIcons.Open, "Open profile"))
        {
            if (ImGui.IsWindowAppearing())
            {
                menuProfiles = ProfileFiles(Settings.WorkspaceOrDefault);
            }

            foreach (var path in menuProfiles)
            {
                if (Theme.IconMenuItem(EditorIcons.LayerGroup, Path.GetFileNameWithoutExtension(path), !loading))
                {
                    OpenProfile(path);
                }

                ImGui.SetItemTooltip(loading ? ProfileLoading : path);
            }

            if (menuProfiles.Count == 0)
            {
                ImGui.MenuItem($"No profiles in {Path.Combine(Settings.WorkspaceOrDefault, ModWorkspace.ProfilesFolder)}",
                    false);
            }

            ImGui.EndMenu();
        }

        if (Theme.BeginIconMenu(EditorIcons.Folder, "Open recent"))
        {
            if (ImGui.IsWindowAppearing())
            {
                missingRecentProfiles = Settings.RecentProfiles.Where(r => !File.Exists(r.Path)).Select(r => r.Path)
                    .ToHashSet();
            }

            foreach (var recent in Settings.RecentProfiles)
            {
                var exists = !missingRecentProfiles.Contains(recent.Path);
                if (Theme.IconMenuItem(EditorIcons.LayerGroup, Path.GetFileNameWithoutExtension(recent.Path),
                        exists && !loading))
                {
                    OpenProfile(recent.Path);
                }

                ImGui.SetItemTooltip(loading ? ProfileLoading
                    : exists ? recent.Path
                    : $"The file is not found.\n{recent.Path}");
            }

            if (Settings.RecentProfiles.Count == 0)
            {
                ImGui.MenuItem("No recent profiles", false);
            }

            ImGui.EndMenu();
        }

        if (Theme.IconMenuItem(EditorIcons.FileImport, "Open profile file...", !loading))
        {
            OpenProfileDialog();
        }

        if (loading)
        {
            ImGui.SetItemTooltip(ProfileLoading);
        }

        if (Theme.IconMenuItem(EditorIcons.Link, "Game link...", true))
        {
            ShowGameLink();
        }

        ImGui.SetItemTooltip("Shows the linked game folders, their versions and their profiles.");
        if (Theme.IconMenuItem(EditorIcons.X, "Close profile", Session != null && !loading))
        {
            CloseProfile();
        }

        if (loading)
        {
            ImGui.SetItemTooltip(ProfileLoading);
        }

        ImGui.Separator();
        SaveMenuItem();
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.DoorOpen, "Exit", true))
        {
            closePrompt.Request();
        }
    }

    private void ViewMenu()
    {
        if (Theme.BeginIconMenu(EditorIcons.MagnifyingGlass, "UI scale"))
        {
            foreach (var scale in UiScales)
            {
                if (ImGui.MenuItem($"{scale * 100:0}%", "", ImGuiHelper.UserScale == scale))
                {
                    // ImGuiHelper applies the new scale at the start of the next frame. Then EditorTheme writes
                    // the style again.
                    ImGuiHelper.UserScale = scale;
                    Settings.UiScale = scale;
                    Settings.Save();
                }
            }

            ImGui.EndMenu();
        }

        var rendered = Settings.Thumbnails == ThumbnailMode.Rendered;
        if (ImGui.MenuItem($"{EditorIcons.Images}   Thumbnails: Rendered", "", rendered))
        {
            Settings.Thumbnails = rendered ? ThumbnailMode.IconsOnly : ThumbnailMode.Rendered;
            Settings.Save();
        }

        ImGui.SetItemTooltip("On: the Assets panel renders an image of the model of each tile. Off: each tile shows the " +
                             "glyph of its kind, and a hover shows the image.");
        if (Theme.IconMenuItem(EditorIcons.Table, "Reset layout", true))
        {
            Settings.HierarchyWidth = EditorSettings.DefaultHierarchyWidth;
            Settings.AssetsTreeWidth = EditorSettings.DefaultAssetsTreeWidth;
            Settings.InspectorWidth = EditorSettings.DefaultInspectorWidth;
            Settings.BottomHeight = EditorSettings.DefaultBottomHeight;
            Settings.DatabaseCategoryWidth = EditorSettings.DefaultDatabaseCategoryWidth;
            Settings.ConsoleDetailHeight = EditorSettings.DefaultConsoleDetailHeight;
            Settings.Save();
        }

        ImGui.SetItemTooltip("Sets the panel sizes to their defaults.");
    }

    /// <summary>
    /// The profile files of the profiles folder of the workspace folder (decision D191), and of a developer workspace:
    /// the GameData\profiles folder, found from the editor's folder. A file that is in both lists shows once.
    /// </summary>
    internal static List<string> ProfileFiles(string workspace)
    {
        var folders = new List<string> { Path.Combine(workspace, ModWorkspace.ProfilesFolder) };
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var profiles = Path.Combine(dir.FullName, "GameData", "profiles");
            if (Directory.Exists(profiles))
            {
                folders.Add(profiles);
                break;
            }
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return folders.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(Path.GetFullPath(folder), "*.json"))
            .Distinct(comparer)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
