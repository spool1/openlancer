using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The rules of the suggested start place (section 5.3, step 7 of the M6c design, with the fix of 2026-10-27 and its
/// reviews). The suggestion is where the player is at the chosen trigger in the retail game. The scripts are synthetic,
/// and all names are invented.
/// </summary>
public sealed class StartPlacesTests
{
    /// <summary>
    /// A story in the shape of the report of 2026-10-27. It has a bar visit at a base, the accept, and a launch that
    /// waits for Cnd_SpaceEnter without a system. Then a step in space spawns a ship, a point waits near a gate, and a jump
    /// goes into a second system. A watcher waits for the end of the launch. The bar and the launch triggers have no
    /// system hint.
    /// </summary>
    private const string Story = """
        [MsnShip]
        nickname = ol_king
        system = ol_sys_a
        position = 500, 0, 600

        [MsnShip]
        nickname = ol_juni
        system = ol_sys_b
        position = -500, 0, -600

        [Trigger]
        nickname = p_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = p_bar

        [Trigger]
        nickname = p_bar
        Cnd_LocEnter = ol_bar, ol_base_a
        Act_ActTrig = p_accept

        [Trigger]
        nickname = p_accept
        Cnd_MsnResponse = accept
        Act_ActTrig = p_exit
        Act_ActTrig = p_launch

        [Trigger]
        nickname = p_exit
        Cnd_BaseExit = ol_base_a

        [Trigger]
        nickname = p_launch
        Cnd_SpaceEnter = no_params
        Act_ActTrig = p_init
        Act_ActTrig = p_launched

        [Trigger]
        nickname = p_launched
        Cnd_LaunchComplete = Player

        [Trigger]
        nickname = p_init
        system = ol_sys_a
        Cnd_True = no_params
        Act_SpawnShip = ol_king
        Act_ActTrig = p_near

        [Trigger]
        nickname = p_near
        system = ol_sys_a
        Cnd_DistVec = inside, Player, 1000, 0, 2000, 500
        Act_ActTrig = p_lane

        [Trigger]
        nickname = p_lane
        system = ol_sys_a
        Cnd_TLExited = Player, ol_ring_1
        Act_ActTrig = p_jump

        [Trigger]
        nickname = p_jump
        system = ol_sys_b
        Cnd_NPCSystemEnter = ol_sys_b, ol_king
        Act_ActTrig = p_after

        [Trigger]
        nickname = p_after
        system = ol_sys_b
        Cnd_Timer = 5
        """;

    private static StartPlan Plan(string text, string trigger, MissionWorld? world = null,
        StartPlaceChoice choice = StartPlaceChoice.Suggested, string? dock = null) =>
        StartPlan.Make(Index(text, world), StartPlanTests.Slot, MissionStartPoint.At(trigger), choice, dock);

    private static StartPlace Place(string text, string trigger, MissionWorld? world = null) =>
        Plan(text, trigger, world).Place;

    /// <summary>A world that knows the three test systems and the system of each base.</summary>
    private static MissionWorld World(params (string Base, string System)[] bases) => World([], bases);

    /// <summary>A world that knows the three test systems, the system of each base and some objects of the systems.</summary>
    private static MissionWorld World(IReadOnlyList<WorldObject> objects, params (string Base, string System)[] bases) =>
        new(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["ol_sys_a", "ol_sys_b", "ol_sys_c"],
            [MissionValueKind.Base] = bases.Select(b => b.Base).ToList(),
        }, null, bases.ToDictionary(b => b.Base, b => b.System), objects);

    /// <summary>The jump gate of the second system that leads back to the first system. Its heading turns +z to +x.</summary>
    private static readonly WorldObject GateToA = new("ol_gate_b_to_a", "ol_sys_b", new Vector3(20000, 0, 30000),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), "ol_sys_a");

    /// <summary>The point of a start 1,000 units in front of <see cref="GateToA"/>.</summary>
    private static Vector3 AtGateToA =>
        GateToA.Position + Vector3.Transform(new Vector3(0, 0, StartPlaces.Offset), GateToA.Rotation);

    private static void Space(StartPlace place, string system, Vector3 point, string source)
    {
        Assert.Equal(StartPlaceKind.Space, place.Kind);
        Assert.Equal(system, place.System);
        Assert.Equal(point, place.Position);
        Assert.Equal(source, place.Source);
    }

    private static void Docked(StartPlace place, string dock, string source)
    {
        Assert.Equal(StartPlaceKind.Base, place.Kind);
        Assert.Equal(dock, place.Base);
        Assert.Equal(source, place.Source);
    }

    // Rule 3: the newest fact decides the kind. A base fact before a later launch is stale.

    [Fact]
    public void TheBarVisitAndTheAcceptStartDockedAtTheBase()
    {
        Docked(Place(Story, "p_bar"), "ol_base_a", "Cnd_LocEnter of p_bar.");
        Docked(Place(Story, "p_accept"), "ol_base_a", "Cnd_LocEnter of p_bar.");
    }

    /// <summary>The bug of 2026-10-27: after the launch, the start is in space and not at the bar of the base.</summary>
    [Fact]
    public void ATriggerAfterTheLaunchStartsInSpaceAndNotAtTheBarBase()
    {
        // The launch names no system, so the system hint of the step gives it. The step spawns the first ship.
        Space(Place(Story, "p_init"), "ol_sys_a", new Vector3(500, 0, 600 + StartPlaces.Offset),
            "Cnd_SpaceEnter of p_launch. The system comes from the system hint of p_init. The point is 1,000 units from " +
            "the spawn of ol_king.");
        // A Cnd_DistVec line that waits for the player inside its sphere gives its point.
        Space(Place(Story, "p_lane"), "ol_sys_a", new Vector3(1000, 0, 2000 + StartPlaces.Offset),
            "Cnd_DistVec of p_near. The system comes from the system hint of p_near.");
    }

    /// <summary>Review fix: the system of the base of the launch comes before a system hint.</summary>
    [Fact]
    public void TheBaseOfTheLaunchGivesTheSystemWhenTheWorldKnowsIt()
    {
        Space(Place(Story, "p_lane", World(("ol_base_a", "ol_sys_c"))), "ol_sys_c",
            new Vector3(1000, 0, 2000 + StartPlaces.Offset),
            "Cnd_DistVec of p_near. The system comes from the base of Cnd_LocEnter of p_bar.");
        // The spawn of the step is in the system of its hint, so it gives no point in that system. The start is at the
        // base of the launch.
        Docked(Place(Story, "p_init", World(("ol_base_a", "ol_sys_c"))), "ol_base_a",
            "Cnd_LocEnter of p_bar. No line of the path gives a point in ol_sys_c after Cnd_SpaceEnter of p_launch.");
    }

    /// <summary>The point fallback after the fact itself: the first point of the segment.</summary>
    [Fact]
    public void ASpaceFactWithoutAPointTakesThePointOfAnOlderFactOfTheSegment()
    {
        Space(Place(Story, "p_jump"), "ol_sys_a", new Vector3(1000, 0, 2000 + StartPlaces.Offset),
            "Cnd_TLExited of p_lane. The system comes from the system hint of p_lane. The point comes from Cnd_DistVec " +
            "of p_near.");
    }

    // Rule 2: the facts of the chosen trigger.

    /// <summary>A trigger that waits at the base for the launch starts docked, because the launch fires it.</summary>
    [Fact]
    public void ATriggerThatWaitsAtTheBaseForTheLaunchStartsDocked()
    {
        Docked(Place(Story, "p_launch"), "ol_base_a", "Cnd_LocEnter of p_bar.");
        Docked(Place(Story, "p_exit"), "ol_base_a", "Cnd_BaseExit of p_exit.");
        // Review fix: a Cnd_SpaceEnter with a system waits at the base too.
        Docked(Place(Story.Replace("Cnd_SpaceEnter = no_params", "Cnd_SpaceEnter = ol_sys_a"), "p_launch"),
            "ol_base_a", "Cnd_LocEnter of p_bar.");
    }

    /// <summary>Review fix: a start in space never ends a launch, so a chosen Cnd_LaunchComplete starts docked at the base
    /// of the launch.</summary>
    [Fact]
    public void ATriggerThatWaitsForTheEndOfTheLaunchStartsDockedAtTheBaseOfTheLaunch()
    {
        var plan = Plan(Story, "p_launched");

        Docked(plan.Place, "ol_base_a",
            "Cnd_LocEnter of p_bar. Trigger p_launched waits for the end of a launch, so the play starts docked.");
        Assert.DoesNotContain(StartPlaces.LaunchWatcherWarning("p_launched"), plan.Warnings);
    }

    /// <summary>Without a base fact, the start is in space, and the page warns that the trigger does not run.</summary>
    [Fact]
    public void ATriggerThatWaitsForTheEndOfTheLaunchWithoutABaseWarns()
    {
        var text = Story.Replace("Cnd_LocEnter = ol_bar, ol_base_a", "Cnd_Timer = 1");

        var plan = Plan(text, "p_launched");

        Assert.NotEqual(StartPlaceKind.Base, plan.Place.Kind);
        Assert.Contains(StartPlaces.LaunchWatcherWarning("p_launched"), plan.Warnings);
    }

    /// <summary>Review fix: after a start in space, the triggers that wait for the end of the launch do not run. That is
    /// also true after a later jump. The page warns about them.</summary>
    [Fact]
    public void AStartInSpaceWarnsAboutTheTriggersThatWaitForTheEndOfTheLaunch()
    {
        var warning = StartPlaces.LaunchWatcherWarning("p_launched");

        Assert.Contains(warning, Plan(Story, "p_lane").Warnings);
        Assert.Contains(warning, Plan(Story, "p_after").Warnings);
        // A base start launches again, so the trigger runs.
        Assert.DoesNotContain(warning, Plan(Story, "p_lane", choice: StartPlaceChoice.Base, dock: "ol_base_a").Warnings);
        Assert.DoesNotContain(warning, Plan(Story, "p_accept").Warnings);
        Assert.Empty(SteLint.Problems(warning));
    }

    /// <summary>Rule 2 and review fix: a chosen trigger that waits for an arrival needs the jump, so the start is before
    /// the jump. Its own place line and its system hint name the next system, so they do not count.</summary>
    [Fact]
    public void ATriggerThatWaitsForAnArrivalStartsBeforeTheJump()
    {
        var text = Story.Replace("Cnd_NPCSystemEnter = ol_sys_b, ol_king\n", "Cnd_JumpInComplete = ol_sys_b\nAct_MovePlayer = 9, 9, 9, 100\n");

        var arrival = Place(Story, "p_jump");
        var jumpIn = Place(text, "p_jump");
        var systemEnter = Place(Story.Replace("Cnd_NPCSystemEnter = ol_sys_b, ol_king", "Cnd_SystemEnter = ol_sys_b"),
            "p_jump");

        Assert.Equal(("ol_sys_a", new Vector3(1000, 0, 3000)), (arrival.System, arrival.Position));
        Assert.Equal(("ol_sys_a", new Vector3(1000, 0, 3000)), (jumpIn.System, jumpIn.Position));
        Assert.Equal(("ol_sys_a", new Vector3(1000, 0, 3000)), (systemEnter.System, systemEnter.Position));
    }

    /// <summary>Review fix: the path already made the jump that the chosen trigger waits for. The replay ran that
    /// arrival and the triggers after it, so the start is after the jump, in the next system. The page warns that
    /// OpenLancer runs the chosen trigger only at the next jump.</summary>
    [Fact]
    public void AnArrivalOfTheSameJumpOnThePathPutsTheStartAfterTheJump()
    {
        var text = Story.Replace("""
            Act_ActTrig = p_after

            [Trigger]
            nickname = p_after
            system = ol_sys_b
            Cnd_Timer = 5
            """, """
            Act_ActTrig = p_after

            [Trigger]
            nickname = p_after
            system = ol_sys_b
            Cnd_JumpInComplete = ol_sys_b
            """);

        var plan = Plan(text, "p_after");
        var atGate = Place(text, "p_after", World([GateToA]));

        Assert.Equal((StartPlaceKind.Camera, "ol_sys_b"), (plan.Place.Kind, plan.Place.System));
        Assert.Contains(StartPlaces.ArrivedWarning("p_after", "ol_sys_b"), plan.Warnings);
        Assert.Empty(SteLint.Problems(StartPlaces.ArrivedWarning("p_after", "ol_sys_b")));
        Assert.Equal(("ol_sys_b", AtGateToA), (atGate.System, atGate.Position));
    }

    // An arrival ends the segment.

    /// <summary>After the jump, the start is in the new system. The points of the previous system do not count, and
    /// without a point in the new system the place is the Scene camera there.</summary>
    [Fact]
    public void AnArrivalGivesTheNewSystemWithoutThePointsOfThePreviousSystem()
    {
        var place = Place(Story, "p_after");

        Assert.Equal(StartPlaceKind.Camera, place.Kind);
        Assert.Equal("ol_sys_b", place.System);
        Assert.Equal("At the Scene camera in ol_sys_b", place.Text);
        Assert.Equal("Cnd_NPCSystemEnter of p_jump. No line of the path gives a point in ol_sys_b.", place.Source);
        Assert.Equal("p_jump", place.Trigger);
    }

    /// <summary>Review fix: without a point of the path after a jump, the start is at the jump gate or hole that leads
    /// back to the system before the jump, as the game places a ship after a jump.</summary>
    [Fact]
    public void AnArrivalWithoutAPointStartsAtTheJumpGateOfTheJump()
    {
        var place = Place(Story, "p_after", World([GateToA]));

        Space(place, "ol_sys_b", AtGateToA,
            "Cnd_NPCSystemEnter of p_jump. The point is 1,000 units from ol_gate_b_to_a, the jump gate or hole to ol_sys_a.");
        Assert.Equal(GateToA.Rotation, place.Orientation);
        // No object of the system leads back, so the start is at the Scene camera in that system.
        Assert.Equal(StartPlaceKind.Camera, Place(Story, "p_after", World([GateToA with { JumpTo = "ol_sys_c" }])).Kind);
    }

    /// <summary>Review fix: after a jump, the jump gate comes before each spawn of the mission, because a script also
    /// places a ship far from the gate, for example at a target or at a parking point. Without the gate, the spawn gives
    /// the point.</summary>
    [Theory]
    [InlineData("Act_SpawnShip = ol_juni")]
    [InlineData("Act_SpawnShip = ol_juni, no_ol, -100, 0, -200")]
    public void AfterAJumpTheJumpGateComesBeforeEachSpawn(string spawn)
    {
        var text = Story.Replace("Act_ActTrig = p_after", $"{spawn}\nAct_ActTrig = p_after");

        Assert.Equal(AtGateToA, Place(text, "p_after", World([GateToA])).Position);
        Assert.EndsWith("units from the spawn of ol_juni.", Place(text, "p_after").Source);
    }

    [Fact]
    public void ASpawnInTheNewSystemGivesThePointAfterTheArrival()
    {
        var text = Story.Replace("Act_ActTrig = p_after", "Act_SpawnShip = ol_juni\nAct_ActTrig = p_after");

        Space(Place(text, "p_after"), "ol_sys_b", new Vector3(-500, 0, -600 + StartPlaces.Offset),
            "Cnd_NPCSystemEnter of p_jump. The point is 1,000 units from the spawn of ol_juni.");
    }

    // Jump-here triggers.

    /// <summary>A debug trigger that nothing starts and that moves the player to a step gives the point of the step.
    /// A debug trigger of another system does not.</summary>
    [Fact]
    public void AJumpHereTriggerGivesThePointOfItsStep()
    {
        string Debug(string system) => Story + $"""


            [Trigger]
            nickname = p_debug
            system = {system}
            Cnd_True = no_params
            Act_MovePlayer = 7000, 0, 7000, 1000
            Act_ActTrig = p_after
            """;

        Space(Place(Debug("ol_sys_b"), "p_after"), "ol_sys_b", new Vector3(7000, 0, 7000),
            "Cnd_NPCSystemEnter of p_jump. The point comes from Act_MovePlayer of p_debug, which starts p_after.");
        Assert.Equal(StartPlaceKind.Camera, Place(Debug("ol_sys_a"), "p_after").Kind);
    }

    /// <summary>A debug trigger that also starts a later step does not count for a watcher that it starts: the watcher
    /// is not its latest step.</summary>
    [Fact]
    public void AJumpHereTriggerCountsOnlyForItsLatestStep()
    {
        var text = Story + """


            [Trigger]
            nickname = p_debug
            system = ol_sys_a
            Cnd_True = no_params
            Act_MovePlayer = 7000, 0, 7000, 1000
            Act_ActTrig = p_near
            Act_ActTrig = p_after
            """;

        var place = Place(text, "p_near");

        Assert.NotEqual(new Vector3(7000, 0, 7000), place.Position);
        Assert.Equal(new Vector3(500, 0, 600 + StartPlaces.Offset), place.Position);
    }

    /// <summary>Rule 5: a debug trigger that nothing starts takes its system hint before its condition, because retail
    /// never runs its condition. A restore trigger takes the system of its condition. The page warns when the chosen
    /// trigger waits for the player to enter space in another system than the start.</summary>
    [Fact]
    public void ADebugJumpTakesItsHintAndARestoreTriggerTakesItsCondition()
    {
        var text = Story + """


            [Trigger]
            nickname = p_debug
            system = ol_sys_a
            Cnd_SpaceEnter = ol_sys_b
            Act_MovePlayer = 7000, 0, 7000, 1000

            [Trigger]
            nickname = p_saver
            Cnd_Timer = 1
            Act_Save = p_restore, 1

            [Trigger]
            nickname = p_restore
            system = ol_sys_a
            Cnd_SpaceEnter = ol_sys_b
            Act_SetInitialPlayerPos = 8000, 0, 8000, 1, 0, 0, 0
            """.Replace("Act_ActTrig = p_after\n", "Act_ActTrig = p_after\nAct_ActTrig = p_saver\n");

        var debug = Plan(text, "p_debug");
        var restore = Plan(text, "p_restore");

        Space(debug.Place, "ol_sys_a", new Vector3(7000, 0, 7000), "Act_MovePlayer of p_debug.");
        Assert.Contains(StartPlaces.OtherSystemWarning("p_debug", "ol_sys_b", "ol_sys_a"), debug.Warnings);
        Assert.Empty(SteLint.Problems(StartPlaces.OtherSystemWarning("p_debug", "ol_sys_b", "ol_sys_a")));
        Space(restore.Place, "ol_sys_b", new Vector3(8000, 0, 8000), "Act_SetInitialPlayerPos of p_restore.");
        Assert.DoesNotContain(restore.Warnings, w => w.Contains("enter space in"));
    }

    /// <summary>Review fix: a place line without a system takes the system of the story at the step that it starts.</summary>
    [Fact]
    public void ADebugJumpWithoutASystemTakesTheSystemOfTheStoryAtItsStep()
    {
        var text = Story + """


            [Trigger]
            nickname = p_debug
            Cnd_True = no_params
            Act_MovePlayer = 7000, 0, 7000, 1000
            Act_ActTrig = p_lane
            """;

        Space(Place(text, "p_debug"), "ol_sys_a", new Vector3(7000, 0, 7000),
            "Act_MovePlayer of p_debug. The system comes from the system hint of p_lane.");
    }

    // Checkpoints and place lines.

    /// <summary>A checkpoint in two stages. The first stage waits for the launch and sets the pose. The second stage
    /// waits for the space entry in a system. The place takes the system of the second stage and the heading of the
    /// pose (w, x, y, z).</summary>
    [Fact]
    public void ATwoStageCheckpointTakesTheSystemOfItsSecondStageAndItsHeading()
    {
        var text = Story.Replace("Act_ActTrig = p_after\n", "Act_ActTrig = p_after\nAct_Save = p_stage1, 1\n") + """


            [Trigger]
            nickname = p_stage1
            Cnd_PlayerLaunch = no_params
            Act_SetInitialPlayerPos = 10, 20, 30, 0, 0, 1, 0
            Act_ActTrig = p_stage2

            [Trigger]
            nickname = p_stage2
            Cnd_SpaceEnter = ol_sys_c
            Act_ActTrig = p_later

            [Trigger]
            nickname = p_later
            Cnd_Timer = 5
            """;

        var checkpoint = Plan(text, "p_stage1");
        var later = Place(text, "p_later");

        Assert.Equal(StartKind.Checkpoint, checkpoint.Kind);
        Space(checkpoint.Place, "ol_sys_c", new Vector3(10, 20, 30),
            "Act_SetInitialPlayerPos of p_stage1. The system comes from Cnd_SpaceEnter of p_stage2.");
        Assert.Equal(new Quaternion(0, 1, 0, 0), checkpoint.Place.Orientation);
        // A step after the checkpoint keeps the pose of the first stage.
        Assert.Equal(("ol_sys_c", new Vector3(10, 20, 30)), (later.System, later.Position));
        Assert.StartsWith("Cnd_SpaceEnter of p_stage2. The point comes from Act_SetInitialPlayerPos of p_stage1.",
            later.Source);
    }

    /// <summary>Rule 2 and review fix: a restore trigger and a Cnd_True trigger that it starts run together at the load,
    /// so the place line of that step gives the place of the checkpoint.</summary>
    [Fact]
    public void TheCndTrueStepOfARestoreTriggerGivesThePlaceOfTheCheckpoint()
    {
        var text = Story.Replace("Act_ActTrig = p_after\n", "Act_ActTrig = p_after\nAct_Save = p_restore, 1\n") + """


            [Trigger]
            nickname = p_restore
            system = ol_sys_a
            Cnd_SpaceEnter = no_params
            Act_ActTrig = p_step

            [Trigger]
            nickname = p_step
            system = ol_sys_a
            Cnd_True = no_params
            Act_MovePlayer = 4000, 0, 4000, 100
            """;

        var plan = Plan(text, "p_restore");

        Assert.Equal(StartKind.Checkpoint, plan.Kind);
        Space(plan.Place, "ol_sys_a", new Vector3(4000, 0, 4000), "Act_MovePlayer of p_step, which p_restore starts.");
    }

    /// <summary>Act_RelocateShip of the player is a place line with a heading. The setup triggers do not copy it, so a
    /// later move does not undo the place. A move of another ship gives no point.</summary>
    [Fact]
    public void ActRelocateShipOfThePlayerIsAPlaceLineThatTheSetupDoesNotCopy()
    {
        var text = Story.Replace("Act_SpawnShip = ol_king\n",
            "Act_SpawnShip = ol_king\nAct_RelocateShip = ol_king, 40000, 0, 40000\nAct_RelocateShip = Player, 300, 0, 400, 0, 0, 1, 0\n");

        var plan = Plan(text, "p_near");

        Space(plan.Place, "ol_sys_a", new Vector3(300, 0, 400), "Act_RelocateShip of p_init.");
        Assert.Equal(new Quaternion(0, 1, 0, 0), plan.Place.Orientation);
        var world = plan.Setup.Single(s => s.Nickname == "ol_play_world").Lines;
        Assert.Contains(world, l => l.Key == "Act_RelocateShip" && l.Values[0] == "ol_king");
        Assert.DoesNotContain(world, l => l.Key == "Act_RelocateShip" && l.Values[0] == "Player");
    }

    /// <summary>Act_ForceLand docks the player at its base.</summary>
    [Fact]
    public void ActForceLandOnThePathGivesABaseStart()
    {
        var text = Story.Replace("Act_ActTrig = p_jump", "Act_ForceLand = ol_base_b\nAct_ActTrig = p_jump");

        Docked(Place(text, "p_jump"), "ol_base_b", "Act_ForceLand of p_lane.");
    }

    // Review fixes about names and docks that no line names.

    /// <summary>A base fact without a base name docks at a base that the walk cannot name. The start is at the Scene
    /// camera with the reason.</summary>
    [Theory]
    [InlineData("Cnd_BaseEnter = ANY")]
    [InlineData("Cnd_SpaceExit = no_params")]
    [InlineData("Cnd_InSpace = false")]
    public void ADockWithoutABaseNameGivesTheSceneCameraWithTheReason(string condition)
    {
        var text = Story.Replace("Cnd_TLExited = Player, ol_ring_1", condition);

        var place = Place(text, "p_jump");

        Assert.Equal(StartPlaceKind.Camera, place.Kind);
        Assert.EndsWith(" of p_lane. The player is docked at a base that no line names.", place.Source);
    }

    /// <summary>A system name that the world does not have, for example the system hint ALL, gives no system. The next
    /// rule gives it.</summary>
    [Fact]
    public void ASystemNameThatTheWorldDoesNotHaveGivesNoSystem()
    {
        var text = Story.Replace("nickname = p_near\nsystem = ol_sys_a", "nickname = p_near\nsystem = ALL");

        var known = Place(text, "p_lane", World());
        var unknownWorld = Place(text, "p_lane");

        Assert.Equal("ol_sys_a", known.System);
        Assert.EndsWith("The system comes from the system hint of p_lane.", known.Source);
        // A world without the names of the systems cannot check the name.
        Assert.Equal("ALL", unknownWorld.System);
    }

    /// <summary>Without a system on the path, the start is at the base of the launch. Without any fact, it is at the
    /// Scene camera.</summary>
    [Fact]
    public void WithoutASystemTheStartIsAtTheBaseOfTheLaunchAndWithoutAFactAtTheCamera()
    {
        var text = Story.Replace("system = ol_sys_a\n", "");

        Docked(Place(text, "p_lane"), "ol_base_a",
            "Cnd_LocEnter of p_bar. No line of the path names the system of Cnd_DistVec of p_near.");
        var none = Place(Story, "p_start");
        Assert.Equal((StartPlaceKind.Camera, "No line of the path gives a place."), (none.Kind, none.Source));
        // Review fix: rule 7 gives the Scene camera in any system, also for a trigger with a system hint. The start trigger
        // and the mission start without a trigger then have the same place.
        var hinted = Story.Replace("nickname = p_start\n", "nickname = p_start\nsystem = ol_sys_a\n");
        Assert.Equal((StartPlaceKind.Camera, null), (Place(hinted, "p_start").Kind, Place(hinted, "p_start").System));
        Assert.Null(StartPlan.Make(Index(hinted), StartPlanTests.Slot, MissionStartPoint.MissionStart).Place.System);
    }

    /// <summary>Review fix: a launch without a point in its system starts at the base of the launch. Its dock is the
    /// point of the launch in the game. The center of a system is never a start point.</summary>
    [Fact]
    public void ALaunchWithoutAPointStartsAtTheBaseOfTheLaunch()
    {
        var text = Story.Replace("Act_SpawnShip = ol_king\n", "");

        Docked(Place(text, "p_init"), "ol_base_a",
            "Cnd_LocEnter of p_bar. No line of the path gives a point in ol_sys_a after Cnd_SpaceEnter of p_launch.");
    }

    /// <summary>Review fix: within the same part of the walk, a spawn with its own point comes before a spawn at the
    /// position of its item.</summary>
    [Fact]
    public void ASpawnWithItsOwnPointComesBeforeASpawnAtThePositionOfItsItem()
    {
        var text = Story.Replace("Act_SpawnShip = ol_king\n", "Act_SpawnShip = ol_king\nAct_SpawnShip = ol_juni, no_ol, 100, 0, 200\n");

        Space(Place(text, "p_init"), "ol_sys_a", new Vector3(100, 0, 200 + StartPlaces.Offset),
            "Cnd_SpaceEnter of p_launch. The system comes from the system hint of p_init. The point is 1,000 units from " +
            "the spawn of ol_juni.");
    }

    /// <summary>Review fix: a spawn that the path makes hostile to the player gives no point, also through a label. The
    /// last attitude line counts.</summary>
    [Theory]
    [InlineData("Act_SetVibe = ol_king, Player, REP_HOSTILE_MAXIMUM", true)]
    [InlineData("Act_SetVibeLblToShip = ol_enemies, Player, REP_NEUTRAL_HOSTILE", true)]
    [InlineData("Act_SetVibe = Player, ol_king, -0.6", true)]
    [InlineData("Act_SetVibe = ol_king, Player, REP_HOSTILE_MAXIMUM\nAct_SetVibe = ol_king, Player, REP_FRIEND_THRESHOLD", false)]
    [InlineData("Act_SetVibe = ol_king, ol_juni, REP_HOSTILE_MAXIMUM", false)]
    public void ASpawnThatThePathMakesHostileToThePlayerGivesNoPoint(string vibes, bool hostile)
    {
        var text = Story.Replace("position = 500, 0, 600\n", "position = 500, 0, 600\nlabel = ol_enemies\n")
            .Replace("Act_SpawnShip = ol_king\n", $"Act_SpawnShip = ol_king\n{vibes}\n");

        var place = Place(text, "p_init");

        Assert.Equal(hostile ? StartPlaceKind.Base : StartPlaceKind.Space, place.Kind);
    }

    /// <summary>Review fix: a Cnd_DistShip line that waits for the player near an object of the game data gives a point
    /// at the edge of its range from that object, and the system of the object. The point is at least 1,000 units
    /// away.</summary>
    [Fact]
    public void ACndDistShipNearAnObjectOfTheGameDataGivesItsPointAndSystem()
    {
        var text = Story.Replace("Cnd_TLExited = Player, ol_ring_1", "Cnd_DistShip = inside, Player, ol_buoy, 2000");
        var buoy = new WorldObject("ol_buoy", "ol_sys_c", new Vector3(-7000, 0, 5000), Quaternion.Identity, null);

        Space(Place(text, "p_jump", World([buoy])), "ol_sys_c", new Vector3(-7000, 0, 7000), "Cnd_DistShip of p_lane.");
        Assert.Equal(new Vector3(-7000, 0, 5000 + StartPlaces.Offset),
            Place(text.Replace("ol_buoy, 2000", "ol_buoy, 300"), "p_jump", World([buoy])).Position);
        // An object that the world does not know gives no point, so the older point of the segment gives it.
        Assert.Equal(new Vector3(1000, 0, 3000), Place(text, "p_jump").Position);
    }

    /// <summary>Review fix: a Cnd_BaseExit with a base names the base of the launch. It gives the system of the start,
    /// and the start is docked there when no line gives a point.</summary>
    [Fact]
    public void CndBaseExitWithABaseNamesTheBaseOfTheLaunch()
    {
        var text = Story.Replace("Cnd_LocEnter = ol_bar, ol_base_a", "Cnd_BaseExit = ol_base_a")
            .Replace("Act_SpawnShip = ol_king\n", "").Replace("nickname = p_init\nsystem = ol_sys_a\n", "nickname = p_init\n");

        Docked(Place(text, "p_init", World(("ol_base_a", "ol_sys_a"))), "ol_base_a",
            "Cnd_BaseExit of p_bar. No line of the path gives a point in ol_sys_a after Cnd_SpaceEnter of p_launch.");
    }

    /// <summary>Rule 6: a spawn of the replay before the segment comes last. The system of a spawn is the system of its
    /// trigger first, then the system of the ship.</summary>
    [Fact]
    public void ASpawnOfTheReplayBeforeTheSegmentGivesThePointLast()
    {
        var text = Story.Replace("Act_SpawnShip = ol_king\n", "")
            .Replace("Act_ActTrig = p_bar", "Act_SpawnShip = ol_king, no_ol, 3000, 0, 3000\nAct_ActTrig = p_bar");

        Space(Place(text, "p_init"), "ol_sys_a", new Vector3(3000, 0, 3000 + StartPlaces.Offset),
            "Cnd_SpaceEnter of p_launch. The system comes from the system hint of p_init. The point is 1,000 units from " +
            "the spawn of ol_king.");
    }

    // The plan and the page.

    /// <summary>Review fix: the warnings follow the place that the user picks. A start at the Scene camera is in space,
    /// so the launch watchers do not run. A picked base launches, so they run, and the page compares its system with the
    /// system of the chosen trigger.</summary>
    [Fact]
    public void TheWarningsFollowThePlaceThatTheUserPicks()
    {
        var warning = StartPlaces.LaunchWatcherWarning("p_launched");
        var text = Story + """


            [Trigger]
            nickname = p_debug
            system = ol_sys_a
            Cnd_SpaceEnter = ol_sys_b
            Act_MovePlayer = 7000, 0, 7000, 1000
            """;
        var world = World(("ol_base_a", "ol_sys_a"), ("ol_base_b", "ol_sys_b"));

        Assert.Contains(warning, Plan(Story, "p_lane", choice: StartPlaceChoice.Camera).Warnings);
        Assert.DoesNotContain(warning, Plan(Story, "p_lane", choice: StartPlaceChoice.Base, dock: "ol_base_a").Warnings);
        Assert.Contains(StartPlaces.OtherSystemWarning("p_debug", "ol_sys_b", "ol_sys_a"),
            Plan(text, "p_debug", world, StartPlaceChoice.Base, "ol_base_a").Warnings);
        Assert.DoesNotContain(Plan(text, "p_debug", world, StartPlaceChoice.Base, "ol_base_b").Warnings,
            w => w.Contains("enter space in"));
        // The system of the Scene camera is not known before the play.
        Assert.DoesNotContain(Plan(text, "p_debug", world, StartPlaceChoice.Camera).Warnings,
            w => w.Contains("enter space in"));
    }

    /// <summary>Review fix: the plan keeps the suggestion also while the place choice is a base or the Scene camera, so
    /// the page can show it.</summary>
    [Fact]
    public void ThePlanKeepsTheSuggestionWhileTheUserPicksAnotherPlace()
    {
        var picked = Plan(Story, "p_lane", choice: StartPlaceChoice.Base, dock: "ol_base_b");
        var camera = Plan(Story, "p_lane", choice: StartPlaceChoice.Camera);

        Assert.Equal(("ol_base_b", "The base that you picked."), (picked.Place.Base, picked.Place.Source));
        Assert.Equal(Plan(Story, "p_lane").Place, picked.Suggested);
        Assert.Equal(StartPlaceKind.Camera, camera.Place.Kind);
        Assert.Equal(StartPlaceKind.Space, camera.Suggested.Kind);
        Assert.Equal("p_near", camera.Suggested.Trigger);
    }

    /// <summary>The mission start reads the same walk: the triggers that run at once at the start.</summary>
    [Fact]
    public void TheMissionStartReadsThePlaceLinesThatRunAtOnce()
    {
        var text = Story.Replace("Act_ActTrig = p_bar", "Act_MovePlayer = 100, 0, 100, 500\nAct_ActTrig = p_bar")
            .Replace("nickname = p_start\n", "nickname = p_start\nsystem = ol_sys_a\n");

        var place = StartPlan.Make(Index(text), StartPlanTests.Slot, MissionStartPoint.MissionStart).Place;

        Space(place, "ol_sys_a", new Vector3(100, 0, 100), "Act_MovePlayer of p_start.");
        Assert.Equal(StartPlaceKind.Camera,
            StartPlan.Make(Index(Story), StartPlanTests.Slot, MissionStartPoint.MissionStart).Place.Kind);
    }

    /// <summary>The world knows the system of each base without letter case, also in each copy of it.</summary>
    [Fact]
    public void TheWorldKeepsTheSystemOfEachBaseInItsCopies()
    {
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.Base] = ["ol_base_a"],
        }, _ => true, new Dictionary<string, string> { [" ol_base_a "] = " ol_sys_a ", ["ol_base_b"] = "" });
        var removed = new Dictionary<MissionValueKind, IReadOnlyDictionary<string, string>>
        {
            [MissionValueKind.Base] = new Dictionary<string, string> { ["ol_base_a"] = "ol_mod" },
        };

        Assert.Equal("ol_sys_a", world.SystemOfBase("OL_BASE_A"));
        Assert.Null(world.SystemOfBase("ol_base_b"));
        Assert.Equal("ol_sys_a", world.WithFiles(_ => false).SystemOfBase("ol_base_a"));
        Assert.Equal("ol_sys_a", world.WithNames(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["ol_sys_a"],
        }).SystemOfBase("ol_base_a"));
        Assert.Equal("ol_sys_a", world.WithRemoved(removed).SystemOfBase("ol_base_a"));
        Assert.Null(MissionWorld.Empty.SystemOfBase("ol_base_a"));
    }

    /// <summary>The world knows the objects of the systems without letter case, also in each copy of it. The first jump
    /// object of a system that leads to the other system is the arrival of a jump.</summary>
    [Fact]
    public void TheWorldKeepsTheObjectsAndTheJumpsInItsCopies()
    {
        var hole = GateToA with { Nickname = "ol_hole_b_to_a", Position = new Vector3(1, 2, 3) };
        var buoy = new WorldObject("ol_buoy", "ol_sys_a", Vector3.Zero, Quaternion.Identity, null);
        var world = World([GateToA, hole, buoy, buoy with { System = "ol_sys_c" }, buoy with { Nickname = " " }]);

        Assert.Equal(GateToA, world.ObjectOf(" OL_GATE_B_TO_A "));
        Assert.Equal("ol_sys_a", world.ObjectOf("ol_buoy")!.System);
        Assert.Equal(GateToA, world.JumpInto("OL_SYS_B", "ol_sys_a"));
        Assert.Null(world.JumpInto("ol_sys_a", "ol_sys_b"));
        Assert.Equal(GateToA, world.WithFiles(_ => false).JumpInto("ol_sys_b", "ol_sys_a"));
        Assert.Equal(GateToA,
            world.WithNames(new Dictionary<MissionValueKind, IEnumerable<string>>()).ObjectOf("ol_gate_b_to_a"));
        Assert.Null(MissionWorld.Empty.ObjectOf("ol_buoy"));
    }

    /// <summary>Each source text of the suggestion is plain STE and ends with a period.</summary>
    [Fact]
    public void TheSourceTextsAreSteAndEndWithAPeriod()
    {
        foreach (var trigger in new[] { "p_start", "p_bar", "p_launch", "p_launched", "p_init", "p_near", "p_lane",
                     "p_jump", "p_after" })
        {
            var source = Place(Story, trigger).Source;
            Assert.EndsWith(".", source);
            Assert.Empty(SteLint.Problems(source));
        }
    }
}
