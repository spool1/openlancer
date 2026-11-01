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

    /// <summary>The <c>[Solar] type</c> of the archetype of an object in W, in lowercase, or null.</summary>
    public static string? ArchetypeType(EditSession session, SectionView view) =>
        Last(view, "archetype") is { } archetype &&
        session.Lists.List("solar_archetype")?.Find(archetype)?.Section is { } solar &&
        session.Get(solar) is { } section
            ? Last(section, "type")?.ToLowerInvariant()
            : null;

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
        {
            return type switch
            {
                "jump_gate" => "jump gate",
                "jump_hole" or "jumphole" => "jump hole",
                "airlock_gate" => "airlock gate",
                _ => "trade lane ring",
            };
        }
        if (view.Lines("goto").Count > 0)
            return "jump gate";
        return view.Lines("prev_ring").Count > 0 || view.Lines("next_ring").Count > 0 ? "trade lane ring" : null;
    }

    /// <summary>
    /// Why the editor refuses an action on a connection object, with its partner (decision D253). For example: "Li01_to_Li02
    /// is a jump gate. This version cannot delete jump gates, jump holes or trade lane rings. The partner gate Li02:
    /// Li02_to_Li01 and the route files must change with it."
    /// </summary>
    /// <param name="verb">The action, for example "delete" or "copy".</param>
    public static string ConnectionMessage(string verb, SectionRef section, SectionView view, string kind)
    {
        var text = $"{section.Id.Key} is a {kind}. This version cannot {verb} jump gates, jump holes or trade lane rings.";
        if (view.Lines("goto").LastOrDefault() is { Values.Count: >= 2 } line)
        {
            var partner = kind == "jump hole" ? "hole" : "gate";
            return text + $" The partner {partner} {line.Values[0]}: {line.Values[1]} and the route files must change with it.";
        }
        var rings = new[] { Last(view, "prev_ring"), Last(view, "next_ring") }.OfType<string>().ToList();
        return rings.Count > 0
            ? text + $" Its neighbor rings {string.Join(" and ", rings)} must change with it."
            : text;
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
