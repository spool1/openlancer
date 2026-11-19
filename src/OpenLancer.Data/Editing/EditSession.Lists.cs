using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private SectionTable? table;
    private DataFiles? dataFiles;
    private FieldFiles? fieldFiles;
    private DataLists? lists;

    /// <summary>
    /// A number that changes at each change of W: each command, also one that throws, each step of a batch, each step
    /// and end of a transaction, the cancel of a batch, an undo, a redo, a restore of unsaved edits and a save. The
    /// caches of the lists and of the item index compare it, so a call at the same number reads no file.
    /// </summary>
    /// <remarks>It is not the public <see cref="Revision"/>. A preview changes W and puts it back, so the number must
    /// grow then too. Else a later edit could use a cache of the preview (decision D764 of the MCP1 design).</remarks>
    internal int CacheRevision { get; private set; }

    /// <summary>The shared index of the sections of the edit view (decision D121).</summary>
    internal SectionTable Table => table ??= new SectionTable(OpenDocument);

    /// <summary>The files of each role of the rule data.</summary>
    internal DataFiles DataFiles => dataFiles ??= new DataFiles(FreelancerIni, ruleData, Systems, Rooms, FieldFiles,
        () => MissionScriptPaths, () => MissionShipPaths);

    /// <summary>The rule data of the lists: the retail rules, or the rules of a test.</summary>
    private ListRules ruleData = ListRules.Retail;

    /// <summary>Gives the lists other rule data. A test uses it for a rule that the retail rules do not have, for
    /// example a rule whose delete leaves "Missing".</summary>
    internal void UseRules(ListRules rules)
    {
        ruleData = rules;
        dataFiles = null;
        lists = null;
    }

    /// <summary>The nebula and field files of the edit view, and the system files that name them (milestone M3).</summary>
    internal FieldFiles FieldFiles => fieldFiles ??= new FieldFiles(this, () => FreelancerIni);

    /// <summary>The data lists of the edit view, and the reverse index of their references (milestone M21).</summary>
    public DataLists Lists => lists ??= new DataLists(this, ruleData);

    /// <summary>
    /// The list checks of a section (layer 2 of the checks): values outside their lists, values that name an entry
    /// that the active mod removed or that the engine cannot use, required keys without a valid line, and FLHash
    /// collisions. Each problem has the section as its target and the key as its key. The list is empty when the edit
    /// view has no such section.
    /// </summary>
    public IReadOnlyList<Diagnostic> CheckLists(SectionRef target) =>
        Lists.Problems(target).Select(p => p.Diagnostic).ToList();

    /// <summary>
    /// True when the editor knows the schema of the file: an item file, <c>universe.ini</c>, a star system file or a
    /// room file (decision D135 of the M8b design). The editor reads the other files only for the lists, so the
    /// Inspector shows their sections read-only (decision D123).
    /// </summary>
    public bool KnowsSchema(string path) =>
        Items.KindOf(path) != null ||
        UniversePath is { } universe && VfsPath.Comparer.Equals(VfsPath.Normalize(path), universe) ||
        IsSystemFile(path) || IsRoomFile(path);

    /// <summary>
    /// The layer that gives an entry of a list: the active mod when it adds the section, else the last lower mod that
    /// changes the key entry or the section, else the base data. Also tells if W has the section, and if the entry is a
    /// section of an edit that is not saved.
    /// </summary>
    internal (ValueOrigin Origin, string? ModId, bool InW, bool Unsaved) EntryOrigin(FileTable source, int index,
        string keyName)
    {
        var id = source.Ids[index];
        if (source.Document is not { } document)
        {
            // A whole file of the active mod that the editor shows read-only: W has the lower data of the file.
            var inW = OpenDocument(source.Path) is { } w && w.IndexOf(id) >= 0;
            return (ValueOrigin.ActiveMod, ActiveMod?.Id, inW, false);
        }
        // A whole file of the active mod that the editor edits: each section comes from the active mod.
        if (document.IsWholeFile)
            return (ValueOrigin.ActiveMod, ActiveMod?.Id, true, !document.InInitial(id));
        // A section of B that the active mod renames gives a name of the active mod.
        if (ActiveMod != null && (!document.InBase(id) || document.IsRenamed(id)))
            return (ValueOrigin.ActiveMod, ActiveMod.Id, true, !document.InInitial(id));
        var owner = lowerView.Ledger.Owner(document.Path, id, keyName);
        return owner == null ? (ValueOrigin.Base, null, true, false) : (ValueOrigin.LowerMod, owner, true, false);
    }

    /// <summary>
    /// The documents of the INI files below the folder of the active mod that the session has (decision O2): the new
    /// files of W, the files that the session opened, and the files that W deleted. The files lists and the file check
    /// read them, so a new file is in a list before its save, and a file that W deleted is not (see
    /// <see cref="DataLists"/>). Empty without an active mod.
    /// </summary>
    /// <remarks>Each such document is a whole file (see <see cref="EditDocument.EditsWholeFile"/>). A file of the folder
    /// that the session did not open is as the edit view gives it.</remarks>
    internal IEnumerable<EditDocument> ModFolderDocuments() =>
        ActiveMod is { } mod
            ? documents.Values.Where(d => d.IsWholeFile && ContentPaths.IsOwnFile(d.Path, mod.Id))
            : [];

    /// <summary>The sections of a file as the edit view gives it, or null when it has no such INI file. For a whole
    /// file of the active mod, this is the only view of its content: W has the lower data.</summary>
    internal IReadOnlyList<Section>? EditViewSections(string path)
    {
        var fs = EditView.CreateFileSystem();
        if (!fs.FileExists(path))
            return null;
        return IniDocument.Parse(path, fs.ReadAllBytes(path)).Sections;
    }

    /// <summary>The sections of a file as the lower layer gives it, or null when the lower layer has no such INI file or
    /// the file does not parse.</summary>
    internal IReadOnlyList<Section>? LowerSections(string path)
    {
        try
        {
            return lowerView.Document(path)?.Sections;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The INI files that the active mod changes: the files that a command changed, and the files of its
    /// patches and its whole files.</summary>
    internal IReadOnlyList<string> ActiveChangedPaths()
    {
        if (ActiveMod is not { } active)
            return [];
        return IniPaths(touched.Concat(active.Patches.Keys).Concat(active.Files.Keys)).ToList();
    }

    /// <summary>
    /// The INI files that the active mod changes (see <see cref="ActiveChangedPaths"/>), then the other INI files of the
    /// open transaction or the open batch. A transaction or a batch adds its files to the files that a command changed
    /// only at its commit. So a check that runs before the commit, for example the commit checks of an agent batch,
    /// reads this list.
    /// </summary>
    internal IReadOnlyList<string> ActiveChangedPathsWithOpen()
    {
        var changed = ActiveChangedPaths();
        var open = OpenPaths;
        return ActiveMod == null || open.Count == 0 ? changed : IniPaths(changed.Concat(open)).ToList();
    }

    /// <summary>The files of the open transaction or the open batch, without the editor files of the mod. Empty when
    /// neither is open.</summary>
    private IReadOnlyCollection<string> OpenPaths =>
        (transaction?.Paths ?? batch?.Paths)?.Where(p => !ModFileDocument.IsKey(p)).ToList() ?? [];

    /// <summary>The INI files of some paths, in normal form, each one time, in the order of the paths.</summary>
    private static IEnumerable<string> IniPaths(IEnumerable<string> paths) =>
        paths.Select(p => VfsPath.Normalize(p) ?? p)
            .Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            .Distinct(VfsPath.Comparer);
}
