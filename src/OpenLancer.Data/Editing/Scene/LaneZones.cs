using System;
using System.Numerics;
using LibreLancer.Data.GameData.World;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The retail rule of the zone of a trade lane, where lane traffic spawns. The plans of the trade lanes, the checks and
/// the Scene of the editor use this one rule.
/// </summary>
/// <remarks>Retail facts: the content layer uses a zone as the zone of a trade lane when two things are true. Its
/// nickname has the word "Tradelane" with this letter case. It is a box that is more than 10 times longer on its z axis
/// than on its x axis and on its y axis. The game does not read <c>lane_id</c> (decision D513 of the console filter
/// design), so the rule does not use it. 129 zones of the retail system files meet the rule.</remarks>
public static class LaneZones
{
    /// <summary>The word that the nickname of a lane zone holds, with this letter case.</summary>
    public const string Word = "Tradelane";

    /// <summary>How many times longer than wide and high a lane zone is, at least. The z size is more than this value
    /// times the x size and the y size.</summary>
    public const int Thinness = 10;

    /// <summary>True when the game uses a zone as the zone of a trade lane.</summary>
    /// <param name="nickname">The nickname of the zone.</param>
    /// <param name="shape">The shape of the zone.</param>
    /// <param name="size">The size of the zone as the engine keeps it (see <see cref="ZoneShapes.SizeOf"/>). A box has
    /// its three lengths in the order of the line.</param>
    /// <remarks>Retail does not round the products of the sizes to float precision. A product in float precision can
    /// round up to the z size and give another result at the limit. A double holds the exact product.</remarks>
    public static bool IsLaneZone(string nickname, ShapeKind shape, Vector3 size) =>
        nickname.Contains(Word, StringComparison.Ordinal) && shape == ShapeKind.Box &&
        (double)size.X * Thinness < size.Z && (double)size.Y * Thinness < size.Z;

    /// <summary>True when a <c>[zone]</c> of W is the zone of a trade lane (see
    /// <see cref="IsLaneZone(string, ShapeKind, Vector3)"/>). A zone that the engine cannot read is not.</summary>
    internal static bool IsLaneZone(EditSession session, SectionRef zone) => Read(session, zone) != null;

    /// <summary>A <c>[zone]</c> of W as the engine reads it (see <see cref="ZoneShapes.Read"/>), when it is the zone
    /// of a trade lane, or null.</summary>
    internal static LibreLancer.Data.Schema.Universe.Zone? Read(EditSession session, SectionRef zone) =>
        zone.Id.Key?.Contains(Word, StringComparison.Ordinal) == true && ZoneShapes.Read(session, zone) is { } read &&
        IsLaneZone(read.Nickname, ZoneShapes.ShapeOf(read.Shape), ZoneShapes.SizeOf(read.Size, ZoneShapes.ShapeOf(read.Shape)))
            ? read
            : null;
}
