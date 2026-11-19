using System;
using System.Collections.Generic;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The math of the shape of a zone (shared step SH6): whether a point is inside, the immersion of a point, the size of
/// the shape in meters, and the size line for a size in meters. The zone is a <c>[zone]</c> of W as the engine reads its
/// lines (see <see cref="Read"/>), so the math needs no file and no game load.
/// </summary>
/// <remarks>
/// <para>The engine reads the <c>size</c> line in the order of the line. A sphere uses the radius. An ellipsoid uses
/// the three half axes, and a box the three full lengths. A cylinder uses the radius and the height. A ring uses the
/// outer radius, the inner radius and the height. One value v reads as (v, v, v), two values give 0 as the third
/// value, and no line reads as (1, 1, 1). The X, Y and Z axes are the axes of the zone after its rotation, and Y is
/// the height of a cylinder and of a ring.</para>
/// <para><see cref="Contains"/> uses the shape code of the engine (<see cref="Zone.ContainsPoint"/>), as OpenLancer Play
/// does. <see cref="Immersion"/> follows the retail rule, because a nebula hole uses it in the retail games.</para>
/// </remarks>
internal static class ZoneShapes
{
    /// <summary>The smallest length that the retail rule divides by. A shorter length gives the scaled distance
    /// 0.</summary>
    private const float Tiny = 1e-4f;

    /// <summary>A <c>[zone]</c> of W as the engine reads its lines (see <see cref="EditSession.Parse{T}"/>). Null when
    /// the edit view has no such section, or when the section has a check error.</summary>
    public static Universe.Zone? Read(EditSession session, SectionRef zone) =>
        session.Parse<Universe.Zone>(zone, Universe.Zone.TryParse);

    /// <summary>True when the shape of the zone holds the point, as OpenLancer Play finds it
    /// (<see cref="Zone.ContainsPoint"/>). A point on the edge is inside.</summary>
    public static bool Contains(Universe.Zone zone, Vector3 point)
    {
        var shape = ShapeOf(zone);
        var size = SizeOf(zone);
        var built = new Zone
        {
            Shape = shape,
            RotationMatrix = RotationOf(zone),
            // The engine keeps a ring as (outer, height, inner), from the line order (outer, inner, height).
            Size = shape == ShapeKind.Ring ? new Vector3(size.X, size.Z, size.Y) : size,
            Position = zone.Pos ?? Vector3.Zero,
        };
        return built.ContainsPoint(point);
    }

    /// <summary>
    /// How deep a point is in the zone, with the retail rule: 1 from the start of the fade band inward, 0 on the edge and
    /// outside, and a value between 0 and 1 in the fade band. The fade band is the outer part of the zone, of the
    /// <c>edge_fraction</c> of the scaled distance (see <see cref="ScaledDistance"/>).
    /// </summary>
    /// <remarks>
    /// <para>A point outside the bounding sphere of the zone gets 0. Retail clamps <c>edge_fraction</c> to 0 to 1. A
    /// zone without the line, or with an edge fraction below 0.0001, has no fade band, so each point inside gets 1.
    /// OpenLancer Play gives the fog of a nebula without the line the edge fraction 0.25.</para>
    /// <para>Retail logs a data error for a nebula hole whose center has an immersion of the nebula zone below 1, and it
    /// skips the hole at 0.</para>
    /// </remarks>
    public static float Immersion(Universe.Zone zone, Vector3 point)
    {
        var radius = BoundingRadius(zone);
        if (Vector3.DistanceSquared(point, zone.Pos ?? Vector3.Zero) > radius * radius)
            return 0;
        var scaled = ScaledDistance(zone, point);
        if (!(scaled < 1))
            return 0;
        var edge = Math.Clamp(zone.EdgeFraction ?? 0, 0, 1);
        if (1 - edge > scaled || edge < Tiny)
            return 1;
        return (1 - scaled) / edge;
    }

    /// <summary>
    /// The width (X), the height (Y) and the depth (Z) of the shape of the zone in meters, along the axes of the zone. A
    /// sphere, an ellipsoid, a cylinder and a ring are as wide as two radii. A box is its three lengths.
    /// </summary>
    public static Vector3 Extent(Universe.Zone zone)
    {
        var size = SizeOf(zone);
        return ShapeOf(zone) switch
        {
            ShapeKind.Sphere => new Vector3(2 * size.X),
            ShapeKind.Ellipsoid => 2 * size,
            ShapeKind.Box => size,
            ShapeKind.Cylinder => new Vector3(2 * size.X, size.Y, 2 * size.X),
            _ => new Vector3(2 * size.X, size.Z, 2 * size.X),
        };
    }

    /// <summary>
    /// The values of the <c>size</c> line of the smallest shape whose extent is at least the extent on each axis (see
    /// <see cref="Extent"/>), in the order of the line. A sphere takes the largest of the three values. A cylinder and a
    /// ring take the larger of the width and the depth. A ring gets the inner radius 0.
    /// </summary>
    /// <param name="extent">The width, the height and the depth in meters.</param>
    public static IReadOnlyList<float> SizeFor(Universe.ZoneShape shape, Vector3 extent) => shape switch
    {
        Universe.ZoneShape.SPHERE => [MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) / 2],
        Universe.ZoneShape.ELLIPSOID => [extent.X / 2, extent.Y / 2, extent.Z / 2],
        Universe.ZoneShape.BOX => [extent.X, extent.Y, extent.Z],
        Universe.ZoneShape.CYLINDER => [MathF.Max(extent.X, extent.Z) / 2, extent.Y],
        _ => [MathF.Max(extent.X, extent.Z) / 2, 0, extent.Y],
    };

    /// <summary>
    /// The scaled distance of a point with the retail rule: 0 at the center, 1 on the edge and more than 1 outside.
    /// A sphere, an ellipsoid and a cylinder divide the distance from the center by the distance from the center to the
    /// edge in the same direction. A ring does the same from the middle circle of its ring, in the cross section of the
    /// ring. A box uses its signed squared distance to the surface s, negative inside, and the radius r of its bounding
    /// sphere: (s + r²) / r².
    /// </summary>
    private static float ScaledDistance(Universe.Zone zone, Vector3 point)
    {
        var size = SizeOf(zone);
        var local = Vector3.Transform(point - (zone.Pos ?? Vector3.Zero), Matrix4x4.Transpose(RotationOf(zone)));
        switch (ShapeOf(zone))
        {
            case ShapeKind.Box:
            {
                var r = BoundingRadius(zone);
                return (SignedSquaredDistance(size / 2, local) + r * r) / (r * r);
            }
            case ShapeKind.Ring:
            {
                var middle = (size.X + size.Y) / 2;
                var radial = MathF.Sqrt(local.X * local.X + local.Z * local.Z);
                // The nearest point of the middle circle. A point on the axis of the ring takes the point on the X axis.
                var rim = radial > 0 ? new Vector3(local.X, 0, local.Z) * (middle / radial) : new Vector3(middle, 0, 0);
                var tube = local - rim;
                var length = tube.Length();
                var direction = length < Tiny ? tube : tube / length;
                var surface = RingSurface(size, direction);
                return surface > Tiny ? length / surface : 0;
            }
            default:
            {
                var length = local.Length();
                if (length < Tiny)
                    return 0;
                var edge = EdgeAlong(ShapeOf(zone), size, local / length);
                return edge > Tiny ? length / edge : 0;
            }
        }
    }

    /// <summary>The distance from the center of a sphere, an ellipsoid or a cylinder to its edge in a direction of
    /// length 1, along the axes of the zone.</summary>
    private static float EdgeAlong(ShapeKind shape, Vector3 size, Vector3 direction)
    {
        switch (shape)
        {
            case ShapeKind.Ellipsoid:
            {
                var sum = direction.X * direction.X / (size.X * size.X) + direction.Y * direction.Y / (size.Y * size.Y) +
                          direction.Z * direction.Z / (size.Z * size.Z);
                return sum > 0 && float.IsFinite(sum) ? 1 / MathF.Sqrt(sum) : 0;
            }
            case ShapeKind.Cylinder:
            {
                var half = size.Y / 2;
                var corner = half / MathF.Sqrt(size.X * size.X + half * half);
                return MathF.Abs(direction.Y) > corner
                    ? half / MathF.Abs(direction.Y)
                    : size.X / MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
            }
            default:
                return size.X;
        }
    }

    /// <summary>
    /// The distance from the middle circle of a ring to the edge of its cross section, in a direction in the cross
    /// section. The cross section is as wide as the outer radius minus the inner radius, and as high as the ring.
    /// </summary>
    /// <remarks>Retail compares the direction with the corner of a section as wide as the outer radius. So it chooses
    /// the top or the bottom for some directions to the inner or the outer side. The method does the same.</remarks>
    private static float RingSurface(Vector3 size, Vector3 direction)
    {
        var half = size.Z / 2;
        var corner = half / MathF.Sqrt(size.X * size.X + half * half);
        return MathF.Abs(direction.Y) > corner
            ? half / MathF.Abs(direction.Y)
            : (size.X - size.Y) / 2 / MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
    }

    /// <summary>The squared distance from a point to the surface of a box with the half lengths, along the axes of the
    /// box: positive outside and on the surface, negative inside, with the distance to the nearest face.</summary>
    private static float SignedSquaredDistance(Vector3 half, Vector3 point)
    {
        var over = Vector3.Abs(point) - half;
        if (over.X >= 0 || over.Y >= 0 || over.Z >= 0)
            return Vector3.Max(over, Vector3.Zero).LengthSquared();
        var nearest = MathF.Max(over.X, MathF.Max(over.Y, over.Z));
        return -nearest * nearest;
    }

    /// <summary>The radius of the bounding sphere of the zone with the retail rule. A sphere uses its radius, an
    /// ellipsoid its largest half axis, and a box half its diagonal. A cylinder and a ring use the distance from the
    /// center to the outer edge of their top.</summary>
    private static float BoundingRadius(Universe.Zone zone)
    {
        var size = SizeOf(zone);
        return ShapeOf(zone) switch
        {
            ShapeKind.Sphere => size.X,
            ShapeKind.Ellipsoid => MathF.Max(size.X, MathF.Max(size.Y, size.Z)),
            ShapeKind.Box => size.Length() / 2,
            ShapeKind.Cylinder => MathF.Sqrt(size.X * size.X + size.Y * size.Y / 4),
            _ => MathF.Sqrt(size.X * size.X + size.Z * size.Z / 4),
        };
    }

    /// <summary>The shape of a zone. A zone without a shape is a sphere (GameItemDb.cs:1663).</summary>
    private static ShapeKind ShapeOf(Universe.Zone zone) => (ShapeKind)(int)(zone.Shape ?? Universe.ZoneShape.SPHERE);

    /// <summary>The size of a zone in the order of the line, or (1, 1, 1) without a line (GameItemDb.cs:1664).</summary>
    private static Vector3 SizeOf(Universe.Zone zone) => zone.Size ?? Vector3.One;

    /// <summary>The rotation of a zone from its angles in degrees, or no rotation (GameItemDb.cs:1647-1661).</summary>
    private static Matrix4x4 RotationOf(Universe.Zone zone) =>
        zone.Rotate is { } degrees ? MathHelper.MatrixFromEulerDegrees(degrees) : Matrix4x4.Identity;
}
