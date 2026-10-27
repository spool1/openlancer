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
/// The tests count and compare triggers. They name the retail triggers of the design rows and of a few examples, and
/// read their points from the script lines. They hold no other retail text.
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
    /// a later trigger of the story path has a launch, an arrival or a space condition. A suspect row must give one of
    /// the reasons of <see cref="Reason"/> in its source text. The test counts the rows of each reason. It also counts
    /// the suggestions at the Scene camera in one system, because Play refuses them while the Scene shows another system.
    /// </summary>
    [Fact]
    public void EachStoryTriggerOfTheBaseGameStartsWhereThePlayerIs() =>
        CheckEachStoryTrigger(Scripts(), "base game", new Dictionary<Reason, int>
        {
            [Reason.LaunchEnd] = 12,
            [Reason.AtTheBaseOfTheLaunch] = 18,
            [Reason.CameraWithoutAPoint] = 3,
        }, 4);

    /// <summary>The same for the 14 HD Edition scripts. HD Edition removed most debug triggers that move the player, so
    /// more starts have no point after a launch.</summary>
    [Fact]
    public void EachHdStoryTriggerStartsWhereThePlayerIs()
    {
        GameDataPaths.RequireHde();
        CheckEachStoryTrigger(Slots(Hde.Value), "HD Edition", new Dictionary<Reason, int>
        {
            [Reason.LaunchEnd] = 12,
            [Reason.AtTheBaseOfTheLaunch] = 44,
            [Reason.CameraWithoutAPoint] = 3,
        }, 4);
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
        // Review fix: HD Edition parks the ship of a comm voice far away, so the jump gate of the jump comes before the
        // spawns of the mission.
        AtGate(scripts, "M02", "practicescan_init", "Li03", "Li01");
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

        // The rows of the reviews of the fix. A Cnd_True step of a restore trigger gives the place of the checkpoint.
        SpaceAt(scripts, "M01a", "w_tutorialsave", "Li01", LinePoint(Slot(scripts, "M01a"), "w_tutorial", "Act_MovePlayer"));
        Assert.Equal("Act_MovePlayer of w_tutorial, which w_tutorialsave starts.",
            Suggested(scripts, "M01a", "w_tutorialsave").Source);
        // A Cnd_DistShip line near an object of the game data gives a point near that object. So the checkpoint at the
        // base of the Blood Dragons starts near that base, and not at a generator that the restore trigger makes hostile.
        NearObjectOf(scripts, "M09", "key_save_at_ryuku", "near_blood_dragon_base");
        NearObjectOf(scripts, "M04", "key_to_br04_init", "close_to_leeds_jump_hole");
        // The path to these triggers already made the jump that they wait for. So the start is after the jump, at the jump
        // gate of the jump, and the page warns.
        AtGate(scripts, "M03", "play_brandon_scene", "Li02", "Li01");
        var m10 = Slot(scripts, "M10");
        Assert.Equal("Rh04", Suggested(scripts, "M10", "nse_jonnerpal_Rh04").System, ignoreCase: true);
        Assert.Contains(StartPlaces.ArrivedWarning("nse_jonnerpal_Rh04", "Rh04"),
            Plan(m10, m10.Index.Trigger("nse_jonnerpal_Rh04")!).Warnings);
        // A spawn that the path makes hostile to the player gives no point.
        Assert.DoesNotContain("bad_wing", Suggested(scripts, "M13", "general_triggers").Source);
        // A start trigger without a place starts at the Scene camera in any system, as the mission start does.
        Assert.Equal((StartPlaceKind.Camera, null), (Suggested(scripts, "M04", "start").Kind,
            Suggested(scripts, "M04", "start").System));
    }

    /// <summary>Checks a start near the object of the Cnd_DistShip line of a trigger: at the edge of its range from the
    /// object, at least the offset away.</summary>
    private static void NearObjectOf(IReadOnlyList<MissionAnalysis> scripts, string slot, string trigger, string near)
    {
        var condition = Slot(scripts, slot).Index.Trigger(near)!.Condition!;
        Assert.True(MissionIndex.Is(condition.Key, "Cnd_DistShip"), condition.Key);
        var name = MissionIndex.Is(condition.Value(1), "Player") ? condition.Value(2)! : condition.Value(1)!;
        var item = Slot(scripts, slot).Index.World.ObjectOf(name)!;
        SpaceAt(scripts, slot, trigger, item.System,
            item.Position + new Vector3(0, 0, Math.Max(StartPlaces.Offset, Number(condition, 3))));
        Assert.Equal($"Cnd_DistShip of {near}.", Suggested(scripts, slot, trigger).Source);
    }

    /// <summary>Checks a start at the jump gate or hole of a system that leads to the system before the jump.</summary>
    private static void AtGate(IReadOnlyList<MissionAnalysis> scripts, string slot, string trigger, string system,
        string from)
    {
        var gate = Slot(scripts, slot).Index.World.JumpInto(system, from)!;
        SpaceAt(scripts, slot, trigger, system,
            gate.Position + Vector3.Transform(new Vector3(0, 0, StartPlaces.Offset), gate.Rotation));
        Assert.EndsWith($"units from {gate.Nickname}, the jump gate or hole to {from}.", Suggested(scripts, slot, trigger).Source);
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
    /// <param name="expected">The number of suspect rows of each reason in this data set.</param>
    /// <param name="cameras">The number of suggestions at the Scene camera in one system.</param>
    private void CheckEachStoryTrigger(IReadOnlyList<MissionAnalysis> scripts, string name,
        IReadOnlyDictionary<Reason, int> expected, int cameras)
    {
        var kinds = new Dictionary<StartPlaceKind, int>();
        var inOneSystem = 0;
        var reasons = Enum.GetValues<Reason>().ToDictionary(r => r, _ => 0);
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
                if (place is { Kind: StartPlaceKind.Camera, System: not null })
                    inOneSystem++;
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
                if (ReasonOf(place) is { } reason)
                    reasons[reason]++;
                else
                    wrong.Add($"{row}: {place.Text}. {place.Source}");
            }
        }

        output.WriteLine($"{name}: {string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}, " +
                         $"suspect rows {string.Join(", ", reasons.Select(r => $"{r.Key} {r.Value}"))}, " +
                         $"{inOneSystem} at the Scene camera in one system");
        Assert.Empty(wrong);
        Assert.Equal(expected.OrderBy(r => r.Key), reasons.OrderBy(r => r.Key));
        Assert.Equal(cameras, inOneSystem);
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

    /// <summary>Why a base or camera start can follow a launch, an arrival or a space condition of the path.</summary>
    private enum Reason
    {
        /// <summary>The chosen trigger waits for the end of a launch (Cnd_LaunchComplete of the player). Only a launch
        /// from a dock ends a launch, in retail and in OpenLancer. So the start is docked at the base of the launch, and
        /// the launch runs the trigger.</summary>
        LaunchEnd,

        /// <summary>No line of the path gives a point in the system after the launch. The start is docked at the base of
        /// the launch. The dock is the point of the launch in the retail game, and the launch runs the launch triggers
        /// again. The center of a system is never a start point, because most suns are there.</summary>
        AtTheBaseOfTheLaunch,

        /// <summary>No line of the path gives a point in the system, and no line names a base of the launch. The start is
        /// at the Scene camera in that system.</summary>
        CameraWithoutAPoint,
    }

    /// <summary>The reason that the source text of a suspect place gives, or null.</summary>
    private static Reason? ReasonOf(StartPlace place) => place.Source switch
    {
        _ when place.Kind == StartPlaceKind.Base &&
               place.Source.EndsWith("waits for the end of a launch, so the play starts docked.", StringComparison.Ordinal) =>
            Reason.LaunchEnd,
        _ when place.Kind == StartPlaceKind.Base &&
               place.Source.Contains(". No line of the path gives a point in ", StringComparison.Ordinal) &&
               place.Source.Contains(" after ", StringComparison.Ordinal) => Reason.AtTheBaseOfTheLaunch,
        _ when place is { Kind: StartPlaceKind.Camera, System: not null } &&
               place.Source.Contains(" No line of the path gives a point in ", StringComparison.Ordinal) =>
            Reason.CameraWithoutAPoint,
        _ => null,
    };

    /// <summary>A few suspect rows of each reason, in both data sets.</summary>
    [Fact]
    public void ExamplesOfTheReasonsOfTheSuspectRows()
    {
        var scripts = Scripts();
        foreach (var (slot, trigger) in new[] { ("M02", "after_launch"), ("M06", "lcp_start"), ("M13", "launch_complete") })
            Assert.Equal(Reason.LaunchEnd, ReasonOf(Suggested(scripts, slot, trigger)));
        foreach (var (slot, trigger) in new[] { ("M03", "tr_0100_juni"), ("M05", "tr_to_sprague") })
            Assert.Equal(Reason.AtTheBaseOfTheLaunch, ReasonOf(Suggested(scripts, slot, trigger)));
        foreach (var (slot, trigger) in new[] { ("M06", "tm_junispeaks"), ("M01b", "launch_complete_RTC") })
            Assert.Equal(Reason.CameraWithoutAPoint, ReasonOf(Suggested(scripts, slot, trigger)));
    }

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
