using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The read model of one base (section C.1 of the bases design): its entry, its base file and rooms, its bar data, its
/// market lines over all market files, and the objects that dock with it. The view reads W, so an unsaved edit
/// counts.
/// </summary>
public sealed class BaseViewTests
{
    [Fact]
    public void AStationGivesItsRoomsCharactersMarketAndDockObject()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var view = session.Base("ol01_01_base")!;

        Assert.Equal(BaseWorkspace.Station, view.Nickname);
        Assert.Equal(BaseWorkspace.Section(session, BaseWorkspace.UniversePath, "Base", BaseWorkspace.Station), view.Entry);
        Assert.Equal(("Ol01", "1001", "Deck", BaseKind.Station), (view.System, view.Name, view.StartRoom, view.Kind));
        Assert.Equal(BaseWorkspace.StationPath, view.File, System.StringComparer.OrdinalIgnoreCase);
        Assert.Equal(BaseWorkspace.Section(session, BaseWorkspace.StationPath, "BaseInfo"), view.Info);
        Assert.Equal((ValueOrigin.Base, (string?)null), (view.Origin, view.OriginMod));
        Assert.Empty(view.Terrain);

        Assert.Equal(["Deck", "Bar"], view.Rooms.Select(r => r.Nickname));
        var deck = view.Rooms[0];
        Assert.Equal((false, false, 0), (deck.Shared, deck.Own, deck.ShipPlaces));
        Assert.Equal([BaseDealer.CommodityTrader, BaseDealer.EquipmentDealer], deck.Dealers);
        Assert.Equal(["Trader", "Equipment"], deck.VirtualRooms);
        Assert.Equal([new BaseDoor("IDS_HOTSPOT_BAR", "Bar", null)], deck.Doors);
        Assert.True(view.Rooms[1].Shared);

        Assert.Equal(BaseWorkspace.Section(session, BaseWorkspace.MBasesPath, "MBase", BaseWorkspace.Station), view.MBase);
        Assert.Equal(["MVendor", "BaseFaction", "GF_NPC", "GF_NPC", "GF_NPC", "MRoom", "MRoom"],
            view.MBaseParts.Select(p => p.Id.Type));
        Assert.Equal([
                ("ol01_01_bartender", "bartender", "Bar", null, "ol_grp"),
                ("ol01_01_trader", "trader", "Deck", null, "ol_grp"),
                ("ol01_01_npc_1", null, "Bar", "ol_grp", "ol_grp"),
            ],
            view.Characters.Select(c => (c.Nickname, c.Role, c.Room, c.Faction, c.Affiliation)));
        Assert.All(view.Characters, c => Assert.Equal("ol_voice", c.Voice));

        Assert.Equal(["ol_gun", "ol_water"], view.Market.Select(m => m.Good));
        Assert.Equal([BaseWorkspace.MiscMarketPath, BaseWorkspace.CommodityMarketPath],
            view.Market.Select(m => m.Line.Section.Path), System.StringComparer.OrdinalIgnoreCase);
        Assert.All(view.Market, m => Assert.Equal((ValueOrigin.Base, false), (m.Origin, m.CanChange)));

        var dock = Assert.Single(view.DockObjects);
        Assert.Equal(("Ol01_01", true, true), (dock.Section.Id.Key, dock.DocksWith, dock.HasBaseLine));
        Assert.Equal(dock.Section, view.NamedObject);
    }

    /// <summary>A planet base: the terrain lines, a dealer room with ship places and a door that sets a virtual room, a
    /// docking ring without the base line, and no [MBase]. The planet has the base line and the name of the
    /// base.</summary>
    [Fact]
    public void APlanetGivesItsTerrainShipDealerAndDockingRing()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var view = session.Base(BaseWorkspace.Planet)!;

        Assert.Equal(("Cityscape", BaseKind.Planet), (view.StartRoom, view.Kind));
        Assert.Equal([new BaseTerrainLine("terrain_tiny", "ol_rock_tiny"), new BaseTerrainLine("terrain_sml", "ol_rock_sml")],
            view.Terrain);
        var dealer = view.Rooms.Single(r => r.Nickname == "ShipDealer");
        Assert.Equal([BaseDealer.ShipDealer], dealer.Dealers);
        Assert.Equal(2, dealer.ShipPlaces);
        Assert.Equal([new BaseDoor("IDS_HOTSPOT_PLANETSCAPE", "Cityscape", "Cityscape")], dealer.Doors);
        Assert.Null(view.MBase);
        Assert.Empty(view.Characters);
        Assert.Equal(["ol_ship_package"], view.Market.Select(m => m.Good));
        Assert.Equal([("Ol01_02", false, true), ("Dock_Ring_Ol01_02", true, false)],
            view.DockObjects.Select(d => (d.Section.Id.Key, d.DocksWith, d.HasBaseLine)));
        Assert.Equal("Ol01_02", view.NamedObject?.Id.Key);
    }

    [Fact]
    public void ABaseWithACostumeCharacterAndAnUnknownBase()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        var view = session.Base(BaseWorkspace.Pirate)!;

        var bartender = Assert.Single(view.Characters);
        Assert.Equal(("ol02_01_bartender", "bartender", "Bar", "ol_pirates"),
            (bartender.Nickname, bartender.Role, bartender.Room, bartender.Affiliation));
        Assert.Equal([("Deck", false), ("Bar", true)], view.Rooms.Select(r => (r.Nickname, r.Shared)));
        Assert.Null(session.Base("no_base"));
    }

    /// <summary>
    /// Two <c>[MBase]</c> blocks with one nickname: the fork keeps the last block, so the view and the part pickers read
    /// the last block. Only a whole copy of mBases.ini can have two blocks, because a patch finds a block by its
    /// nickname.
    /// </summary>
    [Fact]
    public void TheLastMBaseBlockOfANicknameCounts()
    {
        var copy = BaseWorkspace.MBases + """

            [MBase]
            nickname = Ol01_01_Base
            local_faction = ol_pirates
            [GF_NPC]
            nickname = dup_npc
            affiliation = ol_pirates
            voice = ol_voice
            [MRoom]
            nickname = Bar
            fixture = dup_npc, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\ol_bartender_fidget.thn, bartender
            """;
        using var ws = BaseWorkspace.Create(activeFiles: new() { ["files\\" + BaseWorkspace.MBasesPath] = copy });
        var session = ws.Session("act");
        var blocks = session.Sections(BaseWorkspace.MBasesPath)
            .Where(s => s.Id.Type == "MBase" && s.Id.Key == BaseWorkspace.Station).ToList();
        Assert.Equal(2, blocks.Count);

        var view = session.Base(BaseWorkspace.Station)!;

        Assert.Equal(blocks[1], view.MBase);
        Assert.Equal(["GF_NPC", "MRoom"], view.MBaseParts.Select(p => p.Id.Type));
        var character = Assert.Single(view.Characters);
        Assert.Equal(("dup_npc", "bartender", "Bar", "ol_pirates"),
            (character.Nickname, character.Role, character.Room, character.Affiliation));
        var room = view.MBaseParts[1];
        var parts = FieldBinding.Create(session, room, session.Get(room)!, "fixture").Parts!;
        Assert.Equal([("dup_npc", 1)], parts.List.Entries.Select(e => (e.Value, parts.UsedBy(e))));
    }

    /// <summary>
    /// The view reads W: a new market line of the active mod can change, a new start room shows at once, a new room
    /// file of the active mod is its own, and a new base of the active mod has the active mod as its origin. The market
    /// rule of the view is the rule of the Inspector.
    /// </summary>
    [Fact]
    public void AnUnsavedEditShowsInTheView()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var info = BaseWorkspace.Section(session, BaseWorkspace.StationPath, "BaseInfo");
        var bar = BaseWorkspace.Section(session, BaseWorkspace.StationPath, "Room", "Bar");
        var copy = ContentPaths.NewRoomFile("act", BaseWorkspace.Station, "Bar");

        session.Execute(MarketEdits.AddToBase(session, "ol_ship_package", BaseWorkspace.Station, "0, -1, 1, 1, 0, 1, 1"));
        session.Execute(EditCommand.SetLinesFromText(info, "start_room", ["Bar"]));
        session.Execute(EditCommand.NewFile(copy, ContentPathsTests.Sections("[Room_Info]\nset_script = Scripts\\Bases\\ol_bar_set.thn\n")));
        session.Execute(EditCommand.SetLinesFromText(bar, "file", [ContentPaths.DataValue(copy)!]));
        session.Execute(EditCommand.AddSection(BaseWorkspace.UniversePath, "Base",
        [
            new EntryView("nickname", [new LibreLancer.Data.Ini.StringValue("Ol01_09_Base")]),
            new EntryView("system", [new LibreLancer.Data.Ini.StringValue("Ol01")]),
        ]));
        var view = session.Base(BaseWorkspace.Station)!;

        var row = view.Market.Single(m => m.Good == "ol_ship_package");
        Assert.Equal((ValueOrigin.ActiveMod, "act", true), (row.Origin, row.ModId, row.CanChange));
        Assert.Equal(row.CanChange, MarketEdits.CanChange(session, row.Line));
        Assert.Equal(("Bar", BaseKind.Other), (view.StartRoom, view.Kind));
        var room = view.Rooms.Single(r => r.Nickname == "Bar");
        Assert.Equal((true, false), (room.Own, room.Shared));
        var added = session.Base("Ol01_09_Base")!;
        Assert.Equal((ValueOrigin.ActiveMod, "act", (string?)null), (added.Origin, added.OriginMod, added.File));
        Assert.Empty(added.Rooms);
    }
}
