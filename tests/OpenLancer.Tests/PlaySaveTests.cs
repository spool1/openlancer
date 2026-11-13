using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.Market;
using LibreLancer.Data.Schema.Goods;
using LibreLancer.Data.Schema.Save;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rules of the equipment and the cargo of a play save (decisions D47 and D56), on synthetic data: from a ship
/// package, and from a start save with a ship. The server mounts an <c>equip</c> line at its hardpoint and puts a
/// <c>cargo</c> line into the hold.
/// </summary>
public sealed class PlaySaveTests
{
    [Fact]
    public void AnAddonWithTheAmountOneGoesOntoItsHardpoint()
    {
        var player = Equip(new PackageAddon(Item("test_gun"), "HpWeapon01", 1));

        var line = Assert.Single(player.Equip);
        Assert.Equal(FLHash.CreateID("test_gun"), (uint)line.Item);
        Assert.Equal("HpWeapon01", line.Hardpoint);
        Assert.Empty(player.Cargo);
    }

    [Fact]
    public void AnAddonWithTheAmountOneAndNoHardpointGivesAnEquipLineWithoutOne()
    {
        var player = Equip(new PackageAddon(Item("test_engine"), null, 1));

        var line = Assert.Single(player.Equip);
        Assert.Equal(FLHash.CreateID("test_engine"), (uint)line.Item);
        Assert.True(string.IsNullOrEmpty(line.Hardpoint), $"The line has the hardpoint '{line.Hardpoint}'.");
        Assert.Empty(player.Cargo);
    }

    [Fact]
    public void AnAddonWithALargerAmountGoesIntoTheHold()
    {
        var player = Equip(
            new PackageAddon(Item("test_mine"), "HpMine01", 2),
            new PackageAddon(Item("test_missile"), "internal", 3));

        Assert.Empty(player.Equip);
        Assert.Equal([(FLHash.CreateID("test_mine"), 2), (FLHash.CreateID("test_missile"), 3)], Cargo(player));
    }

    [Fact]
    public void ACombinableGoodGoesIntoTheHoldAlsoWithTheAmountOne()
    {
        var player = Equip(new PackageAddon(Battery(), "internal", 1));

        Assert.Empty(player.Equip);
        Assert.Equal([(FLHash.CreateID("test_battery"), 1)], Cargo(player));
    }

    [Fact]
    public void ACombinableGoodOfTheNewCharacterLoadoutGoesIntoTheHold()
    {
        var player = OldSave();
        player.Equip.Add(new PlayerEquipment { Item = new HashValue("test_battery"), Hardpoint = "" });

        PlaySave.Equip(player, null, [Item("old_gun"), Item("old_battery"), Battery()]);

        // Without a package, the save keeps its ship, its other equipment and its cargo.
        Assert.Equal(FLHash.CreateID("old_ship"), (uint)player.ShipArchetype);
        var line = Assert.Single(player.Equip);
        Assert.Equal(FLHash.CreateID("old_gun"), (uint)line.Item);
        Assert.Equal([(FLHash.CreateID("old_battery"), 3), (FLHash.CreateID("test_battery"), 1)], Cargo(player));
    }

    [Fact]
    public void AnAddonWithAnAmountBelowOneGivesNoLine()
    {
        var player = Equip(new PackageAddon(Item("test_gun"), "HpWeapon01", 0),
            new PackageAddon(Item("test_mine"), "internal", -2));

        Assert.Empty(player.Equip);
        Assert.Empty(player.Cargo);
    }

    [Fact]
    public void ThePackageReplacesTheShipTheEquipmentAndTheCargoOfTheSave()
    {
        var player = Equip(new PackageAddon(Item("test_gun"), "HpWeapon01", 1));

        Assert.Equal(FLHash.CreateID("test_ship"), (uint)player.ShipArchetype);
        Assert.DoesNotContain(player.Equip, e => e.Item == new HashValue("old_gun"));
        Assert.DoesNotContain(player.Cargo, c => c.Item == new HashValue("old_battery"));
    }

    /// <summary>A package and the start save of the editor give the lines of one rule: the save from a package has the
    /// lines of <see cref="StartSaveShip.FromAddons"/> for its addons.</summary>
    [Fact]
    public void APackageGivesTheLinesOfTheAddonRuleOfTheStartSave()
    {
        PackageAddon[] addons =
        [
            new(Item("test_gun"), "HpWeapon01", 1), new(Item("test_mine"), "HpMine01", 2),
            new(Item("test_engine"), null, 1), new(Item("test_shield"), "HpShield01", 0),
        ];
        var rule = StartSaveShip.FromAddons("test_ship", addons.Select(a => (a.Equipment.Nickname, a.Hardpoint, a.Amount)));

        var player = Equip(addons);

        Assert.Equal(FLHash.CreateID(rule.Ship), (uint)player.ShipArchetype);
        Assert.Equal(rule.Equip.Select(e => (FLHash.CreateID(e.Item), e.Hardpoint)),
            player.Equip.Select(e => ((uint)e.Item, e.Hardpoint)));
        Assert.Equal(rule.Cargo.Select(c => (FLHash.CreateID(c.Item), c.Count)), Cargo(player));
    }

    [Fact]
    public void TheShipOfAStartSaveReplacesTheShipTheEquipmentAndTheCargoOfTheSave()
    {
        var player = OldSave();
        var start = StartSave(new PlayerEquipment { Item = new HashValue("test_gun"), Hardpoint = "HpWeapon01" },
            new PlayerCargo { Item = new HashValue("test_missile"), Count = 4 });

        PlaySave.GiveShip(player, Saved(start), null, [Item("test_gun"), Item("test_missile")]);

        Assert.Equal(FLHash.CreateID("test_ship"), (uint)player.ShipArchetype);
        var line = Assert.Single(player.Equip);
        Assert.Equal((FLHash.CreateID("test_gun"), "HpWeapon01"), ((uint)line.Item, line.Hardpoint));
        Assert.Equal([(FLHash.CreateID("test_missile"), 4)], Cargo(player));
    }

    /// <summary>The combinable rule (decision D56) also holds for an <c>equip</c> line of a start save. A combinable good
    /// with a stack in the hold adds one to the hold.</summary>
    [Fact]
    public void ACombinableGoodOfAStartSaveGoesIntoTheHold()
    {
        var player = OldSave();
        var start = StartSave(new PlayerEquipment { Item = new HashValue("test_battery"), Hardpoint = "" },
            new PlayerCargo { Item = new HashValue("test_battery"), Count = 2 });
        start.Equip.Add(new PlayerEquipment { Item = new HashValue("test_gun"), Hardpoint = "HpWeapon01" });

        PlaySave.GiveShip(player, Saved(start), null, [Item("test_gun"), Battery()]);

        var line = Assert.Single(player.Equip);
        Assert.Equal(FLHash.CreateID("test_gun"), (uint)line.Item);
        Assert.Equal([(FLHash.CreateID("test_battery"), 2), (FLHash.CreateID("test_battery"), 1)], Cargo(player));
    }

    /// <summary>The ship of a start save wins over the ship package of the play: the package has no effect.</summary>
    [Fact]
    public void TheShipOfAStartSaveWinsOverThePackage()
    {
        var player = OldSave();
        var start = StartSave(new PlayerEquipment { Item = new HashValue("test_gun"), Hardpoint = "HpWeapon01" },
            new PlayerCargo { Item = new HashValue("test_missile"), Count = 4 });
        var (package, equipment) = Package("other_ship", new PackageAddon(Item("other_gun"), "HpWeapon02", 1));
        equipment.Add(Item("test_gun"));
        equipment.Add(Item("test_missile"));

        PlaySave.GiveShip(player, Saved(start), package, equipment);

        Assert.Equal(FLHash.CreateID("test_ship"), (uint)player.ShipArchetype);
        var line = Assert.Single(player.Equip);
        Assert.Equal((FLHash.CreateID("test_gun"), "HpWeapon01"), ((uint)line.Item, line.Hardpoint));
        Assert.Equal([(FLHash.CreateID("test_missile"), 4)], Cargo(player));
    }

    /// <summary>A start save without <c>ship_archetype</c> has only the place. The package then gives the ship, and
    /// without a package the save keeps the ship of the new character.</summary>
    [Fact]
    public void AStartSaveWithoutAShipKeepsThePackageOrTheShipOfTheNewCharacter()
    {
        var withPackage = OldSave();
        var (package, equipment) = Package("other_ship", new PackageAddon(Item("other_gun"), "HpWeapon02", 1));

        PlaySave.GiveShip(withPackage, Saved(new SavePlayer()), package, equipment);

        Assert.Equal(FLHash.CreateID("other_ship"), (uint)withPackage.ShipArchetype);
        var line = Assert.Single(withPackage.Equip);
        Assert.Equal((FLHash.CreateID("other_gun"), "HpWeapon02"), ((uint)line.Item, line.Hardpoint));
        Assert.Empty(withPackage.Cargo);

        var withoutPackage = OldSave();
        PlaySave.GiveShip(withoutPackage, Saved(new SavePlayer()), null, [Item("old_gun"), Item("old_battery")]);

        Assert.Equal(FLHash.CreateID("old_ship"), (uint)withoutPackage.ShipArchetype);
        Assert.Equal(FLHash.CreateID("old_gun"), (uint)Assert.Single(withoutPackage.Equip).Item);
        Assert.Equal([(FLHash.CreateID("old_battery"), 3)], Cargo(withoutPackage));
    }

    /// <summary>The check of a start save ship names the ship, or the item of an <c>equip</c> or a <c>cargo</c> line, that
    /// the game data does not have.</summary>
    [Fact]
    public void TheCheckOfAStartSaveShipNamesTheShipOrTheItemThatTheGameDataDoesNotHave()
    {
        GameItemCollection<Ship> ships = [new Ship { Nickname = "test_ship", CRC = FLHash.CreateID("test_ship") }];
        GameItemCollection<Equipment> equipment = [Item("test_gun"), Item("test_missile")];
        var start = StartSave(new PlayerEquipment { Item = new HashValue("test_gun"), Hardpoint = "HpWeapon01" },
            new PlayerCargo { Item = new HashValue("test_missile"), Count = 4 });

        Assert.Null(PlaySave.StartShipProblem(ships, equipment, start));

        start.ShipArchetype = new HashValue("no_ship");
        Assert.Equal("The start save names the ship 'no_ship', which is not in the game data. The game cannot give the " +
                     "player a ship.", PlaySave.StartShipProblem(ships, equipment, start));

        start.ShipArchetype = new HashValue("test_ship");
        start.Equip.Add(new PlayerEquipment { Item = new HashValue("no_gun"), Hardpoint = "HpWeapon02" });
        Assert.Equal(Unknown("no_gun"), PlaySave.StartShipProblem(ships, equipment, start));

        start.Equip.RemoveAt(1);
        start.Cargo.Add(new PlayerCargo { Item = new HashValue("no_missile"), Count = 2 });
        Assert.Equal(Unknown("no_missile"), PlaySave.StartShipProblem(ships, equipment, start));
        Assert.Empty(SteLint.Problems(Unknown("no_missile")));

        static string Unknown(string item) =>
            $"The start save names the equipment '{item}', which is not in the game data. The game cannot give it to the " +
            "player.";
    }

    /// <summary>A start save with the <c>[Player]</c> section of the start save, docked at a base.</summary>
    private static PlayStartSave Saved(SavePlayer start) =>
        new(new SaveGame { Player = start }, new PlayStart.AtBase("test_base"));

    /// <summary>The <c>[Player]</c> section of a start save with the ship <c>test_ship</c>, one equip line and one cargo
    /// line.</summary>
    private static SavePlayer StartSave(PlayerEquipment equip, PlayerCargo cargo)
    {
        var start = new SavePlayer { ShipArchetype = new HashValue("test_ship") };
        start.Equip.Add(equip);
        start.Cargo.Add(cargo);
        return start;
    }

    /// <summary>A save with an old ship, an old gun and old cargo, after <see cref="PlaySave.Equip"/> with a package of
    /// the ship <c>test_ship</c>. The equipment of the data is the equipment of the addons.</summary>
    private static SavePlayer Equip(params PackageAddon[] addons)
    {
        var player = OldSave();
        var (package, equipment) = Package("test_ship", addons);

        PlaySave.Equip(player, package, equipment);
        return player;
    }

    /// <summary>A ship package of a ship with addons, and the equipment of the addons.</summary>
    private static (ShipPackage Package, GameItemCollection<Equipment> Equipment) Package(string ship,
        params PackageAddon[] addons)
    {
        var package = new ShipPackage("test_package", new Ship { Nickname = ship, CRC = FLHash.CreateID(ship) });
        package.Addons.AddRange(addons);
        var equipment = new GameItemCollection<Equipment>();
        foreach (var addon in addons)
            equipment.Add(addon.Equipment);
        return (package, equipment);
    }

    /// <summary>A save with the ship <c>old_ship</c>, the gun <c>old_gun</c> and 3 of <c>old_battery</c>.</summary>
    private static SavePlayer OldSave()
    {
        var player = new SavePlayer { ShipArchetype = new HashValue("old_ship") };
        player.Equip.Add(new PlayerEquipment { Item = new HashValue("old_gun"), Hardpoint = "HpWeapon03" });
        player.Cargo.Add(new PlayerCargo { Item = new HashValue("old_battery"), Count = 3 });
        return player;
    }

    private static Equipment Item(string nickname) => new() { Nickname = nickname, CRC = FLHash.CreateID(nickname) };

    /// <summary>The equipment <c>test_battery</c>, with a combinable good.</summary>
    private static Equipment Battery()
    {
        var battery = Item("test_battery");
        battery.Good = new ResolvedGood
        {
            Nickname = battery.Nickname,
            CRC = battery.CRC,
            Equipment = battery,
            Ini = new Good { Nickname = battery.Nickname, Combinable = true },
        };
        return battery;
    }

    private static (uint, int)[] Cargo(SavePlayer player) => player.Cargo.Select(c => ((uint)c.Item, c.Count)).ToArray();
}
