using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibreLancer.Dialogs;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Export;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>The actions of the Mods window (section 12 of the M11c design): Apply, the checks, Play, the marks, the
/// profile actions, Export, Rename and Duplicate.</summary>
internal sealed partial class ModsWindow
{
    private static readonly FileDialogFilters ImageFilters = new(new FileFilter("PNG or JPEG image", "png", "jpg", "jpeg"));

    // The prepare of "Play this mod alone" on a worker thread.
    private (ModRow Row, PlayTarget Target, Task<PreparedExport> Task)? playPrepare;

    /// <summary>The reason of an action that waits while Play in the game installs the mods into a play copy.</summary>
    private const string InstallingText =
        "Play in the game installs the mods into a play copy now. Wait until the game starts.";

    /// <summary>The reason of an action that waits while an export runs.</summary>
    private const string ExportRunsText = "An export runs now. Wait until it ends.";

    /// <summary>The reason of Apply in the Export dialog when a file changed on disk. The dialog cannot ask the
    /// question of the Mods window.</summary>
    private const string ChangedBeforeExport =
        "A file of the mod changed outside the Mods window. Click Apply in the Mods window and answer its question. " +
        "Then export again.";

    /// <summary>
    /// Why an action cannot change the files of a mod now, or null (section 10.3 of the M11c design): a play copy
    /// installs, an export runs, or the write guard refuses the mod (decision D587). The window keeps the result of the
    /// guard until the next scan.
    /// </summary>
    public string? WriteBlock(string folder)
    {
        if (ChangeBlock() is { } busy)
        {
            return busy;
        }

        var key = Path.GetFullPath(folder);
        if (!guards.TryGetValue(key, out var guard))
        {
            guard = ModWrite.Guard(key, WriteContext());
            guards[key] = guard;
        }

        return guard;
    }

    /// <summary>Why an action cannot change a mod or the profile file now, or null: a play copy installs or an export
    /// runs.</summary>
    private string? ChangeBlock() =>
        window.GamePlay.Installing ? InstallingText : window.ExportRuns ? ExportRunsText : null;

    /// <summary>
    /// Apply of the publish draft (section 10.7 of the M11c design). A file that changed on disk asks: Load again,
    /// Replace or Cancel. After the write, unused images ask before a delete, and a change of the targets opens the
    /// profile again. <paramref name="done"/> runs when Apply wrote the files.
    /// </summary>
    public void ApplyDraft(Action? done = null) => Apply(done, replace: false);

    /// <summary>
    /// Apply of the draft of a mod before "Export as &lt;next&gt;" of the Export dialog (section 13.2 of the M11c
    /// design). The Export dialog cannot show a second question. So a file that changed on disk stops the apply with a
    /// reason. The question about unused images and the new open of the profile wait until no dialog is open and no
    /// export runs.
    /// </summary>
    /// <returns>The reason why the draft did not apply, or null when it applied or has no changes.</returns>
    public string? ApplyBeforeExport(string folder)
    {
        if (Draft is not { HasChanges: true } draft || !GameFolders.Same(draft.ModFolder, folder))
        {
            return null;
        }

        var result = draft.Apply(WriteContext(), SessionHolds(folder));
        if (result.State != ApplyState.Applied)
        {
            return ApplyRefusal(result);
        }

        Applied(draft);
        afterApply = (draft, result);
        return null;
    }

    /// <summary>The reason of an Apply that did not write, for a place that cannot ask the question of a file that
    /// changed on disk.</summary>
    public static string ApplyRefusal(ApplyResult result) =>
        result.State == ApplyState.ChangedOnDisk ? ChangedBeforeExport : result.Message ?? "";

    /// <summary>Runs the steps of an Apply of the Export dialog that need a question, after the dialog
    /// closed.</summary>
    private void AfterExportApply()
    {
        if (afterApply is not { } follow || window.ExportRuns || window.DialogOpen)
        {
            return;
        }

        afterApply = null;
        FollowUp(follow.Draft, follow.Result, null);
    }

    private void Apply(Action? done, bool replace)
    {
        if (Draft is not { } draft)
        {
            done?.Invoke();
            return;
        }

        if (WriteBlock(draft.ModFolder) is { } block)
        {
            Refused(block);
            return;
        }

        var result = draft.Apply(WriteContext(), SessionHolds(draft.ModFolder), replace);
        if (result.State == ApplyState.Refused)
        {
            Refused(result.Message!);
            return;
        }

        if (result.State == ApplyState.ChangedOnDisk)
        {
            window.Choice.Show("Apply the publish settings", result.Message!,
            [
                new Choice("Load again", () =>
                {
                    OpenDraft(draft.ModFolder);
                    Refresh();
                }),
                new Choice("Replace", () => Apply(done, replace: true)),
                new Choice("Cancel", () => { }) { Default = true },
            ]);
            return;
        }

        Applied(draft);
        FollowUp(draft, result, done);
    }

    /// <summary>The Console line of an Apply that wrote the files, and a new scan.</summary>
    private void Applied(PublishDraft draft)
    {
        window.Log.Add(ConsoleSeverity.Info, Source, $"Applied the publish settings of {draft.Applied.Name} ({draft.ModId}).");
        Refresh();
    }

    /// <summary>The steps after an Apply: the question about unused images, then a new open of the profile when the
    /// targets of a mod of the profile changed. Delete is permanent, so "Keep the files" is the default button, and
    /// Escape keeps the files too.</summary>
    private void FollowUp(PublishDraft draft, ApplyResult result, Action? done)
    {
        var reopen = result.ReopenProfile && InOpenProfile(draft.ModFolder);
        Action finish = reopen ? () => window.ReopenProfile(done) : () => done?.Invoke();
        if (result.UnusedImages.Count == 0)
        {
            finish();
            return;
        }

        window.Choice.Show("Unused images",
            PublishMessages.DeleteImages(result.UnusedImages.Count) + "\n\n" +
            string.Join("\n", result.UnusedImages.Select(Path.GetFileName)),
        [
            new Choice("Delete", () =>
            {
                foreach (var warning in draft.DeleteImages(result.UnusedImages, WriteContext(), SessionHolds(draft.ModFolder)))
                {
                    window.Log.Add(ConsoleSeverity.Warning, Source, warning);
                }

                Refresh();
                finish();
            }),
            new Choice("Keep the files", finish) { Default = true },
        ], cancel: finish);
    }

    /// <summary>Shows the reason why an action did not run, and writes it to the Console.</summary>
    private void Refused(string reason)
    {
        window.Log.Add(ConsoleSeverity.Error, Source, reason);
        window.Choice.Show("Mods", reason, [new Choice("OK", () => { })]);
    }

    /// <summary>True when the open profile lists the mod folder.</summary>
    public bool InOpenProfile(string folder) =>
        window.Session?.Profile.Mods.Any(m => GameFolders.Same(m.Folder, folder)) == true;

    /// <summary>Checks a mod on a worker thread (decision D572). The result goes into its publish history.</summary>
    public void CheckNow(ModRow row) =>
        Checks.Request(new ModCheckRequest(row.Folder, Workspace, window.LinkedGameReports(), GameRoots()));

    private void CheckDone(ModCheckResult result)
    {
        foreach (var warning in result.Warnings)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, warning);
        }

        window.Log.Add(result.Errors > 0 ? ConsoleSeverity.Warning : ConsoleSeverity.Info, Source,
            $"Checked {result.Folder}: {Plural.Count(result.Errors, "error")}, " +
            $"{Plural.Count(result.WarningCount, "warning")}. Show in Console lists them.");
        Refresh();
    }

    /// <summary>Writes the problems of the last check of a mod to the Console, and shows the Console.</summary>
    public void ShowProblems(ModRow row)
    {
        if (Checks.ResultOf(row.Folder) is not { } result)
        {
            window.Log.Add(ConsoleSeverity.Info, Source,
                $"OpenLancer did not check {row.Id} in this session. Click Check now.");
        }
        else
        {
            window.Log.Add(ConsoleSeverity.Info, Source,
                $"The check of {row.Id} at {PublishMessages.Time(result.Time)} found " +
                $"{Plural.Count(result.Errors, "error")} and {Plural.Count(result.WarningCount, "warning")}:");
            foreach (var problem in result.Problems)
            {
                window.Log.Add(problem);
            }
        }

        window.ShowPanel(Logs.ConsolePanel.PanelId);
    }

    /// <summary>"Mark as tested": records a test of the current content outside OpenLancer (decision D569).</summary>
    public void MarkAsTested(ModRow row) =>
        window.Choice.Show("Mark as tested", PublishMessages.MarkAsTested(row.Name),
        [
            new Choice("Mark", () =>
            {
                var play = new HistoryPlay(DateTime.UtcNow, null, row.Content, Alone: true, 0, Manual: true);
                RecordHistory(row, "mark", h => h.AddPlay(play));
            }),
            new Choice("Cancel", () => { }),
        ]);

    /// <summary>Gives a version the mark "published", or removes the mark, in the history of a mod.</summary>
    public void Mark(ModRow row, string version, string? page)
    {
        RecordHistory(row, "mark", h =>
        {
            if (page == null)
            {
                h.Unmark(version);
            }
            else
            {
                h.Mark(version, DateTime.UtcNow, page);
            }
        });
    }

    /// <summary>Changes the publish history of a mod, writes the warnings to the Console and reads the mods
    /// again.</summary>
    private void RecordHistory(ModRow row, string record, Action<PublishHistory> change)
    {
        var location = new HistoryLocation(PublishHistory.FolderOf(Workspace, row.Id), row.Id, row.Folder, GameRoots());
        foreach (var message in PublishHistory.Update(location, record, change).Messages)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, message);
        }

        Refresh();
    }

    /// <summary>The games that "Play this mod alone" can start for a mod (decision D577): each linked game with a game
    /// program that is the base of a retail target of the mod.</summary>
    public IReadOnlyList<PlayTarget> PlayGames(ModManifest manifest) => PlayGamesOf(manifest, window.GamePlay.Targets);

    internal static IReadOnlyList<PlayTarget> PlayGamesOf(ModManifest manifest, IEnumerable<PlayTarget> targets) =>
    [
        .. targets.Where(t => ExportTarget.Find(t.Game.Target ?? "") is { IsRetail: true } target &&
                              target.Refusal(manifest) == null && target.BaseRefusal(t.Game) == null),
    ];

    /// <summary>
    /// "Play this mod alone in &lt;game&gt;" (section 12.7 of the M11c design): the build of the export for the game,
    /// installed into its play copy, without a package. For the active mod with unsaved edits, it asks to save first.
    /// </summary>
    public void PlayAlone(ModRow row, PlayTarget target)
    {
        if (PlayBlock() is { } block)
        {
            window.Log.Add(ConsoleSeverity.Warning, GamePlay.Source, $"Play did not start. {block}");
            return;
        }

        if (SessionHolds(row.Folder) && window.Session is { Edits.IsDirty: true } session)
        {
            window.Choice.Show("Play this mod alone", "Play uses the saved files. Save the changes first?",
            [
                new Choice("Save", () =>
                {
                    if (window.Edits.Save().Saved && window.Session == session && !session.Edits.IsDirty)
                    {
                        StartPlay(row, target);
                    }
                }),
                new Choice("Cancel", () => { }),
            ]);
            return;
        }

        StartPlay(row, target);
    }

    /// <summary>Why "Play this mod alone" cannot start now, or null.</summary>
    public string? PlayBlock() =>
        playPrepare != null || window.GamePlay.Busy ? "Play in the game prepares or runs a game now. Stop it first." : null;

    private void StartPlay(ModRow row, PlayTarget target)
    {
        var exportTarget = ExportTarget.Find(target.Game.Target ?? "")!;
        var root = PublishHistory.FolderOf(Workspace, row.Id);
        window.Log.Add(ConsoleSeverity.Info, GamePlay.Source, $"Play {row.Name} alone in {target.Name}: building the mod...");
        playPrepare = (row, target, Task.Run(() =>
        {
            var mod = Mod.Load(row.Folder);
            var request = ExportDialog.RequestOf(mod, [exportTarget], [target.Game], root, 0, out _) with
            {
                // Play writes no file, so it needs no package and no check of the output folder.
                ChecklistOnly = true,
            };
            return ModExport.Prepare(request);
        }));
    }

    /// <summary>Takes the build of "Play this mod alone" and starts the game.</summary>
    private void PlayPrepared()
    {
        if (playPrepare is not { Task.IsCompleted: true } done)
        {
            return;
        }

        playPrepare = null;
        if (done.Task.Exception?.GetBaseException() is { } failure)
        {
            window.Log.Add(ConsoleSeverity.Error, GamePlay.Source, $"Play did not start. {ErrorText.Of(failure)}");
            return;
        }

        var prepared = done.Task.Result;
        foreach (var problem in prepared.Problems)
        {
            window.Log.Add(problem);
        }

        var exportTarget = ExportTarget.Find(done.Target.Game.Target ?? "")!;
        if (prepared.Refused || prepared.Problems.Any(p => p.Severity == DiagnosticSeverity.Error) ||
            !prepared.Builds.TryGetValue(exportTarget, out var files))
        {
            window.Log.Add(ConsoleSeverity.Error, GamePlay.Source, "Play did not start. Fix the errors, then play again.");
            return;
        }

        var row = done.Row;
        var mod = new PlayedMod(row.Id, row.Name, row.Folder, PublishHistory.FolderOf(Workspace, row.Id), prepared.Content);
        window.GamePlay.PlayExport(done.Target.Game, files, mod, alone: true);
    }

    /// <summary>Export... of a mod (section 12.6 of the M11c design), also from Mod &gt; Export mod. A draft with
    /// changes asks first, because the export uses the applied settings.</summary>
    public void Export(string folder)
    {
        if (Draft is { HasChanges: true } draft && GameFolders.Same(draft.ModFolder, folder))
        {
            window.Choice.Show("Export mod", "Apply the publish settings first? The export uses the applied settings.",
            [
                new Choice("Apply", () => ApplyDraft(() => window.ShowExport(folder))),
                new Choice("Export without them", () => window.ShowExport(folder)),
                new Choice("Cancel", () => { }),
            ]);
            return;
        }

        window.ShowExport(folder);
    }

    /// <summary>Opens the folder of the last export of a mod, or its output root.</summary>
    public void OpenExportFolder(ModRow row)
    {
        var folder = row.History.Newest?.Folder is { } last && Directory.Exists(last)
            ? last
            : PublishHistory.FolderOf(Workspace, row.Id);
        FolderActions.Open(window, folder);
    }

    /// <summary>Opens the file dialog of Add images. The files go into the draft (decision D586).</summary>
    public void AddImages()
    {
        if (Draft is not { } draft)
        {
            return;
        }

        try
        {
            FileDialog.OpenMultiple(paths => window.QueueUIThread(() =>
            {
                if (Draft != draft || paths.Length == 0)
                {
                    return;
                }

                var refused = draft.AddImages(paths, GameRoots());
                foreach (var reason in refused)
                {
                    window.Log.Add(ConsoleSeverity.Warning, Source, reason);
                }

                if (refused.Count > 0)
                {
                    window.Choice.Show("Add images", string.Join("\n\n", refused), [new Choice("OK", () => { })]);
                }
            }), ImageFilters);
        }
        catch (Exception e)
        {
            // The file dialog of the system can fail, for example without a desktop portal.
            window.Log.Add(ConsoleSeverity.Error, Source, $"The file dialog did not open: {e.Message}");
        }
    }

    /// <summary>Why the window cannot change the profile file now, or null: the profile is in a game folder, or in the
    /// OpenLancer repo without the allowance (decisions D584 and D587). Also while the game data of the profile loads,
    /// a play copy installs or an export runs (section 10.3 of the M11c design).</summary>
    public string? ProfileBlock()
    {
        if (window.Session?.Profile.FilePath is not { } path)
        {
            return "No profile is open.";
        }

        if (ModWrite.ProfileGuard(path, GameRoots()) is { } guard)
        {
            return guard;
        }

        if (ProfileNeedsAllowance)
        {
            return "This profile is part of the OpenLancer repo. Its file is a tracked file. " +
                   "Allow edits of this repo profile first.";
        }

        return ChangeBlock() ?? (window.CanChangeProfile ? null : EditorWindow.ProfileLoading);
    }

    /// <summary>True when the open profile is a file of the OpenLancer repo whose edits the user did not allow.</summary>
    public bool ProfileNeedsAllowance => window.Session?.Profile.FilePath is { } path &&
                                         RepoCheck.RepoRoot(Path.GetDirectoryName(path)!) != null &&
                                         !repoAllowed.Contains(path);

    /// <summary>Allows the edits of the open profile in the OpenLancer repo until the editor closes (decision
    /// D584).</summary>
    public void AllowProfile()
    {
        if (window.Session?.Profile.FilePath is { } path)
        {
            repoAllowed.Add(path);
            window.Log.Add(ConsoleSeverity.Warning, Source,
                $"Allowed the edits of the repo profile {path} until the editor closes.");
        }
    }

    /// <summary>Add to profile (section 12.4 of the M11c design): the mod goes to the end of the load order, and the
    /// profile opens again.</summary>
    public void AddToProfile(ModRow row)
    {
        if (window.Session?.Profile.FilePath is not { } path)
        {
            return;
        }

        window.Leave("adding the mod to the profile", () =>
        {
            try
            {
                ModWorkspace.AddToProfile(path, row.Folder);
                window.Log.Add(ConsoleSeverity.Info, Source, $"Added {row.Id} to the profile {Path.GetFileNameWithoutExtension(path)}.");
            }
            catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException)
            {
                Refused(e.Message);
                return;
            }

            window.OpenProfileNow(path);
            Refresh();
        });
    }

    /// <summary>
    /// Remove from profile (section 12.4 of the M11c design): the profile file loses the mod, and the folder stays. A
    /// removed active mod makes the last mod of the profile the active mod. The profile opens again.
    /// </summary>
    public void RemoveFromProfile(ModRow row)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        var path = session.Profile.FilePath;
        window.Leave("removing the mod from the profile", () =>
        {
            try
            {
                ModWorkspace.RemoveFromProfile(path, row.Folder);
            }
            catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException)
            {
                Refused(e.Message);
                return;
            }

            window.Log.Add(ConsoleSeverity.Info, Source,
                $"Removed {row.Id} from the profile {Path.GetFileNameWithoutExtension(path)}. Its folder stays.");
            if (session.Edits.ActiveMod is { } active && GameFolders.Same(active.Folder, row.Folder))
            {
                var others = session.Profile.Mods.Where(m => !GameFolders.Same(m.Folder, row.Folder)).ToList();
                window.Settings.SetActiveMod(path, others.Count > 0 ? others[^1].Id : null);
                window.Settings.Save();
            }

            window.OpenProfileNow(path);
            Refresh();
        });
    }

    /// <summary>Opens the Rename dialog of a mod (section 12.3 of the M11c design). The rename changes the selected
    /// mod, so the question about the publish draft runs first (decision D571).</summary>
    public void Rename(ModRow row) => AskDraft(() => renameDialog.Show(WithAppliedName(row)));

    /// <summary>Why Rename cannot run now, or null: the write guard and the busy states of <see cref="WriteBlock"/>,
    /// and the load of the game data of the open profile. The rename closes the session of a mod of the profile, and a
    /// session that loads cannot close.</summary>
    public string? RenameBlock(ModRow row) =>
        WriteBlock(row.Folder) ??
        (!window.CanChangeProfile && InOpenProfile(row.Folder) ? EditorWindow.ProfileLoading : null);

    /// <summary>The request of a rename of a mod, with the workspace and the game folders of the open
    /// profile.</summary>
    internal RenameRequest RenameRequestOf(ModRow row, string newId) =>
        new(row.Folder, newId, Workspace)
        {
            RecentProfiles = [.. window.Settings.RecentProfiles.Select(r => r.Path)],
            GameRoots = GameRoots(),
            Busy = RenameBusy(row.Folder),
            SessionHolds = SessionHolds(row.Folder),
        };

    /// <summary>True while a game of a play copy prepares or runs, an export runs, or a check of the mod runs or waits.
    /// A check writes the publish history of the old id.</summary>
    internal bool RenameBusy(string folder) =>
        window.GamePlay.Busy || window.ExportRuns || playPrepare != null || Checks.IsBusyWith(folder);

    /// <summary>The changes of the settings of the editor for a planned rename, for the plan of the dialog.</summary>
    internal IReadOnlyList<string> RenameSettingsChanges(RenamePlan plan, RenameRequest request) =>
        window.Settings.RenameChanges(plan, request.Workspace);

    /// <summary>
    /// Runs a rename after the plan dialog. When the mod is in the open profile, the leave question runs and the
    /// session closes first, so that the editor releases the lock of the mod. Then the editor changes its settings and
    /// opens the profile again.
    /// </summary>
    /// <param name="request">The request of the plan that the dialog showed. It keeps the workspace and the game
    /// folders of the open profile, because the workspace of the window changes when the session closes.</param>
    internal void RunRename(RenameRequest request)
    {
        var profile = window.Session?.Profile.FilePath;
        if (profile == null || !InOpenProfile(request.ModFolder))
        {
            RenameNow(request, null);
            return;
        }

        window.Leave("renaming the mod", () =>
        {
            window.CloseProfileNow();
            RenameNow(request, profile);
        });
    }

    private void RenameNow(RenameRequest request, string? reopen)
    {
        // The session is closed now, so the plan runs again without its lock.
        var plan = ModFolderRename.Plan(request with { Busy = RenameBusy(request.ModFolder), SessionHolds = false });
        var result = ModFolderRename.Run(plan);
        foreach (var line in result.Log)
        {
            window.Log.Add(ConsoleSeverity.Info, Source, line);
        }

        if (result.Done)
        {
            window.Settings.RenameMod(plan, request.Workspace);
            window.Settings.Save();
            if (Draft != null && GameFolders.Same(Draft.ModFolder, plan.OldFolder))
            {
                Draft = null;
            }

            Select(plan.NewFolder);
            window.Log.Add(ConsoleSeverity.Info, Source, $"Renamed the mod {plan.OldId} to {plan.NewId}.");
        }
        else
        {
            Refused(result.Error!);
        }

        if (reopen != null)
        {
            window.OpenProfileNow(reopen);
        }

        Refresh();
    }

    /// <summary>Opens the Duplicate dialog of a mod (section 12.2 of the M11c design). The window then selects the
    /// copy, so the question about the publish draft runs first (decision D571).</summary>
    public void Duplicate(ModRow row) => AskDraft(() => duplicateDialog.Show(WithAppliedName(row)));

    /// <summary>The row with the applied display name of the draft. Apply in the question about the draft can change
    /// the name, and the scan that reads the new name ends later.</summary>
    private ModRow WithAppliedName(ModRow row) =>
        Draft is { } draft && row.Manifest is { } manifest && GameFolders.Same(draft.ModFolder, row.Folder)
            ? row with { Manifest = manifest with { Name = draft.Applied.Name } }
            : row;

    /// <summary>The mods folder of the workspace.</summary>
    public string ModsFolder => Path.Combine(Workspace, ModWorkspace.ModsFolder);

    /// <summary>Selects a new mod of the window, for example a copy, and reads the mods again. The caller asks about
    /// the draft before it makes the mod.</summary>
    internal void Created(string folder)
    {
        Select(folder);
        Refresh();
    }

    /// <summary>New mod... (section 12.1 of the M11c design): with an open profile, the mod goes into the profile and
    /// becomes the active mod, as Mod &gt; New mod does. Without a profile, it goes into the mods folder only.</summary>
    private void NewMod() => AskDraft(() => window.NewModFromModsWindow(Created));
}
