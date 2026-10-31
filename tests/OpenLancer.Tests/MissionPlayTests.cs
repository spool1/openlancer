using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The mission play request (section 7.2 of the M6c design, build step 2.4): the play start of each script, the start
/// of a request, the agent play rule of the user decision of 2026-10-20, and the start that Play prepares after its save.
/// The editor is a fake, and the script is the synthetic script of the start plan tests.
/// </summary>
public sealed class MissionPlayTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly TestWorkspace ws = new();
    private readonly FakeEditor editor;
    private readonly MissionPlay plays;

    public MissionPlayTests()
    {
        editor = new FakeEditor(Path.Combine(ws.Root, "profiles", "p.json"));
        plays = new MissionPlay(editor);
    }

    private static string ScriptPath => MissionTestData.Path;

    public void Dispose() => ws.Dispose();

    private IReadOnlyList<string> Console => editor.Log.Entries.Select(e => e.Message).ToList();

    [Fact]
    public void AScriptPlaysFromTheMissionStartUntilAPlayStartIsSet()
    {
        Assert.Equal("Mission start", plays.StartNameOf(ScriptPath));

        plays.SetPlayStart(ScriptPath, plays.PlayStartOf(ScriptPath) with { Trigger = "t_kill" });

        Assert.Equal("t_kill (approximate)", plays.StartNameOf(ScriptPath));
        plays.SetPlayStart(ScriptPath, plays.PlayStartOf(ScriptPath) with { Trigger = "t_restore" });
        Assert.Equal("Checkpoint t_restore", plays.StartNameOf(ScriptPath));
    }

    /// <summary>F5 plays the play start. A request of the user does what F5 does: no question, and the play start does
    /// not change.</summary>
    [Fact]
    public void F5RequestsAPlayOfThePlayStart()
    {
        editor.UnsavedMod = "act";
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("t_kill", "base:ol_base_a"));

        var result = plays.PlayFromPlayStart(ScriptPath);

        Assert.Null(editor.Asked);
        var start = Assert.IsType<MissionPlayStart>(Assert.Single(editor.Requests));
        Assert.Equal(MissionStartPoint.At("t_kill"), start.Request.Start);
        Assert.Equal((StartPlaceChoice.Base, "ol_base_a"), (start.Request.Place, start.Request.PlaceBase));
        Assert.Equal(EditOrigin.User, start.Request.Origin);
        Assert.True(start.Request.FromPlayStart);
        Assert.Equal([PlaySaveRule.Save], editor.Saves);
        Assert.True(result.Wait(Wait));
        Assert.True(result.Result.Started);
    }

    /// <summary>"Play from here" sets the play start and plays. The place choice of the play start stays.</summary>
    [Fact]
    public void PlayFromHereSetsThePlayStartAndPlays()
    {
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("", "camera"));

        plays.PlayFromHere(ScriptPath, "t_kill");

        Assert.Equal(new PlayStartSetting("t_kill", "camera"), plays.PlayStartOf(ScriptPath));
        var start = Assert.IsType<MissionPlayStart>(Assert.Single(editor.Requests));
        Assert.Equal((MissionStartPoint.At("t_kill"), StartPlaceChoice.Camera), (start.Request.Start, start.Request.Place));
    }

    /// <summary>The Play buttons of the nodes of the tests, with passes that run at once.</summary>
    private static PlayFromHereStates Buttons() => new(_ => null, _ => { }, (pass, _) => Task.FromResult(pass()));

    /// <summary>A click on the Play button of a node (note of 2026-10-27 in section 8.1 of the M6c design) plays after the
    /// canvas, as "Play from here" does: it sets the play start and plays the start that the state of the button shows,
    /// the trigger at the place of the state. The click plays once.</summary>
    [Fact]
    public void AClickOnAPlayButtonPlaysTheStartOfItsState()
    {
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("t_restore", "base:ol_base_a"));
        var states = PlayFromHereStates.Make(editor.Index, editor.Slot, plays.PlayStartOf(ScriptPath),
            System.Threading.CancellationToken.None);
        var state = states["t_kill"];
        var buttons = Buttons();
        Assert.Null(buttons.PlayClick(plays, ScriptPath));

        buttons.Click(editor.Index.Trigger("t_kill")!, 1);
        var result = buttons.PlayClick(plays, ScriptPath);

        Assert.NotNull(result);
        Assert.Null(buttons.PlayClick(plays, ScriptPath));
        Assert.Equal(new PlayStartSetting("t_kill", "base:ol_base_a"), plays.PlayStartOf(ScriptPath));
        var start = Assert.IsType<MissionPlayStart>(Assert.Single(editor.Requests));
        var plan = StartPlan.Make(editor.Index, editor.Slot, start.Request.Start, start.Request.Place, start.Request.PlaceBase);
        Assert.Equal(MissionStartPoint.At("t_kill"), start.Request.Start);
        Assert.True(start.Request.FromPlayStart);
        Assert.Equal((state.CanStart, state.Place), (plan.CanStart, plan.Place));
        Assert.Equal(StartPlaceKind.Base, state.Place.Kind);
    }

    /// <summary>A double-click on a Play button plays once. The release that ends the double-click, and each later click
    /// in the row, does not start the game again. A click that the frame does not play is gone in the next
    /// frame.</summary>
    [Fact]
    public void ADoubleClickOnAPlayButtonPlaysOnce()
    {
        var buttons = Buttons();
        var trigger = editor.Index.Trigger("t_kill")!;

        buttons.Click(trigger, 1);
        buttons.PlayClick(plays, ScriptPath);
        buttons.Click(trigger, 2);
        Assert.Null(buttons.PlayClick(plays, ScriptPath));
        buttons.Click(trigger, 3);
        Assert.Null(buttons.PlayClick(plays, ScriptPath));

        Assert.Single(editor.Requests);
        buttons.Click(trigger, 1);
        buttons.Sync(null, PlayStartSetting.Default, false);
        Assert.Null(buttons.PlayClick(plays, ScriptPath));
        Assert.Single(editor.Requests);
    }

    /// <summary>The agent play rule: with unsaved edits, the editor asks. Cancel gives refused with the reason, and no
    /// play starts. "Save and continue" asks Play for the play, and Play saves first.</summary>
    [Fact]
    public void AnAgentRequestWithUnsavedEditsAsksTheUserFirst()
    {
        editor.UnsavedMod = "act";

        var cancelled = plays.Request(AgentRequest("t_kill"));
        editor.Asked!.Value.Choices.Single(c => c.Label == "Cancel").Action();

        Assert.True(cancelled.Wait(Wait));
        Assert.Equal("The user did not save. Play did not start.", cancelled.Result.Error!.Message);
        Assert.Empty(editor.Requests);

        var saved = plays.Request(AgentRequest("t_kill"));
        editor.Asked!.Value.Choices.Single(c => c.Label == "Save and continue").Action();

        Assert.True(saved.Wait(Wait));
        Assert.True(saved.Result.Started);
        Assert.Single(editor.Requests);
        Assert.Equal([PlaySaveRule.Save], editor.Saves);
    }

    /// <summary>A second agent request while the question of the first is open gives busy, so the first question stays
    /// and its agent gets its answer. After the answer, a new request asks again.</summary>
    [Fact]
    public void ASecondAgentRequestWhileTheQuestionIsOpenGivesBusy()
    {
        editor.UnsavedMod = "act";
        var first = plays.Request(AgentRequest("t_kill"));
        var question = editor.Asked!.Value;

        var second = plays.Request(AgentRequest(null));

        Assert.True(second.Wait(Wait));
        Assert.Equal(EditErrorKind.Busy, second.Result.Error!.Kind);
        Assert.Equal(PlayRequests.AgentWaitsReason, second.Result.Error.Message);
        Assert.False(first.IsCompleted);
        question.Cancel();
        Assert.True(first.Wait(Wait));

        plays.Request(AgentRequest(null));
        Assert.NotSame(question.Choices, editor.Asked!.Value.Choices);
    }

    /// <summary>After "Save and continue", the earlier agent request runs until Play gives its result, for example until
    /// the game answers a restart. A second agent request then gets busy, and the reason does not say that the user did
    /// not answer. After the result, a new request starts.</summary>
    [Fact]
    public async Task ASecondAgentRequestAfterTheAnswerGivesBusyUntilTheFirstEnds()
    {
        var cancel = TestContext.Current.CancellationToken;
        editor.UnsavedMod = "act";
        var running = new TaskCompletionSource<PlayRequestResult>();
        editor.Result = running;
        var first = plays.Request(AgentRequest("t_kill"));
        editor.Asked!.Value.Choices.Single(c => c.Label == "Save and continue").Action();

        var second = await plays.Request(AgentRequest(null)).WaitAsync(Wait, cancel);

        Assert.Equal(EditErrorKind.Busy, second.Error!.Kind);
        Assert.Equal(PlayRequests.AgentWaitsDetail, second.Error.Detail);
        Assert.Equal(PlayRequests.AgentRunsReason, second.Error.Message);
        Assert.False(first.IsCompleted);

        running.SetResult(new PlayRequestResult(PlayStatus.Running));
        Assert.True((await first.WaitAsync(Wait, cancel)).Started);
        editor.Result = null;
        editor.UnsavedMod = null;
        var third = await plays.Request(AgentRequest(null)).WaitAsync(Wait, cancel);

        Assert.True(third.Started);
        Assert.Equal(2, editor.Requests.Count);
    }

    /// <summary>Only a base that the user picked keeps the start check of a mission play away from the Scene, so Play
    /// waits for the Scene in the other cases. A base choice without a base uses the suggested place.</summary>
    [Theory]
    [InlineData(StartPlaceChoice.Suggested, null, true)]
    [InlineData(StartPlaceChoice.Camera, null, true)]
    [InlineData(StartPlaceChoice.Base, null, true)]
    [InlineData(StartPlaceChoice.Base, "ol_base_a", false)]
    public void AStartReadsTheSceneUnlessItsPlaceIsAPickedBase(StartPlaceChoice place, string? dock, bool reads)
    {
        var request = new MissionPlayRequest(ScriptPath, MissionStartPoint.MissionStart) { Place = place, PlaceBase = dock };

        Assert.Equal(reads, new MissionPlayStart(plays, request).ReadsScene);
    }

    /// <summary>The user can start another play while the question is open. "Save and continue" then checks the rule
    /// again, and the agent gets refused, so the request does not close the play of the user.</summary>
    [Fact]
    public void SaveAndContinueChecksTheRuleOfAnotherPlayAgain()
    {
        editor.UnsavedMod = "act";
        var result = plays.Request(AgentRequest("t_kill"));
        editor.PlayStatus = PlayStatus.Running;

        editor.Asked!.Value.Choices.Single(c => c.Label == "Save and continue").Action();

        Assert.True(result.Wait(Wait));
        Assert.Equal(MissionPlay.OtherPlayDetail, result.Result.Error!.Detail);
        Assert.Empty(editor.Requests);
    }

    /// <summary>The setting skips the question, and a request without unsaved edits starts at once.</summary>
    [Theory]
    [InlineData("act", true)]
    [InlineData(null, false)]
    public void AnAgentRequestStartsAtOnceWithTheSettingOrWithoutUnsavedEdits(string? unsaved, bool autoSave)
    {
        editor.UnsavedMod = unsaved;
        editor.AutoSaveAgentPlay = autoSave;

        var result = plays.Request(AgentRequest("t_kill"));

        Assert.Null(editor.Asked);
        Assert.Single(editor.Requests);
        // Without the setting, nobody allowed a save, so Play refuses edits that come before its save.
        Assert.Equal([autoSave ? PlaySaveRule.Save : PlaySaveRule.NoEdits], editor.Saves);
        Assert.True(result.Wait(Wait));
        Assert.True(result.Result.Started);
    }

    /// <summary>A request of an agent gives refused while another play runs: a play from the camera, or the game of a
    /// play copy. While a mission play runs, the game starts again at the new start, so the request goes on.</summary>
    [Fact]
    public void AnAgentRequestWhileAnotherPlayRunsIsRefused()
    {
        editor.GameRuns = true;
        Assert.Equal(MissionPlay.OtherPlayDetail, plays.Request(AgentRequest(null)).Result.Error!.Detail);

        editor.GameRuns = false;
        editor.PlayStatus = PlayStatus.Running;
        Assert.Equal(MissionPlay.OtherPlayDetail, plays.Request(AgentRequest(null)).Result.Error!.Detail);

        editor.RunningMission = new MissionPlayStart(plays, new MissionPlayRequest(ScriptPath, MissionStartPoint.MissionStart));
        Assert.Null(plays.Request(AgentRequest(null)).Result.Error);
        Assert.Single(editor.Requests);
    }

    /// <summary>A plan that cannot start refuses the request of an agent with the reason of the plan, before the
    /// question. A trigger that the script does not have gives not_found.</summary>
    [Fact]
    public void AnAgentRequestWithAPlanThatCannotStartIsRefusedBeforeTheQuestion()
    {
        editor.UnsavedMod = "act";
        editor.Slot = null;

        var noSlot = plays.Request(AgentRequest("t_kill")).Result;

        Assert.Equal(EditErrorKind.Refused, noSlot.Error!.Kind);
        Assert.Equal(StartPlan.NoSlotReason, noSlot.Error.Message);
        Assert.Equal(StartRefusal.NoSlot, noSlot.Plan!.Refusal);
        editor.Slot = StartPlanTests.Slot;
        Assert.Equal(EditErrorKind.NotFound, plays.Request(AgentRequest("t_gone")).Result.Error!.Kind);
        Assert.Equal(StartPlan.NoTriggerReason("t_gone"), plays.Request(AgentRequest("t_gone")).Result.Error!.Message);
        editor.Missing = "The session has no such mission script.";
        var noScript = plays.Request(AgentRequest(null)).Result.Error!;
        Assert.Equal((EditErrorKind.NotFound, MissionErrors.ScriptNotFound), (noScript.Kind, noScript.Detail));
        Assert.Null(editor.Asked);
        Assert.Empty(editor.Requests);
    }

    /// <summary>The start makes its plan from the model when Play prepares it, after the save. So the plan reads the
    /// script that the game reads, not the model of the request.</summary>
    [Fact]
    public void ThePlanUsesTheModelOfTheSavedScript()
    {
        plays.PlayFromHere(ScriptPath, "t_kill");
        var start = (MissionPlayStart)editor.Requests[0];
        // The save writes an edit after the request: t_launch also starts a timer beside t_kill.
        editor.Index = MissionTestData.Index(StartPlanTests.Script.Replace("Act_ActTrig = t_fail",
            "Act_ActTrig = t_fail\nAct_ActTrig = t_extra") + "\n[Trigger]\nnickname = t_extra\nCnd_Timer = 30\n");

        var fromSave = start.Prepare();

        Assert.NotNull(fromSave);
        Assert.Contains("t_extra", start.Plan!.ActiveSet);
        Assert.Equal(PlayFolder.StartSavePath(editor.ProfilePath!), fromSave.Save);
        Assert.Equal(PlayFolder.TestLayerPath(editor.ProfilePath!), fromSave.TestLayer);
        Assert.True(File.Exists(fromSave.Save));
        Assert.True(Directory.Exists(fromSave.TestLayer));
        Assert.Equal("t_kill (approximate)", start.StartName);
        Assert.Equal("M99", start.Slot);
        Assert.True(start.Approximate);
        Assert.Equal(start.Plan.Setup.Select(t => t.Nickname), start.SetupTriggers);
    }

    /// <summary>Play makes the plan again with the place of the play after the start check (the note of 2026-10-28 in
    /// section 5.3 of the M6c design). A start at the Scene camera then makes only the world of the camera system, and
    /// the companions start next to the camera.</summary>
    [Fact]
    public void ThePlanOfThePlayUsesThePlaceOfThePlay()
    {
        editor.Index = MissionTestData.Index(StartWorldTests.Script(), StartWorldTests.World());
        editor.Camera = new StartSavePlace("ol_sys_b", null, new Vector3(-5000, 0, 0), Quaternion.Identity);
        plays.Request(new MissionPlayRequest(ScriptPath, MissionStartPoint.At("t_chosen")) { Place = StartPlaceChoice.Camera });
        var start = (MissionPlayStart)editor.Requests[0];

        var fromSave = start.Prepare();

        Assert.NotNull(fromSave);
        Assert.Contains(start.Plan!.LeftOut, l => l.Object == "ol_guard");
        Assert.DoesNotContain(StartWorld.CameraWarning, start.Plan.Warnings);
        var patch = Directory.GetFiles(fromSave.TestLayer!, "*.ini", SearchOption.AllDirectories).Single();
        var text = File.ReadAllText(patch);
        Assert.Contains("Act_RelocateShip = ol_wing, -5300, 0, 0, 1, 0, 0, 0", text);
        Assert.DoesNotContain("ol_guard", text);
    }

    private const string PlayStartNote = "The play start of M99 named trigger 't_gone'. The script does not have this " +
                                         "trigger now. The play start is the mission start again.";

    private const string OtherStartNote = "This play of M99 started at trigger 't_gone'. The script does not have this " +
                                          "trigger now, so the game plays the mission start.";

    /// <summary>A start whose trigger is gone becomes the mission start, and the Console tells it (decision D762). Only a
    /// request of the user from the play start also makes the play start the mission start. A Play menu item, a request
    /// of an agent and a play start that the user changed during the play keep the play start of the user.</summary>
    [Theory]
    [InlineData(EditOrigin.User, true, "t_gone", "", PlayStartNote)]
    [InlineData(EditOrigin.User, false, "t_gone", "t_gone", OtherStartNote)]
    [InlineData(EditOrigin.Agent, true, "t_gone", "t_gone", OtherStartNote)]
    [InlineData(EditOrigin.User, true, "t_kill", "t_kill", OtherStartNote)]
    public void AMissingStartFallsBackToTheMissionStart(EditOrigin origin, bool fromPlayStart, string playStart,
        string playStartAfter, string note)
    {
        plays.SetPlayStart(ScriptPath, new PlayStartSetting(playStart, "suggested"));
        var start = new MissionPlayStart(plays, new MissionPlayRequest(ScriptPath, MissionStartPoint.At("t_gone"))
        {
            Origin = origin,
            FromPlayStart = fromPlayStart,
        });

        Assert.NotNull(start.Prepare());

        Assert.Equal(StartKind.MissionStart, start.Plan!.Kind);
        Assert.Equal([note], Console);
        Assert.Equal(playStartAfter, plays.PlayStartOf(ScriptPath).Trigger);
    }

    /// <summary>A start that cannot start keeps its reason for the result of the request. Play gives it to the Console
    /// with the text of a start or of a restart (see <c>PlayControllerTests</c>).</summary>
    [Fact]
    public void AStartThatCannotStartTellsWhy()
    {
        editor.Slot = null;
        var start = new MissionPlayStart(plays, new MissionPlayRequest(ScriptPath, MissionStartPoint.MissionStart));

        Assert.Null(start.Prepare());

        Assert.Equal(StartPlan.NoSlotReason, start.Refusal);
        Assert.Empty(Console);
        Assert.Null(start.Plan);
    }

    /// <summary>
    /// A rename of the trigger of the start during a mission play moves the start of the running play, of its restart and
    /// of a request that waits (section 5.2 of the M6c design, review fix). So the restart after the save of the rename
    /// starts at the renamed trigger, and the play start of the user follows the rename. An Undo moves it back.
    /// </summary>
    [Fact]
    public void ARestartUsesTheStartOfTheRunningPlayAfterARenameAndItsUndo()
    {
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("t_kill", "suggested"));
        plays.PlayFromPlayStart(ScriptPath);
        var running = (MissionPlayStart)editor.Requests.Single();
        editor.Kept.Add(running);
        Assert.NotNull(running.Prepare());

        plays.Renamed(Renames(("t_kill", "t_kill_two")));
        // The save writes the rename, so the model of the saved script has the new name.
        editor.Index = MissionTestData.Index(StartPlanTests.Script.Replace("t_kill", "t_kill_two"));

        Assert.NotNull(running.Prepare());
        Assert.Equal(MissionStartPoint.At("t_kill_two"), running.Plan!.Start);
        Assert.Equal(StartKind.Approximate, running.Plan.Kind);
        Assert.Equal("t_kill_two", plays.PlayStartOf(ScriptPath).Trigger);
        Assert.Empty(Console);

        plays.Renamed(Renames(("t_kill_two", "t_kill")));
        editor.Index = MissionTestData.Index(StartPlanTests.Script);
        Assert.NotNull(running.Prepare());
        Assert.Equal(MissionStartPoint.At("t_kill"), running.Plan!.Start);
        Assert.Equal("t_kill", plays.PlayStartOf(ScriptPath).Trigger);
    }

    /// <summary>The name of a start comes from the kind of the plan: a script without a story slot gives "Test script"
    /// on the toolbar chip, as on the Play start page.</summary>
    [Fact]
    public void TheNameOfTheStartIsTheNameOfThePlan()
    {
        plays.SetPlayStart(ScriptPath, new PlayStartSetting("t_restore", "suggested"));
        Assert.Equal("Checkpoint t_restore", plays.StartNameOf(ScriptPath));

        editor.Slot = null;

        Assert.Equal("Test script", plays.StartNameOf(ScriptPath));
        Assert.Equal(StartPlan.Make(editor.Index, null, MissionStartPoint.At("t_restore")).StartName,
            plays.StartNameOf(ScriptPath));
    }

    private static MissionScriptsChange Renames(params (string From, string To)[] pairs) =>
        new(new Dictionary<string, long>(),
            pairs.Select(p => (new TriggerRef(ScriptPath, p.From), new TriggerRef(ScriptPath, p.To))).ToList());

    /// <summary>The play start follows a rename of its trigger, also the Undo and the Redo of the rename (section 5.2 of
    /// the M6c design). The renames come from the event of each change of the history.</summary>
    [Fact]
    public void ThePlayStartFollowsARenameItsUndoAndItsRedo()
    {
        using var m = Create();
        var settings = EditorSettings.Transient();
        var profile = m.Session.Profile.FilePath;
        settings.SetPlayStart(profile, Script, new PlayStartSetting("ol_end", "suggested"));
        m.Missions.ScriptsChanged += change => MissionPlay.Rename(settings, profile, change);

        m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));
        Assert.Equal("ol_finish", settings.PlayStartOf(profile, Script).Trigger);

        m.Session.Undo();
        Assert.Equal("ol_end", settings.PlayStartOf(profile, Script).Trigger);

        m.Session.Redo();
        Assert.Equal("ol_finish", settings.PlayStartOf(profile, Script).Trigger);

        // A rename of another trigger does not move the play start.
        m.Apply(new RenameTrigger(T("ol_wave"), "ol_wave_2"));
        Assert.Equal("ol_finish", settings.PlayStartOf(profile, Script).Trigger);
    }

    /// <summary>The settings keep the play start of each profile and script. The mission start with the suggested place
    /// keeps no entry, and a read of the settings file gives the same play starts.</summary>
    [Fact]
    public void TheSettingsKeepThePlayStarts()
    {
        var settings = EditorSettings.Transient();
        const string profile = @"C:\p\sample.json";
        settings.SetPlayStart(profile, ScriptPath, new PlayStartSetting("t_kill", "base:ol_base_a"));

        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        var start = read.PlayStartOf(profile, ScriptPath.ToLowerInvariant());
        Assert.Equal(MissionStartPoint.At("t_kill"), start.Start);
        Assert.Equal((StartPlaceChoice.Base, "ol_base_a"), start.PlaceChoice);
        read.SetPlayStart(profile, ScriptPath, new PlayStartSetting("", "suggested"));
        Assert.Empty(read.PlayStarts[profile]);
        Assert.Equal((StartPlaceChoice.Suggested, (string?)null), new PlayStartSetting("", "unknown").PlaceChoice);
        Assert.Equal(new PlayStartSetting("", "camera"),
            PlayStartSetting.Of(MissionStartPoint.MissionStart, StartPlaceChoice.Camera, null));
    }

    /// <summary>The play start of a mission is an item of the Mission tab: the toolbar chip selects it, and the
    /// Inspector shows the Play start page. A run of the checks keeps the selection while the run has the
    /// script.</summary>
    [Fact]
    public void ThePlayStartIsAnItemThatARunKeeps()
    {
        var item = MissionRef.PlayStartOf("DATA/MISSIONS/TEST/test.ini");
        using var folder = new MissionFolder();
        folder.Write(MissionTestData.Path, StartPlanTests.Script);
        var result = MissionRun.Execute(folder.Inputs());

        Assert.Equal((MissionTestData.Path, MissionItemKind.PlayStart), (item.Script, item.Kind));
        Assert.Null(item.Section);
        Assert.Equal(item, MissionWorkspace.Valid(result, item));
        Assert.NotEqual(MissionRef.Of(MissionTestData.Path), item);
    }

    private static MissionPlayRequest AgentRequest(string? trigger) =>
        new(ScriptPath, trigger == null ? MissionStartPoint.MissionStart : MissionStartPoint.At(trigger))
        {
            Origin = EditOrigin.Agent,
        };

    /// <summary>The editor side of the mission play. It gives the synthetic script of the start plan tests and keeps
    /// each request and the last question.</summary>
    private sealed class FakeEditor(string profile) : IMissionPlayEditor
    {
        public string? ProfilePath { get; } = profile;

        public EditorSettings Settings { get; } = EditorSettings.Transient();

        public ConsoleLog Log { get; } = new();

        public MissionIndex Index { get; set; } = MissionTestData.Index(StartPlanTests.Script);

        public StorySlot? Slot { get; set; } = StartPlanTests.Slot;

        public string? PlayPackage => null;

        public PlayStatus PlayStatus { get; set; } = PlayStatus.Idle;

        public IMissionStart? RunningMission { get; set; }

        /// <summary>The starts that Play keeps, for the renames.</summary>
        public List<IMissionStart> Kept { get; } = [];

        public IReadOnlyList<IMissionStart> MissionStarts => Kept;

        public bool GameRuns { get; set; }

        public string? UnsavedMod { get; set; }

        public bool AutoSaveAgentPlay { get; set; }

        public List<IMissionStart> Requests { get; } = [];

        /// <summary>The save rule of each request, in order.</summary>
        public List<PlaySaveRule> Saves { get; } = [];

        public (string Title, string Message, IReadOnlyList<Choice> Choices, Action Cancel)? Asked { get; private set; }

        public (MissionIndex Index, StorySlot? Slot)? IndexOf(string script) => (Index, Slot);

        /// <summary>The reason of a script that the session does not have, or null.</summary>
        public string? Missing { get; set; }

        public (MissionIndex Index, StorySlot? Slot)? ModelOf(string script, out string? problem)
        {
            problem = Missing;
            return Missing == null ? (Index, Slot) : null;
        }

        /// <summary>The place of a start at the Scene camera, or null for the point of the plan.</summary>
        public StartSavePlace? Camera { get; set; }

        public StartSavePlace? PlaceOf(StartPlace place, out string? problem)
        {
            problem = null;
            if (place.Kind == StartPlaceKind.Camera && Camera != null)
                return Camera;
            return place.Kind == StartPlaceKind.Base
                ? StartSavePlace.AtBase("ol_sys_a", place.Base!)
                : new StartSavePlace(place.System ?? "ol_sys_a", null, place.Position, place.Orientation);
        }

        /// <summary>The result of the next requests, or null for a play that started at once.</summary>
        public TaskCompletionSource<PlayRequestResult>? Result { get; set; }

        public Task<PlayRequestResult> RequestMission(IMissionStart start, string? package, PlaySaveRule save)
        {
            Requests.Add(start);
            Saves.Add(save);
            return Result?.Task ?? Task.FromResult(new PlayRequestResult(PlayStatus.Starting));
        }

        public void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel) =>
            Asked = (title, message, choices, cancel);

        public void Withdraw(IReadOnlyList<Choice> choices)
        {
        }

        public AgentPlayRule AgentPlays { get; } = new();
    }
}
