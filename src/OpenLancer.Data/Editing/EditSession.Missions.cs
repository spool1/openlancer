using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private MissionEditor? missions;

    /// <summary>
    /// The mission editor of the session: the only write path for mission files (section 4 of the M6b design, decision
    /// D660). The studio, the M3 rename and delete, the headless host and the agent tools use this instance.
    /// </summary>
    public MissionEditor Missions => missions ??= new MissionEditor(this, new SessionMissionWorld(this));

    // The scripts and the files of the NPC ship builds of the last run of the mission checks, or null before the first
    // run. The roles 'mission' and 'mission_ships' read them.
    private IReadOnlyList<string>? missionScripts;
    private IReadOnlyList<string>? missionShipFiles;
    private int missionGeneration;

    /// <summary>The mission scripts of the edit view for the role <c>mission</c>: the scripts of the last run of the
    /// mission checks, or the slot scripts before the first run (section 14.1 of the M6a design).</summary>
    internal IReadOnlyList<string> MissionScriptPaths => missionScripts ??= SlotScriptPaths();

    /// <summary>The files of the NPC ship builds for the role <c>mission_ships</c>: the files of the last run, or the
    /// files of the folder rules before the first run, and the ship build file that each script of W names. So a new
    /// ship build file of a script gives its names before the next run, also in a session without a worker, for example
    /// of an agent host.</summary>
    internal IReadOnlyList<string> MissionShipPaths =>
        (missionShipFiles ??= NpcShipFiles.Find(relative => DataPaths.Resolve(FreelancerIni, relative), EditViewFolders, []))
        .Concat(ShipBuildFilesOfScripts()).Distinct(VfsPath.Comparer).ToList();

    /// <summary>
    /// Makes the snapshot of a run of the mission checks (section 12.3 of the M6a design). Call it on the UI thread: it
    /// reads the data lists. The run gets a new generation, so the result of an older run is dropped. The snapshot has
    /// the overlay of W of the mission files that the session changed and did not save (section 7 of the M6b design).
    /// </summary>
    /// <param name="coverage">The OpenLancer marks of the engine.</param>
    public MissionInputs MissionInputs(IEngineCoverage coverage) =>
        (MissionWorldInputs(coverage) with { Generation = ++missionGeneration }).WithOverlay(MissionOverlay());

    /// <summary>The snapshot of the names of the game data for the mission checks, without an overlay and without a
    /// new generation. The mission editor keeps it until the names change.</summary>
    internal MissionInputs MissionWorldInputs(IEngineCoverage coverage)
    {
        var files = EditView.CreateFileSystem();
        var baseFiles = EditView.Base.OpenFileSystem();
        var names = WorldLists.ToDictionary(l => l.Kind, l => (IEnumerable<string>)ListNames(l.List));
        names[MissionValueKind.SystemObject] = Nicknames.All.Where(n => !n.IsZone).Select(n => n.Name).ToList();
        var ini = FreelancerIni;
        var world = new MissionWorld(names, relative =>
            DataPaths.Resolve(ini, relative) is { } path && files.FileExists(path), BaseSystems(), SystemObjects());
        var removed = RemovedNames(baseFiles, world);
        var targets = ActiveMod?.Manifest.Targets;
        return new MissionInputs
        {
            EditView = files,
            BaseView = baseFiles,
            FreelancerIni = ini,
            World = world,
            BaseWorld = removed.Count > 0 ? world.WithRemoved(removed) : null,
            Ledger = new LedgerView(EditView.Ledger),
            Coverage = coverage,
            ActiveModId = ActiveMod?.Id,
            Options = new MissionCheckOptions(targets?.Contains(ModTargets.OpenLancer) == true),
            Generation = missionGeneration,
        };
    }

    /// <summary>The message of the guard of the mission files (decision D660).</summary>
    internal static string MissionGuardMessage(string path) =>
        $"{path} is a mission file. Change it with the mission commands, so that the checks, the references and the " +
        "undo of the missions follow the edit.";

    /// <summary>
    /// True when the guard of the mission files covers a file (section 4.1 of the M6b design, decision D660): a mission
    /// script, and each ship build file that a <c>[Mission] npc_ship_file</c> line of a script names. The shared files
    /// directly in <c>DATA\MISSIONS</c>, for example <c>npcships.ini</c> and the pilot files, are world data (review
    /// finding RB2).
    /// </summary>
    /// <param name="path">An install path in normal form.</param>
    internal bool IsGuardedMissionFile(string path)
    {
        if (!SectionKeys.IsMissionFile(path))
            return false;
        if (OpenDocument(path) is { HoldsScript: true })
            return true;
        if (DataPath("MISSIONS") is { } missions && VfsPath.Normalize(System.IO.Path.GetDirectoryName(path) ?? "") is { } folder &&
            VfsPath.Comparer.Equals(folder, missions))
            return false;
        return ShipBuildFilesOfScripts().Contains(path);
    }

    private (int Revision, HashSet<string> Files)? shipBuildFiles;

    /// <summary>The ship build files that the <c>[Mission] npc_ship_file</c> lines of the scripts of W name (see
    /// <see cref="MissionEditor.ScriptPaths"/>).</summary>
    internal IReadOnlySet<string> ShipBuildFilesOfScripts()
    {
        if (shipBuildFiles is var (revision, known) && revision == CacheRevision)
            return known;
        var files = new HashSet<string>(VfsPath.Comparer);
        var scripts = Missions.ScriptPaths();
        foreach (var script in scripts)
        {
            if (ShipBuildFileOf(script) is { } file)
                files.Add(file);
        }
        shipBuildFiles = (CacheRevision, files);
        return files;
    }

    /// <summary>The install path of the ship build file that the <c>[Mission] npc_ship_file</c> line of W of a script
    /// names, or null.</summary>
    internal string? ShipBuildFileOf(string script) =>
        OpenDocument(script) is { } document &&
        document.Working.FirstOrDefault(s => s.Name.Equals("Mission", StringComparison.OrdinalIgnoreCase)) is { } mission &&
        ItemIndex.First(mission, "npc_ship_file") is { } relative && MissionFiles.RelativeOf(relative) is { } normal
            ? DataPath(normal)
            : null;

    /// <summary>
    /// The document of a new whole file of the active mod at an install path (decision D680). The document exists
    /// in the session, but the file exists only after a command makes it. A path that the session has a document for
    /// gives that document.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not an install path.</exception>
    internal EditDocument NewWholeDocument(string path)
    {
        var normal = Data.Editing.Commands.InstallPaths.TryParse(path) ??
                     throw new ArgumentException($"{path} is not an install path.", nameof(path));
        if (TryDocument(normal, out var known))
            return known!;
        normal = LetterCaseOfFiles(normal);
        var document = EditDocument.NewWhole(normal);
        documents[normal] = document;
        return document;
    }

    /// <summary>True when the lower layer has a file at an install path: the base data or a lower mod. A whole file of
    /// the active mod at that path takes its place.</summary>
    /// <param name="path">An install path in normal form.</param>
    internal bool LowerLayerHas(string path) =>
        lowerView.MergedDocument(path) != null || lowerView.CreateFileSystem().FileExists(path);

    /// <summary>The install paths of the mission scripts that the session opened and that exist in W.</summary>
    internal IEnumerable<string> OpenMissionScripts() =>
        documents.Values.Where(d => d is { HoldsScript: true, State.Exists: true }).Select(d => d.Path);

    /// <summary>A new number for a run of the mission checks, so the result of an older run is dropped.</summary>
    internal int NextMissionGeneration() => ++missionGeneration;

    /// <summary>
    /// W of the files under <c>DATA\MISSIONS</c> that the session changed and did not save (section 7 of the M6b
    /// design). A run of the mission checks reads them in place of the files of the edit view.
    /// </summary>
    internal IReadOnlyDictionary<string, MissionOverlayFile> MissionOverlay()
    {
        var result = new Dictionary<string, MissionOverlayFile>(VfsPath.Comparer);
        foreach (var (path, document) in documents)
        {
            if (!ReferenceEquals(document.State, document.Initial) && SectionKeys.IsMissionFile(path))
                result[path] = OverlayOf(document, document.State);
        }
        return result;
    }

    /// <summary>
    /// W of a state of a mission file for the overlay of a run, with the mods that change the file and each section: the
    /// lower mods that the lower layer records, and the active mod for each section that W changes. So a problem of an
    /// unsaved edit has the origin of the active mod (decision D551).
    /// </summary>
    internal MissionOverlayFile OverlayOf(EditDocument document, DocumentState state)
    {
        var path = document.Path;
        var active = ActiveMod?.Id;
        var ledger = lowerView.Ledger;
        var sections = new Dictionary<SectionIdentity, IReadOnlyList<string>>();
        foreach (var (id, baseId, changed) in document.Compare(state))
        {
            IReadOnlyList<string> mods = baseId != null && !document.IsWholeFile ? ledger.ModsThatChangeSection(path, baseId) : [];
            if (changed && active != null)
                mods = [.. mods.Where(m => m != active), active];
            if (mods.Count > 0)
                sections[id] = mods;
        }
        IReadOnlyList<string> fileMods = document.IsWholeFile ? [] : ledger.ModsThatChangeFile(path);
        if (active != null && document.ChangesBase(state))
            fileMods = [.. fileMods.Where(m => m != active), active];
        return new MissionOverlayFile(state.Working, document.IsBinary)
        {
            Exists = state.Exists,
            FileMods = fileMods,
            ReplacedBy = document.IsWholeFile ? active : ledger.ReplacedBy(path),
            SectionMods = sections,
            State = state,
        };
    }

    /// <summary>The kinds of the checks whose names come from a data list of the edit view, with the id of the
    /// list.</summary>
    private static readonly (MissionValueKind Kind, string List)[] WorldLists =
    [
        (MissionValueKind.System, "system"), (MissionValueKind.Base, "base"), (MissionValueKind.Faction, "faction"),
        (MissionValueKind.ShipArch, "ship"), (MissionValueKind.Loadout, "loadout"), (MissionValueKind.Pilot, "pilot"),
        (MissionValueKind.Voice, "voice"), (MissionValueKind.Good, "good"), (MissionValueKind.Equipment, "equipment"),
        (MissionValueKind.Head, "head"), (MissionValueKind.Body, "body"), (MissionValueKind.Accessory, "accessory"),
        (MissionValueKind.NpcShipArch, "npc_ship_arch"), (MissionValueKind.FormationShape, "formation_shape"),
        (MissionValueKind.CommLine, "comm_line"), (MissionValueKind.Fuse, "fuse"),
    ];

    /// <summary>The kind of the mission values whose names come from a data list, or null.</summary>
    internal static MissionValueKind? MissionKindOfList(string list) =>
        WorldLists.Where(l => l.List.Equals(list, StringComparison.OrdinalIgnoreCase)).Select(l => (MissionValueKind?)l.Kind)
            .FirstOrDefault();

    /// <summary>
    /// The names that the base folder without mods (B) has and the edit view does not have, by kind, each with the last
    /// mod that changes its file (decision D551 of the M6a design). A mod removes or renames such a name, so a mission
    /// line that names it has a problem only with the mods. Only the files that the mods change can lose a name, so a
    /// profile without mods reads no file. A file that the active mod changes, also with an edit that it did not save,
    /// has the active mod as its last mod (section 6.10 of the M6b design).
    /// </summary>
    private Dictionary<MissionValueKind, IReadOnlyDictionary<string, string>> RemovedNames(FileSystem baseFiles,
        MissionWorld world)
    {
        var result = new Dictionary<MissionValueKind, IReadOnlyDictionary<string, string>>();
        if (EditView.Mods.Count == 0)
            return result;
        var ledger = EditView.Ledger;
        var active = ActiveChangedPaths().ToHashSet(VfsPath.Comparer);
        string? ModOf(string path)
        {
            var normal = VfsPath.Normalize(path) ?? path;
            if (active.Contains(normal))
                return ActiveMod!.Id;
            return ledger.ModsThatChangeFile(normal) is [.., var last] ? last : null;
        }
        foreach (var (kind, list) in WorldLists)
        {
            if (Lists.NamesOnlyInBase(list, baseFiles, ModOf) is { Count: > 0 } only)
                result[kind] = only;
        }
        // The objects of the system files, as the nickname index reads them: the [Object] sections.
        var objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Systems.Files)
        {
            if (ModOf(path) is not { } mod || DataLists.BaseSections(baseFiles, path) is not { } sections)
                continue;
            foreach (var section in sections.Where(s => s.Name.Equals("Object", StringComparison.OrdinalIgnoreCase)))
            {
                if (ItemIndex.First(section, "nickname") is { } nickname &&
                    !world.Has(MissionValueKind.SystemObject, nickname))
                    objects.TryAdd(nickname.Trim(), mod);
            }
        }
        if (objects.Count > 0)
            result[MissionValueKind.SystemObject] = objects;
        return result;
    }

    /// <summary>The install path of a path relative to the data folder of the edit view, or null.</summary>
    public string? DataPath(string relative) => DataPaths.Resolve(FreelancerIni, relative);

    /// <summary>The nickname of the system whose file has an object or a zone of a nickname. It is null when no system
    /// file has it (section 7.3 of the M6a design). The cross-links of the Mission tab use it for an item in another
    /// system.</summary>
    public string? SystemOfObject(string nickname) =>
        Nicknames.Taken(nickname) is { } name ? Systems.NicknameOf(name.Section.Path) : null;

    /// <summary>True when the active mod has a <c>targets</c> list without OpenLancer. The editor then hides the
    /// OpenLancer marks (AGENTS.md UI rule 4).</summary>
    public bool HidesOpenLancer => ActiveMod?.Manifest.Targets is { } targets && !targets.Contains(ModTargets.OpenLancer);

    /// <summary>Takes the result of a run of the mission checks: its scripts and files of NPC ship builds feed the
    /// roles. Returns false and takes nothing when a newer run started after the run of the result.</summary>
    public bool TakeMissionRun(MissionRunResult result)
    {
        if (result.Generation != missionGeneration)
            return false;
        missionScripts = result.Scripts.Where(s => s.State != MissionScriptState.Missing).Select(s => s.Path).ToList();
        missionShipFiles = result.ShipFiles;
        return true;
    }

    /// <summary>The install paths of the slot scripts that the edit view has.</summary>
    private IReadOnlyList<string> SlotScriptPaths()
    {
        var files = EditView.CreateFileSystem();
        return MissionSchema.Retail.StorySlots.Select(s => MissionFiles.SlotPath(FreelancerIni, s))
            .OfType<string>().Where(files.FileExists).ToList();
    }

    private IReadOnlyList<string> EditViewFolders(string path) => EditView.CreateFileSystem().GetDirectories(path);

    /// <summary>The values of a list of the edit view, or none when the list does not exist.</summary>
    private List<string> ListNames(string id) => Lists.List(id)?.Entries.Select(e => e.Value).ToList() ?? [];

    /// <summary>The system of a <c>[Base]</c> section of <c>universe.ini</c>: its <c>system</c> key, or null.</summary>
    internal string? SystemOfBase(SectionRef section) => Get(section) is { } view ? SceneSections.Last(view, "system") : null;

    /// <summary>The system of each base of the list of the bases. The suggested start place of a mission play reads the
    /// system of the base that the player launched from.</summary>
    private Dictionary<string, string> BaseSystems()
    {
        var systems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Lists.List("base")?.Entries ?? [])
        {
            if (entry.Section is { } section && SystemOfBase(section) is { } system)
                systems.TryAdd(entry.Value, system);
        }
        return systems;
    }

    /// <summary>The objects of the system files of W, in file order, with their system, point, heading and jump. The
    /// suggested start place of a mission play reads the point near an object and the jump gate or hole of a
    /// jump.</summary>
    private List<WorldObject> SystemObjects()
    {
        var objects = new List<WorldObject>();
        foreach (var name in Nicknames.All.Where(n => !n.IsZone))
        {
            if (Systems.NicknameOf(name.Section.Path) is not { } system || Get(name.Section) is not { } view ||
                view.Lines("pos").LastOrDefault() is not { } pos || !PropGroupFile.TryPosition(pos.Values, out var point))
                continue;
            var rotation = view.Lines("rotate").LastOrDefault() is { } rotate &&
                           PropGroupFile.TryPosition(rotate.Values, out var degrees)
                ? MathHelper.MatrixFromEulerDegrees(degrees).ExtractRotation()
                : Quaternion.Identity;
            objects.Add(new WorldObject(name.Name, system, point, rotation, SceneSections.Last(view, "goto")));
        }
        return objects;
    }

    /// <summary>The ledger of the edit view for the worker of the mission checks. The ledger of an edit view does not
    /// change, so the worker can read it.</summary>
    private sealed class LedgerView(PatchLedger ledger) : IMissionLedger
    {
        public IReadOnlyList<string> ModsThatChangeFile(string path) =>
            ledger.ModsThatChangeFile(VfsPath.Normalize(path) ?? path);

        public IReadOnlyList<string> ModsThatChangeSection(string path, SectionIdentity section) =>
            ledger.ModsThatChangeSection(VfsPath.Normalize(path) ?? path, section);

        public string? ReplacedBy(string path) => ledger.ReplacedBy(VfsPath.Normalize(path) ?? path);
    }
}
