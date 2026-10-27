using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A small synthetic install with the files of the roles of the data lists: solar archetypes, two systems with a jump
/// gate pair and a trade lane, loadouts in two files, equipment, ships, goods, a market, voices, curves, stars,
/// factions and a data-only file.
/// </summary>
internal static class ListWorkspace
{
    public const string UniversePath = @"DATA\UNIVERSE\universe.ini";
    public const string Li01Path = @"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini";
    public const string Li02Path = @"DATA\UNIVERSE\SYSTEMS\LI02\li02.ini";
    public const string SolarPath = @"DATA\SOLAR\solararch.ini";
    public const string ShipLoadoutsPath = @"DATA\SHIPS\loadouts.ini";
    public const string SolarLoadoutsPath = @"DATA\SOLAR\loadouts.ini";
    public const string EquipmentPath = @"DATA\EQUIPMENT\weapon_equip.ini";
    public const string ShipsPath = @"DATA\SHIPS\shiparch.ini";
    public const string GoodsPath = @"DATA\EQUIPMENT\goods.ini";
    public const string MarketPath = @"DATA\EQUIPMENT\market_misc.ini";
    public const string VoicesPath = @"DATA\AUDIO\voices.ini";
    public const string GroupsPath = @"DATA\initialworld.ini";
    public const string WeaponModPath = @"DATA\EQUIPMENT\WeaponModDB.ini";

    /// <summary>The objects of Li01: a station, a jump gate to Li02, two trade lane rings, a child object and a
    /// light.</summary>
    public const string Li01Text = """
        [Object]
        nickname = Li01_01
        archetype = station1
        loadout = lo_station
        reputation = li_n_grp
        base = Li01_01_Base
        voice = pilot_f
        [Object]
        nickname = Li01_to_Li02
        archetype = jumpgate
        goto = Li02, Li02_to_Li01, gate_tunnel_bretonia
        [Object]
        nickname = ring1
        archetype = ring
        next_ring = ring2
        [Object]
        nickname = ring2
        archetype = ring
        prev_ring = ring1
        [Object]
        nickname = Li01_child
        archetype = station1
        parent = Li01_01
        [LightSource]
        nickname = light1
        pos = 0, 0, 0
        atten_curve = curve1
        """;

    public const string FreelancerIni = $"""
        [Freelancer]
        data path = ..\data

        {TestWorkspace.ResourcesSection}

        [Data]
        solar = solar\solararch.ini
        universe = universe\universe.ini
        loadouts = ships\loadouts.ini
        loadouts = solar\loadouts.ini
        equipment = equipment\weapon_equip.ini
        ships = ships\shiparch.ini
        goods = equipment\goods.ini
        markets = equipment\market_misc.ini
        voices = audio\voices.ini
        igraph = igraph.ini
        stars = solar\stararch.ini
        """;

    /// <summary>A workspace with the files in the base folder, the empty mods "lower" and "act", and the profile.</summary>
    /// <param name="lowerFiles">The files of the mod "lower", which loads before "act".</param>
    /// <param name="activeFiles">The files of the active mod "act".</param>
    public static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null,
        Dictionary<string, string>? activeFiles = null)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni);
        ws.WriteBase(SolarPath, """
            [Solar]
            nickname = station1
            type = STATION
            DA_archetype = solar\station1.cmp
            docking_sphere = berth, HpDockMountA, 50
            loadout = lo_station
            [CollisionGroup]
            obj = part1
            [Solar]
            nickname = jumpgate
            type = JUMP_GATE
            [Solar]
            nickname = ring
            type = TRADELANE_RING
            """);
        ws.WriteBase(UniversePath, """
            [system]
            nickname = Li01
            file = systems\Li01\li01.ini
            [system]
            nickname = Li02
            file = systems\Li02\li02.ini
            [Base]
            nickname = Li01_01_Base
            system = Li01
            """);
        ws.WriteBase(Li01Path, Li01Text);
        ws.WriteBase(Li02Path, """
            [Object]
            nickname = Li02_to_Li01
            archetype = jumpgate
            goto = Li01, Li01_to_Li02, gate_tunnel_bretonia
            """);
        ws.WriteBase(ShipLoadoutsPath, """
            [Loadout]
            nickname = lo_station
            archetype = station1
            equip = gun1, HpWeapon01
            equip = gun1, HpWeapon02
            cargo = ammo1, 5
            [Loadout]
            nickname = lo_shared
            archetype = ship1
            """);
        ws.WriteBase(SolarLoadoutsPath, """
            [Loadout]
            nickname = lo_shared
            [Loadout]
            nickname = lo_solar
            """);
        ws.WriteBase(EquipmentPath, """
            [Munition]
            nickname = ammo1
            [Motor]
            nickname = motor1
            [Explosion]
            nickname = expl1
            [Munition]
            nickname = missile1
            motor = motor1
            explosion_arch = expl1
            [Gun]
            nickname = gun1
            projectile_archetype = ammo1
            """);
        ws.WriteBase(ShipsPath, """
            [Ship]
            nickname = ship1
            """);
        ws.WriteBase(GoodsPath, """
            [Good]
            nickname = gun1
            equipment = gun1
            category = equipment
            [Good]
            nickname = ship1_hull
            category = shiphull
            ship = ship1
            """);
        ws.WriteBase(MarketPath, """
            [BaseGood]
            base = Li01_01_Base
            marketgood = gun1, 0, -1, 10, 10, 0, 1
            """);
        ws.WriteBase(VoicesPath, """
            [Voice]
            nickname = pilot_f
            [Voice]
            extend = pilot_f
            [Voice]
            extend = other_voice
            """);
        ws.WriteBase(@"DATA\igraph.ini", """
            [IGraph]
            nickname = curve1
            type = FLOAT
            point = 0, 1
            """);
        ws.WriteBase(@"DATA\SOLAR\stararch.ini", """
            [star]
            nickname = sun1
            star_glow = glow1
            [star_glow]
            nickname = glow1
            """);
        ws.WriteBase(GroupsPath, """
            [Group]
            nickname = li_n_grp
            rep = 0.5, li_p_grp
            [Group]
            nickname = li_p_grp
            """);
        ws.WriteBase(WeaponModPath, """
            [WeaponType]
            nickname = W_Laser
            shield_mod = S_Graviton, 0.5
            shield_mod = S_Molecular, 1
            [WeaponType]
            nickname = W_Gun
            shield_mod = S_Graviton, 2
            """);
        ws.Mod("lower", lowerFiles ?? new());
        ws.Mod("act", activeFiles ?? new());
        ws.WriteProfile("lower", "act");
        return ws;
    }

    public static SectionRef Object(string path, string nickname) => new(path, new SectionIdentity("Object", nickname));

    public static SectionRef Li01(string nickname) => Object(Li01Path, nickname);

    public static SectionRef Li01Section(string type, string nickname) => new(Li01Path, new SectionIdentity(type, nickname));

    public static SectionRef Loadout(string path, string nickname) => new(path, new SectionIdentity("Loadout", nickname));

    public static EntryView Line(string name, params string[] values) =>
        new(name, values.Select(v => (ValueBase)new StringValue(v)).ToList());
}

/// <summary>
/// The files of the nebula and asteroid lists (section 11 of the M22 design), on top of <see cref="ListWorkspace"/>.
/// Li01 has a nebula and a field on one zone. <c>SOLAR\NEBULA</c> has a nebula file and a texture file, and
/// <c>SOLAR\ASTEROIDS</c> has an asteroid file and a nebula file. Another folder has one more nebula file.
/// </summary>
internal static class NebulaFiles
{
    public const string Cloud = @"solar\nebula\cloud.ini";
    public const string Rocks = @"solar\asteroids\rocks.ini";
    public const string FarCloud = @"solar\more\far_cloud.ini";
    public static readonly SectionRef Nebula = ListWorkspace.Li01Section("Nebula", "Zone_cloud");
    public static readonly SectionRef Asteroids = ListWorkspace.Li01Section("Asteroids", "Zone_cloud");

    public static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null,
        Dictionary<string, string>? activeFiles = null)
    {
        var ws = ListWorkspace.Create(lowerFiles, activeFiles);
        ws.WriteBase(ListWorkspace.Li01Path, ListWorkspace.Li01Text + $"""

            [Zone]
            nickname = Zone_cloud
            pos = 0, 0, 0
            shape = SPHERE
            size = 5000
            [Nebula]
            file = {Cloud}
            zone = Zone_cloud
            [Asteroids]
            file = {Rocks}
            zone = Zone_cloud
            """);
        ws.WriteBase(@"DATA\SOLAR\NEBULA\cloud.ini", "[Fog]\nfog_enabled = 1\ncolor = 10, 7, 30\n[Exterior]\nbit_radius = 300\n");
        ws.WriteBase(@"DATA\SOLAR\NEBULA\cloud_shapes.ini", "[Texture]\nfile = solar\\nebula\\cloud.txm\n[Shape]\ntexture_name = puff\n");
        ws.WriteBase(@"DATA\SOLAR\ASTEROIDS\rocks.ini", "[Field]\nfill_dist = 1250\n[Exclusion Zones]\nexclusion = Zone_cloud\n");
        ws.WriteBase(@"DATA\SOLAR\ASTEROIDS\lost_cloud.ini", "[Fog]\nfog_enabled = 1\n");
        ws.WriteBase(@"DATA\SOLAR\MORE\far_cloud.ini", "[Fog]\nfog_enabled = 1\n");
        return ws;
    }
}

/// <summary>The data lists of a session (section 20.1 item 4 of the M21 design).</summary>
public sealed class DataListsTests
{
    /// <summary>A files list with a folder holds only the files below its folder (section 11 of the M22 design). A value
    /// is a path below the data folder, in the letter case of the file.</summary>
    [Fact]
    public void AFilesListWithAFolderHoldsOnlyItsFolder()
    {
        using var ws = NebulaFiles.Create();
        var session = ws.Session("act");

        Assert.Equal([@"SOLAR\NEBULA\cloud.ini"], session.Lists.List("nebula_file")!.Entries.Select(e => e.Value));
        Assert.Equal([@"SOLAR\ASTEROIDS\rocks.ini"], session.Lists.List("asteroid_file")!.Entries.Select(e => e.Value));
        var cloud = session.Lists.List("nebula_file")!.Find(NebulaFiles.Cloud)!;
        Assert.Equal(("cloud", ValueOrigin.Base), (cloud.Name, cloud.Origin));
        // A file of another folder is not in the list, but it exists in the edit view.
        Assert.Null(session.Lists.List("nebula_file")!.Find(NebulaFiles.FarCloud));
        Assert.True(session.Lists.FileExists(NebulaFiles.FarCloud));
        Assert.False(session.Lists.FileExists(@"solar\nebula\nothing.ini"));
        Assert.False(session.Lists.FileExists(""));
    }

    /// <summary>A file is an entry only when each of its sections is a section of the class of the list. So the texture
    /// file of the nebula folder is no nebula file, and a nebula file in the asteroid folder is no asteroid file.</summary>
    [Fact]
    public void AFileOfAnotherClassIsNoEntry()
    {
        using var ws = NebulaFiles.Create(activeFiles: new()
        {
            [@"files\DATA\SOLAR\NEBULA\mod_cloud.ini"] = "[Exterior]\nbit_radius = 100\n[TexturePanels]\nfile = a.ini\n",
            [@"files\DATA\SOLAR\NEBULA\empty.ini"] = "",
        });
        var session = ws.Session("act");

        var nebulae = session.Lists.List("nebula_file")!;
        Assert.Equal([@"SOLAR\NEBULA\cloud.ini", @"SOLAR\NEBULA\mod_cloud.ini"],
            nebulae.Entries.Select(e => e.Value).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(ValueOrigin.ActiveMod, nebulae.Find(@"solar\nebula\MOD_CLOUD.ini")!.Origin);
        Assert.Null(nebulae.Find(@"solar\nebula\cloud_shapes.ini"));
        Assert.Null(nebulae.Find(@"solar\nebula\empty.ini"));
        Assert.Null(session.Lists.List("asteroid_file")!.Find(@"solar\asteroids\lost_cloud.ini"));
    }

    [Fact]
    public void ASectionsListHoldsTheNamesOfItsSectionTypesInLoadOrder()
    {
        using var ws = ListWorkspace.Create();
        var lists = ws.Session("act").Lists;

        var solars = lists.List("solar_archetype")!;
        Assert.Equal(["station1", "jumpgate", "ring"], solars.Entries.Select(e => e.Value));
        Assert.True(solars.Closed);
        var station = solars.Find("STATION1")!;
        Assert.Equal(new SectionRef(ListWorkspace.SolarPath, new SectionIdentity("Solar", "station1")), station.Section);
        Assert.Equal(ValueOrigin.Base, station.Origin);
        Assert.Equal(@"type = STATION, DA_archetype = solar\station1.cmp", station.Detail);
        Assert.Null(solars.Find("part1"));
        // A repeated name gives one entry. The last section gives it, as in the engine.
        var shared = lists.List("loadout")!.Find("lo_shared")!;
        Assert.Equal(ListWorkspace.SolarLoadoutsPath, shared.Section!.Path, ignoreCase: true);
        Assert.Equal(["lo_station", "lo_shared", "lo_solar"], lists.List("loadout")!.Entries.Select(e => e.Value));
    }

    [Fact]
    public void TheOtherSourceKindsGiveTheirEntries()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var lists = session.Lists;

        // A values list: the distinct values of value 0 of shield_mod. Its role is data only, so it is never closed.
        var shields = lists.List("shield_type")!;
        Assert.Equal(["S_Graviton", "S_Molecular"], shields.Entries.Select(e => e.Value));
        Assert.False(shields.Closed);
        // An enum list.
        var docks = lists.List("dock_sphere_type")!;
        Assert.True(docks.IsEnum);
        Assert.NotNull(docks.Find("berth"));
        // A list by nickname or extend.
        Assert.Equal(["pilot_f", "other_voice"], lists.List("voice")!.Entries.Select(e => e.Value));
        // Two lists in order: solar archetypes, then ships.
        var archetype = Assert.Single(lists.Rules(ListWorkspace.Loadout(ListWorkspace.ShipLoadoutsPath, "lo_station"), "archetype"));
        var both = lists.List(archetype, ListWorkspace.Loadout(ListWorkspace.ShipLoadoutsPath, "lo_station"), null)!;
        Assert.Equal("solar_archetype+ship", both.Id);
        Assert.NotNull(both.Find("station1"));
        Assert.NotNull(both.Find("ship1"));
    }

    [Fact]
    public void AScopedListHoldsTheSectionsOfOneFile()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var lists = session.Lists;
        var gate = ListWorkspace.Li01("Li01_to_Li02");
        var rules = lists.Rules(gate, "goto");
        var line = session.Get(gate)!.Lines("goto")[0].Values;

        // The scope "file": the objects of the file of the section.
        var next = Assert.Single(lists.Rules(ListWorkspace.Li01("ring1"), "next_ring"));
        var objects = lists.List(next, ListWorkspace.Li01("ring1"), null)!;
        Assert.NotNull(objects.Find("Li01_01"));
        Assert.Null(objects.Find("Li02_to_Li01"));
        // The scope "systemOf:0": the objects of the system that value 0 names.
        var exits = lists.List(rules[1], gate, line)!;
        Assert.NotNull(exits.Find("Li02_to_Li01"));
        Assert.Null(exits.Find("Li01_01"));
        // A system that does not exist: the list is not available, and only value 0 reports.
        Assert.Null(lists.List(rules[1], gate, [new StringValue("no_system"), new StringValue("x")]));
    }

    [Fact]
    public void EntriesOfTheLowerModAndTheActiveModHaveTheirOrigin()
    {
        using var ws = ListWorkspace.Create(lowerFiles: new()
        {
            [@"patches\DATA\SOLAR\loadouts.ini"] = "[Loadout]\nol.new = true\nnickname = lo_lower\n",
        });
        var session = ws.Session("act");

        var lower = session.Lists.List("loadout")!.Find("lo_lower")!;
        Assert.Equal((ValueOrigin.LowerMod, "lower"), (lower.Origin, lower.ModId));
        Assert.False(lower.Unsaved);

        session.Execute(EditCommand.AddSection(ListWorkspace.SolarLoadoutsPath, "Loadout",
            [ListWorkspace.Line("nickname", "lo_new")]));
        var added = session.Lists.List("loadout")!.Find("lo_new")!;
        Assert.Equal((ValueOrigin.ActiveMod, "act"), (added.Origin, added.ModId));
        Assert.True(added.Unsaved);

        Assert.True(session.Save().Saved);
        Assert.False(session.Lists.List("loadout")!.Find("lo_new")!.Unsaved);
    }

    [Fact]
    public void AListFollowsEditsUndoRedoAndSave()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var lists = session.Lists;
        Assert.Null(lists.List("loadout")!.Find("lo_new"));

        session.Execute(EditCommand.AddSection(ListWorkspace.SolarLoadoutsPath, "Loadout",
            [ListWorkspace.Line("nickname", "lo_new")]));
        Assert.NotNull(lists.List("loadout")!.Find("lo_new"));
        session.Undo();
        Assert.Null(lists.List("loadout")!.Find("lo_new"));
        session.Redo();
        Assert.NotNull(lists.List("loadout")!.Find("lo_new"));
        Assert.True(session.Save().Saved);
        Assert.NotNull(lists.List("loadout")!.Find("lo_new"));
        // An edit of another file keeps the list.
        var list = lists.List("loadout");
        session.Execute(EditCommand.SetLinesFromText(ListWorkspace.Li01("Li01_01"), "pos", ["1, 2, 3"]));
        Assert.Same(list, lists.List("loadout"));
    }

    /// <summary>A list is available only when its files have a file in the edit view (section 8.3). So a synthetic
    /// profile without the files of a role gets no list check.</summary>
    [Fact]
    public void AProfileWithoutTheFilesOfARoleHasNoList()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var depot = SystemWorkspace.Li01Section("Object", "Li01_depot");

        Assert.Null(session.Lists.List("solar_archetype"));
        Assert.Null(session.Lists.List("loadout"));
        Assert.NotNull(session.Lists.List("system"));
        session.Execute(EditCommand.SetLinesFromText(depot, "archetype", ["no_such_archetype"]));
        Assert.Empty(session.CheckLists(depot));
    }

    /// <summary>The engine adds the entries of a hash list to a collection that throws for two names with one FLHash.
    /// The test finds such a pair by search, so it holds no magic names.</summary>
    [Fact]
    public void AHashListFindsTheNamesWithOneFlHash()
    {
        var (first, second) = FlHashPair();
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.AddSection(ListWorkspace.ShipsPath, "Ship", [ListWorkspace.Line("nickname", first)]));
        session.Execute(EditCommand.AddSection(ListWorkspace.ShipsPath, "Ship", [ListWorkspace.Line("nickname", second)]));

        var collision = Assert.Single(session.Lists.List("ship")!.Collisions);
        Assert.Equal([first, second], collision);
        var problem = Assert.Single(session.CheckLists(new SectionRef(ListWorkspace.ShipsPath, new SectionIdentity("Ship", second))));
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Contains($"has the FLHash of '{first}'", problem.Message);
        Assert.Contains(problem, session.SaveProblems());
        Assert.False(session.Save().Saved);
    }

    /// <summary>Two short names with one FLHash.</summary>
    internal static (string First, string Second) FlHashPair()
    {
        var seen = new Dictionary<uint, string>();
        for (var i = 0; ; i++)
        {
            var name = $"x{i}";
            if (seen.TryGetValue(FLHash.CreateID(name), out var other))
                return (other, name);
            seen[FLHash.CreateID(name)] = name;
        }
    }
}

/// <summary>The reverse index of the lists (section 20.1 item 5 of the M21 design).</summary>
public sealed class ReferencesToTests
{
    [Fact]
    public void ReferencesToFindsEachLineThatNamesAnEntry()
    {
        using var ws = ListWorkspace.Create();
        var lists = ws.Session("act").Lists;

        var loadout = lists.ReferencesTo("loadout", "LO_STATION");
        Assert.Equal(2, loadout.Count);
        Assert.Contains(loadout, r => r.Section == ListWorkspace.Li01("Li01_01") && r.Key == "loadout");
        Assert.Contains(loadout, r => r.Section == new SectionRef(ListWorkspace.SolarPath,
            new SectionIdentity("Solar", "station1")) && r.Key == "loadout");
        // The lines of a multiline key, each with its position.
        var gun = lists.ReferencesTo("equipment", "gun1");
        Assert.Equal([(0, "equip"), (1, "equip"), (0, "equipment")],
            gun.Select(r => (r.Line, r.Key)).OrderBy(r => r.Key).ThenBy(r => r.Line));
        Assert.Equal(gun.Count, lists.CountReferences("equipment", "gun1"));
        // A market line names the good, not the equipment.
        Assert.Equal("marketgood", Assert.Single(lists.ReferencesTo("good", "gun1")).Key);
        // A value of a line with more than one value: value 0 of goto names the system.
        var system = Assert.Single(lists.ReferencesTo("system", "Li02"), r => r.Key == "goto");
        Assert.Equal((0, ListWorkspace.Li01("Li01_to_Li02")), (system.Index, system.Section));
        Assert.Single(lists.ReferencesTo("faction", "li_p_grp"));
        Assert.Single(lists.ReferencesTo("voice", "pilot_f"));
        // Each reference carries its rule, so M3 can read its effect.
        Assert.Equal(RuleEffect.Degrade, loadout[0].Rule.Effect);
    }

    /// <summary>
    /// A change of one referrer file changes only the keys of its part in the merged index. A part takes the references
    /// of each section that did not change from its older part. After each edit, undo and redo, the index of the
    /// session gives the references of an index that a new build makes.
    /// </summary>
    [Fact]
    public void TheMergedIndexAfterEachEditIsTheIndexOfANewBuild()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var edits = new Action[]
        {
            () => session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "Object",
                [ListWorkspace.Line("nickname", "new_one"), ListWorkspace.Line("parent", "Li01_01"),
                    ListWorkspace.Line("loadout", "lo_station")])),
            () => session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_child"), "parent", ["Li01_dock"])),
            () => session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_01"), "loadout", ["lo_other"])),
            () => session.Execute(ScenePlans.Delete(session, [SceneWorkspace.Object("Li01_grandchild")]).Command!),
            () => session.Execute(EditCommand.AddSection(SceneWorkspace.Li02Path, "zone", [ListWorkspace.Line("nickname", "z2")])),
            () => session.Undo(),
            () => session.Undo(),
            () => session.Redo(),
        };

        AssertSameAsANewBuild(session);
        foreach (var edit in edits)
        {
            edit();
            AssertSameAsANewBuild(session);
        }
    }

    /// <summary>The reverse index of the session and a new index of the same W give the same references.</summary>
    private static void AssertSameAsANewBuild(EditSession session)
    {
        var lists = session.Lists;
        var fresh = new DataLists(session, lists.RuleData);
        foreach (var path in new[] { SceneWorkspace.Li01Path, SceneWorkspace.Li02Path })
        {
            foreach (var name in session.Sections(path).Select(s => s.Id.Key).OfType<string>().Append("new_one").Append("z2"))
            {
                foreach (var list in new[] { "object_of_system", "zone_of_system" })
                    Assert.Equal(fresh.ReferencesTo(list, name, path), lists.ReferencesTo(list, name, path));
            }
        }
        foreach (var (list, value) in new[] { ("loadout", "lo_station"), ("loadout", "lo_other"), ("faction", "li_n_grp"),
                     ("system", "Li02"), ("object_of_system", "Li01_01") })
        {
            Assert.Equal(fresh.ReferencesTo(list, value).OrderBy(r => r.Section.ToString()).ThenBy(r => r.Key),
                lists.ReferencesTo(list, value).OrderBy(r => r.Section.ToString()).ThenBy(r => r.Key));
        }
    }

    /// <summary>A scoped reference resolves its scope, so the exit object of a jump gate finds the partner
    /// gate.</summary>
    [Fact]
    public void AScopedReferenceFindsThePartnerGate()
    {
        using var ws = ListWorkspace.Create();
        var lists = ws.Session("act").Lists;

        var partner = Assert.Single(lists.ReferencesTo("object_of_system", "Li01_to_Li02", ListWorkspace.Li01Path));
        Assert.Equal((ListWorkspace.Object(ListWorkspace.Li02Path, "Li02_to_Li01"), "goto", 1),
            (partner.Section, partner.Key, partner.Index));
        Assert.Equal(ListWorkspace.Li01("ring2"),
            Assert.Single(lists.ReferencesTo("object_of_system", "ring1", ListWorkspace.Li01Path)).Section);
        Assert.Empty(lists.ReferencesTo("object_of_system", "Li01_to_Li02", ListWorkspace.Li02Path));
    }

    [Fact]
    public void TheIndexFollowsEditsAndUndo()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var lists = session.Lists;

        session.Execute(EditCommand.SetLinesFromText(ListWorkspace.Li01("ring1"), "loadout", ["lo_station"]));
        Assert.Equal(3, lists.CountReferences("loadout", "lo_station"));
        session.Undo();
        Assert.Equal(2, lists.CountReferences("loadout", "lo_station"));
    }
}

/// <summary>The entries that the active mod removes (section 20.1 item 6 of the M21 design).</summary>
public sealed class RemovedEntriesTests
{
    [Fact]
    public void AnOlDeletePatchRemovesALowerEntry()
    {
        using var ws = ListWorkspace.Create(activeFiles: new()
        {
            [@"patches\DATA\SHIPS\loadouts.ini"] = "[Loadout]\nnickname = lo_station\nol.delete = true\n",
        });
        var session = ws.Session("act");

        var removed = Assert.Single(session.Lists.Removed());
        Assert.Equal(("loadout", "lo_station"), (removed.List, removed.Value));
        Assert.Equal(ListWorkspace.Loadout(ListWorkspace.ShipLoadoutsPath, "lo_station"), removed.LowerSection);
        Assert.EndsWith(@"patches\DATA\SHIPS\loadouts.ini", removed.ModPath);
        // The object of the base data that names it is an error of the active mod.
        var problem = Assert.Single(session.Lists.Problems(ListWorkspace.Li01("Li01_01")));
        Assert.Equal(ListProblemKind.Removed, problem.Kind);
        Assert.True(problem.IsError);
        Assert.Contains("the active mod removes the loadout 'lo_station'", problem.Diagnostic.Message);
        Assert.Contains(session.SaveProblems(), p => p.Target == ListWorkspace.Li01("Li01_01") && p.Key == "loadout");
    }

    /// <summary>A scoped list with a role reads the files of its role: a delete in a mission script removes a mission
    /// trigger, and a delete in a system file removes no mission entry (section 14.1 of the M6a design).</summary>
    [Fact]
    public void AScopedListWithARoleReadsTheFilesOfItsRole()
    {
        const string script = @"DATA\MISSIONS\M01A\m01a.ini";
        using var ws = ListWorkspace.Create(activeFiles: new()
        {
            [@"patches\" + script] = "[Trigger]\nnickname = t_end\nol.delete = true\n",
            [@"patches\" + ListWorkspace.Li01Path] = "[Object]\nnickname = Li01_child\nol.delete = true\n",
        });
        ws.WriteBase(script, MissionTestData.Story);
        var session = ws.Session("act");

        var removed = session.Lists.Removed();

        Assert.Contains(removed, r => (r.List, r.Value) == ("mission_trigger", "t_end"));
        Assert.DoesNotContain(removed, r => r.List.StartsWith("mission_") && r.Value == "Li01_child");
        Assert.All(removed.Where(r => r.List.StartsWith("mission_")),
            r => Assert.Equal(script, r.LowerSection.Path, ignoreCase: true));
    }

    [Fact]
    public void ADeleteRemovesTheEntryAndAnUndoBringsItBack()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(ListWorkspace.Loadout(ListWorkspace.SolarLoadoutsPath, "lo_solar")));
        Assert.Equal(["lo_solar"], session.Lists.Removed().Select(r => r.Value));
        Assert.Equal(ListWorkspace.SolarLoadoutsPath, session.Lists.Removed()[0].ModPath, ignoreCase: true);
        session.Undo();
        Assert.Empty(session.Lists.Removed());
    }

    /// <summary>A name that another file of the list still gives is not removed.</summary>
    [Fact]
    public void ANameThatAnotherFileGivesIsNotRemoved()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(ListWorkspace.Loadout(ListWorkspace.SolarLoadoutsPath, "lo_shared")));

        Assert.Empty(session.Lists.Removed());
        Assert.NotNull(session.Lists.List("loadout")!.Find("lo_shared"));
    }

    /// <summary>A whole file of the active mod without the entry removes it. W has the lower data of such a file, so
    /// the lists read it as the edit view gives it.</summary>
    [Fact]
    public void AWholeFileWithoutTheEntryRemovesIt()
    {
        using var ws = ListWorkspace.Create(activeFiles: new()
        {
            [@"files\DATA\SOLAR\loadouts.ini"] = "[Loadout]\nnickname = lo_shared\n[Loadout]\nnickname = lo_whole\n",
        });
        var session = ws.Session("act");

        Assert.Equal(["lo_solar"], session.Lists.Removed().Select(r => r.Value));
        var whole = session.Lists.List("loadout")!.Find("lo_whole")!;
        Assert.Equal(ValueOrigin.ActiveMod, whole.Origin);
    }

    /// <summary>The editor renames only a section that the active mod adds. Its old name was never in the lower
    /// layer, so it is not a removed entry. A line that names the old name is outside the list, as Unity shows a
    /// missing reference.</summary>
    [Fact]
    public void ARenameOfAnAddedSectionRemovesNoLowerEntry()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(ListWorkspace.SolarLoadoutsPath, "Loadout",
            [ListWorkspace.Line("nickname", "lo_new")]));
        session.Execute(EditCommand.SetLinesFromText(ListWorkspace.Li01("ring1"), "loadout", ["lo_new"]));

        session.Execute(EditCommand.Rename(ListWorkspace.Loadout(ListWorkspace.SolarLoadoutsPath, "lo_new"), "lo_renamed"));

        Assert.Empty(session.Lists.Removed());
        Assert.Equal(ListProblemKind.Missing, Assert.Single(session.Lists.Problems(ListWorkspace.Li01("ring1"))).Kind);
    }

    /// <summary>
    /// The files source of the list of the cutscenes (decision D136): each .thn file of the edit view, as a path below
    /// the data folder in the letter case of the file, and each new cutscene of W. The name is the file name without its
    /// extension. An undo of New cutscene takes the new entry away.
    /// </summary>
    [Fact]
    public void TheCutsceneListHoldsTheThnFilesOfTheEditViewAndTheNewCutscenesOfW()
    {
        using var ws = RoomWorkspace.Create(lowerFiles: new()
        {
            [@"files\DATA\MODS\lower\CUTSCENES\low.thn"] = "duration = 1\nentities = {}\nevents = {}\n",
        });
        var session = ws.Session("act");

        var list = session.Lists.List("cutscene")!;
        Assert.True(list.Closed);
        Assert.Equal(["Li_01_bar_ambi_int_01", "Li_01_bar_ambi_int_s", "Li_01_bar_enter_01", "Li_01_bar_hardpoint_01",
            "Li_01_deck_hardpoint_01", "Li_01_deck_land_01", "Li_01_deck_launch_01", "low"],
            list.Entries.Select(e => e.Name!).Order(StringComparer.OrdinalIgnoreCase));
        var bar = list.Find(@"Scripts\Bases\Li_01_bar_hardpoint_01.thn")!;
        Assert.Equal(@"SCRIPTS\BASES\Li_01_bar_hardpoint_01.thn", bar.Value);
        Assert.Equal((ValueOrigin.Base, (string?)null, false), (bar.Origin, bar.ModId, bar.Unsaved));
        Assert.Equal((ValueOrigin.LowerMod, "lower"), (list.Find(@"MODS\lower\CUTSCENES\low.thn")!.Origin,
            list.Find(@"MODS\lower\CUTSCENES\low.thn")!.ModId));

        session.Execute(Data.Thn.ThnEdits.NewCutscene(@"DATA\MODS\act\CUTSCENES\bar_shot.thn",
            Data.Thn.ThnTemplates.NewScript()));
        var added = session.Lists.List("cutscene")!.Find(@"mods/ACT/cutscenes/BAR_SHOT.thn")!;
        Assert.Equal((@"MODS\act\CUTSCENES\bar_shot.thn", "bar_shot", ValueOrigin.ActiveMod, true),
            (added.Value, added.Name, added.Origin, added.Unsaved));

        session.Undo();
        Assert.Null(session.Lists.List("cutscene")!.Find(@"MODS\act\CUTSCENES\bar_shot.thn"));
    }

    /// <summary>The values of the list of the cutscenes compare as paths below the data folder: without letter case,
    /// with either separator, and without empty parts or "." parts, as the engine finds the files.</summary>
    [Theory]
    [InlineData(@"Scripts\Bases\Li_01_bar_enter_01.thn")]
    [InlineData(@"scripts/bases/LI_01_BAR_ENTER_01.THN")]
    [InlineData(@"Scripts\\Bases\.\Li_01_bar_enter_01.thn")]
    [InlineData(@" Scripts\Bases\Li_01_bar_enter_01.thn ")]
    public void TheValuesOfTheCutsceneListCompareAsPaths(string value)
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal("Li_01_bar_enter_01", session.Lists.List("cutscene")!.Find(value)?.Name);
    }

    /// <summary>
    /// The scene rule reads the value that the engine reads, by its place: value 1, or value 2 after <c>all</c>,
    /// whatever its text (RoomInfo.cs:31-45). A rule that took the first value that ends with .thn would read the
    /// script of <c>all, x.thn, ambient</c>, where the engine reads <c>ambient</c>, and no value of a script without the
    /// .thn ending.
    /// </summary>
    [Theory]
    [InlineData("all, ambient, x.thn", 2)]
    [InlineData("ambient, x.thn, TRAFFIC_PRIORITY", 1)]
    [InlineData("ALL, ambient, x.thn, TRAFFIC_PRIORITY", 2)]
    [InlineData("all, x.thn, ambient", 2)]
    [InlineData(@"ambient, Scripts\x", 1)]
    [InlineData("all, ambient", -1)]
    [InlineData("x.thn", -1)]
    public void TheSceneRuleReadsTheValueByItsPlace(string line, int index)
    {
        var rule = ListRules.Retail.Rules("room", "Room_Info", "scene").Single();
        var entry = new LibreLancer.Data.Ini.Entry(new Section("Room_Info"), "scene");
        foreach (var value in line.Split(','))
            entry.Add(new StringValue(value.Trim()));

        Assert.True(rule.Scene);
        Assert.Equal(index, rule.IndexIn(entry));
        // The values of a line of the Inspector give the same place, so a pick replaces the value that the rule reads.
        Assert.Equal(index, rule.IndexIn(entry.ToList()));
        Assert.Equal(0, ListRules.Retail.Rules("room", "Room_Info", "set_script").Single().IndexIn(entry));
    }

    /// <summary>The reverse index of the list of the cutscenes holds the place of the value of each scene line.</summary>
    [Fact]
    public void TheReferencesOfAScriptHoldThePlaceOfTheSceneValue()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");

        var all = Assert.Single(session.Lists.ReferencesTo("cutscene", @"Scripts\Bases\Li_01_bar_ambi_int_01.thn"));
        Assert.Equal(("scene", 0, 2), (all.Key, all.Line, all.Index));
        // The line spells the path in another way than the file.
        var priority = Assert.Single(session.Lists.ReferencesTo("cutscene", @"SCRIPTS\BASES\Li_01_bar_ambi_int_s.thn"));
        Assert.Equal(("scene", 1, 1), (priority.Key, priority.Line, priority.Index));
        var start = Assert.Single(session.Lists.ReferencesTo("cutscene", @"Scripts\Bases\Li_01_bar_enter_01.thn"));
        Assert.Equal("start_script", start.Key);
        Assert.Empty(session.Lists.Problems(RoomWorkspace.Section(session, RoomWorkspace.BarPath, "Room_Info")));
    }
}
