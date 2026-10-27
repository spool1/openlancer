using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The bound rows of the Inspector without the GPU: the state of a key (its lines, the layer that sets them, the later
/// mods, the lock), the edits of a row, and one field edit from its start to its end.
/// </summary>
public sealed class FieldBindingTests
{
    /// <summary>
    /// The item workspace with three mods: "low" loads before the active mod "act" and sets the price of the hull
    /// good, "act" sets the mass of the ship, and "late" loads after it and changes the mass too.
    /// </summary>
    internal static TestWorkspace Layers()
    {
        var ws = Create();
        ws.Mod("low", new()
        {
            [@"patches\" + GoodsPath] = """
                [Good]
                nickname = s1_hull
                price = 900
                """,
        });
        ws.Mod("act", new()
        {
            [@"patches\" + ShipsPath] = """
                [Ship]
                nickname = s1
                mass = 120
                """,
        });
        ws.Mod("late", new()
        {
            [@"patches\" + ShipsPath] = """
                [Ship]
                nickname = s1
                mass = 140
                """,
        });
        ws.WriteProfile("low", "act", "late");
        return ws;
    }

    internal static FieldBinding Bind(EditSession session, SectionRef section, string name) =>
        FieldBinding.Create(session, section, session.Get(section)!, name);

    [Fact]
    public void ABoundKeyShowsItsLinesAndTheLayerThatSetsThem()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var mass = Bind(session, Ship("s1"), "mass");
        Assert.Equal("120", mass.Text);
        Assert.Equal(ValueOrigin.ActiveMod, mass.Origin);
        Assert.Equal("act", mass.OriginMod);
        Assert.Equal(["late"], mass.LaterMods);
        Assert.Equal(typeof(float), mass.Key!.ClrType);
        // The lower layer has the ship, so a revert can put back its value.
        Assert.True(mass.CanRevert);
        Assert.Null(mass.ReadOnlyReason);

        var price = Bind(session, Good(GoodsPath, "s1_hull"), "price");
        Assert.Equal((ValueOrigin.LowerMod, "low"), (price.Origin, price.OriginMod));
        Assert.False(price.CanRevert);
        Assert.Contains("'low'", price.OriginText);

        var ids = Bind(session, Ship("s1"), "ids_name");
        Assert.Equal(ValueOrigin.Base, ids.Origin);
        Assert.Empty(ids.LaterMods);

        // A key of the schema that the section does not set has no lines.
        var hold = Bind(session, Ship("s1"), "hold_size");
        Assert.Empty(hold.Lines);
        Assert.Contains("default", hold.OriginText);

        Assert.NotNull(Bind(session, Ship("s1"), "nickname").ReadOnlyReason);
    }

    [Fact]
    public void AnEditOfTheFieldSetsTheKeyAndARevertPutsBackTheLowerValue()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        session.Execute(FieldCommands.SetLinesFromText(session, Good(GoodsPath, "s1_hull"), "price", " 1200 ").Command!);

        var price = Bind(session, Good(GoodsPath, "s1_hull"), "price");
        Assert.Equal("1200", price.Text);
        Assert.Equal(ValueOrigin.ActiveMod, price.Origin);
        Assert.True(price.CanRevert);
        session.Execute(FieldCommands.RevertKey(session, price.Section, price.Name).Command!);
        Assert.Equal(("900", ValueOrigin.LowerMod), (Bind(session, Good(GoodsPath, "s1_hull"), "price").Text,
            Bind(session, Good(GoodsPath, "s1_hull"), "price").Origin));
    }

    [Fact]
    public void AKeyOfASectionThatTheActiveModAddsHasNoLowerValue()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));

        var link = Bind(session, Gun("gun2"), "projectile_archetype");

        Assert.Equal(ValueOrigin.ActiveMod, link.Origin);
        Assert.False(link.CanRevert);
        Assert.True(session.Get(Gun("gun2"))!.IsNew);
    }

    [Fact]
    public void AnEmptyFieldRemovesTheKey()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        session.Execute(FieldCommands.SetLinesFromText(session, Ship("s1"), "mass", "").Command!);

        Assert.Empty(session.Get(Ship("s1"))!.Lines("mass"));
        Assert.Equal("Remove mass", session.UndoLabel);
    }

    [Fact]
    public void AFieldEditRefusesTextThatTheSchemaDoesNotAllow()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var plan = FieldCommands.SetLinesFromText(session, Ship("s1"), "mass", "heavy");

        Assert.False(plan.CanRun);
        Assert.Contains("is not a number", Assert.Single(plan.Refusals).Message);
    }

    [Fact]
    public void AGestureShowsEachChangeAndBecomesOneEntryOfTheHistory()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        var changes = 0;
        session.Changed += _ => changes++;

        using (var gesture = TestGestures.Line(session, Bind(session, Ship("s1"), "mass")))
        {
            gesture.Change("1");
            Assert.Equal("1", Bind(session, Ship("s1"), "mass").Text);
            gesture.Change("15");
            gesture.Change("150");
            Assert.Null(gesture.End(keep: true, interactive: true));
        }

        Assert.Equal("150", Bind(session, Ship("s1"), "mass").Text);
        Assert.Equal("Set mass", session.UndoLabel);
        Assert.True(changes >= 4);
        session.Undo();
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AGestureThatEndsWithTheStartTextChangesNothing()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = TestGestures.Line(session, Bind(session, Ship("s1"), "mass"));
        gesture.Change("200");
        gesture.Change("120");
        gesture.End(keep: true, interactive: true);

        Assert.False(session.CanUndo);
        // The pin of the patch line stays, so the mod keeps its value when a lower mod changes.
        Assert.Equal(ValueOrigin.ActiveMod, Bind(session, Ship("s1"), "mass").Origin);
        var result = session.Save();
        Assert.True(result.Saved);
        Assert.Empty(result.Written);
    }

    [Fact]
    public void AGestureWithTextThatIsNotValidEndsWithItsErrorAndChangesNothing()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = TestGestures.Line(session, Bind(session, Ship("s1"), "mass"));
        gesture.Change("130");
        gesture.Change("13x");
        Assert.Contains("is not a number", gesture.Error);
        // The last valid text shows until the end.
        Assert.Equal("130", Bind(session, Ship("s1"), "mass").Text);

        Assert.Contains("is not a number", gesture.End(keep: true, interactive: true));
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AGestureThatTheUserCancelsPutsBackTheStart()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = TestGestures.Line(session, Bind(session, Ship("s1"), "mass"));
        gesture.Change("130");

        Assert.Null(gesture.End(keep: false, interactive: true));
        Assert.Equal("120", Bind(session, Ship("s1"), "mass").Text);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void AGestureOnALineThatAPatchCannotHoldKeepsTheErrorOfTheCommand()
    {
        using var ws = Layers();
        var session = ws.Session("act");

        var gesture = TestGestures.Line(session, Bind(session, Ship("s1"), "da_archetype"));
        gesture.Change(@"ships\s1;old.cmp");

        Assert.Contains("';'", gesture.Error);
        Assert.NotNull(gesture.End(keep: true, interactive: true));
        Assert.Equal([@"ships\s1.cmp"], Firsts(session, Ship("s1"), "da_archetype"));
    }

    [Fact]
    public void ARowOfAReadOnlySessionHasTheReasonOfTheSession()
    {
        using var ws = Layers();
        var session = EditSession.Open(Data.Mods.ModProfile.Load(ws.ProfilePath), null, TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator);

        var mass = Bind(session, Ship("s1"), "mass");

        Assert.Equal(session.ReadOnlyReason, mass.ReadOnlyReason);
        Assert.NotNull(mass.ReadOnlyReason);
        Assert.False(mass.CanRevert);
        Assert.Throws<InvalidOperationException>(() => TestGestures.Line(session, mass));
    }

    [Fact]
    public void TheEditOfAFieldSavesAsAPatchLineOfTheActiveMod()
    {
        using var ws = Layers();
        var session = ws.Session("act");
        session.Execute(FieldCommands.SetLinesFromText(session, Good(GoodsPath, "s1_hull"), "price", "1200").Command!);

        Assert.True(session.Save().Saved);

        var patch = File.ReadAllText(Path.Combine(ws.ModFolder("act"), "patches", GoodsPath));
        Assert.Contains("nickname = s1_hull", patch);
        Assert.Contains("price = 1200", patch);
        Assert.False(File.Exists(Path.Combine(ws.ModFolder("low"), "patches", ShipsPath)));
    }

    /// <summary>
    /// A star system file with a section of each scene section type, a zone with encounter lines, and two lights with
    /// one nickname (section 12 of the M2 design). A nebula and an asteroid field share the zone of the cloud (section 12
    /// of the M22 design).
    /// </summary>
    internal const string SceneLi01 = """
        [SystemInfo]
        space_color = 0, 0, 0
        local_faction = li_n_grp

        [Archetype]
        ship = li_elite
        solar = depot

        [EncounterParameters]
        nickname = area_scout
        filename = missions\encounters\area_scout.ini

        [TexturePanels]
        file = universe\heavens\shapes.ini

        [Music]
        space = music_li_space

        [Dust]
        spacedust = Dust

        [Ambient]
        color = 20, 20, 30

        [Background]
        basic_stars = solar\starsphere\starsphere_stars_basic.cmp

        [Object]
        nickname = Li01_depot
        pos = -38675, 0, -27705
        archetype = depot
        faction = li_n_grp

        [Zone]
        nickname = Zone_pop
        pos = 0, 0, 0
        shape = SPHERE
        size = 4100
        encounter = area_scout, 5, 0.4
        faction = li_n_grp, 1.0

        [LightSource]
        nickname = Light_main
        pos = 1, 2, 3

        [LightSource]
        nickname = light_twice
        pos = 0, 0, 0

        [LightSource]
        nickname = light_twice
        pos = 0, 0, 0

        [Zone]
        nickname = Zone_cloud
        pos = 1000, 0, 0
        shape = ELLIPSOID
        size = 3000, 1000, 2000

        [Nebula]
        file = solar\nebula\cloud.ini
        zone = Zone_cloud

        [Asteroids]
        file = solar\asteroids\cloud_rocks.ini
        zone = Zone_cloud
        """;

    /// <summary>The section of each scene section type in <see cref="SceneLi01"/> and universe.ini.</summary>
    internal static readonly Dictionary<SceneSection, SectionRef> SceneSections = new()
    {
        [SceneSection.Object] = SystemWorkspace.Li01Section("Object", "Li01_depot"),
        [SceneSection.Zone] = SystemWorkspace.Li01Section("Zone", "Zone_pop"),
        [SceneSection.LightSource] = SystemWorkspace.Li01Section("LightSource", "Light_main"),
        [SceneSection.SystemInfo] = SystemWorkspace.Li01Section("SystemInfo", null),
        [SceneSection.Ambient] = SystemWorkspace.Li01Section("Ambient", null),
        [SceneSection.Background] = SystemWorkspace.Li01Section("Background", null),
        [SceneSection.Dust] = SystemWorkspace.Li01Section("Dust", null),
        [SceneSection.Music] = SystemWorkspace.Li01Section("Music", null),
        [SceneSection.TexturePanels] = SystemWorkspace.Li01Section("TexturePanels", null),
        [SceneSection.Preloads] = SystemWorkspace.Li01Section("Archetype", null),
        [SceneSection.EncounterParameters] = SystemWorkspace.Li01Section("EncounterParameters", "area_scout"),
        [SceneSection.UniverseSystem] = SystemWorkspace.System("Li01"),
        [SceneSection.Nebula] = SystemWorkspace.Li01Section("Nebula", "Zone_cloud"),
        [SceneSection.Asteroids] = SystemWorkspace.Li01Section("Asteroids", "Zone_cloud"),
    };

    /// <summary>
    /// The rows of the scene sections lock exactly the keys that the table of live actions locks: the key entries, the
    /// linked group of a zone, the file of a system and the preloads. So the Inspector and the table cannot disagree.
    /// </summary>
    [Fact]
    public void ARowOfASceneSectionIsReadOnlyExactlyForTheLockedKeysOfTheTable()
    {
        using var ws = SystemWorkspace.Create(SceneLi01);
        var session = ws.Session("act");

        Assert.Equal(Enum.GetValues<SceneSection>().Order(), SceneSections.Keys.Order());
        foreach (var row in SceneFields.Rows)
        {
            var field = Bind(session, SceneSections[row.Section], row.Key);
            Assert.True(row.Locked == (field.ReadOnlyReason != null),
                $"{row.Section} {row.Key}: locked {row.Locked}, reason '{field.ReadOnlyReason}'");
        }
    }

    [Fact]
    public void TheLocksOfTheSceneSectionsGiveTheirReasons()
    {
        using var ws = SystemWorkspace.Create(SceneLi01);
        var session = ws.Session("act");
        const string group = "This key belongs to a group of lines that belong together. A later milestone edits the group.";

        foreach (var key in new[] { "encounter", "faction", "faction_weight" })
            Assert.Equal(group, Bind(session, SceneSections[SceneSection.Zone], key).ReadOnlyReason);
        Assert.Equal("Changing the file of a system moves all its contents. A later milestone does this.",
            Bind(session, SystemWorkspace.System("Li01"), "file").ReadOnlyReason);
        Assert.Equal("'nickname' is the key that finds the section. The editor cannot change it.",
            Bind(session, SceneSections[SceneSection.Object], "nickname").ReadOnlyReason);
        // A patch finds neither a section without a key entry nor a section whose key another section has too.
        Assert.Equal("it has no single 'nickname' entry with one value.",
            Bind(session, SceneSections[SceneSection.Preloads], "ship").ReadOnlyReason);
        Assert.Equal("another [LightSource] section also has nickname = light_twice.",
            Bind(session, SystemWorkspace.Li01Section("LightSource", "light_twice"), "pos").ReadOnlyReason);

        // The same names in other places take edits: faction of an object is no group, and the file of the texture
        // panels and of a [Base] of universe.ini is no system file.
        Assert.Null(Bind(session, SceneSections[SceneSection.Object], "faction").ReadOnlyReason);
        Assert.Null(Bind(session, SceneSections[SceneSection.TexturePanels], "file").ReadOnlyReason);
        var baseSection = new SectionRef(SystemWorkspace.UniversePath, new SectionIdentity("Base", "Li01_01_Base"));
        Assert.NotNull(session.Schema(baseSection, "file"));
        Assert.Null(Bind(session, baseSection, "file").ReadOnlyReason);
        Assert.Null(Bind(session, SceneSections[SceneSection.Zone], "pos").ReadOnlyReason);
        Assert.Null(Bind(session, SystemWorkspace.System("Li01"), "strid_name").ReadOnlyReason);
    }

    /// <summary>
    /// A revert of a whole section puts back each line of the lower layer. So it cannot run for a [system] whose file
    /// the active mod sets, for example with a patch that the user wrote: that would move the system to another file,
    /// which the lock of the row prevents (section 12 of the M2 design).
    /// </summary>
    [Fact]
    public void ARevertOfASystemCannotPutBackTheFileOfTheSystem()
    {
        using var ws = SystemWorkspace.Create(SceneLi01);
        const string patch = @"patches\" + SystemWorkspace.UniversePath;
        ws.Mod("low", new()
        {
            [patch] = """
                [system]
                nickname = st01
                file = Systems\St01\low.ini
                """,
        });
        ws.Mod("act", new()
        {
            [patch] = """
                [system]
                nickname = Li01
                file = Systems\Li01\moved.ini

                [system]
                nickname = st01
                strid_name = 5
                """,
        });
        ws.WriteProfile("low", "act");
        var session = ws.Session("act");
        var li01 = SystemWorkspace.System("Li01");
        var st01 = SystemWorkspace.System("st01");

        Assert.True(session.Get(li01)!.IsChanged);
        Assert.Equal("Changing the file of a system moves all its contents. A later milestone does this.",
            FieldBinding.RevertLock(session, li01));
        Assert.False(Bind(session, li01, "file").CanRevert);

        // The active mod sets only the name of St01, so a revert keeps the file of the lower mod.
        Assert.True(session.Get(st01)!.IsChanged);
        Assert.Equal(ValueOrigin.LowerMod, session.Origin(st01, "file").Origin);
        Assert.Null(FieldBinding.RevertLock(session, st01));

        // Other sections have no such key.
        Assert.Null(FieldBinding.RevertLock(session, SceneSections[SceneSection.TexturePanels]));
        Assert.Null(FieldBinding.RevertLock(session, SystemWorkspace.System("nothing")));
    }

    [Fact]
    public void ARowOfASceneSectionHasTheReasonOfTheSessionAndOfAWholeFile()
    {
        using var ws = SystemWorkspace.Create(SceneLi01);
        // The active mod replaces the whole file of the system, so a patch cannot change it.
        ws.Mod("whole", new() { [@"files\" + SystemWorkspace.Li01Path] = SceneLi01 });
        ws.WriteProfile("act", "whole");
        var whole = ws.Session("whole");
        Assert.NotNull(Bind(whole, SceneSections[SceneSection.Zone], "pos").ReadOnlyReason);
        Assert.Equal(whole.Get(SceneSections[SceneSection.Zone])!.ReadOnlyReason,
            Bind(whole, SceneSections[SceneSection.Zone], "pos").ReadOnlyReason);

        // A second editor on the active mod opens it read-only.
        ws.Session("act");
        var second = ws.SecondSession("act");
        var pos = Bind(second, SceneSections[SceneSection.Object], "pos");
        Assert.Equal(second.ReadOnlyReason, pos.ReadOnlyReason);
        Assert.StartsWith("Another editor has mod 'act' open", pos.ReadOnlyReason);
    }
}
