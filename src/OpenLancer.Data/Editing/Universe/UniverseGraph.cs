using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>
/// The files that a jump graph reads: <c>universe.ini</c>, the system files that its <c>[system]</c> sections name, the
/// solar archetype files and the gate lock files. Three sources exist: W of an edit session, the merged files of a
/// target build, and the game data without mods.
/// </summary>
internal interface IUniverseSource
{
    /// <summary>The install path of <c>universe.ini</c>, or null when <c>freelancer.ini</c> names none.</summary>
    string? UniversePath { get; }

    /// <summary>The install paths of the solar archetype files, in load order.</summary>
    IReadOnlyList<string> SolarPaths { get; }

    /// <summary>The install paths of the files of the gate locks (the role <c>groups</c>).</summary>
    IReadOnlyList<string> LockPaths { get; }

    /// <summary>The sections of a file, or null when the file does not exist or does not parse.</summary>
    /// <param name="path">An install path in the normal form of <see cref="VfsPath"/>.</param>
    IReadOnlyList<Section>? Sections(string path);
}

/// <summary>The source of a jump graph that reads a <c>freelancer.ini</c> and other files with a read function: the
/// merged files of a target build, or the game data without mods.</summary>
internal sealed class FileUniverseSource : IUniverseSource
{
    private readonly Func<string, IReadOnlyList<Section>?> read;

    /// <param name="read">Gives the sections of an install path, or null when the file does not exist or does not
    /// parse.</param>
    /// <param name="rules">The rule data that tells which <c>[Data]</c> lines name the files of a role.</param>
    public FileUniverseSource(Func<string, IReadOnlyList<Section>?> read, ListRules rules)
    {
        this.read = read;
        var freelancerIni = read(ModOverlay.FreelancerIniPath) ?? [];
        UniversePath = SystemFiles.ReadUniversePath(freelancerIni);
        SolarPaths = DataFiles.FixedPaths(freelancerIni, rules, UniverseGraph.SolarRole);
        LockPaths = DataFiles.FixedPaths(freelancerIni, rules, UniverseGraph.LockRole);
    }

    public string? UniversePath { get; }

    public IReadOnlyList<string> SolarPaths { get; }

    public IReadOnlyList<string> LockPaths { get; }

    public IReadOnlyList<Section>? Sections(string path) => read(path);
}

/// <summary>The kind of a jump connection. The <c>type</c> of the <c>[Solar]</c> archetype of the object gives
/// it.</summary>
public enum ConnectionKind
{
    /// <summary>A jump gate (<c>JUMP_GATE</c>).</summary>
    Gate,

    /// <summary>A jump hole (<c>JUMP_HOLE</c>).</summary>
    Hole,

    /// <summary>An airlock gate (<c>AIRLOCK_GATE</c>).</summary>
    Airlock,
}

/// <summary>One system of a jump graph.</summary>
/// <param name="Nickname">The nickname, in the letter case of <c>universe.ini</c>.</param>
/// <param name="File">The install path of the system file, or null when the <c>[system]</c> names no file.</param>
/// <param name="MapPos">The place on the nav map (<c>pos</c>), or null.</param>
internal sealed record UniverseSystem(string Nickname, string? File, Vector2? MapPos);

/// <summary>One object with a <c>goto</c> line whose archetype is a jump gate, a jump hole or an airlock
/// gate.</summary>
/// <param name="System">The nickname of the system of the object, in the letter case of <c>universe.ini</c>.</param>
/// <param name="Object">The nickname of the object.</param>
/// <param name="TargetSystem">Value 0 of <c>goto</c>, as the file writes it.</param>
/// <param name="ExitObject">Value 1 of <c>goto</c>, or an empty text.</param>
/// <param name="Archetype">The nickname of the solar archetype.</param>
/// <param name="PlayerLocked">True when a <c>locked_gate</c> line names the object.</param>
/// <param name="NpcLocked">True when an <c>npc_locked_gate</c> line names the object.</param>
internal sealed record UniverseConnection(string System, string Object, string TargetSystem, string ExitObject,
    ConnectionKind Kind, string Archetype, bool PlayerLocked, bool NpcLocked);

/// <summary>
/// The jump graph of a universe: the systems of <c>universe.ini</c> and the jump connections of their files. The route
/// tables (<see cref="RouteTables"/>), the universe checks and the agent tools read it.
/// </summary>
/// <remarks>
/// <para>Retail facts: the game reads the <c>[system]</c> sections in file order. The id of a system is the FLHash of
/// its nickname, and the game drops a second system with the same id. Each object with <c>goto</c> becomes one
/// connection of its system. The type of its solar archetype gives the kind: <c>JUMP_GATE</c>, <c>JUMP_HOLE</c> or
/// <c>AIRLOCK_GATE</c>. An object of another type is no connection. Each <c>locked_gate</c> and
/// <c>npc_locked_gate</c> line of <c>[locked_gates]</c> holds the FLHash of an object nickname.</para>
/// <para>The graph keeps the connections of each system in the order of its file. A system file that does not open
/// gives a system without connections, as in the game.</para>
/// </remarks>
internal sealed class UniverseGraph
{
    /// <summary>The role of the solar archetype files in the rule data.</summary>
    public const string SolarRole = "solar";

    /// <summary>The role of the files of the gate locks in the rule data.</summary>
    public const string LockRole = "groups";

    private readonly Dictionary<string, List<UniverseConnection>> bySystem;

    private UniverseGraph(IReadOnlyList<UniverseSystem> systems, Dictionary<string, List<UniverseConnection>> bySystem)
    {
        Systems = systems;
        this.bySystem = bySystem;
        Connections = systems.SelectMany(s => ConnectionsOf(s.Nickname)).ToList();
    }

    /// <summary>The systems, in the order of <c>universe.ini</c>.</summary>
    public IReadOnlyList<UniverseSystem> Systems { get; }

    /// <summary>The connections of all systems: the systems in the order of <c>universe.ini</c>, and the objects of
    /// each system in file order.</summary>
    public IReadOnlyList<UniverseConnection> Connections { get; }

    /// <summary>The connections of a system in the order of its file, or none for an unknown system.</summary>
    public IReadOnlyList<UniverseConnection> ConnectionsOf(string system) =>
        bySystem.TryGetValue(system, out var connections) ? connections : [];

    /// <summary>The system of a nickname, in any letter case, or null.</summary>
    public UniverseSystem? System(string nickname) =>
        Systems.FirstOrDefault(s => s.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a connection of another system goes to the system. A lock does not count, because a mission
    /// can unlock the gate.</summary>
    public bool Reached(string system) =>
        Connections.Any(c => c.TargetSystem.Equals(system, StringComparison.OrdinalIgnoreCase) &&
                             !c.System.Equals(system, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connection that a connection goes to, when that connection goes back to it, or null. Without such a
    /// partner, the player cannot jump back.</summary>
    public UniverseConnection? PartnerOf(UniverseConnection connection) =>
        ConnectionsOf(connection.TargetSystem).FirstOrDefault(c =>
            c.Object.Equals(connection.ExitObject, StringComparison.OrdinalIgnoreCase) &&
            c.TargetSystem.Equals(connection.System, StringComparison.OrdinalIgnoreCase) &&
            c.ExitObject.Equals(connection.Object, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the graph of a source.</summary>
    public static UniverseGraph Build(IUniverseSource source)
    {
        var types = SolarTypes(source);
        var player = new HashSet<uint>();
        var npc = new HashSet<uint>();
        foreach (var path in source.LockPaths)
        {
            if (source.Sections(path) is { } sections)
                GateLocks.Read(sections, player, npc);
        }

        var systems = new List<UniverseSystem>();
        var bySystem = new Dictionary<string, List<UniverseConnection>>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<uint>();
        var universe = source.UniversePath is { } universePath ? source.Sections(universePath) : null;
        foreach (var section in universe ?? [])
        {
            if (!section.Name.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                SectionIdentity.KeyOf(section) is not { } nickname || !ids.Add(FLHash.CreateID(nickname)))
                continue;
            var file = SystemFiles.FileValue(section) is { } value
                ? SystemFiles.FilePath(source.UniversePath!, value)
                : null;
            systems.Add(new UniverseSystem(nickname, file, MapPos(section)));
            var connections = new List<UniverseConnection>();
            bySystem[nickname] = connections;
            foreach (var item in (file == null ? null : source.Sections(file)) ?? [])
            {
                if (Connection(nickname, item, types, player, npc) is { } connection)
                    connections.Add(connection);
            }
        }
        return new UniverseGraph(systems, bySystem);
    }

    /// <summary>The connection of an <c>[Object]</c> with a <c>goto</c> line and an archetype of a jump type, or
    /// null.</summary>
    private static UniverseConnection? Connection(string system, Section item,
        IReadOnlyDictionary<string, ConnectionKind> types, HashSet<uint> player, HashSet<uint> npc)
    {
        if (!item.Name.Equals("Object", StringComparison.OrdinalIgnoreCase) ||
            Last(item, "goto") is not { } jump || First(item, "nickname") is not { } nickname ||
            Last(item, "archetype") is not { } archetypeLine)
            return null;
        var archetype = archetypeLine[0].ToString().Trim();
        if (!types.TryGetValue(archetype, out var kind))
            return null;
        var hash = FLHash.CreateID(nickname);
        var exit = jump.Count > 1 ? jump[1].ToString().Trim() : "";
        return new UniverseConnection(system, nickname, jump[0].ToString().Trim(), exit, kind, archetype,
            player.Contains(hash), npc.Contains(hash));
    }

    /// <summary>The jump kind of each solar archetype of a jump type, by nickname. The first archetype of a nickname
    /// counts.</summary>
    private static Dictionary<string, ConnectionKind> SolarTypes(IUniverseSource source)
    {
        var result = new Dictionary<string, ConnectionKind>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in source.SolarPaths)
        {
            foreach (var section in source.Sections(path) ?? [])
            {
                if (!section.Name.Equals("Solar", StringComparison.OrdinalIgnoreCase) ||
                    First(section, "nickname") is not { } nickname || !seen.Add(nickname))
                    continue;
                if (KindOf(Last(section, "type")?[0].ToString()) is { } kind)
                    result[nickname] = kind;
            }
        }
        return result;
    }

    /// <summary>The jump kind of a <c>[Solar] type</c> value, or null for another type.</summary>
    private static ConnectionKind? KindOf(string? type) => type?.Trim().ToUpperInvariant() switch
    {
        "JUMP_GATE" => ConnectionKind.Gate,
        "JUMP_HOLE" => ConnectionKind.Hole,
        "AIRLOCK_GATE" => ConnectionKind.Airlock,
        _ => null,
    };

    private static Vector2? MapPos(Section system) =>
        Last(system, "pos") is { Count: >= 2 } pos && pos[0].TryToSingle(out var x) && pos[1].TryToSingle(out var y)
            ? new Vector2(x, y)
            : null;

    /// <summary>The last line of a key with a value, or null.</summary>
    private static Entry? Last(Section section, string key) =>
        section.LastOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Count > 0);

    /// <summary>The trimmed value of the first line of a key with a value, or null.</summary>
    private static string? First(Section section, string key) =>
        section.FirstOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Count > 0)?[0]
            .ToString().Trim() is { Length: > 0 } value
            ? value
            : null;
}
