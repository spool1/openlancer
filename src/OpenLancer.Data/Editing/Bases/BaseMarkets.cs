using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>
/// The rules of the <c>marketgood</c> lines of a base that the base checks and the market edits share. They are the
/// buy-only form, the lines that a dealer sells, the buy-only pattern of the ship markets of a game, and the dealer of
/// each goods category.
/// </summary>
/// <remarks>
/// <para>Retail facts: each vanilla <c>[BaseGood]</c> of the ship market lists each ship package. A base sells one to
/// three of them. The other lines are buy-only lines with the minimum stock 0, the maximum stock 0 and the transaction
/// type 1. HD Edition 0.7.1 lists only the ships that a base sells, so its ship markets have no buy-only lines. The game
/// gives the price of a ship at a base from the market price of its hull good there, and a line of the ship package does
/// not change it. So the trade-in value does not depend on the buy-only lines.</para>
/// <para>The fork sells a ship only when the minimum stock or the maximum stock is not 0. It sells another good only
/// when the maximum stock is more than 0. It keeps the last line of a good at a base (Base.AddMarketLine, fork commit
/// OL-0080). The retail games also do not sell a good of a buy-only line.</para>
/// </remarks>
public static class BaseMarkets
{
    /// <summary>The goods category of a commodity.</summary>
    public const string Commodity = "commodity";

    /// <summary>The goods category of an equipment item.</summary>
    public const string Equipment = "equipment";

    /// <summary>The goods category of a ship package.</summary>
    public const string Ship = "ship";

    /// <summary>The number of values that the engine reads from a <c>marketgood</c> line. It ignores a line with fewer
    /// values.</summary>
    private const int ReadValues = MarketLine.PriceIndex + 1;

    /// <summary>True for a buy-only line: the minimum stock 0, the maximum stock 0 and the transaction type 1. The base
    /// buys the good from the player and does not sell it.</summary>
    /// <param name="values">The values of the line, from the good.</param>
    public static bool IsBuyOnly(IReadOnlyList<ValueBase> values) =>
        values.Count >= ReadValues && Whole(values, MarketLine.MinimumIndex) == 0 &&
        Whole(values, MarketLine.MaximumIndex) == 0 &&
        Whole(values, MarketLine.TransactionIndex) == (int)MarketTransaction.BuyOnly;

    /// <summary>True when the dealer of the base sells the good of a line. The line is not buy only, and the stock lets
    /// the engine sell it.</summary>
    /// <param name="values">The values of the line, from the good.</param>
    /// <param name="ship">True for the line of a ship package.</param>
    public static bool Sells(IReadOnlyList<ValueBase> values, bool ship)
    {
        if (values.Count < ReadValues || Whole(values, MarketLine.TransactionIndex) == (int)MarketTransaction.BuyOnly)
            return false;
        var minimum = Whole(values, MarketLine.MinimumIndex) ?? 0;
        var maximum = Whole(values, MarketLine.MaximumIndex) ?? 0;
        return ship ? minimum != 0 || maximum != 0 : maximum > 0;
    }

    /// <summary>
    /// True when the ship markets of a game use the buy-only pattern of vanilla. At least one line of a ship package is a
    /// buy-only line. Vanilla then lists each ship package at each base that sells ships. The ship markets of HD Edition
    /// 0.7.1 do not use the pattern.
    /// </summary>
    /// <param name="shipLines">The values of the lines of the ship packages of the game, without the lines of the
    /// active mod.</param>
    public static bool UsesBuyOnlyShipLines(IEnumerable<IReadOnlyList<ValueBase>> shipLines) => shipLines.Any(IsBuyOnly);

    /// <summary>The dealer that sells the goods of a category, or null for another category, for example
    /// <c>shiphull</c>.</summary>
    public static BaseDealer? DealerOf(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        Commodity => BaseDealer.CommodityTrader,
        Equipment => BaseDealer.EquipmentDealer,
        Ship => BaseDealer.ShipDealer,
        _ => null,
    };

    /// <summary>The goods category that a dealer sells.</summary>
    public static string CategoryOf(BaseDealer dealer) => dealer switch
    {
        BaseDealer.CommodityTrader => Commodity,
        BaseDealer.EquipmentDealer => Equipment,
        _ => Ship,
    };

    /// <summary>The name of a dealer in plain words, for example "equipment dealer".</summary>
    public static string NameOf(BaseDealer dealer) => dealer switch
    {
        BaseDealer.CommodityTrader => "commodity trader",
        BaseDealer.EquipmentDealer => "equipment dealer",
        _ => "ship dealer",
    };

    /// <summary>The goods category of a good of the goods files of W, for example <c>equipment</c> or <c>ship</c>. Null
    /// when the good has no category or the data has no such good.</summary>
    internal static string? CategoryOf(EditSession session, string good) =>
        session.Find(ItemFileKind.Goods, good.Trim()) is { } section && session.Get(section) is { } view &&
        view.Lines("category") is [{ Values: [var value, ..] }, ..]
            ? value.ToString()?.Trim()
            : null;

    /// <summary>A value of a line as a whole number, or null when the line does not have it or it is no number.</summary>
    private static int? Whole(IReadOnlyList<ValueBase> values, int index) =>
        index < values.Count && values[index].TryToInt32(out var number) ? number : null;
}
