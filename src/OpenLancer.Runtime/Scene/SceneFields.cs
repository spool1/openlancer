using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Runtime.Scene;

/// <summary>What the scene does when a key of a section changes.</summary>
[Flags]
public enum SceneEffect
{
    /// <summary>The engine does not read the key, or the editor scene does not show it. The Inspector still edits
    /// it.</summary>
    None = 0,

    /// <summary>The transform fields of the item and of its world object change at once.</summary>
    Transform = 1,

    /// <summary>The item is built again from W with the engine code, and its fields go into the scene item.</summary>
    Data = 2,

    /// <summary>The name changes, so the labels and the Hierarchy names change.</summary>
    Label = 4,

    /// <summary>A zone role changes, so the system index is built again.</summary>
    Roles = 8,

    /// <summary>A lane chain or a dock kind changes, so the system index is built again.</summary>
    Lanes = 16,

    /// <summary>The light is built again, and the renderer loads the lights again.</summary>
    Light = 32,

    /// <summary>A system color changes. The view reads the colors each frame.</summary>
    Environment = 64,

    /// <summary>The model of the object loads again. This effect waits until no edit is open.</summary>
    Reinit = 128,

    /// <summary>The renderer builds the asteroid fields and the nebulae again. This effect applies only to the zone
    /// of a field or a nebula, and it waits until no edit is open.</summary>
    Volume = 256,

    /// <summary>The starsphere models load again. This effect waits until no edit is open.</summary>
    Starsphere = 512,
}

/// <summary>A section type that the scene shows.</summary>
public enum SceneSection
{
    Object,
    Zone,
    LightSource,
    SystemInfo,
    Ambient,
    Background,
    Dust,
    Music,
    TexturePanels,

    /// <summary>The <c>[Archetype]</c> sections of a system file: the preloads.</summary>
    Preloads,
    EncounterParameters,

    /// <summary>The <c>[system]</c> section of a system in <c>universe.ini</c>.</summary>
    UniverseSystem,

    /// <summary>A <c>[Nebula]</c> section of a system file: the scene item of a nebula (milestone M22).</summary>
    Nebula,

    /// <summary>An <c>[Asteroids]</c> section of a system file: the scene item of an asteroid field (milestone
    /// M22).</summary>
    Asteroids,
}

/// <summary>One key of a section type and what the scene does when it changes.</summary>
/// <param name="Locked">True when the Inspector shows the key read-only: a key entry, a key of a linked group, the
/// file of a system, the link of a jump connection or of a trade lane, or a key of a section that a patch cannot
/// find.</param>
/// <param name="Evidence">The engine code that reads the key, or the reason for the effect.</param>
public sealed record SceneField(SceneSection Section, string Key, SceneEffect Effect, bool Locked, string Evidence);

/// <summary>
/// The live action of each key of each section type that the scene shows. The table has one row for each key of the
/// upstream schema, also for each alias of a key. A key that the schema does not know has no row, and its effect is
/// <see cref="SceneEffect.None"/>.
/// </summary>
/// <remarks>The evidence names lines of <c>GameItemDb.cs</c> and other files of the pinned engine.</remarks>
public static class SceneFields
{
    /// <summary>The effects that load models, so they wait until no field or drag is open and no reload runs.</summary>
    public const SceneEffect Deferred = SceneEffect.Reinit | SceneEffect.Volume | SceneEffect.Starsphere;

    private const SceneEffect DataLabel = SceneEffect.Data | SceneEffect.Label;
    private const SceneEffect DataReinit = SceneEffect.Data | SceneEffect.Reinit;
    private const SceneEffect DataRoles = SceneEffect.Data | SceneEffect.Roles;

    /// <summary>The section type names of the system files, by scene section.</summary>
    private static readonly Dictionary<string, SceneSection> SystemFileSections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Object"] = SceneSection.Object,
        ["Zone"] = SceneSection.Zone,
        ["LightSource"] = SceneSection.LightSource,
        ["SystemInfo"] = SceneSection.SystemInfo,
        ["Ambient"] = SceneSection.Ambient,
        ["Background"] = SceneSection.Background,
        ["Dust"] = SceneSection.Dust,
        ["Music"] = SceneSection.Music,
        ["TexturePanels"] = SceneSection.TexturePanels,
        ["Archetype"] = SceneSection.Preloads,
        ["EncounterParameters"] = SceneSection.EncounterParameters,
        ["Nebula"] = SceneSection.Nebula,
        ["Asteroids"] = SceneSection.Asteroids,
    };

    private const string UniverseSystemType = "system";

    /// <summary>Why the links of a trade lane ring are locked rows.</summary>
    private const string LaneLink =
        "The lane tools and the ring buttons change the links of a lane, and the raw INI block can change the line.";

    public static IReadOnlyList<SceneField> Rows { get; } =
    [
        // [Object]: GetSystemObject (GameItemDb.cs:2583-2668).
        Locked(SceneSection.Object, "nickname", "GameItemDb.cs:2587. Rename (F2) in the Hierarchy changes it."),
        Row(SceneSection.Object, "pos", SceneEffect.Transform, "GameItemDb.cs:2591"),
        Row(SceneSection.Object, "rotate", SceneEffect.Transform, "GameItemDb.cs:2633-2636"),
        Row(SceneSection.Object, "archetype", DataReinit | SceneEffect.Label | SceneEffect.Lanes,
            "GameItemDb.cs:2638. The archetype type sets the trade lane dock (:2651-2659), and the Hierarchy groups " +
            "by kind. A missing archetype drops the object (:2660-2664)."),
        Row(SceneSection.Object, "loadout", DataReinit, "GameItemDb.cs:2667, GameWorld.cs:130-137"),
        Row(SceneSection.Object, "star", DataReinit, "GameItemDb.cs:2666, GameWorld.cs:113-115"),
        Row(SceneSection.Object, "spin", DataReinit, "GameItemDb.cs:2593, GameWorld.cs:142"),
        Row(SceneSection.Object, "dock_with", DataReinit, "GameItemDb.cs:2614-2620. GameWorld.cs:147-160 makes the dock."),
        LockedRow(SceneSection.Object, "goto", DataReinit, "GameItemDb.cs:2622-2630. GameWorld.cs:147-160 makes the " +
            "dock. The jump connection tools change the pair, and the raw INI block can change the line."),
        LockedRow(SceneSection.Object, "prev_ring", DataReinit | SceneEffect.Lanes, "GameItemDb.cs:2651-2659. " + LaneLink),
        LockedRow(SceneSection.Object, "next_ring", DataReinit | SceneEffect.Lanes, "GameItemDb.cs:2651-2659. " + LaneLink),
        Row(SceneSection.Object, "tradelane_space_name", DataLabel, "GameItemDb.cs:2640-2649 and the lane walk at :1537-1595"),
        Row(SceneSection.Object, "ids_name", DataLabel, "GameItemDb.cs:2590"),
        Row(SceneSection.Object, "ids_info", SceneEffect.Data, "GameItemDb.cs:2594"),
        Row(SceneSection.Object, "base", SceneEffect.Data, "GameItemDb.cs:2595"),
        Row(SceneSection.Object, "reputation", SceneEffect.Data, "GameItemDb.cs:2588"),
        Row(SceneSection.Object, "faction", SceneEffect.Data, "GameItemDb.cs:2596"),
        Row(SceneSection.Object, "pilot", SceneEffect.Data, "GameItemDb.cs:2597"),
        Row(SceneSection.Object, "behavior", SceneEffect.Data, "GameItemDb.cs:2598"),
        Row(SceneSection.Object, "difficulty_level", SceneEffect.Data, "GameItemDb.cs:2603"),
        Row(SceneSection.Object, "msg_id_prefix", SceneEffect.Data, "GameItemDb.cs:2602"),
        Row(SceneSection.Object, "parent", SceneEffect.Data, "GameItemDb.cs:2592, :2606"),
        Row(SceneSection.Object, "voice", SceneEffect.Data, "GameItemDb.cs:2607"),
        Row(SceneSection.Object, "space_costume", SceneEffect.Data, "GameItemDb.cs:2608"),
        Row(SceneSection.Object, "jump_effect", SceneEffect.Data, "GameItemDb.cs:2609"),
        Row(SceneSection.Object, "comment", SceneEffect.Data, "GameItemDb.cs:2612"),
        Row(SceneSection.Object, "atmosphere_range", SceneEffect.Data, "GameItemDb.cs:2601"),
        Row(SceneSection.Object, "burn_color", SceneEffect.Data, "GameItemDb.cs:2599"),
        Row(SceneSection.Object, "ambient_color", SceneEffect.Data, "GameItemDb.cs:2600"),
        Row(SceneSection.Object, "ambient", SceneEffect.Data,
            "GameItemDb.cs:2600. An alias of ambient_color (Schema/Universe/SystemObject.cs:18-19)."),
        Row(SceneSection.Object, "visit", SceneEffect.Data,
            "GameItemDb.cs:2589. A hidden object draws at half opacity (SceneMarkers.cs:110)."),
        Row(SceneSection.Object, "ring", DataRoles, "GameItemDb.cs:2610-2611 and SystemIndex.ZoneUses"),
        Row(SceneSection.Object, "size", SceneEffect.None, "SystemPart.size exists, but GetSystemObject does not read it."),

        // [Zone]: InitSystems (GameItemDb.cs:1601-1676).
        Locked(SceneSection.Zone, "nickname", "GameItemDb.cs:1603. Rename (F2) in the Hierarchy changes it."),
        Row(SceneSection.Zone, "pos", SceneEffect.Transform, "GameItemDb.cs:1610"),
        Row(SceneSection.Zone, "rotate", SceneEffect.Transform, "GameItemDb.cs:1647-1661"),
        Row(SceneSection.Zone, "size", SceneEffect.Transform | SceneEffect.Volume,
            "GameItemDb.cs:1664-1673. A ring stores (outer, height, inner) from the line (outer, inner, height). " +
            "AsteroidFieldRenderer.cs:45 reads the size only when it starts. ZoneRules.IsTradeLaneZone reads the " +
            "size of a box, so SceneApplier adds Roles only when the size changes that rule for the zone."),
        Row(SceneSection.Zone, "shape", SceneEffect.Transform | SceneEffect.Roles | SceneEffect.Volume,
            "GameItemDb.cs:1663. The ring swap depends on the shape, and Zone.cs:660-666 gives the roles."),
        LockedRow(SceneSection.Zone, "encounter", DataRoles, EncounterEvidence),
        LockedRow(SceneSection.Zone, "faction", DataRoles, EncounterEvidence),
        LockedRow(SceneSection.Zone, "faction_weight", DataRoles, EncounterEvidence),
        Row(SceneSection.Zone, "path_label", DataRoles, "GameItemDb.cs:1634, Zone.cs:660-666"),
        Row(SceneSection.Zone, "lane_id", SceneEffect.Data,
            "GameItemDb.cs:1620. The retail game does not read it (decision D513), so the zone roles do not use it."),
        Row(SceneSection.Zone, "ids_name", DataLabel, "GameItemDb.cs:1604"),
        Row(SceneSection.Zone, "ids_info", SceneEffect.Data, "GameItemDb.cs:1605"),
        Row(SceneSection.Zone, "edge_fraction", SceneEffect.Data,
            "GameItemDb.cs:1606. NebulaRenderer.cs:74 reads it each frame."),
        Row(SceneSection.Zone, "property_flags", SceneEffect.Data, "GameItemDb.cs:1607"),
        Row(SceneSection.Zone, "property_fog_color", SceneEffect.Data, "GameItemDb.cs:1608"),
        Row(SceneSection.Zone, "visit", SceneEffect.Data, "GameItemDb.cs:1609"),
        Row(SceneSection.Zone, "sort", SceneEffect.Data, "GameItemDb.cs:1611"),
        Row(SceneSection.Zone, "music", SceneEffect.Data, "GameItemDb.cs:1613"),
        Row(SceneSection.Zone, "spacedust", SceneEffect.Data, "GameItemDb.cs:1614"),
        Row(SceneSection.Zone, "spacedust_maxparticles", SceneEffect.Data, "GameItemDb.cs:1615"),
        Row(SceneSection.Zone, "interference", SceneEffect.Data, "GameItemDb.cs:1616"),
        Row(SceneSection.Zone, "power_modifier", SceneEffect.Data, "GameItemDb.cs:1617"),
        Row(SceneSection.Zone, "drag_modifier", SceneEffect.Data, "GameItemDb.cs:1618"),
        Row(SceneSection.Zone, "comment", SceneEffect.Data, "GameItemDb.cs:1619"),
        Row(SceneSection.Zone, "tradelane_attack", SceneEffect.Data, "GameItemDb.cs:1621"),
        Row(SceneSection.Zone, "tradelane_down", SceneEffect.Data, "GameItemDb.cs:1622"),
        Row(SceneSection.Zone, "damage", SceneEffect.Data, "GameItemDb.cs:1623"),
        Row(SceneSection.Zone, "toughness", SceneEffect.Data, "GameItemDb.cs:1624"),
        Row(SceneSection.Zone, "density", SceneEffect.Data, "GameItemDb.cs:1625"),
        Row(SceneSection.Zone, "population_additive", SceneEffect.Data, "GameItemDb.cs:1626"),
        Row(SceneSection.Zone, "mission_eligible", SceneEffect.Data, "GameItemDb.cs:1627"),
        Row(SceneSection.Zone, "max_battle_size", SceneEffect.Data, "GameItemDb.cs:1628"),
        Row(SceneSection.Zone, "pop_type", SceneEffect.Data, "GameItemDb.cs:1629"),
        Row(SceneSection.Zone, "relief_time", SceneEffect.Data, "GameItemDb.cs:1630"),
        Row(SceneSection.Zone, "repop_time", SceneEffect.Data, "GameItemDb.cs:1631"),
        Row(SceneSection.Zone, "attack_ids", SceneEffect.Data, "GameItemDb.cs:1632"),
        Row(SceneSection.Zone, "mission_type", SceneEffect.Data, "GameItemDb.cs:1633"),
        Row(SceneSection.Zone, "usage", SceneEffect.Data, "GameItemDb.cs:1635"),
        Row(SceneSection.Zone, "vignette_type", SceneEffect.Data, "GameItemDb.cs:1636"),
        Row(SceneSection.Zone, "density_restriction", SceneEffect.Data, "GameItemDb.cs:1638"),
        Row(SceneSection.Zone, "zone_creation_distance", SceneEffect.None, "The schema has it. InitSystems does not read it."),
        Row(SceneSection.Zone, "spin", SceneEffect.None, "The schema has it. InitSystems does not read it."),
        Row(SceneSection.Zone, "reputation", SceneEffect.None, "The schema has it. InitSystems does not read it."),

        // [LightSource]: InitSystems (GameItemDb.cs:1466-1516).
        Locked(SceneSection.LightSource, "nickname",
            "GameItemDb.cs:1516. Rename (F2) in the Hierarchy changes it."),
        Row(SceneSection.LightSource, "pos", SceneEffect.Transform, "GameItemDb.cs:1483-1490"),
        Row(SceneSection.LightSource, "color", SceneEffect.Light, "GameItemDb.cs:1472-1481"),
        Row(SceneSection.LightSource, "range", SceneEffect.Light, "GameItemDb.cs:1492-1500"),
        Row(SceneSection.LightSource, "type", SceneEffect.Light, "GameItemDb.cs:1503-1506"),
        Row(SceneSection.LightSource, "attenuation", SceneEffect.Light, "GameItemDb.cs:1507"),
        Row(SceneSection.LightSource, "direction", SceneEffect.Light, "GameItemDb.cs:1502"),
        Row(SceneSection.LightSource, "atten_curve", SceneEffect.Light,
            "GameItemDb.cs:1509-1512. An unknown curve throws in the engine (:1352)."),
        Row(SceneSection.LightSource, "rotate", SceneEffect.None, "The schema has it. The engine does not read it."),
        Row(SceneSection.LightSource, "ids_name", SceneEffect.None, "The schema has it. The engine does not read it."),
        Row(SceneSection.LightSource, "color_curve", SceneEffect.None,
            "The schema has it (OL-0048). The engine does not change the light color with it yet."),

        // The sections of a system file that set the fields of the system (GameItemDb.cs:1393-1464).
        Row(SceneSection.SystemInfo, "space_color", SceneEffect.Environment,
            "GameItemDb.cs:1409. The view reads BackgroundColor each frame."),
        Row(SceneSection.SystemInfo, "local_faction", SceneEffect.Data, "GameItemDb.cs:1399"),
        Row(SceneSection.SystemInfo, "space_farclip", SceneEffect.None,
            "GameItemDb.cs:1415. The editor camera sets its own far plane (SceneCamera.Apply)."),
        Row(SceneSection.SystemInfo, "rpop_solar_detection", SceneEffect.None, "The engine does not read it."),
        Row(SceneSection.Ambient, "color", SceneEffect.Environment,
            "GameItemDb.cs:1401. The view reads AmbientColor each frame."),
        Row(SceneSection.Background, "basic_stars", SceneEffect.Starsphere, "GameItemDb.cs:1462, SystemRenderer.cs:133-154"),
        Row(SceneSection.Background, "complex_stars", SceneEffect.Starsphere, "GameItemDb.cs:1463, SystemRenderer.cs:133-154"),
        Row(SceneSection.Background, "nebulae", SceneEffect.Starsphere, "GameItemDb.cs:1464, SystemRenderer.cs:133-154"),
        Row(SceneSection.Dust, "spacedust", SceneEffect.None, "GameItemDb.cs:1413. The editor scene shows no dust."),
        Row(SceneSection.Dust, "dust", SceneEffect.None,
            "GameItemDb.cs:1413. An alias of spacedust (Schema/Universe/SystemDust.cs:8-9)."),
        Row(SceneSection.Dust, "spacedust_maxparticles", SceneEffect.None, "GameItemDb.cs:1414. The editor scene shows no dust."),
        Row(SceneSection.Music, "space", SceneEffect.None, "GameItemDb.cs:1410. The editor plays no music."),
        Row(SceneSection.Music, "danger", SceneEffect.None, "GameItemDb.cs:1412. The editor plays no music."),
        Row(SceneSection.Music, "battle", SceneEffect.None, "GameItemDb.cs:1411. The editor plays no music."),
        Row(SceneSection.TexturePanels, "file", SceneEffect.None, "GameItemDb.cs:1457-1459. The editor scene does not use it."),
        Locked(SceneSection.Preloads, "ship", PreloadEvidence),
        Locked(SceneSection.Preloads, "simple", PreloadEvidence),
        Locked(SceneSection.Preloads, "solar", PreloadEvidence),
        Locked(SceneSection.Preloads, "equipment", PreloadEvidence),
        Locked(SceneSection.Preloads, "snd", PreloadEvidence),
        Locked(SceneSection.Preloads, "voice", PreloadEvidence),
        Locked(SceneSection.EncounterParameters, "nickname", "GameItemDb.cs:1420. The key entry of the section."),
        Row(SceneSection.EncounterParameters, "filename", SceneEffect.None,
            "GameItemDb.cs:1420. The editor scene runs no encounters."),

        // [Nebula] and [Asteroids]: the universe parse reads the file (ZoneReference.cs:9-19), and InitSystems finds the
        // zone (GameItemDb.cs:1686-1728). The scene builds no nebula or field from W.
        Locked(SceneSection.Nebula, "zone", "The key of the section (SectionKeys.cs). GameItemDb.cs:1714."),
        Row(SceneSection.Nebula, "file", SceneEffect.None, FileEvidence),
        Locked(SceneSection.Asteroids, "zone", "The key of the section (SectionKeys.cs). GameItemDb.cs:1773."),
        Row(SceneSection.Asteroids, "file", SceneEffect.None, FileEvidence),

        // The [system] section of universe.ini (GameItemDb.cs:1393-1416).
        Locked(SceneSection.UniverseSystem, "nickname", "GameItemDb.cs:1404. The editor does not rename a system."),
        Locked(SceneSection.UniverseSystem, "file",
            "GameItemDb.cs:1398. A change of the file moves all contents of the system, so the Inspector cannot " +
            "change it."),
        Row(SceneSection.UniverseSystem, "strid_name", DataLabel,
            "GameItemDb.cs:1402. The window title, the status bar, the system list and the Hierarchy show the name."),
        Row(SceneSection.UniverseSystem, "navmapscale", SceneEffect.Data, "GameItemDb.cs:1416. The Sector row reads it."),
        Row(SceneSection.UniverseSystem, "ids_info", SceneEffect.None, "GameItemDb.cs:1403. The scene does not show it."),
        Row(SceneSection.UniverseSystem, "pos", SceneEffect.None, "GameItemDb.cs:1400. The scene does not show it."),
        Row(SceneSection.UniverseSystem, "msg_id_prefix", SceneEffect.None, "GameItemDb.cs:1408. The scene does not show it."),
        Row(SceneSection.UniverseSystem, "visit", SceneEffect.None, "GameItemDb.cs:1405. The scene does not show it."),
        Row(SceneSection.UniverseSystem, "name", SceneEffect.None,
            "Schema/Universe/UniverseElement.cs has it. The engine does not read it."),
    ];

    private const string FileEvidence =
        "Schema/Universe/Nebula.cs:38-45 and AsteroidField.cs:39-46 read the file when the universe loads. The Scene " +
        "shows another file when the system opens again after the save.";

    private const string EncounterEvidence =
        "GameItemDb.cs:1637. A linked group (SectionKeys.cs:74), so the Inspector cannot edit one of its keys.";

    private const string PreloadEvidence =
        "GameItemDb.cs:1423-1455. A section without a key entry, so a patch cannot find it (PatchWriter.cs:146-152).";

    private static readonly Dictionary<SceneSection, Dictionary<string, SceneField>> BySection =
        Rows.GroupBy(r => r.Section).ToDictionary(g => g.Key,
            g => g.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase));

    private static readonly Dictionary<SceneSection, SceneEffect> Unions =
        BySection.ToDictionary(s => s.Key, s => s.Value.Values.Aggregate(SceneEffect.None, (all, r) => all | r.Effect));

    /// <summary>The effect of a key of a section type. A key without a row has no effect.</summary>
    public static SceneEffect Effect(SceneSection section, string key) =>
        BySection.TryGetValue(section, out var keys) && keys.TryGetValue(key, out var field) ? field.Effect : SceneEffect.None;

    /// <summary>The union of the effects of all keys of a section type.</summary>
    public static SceneEffect All(SceneSection section) => Unions.GetValueOrDefault(section);

    /// <summary>The scene section of a section type, or null when the scene does not show the type.</summary>
    /// <param name="universe">True for a section of <c>universe.ini</c>, false for a section of a system file.</param>
    public static SceneSection? SectionOf(string sectionType, bool universe) =>
        universe
            ? sectionType.Equals(UniverseSystemType, StringComparison.OrdinalIgnoreCase) ? SceneSection.UniverseSystem : null
            : SystemFileSections.TryGetValue(sectionType, out var section) ? section : null;

    private static SceneField Row(SceneSection section, string key, SceneEffect effect, string evidence) =>
        new(section, key, effect, false, evidence);

    private static SceneField LockedRow(SceneSection section, string key, SceneEffect effect, string evidence) =>
        new(section, key, effect, true, evidence);

    private static SceneField Locked(SceneSection section, string key, string evidence) =>
        new(section, key, SceneEffect.None, true, evidence);
}
