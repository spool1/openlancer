using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using LibreLancer.Data.Schema.Equipment;
using LibreLancer.Data.Schema.Goods;
using ShipDef = LibreLancer.Data.Schema.Ships.Ship;

namespace OpenLancer.Data.Validation;

/// <summary>
/// Checks the references in the INI data that Librelancer resolves, before the engine loads the data.
/// </summary>
/// <remarks>
/// <para>Some broken references stop the engine load: a ship package whose hull good does not exist or is not a
/// ship hull. Others make the engine drop data with no message: a hull good whose ship is not in shiparch, an
/// unknown package addon, a market good that the engine ignores, a ship that a market sells with
/// min = max = 0, and a market ship whose letter case differs from the package (the engine finds packages with a
/// case-sensitive match). These checks find each case.</para>
/// <para>Vanilla data has problems of its own. Use <see cref="NewProblems"/> to report only the problems that the
/// mods add. <c>OpenLancer.Runtime.Validation.ProfileValidator</c> adds the checks that need the loaded data.</para>
/// </remarks>
public static class ModValidator
{
    private const string Source = "validator";

    /// <param name="Checks">The reference problems.</param>
    /// <param name="ParseLines">The warning and error lines of the engine's INI parser. A parse error can be the cause
    /// of a reference problem, for example an addon line that has too few values and is dropped.</param>
    public sealed record Report(IReadOnlyList<Diagnostic> Checks, IReadOnlyList<Diagnostic> ParseLines);

    /// <summary>Returns the reference problems in <paramref name="modded"/> that <paramref name="vanilla"/> does not have.</summary>
    public static IReadOnlyList<Diagnostic> NewProblems(FileSystem vanilla, FileSystem modded) =>
        Diagnostic.NewIn(Validate(vanilla).Checks, Validate(modded).Checks);

    /// <summary>
    /// True when a market line of a ship package gives the warning that the ship is not for sale (decision D511 of the
    /// console filter design): min = max = 0, and the base does not only buy the ship. Retail reads the sixth value of a
    /// <c>MarketGood</c> line as the transaction type: 0 buy and sell, 1 the base buys only, 2 the base sells only. Retail
    /// marks each ship line with min = max = 0 as type 1 on purpose. The fork keeps the type since OL-0045. A line with
    /// only default values gives the warning of <see cref="SkippedLine"/> instead.
    /// </summary>
    internal static bool ShipNotForSale(MarketGood item) =>
        item.Min == 0 && item.Max == 0 && (MarketTransaction)item.TransactionType != MarketTransaction.BuyOnly &&
        !item.HasOnlyDefaults;

    /// <summary>The warning of a market line in which each value after the good has its default value, or null. The
    /// games skip such a line, so the earlier line of the good counts (fork commit OL-0081).</summary>
    internal static string? SkippedLine(MarketGood item) => item.HasOnlyDefaults
        ? "each value after the good has its default value, so the games skip the line. The earlier line of the good " +
          "counts."
        : null;

    /// <summary>
    /// Why the engine ignores the market line of a good that is not a ship package, or null. A market sells equipment
    /// and commodities with equipment. A line of a hull good sets the price of its ship at the base, for the ship dealer
    /// and the trade-in value (fork commit OL-0080).
    /// </summary>
    /// <param name="category">The category of the good.</param>
    /// <param name="hasEquipment">True when an equipment section has the nickname of the good.</param>
    internal static string? MarketGoodProblem(GoodCategory category, bool hasEquipment) => category switch
    {
        GoodCategory.ShipHull => null,
        GoodCategory.Equipment or GoodCategory.Commodity => hasEquipment
            ? null
            : "the good has no equipment, so the engine ignores the entry.",
        _ => "a market can sell only ship packages, equipment and commodities, and set the price of a ship with its " +
             $"hull good. The engine ignores a {category} good.",
    };

    public static Report Validate(FileSystem fs)
    {
        FreelancerData data;
        IReadOnlyList<string> lines;
        using (var capture = EngineLogCapture.Start())
        {
            data = new FreelancerData(new FreelancerIni(fs), fs);
            data.LoadData();
            lines = capture.Stop();
        }
        var problems = new List<Diagnostic>();
        var context = new Context(data, problems);
        context.CheckNicknames();
        context.CheckGoods();
        context.CheckMarkets();
        context.CheckText();
        return new Report(problems.OrderBy(p => p.Message, StringComparer.Ordinal).ToList(),
            EngineLogCapture.ToDiagnostics(lines, EngineDataLoader.EngineSource).ToList());
    }

    private sealed class Context
    {
        private readonly FreelancerData data;
        private readonly List<Diagnostic> problems;
        private readonly List<Good> goods;
        private readonly List<ShipDef> ships;
        private readonly List<AbstractEquipment> equipment;
        private readonly Dictionary<string, Good> goodsByName;
        private readonly Dictionary<string, ShipDef> shipsByName;
        private readonly Dictionary<string, AbstractEquipment> equipmentByName;

        public Context(FreelancerData data, List<Diagnostic> problems)
        {
            this.data = data;
            this.problems = problems;
            // GameItemDb adds munitions and mines to the same equipment collection as all other items.
            var allEquipment = (data.Equipment?.Equip.Where(e => e is not Lod) ?? [])
                .Concat<AbstractEquipment>(data.Equipment?.Munitions ?? [])
                .Concat(data.Equipment?.Mines ?? []);
            goods = Named(data.Goods?.Goods ?? [], g => g.Nickname, "[Good]");
            ships = Named(data.Ships?.Ships ?? [], s => s.Nickname, "[Ship]");
            equipment = Named(allEquipment, e => e.Nickname, "equipment");
            goodsByName = ByName(goods, g => g.Nickname);
            shipsByName = ByName(ships, s => s.Nickname);
            equipmentByName = ByName(equipment, e => e.Nickname);
        }

        public void CheckNicknames()
        {
            var all = new List<(string Kind, string Nickname)>();
            all.AddRange(goods.Select(g => ("good", g.Nickname)));
            all.AddRange(ships.Select(s => ("ship", s.Nickname)));
            all.AddRange(equipment.Select(e => ("equipment", e.Nickname)));
            all.AddRange((data.Universe?.Bases ?? []).Where(b => b.Nickname != null).Select(b => ("base", b.Nickname)));
            all.AddRange((data.Universe?.Systems ?? []).Where(s => s.Nickname != null).Select(s => ("system", s.Nickname)));

            foreach (var (kind, nickname) in all.Where(n => n.Nickname.Any(c => c > 127)))
                Error($"The {kind} nickname '{nickname}' is not ASCII.");

            foreach (var group in all.GroupBy(n => (n.Kind, n.Nickname.ToLowerInvariant())).Where(g => g.Count() > 1))
                Error($"The {group.Key.Kind} nickname '{group.First().Nickname}' is used {group.Count()} times.");

            // A good and its equipment share one nickname by design, so compare distinct names only.
            foreach (var group in all.Select(n => n.Nickname).Distinct(StringComparer.OrdinalIgnoreCase)
                         .GroupBy(FLHash.CreateID).Where(g => g.Count() > 1))
                Error($"The nicknames {string.Join(", ", group.Order(StringComparer.Ordinal))} have the same hash.");
        }

        public void CheckGoods()
        {
            foreach (var good in goods)
            {
                switch (good.Category)
                {
                    case GoodCategory.ShipHull:
                        if (good.Ship == null)
                            Error($"Hull good '{good.Nickname}' has no ship. The engine load stops.");
                        else if (!shipsByName.ContainsKey(good.Ship))
                            Error($"Hull good '{good.Nickname}': ship '{good.Ship}' is not in shiparch. The engine drops its packages.");
                        break;
                    case GoodCategory.Ship:
                        CheckPackage(good);
                        break;
                    case GoodCategory.Equipment or GoodCategory.Commodity:
                        if (!equipmentByName.ContainsKey(good.Nickname))
                            Warning($"Good '{good.Nickname}' has no equipment with the same nickname. The engine ignores it.");
                        break;
                }
            }
        }

        private void CheckPackage(Good package)
        {
            var label = $"Ship package '{package.Nickname}'";
            if (package.Hull == null || !goodsByName.TryGetValue(package.Hull, out var hull) ||
                hull.Category != GoodCategory.ShipHull)
            {
                Error($"{label}: hull '{package.Hull}' is not a shiphull good. The engine load stops.");
                return;
            }

            var types = new List<AbstractEquipment>();
            foreach (var addon in package.Addons)
            {
                if (addon.Equipment == null || !equipmentByName.TryGetValue(addon.Equipment, out var item))
                    Error($"{label}: addon '{addon.Equipment}' is not equipment. The engine drops it.");
                else
                    types.Add(item);
            }
            if (!types.Any(t => t is Engine))
                Error($"{label}: the addons have no engine.");
            if (!types.Any(t => t is PowerCore))
                Error($"{label}: the addons have no power plant.");
        }

        public void CheckMarkets()
        {
            var bases = (data.Universe?.Bases ?? []).Where(b => b.Nickname != null).Select(b => b.Nickname)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packages = goods.Where(g => g.Category == GoodCategory.Ship).ToList();
            var packagesExact = packages.Select(g => g.Nickname).ToHashSet(StringComparer.Ordinal);
            var packagesAnyCase = packages.Select(g => g.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var market in data.Markets?.BaseGoods ?? [])
            {
                if (market.Base == null || !bases.Contains(market.Base))
                {
                    Error($"Market for base '{market.Base}': the base is not in universe.ini.");
                    continue;
                }
                foreach (var item in market.MarketGoods)
                {
                    var label = $"Market for base '{market.Base}', good '{item.Good}'";
                    if (item.Good == null)
                        Error($"{label}: no good nickname.");
                    else if (SkippedLine(item) is { } skipped)
                        Warning($"{label}: {skipped}");
                    else if (packagesExact.Contains(item.Good))
                    {
                        if (ShipNotForSale(item))
                            Warning($"{label}: min and max are 0, so the ship is not for sale.");
                    }
                    else if (packagesAnyCase.Contains(item.Good))
                        Error($"{label}: the letter case differs from the ship package. The engine will not find it.");
                    else if (!goodsByName.TryGetValue(item.Good, out var good))
                        Error($"{label}: the good does not exist.");
                    else if (MarketGoodProblem(good.Category, equipmentByName.ContainsKey(good.Nickname)) is { } problem)
                        Error($"{label}: {problem}");
                }
            }
        }

        public void CheckText()
        {
            var text = new InfocardManager(data.Freelancer.Resources);
            foreach (var ship in ships)
            {
                if (ship.IdsName != 0 && !text.HasStringResource(ship.IdsName))
                    Error($"Ship '{ship.Nickname}': ids_name {ship.IdsName} is not a string.");
                if (ship.IdsInfo != 0 && !text.HasXmlResource(ship.IdsInfo))
                    Error($"Ship '{ship.Nickname}': ids_info {ship.IdsInfo} is not an infocard.");
            }
            foreach (var good in goods.Where(g => g.IdsName != 0))
            {
                if (!text.HasStringResource(good.IdsName))
                    Error($"Good '{good.Nickname}': ids_name {good.IdsName} is not a string.");
            }
        }

        private void Error(string message) => problems.Add(Diagnostic.Error(Source, message));

        private void Warning(string message) => problems.Add(Diagnostic.Warning(Source, message));

        /// <summary>Reports the items without a nickname and returns the others.</summary>
        private List<T> Named<T>(IEnumerable<T> items, Func<T, string?> name, string kind)
        {
            var result = new List<T>();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(name(item)))
                    Error($"A {kind} section has no nickname.");
                else
                    result.Add(item);
            }
            return result;
        }

        private static Dictionary<string, T> ByName<T>(IEnumerable<T> items, Func<T, string> name)
        {
            var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                map.TryAdd(name(item), item);
            return map;
        }
    }
}
