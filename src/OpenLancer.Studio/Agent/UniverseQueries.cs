using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Studio.Agent;

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
        IReadOnlyList<SectionRef> systems = call.Args.Text("system") is { } text
            ? [SceneQueries.System(session, text).Section]
            : UniverseRows.Systems(session);
        var page = call.Paged(systems, section => UniverseRows.System(session, call.Texts, section));
        return ToolResult.Of(new UniverseView(page.Rows, UniverseRows.Connections(session, page.Rows),
            session.RouteFilesChange, page.Total, page.NextCursor));
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

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
