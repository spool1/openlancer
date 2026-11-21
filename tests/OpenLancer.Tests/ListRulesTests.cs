using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Schema.Audio;
using LibreLancer.Data.Schema.Characters;
using LibreLancer.Data.Schema.Effects;
using LibreLancer.Data.Schema.Equipment;
using LibreLancer.Data.Schema.Fuses;
using LibreLancer.Data.Schema.Goods;
using LibreLancer.Data.Schema.InitialWorld;
using LibreLancer.Data.Schema.Pilots;
using LibreLancer.Data.Schema.Ships;
using LibreLancer.Data.Schema.Solar;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rule data of the data lists (section 20.1 item 1 of the M21 design). A renamed key or class of an upstream merge
/// breaks a rule here, in the build, and not in the hands of a user.
/// </summary>
public sealed class ListRulesTests
{
    /// <summary>The <c>[ParsedIni]</c> class of each role whose files the generated parsers read.</summary>
    private static readonly Dictionary<string, Type> Parsed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solar"] = typeof(SolararchIni),
        ["stars"] = typeof(StararchIni),
        ["loadouts"] = typeof(LoadoutsIni),
        ["groups"] = typeof(InitialWorldIni),
        ["pilots"] = typeof(PilotsIni),
        ["sounds"] = typeof(AudioIni),
        ["effects"] = typeof(EffectsIni),
        ["explosions"] = typeof(ExplosionsIni),
        ["fuses"] = typeof(FuseIni),
        ["equipment"] = typeof(EquipmentIni),
        ["ships"] = typeof(ShiparchIni),
        ["goods"] = typeof(GoodsIni),
        ["markets"] = typeof(MarketsIni),
        ["costumes"] = typeof(CostumesIni),
        ["system"] = typeof(StarSystem),
        // The room files of the bases (decision D135 of the M8b design).
        ["room"] = typeof(Room),
        // The nebula and asteroid field files that the system files name (milestone M3).
        ["nebula_files"] = typeof(Nebula),
        ["field_files"] = typeof(AsteroidField),
        // The mission scripts, the NPC ship builds and the formation shapes (section 14.1 of the M6a design).
        ["mission"] = typeof(LibreLancer.Data.Schema.Missions.MissionIni),
        ["mission_ships"] = typeof(LibreLancer.Data.Schema.Missions.NPCShipIni),
        ["specific_npc"] = typeof(LibreLancer.Data.Schema.Missions.SpecificNPCIni),
        ["formations"] = typeof(LibreLancer.Data.Schema.Missions.FormationsIni),
    };

    /// <summary>The section classes of the files that the engine reads itself and gives section by section to a
    /// generated parser.</summary>
    private static readonly Dictionary<(string Role, string Section), Type> Classes = new()
    {
        [("universe", "system")] = typeof(LibreLancer.Data.Schema.Universe.StarSystem),
        [("universe", "base")] = typeof(Base),
        [("bodyparts", "head")] = typeof(Bodypart),
        [("bodyparts", "body")] = typeof(Bodypart),
        [("bodyparts", "lefthand")] = typeof(Bodypart),
        [("bodyparts", "righthand")] = typeof(Bodypart),
        [("bodyparts", "accessory")] = typeof(Accessory),
        // The asteroid archetypes of the Assets panel (milestone M23, AsteroidArchIni.cs), and the mines of the cutscene
        // templates (section 12.1 of the M8b design).
        [("asteroids", "asteroid")] = typeof(Asteroid),
        [("asteroids", "asteroidmine")] = typeof(Asteroid),
    };

    /// <summary>The keys that a hand parser reads, for the files without a section class (VoicesIni.cs:32-44,
    /// GraphIni.cs:24-63).</summary>
    private static readonly Dictionary<(string Role, string Section), string[]> HandKeys = new()
    {
        [("voices", "voice")] = ["nickname", "extend"],
        [("igraph", "igraph")] = ["nickname", "type", "point"],
    };

    private static readonly ListRules Rules = ListRules.Retail;

    /// <summary>The schema of a section type of a role, or null when the role has none (a data-only role).</summary>
    private static SectionSchema? Schema(string role, string section)
    {
        if (Parsed.TryGetValue(role, out var ini))
            return SchemaCatalog.Section(ini, section);
        return Classes.TryGetValue((role, section.ToLowerInvariant()), out var type)
            ? SchemaCatalog.SectionClass(type, section)
            : null;
    }

    private static bool Knows(string role, string section, string key) =>
        Schema(role, section)?.Key(key) != null ||
        HandKeys.TryGetValue((role, section.ToLowerInvariant()), out var keys) &&
        keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheResourceParses()
    {
        Assert.NotEmpty(Rules.Roles);
        Assert.NotEmpty(Rules.Lists);
        Assert.NotEmpty(Rules.All);
        Assert.Throws<InvalidDataException>(() => ListRules.Parse("{ \"roles\": {} }"));
    }

    [Fact]
    public void EachRuleNamesASectionTypeAndAKeyOfTheSchemaOfItsRole()
    {
        foreach (var rule in Rules.All)
        {
            var name = $"{rule.Role} [{rule.Section}] {rule.Key}";
            Assert.True(Rules.Roles.ContainsKey(rule.Role), name);
            var schema = Schema(rule.Role, rule.Section);
            Assert.True(schema != null, $"{name}: the schema of the role has no section type [{rule.Section}].");
            var key = schema.Key(rule.Key);
            Assert.True(key != null, $"{name}: the schema has no key '{rule.Key}'.");
            // The line type of the key is a string, a string array, or an entry handler.
            Assert.True(key.ClrType == null || key.ClrType == typeof(string) || key.ClrType == typeof(string[]),
                $"{name}: the key reads {key.ClrType}.");
        }
    }

    [Fact]
    public void EachListSourceNamesSectionTypesAndKeysOfItsRole()
    {
        foreach (var list in Rules.Lists.Values)
        {
            if (list.Enum != null)
            {
                Assert.True(typeof(LibreLancer.Data.FLHash).Assembly.GetType(list.Enum) is { IsEnum: true }, list.Id);
                continue;
            }
            var source = list.Source!;
            // A files list has no sections: its entries are the paths of files (section 5.6 of the M8b design). The
            // cutscenes are .thn files of any folder. The nebula and asteroid files are the INI files of one folder
            // whose sections are all sections of one [ParsedIni] class (milestone M22). The models are the model files
            // below the data folder (milestone M23).
            // The clips are the scripts of the animation files that the [Animations] lines of bodyparts.ini name
            // (section 12.1 of the M8b design, BodypartsIni.cs:30-36).
            if (source.AnimationScripts)
            {
                Assert.Equal(("bodyparts", 0), (source.Role, source.Sections.Count));
                continue;
            }
            if (source.Files != null)
            {
                Assert.True(source.Files switch
                {
                    [".thn"] => source.Folder == null && source.FileClass == null,
                    [".ini"] => source.Folder != null && source.FileClassType != null,
                    [".cmp", ".3db", ".sph"] => source.Folder == "." && source.FileClass == null,
                    _ => false,
                }, list.Id);
                continue;
            }

            var role = source.Scoped ? source.Role ?? "system" : source.Role!;
            Assert.True(Rules.Roles.ContainsKey(role), list.Id);
            if (!Rules.Roles[role].Engine)
                continue;
            foreach (var section in source.Sections)
            {
                var keys = (source.Keys ?? [SectionKeys.For(section).KeyEntry!]).Concat(source.Where.Keys)
                    .Concat(list.Requires).Concat(list.Detail);
                foreach (var key in keys)
                    Assert.True(Knows(role, section, key), $"{list.Id}: [{section}] {key} in role {role}");
            }
        }
    }

    [Fact]
    public void EachRuleNamesKnownListsAndNoTwoRulesShareAnAddress()
    {
        foreach (var rule in Rules.All)
        {
            Assert.NotEmpty(rule.Lists);
            Assert.All(rule.Lists, l => Assert.True(Rules.Lists.ContainsKey(l), $"{rule.Key}: list {l}"));
        }
        var addresses = Rules.All.GroupBy(r => (r.Role.ToLowerInvariant(), r.Section.ToLowerInvariant(),
            r.Key.ToLowerInvariant(), r.Index)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(addresses);
    }

    /// <summary>Decisions D109 and D82: a closed rule needs an engine line and a list whose files the engine reads. A
    /// required rule needs an engine line and the effect drop, crash or loadBreak. A room without set_script crashes the
    /// game (review finding R3 of the M8b design).</summary>
    [Fact]
    public void ClosedAndRequiredRulesHaveEvidence()
    {
        foreach (var rule in Rules.All)
        {
            var name = $"{rule.Role} [{rule.Section}] {rule.Key} ({rule.Index})";
            if (rule.Closed)
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Evidence), name);
                Assert.False(rule.Evidence!.StartsWith("Open", StringComparison.Ordinal), name);
                foreach (var list in rule.Lists.Select(l => Rules.Lists[l]))
                {
                    var role = list.Source is { Scoped: false, Role: { } r } ? r : null;
                    Assert.True(role == null || Rules.Roles[role].Engine, $"{name}: list {list.Id} is data only.");
                }
            }
            else
                Assert.Equal(RuleEffect.None, rule.Effect);
            if (rule.Required || rule.RequiredWith != null)
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Evidence), name);
                Assert.Contains(rule.Effect, new[] { RuleEffect.Drop, RuleEffect.Crash, RuleEffect.LoadBreak });
            }
            // A crash rule tells when the game crashes, for example when the player docks.
            if (rule.Effect == RuleEffect.Crash)
                Assert.False(string.IsNullOrWhiteSpace(rule.Consequence), name);
        }
    }

    /// <summary>Each error tells what the game does with the data, in plain words (decision D82, as decided on
    /// 2026-10-18).</summary>
    [Fact]
    public void EachClosedRuleTellsWhatTheGameDoes()
    {
        foreach (var rule in Rules.All.Where(r => r.Closed))
        {
            var name = $"{rule.Role} [{rule.Section}] {rule.Key} ({rule.Index})";
            Assert.False(rule.ConsequenceText.Contains("engine", StringComparison.OrdinalIgnoreCase), name);
            Assert.EndsWith(".", rule.ConsequenceText);
            Assert.EndsWith(".", rule.MissingText);
        }
        var curve = Assert.Single(Rules.Rules("system", "LightSource", "atten_curve"));
        Assert.Equal("The game stops loading with this value.", curve.ConsequenceText);
        var explosion = Assert.Single(Rules.Rules("equipment", "Munition", "explosion_arch"));
        Assert.Equal("The game stops loading without this key.", explosion.MissingText);
        var dock = Assert.Single(Rules.Rules("system", "Object", "dock_with"));
        Assert.Equal((RuleEffect.Crash, "The game crashes when the player docks here."), (dock.Effect, dock.ConsequenceText));
        Assert.Equal("The game drops this object.", Assert.Single(Rules.Rules("system", "Object", "archetype")).ConsequenceText);
    }

    /// <summary>A minimal rule data with one files list, for the parse tests of the M22 fields.</summary>
    private static string FilesRules(string listSource, string ruleFlags) => $$"""
        {
          "roles": { "system": { "systemFiles": true } },
          "lists": [
            { "id": "zone_of_system", "label": "Zone", "kind": "Zone", "source": { "scope": "file", "sections": ["zone"] } },
            { "id": "nebula_file", "label": "Nebula file", "kind": "Nebula", "source": {{listSource}} }
          ],
          "rules": [
            { "role": "system", "section": "Nebula", "key": "file", "list": "nebula_file", "closed": true,
              "effect": "loadBreak", "evidence": "Schema\\Universe\\Nebula.cs:38-45"{{ruleFlags}} }
          ]
        }
        """;

    /// <summary>The flag exists accepts a file outside the list, so it needs a list of files (decision D160 of the M22
    /// design).</summary>
    [Fact]
    public void ExistsNeedsAFilesList()
    {
        var files = ListRules.Parse(FilesRules("""{ "files": ".ini", "folder": "SOLAR\\NEBULA" }""", """, "exists": true"""));
        Assert.True(Assert.Single(files.Rules("system", "Nebula", "file")).Exists);

        var error = Assert.Throws<InvalidDataException>(() => ListRules.Parse(
            FilesRules("""{ "files": ".ini" }""", """, "exists": true""").Replace("\"list\": \"nebula_file\"",
                "\"list\": \"zone_of_system\"", StringComparison.Ordinal)));
        Assert.Contains("needs a files list", error.Message);
        Assert.False(Assert.Single(ListRules.Parse(FilesRules("""{ "files": ".ini" }""", "")).All).Exists);
    }

    /// <summary>Decision D293 of the M23 design: the <c>files</c> key of a list source takes one extension or a list of
    /// extensions, each with its dot.</summary>
    [Fact]
    public void FilesTakeOneExtensionOrAList()
    {
        Assert.Equal([".ini"], ListRules.Parse(FilesRules("""{ "files": ".ini" }""", "")).Lists["nebula_file"].Source!.Files!);
        Assert.Equal([".cmp", ".3db"],
            ListRules.Parse(FilesRules("""{ "files": [".cmp", ".3db"] }""", "")).Lists["nebula_file"].Source!.Files!);
        Assert.Throws<InvalidDataException>(() => ListRules.Parse(FilesRules("""{ "files": [] }""", "")));
        Assert.Throws<InvalidDataException>(() => ListRules.Parse(FilesRules("""{ "files": ["cmp"] }""", "")));

        // The retail lists of the asset browser: the asteroid archetypes and the models below the data folder.
        Assert.Equal("asteroids", Rules.Lists["asteroid"].Source!.Role);
        Assert.Equal(["Asteroid"], Rules.Lists["asteroid"].Source!.Sections);
        Assert.Equal("asteroids", Rules.Roles["asteroids"].Data);
        Assert.Equal([".cmp", ".3db", ".sph"], Rules.Lists["model"].Source!.Files!);
        Assert.Equal(".", Rules.Lists["model"].Source!.Folder);
    }

    /// <summary>The sourceKind animationScripts gives the clips of the animation files of a role (section 12.1 of the
    /// M8b design). It needs a role and no sections, values, files or scope. Another source kind is an error.</summary>
    [Fact]
    public void TheAnimationSourceKindNeedsARoleAndNoSections()
    {
        Assert.True(Rules.Lists["animation"].Source!.AnimationScripts);
        Assert.Equal(("Animation", "Animation"), (Rules.Lists["animation"].Label, Rules.Lists["animation"].Kind));
        Assert.True(ListRules.Parse(FilesRules("""{ "role": "system", "sourceKind": "animationScripts" }""", ""))
            .Lists["nebula_file"].Source!.AnimationScripts);
        Assert.Contains("needs a role", Assert.Throws<InvalidDataException>(() => ListRules.Parse(
            FilesRules("""{ "sourceKind": "animationScripts" }""", ""))).Message);
        Assert.Contains("needs a role", Assert.Throws<InvalidDataException>(() => ListRules.Parse(
            FilesRules("""{ "role": "system", "sourceKind": "animationScripts", "sections": ["Animations"] }""", ""))).Message);
        Assert.Contains("is not known", Assert.Throws<InvalidDataException>(() => ListRules.Parse(
            FilesRules("""{ "role": "system", "sourceKind": "sounds" }""", ""))).Message);
    }

    /// <summary>Decision D267 of the M23 design: the categories of the Assets panel are rule data. Each category names
    /// a list of the rule data or a known kind, and a known split.</summary>
    [Fact]
    public void TheAssetCategoriesParse()
    {
        Assert.Equal(["groups", "solars", "ships", "asteroids", "equipment", "effects", "loadouts", "factions", "models", "prefabs"],
            Rules.AssetCategories.Select(c => c.Id));
        Assert.All(Rules.AssetCategories.Where(c => c.List != null), c => Assert.True(Rules.Lists.ContainsKey(c.List!), c.Id));
        Assert.Equal(("propGroups", null), (Rules.AssetCategories[0].Kind, Rules.AssetCategories[0].List));
        Assert.Equal(["sceneTemplates", null, null, "section", null, null, null, "folder", null],
            Rules.AssetCategories.Skip(1).Select(c => c.Split));
        Assert.Equal(("missionPrefabs", null), (Rules.AssetCategories[^1].Kind, Rules.AssetCategories[^1].List));

        string With(string category) => FilesRules("""{ "files": ".ini" }""", "")[..^1].TrimEnd() +
                                        $$""", "assetCategories": [ {{category}} ] }""";
        Assert.Equal("nebulae", Assert.Single(ListRules.Parse(With(
            """{ "id": "nebulae", "label": "Nebulae", "list": "nebula_file" }""")).AssetCategories).Id);
        foreach (var bad in new[]
                 {
                     """{ "id": "x", "label": "X", "list": "no_such_list" }""",
                     """{ "id": "x", "label": "X" }""",
                     """{ "id": "x", "label": "X", "kind": "propGroups", "list": "nebula_file" }""",
                     """{ "id": "x", "label": "X", "kind": "costumes" }""",
                     """{ "id": "x/y", "label": "X", "list": "nebula_file" }""",
                     """{ "id": "x", "label": "X", "list": "nebula_file", "split": "colors" }""",
                 })
            Assert.Throws<InvalidDataException>(() => ListRules.Parse(With(bad)));
    }

    /// <summary>
    /// The rule data of the builders of the agent tools (decisions D730 and D731 of the MCP1 design, section 9.3). The
    /// wreck takes the archetypes of the type mission_satellite, and each route template gives the keys of a zone of the
    /// engine schema. So does the death zone of the sun of a new system. The texts pass the STE lint, because the agent
    /// reads them. A template that gives a line that the builder writes, or that has no place for the label or the
    /// usage, does not load. Nor does a death zone without a size.
    /// </summary>
    [Fact]
    public void TheBuilderRulesParseAndTheirTextsPassTheSteLint()
    {
        var builders = Rules.Builders;
        var wreck = builders.Wreck!;

        Assert.Equal(["mission_satellite"], wreck.Types);
        Assert.Equal([new BuilderLine("visit", "16")], wreck.Lines);
        Assert.Equal(["patrol", "trade"], builders.Routes.Select(r => r.Usage));
        Assert.All(builders.Routes, r => Assert.Equal(750, r.Radius));
        Assert.Equal("attack_patrol", builders.Route("PATROL")!.Lines.Single(l => l.Key == "pop_type").Value);
        Assert.Null(builders.Route("mining"));
        Assert.All(wreck.Lines, l => Assert.True(Knows("system", "Object", l.Key), l.Key));
        Assert.All(builders.Routes.SelectMany(r => r.Lines), l => Assert.True(Knows("system", "zone", l.Key), l.Key));
        var sunZone = builders.SunZone!;
        Assert.Equal((10000f, 1000f), (sunZone.Size, sunZone.AtmosphereMargin));
        Assert.Equal([new BuilderLine("damage", "10000"), new BuilderLine("sort", "99.5"), new BuilderLine("density", "0"),
            new BuilderLine("relief_time", "0")], sunZone.Lines);
        Assert.All(sunZone.Lines, l => Assert.True(Knows("system", "zone", l.Key), l.Key));
        var texts = new[] { wreck.Help, wreck.Name, wreck.Info }.Concat(builders.Routes.Select(r => r.Help));
        Assert.Empty(texts.SelectMany(t => SteLint.Problems(t).Select(p => $"{p}: {t}")));

        string With(string builder) => FilesRules("""{ "files": ".ini" }""", "")[..^1].TrimEnd() +
                                       $$""", "connectionTypes": ["jump_gate"], "sceneBuilders": {{builder}} }""";
        const string route = """{ "key": "path_label" }, { "key": "usage" }""";
        Assert.Equal("trade", Assert.Single(ListRules.Parse(With(
            $$"""{ "routes": [ { "usage": "trade", "radius": 500, "help": "A route.", "lines": [ {{route}} ] } ] }"""))
            .Builders.Routes).Usage);
        Assert.Same(SceneBuilders.None, ListRules.Parse(FilesRules("""{ "files": ".ini" }""", "")).Builders);
        foreach (var bad in new[]
                 {
                     $$"""{ "routes": [ { "usage": "x", "radius": 500, "help": "A.", "lines": [ { "key": "usage" } ] } ] }""",
                     $$"""{ "routes": [ { "usage": "x", "radius": 0, "help": "A.", "lines": [ {{route}} ] } ] }""",
                     $$"""{ "routes": [ { "usage": "x", "radius": 5, "help": "A.", "lines": [ {{route}}, { "key": "size", "value": "1" } ] } ] }""",
                     $$"""{ "routes": [ { "usage": "x", "radius": 5, "help": "A.", "lines": [ {{route}}, { "key": "sort" } ] } ] }""",
                     $$"""{ "routes": [ { "usage": "x", "radius": 5, "help": "A.", "lines": [ {{route}}, { "key": "sort", "value": "1, 2" } ] } ] }""",
                     $$"""{ "routes": [ { "usage": "x", "radius": 5, "help": "A.", "lines": [ {{route}} ] }, { "usage": "X", "radius": 5, "help": "A.", "lines": [ {{route}} ] } ] }""",
                     """{ "wreck": { "help": "A.", "types": [], "name": "W", "info": "I." } }""",
                     """{ "wreck": { "help": "A.", "types": ["jump_gate"], "name": "W", "info": "I." } }""",
                     """{ "wreck": { "help": "A.", "types": ["x"], "name": "W", "info": "I.", "lines": [ { "key": "visit" } ] } }""",
                     """{ "wreck": { "help": "A.", "types": ["x"], "name": "W", "info": "I.", "lines": [ { "key": "loadout", "value": "a" } ] } }""",
                     """{ "system": { "sunDeathZone": { "size": 0, "atmosphereMargin": 1000 } } }""",
                     """{ "system": { "sunDeathZone": { "size": 10, "atmosphereMargin": -1 } } }""",
                     """{ "system": { "sunDeathZone": { "size": 10, "atmosphereMargin": 0, "lines": [ { "key": "damage" } ] } } }""",
                     """{ "system": { "sunDeathZone": { "size": 10, "atmosphereMargin": 0, "lines": [ { "key": "size", "value": "1" } ] } } }""",
                     """{ "system": { } }""",
                 })
            Assert.Throws<InvalidDataException>(() => ListRules.Parse(With(bad)));
    }

    /// <summary>
    /// The rule data of a new jump connection: the tunnel and the smallest distance from the center. It also has the
    /// lines and the traffic zone of each kind, and the loadout and the pilot of each house. Each line is a key of the
    /// engine schema. A name pattern without the system part does not load. Nor does a line that the builder writes, a
    /// traffic zone without a size or a house that repeats.
    /// </summary>
    [Fact]
    public void TheConnectionBuilderRulesParse()
    {
        var connection = Rules.Builders.Connection!;

        Assert.Equal(("gate_tunnel_bretonia", 50000f), (connection.Tunnel, connection.MinRadius));
        Assert.Equal(("jumpgate", 66145, "{system} Jump Gate", 4000f),
            (connection.Gate.Archetype, connection.Gate.Infocard, connection.Gate.Name, connection.Gate.TrafficSize));
        Assert.Equal(("jumphole", 66146, "{system} Jump Hole"),
            (connection.Hole.Archetype, connection.Hole.Infocard, connection.Hole.Name));
        Assert.Equal([new BuilderLine("jump_effect", "jump_effect_hole"), new BuilderLine("visit", "0")], connection.Hole.Lines);
        Assert.Equal("wormhole", connection.Hole.Traffic.Single(l => l.Key == "pop_type").Value);
        Assert.Equal("jumpgate", connection.Gate.Traffic.Single(l => l.Key == "pop_type").Value);
        Assert.Same(connection.Hole, connection.For(OpenLancer.Data.Editing.Universe.ConnectionKind.Hole));
        Assert.Equal(new HouseLoadout("br", "jumpgate_br_02", "pilot_solar_easiest"), connection.HouseOf("br_n_grp"));
        Assert.Equal("jumpgate_li_01", connection.HouseOf("LI_P_GRP")!.Loadout);
        Assert.Null(connection.HouseOf("fc_x_grp"));
        Assert.Null(connection.HouseOf(null));
        Assert.All(connection.Gate.Lines.Concat(connection.Hole.Lines), l => Assert.True(Knows("system", "Object", l.Key), l.Key));
        Assert.All(connection.Gate.Traffic.Concat(connection.Hole.Traffic),
            l => Assert.True(Knows("system", "zone", l.Key), l.Key));

        string With(string builder) => FilesRules("""{ "files": ".ini" }""", "")[..^1].TrimEnd() +
                                       $$""", "connectionTypes": ["jump_gate"], "sceneBuilders": { "connection": {{builder}} } }""";
        const string end = "\"archetype\": \"a\", \"infocard\": 1, \"name\": \"{system} Gate\", \"traffic\": { \"size\": 10 }";
        Assert.Equal("t", ListRules.Parse(With($$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end}} }, "hole": { {{end}} } }"""))
            .Builders.Connection!.Tunnel);
        foreach (var bad in new[]
                 {
                     $$"""{ "tunnel": "t, u", "minRadius": 1, "gate": { {{end}} }, "hole": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 0, "gate": { {{end}} }, "hole": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end.Replace("{system} ", "")}} }, "hole": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end.Replace("10", "0")}} }, "hole": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end}}, "lines": [ { "key": "loadout", "value": "x" } ] }, "hole": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end}}, "lines": [ { "key": "mass", "value": "1" } ] }, "hole": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end}}, "lines": [ { "key": "visit" } ] }, "hole": { {{end}} } }""",
                     $$"""{ "tunnel": "t", "minRadius": 1, "gate": { {{end}} }, "hole": { {{end}} }, "houses": [ { "house": "li", "loadout": "a", "pilot": "b" }, { "house": "LI", "loadout": "a", "pilot": "b" } ] }""",
                 })
            Assert.Throws<InvalidDataException>(() => ListRules.Parse(With(bad)));
    }

    /// <summary>
    /// The rule data of a new trade lane. It gives the archetype, the name, the infocard and the lines of a ring. It also
    /// gives the spacing with the retail range, the clearance, the lane zone, and the loadout and the pilot of each house.
    /// Each line is a key of the engine schema. A spacing outside the retail range does not load. Nor does a line that the builder
    /// writes, a ring line of another key, a zone without a width or a house that repeats.
    /// </summary>
    [Fact]
    public void TheLaneBuilderRulesParse()
    {
        var lane = Rules.Builders.Lane!;

        Assert.Equal(("Trade_Lane_Ring", 60245, 66170), (lane.Archetype, lane.Name, lane.Infocard));
        Assert.Equal((7500f, 5165f, 9445f, 2000f), (lane.Spacing, lane.RetailMin, lane.RetailMax, lane.Clearance));
        Assert.Equal([new BuilderLine("behavior", "NOTHING"), new BuilderLine("difficulty_level", "10")], lane.Lines);
        Assert.Equal((1000f, 500f), (lane.ZoneMargin, lane.ZoneWidth));
        Assert.Equal("major_tradelane", lane.Zone.Single(l => l.Key == "pop_type").Value);
        Assert.Equal(new HouseLoadout("li", "trade_lane_ring_li_01", "pilot_solar_hard"), lane.HouseOf("li_n_grp"));
        Assert.Equal("trade_lane_ring_co_01", lane.HouseOf("gd_bh_grp")!.Loadout);
        Assert.Null(lane.HouseOf("fc_x_grp"));
        Assert.All(lane.Lines, l => Assert.True(Knows("system", "Object", l.Key), l.Key));
        Assert.All(lane.Zone, l => Assert.True(Knows("system", "zone", l.Key), l.Key));

        string With(string builder) => FilesRules("""{ "files": ".ini" }""", "")[..^1].TrimEnd() +
                                       $$""", "connectionTypes": ["jump_gate"], "sceneBuilders": { "lane": {{builder}} } }""";
        const string head = "\"archetype\": \"r\", \"name\": 1, \"infocard\": 2, \"spacing\": 10, \"clearance\": 0";
        const string zone = "\"zone\": { \"margin\": 0, \"width\": 1 }";
        Assert.Equal(10f, ListRules.Parse(With($$"""{ {{head}}, "retailRange": [5, 20], {{zone}} }""")).Builders.Lane!.Spacing);
        foreach (var bad in new[]
                 {
                     $$"""{ {{head}}, "retailRange": [11, 20], {{zone}} }""",
                     $$"""{ {{head}}, "retailRange": [5], {{zone}} }""",
                     $$"""{ {{head.Replace("\"name\": 1", "\"name\": 0")}}, "retailRange": [5, 20], {{zone}} }""",
                     $$"""{ {{head.Replace("\"clearance\": 0", "\"clearance\": -1")}}, "retailRange": [5, 20], {{zone}} }""",
                     $$"""{ {{head}}, "retailRange": [5, 20], "zone": { "margin": 0, "width": 0 } }""",
                     $$"""{ {{head}}, "retailRange": [5, 20], {{zone}}, "lines": [ { "key": "pilot", "value": "p" } ] }""",
                     $$"""{ {{head}}, "retailRange": [5, 20], {{zone}}, "lines": [ { "key": "visit", "value": "0" } ] }""",
                     $$"""{ {{head}}, "retailRange": [5, 20], "zone": { "margin": 0, "width": 1, "lines": [ { "key": "lane_id", "value": "1" } ] } }""",
                     $$"""{ {{head}}, "retailRange": [5, 20], {{zone}}, "houses": [ { "house": "li", "loadout": "a", "pilot": "b" }, { "house": "LI", "loadout": "a", "pilot": "b" } ] }""",
                 })
            Assert.Throws<InvalidDataException>(() => ListRules.Parse(With(bad)));
    }

    [Fact]
    public void FolderAndFileClassParse()
    {
        var rules = ListRules.Parse(FilesRules(
            """{ "files": ".ini", "folder": "SOLAR\\NEBULA", "fileClass": "LibreLancer.Data.Schema.Universe.Nebula" }""",
            ""));
        var source = rules.Lists["nebula_file"].Source!;
        Assert.Equal([".ini"], source.Files!);
        Assert.Equal((@"SOLAR\NEBULA", typeof(Nebula)), (source.Folder, source.FileClassType));

        // A folder or a class needs a files list, and the class must be a [ParsedIni] class of the engine.
        Assert.Throws<InvalidDataException>(() => ListRules.Parse(FilesRules(
            """{ "role": "system", "sections": ["zone"], "folder": "SOLAR" }""", "")));
        Assert.Throws<InvalidDataException>(() => ListRules.Parse(FilesRules(
            """{ "files": ".ini", "fileClass": "LibreLancer.Data.Schema.Universe.Zone" }""", "")));
        Assert.Throws<InvalidDataException>(() => ListRules.Parse(FilesRules(
            """{ "files": ".ini", "fileClass": "No.Such.Class" }""", "")));

        // The retail lists of the nebula and asteroid files.
        Assert.Equal((@"SOLAR\NEBULA", typeof(Nebula)),
            (Rules.Lists["nebula_file"].Source!.Folder, Rules.Lists["nebula_file"].Source!.FileClassType));
        Assert.Equal((@"SOLAR\ASTEROIDS", typeof(AsteroidField)),
            (Rules.Lists["asteroid_file"].Source!.Folder, Rules.Lists["asteroid_file"].Source!.FileClassType));
    }

    /// <summary>
    /// The rules of the zone and the file of a nebula and a field (section 11 of the M22 design). A zone that the system
    /// does not have makes the engine drop the item. A file that does not exist makes the game crash while it loads the
    /// data. A section without a file line is dropped. NebulaRuleRetailTests proves the three effects.
    /// </summary>
    [Fact]
    public void TheZoneRulesHaveEvidence()
    {
        foreach (var (section, item) in new[] { ("Nebula", "nebula"), ("Asteroids", "asteroid field") })
        {
            var zone = Assert.Single(Rules.Rules("system", section, "zone"));
            Assert.Equal(["zone_of_system"], zone.Lists);
            Assert.Equal(("file", true, true, RuleEffect.Drop), (zone.Scope, zone.Closed, zone.Required, zone.Effect));
            Assert.Equal($"The game drops this {item}.", zone.ConsequenceText);
            Assert.Contains("GameItemDb.cs:", zone.Evidence);

            var file = Assert.Single(Rules.Rules("system", section, "file"));
            Assert.Equal((true, true, true, RuleEffect.LoadBreak), (file.Closed, file.Required, file.Exists, file.Effect));
            Assert.Equal(section == "Nebula" ? ["nebula_file"] : ["asteroid_file"], file.Lists);
            Assert.Equal("The game crashes when it loads the data.", file.ConsequenceText);
            Assert.Equal($"The game drops this {item}.", file.MissingText);
            Assert.Contains("FileSystem.cs:", file.Evidence);
        }
    }

    [Fact]
    public void ARuleWithValuesGivesOneRuleForEachValue()
    {
        var gotos = Rules.Rules("system", "OBJECT", "GoTo");
        Assert.Equal([0, 1, 2], gotos.Select(r => r.Index));
        Assert.Equal(["System", "Exit object", "Tunnel"], gotos.Select(r => r.Label));
        Assert.Equal(0, gotos[1].SystemValue);
        Assert.True(gotos[1].Closed);
        Assert.False(gotos[2].Closed);
        Assert.Equal(["solar_archetype", "ship"], Assert.Single(Rules.Rules("loadouts", "Loadout", "archetype")).Lists);
        Assert.Empty(Rules.Rules("system", "Object", "no_such_key"));
    }
}

/// <summary>The files of the roles, as the engine finds them (section 20.1 item 2 of the M21 design).</summary>
public sealed class DataFilesTests
{
    private const string FreelancerIni = """
        [Freelancer]
        data path = ..\data

        [Data]
        solar = solar\solararch.ini
        SOLAR = solar\more_solar.ini
        stars = solar\first_stars.ini
        stars = solar\last_stars.ini
        explosions = fx\explosions.ini
        debris = fx\explosions.ini
        equipment = equipment\weapon_equip.ini
        WeaponModDB = equipment\WeaponModDB.ini
        universe = universe\universe.ini
        """;

    /// <summary>Install paths compare without letter case.</summary>
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    private static DataFiles Files(TestWorkspace ws, string freelancerIni)
    {
        ws.WriteBase(@"EXE\freelancer.ini", freelancerIni);
        return ws.Session("act").DataFiles;
    }

    [Fact]
    public void TheRolesGiveTheFilesAsTheEngineFindsThem()
    {
        using var ws = SystemWorkspace.Create();
        var files = Files(ws, FreelancerIni);

        // All entries of a name, in load order, without letter case.
        Assert.Equal([@"DATA\solar\solararch.ini", @"DATA\solar\more_solar.ini"], files.Paths("solar"), IgnoreCase);
        // The last entry of a name.
        Assert.Equal([@"DATA\solar\last_stars.ini"], files.Paths("stars"), IgnoreCase);
        // Fixed paths, relative to the data folder. The engine reads initialworld.ini without a [Data] entry.
        Assert.Equal([@"DATA\initialworld.ini"], files.Paths("groups"), IgnoreCase);
        Assert.Equal([@"DATA\MISSIONS\pilots_population.ini", @"DATA\MISSIONS\pilots_story.ini"], files.Paths("pilots"),
            IgnoreCase);
        // A data-only role: retail reads the file, the fork does not. The [Data] entry WeaponModDB does not count.
        Assert.Equal([@"DATA\EQUIPMENT\WeaponModDB.ini"], files.Paths("weapon_mod"), IgnoreCase);
        Assert.False(files.Engine("weapon_mod"));
        Assert.True(files.Engine("equipment"));
        // The universe and the system files.
        Assert.Equal([SystemWorkspace.UniversePath], files.Paths("universe"), IgnoreCase);
        Assert.Contains(SystemWorkspace.Li01Path, files.Paths("system"), StringComparer.OrdinalIgnoreCase);
        Assert.Empty(files.Paths("no_such_role"));
    }

    [Fact]
    public void APathCanHaveMoreThanOneRole()
    {
        using var ws = SystemWorkspace.Create();
        var files = Files(ws, FreelancerIni);

        Assert.Equal(["explosions"], files.RolesOf(@"data\FX\EXPLOSIONS.INI"));
        Assert.Equal(["system"], files.RolesOf(SystemWorkspace.Li01Path));
        Assert.Equal(["universe"], files.RolesOf(SystemWorkspace.UniversePath));
        Assert.Empty(files.RolesOf(@"DATA\solar\first_stars.ini"));
        Assert.Equal(["equipment"], files.RolesOf(@"DATA\EQUIPMENT\weapon_equip.ini"));
    }

    [Fact]
    public void TheLastEntryFallsBackToTheEngineDefault()
    {
        using var ws = SystemWorkspace.Create();
        var files = Files(ws, """
            [Freelancer]
            data path = ..\data

            [Data]
            solar = solar\solararch.ini
            """);

        Assert.Equal([@"DATA\SOLAR\stararch.ini"], files.Paths("stars"), IgnoreCase);
        Assert.Empty(files.Paths("bodyparts"));
        Assert.Empty(files.Paths("universe"));
    }
}
