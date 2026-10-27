using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Data.Missions;

/// <summary>An object of a system file: its nickname, its system, its point, its heading, and the system that its jump
/// gate or jump hole leads to. <see cref="JumpTo"/> is null for an object without a jump.</summary>
public sealed record WorldObject(string Nickname, string System, Vector3 Position, Quaternion Rotation, string? JumpTo)
{
    /// <summary>The base that a ship docks with at this object (its <c>dock_with</c> key), or null.</summary>
    public string? DockWith { get; init; }
}

/// <summary>
/// The names of the game data outside the mission scripts, the places of the bases and the objects of the systems, and
/// the files of the edit view. Section 12.3 of the M6a design tells about it. The UI thread makes it from the data lists
/// of the edit view. The worker of the checks reads it only, so it holds plain sets and no session.
/// </summary>
/// <remarks>The world of a base-game run (decision D551) also holds the names that a mod removes or renames (see
/// <see cref="WithRemoved"/>). A line that names such a name has a problem only with the mods. So the problem shows also
/// in a script that no mod changes.</remarks>
public sealed class MissionWorld
{
    private readonly Dictionary<MissionValueKind, HashSet<string>> names = [];
    private readonly Dictionary<MissionValueKind, Dictionary<string, string>> removed = [];
    private readonly Func<string, bool>? fileExists;
    private readonly Places places;

    /// <summary>A world without names and files. The checks of names outside the script then find nothing.</summary>
    public static readonly MissionWorld Empty = new(new Dictionary<MissionValueKind, IEnumerable<string>>(), null);

    /// <param name="names">The names of each kind outside the script, for example the systems.</param>
    /// <param name="fileExists">Tells whether a file below the data folder exists, by its relative path, or null when
    /// the world does not know the files.</param>
    /// <param name="baseSystems">The system of each base, by the nickname of the base, or null when the world does not
    /// know them.</param>
    /// <param name="objects">The objects of the system files in file order, or null when the world does not know
    /// them.</param>
    public MissionWorld(IReadOnlyDictionary<MissionValueKind, IEnumerable<string>> names, Func<string, bool>? fileExists,
        IReadOnlyDictionary<string, string>? baseSystems = null, IEnumerable<WorldObject>? objects = null)
    {
        foreach (var (kind, values) in names)
            this.names[kind] = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        this.fileExists = fileExists;
        places = new Places(baseSystems, objects);
    }

    private MissionWorld(MissionWorld world, IReadOnlyDictionary<MissionValueKind, IReadOnlyDictionary<string, string>> removed)
    {
        names = world.names;
        fileExists = world.fileExists;
        places = world.places;
        // The names compare without letter case, as the engine hash does.
        foreach (var (kind, map) in removed.Where(p => p.Value.Count > 0))
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, mod) in map)
                names.TryAdd(name.Trim(), mod);
            this.removed[kind] = names;
        }
    }

    private MissionWorld(MissionWorld world, IReadOnlyDictionary<MissionValueKind, IEnumerable<string>> names)
        : this(names, world.fileExists) => places = world.places;

    private MissionWorld(MissionWorld world, Func<string, bool> fileExists)
    {
        names = world.names;
        removed = world.removed;
        places = world.places;
        this.fileExists = fileExists;
    }

    /// <summary>The world with another check of the files, for example a check that sees the files of W (section 7 of
    /// the M6b design). A world that does not know the files stays as it is.</summary>
    internal MissionWorld WithFiles(Func<string, bool> exists) => fileExists == null ? this : new MissionWorld(this, exists);

    /// <summary>A world with more names of some kinds, for example the names of a test that the data lists of its game
    /// data do not have.</summary>
    internal MissionWorld WithNames(IReadOnlyDictionary<MissionValueKind, IEnumerable<string>> more)
    {
        var all = names.ToDictionary(p => p.Key, p => (IEnumerable<string>)p.Value);
        foreach (var (kind, values) in more)
            all[kind] = all.TryGetValue(kind, out var known) ? known.Concat(values).ToList() : values;
        var world = new MissionWorld(this, all);
        return removed.Count == 0
            ? world
            : world.WithRemoved(removed.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value));
    }

    /// <summary>True when the world holds names that a mod removes or renames (see <see cref="WithRemoved"/>).</summary>
    public bool HasRemovedNames => removed.Count > 0;

    /// <summary>True when a mod removes or renames a name of this world (see <see cref="WithRemoved"/>).</summary>
    public bool RemovesNamesOf(string mod) =>
        removed.Values.Any(map => map.Values.Contains(mod, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The world of the base game for the base-game runs of the checks (decision D551 of the M6a design). It has the
    /// names of this world and the names that a mod removes or renames.
    /// </summary>
    /// <param name="removed">The names of each kind that the base game has and this world does not have. Each name has
    /// the last mod that changes its file.</param>
    public MissionWorld WithRemoved(IReadOnlyDictionary<MissionValueKind, IReadOnlyDictionary<string, string>> removed) =>
        new(this, removed);

    /// <summary>True when the world knows the names of a kind, or the files for a file kind.</summary>
    public bool Knows(MissionValueKind kind) =>
        MissionSchema.ScopeOf(kind) == MissionKindScope.File ? fileExists != null : names.ContainsKey(kind);

    /// <summary>True when the world has a name of a kind, without letter case.</summary>
    public bool Has(MissionValueKind kind, string name) =>
        names.TryGetValue(kind, out var set) && set.Contains(name.Trim()) || RemovedBy(kind, name) != null;

    /// <summary>
    /// The mod that removes or renames a name of the base game, or null for a name that no mod removes. A name of the
    /// label rule (<see cref="MissionValueKind.Object"/>) outside the script is an object of a system.
    /// </summary>
    public string? RemovedBy(MissionValueKind kind, string name) =>
        removed.TryGetValue(kind == MissionValueKind.Object ? MissionValueKind.SystemObject : kind, out var map)
            ? map.GetValueOrDefault(name.Trim())
            : null;

    /// <summary>The system of a base of the game data, without letter case, or null when the world does not know
    /// it.</summary>
    public string? SystemOfBase(string name) => places.BaseSystems.GetValueOrDefault(name.Trim());

    /// <summary>The object of a system file with a nickname, without letter case, or null when the world does not know
    /// it.</summary>
    public WorldObject? ObjectOf(string name) => places.Objects.GetValueOrDefault(name.Trim());

    /// <summary>The first object of a system, in file order, whose jump gate or jump hole leads to another system, or
    /// null. A ship that jumps from that other system arrives at this object.</summary>
    public WorldObject? JumpInto(string system, string from) =>
        places.Jumps.FirstOrDefault(o => o.System.Equals(system.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                         o.JumpTo!.Equals(from.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The dock object of a base: the first object of the system files, in file order, whose <c>dock_with</c>
    /// key names the base, without letter case. Null when the world does not know such an object.</summary>
    public WorldObject? DockOf(string dock) => places.Docks.GetValueOrDefault(dock.Trim());

    /// <summary>True when a file below the data folder exists. A world that does not know the files says yes.</summary>
    public bool FileExists(string relativePath) => fileExists?.Invoke(relativePath) ?? true;

    /// <summary>The number of names of a kind, for the tests.</summary>
    internal int Count(MissionValueKind kind) => names.TryGetValue(kind, out var set) ? set.Count : 0;

    /// <summary>The places of the game data. Each copy of the world shares them.</summary>
    private sealed class Places
    {
        public Places(IReadOnlyDictionary<string, string>? baseSystems, IEnumerable<WorldObject>? objects)
        {
            foreach (var (dock, system) in baseSystems ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(dock) && !string.IsNullOrWhiteSpace(system))
                    BaseSystems.TryAdd(dock.Trim(), system.Trim());
            }
            foreach (var item in objects ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Nickname) || string.IsNullOrWhiteSpace(item.System))
                    continue;
                Objects.TryAdd(item.Nickname.Trim(), item);
                if (!string.IsNullOrWhiteSpace(item.JumpTo))
                    Jumps.Add(item);
                if (!string.IsNullOrWhiteSpace(item.DockWith))
                    Docks.TryAdd(item.DockWith.Trim(), item);
            }
        }

        public Dictionary<string, string> BaseSystems { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, WorldObject> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<WorldObject> Jumps { get; } = [];

        /// <summary>The first dock object of each base, by the nickname of the base.</summary>
        public Dictionary<string, WorldObject> Docks { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
