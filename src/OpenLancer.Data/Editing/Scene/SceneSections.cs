using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>The kind of a section of a system file that the Scene shows as an item.</summary>
internal enum SceneKind
{
    Object,
    Zone,
    Light,
    Nebula,
    Asteroids,
}

/// <summary>The scene items of the sections of a system file in W, and their connection kind (milestone M3).</summary>
internal static class SceneSections
{
    public const string ObjectType = "Object";
    public const string ZoneType = "zone";
    public const string LightType = "LightSource";

    /// <summary>The kind of a section of a system file, or null for another section or another file.</summary>
    public static SceneKind? KindOf(EditSession session, SectionRef section)
    {
        if (!session.IsSystemFile(section.Path))
            return null;
        return section.Id.Type.ToLowerInvariant() switch
        {
            "object" => SceneKind.Object,
            "zone" => SceneKind.Zone,
            "lightsource" => SceneKind.Light,
            "nebula" => SceneKind.Nebula,
            "asteroids" => SceneKind.Asteroids,
            _ => null,
        };
    }

    /// <summary>The first value of the last line of a key, trimmed, or null.</summary>
    public static string? Last(SectionView view, string key, int index = 0) =>
        view.Lines(key).LastOrDefault(l => l.Values.Count > index)?.Values[index].ToString().Trim() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>The value of the last <c>pos</c> line with three numbers, or null.</summary>
    public static System.Numerics.Vector3? Position(SectionView view) =>
        view.Lines("pos").LastOrDefault(l => l.Values.Count >= 3)?.Values is { } p && p[0].TryToSingle(out var x) &&
        p[1].TryToSingle(out var y) && p[2].TryToSingle(out var z)
            ? new System.Numerics.Vector3(x, y, z)
            : null;

    /// <summary>The <c>[Solar] type</c> of the archetype of an object in W, in lowercase, or null.</summary>
    public static string? ArchetypeType(EditSession session, SectionView view) =>
        SolarOf(session, view) is { } solar ? Last(solar, "type")?.ToLowerInvariant() : null;

    /// <summary>The <c>[Solar]</c> of the archetype of an object in W, or null.</summary>
    public static SectionView? SolarOf(EditSession session, SectionView view) =>
        Last(view, "archetype") is { } archetype &&
        session.Lists.List("solar_archetype")?.Find(archetype)?.Section is { } solar
            ? session.Get(solar)
            : null;

    /// <summary>
    /// The <c>solar_radius</c> of a <c>[Solar]</c> in meters, or null when the last line of the key has no finite
    /// number. This is the one reader of the key: the plans of the trade lanes, their placement checks and the agent
    /// tools use it.
    /// </summary>
    public static float? SolarRadius(SectionView solar) =>
        solar.Lines("solar_radius").LastOrDefault() is { Values.Count: > 0 } line &&
        float.TryParse(line.Values[0].ToString().Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var radius) && float.IsFinite(radius)
            ? radius
            : null;

    /// <summary>The <c>solar_radius</c> of the archetype of an object in W in meters, or null (see
    /// <see cref="SolarRadius"/>).</summary>
    public static float? RadiusOf(EditSession session, SectionView view) =>
        SolarOf(session, view) is { } solar ? SolarRadius(solar) : null;

    /// <summary>The tool that makes a jump gate pair or a jump hole pair, as the texts of the editor name it.</summary>
    public const string PairTool = "the jump connection tool connection_create of the agent server";

    /// <summary>The tool that sets the lock of a jump gate or a jump hole, as the texts of the editor name it.</summary>
    public const string LockTool = "the jump connection tool connection_set_lock of the agent server";

    /// <summary>The tools that make a trade lane and add a ring to one, as the texts of the editor name them.</summary>
    public const string LaneTool = "the trade lane tools trade_lane_create and trade_lane_insert_ring of the agent server";

    /// <summary>The kind of a trade lane ring in plain words (see <see cref="ConnectionKind"/>).</summary>
    public const string LaneRingKind = "trade lane ring";

    /// <summary>
    /// The kind of a connection object in plain words, or null for another section (decision D253): an object with a
    /// <c>goto</c>, a <c>prev_ring</c> or a <c>next_ring</c> line, or an archetype of a connection type.
    /// </summary>
    public static string? ConnectionKind(EditSession session, SectionView view)
    {
        if (!view.Type.Equals(ObjectType, StringComparison.OrdinalIgnoreCase))
            return null;
        var type = ArchetypeType(session, view);
        if (type != null && session.Lists.RuleData.ConnectionTypes.Contains(type))
            return ConnectionWords(type);
        if (view.Lines("goto").Count > 0)
            return "jump gate";
        return view.Lines("prev_ring").Count > 0 || view.Lines("next_ring").Count > 0 ? LaneRingKind : null;
    }

    /// <summary>True for a trade lane ring (see <see cref="ConnectionKind"/>). It is an object with an archetype of the
    /// type <c>TRADELANE_RING</c>, or with a <c>prev_ring</c> or a <c>next_ring</c> line and no other connection.</summary>
    public static bool IsLaneRing(EditSession session, SectionView view) => ConnectionKind(session, view) == LaneRingKind;

    /// <summary>The kind of a connection type of the rule data in plain words, for example "jump hole" for
    /// <c>jumphole</c>.</summary>
    /// <param name="type">The <c>[Solar] type</c> in lowercase.</param>
    public static string ConnectionWords(string type) => type switch
    {
        "jump_gate" => "jump gate",
        "jump_hole" or "jumphole" => "jump hole",
        "airlock_gate" => "airlock gate",
        _ => LaneRingKind,
    };

    /// <summary>True for a connection kind that goes as a pair: a jump gate or a jump hole. A delete of one end
    /// deletes the pair (see <see cref="OpenLancer.Data.Editing.Universe.ConnectionPlans.DeletePair"/>).</summary>
    public static bool IsPairKind(string kind) => kind is "jump gate" or "jump hole";

    /// <summary>A kind in plain words with its article, for example "an airlock gate".</summary>
    public static string WithArticle(string kind) => (kind.Length > 0 && "aeiou".Contains(kind[0]) ? "an " : "a ") + kind;

    /// <summary>
    /// Why the editor refuses an action on a connection object (decision D253). A jump gate, a jump hole or a trade
    /// lane ring cannot get a copy. A copy has no partner, or it is not a part of the lane. In this version, an
    /// airlock gate takes only the rename. For example: "Li01_to_Li02 is a jump gate. This version cannot copy jump
    /// gates or jump holes, because a copy has no partner. To make a pair, use the jump connection tool
    /// connection_create of the agent server."
    /// </summary>
    /// <param name="verb">The action, for example "copy" or "delete". For a jump gate, a jump hole or a trade lane ring,
    /// it is an action that makes copies. The editor deletes their pairs and relinks their lanes.</param>
    public static string ConnectionMessage(string verb, SectionRef section, string kind)
    {
        var name = section.Id.Key;
        if (IsPairKind(kind))
            return $"{name} is a {kind}. This version cannot {verb} jump gates or jump holes, because a copy has no " +
                   $"partner. To make a pair, use {PairTool}.";
        if (kind == LaneRingKind)
            return $"{name} is a trade lane ring. This version cannot {verb} trade lane rings, because a copy is not a " +
                   $"part of the lane. To make a lane or a ring, use {LaneTool}.";
        return $"{name} is {WithArticle(kind)}. This version cannot {verb} {kind}s.";
    }

    /// <summary>The label of an item for a message, for example "the nebula of Zone_Bw05_Crow_nebula".</summary>
    public static string Label(SceneKind kind, SectionRef section) => kind switch
    {
        SceneKind.Nebula => $"the nebula of {section.Id.Key}",
        SceneKind.Asteroids => $"the asteroid field of {section.Id.Key}",
        _ => section.Id.Key ?? section.Id.ToString(),
    };

    /// <summary>The file name of an install path, for a message.</summary>
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>The kinds of the items that the Scene shows as objects or zones: they have a name in the shared name
    /// space of decision D265.</summary>
    public static bool IsNamed(SceneKind kind) => kind is SceneKind.Object or SceneKind.Zone;

    /// <summary>The list of the names of a kind, or null for a kind without a list.</summary>
    public static string? ListOf(SceneKind kind) => kind switch
    {
        SceneKind.Object => "object_of_system",
        SceneKind.Zone => "zone_of_system",
        _ => null,
    };

    public static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The distinct texts of a list, in order, for a message.</summary>
    public static string Join(IEnumerable<string> texts) => string.Join(", ", texts.Distinct(StringComparer.OrdinalIgnoreCase));
}
