using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The save prompt when the user closes the editor window (roadmap decision D78). The host of these tests runs the
/// close prompt on a real edit session, as the editor window does.
/// </summary>
public sealed class ClosePromptTests
{
    private const string ModName = "Act Mod";

    /// <summary>A workspace whose active mod "act" has a patch of the goods and the name <see cref="ModName"/>.</summary>
    private static TestWorkspace Workspace()
    {
        var ws = EditSessionTests.Workspace(new() { [@"patches\" + GoodsPath] = "[Good]\nnickname = g1\nprice = 120\n" });
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), ModManifest.FileName),
            $$"""{ "id": "act", "name": "{{ModName}}", "formatVersion": 1 }""");
        return ws;
    }

    private static void Edit(EditSession session) => session.Execute(Set(Good("g1"), "volume", new Int32Value(3)));

    [Fact]
    public void WithoutUnsavedEditsTheWindowClosesAtOnce()
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act"));

        Assert.True(new ClosePrompt(host).Request());

        Assert.Equal(["end edits", "exit"], host.Calls);
        Assert.Null(host.Dialog);
    }

    [Fact]
    public void UnsavedEditsKeepTheWindowOpenAndShowTheDialog()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);

        Assert.False(new ClosePrompt(host).Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        var dialog = host.Dialog!.Value;
        Assert.Equal(ClosePrompt.Title, dialog.Title);
        Assert.Equal($"Save changes to {ModName} before you close?", dialog.Message);
        // Save is the first choice, so Enter keeps the edits.
        Assert.Equal(["Save", "Don't Save", "Cancel"], dialog.Choices.Select(c => c.Label).ToList());
    }

    /// <summary>A field edit or a drag that is open when the request comes ends first. Its change counts as an unsaved
    /// edit.</summary>
    [Fact]
    public void AnOpenFieldEditEndsBeforeTheCheck()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var host = new Host(session) { OpenEdit = () => Edit(session) };

        Assert.False(new ClosePrompt(host).Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void ASaveThatSucceedsClosesTheWindow()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        Assert.False(new ClosePrompt(host).Request());

        host.Choose("Save");

        Assert.Equal(["end edits", "ask", "save", "exit"], host.Calls);
        Assert.False(session.IsDirty);
        Assert.Contains("volume = 3", File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath)));
    }

    /// <summary>A save that fails keeps the window open. The Console of the editor tells why. The next request asks
    /// again.</summary>
    [Fact]
    public void ASaveThatFailsKeepsTheWindowOpen()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());
        // Something outside the editor changes the patch, so the save stops (rule P7).
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath), "[Good]\r\nnickname = g1\r\nprice = 130\r\n");

        host.Choose("Save");

        Assert.Equal(["end edits", "ask", "save"], host.Calls);
        Assert.True(session.IsDirty);
        Assert.False(prompt.Request());
        Assert.NotNull(host.Dialog);
        Assert.DoesNotContain("exit", host.Calls);
    }

    /// <summary>Don't Save closes the window and deletes the recovery file, so the next open does not offer the
    /// edits. The files of the mod do not change.</summary>
    [Fact]
    public void DontSaveDeletesTheRecoveryFileAndClosesTheWindow()
    {
        using var ws = Workspace();
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        var session = ws.Session("act");
        Edit(session);
        Assert.Empty(session.WriteRecovery());
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        Assert.True(File.Exists(recovery));
        var host = new Host(session);
        Assert.False(new ClosePrompt(host).Request());

        host.Choose("Don't Save");

        Assert.Equal(["end edits", "ask", "discard", "exit"], host.Calls);
        Assert.False(File.Exists(recovery));
        session.Dispose();
        Assert.False(ws.Session("act").HasRecovery);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act"), exclude: SaveTransaction.FolderName));
    }

    /// <summary>Cancel and Escape keep the window open and the edits unsaved. The next request asks again.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelAndEscapeKeepTheWindowOpen(bool escape)
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());

        if (escape)
            host.Escape();
        else
            host.Choose("Cancel");

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.True(session.IsDirty);
        Assert.False(prompt.Request());
        Assert.Equal(["end edits", "ask", "end edits", "ask"], host.Calls);
    }

    /// <summary>A second request while the dialog shows, for example a second click on the close button, changes
    /// nothing.</summary>
    [Fact]
    public void ASecondRequestWhileTheDialogShowsDoesNothing()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session);
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());
        var dialog = host.Dialog;

        Assert.False(prompt.Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.Equal(dialog, host.Dialog);
    }

    /// <summary>A publish draft of the Mods window asks first (section 10.7 of the M11c design). After Apply, the
    /// question about the unsaved edits follows.</summary>
    [Fact]
    public void ADraftAsksBeforeTheUnsavedEdits()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        var host = new Host(session) { UnappliedDraft = "Starrunner" };
        var prompt = new ClosePrompt(host);

        Assert.False(prompt.Request());

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.Equal(PublishMessages.DraftQuestion("Starrunner"), host.Dialog!.Value.Message);
        Assert.Equal(["Apply", "Revert", "Cancel"], host.Dialog.Value.Choices.Select(c => c.Label).ToList());
        host.Choose("Apply");
        Assert.Equal(["end edits", "ask", "apply draft", "ask"], host.Calls);
        Assert.Equal($"Save changes to {ModName} before you close?", host.Dialog!.Value.Message);
        host.Choose("Don't Save");
        Assert.Equal(["end edits", "ask", "apply draft", "ask", "discard", "exit"], host.Calls);
    }

    /// <summary>Revert drops the draft. Without unsaved edits, the window then closes.</summary>
    [Fact]
    public void RevertOfTheDraftClosesTheWindowWithoutUnsavedEdits()
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act")) { UnappliedDraft = "Starrunner" };

        Assert.False(new ClosePrompt(host).Request());
        host.Choose("Revert");

        Assert.Equal(["end edits", "ask", "revert draft", "exit"], host.Calls);
    }

    /// <summary>Cancel and Escape keep the window open and the draft. The next request asks again.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelOfTheDraftQuestionKeepsTheWindowOpen(bool escape)
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act")) { UnappliedDraft = "Starrunner" };
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());

        if (escape)
            host.Escape();
        else
            host.Choose("Cancel");

        Assert.Equal(["end edits", "ask"], host.Calls);
        Assert.Equal("Starrunner", host.UnappliedDraft);
        Assert.False(prompt.Request());
        Assert.Equal(["end edits", "ask", "end edits", "ask"], host.Calls);
    }

    /// <summary>An Apply that stops, for example after a refusal of the write guard, keeps the window open.</summary>
    [Fact]
    public void AnApplyThatStopsKeepsTheWindowOpen()
    {
        using var ws = Workspace();
        var host = new Host(ws.Session("act")) { UnappliedDraft = "Starrunner", ApplyWrites = false };
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());

        host.Choose("Apply");

        Assert.Equal(["end edits", "ask", "apply draft"], host.Calls);
        Assert.False(prompt.Request());
        Assert.DoesNotContain("exit", host.Calls);
    }

    /// <summary>
    /// Finding 9 of the S1 review: the user closes the window while the game data loads. The close waits for the end of
    /// the load. Until then, a dialog that the user cannot cancel blocks the window, so no edit comes after the answer.
    /// A second close changes nothing. The window ends one time, when the load ends.
    /// </summary>
    [Fact]
    public async Task ACloseDuringALoadBlocksTheWindowUntilTheLoadEnds()
    {
        using var ws = Workspace();
        using var load = new RunningLoad();
        var host = new Host(ws.Session("act")) { Loads = load.Loads };
        var prompt = new ClosePrompt(host);

        Assert.False(prompt.Request());

        Assert.Equal(["end edits", "wait"], host.Calls);
        Assert.True(prompt.Waits);
        var wait = host.Waiting!.Value;
        Assert.Equal((ClosePrompt.Title, ClosePrompt.WaitMessage), (wait.Title, wait.Message));
        Assert.Equal([ClosePrompt.CloseNow], wait.Choices.Select(c => c.Label).ToList());
        Assert.Equal(ClosePrompt.CloseNowTooltip, wait.Choices[0].Tooltip);
        Assert.False(prompt.Request());
        Assert.Equal(["end edits", "wait"], host.Calls);

        await load.EndAsync();

        Assert.Equal(["end edits", "wait", "exit"], host.Calls);
        Assert.False(prompt.Waits);
    }

    /// <summary>
    /// Finding 3 of the S3 review: after Don't Save during a load, the close waits. A second close while it waits does
    /// not ask again, and the dialog of the wait cannot be cancelled. So no edit can come after Don't Save, and the
    /// recovery file stays deleted.
    /// </summary>
    [Fact]
    public async Task DontSaveDuringALoadDeletesTheRecoveryFileAndWaits()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        Assert.Empty(session.WriteRecovery());
        var recovery = RecoveryFile.PathOf(ws.ModFolder("act"));
        using var load = new RunningLoad();
        var host = new Host(session) { Loads = load.Loads };
        var prompt = new ClosePrompt(host);
        Assert.False(prompt.Request());

        host.Choose("Don't Save");
        Assert.False(prompt.Request());

        Assert.Equal(["end edits", "ask", "discard", "wait"], host.Calls);
        Assert.Null(host.Dialog);
        Assert.False(File.Exists(recovery));
        await load.EndAsync();
        Assert.Equal(["end edits", "ask", "discard", "wait", "exit"], host.Calls);
    }

    /// <summary>Save in the dialog during a load saves the files, and the window ends when the load ends.</summary>
    [Fact]
    public async Task SaveDuringALoadClosesTheWindowWhenTheLoadEnds()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Edit(session);
        using var load = new RunningLoad();
        var host = new Host(session) { Loads = load.Loads };
        Assert.False(new ClosePrompt(host).Request());

        host.Choose("Save");

        Assert.Equal(["end edits", "ask", "save", "wait"], host.Calls);
        Assert.False(session.IsDirty);
        await load.EndAsync();
        Assert.Equal(["end edits", "ask", "save", "wait", "exit"], host.Calls);
    }

    /// <summary>Finding 4 of the S3 review: a load that does not end must not keep the window open until a kill. Close
    /// now ends the window at once.</summary>
    [Fact]
    public void CloseNowEndsTheWindowWithoutAWaitForTheLoad()
    {
        using var ws = Workspace();
        using var load = new RunningLoad();
        var host = new Host(ws.Session("act")) { Loads = load.Loads };
        Assert.False(new ClosePrompt(host).Request());

        host.ChooseInWait(ClosePrompt.CloseNow);

        Assert.Equal(["end edits", "wait", "exit"], host.Calls);
        Assert.True(load.Loads.IsBusy);
    }

    /// <summary>Without a running load, the window closes at once, also with a session.</summary>
    [Fact]
    public async Task WithoutARunningLoadTheWindowClosesAtOnce()
    {
        using var ws = Workspace();
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        loads.Load(() => () => { });
        await ui.RunUntilAsync(() => !loads.IsBusy, "the first load");
        var host = new Host(ws.Session("act")) { Loads = loads };

        Assert.True(new ClosePrompt(host).Request());

        Assert.Equal(["end edits", "exit"], host.Calls);
        Assert.True(loads.IsClosed);
    }

    /// <summary>A first load of the game data that runs until the test ends it, as the loads of the editor run.</summary>
    private sealed class RunningLoad : IDisposable
    {
        private readonly QueuedUIThread ui = new();
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RunningLoad()
        {
            Loads = new DataLoads(ui);
            using var running = new ManualResetEventSlim();
            Loads.Load(() =>
            {
                running.Set();
                Assert.True(release.Task.Wait(TimeSpan.FromSeconds(30)), "The test did not end the load.");
                return () => { };
            });
            Assert.True(running.Wait(TimeSpan.FromSeconds(30)));
        }

        public DataLoads Loads { get; }

        /// <summary>Ends the load, and runs its end on the UI thread, as the next frame of the editor does.</summary>
        public async Task EndAsync()
        {
            release.TrySetResult();
            await ui.RunUntilAsync(() => !Loads.IsBusy, "the end of the load");
        }

        /// <summary>Ends a load that the test did not end.</summary>
        public void Dispose() => release.TrySetResult();
    }

    /// <summary>The editor window as the close prompt sees it, on a real edit session. The dialog is a record of its
    /// last call.</summary>
    private sealed class Host(EditSession session) : IClosePromptHost
    {
        public List<string> Calls { get; } = [];

        public DataLoads? Loads { get; init; }

        /// <summary>The display name of the mod of a publish draft with changes, or null.</summary>
        public string? UnappliedDraft { get; set; }

        /// <summary>False for an Apply that stops, for example after a refusal.</summary>
        public bool ApplyWrites { get; init; } = true;

        public void ApplyDraft(Action applied)
        {
            Calls.Add("apply draft");
            if (!ApplyWrites)
                return;
            UnappliedDraft = null;
            applied();
        }

        public void RevertDraft()
        {
            Calls.Add("revert draft");
            UnappliedDraft = null;
        }

        /// <summary>The open field edit or drag: its end makes the edit.</summary>
        public Action? OpenEdit { get; init; }

        private bool editOpen = true;

        /// <summary>The dialog that shows, or null.</summary>
        public (string Title, string Message, IReadOnlyList<Choice> Choices, Action Cancel)? Dialog { get; private set; }

        public string? UnsavedMod => session.IsDirty ? session.ActiveMod!.Manifest.Name : null;

        public void EndEdits()
        {
            Calls.Add("end edits");
            if (editOpen)
                OpenEdit?.Invoke();
            editOpen = false;
        }

        public void Save(Action saved)
        {
            Calls.Add("save");
            if (session.Save().Saved)
                saved();
        }

        public void DiscardRecovery()
        {
            Calls.Add("discard");
            session.DiscardRecovery();
        }

        public void Exit() => Calls.Add("exit");

        public void Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel)
        {
            Calls.Add("ask");
            Dialog = (title, message, choices, cancel);
        }

        /// <summary>The dialog that the user cannot cancel, or null.</summary>
        public (string Title, string Message, IReadOnlyList<Choice> Choices)? Waiting { get; private set; }

        public void Wait(string title, string message, IReadOnlyList<Choice> choices)
        {
            Calls.Add("wait");
            Waiting = (title, message, choices);
        }

        /// <summary>Clicks a button of the dialog that the user cannot cancel.</summary>
        public void ChooseInWait(string label)
        {
            var choice = Waiting!.Value.Choices.Single(c => c.Label == label);
            Waiting = null;
            choice.Action();
        }

        /// <summary>Clicks a button of the dialog. As in <see cref="ChoiceDialog"/>, the dialog closes first.</summary>
        public void Choose(string label)
        {
            var choice = Dialog!.Value.Choices.Single(c => c.Label == label);
            Dialog = null;
            choice.Action();
        }

        /// <summary>Presses Escape: the dialog closes and runs its cancel action.</summary>
        public void Escape()
        {
            var cancel = Dialog!.Value.Cancel;
            Dialog = null;
            cancel();
        }
    }
}
