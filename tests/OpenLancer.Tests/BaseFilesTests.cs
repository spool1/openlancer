using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A small install with two bases, three room files and the cutscene scripts that the rooms name (section 5.5 of the
/// M8b design). The second base shares the bar file of the first base.
/// </summary>
internal static class RoomWorkspace
{
    public const string UniversePath = @"DATA\UNIVERSE\universe.ini";
    public const string Base1Path = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\li01_01_base.ini";
    public const string Base2Path = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\li01_02_base.ini";
    public const string BarPath = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\ROOMS\li01_01_bar.ini";
    public const string DeckPath = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\ROOMS\li01_01_deck.ini";
    public const string ShopPath = @"DATA\UNIVERSE\SYSTEMS\LI01\BASES\ROOMS\li01_02_shop.ini";
    public const string BarPatch = @"patches\" + BarPath;

    /// <summary>The cutscene scripts of the rooms, as install paths.</summary>
    public static readonly string[] Scripts =
    [
        @"DATA\SCRIPTS\BASES\Li_01_bar_hardpoint_01.thn", @"DATA\SCRIPTS\BASES\Li_01_bar_ambi_int_01.thn",
        @"DATA\SCRIPTS\BASES\Li_01_bar_ambi_int_s.thn", @"DATA\SCRIPTS\BASES\Li_01_bar_enter_01.thn",
        @"DATA\SCRIPTS\BASES\Li_01_deck_hardpoint_01.thn", @"DATA\SCRIPTS\BASES\Li_01_deck_land_01.thn",
        @"DATA\SCRIPTS\BASES\Li_01_deck_launch_01.thn",
    ];

    public const string Bar = """
        [Room_Info]
        set_script = Scripts\Bases\Li_01_bar_hardpoint_01.thn
        scene = all, ambient, Scripts\Bases\Li_01_bar_ambi_int_01.thn
        scene = ambient, scripts/bases/LI_01_BAR_AMBI_int_s.THN, TRAFFIC_PRIORITY
        [Camera]
        name = Camera_0
        [CharacterPlacement]
        name = Zone_Bar_Character_Start
        start_script = Scripts\Bases\Li_01_bar_enter_01.thn
        """;

    /// <summary>A workspace with the files in the base folder, the empty mods "lower" and "act", and the profile.</summary>
    public static TestWorkspace Create(Dictionary<string, string>? activeFiles = null,
        Dictionary<string, string>? lowerFiles = null)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", $"""
            [Freelancer]
            data path = ..\data

            {TestWorkspace.ResourcesSection}

            [Data]
            universe = universe\universe.ini
            """);
        ws.WriteBase(UniversePath, """
            [Base]
            nickname = Li01_01_Base
            system = Li01
            file = Universe\Systems\Li01\Bases\li01_01_base.ini
            [Base]
            nickname = Li01_02_Base
            system = Li01
            file = universe/systems/LI01/bases/li01_02_base.ini
            """);
        ws.WriteBase(Base1Path, """
            [BaseInfo]
            nickname = Li01_01_Base
            [Room]
            nickname = Bar
            file = Universe\Systems\Li01\Bases\Rooms\li01_01_bar.ini
            [Room]
            nickname = Deck
            file = Universe\Systems\Li01\Bases\Rooms\li01_01_deck.ini
            """);
        ws.WriteBase(Base2Path, """
            [BaseInfo]
            nickname = Li01_02_Base
            [Room]
            nickname = Bar
            file = Universe\Systems\Li01\Bases\Rooms\li01_01_bar.ini
            [Room]
            nickname = Shop
            file = Universe\Systems\Li01\Bases\Rooms\li01_02_shop.ini
            """);
        ws.WriteBase(BarPath, Bar);
        ws.WriteBase(DeckPath, """
            [Room_Info]
            set_script = Scripts\Bases\Li_01_deck_hardpoint_01.thn
            [PlayerShipPlacement]
            name = X
            landing_script = Scripts\Bases\Li_01_deck_land_01.thn
            launching_script = Scripts\Bases\Li_01_deck_launch_01.thn
            """);
        ws.WriteBase(ShopPath, """
            [Room_Info]
            [CharacterPlacement]
            name = Zone_Shop
            """);
        foreach (var script in Scripts)
            ws.WriteBase(script, "duration = 1\nentities = {}\nevents = {}\n");
        ws.Mod("lower", lowerFiles ?? new());
        ws.Mod("act", activeFiles ?? new());
        ws.WriteProfile("lower", "act");
        return ws;
    }

    /// <summary>The section of a type of a room file.</summary>
    public static SectionRef Section(EditSession session, string path, string type) =>
        session.Sections(path).Single(s => s.Id.Type == type);
}

/// <summary>
/// The base files and the room files (decisions D66 and D135, test 5 of section 8.1 of the M8b design, and section H.1 of
/// the bases design): the roles from the bases of universe.ini, the base of each file and the rooms of each base, the
/// schemas of the base files, the room files and mBases.ini, and the patch of a singleton room section.
/// </summary>
public sealed class BaseFilesTests
{
    [Fact]
    public void TheRoomFilesComeFromTheBasesOfUniverseIniWithoutRepeats()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal([RoomWorkspace.BarPath, RoomWorkspace.DeckPath, RoomWorkspace.ShopPath],
            session.DataFiles.Paths("room"), StringComparer.OrdinalIgnoreCase);
        Assert.True(session.IsRoomFile(RoomWorkspace.BarPath.ToLowerInvariant().Replace('\\', '/')));
        Assert.False(session.IsRoomFile(RoomWorkspace.Base1Path));
        Assert.False(session.IsRoomFile(RoomWorkspace.UniversePath));
        Assert.Contains("room", session.DataFiles.RolesOf(RoomWorkspace.DeckPath));
        Assert.Equal(["base_files"], session.DataFiles.RolesOf(RoomWorkspace.Base1Path));
        // The first use reads the base files from the edit view, so it opens no document of them.
        Assert.Null(session.OpenedDocument(RoomWorkspace.Base1Path));
        Assert.Null(session.OpenedDocument(RoomWorkspace.Base2Path));
    }

    /// <summary>The map reads W of a base file that the session has open, so an edit of a [Room] shows at once.</summary>
    [Fact]
    public void AnEditOfABaseFileChangesTheRoomFiles()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");
        Assert.True(session.IsRoomFile(RoomWorkspace.ShopPath));

        var shop = session.Sections(RoomWorkspace.Base2Path).Single(s => s.Id.Key == "Shop");
        session.Execute(EditCommand.DeleteSection(shop));

        Assert.False(session.IsRoomFile(RoomWorkspace.ShopPath));
        Assert.True(session.IsRoomFile(RoomWorkspace.BarPath));
        session.Undo();
        Assert.True(session.IsRoomFile(RoomWorkspace.ShopPath));
    }

    /// <summary>The editor knows the schema of a room file (decision D135), so the Inspector edits the lines of
    /// [CharacterPlacement]. A section without a key stays read-only.</summary>
    [Fact]
    public void TheSchemaOfARoomFileMakesItsSingletonSectionsEditable()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");
        var placement = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "CharacterPlacement");
        var camera = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "Camera");

        Assert.True(session.KnowsSchema(RoomWorkspace.BarPath));
        Assert.True(session.KnowsSchema(RoomWorkspace.Base1Path));
        Assert.False(session.KnowsSchema(RoomWorkspace.UniversePath.Replace("universe.ini", "other.ini")));
        var binding = FieldBinding.Create(session, placement, session.Get(placement)!, "start_script");
        Assert.Null(binding.ReadOnlyReason);
        Assert.NotNull(binding.Key);
        var info = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "Room_Info");
        Assert.True(session.Schema(info, "scene")!.Multiline);
        Assert.NotNull(FieldBinding.SectionLock(session, camera, session.Get(camera)!) ??
                       session.Get(camera)!.ReadOnlyReason);
    }

    /// <summary>Decision D66: [CharacterPlacement] is a singleton, so a patch section of the type finds its section by
    /// its type. The save writes one section with the new line, and the merge puts it into the room file.</summary>
    [Fact]
    public void APatchOfCharacterPlacementMergesIntoTheRoomFile()
    {
        using var ws = RoomWorkspace.Create();
        var session = ws.Session("act");
        var placement = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "CharacterPlacement");

        session.Execute(EditCommand.SetLinesFromText(placement, "start_script", [@"Scripts\Bases\Li_01_deck_land_01.thn"]));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        var patch = File.ReadAllText(Path.Combine(ws.ModFolder("act"), RoomWorkspace.BarPatch));
        Assert.Equal(1, patch.Split('[').Length - 1);
        Assert.Contains("[CharacterPlacement]", patch);
        Assert.Contains(@"start_script = Scripts\Bases\Li_01_deck_land_01.thn", patch);
        var merged = TestWorkspace.Sections(session.EditView, RoomWorkspace.BarPath);
        var section = Assert.Single(merged, s => s.Name == "CharacterPlacement");
        Assert.Equal(@"Scripts\Bases\Li_01_deck_land_01.thn",
            section.Single(e => e.Name == "start_script")[0].ToString());
        Assert.Equal("Zone_Bar_Character_Start", section.Single(e => e.Name == "name")[0].ToString());
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For("CharacterPlacement").Kind);
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For("room_info").Kind);
        Assert.Equal(SectionMatchKind.Singleton, SectionKeys.For("PlayerShipPlacement").Kind);
    }

    /// <summary>The map gives the base file of each base, the rooms of each base and the bases of each room file, in any
    /// letter case. The shared bar has two users. The roles and the schemas follow the map.</summary>
    [Fact]
    public void TheMapGivesTheBaseFileTheRoomsAndTheUsersOfEachBase()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var bases = session.Bases;

        Assert.Equal([BaseWorkspace.Station, BaseWorkspace.Planet, BaseWorkspace.Pirate], bases.Nicknames);
        Assert.Equal([BaseWorkspace.StationPath, BaseWorkspace.PlanetPath, BaseWorkspace.PiratePath], bases.Files,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(BaseWorkspace.PlanetPath, bases.FileOf("OL01_02_BASE"), StringComparer.OrdinalIgnoreCase);
        Assert.Null(bases.FileOf("no_base"));
        Assert.Equal([("Deck", BaseWorkspace.StationDeckPath), ("Bar", BaseWorkspace.BarPath)],
            bases.RoomsOf(BaseWorkspace.Station).Select(r => (r.Nickname, r.File.ToUpperInvariant())),
            new TupleComparer());
        Assert.Equal([BaseWorkspace.Station, BaseWorkspace.Pirate], bases.BasesOfRoomFile(BaseWorkspace.BarPath.ToLowerInvariant()));
        Assert.Equal([BaseWorkspace.Planet], bases.BasesOfRoomFile(BaseWorkspace.CityscapePath));
        Assert.Equal(5, bases.RoomFiles.Count);
        Assert.True(session.IsBaseFile(BaseWorkspace.PiratePath.ToLowerInvariant().Replace('\\', '/')));
        Assert.False(session.IsBaseFile(BaseWorkspace.BarPath));
        Assert.True(session.IsMBasesFile(@"data\missions\MBASES.INI"));
        Assert.True(session.IsNewsFile(BaseWorkspace.NewsPath));
        Assert.False(session.IsMBasesFile(BaseWorkspace.NewsPath));

        Assert.Equal(["base_files"], session.DataFiles.RolesOf(BaseWorkspace.StationPath));
        Assert.Equal(["mbases"], session.DataFiles.RolesOf(BaseWorkspace.MBasesPath));
        Assert.Equal(3, session.DataFiles.Paths("base_files").Count);
        Assert.True(session.KnowsSchema(BaseWorkspace.StationPath));
        Assert.True(session.KnowsSchema(BaseWorkspace.MBasesPath));
        Assert.False(session.KnowsSchema(BaseWorkspace.NewsPath));
        // The first use reads the files from the edit view, so it opens no document of them.
        Assert.Null(session.OpenedDocument(BaseWorkspace.StationPath));
    }

    /// <summary>The map reads W of universe.ini and of the base files, so an edit shows at once, and an undo puts the
    /// map back.</summary>
    [Fact]
    public void AnEditOfUniverseIniOrOfABaseFileChangesTheMap()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var pirate = BaseWorkspace.Section(session, BaseWorkspace.UniversePath, "Base", BaseWorkspace.Pirate);

        session.Execute(EditCommand.DeleteSection(pirate));
        Assert.False(session.IsBaseFile(BaseWorkspace.PiratePath));
        Assert.Equal([BaseWorkspace.Station], session.Bases.BasesOfRoomFile(BaseWorkspace.BarPath));
        Assert.False(session.IsRoomFile(BaseWorkspace.PirateDeckPath));
        session.Undo();
        Assert.True(session.IsBaseFile(BaseWorkspace.PiratePath));

        var room = BaseWorkspace.Section(session, BaseWorkspace.StationPath, "Room", "Bar");
        session.Execute(EditCommand.SetLinesFromText(room, "file", [@"Universe\Systems\Ol02\Bases\Rooms\ol02_01_deck.ini"]));
        Assert.Equal([BaseWorkspace.Pirate], session.Bases.BasesOfRoomFile(BaseWorkspace.BarPath));
        Assert.Equal([BaseWorkspace.Station, BaseWorkspace.Pirate], session.Bases.BasesOfRoomFile(BaseWorkspace.PirateDeckPath));
    }

    /// <summary>The editor knows the schemas of a base file and of mBases.ini (section C.3 of the bases design).
    /// [BaseInfo] has the keys of the fork and the retail key ship_repair_cost. The children of an [MBase] take
    /// edits, and individual_name holds a string id.</summary>
    [Fact]
    public void TheSchemasOfTheBaseFilesAndOfMBasesMakeTheirSectionsEditable()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var info = BaseWorkspace.Section(session, BaseWorkspace.StationPath, "BaseInfo");
        var room = BaseWorkspace.Section(session, BaseWorkspace.StationPath, "Room", "Deck");
        var npc = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC", "ol01_01_npc_1");
        var mbase = BaseWorkspace.Section(session, BaseWorkspace.MBasesPath, "MBase", BaseWorkspace.Station);

        Assert.Equal(["nickname", "start_room", "price_variance", "ship_repair_cost"],
            session.Schema(info).Select(k => k.Name));
        Assert.Equal(typeof(float), session.Schema(info, "ship_repair_cost")!.ClrType);
        Assert.Equal(["nickname", "file"], session.Schema(room).Select(k => k.Name));
        Assert.Equal(Data.Strings.StringKind.String, session.Schema(npc, "individual_name")!.TextKind);
        Assert.NotNull(session.Schema(npc, "rumor"));
        Assert.NotNull(session.Schema(mbase, "local_faction"));
        Assert.Empty(session.Check(info));
        Assert.Empty(session.Check(npc));
        foreach (var (section, key) in new[] { (info, "start_room"), (npc, "head"), (mbase, "diff") })
        {
            var binding = FieldBinding.Create(session, section, session.Get(section)!, key);
            Assert.Null(binding.ReadOnlyReason);
        }
        // A patch finds a child section by its nickname. An [MVendor] has none, and the nickname Bar of an [MRoom]
        // repeats in the other [MBase].
        Assert.NotNull(session.Get(BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "MVendor"))!.ReadOnlyReason);
        Assert.NotNull(session.Get(BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "MRoom", "Bar"))!.ReadOnlyReason);
        Assert.Null(session.Get(BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "MRoom", "Deck"))!.ReadOnlyReason);
        // The edits of mBases.ini stay patches, because the active mod holds no whole copy.
        Assert.False(session.OpenDocument(BaseWorkspace.MBasesPath)!.IsWholeFile);
        Assert.False(session.IsGuardedMissionFile(BaseWorkspace.MBasesPath));
    }

    private sealed class TupleComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
