using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The shape of a zone (shared step SH6). The class has the rules of the engine that make the shape, the size and the
/// rotation of a zone from its lines. The scene build uses the same rules. The class also has the math of the shape.
/// The math tells whether a point is inside and how deep the point is. It also gives the size of the shape in meters,
/// and the size line for a size in meters.
/// </summary>
/// <remarks>
/// <para>The math reads a <c>[zone]</c> of W as the engine reads its lines (see <see cref="Read"/>). So it needs no
/// file and no game load.</para>
/// <para>The engine reads the <c>size</c> line in the order of the line. A sphere uses the radius. An ellipsoid uses
/// the three half axes, and a box the three full lengths. A cylinder uses the radius and the height. A ring uses the
/// outer radius, the inner radius and the height. One value v reads as (v, v, v), two values give 0 as the third
/// value, and no line reads as (1, 1, 1). The X, Y and Z axes are the axes of the zone after its rotation. Y is the
/// height of a cylinder and of a ring.</para>
/// <para><see cref="Contains"/> uses the shape code of the engine (<see cref="Zone.ContainsPoint"/>), as OpenLancer
/// Play does. <see cref="Immersion"/> follows the retail rule, because a nebula hole uses it in the retail
/// games.</para>
/// </remarks>
public static class ZoneShapes
{
    /// <summary>The smallest distance in meters that the retail rule uses. Below it, the scaled distance is
    /// 0.</summary>
    private const float Tiny = 1e-4f;

    /// <summary>The shape of a zone. A zone without a shape is a sphere (GameItemDb.cs:1663).</summary>
    public static ShapeKind ShapeOf(Universe.ZoneShape? shape) => (ShapeKind)(int)(shape ?? Universe.ZoneShape.SPHERE);

    /// <summary>
    /// The size of a zone as the engine keeps it (GameItemDb.cs:1664-1673): (1, 1, 1) without a line. A ring has the
    /// size (outer radius, height, inner radius), from the line order (outer radius, inner radius, height). Each other
    /// shape keeps the line order.
    /// </summary>
    /// <param name="size">The values of the <c>size</c> line as the parser reads them, or null without a line.</param>
    public static Vector3 SizeOf(Vector3? size, ShapeKind shape)
    {
        var values = size ?? Vector3.One;
        return shape == ShapeKind.Ring ? new Vector3(values.X, values.Z, values.Y) : values;
    }

    /// <summary>The rotation of a zone from its angles in degrees, or no rotation (GameItemDb.cs:1647-1661).</summary>
    public static Matrix4x4 RotationOf(Vector3? degrees) =>
        degrees is { } angles ? MathHelper.MatrixFromEulerDegrees(angles) : Matrix4x4.Identity;

    /// <summary>A <c>[zone]</c> of W as the engine reads its lines (see <see cref="EditSession.Parse{T}"/>). Null when
    /// the edit view has no such section, or when the section has a check error.</summary>
    internal static Universe.Zone? Read(EditSession session, SectionRef zone) =>
        session.Parse<Universe.Zone>(zone, Universe.Zone.TryParse);

    /// <summary>True when the shape of the zone holds the point, as OpenLancer Play finds it
    /// (<see cref="Zone.ContainsPoint"/>). A point on the edge is inside.</summary>
    internal static bool Contains(Universe.Zone zone, Vector3 point) => Build(zone).ContainsPoint(point);

    /// <summary>
    /// How deep a point is in the zone, with the retail rule. The value is 1 from the start of the fade band inward,
    /// and 0 on the edge and outside. In the fade band, it goes from 1 to 0. The fade band is the outer part of the
    /// zone, of the <c>edge_fraction</c> of the scaled distance (see <see cref="ScaledDistance"/>).
    /// </summary>
    /// <remarks>
    /// <para>A point outside the bounding sphere of the zone gets 0. Retail clamps <c>edge_fraction</c> to 0 to 1. A
    /// zone without the line, or with an edge fraction below 0.0001, has no fade band, so each point inside gets 1.
    /// OpenLancer Play gives the fog of a nebula without the line the edge fraction 0.25.</para>
    /// <para>Retail logs a data error for a nebula hole when the immersion of the nebula zone at the hole center is
    /// below 1. At 0, retail skips the hole.</para>
    /// </remarks>
    internal static float Immersion(Universe.Zone zone, Vector3 point)
    {
        var built = Build(zone);
        var bound = BoundingRadius(built);
        var scaled = ScaledDistance(built, point);
        if (Vector3.DistanceSquared(point, built.Position) > bound * bound || !(scaled < 1))
            return 0;
        var band = Math.Clamp(zone.EdgeFraction ?? 0, 0, 1);
        return band < Tiny ? 1 : MathF.Min(1, (1 - (float)scaled) / band);
    }

    /// <summary>
    /// The width (X), the height (Y) and the depth (Z) of the shape of the zone in meters, along the axes of the zone.
    /// A sphere, an ellipsoid, a cylinder and a ring are as wide as two radii. A box is its three lengths.
    /// </summary>
    internal static Vector3 Extent(Universe.Zone zone)
    {
        var built = Build(zone);
        var size = built.Size;
        return built.Shape switch
        {
            ShapeKind.Sphere => new Vector3(2 * size.X),
            ShapeKind.Ellipsoid => 2 * size,
            ShapeKind.Box => size,
            // A cylinder and a ring: the outer radius on X and Z, the height on Y.
            _ => new Vector3(2 * size.X, size.Y, 2 * size.X),
        };
    }

    /// <summary>
    /// The values of the <c>size</c> line of the smallest shape whose extent is at least the extent on each axis (see
    /// <see cref="Extent"/>). The values are in the order of the line. A sphere takes the largest of the three values.
    /// A cylinder and a ring take the larger of the width and the depth. A ring gets the inner radius 0.
    /// </summary>
    /// <param name="extent">The width, the height and the depth in meters.</param>
    internal static IReadOnlyList<float> SizeFor(Universe.ZoneShape shape, Vector3 extent) => shape switch
    {
        Universe.ZoneShape.SPHERE => [MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) / 2],
        Universe.ZoneShape.ELLIPSOID => [extent.X / 2, extent.Y / 2, extent.Z / 2],
        Universe.ZoneShape.BOX => [extent.X, extent.Y, extent.Z],
        Universe.ZoneShape.CYLINDER => [MathF.Max(extent.X, extent.Z) / 2, extent.Y],
        _ => [MathF.Max(extent.X, extent.Z) / 2, 0, extent.Y],
    };

    /// <summary>The shape, the size, the rotation and the position of a zone, as the engine builds them.</summary>
    private static Zone Build(Universe.Zone zone)
    {
        var shape = ShapeOf(zone.Shape);
        return new Zone
        {
            Shape = shape,
            RotationMatrix = RotationOf(zone.Rotate),
            Size = SizeOf(zone.Size, shape),
            Position = zone.Pos ?? Vector3.Zero,
        };
    }

    /// <summary>
    /// The scaled distance of a point with the retail rule: 0 at the center, 1 on the edge and more than 1 outside.
    /// </summary>
    /// <remarks>
    /// <para>The retail rule for each shape, along the axes of the zone:</para>
    /// <list type="bullet">
    /// <item>A sphere and an ellipsoid: the distance from the center, divided by the distance from the center to the
    /// surface in the same direction.</item>
    /// <item>A cylinder: the larger of two values. One is the distance from the middle plane divided by half the
    /// height. The other is the distance from the axis divided by the radius.</item>
    /// <item>A ring: the same two values in the cross section of the ring. The cross section has its center on the
    /// middle circle of the ring, and it is as wide as the outer radius minus the inner radius. Retail does not take
    /// the larger value. It uses the height value when that value is larger than the distance from the middle circle
    /// divided by the outer radius. So it uses the height value for some points beyond the inner or the outer side of
    /// a flat ring, and counts them as inside.</item>
    /// <item>A box: (s + r²) / r². Here s is the signed squared distance to the surface, negative inside, and r is the
    /// radius of the bounding sphere. So even the center of a box has a scaled distance near 1.</item>
    /// </list>
    /// <para>Retail gives 0 for a point less than 0.0001 m from the center. It also gives 0 when the surface is 0.0001 m
    /// or less from the center in the direction of the point. For a ring, the center is the nearest point of the middle
    /// circle.
    /// Retail divides by zero for a point exactly on the axis of a ring. Here such a point is in the hole, as the
    /// points near the axis are.</para>
    /// </remarks>
    private static double ScaledDistance(Zone zone, Vector3 point)
    {
        var size = zone.Size;
        var local = Vector3.Transform(point - zone.Position, Matrix4x4.Transpose(zone.RotationMatrix));
        var fromAxis = MathF.Sqrt(local.X * local.X + local.Z * local.Z);
        switch (zone.Shape)
        {
            case ShapeKind.Sphere:
                return Checked(local.Length(), local.Length() / size.X);
            case ShapeKind.Ellipsoid:
                return Checked(local.Length(), (local / size).Length());
            case ShapeKind.Cylinder:
                return Checked(local.Length(), CrossSection(fromAxis, local.Y, size.X, size.Y / 2, size.X));
            case ShapeKind.Ring:
            {
                // The point in the cross section: the offset from the middle circle, and the height.
                var side = fromAxis - (size.X + size.Z) / 2;
                var distance = MathF.Sqrt(side * side + local.Y * local.Y);
                return Checked(distance, CrossSection(side, local.Y, (size.X - size.Z) / 2, size.Y / 2, size.X));
            }
            default:
            {
                // A box. The value is near 1 for each point near a face. So the value has double precision, as in
                // retail, and a point a few centimeters inside a face is inside.
                double radius = BoundingRadius(zone);
                return (SignedSquaredDistance(size / 2, local) + radius * radius) / (radius * radius);
            }
        }
    }

    /// <summary>
    /// The scaled distance in the cross section of a cylinder or a ring. It is the height value when that value is
    /// larger than the side value for the half width <paramref name="compareWidth"/>. Else it is the side value.
    /// </summary>
    /// <param name="side">The distance from the vertical center line of the section.</param>
    /// <param name="height">The distance from the middle plane.</param>
    /// <param name="halfWidth">Half the width of the section.</param>
    /// <param name="halfHeight">Half the height of the section.</param>
    /// <param name="compareWidth">The half width that the retail rule compares with.</param>
    private static float CrossSection(float side, float height, float halfWidth, float halfHeight, float compareWidth)
    {
        var up = MathF.Abs(height) / halfHeight;
        return up > MathF.Abs(side) / compareWidth ? up : MathF.Abs(side) / halfWidth;
    }

    /// <summary>
    /// A scaled distance, or 0 when the point is less than 0.0001 m from the center. It is also 0 when the surface is
    /// 0.0001 m or less from the center in the direction of the point, or when the value is not a number.
    /// </summary>
    /// <param name="distance">The distance from the center in meters.</param>
    /// <param name="scaled">The scaled distance.</param>
    private static float Checked(float distance, float scaled) =>
        distance < Tiny || !(distance / scaled > Tiny) ? 0 : scaled;

    /// <summary>The squared distance from a point to the surface of a box with the half lengths, along the axes of the
    /// box. It is positive outside and on the surface. Inside, it is negative, with the distance to the nearest
    /// face.</summary>
    private static float SignedSquaredDistance(Vector3 half, Vector3 point)
    {
        var beyond = Vector3.Abs(point) - half;
        if (beyond.X >= 0 || beyond.Y >= 0 || beyond.Z >= 0)
            return Vector3.Max(beyond, Vector3.Zero).LengthSquared();
        var nearest = MathF.Max(beyond.X, MathF.Max(beyond.Y, beyond.Z));
        return -nearest * nearest;
    }

    /// <summary>The radius of the bounding sphere of the zone with the retail rule. A sphere uses its radius, an
    /// ellipsoid its largest half axis, and a box half its diagonal. A cylinder and a ring use the distance from the
    /// center to the outer edge of their top.</summary>
    private static float BoundingRadius(Zone zone)
    {
        var size = zone.Size;
        return zone.Shape switch
        {
            ShapeKind.Sphere => size.X,
            ShapeKind.Ellipsoid => MathF.Max(size.X, MathF.Max(size.Y, size.Z)),
            ShapeKind.Box => size.Length() / 2,
            _ => MathF.Sqrt(size.X * size.X + size.Y * size.Y / 4),
        };
    }
}
