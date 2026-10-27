using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Play;

/// <summary>
/// What Play in the game needs from the editor, more than each Play needs. It needs the settings, the linked games, a
/// question and a folder picker. It also needs the folders that a play copy must not overlap.
/// <see cref="EditorWindow"/> implements it. The tests give a fake.
/// </summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IGamePlayHost : IPlayHost
{
    EditorSettings Settings { get; }

    /// <summary>The checks of the linked games, in the order of their links.</summary>
    IReadOnlyList<GameReport> LinkedGameReports();

    /// <summary>Asks a question with buttons. The first button is the default button.</summary>
    void Ask(string title, string message, IReadOnlyList<Choice> choices);

    /// <summary>Opens the folder picker. A folder comes on the UI thread, and Cancel gives none.</summary>
    void ChooseFolder(Action<string> chosen);

    /// <summary>The folders that a play copy must not overlap (decision D309): the workspace, the mod folders of the
    /// open profile and the OpenLancer repo.</summary>
    IReadOnlyList<string> ProtectedFolders();
}

/// <summary>A mod that a play installs, for the record of a test play (decision D569 of the M11c design).</summary>
/// <param name="Name">The display name of the mod.</param>
/// <param name="History">The folder of the publish history of the mod.</param>
/// <param name="Content">The content fingerprint of the mod at the install.</param>
internal sealed record PlayedMod(string Id, string Name, string Folder, string History, string Content)
{
    /// <summary>The played mod of a mod of a profile, or null when OpenLancer cannot read a file of the mod now. The
    /// play then records no test play for the mod. The history is in the workspace of the profile.</summary>
    public static PlayedMod? Of(Mod mod, string profilePath, ModFingerprint fingerprints)
    {
        try
        {
            return new PlayedMod(mod.Id, mod.Manifest.Name, mod.Folder,
                PublishHistory.FolderOf(ModCatalog.WorkspaceOf(profilePath), mod.Id),
                fingerprints.Content(mod.Manifest, mod.Folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A play copy of a linked game as the Game link window shows it.</summary>
/// <param name="Folder">The folder of the play copy.</param>
/// <param name="Record">The record, or null when the folder holds no play copy.</param>
/// <param name="Size">The bytes of the real files.</param>
internal sealed record PlayCopyInfo(string Folder, PlayCopyRecord? Record, long Size);

/// <summary>
/// Play in the game and the test server (sections 8.6 and 8.7 of the M11 design). Play in the game saves the active
/// mod and waits for the validation of the save. Then it merges all mods of the profile on the files of the linked game.
/// It installs them into the play copy of that game, and starts the game program of the play copy. Host test server does
/// the same and starts FLServer of the play copy. The original game folder never changes.
/// </summary>
/// <remarks>
/// <para>The merge, the make of the play copy and the install run on a worker thread. The play copy keeps its lock while
/// the game or the test server runs. After each exit, the check of the linked files runs (decision D323).</para>
/// <para>The game ends with the editor, through the job of its process (decision D313). Stop closes the main window of
/// the game. After 5 seconds, Stop becomes Force stop, which ends the process. Stop test server closes the window of
/// FLServer and offers Force stop at once. The editor ends FLServer itself after 60 seconds (decision D336).</para>
/// <para>Play in OpenLancer (<see cref="PlayController"/>) does not change. Both can run at the same time.</para>
/// <para>A game that runs 60 s or more and ends without a crash and without Force stop records a test play in the
/// publish history of each mod that its play installed (decision D569 of the M11c design). The test server records
/// nothing.</para>
/// </remarks>
internal sealed class GamePlay : IDisposable, IGameStop
{
    /// <summary>The Console source of Play in the game.</summary>
    public const string Source = "Play in the game";

    /// <summary>The time that the game has to close after Stop, before Stop becomes Force stop.</summary>
    internal static readonly TimeSpan ForceStopLimit = TimeSpan.FromSeconds(5);

    internal const string SavesNote = "Freelancer 1.x keeps its saves in Documents\\My Games\\Freelancer. The saves of " +
                                      "this play go there too. Load them only with the mod.";

    internal const string ReloadNote = "The game does not load changes while it runs. Stop the game and play again.";

    internal const string NoTarget = "Link a game folder with its game program in File > Game link.";

    /// <summary>The shortest run of the game that counts as a test play.</summary>
    internal static readonly TimeSpan TestPlayTime = TimeSpan.FromSeconds(60);

    /// <summary>The first exit code of a crash. Windows gives the codes of a crash from this value up, for example
    /// 0xC0000005.</summary>
    internal const uint CrashCodes = 0xC0000000;

    private readonly IGamePlayHost host;
    private readonly GameStarter start;
    private readonly IStringResourceWriter strings;
    private readonly IThnEvaluator thn;
    private readonly Func<TimeSpan> clock;
    private readonly HashSet<string> savesNoteShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task<CheckOutcome>> checks = [];
    private readonly Dictionary<string, PlayCopyInfo> infos = new(StringComparer.OrdinalIgnoreCase);
    private bool charactersNoteShown;

    // A request that waits for the end of an open field edit, a drag or a dialog, so that the save has the edit.
    private Job? pending;

    // The request from its save to the start of its program.
    private Job? job;

    // The play copy of the game and the server that run, with its lock.
    private PlayCopy? open;
    private GameRun? client;
    private GameRun? server;

    // An action of the Game link window on a play copy, and the closes that wait for the end of the test server.
    private (string Root, string What, Task<string> Task, Action? Done)? action;
    private Action? closeAfterServer;

    /// <param name="start">Starts the game and FLServer. The tests give a fake.</param>
    /// <param name="clock">Gives the time for Force stop and for the stop of the test server.</param>
    public GamePlay(IGamePlayHost host, GameStarter start, IStringResourceWriter strings, IThnEvaluator thn,
        Func<TimeSpan> clock)
    {
        this.host = host;
        this.start = start;
        this.strings = strings;
        this.thn = thn;
        this.clock = clock;
        host.Validated += OnValidated;
    }

    /// <summary>Runs on the UI thread when the history of a mod got a test play.</summary>
    public event Action? PlaysRecorded;

    private enum JobKind
    {
        Game,
        Server,
        Export,
    }

    /// <summary>The play targets of the linked games, in the order of the links.</summary>
    public IReadOnlyList<PlayTarget> Targets => PlayTarget.Of(host.LinkedGameReports());

    /// <summary>The play target of Ctrl+F5: the target of the last Play in the game, else the first one.</summary>
    public PlayTarget? LastTarget =>
        Targets.FirstOrDefault(t => host.Settings.LastGameTarget is { } last && GameFolders.Same(t.Root, last)) ??
        Targets.FirstOrDefault();

    /// <summary>True while a request saves, validates, merges or installs, or while the game or the server
    /// runs.</summary>
    public bool Busy => pending != null || job != null || client != null || server != null;

    /// <summary>True while a request saves, validates, merges or installs the mods into a play copy.</summary>
    public bool Installing => pending != null || job != null;

    public bool GameRuns => client != null;

    public bool ServerRuns => server != null;

    /// <summary>True while a check after an exit runs. It holds the lock of the play copy.</summary>
    internal bool Checking => checks.Count > 0;

    /// <summary>True when Stop ends the game at once: the game did not close in 5 seconds after Stop, or it has no
    /// window.</summary>
    public bool ForceStop => client is { Killed: false, StopSentAt: { } sent } run &&
                             (run.NoWindow || clock() - sent >= ForceStopLimit);

    /// <summary>True when Stop test server ends FLServer at once: Stop test server ran before.</summary>
    public bool ServerForceStop => server is { Killed: false, StopSentAt: not null };

    /// <summary>The status text of the game of the play copy, or null when none runs. For example "Playing in HD
    /// Edition 0.7.1 (play copy)".</summary>
    public string? GameStatus => job is { Kind: not JobKind.Server } running ? JobText(running)
        : client switch
        {
            null => null,
            { StopSentAt: not null } => "Stopping the game of the play copy",
            { Status: { } status } => status,
            { } run => $"Playing in {run.Target.Name} (play copy)",
        };

    /// <summary>The status text of the test server, or null when none runs.</summary>
    public string? ServerStatus => job is { Kind: JobKind.Server } running ? JobText(running)
        : server switch
        {
            null => null,
            { StopSentAt: not null } => "Stopping the test server",
            { } run => TestServer.Status(run.Target),
        };

    /// <summary>Why Join test server cannot run now, or null.</summary>
    public string? JoinBlock => server == null ? "Host a test server first."
        : client != null ? "The game of the play copy runs."
        : server.StopSentAt != null ? "The test server stops."
        : null;

    /// <summary>Plays the profile in a linked game: Play in &lt;game&gt; and Ctrl+F5. It runs at the next
    /// <see cref="Update"/> without an open field edit, drag or dialog.</summary>
    public void Play(PlayTarget target) => Request(new Job(target, JobKind.Game));

    /// <summary>Plays in the last game target, or in the first one (decision D324).</summary>
    public void PlayLast()
    {
        if (LastTarget is { } target)
        {
            Play(target);
        }
        else
        {
            Log(ConsoleSeverity.Warning, $"Play did not start. {NoTarget}");
        }
    }

    /// <summary>Host test server: the steps of Play in the game, then FLServer of the play copy with <c>/c</c>.</summary>
    public void HostServer(PlayTarget target) => Request(new Job(target, JobKind.Server));

    /// <summary>Join test server: starts the game of the play copy of the running server, without a new
    /// install.</summary>
    public void JoinServer()
    {
        if (JoinBlock is { } block)
        {
            Log(ConsoleSeverity.Warning, $"Play did not start. {block}");
            return;
        }

        // The game plays the mods that the test server installed.
        StartGame(server!.Target, null, server.Installed);
    }

    /// <summary>
    /// Export and play (decision D326) and "Play this mod alone" of the Mods window (decision D577): installs the files
    /// of an export into the play copy of its base, and starts the game. The play copy then holds this mod alone, with
    /// its own text DLL. The next Play in the game installs the profile again.
    /// </summary>
    /// <param name="alone">True for "Play this mod alone", false for "Export and play". Only the status text
    /// differs.</param>
    public void PlayExport(GameReport game, TargetFiles files, PlayedMod mod, bool alone = false)
    {
        if (PlayTarget.Of([game]) is not [var target])
        {
            Log(ConsoleSeverity.Warning, $"Play did not start. {NoTarget}");
            return;
        }

        var export = new Job(target, JobKind.Export)
        {
            Files = files,
            Mods = [mod],
            Status = alone
                ? $"Playing {mod.Name} alone in {target.Name} (play copy)"
                : $"Playing the export of {mod.Id} in {target.Name} (play copy)",
        };
        if (Refusal(export) is { } refusal)
        {
            Refuse(refusal);
            return;
        }

        job = export;
        Build(export);
    }

    /// <summary>Stops the game of the play copy, or ends it at once when <see cref="ForceStop"/> is true.</summary>
    public void StopGame()
    {
        if (client is not { Killed: false } run)
        {
            return;
        }

        if (ForceStop)
        {
            Kill(run, "The game of the play copy did not close, so the editor ended it.");
            return;
        }

        if (run.StopSentAt != null)
        {
            return;
        }

        run.StopSentAt = clock();
        if (!run.Process.CloseMainWindow())
        {
            run.NoWindow = true;
            Log(ConsoleSeverity.Warning, "The game has no window that OpenLancer can close. Click Force stop to end it.");
        }
    }

    /// <summary>Stop test server (decision D336): closes the main window of FLServer, which saves and stops. A second
    /// click is Force stop, which ends the process at once.</summary>
    public void StopServer()
    {
        if (server is not { Killed: false } run)
        {
            return;
        }

        if (run.StopSentAt != null)
        {
            Kill(run, "The editor ended the test server.");
            return;
        }

        run.StopSentAt = clock();
        Log(ConsoleSeverity.Info, TestServer.Stopping);
        if (!run.Process.CloseMainWindow())
        {
            Log(ConsoleSeverity.Warning, TestServer.NoWindow);
        }
    }

    /// <summary>
    /// The close of the editor with a running test server (decision D336). Without a server, the result is true: the
    /// close goes on. With a server, a dialog asks first and the result is false. Stop and close stops the server, and
    /// <paramref name="close"/> runs when the server ended.
    /// </summary>
    public bool CloseRequested(Action close)
    {
        if (server == null)
        {
            return true;
        }

        host.Ask("Close OpenLancer", TestServer.CloseQuestion,
        [
            new Choice("Stop and close", () =>
            {
                closeAfterServer = close;
                if (server is { StopSentAt: null })
                {
                    StopServer();
                }
            }),
            new Choice("Cancel", () => { }),
        ]);
        return false;
    }

    /// <summary>Runs a request that waits, the gate, the end of the worker, the watchdogs of the stops and the notes.
    /// Call it once each frame, after the layout.</summary>
    /// <param name="editing">True while a field edit or a drag is open.</param>
    /// <param name="dialogOpen">True while a dialog is open.</param>
    public void Update(bool editing, bool dialogOpen)
    {
        if (pending != null && !editing && !dialogOpen)
        {
            var request = pending;
            pending = null;
            Begin(request);
        }

        CheckGate();
        if (job?.Task is { IsCompleted: true } done)
        {
            var finished = job;
            job = null;
            Finish(finished, done);
        }

        if (server is { Killed: false, StopSentAt: { } sent } stopping && clock() - sent >= TestServer.StopLimit)
        {
            Kill(stopping, "The test server did not stop in 60 s, so the editor ended it.");
        }

        NoteSaves();
        while (checks.Count > 0 && checks[0].IsCompleted)
        {
            var check = checks[0];
            checks.RemoveAt(0);
            Checked(check);
        }

        if (action is { Task.IsCompleted: true } ended)
        {
            action = null;
            ActionDone(ended.Root, ended.What, ended.Task, ended.Done);
        }
    }

    /// <summary>Ends the game and the server, because the editor closes. Their jobs end the processes.</summary>
    public void Dispose()
    {
        host.Validated -= OnValidated;
        client?.Process.Dispose();
        server?.Process.Dispose();
        client = server = null;
        open?.Dispose();
        open = null;
    }

    /// <summary>The folder of the play copy of a linked game (decision D309): the folder that the user picked, else the
    /// default folder.</summary>
    public string CopyFolder(string root) =>
        host.Settings.Games.FirstOrDefault(g => GameFolders.Same(g.Path, root))?.PlayCopy ?? PlayCopy.DefaultFolder(root);

    /// <summary>The play copy of a linked game as the last <see cref="RefreshCopy"/> read it, or null.</summary>
    public PlayCopyInfo? CopyInfo(string root) => infos.GetValueOrDefault(GameFolders.Normal(root));

    /// <summary>Why the Game link window cannot change the play copy of a game now, or null.</summary>
    public string? CopyBlock(string root) =>
        action != null ? $"OpenLancer runs {action.Value.What} now."
        : Uses(root) ? "The game or the test server of this play copy runs, or Play in the game installs it now."
        : null;

    /// <summary>Reads the record of the play copy of a game in the background, for the Game link window.</summary>
    public void RefreshCopy(string root)
    {
        var key = GameFolders.Normal(root);
        var folder = CopyFolder(key);
        Task.Run(() =>
        {
            PlayCopyInfo info;
            try
            {
                var record = PlayCopyRecord.Read(folder);
                info = new PlayCopyInfo(folder, record, record?.SizeOnDisk(folder) ?? 0);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                info = new PlayCopyInfo(folder, null, 0);
            }

            host.QueueUIThread(() => infos[key] = info);
        });
    }

    /// <summary>Check play copy: checks the links and the installed files, and changes nothing.</summary>
    public void CheckCopy(string root) => RunAction(root, "the check of the play copy", copy =>
    {
        var result = copy.Check();
        return result.Problems.Count == 0
            ? $"The play copy {copy.Root} has no difference: {result.Links} links, {result.Copies} real files."
            : Problems(result, $"The check of the play copy {copy.Root} found {result.Problems.Count} differences.");
    });

    /// <summary>Uninstall mods: gives each installed file of the play copy its kind of the make again.</summary>
    public void UninstallCopy(string root) => RunAction(root, "the uninstall of the mods", copy =>
    {
        var result = copy.Uninstall();
        return Problems(result, $"OpenLancer removed the mods from the play copy {copy.Root}: {result.Restored} files " +
                                "got their game version again.");
    });

    /// <summary>Remove play copy: deletes the play copy. The game folder does not change. The window asks first. After
    /// the remove, the game gets the default folder again. A remove that fails keeps the folder in the settings, so the
    /// Game link window still shows the play copy.</summary>
    public void RemoveCopy(string root) => RunAction(root, "the remove of the play copy", copy =>
    {
        var folder = copy.Root;
        copy.Remove();
        return $"OpenLancer removed the play copy {folder}. Your game folder did not change.";
    }, done: () => SetCopyFolder(root, null));

    private void Request(Job request)
    {
        if (Refusal(request) is { } refusal)
        {
            Refuse(refusal);
            return;
        }

        pending = request;
    }

    /// <summary>The reason why a request cannot start now (step 1 of section 8.6), or null.</summary>
    private string? Refusal(Job request) =>
        job != null || pending != null ? "Play in the game prepares a game now. Wait until it starts."
        : client != null ? "The game of the play copy runs. Stop it first."
        : server != null ? "The test server of the play copy runs. Stop it first."
        : request.Kind == JobKind.Server && !request.Target.HasServer ? TestServer.Missing
        : null;

    /// <summary>The steps 2 and 3 of section 8.6: the checks of the mods, then the save of the active mod.</summary>
    private void Begin(Job request)
    {
        if (Refusal(request) is { } refusal)
        {
            Refuse(refusal);
            return;
        }

        if (host.Session is not { } session)
        {
            Refuse("No profile is open.");
            return;
        }

        try
        {
            if (PlayBuild.Refusal(request.Target.Game, ModProfile.Load(session.ProfilePath).Mods) is { } mods)
            {
                Refuse(mods);
                return;
            }
        }
        catch (ModLoadException e)
        {
            Refuse(e.Message);
            return;
        }

        host.Settings.LastGameTarget = request.Target.Root;
        host.Settings.Save();
        if (session.ActiveMod is { } mod && session.NoEditReason == null)
        {
            var outcome = host.Save();
            if (!outcome.Saved)
            {
                Refuse("The save did not complete. The Console tells why.");
                return;
            }

            if (outcome.Incomplete)
            {
                Refuse($"Save {outcome.Number} is not complete.");
                return;
            }

            if (outcome.Number > 0)
            {
                Log(ConsoleSeverity.Info, $"Play in {request.Target.Name}: saved mod '{mod}' (save {outcome.Number}). " +
                                          "Waiting for its validation.");
            }
        }

        request.Save = host.Session?.Saves ?? 0;
        job = request;
        CheckGate();
    }

    /// <summary>
    /// The gate of step 3: the validation of the last save must pass. Errors stop Play. A validation that could not run
    /// lets Play go on with a warning, as Play in OpenLancer does. A save that is not complete stops Play.
    /// </summary>
    private void CheckGate()
    {
        if (job is not { Task: null, Kind: not JobKind.Export } waiting)
        {
            return;
        }

        if (host.Session is not { } session)
        {
            job = null;
            Refuse("The profile closed.");
            return;
        }

        // The merge reads the files of the newest save.
        if (session.Saves > waiting.Save)
        {
            waiting.Save = session.Saves;
            waiting.ValidateAsked = false;
        }

        if (waiting.Save > 0 && session.IncompleteSave == waiting.Save)
        {
            job = null;
            Refuse($"Save {waiting.Save} is not complete. Correct the problem of the file, save again, then play again.");
            return;
        }

        var validation = host.Validation is { } state && state.Save == waiting.Save ? state : null;
        if (validation is { Running: true })
        {
            return;
        }

        if (validation?.Result is { } result)
        {
            if (result.Problems is not { } problems)
            {
                Log(ConsoleSeverity.Warning, $"Play in {waiting.Target.Name} goes on without a validation: the " +
                                             $"validation of {EditController.SaveText(waiting.Save)} could not run.");
            }
            else if (problems.Count(p => p.Severity == DiagnosticSeverity.Error) is var errors and > 0)
            {
                job = null;
                Refuse($"The validation of {EditController.SaveText(waiting.Save)} found " +
                       $"{(errors == 1 ? "1 error" : $"{errors} errors")}. The Console lists them.");
                return;
            }

            Build(waiting);
            return;
        }

        if (!waiting.ValidateAsked)
        {
            waiting.ValidateAsked = true;
            host.ValidateNow();
        }
    }

    private void OnValidated(ValidationState state) => CheckGate();

    /// <summary>Steps 4 and 5 on a worker thread. The worker merges the mods and runs the format checks. Then it makes
    /// the play copy and installs the files. A folder that the play copy cannot use asks the user for another
    /// one.</summary>
    private void Build(Job request)
    {
        var folder = request.Folder ?? CopyFolder(request.Target.Root);
        if (FolderRefusal(folder, request.Target) is { } refusal)
        {
            job = null;
            AskFolder(request, refusal);
            return;
        }

        var profile = host.Session?.ProfilePath;
        var before = checks.ToList();
        request.Task = Task.Run(() =>
        {
            // A check after an exit holds the lock of the play copy until it ends.
            foreach (var check in before)
            {
                check.ContinueWith(_ => { }).Wait();
            }

            return Prepare(request, profile, folder);
        });
    }

    /// <summary>The work of <see cref="Build"/> on the worker thread.</summary>
    private Prepared Prepare(Job request, string? profilePath, string folder)
    {
        var problems = new List<Diagnostic>();
        var files = request.Files;
        // The files of an export hold one mod alone.
        var installed = new Installed(request.Mods, Alone: true);
        if (files == null)
        {
            var profile = ModProfile.Load(profilePath!);
            if (PlayBuild.Refusal(request.Target.Game, profile.Mods) is { } refusal)
            {
                return new Prepared(folder) { Refusal = refusal };
            }

            // The fingerprints of the install, for the test plays. They come before the merge, so that a change of a
            // file during the merge gives the fingerprint of the old content. The test play then does not count for
            // the new content, which the game can miss.
            var fingerprints = new ModFingerprint();
            installed = new Installed(
                [.. profile.Mods.Select(m => PlayedMod.Of(m, profilePath!, fingerprints)).OfType<PlayedMod>()],
                Alone: profile.Mods.Count == 1);
            var build = PlayBuild.Run(request.Target.Game, profile.Mods, strings, thn);
            problems.AddRange(build.Problems);
            if (build.HasErrors)
            {
                return new Prepared(folder) { Problems = problems };
            }

            files = build.Files;
        }

        var source = request.Target.Game.Base!;
        var made = PlayCopyRecord.Read(folder) is not { State: PlayCopyState.Complete };
        if (request.Mode == PlayCopyMode.Links && !PlayCopyRecord.Exists(folder) &&
            PlayCopy.Probe(source, folder) is { } failure)
        {
            return new Prepared(folder) { Problems = problems, ProbeFailure = failure };
        }

        var copy = PlayCopy.Make(source, folder, request.Mode, request.Target.Game.Build);
        try
        {
            var result = copy.Install(files, profilePath);
            return new Prepared(folder)
            {
                Problems = problems,
                Copy = copy,
                Install = result,
                Made = made,
                Files = files.Files.Count,
                Installed = installed,
            };
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    /// <summary>The end of the worker on the UI thread: the problems, a question for another folder, or the start of the
    /// program (step 6).</summary>
    private void Finish(Job request, Task<Prepared> task)
    {
        if (task.Exception?.GetBaseException() is { } failure)
        {
            Refuse(ErrorText.Of(failure));
            return;
        }

        var prepared = task.Result;
        foreach (var problem in prepared.Problems.Concat(prepared.Install?.Problems ?? []))
        {
            host.Log.Add(problem);
        }

        if (prepared.Refusal != null)
        {
            Refuse(prepared.Refusal);
            return;
        }

        if (prepared.ProbeFailure != null)
        {
            AskFolder(request, prepared.ProbeFailure);
            return;
        }

        if (prepared.Copy is not { } copy)
        {
            Refuse("Fix the errors, then play again.");
            return;
        }

        open = copy;
        if (!GameFolders.Same(copy.Root, PlayCopy.DefaultFolder(request.Target.Root)))
        {
            SetCopyFolder(request.Target.Root, copy.Root);
        }

        RefreshCopy(request.Target.Root);

        if (prepared.Made)
        {
            Log(ConsoleSeverity.Info, PlayCopyTexts.Make(request.Target.Game.Name, copy.Root));
            Log(ConsoleSeverity.Info, PlayCopyTexts.Done(copy.Record.LinkCount, copy.Record.CopyCount,
                PlayCopyTexts.Size(copy.SizeOnDisk)));
        }

        Log(ConsoleSeverity.Info, $"Play in {request.Target.Name}: OpenLancer installed " +
                                  $"{(prepared.Files == 1 ? "1 file" : $"{prepared.Files} files")}. " +
                                  (request.Kind == JobKind.Server ? "The test server starts." : "The game starts."));
        if (request.Kind == JobKind.Server)
        {
            StartServer(request.Target, prepared.Installed);
        }
        else
        {
            StartGame(request.Target, request.Status, prepared.Installed);
        }

        if (client == null && server == null)
        {
            CloseCopy();
        }
    }

    /// <summary>Starts the game program of the open play copy in its program folder, without arguments.</summary>
    /// <param name="status">The status text while the game runs, or null for the text of a play of the
    /// profile.</param>
    /// <param name="installed">The mods that the play copy holds.</param>
    private void StartGame(PlayTarget target, string? status, Installed installed)
    {
        var copy = open!;
        if (copy.ProgramPath is not { } program)
        {
            Refuse($"The game folder has no game program: {Path.Combine(copy.ProgramFolder, "Freelancer.exe")}.");
            return;
        }

        client = Start(target, new ProcessStartInfo(program) { WorkingDirectory = copy.ProgramFolder }, server: false,
            installed);
        if (client == null)
        {
            return;
        }

        client.Status = status;
        if (!copy.SavesInCopy && savesNoteShown.Add(target.Root))
        {
            Log(ConsoleSeverity.Info, SavesNote);
        }
    }

    /// <summary>Starts FLServer of the open play copy with <c>/c</c> in the program folder (section 8.7).</summary>
    private void StartServer(PlayTarget target, Installed installed)
    {
        var copy = open!;
        if (copy.ServerPath is not { } program)
        {
            Refuse(TestServer.Missing);
            return;
        }

        server = Start(target, TestServer.Command(program, copy.ProgramFolder), server: true, installed);
        if (server == null)
        {
            return;
        }

        Log(ConsoleSeverity.Info, TestServer.Started);
        if (!charactersNoteShown)
        {
            charactersNoteShown = true;
            Log(ConsoleSeverity.Info, TestServer.Characters);
        }
    }

    private GameRun? Start(PlayTarget target, ProcessStartInfo info, bool server, Installed installed)
    {
        GameRun? run = null;
        try
        {
            // The process gives its exit on the UI thread after this method, so the callback finds the run.
            run = new GameRun(start(info, code => Exited(run!, code)), target, server, host.Session?.Saves ?? 0)
            {
                StartedAt = clock(),
                Installed = installed,
            };
            return run;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            Refuse($"OpenLancer could not start {info.FileName}: {e.Message}");
            return null;
        }
    }

    /// <summary>The exit of the game or of the server (steps 9 and 10): the check of the linked files runs after
    /// it.</summary>
    private void Exited(GameRun run, int code)
    {
        if (run == client)
        {
            client = null;
        }
        else if (run == server)
        {
            server = null;
        }
        else
        {
            return;
        }

        run.Process.Dispose();
        var name = run.IsServer ? "The test server" : "The game of the play copy";
        if (!run.Killed && code != 0)
        {
            Log(ConsoleSeverity.Warning, $"{name} stopped with exit code {code}.");
        }
        else if (!run.Killed)
        {
            Log(ConsoleSeverity.Info, $"{name} closed.");
        }

        var time = clock() - run.StartedAt;
        if (IsTestPlay(run.IsServer, run.Killed, code, time) && run.Installed.Mods.Count > 0)
        {
            RecordPlays(run, (int)time.TotalSeconds);
        }

        if (open != null)
        {
            var copy = open;
            var last = client == null && server == null;
            if (last)
            {
                open = null;
            }

            var before = checks.ToList();
            checks.Add(Task.Run(() =>
            {
                foreach (var check in before)
                {
                    check.ContinueWith(_ => { }).Wait();
                }

                try
                {
                    return new CheckOutcome(copy.Check(), null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlayCopyException)
                {
                    return new CheckOutcome(null, e.Message);
                }
                finally
                {
                    if (last)
                    {
                        copy.Dispose();
                    }
                }
            }));
        }

        if (server == null && closeAfterServer is { } close)
        {
            closeAfterServer = null;
            close();
        }
    }

    /// <summary>
    /// True when a run counts as a test play (decision D569, finding R17): a game, not the test server, that ran 60 s or
    /// more, without Force stop, and whose exit code as an unsigned number is below 0xC0000000.
    /// </summary>
    /// <param name="server">True for the test server.</param>
    /// <param name="killed">True after Force stop.</param>
    /// <param name="code">The exit code, as the process gives it.</param>
    /// <param name="time">The time from the start to the exit.</param>
    internal static bool IsTestPlay(bool server, bool killed, int code, TimeSpan time) =>
        !server && !killed && unchecked((uint)code) < CrashCodes && time >= TestPlayTime;

    /// <summary>
    /// Records a test play in the publish history of each mod of a run, on a worker thread, because a write waits for
    /// the lock of the history. A failed write gives a Console warning and no other effect.
    /// </summary>
    private void RecordPlays(GameRun run, int seconds)
    {
        var target = run.Target.Game.Target;
        var roots = host.LinkedGameReports().Select(r => r.Root).Append(host.Session?.BaseFolder).OfType<string>()
            .ToList();
        var time = DateTime.UtcNow;
        foreach (var mod in run.Installed.Mods)
        {
            var play = new HistoryPlay(time, target, mod.Content, run.Installed.Alone, seconds, Manual: false);
            var location = new HistoryLocation(mod.History, mod.Id, mod.Folder, roots);
            Task.Run(() => PublishHistory.Update(location, "play", h => h.AddPlay(play))).ContinueWith(task =>
                host.QueueUIThread(() => PlayRecorded(mod, run.Target, seconds, task)), TaskScheduler.Default);
        }
    }

    /// <summary>The end of the record of one test play, on the UI thread.</summary>
    private void PlayRecorded(PlayedMod mod, PlayTarget target, int seconds, Task<HistoryWrite> task)
    {
        if (task.Exception?.GetBaseException() is { } failure)
        {
            Log(ConsoleSeverity.Warning, $"OpenLancer did not record the test play of {mod.Id}: {ErrorText.Of(failure)}");
            return;
        }

        foreach (var message in task.Result.Messages)
        {
            Log(ConsoleSeverity.Warning, message);
        }

        if (task.Result.Written)
        {
            Log(ConsoleSeverity.Info, $"Recorded a test play of {mod.Name} ({mod.Id}) in {target.Name}: {seconds} s.");
            PlaysRecorded?.Invoke();
        }
    }

    /// <summary>The check after an exit (decision D323): each difference is a warning.</summary>
    private void Checked(Task<CheckOutcome> check)
    {
        if (check.Exception?.GetBaseException() is { } failure)
        {
            Log(ConsoleSeverity.Warning, $"OpenLancer could not check the play copy: {ErrorText.Of(failure)}");
            return;
        }

        if (check.Result.Error is { } error)
        {
            Log(ConsoleSeverity.Warning, $"OpenLancer could not check the play copy: {error}");
            return;
        }

        foreach (var problem in check.Result.Result!.Problems)
        {
            host.Log.Add(problem);
        }
    }

    /// <summary>The note of decision D331: a save while the game or the server runs.</summary>
    private void NoteSaves()
    {
        if (host.Session?.Saves is not { } saves)
        {
            return;
        }

        var runs = new[] { client, server }.OfType<GameRun>().Where(r => saves > r.SavesSeen).ToList();
        if (runs.Count == 0)
        {
            return;
        }

        foreach (var run in runs)
        {
            run.SavesSeen = saves;
        }

        Log(ConsoleSeverity.Info, ReloadNote);
    }

    /// <summary>Asks for another folder for the play copy (decision D309), or for a full copy when a folder that the user
    /// picked failed too.</summary>
    private void AskFolder(Job request, string reason)
    {
        var drive = Path.GetPathRoot(request.Target.Root) ?? request.Target.Root;
        if (request.Folder == null)
        {
            Log(ConsoleSeverity.Warning, PlayCopyTexts.ProbeFailed(drive, reason));
            host.Ask("Play in the game", PlayCopyTexts.ProbeFailed(drive, reason),
            [
                new Choice("Select folder...", () => host.ChooseFolder(folder => Retry(request, folder))),
                new Choice("Cancel", () => { }),
            ]);
            return;
        }

        if (request.Mode == PlayCopyMode.Full)
        {
            Refuse(reason);
            return;
        }

        var size = PlayCopyTexts.Size(PlayCopy.FullCopySize(request.Target.Game.Base!));
        Log(ConsoleSeverity.Warning, reason);
        host.Ask("Play in the game", PlayCopyTexts.FullCopyQuestion(size),
        [
            new Choice("Copy", () => Retry(request, request.Folder, PlayCopyMode.Full)),
            new Choice("Cancel", () => { }),
        ]);
    }

    /// <summary>Runs a request again from step 4 with the folder that the user picked. The save gate passed
    /// already.</summary>
    private void Retry(Job request, string folder, PlayCopyMode mode = PlayCopyMode.Links)
    {
        if (Refusal(request) is { } refusal)
        {
            Refuse(refusal);
            return;
        }

        var again = request with { Folder = GameFolders.Normal(folder), Mode = mode, Task = null };
        if (FolderRefusal(again.Folder, again.Target) is { } problem)
        {
            Refuse(problem);
            return;
        }

        job = again;
        Build(again);
    }

    /// <summary>The reason why a folder cannot be the play copy of a game (decision D309, step 6), or null.</summary>
    private string? FolderRefusal(string folder, PlayTarget target) =>
        PlayCopy.FolderProblem(folder, target.Root) ?? PlayCopy.ProtectedProblem(folder, host.ProtectedFolders());

    /// <summary>True when Play in the game uses the play copy of the game now.</summary>
    private bool Uses(string root) =>
        open != null && GameFolders.Same(open.Record.Source, root) ||
        job != null && GameFolders.Same(job.Target.Root, root) ||
        checks.Count > 0;

    /// <summary>Runs an action of the Game link window on the play copy of a game, on a worker thread. The action does not
    /// start while Play in the game uses a play copy.</summary>
    /// <param name="done">Runs on the UI thread when the action completed without an error.</param>
    private void RunAction(string root, string what, Func<PlayCopy, string> work, Action? done = null)
    {
        if (CopyBlock(root) is { } block)
        {
            Log(ConsoleSeverity.Warning, block);
            return;
        }

        var folder = CopyFolder(root);
        action = (GameFolders.Normal(root), what, Task.Run(() =>
        {
            using var copy = PlayCopy.Open(folder);
            return work(copy);
        }), done);
    }

    private void ActionDone(string root, string what, Task<string> task, Action? done)
    {
        if (task.Exception?.GetBaseException() is { } failure)
        {
            Log(ConsoleSeverity.Error, $"OpenLancer could not complete {what}: {ErrorText.Of(failure)}");
        }
        else
        {
            Log(ConsoleSeverity.Info, task.Result);
            done?.Invoke();
        }

        RefreshCopy(root);
    }

    /// <summary>Keeps the problems of an action in the Console, and gives the summary line.</summary>
    private string Problems(PlayCopyResult result, string summary)
    {
        foreach (var problem in result.Problems)
        {
            host.QueueUIThread(() => host.Log.Add(problem));
        }

        return summary;
    }

    /// <summary>Stores the folder of the play copy of a linked game in the settings. Null gives the default folder
    /// again.</summary>
    private void SetCopyFolder(string root, string? folder)
    {
        var games = host.Settings.Games;
        var index = games.FindIndex(g => GameFolders.Same(g.Path, root));
        if (index < 0)
        {
            return;
        }

        games[index] = games[index] with { PlayCopy = folder };
        host.Settings.Save();
    }

    private void CloseCopy()
    {
        open?.Dispose();
        open = null;
    }

    private void Kill(GameRun run, string message)
    {
        run.Killed = true;
        run.Process.Kill();
        Log(ConsoleSeverity.Warning, message);
    }

    private void Refuse(string reason) => Log(ConsoleSeverity.Error, $"Play did not start. {reason}");

    private void Log(ConsoleSeverity severity, string message) => host.Log.Add(severity, Source, message);

    private static string JobText(Job running) => running.Task == null
        ? $"Play in {running.Target.Name}: validating {EditController.SaveText(running.Save)}..."
        : $"Play in {running.Target.Name}: installing the mods...";

    /// <summary>A request from its save to the start of its program.</summary>
    private sealed record Job(PlayTarget Target, JobKind Kind)
    {
        /// <summary>The save whose files the merge reads. A later save replaces it.</summary>
        public int Save { get; set; }

        public bool ValidateAsked { get; set; }

        /// <summary>The files of an export, or null to merge the mods of the profile.</summary>
        public TargetFiles? Files { get; init; }

        /// <summary>The mod of the files of an export. A play of the profile finds its mods at the install.</summary>
        public IReadOnlyList<PlayedMod> Mods { get; init; } = [];

        /// <summary>The status text while the game of an export runs, or null.</summary>
        public string? Status { get; init; }

        /// <summary>The folder of the play copy that the user picked, or null for the folder of the settings.</summary>
        public string? Folder { get; init; }

        public PlayCopyMode Mode { get; init; } = PlayCopyMode.Links;

        /// <summary>The worker of steps 4 and 5, or null while the gate waits.</summary>
        public Task<Prepared>? Task { get; set; }
    }

    /// <summary>The result of the worker.</summary>
    /// <param name="Folder">The folder of the play copy.</param>
    private sealed record Prepared(string Folder)
    {
        public IReadOnlyList<Diagnostic> Problems { get; init; } = [];

        public string? Refusal { get; init; }

        public string? ProbeFailure { get; init; }

        /// <summary>The open play copy, with its lock.</summary>
        public PlayCopy? Copy { get; init; }

        public PlayCopyResult? Install { get; init; }

        /// <summary>True when the worker made or completed the play copy.</summary>
        public bool Made { get; init; }

        /// <summary>The number of files of the install.</summary>
        public int Files { get; init; }

        /// <summary>The mods of the install, with their fingerprints.</summary>
        public Installed Installed { get; init; } = Installed.None;
    }

    /// <summary>The mods that a play copy holds after an install, for the test plays.</summary>
    /// <param name="Alone">True when the install holds one mod alone: an export, or a profile with one mod.</param>
    private sealed record Installed(IReadOnlyList<PlayedMod> Mods, bool Alone)
    {
        public static Installed None { get; } = new([], false);
    }

    /// <summary>The result of a check after an exit, or the error that stopped it.</summary>
    private sealed record CheckOutcome(PlayCopyResult? Result, string? Error);

    /// <summary>The game or the server of the play copy.</summary>
    private sealed class GameRun(IGameProcess process, PlayTarget target, bool isServer, int saves)
    {
        public IGameProcess Process { get; } = process;

        public PlayTarget Target { get; } = target;

        public bool IsServer { get; } = isServer;

        /// <summary>The status text while the game runs, or null for the text of a play of the profile.</summary>
        public string? Status { get; set; }

        /// <summary>The time of the clock of <see cref="GamePlay"/> at the start.</summary>
        public TimeSpan StartedAt { get; init; }

        /// <summary>The mods that the play copy holds.</summary>
        public Installed Installed { get; init; } = Installed.None;

        /// <summary>The last save of the session that the Console told about.</summary>
        public int SavesSeen { get; set; } = saves;

        public TimeSpan? StopSentAt { get; set; }

        /// <summary>True when the program had no window to close at Stop.</summary>
        public bool NoWindow { get; set; }

        public bool Killed { get; set; }
    }
}
