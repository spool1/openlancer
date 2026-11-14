using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Thn;

/// <summary>The rule of the template of one kind of entity: the game data that the template names, and what the game
/// does when the game data has no such item.</summary>
/// <param name="Kind">The collection of the game data that the engine reads for the template.</param>
/// <param name="What">The name of the item in a message, for example "solar archetype".</param>
/// <param name="MissingStops">True when the engine stops for a template that does not exist. The engine always stops for
/// an item without a model (see <see cref="ThnTemplateRules.NeedsModel"/>).</param>
/// <param name="Consequence">What the scene does with a template that does not exist, when the engine does not stop,
/// or null when it stops.</param>
/// <param name="Closed">False when a value outside the lists is valid, for example a SOUND that names a voice line.
/// Such a value gives no problem.</param>
/// <param name="Label">The name of the item in the Inspector, for example "Solar archetype". It is the label of the
/// first data list of the item.</param>
public sealed record ThnTemplateRule(ThnItemKind Kind, string What, bool MissingStops, string? Consequence, bool Closed,
    string Label)
{
    /// <summary>The data lists of the item, in order (see <see cref="ThnTemplateRules.ListsOf"/>).</summary>
    public IReadOnlyList<string> Lists => ThnTemplateRules.ListsOf(Kind);
}

/// <summary>
/// The one table of the entity templates of a cutscene (section 12.1 of the M8b design): the COMPOUND categories, the
/// costume of a DEFORMABLE, the effect of a PSYS and the sound of a SOUND, with their game data and their data lists.
/// It also has the rule of the clip of a START_MOTION event. The error checks (<see cref="ThnChecks"/>), the game data
/// of the save gate and of the validation, and the Inspector read it.
/// </summary>
public static class ThnTemplateRules
{
    /// <summary>The template of a COMPOUND that takes the player ship of the scene (ThnScriptInstance.cs:79-96). The
    /// game gives a player ship to the scene of each room (RoomGameplay.cs:1038-1046).</summary>
    public const string PlayerShip = "PlayerShip";

    /// <summary>The template of a PSYS that takes the engine effect of the player ship of the scene
    /// (ThnScriptInstance.cs:98-104). The engine reads this template before the effects.</summary>
    public const string PlayerShipEngines = "PlayerShipEngines";

    /// <summary>The entity type of a character.</summary>
    public const string CharacterType = "DEFORMABLE";

    /// <summary>The path of the clip of a START_MOTION event.</summary>
    public const string AnimationPath = "props.animation";

    /// <summary>What the scene does when a character gets a clip that the game does not have: the engine finds no
    /// script with the name and starts no motion (AnimationComponent.cs:192-203).</summary>
    public const string ClipConsequence = "The character does not move.";

    /// <summary>The problem of a START_MOTION event on a character with an empty clip. The engine finds no script with
    /// an empty name either.</summary>
    public const string NoClip = "The event has no clip.";

    private const string NoModel = "The scene shows the entity without a model.";

    /// <summary>
    /// The mesh categories of a COMPOUND and their game data (ThnScriptInstance.cs:120-154). The engine stops when a
    /// solar, a ship, a room or a cart does not exist, and when the item has no model. A missing prop, equipment or
    /// asteroid gives an object without a model. A category that is not in this table stops the engine. The names
    /// compare without letter case.
    /// </summary>
    public static IReadOnlyDictionary<string, ThnTemplateRule> Categories { get; } = new Dictionary<string, ThnTemplateRule>
    {
        ["solar"] = new(ThnItemKind.Archetype, "solar archetype", MissingStops: true, null, Closed: true, "Solar archetype"),
        ["ship"] = new(ThnItemKind.Ship, "ship", MissingStops: true, null, Closed: true, "Ship"),
        ["spaceship"] = new(ThnItemKind.Ship, "ship", MissingStops: true, null, Closed: true, "Ship"),
        ["prop"] = new(ThnItemKind.Prop, "prop of petalDB", MissingStops: false, NoModel, Closed: true, "Prop"),
        ["room"] = new(ThnItemKind.Room, "room of petalDB", MissingStops: true, null, Closed: true, "Room"),
        ["equipment cart"] = new(ThnItemKind.Cart, "cart of petalDB", MissingStops: true, null, Closed: true, "Cart"),
        ["equipment"] = new(ThnItemKind.Equipment, "equipment", MissingStops: false, NoModel, Closed: true, "Equipment"),
        ["asteroid"] = new(ThnItemKind.Asteroid, "asteroid", MissingStops: false, NoModel, Closed: true, "Asteroid"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The costume of a DEFORMABLE without <c>userprops.Actor</c> (ThnScriptInstance.cs:255-264). With an
    /// Actor, the costume loads only when no room NPC or RTC character plays the actor, so the checks skip it.</summary>
    public static ThnTemplateRule Costume { get; } =
        new(ThnItemKind.Costume, "costume", MissingStops: true, null, Closed: true, "Costume");

    /// <summary>The effect of a PSYS (ThnScriptInstance.cs:192-205). Without the effect, the scene shows nothing for the
    /// entity.</summary>
    public static ThnTemplateRule Effect { get; } = new(ThnItemKind.Effect, "effect", MissingStops: false,
        "The scene does not show this entity.", Closed: true, "Effect");

    /// <summary>The sound of a SOUND (ThnScriptInstance.cs:282-288). A template can also name a voice line, so a name
    /// outside the list is valid.</summary>
    public static ThnTemplateRule Sound { get; } =
        new(ThnItemKind.Sound, "sound", MissingStops: false, null, Closed: false, "Sound");

    /// <summary>The data lists of a kind of item, in order. The first list with the name gives the item, as the engine
    /// first reads the effects and then the visual effects (ThnScriptInstance.cs:194-196). The engine puts the asteroids
    /// and the mines in one collection (AsteroidArchIni.cs).</summary>
    public static IReadOnlyList<string> ListsOf(ThnItemKind kind) => kind switch
    {
        ThnItemKind.Archetype => ["solar_archetype"],
        ThnItemKind.Ship => ["ship"],
        ThnItemKind.Room => ["petal_room"],
        ThnItemKind.Cart => ["petal_cart"],
        ThnItemKind.Prop => ["petal_prop"],
        ThnItemKind.Equipment => ["equipment"],
        ThnItemKind.Asteroid => ["asteroid", "asteroid_mine"],
        ThnItemKind.Costume => ["costume"],
        ThnItemKind.Effect => ["effect", "vis_effect"],
        ThnItemKind.Sound => ["sound"],
        ThnItemKind.Animation => ["animation"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>True when the room fills in a template of the kind that starts with <c>$</c>, for example
    /// <c>$terrain_dyna_01</c>. The engine fills in the template of a COMPOUND and of a DEFORMABLE, but it reads the
    /// template of a PSYS and of a SOUND, and a clip, as it is (ThnScriptInstance.cs:106-109, :192-196,
    /// :282-288).</summary>
    public static bool RoomFillsIn(ThnItemKind kind) => kind is not (ThnItemKind.Effect or ThnItemKind.Sound or
        ThnItemKind.Animation);

    /// <summary>The value of a key of the <c>userprops</c> of an entity, or null. The engine finds such a key without
    /// letter case, and the last key with the name wins (ThnScript.cs:305-314).</summary>
    public static ThnValue? UserProp(ThnTable entity, string key) =>
        entity["userprops"] is ThnTable props
            ? props.Record.LastOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value
            : null;

    /// <summary>True when a DEFORMABLE loads the costume of its template: it has no <c>userprops.Actor</c>, or an empty
    /// one. With an Actor, the costume loads only when no room NPC or RTC character plays the actor
    /// (ThnScriptInstance.cs:255-264). The engine reads an Actor with <c>ToString</c>, so only an empty text counts as
    /// no Actor.</summary>
    public static bool LoadsCostume(ThnTable entity) => UserProp(entity, "Actor") is null or ThnText { Value.Length: 0 };

    /// <summary>True for an item whose model file the engine loads when it builds the entity: a solar archetype, a ship,
    /// an equipment or an asteroid. The engine stops when the item has no <c>DA_archetype</c> line, or when the file of
    /// the line does not exist (GameItemDb.cs:122-138, :2088-2105, ThnScriptInstance.cs:124-151). An equipment also
    /// needs a section type that gets a model (see <see cref="EquipmentModel"/>).</summary>
    public static bool NeedsModel(ThnItemKind kind) =>
        kind is ThnItemKind.Archetype or ThnItemKind.Ship or ThnItemKind.Equipment or ThnItemKind.Asteroid;

    /// <summary>
    /// The type of the entity of the first target of an event, for example "DEFORMABLE". Null when the event has no
    /// target, or when the target names no entity of the script. For a name that two entities have, the last entity
    /// counts, as in the engine (ThnScript.cs:107, :130-137).
    /// </summary>
    /// <param name="entities">The entities of the script, in the order of the engine.</param>
    /// <param name="targets">The targets of the event.</param>
    public static string? TargetType(IEnumerable<ThnTable> entities, IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        targets.Count > 0 && entities.LastOrDefault(e =>
            ThnScriptModel.NameOf(e)?.Equals(targets[0], StringComparison.OrdinalIgnoreCase) == true) is { } entity
            ? ThnSchema.TypeOf(ThnElementKind.Entity, entity, symbols)
            : null;

    /// <summary>
    /// True when the clip of a START_MOTION event names a clip of the animation files (the list <c>animation</c>): the
    /// first target names a character (see <see cref="TargetType"/>). A character plays the clips of the animation
    /// files (ThnScriptInstance.cs:266-270). A COMPOUND plays the scripts of its own model (GameObject.cs:602-605), and a
    /// target that names no entity of the script names an object of another script. So the Inspector gives the clip
    /// picker, and the save gate checks the clip, only when this rule is true.
    /// </summary>
    public static bool PlaysClips(IEnumerable<ThnTable> entities, IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        TargetType(entities, targets, symbols) == CharacterType;

    /// <summary>The problem of a clip that the game does not have, with its consequence: an empty clip
    /// (<see cref="NoClip"/>) or a name that no clip has. The clip field of the Inspector, the save gate and the agent
    /// tools give this text.</summary>
    public static string ClipProblem(string clip) => clip.Length == 0
        ? $"{NoClip} {ClipConsequence}"
        : $"The game has no animation {ThnChecks.Shown(new ThnText(clip))}. {ClipConsequence}";

    /// <summary>
    /// What the engine makes of an equipment section for a COMPOUND (GameItemDb.cs:1008-1303). Only some section types
    /// get the model of their <c>DA_archetype</c> line. The engine makes the items of the other types without a model, so
    /// a COMPOUND with such an item stops the engine (ThnScriptInstance.cs:144-147). The engine makes no item of an
    /// <c>[Armor]</c> or a <c>[Shield]</c> section, so such a template names no item. The names compare without letter
    /// case.
    /// </summary>
    /// <param name="sectionType">The section type, for example <c>Gun</c>.</param>
    /// <param name="motor">True when the section has a <c>motor</c> line with a value. Only a <c>[Munition]</c> with a
    /// motor gets a model (GameItemDb.cs:1012-1043).</param>
    /// <returns>Ready when the item gets the model of its <c>DA_archetype</c> line, NoModel when the engine makes the
    /// item without a model, and Missing when the engine makes no item. A type that is not in the table gives Ready, so
    /// the check gives no false error.</returns>
    public static ThnItemState EquipmentModel(string sectionType, bool motor) => sectionType.ToLowerInvariant() switch
    {
        "munition" => motor ? ThnItemState.Ready : ThnItemState.NoModel,
        "light" or "scanner" or "tractor" or "cloakingdevice" or "shieldbattery" or "repairkit" or "engine" or "tradelane" or
            "commodity" or "internalfx" or "attachedfx" => ThnItemState.NoModel,
        "armor" or "shield" => ThnItemState.Missing,
        // Gun, Thruster, ShieldGenerator, Power, LootCrate, CargoPod, Countermeasure, CountermeasureDropper, MineDropper
        // and Mine.
        _ => ThnItemState.Ready,
    };
}
