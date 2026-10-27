using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.World;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Scene;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>A shape that matters for the start of Play from camera: an object or a damage zone.</summary>
/// <param name="Name">The name of the item for the Console, for example "Planet Manhattan".</param>
/// <param name="Span">Gives the part of a ray in the shape.</param>
/// <param name="Damage">The damage per second of a zone, or null for an object.</param>
internal sealed record SpawnShape(string Name, Func<Ray, RaySpan> Span, float? Damage)
{
    /// <summary>True when the ship must not start in the shape: an object, or a death zone (see
    /// <see cref="SpawnPoint.DeathDamage"/>). The ship can start in another damage zone, and the Console warns.</summary>
    public bool Blocks => Damage is not { } damage || damage >= SpawnPoint.DeathDamage;

    /// <summary>True when the shape holds a point. The direction can be any direction that is not zero.</summary>
    public bool Holds(Vector3 point, Vector3 direction) => Span(new Ray(point, direction)).HoldsOrigin;
}

/// <summary>The result of <see cref="SpawnPoint.Clear"/>.</summary>
/// <param name="Position">The start point.</param>
/// <param name="Distance">The move along the view direction: above 0 ahead of the camera, below 0 behind it.</param>
/// <param name="MovedOutOf">The name of the first blocking shape that holds the camera, or null when the start is the
/// camera position.</param>
/// <param name="StillIn">The name of a blocking shape that holds the start, or null. Only a start that did not get out
/// in <see cref="SpawnPoint.MaxMoves"/> moves in either direction has one.</param>
internal readonly record struct SpawnMove(Vector3 Position, float Distance, string? MovedOutOf, string? StillIn);

/// <summary>The start of Play from camera, and the Console lines of its check.</summary>
/// <param name="Lines">Why the start moved, a start that is still in a blocking shape, and each damage zone that holds
/// the start. Empty for a start at the camera outside each shape.</param>
internal sealed record SpawnCheck(PlayStart.InSpace Start,
    IReadOnlyList<(ConsoleSeverity Severity, string Message)> Lines);

/// <summary>
/// The start point of Play from camera (section 9.5 of the M4 design, decision D40). The camera can be inside a planet
/// or a station, or in the death zone around a planet. Then the start moves along the view axis until it is outside each
/// blocking shape.
/// </summary>
internal static class SpawnPoint
{
    /// <summary>The distance between the start and the shape that it leaves.</summary>
    public const float Margin = 100;

    /// <summary>The number of moves in one direction. A direction that is still in a shape after them is not
    /// clear.</summary>
    public const int MaxMoves = 16;

    /// <summary>
    /// The damage per second from which a zone is a death zone (decision D40). The engine applies the damage of a zone
    /// per second to the hull. In retail, each hazard cloud has 300 or less, and nearly each zone with 1,000 or more is a
    /// death zone around a sun or a planet. No zone has a value between them. The default ship has 1,100 hit points.
    /// </summary>
    public const float DeathDamage = 1_000;

    /// <summary>
    /// The shapes of a system: the pick shape of each object (see <see cref="ScenePicker.ObjectSpan"/>), then the volume
    /// of each zone with damage. A zone without damage does not harm a ship, so it is not a shape.
    /// </summary>
    /// <param name="name">The name of an object or a zone for the Console.</param>
    public static IReadOnlyList<SpawnShape> Shapes(IReadOnlyDictionary<SystemObject, GameObject> objects,
        IEnumerable<Zone> zones, Func<NicknameItem, string> name) =>
    [
        .. objects.Select(o =>
            new SpawnShape(name(o.Key), ray => ScenePicker.ObjectSpan(o.Key, o.Value, ray), null)),
        .. zones.Where(z => z.Damage > 0).Select(z =>
            new SpawnShape(name(z), ray => ScenePicker.ZoneSpan(z, ray), z.Damage)),
    ];

    /// <summary>
    /// The shapes of a system from the game data alone: the volume of each zone with damage. A mission play uses them
    /// for a start point in a system that the Scene does not show. The pick shapes of the objects need the models of the
    /// Scene, so planets and stations are not in the list. The death zone of a sun or a planet holds its body, so a start
    /// outside the death zone is also outside the body.
    /// </summary>
    public static IReadOnlyList<SpawnShape> ZoneShapes(StarSystem system, Func<NicknameItem, string> name) =>
        Shapes(new Dictionary<SystemObject, GameObject>(), system.Zones, name);

    /// <summary>The name of an item in the lines of a start check: its name in the game, else its nickname.</summary>
    public static Func<NicknameItem, string> NameOf(EditorSession? session) =>
        item => session?.ItemName(item) is { Length: > 0 } name ? name : item.Nickname;

    /// <summary>
    /// The start of Play from camera: the camera position, moved out of the blocking shapes along the view axis (see
    /// <see cref="Clear"/>), with the orientation of the camera. The lines tell why the start moved. They warn when a
    /// blocking shape still holds the start, and for each damage zone that holds it.
    /// </summary>
    /// <param name="camera">The system, the position and the orientation of the camera. The camera looks along the -Z
    /// of its orientation.</param>
    public static SpawnCheck FromCamera(PlayStart.InSpace camera, IReadOnlyList<SpawnShape> shapes) =>
        Check(camera, shapes, "Play from camera", "the camera", "the view axis");

    /// <summary>
    /// The same check for the start point of a mission play in space (section 5.3, step 7 of the M6c design), for
    /// example a point near a spawn of the mission or near a jump gate. A rule of the start plan gives the point, so it
    /// can be inside a planet, a station or a death zone.
    /// </summary>
    /// <param name="start">The system, the point and the heading of the start. The ship looks along the -Z of the
    /// heading.</param>
    public static SpawnCheck FromPoint(PlayStart.InSpace start, IReadOnlyList<SpawnShape> shapes) =>
        Check(start, shapes, "Mission play", "the start point", "the heading of the ship");

    /// <param name="who">The start of each line, for example "Play from camera".</param>
    /// <param name="point">The point in words, for example "the camera".</param>
    /// <param name="axis">The axis of the moves in words, for example "the view axis".</param>
    private static SpawnCheck Check(PlayStart.InSpace start, IReadOnlyList<SpawnShape> shapes, string who, string point,
        string axis)
    {
        var forward = Vector3.Transform(-Vector3.UnitZ, start.Orientation);
        var move = Clear(start.Position, forward, shapes);
        var lines = new List<(ConsoleSeverity, string)>();
        if (move.MovedOutOf is { } inside)
        {
            var units = MathF.Abs(move.Distance).ToString("N0", CultureInfo.InvariantCulture);
            var place = $"{units} units {(move.Distance >= 0 ? "ahead of" : "behind")} {point}";
            lines.Add(move.StillIn is { } still
                ? (ConsoleSeverity.Warning, $"{who}: {point} is inside {inside}. In {MaxMoves} moves along {axis}, the " +
                                            $"start did not get out of each shape. The ship starts {place}, inside " +
                                            $"{still}.")
                : (ConsoleSeverity.Info, $"{who}: {point} is inside {inside}. The ship starts {place}."));
        }

        foreach (var zone in shapes.Where(s => !s.Blocks && s.Holds(move.Position, forward)))
        {
            var damage = zone.Damage!.Value.ToString("0.###", CultureInfo.InvariantCulture);
            lines.Add((ConsoleSeverity.Warning,
                $"{who}: the ship starts in {zone.Name}, which does {damage} damage per second."));
        }

        return new SpawnCheck(start with { Position = move.Position }, lines);
    }

    /// <summary>
    /// A start point outside each blocking shape. When a blocking shape holds the camera, the start moves along +forward,
    /// and also along -forward. In each direction, while blocking shapes hold the point, the point moves to the farthest
    /// exit of these shapes plus <see cref="Margin"/>. The start takes the direction with the shorter move, and ahead for
    /// a tie. A direction that is not clear counts only when the other direction is not clear either. Thus a camera in
    /// the death zone of a planet that looks away from the planet moves ahead and not across the planet.
    /// </summary>
    /// <param name="forward">The view direction of the camera.</param>
    public static SpawnMove Clear(Vector3 position, Vector3 forward, IReadOnlyList<SpawnShape> shapes)
    {
        // A distance along a ray is in units of the length of its direction.
        forward = Vector3.Normalize(forward);
        var blocking = shapes.Where(s => s.Blocks).ToList();
        var movedOutOf = blocking.FirstOrDefault(s => s.Holds(position, forward))?.Name;
        if (movedOutOf == null)
        {
            return new SpawnMove(position, 0, null, null);
        }

        var ahead = Move(position, forward, blocking);
        var back = Move(position, -forward, blocking);
        var useAhead = (ahead.StillIn == null) != (back.StillIn == null)
            ? ahead.StillIn == null
            : ahead.Distance <= back.Distance;
        var (distance, stillIn) = useAhead ? ahead : (-back.Distance, back.StillIn);
        return new SpawnMove(position + forward * distance, distance, movedOutOf, stillIn);
    }

    /// <summary>The move in one direction, and the name of a shape that still holds its end, or null when the end is
    /// outside each shape.</summary>
    private static (float Distance, string? StillIn) Move(Vector3 position, Vector3 direction,
        IReadOnlyList<SpawnShape> shapes)
    {
        var moved = 0f;
        for (var move = 0;; move++)
        {
            var ray = new Ray(position + direction * moved, direction);
            var exit = float.NegativeInfinity;
            string? holder = null;
            foreach (var shape in shapes)
            {
                if (shape.Span(ray) is { HoldsOrigin: true } span)
                {
                    exit = Math.Max(exit, span.Exit);
                    holder ??= shape.Name;
                }
            }

            if (holder == null || move == MaxMoves)
            {
                return (moved, holder);
            }

            moved += exit + Margin;
        }
    }
}
