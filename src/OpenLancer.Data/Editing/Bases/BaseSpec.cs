using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>The characters that a new base copies from its template (see <see cref="BasePlans.Create"/>).</summary>
public enum CharacterCopy
{
    /// <summary>The bartender, the dealers and the bar crowd of the rooms that the base keeps.</summary>
    Template,

    /// <summary>The bartender and the dealers of the rooms that the base keeps: each character of a fixture.</summary>
    Dealers,

    /// <summary>No characters.</summary>
    None,
}

/// <summary>The docking ring of a new planet base. A value that is null comes from the ring of the template.</summary>
/// <param name="Archetype">The solar archetype of the ring. It needs a docking sphere.</param>
/// <param name="Position">The position of the ring, or null for the place of the ring of the template. That place has
/// the same height above the surface of the planet, in the same direction.</param>
/// <param name="Rotation">The rotation of the ring in degrees, or null for the rotation of the ring of the
/// template.</param>
public sealed record RingSpec(string? Archetype = null, Vector3? Position = null, Vector3? Rotation = null);

/// <summary>
/// The object that docks with a new base (section C.5 of the bases design): a new station, or a planet with a docking
/// ring. A value that is null comes from the dock object of the template.
/// </summary>
public sealed record DockSpec
{
    /// <summary>The solar archetype of a new station or a new planet, or null for the archetype of the dock object of
    /// the template. A station archetype needs a docking sphere.</summary>
    public string? Archetype { get; init; }

    /// <summary>The position of a new station or a new planet. An existing planet needs none.</summary>
    public Vector3? Position { get; init; }

    /// <summary>The rotation of a new station or a new planet in degrees, or null for no <c>rotate</c> line.</summary>
    public Vector3? Rotation { get; init; }

    /// <summary>The nickname of a new station or a new planet, or null for the next free nickname.</summary>
    public string? Nickname { get; init; }

    /// <summary>An existing planet of the system that gets the base, or null.</summary>
    public string? Planet { get; init; }

    /// <summary>The docking ring of a planet base, or null for a copy of the ring of the template.</summary>
    public RingSpec? Ring { get; init; }
}

/// <summary>A request for a new base from a template base (see <see cref="BasePlans.Create"/>).</summary>
/// <param name="Nickname">The nickname of the new base, at most 31 characters.</param>
/// <param name="System">The system of the base.</param>
/// <param name="Template">The base whose rooms, characters, news and dock object the new base copies.</param>
/// <param name="Name">The name of the base and its dock object in the game.</param>
public sealed record BaseSpec(string Nickname, string System, string Template, string Name)
{
    /// <summary>The plain text of the infocard of the dock object. Null gives a new station or a new planet the name as
    /// its infocard, and an existing planet keeps its infocard.</summary>
    public string? Info { get; init; }

    /// <summary>The rooms of the template that the base keeps, or null for each room.</summary>
    public IReadOnlyList<string>? Rooms { get; init; }

    /// <summary>The room where the player lands, or null for the start room of the template.</summary>
    public string? StartRoom { get; init; }

    /// <summary>The characters that the base copies.</summary>
    public CharacterCopy Characters { get; init; } = CharacterCopy.Template;

    /// <summary>The faction that owns the base, or null for the faction of the template. It is also the reputation of
    /// each new dock object.</summary>
    public string? LocalFaction { get; init; }

    /// <summary>The object that docks with the base, or null for no dock object.</summary>
    public DockSpec? Dock { get; init; }

    /// <summary>
    /// Gives the text of a text key of a section of W, for example the <c>individual_name</c> of a character, or null.
    /// The host knows the texts of the game. Without it, the plan knows only the texts of the mods.
    /// </summary>
    public Func<SectionRef, string, string?>? Text { get; init; }
}
