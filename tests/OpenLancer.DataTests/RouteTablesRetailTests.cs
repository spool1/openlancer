using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the route tables: the tables that OpenLancer makes from the data of a game have each path of the
/// route files that the game ships, in the same order of systems.
/// </summary>
public sealed class RouteTablesRetailTests(ITestOutputHelper output)
{
    /// <summary>The system whose section the HD Edition tables lose. HD Edition adds a <c>locked_gate</c> line for a
    /// hole of Ku07, so Ku07 has no open connection. It ships the route files of the retail game, which still have the
    /// section.</summary>
    private const string HdLockedSystem = "Ku07";

    [Fact]
    public void TheTablesOfVanillaHaveEachPathOfTheRetailFiles()
    {
        var (tables, shipped) = Read(GameDataPaths.RequireBase());

        AssertTable(tables.All, shipped[RouteKind.All], 50, 2079);
        AssertTable(tables.Gates, shipped[RouteKind.Gates], 35, 1225);
        AssertTable(tables.Holes, shipped[RouteKind.Holes], 46, 2071);
    }

    [Fact]
    public void TheTablesOfHdEditionHaveEachPathOfItsFilesExceptTheSectionOfItsNewLock()
    {
        var (tables, shipped) = Read(GameDataPaths.RequireHde());

        AssertTable(tables.Gates, shipped[RouteKind.Gates], 35, 1225);
        foreach (var kind in new[] { RouteKind.All, RouteKind.Holes })
        {
            var table = tables.Tables[(int)kind];
            var lost = shipped[kind].Where(p => p.Key.From.Equals(HdLockedSystem, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal(46, lost.Count);
            Assert.DoesNotContain(table.Routes, r => r.From.Equals(HdLockedSystem, StringComparison.OrdinalIgnoreCase));
            var rest = shipped[kind].Where(p => !lost.Contains(p)).ToDictionary(p => p.Key, p => p.Value);
            AssertTable(table, rest, kind == RouteKind.All ? 49 : 45, (kind == RouteKind.All ? 2079 : 2071) - 46);
        }
    }

    /// <summary>The tables of the data of a game, and the paths of its route files by start and end system in
    /// lowercase.</summary>
    private static (RouteTables Tables, Dictionary<RouteKind, Dictionary<(string From, string To), string[]>> Shipped)
        Read(string root)
    {
        var fs = GameBase.Standard(root).OpenFileSystem();
        var files = new Dictionary<string, IReadOnlyList<Section>?>(VfsPath.Comparer);
        var source = new FileUniverseSource(path => files.TryGetValue(path, out var sections)
            ? sections
            : files[path] = CheckView.Read(fs, path), ListRules.Retail);
        var tables = RouteTables.Build(UniverseGraph.Build(source));
        var shipped = new Dictionary<RouteKind, Dictionary<(string, string), string[]>>();
        foreach (var kind in Enum.GetValues<RouteKind>())
        {
            var path = @"DATA\UNIVERSE\" + RouteTables.FileName(kind);
            var paths = new Dictionary<(string, string), string[]>();
            foreach (var entry in CheckView.Read(fs, path)!.Where(s => s.Name.Equals("SystemConnections", StringComparison.OrdinalIgnoreCase)).SelectMany(s => s))
                paths[(entry[0].ToString()!.ToLowerInvariant(), entry[1].ToString()!.ToLowerInvariant())] =
                    [.. entry.Skip(2).Select(v => v.ToString()!.ToLowerInvariant())];
            shipped[kind] = paths;
        }
        return (tables, shipped);
    }

    /// <param name="sections">The expected count of sections.</param>
    /// <param name="routes">The expected count of path lines.</param>
    private void AssertTable(RouteTable table, Dictionary<(string From, string To), string[]> shipped, int sections, int routes)
    {
        var lines = table.Routes.ToList();
        output.WriteLine($"{table.FileName}: {table.Sections.Count} sections, {lines.Count} paths, longest line " +
                         $"{lines.Max(r => r.Line.Length)} bytes");
        Assert.Equal(sections, table.Sections.Count);
        Assert.Equal(sections, shipped.Keys.Select(k => k.From).Distinct().Count());
        Assert.Equal(routes, lines.Count);
        Assert.Equal(routes, shipped.Count);
        foreach (var route in lines)
        {
            var key = (route.From.ToLowerInvariant(), route.To.ToLowerInvariant());
            Assert.True(shipped.TryGetValue(key, out var path), $"The game has no path from {route.From} to {route.To}.");
            Assert.Equal(path, route.Systems.Select(s => s.ToLowerInvariant()));
        }
        Assert.Empty(table.TooLong);
    }
}
