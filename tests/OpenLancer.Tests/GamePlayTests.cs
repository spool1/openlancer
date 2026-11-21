using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Strings;
using OpenLancer.Editor;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Studio;
using Xunit;
using static OpenLancer.Tests.PlayGame;

namespace OpenLancer.Tests;

/// <summary>A program of a play copy for the tests. It records the closes of its window and the kill.</summary>
internal sealed class FakeGameProcess : IGameProcess
{
    public int Closes { get; private set; }

    public bool HasWindow { get; set; } = true;

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public bool CloseMainWindow()
    {
        Closes++;
        return HasWindow;
    }

    public void Kill() => Killed = true;

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Play in the game with a fake editor, fake programs and a fake clock, on a fake game (milestone M11). The play copy of
/// the game is real, in the temporary folder of the test. No test starts a game program or FLServer.
/// </summary>
internal sealed class GamePlayRig : IAsyncDisposable
{
    public const string GoodsPatch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    /// <param name="strings">The writer of the text DLL of the merge, or null for the fake writer.</param>
    public GamePlayRig(bool hd = false, IStringResourceWriter? strings = null)
    {
        Game = new PlayGame(hd);
        Plays = new GamePlay(Host, (info, exited) =>
        {
            var process = new FakeGameProcess();
            Starts.Add((info, exited, process));
            return process;
        }, strings ?? TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator, () => Now);
        var report = Game.Export.Report();
        Host.Games.Add(report);
        Host.Settings.Games.Add(new LinkedGame(report.Root!, DateTime.UtcNow, report.Name, report.Build));
    }

    public PlayGame Game { get; }

    public FakeGameHost Host { get; } = new();

    public GamePlay Plays { get; }

    /// <summary>The start info, the exit callback and the fake process of each start.</summary>
    public List<(ProcessStartInfo Info, Action<int> Exit, FakeGameProcess Process)> Starts { get; } = [];

    public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(100);

    public PlayTarget Target => Plays.Targets.Single();

    public IReadOnlyList<string> Console => Host.Log.Entries.Select(e => e.Message).ToList();

    /// <summary>Opens a profile with the mods. The last mod is the active mod, and its last validation passed.</summary>
    public void Open(params Mod[] mods)
    {
        Host.Session = new PlaySession(Game.Profile(mods), mods.LastOrDefault()?.Id, 0, 0, null, []);
        Host.Finish(0);
    }

    /// <summary>Opens a profile with a goods patch and plays until the game starts.</summary>
    public async Task StartGameAsync()
    {
        Open(Game.Export.GoodsMod(GoodsPatch));
        Plays.Play(Target);
        await RunAsync(() => Starts.Count == 1, "the start of the game");
    }

    /// <summary>Runs the queued actions and the frames of the editor until the condition is true.</summary>
    public async Task RunAsync(Func<bool> done, string what)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            Host.Ui.RunQueued();
            Plays.Update(false, false);
            if (done())
                return;
            Assert.True(DateTime.UtcNow < end, $"Not in 60 s: {what}. The Console: {string.Join(" | ", Console)}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RunAsync(() => !Plays.Checking, "the end of the checks");
        Plays.Dispose();
        Game.Dispose();
    }
}

/// <summary>The editor side of Play in the game.</summary>
internal sealed class FakeGameHost : IGamePlayHost
{
    public QueuedUIThread Ui { get; } = new();

    public ConsoleLog Log { get; } = new();

    public PlaySession? Session { get; set; }

    public ValidationState? Validation { get; private set; }

    public event Action<ValidationState>? Validated;

    public SaveOutcome SaveResult { get; set; } = new(true, 0, false);

    public int SaveCalls { get; private set; }

    public EditorSettings Settings { get; } = EditorSettings.Transient();

    public List<GameReport> Games { get; } = [];

    public List<string> Protected { get; } = [];

    public (string Title, string Message, IReadOnlyList<Choice> Choices)? Asked { get; private set; }

    /// <summary>The callback of the last folder picker.</summary>
    public Action<string>? Chooser { get; private set; }

    public SaveOutcome Save()
    {
        SaveCalls++;
        if (SaveResult is { Saved: true, Number: > 0 } saved)
            Commit(saved.Number);
        return SaveResult;
    }

    /// <summary>The status frame of the save gate, as the editor has it. The test calls its frames.</summary>
    public GateWait Gate { get; } = new();

    /// <summary>True: the save gate waits for the index of the cutscene uses.</summary>
    public bool GateWaits { get; set; }

    public bool HoldForGate(object request) =>
        Gate.Hold(request, GateWaits, s => Log.AddStatus(ConsoleSeverity.Info, "Save", s));

    /// <summary>A save that changed files: it starts its validation.</summary>
    public void Commit(int number)
    {
        Session = Session! with { Saves = number };
        Validation = new ValidationState(number, true, null);
    }

    public void ValidateNow() => Validation = new ValidationState(Session!.Saves, true, null);

    public void Finish(int save, params Diagnostic[] problems)
    {
        Validation = new ValidationState(save, false, new ValidationResult(save, problems, null));
        Validated?.Invoke(Validation);
    }

    public IReadOnlyList<GameReport> LinkedGameReports() => Games;

    public void Ask(string title, string message, IReadOnlyList<Choice> choices) => Asked = (title, message, choices);

    public void ChooseFolder(Action<string> chosen) => Chooser = chosen;

    public IReadOnlyList<string> ProtectedFolders() => Protected;

    public void QueueUIThread(Action work) => Ui.QueueUIThread(work);
}

/// <summary>Play in the game (section 8.6 of the M11 design).</summary>
public sealed class GamePlayTests : IAsyncDisposable
{
    private readonly GamePlayRig rig = new();

    public ValueTask DisposeAsync() => rig.DisposeAsync();

    /// <summary>The README table "Supported games" says that Freelancer 1.0 needs the 1.1 patch before Ctrl+F5. The
    /// item of a 1.0 game gives that cell in its tooltip. The item of a 1.1 game has no such note.</summary>
    [Fact]
    public void TheItemOfFreelancer10TellsToPatchTheGameFirst()
    {
        var oneZero = Assert.Single(PlayTarget.Of(rig.Host.Games));
        var oneOne = oneZero with { Game = oneZero.Game with { Version = "1.1" } };
        Assert.Equal((GameKind.Vanilla, "1.0"), (oneZero.Game.Kind, oneZero.Game.Version));
        var readme = File.ReadAllText(RepoFiles.Of("README.md"));

        Assert.Equal(PlayTarget.PatchFirst, oneZero.Note);
        Assert.EndsWith($"\n\n{PlayTarget.PatchFirst}", PlayMenu.GameTooltip(oneZero));
        Assert.Null(oneOne.Note);
        Assert.DoesNotContain(PlayTarget.PatchFirst, PlayMenu.GameTooltip(oneOne));
        Assert.Contains($"| Freelancer 1.0 (retail) | Yes. It has the same INI files as 1.1. | Yes, for 1.1 | {PlayTarget.PatchFirst} |",
            readme);
    }

    [Fact]
    public async Task TheGameStartsInItsProgramFolder()
    {
        await rig.StartGameAsync();

        var (info, exit, process) = rig.Starts[0];
        Assert.Equal(rig.Game.CopyPath(ProgramPath), info.FileName);
        Assert.Equal(rig.Game.CopyPath("EXE"), info.WorkingDirectory);
        Assert.Empty(info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.Contains("price = 5600", File.ReadAllText(rig.Game.CopyPath(ExportGame.GoodsPath)));
        Assert.DoesNotContain("price = 5600", File.ReadAllText(rig.Game.Game.PathOf(ExportGame.GoodsPath)));
        Assert.Equal("Playing in Freelancer 1.0 (play copy)", rig.Plays.GameStatus);
        Assert.Contains(PlayCopyTexts.Make("Freelancer 1.0", rig.Game.CopyFolder), rig.Console);
        Assert.Contains("Play in Freelancer 1.0: OpenLancer installed 1 file. The game starts.", rig.Console);
        // The program of 1.0 does not start on Windows 10 and 11, so the Console gives the Play cell of the README.
        Assert.Contains($"Play in Freelancer 1.0: {PlayTarget.PatchFirst}", rig.Console);
        Assert.Contains(GamePlay.SavesNote, rig.Console);
        Assert.Equal(rig.Game.Root, rig.Host.Settings.LastGameTarget);
        Assert.Equal(1, rig.Host.SaveCalls);
        // The game holds the lock of the play copy while it runs.
        Assert.Throws<PlayCopyException>(() => PlayCopy.Open(rig.Game.CopyFolder));

        exit(0);
        await rig.RunAsync(() => !rig.Plays.Checking, "the check after the exit");

        Assert.True(process.Disposed);
        Assert.Null(rig.Plays.GameStatus);
        Assert.Contains("The game of the play copy closed.", rig.Console);
        using (PlayCopy.Open(rig.Game.CopyFolder))
        {
        }
    }

    [Fact]
    public async Task StopClosesThenForceStops()
    {
        await rig.StartGameAsync();
        var process = rig.Starts[0].Process;

        rig.Plays.StopGame();
        rig.Plays.StopGame();

        Assert.Equal(1, process.Closes);
        Assert.False(rig.Plays.ForceStop);
        Assert.Equal("Stopping the game of the play copy", rig.Plays.GameStatus);
        rig.Now += GamePlay.ForceStopLimit;
        Assert.True(rig.Plays.ForceStop);

        rig.Plays.StopGame();

        Assert.True(process.Killed);
        Assert.Contains("The game of the play copy did not close, so the editor ended it.", rig.Console);
        rig.Starts[0].Exit(1);
        Assert.DoesNotContain(rig.Console, c => c.Contains("exit code"));
    }

    [Fact]
    public async Task AGameWithoutAWindowGetsForceStopAtOnce()
    {
        await rig.StartGameAsync();
        rig.Starts[0].Process.HasWindow = false;

        rig.Plays.StopGame();

        Assert.True(rig.Plays.ForceStop);
        Assert.Contains("The game has no window that OpenLancer can close. Click Force stop to end it.", rig.Console);
    }

    [Fact]
    public async Task ASaveWhileTheGameRunsGivesTheNote()
    {
        await rig.StartGameAsync();

        rig.Host.Commit(1);
        rig.Plays.Update(false, false);
        rig.Plays.Update(false, false);

        Assert.Single(rig.Console, GamePlay.ReloadNote);
    }

    /// <summary>
    /// When the save gate waits for the index of the cutscene uses (#87), each Play in the game first shows the status
    /// line for one frame, and then saves. So the menu items and Ctrl+F5 show it the same way.
    /// </summary>
    [Fact]
    public async Task APlayThatWaitsForTheSaveGateShowsTheStatusLineForOneFrameFirst()
    {
        rig.Open(rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch));
        rig.Host.GateWaits = true;

        rig.Plays.Play(rig.Target);
        rig.Plays.Update(false, false);
        Assert.Equal(GateWait.Status, rig.Console[^1]);
        Assert.Equal(0, rig.Host.SaveCalls);
        Assert.True(rig.Plays.Busy);
        // The frame that draws the status line.
        rig.Host.Gate.Frame();
        rig.Plays.Update(false, false);
        Assert.Equal(0, rig.Host.SaveCalls);
        rig.Host.Gate.Frame();
        rig.Plays.Update(false, false);

        Assert.Equal(1, rig.Host.SaveCalls);
        Assert.Single(rig.Console, GateWait.Status);
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start of the game");
    }

    [Fact]
    public void PlayRefusesAModWithoutTheTarget()
    {
        rig.Open(rig.Game.Export.Mod("hd-only", new() { [@"patches\" + ExportGame.GoodsPath] = GamePlayRig.GoodsPatch },
            """, "targets": ["hde-0.7.1"]"""));

        rig.Plays.Play(rig.Target);
        rig.Plays.Update(false, false);

        Assert.Contains("Play did not start. Mod 'hd-only' does not name the target 'vanilla-1.1'. Add the target to " +
                        "mod.json, or pick another target.", rig.Console);
        Assert.Empty(rig.Starts);
        Assert.Equal(0, rig.Host.SaveCalls);
        Assert.False(Directory.Exists(rig.Game.CopyFolder));
    }

    [Fact]
    public void PlayRefusesATier2ModOfTheProfile()
    {
        rig.Open(rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch, "base"),
            rig.Game.Export.Mod("runtime", [], """, "tier": 2"""));

        rig.Plays.Play(rig.Target);
        rig.Plays.Update(false, false);

        Assert.Contains("Play did not start. Mod 'runtime' is a tier-2 mod. A tier-2 mod runs only in OpenLancer. Pick " +
                        "the target OpenLancer.", rig.Console);
        Assert.Empty(rig.Starts);
    }

    [Fact]
    public async Task ACheckErrorStopsPlay()
    {
        rig.Open(rig.Game.Export.GoodsMod($"[Good]\nnickname = g2\nname = {new string('a', 1100)}\n"));

        rig.Plays.Play(rig.Target);
        await rig.RunAsync(() => rig.Console.Any(c => c.StartsWith("Play did not start.")), "the refusal");

        Assert.Contains(rig.Host.Log.Entries, e => e.Severity == ConsoleSeverity.Error &&
                                                   e.Message.EndsWith("The game writes past the end of its buffer."));
        Assert.Contains("Play did not start. Fix the errors, then play again.", rig.Console);
        Assert.Empty(rig.Starts);
        Assert.False(Directory.Exists(rig.Game.CopyFolder));
        Assert.Null(rig.Plays.GameStatus);
    }

    [Fact]
    public void AValidationErrorStopsPlay()
    {
        rig.Open(rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch));
        rig.Host.SaveResult = new SaveOutcome(true, 2, false);

        rig.Plays.Play(rig.Target);
        rig.Plays.Update(false, false);

        Assert.Equal("Play in Freelancer 1.0: validating save 2...", rig.Plays.GameStatus);
        rig.Host.Finish(2, Diagnostic.Error("goods.ini", "no_such_hull"));
        rig.Plays.Update(false, false);
        Assert.Contains("Play did not start. The validation of save 2 found 1 error. The Console lists them.", rig.Console);
        Assert.Empty(rig.Starts);
        Assert.Null(rig.Plays.GameStatus);
    }

    [Fact]
    public async Task ARequestWaitsForTheEndOfAFieldEdit()
    {
        rig.Open(rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch));

        rig.Plays.Play(rig.Target);
        rig.Plays.Update(editing: true, dialogOpen: false);
        rig.Plays.Update(editing: false, dialogOpen: true);

        Assert.Equal(0, rig.Host.SaveCalls);
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start of the game");
        Assert.Equal(1, rig.Host.SaveCalls);
    }

    [Fact]
    public async Task TheLinkCheckRunsAfterTheExit()
    {
        await rig.StartGameAsync();
        // A program writes into a linked file of the play copy in place, so the game file changes too.
        var model = rig.Game.Game.PathOf(SoundPath);
        using (var stream = new FileStream(model, FileMode.Append, FileAccess.Write))
            stream.Write(" and more"u8);

        rig.Starts[0].Exit(0);
        await rig.RunAsync(() => !rig.Plays.Checking, "the check after the exit");

        Assert.Contains(PlayCopyTexts.LinkedFileChanged(rig.Game.CopyPath(SoundPath), model), rig.Console);
    }

    [Fact]
    public async Task ExportAndPlayInstallsTheExport()
    {
        var mod = rig.Game.Export.Mod("m", new()
        {
            ["strings.json"] = """{ "strings": { "name": "Name" } }""",
            [@"patches\" + ExportGame.GoodsPath] = "[Good]\nnickname = g2\nids_name = $str:name\n",
        });
        var result = ModExport.Run(rig.Game.Export.Request(mod) with { Forms = ExportForms.DropIn });
        Assert.True(result.Succeeded, string.Join(" | ", result.Problems));

        rig.Plays.PlayExport(rig.Host.Games[0], result.Builds[ExportTarget.Vanilla], Played(mod, result.Content));
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start of the game");

        Assert.Equal("Playing the export of m in Freelancer 1.0 (play copy)", rig.Plays.GameStatus);
        Assert.True(File.Exists(rig.Game.CopyPath(@"EXE\ol_m.dll")));
        Assert.Contains("DLL = ol_m.dll", File.ReadAllText(rig.Game.CopyPath(@"EXE\freelancer.ini")));
        Assert.DoesNotContain("ol_m.dll", File.ReadAllText(rig.Game.Game.PathOf(@"EXE\freelancer.ini")));
        Assert.Equal(0, rig.Host.SaveCalls);
    }

    /// <summary>A game that runs a minute records a test play in the history of each mod of the profile (decision D569
    /// of the M11c design), with the target of the game and the content at the install.</summary>
    [Fact]
    public async Task AGameThatRanAMinuteRecordsATestPlayForEachInstalledMod()
    {
        var first = rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch, "first");
        var second = rig.Game.Export.Mod("second", []);
        rig.Open(first, second);
        rig.Plays.Play(rig.Target);
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start of the game");

        rig.Now += GamePlay.TestPlayTime;
        rig.Starts[0].Exit(0);
        await rig.RunAsync(() => rig.Console.Count(c => c.StartsWith("Recorded a test play")) == 2, "the records of the plays");

        foreach (var mod in new[] { first, second })
        {
            var play = Assert.Single(History(mod).Plays);
            Assert.Equal(ExportTarget.Vanilla.Id, play.Target);
            Assert.False(play.Alone);
            Assert.False(play.Manual);
            Assert.Equal(60, play.Seconds);
            Assert.Equal(new ModFingerprint().Content(mod.Manifest, mod.Folder), play.Content);
        }

        Assert.Contains("Recorded a test play of Mod first (first) in Freelancer 1.0: 60 s.", rig.Console);
    }

    /// <summary>A play of the profile takes the fingerprints before the merge (review of M11c Build 2). A file that
    /// changes during the merge then gives the test play the old content, so the play does not count for the new
    /// content, which the game can miss.</summary>
    [Fact]
    public async Task ATestPlayHasTheContentOfTheStartOfTheInstall()
    {
        string? patch = null;
        var writer = new ChangingWriter(() => File.AppendAllText(patch!, "\n; A change during the merge.\n"));
        await using var local = new GamePlayRig(strings: writer);
        var mod = local.Game.Export.Mod("m", new()
        {
            ["strings.json"] = """{ "strings": { "name": "Name" } }""",
            [@"patches\" + ExportGame.GoodsPath] = "[Good]\nnickname = g2\nids_name = $str:name\n",
        });
        patch = Path.Combine(mod.Folder, "patches", ExportGame.GoodsPath);
        var before = new ModFingerprint().Content(mod.Manifest, mod.Folder);
        local.Open(mod);

        local.Plays.Play(local.Target);
        await local.RunAsync(() => local.Starts.Count == 1, "the start of the game");
        local.Now += GamePlay.TestPlayTime;
        local.Starts[0].Exit(0);
        await local.RunAsync(() => local.Console.Any(c => c.StartsWith("Recorded a test play")), "the record of the play");

        Assert.True(writer.Changed);
        var history = PublishHistory.Load(PublishHistory.FolderOf(local.Game.Game.Folder, mod.Id), mod.Id).History;
        var play = Assert.Single(history.Plays);
        Assert.Equal(before, play.Content);
        Assert.NotEqual(before, new ModFingerprint().Content(mod.Manifest, mod.Folder));
    }

    /// <summary>A writer of the text DLL that changes a file of the mod at its first call, during the merge.</summary>
    private sealed class ChangingWriter(Action change) : IStringResourceWriter
    {
        public bool Changed { get; private set; }

        public byte[] Write(IReadOnlyDictionary<int, string> strings, IReadOnlyDictionary<int, string> infocards)
        {
            if (!Changed)
            {
                Changed = true;
                change();
            }

            return TestWorkspace.FakeDllWriter.Instance.Write(strings, infocards);
        }
    }

    /// <summary>The rule of a test play (finding R17): Force stop and a crash code give no play. The exit code comes as
    /// a signed number, so the rule compares it as an unsigned number.</summary>
    [Theory]
    [InlineData(false, false, 0, 60, true)]
    [InlineData(false, false, 1, 60, true)]
    [InlineData(false, true, 0, 60, false)]
    [InlineData(false, false, unchecked((int)0xC0000005), 60, false)]
    [InlineData(false, false, unchecked((int)0xC0000000), 600, false)]
    [InlineData(false, false, 0, 59, false)]
    [InlineData(true, false, 0, 600, false)]
    public void TheRuleOfATestPlay(bool server, bool killed, int code, int seconds, bool counts)
    {
        Assert.Equal(counts, GamePlay.IsTestPlay(server, killed, code, TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>The worker of Play and the check after an exit wait until each earlier check ended, because a check holds
    /// the lock of the play copy. A failed check does not end the wait early, and it does not stop the next work.</summary>
    [Fact]
    public async Task TheWaitForEarlierChecksWaitsForEachCheckAlsoAFailedOne()
    {
        var failed = new TaskCompletionSource();
        var running = new TaskCompletionSource();
        var wait = OwnThread.Run(() => GamePlay.WaitFor([failed.Task, running.Task]));

        failed.SetException(new IOException("The check failed."));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var early = wait.IsCompleted;
        running.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(early);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceStopOrACrashCodeRecordsNoPlay(bool forceStop)
    {
        await rig.StartGameAsync();
        rig.Now += GamePlay.TestPlayTime;
        if (forceStop)
        {
            rig.Plays.StopGame();
            rig.Now += GamePlay.ForceStopLimit;
            rig.Plays.StopGame();
        }

        rig.Starts[0].Exit(forceStop ? 1 : unchecked((int)0xC0000005));

        await AssertNoPlayAsync("m");
    }

    [Fact]
    public async Task AShortRunRecordsNoPlay()
    {
        await rig.StartGameAsync();
        rig.Now += GamePlay.TestPlayTime - TimeSpan.FromSeconds(1);

        rig.Starts[0].Exit(0);

        await AssertNoPlayAsync("m");
    }

    /// <summary>A test server run records nothing. The game that joins it is a game run, with the mods of the
    /// install of the server.</summary>
    [Fact]
    public async Task TheTestServerRecordsNoPlay()
    {
        var mod = rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch);
        rig.Open(mod);
        rig.Plays.HostServer(rig.Target);
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start of the server");
        rig.Now += GamePlay.TestPlayTime;
        rig.Starts[0].Exit(0);
        await AssertNoPlayAsync(mod.Id);

        rig.Plays.HostServer(rig.Target);
        await rig.RunAsync(() => rig.Starts.Count == 2, "the start of the server");
        rig.Plays.JoinServer();
        rig.Now += GamePlay.TestPlayTime;
        rig.Starts[2].Exit(0);
        await rig.RunAsync(() => rig.Console.Any(c => c.StartsWith("Recorded a test play")), "the record of the play");

        Assert.Single(History(mod).Plays);
        rig.Starts[1].Exit(0);
    }

    /// <summary>"Play this mod alone" of the Mods window (decision D577) records a play alone.</summary>
    [Fact]
    public async Task PlayOfOneModRecordsAPlayAlone()
    {
        var mod = rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch);
        var prepared = ModExport.Prepare(rig.Game.Export.Request(mod) with { Forms = 0, ChecklistOnly = true });

        rig.Plays.PlayExport(rig.Host.Games[0], prepared.Builds[ExportTarget.Vanilla], Played(mod, prepared.Content),
            alone: true);
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start of the game");
        Assert.Equal("Playing Mod m alone in Freelancer 1.0 (play copy)", rig.Plays.GameStatus);
        rig.Now += GamePlay.TestPlayTime;
        rig.Starts[0].Exit(0);
        await rig.RunAsync(() => rig.Console.Any(c => c.StartsWith("Recorded a test play")), "the record of the play");

        var play = Assert.Single(History(mod).Plays);
        Assert.True(play.Alone);
        Assert.Equal(prepared.Content, play.Content);
        Assert.Equal(0, rig.Host.SaveCalls);
    }

    /// <summary>The played mod of a mod of the export game, with its history in the workspace of the profile.</summary>
    private PlayedMod Played(Mod mod, string content) =>
        new(mod.Id, mod.Manifest.Name, mod.Folder, PublishHistory.FolderOf(rig.Game.Game.Folder, mod.Id), content);

    private PublishHistory History(Mod mod) =>
        PublishHistory.Load(PublishHistory.FolderOf(rig.Game.Game.Folder, mod.Id), mod.Id).History;

    /// <summary>Runs the frames until the check after the exit ends, then a few more frames for a record that would
    /// write. The history of the mod has no play.</summary>
    private async Task AssertNoPlayAsync(string modId)
    {
        await rig.RunAsync(() => !rig.Plays.Checking, "the check after the exit");
        for (var i = 0; i < 20; i++)
        {
            rig.Host.Ui.RunQueued();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.DoesNotContain(rig.Console, c => c.StartsWith("Recorded a test play"));
        Assert.False(File.Exists(Path.Combine(PublishHistory.FolderOf(rig.Game.Game.Folder, modId), PublishHistory.FileName)));
    }

    [Fact]
    public async Task TheGameLinkActionsWaitForTheGameAndChangeOnlyThePlayCopy()
    {
        await rig.StartGameAsync();
        var root = rig.Game.Root;
        var before = Stamps(root);

        Assert.NotNull(rig.Plays.CopyBlock(root));
        rig.Plays.UninstallCopy(root);
        Assert.Contains(rig.Plays.CopyBlock(root), rig.Console);
        rig.Starts[0].Exit(0);
        await rig.RunAsync(() => !rig.Plays.Checking, "the check after the exit");
        Assert.Null(rig.Plays.CopyBlock(root));

        rig.Plays.RefreshCopy(root);
        await rig.RunAsync(() => rig.Plays.CopyInfo(root) is { Record: not null }, "the play copy row");
        Assert.Equal(rig.Game.CopyFolder, rig.Plays.CopyInfo(root)!.Folder);
        rig.Plays.CheckCopy(root);
        await rig.RunAsync(() => rig.Console.Any(c => c.EndsWith("has no difference: " +
                                                      $"{rig.Plays.CopyInfo(root)!.Record!.LinkCount} links, " +
                                                      $"{rig.Plays.CopyInfo(root)!.Record!.CopyCount} real files.")),
            "the check of the play copy");
        rig.Plays.UninstallCopy(root);
        await rig.RunAsync(() => rig.Console.Any(c => c.StartsWith("OpenLancer removed the mods from the play copy")),
            "the uninstall");
        Assert.DoesNotContain("price = 5600", File.ReadAllText(rig.Game.CopyPath(ExportGame.GoodsPath)));
        rig.Plays.RemoveCopy(root);
        await rig.RunAsync(() => rig.Console.Any(c => c.StartsWith("OpenLancer removed the play copy")), "the remove");

        Assert.False(Directory.Exists(rig.Game.CopyFolder));
        Assert.Null(rig.Host.Settings.Games[0].PlayCopy);
        Assert.Equal(before, Stamps(root));
        await rig.RunAsync(() => rig.Plays.CopyInfo(root) is { Record: null }, "the row without a play copy");
    }

    [Fact]
    public async Task AFailedRemoveKeepsTheFolderOfThePlayCopy()
    {
        var other = Path.Combine(rig.Game.Game.Folder, "other play");
        rig.Game.Track(other);
        rig.Host.Settings.Games[0] = rig.Host.Settings.Games[0] with { PlayCopy = other };
        await rig.StartGameAsync();
        rig.Starts[0].Exit(0);
        await rig.RunAsync(() => !rig.Plays.Checking, "the check after the exit");
        var root = rig.Game.Root;

        // An open handle without delete sharing stops the delete of one name.
        using (new FileStream(Path.Combine(other, ProgramPath), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            rig.Plays.RemoveCopy(root);
            await rig.RunAsync(() => rig.Console.Any(c => c.StartsWith("OpenLancer could not complete the remove")), "the error");
        }

        Assert.Equal(other, rig.Host.Settings.Games[0].PlayCopy);
        Assert.Equal(other, rig.Plays.CopyFolder(root));
        rig.Plays.RemoveCopy(root);
        await rig.RunAsync(() => rig.Console.Any(c => c.StartsWith("OpenLancer removed the play copy")), "the remove");
        Assert.False(Directory.Exists(other));
        Assert.Null(rig.Host.Settings.Games[0].PlayCopy);
    }

    [Fact]
    public async Task AFolderThatFailsAsksForAnotherFolder()
    {
        rig.Open(rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch));
        // The default folder is in the workspace, so the play copy cannot go there.
        rig.Host.Protected.Add(rig.Game.Game.Folder);

        rig.Plays.Play(rig.Target);
        await rig.RunAsync(() => rig.Host.Asked != null, "the question");

        Assert.StartsWith("OpenLancer cannot make the play copy next to the game.", rig.Host.Asked!.Value.Message);
        Assert.Equal(["Select folder...", "Cancel"], rig.Host.Asked.Value.Choices.Select(c => c.Label));
        Assert.Empty(rig.Starts);
        var other = Path.Combine(rig.Game.Game.Folder, "other play");
        rig.Game.Track(other);
        rig.Host.Protected.Clear();
        rig.Host.Asked.Value.Choices[0].Action();
        rig.Host.Chooser!(other);
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start in the other folder");

        Assert.Equal(Path.Combine(other, ProgramPath), rig.Starts[0].Info.FileName);
        Assert.Equal(other, rig.Host.Settings.Games[0].PlayCopy);
        Assert.Equal(other, rig.Plays.CopyFolder(rig.Game.Root));
    }
}
