using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Export;

/// <summary>The package forms of a retail target.</summary>
[Flags]
public enum ExportForms
{
    DropIn = 1,
    Flmm = 2,
    All = DropIn | Flmm,
}

/// <summary>What an export makes (milestone M11).</summary>
public sealed record ExportRequest
{
    public required Mod Mod { get; init; }

    /// <summary>The targets, in the order of <see cref="ExportTarget.All"/>.</summary>
    public required IReadOnlyList<ExportTarget> Targets { get; init; }

    /// <summary>The check of the linked game that is the base of a retail target, or null when no game gives the
    /// target.</summary>
    public required Func<ExportTarget, GameReport?> BaseOf { get; init; }

    /// <summary>The exact folder of the packages. The export makes it. The editor and the default of
    /// <c>oltool export</c> give <see cref="ModExport.VersionFolder"/>.</summary>
    public required string OutputFolder { get; init; }

    public ExportForms Forms { get; init; } = ExportForms.All;

    /// <summary>True to make the probe of the FLMM check (see <see cref="FlmmPackage.Write"/>).</summary>
    public bool FlmmProbe { get; init; }

    /// <summary>True to make the checklist only, and write no file.</summary>
    public bool ChecklistOnly { get; init; }

    /// <summary>The base of the checklist of the target OpenLancer, or null. A retail target uses its own base.</summary>
    public GameBase? OpenLancerBase { get; init; }

    /// <summary>The publish settings of the mod (milestone M11c). The texts of the packages use them.</summary>
    public PublishSettings Publish { get; init; } = PublishSettings.Empty;

    public required IStringResourceWriter StringWriter { get; init; }

    public required IThnEvaluator Thn { get; init; }

    /// <summary>
    /// The request of an export of the saved files of a mod, with the texts of its <c>publish.json</c> (milestone
    /// M11c). Each retail target takes its base from the linked games (see <see cref="ExportTarget.BaseIn"/>). The
    /// Export dialog, the checks and "Play this mod alone" of the Mods window, <c>oltool export</c> and the agent tool
    /// <c>export_mod</c> build their request here. So all of them export in the same way (review finding R12 of
    /// MCP1-2).
    /// </summary>
    /// <param name="targets">The targets, in the order of <see cref="ExportTarget.All"/>.</param>
    /// <param name="reports">The checks of the linked games.</param>
    /// <param name="folder">The exact folder of the packages, for example <see cref="ModExport.VersionFolder"/>.</param>
    /// <param name="publishProblems">The problems of the read of <c>publish.json</c>. The texts leave out a field with
    /// a problem, so the caller shows the problems.</param>
    public static ExportRequest Of(Mod mod, IReadOnlyList<ExportTarget> targets, IReadOnlyList<GameReport> reports,
        string folder, ExportForms forms, IStringResourceWriter strings, IThnEvaluator thn,
        out IReadOnlyList<string> publishProblems)
    {
        var publish = PublishSettings.Load(mod.Folder);
        publishProblems = publish.Problems;
        return new ExportRequest
        {
            Mod = mod,
            Targets = targets,
            BaseOf = target => target.BaseIn(reports),
            OutputFolder = folder,
            Forms = forms,
            Publish = publish.Settings,
            StringWriter = strings,
            Thn = thn,
        };
    }
}

/// <summary>One package of an export.</summary>
/// <param name="Path">The full path of the written file, or null when the export refused the package.</param>
public sealed record ExportedPackage(ExportTarget Target, string FileName, string? Path)
{
    public PackageForm Form { get; init; }

    /// <summary>The SHA-256 of the written file, or null.</summary>
    public string? Sha256 { get; init; }

    /// <summary>The guard digest of the game files of the package (decision D589), or null when the export refused
    /// the package.</summary>
    public string? GameFiles { get; init; }
}

/// <summary>The result of the checks of one target (decision D572).</summary>
/// <param name="Base">The base of the check: <c>&lt;root&gt;|&lt;build&gt;</c>, or empty for the target OpenLancer and
/// for a target without a base.</param>
public sealed record TargetCheck(ExportTarget Target, string Base, int Errors, int Warnings)
{
    /// <summary>The base key of a linked game: its root and its build.</summary>
    public static string BaseKey(GameReport? report) =>
        report?.Root is { } root ? $"{GameFolders.Normal(root)}|{report.Build}" : "";
}

/// <summary>The result of an export.</summary>
public sealed record ExportResult(
    string OutputFolder,
    IReadOnlyList<ExportedPackage> Packages,
    IReadOnlyList<Diagnostic> Problems,
    IReadOnlyList<string> Checklist,
    IReadOnlyDictionary<ExportTarget, TargetFiles> Builds)
{
    /// <summary>True when the export wrote each package that the request asks for.</summary>
    public bool Succeeded => Packages.Count > 0 && Packages.All(p => p.Path != null) &&
                             Problems.All(p => p.Severity != DiagnosticSeverity.Error);

    /// <summary>The version of the mod at the export.</summary>
    public string Version { get; init; } = "";

    /// <summary>The content fingerprint of the mod at the export (decision D583).</summary>
    public string Content { get; init; } = "";

    /// <summary>The page fingerprint of the mod at the export.</summary>
    public string Page { get; init; } = "";

    /// <summary>The result of the checks of each target.</summary>
    public IReadOnlyList<TargetCheck> Checks { get; init; } = [];
}

/// <summary>A package of a prepared export, before its compression.</summary>
public sealed record PreparedPackage(ExportTarget Target, PackageForm Form, string FileName, string? GameFiles)
{
    /// <summary>Makes the bytes, or null when a problem refuses the package.</summary>
    internal Func<byte[]>? Make { get; init; }

    public bool Refused => Make == null;

    /// <summary>The text of the README that the package holds, or null when a problem refuses the package. The Preview
    /// tab of the Mods window shows it.</summary>
    public string? Readme { get; init; }

    /// <summary>The header element of the script of an FLMM package, else null.</summary>
    public string? ScriptHeader { get; init; }
}

/// <summary>
/// An export after its checks and before its writes (section 13.1 of the M11c design): the problems, the checklist,
/// the guard digests and the plan of each package. A check of a mod is a <see cref="ModExport.Prepare"/> without a
/// <see cref="ModExport.Write"/>.
/// </summary>
public sealed class PreparedExport
{
    private readonly List<PreparedPackage> packages = [];
    private readonly List<Diagnostic> problems = [];
    private readonly List<(ExportTarget, IReadOnlyList<string>)> blocks = [];
    private readonly Dictionary<ExportTarget, TargetFiles> builds = [];
    private readonly List<TargetCheck> checks = [];
    private readonly List<PublishScreenshot> screenshots = [];

    internal PreparedExport(ExportRequest request, string outputFolder)
    {
        Request = request;
        OutputFolder = outputFolder;
        Texts = new PublishTexts(request.Mod.Manifest, request.Publish);
    }

    public ExportRequest Request { get; }

    public string OutputFolder { get; }

    public PublishTexts Texts { get; }

    public string Version => Request.Mod.Manifest.Version;

    /// <summary>True when the export can write nothing: the output folder is refused, the last save of the mod is not
    /// complete, or the version is not safe in a file name.</summary>
    public bool Refused { get; internal set; }

    public IReadOnlyList<PreparedPackage> Packages => packages;

    public IReadOnlyList<Diagnostic> Problems => problems;

    /// <summary>The change list of each target that passed its checks, in target order.</summary>
    public IReadOnlyList<(ExportTarget Target, IReadOnlyList<string> Lines)> ChecklistBlocks => blocks;

    /// <summary>The change lists of all targets, each after the line "Target: &lt;name&gt;".</summary>
    public IReadOnlyList<string> Checklist =>
    [
        .. ChecklistBlocks.SelectMany((b, i) =>
            (i > 0 ? [""] : Array.Empty<string>()).Append(PackageTexts.ChecklistTarget(b.Target)).Concat(b.Lines)),
    ];

    public IReadOnlyDictionary<ExportTarget, TargetFiles> Builds => builds;

    public IReadOnlyList<TargetCheck> Checks => checks;

    /// <summary>The content fingerprint of the mod (decision D583).</summary>
    public string Content { get; internal set; } = "";

    /// <summary>The page fingerprint of the mod.</summary>
    public string Page { get; internal set; } = "";

    /// <summary>The screenshots that pass the screenshot rule, in list order: the path relative to the mod folder and
    /// the caption. The list is empty when one screenshot fails (decision D586).</summary>
    public IReadOnlyList<PublishScreenshot> Screenshots => screenshots;

    /// <summary>The target, the form and the guard digest of each package that is not refused, for the guard.</summary>
    public IReadOnlyList<(string Target, string Form, string GameFiles)> Digests =>
    [
        .. Packages.Where(p => p.GameFiles != null).Select(p => (p.Target.Id, PackageForms.Of(p.Form), p.GameFiles!)),
    ];

    internal void Add(PreparedPackage package) => packages.Add(package);

    internal void Add(TargetCheck check) => checks.Add(check);

    internal void Add(Diagnostic problem) => problems.Add(problem);

    internal void AddRange(IEnumerable<Diagnostic> more) => problems.AddRange(more);

    internal void AddBlock(ExportTarget target, IReadOnlyList<string> lines) => blocks.Add((target, lines));

    internal void SetBuild(ExportTarget target, TargetFiles files) => builds[target] = files;

    /// <summary>Keeps the screenshots that passed the screenshot rule, or none when one failed.</summary>
    internal void SetScreenshots(IEnumerable<PublishScreenshot> accepted)
    {
        screenshots.Clear();
        screenshots.AddRange(accepted);
    }
}

/// <summary>
/// The export of one mod (milestones M11 and M11c): for each retail target, the merge on the files of a linked game,
/// the format checks, the drop-in zip and the FLMM package. For the target OpenLancer, a zip of the mod folder. Then the
/// text of the Nexus page, the checklist for testers and the screenshots.
/// </summary>
/// <remarks>
/// <para>The export only reads the game folders and the mod folder. It writes only into the output folder, which must
/// not overlap a game folder or the mod folder.</para>
/// <para>The export runs in two steps. <see cref="Prepare"/> runs each step that can give a problem and computes the
/// guard digests, without compression. <see cref="Write"/> makes and writes one package at a time. The guard of the
/// publish history runs between them. <see cref="Write"/> does not write the history: its caller does.</para>
/// </remarks>
public static class ModExport
{
    public const string NexusPageFile = "nexus-page.txt";
    public const string ChecklistFile = "CHECKLIST.txt";

    /// <summary>The folder of the screenshots in the output folder, for the Nexus upload.</summary>
    public const string ScreenshotsFolder = "screenshots";

    /// <summary>The text after the errors of a target.</summary>
    public const string Stopped = "The export stopped. Fix the errors, then export again.";

    /// <summary>The default output root of a mod: <c>exports\&lt;mod id&gt;</c> next to the folder of the profile
    /// folder. The publish history of the mod is in this folder too.</summary>
    public static string DefaultFolder(string profilePath, string modId) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath))!, "..", "exports", modId));

    /// <summary>The folder of a version in an output root: <c>&lt;root&gt;\&lt;version&gt;</c> (decision D566).</summary>
    /// <exception cref="ArgumentException">The version is not safe in a path (decision D585).</exception>
    public static string VersionFolder(string root, ModManifest mod) =>
        ModVersion.SafeProblem(mod.Version) is { } problem
            ? throw new ArgumentException(problem, nameof(mod))
            : Path.Combine(Path.GetFullPath(root), mod.Version);

    /// <summary>The prefix of the file name of the text DLL of an exported mod.</summary>
    public static string DllName(string modId) => $"ol_{modId}.dll";

    /// <summary>Prepares and writes an export without earlier packages of the version, as M11 did.</summary>
    public static ExportResult Run(ExportRequest request) => Write(Prepare(request), []);

    /// <summary>
    /// Runs the merge, the format checks, the content checks, the FLMM script, the game file rule and the screenshot
    /// rule of each target, and computes the guard digests. It writes no file and compresses nothing.
    /// </summary>
    public static PreparedExport Prepare(ExportRequest request)
    {
        var mod = request.Mod;
        var prepared = new PreparedExport(request, Path.GetFullPath(request.OutputFolder));
        var output = prepared.OutputFolder;
        var fingerprint = new ModFingerprint();
        prepared.Content = fingerprint.Content(mod.Manifest, mod.Folder);
        prepared.Page = fingerprint.Page(mod.Manifest, mod.Folder, request.Publish);

        if (OutputRefusal(output, request) is { } refusal)
        {
            prepared.Add(Diagnostic.Error(output, refusal));
            prepared.Refused = true;
            return prepared;
        }
        // A save that is not complete leaves a mix of old and new files (finding R31). The check and the export both
        // stop, so the text names neither.
        if (mod.HasPendingSave)
        {
            prepared.Add(Diagnostic.Error(mod.Folder, PublishMessages.PendingSave));
            prepared.Refused = true;
            return prepared;
        }
        if (ModVersion.SafeProblem(mod.Manifest.Version) is { } unsafeVersion)
        {
            prepared.Add(Diagnostic.Error(Path.Combine(mod.Folder, ModManifest.FileName), unsafeVersion));
            prepared.Refused = true;
            return prepared;
        }
        if (!request.ChecklistOnly && InGitWorkTree(output))
            prepared.Add(Diagnostic.Warning(output, "The export folder is in a git repository. The packages hold merged " +
                                                    "game files. Do not commit them."));
        // A hand-written publish.json can break the rules of the publish page. The texts then leave out a page link
        // that does not start with https://. The screenshot rule below gives its own errors.
        var publishFile = Path.Combine(mod.Folder, PublishSettings.FileName);
        prepared.AddRange(request.Publish.Check().Where(p => p.Field != PublishField.Screenshots)
            .Select(p => Diagnostic.Warning(publishFile, p.Message)));
        var screenshotErrors = Screenshots(prepared);
        prepared.AddRange(screenshotErrors);

        foreach (var target in request.Targets)
        {
            if (target.Refusal(mod.Manifest) is { } reason)
            {
                prepared.Add(Diagnostic.Error(mod.Folder, reason));
                prepared.Add(new PreparedPackage(target, target.IsRetail ? PackageForm.DropIn : PackageForm.ModFolder,
                    target.IsRetail ? DropInPackage.FileName(mod.Manifest, target) : ModZip.FileName(mod.Manifest), null));
                prepared.Add(new TargetCheck(target, "", 1, 0));
                continue;
            }
            if (!target.IsRetail)
                PrepareOpenLancer(prepared, target, screenshotErrors);
            else
                PrepareRetail(prepared, target);
        }
        return prepared;
    }

    /// <summary>
    /// Makes and writes each package of a prepared export, one at a time. Then it copies the screenshots and writes
    /// <c>nexus-page.txt</c> and <c>CHECKLIST.txt</c>.
    /// </summary>
    /// <param name="earlier">The valid packages of earlier exports of the same version into the same folder (see
    /// <see cref="PublishHistory.ValidPackages"/>). The Nexus page and the checklist list them too.</param>
    public static ExportResult Write(PreparedExport prepared, IReadOnlyList<ExportedPackage> earlier)
    {
        var output = prepared.OutputFolder;
        var packages = new List<ExportedPackage>();
        if (!prepared.Refused && !prepared.Request.ChecklistOnly)
        {
            foreach (var package in prepared.Packages)
                packages.Add(Save(output, package));
        }
        var written = packages.Where(p => p.Path != null).ToList();
        // The probe of the FLMM check is not for players. It writes only its package, so the texts and the images of
        // the folder stay for the real packages.
        if (written.Count > 0 && !prepared.Request.FlmmProbe)
        {
            var images = CopyScreenshots(output, prepared);
            var listed = earlier.Where(e => written.All(w => !w.FileName.Equals(e.FileName, StringComparison.OrdinalIgnoreCase)))
                .Concat(written)
                .OrderBy(p => ExportTarget.All.ToList().IndexOf(p.Target)).ThenBy(p => p.Form)
                .ToList();
            AtomicFile.WriteAllBytes(Path.Combine(output, NexusPageFile),
                Encoding.UTF8.GetBytes(PackageTexts.NexusPage(prepared.Texts, listed, images)));
            AtomicFile.WriteAllBytes(Path.Combine(output, ChecklistFile),
                Encoding.UTF8.GetBytes(PackageTexts.ChecklistFile(prepared.Texts, Blocks(output, prepared, listed))));
        }
        return new ExportResult(output, packages, prepared.Problems, prepared.Checklist, prepared.Builds)
        {
            Version = prepared.Version,
            Content = prepared.Content,
            Page = prepared.Page,
            Checks = prepared.Checks,
        };
    }

    /// <summary>The reason why the export cannot write into a folder, or null: the folder overlaps the mod folder or a
    /// game folder, or it is in a game folder.</summary>
    public static string? FolderRefusal(string folder, string modFolder, IEnumerable<string> gameRoots) =>
        FolderOverlap(folder, modFolder, gameRoots, "The export folder", " Pick another folder.");

    /// <summary>The reason why OpenLancer cannot write the publish history into a folder, or null. The rules are the
    /// rules of the export folder.</summary>
    public static string? HistoryFolderRefusal(string folder, string modFolder, IEnumerable<string> gameRoots) =>
        FolderOverlap(folder, modFolder, gameRoots, "The publish history folder", "");

    /// <param name="subject">The name of the folder at the start of the text, for example "The export folder".</param>
    /// <param name="advice">The text after the reason, with its leading space, or empty.</param>
    private static string? FolderOverlap(string folder, string modFolder, IEnumerable<string> gameRoots, string subject,
        string advice)
    {
        folder = Path.GetFullPath(folder);
        if (PathRules.Overlaps(folder, Path.GetFullPath(modFolder)))
            return $"{subject} {folder} overlaps the mod folder {modFolder}.{advice}";
        foreach (var root in gameRoots)
        {
            if (PathRules.Overlaps(folder, root))
                return $"{subject} {folder} overlaps the game folder {root}. OpenLancer does not write into the game " +
                       $"folder.{advice}";
        }
        // Also a game that is not a base of the export, for example the profile base or another install.
        if (GameRoot.Containing(folder) is { } game)
            return $"{subject} {folder} is in the game folder {game}. OpenLancer does not write into a game folder.{advice}";
        return null;
    }

    /// <summary>The roots of the linked games that are the bases of the retail targets of a request.</summary>
    private static IReadOnlyList<string> GameRoots(ExportRequest request) =>
    [
        .. request.Targets.Where(t => t.IsRetail).Select(t => request.BaseOf(t)?.Root).OfType<string>(),
    ];

    private static string? OutputRefusal(string output, ExportRequest request) =>
        FolderRefusal(output, request.Mod.Folder, GameRoots(request));

    /// <summary>Checks each listed screenshot (decision D586). A failure is an error, and then no screenshot goes into
    /// the export.</summary>
    private static List<Diagnostic> Screenshots(PreparedExport prepared)
    {
        var request = prepared.Request;
        var errors = new List<Diagnostic>();
        var accepted = new List<PublishScreenshot>();
        var source = Path.Combine(request.Mod.Folder, PublishSettings.FileName);
        foreach (var screenshot in request.Publish.Screenshots)
        {
            if (ScreenshotRules.Check(request.Mod.Folder, screenshot.File, GameRoots(request)) is { } reason)
                errors.Add(Diagnostic.Error(source, reason));
            else
                accepted.Add(screenshot);
        }
        prepared.SetScreenshots(errors.Count > 0 ? [] : accepted);
        return errors;
    }

    /// <summary>The checks and the zip of the target OpenLancer: the rules of the zip and the screenshot rule.</summary>
    private static void PrepareOpenLancer(PreparedExport prepared, ExportTarget target, IReadOnlyList<Diagnostic> screenshotErrors)
    {
        var request = prepared.Request;
        var mod = request.Mod;
        if (request.OpenLancerBase is { } openLancerBase)
        {
            var build = TargetBuild.Run(openLancerBase, [mod], ModOverlay.StringsDllName, request.StringWriter, request.Thn);
            prepared.SetBuild(target, build);
            prepared.AddBlock(target, PackageTexts.Checklist(build));
        }
        if (request.ChecklistOnly)
            return;
        var plan = ModZip.Plan(mod, prepared.Texts, [.. prepared.Screenshots.Select(s => s.File)], prepared.Content, null);
        prepared.AddRange(plan.Problems);
        var targetProblems = plan.Problems.Concat(screenshotErrors).ToList();
        prepared.Add(Check(target, "", targetProblems));
        var refused = screenshotErrors.Count > 0 || plan.Make == null;
        prepared.Add(new PreparedPackage(target, PackageForm.ModFolder, plan.FileName, refused ? null : plan.GameFiles)
        {
            Make = refused ? null : plan.Make,
            Readme = refused ? null : plan.Readme,
        });
    }

    /// <summary>The merge, the checks and the package plans of a retail target.</summary>
    private static void PrepareRetail(PreparedExport prepared, ExportTarget target)
    {
        var request = prepared.Request;
        var mod = request.Mod;
        var report = request.BaseOf(target);
        if (target.BaseRefusal(report) is { } noBase)
        {
            prepared.Add(Diagnostic.Error(mod.Folder, noBase));
            AddRefused(prepared, target);
            prepared.Add(new TargetCheck(target, "", 1, 0));
            return;
        }
        var baseKey = TargetCheck.BaseKey(report);
        var targetProblems = new List<Diagnostic>();
        targetProblems.AddRange(target.BaseWarnings(report!).Select(w => Diagnostic.Warning(report!.Root!, w)));
        targetProblems.AddRange(PackageRules.RefusedFiles(mod));
        var gameBase = GameBase.Standard(report!.Root!);
        var files = TargetBuild.Run(gameBase, [mod], DllName(mod.Id), request.StringWriter, request.Thn);
        prepared.SetBuild(target, files);
        targetProblems.AddRange(files.Diagnostics);
        targetProblems.AddRange(FormatChecks.Run(files));
        targetProblems.AddRange(TargetChecks.Run(files));
        if (RetailReference(request, target) is { } retail)
            targetProblems.AddRange(TargetChecks.HdEditionLines(files, retail));
        if (files.File(ModOverlay.FreelancerIniPath) != null && FreelancerIniDiffers(gameBase, report))
            targetProblems.Add(Diagnostic.Warning(ModOverlay.FreelancerIniPath,
                $"Your freelancer.ini differs from the file list of {GameDetector.BuildName(report.Build!)}. The " +
                "drop-in zip carries these differences to each player."));
        prepared.AddRange(targetProblems);
        if (targetProblems.Any(p => p.Severity == DiagnosticSeverity.Error))
        {
            prepared.Add(Diagnostic.Error(mod.Folder, Stopped));
            AddRefused(prepared, target);
            prepared.Add(Check(target, baseKey, targetProblems));
            return;
        }
        prepared.AddBlock(target, PackageTexts.Checklist(files));
        if (request.ChecklistOnly)
        {
            prepared.Add(Check(target, baseKey, targetProblems));
            return;
        }

        if (request.Forms.HasFlag(ExportForms.DropIn))
        {
            var dropIn = DropInPackage.Plan(files, prepared.Texts, target, null);
            prepared.AddRange(dropIn.Problems);
            targetProblems.AddRange(dropIn.Problems);
            prepared.Add(new PreparedPackage(target, PackageForm.DropIn, dropIn.FileName, dropIn.GameFiles)
            {
                Make = dropIn.Make,
                Readme = dropIn.Readme,
            });
        }
        if (request.Forms.HasFlag(ExportForms.Flmm))
        {
            var placeholders = TargetBuild.Run(gameBase, [mod], DllName(mod.Id), request.StringWriter, request.Thn,
                keepPlaceholders: true);
            var flmm = FlmmPackage.Plan(placeholders, mod, prepared.Texts, target, request.FlmmProbe, null);
            prepared.AddRange(flmm.Problems);
            targetProblems.AddRange(flmm.Problems);
            prepared.Add(new PreparedPackage(target, PackageForm.Flmm, flmm.FileName, flmm.GameFiles)
            {
                Make = flmm.Make,
                Readme = flmm.Readme,
                ScriptHeader = flmm.ScriptHeader,
            });
        }
        prepared.Add(Check(target, baseKey, targetProblems));
    }

    private static TargetCheck Check(ExportTarget target, string baseKey, IReadOnlyCollection<Diagnostic> problems) =>
        new(target, baseKey, problems.Count(p => p.Severity == DiagnosticSeverity.Error),
            problems.Count(p => p.Severity == DiagnosticSeverity.Warning));

    private static void AddRefused(PreparedExport prepared, ExportTarget target)
    {
        var request = prepared.Request;
        if (request.ChecklistOnly)
            return;
        if (request.Forms.HasFlag(ExportForms.DropIn))
            prepared.Add(new PreparedPackage(target, PackageForm.DropIn,
                DropInPackage.FileName(request.Mod.Manifest, target), null));
        if (request.Forms.HasFlag(ExportForms.Flmm))
            prepared.Add(new PreparedPackage(target, PackageForm.Flmm,
                FlmmPackage.FileName(request.Mod.Manifest, target, request.FlmmProbe), null));
    }

    private static ExportedPackage Save(string output, PreparedPackage package)
    {
        if (package.Make == null)
            return new ExportedPackage(package.Target, package.FileName, null) { Form = package.Form };
        var bytes = package.Make();
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, package.FileName);
        AtomicFile.WriteAllBytes(path, bytes);
        return new ExportedPackage(package.Target, package.FileName, path)
        {
            Form = package.Form,
            Sha256 = ContentHash.Of(bytes),
            GameFiles = package.GameFiles,
        };
    }

    /// <summary>The name of a screenshot in <c>screenshots</c> of the output folder: <c>01-&lt;name&gt;.&lt;ext&gt;</c>
    /// in list order (decision D567).</summary>
    /// <param name="index">The place of the image in the list of the images that passed the screenshot rule, from
    /// 0.</param>
    /// <param name="file">The path of the image in <c>publish.json</c>.</param>
    public static string ScreenshotName(int index, string file) => $"{index + 1:00}-{Path.GetFileName(file)}";

    /// <summary>
    /// Copies the screenshots that passed the screenshot rule into <c>screenshots</c> of the output folder, with the
    /// names <c>01-&lt;name&gt;.&lt;ext&gt;</c> in list order (decision D567). The export deletes no file there. The
    /// user can pick any output folder, and the files in it can belong to the user.
    /// </summary>
    /// <returns>The file names and the captions, in list order. <c>nexus-page.txt</c> lists only these files.</returns>
    private static List<(string File, string Caption)> CopyScreenshots(string output, PreparedExport prepared)
    {
        var folder = Path.Combine(output, ScreenshotsFolder);
        var images = new List<(string, string)>();
        for (var i = 0; i < prepared.Screenshots.Count; i++)
        {
            var screenshot = prepared.Screenshots[i];
            var name = ScreenshotName(i, screenshot.File);
            Directory.CreateDirectory(folder);
            var source = Path.Combine(prepared.Request.Mod.Folder, screenshot.File.Replace('/', Path.DirectorySeparatorChar));
            AtomicFile.WriteAllBytes(Path.Combine(folder, name), File.ReadAllBytes(source));
            images.Add((name, screenshot.Caption));
        }
        return images;
    }

    /// <summary>
    /// The change list of each target of the listed packages: the list of this export, or else the block of the target
    /// in the <c>CHECKLIST.txt</c> of an earlier export in the folder.
    /// </summary>
    private static IEnumerable<(ExportTarget, IReadOnlyList<string>)> Blocks(string output, PreparedExport prepared,
        IReadOnlyList<ExportedPackage> listed)
    {
        var old = OldBlocks(Path.Combine(output, ChecklistFile));
        foreach (var target in ExportTarget.All.Where(t => listed.Any(p => p.Target == t)))
        {
            var block = prepared.ChecklistBlocks.FirstOrDefault(b => b.Target == target).Lines;
            if (block != null)
                yield return (target, block);
            else if (old.TryGetValue(PackageTexts.ChecklistTarget(target), out var earlier))
                yield return (target, earlier);
        }
    }

    /// <summary>The blocks of an existing <c>CHECKLIST.txt</c>, by their line "Target: &lt;name&gt;".</summary>
    private static Dictionary<string, IReadOnlyList<string>> OldBlocks(string path)
    {
        var blocks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return blocks;
        string? name = null;
        var lines = new List<string>();
        foreach (var line in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').Append("Target: "))
        {
            if (!line.StartsWith("Target: ", StringComparison.Ordinal))
            {
                lines.Add(line);
                continue;
            }
            if (name != null)
            {
                while (lines.Count > 0 && lines[^1].Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                blocks[name] = lines;
            }
            name = line;
            lines = [];
        }
        return blocks;
    }

    /// <summary>The files of a linked Freelancer 1.0 or 1.1 game for the check of the lines that HD Edition changed, or
    /// null. Only the target HD Edition 0.7.1 uses it.</summary>
    private static GameBase? RetailReference(ExportRequest request, ExportTarget target) =>
        target == ExportTarget.HdEdition && request.BaseOf(ExportTarget.Vanilla) is
            { CanBeBase: true, Target: ModTargets.Vanilla } report
            ? report.Base
            : null;

    /// <summary>True when the <c>freelancer.ini</c> of a base differs from the row of the file list of its build.</summary>
    private static bool FreelancerIniDiffers(GameBase gameBase, GameReport report)
    {
        if (report.Build is not { } build || KnownBuilds.Embedded.Reference(build) is not { } known ||
            !known.Rows.TryGetValue(DataDigest.ManifestPath(ModOverlay.FreelancerIniPath), out var row))
            return false;
        var path = Path.Combine(gameBase.Root, PathRules.WithDiskCase(gameBase.Root, ModOverlay.FreelancerIniPath));
        return !File.Exists(path) || new FileInfo(path).Length != row.Size || DataDigest.HashFile(path) != row.Sha256;
    }

    /// <summary>True when the folder is inside a git work tree: a folder above it has a <c>.git</c> folder or
    /// file.</summary>
    private static bool InGitWorkTree(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return true;
        }
        return false;
    }
}
