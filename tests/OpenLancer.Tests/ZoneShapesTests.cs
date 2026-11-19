using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using Xunit;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Tests;

/// <summary>
/// The zone math of <see cref="ZoneShapes"/> (shared step SH6) for each shape. The tests cover the points inside, the
/// immersion with the retail rule, the size in meters and the size line for a size in meters. The zones are
/// invented.
/// </summary>
public sealed class ZoneShapesTests
{
    private static Universe.Zone Zone(Universe.ZoneShape shape, Vector3 size, float? edge = 0.25f, Vector3? pos = null,
        Vector3? rotate = null) => new()
    {
        Nickname = "zone_test",
        Shape = shape,
        Size = size,
        EdgeFraction = edge,
        Pos = pos,
        Rotate = rotate,
    };

    [Fact]
    public void ASphereUsesItsRadius()
    {
        var zone = Zone(Universe.ZoneShape.SPHERE, new Vector3(1000), pos: new Vector3(100, 0, 0));

        Assert.True(ZoneShapes.Contains(zone, new Vector3(1099, 0, 0)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(1101, 0, 0)));
        Assert.Equal(new Vector3(2000), ZoneShapes.Extent(zone));
        // The scaled distance is the distance divided by the radius. The fade band is the outer quarter.
        Assert.Equal(1, ZoneShapes.Immersion(zone, new Vector3(100, 0, 0)));
        Assert.Equal(1, ZoneShapes.Immersion(zone, new Vector3(100, 700, 0)));
        Assert.Equal(0.8f, ZoneShapes.Immersion(zone, new Vector3(100, 0, 800)), 4);
        Assert.Equal(0, ZoneShapes.Immersion(zone, new Vector3(1100, 0, 0)));
        Assert.Equal(0, ZoneShapes.Immersion(zone, new Vector3(5000, 0, 0)));
    }

    /// <summary>Retail clamps <c>edge_fraction</c> to 0 to 1. A zone without the line has no fade band.</summary>
    [Theory]
    [InlineData(null, 1f)]
    [InlineData(0f, 1f)]
    [InlineData(-1f, 1f)]
    [InlineData(0.00001f, 1f)]
    [InlineData(0.5f, 0.2f)]
    [InlineData(1f, 0.1f)]
    [InlineData(2f, 0.1f)]
    public void TheEdgeFractionSetsTheFadeBand(float? edge, float expected)
    {
        var zone = Zone(Universe.ZoneShape.SPHERE, new Vector3(1000), edge);

        Assert.Equal(expected, ZoneShapes.Immersion(zone, new Vector3(900, 0, 0)), 4);
    }

    /// <summary>An ellipsoid uses its half axes along the axes of the zone. The rotation of 90 degrees turns the long
    /// axis Z of the zone to the X axis of the world.</summary>
    [Fact]
    public void AnEllipsoidUsesItsHalfAxesAfterItsRotation()
    {
        var zone = Zone(Universe.ZoneShape.ELLIPSOID, new Vector3(1000, 500, 2000), rotate: new Vector3(0, 90, 0));

        Assert.True(ZoneShapes.Contains(zone, new Vector3(1900, 0, 0)));
        Assert.True(ZoneShapes.Contains(zone, new Vector3(0, 0, 900)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(0, 0, 1100)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(0, 510, 0)));
        Assert.Equal(new Vector3(2000, 1000, 4000), ZoneShapes.Extent(zone));
        Assert.Equal(1, ZoneShapes.Immersion(zone, new Vector3(0, 250, 0)));
        Assert.Equal(0.4f, ZoneShapes.Immersion(zone, new Vector3(0, 450, 0)), 4);
        Assert.Equal(0.4f, ZoneShapes.Immersion(zone, new Vector3(1800, 0, 0)), 4);
        Assert.Equal(0, ZoneShapes.Immersion(zone, new Vector3(0, 0, 1100)));
    }

    /// <summary>A box uses its full lengths. The retail scaled distance of a box is (s + r²) / r². Here s is the signed
    /// squared distance to the surface, and r is the radius of the bounding sphere. So even the center of a box is in
    /// a wide fade band.</summary>
    [Fact]
    public void ABoxUsesItsLengthsAndTheRetailScaledDistance()
    {
        var zone = Zone(Universe.ZoneShape.BOX, new Vector3(2000, 1000, 4000), 0.5f);

        Assert.True(ZoneShapes.Contains(zone, new Vector3(999, 499, 1999)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(1001, 0, 0)));
        Assert.Equal(new Vector3(2000, 1000, 4000), ZoneShapes.Extent(zone));
        // r² = (2000² + 1000² + 4000²) / 4 = 5250000. The nearest face of the center is 500 m away.
        var center = 1 - 250000f / 5250000f;
        Assert.Equal((1 - center) / 0.5f, ZoneShapes.Immersion(zone, Vector3.Zero), 4);
        Assert.Equal(1, ZoneShapes.Immersion(Zone(Universe.ZoneShape.BOX, new Vector3(2000, 1000, 4000), 0.04f), Vector3.Zero));
        // Outside the box, and inside its bounding sphere.
        Assert.Equal(0, ZoneShapes.Immersion(zone, new Vector3(1100, 0, 0)));
    }

    /// <summary>A cylinder uses its radius and its height. A size line with two values reads as (radius, height,
    /// 0).</summary>
    [Fact]
    public void ACylinderUsesItsRadiusAndItsHeight()
    {
        var zone = Zone(Universe.ZoneShape.CYLINDER, new Vector3(1000, 400, 0));

        Assert.True(ZoneShapes.Contains(zone, new Vector3(700, 199, 700)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(0, 201, 0)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(1001, 0, 0)));
        Assert.Equal(new Vector3(2000, 400, 2000), ZoneShapes.Extent(zone));
        Assert.Equal(1, ZoneShapes.Immersion(zone, new Vector3(500, 0, 0)));
        Assert.Equal(0.4f, ZoneShapes.Immersion(zone, new Vector3(0, 180, 0)), 4);
        Assert.Equal(0.4f, ZoneShapes.Immersion(zone, new Vector3(0, 0, 900)), 4);
        Assert.Equal(0, ZoneShapes.Immersion(zone, new Vector3(0, 210, 0)));
    }

    /// <summary>A ring uses its outer radius, its inner radius and its height, in the order of the line. The scaled
    /// distance of a ring starts at its middle circle.</summary>
    [Fact]
    public void ARingUsesItsRadiiAndItsHeight()
    {
        var zone = Zone(Universe.ZoneShape.RING, new Vector3(2000, 1000, 200));

        Assert.True(ZoneShapes.Contains(zone, new Vector3(1500, 0, 0)));
        Assert.True(ZoneShapes.Contains(zone, new Vector3(0, 90, -1900)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(500, 0, 0)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(2100, 0, 0)));
        Assert.False(ZoneShapes.Contains(zone, new Vector3(1500, 150, 0)));
        Assert.Equal(new Vector3(4000, 200, 4000), ZoneShapes.Extent(zone));
        Assert.Equal(1, ZoneShapes.Immersion(zone, new Vector3(0, 0, 1500)));
        Assert.Equal(0.8f, ZoneShapes.Immersion(zone, new Vector3(1900, 0, 0)), 4);
        Assert.Equal(0.8f, ZoneShapes.Immersion(zone, new Vector3(-1100, 0, 0)), 4);
        Assert.Equal(0.8f, ZoneShapes.Immersion(zone, new Vector3(1500, 80, 0)), 4);
        Assert.Equal(0, ZoneShapes.Immersion(zone, new Vector3(2001, 0, 0)));
        // A point on the axis of the ring is in its hole.
        Assert.Equal(0, ZoneShapes.Immersion(zone, Vector3.Zero));
    }

    /// <summary>The size line of the smallest shape that has at least the extent on each axis, in the order of the
    /// line. The extent of that line holds the extent.</summary>
    [Theory]
    [InlineData(Universe.ZoneShape.SPHERE, new[] { 2000f })]
    [InlineData(Universe.ZoneShape.ELLIPSOID, new[] { 1000f, 2000f, 250f })]
    [InlineData(Universe.ZoneShape.BOX, new[] { 2000f, 4000f, 500f })]
    [InlineData(Universe.ZoneShape.CYLINDER, new[] { 1000f, 4000f })]
    [InlineData(Universe.ZoneShape.RING, new[] { 1000f, 0f, 4000f })]
    public void SizeForGivesTheSizeLineOfAnExtent(Universe.ZoneShape shape, float[] line)
    {
        var extent = new Vector3(2000, 4000, 500);

        var size = ZoneShapes.SizeFor(shape, extent);

        Assert.Equal(line, size.ToArray());
        var zone = Zone(shape, size.Count switch
        {
            1 => new Vector3(size[0]),
            2 => new Vector3(size[0], size[1], 0),
            _ => new Vector3(size[0], size[1], size[2]),
        });
        var made = ZoneShapes.Extent(zone);
        Assert.True(made.X >= extent.X && made.Y >= extent.Y && made.Z >= extent.Z, made.ToString());
    }

    /// <summary>The zone is W as the engine reads its lines. An edit shows at once, and a section with a check error
    /// gives no zone.</summary>
    [Fact]
    public void ReadGivesTheZoneOfW()
    {
        using var ws = NebulaFiles.Create();
        var session = ws.Session("act");
        var cloud = ListWorkspace.Li01Section("Zone", "Zone_cloud");

        Assert.True(ZoneShapes.Contains(ZoneShapes.Read(session, cloud)!, new Vector3(4999, 0, 0)));
        session.Execute(EditCommand.SetLinesFromText(cloud, "size", ["100"]));
        Assert.False(ZoneShapes.Contains(ZoneShapes.Read(session, cloud)!, new Vector3(4999, 0, 0)));
        Assert.Equal(new Vector3(200), ZoneShapes.Extent(ZoneShapes.Read(session, cloud)!));
        session.Execute(EditCommand.SetLinesFromText(cloud, "shape", ["TRIANGLE"]));
        Assert.Null(ZoneShapes.Read(session, cloud));
        Assert.Null(ZoneShapes.Read(session, ListWorkspace.Li01Section("Zone", "no_zone")));
    }
}
