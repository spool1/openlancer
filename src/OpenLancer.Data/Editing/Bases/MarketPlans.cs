using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>A value of a market line that has its own field in the "Markets" section of a good.</summary>
/// <param name="Index">The place of the value in <see cref="MarketLine.Values"/>.</param>
/// <param name="Name">The name of the field.</param>
/// <param name="Whole">True when the engine reads the value as a whole number.</param>
/// <param name="Tip">What the value does in the game, for the tooltip of the field.</param>
public sealed record MarketValue(int Index, string Name, bool Whole, string Tip);

/// <summary>
/// One good of a market change (section E.2 of the bases design). A value that is null keeps the value of the line
/// that the base has for the good. A new line gets the value of a new line (see <see cref="MarketPlans.DefaultValues"/>).
/// </summary>
/// <param name="Good">The nickname of the good.</param>
public sealed record MarketRow(string Good)
{
    /// <summary>What the base does with the good. A buy-only row without stock values gets the stock 0 and 0, as the
    /// retail lines have.</summary>
    public MarketTransaction? Transaction { get; init; }

    /// <summary>The price of the good at the base is the price of the good times this value.</summary>
    public float? PriceMultiplier { get; init; }

    /// <summary>The smallest stock of the good at the base.</summary>
    public int? MinStock { get; init; }

    /// <summary>The largest stock of the good at the base.</summary>
    public int? MaxStock { get; init; }

    /// <summary>The rank that the player needs to buy the good at the base.</summary>
    public int? Rank { get; init; }

    /// <summary>The reputation with the faction of the base that the player needs, from -1 to 1.</summary>
    public float? Reputation { get; init; }
}

/// <summary>A factor for the price multiplier of the copied and named goods of a market change.</summary>
/// <param name="Factor">The factor, above 0.</param>
/// <param name="Category">The goods category that the factor applies to, for example <c>commodity</c>, or null for each
/// good.</param>
public sealed record MarketPriceScale(float Factor, string? Category = null);

/// <summary>
/// A line of the active mod that replaces the market line of a lower layer: the base data or a mod that loads before the
/// active mod. The games keep the last line of a good at a base.
/// </summary>
/// <param name="Line">The line of the active mod.</param>
/// <param name="Lower">The line of the lower layer that it replaces.</param>
/// <param name="Removed">True when the line is the buy-only form: the base stops the sale of the good and only buys
/// it.</param>
/// <param name="Text">The result in plain words, for example "Removed: the base only buys it".</param>
/// <param name="Tip">The line that the save writes, and why it replaces the lower line.</param>
public sealed record MarketOverride(MarketLine Line, MarketLine Lower, bool Removed, string Text, string Tip);

/// <summary>The plan of a market change of one base (see <see cref="MarketPlans.Set"/>).</summary>
/// <remarks>A plan reads W when it is made. Its command runs only while W is as the plan read it, so make the plan and
/// run it at once.</remarks>
public sealed class MarketPlan : EditPlan
{
    internal MarketPlan(string label) : base(label)
    {
    }

    /// <summary>The part of the request that the refusal names, for example <c>goods[2].good</c>, or null.</summary>
    public string? RefusedPart { get; private set; }

    /// <summary>The commands of the plan in their order, without the test of <see cref="EditSession.Snapshot"/>. The plan
    /// of a new base runs them in its own edit.</summary>
    internal IReadOnlyList<EditCommand> Steps { get; set; } = [];

    /// <summary>Gives the plan a refusal, so it cannot run.</summary>
    internal MarketPlan Refuse(string path, string message, string? detail = null, string? part = null,
        SectionRef? target = null)
    {
        Command = null;
        Refusals = [Diagnostic.Error(path, message) with { Target = target, Rule = detail }];
        RefusedPart = part;
        return this;
    }
}

/// <summary>
/// The edits of the <c>marketgood</c> lines of the bases (section C.5 of the bases design). The agent tools, the base
/// plans and the "Markets" section of the Inspector use them.
/// </summary>
/// <remarks>
/// <para>Retail fact: the games keep the last line of a good at a base, in the order of the market files of
/// <c>[Data]</c>. The OpenLancer engine does the same (fork commit OL-0080). So the active mod changes a line of a lower
/// layer with a later line: a line in a <c>[BaseGood]</c> of the active mod in the market file of the lower line. The
/// merge puts the sections of the active mod after the sections of the lower layers. A remove of a lower line writes the
/// buy-only form: the minimum stock 0, the maximum stock 0 and the transaction type 1.</para>
/// <para>A line of the active mod changes in its place. A remove of a line of the active mod deletes it, and a section
/// without lines goes. When the line replaces a lower line, the remove writes the buy-only form instead.</para>
/// </remarks>
public static class MarketPlans
{
    private const string MarketGood = "marketgood";
    private const string BaseGood = "BaseGood";

    /// <summary>The values after the good of a buy-only line of a ship package, in the form of the retail ship
    /// lines.</summary>
    private const string BuyOnlyShipValues = "0, -1, 0, 0, 1, 1, 1";

    /// <summary>The most ship packages that a fix text names.</summary>
    private const int ShownShips = 5;

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
        string.Equals(category, BaseMarkets.Ship, StringComparison.OrdinalIgnoreCase)
            ? "0, -1, 1, 1, 0, 1, 1"
            : "0, -1, 10, 10, 0, 1";

    /// <summary>The text of the field of a value of a line, or an empty text when the line does not have the
    /// value.</summary>
    public static string FieldText(MarketLine line, MarketValue field) =>
        field.Index < line.Values.Count ? RetailText.Of(line.Values[field.Index]) : "";

    /// <summary>The category of a good, for example <c>equipment</c> or <c>ship</c>, or null. The base checks read the
    /// same category (see <see cref="BaseMarkets.CategoryOf(EditSession, string)"/>).</summary>
    public static string? Category(EditSession session, string good) => BaseMarkets.CategoryOf(session, good);

    /// <summary>
    /// True when an edit can change a market line. A line of the active mod changes in its place. A line of a lower
    /// layer changes through a later line of the active mod in the same market file. Both need a session and a market
    /// file that take edits.
    /// </summary>
    public static bool CanChange(EditSession session, MarketLine line) => NoChangeReason(session, line) == null;

    /// <summary>Why an edit cannot change a market line, or null when it can (see <see cref="CanChange"/>).</summary>
    public static string? NoChangeReason(EditSession session, MarketLine line)
    {
        if (session.ReadOnlyReason is { } locked)
            return locked;
        if (session.Get(line.Section) is not { } view)
            return $"No market file of the edit view has the line of {line.Base}.";
        if (view.IsNew)
            return view.ReadOnlyReason;
        return session.OpenDocument(line.Section.Path) is { ReadOnlyReason: { } reason }
            ? $"{SceneSections.FileName(line.Section.Path)}: {reason}"
            : null;
    }

    /// <summary>
    /// The line of the active mod that replaces a lower line of the same good at the base, with the result in plain
    /// words, or null. A line is such a line when it is the last line of its good at the base, the active mod adds its
    /// section, and a lower layer has a line of the good at the base.
    /// </summary>
    public static MarketOverride? Override(EditSession session, MarketLine line)
    {
        var lines = session.Items.BaseMarketLines(line.Base).Where(l => Is(Good(l), Good(line))).ToList();
        if (lines.Count == 0 || !Same(lines[^1], line) || !Own(session, line) ||
            lines.LastOrDefault(l => !Own(session, l)) is not { } lower)
            return null;
        var (origin, mod) = session.Origin(lower.Section, MarketGood);
        var layer = origin == ValueOrigin.Base ? "the game" : $"the mod {mod}";
        var removed = BaseMarkets.IsBuyOnly(line.Values);
        var text = removed
            ? "Removed: the base only buys it"
            : $"Changed: the base uses this line, not the line of {layer}";
        var tip = $"The save writes the line 'marketgood = {Text(line.Values)}' in a [BaseGood] of {line.Base} in " +
                  $"{SceneSections.FileName(line.Section.Path)}. It replaces the line 'marketgood = {Text(lower.Values)}' " +
                  $"of {layer}. The games keep the last line of a good at a base." +
                  (removed ? " The minimum stock 0, the maximum stock 0 and the type 1 stop the sale of the good." : "");
        return new MarketOverride(line, lower, removed, text, tip);
    }

    /// <summary>
    /// The edit that sets one value of a line from the text of its field. The other values stay in their places. A line
    /// of the active mod changes. A line of a lower layer gets a later line of the active mod with the new value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The text is not a number of the kind that the engine reads for the
    /// value, or the line cannot change (see <see cref="NoChangeReason"/>).</exception>
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
        return WithValue(session, line, field.Index, value, $"Set {field.Name}");
    }

    /// <summary>The edit that sets the transaction type of a line. The other values stay in their places (see
    /// <see cref="SetValue"/>).</summary>
    /// <exception cref="InvalidOperationException">The line cannot change.</exception>
    public static EditCommand SetTransaction(EditSession session, MarketLine line, MarketTransaction type) =>
        WithValue(session, line, MarketLine.TransactionIndex, ((int)type).ToString(CultureInfo.InvariantCulture),
            "Set transaction type");

    /// <summary>
    /// The edit that sets the values of a line after the good, or removes the line when <paramref name="values"/> is
    /// null. A line of the active mod changes or goes, and a section without lines goes. A line of a lower layer gets a
    /// later line of the active mod. A remove of a lower line, also of a line that replaces a lower line, writes the
    /// buy-only form with the old price multiplier.
    /// </summary>
    /// <exception cref="InvalidOperationException">The line cannot change.</exception>
    public static EditCommand SetLine(EditSession session, MarketLine line, string? values)
    {
        var draft = Draft.For(session, line);
        if (values == null)
            draft.Remove(line);
        else
            draft.Set(line, [.. Split(values)]);
        return draft.Command(values == null ? $"Remove {Good(line)} at {line.Base}" : $"Set {Good(line)} at {line.Base}");
    }

    /// <summary>
    /// The edit that removes each line of the active mod of the good of a line at its base, so the line of the lower
    /// layer counts again (see <see cref="Override"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The line does not replace a lower line, or it cannot
    /// change.</exception>
    public static EditCommand Revert(EditSession session, MarketLine line)
    {
        if (Override(session, line) is null)
            throw new InvalidOperationException(
                $"The line of {Good(line)} at {line.Base} does not replace a line of a lower layer.");
        var draft = Draft.For(session, line);
        draft.Revert(Good(line));
        return draft.Command($"Revert {Good(line)} at {line.Base}");
    }

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
        var draft = new Draft(session, baseNickname);
        draft.Add(good, [.. Split(values)], null);
        return draft.Command($"Sell {good} at {baseNickname}");
    }

    /// <summary>
    /// Plans a market change of a base as one entry of the edit history "Set market of &lt;base&gt;" (section C.5 of
    /// the bases design).
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><paramref name="copyFrom"/> copies the last line of each good of that base first. A copied line goes into
    /// the market file of the line that it copies.</item>
    /// <item>Each row sets the line of its good. A row keeps the values that it does not give from the line of the base,
    /// or from the copied line.</item>
    /// <item><paramref name="replace"/> removes each good of the base that the copy and the rows do not give.</item>
    /// <item><paramref name="priceScale"/> multiplies the price multiplier of each copied and named good.</item>
    /// <item>When the base sells a ship after the change, and the ship markets of the game use buy-only lines, each
    /// ship package without a line at the base gets a buy-only line. The fixes list them.</item>
    /// </list>
    /// </remarks>
    /// <param name="baseNickname">The base. It must be a base of W.</param>
    /// <param name="rows">The goods to set.</param>
    /// <param name="remove">The goods that the base stops selling.</param>
    /// <param name="copyFrom">The base whose market lines the base copies first, or null.</param>
    /// <param name="replace">True to remove each good that the copy and the rows do not give.</param>
    /// <param name="priceScale">The factor for the price multipliers, or null.</param>
    public static MarketPlan Set(EditSession session, string baseNickname, IReadOnlyList<MarketRow> rows,
        IReadOnlyList<string> remove, string? copyFrom = null, bool replace = false, MarketPriceScale? priceScale = null)
    {
        var name = baseNickname.Trim();
        var plan = new MarketPlan($"Set market of {name}");
        if (session.Base(name) is not { } view)
            return plan.Refuse("", $"No [Base] of universe.ini has the nickname {name}. Get the bases from list_bases.",
                MissionErrors.ItemNotFound, "base");
        return Plan(session, plan, view.Nickname, rows, remove, copyFrom, replace, priceScale);
    }

    /// <summary>
    /// Plans the buy-only lines of the ship packages that a base does not list, when the base sells a ship and the ship
    /// markets of the game use buy-only lines (see <see cref="MissingShipLines"/>). The completeness report of a base
    /// gives it as the fix of its market.
    /// </summary>
    public static MarketPlan ShipLines(EditSession session, string baseNickname)
    {
        var name = baseNickname.Trim();
        var plan = new MarketPlan($"Add the ship lines of {name}");
        if (session.Base(name) is not { } view)
            return plan.Refuse("", $"No [Base] of universe.ini has the nickname {name}.", MissionErrors.ItemNotFound,
                "base");
        if (MissingShipLines(session, view).Count == 0)
            return plan.Refuse("", $"The market of {view.Nickname} lists each ship package, or the base sells no ship.");
        return Plan(session, plan, view.Nickname, [], [], null, false, null);
    }

    /// <summary>
    /// The ship packages that a base does not list while it sells a ship, when the ship markets of the game and the
    /// lower mods use buy-only lines (see <see cref="BaseMarkets.UsesBuyOnlyShipLines"/>). Vanilla lists each ship
    /// package at each base that sells ships. The list is empty for a base whose market neither the active mod adds nor
    /// changes. So a new ship package of the mod does not name each base of the game.
    /// </summary>
    public static IReadOnlyList<string> MissingShipLines(EditSession session, BaseView view)
    {
        var state = session.BaseCheckState;
        if (!state.UsesBuyOnlyShipLines ||
            view.Origin != ValueOrigin.ActiveMod && view.Market.All(r => r.Origin != ValueOrigin.ActiveMod))
            return [];
        var last = view.Market.GroupBy(r => r.Good, StringComparer.OrdinalIgnoreCase).Select(g => g.Last());
        if (!last.Any(r => BaseMarkets.Ship.Equals(state.Category(r.Good), StringComparison.OrdinalIgnoreCase) &&
                           BaseMarkets.Sells(r.Line.Values, ship: true)))
            return [];
        var listed = view.Market.Select(r => r.Good).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return state.ShipPackages.Where(p => !listed.Contains(p)).ToList();
    }

    /// <summary>
    /// Plans the market lines of a base for <see cref="Set"/>, <see cref="ShipLines"/> and the market copy of a new base.
    /// The base need not be in W yet: the plan of a new base runs the market commands in the same edit as the new
    /// <c>[Base]</c>.
    /// </summary>
    /// <returns>The plan. Its command is null when it refuses or when the market does not change.</returns>
    internal static MarketPlan Plan(EditSession session, MarketPlan plan, string baseNickname,
        IReadOnlyList<MarketRow> rows, IReadOnlyList<string> remove, string? copyFrom, bool replace,
        MarketPriceScale? priceScale)
    {
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (priceScale is { Factor: <= 0 or float.NaN or float.PositiveInfinity })
            return plan.Refuse("", "The price factor must be a number above 0.", MissionErrors.FieldValue,
                "price_scale.factor");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var good = rows[i].Good.Trim();
            if (RowProblem(session, rows[i]) is { } problem)
                return plan.Refuse("", problem.Message, problem.Detail, $"goods[{i}].{problem.Part}");
            if (!seen.Add(good))
                return plan.Refuse("", $"The good {good} is in more than one row. Give each good one row.",
                    MissionErrors.FieldValue, $"goods[{i}].good");
        }
        for (var i = 0; i < remove.Count; i++)
        {
            if (!seen.Add(remove[i].Trim()))
                return plan.Refuse("", $"The good {remove[i].Trim()} is in a row and in the goods to remove. Give it " +
                                       "only one place.", MissionErrors.FieldValue, $"remove[{i}]");
        }

        var draft = new Draft(session, baseNickname);
        // A copied line goes into the market file of the line that it copies. A row of a copied good starts from the
        // copied values.
        var copies = new Dictionary<string, (string File, IReadOnlyList<string> Values)>(StringComparer.OrdinalIgnoreCase);
        if (copyFrom != null)
        {
            var source = copyFrom.Trim();
            if (session.Base(source) is not { } sourceView)
                return plan.Refuse("", $"No [Base] of universe.ini has the nickname {source}. Get the bases from " +
                                       "list_bases.", MissionErrors.ItemNotFound, "copy_from");
            var removed = remove.Select(g => g.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in sourceView.Market.GroupBy(r => r.Good, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.Last()).Where(r => !removed.Contains(r.Good)))
                copies[row.Good] = (row.Line.Section.Path, Texts(row.Line.Values));
        }
        var rowGoods = rows.Select(r => r.Good.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = copies.Where(c => !rowGoods.Contains(c.Key))
            .Select(c => (Row: new MarketRow(c.Key), File: (string?)c.Value.File, Values: (IReadOnlyList<string>?)c.Value.Values))
            .ToList();
        foreach (var row in rows.Select(r => r with { Good = r.Good.Trim() }))
            named.Add(copies.TryGetValue(row.Good, out var copy) ? (row, copy.File, copy.Values) : (row, null, null));
        var given = named.Select(n => n.Row.Good).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (row, file, copied) in named)
            {
                var category = Category(session, row.Good);
                IReadOnlyList<string>? current = copied;
                if (current == null && draft.Last(row.Good) is { } line)
                    current = Texts(line.Values);
                var values = Values(row, current, category);
                if (priceScale != null && (priceScale.Category == null ||
                                           Is(priceScale.Category.Trim(), category ?? "")))
                    values[MarketLine.PriceIndex - 1] = RetailText.Float(Number(values[MarketLine.PriceIndex - 1]) *
                                                                         priceScale.Factor);
                draft.SetGood(row.Good, values, file);
            }
            foreach (var good in remove.Select(g => g.Trim()))
            {
                if (draft.Last(good) is null)
                    plan.Warnings = [.. plan.Warnings, Diagnostic.Warning("", $"{baseNickname} does not sell or buy {good}.")];
                else
                    draft.RemoveGood(good);
            }
            if (replace)
            {
                foreach (var good in draft.Goods().Where(g => !given.Contains(g)).ToList())
                    draft.RemoveGood(good);
            }
            draft.AddShipLines();
            // A market file that takes no edits refuses when the draft makes its commands.
            draft.Fill(plan);
        }
        catch (InvalidOperationException e)
        {
            return plan.Refuse("", e.Message);
        }
        return plan;
    }

    /// <summary>Why a row is not valid: the text, the detail code and the part of the row, or null.</summary>
    private static (string Message, string Detail, string Part)? RowProblem(EditSession session, MarketRow row)
    {
        var good = row.Good.Trim();
        if (good.Length == 0)
            return ("The row needs a good.", MissionErrors.FieldValue, "good");
        if (session.Find(ItemFileKind.Goods, good) is null)
            return ($"No [Good] of the goods files has the nickname {good}. Get the goods from list_values with the " +
                    "list good.", MissionErrors.ItemNotFound, "good");
        if (row.Reputation is { } rep && (!float.IsFinite(rep) || rep < -1 || rep > 1))
            return ("The reputation must be a number from -1 to 1.", MissionErrors.FieldValue, "reputation");
        if (row.PriceMultiplier is { } price && (!float.IsFinite(price) || price < 0))
            return ("The price multiplier must be a number of 0 or more.", MissionErrors.FieldValue, "price_multiplier");
        if (row.MinStock < 0 || row.MaxStock < 0)
            return ("A stock value must be 0 or more.", MissionErrors.FieldValue,
                row.MinStock < 0 ? "min_stock" : "max_stock");
        return null;
    }

    /// <summary>
    /// The values of a line after the good for a row: the values of the current line, else the values of a new line
    /// (see <see cref="DefaultValues"/>), with the values of the row. A buy-only row without stock values gets the
    /// stock 0 and 0.
    /// </summary>
    private static List<string> Values(MarketRow row, IReadOnlyList<string>? current, string? category)
    {
        var defaults = Split(DefaultValues(row.Transaction == MarketTransaction.BuyOnly ? null : category));
        var values = current?.Skip(1).ToList() ?? [.. defaults];
        for (var place = values.Count; place < MarketLine.PriceIndex; place++)
            values.Add(defaults[place]);
        static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);
        if (row.Rank is { } rank)
            values[MarketLine.RankIndex - 1] = Whole(rank);
        if (row.Reputation is { } rep)
            values[MarketLine.ReputationIndex - 1] = RetailText.Float(rep);
        if (row.MinStock is { } min)
            values[MarketLine.MinimumIndex - 1] = Whole(min);
        if (row.MaxStock is { } max)
            values[MarketLine.MaximumIndex - 1] = Whole(max);
        if (row.Transaction is { } type)
        {
            values[MarketLine.TransactionIndex - 1] = Whole((int)type);
            if (type == MarketTransaction.BuyOnly && row.MinStock == null && row.MaxStock == null)
                values[MarketLine.MinimumIndex - 1] = values[MarketLine.MaximumIndex - 1] = "0";
        }
        if (row.PriceMultiplier is { } price)
            values[MarketLine.PriceIndex - 1] = RetailText.Float(price);
        return values;
    }

    /// <summary>
    /// The edit that sets value <paramref name="index"/> of <see cref="MarketLine.Values"/>. The engine ignores a line
    /// without the price multiplier. So a line that ends before it also gets the values of a new line in the missing
    /// places (see <see cref="DefaultValues"/>), and each value keeps its place. For example, a price multiplier on a
    /// line without the transaction type also writes the type 0: the base buys and sells the good.
    /// </summary>
    private static EditCommand WithValue(EditSession session, MarketLine line, int index, string value, string label)
    {
        var draft = Draft.For(session, line);
        // A lower line that a later line of the active mod replaces changes through that later line, with its values.
        var current = Own(session, line) ? line : draft.Last(Good(line)) ?? line;
        var values = Texts(current.Values);
        var defaults = Split(DefaultValues(Category(session, values[0])));
        for (var place = values.Count; place <= MarketLine.PriceIndex; place++)
            values.Add(defaults[place - 1]);
        values[index] = value;
        draft.Set(line, values.Skip(1).ToList());
        return draft.Command(label);
    }

    /// <summary>The value texts of a line, the good first.</summary>
    private static List<string> Texts(IReadOnlyList<ValueBase> values) => values.Select(RetailText.Of).ToList();

    /// <summary>The text of a line after its key.</summary>
    private static string Text(IReadOnlyList<ValueBase> values) => string.Join(", ", values.Select(RetailText.Of));

    private static string[] Split(string values) =>
        values.Trim().Length == 0 ? [] : values.Split(',').Select(v => v.Trim()).ToArray();

    private static float Number(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 1;

    private static string Good(MarketLine line) => line.Values[0].ToString().Trim();

    private static bool Own(EditSession session, MarketLine line) => session.Get(line.Section) is { IsNew: true };

    /// <summary>True when two market lines are the same line of the same section. The values do not count.</summary>
    private static bool Same(MarketLine a, MarketLine b) => a.Section.Equals(b.Section) && a.Line == b.Line;

    private static bool Is(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The market lines of one base while a plan changes them. The draft knows the lines of each layer in load order,
    /// changes the lines of the active mod and adds new sections. Then it gives the commands.
    /// </summary>
    private sealed class Draft
    {
        private readonly EditSession session;
        private readonly string baseNickname;

        /// <summary>The market sections of the base in load order. A section of the active mod has a list of lines
        /// that the draft changes. A removed line is null.</summary>
        private readonly List<Part> parts = [];

        /// <summary>The market files in load order.</summary>
        private readonly List<string> files = [];

        /// <summary>The fixes of the draft, by the section that gets the lines.</summary>
        private readonly List<(Part Part, string Text)> fixes = [];

        public Draft(EditSession session, string baseNickname)
        {
            this.session = session;
            this.baseNickname = baseNickname;
            foreach (var market in session.Items.List(ItemFileKind.Markets))
            {
                if (!files.Contains(market.Path, VfsPath.Comparer))
                    files.Add(market.Path);
                if (session.Get(market) is not { } view || !Is(First(view, "base")?.Trim(), baseNickname))
                    continue;
                var lines = view.Lines(MarketGood).Select(l => (string?)Text(l.Values)).ToList();
                parts.Add(new Part(market.Path, market, view.IsNew, lines, view.IsNew ? view.ReadOnlyReason : null));
            }
        }

        /// <summary>The draft of the base of a line. The line must be able to change (see
        /// <see cref="NoChangeReason"/>).</summary>
        public static Draft For(EditSession session, MarketLine line)
        {
            if (NoChangeReason(session, line) is { } reason)
                throw new InvalidOperationException(reason);
            return new Draft(session, line.Base);
        }

        /// <summary>The goods of the base, in the order of their first line.</summary>
        public IEnumerable<string> Goods() => Lines().Select(l => l.Good).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last line of a good at the base, or null.</summary>
        public MarketLine? Last(string good) =>
            Lines().LastOrDefault(l => Is(l.Good, good)) is { } last ? Line(last) : null;

        /// <summary>Sets the values of a line. A line of the active mod changes in its place. A lower line gets a later
        /// line of the active mod in its market file.</summary>
        public void Set(MarketLine line, IReadOnlyList<string> values)
        {
            if (Own(session, line))
                Change(Find(line), Good(line), values);
            else if (LastOwn(Good(line)) is { } own)
                own.Part.Lines[own.Index] = Line(Good(line), values);
            else if (!SameValues(line, values))
                Override(line.Section.Path, Good(line), values);
        }

        /// <summary>Removes a line. See <see cref="SetLine"/>.</summary>
        public void Remove(MarketLine line)
        {
            var good = Good(line);
            var lower = Lines().Any(l => Is(l.Good, good) && !l.Part.Own);
            if (!lower)
            {
                var part = Find(line);
                part.Part.Lines[part.Index] = null;
                return;
            }
            var buyOnly = BuyOnly(line);
            if (Own(session, line))
                Change(Find(line), good, buyOnly);
            else if (LastOwn(good) is { } own)
                own.Part.Lines[own.Index] = Line(good, buyOnly);
            else if (!BaseMarkets.IsBuyOnly(line.Values))
                Override(line.Section.Path, good, buyOnly);
        }

        /// <summary>The last line of the active mod of a good when it comes after each lower line of the good, or
        /// null.</summary>
        private DraftLine? LastOwn(string good) =>
            Lines().LastOrDefault(l => Is(l.Good, good)) is { Part.Own: true } last ? last : null;

        /// <summary>Sets the line of a good. The last line of the good changes (see <see cref="Set"/>). A new good gets a
        /// line in <paramref name="file"/>, or by the file rule of <see cref="AddToBase"/>.</summary>
        public void SetGood(string good, IReadOnlyList<string> values, string? file)
        {
            if (Last(good) is { } last)
            {
                if (!SameValues(last, values))
                    Set(last, values);
            }
            else
            {
                Add(good, values, file);
            }
        }

        /// <summary>Removes the good: the last line goes (see <see cref="Remove"/>). Each other line of the active mod
        /// of the good goes too when no lower layer has the good.</summary>
        public void RemoveGood(string good)
        {
            if (Last(good) is not { } last)
                return;
            if (!Lines().Any(l => Is(l.Good, good) && !l.Part.Own))
            {
                Revert(good);
                return;
            }
            Remove(last);
        }

        /// <summary>Removes each line of the active mod of a good.</summary>
        public void Revert(string good)
        {
            foreach (var line in Lines().Where(l => Is(l.Good, good) && l.Part.Own).ToList())
                line.Part.Lines[line.Index] = null;
        }

        /// <summary>Adds the line of a good that the base does not list. It goes into <paramref name="file"/>, else into
        /// a section of the active mod of the base, else into a new section by the file rule of
        /// <see cref="AddToBase"/>.</summary>
        public void Add(string good, IReadOnlyList<string> values, string? file)
        {
            var path = file ?? parts.FirstOrDefault(p => p.Own)?.Path ?? MarketFile(good);
            Target(path).Lines.Add(Line(good, values));
        }

        /// <summary>
        /// Adds a buy-only line for each ship package without a line, when the base sells a ship and the ship markets
        /// of the game and the lower mods use buy-only lines. The lines go into the market file in which the base
        /// sells the ship.
        /// </summary>
        public void AddShipLines()
        {
            var state = session.BaseCheckState;
            if (!state.UsesBuyOnlyShipLines)
                return;
            var lines = Lines().ToList();
            var last = lines.GroupBy(l => l.Good, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
            var ship = last.FirstOrDefault(l => BaseMarkets.Ship.Equals(state.Category(l.Good), StringComparison.OrdinalIgnoreCase) &&
                                                BaseMarkets.Sells(Values(l.Text), ship: true));
            if (ship == null)
                return;
            var listed = lines.Select(l => l.Good).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = state.ShipPackages.Where(p => !listed.Contains(p)).ToList();
            if (missing.Count == 0)
                return;
            var target = ship.Part.Own ? ship.Part : Target(ship.Part.Path);
            foreach (var package in missing)
                target.Lines.Add($"{package}, {BuyOnlyShipValues}");
            var names = missing.Count <= ShownShips
                ? SceneSections.Join(missing)
                : $"{string.Join(", ", missing.Take(ShownShips))} and {missing.Count - ShownShips} more";
            var count = missing.Count == 1 ? "1 ship package" : $"{missing.Count} ship packages";
            fixes.Add((target, $"{baseNickname} sells ships, so it gets a buy-only line for {count}: {names}. The ship " +
                               "markets of the game list each ship package at each base that sells ships."));
        }

        /// <summary>The command of the draft.</summary>
        /// <exception cref="InvalidOperationException">The market does not change.</exception>
        public EditCommand Command(string label)
        {
            var (commands, _, _, _) = Commands();
            return commands.Count == 0
                ? throw new InvalidOperationException($"The market of {baseNickname} does not change.")
                : EditCommand.Group(label, commands);
        }

        /// <summary>Gives the plan the command, the changed, added and deleted sections and the fixes. A draft without a
        /// change gives no command.</summary>
        public void Fill(MarketPlan plan)
        {
            var (commands, adds, changes, deletes) = Commands();
            plan.Adds = adds;
            plan.Changes = changes;
            plan.Deletes = deletes;
            plan.Fixes = fixes.Select(f => new PlannedFix(Final(f.Part.Reference!, deletes), MarketGood, f.Text)).ToList();
            plan.Steps = commands;
            plan.Command = commands.Count == 0 ? null : EditCommand.Group(plan.Label, commands, session.Snapshot());
            if (commands.Count == 0 && plan.Refusals.Count == 0)
                plan.Refusals = [Diagnostic.Error("", $"The market of {baseNickname} does not change.") with
                {
                    Rule = NoChange,
                }];
        }

        /// <summary>
        /// The commands of the draft, with the final references: a changed section gets its lines, a new section is
        /// added with its lines, and a section of the active mod without lines goes. The deletes run last, so the
        /// ordinals of the earlier commands stay valid.
        /// </summary>
        private (List<EditCommand> Commands, List<SectionRef> Adds, List<SectionRef> Changes, List<PlannedDelete> Deletes)
            Commands()
        {
            var commands = new List<EditCommand>();
            var adds = new List<SectionRef>();
            var changes = new List<SectionRef>();
            var gone = new List<SectionRef>();
            var added = new Dictionary<string, int>(VfsPath.Comparer);
            foreach (var part in parts.Where(p => p.Own || p.Section == null))
            {
                var lines = part.Lines.OfType<string>().ToList();
                if (part.Section is { } section)
                {
                    if (lines.SequenceEqual(part.Original.OfType<string>()))
                        continue;
                    if (part.ReadOnlyReason is { } reason)
                        throw new InvalidOperationException(reason);
                    if (lines.Count == 0)
                    {
                        gone.Add(section);
                        continue;
                    }
                    commands.Add(EditCommand.SetLinesFromText(section, MarketGood, lines));
                    changes.Add(section);
                    continue;
                }
                if (lines.Count == 0)
                    continue;
                if (session.OpenDocument(part.Path) is { ReadOnlyReason: { } fileReason })
                    throw new InvalidOperationException($"{SceneSections.FileName(part.Path)}: {fileReason}");
                var count = added.GetValueOrDefault(part.Path);
                added[part.Path] = count + 1;
                var ordinal = session.Sections(part.Path).Count(s => Is(s.Id.Type, BaseGood)) + count;
                var reference = new SectionRef(part.Path, new SectionIdentity(BaseGood, null, null, ordinal));
                part.Reference = reference;
                commands.Add(EditCommand.AddSection(part.Path, BaseGood,
                    [new EntryView("base", [new StringValue(baseNickname)])]));
                commands.Add(EditCommand.SetLinesFromText(reference, MarketGood, lines));
                adds.Add(reference);
            }
            // A delete moves the ordinals of the later sections of its file, so the last section goes first.
            foreach (var section in gone.OrderByDescending(s => s.Id.Ordinal))
                commands.Add(EditCommand.DeleteSection(section));
            var deletes = gone.Select(s => new PlannedDelete(s, 0, DeleteReason.Target, null)).ToList();
            return (commands, adds.Select(a => Final(a, deletes)).ToList(), changes.Select(c => Final(c, deletes)).ToList(),
                deletes);
        }

        /// <summary>The reference of a section after the deletes of the draft.</summary>
        private static SectionRef Final(SectionRef section, IReadOnlyList<PlannedDelete> deletes)
        {
            var before = deletes.Count(d => VfsPath.Comparer.Equals(d.Section.Path, section.Path) &&
                                            Is(d.Section.Id.Type, section.Id.Type) &&
                                            d.Section.Id.Ordinal < section.Id.Ordinal);
            return before == 0 ? section : section with { Id = section.Id with { Ordinal = section.Id.Ordinal - before } };
        }

        /// <summary>Changes a line of the active mod in its place.</summary>
        private static void Change((Part Part, int Index) at, string good, IReadOnlyList<string> values) =>
            at.Part.Lines[at.Index] = Line(good, values);

        /// <summary>Adds a line of the active mod after the lower lines of a market file: in a section of the active mod
        /// of the base in that file, else in a new section.</summary>
        private void Override(string path, string good, IReadOnlyList<string> values) =>
            Target(path).Lines.Add(Line(good, values));

        /// <summary>The section of the active mod of the base in a market file that gets new lines: the first one, else
        /// a new section.</summary>
        private Part Target(string path)
        {
            if (parts.FirstOrDefault(p => p.Own && VfsPath.Comparer.Equals(p.Path, path)) is { } own)
                return own;
            var part = new Part(path, null, true, [], null);
            // The new section goes after the other sections of its file, so the lines of the file come in load order.
            var index = parts.FindLastIndex(p => VfsPath.Comparer.Equals(p.Path, path));
            if (index < 0)
                index = parts.FindLastIndex(p => FileIndex(p.Path) < FileIndex(path));
            parts.Insert(index + 1, part);
            return part;
        }

        private int FileIndex(string path) => files.FindIndex(f => VfsPath.Comparer.Equals(f, path));

        /// <summary>The place of a line of the active mod in the draft.</summary>
        private (Part Part, int Index) Find(MarketLine line) =>
            (parts.First(p => Equals(p.Section, line.Section)), line.Line);

        /// <summary>The lines of the base in load order, without the removed lines.</summary>
        private IEnumerable<DraftLine> Lines()
        {
            foreach (var part in parts)
            {
                for (var i = 0; i < part.Lines.Count; i++)
                {
                    if (part.Lines[i] is { } text)
                        yield return new DraftLine(part, i, text, Values(text)[0].ToString().Trim());
                }
            }
        }

        /// <summary>A market line of a line of the draft, for the rules that read the values.</summary>
        private MarketLine Line(DraftLine line) =>
            new(line.Part.Section ?? new SectionRef(line.Part.Path, new SectionIdentity(BaseGood, null)), baseNickname,
                line.Index, Values(line.Text));

        /// <summary>The market file of a new good: the file in which the base sells goods of the same category, else
        /// the first file of the base, else the first market file.</summary>
        private string MarketFile(string good)
        {
            var category = Category(session, good);
            if (category != null && Lines().FirstOrDefault(l => Is(Category(session, l.Good), category)) is { } same)
                return same.Part.Path;
            return parts.FirstOrDefault()?.Path ?? files.FirstOrDefault() ??
                throw new InvalidOperationException("The edit view has no market file.");
        }

        /// <summary>The buy-only form of a line, with its price multiplier.</summary>
        private List<string> BuyOnly(MarketLine line)
        {
            var values = Texts(line.Values).Skip(1).ToList();
            var defaults = Split(DefaultValues(Category(session, Good(line))));
            for (var place = values.Count; place < MarketLine.PriceIndex; place++)
                values.Add(defaults[place]);
            values[MarketLine.MinimumIndex - 1] = "0";
            values[MarketLine.MaximumIndex - 1] = "0";
            values[MarketLine.TransactionIndex - 1] = "1";
            return values;
        }

        private static bool SameValues(MarketLine line, IReadOnlyList<string> values) =>
            Texts(line.Values).Skip(1).SequenceEqual(values);

        private static string Line(string good, IReadOnlyList<string> values) =>
            values.Count == 0 ? good : $"{good}, {string.Join(", ", values)}";

        /// <summary>The values of a line text, as the index reads them: the good as a string, numbers as
        /// numbers.</summary>
        private static List<ValueBase> Values(string text) =>
            Split(text).Select(ValueOf).ToList();

        private static ValueBase ValueOf(string text) =>
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                ? new Int32Value(whole)
                : float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? new SingleValue(number, null)
                    : new StringValue(text);

        private static string? First(SectionView view, string name) =>
            view.Lines(name) is [{ Values: [var value, ..] }, ..] ? value.ToString() : null;

        /// <summary>A market section of the base. <see cref="Section"/> is null for a new section of the draft.</summary>
        private sealed class Part(string path, SectionRef? section, bool own, List<string?> lines, string? readOnlyReason)
        {
            public string Path { get; } = path;
            public SectionRef? Section { get; } = section;

            /// <summary>The section of the part: <see cref="Section"/>, or the new section after the commands.</summary>
            public SectionRef? Reference { get; set; } = section;
            public bool Own { get; } = own;
            public List<string?> Lines { get; } = lines;
            public IReadOnlyList<string?> Original { get; } = [.. lines];
            public string? ReadOnlyReason { get; } = readOnlyReason;
        }

        private sealed record DraftLine(Part Part, int Index, string Text, string Good);
    }

    /// <summary>The detail code of a market change that changes no line.</summary>
    public const string NoChange = "NoChange";
}
