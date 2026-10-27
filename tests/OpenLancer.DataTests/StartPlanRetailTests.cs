using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Runtime.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the start plan (sections 5.3 and 12.3 of the M6c design) over the 14 story scripts of the base game.
/// The tests count and compare triggers. They hold no retail text.
/// </summary>
public sealed class StartPlanRetailTests(ITestOutputHelper output)
{
    private static readonly Lazy<MissionRunResult> Hde = new(() =>
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireHde(), "m6cplanhde");
        return MissionRun.Execute(workspace.Open().MissionInputs(EngineCoverage.Instance));
    });

    private static IReadOnlyList<MissionAnalysis> Slots(MissionRunResult run) =>
        run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!).ToList();

    private static IReadOnlyList<MissionAnalysis> Scripts() => Slots(RetailMissions.Run());

    private static StartPlan Plan(MissionAnalysis analysis, MissionTrigger trigger) =>
        StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.At(trigger.Nickname));

    [Fact]
    public void EachRestoreTriggerGivesAnExactCheckpointStart()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var restore in DryRun.Checkpoints(analysis.Index))
            {
                var plan = Plan(analysis, restore);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {restore.Nickname}: {plan.Reason}");
                Assert.Equal(StartKind.Checkpoint, plan.Kind);
                // No retail restore trigger has a condition that saves a state, so the save names it alone.
                Assert.Equal([restore.Nickname], plan.SavedTriggers);
                Assert.Empty(plan.Setup);
                Assert.Contains(StartPlan.CheckpointStateWarning(restore.Nickname), plan.Warnings);
                count++;
            }
        }

        Assert.Equal(34, count);
    }

    /// <summary>The same for the 14 HD Edition scripts (section 12.3 of the M6c design): each of the 33 restore triggers
    /// gives an exact checkpoint start, and each start trigger gives a mission start.</summary>
    [Fact]
    public void EachHdRestoreTriggerAndStartTriggerGivesAnExactStart()
    {
        GameDataPaths.RequireHde();
        var count = 0;
        foreach (var analysis in Slots(Hde.Value))
        {
            foreach (var restore in DryRun.Checkpoints(analysis.Index))
            {
                var plan = Plan(analysis, restore);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {restore.Nickname}: {plan.Reason}");
                Assert.Equal(StartKind.Checkpoint, plan.Kind);
                Assert.Equal([restore.Nickname], plan.SavedTriggers);
                Assert.Empty(plan.Setup);
                count++;
            }

            foreach (var start in analysis.Index.StartTriggers)
            {
                var plan = Plan(analysis, start);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {start.Nickname}: {plan.Reason}");
                Assert.Equal(StartKind.MissionStart, plan.Kind);
            }
        }

        Assert.Equal(33, count);
    }

    [Fact]
    public void EachStartTriggerGivesAMissionStart()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var start in analysis.Index.StartTriggers)
            {
                var plan = Plan(analysis, start);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {start.Nickname}: {plan.Reason}");
                Assert.Equal(StartKind.MissionStart, plan.Kind);
                Assert.Empty(plan.SavedTriggers);
                Assert.False(plan.Accepted);
                count++;
            }
        }

        Assert.Equal(16, count);
    }

    /// <summary>Each of the 14 restore triggers with Cnd_PlayerLaunch has a place line, and the plan starts in space at
    /// its point.</summary>
    [Fact]
    public void EachPlayerLaunchRestoreTriggerGivesASpaceStartAtThePointOfItsPlaceLine()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var restore in DryRun.Checkpoints(analysis.Index).Where(t => t.ConditionIs("Cnd_PlayerLaunch")))
            {
                var line = restore.Actions.First(a =>
                    a.Key.Equals("Act_MovePlayer", StringComparison.OrdinalIgnoreCase) ||
                    a.Key.Equals("Act_SetInitialPlayerPos", StringComparison.OrdinalIgnoreCase));
                var point = new Vector3(Number(line, 0), Number(line, 1), Number(line, 2));

                var place = Plan(analysis, restore).Place;

                Assert.Equal(StartPlaceKind.Space, place.Kind);
                Assert.Equal(point, place.Position);
                Assert.NotNull(place.System);
                count++;
            }
        }

        Assert.Equal(14, count);
    }

    /// <summary>For every tenth trigger that a start trigger reaches, in file order, the approximate plan has no error
    /// of the mission checks in its setup triggers. A plan can refuse for another reason, for example a stop on another
    /// branch.</summary>
    [Fact]
    public void EveryTenthReachedTriggerHasAnApproximatePlanWithoutACheckError()
    {
        var kinds = new Dictionary<string, int>();
        foreach (var analysis in Scripts())
        {
            var index = analysis.Index;
            var exact = index.StartTriggers.Concat(DryRun.Checkpoints(index)).ToHashSet();
            var reached = index.Triggers.Where(t => index.Reached.Contains(t) && !t.IsDuplicate).ToList();
            for (var i = 0; i < reached.Count; i += 10)
            {
                if (exact.Contains(reached[i]))
                    continue;
                var plan = Plan(analysis, reached[i]);
                Assert.True(plan.Refusal != StartRefusal.SetupProblem,
                    $"{analysis.Script.Slot!.Slot} {reached[i].Nickname}: {plan.Reason}");
                if (plan.CanStart)
                    Assert.Equal(StartKind.Approximate, plan.Kind);
                var key = plan.CanStart ? "ready" : plan.Refusal.ToString();
                kinds[key] = kinds.GetValueOrDefault(key) + 1;
            }
        }

        output.WriteLine(string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
        Assert.True(kinds.GetValueOrDefault("ready") > 200, "Too few approximate plans can start.");
    }

    /// <summary>A trigger that no start trigger and no restore trigger reaches over Start links gives a plan with only
    /// that trigger and the warning.</summary>
    [Fact]
    public void EachTriggerWithoutAPathGivesAPlanWithOnlyThatTrigger()
    {
        var count = 0;
        foreach (var analysis in Scripts())
        {
            var index = analysis.Index;
            var roots = index.StartTriggers.Concat(DryRun.Checkpoints(index)).ToList();
            var linked = Reach(index, roots);
            foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate && !linked.Contains(t)))
            {
                var plan = Plan(analysis, trigger);
                Assert.True(plan.CanStart, $"{analysis.Script.Slot!.Slot} {trigger.Nickname}: {plan.Reason}");
                Assert.Equal([trigger.Nickname], plan.ActiveSet);
                Assert.Contains(StartPlan.NoPathWarning(trigger.Nickname), plan.Warnings);
                count++;
            }
        }

        output.WriteLine($"{count} triggers have no path.");
        Assert.True(count > 0);
    }

    /// <summary>
    /// The suggested place of each story trigger of the base game starts where the player is in the retail game (fix
    /// of 2026-10-27). The bug was a base start after the path had left the base. A base or camera start is suspect when
    /// a later trigger of the story path has a launch, an arrival or a space condition. Only the named rows below can be
    /// suspect.
    /// </summary>
    [Fact]
    public void EachStoryTriggerOfTheBaseGameStartsWhereThePlayerIs() =>
        CheckEachStoryTrigger(Scripts(), "base game", 53);

    /// <summary>The same for the 14 HD Edition scripts. HD Edition removed most debug triggers that move the player, so
    /// more starts have no point after a jump.</summary>
    [Fact]
    public void EachHdStoryTriggerStartsWhereThePlayerIs()
    {
        GameDataPaths.RequireHde();
        CheckEachStoryTrigger(Slots(Hde.Value), "HD Edition", 159);
    }

    /// <summary>The places of the design rows (section 5.3, step 7 of the M6c design, fix of 2026-10-27), with the points
    /// read from the script lines.</summary>
    [Fact]
    public void TheDesignRowsOfTheBaseGameHaveTheirPlaces()
    {
        var scripts = Scripts();
        var m02 = Slot(scripts, "M02");
        // The world of the scripts knows the system of each base.
        Assert.Equal("Li01", m02.Index.World.SystemOfBase("li01_01_base"), ignoreCase: true);
        var debris = LinePoint(m02, "key_save_fly_to_debris", "Act_SetInitialPlayerPos");
        foreach (var trigger in new[] { "flytodebris_init", "in_range" })
        {
            var place = Suggested(scripts, "M02", trigger);
            Assert.Equal(("Li03", debris), (place.System, place.Position));
            Assert.Equal(LineHeading(m02, "key_save_fly_to_debris"), place.Orientation);
            Assert.Equal("Act_SetInitialPlayerPos of key_save_fly_to_debris.", place.Source);
        }
        // The debug trigger fly_to_debris moves the player to the same point.
        Assert.Equal(debris, LinePoint(m02, "fly_to_debris", "Act_MovePlayer"));

        foreach (var trigger in new[] { "lce_bar_li01_01", "mrp_accept", "spe_li01_01", "exit_base", "after_launch" })
            Assert.Equal((StartPlaceKind.Base, "Li01_01_base"), Docked(scripts, "M02", trigger));

        SpaceAt(scripts, "M02", "jumpcolorado_end", "Li01",
            ConditionPoint(m02, "flytocolorado_end") + new Vector3(0, 0, StartPlaces.Offset));
        SpaceAt(scripts, "M02", "practicescan_init", "Li03", LinePoint(m02, "practice_scan", "Act_MovePlayer"));
        SpaceAt(scripts, "M02", "wait_for_player", "Li01", LinePoint(m02, "fly_to_colorado", "Act_MovePlayer"));
        SpaceAt(scripts, "M03", "make_gamma3_follow_convoy", "Li02",
            LinePoint(Slot(scripts, "M03"), "key_to_enc1", "Act_MovePlayer"));
        SpaceAt(scripts, "M05", "chase_player", "bw01",
            LinePoint(Slot(scripts, "M05"), "key_leaving_sprague", "Act_MovePlayer"));
        SpaceAt(scripts, "M01a", "d_order", "Li01", LinePoint(Slot(scripts, "M01a"), "saveit", "Act_RelocateShip", 1));
        SpaceAt(scripts, "M06", "com_0950", "Br05", LinePoint(Slot(scripts, "M06"), "start", "Act_RelocateShip", 1));
        SpaceAt(scripts, "M07", "wait_for_us_to_attack_enemy", "Br04",
            LinePoint(Slot(scripts, "M07"), "launch_key_save_after_stokes", "Act_SetInitialPlayerPos"));
        SpaceAt(scripts, "M10", "spawn_ex_7", "Rh04",
            LinePoint(Slot(scripts, "M10"), "save_post-bund", "Act_SetInitialPlayerPos"));
        SpaceAt(scripts, "M11", "dst_tw_jgate", "Li05", LinePoint(Slot(scripts, "M11"), "AKescape", "Act_MovePlayer"));
        CommonRows(scripts);

        // A debug trigger without a system hint takes the system of the story at its step.
        Assert.Equal("Br03", Suggested(scripts, "M05", "key_to_sprague").System);
        // A debug trigger whose condition names another system than its hint warns.
        var m04 = Slot(scripts, "M04");
        Assert.Contains(StartPlaces.OtherSystemWarning("key_save_flee_vanpelt", "Li02", "Li01"),
            Plan(m04, m04.Index.Trigger("key_save_flee_vanpelt")!).Warnings);
    }

    /// <summary>The design rows of the HD Edition scripts. HD Edition has no debug trigger for these steps, so the
    /// checkpoints give the points.</summary>
    [Fact]
    public void TheDesignRowsOfTheHdScriptsHaveTheirPlaces()
    {
        GameDataPaths.RequireHde();
        var scripts = Slots(Hde.Value);
        var m02 = Slot(scripts, "M02");
        var debris = LinePoint(m02, "key_save_fly_to_debris", "Act_SetInitialPlayerPos");
        SpaceAt(scripts, "M02", "in_range", "Li03", debris);
        SpaceAt(scripts, "M02", "flytodebris_init", "Li03", debris);
        foreach (var trigger in new[] { "lce_bar_li01_01", "mrp_accept", "spe_li01_01", "exit_base", "after_launch" })
            Assert.Equal((StartPlaceKind.Base, "Li01_01_base"), Docked(scripts, "M02", trigger));
        SpaceAt(scripts, "M02", "jumpcolorado_end", "Li01",
            ConditionPoint(m02, "flytocolorado_end") + new Vector3(0, 0, StartPlaces.Offset));
        SpaceAt(scripts, "M03", "make_gamma3_follow_convoy", "Li02",
            LinePoint(Slot(scripts, "M03"), "launch_key_save_passage_entrance", "Act_SetInitialPlayerPos"));
        SpaceAt(scripts, "M05", "chase_player", "bw01",
            LinePoint(Slot(scripts, "M05"), "key_save_leaving_sprague", "Act_SetInitialPlayerPos"));
        SpaceAt(scripts, "M01a", "d_order", "Li01", LinePoint(Slot(scripts, "M01a"), "saveit", "Act_RelocateShip", 1));
        SpaceAt(scripts, "M07", "wait_for_us_to_attack_enemy", "Br04",
            LinePoint(Slot(scripts, "M07"), "launch_key_save_after_stokes", "Act_SetInitialPlayerPos"));
        SpaceAt(scripts, "M10", "spawn_ex_7", "Rh04",
            LinePoint(Slot(scripts, "M10"), "save_post-bund", "Act_SetInitialPlayerPos"));
        SpaceAt(scripts, "M11", "dst_tw_jgate", "Li05",
            LinePoint(Slot(scripts, "M11"), "key_save_Akescape", "Act_SetInitialPlayerPos"));
        CommonRows(scripts);
    }

    /// <summary>The rows of the design and of its reviews that both data sets share.</summary>
    private static void CommonRows(IReadOnlyList<MissionAnalysis> scripts)
    {
        // The two M07 checkpoints in two stages take the system of their second stage.
        Assert.Equal("Bw10", Suggested(scripts, "M07", "launch_key_save_to_broken_tradelane").System);
        Assert.Equal("Bw09", Suggested(scripts, "M07", "launch_key_save_outside_shinkaku").System);
        // A trigger that waits for a jump starts before the jump, at the point that waits for the player near the hole.
        SpaceAt(scripts, "M10", "sye_Rh02", "Rh04",
            ConditionPoint(Slot(scripts, "M10"), "near_the_jump_hole") + new Vector3(0, 0, StartPlaces.Offset));
        // A trigger that waits at a base for the launch starts docked at that base.
        Assert.Equal((StartPlaceKind.Base, "Li01_01_base"), Docked(scripts, "M04", "flee_to_debris_spe"));
        Assert.Equal((StartPlaceKind.Base, "st01_01_base"), Docked(scripts, "M13", "forceland"));
        // A launch without a point in its system starts at the base of the launch.
        Assert.Equal((StartPlaceKind.Base, "Li01_01_base"), Docked(scripts, "M03", "tr_0100_juni"));
        // A trigger that waits for the end of the launch starts docked, and the starts after the launch warn about it.
        Assert.Equal((StartPlaceKind.Base, "Br05_01_base"), Docked(scripts, "M06", "lcp_start"));
        var m06 = Slot(scripts, "M06");
        Assert.Contains(StartPlaces.LaunchWatcherWarning("lcp_start"), Plan(m06, m06.Index.Trigger("com_0950")!).Warnings);
    }

    /// <summary>A time gate: the walk of the suggested place over each trigger of the biggest script, after a warm-up.
    /// The replay of the plan is not part of the gate.</summary>
    [Fact]
    public void TheSuggestedPlaceOfEachTriggerOfM10IsFast()
    {
        var m10 = Slot(Scripts(), "M10");
        var replays = m10.Index.Triggers.Where(t => !t.IsDuplicate)
            .Select(t => StartReplay.Run(m10.Index, MissionStartPoint.At(t.Nickname)))
            .Where(r => r.Refusal == StartRefusal.None).ToList();
        StartPlaces.Suggest(m10.Index, replays[0]);

        var times = replays.Select(replay =>
        {
            var watch = Stopwatch.StartNew();
            StartPlaces.Suggest(m10.Index, replay);
            return watch.Elapsed.TotalMilliseconds;
        }).Order().ToList();

        var median = times[times.Count / 2];
        output.WriteLine($"{times.Count} places: median {median:F2} ms, max {times[^1]:F2} ms, total {times.Sum():F0} ms");
        Assert.True(median < 5, $"The median place took {median:F2} ms.");
        Assert.True(times.Sum() < 4000, $"The places of M10 took {times.Sum():F0} ms.");
    }

    /// <summary>The check of each story trigger of a data set (see
    /// <see cref="EachStoryTriggerOfTheBaseGameStartsWhereThePlayerIs"/>). Each suggestion names a system or a base that
    /// the game data has.</summary>
    /// <param name="suspects">The number of suspect rows that the named lists allow in this data set.</param>
    private void CheckEachStoryTrigger(IReadOnlyList<MissionAnalysis> scripts, string name, int suspects)
    {
        var kinds = new Dictionary<StartPlaceKind, int>();
        var allowed = new List<string>();
        var wrong = new List<string>();
        foreach (var analysis in scripts)
        {
            var index = analysis.Index;
            var slot = analysis.Script.Slot!.Slot;
            foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate))
            {
                var plan = Plan(analysis, trigger);
                if (!plan.CanStart)
                    continue;
                var place = plan.Suggested;
                kinds[place.Kind] = kinds.GetValueOrDefault(place.Kind) + 1;
                var row = $"{slot} {trigger.Nickname}";
                if (place.System is { } system)
                    Assert.True(index.World.Has(MissionValueKind.System, system), $"{row}: {place.Text}");
                if (place.Base is { } dock)
                    Assert.True(index.World.Has(MissionValueKind.Base, dock), $"{row}: {place.Text}");
                if (place.Kind == StartPlaceKind.Space)
                    Assert.True(float.IsFinite(place.Position.Length()), row);
                if (place.Kind == StartPlaceKind.Space ||
                    !SpaceAfterThePlace(StartPlan.StoryPath(index, trigger), trigger, place))
                    continue;
                if (AllowedSuspect(row, place.Source))
                    allowed.Add(row);
                else
                    wrong.Add($"{row}: {place.Text}. {place.Source}");
            }
        }

        output.WriteLine($"{name}: {string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}, " +
                         $"{allowed.Count} allowed suspect rows");
        Assert.Empty(wrong);
        Assert.Equal(suspects, allowed.Count);
    }

    /// <summary>The rule of the survey of 2026-10-27. A trigger of the story path after the trigger of the place has a
    /// launch, an arrival or a space condition with the player. For a Scene camera, the check starts at the start of the
    /// path. The chosen trigger does not count.</summary>
    private static bool SpaceAfterThePlace(IReadOnlyList<MissionTrigger> story, MissionTrigger chosen, StartPlace place)
    {
        var from = place is { Kind: StartPlaceKind.Base, Trigger: { } at }
            ? story.ToList().FindIndex(t => MissionIndex.Is(t.Nickname, at)) + 1
            : 0;
        return story.Skip(from).Where(t => t != chosen).Any(t => t.Condition is { } condition &&
            (SpaceAlways.Any(k => MissionIndex.Is(condition.Key, k)) ||
             SpaceWithPlayer.Any(k => MissionIndex.Is(condition.Key, k)) &&
             Enumerable.Range(0, condition.Count).Any(i => MissionIndex.Is(condition.Value(i), "Player"))));
    }

    private static readonly string[] SpaceAlways =
        ["Cnd_SpaceEnter", "Cnd_BaseExit", "Cnd_LaunchComplete", "Cnd_JumpInComplete", "Cnd_SystemEnter"];

    private static readonly string[] SpaceWithPlayer =
        ["Cnd_NPCSystemEnter", "Cnd_TLEntered", "Cnd_TLExited", "Cnd_DistShip", "Cnd_DistVec"];

    /// <summary>True when a suspect row is in the named list of its reason, and its source gives that reason.</summary>
    private static bool AllowedSuspect(string row, string source) =>
        LaunchEnd.Contains(row) && source.EndsWith("waits for the end of a launch, so the play starts docked.",
            StringComparison.Ordinal) ||
        AtTheBaseOfTheLaunch.Contains(row) && source.Contains(". No line of the path gives a point in ",
            StringComparison.Ordinal) && source.Contains(" after ", StringComparison.Ordinal) ||
        CameraWithoutAPoint.Contains(row) && source.Contains("No line of the path gives a point in ", StringComparison.Ordinal);

    /// <summary>
    /// The chosen trigger waits for the end of a launch (Cnd_LaunchComplete of the player). Only a launch from a dock
    /// ends a launch, in retail and in OpenLancer. So the start is docked at the base of the launch, and the launch runs
    /// the trigger.
    /// </summary>
    private static readonly HashSet<string> LaunchEnd =
    [
        "M02 after_launch", "M04 enter_space", "M05 lcp_Br03_01", "M06 lcp_start", "M08 ku01_init",
        .. Rows("M09", "lcp_ku06_01 lcp_ku07_01"),
        .. Rows("M10", "timer_sendcomm_dx_m10_1260_fischer player_follows_von_claussen"),
        .. Rows("M13", "launch_complete stop_player wait_until_after_launch"),
    ];

    /// <summary>
    /// No line of the path gives a point in the system after the launch. The start is docked at the base of the launch.
    /// The dock is the point of the launch in the retail game, and the launch runs the launch triggers again. The center
    /// of a system is never a start point, because most suns are there.
    /// </summary>
    private static readonly HashSet<string> AtTheBaseOfTheLaunch =
    [
        .. Rows("M03", "tr_0100_juni com_0100_juni commcomplete_dx_m03_0120_juni hsm_yes commcomplete_DX_M03_0130_TRENT " +
                       "commcomplete_DX_M03_0140_JUNI hsm_no commcomplete_DX_M03_0150_TRENT commcomplete_DX_M03_0160_JUNI " +
                       "syx_ny sye_ca loaded_from_autosave play_brandon_scene sye_ca_repeatable " +
                       "dialog_toward_ca_minor_complete dialog_at_ca_minor_complete"),
        // HD Edition: all of them. The base game: only tr_to_sprague, because debug triggers give the other points.
        .. Rows("M05", "tr_to_sprague tr_stop_player first_com com_0100 tle_Br03_45 com_0110 com_0120 tl_exit " +
                       "com_0125_Norfolk com_0126 com_0127_Norfolk com_0130 tle_Br03_29 tl_23_exit delay_0140 syx_br03_bw01 " +
                       "sye_bw01"),
        // HD Edition: all of them. The base game: only Cam2_Timer and Cam3_Timer.
        .. Rows("M07", "Cam2_Timer Cam3_Timer tr_to_space rh_agents_detected engage_rheinlanders_outside_leeds " +
                       "rh_agent1_dead rh_agent2_dead agent_runs1 commcomplete_DX_M07_0180_RH_AGENT_01 " +
                       "commcomplete_DX_M07_0200_JUNI"),
        // HD Edition only.
        .. Rows("M09", "commcomplete_DX_M09_0100_OZU start_travel_to_jumphole_dialog commcomplete_DX_M09_0110_JUNI " +
                       "commcomplete_DX_M09_0120_OZU commcomplete_DX_M09_0130_JUNI commcomplete_DX_M09_0140_OZU " +
                       "commcomplete_DX_M09_0150_JUNI commcomplete_DX_M09_0160_OZU start_near_jumphole_dialog " +
                       "start_at_jumphole_dialog syx_Ku06 jumped_to_ku07 wait_for_ozu_in_ku07"),
    ];

    /// <summary>
    /// No line of the path gives a point in the system, and no line names a base of a launch. The path comes after a
    /// jump, or after a launch from a base that the mission does not name. The start is at the Scene camera in that
    /// system. Only the game data knows the arrival gate or hole of a jump (open point (c) of the design).
    /// </summary>
    private static readonly HashSet<string> CameraWithoutAPoint =
    [
        "M01b launch_complete_RTC", "M06 tm_junispeaks", "M06 com_0100", "M13 clear_waypoint", "M13 fail_range",
        .. Rows("M03", "syx_ca perform_dialog_toward_ca_minor dialog_toward_ca_minor perform_dialog_at_ca_minor " +
                       "dialog_at_ca_minor give_obj_to_dock_on_ca_minor"),
        // HD Edition: also the two comm lines.
        .. Rows("M04", "juni_near_player sendcomm_DX_M04_1370_JUNI sendcomm_DX_M04_1380_JUNI"),
        // HD Edition only. The base game has debug triggers with points in Br03 and Br04.
        .. Rows("M05", "syx_br03_br04 sye_br03_init sinclair_com com_0720 delay_next_juni_com com_0670 com_0680 com_0690 " +
                       "delay_traitors_appearance traitors_decloak com_0730 com_740 com_750 com_760 com_770 com_780 com_790 " +
                       "com_800 com_0810 dst_save_bship head_to_battleship delay_com_830 com_830 nse_juni_br04 " +
                       "sye_br04_init com_960 com_970 com_980 com_990 com_1000 dst_to_br04_tradelane com_1029 com_1030 " +
                       "player_near_br04_01"),
        // HD Edition only. The base game has debug triggers with points in Bw10, Bw08 and Ku03.
        .. Rows("M07", "tr_to_tau31 commcomplete_DX_M07_0595_JUNI tl_enter_to_holman tl_exit_at_holman " +
                       "commcomplete_DX_M07_0650_TOBIAS juni_cautious tr_to_tau23 make_outcast_base_friendly " +
                       "commcomplete_DX_M07_1590_RAZOR_1 close_to_outcast_base commcomplete_DX_M07_1620_CALI " +
                       "commcomplete_DX_M07_1630_RAZOR_1 commcomplete_DX_M07_1640_CALI commcomplete_DX_M07_1650_RAZOR_1 " +
                       "tr_to_ku03 commcomplete_DX_M07_1730_JUNI start_travel_to_tsushima_dialog " +
                       "commcomplete_DX_M07_1740_JUNI start_near_kyushu_dialog commcomplete_DX_M07_1750_JUNI " +
                       "start_at_kyushu_dialog commcomplete_DX_M07_1760_JUNI"),
        // HD Edition: also the rows from tr_to_ku06.
        .. Rows("M09", "tr_to_ku07 commcomplete_DX_M09_0210_OZU half_way_to_blood_dragon_base at_blood_dragon_base " +
                       "near_blood_dragon_base commcomplete_DX_M09_0380_OZU commcomplete_DX_M09_0390_RYUKU " +
                       "commcomplete_DX_M09_0391_RYUKU commcomplete_DX_M09_0410_OZU commcomplete_DX_M09_0401_RYUKU " +
                       "tr_to_ku06 commcomplete_DX_M09_0740_HAKKERA Akira_sights_dragon_base restart_hakkera_patrol_path " +
                       "activate_look_to_restart_hakkera_path restart_juni_patrol_path activate_look_to_restart_juni_path " +
                       "commcomplete_DX_M09_0750_HAKKERA commcomplete_DX_M09_0760_KYOTO commcomplete_DX_M09_0770_HAKKERA " +
                       "commcomplete_DX_M09_0780_KYOTO"),
    ];

    private static IEnumerable<string> Rows(string slot, string triggers) =>
        triggers.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => $"{slot} {t}");

    private static MissionAnalysis Slot(IReadOnlyList<MissionAnalysis> scripts, string slot) =>
        scripts.Single(a => a.Script.Slot!.Slot == slot);

    private static StartPlace Suggested(IReadOnlyList<MissionAnalysis> scripts, string slot, string trigger)
    {
        var analysis = Slot(scripts, slot);
        var plan = Plan(analysis, analysis.Index.Trigger(trigger)!);
        Assert.True(plan.CanStart, $"{slot} {trigger}: {plan.Reason}");
        return plan.Suggested;
    }

    private static (StartPlaceKind, string?) Docked(IReadOnlyList<MissionAnalysis> scripts, string slot, string trigger)
    {
        var place = Suggested(scripts, slot, trigger);
        return (place.Kind, place.Base);
    }

    private static void SpaceAt(IReadOnlyList<MissionAnalysis> scripts, string slot, string trigger, string system,
        Vector3 point)
    {
        var place = Suggested(scripts, slot, trigger);
        Assert.True(place.Kind == StartPlaceKind.Space, $"{slot} {trigger}: {place.Text}. {place.Source}");
        Assert.Equal(system, place.System, ignoreCase: true);
        Assert.Equal(point, place.Position);
    }

    /// <summary>The point of the first line of a key of a trigger, from a value index.</summary>
    private static Vector3 LinePoint(MissionAnalysis analysis, string trigger, string key, int from = 0)
    {
        var line = analysis.Index.Trigger(trigger)!.Actions.First(a => MissionIndex.Is(a.Key, key));
        return new Vector3(Number(line, from), Number(line, from + 1), Number(line, from + 2));
    }

    /// <summary>The heading of the Act_SetInitialPlayerPos line of a trigger: w, x, y, z after the point.</summary>
    private static Quaternion LineHeading(MissionAnalysis analysis, string trigger)
    {
        var line = analysis.Index.Trigger(trigger)!.Actions.First(a => MissionIndex.Is(a.Key, "Act_SetInitialPlayerPos"));
        return Quaternion.Normalize(new Quaternion(Number(line, 4), Number(line, 5), Number(line, 6), Number(line, 3)));
    }

    /// <summary>The point of the Cnd_DistVec condition of a trigger.</summary>
    private static Vector3 ConditionPoint(MissionAnalysis analysis, string trigger)
    {
        var condition = analysis.Index.Trigger(trigger)!.Condition!;
        Assert.True(MissionIndex.Is(condition.Key, "Cnd_DistVec"), condition.Key);
        return new Vector3(Number(condition, 2), Number(condition, 3), Number(condition, 4));
    }

    /// <summary>The triggers that the roots reach over Start links, the roots included.</summary>
    private static HashSet<MissionTrigger> Reach(MissionIndex index, IReadOnlyList<MissionTrigger> roots)
    {
        var next = index.Links.Where(l => l.Kind == MissionLinkKind.Start && l.Target is { IsDuplicate: false })
            .ToLookup(l => l.Source, l => l.Target!);
        var seen = roots.ToHashSet();
        var queue = new Queue<MissionTrigger>(roots);
        while (queue.Count > 0)
        {
            foreach (var child in next[queue.Dequeue()])
            {
                if (seen.Add(child))
                    queue.Enqueue(child);
            }
        }
        return seen;
    }

    private static float Number(MissionLine line, int index) =>
        float.Parse(line.Value(index)!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
