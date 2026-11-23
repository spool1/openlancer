using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;

namespace OpenLancer.Studio.Commands;

/// <summary>A value of a market line that has its own field in the "Markets" section.</summary>
/// <param name="Index">The place of the value in <see cref="MarketLine.Values"/>.</param>
/// <param name="Name">The name of the field.</param>
/// <param name="Whole">True when the engine reads the value as a whole number.</param>
/// <param name="Tip">What the value does in the game, for the tooltip of the field.</param>
internal sealed record MarketValue(int Index, string Name, bool Whole, string Tip);

/// <summary>
/// The edits of the <c>marketgood</c> lines that sell a good. A patch cannot change a line of a lower layer,
/// so the edits change only the <c>[BaseGood]</c> sections that the active mod adds (see <c>docs/MOD_FORMAT.md</c>,
/// "Markets").
/// </summary>
internal static class MarketEdits
{
    private const string MarketGood = "marketgood";

    /// <summary>The values of a line that have a field, in the order of the line. The transaction type has its
    /// dropdown (see <see cref="SetTransaction"/>), and the engine does not read the values after the price
    /// multiplier.</summary>
    public static readonly IReadOnlyList<MarketValue> Fields =
    [
        new(MarketLine.RankIndex, "Rank", true, "The rank that the player needs to buy the good at the base."),
        new(MarketLine.ReputationIndex, "Reputation", false,
            "The reputation with the faction of the base that the player needs to buy the good at the base, from -1 " +
            "to 1."),
        new(MarketLine.MinimumIndex, "Minimum stock", true,
            "The smallest stock of the good at the base. The engine sells a ship only when the minimum stock or the " +
            "maximum stock is not 0."),
        new(MarketLine.MaximumIndex, "Maximum stock", true,
            "The largest stock of the good at the base. The engine sells a ship only when the minimum stock or the " +
            "maximum stock is not 0. It sells another good only when the maximum stock is more than 0."),
        new(MarketLine.PriceIndex, "Price multiplier", false,
            "The price of the good at the base is the price of the good times this value. For example, 2 doubles " +
            "the price."),
    ];

    /// <summary>
    /// The values of a new line after the good: rank, reputation, minimum, maximum, transaction type and price
    /// multiplier. The base buys and sells the good. A ship package gets the form of the retail ship lines, which sell
    /// one ship. The engine sells a ship only when the minimum or the maximum is not 0.
    /// </summary>
    public static string DefaultValues(string? category) =>
        string.Equals(category, "ship", StringComparison.OrdinalIgnoreCase) ? "0, -1, 1, 1, 0, 1, 1" : "0, -1, 10, 10, 0, 1";

    /// <summary>The text of the field of a value of a line, or an empty text when the line does not have the
    /// value.</summary>
    public static string FieldText(MarketLine line, MarketValue field) =>
        field.Index < line.Values.Count ? FieldEdit.FormatValue(line.Values[field.Index]) : "";

    /// <summary>The edit that sets one value of a line from the text of its field. The other values stay in their
    /// places (see <see cref="WithValue"/>).</summary>
    /// <exception cref="InvalidOperationException">The text is not a number of the kind that the engine reads for the
    /// value, or the active mod does not add the section of the line.</exception>
    public static EditCommand SetValue(EditSession session, MarketLine line, MarketValue field, string text)
    {
        var value = text.Trim();
        var valid = field.Whole
            ? int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            : float.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var number) && float.IsFinite(number);
        if (!valid)
            throw new InvalidOperationException(field.Whole
                ? $"{field.Name} must be a whole number, for example 3."
                : $"{field.Name} must be a number, for example 1.5.");
        return WithValue(session, line, field.Index, value);
    }

    /// <summary>The edit that sets the transaction type of a line. The other values stay in their places (see
    /// <see cref="WithValue"/>).</summary>
    /// <exception cref="InvalidOperationException">The active mod does not add the section of the line.</exception>
    public static EditCommand SetTransaction(EditSession session, MarketLine line, MarketTransaction type) =>
        WithValue(session, line, MarketLine.TransactionIndex, ((int)type).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The edit that sets the value at place <paramref name="index"/> of <see cref="MarketLine.Values"/>. The engine
    /// ignores a line without the price multiplier. So a line that ends before it also gets the values of a new line in
    /// the missing places (see <see cref="DefaultValues"/>), and each value keeps its place. For example, a price
    /// multiplier on a line without the transaction type also writes the type 0: the base buys and sells the good.
    /// </summary>
    private static EditCommand WithValue(EditSession session, MarketLine line, int index, string value)
    {
        var values = line.Values.Select(FieldEdit.FormatValue).ToList();
        var defaults = FieldEdit.Values(DefaultValues(Category(session, values[0])));
        for (var place = values.Count; place <= MarketLine.PriceIndex; place++)
            values.Add(defaults[place - 1]);
        values[index] = value;
        return SetLine(session, line, string.Join(", ", values.Skip(1)));
    }

    /// <summary>The key of the value fields of a line in the "Markets" section. Each field of the line is its own
    /// widget of the key.</summary>
    public static FieldKey LineKey(MarketLine line) => new(line.Section, MarketGood, line.Line);

    /// <summary>True when the active mod adds the section of the line, so an edit can change the line. The base view
    /// gives the same rule to each market line of a base.</summary>
    public static bool CanChange(EditSession session, MarketLine line) => BaseView.CanChange(session, line);

    /// <summary>
    /// The edit that makes a base sell a good. The line goes into a <c>[BaseGood]</c> of that base that the active mod
    /// adds. Without one, a new section goes into the market file in which the base sells goods of the same category,
    /// else into the first market file of the base, else into the first market file.
    /// </summary>
    /// <param name="values">The values after the good (see <see cref="DefaultValues"/>).</param>
    /// <exception cref="InvalidOperationException">A market file already sells the good at the base, or the edit view
    /// has no market file.</exception>
    public static EditCommand AddToBase(EditSession session, string good, string baseNickname, string values)
    {
        if (session.MarketLines(good).Any(l => Is(l.Base, baseNickname)))
            throw new InvalidOperationException($"The base {baseNickname} already sells {good}.");
        var line = $"{good}, {values}";
        var markets = session.List(ItemFileKind.Markets);
        foreach (var market in markets)
        {
            if (session.Get(market) is { IsNew: true, ReadOnlyReason: null } view && Is(Base(view), baseNickname))
                return EditCommand.SetLinesFromText(market, MarketGood, [.. Lines(view), line]);
        }

        var path = MarketFile(session, markets, good, baseNickname);
        // A new section goes at the end of its file, after the other [BaseGood] sections.
        var ordinal = markets.Count(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && Is(m.Id.Type, "BaseGood"));
        var added = new SectionRef(path, new SectionIdentity("BaseGood", null, null, ordinal));
        return EditCommand.Group($"Sell {good} at {baseNickname}",
        [
            EditCommand.AddSection(path, "BaseGood", [new EntryView("base", [new StringValue(baseNickname)])]),
            EditCommand.SetLinesFromText(added, MarketGood, [line]),
        ]);
    }

    /// <summary>The edit that sets the values of a line after the good, or removes the line when
    /// <paramref name="values"/> is null. A section without lines goes.</summary>
    /// <exception cref="InvalidOperationException">The active mod does not add the section of the line.</exception>
    public static EditCommand SetLine(EditSession session, MarketLine line, string? values)
    {
        var view = session.Get(line.Section);
        if (view is not { IsNew: true, ReadOnlyReason: null })
            throw new InvalidOperationException(
                "A patch cannot change a market line of a lower layer. Use a whole-file override for that.");
        var lines = Lines(view);
        if (values == null)
            lines.RemoveAt(line.Line);
        else
            lines[line.Line] = $"{line.Values[0]}, {values}";
        return lines.Count == 0
            ? EditCommand.DeleteSection(line.Section)
            : EditCommand.SetLinesFromText(line.Section, MarketGood, lines);
    }

    private static List<string> Lines(SectionView view) =>
        view.Lines(MarketGood).Select(l => FieldEdit.FormatLine(l.Values)).ToList();

    private static string MarketFile(EditSession session, IReadOnlyList<SectionRef> markets, string good, string baseNickname)
    {
        var category = Category(session, good);
        string? withBase = null;
        foreach (var market in markets)
        {
            if (session.Get(market) is not { } view || !Is(Base(view), baseNickname))
                continue;
            withBase ??= market.Path;
            if (category != null && view.Lines(MarketGood).Any(l => l.Values.Count > 0 &&
                                                                   Is(Category(session, l.Values[0].ToString()), category)))
                return market.Path;
        }
        return withBase ?? markets.FirstOrDefault()?.Path ??
            throw new InvalidOperationException("The edit view has no market file.");
    }

    /// <summary>The category of a good, for example <c>equipment</c> or <c>ship</c>, or null.</summary>
    public static string? Category(EditSession session, string good) =>
        session.Find(ItemFileKind.Goods, good) is { } section ? First(session.Get(section), "category") : null;

    private static string? Base(SectionView view) => First(view, "base");

    private static string? First(SectionView? view, string name) =>
        view?.Lines(name) is [{ Values: [var value, ..] }, ..] ? value.ToString() : null;

    private static bool Is(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
