using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The table of live actions has one row for each key of the upstream schema of each section type that the scene
/// shows. So a new upstream key fails the test until someone decides its effect.
/// </summary>
public sealed class SceneFieldsTests
{
    /// <summary>The schema of each scene section: the section types of a system file and the [system] of
    /// universe.ini.</summary>
    private static Dictionary<SceneSection, SectionSchema> Schemas()
    {
        var schemas = new Dictionary<SceneSection, SectionSchema>();
        foreach (var (ini, type, schema) in SchemaCatalog.All.Where(s => s.Ini == typeof(StarSystem)))
        {
            if (SceneFields.SectionOf(type, universe: false) is { } section)
                schemas.Add(section, schema);
        }
        schemas.Add(SceneFields.SectionOf("system", universe: true)!.Value,
            SchemaCatalog.SectionClass(typeof(StarSystem), "system"));
        return schemas;
    }

    [Fact]
    public void EachSchemaKeyOfEachSceneSectionHasExactlyOneRow()
    {
        var schemas = Schemas();

        Assert.Equal(Enum.GetValues<SceneSection>().Order(), schemas.Keys.Order());
        foreach (var (section, schema) in schemas)
        {
            var keys = schema.Keys.Select(k => k.Name.ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
            var rows = SceneFields.Rows.Where(r => r.Section == section)
                .Select(r => r.Key.ToLowerInvariant()).Order(StringComparer.Ordinal).ToList();
            Assert.True(keys.SequenceEqual(rows),
                $"{section}: schema keys [{string.Join(", ", keys)}], rows [{string.Join(", ", rows)}]");
        }
        Assert.Equal(SceneFields.Rows.Count, schemas.Values.Sum(s => s.Keys.Count));
    }

    [Fact]
    public void EachAliasHasItsOwnRowAndAKeyOutsideTheSchemaHasNone()
    {
        Assert.Equal(SceneEffect.Data, SceneFields.Effect(SceneSection.Object, "ambient"));
        Assert.Equal(SceneEffect.Data, SceneFields.Effect(SceneSection.Object, "ambient_color"));
        Assert.Single(SceneFields.Rows, r => r.Section == SceneSection.Dust && r.Key == "dust");
        Assert.Single(SceneFields.Rows, r => r.Section == SceneSection.Dust && r.Key == "spacedust");
        // Retail has a name line in 31 [SystemInfo] sections, but the schema does not know the key.
        Assert.DoesNotContain(SceneFields.Rows, r => r.Section == SceneSection.SystemInfo && r.Key == "name");
        Assert.Equal(SceneEffect.None, SceneFields.Effect(SceneSection.SystemInfo, "name"));
        // Retail has behavior lines in [LightSource] sections, but the schema does not know the key.
        Assert.DoesNotContain(SceneFields.Rows, r => r.Section == SceneSection.LightSource && r.Key == "behavior");
        Assert.Equal(SceneEffect.None, SceneFields.Effect(SceneSection.LightSource, "behavior"));
    }

    /// <summary>
    /// The effect and the lock of each row, from the tables of the design (sections 7.2 to 7.5). The applier runs these
    /// effects, so a changed effect fails this test until the design changes too. The locked rows are the key entries,
    /// the linked group, the file of a system, the link of a jump connection and the preloads.
    /// </summary>
    [Fact]
    public void EachRowHasTheEffectAndTheLockOfTheDesign()
    {
        const SceneEffect none = SceneEffect.None, transform = SceneEffect.Transform, data = SceneEffect.Data;
        const SceneEffect label = SceneEffect.Label, roles = SceneEffect.Roles, lanes = SceneEffect.Lanes;
        const SceneEffect reinit = SceneEffect.Reinit;
        var expected = new List<string>();
        void Rows(SceneSection section, SceneEffect effect, bool locked, params string[] keys) =>
            expected.AddRange(keys.Select(k => Describe(section, k, effect, locked)));

        // 7.2 [Object]
        Rows(SceneSection.Object, none, true, "nickname");
        Rows(SceneSection.Object, transform, false, "pos", "rotate");
        Rows(SceneSection.Object, data | label | lanes | reinit, false, "archetype");
        Rows(SceneSection.Object, data | reinit, false, "loadout", "star", "spin", "dock_with");
        // The link of a jump connection: the connection tools change the pair, and the raw INI block can change it.
        Rows(SceneSection.Object, data | reinit, true, "goto");
        // The links of a trade lane: the lane tools change the lane, and the raw INI block can change them.
        Rows(SceneSection.Object, data | lanes | reinit, true, "prev_ring", "next_ring");
        Rows(SceneSection.Object, data | label, false, "tradelane_space_name", "ids_name");
        Rows(SceneSection.Object, data, false, "ids_info", "base", "reputation", "faction", "pilot", "behavior",
            "difficulty_level", "msg_id_prefix", "parent", "voice", "space_costume", "jump_effect", "comment",
            "atmosphere_range", "burn_color", "ambient_color", "ambient", "visit");
        Rows(SceneSection.Object, data | roles, false, "ring");
        Rows(SceneSection.Object, none, false, "size");
        // 7.3 [Zone]
        Rows(SceneSection.Zone, none, true, "nickname");
        Rows(SceneSection.Zone, transform, false, "pos", "rotate");
        // The applier adds Roles for a size only when the size changes the trade lane rule of the zone.
        Rows(SceneSection.Zone, transform | SceneEffect.Volume, false, "size");
        Rows(SceneSection.Zone, transform | roles | SceneEffect.Volume, false, "shape");
        Rows(SceneSection.Zone, data | roles, true, "encounter", "faction", "faction_weight");
        Rows(SceneSection.Zone, data | roles, false, "path_label");
        Rows(SceneSection.Zone, data | label, false, "ids_name");
        Rows(SceneSection.Zone, data, false, "ids_info", "edge_fraction", "property_flags", "property_fog_color", "visit",
            "sort", "music", "spacedust", "spacedust_maxparticles", "interference", "power_modifier", "drag_modifier",
            "comment", "lane_id", "tradelane_attack", "tradelane_down", "damage", "toughness", "density", "population_additive",
            "mission_eligible", "max_battle_size", "pop_type", "relief_time", "repop_time", "attack_ids", "mission_type",
            "usage", "vignette_type", "density_restriction");
        Rows(SceneSection.Zone, none, false, "zone_creation_distance", "spin", "reputation");
        // 7.4 [LightSource]: the Transform effect of a light sets its position and loads the lights again.
        Rows(SceneSection.LightSource, none, true, "nickname");
        Rows(SceneSection.LightSource, transform, false, "pos");
        Rows(SceneSection.LightSource, SceneEffect.Light, false, "color", "range", "type", "attenuation", "direction",
            "atten_curve");
        Rows(SceneSection.LightSource, none, false, "rotate", "ids_name", "color_curve");
        // 7.5 The system sections and universe.ini
        Rows(SceneSection.SystemInfo, SceneEffect.Environment, false, "space_color");
        Rows(SceneSection.SystemInfo, data, false, "local_faction");
        Rows(SceneSection.SystemInfo, none, false, "space_farclip", "rpop_solar_detection");
        Rows(SceneSection.Ambient, SceneEffect.Environment, false, "color");
        Rows(SceneSection.Background, SceneEffect.Starsphere, false, "basic_stars", "complex_stars", "nebulae");
        Rows(SceneSection.Dust, none, false, "spacedust", "dust", "spacedust_maxparticles");
        Rows(SceneSection.Music, none, false, "space", "danger", "battle");
        Rows(SceneSection.TexturePanels, none, false, "file");
        Rows(SceneSection.Preloads, none, true, "ship", "simple", "solar", "equipment", "snd", "voice");
        Rows(SceneSection.EncounterParameters, none, true, "nickname");
        Rows(SceneSection.EncounterParameters, none, false, "filename");
        Rows(SceneSection.UniverseSystem, none, true, "nickname", "file");
        Rows(SceneSection.UniverseSystem, data | label, false, "strid_name");
        Rows(SceneSection.UniverseSystem, data, false, "navmapscale");
        Rows(SceneSection.UniverseSystem, none, false, "ids_info", "pos", "msg_id_prefix", "visit", "name");
        // Section 12 of the M22 design: the [Nebula] and [Asteroids] items.
        Rows(SceneSection.Nebula, none, true, "zone");
        Rows(SceneSection.Nebula, none, false, "file");
        Rows(SceneSection.Asteroids, none, true, "zone");
        Rows(SceneSection.Asteroids, none, false, "file");

        Assert.Equal(expected.Order(StringComparer.Ordinal),
            SceneFields.Rows.Select(r => Describe(r.Section, r.Key, r.Effect, r.Locked)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EachRowHasEvidence() =>
        Assert.All(SceneFields.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Evidence), $"{r.Section} {r.Key}"));

    [Fact]
    public void TheLookupIgnoresLetterCaseAndEachDeferredEffectComesWithAnEffectThatRunsAtOnce()
    {
        Assert.Equal(SceneEffect.Transform, SceneFields.Effect(SceneSection.Object, "POS"));
        // Each deferred effect comes with an effect that runs at once, so the scene item holds the new values first.
        Assert.All(SceneFields.Rows.Where(r => (r.Effect & SceneEffect.Reinit) != 0),
            r => Assert.True((r.Effect & SceneEffect.Data) != 0, r.Key));
        Assert.All(SceneFields.Rows.Where(r => (r.Effect & SceneEffect.Volume) != 0),
            r => Assert.True((r.Effect & SceneEffect.Transform) != 0, r.Key));
        Assert.Equal(SceneEffect.Starsphere, SceneFields.All(SceneSection.Background));
        Assert.Equal(SceneEffect.None, SceneFields.All(SceneSection.Music));
    }

    [Fact]
    public void ASectionTypeMapsToItsSceneSectionOnlyInItsFile()
    {
        Assert.Equal(SceneSection.Zone, SceneFields.SectionOf("zone", universe: false));
        Assert.Equal(SceneSection.Preloads, SceneFields.SectionOf("Archetype", universe: false));
        Assert.Equal(SceneSection.UniverseSystem, SceneFields.SectionOf("System", universe: true));
        Assert.Null(SceneFields.SectionOf("system", universe: false));
        Assert.Null(SceneFields.SectionOf("Object", universe: true));
        // The [Nebula] and [Asteroids] sections of a system file are the scene items of milestone M22.
        Assert.Equal(SceneSection.Nebula, SceneFields.SectionOf("nebula", universe: false));
        Assert.Equal(SceneSection.Asteroids, SceneFields.SectionOf("Asteroids", universe: false));
        Assert.Null(SceneFields.SectionOf("Nebula", universe: true));
    }

    /// <summary>
    /// The rows of a nebula and a field (section 12 of the M22 design): the zone is the key of the section, so it is
    /// locked. The file has no effect on the open scene, because the engine reads the file when the universe loads. The
    /// coverage test above needs exactly these two rows for each section.
    /// </summary>
    [Fact]
    public void NebulaAndAsteroidsRows()
    {
        foreach (var section in new[] { SceneSection.Nebula, SceneSection.Asteroids })
        {
            var rows = SceneFields.Rows.Where(r => r.Section == section).OrderBy(r => r.Key).ToList();
            Assert.Equal(["file", "zone"], rows.Select(r => r.Key));
            Assert.Equal([(SceneEffect.None, false), (SceneEffect.None, true)], rows.Select(r => (r.Effect, r.Locked)));
            Assert.Equal(SceneEffect.None, SceneFields.All(section));
            Assert.Contains("opens again", rows[0].Evidence);
        }
    }

    private static string Describe(SceneSection section, string key, SceneEffect effect, bool locked) =>
        $"{section} {key}: {effect}{(locked ? ", locked" : "")}";
}
