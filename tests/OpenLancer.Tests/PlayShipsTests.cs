using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The ship part of a start save from a ship package of the edit view (issue #48). The builder reads the addon
/// lines as the engine reads them when it loads the package.</summary>
public sealed class PlayShipsTests
{
    [Fact]
    public void APackageGivesItsShipAndTheLinesOfItsAddons()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");

        var ship = PlayShips.ShipPart(session, "S1_PACKAGE", out var problem);

        Assert.Null(problem);
        Assert.NotNull(ship);
        Assert.Equal("s1", ship.Ship);
        Assert.Equal([("eng1", "internal"), ("gun1", "HpWeapon01")], ship.Equip);
        Assert.Empty(ship.Cargo);
    }

    /// <summary>The addon rule gives the line of each amount. A line with two values has the amount 1. The engine
    /// ignores a line with one value, and it drops an addon whose equipment the data does not have. The builder does the
    /// same.</summary>
    [Fact]
    public void TheAddonLinesFollowTheRulesOfTheEngine()
    {
        using var ws = ItemWorkspace.Create();
        ws.WriteBase(ItemWorkspace.GoodsPath, """
            [Good]
            nickname = s1_hull
            category = shiphull
            ship = s1
            [Good]
            nickname = s1_package
            category = ship
            hull = s1_hull
            addon = eng1, internal
            addon = gun1, HpWeapon01, 1
            addon = gun1, HpWeapon02, 0
            addon = gun1_ammo, internal, 50
            addon = no_such_gun, HpWeapon03, 1
            addon = gun1
            """);
        var session = ws.Session("act");

        var ship = PlayShips.ShipPart(session, "s1_package", out var problem);

        Assert.Null(problem);
        Assert.NotNull(ship);
        Assert.Equal([("eng1", "internal"), ("gun1", "HpWeapon01")], ship.Equip);
        Assert.Equal([("gun1_ammo", 50)], ship.Cargo);
    }

    /// <summary>The builder reads the edit view, so a package that an unsaved edit adds gives its ship part.</summary>
    [Fact]
    public void APackageOfAnEditGivesItsShipPart()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(ItemWorkspace.GoodsPath, "Good",
        [
            ItemWorkspace.Line("nickname", new StringValue("s1_edit_package")),
            ItemWorkspace.Line("category", new StringValue("ship")),
            ItemWorkspace.Line("hull", new StringValue("s1_hull")),
            ItemWorkspace.Line("addon", new StringValue("gun1"), new StringValue("HpWeapon01"), new Int32Value(1)),
            ItemWorkspace.Line("addon", new StringValue("gun1_ammo"), new StringValue("internal"), new Int32Value(20)),
        ]));

        var ship = PlayShips.ShipPart(session, "s1_edit_package", out _);

        Assert.NotNull(ship);
        Assert.Equal("s1", ship.Ship);
        Assert.Equal([("gun1", "HpWeapon01")], ship.Equip);
        Assert.Equal([("gun1_ammo", 20)], ship.Cargo);
    }

    /// <summary>No package with the nickname, a good that is no package, and a package without a ship give their
    /// reasons.</summary>
    [Fact]
    public void APackageThatTheEditViewCannotResolveGivesTheReason()
    {
        using var ws = ItemWorkspace.Create();
        ws.WriteBase(ItemWorkspace.GoodsPath, """
            [Good]
            nickname = s1_hull
            category = shiphull
            ship = s1
            [Good]
            nickname = lost_package
            category = ship
            hull = no_such_hull
            addon = gun1, HpWeapon01, 1
            """);
        var session = ws.Session("act");

        foreach (var nickname in new[] { "no_such_package", "s1_hull" })
        {
            Assert.Null(PlayShips.ShipPart(session, nickname, out var problem));
            Assert.Equal($"The edit view has no ship package '{nickname}'. Play cannot give the player this ship.",
                problem);
            Assert.Empty(SteLint.Problems(problem!));
        }
        Assert.Null(PlayShips.ShipPart(session, "lost_package", out var noShip));
        Assert.Equal("The edit view has no ship of the ship package 'lost_package'. Play cannot give the player this " +
                     "ship.", noShip);
        Assert.Empty(SteLint.Problems(noShip!));
    }
}
