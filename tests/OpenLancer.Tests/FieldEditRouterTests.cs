using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.FieldBindingTests;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The field edits of the text fields without the GPU. Each test replays, frame by frame, the events that ImGui gives
/// for the widgets. The Inspector rows also keep their places while an edit is open.
/// </summary>
public sealed class FieldEditRouterTests
{
    // The ImGui ids of two widgets.
    private const uint X = 11;
    private const uint Y = 12;
    private const uint Z = 13;

    // The ImGui id of the drag button of a label.
    private const uint Label = 14;

    private static readonly FieldEvents Activated = new(Activated: true, Active: true, Changed: false,
        Deactivated: false, Cancelled: false);

    private static readonly FieldEvents Active = new(Activated: false, Active: true, Changed: false,
        Deactivated: false, Cancelled: false);

    private static readonly FieldEvents Typed = new(Activated: false, Active: true, Changed: true,
        Deactivated: false, Cancelled: false);

    private static readonly FieldEvents Ended = new(Activated: false, Active: false, Changed: false,
        Deactivated: true, Cancelled: false);

    // Escape puts back the text of the widget, so the text changes in the frame in which the widget ends.
    private static readonly FieldEvents Escaped = new(Activated: false, Active: false, Changed: true,
        Deactivated: true, Cancelled: true);

    // A key and a click outside the field in one frame: ImGui changes the text, then ends the field.
    private static readonly FieldEvents TypedAndEnded = new(Activated: false, Active: false, Changed: true,
        Deactivated: true, Cancelled: false);

    [Fact]
    public void EachAxisOfAVectorKeepsItsTextWhenTheUserMovesDirectlyToTheNextAxis()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(FieldCommands.SetLinesFromText(session, Ship("s1"), "steering_torque", "1, 2, 3").Command!);
        var warnings = new List<string>();
        var router = new FieldEditRouter(warnings.Add);
        FieldBinding Torque() => Bind(session, Ship("s1"), "steering_torque");
        void Axis(uint id, int index, FieldEvents events, string value, int frame) =>
            Route(router, session, id, Torque(), events, start => FieldEdit.WithValue(start, index, value), frame);

        // Frame 1: the user clicks X. Frame 2: the user types 5.
        Axis(X, 0, Activated, "1", 1);
        Axis(X, 0, Typed, "5", 2);
        // Frame 3: X draws while it is active. Then the user clicks Y, and Y becomes active.
        Axis(X, 0, Active, "5", 3);
        Axis(Y, 1, Activated, "2", 3);
        // Frame 4: ImGui tells the end of X one frame late. The user types 7 in Y.
        Axis(X, 0, Ended, "5", 4);
        Axis(Y, 1, Typed, "7", 4);
        Assert.Equal("5, 7, 3", Torque().Text);
        // Frame 5: Y ends.
        Axis(Y, 1, Ended, "7", 5);

        Assert.Null(router.Open);
        Assert.Equal("5, 7, 3", Torque().Text);
        Assert.Empty(warnings);
        // Each axis is one entry of the history.
        session.Undo();
        Assert.Equal("5, 2, 3", Torque().Text);
    }

    [Fact]
    public void EscapeCancelsTheEditAndTheOtherEndsKeepIt()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        FieldBinding Mass() => Bind(session, Ship("s1"), "mass");
        void Field(FieldEvents events, string text, int frame) =>
            Route(router, session, X, Mass(), events, _ => text, frame);

        Field(Activated, "120", 1);
        Field(Typed, "130", 2);
        Assert.Equal("130", Mass().Text);
        Field(Escaped, "120", 3);

        Assert.Null(router.Open);
        Assert.Equal("120", Mass().Text);
        Assert.False(session.CanUndo);

        // Enter, Tab or a click outside the field.
        Field(Activated, "120", 4);
        Field(Typed, "140", 5);
        Field(Ended, "140", 6);

        Assert.Equal("140", Mass().Text);
        Assert.Equal("Set mass", session.UndoLabel);
    }

    [Fact]
    public void AnEditWhoseWidgetDoesNotDrawEndsOneFrameLaterAndKeepsItsText()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        Route(router, session, X, Bind(session, Ship("s1"), "mass"), Activated, _ => "", 1);
        Route(router, session, X, Bind(session, Ship("s1"), "mass"), Typed, _ => "150", 2);

        router.EndLost(3);
        Assert.NotNull(router.Open);
        router.EndLost(4);

        Assert.Null(router.Open);
        Assert.Equal("150", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("Set mass", session.UndoLabel);
    }

    [Fact]
    public void AnEditWithTextThatIsNotValidEndsWithAWarningWhenTheNextFieldStarts()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var warnings = new List<string>();
        var router = new FieldEditRouter(warnings.Add);
        Route(router, session, X, Bind(session, Ship("s1"), "mass"), Activated, _ => "", 1);
        Route(router, session, X, Bind(session, Ship("s1"), "mass"), Typed, _ => "13x", 2);
        Assert.Contains("is not a number", router.ErrorOf(Bind(session, Ship("s1"), "mass").FieldKey));
        Assert.Null(router.ErrorOf(Bind(session, Ship("s1"), "hold_size").FieldKey));

        Route(router, session, Y, Bind(session, Ship("s1"), "hold_size"), Activated, _ => "", 3);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("'mass' did not change:", warning);
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("hold_size", router.Open!.Key.Name);
    }

    [Fact]
    public void TheWidgetOfAnEditDoesNotChangeAnotherKey()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        Route(router, session, X, Bind(session, Ship("s1"), "mass"), Activated, _ => "", 1);

        // The rows moved, and the active widget now draws the row of another key.
        Route(router, session, X, Bind(session, Ship("s1"), "hold_size"), Typed, _ => "9", 2);

        // The text of that widget goes to neither key.
        Assert.Empty(Bind(session, Ship("s1"), "hold_size").Lines);
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("mass", router.Open!.Key.Name);
    }

    [Fact]
    public void TheKeyListsOfASectionKeepTheKeyOfAnOpenEditInItsPlace()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var ship = Ship("s1");
        session.Execute(EditCommand.SetLinesFromText(ship, "custom_key", ["5"]));
        var before = SchemaPage.Layout(session, ship, null);
        Assert.Contains("hold_size", before.Unset);
        Assert.Contains("mass", before.Set);
        Assert.Equal(["custom_key"], before.Unknown);

        // An unset key gets a value. The first character adds the key.
        using (var gesture = TestGestures.Line(session, Bind(session, ship, "hold_size")))
        {
            gesture.Change("1");

            Assert.Equal(Lists(before), Lists(SchemaPage.Layout(session, ship, gesture)));
            Assert.Contains("hold_size", SchemaPage.Layout(session, ship, null).Set);
            // The lists of another section do not wait for the edit.
            var good = Good(GoodsPath, "s1_hull");
            Assert.Equal(Lists(SchemaPage.Layout(session, good, null)), Lists(SchemaPage.Layout(session, good, gesture)));
            Assert.Null(gesture.End(keep: true, interactive: true));
        }

        // A set key and a key that the engine does not read become empty before the user types the new value.
        foreach (var name in (string[])["mass", "custom_key"])
        {
            var start = SchemaPage.Layout(session, ship, null);
            using var gesture = TestGestures.Line(session, Bind(session, ship, name));
            gesture.Change("");
            Assert.Empty(session.Get(ship)!.Lines(name));

            Assert.Equal(Lists(start), Lists(SchemaPage.Layout(session, ship, gesture)));
            var after = SchemaPage.Layout(session, ship, null);
            Assert.DoesNotContain(name, after.Set);
            Assert.DoesNotContain(name, after.Unknown);
        }
    }

    [Fact]
    public void AKeyThatTheOpenEditClearsStaysBoundUntilTheEditEnds()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var ship = Ship("s1");
        session.Execute(EditCommand.SetLinesFromText(ship, "custom_key", ["5"]));
        using var gesture = TestGestures.Line(session, Bind(session, ship, "custom_key"));

        gesture.Change("");

        var view = session.Get(ship)!;
        Assert.NotNull(new PropertyGrid.BoundSection(session, ship, view, view, "custom_key").Field("custom_key"));
        // Without the edit, a key that neither the schema nor the section has is not bound.
        Assert.Null(new PropertyGrid.BoundSection(session, ship, view, view, null).Field("custom_key"));
    }

    /// <summary>
    /// A text key changes the text of the active mod when its field ends (step 0 A). While the field is active, the
    /// edit changes nothing, but it is open, so the history, the save and the recovery file wait. The window close ends
    /// the field without a question: the gun alone gets the new text ("Only this one"), and the recovery file keeps
    /// it.
    /// </summary>
    [Fact]
    public void AWindowCloseCommitsTheLastTextOfATextKeyWithoutTheSharedTextDialog()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        var name = Bind(session, Gun("gun1"), "ids_name");
        var asked = new List<string>();
        var commit = TextKeyCommit(session, name, (_, message, _) => asked.Add(message));
        void Field(FieldEvents events, string text, int frame) =>
            Commit(router, X, name.FieldKey, events, "", text, commit, frame);

        Field(Activated, "", 1);
        Field(Typed, "Ion", 2);
        Field(Typed, "Ion Gun", 3);
        Assert.NotNull(router.Open);
        Assert.Equal(["300"], Firsts(session, Gun("gun1"), "ids_name"));

        // EditorWindow.Cleanup ends the field (PropertyGrid.EndEdit).
        router.EndAtClose();

        Assert.Null(router.Open);
        Assert.Empty(asked);
        Assert.Equal(["$str:gun1"], Firsts(session, Gun("gun1"), "ids_name"));
        Assert.Equal("Ion Gun", session.Text(new StringPlaceholder(StringKind.String, "gun1")));
        Assert.Equal(["300"], Firsts(session, Good(WeaponGoodsPath, "gun1"), "ids_name"));
        Assert.Empty(session.WriteRecovery());
        Assert.Contains("Ion Gun", File.ReadAllText(RecoveryFile.PathOf(ws.ModFolder("act"))));
    }

    /// <summary>
    /// The "same text" dialog shows only for an end by the user. The gun and its good show one text. "All of them"
    /// gives both the new text, and "Only this one" gives it to the gun alone. An empty text changes nothing.
    /// </summary>
    [Fact]
    public void TheSharedTextDialogShowsOnlyForAnInteractiveEnd()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var name = Bind(session, Gun("gun1"), "ids_name");
        var warnings = new List<string>();
        var router = new FieldEditRouter(warnings.Add);
        var dialogs = new List<(string Title, string Message, IReadOnlyList<Choice> Choices)>();
        var commit = TextKeyCommit(session, name, (title, message, choices) => dialogs.Add((title, message, choices)));
        void Field(FieldEvents events, string text, int frame) =>
            Commit(router, X, name.FieldKey, events, "", text, commit, frame);

        // Enter, Tab or a click outside the field.
        Field(Activated, "", 1);
        Field(Typed, "Ion Gun", 2);
        Field(Ended, "Ion Gun", 3);

        var (title, message, choices) = Assert.Single(dialogs);
        Assert.Equal("The same text", title);
        Assert.Equal("These sections of the item show the same text as [Gun] nickname = gun1: " +
                     "[Good] nickname = gun1. Give them the new text too?", message);
        Assert.Equal(["All of them", "Only this one", "Cancel"], choices.Select(c => c.Label));
        Assert.Equal(["300"], Firsts(session, Gun("gun1"), "ids_name"));
        choices[0].Action();
        Assert.Equal(["$str:gun1"], Firsts(session, Gun("gun1"), "ids_name"));
        Assert.Equal(["$str:gun1"], Firsts(session, Good(WeaponGoodsPath, "gun1"), "ids_name"));
        session.Undo();
        choices[1].Action();
        Assert.Equal(["$str:gun1"], Firsts(session, Gun("gun1"), "ids_name"));
        Assert.Equal(["300"], Firsts(session, Good(WeaponGoodsPath, "gun1"), "ids_name"));
        Assert.Empty(warnings);

        Field(Activated, "", 4);
        Field(Typed, " ", 5);
        Field(Ended, " ", 6);

        Assert.Single(dialogs);
        Assert.Equal("'ids_name' did not change: A text cannot be empty. To remove the line, use the INI rows of the " +
                     "section.", Assert.Single(warnings));
        Assert.Equal("Ion Gun", session.Text(new StringPlaceholder(StringKind.String, "gun1")));
    }

    [Fact]
    public void EscapeDiscardsTheTextOfACommitField()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        var commits = new List<(string Text, bool Interactive)>();
        var key = Bind(session, Gun("gun1"), "ids_name").FieldKey;
        void Field(FieldEvents events, string text, int frame) =>
            Commit(router, X, key, events, "Gun", text, Record(commits), frame);

        Field(Activated, "Gun", 1);
        Field(Typed, "Ion Gun", 2);
        Field(Escaped, "Gun", 3);

        Assert.Null(router.Open);
        Assert.Empty(commits);
        // A later end has nothing to commit.
        router.EndAtClose();
        Assert.Empty(commits);
    }

    /// <summary>A commit field whose widget does not draw, for example after a change of the selection, commits its
    /// last text one frame later, one time. A field that ends with its start text commits nothing.</summary>
    [Fact]
    public void ALostCommitFieldCommitsItsLastTextOneTime()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        var commits = new List<(string Text, bool Interactive)>();
        var key = Bind(session, Gun("gun1"), "ids_name").FieldKey;
        void Field(FieldEvents events, string text, int frame) =>
            Commit(router, X, key, events, "Gun", text, Record(commits), frame);

        Field(Activated, "Gun", 1);
        Field(Typed, "Ion Gun", 2);
        router.EndLost(3);
        Assert.NotNull(router.Open);
        router.EndLost(4);
        router.EndLost(5);
        router.EndAtClose();

        Assert.Equal([("Ion Gun", true)], commits);

        Field(Activated, "Gun", 6);
        Field(Typed, "Gu", 7);
        Field(Typed, "Gun", 8);
        Field(Ended, "Gun", 9);
        Assert.Single(commits);
    }

    /// <summary>A commit gesture runs its commit action one time at most, also when it ends again or is
    /// disposed.</summary>
    [Fact]
    public void ACommitGestureCommitsOneTime()
    {
        var commits = new List<(string Text, bool Interactive)>();
        var key = new FieldKey(Gun("gun1"), "ids_name", FieldKey.AllLines);
        using var gesture = new CommitGesture(key, "Gun", Record(commits));

        gesture.Change("Ion Gun");
        Assert.Null(gesture.End(keep: true, interactive: true));
        gesture.Change("Other");
        gesture.End(keep: true, interactive: false);
        gesture.Dispose();

        Assert.Equal([("Ion Gun", true)], commits);
        Assert.Null(gesture.Error);
    }

    /// <summary>
    /// ImGui can change the text in the frame in which the field ends, for example when a key and a click outside the
    /// field come in one frame. The end keeps that text, for a field of lines and for a commit field.
    /// </summary>
    [Fact]
    public void TheEndOfAFieldKeepsTheChangeOfItsLastFrame()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        FieldBinding Mass() => Bind(session, Ship("s1"), "mass");
        void Field(FieldEvents events, string text, int frame) =>
            Route(router, session, X, Mass(), events, _ => text, frame);

        Field(Activated, "120", 1);
        Field(Typed, "13", 2);
        Field(TypedAndEnded, "135", 3);

        Assert.Null(router.Open);
        Assert.Equal("135", Mass().Text);
        session.Undo();
        Assert.Equal("120", Mass().Text);

        var commits = new List<(string Text, bool Interactive)>();
        var name = new FieldKey(Ship("s1"), "ids_name", FieldKey.AllLines);
        Commit(router, Y, name, Activated, "Ship", "Ship", Record(commits), 4);
        Commit(router, Y, name, Typed, "Ship", "Ship ", Record(commits), 5);
        Commit(router, Y, name, TypedAndEnded, "Ship", "Ship 1", Record(commits), 6);

        Assert.Null(router.Open);
        Assert.Equal([("Ship 1", true)], commits);
    }

    /// <summary>Escape puts back the start text in the frame in which the field ends. That change is not a step of the
    /// edit, so the edit view goes back to its start state in one change.</summary>
    [Fact]
    public void EscapePutsBackTheEditViewInOneChange()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var router = new FieldEditRouter(_ => { });
        FieldBinding Mass() => Bind(session, Ship("s1"), "mass");
        void Field(FieldEvents events, string text, int frame) =>
            Route(router, session, X, Mass(), events, _ => text, frame);
        Field(Activated, "120", 1);
        Field(Typed, "130", 2);
        var changes = 0;
        session.Changed += _ => changes++;

        Field(Escaped, "120", 3);

        Assert.Equal(1, changes);
        Assert.Null(router.Open);
        Assert.Equal("120", Mass().Text);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// Each market line is its own key, so the widget of a field of one line cannot change another line of the same
    /// section. The window close commits the last text of the field.
    /// </summary>
    [Fact]
    public void TheFieldOfAMarketLineCommitsOnlyItsOwnLine()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(MarketEdits.AddToBase(session, "eng1", "b1", MarketEdits.DefaultValues("equipment")));
        session.Execute(MarketEdits.AddToBase(session, "s1_hull", "b1", MarketEdits.DefaultValues("equipment")));
        var router = new FieldEditRouter(_ => { });
        var maximum = MarketEdits.Fields.Single(f => f.Index == MarketLine.MaximumIndex);
        MarketLine Line(string good) => session.MarketLines(good).Single(l => l.Base == "b1");
        void Field(MarketLine line, FieldEvents events, string text, int frame) =>
            Commit(router, X, MarketEdits.LineKey(line), events, MarketEdits.FieldText(line, maximum), text,
                (value, _) =>
                {
                    session.Execute(MarketEdits.SetValue(session, line, maximum, value));
                    return null;
                }, frame);
        Assert.Equal(Line("eng1").Section, Line("s1_hull").Section);
        Assert.NotEqual(MarketEdits.LineKey(Line("eng1")), MarketEdits.LineKey(Line("s1_hull")));

        Field(Line("eng1"), Activated, "10", 1);
        Field(Line("eng1"), Typed, "20", 2);
        // The rows moved, and the active widget now draws the other line.
        Field(Line("s1_hull"), Typed, "99", 3);
        router.EndAtClose();

        Assert.Equal("0, -1, 10, 20, 0, 1", FieldEdit.FormatLine(Line("eng1").Values.Skip(1).ToList()));
        Assert.Equal("0, -1, 10, 10, 0, 1", FieldEdit.FormatLine(Line("s1_hull").Values.Skip(1).ToList()));
    }

    /// <summary>
    /// A label drag and the text fields of a vector edit one key. A press on the label ends the text edit of the row
    /// and keeps its text, and the drag starts from that line. A text edit after the drag starts from the line of the
    /// drag. Each edit is one entry of the history (section 6.3 of the M20 design).
    /// </summary>
    [Fact]
    public void ALabelDragAndTheTextFieldsOfAVectorShareTheirKey()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(FieldCommands.SetLinesFromText(session, Ship("s1"), "steering_torque", "1, 2, 3").Command!);
        var warnings = new List<string>();
        var router = new FieldEditRouter(warnings.Add);
        FieldBinding Torque() => Bind(session, Ship("s1"), "steering_torque");

        Route(router, session, X, Torque(), Activated, start => FieldEdit.WithValue(start, 0, "1"), 1);
        Route(router, session, X, Torque(), Typed, start => FieldEdit.WithValue(start, 0, "5"), 2);
        // Frame 3: the drag of the Y label passes the threshold. ImGui tells the end of the X field one frame late.
        Drag(router, session, Label, Torque(), Activated, 1, 0, 3);
        Route(router, session, X, Torque(), Ended, start => FieldEdit.WithValue(start, 0, "5"), 4);
        Assert.True(router.IsOpenFor(Label));
        Assert.False(router.IsOpenFor(X));
        Assert.Equal("5, 2, 3", router.Open!.StartText);
        // 10 points with the step of the line: 0.01 for the largest value 5.
        Drag(router, session, Label, Torque(), Typed, 1, 10, 4);
        Assert.Equal("5, 2.1, 3", Torque().Text);
        Drag(router, session, Label, Torque(), Ended, 1, 10, 5);
        Assert.False(router.IsOpenFor(Label));

        Route(router, session, Z, Torque(), Activated, start => FieldEdit.WithValue(start, 2, "3"), 6);
        Route(router, session, Z, Torque(), TypedAndEnded, start => FieldEdit.WithValue(start, 2, "7"), 7);

        Assert.Empty(warnings);
        Assert.Equal("5, 2.1, 7", Torque().Text);
        session.Undo();
        Assert.Equal("5, 2.1, 3", Torque().Text);
        session.Undo();
        Assert.Equal("5, 2, 3", Torque().Text);
    }

    /// <summary>
    /// The end of a text edit with an error puts back the line before it. A drag that starts in that frame reads its
    /// start from the edit view after that end, not from the line that the row showed (finding R4 of the M20 design).
    /// </summary>
    [Fact]
    public void ADragAfterATextWithAnErrorStartsFromTheEditView()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(FieldCommands.SetLinesFromText(session, Ship("s1"), "steering_torque", "1, 2, 3").Command!);
        var warnings = new List<string>();
        var router = new FieldEditRouter(warnings.Add);
        FieldBinding Torque() => Bind(session, Ship("s1"), "steering_torque");
        Route(router, session, X, Torque(), Activated, start => FieldEdit.WithValue(start, 0, "1"), 1);
        Route(router, session, X, Torque(), Typed, start => FieldEdit.WithValue(start, 0, "4"), 2);
        Route(router, session, X, Torque(), Typed, start => FieldEdit.WithValue(start, 0, "4x"), 3);
        Assert.Equal("4, 2, 3", Torque().Text);

        // The binding of the row is from before the end of the text edit.
        var row = Torque();
        Drag(router, session, Label, row, Activated, 0, 0, 4);
        Drag(router, session, Label, row, Typed, 0, 10, 4);

        Assert.StartsWith("'steering_torque' did not change:", Assert.Single(warnings));
        Assert.Equal("1, 2, 3", router.Open!.StartText);
        Assert.Equal("1.1, 2, 3", Torque().Text);
    }

    /// <summary>Routes the events of a label drag of a value of a vector, as the Inspector does.</summary>
    private static void Drag(FieldEditRouter router, EditSession session, uint id, FieldBinding field,
        FieldEvents events, int index, decimal units, int frame) =>
        router.Route(id, field.FieldKey, events, () => TestGestures.Number(session, field, 3),
            start => FieldDrag.DragText(DragKind.Scaled, start, 3, index, units), frame);

    /// <summary>Routes the events of a widget that changes the lines of a bound key, as the Inspector does.</summary>
    private static void Route(FieldEditRouter router, EditSession session, uint id, FieldBinding field,
        FieldEvents events, Func<string, string> text, int frame) =>
        router.Route(id, field.FieldKey, events, () => TestGestures.Line(session, field), text, frame);

    /// <summary>Routes the events of a widget whose edit runs when it ends, as <see cref="PropertyGrid.CommitField"/>
    /// does.</summary>
    private static void Commit(FieldEditRouter router, uint id, FieldKey key, FieldEvents events, string start,
        string text, Func<string, bool, string?> commit, int frame) =>
        router.Route(id, key, events, () => new CommitGesture(key, start, commit), _ => text, frame);

    /// <summary>A commit that records each text with its interactive flag, and runs no edit.</summary>
    private static Func<string, bool, string?> Record(List<(string Text, bool Interactive)> commits) =>
        (text, interactive) =>
        {
            commits.Add((text, interactive));
            return null;
        };

    /// <summary>The commit of a text key, as <see cref="PropertyGrid"/> makes it, with the session in place of the
    /// editor.</summary>
    /// <param name="ask">Gets the title, the message and the choices of a dialog.</param>
    private static Func<string, bool, string?> TextKeyCommit(EditSession session, FieldBinding field,
        Action<string, string, IReadOnlyList<Choice>> ask) =>
        EditController.TextKeyCommit(session, field, make => FakeCommandHost.Runner(session).Run(make), ask);

    /// <summary>The key lists as one text, so that a failed test shows the lists.</summary>
    private static string Lists(SchemaPage.KeyLists lists) =>
        $"Set: {string.Join(", ", lists.Set)}. Unknown: {string.Join(", ", lists.Unknown)}. " +
        $"Unset: {string.Join(", ", lists.Unset)}.";
}
