using System;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Solar;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;

namespace OpenLancer.Editor.Ui;

/// <summary>How the editor shows one kind of item: its name, glyph and color, and its marker in the Scene.</summary>
/// <param name="Label">The name of the kind, for example "Station".</param>
/// <param name="Icon">The glyph from <see cref="EditorIcons"/>.</param>
/// <param name="Color">The color of the glyph and of a zone volume.</param>
/// <param name="MarkerSize">The size of the Scene marker, in unscaled points.</param>
/// <param name="LabelPriority">
/// The order of the Scene labels. A higher priority gets its label first. A kind with 0 shows a label only when the
/// object is selected or under the mouse.
/// </param>
/// <param name="FadeDistance">
/// The camera distance where the Scene marker is gone. It starts to fade at 60% of this distance.
/// </param>
internal sealed record ObjectKind(string Label, char Icon, Vector4 Color, float MarkerSize = 10, int LabelPriority = 0,
    float FadeDistance = float.PositiveInfinity)
{
    /// <summary>The color as an ImGui ABGR value for a draw list.</summary>
    public uint Abgr => EditorTheme.ToAbgr(Color);
}

/// <summary>The role of a zone in its system. The first role that applies wins, in the order of this list.</summary>
internal enum ZoneRole
{
    AsteroidField,
    Nebula,
    Exclusion,
    Population,
    PatrolPath,
    TradeLane,
    Other,
}

/// <summary>
/// The kind table. The Hierarchy, the Scene, the Inspector, the Database and the status bar take the glyph and the
/// color of an item only from here, so each kind looks the same everywhere.
/// </summary>
internal static class ObjectKinds
{
    // The label priorities are the values of the game map (Navmap.Rendering.cs, LabelPriority). There is one
    // deliberate change: the game map gives airlock gates 0. Here they get 70, as jump gates do, because an airlock
    // gate is an entrance to the system as a jump gate is.
    public static readonly ObjectKind Sun = new("Sun", EditorIcons.Sun, EditorTheme.Rgb(0xFFD040), 18, 40);
    public static readonly ObjectKind Planet = new("Planet", EditorIcons.Globe, EditorTheme.Rgb(0x50A0E0), 16, 90);
    public static readonly ObjectKind Station = new("Station", EditorIcons.Building, EditorTheme.Rgb(0x60D060), 14, 80);
    public static readonly ObjectKind DockingRing =
        new("Docking ring", EditorIcons.LifeRing, EditorTheme.Rgb(0x60D060), 12, 50);
    public static readonly ObjectKind JumpGate =
        new("Jump gate", EditorIcons.Dungeon, EditorTheme.Rgb(0xC070E0), 14, 70);
    public static readonly ObjectKind AirlockGate =
        new("Airlock gate", EditorIcons.DoorOpen, EditorTheme.Rgb(0xC070E0), 14, 70);
    public static readonly ObjectKind JumpHole =
        new("Jump hole", EditorIcons.CircleNotch, EditorTheme.Rgb(0xC070E0), 14, 60);
    public static readonly ObjectKind TradeLaneRing =
        new("Trade lane ring", EditorIcons.DotCircle, EditorTheme.Rgb(0x8A9BB8), 8, 0, 15_000);
    public static readonly ObjectKind Satellite =
        new("Satellite", EditorIcons.Satellite, EditorTheme.Rgb(0xA8A8A8), 11, 0, 30_000);
    public static readonly ObjectKind WeaponsPlatform =
        new("Weapons platform", EditorIcons.Crosshairs, EditorTheme.Rgb(0xE0805A), 12, 0, 30_000);
    public static readonly ObjectKind Depot = new("Depot", EditorIcons.Box, EditorTheme.Rgb(0xA8A8A8), 11, 0, 30_000);
    public static readonly ObjectKind OtherObject =
        new("Object", EditorIcons.CubeSolid, EditorTheme.Rgb(0x909090), 10, 0, 30_000);

    /// <summary>
    /// A nebula: its <c>[Nebula]</c> section, as a scene item (decision D151 of the M22 design). Its glyph is at the center
    /// of its zone, and it never fades, because a nebula is a large landmark (decision D164).
    /// </summary>
    public static readonly ObjectKind Nebula = new("Nebula", EditorIcons.Cloud, EditorTheme.Rgb(0xB080E0), 14, 30);

    /// <summary>
    /// An asteroid field: its <c>[Asteroids]</c> section, as a scene item. One system can have many fields (Li01 has 9).
    /// So a field shows its label only on hover and selection, and its glyph fades out at 100,000 (decision D164).
    /// </summary>
    public static readonly ObjectKind AsteroidField =
        new("Asteroid field", EditorIcons.Meteor, EditorTheme.Rgb(0xE0905A), 12, 0, 100_000);

    /// <summary>A trade lane: a chain of trade lane rings. It has the color of the rings.</summary>
    public static readonly ObjectKind TradeLane = new("Trade lane", EditorIcons.Stream, TradeLaneRing.Color);

    /// <summary>A light source. The Scene colors each light glyph with the color of its light.</summary>
    public static readonly ObjectKind Light = new("Light", EditorIcons.Lightbulb, EditorTheme.Text, 12);

    public static readonly ObjectKind StarSystem = new("Star system", EditorIcons.Map, EditorTheme.Text);
    public static readonly ObjectKind Ship = new("Ship", EditorIcons.SpaceShuttle, EditorTheme.Text);

    /// <summary>A <c>[Good]</c> of the goods files: what a base sells.</summary>
    public static readonly ObjectKind Good = new("Good", EditorIcons.Tag, EditorTheme.Text);

    /// <summary>A <c>[BaseGood]</c> of the market files: the goods that one base sells.</summary>
    public static readonly ObjectKind Market = new("Market", EditorIcons.Store, EditorTheme.Text);

    /// <summary>
    /// Equipment of any type, for example the Database category with all the equipment. It is also the kind of an
    /// equipment class that has no kind of its own.
    /// </summary>
    public static readonly ObjectKind AnyEquipment = new("Equipment", EditorIcons.Cog, EditorTheme.Text);

    /// <summary>
    /// The kind of an item of a type that the table does not know, for example in the Inspector header. All the
    /// selectable types have a kind, so this is only a fallback.
    /// </summary>
    public static readonly ObjectKind Unknown = new("Item", EditorIcons.QuestionCircle, EditorTheme.Text);

    // A light glyph is at least this bright, so that a dark light stays visible.
    private const float MinLightBrightness = 0.5f;

    /// <summary>
    /// The color of the Scene glyph of a light: the light color, made brighter when its brightest channel is below
    /// 0.5. A black light gets grey.
    /// </summary>
    public static Vector4 LightColor(Color3f color)
    {
        var brightest = MathF.Max(color.R, MathF.Max(color.G, color.B));
        if (brightest <= 0)
        {
            return new Vector4(MinLightBrightness, MinLightBrightness, MinLightBrightness, 1);
        }

        var gain = brightest < MinLightBrightness ? MinLightBrightness / brightest : 1;
        return new Vector4(MathF.Min(color.R * gain, 1), MathF.Min(color.G * gain, 1), MathF.Min(color.B * gain, 1), 1);
    }

    /// <summary>The kind of a system object, from the type of its archetype.</summary>
    public static ObjectKind Of(SystemObject obj) => Of(obj.Archetype?.Type);

    /// <summary>The kind for an archetype type. A null type gives the kind for other objects.</summary>
    public static ObjectKind Of(ArchetypeType? type) => type switch
    {
        ArchetypeType.sun => Sun,
        ArchetypeType.planet => Planet,
        ArchetypeType.station => Station,
        ArchetypeType.docking_ring => DockingRing,
        ArchetypeType.jump_gate => JumpGate,
        ArchetypeType.airlock_gate => AirlockGate,
        ArchetypeType.jumphole or ArchetypeType.jump_hole => JumpHole,
        ArchetypeType.tradelane_ring => TradeLaneRing,
        ArchetypeType.satellite or ArchetypeType.mission_satellite => Satellite,
        ArchetypeType.weapons_platform => WeaponsPlatform,
        ArchetypeType.destroyable_depot => Depot,
        _ => OtherObject,
    };

    /// <summary>The kind for a zone role. Zone markers are 6 point diamonds, not glyphs.</summary>
    public static ObjectKind Of(ZoneRole role) => role switch
    {
        ZoneRole.AsteroidField => Zones.AsteroidField,
        ZoneRole.Nebula => Zones.Nebula,
        ZoneRole.Exclusion => Zones.Exclusion,
        ZoneRole.Population => Zones.Population,
        ZoneRole.PatrolPath => Zones.PatrolPath,
        ZoneRole.TradeLane => Zones.TradeLane,
        _ => Zones.Other,
    };

    /// <summary>
    /// The kind of an equipment item, from its runtime class. The class names do not follow one pattern (for example
    /// GunEquipment, MunitionEquip and MissileEquip), so this is a type switch and not a name lookup.
    /// </summary>
    public static ObjectKind Of(Equipment equipment) => equipment switch
    {
        GunEquipment => Equip.Gun,
        MissileLauncherEquipment => Equip.MissileLauncher,
        MissileEquip => Equip.Missile,
        MunitionEquip => Equip.Munition,
        MineDropperEquipment => Equip.MineDropper,
        ShieldEquipment => Equip.Shield,
        EngineEquipment => Equip.Engine,
        PowerEquipment => Equip.Power,
        ThrusterEquipment => Equip.Thruster,
        CountermeasureEquipment => Equip.Countermeasure,
        CommodityEquipment => Equip.Commodity,
        LightEquipment => Equip.Light,
        CloakEquipment => Equip.Cloak,
        TradelaneEquipment => Equip.Tradelane,
        ScannerEquipment => Equip.Scanner,
        TractorEquipment => Equip.Tractor,
        RepairKitEquipment => Equip.RepairKit,
        ShieldBatteryEquipment => Equip.ShieldBattery,
        CargoPodEquipment => Equip.CargoPod,
        LootCrateEquipment => Equip.LootCrate,
        InternalFxEquipment => Equip.Animation,
        EffectEquipment => Equip.Effect,
        _ => AnyEquipment,
    };

    /// <summary>
    /// The kind of any item that the editor can select, or null for an item of an unknown type. A zone gets the kind
    /// of the Other role here, because its role depends on its system. <see cref="Scene.SystemIndex.KindOf"/> gives a
    /// zone the kind of its role.
    /// </summary>
    public static ObjectKind? Of(object? item) => item switch
    {
        SystemObject obj => Of(obj),
        Zone => Zones.Other,
        LightSource => Light,
        LibreLancer.Data.GameData.World.Nebula => Nebula,
        LibreLancer.Data.GameData.World.AsteroidField => AsteroidField,
        LibreLancer.Data.GameData.World.StarSystem => StarSystem,
        LibreLancer.Data.GameData.Ship => Ship,
        Equipment equipment => Of(equipment),
        SectionRef section => OfSection(section.Id.Type),
        AssetRef asset => Assets.Of(asset.Kind),
        _ => null,
    };

    /// <summary>
    /// The kind of a section of an item file, from its type. A type of equipment that has no kind of its own gets the
    /// kind of all equipment.
    /// </summary>
    public static ObjectKind OfSection(string type) => type.ToLowerInvariant() switch
    {
        "ship" => Ship,
        "good" => Good,
        "basegood" => Market,
        "gun" => Equip.Gun,
        "munition" => Equip.Munition,
        "minedropper" => Equip.MineDropper,
        "mine" => Equip.Munition,
        "shieldgenerator" => Equip.Shield,
        "engine" => Equip.Engine,
        "power" => Equip.Power,
        "thruster" => Equip.Thruster,
        "countermeasuredropper" or "countermeasure" => Equip.Countermeasure,
        "commodity" => Equip.Commodity,
        "light" => Equip.Light,
        "cloakingdevice" => Equip.Cloak,
        "tradelane" => Equip.Tradelane,
        "scanner" => Equip.Scanner,
        "tractor" => Equip.Tractor,
        "repairkit" => Equip.RepairKit,
        "shieldbattery" => Equip.ShieldBattery,
        "cargopod" => Equip.CargoPod,
        "lootcrate" => Equip.LootCrate,
        // The sections that the Assets panel selects (milestone M23).
        "solar" => Lists.SolarArchetype,
        "asteroid" => Assets.Asteroid,
        "loadout" => Lists.Loadout,
        "effect" => Equip.Effect,
        _ => AnyEquipment,
    };

    /// <summary>
    /// The kind of the entries of a data list, from the <c>kind</c> of the list in the rule data (milestone M21). An
    /// unknown kind gets the kind of other objects.
    /// </summary>
    public static ObjectKind OfList(string kind) => kind.ToLowerInvariant() switch
    {
        "solararchetype" => Lists.SolarArchetype,
        "ship" => Ship,
        "loadout" => Lists.Loadout,
        "faction" => Lists.Faction,
        "system" => StarSystem,
        "base" => Lists.Base,
        "object" => OtherObject,
        "zone" => Zones.Other,
        "encounter" => Zones.Population,
        "star" => Sun,
        "starpart" => Lists.StarPart,
        "pilot" => Lists.Pilot,
        "voice" or "sound" => Lists.Sound,
        "effect" => Equip.Effect,
        "explosion" => Lists.Explosion,
        "equipment" => AnyEquipment,
        "good" => Good,
        "bodypart" => Lists.BodyPart,
        "curve" => Lists.Curve,
        "fuse" => Lists.Fuse,
        "enum" => Lists.Enum,
        "cutscene" => Thn.Script,
        "nebula" => Nebula,
        "asteroidfield" => AsteroidField,
        "asteroid" => Assets.Asteroid,
        "model" => Assets.Model,
        _ => OtherObject,
    };

    /// <summary>The kinds of the assets of the Assets panel that no other kind fits (milestone M23).</summary>
    public static class Assets
    {
        public static readonly ObjectKind PropGroup = new("Prop group", EditorIcons.Cubes, EditorTheme.Rgb(0x60C0A0));
        public static readonly ObjectKind Model = new("Model", EditorIcons.CubeSolid, EditorTheme.Text);
        public static readonly ObjectKind Asteroid = new("Asteroid", EditorIcons.Meteor, EditorTheme.Rgb(0xE0905A));

        /// <summary>A mission prefab asset (section 8.3 of the M6b design). It has the text color, as the rows of a
        /// mission in the Hierarchy have.</summary>
        public static readonly ObjectKind MissionPrefab = new("Mission prefab", EditorIcons.Cubes, EditorTheme.Text);

        /// <summary>The kind of an asset. A list entry gets the kind of its list.</summary>
        public static ObjectKind Of(AssetKind kind) => kind switch
        {
            AssetKind.PropGroup => PropGroup,
            AssetKind.SolarArchetype => Lists.SolarArchetype,
            AssetKind.Ship => Ship,
            AssetKind.Asteroid => Asteroid,
            AssetKind.Equipment => AnyEquipment,
            AssetKind.Effect => Equip.Effect,
            AssetKind.Loadout => Lists.Loadout,
            AssetKind.Faction => Lists.Faction,
            AssetKind.MissionPrefab => MissionPrefab,
            _ => Model,
        };

        /// <summary>The kind of a row: a solar archetype gets the kind of its Create template, and equipment the kind of
        /// its section type.</summary>
        public static ObjectKind Of(AssetRow row) => row.Asset.Kind switch
        {
            AssetKind.SolarArchetype => OfCategory(row.CategoryId),
            AssetKind.Equipment => OfSection(row.Detail),
            var kind => Of(kind),
        };

        /// <summary>The kind of a category of the tree, for its glyph.</summary>
        public static ObjectKind OfCategory(string id) => id.ToLowerInvariant() switch
        {
            "all" => Lists.Enum,
            "solars/station" => Station,
            "solars/planet" => Planet,
            "solars/sun" => Sun,
            "solars/satellite" => Satellite,
            "solars/connections" => JumpGate,
            "solars/solar" => OtherObject,
            "groups" => PropGroup,
            "prefabs" => MissionPrefab,
            "solars" => Lists.SolarArchetype,
            "ships" => Ship,
            "asteroids" => Asteroid,
            "effects" => Equip.Effect,
            "loadouts" => Lists.Loadout,
            "factions" => Lists.Faction,
            var other when other.StartsWith("equipment/", StringComparison.Ordinal) => OfSection(other["equipment/".Length..]),
            "equipment" => AnyEquipment,
            var other when other.StartsWith("models", StringComparison.Ordinal) => Model,
            _ => OtherObject,
        };
    }

    /// <summary>The kinds of the entries of the data lists that no other kind fits.</summary>
    private static class Lists
    {
        public static readonly ObjectKind SolarArchetype = new("Solar archetype", EditorIcons.Building, Station.Color);
        public static readonly ObjectKind Loadout = new("Loadout", EditorIcons.Boxes, EditorTheme.Text);
        public static readonly ObjectKind Faction = new("Faction", EditorIcons.Users, EditorTheme.Text);
        public static readonly ObjectKind Base = new("Base", EditorIcons.DoorOpen, Station.Color);
        public static readonly ObjectKind StarPart = new("Star part", EditorIcons.Star, Sun.Color);
        public static readonly ObjectKind Pilot = new("Pilot", EditorIcons.User, EditorTheme.Text);
        public static readonly ObjectKind Sound = new("Sound", EditorIcons.VolumeUp, EditorTheme.Text);
        public static readonly ObjectKind Explosion = new("Explosion", EditorIcons.Bomb, EditorTheme.Text);
        public static readonly ObjectKind BodyPart = new("Body part", EditorIcons.User, EditorTheme.Text);
        public static readonly ObjectKind Curve = new("Curve", EditorIcons.Stream, EditorTheme.Text);
        public static readonly ObjectKind Fuse = new("Fuse", EditorIcons.Fire, EditorTheme.Text);
        public static readonly ObjectKind Enum = new("Name", EditorIcons.List, EditorTheme.Text);
    }

    /// <summary>
    /// The kinds of the entities of a cutscene, in the groups of the Timeline and the Hierarchy (section 7.3 of the M8
    /// design). The Cutscene view draws its overlay glyphs with them.
    /// </summary>
    public static class Thn
    {
        public static readonly ObjectKind Camera = Entity("Camera", EditorIcons.Video, 0x6C9BE0);
        public static readonly ObjectKind Monitor = Entity("Monitor", EditorIcons.Desktop, 0x6C9BE0);
        public static readonly ObjectKind Character = Entity("Character", EditorIcons.User, 0x60D060);
        public static readonly ObjectKind Prop = Entity("Prop", EditorIcons.CubeSolid, 0xA8A8A8);
        public static readonly ObjectKind Light = Entity("Light", EditorIcons.Lightbulb, 0xE0C040);
        public static readonly ObjectKind Effect = Entity("Effect", EditorIcons.Asterisk, 0xE0905A);
        public static readonly ObjectKind Sound = Entity("Sound", EditorIcons.VolumeUp, 0xB080E0);
        public static readonly ObjectKind Marker = Entity("Marker", EditorIcons.MapMarkerAlt, 0x50C0C0);
        public static readonly ObjectKind Path = Entity("Motion path", EditorIcons.Route, 0xC8B560);
        public static readonly ObjectKind Scene = Entity("Scene", EditorIcons.Image, 0x8A9BB8);
        public static readonly ObjectKind Other = Entity("Entity", EditorIcons.QuestionCircle, 0x909090);

        /// <summary>An event target that names no entity of the script.</summary>
        public static readonly ObjectKind Missing = Entity("Not in this script", EditorIcons.ExclamationTriangle, 0x909090);

        /// <summary>A cutscene script.</summary>
        public static readonly ObjectKind Script = new("Cutscene", EditorIcons.Film, EditorTheme.Text);

        /// <summary>An animation clip of a character, in the list of the animation of a START_MOTION event.</summary>
        public static readonly ObjectKind Clip = new("Animation", EditorIcons.User, EditorTheme.Text);

        /// <summary>A cutscene script that no INI line names. Its glyph is dim (decision D613 of the cutscene categories
        /// design).</summary>
        public static readonly ObjectKind UnusedScript = new("Unused cutscene", EditorIcons.Film, EditorTheme.TextDim);

        // The categories of the cutscene list (section 5.1 of the cutscene categories design). They have the text color,
        // as the script has. So they do not look like the entity kinds with the same glyphs (review finding R18).
        public static readonly ObjectKind IntroScenes = new("Intro scenes", EditorIcons.Desktop, EditorTheme.Text);
        public static readonly ObjectKind MainStory = new("Main story", EditorIcons.Star, EditorTheme.Text);
        public static readonly ObjectKind BasesAndRooms = new("Bases and rooms", EditorIcons.Building, EditorTheme.Text);
        public static readonly ObjectKind BarConversations = new("Bar conversations", EditorIcons.Users, EditorTheme.Text);
        public static readonly ObjectKind Characters = new("Characters", EditorIcons.User, EditorTheme.Text);
        public static readonly ObjectKind RandomMissions = new("Random missions", EditorIcons.Route, EditorTheme.Text);
        public static readonly ObjectKind ModCutscenes = new("Mod cutscenes", EditorIcons.BoxOpen, EditorTheme.Text);
        public static readonly ObjectKind OtherScripts = new("Other", EditorIcons.Folder, EditorTheme.Text);

        /// <summary>An event of a cutscene.</summary>
        public static readonly ObjectKind Event = new("Event", EditorIcons.Bolt, EditorTheme.Text);

        private static ObjectKind Entity(string label, char icon, uint rgb) => new(label, icon, EditorTheme.Rgb(rgb), 12);
    }

    private static class Zones
    {
        // The zone of a field or a nebula has the glyph and the color of its item. Its label tells that it is the zone, not
        // the item (section 5 of the M22 design).
        public static readonly ObjectKind AsteroidField = Zone("Asteroid field zone", EditorIcons.Meteor, 0xE0905A);
        public static readonly ObjectKind Nebula = Zone("Nebula zone", EditorIcons.Cloud, 0xB080E0);
        public static readonly ObjectKind Exclusion = Zone("Exclusion zone", EditorIcons.Ban, 0x50C0C0);
        public static readonly ObjectKind Population = Zone("Population zone", EditorIcons.Users, 0x60C0A0);
        public static readonly ObjectKind PatrolPath = Zone("Patrol path", EditorIcons.Route, 0xE0C040);
        public static readonly ObjectKind TradeLane = Zone("Trade lane zone", EditorIcons.Road, 0x8A9BB8);
        public static readonly ObjectKind Other = Zone("Zone", EditorIcons.VectorSquare, 0xC8B560);

        private static ObjectKind Zone(string label, char icon, uint rgb) => new(label, icon, EditorTheme.Rgb(rgb), 6);
    }

    private static class Equip
    {
        public static readonly ObjectKind Gun = Item("Gun", EditorIcons.Bullseye);
        public static readonly ObjectKind MissileLauncher = Item("Missile launcher", EditorIcons.Rocket);
        public static readonly ObjectKind Missile = Item("Missile", EditorIcons.Rocket);
        public static readonly ObjectKind Munition = Item("Munition", EditorIcons.Circle);
        public static readonly ObjectKind MineDropper = Item("Mine dropper", EditorIcons.Bomb);
        public static readonly ObjectKind Shield = Item("Shield", EditorIcons.ShieldAlt);
        public static readonly ObjectKind Engine = Item("Engine", EditorIcons.Fire);
        public static readonly ObjectKind Power = Item("Power plant", EditorIcons.Bolt);
        public static readonly ObjectKind Thruster = Item("Thruster", EditorIcons.Wind);
        public static readonly ObjectKind Countermeasure = Item("Countermeasure", EditorIcons.Magic);
        public static readonly ObjectKind Commodity = Item("Commodity", EditorIcons.Box);
        public static readonly ObjectKind Light = Item("Light", EditorIcons.Lightbulb);
        public static readonly ObjectKind Cloak = Item("Cloak", EditorIcons.EyeSlash);
        public static readonly ObjectKind Tradelane = Item("Trade lane", EditorIcons.Route);
        public static readonly ObjectKind Scanner = Item("Scanner", EditorIcons.SatelliteDish);
        public static readonly ObjectKind Tractor = Item("Tractor", EditorIcons.Magnet);
        public static readonly ObjectKind RepairKit = Item("Repair kit", EditorIcons.Wrench);
        public static readonly ObjectKind ShieldBattery = Item("Shield battery", EditorIcons.BatteryFull);
        public static readonly ObjectKind CargoPod = Item("Cargo pod", EditorIcons.Boxes);
        public static readonly ObjectKind LootCrate = Item("Loot crate", EditorIcons.BoxOpen);
        public static readonly ObjectKind Animation = Item("Animation", EditorIcons.Film);
        public static readonly ObjectKind Effect = Item("Effect", EditorIcons.Asterisk);

        private static ObjectKind Item(string label, char icon) => new(label, icon, EditorTheme.Text);
    }
}
