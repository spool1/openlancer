using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Universe;

/// <summary>A system of the Universe panel.</summary>
/// <param name="Name">The name that the game shows, or an empty text.</param>
/// <param name="Place">The place on the nav map grid, or null when the <c>[system]</c> has no <c>pos</c> line with two
/// numbers.</param>
/// <param name="JumpObjects">The jump objects of the system and the most that one system can have.</param>
/// <param name="IsNew">True for a new system that the mod has not saved.</param>
/// <param name="Connections">One text for each jump connection of the system, in the order of its file.</param>
internal sealed record UniverseMapSystem(string Nickname, string Name, Vector2? Place, JumpSlots JumpObjects, bool IsNew,
    IReadOnlyList<string> Connections)
{
    /// <summary>The text under the dot of the system: its name, or its nickname when it has no name.</summary>
    public string Label => Name.Length > 0 ? Name : Nickname;

    /// <summary>The jump objects as "n of 10".</summary>
    public string Slots => $"{JumpObjects.Used} of {JumpObjects.Max}";

    /// <summary>The hover tooltip of the system: its name, its nickname, its jump objects, the mark of a new system and
    /// its connections, one on each line.</summary>
    public string Tooltip => string.Join('\n', new[]
        {
            Label, $"Nickname: {Nickname}", $"Jump objects: {Slots}", IsNew ? SystemEntry.NewMark : null,
        }.OfType<string>().Concat(Connections));
}

/// <summary>A line of the Universe panel: the jump connections of one kind between two systems.</summary>
/// <param name="From">The nickname of the system at the start of the line.</param>
/// <param name="To">The nickname of the system at the end of the line. A one-way line points to it.</param>
/// <param name="Dashed">True for jump holes, false for jump gates and airlock gates.</param>
/// <param name="PlayerLocked">True when a connection of the line has a player lock in a new game.</param>
/// <param name="OneWay">True when the line has no connection whose other end goes back. The player cannot jump
/// back.</param>
/// <param name="Offset">The place of the line among the lines between the same two systems, in line gaps from the
/// middle: 0 for one line, -0.5 and 0.5 for two lines.</param>
internal sealed record UniverseMapLine(string From, string To, bool Dashed, bool PlayerLocked, bool OneWay, float Offset);

/// <summary>
/// The model of the read-only Universe panel: each system of the edit view at its place on the nav map grid, and each
/// jump connection as a line. A pair of jump gates is one solid line, and a pair of jump holes is one dashed line. A
/// player lock makes a line red. The rows come from <see cref="UniverseRows"/>, as the agent tool <c>get_universe</c>
/// gives them, so the panel and the agent show one graph.
/// </summary>
internal sealed class UniverseModel
{
    /// <summary>The largest x of the nav map grid of the retail game. The grid starts at 0.</summary>
    public const int GridWidth = 15;

    /// <summary>The largest y of the nav map grid of the retail game. The grid starts at 0, and a larger y is lower on the
    /// map.</summary>
    public const int GridHeight = 14;

    private readonly Dictionary<string, UniverseMapSystem> byNickname;

    private UniverseModel(IReadOnlyList<UniverseMapSystem> systems, IReadOnlyList<UniverseMapLine> lines)
    {
        Systems = systems;
        Lines = lines;
        byNickname = systems.ToDictionary(s => s.Nickname, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The systems in the order of <c>universe.ini</c>.</summary>
    public IReadOnlyList<UniverseMapSystem> Systems { get; }

    /// <summary>The lines between two different systems of <see cref="Systems"/>, in the order of their first
    /// connection.</summary>
    public IReadOnlyList<UniverseMapLine> Lines { get; }

    /// <summary>The systems without a place on the map.</summary>
    public IEnumerable<UniverseMapSystem> Unplaced => Systems.Where(s => s.Place == null);

    /// <summary>The smallest and the largest corner of the area to show: the nav map grid, and each place outside it.
    /// A place outside the grid is off the nav map of the game, and the panel shows it there.</summary>
    public (Vector2 Min, Vector2 Max) Bounds
    {
        get
        {
            var (min, max) = (Vector2.Zero, new Vector2(GridWidth, GridHeight));
            foreach (var place in Systems.Select(s => s.Place).OfType<Vector2>())
            {
                (min, max) = (Vector2.Min(min, place), Vector2.Max(max, place));
            }

            return (min, max);
        }
    }

    /// <summary>The system with a nickname, in any letter case, or null.</summary>
    public UniverseMapSystem? System(string nickname) => byNickname.GetValueOrDefault(nickname);

    /// <summary>The model of the rows of the jump graph.</summary>
    /// <param name="isNew">True for the nickname of a new system that the mod has not saved.</param>
    public static UniverseModel Of(IReadOnlyList<UniverseSystemRow> systems, IReadOnlyList<UniverseConnectionRow> connections,
        Func<string, bool> isNew)
    {
        var byRef = systems.ToDictionary(s => s.Ref, StringComparer.OrdinalIgnoreCase);
        string TargetText(string reference) => byRef.TryGetValue(reference, out var target)
            ? target.Name.Length > 0 ? $"{target.Name} ({target.Nickname})" : target.Nickname
            // A system that the universe does not have: the nickname after the "system:" part of the reference.
            : reference[(reference.IndexOf(':') + 1)..];
        var mapSystems = systems.Select(s => new UniverseMapSystem(s.Nickname, s.Name,
                s.MapPos is [var x, var y, ..] ? new Vector2(x, y) : null, s.JumpObjects, isNew(s.Nickname),
                connections.Where(c => c.System.Equals(s.Ref, StringComparison.OrdinalIgnoreCase))
                    .Select(c => ConnectionText(c, TargetText(c.ToSystem)))
                    .ToList()))
            .ToList();
        return new UniverseModel(mapSystems, LinesOf(connections, byRef));
    }

    /// <summary>The text of a connection in the tooltip of its system, for example "Jump gate to New York (Li01).
    /// Locked for the player."</summary>
    internal static string ConnectionText(UniverseConnectionRow connection, string target)
    {
        var kind = connection.Kind switch
        {
            JumpKind.JumpGate => "Jump gate",
            JumpKind.JumpHole => "Jump hole",
            _ => "Airlock gate",
        };
        var text = $"{kind} to {target}.";
        if (connection.Locked != JumpLock.None)
        {
            text += $" {ConnectionSections.LockLabel(connection.Locked)}.";
        }

        return connection.PairOk ? text : text + " The player cannot jump back.";
    }

    /// <summary>
    /// The lines of the connections. The two ends of a pair make one line, and a one-way connection makes a line of its
    /// own with a direction. A connection to its own system or to a system that the universe does not have makes no
    /// line. The lines between the same two systems get offsets, so that each one shows.
    /// </summary>
    private static List<UniverseMapLine> LinesOf(IReadOnlyList<UniverseConnectionRow> connections,
        Dictionary<string, UniverseSystemRow> byRef)
    {
        var lines = new List<UniverseMapLine>();
        var index = new Dictionary<(string, string, bool, bool), int>();
        foreach (var connection in connections)
        {
            if (!byRef.TryGetValue(connection.System, out var from) || !byRef.TryGetValue(connection.ToSystem, out var to) ||
                ReferenceEquals(from, to))
            {
                continue;
            }

            var dashed = connection.Kind == JumpKind.JumpHole;
            var oneWay = !connection.PairOk;
            var (a, b) = (from.Nickname.ToLowerInvariant(), to.Nickname.ToLowerInvariant());
            // The ends of a pair share one key in either direction. A one-way connection keeps its direction.
            var key = oneWay || string.CompareOrdinal(a, b) < 0 ? (a, b, dashed, oneWay) : (b, a, dashed, oneWay);
            var locked = connection.Locked is JumpLock.Player or JumpLock.Both;
            if (index.TryGetValue(key, out var at))
            {
                lines[at] = lines[at] with { PlayerLocked = lines[at].PlayerLocked || locked };
                continue;
            }

            index[key] = lines.Count;
            lines.Add(new UniverseMapLine(from.Nickname, to.Nickname, dashed, locked, oneWay, 0));
        }

        foreach (var group in Enumerable.Range(0, lines.Count).GroupBy(i => PairKey(lines[i])).Where(g => g.Count() > 1))
        {
            var members = group.ToList();
            for (var i = 0; i < members.Count; i++)
            {
                lines[members[i]] = lines[members[i]] with { Offset = i - (members.Count - 1) / 2f };
            }
        }

        return lines;
    }

    /// <summary>The two systems of a line without their order.</summary>
    private static (string, string) PairKey(UniverseMapLine line)
    {
        var (a, b) = (line.From.ToLowerInvariant(), line.To.ToLowerInvariant());
        return string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
    }
}
