using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// A small synthetic install for the asset browser of milestone M23, on top of <see cref="SceneWorkspace"/>: solar
/// archetypes with models, a wreck with a ship model, two ships, an asteroid, a gun with a model, an effect, and model
/// files in four folders below the data folder and one outside it.
/// </summary>
internal static class AssetWorkspace
{
    public const string AsteroidsPath = @"DATA\SOLAR\asteroidarch.ini";
    public const string EffectsPath = @"DATA\FX\effects.ini";
    public const string FighterModel = @"ships\liberty\li_fighter\li_fighter.cmp";
    public const string GunboatModel = @"ships\bretonia\br_gunboat\br_gunboat.cmp";

    public const string Solars = """
        [Solar]
        nickname = station1
        type = STATION
        DA_archetype = solar\dockable\station1.cmp
        material_library = solar\dockable\station1.mat
        docking_sphere = berth, HpDockMountA, 50
        [Solar]
        nickname = station_nodock
        type = STATION
        DA_archetype = solar\dockable\station1.cmp
        material_library = solar\dockable\station1.mat
        [Solar]
        nickname = dock_ring
        type = DOCKING_RING
        docking_sphere = ring, HpDockMountA, 50
        [Solar]
        nickname = jumpgate
        type = JUMP_GATE
        docking_sphere = jump, HpDockMountA, 50
        [Solar]
        nickname = ring
        type = TRADELANE_RING
        [Solar]
        nickname = planet1
        type = PLANET
        DA_archetype = solar\planets\planet1.sph
        [Solar]
        nickname = sun1
        type = SUN
        [Solar]
        nickname = buoy
        type = SATELLITE
        [Solar]
        nickname = depot
        type = DESTROYABLE_DEPOT
        [Solar]
        nickname = wreck_fighter
        type = MISSION_SATELLITE
        DA_archetype = Ships\Liberty\li_fighter\li_fighter.cmp
        material_library = ships\liberty\li_fighter\li_fighter.mat
        LODranges = 0, 1000
        [Solar]
        nickname = odd
        type = NO_SUCH_TYPE
        """;

    public const string Ships = """
        [Ship]
        nickname = ship1
        type = FIGHTER
        DA_archetype = ships/liberty/li_fighter/li_fighter.cmp
        material_library = ships\liberty\li_fighter\li_fighter.mat
        [Ship]
        nickname = br_gunboat
        type = GUNBOAT
        DA_archetype = ships\bretonia\br_gunboat\br_gunboat.cmp
        material_library = ships\bretonia\br_gunboat\br_gunboat.mat
        material_library = ships\bretonia\br_playerships.mat
        envmap_material = envmapbasic
        LODranges = 0, 500, 1000
        hit_pts = 4000
        """;

    public const string Asteroids = """
        [Asteroid]
        nickname = rock1
        DA_archetype = solar\asteroids\rock1.3db
        material_library = solar\asteroids\rocks.mat
        """;

    public const string Equipment = """
        [Munition]
        nickname = ammo1
        [Motor]
        nickname = motor1
        [Explosion]
        nickname = expl1
        [Munition]
        nickname = missile1
        motor = motor1
        explosion_arch = expl1
        [Gun]
        nickname = gun1
        projectile_archetype = ammo1
        DA_archetype = equipment\models\weapons\gun1.cmp
        material_library = equipment\models\weapons.mat
        """;

    /// <summary>The model files: below the data folder, and one outside it, which no list holds.</summary>
    public static readonly string[] ModelFiles =
    [
        @"DATA\SOLAR\DOCKABLE\station1.cmp", @"DATA\SOLAR\PLANETS\planet1.sph", @"DATA\SOLAR\ASTEROIDS\rock1.3db",
        @"DATA\SHIPS\LIBERTY\li_fighter\li_fighter.cmp", @"DATA\SHIPS\BRETONIA\br_gunboat\br_gunboat.cmp",
        @"DATA\EQUIPMENT\MODELS\WEAPONS\gun1.cmp", @"DATA\INTERFACE\orphan.3db", @"DATA\loose.cmp",
    ];

    /// <summary>A workspace with the asset files, the empty mods "lower" and "act", and the profile.</summary>
    public static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null,
        Dictionary<string, string>? activeFiles = null)
    {
        var ws = SceneWorkspace.Create(lowerFiles, activeFiles);
        ws.WriteBase(@"EXE\freelancer.ini", FreelancerIni + "\nasteroids = solar\\asteroidarch.ini\neffects = fx\\effects.ini\n");
        ws.WriteBase(SolarPath, Solars);
        ws.WriteBase(ShipsPath, Ships);
        ws.WriteBase(AsteroidsPath, Asteroids);
        ws.WriteBase(EquipmentPath, Equipment);
        ws.WriteBase(EffectsPath, "[Effect]\nnickname = fx_smoke\neffect_type = EFT_MISC\n");
        foreach (var file in ModelFiles)
            ws.WriteBase(file, "not a real model");
        ws.WriteBase(@"EXE\stray.cmp", "not a real model");
        return ws;
    }

    public static SectionRef Solar(string nickname) => new(SolarPath, new SectionIdentity("Solar", nickname));

    public static SectionRef Ship(string nickname) => new(ShipsPath, new SectionIdentity("Ship", nickname));

    public static SectionRef Asteroid(string nickname) => new(AsteroidsPath, new SectionIdentity("Asteroid", nickname));

    public static SectionRef Gun(string nickname) => new(EquipmentPath, new SectionIdentity("Gun", nickname));

    public static AssetRow Row(EditSession session, string category, string nickname) =>
        session.Assets.Rows(category).Single(r => r.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The rows and categories of the Assets panel (sections 5.1 to 5.4 of the M23 design).</summary>
public sealed class AssetCatalogTests
{
    [Fact]
    public void EachSolarArchetypeIsInTheChildOfItsTemplate()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");

        var solars = session.Assets.Rows("solars").ToDictionary(r => r.Nickname, r => r.CategoryId);
        Assert.Equal("solars/station", solars["station1"]);
        Assert.Equal("solars/station", solars["station_nodock"]);
        Assert.Equal("solars/station", solars["dock_ring"]);
        Assert.Equal("solars/planet", solars["planet1"]);
        Assert.Equal("solars/sun", solars["sun1"]);
        Assert.Equal("solars/satellite", solars["buoy"]);
        Assert.Equal("solars/solar", solars["depot"]);
        Assert.Equal("solars/solar", solars["wreck_fighter"]);
        Assert.Equal("solars/connections", solars["jumpgate"]);
        Assert.Equal("solars/connections", solars["ring"]);
        Assert.Null(AssetWorkspace.Row(session, "solars", "station_nodock").NotPlaceable);
        Assert.Equal("STATION", AssetWorkspace.Row(session, "solars", "station1").Detail);
        Assert.Equal(@"solar\dockable\station1.cmp", AssetWorkspace.Row(session, "solars", "station1").ModelPath);
    }

    /// <summary>A connection type cannot spawn until milestone M27. A type in no template goes to "Other solar" with its
    /// reason (section 5.2).</summary>
    [Fact]
    public void ConnectionsAndTypesInNoTemplateHaveTheirReasons()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal("This version cannot make jump gates, jump holes or trade lane rings.",
            AssetWorkspace.Row(session, "solars/connections", "jumpgate").NotPlaceable);
        var odd = AssetWorkspace.Row(session, "solars", "odd");
        Assert.Equal("solars/solar", odd.CategoryId);
        Assert.Equal("The type no_such_type is in no Create template of the rule data.", odd.NotPlaceable);
        Assert.StartsWith("Equipment goes on an object through its loadout.",
            AssetWorkspace.Row(session, "equipment", "gun1").NotPlaceable);
        Assert.StartsWith("An effect goes on an object", AssetWorkspace.Row(session, "effects", "fx_smoke").NotPlaceable);
        Assert.Null(AssetWorkspace.Row(session, "ships", "ship1").NotPlaceable);
        Assert.Null(AssetWorkspace.Row(session, "asteroids", "rock1").NotPlaceable);
    }

    [Fact]
    public void TheCategoriesHaveTheirChildrenAndCounts()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");

        var categories = session.Assets.Categories;
        Assert.Equal(AssetCatalog.AllId, categories[0].Id);
        Assert.Equal(["groups", "solars", "ships", "asteroids", "equipment", "effects", "loadouts", "factions", "models", "prefabs"],
            categories.Where(c => c.ParentId == AssetCatalog.AllId).Select(c => c.Id));
        Assert.Equal(["solars/station", "solars/planet", "solars/sun", "solars/satellite", "solars/solar",
            "solars/connections"], categories.Where(c => c.ParentId == "solars").Select(c => c.Id));
        Assert.Equal([3, 1, 1, 1, 3, 2], categories.Where(c => c.ParentId == "solars").Select(c => c.Count));
        Assert.Equal(11, categories.Single(c => c.Id == "solars").Count);
        Assert.Equal(["equipment/gun", "equipment/munition"],
            categories.Where(c => c.ParentId == "equipment").Select(c => c.Id));
        // The models are the model files below the data folder, by their top folder. A file of the data folder itself
        // stays in the top category.
        Assert.Equal(["EQUIPMENT", "INTERFACE", "SHIPS", "SOLAR"],
            categories.Where(c => c.ParentId == "models").Select(c => c.Label));
        Assert.Equal(8, categories.Single(c => c.Id == "models").Count);
        Assert.Equal("models", AssetWorkspace.Row(session, "models", "loose").CategoryId);
        Assert.DoesNotContain(session.Assets.Rows("models"), r => r.Nickname == "stray");
        Assert.Equal(categories.Where(c => c.ParentId == AssetCatalog.AllId).Sum(c => c.Count), categories[0].Count);
    }

    /// <summary>Decisions D268 and D245: an unsaved new [Solar] of the active mod shows at once with the "+" mark, a
    /// changed section gets the blue mark, and a section that the active mod deletes does not show.</summary>
    [Fact]
    public void TheRowsFollowTheEditsOfTheActiveModBeforeASave()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.AddSection(SolarPath, "Solar",
            [Line("nickname", "new_rock"), Line("type", "NON_TARGETABLE")]));
        session.Execute(EditCommand.SetLinesFromText(AssetWorkspace.Solar("planet1"), "loadout", ["lo_solar"]));
        session.Execute(EditCommand.DeleteSection(AssetWorkspace.Solar("depot")));

        var added = AssetWorkspace.Row(session, "solars", "new_rock");
        Assert.Equal(AssetMark.Added | AssetMark.Unsaved, added.Mark);
        Assert.Equal(ValueOrigin.ActiveMod, added.Origin);
        Assert.Equal("solars/solar", added.CategoryId);
        Assert.Equal(AssetMark.Changed, AssetWorkspace.Row(session, "solars", "planet1").Mark);
        Assert.Equal(AssetMark.None, AssetWorkspace.Row(session, "solars", "station1").Mark);
        Assert.DoesNotContain(session.Assets.Rows("solars"), r => r.Nickname == "depot");

        Assert.True(session.Save().Saved);
        Assert.Equal(AssetMark.Added, AssetWorkspace.Row(session, "solars", "new_rock").Mark);
    }

    /// <summary>Section 5.4: the search finds a display name, a nickname, a type and a model path, without letter case.
    /// "All assets" searches each category.</summary>
    [Fact]
    public void TheSearchFindsNamesNicknamesTypesAndModels()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");
        string Name(AssetRow row) => row.Nickname == "station1" ? "Freeport Seven" : "";

        Assert.Equal(["station1"], session.Assets.Search("solars", "FREEPORT", Name).Select(r => r.Nickname));
        Assert.Equal(["station_nodock"], session.Assets.Search("solars", "NODOCK", Name).Select(r => r.Nickname));
        Assert.Equal(["planet1"], session.Assets.Search("solars", "planet", Name).Select(r => r.Nickname));
        Assert.Equal(["wreck_fighter"], session.Assets.Search("solars", "LI_FIGHTER", Name).Select(r => r.Nickname));
        // All assets: the wreck, the ship of the same model and the model file.
        Assert.Equal(["wreck_fighter", "ship1", "li_fighter"],
            session.Assets.Search(AssetCatalog.AllId, "li_fighter", Name).Select(r => r.Nickname));
        Assert.Equal(session.Assets.Rows("solars/station"), session.Assets.Search("solars/station", "  ", Name));
    }

    /// <summary>Section 5.3: the rows of a category build again only when a list that it reads changes, or when the prop
    /// groups change. An edit of a system file changes no row.</summary>
    [Fact]
    public void TheRowsBuildAgainOnlyWhenTheirListsChange()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");
        var solars = session.Assets.Rows("solars");
        var groups = session.Assets.Rows("groups");

        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_01"), "pos", ["1, 2, 3"]));
        Assert.Same(solars, session.Assets.Rows("solars"));
        Assert.Same(groups, session.Assets.Rows("groups"));

        session.Execute(EditCommand.SetLinesFromText(AssetWorkspace.Solar("planet1"), "loadout", ["lo_solar"]));
        Assert.NotSame(solars, session.Assets.Rows("solars"));

        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_dock")]);
        PropGroups.Write(session, draft, "dock_group", "Dock");
        Assert.NotSame(groups, session.Assets.Rows("groups"));
        Assert.Equal("dock_group", Assert.Single(session.Assets.Rows("groups")).Nickname);
    }

    /// <summary>Section 6.2: a ship, an asteroid or a model gets the "prop" badge when W has a solar archetype with its
    /// model. A model that no solar archetype, ship or asteroid names does not spawn (decision D280).</summary>
    [Fact]
    public void TheModelsAndTheirPropBadges()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");

        Assert.True(AssetWorkspace.Row(session, "ships", "ship1").HasProp);
        Assert.False(AssetWorkspace.Row(session, "ships", "br_gunboat").HasProp);
        Assert.False(AssetWorkspace.Row(session, "asteroids", "rock1").HasProp);
        Assert.True(AssetWorkspace.Row(session, "models", "li_fighter").HasProp);
        Assert.Null(AssetWorkspace.Row(session, "models", "br_gunboat").NotPlaceable);
        Assert.Null(AssetWorkspace.Row(session, "models", "rock1").NotPlaceable);
        Assert.StartsWith("Retail has no solar with an equipment model.",
            AssetWorkspace.Row(session, "models", "gun1").NotPlaceable);
        Assert.StartsWith("No solar archetype, ship or asteroid uses this model",
            AssetWorkspace.Row(session, "models", "orphan").NotPlaceable);
        Assert.Equal(@"SHIPS\BRETONIA\br_gunboat", AssetWorkspace.Row(session, "models", "br_gunboat").Detail);
        Assert.Equal(AssetKind.SolarArchetype, session.Assets.KindOfList("solar_archetype"));
        Assert.Equal("ship1", session.Assets.Find(AssetKind.Ship, "SHIP1")!.Nickname);
    }
}
