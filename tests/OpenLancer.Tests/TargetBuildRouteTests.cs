using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A build for a retail game makes the route tables of the game when the mods change the routes.</summary>
public sealed class TargetBuildRouteTests
{
    private const string AllPath = @"data\Universe\systems_shortest_path.ini";
    private const string GatesPath = @"data\Universe\shortest_legal_path.ini";
    private const string HolesPath = @"data\Universe\shortest_illegal_path.ini";

    /// <summary>The paths of the three tables, in the order of the install paths.</summary>
    private static readonly string[] RoutePaths = [HolesPath, GatesPath, AllPath];

    private static ExportGame Game(bool hd = false) =>
        new RouteWorld().System("A", "gate B").System("B", "gate A").WriteInto(new ExportGame(hd));

    private static string Text(TargetFile file) => ExportGame.Text(file.Bytes);

    private static TargetRoutes Routes(ExportGame game, Mod mod)
    {
        var overlay = ModOverlay.Build(game.Base, [mod], TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator,
            OverlayOptions.Default with { KeepDocuments = true });
        return TargetRoutes.Of(overlay, game.Base.OpenFileSystem(), [mod]);
    }

    [Fact]
    public void AConnectionChangeAddsTheThreeTablesOfTheMergedData()
    {
        using var game = Game();
        var mod = RouteWorld.ConnectionMod(game);

        var files = RouteWorld.Build(game, mod);

        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var routes = RouteWorld.RouteFiles(files);
        Assert.Equal(RoutePaths, routes.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        Assert.All(routes, f => Assert.True(f is { Kind: TargetFileKind.Ini, HasBase: true, IsModFile: false }));
        var all = files.File(AllPath)!;
        Assert.StartsWith("[SystemConnections]\r\nPath = A, A, A\r\nPath = A, B, A, B\r\nPath = A, E, A, E\r\n", Text(all));
        Assert.Contains("Path = B, E, B, A, E\r\n", Text(all));
        Assert.Contains("Path = E, B, E, A, B\r\n", Text(all));
        Assert.Equal(new RouteDiff(4, 0, 0, 0), all.Routes!.Diff);
        Assert.Empty(all.Routes.Unreached);
        // The game has no jump hole, so the table of holes has no section.
        Assert.Equal("", Text(files.File(HolesPath)!));
        // The list stays in the order of the install paths.
        Assert.Equal(files.Files.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase), files.Files.Select(f => f.Path));
    }

    [Fact]
    public void AModThatChangesNoConnectionFileReadsNoGraphAndGetsNoRouteFile()
    {
        using var game = Game();
        var mod = game.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n");

        Assert.False(Routes(game, mod).MayChange());
        Assert.Empty(RouteWorld.RouteFiles(RouteWorld.Build(game, mod)));
        Assert.True(Routes(game, RouteWorld.ConnectionMod(game)).MayChange());
    }

    [Fact]
    public void AChangeOfASystemFileThatKeepsTheRoutesGetsNoRouteFile()
    {
        using var game = Game();
        var mod = game.Mod("pos",
            new() { [@"patches\" + RouteWorld.SystemPath("A")] = "[Object]\nnickname = A_sun\npos = 5, 0, 0\n" });

        Assert.True(Routes(game, mod).MayChange());
        var files = RouteWorld.Build(game, mod);
        Assert.Empty(RouteWorld.RouteFiles(files));
        Assert.NotNull(files.File(RouteWorld.SystemPath("A")));
    }

    [Fact]
    public void ABuildWithoutTheRouteParameterMakesNoTables()
    {
        using var game = Game();

        var files = RouteWorld.Build(game, RouteWorld.ConnectionMod(game), routeTables: false);

        Assert.Empty(RouteWorld.RouteFiles(files));
        Assert.DoesNotContain(files.Files, f => f.Path.EndsWith("_path.ini", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheRouteFilesOfAModGiveWayToTheTablesOfTheBuild()
    {
        using var game = Game();
        const string own = "[SystemConnections]\nPath = A, A, A\n";
        var mod = RouteWorld.ConnectionMod(game, more: new() { [@"files\DATA\UNIVERSE\systems_shortest_path.ini"] = own });

        var files = RouteWorld.Build(game, mod);

        Assert.Contains("Path = A, E, A, E", Text(files.File(AllPath)!));
        Assert.True(files.File(AllPath)!.Generated);
        var warning = Assert.Single(files.Diagnostics, d => d.Message.Contains("route table files"));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("Mod 'conn' has its own route table files. OpenLancer makes new route tables from the merged data, so " +
                     "the game does not get the files of the mod.", warning.Message);
        // The file of the mod is not in the build, so no warning tells that it replaces the file of the game.
        Assert.DoesNotContain(files.Diagnostics, d => d != warning &&
                                                      d.Message.Contains("systems_shortest_path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheRouteFilesOfAModGoToTheGameWhenTheBuildMakesNoTables()
    {
        using var game = Game();
        var mod = game.Mod("own", new()
        {
            [@"patches\" + ExportGame.GoodsPath] = "[Good]\nnickname = lf_hull\nprice = 5600\n",
            [@"files\DATA\UNIVERSE\systems_shortest_path.ini"] = "[SystemConnections]\nPath = A, A, A\n",
        });

        var files = RouteWorld.Build(game, mod);

        var file = files.File(AllPath)!;
        Assert.False(file.Generated);
        Assert.True(file.IsModFile);
        Assert.Contains(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message ==
            "Mod 'own' has its own route table files. The merged data gives the routes of the game, so OpenLancer makes no " +
            "route tables. The game gets the files of the mod as they are, and they can be out of date.");
        Assert.Contains(files.Diagnostics, d => d.Message == PackageRules.ReplacesGameFile(file.Path));
    }

    [Fact]
    public void EachGameGetsTheTablesOfItsOwnData()
    {
        using var vanilla = Game();
        using var hd = new RouteWorld().System("A", "gate B").System("B", "gate A").Lock("A_to_B")
            .WriteInto(new ExportGame(hd: true));

        var one = Text(RouteWorld.Build(vanilla, RouteWorld.ConnectionMod(vanilla)).File(AllPath)!);
        var two = Text(RouteWorld.Build(hd, RouteWorld.ConnectionMod(hd)).File(AllPath)!);

        Assert.Contains("Path = A, B, A, B", one);
        // The lock of the HD game closes A_to_B, so A reaches only E.
        Assert.DoesNotContain("Path = A, B,", two);
        Assert.Contains("Path = B, E, B, A, E", two);
    }

    [Fact]
    public void ARouteLineLongerThanTheGameReadsIsAnError()
    {
        var name = new string('x', 120);
        var world = new RouteWorld().System("A", $"gate {name}0");
        for (var i = 0; i < 10; i++)
            world.System($"{name}{i}", i < 9 ? $"gate {name}{i + 1}" : "gate A");
        using var game = world.WriteInto(new ExportGame());

        var files = RouteWorld.Build(game, RouteWorld.ConnectionMod(game));

        var errors = files.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Contains("is too long for the game. The game reads only the first 1,023 bytes of a " +
                                                "line, and it reads the rest as the next line.", e.Message));
        Assert.Contains(errors, e => e.Source.Equals(AllPath, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    public void ARouteLineOfMoreThan1023BytesIsAnError(int bytes, bool error)
    {
        // The longest line is "Path = a, c, a, b, c": 15 bytes, a and c two times, and b one time. The mod adds E with
        // a gate to A, so that the build makes the tables.
        var (a, c) = (new string('a', 250), new string('c', 250));
        var b = new string('b', bytes - 15 - 2 * a.Length - 2 * c.Length);
        using var game = new RouteWorld()
            .System("A", $"gate {a}")
            .System(a, "gate A", $"gate {b}")
            .System(b, $"gate {a}", $"gate {c}")
            .System(c, $"gate {b}")
            .WriteInto(new ExportGame());

        var files = RouteWorld.Build(game, RouteWorld.ConnectionMod(game));

        var lines = Text(files.File(AllPath)!).Split("\r\n");
        Assert.Equal(bytes, lines.Max(l => l.Length));
        // The routes go through jump gates only, so the table of gates has the same lines.
        string[] expected = error ? [GatesPath, AllPath] : [];
        Assert.Equal(expected, files.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Source).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheChecklistNamesTheChangedRoutesAndTheNewSystemsThatNoRouteReaches()
    {
        using var game = Game();
        var mod = RouteWorld.ConnectionMod(game, more: new()
        {
            [@"patches\DATA\UNIVERSE\universe.ini"] =
                "[system]\nol.new = true\nnickname = E\nfile = ..\\MODS\\conn\\SYSTEMS\\E\\E.ini\npos = 5, 5\n\n" +
                "[system]\nol.new = true\nnickname = F\nfile = ..\\MODS\\conn\\SYSTEMS\\E\\E.ini\npos = 6, 5\n",
        });

        var checklist = PackageTexts.Checklist(RouteWorld.Build(game, mod));

        var at = checklist.ToList().FindIndex(l => l.Equals(AllPath, StringComparison.OrdinalIgnoreCase));
        Assert.True(at >= 0, string.Join("\n", checklist));
        // F shares the file of E, so it has a gate to A, but no system has a gate to F.
        Assert.Equal("- OpenLancer makes this route table from the merged data: 7 new, 0 shorter, 0 longer and 0 lost routes.",
            checklist[at + 1]);
        Assert.Equal("- No route of this table reaches the new system F.", checklist[at + 2]);
        // The table of holes does not change. It gets the counts too.
        Assert.Contains("- OpenLancer makes this route table from the merged data: 0 new, 0 shorter, 0 longer and 0 lost routes.",
            checklist);
    }
}
