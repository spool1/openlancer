using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Export;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The jump graph reads the same universe from each of its three sources.</summary>
public sealed class UniverseGraphTests
{
    private const string NewGate =
        "[Object]\nol.new = true\nnickname = A_to_C\narchetype = jumphole\ngoto = C, C_to_A, gate_tunnel_bretonia\n";

    /// <summary>A, B and C with a gate pair, a hole pair and a lock on one gate.</summary>
    private static RouteWorld World(bool withNewHole = false) => new RouteWorld()
        .System("A", withNewHole ? ["gate B", "hole C"] : ["gate B"])
        .System("B", "gate A", "gate C")
        .System("C", "gate B", "hole A")
        .Lock("B_to_C");

    private static TestWorkspace Workspace(RouteWorld world, Dictionary<string, string>? mod = null)
    {
        var ws = new TestWorkspace();
        foreach (var (path, text) in world.Files())
            ws.WriteBase(path, text);
        ws.Mod("act", mod ?? []);
        ws.WriteProfile("act");
        return ws;
    }

    private static void AssertSame(UniverseGraph expected, UniverseGraph actual)
    {
        Assert.Equal(expected.Systems, actual.Systems);
        Assert.Equal(expected.Connections, actual.Connections);
    }

    [Fact]
    public void TheGraphHasTheSystemsTheConnectionsAndTheLocks()
    {
        var graph = World().Graph();

        Assert.Equal(["A", "B", "C"], graph.Systems.Select(s => s.Nickname));
        Assert.Equal(new Vector2(1, 1), graph.System("b")!.MapPos);
        Assert.Equal(@"data\universe\Systems\B\B.ini", graph.System("B")!.File, ignoreCase: true);
        Assert.Equal(
        [
            new UniverseConnection("B", "B_to_A", "A", "A_to_B", ConnectionKind.Gate, "jumpgate", false, false),
            new UniverseConnection("B", "B_to_C", "C", "C_to_B", ConnectionKind.Gate, "jumpgate", true, false),
        ], graph.ConnectionsOf("B"));
        Assert.Equal(ConnectionKind.Hole, graph.ConnectionsOf("C")[1].Kind);
    }

    [Fact]
    public void TheThreeSourcesGiveTheSameGraphForTheSameData()
    {
        var world = World();
        using var ws = Workspace(world);
        var fs = GameBase.Standard(ws.Base).OpenFileSystem();
        var overlay = ModOverlay.Build(GameBase.Standard(ws.Base), [], TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator, OverlayOptions.Default with { KeepDocuments = true });
        var routes = TargetRoutes.Of(overlay, fs, []);

        var expected = world.Graph();
        Assert.Equal(5, expected.Connections.Count);
        AssertSame(expected, UniverseGraph.Build(routes.GameSource));
        AssertSame(expected, UniverseGraph.Build(routes.MergedSource));
        AssertSame(expected, ws.Session("act").Routes.Graph);
    }

    [Fact]
    public void AModGivesTheSameGraphInTheMergeAndInTheEditSession()
    {
        // The game data without mods, with the change of the mod written by hand.
        var expected = World(withNewHole: true).Graph();
        var mod = new Dictionary<string, string> { [@"patches\" + RouteWorld.SystemPath("A")] = NewGate };
        using var ws = Workspace(World(), mod);
        var activeMod = Data.Mods.Mod.Load(ws.ModFolder("act"));
        var gameBase = GameBase.Standard(ws.Base);
        var overlay = ModOverlay.Build(gameBase, [activeMod], TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator,
            OverlayOptions.Default with { KeepDocuments = true });
        var routes = TargetRoutes.Of(overlay, gameBase.OpenFileSystem(), [activeMod]);
        var session = ws.Session("act");

        AssertSame(expected, UniverseGraph.Build(routes.MergedSource));
        AssertSame(expected, session.Routes.Graph);
        AssertSame(World().Graph(), UniverseGraph.Build(routes.GameSource));
        AssertSame(World().Graph(), session.GameRoutes.Graph);
        Assert.True(session.RouteFilesChange);
    }

    [Fact]
    public void ASystemFileValueWithTwoDotsResolvesFromTheUniverseFolder()
    {
        var world = new RouteWorld().System("A", "gate E");
        var files = world.Files();
        files[RouteWorld.UniversePath] += "[system]\nnickname = E\nfile = ..\\MODS\\m\\SYSTEMS\\E\\E.ini\n";
        files[@"DATA\MODS\m\SYSTEMS\E\E.ini"] =
            "[Object]\nnickname = E_to_A\narchetype = jumpgate\ngoto = A, A_to_E, gate_tunnel_bretonia\n";

        var graph = RouteWorld.Graph(files);

        Assert.Equal(@"data\MODS\m\SYSTEMS\E\E.ini", graph.System("E")!.File, ignoreCase: true);
        Assert.Equal(@"DATA\MODS\m\SYSTEMS\E\E.ini",
            SystemFiles.FilePath(@"DATA\UNIVERSE\universe.ini", @"..\MODS\m\SYSTEMS\E\E.ini"));
        Assert.Equal("E_to_A", Assert.Single(graph.ConnectionsOf("E")).Object);
        Assert.Contains("Path = A, E, A, E", RouteWorld.Lines(RouteTables.Build(graph).All));
    }

    [Fact]
    public void ASecondSystemWithTheSameNicknameIsDropped()
    {
        var world = new RouteWorld().System("A", "gate B").System("B", "gate A");
        var files = world.Files();
        files[RouteWorld.UniversePath] += "[system]\nnickname = a\nfile = Systems\\B\\B.ini\n";

        var graph = RouteWorld.Graph(files);

        Assert.Equal(["A", "B"], graph.Systems.Select(s => s.Nickname));
        Assert.Equal("A_to_B", Assert.Single(graph.ConnectionsOf("A")).Object);
    }

    [Fact]
    public void TheSessionKeepsTheRoutesUntilAFileOfTheGraphChanges()
    {
        using var ws = Workspace(World());
        var session = ws.Session("act");

        var first = session.Routes;
        Assert.Same(first, session.Routes);
        Assert.False(session.RouteFilesChange);

        var a = new SectionRef(RouteWorld.SystemPath("A"), new SectionIdentity("Object", "A_to_B"));
        session.Execute(EditCommand.SetLinesFromText(a, "goto", ["C, C_to_A, gate_tunnel_bretonia"]));
        var second = session.Routes;
        Assert.NotSame(first, second);
        Assert.Equal("C", session.Routes.Graph.ConnectionsOf("A")[0].TargetSystem);
        Assert.True(session.RouteFilesChange);

        session.Undo();
        Assert.Equal("B", session.Routes.Graph.ConnectionsOf("A")[0].TargetSystem);
        Assert.False(session.RouteFilesChange);
    }
}
