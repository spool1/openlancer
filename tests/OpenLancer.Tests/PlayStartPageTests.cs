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
            ("ship", PlayStartPageModel.ShipTip),
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
            ("ship", PlayShips.ShipText(null, null).Tooltip), ("ship missing", PlayShips.ShipText(null, "ol_package").Tooltip),
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
