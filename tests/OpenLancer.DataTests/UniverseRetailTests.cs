using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using OpenLancer.Editor.Universe;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the model of the Universe panel, the same in vanilla 1.1 and in HD Edition 0.7.1. Each of the 53
/// systems has a name and a place with whole numbers on the nav map grid, so the panel shows the grid and no system
/// off it. Each of the 232 jump objects goes both ways, so no line has a direction. The 116 pairs make 115 lines,
/// because the hole pair inside Li01 has no line. Two hole pairs join Rh02 and Rh04, and two airlock gate pairs join
/// St02 and St02c, so these lines show side by side.
/// </summary>
public sealed class UniverseRetailTests(ITestOutputHelper output)
{
    static UniverseRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheUniversePanelShowsEachSystemOfVanillaOnTheGridWithItsPairs() => Check(GameDataPaths.RequireBase());

    [Fact]
    public void TheUniversePanelShowsEachSystemOfHdEditionOnTheGridWithItsPairs() => Check(GameDataPaths.RequireHde());

    private void Check(string root)
    {
        using var workspace = new RetailWorkspace(root, "oluniverse");
        var session = workspace.Open();
        var infocards = new InfocardManager(new FreelancerIni(FileSystem.FromPath(root)).Resources);
        var (systems, connections) = UniverseRows.All(session, new ItemTexts(session,
            id => infocards.GetStringResource(id) ?? "", _ => ""));

        var model = UniverseModel.Of(systems, connections, _ => false);

        output.WriteLine($"{model.Systems.Count} systems, {connections.Count} connections, {model.Lines.Count} lines, " +
                         $"{model.Lines.Count(l => l.Dashed)} dashed, {model.Lines.Count(l => l.PlayerLocked)} red");
        Assert.Equal(53, model.Systems.Count);
        Assert.Empty(model.Unplaced);
        Assert.All(model.Systems, s => Assert.NotEqual(s.Nickname, s.Label));
        Assert.All(model.Systems, s => Assert.True(float.IsInteger(s.Place!.Value.X) && float.IsInteger(s.Place.Value.Y),
            s.Nickname));
        Assert.Equal((Vector2.Zero, new Vector2(UniverseModel.GridWidth, UniverseModel.GridHeight)), model.Bounds);
        Assert.Equal(232, connections.Count);
        Assert.DoesNotContain(model.Lines, l => l.OneWay);
        Assert.Equal(115, model.Lines.Count);
        Assert.DoesNotContain(model.Lines, l => l.From == l.To);
        Assert.Equal(2, model.Lines.Count(l => l is { From: "Rh02", To: "Rh04", Dashed: true }));
        Assert.Equal([-0.5f, 0.5f], model.Lines.Where(l => l is { From: "St02", To: "St02c" }).Select(l => l.Offset));
    }
}
