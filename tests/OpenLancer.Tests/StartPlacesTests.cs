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
    /// A story in the shape of the report of 2026-10-27: a bar visit at a base, the accept, a launch that waits for
    /// Cnd_SpaceEnter without a system, a step in space with a spawn, a point near a gate, and a jump into a second
    /// system. A watcher waits for the end of the launch. The bar and the launch triggers have no system hint.
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
    private static MissionWorld World(params (string Base, string System)[] bases) => new(
        new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.System] = ["ol_sys_a", "ol_sys_b", "ol_sys_c"],
            [MissionValueKind.Base] = bases.Select(b => b.Base).ToList(),
        }, null, bases.ToDictionary(b => b.Base, b => b.System));

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

    /// <summary>Review fix: the triggers that wait for the end of the launch of the timeline do not run after a start in
    /// space, also after a later jump. The page warns about them.</summary>
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

    /// <summary>An arrival on the path that is newer than each other fact is the same jump as the one that the chosen
    /// trigger waits for, so the start is before it.</summary>
    [Fact]
    public void AnArrivalOfTheSameJumpOnThePathDoesNotPutTheStartAfterTheJump()
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

        var place = Place(text, "p_after");

        Assert.Equal(("ol_sys_a", new Vector3(1000, 0, 3000)), (place.System, place.Position));
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

    /// <summary>The setup of a checkpoint in two stages: the first stage waits for the launch and sets the pose, the
    /// second stage waits for the space entry in a system. The place takes the system of the second stage, and the heading
    /// of the pose (w, x, y, z).</summary>
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
    }

    /// <summary>Review fix: a launch without a point in its system starts at the base of the launch, whose dock is the
    /// point of the launch in the game. The center of a system is never a start point.</summary>
    [Fact]
    public void ALaunchWithoutAPointStartsAtTheBaseOfTheLaunch()
    {
        var text = Story.Replace("Act_SpawnShip = ol_king\n", "");

        Docked(Place(text, "p_init"), "ol_base_a",
            "Cnd_LocEnter of p_bar. No line of the path gives a point in ol_sys_a after Cnd_SpaceEnter of p_launch.");
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
