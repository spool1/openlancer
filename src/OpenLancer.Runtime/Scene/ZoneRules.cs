using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Runtime.Scene;

/// <summary>The rules of the retail game that read the values of a zone.</summary>
public static class ZoneRules
{
    /// <summary>
    /// True when the game uses a zone as the zone of a trade lane, by the one retail rule of
    /// <see cref="LaneZones.IsLaneZone(string, ShapeKind, System.Numerics.Vector3)"/>: its nickname has the word
    /// "Tradelane" with this letter case, and it is a box that is more than 10 times longer on its z axis than on its x
    /// axis and on its y axis. Most trade lane zones have a text such as <c>br01_2</c> in <c>lane_id</c>, and the engine
    /// reads such a text in a text file as 0. The rule does not use that key.
    /// </summary>
    public static bool IsTradeLaneZone(Zone zone) => LaneZones.IsLaneZone(zone.Nickname, zone.Shape, zone.Size);
}
