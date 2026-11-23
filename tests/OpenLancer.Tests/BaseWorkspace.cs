using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;

namespace OpenLancer.Tests;

/// <summary>
/// A small install with three bases for the base tests (section H.1 of the bases design). The data is made up for the
/// tests. <c>universe.ini</c> has the bases before the systems. Ol01_01_Base is a station with a deck that has two
/// dealers as virtual rooms. Ol01_02_Base is a planet with terrain lines, a cityscape whose scene names a
/// <c>$terrain_*</c> template, and a ship dealer room. It has no <c>[MBase]</c>. Ol02_01_Base is a station whose
/// bartender has a <c>base_appr</c> costume. The two stations share one bar file. The three market files, the goods,
/// the body parts, the costumes, the voices and the factions give the lists of the rules.
/// </summary>
internal static class BaseWorkspace
{
    public const string UniversePath = @"DATA\UNIVERSE\universe.ini";
    public const string Ol01Path = @"DATA\UNIVERSE\SYSTEMS\OL01\ol01.ini";
    public const string Ol02Path = @"DATA\UNIVERSE\SYSTEMS\OL02\ol02.ini";
    public const string StationPath = @"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ol01_01_base.ini";
    public const string PlanetPath = @"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ol01_02_base.ini";
    public const string PiratePath = @"DATA\UNIVERSE\SYSTEMS\OL02\BASES\ol02_01_base.ini";
    public const string BarPath = @"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ROOMS\ol_shared_bar.ini";
    public const string StationDeckPath = @"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ROOMS\ol01_01_deck.ini";
    public const string CityscapePath = @"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ROOMS\ol01_02_cityscape.ini";
    public const string ShipDealerPath = @"DATA\UNIVERSE\SYSTEMS\OL01\BASES\ROOMS\ol01_02_shipdealer.ini";
    public const string PirateDeckPath = @"DATA\UNIVERSE\SYSTEMS\OL02\BASES\ROOMS\ol02_01_deck.ini";
    public const string MBasesPath = @"DATA\MISSIONS\mBases.ini";
    public const string NewsPath = @"DATA\MISSIONS\news.ini";
    public const string MiscMarketPath = @"DATA\EQUIPMENT\market_misc.ini";
    public const string ShipMarketPath = @"DATA\EQUIPMENT\market_ships.ini";
    public const string CommodityMarketPath = @"DATA\EQUIPMENT\market_commodities.ini";

    public const string Station = "Ol01_01_Base";
    public const string Planet = "Ol01_02_Base";
    public const string Pirate = "Ol02_01_Base";

    /// <summary>The script of the cityscape. Its scene shows the small terrain of the base.</summary>
    public const string CityScenePath = @"DATA\SCRIPTS\BASES\ol_city_ambi.thn";

    /// <summary>The cutscene scripts of the rooms, as install paths, without the script of the cityscape.</summary>
    public static readonly string[] Scripts =
    [
        @"DATA\SCRIPTS\BASES\ol_bar_set.thn", @"DATA\SCRIPTS\BASES\ol_deck_set.thn",
        @"DATA\SCRIPTS\BASES\ol_city_set.thn", @"DATA\SCRIPTS\BASES\ol_shipdealer_set.thn",
    ];

    public const string FreelancerIni = $"""
        [Freelancer]
        data path = ..\data

        {TestWorkspace.ResourcesSection}

        [Data]
        solar = solar\solararch.ini
        universe = universe\universe.ini
        equipment = equipment\weapon_equip.ini
        ships = ships\shiparch.ini
        goods = equipment\goods.ini
        markets = equipment\market_misc.ini
        markets = equipment\market_ships.ini
        markets = equipment\market_commodities.ini
        bodyparts = characters\bodyparts.ini
        costumes = characters\costumes.ini
        voices = audio\voices.ini
        """;

    public const string Universe = """
        [Base]
        nickname = Ol01_01_Base
        system = Ol01
        strid_name = 1001
        file = Universe\Systems\Ol01\Bases\ol01_01_base.ini
        BGCS_base_run_by = ol_run_by_a
        [Base]
        nickname = Ol01_02_Base
        system = Ol01
        strid_name = 1002
        file = Universe\Systems\Ol01\Bases\ol01_02_base.ini
        BGCS_base_run_by = ol_run_by_b
        terrain_tiny = ol_rock_tiny
        terrain_sml = ol_rock_sml
        [Base]
        nickname = Ol02_01_Base
        system = Ol02
        strid_name = 1003
        file = Universe\Systems\Ol02\Bases\ol02_01_base.ini
        [system]
        nickname = Ol01
        file = systems\Ol01\ol01.ini
        [system]
        nickname = Ol02
        file = systems\Ol02\ol02.ini
        """;

    public const string MBases = """
        [MBase]
        nickname = Ol01_01_Base
        local_faction = ol_grp
        diff = 3
        [MVendor]
        num_offers = 2, 4
        [BaseFaction]
        faction = ol_grp
        weight = 100
        npc = ol01_01_npc_1
        [GF_NPC]
        nickname = ol01_01_bartender
        body = ol_body
        head = ol_head
        lefthand = ol_left
        righthand = ol_right
        individual_name = 1100
        affiliation = ol_grp
        voice = ol_voice
        [GF_NPC]
        nickname = ol01_01_trader
        body = ol_body
        head = ol_head
        lefthand = ol_left
        righthand = ol_right
        individual_name = 1101
        affiliation = ol_grp
        voice = ol_voice
        [GF_NPC]
        nickname = ol01_01_npc_1
        body = ol_body
        head = ol_head
        lefthand = ol_left
        righthand = ol_right
        individual_name = 1102
        affiliation = ol_grp
        voice = ol_voice
        room = Bar
        [MRoom]
        nickname = Bar
        character_density = 3
        fixture = ol01_01_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_bartender_fidget.thn, bartender
        [MRoom]
        nickname = Deck
        fixture = ol01_01_trader, Zs/NPC/Trader/01/A/Stand, scripts\vendors\ol_trader_fidget.thn, trader
        [MBase]
        nickname = Ol02_01_Base
        local_faction = ol_pirates
        diff = 5
        [MVendor]
        num_offers = 1, 2
        [GF_NPC]
        nickname = ol02_01_bartender
        base_appr = ol_costume
        individual_name = 1200
        affiliation = ol_pirates
        voice = ol_voice
        [MRoom]
        nickname = Bar
        fixture = ol02_01_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_bartender_fidget.thn, bartender
        """;

    /// <summary>A workspace with the files in the base folder, the empty mods "lower" and "act", and the profile.</summary>
    public static TestWorkspace Create(Dictionary<string, string>? activeFiles = null,
        Dictionary<string, string>? lowerFiles = null)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni);
        ws.WriteBase(UniversePath, Universe);
        ws.WriteBase(@"DATA\SOLAR\solararch.ini", """
            [Solar]
            nickname = ol_station
            type = STATION
            docking_sphere = berth, HpDockMountA, 50
            [Solar]
            nickname = ol_planet
            type = PLANET
            solar_radius = 2000
            [Solar]
            nickname = ol_ring
            type = DOCKING_RING
            docking_sphere = ring, HpDockMountA, 30
            """);
        ws.WriteBase(Ol01Path, """
            [Object]
            nickname = Ol01_01
            ids_name = 1001
            pos = 0, 0, 0
            archetype = ol_station
            base = Ol01_01_Base
            dock_with = Ol01_01_Base
            reputation = ol_grp
            [Object]
            nickname = Ol01_02
            ids_name = 1002
            pos = 10000, 0, 0
            archetype = ol_planet
            base = Ol01_02_Base
            [Object]
            nickname = Dock_Ring_Ol01_02
            ids_name = 1099
            pos = 10000, 0, 2500
            archetype = ol_ring
            dock_with = Ol01_02_Base
            """);
        ws.WriteBase(Ol02Path, """
            [Object]
            nickname = Ol02_01
            ids_name = 1003
            pos = 0, 0, 0
            archetype = ol_station
            base = Ol02_01_Base
            dock_with = Ol02_01_Base
            """);
        ws.WriteBase(StationPath, """
            [BaseInfo]
            nickname = Ol01_01_Base
            start_room = Deck
            [Room]
            nickname = Deck
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_01_deck.ini
            [Room]
            nickname = Bar
            file = Universe\Systems\Ol01\Bases\Rooms\ol_shared_bar.ini
            """);
        ws.WriteBase(PlanetPath, """
            [BaseInfo]
            nickname = Ol01_02_Base
            start_room = Cityscape
            [Room]
            nickname = Cityscape
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_02_cityscape.ini
            [Room]
            nickname = ShipDealer
            file = Universe\Systems\Ol01\Bases\Rooms\ol01_02_shipdealer.ini
            """);
        ws.WriteBase(PiratePath, """
            [BaseInfo]
            nickname = Ol02_01_Base
            start_room = Deck
            [Room]
            nickname = Deck
            file = Universe\Systems\Ol02\Bases\Rooms\ol02_01_deck.ini
            [Room]
            nickname = Bar
            file = Universe\Systems\Ol01\Bases\Rooms\ol_shared_bar.ini
            """);
        ws.WriteBase(BarPath, """
            [Room_Info]
            set_script = Scripts\Bases\ol_bar_set.thn
            [Hotspot]
            name = IDS_HOTSPOT_DECK
            behavior = ExitDoor
            room_switch = Deck
            """);
        ws.WriteBase(StationDeckPath, """
            [Room_Info]
            set_script = Scripts\Bases\ol_deck_set.thn
            [PlayerShipPlacement]
            name = X/Shipcentre/01
            [Hotspot]
            name = IDS_HOTSPOT_BAR
            behavior = ExitDoor
            room_switch = Bar
            [Hotspot]
            name = IDS_HOTSPOT_COMMODITYTRADER_ROOM
            behavior = VirtualRoom
            room_switch = Trader
            [Hotspot]
            name = IDS_HOTSPOT_EQUIPMENTDEALER_ROOM
            behavior = VirtualRoom
            room_switch = Equipment
            [Hotspot]
            name = IDS_HOTSPOT_COMMODITYTRADER
            behavior = StartDealer
            virtual_room = Trader
            [Hotspot]
            name = IDS_HOTSPOT_EQUIPMENTDEALER
            behavior = StartEquipDealer
            virtual_room = Equipment
            """);
        ws.WriteBase(CityscapePath, """
            [Room_Info]
            set_script = Scripts\Bases\ol_city_set.thn
            scene = all, ambient, Scripts\Bases\ol_city_ambi.thn
            [PlayerShipPlacement]
            name = X/Shipcentre/01
            [Hotspot]
            name = IDS_HOTSPOT_SHIPDEALER_ROOM
            behavior = ExitDoor
            room_switch = ShipDealer
            """);
        ws.WriteBase(ShipDealerPath, """
            [Room_Info]
            set_script = Scripts\Bases\ol_shipdealer_set.thn
            [ForSaleShipPlacement]
            name = X/Shipcentre/01
            [ForSaleShipPlacement]
            name = X/Shipcentre/02
            [Hotspot]
            name = IDS_HOTSPOT_PLANETSCAPE
            behavior = ExitDoor
            room_switch = Cityscape
            set_virtual_room = Cityscape
            [Hotspot]
            name = IDS_HOTSPOT_SHIPDEALER
            behavior = StartShipDealer
            """);
        ws.WriteBase(PirateDeckPath, """
            [Room_Info]
            set_script = Scripts\Bases\ol_deck_set.thn
            [Hotspot]
            name = IDS_HOTSPOT_BAR
            behavior = ExitDoor
            room_switch = Bar
            [Hotspot]
            name = IDS_HOTSPOT_EQUIPMENTDEALER
            behavior = StartEquipDealer
            """);
        foreach (var script in Scripts)
            ws.WriteBase(script, "duration = 1\nentities = {}\nevents = {}\n");
        ws.WriteBase(CityScenePath, """
            duration = 10
            entities = {
                { entity_name = "Rock", type = COMPOUND, template_name = "$terrain_tiny", userprops = { category = "solar" } }
            }
            events = {}
            """);
        ws.WriteBase(MBasesPath, MBases);
        ws.WriteBase(@"DATA\initialworld.ini", """
            [Group]
            nickname = ol_grp
            [Group]
            nickname = ol_pirates
            """);
        ws.WriteBase(@"DATA\CHARACTERS\bodyparts.ini", """
            [Body]
            nickname = ol_body
            [Head]
            nickname = ol_head
            [LeftHand]
            nickname = ol_left
            [RightHand]
            nickname = ol_right
            [Accessory]
            nickname = ol_hat
            """);
        ws.WriteBase(@"DATA\CHARACTERS\costumes.ini", """
            [Costume]
            nickname = ol_costume
            body = ol_body
            head = ol_head
            """);
        ws.WriteBase(@"DATA\AUDIO\voices.ini", """
            [Voice]
            nickname = ol_voice
            """);
        ws.WriteBase(@"DATA\EQUIPMENT\weapon_equip.ini", """
            [Munition]
            nickname = ol_ammo
            [Gun]
            nickname = ol_gun
            projectile_archetype = ol_ammo
            """);
        ws.WriteBase(@"DATA\SHIPS\shiparch.ini", """
            [Ship]
            nickname = ol_ship
            """);
        ws.WriteBase(@"DATA\EQUIPMENT\goods.ini", """
            [Good]
            nickname = ol_water
            category = commodity
            [Good]
            nickname = ol_gun
            equipment = ol_gun
            category = equipment
            [Good]
            nickname = ol_ship_hull
            category = shiphull
            ship = ol_ship
            [Good]
            nickname = ol_ship_package
            category = ship
            hull = ol_ship_hull
            """);
        ws.WriteBase(MiscMarketPath, """
            [BaseGood]
            base = Ol01_01_Base
            marketgood = ol_gun, 0, -1, 10, 10, 0, 1
            [BaseGood]
            base = Ol02_01_Base
            marketgood = ol_gun, 0, -1, 5, 5, 0, 1.5
            """);
        ws.WriteBase(ShipMarketPath, """
            [BaseGood]
            base = Ol01_02_Base
            marketgood = ol_ship_package, 0, -1, 1, 1, 0, 1, 1
            """);
        ws.WriteBase(CommodityMarketPath, """
            [BaseGood]
            base = Ol01_01_Base
            marketgood = ol_water, 0, -1, 150, 500, 0, 1
            """);
        ws.Mod("lower", lowerFiles ?? new());
        ws.Mod("act", activeFiles ?? new());
        ws.WriteProfile("lower", "act");
        return ws;
    }

    /// <summary>The section of a type and a key in a file of W.</summary>
    public static SectionRef Section(EditSession session, string path, string type, string? key = null) =>
        session.Sections(path).Single(s => s.Id.Type == type && (key == null || s.Id.Key == key));

    /// <summary>A child section of the <c>[MBase]</c> of a base in W of mBases.ini.</summary>
    public static SectionRef MBasePart(EditSession session, string baseNickname, string type, string? key = null) =>
        session.Sections(MBasesPath).Single(s => s.Id.Type == type && s.Id.Parent?.Key == baseNickname &&
                                                 (key == null || s.Id.Key == key));
}
