using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The Play start page of the Inspector and the UI texts of Play from a trigger (sections 8.1, 8.3 and 8.5 of the M6c
/// design, build step 2.4). The page model needs no window. The script is the synthetic script of the start plan
/// tests.
/// </summary>
public sealed class PlayStartPageTests
{
    private static readonly MissionIndex Index = MissionTestData.Index(StartPlanTests.Script);

    private static PlayStartPageModel Model(PlayStartSetting setting)
    {
        var (place, dock) = setting.PlaceChoice;
        return new PlayStartPageModel(MissionTestData.Path, setting,
            StartPlan.Make(Index, StartPlanTests.Slot, setting.Start, place, dock));
    }

    /// <summary>The field "Start" offers the mission start, each checkpoint, and the trigger of the play start when it is
    /// another trigger.</summary>
    [Fact]
    public void TheStartChoicesAreTheMissionStartTheCheckpointsAndThePlayStart()
    {
        var mission = Model(new PlayStartSetting("", "suggested"));
        var trigger = Model(new PlayStartSetting("t_kill", "suggested"));

        Assert.Equal(["Mission start", "Checkpoint t_restore"], mission.StartChoices(Index).Select(c => c.Label));
        Assert.Equal(["Mission start", "Checkpoint t_restore", "t_kill (approximate)"],
            trigger.StartChoices(Index).Select(c => c.Label));
        Assert.Equal(MissionStartPoint.At("t_kill"), trigger.StartChoices(Index)[^1].Start);
    }

    /// <summary>The fields of an approximate start: the stage after the accept, the place, and the setup lines of the
    /// closed section "Advanced".</summary>
    [Fact]
    public void AnApproximateStartShowsItsStageItsPlaceAndItsSetupLines()
    {
        var model = Model(new PlayStartSetting("t_kill", "suggested"));

        Assert.Equal(StartKind.Approximate, model.Plan.Kind);
        Assert.True(model.Plan.Accepted);
        Assert.Equal($"{StoryStages.NameOf(5)} (5)", model.StageText);
        Assert.StartsWith("Suggested: In ol_sys_a at ", model.PlaceText);
        Assert.Contains("nickname = ol_play_setup", model.SetupIni);
        Assert.Contains("ol.new = true", model.SetupIni);
        Assert.Contains("t_kill", model.Plan.ActiveSet);
    }

    /// <summary>A checkpoint start has no setup lines. The place choices show their own texts.</summary>
    [Fact]
    public void ACheckpointHasNoSetupLinesAndThePlaceChoicesShowTheirTexts()
    {
        Assert.Null(Model(new PlayStartSetting("t_restore", "suggested")).SetupIni);
        Assert.Null(Model(new PlayStartSetting("t_restore", "suggested")).SetupText);
        Assert.Equal("At base ol_base_a", Model(new PlayStartSetting("t_restore", "base:ol_base_a")).PlaceText);
        Assert.Equal("At the Scene camera", Model(new PlayStartSetting("t_restore", "camera")).PlaceText);
    }

    /// <summary>The Play start page of a checkpoint start shows the limit of this version (#56, #8): the start does not
    /// restore the objective, the locks and the reputations of a saved game. The page of a mission start and of an
    /// approximate start without a checkpoint on its path does not show it.</summary>
    [Fact]
    public void TheLimitTextShowsOnlyForAStartFromACheckpoint()
    {
        var checkpoint = Model(new PlayStartSetting("t_restore", "suggested"));
        var mission = Model(new PlayStartSetting("", "suggested"));
        var approximate = Model(new PlayStartSetting("t_chosen", "suggested"));

        Assert.Equal(StartKind.Checkpoint, checkpoint.Plan.Kind);
        var limit = Assert.Single(checkpoint.Plan.Warnings, StartPlanTests.IsCheckpointLimit);
        Assert.StartsWith("This version restores only the triggers of checkpoint 't_restore'.", limit,
            StringComparison.Ordinal);
        foreach (var state in new[] { "the objective", "the dock and lane rules", "the reputations" })
            Assert.Contains(state, limit, StringComparison.Ordinal);
        Assert.EndsWith("This version does not restore that state.", limit, StringComparison.Ordinal);
        Assert.Equal(StartKind.MissionStart, mission.Plan.Kind);
        Assert.DoesNotContain(mission.Plan.Warnings, StartPlanTests.IsCheckpointLimit);
        Assert.Equal(StartKind.Approximate, approximate.Plan.Kind);
        Assert.DoesNotContain(approximate.Plan.Warnings, StartPlanTests.IsCheckpointLimit);
    }

    /// <summary>The suggested place and the row "Source of the place" (fix of 2026-10-27). The dropdown item of the
    /// suggestion shows the true suggestion also while the user picked a base or the Scene camera. The row shows only for
    /// the suggested place.</summary>
    [Fact]
    public void TheSuggestionAndItsSourceShowAlsoWhileTheUserPicksAnotherPlace()
    {
        var suggested = Model(new PlayStartSetting("t_kill", "suggested"));
        var atBase = Model(new PlayStartSetting("t_kill", "base:ol_base_b"));
        var atCamera = Model(new PlayStartSetting("t_kill", "camera"));

        Assert.Equal(suggested.SuggestedText, atBase.SuggestedText);
        Assert.Equal(suggested.SuggestedText, atCamera.SuggestedText);
        Assert.StartsWith("In ol_sys_a at ", atBase.SuggestedText);
        Assert.Equal("At base ol_base_b", atBase.PlaceText);
        Assert.True(suggested.ShowsSource);
        Assert.False(atBase.ShowsSource);
        Assert.False(atCamera.ShowsSource);
        Assert.Equal("Cnd_SpaceEnter of t_launch. The point is 1,000 units from the spawn of ol_ship_a.",
            suggested.Plan.Suggested.Source);
        Assert.Equal("t_launch", suggested.Plan.Suggested.Trigger);
        Assert.False(Model(new PlayStartSetting("t_none", "suggested")).ShowsSource);
    }

    // The field "Ship" and the combat warning of issue #48.

    /// <summary>The names of the ships of <see cref="ShipWorkspace"/>. Two ships have the same name.</summary>
    private static readonly Dictionary<string, string> ShipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s1"] = "Starflier",
        ["s2"] = "Patriot",
        ["s3"] = "Patriot",
    };

    /// <summary>An edit view with the starter ship s1 and three ship packages. The ships s2 and s3 have the same name. The
    /// package lost_package names a hull good that the view does not have.</summary>
    private static ShipView ShipWorkspace(TestWorkspace ws)
    {
        ws.WriteBase(ItemWorkspace.ShipsPath, """
            [Ship]
            nickname = s1
            [Ship]
            nickname = s2
            [Ship]
            nickname = s3
            """);
        ws.WriteBase(ItemWorkspace.GoodsPath, """
            [Good]
            nickname = s1_hull
            category = shiphull
            ship = s1
            [Good]
            nickname = s2_hull
            category = shiphull
            ship = s2
            [Good]
            nickname = s3_hull
            category = shiphull
            ship = s3
            [Good]
            nickname = s3_package
            category = ship
            hull = s3_hull
            [Good]
            nickname = s1_package
            category = ship
            hull = s1_hull
            [Good]
            nickname = s2_package
            category = ship
            hull = s2_hull
            [Good]
            nickname = lost_package
            category = ship
            hull = no_such_hull
            """);
        return new ShipView(ws.Session("act"), s => ShipNames.GetValueOrDefault(s.Id.Key ?? "") ?? "", "s1");
    }

    /// <summary>The field "Ship" is a picker. Its first row is the ship of the profile with the name that the Play menu
    /// gives. The other rows are the ship packages of the edit view by the plain names of their ships. A name that two
    /// packages share shows the package nickname as a hint. A package without a ship gives no row.</summary>
    [Fact]
    public void TheShipFieldListsTheShipOfTheProfileThenEachShipPackage()
    {
        using var ws = ItemWorkspace.Create();
        var view = ShipWorkspace(ws);

        var field = PlayStartPageModel.ShipFieldOf(view, PlayStartSetting.Default, null);
        var profile = PlayStartPageModel.ShipFieldOf(view, PlayStartSetting.Default, "s2_package");

        Assert.Equal(
        [
            new ShipRow(null, "Ship of the profile: Starflier (default)", null),
            new ShipRow("s2_package", "Patriot", "s2_package"),
            new ShipRow("s3_package", "Patriot", "s3_package"),
            new ShipRow("s1_package", "Starflier", null),
        ], field.Rows);
        Assert.Equal("Ship of the profile: Starflier (default)", field.Text);
        Assert.Null(field.Problem);
        Assert.True(field.Starter);
        Assert.Equal("Ship of the profile: Patriot", profile.Text);
        Assert.Equal(profile.Rows[0].Text, profile.Text);
        Assert.False(profile.Starter);
        Assert.Equal(PlayStartPageModel.ProfileRowTip, PlayStartPageModel.ShipRowTip(field.Rows[0]));
        Assert.Equal("The ship package s1_package.", PlayStartPageModel.ShipRowTip(field.Rows[3]));
    }

    /// <summary>A pick gives the ship of its package, also over the ship of the profile. A package whose ship is the ship
    /// of a new character gives the starter ship.</summary>
    [Fact]
    public void APickGivesTheShipOfItsPackage()
    {
        using var ws = ItemWorkspace.Create();
        var view = ShipWorkspace(ws);

        var patriot = PlayStartPageModel.ShipFieldOf(view, PlayStartSetting.Default with { Ship = "S2_PACKAGE" },
            "s1_package");
        var starflier = PlayStartPageModel.ShipFieldOf(view, PlayStartSetting.Default with { Ship = "s1_package" },
            "s2_package");

        Assert.Equal(("Patriot (s2_package)", "The ship package S2_PACKAGE.", false),
            (patriot.Text, patriot.Tooltip, patriot.Starter));
        Assert.Null(patriot.Problem);
        Assert.Equal(("Starflier", true), (starflier.Text, starflier.Starter));
    }

    /// <summary>A package that the edit view does not have, or whose ship it does not have, shows "(not in the data)"
    /// with the reason of the builder. Play refuses with the same reason, so the page shows it under the field.</summary>
    [Theory]
    [InlineData("no_such_package", "The edit view has no ship package 'no_such_package'.")]
    [InlineData("lost_package", "The edit view has no ship of the ship package 'lost_package'.")]
    public void APackageThatTheEditViewCannotResolveShowsTheReason(string package, string reason)
    {
        using var ws = ItemWorkspace.Create();
        var view = ShipWorkspace(ws);

        var field = PlayStartPageModel.ShipFieldOf(view, PlayStartSetting.Default with { Ship = package }, null);
        var profile = PlayStartPageModel.ShipFieldOf(view, PlayStartSetting.Default, package);

        Assert.Equal($"{package} (not in the data)", field.Text);
        Assert.Equal($"{reason} Play cannot give the player this ship.", field.Problem);
        Assert.Equal(field.Problem, field.Tooltip);
        Assert.False(field.Starter);
        Assert.Equal(field.Problem, profile.Problem);
    }

    /// <summary>The page warns about a combat start with the starter ship. Another ship, a calm start, a base start and a
    /// ship that the edit view cannot resolve give no warning.</summary>
    [Fact]
    public void TheCombatWarningShowsForACombatStartWithTheStarterShip()
    {
        using var ws = ItemWorkspace.Create();
        var view = ShipWorkspace(ws);
        var hostile = MissionTestData.Index(
            StartWorldTests.Script(jump: "Act_SetVibe = ol_local, Player, REP_HOSTILE_THRESHOLD"), StartWorldTests.World());
        var calm = MissionTestData.Index(StartWorldTests.Script(), StartWorldTests.World());
        var combat = new PlayStartSetting("t_chosen", "suggested");

        Assert.True(Page(hostile, combat).Plan.Combat);
        Assert.Equal(PlayStartPageModel.CombatWarning, Warning(hostile, combat));
        Assert.Equal(PlayStartPageModel.CombatWarning, Warning(hostile, combat with { Ship = "s1_package" }));
        Assert.Null(Warning(hostile, combat with { Ship = "s2_package" }));
        Assert.Null(Warning(hostile, combat, "s2_package"));
        Assert.Null(Warning(hostile, combat with { Ship = "lost_package" }));
        Assert.Null(Warning(hostile, combat.WithPlace(StartPlaceChoice.Base, "ol_base_b")));
        Assert.Null(Warning(calm, combat));
        return;

        static PlayStartPageModel Page(MissionIndex index, PlayStartSetting setting)
        {
            var (place, dock) = setting.PlaceChoice;
            return new PlayStartPageModel(MissionTestData.Path, setting,
                StartPlan.Make(index, StartPlanTests.Slot, setting.Start, place, dock));
        }

        string? Warning(MissionIndex index, PlayStartSetting setting, string? profile = null) =>
            Page(index, setting).CombatWarningOf(PlayStartPageModel.ShipFieldOf(view, setting, profile));
    }

    /// <summary>The UI texts of Play from a trigger pass the STE lint (section 12.2 of the M6c design).</summary>
    [Fact]
    public void TheTextsPassTheSteLint()
    {
        var texts = new (string Name, string Text)[]
        {
            ("page", PlayStartPageModel.Help.WhatItIs), ("page writes", PlayStartPageModel.Help.WhatItWrites),
            ("approximate", PlayStartPageModel.ApproximateHelp.WhatItIs),
            ("approximate writes", PlayStartPageModel.ApproximateHelp.WhatItWrites),
            ("start", PlayStartPageModel.StartTip), ("slot", PlayStartPageModel.SlotTip),
            ("stage", PlayStartPageModel.StageTip), ("accept", PlayStartPageModel.AcceptTip),
            ("place", PlayStartPageModel.PlaceTip), ("source", PlayStartPageModel.SourceTip),
            ("ship", PlayStartPageModel.ShipTip), ("combat", PlayStartPageModel.CombatWarning),
            ("profile ship", PlayStartPageModel.ProfileRowTip),
            ("wait", PlayStartPageModel.WaitTip), ("setup", PlayStartPageModel.SetupTip),
            ("chip", MissionView.PlayStartTip), ("rules", MissionView.RulesBanner),
            ("rules help", MissionView.RulesHelp.WhatItIs), ("dry run during play", MissionView.DryRunPlayTip),
            ("play from here", GraphView.PlayFromHereTip), ("set as play start", GraphView.SetPlayStartTip),
            ("dry run from here", GraphView.DryRunFromHereTip), ("node chip", GraphView.PlayStartChip.Tooltip),
            ("duplicate", PlayFromHereStates.DuplicateReason), ("play runs", PlayFromHereStates.PlayRunsLine),
            ("exact start", PlayFromHereStates.ExactStart), ("exact triggers", PlayFromHereStates.ExactTriggers),
            ("approximate start", PlayFromHereStates.ApproximateStart),
            ("failed pass", PlayFromHereStates.FailedLine("The plan broke.")),
            ("choice problem", MissionView.ChoiceProblemLead), ("no game data", MissionPlay.NoGameData),
            ("live", RunPanel.LiveHelp.WhatItIs), ("clear", RunPanel.ClearTip),
            ("rules differ", RunOverlay.RulesDifferTip), ("no effect", RunOverlay.NoEffectStallTip),
            ("approximate note", MissionPlayTexts.Approximate),
            ("missing start", MissionPlayStart.MissingStart("M02", "t_wave")),
            ("no path", StartPlan.NoPathWarning("t_wave")), ("no slot", StartPlan.NoSlotReason),
            ("play tip", PlayStartPageModel.PlayTip), ("no trigger", StartPlan.NoTriggerReason("t_wave")),
            ("checkpoint state", StartPlan.CheckpointStateWarning("t_restore")),
            ("other start", MissionPlayStart.MissingPlayTrigger("M02", "t_wave")),
            ("mission start item", PlayMenu.MissionStartTip), ("checkpoint item", PlayMenu.CheckpointTip),
            ("restart item", PlayMenu.RestartTip), ("stop", PlayMenu.StopTip(false)), ("force stop", PlayMenu.StopTip(true)),
            ("no slot banner", MissionView.NoSlotBanner), ("other banner", MissionView.OtherMissionBanner("M02", "M03")),
            ("play banner", MissionView.PlayBannerText("M02", "Checkpoint t_restore")),
            ("no camera", MissionPlay.NoCameraSystem), ("unchecked point", EditorWindow.UncheckedPoint("New York (Li01)")),
            ("camera system", MissionPlay.CameraSystemReason("Li01")),
            ("launch watcher", StartPlaces.LaunchWatcherWarning("t_wave")),
            ("other system", StartPlaces.OtherSystemWarning("t_wave", "Li02", "Li01")),
            ("arrived", StartPlaces.ArrivedWarning("t_wave", "Li02")),
            ("restart refused", MissionPlayTexts.RestartRefused(new NamedStart(), "The path is refused.", "save 2")),
            ("restart failed", MissionPlayTexts.RestartFailedInGame(new NamedStart(), "The start save does not load.")),
            ("ship", PlayShips.ShipText((ShipView?)null, null).Tooltip),
            ("ship missing", PlayShips.ShipText((ShipView?)null, "ol_package").Tooltip),
            ("left out", PlayStartPageModel.LeftOutTip), ("companions", PlayStartPageModel.CompanionsTip),
            ("row", PlayStartPageModel.RowTip), ("left out title", PlayStartPageModel.LeftOutLabel(3)),
            ("companions title", PlayStartPageModel.CompanionsLabel(2)),
            ("waiting", StartWorld.WaitingWarning("t_wave", "ol_ship", "Li02")),
            ("label", StartWorld.LabelWarning("t_wave", "ol_label", 2)), ("camera", StartWorld.CameraWarning),
            ("no dock", StartWorld.NoDockWarning("li01_01_base")),
            ("spawn elsewhere", StartWorld.OtherSystemWarning("t_wave", "Li04", "Li01")),
            ("made again", PlayStartPageModel.LeftOutText(new LeftOutObject("ol_ship", StartObjectKind.Ship, "Li01", "t_a")
            {
                MadeAgainBy = "t_wave",
            })),
        };

        Assert.Empty(texts.SelectMany(t => SteLint.Problems(t.Text).Select(p => $"{t.Name}: {p}")));
    }

    /// <summary>The Console texts of a mission play have the words of section 8.4 of the M6c design.</summary>
    [Fact]
    public void TheConsoleTextsHaveTheWordsOfTheDesign()
    {
        var start = new NamedStart();

        Assert.Equal("OpenLancer plays M02 from Checkpoint t_restore.", MissionPlayTexts.Start(start));
        Assert.Equal("Save 3 changed the files of the mission play. The game starts M02 again from Checkpoint t_restore.",
            MissionPlayTexts.Restart(3, start));
        Assert.Equal("The game cannot start M02 again. The start save does not load.",
            MissionPlayTexts.RestartFailed(start, "The start save does not load."));
    }

    /// <summary>The play banner of the Mission tab: none without a mission play, the banner of the play while the graph
    /// shows the mission of the game, the banner of another mission, and the banner of a mission without a story slot
    /// (section 6.3 of the M6c design). Before the first mission event, the game runs the script of the start.</summary>
    [Fact]
    public void ThePlayBannerFollowsTheMissionOfTheGame()
    {
        const string m02 = @"DATA\MISSIONS\M02\m02.ini";
        const string m03 = @"DATA\MISSIONS\M03\m03.ini";

        Assert.Equal(MissionView.PlayBanner.None, MissionView.PlayBannerOf(false, m02, true, m02, m02));
        Assert.Equal(MissionView.PlayBanner.Play, MissionView.PlayBannerOf(true, m02, true, m02, m02.ToLowerInvariant()));
        Assert.Equal(MissionView.PlayBanner.Play, MissionView.PlayBannerOf(true, null, false, m02, m02));
        Assert.Equal(MissionView.PlayBanner.OtherMission, MissionView.PlayBannerOf(true, m03, true, m02, m02));
        Assert.Equal(MissionView.PlayBanner.OtherMission, MissionView.PlayBannerOf(true, null, false, m03, m02));
        Assert.Equal(MissionView.PlayBanner.NoSlot, MissionView.PlayBannerOf(true, null, true, m02, m02));
    }

    private sealed class NamedStart : IMissionStart
    {
        public string Script => @"DATA\MISSIONS\M02\m02.ini";

        public string Slot => "M02";

        public string StartName => "Checkpoint t_restore";

        public bool Approximate => false;

        public bool ReadsScene => true;

        public IReadOnlySet<string> SetupTriggers => new HashSet<string>();

        public StartPlan? Plan => null;

        public string? Refusal => null;

        public OpenLancer.Runtime.Play.PlayStart.FromSave? Prepare() => null;
    }
}
