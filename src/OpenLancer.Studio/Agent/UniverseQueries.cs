using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Studio.Agent;

/// <summary>The kind of a jump connection in the results of the universe tools.</summary>
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

/// <summary>A star system of the jump graph of W (tool <c>get_universe</c>).</summary>
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

/// <summary>A jump connection of the jump graph of W (tool <c>get_universe</c>).</summary>
/// <param name="Ref">The <c>obj:</c> reference of the object with the <c>goto</c> line.</param>
/// <param name="System">The <c>system:</c> reference of the system of the object.</param>
/// <param name="ToSystem">The <c>system:</c> reference that value 0 of <c>goto</c> names.</param>
/// <param name="ToObject">The <c>obj:</c> reference that value 1 of <c>goto</c> names, or null.</param>
/// <param name="PairOk">True when the object that the connection goes to goes back to it.</param>
/// <param name="Locked">The lock at the start of a new game.</param>
internal sealed record UniverseConnectionRow(string Ref, JumpKind Kind, string System, string ToSystem, string? ToObject,
    bool PairOk, JumpLock Locked);

/// <summary>The jump graph of W: one page of systems and the connections of these systems (tool
/// <c>get_universe</c>).</summary>
/// <param name="Connections">The connections of the systems of the page: the systems in order, and the objects of each
/// system in file order.</param>
/// <param name="RouteFilesChange">True when the route tables of W differ from the route tables of the game data
/// without mods.</param>
/// <param name="Total">The number of systems of all pages.</param>
/// <param name="NextCursor">The cursor of the next page, or null on the last page.</param>
internal sealed record UniverseView(IReadOnlyList<UniverseSystemRow> Systems, IReadOnlyList<UniverseConnectionRow> Connections,
    bool RouteFilesChange, int Total, string? NextCursor);

/// <summary>The shortest route from one system to another system (tool <c>get_route</c>).</summary>
/// <param name="To">The <c>system:</c> reference of the last system.</param>
/// <param name="Systems">The <c>system:</c> references of the systems of the route, from the start system to the last
/// system.</param>
/// <param name="Jumps">The number of jumps of the route.</param>
internal sealed record RouteRow(string To, IReadOnlyList<string> Systems, int Jumps);

/// <summary>The routes from one system of a route table of W (tool <c>get_route</c>).</summary>
/// <param name="Unreachable">The <c>system:</c> references of the systems that no route of the table reaches.</param>
internal sealed record RouteView(IReadOnlyList<RouteRow> Routes, IReadOnlyList<string> Unreachable);

/// <summary>
/// The query tools of the universe: the jump graph of W with its systems and jump connections, and the routes of the
/// route tables. The tables follow the rules of the retail game (see <see cref="RouteTables"/>). OpenLancer makes them
/// at the build for a retail game, so they show what the game reads after Ctrl+F5 or an export.
/// </summary>
internal static class UniverseQueries
{
    /// <summary>The words of <c>kind</c> of <c>get_route</c>, in the order of the route tables.</summary>
    public static readonly IReadOnlyList<string> RouteKinds = ["all", "gates", "holes"];

    public static ToolStep GetUniverse(ToolCall call)
    {
        var session = call.Session;
        var routes = session.Routes;
        var graph = routes.Graph;
        IReadOnlyList<SectionRef> systems = call.Args.Text("system") is { } text
            ? [SceneQueries.System(session, text).Section]
            : Systems(session);
        var page = call.Paged(systems, section =>
        {
            var view = session.Get(section)!;
            var nickname = section.Id.Key!;
            var path = graph.System(nickname)?.File;
            return new UniverseSystemRow(SceneQueries.SystemRef(nickname), nickname, call.Texts.TextOf(section, "strid_name"),
                path, AgentRows.OriginOf(session, section, view, "nickname"))
            {
                MapPos = AgentRows.Numbers(view, "pos"),
                NavMapScale = AgentRows.Numbers(view, "NavMapScale") is [var scale, ..] ? scale : null,
                Visit = AgentRows.Numbers(view, "visit") is [var visit, ..] && float.IsInteger(visit) ? (int)visit : null,
                JumpObjects = Slots(session, path),
                Reachable = graph.Reached(nickname),
            };
        });
        var connections = page.Rows.SelectMany(s => Connections(session, graph, s.Nickname)).ToList();
        return ToolResult.Of(new UniverseView(page.Rows, connections, session.RouteFilesChange, page.Total, page.NextCursor));
    }

    public static ToolStep GetRoute(ToolCall call)
    {
        var session = call.Session;
        var args = call.Args;
        var from = SceneQueries.System(session, args.Text("from")!, "from").Nickname;
        var to = args.Text("to") is { } text ? SceneQueries.System(session, text, "to").Nickname : null;
        var routes = session.Routes;
        var table = routes.Tables.Tables[RouteKinds.ToList().IndexOf(args.Text("kind")!)];
        // The first route of a section is the route of its start system to itself.
        var section = table.Sections.FirstOrDefault(s => s.Count > 0 && Same(s[0].From, from)) ?? [];
        bool Wanted(string system) => !Same(system, from) && (to == null || Same(system, to));
        var rows = section.Where(r => Wanted(r.To))
            .Select(r => new RouteRow(SceneQueries.SystemRef(r.To), r.Systems.Select(SceneQueries.SystemRef).ToList(),
                r.Systems.Count - 1))
            .ToList();
        var reached = section.Select(r => r.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreachable = routes.Graph.Systems.Select(s => s.Nickname)
            .Where(s => Wanted(s) && !reached.Contains(s))
            .Select(SceneQueries.SystemRef)
            .ToList();
        return ToolResult.Of(new RouteView(rows, unreachable));
    }

    /// <summary>The jump objects of a system file in W, or none for a system without a file.</summary>
    internal static JumpSlots Slots(EditSession session, string? path) => new(
        path == null ? 0 : ConnectionChecks.JumpObjects(session.WorkingSections(path) ?? []), ConnectionChecks.MaxJumpObjects);

    /// <summary>The <c>[system]</c> sections of W of <c>universe.ini</c> that the game reads, in file order. The game
    /// drops a section without a nickname, and a second section whose nickname has the FLHash of an earlier one, as the
    /// jump graph does.</summary>
    private static IReadOnlyList<SectionRef> Systems(EditSession session)
    {
        if (session.UniversePath is not { } universe)
            return [];
        var ids = new HashSet<uint>();
        return session.Sections(universe)
            .Where(s => session.IsSystemSection(s) && s.Id.Key != null && ids.Add(FLHash.CreateID(s.Id.Key)))
            .ToList();
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

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
