using System;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The nebula and field files of the system files and their owners (section 7 of the M3 design).</summary>
public sealed class FieldFilesTests
{
    [Fact]
    public void TheFilesHaveTheirOwnerSystems()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li02Path, SceneWorkspace.Li02Text + "\n[Nebula]\nfile = SOLAR\\Nebula\\CLOUD.ini\nzone = Zone_li02\n" +
                                              "[Nebula]\nfile = solar\\nebula\\cloud.ini\nzone = Zone_li02_b\n");
        var session = ws.Session("act");
        var files = session.DataFiles;

        Assert.Equal([SceneWorkspace.CloudPath], files.Paths("nebula_files"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal([SceneWorkspace.RocksPath], files.Paths("field_files"), StringComparer.OrdinalIgnoreCase);
        // One file of two systems, and one system that names the file twice, give each owner once.
        Assert.Equal([SceneWorkspace.Li01Path, SceneWorkspace.Li02Path], files.OwnersOf(SceneWorkspace.CloudPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["nebula_files"], files.RolesOf(SceneWorkspace.CloudPath));
        Assert.Empty(files.OwnersOf(ListWorkspace.SolarPath));
    }

    [Fact]
    public void AChangeOfAFileLineChangesTheFilesAndAnUndoPutsThemBack()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var state = session.DataFiles.FieldFilesState;
        var nebula = SceneWorkspace.Li01("Nebula", "Zone_cloud");

        // A change of another line keeps the state, so the caches of the owners stay.
        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_01"), "pos", ["1, 2, 3"]));
        Assert.Same(state, session.DataFiles.FieldFilesState);
        session.Execute(EditCommand.SetLinesFromText(nebula, "file", [@"solar\nebula\other.ini"]));

        Assert.Equal([@"DATA\SOLAR\NEBULA\other.ini"], session.DataFiles.Paths("nebula_files"), StringComparer.OrdinalIgnoreCase);
        Assert.Empty(session.DataFiles.OwnersOf(SceneWorkspace.CloudPath));
        session.Undo();
        Assert.Equal([SceneWorkspace.Li01Path], session.DataFiles.OwnersOf(SceneWorkspace.CloudPath), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The scope <c>owner</c> of the hole and loot rules (section 7 of the M3 design).</summary>
public sealed class OwnerScopeTests
{
    [Fact]
    public void AHoleLineReferencesTheZoneOfEachOwner()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li02Path, SceneWorkspace.Li02Text + "\n[Nebula]\nfile = solar\\nebula\\cloud.ini\nzone = Zone_li02\n");
        var session = ws.Session("act");

        var holes = session.Lists.ReferencesTo("zone_of_system", "Zone_hole", SceneWorkspace.Li01Path);
        // Two holes of the nebula file, one of the field file, and the loot zone of the field file.
        Assert.Equal([0, 0, 2], holes.Where(r => r.Key == "exclusion").Select(r => r.Line).Order());
        Assert.Single(holes, r => r.Key == "zone" && r.Section.Id.Type == "LootableZone" &&
                                  r.Section.Path.Equals(SceneWorkspace.RocksPath, StringComparison.OrdinalIgnoreCase));
        // The file of two systems names the zones of both owners.
        Assert.Equal(2, session.Lists.ReferencesTo("zone_of_system", "Zone_hole", SceneWorkspace.Li02Path).Count);
        Assert.Equal(DeleteAction.ClearHole, holes.First(r => r.Key == "exclusion").Rule.OnDelete);
    }

    /// <summary>The list check of a hole line accepts a zone of any owner, and a zone of no owner is an error of the
    /// active mod.</summary>
    [Fact]
    public void TheListCheckAcceptsAZoneOfAnyOwner()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li02Path, SceneWorkspace.Li02Text + "\n[Nebula]\nfile = solar\\nebula\\cloud.ini\nzone = Zone_li02\n");
        var session = ws.Session("act");
        var holes = new SectionRef(SceneWorkspace.CloudPath, new SectionIdentity("Exclusion Zones", null));

        session.Execute(EditCommand.SetValueAt(holes, "exclusion", 1, 0, new StringValue("Zone_li02")));
        Assert.Empty(session.CheckLists(holes));

        session.Execute(EditCommand.SetValueAt(holes, "exclusion", 1, 0, new StringValue("Zone_nothing")));
        var problem = Assert.Single(session.CheckLists(holes));
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains("The game skips this hole. Retail gives the lines of this hole to the hole before it.", problem.Message);
    }

    /// <summary>The reverse index of a nebula file follows a change of the file line of its owner.</summary>
    [Fact]
    public void TheIndexFollowsAChangeOfTheOwners()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        Assert.NotEmpty(session.Lists.ReferencesTo("zone_of_system", "Zone_ring", SceneWorkspace.Li01Path));

        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Li01("Nebula", "Zone_cloud"), "file",
            [@"solar\nebula\other.ini"]));

        Assert.DoesNotContain(session.Lists.ReferencesTo("zone_of_system", "Zone_ring", SceneWorkspace.Li01Path),
            r => r.Key == "exclusion");
    }
}

/// <summary>The nicknames of the objects and zones of all system files (section 8 of the M3 design).</summary>
public sealed class NicknamesTests
{
    [Fact]
    public void ANameIsTakenWithoutLetterCaseAndByItsFlHashInAllSystems()
    {
        var (first, second) = DataListsTests.FlHashPair();
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li02Path, SceneWorkspace.Li02Text + $"\n[zone]\nnickname = {first}\npos = 0, 0, 0\n");
        var session = ws.Session("act");
        var names = session.Nicknames;

        Assert.Equal(SceneWorkspace.Object("Li01_01"), names.Taken("LI01_01")!.Section);
        Assert.Equal(SceneWorkspace.Li02("zone", first), names.Taken(second)!.Section);
        Assert.Null(names.Taken("Li01_01", SceneWorkspace.Object("Li01_01")));
        // Objects and zones share one name space (decision D265).
        Assert.True(names.Taken("zone_ring")!.IsZone);
        Assert.Null(names.Taken("light1"));
        Assert.Equal(SceneWorkspace.Li01("LightSource", "light1"), names.LightTaken(SceneWorkspace.Li01Path, "LIGHT1"));
    }

    [Fact]
    public void AFileOutsideUniverseIniDoesNotCount()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(@"DATA\UNIVERSE\SYSTEMS\LI01\intro.ini", "[Object]\nnickname = intro_only\narchetype = station1\n");
        var session = ws.Session("act");

        Assert.Null(session.Nicknames.Taken("intro_only"));
    }

    [Fact]
    public void TheIndexFollowsEditsAndBuildsOnlyTheChangedFile()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        Assert.Null(session.Nicknames.Taken("new_one"));
        var builds = session.Table.Builds;

        session.Execute(EditCommand.AddSection(SceneWorkspace.Li02Path, "zone", [ListWorkspace.Line("nickname", "new_one")]));

        Assert.NotNull(session.Nicknames.Taken("new_one"));
        Assert.Equal(builds + 1, session.Table.Builds);
        session.Undo();
        Assert.Null(session.Nicknames.Taken("new_one"));
    }

    [Fact]
    public void FirstFreeAndCopyNameFollowTheNumberOfTheSource()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li02Path, SceneWorkspace.Li02Text + "\n[zone]\nnickname = li02_zone_1\n[zone]\nnickname = Li02_x_07\n");
        var names = ws.Session("act").Nicknames;

        Assert.Equal("li02_zone_2", names.FirstFree("li02_zone", 1));
        Assert.Equal("li01_station_1", names.FirstFree("li01_station", 1));
        // Decision D262: a copy keeps the stem and the count of digits, or gets _1.
        Assert.Equal("li02_zone_2", names.CopyName("li02_zone_1"));
        Assert.Equal("Li02_x_01", names.CopyName("Li02_x_07"));
        Assert.Equal("Li01_dock_1", names.CopyName("Li01_dock"));
        Assert.Equal("li02_zone_3", names.CopyName("li02_zone_1", new System.Collections.Generic.HashSet<string> { "li02_zone_2" }));
    }

    /// <summary>
    /// The index changes only the names of the part of the file that an edit changes. After each edit, undo and redo,
    /// it gives the names, the taken names and the collisions of an index that a new session builds. The edits are a
    /// create, a rename, a change of a line and a delete. A new name has the FLHash of another name. Then come undos and
    /// a redo. Then two zones get one nickname, and the first one gets a new nickname. The second zone does not change,
    /// but its identity does, so the index must not keep its old reference.
    /// </summary>
    [Fact]
    public void TheIndexAfterEachEditIsTheIndexOfANewBuild()
    {
        var (first, second) = DataListsTests.FlHashPair();
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var edits = new Action[]
        {
            () => session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "Object",
                [ListWorkspace.Line("nickname", "new_one"), ListWorkspace.Line("parent", "Li01_01")])),
            () => session.Execute(ScenePlans.Rename(session, SceneWorkspace.Object("Li01_dock"), "Li01_dock2").Command!),
            () => session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_child"), "parent", ["Li01_dock2"])),
            () => session.Execute(ScenePlans.Delete(session, [SceneWorkspace.Object("Li01_grandchild")]).Command!),
            () => session.Execute(EditCommand.AddSection(SceneWorkspace.Li02Path, "zone", [ListWorkspace.Line("nickname", first)])),
            () => session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "zone", [ListWorkspace.Line("nickname", second)])),
            () => session.Undo(),
            () => session.Undo(),
            () => session.Redo(),
            () => session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "zone", [ListWorkspace.Line("nickname", "twin")])),
            () => session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "zone", [ListWorkspace.Line("nickname", "twin")])),
            () => session.Execute(ScenePlans.Rename(session, SceneWorkspace.Zone("twin"), "twin_a").Command!),
            () => session.Undo(),
            () => session.Redo(),
        };

        AssertSameAsANewBuild(session, first, second);
        foreach (var edit in edits)
        {
            edit();
            AssertSameAsANewBuild(session, first, second);
        }
    }

    /// <summary>The index of the session and a new index of the same W give the same answers.</summary>
    private static void AssertSameAsANewBuild(EditSession session, params string[] more)
    {
        var index = session.Nicknames;
        var fresh = new Nicknames(session);
        Assert.Equal(fresh.All, index.All);
        foreach (var name in fresh.All.Select(n => n.Name).Concat(more).Append("Li01_dock").Append("no_such_name"))
        {
            Assert.Equal(fresh.Taken(name), index.Taken(name));
            Assert.Equal(fresh.Find(name, true), index.Find(name, true));
            Assert.Equal(fresh.Find(name, false), index.Find(name, false));
        }
        foreach (var zones in new[] { true, false })
            Assert.Equal(fresh.Collisions(zones).Select(g => g.ToList()), index.Collisions(zones).Select(g => g.ToList()));
    }

    /// <summary>A line that names a name that no section has adopts a new item with the name (review finding
    /// R29).</summary>
    [Fact]
    public void DanglingGivesTheLinesThatNameAMissingName()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_dock"), "parent", ["Li01_ghost"]));

        Assert.Equal(SceneWorkspace.Object("Li01_dock"), Assert.Single(session.Nicknames.Dangling("li01_ghost", SceneWorkspace.Li01Path)).Section);
        Assert.Empty(session.Nicknames.Dangling("Li01_01", SceneWorkspace.Li01Path));
    }
}

/// <summary>The runs of the holes of an <c>[Exclusion Zones]</c> section (section 11.4 of the M3 design).</summary>
public sealed class ExclusionHolesTests
{
    private static readonly Section Holes = Parse("""
        [Exclusion Zones]
        exclusion = z1
        fog_far = 1
        exclusion = Z2
        exclusion = z3
        zone_shell = s
        max_alpha = 1
        exclude = z2
        color = 1, 2, 3
        """);

    [Theory]
    [InlineData("z1", new[] { 0, 2 })]
    [InlineData("z3", new[] { 3, 3 })]
    [InlineData("z2", new[] { 2, 1, 6, 2 })]
    [InlineData("z9", new int[0])]
    public void TheRunsOfAZoneAreItsLinesUpToTheNextHole(string zone, int[] runs)
    {
        var result = ExclusionHoles.Runs(Holes, zone);

        Assert.Equal(runs, result.SelectMany(r => new[] { r.First, r.Count }));
    }

    private static Section Parse(string text) => PatchWriterTests.Text(text).Sections[0];
}

/// <summary>The text scan of the story mentions (section 13 of the M3 design).</summary>
public sealed class StoryMentionsTests
{
    [Fact]
    public void AMentionIsAWholeWordWithoutLetterCase()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");

        var mentions = session.StoryMentions("LI01_01");
        // The mission scripts are not in the scan, because the mission index has their references (decision D668 of
        // the M6b design).
        Assert.Equal([(SceneWorkspace.KnowledgeMapPath, 1)], mentions.Select(m => (m.Path, m.Count)), new PathCount());
        // Li01_01_Base is another word, and the system files do not count.
        Assert.Empty(session.StoryMentions("Li01_0"));
        Assert.DoesNotContain(session.StoryMentions("Zone_hole"), m => m.Path.EndsWith("cloud.ini", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ABinaryFileAndANicknameWithASpaceAreFound()
    {
        using var ws = SceneWorkspace.Create();
        // Files of the missions that are not scripts are in the scan.
        ws.WriteBaseBini(@"DATA\MISSIONS\bini.ini", "[Rumor]\nnickname = t2\nCnd = Li01_child\n");
        ws.WriteBase(@"DATA\MISSIONS\space.ini", "[Rumor]\nname = Br03_weapons platform_5, x\nname = abr03_weapons platform_5\n");
        var session = ws.Session("act");

        Assert.Contains(session.StoryMentions("li01_child"), m => m.Path.EndsWith("bini.ini", StringComparison.OrdinalIgnoreCase));
        var space = Assert.Single(session.StoryMentions("br03_WEAPONS platform_5"));
        Assert.Equal(1, space.Count);
    }

    /// <summary>The index belongs to the edit view. A save builds the edit view again, and the index follows it.</summary>
    [Fact]
    public void TheIndexFollowsTheEditViewOfASave()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        session.StartStoryMentions();
        Assert.Empty(session.StoryMentions("lo_new_loadout"));
        session.Execute(EditCommand.AddSection(ListWorkspace.SolarLoadoutsPath, "Loadout",
            [ListWorkspace.Line("nickname", "lo_new_loadout")]));
        // An unsaved edit is not in the scan.
        Assert.Empty(session.StoryMentions("lo_new_loadout"));

        Assert.True(session.Save().Saved);

        Assert.Single(session.StoryMentions("lo_new_loadout"));
    }

    private sealed class PathCount : System.Collections.Generic.IEqualityComparer<(string Path, int Count)>
    {
        public bool Equals((string Path, int Count) x, (string Path, int Count) y) =>
            x.Path.Equals(y.Path, StringComparison.OrdinalIgnoreCase) && x.Count == y.Count;

        public int GetHashCode((string Path, int Count) obj) => obj.Count;
    }
}
