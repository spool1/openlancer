using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Solar;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

public sealed class ObjectKindsTests
{
    [Theory]
    [InlineData(ArchetypeType.sun, "Sun")]
    [InlineData(ArchetypeType.planet, "Planet")]
    [InlineData(ArchetypeType.station, "Station")]
    [InlineData(ArchetypeType.docking_ring, "Docking ring")]
    [InlineData(ArchetypeType.jump_gate, "Jump gate")]
    [InlineData(ArchetypeType.airlock_gate, "Airlock gate")]
    [InlineData(ArchetypeType.jumphole, "Jump hole")]
    [InlineData(ArchetypeType.jump_hole, "Jump hole")]
    [InlineData(ArchetypeType.tradelane_ring, "Trade lane ring")]
    [InlineData(ArchetypeType.satellite, "Satellite")]
    [InlineData(ArchetypeType.mission_satellite, "Satellite")]
    [InlineData(ArchetypeType.weapons_platform, "Weapons platform")]
    [InlineData(ArchetypeType.destroyable_depot, "Depot")]
    [InlineData(ArchetypeType.non_targetable, "Object")]
    [InlineData(ArchetypeType.waypoint, "Object")]
    [InlineData(ArchetypeType.NONE, "Object")]
    public void MapsEachArchetypeTypeToItsKind(ArchetypeType type, string label)
    {
        var obj = new SystemObject { Nickname = "obj", Archetype = new Archetype { Nickname = "arch", Type = type } };

        Assert.Equal(label, ObjectKinds.Of(obj).Label);
    }

    [Fact]
    public void GivesAnObjectWithoutArchetypeTheOtherKind()
    {
        Assert.Same(ObjectKinds.OtherObject, ObjectKinds.Of(new SystemObject { Nickname = "obj" }));
    }

    [Fact]
    public void UsesTheLabelPrioritiesOfTheGameMapWithAirlockGatesAsJumpGates()
    {
        Assert.Equal(90, ObjectKinds.Planet.LabelPriority);
        Assert.Equal(80, ObjectKinds.Station.LabelPriority);
        Assert.Equal(70, ObjectKinds.JumpGate.LabelPriority);
        Assert.Equal(70, ObjectKinds.AirlockGate.LabelPriority);
        Assert.Equal(60, ObjectKinds.JumpHole.LabelPriority);
        Assert.Equal(50, ObjectKinds.DockingRing.LabelPriority);
        Assert.Equal(40, ObjectKinds.Sun.LabelPriority);
        Assert.Equal(0, ObjectKinds.TradeLaneRing.LabelPriority);
    }

    [Theory]
    [InlineData(typeof(GunEquipment), "Gun")]
    [InlineData(typeof(MunitionEquip), "Munition")]
    [InlineData(typeof(MissileEquip), "Missile")]
    [InlineData(typeof(MissileLauncherEquipment), "Missile launcher")]
    [InlineData(typeof(MineDropperEquipment), "Mine dropper")]
    [InlineData(typeof(ShieldBatteryEquipment), "Shield battery")]
    [InlineData(typeof(TradelaneEquipment), "Trade lane")]
    [InlineData(typeof(EffectEquipment), "Effect")]
    [InlineData(typeof(Equipment), "Equipment")]
    public void MapsEachEquipmentClassToItsKind(Type type, string label)
    {
        // Some equipment classes have required members. The kind depends only on the class.
        var equipment = (Equipment)RuntimeHelpers.GetUninitializedObject(type);

        Assert.Equal(label, ObjectKinds.Of(equipment).Label);
    }

    [Fact]
    public void GivesEveryEquipmentClassItsOwnKind()
    {
        var classes = typeof(Equipment).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Equipment)) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(classes);
        foreach (var type in classes)
        {
            var kind = ObjectKinds.Of((Equipment)RuntimeHelpers.GetUninitializedObject(type));
            Assert.True(kind.Label != "Equipment", $"{type.Name} has no kind of its own.");
        }
    }

    [Fact]
    public void FindsTheKindOfEachSelectableItem()
    {
        Assert.Same(ObjectKinds.Light, ObjectKinds.Of(RuntimeHelpers.GetUninitializedObject(typeof(LightSource))));
        Assert.Same(ObjectKinds.StarSystem,
            ObjectKinds.Of((object)new StarSystem { Nickname = "li01", SourceFile = "" }));
        Assert.Same(ObjectKinds.Ship, ObjectKinds.Of((object)new Ship { Nickname = "ship" }));
        Assert.Same(ObjectKinds.Of(ZoneRole.Other), ObjectKinds.Of((object)new Zone { Nickname = "zone" }));
        Assert.Null(ObjectKinds.Of((object?)null));
        Assert.Null(ObjectKinds.Of((object)"text"));
    }

    /// <summary>
    /// A nebula and a field are scene items with kinds of their own (section 5 of the M22 design), with the marker values
    /// of decision D164. Their zones keep the zone kinds, with the labels "Nebula zone" and "Asteroid field zone".
    /// </summary>
    [Fact]
    public void TheItemKindsAndTheZoneRoleKindsHaveTheirLabels()
    {
        Assert.Same(ObjectKinds.Nebula, ObjectKinds.Of((object)new Nebula { SourceFile = "" }));
        Assert.Same(ObjectKinds.AsteroidField, ObjectKinds.Of((object)new AsteroidField { SourceFile = "" }));
        Assert.Equal(("Nebula", EditorIcons.Cloud, 14f, 30, float.PositiveInfinity),
            (ObjectKinds.Nebula.Label, ObjectKinds.Nebula.Icon, ObjectKinds.Nebula.MarkerSize,
                ObjectKinds.Nebula.LabelPriority, ObjectKinds.Nebula.FadeDistance));
        Assert.Equal(("Asteroid field", EditorIcons.Meteor, 12f, 0, 100_000f),
            (ObjectKinds.AsteroidField.Label, ObjectKinds.AsteroidField.Icon, ObjectKinds.AsteroidField.MarkerSize,
                ObjectKinds.AsteroidField.LabelPriority, ObjectKinds.AsteroidField.FadeDistance));

        var nebulaZone = ObjectKinds.Of(ZoneRole.Nebula);
        var fieldZone = ObjectKinds.Of(ZoneRole.AsteroidField);
        Assert.Equal(("Nebula zone", ObjectKinds.Nebula.Color, 6f), (nebulaZone.Label, nebulaZone.Color, nebulaZone.MarkerSize));
        Assert.Equal(("Asteroid field zone", ObjectKinds.AsteroidField.Color, 6f),
            (fieldZone.Label, fieldZone.Color, fieldZone.MarkerSize));

        // The picker of a file list shows the glyph of the item kind.
        Assert.Same(ObjectKinds.Nebula, ObjectKinds.OfList("Nebula"));
        Assert.Same(ObjectKinds.AsteroidField, ObjectKinds.OfList("AsteroidField"));
        // The list of the animation clips shows the glyph of the clip picker.
        Assert.Same(ObjectKinds.Thn.Clip, ObjectKinds.OfList("Animation"));
        // The pickers of the parts of a base show a room and a character.
        Assert.Equal(("Room", "Character"), (ObjectKinds.OfList("Room").Label, ObjectKinds.OfList("Character").Label));
    }

    [Fact]
    public void PacksTheColorAsImGuiAbgr()
    {
        Assert.Equal(0xFF40D0FFu, ObjectKinds.Sun.Abgr);
        Assert.Equal(0xFFE0A050u, ObjectKinds.Planet.Abgr);
        Assert.Equal(0x80FFFFFFu, EditorTheme.ToAbgr(new Vector4(1, 1, 1, 0.5f)));
    }

    [Fact]
    public void FadesAMarkerBetween60And100PercentOfItsFadeDistance()
    {
        Assert.Equal(1, SceneMarkers.Fade(5_000, 15_000));
        Assert.Equal(1, SceneMarkers.Fade(9_000, 15_000));
        Assert.Equal(0.5f, SceneMarkers.Fade(12_000, 15_000), 0.001f);
        Assert.Equal(0, SceneMarkers.Fade(15_000, 15_000));
        Assert.Equal(0, SceneMarkers.Fade(80_000, 15_000));
        Assert.Equal(1, SceneMarkers.Fade(1e7f, float.PositiveInfinity));
    }

    [Theory]
    [InlineData(0.2f, 0.1f, 0, 0.5f, 0.25f, 0)]
    [InlineData(0.8f, 0.6f, 0.4f, 0.8f, 0.6f, 0.4f)]
    [InlineData(0, 0, 0, 0.5f, 0.5f, 0.5f)]
    [InlineData(2, 1, 0.5f, 1, 1, 0.5f)]
    public void RaisesADarkLightColorToHalfBrightness(float r, float g, float b, float expectedR, float expectedG,
        float expectedB)
    {
        var color = ObjectKinds.LightColor(new LibreLancer.Color3f(r, g, b));

        Assert.Equal(expectedR, color.X, 0.0001f);
        Assert.Equal(expectedG, color.Y, 0.0001f);
        Assert.Equal(expectedB, color.Z, 0.0001f);
        Assert.Equal(1, color.W);
    }

    [Theory]
    [InlineData("Ship", "Ship")]
    [InlineData("good", "Good")]
    [InlineData("BaseGood", "Market")]
    [InlineData("Gun", "Gun")]
    [InlineData("ShieldGenerator", "Shield")]
    [InlineData("Motor", "Equipment")]
    public void ASectionOfAnItemFileGetsTheKindOfItsType(string type, string label)
    {
        Assert.Equal(label, ObjectKinds.OfSection(type).Label);
        var section = new OpenLancer.Data.Editing.SectionRef(@"DATA\EQUIPMENT\goods.ini",
            new OpenLancer.Data.Editing.SectionIdentity(type, "x"));
        Assert.Same(ObjectKinds.OfSection(type), ObjectKinds.Of(section));
    }
}
