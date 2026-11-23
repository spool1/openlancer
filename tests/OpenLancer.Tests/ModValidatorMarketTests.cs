using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Goods;
using OpenLancer.Data;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The market rule of the validator (decision D511 of the console filter design). Retail reads the sixth value of a
/// <c>MarketGood</c> line as the transaction type: 0 buy and sell, 1 the base buys only, 2 the base sells only. A ship
/// with min = max = 0 is not for sale on purpose when the base only buys it.
/// </summary>
public sealed class ModValidatorMarketTests
{
    /// <summary>The engine reads a market line of the text.</summary>
    private static MarketGood Line(string values)
    {
        var section = IniFile.ParseFile("market.ini", new MemoryStream(Encoding.ASCII.GetBytes(
            $"[BaseGood]\nbase = b1\nmarketgood = {values}\n"))).Single();
        return new MarketGood(section.Single(e => e.Name == "marketgood"));
    }

    [Fact]
    public void AShipThatTheBaseOnlyBuysGivesNoWarning() =>
        Assert.False(ModValidator.ShipNotForSale(Line("pkg1, 0, -1, 0, 0, 1, 1")));

    /// <summary>The base sells the ship (type 0 or 2), but it has no stock, so the ship is not for sale.</summary>
    [Theory]
    [InlineData("pkg1, 0, -1, 0, 0, 0, 1")]
    [InlineData("pkg1, 0, -1, 0, 0, 2, 1")]
    public void AShipWithMinAndMaxZeroThatTheBaseCanSellGivesTheWarning(string values) =>
        Assert.True(ModValidator.ShipNotForSale(Line(values)));

    /// <summary>The rule reads the same transaction types as the "Buys or sells" dropdown of the Inspector.</summary>
    [Theory]
    [InlineData(MarketTransaction.BuyAndSell, true)]
    [InlineData(MarketTransaction.BuyOnly, false)]
    [InlineData(MarketTransaction.SellOnly, true)]
    public void TheRuleReadsTheTransactionTypeOfTheInspector(MarketTransaction type, bool warning) =>
        Assert.Equal(warning, ModValidator.ShipNotForSale(Line($"pkg1, 0, -1, 0, 0, {(int)type}, 1")));

    [Fact]
    public void AShipWithStockGivesNoWarning() =>
        Assert.False(ModValidator.ShipNotForSale(Line("pkg1, 0, -1, 1, 1, 0, 1")));

    /// <summary>A line of a hull good sets the price of its ship at the base (fork commit OL-0080), so it is no error. A
    /// good without equipment is still an error.</summary>
    [Fact]
    public void AHullGoodSetsThePriceOfItsShipAndAGoodWithoutEquipmentIsIgnored()
    {
        Assert.Null(ModValidator.MarketGoodProblem(GoodCategory.ShipHull, hasEquipment: false));
        Assert.Null(ModValidator.MarketGoodProblem(GoodCategory.Equipment, hasEquipment: true));
        Assert.Null(ModValidator.MarketGoodProblem(GoodCategory.Commodity, hasEquipment: true));
        Assert.Equal("the good has no equipment, so the engine ignores the entry.",
            ModValidator.MarketGoodProblem(GoodCategory.Commodity, hasEquipment: false));
        Assert.NotNull(ModValidator.MarketGoodProblem(GoodCategory.Ship, hasEquipment: false));
    }
}
