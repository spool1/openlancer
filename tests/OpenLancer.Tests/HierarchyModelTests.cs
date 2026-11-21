using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Solar;
using OpenLancer.Editor;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

public sealed class HierarchyModelTests
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Medium White",
        [2] = "Planet Manhattan",
        [3] = "Fort Bush",
        [4] = "New York -> Texas",
        [5] = "New York -> Alaska",
        [6] = "Jump Hole",
        [7] = "Trade Lane Ring",
        [10] = "New York",
    };

    private static SystemObject Object(string nickname, ArchetypeType type, int ids = 0) => new()
    {
        Nickname = nickname, IdsName = ids, Archetype = new Archetype { Nickname = type.ToString(), Type = type },
    };

    private static SystemObject Ring(string nickname, string? next, string? previous, int spaceName = 0)
    {
        var ring = Object(nickname, ArchetypeType.tradelane_ring, 7);
        ring.Dock = new DockAction { Kind = DockKinds.Tradelane, Target = next, TargetLeft = previous };
        ring.TradelaneSpaceName = spaceName;
        return ring;
    }

    private static StarSystem System(bool lights = true)
    {
        var field = SystemIndexTests.Zone("zone_field");
        return new StarSystem
        {
            Nickname = "li01",
            IdsName = 10,
            SourceFile = "",
            Objects =
            [
                Object("sun1", ArchetypeType.sun, 1),
                Object("planet1", ArchetypeType.planet, 2),
                Object("station1", ArchetypeType.station, 3),
                Object("ring_dock", ArchetypeType.docking_ring),
                Object("gate1", ArchetypeType.jump_gate, 4),
                Object("gate2", ArchetypeType.jump_gate, 5),
                Object("hole1", ArchetypeType.jump_hole, 6),
                Ring("lane_1", "lane_2", null, 2),
                Ring("lane_2", "lane_3", "lane_1"),
                Ring("lane_3", null, "lane_2", 3),
                Object("sat1", ArchetypeType.satellite),
                Object("buoy", ArchetypeType.non_targetable),
            ],
            Zones = [field, SystemIndexTests.Zone("zone_other")],
            AsteroidFields = [new AsteroidField { SourceFile = "", Zone = field }],
            LightSources = lights ? [new LightSource { Nickname = "light1", AttenuationCurveName = null }] : [],
        };
    }

    private static HierarchyModel Model(StarSystem? system = null)
    {
        system ??= System();
        return new HierarchyModel(new SystemIndex(system, system), IdText);
    }

    /// <summary>The text of a key from the text id of the item, as the game data gives it when W has no edit.</summary>
    private static string IdText(object item, string key) => Names.GetValueOrDefault(key switch
    {
        "tradelane_space_name" => ((SystemObject)item).TradelaneSpaceName,
        _ => ItemTexts.NameIds(item),
    }, "");

    private static HierarchyNode Group(HierarchyModel model, string id) => model.Root.Children.Single(c => c.Id == id);

    private static string[] RowNames(HierarchyModel model) => model.Rows.Select(r => r.Node.Name).ToArray();

    [Fact]
    public void PutsTheObjectsZonesAndLightsInGroupsWithCounts()
    {
        var model = Model();

        Assert.Equal("New York", model.Root.Name);
        Assert.Equal("li01", model.Root.Detail);
        Assert.Equal("", model.Root.CountText);
        Assert.Equal(["suns", "stations", "gates", "lanes", "satellites", "other", "fields", "zones", "lights"],
            model.Root.Children.Select(c => c.Id));
        Assert.Equal(["2", "2", "3", "3", "1", "1", "1", "2", "1"], model.Root.Children.Select(c => c.CountText));
    }

    [Fact]
    public void HidesAGroupWithoutItems()
    {
        Assert.DoesNotContain(Model(System(lights: false)).Root.Children, c => c.Id == "lights");
    }

    [Fact]
    public void ShowsATradeLaneAsOneNodeWithItsRingsInLaneOrder()
    {
        var lane = Group(Model(), "lanes").Children.Single();

        Assert.Equal("Planet Manhattan -> Fort Bush", lane.Name);
        Assert.Null(lane.Item);
        Assert.Equal(["lane_1", "lane_2", "lane_3"], lane.Children.Select(c => c.Detail));
    }

    [Fact]
    public void NamesALaneByTheNicknamesOfItsEndsWhenTheRingNamesAreTheSame()
    {
        var system = System();
        foreach (var ring in system.Objects.Where(o => o.Dock != null))
        {
            ring.TradelaneSpaceName = 0;
        }

        Assert.Equal("lane_1 -> lane_3", Group(Model(system), "lanes").Children.Single().Name);
    }

    [Fact]
    public void ShowsTheZonesInOneNodeForEachRole()
    {
        var zones = Group(Model(), "zones");

        Assert.Equal(["Field zones", "Other zones"], zones.Children.Select(c => c.Name));
    }

    /// <summary>
    /// The nebulae and the asteroid fields are scene items with top-level groups after "Other objects" and before
    /// "Zones" (decision D158 of the M22 design). A row shows the name of the zone, or else the file name, and the zone
    /// nickname. The id of a row is its prefix and the zone nickname, with "#k" for the k-th repeat.
    /// </summary>
    [Fact]
    public void NebulaeAndFieldsHaveTheirOwnGroups()
    {
        var system = System();
        var cloudZone = SystemIndexTests.Zone("zone_cloud");
        cloudZone.IdsName = 10;
        system.Zones.Add(cloudZone);
        var cloud = new Nebula { SourceFile = @"solar\nebula\cloud.ini", Zone = cloudZone };
        var repeat = new Nebula { SourceFile = @"solar\nebula\cloud_two.ini", Zone = cloudZone };
        system.Nebulae.AddRange([cloud, repeat]);
        var model = Model(system);

        var ids = model.Root.Children.Select(c => c.Id).ToList();
        Assert.Equal(ids.IndexOf("other") + 1, ids.IndexOf("nebulae"));
        Assert.Equal(ids.IndexOf("nebulae") + 1, ids.IndexOf("fields"));
        Assert.Equal(ids.IndexOf("fields") + 1, ids.IndexOf("zones"));
        var nebulae = Group(model, "nebulae");
        Assert.Equal(("Nebulae", ObjectKinds.Nebula), (nebulae.Name, nebulae.Kind));
        Assert.Equal(["n:zone_cloud", "n:zone_cloud#2"], nebulae.Children.Select(c => c.Id));
        Assert.All(nebulae.Children, c => Assert.Equal(("New York", "zone_cloud"), (c.Name, c.Detail)));
        Assert.Equal([cloud, repeat], nebulae.Children.Select(c => c.Item));

        // A zone without a name gives the file name without the extension.
        var field = Group(model, "fields").Children.Single();
        Assert.Equal(("f:zone_field", "zone_field", "zone_field"), (field.Id, field.Name, field.Detail));
        system.AsteroidFields[0] = new AsteroidField { SourceFile = @"solar\asteroids\li01_rock.ini", Zone = system.Zones[0] };
        Assert.Equal("li01_rock", Group(Model(system), "fields").Children.Single().Name);
    }

    /// <summary>A nebula of a zone that a field also uses has its row in "Nebulae", and the field in "Asteroid
    /// fields". The zone is in "Field zones" only, as before.</summary>
    [Fact]
    public void ANebulaOfASharedZoneIsInTheNebulae()
    {
        var system = System();
        var shared = system.AsteroidFields[0].Zone!;
        var nebula = new Nebula { SourceFile = "smog.ini", Zone = shared };
        system.Nebulae.Add(nebula);
        var model = Model(system);

        Assert.Same(nebula, Group(model, "nebulae").Children.Single().Item);
        Assert.Same(system.AsteroidFields[0], Group(model, "fields").Children.Single().Item);
        Assert.Equal(["Field zones", "Other zones"], Group(model, "zones").Children.Select(c => c.Name));
        Assert.True(model.Reveal(nebula));
    }

    [Fact]
    public void TypeWordsSelectTheItems()
    {
        var system = System();
        system.Nebulae.Add(new Nebula { SourceFile = "smog.ini", Zone = system.Zones[1] });
        var model = Model(system);

        model.SetSearch("t:nebula");
        Assert.Equal(["New York", "Nebulae", "smog"], RowNames(model));

        model.SetSearch("t:field");
        Assert.Equal(["New York", "Asteroid fields", "zone_field"], RowNames(model));

        // "t:zone" still selects the zones, so a user who searched the zones of fields uses it.
        model.SetSearch("t:zone");
        Assert.Equal(2, model.Root.Matches);
        Assert.Equal(1, HierarchyModel.TypeCounts(system)["nebula"]);
    }

    [Fact]
    public void ShowsTheSystemAndTheClosedGroupsFirst()
    {
        var model = Model();

        Assert.Equal(10, model.Rows.Count);
        Assert.Equal(0, model.Rows[0].Depth);
        Assert.All(model.Rows.Skip(1), r => Assert.Equal(1, r.Depth));
    }

    [Fact]
    public void SortsTheItemsByNameAndShowsTheNicknameOfAnItemWithoutName()
    {
        var model = Model();

        model.SetOpen(Group(model, "stations"), true);

        var items = model.Rows.Where(r => r.Depth == 2).Select(r => r.Node).ToArray();
        Assert.Equal(["Fort Bush", "ring_dock"], items.Select(n => n.Name));
        Assert.Equal(["station1", ""], items.Select(n => n.Detail));
    }

    [Fact]
    public void KeepsOnlyTheTypeOfATypeWordAndCountsTheMatches()
    {
        var model = Model();

        model.SetSearch("t:gate");

        Assert.True(model.IsSearching);
        Assert.Equal(["New York", "Gates and holes", "New York -> Alaska", "New York -> Texas"], RowNames(model));
        Assert.Equal("2 / 3", Group(model, "gates").CountText);
    }

    [Fact]
    public void MatchesTheNameOrTheNicknameWithoutCase()
    {
        var model = Model();

        model.SetSearch("MANHATTAN");
        Assert.Equal(["New York", "Suns and planets", "Planet Manhattan"], RowNames(model));

        model.SetSearch("Lane_2");
        Assert.Equal(["New York", "Trade lanes", "Planet Manhattan -> Fort Bush", "Trade Lane Ring"],
            RowNames(model));
    }

    [Fact]
    public void MatchesNothingForAnUnknownType()
    {
        var model = Model();

        model.SetSearch("t:spaceship");

        Assert.Equal(0, model.Root.Matches);
        Assert.Equal(["New York"], RowNames(model));
    }

    [Fact]
    public void KeepsTheOpenGroupsOfTheSearchApartFromTheNormalOpenGroups()
    {
        var model = Model();
        var gates = Group(model, "gates");
        model.SetSearch("t:gate");

        model.SetOpen(gates, false);
        Assert.Equal(["New York", "Gates and holes"], RowNames(model));

        model.SetSearch("");
        Assert.False(model.IsOpen(gates));
        model.SetSearch("t:gate");
        Assert.True(model.IsOpen(gates));
    }

    [Fact]
    public void OpensTheParentsOfAnItemSoThatItsRowShows()
    {
        var model = Model();
        var ring = model.Index!.ObjectsByNickname["lane_2"];
        Assert.Equal(-1, model.IndexOf(ring));

        Assert.True(model.Reveal(ring));

        var index = model.IndexOf(ring);
        Assert.Equal(3, model.Rows[index].Depth);
        Assert.Same(ring, model.Rows[index].Node.Item);
    }

    [Fact]
    public void DoesNotRevealAnItemThatTheSearchHides()
    {
        var model = Model();
        model.SetSearch("t:gate");

        Assert.False(model.Reveal(model.Index!.ObjectsByNickname["planet1"]));
        Assert.False(model.Reveal(new SystemObject { Nickname = "elsewhere" }));
    }

    [Fact]
    public void TypeCountsMatchTheSearchOfEachTypeWord()
    {
        var system = System();
        var counts = HierarchyModel.TypeCounts(system);
        Assert.Equal(1, counts["sun"]);
        Assert.Equal(2, counts["gate"]);
        Assert.Equal(1, counts["hole"]);
        Assert.Equal(3, counts["lane"]);
        Assert.Equal(1, counts["ring"]);
        Assert.Equal(2, counts["zone"]);
        Assert.Equal(1, counts["field"]);
        Assert.Equal(0, counts["nebula"]);
        Assert.Equal(1, counts["light"]);

        // The Contents links of the Inspector set this search, so each count must be the match count of the tree.
        var model = Model(system);
        foreach (var (word, count) in counts)
        {
            model.SetSearch($"t:{word}");
            Assert.Equal(count, model.Root.Matches);
        }
    }

    /// <summary>
    /// The names come from the text function, which reads W (section 14 of the design): the system row shows the
    /// <c>strid_name</c> of the system, an item its <c>ids_name</c>, for example a placeholder name of the active mod,
    /// and a lane the <c>tradelane_space_name</c> of its end rings.
    /// </summary>
    [Fact]
    public void TheNamesComeFromTheTextFunction()
    {
        var system = System();
        string Text(object item, string key) => (item, key) switch
        {
            (StarSystem, "strid_name") => "New New York",
            (SystemObject { Nickname: "station1" }, "ids_name") => "Water depot",
            (SystemObject { Nickname: "lane_1" }, "tradelane_space_name") => "Manhattan",
            (SystemObject { Nickname: "lane_3" }, "tradelane_space_name") => "Freeport 1",
            _ => IdText(item, key),
        };

        var model = new HierarchyModel(new SystemIndex(system, system), Text);

        Assert.Equal("New New York", model.Root.Name);
        Assert.Equal("Water depot", model.NodeOf(system.Objects.Single(o => o.Nickname == "station1"))!.Name);
        Assert.Equal("Manhattan -> Freeport 1", Group(model, "lanes").Children.Single().Name);
        model.SetSearch("water");
        Assert.Equal(1, model.Root.Matches);
    }

    /// <summary>
    /// A tree of the same system that the names or the index make again keeps the open groups and the search of the
    /// tree before it. A tree of another system starts with closed groups and no search.
    /// </summary>
    [Fact]
    public void ATreeOfTheSameSystemKeepsTheOpenGroupsAndTheSearch()
    {
        var system = System();
        var model = new HierarchyModel(new SystemIndex(system, system), IdText);
        model.SetOpen(Group(model, "stations"), true);
        model.SetSearch("t:lane");
        model.SetOpen(Group(model, "lanes"), false);

        var again = new HierarchyModel(new SystemIndex(system, system), (item, key) => "x" + IdText(item, key), model);

        Assert.Equal("t:lane", again.Search);
        Assert.False(again.IsOpen(Group(again, "lanes")));
        again.SetSearch("");
        Assert.True(again.IsOpen(Group(again, "stations")));
        Assert.Equal("xNew York", again.Root.Name);

        var other = System();
        var fresh = new HierarchyModel(new SystemIndex(other, other), IdText, model);
        Assert.Equal("", fresh.Search);
        Assert.False(fresh.IsOpen(Group(fresh, "stations")));
    }

    /// <summary>
    /// The panel keeps its tree until the index of the scene or a name changes. A new name of an item, for example a
    /// rename before the save, builds the tree again with the new name, and the tree keeps its open groups.
    /// </summary>
    [Fact]
    public void TheTreeBuildsAgainOnlyForANewIndexOrANewName()
    {
        var system = System();
        var index = new SystemIndex(system, system);
        var model = HierarchyModel.For(index, 1, IdText, null);
        model.SetOpen(Group(model, "stations"), true);

        Assert.Same(model, HierarchyModel.For(index, 1, (item, key) => "x" + IdText(item, key), model));

        var renamed = HierarchyModel.For(index, 2, (item, key) => "x" + IdText(item, key), model);
        Assert.NotSame(model, renamed);
        Assert.Equal("xNew York", renamed.Root.Name);
        Assert.True(renamed.IsOpen(Group(renamed, "stations")));

        var reindexed = HierarchyModel.For(new SystemIndex(system, system), 2, IdText, renamed);
        Assert.NotSame(renamed, reindexed);
        Assert.Equal("New York", reindexed.Root.Name);
    }

    /// <summary>The marks of the rows (decision D245 of the M3 design): a section that the active mod adds, a section
    /// that it changes, and a section of the lower layer.</summary>
    [Fact]
    public void TheMarksFollowTheSectionsOfTheActiveMod()
    {
        var section = new LibreLancer.Data.Ini.Section("Object");

        Assert.Equal(RowMark.None, HierarchyModel.MarkOf(null));
        Assert.Equal(RowMark.Added, HierarchyModel.MarkOf(new Data.Editing.SectionView(section, null, true, true)));
        Assert.Equal(RowMark.Changed, HierarchyModel.MarkOf(new Data.Editing.SectionView(section, null, false, true)));
        Assert.Equal(RowMark.None, HierarchyModel.MarkOf(new Data.Editing.SectionView(section, null, false, false)));
    }

    /// <summary>A create and a delete give the scene a new index, and the new tree has the row of the new item and no row
    /// of the deleted item. It keeps the open groups.</summary>
    [Fact]
    public void TheRowsFollowAnAddAndADelete()
    {
        var system = System();
        var model = HierarchyModel.For(new SystemIndex(system, system), 1, IdText, null);
        model.SetOpen(Group(model, "stations"), true);

        var added = Object("li01_station_1", ArchetypeType.station);
        system.Objects.Add(added);
        system.Objects.RemoveAll(o => o.Nickname == "gate1");
        var after = HierarchyModel.For(new SystemIndex(system, system), 1, IdText, model);

        Assert.NotNull(after.NodeOf(added));
        Assert.Contains(after.NodeOf(added)!, Group(after, "stations").Children);
        Assert.DoesNotContain(after.Rows, r => r.Node.Detail == "gate1" || r.Node.Name == "gate1");
        Assert.True(after.IsOpen(Group(after, "stations")));
    }

    /// <summary>
    /// The next tree of a system that has a new jump gate and the rings of a new lane: the gate is in the gates, and the
    /// lane is one node with its rings in lane order. The open groups stay. The test makes the items by hand. The
    /// retail test of the lanes proves that the scene makes such items from the edit view.
    /// </summary>
    [Fact]
    public void TheNextTreeShowsANewGateAndTheRingsOfANewLane()
    {
        var system = System();
        var model = HierarchyModel.For(new SystemIndex(system, system), 1, IdText, null);
        model.SetOpen(Group(model, "lanes"), true);

        var gate = Object("gate_new", ArchetypeType.jump_gate, 4);
        gate.Dock = new DockAction { Kind = DockKinds.Jump, Target = "li02", Exit = "gate_back" };
        system.Objects.AddRange([gate, Ring("new_1", "new_2", null, 2), Ring("new_2", "new_3", "new_1"),
            Ring("new_3", null, "new_2", 3)]);
        var after = HierarchyModel.For(new SystemIndex(system, system), 1, IdText, model);

        Assert.Contains(after.NodeOf(gate)!, Group(after, "gates").Children);
        var lanes = Group(after, "lanes").Children;
        Assert.Equal(2, lanes.Count);
        Assert.Contains(lanes, lane => lane.Children.Select(c => c.Detail).SequenceEqual(["new_1", "new_2", "new_3"]));
        Assert.True(after.IsOpen(Group(after, "lanes")));
    }

    /// <summary>The plain tree of ForList: a node without a search text matches its name and its detail as before. A
    /// node with a search text also matches a word of that text. A "t:" word matches nothing, and a new tree with the
    /// same identity keeps the open groups (section 4.4 of the cutscene categories design).</summary>
    [Fact]
    public void APlainTreeMatchesTheSearchTextOfANode()
    {
        static HierarchyModel Tree(HierarchyModel? previous)
        {
            var root = new HierarchyNode("root", ObjectKinds.Thn.Script, "Root");
            var group = new HierarchyNode("g", ObjectKinds.Thn.Script, "Group");
            group.Add(new HierarchyNode("a", ObjectKinds.Thn.Script, "alpha", "first", "a"));
            group.Add(new HierarchyNode("b", ObjectKinds.Thn.Script, "beta", "", "b", search: "hidden words\nharbor"));
            root.Add(group);
            return HierarchyModel.ForList(root, "list", previous);
        }

        var model = Tree(null);
        Assert.Equal(["Root", "Group"], RowNames(model));

        model.SetSearch("FIRST");
        Assert.Equal(["Root", "Group", "alpha"], RowNames(model));
        model.SetSearch("harbor hidden");
        Assert.Equal(["Root", "Group", "beta"], RowNames(model));
        Assert.Equal("1 / 2", Group(model, "g").CountText);
        model.SetSearch("t:camera");
        Assert.Equal(0, model.Root.Matches);

        model.SetSearch("");
        model.SetOpen(Group(model, "g"), true);
        var again = Tree(model);
        Assert.Equal(["Root", "Group", "alpha", "beta"], RowNames(again));
        Assert.Equal("", again.NodeOf("a")!.Search);
    }
}
