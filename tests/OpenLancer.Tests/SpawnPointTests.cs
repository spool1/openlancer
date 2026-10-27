using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Render;
using LibreLancer.World;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Scene;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The start point of Play from camera (section 9.5 of the M4 design, decision D40), with synthetic shapes. The camera
/// looks along -Z unless a test turns it. A shape has the nickname of its object or zone as its name. A death zone has
/// the damage of a retail planet death zone, 2,000,000 per second.
/// </summary>
public sealed class SpawnPointTests
{
    private const float Death = 2_000_000;

    private static readonly Vector3 Ahead = -Vector3.UnitZ;

    private readonly Dictionary<SystemObject, GameObject> objects = [];
    private readonly List<Zone> zones = [];

    [Fact]
    public void APointOutsideEveryShapeStays()
    {
        AddPlanet("planet", new Vector3(0, 0, -5_000), 1_000);
        AddZone("death", new Vector3(0, 0, 5_000), 1_000, Death);

        // The sphere of the planet is from a depth of 4,000 to 6,000. These points are 10 units outside it.
        foreach (var camera in (Vector3[])[Vector3.Zero, new(0, 0, -3_990), new(0, 0, -6_010), new(1_010, 0, -5_000)])
        {
            Assert.Equal(new SpawnMove(camera, 0, null, null), Clear(camera, Ahead));
        }
    }

    [Fact]
    public void APointNearTheFrontSurfaceOfASphereMovesAheadToThatSurfacePlus100()
    {
        AddPlanet("planet", new Vector3(0, 0, -5_000), 1_000);

        // The exit ahead is 100 units away, and the exit behind is 1,900 units away.
        var move = Clear(new Vector3(0, 0, -5_900), Ahead);

        Moved(move, new Vector3(0, 0, -6_100), 200, "planet");
    }

    [Fact]
    public void APointNearTheBackSurfaceOfASphereMovesBack()
    {
        AddPlanet("planet", new Vector3(0, 0, -5_000), 1_000);

        // The exit ahead is 1,900 units away, and the exit behind is 100 units away.
        var move = Clear(new Vector3(0, 0, -4_100), Ahead);

        Moved(move, new Vector3(0, 0, -3_900), -200, "planet");
    }

    [Fact]
    public void ACameraInADeathZoneThatLooksAwayFromThePlanetMovesAheadAndNotAcrossThePlanet()
    {
        // As a death zone around a planet: the planet has a radius of 4,000, and the zone a radius of 5,000. The camera is
        // between the planet and the edge of the zone, and it looks away from the planet. The exit ahead is 500 units
        // away. Behind the camera, the exit is on the far side of the planet, 9,500 units away.
        AddPlanet("planet", Vector3.Zero, 4_000);
        AddZone("death", Vector3.Zero, 5_000, Death);

        var move = Clear(new Vector3(0, 0, -4_500), Ahead);

        Moved(move, new Vector3(0, 0, -5_100), 600, "death");
    }

    [Fact]
    public void WithTwoOverlappingShapesTheStartEndsOutsideBoth()
    {
        // The first sphere is from a depth of -2,000 to 2,000, and the second from 1,900 to 3,900. The camera is only in
        // the first one. Its exit ahead, plus 100, is in the second one, so the start moves two times: 250, then 1,900.
        // Behind the camera, the exit of the first sphere is 3,850 units away.
        AddPlanet("first", Vector3.Zero, 2_000);
        AddPlanet("second", new Vector3(0, 0, -2_900), 1_000);

        var move = Clear(new Vector3(0, 0, -1_850), Ahead);

        Moved(move, new Vector3(0, 0, -4_000), 2_150, "first");
        Assert.All(Shapes(), s => Assert.False(s.Holds(move.Position, Ahead), s.Name));
    }

    [Fact]
    public void TheBoxOfAStationTurnsWithTheStation()
    {
        // A box 2,000 long on X and 200 thick, turned 90 degrees about Y, lies along Z from a depth of 4,000 to 6,000.
        // Without the turn, the box would not hold the camera.
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        AddObject("station", new Vector3(0, 0, -5_000), ArchetypeType.station, BoxModel(new Vector3(1_000, 100, 100)),
            turned);
        var camera = new Vector3(0, 0, -5_800);

        Moved(Clear(camera, Ahead), new Vector3(0, 0, -6_100), 300, "station");

        // Across the box, the exits are 100 units away on each side. Then the start moves ahead.
        Moved(Clear(camera, Vector3.UnitX), new Vector3(200, 0, -5_800), 200, "station");
    }

    [Fact]
    public void OnlyObjectsAndDeathZonesBlockTheStartAndAZoneWithoutDamageIsNoShape()
    {
        AddZone("field", new Vector3(0, 0, -5_000), 1_000, damage: 0);
        AddZone("cloud", new Vector3(0, 0, -5_000), 2_000, damage: 15);
        AddZone("death", new Vector3(0, 0, 5_000), 1_000, Death);
        AddPlanet("planet", new Vector3(0, 0, 50_000), 1_000);

        Assert.Equal([("planet", true), ("cloud", false), ("death", true)], Shapes().Select(s => (s.Name, s.Blocks)));

        // The field and the cloud hold this camera, and they do not move the start.
        var inCloud = new Vector3(0, 0, -5_900);
        Assert.Equal(new SpawnMove(inCloud, 0, null, null), Clear(inCloud, Ahead));

        Moved(Clear(new Vector3(0, 0, 5_900), Ahead), new Vector3(0, 0, 6_100), -200, "death");
    }

    [Theory]
    [InlineData(SpawnPoint.DeathDamage, true)]
    [InlineData(SpawnPoint.DeathDamage - 1, false)]
    public void AZoneIsADeathZoneFrom1000DamagePerSecond(float damage, bool blocks)
    {
        // Retail has hazard clouds of up to 300 damage per second, and death zones of 1,000 and more.
        Assert.Equal(1_000, SpawnPoint.DeathDamage);
        AddZone("zone", new Vector3(0, 0, -5_000), 1_000, damage);
        var camera = new Vector3(0, 0, -5_900);

        Assert.Equal(blocks, Assert.Single(Shapes()).Blocks);
        Assert.Equal(blocks ? new Vector3(0, 0, -6_100) : camera, Clear(camera, Ahead).Position);
    }

    [Fact]
    public void ACameraInALargeHazardZoneStaysAndTheConsoleWarns()
    {
        // As Zone_Bw04_system_radiation: a sphere with a radius of 40,000 and 15 damage per second. Its edge is 35,000
        // units from the camera.
        AddZone("Zone_Bw04_system_radiation", Vector3.Zero, 40_000, damage: 15);
        var camera = new PlayStart.InSpace("bw04", new Vector3(0, 0, -5_000), Quaternion.Identity);

        var check = SpawnPoint.FromCamera(camera, Shapes());

        Assert.Equal(camera, check.Start);
        Assert.Equal([(ConsoleSeverity.Warning, "Play from camera: the ship starts in Zone_Bw04_system_radiation, which " +
                                                "does 15 damage per second.")], check.Lines);
    }

    [Fact]
    public void AfterAMoveTheConsoleWarnsForEachDamageZoneThatHoldsTheStart()
    {
        // The camera is in the planet and in both clouds. The start moves out of the planet to a depth of 6,100. The
        // large cloud holds the start, and the small cloud does not.
        AddPlanet("planet", new Vector3(0, 0, -5_000), 1_000);
        AddZone("small", new Vector3(0, 0, -5_000), 1_050, damage: 300);
        AddZone("large", new Vector3(0, 0, -5_000), 2_000, damage: 0.5f);
        var camera = new PlayStart.InSpace("li01", new Vector3(0, 0, -5_900), Quaternion.Identity);

        var check = SpawnPoint.FromCamera(camera, Shapes());

        Near(new Vector3(0, 0, -6_100), check.Start.Position);
        Assert.Equal(
        [
            (ConsoleSeverity.Info, "Play from camera: the camera is inside planet. The ship starts 200 units ahead of " +
                                   "the camera."),
            (ConsoleSeverity.Warning, "Play from camera: the ship starts in large, which does 0.5 damage per second."),
        ], check.Lines);
    }

    /// <summary>The start point of a mission play gets the same check, with texts about the start point (review fix of
    /// M6c-2). The ship looks along the -Z of the heading of the start.</summary>
    [Fact]
    public void TheStartPointOfAMissionPlayGetsTheSameCheck()
    {
        AddPlanet("planet", new Vector3(0, 0, -5_000), 1_000);
        AddZone("large", new Vector3(0, 0, -5_000), 2_000, damage: 0.5f);
        var start = new PlayStart.InSpace("li01", new Vector3(0, 0, -5_900), Quaternion.Identity);

        var check = SpawnPoint.FromPoint(start, Shapes());

        Near(new Vector3(0, 0, -6_100), check.Start.Position);
        Assert.Equal(
        [
            (ConsoleSeverity.Info, "Mission play: the start point is inside planet. The ship starts 200 units ahead of " +
                                   "the start point."),
            (ConsoleSeverity.Warning, "Mission play: the ship starts in large, which does 0.5 damage per second."),
        ], check.Lines);
        Assert.All(check.Lines, l => Assert.Empty(SteLint.Problems(l.Message)));
    }

    [Theory]
    [InlineData(16, -2_600)]
    [InlineData(17, 5_100)]
    public void ADirectionThatIsNotClearAfter16MovesLosesToAClearDirection(int chain, float depth)
    {
        // A chain of death zones ahead (see AddChain). A chain of n zones needs n moves, and the start then is at a depth
        // of 160 n + 40. Behind the camera, a large zone ends 5,000 units away. With 16 zones, the shorter move ahead is
        // clear. With 17 zones, the start is still in a zone after 16 moves ahead, so it moves back, although that move
        // is longer.
        AddChain("ahead", chain, Ahead);
        AddZone("behind", new Vector3(0, 0, 2_475), 2_525, Death);

        var move = Clear(Vector3.Zero, Ahead);

        Moved(move, new Vector3(0, 0, depth), -depth, "ahead0");
    }

    [Fact]
    public void WhenNeitherDirectionIsClearTheConsoleWarnsThatTheShipStartsInAShape()
    {
        // Chains of 17 death zones ahead and behind the camera: no direction gets clear in 16 moves. Both moves are 2,600
        // units, so the start takes ahead.
        AddChain("ahead", 17, Ahead);
        AddChain("behind", 17, -Ahead, first: 1);
        var camera = new PlayStart.InSpace("li01", Vector3.Zero, Quaternion.Identity);

        var move = Clear(camera.Position, Ahead);
        var check = SpawnPoint.FromCamera(camera, Shapes());

        Assert.Equal("ahead16", move.StillIn);
        Near(new Vector3(0, 0, -2_600), check.Start.Position);
        Assert.Equal([(ConsoleSeverity.Warning, "Play from camera: the camera is inside ahead0. In 16 moves along the " +
                                                "view axis, the start did not get out of each shape. The ship starts " +
                                                "2,600 units ahead of the camera, inside ahead16.")], check.Lines);
    }

    [Theory]
    [InlineData(900, 200, "ahead of")]
    [InlineData(-900, -200, "behind")]
    public void TheStartOfATurnedSceneCameraMovesAlongItsView(float offset, float distance, string side)
    {
        // A planet with a radius of 1,000. The camera looks along a direction that is not an axis, and it is 900 units
        // from the center along that direction or against it. So the near exit is 100 units ahead or behind it.
        var center = new Vector3(20_000, -3_000, 7_000);
        AddPlanet("planet", center, 1_000);
        var direction = Vector3.Normalize(new Vector3(1, -0.5f, -2));
        var scene = new SceneCamera();
        scene.Frame(center, 1_000);
        scene.LookAlong(direction);
        scene.Focus(center + direction * (offset + 500), 500);
        var camera = new PlayStart.InSpace("li01", scene.Position, scene.Orientation);

        var check = SpawnPoint.FromCamera(camera, Shapes());

        Near(center + direction * offset, camera.Position, 0.5f);
        Near(camera.Position + direction * distance, check.Start.Position, 0.5f);
        Assert.Equal(camera with { Position = check.Start.Position }, check.Start);
        Assert.Equal([(ConsoleSeverity.Info, $"Play from camera: the camera is inside planet. The ship starts 200 " +
                                             $"units {side} the camera.")], check.Lines);
    }

    [Fact]
    public void AShipCanStartInTheHoleOfARing()
    {
        // A ring zone stores (outer radius, height, inner radius). In the frame of the ring, the camera is at a depth of
        // 650 in its wall, which is from 600 to 1,000. The exit ahead is 350 units away. Behind the camera, the wall ends
        // at the hole after 50 units.
        AddZone("ring", ShapeKind.Ring, new Vector3(0, 0, -10_000), new Vector3(1_000, 200, 600), Death);

        var move = Clear(new Vector3(0, 0, -10_650), Ahead);

        Moved(move, new Vector3(0, 0, -10_500), -150, "ring");
    }

    /// <summary>A start point in a system that the Scene does not show gets the check of the zones of the game data
    /// (fix of 2026-10-27). A death zone around a sun at the center holds a point near the center, so the start moves
    /// out of it.</summary>
    [Fact]
    public void AStartPointInASystemThatTheSceneDoesNotShowGetsTheCheckOfTheZones()
    {
        AddZone("sun_death", Vector3.Zero, 4_000, Death);
        AddZone("field", Vector3.Zero, 10_000, damage: 0);
        var system = new StarSystem { Nickname = "ol_sys", SourceFile = "" };
        system.Zones.AddRange(zones);

        // Without a session, the lines name each item by its nickname.
        var shapes = SpawnPoint.ZoneShapes(system, SpawnPoint.NameOf(null));
        var check = SpawnPoint.FromPoint(new PlayStart.InSpace("ol_sys", new Vector3(0, 0, 1_000), Quaternion.Identity),
            shapes);

        Assert.Equal(["sun_death"], shapes.Select(s => s.Name));
        Near(new Vector3(0, 0, 4_100), check.Start.Position);
        Assert.Equal((ConsoleSeverity.Info, "Mission play: the start point is inside sun_death. The ship starts 3,100 " +
                                            "units behind the start point."), Assert.Single(check.Lines));
    }

    private IReadOnlyList<SpawnShape> Shapes() => SpawnPoint.Shapes(objects, zones, item => item.Nickname);

    private SpawnMove Clear(Vector3 camera, Vector3 forward) => SpawnPoint.Clear(camera, forward, Shapes());

    /// <summary>A move out of a shape to a clear start.</summary>
    private static void Moved(SpawnMove move, Vector3 position, float distance, string inside)
    {
        Near(position, move.Position);
        Assert.Equal(distance, move.Distance, 0.01f);
        Assert.Equal(inside, move.MovedOutOf);
        Assert.Null(move.StillIn);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.05f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"Expected {expected}, but got {actual}.");

    /// <summary>
    /// Death zones with a radius of 100 along a direction from the origin, 160 units apart, from the zone with the
    /// number <paramref name="first"/> to the zone with the number <paramref name="count"/> - 1. From the origin, move m
    /// ends at 160 m + 40, inside zone m and 20 units before zone m + 1. So each move leaves one zone. A space of 150
    /// would put the end of a move on the near surface of the next zone, which then holds it too.
    /// </summary>
    private void AddChain(string name, int count, Vector3 direction, int first = 0)
    {
        for (var i = first; i < count; i++)
        {
            AddZone($"{name}{i}", direction * (160 * i), 100, Death);
        }
    }

    private void AddPlanet(string nickname, Vector3 position, float radius) =>
        AddObject(nickname, position, ArchetypeType.planet,
            Model(BoundingBox.CreateFromSphere(new BoundingSphere(Vector3.Zero, radius)), radius, RigidModelSource.Sphere),
            Quaternion.Identity);

    private void AddObject(string nickname, Vector3 position, ArchetypeType type, RigidModel model,
        Quaternion rotation)
    {
        var obj = new SystemObject
        {
            Nickname = nickname,
            Position = position,
            Rotation = rotation,
            Archetype = new Archetype { Nickname = nickname, Type = type },
        };
        var gameObject = new GameObject { RenderComponent = new ModelRenderer(model) };
        gameObject.SetTransform(new Transform3D(position, rotation));
        objects[obj] = gameObject;
    }

    private void AddZone(string nickname, Vector3 position, float radius, float damage) =>
        AddZone(nickname, ShapeKind.Sphere, position, new Vector3(radius), damage);

    private void AddZone(string nickname, ShapeKind shape, Vector3 position, Vector3 size, float damage)
    {
        var zone = new Zone
        {
            Nickname = nickname, Shape = shape, RotationMatrix = Matrix4x4.Identity, Size = size, Position = position,
        };
        zone.Damage = damage;
        zones.Add(zone);
    }

    /// <summary>A model with one part whose mesh has a box from -half to half.</summary>
    private static RigidModel BoxModel(Vector3 half) =>
        Model(new BoundingBox(-half, half), half.Length(), RigidModelSource.SinglePart);

    private static RigidModel Model(BoundingBox box, float radius, RigidModelSource source)
    {
        var part = new RigidModelPart { Mesh = new VisualMesh { BoundingBox = box, Radius = radius } };
        var model = new RigidModel { Root = part, AllParts = [part], Source = source };
        model.UpdateTransform();
        return model;
    }
}
