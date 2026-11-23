using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>The kind of a base, from its start room.</summary>
public enum BaseKind
{
    /// <summary>The start room is Deck: the player lands on the deck of a station.</summary>
    Station,

    /// <summary>The start room is Cityscape or Planetscape: the player lands on a planet.</summary>
    Planet,

    /// <summary>Another start room, or none.</summary>
    Other,
}

/// <summary>A dealer of a room: the hotspot that opens a dealer screen.</summary>
public enum BaseDealer
{
    /// <summary>A hotspot with the behavior <c>StartDealer</c>: the commodity trader.</summary>
    CommodityTrader,

    /// <summary>A hotspot with the behavior <c>StartEquipDealer</c>: the equipment dealer.</summary>
    EquipmentDealer,

    /// <summary>A hotspot with the behavior <c>StartShipDealer</c>: the ship dealer.</summary>
    ShipDealer,
}

/// <summary>A door of a room: a hotspot with the behavior <c>ExitDoor</c>.</summary>
/// <param name="Name">The name of the hotspot, for example <c>IDS_HOTSPOT_BAR</c>. The nav bar of the base screens
/// shows a button for it.</param>
/// <param name="Target">The room of <c>room_switch</c>, or null for a door without the line.</param>
/// <param name="SetVirtualRoom">The virtual room of <c>set_virtual_room</c>, or null.</param>
public sealed record BaseDoor(string Name, string? Target, string? SetVirtualRoom);

/// <summary>One room of a base.</summary>
/// <param name="Section">The <c>[Room]</c> section of the base file.</param>
/// <param name="Nickname">The nickname of the room, or an empty text for a room without a nickname.</param>
/// <param name="File">The install path of the room file, or null when the room names no file.</param>
/// <param name="Shared">True when another base uses the room file too.</param>
/// <param name="Own">True when the active mod adds the room file in its own folder.</param>
/// <param name="Dealers">The dealers of the room file, each one time.</param>
/// <param name="VirtualRooms">The virtual rooms of the room file: the <c>room_switch</c> values of the hotspots with
/// the behavior <c>VirtualRoom</c>, for example Trader. A virtual room is a dealer inside the room, not a door.</param>
/// <param name="ShipPlaces">The number of <c>[ForSaleShipPlacement]</c> sections: the most ships that the room
/// shows for sale.</param>
/// <param name="Doors">The doors of the room file, in file order.</param>
public sealed record BaseRoomView(SectionRef Section, string Nickname, string? File, bool Shared, bool Own,
    IReadOnlyList<BaseDealer> Dealers, IReadOnlyList<string> VirtualRooms, int ShipPlaces, IReadOnlyList<BaseDoor> Doors);

/// <summary>One character of the bar data of a base: a <c>[GF_NPC]</c> of its <c>[MBase]</c>.</summary>
/// <param name="Section">The <c>[GF_NPC]</c> section.</param>
/// <param name="Role">The role of the fixture that places the character, for example <c>bartender</c>, or null for a
/// character of the crowd.</param>
/// <param name="Room">The room of the character: its <c>room</c> line, or the room of its fixture.</param>
/// <param name="Faction">The faction of the <c>[BaseFaction]</c> whose <c>npc</c> line names the character, or
/// null.</param>
/// <param name="Affiliation">The faction of the <c>affiliation</c> line, or null.</param>
/// <param name="Voice">The voice of the character, or null.</param>
public sealed record BaseCharacter(SectionRef Section, string Nickname, string? Role, string? Room, string? Faction,
    string? Affiliation, string? Voice);

/// <summary>One market line of a base.</summary>
/// <param name="Line">The <c>marketgood</c> line.</param>
/// <param name="Origin">The layer that sets the <c>marketgood</c> lines of its <c>[BaseGood]</c>.</param>
/// <param name="ModId">The mod of the layer, or null for the base data.</param>
/// <param name="CanChange">True when the editor can change the line (see <see cref="BaseView.CanChange"/>).</param>
public sealed record BaseMarketRow(MarketLine Line, ValueOrigin Origin, string? ModId, bool CanChange)
{
    /// <summary>The good of the line.</summary>
    public string Good => Line.Values[0].ToString().Trim();
}

/// <summary>An object of a system file that names the base.</summary>
/// <param name="Section">The <c>[Object]</c> section.</param>
/// <param name="DocksWith">True when its <c>dock_with</c> line names the base: the player docks here.</param>
/// <param name="HasBaseLine">True when its <c>base</c> line names the base.</param>
public sealed record BaseDockObject(SectionRef Section, bool DocksWith, bool HasBaseLine);

/// <summary>One <c>terrain_*</c> line of a <c>[Base]</c>: the solar archetype that a room scene shows for a
/// <c>$terrain_*</c> template.</summary>
public sealed record BaseTerrainLine(string Key, string Value);

/// <summary>The entries of the picker of a key that names a part of a base (see <see cref="BaseView.PartList"/>), with
/// the number of lines of the base that name each entry.</summary>
/// <param name="List">The entries: the rooms or the characters of the base.</param>
/// <param name="Uses">The number of lines of the base that name each entry, by value without letter case.</param>
public sealed record BasePartList(ListView List, IReadOnlyDictionary<string, int> Uses)
{
    /// <summary>The number of lines of the base that name an entry, for the footer of the picker.</summary>
    public int UsedBy(ListEntry entry) => Uses.GetValueOrDefault(entry.Value.Trim());
}

/// <summary>
/// The parts of one base as W gives them now (section C.1 of the bases design): its <c>[Base]</c> entry of
/// <c>universe.ini</c>, its base file with <c>[BaseInfo]</c> and its rooms, the <c>[MBase]</c> of <c>mBases.ini</c>
/// with its characters, its market lines over all market files, and the objects that dock with it. An unsaved edit
/// counts.
/// </summary>
/// <remarks>The view reads the base file, the room files and <c>mBases.ini</c> as documents of the session. It is a
/// snapshot: make a new view after an edit.</remarks>
public sealed class BaseView
{
    private const string Hotspot = "Hotspot";

    private BaseView(string nickname, SectionRef entry)
    {
        Nickname = nickname;
        Entry = entry;
    }

    /// <summary>The nickname of the base, as its <c>[Base]</c> spells it.</summary>
    public string Nickname { get; }

    /// <summary>The <c>[Base]</c> of <c>universe.ini</c>.</summary>
    public SectionRef Entry { get; }

    /// <summary>The system of the <c>system</c> line, or null.</summary>
    public string? System { get; private init; }

    /// <summary>The value of the <c>strid_name</c> line: a text id or a text placeholder, or null.</summary>
    public string? Name { get; private init; }

    /// <summary>The install path of the base file, or null when the <c>file</c> line names no path.</summary>
    public string? File { get; private init; }

    /// <summary>The <c>[BaseInfo]</c> of the base file, or null.</summary>
    public SectionRef? Info { get; private init; }

    /// <summary>The <c>start_room</c> of <c>[BaseInfo]</c>, or null.</summary>
    public string? StartRoom { get; private init; }

    /// <summary>The kind of the base, from the start room.</summary>
    public BaseKind Kind { get; private init; }

    /// <summary>The rooms of the base file, in file order.</summary>
    public IReadOnlyList<BaseRoomView> Rooms { get; private init; } = [];

    /// <summary>The <c>[MBase]</c> of the base, or null. When two sections have the nickname, the fork uses the last
    /// one, so the view does too.</summary>
    public SectionRef? MBase { get; private init; }

    /// <summary>The children of the <c>[MBase]</c>, in file order: <c>[MVendor]</c>, <c>[BaseFaction]</c>,
    /// <c>[GF_NPC]</c> and <c>[MRoom]</c> sections.</summary>
    public IReadOnlyList<SectionRef> MBaseParts { get; private init; } = [];

    /// <summary>The characters of the <c>[MBase]</c>, in file order.</summary>
    public IReadOnlyList<BaseCharacter> Characters { get; private init; } = [];

    /// <summary>The market lines of the base over all market files, in load order.</summary>
    public IReadOnlyList<BaseMarketRow> Market { get; private init; } = [];

    /// <summary>The objects of the system files whose <c>dock_with</c> or <c>base</c> line names the base.</summary>
    public IReadOnlyList<BaseDockObject> DockObjects { get; private init; } = [];

    /// <summary>The object with a <c>base</c> line of the base and the same name text as the base, or null. The games
    /// link the base to this object. The trade lists of the base screens skip a base without it. An object of the
    /// system of the base comes before an object of another system.</summary>
    public SectionRef? NamedObject { get; private init; }

    /// <summary>The <c>terrain_*</c> lines of the <c>[Base]</c>, in file order.</summary>
    public IReadOnlyList<BaseTerrainLine> Terrain { get; private init; } = [];

    /// <summary>The layer that adds the base.</summary>
    public ValueOrigin Origin { get; private init; }

    /// <summary>The mod of <see cref="Origin"/>, or null for the base data.</summary>
    public string? OriginMod { get; private init; }

    /// <summary>
    /// True when the editor can change a market line. A line of the active mod changes in its place. A line of a lower
    /// layer changes through a later line of the active mod in the same market file (see
    /// <see cref="MarketPlans.CanChange"/>).
    /// </summary>
    public static bool CanChange(EditSession session, MarketLine line) => MarketPlans.CanChange(session, line);

    /// <summary>The view of a base, or null when no <c>[Base]</c> of W of <c>universe.ini</c> has the nickname. When
    /// two sections have it, the first one counts.</summary>
    internal static BaseView? Of(EditSession session, string nickname)
    {
        if (EntryOf(session, nickname) is not { } entry || session.WorkingSection(entry) is not { } section)
            return null;
        var name = entry.Id.Key!;
        var file = session.Bases.FileOf(name);
        var info = file == null ? null : session.Sections(file).FirstOrDefault(s => Is(s.Id.Type, "BaseInfo"));
        var startRoom = info is { } i && session.WorkingSection(i) is { } infoSection ? Last(infoSection, "start_room") : null;
        var (mbase, parts) = MBaseOf(session, name);
        var market = session.Items.BaseMarketLines(name)
            .Select(l =>
            {
                var (origin, mod) = session.Origin(l.Section, "marketgood");
                return new BaseMarketRow(l, origin, mod, CanChange(session, l));
            })
            .ToList();
        var docks = DockObjectsOf(session, name);
        var (baseOrigin, baseMod) = session.Origin(entry, "nickname");
        return new BaseView(name, entry)
        {
            System = Last(section, "system"),
            Name = Last(section, "strid_name"),
            File = file,
            Info = info,
            StartRoom = startRoom,
            Kind = KindOf(startRoom),
            Rooms = RoomsOf(session, name, file),
            MBase = mbase,
            MBaseParts = parts,
            Characters = CharactersOf(session, mbase, parts),
            Market = market,
            DockObjects = docks,
            NamedObject = NamedObjectOf(session, docks, Last(section, "strid_name"), Last(section, "system")),
            Terrain = section.Where(e => e.Name.StartsWith("terrain_", StringComparison.OrdinalIgnoreCase) && e.Count > 0)
                .Select(e => new BaseTerrainLine(e.Name, e[0].ToString().Trim()))
                .ToList(),
            Origin = baseOrigin,
            OriginMod = baseMod,
        };
    }

    /// <summary>
    /// The entries of the picker of a key that names a part of the base of a section of <c>mBases.ini</c>, or null for
    /// another key. <c>[GF_NPC] room</c> and <c>[MRoom] nickname</c> name a room of the base: the entries are the list
    /// <c>room_of_base</c> of the base file of the base. Value 0 of <c>[MRoom] fixture</c> and of
    /// <c>[BaseFaction] npc</c> names a character of the <c>[MBase]</c>. A list rule cannot give these entries, because
    /// the base of the section names them, not the file of the section. So the counts of the data lists do not see the
    /// lines that name these parts, and the result has its own counts.
    /// </summary>
    internal static BasePartList? PartList(EditSession session, SectionRef section, string key)
    {
        if (!session.IsMBasesFile(section.Path) || BaseOf(section) is not { } baseName)
            return null;
        switch (section.Id.Type.ToLowerInvariant(), key.ToLowerInvariant())
        {
            case ("gf_npc", "room") or ("mroom", "nickname"):
                return session.Bases.FileOf(baseName) is { } file && session.Lists.List(RoomList, file) is { } rooms
                    ? new BasePartList(rooms, RoomUses(session, baseName, file))
                    : null;
            case ("mroom", "fixture") or ("basefaction", "npc"):
                return new BasePartList(CharacterListOf(session, baseName), CharacterUses(session, MBaseOf(session, baseName).Parts));
            default:
                return null;
        }
    }

    /// <summary>
    /// The entries of a list of the parts of one base: <see cref="RoomList"/> gives the rooms of its base file, and
    /// <see cref="CharacterList"/> gives the characters of its <c>[MBase]</c>. Null for another list, or for the rooms of
    /// a base without a base file. <c>list_values</c> gives these entries for one base.
    /// </summary>
    internal static ListView? PartsOf(EditSession session, string baseNickname, string list)
    {
        if (string.Equals(list, CharacterList, StringComparison.Ordinal))
            return CharacterListOf(session, baseNickname.Trim());
        return string.Equals(list, RoomList, StringComparison.Ordinal) && session.Bases.FileOf(baseNickname) is { } file
            ? session.Lists.List(RoomList, file)
            : null;
    }

    /// <summary>The list of the characters of the <c>[MBase]</c> of a base. The detail of an entry is the role of its
    /// fixture, or <c>crowd</c>.</summary>
    private static ListView CharacterListOf(EditSession session, string baseName)
    {
        var (mbase, parts) = MBaseOf(session, baseName);
        var characters = CharactersOf(session, mbase, parts)
            .Select(c =>
            {
                var (origin, mod) = session.Origin(c.Section, "nickname");
                return new ListEntry(c.Nickname, c.Section, origin, mod, c.Role ?? "crowd");
            })
            .ToList();
        return new ListView(CharacterList, "Character", "Character", true, false, characters, []);
    }

    /// <summary>
    /// The counts of a base without a view of the whole base, for a list of many bases: the characters of its
    /// <c>[MBase]</c>, the goods of the market lines that the games keep, and the objects whose <c>dock_with</c> line
    /// names the base. The counts are the counts of <see cref="Characters"/>, of the kept lines of <see cref="Market"/>
    /// and of the docking objects of <see cref="DockObjects"/>.
    /// </summary>
    internal static (int Characters, int Goods, int DockObjects) CountsOf(EditSession session, string baseNickname)
    {
        var name = baseNickname.Trim();
        var (mbase, parts) = MBaseOf(session, name);
        var goods = BaseMarkets.KeptLines(session.Items.BaseMarketLines(name), l => l.Values[0].ToString().Trim(),
            l => l.Values).Count;
        return (CharactersOf(session, mbase, parts).Count, goods, DockObjectsOf(session, name).Count(d => d.DocksWith));
    }

    /// <summary>The kind of a base from the start room of its base file, without a view of the whole base. A base
    /// without a base file is of the kind <see cref="BaseKind.Other"/>.</summary>
    internal static BaseKind KindOf(EditSession session, string baseNickname)
    {
        var file = session.Bases.FileOf(baseNickname);
        var info = file == null ? null : session.Sections(file).FirstOrDefault(s => Is(s.Id.Type, "BaseInfo"));
        return KindOf(info is { } i && session.WorkingSection(i) is { } section ? Last(section, "start_room") : null);
    }

    /// <summary>
    /// The number of lines of a base that name each of its rooms: the <c>start_room</c> of <c>[BaseInfo]</c>, the
    /// <c>room</c> lines of the characters and the <c>nickname</c> of each <c>[MRoom]</c> of its <c>[MBase]</c>, and
    /// the <c>room_switch</c> of each door of its rooms.
    /// </summary>
    private static Dictionary<string, int> RoomUses(EditSession session, string baseName, string file)
    {
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in session.Sections(file).Where(s => Is(s.Id.Type, "BaseInfo")))
            CountValues(uses, session.WorkingSection(info), "start_room");
        foreach (var part in MBaseOf(session, baseName).Parts)
        {
            var section = session.WorkingSection(part);
            CountValues(uses, section, Is(section?.Name, "GF_NPC") ? "room" : Is(section?.Name, "MRoom") ? "nickname" : null);
        }
        foreach (var door in RoomsOf(session, baseName, file).SelectMany(r => r.Doors))
        {
            if (door.Target is { } target)
                uses[target] = uses.GetValueOrDefault(target) + 1;
        }
        return uses;
    }

    /// <summary>The number of lines of an <c>[MBase]</c> that name each of its characters: value 0 of each
    /// <c>fixture</c> of an <c>[MRoom]</c> and of each <c>npc</c> of a <c>[BaseFaction]</c>.</summary>
    private static Dictionary<string, int> CharacterUses(EditSession session, IReadOnlyList<SectionRef> parts)
    {
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            var section = session.WorkingSection(part);
            CountValues(uses, section, Is(section?.Name, "MRoom") ? "fixture" : Is(section?.Name, "BaseFaction") ? "npc" : null);
        }
        return uses;
    }

    /// <summary>Adds 1 to the count of value 0 of each line of a key of a section. Does nothing for a null section or
    /// key.</summary>
    private static void CountValues(Dictionary<string, int> uses, Section? section, string? key)
    {
        if (section == null || key == null)
            return;
        foreach (var line in section.Where(e => Is(e.Name, key) && e.Count > 0))
        {
            if (line[0].ToString().Trim() is { Length: > 0 } value)
                uses[value] = uses.GetValueOrDefault(value) + 1;
        }
    }

    /// <summary>The list of the rule data that holds the rooms of one base file.</summary>
    public const string RoomList = "room_of_base";

    /// <summary>The list of the characters of one base. The rule data has no such list, because the <c>[MBase]</c> of
    /// the base gives the entries, not a file.</summary>
    public const string CharacterList = "character_of_base";

    /// <summary>The lists that hold the parts of one base (see <see cref="PartsOf"/>).</summary>
    public static IReadOnlyList<string> PartLists { get; } = [RoomList, CharacterList];

    /// <summary>The base of a section of <c>mBases.ini</c>: the key of an <c>[MBase]</c>, or the key of the
    /// <c>[MBase]</c> of a child section. Null for another section.</summary>
    internal static string? BaseOf(SectionRef section) =>
        Is(section.Id.Type, "MBase") ? section.Id.Key
        : section.Id.Parent is { } parent && Is(parent.Type, "MBase") ? parent.Key
        : null;

    /// <summary>The first <c>[Base]</c> of W of <c>universe.ini</c> with the nickname, or null.</summary>
    private static SectionRef? EntryOf(EditSession session, string nickname) =>
        session.UniversePath is { } universe
            ? session.Sections(universe).FirstOrDefault(s => Is(s.Id.Type, "Base") && Is(s.Id.Key, nickname.Trim()))
            : null;

    private static BaseKind KindOf(string? startRoom) => startRoom?.ToLowerInvariant() switch
    {
        "deck" => BaseKind.Station,
        "cityscape" or "planetscape" => BaseKind.Planet,
        _ => BaseKind.Other,
    };

    /// <summary>The rooms of the base file of a base, with the parts of each room file.</summary>
    private static IReadOnlyList<BaseRoomView> RoomsOf(EditSession session, string baseName, string? file)
    {
        if (file == null)
            return [];
        var result = new List<BaseRoomView>();
        var active = session.ActiveMod?.Id;
        foreach (var room in session.Sections(file).Where(s => Is(s.Id.Type, "Room")))
        {
            if (session.WorkingSection(room) is not { } section)
                continue;
            var roomFile = Last(section, "file") is { } value ? session.DataPath(value) : null;
            var sections = roomFile == null ? [] : session.OpenDocument(roomFile)?.Working ?? [];
            var hotspots = sections.Where(s => Is(s.Name, Hotspot)).ToList();
            var shared = roomFile != null &&
                         session.Bases.BasesOfRoomFile(roomFile).Any(b => !Is(b, baseName));
            var own = roomFile != null && active != null && ContentPaths.IsOwnFile(roomFile, active) &&
                      session.HasFile(roomFile);
            result.Add(new BaseRoomView(room, Last(section, "nickname") ?? "", roomFile, shared, own,
                hotspots.Select(h => DealerOf(Last(h, "behavior"))).OfType<BaseDealer>().Distinct().ToList(),
                hotspots.Where(h => Is(Last(h, "behavior"), "VirtualRoom")).Select(h => Last(h, "room_switch"))
                    .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                sections.Count(s => Is(s.Name, "ForSaleShipPlacement")),
                hotspots.Where(h => Is(Last(h, "behavior"), "ExitDoor"))
                    .Select(h => new BaseDoor(Last(h, "name") ?? "", Last(h, "room_switch"), Last(h, "set_virtual_room")))
                    .ToList()));
        }
        return result;
    }

    private static BaseDealer? DealerOf(string? behavior) => behavior?.ToLowerInvariant() switch
    {
        "startdealer" => BaseDealer.CommodityTrader,
        "startequipdealer" => BaseDealer.EquipmentDealer,
        "startshipdealer" => BaseDealer.ShipDealer,
        _ => null,
    };

    /// <summary>The last <c>[MBase]</c> of W of <c>mBases.ini</c> with the nickname, and the child sections that follow
    /// it. The fork keeps the last block of a nickname (GameItemDb.cs:343-346).</summary>
    private static (SectionRef? MBase, IReadOnlyList<SectionRef> Parts) MBaseOf(EditSession session, string baseName)
    {
        if (session.Bases.MBasesPath is not { } path)
            return (null, []);
        var sections = session.Sections(path);
        var at = -1;
        for (var i = 0; i < sections.Count; i++)
        {
            if (Is(sections[i].Id.Type, "MBase") && Is(sections[i].Id.Key, baseName))
                at = i;
        }
        if (at < 0)
            return (null, []);
        var parts = sections.Skip(at + 1).TakeWhile(s => SectionKeys.IsChild(s.Id.Type)).ToList();
        return (sections[at], parts);
    }

    /// <summary>The characters of an <c>[MBase]</c>, with the role and the room of their fixtures and the faction of
    /// their <c>[BaseFaction]</c>.</summary>
    private static IReadOnlyList<BaseCharacter> CharactersOf(EditSession session, SectionRef? mbase,
        IReadOnlyList<SectionRef> parts)
    {
        if (mbase == null)
            return [];
        var fixtures = new Dictionary<string, (string? Role, string? Room)>(StringComparer.OrdinalIgnoreCase);
        var factions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var npcs = new List<(SectionRef Ref, Section Section)>();
        foreach (var part in parts)
        {
            if (session.WorkingSection(part) is not { } section)
                continue;
            if (Is(section.Name, "GF_NPC"))
                npcs.Add((part, section));
            else if (Is(section.Name, "MRoom"))
            {
                foreach (var fixture in section.Where(e => Is(e.Name, "fixture") && e.Count > 0))
                {
                    fixtures.TryAdd(fixture[0].ToString().Trim(),
                        (fixture.Count > 3 ? fixture[3].ToString().Trim() : null, Last(section, "nickname")));
                }
            }
            else if (Is(section.Name, "BaseFaction") && Last(section, "faction") is { } faction)
            {
                foreach (var npc in section.Where(e => Is(e.Name, "npc") && e.Count > 0))
                    factions.TryAdd(npc[0].ToString().Trim(), faction);
            }
        }
        return npcs.Select(n =>
        {
            var nickname = n.Ref.Id.Key ?? Last(n.Section, "nickname") ?? "";
            var fixture = fixtures.GetValueOrDefault(nickname);
            return new BaseCharacter(n.Ref, nickname, fixture.Role, Last(n.Section, "room") ?? fixture.Room,
                factions.GetValueOrDefault(nickname), Last(n.Section, "affiliation"), Last(n.Section, "voice"));
        }).ToList();
    }

    /// <summary>The objects of the system files whose <c>dock_with</c> or <c>base</c> line names the base.</summary>
    private static IReadOnlyList<BaseDockObject> DockObjectsOf(EditSession session, string baseName) =>
        session.Lists.ReferencesTo("base", baseName)
            .Where(r => Is(r.Section.Id.Type, "Object") && session.IsSystemFile(r.Section.Path) &&
                        (Is(r.Key, "dock_with") || Is(r.Key, "base")))
            .GroupBy(r => r.Section)
            .Select(g => new BaseDockObject(g.Key, g.Any(r => Is(r.Key, "dock_with")), g.Any(r => Is(r.Key, "base"))))
            .ToList();

    /// <summary>The object with a <c>base</c> line of the base whose <c>ids_name</c> is the <c>strid_name</c> of the
    /// base, as text. A text placeholder compares as its text, so a base and its object of one mod share it. An object
    /// in the file of the system of the base comes first.</summary>
    private static SectionRef? NamedObjectOf(EditSession session, IReadOnlyList<BaseDockObject> docks, string? name,
        string? system) =>
        name == null
            ? null
            : docks.Where(d => d.HasBaseLine && session.WorkingSection(d.Section) is { } section &&
                               Is(Last(section, "ids_name"), name))
                .OrderByDescending(d => InSystem(session, d.Section, system))
                .FirstOrDefault()?.Section;

    /// <summary>True when a section is in the file of a system.</summary>
    internal static bool InSystem(EditSession session, SectionRef section, string? system) =>
        system != null && session.SystemPath(system) is { } path && VfsPath.Comparer.Equals(section.Path, path);

    /// <summary>The first value of the last line of a key with a value, trimmed, or null.</summary>
    private static string? Last(Section section, string key) =>
        section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0)?[0].ToString().Trim() is { Length: > 0 } value
            ? value
            : null;

    private static bool Is(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
