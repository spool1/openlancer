using System;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Schema.Save;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>The OpenLancer form of the start save (section 5.5 of the M6c design, build step 2.2). The fork reads the
/// text back with <c>SaveGame.FromString</c>.</summary>
public sealed class StartSaveTextTests
{
    private static readonly DateTime Time = new(2026, 10, 21, 12, 30, 0, DateTimeKind.Local);

    private static readonly StartSavePlace InSpace = new("ol_sys_a", null, new Vector3(-33270, 0, -25039),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));

    private static StartPlan Plan(string? trigger) => StartPlan.Make(Index(StartPlanTests.Script), StartPlanTests.Slot,
        trigger == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(trigger));

    private static SaveGame Read(string text) => SaveGame.FromString("start.fl", text);

    [Fact]
    public void TheTextParsesToTheSameStoryMissionAndTriggerValues()
    {
        var plan = Plan("t_chosen");

        var save = Read(StartSaveText.OpenLancer(plan, InSpace, Time));

        Assert.Equal("Mission_99", save.StoryInfo!.Mission);
        Assert.Equal(5, save.StoryInfo.MissionNum);
        Assert.Equal(-1, save.StoryInfo.DeltaWorth);
        Assert.Equal(1, save.MissionState!.MissionAccepted);
        Assert.Equal(500001, save.MissionState.MsnTitle);
        Assert.Equal([new MissionRtc(@"missions\ol\ol_other.ini", false)], save.MissionState.Rtcs);
        Assert.Equal([FLHash.CreateID("ol_play_setup")], save.TriggerSave.Select(t => (uint)t.Trigger));
        Assert.NotNull(save.BStoryMissionDone);
        Assert.Equal("OpenLancer start: M99, t_chosen (approximate)", save.Player!.Description);
        Assert.Equal(Time, save.Player.TimeStamp);
        Assert.Equal("ol_sys_a", save.Player.System);
        Assert.Null(save.Player.Base);
        Assert.Equal(InSpace.Position, save.Player.Position);
        var turn = MathHelper.MatrixFromEulerDegrees(save.Player.Rotate).ExtractRotation();
        Assert.True(MathF.Abs(Quaternion.Dot(turn, InSpace.Orientation)) > 0.9999f, $"{turn} {InSpace.Orientation}");
    }

    [Fact]
    public void TheMissionPartHasItsOrderBeforeBStoryMissionDone()
    {
        var text = StartSaveText.OpenLancer(Plan("t_chosen"), InSpace, Time);
        int At(string line) => text.IndexOf(line, StringComparison.Ordinal);

        Assert.True(At("[Player]") == 0);
        Assert.True(At("[Player]") < At("[StoryInfo]"));
        Assert.True(At("Mission = Mission_99") < At("MissionNum = 5"));
        Assert.True(At("[StoryInfo]") < At("[MissionState]"));
        Assert.True(At("[MissionState]") < At("[TriggerSave]"));
        Assert.True(At("[TriggerSave]") < At("[BStoryMissionDone]"));
        Assert.EndsWith("[BStoryMissionDone]\r\n", text);
    }

    [Fact]
    public void MissionStateIsAlwaysPresentAndAMissionStartHasNoTriggerSave()
    {
        var text = StartSaveText.OpenLancer(Plan(null), InSpace, Time);

        Assert.Contains("[MissionState]\r\nmission_accepted = 0\r\n", text);
        Assert.DoesNotContain("[TriggerSave]", text);
        Assert.Contains("MissionNum = 4", text);
    }

    [Fact]
    public void TheTriggerIdIsTheGameIdAsAnUnsignedNumber()
    {
        var text = StartSaveText.OpenLancer(Plan("t_restore"), InSpace, Time);
        var id = FLHash.CreateID("t_restore");

        // The game id has its top bit set, so a signed number would be negative.
        Assert.True(id > int.MaxValue);
        Assert.Contains($"[TriggerSave]\r\ntrigger = {id}\r\n", text);
        Assert.Equal(id, (uint)Read(text).TriggerSave.Single().Trigger);
    }

    [Fact]
    public void TheDescriptionIsHexWithFourDigitsForEachUnit()
    {
        Assert.Equal("0041", StartSaveText.Hex("A"));
        Assert.Equal("004F0070", StartSaveText.Hex("Op"));
    }

    [Fact]
    public void ABaseStartWritesTheBaseAndNoPoint()
    {
        var text = StartSaveText.OpenLancer(Plan("t_chosen"), StartSavePlace.AtBase("ol_sys_a", "ol_base_a"), Time);

        Assert.Contains("system = ol_sys_a\r\nbase = ol_base_a\r\n", text);
        Assert.DoesNotContain("pos = ", text);
        Assert.Equal("ol_base_a", Read(text).Player!.Base);
    }

    [Fact]
    public void TheOpenLancerFormNeedsNoTemplate()
    {
        var text = StartSaveText.OpenLancer(Plan("t_restore"), InSpace, Time);
        var player = text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n");

        Assert.Equal(["[Player]", "description", "tstamp", "system", "pos", "rotate"],
            player.Select(l => l.Split(" = ")[0]));
    }

    /// <summary>
    /// An ambient script of the path writes the game id of its room, which the game makes from the base and the room
    /// name, as the fork writes it in its own saves. The fork finds the room of the base by that id when it loads the
    /// save (review fix of M6c-2: the id of the room name alone stopped the load).
    /// </summary>
    [Fact]
    public void AnAmbientScriptWritesTheIdOfTheRoomOfItsBase()
    {
        var text = StartPlanTests.Script.Replace("Act_ActTrig = t_scene",
            "Act_AddAmbient = scripts\\ol\\ol_amb.thn, ol_room, ol_base_a\nAct_ActTrig = t_scene");
        var plan = StartPlan.Make(Index(text), StartPlanTests.Slot, MissionStartPoint.At("t_chosen"));
        var room = FLHash.CreateLocationID("ol_base_a", "ol_room");

        var save = StartSaveText.OpenLancer(plan, InSpace, Time);

        Assert.Contains($"ambi_scene = scripts\\ol\\ol_amb.thn, {room}, {FLHash.CreateID("ol_base_a")}\r\n", save);
        var ambient = Assert.Single(Read(save).MissionState!.Ambients);
        Assert.Equal((room, FLHash.CreateID("ol_base_a")), (ambient.Room.Hash, ambient.Base.Hash));
        Assert.NotEqual(FLHash.CreateID("ol_room"), ambient.Room.Hash);
    }

    /// <summary>A ship with two mounted items, one of them without a hardpoint, and two goods in the hold.</summary>
    internal static readonly StartSaveShip Ship = new("ol_ship",
        [("ol_gun", "HpWeapon01"), ("ol_engine", null)], [("ol_missile", 3), ("ol_battery", 2)]);

    [Fact]
    public void AShipGivesItsShipEquipAndCargoLinesAfterThePlace()
    {
        var text = StartSaveText.OpenLancer(Plan("t_chosen"), InSpace, Time, Ship);
        var player = text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n");

        Assert.Equal(["[Player]", "description", "tstamp", "system", "pos", "rotate", "ship_archetype", "equip", "equip",
            "cargo", "cargo"], player.Select(l => l.Split(" = ")[0]));
        Assert.Equal(["ship_archetype = ol_ship", "equip = ol_gun, HpWeapon01", "equip = ol_engine",
            "cargo = ol_missile, 3", "cargo = ol_battery, 2"], player[6..]);
    }

    [Fact]
    public void TheShipLinesParseToTheSameShipEquipAndCargo()
    {
        var player = Read(StartSaveText.OpenLancer(Plan("t_chosen"), StartSavePlace.AtBase("ol_sys_a", "ol_base_a"),
            Time, Ship)).Player!;

        Assert.Equal(FLHash.CreateID("ol_ship"), (uint)player.ShipArchetype);
        Assert.Equal([(FLHash.CreateID("ol_gun"), "HpWeapon01"), (FLHash.CreateID("ol_engine"), null)],
            player.Equip.Select(e => ((uint)e.Item, e.Hardpoint)));
        Assert.Equal([(FLHash.CreateID("ol_missile"), 3), (FLHash.CreateID("ol_battery"), 2)],
            player.Cargo.Select(c => ((uint)c.Item, c.Count)));
        Assert.All(player.Cargo, c => Assert.False(c.IsMissionCargo));
        Assert.Equal("ol_base_a", player.Base);
    }

    [Fact]
    public void WithoutAShipTheTextHasNoShipLines()
    {
        var text = StartSaveText.OpenLancer(Plan("t_chosen"), InSpace, Time);

        Assert.Equal(text, StartSaveText.OpenLancer(Plan("t_chosen"), InSpace, Time, null));
        Assert.DoesNotContain("ship_archetype", text);
        Assert.DoesNotContain("equip = ", text);
        Assert.DoesNotContain("cargo = ", text);
        Assert.Equal(0u, (uint)Read(text).Player!.ShipArchetype);
    }

    /// <summary>The addon rule of a ship package (decision D47): the amount 1 mounts the equipment at its hardpoint, a
    /// larger amount puts it into the hold, and an amount below 1 gives nothing. The order of the addons stays.</summary>
    [Fact]
    public void TheAddonsOfAPackageGiveTheShipPartByTheirAmount()
    {
        var ship = StartSaveShip.FromAddons("ol_ship",
        [
            ("ol_gun", "HpWeapon01", 1), ("ol_mine", "HpMine01", 2), ("ol_engine", null, 1),
            ("ol_shield", "HpShield01", 0), ("ol_missile", "internal", 5), ("ol_light", "HpLight01", -1),
        ]);

        Assert.Equal("ol_ship", ship.Ship);
        Assert.Equal([("ol_gun", "HpWeapon01"), ("ol_engine", null)], ship.Equip);
        Assert.Equal([("ol_mine", 2), ("ol_missile", 5)], ship.Cargo);
    }

    [Fact]
    public void APlanThatCannotStartHasNoStartSave()
    {
        var plan = StartPlan.Make(Index(StartPlanTests.Script), null, MissionStartPoint.MissionStart);

        Assert.Throws<ArgumentException>(() => StartSaveText.OpenLancer(plan, InSpace, Time));
    }
}
