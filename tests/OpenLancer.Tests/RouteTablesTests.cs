using System;
using System.IO;
using System.Linq;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.Editing.Universe;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The route tables of a jump graph follow the rules of the retail game (section B.5 of the systems
/// design).</summary>
public sealed class RouteTablesTests
{
    [Fact]
    public void AChainGivesEachShortestPathWithTheSelfPathFirst()
    {
        var tables = new RouteWorld()
            .System("A", "gate B")
            .System("B", "gate A", "gate C")
            .System("C", "gate B")
            .Tables();

        Assert.Equal(
        [
            "Path = A, A, A", "Path = A, B, A, B", "Path = A, C, A, B, C",
            "Path = B, B, B", "Path = B, A, B, A", "Path = B, C, B, C",
            "Path = C, C, C", "Path = C, B, C, B", "Path = C, A, C, B, A",
        ], RouteWorld.Lines(tables.All));
        Assert.Equal(3, tables.All.Sections.Count);
        Assert.Equal(RouteWorld.Lines(tables.All), RouteWorld.Lines(tables.Gates));
        // No hole, so no section of the hole table.
        Assert.Empty(tables.Holes.Sections);
    }

    [Fact]
    public void ACircleTakesTheDirectConnection()
    {
        var tables = new RouteWorld()
            .System("A", "gate B", "gate C")
            .System("B", "gate C", "gate A")
            .System("C", "gate A", "gate B")
            .Tables();

        Assert.Contains("Path = A, C, A, C", RouteWorld.Lines(tables.All));
        Assert.Contains("Path = C, B, C, B", RouteWorld.Lines(tables.All));
    }

    [Fact]
    public void TheGateTableHasOnlyGatesAndTheHoleTableOnlyHoles()
    {
        var tables = new RouteWorld()
            .System("A", "gate B", "hole C")
            .System("B", "gate A")
            .System("C", "hole A")
            .Tables();

        Assert.Contains("Path = B, C, B, A, C", RouteWorld.Lines(tables.All));
        Assert.Equal(["Path = A, A, A", "Path = A, B, A, B", "Path = B, B, B", "Path = B, A, B, A"],
            RouteWorld.Lines(tables.Gates));
        Assert.Equal(["Path = A, A, A", "Path = A, C, A, C", "Path = C, C, C", "Path = C, A, C, A"],
            RouteWorld.Lines(tables.Holes));
    }

    [Fact]
    public void APlayerLockClosesAGateAndAHole()
    {
        var tables = new RouteWorld()
            .System("A", "gate B", "hole C")
            .System("B", "gate C")
            .System("C", "hole A", "gate B")
            .Lock("A_to_B")
            .Lock("A_to_C")
            .Tables();

        // A has no open connection, so A gets no section. The other systems still reach A.
        Assert.DoesNotContain(tables.All.Routes, r => r.From == "A");
        Assert.Contains("Path = B, A, B, C, A", RouteWorld.Lines(tables.All));
        Assert.DoesNotContain(tables.Holes.Routes, r => r.From == "A");
        Assert.Equal(["Path = C, C, C", "Path = C, A, C, A"], RouteWorld.Lines(tables.Holes));
    }

    [Fact]
    public void AnNpcLockHasNoEffect()
    {
        var world = new RouteWorld()
            .System("A", "gate B")
            .System("B", "gate A");
        var open = world.Tables();
        var locked = world.Lock("A_to_B", npc: true).Tables();

        Assert.Equal(RouteWorld.Lines(open.All), RouteWorld.Lines(locked.All));
        Assert.True(locked.SameRoutes(open));
        Assert.True(world.Graph().Connections.Single(c => c.Object == "A_to_B").NpcLocked);
    }

    [Fact]
    public void AHoleToItsOwnSystemIsNoRoute()
    {
        var tables = new RouteWorld()
            .System("A", "hole A")
            .System("B", "gate A")
            .Tables();

        Assert.DoesNotContain(tables.Holes.Routes, r => r.From == "A");
        Assert.DoesNotContain(tables.All.Routes, r => r.From == "A");
        Assert.Equal(["Path = B, B, B", "Path = B, A, B, A"], RouteWorld.Lines(tables.All));
    }

    [Fact]
    public void AnAirlockGateCountsOnlyInTheTableOfAllConnections()
    {
        var tables = new RouteWorld()
            .System("A", "airlock B")
            .System("B", "airlock A")
            .Tables();

        Assert.Equal(["Path = A, A, A", "Path = A, B, A, B", "Path = B, B, B", "Path = B, A, B, A"],
            RouteWorld.Lines(tables.All));
        Assert.Empty(tables.Gates.Sections);
        Assert.Empty(tables.Holes.Sections);
    }

    [Fact]
    public void ASystemWithoutAnOpenConnectionGetsNoSectionAndNoSystemReachesAnUnconnectedSystem()
    {
        var world = new RouteWorld()
            .System("A", "gate B")
            .System("B", "gate A")
            .System("Lonely")
            .System("Station", "station A")
            .System("Lost", "gate Nowhere");
        var tables = world.Tables();

        Assert.Equal(["A", "B"], tables.All.Sections.Select(s => s[0].From));
        Assert.DoesNotContain(tables.All.Routes, r => r.To is "Lonely" or "Station" or "Lost" or "Nowhere");
        Assert.Equal(["Lonely", "Lost"], tables.All.Unreached(["A", "Lonely", "Lost"]));
        // A goto on a station, or to a system that universe.ini does not have, is no connection.
        Assert.DoesNotContain(world.Graph().Connections, c => c.System == "Station");
    }

    [Fact]
    public void TheFirstShortestPathWinsInTheOrderOfTheFile()
    {
        var first = new RouteWorld()
            .System("A", "gate B", "gate C")
            .System("B", "gate D")
            .System("C", "gate D")
            .System("D")
            .Tables();
        var second = new RouteWorld()
            .System("A", "gate C", "gate B")
            .System("B", "gate D")
            .System("C", "gate D")
            .System("D")
            .Tables();

        Assert.Contains("Path = A, D, A, B, D", RouteWorld.Lines(first.All));
        Assert.Contains("Path = A, D, A, C, D", RouteWorld.Lines(second.All));
        // Both have the same lengths, so the diff counts no change, but the routes differ.
        Assert.True(RouteTable.Diff(first.All, second.All).IsEmpty);
        Assert.False(first.SameRoutes(second));
    }

    [Fact]
    public void ALineLongerThanTheReaderReadsIsTooLong()
    {
        var name = new string('x', 120);
        var world = new RouteWorld();
        for (var i = 0; i < 10; i++)
            world.System($"{name}{i}", i < 9 ? $"gate {name}{i + 1}" : $"gate {name}0");
        var tables = world.Tables();

        var tooLong = tables.All.TooLong.ToList();
        Assert.NotEmpty(tooLong);
        Assert.All(tooLong, r => Assert.True(r.Line.Length > 1023));
        Assert.All(tables.All.Routes.Except(tooLong), r => Assert.True(r.Line.Length <= 1023));
        Assert.Empty(new RouteWorld().System("A", "gate B").System("B", "gate A").Tables().All.TooLong);
    }

    [Fact]
    public void TheDiffCountsNewShorterLongerAndLostRoutes()
    {
        var before = new RouteWorld()
            .System("A", "gate B")
            .System("B", "gate A", "gate C")
            .System("C", "gate B", "gate D")
            .System("D", "gate C")
            .Tables();
        // A gets a direct gate to D. D has only a gate to A. The new system E has a gate to A.
        var after = new RouteWorld()
            .System("A", "gate B", "gate D")
            .System("B", "gate A", "gate C")
            .System("C", "gate B", "gate D")
            .System("D", "gate A")
            .System("E", "gate A")
            .Tables();

        var diff = RouteTable.Diff(before.All, after.All);
        // New: E reaches A, B, C and D. No system reaches E. Shorter: A to D and D to A. Longer: D to C.
        Assert.Equal(4, diff.New);
        Assert.Equal(2, diff.Shorter);
        Assert.Equal(1, diff.Longer);
        Assert.Equal(0, diff.Lost);
        Assert.Contains("Path = A, D, A, D", RouteWorld.Lines(after.All));
        Assert.Contains("Path = D, C, D, A, B, C", RouteWorld.Lines(after.All));
        Assert.Equal(new RouteDiff(0, 0, 0, 0), RouteTable.Diff(before.All, before.All));
        var lost = RouteTable.Diff(after.All, before.All);
        Assert.Equal(4, lost.Lost);
    }

    [Fact]
    public void TheEngineParserReadsTheTextBack()
    {
        var tables = new RouteWorld()
            .System("A", "gate B", "hole C")
            .System("B", "gate A")
            .System("C", "hole A")
            .Tables();

        foreach (var table in tables.Tables)
        {
            var parsed = new ShortestPathIni();
            parsed.ParseIni(new MemoryStream(table.Text()), table.FileName);
            Assert.Equal(table.Sections.Count, parsed.SystemConnections.Count);
            var entries = parsed.SystemConnections.SelectMany(s => s.Entries)
                .Select(e => string.Join(", ", new[] { e.Start, e.End }.Concat(e.Hops))).ToList();
            Assert.Equal(table.Routes.Select(r => r.Line["Path = ".Length..]), entries);
        }
        var text = System.Text.Encoding.ASCII.GetString(tables.All.Text());
        Assert.StartsWith("[SystemConnections]\r\nPath = A, A, A\r\n", text);
    }
}
