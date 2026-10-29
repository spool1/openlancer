using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>play</c> and <c>play_stop</c> (section 7.6 of the MCP1 design, decision D739) with the agent play rule of
/// the user decision of 2026-10-20: the Play of the editor with a fake editor, the checks that the Play menu shares, the
/// question of the user, and its close at the time limit and at a cancel of the client.
/// </summary>
public sealed class AgentPlayToolTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string AtBase = """{ "start": "base", "base": "ol_base_01" }""";

    private const string InSpace =
        """{ "start": "space", "system": "ol_sys", "position": [100, 0, -200], "orientation": [0, 90, 0] }""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The editor host with the Play of a fake editor.</summary>
    private static (TestAgentHost Host, FakePlayEditor Editor) Editor()
    {
        var editor = new FakePlayEditor();
        return (new TestAgentHost(null, AgentHostKind.Editor) { TestPlay = new EditorAgentPlay(editor) }, editor);
    }

    /// <summary>Without unsaved edits, the play starts at once. Nobody allowed a save, so Play refuses edits that come
    /// before its save (<see cref="PlaySaveRule.NoEdits"/>).</summary>
    [Fact]
    public void WithoutUnsavedEditsThePlayStartsAtOnceWithoutASave()
    {
        var (host, editor) = Editor();

        var result = Ok(host, "play", AtBase);

        Assert.Equal("starting", (string?)result["play"]);
        Assert.Equal([(new PlayStart.AtBase("ol_base_01"), PlaySaveRule.NoEdits)], editor.Requests);
        Assert.Null(editor.Asked);
    }

    /// <summary>A start in space takes a system, a point and a heading in degrees, as the rotate key holds it. A
    /// reference of the right kind gives its nickname.</summary>
    [Fact]
    public void AStartInSpaceHasThePointAndTheHeadingAndAReferenceGivesItsNickname()
    {
        var (host, editor) = Editor();

        Ok(host, "play", InSpace);
        Ok(host, "play", """{ "start": "space", "system": "system:ol_sys", "position": [0, 0, 0] }""");
        Ok(host, "play", """{ "start": "base", "base": "base:ol_base_01" }""");

        var space = Assert.IsType<PlayStart.InSpace>(editor.Requests[0].Start);
        Assert.Equal(("ol_sys", new Vector3(100, 0, -200)), (space.System, space.Position));
        Assert.Equal(MathHelper.MatrixFromEulerDegrees(new Vector3(0, 90, 0)).ExtractRotation(), space.Orientation);
        // The ship of a start without a heading flies along -Z.
        var plain = Assert.IsType<PlayStart.InSpace>(editor.Requests[1].Start);
        Assert.Equal("ol_sys", plain.System);
        Assert.True(Vector3.Distance(-Vector3.UnitZ, Vector3.Transform(-Vector3.UnitZ, plain.Orientation)) < 1e-5f);
        Assert.Equal(new PlayStart.AtBase("ol_base_01"), editor.Requests[2].Start);
    }

    /// <summary>An argument of the other kind of start, a missing argument and a reference of another kind give
    /// invalid_argument with the name of the argument. No play starts.</summary>
    [Theory]
    [InlineData("""{ "start": "base", "base": "b", "position": [0, 0, 0] }""", "position",
        "The argument 'position' is only for a start in space. Remove it, or use start: space.")]
    [InlineData("""{ "start": "base", "base": "b", "system": "s" }""", "system",
        "The argument 'system' is only for a start in space. Remove it, or use start: space.")]
    [InlineData("""{ "start": "space", "system": "s", "position": [0, 0, 0], "base": "b" }""", "base",
        "The argument 'base' is only for a start at a base. Remove it, or use start: base.")]
    [InlineData("""{ "start": "space", "position": [0, 0, 0] }""", "system",
        "The argument 'system' is necessary. Give it, then call the tool again.")]
    [InlineData("""{ "start": "space", "system": "s" }""", "position",
        "The argument 'position' is necessary. Give it, then call the tool again.")]
    [InlineData("""{ "start": "base" }""", "base", "The argument 'base' is necessary. Give it, then call the tool again.")]
    [InlineData("""{ "start": "space", "system": "obj:my_station", "position": [0, 0, 0] }""", "system",
        "The argument 'system' takes a nickname or a system: reference, not obj:my_station.")]
    [InlineData("""{ "start": "base", "base": "system:s" }""", "base",
        "The argument 'base' takes a nickname or a base: reference, not system:s.")]
    [InlineData("""{ "base": "b" }""", "start", null)]
    [InlineData("""{ "start": "space", "system": "s", "position": [20000000, 0, 0] }""", "position", null)]
    public void AnArgumentThatDoesNotFitTheStartGivesInvalidArgument(string arguments, string field, string? message)
    {
        var (host, editor) = Editor();

        var result = Call(host, "play", arguments);

        Assert.True(result.IsError, result.Text);
        Assert.Equal(("invalid_argument", field), ((string?)result.Content["code"], (string?)result.Content["field"]));
        if (message != null)
            Assert.Equal(message, (string?)result.Content["message"]);
        Assert.Empty(editor.Requests);
    }

    /// <summary>With unsaved edits, the editor asks the user. Cancel gives refused with the reason of the user
    /// decision, and Play does not start.</summary>
    [Fact]
    public async Task WithUnsavedEditsTheQuestionShowsAndCancelGivesRefused()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";

        var tail = Tail(host, AtBase, CancellationToken.None);

        Assert.True(editor.QuestionShows);
        Assert.Equal("An agent wants to start Play. Mod 'ol_mod' has unsaved edits. Save them and continue?",
            editor.Asked!.Value.Message);
        Assert.False(tail.IsCompleted);
        editor.Click(1);
        var result = await tail.WaitAsync(Wait, Token);

        Assert.True(result.IsError);
        Assert.Equal(("refused", "NotSaved", "The user did not save. Play did not start."),
            ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["message"]));
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find("play")!.OutputSchema, result.Content));
        Assert.Empty(editor.Requests);
    }

    /// <summary>"Save and continue" starts the play, and Play saves first, as F5 does.</summary>
    [Fact]
    public async Task SaveAndContinueStartsThePlayWithASave()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";

        var tail = Tail(host, InSpace, CancellationToken.None);
        editor.Click(0);
        var result = await tail.WaitAsync(Wait, Token);

        Assert.False(result.IsError, result.Text);
        Assert.Equal("starting", (string?)result.Content["play"]);
        Assert.Equal([PlaySaveRule.Save], editor.Requests.Select(r => r.Save));
    }

    /// <summary>The setting "Auto-save and continue when an MCP agent starts Play" skips the question, and Play
    /// saves.</summary>
    [Fact]
    public void WithTheSettingPlaySavesWithoutTheQuestion()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        editor.AutoSaveAgentPlay = true;

        Ok(host, "play", AtBase);

        Assert.Null(editor.Asked);
        Assert.Equal([PlaySaveRule.Save], editor.Requests.Select(r => r.Save));
    }

    /// <summary>While a play of OpenLancer or the game of a play copy runs, the tool refuses, also before it asks. The
    /// agent calls play_stop first. The Console of the editor gets the refusal.</summary>
    [Theory]
    [InlineData((int)PlayStatus.Running, false)]
    [InlineData((int)PlayStatus.Starting, false)]
    [InlineData((int)PlayStatus.Idle, true)]
    public void WhileAGameRunsTheToolRefuses(int status, bool gameRuns)
    {
        var (host, editor) = Editor();
        editor.PlayStatus = (PlayStatus)status;
        editor.GameRuns = gameRuns;
        editor.UnsavedMod = "ol_mod";

        var (code, detail, message) = Refusal(host, AtBase);

        Assert.Equal(("refused", "OtherPlay", "A game of Play runs, so Play did not start. Call play_stop first."),
            (code, detail, message));
        Assert.Null(editor.Asked);
        Assert.Empty(editor.Requests);
        Assert.Equal("play", Assert.Single(host.Refusals).Tool);
    }

    /// <summary>A game that starts while the question is open stops the request after the answer.</summary>
    [Fact]
    public async Task AGameThatStartsWhileTheQuestionIsOpenStopsTheRequestAfterTheAnswer()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";

        var tail = Tail(host, AtBase, CancellationToken.None);
        editor.GameRuns = true;
        editor.Click(0);
        var result = await tail.WaitAsync(Wait, Token);

        Assert.Equal(("refused", "OtherPlay"), ((string?)result.Content["code"], (string?)result.Content["detail"]));
        Assert.Empty(editor.Requests);
    }

    /// <summary>When the save of Play stops, the agent gets the error of the save with its code (section 7.6 of the MCP1
    /// design): the save gate found errors, files of the mod changed outside the editor, or edits came after the
    /// request. Play gives the message.</summary>
    [Theory]
    [InlineData(EditErrorKind.SaveBlocked, "save_blocked", "SaveBlocked")]
    [InlineData(EditErrorKind.ChangedOnDisk, "changed_on_disk", "ChangedOnDisk")]
    [InlineData(EditErrorKind.Refused, "refused", PlayRequests.NotSavedDetail)]
    public void AnErrorOfTheSaveOfPlayGivesItsCode(EditErrorKind kind, string code, string detail)
    {
        const string message = "The save of Play stopped.";
        var (host, editor) = Editor();
        editor.Result = new TaskCompletionSource<PlayRequestResult>();
        editor.Result.SetResult(PlayRequestResult.Refused(PlayStatus.Idle, detail, message, kind));

        var error = Error(host, "play", AtBase);

        Assert.Equal((code, detail, message), error);
        Assert.Single(editor.Requests);
    }

    /// <summary>One dialog shows the question, so a second agent request gets busy until the first ends: while the
    /// question is open, and after "Save and continue" until Play gives the result.</summary>
    [Fact]
    public async Task ASecondRequestGetsBusyUntilTheRequestThatAskedEnds()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        editor.Result = new TaskCompletionSource<PlayRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Tail(host, AtBase, CancellationToken.None);
        var waits = Refusal(host, AtBase);
        editor.Click(0);
        var runs = Refusal(host, AtBase);
        editor.Result.SetResult(new PlayRequestResult(PlayStatus.Starting));
        await first.WaitAsync(Wait, Token);
        editor.Result = null;
        editor.UnsavedMod = null;
        var later = Call(host, "play", AtBase);

        Assert.Equal(("busy", PlayRequests.AgentWaitsDetail, PlayRequests.AgentWaitsReason), waits);
        Assert.Equal(("busy", PlayRequests.AgentWaitsDetail, PlayRequests.AgentRunsReason), runs);
        Assert.False(later.IsError, later.Text);
        Assert.Equal(2, editor.Requests.Count);
    }

    /// <summary>At the time limit of the tail, the call gives limit and the question closes as Cancel. The close waits
    /// for the UI thread, and a click before it does not start the play either.</summary>
    [Fact]
    public async Task AtTheTailLimitTheQuestionClosesAsCancelAndALateClickStartsNothing()
    {
        var clock = new FakeClock();
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        using var queue = new ToolQueue(host, clock);

        var call = queue.CallAsync("play", Args(AtBase), null, Token);
        Assert.True(queue.RunNext());
        Assert.True(editor.QuestionShows);
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(100, Token);
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await call.WaitAsync(Wait, Token);

        Assert.Equal(("limit", "TailTime"), ((string?)result.Content["code"], (string?)result.Content["detail"]));
        Assert.Equal(1, editor.Withdrawn);
        Assert.True(editor.QuestionShows);
        editor.Click(0);
        editor.RunWithdrawn();
        Assert.Empty(editor.Requests);
        // The request that asked ended, so a new request does not get busy.
        Assert.Null(editor.AgentPlays.Busy(PlayStatus.Idle));
    }

    /// <summary>A cancel of the client closes the question as Cancel. The close runs on the UI thread, after the
    /// cancel.</summary>
    [Fact]
    public async Task ACancelOfTheClientClosesTheQuestionAsCancel()
    {
        var (host, editor) = Editor();
        editor.UnsavedMod = "ol_mod";
        using var queue = new ToolQueue(host, new FakeClock());
        using var cancel = new CancellationTokenSource();

        var call = queue.CallAsync("play", Args(AtBase), null, cancel.Token);
        Assert.True(queue.RunNext());
        Assert.True(editor.QuestionShows);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Wait, Token));
        Assert.Equal(1, editor.Withdrawn);
        editor.RunWithdrawn();
        Assert.False(editor.QuestionShows);
        Assert.Empty(editor.Requests);
        Assert.Null(editor.AgentPlays.Busy(PlayStatus.Idle));
    }

    /// <summary>play_stop stops the game of Play in OpenLancer, else the game of a play copy, as Shift+F5 does. It gives
    /// what it stopped and the Play status.</summary>
    [Theory]
    [InlineData((int)PlayStopped.OpenLancer, (int)PlayStatus.Stopping, "openlancer", "stopping")]
    [InlineData((int)PlayStopped.Game, (int)PlayStatus.Idle, "game", "idle")]
    [InlineData((int)PlayStopped.Nothing, (int)PlayStatus.Idle, "nothing", "idle")]
    public void PlayStopGivesWhatItStoppedAndTheStatus(int stopped, int after, string name, string status)
    {
        var (host, editor) = Editor();
        editor.Stopping = (PlayStopped)stopped;
        editor.AfterStop = (PlayStatus)after;

        var result = Ok(host, "play_stop");

        Assert.Equal((name, status), ((string?)result["stopped"], (string?)result["play"]));
        Assert.Equal(1, editor.Stops);
    }

    /// <summary>Pause refuses play, and lets play_stop run (decision D734).</summary>
    [Fact]
    public void PauseRefusesPlayAndLetsPlayStopRun()
    {
        var (host, editor) = Editor();
        host.TestPaused = true;

        var play = Error(host, "play", AtBase);
        var stop = Ok(host, "play_stop");

        Assert.Equal(("paused", "Paused"), (play.Code, play.Detail));
        Assert.Equal("nothing", (string?)stop["stopped"]);
        Assert.Empty(editor.Requests);
    }

    /// <summary>Only the editor host can play (decision D739).</summary>
    [Fact]
    public void TheHostWithoutAWindowGivesNotAvailable()
    {
        var host = new TestAgentHost(null);

        Assert.Equal(("not_available", "EditorOnly"), Codes(Error(host, "play", AtBase)));
        Assert.Equal(("not_available", "EditorOnly"), Codes(Error(host, "play_stop")));
    }

    /// <summary>A tool of the hidden group Play stays in the list and gives not_available (decision D725).</summary>
    [Fact]
    public void AHiddenPlayGroupGivesNotAvailable()
    {
        var (host, editor) = Editor();
        host.Hidden.Add(ToolGroup.Play);

        Assert.Equal(("not_available", "GroupHidden"), Codes(Error(host, "play", AtBase)));
        Assert.Equal(("not_available", "GroupHidden"), Codes(Error(host, "play_stop")));
        Assert.Equal(0, editor.Stops);
        Assert.DoesNotContain(ToolGroup.Play, host.ShownGroups);
    }

    /// <summary>A play request is not an edit, so edit_batch refuses play and play_stop as steps.</summary>
    [Theory]
    [InlineData("play")]
    [InlineData("play_stop")]
    public void EditBatchRefusesAPlayToolAsAStep(string tool)
    {
        var (ws, session, host) = AgentEditToolTests.Scene();
        using var _ = ws;

        var (code, detail, message) = Error(host, "edit_batch", $$"""{ "steps": [{ "tool": "{{tool}}" }] }""");

        Assert.Equal(("refused", "StepTool"), (code, detail));
        Assert.Equal($"Step 0: {tool} cannot be a step of edit_batch. A step must be an edit tool, for example set_fields.",
            message);
        Assert.False(session.CanUndo);
    }

    /// <summary>The Play menu and the tool share one check (review finding R21 of MCP1-2). The tool gives the reason of
    /// the check for a system that the game data does not have, and the check of Play from camera gives the same
    /// reason.</summary>
    [Fact]
    public void TheMenuAndTheToolGiveTheSameReasonForASystemThatDoesNotExist()
    {
        using var ws = new TestWorkspace();
        var items = PlayMenuTests.Items(ws);
        var (host, editor) = Editor();
        editor.Check = start => PlayChecks.StartBlock(true, items, "ol_sys", start);
        var gone = new PlayStart.InSpace("ol_gone", Vector3.Zero, Quaternion.Identity);

        var (code, detail, message) = Refusal(host, """{ "start": "space", "system": "ol_gone", "position": [0, 0, 0] }""");

        Assert.Equal(("refused", EditorAgentPlay.StartBlockedDetail), (code, detail));
        Assert.Equal("The game data of the profile has no system 'ol_gone'.", message);
        // The camera check of the menu, while the Scene shows a system that the game data does not have now.
        Assert.Equal(message, PlayChecks.StartBlock(true, items, "ol_gone", gone));
        Assert.Empty(editor.Requests);
    }

    /// <summary>The texts of the Play of the agent pass the STE lint. Each group of the agent settings has its
    /// tooltip.</summary>
    [Fact]
    public void TheTextsPassTheSteLint()
    {
        string[] texts =
        [
            EditorAgentPlay.OtherPlayReason,
            .. Enum.GetValues<ToolGroup>().Where(g => g != ToolGroup.Studio).Select(AgentMenu.GroupTip),
        ];

        Assert.Empty(texts.SelectMany(t => SteLint.Problems(t)));
    }

    /// <summary>The state of Play for the agent has a value for each Play status, with the same name.</summary>
    [Fact]
    public void EachPlayStatusHasTheStateOfTheSameName()
    {
        foreach (var status in Enum.GetValues<PlayStatus>())
            Assert.Equal(status.ToString(), EditorAgentPlay.StatusOf(status).ToString());
        Assert.Equal(Enum.GetNames<PlayStatus>().Length, Enum.GetNames<AgentPlayStatus>().Length);
    }

    private static (string?, string?) Codes((string? Code, string? Detail, string? Message) error) => (error.Code, error.Detail);

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Runs a play call that the session part refuses, and gives its code, its detail and its message. A
    /// refusal of the session part has no tail, so no question of the user waits for an answer.</summary>
    private static (string? Code, string? Detail, string? Message) Refusal(AgentHost host, string arguments)
    {
        var result = AgentTools.Run(host, "play", Args(arguments)).Result;
        Assert.NotNull(result);
        Assert.True(result.IsError, result.Text);
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find("play")!.OutputSchema, result.Content));
        return ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["message"]);
    }

    /// <summary>Runs the session part of a play call, then starts its tail.</summary>
    private static Task<ToolResult> Tail(AgentHost host, string arguments, CancellationToken cancel)
    {
        var step = AgentTools.Run(host, "play", Args(arguments));
        Assert.Null(step.Result);
        return step.Tail!(cancel, ToolProgress.None);
    }

    /// <summary>The Play of a test editor: it keeps each request and each stop.</summary>
    internal sealed class FakePlayEditor : PlayRequestsTests.FakeAgentHost, IAgentPlayEditor
    {
        public PlayStatus PlayStatus { get; set; } = PlayStatus.Idle;

        public bool GameRuns { get; set; }

        /// <summary>The checks of the Play menu. The default lets each start pass.</summary>
        public Func<PlayStart, string?> Check { get; set; } = _ => null;

        public List<(PlayStart Start, PlaySaveRule Save)> Requests { get; } = [];

        /// <summary>The result of the next requests, or null for a play that starts at once.</summary>
        public TaskCompletionSource<PlayRequestResult>? Result { get; set; }

        /// <summary>What the next stop stops.</summary>
        public PlayStopped Stopping { get; set; }

        /// <summary>The Play status after a stop.</summary>
        public PlayStatus AfterStop { get; set; } = PlayStatus.Idle;

        public int Stops { get; private set; }

        public string? StartBlock(PlayStart start) => Check(start);

        public Task<PlayRequestResult> Request(PlayStart start, PlaySaveRule save)
        {
            Requests.Add((start, save));
            return Result?.Task ?? Task.FromResult(new PlayRequestResult(PlayStatus.Starting));
        }

        public PlayStopped Stop()
        {
            Stops++;
            PlayStatus = AfterStop;
            return Stopping;
        }
    }
}
