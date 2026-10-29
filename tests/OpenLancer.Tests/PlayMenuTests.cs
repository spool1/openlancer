using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The lists of the Play menu: the bases of Play at base (section 9.7 of the M4 design) and the ship packages
/// (section 11.2). Also the checks before a play, which the Play menu and the agent tool play share.</summary>
public sealed class PlayMenuTests
{
    private static readonly StarSystem[] Systems =
    [
        new() { Nickname = "Li01", SourceFile = "" },
        new() { Nickname = "Li02", SourceFile = "" },
        new() { Nickname = "Br01", SourceFile = "" },
        new() { Nickname = "Ew01", SourceFile = "" },
    ];

    private static readonly Base[] Bases =
    [
        new() { Nickname = "Br01_01_Base", System = "br01" },
        new() { Nickname = "Li01_01_Base", System = "Li01" },
        new() { Nickname = "Li02_01_Base", System = "Li02" },
        new() { Nickname = "Li01_02_Base", System = "LI01" },
        // A base that no system of the data has, and a base without a system.
        new() { Nickname = "Xx01_01_Base", System = "xx01" },
        new() { Nickname = "intro_base" },
    ];

    [Fact]
    public void TheBasesOfTheOpenSystemComeFirstThenTheOtherSystemsInTheOrderOfTheData()
    {
        var groups = PlayMenu.BaseGroups(Systems, Bases, "br01");

        // A system without bases is not in the list.
        Assert.Equal(["Br01", "Li01", "Li02"], groups.Select(g => g.System.Nickname));
        Assert.Equal(["Br01_01_Base"], groups[0].Bases.Select(b => b.Nickname));
        // The bases of a system keep the order of the data.
        Assert.Equal(["Li01_01_Base", "Li01_02_Base"], groups[1].Bases.Select(b => b.Nickname));
    }

    /// <summary>No open system, an open system without bases, and an open system that is the first system of the
    /// data.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("ew01")]
    [InlineData("li01")]
    public void ElseTheListHasTheOrderOfTheData(string? open)
    {
        var groups = PlayMenu.BaseGroups(Systems, Bases, open);

        Assert.Equal(["Li01", "Li02", "Br01"], groups.Select(g => g.System.Nickname));
        Assert.DoesNotContain(groups.SelectMany(g => g.Bases), b => b.Nickname is "Xx01_01_Base" or "intro_base");
    }

    [Fact]
    public void AShipPackageIsAGoodOfCategoryShip()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var package = ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_package");

        Assert.Equal(package, PlayShips.Find(session, "S1_PACKAGE"));
        Assert.True(PlayShips.IsPackage(package, session.Get(package)!));
        // A hull good, an equipment good and an unknown nickname are no packages.
        Assert.Null(PlayShips.Find(session, "s1_hull"));
        Assert.Null(PlayShips.Find(session, "eng1"));
        Assert.Null(PlayShips.Find(session, "no_such_package"));
        var hull = ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_hull");
        Assert.False(PlayShips.IsPackage(hull, session.Get(hull)!));
        // The menu names the ship of the package.
        Assert.Equal([ItemWorkspace.Ship("s1")], session.Related(package, ItemRelation.Ship));
    }

    /// <summary>The checks before a play (review finding R21 of MCP1-2): a profile, its game data, a system of the game
    /// data that the Scene shows for a start in space, and a base of the game data in one of its systems, as the Play at
    /// base list has it.</summary>
    [Fact]
    public void TheChecksBeforeAPlayNeedThePlaceInTheGameData()
    {
        using var ws = new TestWorkspace();
        var items = Items(ws);
        PlayStart Space(string system) => new PlayStart.InSpace(system, Vector3.Zero, Quaternion.Identity);

        Assert.Equal("No profile is open.", PlayChecks.StartBlock(false, null, null, Space("ol_sys")));
        Assert.Equal("The game data of the profile did not load.", PlayChecks.StartBlock(true, null, null, Space("ol_sys")));
        Assert.Null(PlayChecks.StartBlock(true, items, "OL_SYS", Space("ol_sys")));
        Assert.Equal("The game data of the profile has no system 'ol_gone'.",
            PlayChecks.StartBlock(true, items, "ol_gone", Space("ol_gone")));
        Assert.Equal("The Scene does not show the system 'ol_sys', so Play cannot check the start point. Open the system " +
                     "in the Scene first.", PlayChecks.StartBlock(true, items, "ol_other", Space("ol_sys")));
        Assert.Null(PlayChecks.StartBlock(true, items, null, new PlayStart.AtBase("OL_BASE_01")));
        foreach (var dock in new[] { "ol_gone_base", "ol_base_nowhere", "ol_base_lost" })
        {
            Assert.Equal($"The game data of the profile has no base '{dock}' in one of its systems.",
                PlayChecks.StartBlock(true, items, "ol_sys", new PlayStart.AtBase(dock)));
        }
        Assert.Equal("The game data of the profile did not load.", PlayChecks.DataBlock(true, null));
        Assert.Null(PlayChecks.DataBlock(true, items));
        // Play from camera: the checks of the start at the camera, and an open system.
        var camera = new PlayStart.InSpace("ol_sys", new Vector3(1, 2, 3), Quaternion.Identity);
        Assert.Null(PlayChecks.CameraBlock(true, items, camera));
        Assert.Equal("No profile is open.", PlayChecks.CameraBlock(false, null, null));
        Assert.Equal("The game data of the profile did not load.", PlayChecks.CameraBlock(true, null, camera));
        Assert.Equal("No system is open. Play from camera starts the ship at the Scene camera.",
            PlayChecks.CameraBlock(true, items, null));
        Assert.Equal("The game data of the profile has no system 'ol_gone'.",
            PlayChecks.CameraBlock(true, items, camera with { System = "ol_gone" }));
        // The Play at base list has the bases that the checks let start.
        Assert.Equal(["ol_base_01"], PlayMenu.BaseGroups(items.Systems, items.Bases, null)
            .SelectMany(g => g.Bases).Select(b => b.Nickname));
    }

    [Fact]
    public void TheTextsOfTheChecksPassTheSteLint()
    {
        string[] texts =
        [
            PlayChecks.NoProfile, PlayChecks.NoGameData, PlayChecks.NoOpenSystem, PlayChecks.NoSystem("ol_sys"),
            PlayChecks.NotInScene("ol_sys"), PlayChecks.NoBase("ol_base_01"),
        ];

        Assert.Empty(texts.SelectMany(t => SteLint.Problems(t)));
    }

    /// <summary>Game data with the system ol_sys, the base ol_base_01 in it, a base without a system and a base in a
    /// system that the game data does not have.</summary>
    internal static GameItemDb Items(TestWorkspace ws)
    {
        // A freelancer.ini without the resource DLLs, so the game data object loads no string resources.
        ws.WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n");
        var items = new GameItemDb(FileSystem.FromPath(ws.Base));
        Add(items.Systems, new StarSystem { Nickname = "ol_sys", SourceFile = "" });
        Add(items.Bases, new Base { Nickname = "ol_base_01", System = "ol_sys" });
        Add(items.Bases, new Base { Nickname = "ol_base_nowhere" });
        Add(items.Bases, new Base { Nickname = "ol_base_lost", System = "ol_lost" });
        return items;
    }

    private static void Add<T>(GameItemCollection<T> collection, T item) where T : IdentifiableItem
    {
        item.CRC = FLHash.CreateID(item.Nickname);
        collection.Add(item);
    }
}
