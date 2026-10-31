using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Studio.Agent;

/// <summary>A step of the publish checklist in the result of <c>publish_checklist</c>.</summary>
/// <param name="Missing">What is missing and why it is necessary. Empty when the step is complete.</param>
internal sealed record ChecklistStepRow(int Number, string Name, bool Complete, IReadOnlyList<string> Missing);

/// <summary>The result of <c>publish_checklist</c>: the steps of the checklist of the Mods window, as <c>oltool
/// publish --json</c> gives them.</summary>
/// <param name="Mod">The id of the active mod.</param>
/// <param name="Name">The display name of the mod.</param>
/// <param name="Version">The version of the mod.</param>
/// <param name="Complete">The number of complete steps.</param>
/// <param name="Total">The number of steps.</param>
/// <param name="Recommended">The recommended items. They do not count as steps.</param>
internal sealed record ChecklistView(string Mod, string Name, string Version, int Complete, int Total,
    IReadOnlyList<ChecklistStepRow> Steps, IReadOnlyList<string> Recommended)
{
    /// <summary>The problems of the reads of <c>publish.json</c> and of the publish history.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>A note about what the checklist reads, or null.</summary>
    public string? Note { get; init; }
}

/// <summary>A package of an export in the result of <c>export_mod</c>.</summary>
/// <param name="Target">The id of the target, for example <c>vanilla-1.1</c>.</param>
/// <param name="Form">The form of the package.</param>
/// <param name="File">The name of the file in the folder of the version.</param>
/// <param name="Written">False when a problem refused the package.</param>
internal sealed record PackageRow(string Target, string Form, string File, bool Written);

/// <summary>The checks of a target in the result of <c>export_mod</c>.</summary>
internal sealed record TargetCheckRow(string Target, int Errors, int Warnings);

/// <summary>The result of <c>export_mod</c>.</summary>
/// <param name="Version">The version of the mod that the export wrote.</param>
/// <param name="Folder">The folder of the packages, relative to the workspace of the profile.</param>
/// <param name="Succeeded">True when the export wrote each package without an error.</param>
/// <param name="Replaced">True when the export replaced the packages of an earlier export of the version.</param>
/// <param name="Problems">The first problems of the export, errors first.</param>
/// <param name="ProblemCount">The number of all problems of the export.</param>
internal sealed record ExportView(string Version, string Folder, bool Succeeded, bool Replaced,
    IReadOnlyList<PackageRow> Packages, IReadOnlyList<TargetCheckRow> Checks, IReadOnlyList<Problem> Problems,
    int ProblemCount)
{
    /// <summary>The messages of the record in the publish history, for example about an earlier export with the same
    /// packages. A new record has no message.</summary>
    public IReadOnlyList<string> History { get; init; } = [];

    /// <summary>True when the publish history recorded the export. An export with a failed package records the
    /// packages that it wrote too.</summary>
    public bool Recorded { get; init; }

    /// <summary>The problems of the reads and writes of <c>publish.json</c> and of the publish history.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// The tools <c>publish_checklist</c> and <c>export_mod</c> of section 7.6 of the MCP1 design. Both read the saved files
/// of the active mod, not the edit view. They use the builders of the Mods window, the Export dialog and oltool (review
/// finding R12 of MCP1-2). So the agent gets the same steps and the same packages.
/// </summary>
internal static class PublishTools
{
    /// <summary>
    /// The publish checklist of the active mod (<c>PublishChecklist.Of</c>). The session part loads the mod and the
    /// linked games. The tail reads the history and the fingerprints of the files of the mod on a worker, as the Export
    /// dialog does.
    /// </summary>
    public static ToolStep Checklist(ToolCall call)
    {
        var session = call.Session;
        var mod = SavedMod(session);
        var games = call.Host.Games(mod.Manifest);
        var history = ModExport.DefaultFolder(session.Profile.FilePath, mod.Id);
        var note = session.IsDirty ? AgentTexts.Error("checklist_unsaved") : null;
        return ToolStep.Then(cancel => Task.Run(() =>
        {
            ChecklistInput input;
            IReadOnlyList<string> problems;
            try
            {
                input = ChecklistInput.Read(mod, history, games.Reports, games.PlayTargets, out problems);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new EditException(new EditError(EditErrorKind.Refused, "ModNotRead",
                    AgentTexts.Error("mod_not_read", ("reason", e.Message))));
            }
            var checklist = PublishChecklist.Of(input);
            return ToolResult.Of(new ChecklistView(mod.Id, mod.Manifest.Name, mod.Manifest.Version, checklist.CompleteCount,
                checklist.Steps.Count,
                checklist.Steps.Select(s => new ChecklistStepRow(s.Number, s.Name, s.Complete, s.Missing)).ToList(),
                checklist.Recommended.Select(r => r.Text).ToList())
            {
                Warnings = problems,
                Note = note,
            });
        }, cancel));
    }

    /// <summary>The value names of the argument <c>formats</c> of <c>export_mod</c>.</summary>
    public static readonly IReadOnlyList<string> Formats = ["drop_in", "flmm"];

    /// <summary>The targets of the argument <c>targets</c> of <c>export_mod</c>.</summary>
    public static IReadOnlyList<string> Targets => [.. ExportTarget.All.Select(t => t.Id)];

    /// <summary>The steps of the progress of an export: the check of the mod, the write and the record.</summary>
    private const int ExportSteps = 3;

    /// <summary>
    /// The export of the saved files of the active mod (decisions D714 and D765), as the Export dialog does it. The
    /// session part checks the request and takes the place of the one export. The tail runs
    /// <see cref="GuardedExport.Prepare"/>, the same-version guard, the write and the record of the export on a worker.
    /// The packages go into the default export folder of the mod only.
    /// </summary>
    /// <remarks>A cancel of the client, the time limit of the tail or a stop of the server stop the export before its
    /// write. A write that started goes on to its end and records the export in the publish history (sections 5.5, item
    /// 6 and 8.5, item 6 of the MCP1 design).</remarks>
    public static ToolStep Export(ToolCall call)
    {
        var session = call.Session;
        var host = call.Host;
        if (session.IsDirty)
            throw Refused("UnsavedEdits", AgentTexts.Error("export_unsaved"));
        if (host.ExportBusy is { } busy)
            throw Refused("ExportRuns", busy);
        var mod = SavedMod(session);
        if (ModVersion.SafeProblem(mod.Manifest.Version) is { } unsafeVersion)
            throw Refused("UnsafeVersion", unsafeVersion);
        var games = host.Games(mod.Manifest);
        var targets = TargetsOf(call.Args, mod.Manifest, games.Reports);
        var forms = FormsOf(call.Args, targets);
        var root = ModExport.DefaultFolder(session.Profile.FilePath, mod.Id);
        var request = ExportRequest.Of(mod, targets, games.Reports, ModExport.VersionFolder(root, mod.Manifest), forms,
            DllStringResourceWriter.Instance, ThornEvaluator.Instance, out var publishProblems);
        var location = new HistoryLocation(root, mod.Id, mod.Folder, games.Roots);
        var replace = call.Args.Flag("replace_version");
        // The default export folder is <workspace>\exports\<mod id> (decisions D565 and D714).
        var workspace = Path.GetDirectoryName(Path.GetDirectoryName(root))!;
        var run = host.Exports.Begin() ?? throw Refused("ExportRuns", AgentTexts.Error("export_busy"));
        return ToolStep.Then(async (cancel, progress) =>
        {
            var live = new LiveProgress(progress);
            var work = run.Start(beforeWrite => Work(run, request, location, replace, beforeWrite, live));
            try
            {
                var outcome = await work.WaitAsync(cancel).ConfigureAwait(false);
                return outcome.Result(workspace, publishProblems);
            }
            catch (OperationCanceledException)
            {
                run.Stop();
                if (!run.Writes)
                    throw;
                return ToolResult.Failure(EditErrorKind.Limit, "TailTime", AgentTexts.Error("export_write_goes_on"));
            }
            finally
            {
                live.End();
            }
        });
    }

    /// <summary>The work of an export on a worker: the prepare, the guard, the write and the record. It touches no
    /// session.</summary>
    /// <param name="beforeWrite">Tells the export to stop before its write.</param>
    private static ExportOutcome Work(AgentExports.ExportRun run, ExportRequest request, HistoryLocation location,
        bool replace, CancellationToken beforeWrite, IProgress<ToolProgress> progress)
    {
        progress.Report(new ToolProgress(0, ExportSteps, AgentTexts.Error("export_progress_check")));
        var export = GuardedExport.Prepare(request, location, DateTime.UtcNow);
        if (export.Refusal is { } noHistory)
            return ExportOutcome.Error(EditErrorKind.Refused, "HistoryNotLoaded", noHistory);
        var prepared = export.Prepared!;
        if (prepared.Refused)
        {
            var errors = prepared.Problems.Where(p => p.Severity == DiagnosticSeverity.Error).Take(3).Select(p => p.Message);
            return ExportOutcome.Error(EditErrorKind.Refused, "ExportRefused",
                AgentTexts.Error("export_refused", ("errors", string.Join(" ", errors))));
        }
        var guard = export.Guard;
        if (guard.Kind == GuardKind.Published || guard.Kind == GuardKind.Exported && (!replace || !guard.CanReplace))
        {
            // The checklist still gets the checks of the prepare, as in the Export dialog.
            var warnings = export.RecordChecks();
            var (detail, message) = guard.Kind == GuardKind.Published
                ? ("Published", AgentTexts.Error("guard_published", ("version", guard.Version)))
                : replace
                    ? ("PartialReplace", AgentTexts.Error("guard_partial", ("version", guard.Version),
                        ("targets", string.Join(", ", guard.Missing))))
                    : ("Exported", AgentTexts.Error("guard_exported", ("version", guard.Version)));
            return ExportOutcome.Error(EditErrorKind.GuardConflict, detail, message, warnings);
        }
        if (beforeWrite.IsCancellationRequested || !run.Write())
            return ExportOutcome.Error(EditErrorKind.Refused, "Stopped", AgentTexts.Error("export_stopped"));
        progress.Report(new ToolProgress(1, ExportSteps, AgentTexts.Error("export_progress_write")));
        var result = export.Write();
        progress.Report(new ToolProgress(2, ExportSteps, AgentTexts.Error("export_progress_record")));
        var replaced = guard.Kind == GuardKind.Exported;
        var (record, recordWarnings) = export.RecordExport(result, replaced);
        progress.Report(new ToolProgress(ExportSteps, ExportSteps, null));
        return new ExportOutcome(null, result, replaced, record?.Messages(result.Version).ToList() ?? [], recordWarnings)
        {
            Recorded = record != null,
        };
    }

    /// <summary>The targets of an export: the argument, or each target of <c>mod.json</c> that has a base. The Export
    /// dialog checks the targets in the same way.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/>: a target that the mod or the linked games
    /// do not allow, or no target.</exception>
    private static IReadOnlyList<ExportTarget> TargetsOf(ToolArgs args, ModManifest manifest,
        IReadOnlyList<GameReport> reports)
    {
        string? Refusal(ExportTarget target) => target.Refusal(manifest) ?? target.BaseRefusal(target.BaseIn(reports));
        if (args.Texts("targets") is { } names)
        {
            var chosen = ExportTarget.All.Where(t => names.Contains(t.Id)).ToList();
            if (chosen.Count == 0)
                throw ToolInput.Bad("targets", AgentTexts.Error("export_no_target_given"));
            foreach (var target in chosen)
            {
                if (Refusal(target) is { } reason)
                {
                    throw new EditException(new EditError(EditErrorKind.Refused, "TargetRefused", reason)
                    {
                        Field = "targets",
                    });
                }
            }
            return chosen;
        }
        var named = ExportTarget.All.Where(t => manifest.Targets.Contains(t.Id)).ToList();
        var available = named.Where(t => Refusal(t) == null).ToList();
        if (available.Count > 0)
            return available;
        throw Refused("NoTarget", AgentTexts.Error("export_no_target",
            ("reasons", string.Join(" ", named.Select(Refusal).OfType<string>()))));
    }

    /// <summary>The package forms of the retail targets: the argument, or both forms.</summary>
    private static ExportForms FormsOf(ToolArgs args, IReadOnlyList<ExportTarget> targets)
    {
        if (args.Texts("formats") is not { } names)
            return ExportForms.All;
        var forms = (names.Contains("drop_in") ? ExportForms.DropIn : 0) | (names.Contains("flmm") ? ExportForms.Flmm : 0);
        if (forms == 0 && targets.Any(t => t.IsRetail))
            throw ToolInput.Bad("formats", AgentTexts.Error("export_no_format"));
        return forms;
    }

    private static EditException Refused(string detail, string message) =>
        new(new EditError(EditErrorKind.Refused, detail, message));

    /// <summary>What the work of an export gave: an error, or the written export with its record.</summary>
    private sealed record ExportOutcome(EditError? Failure, ExportResult? Written, bool Replaced,
        IReadOnlyList<string> History, IReadOnlyList<string> Warnings)
    {
        /// <summary>True when the publish history recorded the export.</summary>
        public bool Recorded { get; init; }

        public static ExportOutcome Error(EditErrorKind kind, string detail, string message,
            IReadOnlyList<string>? warnings = null) =>
            new(new EditError(kind, detail, message), null, false, [], warnings ?? []);

        /// <summary>The result of the call. The result gives at most <see cref="SaveTools.ShownProblems"/> problems, so
        /// the size limit of a result never hides an export that wrote its packages.</summary>
        public ToolResult Result(string workspace, IReadOnlyList<string> publishProblems)
        {
            if (Failure != null)
                return ToolResult.Failure(Failure);
            var done = Written!;
            var problems = done.Problems;
            return ToolResult.Of(new ExportView(done.Version,
                Path.GetRelativePath(workspace, done.OutputFolder).Replace('/', '\\'), done.Succeeded, Replaced,
                done.Packages.Select(p => new PackageRow(p.Target.Id, AgentTools.Snake(p.Form.ToString()), p.FileName,
                    p.Path != null)).ToList(),
                done.Checks.Select(c => new TargetCheckRow(c.Target.Id, c.Errors, c.Warnings)).ToList(),
                problems.OrderBy(p => p.Severity == DiagnosticSeverity.Error ? 0 : 1).Take(SaveTools.ShownProblems)
                    .Select(p => Problem.From(p, null)).ToList(),
                problems.Count)
            {
                History = History,
                Recorded = Recorded,
                Warnings = [.. publishProblems, .. Warnings],
            });
        }
    }

    /// <summary>The progress of a call until its tail ends. The work of an export can go on after the end of the call,
    /// and its later reports then go nowhere.</summary>
    private sealed class LiveProgress(IProgress<ToolProgress> progress) : IProgress<ToolProgress>
    {
        private volatile bool ended;

        public void Report(ToolProgress value)
        {
            if (!ended)
                progress.Report(value);
        }

        public void End() => ended = true;
    }

    /// <summary>The active mod as its saved files give it: the export and the checklist read the saved
    /// files.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/>: the session has no active mod, or its
    /// <c>mod.json</c> does not load.</exception>
    private static Mod SavedMod(EditSession session)
    {
        if (session.ActiveMod is not { } active)
            throw new EditException(new EditError(EditErrorKind.Refused, "NoActiveMod", AgentTexts.Error("no_active_mod")));
        try
        {
            return Mod.Load(active.Folder);
        }
        catch (ModLoadException e)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, "ModNotRead",
                AgentTexts.Error("mod_not_read", ("reason", e.Message))));
        }
    }
}
