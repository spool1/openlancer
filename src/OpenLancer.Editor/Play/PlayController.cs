using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>The state of Play, for the status bar and the Stop button (section 11.3 of the M4 design).</summary>
internal enum PlayStatus
{
    /// <summary>No game runs.</summary>
    Idle,

    /// <summary>The game loads its data, or it starts the play after the start command.</summary>
    Starting,

    /// <summary>The game waits for the validation of its save.</summary>
    Validating,

    /// <summary>The player is in the game.</summary>
    Running,

    /// <summary>The game loads the files of a save again and continues at the place of the player (section 12).</summary>
    Reloading,

    /// <summary>The game did not load its data or end a reload in time. Stop ends it at once.</summary>
    NotResponding,

    /// <summary>The editor stopped the game and waits for its exit.</summary>
    Stopping,
}

/// <summary>
/// Play in the editor (section 9 of the M4 design). Play saves the active mod, starts the game process at once, and
/// lets the player in when the validation of the save passes. Stop ends the game, and the game ends with the session.
/// </summary>
/// <remarks>
/// <para>A request waits for the end of the frame and for the end of an open field edit, drag or dialog, so that the
/// save has the edit of the field (section 9.1). A request whose start check reads the Scene also waits until the Scene
/// shows each edit (section 9.5): a start at the camera, and a mission play whose start place can be in the Scene.
/// Before the panels draw, Play lets the Scene apply the edits, also while the Scene does not show.</para>
/// <para>The gate (section 9.3): the validation of the gate save decides. The gate save is the save whose files the game
/// reads. Errors stop the game at once (D16). The start command waits for the waiting event of the game: until then,
/// the game reads the mod files, and a save in that time makes the newest save the gate save (the read window, D51).
/// When the validation cannot give a result, because it could not run or because a save after the read window stopped
/// it, Play goes on with a warning (D43, D58). A gate save that is not complete gets no validation, and it stops the
/// game (D59).</para>
/// <para>The reload trigger (section 12.1, D17, D52): after the player arrived, a save whose validation passes reloads
/// the running game. One reload runs at a time. A save while the game reads the mod files makes the files of the game
/// a mix of two saves, so the game then gets the newest save again.</para>
/// <para>A mission play (section 5.6 of the M6c design) prepares its start save and its test layer after the save. After
/// the player arrived, a save whose validation passes starts the mission again from its start (restart, section 5.8),
/// in place of a reload. A new mission play while a mission play runs with the player in the game starts the game again
/// at the new start. The game gives the trigger changes of its mission to the editor (section 6.2).</para>
/// <para>Each request gives its result when it ends: the play started, or the reason why it did not (section 7.2 of the
/// M6c design). The agent tools read it. The keys and the menus do not need it.</para>
/// <para>The controller talks to the editor only through <see cref="IPlayEditor"/> (D50), and it reads the time from a
/// clock that the tests replace.</para>
/// </remarks>
internal sealed class PlayController : IDisposable
{
    public const string Source = "Play";

    /// <summary>The time that the game has to load its data and send the waiting event (section 9.10).</summary>
    internal static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(60);

    /// <summary>The time that the game has to exit after stop or after an error event.</summary>
    internal static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(5);

    /// <summary>The time that the window close waits for the exit of the game before it closes the job.</summary>
    internal static readonly TimeSpan CloseLimit = TimeSpan.FromSeconds(2);

    /// <summary>The time that the game has to end a reload with reloaded or reload-failed (section 9.10).</summary>
    internal static readonly TimeSpan ReloadLimit = TimeSpan.FromSeconds(30);

    private readonly IPlayEditor editor;
    private readonly Func<PlayArgs, ProcessStartInfo> command;
    private readonly Func<TimeSpan> clock;

    // The request that waits for the end of the frame, or for the exit of the running game (section 9.9).
    private PlayRequest? pending;

    // The game from its start to its exit.
    private GameRun? game;

    // A game that crashed while the validation of its save ran. Its report waits for the result (decision D55).
    private Crash? crash;

    // The last save of the session and the time when the controller saw it first. Update runs in each frame, so the
    // time is the time of the save within one frame. A reload gives the time from the save.
    private (int Save, TimeSpan At) lastSave;

    /// <param name="command">Gives the program and the arguments of a play. The tests give a stub program.</param>
    /// <param name="clock">Gives the time for the watchdogs and for the time from Play to flight.</param>
    public PlayController(IPlayEditor editor, Func<PlayArgs, ProcessStartInfo> command, Func<TimeSpan> clock)
    {
        this.editor = editor;
        this.command = command;
        this.clock = clock;
        editor.Validated += OnValidated;
    }

    public PlayStatus Status => game switch
    {
        null => crash != null ? PlayStatus.Validating : PlayStatus.Idle,
        { Ending: true } => PlayStatus.Stopping,
        { NotResponding: true } => PlayStatus.NotResponding,
        { Reload: not null } => PlayStatus.Reloading,
        { Spawned: true } => PlayStatus.Running,
        { StartSent: false } run when GateValidationRuns(run) => PlayStatus.Validating,
        _ => PlayStatus.Starting,
    };

    /// <summary>
    /// The text of the Play segment of the status bar (section 11.3 of the M4 design), or null when no game runs. For
    /// example "Play: validating save 2..." or "Playing: New York (Li01), save 2".
    /// </summary>
    public string? StatusText => Status switch
    {
        PlayStatus.Starting => "Play: starting...",
        PlayStatus.Validating => $"Play: validating {EditController.SaveText(game is { } run ? GateSave(run) : crash!.Save)}...",
        // Without a save of the session, the text names no save.
        PlayStatus.Running => game is { Whole: true, Shown: 0 }
            ? $"Playing: {game.Place}"
            : $"Playing: {game!.Place}, {Shown(game)}",
        PlayStatus.Reloading when game!.Reload!.Restart => "Play: starting the mission again...",
        PlayStatus.Reloading => $"Play: reloading {EditController.SaveText(game!.Reload!.Save)}...",
        PlayStatus.NotResponding => "Play: the game does not respond",
        PlayStatus.Stopping => "Play: stopping...",
        _ => null,
    };

    /// <summary>
    /// Why Reload game cannot run now, or null when it can. It runs while the player is in the game, no reload runs, the
    /// game sent no error event, and the last save of the session is complete.
    /// </summary>
    /// <remarks>The play load refuses the files of a save that is not complete, because its journal stays in the mod.
    /// The save that completes it keeps its number, and the reload trigger then sends it (review of step 8).</remarks>
    public string? ReloadBlock =>
        Status != PlayStatus.Running ? "Available while the player is in the game and no reload runs."
        : game!.Error != null ? "The game sent an error event, and it closes."
        : editor.Session is { } session && LastSaveIncomplete(session)
            ? $"Save {session.Saves} is not complete, so the game cannot load the mod files. Correct the problem of " +
              "the file, then save again."
            : null;

    /// <summary>True when Reload game can run (see <see cref="ReloadBlock"/>).</summary>
    public bool CanReload => ReloadBlock == null;

    /// <summary>The mission start of the game that runs, or null for a play from the camera or at a base. During a
    /// restart, it stays the start that the game runs until the game answers restarted.</summary>
    public IMissionStart? Mission => game?.Mission;

    /// <summary>The mission start whose mission the game loads now: the start of a restart that the game did not end,
    /// else the start of the running game. The setup triggers of its test layer are in the mission events of the
    /// load.</summary>
    public IMissionStart? LoadingMission => game?.Restarting ?? game?.Mission;

    /// <summary>
    /// The mission starts that Play keeps: the start of a request that waits, the start of the running game, the start of
    /// a restart that the game did not end, and a new start that waits for its restart. A trigger rename moves each of
    /// them (section 5.2 of the M6c design), so a later restart finds the trigger.
    /// </summary>
    public IReadOnlyList<IMissionStart> MissionStarts =>
        new[] { pending?.Mission, game?.Mission, game?.Restarting, game?.Wanted?.Mission }
            .OfType<IMissionStart>().Distinct().ToList();

    /// <summary>
    /// Why Restart the mission cannot run now, or null when it can: a mission play runs, the player is in the game, and
    /// the game sent no error event.
    /// </summary>
    public string? RestartBlock =>
        game is not { Mission: not null } ? "Available while a mission play runs."
        : !CanRestart(game) ? "Available while the player is in the game."
        : null;


    /// <summary>
    /// Asks for a mission play (section 5.6 of the M6c design). It runs at the next <see cref="Update"/>, as
    /// <see cref="Request"/> does. While a mission play runs and the player is in the game, the game starts again at the
    /// new start (section 5.8). Else a running game closes first.
    /// </summary>
    /// <param name="package">The ship package, or null for the package of a new multiplayer character (D15).</param>
    /// <param name="save">What the save of Play does (user decision of 2026-10-20 of the M6c design).</param>
    /// <returns>The result of the request, when the play started or did not start. A request that starts the running
    /// game again gives its result when the game answers.</returns>
    public Task<PlayRequestResult> RequestMission(IMissionStart mission, string? package,
        PlaySaveRule save = PlaySaveRule.Save) =>
        Replace(new PlayRequest(null, package, clock()) { Mission = mission, Save = save });

    /// <summary>Play > Restart the mission (section 5.8 of the M6c design): the running mission play starts again from
    /// its start, with the current save. Play does not save, so the game uses the saved files. It does nothing when
    /// <see cref="RestartBlock"/> gives a reason.</summary>
    public void RestartMission()
    {
        if (RestartBlock == null)
        {
            RequestMission(game!.Mission!, game.Args.Package, PlaySaveRule.None);
        }
    }

    /// <summary>True when <see cref="Stop"/> ends the game process at once: the game does not respond, or it did not
    /// exit in 5 s after stop. The Stop button is then Force stop.</summary>
    public bool ForceStop => game is { Killed: false } run &&
                             (run.NotResponding || run.StopSentAt is { } sent && clock() - sent >= ExitLimit);

    /// <summary>Asks for a play. It runs at the next <see cref="Update"/> without an open field, drag or dialog. A
    /// running game stops first.</summary>
    /// <param name="start">The start. A start in space is the camera of Play from camera. The start check of section 9.5
    /// moves it when the game starts.</param>
    /// <param name="package">The ship package, or null for the package of a new multiplayer character (D15).</param>
    /// <param name="save">What the save of Play does (user decision of 2026-10-20 of the M6c design).</param>
    /// <param name="origin">Who asks. The start check of a start in space of an agent names the start point, not the
    /// camera.</param>
    /// <returns>The result of the request, when the play started or did not start.</returns>
    public Task<PlayRequestResult> Request(PlayStart start, string? package, PlaySaveRule save = PlaySaveRule.Save,
        EditOrigin origin = EditOrigin.User) =>
        Replace(new PlayRequest(start, package, clock()) { Save = save, Origin = origin });

    /// <summary>Stops the game, or ends its process at once when <see cref="ForceStop"/> is true. A request that waits
    /// is dropped.</summary>
    public void Stop()
    {
        DropRequests("Stop ended the play request, so the play did not start.");
        ReportCrashNow();
        if (game is not { Killed: false } run)
        {
            return;
        }

        if (ForceStop)
        {
            Kill(run, "The game did not respond, so the editor ended it.");
        }
        else if (run.StopSentAt == null)
        {
            SendStop(run);
        }
    }

    /// <summary>
    /// Play > Reload game (decision D45): the running game loads the mod files again, with the number of the last save
    /// of the session. It is for a change outside the editor, and it sends the reload also when the validation of that
    /// save found errors. It does nothing when <see cref="CanReload"/> is false.
    /// </summary>
    public void Reload()
    {
        if (CanReload)
        {
            SendReload(game!, editor.Session?.Saves ?? game!.Shown, savedAt: null);
        }
    }

    /// <summary>Runs the watchdogs, the reload trigger and a request that waits. Call it once each frame, after the
    /// layout.</summary>
    /// <param name="editing">True while a field edit or a drag is open.</param>
    /// <param name="dialogOpen">True while a dialog is open.</param>
    public void Update(bool editing, bool dialogOpen)
    {
        NoteSave();
        if (game != null)
        {
            Watch(game);
            // A save stops a running validation and gives no event, so the gate and the reload trigger look each frame.
            CheckGate(game);
            CheckReload(game);
        }

        // A later save can stop the validation that a crash report waits for, with no result.
        ResolveCrash();
        if (pending is not { } request || editing || dialogOpen)
        {
            return;
        }

        // The start check reads the Scene. So the request waits until the Scene shows each edit, also the edit of a field
        // that the click on Play ended (section 9.5). BeforePanels lets the Scene apply the edits in the next frame.
        var sceneBehind = ReadsScene(request) && editor.SceneBehind;
        if (game is { } running)
        {
            // A mission play while a mission play runs starts the game again at the new start (section 5.8 of the M6c
            // design). The process stays.
            if (request.Mission != null && running.Mission != null && CanRestart(running))
            {
                if (sceneBehind)
                {
                    return;
                }

                pending = null;
                Restart(running, request);
                return;
            }

            // A second Play: the running game exits first (section 9.9). The request runs after the exit.
            if (!running.Ending)
            {
                Log(ConsoleSeverity.Info, "Play closes the running game first.");
                SendStop(running);
            }

            return;
        }

        if (sceneBehind)
        {
            return;
        }

        pending = null;
        Finish(request, Begin(request));
    }

    /// <summary>
    /// Lets the Scene apply each edit for a request whose start check reads the Scene (section 9.5). The Scene applies
    /// the edits only while it draws, so without this step such a request waits without end while the Scene does not
    /// show, for example while the Mission tab hides its Scene part. When the Scene still cannot show each edit, because a
    /// new model waits for the load of the game data, the Console tells once why Play waits. Call it once each frame,
    /// before the panels draw.
    /// </summary>
    /// <param name="editing">True while a field edit or a drag is open. The request waits for its end first.</param>
    public void BeforePanels(bool editing)
    {
        if (editing || pending is not { } request || !ReadsScene(request) || !editor.SceneBehind)
        {
            return;
        }

        editor.CatchUpScene();
        if (editor.SceneBehind && !request.SceneWaitTold)
        {
            request.SceneWaitTold = true;
            Log(ConsoleSeverity.Info, SceneWaitText);
        }
    }

    /// <summary>The note of a request that waits until the Scene shows each edit.</summary>
    internal const string SceneWaitText = "Play waits until the Scene shows each edit, because the start check of Play " +
                                          "reads the Scene. The Scene shows new models when the load of the game data " +
                                          "ends.";

    /// <summary>True when the start check of a request reads the Scene: Play from camera, and a mission play whose start
    /// place can be the Scene camera or a point in the system that the Scene shows.</summary>
    private static bool ReadsScene(PlayRequest request) =>
        request.Start is PlayStart.InSpace || request.Mission is { ReadsScene: true };

    /// <summary>
    /// Ends the game because the session ends: a profile close, a change of the active mod or a new mod, or the window
    /// close. The game shows the data of one profile and one active mod, so it cannot follow another session (D48).
    /// </summary>
    /// <param name="windowClose">True when the editor window closes. Then the game gets the end of its input and at
    /// most 2 s to exit, and then the job ends it. Else the game gets stop.</param>
    public void SessionEnding(bool windowClose)
    {
        DropRequests("The profile closed, so the play did not start.");
        // The validation of the session stops, so a crash report cannot wait for it.
        ReportCrashNow();
        if (game is not { } run)
        {
            return;
        }

        if (!windowClose)
        {
            if (!run.Ending)
            {
                SendStop(run);
            }

            return;
        }

        game = null;
        run.Process.CloseInput();
        run.Process.WaitForExit(CloseLimit);
        run.Process.Dispose();
    }

    /// <summary>Ends the game process, because the job closes.</summary>
    public void Dispose()
    {
        DropRequests("The editor closed, so the play did not start.");
        editor.Validated -= OnValidated;
        game?.Process.Dispose();
        game = null;
    }

    private PlayRequestResult Begin(PlayRequest request)
    {
        ReportCrashNow();
        if (editor.Session is not { } session)
        {
            Log(ConsoleSeverity.Warning, NoProfile);
            return Refused("NoProfile", NoProfile);
        }

        // Play writes its config and saves next to the profile file, so a profile file in the game folder would make it
        // write into the game folder (decision D214).
        if (session.BaseFolder != null && GameFolders.Overlaps(session.ProfilePath, session.BaseFolder))
        {
            Log(ConsoleSeverity.Error, GameTexts.PlayRefused);
            return Refused("ProfileInGameFolder", GameTexts.PlayRefused);
        }

        // The start check of Play from camera (section 9.5, D40). A save does not change the Scene, so the check runs
        // before the save, and a system that the Scene does not show now stops Play before it saves.
        var start = request.Start;
        SpawnCheck? check = null;
        if (start is PlayStart.InSpace camera)
        {
            var agent = request.Origin == EditOrigin.Agent;
            if (editor.SpawnShapes(camera.System) is not { } shapes)
            {
                var message = agent
                    ? $"Agent Play stopped: the Scene does not show {SystemText(camera.System)} now, so Play cannot " +
                      "check the start point. Open the system, then start Play again."
                    : "Play from camera stopped: the Scene does not show " +
                      $"{SystemText(camera.System)} now, so Play cannot check the start at " +
                      "the camera. Open the system, then press Play.";
                Log(ConsoleSeverity.Warning, message);
                return Refused("NoSceneSystem", message);
            }

            check = agent ? SpawnPoint.FromAgentPoint(camera, shapes) : SpawnPoint.FromCamera(camera, shapes);
            start = check.Start;
        }

        if (SaveForPlay(session, request.Save) is { } saveError)
        {
            return new PlayRequestResult(Status) { Error = saveError };
        }

        // A mission play makes its start from the model of the saved script (section 5.6 of the M6c design).
        if (request.Mission is { } mission)
        {
            if (mission.Prepare() is not { } fromSave)
            {
                var reason = mission.Refusal ?? NoStart;
                Log(ConsoleSeverity.Warning, MissionPlayTexts.Refused(reason));
                return Refused("StartRefused", reason);
            }

            start = fromSave;
        }

        session = editor.Session ?? session;
        var args = new PlayArgs(session.ProfilePath, session.ActiveMod, session.Saves, start!, request.Package);
        // The process gives its events on the UI thread after this method, so the callbacks find the run.
        GameRun? run = null;
        PlayProcess process;
        try
        {
            process = PlayProcess.Start(command(args), editor, e => OnEvent(run!, e), line => OnLogLine(run!, line),
                (code, errors) => OnExit(run!, code, errors));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            var message = $"Play could not start the game: {e.Message}";
            Log(ConsoleSeverity.Error, message);
            return Refused("ProcessStart", message);
        }

        run = new GameRun(process, args, request.Time, clock()) { Lines = editor.Log.BeginGameRun(), Mission = request.Mission };
        game = run;
        editor.PlayStarted();
        if (request.Mission is { } started)
        {
            run.Lines.Layer = started.SetupTriggers;
            StartNotes(started);
        }

        // The lines of the start check belong to a game that started.
        foreach (var (severity, message) in check?.Lines ?? [])
        {
            Log(severity, message);
        }

        if (session.LaterMods.Count > 0)
        {
            Log(ConsoleSeverity.Warning, $"Play does not include the later mods: {string.Join(", ", session.LaterMods)}.");
        }

        CheckGate(run);
        return new PlayRequestResult(Status);
    }

    /// <summary>The start note of a mission play, and the note of an approximate start (section 8.4 of the M6c
    /// design).</summary>
    private void StartNotes(IMissionStart mission)
    {
        Log(ConsoleSeverity.Info, MissionPlayTexts.Start(mission));
        if (mission.Approximate)
        {
            Log(ConsoleSeverity.Info, MissionPlayTexts.Approximate);
        }
    }

    /// <summary>True when a mission play can start again: the player is in the game, the game sent no error event,
    /// and it responds.</summary>
    private static bool CanRestart(GameRun run) => run.Spawned && !run.Ending && run.Error == null && !run.NotResponding;

    /// <summary>
    /// A new mission play while a mission play runs (section 5.8 of the M6c design): Play saves the active mod, and the
    /// game starts again at the new start. A save with new files waits for its validation, as a reload does. Without new
    /// files, the restart goes at once. A newer request takes the place of a request that waits for its restart.
    /// Restart the mission does not save.
    /// </summary>
    private void Restart(GameRun run, PlayRequest request)
    {
        if (editor.Session is not { } session)
        {
            Log(ConsoleSeverity.Warning, NoProfile);
            Finish(request, Refused("NoProfile", NoProfile));
            return;
        }

        if (SaveForPlay(session, request.Save) is { } saveError)
        {
            Finish(request, new PlayRequestResult(Status) { Error = saveError });
            return;
        }

        if (run.Wanted is { } earlier)
        {
            Finish(earlier, Refused("Replaced", ReplacedText));
        }

        run.Wanted = request;
        CheckReload(run);
    }

    /// <summary>The save of Play for each case of section 9.2. Returns the error when Play stops, else null.</summary>
    /// <param name="rule">What the save does. An agent request that nobody allowed to save refuses edits that came after
    /// the request (user decision of 2026-10-20 of the M6c design).</param>
    private EditError? SaveForPlay(PlaySession session, PlaySaveRule rule)
    {
        if (rule == PlaySaveRule.None)
        {
            return null;
        }

        if (session.ActiveMod is not { } mod)
        {
            Log(ConsoleSeverity.Info, "No mod is active, so Play uses the saved files of all mods of the profile.");
            return null;
        }

        // Another editor has the mod open, a recovery file waits, or the edits of a repo mod are not allowed (D5). The
        // session then has no unsaved edits that it can save.
        if (session.NoEditReason is { } reason)
        {
            Log(ConsoleSeverity.Info, $"Play uses the saved files of mod '{mod}'. The editor takes no edits for it now: {reason}");
            return null;
        }

        // The agent play rule asked at the request, or it did not need to ask. Edits after the request need a new
        // question.
        if (rule == PlaySaveRule.NoEdits && session.Unsaved)
        {
            var message = PlayRequests.EditsWhileWaiting(mod);
            Log(ConsoleSeverity.Warning, message);
            return new EditError(EditErrorKind.Refused, PlayRequests.NotSavedDetail, message);
        }

        var outcome = editor.Save();
        if (!outcome.Saved)
        {
            Log(ConsoleSeverity.Error, "Play stopped: the save did not complete. The Console tells why.");
            return outcome.Failure switch
            {
                SaveFailure.Blocked => new EditError(EditErrorKind.SaveBlocked, "SaveBlocked",
                    $"The save gate found errors in mod '{mod}', so Play did not save and did not start. The Console " +
                    "lists the errors."),
                SaveFailure.ChangedOnDisk => new EditError(EditErrorKind.ChangedOnDisk, "ChangedOnDisk",
                    $"Files of mod '{mod}' changed outside the editor, so Play did not save and did not start. The " +
                    "editor asks the user what to do with the files."),
                _ => new EditError(EditErrorKind.Refused, "NotSaved",
                    "The save did not complete, so Play did not start. The Console tells why."),
            };
        }

        if (outcome.Incomplete)
        {
            Log(ConsoleSeverity.Error, $"Play stopped: save {outcome.Number} is not complete.");
            return new EditError(EditErrorKind.Refused, "SaveIncomplete",
                $"Save {outcome.Number} is not complete, so Play did not start. The Console tells why.");
        }

        if (outcome.Number > 0)
        {
            Log(ConsoleSeverity.Info, $"Play: saved mod '{mod}' (save {outcome.Number}). Waiting for its validation.");
        }

        return null;
    }

    /// <summary>
    /// The save whose files the game reads. Until the waiting event, the game reads the mod files, so a save in that
    /// time makes the newest save the gate save (D51). After the waiting event, the gate save stays.
    /// </summary>
    private int GateSave(GameRun run) =>
        run.Waited ? run.GateSave : Math.Max(run.GateSave, editor.Session?.Saves ?? 0);

    private bool GateValidationRuns(GameRun run) =>
        editor.Validation is { Running: true } validation && validation.Save == GateSave(run);

    /// <summary>
    /// The gate of section 9.3. Errors of the validation of the gate save stop the game at once. A passed validation,
    /// or a validation without a result, sends start after the waiting event. A gate save that is not complete stops
    /// the game (D59).
    /// </summary>
    private void CheckGate(GameRun run)
    {
        if (run.StartSent || run.Ending)
        {
            return;
        }

        var save = GateSave(run);
        // Only the validation of the gate save tells whether the files of the game load.
        var validation = editor.Validation is { } state && state.Save == save ? state : null;
        if (validation is { Running: true })
        {
            return;
        }

        if (validation?.Result is { } result)
        {
            Decide(run, save, result);
            return;
        }

        // The validation of the gate save gives no result. Until the waiting event, the gate save is the last save.
        var session = editor.Session;
        if (session?.Saves > save)
        {
            // A save after the read window stopped the validation of the gate save (D58). The game shows the gate save.
            GoOn(run, $"Play goes on without a validation of {EditController.SaveText(save)}: a later save stopped " +
                      $"it. The game shows the files of {EditController.SaveText(save)}.");
            return;
        }

        if (save > 0 && session?.IncompleteSave == save)
        {
            // The save journal holds the gate save, so the game can hold parts of two saves. Such files get no
            // validation (D59).
            Log(ConsoleSeverity.Error, $"Play stopped: save {save} is not complete, and the game can hold parts of it. " +
                                       "Correct the problem of the file, save again, then press Play.");
            run.CauseReported = true;
            SendStop(run);
            return;
        }

        // No validation of the gate save exists, for example in a new session, or a save stopped it and wrote nothing. A
        // validation that cannot start gives its result at once, and that result runs the gate again.
        editor.ValidateNow();
    }

    /// <summary>The gate for the result of the validation of the gate save.</summary>
    private void Decide(GameRun run, int save, ValidationResult result)
    {
        if (result.Problems is not { } problems)
        {
            GoOn(run, $"Play goes on without a validation: the validation of {EditController.SaveText(save)} could not " +
                      "run.");
            return;
        }

        var errors = problems.Count(p => p.Severity == DiagnosticSeverity.Error);
        if (errors > 0)
        {
            // The game sends an error event before it exits, so a crash of its load came already (D55).
            Log(ConsoleSeverity.Error, $"Play stopped: the validation of {EditController.SaveText(save)} found " +
                                       $"{Errors(errors)}." + (run.Error is { Crash: true }
                                           ? " The game closed during its load because of them."
                                           : " The Console lists them."));
            run.CauseReported = true;
            SendStop(run);
            return;
        }

        if (run.Waited)
        {
            SendStart(run);
        }
    }

    /// <summary>Starts the play without a validation result (D43), after the waiting event.</summary>
    private void GoOn(GameRun run, string warning)
    {
        if (run.Waited)
        {
            Log(ConsoleSeverity.Warning, warning);
            SendStart(run);
        }
    }

    private void OnValidated(ValidationState state)
    {
        NoteSave();
        ResolveCrash();
        if (game != null)
        {
            CheckGate(game);
            CheckReload(game);
        }
    }

    /// <summary>
    /// The reload trigger of section 12.1 (D17, D52). After the player arrived, the game gets the last save of the
    /// session when the game does not show that save whole and the validation of the save passes. A validation that
    /// could not run lets the reload go on with a warning, as the start does (D75). Validation errors keep the save of
    /// the game, and the Console tells it once. A reload that failed is not sent again for the same save. Reload game
    /// sends it (D74).
    /// </summary>
    /// <remarks>One reload runs at a time. A newer save during a reload waits for the end of the reload, and the trigger
    /// then looks only at the last save, so the older saves drop. A save that is not complete gets no validation
    /// (D59). The save that completes it starts the validation. A mission play starts again from its start in place of
    /// a reload (section 5.8 of the M6c design). A new mission play that waits for a restart gets it also without a new
    /// save.</remarks>
    private void CheckReload(GameRun run)
    {
        if (!run.Spawned || run.Ending || run.Error != null || run.Reload != null || editor.Session is not { } session)
        {
            return;
        }

        var save = session.Saves;
        var newSave = !(save < run.Shown || save == run.Shown && run.Whole || save == run.FailedReload);
        if (!newSave && run.Wanted == null || LastSaveIncomplete(session) || editor.Validation is { Running: true })
        {
            return;
        }

        if (!newSave)
        {
            // The files of the game did not change, so the new start needs no validation.
            SendRestart(run, save, null);
            return;
        }

        if (editor.Validation is not { Result: { } result } validation || validation.Save != save)
        {
            // A save stopped the validation of the last save and wrote nothing, for example after an exception. The
            // gate does the same (section 9.3).
            editor.ValidateNow();
            return;
        }

        if (result.Problems is not { } problems)
        {
            Log(ConsoleSeverity.Warning, $"The running game loads {EditController.SaveText(save)} without a " +
                                         "validation: the validation could not run.");
        }
        else if (problems.Count(p => p.Severity == DiagnosticSeverity.Error) is var errors and > 0)
        {
            if (run.ErrorsTold != save)
            {
                run.ErrorsTold = save;
                Log(ConsoleSeverity.Warning, (run.Whole
                    ? $"The running game keeps {EditController.SaveText(run.Shown)}."
                    : $"The running game can show parts of save {run.Shown}.") +
                    $" The validation of {EditController.SaveText(save)} found {Errors(errors)}.");
            }

            // The game keeps its files, so a new mission play that waited does not start.
            if (run.Wanted is { } wanted)
            {
                run.Wanted = null;
                var message = $"Play did not start {wanted.Mission!.Slot} from {wanted.Mission.StartName}: the " +
                              $"validation of {EditController.SaveText(save)} found {Errors(errors)}.";
                Log(ConsoleSeverity.Warning, message);
                Finish(wanted, Refused("ValidationErrors", message));
            }

            return;
        }

        var savedAt = lastSave.Save == save ? lastSave.At : (TimeSpan?)null;
        if (run.Mission != null)
        {
            SendRestart(run, save, savedAt);
            return;
        }

        SendReload(run, save, savedAt);
    }

    /// <summary>
    /// Starts the mission play again (section 5.8 of the M6c design): from the start of a new mission play that waits,
    /// else from the start of the running play, not from the play start of the settings. The editor makes the plan again
    /// from the new model and writes the start save and the test layer, then the game gets restart. The start of the
    /// running play changes only when the game answers restarted. The process reads its start save and its layer from
    /// the folders of its arguments. A start that needs other folders closes the game and plays again.
    /// </summary>
    /// <remarks>A new start that cannot start gives its refusal, and the save that its request made goes to the game
    /// with the start of the running play. When the start of the running play cannot start, the game keeps its mission
    /// and its data, and the trigger of the reload does not send this save again.</remarks>
    private void SendRestart(GameRun run, int save, TimeSpan? savedAt)
    {
        var wanted = run.Wanted;
        run.Wanted = null;
        var mission = wanted?.Mission ?? run.Mission!;
        if (mission.Prepare() is not { } start)
        {
            var reason = mission.Refusal ?? NoStart;
            if (wanted != null)
            {
                Log(ConsoleSeverity.Warning, MissionPlayTexts.Refused(reason));
                Finish(wanted, Refused("StartRefused", reason));
                CheckReload(run);
                return;
            }

            Log(ConsoleSeverity.Warning, MissionPlayTexts.RestartRefused(mission, reason, Shown(run)));
            run.FailedReload = save;
            return;
        }

        if (start != run.Args.Start)
        {
            var again = wanted ?? new PlayRequest(null, run.Args.Package, clock()) { Mission = mission };
            if (pending == null)
            {
                pending = again;
            }
            else
            {
                Finish(again, Refused("Replaced", ReplacedText));
            }

            SendStop(run);
            return;
        }

        run.Restarting = mission;
        run.RestartRequest = wanted;
        run.Reload = new ReloadRun(save, clock(), savedAt) { Restart = true };
        // The restart loads all game data again, so the Console counts the engine lines of the run again. The lines of
        // the load name the setup triggers of the new start.
        run.Lines.LoadStarted();
        run.Lines.Layer = mission.SetupTriggers;
        if (wanted != null)
        {
            StartNotes(mission);
        }
        else
        {
            Log(ConsoleSeverity.Info, MissionPlayTexts.Restart(save, mission));
        }

        Send(run, PlayCommandKind.Restart, save);
        editor.PlayStarted();
    }

    /// <summary>True when the last save of the session is not complete. The save journal holds it, so it gets no
    /// validation (D59), and the play load refuses its files.</summary>
    private static bool LastSaveIncomplete(PlaySession session) =>
        session.Saves > 0 && session.Saves == session.IncompleteSave;

    private void SendReload(GameRun run, int save, TimeSpan? savedAt)
    {
        run.Reload = new ReloadRun(save, clock(), savedAt);
        // The reload loads all game data again, so the Console counts the engine lines of the run again.
        run.Lines.LoadStarted();
        Send(run, PlayCommandKind.Reload, save);
    }

    /// <summary>Notes the time of a new save of the session, for the time from the save to the reloaded game.</summary>
    private void NoteSave()
    {
        if (editor.Session?.Saves is { } saves && saves != lastSave.Save)
        {
            lastSave = (saves, clock());
        }
    }

    private void OnEvent(GameRun run, PlayEvent e)
    {
        if (run != game)
        {
            return;
        }

        switch (e)
        {
            case PlayEvent.Waiting:
                run.GateSave = GateSave(run);
                run.Waited = true;
                run.NotResponding = false;
                if (run.GateSave != run.Args.Save && !run.Ending)
                {
                    Log(ConsoleSeverity.Info, $"Save {run.GateSave} came while the game read the mod files. Play " +
                                              "waits for its validation.");
                }

                CheckGate(run);
                break;
            // The game can start the play before it reads a stop, so a spawn can follow stop (section 9.8).
            case PlayEvent.Spawned spawned when !run.Ending:
                run.Spawned = true;
                run.Place = StatusPlace(spawned.System, spawned.Base);
                // A save in the read window makes the files of the game a mix of two saves (D51).
                run.Shown = run.GateSave;
                run.Whole = run.GateSave == run.Args.Save;
                var ship = spawned.ShipName.Length > 0 ? $"{spawned.ShipName} {spawned.Ship}" : spawned.Ship;
                Log(ConsoleSeverity.Info, $"Playing {Place(spawned)} with {ship}. From Play to flight: " +
                                          $"{Seconds(clock() - run.Requested)} s.");
                // A save after the read window does not change the start. Its reload follows now (D52).
                CheckReload(run);
                break;
            case PlayEvent.Reloaded reloaded when run.Reload is { } reload && !run.Ending:
                // A save while the game read the mod files makes the files of the game a mix of two saves (D51).
                var saves = editor.Session?.Saves ?? reload.Save;
                run.Reload = null;
                run.NotResponding = false;
                run.Place = StatusPlace(reloaded.System, reloaded.Base);
                run.Shown = Math.Max(saves, reload.Save);
                run.Whole = saves <= reload.Save;
                var fromSave = reload.SavedAt is { } savedAt ? $", {Seconds(clock() - savedAt)} s from the save" : "";
                Log(ConsoleSeverity.Info, $"The running game shows {EditController.SaveText(reload.Save)} (reload " +
                                          $"{Seconds(TimeSpan.FromMilliseconds(reloaded.Ms))} s{fromSave}).");
                if (!run.Whole)
                {
                    Log(ConsoleSeverity.Info,
                        $"Save {run.Shown} came during the reload, so the game can show parts of it.");
                }

                CheckReload(run);
                break;
            case PlayEvent.ReloadFailed failed when run.Reload is { } reload && !run.Ending:
                // The game keeps its data, also when a save came during the reload.
                run.Reload = null;
                run.NotResponding = false;
                run.FailedReload = reload.Save;
                Log(ConsoleSeverity.Error, $"The reload of {EditController.SaveText(reload.Save)} failed, so the " +
                                           $"running game keeps {Shown(run)}: {failed.Message.TrimEnd('.')}.");
                CheckReload(run);
                break;
            case PlayEvent.Restarted restarted when run.Reload is { Restart: true } reload && !run.Ending:
                var current = editor.Session?.Saves ?? reload.Save;
                run.Reload = null;
                run.NotResponding = false;
                run.Mission = run.Restarting ?? run.Mission;
                run.Restarting = null;
                run.Place = StatusPlace(restarted.System, restarted.Base);
                run.Shown = Math.Max(current, reload.Save);
                run.Whole = current <= reload.Save;
                Log(ConsoleSeverity.Info, $"The game started {run.Mission!.Slot} again from {run.Mission.StartName} " +
                                          $"with {EditController.SaveText(reload.Save)} (restart " +
                                          $"{Seconds(TimeSpan.FromMilliseconds(restarted.Ms))} s).");
                if (!run.Whole)
                {
                    Log(ConsoleSeverity.Info,
                        $"Save {run.Shown} came during the restart, so the game can show parts of it.");
                }

                FinishRestart(run, new PlayRequestResult(Status));
                CheckReload(run);
                break;
            case PlayEvent.RestartFailed failed when run.Reload is { Restart: true } reload && !run.Ending:
                // The game goes on at the place of the player with its old data and its old start (section 5.8 of the
                // M6c design).
                run.Reload = null;
                run.NotResponding = false;
                run.FailedReload = reload.Save;
                var tried = run.Restarting ?? run.Mission!;
                run.Restarting = null;
                run.Lines.Layer = run.Mission!.SetupTriggers;
                var text = MissionPlayTexts.RestartFailedInGame(tried, failed.Message);
                Log(ConsoleSeverity.Warning, text);
                FinishRestart(run, Refused("RestartFailed", text));
                CheckReload(run);
                break;
            case PlayEvent.MissionChanges changes:
                editor.MissionChanged(changes);
                break;
            case PlayEvent.Error error:
                run.Error = error;
                run.ErrorAt = clock();
                break;
        }
    }

    /// <summary>"in New York (Li01)", or "at Planet Manhattan (Li01_01_Base) in New York (Li01)".</summary>
    private string Place(PlayEvent.Spawned spawned) =>
        spawned.Base is { } dock
            ? $"at {BaseText(dock)} in {SystemText(spawned.System)}"
            : $"in {SystemText(spawned.System)}";

    /// <summary>The place of the status text: the base, or the system in space.</summary>
    private string StatusPlace(string system, string? dock) => dock != null ? BaseText(dock) : SystemText(system);

    /// <summary>"save 2", "the saved files", or "parts of save 3" after a save in a read window.</summary>
    private static string Shown(GameRun run) =>
        run.Whole ? EditController.SaveText(run.Shown) : $"parts of save {run.Shown}";

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>"New York (Li01)", or only the nickname when the system has no name.</summary>
    private string SystemText(string system) => Named(editor.SystemName(system), system);

    /// <summary>"Planet Manhattan (Li01_01_Base)", or only the nickname when the base has no name.</summary>
    private string BaseText(string dock) => Named(editor.BaseName(dock), dock);

    private static string Named(string name, string nickname) => name.Length > 0 ? $"{name} ({nickname})" : nickname;

    private void OnLogLine(GameRun run, string line)
    {
        if (run != game || ConsoleLog.ParseGameLine(line) is not { } parsed)
        {
            return;
        }

        if (parsed.Severity == LogSeverity.Error)
        {
            run.LastErrorLine = $"{parsed.Source}: {parsed.Message}";
        }

        // Decision D57: only the lines that the Console does not have yet. The run is a batch of the Console, whose
        // lines resolve when they arrive (decision D514 of the console filter design). A line that starts a load of the
        // game starts the counts of the run again.
        editor.Log.AddGameLine(run.Lines, parsed, Source);
    }

    private void OnExit(GameRun run, int code, string errorOutput)
    {
        if (run != game)
        {
            return;
        }

        game = null;
        run.Process.Dispose();
        editor.PlayEnded();
        if (run.Wanted is { } wanted)
        {
            run.Wanted = null;
            Finish(wanted, Refused("GameClosed", "The game closed before the mission play started again."));
        }

        FinishRestart(run, Refused("GameClosed", "The game closed before the mission play started again."));

        // A kill sets the exit code, so the code tells nothing.
        int? exitCode = run.Killed ? null : code;
        // The line of the kill told why the editor ended the game. After an error event, the cause of that error
        // follows, for example when the crash window of a Release build waited for a click.
        if (run.Killed && run.Error == null)
        {
            return;
        }

        if (exitCode == PlayExitCodes.Ok)
        {
            if (!run.CauseReported)
            {
                Log(ConsoleSeverity.Info, "The game closed.");
            }

            return;
        }

        // An error event without a crash gives its own cause, for example an unknown ship package.
        if (run.Error is { Crash: false })
        {
            ReportExit(run, exitCode, errorOutput);
            return;
        }

        if (run.CauseReported)
        {
            return;
        }

        // Validation errors can make the engine load throw. So the report of a crash during the validation of its save
        // waits for the result (D55).
        if (GateValidationRuns(run))
        {
            crash = new Crash(run, exitCode, errorOutput, GateSave(run));
            return;
        }

        ReportExit(run, exitCode, errorOutput);
    }

    /// <summary>Reports the crash that waits, when the validation of its save does not run any more.</summary>
    private void ResolveCrash()
    {
        if (crash is not { } waiting || editor.Validation is { Running: true } running && running.Save == waiting.Save)
        {
            return;
        }

        crash = null;
        var validation = editor.Validation;
        var errors = validation is { Result.Problems: { } problems } && validation.Save == waiting.Save
            ? problems.Count(p => p.Severity == DiagnosticSeverity.Error)
            : 0;
        if (errors > 0)
        {
            Log(ConsoleSeverity.Error, $"Play stopped: the validation of {EditController.SaveText(waiting.Save)} found " +
                                       $"{Errors(errors)}. The game closed during its load because of them.");
        }
        else
        {
            ReportExit(waiting.Run, waiting.Code, waiting.ErrorOutput);
        }
    }

    /// <summary>Reports the crash that waits for a validation now, for example when a new play starts.</summary>
    private void ReportCrashNow()
    {
        if (crash is { } waiting)
        {
            crash = null;
            ReportExit(waiting.Run, waiting.Code, waiting.ErrorOutput);
        }
    }

    /// <summary>The Console line of an exit with a code other than 0 (section 11.4).</summary>
    /// <param name="code">The exit code, or null when the editor ended the game.</param>
    private void ReportExit(GameRun run, int? code, string errorOutput)
    {
        if (code == PlayExitCodes.Data && run.Error is { } data)
        {
            Log(ConsoleSeverity.Error, $"Play stopped: {data.Message}");
            return;
        }

        var cause = run.Error?.Message ?? (errorOutput.Length > 0 ? errorOutput : run.LastErrorLine) ??
            "The game gave no cause.";
        var stopped = code is { } exit ? $"The game stopped with exit code {exit}" : "The game stopped";
        Log(ConsoleSeverity.Error, $"{stopped}: {cause.TrimEnd('.')}. The log is {ProgramPaths.PlayLog}.");
    }

    /// <summary>The watchdogs of section 9.10.</summary>
    private void Watch(GameRun run)
    {
        if (run.Killed)
        {
            return;
        }

        var now = clock();
        if (run.Error != null && now - run.ErrorAt >= ExitLimit)
        {
            // For example the crash window of a Release build, which waits for a click.
            Kill(run, "The game did not exit 5 s after its error, so the editor ended it.");
        }
        else if (run.StopSentAt is { } sent)
        {
            if (pending != null && now - sent >= ExitLimit)
            {
                Kill(run, "The game did not close in 5 s, so the editor ended it to play again.");
            }
        }
        else if (!run.Waited && !run.NotResponding && now - run.Started >= LoadLimit)
        {
            run.NotResponding = true;
            Log(ConsoleSeverity.Warning, "Play: the game does not respond. It did not load its data in 60 s. Stop ends it.");
        }
        else if (run.Reload is { } reload && !run.NotResponding && now - reload.Sent >= ReloadLimit)
        {
            run.NotResponding = true;
            Log(ConsoleSeverity.Warning, reload.Restart
                ? "Play: the game does not respond. It did not start the mission again with " +
                  $"{EditController.SaveText(reload.Save)} in 30 s. Stop ends it."
                : "Play: the game does not respond. It did not end the reload of " +
                  $"{EditController.SaveText(reload.Save)} in 30 s. Stop ends it.");
        }
    }

    private void SendStart(GameRun run)
    {
        Send(run, PlayCommandKind.Start);
        run.StartSent = true;
    }

    private void SendStop(GameRun run)
    {
        Send(run, PlayCommandKind.Stop);
        run.StopSentAt = clock();
    }

    /// <summary>Sends a command. A game that ended gets nothing, and its exit follows.</summary>
    /// <param name="save">The save of a reload.</param>
    private static void Send(GameRun run, PlayCommandKind kind, int save = 0) =>
        run.Process.Send(new PlayCommand(kind, save));

    private void Kill(GameRun run, string message)
    {
        run.Killed = true;
        run.Process.Kill();
        Log(ConsoleSeverity.Warning, message);
    }

    private void Log(ConsoleSeverity severity, string message) => editor.Log.Add(severity, Source, message);

    /// <summary>The text of a request without an open profile.</summary>
    private const string NoProfile = "Play needs an open profile whose game data loaded.";

    /// <summary>The text of a mission start that refused without a reason.</summary>
    private const string NoStart = "The mission play cannot start. The Console tells why.";

    /// <summary>The text of a request that a newer request replaced.</summary>
    private const string ReplacedText = "A newer play request replaced this request, so this play did not start.";

    /// <summary>Puts a new request in place of the request that waits, which then gives its refusal.</summary>
    private Task<PlayRequestResult> Replace(PlayRequest request)
    {
        if (pending is { } earlier)
        {
            Finish(earlier, Refused("Replaced", ReplacedText));
        }

        pending = request;
        return request.Result.Task;
    }

    /// <summary>Drops the request that waits and a new mission play that waits for its restart. Each gives its
    /// refusal.</summary>
    private void DropRequests(string message)
    {
        if (pending is { } request)
        {
            pending = null;
            Finish(request, Refused("Dropped", message));
        }

        if (game?.Wanted is { } wanted)
        {
            game.Wanted = null;
            Finish(wanted, Refused("Dropped", message));
        }

        if (game != null)
        {
            FinishRestart(game, Refused("Dropped", message));
        }
    }

    /// <summary>Gives the request of a restart its result when the game answers, or when the restart ends without an
    /// answer.</summary>
    private static void FinishRestart(GameRun run, PlayRequestResult result)
    {
        if (run.RestartRequest is { } request)
        {
            run.RestartRequest = null;
            Finish(request, result);
        }
    }

    /// <summary>Gives a request its result, with the plan of its mission start.</summary>
    private static void Finish(PlayRequest request, PlayRequestResult result) =>
        request.Result.TrySetResult(result.Plan == null && request.Mission?.Plan is { } plan ? result with { Plan = plan } : result);

    private PlayRequestResult Refused(string detail, string message) =>
        PlayRequestResult.Refused(Status, detail, message);

    private static string Errors(int count) => count == 1 ? "1 error" : $"{count} errors";

    /// <param name="Start">The start, or null for a mission play, which prepares its start after the save.</param>
    /// <param name="Time">The time of the request, for the time from Play to flight.</param>
    private sealed record PlayRequest(PlayStart? Start, string? Package, TimeSpan Time)
    {
        /// <summary>The mission start of a mission play, or null.</summary>
        public IMissionStart? Mission { get; init; }

        /// <summary>What the save of Play does for the request.</summary>
        public PlaySaveRule Save { get; init; }

        /// <summary>Who asks for the play.</summary>
        public EditOrigin Origin { get; init; }

        /// <summary>True after the Console told why the request waits for the Scene.</summary>
        public bool SceneWaitTold { get; set; }

        /// <summary>The result of the request. Its continuations run on the thread pool, not in the frame of the
        /// editor.</summary>
        public TaskCompletionSource<PlayRequestResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <param name="Code">The exit code, or null when the editor ended the game.</param>
    /// <param name="Save">The gate save, whose validation the report waits for.</param>
    private sealed record Crash(GameRun Run, int? Code, string ErrorOutput, int Save);

    /// <summary>A reload that the game did not end yet.</summary>
    /// <param name="Sent">The time of the reload command, for the watchdog.</param>
    /// <param name="SavedAt">The time of the save, or null for Reload game.</param>
    private sealed record ReloadRun(int Save, TimeSpan Sent, TimeSpan? SavedAt)
    {
        /// <summary>True for a restart of a mission play (section 5.8 of the M6c design).</summary>
        public bool Restart { get; init; }
    }

    /// <summary>One game process and the state of its play.</summary>
    private sealed class GameRun(PlayProcess process, PlayArgs args, TimeSpan requested, TimeSpan started)
    {
        public PlayProcess Process { get; } = process;

        public PlayArgs Args { get; } = args;

        /// <summary>The time of the request.</summary>
        public TimeSpan Requested { get; } = requested;

        /// <summary>The time of the process start.</summary>
        public TimeSpan Started { get; } = started;

        /// <summary>The gate save. Until the waiting event, the newest save can replace it (see
        /// <see cref="PlayController.GateSave"/>).</summary>
        public int GateSave { get; set; } = args.Save;

        public bool Waited { get; set; }

        public bool StartSent { get; set; }

        public bool Spawned { get; set; }

        /// <summary>The base, or the system in space, of the last spawn or reload, for the status text.</summary>
        public string Place { get; set; } = "";

        /// <summary>The save that the game shows after the spawn. When <see cref="Whole"/> is false, it is the last save
        /// that came while the game read the mod files, and the game can show parts of it (D51).</summary>
        public int Shown { get; set; }

        public bool Whole { get; set; } = true;

        /// <summary>The reload that the game did not end yet, or null.</summary>
        public ReloadRun? Reload { get; set; }

        /// <summary>The save of the last reload that failed. The reload trigger does not send it again.</summary>
        public int? FailedReload { get; set; }

        /// <summary>The save whose validation errors the Console told for the running game.</summary>
        public int? ErrorsTold { get; set; }

        public bool NotResponding { get; set; }

        public bool Killed { get; set; }

        /// <summary>True after stop or a kill: the game ends, and the editor sends it nothing more.</summary>
        public bool Ending => StopSentAt != null || Killed;

        /// <summary>True after the Console told why Play stopped, so the exit adds no second cause.</summary>
        public bool CauseReported { get; set; }

        public TimeSpan? StopSentAt { get; set; }

        /// <summary>The error event. The game exits after it.</summary>
        public PlayEvent.Error? Error { get; set; }

        public TimeSpan ErrorAt { get; set; }

        /// <summary>The last error line of the engine log, for the report of a crash without a cause.</summary>
        public string? LastErrorLine { get; set; }

        /// <summary>The engine lines of the run for the Console: the count of each key and the notice.</summary>
        public required GameRunLines Lines { get; init; }

        /// <summary>The mission start of a mission play, or null. A new mission play replaces it when the game starts
        /// again at the new start.</summary>
        public IMissionStart? Mission { get; set; }

        /// <summary>A new mission play that waits for its restart, or null (section 5.8 of the M6c design).</summary>
        public PlayRequest? Wanted { get; set; }

        /// <summary>The start of the restart that the game did not end yet, or null. It becomes <see cref="Mission"/>
        /// when the game answers restarted.</summary>
        public IMissionStart? Restarting { get; set; }

        /// <summary>The request of the new start of the restart that the game did not end yet, or null. It gets its
        /// result when the game answers.</summary>
        public PlayRequest? RestartRequest { get; set; }
    }
}
