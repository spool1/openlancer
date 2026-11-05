using System;
using System.Linq;
using LibreLancer.Data.IO;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The INI scan of <see cref="ThnReferences"/> on small text INI files (test 13.1 item 15 of the M8 design): the path
/// rule, and the rooms of a script.
/// </summary>
public sealed class ThnReferencesTests
{
    /// <summary>A doubled backslash, a slash and another case name one script, as the engine's file system reads
    /// them.</summary>
    [Fact]
    public void ThreeSpellingsOfAScriptGiveOnePath()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\MISSIONS\M01A\X.THN", "-- script");
        ws.WriteBase(@"DATA\Missions\a.ini", "[Trigger]\nAct_CallThorn = missions\\\\m01a\\\\x.thn, obj\n");
        ws.WriteBase(@"DATA\Missions\b.ini", "[Trigger]\nAct_CallThorn = Missions/M01a/x.thn\n");
        ws.WriteBase(@"DATA\Missions\c.ini", "[Trigger]\nAct_CallThorn = MISSIONS\\M01A\\X.THN\n");

        var references = ThnReferences.Scan(FileSystem.FromPath(ws.Base));

        var path = Assert.Single(references.Paths);
        Assert.Equal(@"DATA\missions\m01a\x.thn", path, ignoreCase: true);
        Assert.Equal(["missions\\\\m01a\\\\x.thn", "Missions/M01a/x.thn", @"MISSIONS\M01A\X.THN"],
            references.UsedBy(@"data/missions/M01A/x.thn").Select(r => r.Value));
        Assert.Equal(["Act_CallThorn"], references.All.Select(r => r.Key).Distinct());
        Assert.Empty(references.Problems);
    }

    /// <summary>
    /// The rooms come from the INI chain: universe.ini names the base file, the base file names the room file, and the
    /// room file names its scripts. mbases.ini, an RTC file and a mission trigger name the base and the room directly.
    /// The fidgets are the fixture scripts, the [Char] fidgets and the scripts of the fidget segment.
    /// </summary>
    [Fact]
    public void TheRoomsOfAScriptComeFromTheRoomFilesAndTheOtherIniFiles()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\UNIVERSE\universe.ini",
            "[Base]\nnickname = Li01_01_Base\nfile = Universe\\Bases\\Li01_01.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\Bases\Li01_01.ini",
            "[Room]\nnickname = Cityscape\nfile = Universe\\Bases\\Rooms\\city.ini\n" +
            "[Room]\nnickname = Bar\nfile = Universe\\Bases\\Rooms\\bar.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\Bases\Rooms\city.ini",
            "[Room_Info]\nset_script = Scripts\\city_set.thn\nscene = all, ambient, Scripts\\city_ambi.thn\n" +
            "[PlayerShipPlacement]\nname = X/Shipcentre/01\nlaunching_script = Scripts\\city_launch.thn\n");
        ws.WriteBase(@"DATA\UNIVERSE\Bases\Rooms\bar.ini", "[Room_Info]\nset_script = Scripts\\bar_set.thn\n");
        ws.WriteBase(@"DATA\MISSIONS\mbases.ini",
            "[MBase]\nnickname = li01_01_base\n[MRoom]\nnickname = bar\n" +
            "fixture = li_bartender, Zs/NPC/Bartender/01/A/Stand, Scripts\\bartender_fidget.thn, bartender\n");
        ws.WriteBase(@"DATA\MISSIONS\rtc.ini",
            "[CharacterEncounter]\naction = Scripts\\story.thn\nLocation = Li01_01_base, Cityscape\n" +
            "[Char]\nnpc = juni\nactor = juni\nfidget = Scripts\\char_fidget.thn\n");
        ws.WriteBase(@"DATA\MISSIONS\m01.ini", "[Trigger]\nAct_AddAmbient = Scripts\\\\music.thn, Bar, Li01_01_Base\n");
        ws.WriteBase(@"DATA\SCRIPTS\GenericScripts.ini",
            "[GenericScripts]\nset_segment = fidget\nscript = Scripts\\gcs_fidget.thn\n" +
            "set_segment = idle\nscript = Scripts\\gcs_idle.thn\n");

        var references = ThnReferences.Scan(FileSystem.FromPath(ws.Base));

        Assert.Equal(new ThnRoomUse("Li01_01_Base", "Cityscape", ThnRoomRole.Set, @"DATA\UNIVERSE\Bases\Rooms\city.ini"),
            Assert.Single(references.Rooms(@"DATA\Scripts\city_set.thn")));
        Assert.Equal(ThnRoomRole.Scene, Assert.Single(references.Rooms(@"DATA\Scripts\city_ambi.thn")).Role);
        Assert.Equal(ThnRoomRole.Launching, Assert.Single(references.Rooms(@"DATA\Scripts\city_launch.thn")).Role);
        Assert.Equal("Bar", Assert.Single(references.Rooms(@"DATA\Scripts\bar_set.thn")).Room);
        Assert.Equal(new ThnRoomUse("li01_01_base", "bar", ThnRoomRole.Fixture, @"DATA\MISSIONS\mbases.ini",
            "Zs/NPC/Bartender/01/A/Stand"), Assert.Single(references.Rooms(@"DATA\Scripts\bartender_fidget.thn")));
        Assert.Equal(new ThnRoomUse("Li01_01_base", "Cityscape", ThnRoomRole.Encounter, @"DATA\MISSIONS\rtc.ini"),
            Assert.Single(references.Rooms(@"DATA\Scripts\story.thn")));
        Assert.Equal(new ThnRoomUse("Li01_01_Base", "Bar", ThnRoomRole.Ambient, @"DATA\MISSIONS\m01.ini"),
            Assert.Single(references.Rooms(@"DATA\Scripts\music.thn")));
        Assert.Empty(references.Rooms(@"DATA\Scripts\char_fidget.thn"));

        Assert.True(references.IsFidget(@"DATA\Scripts\bartender_fidget.thn"));
        Assert.True(references.IsFidget(@"DATA\Scripts\char_fidget.thn"));
        Assert.True(references.IsFidget(@"DATA\Scripts\gcs_fidget.thn"));
        Assert.False(references.IsFidget(@"DATA\Scripts\gcs_idle.thn"));
        Assert.False(references.IsFidget(@"DATA\Scripts\story.thn"));
    }

    /// <summary>
    /// The rename list of an entity (section 7.5 of the M8 design): the room INI names and the fixture spots of the rooms
    /// whose set script or scene script is the script, without letter case, and the REFERENCE entities with the name in
    /// the other scripts of those rooms. A script that only a cutscene of the room plays has no such list.
    /// </summary>
    [Fact]
    public void TheRenameListHasTheRoomNamesTheFixtureSpotsAndTheReferenceEntities()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\UNIVERSE\universe.ini", "[Base]\nnickname = B\nfile = Universe\\b.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\b.ini", "[Room]\nnickname = Bar\nfile = Universe\\bar.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\bar.ini",
            "[Room_Info]\nset_script = Scripts\\set.thn\nscene = all, ambient, Scripts\\ambi.thn\n" +
            "[Camera]\nname = Camera_0\n[CharacterPlacement]\nname = Zg/PC/Player/01/A/Stand\nstart_script = Scripts\\enter.thn\n" +
            "[PlayerShipPlacement]\nname = X/Shipcentre/01\n");
        ws.WriteBase(@"DATA\MISSIONS\mbases.ini",
            "[MBase]\nnickname = b\n[MRoom]\nnickname = bar\nfixture = npc, camera_0, Scripts\\fidget.thn, bartender\n" +
            "[MBase]\nnickname = other\n[MRoom]\nnickname = bar\nfixture = npc, Camera_0, Scripts\\fidget.thn, bartender\n");
        var references = ThnReferences.Scan(FileSystem.FromPath(ws.Base));

        var uses = references.EntityNameUses(@"DATA\Scripts\set.thn", "CAMERA_0");

        Assert.Equal([(@"DATA\UNIVERSE\bar.ini", "Camera", "name", "Camera_0"), (@"DATA\MISSIONS\mbases.ini", "MRoom", "fixture", "camera_0")],
            uses.Select(u => (u.File, u.Section, u.Key, u.Value)));
        Assert.Single(references.EntityNameUses(@"DATA\Scripts\ambi.thn", "x/shipcentre/01"));
        Assert.Empty(references.EntityNameUses(@"DATA\Scripts\enter.thn", "Camera_0"));
        Assert.Equal([@"DATA\Scripts\ambi.thn", @"DATA\Scripts\enter.thn", @"DATA\Scripts\fidget.thn"],
            references.OtherRoomScripts(@"DATA\Scripts\set.thn").Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        // The other scripts of the room that have a REFERENCE entity with the name.
        var withReference = OpenLancer.Data.Thn.ThnScriptModel.From(ThnScripts.Evaluate(
            "duration = 1\nentities = { { entity_name = \"camera_0\", type = CAMERA, flags = REFERENCE } }\nevents = {}\n"),
            ThnElementId.Counter());
        var withCopy = OpenLancer.Data.Thn.ThnScriptModel.From(ThnScripts.Evaluate(
            "duration = 1\nentities = { { entity_name = \"Camera_0\", type = CAMERA } }\nevents = {}\n"), ThnElementId.Counter());
        var list = OpenLancer.Studio.Commands.CutsceneRename.Uses(references, @"DATA\Scripts\set.thn", "Camera_0",
            path => path.EndsWith("enter.thn", StringComparison.OrdinalIgnoreCase) ? withReference
                : path.EndsWith("ambi.thn", StringComparison.OrdinalIgnoreCase) ? withCopy : null,
            ThnScripts.Evaluator.Symbols);
        Assert.Equal(["bar.ini [Camera] name = Camera_0", "mbases.ini [MRoom] fixture = camera_0",
            "enter.thn: the REFERENCE entity camera_0"], list.Select(u => u.Text));
    }

    /// <summary>A reference has the place of its section in the file, so that the Cutscenes tab can show that section
    /// also when another section has the same line.</summary>
    [Fact]
    public void AReferenceHasThePlaceOfItsSection()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\MISSIONS\m.ini",
            "[Mission]\nnickname = m\n[Trigger]\nnickname = a\nAct_CallThorn = x.thn\n" +
            "[Trigger]\nnickname = b\nAct_CallThorn = x.thn\n");

        var references = ThnReferences.Scan(FileSystem.FromPath(ws.Base));

        Assert.Equal([1, 2], references.UsedBy(@"DATA\x.thn").Select(r => r.SectionIndex));
    }

    /// <summary>A room file that no base file names gives no room, and a file that the parser cannot read is a
    /// problem, not an exception.</summary>
    [Fact]
    public void ARoomFileWithoutABaseGivesNoRoomAndABrokenFileIsAProblem()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\Rooms\orphan.ini", "[Room_Info]\nset_script = Scripts\\set.thn\n");
        // A binary INI file of version 2, which the engine parser refuses.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(ws.Base, @"DATA\broken.ini"),
            "BINI\u0002\0\0\0"u8.ToArray());

        var references = ThnReferences.Scan(FileSystem.FromPath(ws.Base));

        Assert.Single(references.UsedBy(@"DATA\Scripts\set.thn"));
        Assert.Empty(references.Rooms(@"DATA\Scripts\set.thn"));
        var problem = Assert.Single(references.Problems);
        Assert.Equal(@"DATA\broken.ini", problem.Source);
    }

    /// <summary>
    /// Each [Trigger] Act_AddRTC line shows in RtcLines with its section index, also with a doubled backslash. PathOf of
    /// its value gives the install path of the RTC file. An Act_AddRTC key outside a [Trigger] section does not show
    /// (section 4.3 of the cutscene categories design).
    /// </summary>
    [Fact]
    public void TheAddRtcLinesOfTheTriggersShowInScanOrder()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(@"DATA\MISSIONS\MX07\mx07.ini",
            "[Mission]\nnickname = mx07\n[Trigger]\nnickname = a\nAct_CallThorn = missions\\\\mx07\\\\zz_a.thn\n" +
            "[Trigger]\nnickname = b\nAct_AddRTC = missions\\\\mx07\\\\zz_rtc.ini\n" +
            "[Other]\nAct_AddRTC = missions\\mx07\\zz_not.ini\n");

        var references = ThnReferences.Scan(FileSystem.FromPath(ws.Base));

        var line = Assert.Single(references.RtcLines);
        Assert.Equal((@"DATA\MISSIONS\MX07\mx07.ini", "Trigger", "Act_AddRTC", 2),
            (line.File, line.Section, line.Key, line.SectionIndex));
        Assert.Equal(@"missions\\mx07\\zz_rtc.ini", line.Value);
        Assert.Equal(@"DATA\missions\mx07\zz_rtc.ini", ThnReferences.PathOf(line.Value));
        Assert.DoesNotContain(references.All, r => r.Key == "Act_AddRTC");
    }
}
