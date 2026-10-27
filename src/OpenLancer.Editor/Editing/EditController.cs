using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Editing;

/// <summary>What a save did. Play reads it (section 9.2 of the M4 design).</summary>
/// <param name="Saved">True when the save saved, also when it changed no file. False when it wrote nothing, for
/// example after an error, or when files of the mod changed outside the editor.</param>
/// <param name="Number">The number of the save, or 0 when it changed no file.</param>
/// <param name="Incomplete">True when the save journal holds the save, because a file did not move into place.</param>
internal sealed record SaveOutcome(bool Saved, int Number, bool Incomplete)
{
    public static readonly SaveOutcome NotSaved = new(false, 0, false);

    /// <summary>The outcome of a save that the save gate refused.</summary>
    public static readonly SaveOutcome Blocked = NotSaved with { Failure = SaveFailure.Blocked };

    /// <summary>Why a save that did not save stopped. A play request of an agent gives it as its error (section 7.4 of
    /// the M6c design).</summary>
    public SaveFailure Failure { get; init; }

    /// <summary>The outcome of a save that gave <paramref name="result"/>.</summary>
    /// <param name="number">The number that the session gave the save (see <see cref="EditorSession.Saved"/>).</param>
    public static SaveOutcome Of(SaveResult result, int number) => new(result.Saved, number, result.Incomplete)
    {
        Failure = !result.Saved && result.ChangedOnDisk.Count > 0 ? SaveFailure.ChangedOnDisk : SaveFailure.None,
    };
}

/// <summary>Why a save did not save.</summary>
internal enum SaveFailure
{
    /// <summary>The save saved, or it stopped for another reason. The Console tells why.</summary>
    None,

    /// <summary>The save gate found errors in the active mod.</summary>
    Blocked,

    /// <summary>Files of the active mod changed outside the editor.</summary>
    ChangedOnDisk,
}

/// <summary>
/// Runs the saves of the open profile, undo and redo, and what follows a save: the Console report, the reload of the
/// game data and the validation. It also keeps the recovery file of the unsaved edits up to date (decision D8). The
/// edits run through <see cref="CommandRunner"/>.
/// </summary>
/// <remarks>
/// <para>A save writes the files of the active mod at once. Then the game data loads again in the background from a
/// new file system of the edit view (review finding F5, roadmap rule R5). One load of the game data runs at a time
/// (see <see cref="DataLoads"/>). A save during the first load or during a reload writes its files at once, and its
/// reload starts after the running load. The validation runs in an oltool process (see
/// <see cref="ValidationRunner"/>).</para>
/// <para>A close of the window with unsaved edits asks first: Save, Don't Save or Cancel (see
/// <see cref="ClosePrompt"/>, roadmap decision D78). A crash or a kill of the editor cannot ask. So the editor also
/// writes the unsaved edits to the recovery file a moment after each edit, and in the cleanup after a crash.</para>
/// </remarks>
internal sealed class EditController(EditorWindow window) : IDisposable
{
    public const string Source = "Edit";
    private const string ValidationSource = "Validation";

    /// <summary>The Console line of a reload that waits for the running load.</summary>
    internal const string ReloadWaitsText = "The game data loads now. It loads again with the saved files after this load.";

    /// <summary>The time without edits before the editor writes the recovery file.</summary>
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(2);

    private readonly ValidationRunner validation = new();

    private ConsoleLog Log => window.Log;

    /// <summary>The state of the last validation of the session, or null before the first one.</summary>
    public ValidationState? Validation => validation.State;

    /// <summary>Runs on the UI thread when a validation of the session gives its result, after its Console
    /// lines.</summary>
    public event Action<ValidationState>? Validated
    {
        add => validation.Validated += value;
        remove => validation.Validated -= value;
    }

    /// <summary>The number of a save of the session that is not complete, or 0. The next save completes it first. Play
    /// reads it, because the game can hold parts of such a save (decision D59).</summary>
    public int IncompleteSave { get; private set; }

    /// <summary>The text for a save in the Console: "save 2", or "the saved files" for 0, the saved files of a session
    /// without a save that changed files.</summary>
    public static string SaveText(int save) => save > 0 ? $"save {save}" : "the saved files";

    public void Undo()
    {
        var label = window.Session?.Edits.UndoLabel;
        Step(session => session.Edits.Undo());
        // The undo of a delete in the Scene selects the item again (decision D249 of the M3 design).
        window.SceneEdits.Undone(label);
    }

    public void Redo() => Step(session => session.Edits.Redo());

    private void Step(Action<EditorSession> step)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        if (session.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return;
        }

        try
        {
            step(session);
        }
        catch (InvalidOperationException e)
        {
            Log.Add(ConsoleSeverity.Warning, Source, e.Message);
        }
    }

    /// <summary>
    /// Saves the edits of the active mod and writes the report to the Console. The status bar shows the summary of the
    /// report, also when the save does not start, for example without an active mod. A save that changed files starts
    /// the reload of the game data and the validation. When files of the mod changed outside the editor, a dialog asks
    /// to overwrite them.
    /// </summary>
    /// <returns>What the save did. A save of the Overwrite dialog comes later and is not part of it.</returns>
    public SaveOutcome Save() => Save(overwrite: false, leave: null);

    /// <summary>
    /// Saves as <see cref="Save()"/> does, then runs <paramref name="leave"/>, an action that ends the session, for
    /// example the close of the window. It runs when the save saved, also after the Overwrite dialog. The game data
    /// then does not load again, and no validation runs. After a save that fails, it does not run.
    /// </summary>
    public void Save(Action leave) => Save(overwrite: false, leave);

    /// <param name="overwrite">True to replace the files of the active mod that changed outside the editor.</param>
    /// <param name="leave">The action that ends the session after the save, for example a close of the profile, or
    /// null. It runs when the save leaves no unsaved edits, also after the Overwrite dialog. The game data then does
    /// not load again, and no validation runs.</param>
    private SaveOutcome Save(bool overwrite, Action? leave)
    {
        if (window.Session is not { } session)
        {
            Log.AddStatus(ConsoleSeverity.Info, SaveReport.Source, SaveReport.NoProfile);
            return SaveOutcome.NotSaved;
        }

        if (session.Edits.ActiveMod is not { } mod)
        {
            Log.AddStatus(ConsoleSeverity.Warning, SaveReport.Source, SaveReport.NoActiveMod);
            return SaveOutcome.NotSaved;
        }

        // A session that takes no edits does not save, for example when the mod is in the game folder (the write guard)
        // or when the mod is in the OpenLancer repo and the session has no confirmation (decision D5).
        if (session.NoEditReason is { } reason)
        {
            Log.AddStatus(ConsoleSeverity.Warning, SaveReport.Source, SaveReport.ReadOnly(mod.Id, reason));
            return SaveOutcome.NotSaved;
        }

        var hadEdits = session.Edits.IsDirty;
        // A retail target without a linked game gives one info line in a session (section 6.12 of the M6b design).
        foreach (var note in session.Edits.TakeTargetNotes())
        {
            Log.Add(ConsoleSeverity.Info, SaveReport.Source, note);
        }

        // The save gate runs first, so that a refused save does not stop the validation for nothing (review finding
        // R21 of the M21 design). EditSession.Save runs it again.
        if (Gate(session.Edits) is { } refused)
        {
            Report(refused, mod, 0, hadEdits);
            return SaveOutcome.Blocked;
        }

        // The oltool process of an earlier validation reads the files of the mod. So it stops before a save of the edits
        // moves files, because a file that another process has open cannot move (review finding F16).
        var stopped = hadEdits ? StopValidation() : null;
        SaveResult result;
        try
        {
            result = session.Edits.Save(overwrite);
        }
        catch (InvalidOperationException e)
        {
            Log.AddStatus(ConsoleSeverity.Warning, SaveReport.Source, SaveReport.NotStarted(mod.Id, e.Message));
            return SaveOutcome.NotSaved;
        }

        var changedFiles = result.Saved && result.Written.Count + result.Deleted.Count > 0;
        var number = result.Saved ? session.Saved(changedFiles) : 0;
        Report(result, mod, number, hadEdits);

        if (!result.Saved && result.ChangedOnDisk.Count > 0)
        {
            var files = string.Join("\n", result.ChangedOnDisk.Select(f => Path.GetRelativePath(mod.Folder, f)));
            window.Choice.Show("Files changed outside the editor",
                $"Something outside the editor changed these files of mod '{mod.Id}' after the editor read them:\n\n" +
                $"{files}\n\nReplace them with the edits? Their changes are then lost.",
                OverwriteChoices(() => Save(overwrite: true, leave)));
        }

        var outcome = SaveOutcome.Of(result, number);
        if (result.Saved && leave != null)
        {
            leave();
            return outcome;
        }

        FollowUp(session, result, number, stopped);
        return outcome;
    }

    /// <summary>
    /// The result of a save that the save gate refuses (see <see cref="EditSession.SaveProblems"/>), or null when the gate
    /// finds no error. The result tells nothing about the files, because the save does not start.
    /// </summary>
    internal static SaveResult? Gate(EditSession edits)
    {
        var problems = edits.SaveProblems();
        return problems.Any(p => p.Severity == DiagnosticSeverity.Error)
            ? new SaveResult(false, [], [], problems, [])
            : null;
    }

    /// <summary>Writes the report of a save to the Console. The summary comes last, so the status bar shows it.</summary>
    /// <param name="number">The number of the save, or 0 when it changed no file.</param>
    /// <param name="hadEdits">True when the session had unsaved edits before the save.</param>
    private void Report(SaveResult result, Mod mod, int number, bool hadEdits)
    {
        var edits = window.Session?.Edits;
        foreach (var line in SaveReport.Lines(result, mod.Folder, edits == null ? null : edits.SaveProblemOrigin))
        {
            Log.Add(line.Severity, line.Source, line.Message, line.Target, line.Cutscene, line.Origin);
        }

        var summary = SaveReport.Summary(result, mod.Id, number, hadEdits);
        Log.AddStatus(summary.Severity, summary.Source, summary.Message);
    }

    /// <summary>
    /// Starts the reload of the game data and the validation after a save that changed files. A save that is not
    /// complete starts neither, because the files of the mod are not all in place. The next save completes it first,
    /// and then they start.
    /// </summary>
    /// <param name="number">The number of the save, or 0 when it changed no file.</param>
    /// <param name="stopped">The save of the validation that stopped before the save, or null.</param>
    private void FollowUp(EditorSession session, SaveResult result, int number, int? stopped)
    {
        if (result.Incomplete)
        {
            IncompleteSave = number;
            Log.Add(ConsoleSeverity.Warning, Source,
                $"Save {number} is not complete. The game data does not load again, and the validation does not run. " +
                "Correct the problem of the file, then save again.");
            return;
        }

        // A save after an incomplete save completed the files of that save first.
        var completed = result.Saved ? IncompleteSave : 0;
        if (result.Saved)
        {
            IncompleteSave = 0;
        }

        if (number == 0 && completed > 0)
        {
            Log.Add(ConsoleSeverity.Info, Source, $"Save {completed} is complete now.");
        }

        var changed = number > 0 ? number : completed;
        if (changed > 0)
        {
            Reload(session);
            Validate(session, changed);
        }
        else if (stopped is { } save)
        {
            // The save changed no file, so the result of the stopped validation is not old.
            Validate(session, save);
        }
    }

    /// <summary>
    /// Runs an action that ends the edits of the session, for example a close of the profile. With unsaved edits, a
    /// dialog asks first: Save, Discard or Cancel.
    /// </summary>
    /// <param name="what">The action in the question, for example "closing the profile".</param>
    public void Leave(string what, Action action)
    {
        if (window.Session is not { Edits: { IsDirty: true, ActiveMod: { } mod } } session)
        {
            action();
            return;
        }

        window.Choice.Show("Unsaved edits", $"The mod '{mod.Id}' has unsaved edits. Save them before {what}?",
            LeaveChoices(() => Save(overwrite: false, leave: action), () =>
            {
                Discard(session);
                action();
            }));
    }

    /// <summary>Shows the dialog for the recovery file of an earlier session, when the active mod has one.</summary>
    public void OfferRecovery()
    {
        if (window.Session is not { Edits: { HasRecovery: true, ActiveMod: { } mod } } session)
        {
            return;
        }

        // A restore is an edit of the mod. For a mod in the OpenLancer repo, the restore is also the one confirmation
        // that each session needs before an edit (decision D5).
        var repo = session.NeedsRepoConfirmation
            ? " The mod is part of the OpenLancer repo, so its files are tracked files. A restore also allows the edits " +
              "of the mod for this session."
            : "";
        window.Choice.Show("Unsaved edits of an earlier session",
            $"An earlier session of the editor closed with unsaved edits of mod '{mod.Id}'. Restore them? Until you " +
            "restore or discard them, the editor takes no edits for the mod." + repo,
            RecoveryChoices(() => Restore(session), () => Discard(session)));
    }

    private void Restore(EditorSession session)
    {
        var problems = session.Edits.RestoreRecovery();
        foreach (var problem in problems)
        {
            Log.Add(problem);
        }

        if (problems.Count == 0)
        {
            Log.Add(ConsoleSeverity.Info, Source, "Restored the unsaved edits of the earlier session. Save them to keep them.");
            if (session.NeedsRepoConfirmation)
            {
                session.RepoEditsAllowed = true;
                Log.Add(ConsoleSeverity.Warning, Source,
                    $"The restore allowed the edits of the repo mod '{session.Edits.ActiveMod!.Id}' for this session.");
            }

            return;
        }

        window.Choice.Show("The unsaved edits do not apply",
            "The files of the mod changed after the earlier session wrote its unsaved edits, so the edits do not apply. " +
            "The Console shows the files. Discard the edits?",
            NotAppliedChoices(() => Discard(session)));
    }

    // The first choice of a dialog is its default button, so Enter clicks it (see ChoiceDialog). Each dialog of the
    // edits puts the choice that loses no work first.

    /// <summary>The choices of the dialog for files of the active mod that changed outside the editor. Cancel is the
    /// first choice, so Enter does not discard the changes of the files.</summary>
    internal static IReadOnlyList<Choice> OverwriteChoices(Action overwrite) =>
        [new Choice("Cancel", () => { }), new Choice("Overwrite", overwrite)];

    /// <summary>The choices of the dialog for unsaved edits before an action that ends the session. Save is the first
    /// choice, so Enter keeps the edits.</summary>
    internal static IReadOnlyList<Choice> LeaveChoices(Action save, Action discard) =>
        [new Choice("Save", save), new Choice("Discard", discard), new Choice("Cancel", () => { })];

    /// <summary>The choices of the dialog for the recovery file of an earlier session. Restore is the first choice, so
    /// Enter keeps the edits.</summary>
    internal static IReadOnlyList<Choice> RecoveryChoices(Action restore, Action discard) =>
        [new Choice("Restore", restore), new Choice("Discard", discard), new Choice("Later", () => { })];

    /// <summary>The choices of the dialog for a recovery file whose edits do not apply. Later is the first choice, so
    /// Enter keeps the recovery file.</summary>
    internal static IReadOnlyList<Choice> NotAppliedChoices(Action discard) =>
        [new Choice("Later", () => { }), new Choice("Discard", discard)];

    /// <summary>
    /// The commit of the field of a text key, for example <c>ids_name</c> (see <see cref="CommitGesture"/>). It gives
    /// the section a new text of the active mod through <see cref="FieldCommands.SetText"/>. When other sections of the
    /// item show the same text, for example a gun and its good, a dialog asks first whether they get the new text too
    /// (review finding F12).
    /// </summary>
    /// <remarks>An end that is not interactive, for example when the window closes, cannot show the dialog. Then only
    /// this section gets the new text, as with the choice "Only this one", which changes the least. The recovery file
    /// then keeps the edit. A text that the command refuses, for example an empty text, ends the field with the reason
    /// and asks nothing.</remarks>
    /// <param name="run">Makes the plan of the edit and runs it, as
    /// <see cref="CommandRunner.Run(Func{EditSession, EditPlan})"/> does.</param>
    /// <param name="ask">Shows a dialog: its title, its message and its choices.</param>
    internal static Func<string, bool, string?> TextKeyCommit(EditSession edits, FieldBinding field,
        Action<Func<EditSession, EditPlan>> run, Action<string, string, IReadOnlyList<Choice>> ask) =>
        (text, interactive) =>
        {
            if (FieldCommands.SetText(edits, field.Section, field.Name, text) is { CanRun: false } refused)
            {
                return refused.Refusals.FirstOrDefault()?.Message ?? $"'{field.Name}' did not change.";
            }

            void Set(IReadOnlyList<SectionRef>? also) =>
                run(session => FieldCommands.SetText(session, field.Section, field.Name, text, also));

            var shared = interactive ? ItemEdits.SharedText(edits, field.Section, field.Name) : [];
            if (shared.Count == 0)
            {
                Set(null);
                return null;
            }

            ask("The same text",
                $"These sections of the item show the same text as {field.Section.Id}: " +
                $"{string.Join(", ", shared.Select(s => s.Id.ToString()))}. Give them the new text too?",
            [
                new Choice("All of them", () => Set(shared)),
                new Choice("Only this one", () => Set(null)),
                new Choice("Cancel", () => { }),
            ]);
            return null;
        };

    /// <summary>Deletes the recovery file of the open session, for example when the user closes the window without a
    /// save.</summary>
    public void DiscardRecovery()
    {
        if (window.Session is { } session)
        {
            Discard(session);
        }
    }

    /// <summary>Deletes the recovery file: the unsaved edits of an earlier session, or of this session.</summary>
    private void Discard(EditorSession session)
    {
        try
        {
            session.Edits.DiscardRecovery();
            session.RecoveryWritten();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Add(ConsoleSeverity.Error, Source, "The editor could not delete the recovery file: " + e.Message);
        }
    }

    /// <summary>
    /// Writes the recovery file a moment after the last edit. Call it each frame.
    /// </summary>
    /// <param name="editing">True while a field edit or a drag of the gizmo is open (see
    /// <see cref="EditorWindow.IsEditing"/>). The file waits for its end.</param>
    public void Update(bool editing)
    {
        if (window.Session is { RecoveryOutdated: true } session && !editing &&
            DateTime.UtcNow - session.LastChange >= RecoveryDelay)
        {
            WriteRecovery(session);
        }
    }

    /// <summary>Writes the unsaved edits to the recovery file now, for example when the window closes.</summary>
    public void WriteRecovery(EditorSession session)
    {
        foreach (var problem in session.Edits.WriteRecovery())
        {
            Log.Add(problem, "The recovery file does not keep this edit: ");
        }

        session.RecoveryWritten();
    }

    /// <summary>
    /// Loads the game data again from the edit view. While the first load or a reload runs, the reload starts after it
    /// ends, because each load uses the resources of the session (see <see cref="DataLoads"/>).
    /// </summary>
    private void Reload(EditorSession session)
    {
        if (RequestReload(session.Loads, () => StartReload(session)) is { } waits)
        {
            Log.Add(ConsoleSeverity.Info, Source, waits);
        }
    }

    /// <summary>
    /// Asks the loads for a reload after a save (see <see cref="DataLoads.Reload"/>). Returns the Console line when the
    /// reload waits for the running load, else null. More saves during one load give one line, because they give one
    /// reload. A save while the window closes gives no reload and no line.
    /// </summary>
    internal static string? RequestReload(DataLoads loads, Func<DataLoad> start)
    {
        var waited = loads.ReloadWaits;
        loads.Reload(start);
        return loads.ReloadWaits && !waited ? ReloadWaitsText : null;
    }

    /// <summary>Starts a reload on the UI thread. It reads the saved files now, and gives the load that runs in the
    /// background.</summary>
    private DataLoad StartReload(EditorSession session)
    {
        Log.SkipKnownEngineLines = true;
        // Each reload reads its own file system of the edit view, which no later save changes.
        var fs = session.Edits.EditView.CreateFileSystem();
        var saves = session.Saves;
        // The engine lines of the reload form a batch, which the Console compares with the base run.
        var batch = window.BeginConsoleBatch();
        return () =>
        {
            GameDataManager? data = null;
            string? error = null;
            try
            {
                data = new GameDataManager(new GameItemDb(fs), session.Resources);
                // Without a UI thread, the load adds no shapes to the resource manager. It has them from the first
                // load, and a second add throws.
                data.LoadData(null);
            }
            catch (Exception e)
            {
                (data, error) = (null, e.Message);
            }

            ConsoleLog.EndBatch(batch);
            // FLLog gives its lines to the UI thread in order. So after the flush, the lines of the load come first.
            FLLog.Flush();
            return () => Reloaded(session, data, error, saves);
        };
    }

    private void Reloaded(EditorSession session, GameDataManager? data, string? error, int saves)
    {
        session.ReloadEnded(data);
        // The last line of FLLog can follow the flush by a moment, so the log adds all engine lines again one step later.
        window.QueueUIThread(() => Log.SkipKnownEngineLines = window.Session?.IsReloading == true);
        if (window.Session != session)
        {
            return;
        }

        if (data != null)
        {
            window.GameDataReplaced(data);
            Log.Add(ConsoleSeverity.Info, Source, $"The game data loaded again after save {saves}.");
        }
        else
        {
            Log.Add(ConsoleSeverity.Error, Source,
                $"The game data did not load again after save {saves}: {error} The editor shows the data before the save.");
        }
    }

    /// <summary>
    /// Validates the saved files as they are now, with the number of the last save of the session. Play calls it when
    /// no validation of that save exists or runs, and never for a save that is not complete (sections 9.3 and 12.1 of
    /// the M4 design). A running validation stops.
    /// </summary>
    public void ValidateNow()
    {
        if (window.Session is { } session)
        {
            Validate(session, session.Saves);
        }
    }

    /// <summary>Starts the validation of a save. Without an active mod, it validates what all mods add.</summary>
    private void Validate(EditorSession session, int save)
    {
        if (validation.State is { Running: true } running)
        {
            Log.Add(ConsoleSeverity.Info, ValidationSource,
                $"The validation of {SaveText(running.Save)} stopped. {Capital(SaveText(save))} replaces it.");
        }

        Log.Add(ConsoleSeverity.Info, ValidationSource, $"Validating {SaveText(save)}...");
        validation.Start(session.Profile.FilePath, session.Edits.ActiveMod?.Id, save, window,
            result => ValidationDone(session, result));
    }

    /// <summary>Writes the result to the Console. The runner then raises <see cref="Validated"/>.</summary>
    private void ValidationDone(EditorSession session, ValidationResult result)
    {
        if (window.Session != session)
        {
            return;
        }

        var save = SaveText(result.Save);
        if (result.Problems is not { } problems)
        {
            Log.Add(ConsoleSeverity.Error, ValidationSource, $"The validation of {save} could not run: {result.Failure}");
        }
        else
        {
            var errors = problems.Count(p => p.Severity == DiagnosticSeverity.Error);
            var warnings = problems.Count - errors;
            var adds = session.Edits.ActiveMod == null ? "that the mods add" : "that the mod adds";
            Log.Add(errors > 0 ? ConsoleSeverity.Error : ConsoleSeverity.Info, ValidationSource, errors > 0
                ? $"Validation of {save} failed: {errors} errors and {warnings} warnings {adds}."
                : $"Validation OK for {save}: no errors, {warnings} warnings {adds}.");
            // The command reports only the problems that the active mod adds, or that all mods add without an active mod
            // (section 3.6 of the console filter design).
            var origin = session.Edits.ActiveMod is { } active
                ? MessageOrigin.Mod(active.Id, ModEvidence.ActiveMod)
                : MessageOrigin.SomeMod;
            foreach (var problem in problems)
            {
                Log.Add(problem with { Origin = problem.Origin ?? origin }, $"{Capital(save)}: ");
            }
        }
    }

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Stops the running validation before a save. Returns the number of its save, or null when none
    /// ran.</summary>
    private int? StopValidation()
    {
        if (validation.State is not { Running: true } running)
        {
            return null;
        }

        // The validation stops without a result. Usually a validation of the next save, or of the same files again,
        // follows. A save that is not complete or that throws starts none, and the gate and the reload trigger of Play
        // handle that (sections 9.3 and 12.1 of the M4 design).
        validation.Stop();
        Log.Add(ConsoleSeverity.Info, ValidationSource,
            $"The validation of {SaveText(running.Save)} stopped before the next save.");
        return running.Save;
    }

    /// <summary>Stops the validation of the session and forgets its state, for example when the profile
    /// closes.</summary>
    public void Stop()
    {
        validation.Reset();
        IncompleteSave = 0;
    }

    public void Dispose() => validation.Dispose();
}
