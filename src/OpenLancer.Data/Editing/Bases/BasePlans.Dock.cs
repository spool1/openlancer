using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing.Bases;

public static partial class BasePlans
{
    /// <summary>The detail code of an archetype that cannot be a dock object. It is an unknown archetype, a station
    /// archetype without a docking sphere, or an archetype of another type.</summary>
    public const string DockArchetype = "DockArchetype";

    /// <summary>The detail code of a planet that is not a planet of the system, or that another base uses.</summary>
    public const string NotAPlanet = "NotAPlanet";

    /// <summary>The detail code of a dock object without a position.</summary>
    public const string DockPlace = "DockPlace";

    /// <summary>The lines of the ring of the template that a new ring does not copy: the new ring has its own place,
    /// archetype and base.</summary>
    private static readonly string[] RingSkipped =
        ["nickname", "ids_name", "pos", "rotate", "archetype", "dock_with", "base", "parent"];

    /// <summary>The commands of the dock objects of a new base, with the new objects and the changed planet. It also
    /// holds the object that the games link to the base, the new ring, and true when a new object docks with the
    /// base.</summary>
    private sealed record DockPlan(List<EditCommand> Commands, List<SectionRef> Adds, List<SectionRef> Changes,
        SectionRef Named, SectionRef? Ring, bool Docks);

    /// <summary>
    /// The dock objects of a new base (see <see cref="DockSpec"/>), or null when the request has none. A station gets
    /// <c>base</c>, <c>dock_with</c>, the name of the base and the infocard. It also gets the lines of the station of
    /// the template (see <see cref="BaseTemplate.StationLines"/>). A planet gets <c>base</c> and the name of the base.
    /// Its new docking ring gets <c>dock_with</c>, the name of the ring of the template and the lines of that ring.
    /// Retail rings have no <c>base</c> line. A <c>reputation</c> line names the faction of the request when it gives
    /// one.
    /// </summary>
    /// <remarks>A <c>loadout</c> names hardpoints of an archetype. So a new object copies the loadout of the template
    /// only when it has the archetype of the template object.</remarks>
    private static (DockPlan? Plan, Refusal? Refusal) DockCommands(EditSession session, BaseSpec spec,
        BaseTemplate template, string system, string nickname, ValueBase name, SectionCopies texts)
    {
        if (spec.Dock is not { } dock)
            return (null, null);
        if (ScenePlans.SystemFileProblem(session, system, out var path) is { } fileProblem)
            return (null, new Refusal(fileProblem.Source, fileProblem.Message, NoSystemFile, "system"));
        var main = template.MainObject is { } mainRef ? session.Get(mainRef) : null;
        var info = ItemEdits.ToInfocard(string.IsNullOrWhiteSpace(spec.Info) ? spec.Name.Trim() : spec.Info.Trim());
        var faction = spec.LocalFaction?.Trim() is { Length: > 0 } given ? given : null;
        var commands = new List<EditCommand>();

        if (dock.Planet?.Trim() is { Length: > 0 } planetName)
        {
            var planet = session.Sections(path).FirstOrDefault(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) &&
                                                                     SceneSections.Is(s.Id.Key ?? "", planetName));
            var view = planet == null ? null : session.Get(planet);
            if (planet == null || view == null || SceneSections.ArchetypeType(session, view) != "planet")
                return (null, new Refusal(path, $"{planetName} is not a planet of {system}.", NotAPlanet,
                    "dock_object.planet"));
            if (SceneSections.Last(view, "base") is { } other && !SceneSections.Is(other, nickname) &&
                session.Base(other) != null)
                return (null, new Refusal(path, $"The planet {planet.Id.Key} is the dock object of the base {other}. Pick a " +
                                                "planet without a base.", NotAPlanet, "dock_object.planet", planet));
            if (view.ReadOnlyReason is { } reason)
                return (null, new Refusal(path, $"{planet.Id.Key}: {reason}", null, "dock_object.planet", planet));
            commands.Add(EditCommand.SetLines(planet, "base", [[new StringValue(nickname)]]));
            commands.Add(EditCommand.SetLines(planet, "ids_name", [[name]]));
            if (!string.IsNullOrWhiteSpace(spec.Info) &&
                texts.New(StringKind.Infocard, info, planet.Id, "ids_info") is { } planetInfo)
                commands.Add(EditCommand.SetLines(planet, "ids_info", [[planetInfo]]));
            var ring = Ring(session, spec, template, path, planet.Id.Key!, SceneSections.Position(view) ?? Vector3.Zero,
                SceneSections.RadiusOf(session, view), nickname, faction, texts);
            if (ring.Refusal != null)
                return (null, ring.Refusal);
            commands.AddRange(ring.Commands);
            return (new DockPlan(commands, ring.Added is { } r ? [r] : [], [planet], planet, ring.Added, ring.Added != null),
                null);
        }

        var archetype = dock.Archetype?.Trim() is { Length: > 0 } a ? a
            : main == null ? null
            : SceneSections.Last(main, "archetype");
        if (archetype == null)
            return (null, new Refusal(path, $"Give the archetype of the dock object. The template {template.Nickname} has no " +
                                            "dock object.", DockArchetype, "dock_object.archetype"));
        var type = ScenePlans.TypeOf(session, archetype);
        if (type != "planet" && DockArchetypeProblem(session, archetype) is { } archetypeProblem)
            return (null, new Refusal(path, archetypeProblem, DockArchetype, "dock_object.archetype"));
        if (dock.Position is not { } position)
            return (null, new Refusal(path, $"Give the position of the new {(type == "planet" ? "planet" : "station")}.",
                DockPlace, "dock_object.position"));
        var binary = session.IsBinaryFile(path);
        var (givenName, nameRefusal) = ObjectName(session, dock.Nickname, binary, path);
        if (nameRefusal != null)
            return (null, nameRefusal);
        var stem = $"{system}_{(type == "planet" ? "planet" : "station")}".ToLowerInvariant();
        if (ScenePlans.NewObject(session, path, stem, position, archetype, null, type == "planet" ? null : nickname,
                nickname, givenName, out var error) is not { } made)
            return (null, new Refusal(path, error!, null, "dock_object"));
        var objectName = made.Nickname;
        var objectId = new SectionIdentity(SceneSections.ObjectType, objectName);
        var infoValue = texts.New(StringKind.Infocard, info, objectId, "ids_info");
        var entries = WithLines(made.Entries, name, dock.Rotation, null, infoValue);

        // The station copies the lines of the object of the template that docks: the station, or the ring of a planet.
        var source = type == "planet" ? template.MainObject : template.Ring ?? template.MainObject;
        var sourceSection = source == null ? null : session.WorkingSection(source);
        var sameArchetype = sourceSection != null &&
                            SceneSections.Is(BaseTemplate.Last(sourceSection, "archetype") ?? "", archetype);
        string[] copiedLines = type == "planet" ? ["reputation"] : BaseTemplate.StationLines;
        entries.AddRange(TemplateLines(sourceSection, copiedLines, objectId, sameArchetype, faction, texts, binary));
        commands.Add(EditCommand.AddSection(path, SceneSections.ObjectType, entries));
        var added = new SectionRef(path, objectId);
        if (type != "planet")
            return (new DockPlan(commands, [added], [], added, null, true), null);

        var newRing = Ring(session, spec, template, path, objectName, position, PlanetRadius(session, archetype), nickname,
            faction, texts);
        if (newRing.Refusal != null)
            return (null, newRing.Refusal);
        commands.AddRange(newRing.Commands);
        return (new DockPlan(commands, newRing.Added is { } ring2 ? [added, ring2] : [added], [], added, newRing.Added,
            newRing.Added != null), null);
    }

    /// <summary>
    /// The new docking ring of a planet base, or no ring when the request and the template have none. The ring takes the
    /// archetype, the place and the rotation of the request, else of the ring of the template. The place of the
    /// template ring keeps its direction from the planet and its height above the surface.
    /// </summary>
    private static (List<EditCommand> Commands, SectionRef? Added, Refusal? Refusal) Ring(EditSession session,
        BaseSpec spec, BaseTemplate template, string path, string planet, Vector3 planetPosition, float? planetRadius,
        string nickname, string? faction, SectionCopies texts)
    {
        var request = spec.Dock!.Ring;
        if (request == null && template.Ring == null)
            return ([], null, null);
        var ringSection = template.Ring is { } templateRing ? session.WorkingSection(templateRing) : null;
        var archetype = request?.Archetype?.Trim() is { Length: > 0 } a ? a
            : ringSection == null ? null : BaseTemplate.Last(ringSection, "archetype");
        if (archetype == null)
            return ([], null, new Refusal(path, $"Give the archetype of the docking ring. The template {template.Nickname} " +
                                                "has no docking ring.", DockArchetype, "dock_object.ring.archetype"));
        if (DockArchetypeProblem(session, archetype) is { } problem)
            return ([], null, new Refusal(path, problem, DockArchetype, "dock_object.ring.archetype"));
        if ((request?.Position ?? TemplatePlace(session, template, planetPosition, planetRadius)) is not { } position)
            return ([], null, new Refusal(path, "Give the position of the docking ring.", DockPlace,
                "dock_object.ring.position"));
        var stem = $"dock_ring_{planet}";
        var name = session.Nicknames.Taken(stem) == null ? stem : session.Nicknames.FirstFree(stem, 1);
        var binary = session.IsBinaryFile(path);
        if (ScenePlans.NewObject(session, path, stem, position, archetype, null, nickname, null, name, out var error)
            is not { } made)
            return ([], null, new Refusal(path, error!, null, "dock_object.ring"));
        var ringId = new SectionIdentity(SceneSections.ObjectType, made.Nickname);
        // A retail ring has a name of all rings, for example "Docking Ring", not the name of the base.
        var ringText = ringSection?.LastOrDefault(e => SceneSections.Is(e.Name, "ids_name") && e.Count > 0) is { } line
            ? SectionCopies.Stored(texts.Copy(line, 0, ringId), binary)
            : null;
        var rotate = request?.Rotation is null
            ? ringSection?.LastOrDefault(e => SceneSections.Is(e.Name, "rotate"))
            : null;
        var entries = WithLines(made.Entries, ringText, request?.Rotation, rotate, null);
        var sameArchetype = ringSection != null &&
                            SceneSections.Is(BaseTemplate.Last(ringSection, "archetype") ?? "", archetype);
        var copied = ringSection?.Select(e => e.Name).Where(n => !RingSkipped.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        entries.AddRange(TemplateLines(ringSection, copied, ringId, sameArchetype, faction, texts, binary));
        return ([EditCommand.AddSection(path, SceneSections.ObjectType, entries)], new SectionRef(path, ringId), null);
    }

    /// <summary>
    /// The place of a new ring from the ring of the template. The new ring keeps the direction from the planet and the
    /// height above the surface of the template ring. Without the radius of a planet, it keeps the distance of the
    /// template. Null when the template has no ring, or the ring or its planet has no position.
    /// </summary>
    private static Vector3? TemplatePlace(EditSession session, BaseTemplate template, Vector3 planet, float? radius)
    {
        if (template.Ring is not { } ringRef || template.MainObject is not { } mainRef ||
            session.Get(ringRef) is not { } ring || session.Get(mainRef) is not { } main ||
            SceneSections.Position(ring) is not { } ringAt || SceneSections.Position(main) is not { } mainAt)
            return null;
        var offset = ringAt - mainAt;
        var distance = offset.Length();
        if (distance < 1)
            return planet + offset;
        var height = SceneSections.RadiusOf(session, main) is { } templateRadius && radius is { } newRadius
            ? newRadius + MathF.Max(0, distance - templateRadius)
            : distance;
        return planet + offset / distance * height;
    }

    /// <summary>The <c>solar_radius</c> of an archetype of the list of the solar archetypes, or null.</summary>
    private static float? PlanetRadius(EditSession session, string archetype) =>
        session.Lists.List("solar_archetype")?.Find(archetype)?.Section is { } solar && session.Get(solar) is { } view
            ? SceneSections.SolarRadius(view)
            : null;

    /// <summary>
    /// Why an archetype cannot dock a ship, or null: an archetype of the station template of the rule data with a
    /// docking sphere can.
    /// </summary>
    private static string? DockArchetypeProblem(EditSession session, string archetype)
    {
        if (ScenePlans.TypeOf(session, archetype) is not { } type)
            return $"No solar archetype has the name {archetype}. Get the archetypes from list_values with the list " +
                   "solar_archetype.";
        if (ScenePlans.TemplateFor(session, archetype) != SceneTemplate.Station)
            return $"The archetype {archetype} is a {type.ToUpperInvariant()}. A dock object needs a station archetype " +
                   "with a docking sphere, or a planet.";
        return ScenePlans.AsksForBase(session, SceneTemplate.Station, archetype)
            ? null
            : $"The archetype {archetype} has no docking sphere, so the player cannot dock with it. Pick a station " +
              "archetype with a docking sphere.";
    }

    /// <summary>The refusal of a nickname of a new dock object that the request gives, or no refusal.</summary>
    private static (string? Name, Refusal? Refusal) ObjectName(EditSession session, string? nickname, bool binary,
        string path)
    {
        if (nickname?.Trim() is not { Length: > 0 } given)
            return (null, null);
        if (PatchFormatter.KeyError(given, binary) is { } keyError)
            return (null, new Refusal(path, keyError, MissionErrors.NameInvalid, "dock_object.nickname"));
        return session.Nicknames.Taken(given) is { } taken
            ? (null, new Refusal(path, $"The nickname {given} is taken by {taken.Section.Id.Key} in " +
                                       $"{SceneSections.FileName(taken.Section.Path)}.", MissionErrors.NameTaken,
                "dock_object.nickname", taken.Section))
            : (given, null);
    }

    /// <summary>The lines of a new object in the retail order. The name follows the nickname, the rotation follows the
    /// position, and the infocard follows the archetype.</summary>
    /// <param name="rotation">The rotation of the request, or null.</param>
    /// <param name="rotate">The <c>rotate</c> line of the template when the request gives no rotation, or null.</param>
    private static List<EntryView> WithLines(List<EntryView> entries, ValueBase? name, Vector3? rotation, Entry? rotate,
        ValueBase? info)
    {
        var result = new List<EntryView>();
        foreach (var entry in entries)
        {
            result.Add(entry);
            if (SceneSections.Is(entry.Name, "nickname") && name != null)
                result.Add(new EntryView("ids_name", [name]));
            else if (SceneSections.Is(entry.Name, "pos") && rotation is { } r)
                result.Add(new EntryView("rotate", Whole(r)));
            else if (SceneSections.Is(entry.Name, "pos") && rotate != null)
                result.Add(Line(rotate));
            else if (SceneSections.Is(entry.Name, "archetype") && info != null)
                result.Add(new EntryView("ids_info", [info]));
        }
        return result;
    }

    /// <summary>
    /// The lines of an object of the template that a new object copies, in the order of the names. A text of a mod gets
    /// a copy (see <see cref="SectionCopies.Copy(Entry, int, SectionIdentity)"/>). A <c>loadout</c> goes only to an
    /// object with the archetype of the template object. A <c>reputation</c> names the faction of the request when it
    /// gives one, also when the template object has no such line.
    /// </summary>
    private static IEnumerable<EntryView> TemplateLines(Section? source, IReadOnlyList<string> names, SectionIdentity copy,
        bool sameArchetype, string? faction, SectionCopies texts, bool binary)
    {
        foreach (var key in names)
        {
            if (SceneSections.Is(key, "reputation") && faction != null)
            {
                yield return new EntryView("reputation", [new StringValue(faction)]);
                continue;
            }
            if (SceneSections.Is(key, "loadout") && !sameArchetype)
                continue;
            foreach (var line in source?.Where(e => SceneSections.Is(e.Name, key)) ?? [])
                yield return CopiedLine(line, copy, texts, binary);
        }
        if (faction != null && !names.Contains("reputation", StringComparer.OrdinalIgnoreCase))
            yield return new EntryView("reputation", [new StringValue(faction)]);
    }

    /// <summary>The three values of a rotation in whole degrees.</summary>
    private static IReadOnlyList<ValueBase> Whole(Vector3 rotation) =>
        [new Int32Value((int)MathF.Round(rotation.X)), new Int32Value((int)MathF.Round(rotation.Y)),
            new Int32Value((int)MathF.Round(rotation.Z))];
}
