using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>A nickname of an <c>[Object]</c> or a <c>[zone]</c> of a system file, with its FLHash.</summary>
internal sealed record SceneName(string Name, uint Hash, SectionRef Section)
{
    /// <summary>True for a zone, false for an object.</summary>
    public bool IsZone => Section.Id.Type.Equals("zone", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The nicknames of the objects and zones of all system files of W, with their FLHash (decisions D240 and D265 of the M3
/// design). Retail keeps the zones of all systems in one map by the hash of the zone nickname, and the visit flags of
/// objects and zones by the hash of the nickname. So Create, Duplicate and Rename take a nickname only when no object or
/// zone of any system has it, without letter case, or has its FLHash.
/// </summary>
/// <remarks>
/// <para>The system files are the files of <c>universe.ini</c>, so a file that <c>universe.ini</c> does not name does not
/// count. The index builds one part for each file and builds a part again only when the W state of its file changes.
/// The first use opens the system files that the session did not open.</para>
/// <para>A change of a part changes only the names of the index that the part gives or gave. So a create in a large
/// system does not build the index of all systems again. A batch of creates runs in one frame of the editor (section
/// 13.4 of the MCP1 design).</para>
/// <para>A light nickname must be unique in its file only (see <see cref="LightTaken"/>).</para>
/// </remarks>
internal sealed class Nicknames(EditSession session)
{
    private readonly Dictionary<string, Part> parts = new(VfsPath.Comparer);
    private string[] files = [];
    private int revision = int.MinValue;
    private List<SceneName>? all;
    private readonly Dictionary<string, List<SceneName>> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, List<SceneName>> byHash = [];

    /// <summary>The last <see cref="Part.Id"/>.</summary>
    private long partIds;

    /// <summary>The objects and zones of all system files, in the order of the files.</summary>
    public IReadOnlyList<SceneName> All
    {
        get
        {
            Refresh();
            return all ??= files.Where(parts.ContainsKey).SelectMany(p => parts[p].Names).ToList();
        }
    }

    /// <summary>The object or zone that has the name, or a name with its FLHash, other than
    /// <paramref name="except"/>. Null when no other section has it.</summary>
    public SceneName? Taken(string name, SectionRef? except = null)
    {
        Refresh();
        var same = byName.GetValueOrDefault(name.Trim()) ?? [];
        var hashed = byHash.GetValueOrDefault(FLHash.CreateID(name.Trim())) ?? [];
        return same.Concat(hashed).FirstOrDefault(n => except == null || !n.Section.Equals(except));
    }

    /// <summary>The light of a file that has the name, without letter case, other than
    /// <paramref name="except"/>, or null.</summary>
    public SectionRef? LightTaken(string path, string name, SectionRef? except = null) =>
        session.Sections(path).FirstOrDefault(s => s.Id.Type.Equals("LightSource", StringComparison.OrdinalIgnoreCase) &&
                                                  string.Equals(s.Id.Key, name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                                  (except == null || !s.Equals(except)));

    /// <summary>
    /// The first name <c>&lt;stem&gt;_&lt;n&gt;</c> with n from 1, written with at least <paramref name="digits"/>
    /// digits, that no object or zone has and that <paramref name="reserved"/> does not hold (decisions D235 and D262).
    /// </summary>
    /// <param name="taken">Tells whether another name rule takes a name, for example a light of the file, or null.</param>
    public string FirstFree(string stem, int digits, IReadOnlySet<string>? reserved = null, Func<string, bool>? taken = null)
    {
        Func<string, bool> isTaken = taken ?? (x => Taken(x) != null);
        var format = new string('0', Math.Max(1, digits));
        for (var n = 1; ; n++)
        {
            var name = $"{stem}_{n.ToString(format, CultureInfo.InvariantCulture)}";
            var hash = FLHash.CreateID(name);
            if (isTaken(name) || reserved?.Any(r => FLHash.CreateID(r) == hash) == true)
                continue;
            return name;
        }
    }

    /// <summary>
    /// The nickname of a copy (decision D262): a nickname that ends with <c>_</c> and digits keeps its stem and takes the
    /// first free number with the same count of digits. Another nickname gets <c>_1</c>, or the next free number.
    /// </summary>
    public string CopyName(string nickname, IReadOnlySet<string>? reserved = null, Func<string, bool>? taken = null)
    {
        var at = nickname.LastIndexOf('_');
        var number = at > 0 ? nickname[(at + 1)..] : "";
        return number.Length > 0 && number.All(char.IsAsciiDigit)
            ? FirstFree(nickname[..at], number.Length, reserved, taken)
            : FirstFree(nickname, 1, reserved, taken);
    }

    /// <summary>
    /// The groups of objects or of zones whose nicknames are equal without letter case or have one FLHash, across all
    /// system files. Each group has more than one section.
    /// </summary>
    /// <param name="zones">True for the zones, false for the objects.</param>
    public IReadOnlyList<IReadOnlyList<SceneName>> Collisions(bool zones)
    {
        var names = All;
        // The Inspector asks in each frame, so the groups stay until the names change.
        if (collisions.TryGetValue(zones, out var known) && ReferenceEquals(known.All, names))
            return known.Groups;
        var result = new List<IReadOnlyList<SceneName>>();
        var seen = new HashSet<SectionRef>();
        foreach (var name in names.Where(n => n.IsZone == zones))
        {
            if (seen.Contains(name.Section))
                continue;
            var group = byName.GetValueOrDefault(name.Name)!.Concat(byHash.GetValueOrDefault(name.Hash)!)
                .Where(n => n.IsZone == zones).Distinct().ToList();
            if (group.Count < 2)
                continue;
            seen.UnionWith(group.Select(g => g.Section));
            result.Add(group);
        }
        collisions[zones] = (names, result);
        return result;
    }

    private readonly Dictionary<bool, (IReadOnlyList<SceneName> All, IReadOnlyList<IReadOnlyList<SceneName>> Groups)>
        collisions = [];

    /// <summary>
    /// The lines of a system file that name a name as an object or a zone, when no section of the file has the name
    /// (review finding R29). A new item with the name adopts these lines.
    /// </summary>
    public IReadOnlyList<ListReference> Dangling(string name, string systemPath)
    {
        var path = VfsPath.Normalize(systemPath) ?? systemPath;
        if (session.Sections(path).Any(s => (Is(s.Id.Type, "Object") || Is(s.Id.Type, "zone")) &&
                                           string.Equals(s.Id.Key, name, StringComparison.OrdinalIgnoreCase)))
            return [];
        return session.Lists.ReferencesTo("object_of_system", name, path)
            .Concat(session.Lists.ReferencesTo("zone_of_system", name, path))
            .ToList();
    }

    /// <summary>
    /// Builds again the part of each system file whose W state changed. A part that changed gives the names of the
    /// index that it gives or gave. Only these names get their sections again, in the order of the files. A new list of
    /// files, or a change of more than one part, builds the whole index again.
    /// </summary>
    private void Refresh()
    {
        if (revision == session.CacheRevision)
            return;
        revision = session.CacheRevision;
        var now = session.Systems.Files;
        var changed = new List<(Part? Old, Part? New)>();
        foreach (var path in now)
        {
            if (session.Table.File(path) is not { } table)
                continue;
            if (parts.TryGetValue(path, out var part) && ReferenceEquals(part.Table, table))
                continue;
            var next = new Part(table, part, ++partIds);
            parts[path] = next;
            changed.Add((part, next));
        }
        foreach (var gone in parts.Keys.Where(p => !now.Contains(p, VfsPath.Comparer)).ToList())
        {
            changed.Add((parts[gone], null));
            parts.Remove(gone);
        }
        var sameFiles = now.SequenceEqual(files, VfsPath.Comparer);
        if (changed.Count == 0 && sameFiles)
            return;
        files = [.. now];
        all = null;
        if (!sameFiles || changed.Count > 1)
        {
            byName.Clear();
            byHash.Clear();
            foreach (var name in All)
            {
                Add(byName, name.Name, name);
                Add(byHash, name.Hash, name);
            }
            return;
        }
        var (old, made) = changed[0];
        var built = made != null && old != null && made.FromId == old.Id;
        IEnumerable<string> names = built ? made!.ChangedNames : Changed(old?.ByName, made?.ByName);
        IEnumerable<uint> hashes = built ? made!.ChangedHashes : Changed(old?.ByHash, made?.ByHash);
        foreach (var key in names)
            Set(byName, key, files.Where(parts.ContainsKey).SelectMany(p => parts[p].ByName.GetValueOrDefault(key) ?? []));
        foreach (var key in hashes)
            Set(byHash, key, files.Where(parts.ContainsKey).SelectMany(p => parts[p].ByHash.GetValueOrDefault(key) ?? []));
    }

    /// <summary>
    /// The object or zone that has the name, without letter case, in the order of the files, or null. Only the name
    /// counts, not its FLHash. An item reference resolves with it (see <see cref="Commands.ItemRefs"/>).
    /// </summary>
    /// <param name="zone">True for a zone, false for an object.</param>
    public SceneName? Find(string name, bool zone)
    {
        Refresh();
        return byName.GetValueOrDefault(name)?.FirstOrDefault(n => n.IsZone == zone);
    }

    /// <summary>The keys whose names differ between the old and the new map of a part.</summary>
    private static List<TKey> Changed<TKey>(Dictionary<TKey, List<SceneName>>? old,
        Dictionary<TKey, List<SceneName>>? made) where TKey : notnull
    {
        var keys = new List<TKey>();
        foreach (var (key, names) in old ?? [])
        {
            if (made?.GetValueOrDefault(key) is not { } now || !names.SequenceEqual(now))
                keys.Add(key);
        }
        keys.AddRange((made ?? []).Keys.Where(k => old?.ContainsKey(k) != true));
        return keys;
    }

    private static void Add<TKey>(Dictionary<TKey, List<SceneName>> map, TKey key, SceneName name) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(name);
    }

    /// <summary>Gives a key of the index its names, or removes the key without names.</summary>
    private static void Set<TKey>(Dictionary<TKey, List<SceneName>> map, TKey key, IEnumerable<SceneName> names)
        where TKey : notnull
    {
        var list = names.ToList();
        if (list.Count == 0)
            map.Remove(key);
        else
            map[key] = list;
    }

    /// <summary>
    /// The names of one W state of a system file. A section that the part of the older state has at the same place with
    /// the same identity keeps its name. So a create in a large system reads only the new section, and the part knows
    /// the names that changed.
    /// </summary>
    private sealed class Part
    {
        /// <param name="old">The part of an older W state of the file, or null.</param>
        /// <param name="id">A number that no other part of the index has.</param>
        public Part(FileTable table, Part? old, long id)
        {
            Table = table;
            Id = id;
            FromId = old?.Id ?? 0;
            At = new SceneName?[table.Working.Count];
            var kept = new bool[old?.At.Length ?? 0];
            foreach (var type in new[] { "Object", "zone" })
            {
                foreach (var index in table.OfType(type))
                {
                    SceneName? name;
                    if (old != null && index < old.At.Length && ReferenceEquals(old.Table.Working[index], table.Working[index]) &&
                        old.Table.Ids[index].Equals(table.Ids[index]))
                    {
                        name = old.At[index];
                        kept[index] = true;
                    }
                    else
                    {
                        name = SectionIdentity.KeyOf(table.Working[index]) is { Length: > 0 } key
                            ? new SceneName(key, FLHash.CreateID(key), table.Ref(index))
                            : null;
                        Note(name);
                    }
                    At[index] = name;
                    if (name == null)
                        continue;
                    Names.Add(name);
                    Add(ByName, name.Name, name);
                    Add(ByHash, name.Hash, name);
                }
            }
            for (var index = 0; index < kept.Length; index++)
            {
                if (!kept[index])
                    Note(old!.At[index]);
            }
        }

        public FileTable Table { get; }

        public long Id { get; }

        /// <summary>The <see cref="Id"/> of the part that this part was built from, or 0. The part keeps the number, not
        /// the old part, so the old parts do not stay in memory.</summary>
        public long FromId { get; }

        /// <summary>The name of the section at each index of the table, or null.</summary>
        public SceneName?[] At { get; }

        /// <summary>The objects and zones of the file in file order, the objects first.</summary>
        public List<SceneName> Names { get; } = [];

        public Dictionary<string, List<SceneName>> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<uint, List<SceneName>> ByHash { get; } = [];

        /// <summary>The names that differ from the part of <see cref="FromId"/>.</summary>
        public HashSet<string> ChangedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The hashes that differ from the part of <see cref="FromId"/>.</summary>
        public HashSet<uint> ChangedHashes { get; } = [];

        private void Note(SceneName? name)
        {
            if (name == null)
                return;
            ChangedNames.Add(name.Name);
            ChangedHashes.Add(name.Hash);
        }
    }

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
