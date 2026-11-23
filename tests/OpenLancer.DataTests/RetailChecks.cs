using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema.Save;
using LibreLancer.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>Checks that more than one retail proof uses.</summary>
internal static class RetailChecks
{
    /// <summary>Planet Manhattan in New York, the first base of the story.</summary>
    public const string Manhattan = "li01_01_base";

    /// <summary>The install paths of universe.ini and of the file of each of its [system] sections, in file
    /// order.</summary>
    public static List<string> SystemFiles(EditSession session) =>
        session.Sections(session.UniversePath!)
            .Where(s => s.Id.Type.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(s => session.SystemPath(s.Id.Key!)!)
            .Prepend(session.UniversePath!)
            .ToList();

    /// <summary>The base folder holds the unchanged retail data, and each of its files is read-only.</summary>
    public static void AssertBaseIsUnchanged(string baseFolder)
    {
        Assert.Equal((DataDigest.RetailV1FileCount, DataDigest.RetailV1), DataDigest.Compute(baseFolder));
        Assert.All(Directory.EnumerateFiles(baseFolder, "*", SearchOption.AllDirectories),
            f => Assert.True(new FileInfo(f).IsReadOnly, f));
    }

    /// <summary>
    /// A new story character can buy a ship package at Manhattan: the purchase checks of the server
    /// (<c>BasesidePlayer.PurchaseShip</c>), without the resale value of the equipment.
    /// </summary>
    /// <param name="fs">The data that <paramref name="db"/> comes from.</param>
    public static void AssertNewPlayerCanBuy(GameItemDb db, FileSystem fs, string package, ITestOutputHelper output)
    {
        // The story starts from newplayer.fl. Mission 1 removes the ship after the intro, and it gives a Starflier
        // (ge_fighter) when the player accepts Juni's offer (m01a.ini, trigger mrp_accept).
        const string newPlayer = "EXE/newplayer.fl";
        var player = SaveGame.FromBytes(newPlayer, fs.ReadAllBytes(newPlayer)).Player!;
        var manhattan = db.Bases.Get(Manhattan)!;
        // The trade-in and the price of the ship read the market price of the ship at the base (fork commit OL-0080).
        var tradeIn = (long)TradeConstants.ShipResale(db.GetShipPrice(db.Ships.Get("ge_fighter")!, manhattan));
        var sold = manhattan.SoldShips.Single(s => s.Package.Nickname == package);
        var price = db.GetShipPrice(sold.Package.Ship, manhattan) +
                    sold.Package.Addons.Sum(a => (long)manhattan.GetUnitPrice(a.Equipment) * a.Amount);

        output.WriteLine($"Credits {player.Money}, trade-in {tradeIn}, package price {price}.");
        Assert.True(sold.Rank <= player.Rank, $"The ship needs rank {sold.Rank}.");
        Assert.True(player.Money + tradeIn >= price, $"The ship costs {price}.");
    }
}
