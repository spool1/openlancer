using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Mods;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Export;

/// <summary>
/// Mod &gt; Export mod (milestone M11): the targets with their bases, the package forms and the output folder, as the
/// Build Settings window of Unity shows them. The export runs on a worker thread. The result replaces the buttons, and
/// the Console gets each problem. Export and play installs the export of the first checked retail target into the play
/// copy of its base. Then it starts the game, as Build And Run of Unity does (decision D326).
/// </summary>
/// <remarks>
/// <para>Milestone M11c: the dialog loads the mod from its folder when it opens, so it uses the applied display name,
/// version and targets. It shows the publish checklist, and it writes into the version folder of the output root
/// (decision D566). Between the prepare and the write, the same-version guard of the publish history asks in the dialog
/// (decision D568). After the write, the dialog records the export in the history (decision D590).</para>
/// <para>The tasks of the export are in an <see cref="ExportRun"/>, which the editor updates in each frame. So a close
/// of the dialog with Escape while the export runs does not stop the export or its record in the history.</para>
/// </remarks>
internal sealed class ExportDialog : ModalDialog
{
    /// <summary>The source of the Console lines of the export.</summary>
    public const string Source = "Export";

    private const string ReadingFiles = "reading the files of the mod...";

    private readonly EditorWindow window;
    private readonly ExportRun run = new();
    private Mod? mod;
    private string? loadError;
    private string workspace = "";
    private IReadOnlyList<GameReport> bases = [];
    private readonly HashSet<string> targets = new(StringComparer.Ordinal);
    private bool dropIn = true;
    private bool flmm = true;
    private string folder = "";
    private GuardedExport? asking;

    // The texts of the header, read when the mod loads: the line below the display name and the checklist summary.
    // The fingerprints of the checklist read each file of the mod, so they come from a worker thread.
    private string modLine = "";
    private PublishSettings settings = PublishSettings.Empty;
    private Task<ChecklistFiles>? checklistRead;
    private string checklistSummary = "";
    private ExportResult? result;
    private RecordResult? recorded;
    private string? error;

    // The retail target whose export the game plays after the export, or null for Export alone.
    private ExportTarget? playTarget;

    public ExportDialog(EditorWindow window)
    {
        this.window = window;
        run.Prepared += Prepared;
        run.Written += Finish;
        run.Failed += Failed;
    }

    /// <summary>What the publish checklist reads from the files of the mod.</summary>
    private sealed record ChecklistFiles(PublishHistory History, string Content, string Page);

    protected override string Title => mod == null
        ? "Export mod###export"
        : $"Export mod: {mod.Manifest.Name} ({mod.Id}), version {mod.Manifest.Version}###export";

    /// <summary>True while the export prepares or writes, also after a close of the dialog.</summary>
    public bool Running => run.Running;

    /// <summary>Takes the results of the tasks of the export. The editor calls it in each frame, also while the dialog
    /// is closed.</summary>
    public void Update() => run.Update();

    /// <summary>Shows the dialog for a mod. The dialog loads the mod from its folder.</summary>
    /// <param name="workspace">The workspace of the mod, which holds the default output root and the publish
    /// history.</param>
    /// <param name="reports">The checks of the linked games.</param>
    public void Show(string modFolder, string workspace, IReadOnlyList<GameReport> reports)
    {
        this.workspace = workspace;
        bases = reports;
        (result, recorded, error, asking, playTarget) = (null, null, null, null, null);
        Load(modFolder);
        if (mod is { } loaded)
        {
            var saved = window.Settings.ExportOf(loaded.Folder);
            targets.Clear();
            foreach (var target in ExportTarget.All.Where(t => Available(t) && t.Refusal(loaded.Manifest) == null))
            {
                if (saved == null || saved.Targets.Contains(target.Id))
                    targets.Add(target.Id);
            }

            (dropIn, flmm) = saved == null ? (true, true) : (saved.DropIn, saved.Flmm);
            folder = saved?.Folder is { Length: > 0 } path ? path : PublishHistory.FolderOf(workspace, loaded.Id);
        }

        Open();
    }

    /// <summary>Loads the mod from its folder, with the applied name, version and targets, and reads the texts of the
    /// header.</summary>
    private void Load(string modFolder)
    {
        try
        {
            mod = Mod.Load(modFolder);
            loadError = null;
        }
        catch (ModLoadException e)
        {
            mod = null;
            loadError = e.Message;
            return;
        }

        settings = PublishSettings.Load(mod.Folder).Settings;
        var summary = settings.Summary.Split('\n')[0];
        modLine = $"Mod id: {mod.Id}. Version {mod.Manifest.Version}." + (summary.Length > 0 ? $" {summary}" : "");
        var (loaded, read, historyFolder) = (mod, settings, PublishHistory.FolderOf(workspace, mod.Id));
        var fingerprints = window.Mods.Fingerprints;
        checklistSummary = ReadingFiles;
        checklistRead = Task.Run(() => ReadChecklistFiles(loaded, read, historyFolder, fingerprints));
    }

    /// <summary>The history and the fingerprints of a mod for the publish checklist. It runs on a worker thread, with
    /// the file hashes of the Mods window, so that a new open of the dialog reads only the changed files.</summary>
    private static ChecklistFiles ReadChecklistFiles(Mod mod, PublishSettings settings, string historyFolder,
        ModFingerprint fingerprints)
    {
        var history = PublishHistory.Load(historyFolder, mod.Id).History;
        try
        {
            return new ChecklistFiles(history, fingerprints.Content(mod.Manifest, mod.Folder),
                fingerprints.Page(mod.Manifest, mod.Folder, settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that OpenLancer cannot read now. The checklist then counts no check and no export.
            return new ChecklistFiles(history, "", "");
        }
    }

    /// <summary>Takes the files of the checklist when the worker thread read them, and counts the steps with the rules
    /// of the Mods window.</summary>
    private void ChecklistRead(Mod loaded)
    {
        if (checklistRead is not { IsCompleted: true } read)
        {
            return;
        }

        checklistRead = null;
        checklistSummary = read.IsCompletedSuccessfully
            ? PublishChecklist.Of(window.Mods.ChecklistInputOf(loaded.Manifest, settings, read.Result.History,
                read.Result.Content, read.Result.Page)).Summary
            : $"OpenLancer could not read the mod: {ErrorText.Of(read.Exception!.GetBaseException())}";
    }

    /// <summary>The check of the linked game that is the base of a retail target, or null.</summary>
    internal static GameReport? BaseOf(ExportTarget target, IReadOnlyList<GameReport> reports) =>
        target.IsRetail ? reports.FirstOrDefault(r => target.BaseRefusal(r) == null) : null;

    private bool Available(ExportTarget target) => !target.IsRetail || BaseOf(target, bases) != null;

    protected override void Body()
    {
        if (mod == null)
        {
            Problem(loadError ?? "The mod did not load.");
            if (Button("Close", primary: true))
                Close();
            return;
        }

        ChecklistRead(mod);
        if (run.Running)
        {
            Header(mod);
            ImGui.Text(run.Preparing ? "Checking the mod..." : "Writing the packages...");
            return;
        }

        if (result != null)
        {
            Result(result);
            return;
        }

        Header(mod);
        if (asking != null)
        {
            Guard(asking);
            return;
        }

        Targets(mod);
        ImGui.Spacing();
        ImGui.Text("Packages");
        ImGui.SameLine();
        ImGui.Checkbox("Drop-in zip for Nexus", ref dropIn);
        ImGui.SameLine();
        ImGui.Checkbox(FlmmPackage.Checked ? "FLMM package" : "FLMM package (preview)", ref flmm);
        if (!FlmmPackage.Checked)
            ImGui.SetItemTooltip("OpenLancer did not check this package with FLMM yet. Check it with FLMM before you " +
                                 "publish it.");
        ImGui.Spacing();
        ImGui.Text($"Output folder {folder}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Change..."))
            FolderActions.Choose(window, path => folder = path);
        var versionProblem = ModVersion.SafeProblem(mod.Manifest.Version);
        if (versionProblem == null)
            CardLayout.Dim($"This export writes to {ModExport.VersionFolder(folder, mod.Manifest)}.");
        Problem(versionProblem ?? error);
        ImGui.Spacing();

        var retail = targets.Any(id => ExportTarget.Find(id)!.IsRetail);
        var ready = targets.Count > 0 && (!retail || dropIn || flmm) && versionProblem == null;
        if (Button("Export", primary: true, enabled: ready))
            Start(mod, play: null);
        // Export and play needs a checked retail target whose base has a game program.
        if (PlayTargetOf() is { } play)
        {
            ImGui.SameLine();
            if (Button("Export and play", enabled: ready && !window.GamePlay.Busy))
                Start(mod, play);
            ImGui.SetItemTooltip(window.GamePlay.Busy
                ? "A game or the test server of a play copy runs. Stop it first."
                : $"Export, then install the export for {play.Name} into the play copy of {BaseOf(play, bases)!.Root} " +
                  "and start the game. The play copy then holds this mod alone.");
        }
        ImGui.SameLine();
        if (Button("Cancel"))
            Close();
    }

    /// <summary>
    /// The header (section 13.4 of the M11c design): the display name with Change..., the id, the version and the first
    /// line of the summary, and the publish checklist with Open in Mods window.
    /// </summary>
    private void Header(Mod exported)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"Display name: {exported.Manifest.Name}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Change...##name"))
            OpenMods(PublishField.DisplayName);
        ImGui.SetItemTooltip("Open the Mods window at the display name. Players see the display name on Nexus, in FLMM " +
                             "and in the README.");
        CardLayout.Dim(modLine);
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"Publish checklist: {checklistSummary}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Open in Mods window"))
            OpenMods(null);
        ImGui.Separator();
    }

    /// <summary>Escape or the close button of the title bar while the export runs: the export goes on without the
    /// dialog. A prepare then writes nothing, and a write still records its packages in the history.</summary>
    protected override void Cancelled()
    {
        asking = null;
        if (!run.Running)
        {
            return;
        }

        window.Log.Add(ConsoleSeverity.Info, Source, run.Preparing
            ? "You closed the Export dialog. The export stops after the check of the mod and writes no package."
            : "You closed the Export dialog. The export writes its packages, and the Console shows the result.");
        run.Close();
    }

    /// <summary>Closes the dialog and shows the mod in the Mods window, at a field of the Settings tab.</summary>
    private void OpenMods(PublishField? field)
    {
        if (Running)
            return;
        Close();
        window.ShowMods(mod?.Folder, field);
    }

    /// <summary>The first checked retail target whose base has a game program, or null.</summary>
    private ExportTarget? PlayTargetOf() =>
        ExportTarget.All.FirstOrDefault(t => t.IsRetail && targets.Contains(t.Id) && BaseOf(t, bases) is { ExePath: not null });

    /// <summary>The check box of each target, with its base or the reason why it has none.</summary>
    private void Targets(Mod exported)
    {
        if (!ImGui.BeginTable("##targets", 2, ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("Targets");
        ImGui.TableSetupColumn("Base");
        ImGui.TableHeadersRow();
        foreach (var target in ExportTarget.All)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var reason = target.Refusal(exported.Manifest) ?? target.BaseRefusal(BaseOf(target, bases));
            var on = targets.Contains(target.Id) && reason == null;
            ImGui.BeginDisabled(reason != null);
            if (ImGui.Checkbox(target.Name, ref on))
            {
                if (on)
                    targets.Add(target.Id);
                else
                    targets.Remove(target.Id);
            }
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            if (reason != null)
                ImGui.TextColored(EditorTheme.Warning, reason);
            else
                ImGui.Text(target.IsRetail ? BaseText(BaseOf(target, bases)!) : "the mod folder");
        }
        ImGui.EndTable();
    }

    private static string BaseText(GameReport report) =>
        report.Kind == GameKind.Vanilla ? $"{report.Root} ({report.Name})" : report.Root!;

    /// <summary>Saves the choices and prepares the export on a worker thread. The guard runs when the prepare
    /// ends.</summary>
    private void Start(Mod exported, ExportTarget? play)
    {
        error = null;
        playTarget = play;
        var chosen = ExportTarget.All.Where(t => targets.Contains(t.Id)).ToList();
        window.Settings.SetExport(exported.Folder, new ExportChoices([.. chosen.Select(t => t.Id)], dropIn, flmm, folder));
        window.Settings.Save();
        var request = RequestOf(exported, chosen, bases, ModExport.VersionFolder(folder, exported.Manifest),
            (dropIn ? ExportForms.DropIn : 0) | (flmm ? ExportForms.Flmm : 0), out var publishProblems);
        foreach (var problem in publishProblems)
            window.Log.Add(ConsoleSeverity.Warning, Source, $"{exported.Folder}: {problem}");
        var location = Location(exported);
        run.Prepare(() => GuardedExport.Prepare(request, location, DateTime.UtcNow));
    }

    /// <summary>The publish history of a mod in the workspace of the dialog (decision D565).</summary>
    private HistoryLocation Location(Mod exported) =>
        new(PublishHistory.FolderOf(workspace, exported.Id), exported.Id, exported.Folder, window.Mods.GameRoots());

    /// <summary>The request of an export. The texts of the packages come from the publish.json of the mod (milestone
    /// M11c).</summary>
    /// <param name="publishProblems">The problems of the read of publish.json. The texts leave out a field with a
    /// problem, so the Console shows them.</param>
    internal static ExportRequest RequestOf(Mod exported, IReadOnlyList<ExportTarget> chosen, IReadOnlyList<GameReport> reports,
        string folder, ExportForms forms, out IReadOnlyList<string> publishProblems)
    {
        var publish = PublishSettings.Load(exported.Folder);
        publishProblems = publish.Problems;
        return new ExportRequest
        {
            Mod = exported,
            Targets = chosen,
            BaseOf = target => BaseOf(target, reports),
            OutputFolder = folder,
            Forms = forms,
            Publish = publish.Settings,
            StringWriter = DllStringResourceWriter.Instance,
            Thn = ThornEvaluator.Instance,
        };
    }

    /// <summary>After the prepare: a history that does not load stops the export, a conflict asks in the dialog, and
    /// else the write starts.</summary>
    private void Prepared(GuardedExport export)
    {
        if (export.Refusal is { } refusal)
        {
            Failed(refusal);
            return;
        }

        // A page link without a mark tells that the history can be lost, so the guard cannot protect a published
        // version (section 20 of the M11c design).
        if (export.Prepared!.Request.Publish.PageUrl.Length > 0 && export.History.History.Published.Count == 0)
            window.Log.Add(ConsoleSeverity.Warning, Source,
                $"{export.Prepared.Request.Mod.Id} has a page link, but the publish history has no version with the mark " +
                "\"published\". The guard cannot protect a version on Nexus without the mark. Mark the versions on " +
                "Nexus in the History tab of the Mods window.");

        if (export.Guard.Kind == GuardKind.None)
        {
            Write(export, replace: false);
            return;
        }

        // The checklist still gets the checks of the prepare.
        foreach (var warning in export.RecordChecks())
            window.Log.Add(ConsoleSeverity.Warning, Source, warning);
        asking = export;
    }

    /// <summary>The question of the same-version guard (section 13.2 of the M11c design), in place of the buttons.
    /// The first choice is the default.</summary>
    private void Guard(GuardedExport export)
    {
        var guard = export.Guard;
        ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {guard.Title}");
        ImGui.TextWrapped(guard.Text);
        Problem(error);
        ImGui.Spacing();
        var choices = GuardChoices(guard, NextVersion(export), () => ExportAsNext(export),
            () => Write(export, replace: true), () => asking = null);
        for (var i = 0; i < choices.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            if (Button(choices[i].Label, primary: i == 0, enabled: choices[i].Enabled))
            {
                error = null;
                choices[i].Action();
                return;
            }

            if (choices[i].Tooltip is { } tooltip && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(tooltip);
        }
    }

    /// <summary>
    /// The choices of the same-version guard (decision D568): "Export as &lt;next&gt;" first, then "Replace version
    /// &lt;v&gt;" for a version without the mark "published", then Cancel. Replace needs each target and form of the old
    /// export, and else it is disabled with the reason.
    /// </summary>
    /// <param name="next">The next patch version, or null when the version does not follow the rule.</param>
    internal static IReadOnlyList<Choice> GuardChoices(GuardResult guard, string? next, Action exportAsNext, Action replace,
        Action cancel)
    {
        var choices = new List<Choice>
        {
            new(next == null ? "Export as a new version" : $"Export as {next}", exportAsNext)
            {
                Enabled = next != null,
                Tooltip = next == null ? PublishMessages.VersionRule : $"Write version {next} into mod.json, then export.",
            },
        };
        if (guard.Kind == GuardKind.Exported)
            choices.Add(new Choice($"Replace version {guard.Version}", replace)
            {
                Enabled = guard.CanReplace,
                Tooltip = guard.PartialText ?? $"Write the new packages over the packages of version {guard.Version}.",
            });
        choices.Add(new Choice("Cancel", cancel));
        return choices;
    }

    /// <summary>The next patch version that the history does not have, or null.</summary>
    private static string? NextVersion(GuardedExport export) =>
        ModVersion.Next(export.Prepared!.Version, VersionPart.Patch, export.History.History.Versions);

    /// <summary>
    /// "Export as &lt;next&gt;": applies the draft of the Mods window first, when it has changes. Then it writes the
    /// next patch version into mod.json and a changelog entry into publish.json, loads the mod again and prepares the
    /// export again. The write guard of the Mods window applies to both writes.
    /// </summary>
    private void ExportAsNext(GuardedExport export)
    {
        var exported = mod!;
        var mods = window.Mods;
        if ((mods.WriteBlock(exported.Folder) ?? mods.ApplyBeforeExport(exported.Folder)) is { } block)
        {
            error = block;
            return;
        }

        PublishDraft increase;
        try
        {
            increase = PublishDraft.Open(exported.Folder);
        }
        catch (ModLoadException e)
        {
            error = e.Message;
            return;
        }

        if (increase.IncreaseVersion(VersionPart.Patch, export.History.History.Versions) is not { } next)
        {
            error = PublishMessages.VersionRule;
            return;
        }

        var result = increase.Apply(mods.WriteContext(), mods.SessionHolds(exported.Folder));
        if (result.State != ApplyState.Applied)
        {
            error = ModsWindow.ApplyRefusal(result);
            return;
        }

        window.Log.Add(ConsoleSeverity.Info, Source, $"Wrote version {next} into the mod.json of {exported.Id}.");
        mods.Refresh();
        asking = null;
        Load(exported.Folder);
        if (mod != null)
            Start(mod, playTarget);
    }

    /// <summary>Writes the packages on a worker thread.</summary>
    /// <param name="replace">True after the answer "Replace version".</param>
    private void Write(GuardedExport export, bool replace)
    {
        asking = null;
        run.Write(export, replace);
    }

    private void Failed(string reason)
    {
        error = reason;
        window.Log.Add(ConsoleSeverity.Error, Source, reason);
    }

    /// <summary>Takes the result of the write and its record in the history, and writes the problems and the summary
    /// to the Console. Export and play starts the game only while the dialog is open.</summary>
    private void Finish(ExportWritten written)
    {
        var done = written.Result;
        result = done;
        recorded = written.Record;
        foreach (var problem in done.Problems)
            window.Log.Add(problem);
        var count = done.Packages.Count(p => p.Path != null);
        window.Log.Add(done.Succeeded ? ConsoleSeverity.Info : ConsoleSeverity.Error, Source, done.Succeeded
            ? $"The export wrote {count} packages to {done.OutputFolder}."
            : ModExport.Stopped);
        foreach (var warning in written.Warnings)
            window.Log.Add(ConsoleSeverity.Warning, Source, warning);
        foreach (var line in written.Record?.Messages(done.Version) ?? [])
            window.Log.Add(ConsoleSeverity.Info, Source, line);

        window.Mods.Refresh();
        if (playTarget is not { } play || mod == null)
            return;
        if (written.Closed)
            window.Log.Add(ConsoleSeverity.Info, Source, "Play did not start, because you closed the Export dialog.");
        else if (done.Builds.TryGetValue(play, out var files) &&
                 done.Packages.Where(p => p.Target == play).All(p => p.Path != null) && BaseOf(play, bases) is { } game)
            window.GamePlay.PlayExport(game, files, new PlayedMod(mod.Id, mod.Manifest.Name, mod.Folder,
                PublishHistory.FolderOf(workspace, mod.Id), done.Content));
        else
            window.Log.Add(ConsoleSeverity.Error, Source,
                $"Play did not start. The export for {play.Name} did not complete.");
    }

    /// <summary>The result in place of the buttons: a line for each package, the number of warnings, the next step
    /// and Open folder.</summary>
    private void Result(ExportResult done)
    {
        foreach (var package in done.Packages)
        {
            if (package.Path != null)
                ImGui.Text($"{EditorIcons.CheckCircle}  {package.FileName}");
            else
                ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.TimesCircle}  {package.FileName}: refused. See the Console.");
        }
        var warnings = done.Problems.Count(p => p.Severity == DiagnosticSeverity.Warning);
        var errors = done.Problems.Count(p => p.Severity == DiagnosticSeverity.Error);
        ImGui.Text($"{errors} errors, {warnings} warnings. The Console shows each problem.");
        foreach (var line in recorded?.Messages(done.Version) ?? [])
            ImGui.TextWrapped(line);
        if (done.Packages.Any(p => p.Path != null))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(PublishMessages.ExportNextStep(done.Version));
        }
        ImGui.Spacing();
        if (Button("Open folder", enabled: Directory.Exists(done.OutputFolder)))
            FolderActions.Open(window, done.OutputFolder);
        ImGui.SameLine();
        if (Button("Open in Mods window"))
            OpenMods(null);
        ImGui.SameLine();
        if (Button("Close", primary: true))
            Close();
    }
}
