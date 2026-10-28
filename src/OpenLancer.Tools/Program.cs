using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LibreLancer;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Dump;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;

namespace OpenLancer.Tools;

/// <summary>
/// oltool: command-line tasks for OpenLancer profiles and game data.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitProblems = 1;
    private const int ExitFailure = 2;

    private static readonly string Usage = """
        Usage: oltool <command> [arguments]

        Commands:
          verify-base <folder>              Print the data digest. Exit 1 if it is not retail v1.0 or a file is writable.
          game <folder> [--json] [--hashes] [--write-manifest <file>]
                                            Tell which game and version a folder holds. Exit 1 if it holds no game.
                                            --json: write the report as JSON, and nothing else.
                                            --hashes: add the manifest lines of the code files and the marker DLLs.
                                            The lines hold paths, sizes and hashes only, no game content.
                                            --write-manifest: write the manifest line of each file of the game folder
                                            to the file. The file must be outside each game folder. The list leaves out
                                            unins* in the root, the SAVE folder and *.log files.
          build <profile.json>              Merge the profile's mods and print the problems and the changed files.
          validate <profile.json> [--active <mod id>] [--json]
                                            Build, then report the data problems that the mods add to vanilla.
                                            --active: report only the problems that this mod adds to the mods that
                                            load before it. The later mods are not used.
                                            --json: write the problems to stdout as a JSON list of
                                            {"severity", "source", "message"}, and nothing else. A problem
                                            of a cutscene also has "cutscene": the path, the element and
                                            the value.
          validate <profile.json> --target <hde-0.7.1|vanilla-1.1> [--game <folder>] [--json]
                                            Merge all mods of the profile on the files of the target game, and report
                                            the problems of the format checks and the content checks of the retail
                                            game. --game: the game folder of the target. Without it, the base of the
                                            profile must give the target. The game folder does not change.
          deploy <profile.json>             Build and validate, then write the merged data next to the profile.
          export <profile.json> --mod <id> --target <hde-0.7.1|vanilla-1.1|openlancer> [--target ...]
                 [--game <folder>]... [--format dropin|flmm|all] [--out <folder>] [--checklist] [--flmm-probe]
                 [--same-version]
                                            Merge one mod on the files of each target game, check the result and write
                                            the packages of the targets. Repeat --target for more targets.
                                            --game: the game folder of a retail target. Repeat it for each retail
                                            target. The export uses each folder for the target of its game. Without
                                            it, the base of the profile must give the target. --format: the package
                                            forms of a retail target, all by default. --out: the exact output folder,
                                            <profile folder>\..\exports\<mod id>\<version> by default.
                                            --checklist: print the checklist for testers, and write nothing.
                                            --flmm-probe: write only the probe of the FLMM check, which is not for
                                            players. The export records the packages in the publish history of
                                            <profile folder>\..\exports\<mod id>. An export of a version that the
                                            history has with other game files exits 1. --same-version: replace that
                                            version, when it does not have the mark "published". The replace needs
                                            each target and form of the earlier exports. A history that does not load
                                            stops the export with exit 2.
          publish <profile.json> --mod <id> [--json]
                                            Print the publish checklist of a mod: each step with "done" or what is
                                            missing, and the recommended items. It reads the publish history and the
                                            last checks, and runs no check. Exit 1 when a step is missing.
                                            --json: write one JSON object, and nothing else.
          install <profile.json> --game <folder> [--into <play copy>] [--full-copy]
                                            Merge all mods of the profile on the files of the game, check the result,
                                            make or update the play copy of the game, and install the files into it.
                                            The game folder does not change. It starts no program. --into: the play
                                            copy folder, "<game folder> - OpenLancer play" next to the game by default.
                                            It must not overlap the profile folder, a mod folder or the OpenLancer repo.
                                            --full-copy: real copies of all files, also for FLMM and drop-in tests.
          uninstall <play copy>             Give each installed file of the play copy its kind of the make again.
          playcopy check <play copy>        Check the links and the installed files of a play copy, and change
                                            nothing. Exit 1 on a difference.
          playcopy remove <play copy>       Delete a play copy. The game folder does not change.
          engine-lines <folder> [--json]    Load the game folder without mods as the editor does: the game data, each
                                            star system and each story mission. Print the number of engine warning and
                                            error lines of each kind of the Console. --json: write one JSON object with
                                            the count of each line key, and nothing else. The editor compares the lines
                                            of a profile with these counts. The folder does not change.
          ini <profile.json|folder> <path>  Print a merged INI file (binary or text) as text.
          dump <profile.json|folder> <out>  Write a canonical JSON dump of the resolved game data.
          thn <profile.json|folder> <path>  Print a cutscene script (.thn) as canonical Lua text. The script comes from
                                            the merged view, so the text shows the changes of the mods.
          thn roundtrip <profile.json|folder> [<path>]
                                            Check that the canonical text of each .thn file, or of one file, gives the
                                            same tables, with exact numbers and key order, and that the text is a fixed
                                            point. Exit 1 on a difference. Each problem names the first different value.

        Agent server (milestone MCP1):
        """ + "\n" + McpCommands.Usage + "\n\n" + """
        Exit codes: 0 = OK, 1 = the data has problems, 2 = the command could not run (wrong arguments, files, folders).
        """;

    private static TextWriter Out = Console.Out;
    private static TextWriter Err = Console.Error;

    private static int Main(string[] args)
    {
        // The engine writes its log to the console. oltool shows only its own output and reports problems itself.
        Out = Console.Out;
        Err = Console.Error;
        Console.SetOut(TextWriter.Null);
        AppHandler.ConsoleInit(silent: true);
        try
        {
            return args switch
            {
                ["verify-base", var folder] => VerifyBase(folder),
                ["game", var folder, .. var options] => Game(folder, options),
                ["build", var profile] => Build(profile),
                ["validate", var profile, .. var options] => Validate(profile, options),
                ["deploy", var profile] => Deploy(profile),
                ["export", var profile, .. var options] => Export(profile, options),
                ["publish", var profile, .. var options] => Publish(profile, options),
                ["install", var profile, .. var options] => Install(profile, options),
                ["uninstall", var folder] => Uninstall(folder),
                ["playcopy", "check", var folder] => CheckPlayCopy(folder),
                ["playcopy", "remove", var folder] => RemovePlayCopy(folder),
                ["engine-lines", var folder, .. var options] => EngineLines(folder, options),
                ["ini", var source, var path] => PrintIni(source, path),
                ["dump", var source, var output] => Dump(source, output),
                ["thn", "roundtrip", var source] => ThnRoundTrip(source, null),
                ["thn", "roundtrip", var source, var path] => ThnRoundTrip(source, path),
                ["thn", var source, var path] => PrintThn(source, path),
                ["mcp", .. var mcp] => McpCommands.Run(mcp, Out, Err) ?? Fail(Usage),
                _ => Fail(Usage),
            };
        }
        catch (Exception e) when (e is ModLoadException or DeployException or EngineLoadException or PlayCopyException
                                      or IOException or UnauthorizedAccessException)
        {
            return Fail(e.Message);
        }
    }

    private static int VerifyBase(string folder)
    {
        if (!Directory.Exists(folder))
            return Fail($"The folder {folder} does not exist.");
        var (count, digest) = DataDigest.Compute(folder);
        var writable = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Count(f => !new FileInfo(f).IsReadOnly);
        Out.WriteLine($"{count} {digest}");
        var isRetail = count == DataDigest.RetailV1FileCount && digest == DataDigest.RetailV1;
        Out.WriteLine(isRetail ? "Matches the retail v1.0 data." : "Does NOT match the retail v1.0 data.");
        Out.WriteLine(writable == 0 ? "All files are read-only." : $"{writable} files are writable.");
        return isRetail && writable == 0 ? ExitOk : ExitProblems;
    }

    /// <summary>Prints the report of the game folder check (section 11 of the M11a design).</summary>
    private static int Game(string folder, string[] options)
    {
        var json = false;
        var hashes = false;
        string? manifest = null;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--json" when !json:
                    json = true;
                    break;
                case "--hashes" when !hashes:
                    hashes = true;
                    break;
                case "--write-manifest" when manifest == null && i + 1 < options.Length:
                    manifest = options[++i];
                    break;
                default:
                    return Fail(Usage);
            }
        }

        if (!Directory.Exists(folder) && !File.Exists(folder))
            return Fail($"The folder {folder} does not exist.");
        var report = GameDetector.Detect(folder);
        if (manifest != null)
        {
            if (report.Root == null)
                return Fail(report.Error ?? $"The folder {folder} holds no game.");
            int count;
            try
            {
                count = ManifestWriter.Write(report.Root, manifest);
            }
            catch (InvalidOperationException e)
            {
                return Fail(e.Message);
            }
            Out.WriteLine($"Wrote {count} manifest lines of {report.Root} to {Path.GetFullPath(manifest)}.");
            return ExitOk;
        }
        var lines = hashes && report.Root != null ? GameDetector.HashLines(report.Root) : [];
        if (json)
        {
            var node = JsonSerializer.SerializeToNode(report, ReportJson)!.AsObject();
            if (hashes)
                node["Hashes"] = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            Out.WriteLine(node.ToJsonString(ReportJson));
        }
        else
        {
            foreach (var line in GameReportText(report))
                Out.WriteLine(line);
            foreach (var line in lines)
                Out.WriteLine(line);
        }
        return report.IsGame ? ExitOk : ExitProblems;
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The lines of a report as text, in the order of the M11a design.</summary>
    private static IEnumerable<string> GameReportText(GameReport report)
    {
        yield return $"Game: {report.Name}";
        if (report.Root != null)
        {
            yield return $"Root: {report.Root}";
            yield return $"Build: {report.Build} (proof: {report.Proof.ToString().ToLowerInvariant()})";
            yield return $"Export target: {report.Target ?? "none"}";
            yield return report.ExePath == null ? "Game program: not found (game data only)" : $"Game program: {report.ExePath}";
            yield return report.Data == DataState.NotCompared
                ? report.DataText
                : $"{report.DataText} ({report.ComparedFiles} INI files compared with {report.Reference})";
            foreach (var file in report.DataFiles)
                yield return $"  {file}";
        }
        foreach (var finding in report.Findings)
            yield return $"{finding.Severity}: {finding.Text}";
    }

    private static int Build(string profilePath)
    {
        var (profile, overlay) = BuildOverlay(profilePath);
        Out.WriteLine($"Profile {profile.Name}: {profile.Mods.Count} mods: " +
                      string.Join(", ", profile.Mods.Select(m => m.Id)));
        foreach (var file in overlay.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            Out.WriteLine($"  {(file.DiskPath == null ? "merged " : "file   ")} {file.Path}");
        return Report(overlay);
    }

    private static int Validate(string profilePath, string[] options)
    {
        string? active = null, target = null, game = null;
        var json = false;
        for (var i = 0; i < options.Length; i++)
        {
            var hasValue = i + 1 < options.Length;
            switch (options[i])
            {
                case "--json" when !json:
                    json = true;
                    break;
                case "--active" when active == null && hasValue:
                    active = options[++i];
                    break;
                case "--target" when target == null && hasValue:
                    target = options[++i];
                    break;
                case "--game" when game == null && hasValue:
                    game = options[++i];
                    break;
                default:
                    return Fail(Usage);
            }
        }

        if (target != null || game != null)
            return target == null || active != null ? Fail(Usage) : ValidateTarget(profilePath, target, game, json);
        if (active == null && !json)
        {
            var (_, overlay) = BuildOverlay(profilePath);
            if (Report(overlay) != ExitOk)
                return ExitProblems;
            return ReportValidation(overlay);
        }

        var problems = Problems(ModProfile.Load(profilePath), active);
        if (json)
        {
            Out.WriteLine(ValidationJson.Write(problems));
            return problems.Any(p => p.Severity == DiagnosticSeverity.Error) ? ExitProblems : ExitOk;
        }
        foreach (var problem in problems)
            Err.WriteLine(problem);
        return Summary(problems);
    }

    /// <summary>Runs the checks of a profile on a retail target game (see <see cref="TargetValidation"/>) and prints
    /// their problems and notes. The command only reads the game folder.</summary>
    private static int ValidateTarget(string profilePath, string targetId, string? game, bool json)
    {
        if (ExportTarget.Find(targetId) is not { } target)
            return Fail(Usage);
        if (!target.IsRetail)
            return Fail($"The target {targetId} has no checks of the retail game. Run validate without --target.");
        if (game != null && !Directory.Exists(game))
            return Fail($"The folder {game} does not exist.");

        var (problems, notes) = TargetValidation.Run(ModProfile.Load(profilePath), target, game);
        if (json)
        {
            Out.WriteLine(ValidationJson.Write(problems));
            return problems.Any(p => p.Severity == DiagnosticSeverity.Error) ? ExitProblems : ExitOk;
        }
        foreach (var problem in problems)
            Err.WriteLine(problem);
        foreach (var note in notes)
            Out.WriteLine($"Note: {note}");
        return Summary(problems);
    }

    /// <summary>
    /// The merge problems and the data problems that a mod adds to the mods that load before it, or that all mods add
    /// to vanilla when <paramref name="activeId"/> is null. The data checks run only when the merge has no error.
    /// </summary>
    /// <exception cref="ModLoadException">The profile has no mod with the id, or the mods before it do not
    /// merge.</exception>
    private static List<Diagnostic> Problems(ModProfile profile, string? activeId)
    {
        ModOverlay? lower = null;
        if (activeId != null)
        {
            var index = profile.IndexOf(activeId);
            if (index < 0)
                throw new ModLoadException(profile.FilePath, $"The profile has no mod '{activeId}'.");
            lower = ModOverlay.Build(profile.BaseFolder, profile.Mods.Take(index).ToList(), DllStringResourceWriter.Instance,
                ThornEvaluator.Instance);
            if (lower.HasErrors)
                throw new ModLoadException(profile.FilePath,
                    $"The mods that load before '{activeId}' have merge errors. Run 'oltool build' on a profile with them.");
        }
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.ModsThrough(activeId), DllStringResourceWriter.Instance,
            ThornEvaluator.Instance);
        var problems = (lower == null ? overlay.Diagnostics : Diagnostic.NewIn(lower.Diagnostics, overlay.Diagnostics)).ToList();
        if (!overlay.HasErrors)
        {
            var before = lower?.CreateFileSystem() ?? FileSystem.FromPath(overlay.BaseFolder);
            problems.AddRange(ProfileValidator.NewProblems(before, overlay.CreateFileSystem(), overlay.CutscenePaths));
        }
        return problems;
    }

    private static int ReportValidation(ModOverlay overlay)
    {
        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(overlay.BaseFolder), overlay.CreateFileSystem(),
            overlay.CutscenePaths);
        foreach (var problem in problems)
            Err.WriteLine(problem);
        return Summary(problems);
    }

    private static int Summary(IReadOnlyList<Diagnostic> problems)
    {
        var errors = problems.Count(p => p.Severity == DiagnosticSeverity.Error);
        Out.WriteLine(errors > 0
            ? $"Validation failed: {errors} errors, {problems.Count - errors} warnings."
            : $"Validation OK: {problems.Count} warnings.");
        return errors > 0 ? ExitProblems : ExitOk;
    }

    private static int Dump(string source, string output)
    {
        File.WriteAllText(output, DataDump.Create(OpenSource(source)));
        Out.WriteLine($"Wrote {output}");
        return ExitOk;
    }

    private static int Deploy(string profilePath)
    {
        var (profile, overlay) = BuildOverlay(profilePath);
        if (Report(overlay) != ExitOk || ReportValidation(overlay) != ExitOk)
            return ExitProblems;
        ProfileDeployer.Result result;
        try
        {
            result = ProfileDeployer.Deploy(overlay, profile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"{e.Message}{Environment.NewLine}The deploy did not complete. Run deploy again to complete the folder.");
        }
        if (result.PreviousDeployIncomplete)
            Out.WriteLine("The last deploy did not complete. This deploy completed the folder.");
        if (result.Folder.Any(c => c > 0x7F))
            Out.WriteLine("Note: the deploy folder path has characters outside ASCII. lancer.exe reads it correctly " +
                          "until the game saves its settings. After that, run 'oltool deploy' again.");
        Out.WriteLine($"Deployed to {result.Folder}: {result.Copied} copied, {result.Written} written, " +
                      $"{result.Deleted} deleted, {result.Unchanged} unchanged.");
        Out.WriteLine($"Start the game: lancer.exe \"{ProfileDeployer.GameConfigPath(profile)}\"");
        return ExitOk;
    }

    /// <summary>Exports one mod of a profile for its targets (section 7.14 of the M11 design). The export goes into
    /// the version folder, runs the same-version guard and records the publish history (section 15 of the M11c
    /// design).</summary>
    private static int Export(string profilePath, string[] options)
    {
        string? modId = null, format = null, output = null;
        var targetIds = new List<string>();
        var games = new List<string>();
        bool checklist = false, probe = false, sameVersion = false;
        for (var i = 0; i < options.Length; i++)
        {
            var hasValue = i + 1 < options.Length;
            switch (options[i])
            {
                case "--mod" when modId == null && hasValue:
                    modId = options[++i];
                    break;
                case "--target" when hasValue:
                    targetIds.Add(options[++i]);
                    break;
                case "--game" when hasValue:
                    games.Add(options[++i]);
                    break;
                case "--format" when format == null && hasValue:
                    format = options[++i];
                    break;
                case "--out" when output == null && hasValue:
                    output = options[++i];
                    break;
                case "--checklist" when !checklist:
                    checklist = true;
                    break;
                case "--flmm-probe" when !probe:
                    probe = true;
                    break;
                case "--same-version" when !sameVersion:
                    sameVersion = true;
                    break;
                default:
                    return Fail(Usage);
            }
        }
        if (modId == null || targetIds.Count == 0 || targetIds.Distinct().Count() != targetIds.Count ||
            targetIds.Any(t => ExportTarget.Find(t) == null))
            return Fail(Usage);
        var targets = ExportTarget.All.Where(t => targetIds.Contains(t.Id)).ToList();
        ExportForms forms;
        switch (format)
        {
            case null or "all":
                forms = ExportForms.All;
                break;
            case "dropin":
                forms = ExportForms.DropIn;
                break;
            case "flmm":
                forms = ExportForms.Flmm;
                break;
            default:
                return Fail(Usage);
        }

        var profile = ModProfile.Load(profilePath);
        var index = profile.IndexOf(modId);
        if (index < 0)
            return Fail($"The profile has no mod '{modId}'.");
        var mod = profile.Mods[index];
        var reports = new List<GameReport>();
        if (targets.Any(t => t.IsRetail))
        {
            foreach (var game in games)
            {
                if (!Directory.Exists(game))
                    return Fail($"The folder {game} does not exist.");
                reports.Add(GameDetector.Detect(game));
            }
            if (games.Count == 0)
                reports.Add(GameDetector.CheckRoot(profile.BaseFolder));
        }
        // The output root holds the version folders and the publish history (decisions D565 and D566).
        var root = ModExport.DefaultFolder(profile.FilePath, modId);
        if (output == null && ModVersion.SafeProblem(mod.Manifest.Version) is { } unsafeVersion)
        {
            Err.WriteLine(unsafeVersion);
            return ExitProblems;
        }
        // Each retail target uses the game of its kind.
        var request = ExportRequest.Of(mod, targets, reports, output ?? ModExport.VersionFolder(root, mod.Manifest), forms,
            DllStringResourceWriter.Instance, ThornEvaluator.Instance, out var publishProblems) with
        {
            FlmmProbe = probe,
            ChecklistOnly = checklist,
            OpenLancerBase = GameBase.Standard(profile.BaseFolder),
        };
        foreach (var problem in publishProblems)
            Err.WriteLine($"Warning: {mod.Folder}: {problem}");
        // The probe is not for players, and the checklist writes nothing. Neither runs the guard or goes into the
        // history.
        if (checklist || probe)
        {
            var prepared = ModExport.Prepare(request);
            PrintProblems(prepared);
            if (checklist)
            {
                foreach (var line in prepared.Checklist)
                    Out.WriteLine(line);
                return prepared.Problems.Any(p => p.Severity == DiagnosticSeverity.Error) ? ExitProblems : ExitOk;
            }
            return PrintPackages(ModExport.Write(prepared, []));
        }

        var version = mod.Manifest.Version;
        var location = new HistoryLocation(root, modId, mod.Folder,
            [profile.BaseFolder, .. reports.Select(r => r.Root).OfType<string>()]);
        var export = GuardedExport.Prepare(request, location, DateTime.UtcNow);
        if (export.Refusal is { } noHistory)
            return Fail(noHistory);
        PrintProblems(export.Prepared!);
        var guard = export.Guard;
        if (guard.Kind != GuardKind.None)
            PrintWarnings(export.RecordChecks());
        if (guard.Kind == GuardKind.Published)
        {
            Err.WriteLine(sameVersion ? PublishMessages.SameVersionPublished(version) : guard.Text);
            return ExitProblems;
        }
        if (guard.Kind == GuardKind.Exported && (!sameVersion || !guard.CanReplace))
        {
            Err.WriteLine(sameVersion ? guard.PartialText : guard.Text);
            return ExitProblems;
        }

        var result = export.Write();
        var exit = PrintPackages(result);
        var (recorded, warnings) = export.RecordExport(result, replace: guard.Kind == GuardKind.Exported);
        PrintWarnings(warnings);
        foreach (var line in recorded?.Messages(version) ?? [])
            Out.WriteLine(line);
        return exit;
    }

    private static void PrintProblems(PreparedExport prepared)
    {
        foreach (var problem in prepared.Problems)
            Err.WriteLine(problem);
    }

    /// <summary>Prints a line for each package, and gives the exit code of the result.</summary>
    private static int PrintPackages(ExportResult result)
    {
        foreach (var package in result.Packages)
            Out.WriteLine(package.Path != null ? $"Wrote {package.Path}" : $"Refused {package.FileName}");
        return result.Succeeded ? ExitOk : ExitProblems;
    }

    /// <summary>Prints the warnings of a write of the publish history.</summary>
    private static void PrintWarnings(IEnumerable<string> warnings)
    {
        foreach (var message in warnings)
            Err.WriteLine($"Warning: {message}");
    }

    /// <summary>Prints the publish checklist of a mod (section 15 of the M11c design). It reads the history, the last
    /// checks and the profile base, and runs no check.</summary>
    private static int Publish(string profilePath, string[] options)
    {
        string? modId = null;
        var json = false;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--mod" when modId == null && i + 1 < options.Length:
                    modId = options[++i];
                    break;
                case "--json" when !json:
                    json = true;
                    break;
                default:
                    return Fail(Usage);
            }
        }
        if (modId == null)
            return Fail(Usage);
        var profile = ModProfile.Load(profilePath);
        var index = profile.IndexOf(modId);
        if (index < 0)
            return Fail($"The profile has no mod '{modId}'.");
        var mod = profile.Mods[index];
        var input = ChecklistInput.Read(mod, ModExport.DefaultFolder(profile.FilePath, modId),
            [GameDetector.CheckRoot(profile.BaseFolder)], [], out var problems);
        foreach (var problem in problems)
            Err.WriteLine($"Warning: {problem}");
        var checklist = PublishChecklist.Of(input);

        if (json)
        {
            Out.WriteLine(JsonSerializer.Serialize(new
            {
                mod = mod.Id,
                name = mod.Manifest.Name,
                version = mod.Manifest.Version,
                complete = checklist.CompleteCount,
                total = checklist.Steps.Count,
                steps = checklist.Steps.Select(s => new { number = s.Number, name = s.Name, complete = s.Complete, missing = s.Missing }),
                recommended = checklist.Recommended.Select(r => r.Text),
            }, ReportJson));
        }
        else
        {
            Out.WriteLine($"{mod.Manifest.Name} {mod.Manifest.Version} ({mod.Id}): {checklist.Summary}");
            foreach (var step in checklist.Steps)
                Out.WriteLine($"{step.Number}. {step.Name}: {(step.Complete ? "done" : string.Join(" ", step.Missing))}");
            if (checklist.Recommended.Count > 0)
            {
                Out.WriteLine("Recommended:");
                foreach (var item in checklist.Recommended)
                    Out.WriteLine($"- {item.Text}");
            }
        }
        return checklist.AllComplete ? ExitOk : ExitProblems;
    }

    /// <summary>Installs the mods of a profile into the play copy of a game (section 8.9 of the M11 design). It starts no
    /// program.</summary>
    private static int Install(string profilePath, string[] options)
    {
        string? game = null, into = null;
        var full = false;
        for (var i = 0; i < options.Length; i++)
        {
            var hasValue = i + 1 < options.Length;
            switch (options[i])
            {
                case "--game" when game == null && hasValue:
                    game = options[++i];
                    break;
                case "--into" when into == null && hasValue:
                    into = options[++i];
                    break;
                case "--full-copy" when !full:
                    full = true;
                    break;
                default:
                    return Fail(Usage);
            }
        }
        if (game == null)
            return Fail(Usage);
        if (!Directory.Exists(game))
            return Fail($"The folder {game} does not exist.");
        var report = GameDetector.Detect(game);
        if (!report.IsGame || report.Base is not { } source)
            return Fail(report.Error ?? $"The folder {game} holds no game.");

        var profile = ModProfile.Load(profilePath);
        if (PlayBuild.Refusal(report, profile.Mods) is { } refusal)
        {
            Err.WriteLine(refusal);
            return ExitProblems;
        }
        var build = PlayBuild.Run(report, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        foreach (var problem in build.Problems)
            Err.WriteLine(problem);
        if (build.HasErrors)
        {
            Out.WriteLine("The install stopped. Fix the errors, then install again.");
            return ExitProblems;
        }

        var folder = Path.GetFullPath(into ?? PlayCopy.DefaultFolder(source.Root));
        // The folder of the profile file is the workspace of oltool.
        var protectedFolders = PlayCopy.ProtectedFolders(Path.GetDirectoryName(profile.FilePath)!, profile);
        if (PlayCopy.ProtectedProblem(folder, protectedFolders) is { } overlap)
            return Fail(overlap);
        var mode = full ? PlayCopyMode.Full : PlayCopyMode.Links;
        if (mode == PlayCopyMode.Links && !PlayCopyRecord.Exists(folder) && PlayCopy.Probe(source, folder) is { } reason)
            return Fail($"{PlayCopyTexts.ProbeFailed(Path.GetPathRoot(source.Root) ?? source.Root, reason)} Use " +
                        "--into with a folder on that drive, or --full-copy.");
        if (!PlayCopyRecord.Exists(folder))
            Out.WriteLine(PlayCopyTexts.Make(report.Name, folder));
        using var copy = PlayCopy.Make(source, folder, mode, report.Build);
        var result = copy.Install(build.Files, profile.FilePath);
        foreach (var problem in result.Problems)
            Err.WriteLine(problem);
        Out.WriteLine($"Play copy: {copy.Root}");
        Out.WriteLine($"Program: {copy.ProgramPath ?? "none"}");
        Out.WriteLine($"Installed {build.Files.Files.Count} files: {result.Written} written, {result.Restored} restored. " +
                      $"The play copy has {result.Links} links and {result.Copies} real files.");
        return ExitOk;
    }

    /// <summary>Gives each installed file of a play copy its kind of the make again.</summary>
    private static int Uninstall(string folder)
    {
        using var copy = PlayCopy.Open(folder);
        var result = copy.Uninstall();
        foreach (var problem in result.Problems)
            Err.WriteLine(problem);
        Out.WriteLine($"Uninstalled the mods of {copy.Root}: {result.Restored} files restored.");
        return ExitOk;
    }

    /// <summary>Prints each difference of a play copy and the counts of its links and real files.</summary>
    private static int CheckPlayCopy(string folder)
    {
        using var copy = PlayCopy.Open(folder);
        var result = copy.Check();
        foreach (var problem in result.Problems)
            Err.WriteLine(problem);
        Out.WriteLine($"{copy.Root}: {result.Links} links, {result.Copies} real files, {result.Problems.Count} " +
                      "differences.");
        return result.Problems.Count == 0 ? ExitOk : ExitProblems;
    }

    private static int RemovePlayCopy(string folder)
    {
        PlayCopy.Remove(folder);
        Out.WriteLine($"Removed the play copy {Path.GetFullPath(folder)}. The game folder did not change.");
        return ExitOk;
    }

    /// <summary>The base run of the console filter (decision D516): the engine lines of a game folder without mods.</summary>
    private static int EngineLines(string folder, string[] options)
    {
        var json = options is ["--json"];
        if (options.Length > 0 && !json)
            return Fail(Usage);
        if (!Directory.Exists(folder))
            return Fail($"The folder {folder} does not exist.");
        if (!GameConfig.CheckFLDirectory(folder))
            return Fail($"The folder {folder} has no EXE\\freelancer.ini.");
        var run = BaseEngineLines.Run(Path.GetFullPath(folder));
        // A system or a mission script that does not load is not an engine line. The Scene and the Mission Studio
        // report it themselves.
        foreach (var failure in run.Failures)
            Err.WriteLine(failure);
        var lines = run.All.ToList();
        if (json)
        {
            Out.WriteLine(BaseEngineLines.ToJson(BaseEngineLines.Count(lines)));
            return ExitOk;
        }

        var byKind = lines.GroupBy(l => EngineLineRules.Match(l)?.Id ?? "-").OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var kind in byKind)
        {
            Out.WriteLine($"{kind.Key}\t{kind.Count()}");
            if (kind.Key == "-")
            {
                foreach (var line in kind)
                    Out.WriteLine($"\t{line.Normalized}");
            }
        }
        Out.WriteLine($"{lines.Count} lines.");
        return ExitOk;
    }

    private static int PrintIni(string source, string path)
    {
        var fs = OpenSource(source);
        if (!fs.FileExists(path))
            return Fail($"{path} is not in {source}.");
        using var input = fs.Open(path);
        var sections = IniFile.ParseFile(path, input, preparse: false).ToList();
        using var output = Console.OpenStandardOutput();
        IniWriter.WriteIni(output, sections);
        return ExitOk;
    }

    /// <summary>Prints the canonical text of a script. The bytes come through the file system, because a merged file has
    /// no file on disk.</summary>
    private static int PrintThn(string source, string path)
    {
        var fs = OpenSource(source);
        if (!fs.FileExists(path))
            return Fail($"{path} is not in {source}.");
        try
        {
            Out.Write(ThnWriter.Write(ThornEvaluator.Instance.Evaluate(fs.ReadAllBytes(path), path)));
            return ExitOk;
        }
        catch (ThnEvaluationException e)
        {
            Err.WriteLine(e.Message);
            return ExitProblems;
        }
    }

    /// <summary>Evaluates each script, writes it, evaluates the text and compares. Prints one line for each script
    /// with a problem, then a summary.</summary>
    private static int ThnRoundTrip(string source, string? path)
    {
        var fs = OpenSource(source);
        List<string> paths;
        if (path == null)
            paths = ThnFiles(fs);
        else if (fs.FileExists(path))
            paths = [path];
        else
            return Fail($"{path} is not in {source}.");

        var failed = 0;
        foreach (var file in paths)
        {
            string line;
            try
            {
                var problem = ThnWriter.RoundTripProblem(ThornEvaluator.Instance.Evaluate(fs.ReadAllBytes(file), file),
                    ThornEvaluator.Instance, file);
                if (problem == null)
                    continue;
                line = $"{file}: {problem}";
            }
            catch (ThnEvaluationException e)
            {
                // The message starts with the file.
                line = e.Message;
            }
            failed++;
            Err.WriteLine(line);
        }
        Out.WriteLine($"Round trip: {paths.Count - failed} of {paths.Count} scripts give the same tables, with key " +
                      "order, and a fixed point text.");
        return failed == 0 ? ExitOk : ExitProblems;
    }

    /// <summary>The paths of all <c>.thn</c> files of a file system, in a stable order.</summary>
    private static List<string> ThnFiles(FileSystem fs)
    {
        var files = new List<string>();
        var folders = new Stack<string>([""]);
        while (folders.TryPop(out var folder))
        {
            foreach (var name in fs.GetFiles(folder))
            {
                if (name.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
                    files.Add(folder.Length == 0 ? name : $@"{folder}\{name}");
            }
            foreach (var name in fs.GetDirectories(folder))
                folders.Push(folder.Length == 0 ? name : $@"{folder}\{name}");
        }
        files.Sort(VfsPath.Comparer);
        return files;
    }

    /// <summary>A profile file gives its merged view. A folder gives its plain contents.</summary>
    private static FileSystem OpenSource(string source)
    {
        if (Directory.Exists(source))
            return FileSystem.FromPath(source);
        if (!File.Exists(source))
            throw new ModLoadException(source, "This is not a profile file or a folder.");
        var (_, overlay) = BuildOverlay(source);
        if (overlay.HasErrors)
            throw new ModLoadException(source, "The profile has merge errors. Run 'oltool build' to see them.");
        return overlay.CreateFileSystem();
    }

    private static (ModProfile, ModOverlay) BuildOverlay(string profilePath)
    {
        var profile = ModProfile.Load(profilePath);
        return (profile, ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance));
    }

    private static int Report(ModOverlay overlay)
    {
        foreach (var d in overlay.Diagnostics)
            Err.WriteLine(d);
        Out.WriteLine(overlay.HasErrors
            ? $"Failed: {overlay.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} errors."
            : $"OK: {overlay.Diagnostics.Count} warnings.");
        return overlay.HasErrors ? ExitProblems : ExitOk;
    }

    private static int Fail(string message)
    {
        Err.WriteLine(message);
        return ExitFailure;
    }
}
