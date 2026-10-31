using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>mission_play</c> and <c>mission_play_trace</c> (section 7.4 of the MCP1 design) with the agent play rule
/// of the user decision of 2026-10-20. The fake editor runs the real mission play request of the shared command layer
/// (<see cref="MissionPlay"/>) on the synthetic script of the start plan tests, and the real live trace
/// (<see cref="MissionLive"/>).
/// </summary>
public sealed partial class AgentPlayToolTests
{
    private static readonly string Script = new ScriptRef(MissionTestData.Path).ToString();

    /// <summary>The arguments of a mission play of the test script from the mission start.</summary>
    private static string MissionStart => $$"""{ "script": {{Json(Script)}} }""";

    private static string Trigger(string name) => new TriggerRef(MissionTestData.Path, name).ToString();

    /// <summary>The arguments of a mission play of the test script from a trigger.</summary>
    private static string MissionAt(string trigger) =>
        $$"""{ "script": {{Json(Script)}}, "start": {{Json(Trigger(trigger))}} }""";

    private static string Snake(Enum value) => AgentTools.Snake(value.ToString());

    /// <summary>Without unsaved edits, a mission play starts at once. Nobody allowed a save, so Play refuses edits that
    /// come before its save. The result gives the start of the plan of the play.</summary>
    [Fact]
    public void WithoutUnsavedEditsAMissionPlayStartsAtOnceAndGivesItsStart()
    {
        var (host, editor) = Editor();

        var result = Ok(host, "mission_play", MissionAt("t_kill"));

        var plan = StartPlan.Make(editor.Index, editor.Slot, MissionStartPoint.At("t_kill"), StartPlaceChoice.Suggested,
            null);
        Assert.Equal(("starting", Script, Trigger("t_kill"), Snake(plan.Kind), plan.StartName),
            ((string?)result["play"], (string?)result["script"], (string?)result["start"], (string?)result["kind"],
                (string?)result["start_name"]));
        Assert.Equal(plan.Warnings, result["warnings"]!.AsArray().Select(w => (string)w!));
        var (start, save) = Assert.Single(editor.MissionRequests);
        Assert.Equal((MissionTestData.Path, MissionStartPoint.At("t_kill"), EditOrigin.Agent, false),
            (start.Request.Script, start.Request.Start, start.Request.Origin, start.Request.FromPlayStart));
        Assert.Equal(PlaySaveRule.NoEdits, save);
        Assert.Null(editor.Asked);
        Assert.Empty(editor.Requests);
        Assert.Empty(host.Refusals);
    }

    /// <summary>The first sentence of the description of each tool that starts Play tells the agent play rule of the user
    /// decision of 2026-10-20 (section 7.4 of the MCP1 design). The decision replaces the text of section 9.1 that saves
    /// first.</summary>
    [Fact]
    public void TheDescriptionOfEachToolThatStartsPlayTellsTheAgentPlayRuleFirst()
    {
        foreach (var tool in AgentMenu.PlayTools)
        {
            Assert.StartsWith("When the active mod has unsaved edits, the editor asks the user to save them first. ",
                AgentTools.Find(tool)!.Description);
        }
        Assert.Contains("does not change the play start of the user", AgentTools.Find("mission_play")!.Description);
    }

    /// <summary>mission_play reads its start as mission_start_plan does: the mission start by default, the sec: form of a
    /// trigger, and the start place.</summary>
    [Fact]
    public void TheMissionPlayReadsTheStartAsTheStartPlanDoes()
    {
        var (host, editor) = Editor();
        var section = ItemRef.Of(MissionTestData.Path, "Trigger", "t_kill").ToString();

        var first = Ok(host, "mission_play", MissionStart);
        var arguments = $$"""{ "script": {{Json(Script)}}, "start": {{Json(section)}}, "start_place": "base:ol_base_a" }""";
        Ok(host, "mission_play", arguments);

        var mission = StartPlan.Make(editor.Index, editor.Slot, MissionStartPoint.MissionStart, StartPlaceChoice.Suggested,
            null);
        Assert.Equal(("mission_start", "mission_start", mission.StartName),
            ((string?)first["start"], (string?)first["kind"], (string?)first["start_name"]));
        var request = editor.MissionRequests[1].Start.Request;
        Assert.Equal((MissionStartPoint.At("t_kill"), StartPlaceChoice.Base, "ol_base_a"),
            (request.Start, request.Place, request.PlaceBase));
        Assert.Same(AgentTools.Find("mission_start_plan")!.Input, AgentTools.Find("mission_play")!.Input);
    }

    /// <summary>An argument that the reader of the start plan refuses gives invalid_argument, and no play
    /// starts.</summary>
    [Theory]
    [InlineData("""{ "script": "prefab:mod:ol_gate" }""", "script")]
    [InlineData("""{ "script": "msn:DATA\\MISSIONS\\TEST\\test.ini", "start": "trg:DATA\\MISSIONS\\OTHER\\o.ini#t_kill" }""",
        "start")]
    [InlineData("""{ "script": "msn:DATA\\MISSIONS\\TEST\\test.ini", "start": "t_kill" }""", "start")]
    [InlineData("""{ "script": "msn:DATA\\MISSIONS\\TEST\\test.ini", "target": "vanilla-1.1" }""", "target")]
    [InlineData("""{ "script": "msn:DATA\\MISSIONS\\TEST\\test.ini", "start_place": "nowhere" }""", "start_place")]
    [InlineData("""{ "start": "mission_start" }""", "script")]
    public void AnArgumentThatTheStartReaderRefusesGivesInvalidArgument(string arguments, string field)
    {
        var (host, editor) = Editor();

        var result = Call(host, "mission_play", arguments);

        Assert.True(result.IsError, result.Text);
        Assert.Equal(("invalid_argument", field), ((string?)result.Content["code"], (string?)result.Content["field"]));
        Assert.Empty(editor.MissionRequests);
    }

    /// <summary>With unsaved edits, the editor asks the user, with the one question of the agent play rule. Cancel gives
    /// refused with the reason of the user decision, and Play does not start.</summary>
    [Fact]
    public async Task WithUnsavedEditsTheMissionPlayAsksAndCancelGivesRefused()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";

        var tail = Tail(host, MissionAt("t_kill"), CancellationToken.None, "mission_play");

        Assert.True(editor.QuestionShows);
        Assert.Equal(PlayRequests.AgentPlayQuestion("ol_mod"), editor.Asked!.Value.Message);
        Assert.False(tail.IsCompleted);
        editor.Click(1);
        var result = await tail.WaitAsync(Wait, Token);

        Assert.True(result.IsError);
        Assert.Equal(("refused", "NotSaved", "The user did not save. Play did not start."),
            ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["message"]));
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find("mission_play")!.OutputSchema, result.Content));
        Assert.Empty(editor.MissionRequests);
    }

    /// <summary>"Save and continue" starts the mission play, and Play saves first, as F5 does.</summary>
    [Fact]
    public async Task SaveAndContinueStartsTheMissionPlayWithASave()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";

        var tail = Tail(host, MissionStart, CancellationToken.None, "mission_play");
        editor.Click(0);
        var result = await tail.WaitAsync(Wait, Token);

        Assert.False(result.IsError, result.Text);
        Assert.Equal("starting", (string?)result.Content["play"]);
        Assert.Equal([PlaySaveRule.Save], editor.MissionRequests.Select(r => r.Save));
    }

    /// <summary>The setting "Auto-save and continue when an MCP agent starts Play" skips the question for a mission play
    /// too, and Play saves.</summary>
    [Fact]
    public void WithTheSettingTheMissionPlaySavesWithoutTheQuestion()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        editor.AutoSaveAgentPlay = true;

        Ok(host, "mission_play", MissionStart);

        Assert.Null(editor.Asked);
        Assert.Equal([PlaySaveRule.Save], editor.MissionRequests.Select(r => r.Save));
    }

    /// <summary>At the time limit of the tail, the call gives limit and the question closes as Cancel. The close waits
    /// for the UI thread, and a click before it does not start the mission play either.</summary>
    [Fact]
    public async Task AtTheTailLimitTheMissionPlayQuestionClosesAndALateClickStartsNothing()
    {
        var clock = new FakeClock();
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        using var queue = new ToolQueue(host, clock);

        var call = queue.CallAsync("mission_play", Args(MissionStart), null, Token);
        Assert.True(queue.RunNext());
        Assert.True(editor.QuestionShows);
        clock.Advance(TimeSpan.FromSeconds(60));
        var result = await call.WaitAsync(Wait, Token);

        Assert.Equal(("limit", "TailTime"), ((string?)result.Content["code"], (string?)result.Content["detail"]));
        Assert.Equal(1, editor.Withdrawn);
        editor.Click(0);
        editor.RunWithdrawn();
        Assert.Empty(editor.MissionRequests);
        Assert.Null(editor.AgentPlays.Busy(PlayStatus.Idle));
    }

    /// <summary>One dialog shows the question, so play and mission_play share the rule of busy: while the question of
    /// one is open, a request of the other gets busy.</summary>
    [Fact]
    public async Task PlayAndMissionPlayShareTheOneQuestionSoASecondRequestGetsBusy()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        var busy = ("busy", PlayRequests.AgentWaitsDetail, PlayRequests.AgentWaitsReason);

        var mission = Tail(host, MissionStart, CancellationToken.None, "mission_play");
        var play = Refusal(host, AtBase);
        var second = Refusal(host, MissionAt("t_kill"), "mission_play");
        editor.Click(1);
        await mission.WaitAsync(Wait, Token);
        var asked = Tail(host, AtBase, CancellationToken.None);
        var whilePlayAsks = Refusal(host, MissionStart, "mission_play");
        editor.Click(1);
        await asked.WaitAsync(Wait, Token);

        Assert.Equal(busy, play);
        Assert.Equal(busy, second);
        Assert.Equal(busy, whilePlayAsks);
        Assert.Empty(editor.MissionRequests);
        Assert.Empty(editor.Requests);
    }

    /// <summary>While another play runs, mission_play gives refused before it asks: the game of a play copy, or a play of
    /// OpenLancer that is not a mission play. While a mission play runs, the game starts again at the new start. The
    /// Console gets each refusal.</summary>
    [Fact]
    public void WhileAnotherPlayRunsTheMissionPlayRefusesAndARunningMissionPlayStartsAgain()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        editor.GameRuns = true;
        var copy = Refusal(host, MissionStart, "mission_play");
        editor.GameRuns = false;
        editor.PlayStatus = PlayStatus.Running;
        var camera = Refusal(host, MissionStart, "mission_play");
        Assert.Null(editor.Asked);

        editor.UnsavedMod = null;
        editor.RunningMission = new MissionPlayStart(editor.Plays,
            new MissionPlayRequest(MissionTestData.Path, MissionStartPoint.MissionStart));
        var again = Ok(host, "mission_play", MissionAt("t_kill"));

        Assert.Equal(("refused", MissionPlay.OtherPlayDetail, MissionPlay.OtherPlayReason), copy);
        Assert.Equal(copy, camera);
        Assert.Equal(Trigger("t_kill"), (string?)again["start"]);
        Assert.Equal(MissionStartPoint.At("t_kill"), Assert.Single(editor.MissionRequests).Start.Request.Start);
        Assert.Equal(["mission_play", "mission_play"], host.Refusals.Select(r => r.Tool));
    }

    /// <summary>A start that the plan refuses gives refused with the reason of the plan, before the question. A trigger
    /// or a script that does not resolve gives not_found with its argument.</summary>
    [Fact]
    public void AStartThatThePlanRefusesGivesRefusedAndAMissingTriggerGivesNotFound()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        editor.Slot = null;

        var noSlot = Refusal(host, MissionAt("t_kill"), "mission_play");
        editor.Slot = StartPlanTests.Slot;
        var gone = Call(host, "mission_play", MissionAt("t_gone"));
        editor.Missing = "The session has no such mission script.";
        var noScript = Call(host, "mission_play", MissionStart);

        Assert.Equal(("refused", "StartRefused", StartPlan.NoSlotReason), noSlot);
        Assert.Equal(("not_found", MissionErrors.TriggerNotFound, StartPlan.NoTriggerReason("t_gone"), "start"),
            ((string?)gone.Content["code"], (string?)gone.Content["detail"], (string?)gone.Content["message"],
                (string?)gone.Content["field"]));
        Assert.Equal(("not_found", MissionErrors.ScriptNotFound, "script"),
            ((string?)noScript.Content["code"], (string?)noScript.Content["detail"], (string?)noScript.Content["field"]));
        Assert.Null(editor.Asked);
        Assert.Empty(editor.MissionRequests);
    }

    /// <summary>The request checks the script and the trigger again after "Save and continue", because the user can
    /// delete or rename them while the question is open. That refusal also gives the argument as the field, as the
    /// refusal before the question does.</summary>
    [Fact]
    public async Task AScriptOrATriggerThatGoesWhileTheQuestionIsOpenGivesNotFoundWithItsArgument()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";

        var trigger = Tail(host, MissionAt("t_kill"), CancellationToken.None, "mission_play");
        editor.Index = MissionTestData.Index(StartPlanTests.Script.Replace("nickname = t_kill", "nickname = t_kill_2",
            StringComparison.Ordinal));
        editor.Click(0);
        var noTrigger = await trigger.WaitAsync(Wait, Token);
        editor.Index = MissionTestData.Index(StartPlanTests.Script);
        var script = Tail(host, MissionAt("t_kill"), CancellationToken.None, "mission_play");
        editor.Missing = "The session has no such mission script.";
        editor.Click(0);
        var noScript = await script.WaitAsync(Wait, Token);

        Assert.Equal(("not_found", MissionErrors.TriggerNotFound, "start"), (
            (string?)noTrigger.Content["code"], (string?)noTrigger.Content["detail"],
            (string?)noTrigger.Content["field"]));
        Assert.Equal(("not_found", MissionErrors.ScriptNotFound, "script"), (
            (string?)noScript.Content["code"], (string?)noScript.Content["detail"], (string?)noScript.Content["field"]));
        Assert.Empty(editor.MissionRequests);
    }

    /// <summary>When the save of Play stops, mission_play gives the error of the save with its code.</summary>
    [Theory]
    [InlineData(EditErrorKind.SaveBlocked, "save_blocked", "SaveBlocked")]
    [InlineData(EditErrorKind.ChangedOnDisk, "changed_on_disk", "ChangedOnDisk")]
    public void AnErrorOfTheSaveOfPlayGivesItsCodeForAMissionPlay(EditErrorKind kind, string code, string detail)
    {
        const string message = "The save of Play stopped.";
        var (host, editor) = Editor();
        editor.MissionResult = new TaskCompletionSource<PlayRequestResult>();
        editor.MissionResult.SetResult(PlayRequestResult.Refused(PlayStatus.Idle, detail, message, kind));

        Assert.Equal((code, detail, message), Error(host, "mission_play", MissionStart));
    }

    /// <summary>Play makes the plan after its save. When the saved script does not have the trigger of the request, the
    /// game plays the mission start, and the result says it. A result without a plan gives the start of the
    /// request.</summary>
    [Fact]
    public void TheResultGivesTheStartThatTheGamePlays()
    {
        var (host, editor) = Editor();
        editor.MissionResult = new TaskCompletionSource<PlayRequestResult>();
        editor.MissionResult.SetResult(new PlayRequestResult(PlayStatus.Running)
        {
            Plan = StartPlan.Make(editor.Index, editor.Slot, MissionStartPoint.MissionStart, StartPlaceChoice.Suggested, null),
        });

        var fallBack = Ok(host, "mission_play", MissionAt("t_kill"));
        editor.MissionResult = new TaskCompletionSource<PlayRequestResult>();
        editor.MissionResult.SetResult(new PlayRequestResult(PlayStatus.Running));
        var noPlan = Ok(host, "mission_play", MissionAt("t_kill"));

        Assert.Equal(("running", "mission_start", "mission_start"),
            ((string?)fallBack["play"], (string?)fallBack["start"], (string?)fallBack["kind"]));
        Assert.Equal((Trigger("t_kill"), "approximate", StartPlan.NameOf(StartKind.Approximate, MissionStartPoint.At("t_kill"))),
            ((string?)noPlan["start"], (string?)noPlan["kind"], (string?)noPlan["start_name"]));
    }

    /// <summary>A mission play of an agent plays the start of its request and never changes the play start of the
    /// user.</summary>
    [Fact]
    public void AMissionPlayOfAnAgentKeepsThePlayStartOfTheUser()
    {
        var (host, editor) = Editor();
        editor.Plays.SetPlayStart(MissionTestData.Path, PlayStartSetting.Default with { Trigger = "t_launch" });

        Ok(host, "mission_play", MissionAt("t_kill"));
        Ok(host, "mission_play", MissionStart);

        Assert.Equal("t_launch", editor.Plays.PlayStartOf(MissionTestData.Path).Trigger);
        Assert.Equal([MissionStartPoint.At("t_kill"), MissionStartPoint.MissionStart],
            editor.MissionRequests.Select(r => r.Start.Request.Start));
    }

    /// <summary>play_stop stops a mission play, as Shift+F5 does.</summary>
    [Fact]
    public void PlayStopStopsAMissionPlay()
    {
        var (host, editor) = Editor();
        Ok(host, "mission_play", MissionStart);
        editor.PlayStatus = PlayStatus.Running;
        editor.RunningMission = editor.MissionRequests[0].Start;
        editor.Stopping = PlayStopped.OpenLancer;
        editor.AfterStop = PlayStatus.Stopping;

        var stop = Ok(host, "play_stop");

        Assert.Equal(("openlancer", "stopping"), ((string?)stop["stopped"], (string?)stop["play"]));
        Assert.Equal(1, editor.Stops);
    }

    /// <summary>mission_play is in the group Play, and its script is an item of the group Missions. So the tool refuses
    /// while the user hides either group (decision D725). mission_play_trace is in the group Missions.</summary>
    [Fact]
    public void AHiddenMissionsGroupGivesNotAvailableForTheMissionPlayAndTheTrace()
    {
        var (host, editor) = Editor();
        host.Hidden.Add(ToolGroup.Missions);

        var play = Call(host, "mission_play", MissionStart);
        var trace = Error(host, "mission_play_trace");
        Ok(host, "play", AtBase);

        Assert.Equal(("not_available", "GroupHidden", Script),
            ((string?)play.Content["code"], (string?)play.Content["detail"], (string?)play.Content["target"]));
        Assert.Equal("The user hid the tool group 'missions'. Ask the user to show it in the agent settings.",
            (string?)play.Content["message"]);
        Assert.Equal(("not_available", "GroupHidden"), Codes(trace));
        Assert.Empty(editor.MissionRequests);
        Assert.Single(editor.Requests);
    }

    /// <summary>Pause refuses mission_play, and lets mission_play_trace run, because it only reads (decision
    /// D734).</summary>
    [Fact]
    public void PauseRefusesTheMissionPlayAndLetsTheTraceRun()
    {
        var (host, editor) = Editor();
        host.TestPaused = true;

        var play = Error(host, "mission_play", MissionStart);
        var trace = Ok(host, "mission_play_trace");

        Assert.Equal(("paused", "Paused"), Codes(play));
        Assert.Equal((0, "idle"), ((int)trace["play"]!, (string?)trace["status"]));
        Assert.Empty(editor.MissionRequests);
    }

    /// <summary>Without an open profile, mission_play gives the error of each tool that reads the profile. The live
    /// trace stays after the end of a play, so mission_play_trace also works without a profile.</summary>
    [Fact]
    public void WithoutAProfileTheMissionPlayGivesNotAvailableAndTheTraceRuns()
    {
        var (host, editor) = Editor(profileOpen: false);

        var play = Refusal(host, MissionStart, "mission_play");
        Ok(host, "mission_play_trace");

        Assert.Equal(("not_available", "NoProfile", "No profile is open. Open a profile in the editor, then try again."),
            play);
        Assert.Empty(editor.MissionRequests);
    }

    /// <summary>The live trace gives the changes of the current play after since, with the trg: reference of each
    /// trigger. A request with another play number gets all changes of the current play.</summary>
    [Fact]
    public void TheTraceGivesTheChangesAfterSinceAndAllChangesOfAnotherPlay()
    {
        var (host, editor) = Editor();
        editor.LiveScript = MissionTestData.Path;
        editor.Live.Started();
        editor.Live.Take(new PlayEvent.MissionChanges("Mission_99", true, 0,
        [
            new MissionChange(100, "t_start", MissionChangeKind.Ran),
            new MissionChange(100, "t_offer", MissionChangeKind.Started),
            new MissionChange(2500, "t_offer", MissionChangeKind.Ran),
        ]));
        editor.PlayStatus = PlayStatus.Running;

        var all = Ok(host, "mission_play_trace");
        var later = Ok(host, "mission_play_trace", """{ "play": 1, "since": 2 }""");
        var other = Ok(host, "mission_play_trace", """{ "play": 7, "since": 2 }""");

        Assert.Equal((1, "running", "Mission_99", Script, 0, false, false),
            ((int)all["play"]!, (string?)all["status"], (string?)all["mission"], (string?)all["script"],
                (int)all["dropped"]!, (bool)all["game_runs"]!, (bool)all["more"]!));
        Assert.Equal(
        [
            (1L, 0.1, Trigger("t_start"), "ran"), (2L, 0.1, Trigger("t_offer"), "started"),
            (3L, 2.5, Trigger("t_offer"), "ran"),
        ], Changes(all));
        Assert.Equal([3L], Changes(later).Select(c => c.Sequence));
        Assert.Equal([1L, 2L, 3L], Changes(other).Select(c => c.Sequence));
    }

    /// <summary>The limit cuts the changes of a long play into pages, and more tells that the play has more changes
    /// (section 8.4 of the MCP1 design). Without the limit, the page has the default rows of the limits. A limit above
    /// the largest page gives limit.</summary>
    [Fact]
    public void TheLimitCutsTheChangesIntoPagesAndGivesMore()
    {
        var (host, editor) = Editor();
        host.TestLimits = new AgentLimits { ListRows = 3, MaxListRows = 4 };
        editor.Live.Started();
        editor.Live.Take(new PlayEvent.MissionChanges("Mission_99", true, 0,
            Enumerable.Range(1, 5).Select(i => new MissionChange(i * 1000, $"t_{i}", MissionChangeKind.Ran)).ToList()));

        var first = Ok(host, "mission_play_trace", """{ "limit": 2 }""");
        var second = Ok(host, "mission_play_trace", """{ "play": 1, "since": 2, "limit": 2 }""");
        var last = Ok(host, "mission_play_trace", """{ "play": 1, "since": 4, "limit": 2 }""");
        var byDefault = Ok(host, "mission_play_trace");
        var over = Error(host, "mission_play_trace", """{ "limit": 5 }""");

        Assert.Equal(("1 2", true), Page(first));
        Assert.Equal(("3 4", true), Page(second));
        Assert.Equal(("5", false), Page(last));
        Assert.Equal(3, Changes(byDefault).Count);
        Assert.Equal(("limit", "ListRows"), Codes(over));
    }

    /// <summary>A sequence number belongs to one play, and another play number gives all changes of the current play.
    /// So since without play would give the first page again in a loop. The tool refuses it.</summary>
    [Fact]
    public void SinceWithoutAPlayNumberGivesInvalidArgument()
    {
        var (host, editor) = Editor();
        editor.Live.Started();
        editor.Live.Take(new PlayEvent.MissionChanges("Mission_99", true, 0,
            Enumerable.Range(1, 5).Select(i => new MissionChange(i * 1000, $"t_{i}", MissionChangeKind.Ran)).ToList()));

        var noPlay = Call(host, "mission_play_trace", """{ "since": 2, "limit": 2 }""");
        var playZero = Call(host, "mission_play_trace", """{ "play": 0, "since": 2 }""");
        var page = Ok(host, "mission_play_trace", """{ "play": 1, "since": 2, "limit": 2 }""");

        foreach (var refused in new[] { noPlay, playZero })
        {
            Assert.True(refused.IsError, refused.Text);
            Assert.Equal(("invalid_argument", "since"),
                ((string?)refused.Content["code"], (string?)refused.Content["field"]));
        }
        Assert.Equal(("3 4", true), Page(page));
    }

    /// <summary>Without a known script of the mission, a change names its trigger by nickname. While only the game of a
    /// play copy runs, the trace gives only the status, because that game has no channel to the editor.</summary>
    [Fact]
    public void WithoutAScriptATraceGivesNicknamesAndAPlayCopyGivesOnlyTheStatus()
    {
        var (host, editor) = Editor();
        editor.Live.Started();
        editor.Live.Take(new PlayEvent.MissionChanges("Mission_99", true, 3,
            [new MissionChange(100, "t_start", MissionChangeKind.Ran)]));

        var nicknames = Ok(host, "mission_play_trace");
        editor.GameRuns = true;
        var copy = Ok(host, "mission_play_trace");

        Assert.Equal(("t_start", (string?)null, 3), (Changes(nicknames).Single().Trigger, (string?)nicknames["script"],
            (int)nicknames["dropped"]!));
        Assert.Equal((true, "idle"), ((bool)copy["game_runs"]!, (string?)copy["status"]));
        Assert.Empty(Changes(copy));
    }

    /// <summary>The sequence numbers of a page of the trace, and if the play has more changes.</summary>
    private static (string Sequences, bool More) Page(System.Text.Json.Nodes.JsonObject trace) =>
        (string.Join(" ", Changes(trace).Select(c => c.Sequence)), (bool)trace["more"]!);

    private static List<(long Sequence, double Time, string Trigger, string Change)> Changes(
        System.Text.Json.Nodes.JsonObject trace) =>
        trace["changes"]!.AsArray().Select(c => ((long)c!["sequence"]!, (double)c["time"]!, (string)c["trigger"]!,
            (string)c["change"]!)).ToList();

    /// <summary>The mission play of the test editor: the real mission play request of the shared command layer on the
    /// synthetic script of the start plan tests, and the real live trace. It keeps each mission request.</summary>
    internal sealed partial class FakePlayEditor : IMissionPlayEditor
    {
        private MissionPlay? plays;

        /// <summary>The mission play request of the shared command layer, with this editor.</summary>
        public MissionPlay Plays => plays ??= new MissionPlay(this);

        /// <summary>The profile file. The fake writes no file into its folder.</summary>
        public string? ProfilePath { get; set; } = "ol_profile.json";

        public EditorSettings Settings { get; } = EditorSettings.Transient();

        public ConsoleLog Log { get; } = new();

        public MissionIndex Index { get; set; } = MissionTestData.Index(StartPlanTests.Script);

        public StorySlot? Slot { get; set; } = StartPlanTests.Slot;

        /// <summary>The reason of a script that the session does not have, or null.</summary>
        public string? Missing { get; set; }

        public string? PlayPackage => null;

        public IMissionStart? RunningMission { get; set; }

        public IReadOnlyList<IMissionStart> MissionStarts => RunningMission is { } running ? [running] : [];

        /// <summary>Each mission request that reached Play, with its save rule.</summary>
        public List<(MissionPlayStart Start, PlaySaveRule Save)> MissionRequests { get; } = [];

        /// <summary>The result of the next mission requests, or null for a play that starts at once.</summary>
        public TaskCompletionSource<PlayRequestResult>? MissionResult { get; set; }

        /// <summary>The live trace of the mission of the game.</summary>
        public MissionLive Live { get; } = new(() => 0);

        /// <summary>The script of the mission of the live trace, or null.</summary>
        public string? LiveScript { get; set; }

        public (MissionIndex Index, StorySlot? Slot)? IndexOf(string script) => (Index, Slot);

        public (MissionIndex Index, StorySlot? Slot)? ModelOf(string script, out string? problem)
        {
            problem = Missing;
            return Missing == null ? (Index, Slot) : null;
        }

        public StartSavePlace? PlaceOf(StartPlace place, out string? problem)
        {
            problem = null;
            return place.Kind == StartPlaceKind.Base
                ? StartSavePlace.AtBase("ol_sys_a", place.Base!)
                : new StartSavePlace(place.System ?? "ol_sys_a", null, place.Position, place.Orientation);
        }

        /// <summary>Keeps the request. Play gives the plan of the start that it prepared after its save, so a play that
        /// starts at once gives the plan of the start of the request.</summary>
        public Task<PlayRequestResult> RequestMission(IMissionStart start, string? package, PlaySaveRule save)
        {
            var mission = (MissionPlayStart)start;
            MissionRequests.Add((mission, save));
            var request = mission.Request;
            return MissionResult?.Task ?? Task.FromResult(new PlayRequestResult(PlayStatus.Starting)
            {
                Plan = StartPlan.Make(Index, Slot, request.Start, request.Place, request.PlaceBase),
            });
        }

        public Task<PlayRequestResult> PlayMission(MissionPlayRequest request, CancellationToken cancel) =>
            Plays.Request(request, cancel);

        public LiveTraceResult LiveTrace(int play, long since) =>
            Live.Result(play, since, PlayStatus, GameRuns, LiveScript);
    }
}
