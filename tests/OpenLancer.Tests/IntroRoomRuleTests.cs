using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The <c>set_script</c> rule skips the rooms of the intro bases (section 6 of the console filter design). The fork marks
/// an intro base by the nickname prefix "intro", and only the main menu shows its rooms.
/// </summary>
public sealed class IntroRoomRuleTests
{
    private const string IntroBasePath = @"DATA\UNIVERSE\SYSTEMS\INTRO\BASES\intro1_base.ini";
    private const string IntroRoomPath = @"DATA\UNIVERSE\SYSTEMS\INTRO\BASES\ROOMS\intro1_cityscape.ini";

    private static TestWorkspace Create()
    {
        var ws = RoomWorkspace.Create();
        ws.WriteBase(RoomWorkspace.UniversePath, """
            [Base]
            nickname = Li01_01_Base
            system = Li01
            file = Universe\Systems\Li01\Bases\li01_01_base.ini
            [Base]
            nickname = Li01_02_Base
            system = Li01
            file = universe/systems/LI01/bases/li01_02_base.ini
            [Base]
            nickname = Intro1_Base
            system = Intro1
            file = Universe\Systems\Intro\Bases\intro1_base.ini
            """);
        ws.WriteBase(IntroBasePath, """
            [BaseInfo]
            nickname = Intro1_Base
            [Room]
            nickname = Cityscape
            file = Universe\Systems\Intro\Bases\Rooms\intro1_cityscape.ini
            [Room]
            nickname = Bar
            file = Universe\Systems\Li01\Bases\Rooms\li01_01_bar.ini
            """);
        ws.WriteBase(IntroRoomPath, "[Room_Info]\nscene = all, ambient, Scripts\\Bases\\Li_01_bar_ambi_int_01.thn\n");
        return ws;
    }

    [Fact]
    public void ARoomOfAnIntroBaseNeedsNoSetScript()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var info = RoomWorkspace.Section(session, IntroRoomPath, "Room_Info");

        Assert.True(session.Bases.IsIntroRoom(IntroRoomPath));
        Assert.Empty(session.Lists.Problems(info));

        // A change of the active mod gives no error either, because the player cannot enter the room.
        session.Execute(EditCommand.SetLinesFromText(info, "scene", [@"all, ambient, Scripts\Bases\Li_01_bar_ambi_int_s.thn"]));
        Assert.Empty(session.Lists.Problems(info));
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>A room that a normal base also names is not an intro room, so the rule applies.</summary>
    [Fact]
    public void ARoomThatANormalBaseAlsoNamesKeepsTheRule()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var bar = RoomWorkspace.Section(session, RoomWorkspace.BarPath, "Room_Info");

        Assert.False(session.Bases.IsIntroRoom(RoomWorkspace.BarPath));
        session.Execute(EditCommand.RemoveName(bar, "set_script"));

        Assert.Contains(session.Lists.Problems(bar), p => p.Diagnostic.Key == "set_script" && p.IsError);
    }
}
