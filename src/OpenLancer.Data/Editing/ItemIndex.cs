using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Equipment;
using LibreLancer.Data.Schema.Goods;
using LibreLancer.Data.Schema.Ships;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>A kind of item file. <c>freelancer.ini</c> lists the files of each kind in its <c>[Data]</c> section.</summary>
public enum ItemFileKind
{
    /// <summary><c>ships</c>: <c>[Ship]</c> sections.</summary>
    Ships,

    /// <summary><c>equipment</c>: guns, munitions, engines and the other equipment sections.</summary>
    Equipment,

    /// <summary><c>goods</c>: <c>[Good]</c> sections. A good sells equipment, a ship hull or a ship package.</summary>
    Goods,

    /// <summary><c>markets</c>: <c>[BaseGood]</c> sections, which tell what a base sells.</summary>
    Markets,
}

/// <summary>A link from an item to other sections of the item files.</summary>
public enum ItemRelation
{
    /// <summary>From an equipment section: the <c>[Good]</c> with the same nickname. The engine finds the
    /// equipment of a good by the nickname of the good.</summary>
    Good,

    /// <summary>From a <c>[Ship]</c>: the hull goods, the <c>[Good]</c> sections with <c>category = shiphull</c> and
    /// <c>ship</c> = the ship. The engine takes the price and the icon of the ship from the last one.</summary>
    Hull,

    /// <summary>From a <c>[Ship]</c> or a hull good: the packages, the <c>[Good]</c> sections with
    /// <c>category = ship</c> and <c>hull</c> = a hull good of the ship. A base sells a ship as a package.</summary>
    Package,

    /// <summary>From a gun, a mine dropper or a countermeasure dropper: the section that
    /// <c>projectile_archetype</c> names.</summary>
    Munition,

    /// <summary>From a package or a hull good: the <c>[Ship]</c> that it sells. A package names its hull good with
    /// <c>hull</c>, and the hull good names the ship with <c>ship</c>. When more than one section has the nickname, the
    /// engine keeps the last hull good and the last <c>[Ship]</c>, so the relation gives the ship of these.</summary>
    Ship,
}

/// <summary>One <c>marketgood</c> line of a <c>[BaseGood]</c> section.</summary>
/// <param name="Section">The <c>[BaseGood]</c> section.</param>
/// <param name="Base">The base that sells the good.</param>
/// <param name="Line">The position of the line among the <c>marketgood</c> lines of the section, from 0.</param>
/// <param name="Values">The values: the good, the rank, the reputation, the minimum and maximum stock, the transaction
/// type and the price multiplier. The engine ignores a line with fewer values.</param>
public sealed record MarketLine(SectionRef Section, string Base, int Line, IReadOnlyList<ValueBase> Values)
{
    /// <summary>The place of the rank in <see cref="Values"/>.</summary>
    public const int RankIndex = 1;

    /// <summary>The place of the reputation in <see cref="Values"/>.</summary>
    public const int ReputationIndex = 2;

    /// <summary>The place of the minimum stock in <see cref="Values"/>.</summary>
    public const int MinimumIndex = 3;

    /// <summary>The place of the maximum stock in <see cref="Values"/>.</summary>
    public const int MaximumIndex = 4;

    /// <summary>The place of the transaction type in <see cref="Values"/>.</summary>
    public const int TransactionIndex = 5;

    /// <summary>The place of the price multiplier in <see cref="Values"/>, the last value that the engine reads.</summary>
    public const int PriceIndex = 6;

    /// <summary>The transaction type of the line, or null when the line has no sixth value or when the value is not 0,
    /// 1 or 2.</summary>
    public MarketTransaction? Transaction =>
        Values.Count > TransactionIndex && Values[TransactionIndex].TryToInt32(out var type) &&
        Enum.IsDefined((MarketTransaction)type)
            ? (MarketTransaction)type
            : null;
}

/// <summary>
/// The item files of the edit view, the ship, equipment, goods and market files, with the links between their
/// sections. The index reads W of each file, so it shows the edits.
/// </summary>
/// <remarks>
/// <para>The index reads the table of each file from the shared <see cref="SectionTable"/>, and builds its maps again
/// only when the table of one of its own files changes. A call at the revision of W of the last refresh reads no file
/// (decision D121).</para>
/// <para>Each kind of file has its own nicknames: a gun and its good have the same nickname. A nickname that two
/// sections of one kind share is ambiguous, because the engine uses only one of them. <see cref="Find"/> then gives
/// null.</para>
/// </remarks>
internal sealed class ItemIndex
{
    /// <summary>The lines that name another section by its nickname, in their first value. A template copy and a
    /// rename change them (review finding F12).</summary>
    private static readonly Reference[] References =
    [
        new(ItemFileKind.Goods, "ship", ItemFileKind.Ships),
        new(ItemFileKind.Goods, "hull", ItemFileKind.Goods),
        new(ItemFileKind.Goods, "equipment", ItemFileKind.Equipment),
        new(ItemFileKind.Goods, "addon", ItemFileKind.Equipment),
        new(ItemFileKind.Equipment, "projectile_archetype", ItemFileKind.Equipment),
        new(ItemFileKind.Markets, "marketgood", ItemFileKind.Goods),
    ];

    private static readonly Dictionary<ItemFileKind, Type> Schemas = new()
    {
        [ItemFileKind.Ships] = typeof(ShiparchIni),
        [ItemFileKind.Equipment] = typeof(EquipmentIni),
        [ItemFileKind.Goods] = typeof(GoodsIni),
        [ItemFileKind.Markets] = typeof(MarketsIni),
    };

    /// <summary>The role of the rule data of each kind of item file (see <see cref="DataFiles"/>).</summary>
    private static readonly (ItemFileKind Kind, string Role)[] Roles =
    [
        (ItemFileKind.Ships, "ships"),
        (ItemFileKind.Equipment, "equipment"),
        (ItemFileKind.Goods, "goods"),
        (ItemFileKind.Markets, "markets"),
    ];

    private readonly IReadOnlyList<(string Path, ItemFileKind Kind)> files;
    private readonly SectionTable table;
    private readonly Func<int> revision;

    /// <summary>The table of each file of <see cref="files"/> at the last refresh, or null for a file that the edit
    /// view does not have.</summary>
    private readonly FileTable?[] indexed;

    private int checkedRevision = int.MinValue;
    private Dictionary<ItemFileKind, List<Item>> byKind = [];
    private Dictionary<(ItemFileKind, string), List<Item>> byNickname = [];
    private Dictionary<SectionRef, Item> byRef = [];

    /// <summary>The <c>[BaseGood]</c> sections of each base in load order, by the first <c>base</c> line in lower
    /// case.</summary>
    private Dictionary<string, List<Item>> marketsByBase = [];

    /// <param name="files">The item files in load order.</param>
    /// <param name="table">The shared section index of the edit view.</param>
    /// <param name="revision">Gives the revision of W (see <see cref="EditSession.CacheRevision"/>). A call at the
    /// revision of the last refresh reads no file.</param>
    public ItemIndex(IReadOnlyList<(string Path, ItemFileKind Kind)> files, SectionTable table, Func<int> revision)
    {
        this.files = files;
        this.table = table;
        this.revision = revision;
        indexed = new FileTable?[files.Count];
    }

    /// <summary>
    /// The item files that <c>freelancer.ini</c> lists, in load order for each kind, as the engine finds them: the
    /// <c>[Data]</c> entries <c>ships</c>, <c>equipment</c>, <c>goods</c> and <c>markets</c> in the folder of
    /// <c>data path</c>.
    /// </summary>
    public static List<(string Path, ItemFileKind Kind)> ReadPaths(DataFiles dataFiles) =>
        Roles.SelectMany(r => dataFiles.Paths(r.Role).Select(path => (path, r.Kind))).ToList();

    /// <summary>The <c>[ParsedIni]</c> class that reads the files of a kind.</summary>
    public static Type SchemaOf(ItemFileKind kind) => Schemas[kind];

    public ItemFileKind? KindOf(string path)
    {
        var normal = VfsPath.Normalize(path);
        return files.Where(f => VfsPath.Comparer.Equals(f.Path, normal)).Select(f => (ItemFileKind?)f.Kind).FirstOrDefault();
    }

    /// <summary>The sections of the files of a kind that are not child sections, in load order.</summary>
    public IReadOnlyList<SectionRef> List(ItemFileKind kind) => Items(kind).Select(i => i.Ref).ToList();

    /// <summary>The sections of the files of a kind with a nickname, in load order. The nickname compares without
    /// letter case.</summary>
    public IReadOnlyList<SectionRef> Named(ItemFileKind kind, string nickname) =>
        ItemsNamed(kind, nickname).Select(i => i.Ref).ToList();

    /// <summary>The one section of the files of a kind with a nickname, or null when no section or more than one
    /// section has it.</summary>
    public SectionRef? Find(ItemFileKind kind, string nickname) => One(kind, nickname)?.Ref;

    /// <summary>The section of W, or null when the index has no such section.</summary>
    public Section? Section(SectionRef target) => Get(target)?.Section;

    /// <summary>True when the file of a section is a binary (BINI) file.</summary>
    public bool IsBinary(SectionRef target) => table.File(target.Path)?.Document?.IsBinary ?? false;

    /// <summary>The child sections that follow a section in its file.</summary>
    public IReadOnlyList<Section> Children(SectionRef target)
    {
        if (Get(target) is not { } item)
            return [];
        var working = item.File.Working;
        return working.Skip(item.Index + 1).Take(SectionKeys.ChildCount(working, item.Index)).ToList();
    }

    /// <summary>The sections that a relation links an item to, in load order.</summary>
    public IReadOnlyList<SectionRef> Related(SectionRef target, ItemRelation relation) =>
        Get(target) is { } item ? RelatedItems(item, relation).Select(i => i.Ref).ToList() : [];

    /// <summary>
    /// The sections that belong to the same item as a section: a ship with its hull goods and packages, or
    /// equipment with its good, its munition and the good of the munition. A good belongs to the item of its
    /// equipment or its ship.
    /// </summary>
    public IReadOnlyList<SectionRef> Parts(SectionRef target)
    {
        if (Get(target) is not { } item)
            return [];
        var owner = Owner(item);
        var parts = new List<Item> { owner };
        if (owner.Kind == ItemFileKind.Ships)
        {
            var hulls = RelatedItems(owner, ItemRelation.Hull);
            parts.AddRange(hulls);
            parts.AddRange(hulls.SelectMany(h => RelatedItems(h, ItemRelation.Package)));
        }
        else if (owner.Kind == ItemFileKind.Equipment)
        {
            parts.AddRange(RelatedItems(owner, ItemRelation.Good));
            foreach (var munition in RelatedItems(owner, ItemRelation.Munition))
                parts.AddRange([munition, .. RelatedItems(munition, ItemRelation.Good)]);
        }
        return parts.Select(p => p.Ref).Distinct().ToList();
    }

    /// <summary>The <c>marketgood</c> lines of all market files that sell a good, in load order.</summary>
    public IReadOnlyList<MarketLine> MarketLines(string good)
    {
        var result = new List<MarketLine>();
        foreach (var market in Items(ItemFileKind.Markets))
        {
            var lines = market.Section.Where(e => Is(e.Name, "marketgood")).ToList();
            var baseName = First(market.Section, "base") ?? "";
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Count > 0 && Is(lines[i][0].ToString(), good))
                    result.Add(new MarketLine(market.Ref, baseName, i, lines[i].ToList()));
            }
        }
        return result;
    }

    /// <summary>The <c>marketgood</c> lines of all market files at a base, in load order. The base compares without
    /// letter case.</summary>
    public IReadOnlyList<MarketLine> BaseMarketLines(string baseNickname)
    {
        Refresh();
        var result = new List<MarketLine>();
        foreach (var market in marketsByBase.GetValueOrDefault(baseNickname.Trim().ToLowerInvariant()) ?? [])
        {
            var baseName = First(market.Section, "base")!;
            var lines = market.Section.Where(e => Is(e.Name, "marketgood")).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Count > 0)
                    result.Add(new MarketLine(market.Ref, baseName, i, lines[i].ToList()));
            }
        }
        return result;
    }

    /// <summary>The names of the lines that name a section of a kind by its nickname, for each section that has
    /// such a line (see <see cref="References"/>).</summary>
    public IEnumerable<(SectionRef Section, string Name)> ReferencesTo(ItemFileKind kind, string nickname)
    {
        foreach (var reference in References.Where(r => r.Target == kind))
        {
            foreach (var item in Items(reference.Source))
            {
                if (item.Section.Any(e => Is(e.Name, reference.Name) && e.Count > 0 && Is(e[0].ToString(), nickname)))
                    yield return (item.Ref, reference.Name);
            }
        }
    }

    /// <summary>
    /// The lines of a section of a kind in which the first value names a section by its nickname: the name of
    /// the line and the kind of the section that it names.
    /// </summary>
    public static IEnumerable<(string Name, ItemFileKind Target)> Links(ItemFileKind kind) =>
        References.Where(r => r.Source == kind).Select(r => (r.Name, r.Target));

    private Item? Get(SectionRef target)
    {
        Refresh();
        return byRef.GetValueOrDefault(target with { Path = VfsPath.Normalize(target.Path) ?? target.Path });
    }

    /// <summary>The one section of a kind with a nickname, or null.</summary>
    private Item? One(ItemFileKind kind, string? nickname) =>
        nickname != null && ItemsNamed(kind, nickname) is [var one] ? one : null;

    /// <summary>The item that a good belongs to: its equipment, or the ship of a hull good or of a package.</summary>
    private Item Owner(Item item)
    {
        if (item.Kind != ItemFileKind.Goods)
            return item;
        if (One(ItemFileKind.Equipment, item.Nickname) is { } equipment)
            return equipment;
        return ShipOf(item) ?? item;
    }

    /// <summary>The ship of a package or a hull good (see <see cref="ItemRelation.Ship"/>), or null.</summary>
    private Item? ShipOf(Item good)
    {
        // The engine finds the hull of a package among the hull goods only.
        var hull = Is(Category(good), "ship") ? Last(ItemFileKind.Goods, First(good.Section, "hull"), IsHull) : good;
        return hull != null && IsHull(hull)
            ? Last(ItemFileKind.Ships, First(hull.Section, "ship"), s => Is(s.Section.Name, "Ship"))
            : null;

        static bool IsHull(Item item) => Is(Category(item), "shiphull");
    }

    /// <summary>The last section of a kind with a nickname that a filter accepts, or null. When more than one section
    /// has the nickname, the engine keeps the last one.</summary>
    private Item? Last(ItemFileKind kind, string? nickname, Func<Item, bool> accepts) =>
        nickname == null ? null : ItemsNamed(kind, nickname).LastOrDefault(accepts);

    private List<Item> RelatedItems(Item item, ItemRelation relation)
    {
        var goods = Items(ItemFileKind.Goods);
        switch (relation)
        {
            case ItemRelation.Good when item.Kind == ItemFileKind.Equipment && item.Nickname != null:
                return ItemsNamed(ItemFileKind.Goods, item.Nickname).Where(g => Is(g.Section.Name, "Good")).ToList();
            case ItemRelation.Hull when Is(item.Section.Name, "Ship") && item.Nickname != null:
                return goods.Where(g => Is(Category(g), "shiphull") && Is(First(g.Section, "ship"), item.Nickname)).ToList();
            case ItemRelation.Package when Is(item.Section.Name, "Ship"):
                return RelatedItems(item, ItemRelation.Hull).SelectMany(h => RelatedItems(h, ItemRelation.Package)).ToList();
            case ItemRelation.Package when Is(Category(item), "shiphull") && item.Nickname != null:
                return goods.Where(g => Is(Category(g), "ship") && Is(First(g.Section, "hull"), item.Nickname)).ToList();
            case ItemRelation.Ship when item.Kind == ItemFileKind.Goods:
                return ShipOf(item) is { } ship ? [ship] : [];
            case ItemRelation.Munition when item.Kind == ItemFileKind.Equipment &&
                                            First(item.Section, "projectile_archetype") is { } munition:
                return ItemsNamed(ItemFileKind.Equipment, munition);
            default:
                return [];
        }
    }

    private List<Item> Items(ItemFileKind kind)
    {
        Refresh();
        return byKind.GetValueOrDefault(kind) ?? [];
    }

    private List<Item> ItemsNamed(ItemFileKind kind, string nickname)
    {
        Refresh();
        return byNickname.GetValueOrDefault((kind, nickname.ToLowerInvariant())) ?? [];
    }

    /// <summary>Indexes the files whose W changed since the last call. A call at the same revision reads no
    /// file.</summary>
    private void Refresh()
    {
        var now = revision();
        if (now == checkedRevision)
            return;
        var changed = checkedRevision == int.MinValue;
        checkedRevision = now;
        for (var i = 0; i < files.Count; i++)
        {
            var current = table.File(files[i].Path);
            changed |= !ReferenceEquals(current, indexed[i]);
            indexed[i] = current;
        }
        if (!changed)
            return;
        byKind = [];
        byNickname = [];
        byRef = [];
        marketsByBase = [];
        for (var f = 0; f < files.Count; f++)
        {
            if (indexed[f] is not { } file)
                continue;
            var kind = files[f].Kind;
            var list = byKind.TryGetValue(kind, out var l) ? l : byKind[kind] = [];
            for (var i = 0; i < file.Working.Count; i++)
            {
                if (SectionKeys.IsChild(file.Working[i].Name))
                    continue;
                var item = new Item(file.Ref(i), kind, file.Working[i], file, i);
                list.Add(item);
                byRef[item.Ref] = item;
                if (item.Nickname is { } nickname)
                {
                    var key = (kind, nickname.ToLowerInvariant());
                    (byNickname.TryGetValue(key, out var named) ? named : byNickname[key] = []).Add(item);
                }
                // A [BaseGood] has no key. Its first base line names the base.
                if (kind == ItemFileKind.Markets && First(item.Section, "base") is { } baseName)
                {
                    var key = baseName.Trim().ToLowerInvariant();
                    (marketsByBase.TryGetValue(key, out var sold) ? sold : marketsByBase[key] = []).Add(item);
                }
            }
        }
    }

    private static string? Category(Item item) => Is(item.Section.Name, "Good") ? First(item.Section, "category") : null;

    /// <summary>The first value of the first line of a name, as text, or null.</summary>
    internal static string? First(Section section, string name) =>
        section.FirstOrDefault(e => Is(e.Name, name)) is { Count: > 0 } entry ? entry[0].ToString() : null;

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed record Reference(ItemFileKind Source, string Name, ItemFileKind Target);

    /// <param name="File">The table of the W state of the file that holds the section.</param>
    /// <param name="Index">The index of the section in W of its file.</param>
    private sealed record Item(SectionRef Ref, ItemFileKind Kind, Section Section, FileTable File, int Index)
    {
        public string? Nickname => Ref.Id.Key;
    }
}
