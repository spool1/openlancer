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

    /// <summary>The world lines of the setup trigger of the world, as "key = values", without the starts of the active
    /// set at its end.</summary>
    private static List<string> WorldLines(StartPlan plan) =>
        plan.Setup.Count < 2
            ? []
            : plan.Setup[1].Lines.Skip(1).Where(l => l.Key != "Act_ActTrig")
                .Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

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

        // A play in another system than the suggestion leaves out the objects of the suggested system. The player goes
        // to the system of the play after the last run, and the jumper goes with the player.
        var other = Plan(Script(), play: new StartSavePlace("ol_sys_a", null, Vector3.Zero, Quaternion.Identity));
        Assert.Contains(other.LeftOut, l => l.Object == "ol_local");
        Assert.DoesNotContain(other.LeftOut, l => l.Object == "ol_guard");
        Assert.Equal(("ol_sys_b", CompanionSigns.Jump), (Companion(other, "ol_wing").JumpedFrom, Companion(other, "ol_wing").Signs));
    }

    // Group 8: the warnings of the waiting triggers.

    /// <summary>A waiting trigger whose condition names only left-out objects does not run in the play. A waiting trigger
    /// of a label with left-out members can run too soon.</summary>
    [Fact]
    public void TheWaitingTriggersOfLeftOutObjectsGiveWarnings()
    {
        var plan = Plan(Script(jump: "Act_ActTrig = t_guard_dies\nAct_ActTrig = t_friends_die\nAct_ActTrig = t_near_buoy\n" +
            "Act_ActTrig = t_spawn_a\nAct_ActTrig = t_arrive_a",
            triggers: """
            [Trigger]
            nickname = t_spawn_a
            system = ol_sys_a
            Cnd_PlayerLaunch = no_params
            Act_SpawnShip = ol_guard

            [Trigger]
            nickname = t_arrive_a
            system = ol_sys_a
            Cnd_SystemEnter = ol_sys_a
            Act_SpawnShip = ol_guard

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
        // A trigger whose system key names another system spawns its objects in the start system, because the game does
        // not read that key. A condition that names a system has its own rule.
        Assert.Contains(StartWorld.OtherSystemWarning("t_spawn_a", "ol_sys_a", "ol_sys_b"), plan.Warnings);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("'t_arrive_a'", StringComparison.Ordinal));
        Assert.Contains(StartWorld.LabelWarning("t_friends_die", "ol_friends", 1), plan.Warnings);
        // An object of a system file of another system stays in the game, so the trigger can run there.
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("t_near_buoy", StringComparison.Ordinal));
    }

    // Group 9: the fixes of the review of 2026-10-28.

    /// <summary>Only the newest list of the player counts for the formation of the player. A newer list without the ship,
    /// or a list that only breaks the formation, makes it no companion.</summary>
    [Fact]
    public void OnlyTheNewestListOfThePlayerCounts()
    {
        const string lists = """
            [ObjList]
            nickname = ol_wingmen_b
            FollowPlayer = ol_shape, ol_wing

            [ObjList]
            nickname = ol_break
            BreakFormation = no_params
            """;
        Assert.Equal(CompanionSigns.PlayerFormation, Companion(Plan(Script(jump: """
            Act_GiveObjList = Player, ol_wingmen_b
            Act_GiveObjList = Player, ol_wingmen
            """, records: lists)), "ol_local").Signs);
        Assert.DoesNotContain(Plan(Script(jump: """
            Act_GiveObjList = Player, ol_wingmen
            Act_GiveObjList = Player, ol_wingmen_b
            """, records: lists)).Companions, c => c.Object == "ol_local");
        Assert.DoesNotContain(Plan(Script(jump: """
            Act_GiveObjList = Player, ol_wingmen
            Act_GiveObjList = Player, ol_break
            """, records: lists)).Companions, c => c.Object == "ol_local");
    }

    /// <summary>No companion starts nearer than the spacing to the player or to another companion. A jump-here point at
    /// the point of the player, and a second jump-here point 20 units from the first, go to the next free slot.</summary>
    [Fact]
    public void TheCompanionsKeepTheirDistance()
    {
        var plan = Plan(Script(a: "Act_SpawnShip = ol_wing2\nAct_SpawnShip = ol_wing3", records: """
            [MsnShip]
            nickname = ol_wing2
            jumper = true

            [MsnShip]
            nickname = ol_wing3
            jumper = true
            """, triggers: """
            [Trigger]
            nickname = t_debug_jump
            system = ol_sys_b
            Cnd_True = no_params
            Act_MovePlayer = 40000, 0, 40000, 100
            Act_SpawnShip = ol_wing, no_ol, 40000, 0, 40000
            Act_SpawnShip = ol_wing2, no_ol, 40100, 0, 40200
            Act_SpawnShip = ol_wing3, no_ol, 40120, 0, 40200
            Act_ActTrig = t_chosen
            """));

        Assert.Equal(CompanionPoint.Slot, Companion(plan, "ol_wing").Point);
        Near(Start + new Vector3(-300, 0, 0), Companion(plan, "ol_wing").Position);
        Assert.Equal(CompanionPoint.JumpHere, Companion(plan, "ol_wing2").Point);
        Near(Start + new Vector3(100, 0, 200), Companion(plan, "ol_wing2").Position);
        Assert.Equal(CompanionPoint.Slot, Companion(plan, "ol_wing3").Point);
        Near(Start + new Vector3(300, 0, 0), Companion(plan, "ol_wing3").Position);
        var points = plan.Companions.Select(c => c.Position!.Value).Append(Start).ToList();
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
                Assert.True(Vector3.Distance(points[i], points[j]) >= StartWorld.Spacing, $"{points[i]} and {points[j]}");
        }
    }

    /// <summary>Two companions with the same Follow offset do not share a point. The second takes a slot.</summary>
    [Fact]
    public void TwoCompanionsWithOneFollowOffsetDoNotShareAPoint()
    {
        var plan = Plan(Script(jump: """
            Act_GiveObjList = ol_local, ol_follow
            Act_SpawnShip = ol_escort
            Act_GiveObjList = ol_escort, ol_follow
            """, records: """
            [MsnShip]
            nickname = ol_escort
            position = 40000, 0, 40000
            """));

        Assert.Equal(CompanionPoint.Follow, Companion(plan, "ol_local").Point);
        Assert.Equal(CompanionPoint.Slot, Companion(plan, "ol_escort").Point);
        Assert.NotEqual(Companion(plan, "ol_local").Position, Companion(plan, "ol_escort").Position);
    }

    /// <summary>A jump-here point more than 5,000 units from the point of the player of its trigger does not count. The
    /// companion takes a slot.</summary>
    [Fact]
    public void AFarJumpHerePointDoesNotCount()
    {
        var plan = Plan(Script(triggers: """
            [Trigger]
            nickname = t_debug_jump
            system = ol_sys_b
            Cnd_True = no_params
            Act_MovePlayer = 40000, 0, 40000, 100
            Act_SpawnShip = ol_wing, no_ol, 46000, 0, 40000
            Act_ActTrig = t_chosen
            """));

        Assert.Equal(CompanionPoint.Slot, Companion(plan, "ol_wing").Point);
        Assert.Null(Companion(plan, "ol_wing").PointTrigger);
    }

    /// <summary>A solar always spawns in the system of the player, also with a <c>system</c> key in its record.</summary>
    [Fact]
    public void ASolarSpawnsInTheSystemOfThePlayer()
    {
        var plan = Plan(Script(jump: "Act_SpawnSolar = ol_tower", records: """
            [MsnSolar]
            nickname = ol_tower
            system = ol_sys_a
            position = 12000, 0, 12000
            """));

        Assert.DoesNotContain(plan.LeftOut, l => l.Object == "ol_tower");
        Assert.Contains("Act_SpawnSolar = ol_tower", WorldLines(plan));
    }

    /// <summary>At a jump of the player, a jumper goes with the player also from a third system. Retail takes each
    /// jumper out of its system at the jump.</summary>
    [Fact]
    public void AJumperOfAThirdSystemJumpsWithThePlayer()
    {
        var plan = StartPlan.Make(Index(Script(a: "Act_SpawnShip = ol_far_wing", records: """
            [MsnShip]
            nickname = ol_far_wing
            system = ol_sys_c
            jumper = true
            """), ThreeSystems()), StartPlanTests.Slot, MissionStartPoint.At("t_chosen"));

        Assert.True(plan.CanStart, plan.Reason);
        var wing = Companion(plan, "ol_far_wing");
        Assert.Equal(("ol_sys_c", CompanionSigns.Jump), (wing.JumpedFrom, wing.Signs));
    }

    /// <summary>Retail cancels the orders of each jumper that comes with the player when it arrives. So an order line
    /// that a jumper got before a jump that a condition shows goes, and its orders are no sign. An order line after the
    /// arrival stays. A line that also gives orders to a ship that did not jump stays. A jump that only system keys show
    /// cancels nothing, because a system key can name the wrong system.</summary>
    [Fact]
    public void AJumperLosesTheOrdersOfBeforeTheJump()
    {
        const string old = "Act_GiveObjList = ol_wing, ol_follow";
        var before = Plan(Script(a: old));
        Assert.DoesNotContain(old, WorldLines(before));
        var wing = Companion(before, "ol_wing");
        Assert.Equal((CompanionSigns.Jump, CompanionPoint.Slot), (wing.Signs, wing.Point));

        var after = Plan(Script(a: old, jump: "Act_GiveObjList = ol_wing, ol_follow_near"));
        Assert.DoesNotContain(old, WorldLines(after));
        Assert.Contains("Act_GiveObjList = ol_wing, ol_follow_near", WorldLines(after));
        Assert.Equal(CompanionSigns.Orders | CompanionSigns.Jump, Companion(after, "ol_wing").Signs);

        Assert.Contains("Act_GiveObjList = ol_friends, ol_follow",
            WorldLines(Plan(Script(a: "Act_GiveObjList = ol_friends, ol_follow"))));

        var keys = Plan(Script(a: old).Replace("Cnd_SystemEnter = ol_sys_b", "Cnd_Timer = 1"));
        Assert.Contains(old, WorldLines(keys));
        Assert.Equal(CompanionSigns.Orders | CompanionSigns.Jump, Companion(keys, "ol_wing").Signs);
    }

    /// <summary>A trigger that waits for the arrival of the jumpers (Cnd_NPCSystemEnter) gives them new orders in the
    /// game after the cancel. The play makes the jumpers in the start system, so the trigger does not run. The setup
    /// gives its orders to each jumper without orders after the arrival, after the world lines. That is true for a
    /// waiting trigger, for the chosen trigger and for a trigger that a trigger of the start starts. A warning names the
    /// trigger, but for the chosen trigger the place of the start warns. The setup does not give orders to a ship that
    /// did not jump, to a jumper with orders after the arrival or for an arrival in another system.</summary>
    [Fact]
    public void AnArrivalTriggerOfTheStartGivesItsOrdersToTheJumpers()
    {
        const string old = "Act_GiveObjList = ol_wing, ol_follow";
        const string orders = "Act_GiveObjList = ol_wing, ol_follow_near";
        string Arrival(string condition) => $"""
            [Trigger]
            nickname = t_init
            system = ol_sys_b
            Cnd_True = no_params
            Act_ActTrig = t_arrive

            [Trigger]
            nickname = t_arrive
            system = ol_sys_b
            Cnd_NPCSystemEnter = {condition}
            Act_GiveObjList = ol_wing, ol_follow_near
            Act_GiveObjList = ol_guard, ol_patrol
            """;
        var warning = StartWorld.ArrivalWarning("t_arrive", "ol_wing", "ol_sys_b", "ol_wing");
        foreach (var (start, jump) in new[]
                 {
                     ("t_chosen", "Act_ActTrig = t_arrive"), ("t_arrive", "Act_ActTrig = t_arrive"),
                     ("t_init", "Act_ActTrig = t_init"),
                 })
        {
            var plan = Plan(Script(a: old, jump: jump, triggers: Arrival("ol_sys_b, ol_wing, Player")), start);
            var world = WorldLines(plan);
            Assert.Equal(orders, world[^1]);
            Assert.DoesNotContain(old, world);
            Assert.DoesNotContain(world, l => l.Contains("ol_patrol", StringComparison.Ordinal));
            Assert.Equal(CompanionSigns.Orders | CompanionSigns.Jump, Companion(plan, "ol_wing").Signs);
            // The place of the start warns about the chosen trigger.
            Assert.Equal(start != "t_arrive", plan.Warnings.Contains(warning));
            Assert.Equal(start == "t_arrive", plan.Warnings.Contains(StartPlaces.ArrivedWarning("t_arrive", "ol_sys_b")));
        }

        var later = Plan(Script(a: old, jump: "Act_GiveObjList = ol_wing, ol_hunt\nAct_ActTrig = t_arrive",
            triggers: Arrival("ANY, ol_wing")));
        Assert.Equal("Act_GiveObjList = ol_wing, ol_hunt", Assert.Single(WorldLines(later), l => l.Contains("ol_wing, ol_")));
        Assert.Contains(StartWorld.ArrivalWarning("t_arrive", "ol_wing", "ol_sys_b", null), later.Warnings);

        foreach (var condition in new[] { "ol_sys_a, ol_wing", "ol_sys_b, ol_wing, ol_local" })
        {
            var plan = Plan(Script(a: old, jump: "Act_ActTrig = t_arrive", triggers: Arrival(condition)));
            Assert.DoesNotContain(orders, WorldLines(plan));
            Assert.DoesNotContain(plan.Warnings, w => w.StartsWith("Trigger 't_arrive'", StringComparison.Ordinal));
            Assert.Equal(CompanionSigns.Jump, Companion(plan, "ol_wing").Signs);
        }
    }

    /// <summary>Act_SpawnShipRel with a new nickname makes a ship with that nickname. A later line names the ship by
    /// it. No item of the script has that nickname, so the setup does not move the ship.</summary>
    [Fact]
    public void AShipOfSpawnShipRelHasItsNewName()
    {
        var plan = Plan(Script(jump: """
            Act_SpawnShipRel = ol_local, no_ol, ol_buoy_b, POINT, 0, 0, 100, 0, ol_renamed
            Act_GiveObjList = ol_renamed, ol_follow
            """));

        var renamed = Companion(plan, "ol_renamed");
        Assert.Equal((CompanionSigns.Orders, CompanionPoint.Kept), (renamed.Signs, renamed.Point));
        Assert.Contains("Act_GiveObjList = ol_renamed, ol_follow", WorldLines(plan));
        Assert.DoesNotContain(WorldLines(plan), l => l.StartsWith("Act_RelocateShip = ol_renamed", StringComparison.Ordinal));
    }

    /// <summary>A base whose dock is a mission solar of the script: the companions start in front of the solar, at the
    /// point of its record, as the game launches the player from it.</summary>
    [Fact]
    public void AMissionSolarCanBeTheDockOfABase()
    {
        var plan = Plan(Script(records: """
            [MsnSolar]
            nickname = ol_depot
            system = ol_sys_b
            base = ol_base_c
            position = 5000, 0, 0
            orientation = 1, 0, 0, 0
            """), choice: StartPlaceChoice.Base, dock: "ol_base_c");

        Assert.DoesNotContain(StartWorld.NoDockWarning("ol_base_c"), plan.Warnings);
        Assert.Equal(CompanionPoint.Slot, Companion(plan, "ol_wing").Point);
        Near(new Vector3(4700, 0, 1000), Companion(plan, "ol_wing").Position);
    }

    /// <summary>A line on an object that no kept line makes goes: a ship without a spawn, a label without a made member,
    /// and a formation whose spawn the path destroyed. A line on an object of a system file of the start system
    /// stays.</summary>
    [Fact]
    public void ALineOnAnObjectThatNoLineMakesGoes()
    {
        var plan = Plan(Script(jump: """
            Act_SpawnFormation = ol_form_b
            Act_Destroy = ol_form_b
            Act_MarkObj = ol_unmade, 1
            Act_Cloak = ol_nobody, false
            Act_GiveObjList = ol_form_b, ol_patrol
            Act_MarkObj = ol_g1, 1
            Act_SetVibeLbl = ol_locals, Player, REP_FRIEND_THRESHOLD
            Act_MarkObj = ol_buoy_b, 1
            """, records: """
            [MsnShip]
            nickname = ol_unmade
            label = ol_nobody

            [MsnShip]
            nickname = ol_g1
            position = 0, 0, 0

            [MsnFormation]
            nickname = ol_form_b
            position = 12000, 0, 12000
            ship = ol_g1
            """));
        var lines = WorldLines(plan);

        Assert.DoesNotContain(lines, l => l.Contains("ol_unmade") || l.Contains("ol_nobody") || l.Contains("ol_form_b") ||
                                          l.Contains("ol_g1"));
        Assert.Contains("Act_SetVibeLbl = ol_locals, Player, REP_FRIEND_THRESHOLD", lines);
        Assert.Contains("Act_MarkObj = ol_buoy_b, 1", lines);
    }

    /// <summary>A trigger of the start that spawns an object of the setup again makes a new object. The setup leaves out
    /// the old object, which is then no companion, and the page names the trigger. A trigger that waits for the object
    /// gives no warning. An Act_Destroy before the spawn keeps the old object.</summary>
    [Fact]
    public void ATriggerOfTheStartThatSpawnsAgainReplacesTheObject()
    {
        const string again = """
            Act_ActTrig = t_again
            Act_ActTrig = t_local_dies

            [Trigger]
            nickname = t_again
            Cnd_True = no_params
            {LINES}

            [Trigger]
            nickname = t_local_dies
            Cnd_Destroyed = ol_local
            """;
        var plan = Plan(Script(jump: "Act_GiveObjList = ol_local, ol_follow",
            triggers: again.Replace("{LINES}", "Act_SpawnShip = ol_local")));
        var row = Assert.Single(plan.LeftOut, l => l.Object == "ol_local");
        Assert.Equal(("t_again", "ol_sys_b"), (row.MadeAgainBy, row.System));
        Assert.Equal("ol_local. Trigger t_again makes it again at the start.", PlayStartPageModel.LeftOutText(row));
        Assert.EndsWith("#t_again", Assert.Single(RunJson.View(plan, 7).LeftOut,
            l => l.Object.EndsWith("MsnShip/ol_local", StringComparison.Ordinal)).MadeAgainBy);
        Assert.DoesNotContain(plan.Companions, c => c.Object == "ol_local");
        Assert.DoesNotContain(WorldLines(plan), l => l.Contains("ol_local"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("t_local_dies", StringComparison.Ordinal));

        var destroyed = Plan(Script(jump: "Act_GiveObjList = ol_local, ol_follow",
            triggers: again.Replace("{LINES}", "Act_Destroy = ol_local\nAct_SpawnShip = ol_local")));
        Assert.DoesNotContain(destroyed.LeftOut, l => l.Object == "ol_local");
        Assert.Contains("Act_SpawnShip = ol_local", WorldLines(destroyed));
        Assert.Contains(destroyed.Companions, c => c.Object == "ol_local");
    }

    /// <summary>A trigger of the start that destroys an object of the setup, and does not spawn it again, makes it leave
    /// soon after the start. So the object is no companion and keeps its point, but the setup makes it. The destroy then
    /// runs in the play, so a trigger that waits for the end of the object still runs (fix of the review of
    /// 2026-10-28).</summary>
    [Fact]
    public void AnObjectThatATriggerOfTheStartDestroysIsNoCompanion()
    {
        var plan = Plan(Script(jump: "Act_GiveObjList = ol_local, ol_follow", triggers: """
            Act_ActTrig = t_end
            Act_ActTrig = t_local_dies

            [Trigger]
            nickname = t_end
            Cnd_Timer = 2
            Act_Destroy = ol_local, SILENT

            [Trigger]
            nickname = t_local_dies
            Cnd_Destroyed = ol_local
            """));

        Assert.DoesNotContain(plan.Companions, c => c.Object == "ol_local");
        Assert.DoesNotContain(plan.LeftOut, l => l.Object == "ol_local");
        Assert.Equal(["Act_SpawnShip = ol_local", "Act_GiveObjList = ol_local, ol_follow"],
            WorldLines(plan).Where(l => l.Contains("ol_local")));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("t_local_dies", StringComparison.Ordinal));
    }

    /// <summary>An old timer of the start that leads to a trigger that ran in the replay does not make its objects
    /// again. The first run made them, so the setup keeps them. A trigger that did not run in the replay still makes
    /// its objects again (fix of the review of 2026-10-28).</summary>
    [Theory]
    [InlineData("Act_ActTrig = t_help\nAct_ActTrig = t_clock", false)]
    [InlineData("Act_SpawnShip = ol_help\nAct_ActTrig = t_clock", true)]
    public void AnOldTimerDoesNotMakeTheObjectsOfARunAgain(string jump, bool again)
    {
        var plan = Plan(Script(jump: jump, records: """
            [MsnShip]
            nickname = ol_help
            position = 12000, 0, 12000
            """, triggers: """

            [Trigger]
            nickname = t_help
            Cnd_True = no_params
            Act_SpawnShip = ol_help

            [Trigger]
            nickname = t_clock
            Cnd_Timer = 1
            Act_ActTrig = t_help
            """));

        Assert.Contains("t_clock", plan.ActiveSet);
        Assert.Equal(again, plan.LeftOut.Any(l => l.Object == "ol_help" && l.MadeAgainBy == "t_help"));
        Assert.Equal(!again, WorldLines(plan).Contains("Act_SpawnShip = ol_help"));
    }

    /// <summary>A start in space spawns the player with a launch, so a launch trigger of the start spawns again, as at a
    /// base start. The launch ends only after an undock, so a trigger that waits for the end of the launch spawns again
    /// only at a base start (fix of the review of 2026-10-28).</summary>
    [Theory]
    [InlineData("Cnd_PlayerLaunch = no_params", null, true)]
    [InlineData("Cnd_PlayerLaunch = no_params", "ol_base_b", true)]
    [InlineData("Cnd_LaunchComplete = Player", null, false)]
    [InlineData("Cnd_LaunchComplete = Player", "ol_base_b", true)]
    public void TheEndOfALaunchSpawnsAgainOnlyAtABaseStart(string condition, string? dock, bool again)
    {
        var text = Script(jump: "Act_GiveObjList = ol_local, ol_follow", triggers: $"""
            Act_ActTrig = t_again

            [Trigger]
            nickname = t_again
            {condition}
            Act_SpawnShip = ol_local
            """);
        var plan = dock == null ? Plan(text) : Plan(text, choice: StartPlaceChoice.Base, dock: dock);

        Assert.Equal(again, plan.LeftOut.Any(l => l.Object == "ol_local" && l.MadeAgainBy == "t_again"));
        Assert.Equal(!again, plan.Companions.Any(c => c.Object == "ol_local"));
    }

    /// <summary>The setup trigger of the world waits for the launch of the player, also for a start in space. The game
    /// sends the launch when the start spawns the player, before the entry to space. So a chosen trigger that waits for
    /// the launch starts after the world lines and still runs at the spawn (fix of the review of 2026-10-28).</summary>
    [Fact]
    public void AChosenLaunchTriggerOfASpaceStartStartsAfterTheWorld()
    {
        var plan = Plan(Script(triggers: """
            Act_ActTrig = t_launch

            [Trigger]
            nickname = t_launch
            Cnd_PlayerLaunch = no_params
            Act_GiveObjList = Player, ol_wingmen
            """), "t_launch");

        Assert.Equal(StartPlaceKind.Space, plan.Place.Kind);
        Assert.Equal(["Cnd_True = no_params", "Act_SetRep = Player, ol_faction_a, -0.9", "Act_ActTrig = ol_play_world"],
            SetupLines(plan));
        Assert.Equal(("Cnd_PlayerLaunch", "no_params"), (plan.Setup[1].Lines[0].Key, plan.Setup[1].Lines[0].Values[0]));
        Assert.Equal(("Act_ActTrig", "t_launch"), (plan.Setup[1].Lines[^1].Key, plan.Setup[1].Lines[^1].Values[0]));
    }

    /// <summary>The system of the player at the first run of a restore trigger at the root comes from its place line:
    /// here the Cnd_SpaceEnter system of its second stage, not the system of the story before it. A Cnd_BaseExit run
    /// takes the system of its base.</summary>
    [Fact]
    public void ARestoreRootAndABaseExitGiveTheSystemOfTheirRun()
    {
        const string text = """
            [MsnShip]
            nickname = ol_guard
            position = 5000, 0, 5000

            [MsnShip]
            nickname = ol_local
            position = 7000, 0, 7000

            [Trigger]
            nickname = t_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = t_a

            [Trigger]
            nickname = t_a
            system = ol_sys_a
            Cnd_SpaceEnter = ol_sys_a
            Act_Save = t_restore, 1

            [Trigger]
            nickname = t_restore
            Cnd_PlayerLaunch = no_params
            Act_SetInitialPlayerPos = 100, 0, 100, 1, 0, 0, 0
            Act_SpawnShip = ol_guard
            Act_ActTrig = t_stage2

            [Trigger]
            nickname = t_stage2
            Cnd_SpaceEnter = ol_sys_b
            Act_ActTrig = t_exit

            [Trigger]
            nickname = t_exit
            Cnd_BaseExit = ol_base_a
            Act_SpawnShip = ol_local
            Act_ActTrig = t_chosen

            [Trigger]
            nickname = t_chosen
            Cnd_Timer = 5
            """;
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["ol_sys_a", "ol_sys_b"],
            [MissionValueKind.Base] = ["ol_base_a", "ol_base_b"],
        }, null, new Dictionary<string, string> { ["ol_base_a"] = "ol_sys_a", ["ol_base_b"] = "ol_sys_b" });
        var index = Index(text, world);
        var replay = StartReplay.Run(index, MissionStartPoint.At("t_chosen"));
        var facts = StartPlaces.World(index, replay);
        Assert.Equal(["t_restore ol_sys_b", "t_stage2 ol_sys_b", "t_exit ol_sys_a"], replay.Changes
            .Select((change, at) => (change, at)).Where(p => p.change.Kind == MachineChangeKind.Ran)
            .Select(p => $"{p.change.Trigger.Nickname} {facts.Systems[p.at] ?? "-"}"));

        var plan = StartPlan.Make(index, StartPlanTests.Slot, MissionStartPoint.At("t_chosen"),
            play: new StartSavePlace("ol_sys_a", null, new Vector3(100, 0, 100), Quaternion.Identity));
        Assert.True(plan.CanStart, plan.Reason);
        Assert.Equal(["ol_guard ol_sys_b"], plan.LeftOut.Select(l => $"{l.Object} {l.System}"));
        Assert.Contains("Act_SpawnShip = ol_local", WorldLines(plan));
    }

    /// <summary>The triggers of the active set start after the world lines. A space start starts each of them at the end
    /// of the world trigger. A base start starts there only a trigger that runs at once and has a world line, or starts
    /// such a trigger. The other triggers of a base start start at the load, for example a trigger of a room or a
    /// trigger that runs at once with only a comm line.</summary>
    [Fact]
    public void TheActiveSetStartsAfterTheWorld()
    {
        var text = Script(jump: "Act_ActTrig = t_room\nAct_ActTrig = t_watch", triggers: """
            Act_ActTrig = t_next
            Act_ActTrig = t_talk

            [Trigger]
            nickname = t_room
            Cnd_LocEnter = Bar, ol_base_b
            Act_SendComm = ol_local, Player, ol_line_1

            [Trigger]
            nickname = t_watch
            Cnd_Timer = 9
            Act_SendComm = ol_local, Player, ol_line_3

            [Trigger]
            nickname = t_next
            Cnd_True = no_params
            Act_ActTrig = t_orders

            [Trigger]
            nickname = t_orders
            Cnd_True = no_params
            Act_GiveObjList = ol_local, ol_patrol

            [Trigger]
            nickname = t_talk
            Cnd_True = no_params
            Act_SendComm = ol_local, Player, ol_line_2
            """);
        string Starts(StartPlan plan, int at) => string.Join(" ", plan.Setup[at].Lines.Where(l => l.Key == "Act_ActTrig")
            .Select(l => l.Values[0]));

        var space = Plan(text, "t_next");
        Assert.Equal("ol_play_world", Starts(space, 0));
        Assert.Equal(string.Join(" ", space.ActiveSet), Starts(space, 1));
        Assert.Contains("t_room", space.ActiveSet);
        Assert.Equal("Act_ActTrig", space.Setup[1].Lines[^1].Key);

        var docked = Plan(text, "t_next", StartPlaceChoice.Base, "ol_base_b");
        Assert.Equal("ol_play_world " + string.Join(" ", docked.ActiveSet.Where(t => t != "t_next")), Starts(docked, 0));
        Assert.Equal("t_next", Starts(docked, 1));

        var talk = Plan(text, "t_talk", StartPlaceChoice.Base, "ol_base_b");
        Assert.Contains("t_talk", Starts(talk, 0).Split(' '));
        Assert.DoesNotContain("t_talk", Starts(talk, 1).Split(' '));
    }

    /// <summary>The world of <see cref="World"/> with a third system.</summary>
    private static MissionWorld ThreeSystems() =>
        new(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["ol_sys_a", "ol_sys_b", "ol_sys_c"],
            [MissionValueKind.Base] = ["ol_base_b", "ol_base_c"],
            [MissionValueKind.SystemObject] = Objects.Select(o => o.Nickname).ToList(),
        }, null, new Dictionary<string, string> { ["ol_base_b"] = "ol_sys_b", ["ol_base_c"] = "ol_sys_b" }, Objects);

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
