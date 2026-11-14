using System;
using System.IO;
using System.Linq;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the field "Ship build file" of the Mission page (#18 and its review): the files of its list, and the
/// builds that the game does not find after a new empty ship build file. The tests read the vanilla files at run time
/// and hold no text of them.
/// </summary>
public sealed class MissionShipFileRetailTests(ITestOutputHelper output)
{
    [Fact]
    public void TheListHasTheOwnFilesOfTheStoryScriptsAndTheSharedFiles()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "shipfiles");
        var session = workspace.Open();
        var scripts = MissionSchema.Retail.StorySlots.Select(s => session.DataPath(s.Path)!)
            .Where(p => session.OpenDocument(p) is { HoldsScript: true }).Distinct(VfsPath.Comparer).ToList();

        var files = session.Missions.ShipBuildFiles();

        output.WriteLine(string.Join("\n", files.Select(f => $"{f.Value} {(f.Shared ? "shared" : "own")}")));
        // The game reads the two files directly in DATA\MISSIONS for all missions, encounters and bases.
        Assert.Equal(["npcships.ini", "npcships_test.ini"],
            files.Where(f => f.Shared).Select(f => Path.GetFileName(f.Path).ToLowerInvariant()).Order(StringComparer.Ordinal));
        // Each story script names its own ship build file in its folder, and the list has it.
        var own = scripts.Select(session.ShipBuildFileOf).OfType<string>().Distinct(VfsPath.Comparer).ToList();
        Assert.NotEmpty(own);
        Assert.All(own, path => Assert.Contains(files, f => !f.Shared && VfsPath.Comparer.Equals(f.Path, path)));
        // Each other file of the list is the npcships.ini of a mission folder.
        Assert.All(files.Where(f => !f.Shared),
            f => Assert.Equal("npcships.ini", Path.GetFileName(f.Path), ignoreCase: true));
    }

    [Fact]
    public void ANewShipBuildFileOfAStoryScriptLosesTheBuildsOfItsOwnFile()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "shipbuilds");
        var session = workspace.Open();
        var scripts = MissionSchema.Retail.StorySlots.Select(s => session.DataPath(s.Path)!)
            .Where(p => session.OpenDocument(p) is { HoldsScript: true }).Distinct(VfsPath.Comparer).ToList();

        // Retail keeps the builds of the mission ships in the own file of each script. So a new empty file loses them,
        // and a pick of the own file loses none.
        foreach (var script in scripts)
        {
            var lost = session.Missions.LostBuilds(script, null);
            output.WriteLine($"{MissionFiles.Display(script)}: {lost.Count} builds");
            Assert.Empty(session.Missions.LostBuilds(script, session.ShipBuildFileOf(script)));
        }
        Assert.All(scripts.Where(s => session.ShipBuildFileOf(s) != null),
            script => Assert.NotEmpty(session.Missions.LostBuilds(script, null)));
    }
}
