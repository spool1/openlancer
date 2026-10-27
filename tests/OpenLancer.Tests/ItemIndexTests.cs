using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A synthetic install with item files: a ship, a gun with its munition, goods and markets.</summary>
internal static class ItemWorkspace
{
    public const string ShipsPath = @"DATA\SHIPS\shiparch.ini";
    public const string WeaponsPath = @"DATA\EQUIPMENT\weapon_equip.ini";
    public const string MiscPath = @"DATA\EQUIPMENT\misc_equip.ini";
    public const string GoodsPath = @"DATA\EQUIPMENT\goods.ini";
    public const string WeaponGoodsPath = @"DATA\EQUIPMENT\weapon_good.ini";
    public const string MarketPath = @"DATA\EQUIPMENT\market_misc.ini";
    public const string LoadoutsPath = @"DATA\SHIPS\loadouts.ini";

    public const string FreelancerIni = """
        [Freelancer]
        data path = ..\data

        [Resources]
        DLL = InfoCards.dll

        [Data]
        ships = ships\shiparch.ini
        equipment = equipment\weapon_equip.ini
        equipment = equipment\misc_equip.ini
        goods = equipment\goods.ini
        goods = equipment\weapon_good.ini
        markets = equipment\market_misc.ini
        """;

    /// <summary>A workspace with the item files in the base folder, an empty active mod "act" and the profile.</summary>
    /// <param name="weapons">The text of weapon_equip.ini, or null for the default text.</param>
    public static TestWorkspace Create(string? weapons = null)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni);
        ws.WriteBase(ShipsPath, """
            [Ship]
            nickname = s1
            ids_name = 100
            ids_info = 200
            da_archetype = ships\s1.cmp
            mass = 100
            [CollisionGroup]
            obj = wing
            separable =
            [CollisionGroup]
            obj = tail
            [Simple]
            nickname = s1_debris
            """);
        if (weapons != null)
            ws.WriteBase(WeaponsPath, weapons);
        else
        {
            ws.WriteBase(WeaponsPath, """
                [Munition]
                nickname = gun1_ammo
                hull_damage = 10
                [Gun]
                nickname = gun1
                ids_name = 300
                ids_info = 301
                projectile_archetype = gun1_ammo
                [LOD]
                obj = gun1_lod1
                """);
        }
        ws.WriteBase(MiscPath, """
            [Engine]
            nickname = eng1
            ids_name = 400
            """);
        ws.WriteBase(GoodsPath, """
            [Good]
            nickname = s1_hull
            category = shiphull
            ship = s1
            price = 1000
            ids_name = 101
            [Good]
            nickname = s1_package
            category = ship
            hull = s1_hull
            addon = eng1, internal, 1
            addon = gun1, HpWeapon01, 1
            [Good]
            nickname = eng1
            equipment = eng1
            category = equipment
            price = 50
            """);
        ws.WriteBase(WeaponGoodsPath, """
            [Good]
            nickname = gun1
            equipment = gun1
            category = equipment
            ids_name = 300
            price = 500
            """);
        ws.WriteBase(MarketPath, """
            [BaseGood]
            base = b1
            marketgood = gun1, 0, -1, 10, 10, 0, 1
            marketgood = s1_package, 0, -1, 1, 1, 0, 1
            [BaseGood]
            base = b2
            marketgood = eng1, 0, -1, 10, 10, 0, 1
            """);
        ws.Mod("act", new());
        ws.WriteProfile("act");
        return ws;
    }

    /// <summary>A workspace of <see cref="Create"/> with a loadout file of the role "loadouts" in the base
    /// folder.</summary>
    /// <param name="loadouts">The text of the loadout file.</param>
    public static TestWorkspace WithLoadouts(string loadouts)
    {
        var ws = Create();
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni + "\nloadouts = ships\\loadouts.ini\n");
        ws.WriteBase(LoadoutsPath, loadouts);
        return ws;
    }

    public static SectionRef Ref(string path, string type, string? key, int ordinal = 0) =>
        new(path, new SectionIdentity(type, key, null, ordinal));

    public static SectionRef Ship(string nickname) => Ref(ShipsPath, "Ship", nickname);

    public static SectionRef Gun(string nickname) => Ref(WeaponsPath, "Gun", nickname);

    public static SectionRef Munition(string nickname) => Ref(WeaponsPath, "Munition", nickname);

    public static SectionRef Good(string path, string nickname) => Ref(path, "Good", nickname);

    public static SectionRef Market(int ordinal) => Ref(MarketPath, "BaseGood", null, ordinal);

    /// <summary>The first value of each line of a name, as text.</summary>
    public static string[] Firsts(EditSession session, SectionRef target, string name) =>
        session.Get(target)!.Lines(name).Select(l => l.Values[0].ToString()).ToArray();

    public static EntryView Line(string name, params ValueBase[] values) => new(name, values);
}

/// <summary>The item index of the edit session: item files, nicknames and the links between items.</summary>
public sealed class ItemIndexTests
{
    [Fact]
    public void TheItemFilesComeFromTheDataSectionOfFreelancerIni()
    {
        using var ws = ItemWorkspace.Create();
        ws.WriteBase(@"DATA\EQUIPMENT\unlisted_equip.ini", "[Gun]\nnickname = gun9");
        var session = ws.Session("act");

        Assert.Equal(["data\\equipment\\weapon_equip.ini", "data\\equipment\\misc_equip.ini"],
            session.List(ItemFileKind.Equipment).Select(r => r.Path.ToLowerInvariant()).Distinct());
        Assert.Equal([ItemWorkspace.Munition("gun1_ammo"), ItemWorkspace.Gun("gun1"),
            ItemWorkspace.Ref(ItemWorkspace.MiscPath, "Engine", "eng1")], session.List(ItemFileKind.Equipment));
        // A child section is not an item. A [Simple] of a ship file is a section of that kind of file.
        Assert.Equal([ItemWorkspace.Ship("s1"), ItemWorkspace.Ref(ItemWorkspace.ShipsPath, "Simple", "s1_debris")],
            session.List(ItemFileKind.Ships));
        Assert.Null(session.Find(ItemFileKind.Equipment, "gun9"));
        Assert.Equal(ItemWorkspace.Gun("gun1"), session.Find(ItemFileKind.Equipment, "GUN1"));
        Assert.Equal(ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "gun1"), session.Find(ItemFileKind.Goods, "gun1"));
        Assert.Equal([ItemWorkspace.Market(0), ItemWorkspace.Market(1)], session.List(ItemFileKind.Markets));
    }

    [Fact]
    public void TheRelationsFollowTheLinksOfTheEngine()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var hull = ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_hull");
        var package = ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_package");

        Assert.Equal([hull], session.Related(ItemWorkspace.Ship("s1"), ItemRelation.Hull));
        Assert.Equal([package], session.Related(ItemWorkspace.Ship("s1"), ItemRelation.Package));
        Assert.Equal([package], session.Related(hull, ItemRelation.Package));
        Assert.Equal([ItemWorkspace.Ship("s1")], session.Related(package, ItemRelation.Ship));
        Assert.Equal([ItemWorkspace.Ship("s1")], session.Related(hull, ItemRelation.Ship));
        Assert.Empty(session.Related(ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "gun1"), ItemRelation.Ship));
        Assert.Empty(session.Related(ItemWorkspace.Ship("s1"), ItemRelation.Ship));
        Assert.Equal([ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "gun1")],
            session.Related(ItemWorkspace.Gun("gun1"), ItemRelation.Good));
        Assert.Equal([ItemWorkspace.Munition("gun1_ammo")], session.Related(ItemWorkspace.Gun("gun1"), ItemRelation.Munition));
        Assert.Empty(session.Related(ItemWorkspace.Ship("s1"), ItemRelation.Good));
        Assert.Empty(session.Related(ItemWorkspace.Munition("gun1_ammo"), ItemRelation.Munition));

        var line = Assert.Single(session.MarketLines("GUN1"));
        Assert.Equal((ItemWorkspace.Market(0), "b1", 0), (line.Section, line.Base, line.Line));
        Assert.Equal(["gun1", "0", "-1", "10", "10", "0", "1"], line.Values.Select(v => v.ToString()));
        Assert.Equal(1, Assert.Single(session.MarketLines("s1_package")).Line);
    }

    [Fact]
    public void TheIndexShowsTheEditsAndTheirUndo()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var second = ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "s1_hull2");

        session.Execute(EditCommand.AddSection(ItemWorkspace.WeaponGoodsPath, "Good",
            [ItemWorkspace.Line("nickname", new StringValue("s1_hull2")), ItemWorkspace.Line("category", new StringValue("shiphull")),
             ItemWorkspace.Line("ship", new StringValue("s1"))]));

        // In load order: the engine takes the price of the ship from the last hull good.
        Assert.Equal([ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_hull"), second],
            session.Related(ItemWorkspace.Ship("s1"), ItemRelation.Hull));
        session.Undo();
        Assert.DoesNotContain(second, session.Related(ItemWorkspace.Ship("s1"), ItemRelation.Hull));
        Assert.Null(session.Find(ItemFileKind.Goods, "s1_hull2"));
    }

    [Fact]
    public void ANicknameThatTwoFilesOfAKindShareIsAmbiguous()
    {
        using var ws = ItemWorkspace.Create();
        ws.WriteBase(ItemWorkspace.MiscPath, "[Engine]\nnickname = gun1");
        var session = ws.Session("act");

        Assert.Null(session.Find(ItemFileKind.Equipment, "gun1"));
        var problem = Assert.Single(session.Check(ItemWorkspace.Gun("gun1")));
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Contains("2 sections of the equipment files have this nickname", problem.Message);
        // The good has the same nickname, but goods are another kind of file.
        Assert.NotNull(session.Find(ItemFileKind.Goods, "gun1"));
        // The sections stay editable, so that an edit can remove the second nickname.
        Assert.Null(session.Get(ItemWorkspace.Gun("gun1"))!.ReadOnlyReason);
    }

    [Fact]
    public void PartsAreTheSectionsOfOneItem()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var gunParts = new[]
        {
            ItemWorkspace.Gun("gun1"), ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "gun1"),
            ItemWorkspace.Munition("gun1_ammo"),
        };

        Assert.Equal(gunParts, session.Items.Parts(ItemWorkspace.Gun("gun1")));
        // A good belongs to the item of its equipment, a hull good and a package to the item of their ship.
        Assert.Equal(gunParts, session.Items.Parts(ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "gun1")));
        var shipParts = new[]
        {
            ItemWorkspace.Ship("s1"), ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_hull"),
            ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_package"),
        };
        Assert.Equal(shipParts, session.Items.Parts(ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_package")));
        Assert.Equal(shipParts, session.Items.Parts(ItemWorkspace.Good(ItemWorkspace.GoodsPath, "s1_hull")));
        Assert.Empty(session.Items.Parts(ItemWorkspace.Gun("nothing")));
    }
}
