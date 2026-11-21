using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The kind of a jump connection in the rows of the jump graph.</summary>
internal enum JumpKind
{
    JumpGate,
    JumpHole,
    AirlockGate,
}

/// <summary>The jump objects of a system: the objects with a <c>goto</c> line, and the most that one system can
/// have.</summary>
/// <param name="Used">The jump objects of the system file in W.</param>
/// <param name="Max">The most jump objects that one system can have in the game.</param>
internal sealed record JumpSlots(int Used, int Max);

/// <summary>A star system of the jump graph of W (tool <c>get_universe</c> and the Universe panel of the
/// editor).</summary>
/// <param name="Ref">The <c>system:</c> reference.</param>
/// <param name="Name">The name that the game shows, or an empty text.</param>
/// <param name="File">The install path of the system file, or null when the <c>[system]</c> names no file.</param>
internal sealed record UniverseSystemRow(string Ref, string Nickname, string Name, string? File, ItemOrigin Origin)
{
    /// <summary>The place on the nav map grid, as the <c>pos</c> line gives it, or null.</summary>
    public float[]? MapPos { get; init; }

    /// <summary>The zoom of the system map (<c>NavMapScale</c>), or null.</summary>
    public float? NavMapScale { get; init; }

    /// <summary>The value of the <c>visit</c> line, or null.</summary>
    public int? Visit { get; init; }

    public required JumpSlots JumpObjects { get; init; }

    /// <summary>True when a jump gate, a jump hole or an airlock gate of another system goes to the system.</summary>
    public bool Reachable { get; init; }
}

/// <summary>A jump connection of the jump graph of W (tool <c>get_universe</c> and the Universe panel of the
/// editor).</summary>
/// <param name="Ref">The <c>obj:</c> reference of the object with the <c>goto</c> line.</param>
/// <param name="System">The <c>system:</c> reference of the system of the object.</param>
/// <param name="ToSystem">The <c>system:</c> reference that value 0 of <c>goto</c> names.</param>
/// <param name="ToObject">The <c>obj:</c> reference that value 1 of <c>goto</c> names, or null.</param>
/// <param name="PairOk">True when the object that the connection goes to goes back to it.</param>
/// <param name="Locked">The lock at the start of a new game.</param>
internal sealed record UniverseConnectionRow(string Ref, JumpKind Kind, string System, string ToSystem, string? ToObject,
    bool PairOk, JumpLock Locked);

/// <summary>
/// The rows of the jump graph of W: one row for each system of <c>universe.ini</c> that the game reads, and one row for
/// each jump connection of these systems. The agent tool <c>get_universe</c> gives them in pages, and the Universe panel
/// of the editor draws all of them. So the agent and the user see one graph.
/// </summary>
internal static class UniverseRows
{
    /// <summary>The <c>[system]</c> sections of W of <c>universe.ini</c> that the game reads, in file order. The game
    /// drops a section without a nickname, and a second section whose nickname has the FLHash of an earlier one, as the
    /// jump graph does.</summary>
    public static IReadOnlyList<SectionRef> Systems(EditSession session)
    {
        if (session.UniversePath is not { } universe)
            return [];
        var ids = new HashSet<uint>();
        return session.Sections(universe)
            .Where(s => session.IsSystemSection(s) && s.Id.Key != null && ids.Add(FLHash.CreateID(s.Id.Key)))
            .ToList();
    }

    /// <summary>The row of a <c>[system]</c> section of W.</summary>
    /// <param name="texts">The texts of the session, for the name.</param>
    public static UniverseSystemRow System(EditSession session, ItemTexts texts, SectionRef section) =>
        System(session, session.Routes.Graph, texts, section);

    /// <summary>The connections of systems: the systems in their order, and the objects of each system in file
    /// order.</summary>
    public static IReadOnlyList<UniverseConnectionRow> Connections(EditSession session,
        IEnumerable<UniverseSystemRow> systems)
    {
        var graph = session.Routes.Graph;
        return systems.SelectMany(s => Connections(session, graph, s.Nickname)).ToList();
    }

    /// <summary>The whole jump graph of W: the row of each system of <see cref="Systems"/>, and each connection of
    /// these systems.</summary>
    public static (IReadOnlyList<UniverseSystemRow> Systems, IReadOnlyList<UniverseConnectionRow> Connections) All(
        EditSession session, ItemTexts texts)
    {
        var graph = session.Routes.Graph;
        var systems = Systems(session).Select(s => System(session, graph, texts, s)).ToList();
        return (systems, systems.SelectMany(s => Connections(session, graph, s.Nickname)).ToList());
    }

    /// <summary>The jump objects of a system file in W, or none for a system without a file.</summary>
    public static JumpSlots Slots(EditSession session, string? path) => new(
        path == null ? 0 : ConnectionChecks.JumpObjects(session.WorkingSections(path) ?? []), ConnectionChecks.MaxJumpObjects);

    private static UniverseSystemRow System(EditSession session, UniverseGraph graph, ItemTexts texts, SectionRef section)
    {
        var view = session.Get(section)!;
        var nickname = section.Id.Key!;
        var path = graph.System(nickname)?.File;
        return new UniverseSystemRow(SceneQueries.SystemRef(nickname), nickname, texts.TextOf(section, "strid_name"),
            path, AgentRows.OriginOf(session, section, view, "nickname"))
        {
            MapPos = AgentRows.Numbers(view, "pos"),
            NavMapScale = AgentRows.Numbers(view, "NavMapScale") is [var scale, ..] ? scale : null,
            Visit = AgentRows.Numbers(view, "visit") is [var visit, ..] && float.IsInteger(visit) ? (int)visit : null,
            JumpObjects = Slots(session, path),
            Reachable = graph.Reached(nickname),
        };
    }

    /// <summary>The connections of a system in the order of its file.</summary>
    private static IEnumerable<UniverseConnectionRow> Connections(EditSession session, UniverseGraph graph, string system)
    {
        var path = graph.System(system)?.File;
        var objects = new Dictionary<string, SectionRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in path == null ? [] : session.Sections(path))
        {
            if (SceneSections.Is(section.Id.Type, SceneSections.ObjectType) && section.Id.Key is { } key)
                objects.TryAdd(key, section);
        }
        foreach (var connection in graph.ConnectionsOf(system))
        {
            var reference = objects.TryGetValue(connection.Object, out var section)
                ? ItemRefs.Canonical(session, section)
                : ItemRefs.Format(new ItemReference(ItemRefKind.Object, connection.Object));
            yield return new UniverseConnectionRow(reference, KindOf(connection.Kind), SceneQueries.SystemRef(system),
                SceneQueries.SystemRef(connection.TargetSystem),
                connection.ExitObject.Length > 0 ? ItemRefs.Format(new ItemReference(ItemRefKind.Object, connection.ExitObject)) : null,
                graph.PartnerOf(connection) != null,
                (connection.PlayerLocked, connection.NpcLocked) switch
                {
                    (true, true) => JumpLock.Both,
                    (true, false) => JumpLock.Player,
                    (false, true) => JumpLock.Npc,
                    _ => JumpLock.None,
                });
        }
    }

    private static JumpKind KindOf(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Gate => JumpKind.JumpGate,
        ConnectionKind.Hole => JumpKind.JumpHole,
        _ => JumpKind.AirlockGate,
    };
}
