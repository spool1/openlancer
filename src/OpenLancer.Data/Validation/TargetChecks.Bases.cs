using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Validation;

/// <summary>
/// The content checks of the bases for a retail target (section D of the bases design). They read the merged data of
/// the target, and they report only what the mods add.
/// </summary>
/// <remarks>
/// <para>Retail facts: the game makes a room id for each door, also for a room that the base does not have. One lookup
/// reads that room without a test. The game reads the first line of an <c>[MBase]</c> as its nickname. An
/// <c>[MBase]</c> whose nickname names no base stops the read of the blocks after it. The game copies the nickname of a
/// base into a buffer of 64 bytes to find its <c>[MBase]</c>. It drops a fixture whose character comes later in the
/// block. The id of a base is the FLHash of its nickname, and the game drops a second base with the same id. Vanilla
/// lists each ship package at each base that sells ships.</para>
/// <para>The checks give errors, except the check of the ship lines. It gives a warning, and only when the ship markets
/// of the target use buy-only lines (see <see cref="BaseMarkets.UsesBuyOnlyShipLines"/>). Vanilla does, and HD Edition
/// 0.7.1 does not. The ship lines do not give the trade-in value of a ship. They keep the vanilla form of the ship
/// markets.</para>
/// </remarks>
public static partial class TargetChecks
{
    /// <summary>The most characters of a base nickname for which the game finds the <c>[MBase]</c>.</summary>
    private const int BarDataNameLength = 63;

    /// <summary>The most ship packages that the warning of the ship lines names.</summary>
    private const int ShownShips = 5;

    /// <summary>The doors, the names and the bar data of the bases, and the ship lines of the bases that sell
    /// ships.</summary>
    private static IEnumerable<Finding> Bases(Side side, Scope scope) =>
        BaseNames(side, scope).Concat(BarData(side, scope)).Concat(Doors(side, scope)).Concat(ShipLines(side, scope));

    /// <summary>The length of each base nickname, and two bases with one id, when the mods change
    /// <c>universe.ini</c>.</summary>
    private static IEnumerable<Finding> BaseNames(Side side, Scope scope)
    {
        if (side.UniversePath is not { } universe || !scope.Changed.Contains(universe))
            yield break;
        var first = new Dictionary<uint, string>();
        foreach (var entry in side.Bases)
        {
            var key = entry.Nickname.ToLowerInvariant();
            if (entry.Nickname.Length > BarDataNameLength)
                yield return new($"basename|{key}", Problem(true, universe, entry.Id, "nickname",
                    $"Base '{entry.Nickname}': the nickname has more than {BarDataNameLength} characters. The game then " +
                    "ignores the bar data of the base. Give the base a shorter nickname."));
            var hash = FLHash.CreateID(entry.Nickname);
            if (!first.TryAdd(hash, entry.Nickname))
                yield return new($"basehash|{hash}|{key}", Problem(true, universe, entry.Id, "nickname",
                    $"Base '{entry.Nickname}': the game already has the base '{first[hash]}' with the same nickname or " +
                    "the same id. The game drops one of the two bases. Give the base another nickname."));
        }
    }

    /// <summary>The <c>[MBase]</c> blocks, when the mods change <c>mBases.ini</c> or <c>universe.ini</c>: the first
    /// line, the base of the nickname and the order of the fixtures.</summary>
    private static IEnumerable<Finding> BarData(Side side, Scope scope)
    {
        if (side.MBasesPath is not { } path ||
            !scope.Changed.Contains(path) && (side.UniversePath is not { } universe || !scope.Changed.Contains(universe)) ||
            side.Sections(path) is not { } sections)
            yield break;
        var bases = side.Bases.Select(b => b.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = SectionIdentity.Identify(sections);
        string? block = null;
        var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (Is(section.Name, "MBase"))
            {
                block = ItemIndex.First(section, "nickname");
                characters.Clear();
                var label = block ?? ids[i].ToString();
                if (section.Count > 0 && !Is(section[0].Name, "nickname"))
                    yield return new($"mbasefirst|{label.ToLowerInvariant()}", Problem(true, path, ids[i], "nickname",
                        $"[MBase] {label}: the first line is not the nickname line. The game reads the first line as the " +
                        "nickname. Put the nickname line first."));
                if (block != null && !bases.Contains(block))
                    yield return new($"mbasenobase|{block.ToLowerInvariant()}", Problem(true, path, ids[i], "nickname",
                        $"[MBase] {block}: no base has this nickname. The game stops reading the bar data of the bases " +
                        "after this block."));
                continue;
            }
            if (block == null)
                continue;
            if (Is(section.Name, "GF_NPC") && ItemIndex.First(section, "nickname") is { } character)
                characters.Add(character);
            else if (Is(section.Name, "MRoom"))
            {
                var room = ItemIndex.First(section, "nickname");
                foreach (var fixture in section.Where(e => Is(e.Name, "fixture") && e.Count > 0)
                             .Select(e => e[0].ToString()?.Trim()).OfType<string>()
                             .Where(f => f.Length > 0 && !characters.Contains(f)))
                    yield return new(string.Join("|", "fixture", block, room, fixture).ToLowerInvariant(),
                        Problem(true, path, ids[i], "fixture",
                            $"[MBase] {block}: the fixture {fixture} in the room {room} names no [GF_NPC] before it. The " +
                            "game shows no character at this place."));
            }
        }
    }

    /// <summary>The doors of the rooms of the bases that the mods can change (see <see cref="Scope.DoorBases"/>): each
    /// door must lead to a room of the base. A door without <c>room_switch</c> gives no finding.</summary>
    private static IEnumerable<Finding> Doors(Side side, Scope scope)
    {
        foreach (var entry in side.Bases.Where(b => b.File != null && scope.DoorBases.Contains(b.Nickname)))
        {
            var rooms = side.Rooms(entry.File!);
            var names = rooms.Select(r => r.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var room in rooms.Where(r => r.File != null))
            {
                foreach (var (door, target) in side.Doors(room.File!).Where(d => !names.Contains(d.Target)))
                    yield return new(
                        string.Join("|", "door", entry.Nickname, room.Nickname, door, target).ToLowerInvariant(),
                        Problem(true, entry.File!, room.Id, "file",
                            $"Base '{entry.Nickname}': the door {door} in the room {room.Nickname} leads to {target}, but " +
                            $"the base has no room {target}. The game can crash when the player uses the door."));
            }
        }
    }

    /// <summary>
    /// The ship lines of each base that sells a ship and whose market lines the mods add or change. The check runs only
    /// when the ship markets of the target use buy-only lines, as vanilla lists each ship package at each base that sells
    /// ships. A new ship package of a mod gives no finding for the bases that the mods do not change.
    /// </summary>
    private static IEnumerable<Finding> ShipLines(Side side, Scope scope)
    {
        if (!scope.ChangesMarkets || !scope.UsesBuyOnlyShipLines)
            yield break;
        var packages = side.ShipPackages;
        var bases = side.Bases.Select(b => b.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (baseName, lines) in side.MarketLines.Where(m => bases.Contains(m.Key) && scope.MarketBases.Contains(m.Key)))
        {
            // The game keeps the last line of a good that it does not skip.
            var kept = BaseMarkets.KeptLines(lines, l => l.Good, l => l.Values);
            if (!kept.Any(l => packages.Contains(l.Good) && BaseMarkets.Sells(l.Values, ship: true)))
                continue;
            var listed = kept.Select(l => l.Good).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = packages.Where(p => !listed.Contains(p)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count == 0)
                continue;
            var at = lines.FirstOrDefault(l => packages.Contains(l.Good)) ?? lines[0];
            var list = missing.Count == 1
                ? $"the ship package {missing[0]}"
                : $"{missing.Count} ship packages: {string.Join(", ", missing.Take(ShownShips))}" +
                  (missing.Count > ShownShips ? $" and {missing.Count - ShownShips} more" : "");
            yield return new($"shiplines|{baseName.ToLowerInvariant()}|{string.Join(",", missing).ToLowerInvariant()}",
                Problem(false, at.Path, at.Id, "marketgood",
                    $"Base '{baseName}': the base sells ships, but its market has no line for {list}. The ship " +
                    "markets of this game list each ship package at each base that sells ships. " +
                    BaseChecks.ShipLinesNote));
        }
    }

    /// <summary>One <c>[Base]</c> of <c>universe.ini</c>.</summary>
    /// <param name="File">The install path of the base file of the last <c>file</c> line, or null.</param>
    /// <param name="Lines">The text of all lines of the section, to compare the two sides.</param>
    private sealed record BaseEntry(string Nickname, SectionIdentity Id, string? File, string Lines);

    /// <summary>One <c>[Room]</c> of a base file.</summary>
    /// <param name="File">The install path of the room file of the last <c>file</c> line, or null.</param>
    private sealed record RoomEntry(string Nickname, SectionIdentity Id, string? File);

    /// <summary>One <c>marketgood</c> line of a base.</summary>
    /// <param name="Path">The market file.</param>
    /// <param name="Id">The <c>[BaseGood]</c> section.</param>
    private sealed record MarketEntry(string Path, SectionIdentity Id, string Good, IReadOnlyList<ValueBase> Values);

    private sealed partial class Side
    {
        private IReadOnlyList<BaseEntry>? bases;
        private readonly Dictionary<string, IReadOnlyList<RoomEntry>> rooms = new(VfsPath.Comparer);
        private IReadOnlySet<string>? shipPackages;
        private IReadOnlyDictionary<string, List<MarketEntry>>? marketLines;

        /// <summary>The install path of <c>universe.ini</c>, or null.</summary>
        public string? UniversePath => Editing.SystemFiles.ReadUniversePath(FreelancerIni);

        /// <summary>The install path of <c>mBases.ini</c>, or null. The game reads it at a fixed path.</summary>
        public string? MBasesPath => DataPath(BaseFiles.MBasesValue);

        /// <summary>The <c>[Base]</c> sections of <c>universe.ini</c> with a nickname, in file order.</summary>
        public IReadOnlyList<BaseEntry> Bases => bases ??= ReadBases();

        private List<BaseEntry> ReadBases()
        {
            if (UniversePath is not { } universe)
                return [];
            return Each(universe, "Base")
                .Select(b => (b.Section, b.Id, Nickname: ItemIndex.First(b.Section, "nickname")?.Trim()))
                .Where(b => !string.IsNullOrEmpty(b.Nickname))
                .Select(b => new BaseEntry(b.Nickname!, b.Id, Last(b.Section, "file") is { } file ? DataPath(file) : null,
                    string.Join("\n", b.Section.Select(IniSectionDiff.LineKey))))
                .ToList();
        }

        /// <summary>The <c>[Room]</c> sections of a base file with a nickname, in file order.</summary>
        public IReadOnlyList<RoomEntry> Rooms(string baseFile)
        {
            if (!rooms.TryGetValue(baseFile, out var result))
                rooms[baseFile] = result = Each(baseFile, "Room")
                    .Where(r => Last(r.Section, "nickname") != null)
                    .Select(r => new RoomEntry(Last(r.Section, "nickname")!, r.Id,
                        Last(r.Section, "file") is { } file ? DataPath(file) : null))
                    .ToList();
            return result;
        }

        /// <summary>The doors of a room file: the name and the <c>room_switch</c> of each <c>[Hotspot]</c> with the
        /// behavior <c>ExitDoor</c> and a <c>room_switch</c>.</summary>
        public IEnumerable<(string Name, string Target)> Doors(string roomFile) =>
            Each(roomFile, "Hotspot")
                .Where(h => Is(Last(h.Section, "behavior"), "ExitDoor") && Last(h.Section, "room_switch") != null)
                .Select(h => (Last(h.Section, "name") ?? "", Last(h.Section, "room_switch")!));

        /// <summary>The ship packages of the goods files: the goods of the category <c>ship</c>.</summary>
        public IReadOnlySet<string> ShipPackages => shipPackages ??= DataFiles("goods")
            .SelectMany(p => Each(p, "Good"))
            .Where(g => Is(Last(g.Section, "category"), BaseMarkets.Ship))
            .Select(g => ItemIndex.First(g.Section, "nickname")?.Trim()).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>The <c>marketgood</c> lines of each base of the market files, in load order.</summary>
        public IReadOnlyDictionary<string, List<MarketEntry>> MarketLines => marketLines ??= ReadMarkets();

        private Dictionary<string, List<MarketEntry>> ReadMarkets()
        {
            var result = new Dictionary<string, List<MarketEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in DataFiles("markets"))
            {
                foreach (var (section, id) in Each(path, "BaseGood"))
                {
                    if (ItemIndex.First(section, "base")?.Trim() is not { Length: > 0 } baseName)
                        continue;
                    if (!result.TryGetValue(baseName, out var lines))
                        result[baseName] = lines = [];
                    foreach (var line in section.Where(e => Is(e.Name, "marketgood") && e.Count > 0))
                        lines.Add(new MarketEntry(path, id, line[0].ToString()!.Trim(), line.ToList()));
                }
            }
            return result;
        }

        /// <summary>True when a file has a <c>[Hotspot]</c> section, so its doors can change.</summary>
        public bool HasHotspot(string path) => Each(path, "Hotspot").Any();

        /// <summary>The first value of the last line of a key with a value, trimmed, or null.</summary>
        private static string? Last(Section section, string key) =>
            section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0)?[0].ToString()?.Trim() is { Length: > 0 } value
                ? value
                : null;
    }

    private sealed partial class Scope
    {
        private HashSet<string>? doorBases;
        private bool? buyOnly;
        private bool? changesMarkets;
        private HashSet<string>? marketBases;

        /// <summary>
        /// The bases whose doors the checks read on both sides. These are the bases whose <c>[Base]</c>, base file or
        /// room file the mods add or change. Only a changed file with a <c>[Hotspot]</c> can change a door. So the
        /// checks read the base files of all bases only for such a file.
        /// </summary>
        public IReadOnlySet<string> DoorBases => doorBases ??= FindDoorBases();

        /// <summary>The bases whose market lines differ on the two sides: the mods add, change or remove a line of
        /// the base.</summary>
        public IReadOnlySet<string> MarketBases => marketBases ??= FindMarketBases();

        private HashSet<string> FindMarketBases()
        {
            static string Text(List<MarketEntry> lines) =>
                string.Join("\n", lines.Select(l => string.Join(",", l.Values.Select(v => v.ToString()?.Trim()))));
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!ChangesMarkets)
                return result;
            foreach (var name in without.MarketLines.Keys.Union(with.MarketLines.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (!without.MarketLines.TryGetValue(name, out var before) || !with.MarketLines.TryGetValue(name, out var after) ||
                    Text(before) != Text(after))
                    result.Add(name);
            }
            return result;
        }

        /// <summary>True when the mods change a market file or a goods file.</summary>
        public bool ChangesMarkets => changesMarkets ??= new[] { without, with }
            .SelectMany(s => s.DataFiles("markets").Concat(s.DataFiles("goods"))).Any(Changed.Contains);

        /// <summary>True when the ship markets of the data without the mods use buy-only lines (see
        /// <see cref="BaseMarkets.UsesBuyOnlyShipLines"/>). Both sides use this answer, so their findings
        /// compare.</summary>
        public bool UsesBuyOnlyShipLines => buyOnly ??= BaseMarkets.UsesBuyOnlyShipLines(
            without.MarketLines.Values.SelectMany(l => l).Where(l => without.ShipPackages.Contains(l.Good))
                .Select(l => l.Values));

        private HashSet<string> FindDoorBases()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in without.Bases)
                before.TryAdd(entry.Nickname, entry.Lines);
            foreach (var entry in with.Bases)
            {
                if (!before.TryGetValue(entry.Nickname, out var lines) || lines != entry.Lines)
                    result.Add(entry.Nickname);
            }
            var roomFiles = Changed.Where(p => without.HasHotspot(p) || with.HasHotspot(p)).ToHashSet(VfsPath.Comparer);
            foreach (var side in new[] { without, with })
            {
                foreach (var entry in side.Bases.Where(b => b.File != null))
                {
                    if (Changed.Contains(entry.File!) ||
                        roomFiles.Count > 0 && side.Rooms(entry.File!).Any(r => r.File != null && roomFiles.Contains(r.File)))
                        result.Add(entry.Nickname);
                }
            }
            return result;
        }
    }
}
