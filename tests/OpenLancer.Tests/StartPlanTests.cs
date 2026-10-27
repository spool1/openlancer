using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The start plan of a play from a trigger (section 5.3 of the M6c design, build step 2.2): the replay, the active set,
/// the story state, the start place and the setup triggers. The scripts are synthetic, and all names are invented.
/// </summary>
public sealed class StartPlanTests
{
    /// <summary>A slot of the tests: start stage 4, accept stage 5.</summary>
    internal static readonly StorySlot Slot = new("M99", "Mission_99", @"MISSIONS\TEST\test.ini", 4, 5,
        new NextMission(NextMissionKind.AtOnce, 0), 0, 0, []);

    /// <summary>
    /// The offer at a base, the accept, the launch with two spawns, a kill that a later trigger waits for, a base scene
    /// and the chosen timer. A checkpoint of the launch restores at the next launch with a place line. A fail watcher
    /// waits beside the kill. One timer has no path.
    /// </summary>
    internal const string Script = """
        [Mission]

        [MsnShip]
        nickname = ol_ship_a
        system = ol_sys_a
        position = 100, 0, 200

        [MsnShip]
        nickname = ol_ship_c
        system = ol_sys_a
        position = 300, 0, 400

        [NNObjective]
        nickname = ol_obj_1
        type = ids, 500100

        [Trigger]
        nickname = t_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = t_offer

        [Trigger]
        nickname = t_offer
        Cnd_BaseEnter = ol_base_a
        Act_SetTitle = 500001
        Act_SendComm = Player, Player, ol_line_1
        Act_ActTrig = t_accept

        [Trigger]
        nickname = t_accept
        Cnd_MsnResponse = accept
        Act_SetRep = Player, ol_faction_a, -0.9
        Act_ActTrig = t_launch

        [Trigger]
        nickname = t_launch
        Cnd_SpaceEnter = ol_sys_a
        Act_SpawnShip = ol_ship_a
        Act_SpawnShip = ol_ship_c
        Act_PlayMusic = ol_music_1
        Act_ActTrig = t_kill
        Act_ActTrig = t_fail
        Act_Save = t_restore, 1

        [Trigger]
        nickname = t_kill
        Cnd_Destroyed = ol_ship_a
        Act_PlayMusic = ol_music_2
        Act_SetNNObj = ol_obj_1
        Act_AddRTC = missions\ol\ol_scene.ini
        Act_AddRTC = missions\ol\ol_other.ini
        Act_ActTrig = t_scene

        [Trigger]
        nickname = t_scene
        Cnd_RTCDone = missions\ol\ol_scene.ini
        Act_ActTrig = t_chosen

        [Trigger]
        nickname = t_fail
        Cnd_Destroyed = ol_ship_c
        Act_ChangeState = FAIL, 1

        [Trigger]
        nickname = t_chosen
        Cnd_Timer = 5
        Act_ChangeState = SUCCEED

        [Trigger]
        nickname = t_restore
        Cnd_PlayerLaunch = no_params
        Act_SetInitialPlayerPos = 10, 20, 30, 1, 0, 0, 0
        Act_ActTrig = t_fail

        [Trigger]
        nickname = t_orphan
        Cnd_Timer = 2
        """;

    private static StartPlan Plan(string text, string? trigger, StartPlaceChoice place = StartPlaceChoice.Suggested,
        string? dock = null) =>
        StartPlan.Make(Index(text), Slot, trigger == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(trigger),
            place, dock);

    /// <summary>The lines of a setup trigger as text.</summary>
    private static List<string> Lines(StartPlan plan, int setup) =>
        plan.Setup[setup].Lines.Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

    [Fact]
    public void AStartTriggerGivesAMissionStartWithoutATriggerSave()
    {
        var plan = Plan(Script, "t_start");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.MissionStart, plan.Kind);
        Assert.Empty(plan.SavedTriggers);
        Assert.Empty(plan.Setup);
        Assert.Equal(["t_offer"], plan.ActiveSet);
        Assert.Equal((4, false), (plan.Stage, plan.Accepted));
        Assert.Equal("Mission start (t_start)", plan.StartName);
    }

    [Fact]
    public void TheMissionStartGivesTheStartSet()
    {
        var plan = Plan(Script, null);

        Assert.Equal((StartKind.MissionStart, "Mission start"), (plan.Kind, plan.StartName));
        Assert.Equal(["t_offer"], plan.ActiveSet);
        Assert.Empty(plan.SavedTriggers);
    }

    [Fact]
    public void ARestoreTriggerGivesACheckpointStartWithOneTriggerSave()
    {
        var plan = Plan(Script, "t_restore");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.Checkpoint, plan.Kind);
        Assert.Equal(["t_restore"], plan.SavedTriggers);
        Assert.Empty(plan.Setup);
        Assert.Equal("Checkpoint t_restore", plan.StartName);
        // The story path reaches the checkpoint only through its Act_Save line, after the accept.
        Assert.Equal((5, true), (plan.Stage, plan.Accepted));
    }

    /// <summary>
    /// A saved game keeps more state than the triggers, for example the objective, the dock rules and the reputations.
    /// The start save keeps only the triggers, so a checkpoint start and an approximate start whose path starts at a
    /// checkpoint warn about it (review fix of M6c-2). A start from a start trigger has no such warning.
    /// </summary>
    [Fact]
    public void ACheckpointStartWarnsThatTheStateOfASavedGameIsNotInTheStart()
    {
        var checkpoint = Plan(Script, "t_restore");
        var fromCheckpoint = Plan(Script, "t_fail");

        Assert.Contains(StartPlan.CheckpointStateWarning("t_restore"), checkpoint.Warnings);
        Assert.Equal(["t_restore", "t_fail"], fromCheckpoint.Path);
        Assert.Contains(StartPlan.CheckpointStateWarning("t_restore"), fromCheckpoint.Warnings);
        Assert.DoesNotContain(Plan(Script, "t_chosen").Warnings, w => w.StartsWith("A saved game", StringComparison.Ordinal));
        Assert.DoesNotContain(Plan(Script, null).Warnings, w => w.StartsWith("A saved game", StringComparison.Ordinal));
        Assert.Empty(SteLint.Problems(StartPlan.CheckpointStateWarning("t_restore")));
    }

    /// <summary>The kind of a start, for the chip of the toolbar and for the plan: one rule.</summary>
    [Fact]
    public void TheKindOfAStartIsTheKindOfThePlan()
    {
        var index = Index(Script);
        foreach (var start in new[] { null, "t_start", "t_restore", "t_chosen", "t_gone" })
        {
            var point = start == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(start);
            var expected = start == "t_gone" ? StartKind.Approximate : StartPlan.Make(index, Slot, point).Kind;
            Assert.Equal(expected, StartPlan.KindOf(index, Slot, point));
            Assert.Equal(StartKind.TestScript, StartPlan.KindOf(index, null, point));
        }
    }

    [Fact]
    public void ARestoreTriggerWithActSetInitialPlayerPosGivesASpaceStartAtItsPoint()
    {
        var place = Plan(Script, "t_restore").Place;

        Assert.Equal(StartPlaceKind.Space, place.Kind);
        Assert.Equal(new Vector3(10, 20, 30), place.Position);
        Assert.Equal(Quaternion.Identity, place.Orientation);
        // The line has no system. The story at the step that the restore trigger starts gives it.
        Assert.Equal("ol_sys_a", place.System);
        Assert.Equal("Act_SetInitialPlayerPos of t_restore. The system comes from Cnd_SpaceEnter of t_launch.", place.Source);
    }

    [Fact]
    public void ARestoreTriggerWhoseConditionSavesAStateGivesAOneLineSetup()
    {
        var text = Script.Replace("Cnd_PlayerLaunch = no_params", "Cnd_Timer = 10");

        var plan = Plan(text, "t_restore");

        Assert.Equal(StartKind.Checkpoint, plan.Kind);
        var setup = Assert.Single(plan.Setup);
        Assert.Equal("ol_play_setup", setup.Nickname);
        Assert.Equal(["Cnd_True = no_params", "Act_ActTrig = t_restore"], Lines(plan, 0));
        Assert.Equal(["ol_play_setup"], plan.SavedTriggers);
    }

    [Fact]
    public void TheReplayRunsThePathAndStopsWhenTheChosenTriggerStarts()
    {
        var plan = Plan(Script, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Equal(StartKind.Approximate, plan.Kind);
        // The start trigger ran by itself, a heartbeat started the pending offer, and each active trigger of the path
        // ran by force.
        Assert.Equal(["t_start", "t_offer", "t_accept", "t_launch", "t_kill", "t_scene", "t_chosen"], plan.Path);
        // The fail watcher waits beside the chosen trigger.
        Assert.Equal(["t_fail", "t_chosen"], plan.ActiveSet);
        Assert.Equal("t_chosen (approximate)", plan.StartName);
    }

    [Fact]
    public void AnApproximateStartHoldsOnlyWorldAndStateActionsInReplayOrder()
    {
        var plan = Plan(Script, "t_chosen");

        Assert.Equal(2, plan.Setup.Count);
        // The state lines and the starts of the whole active set. The comm line, the starts and the save do not go.
        Assert.Equal(
        [
            "Cnd_True = no_params", "Act_SetRep = Player, ol_faction_a, -0.9", "Act_SetNNObj = ol_obj_1",
            "Act_ActTrig = ol_play_world", "Act_ActTrig = t_fail", "Act_ActTrig = t_chosen",
        ], Lines(plan, 0));
        // The world waits for space. The spawn of the ship that the path destroys goes, and only the last music stays.
        Assert.Equal("ol_play_world", plan.Setup[1].Nickname);
        Assert.Equal(["Cnd_SpaceEnter = no_params", "Act_SpawnShip = ol_ship_c", "Act_PlayMusic = ol_music_2"],
            Lines(plan, 1));
        Assert.Equal(["ol_play_setup"], plan.SavedTriggers);
    }

    [Fact]
    public void TheSaveKeysAreTheNetResultWithoutASceneThatThePathPlayed()
    {
        var state = Plan(Script, "t_chosen").State;

        Assert.Equal(500001, state.Title);
        Assert.Equal(0, state.Offer);
        // The path waits for the end of ol_scene, so it does not play again.
        Assert.Equal([(@"missions\ol\ol_other.ini", false)], state.Scenes);
    }

    /// <summary>
    /// The base scenes and the ambient scripts compare their paths as the game compares them (review fix of M6c-2). Each
    /// add adds one entry. Act_RemoveRTC compares with letter case and removes each match. Act_RemoveAmbient compares
    /// without letter case and removes the first match. No compare reads / as \.
    /// </summary>
    [Fact]
    public void TheSaveKeysComparePathsAsTheGameDoes()
    {
        const string text = """
            [Trigger]
            nickname = s_lines
            Cnd_Timer = 1
            Act_AddRTC = missions\ol\a.ini
            Act_AddRTC = missions\ol\a.ini
            Act_AddRTC = missions\ol\b.ini
            Act_AddRTC = missions\ol\c.ini
            Act_AddRTC = missions\ol\d.ini
            Act_RemoveRTC = MISSIONS\OL\B.INI
            Act_RemoveRTC = missions/ol/c.ini
            Act_RemoveRTC = missions\ol\d.ini
            Act_AddAmbient = scripts\ol\amb.thn, ol_room, ol_base_a
            Act_AddAmbient = scripts\ol\amb.thn, ol_room_2, ol_base_a
            Act_RemoveAmbient = SCRIPTS\OL\AMB.THN
            """;
        var index = Index(text);

        var state = StartSetup.StateOf(index, index.Trigger("s_lines")!.Actions, []);

        Assert.Equal([@"missions\ol\a.ini", @"missions\ol\a.ini", @"missions\ol\b.ini", @"missions\ol\c.ini"],
            state.Scenes.Select(s => s.File));
        Assert.Equal([(@"scripts\ol\amb.thn", "ol_room_2", "ol_base_a")], state.Ambients);
    }

    /// <summary>An ambient script of the path whose base the game data does not have refuses the start, because the
    /// game cannot load such a line of the start save (review fix of M6c-2).</summary>
    [Fact]
    public void AnAmbientScriptInABaseThatTheGameDataDoesNotHaveRefuses()
    {
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>
        {
            [MissionValueKind.Base] = ["ol_base_a"],
        }, null);
        string Text(string dock) => Script.Replace("Act_ActTrig = t_scene",
            $"Act_AddAmbient = scripts\\ol\\amb.thn, ol_room, {dock}\nAct_ActTrig = t_scene");

        var known = StartPlan.Make(Index(Text("ol_base_a"), world), Slot, MissionStartPoint.At("t_chosen"));
        var unknown = StartPlan.Make(Index(Text("ol_base_x"), world), Slot, MissionStartPoint.At("t_chosen"));

        Assert.True(known.CanStart, known.Reason);
        Assert.Equal([(@"scripts\ol\amb.thn", "ol_room", "ol_base_a")], known.State.Ambients);
        Assert.Equal(StartRefusal.SaveProblem, unknown.Refusal);
        Assert.Equal(@"The path adds the ambient script 'scripts\ol\amb.thn' in base 'ol_base_x', which is not in the game " +
                     "data. The game cannot load the start save with it. Correct the Act_AddAmbient line, or pick another " +
                     "trigger.", unknown.Reason);
        Assert.Empty(SteLint.Problems(unknown.Reason!));
    }

    [Fact]
    public void AnAcceptOnTheStoryPathGivesTheAcceptStageAndMissionAccepted()
    {
        Assert.Equal((5, true), (Plan(Script, "t_chosen").Stage, Plan(Script, "t_chosen").Accepted));
        // The accept trigger itself waits for the accept.
        Assert.Equal((4, false), (Plan(Script, "t_accept").Stage, Plan(Script, "t_accept").Accepted));
    }

    /// <summary>The launch gives the system, and the first spawn after the launch gives the point. Section 5.3, step 7
    /// of the M6c design has the rule (fix of 2026-10-27).</summary>
    [Fact]
    public void TheLaunchOfTheStoryPathGivesASpaceStartNearTheFirstSpawnAfterIt()
    {
        var place = Plan(Script, "t_chosen").Place;

        Assert.Equal(StartPlaceKind.Space, place.Kind);
        Assert.Equal("ol_sys_a", place.System);
        Assert.Equal(new Vector3(100, 0, 200 + StartPlaces.Offset), place.Position);
        Assert.Equal("Cnd_SpaceEnter of t_launch. The point is 1,000 units from the spawn of ol_ship_a.", place.Source);
    }

    /// <summary>A launch without a system is still a launch, so the start is not at the base of the offer. The base of the
    /// launch gives the system when the world knows it. Else the start is at that base, and the launch fires the
    /// triggers of the path again.</summary>
    [Fact]
    public void CndSpaceEnterWithNoParamsIsALaunchFromTheBaseOfTheOffer()
    {
        var text = Script.Replace("Cnd_SpaceEnter = ol_sys_a", "Cnd_SpaceEnter = no_params");
        var world = new MissionWorld(new Dictionary<MissionValueKind, IEnumerable<string>>(), null,
            new Dictionary<string, string> { ["ol_base_a"] = "ol_sys_a" });

        var known = StartPlan.Make(Index(text, world), Slot, MissionStartPoint.At("t_chosen")).Place;
        var unknown = Plan(text, "t_chosen").Place;

        Assert.Equal((StartPlaceKind.Space, "ol_sys_a"), (known.Kind, known.System));
        Assert.Equal(new Vector3(100, 0, 200 + StartPlaces.Offset), known.Position);
        Assert.Equal("Cnd_SpaceEnter of t_launch. The system comes from the base of Cnd_BaseEnter of t_offer. The point " +
                     "is 1,000 units from the spawn of ol_ship_a.", known.Source);
        Assert.Equal((StartPlaceKind.Base, "ol_base_a"), (unknown.Kind, unknown.Base));
        Assert.Equal("Cnd_BaseEnter of t_offer. No line of the path names the system of Cnd_SpaceEnter of t_launch.",
            unknown.Source);
    }

    [Fact]
    public void ABaseStartRebuildsTheWorldAtTheLaunch()
    {
        var plan = Plan(Script, "t_chosen", StartPlaceChoice.Base, "ol_base_b");

        Assert.Equal((StartPlaceKind.Base, "ol_base_b"), (plan.Place.Kind, plan.Place.Base));
        Assert.Equal("Cnd_PlayerLaunch = no_params", Lines(plan, 1)[0]);
    }

    [Fact]
    public void TheCameraChoiceGivesTheCamera()
    {
        Assert.Equal(StartPlaceKind.Camera, Plan(Script, "t_chosen", StartPlaceChoice.Camera).Place.Kind);
    }

    [Fact]
    public void AChosenTriggerWithCndTrueIsInTheActiveSet()
    {
        var text = Script.Replace("""
            nickname = t_chosen
            Cnd_Timer = 5
            """, """
            nickname = t_chosen
            Cnd_True = no_params
            """);

        var plan = Plan(text, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("t_chosen", plan.ActiveSet);
        Assert.Contains("Act_ActTrig = t_chosen", Lines(plan, 0));
    }

    [Fact]
    public void ATriggerThatAStopOnAnotherBranchRemovesRefusesWithItsReason()
    {
        const string text = """
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_branch
            Act_ActTrig = s_mid

            [Trigger]
            nickname = s_branch
            Cnd_True = no_params
            Act_DeactTrig = s_mid

            [Trigger]
            nickname = s_mid
            Cnd_Timer = 3
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 3
            """;

        var plan = Plan(text, "s_chosen");

        Assert.False(plan.CanStart);
        Assert.Equal(StartRefusal.NotActive, plan.Refusal);
        Assert.Equal("Trigger 's_mid' on the path to 's_chosen' is not active at that point, because another trigger " +
                     "stops it first. Pick another trigger.", plan.Reason);
    }

    /// <summary>When the path does not start the chosen trigger, the refusal names it once (review fix of M6c-2). Here a
    /// trigger that starts first in id order stops the chosen trigger before it starts.</summary>
    [Fact]
    public void AChosenTriggerThatThePathDoesNotStartRefusesWithItsOwnReason()
    {
        var killer = Enumerable.Range(0, 100).Select(n => $"s_kill{n}")
            .First(n => FLHash.CreateID(n) < FLHash.CreateID("s_chosen"));
        var text = $"""
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_mid

            [Trigger]
            nickname = s_mid
            Cnd_Timer = 3
            Act_ActTrig = {killer}
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = {killer}
            Cnd_True = no_params
            Act_DeactTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 3
            """;

        var plan = Plan(text, "s_chosen");

        Assert.Equal(StartRefusal.NotActive, plan.Refusal);
        Assert.Equal("The path to trigger 's_chosen' does not start it, because another line stops it or cancels its " +
                     "start. Pick another trigger.", plan.Reason);
    }

    [Fact]
    public void APathThatPassesATriggerWithoutAConditionRefuses()
    {
        const string text = """
            [Trigger]
            nickname = s_start
            InitState = ACTIVE
            Cnd_True = no_params
            Act_ActTrig = s_empty

            [Trigger]
            nickname = s_empty
            Act_ActTrig = s_chosen

            [Trigger]
            nickname = s_chosen
            Cnd_Timer = 3
            """;

        var plan = Plan(text, "s_chosen");

        Assert.Equal(StartRefusal.NoCondition, plan.Refusal);
        Assert.Equal("The path to trigger 's_chosen' passes trigger 's_empty', which has no condition. The game stops at " +
                     "that trigger. Add a condition to it.", plan.Reason);
    }

    [Fact]
    public void AnEmptyPathGivesOnlyTheChosenTriggerAndTheWarning()
    {
        var plan = Plan(Script, "t_orphan");

        Assert.True(plan.CanStart);
        Assert.Equal(StartKind.Approximate, plan.Kind);
        Assert.Equal(["t_orphan"], plan.ActiveSet);
        Assert.Empty(plan.Path);
        Assert.Contains(StartPlan.NoPathWarning("t_orphan"), plan.Warnings);
        Assert.Single(plan.Setup);
        Assert.Equal(["Cnd_True = no_params", "Act_ActTrig = t_orphan"], Lines(plan, 0));
        // Without a path, nothing names a place.
        Assert.Equal(StartPlaceKind.Camera, plan.Place.Kind);
    }

    [Fact]
    public void ASetupNameThatTheScriptHasByNameOrByGameIdGetsASuffix()
    {
        var byName = Script + """


            [Trigger]
            nickname = OL_PLAY_SETUP
            Cnd_Timer = 1
            """;
        Assert.Equal("ol_play_setup_2", Plan(byName, "t_chosen").Setup[0].Nickname);

        // Two names with one game id: the game keeps only the first trigger of the id (TR4).
        var (first, second) = SameId();
        var byId = Script + $"""


            [Trigger]
            nickname = {first}
            Cnd_Timer = 1
            """;
        Assert.Equal($"{second}_2", StartSetup.FreeName(Index(byId), second, []));
        Assert.Equal("ol_free", StartSetup.FreeName(Index(byId), "ol_free", []));
        Assert.Equal("ol_free_2", StartSetup.FreeName(Index(byId), "ol_free", ["ol_free"]));
    }

    /// <summary>Two names of trigger form with one game id, found by a search.</summary>
    private static (string First, string Second) SameId()
    {
        var seen = new Dictionary<uint, string>();
        for (var i = 0; ; i++)
        {
            var name = $"ol_t{i}";
            if (seen.TryGetValue(FLHash.CreateID(name), out var other))
                return (other, name);
            seen[FLHash.CreateID(name)] = name;
        }
    }

    [Fact]
    public void TheSetupPassesTheMissionChecksWithoutAnError()
    {
        var plan = Plan(Script, "t_chosen");
        var document = Document(Script + "\r\n\r\n" + plan.SetupPatch.Replace("ol.new = true\r\n", ""));

        var errors = MissionChecks.Run(MissionIndex.Build(document, MissionSchema.Retail, MissionWorld.Empty,
                FullCoverage.Instance))
            .Where(p => p.Severity == DiagnosticSeverity.Error);

        Assert.Empty(errors);
    }

    [Fact]
    public void ASetupLineWithANewErrorRefusesTheStartWithItsMessage()
    {
        var index = Index(Script);
        var plan = Plan(Script, "t_chosen");
        var broken = plan with { Setup = [new SetupTrigger("ol_play_setup", [("Cnd_True", ["no_params"]), ("Act_SetNNObj", ["ol_obj_none"])])] };

        var checkedPlan = StartSetup.Check(broken, index);

        Assert.Equal(StartRefusal.SetupProblem, checkedPlan.Refusal);
        Assert.StartsWith("A setup trigger has a problem: ", checkedPlan.Reason);
        Assert.Contains("ol_obj_none", checkedPlan.Reason);
    }

    [Fact]
    public void ASetupLineWithTheErrorOfItsSourceLineDoesNotRefuse()
    {
        // The objective of the path does not exist. The game ignores the line and its copy in the same way.
        var text = Script.Replace("nickname = ol_obj_1", "nickname = ol_obj_other");

        var plan = Plan(text, "t_chosen");

        Assert.True(plan.CanStart, plan.Reason);
        Assert.Contains("Act_SetNNObj = ol_obj_1", Lines(plan, 0));
    }

    /// <summary>A plan without the mission checks of the setup triggers (the Play buttons of the graph, note of 2026-10-27
    /// in section 8.1 of the M6c design) equals the full plan for each trigger of the script.</summary>
    [Fact]
    public void APlanWithoutTheSetupCheckEqualsTheFullPlan()
    {
        var index = Index(Script);

        foreach (var trigger in index.Triggers)
        {
            var start = MissionStartPoint.At(trigger.Nickname);
            var full = StartPlan.Make(index, Slot, start);
            var fast = StartPlan.Make(index, Slot, start, checkSetup: false);

            Assert.Equal((full.Kind, full.Refusal, full.Reason, full.Place), (fast.Kind, fast.Refusal, fast.Reason, fast.Place));
            Assert.Equal(full.ActiveSet, fast.ActiveSet);
            Assert.Equal(full.SavedTriggers, fast.SavedTriggers);
            Assert.Equal(full.SetupPatch, fast.SetupPatch);
            Assert.Equal(full.State.Title, fast.State.Title);
            Assert.Equal(full.State.Scenes, fast.State.Scenes);
            Assert.Equal(full.Warnings, fast.Warnings);
        }
    }

    /// <summary>
    /// Only the full plan runs the setup check (the Play buttons of the graph, note of 2026-10-27 in section 8.1 of the
    /// M6c design). The check refuses a setup line with a new error. The plan without the check keeps the line and can
    /// start. A line of the same script has the error of its source line, which the check accepts, so the replay of the
    /// test gets a state line of another script.
    /// </summary>
    [Fact]
    public void ThePlanWithoutTheSetupCheckSkipsTheCheck()
    {
        var index = Index(Script);
        var start = MissionStartPoint.At("t_chosen");
        var replay = StartReplay.Run(index, start);
        var other = Index(Script.Replace("Act_SetNNObj = ol_obj_1", "Act_SetNNObj = ol_obj_none"));
        var bad = other.Lines.Single(l => MissionIndex.Is(l.Key, "Act_SetNNObj"));
        var broken = replay with { Effects = [.. replay.Effects, (replay.Chosen!, bad)] };
        var plan = StartPlan.Make(index, Slot, start, checkSetup: false);
        var story = StartPlan.StoryPath(index, replay.Chosen);

        var full = StartSetup.Apply(plan, index, broken, story);
        var fast = StartSetup.Apply(plan, index, broken, story, check: false);

        Assert.Equal(StartRefusal.SetupProblem, full.Refusal);
        Assert.Contains("ol_obj_none", full.Reason);
        Assert.True(fast.CanStart, fast.Reason);
        Assert.Contains("Act_SetNNObj = ol_obj_none", Lines(fast, 0));
        Assert.DoesNotContain("Act_SetNNObj = ol_obj_1", Lines(fast, 0));
    }

    [Fact]
    public void AScriptWithoutASlotRefuses()
    {
        var plan = StartPlan.Make(Index(Script), null, MissionStartPoint.At("t_chosen"));

        Assert.Equal((StartKind.TestScript, StartRefusal.NoSlot), (plan.Kind, plan.Refusal));
        Assert.Equal(StartPlan.NoSlotReason, plan.Reason);
    }

    [Fact]
    public void AMissingTriggerRefuses()
    {
        Assert.Equal(StartRefusal.NoTrigger, Plan(Script, "t_none").Refusal);
    }

    [Fact]
    public void TheSetupPatchHasTheTwoNewSectionsAtTheEnd()
    {
        var patch = Plan(Script, "t_chosen").SetupPatch;

        Assert.StartsWith("[Trigger]\r\nnickname = ol_play_setup\r\nol.new = true\r\nCnd_True = no_params\r\n", patch);
        Assert.Contains("\r\n\r\n[Trigger]\r\nnickname = ol_play_world\r\nol.new = true\r\nCnd_SpaceEnter = no_params\r\n",
            patch);
    }

    [Fact]
    public void ThePlanIsTheSameForTheSameIndex()
    {
        var index = Index(Script);
        var a = StartPlan.Make(index, Slot, MissionStartPoint.At("t_chosen"));
        var b = StartPlan.Make(index, Slot, MissionStartPoint.At("T_CHOSEN"));

        Assert.Equal(a.ActiveSet, b.ActiveSet);
        Assert.Equal(a.SetupPatch, b.SetupPatch);
        Assert.Equal(a.Start, b.Start);
    }

    [Fact]
    public void AStartPointFollowsARenameWithoutLetterCase()
    {
        var start = MissionStartPoint.At("t_chosen");

        Assert.Equal(MissionStartPoint.At("t_new"), start.Renamed("T_CHOSEN", "t_new"));
        Assert.Same(start, start.Renamed("t_other", "t_new"));
        Assert.Same(MissionStartPoint.MissionStart, MissionStartPoint.MissionStart.Renamed("t_chosen", "t_new"));
        Assert.Throws<ArgumentException>(() => MissionStartPoint.At(" "));
    }
}
