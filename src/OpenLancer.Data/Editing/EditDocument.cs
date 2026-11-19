using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The state of the working copy of one file: W, the pinned names and the deleted sections (see
/// <see cref="EditDocument"/>). The state cannot change, so the edit history keeps old states to undo edits.
/// </summary>
/// <param name="Working">The sections of W.</param>
/// <param name="Pins">The pinned names of each section.</param>
/// <param name="Deleted">The sections that the loaded patch or an edit deleted. A rename does not delete.</param>
internal sealed record DocumentState(
    ImmutableList<Section> Working,
    ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>> Pins,
    ImmutableHashSet<SectionIdentity> Deleted) : IFileState
{
    /// <summary>The sections of B that W has under a new key (<c>ol.rename</c>): the identity in W to the identity in
    /// B.</summary>
    public ImmutableDictionary<SectionIdentity, SectionIdentity> Renamed { get; init; } =
        ImmutableDictionary<SectionIdentity, SectionIdentity>.Empty;

    /// <summary>False when W has no such file: a new whole file of the active mod before its create, or a whole file
    /// after its delete (section 6.7 of the M6b design). Each other file exists.</summary>
    public bool Exists { get; init; } = true;

    /// <summary>The <c>ol.base</c> hash of sections of B by their identity in B. It replaces the hash of the loaded
    /// patch. An example is the hash of B now, after the user kept the lines of the section (command <c>AcceptBase</c>,
    /// decision D429).</summary>
    public ImmutableDictionary<SectionIdentity, string> BaseHashes { get; init; } =
        ImmutableDictionary<SectionIdentity, string>.Empty;

    /// <summary>
    /// True when the state has the same content as another state. The sections, their lines and values, the pins, the
    /// deletes, the renames, the base hashes and the existence are the same. A value compares by its type and its value,
    /// so a binary file keeps the type of each value.
    /// </summary>
    public bool SameAs(DocumentState other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (Exists != other.Exists || Working.Count != other.Working.Count || !Deleted.SetEquals(other.Deleted) ||
            BaseHashes.Count != other.BaseHashes.Count ||
            BaseHashes.Any(h => !other.BaseHashes.TryGetValue(h.Key, out var hash) || hash != h.Value) ||
            Renamed.Count != other.Renamed.Count ||
            Renamed.Any(r => !other.Renamed.TryGetValue(r.Key, out var b) || !b.Equals(r.Value)) || !SamePins(Pins, other.Pins))
            return false;
        for (var i = 0; i < Working.Count; i++)
        {
            if (!SameSection(Working[i], other.Working[i]))
                return false;
        }
        return true;
    }

    private static bool SamePins(ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>> a,
        ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>> b)
    {
        static IEnumerable<KeyValuePair<SectionIdentity, ImmutableHashSet<string>>> Set(
            ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>> pins) => pins.Where(p => p.Value.Count > 0);
        return Set(a).Count() == Set(b).Count() &&
               Set(a).All(p => b.TryGetValue(p.Key, out var names) && names.SetEquals(p.Value));
    }

    private static bool SameSection(Section a, Section b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            Entry x = a[i], y = b[i];
            if (ReferenceEquals(x, y))
                continue;
            if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal) || x.Count != y.Count)
                return false;
            for (var v = 0; v < x.Count; v++)
            {
                if (!ReferenceEquals(x[v], y[v]) &&
                    (x[v].GetType() != y[v].GetType() || !IniValueComparer.StrictEqual(x[v], y[v], binary: true)))
                    return false;
            }
        }
        return true;
    }
}

/// <summary>
/// One INI file in the editor: B, the document that the merge gives to the active mod's patch, and W, the working
/// copy that the edits change.
/// </summary>
/// <remarks>
/// <para>W starts as B with the active mod's patch applied. The text placeholders of the patch stay as text.</para>
/// <para>A pinned name is a name that the loaded patch sets or removes in a section of B, and that no edit has
/// touched since. The writer writes the pinned names again, also when their lines equal B. So the mod keeps its
/// values when a lower mod changes. An edit of a name removes its pin. The writer also keeps the lines of the
/// loaded patch that no edit touched: their text, the comment lines and the encoding (see
/// <see cref="PatchSectionBuilder"/>).</para>
/// <para>The document records the sections that the loaded patch or an edit deleted. When W has such a section again
/// at the end, the writer deletes it and adds it again, as the edit did.</para>
/// <para>The document also records the sections of B that W has under a new key (<c>ol.rename</c>, milestone M3). Such a
/// section is a section of B for <see cref="InBase"/> and <see cref="BaseSection"/>, and the writer writes its rename.
/// A section of W with the old key of a renamed section is a new section.</para>
/// <para>The document gives no patch, and is read-only, in these cases:</para>
/// <list type="bullet">
/// <item>The loaded patch has an error. Then some of its lines are not in W, and a save would lose them.</item>
/// <item>The active mod replaces the file with its own file, outside the files of <see cref="EditsWholeFile"/>. The
/// merge then applies the patch of the mod to that file, not to B. W is then B, and the document only shows it.</item>
/// <item>The file is <c>EXE\freelancer.ini</c>, which a patch cannot change.</item>
/// <item>The active mod has a whole mission file and a patch for the same path. The merge applies the patch to the
/// whole file, so an edit of the whole file cannot reach the game. W is the whole file with the patch, as the game
/// gets it, and the document only shows it.</item>
/// </list>
/// <para>A whole file of the active mod that the editor edits (see <see cref="EditsWholeFile"/>) is a document of the
/// kind "whole file" (section 6.7 of the M6b design). B is the file of the mod as it is on disk, and W gets the edits.
/// A save writes the whole file as text (see <see cref="ChangedWholeFile"/>), not a patch. Each section is editable,
/// also a section whose key repeats.</para>
/// <para>Edits never change a section or an entry in place. They put new sections into the list, so an old
/// <see cref="DocumentState"/> stays valid.</para>
/// </remarks>
internal sealed class EditDocument : IEditFile
{
    private static readonly ImmutableHashSet<string> NoPins = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    private readonly IniDocument b;
    private readonly PatchSource? source;

    /// <summary>The whole file of the active mod of a document of the kind "whole file", or null.</summary>
    private readonly WholeSource? whole;

    /// <summary>Why the document gives no patch, or null.</summary>
    private readonly Diagnostic? refusal;

    private DocumentState state;
    private SectionIdentity[]? workingIds;
    private Dictionary<SectionIdentity, int>? workingIndex;
    private Dictionary<SectionIdentity, int>? baseIndex;
    private KeyCounts? baseCounts;
    private KeyCounts? workingCounts;
    private PatchText? initialText;
    private WholeFileText? initialWholeText;
    private Dictionary<SectionIdentity, int>? initialIndex;

    private EditDocument(string path, IniDocument b, DocumentState state, PatchSource? source, Diagnostic? refusal,
        string? sourceHash, WholeSource? whole = null)
    {
        Path = path;
        this.b = b;
        this.state = state;
        Initial = state;
        this.source = source;
        this.refusal = refusal;
        SourceHash = sourceHash;
        this.whole = whole;
    }

    /// <summary>The file of the active mod of a whole file, and its bytes as the session read them. A new whole file
    /// has no file and no bytes.</summary>
    private sealed record WholeSource(string? File, byte[] Bytes);

    /// <summary>The install path, in <see cref="VfsPath.Normalize"/> form.</summary>
    public string Path { get; }

    public bool IsBinary => b.IsBinary;

    /// <summary>The sections of B. They never change.</summary>
    public IReadOnlyList<Section> Base => b.Sections;

    /// <summary>The sections of W.</summary>
    public IReadOnlyList<Section> Working => state.Working;

    /// <summary>The state of W now. <see cref="Restore"/> puts back a state that this property gave.</summary>
    public DocumentState State => state;

    IFileState IEditFile.State => state;

    /// <summary>The state when the document opened: the state that the loaded patch gives.</summary>
    public DocumentState Initial { get; }

    /// <summary>Why the document gives no patch and cannot be edited, or null.</summary>
    public string? ReadOnlyReason => refusal?.Message;

    /// <summary>The SHA-256 of the loaded patch file, or of the whole file of a document of the kind "whole file", in
    /// lowercase hex. Null when the active mod had no patch for the file.</summary>
    public string? SourceHash { get; }

    /// <summary>True for a document of the kind "whole file": B is a whole file of the active mod, and a save writes
    /// the whole file.</summary>
    public bool IsWholeFile => whole != null;

    /// <summary>
    /// True when the file is a mission script: a file under <c>DATA\MISSIONS</c> whose B or W holds a <c>[Mission]</c>
    /// or a <c>[Trigger]</c> section (decision D535). A new script of the active mod is a script too.
    /// </summary>
    public bool HoldsScript
    {
        get
        {
            if (!SectionKeys.IsMissionFile(Path))
                return false;
            baseHoldsScript ??= b.Sections.Any(s => Missions.MissionFiles.IsScriptSection(s.Name));
            return baseHoldsScript.Value || state.Working.Any(s => Missions.MissionFiles.IsScriptSection(s.Name));
        }
    }

    private bool? baseHoldsScript;

    /// <summary>
    /// True when the editor edits a whole file of the active mod at an install path. The rule takes two kinds of file:
    /// <list type="bullet">
    /// <item>A file under <c>DATA\MISSIONS</c> (decision D680, section 6.7 of the M6b design), for example a test
    /// script, a replaced slot script or a ship build file.</item>
    /// <item>An INI file below the folder of the active mod, <c>DATA\MODS\&lt;mod id&gt;</c> (see
    /// <see cref="ContentPaths"/>), for example the file of a new star system. Two mods have two ids, so such a file
    /// is a new file of the active mod.</item>
    /// </list>
    /// The editor shows the other whole files of the active mod read-only.
    /// </summary>
    /// <remarks>The rule also takes the shared world files under <c>DATA\MISSIONS</c>, for example
    /// <c>npcships.ini</c>. They are world data, so the mission guard does not cover them (section 4.1 of the M6b
    /// design). Their edits get the checks of the edits of a patch, and their errors stop the save.</remarks>
    /// <param name="path">An install path in any form.</param>
    /// <param name="modId">The id of the active mod.</param>
    public static bool EditsWholeFile(string path, string modId) =>
        SectionKeys.IsMissionFile(path) ||
        path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && ContentPaths.IsOwnFile(path, modId);

    /// <summary>
    /// Opens a file for editing. W is B with the patch of <paramref name="activeMod"/> for this path applied.
    /// </summary>
    /// <param name="path">The install path, in <see cref="VfsPath.Normalize"/> form.</param>
    /// <param name="b">The document that the merge gives to the active mod's patch, or null when the lower layer does
    /// not have the file. Only a whole file of the active mod can open without it. The editor owns it from now
    /// on.</param>
    /// <param name="activeMod">The mod that the edits go to, or null.</param>
    /// <param name="diagnostics">Gets the problems of the active mod's patch, and why the document gives no
    /// patch.</param>
    public static EditDocument Open(string path, IniDocument? b, Mod? activeMod, List<Diagnostic> diagnostics)
    {
        if (activeMod != null && activeMod.Files.TryGetValue(path, out var file))
        {
            if (EditsWholeFile(path, activeMod.Id))
            {
                return activeMod.Patches.TryGetValue(path, out var patchOfWhole)
                    ? WholeWithPatch(path, file, patchOfWhole, b, activeMod.Id, diagnostics)
                    : OpenWhole(path, file, b, diagnostics);
            }
            return Refused(path, b!, diagnostics, Diagnostic.Error(file,
                $"The active mod '{activeMod.Id}' replaces {path} with this file. The editor cannot write a patch " +
                "for a file that the active mod replaces."));
        }
        if (b == null)
            throw new ArgumentNullException(nameof(b), $"The lower layer has no file {path}.");
        if (activeMod == null)
            return Load(path, b, "", null, null, diagnostics);
        if (VfsPath.Comparer.Equals(path, ModOverlay.FreelancerIniPath))
        {
            return Refused(path, b, diagnostics, Diagnostic.Error(path,
                $"{ModOverlay.FreelancerIniPath} cannot be patched in format v1."));
        }
        if (!activeMod.Patches.TryGetValue(path, out var patchFile))
            return Load(path, b, activeMod.Id, null, null, diagnostics);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(patchFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(path, b, diagnostics, Diagnostic.Error(patchFile, e.Message));
        }
        return Load(path, b, activeMod.Id, patchFile, bytes, diagnostics);
    }

    /// <summary>
    /// The document that the patch <paramref name="bytes"/> of the active mod gives, with the same B. The editor uses
    /// it after a save, so that W is what a reload of the saved files gives.
    /// </summary>
    /// <param name="modId">The id of the active mod.</param>
    /// <param name="patchFile">The patch file, for messages.</param>
    /// <param name="bytes">The bytes of the patch file, or null for no patch.</param>
    /// <remarks>For a document of the kind "whole file", the bytes are the whole file, and they become B.</remarks>
    public EditDocument Reopen(string modId, string patchFile, byte[]? bytes, List<Diagnostic> diagnostics) =>
        whole != null
            ? bytes != null ? Whole(Path, patchFile, bytes, b, diagnostics) : NewWhole(Path)
            : Load(Path, b, modId, patchFile, bytes, diagnostics);

    /// <summary>
    /// The document of a new whole file of the active mod at an install path (section 6.7 of the M6b design, decision
    /// D680): B has no sections, and the file does not exist until a command makes it. <c>CreateScript</c> and
    /// <see cref="EditCommand.NewFile"/> use it.
    /// </summary>
    public static EditDocument NewWhole(string path) =>
        new(path, IniDocument.Parse(path, []), new DocumentState(ImmutableList<Section>.Empty,
                ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>>.Empty, ImmutableHashSet<SectionIdentity>.Empty)
            {
                Exists = false,
            }, null, null, null, new WholeSource(null, []));

    /// <summary>The pinned names of a section.</summary>
    public IReadOnlyCollection<string> Pins(SectionIdentity id) => state.Pins.GetValueOrDefault(id, NoPins);

    public SectionIdentity IdentityAt(int index) => WorkingIds()[index];

    /// <summary>The index of a section in W, or -1.</summary>
    public int IndexOf(SectionIdentity id)
    {
        workingIndex ??= WorkingIds().Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        return workingIndex.GetValueOrDefault(id, -1);
    }

    /// <summary>True when B has the section of W with this identity, also under another key (see
    /// <see cref="BaseIdOf"/>).</summary>
    public bool InBase(SectionIdentity id) => BaseIdOf(id) != null;

    /// <summary>
    /// The identity in B of the section of W with this identity: the identity before a rename, or the identity itself.
    /// Null for a section that B does not have, also for a new section with the old key of a renamed section.
    /// </summary>
    public SectionIdentity? BaseIdOf(SectionIdentity id)
    {
        if (state.Renamed.TryGetValue(id, out var before))
            return before;
        return BaseIndexOf(id) >= 0 && !IsRenamedAway(id) ? id : null;
    }

    /// <summary>True when the section of W with this identity is a section of B under a new key.</summary>
    public bool IsRenamed(SectionIdentity id) => state.Renamed.ContainsKey(id);

    /// <summary>True when B has a section with this identity and W has that section under a new key.</summary>
    private bool IsRenamedAway(SectionIdentity id) => state.Renamed.Count > 0 && state.Renamed.ContainsValue(id);

    /// <summary>True when W had a section with this identity when the document opened or the last save reopened it, so
    /// the mod files have it.</summary>
    public bool InInitial(SectionIdentity id) => InitialIndex().ContainsKey(id);

    /// <summary>The section that W had with this identity when the document opened or the last save reopened it, or
    /// null. W shares each section that no edit changed since then, so a section of W that is not this section has an
    /// edit that the session did not save.</summary>
    public Section? InitialSection(SectionIdentity id) =>
        InitialIndex().TryGetValue(id, out var index) ? Initial.Working[index] : null;

    private Dictionary<SectionIdentity, int> InitialIndex() =>
        initialIndex ??= SectionIdentity.Identify(Initial.Working).Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);

    /// <summary>The section of B of the section of W with this identity (see <see cref="BaseIdOf"/>), or null.</summary>
    public Section? BaseSection(SectionIdentity id) =>
        BaseIdOf(id) is { } before && BaseIndexOf(before) is >= 0 and var i ? b.Sections[i] : null;

    /// <summary>
    /// Why a patch cannot change the section at <paramref name="index"/> of W, or null. A section that B does not
    /// have is new, so a patch adds it.
    /// </summary>
    public string? ReadOnlyReasonAt(int index)
    {
        if (refusal != null)
            return refusal.Message;
        // A whole file needs no patch, so a repeated key or the order of the lines does not matter (section 6.7 of the
        // M6b design).
        if (whole != null)
            return null;
        var id = IdentityAt(index);
        if (BaseIdOf(id) is not { } before)
            return null;
        var i = BaseIndexOf(before);
        baseCounts ??= new KeyCounts(b.Sections);
        workingCounts ??= new KeyCounts(state.Working);
        // W has a renamed section under its new key, so only B must give the old key once.
        return PatchWriter.NotPatchable(b.Sections[i], kept: before.Equals(id), baseCounts, workingCounts, Path) ??
               PatchWriter.OrderNotKept(b.Sections[i], Path);
    }

    /// <summary>
    /// Puts <paramref name="section"/> in place of the section at <paramref name="index"/>. When a section of B gets
    /// another identity, it counts as renamed and keeps its pins. A rename back to its identity in B ends the rename.
    /// When another section gets another identity, the old section counts as deleted.
    /// </summary>
    /// <param name="touched">The names that the edit sets or removes. They lose their pins.</param>
    public void Replace(int index, Section section, IEnumerable<string> touched)
    {
        var old = IdentityAt(index);
        var before = BaseIdOf(old);
        Change(state with { Working = state.Working.SetItem(index, section) });
        var now = IdentityAt(index);
        if (now.Equals(old))
        {
            if (state.Pins.TryGetValue(old, out var names))
                state = state with { Pins = state.Pins.SetItem(old, names.Except(touched)) };
            return;
        }
        var pins = state.Pins.Remove(old);
        if (before == null)
        {
            state = state with { Pins = pins, Deleted = state.Deleted.Add(old) };
            return;
        }
        // A rename of a section of B does not delete it (review finding R19 of the M3 design).
        if (state.Pins.TryGetValue(old, out var kept))
            pins = pins.SetItem(now, kept.Except(touched));
        var renamed = state.Renamed.Remove(old);
        if (!now.Equals(before))
            renamed = renamed.SetItem(now, before);
        state = state with { Pins = pins, Renamed = renamed };
    }

    public void Insert(int index, Section section) => Change(state with { Working = state.Working.Insert(index, section) });

    /// <summary>
    /// Puts a section back at an index, for example the inverse of a delete (section 4.8 of the M6b design). When the
    /// section has the identity and the lines of a deleted section of B, W gets the section of B again, and the
    /// section does not count as deleted any more. So the writer writes no change for it.
    /// </summary>
    public void InsertRestored(int index, Section section)
    {
        Insert(index, section);
        var id = IdentityAt(index);
        if (whole == null && state.Deleted.Contains(id) && BaseSection(id) is { } lower &&
            SectionComparer.Equivalent(lower, section, IsBinary, Path))
            Change(state with { Working = state.Working.SetItem(index, lower), Deleted = state.Deleted.Remove(id) });
    }

    /// <summary>The hash of the <c>ol.base</c> line of the loaded patch for a section of W, or null when the loaded
    /// patch has no such line for it (section 6.3 of the M6b design).</summary>
    public string? BaseHashOfPatch(SectionIdentity id) =>
        BaseIdOf(id) is { } before ? source?.BaseHash(before) : null;

    /// <summary>The <c>ol.base</c> hash that the patch gives a section of W now: the hash of
    /// <see cref="DocumentState.BaseHashes"/>, else the hash of the loaded patch, else the hash of B. Null for a section
    /// that B does not have.</summary>
    public string? WrittenBaseHash(SectionIdentity id) =>
        BaseIdOf(id) is not { } before ? null
        : state.BaseHashes.TryGetValue(before, out var hash) ? hash
        : source?.BaseHash(before) ?? (BaseSection(id) is { } lower ? SectionHash.Of(lower) : null);

    /// <summary>Gives the patch of a section of W another <c>ol.base</c> hash (command <c>AcceptBase</c>, decision
    /// D429). W does not change.</summary>
    /// <exception cref="InvalidOperationException">B does not have the section.</exception>
    public void SetBaseHash(SectionIdentity id, string hash)
    {
        var before = BaseIdOf(id) ?? throw new InvalidOperationException($"{Path}: B has no section {id}.");
        Change(state with { BaseHashes = state.BaseHashes.SetItem(before, hash) });
    }

    /// <summary>Removes <paramref name="count"/> sections from <paramref name="index"/>, and their pins. A removed
    /// section that has a new key counts as a deleted section of B under its old key.</summary>
    public void Remove(int index, int count)
    {
        var removed = WorkingIds().Skip(index).Take(count).ToList();
        var deleted = removed.Select(id => state.Renamed.GetValueOrDefault(id, id));
        Change(state with
        {
            Working = state.Working.RemoveRange(index, count),
            Pins = state.Pins.RemoveRange(removed),
            Deleted = state.Deleted.Union(deleted),
            Renamed = state.Renamed.RemoveRange(removed),
        });
    }

    /// <summary>Puts back a state that <see cref="State"/> gave, to undo or redo an edit.</summary>
    public void Restore(DocumentState restored) => Change(restored);

    /// <summary>
    /// The sections of W of a state with their identity in B, and true for each section that the active mod changes:
    /// a section that B does not have, a section that W does not share with B, and each section of a whole file. The
    /// state can be an older state of the document. The mission checks give the origin of a problem with it (section
    /// 7 of the M6b design).
    /// </summary>
    public IReadOnlyList<(SectionIdentity Id, SectionIdentity? BaseId, bool Changed)> Compare(DocumentState s)
    {
        var ids = SectionIdentity.Identify(s.Working);
        var renamedAway = s.Renamed.Values.ToHashSet();
        var result = new (SectionIdentity, SectionIdentity?, bool)[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            SectionIdentity? before = s.Renamed.TryGetValue(id, out var renamed) ? renamed
                : BaseIndexOf(id) >= 0 && !renamedAway.Contains(id) ? id
                : null;
            var changed = whole != null || before == null || !ReferenceEquals(b.Sections[BaseIndexOf(before)], s.Working[i]);
            result[i] = (id, before, changed);
        }
        return result;
    }

    /// <summary>True when W of a state differs from B in a way that the active mod causes: a whole file, a changed, new
    /// or renamed section, or a deleted section.</summary>
    public bool ChangesBase(DocumentState s) =>
        whole != null || s.Working.Count != b.Sections.Count || s.Renamed.Count > 0 ||
        s.Working.Where((section, i) => !ReferenceEquals(section, b.Sections[i])).Any();

    void IEditFile.Restore(IFileState restored) => Restore((DocumentState)restored);

    /// <summary>Writes the patch that changes B into W.</summary>
    public PatchText Write() => Write(state);

    /// <summary>
    /// The patch for W, or null when the edits have no effect on the patch: W has its initial state, or the patch has
    /// the same bytes as the patch for the initial state. Then the patch file keeps its text (rule P2).
    /// </summary>
    public PatchText? ChangedPatch()
    {
        if (state == Initial)
            return null;
        var patch = Write();
        initialText ??= Write(Initial);
        return patch.Succeeded && initialText.Succeeded && patch.ToBytes().AsSpan().SequenceEqual(initialText.ToBytes())
            ? null
            : patch;
    }

    private PatchText Write(DocumentState s) =>
        refusal == null && whole == null
            ? PatchWriter.Write(Path, b, s.Working, id => s.Pins.GetValueOrDefault(id, NoPins), source, s.Deleted,
                s.Renamed, s.BaseHashes)
            : new PatchText([], [refusal ?? Diagnostic.Error(Path, $"{Path} is a whole file of the mod, not a patch.")]);

    /// <summary>
    /// The bytes that a save writes for a document of the kind "whole file", or null when the edits do not change the
    /// file: W has its initial state, or its text equals the text of the initial state. A text file keeps its comments
    /// and the lines that do not change (see <see cref="IniTextEdit"/>). A binary file becomes canonical text.
    /// </summary>
    public WholeFileText? ChangedWholeFile()
    {
        if (whole == null || refusal != null || state == Initial)
            return null;
        // A delete of the file, and a new file that does not exist on disk yet (decision D680).
        if (!state.Exists)
            return Initial.Exists ? new WholeFileText(null, null) : null;
        if (!Initial.Exists)
            return new WholeFileText(b.WithSections(state.Working).ToText(), null);
        var text = WholeText(state);
        initialWholeText ??= WholeText(Initial);
        return text.Bytes.AsSpan().SequenceEqual(initialWholeText.Bytes!) ? null : text;
    }

    /// <summary>The state with the sections that the bytes of a whole file give, for the restore of the recovery
    /// file.</summary>
    /// <exception cref="InvalidOperationException">The document is not of the kind "whole file".</exception>
    /// <exception cref="Exception">The parser cannot read the bytes.</exception>
    public DocumentState WholeState(byte[] bytes)
    {
        if (whole == null)
            throw new InvalidOperationException($"{Path} is not a whole file of the mod.");
        return Initial with { Working = IniDocument.Parse(Path, bytes).Sections.ToImmutableList(), Exists = true };
    }

    private WholeFileText WholeText(DocumentState s)
    {
        var w = b.WithSections(s.Working);
        if (b.IsBinary)
            return new WholeFileText(w.ToText(), $"The save writes the binary file {whole!.File ?? Path} as text.");
        var result = IniTextEdit.Write(whole!.Bytes, b, w, IniSectionDiff.Compare(b.Sections, s.Working));
        return new WholeFileText(result.Bytes, result.Fallback == null
            ? null
            : $"The save writes {whole.File} as canonical text, because {result.Fallback}. Its comments go away.");
    }

    /// <summary>A whole file of the active mod, as a document of the kind "whole file".</summary>
    /// <param name="lower">The document of the lower layer, or null. A file that cannot be read gives a read-only
    /// document with it.</param>
    private static EditDocument OpenWhole(string path, string file, IniDocument? lower, List<Diagnostic> diagnostics)
    {
        byte[] bytes;
        try
        {
            bytes = SharedFile.ReadAllBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(path, lower ?? IniDocument.Parse(path, []), diagnostics, Diagnostic.Error(file, e.Message));
        }
        return Whole(path, file, bytes, lower, diagnostics);
    }

    /// <summary>
    /// A whole mission file of the active mod that the active mod also patches. The merge applies the patch to the whole
    /// file. So the document is read-only, and W is the whole file with the patch.
    /// </summary>
    private static EditDocument WholeWithPatch(string path, string file, string patchFile, IniDocument? lower,
        string modId, List<Diagnostic> diagnostics)
    {
        var whole = OpenWhole(path, file, lower, diagnostics);
        if (whole.refusal != null)
            return whole;
        var refusal = Diagnostic.Error(patchFile,
            $"The active mod '{modId}' has a whole file and a patch for {path}. The game applies the patch to the whole " +
            "file, so the editor cannot save an edit of this file. Move the lines of the patch into the whole file, " +
            "then delete the patch.");
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(patchFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refused(path, whole.b, diagnostics, refusal);
        }
        // The merge reports the problems of the patch, so only the refusal goes into the diagnostics.
        var patched = Load(path, whole.b, modId, patchFile, bytes, []);
        return Refused(path, whole.b.WithSections(patched.Working), diagnostics, refusal);
    }

    private static EditDocument Whole(string path, string file, byte[] bytes, IniDocument? lower,
        List<Diagnostic> diagnostics)
    {
        IniDocument document;
        try
        {
            document = IniDocument.Parse(path, bytes);
        }
        catch (Exception e)
        {
            return Refused(path, lower ?? IniDocument.Parse(path, []), diagnostics, Diagnostic.Error(file, e.Message));
        }
        return new EditDocument(path, document, new DocumentState(document.Sections.ToImmutableList(),
                ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>>.Empty, ImmutableHashSet<SectionIdentity>.Empty),
            null, null, ContentHash.Of(bytes), new WholeSource(file, bytes));
    }

    private static EditDocument Load(string path, IniDocument b, string modId, string? patchFile, byte[]? bytes,
        List<Diagnostic> diagnostics)
    {
        // A copy of the section list is enough: the merge never changes a section in place.
        var w = b.WithSections(b.Sections);
        var pins = ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>>.Empty;
        var deleted = ImmutableHashSet<SectionIdentity>.Empty;
        var renamed = ImmutableDictionary<SectionIdentity, SectionIdentity>.Empty;
        PatchSource? source = null;
        Diagnostic? refusal = null;
        if (bytes != null)
        {
            var count = diagnostics.Count;
            try
            {
                source = PatchSource.Parse(patchFile!, bytes, path);
            }
            catch (Exception e)
            {
                diagnostics.Add(Diagnostic.Error(patchFile!, e.Message));
            }
            if (source != null)
            {
                var ledger = new PatchLedger();
                IniPatcher.Apply(w, source.Sections, new PatchContext(modId, patchFile!, path, null, ledger,
                    diagnostics, keepPlaceholders: true));
                var changes = ledger.Changes(path, modId).ToList();
                pins = changes
                    .Where(c => c.Name != null)
                    .GroupBy(c => c.Section)
                    .ToImmutableDictionary(g => g.Key,
                        g => ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, g.Select(c => c.Name!)));
                deleted = changes.Where(c => c.Name == null).Select(c => c.Section).ToImmutableHashSet();
                // A loaded ol.rename opens as a rename, not as a delete of the old identity. A renamed section that a
                // later patch section deleted is a delete of its old identity.
                var inW = SectionIdentity.Identify(w.Sections).ToHashSet();
                var inB = SectionIdentity.Identify(b.Sections).ToHashSet();
                renamed = ledger.Renames(path, modId)
                    .Where(r => inW.Contains(r.To) && inB.Contains(r.From))
                    .ToImmutableDictionary(r => r.To, r => r.From);
                deleted = deleted.Except(renamed.Values);
            }
            if (diagnostics.Skip(count).Any(d => d.Severity == DiagnosticSeverity.Error))
                refusal = Diagnostic.Error(patchFile!, "This patch has an error, so the editor cannot apply all its " +
                                                       "lines. Correct the file, then open it again.");
        }
        return new EditDocument(path, b, new DocumentState(w.Sections.ToImmutableList(), pins, deleted) { Renamed = renamed },
            source, refusal, bytes == null ? null : ContentHash.Of(bytes));
    }

    private static EditDocument Refused(string path, IniDocument b, List<Diagnostic> diagnostics, Diagnostic refusal)
    {
        diagnostics.Add(refusal);
        return new EditDocument(path, b, new DocumentState(b.Sections.ToImmutableList(),
            ImmutableDictionary<SectionIdentity, ImmutableHashSet<string>>.Empty,
            ImmutableHashSet<SectionIdentity>.Empty), null, refusal, null);
    }

    private void Change(DocumentState changed)
    {
        if (!ReferenceEquals(changed.Working, state.Working))
        {
            workingIds = null;
            workingIndex = null;
            workingCounts = null;
        }
        state = changed;
    }

    private SectionIdentity[] WorkingIds() => workingIds ??= SectionIdentity.Identify(state.Working);

    private int BaseIndexOf(SectionIdentity id)
    {
        baseIndex ??= SectionIdentity.Identify(b.Sections).Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        return baseIndex.GetValueOrDefault(id, -1);
    }
}

/// <summary>The bytes that a save writes for a whole file of the mod.</summary>
/// <param name="Bytes">The bytes of the file, or null when the save deletes the file.</param>
/// <param name="Note">A warning for the save, or null: the save writes a binary file as text, or writes canonical text
/// and drops the comments.</param>
internal sealed record WholeFileText(byte[]? Bytes, string? Note);
