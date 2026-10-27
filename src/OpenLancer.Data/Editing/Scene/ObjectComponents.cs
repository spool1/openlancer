using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>The components of the sections of a system file (section 5.4 of the M3 design, decisions D243 and
/// D258).</summary>
public enum ObjectComponent
{
    /// <summary>Each object: <c>nickname</c>, <c>pos</c>, <c>rotate</c> and <c>archetype</c>.</summary>
    Solar,

    /// <summary>An object with a <c>loadout</c> line.</summary>
    LoadoutUser,

    /// <summary>An object with a <c>dock_with</c> line: the player docks with the base.</summary>
    DockableStation,

    /// <summary>An object with a <c>base</c> line: the object of the base in space.</summary>
    BaseObject,

    /// <summary>An object with a <c>reputation</c> line.</summary>
    FactionMember,

    /// <summary>An object with a <c>goto</c> line, or a gate type.</summary>
    JumpConnection,

    /// <summary>An object with a <c>prev_ring</c> or <c>next_ring</c> line, or the type <c>tradelane_ring</c>.</summary>
    TradeLaneRing,

    /// <summary>An object with a <c>ring</c> line.</summary>
    PlanetRing,

    /// <summary>Each <c>[Nebula]</c> and <c>[Asteroids]</c>: <c>zone</c> and <c>file</c>.</summary>
    FieldReferences,

    /// <summary>A zone with <c>encounter</c> lines.</summary>
    EncounterZone,
}

/// <summary>A component that a section has, and its problems.</summary>
public sealed record ComponentState(ObjectComponent Kind, IReadOnlyList<Diagnostic> Problems);

/// <summary>
/// The components of a section of a system file: a view over its keys, as Unity shows the components of a GameObject
/// (roadmap section 5.3, milestone M3). Each component names the keys that it owns. Each component of an object requires
/// Solar, and Solar cannot go, so the Remove of Solar is the only refused remove (Unity RequireComponent). "Add component"
/// and "Remove component" are one entry of the edit history each.
/// </summary>
public static class ObjectComponents
{
    private static readonly Dictionary<ObjectComponent, (string Name, string[] Keys)> Info = new()
    {
        [ObjectComponent.Solar] = ("Solar", ["nickname", "pos", "rotate", "archetype"]),
        [ObjectComponent.LoadoutUser] = ("Loadout user", ["loadout"]),
        [ObjectComponent.DockableStation] = ("Dockable station", ["dock_with"]),
        [ObjectComponent.BaseObject] = ("Base object", ["base"]),
        [ObjectComponent.FactionMember] = ("Faction member", ["reputation"]),
        [ObjectComponent.JumpConnection] = ("Jump connection", ["goto"]),
        [ObjectComponent.TradeLaneRing] = ("Trade lane ring", ["prev_ring", "next_ring", "tradelane_space_name"]),
        [ObjectComponent.PlanetRing] = ("Planet ring", ["ring"]),
        [ObjectComponent.FieldReferences] = ("Field references", ["zone", "file"]),
        [ObjectComponent.EncounterZone] = ("Encounter zone", ["encounter", "faction", "faction_weight"]),
    };

    /// <summary>The components that "Add component" offers for an object, in order (decision D244).</summary>
    private static readonly ObjectComponent[] Addable =
        [ObjectComponent.LoadoutUser, ObjectComponent.DockableStation, ObjectComponent.BaseObject, ObjectComponent.FactionMember];

    /// <summary>The name of a component, for example "Dockable station".</summary>
    public static string Name(ObjectComponent kind) => Info[kind].Name;

    /// <summary>The keys that a component owns.</summary>
    public static IReadOnlyList<string> Keys(ObjectComponent kind) => Info[kind].Keys;

    /// <summary>True for a component that "Add component" can offer for an object (decision D244). Such a component has
    /// a value with a list (see <see cref="ListOf"/>).</summary>
    public static bool IsAddable(ObjectComponent kind) => Addable.Contains(kind);

    /// <summary>The list of the value of an added component, for its picker: <c>loadout</c>, <c>base</c> or
    /// <c>faction</c>.</summary>
    public static string ListOf(ObjectComponent kind) => kind switch
    {
        ObjectComponent.LoadoutUser => "loadout",
        ObjectComponent.FactionMember => "faction",
        ObjectComponent.DockableStation or ObjectComponent.BaseObject => "base",
        _ => throw new ArgumentException($"{Name(kind)} has no picker.", nameof(kind)),
    };

    /// <summary>
    /// The components of a section in table order, each with the list problems and the component checks of its keys. Empty
    /// for a section that is not an object, a zone, a nebula or a field of a system file.
    /// </summary>
    public static IReadOnlyList<ComponentState> Of(EditSession session, SectionRef section)
    {
        if (session.Get(section) is not { } view || SceneSections.KindOf(session, section) is not { } kind)
            return [];
        var present = new List<ObjectComponent>();
        switch (kind)
        {
            case SceneKind.Object:
                present.Add(ObjectComponent.Solar);
                var type = SceneSections.ArchetypeType(session, view);
                Add(present, ObjectComponent.LoadoutUser, view.Lines("loadout").Count > 0);
                Add(present, ObjectComponent.DockableStation, view.Lines("dock_with").Count > 0);
                Add(present, ObjectComponent.BaseObject, view.Lines("base").Count > 0);
                Add(present, ObjectComponent.FactionMember, view.Lines("reputation").Count > 0);
                Add(present, ObjectComponent.JumpConnection, view.Lines("goto").Count > 0 ||
                                                             type is "jump_gate" or "jump_hole" or "jumphole" or "airlock_gate");
                Add(present, ObjectComponent.TradeLaneRing, view.Lines("prev_ring").Count > 0 ||
                                                            view.Lines("next_ring").Count > 0 || type == "tradelane_ring");
                Add(present, ObjectComponent.PlanetRing, view.Lines("ring").Count > 0);
                break;
            case SceneKind.Nebula or SceneKind.Asteroids:
                present.Add(ObjectComponent.FieldReferences);
                break;
            case SceneKind.Zone:
                Add(present, ObjectComponent.EncounterZone, view.Lines("encounter").Count > 0);
                break;
        }
        var problems = session.CheckLists(section).Concat(ComponentChecks.Check(session, section, onlyActiveMod: false)).ToList();
        return present.Select(c => new ComponentState(c, problems
            .Where(p => p.Key != null && Info[c].Keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .ToList())).ToList();
    }

    /// <summary>
    /// The components that "Add component" offers for an object (decision D244), each with null when it can apply, or why
    /// it cannot. A component that the object has is not in the list. Empty for another section.
    /// </summary>
    public static IReadOnlyList<(ObjectComponent Kind, string? Unavailable)> CanAdd(EditSession session, SectionRef section)
    {
        if (SceneSections.KindOf(session, section) != SceneKind.Object || session.Get(section) is not { } view)
            return [];
        var present = Of(session, section).Select(c => c.Kind).ToHashSet();
        var result = new List<(ObjectComponent, string?)>();
        foreach (var kind in Addable.Where(k => !present.Contains(k)))
        {
            string? unavailable = view.ReadOnlyReason;
            if (unavailable == null && kind == ObjectComponent.DockableStation && !HasDockingSphere(session, view))
                unavailable = "Pick an archetype with a docking sphere first.";
            result.Add((kind, unavailable));
        }
        return result;
    }

    /// <summary>The edit that adds a component with its value, for example a loadout, as one entry of the edit
    /// history.</summary>
    /// <exception cref="ArgumentException">The component cannot be added with a value.</exception>
    public static EditCommand Add(SectionRef section, ObjectComponent kind, string value)
    {
        if (!Addable.Contains(kind))
            throw new ArgumentException($"{Name(kind)} cannot be added in the Inspector.", nameof(kind));
        return EditCommand.Group($"Add {Name(kind)}", [EditCommand.SetLinesFromText(section, Info[kind].Keys[0], [value])]);
    }

    /// <summary>Why "Remove component" cannot run, or null (decision D244).</summary>
    public static string? RemoveProblem(ObjectComponent kind) => kind switch
    {
        ObjectComponent.Solar => "Each object needs an archetype.",
        ObjectComponent.JumpConnection or ObjectComponent.TradeLaneRing =>
            "The editor changes jump gates, jump holes and trade lane rings in milestone M27.",
        ObjectComponent.FieldReferences => "Delete the nebula or the asteroid field to remove its section.",
        ObjectComponent.EncounterZone => "The editor edits the encounters of a zone in milestone M3b.",
        _ => null,
    };

    /// <summary>The edit that removes the keys of a component, as one entry of the edit history. Remove of Dockable
    /// station keeps <c>base</c>, and Remove of Base object keeps <c>dock_with</c> (decision D258).</summary>
    /// <exception cref="InvalidOperationException">The component cannot go (see <see cref="RemoveProblem"/>).</exception>
    public static EditCommand Remove(SectionRef section, ObjectComponent kind)
    {
        if (RemoveProblem(kind) is { } problem)
            throw new InvalidOperationException($"{Name(kind)}: {problem}");
        return EditCommand.Group($"Remove {Name(kind)}", Info[kind].Keys.Select(k => EditCommand.RemoveName(section, k)).ToList());
    }

    /// <summary>
    /// The component checks of a section (layer 3, section 5.5 of the M3 design): for an object, a zone or a
    /// <c>[Base]</c> of <c>universe.ini</c>, also of the lower layer. Only a zone nickname that another zone has is an
    /// error. The Inspector shows the warnings of a base here, because a base has no components strip.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Checks(EditSession session, SectionRef section) =>
        ComponentChecks.Check(session, section, onlyActiveMod: false);

    /// <summary>True when the archetype of an object has a <c>docking_sphere</c> line in W.</summary>
    internal static bool HasDockingSphere(EditSession session, SectionView view) =>
        SceneSections.Last(view, "archetype") is { } archetype &&
        session.Lists.List("solar_archetype")?.Find(archetype)?.Section is { } solar &&
        session.Get(solar)?.Lines("docking_sphere").Count > 0;

    private static void Add(List<ObjectComponent> present, ObjectComponent kind, bool has)
    {
        if (has)
            present.Add(kind);
    }
}
