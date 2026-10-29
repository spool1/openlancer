using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Studio.Agent;

/// <summary>The kind of an item of a system file (tool <c>list_items</c>).</summary>
internal enum SceneItemKind
{
    Object,
    Zone,
    Light,
    Nebula,
    Field,
}

/// <summary>The kind of an object of a system file (tool <c>list_items</c>). A wreck is an object whose archetype has
/// the type <c>MISSION_SATELLITE</c>.</summary>
internal enum SceneObjectKind
{
    Station,
    Planet,
    Sun,
    Satellite,
    Wreck,
    Gate,
    Hole,
    LaneRing,
    Other,
}

/// <summary>A star system of the edit view (tool <c>list_systems</c>).</summary>
/// <param name="Ref">The <c>system:</c> reference.</param>
/// <param name="Name">The name that the game shows, or an empty text.</param>
/// <param name="File">The install path of the system file, or null when the <c>[system]</c> names no file.</param>
internal sealed record SystemRow(string Ref, string Nickname, string Name, string? File, ItemOrigin Origin);

/// <summary>A jump gate, a jump hole or a trade lane ring of a system, with the references of its partners.</summary>
/// <param name="Kind"><see cref="SceneObjectKind.Gate"/>, <see cref="SceneObjectKind.Hole"/> or
/// <see cref="SceneObjectKind.LaneRing"/>.</param>
/// <param name="ToSystem">The <c>system:</c> reference of the partner of a gate or a hole, or null.</param>
/// <param name="ToObject">The <c>obj:</c> reference of the partner of a gate or a hole, or null.</param>
/// <param name="Previous">The <c>obj:</c> reference of the previous ring of a trade lane ring, or null.</param>
/// <param name="Next">The <c>obj:</c> reference of the next ring of a trade lane ring, or null.</param>
internal sealed record ConnectionRow(string Ref, SceneObjectKind Kind, string? ToSystem, string? ToObject, string? Previous,
    string? Next);

/// <summary>A star system with its counts, bases, connections and fields (tool <c>get_system</c>).</summary>
internal sealed record SystemView(string Ref, string Name, string? File)
{
    /// <summary>The fields of the <c>[system]</c> section of <c>universe.ini</c>, as <c>get_item</c> gives them.</summary>
    public IReadOnlyList<FieldRow> Fields { get; init; } = [];

    /// <summary>The number of items of each kind, by the names of <see cref="SceneItemKind"/>.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();

    /// <summary>The number of objects of each object kind, by the names of <see cref="SceneObjectKind"/>.</summary>
    public IReadOnlyDictionary<string, int> ObjectKinds { get; init; } = new Dictionary<string, int>();

    /// <summary>The <c>base:</c> references of the bases that objects of the system name.</summary>
    public IReadOnlyList<string> Bases { get; init; } = [];

    public IReadOnlyList<ConnectionRow> Connections { get; init; } = [];

    /// <summary>The <c>nebula:</c> references.</summary>
    public IReadOnlyList<string> Nebulae { get; init; } = [];

    /// <summary>The <c>field:</c> references of the asteroid fields.</summary>
    public IReadOnlyList<string> AsteroidFields { get; init; } = [];

    public IReadOnlyList<Problem> Problems { get; init; } = [];
}

/// <summary>An item of a system (tool <c>list_items</c>).</summary>
/// <param name="Ref">The canonical reference, for example <c>obj:</c> or <c>zone:</c>.</param>
/// <param name="Nickname">The nickname, or the zone of a nebula or an asteroid field.</param>
internal sealed record SceneItemRow(string Ref, string? Nickname, string Name, SceneItemKind Kind)
{
    /// <summary>The object kind of an object, else null.</summary>
    public SceneObjectKind? ObjectKind { get; init; }

    public string? Archetype { get; init; }

    /// <summary>The position in meters, as the <c>pos</c> line gives it.</summary>
    public float[]? Pos { get; init; }

    /// <summary>The rotation in degrees about X, Y and Z, as the <c>rotate</c> line gives it.</summary>
    public float[]? Rotate { get; init; }

    /// <summary>The size of a zone, as the <c>size</c> line gives it.</summary>
    public float[]? Size { get; init; }

    public IReadOnlyList<ObjectComponent> Components { get; init; } = [];

    public required ItemOrigin Origin { get; init; }

    /// <summary>True when an agent entry changed the item after the last save (see
    /// <see cref="EditSession.AgentSections"/>). The Hierarchy of the editor marks the same rows.</summary>
    public bool AgentChanged { get; init; }
}

/// <summary>The query tools of the star systems and their items (section 7.1 of the MCP1 design).</summary>
internal static class SceneQueries
{
    public static ToolStep ListSystems(ToolCall call)
    {
        var session = call.Session;
        var text = call.Args.Text("text");
        var entries = session.Lists.List("system")?.Entries.Where(e => e.Section != null).ToList() ?? [];
        var rows = entries.Select(e => (Entry: e, Name: call.Texts.TextOf(e.Section!, "strid_name")))
            .Where(r => ToolCall.Matches(text, r.Entry.Value, r.Name))
            .ToList();
        return ToolResult.Of(call.Paged(rows, r => new SystemRow(SystemRef(r.Entry.Value), r.Entry.Value, r.Name,
            session.SystemPath(r.Entry.Value),
            new ItemOrigin(r.Entry.Origin, r.Entry.ModId, session.IsUnsaved(r.Entry.Section!)))));
    }

    public static ToolStep GetSystem(ToolCall call)
    {
        var session = call.Session;
        var (_, section, path) = System(session, call.Args.Text("system")!);
        var view = session.Get(section)!;
        var items = path == null ? [] : Items(session, path).ToList();
        var kinds = new ObjectKinds(session);
        var objects = items.Where(i => i.Kind == SceneKind.Object)
            .Select(i => (i.Section, View: session.Get(i.Section)!))
            .Select(o => (o.Section, o.View, Kind: kinds.Of(o.View)))
            .ToList();
        string Ref(SectionRef s) => ItemRefs.Canonical(session, s);
        return ToolResult.Of(new SystemView(Ref(section), call.Texts.TextOf(section, "strid_name"), path)
        {
            Fields = ItemQueries.Fields(session, section, view, AgentRows.DiagnosticsOf(session, section)),
            Counts = Enum.GetValues<SceneItemKind>().ToDictionary(Name, k => items.Count(i => KindOf(i.Kind) == k)),
            ObjectKinds = objects.GroupBy(o => o.Kind).ToDictionary(g => Name(g.Key), g => g.Count()),
            Bases = objects.Select(o => SceneSections.Last(o.View, "base")).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(b => ItemRefs.Format(new ItemReference(ItemRefKind.Base, b))).ToList(),
            Connections = objects.Where(o => o.Kind is SceneObjectKind.Gate or SceneObjectKind.Hole or SceneObjectKind.LaneRing)
                .Select(o => Connection(session, o.Section, o.View, o.Kind)).ToList(),
            Nebulae = items.Where(i => i.Kind == SceneKind.Nebula).Select(i => Ref(i.Section)).ToList(),
            AsteroidFields = items.Where(i => i.Kind == SceneKind.Asteroids).Select(i => Ref(i.Section)).ToList(),
            Problems = AgentRows.ProblemsOf(session, section),
        });
    }

    public static ToolStep ListItems(ToolCall call)
    {
        var session = call.Session;
        var args = call.Args;
        var (_, _, path) = System(session, args.Text("system")!);
        var kinds = args.Texts("kinds");
        var objectKinds = args.Texts("object_kinds");
        var text = args.Text("text");
        var origin = args.Text("origin");
        var near = args.Group("near");
        var point = near?.Point("point");
        var radius = near?.Number("radius");
        var kindsOf = new ObjectKinds(session);
        // A filter reads only what it needs, and the rows of the page read the rest. So a list of a large system
        // stays in one frame of the editor.
        var rows = new List<(SectionRef Section, SceneKind Kind)>();
        foreach (var (section, kind) in path == null ? [] : Items(session, path))
        {
            if (kinds != null && !kinds.Contains(Name(KindOf(kind))))
                continue;
            SectionView? view = null;
            SectionView View() => view ??= session.Get(section)!;
            if (objectKinds != null && (kind != SceneKind.Object || !objectKinds.Contains(Name(kindsOf.Of(View())))))
                continue;
            if (text != null && !ToolCall.Matches(text, section.Id.Key,
                    call.Texts.NameOf(Named(session, section, kind) ?? section), SceneSections.Last(View(), "archetype")))
                continue;
            if (point is { } p && (AgentRows.Numbers(View(), "pos") is not [var x, var y, var z] ||
                                   Vector3.Distance(p, new Vector3(x, y, z)) > radius))
                continue;
            if (origin != null && Name(Origin(session, section, View(), kind).Layer) != origin)
                continue;
            rows.Add((section, kind));
        }
        return ToolResult.Of(call.Paged(rows, r =>
        {
            var view = session.Get(r.Section)!;
            var isObject = r.Kind == SceneKind.Object;
            return new SceneItemRow(ItemRefs.Canonical(session, r.Section), r.Section.Id.Key,
                call.Texts.NameOf(Named(session, r.Section, r.Kind) ?? r.Section), KindOf(r.Kind))
            {
                ObjectKind = isObject ? kindsOf.Of(view) : null,
                Archetype = isObject ? SceneSections.Last(view, "archetype") : null,
                Pos = AgentRows.Numbers(view, "pos"),
                Rotate = AgentRows.Numbers(view, "rotate"),
                Size = r.Kind == SceneKind.Zone ? AgentRows.Numbers(view, "size") : null,
                Components = isObject ? ObjectComponents.Of(session, r.Section).Select(c => c.Kind).ToList() : [],
                Origin = Origin(session, r.Section, view, r.Kind),
                AgentChanged = session.AgentSections.Contains(r.Section),
            };
        }));
    }

    /// <summary>
    /// The <c>[system]</c> of the argument <c>system</c>, with its file. The argument is a nickname or a
    /// <c>system:</c> reference, as the rows of <c>list_systems</c> give it.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/> with the closest names, or
    /// <see cref="EditErrorKind.InvalidArgument"/> for a reference of another kind.</exception>
    internal static (string Nickname, SectionRef Section, string? Path) System(EditSession session, string text)
    {
        // No nickname holds a ':' (section 5.6 of the MCP1 design), so a text with one is a reference.
        var reference = text.Contains(':') ? ItemRefs.Parse(text) : new ItemReference(ItemRefKind.System, text);
        if (reference.Kind != ItemRefKind.System)
            throw ToolInput.Bad("system", AgentTexts.Error("system_ref", ("ref", text)));
        var section = ItemRefs.ResolveSection(session, reference);
        var key = section.Id.Key ?? reference.Name;
        return (key, section, session.SystemPath(key));
    }

    /// <summary>The items of a system file in file order, with their kinds (see <see cref="SceneSections.KindOf"/>).</summary>
    internal static IEnumerable<(SectionRef Section, SceneKind Kind)> Items(EditSession session, string path)
    {
        if (!session.IsSystemFile(path))
            yield break;
        foreach (var section in session.Sections(path))
        {
            SceneKind? kind = section.Id.Type.ToLowerInvariant() switch
            {
                "object" => SceneKind.Object,
                "zone" => SceneKind.Zone,
                "lightsource" => SceneKind.Light,
                "nebula" => SceneKind.Nebula,
                "asteroids" => SceneKind.Asteroids,
                _ => null,
            };
            if (kind is { } found)
                yield return (section, found);
        }
    }

    /// <summary>The kind of an item for the results.</summary>
    private static SceneItemKind KindOf(SceneKind kind) => kind switch
    {
        SceneKind.Object => SceneItemKind.Object,
        SceneKind.Zone => SceneItemKind.Zone,
        SceneKind.Light => SceneItemKind.Light,
        SceneKind.Nebula => SceneItemKind.Nebula,
        _ => SceneItemKind.Field,
    };

    /// <summary>The JSON name of an enum value, as the arguments take it.</summary>
    private static string Name<T>(T value) where T : struct, Enum => AgentTools.Snake(value.ToString());

    /// <summary>The <c>system:</c> reference of a system.</summary>
    internal static string SystemRef(string nickname) => ItemRefs.Format(new ItemReference(ItemRefKind.System, nickname));

    private static string ObjectRef(string nickname) => ItemRefs.Format(new ItemReference(ItemRefKind.Object, nickname));

    /// <summary>
    /// The object kinds of the objects of one call. An object of a connection type, or with a <c>goto</c>, a
    /// <c>prev_ring</c> or a <c>next_ring</c> line, is a connection (decision D253). Else the kind follows the template
    /// of the archetype type. A <c>mission_satellite</c> is a wreck: retail gives wrecks this type (section 7.3 of the
    /// MCP1 design). The archetype types come from the list once for each archetype of the call.
    /// </summary>
    internal sealed class ObjectKinds(EditSession session)
    {
        private readonly Dictionary<string, string?> types = new(StringComparer.OrdinalIgnoreCase);
        private readonly ListView? archetypes = session.Lists.List("solar_archetype");

        public SceneObjectKind Of(SectionView view)
        {
            var type = TypeOf(view);
            if (type != null && session.Lists.RuleData.ConnectionTypes.Contains(type))
            {
                return type switch
                {
                    "jump_gate" or "airlock_gate" => SceneObjectKind.Gate,
                    "jump_hole" or "jumphole" => SceneObjectKind.Hole,
                    _ => SceneObjectKind.LaneRing,
                };
            }
            if (view.Lines("goto").Count > 0)
                return SceneObjectKind.Gate;
            if (view.Lines("prev_ring").Count > 0 || view.Lines("next_ring").Count > 0)
                return SceneObjectKind.LaneRing;
            return type switch
            {
                "station" or "docking_ring" => SceneObjectKind.Station,
                "planet" => SceneObjectKind.Planet,
                "sun" => SceneObjectKind.Sun,
                "satellite" or "waypoint" => SceneObjectKind.Satellite,
                "mission_satellite" => SceneObjectKind.Wreck,
                _ => SceneObjectKind.Other,
            };
        }

        /// <summary>The <c>[Solar] type</c> of the archetype of an object in lowercase, or null.</summary>
        private string? TypeOf(SectionView view)
        {
            if (SceneSections.Last(view, "archetype") is not { } archetype)
                return null;
            if (!types.TryGetValue(archetype, out var type))
            {
                type = archetypes?.Find(archetype)?.Section is { } solar && session.Get(solar) is { } section
                    ? SceneSections.Last(section, "type")?.ToLowerInvariant()
                    : null;
                types[archetype] = type;
            }
            return type;
        }
    }

    /// <summary>The section that gives the name of an item: the zone of a nebula or a field, else the item.</summary>
    private static SectionRef? Named(EditSession session, SectionRef section, SceneKind kind) =>
        kind is SceneKind.Nebula or SceneKind.Asteroids && section.Id.Key is { } zone
            ? session.Nicknames.All.FirstOrDefault(n => n.IsZone && n.Name.Equals(zone, StringComparison.OrdinalIgnoreCase))?.Section
            : null;

    private static ItemOrigin Origin(EditSession session, SectionRef section, SectionView view, SceneKind kind) =>
        AgentRows.OriginOf(session, section, view, kind is SceneKind.Nebula or SceneKind.Asteroids ? "zone" : "nickname");

    /// <summary>A connection with the references of its partners: the <c>goto</c> line of a gate or a hole names the
    /// system and the object, and the ring lines of a trade lane ring name the rings.</summary>
    private static ConnectionRow Connection(EditSession session, SectionRef section, SectionView view, SceneObjectKind kind)
    {
        var go = view.Lines("goto").LastOrDefault();
        string? Value(int index) =>
            go is { } line && line.Values.Count > index && line.Values[index].ToString().Trim() is { Length: > 0 } text
                ? text
                : null;
        return new ConnectionRow(ItemRefs.Canonical(session, section), kind,
            Value(0) is { } system ? SystemRef(system) : null,
            Value(1) is { } partner ? ObjectRef(partner) : null,
            SceneSections.Last(view, "prev_ring") is { } previous ? ObjectRef(previous) : null,
            SceneSections.Last(view, "next_ring") is { } next ? ObjectRef(next) : null);
    }
}
