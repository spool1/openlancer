using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Ini;

/// <summary>
/// Records, in load order, which mod changes which entries of each file. The merge uses it to report conflicts
/// between mods. The editor uses it to tell which mod gives a value.
/// </summary>
/// <remarks>
/// <para>A mod can change the lines of a name (it sets or removes them), add or remove single lines of an item list,
/// delete a section, add a section, rename a section, or replace the whole file. A rename is also a delete of the old
/// identity and an add of the new one. A whole-file override discards the earlier changes of the file. So <see cref="Record"/> and <see cref="Changes"/> see
/// only the changes after the last override. <see cref="Owner"/> and <see cref="ModsThatChange"/> see the override
/// itself.</para>
/// <para>Sections compare by <see cref="SectionIdentity"/>, and names compare without letter case.</para>
/// </remarks>
internal sealed class PatchLedger
{
    private readonly Dictionary<string, FileLog> files = new(VfsPath.Comparer);

    /// <summary>
    /// Records that <paramref name="modId"/> changes the lines of a name, or deletes the section when the name is
    /// null. Returns the mod that made the same change last, after the last whole-file override, or null.
    /// </summary>
    public string? Record(string path, SectionIdentity section, string? entryName, string modId)
    {
        var log = Log(path);
        var previous = log.Changes(section)
            .LastOrDefault(c => c.Sequence > log.LastOverride && c.IsSame(entryName))?.ModId;
        log.Add(section, new Change(log.Next(), entryName == null ? ChangeKind.Delete : ChangeKind.Lines, entryName, modId));
        return previous;
    }

    /// <summary>
    /// Records that <paramref name="modId"/> adds or removes single lines of an item list of a section
    /// (<c>ol.append</c>, <c>ol.remove_line</c>). The lines of the other mods stay, so no earlier change conflicts with
    /// it. A later change of all lines of the name conflicts with it (see <see cref="Record"/>).
    /// </summary>
    public void RecordItems(string path, SectionIdentity section, string entryName, string modId)
    {
        var log = Log(path);
        log.Add(section, new Change(log.Next(), ChangeKind.Items, entryName, modId));
    }

    /// <summary>Records that <paramref name="modId"/> adds a section.</summary>
    public void RecordAdd(string path, SectionIdentity section, string modId)
    {
        var log = Log(path);
        log.Add(section, new Change(log.Next(), ChangeKind.Add, null, modId));
    }

    /// <summary>
    /// Records that <paramref name="modId"/> gives a section a new key (<c>ol.rename</c>). The caller also records the
    /// delete of the old identity and the add of the new identity.
    /// </summary>
    public void RecordRename(string path, SectionIdentity from, SectionIdentity to, string modId)
    {
        var log = Log(path);
        log.Renames.Add((log.Next(), from, to, modId));
    }

    /// <summary>
    /// The sections of a file that <paramref name="modId"/> gives a new key, after the last whole-file override, as
    /// (identity before the mod, identity after the mod). Two renames of one section give one pair. A rename back to the
    /// old key gives no pair.
    /// </summary>
    public IReadOnlyList<(SectionIdentity From, SectionIdentity To)> Renames(string path, string modId)
    {
        if (!files.TryGetValue(path, out var log))
            return [];
        // The identity after each rename, to the identity before the first rename of the mod.
        var first = new Dictionary<SectionIdentity, SectionIdentity>();
        foreach (var (_, from, to, _) in log.Renames.Where(r => r.Sequence > log.LastOverride && r.ModId == modId))
        {
            var before = first.Remove(from, out var earlier) ? earlier : from;
            first[to] = before;
        }
        return first.Where(p => !p.Key.Equals(p.Value)).Select(p => (p.Value, p.Key)).ToList();
    }

    /// <summary>Records that <paramref name="modId"/> replaces the whole file, which discards the earlier changes.</summary>
    public void RecordFile(string path, string modId)
    {
        var log = Log(path);
        log.Overrides.Add((log.Next(), modId));
    }

    /// <summary>
    /// The changes of a file that <paramref name="modId"/> made last, after the last whole-file override, as (section,
    /// entry name). A null name is a section that the mod deleted. Added sections are not in the list.
    /// </summary>
    public IEnumerable<(SectionIdentity Section, string? Name)> Changes(string path, string modId)
    {
        if (!files.TryGetValue(path, out var log))
            yield break;
        foreach (var (section, changes) in log.Sections)
        {
            var last = changes
                .Where(c => c.Sequence > log.LastOverride && c.Kind != ChangeKind.Add)
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last());
            foreach (var change in last.Where(c => c.ModId == modId))
                yield return (section, change.Name);
        }
    }

    /// <summary>
    /// The mod whose change gives the lines of a name in the merged file, or null when the lines come from the base
    /// file. That is the last mod that changed the name, added or deleted the section, or replaced the file.
    /// </summary>
    public string? Owner(string path, SectionIdentity section, string name)
    {
        if (!files.TryGetValue(path, out var log))
            return null;
        var change = log.Changes(section).LastOrDefault(c => c.Affects(name));
        if (log.Overrides.Count > 0 && (change == null || log.LastOverride > change.Sequence))
            return log.Overrides[^1].ModId;
        return change?.ModId;
    }

    /// <summary>
    /// The mods that change the lines of a name, add or delete the section, or replace the file, in load order.
    /// </summary>
    public IReadOnlyList<string> ModsThatChange(string path, SectionIdentity section, string name)
    {
        if (!files.TryGetValue(path, out var log))
            return [];
        return log.Changes(section).Where(c => c.Affects(name)).Select(c => (c.Sequence, c.ModId))
            .Concat(log.Overrides)
            .OrderBy(c => c.Sequence)
            .Select(c => c.ModId)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The mods that change a section in any way: they set or remove its lines, add, delete or rename it, or replace the
    /// file. In load order.
    /// </summary>
    public IReadOnlyList<string> ModsThatChangeSection(string path, SectionIdentity section)
    {
        if (!files.TryGetValue(path, out var log))
            return [];
        return log.Changes(section).Select(c => (c.Sequence, c.ModId))
            .Concat(log.Overrides)
            .OrderBy(c => c.Sequence)
            .Select(c => c.ModId)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The mods that change a file in any way: they change, add, delete or rename a section of it, or replace the file.
    /// In load order.
    /// </summary>
    public IReadOnlyList<string> ModsThatChangeFile(string path)
    {
        if (!files.TryGetValue(path, out var log))
            return [];
        return log.Sections.Values.SelectMany(c => c).Select(c => (c.Sequence, c.ModId))
            .Concat(log.Overrides)
            .Concat(log.Renames.Select(r => (r.Sequence, r.ModId)))
            .OrderBy(c => c.Sequence)
            .Select(c => c.ModId)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The mod whose whole file gives the content of a file, or null: the mod of the last whole-file override, when no
    /// mod changes a section or renames one after it (section 5.1 of the M6a design).
    /// </summary>
    public string? ReplacedBy(string path)
    {
        if (!files.TryGetValue(path, out var log) || log.Overrides.Count == 0)
            return null;
        var last = log.LastOverride;
        var later = log.Sections.Values.SelectMany(c => c).Any(c => c.Sequence > last) ||
                    log.Renames.Any(r => r.Sequence > last);
        return later ? null : log.Overrides[^1].ModId;
    }

    private FileLog Log(string path)
    {
        if (!files.TryGetValue(path, out var log))
            files[path] = log = new FileLog();
        return log;
    }

    private enum ChangeKind
    {
        /// <summary>The mod sets or removes the lines of a name.</summary>
        Lines,

        Delete,

        Add,

        /// <summary>The mod adds or removes single lines of an item list of a name.</summary>
        Items,
    }

    /// <param name="Sequence">The position of the change among all changes of the file.</param>
    /// <param name="Name">The entry name of a <see cref="ChangeKind.Lines"/> or <see cref="ChangeKind.Items"/> change,
    /// else null.</param>
    private sealed record Change(int Sequence, ChangeKind Kind, string? Name, string ModId)
    {
        /// <summary>True when a change of all lines of a name, or a delete for null, discards this change: a change of
        /// the lines of the same name, or a delete.</summary>
        public bool IsSame(string? entryName) =>
            entryName == null ? Kind == ChangeKind.Delete : Kind is (ChangeKind.Lines or ChangeKind.Items) && Is(entryName);

        /// <summary>True when this change sets the lines of the name in the merged file.</summary>
        public bool Affects(string entryName) => Kind is not (ChangeKind.Lines or ChangeKind.Items) || Is(entryName);

        private bool Is(string entryName) => string.Equals(Name, entryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The changes of one file.</summary>
    private sealed class FileLog
    {
        private int count;

        public Dictionary<SectionIdentity, List<Change>> Sections { get; } = [];

        /// <summary>The whole-file overrides, as (sequence, mod).</summary>
        public List<(int Sequence, string ModId)> Overrides { get; } = [];

        /// <summary>The renames, as (sequence, old identity, new identity, mod).</summary>
        public List<(int Sequence, SectionIdentity From, SectionIdentity To, string ModId)> Renames { get; } = [];

        /// <summary>The sequence of the last whole-file override, or -1.</summary>
        public int LastOverride => Overrides.Count > 0 ? Overrides[^1].Sequence : -1;

        public int Next() => count++;

        public IReadOnlyList<Change> Changes(SectionIdentity section) => Sections.GetValueOrDefault(section) ?? [];

        public void Add(SectionIdentity section, Change change)
        {
            if (!Sections.TryGetValue(section, out var list))
                Sections[section] = list = [];
            list.Add(change);
        }
    }
}
