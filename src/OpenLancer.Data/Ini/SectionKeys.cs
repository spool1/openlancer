using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Ini;

internal enum SectionMatchKind
{
    /// <summary>The section is found by the value of one key entry, such as <c>nickname</c>.</summary>
    Keyed,

    /// <summary>The file has at most one section of this type.</summary>
    Singleton,

    /// <summary>Patch sections are added as new sections. The engine merges repeated sections.</summary>
    Append,
}

/// <param name="KeyOptional">True for a keyed type whose sections can also have no line of the key. The engine then
/// reads the section without the key as the default of the file, for example the <c>[LootableZone]</c> of the whole
/// field. A patch section without a line of the key finds that section. A file has at most one such section.</param>
internal readonly record struct SectionMatch(SectionMatchKind Kind, string? KeyEntry, bool KeyOptional = false);

/// <summary>
/// How a patch section of each type finds the section that it changes, and which section types belong to a parent.
/// </summary>
/// <remarks>
/// Most Freelancer sections are identified by <c>nickname</c>. This table lists the exceptions.
/// </remarks>
internal static class SectionKeys
{
    public const string DefaultKey = "nickname";

    private static readonly Dictionary<string, SectionMatch> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // DATA\initialworld.ini and faction_prop.ini
        ["FactionProps"] = new(SectionMatchKind.Keyed, "affiliation"),
        ["RepChangeEffects"] = new(SectionMatchKind.Keyed, "group"),
        // DATA\initialworld.ini (milestone M3). The retail file has one such section. Its locked_gate and
        // npc_locked_gate lines hold the FLHash of a gate nickname, so a rename of a gate changes them.
        ["locked_gates"] = new(SectionMatchKind.Singleton, null),
        // DATA\FX\fuse*.ini
        ["fuse"] = new(SectionMatchKind.Keyed, "name"),
        // Market files: the engine adds the goods of repeated [BaseGood] sections for one base.
        ["BaseGood"] = new(SectionMatchKind.Append, null),
        // DATA\MISSIONS\news.ini: the engine keeps each [NewsItem], and a news item has no key. Only news.ini has this
        // type. So a patch section adds a news item, for example the news of a new base (owner decision of 2026-11-21).
        ["NewsItem"] = new(SectionMatchKind.Append, null),
        // Star system files
        ["Nebula"] = new(SectionMatchKind.Keyed, "zone"),
        ["Asteroids"] = new(SectionMatchKind.Keyed, "zone"),
        ["SystemInfo"] = new(SectionMatchKind.Singleton, null),
        ["Music"] = new(SectionMatchKind.Singleton, null),
        ["Dust"] = new(SectionMatchKind.Singleton, null),
        ["Ambient"] = new(SectionMatchKind.Singleton, null),
        ["Background"] = new(SectionMatchKind.Singleton, null),
        ["TexturePanels"] = new(SectionMatchKind.Singleton, null),
        // Room files (decision D66 of the M8 design). In retail, each of these occurs at most once in a file, and only
        // in the 446 room files. [CharacterPlacement] has a name line, but no nickname.
        ["Room_Info"] = new(SectionMatchKind.Singleton, null),
        ["CharacterPlacement"] = new(SectionMatchKind.Singleton, null),
        ["PlayerShipPlacement"] = new(SectionMatchKind.Singleton, null),
        // Nebula files (upstream Nebula.cs) and asteroid field files (AsteroidField.cs), milestone M25. The engine keeps
        // one section of each of these types: the last one, or the first one for [NebulaLight] and [Clouds]. Each
        // type occurs at most once in each retail file, except [NebulaLight] (two in two files). A patch cannot find
        // one of two sections, so those two files keep their [NebulaLight] read-only. The ring and black hole files
        // reuse [Fog], [Exterior] and [Clouds], at most once in each file.
        ["Fog"] = new(SectionMatchKind.Singleton, null),
        ["Exterior"] = new(SectionMatchKind.Singleton, null),
        ["NebulaLight"] = new(SectionMatchKind.Singleton, null),
        ["Clouds"] = new(SectionMatchKind.Singleton, null),
        ["BackgroundLightning"] = new(SectionMatchKind.Singleton, null),
        ["DynamicLightning"] = new(SectionMatchKind.Singleton, null),
        ["Cube"] = new(SectionMatchKind.Singleton, null),
        ["Field"] = new(SectionMatchKind.Singleton, null),
        ["Band"] = new(SectionMatchKind.Singleton, null),
        ["ExclusionBand"] = new(SectionMatchKind.Singleton, null),
        ["AsteroidBillboards"] = new(SectionMatchKind.Singleton, null),
        ["properties"] = new(SectionMatchKind.Singleton, null),
        // The engine reads each [Exclusion Zones] section, but each retail file has one at most. The lines of each
        // excluded zone belong together (see LinkedEntries).
        ["Exclusion Zones"] = new(SectionMatchKind.Singleton, null),
        // The engine keeps each of these sections. Each retail file names an asteroid or a zone one time at most. One
        // [LootableZone] without a zone line gives the loot of the whole field.
        ["DynamicAsteroids"] = new(SectionMatchKind.Keyed, "asteroid"),
        ["LootableZone"] = new(SectionMatchKind.Keyed, "zone", KeyOptional: true),
    };

    /// <summary>
    /// Section types that the engine attaches to the closest section before them that is not of a child type
    /// (upstream <c>[Section(..., Child = true)]</c>).
    /// </summary>
    private static readonly HashSet<string> ChildTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CollisionGroup", // [Ship], [Solar]
        "LOD", // equipment
        // [fuse]. The engine reads the server actions of retail since OL-0046 (the last six).
        "start_effect", "destroy_group", "destroy_hp_attachment", "start_cam_particles", "ignite_fuse", "impulse",
        "destroy_root", "tumble", "damage_group", "damage_root", "damage_hp_attachment", "make_invincible", "dump_cargo",
        "MVendor", "MRoom", "GF_NPC", "BaseFaction", // [MBase]
    };

    /// <summary>
    /// Entry names whose lines belong together: the engine attaches each later name to the last line before it.
    /// A patch must list all names of a group, or none.
    /// </summary>
    private static readonly Dictionary<string, string[][]> LinkedEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Zone.HandleFaction adds faction and faction_weight lines to the last encounter before them.
        ["Zone"] = [["encounter", "faction", "faction_weight"]],
        ["GF_NPC"] = [["know", "knowdb"], ["rumor", "rumor_type2", "rumorknowdb"]],
        ["EncounterFormation"] = [["ship_by_class", "ship_by_npc_arch", "pilot_job", "make_class"]],
        // The exclusion line group (milestone M25). The engine starts a new excluded zone at each exclusion or exclude
        // line, and gives it the lines after it (upstream ParseHelpers.Chunk). The other names are the keys of the
        // nebula and the asteroid exclusions (NebulaExclusion.cs, AsteroidExclusion.cs). Thus a patch keeps each line
        // with its zone, and a delete of a zone can remove the lines of its exclusion (decision D81, milestone M3).
        ["Exclusion Zones"] =
        [[
            "exclusion", "exclude", "fog_far", "fog_near", "zone_shell", "shell_scalar", "max_alpha", "color",
            "exclusion_tint", "exclude_billboards", "exclude_dynamic_asteroids", "empty_cube_frequency", "billboard_count",
        ]],
    };

    /// <summary>
    /// The item lists of each section type (see <see cref="ItemLists"/>). A patch section adds lines of an item list
    /// with <c>ol.append</c>, and removes one line with <c>ol.remove_line</c>.
    /// </summary>
    private static readonly Dictionary<string, ItemLists> ItemListTable = new(StringComparer.OrdinalIgnoreCase)
    {
        // DATA\initialworld.ini: retail reads each locked_gate and npc_locked_gate line of each [locked_gates] section,
        // and locks the gate whose nickname has the hash. HD Edition 0.7.1 has a lock line that vanilla 1.1 does not
        // have, so a mod for both games adds and removes only its own lines (issue #94).
        ["locked_gates"] = new(["locked_gate", "npc_locked_gate"], ItemLists.HashItem, "a hash as a decimal number"),
    };

    /// <summary>The item lists of a section type, or null when the type has none.</summary>
    public static ItemLists? ItemListsOf(string sectionName) => ItemListTable.GetValueOrDefault(sectionName);

    /// <summary>The section types that have item lists, in the order of the table, for messages.</summary>
    public static IEnumerable<string> ItemListTypes => ItemListTable.Keys;

    public static SectionMatch For(string sectionName) =>
        Exceptions.TryGetValue(sectionName, out var match) ? match : new(SectionMatchKind.Keyed, DefaultKey);

    /// <summary>
    /// How a patch section of a type finds its target in a file. In a mission file, <c>[Mission]</c> and
    /// <c>[Constants]</c> are single sections, as <c>[SystemInfo]</c> is in a system file (section 6.5 of the M6b
    /// design). A mission script has one <c>[Mission]</c>, and retail has one <c>[Constants]</c> in M04.
    /// </summary>
    /// <param name="path">The install path of the file.</param>
    public static SectionMatch For(string sectionName, string path) =>
        IsMissionFile(path) && MissionSingles.Contains(sectionName) ? new(SectionMatchKind.Singleton, null) : For(sectionName);

    /// <summary>The types that a mission file has once at most.</summary>
    private static readonly HashSet<string> MissionSingles = new(StringComparer.OrdinalIgnoreCase) { "Mission", "Constants" };

    /// <summary>The folder of the mission files, as the start of an install path.</summary>
    private const string MissionFolder = @"DATA\MISSIONS\";

    /// <summary>True for a file under <c>DATA\MISSIONS</c>: a mission script, a ship build file or a shared file of the
    /// missions.</summary>
    /// <param name="path">An install path in any form.</param>
    public static bool IsMissionFile(string path) =>
        (IO.VfsPath.Normalize(path) ?? path).StartsWith(MissionFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a section whose patch holds all its lines with <c>ol.replace</c>: a section of the seven mission types
    /// whose line order counts, in a mission file (section 6.2 of the M6b design, decision D665). The rule data marks
    /// the types (<c>orderSensitive</c> in <c>mission-schema.json</c>).
    /// </summary>
    /// <param name="path">The install path of the file.</param>
    public static bool IsReplaced(string sectionName, string path) =>
        IsMissionFile(path) && MissionOrderSensitive.Value.Contains(sectionName);

    private static readonly Lazy<HashSet<string>> MissionOrderSensitive = new(() =>
        Missions.MissionSchema.Retail.Sections.Where(s => s.OrderSensitive).Select(s => s.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// True when a patch section finds the section without a key: a section of a singleton type, or a section without
    /// a line of the key of a type whose key is optional (see <see cref="SectionMatch.KeyOptional"/>).
    /// </summary>
    public static bool FindsWithoutKey(Section section, string? path = null)
    {
        var match = path == null ? For(section.Name) : For(section.Name, path);
        return match.Kind == SectionMatchKind.Singleton || match.KeyOptional && !HasKeyLine(section, match);
    }

    /// <summary>True when the section has a line of the key entry of its type.</summary>
    public static bool HasKeyLine(Section section, SectionMatch match) =>
        match.KeyEntry != null && section.Any(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase));

    public static bool IsChild(string sectionName) => ChildTypes.Contains(sectionName);

    /// <summary>The number of child sections that directly follow the section at <paramref name="index"/>.</summary>
    public static int ChildCount(IReadOnlyList<Section> sections, int index)
    {
        var count = 0;
        while (index + 1 + count < sections.Count && IsChild(sections[index + 1 + count].Name))
            count++;
        return count;
    }

    public static IReadOnlyList<string[]> LinkedGroups(string sectionName) =>
        LinkedEntries.TryGetValue(sectionName, out var groups) ? groups : [];

    /// <summary>
    /// True when the engine reads the lines of the section one by one in file order, so the order of all lines
    /// counts. For example, a mission <c>[Trigger]</c> runs its actions in order.
    /// </summary>
    /// <remarks>
    /// These are the section types whose upstream schema class implements <see cref="IEntryHandler"/>. The list comes
    /// from the <c>[Section]</c> attributes of the upstream schema, so it follows upstream changes.
    /// </remarks>
    public static bool IsOrderSensitive(string sectionName) => OrderSensitiveTypes.Value.Contains(sectionName);

    /// <summary>True when the engine reads the lines of a section in order in a file: a type of
    /// <see cref="IsOrderSensitive(string)"/>, or a mission type of <see cref="IsReplaced"/> in a mission
    /// file.</summary>
    /// <param name="path">The install path of the file.</param>
    public static bool IsOrderSensitive(string sectionName, string path) =>
        IsOrderSensitive(sectionName) || IsReplaced(sectionName, path);

    private static readonly Lazy<HashSet<string>> OrderSensitiveTypes = new(() =>
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        return typeof(IEntryHandler).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(flags))
            .SelectMany(f => f.GetCustomAttributes<SectionAttribute>().Select(a => (a.Name, Type: a.Type ?? ItemType(f.FieldType))))
            .Where(s => typeof(IEntryHandler).IsAssignableFrom(s.Type))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>The section class of a <c>[Section]</c> field: the field type, or the item type of a list.</summary>
    private static Type ItemType(Type fieldType) =>
        fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>)
            ? fieldType.GetGenericArguments()[0]
            : fieldType;
}
