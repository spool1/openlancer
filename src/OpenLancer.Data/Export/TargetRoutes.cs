using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Export;

/// <summary>What a route table of a target build changes, for the checklist.</summary>
/// <param name="Diff">The routes that the table adds, makes shorter, makes longer and removes. The diff compares the
/// table with the route file that the game has at its path (see <see cref="TargetRoutes.GameFile"/>).</param>
/// <param name="Unreached">The systems that the mods add and that no route of the table reaches.</param>
internal sealed record RouteFileChange(RouteDiff Diff, IReadOnlyList<string> Unreached);

/// <summary>
/// The route tables of a target build for a retail game. The retail game reads three route tables at its start, and a
/// drop-in zip cannot delete them. So when the mods change the routes, the build makes the three tables from the merged
/// data of the target game and adds them as generated files. The play copy, the drop-in zip and the FLMM package get
/// them. A mod does not hold route tables.
/// </summary>
/// <remarks>
/// <para>The build compares the tables of the merged data with the tables of the game data without mods. Only a
/// change of the routes writes the files. When no file of the merge is <c>universe.ini</c>, a system file, a solar
/// archetype file or a gate lock file, the routes cannot change, and the build reads no graph.</para>
/// <para>The route files of a mod (whole files or patches) go to the game only when the build makes no tables. A
/// warning tells the modder.</para>
/// </remarks>
internal sealed class TargetRoutes
{
    private readonly ModOverlay overlay;
    private readonly FileSystem baseFs;
    private readonly IReadOnlyList<Mod> mods;

    /// <summary>The install path of each table, from the data folder of the merged <c>freelancer.ini</c>.</summary>
    private readonly Dictionary<string, RouteKind> paths = new(VfsPath.Comparer);

    /// <summary>The install paths of the files that the mods add or change.</summary>
    private readonly HashSet<string> changed;

    private readonly Dictionary<string, IReadOnlyList<Section>?> baseFiles = new(VfsPath.Comparer);
    private FileSystem? mergedFs;

    private TargetRoutes(ModOverlay overlay, FileSystem baseFs, IReadOnlyList<Mod> mods)
    {
        this.overlay = overlay;
        this.baseFs = baseFs;
        this.mods = mods;
        changed = overlay.Files.Select(f => f.Path).ToHashSet(VfsPath.Comparer);
        var freelancerIni = Merged(ModOverlay.FreelancerIniPath) ?? [];
        foreach (var kind in Enum.GetValues<RouteKind>())
        {
            // The path keeps the letter case of the folders of the game, as the other files of a package do.
            if (DataPaths.Resolve(freelancerIni, RouteTables.DataPath(kind)) is not { } path)
                continue;
            paths[overlay.Base.ToDisk(path) == path ? PathRules.WithDiskCase(overlay.Base.Root, path) : path] = kind;
        }
        GameSource = new FileUniverseSource(Base, ListRules.Retail);
        MergedSource = new FileUniverseSource(Merged, ListRules.Retail);
    }

    /// <summary>The jump graph source of the game data without mods.</summary>
    internal FileUniverseSource GameSource { get; }

    /// <summary>The jump graph source of the merged data: the merged files, and the files of the game that no mod
    /// changes.</summary>
    internal FileUniverseSource MergedSource { get; }

    /// <summary>The files of the mods at the paths of the tables. The build adds them only when it makes no
    /// tables.</summary>
    public List<TargetFile> ModFiles { get; } = [];

    /// <summary>The problems of <see cref="ModFiles"/>. The build reports them only when it makes no tables.</summary>
    public List<Diagnostic> ModProblems { get; } = [];

    /// <param name="baseFs">The files of the target game.</param>
    public static TargetRoutes Of(ModOverlay overlay, FileSystem baseFs, IReadOnlyList<Mod> mods) =>
        new(overlay, baseFs, mods);

    /// <summary>True when a path is the path of a route table.</summary>
    public bool Holds(string path) => paths.ContainsKey(path);

    /// <summary>
    /// Adds the generated tables to the files of the build when the mods change the routes. Else it adds the route
    /// files of the mods. The list stays in the order of the install paths.
    /// </summary>
    public void Finish(List<TargetFile> files, List<Diagnostic> diagnostics)
    {
        var owners = mods.Where(m => m.Files.Keys.Concat(m.Patches.Keys).Any(Holds)).ToList();
        var made = Make(diagnostics);
        if (made.Count > 0)
        {
            files.AddRange(made);
            diagnostics.AddRange(owners.Select(m => Diagnostic.Warning(m.Folder,
                $"Mod '{m.Id}' has its own route table files. OpenLancer makes new route tables from the merged " +
                "data, so the game does not get the files of the mod.")));
        }
        else
        {
            files.AddRange(ModFiles);
            diagnostics.AddRange(ModProblems);
            diagnostics.AddRange(owners.Select(m => Diagnostic.Warning(m.Folder,
                $"Mod '{m.Id}' has its own route table files. The merged data gives the routes of the game, so " +
                "OpenLancer makes no route tables. The game gets the files of the mod as they are, and they can be " +
                "out of date.")));
        }
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
    }

    /// <summary>The generated files of the tables, or none when the mods do not change the routes.</summary>
    private List<TargetFile> Make(List<Diagnostic> diagnostics)
    {
        if (!MayChange())
            return [];
        var gameGraph = UniverseGraph.Build(GameSource);
        var mergedGraph = UniverseGraph.Build(MergedSource);
        var before = RouteTables.Build(gameGraph);
        var after = RouteTables.Build(mergedGraph);
        if (after.SameRoutes(before))
            return [];
        var added = mergedGraph.Systems.Select(s => s.Nickname)
            .Where(n => gameGraph.System(n) == null).ToList();
        var result = new List<TargetFile>();
        foreach (var (path, kind) in paths)
        {
            var table = after.Tables[(int)kind];
            if (table.TooLong.ToList() is [var first, ..] tooLong)
            {
                var more = tooLong.Count switch
                {
                    1 => "",
                    2 => " 1 other route is too long too.",
                    _ => $" {tooLong.Count - 1} other routes are too long too.",
                };
                diagnostics.Add(Diagnostic.Error(path,
                    $"The route from {first.From} to {first.To} is too long for the game. The game reads only the " +
                    "first 1,023 bytes of a line, and it reads the rest as the next line. " +
                    $"Use shorter system nicknames, or add a connection that makes the route shorter.{more}"));
            }
            result.Add(new TargetFile(path, TargetFileKind.Ini, table.Text())
            {
                HasBase = baseFs.FileExists(path),
                Generated = true,
                Routes = new RouteFileChange(RouteTable.Diff(GameFile(path, kind) ?? before.Tables[(int)kind], table),
                    table.Unreached(added)),
            });
        }
        return result;
    }

    /// <summary>
    /// The route table that the game has at a path, or null when the game has no file at the path or the file does not
    /// parse. The tables of a game can differ from the tables of its data. For example, HD Edition 0.7.1 locks a jump
    /// hole of Ku07, and it keeps the route files of the retail game. When the game has no file, the checklist compares
    /// with the table of the game data without mods.
    /// </summary>
    private RouteTable? GameFile(string path, RouteKind kind) =>
        Base(path) is { } sections ? RouteTable.Read(kind, sections) : null;

    /// <summary>True when a file of the merge can change the routes: <c>universe.ini</c>, a system file, a solar
    /// archetype file or a gate lock file. Else the build reads no graph.</summary>
    internal bool MayChange()
    {
        if (paths.Count == 0)
            return false;
        var (game, merged) = (GameSource, MergedSource);
        if (!VfsPath.Comparer.Equals(game.UniversePath ?? "", merged.UniversePath ?? "") ||
            !game.SolarPaths.SequenceEqual(merged.SolarPaths, VfsPath.Comparer) ||
            !game.LockPaths.SequenceEqual(merged.LockPaths, VfsPath.Comparer))
            return true;
        if (merged.UniversePath is { } universe && changed.Contains(universe) ||
            merged.SolarPaths.Concat(merged.LockPaths).Any(changed.Contains))
            return true;
        // universe.ini does not change, so the system files of the game are the system files of the merge.
        return game.UniversePath is { } path && (Base(path) ?? [])
            .Where(s => s.Name.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(s => SystemFiles.FileValue(s) is { } value ? SystemFiles.FilePath(path, value) : null)
            .Any(p => p != null && changed.Contains(p));
    }

    /// <summary>The sections of a file of the game, read once.</summary>
    private IReadOnlyList<Section>? Base(string path)
    {
        if (!baseFiles.TryGetValue(path, out var sections))
            baseFiles[path] = sections = CheckView.Read(baseFs, path);
        return sections;
    }

    /// <summary>The sections of a merged file: the kept merged document, or the file of a mod, or the file of the
    /// game.</summary>
    private IReadOnlyList<Section>? Merged(string path)
    {
        if (!changed.Contains(path))
            return Base(path);
        return overlay.MergedDocument(path)?.Sections ?? CheckView.Read(mergedFs ??= overlay.CreateFileSystem(), path);
    }
}
