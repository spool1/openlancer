using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The world of the start system (section 5.3, note of 2026-10-28 after step 7 of the M6c design, a change of D748): the
/// setup makes only the objects of the start system, and the ships that travel with the player start next to the
/// player. The scripts are synthetic, and all names are invented.
/// </summary>
public sealed class StartWorldTests
{
    /// <summary>
    /// A path in two systems. The first trigger spawns objects in the first system: a jumper, a ship whose record names
    /// the first system, a solar, a loot and a formation. The arrival in the second system moves the player to a point
    /// there and spawns a ship of the second system. The chosen trigger waits in the second system. The marks in braces
    /// take more lines.
    /// </summary>
    private const string Story = """
        [MsnShip]
        nickname = ol_wing
        label = ol_friends
        jumper = true
        position = 100, 0, 100

        [MsnShip]
        nickname = ol_guard
        label = ol_friends
        {GUARD}
        position = 5000, 0, 5000

        [MsnShip]
        nickname = ol_local
        label = ol_locals
        position = 30000, 0, 30000

        [MsnShip]
        nickname = ol_f1
        position = 0, 0, 0

        [MsnShip]
        nickname = ol_f2
        position = 0, 0, 0

        [MsnFormation]
        nickname = ol_form_a
        position = 3000, 0, 3000
        ship = ol_f1
        ship = ol_f2

        {RECORDS}

        [MsnSolar]
        nickname = ol_station
        position = 2000, 0, 2000

        [MsnLoot]
        nickname = ol_crate
        position = 2500, 0, 2500

        [ObjList]
        nickname = ol_patrol
        GotoVec = goto, 0, 0, 1000, 100

        [ObjList]
        nickname = ol_follow
        Follow = Player, 500, 200, 0, 50

        [ObjList]
        nickname = ol_follow_near
        Follow = Player, 500

        [ObjList]
        nickname = ol_wingmen
        FollowPlayer = ol_shape, ol_wing, ol_local

        [ObjList]
        nickname = ol_hunt
        GotoShip = goto, Player, 100

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_a

        [Trigger]
        nickname = t_a
        {A_SYSTEM}
        Act_SpawnShip = ol_wing
        Act_SpawnShip = ol_guard
        Act_SpawnSolar = ol_station
        Act_SpawnLoot = ol_crate
        Act_SpawnFormation = ol_form_a
        Act_GiveObjList = ol_guard, ol_patrol
        Act_SetVibe = ol_guard, Player, REP_FRIEND_THRESHOLD
        Act_SetVibeLbl = ol_friends, Player, REP_FRIEND_THRESHOLD
        Act_MarkObj = ol_station, 1
        Act_LightFuse = ol_station, ol_fuse_1
        Act_LightFuse = ol_buoy_a, ol_fuse_2
        Act_SetRep = Player, ol_faction_a, -0.9
        {A}
        Act_ActTrig = t_jump

        [Trigger]
        nickname = t_jump
        system = ol_sys_b
        Cnd_SystemEnter = ol_sys_b
        Act_MovePlayer = 10000, 0, 10000, 100
        Act_SpawnShip = ol_local
        {JUMP}
        Act_ActTrig = t_chosen

        [Trigger]
        nickname = t_chosen
        system = ol_sys_b
        Cnd_Timer = 5

        {TRIGGERS}
        """;

    /// <summary>The start point of <see cref="Story"/>.</summary>
    private static readonly Vector3 Start = new(10000, 0, 10000);

    /// <summary>A buoy of the system file of each system, and the dock ring of a base of the second system. The ring
    /// turns +z to -z.</summary>
    private static readonly WorldObject[] Objects =
    [
        new("ol_buoy_a", "ol_sys_a", new Vector3(1, 2, 3), Quaternion.Identity, null),
        new("ol_buoy_b", "ol_sys_b", new Vector3(4, 5, 6), Quaternion.Identity, null),
        new("ol_ring_b", "ol_sys_b", new Vector3(-20000, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
            null) { DockWith = "ol_base_b" },
    ];

    internal static MissionWorld World() =>
        new(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["ol_sys_a", "ol_sys_b"],
            [MissionValueKind.Base] = ["ol_base_b", "ol_base_c"],
            [MissionValueKind.SystemObject] = Objects.Select(o => o.Nickname).ToList(),
        }, null, new Dictionary<string, string> { ["ol_base_b"] = "ol_sys_b", ["ol_base_c"] = "ol_sys_b" }, Objects);

    /// <summary>The story with more lines: <paramref name="a"/> in the first trigger, <paramref name="jump"/> after the
    /// arrival, <paramref name="records"/> before the solar and <paramref name="triggers"/> at the end.</summary>
    internal static string Script(string a = "", string jump = "", string records = "", string triggers = "",
        string guard = "system = ol_sys_a", string aSystem = "system = ol_sys_a\nCnd_SpaceEnter = ol_sys_a") =>
        Story.Replace("{A}", a).Replace("{JUMP}", jump).Replace("{RECORDS}", records).Replace("{TRIGGERS}", triggers)
            .Replace("{GUARD}", guard).Replace("{A_SYSTEM}", aSystem);

    private static StartPlan Plan(string text, string trigger = "t_chosen",
        StartPlaceChoice choice = StartPlaceChoice.Suggested, string? dock = null, StartSavePlace? play = null)
    {
        var plan = StartPlan.Make(Index(text, World()), StartPlanTests.Slot, MissionStartPoint.At(trigger), choice, dock,
            play: play);
        Assert.True(plan.CanStart, plan.Reason);
        return plan;
    }

    /// <summary>The lines of the setup trigger of the world, as "key = values".</summary>
    private static List<string> WorldLines(StartPlan plan) =>
        plan.Setup.Count < 2
            ? []
            : plan.Setup[1].Lines.Skip(1).Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

    private static List<string> SetupLines(StartPlan plan) =>
        plan.Setup[0].Lines.Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

    private static StartCompanion Companion(StartPlan plan, string name) =>
        Assert.Single(plan.Companions, c => c.Object == name);

    private static void Near(Vector3 expected, Vector3? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Vector3.Distance(expected, actual!.Value) < 0.05f, $"{expected} != {actual}");
    }

    // Rules 1 and 2: the runs of the replay in order, and the system of the player at each run.

    /// <summary>The replay keeps the runs and the effects in order. A run takes the system of its condition, else its
    /// system hint, else the system of the run before.</summary>
    [Fact]
    public void TheReplayKeepsTheRunsWithTheSystemOfThePlayer()
    {
        var index = Index(Script(), World());
        var replay = StartReplay.Run(index, MissionStartPoint.At("t_chosen"));
        var facts = StartPlaces.World(index, replay);

        Assert.Equal(["t_start -", "t_a ol_sys_a", "t_jump ol_sys_b"], replay.Changes
            .Select((change, at) => (change, at)).Where(p => p.change.Kind == MachineChangeKind.Ran)
            .Select(p => $"{p.change.Trigger.Nickname} {facts.Systems[p.at] ?? "-"}"));
        Assert.Equal(replay.Changes.Count(c => c.Kind == MachineChangeKind.Effect), replay.Effects.Count);
        Assert.Equal("ol_sys_b", facts.Systems[^1]);
    }

    // Group 1: the track of each object (rule 3).

    /// <summary>A ship or a formation goes to the system of its record, else to the system of the player. A solar and a
    /// loot go to the system of the player. Each object of the first system is left out, and the jumper jumps with the
    /// player.</summary>
    [Fact]
    public void TheSetupMakesOnlyTheObjectsOfTheStartSystem()
    {
        var plan = Plan(Script());

        Assert.Equal(StartPlaceKind.Space, plan.Place.Kind);
        Assert.Equal("ol_sys_b", plan.Place.System);
        Assert.Equal(Start, plan.Place.Position);
        Assert.Equal(
        [
            "ol_guard Ship ol_sys_a t_a",
            "ol_station Solar ol_sys_a t_a",
            "ol_crate Loot ol_sys_a t_a",
            "ol_form_a Formation ol_sys_a t_a",
            "ol_station Fuse ol_sys_a t_a ol_fuse_1",
            "ol_buoy_a Fuse ol_sys_a t_a ol_fuse_2",
        ], plan.LeftOut.Select(l => $"{l.Object} {l.Kind} {l.System} {l.Trigger} {l.Fuse}".TrimEnd()));
        var wing = Companion(plan, "ol_wing");
        Assert.Equal(CompanionSigns.Jump, wing.Signs);
        Assert.Equal("ol_sys_a", wing.JumpedFrom);
        Assert.Equal(
        [
            "Act_SpawnShip = ol_wing",
            "Act_RelocateShip = ol_wing, 9700, 0, 10000, 1, 0, 0, 0",
            "Act_SetVibeLbl = ol_friends, Player, REP_FRIEND_THRESHOLD",
            "Act_SpawnShip = ol_local",
        ], WorldLines(plan));
        // A state line has no place, so it stays.
        Assert.Contains("Act_SetRep = Player, ol_faction_a, -0.9", SetupLines(plan));
    }

    /// <summary>A ship whose record names the start system stays. A move into the system of the player brings an object
    /// of another system into the start system.</summary>
    [Fact]
    public void ARecordSystemAndAMoveIntoTheStartSystemKeepTheObject()
    {
        var plan = Plan(Script(guard: "system = ol_sys_b", jump: "Act_RelocateShip = ol_f1, 20000, 0, 20000"));
        var lines = WorldLines(plan);

        Assert.DoesNotContain(plan.LeftOut, l => l.Object is "ol_guard" or "ol_form_a");
        Assert.Contains("Act_SpawnShip = ol_guard", lines);
        Assert.Contains("Act_GiveObjList = ol_guard, ol_patrol", lines);
        Assert.Contains("Act_SpawnFormation = ol_form_a", lines);
        Assert.Contains("Act_RelocateShip = ol_f1, 20000, 0, 20000", lines);
    }

    /// <summary>The jumper flag at the time of the jump counts. A line that turns the flag off after the jump does not
    /// bring the ship back. A line that turns it off before the jump keeps the ship in the first system.
    /// <c>Act_Jumper</c> of a label sets each member.</summary>
    [Fact]
    public void TheJumperFlagAtTheTimeOfTheJumpCounts()
    {
        var after = Plan(Script(jump: "Act_Jumper = ol_wing, false"));
        Assert.Equal(CompanionSigns.Jump, Companion(after, "ol_wing").Signs);

        var before = Plan(Script(a: "Act_Jumper = ol_wing, false"));
        Assert.Empty(before.Companions);
        Assert.Contains(before.LeftOut, l => l.Object == "ol_wing");

        var label = Plan(Script(a: "Act_Jumper = ol_friends, true"));
        Assert.Equal(["ol_wing", "ol_guard"], label.Companions.Select(c => c.Object));
        Assert.DoesNotContain(label.LeftOut, l => l.Object == "ol_guard");
    }

    /// <summary>An object whose system no line tells counts as an object of the start system.</summary>
    [Fact]
    public void AnObjectOfAnUnknownSystemStays()
    {
        var plan = Plan(Script(guard: "", aSystem: "Cnd_True = no_params"));

        Assert.DoesNotContain(plan.LeftOut, l => l.Kind != StartObjectKind.Fuse);
        Assert.Contains("Act_SpawnShip = ol_guard", WorldLines(plan));
        Assert.Contains("Act_SpawnSolar = ol_station", WorldLines(plan));
        // A buoy of the system file of another system is outside the start system all the same.
        Assert.Contains(plan.LeftOut, l => l is { Object: "ol_buoy_a", Kind: StartObjectKind.Fuse });
        Assert.Empty(plan.Companions);
    }

    // Group 2: rule 1 of the default.

    /// <summary>The lines that name an object outside the start system go: orders, attitudes, marks, destroys and fuses.
    /// A label with a member in the start system keeps its lines. A fuse on an object of a system file of the start
    /// system stays.</summary>
    [Fact]
    public void TheLinesOfObjectsOutsideTheStartSystemGo()
    {
        var plan = Plan(Script(jump: """
            Act_SetVibe = ol_local, ol_guard, REP_HOSTILE_MAXIMUM
            Act_Destroy = ol_crate
            Act_LightFuse = ol_buoy_b, ol_fuse_3
            Act_LightFuse = ol_local, ol_fuse_4
            Act_LightFuse = ol_f2, ol_fuse_5
            """));
        var lines = WorldLines(plan);

        Assert.DoesNotContain(lines, l => l.Contains("ol_guard") || l.Contains("ol_station") || l.Contains("ol_crate") ||
                                          l.Contains("ol_buoy_a") || l.Contains("ol_form_a") || l.Contains("ol_f2"));
        Assert.Contains("Act_SetVibeLbl = ol_friends, Player, REP_FRIEND_THRESHOLD", lines);
        Assert.Contains("Act_LightFuse = ol_buoy_b, ol_fuse_3", lines);
        Assert.Contains("Act_LightFuse = ol_local, ol_fuse_4", lines);
        Assert.Contains(plan.LeftOut, l => l is { Object: "ol_f2", Kind: StartObjectKind.Fuse, Fuse: "ol_fuse_5" });
    }

    /// <summary>A fuse on a mission object that no line of the setup makes goes. The page tells that no line makes
    /// it.</summary>
    [Fact]
    public void AFuseOnAnObjectThatNoLineMakesGoes()
    {
        var plan = Plan(Script(jump: "Act_LightFuse = ol_unmade, ol_fuse_6", records: """
            [MsnShip]
            nickname = ol_unmade
            """));

        var fuse = Assert.Single(plan.LeftOut, l => l.Object == "ol_unmade");
        Assert.Equal(StartObjectKind.Fuse, fuse.Kind);
        Assert.Null(fuse.System);
        Assert.DoesNotContain(WorldLines(plan), l => l.Contains("ol_fuse_6"));
    }

    // Group 3: double spawns (rule 4).

    /// <summary>A second spawn line of an object makes a new object. The earlier spawn line goes, with each line between
    /// that names the object. A line that names it only through a label stays. A spawn after Act_Destroy stays
    /// alone.</summary>
    [Fact]
    public void ASecondSpawnRemovesTheEarlierSpawnAndTheLinesBetween()
    {
        var plan = Plan(Script(jump: """
            Act_GiveObjList = ol_local, ol_patrol
            Act_SetVibeLbl = ol_locals, Player, REP_FRIEND_THRESHOLD
            Act_SpawnShip = ol_local
            Act_SpawnFormation = ol_form_b
            Act_MarkObj = ol_form_b, 1
            Act_SpawnFormation = ol_form_b
            """, records: """
            [MsnShip]
            nickname = ol_g1
            position = 0, 0, 0

            [MsnFormation]
            nickname = ol_form_b
            position = 12000, 0, 12000
            ship = ol_g1
            """));
        var lines = WorldLines(plan);

        Assert.Equal(["Act_SetVibeLbl = ol_locals, Player, REP_FRIEND_THRESHOLD", "Act_SpawnShip = ol_local",
            "Act_SpawnFormation = ol_form_b"], lines.SkipWhile(l => !l.Contains("ol_locals")).ToList());
        Assert.Single(lines, l => l == "Act_SpawnShip = ol_local");
        Assert.DoesNotContain("Act_GiveObjList = ol_local, ol_patrol", lines);
        Assert.DoesNotContain("Act_MarkObj = ol_form_b, 1", lines);

        var destroyed = WorldLines(Plan(Script(jump: """
            Act_Destroy = ol_local
            Act_SpawnShip = ol_local
            """)));
        Assert.Single(destroyed, l => l == "Act_SpawnShip = ol_local");
        Assert.DoesNotContain(destroyed, l => l.StartsWith("Act_Destroy", StringComparison.Ordinal));
    }

    // Group 4: the signs of a companion (rule 7).

    /// <summary>Each sign alone makes a companion: orders that follow the player, the formation of the player, a jump
    /// with the player, a nag that keeps the player near the ship, and a waiting tether trigger.</summary>
    [Fact]
    public void EachSignAloneMakesACompanion()
    {
        Assert.Equal(CompanionSigns.Orders,
            Companion(Plan(Script(jump: "Act_GiveObjList = ol_local, ol_follow")), "ol_local").Signs);
        Assert.Equal(CompanionSigns.PlayerFormation,
            Companion(Plan(Script(jump: "Act_GiveObjList = Player, ol_wingmen")), "ol_local").Signs);
        Assert.Equal(CompanionSigns.Jump, Companion(Plan(Script()), "ol_wing").Signs);
        Assert.Equal(CompanionSigns.Escort,
            Companion(Plan(Script(jump: "Act_NagDistLeaving = ol_nag, ol_local, ol_local, 3000")), "ol_local").Signs);
        Assert.Equal(CompanionSigns.Escort, Companion(Plan(Script(jump: "Act_ActTrig = t_tether", triggers: """
            [Trigger]
            nickname = t_tether
            Cnd_TetherBroke = ol_local, Player, 5000, 10
            """)), "ol_local").Signs);
    }

    /// <summary>A ship that follows a companion is a companion too.</summary>
    [Fact]
    public void AShipThatFollowsACompanionIsACompanion()
    {
        var plan = Plan(Script(jump: """
            Act_GiveObjList = ol_local, ol_follow
            Act_SpawnShip = ol_escort
            """, records: """
            [MsnShip]
            nickname = ol_escort
            position = 40000, 0, 40000
            init_objectives = ol_follow_local

            [ObjList]
            nickname = ol_follow_local
            Follow = ol_local, 300
            """));

        Assert.Equal(["ol_wing", "ol_local", "ol_escort"], plan.Companions.Select(c => c.Object));
        Assert.Equal(CompanionSigns.Orders, Companion(plan, "ol_escort").Signs);
    }

    /// <summary>These give no companion: a hostile follower, a solar with a nag, a nag that a later line stops, an attack
    /// on the player, a move of the ship and the player by one trigger, the formation of the player before the last spawn,
    /// and a jumper that a later line places.</summary>
    [Fact]
    public void TheseAreNotSigns()
    {
        Assert.DoesNotContain(Plan(Script(jump: """
            Act_GiveObjList = ol_local, ol_follow
            Act_SetVibe = ol_local, Player, REP_HOSTILE_MAXIMUM
            """)).Companions, c => c.Object == "ol_local");
        Assert.DoesNotContain(Plan(Script(jump: """
            Act_SpawnSolar = ol_station_b
            Act_NagDistLeaving = ol_nag, Player, ol_station_b, 3000
            """, records: """
            [MsnSolar]
            nickname = ol_station_b
            position = 9000, 0, 9000
            """)).Companions, c => c.Object == "ol_station_b");
        Assert.DoesNotContain(Plan(Script(jump: """
            Act_NagDistLeaving = ol_nag, ol_local, ol_local, 3000
            Act_NagOff = ol_nag
            """)).Companions, c => c.Object == "ol_local");
        Assert.DoesNotContain(Plan(Script(jump: "Act_GiveObjList = ol_local, ol_hunt")).Companions,
            c => c.Object == "ol_local");
        Assert.DoesNotContain(Plan(Script(jump: "Act_RelocateShip = ol_local, 10100, 0, 10100")).Companions,
            c => c.Object == "ol_local");
        Assert.DoesNotContain(Plan(Script(jump: """
            Act_GiveObjList = Player, ol_wingmen
            Act_SpawnShip = ol_local
            """)).Companions, c => c.Object == "ol_local");
        var placed = Plan(Script(jump: "Act_RelocateShip = ol_wing, 50000, 0, 0"));
        Assert.Empty(placed.Companions);
        Assert.Contains("Act_RelocateShip = ol_wing, 50000, 0, 0", WorldLines(placed));
    }

    // Group 5: where a companion starts (rule 9).

    /// <summary>A companion near the start keeps its point. A jump-here trigger gives its offset from the player. The
    /// offset of a Follow order turns with the start heading.</summary>
    [Fact]
    public void ACompanionStartsNearTheJumpHereOrTheFollowPoint()
    {
        var near = Plan(Script(jump: """
            Act_RelocateShip = ol_local, 11000, 0, 10000
            Act_GiveObjList = ol_local, ol_follow
            """));
        Assert.Equal(CompanionPoint.Near, Companion(near, "ol_local").Point);
        Assert.False(Companion(near, "ol_local").Moved);
        Assert.Single(WorldLines(near), l => l.StartsWith("Act_RelocateShip = ol_local", StringComparison.Ordinal));

        var jump = Plan(Script(triggers: """
            [Trigger]
            nickname = t_debug_jump
            system = ol_sys_b
            Cnd_True = no_params
            Act_MovePlayer = 40000, 0, 40000, 100
            Act_SpawnShip = ol_wing, no_ol, 40100, 0, 40200, 0.7071, 0, 0.7071, 0
            Act_ActTrig = t_chosen
            """));
        var wing = Companion(jump, "ol_wing");
        Assert.Equal(CompanionPoint.JumpHere, wing.Point);
        Assert.Equal("t_debug_jump", wing.PointTrigger);
        Near(new Vector3(10100, 0, 10200), wing.Position);
        Assert.Contains("Act_RelocateShip = ol_wing, 10100, 0, 10200, 0.7071, 0, 0.7071, 0", WorldLines(jump));

        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var follow = Plan(Script(jump: "Act_GiveObjList = ol_local, ol_follow"),
            play: new StartSavePlace("ol_sys_b", null, Start, turned));
        Assert.Equal(CompanionPoint.Follow, Companion(follow, "ol_local").Point);
        Near(Start + Vector3.Transform(new Vector3(200, 0, 50), turned), Companion(follow, "ol_local").Position);
    }

    /// <summary>The slots alternate to the left and to the right, and two companions never get the same point. The start
    /// heading turns the slots.</summary>
    [Fact]
    public void TheSlotsAlternateAndNeverRepeat()
    {
        var text = Script(a: "Act_SpawnShip = ol_wing2\nAct_SpawnShip = ol_wing3", records: """
            [MsnShip]
            nickname = ol_wing2
            jumper = true

            [MsnShip]
            nickname = ol_wing3
            jumper = true
            """);
        var plan = Plan(text);
        Assert.Equal(["ol_wing", "ol_wing2", "ol_wing3"], plan.Companions.Select(c => c.Object));
        Near(Start + new Vector3(-300, 0, 0), Companion(plan, "ol_wing").Position);
        Near(Start + new Vector3(300, 0, 0), Companion(plan, "ol_wing2").Position);
        Near(Start + new Vector3(-600, 0, 0), Companion(plan, "ol_wing3").Position);

        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var play = Plan(text, play: new StartSavePlace("ol_sys_b", null, Start, turned));
        Near(Start + Vector3.Transform(new Vector3(-300, 0, 0), turned), Companion(play, "ol_wing").Position);
        Assert.Equal(3, play.Companions.Select(c => c.Position).Distinct().Count());
    }

    /// <summary>A base start puts the companions in front of the dock object of the base. The world waits for the launch.
    /// Without a known dock, the companions keep their points, and the page warns.</summary>
    [Fact]
    public void ABaseStartUsesTheDockOfTheBase()
    {
        var plan = Plan(Script(), choice: StartPlaceChoice.Base, dock: "ol_base_b");
        Assert.Equal("Cnd_PlayerLaunch", plan.Setup[1].Lines[0].Key);
        Near(new Vector3(-19700, 0, -1000), Companion(plan, "ol_wing").Position);
        Assert.Contains(plan.LeftOut, l => l.Object == "ol_guard");

        var dockless = Plan(Script(), choice: StartPlaceChoice.Base, dock: "ol_base_c");
        Assert.Equal(CompanionPoint.Kept, Companion(dockless, "ol_wing").Point);
        Assert.Contains(StartWorld.NoDockWarning("ol_base_c"), dockless.Warnings);
        Assert.DoesNotContain(WorldLines(dockless), l => l.StartsWith("Act_RelocateShip", StringComparison.Ordinal));
    }

    // Group 6: the setup lines of a companion (rule 10).

    /// <summary>A ship gets one Act_RelocateShip after its last line that places it. A formation gets the point and the
    /// heading in its spawn line, and a later move of a member goes. The setup check finds no new error.</summary>
    [Fact]
    public void TheSetupLinesMoveTheCompanions()
    {
        var plan = Plan(Script(jump: """
            Act_RelocateShip = ol_local, 60000, 0, 0
            Act_GiveObjList = ol_local, ol_follow
            Act_SpawnFormation = ol_form_b
            Act_GiveObjList = ol_g1, ol_follow_near
            Act_RelocateShip = ol_g1, 70000, 0, 0
            """, records: """
            [MsnShip]
            nickname = ol_g1
            position = 0, 0, 0

            [MsnFormation]
            nickname = ol_form_b
            position = 12000, 0, 12000
            ship = ol_g1
            """));

        Assert.Equal(
        [
            "Act_SpawnShip = ol_local",
            "Act_RelocateShip = ol_local, 60000, 0, 0",
            "Act_RelocateShip = ol_local, 10200, 0, 10050, 1, 0, 0, 0",
            "Act_GiveObjList = ol_local, ol_follow",
            "Act_SpawnFormation = ol_form_b, 10300, 0, 10000, 1, 0, 0, 0",
            "Act_GiveObjList = ol_g1, ol_follow_near",
        ], WorldLines(plan).SkipWhile(l => l != "Act_SpawnShip = ol_local").ToList());
        Assert.Equal(CompanionPoint.Slot, Companion(plan, "ol_form_b").Point);
        Assert.Equal(StartObjectKind.Formation, Companion(plan, "ol_form_b").Kind);
    }

    // Group 7: the place of the play.

    /// <summary>Without the place of the play, a start at the Scene camera keeps each world line and warns. With it, the
    /// setup makes only the world of the camera system and puts the companions next to the camera. The same inputs give
    /// the same plan.</summary>
    [Fact]
    public void TheSceneCameraUsesThePlaceOfThePlay()
    {
        var page = Plan(Script(), choice: StartPlaceChoice.Camera);
        Assert.Empty(page.LeftOut);
        Assert.Empty(page.Companions);
        Assert.Contains(StartWorld.CameraWarning, page.Warnings);
        Assert.Contains("Act_SpawnShip = ol_guard", WorldLines(page));

        var camera = new StartSavePlace("ol_sys_b", null, new Vector3(-5000, 0, 0), Quaternion.Identity);
        var play = Plan(Script(), choice: StartPlaceChoice.Camera, play: camera);
        Assert.DoesNotContain(StartWorld.CameraWarning, play.Warnings);
        Assert.Contains(play.LeftOut, l => l.Object == "ol_guard");
        Near(new Vector3(-5300, 0, 0), Companion(play, "ol_wing").Position);
        Assert.Equal(play.SetupPatch, Plan(Script(), choice: StartPlaceChoice.Camera, play: camera).SetupPatch);

        // A play in another system than the suggestion leaves out the objects of the suggested system.
        var other = Plan(Script(), play: new StartSavePlace("ol_sys_a", null, Vector3.Zero, Quaternion.Identity));
        Assert.Contains(other.LeftOut, l => l.Object == "ol_local");
        Assert.DoesNotContain(other.LeftOut, l => l.Object == "ol_guard");
    }

    // Group 8: the warnings of the waiting triggers.

    /// <summary>A waiting trigger whose condition names only left-out objects does not run in the play. A waiting trigger
    /// of a label with left-out members can run too soon.</summary>
    [Fact]
    public void TheWaitingTriggersOfLeftOutObjectsGiveWarnings()
    {
        var plan = Plan(Script(jump: "Act_ActTrig = t_guard_dies\nAct_ActTrig = t_friends_die\nAct_ActTrig = t_near_buoy",
            triggers: """
            [Trigger]
            nickname = t_guard_dies
            Cnd_Destroyed = ol_guard

            [Trigger]
            nickname = t_friends_die
            Cnd_Destroyed = ol_friends

            [Trigger]
            nickname = t_near_buoy
            Cnd_DistShip = inside, Player, ol_buoy_a, 1000
            """));

        Assert.Contains(StartWorld.WaitingWarning("t_guard_dies", "ol_guard", "ol_sys_a"), plan.Warnings);
        Assert.Contains(StartWorld.LabelWarning("t_friends_die", "ol_friends", 1), plan.Warnings);
        // An object of a system file of another system stays in the game, so the trigger can run there.
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("t_near_buoy", StringComparison.Ordinal));
    }

    // Group 10: the JSON view and the texts of the Play start page.

    /// <summary>The JSON view has the start system, the left-out objects and the companions with their references and
    /// their sign words.</summary>
    [Fact]
    public void TheJsonViewHasBothLists()
    {
        var plan = Plan(Script(jump: "Act_GiveObjList = Player, ol_wingmen"));
        var view = RunJson.View(plan, 7);
        var json = System.Text.Json.JsonSerializer.Serialize(view, OpenLancer.Data.Editing.Commands.EditJson.Options);

        Assert.Equal("system:ol_sys_b", view.StartSystem);
        var guard = Assert.Single(view.LeftOut, l => l.Object.EndsWith("MsnShip/ol_guard", StringComparison.Ordinal));
        Assert.Equal((StartObjectKind.Ship, "system:ol_sys_a"), (guard.Kind, guard.System));
        Assert.StartsWith("trg:", guard.Trigger);
        Assert.EndsWith("#t_a", guard.Trigger);
        Assert.Contains(view.LeftOut, l => l is { Object: "obj:ol_buoy_a", Kind: StartObjectKind.Fuse, Fuse: "ol_fuse_2" });
        var wing = Assert.Single(view.Companions, c => c.Object.EndsWith("MsnShip/ol_wing", StringComparison.Ordinal));
        Assert.Equal(["player_formation", "jump"], wing.Signs);
        Assert.Equal(("system:ol_sys_a", true), (wing.JumpedFrom, wing.Moved));
        Assert.Contains("\"left_out\"", json);
        Assert.Contains("\"jumped_from\":\"system:ol_sys_a\"", json);
        Assert.Contains("\"point\":\"slot\"", json);
    }

    /// <summary>The page shows the two lists for an approximate start with a start system. Each row tells the object and
    /// why it is in the list.</summary>
    [Fact]
    public void ThePageTellsEachRowOfBothLists()
    {
        var plan = Plan(Script(jump: "Act_LightFuse = ol_unmade, ol_fuse_6", records: """
            [MsnShip]
            nickname = ol_unmade
            """, triggers: """
            [Trigger]
            nickname = t_debug_jump
            system = ol_sys_b
            Cnd_True = no_params
            Act_MovePlayer = 40000, 0, 40000, 100
            Act_SpawnShip = ol_wing, no_ol, 40100, 0, 40200
            Act_ActTrig = t_chosen
            """));
        var model = new PlayStartPageModel(MissionTestData.Path, new PlayStartSetting("t_chosen", "suggested"), plan);

        Assert.True(model.ShowsWorld);
        Assert.Equal("Objects that the setup leaves out (7)", PlayStartPageModel.LeftOutLabel(plan.LeftOut.Count));
        Assert.Equal(
        [
            "ol_guard in ol_sys_a", "ol_station in ol_sys_a", "ol_crate in ol_sys_a", "ol_form_a in ol_sys_a",
            "Fuse ol_fuse_1 on ol_station in ol_sys_a", "Fuse ol_fuse_2 on ol_buoy_a in ol_sys_a",
            "Fuse ol_fuse_6 on ol_unmade. No line makes ol_unmade.",
        ], plan.LeftOut.Select(PlayStartPageModel.LeftOutText));
        Assert.Equal("ol_wing: It jumped with the player from ol_sys_a. Its point comes from t_debug_jump.",
            PlayStartPageModel.CompanionText(Companion(plan, "ol_wing")));
        Assert.Equal("ol_local: Its orders keep it with the player. It flies in the formation of the player. The player " +
                     "must stay near it. It is already near the player.",
            PlayStartPageModel.CompanionText(new StartCompanion("ol_local", StartObjectKind.Ship,
                CompanionSigns.Orders | CompanionSigns.PlayerFormation | CompanionSigns.Escort, "t_jump", CompanionPoint.Near)));

        var camera = new PlayStartPageModel(MissionTestData.Path, new PlayStartSetting("t_chosen", "camera"),
            Plan(Script(), choice: StartPlaceChoice.Camera));
        Assert.False(camera.ShowsWorld);
    }
}
