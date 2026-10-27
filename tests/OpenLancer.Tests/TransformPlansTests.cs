using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The core of a transform as a plan of the command layer: a drag of the gizmo and a transform request make their
/// lines with one rule (<see cref="TransformValues"/> and <see cref="ZoneSizeAxes"/>).
/// </summary>
public sealed class TransformPlansTests
{
    private const string SystemText = """
        [SystemInfo]
        space_color = 0, 0, 0

        [Object]
        nickname = Li01_depot
        archetype = depot
        pos = -38675, 0, -27705
        rotate = 0, 105, 0

        [Zone]
        nickname = Zone_sphere
        pos = 0, 0, 0
        shape = SPHERE
        size = 4100

        [Zone]
        nickname = Zone_cylinder
        pos = 0, 0, 0
        shape = CYLINDER
        size = 750, 13088

        [Zone]
        nickname = Zone_ring
        pos = 0, 0, 0
        shape = RING
        size = 2000, 1000, 200

        [Zone]
        nickname = Zone_box
        pos = 0, 0, 0
        shape = BOX
        size = 100, 200, 300

        [LightSource]
        nickname = Light_main
        pos = 1, 2, 3
        type = POINT
        """;

    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");
    private static readonly SectionRef Light = SystemWorkspace.Li01Section("LightSource", "Light_main");

    /// <summary>The plan of a step of the gizmo writes the line that the number rules give for the value, with the
    /// label of the drag.</summary>
    [Fact]
    public void AStepOfTheGizmoWritesTheLineOfTheNumberRules()
    {
        using var ws = SystemWorkspace.Create(SystemText);
        var session = ws.Session("act");
        var start = session.Get(Depot)!.Lines("pos")[^1].Values;
        var value = new Vector3(-38555.5f, 0, -27705);

        var plan = TransformPlans.Plan(session, Depot, GizmoTool.Move, value);
        Assert.Equal("Move Li01_depot", plan.Label);
        Assert.Equal([Depot], plan.Changes);
        session.Execute(plan.Command!);

        Assert.True(SameLine(TransformValues.Line(start, value), session.Get(Depot)!.Lines("pos")[^1].Values));
        Assert.Equal(["-38555.5, 0, -27705"], Lines(session, Depot, "pos"));
        Assert.Equal("Move Li01_depot", session.UndoLabel);
    }

    /// <summary>A relative position and a relative rotation add to the values that the engine reads. The rotation adds
    /// the degrees to the values of <c>rotate</c>, as the key holds them.</summary>
    [Fact]
    public void ARelativeTransformAddsToTheValuesOfW()
    {
        using var ws = SystemWorkspace.Create(SystemText);
        var session = ws.Session("act");

        var plan = TransformPlans.Plan(session, Depot, new TransformChange(new Vector3(10, 0, -5), new Vector3(0, 100, 0)),
            TransformMode.Relative);
        Assert.Equal("Transform Li01_depot", plan.Label);
        session.Execute(plan.Command!);

        Assert.Equal(["-38665, 0, -27710"], Lines(session, Depot, "pos"));
        Assert.Equal(["0, 205, 0"], Lines(session, Depot, "rotate"));
        Assert.Equal("Transform Li01_depot", session.UndoLabel);
    }

    /// <summary>A size has the values of the line in the order of the line: one for a sphere, two for a cylinder, and
    /// outer radius, inner radius and height for a ring.</summary>
    [Theory]
    [InlineData("Zone_sphere", TransformMode.Absolute, new[] { 5000f }, "5000")]
    [InlineData("Zone_cylinder", TransformMode.Relative, new[] { 10f, -88f }, "760, 13000")]
    [InlineData("Zone_ring", TransformMode.Absolute, new[] { 3000f, 1500f, 300f }, "3000, 1500, 300")]
    [InlineData("Zone_ring", TransformMode.Relative, new[] { 0f, 0f, 50f }, "2000, 1000, 250")]
    [InlineData("Zone_box", TransformMode.Absolute, new[] { 10f, 20f, 30f }, "10, 20, 30")]
    public void ASizeHasTheValuesOfTheLineInItsOrder(string zone, TransformMode mode, float[] size, string line)
    {
        using var ws = SystemWorkspace.Create(SystemText);
        var session = ws.Session("act");
        var section = SystemWorkspace.Li01Section("Zone", zone);

        var plan = TransformPlans.Plan(session, section, new TransformChange(Size: size), mode);
        Assert.Equal($"Scale {zone}", plan.Label);
        session.Execute(plan.Command!);

        Assert.Equal([line], Lines(session, section, "size"));
    }

    /// <summary>A transform refuses a part that the section does not take, a section that is not an object, a zone or a
    /// light, a size with another count of values, and a value that is not finite. Nothing changes.</summary>
    [Fact]
    public void ATransformRefusesWhatTheSectionDoesNotTake()
    {
        using var ws = SystemWorkspace.Create(SystemText);
        var session = ws.Session("act");

        AssertRefused(session, Depot, new TransformChange(Size: [10, 10, 10]), "The engine reads no size for an object.");
        AssertRefused(session, Light, new TransformChange(Rotation: Vector3.One), "The engine reads no rotation for a light.");
        AssertRefused(session, SystemWorkspace.Li01Section("Zone", "Zone_sphere"), new TransformChange(Size: [1, 2, 3]),
            "The size of a sphere zone has one value. Give one value.");
        AssertRefused(session, SystemWorkspace.Li01Section("Zone", "Zone_cylinder"), new TransformChange(Size: [1]),
            "The size of a cylinder zone has 2 values. Give 2 values.");
        AssertRefused(session, SystemWorkspace.Li01Section("SystemInfo", null), new TransformChange(Vector3.One),
            "The gizmo changes only an object, a zone or a light.");
        AssertRefused(session, Depot, new TransformChange(new Vector3(float.NaN, 0, 0)),
            "A value of a transform must be a finite number.");
        Assert.False(session.CanUndo);
    }

    /// <summary>A transform to the values of W has no line in its plan, so it changes nothing.</summary>
    [Fact]
    public void ATransformToTheValuesOfWChangesNothing()
    {
        using var ws = SystemWorkspace.Create(SystemText);
        var session = ws.Session("act");

        var plan = TransformPlans.Plan(session, Depot, new TransformChange(new Vector3(-38675, 0, -27705)),
            TransformMode.Absolute);
        Assert.True(plan.CanRun);
        Assert.Empty(plan.Changes);
        session.Execute(plan.Command!);

        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    /// <summary>The order of the values of a size line and the axes of the gizmo map both ways for each shape.</summary>
    [Theory]
    [InlineData(ShapeKind.Sphere, new[] { 4100f }, 4100f, 1f, 1f)]
    [InlineData(ShapeKind.Cylinder, new[] { 750f, 13088f }, 750f, 13088f, 1f)]
    [InlineData(ShapeKind.Ring, new[] { 2000f, 1000f, 200f }, 2000f, 200f, 1000f)]
    [InlineData(ShapeKind.Ellipsoid, new[] { 10f, 20f, 30f }, 10f, 20f, 30f)]
    public void TheValuesOfASizeLineMapToTheAxesOfTheGizmo(ShapeKind shape, float[] line, float x, float y, float z)
    {
        var axes = ZoneSizeAxes.Of(shape, null);
        Assert.Equal(line.Length, axes.LineCount);
        Assert.Equal(new Vector3(x, y, z), axes.ScaleOf(line));
        Assert.Equal(line, axes.LineValues(new Vector3(x, y, z)));
    }

    private static void AssertRefused(EditSession session, SectionRef section, TransformChange change, string message)
    {
        var plan = TransformPlans.Plan(session, section, change, TransformMode.Absolute);
        Assert.False(plan.CanRun);
        Assert.Equal(message, Assert.Single(plan.Refusals).Message);
        Assert.Equal(section, plan.Refusals[0].Target);
    }

    private static bool SameLine(IReadOnlyList<ValueBase> a, IReadOnlyList<ValueBase> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.GetType() == p.Second.GetType() &&
                                                p.First.ToString() == p.Second.ToString());

    private static string[] Lines(EditSession session, SectionRef section, string key) =>
        session.Get(section)!.Lines(key).Select(l => string.Join(", ", l.Values.Select(v => v.ToString()))).ToArray();
}
