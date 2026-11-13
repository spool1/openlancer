using System;
using System.Collections.Frozen;
using System.Collections.Generic;

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
public sealed record ThnTemplateRule(ThnItemKind Kind, string What, bool MissingStops, string? Consequence, bool Closed)
{
    /// <summary>The data lists of the item, in order (see <see cref="ThnTemplateRules.ListsOf"/>).</summary>
    public IReadOnlyList<string> Lists => ThnTemplateRules.ListsOf(Kind);
}

/// <summary>
/// The one table of the entity templates of a cutscene (section 12.1 of the M8b design): the COMPOUND categories, the
/// costume of a DEFORMABLE, the effect of a PSYS and the sound of a SOUND, with their game data and their data lists.
/// The error checks (<see cref="ThnChecks"/>), the game data of the save gate and of the validation, and the Inspector
/// read it.
/// </summary>
public static class ThnTemplateRules
{
    /// <summary>The template of a COMPOUND that takes the player ship of the scene (ThnScriptInstance.cs:79-96). The
    /// game gives a player ship to the scene of each room (RoomGameplay.cs:1038-1046).</summary>
    public const string PlayerShip = "PlayerShip";

    /// <summary>The template of a PSYS that takes the engine effect of the player ship of the scene
    /// (ThnScriptInstance.cs:98-104). The engine reads this template before the effects.</summary>
    public const string PlayerShipEngines = "PlayerShipEngines";

    private const string NoModel = "The scene shows the entity without a model.";

    /// <summary>
    /// The mesh categories of a COMPOUND and their game data (ThnScriptInstance.cs:120-154). The engine stops when a
    /// solar, a ship, a room or a cart does not exist, and when the item has no model. A missing prop, equipment or
    /// asteroid gives an object without a model. A category that is not in this table stops the engine. The names
    /// compare without letter case.
    /// </summary>
    public static IReadOnlyDictionary<string, ThnTemplateRule> Categories { get; } = new Dictionary<string, ThnTemplateRule>
    {
        ["solar"] = new(ThnItemKind.Archetype, "solar archetype", MissingStops: true, null, Closed: true),
        ["ship"] = new(ThnItemKind.Ship, "ship", MissingStops: true, null, Closed: true),
        ["spaceship"] = new(ThnItemKind.Ship, "ship", MissingStops: true, null, Closed: true),
        ["prop"] = new(ThnItemKind.Prop, "prop of petalDB", MissingStops: false, NoModel, Closed: true),
        ["room"] = new(ThnItemKind.Room, "room of petalDB", MissingStops: true, null, Closed: true),
        ["equipment cart"] = new(ThnItemKind.Cart, "cart of petalDB", MissingStops: true, null, Closed: true),
        ["equipment"] = new(ThnItemKind.Equipment, "equipment", MissingStops: false, NoModel, Closed: true),
        ["asteroid"] = new(ThnItemKind.Asteroid, "asteroid", MissingStops: false, NoModel, Closed: true),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The costume of a DEFORMABLE without <c>userprops.Actor</c> (ThnScriptInstance.cs:255-264). With an
    /// Actor, the costume loads only when no room NPC or RTC character plays the actor, so the checks skip it.</summary>
    public static ThnTemplateRule Costume { get; } = new(ThnItemKind.Costume, "costume", MissingStops: true, null, Closed: true);

    /// <summary>The effect of a PSYS (ThnScriptInstance.cs:192-205). Without the effect, the scene shows nothing for the
    /// entity.</summary>
    public static ThnTemplateRule Effect { get; } = new(ThnItemKind.Effect, "effect", MissingStops: false,
        "The scene does not show this entity.", Closed: true);

    /// <summary>The sound of a SOUND (ThnScriptInstance.cs:282-288). A template can also name a voice line, so a name
    /// outside the list is valid.</summary>
    public static ThnTemplateRule Sound { get; } = new(ThnItemKind.Sound, "sound", MissingStops: false, null, Closed: false);

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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>True when the room fills in a template of the kind that starts with <c>$</c>, for example
    /// <c>$terrain_dyna_01</c>. The engine fills in the template of a COMPOUND and of a DEFORMABLE, but it reads the
    /// template of a PSYS and of a SOUND as it is (ThnScriptInstance.cs:106-109, :192-196, :282-288).</summary>
    public static bool RoomFillsIn(ThnItemKind kind) => kind is not (ThnItemKind.Effect or ThnItemKind.Sound);

    /// <summary>True for an item whose model file the engine loads when it builds the entity: a solar archetype, a ship,
    /// an equipment or an asteroid. The engine stops when the item has no <c>DA_archetype</c> line, or when the file of
    /// the line does not exist (GameItemDb.cs:122-138, :2088-2105, ThnScriptInstance.cs:124-151).</summary>
    public static bool NeedsModel(ThnItemKind kind) =>
        kind is ThnItemKind.Archetype or ThnItemKind.Ship or ThnItemKind.Equipment or ThnItemKind.Asteroid;
}
