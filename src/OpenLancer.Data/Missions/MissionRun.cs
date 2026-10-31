using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The snapshot of one run of the mission checks (section 12.3 of the M6a design). The UI thread makes it. The worker
/// reads only the snapshot: it does not call the edit session, the data lists or the Console.
/// </summary>
/// <remarks>The overlay holds W of the mission files that the edit session changed and did not save (section 7 of the
/// M6b design). A run reads a file of the overlay in place of the file of the edit view.</remarks>
public sealed record MissionInputs
{
    /// <summary>The file system of the edit view.</summary>
    public required FileSystem EditView { get; init; }

    /// <summary>The file system of the base folder without mods (B), or null. The base-game runs of the scripts that a
    /// mod changes read it (decision D551).</summary>
    public FileSystem? BaseView { get; init; }

    /// <summary>The sections of <c>freelancer.ini</c> of the edit view.</summary>
    public required IReadOnlyList<Section> FreelancerIni { get; init; }

    /// <summary>The names of the game data outside the scripts, and the files.</summary>
    public MissionWorld World { get; init; } = MissionWorld.Empty;

    /// <summary>
    /// The names of the base game for the base-game runs (decision D551). They are the names of <see cref="World"/> and
    /// the names that a mod removes or renames. Null when no mod removes or renames a name that the checks read. Then
    /// the base-game runs use <see cref="World"/>.
    /// </summary>
    public MissionWorld? BaseWorld { get; init; }

    /// <summary>Which mod changes which script and section.</summary>
    public IMissionLedger Ledger { get; init; } = NoMods.Instance;

    public IEngineCoverage Coverage { get; init; } = FullCoverage.Instance;

    public MissionSchema Schema { get; init; } = MissionSchema.Retail;

    /// <summary>The active mod, or null.</summary>
    public string? ActiveModId { get; init; }

    /// <summary>The check options of the active mod.</summary>
    public MissionCheckOptions Options { get; init; } = new();

    /// <summary>The number of the run. A run that a newer run replaced drops its result.</summary>
    public int Generation { get; init; }

    /// <summary>
    /// W of the mission files that the edit session changed and did not save, by install path in normal form (section 7
    /// of the M6b design). The states of W cannot change, so a worker can read them.
    /// </summary>
    public IReadOnlyDictionary<string, MissionOverlayFile> Overlay { get; init; } =
        new Dictionary<string, MissionOverlayFile>(VfsPath.Comparer);

    /// <summary>The install path of a path relative to the data folder, or null.</summary>
    public string? DataPath(string relative) => DataPaths.Resolve(FreelancerIni, relative);

    /// <summary>
    /// The inputs with an overlay of W. The ledger then gives the mods of each file of the overlay from the overlay, and
    /// the mods of the other files from the ledger of these inputs.
    /// </summary>
    public MissionInputs WithOverlay(IReadOnlyDictionary<string, MissionOverlayFile> overlay)
    {
        var saved = Ledger is OverlayLedger known ? known.Saved : Ledger;
        return this with
        {
            Overlay = overlay,
            Ledger = overlay.Count == 0 ? saved : new OverlayLedger(saved, overlay),
        };
    }

    /// <summary>The world of the checks of W: a file below the data folder that the overlay adds exists, and a file that
    /// it deletes does not exist.</summary>
    internal MissionWorld CheckWorld => Overlay.Count == 0
        ? World
        : World.WithFiles(relative => DataPath(relative) is { } path ? FileExists(path) : World.FileExists(relative));

    /// <summary>True when the edit view, or the overlay, has a file.</summary>
    internal bool FileExists(string path) =>
        Overlay.TryGetValue(VfsPath.Normalize(path) ?? path, out var file) ? file.Exists : EditView.FileExists(path);

    /// <summary>The sections of a file of the overlay, or null when the overlay does not have the file.</summary>
    internal MissionOverlayFile? OverlayOf(string path) =>
        Overlay.GetValueOrDefault(VfsPath.Normalize(path) ?? path);
}

/// <summary>W of one mission file in the overlay of a run (section 7 of the M6b design), with the mods that change it.
/// </summary>
/// <param name="Sections">The sections of W. They never change.</param>
/// <param name="IsBinary">True when the file is a binary INI file.</param>
public sealed record MissionOverlayFile(IReadOnlyList<Section> Sections, bool IsBinary)
{
    /// <summary>False when W has no such file, for example after a delete of a whole file of the active mod.</summary>
    public bool Exists { get; init; } = true;

    /// <summary>The mods that change the file in W, in load order.</summary>
    public IReadOnlyList<string> FileMods { get; init; } = [];

    /// <summary>The mod whose whole file gives the content of the file, or null.</summary>
    public string? ReplacedBy { get; init; }

    /// <summary>The mods that change each section of W, in load order. A section that no mod changes has no
    /// entry.</summary>
    public IReadOnlyDictionary<SectionIdentity, IReadOnlyList<string>> SectionMods { get; init; } =
        new Dictionary<SectionIdentity, IReadOnlyList<string>>();

    /// <summary>The state of W that the overlay holds. The model cache uses it as a key.</summary>
    public object? State { get; init; }
}

/// <summary>A ledger that answers for the files of an overlay from the overlay, and for the other files from the
/// ledger of the saved files.</summary>
internal sealed class OverlayLedger(IMissionLedger saved, IReadOnlyDictionary<string, MissionOverlayFile> overlay)
    : IMissionLedger
{
    /// <summary>The ledger of the saved files.</summary>
    public IMissionLedger Saved { get; } = saved;

    public IReadOnlyList<string> ModsThatChangeFile(string path) =>
        Find(path) is { } file ? file.FileMods : Saved.ModsThatChangeFile(path);

    public IReadOnlyList<string> ModsThatChangeSection(string path, SectionIdentity section) =>
        Find(path) is { } file ? file.SectionMods.GetValueOrDefault(section) ?? [] : Saved.ModsThatChangeSection(path, section);

    public string? ReplacedBy(string path) => Find(path) is { } file ? file.ReplacedBy : Saved.ReplacedBy(path);

    private MissionOverlayFile? Find(string path) => overlay.GetValueOrDefault(VfsPath.Normalize(path) ?? path);
}

/// <summary>The analysis of one mission script of a run.</summary>
/// <param name="Bytes">The bytes of the script in the edit view.</param>
/// <param name="Problems">The problems with their origins and severities.</param>
/// <param name="BaseIndex">The index of the base-game run of the script (decision D551), or null when the script has
/// none.</param>
public sealed record MissionAnalysis(MissionScriptInfo Script, byte[] Bytes, MissionDocument Document, MissionIndex Index,
    IReadOnlyList<Diagnostic> Problems, MissionIndex? BaseIndex)
{
    /// <summary>The behavior prefab instances that the matcher finds (section 8 of the M6a design).</summary>
    public MissionPrefabResult? Prefabs { get; init; }

    /// <summary>The acts and the frame "Not started by the mission" (section 6.4 of the M6a design).</summary>
    public MissionActs? Acts { get; init; }

    /// <summary>The prefab instances of the base-game run of the script, or null.</summary>
    public MissionPrefabResult? BasePrefabs { get; init; }

    /// <summary>The install path of the mission ship file that <c>[Mission] npc_ship_file</c> names, or null.</summary>
    public string? ShipFile { get; init; }

    /// <summary>The number of <c>[NPCShipArch]</c> sections in the mission ship file.</summary>
    public int ShipBuilds { get; init; }

    /// <summary>True when <see cref="Bytes"/> are the bytes of the file. The analysis of W of the overlay has no file
    /// bytes, so its raw text is the canonical text.</summary>
    public bool HasFileBytes { get; init; } = true;

    /// <summary>The analysis before the linked prefab instances of the graph file joined it (section 5.1 of the M6b
    /// design), or null for an analysis without them. The editor links an analysis again from it.</summary>
    public MissionAnalysis? Matched { get; init; }

    /// <summary>The raw text of the script (decision D529): the file text with its comments for a text script that no
    /// mod changes, else the canonical text.</summary>
    public RawText RawText() => OpenLancer.Data.Editing.Fields.RawText.Of(Script.Path, Document.Sections.Select(s => s.Section).ToList(),
        Document.IsBinary, Bytes, fileText: HasFileBytes && Script.State == MissionScriptState.BaseGame);
}

/// <summary>A news item or a rumor with the story stages of its range (section 5.2 of the M6a design).</summary>
/// <param name="From">The index of the first stage.</param>
/// <param name="To">The index of the last stage.</param>
public readonly record struct StoryItem(int From, int To, bool Rumor);

/// <summary>The result of one run of the mission checks.</summary>
/// <param name="Scripts">The scripts of the browser: the slot scripts in slot order, then the other scripts.</param>
/// <param name="Analyses">The analysis of each script that exists, by install path.</param>
/// <param name="ShipFiles">The files of the NPC ship builds that the scripts can name.</param>
public sealed record MissionRunResult(int Generation, IReadOnlyList<MissionScriptInfo> Scripts,
    IReadOnlyDictionary<string, MissionAnalysis> Analyses, IReadOnlyList<string> ShipFiles, TimeSpan Elapsed)
{
    /// <summary>The news items and rumors with a stage range.</summary>
    public IReadOnlyList<StoryItem> StoryItems { get; init; } = [];

    /// <summary>The ship builds of <see cref="ShipFiles"/> in the edit view.</summary>
    public NpcShipBuilds ShipBuilds { get; init; } = NpcShipBuilds.Empty;

    /// <summary>The ship builds of the files in the base folder without mods (B), or null when no mod changes a file
    /// of the ship builds. Then the base game has the builds of <see cref="ShipBuilds"/>.</summary>
    public NpcShipBuilds? BaseShipBuilds { get; init; }

    /// <summary>The names of the base game for the base-game comparisons (decision D551), or null when they are the
    /// names of the edit view.</summary>
    public MissionWorld? BaseWorld { get; init; }

    /// <summary>The problems of all scripts.</summary>
    public IEnumerable<Diagnostic> Problems => Scripts.Select(s => Analyses.GetValueOrDefault(s.Path))
        .OfType<MissionAnalysis>().SelectMany(a => a.Problems);

    /// <summary>The analysis of a script, or null.</summary>
    public MissionAnalysis? Of(string path) => VfsPath.Normalize(path) is { } normal ? Analyses.GetValueOrDefault(normal) : null;

    /// <summary>True when the path is a mission script of the run (section 12.4 of the M6a design).</summary>
    public bool IsScript(string path) =>
        VfsPath.Normalize(path) is { } normal && Scripts.Any(s => VfsPath.Comparer.Equals(s.Path, normal));
}

/// <summary>
/// The worker run of the mission checks (section 12.3 of the M6a design): it finds the scripts, reads each one, builds
/// its document, its index and its prefab instances, runs the checks, and gives each problem its origin.
/// </summary>
/// <remarks>A file of the overlay of the inputs takes the place of the file of the edit view (section 7 of the M6b
/// design). So the run shows the unsaved edits of the session.</remarks>
public static class MissionRun
{
    /// <summary>Runs the checks on a snapshot. It reads only the snapshot, so a worker task can call it.</summary>
    public static MissionRunResult Execute(MissionInputs inputs)
    {
        var watch = Stopwatch.StartNew();
        var schema = inputs.Schema;
        var scanned = inputs.DataPath("MISSIONS") is { } missions ? Scan(inputs, missions) : [];
        var slotPaths = schema.StorySlots
            .Select(slot => (slot, MissionFiles.SlotPath(inputs.FreelancerIni, slot) is { } path &&
                                   inputs.FileExists(path) ? path : null))
            .ToList();
        var scripts = MissionFiles.List(slotPaths, scanned, path => StateOf(inputs, path));

        var analyses = new Dictionary<string, MissionAnalysis>(VfsPath.Comparer);
        foreach (var script in scripts.Where(s => s.State != MissionScriptState.Missing))
        {
            if (Analyze(inputs, script) is { } analysis)
                analyses[script.Path] = analysis;
        }
        var shipFiles = NpcShipFiles.Find(inputs.DataPath, folder => inputs.EditView.GetDirectories(folder),
            analyses.Values.Select(a => (a.Script.Path, (IReadOnlyList<Section>?)a.Document.Sections.Select(s => s.Section).ToList())));
        var (shipBuilds, baseShipBuilds) = ReadShipBuilds(inputs, shipFiles, analyses);
        return new MissionRunResult(inputs.Generation, scripts, analyses, shipFiles, watch.Elapsed)
        {
            StoryItems = ReadStoryItems(inputs),
            ShipBuilds = shipBuilds,
            BaseShipBuilds = baseShipBuilds,
            BaseWorld = inputs.BaseWorld,
        };
    }

    /// <summary>
    /// Analyzes one script from its sections (section 7 of the M6b design): the index, the checks with the origins, the
    /// prefab instances and the acts. The worker run and the model cache of the mission editor use the same code. The
    /// state of the script comes from the inputs, with their overlay.
    /// </summary>
    /// <param name="path">The install path of the script.</param>
    /// <param name="sections">The sections of the script, for example W of the edit session. They must not change.</param>
    /// <param name="binary">True when the script is a binary INI file.</param>
    public static MissionAnalysis AnalyzeScript(MissionInputs inputs, string path, IReadOnlyList<Section> sections,
        bool binary) => AnalyzeScript(inputs, path, sections, binary, null);

    /// <param name="baseRun">Gives the base-game run of a script that a mod changes, or null to run it now. The model
    /// cache keeps the base-game runs, because the base folder without mods does not change in a session.</param>
    internal static MissionAnalysis AnalyzeScript(MissionInputs inputs, string path, IReadOnlyList<Section> sections,
        bool binary, Func<MissionScriptInfo, BaseRun?>? baseRun)
    {
        var normal = VfsPath.Normalize(path) ?? path;
        var script = Info(inputs, normal);
        var document = MissionDocument.Of(normal, sections, binary);
        return Analyze(inputs, script, document, [], baseRun) with { HasFileBytes = false };
    }

    /// <summary>The script info of a path: the story slot, the state, the mod and the base presence.</summary>
    internal static MissionScriptInfo Info(MissionInputs inputs, string path)
    {
        var slot = inputs.Schema.StorySlots.FirstOrDefault(s =>
            MissionFiles.SlotPath(inputs.FreelancerIni, s) is { } slotPath && VfsPath.Comparer.Equals(slotPath, path));
        var (state, mod, inBase) = StateOf(inputs, path);
        return new MissionScriptInfo(path, slot, state, mod, inBase);
    }

    /// <summary>The base-game run of a script (decision D551): its index and its problems.</summary>
    internal sealed class BaseRun(MissionIndex index, IReadOnlyList<Diagnostic> problems)
    {
        private MissionPrefabResult? prefabs;

        public MissionIndex Index { get; } = index;

        public IReadOnlyList<Diagnostic> Problems { get; } = problems;

        /// <summary>The prefab instances of the base-game run, made at the first use.</summary>
        public MissionPrefabResult Prefabs => prefabs ??= MissionPrefabs.Retail.Match(Index);
    }

    /// <summary>
    /// The base-game run of a script that a mod changes: the base-game version of the script, with the names of the
    /// base game. Null when the script has no base-game version, or when no mod changes it.
    /// </summary>
    internal static BaseRun? ChangedBaseRun(MissionInputs inputs, MissionScriptInfo script)
    {
        if (script.State == MissionScriptState.BaseGame || !script.InBase || inputs.BaseView is not { } baseView ||
            Read(baseView, script.Path) is not var (_, baseDocument))
            return null;
        var index = MissionIndex.Build(baseDocument, inputs.Schema, inputs.BaseWorld ?? inputs.World, inputs.Coverage);
        return new BaseRun(index, MissionChecks.Run(index, inputs.Options));
    }

    /// <summary>The scripts of the scan of the edit view, with the scripts of the overlay: a new script of the overlay
    /// joins, and a script that the overlay deletes goes.</summary>
    private static IReadOnlyList<string> Scan(MissionInputs inputs, string missions)
    {
        var scanned = MissionFiles.Scan(inputs.EditView, missions).Where(inputs.FileExists).ToList();
        var known = scanned.ToHashSet(VfsPath.Comparer);
        foreach (var (path, file) in inputs.Overlay)
        {
            if (file.Exists && !known.Contains(path) && MissionFiles.IsScanned(path, missions) &&
                file.Sections.Any(s => MissionFiles.IsScriptSection(s.Name)))
                scanned.Add(path);
        }
        return scanned;
    }

    /// <summary>
    /// The ship builds of the edit view. Also the ship builds of the base game when a mod changes, replaces or adds a
    /// file of the ship builds (decision D551). A script that the run parsed is not parsed again.
    /// </summary>
    private static (NpcShipBuilds Edit, NpcShipBuilds? Base) ReadShipBuilds(MissionInputs inputs,
        IReadOnlyList<string> shipFiles, IReadOnlyDictionary<string, MissionAnalysis> analyses)
    {
        var edit = NpcShipBuilds.Read(inputs.EditView, shipFiles,
            path => analyses.GetValueOrDefault(path)?.Document ?? OverlayDocument(inputs, path),
            path => inputs.OverlayOf(path) is { Exists: false });
        if (inputs.BaseView is not { } baseView)
            return (edit, null);
        var changed = shipFiles.Any(path => inputs.Ledger.ModsThatChangeFile(path).Count > 0 ||
                                            inputs.Ledger.ReplacedBy(path) != null || !baseView.FileExists(path));
        return (edit, changed ? NpcShipBuilds.Read(baseView, shipFiles) : null);
    }

    /// <summary>The document of W of a file of the overlay, or null.</summary>
    private static MissionDocument? OverlayDocument(MissionInputs inputs, string path) =>
        inputs.OverlayOf(path) is { Exists: true } file ? MissionDocument.Of(path, file.Sections, file.IsBinary) : null;

    /// <summary>The news items of <c>news.ini</c> and the rumors of <c>mBases.ini</c> with a stage range. The stage
    /// names come from the story stages of the engine (section 14.6 of the M6a design).</summary>
    private static IReadOnlyList<StoryItem> ReadStoryItems(MissionInputs inputs)
    {
        var stages = StoryStages.Names.Select((name, i) => (name, i))
            .ToDictionary(p => p.name, p => p.i, StringComparer.OrdinalIgnoreCase);
        var result = new List<StoryItem>();
        void Add(Entry entry, bool rumor)
        {
            if (entry.Count >= 2 && stages.TryGetValue(MissionIndex.Text(entry[0]), out var from) &&
                stages.TryGetValue(MissionIndex.Text(entry[1]), out var to))
                result.Add(new StoryItem(from, to, rumor));
        }
        if (inputs.DataPath(@"MISSIONS\news.ini") is { } news && Read(inputs.EditView, news) is var (_, newsDocument))
        {
            foreach (var entry in newsDocument.Sections.SelectMany(s => s.Section).Where(e => MissionIndex.Is(e.Name, "rank")))
                Add(entry, false);
        }
        if (inputs.DataPath(@"MISSIONS\mBases.ini") is { } bases && Read(inputs.EditView, bases) is var (_, baseDocument))
        {
            foreach (var entry in baseDocument.Sections.SelectMany(s => s.Section)
                         .Where(e => MissionIndex.Is(e.Name, "rumor") || MissionIndex.Is(e.Name, "rumor_type2")))
                Add(entry, true);
        }
        return result;
    }

    /// <summary>Reads and checks one script, from the overlay or from the edit view. Null when the file cannot be
    /// read.</summary>
    /// <param name="baseRun">Gives the base-game run of a script that a mod changes, or null to run it now.</param>
    internal static MissionAnalysis? Analyze(MissionInputs inputs, MissionScriptInfo script,
        Func<MissionScriptInfo, BaseRun?>? baseRun = null)
    {
        if (inputs.OverlayOf(script.Path) is { } file)
        {
            return file.Exists
                ? Analyze(inputs, script, MissionDocument.Of(script.Path, file.Sections, file.IsBinary), [], baseRun)
                    with { HasFileBytes = false }
                : null;
        }
        return Read(inputs.EditView, script.Path) is var (bytes, document)
            ? Analyze(inputs, script, document, bytes, baseRun)
            : null;
    }

    /// <remarks>The base-game run of a script (decision D551) checks the base-game version of a script that a mod
    /// changes. It also checks a script that no mod changes when a mod removes or renames a name that the script names.
    /// Then the base-game run uses the names of the base game, so a problem that the mod causes shows.</remarks>
    private static MissionAnalysis Analyze(MissionInputs inputs, MissionScriptInfo script, MissionDocument document,
        byte[] bytes, Func<MissionScriptInfo, BaseRun?>? baseRun)
    {
        var index = MissionIndex.Build(document, inputs.Schema, inputs.CheckWorld, inputs.Coverage);
        var problems = MissionChecks.Run(index, inputs.Options);
        var run = baseRun != null ? baseRun(script) : ChangedBaseRun(inputs, script);
        if (run == null && script.State == MissionScriptState.BaseGame && inputs.BaseWorld is { } names &&
            NamesRemovedName(index, names))
        {
            var baseIndex = MissionIndex.Build(document, inputs.Schema, names, inputs.Coverage);
            run = new BaseRun(baseIndex, MissionChecks.Run(baseIndex, inputs.Options));
        }
        var classified = MissionOrigins.Apply(problems, run?.Problems, script, inputs.Ledger, inputs.ActiveModId,
            problem => RemovedNameOf(index, problem, inputs.BaseWorld));
        var prefabs = MissionPrefabs.Retail.Match(index);
        var shipFile = document.OfKind(MissionSectionKind.Mission).Select(s => ItemIndex.First(s.Section, "npc_ship_file"))
            .OfType<string>().Select(inputs.DataPath).OfType<string>().FirstOrDefault();
        var shipBuilds = 0;
        if (shipFile != null)
        {
            var ships = inputs.OverlayOf(shipFile) is { } overlay
                ? overlay.Exists ? MissionDocument.Of(shipFile, overlay.Sections, overlay.IsBinary) : null
                : Read(inputs.EditView, shipFile)?.Document;
            shipBuilds = ships?.Sections.Count(s => MissionIndex.Is(s.Type, "NPCShipArch")) ?? 0;
            if (ships != null && IsOwnShipFile(inputs, shipFile))
                classified = [.. classified, .. ShipFileProblems(inputs, ships)];
        }
        return new MissionAnalysis(script, bytes, document, index, classified, run?.Index)
        {
            Prefabs = prefabs,
            Acts = MissionActs.Propose(index),
            BasePrefabs = run?.Prefabs,
            ShipFile = shipFile,
            ShipBuilds = shipBuilds,
        };
    }

    /// <summary>True for the ship build file of a script: a file that is not directly in <c>DATA\MISSIONS</c>. A file
    /// directly in that folder, for example <c>npcships.ini</c>, is world data that encounters and bases use too, so its
    /// problems are not the problems of a script (review finding RB2).</summary>
    private static bool IsOwnShipFile(MissionInputs inputs, string path) =>
        !MissionFiles.IsDirectlyIn(path, inputs.DataPath("MISSIONS"));

    /// <summary>
    /// The problems of the ship build file of a script (final finding 14 of MCP1-3): each name of each ship build that
    /// the game data does not have, for example a loadout, and each word of a closed set that the game does not know.
    /// Each problem has the origin of the file, by the rules of <see cref="MissionOrigins"/>. So a problem of a build of
    /// the base game is a warning, and a problem of a build of the active mod is an error.
    /// </summary>
    internal static IReadOnlyList<Diagnostic> ShipFileProblems(MissionInputs inputs, MissionDocument ships)
    {
        var index = MissionIndex.BuildShipFile(ships, inputs.Schema, inputs.CheckWorld, inputs.Coverage);
        var problems = MissionChecks.Run(index, inputs.Options);
        if (problems.Count == 0)
            return [];
        var file = Info(inputs, ships.Path);
        IReadOnlyList<Diagnostic>? baseProblems = null;
        if (file.State != MissionScriptState.BaseGame && file.InBase && inputs.BaseView is { } baseView &&
            Read(baseView, ships.Path) is var (_, baseShips))
            baseProblems = MissionChecks.Run(MissionIndex.BuildShipFile(baseShips, inputs.Schema,
                inputs.BaseWorld ?? inputs.World, inputs.Coverage), inputs.Options);
        else if (file.State == MissionScriptState.BaseGame && inputs.BaseWorld is { } names && NamesRemovedName(index, names))
            baseProblems = MissionChecks.Run(MissionIndex.BuildShipFile(ships, inputs.Schema, names, inputs.Coverage),
                inputs.Options);
        return MissionOrigins.Apply(problems, baseProblems, file, inputs.Ledger, inputs.ActiveModId,
            problem => RemovedNameOf(index, problem, inputs.BaseWorld));
    }

    /// <summary>True when a name of the script does not resolve in the edit view because a mod removes or renames
    /// it.</summary>
    private static bool NamesRemovedName(MissionIndex index, MissionWorld baseWorld) =>
        index.Lines.SelectMany(l => l.References).Any(r => r.State == MissionReferenceState.Missing &&
                                                            baseWorld.RemovedBy(r.Kind, r.Name) != null);

    /// <summary>The mod that removes or renames a name of the line of a problem, or null.</summary>
    private static string? RemovedNameOf(MissionIndex index, Diagnostic problem, MissionWorld? baseWorld)
    {
        if (baseWorld == null || problem.Target is not { } target || problem.EntryIndex < 0 ||
            index.Document.Find(target) is not { } section)
            return null;
        return index.LinesOf(section).Where(l => l.Entry == problem.EntryIndex).SelectMany(l => l.References)
            .Where(r => r.State == MissionReferenceState.Missing)
            .Select(r => baseWorld.RemovedBy(r.Kind, r.Name)).FirstOrDefault(m => m != null);
    }

    /// <summary>The bytes and the document of a script, or null when it cannot be read or parsed.</summary>
    private static (byte[] Bytes, MissionDocument Document)? Read(FileSystem files, string path)
    {
        try
        {
            if (!files.FileExists(path))
                return null;
            var bytes = files.ReadAllBytes(path);
            return (bytes, MissionDocument.Parse(path, bytes));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The state, the mod and the base presence of a script path.</summary>
    private static (MissionScriptState State, string? ModId, bool InBase) StateOf(MissionInputs inputs, string path)
    {
        var inBase = inputs.BaseView?.FileExists(path) ?? true;
        if (!inputs.FileExists(path))
            return (MissionScriptState.Missing, null, inBase);
        if (inputs.Ledger.ReplacedBy(path) is { } replaced)
            return (MissionScriptState.Replaced, replaced, inBase);
        return inputs.Ledger.ModsThatChangeFile(path) is [.., var last]
            ? (MissionScriptState.Changed, last, inBase)
            : (MissionScriptState.BaseGame, null, inBase);
    }
}
