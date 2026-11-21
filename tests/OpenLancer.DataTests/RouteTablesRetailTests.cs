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

        AssertTable(tables.All, shipped[RouteKind.All].Routes, 50, 2079);
        AssertTable(tables.Gates, shipped[RouteKind.Gates].Routes, 35, 1225);
        AssertTable(tables.Holes, shipped[RouteKind.Holes].Routes, 46, 2071);
        Assert.All(Enum.GetValues<RouteKind>(),
            k => Assert.True(RouteTable.Diff(shipped[k], tables.Tables[(int)k]).IsEmpty));
    }

    [Fact]
    public void TheTablesOfHdEditionHaveEachPathOfItsFilesExceptTheSectionOfItsNewLock()
    {
        var (tables, shipped) = Read(GameDataPaths.RequireHde());

        AssertTable(tables.Gates, shipped[RouteKind.Gates].Routes, 35, 1225);
        Assert.True(RouteTable.Diff(shipped[RouteKind.Gates], tables.Gates).IsEmpty);
        foreach (var kind in new[] { RouteKind.All, RouteKind.Holes })
        {
            var table = tables.Tables[(int)kind];
            var lost = shipped[kind].Routes
                .Where(r => r.From.Equals(HdLockedSystem, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal(46, lost.Count);
            Assert.DoesNotContain(table.Routes, r => r.From.Equals(HdLockedSystem, StringComparison.OrdinalIgnoreCase));
            AssertTable(table, shipped[kind].Routes.Except(lost), kind == RouteKind.All ? 49 : 45,
                (kind == RouteKind.All ? 2079 : 2071) - 46);
            // The checklist of a package for HD Edition counts the routes from Ku07 as lost. The route of Ku07 to
            // itself does not count.
            Assert.Equal(new RouteDiff(0, 0, 0, lost.Count - 1), RouteTable.Diff(shipped[kind], table));
        }
    }

    /// <summary>The tables of the data of a game, and the route files of the game.</summary>
    private static (RouteTables Tables, Dictionary<RouteKind, RouteTable> Shipped) Read(string root)
    {
        var fs = GameBase.Standard(root).OpenFileSystem();
        var files = new Dictionary<string, IReadOnlyList<Section>?>(VfsPath.Comparer);
        var source = new FileUniverseSource(path => files.TryGetValue(path, out var sections)
            ? sections
            : files[path] = CheckView.Read(fs, path), ListRules.Retail);
        var tables = RouteTables.Build(UniverseGraph.Build(source));
        var shipped = Enum.GetValues<RouteKind>().ToDictionary(k => k,
            k => RouteTable.Read(k, CheckView.Read(fs, @"DATA\UNIVERSE\" + RouteTables.FileName(k))!));
        return (tables, shipped);
    }

    /// <param name="shipped">The routes of the route file of the game.</param>
    /// <param name="sections">The expected count of sections.</param>
    /// <param name="routes">The expected count of path lines.</param>
    private void AssertTable(RouteTable table, IEnumerable<Route> shipped, int sections, int routes)
    {
        var lines = table.Routes.ToList();
        output.WriteLine($"{table.FileName}: {table.Sections.Count} sections, {lines.Count} paths, longest line " +
                         $"{lines.Max(r => r.Line.Length)} bytes");
        var paths = shipped.ToDictionary(r => (r.From.ToLowerInvariant(), r.To.ToLowerInvariant()),
            r => r.Systems.Select(s => s.ToLowerInvariant()).ToList());
        Assert.Equal(sections, table.Sections.Count);
        Assert.Equal(sections, shipped.Select(r => r.From.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(routes, lines.Count);
        Assert.Equal(routes, paths.Count);
        foreach (var route in lines)
        {
            var key = (route.From.ToLowerInvariant(), route.To.ToLowerInvariant());
            Assert.True(paths.TryGetValue(key, out var path), $"The game has no path from {route.From} to {route.To}.");
            Assert.Equal(path, route.Systems.Select(s => s.ToLowerInvariant()));
        }
        Assert.Empty(table.TooLong);
    }
}
