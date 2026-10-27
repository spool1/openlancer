using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.FieldBindingTests;

namespace OpenLancer.Tests;

/// <summary>
/// The field edit of a label drag and a color picker (section 6.7 and 11 of the M20 design): the numbers of a change go
/// into the one line of the key by the number rules of a gizmo drag, as one transaction. The tests use a binary Li01,
/// as retail stores it.
/// </summary>
public sealed class NumberLineGestureTests
{
    private const string Li01 = """
        [SystemInfo]
        space_color = 0, 0, 0

        [Object]
        nickname = Li01_depot
        archetype = depot
        pos = -38675, 0, -27705

        [Object]
        nickname = Li01_float
        archetype = depot
        pos = 200.0, 0, 0

        [LightSource]
        nickname = Light_main
        pos = 0, 0, 0
        color = 120, 250, 255, 128
        type = POINT
        """;

    /// <summary>Sections whose lines a helper cannot write.</summary>
    private const string Bad = """
        [Object]
        nickname = Li01_bad
        archetype = depot
        pos = abc, 0, 0

        [Object]
        nickname = Li01_twice
        archetype = depot
        pos = 1, 2, 3
        pos = 4, 5, 6
        """;

    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");
    private static readonly SectionRef Float = SystemWorkspace.Li01Section("Object", "Li01_float");
    private static readonly SectionRef Light = SystemWorkspace.Li01Section("LightSource", "Light_main");

    /// <summary>A drag of X by 1,000 writes an integer, and the values that did not change keep their objects. The
    /// end adds one entry, and an undo puts back the line.</summary>
    [Fact]
    public void ADragWritesIntegersAndKeepsTheOtherValues()
    {
        using var ws = SystemWorkspace.Create(Li01, binary: true);
        var session = ws.Session("act");
        var start = Values(session, Depot, "pos");

        var gesture = TestGestures.Number(session, Bind(session, Depot, "pos"), 3);
        Assert.Equal("-38675, 0, -27705", gesture.StartText);
        gesture.Change("-37675, 0, -27705");

        var line = Values(session, Depot, "pos");
        Assert.Equal(-37675, Assert.IsType<Int32Value>(line[0]).ToInt32());
        Assert.Same(start[1], line[1]);
        Assert.Same(start[2], line[2]);
        Assert.False(session.CanUndo);
        Assert.Null(gesture.End(keep: true, interactive: true));

        Assert.Equal("Set pos", session.UndoLabel);
        session.Undo();
        Assert.Equal("-38675, 0, -27705", FieldEdit.FormatLine(Values(session, Depot, "pos")));
        Assert.False(session.CanUndo);
    }

    /// <summary>A drag that comes back to its start puts back the start objects, so it adds no entry and the patch
    /// gets no line.</summary>
    [Fact]
    public void ADragBackToTheStartIsNoEdit()
    {
        using var ws = SystemWorkspace.Create(Li01, binary: true);
        var session = ws.Session("act");
        var start = Values(session, Depot, "pos");

        var gesture = TestGestures.Number(session, Bind(session, Depot, "pos"), 3);
        gesture.Change("-38665, 0, -27705");
        gesture.Change("-38675, 0, -27705");
        Assert.Null(gesture.End(keep: true, interactive: true));

        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
        Assert.True(Values(session, Depot, "pos").Zip(start).All(p => ReferenceEquals(p.First, p.Second)));
    }

    [Fact]
    public void AnEndWithoutKeepPutsBackTheEditView()
    {
        using var ws = SystemWorkspace.Create(Li01, binary: true);
        var session = ws.Session("act");
        var start = Values(session, Depot, "pos");

        var gesture = TestGestures.Number(session, Bind(session, Depot, "pos"), 3);
        gesture.Change("0, 0, 0");
        Assert.Null(gesture.End(keep: false, interactive: true));

        Assert.False(session.CanUndo);
        Assert.True(Values(session, Depot, "pos").Zip(start).All(p => ReferenceEquals(p.First, p.Second)));
    }

    /// <summary>A whole number that replaces a Single stays a Single, so the save writes <c>210.0</c>.</summary>
    [Fact]
    public void AWholeNumberThatReplacesASingleStaysASingle()
    {
        using var ws = SystemWorkspace.Create(Li01, binary: true);
        var session = ws.Session("act");
        Assert.IsType<SingleValue>(Values(session, Float, "pos")[0]);

        var gesture = TestGestures.Number(session, Bind(session, Float, "pos"), 3);
        gesture.Change(FieldDrag.DragText(DragKind.Position, gesture.StartText, 3, 0, 1));
        Assert.Null(gesture.End(keep: true, interactive: true));

        Assert.IsType<SingleValue>(Values(session, Float, "pos")[0]);
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Contains("pos = 210.0, 0, 0", File.ReadAllText(Assert.Single(saved.Written)));
    }

    /// <summary>The picker writes the first 3 values of a color. The alpha keeps its object.</summary>
    [Fact]
    public void APickKeepsTheAlphaOfAColor()
    {
        using var ws = SystemWorkspace.Create(Li01, binary: true);
        var session = ws.Session("act");
        var start = Values(session, Light, "color");

        var gesture = TestGestures.Number(session, Bind(session, Light, "color"), 3);
        Assert.Equal("120, 250, 255", gesture.StartText);
        gesture.Change("255, 128, 0");
        Assert.Null(gesture.End(keep: true, interactive: true));

        var line = Values(session, Light, "color");
        Assert.Equal("255, 128, 0, 128", FieldEdit.FormatLine(line));
        Assert.All(line.Take(3), v => Assert.IsType<Int32Value>(v));
        Assert.Same(start[3], line[3]);
        Assert.Equal("Set color", session.UndoLabel);
    }

    /// <summary>The edit starts only from one line whose written values are finite numbers. A refused start opens
    /// no transaction.</summary>
    [Fact]
    public void TheEditRefusesALineThatAHelperCannotWrite()
    {
        using var ws = SystemWorkspace.Create(Li01 + "\n\n" + Bad, binary: true);
        var session = ws.Session("act");

        var text = Assert.Throws<InvalidOperationException>(() =>
            TestGestures.Number(session, Bind(session, SystemWorkspace.Li01Section("Object", "Li01_bad"), "pos"), 3));
        Assert.Contains("Value 1 is not a number", text.Message);
        Assert.Throws<InvalidOperationException>(() =>
            TestGestures.Number(session, Bind(session, Depot, "rotate"), 3));
        Assert.Throws<InvalidOperationException>(() =>
            TestGestures.Number(session, Bind(session, SystemWorkspace.Li01Section("Object", "Li01_twice"), "pos"), 3));
        Assert.False(session.InTransaction);
        // A count above the number of values.
        Assert.Throws<InvalidOperationException>(() =>
            TestGestures.Number(session, Bind(session, Light, "pos"), 4));

        using var next = TestGestures.Number(session, Bind(session, Depot, "pos"), 3);
        Assert.NotNull(next);
    }

    /// <summary>A change to the line that the edit view holds applies nothing.</summary>
    [Fact]
    public void TheSameLineTwoTimesAppliesOneCommand()
    {
        using var ws = SystemWorkspace.Create(Li01, binary: true);
        var session = ws.Session("act");
        var changes = 0;
        session.Changed += _ => changes++;

        using var gesture = TestGestures.Number(session, Bind(session, Depot, "pos"), 3);
        gesture.Change("-38675, 0, -27705");
        Assert.Equal(0, changes);
        gesture.Change("-38665, 0, -27705");
        gesture.Change("-38665, 0, -27705");
        Assert.Equal(1, changes);
        gesture.Change("-38655, 0, -27705");
        Assert.Equal(2, changes);
    }

    /// <summary>The label drag and the picker make the text, so a text that is not numbers is a bug. The edit keeps
    /// the last valid state and gives the error.</summary>
    [Fact]
    public void ATextThatIsNotNumbersIsAnError()
    {
        using var ws = SystemWorkspace.Create(Li01, binary: true);
        var session = ws.Session("act");

        var gesture = TestGestures.Number(session, Bind(session, Depot, "pos"), 3);
        gesture.Change("1, 2, 3");
        gesture.Change("1, 2");

        Assert.NotNull(gesture.Error);
        Assert.Equal("1, 2, 3", FieldEdit.FormatLine(Values(session, Depot, "pos")));
        Assert.NotNull(gesture.End(keep: true, interactive: true));
        Assert.False(session.CanUndo);
    }

    private static IReadOnlyList<ValueBase> Values(EditSession session, SectionRef section,
        string name) => session.Get(section)!.Lines(name).Single().Values;
}
