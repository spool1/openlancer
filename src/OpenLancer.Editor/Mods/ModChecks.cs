using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Mods;

/// <summary>A text of the Preview tab: its name in the Show dropdown and the text as the export writes it.</summary>
internal sealed record PreviewText(string Name, string Text);

/// <summary>What the check of a mod needs.</summary>
/// <param name="Workspace">The workspace of the Mods window, which holds the publish history.</param>
/// <param name="Reports">The checks of the linked games. A retail target uses the game of its kind.</param>
/// <param name="GameRoots">The game folders that the history folder must not overlap.</param>
internal sealed record ModCheckRequest(string Folder, string Workspace, IReadOnlyList<GameReport> Reports,
    IReadOnlyCollection<string> GameRoots);

/// <summary>The result of the check of a mod (decision D572 of the M11c design).</summary>
/// <param name="Content">The content fingerprint of the checked files, or empty when the mod did not load.</param>
/// <param name="Page">The page fingerprint of the checked files, or empty.</param>
/// <param name="Targets">The result of each target. The history gets them as the last checks.</param>
/// <param name="Problems">The problems of the check, for "Show in Console".</param>
/// <param name="Previews">The README of each package, the FLMM script header of each target and
/// <c>CHECKLIST.txt</c>.</param>
/// <param name="Warnings">The warnings of the write of the history.</param>
internal sealed record ModCheckResult(string Folder, DateTime Time, string Content, string Page,
    IReadOnlyList<TargetCheck> Targets, IReadOnlyList<Diagnostic> Problems, IReadOnlyList<PreviewText> Previews,
    IReadOnlyList<string> Warnings)
{
    public int Errors => Problems.Count(p => p.Severity == DiagnosticSeverity.Error);

    public int WarningCount => Problems.Count(p => p.Severity == DiagnosticSeverity.Warning);
}

/// <summary>
/// The checks of the Mods window (decision D572 of the M11c design). A check is <see cref="ModExport.Prepare"/> for each
/// target of the mod, without a write and without compression. It records the result of each target in the publish
/// history. The checks run on a worker thread, one at a time. A cancel drops the result of a running check and the
/// checks that wait.
/// </summary>
/// <remarks>Each member except <see cref="Run"/> runs on the UI thread. Call <see cref="Update"/> once each
/// frame.</remarks>
internal sealed class ModChecks(Func<ModCheckRequest, ModCheckResult> run)
{
    private readonly List<ModCheckRequest> waiting = [];
    private readonly Dictionary<string, ModCheckResult> results = new(StringComparer.OrdinalIgnoreCase);
    private (ModCheckRequest Request, Task<ModCheckResult> Task)? running;
    private bool cancelled;

    /// <summary>Runs on the UI thread when a check gives its result.</summary>
    public event Action<ModCheckResult>? Checked;

    /// <summary>The folder of the running check, or null.</summary>
    public string? Running => cancelled ? null : running?.Request.Folder;

    /// <summary>The number of checks that wait.</summary>
    public int Waiting => waiting.Count;

    /// <summary>True when a check of the mod runs or waits.</summary>
    public bool IsChecking(string folder) =>
        Running is { } running && GameFolders.Same(running, folder) || waiting.Any(r => GameFolders.Same(r.Folder, folder));

    /// <summary>True while a check of the mod waits or runs on a worker thread, also a check whose result a cancel
    /// dropped. A check that runs writes the publish history of the mod, so a rename of the mod waits for it.</summary>
    public bool IsBusyWith(string folder) =>
        running is { } current && GameFolders.Same(current.Request.Folder, folder) ||
        waiting.Any(r => GameFolders.Same(r.Folder, folder));

    /// <summary>The last result of a mod in this run of the editor, or null.</summary>
    public ModCheckResult? ResultOf(string folder) => results.GetValueOrDefault(Path.GetFullPath(folder));

    /// <summary>Adds a check. A mod that a check already runs or waits for gets no second check.</summary>
    public void Request(ModCheckRequest request)
    {
        if (!IsChecking(request.Folder))
        {
            waiting.Add(request with { Folder = Path.GetFullPath(request.Folder) });
        }
    }

    /// <summary>Drops the checks that wait, and the result of the running check.</summary>
    public void CancelAll()
    {
        waiting.Clear();
        cancelled = running != null;
    }

    /// <summary>Takes the result of the running check and starts the next check.</summary>
    public void Update()
    {
        if (running is { Task.IsCompleted: true } done)
        {
            running = null;
            var dropped = cancelled;
            cancelled = false;
            if (!dropped)
            {
                var result = done.Task.IsCompletedSuccessfully
                    ? done.Task.Result
                    : Failed(done.Request, done.Task.Exception?.GetBaseException().Message ?? "The check stopped.");
                results[result.Folder] = result;
                Checked?.Invoke(result);
            }
        }

        if (running == null && waiting.Count > 0)
        {
            var next = waiting[0];
            waiting.RemoveAt(0);
            running = (next, Task.Run(() => run(next)));
        }
    }

    /// <summary>The check of a mod: the prepare of an export of each target, the texts of the Preview tab, and the
    /// record of the result of each target in the publish history. It runs on a worker thread.</summary>
    public static ModCheckResult Run(ModCheckRequest request) =>
        Run(request, DllStringResourceWriter.Instance, ThornEvaluator.Instance);

    /// <param name="strings">The writer of the text DLL. The tests give a fake.</param>
    internal static ModCheckResult Run(ModCheckRequest request, IStringResourceWriter strings, IThnEvaluator thn)
    {
        var time = DateTime.UtcNow;
        Mod mod;
        try
        {
            mod = Mod.Load(request.Folder);
        }
        catch (ModLoadException e)
        {
            return Failed(request, e.Message);
        }

        // The export refuses a save that is not complete too, but the check gives the text of a check.
        if (mod.HasPendingSave)
        {
            return Failed(request, PublishMessages.CheckPendingSave);
        }

        var targets = ExportTarget.All.Where(t => mod.Manifest.Targets.Contains(t.Id)).ToList();
        var exportRequest = ExportRequest.Of(mod, targets, request.Reports,
            PublishHistory.FolderOf(request.Workspace, mod.Id), ExportForms.All, strings, thn, out var publishProblems);
        var prepared = ModExport.Prepare(exportRequest);
        var problems = publishProblems.Select(p => Diagnostic.Warning(Path.Combine(mod.Folder, PublishSettings.FileName), p))
            .Concat(prepared.Problems).ToList();
        IReadOnlyList<string> warnings = [];
        if (!prepared.Refused)
        {
            var location = new HistoryLocation(PublishHistory.FolderOf(request.Workspace, mod.Id), mod.Id, mod.Folder,
                request.GameRoots);
            warnings = PublishHistory.Update(location, "check", h => h.SetChecks(prepared.Checks, prepared.Content, time))
                .Messages;
        }

        return new ModCheckResult(mod.Folder, time, prepared.Content, prepared.Page, prepared.Checks, problems,
            Previews(prepared), warnings);
    }

    /// <summary>The texts of the Preview tab from a prepared export: the README of each package that the export can
    /// write, the FLMM script header of each target, and <c>CHECKLIST.txt</c>.</summary>
    internal static IReadOnlyList<PreviewText> Previews(PreparedExport prepared)
    {
        var previews = new List<PreviewText>();
        foreach (var package in prepared.Packages.Where(p => p.Readme != null))
        {
            previews.Add(new PreviewText($"README: {PackageForms.Name(PackageForms.Of(package.Form))}, {package.Target.Name}",
                package.Readme!));
        }

        foreach (var package in prepared.Packages.Where(p => p.ScriptHeader != null))
        {
            previews.Add(new PreviewText($"FLMM script header: {package.Target.Name}", package.ScriptHeader!));
        }

        if (prepared.ChecklistBlocks.Count > 0)
        {
            previews.Add(new PreviewText(ModExport.ChecklistFile,
                PackageTexts.ChecklistFile(prepared.Texts, prepared.ChecklistBlocks)));
        }

        return previews;
    }

    private static ModCheckResult Failed(ModCheckRequest request, string message) =>
        new(Path.GetFullPath(request.Folder), DateTime.UtcNow, "", "", [], [Diagnostic.Error(request.Folder, message)], [], []);
}
