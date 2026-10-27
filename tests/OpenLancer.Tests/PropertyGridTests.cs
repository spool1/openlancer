using System;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

public sealed class PropertyGridTests
{
    [Theory]
    [InlineData(360, 1, 140)]
    [InlineData(800, 1, 320)]
    [InlineData(540, 1.5f, 210)]
    [InlineData(1200, 1.5f, 480)]
    public void GivesTheUnityLabelWidthWithAMinimum(float width, float scale, float expected)
    {
        Assert.Equal(expected, PropertyGrid.LabelWidth(width, scale));
    }

    [Fact]
    public void ScalesTheOffsetAndTheMinimumOnlyOnce()
    {
        // The width is in pixels, so it already has the scale. A second scale would make the column too wide.
        Assert.Equal(1.5f * PropertyGrid.LabelWidth(800, 1), PropertyGrid.LabelWidth(1200, 1.5f));
        Assert.Equal(1.5f * PropertyGrid.LabelWidth(360, 1), PropertyGrid.LabelWidth(540, 1.5f));
    }

    /// <summary>
    /// The labeled value fields of one line: an axis letter gets the axis label width, and a wider label gets a margin
    /// at each side. The fields share one row when each value fits, else each field gets its own row.
    /// </summary>
    [Theory]
    // One value with a word label, for the radius of a sphere. One field never stacks.
    [InlineData(1, 44.2f, 50, 200, 53, 147, false)]
    [InlineData(1, 44.2f, 500, 200, 53, 147, false)]
    // Two values, for the radius and the height of a cylinder.
    [InlineData(2, 46.6f, 40, 200, 55, 43, false)]
    [InlineData(2, 46.6f, 60, 200, 55, 145, true)]
    // Three axis letters keep the width of an axis label, as the vector rows had it before the labels.
    [InlineData(3, 9, 45, 200, 14, 50, false)]
    [InlineData(3, 9, 60, 200, 14, 186, true)]
    // Three word labels, for the outer radius, the inner radius and the height of a ring.
    [InlineData(3, 42, 40, 300, 50, 47, false)]
    public void ComponentFieldsShareARowWhenEachValueFits(int count, float label, float value, float available,
        float prefix, float field, bool stacked)
    {
        Assert.Equal(new PropertyGrid.ComponentLayout(prefix, field, stacked),
            PropertyGrid.ComponentLayout.Of(count, label, value, available, minimumPrefix: 14, spacing: 4));
    }

    /// <summary>
    /// The size of a zone labels its values in INI order by the shape. The color of a label is the color of the gizmo
    /// axis that changes the value (ZoneSizeAxes): a ring changes its outer radius on X, its inner radius on Z and its
    /// height on Y.
    /// </summary>
    [Theory]
    [InlineData(ShapeKind.Sphere, 1, "Radius:X")]
    [InlineData(ShapeKind.Cylinder, 2, "Radius:X, Height:Y")]
    [InlineData(ShapeKind.Ring, 3, "Outer:X, Inner:Z, Height:Y")]
    [InlineData(ShapeKind.Ellipsoid, 3, "X:X, Y:Y, Z:Z")]
    [InlineData(ShapeKind.Box, 3, "X:X, Y:Y, Z:Z")]
    public void TheSizeOfEachShapeLabelsItsValuesInIniOrder(ShapeKind shape, int count, string expected)
    {
        var labels = PropertyGrid.SizeLabels(shape, count);

        Assert.NotNull(labels);
        Assert.Equal(expected, string.Join(", ", labels.Select(l => $"{l.Text}:{Axis(l.Color)}")));
    }

    /// <summary>A size line with another number of values gets one text field: the engine reads one value v as (v, v,
    /// v) and two values as (a, b, 0), so a field per value would not tell what each value does.</summary>
    [Theory]
    [InlineData(ShapeKind.Sphere, 2)]
    [InlineData(ShapeKind.Sphere, 3)]
    [InlineData(ShapeKind.Cylinder, 1)]
    [InlineData(ShapeKind.Cylinder, 3)]
    [InlineData(ShapeKind.Ring, 1)]
    [InlineData(ShapeKind.Ring, 2)]
    [InlineData(ShapeKind.Ellipsoid, 1)]
    [InlineData(ShapeKind.Box, 2)]
    [InlineData(ShapeKind.Box, 0)]
    public void ASizeLineWithAnotherNumberOfValuesHasNoLabels(ShapeKind shape, int count) =>
        Assert.Null(PropertyGrid.SizeLabels(shape, count));

    /// <summary>
    /// The rotation row of a bound object shows the angles of its INI line in three fields. The engine holds the
    /// rotation as a quaternion, and its conversion gives the other Euler branch for this line. So the row asks for
    /// the binding before it converts, and a bound row converts nothing (section 14 of the M2 design).
    /// </summary>
    [Fact]
    public void TheRotationRowOfABoundObjectShowsTheAnglesOfItsLine()
    {
        using var ws = SystemWorkspace.Create("""
            [Object]
            nickname = Li01_to_Li05
            pos = 80632, 0, 12299
            rotate = 0, 105, 0
            """);
        var session = ws.Session("act");
        var gate = SystemWorkspace.Li01Section("Object", "Li01_to_Li05");
        var view = session.Get(gate)!;

        var rotate = new PropertyGrid.BoundSection(session, gate, view, view, null).Field("rotate");

        Assert.NotNull(rotate);
        Assert.Equal(["0", "105", "0"], FieldEdit.Values(rotate.Text));
        Assert.Equal("X, Y, Z", string.Join(", ", PropertyGrid.DefaultLabels(rotate.Key)!(3)!.Select(l => l.Text)));
        Assert.Null(PropertyGrid.DefaultLabels(rotate.Key)!(1));
        var converted = SceneBuild.ObjectRotation(view.Lines("rotate")[0].Values).GetEulerDegrees();
        Assert.Equal(180, MathF.Abs(converted.X), 2);
    }

    /// <summary>
    /// While a field edit of a section is open, a page lays out its rows from the section at the start of the edit.
    /// So a row of a key that not every item has stays while its own edit removes the last line of the key.
    /// </summary>
    [Fact]
    public void TheRowsOfASectionStayWhileTheEditOfOneRemovesItsLine()
    {
        using var ws = SystemWorkspace.Create("""
            [Object]
            nickname = Li01_depot
            pos = 0, 0, 0
            comment = old
            """);
        var session = ws.Session("act");
        var depot = SystemWorkspace.Li01Section("Object", "Li01_depot");
        var comment = FieldBinding.Create(session, depot, session.Get(depot)!, "comment");

        using (var gesture = TestGestures.Line(session, comment))
        {
            gesture.Change("");
            Assert.Empty(session.Get(depot)!.Lines("comment"));
            Assert.Single(PropertyGrid.LayoutView(session, depot, gesture)!.Lines("comment"));
            gesture.End(keep: true, interactive: true);
        }

        Assert.Empty(PropertyGrid.LayoutView(session, depot, null)!.Lines("comment"));
    }

    /// <summary>
    /// The widget of an editable bound row comes from the schema type of its key and the shape of its lines. A color key
    /// with one line gets the swatch of the picker. A color key without a line, with two lines or with multiline lines
    /// gets only the text field (section 6.2 of the M20 design).
    /// </summary>
    [Fact]
    public void EachBoundKeyGetsTheWidgetOfItsType()
    {
        var color = new KeySchema("color", typeof(Color4), false, false, 3, false, null);
        var vector = new KeySchema("spin", typeof(System.Numerics.Vector3), false, false, 1, false, null);
        var flag = new KeySchema("separable", typeof(bool), false, false, 0, false, null);
        var shape = new KeySchema("shape", typeof(ShapeKind), false, false, 1, false, null);
        PropertyGrid.BoundWidget Widget(KeySchema key, int lines, int values) =>
            PropertyGrid.WidgetOf(key, lines, values, PropertyGrid.DefaultLabels(key));

        Assert.Equal(PropertyGrid.BoundWidget.Color, Widget(color, 1, 3));
        Assert.Equal(PropertyGrid.BoundWidget.Color, Widget(color, 1, 4));
        Assert.Equal(PropertyGrid.BoundWidget.Components, Widget(vector, 1, 3));
        Assert.Equal(PropertyGrid.BoundWidget.Bool, Widget(flag, 1, 1));
        Assert.Equal(PropertyGrid.BoundWidget.Enum, Widget(shape, 0, 0));

        Assert.Equal(PropertyGrid.BoundWidget.Text, Widget(color, 0, 0));
        Assert.Equal(PropertyGrid.BoundWidget.Text, Widget(color, 2, 3));
        Assert.Equal(PropertyGrid.BoundWidget.Text, Widget(color with { Multiline = true }, 1, 3));
        Assert.Equal(PropertyGrid.BoundWidget.Text, Widget(vector, 1, 1));
        // The size of a sphere has its own labels.
        Assert.Equal(PropertyGrid.BoundWidget.Components,
            PropertyGrid.WidgetOf(vector, 1, 1, count => PropertyGrid.SizeLabels(ShapeKind.Sphere, count)));
    }

    /// <summary>While a label drag or a picker edits a key, the page lays out its rows from the section at the start
    /// of the edit, and the row keeps the widgets of the key at that start.</summary>
    [Fact]
    public void TheRowsOfASectionStayWhileADragIsOpen()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var depot = SystemWorkspace.Li01Section("Object", "Li01_depot");
        var pos = FieldBinding.Create(session, depot, session.Get(depot)!, "pos");
        var router = new FieldEditRouter(_ => { });

        router.Route(1, pos.FieldKey, new FieldEvents(true, true, false, false, false),
            () => TestGestures.Number(session, pos, 3), start => start, 1);
        router.Route(1, pos.FieldKey, new FieldEvents(false, true, true, false, false),
            () => throw new InvalidOperationException(), _ => "-37675, 0, -27705", 2);

        var gesture = Assert.IsType<NumberLineGesture>(router.Open);
        Assert.Equal("-37675, 0, -27705", FieldEdit.Format(session.Get(depot)!.Lines("pos")));
        Assert.Same(gesture.StartSection, PropertyGrid.LayoutView(session, depot, gesture));
        Assert.Equal("-38675, 0, -27705", FieldEdit.Format(PropertyGrid.LayoutView(session, depot, gesture)!.Lines("pos")));
        var now = FieldBinding.Create(session, depot, session.Get(depot)!, "pos");
        Assert.Same(gesture.Binding, router.StartOf(now));
        router.End(keep: true);
        Assert.Null(router.StartOf(now));
    }

    private static string Axis(System.Numerics.Vector4 color) =>
        color == EditorTheme.AxisX ? "X" : color == EditorTheme.AxisY ? "Y" : color == EditorTheme.AxisZ ? "Z" : "?";
}
