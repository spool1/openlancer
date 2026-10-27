using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;

namespace OpenLancer.Runtime.Scene;

/// <summary>
/// The scale handles of a zone: the scale of the gizmo matrix, the axes that a drag can change, and the values of the
/// <c>size</c> line that each axis writes. The handles are the handles of LancerEdit (ShapeExtensions.cs:56-70) for
/// each count of values that the engine reads.
/// </summary>
/// <remarks>
/// The engine reads one value <c>v</c> as (v, v, v), two values as (a, b, 0), and no line as (1, 1, 1). A ring gets the
/// size (outer, height, inner) from the line (outer, inner, height). A sphere uses only the first value, and a cylinder
/// uses the first two (radius, height).
/// </remarks>
public sealed record ZoneSizeAxes
{
    /// <summary>The smallest size that a drag writes on an axis that it changes. A transform request with a smaller
    /// new value refuses (see <see cref="TransformPlans"/>).</summary>
    internal const float MinSize = 1;

    private readonly ShapeKind shape;

    private ZoneSizeAxes(ShapeKind shape, Vector3 gizmoScale, bool free, string? disabled = null)
    {
        this.shape = shape;
        GizmoScale = gizmoScale;
        FreeX = free;
        FreeY = free && shape != ShapeKind.Sphere;
        FreeZ = free && shape is not (ShapeKind.Sphere or ShapeKind.Cylinder);
        Disabled = disabled;
    }

    /// <summary>The scale of the gizmo matrix: the size of each axis that the shape uses, and 1 on the other
    /// axes.</summary>
    public Vector3 GizmoScale { get; }

    public bool FreeX { get; }

    public bool FreeY { get; }

    public bool FreeZ { get; }

    /// <summary>Why the scale tool is not available for the zone, or null when it is.</summary>
    public string? Disabled { get; }

    /// <param name="sizeLine">The values of the last <c>size</c> line, or null when the zone has no line.</param>
    public static ZoneSizeAxes Of(ShapeKind shape, IReadOnlyList<ValueBase>? sizeLine)
    {
        var count = sizeLine?.Count ?? 0;
        var size = SceneBuild.ZoneSize(sizeLine, shape);
        var axes = shape switch
        {
            ShapeKind.Sphere => new ZoneSizeAxes(shape, new Vector3(size.X, 1, 1), true),
            ShapeKind.Ellipsoid or ShapeKind.Box when count == 2 => new ZoneSizeAxes(shape, size, false,
                "The size has two values, so the engine gives Z the size 0. Give it three values first."),
            ShapeKind.Ellipsoid or ShapeKind.Box => new ZoneSizeAxes(shape, size, true),
            ShapeKind.Cylinder => new ZoneSizeAxes(shape, size with { Z = 1 }, true),
            ShapeKind.Ring when count == 2 => new ZoneSizeAxes(shape, size, false,
                "The size has two values, so the engine gives the ring the height 0. Give it three values first."),
            ShapeKind.Ring => new ZoneSizeAxes(shape, size, true),
            _ => new ZoneSizeAxes(shape, Vector3.One, false, "The engine does not know the shape of the zone."),
        };
        var used = axes.GizmoScale;
        // ImGuizmo inverts the gizmo matrix, so a size of 0 gives infinite values and NaN.
        return axes.FreeX && !(used.X > 0) || axes.FreeY && !(used.Y > 0) || axes.FreeZ && !(used.Z > 0)
            ? new ZoneSizeAxes(shape, used, false, "The size has a value of 0 or less on an axis that the shape uses.")
            : axes;
    }

    /// <summary>The number of values of the <c>size</c> line that the shape uses: one for a sphere, two for a cylinder
    /// (radius, height), and three for the other shapes.</summary>
    public int LineCount => shape switch
    {
        ShapeKind.Sphere => 1,
        ShapeKind.Cylinder => 2,
        _ => 3,
    };

    /// <summary>
    /// The scale of the gizmo for the values of a <c>size</c> line in the order of the line, as the engine reads them
    /// (see <see cref="SceneBuild.ZoneSize"/>). The axes that the shape does not use have the scale 1, as in
    /// <see cref="GizmoScale"/>.
    /// </summary>
    public Vector3 ScaleOf(IReadOnlyList<float> values)
    {
        var size = SceneBuild.ZoneSize(values.Select(v => (ValueBase)new SingleValue(v, null)).ToList(), shape);
        return shape switch
        {
            ShapeKind.Sphere => new Vector3(size.X, 1, 1),
            ShapeKind.Cylinder => size with { Z = 1 },
            _ => size,
        };
    }

    /// <summary>The values of a <c>size</c> line in the order of the line for a scale of the gizmo, one for each value
    /// that the shape uses (see <see cref="LineCount"/>). It is the reverse of <see cref="ScaleOf"/>.</summary>
    public IReadOnlyList<float> LineValues(Vector3 scale) => shape switch
    {
        ShapeKind.Sphere => [scale.X],
        ShapeKind.Cylinder => [scale.X, scale.Y],
        ShapeKind.Ring => [scale.X, scale.Z, scale.Y],
        _ => [scale.X, scale.Y, scale.Z],
    };

    /// <summary>
    /// The values of the <c>size</c> line after a scale. When no free axis changed, the result is the start line, or
    /// an empty list when there is no line. A changed axis gets a size of 1 or more. The values follow the number rules
    /// of <see cref="TransformValues.Line"/>:
    /// <list type="bullet">
    /// <item>A sphere writes its first value. The other values stay.</item>
    /// <item>An ellipsoid and a box write three values, also when the line had one value.</item>
    /// <item>A cylinder writes its first two values (radius, height). A third value stays.</item>
    /// <item>A ring writes three values in the order of the line: outer (X), inner (Z), height (Y).</item>
    /// </list>
    /// </summary>
    /// <param name="start">The values of the size line at the start of the drag, or null when the zone has no line.</param>
    /// <param name="newScale">The scale of the gizmo matrix after the drag.</param>
    /// <exception cref="InvalidOperationException">The scale tool is not available for the zone.</exception>
    public IReadOnlyList<ValueBase> Line(IReadOnlyList<ValueBase>? start, Vector3 newScale)
    {
        if (Disabled != null)
            throw new InvalidOperationException(Disabled);
        var x = Axis(FreeX, GizmoScale.X, newScale.X);
        var y = Axis(FreeY, GizmoScale.Y, newScale.Y);
        var z = Axis(FreeZ, GizmoScale.Z, newScale.Z);
        if (new Vector3(x, y, z) == GizmoScale)
            return start ?? [];
        return shape switch
        {
            ShapeKind.Sphere => TransformValues.Line(start, [x]),
            ShapeKind.Cylinder => TransformValues.Line(start, [x, y]),
            ShapeKind.Ring => TransformValues.Line(start, [x, z, y]),
            _ => TransformValues.Line(start, [x, y, z]),
        };
    }

    /// <summary>The new size of an axis: the start size when the axis is not free or did not change, else the new
    /// size and at least <see cref="MinSize"/>.</summary>
    private static float Axis(bool free, float start, float scale) =>
        !free || scale == start ? start : MathF.Max(scale, MinSize);
}
