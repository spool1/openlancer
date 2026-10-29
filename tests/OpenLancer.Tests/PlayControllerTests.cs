using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer.Data.GameData.World;
using LibreLancer.World;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// Play in the editor (section 9 of the M4 design), with a fake editor, a stub game program, a queued UI thread and a
/// fake clock (section 16.1). The numbers in the comments are the cases of the design.
/// </summary>
public sealed class PlayControllerTests : IDisposable
{
    private static readonly string Waiting = PlayProtocol.Write(new PlayEvent.Waiting(2300));

    private static readonly string Spawned = PlayProtocol.Write(
        new PlayEvent.Spawned("Br01", null, new Vector3(-33270, 0, -25039), "ge_fighter", "Starflier", 450));

    private static readonly PlayStart.InSpace Space = new("br01", new Vector3(-33270, 0, -25039), Quaternion.Identity);

    /// <summary>The start of <see cref="Space"/> after the start check with <see cref="DeathZone"/>.</summary>
    private static readonly Vector3 MovedStart = new(-33270, 0, -24839);

    private const string MovedLine = "Play from camera: the camera is inside Zone_Br01_death. The ship starts 200 units " +
                                     "behind the camera.";

    /// <summary>A game that loads its data, and spawns at start.</summary>
    private static readonly StubPlan Game = new([Waiting], [Spawned]);

    private static readonly Diagnostic HullError = Diagnostic.Error(@"C:\mods\proof\patches\goods.ini", "no_such_hull");

    private readonly TestWorkspace ws = new();
    private readonly QueuedUIThread ui = new();
    private readonly FakeEditor editor;
    private readonly PlayStub stub;
    private readonly List<PlayArgs> starts = [];
    private readonly PlayController play;
    private StubPlan plan = Game;
    private TimeSpan now = TimeSpan.FromSeconds(100);

    public PlayControllerTests()
    {
        editor = new FakeEditor(ui);
        stub = new PlayStub(Path.Combine(ws.Root, "stub"));
        play = new PlayController(editor, args =>
        {
            starts.Add(args);
            return stub.Command(plan);
        }, () => now);
    }

    public void Dispose()
    {
        play.Dispose();
        stub.KillAll();
        ws.Dispose();
    }

    private IReadOnlyList<string> Console => editor.Log.Entries.Select(e => e.Message).ToList();

    /// <summary>(1) No edits and a passed validation of the current save: the game gets start at its waiting event.</summary>
    [Fact]
    public void WithoutEditsAndWithAPassedValidationTheGameGetsStartAtOnce()
    {
        editor.Commit(1);
        editor.Finish(1);

        RequestPlay();

        Assert.Equal(1, editor.SaveCalls);
        Assert.Equal(1, Assert.Single(starts).Save);
        Assert.Equal(PlayStatus.Starting, play.Status);
        Assert.Equal("Play: starting...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.Contains("Playing in New London (Br01) with Starflier ge_fighter. From Play to flight: 0.0 s.", Console);
        Assert.Equal("Playing: New London (Br01), save 1", play.StatusText);
        play.Stop();
        Assert.Equal("Play: stopping...", play.StatusText);
        RunUntil(PlayStatus.Idle);
        Assert.Null(play.StatusText);
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.Equal(0, editor.ValidateNowCalls);
        Assert.Equal("The game closed.", Console[^1]);
    }

    /// <summary>A new session without a validation: Play validates the saved files, and starts when that passes.</summary>
    [Fact]
    public void WithoutAValidationPlayValidatesTheSavedFiles()
    {
        RequestPlay();

        Assert.Equal(1, editor.ValidateNowCalls);
        Assert.Equal(0, Assert.Single(starts).Save);
        ui.Run(1);
        Assert.Equal(PlayStatus.Validating, play.Status);
        Assert.Equal("Play: validating the saved files...", play.StatusText);
        editor.Finish(0);
        RunUntil(PlayStatus.Running);
        // A game without a save of the session names no save.
        Assert.Equal("Playing: New London (Br01)", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>(2) Unsaved edits: the save runs, and start waits for the passed validation of that save.</summary>
    [Fact]
    public void UnsavedEditsAreSavedAndStartWaitsForThePassedValidationOfTheSave()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.SaveResult = new SaveOutcome(true, 2, false);

        RequestPlay();

        Assert.Equal(2, Assert.Single(starts).Save);
        Assert.Contains("Play: saved mod 'proof' (save 2). Waiting for its validation.", Console);
        ui.Run(1);
        play.Update(false, false);
        Assert.Equal(PlayStatus.Validating, play.Status);
        Assert.Equal("Play: validating save 2...", play.StatusText);
        // A start would give a spawn now.
        ui.RunUntilQuiet(TimeSpan.FromMilliseconds(500));
        Assert.Equal(PlayStatus.Validating, play.Status);

        editor.Finish(2);

        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.Equal(0, editor.ValidateNowCalls);
    }

    /// <summary>(3) Validation errors: stop, the Console line, then status Idle at the exit.</summary>
    [Fact]
    public void ValidationErrorsStopTheGame()
    {
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(1);

        editor.Finish(2, HullError, Diagnostic.Warning("Engine", "a warning"));

        Assert.Contains("Play stopped: the validation of save 2 found 1 error. The Console lists them.", Console);
        Assert.Equal(PlayStatus.Stopping, play.Status);
        RunUntil(PlayStatus.Idle);
        Assert.Equal(["stop"], stub.Input(0));
        Assert.DoesNotContain(Console, m => m.StartsWith("Playing") || m == "The game closed.");
    }

    /// <summary>Errors that come while the game loads stop it at once. The gate does not wait for waiting.</summary>
    [Fact]
    public void ValidationErrorsBeforeWaitingStopTheGameAtOnce()
    {
        plan = new StubPlan([], [Spawned]);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();

        editor.Finish(2, HullError, HullError);

        Assert.Contains("Play stopped: the validation of save 2 found 2 errors. The Console lists them.", Console);
        RunUntil(PlayStatus.Idle);
        Assert.Equal(["stop"], stub.Input(0));
    }

    /// <summary>(4) The validation could not run: start, and a warning (D43).</summary>
    [Fact]
    public void AValidationThatCouldNotRunStartsWithAWarning()
    {
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(1);

        editor.FailToRun(2);

        Assert.Contains(editor.Log.Entries, e => e.Severity == ConsoleSeverity.Warning &&
                                                 e.Message == "Play goes on without a validation: the validation of " +
                                                 "save 2 could not run.");
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>(5) A save that does not save, or a save that is not complete: no game starts.</summary>
    [Theory]
    [InlineData(false, 0, false, "Play stopped: the save did not complete. The Console tells why.")]
    [InlineData(true, 3, true, "Play stopped: save 3 is not complete.")]
    public void ASaveThatDoesNotSaveStartsNoGame(bool saved, int number, bool incomplete, string line)
    {
        editor.SaveResult = new SaveOutcome(saved, number, incomplete);

        RequestPlay();

        Assert.Equal(1, editor.SaveCalls);
        Assert.Empty(starts);
        Assert.Equal(PlayStatus.Idle, play.Status);
        Assert.Contains(line, Console);
        play.Update(false, false);
        Assert.Empty(starts);
    }

    /// <summary>
    /// (6) The repo gate is closed (D5), or another reason stops the edits: Play saves nothing, shows no dialog and
    /// plays the saved files. Without an active mod, Play plays all mods.
    /// </summary>
    [Theory]
    [InlineData("proof", "The mod 'proof' is part of the OpenLancer repo, so its files are tracked files. Allow its " +
                         "edits for this session first.",
        "Play uses the saved files of mod 'proof'. The editor takes no edits for it now: The mod 'proof' is part of " +
        "the OpenLancer repo, so its files are tracked files. Allow its edits for this session first.")]
    [InlineData(null, null, "No mod is active, so Play uses the saved files of all mods of the profile.")]
    public void WithoutEditsToSavePlaySavesNothingAndStarts(string? mod, string? reason, string line)
    {
        editor.Session = editor.Session! with { ActiveMod = mod, NoEditReason = reason };
        editor.Commit(1);
        editor.Finish(1);

        RequestPlay();

        Assert.Equal(0, editor.SaveCalls);
        Assert.Equal(mod, Assert.Single(starts).ActiveMod);
        Assert.Contains(line, Console);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>Play writes next to the profile file, so a profile file in its base folder starts no game and saves
    /// nothing (decision D214 of the M11a design).</summary>
    [Fact]
    public void PlayRefusesAProfileFileInItsBaseFolder()
    {
        editor.Session = new PlaySession(Path.Combine(ws.Base, "p.json"), "proof", 0, 0, null, [], ws.Base);

        RequestPlay();

        Assert.Equal(0, editor.SaveCalls);
        Assert.Empty(starts);
        Assert.Equal(PlayStatus.Idle, play.Status);
        Assert.Contains(OpenLancer.Data.Games.GameTexts.PlayRefused, Console);
    }

    /// <summary>(7) A request while a field or a dialog is open runs at the first update without one.</summary>
    [Fact]
    public void ARequestWaitsWhileAFieldOrADialogIsOpen()
    {
        editor.Commit(1);
        editor.Finish(1);
        play.Request(Space, null);

        play.Update(editing: true, dialogOpen: false);
        play.Update(editing: false, dialogOpen: true);
        play.Update(editing: true, dialogOpen: true);

        Assert.Equal(0, editor.SaveCalls);
        Assert.Empty(starts);
        Assert.Equal(PlayStatus.Idle, play.Status);
        play.Update(editing: false, dialogOpen: false);
        Assert.Equal(1, editor.SaveCalls);
        Assert.Single(starts);
        play.Update(editing: false, dialogOpen: false);
        Assert.Single(starts);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>
    /// The start check of Play from camera runs when the game starts (section 9.5). A request that Stop drops, and a save
    /// that does not save, give no line of the check. The game gets the start that the check moved.
    /// </summary>
    [Fact]
    public void TheStartCheckOfPlayFromCameraRunsOnlyForAGameThatStarts()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.Shapes.Add(DeathZone());

        play.Request(Space, null);
        play.Stop();
        play.Update(false, false);
        editor.SaveResult = new SaveOutcome(false, 0, false);
        RequestPlay();

        Assert.Empty(starts);
        Assert.DoesNotContain(Console, m => m.StartsWith("Play from camera", StringComparison.Ordinal));

        editor.SaveResult = new SaveOutcome(true, 0, false);
        RequestPlay();

        var start = Assert.IsType<PlayStart.InSpace>(Assert.Single(starts).Start);
        Near(MovedStart, start.Position);
        Assert.Equal(Space with { Position = start.Position }, start);
        Assert.Equal(1, Console.Count(m => m == MovedLine));
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>
    /// A start at the camera waits until the Scene shows each edit (section 9.5). Here the click on Play ends a field
    /// whose edit puts a death zone around the camera. The field commits after the flush of the Scene in that frame, so
    /// the Scene shows the zone one frame later.
    /// </summary>
    [Fact]
    public void ACameraStartWaitsUntilTheSceneShowsTheEditOfTheFieldThatTheClickEnded()
    {
        editor.Commit(1);
        editor.Finish(1);
        play.Request(Space, null);
        editor.SceneBehind = true;

        play.Update(false, false);

        Assert.Equal(0, editor.SaveCalls);
        Assert.Empty(starts);

        // The next frame: the flush of the Scene applies the edit.
        editor.SceneBehind = false;
        editor.Shapes.Add(DeathZone());
        play.Update(false, false);

        Near(MovedStart, Assert.IsType<PlayStart.InSpace>(Assert.Single(starts).Start).Position);
        Assert.Contains(MovedLine, Console);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>A start at a base has no start check, so it does not wait for the Scene.</summary>
    [Fact]
    public void AStartAtABaseDoesNotWaitForTheScene()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.SceneBehind = true;

        RequestPlay(new PlayStart.AtBase("Br01_01_Base"));

        Assert.Single(starts);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>
    /// Play from camera stops before its save when the Scene does not show the system of the camera now, for example
    /// after the user opened another system while the running game closed.
    /// </summary>
    [Fact]
    public void PlayFromCameraStopsWhenTheSceneDoesNotShowTheSystemOfTheCamera()
    {
        editor.SaveResult = new SaveOutcome(true, 2, false);

        RequestPlay(new PlayStart.InSpace("li01", Vector3.Zero, Quaternion.Identity));

        Assert.Equal(0, editor.SaveCalls);
        Assert.Empty(starts);
        Assert.Equal(PlayStatus.Idle, play.Status);
        Assert.Equal("Play from camera stopped: the Scene does not show li01 now, so Play cannot check the start at " +
                     "the camera. Open the system, then press Play.", Console[^1]);
    }

    /// <summary>
    /// A start in space of the agent tool play has the start check of Play from camera (section 7.6 of the MCP1 design).
    /// The agent gives a point, not the camera, so the lines of the check name the start point. A system that the Scene
    /// does not show stops the play before its save, with the words of the agent.
    /// </summary>
    [Fact]
    public void AnAgentStartInSpaceHasTheStartCheckWithTheWordsOfThePoint()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.Shapes.Add(DeathZone());

        var started = play.Request(Space, null, PlaySaveRule.NoEdits, EditOrigin.Agent);
        play.Update(false, false);

        Assert.True(started.IsCompletedSuccessfully && started.Result.Started);
        Near(MovedStart, Assert.IsType<PlayStart.InSpace>(Assert.Single(starts).Start).Position);
        Assert.Contains("Agent Play: the start point is inside Zone_Br01_death. The ship starts 200 units behind the " +
                        "start point.", Console);
        Assert.DoesNotContain(Console, m => m.StartsWith("Play from camera", StringComparison.Ordinal));
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();

        var elsewhere = play.Request(new PlayStart.InSpace("li01", Vector3.Zero, Quaternion.Identity), null,
            PlaySaveRule.NoEdits, EditOrigin.Agent);
        play.Update(false, false);

        Assert.Equal("NoSceneSystem", elsewhere.Result.Error!.Detail);
        Assert.Equal("Agent Play stopped: the Scene does not show li01 now, so Play cannot check the start point. Open " +
                     "the system, then start Play again.", Console[^1]);
        Assert.Equal(Console[^1], elsewhere.Result.Error.Message);
        Assert.Empty(SteLint.Problems(Console[^1]));
        Assert.Single(starts);
    }

    /// <summary>(8) A second request while the game runs: stop, the exit, then a new game with the new start.</summary>
    [Fact]
    public void ASecondPlayStopsTheGameAndStartsANewOneAfterItsExit()
    {
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        RunUntil(PlayStatus.Running);
        var atBase = new PlayStart.AtBase("Br01_01_Base");

        play.Request(atBase, "le_package");
        play.Update(false, false);

        Assert.Equal(PlayStatus.Stopping, play.Status);
        Assert.Single(starts);
        ui.RunUntil(() =>
        {
            play.Update(false, false);
            return starts.Count == 2;
        }, "the start of the second game");
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.Equal((atBase, "le_package"), (starts[1].Start, starts[1].Package));
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(1));
    }

    /// <summary>A second request for a game that does not close: after 5 s the editor ends it and starts the new one.</summary>
    [Fact]
    public void ASecondPlayEndsAGameThatDoesNotCloseIn5S()
    {
        plan = Game with { IgnoreStop = true };
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        RunUntil(PlayStatus.Running);
        play.Request(Space, null);
        play.Update(false, false);

        now += TimeSpan.FromSeconds(4.9);
        play.Update(false, false);
        Assert.True(stub.Runs(0));
        now += TimeSpan.FromSeconds(0.1);
        play.Update(false, false);

        ui.RunUntil(() =>
        {
            play.Update(false, false);
            return starts.Count == 2;
        }, "the start of the second game");
        Assert.False(stub.Runs(0));
        Assert.Contains("The game did not close in 5 s, so the editor ended it to play again.", Console);
        RunUntil(PlayStatus.Running);
        play.Stop();
        now += PlayController.ExitLimit;
        play.Stop();
        RunUntil(PlayStatus.Idle);
    }

    /// <summary>(9) Stop, and no exit for 5 s on the fake clock: Stop is Force stop, and it kills the game.</summary>
    [Fact]
    public void StopIsForceStopAfter5SAndKillsTheGame()
    {
        plan = Game with { IgnoreStop = true };
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        RunUntil(PlayStatus.Running);

        play.Stop();
        Assert.Equal(PlayStatus.Stopping, play.Status);
        Assert.False(play.ForceStop);
        // The stub read stop, and it runs on.
        stub.WaitForInput(0, 2);
        now += TimeSpan.FromSeconds(4.9);
        play.Update(false, false);
        Assert.False(play.ForceStop);
        now += TimeSpan.FromSeconds(0.1);
        play.Update(false, false);
        Assert.True(play.ForceStop);
        Assert.True(stub.Runs(0));

        play.Stop();

        RunUntil(PlayStatus.Idle);
        Assert.False(stub.Runs(0));
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.Equal("The game did not respond, so the editor ended it.", Console[^1]);
    }

    /// <summary>(10) The window close closes the input of the game, and the game ends.</summary>
    [Fact]
    public void TheWindowCloseClosesTheInputAndTheGameEnds()
    {
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        RunUntil(PlayStatus.Running);
        var clock = Stopwatch.StartNew();

        play.SessionEnding(windowClose: true);

        Assert.True(clock.Elapsed < PlayController.CloseLimit, $"The close took {clock.Elapsed}.");
        Assert.False(stub.Runs(0));
        Assert.Equal(["start", "<eof>"], stub.Input(0));
        Assert.Equal(PlayStatus.Idle, play.Status);
    }

    /// <summary>A game that ignores the end of its input gets 2 s at the window close. Then the job ends it.</summary>
    [Fact]
    public void TheWindowCloseEndsAGameThatIgnoresItsInputAfter2S()
    {
        plan = new StubPlan([], Sleep: 120);
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        stub.ProcessId(0);
        var clock = Stopwatch.StartNew();

        play.SessionEnding(windowClose: true);

        Assert.InRange(clock.Elapsed, PlayController.CloseLimit - TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(10));
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (stub.Runs(0) && DateTime.UtcNow < end)
        {
            Thread.Sleep(20);
        }

        Assert.False(stub.Runs(0));
        Assert.Equal(PlayStatus.Idle, play.Status);
    }

    /// <summary>A profile close or another end of the session sends stop. The exit follows.</summary>
    [Fact]
    public void AProfileCloseStopsTheGame()
    {
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        RunUntil(PlayStatus.Running);
        play.Request(Space, null);

        play.SessionEnding(windowClose: false);

        Assert.Equal(PlayStatus.Stopping, play.Status);
        RunUntil(PlayStatus.Idle);
        play.Update(false, false);
        Assert.Single(starts);
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>(11) An error event and exit code 3: status Idle, and the message in the Console.</summary>
    [Fact]
    public void AnErrorEventAndExitCode3GiveTheMessage()
    {
        plan = new StubPlan([Error("Unknown ship package 'no_such_package'.", crash: false)], Exit: PlayExitCodes.Data);
        editor.Commit(1);
        editor.Finish(1);

        RequestPlay();

        RunUntil(PlayStatus.Idle);
        Assert.Equal("Play stopped: Unknown ship package 'no_such_package'.", Console[^1]);
        Assert.Equal(ConsoleSeverity.Error, editor.Log.Entries[^1].Severity);
    }

    /// <summary>A crash without a validation that runs: the code, the cause and the path of the game log.</summary>
    [Fact]
    public void ACrashGivesTheCodeTheCauseAndTheGameLog()
    {
        plan = new StubPlan(["!err Process terminated. Assertion failed.", "!err    at A.B()"], Exit: 9);
        editor.Commit(1);
        editor.Finish(1);

        RequestPlay();

        RunUntil(PlayStatus.Idle);
        Assert.Equal("The game stopped with exit code 9: Process terminated. Assertion failed. at A.B(). The log is " +
                     $"{OpenLancer.Editor.ProgramPaths.PlayLog}.", Console[^1]);
    }

    /// <summary>
    /// (12) The game writes an error event with crash and exits while the validation of its save runs. Then the
    /// validation finds errors. The Console gives them as the cause (D55), not the crash. Here the exit comes first.
    /// </summary>
    [Fact]
    public void ACrashDuringTheValidationWaitsForItAndReportsItsErrors()
    {
        plan = new StubPlan([Error("The engine load stops.", crash: true)], Exit: 1);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();

        // The error event and the exit.
        ui.Run(2);
        play.Update(false, false);
        Assert.Equal(PlayStatus.Validating, play.Status);
        // The game ended, and its report waits for the validation of its save.
        Assert.Equal("Play: validating save 2...", play.StatusText);
        Assert.DoesNotContain(Console, m => m.StartsWith("The game stopped"));

        editor.Finish(2, HullError);

        Assert.Equal("Play stopped: the validation of save 2 found 1 error. The game closed during its load because " +
                     "of them.", Console[^1]);
        Assert.Equal(PlayStatus.Idle, play.Status);
        Assert.DoesNotContain(Console, m => m.StartsWith("The game stopped"));
    }

    /// <summary>
    /// (12) The same, but the validation result comes after the error event and before the editor handles the exit. The
    /// game ended, so the stop of the gate goes to a game that ended. That write must not throw (R6).
    /// </summary>
    [Fact]
    public void ACrashDuringTheValidationNeedsNoWriteToTheEndedGame()
    {
        plan = new StubPlan([Error("The engine load stops.", crash: true)], Exit: 1);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.WaitForQueued(2);
        Assert.False(stub.Runs(0));
        ui.Run(1);

        editor.Finish(2, HullError);

        Assert.Equal("Play stopped: the validation of save 2 found 1 error. The game closed during its load because " +
                     "of them.", Console[^1]);
        RunUntil(PlayStatus.Idle);
        Assert.DoesNotContain(Console, m => m.StartsWith("The game stopped"));
    }

    /// <summary>(12) Without errors, the Console reports the crash with the message of the error event.</summary>
    [Fact]
    public void ACrashDuringAValidationWithoutErrorsIsReportedAsACrash()
    {
        plan = new StubPlan([Error("Tried to mount combinable item", crash: true)], Exit: 1);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(2);

        editor.Finish(2, Diagnostic.Warning("Engine", "a warning"));

        Assert.Equal("The game stopped with exit code 1: Tried to mount combinable item. The log is " +
                     $"{OpenLancer.Editor.ProgramPaths.PlayLog}.", Console[^1]);
        Assert.Equal(PlayStatus.Idle, play.Status);
    }

    /// <summary>
    /// (13) A save commits before the game sends waiting: the game can hold files of that save, so the gate waits for
    /// the validation of the new save, not of the old one (the read window, D51).
    /// </summary>
    [Fact]
    public void ASaveBeforeWaitingMakesTheGateWaitForTheNewSave()
    {
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        Assert.Equal(1, Assert.Single(starts).Save);
        ui.WaitForQueued(1);

        editor.Commit(2);
        ui.Run(1);

        Assert.Equal(PlayStatus.Validating, play.Status);
        Assert.Contains("Save 2 came while the game read the mod files. Play waits for its validation.", Console);
        editor.Finish(2, HullError);
        Assert.Contains("Play stopped: the validation of save 2 found 1 error. The Console lists them.", Console);
        RunUntil(PlayStatus.Idle);
        Assert.Equal(["stop"], stub.Input(0));
    }

    /// <summary>
    /// A save after waiting stops the validation of the gate save (D58). The start does not change (D52): the game
    /// starts with a warning. The reload of the new save follows when its validation passes.
    /// </summary>
    [Fact]
    public void ASaveAfterWaitingDoesNotChangeTheStart()
    {
        plan = Game with { ReloadLines = [Reloaded(3)] };
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(1);
        Assert.Equal(PlayStatus.Validating, play.Status);

        editor.Commit(3);
        play.Update(false, false);

        Assert.Contains("Play goes on without a validation of save 2: a later save stopped it. The game shows the " +
                        "files of save 2.", Console);
        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 2", play.StatusText);
        editor.Finish(3);
        Assert.Equal("Play: reloading save 3...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 3", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 3", "stop"], stub.Input(0));
    }

    /// <summary>
    /// A save stopped the validation of the gate save and wrote nothing, for example after an exception: Play validates
    /// the files again. A validation that cannot start gives its result at once, during the call. Play then starts
    /// once, with one warning.
    /// </summary>
    [Fact]
    public void AValidationThatCannotStartGivesOneWarningAndOneStart()
    {
        editor.Commit(1);
        RequestPlay();
        ui.WaitForQueued(1);
        editor.StopValidation();
        editor.ValidationCannotStart = true;

        ui.Run(1);

        Assert.Equal(1, editor.ValidateNowCalls);
        Assert.Single(Console, m => m == "Play goes on without a validation: the validation of save 1 could not run.");
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>(14) No waiting for 60 s on the fake clock: the game does not respond, and Stop kills it at once.</summary>
    [Fact]
    public void WithoutWaitingFor60STheGameDoesNotRespondAndStopKillsItAtOnce()
    {
        plan = new StubPlan([]);
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        stub.ProcessId(0);

        now += TimeSpan.FromSeconds(59.9);
        play.Update(false, false);
        Assert.Equal(PlayStatus.Starting, play.Status);
        Assert.False(play.ForceStop);
        now += TimeSpan.FromSeconds(0.1);
        play.Update(false, false);
        Assert.Equal(PlayStatus.NotResponding, play.Status);
        Assert.Equal("Play: the game does not respond", play.StatusText);
        Assert.True(play.ForceStop);
        Assert.Contains("Play: the game does not respond. It did not load its data in 60 s. Stop ends it.", Console);

        play.Stop();

        RunUntil(PlayStatus.Idle);
        Assert.False(stub.Runs(0));
        Assert.Empty(stub.Input(0));
    }

    /// <summary>A game that does not exit 5 s after its error event, for example a crash window that waits: the editor
    /// ends it.</summary>
    [Fact]
    public void AGameThatDoesNotExit5SAfterItsErrorIsEnded()
    {
        plan = new StubPlan([Error("Crashed.", crash: true)], IgnoreStop: true);
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        ui.Run(1);

        now += TimeSpan.FromSeconds(4.9);
        play.Update(false, false);
        Assert.True(stub.Runs(0));
        now += TimeSpan.FromSeconds(0.1);
        play.Update(false, false);

        RunUntil(PlayStatus.Idle);
        Assert.False(stub.Runs(0));
        Assert.Equal("The game did not exit 5 s after its error, so the editor ended it.", Console[^2]);
        // The kill sets the exit code, so the line gives none. The cause is the message of the error event.
        Assert.Equal($"The game stopped: Crashed. The log is {OpenLancer.Editor.ProgramPaths.PlayLog}.", Console[^1]);
    }

    /// <summary>The game can start the play before it reads stop. A spawn after stop then gives no Playing line (section
    /// 9.8).</summary>
    [Fact]
    public void ASpawnAfterStopIsIgnored()
    {
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(1);
        editor.Finish(2);

        play.Stop();

        RunUntil(PlayStatus.Idle);
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.DoesNotContain(Console, m => m.StartsWith("Playing"));
    }

    /// <summary>The warnings and errors of the engine log of the game go to the Console, by decision D57.</summary>
    [Fact]
    public void TheEngineLinesOfTheGameGoToTheConsoleOnce()
    {
        plan = Game with { Lines = ["[Warning: 12:00:00] Ini: known", "[Error: 12:00:00] Ship: new", Waiting] };
        editor.Log.AddEngineLine("[Warning: 11:00:00] Ini: known", LibreLancer.LogSeverity.Warning);
        editor.Commit(1);
        editor.Finish(1);

        RequestPlay();

        RunUntil(PlayStatus.Running);
        Assert.Single(editor.Log.Entries, e => e.Source == PlayController.Source && e.Message == "Ship: new");
        Assert.DoesNotContain(editor.Log.Entries, e => e.Source == PlayController.Source && e.Message == "Ini: known");
        StopAndRunToIdle();
    }

    [Fact]
    public void AtABaseTheConsoleNamesTheBaseAndTheLaterModsShowAsAWarning()
    {
        plan = Game with
        {
            StartLines = [PlayProtocol.Write(new PlayEvent.Spawned("Br01", "Br01_01_Base", null, "le", "", 300))],
        };
        editor.Session = editor.Session! with { LaterMods = ["late1", "late2"] };
        editor.Commit(1);
        editor.Finish(1);

        RequestPlay(new PlayStart.AtBase("Br01_01_Base"));

        RunUntil(PlayStatus.Running);
        Assert.Contains(editor.Log.Entries, e => e.Severity == ConsoleSeverity.Warning &&
                                                 e.Message == "Play does not include the later mods: late1, late2.");
        Assert.Contains("Playing at Planet New London (Br01_01_Base) in New London (Br01) with le. From Play to " +
                        "flight: 0.0 s.", Console);
        Assert.Equal("Playing: Planet New London (Br01_01_Base), save 1", play.StatusText);
        StopAndRunToIdle();
    }

    /// <summary>
    /// After waiting, a save stopped the validation of the gate save and wrote nothing, for example after an exception.
    /// No later save exists, so Play validates the gate save again and does not wait for the stopped validation.
    /// </summary>
    [Fact]
    public void AValidationThatASaveStoppedWithoutALaterSaveRunsAgain()
    {
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(1);
        Assert.Equal(PlayStatus.Validating, play.Status);

        editor.StopValidation();
        play.Update(false, false);

        Assert.Equal(1, editor.ValidateNowCalls);
        Assert.Equal(PlayStatus.Validating, play.Status);
        editor.Finish(2);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>
    /// A save that is not complete, while the game reads the mod files: the game can hold parts of it. Such a save gets
    /// no validation, and Play stops the game (D59).
    /// </summary>
    [Fact]
    public void ASaveThatIsNotCompleteInTheReadWindowStopsTheGame()
    {
        const string stopped = "Play stopped: save 2 is not complete, and the game can hold parts of it. Correct the " +
                               "problem of the file, save again, then press Play.";
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();

        editor.SaveIncomplete(2);
        play.Update(false, false);

        Assert.Equal(0, editor.ValidateNowCalls);
        Assert.Equal(ConsoleSeverity.Error, editor.Log.Entries[^1].Severity);
        Assert.Equal(stopped, Console[^1]);
        RunUntil(PlayStatus.Idle);
        Assert.Equal(["stop"], stub.Input(0));
        Assert.Equal(stopped, Console[^1]);
    }

    /// <summary>
    /// A save that is not complete, after waiting: the game holds the gate save. The save stopped the validation of the
    /// gate save, so Play goes on with the warning of D58 and does not validate the save that is not complete.
    /// </summary>
    [Fact]
    public void ASaveThatIsNotCompleteAfterWaitingGetsNoValidation()
    {
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(1);

        editor.SaveIncomplete(3);
        play.Update(false, false);

        Assert.Equal(0, editor.ValidateNowCalls);
        Assert.Contains("Play goes on without a validation of save 2: a later save stopped it. The game shows the " +
                        "files of save 2.", Console);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>An error event without a crash and exit code 3 give the message at once, also while the validation of
    /// the save runs. Its result cannot be the cause of a data error such as an unknown package.</summary>
    [Fact]
    public void ADataErrorDuringTheValidationGivesItsMessageAtOnce()
    {
        plan = new StubPlan([Error("Unknown ship package 'no_such_package'.", crash: false)], Exit: PlayExitCodes.Data);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();

        // The error event and the exit.
        ui.Run(2);

        Assert.True(editor.Validation!.Running);
        Assert.Equal(PlayStatus.Idle, play.Status);
        Assert.Equal("Play stopped: Unknown ship package 'no_such_package'.", Console[^1]);
    }

    /// <summary>A waiting event after the 60 s watchdog: the game responds again, so Stop sends stop and does not kill
    /// it.</summary>
    [Fact]
    public void AWaitingEventAfterTheLoadLimitEndsNotResponding()
    {
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        ui.WaitForQueued(1);
        now += PlayController.LoadLimit;
        play.Update(false, false);
        Assert.Equal(PlayStatus.NotResponding, play.Status);

        ui.Run(1);

        Assert.False(play.ForceStop);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.Equal("The game closed.", Console[^1]);
    }

    /// <summary>A crash without an error event and without error output: the cause is the last error line of the
    /// engine log (section 9.10).</summary>
    [Fact]
    public void ACrashWithoutAnEventOrErrorOutputGivesTheLastEngineError()
    {
        plan = new StubPlan(["[Error: 12:00:00] Ship: first", "[Error: 12:00:01] Ship: last",
            "[Warning: 12:00:02] Ini: later"], Exit: 9);
        editor.Commit(1);
        editor.Finish(1);

        RequestPlay();

        RunUntil(PlayStatus.Idle);
        Assert.Equal("The game stopped with exit code 9: Ship: last. The log is " +
                     $"{OpenLancer.Editor.ProgramPaths.PlayLog}.", Console[^1]);
    }

    /// <summary>
    /// A crash in the read window: a save came after the start, so the gate save is the new save. The report waits for
    /// the validation of the new save, and its errors are the cause (D51, D55).
    /// </summary>
    [Fact]
    public void ACrashInTheReadWindowWaitsForTheValidationOfTheNewSave()
    {
        plan = new StubPlan([Error("The engine load stops.", crash: true)], Exit: 1);
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        Assert.Equal(1, Assert.Single(starts).Save);
        // The error event and the exit.
        ui.WaitForQueued(2);

        editor.Commit(2);
        ui.Run(2);
        play.Update(false, false);

        Assert.Equal(PlayStatus.Validating, play.Status);
        Assert.DoesNotContain(Console, m => m.StartsWith("The game stopped"));
        editor.Finish(2, HullError);
        Assert.Equal("Play stopped: the validation of save 2 found 1 error. The game closed during its load because " +
                     "of them.", Console[^1]);
        Assert.Equal(PlayStatus.Idle, play.Status);
    }

    /// <summary>Stop while a crash report waits for the validation: the Console reports the crash now, and the status
    /// is Idle.</summary>
    [Fact]
    public void StopReportsACrashThatWaitsForTheValidation()
    {
        plan = new StubPlan([Error("The engine load stops.", crash: true)], Exit: 1);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(2);
        Assert.Equal(PlayStatus.Validating, play.Status);

        play.Stop();

        Assert.Equal(PlayStatus.Idle, play.Status);
        Assert.Equal("The game stopped with exit code 1: The engine load stops. The log is " +
                     $"{OpenLancer.Editor.ProgramPaths.PlayLog}.", Console[^1]);
    }

    /// <summary>
    /// The error watchdog ends a game that crashed while the validation of its save runs. The crash rule (D55) then
    /// applies as for an exit: the report waits for the validation, and its errors are the cause.
    /// </summary>
    [Fact]
    public void AGameThatTheErrorWatchdogEndedDuringTheValidationWaitsForIt()
    {
        plan = new StubPlan([Error("The engine load stops.", crash: true)], IgnoreStop: true);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        RequestPlay();
        ui.Run(1);

        now += PlayController.ExitLimit;
        play.Update(false, false);
        // The exit after the kill.
        ui.Run(1);

        Assert.False(stub.Runs(0));
        Assert.Equal(PlayStatus.Validating, play.Status);
        Assert.Equal("The game did not exit 5 s after its error, so the editor ended it.", Console[^1]);
        editor.Finish(2, HullError);
        Assert.Equal("Play stopped: the validation of save 2 found 1 error. The game closed during its load because " +
                     "of them.", Console[^1]);
        Assert.Equal(PlayStatus.Idle, play.Status);
    }

    /// <summary>
    /// (15) After the spawn, a save whose validation passes reloads the game (D17). The Console gives the time in the
    /// game and the time from the save. Reload game is not available during the reload. Here the player docked after
    /// the spawn, so the reload continues at the base, and the status names the base.
    /// </summary>
    [Fact]
    public void APassedValidationAfterTheSpawnReloadsTheGame()
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2, dock: "Br01_01_Base")] });
        Assert.True(play.CanReload);

        editor.Commit(2);
        play.Update(false, false);
        Assert.Equal(PlayStatus.Running, play.Status);
        now += TimeSpan.FromSeconds(3);
        editor.Finish(2);

        Assert.Equal(PlayStatus.Reloading, play.Status);
        Assert.Equal("Play: reloading save 2...", play.StatusText);
        Assert.False(play.CanReload);
        RunUntil(PlayStatus.Running);
        Assert.Equal(["start", "reload 2"], stub.Input(0));
        Assert.Equal("The running game shows save 2 (reload 0.6 s, 3.0 s from the save).", Console[^1]);
        Assert.Equal("Playing: Planet New London (Br01_01_Base), save 2", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
    }

    /// <summary>A hot reload loads all game data again, so the Console counts the engine lines of the run again. A line
    /// that the base game gives one time stays a base-game line after the reload (decision D514 of the console filter
    /// design).</summary>
    [Fact]
    public void AReloadStartsTheCountsOfTheEngineLinesAgain()
    {
        const string line = @"[Warning: 12:00:00] Ini: Unknown entry typo at section Object: data\other.ini, line 5";
        using var mods = ListWorkspace.Create(activeFiles: new()
        {
            [@"patches\" + ListWorkspace.Li01Path] = "[Object]\nnickname = Li01_01\nloadout = lo_shared\n",
        });
        editor.Log.Origins = new EngineLineOrigins(mods.Session("act"));
        editor.Log.BaseRunEnded(new Dictionary<string, int> { [EngineLine.Parse(line)!.Key] = 1 });
        PlayToRunning(Game with { Lines = [line, Waiting], ReloadLines = [line + StubPlan.NextLine + Reloaded(2)] });

        editor.Commit(2);
        play.Update(false, false);
        editor.Finish(2);
        RunUntil(PlayStatus.Running);

        Assert.Equal(["start", "reload 2"], stub.Input(0));
        var entry = Assert.Single(editor.Log.Entries, e => e.Source == PlayController.Source && e.Message.StartsWith("Ini: "));
        Assert.Equal(MessageOrigin.BaseGame, entry.Origin);
        StopAndRunToIdle();
    }

    /// <summary>(15) Validation errors after the spawn: no reload, and the Console tells once which save the game
    /// keeps.</summary>
    [Fact]
    public void ValidationErrorsAfterTheSpawnKeepTheSaveOfTheGame()
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2)] });

        editor.Commit(2);
        editor.Finish(2, HullError);
        play.Update(false, false);
        play.Update(false, false);

        Assert.Equal(PlayStatus.Running, play.Status);
        Assert.Single(Console, m => m == "The running game keeps save 1. The validation of save 2 found 1 error.");
        Assert.Equal("Playing: New London (Br01), save 1", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>
    /// (15) One reload at a time: the saves during a reload wait for its end. Then only the last save reloads. The
    /// game read the mod files while those saves came, so it can show parts of the last one (D51).
    /// </summary>
    [Fact]
    public void SavesDuringAReloadWaitAndOnlyTheLastSaveReloads()
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2), Reloaded(4)] });
        editor.Commit(2);
        editor.Finish(2);
        // The reloaded event of the stub waits in the queue of the UI thread.
        ui.WaitForQueued(1);

        editor.Commit(3);
        editor.Finish(3);
        editor.Commit(4);
        editor.Finish(4);
        play.Update(false, false);

        Assert.Equal("Play: reloading save 2...", play.StatusText);
        ui.Run(1);
        Assert.Contains("The running game shows save 2 (reload 0.6 s, 0.0 s from the save).", Console);
        Assert.Contains("Save 4 came during the reload, so the game can show parts of it.", Console);
        Assert.Equal("Play: reloading save 4...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 4", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "reload 4", "stop"], stub.Input(0));
    }

    /// <summary>(16) A validation that passes before the spawn sends no reload. The reload follows at the spawn
    /// (D52).</summary>
    [Fact]
    public void APassedValidationBeforeTheSpawnReloadsAtTheSpawn()
    {
        plan = Game with { ReloadLines = [Reloaded(2)] };
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        // The waiting event: the game gets start, and its spawned event waits in the queue.
        ui.Run(1);
        ui.WaitForQueued(1);

        editor.Commit(2);
        editor.Finish(2);
        play.Update(false, false);

        Assert.Equal(PlayStatus.Starting, play.Status);
        Assert.False(play.CanReload);
        play.Reload();
        Assert.Equal(PlayStatus.Starting, play.Status);
        ui.Run(1);
        Assert.Equal("Play: reloading save 2...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.DoesNotContain(Console, m => m.Contains("Press F5", StringComparison.Ordinal));
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
    }

    /// <summary>
    /// (17) A save before waiting: the game read a mix of two saves, so after the spawn the game save is unknown. The
    /// validation of the new save passed at the gate, so the reload of that save follows the spawn at once (D51).
    /// </summary>
    [Fact]
    public void ASaveBeforeWaitingReloadsTheLastSaveAfterTheSpawn()
    {
        plan = Game with { ReloadLines = [Reloaded(2)] };
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        ui.WaitForQueued(1);
        editor.Commit(2);
        ui.Run(1);
        editor.Finish(2);

        ui.RunUntil(() => play.Status != PlayStatus.Starting, "the spawn");

        Assert.Equal("Play: reloading save 2...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 2", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
    }

    /// <summary>
    /// (17) A save during a reload: after reloaded, the game can show parts of that save. A passed validation of the
    /// save then reloads it. Errors give the line that the game can show parts of it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASaveDuringAReloadMakesTheGameSaveUnknown(bool passes)
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2), Reloaded(3)] });
        editor.Commit(2);
        editor.Finish(2);
        ui.WaitForQueued(1);
        editor.Commit(3);

        ui.Run(1);

        Assert.Equal(PlayStatus.Running, play.Status);
        Assert.Equal("Playing: New London (Br01), parts of save 3", play.StatusText);
        Assert.Equal("Save 3 came during the reload, so the game can show parts of it.", Console[^1]);
        if (passes)
        {
            editor.Finish(3);
            Assert.Equal("Play: reloading save 3...", play.StatusText);
            RunUntil(PlayStatus.Running);
            Assert.Equal("Playing: New London (Br01), save 3", play.StatusText);
        }
        else
        {
            editor.Finish(3, HullError);
            Assert.Equal(PlayStatus.Running, play.Status);
            Assert.Equal("The running game can show parts of save 3. The validation of save 3 found 1 error.",
                Console[^1]);
        }

        StopAndRunToIdle();
        string[] input = passes ? ["start", "reload 2", "reload 3", "stop"] : ["start", "reload 2", "stop"];
        Assert.Equal(input, stub.Input(0));
    }

    /// <summary>
    /// A reload that fails keeps the save of the game, and the reload trigger does not send it again. Reload game sends
    /// it again, and a later save reloads.
    /// </summary>
    [Fact]
    public void AFailedReloadIsNotSentAgainButReloadGameSendsIt()
    {
        const string failure = "The new data cannot load the autosave of the player. The ship 'm4proof_fighter' is " +
                               "not in the game data.";
        PlayToRunning(Game with
        {
            ReloadLines = [PlayProtocol.Write(new PlayEvent.ReloadFailed(2, failure)), Reloaded(2, 700), Reloaded(3)],
        });
        editor.Commit(2);
        editor.Finish(2);

        RunUntil(PlayStatus.Running);
        Assert.Equal("The reload of save 2 failed, so the running game keeps save 1: The new data cannot load the " +
                     "autosave of the player. The ship 'm4proof_fighter' is not in the game data.", Console[^1]);
        Assert.Equal(ConsoleSeverity.Error, editor.Log.Entries[^1].Severity);
        Assert.Equal("Playing: New London (Br01), save 1", play.StatusText);
        play.Update(false, false);
        Assert.Equal(PlayStatus.Running, play.Status);

        play.Reload();

        Assert.Equal("Play: reloading save 2...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.Equal("The running game shows save 2 (reload 0.7 s).", Console[^1]);
        editor.Commit(3);
        editor.Finish(3);
        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 3", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "reload 2", "reload 3", "stop"], stub.Input(0));
    }

    /// <summary>Reload game sends the last save also when its validation found errors. It is for a change outside the
    /// editor (D45).</summary>
    [Fact]
    public void ReloadGameSendsTheLastSaveAlsoWithValidationErrors()
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2)] });
        editor.Commit(2);
        editor.Finish(2, HullError);

        play.Reload();

        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 2", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
    }

    /// <summary>A validation that could not run lets the reload go on with a warning, as the start does (D43).</summary>
    [Fact]
    public void AReloadAfterAValidationThatCouldNotRunGoesOnWithAWarning()
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2)] });
        editor.Commit(2);

        editor.FailToRun(2);

        Assert.Contains(editor.Log.Entries, e => e.Severity == ConsoleSeverity.Warning &&
                                                 e.Message == "The running game loads save 2 without a validation: " +
                                                 "the validation could not run.");
        Assert.Equal("Play: reloading save 2...", play.StatusText);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
    }

    /// <summary>
    /// A save stopped the validation of the last save and wrote nothing: the reload trigger validates the files again,
    /// as the gate does. A save that is not complete gets no validation and no reload (D59). The save that completes it
    /// starts the validation.
    /// </summary>
    [Fact]
    public void AStoppedValidationRunsAgainAndASaveThatIsNotCompleteWaits()
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2), Reloaded(3)] });
        editor.Commit(2);
        editor.StopValidation();

        play.Update(false, false);

        Assert.Equal(1, editor.ValidateNowCalls);
        editor.Finish(2);
        RunUntil(PlayStatus.Running);
        editor.SaveIncomplete(3);
        play.Update(false, false);
        Assert.Equal(1, editor.ValidateNowCalls);
        Assert.Equal(PlayStatus.Running, play.Status);
        // The next save completes save 3.
        editor.Commit(3);
        editor.Finish(3);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "reload 3", "stop"], stub.Input(0));
    }

    /// <summary>No reloaded or reload-failed for 30 s on the fake clock: the game does not respond, and Stop kills it at
    /// once (section 9.10). The limit counts from the reload command, not from the process start, and the Console warns
    /// once.</summary>
    [Fact]
    public void WithoutTheEndOfAReloadFor30STheGameDoesNotRespond()
    {
        const string warning = "Play: the game does not respond. It did not end the reload of save 2 in 30 s. Stop " +
                               "ends it.";
        PlayToRunning(Game);
        // The player flew for 5 minutes before the save.
        now += TimeSpan.FromMinutes(5);
        play.Update(false, false);
        editor.Commit(2);
        editor.Finish(2);
        stub.WaitForInput(0, 2);

        now += TimeSpan.FromSeconds(29.9);
        play.Update(false, false);
        Assert.Equal(PlayStatus.Reloading, play.Status);
        Assert.False(play.ForceStop);
        now += TimeSpan.FromSeconds(0.1);
        play.Update(false, false);
        play.Update(false, false);

        Assert.Equal(PlayStatus.NotResponding, play.Status);
        Assert.Equal("Play: the game does not respond", play.StatusText);
        Assert.True(play.ForceStop);
        Assert.Equal(warning, Console[^1]);
        Assert.Single(Console, m => m == warning);
        play.Stop();
        RunUntil(PlayStatus.Idle);
        Assert.False(stub.Runs(0));
    }

    /// <summary>A reloaded or reload-failed event after the 30 s watchdog: the game responds again. So the status is
    /// Running, and Stop sends stop and does not kill the game (section 9.10).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALateEndOfAReloadEndsNotResponding(bool failed)
    {
        PlayToRunning(Game with { ReloadLines = [failed ? ReloadFailed(2) : Reloaded(2)] });
        editor.Commit(2);
        editor.Finish(2);
        // The event of the stub waits in the queue of the UI thread.
        ui.WaitForQueued(1);
        now += PlayController.ReloadLimit;
        play.Update(false, false);
        Assert.Equal(PlayStatus.NotResponding, play.Status);

        ui.Run(1);

        Assert.Equal(PlayStatus.Running, play.Status);
        Assert.False(play.ForceStop);
        Assert.Equal($"Playing: New London (Br01), save {(failed ? 1 : 2)}", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
        Assert.Equal("The game closed.", Console[^1]);
    }

    /// <summary>
    /// A save during a reload that fails: the failure keeps the save of the game, and it marks only the save of that
    /// reload. So the newer save, whose validation passed, reloads at once (D74).
    /// </summary>
    [Fact]
    public void ASaveDuringAFailedReloadReloadsAfterIt()
    {
        PlayToRunning(Game with { ReloadLines = [ReloadFailed(2), Reloaded(3)] });
        editor.Commit(2);
        editor.Finish(2);
        ui.WaitForQueued(1);
        editor.Commit(3);
        editor.Finish(3);
        play.Update(false, false);
        Assert.Equal("Play: reloading save 2...", play.StatusText);

        ui.Run(1);

        Assert.Equal("The reload of save 2 failed, so the running game keeps save 1: The new data cannot load the " +
                     "autosave of the player.", Console[^1]);
        Assert.Equal("Play: reloading save 3...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 3", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "reload 3", "stop"], stub.Input(0));
    }

    /// <summary>
    /// (17) A save in the read window of the start, then a save with validation errors before the spawn. The game can
    /// show parts of the gate save, so the status and the errors line name the gate save, not the save of the start.
    /// </summary>
    [Fact]
    public void ErrorsAfterASaveInTheReadWindowOfTheStartNameTheGateSave()
    {
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        // The waiting event waits in the queue, and save 2 comes while the game reads the mod files.
        ui.WaitForQueued(1);
        editor.Commit(2);
        ui.Run(1);
        editor.Finish(2);
        // The spawned event waits in the queue, and save 3 has an error.
        ui.WaitForQueued(1);
        editor.Commit(3);
        editor.Finish(3, HullError);

        ui.Run(1);

        Assert.Equal(PlayStatus.Running, play.Status);
        Assert.Equal("Playing: New London (Br01), parts of save 2", play.StatusText);
        Assert.Equal("The running game can show parts of save 2. The validation of save 3 found 1 error.", Console[^1]);
        StopAndRunToIdle();
        Assert.Equal(["start", "stop"], stub.Input(0));
    }

    /// <summary>An error event after the spawn: the game closes. So a passed validation sends no reload, and Reload game
    /// is not available.</summary>
    [Fact]
    public void AnErrorEventAfterTheSpawnStopsTheReloads()
    {
        plan = new StubPlan([Waiting], [Spawned, Error("Crashed.", crash: true)], IgnoreStop: true,
            ReloadLines: [Reloaded(2)]);
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        RunUntil(PlayStatus.Running);
        // The error event.
        ui.Run(1);

        Assert.False(play.CanReload);
        Assert.Equal("The game sent an error event, and it closes.", play.ReloadBlock);
        editor.Commit(2);
        editor.Finish(2);
        play.Update(false, false);
        play.Reload();

        Assert.Equal(PlayStatus.Running, play.Status);
        Assert.Equal("Playing: New London (Br01), save 1", play.StatusText);
        now += PlayController.ExitLimit;
        play.Update(false, false);
        RunUntil(PlayStatus.Idle);
        Assert.Equal(["start"], stub.Input(0));
    }

    /// <summary>A reloaded or reload-failed event after stop: the game closes, so the Console gives no line for it, as
    /// for a spawn after stop (section 9.8).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEndOfAReloadAfterStopIsIgnored(bool failed)
    {
        PlayToRunning(Game with { ReloadLines = [failed ? ReloadFailed(2) : Reloaded(2)] });
        editor.Commit(2);
        editor.Finish(2);
        ui.WaitForQueued(1);
        play.Stop();

        ui.Run(1);

        Assert.Equal(PlayStatus.Stopping, play.Status);
        RunUntil(PlayStatus.Idle);
        Assert.DoesNotContain(Console, m => m.StartsWith("The running game", StringComparison.Ordinal) ||
                                            m.StartsWith("The reload of save 2", StringComparison.Ordinal));
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
        Assert.Equal("The game closed.", Console[^1]);
    }

    /// <summary>A save whose validation passes after stop sends no reload, because the game closes (section 9.8).</summary>
    [Fact]
    public void APassedValidationAfterStopSendsNoReload()
    {
        PlayToRunning(Game with { IgnoreStop = true, ReloadLines = [Reloaded(2)] });
        play.Stop();
        stub.WaitForInput(0, 2);

        editor.Commit(2);
        editor.Finish(2);
        play.Update(false, false);

        // The stub reads its input after stop. So a reload command would reach it, and the stub would answer it.
        ui.RunUntilQuiet(TimeSpan.FromMilliseconds(500));
        Assert.Equal(["start", "stop"], stub.Input(0));
        now += PlayController.ExitLimit;
        play.Stop();
        RunUntil(PlayStatus.Idle);
    }

    /// <summary>
    /// Reload game is not available while the last save is not complete, because the play load refuses its files (D59).
    /// The save that completes it keeps its number, and the reload trigger then sends it (review of step 8).
    /// </summary>
    [Fact]
    public void ReloadGameWaitsForTheSaveThatCompletesASaveThatIsNotComplete()
    {
        PlayToRunning(Game with { ReloadLines = [Reloaded(2)] });
        editor.SaveIncomplete(2);
        play.Update(false, false);

        Assert.False(play.CanReload);
        Assert.Equal("Save 2 is not complete, so the game cannot load the mod files. Correct the problem of the file, " +
                     "then save again.", play.ReloadBlock);
        play.Reload();
        Assert.Equal(PlayStatus.Running, play.Status);

        // Ctrl+S without new edits completes save 2. The number stays, and the validation of save 2 starts.
        editor.Commit(2);
        editor.Finish(2);

        Assert.Equal("Play: reloading save 2...", play.StatusText);
        RunUntil(PlayStatus.Running);
        Assert.Equal("Playing: New London (Br01), save 2", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(["start", "reload 2", "stop"], stub.Input(0));
    }

    // ---- The mission play (sections 5.6, 5.8 and 6.2 of the M6c design) ----

    private const string StartNote = "OpenLancer plays M99 from Checkpoint t_restore.";

    /// <summary>A mission play saves the active mod first, then makes its start from the model of the saved script, and
    /// the game starts from the start save. The Console gives the start note and the note of an approximate start.</summary>
    [Fact]
    public void AMissionPlaySavesFirstThenPreparesItsStartAndStartsFromTheStartSave()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.SaveResult = new SaveOutcome(true, 2, false);
        var mission = new FakeMissionStart { StartName = "t_wave (approximate)", Approximate = true };
        var savesAtPrepare = -1;
        mission.OnPrepare = () => savesAtPrepare = editor.SaveCalls;

        var result = play.RequestMission(mission, null);
        play.Update(false, false);

        Assert.Equal(1, savesAtPrepare);
        Assert.Equal(mission.Start, Assert.Single(starts).Start);
        Assert.Same(mission, play.Mission);
        Assert.Equal(["OpenLancer plays M99 from t_wave (approximate).", MissionPlayTexts.Approximate],
            Console.SkipWhile(l => !l.StartsWith("OpenLancer plays")).Take(2));
        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(result.Result.Started);
        editor.Finish(2);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
        Assert.Null(play.Mission);
    }

    /// <summary>A start that cannot start starts no game. The request gives the reason of the start.</summary>
    [Fact]
    public void AStartThatCannotStartStartsNoGameAndGivesItsReason()
    {
        var mission = new FakeMissionStart { RefuseWith = "This mission script has no story slot." };

        var result = play.RequestMission(mission, null);
        play.Update(false, false);

        Assert.Empty(starts);
        Assert.Equal(PlayStatus.Idle, play.Status);
        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(EditErrorKind.Refused, result.Result.Error!.Kind);
        Assert.Equal("This mission script has no story slot.", result.Result.Error.Message);
        Assert.Equal("Play from here is not available: This mission script has no story slot.", Console[^1]);
    }

    /// <summary>The save of Play gives its error to the request: the save gate and files that changed outside the
    /// editor (section 7.4 of the M6c design).</summary>
    [Theory]
    [InlineData((int)SaveFailure.Blocked, EditErrorKind.SaveBlocked)]
    [InlineData((int)SaveFailure.ChangedOnDisk, EditErrorKind.ChangedOnDisk)]
    [InlineData((int)SaveFailure.None, EditErrorKind.Refused)]
    public void AFailedSaveGivesItsErrorToTheRequest(int failure, EditErrorKind kind)
    {
        editor.SaveResult = SaveOutcome.NotSaved with { Failure = (SaveFailure)failure };
        var mission = new FakeMissionStart();

        var result = play.RequestMission(mission, null);
        play.Update(false, false);

        Assert.Empty(starts);
        Assert.Equal(0, mission.Prepares);
        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(kind, result.Result.Error!.Kind);
    }

    /// <summary>A save during a mission play sends restart, not reload (section 5.8 of the M6c design). The start of the
    /// running play makes its plan again, and the Console gives the restart note.</summary>
    [Fact]
    public void ASaveDuringAMissionPlaySendsRestartNotReload()
    {
        var mission = new FakeMissionStart();
        PlayMissionToRunning(Game with { RestartLines = [Restarted(2)] }, mission);
        Assert.Contains(StartNote, Console);

        editor.Commit(2);
        play.Update(false, false);
        now += TimeSpan.FromSeconds(3);
        editor.Finish(2);

        Assert.Equal(PlayStatus.Reloading, play.Status);
        Assert.Equal("Play: starting the mission again...", play.StatusText);
        Assert.Equal(2, mission.Prepares);
        Assert.Contains("Save 2 changed the files of the mission play. The game starts M99 again from Checkpoint t_restore.",
            Console);
        RunUntil(PlayStatus.Running);
        Assert.Equal(["start", "restart 2"], stub.Input(0));
        Assert.Equal("The game started M99 again from Checkpoint t_restore with save 2 (restart 0.6 s).", Console[^1]);
        Assert.Equal("Playing: New London (Br01), save 2", play.StatusText);
        StopAndRunToIdle();
        Assert.Equal(1, stub.Starts);
        // The live view starts a new play at the start and at the restart, and stops its clock at the exit.
        Assert.Equal(["started", "started", "ended"], editor.LiveCalls);
    }

    /// <summary>restart-failed keeps the game: the player goes on with the data of the game, and the Console tells it as
    /// a warning. The trigger of the restart does not send the save again.</summary>
    [Fact]
    public void ARestartFailedKeepsTheGameAndTellsTheConsole()
    {
        var mission = new FakeMissionStart();
        PlayMissionToRunning(Game with { RestartLines = [RestartFailed(2)] }, mission);

        editor.Commit(2);
        play.Update(false, false);
        editor.Finish(2);
        RunUntil(PlayStatus.Running);

        var warning = Assert.Single(editor.Log.Entries, e => e.Message.StartsWith("The game cannot start"));
        Assert.Equal(ConsoleSeverity.Warning, warning.Severity);
        Assert.Equal("The game cannot start M99 again. The start save does not load. The game goes on with its old data. " +
                     "In space, the mission goes back to its last checkpoint, or to the mission start when no checkpoint " +
                     "ran.", warning.Message);
        Assert.True(stub.Runs(0));
        play.Update(false, false);
        play.Update(false, false);
        Assert.Equal(["start", "restart 2"], stub.Input(0));
        StopAndRunToIdle();
    }

    /// <summary>A start that cannot start before a restart keeps the game and its mission. The Console tells why, and the
    /// save is not sent again.</summary>
    [Fact]
    public void AStartThatCannotStartBeforeARestartKeepsTheGame()
    {
        var mission = new FakeMissionStart();
        PlayMissionToRunning(Game, mission);
        mission.RefuseWith = "The path to trigger 't_wave' passes trigger 't_x', which has no condition.";

        editor.Commit(2);
        play.Update(false, false);
        editor.Finish(2);
        play.Update(false, false);

        Assert.Equal(PlayStatus.Running, play.Status);
        Assert.Same(mission, play.Mission);
        Assert.Equal(2, mission.Prepares);
        Assert.Equal(["start"], stub.Input(0));
        Assert.Equal("The game cannot start M99 again. The path to trigger 't_wave' passes trigger 't_x', which has no " +
                     "condition. The running game keeps save 1 and its mission.", Console[^1]);
        play.Update(false, false);
        Assert.Equal(2, mission.Prepares);
        StopAndRunToIdle();
    }

    /// <summary>F5 during a mission play writes a new start and sends restart. The process stays, the new start becomes
    /// the start of the play, and a later save restarts from it, not from the first start.</summary>
    [Fact]
    public void F5DuringAMissionPlayStartsTheGameAgainAtTheNewStart()
    {
        var first = new FakeMissionStart();
        PlayMissionToRunning(Game with { RestartLines = [Restarted(1), Restarted(2)] }, first);
        var second = new FakeMissionStart { StartName = "t_wave (approximate)", Approximate = true };

        var result = play.RequestMission(second, null);
        play.Update(false, false);

        Assert.Equal(2, editor.SaveCalls);
        // The game runs the first start until it answers restarted, and the request gets its result then.
        Assert.Same(first, play.Mission);
        Assert.Same(second, play.LoadingMission);
        Assert.Equal([first, second], play.MissionStarts);
        Assert.Contains("OpenLancer plays M99 from t_wave (approximate).", Console);
        RunUntil(PlayStatus.Running);
        Assert.Same(second, play.Mission);
        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(result.Result.Started);
        Assert.Equal(["start", "restart 1"], stub.Input(0));

        editor.Commit(2);
        play.Update(false, false);
        editor.Finish(2);
        RunUntil(PlayStatus.Running);

        Assert.Equal(1, first.Prepares);
        Assert.Equal(2, second.Prepares);
        Assert.Equal(["start", "restart 1", "restart 2"], stub.Input(0));
        Assert.Equal(1, stub.Starts);
        StopAndRunToIdle();
    }

    /// <summary>A play from the camera during a mission play closes the game first, as a second Play does (section 9.9
    /// of the M4 design).</summary>
    [Fact]
    public void APlayFromTheCameraDuringAMissionPlayClosesTheGameFirst()
    {
        PlayMissionToRunning(Game, new FakeMissionStart());

        RequestPlay();

        Assert.Contains("Play closes the running game first.", Console);
        ui.RunUntil(() =>
        {
            play.Update(false, false);
            return starts.Count == 2;
        }, "the start of the second game");
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.IsType<PlayStart.InSpace>(starts[1].Start);
        Assert.Null(play.Mission);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>The mission events of the game reach the editor (section 6.3 of the M6c design).</summary>
    [Fact]
    public void MissionEventsReachTheEditor()
    {
        var changes = new PlayEvent.MissionChanges("Mission_99", true, 0,
            [new MissionChange(120, "t_restore", MissionChangeKind.Ran), new MissionChange(120, "t_kill", MissionChangeKind.Started)]);
        PlayMissionToRunning(Game with { StartLines = [Spawned, PlayProtocol.Write(changes)] }, new FakeMissionStart());

        ui.RunUntil(() => editor.MissionEvents.Count == 1, "the mission event");

        Assert.Equal(changes.Changes, editor.MissionEvents[0].Changes);
        Assert.Equal("Mission_99", editor.MissionEvents[0].Mission);
        StopAndRunToIdle();
    }

    /// <summary>Stop drops a request that waits, and a newer request replaces it. Each gives its refusal.</summary>
    [Fact]
    public void StopAndANewerRequestEndARequestThatWaits()
    {
        var replaced = play.RequestMission(new FakeMissionStart(), null);
        var stopped = play.RequestMission(new FakeMissionStart(), null);

        play.Stop();
        play.Update(false, false);

        Assert.Empty(starts);
        Assert.True(Task.WaitAll([replaced, stopped], TimeSpan.FromSeconds(5)));
        Assert.Equal("Replaced", replaced.Result.Error!.Detail);
        Assert.Equal("Dropped", stopped.Result.Error!.Detail);
    }

    /// <summary>Shift+F5, the Stop button and the agent tool play_stop all call one stop request: it stops the game of
    /// Play in OpenLancer while one runs, not the game of the play copy (section 7.2 of the M6c design).</summary>
    [Fact]
    public void TheStopRequestStopsTheOpenLancerGameFirst()
    {
        var games = new PlayRequestsTests.FakeGames { GameRuns = true };
        PlayToRunning(Game);

        Assert.Equal(PlayStopped.OpenLancer, PlayRequests.Stop(play, games));

        Assert.Equal(PlayStatus.Stopping, play.Status);
        Assert.Equal(0, games.Stops);
        RunUntil(PlayStatus.Idle);
        Assert.Equal(PlayStopped.Game, PlayRequests.Stop(play, games));
        Assert.Equal(1, games.Stops);
    }

    /// <summary>restart-failed of a new start keeps the start that the game runs: the banner, a later restart and the
    /// origins of the engine lines name it. The request of the new start gets refused with the reason of the game
    /// (review fix of M6c-2).</summary>
    [Fact]
    public void ARestartFailedOfANewStartKeepsTheStartOfTheGame()
    {
        var first = new FakeMissionStart();
        PlayMissionToRunning(Game with { RestartLines = [RestartFailed(1), Restarted(2)] }, first);
        var second = new FakeMissionStart { StartName = "t_wave (approximate)" };

        var result = play.RequestMission(second, null);
        play.Update(false, false);
        RunUntil(PlayStatus.Running);

        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("RestartFailed", result.Result.Error!.Detail);
        Assert.StartsWith("The game cannot start M99 again. The start save does not load.", result.Result.Error.Message);
        Assert.Same(first, play.Mission);
        Assert.Equal([first], play.MissionStarts);
        editor.Commit(2);
        play.Update(false, false);
        editor.Finish(2);
        RunUntil(PlayStatus.Running);
        Assert.Equal(2, first.Prepares);
        Assert.Equal(1, second.Prepares);
        StopAndRunToIdle();
    }

    /// <summary>A new start that cannot start after the save of its request: the request gets its refusal, and the save
    /// goes to the game with the start of the running play, so the game does not keep the old files (review fix of
    /// M6c-2).</summary>
    [Fact]
    public void ARefusedNewStartStillRestartsTheRunningStartWithItsSave()
    {
        var running = new FakeMissionStart();
        PlayMissionToRunning(Game with { RestartLines = [Restarted(2)] }, running);
        editor.SaveResult = new SaveOutcome(true, 2, false);

        var result = play.RequestMission(new FakeMissionStart { RefuseWith = "The path is refused." }, null);
        play.Update(false, false);
        editor.Finish(2);

        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("StartRefused", result.Result.Error!.Detail);
        Assert.Contains("Play from here is not available: The path is refused.", Console);
        RunUntil(PlayStatus.Running);
        Assert.Equal(["start", "restart 2"], stub.Input(0));
        Assert.Equal(2, running.Prepares);
        StopAndRunToIdle();
    }

    /// <summary>Restart the mission does not save: the game starts again with the saved files (section 5.8 of the M6c
    /// design, review fix).</summary>
    [Fact]
    public void RestartTheMissionDoesNotSave()
    {
        var mission = new FakeMissionStart();
        PlayMissionToRunning(Game with { RestartLines = [Restarted(1)] }, mission);
        Assert.Null(play.RestartBlock);
        var saves = editor.SaveCalls;
        editor.SaveResult = new SaveOutcome(true, 2, false);

        play.RestartMission();
        play.Update(false, false);
        RunUntil(PlayStatus.Running);

        Assert.Equal(saves, editor.SaveCalls);
        Assert.Equal(["start", "restart 1"], stub.Input(0));
        StopAndRunToIdle();
        Assert.Equal("Available while a mission play runs.", play.RestartBlock);
    }

    /// <summary>
    /// An agent request that did not ask the user, because the mod had no unsaved edits, does not save edits that came
    /// later, for example while the request waited for a field edit (user decision of 2026-10-20 of the M6c design). It
    /// gives refused, and the agent sends it again, so the editor asks. A request of the user saves them.
    /// </summary>
    [Theory]
    [InlineData((int)PlaySaveRule.NoEdits, true, false)]
    [InlineData((int)PlaySaveRule.NoEdits, false, true)]
    [InlineData((int)PlaySaveRule.Save, true, true)]
    public void AnAgentRequestDoesNotSaveEditsThatCameAfterIt(int rule, bool unsaved, bool started)
    {
        editor.Commit(1);
        editor.Finish(1);
        var result = play.RequestMission(new FakeMissionStart(), null, (PlaySaveRule)rule);
        play.Update(editing: true, dialogOpen: false);

        editor.Session = editor.Session! with { Unsaved = unsaved };
        play.Update(false, false);

        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(started, result.Result.Started);
        Assert.Equal(started ? 1 : 0, editor.SaveCalls);
        Assert.Equal(started ? 1 : 0, starts.Count);
        if (!started)
        {
            Assert.Equal(PlayRequests.NotSavedDetail, result.Result.Error!.Detail);
            Assert.Equal(PlayRequests.EditsWhileWaiting("proof"), result.Result.Error.Message);
            return;
        }

        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>
    /// A mission play whose start place can be in the Scene waits until the Scene shows each edit. The Scene applies the
    /// edits only while it draws. So before the panels draw, Play lets the Scene apply them, and the play starts in the
    /// next frame also while the Scene does not show, for example after a node drag in the Mission tab without its Scene
    /// part (review fix of M6c-2).
    /// </summary>
    [Fact]
    public void AMissionPlayLetsAHiddenSceneShowTheEditsAndStartsInTheNextFrame()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.SceneBehind = true;
        var mission = new FakeMissionStart();

        play.RequestMission(mission, null);
        play.Update(false, false);

        Assert.Equal(0, mission.Prepares);
        Assert.Empty(starts);
        // The next frame: no Scene draws, so only Play makes the Scene apply the edits.
        play.BeforePanels(editing: false);
        Assert.Equal(1, editor.CatchUpCalls);
        play.Update(false, false);
        Assert.Equal(1, mission.Prepares);
        Assert.DoesNotContain(PlayController.SceneWaitText, Console);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>F5 during a mission play also lets a hidden Scene apply the edits, and then the game starts again at the
    /// new start.</summary>
    [Fact]
    public void F5DuringAMissionPlayLetsAHiddenSceneShowTheEdits()
    {
        PlayMissionToRunning(Game with { RestartLines = [Restarted(1)] }, new FakeMissionStart());
        var second = new FakeMissionStart();
        editor.SceneBehind = true;

        play.RequestMission(second, null);
        play.Update(false, false);
        Assert.Equal(0, second.Prepares);

        play.BeforePanels(editing: false);
        play.Update(false, false);

        Assert.Equal(1, second.Prepares);
        RunUntil(PlayStatus.Running);
        Assert.Same(second, play.Mission);
        Assert.Equal(["start", "restart 1"], stub.Input(0));
        StopAndRunToIdle();
    }

    /// <summary>A mission play at a base that the user picked does not read the Scene, so it does not wait for
    /// it.</summary>
    [Fact]
    public void AMissionPlayAtAPickedBaseDoesNotWaitForTheScene()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.SceneBehind = true;
        var mission = new FakeMissionStart { ReadsScene = false };

        play.RequestMission(mission, null);
        play.BeforePanels(editing: false);
        play.Update(false, false);

        Assert.Equal(0, editor.CatchUpCalls);
        Assert.Equal(1, mission.Prepares);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>
    /// When the Scene cannot show each edit, because a new model waits for the load of the game data, Play waits and the
    /// Console tells why, once. Play does not make the Scene apply the edits while a field edit or a drag is open. The
    /// play starts when the Scene shows each edit.
    /// </summary>
    [Fact]
    public void PlayTellsOnceWhyItWaitsWhenTheSceneCannotShowEachEdit()
    {
        editor.Commit(1);
        editor.Finish(1);
        editor.SceneBehind = true;
        editor.SceneStaysBehind = true;

        play.Request(Space, null);
        play.BeforePanels(editing: true);
        Assert.Equal(0, editor.CatchUpCalls);
        for (var frame = 0; frame < 3; frame++)
        {
            play.BeforePanels(editing: false);
            play.Update(false, false);
        }

        Assert.Equal(3, editor.CatchUpCalls);
        Assert.Empty(starts);
        Assert.Equal(1, Console.Count(m => m == PlayController.SceneWaitText));

        editor.SceneStaysBehind = false;
        play.BeforePanels(editing: false);
        play.Update(false, false);

        Assert.Single(starts);
        RunUntil(PlayStatus.Running);
        StopAndRunToIdle();
    }

    /// <summary>A save while a restart runs: the game can show parts of it, and the Console says so. The trigger then
    /// sends the restart of that save. The watchdog of a restart names the restart.</summary>
    [Fact]
    public void ASaveDuringARestartRestartsAgainAndTheWatchdogNamesTheRestart()
    {
        var mission = new FakeMissionStart();
        PlayMissionToRunning(Game with { RestartLines = [Restarted(2), Restarted(3)] }, mission);
        editor.Commit(2);
        play.Update(false, false);
        editor.Finish(2);
        Assert.Equal(PlayStatus.Reloading, play.Status);
        editor.Commit(3);
        play.Update(false, false);
        ui.RunUntil(() => stub.Input(0).Count >= 2 && Console.Any(l => l.StartsWith("Save 3 came during the restart")),
            "the note of the save during the restart");
        editor.Finish(3);
        ui.RunUntil(() => stub.Input(0).Count >= 3 && play.Status == PlayStatus.Running, "the second restart");

        Assert.Equal(["start", "restart 2", "restart 3"], stub.Input(0));
        Assert.Contains("Save 3 came during the restart, so the game can show parts of it.", Console);
        StopAndRunToIdle();

        var waits = new FakeMissionStart();
        PlayMissionToRunning(Game, waits);
        editor.Commit(4);
        play.Update(false, false);
        editor.Finish(4);
        now += PlayController.ReloadLimit + TimeSpan.FromSeconds(1);
        play.Update(false, false);
        Assert.Equal(PlayStatus.NotResponding, play.Status);
        Assert.Equal("Play: the game does not respond. It did not start the mission again with save 4 in 30 s. Stop " +
                     "ends it.", Console[^1]);
        play.Stop();
        play.Stop();
        RunUntil(PlayStatus.Idle);
    }

    /// <summary>A new start that waits gives its refusal when the game exits, and when the validation of its save finds
    /// errors (section 12.1 of the M6c design).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewStartThatWaitsGivesItsRefusal(bool exits)
    {
        PlayMissionToRunning(Game, new FakeMissionStart());
        editor.Commit(2);
        var result = play.RequestMission(new FakeMissionStart { StartName = "t_wave (approximate)" }, null);
        play.Update(false, false);
        Assert.False(result.IsCompleted);

        if (exits)
        {
            using (var process = Process.GetProcessById(stub.ProcessId(0)))
                process.Kill();
            RunUntil(PlayStatus.Idle);
        }
        else
        {
            editor.Finish(2, HullError);
        }

        Assert.True(result.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(exits ? "GameClosed" : "ValidationErrors", result.Result.Error!.Detail);
        if (!exits)
        {
            Assert.Contains("Play did not start M99 from t_wave (approximate): the validation of save 2 found 1 error.",
                Console);
            StopAndRunToIdle();
        }
    }

    private static string Restarted(int save, long ms = 600) =>
        PlayProtocol.Write(new PlayEvent.Restarted(save, ms, "Br01", null));

    private static string RestartFailed(int save) =>
        PlayProtocol.Write(new PlayEvent.RestartFailed(save, "The start save does not load."));

    /// <summary>Starts a mission play for save 1, whose validation passed, and runs it until the spawn.</summary>
    private void PlayMissionToRunning(StubPlan game, FakeMissionStart mission)
    {
        plan = game;
        editor.Commit(1);
        editor.Finish(1);
        play.RequestMission(mission, null);
        play.Update(false, false);
        RunUntil(PlayStatus.Running);
    }

    /// <summary>A mission start of the tests. Each prepare gives the same start save, or refuses with a reason.</summary>
    private sealed class FakeMissionStart : IMissionStart
    {
        public string Script => @"DATA\MISSIONS\M99\m99.ini";

        public string Slot => "M99";

        public string StartName { get; init; } = "Checkpoint t_restore";

        public bool Approximate { get; init; }

        public bool ReadsScene { get; init; } = true;

        public IReadOnlySet<string> SetupTriggers { get; init; } = new HashSet<string>();

        public StartPlan? Plan => null;

        public string? Refusal { get; private set; }

        /// <summary>The reason of the next prepares, or null to start.</summary>
        public string? RefuseWith { get; set; }

        public int Prepares { get; private set; }

        /// <summary>Runs at each prepare.</summary>
        public Action? OnPrepare { get; set; }

        public PlayStart.FromSave Start { get; } = new(@"C:\profiles\p.play\start\start.fl", @"C:\profiles\p.play\start\layer");

        public PlayStart.FromSave? Prepare()
        {
            Prepares++;
            OnPrepare?.Invoke();
            Refusal = RefuseWith;
            return RefuseWith == null ? Start : null;
        }
    }

    private static string Error(string message, bool crash) => PlayProtocol.Write(new PlayEvent.Error(message, crash));

    /// <param name="dock">The base of the player, or null in space.</param>
    private static string Reloaded(int save, long ms = 600, string? dock = null) =>
        PlayProtocol.Write(new PlayEvent.Reloaded(save, ms, "Br01", dock));

    private static string ReloadFailed(int save) =>
        PlayProtocol.Write(new PlayEvent.ReloadFailed(save, "The new data cannot load the autosave of the player."));

    /// <summary>Starts a game for save 1, whose validation passed, and runs it until the spawn.</summary>
    private void PlayToRunning(StubPlan game)
    {
        plan = game;
        editor.Commit(1);
        editor.Finish(1);
        RequestPlay();
        RunUntil(PlayStatus.Running);
    }

    /// <summary>A death zone with a radius of 1,000 whose center is 900 units ahead of the camera of
    /// <see cref="Space"/>. The near exit is 100 units behind the camera, so the start moves 200 units back.</summary>
    private static SpawnShape DeathZone()
    {
        var zone = new Zone
        {
            Nickname = "Zone_Br01_death", Shape = ShapeKind.Sphere, RotationMatrix = Matrix4x4.Identity,
            Size = new Vector3(1_000), Position = Space.Position - new Vector3(0, 0, 900),
        };
        zone.Damage = 2_000_000;
        return Assert.Single(SpawnPoint.Shapes(new Dictionary<SystemObject, GameObject>(), [zone], z => z.Nickname));
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) <= 0.05f, $"Expected {expected}, but got {actual}.");

    private void RequestPlay(PlayStart? start = null)
    {
        play.Request(start ?? Space, null);
        play.Update(false, false);
    }

    private void RunUntil(PlayStatus status) => ui.RunUntil(() => play.Status == status, $"status {status}");

    private void StopAndRunToIdle()
    {
        play.Stop();
        Assert.Equal(PlayStatus.Stopping, play.Status);
        RunUntil(PlayStatus.Idle);
    }

    /// <summary>
    /// The editor side of Play. A save with a number becomes the last save of the session and starts its validation, as
    /// <see cref="EditController"/> does. The test gives each validation result.
    /// </summary>
    internal sealed class FakeEditor(QueuedUIThread ui) : IPlayEditor
    {
        public ConsoleLog Log { get; } = new();

        public PlaySession? Session { get; set; } = new(@"C:\profiles\p.json", "proof", 0, 0, null, []);

        public ValidationState? Validation { get; private set; }

        public event Action<ValidationState>? Validated;

        /// <summary>The outcome of each save.</summary>
        public SaveOutcome SaveResult { get; set; } = new(true, 0, false);

        public int SaveCalls { get; private set; }

        public int ValidateNowCalls { get; private set; }

        /// <summary>True: <see cref="ValidateNow"/> gives a result at once, as when oltool does not start.</summary>
        public bool ValidationCannotStart { get; set; }

        public SaveOutcome Save()
        {
            SaveCalls++;
            if (SaveResult is { Saved: true, Number: > 0 } saved)
            {
                if (saved.Incomplete)
                {
                    SaveIncomplete(saved.Number);
                }
                else
                {
                    Commit(saved.Number);
                }
            }

            return SaveResult;
        }

        /// <summary>A save that changed files, for example Ctrl+S: it stops the running validation and starts its
        /// own.</summary>
        public void Commit(int number)
        {
            Session = Session! with { Saves = number, IncompleteSave = 0 };
            Validation = new ValidationState(number, true, null);
        }

        /// <summary>A save that changed files and is not complete: it stops the running validation and starts no
        /// validation.</summary>
        public void SaveIncomplete(int number)
        {
            StopValidation();
            Session = Session! with { Saves = number, IncompleteSave = number };
        }

        /// <summary>The stop of the running validation before a save. The state keeps its save without a result. A save
        /// that then writes nothing, for example after an exception, starts no validation.</summary>
        public void StopValidation()
        {
            if (Validation is { Running: true } running)
            {
                Validation = running with { Running = false };
            }
        }

        public void ValidateNow()
        {
            ValidateNowCalls++;
            if (ValidationCannotStart)
            {
                FailToRun(Session!.Saves);
            }
            else
            {
                Validation = new ValidationState(Session!.Saves, true, null);
            }
        }

        public void Finish(int save, params Diagnostic[] problems) => Done(new ValidationResult(save, problems, null));

        public void FailToRun(int save) => Done(new ValidationResult(save, null, "oltool did not start."));

        public void QueueUIThread(Action work) => ui.QueueUIThread(work);

        public string SystemName(string nickname) =>
            nickname.Equals("br01", StringComparison.OrdinalIgnoreCase) ? "New London" : "";

        public string BaseName(string nickname) =>
            nickname.Equals("br01_01_base", StringComparison.OrdinalIgnoreCase) ? "Planet New London" : "";

        public bool SceneBehind { get; set; }

        /// <summary>True: the Scene stays behind after a catch-up, as while a new model waits for the load of the game
        /// data.</summary>
        public bool SceneStaysBehind { get; set; }

        public int CatchUpCalls { get; private set; }

        public void CatchUpScene()
        {
            CatchUpCalls++;
            SceneBehind = SceneStaysBehind;
        }

        /// <summary>The shapes of Br01, the system that the Scene shows.</summary>
        public List<SpawnShape> Shapes { get; } = [];

        public IReadOnlyList<SpawnShape>? SpawnShapes(string system) =>
            system.Equals("br01", StringComparison.OrdinalIgnoreCase) ? Shapes.ToList() : null;

        /// <summary>The mission events of the game, in order.</summary>
        public List<PlayEvent.MissionChanges> MissionEvents { get; } = [];

        public void MissionChanged(PlayEvent.MissionChanges changes) => MissionEvents.Add(changes);

        /// <summary>The calls of the live view, in order: "started" and "ended".</summary>
        public List<string> LiveCalls { get; } = [];

        public void PlayStarted() => LiveCalls.Add("started");

        public void PlayEnded() => LiveCalls.Add("ended");

        private void Done(ValidationResult result)
        {
            Validation = new ValidationState(result.Save, false, result);
            Validated?.Invoke(Validation);
        }
    }
}
