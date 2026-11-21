using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>The three route tables of the retail game.</summary>
internal enum RouteKind
{
    /// <summary><c>systems_shortest_path.ini</c>: jump gates, jump holes and airlock gates.</summary>
    All,

    /// <summary><c>shortest_legal_path.ini</c>: jump gates only.</summary>
    Gates,

    /// <summary><c>shortest_illegal_path.ini</c>: jump holes only.</summary>
    Holes,
}

/// <summary>One line of a route table: the shortest path from one system to another system.</summary>
/// <param name="From">The start system.</param>
/// <param name="To">The last system.</param>
/// <param name="Systems">The systems of the path, from <paramref name="From"/> to <paramref name="To"/>.</param>
internal sealed record Route(string From, string To, IReadOnlyList<string> Systems)
{
    /// <summary>The line of the route in the file, for example <c>Path = Li01, Li02, Li01, Li02</c>.</summary>
    public string Line => "Path = " + string.Join(", ", Systems.Prepend(To).Prepend(From));

    /// <summary>True for the route of a system to itself.</summary>
    public bool ToItself => From.Equals(To, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The counts of the routes that a change of a route table adds, makes shorter, makes longer and removes. The
/// route of a system to itself does not count.</summary>
internal sealed record RouteDiff(int New, int Shorter, int Longer, int Lost)
{
    public bool IsEmpty => New == 0 && Shorter == 0 && Longer == 0 && Lost == 0;
}

/// <summary>One route table: one <c>[SystemConnections]</c> section for each start system, with one route for each
/// system that it reaches.</summary>
internal sealed class RouteTable
{
    internal RouteTable(RouteKind kind, IReadOnlyList<IReadOnlyList<Route>> sections)
    {
        Kind = kind;
        Sections = sections;
    }

    public RouteKind Kind { get; }

    /// <summary>The file name of the table in the universe folder of the game.</summary>
    public string FileName => RouteTables.FileName(Kind);

    /// <summary>The routes of each section, in file order. The first route of a section is the route of its start
    /// system to itself.</summary>
    public IReadOnlyList<IReadOnlyList<Route>> Sections { get; }

    /// <summary>Each route of the table, in file order.</summary>
    public IEnumerable<Route> Routes => Sections.SelectMany(s => s);

    /// <summary>The routes whose line is longer than the retail text reader reads in one piece.</summary>
    public IEnumerable<Route> TooLong =>
        Routes.Where(r => IniDocument.TextEncoding.GetByteCount(r.Line) > FormatChecks.MaxLineBytes);

    /// <summary>The text of the file: text INI in code page 1252 with CR LF line ends, as the retail game writes
    /// it.</summary>
    public byte[] Text()
    {
        var text = new StringBuilder();
        foreach (var section in Sections)
        {
            text.Append("[SystemConnections]\r\n");
            foreach (var route in section)
                text.Append(route.Line).Append("\r\n");
            text.Append("\r\n");
        }
        return IniDocument.TextEncoding.GetBytes(text.ToString());
    }

    /// <summary>True when both tables have the same routes, in any order and letter case.</summary>
    public bool SameRoutes(RouteTable other)
    {
        var mine = Index(this);
        var theirs = Index(other);
        return mine.Count == theirs.Count && mine.All(p =>
            theirs.TryGetValue(p.Key, out var path) && path.SequenceEqual(p.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The counts of the routes between two systems that <paramref name="after"/> adds, makes shorter, makes
    /// longer and removes, compared with <paramref name="before"/>.</summary>
    public static RouteDiff Diff(RouteTable before, RouteTable after)
    {
        var old = Index(before);
        var now = Index(after);
        int added = 0, shorter = 0, longer = 0;
        foreach (var (key, path) in now)
        {
            if (key.From.Equals(key.To, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!old.TryGetValue(key, out var earlier))
                added++;
            else if (path.Count < earlier.Count)
                shorter++;
            else if (path.Count > earlier.Count)
                longer++;
        }
        var lost = old.Keys.Count(k => !k.From.Equals(k.To, StringComparison.OrdinalIgnoreCase) && !now.ContainsKey(k));
        return new RouteDiff(added, shorter, longer, lost);
    }

    /// <summary>The systems of a list that no route of the table from another system reaches.</summary>
    public IReadOnlyList<string> Unreached(IEnumerable<string> systems)
    {
        var reached = Routes.Where(r => !r.ToItself).Select(r => r.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return systems.Where(s => !reached.Contains(s)).ToList();
    }

    private static Dictionary<(string From, string To), IReadOnlyList<string>> Index(RouteTable table)
    {
        var result = new Dictionary<(string, string), IReadOnlyList<string>>(KeyComparer.Instance);
        foreach (var route in table.Routes)
            result.TryAdd((route.From, route.To), route.Systems);
        return result;
    }

    private sealed class KeyComparer : IEqualityComparer<(string From, string To)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string From, string To) a, (string From, string To) b) =>
            StringComparer.OrdinalIgnoreCase.Equals(a.From, b.From) &&
            StringComparer.OrdinalIgnoreCase.Equals(a.To, b.To);

        public int GetHashCode((string From, string To) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.From),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.To));
    }
}

/// <summary>
/// The three route tables of a jump graph, with the rules of the retail game. The retail game reads them at its start,
/// and its NPC routes, mission targets and nav map routes use them. OpenLancer makes them when it builds the mods for a
/// retail game (see <c>TargetRoutes</c>). OpenLancer Play computes its own routes from the objects, so it needs no
/// file.
/// </summary>
/// <remarks>
/// The retail rules:
/// <list type="bullet">
/// <item>A breadth-first search from each system over its open connections, in the order of its file. The first
/// shortest path wins.</item>
/// <item>A <c>locked_gate</c> line closes its jump gate or jump hole. An <c>npc_locked_gate</c> line has no effect on
/// the tables.</item>
/// <item>A jump hole or gate to its own system and a connection to a system that <c>universe.ini</c> does not have
/// are not open.</item>
/// <item>An airlock gate counts only in the table of all connections.</item>
/// <item>A system without an open connection of the table gets no section. The first route of a section is the route
/// of its system to itself.</item>
/// </list>
/// </remarks>
internal sealed class RouteTables
{
    private RouteTables(RouteTable all, RouteTable gates, RouteTable holes)
    {
        All = all;
        Gates = gates;
        Holes = holes;
    }

    public RouteTable All { get; }

    public RouteTable Gates { get; }

    public RouteTable Holes { get; }

    /// <summary>The three tables in the order of <see cref="RouteKind"/>.</summary>
    public IReadOnlyList<RouteTable> Tables => [All, Gates, Holes];

    /// <summary>The file name of a table in the universe folder of the game.</summary>
    public static string FileName(RouteKind kind) => kind switch
    {
        RouteKind.All => "systems_shortest_path.ini",
        RouteKind.Gates => "shortest_legal_path.ini",
        _ => "shortest_illegal_path.ini",
    };

    /// <summary>The path of a table relative to the data folder. The retail game opens the tables at these
    /// paths.</summary>
    public static string DataPath(RouteKind kind) => @"Universe\" + FileName(kind);

    /// <summary>Makes the tables of a graph.</summary>
    public static RouteTables Build(UniverseGraph graph) =>
        new(Build(graph, RouteKind.All), Build(graph, RouteKind.Gates), Build(graph, RouteKind.Holes));

    /// <summary>True when each table has the same routes as the table of the other set.</summary>
    public bool SameRoutes(RouteTables other) =>
        All.SameRoutes(other.All) && Gates.SameRoutes(other.Gates) && Holes.SameRoutes(other.Holes);

    private static RouteTable Build(UniverseGraph graph, RouteKind kind)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in graph.Systems)
            names.TryAdd(system.Nickname, system.Nickname);
        var open = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in graph.Systems)
        {
            open[system.Nickname] = graph.ConnectionsOf(system.Nickname)
                .Where(c => Counts(kind, c.Kind) && !c.PlayerLocked)
                .Select(c => names.GetValueOrDefault(c.TargetSystem))
                .OfType<string>()
                .Where(t => !t.Equals(system.Nickname, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var sections = new List<IReadOnlyList<Route>>();
        foreach (var source in graph.Systems.Select(s => s.Nickname))
        {
            if (open[source].Count == 0)
                continue;
            var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [source] = null };
            var order = new List<string> { source };
            var queue = new Queue<string>([source]);
            while (queue.Count > 0)
            {
                var system = queue.Dequeue();
                foreach (var target in open[system])
                {
                    if (!parent.TryAdd(target, system))
                        continue;
                    order.Add(target);
                    queue.Enqueue(target);
                }
            }
            sections.Add(order.Select(t => new Route(source, t, PathTo(parent, t))).ToList());
        }
        return new RouteTable(kind, sections);
    }

    /// <summary>The systems from the start system to a target, by the parent of each system.</summary>
    private static IReadOnlyList<string> PathTo(Dictionary<string, string?> parent, string target)
    {
        var path = new List<string>();
        for (var system = target; system != null; system = parent[system])
            path.Add(system);
        path.Reverse();
        return path;
    }

    private static bool Counts(RouteKind kind, ConnectionKind connection) => kind switch
    {
        RouteKind.All => true,
        RouteKind.Gates => connection == ConnectionKind.Gate,
        _ => connection == ConnectionKind.Hole,
    };
}
