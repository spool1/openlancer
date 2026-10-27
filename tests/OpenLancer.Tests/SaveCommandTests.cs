using System;
using System.Collections.Generic;
using System.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.FieldBindingTests;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The save of Ctrl+S and File > Save without the GPU (see <see cref="SaveCommand"/>). The host runs the command on a
/// real edit session and on the field edits of the Inspector, as the editor window does.
/// </summary>
public sealed class SaveCommandTests
{
    // The ImGui id of the text field.
    private const uint Field = 21;

    private static readonly FieldEvents Activated = new(Activated: true, Active: true, Changed: false,
        Deactivated: false, Cancelled: false);

    private static readonly FieldEvents Typed = new(Activated: false, Active: true, Changed: true,
        Deactivated: false, Cancelled: false);

    private static string GoodsPatch(TestWorkspace ws) => Path.Combine(ws.ModFolder("act"), "patches", GoodsPath);

    /// <summary>Opens a field edit of the price of the good eng1, and types "75", as a user in the Inspector does.</summary>
    private static FieldEditRouter TypePrice(EditSession session)
    {
        var router = new FieldEditRouter(_ => { });
        var price = Bind(session, Good(GoodsPath, "eng1"), "price");
        router.Route(Field, price.FieldKey, Activated, () => TestGestures.Line(session, price), _ => "50", 1);
        router.Route(Field, price.FieldKey, Typed, () => TestGestures.Line(session, price), _ => "75", 2);
        return router;
    }

    /// <summary>
    /// Ctrl+S while the user types in a field of the Inspector: the field edit ends and keeps its text, then the save
    /// writes it. The open transaction of the edit stops a save, so the end must come first.
    /// </summary>
    [Fact]
    public void CtrlSInATextFieldEndsTheFieldEditAndSavesItsText()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var router = TypePrice(session);
        Assert.Throws<InvalidOperationException>(() => session.Save());
        var host = new Host(session, router);

        Assert.True(SaveCommand.Run(host));

        Assert.Equal(["end edits", "save"], host.Calls);
        Assert.Null(router.Open);
        Assert.False(session.IsDirty);
        Assert.Contains("price = 75", File.ReadAllText(GoodsPatch(ws)));
        Assert.Equal("Saved mod 'act' (save 1): 1 file written, 0 files deleted.", host.Summary);
    }

    /// <summary>A field whose edit runs when it ends, for example a name, commits its last text at Ctrl+S. The save
    /// follows at once, so the end asks no question, as at the close of the window.</summary>
    [Fact]
    public void CtrlSCommitsTheLastTextOfACommitFieldWithoutAQuestion()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        var name = Bind(session, Gun("gun1"), "ids_name");
        var asked = new List<string>();
        var commit = EditController.TextKeyCommit(session, name, make => FakeCommandHost.Runner(session).Run(make),
            (_, message, _) => asked.Add(message));
        FieldGesture Begin() => new CommitGesture(name.FieldKey, "", commit);
        router.Route(Field, name.FieldKey, Activated, Begin, _ => "", 1);
        router.Route(Field, name.FieldKey, Typed, Begin, _ => "Ion Gun", 2);
        var host = new Host(session, router);

        Assert.True(SaveCommand.Run(host));

        Assert.Equal(["end edits", "save"], host.Calls);
        Assert.Empty(asked);
        Assert.False(session.IsDirty);
        Assert.Equal("Ion Gun", session.Text(new StringPlaceholder(StringKind.String, "gun1")));
        Assert.StartsWith("Saved mod 'act' (save 1): ", host.Summary);
    }

    /// <summary>A dialog has the keyboard, so Ctrl+S does nothing. The field edit stays open, and nothing saves.</summary>
    [Fact]
    public void ADialogStopsCtrlS()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var router = TypePrice(session);
        var host = new Host(session, router) { DialogOpen = true };

        Assert.False(SaveCommand.Run(host));

        Assert.Empty(host.Calls);
        Assert.NotNull(router.Open);
        Assert.Null(host.Summary);
        Assert.False(File.Exists(GoodsPatch(ws)));
    }

    /// <summary>A text field without a field edit, for example the search field of the Hierarchy, keeps the keyboard.
    /// Ctrl+S saves at once.</summary>
    [Fact]
    public void ATextFieldWithoutAFieldEditKeepsTheKeyboard()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(FieldCommands.SetLinesFromText(session, Good(GoodsPath, "eng1"), "price", "75").Command!);
        var host = new Host(session, new FieldEditRouter(_ => { }));

        Assert.True(SaveCommand.Run(host));

        Assert.Equal(["save"], host.Calls);
        Assert.Equal("Saved mod 'act' (save 1): 1 file written, 0 files deleted.", host.Summary);
    }

    /// <summary>A second Ctrl+S has no unsaved edits, and the status bar tells so.</summary>
    [Fact]
    public void ASecondCtrlSTellsThatThereAreNoUnsavedEdits()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var host = new Host(session, TypePrice(session));
        SaveCommand.Run(host);

        Assert.True(SaveCommand.Run(host));

        Assert.Equal(["end edits", "save", "save"], host.Calls);
        Assert.Equal("Mod 'act' has no unsaved edits. No file changed.", host.Summary);
    }

    /// <summary>The editor window as the save command sees it, on a real edit session and the field edits of the
    /// Inspector. The save writes the summary as the editor writes it to the status bar.</summary>
    private sealed class Host(EditSession session, FieldEditRouter router) : ISaveCommandHost
    {
        private int saves;

        public List<string> Calls { get; } = [];

        /// <summary>The summary of the last save, or null.</summary>
        public string? Summary { get; private set; }

        public bool DialogOpen { get; init; }

        public bool EditOpen => router.Open != null;

        public void EndEdits()
        {
            Calls.Add("end edits");
            router.EndAtClose();
        }

        public void Save()
        {
            Calls.Add("save");
            var hadEdits = session.IsDirty;
            var result = session.Save();
            var number = result.Saved && result.Written.Count + result.Deleted.Count > 0 ? ++saves : 0;
            Summary = SaveReport.Summary(result, session.ActiveMod!.Id, number, hadEdits).Message;
        }
    }
}
