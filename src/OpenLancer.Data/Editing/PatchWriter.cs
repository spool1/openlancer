using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>One section of a patch file, as lines without line ends.</summary>
internal sealed record PatchSection(SectionIdentity Id, IReadOnlyList<string> Lines);

/// <summary>
/// The patch that <see cref="PatchWriter"/> writes for one file: its sections, or the problems that stop the save of
/// the file. A file without sections needs no patch.
/// </summary>
/// <param name="Source">The loaded patch of the file, or null. The file keeps its header, its footer and its
/// encoding.</param>
internal sealed record PatchText(
    IReadOnlyList<PatchSection> Sections,
    IReadOnlyList<Diagnostic> Problems,
    PatchSource? Source = null)
{
    public bool Succeeded => Problems.Count == 0;

    /// <summary>Warnings about the patch that do not stop the save, for example a loaded section that the writer wrote
    /// with <c>ol.replace</c>.</summary>
    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];

    /// <summary>
    /// The text of the patch file: the header of the loaded patch, the sections, and the footer of the loaded patch.
    /// A blank line comes between two sections, unless the first section ends with a blank line. Each line ends with
    /// CRLF. A line with the text of the loaded patch can hold characters outside ASCII, for example in its comment.
    /// The text is empty when there are no sections.
    /// </summary>
    public string Text => Sections.Count == 0 ? "" : string.Concat(FileLines().Select(l => l + "\r\n"));

    /// <summary>
    /// The bytes of <see cref="Text"/> in the encoding of the loaded patch, after its byte order mark. So a line with
    /// the text of the loaded patch gets back the bytes that the loaded file holds. Without a loaded patch, the
    /// encoding is code page 1252, the code page of text INI files.
    /// </summary>
    public byte[] ToBytes()
    {
        if (Sections.Count == 0)
            return [];
        var encoding = Source?.Encoding ?? IniDocument.TextEncoding;
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(Text)];
    }

    private List<string> FileLines()
    {
        var lines = new List<string>(Source?.Header ?? []);
        for (var i = 0; i < Sections.Count; i++)
        {
            if (i > 0 && !PatchSource.IsBlank(lines[^1]))
                lines.Add("");
            lines.AddRange(Sections[i].Lines);
        }
        lines.AddRange(Source?.Footer ?? []);
        return lines;
    }
}

/// <summary>
/// Writes the patch that changes B, the document that the merge gives to the active mod's patch, into W, the
/// working copy of the editor.
/// </summary>
/// <remarks>
/// <para>Sections compare by <see cref="SectionIdentity"/>. The patch first has the changed and deleted sections in
/// the order of B, then the new sections in the order of W. So two saves of similar edits give similar text.</para>
/// <list type="bullet">
/// <item>Changed: the key line, then the lines of each name that changed, in the order of W, then
/// <c>ol.remove</c> with the names that W does not have. A name of a linked group adds the whole group and each line
/// between its lines (see <c>AddLinked</c>). In an order-sensitive section, a change writes all lines, also a change of
/// the order of lines with different names. A pinned name is a name that the loaded patch sets or removes, and that no
/// edit touched. The writer always writes it, so the mod keeps it when a lower mod changes.</item>
/// <item>Deleted: the key line and <c>ol.delete</c>. The merge also deletes the child sections.</item>
/// <item>New: <c>ol.new</c> and all lines. A new child of a section in B gets <c>ol.parent</c>. The children of
/// a new section follow it with no directive. A new <c>[BaseGood]</c> needs no directive.</item>
/// <item>Moved: a section of B at a position in W that the merge cannot keep is deleted and added again. So is a
/// section that an edit or the loaded patch deleted and added again with the same key, when no kept section comes
/// after it. See <c>Plan.Moved</c>.</item>
/// <item>Renamed: a section of B that W has under a new key (milestone M3). The key line of B, then
/// <c>ol.rename</c> with the new key, then the changed lines as for a changed section. The merge keeps the section at
/// its place. A section of W with the old key is then a new section. The child sections of a renamed section count as
/// kept sections.</item>
/// <item>Replaced: a changed section of a mission type whose line order counts, in a mission file (section 6.2 of the
/// M6b design). All lines of W in order, with the key line of B. <c>ol.replace</c> and <c>ol.base</c> come directly
/// after the first key line, and <c>ol.rename</c> too for a rename. Pins have no effect, because the patch holds all
/// lines.</item>
/// <item>Placed: a new or moved ship or formation section of a mission file before a kept section gets
/// <c>ol.before</c> with that section (section 6.4 of the M6b design). It does not move the sections after it. The
/// delete of a moved section of a mission file gets <c>ol.base</c>.</item>
/// <item>Base: <c>ol.base</c> keeps the hash of the loaded section when it has one, also when B changed after the
/// edit (rule P2 of section 6.3 of the M6b design). So the merge warns until the user accepts the new base. A
/// section without a loaded hash gets the hash of B.</item>
/// </list>
/// <para>A patch section finds its target by its key in the whole file. So the writer refuses to change or delete a
/// section without a key, a section whose key another section of its type also has, and a <c>[BaseGood]</c> of
/// B.</para>
/// <para>The writer keeps the lines of the loaded patch that no edit touched (see <see cref="PatchSectionBuilder"/>
/// and <see cref="PatchText"/>). It finds the loaded sections of a section by its type and key. A new section
/// without a key matches by its position among the new sections of its type. A loaded section that changes nothing
/// stays, so its comment lines stay.</para>
/// <para>Then the writer checks its own output: it applies the patch to a copy of B and compares the result with W
/// (see <see cref="SectionComparer.Equivalent"/>). When a section fails, the writer writes it again: all its lines,
/// and none with the text of the loaded patch. Then it checks again. When a section fails again, the writer reports
/// an error and gives no patch for the file, because a partial patch can lose an edit without a message.</para>
/// </remarks>
internal static class PatchWriter
{
    /// <summary>The value of <c>ol.new</c>, <c>ol.delete</c> and <c>ol.replace</c>.</summary>
    private static readonly string[] TrueValue = ["true"];

    /// <summary>The section types of a mission file whose place counts: a formation names the ships of earlier sections
    /// (rule M08 of the mission checks). A new or moved one gets <c>ol.before</c>.</summary>
    private static readonly HashSet<string> PlacedTypes = new(StringComparer.OrdinalIgnoreCase) { "MsnShip", "MsnFormation" };

    /// <param name="path">The install path of the file, for messages.</param>
    /// <param name="b">The document that the merge gives to the patch.</param>
    /// <param name="w">The sections of the working copy.</param>
    /// <param name="pins">The pinned names of each section.</param>
    /// <param name="source">The loaded patch of the file, or null.</param>
    /// <param name="deleted">The sections that the loaded patch or an edit deleted, or null for none. When W has such
    /// a section of B again, the patch can delete it and add it again.</param>
    /// <param name="renamed">The sections of B that W has under a new key: the identity in W to the identity in B, or
    /// null for none.</param>
    /// <param name="baseHashes">The <c>ol.base</c> hash of sections of B, by their identity in B, in place of the hash
    /// of the loaded patch (command <c>AcceptBase</c>), or null for none.</param>
    public static PatchText Write(string path, IniDocument b, IReadOnlyList<Section> w,
        Func<SectionIdentity, IReadOnlyCollection<string>> pins, PatchSource? source = null,
        IReadOnlySet<SectionIdentity>? deleted = null,
        IReadOnlyDictionary<SectionIdentity, SectionIdentity>? renamed = null,
        IReadOnlyDictionary<SectionIdentity, string>? baseHashes = null)
    {
        var plan = new Plan(path, b, w, pins, source, deleted ?? new HashSet<SectionIdentity>(),
            renamed ?? new Dictionary<SectionIdentity, SectionIdentity>(),
            baseHashes ?? new Dictionary<SectionIdentity, string>());
        var rewritten = new HashSet<SectionIdentity>();
        for (var pass = 1; ; pass++)
        {
            var sections = plan.Sections(rewritten);
            if (plan.Problems.Count > 0)
                return new PatchText([], plan.Problems);
            var patch = new PatchText(sections, [], source) { Notes = plan.Notes.ToList() };
            var failures = plan.Check(patch);
            if (failures.Count == 0)
                return patch;
            if (pass == 2 || failures.Any(f => !f.CanRewrite))
            {
                foreach (var failure in failures)
                    plan.Problem(failure.Id, "the patch does not give the edited section: " + failure.Message);
                return new PatchText([], plan.Problems);
            }
            rewritten.UnionWith(failures.Select(f => f.Id!));
        }
    }

    /// <summary>
    /// Adds the names that a patch must also write because of the linked groups of a section (see
    /// <see cref="SectionKeys.LinkedGroups"/>). The merge needs all names of a group, or none. It writes the lines of a
    /// patch as one block at the first line that they replace. So a line between the lines of a group moves with the
    /// group. An example is a density_restriction line between the faction_weight and the encounter lines of a zone.
    /// </summary>
    /// <remarks>
    /// A line between the lines of a group can be a line of another group. So the step repeats until no name comes.
    /// When the merge then gives the lines of a group another place among the written lines than W does, the patch also
    /// writes each group. An example is a new density_restriction line before the encounter lines of a zone. Without the
    /// group, the merge adds it after them.
    /// </remarks>
    /// <param name="names">The names to write. The step adds the new names.</param>
    /// <param name="present">True for a name that B or W has.</param>
    /// <param name="isKey">True for the key name, which a patch does not write.</param>
    private static void AddLinked(Section original, Section edited, HashSet<string> names, Func<string, bool> present,
        Func<string, bool> isKey)
    {
        var groups = SectionKeys.LinkedGroups(edited.Name).Select(g => g.Where(present).ToList()).Where(g => g.Count > 0)
            .ToList();
        if (groups.Count == 0 || names.Count == 0)
            return;
        for (var count = -1; count != names.Count;)
        {
            count = names.Count;
            foreach (var group in groups.Where(g => g.Any(names.Contains)).ToList())
            {
                names.UnionWith(group);
                names.UnionWith(Between(edited, group).Concat(Between(original, group)).Where(n => !isKey(n)));
            }
            if (names.Count == count && !MergeKeepsOrder(original, edited, names, groups))
            {
                foreach (var group in groups)
                    names.UnionWith(group);
            }
        }
    }

    /// <summary>The names of the lines between the first and the last line of a group of names (see
    /// <see cref="SectionKeys.LinkedGroups"/>), without the names of the group.</summary>
    private static IEnumerable<string> Between(Section section, IReadOnlyCollection<string> group)
    {
        bool InGroup(Entry e) => group.Contains(e.Name, StringComparer.OrdinalIgnoreCase);
        var lines = section.ToList();
        var first = lines.FindIndex(InGroup);
        var last = lines.FindLastIndex(InGroup);
        return first < 0
            ? []
            : lines.Skip(first).Take(last - first + 1).Where(e => !InGroup(e)).Select(e => e.Name);
    }

    /// <summary>
    /// True when the merge of a patch that writes <paramref name="names"/> keeps the order of W among the lines of
    /// these names and of the groups. The merge writes the lines of the names that W has as one block. The block goes
    /// to the first line of B that it replaces, or to the end. The merge removes the lines of a name that W does not
    /// have.
    /// </summary>
    private static bool MergeKeepsOrder(Section original, Section edited, HashSet<string> names,
        IEnumerable<IReadOnlyCollection<string>> groups)
    {
        var compared = new HashSet<string>(names.Concat(groups.SelectMany(g => g)), StringComparer.OrdinalIgnoreCase);
        var written = edited.Select(e => e.Name).Where(names.Contains).ToList();
        var replaced = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        var placed = false;
        foreach (var name in original.Select(e => e.Name))
        {
            if (!names.Contains(name))
                merged.Add(name);
            else if (!placed && replaced.Contains(name))
            {
                merged.AddRange(written);
                placed = true;
            }
        }
        if (!placed)
            merged.AddRange(written);
        return merged.Where(compared.Contains).SequenceEqual(edited.Select(e => e.Name).Where(compared.Contains),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Null when a patch can find a section of B in B, and in W when the section is kept. Otherwise the reason.
    /// </summary>
    /// <param name="section">The section of B.</param>
    /// <param name="kept">True when W has the section too.</param>
    /// <param name="bCounts">The key counts of B.</param>
    /// <param name="wCounts">The key counts of W.</param>
    /// <param name="path">The install path of the file.</param>
    internal static string? NotPatchable(Section section, bool kept, KeyCounts bCounts, KeyCounts wCounts, string path)
    {
        var match = SectionKeys.For(section.Name, path);
        switch (match.Kind)
        {
            case SectionMatchKind.Append:
                return $"the merge only adds [{section.Name}] sections. Add a new section.";
            case SectionMatchKind.Singleton:
                return bCounts.Types(section.Name) == 1 && (!kept || wCounts.Types(section.Name) == 1)
                    ? null
                    : $"the file has more than one [{section.Name}] section.";
            default:
                var key = SectionIdentity.KeyOf(section);
                if (key == null && match.KeyOptional && !SectionKeys.HasKeyLine(section, match))
                {
                    return bCounts.Keyless(section.Name) == 1 && (!kept || wCounts.Keyless(section.Name) == 1)
                        ? null
                        : $"the file has more than one [{section.Name}] section without a '{match.KeyEntry}' entry.";
                }
                if (key == null)
                    return $"it has no single '{match.KeyEntry}' entry with one value.";
                return bCounts.Keys(section.Name, key) == 1 && (!kept || wCounts.Keys(section.Name, key) == 1)
                    ? null
                    : $"another [{section.Name}] section also has {match.KeyEntry} = {key}.";
        }
    }

    /// <summary>
    /// Null when a patch can change a section of B and keep the order of its lines. Otherwise the reason.
    /// </summary>
    /// <remarks>
    /// <para>A change of an order-sensitive section writes all lines except the key line. The merge puts these lines
    /// as one block at the place of the first line that they replace, and the key line stays in its place. So the
    /// order stays only when the key line is the first line.</para>
    /// <para>The editor takes no edit of such a section (see <see cref="EditDocument.ReadOnlyReasonAt"/>). The writer
    /// does not refuse the section, because it also writes the pinned lines of a loaded patch. The self-check then
    /// reports the error.</para>
    /// <para>A mission section of <see cref="SectionKeys.IsReplaced"/> keeps its order, because its patch holds all
    /// lines with <c>ol.replace</c>.</para>
    /// </remarks>
    /// <param name="path">The install path of the file.</param>
    internal static string? OrderNotKept(Section section, string path)
    {
        if (SectionKeys.IsReplaced(section.Name, path) || !SectionKeys.IsOrderSensitive(section.Name) ||
            SectionKeys.For(section.Name) is not { Kind: SectionMatchKind.Keyed } match ||
            section.Count == 0 || section[0].Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase))
            return null;
        return $"the engine reads its lines in order, and a patch keeps that order only when the '{match.KeyEntry}' " +
               "line is the first line.";
    }

    /// <param name="CanRewrite">True when the writer can write the section in another way: all its lines for a
    /// section in B and W, or no line with the text of the loaded patch.</param>
    private sealed record Failure(SectionIdentity? Id, string Message, bool CanRewrite);

    private sealed class Plan
    {
        private readonly string path;
        private readonly IniDocument b;
        private readonly IReadOnlyList<Section> w;
        private readonly Func<SectionIdentity, IReadOnlyCollection<string>> pins;
        private readonly PatchSource? source;
        private readonly SectionIdentity[] bIds;
        private readonly SectionIdentity[] wIds;
        private readonly Dictionary<SectionIdentity, int> bIndex;
        private readonly Dictionary<SectionIdentity, int> wIndex;
        private readonly Lazy<KeyCounts> bCounts;
        private readonly Lazy<KeyCounts> wCounts;

        /// <summary>The <c>ol.base</c> hash of sections of B in place of the hash of the loaded patch (command
        /// <c>AcceptBase</c>).</summary>
        private readonly IReadOnlyDictionary<SectionIdentity, string> baseHashes;

        /// <summary>The sections of B that the patch deletes and adds again (see <see cref="Moved"/>), by their identity
        /// in B.</summary>
        private readonly HashSet<SectionIdentity> moved;

        /// <summary>The sections of the last <see cref="Sections"/> call with a line of the loaded patch.</summary>
        private readonly HashSet<SectionIdentity> loaded = [];

        /// <summary>The identity in B of each section of W that has another identity in B: a renamed section and its
        /// child sections.</summary>
        private readonly Dictionary<SectionIdentity, SectionIdentity> wToB = [];

        /// <summary>The other direction of <see cref="wToB"/>.</summary>
        private readonly Dictionary<SectionIdentity, SectionIdentity> bToW = [];

        /// <summary>The sections of B that get <c>ol.rename</c>, by their identity in B.</summary>
        private readonly HashSet<SectionIdentity> renamedRoots = [];

        /// <summary>The new and moved sections of W that get <c>ol.before</c>, by their index in W, with the type and
        /// the key of the kept section after them.</summary>
        private readonly Dictionary<int, (string Type, string Key)> anchors = [];

        /// <summary>The notes of the last <see cref="Sections"/> call.</summary>
        public List<Diagnostic> Notes { get; } = [];

        public Plan(string path, IniDocument b, IReadOnlyList<Section> w,
            Func<SectionIdentity, IReadOnlyCollection<string>> pins, PatchSource? source, IReadOnlySet<SectionIdentity> deleted,
            IReadOnlyDictionary<SectionIdentity, SectionIdentity> renamed, IReadOnlyDictionary<SectionIdentity, string> baseHashes)
        {
            this.path = path;
            this.baseHashes = baseHashes;
            this.b = b;
            this.w = w;
            this.pins = pins;
            this.source = source;
            bIds = SectionIdentity.Identify(b.Sections);
            wIds = SectionIdentity.Identify(w);
            bIndex = Index(bIds);
            wIndex = Index(wIds);
            bCounts = new(() => new KeyCounts(b.Sections));
            wCounts = new(() => new KeyCounts(w));
            foreach (var (wId, bId) in renamed)
            {
                if (!wIndex.ContainsKey(wId) || !bIndex.TryGetValue(bId, out var i))
                    continue;
                renamedRoots.Add(bId);
                Pair(wId, bId);
                // A child section names its parent in its identity. The merge keeps the children of a renamed section.
                for (var k = i + 1; k < b.Sections.Count && SectionKeys.IsChild(b.Sections[k].Name); k++)
                {
                    var child = bIds[k] with { Parent = wId };
                    if (wIndex.ContainsKey(child))
                        Pair(child, bIds[k]);
                }
            }
            moved = Placed(deleted);
        }

        private void Pair(SectionIdentity wId, SectionIdentity bId)
        {
            wToB[wId] = bId;
            bToW[bId] = wId;
        }

        /// <summary>The index in B of the section of W at <paramref name="j"/>, or -1 for a section that B does not have.
        /// A section of W with the old key of a renamed section is a new section.</summary>
        private int BIndexOfW(int j) =>
            wToB.TryGetValue(wIds[j], out var bId) ? bIndex[bId]
            : bToW.ContainsKey(wIds[j]) ? -1
            : bIndex.GetValueOrDefault(wIds[j], -1);

        /// <summary>The index in W of the section of B at <paramref name="i"/>, or -1 when W does not have it.</summary>
        private int WIndexOfB(int i) =>
            bToW.TryGetValue(bIds[i], out var wId) ? wIndex[wId] : wIndex.GetValueOrDefault(bIds[i], -1);

        public List<Diagnostic> Problems { get; } = [];

        private bool Binary => b.IsBinary;

        /// <summary>The patch sections. Adds a problem for each section that cannot be written.</summary>
        /// <param name="rewritten">The sections that get all their lines, and no line of the loaded patch.</param>
        public List<PatchSection> Sections(IReadOnlySet<SectionIdentity> rewritten)
        {
            Problems.Clear();
            Notes.Clear();
            loaded.Clear();
            var result = new List<PatchSection>();
            var parentDeleted = false;
            for (var i = 0; i < b.Sections.Count; i++)
            {
                var isChild = SectionKeys.IsChild(b.Sections[i].Name);
                var kept = IsKeptB(i);
                if (kept)
                {
                    var j = WIndexOfB(i);
                    Add(result, Changed(i, j, rewritten.Contains(wIds[j])));
                }
                // The merge deletes the children of a deleted section with it.
                else if (!(isChild && parentDeleted))
                    Add(result, Deleted(i));
                if (!isChild)
                    parentDeleted = !kept;
            }
            // The n-th new section with a type and key gets the n-th loaded section that adds a section with them.
            var ordinals = new Dictionary<SectionIdentity, int>();
            for (var j = 0; j < w.Count; j++)
            {
                if (IsKeptW(j))
                    continue;
                var typeAndKey = new SectionIdentity(w[j].Name, wIds[j].Key);
                var ordinal = ordinals.GetValueOrDefault(typeAndKey);
                ordinals[typeAndKey] = ordinal + 1;
                Add(result, New(j, source?.Added(w[j].Name, wIds[j].Key, ordinal), keepText: !rewritten.Contains(wIds[j])));
            }
            return result;
        }

        /// <summary>Applies the patch to a copy of B and compares the result with W.</summary>
        public List<Failure> Check(PatchText patch)
        {
            // A copy of the section list is enough: the merge never changes a section in place.
            var result = b.WithSections(b.Sections);
            var diagnostics = new List<Diagnostic>();
            IniPatcher.Apply(result, IniDocument.Parse(path, patch.ToBytes()).Sections,
                new PatchContext("editor", path, path, null, new PatchLedger(), diagnostics, keepPlaceholders: true));
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                return errors.Select(e => new Failure(null, "the merge reports: " + e.Message, false)).ToList();

            var ids = SectionIdentity.Identify(result.Sections);
            for (var k = 0; k < Math.Min(ids.Length, wIds.Length); k++)
            {
                if (!ids[k].Equals(wIds[k]))
                    return [new Failure(wIds[k], $"the merge gives {ids[k]} at its position.", false)];
            }
            if (ids.Length != wIds.Length)
                return [new Failure(null, $"the merge gives {ids.Length} sections, not {wIds.Length}.", false)];

            var failures = new List<Failure>();
            for (var k = 0; k < ids.Length; k++)
            {
                if (!SectionComparer.Equivalent(result.Sections[k], w[k], Binary, path))
                    failures.Add(new Failure(wIds[k], "the merge gives other lines.", IsKeptW(k) || loaded.Contains(wIds[k])));
            }
            return failures;
        }

        public PatchSection? Problem(SectionIdentity? id, string message)
        {
            Problems.Add(Diagnostic.Error(path, id == null ? message : $"{id}: {message}") with
            {
                Target = id == null ? null : new SectionRef(path, id),
            });
            return null;
        }

        /// <summary>A section in B and W, also under a new key in W. Null when the patch needs no lines for it.</summary>
        /// <param name="rewritten">True to write all lines, and none with the text of the loaded patch.</param>
        private PatchSection? Changed(int i, int j, bool rewritten)
        {
            var original = b.Sections[i];
            var edited = w[j];
            var id = bIds[i];
            var rename = renamedRoots.Contains(id);
            var pinned = pins(wIds[j]);
            var match = SectionKeys.For(original.Name, path);
            var keyName = match.Kind == SectionMatchKind.Keyed ? match.KeyEntry : null;
            bool IsKey(string name) => keyName != null && name.Equals(keyName, StringComparison.OrdinalIgnoreCase);
            var loadedSection = source?.Existing(original.Name, id.Key);
            // A loaded section that changes nothing stays, because no edit touched its lines.
            var keepEmpty = loadedSection != null && loadedSection.Lines.All(l => IsKey(l.Parsed.Name));
            if (!rename && ReferenceEquals(original, edited) && pinned.Count == 0 && !rewritten && !keepEmpty)
                return null;

            // The identity compares the key without letter case, but the merge does not change the key.
            var key = SectionIdentity.KeyOf(edited);
            if (rename)
            {
                if (key == null || wCounts.Value.Keys(edited.Name, key) != 1)
                    return Problem(wIds[j], $"another [{edited.Name}] section has {keyName} = {key}.");
            }
            else if (key != SectionIdentity.KeyOf(original))
                return Problem(id, $"the {keyName} '{key}' differs only in letter case. A patch cannot change that.");
            var before = SectionComparer.ByName(original);
            var after = SectionComparer.ByName(edited);

            // The names to write: the changed lines, the removed names and the pinned names.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, editedLines) in after)
            {
                if (rewritten || !before.TryGetValue(name, out var originalLines) ||
                    !SectionComparer.SameLines(originalLines, editedLines, Binary))
                    names.Add(name);
            }
            names.UnionWith(before.Keys.Where(n => !after.ContainsKey(n)));
            names.UnionWith(pinned);
            names.RemoveWhere(IsKey);
            AddLinked(original, edited, names, n => before.ContainsKey(n) || after.ContainsKey(n), IsKey);
            // The lines of each name can be the same while lines with different names change places. In an
            // order-sensitive section, that is a change too. A new key alone is a rename, not a change of the order.
            var orderSensitive = SectionKeys.IsOrderSensitive(edited.Name, path);
            if (names.Count == 0 && orderSensitive && !original.Select(e => e.Name)
                    .SequenceEqual(edited.Select(e => e.Name), StringComparer.OrdinalIgnoreCase))
                names.UnionWith(after.Keys.Where(n => !IsKey(n)));
            if (names.Count == 0 && !keepEmpty && !rename)
                return null;
            if (names.Count > 0 && SectionKeys.IsReplaced(edited.Name, path))
                return Replaced(i, j, rename ? key : null, loadedSection, rewritten);
            if (names.Count > 0 && orderSensitive)
                names.UnionWith(after.Keys.Where(n => !IsKey(n)));

            // W has a renamed section under its new key, so only B must give the old key once.
            var reason = NotPatchable(original, kept: !rename);
            if (reason != null)
                return Problem(id, $"a patch cannot {(rename ? "rename" : "change")} this section, because {reason}");
            var lines = Lines(loadedSection, inB: true, keepText: !rewritten);
            if (lines.Header(edited.Name) is { } headerError)
                return Problem(id, headerError);
            // The merge finds the section by its key line and does not change it. A section without a line of an
            // optional key has no key line.
            if (original.FirstOrDefault(e => IsKey(e.Name)) is { } keyLine && lines.Key(keyLine) is { } keyError)
                return Problem(id, keyError);
            // The merge gives the section of B the new key, and keeps its place (milestone M3).
            if (rename && (PatchFormatter.KeyError(key!, Binary) ?? lines.Directive(IniPatcher.DirectiveRename, [key!])) is
                    { } renameError)
                return Problem(wIds[j], renameError);

            // Each line replaces the line of the same name at the same position in B, which keeps its value type.
            var occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in edited.Where(e => names.Contains(e.Name)))
            {
                var n = occurrence.GetValueOrDefault(entry.Name);
                occurrence[entry.Name] = n + 1;
                var replaced = before.TryGetValue(entry.Name, out var old) && n < old.Count ? old[n] : null;
                if (lines.Entry(entry, replaced) is { } error)
                    return Problem(id, error);
            }

            // The removed names of B in file order, then the pinned names that neither B nor W has.
            var removed = original.Select(e => e.Name).Concat(pinned.Order(StringComparer.OrdinalIgnoreCase))
                .Where(n => names.Contains(n) && !after.ContainsKey(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (removed.Count > 0 && lines.Remove(removed) is { } removeError)
                return Problem(id, removeError);
            return Built(wIds[j], lines);
        }

        /// <summary>A section of B that W does not have.</summary>
        private PatchSection? Deleted(int i)
        {
            var original = b.Sections[i];
            var id = bIds[i];
            var reason = NotPatchable(original, kept: false);
            if (reason != null)
            {
                return Problem(id, WIndexOfB(i) >= 0
                    ? "the merge cannot keep this section at its position in the edit. A patch must delete it and add " +
                      "it again, but it cannot delete it, because " + reason
                    : "a patch cannot delete this section, because " + reason);
            }
            var loadedSection = source?.Existing(original.Name, id.Key);
            var lines = Lines(loadedSection, inB: true, keepText: true);
            var match = SectionKeys.For(original.Name, path);
            var keyLine = match.Kind == SectionMatchKind.Keyed
                ? original.FirstOrDefault(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase))
                : null;
            var error = lines.Header(original.Name) ??
                        (keyLine != null ? lines.Key(keyLine) : null) ??
                        lines.Directive(IniPatcher.DirectiveDelete, TrueValue);
            // The delete of a moved section holds the hash of its base, so the merge finds a base that changed after
            // the edit (section 6.4 of the M6b design).
            if (error == null && WIndexOfB(i) >= 0 && SectionKeys.IsMissionFile(path))
                error = lines.Base(BaseHash(i, loadedSection));
            return error != null ? Problem(id, error) : Built(id, lines);
        }

        /// <summary>A section of W that B does not have, or a moved section of B.</summary>
        /// <param name="loadedSection">The loaded sections that add the section, or null.</param>
        /// <param name="keepText">False to give each line new text.</param>
        private PatchSection? New(int j, SourceSection? loadedSection, bool keepText)
        {
            var section = w[j];
            var id = wIds[j];
            var lines = Lines(loadedSection, inB: false, keepText);
            if (lines.Header(section.Name) is { } headerError)
                return Problem(id, headerError);
            var match = SectionKeys.For(section.Name, path);
            string? error = null;
            if (SectionKeys.IsChild(section.Name))
            {
                var parent = j - 1;
                while (parent >= 0 && SectionKeys.IsChild(w[parent].Name))
                    parent--;
                if (parent < 0)
                    return Problem(id, "a child section needs a parent section before it.");
                // A new child of a new parent follows its parent in the patch, with no directive.
                if (IsKeptW(parent))
                {
                    var key = IniPatcher.KeyOf(w[parent]);
                    if (key == null || wCounts.Value.Parents(key) != 1)
                        return Problem(id, $"{IniPatcher.DirectiveParent} needs a key of the parent section " +
                                           $"{wIds[parent]} that no other section of the file has.");
                    error = lines.Directive(IniPatcher.DirectiveNew, TrueValue) ??
                            lines.Directive(IniPatcher.DirectiveParent, [key]);
                }
            }
            else if (match.Kind == SectionMatchKind.Keyed)
            {
                var key = SectionIdentity.KeyOf(section);
                if (key == null && match.KeyOptional && !SectionKeys.HasKeyLine(section, match))
                {
                    if (wCounts.Value.Keyless(section.Name) != 1)
                        return Problem(id, $"the file can have only one [{section.Name}] section without a " +
                                           $"'{match.KeyEntry}' entry.");
                }
                else if (key == null)
                    return Problem(id, $"a new section needs exactly one '{match.KeyEntry}' entry with one value.");
                else if (wCounts.Value.Keys(section.Name, key) != 1)
                    return Problem(id, $"another [{section.Name}] section has {match.KeyEntry} = {key}.");
                error = lines.Directive(IniPatcher.DirectiveNew, TrueValue);
                if (error == null && anchors.TryGetValue(j, out var anchor))
                    error = lines.Directive(IniPatcher.DirectiveBefore, [anchor.Type, anchor.Key]);
            }
            else if (match.Kind == SectionMatchKind.Singleton)
            {
                if (wCounts.Value.Types(section.Name) != 1)
                    return Problem(id, $"the file can have only one [{section.Name}] section.");
                error = lines.Directive(IniPatcher.DirectiveNew, TrueValue);
            }
            // The merge always adds a [BaseGood] section, so it needs no directive.

            foreach (var entry in section)
                error ??= lines.Entry(entry, null);
            return error != null ? Problem(id, error) : Built(id, lines);
        }

        private string? NotPatchable(Section section, bool kept) =>
            PatchWriter.NotPatchable(section, kept, bCounts.Value, wCounts.Value, path);

        /// <summary>
        /// The hash of the <c>ol.base</c> line of the section of B at <paramref name="i"/>. It is the hash that an edit
        /// gave the section (command <c>AcceptBase</c>), or else the hash of the first loaded section that changes or
        /// deletes it. When that section has no hash, it is the hash of the section in B now.
        /// </summary>
        /// <remarks>Rule P2 of section 6.3 of the M6b design: a save keeps the old hash, also when B changed after the
        /// edit, and also when an edit changed the section. So the merge warns until the user accepts the new base, and
        /// a save never drops the changes of a lower mod without a message.</remarks>
        private string BaseHash(int i, SourceSection? loadedSection) =>
            baseHashes.GetValueOrDefault(bIds[i])
            ?? loadedSection?.Lines
                .Where(l => l.Part == 0 && l.Parsed.Count == 1 &&
                            l.Parsed.Name.Equals(IniPatcher.DirectiveBase, StringComparison.OrdinalIgnoreCase))
                .Select(l => SectionHash.TextOf(l.Parsed[0]))
                .FirstOrDefault(h => h.Length > 0)
            ?? SectionHash.Of(b.Sections[i]);

        /// <summary>
        /// A changed section of a mission type whose line order counts, in a mission file (section 6.2 of the M6b
        /// design): all lines of W in order. The first key line is the key line of B, so the merge finds the section by
        /// its key in B. <c>ol.rename</c>, <c>ol.replace</c> and <c>ol.base</c> follow it. The n-th line of a name
        /// replaces the n-th line of the name in B, which keeps its value type.
        /// </summary>
        /// <param name="newKey">The new key of a rename, or null.</param>
        /// <param name="loadedSection">The loaded sections that change the section, or null. A loaded section of the
        /// old form, with lines by name, gives a note.</param>
        /// <param name="rewritten">True to write no line with the text of the loaded patch.</param>
        private PatchSection? Replaced(int i, int j, string? newKey, SourceSection? loadedSection, bool rewritten)
        {
            var original = b.Sections[i];
            var edited = w[j];
            var id = bIds[i];
            var reason = NotPatchable(original, kept: newKey == null);
            if (reason != null)
                return Problem(id, $"a patch cannot {(newKey != null ? "rename" : "change")} this section, because {reason}");
            var keyName = SectionKeys.For(original.Name, path).KeyEntry!;
            bool IsKey(string name) => name.Equals(keyName, StringComparison.OrdinalIgnoreCase);
            var keyLine = original.First(e => IsKey(e.Name));
            var firstKey = edited.ToList().FindIndex(e => IsKey(e.Name));
            var lines = Lines(loadedSection, inB: true, keepText: !rewritten);
            if (lines.Header(edited.Name) is { } headerError)
                return Problem(id, headerError);
            var before = SectionComparer.ByName(original);
            var occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var k = 0; k < edited.Count; k++)
            {
                var entry = edited[k];
                var n = occurrence.GetValueOrDefault(entry.Name);
                occurrence[entry.Name] = n + 1;
                string? error;
                if (k == firstKey)
                {
                    error = lines.Key(keyLine) ??
                            (newKey != null
                                ? PatchFormatter.KeyError(newKey, Binary) ??
                                  lines.Directive(IniPatcher.DirectiveRename, [newKey])
                                : null) ??
                            lines.Directive(IniPatcher.DirectiveReplace, TrueValue) ??
                            lines.Base(BaseHash(i, loadedSection));
                }
                else
                {
                    var replaced = before.TryGetValue(entry.Name, out var old) && n < old.Count ? old[n] : null;
                    error = lines.Entry(entry, replaced);
                }
                if (error != null)
                    return Problem(id, error);
            }
            if (loadedSection != null && !loadedSection.Lines.Any(l =>
                    l.Parsed.Name.Equals(IniPatcher.DirectiveReplace, StringComparison.OrdinalIgnoreCase)))
            {
                Notes.Add(Diagnostic.Warning(path, $"The editor wrote {id} of {path} with {IniPatcher.DirectiveReplace}. " +
                                                   "The game gets the same lines.") with
                {
                    Target = new SectionRef(path, wIds[j]),
                });
            }
            return Built(wIds[j], lines);
        }

        /// <summary>
        /// The moved sections of B (see <see cref="Moved"/>), and the new and moved ship and formation sections of a
        /// mission file that get <c>ol.before</c> (section 6.4 of the M6b design). Such a section goes directly before
        /// the kept section after it in W, so it does not move the sections after it. A section whose next kept section
        /// has no key that the merge can find goes to the end of the file, as another new section does.
        /// </summary>
        private HashSet<SectionIdentity> Placed(IReadOnlySet<SectionIdentity> deleted)
        {
            var placed = new HashSet<int>();
            if (SectionKeys.IsMissionFile(path))
            {
                for (var j = 0; j < w.Count; j++)
                {
                    if (PlacedTypes.Contains(w[j].Name) && !SectionKeys.IsChild(w[j].Name))
                        placed.Add(j);
                }
            }
            while (true)
            {
                var result = Moved(deleted, placed);
                anchors.Clear();
                var dropped = false;
                foreach (var j in placed.Order().ToList())
                {
                    var i = BIndexOfW(j);
                    if (i >= 0 && !result.Contains(bIds[i]))
                    {
                        // A kept section needs no place.
                        placed.Remove(j);
                        continue;
                    }
                    var next = j + 1;
                    while (next < w.Count && !(BIndexOfW(next) is >= 0 and var n && !result.Contains(bIds[n])))
                        next++;
                    if (next == w.Count)
                        continue;
                    if (AnchorOf(next) is { } anchor)
                        anchors[j] = anchor;
                    else
                    {
                        placed.Remove(j);
                        dropped = true;
                    }
                }
                if (!dropped)
                    return result;
            }
        }

        /// <summary>
        /// The type and the key that <c>ol.before</c> names for the kept section of W at an index, or null when the
        /// merge cannot find the section by them: it has no key, another section of W has its key, or a patch line
        /// cannot hold the key. The new sections come after the kept sections in the patch, so the merge has only
        /// sections of W then.
        /// </summary>
        private (string Type, string Key)? AnchorOf(int k)
        {
            var section = w[k];
            if (SectionKeys.IsChild(section.Name) || SectionKeys.For(section.Name, path).Kind != SectionMatchKind.Keyed ||
                SectionIdentity.KeyOf(section) is not { } key || wCounts.Value.Keys(section.Name, key) != 1 ||
                !PatchFormatter.TryFormatDirective(IniPatcher.DirectiveBefore, [section.Name, key], out _, out _))
                return null;
            return (section.Name, key);
        }

        /// <summary>
        /// The sections of B that the patch deletes and adds again. The merge keeps the sections of B in their order. It
        /// adds a new section at the end of the file, and a new child at the end of the children of its parent. So these
        /// are:
        /// <list type="bullet">
        /// <item>a section of B before a section that comes earlier in B</item>
        /// <item>a section of B after a new or moved section, or a child after a new or moved child of its parent</item>
        /// <item>a section of B that the loaded patch or an edit deleted, and that W has again, when no kept section
        /// comes after it (in the file, or for a child, among the children of its parent). So the patch keeps the form
        /// of the edit, and the new section can hold what a change of the old one cannot, for example the bool string
        /// <c>true</c> where B has <c>True</c>.</item>
        /// </list>
        /// A moved section takes its children with it. In a mission file, the kept sections are the longest run of
        /// sections of B in their order of B, so a move of one ship section moves only that section (section 6.4 of the
        /// M6b design).
        /// </summary>
        /// <param name="deleted">The sections that the loaded patch or an edit deleted.</param>
        /// <param name="placed">The indexes in W of the new and moved sections that get <c>ol.before</c>. They do not
        /// move the sections after them.</param>
        private HashSet<SectionIdentity> Moved(IReadOnlySet<SectionIdentity> deleted, IReadOnlySet<int> placed)
        {
            var result = SectionKeys.IsMissionFile(path) ? MovedOfMissionFile(placed) : MovedInOrder(placed);

            // From the end: the sections that an edit deleted and added again, with no kept section after them.
            var keptAfter = false;
            var keptChildAfter = false;
            var children = new List<SectionIdentity>();
            for (var j = w.Count - 1; j >= 0; j--)
            {
                var isChild = SectionKeys.IsChild(w[j].Name);
                var i = BIndexOfW(j);
                var inB = i >= 0;
                var bId = inB ? bIds[i] : null;
                var kept = inB && !result.Contains(bId!);
                if (kept && deleted.Contains(bId!) && !(isChild ? keptChildAfter : keptAfter))
                {
                    result.Add(bId!);
                    kept = false;
                    if (!isChild)
                        result.UnionWith(children);
                }
                if (isChild)
                {
                    keptChildAfter |= kept;
                    if (inB)
                        children.Add(bId!);
                }
                else
                {
                    keptAfter |= kept;
                    keptChildAfter = false;
                    children.Clear();
                }
            }
            return result;
        }

        /// <summary>The sections of B that come out of their order: each section of B before a section that comes
        /// earlier in B, and each section after a new or moved section that is not placed. A child after a new or
        /// moved child of its parent moves too.</summary>
        private HashSet<SectionIdentity> MovedInOrder(IReadOnlySet<int> placed)
        {
            var result = new HashSet<SectionIdentity>();
            var lastKept = -1;
            var afterNew = false;
            var childAfterNew = false;
            for (var j = 0; j < w.Count; j++)
            {
                var isChild = SectionKeys.IsChild(w[j].Name);
                if (!isChild)
                    childAfterNew = false;
                var i = BIndexOfW(j);
                var inB = i >= 0;
                if (inB && !afterNew && !(isChild && childAfterNew) && i > lastKept)
                {
                    lastKept = i;
                    continue;
                }
                if (inB)
                    result.Add(bIds[i]);
                if (placed.Contains(j))
                    continue;
                if (isChild)
                    childAfterNew = true;
                else
                    afterNew = true;
            }
            return result;
        }

        /// <summary>
        /// The sections of B that come out of their order in a mission file, which has no child sections. The kept
        /// sections are the longest run of sections of B in their order of B, before the first new section that is not
        /// placed. Each other section of B moves.
        /// </summary>
        private HashSet<SectionIdentity> MovedOfMissionFile(IReadOnlySet<int> placed)
        {
            var end = 0;
            while (end < w.Count && (BIndexOfW(end) >= 0 || placed.Contains(end)))
                end++;
            // From the end of the file, so that of two runs with the same length the run with the earlier sections stays,
            // as the merge keeps the first sections in order.
            var candidates = Enumerable.Range(0, end).Where(j => BIndexOfW(j) >= 0).Reverse().ToList();
            // The longest decreasing run of B indexes, with the place of the last item of each length.
            var tails = new List<int>();
            var previous = new int[candidates.Count];
            for (var k = 0; k < candidates.Count; k++)
            {
                var value = -BIndexOfW(candidates[k]);
                int low = 0, high = tails.Count;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (-BIndexOfW(candidates[tails[middle]]) < value)
                        low = middle + 1;
                    else
                        high = middle;
                }
                previous[k] = low > 0 ? tails[low - 1] : -1;
                if (low == tails.Count)
                    tails.Add(k);
                else
                    tails[low] = k;
            }
            var kept = new HashSet<int>();
            for (var k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = previous[k])
                kept.Add(candidates[k]);
            var result = new HashSet<SectionIdentity>();
            for (var j = 0; j < w.Count; j++)
            {
                if (BIndexOfW(j) is >= 0 and var i && !kept.Contains(j))
                    result.Add(bIds[i]);
            }
            return result;
        }

        /// <summary>True for the section of B at <paramref name="i"/> when W has it, also under a new key, at a
        /// position that the merge keeps.</summary>
        private bool IsKeptB(int i) => WIndexOfB(i) >= 0 && !moved.Contains(bIds[i]);

        /// <summary>True for the section of W at <paramref name="j"/> when it is a section of B, also under a new key,
        /// at a position that the merge keeps.</summary>
        private bool IsKeptW(int j) => BIndexOfW(j) is >= 0 and var i && !moved.Contains(bIds[i]);

        private PatchSectionBuilder Lines(SourceSection? loadedSection, bool inB, bool keepText) =>
            new(loadedSection, Binary, inB, keepText);

        /// <summary>The section that a builder gives. Records it when it has text of the loaded patch.</summary>
        private PatchSection Built(SectionIdentity id, PatchSectionBuilder lines)
        {
            if (lines.UsesLoadedText)
                loaded.Add(id);
            return new PatchSection(id, lines.Lines);
        }

        private static void Add(List<PatchSection> sections, PatchSection? section)
        {
            if (section != null)
                sections.Add(section);
        }

        private static Dictionary<SectionIdentity, int> Index(SectionIdentity[] ids) =>
            ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
    }
}

/// <summary>
/// How many sections of a file the merge finds for a type, for a type and key, for a type without its optional key,
/// and for an <c>ol.parent</c> key.
/// </summary>
internal sealed class KeyCounts
{
    private readonly Dictionary<string, int> types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> keyless = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> parents = new(StringComparer.OrdinalIgnoreCase);

    public KeyCounts(IReadOnlyList<Section> sections)
    {
        foreach (var section in sections)
        {
            Increment(types, section.Name);
            var match = SectionKeys.For(section.Name);
            if (match.KeyOptional && !SectionKeys.HasKeyLine(section, match))
                Increment(keyless, section.Name);
            if (match.Kind == SectionMatchKind.Keyed)
            {
                if (!keys.TryGetValue(section.Name, out var ofType))
                    keys[section.Name] = ofType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                // The merge finds a section by any of its key entries.
                var values = section
                    .Where(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase) && e.Count > 0)
                    .Select(e => e[0].ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var value in values)
                    Increment(ofType, value);
            }
            if (!SectionKeys.IsChild(section.Name) && IniPatcher.KeyOf(section) is { } parentKey)
                Increment(parents, parentKey);
        }
    }

    public int Types(string type) => types.GetValueOrDefault(type);

    public int Keys(string type, string key) => keys.TryGetValue(type, out var ofType) ? ofType.GetValueOrDefault(key) : 0;

    /// <summary>The number of sections of a type with an optional key that have no line of the key.</summary>
    public int Keyless(string type) => keyless.GetValueOrDefault(type);

    public int Parents(string key) => parents.GetValueOrDefault(key);

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}
