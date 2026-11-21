using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>A line of a template of a builder: a key and its value. A line without a value is a line that the builder
/// fills, for example <c>path_label</c> of a route zone.</summary>
internal sealed record BuilderLine(string Key, string? Value);

/// <summary>
/// The rules of a new wreck of the agent tool <c>scene_place_wreck</c> (decision D730 of the MCP1 design). They give the
/// <c>[Solar]</c> types of its archetype, the lines of its object, and the texts of its name and its infocard.
/// </summary>
/// <param name="Help">What a wreck is, for the agent.</param>
/// <param name="Types">The <c>[Solar] type</c> values of the archetypes of a wreck, in lowercase.</param>
/// <param name="Lines">The lines of the object after its archetype and its loadout, each with a value.</param>
/// <param name="Name">The name of a wreck when the request gives none.</param>
/// <param name="Info">The plain text of the infocard of a wreck.</param>
internal sealed record WreckRule(string Help, IReadOnlyList<string> Types, IReadOnlyList<BuilderLine> Lines, string Name,
    string Info);

/// <summary>
/// The template of the zones of a route of the agent tool <c>scene_place_patrol_route</c> for one usage (decision D731 of
/// the MCP1 design). Each zone gets the lines of the template in their order, after its position, rotation, shape and
/// size, and before its encounter lines.
/// </summary>
/// <param name="Usage">The value of the <c>usage</c> line, for example <c>patrol</c>.</param>
/// <param name="Help">What the route does, for the agent.</param>
/// <param name="Radius">The radius of each zone when the request gives none, in meters.</param>
/// <param name="Lines">The lines in their order. The lines <c>path_label</c> and <c>usage</c> have no value, because the
/// builder fills them.</param>
internal sealed record RouteTemplate(string Usage, string Help, float Radius, IReadOnlyList<BuilderLine> Lines);

/// <summary>
/// The rules of the death zone of the sun of a new star system, when the sun that the new system copies has none. The
/// zone is a <c>SPHERE</c> at the sun. Its lines are <c>nickname</c>, <c>pos</c>, <c>shape</c> and <c>size</c>, then
/// the lines of the rule in their order.
/// </summary>
/// <param name="Size">The radius of the zone in meters, when the sun has no <c>atmosphere_range</c>.</param>
/// <param name="AtmosphereMargin">The radius of the zone is the <c>atmosphere_range</c> of the sun less this value, in
/// meters.</param>
/// <param name="Lines">The lines after <c>size</c>, each with a value, for example <c>damage</c>.</param>
internal sealed record SunZoneRule(float Size, float AtmosphereMargin, IReadOnlyList<BuilderLine> Lines)
{
    /// <summary>The radius of the zone of a sun, from its <c>atmosphere_range</c> or null.</summary>
    public float RadiusFor(float? atmosphereRange) =>
        atmosphereRange is { } range && range - AtmosphereMargin >= 1 ? range - AtmosphereMargin : Size;
}

/// <summary>
/// The rules of the new ends of a jump connection of one kind. They give the archetype, the infocard, the name and the
/// lines of each new end, and the traffic zone at the end.
/// </summary>
/// <param name="Archetype">The solar archetype of a new end when the request and its template give none.</param>
/// <param name="Infocard">The id of the infocard that the retail ends of this kind share.</param>
/// <param name="Name">The pattern of the name of a new end. <c>{system}</c> is the name of the system at the other
/// end.</param>
/// <param name="Lines">The lines of a new end that the template object does not give, each with a value. Only the keys
/// of <see cref="ConnectionRule.EndKeys"/> can occur.</param>
/// <param name="TrafficSize">The radius of the traffic zone at a new end, in meters.</param>
/// <param name="Traffic">The lines of the traffic zone after its <c>size</c> line, each with a value.</param>
internal sealed record JumpEndRule(string Archetype, int Infocard, string Name, IReadOnlyList<BuilderLine> Lines,
    float TrafficSize, IReadOnlyList<BuilderLine> Traffic);

/// <summary>The loadout and the pilot of a new jump gate or a new trade lane ring of a house, when no template object
/// gives them.</summary>
/// <param name="House">The start of the faction nickname before the first <c>_</c>, for example <c>li</c>.</param>
internal sealed record HouseLoadout(string House, string Loadout, string Pilot)
{
    /// <summary>The loadout of the house of a faction nickname, or null.</summary>
    public static HouseLoadout? Of(IReadOnlyList<HouseLoadout> houses, string? faction) =>
        faction?.Split('_')[0] is { Length: > 0 } house
            ? houses.FirstOrDefault(h => h.House.Equals(house, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>Reads the <c>houses</c> list of a builder. A house occurs once.</summary>
    /// <param name="owner">The builder, for a message.</param>
    /// <exception cref="FormatException">The data is not valid.</exception>
    public static IReadOnlyList<HouseLoadout> Read(JsonElement e, string owner)
    {
        var houses = e.TryGetProperty("houses", out var array)
            ? array.EnumerateArray().Select(h => new HouseLoadout(SceneBuilders.Text(h, "house"),
                SceneBuilders.Text(h, "loadout"), SceneBuilders.Text(h, "pilot"))).ToList()
            : [];
        if (houses.GroupBy(h => h.House, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new FormatException($"The {owner} has the house '{twice.Key}' more than one time.");
        return houses;
    }
}

/// <summary>
/// The rules of a new jump connection. They give the tunnel of the <c>goto</c> line and the smallest distance of a new
/// end from the center of its system. They also give the rules of each kind, and the loadout and the pilot of a gate
/// for each house.
/// </summary>
/// <param name="Tunnel">Value 2 of the <c>goto</c> line of a new end.</param>
/// <param name="MinRadius">The smallest distance of a new end from the center of its system, in meters.</param>
internal sealed record ConnectionRule(string Tunnel, float MinRadius, JumpEndRule Gate, JumpEndRule Hole,
    IReadOnlyList<HouseLoadout> Houses)
{
    /// <summary>The keys of the lines of a new end that the rule data or a template object can give. The builder
    /// writes each other line itself.</summary>
    public static readonly IReadOnlyList<string> EndKeys = ["jump_effect", "behavior", "difficulty_level", "visit"];

    /// <summary>The rules of the ends of a kind: a jump hole, or else a jump gate.</summary>
    public JumpEndRule For(ConnectionKind kind) => kind == ConnectionKind.Hole ? Hole : Gate;

    /// <summary>The smallest distance between two jump objects of one system that a new end keeps: two traffic zones
    /// of a new end, so that the zones of two ends do not overlap. 8000 m with the retail values.</summary>
    public float Spacing => 2 * Math.Max(Gate.TrafficSize, Hole.TrafficSize);

    /// <summary>The house of a faction nickname, or null.</summary>
    public HouseLoadout? HouseOf(string? faction) => HouseLoadout.Of(Houses, faction);
}

/// <summary>
/// The rules of a new trade lane. They give the archetype, the generic name and infocard and the lines of each ring.
/// They also give the spacing of the rings, the clearance of an end at an object, the lane zone, and the loadout and
/// the pilot of a ring for each house.
/// </summary>
/// <param name="Archetype">The solar archetype of a new ring when the request and its template give none.</param>
/// <param name="Name">The id of the name that the rings of a lane share in the target view.</param>
/// <param name="Infocard">The id of the infocard that the rings share.</param>
/// <param name="Spacing">The distance between two rings that gives the ring count, in meters.</param>
/// <param name="RetailMin">The smallest distance between two rings of a retail lane, in meters. A new lane with less
/// gets a warning.</param>
/// <param name="RetailMax">The largest distance between two rings of a retail lane, in meters. A new lane with more
/// gets a warning.</param>
/// <param name="Clearance">The distance of an end ring beyond the <c>solar_radius</c> of the object at that end, and
/// beyond the edge of a zone with damage, in meters.</param>
/// <param name="Lines">The lines of a new ring that the template ring does not give, each with a value. Only the keys
/// of <see cref="RingKeys"/> can occur.</param>
/// <param name="ZoneMargin">The lane zone is longer than the lane by this value, in meters.</param>
/// <param name="ZoneWidth">The largest width and height of the lane zone, in meters.</param>
/// <param name="Zone">The lines of the lane zone after its <c>lane_id</c> line, each with a value.</param>
internal sealed record LaneRule(string Archetype, int Name, int Infocard, float Spacing, float RetailMin, float RetailMax,
    float Clearance, IReadOnlyList<BuilderLine> Lines, float ZoneMargin, float ZoneWidth, IReadOnlyList<BuilderLine> Zone,
    IReadOnlyList<HouseLoadout> Houses)
{
    /// <summary>The keys of the lines of a new ring that the rule data or a template ring can give. The builder writes
    /// each other line itself.</summary>
    public static readonly IReadOnlyList<string> RingKeys = ["behavior", "difficulty_level"];

    /// <summary>The house of a faction nickname, or null.</summary>
    public HouseLoadout? HouseOf(string? faction) => HouseLoadout.Of(Houses, faction);
}

/// <summary>
/// The rule data of the builders of the agent tools in <c>list-rules.json</c> (milestone MCP1-2). It gives the wreck,
/// the route templates, the death zone of the sun of a new system, the new jump connections and the new trade lanes.
/// Section 9.3 of the MCP1 design names that file as the source of the builders.
/// </summary>
internal sealed record SceneBuilders(WreckRule? Wreck, IReadOnlyList<RouteTemplate> Routes, SunZoneRule? SunZone,
    ConnectionRule? Connection, LaneRule? Lane)
{
    /// <summary>The key of the label and the leg number of a route zone.</summary>
    public const string PathLabel = "path_label";

    /// <summary>The key of the usage of a route zone.</summary>
    public const string Usage = "usage";

    /// <summary>The rule data without builders.</summary>
    public static readonly SceneBuilders None = new(null, [], null, null, null);

    /// <summary>The keys that a builder writes itself. A template cannot give them.</summary>
    private static readonly string[] BuilderKeys =
    [
        "nickname", "pos", "rotate", "shape", "size", "archetype", "loadout", "encounter", "faction", "faction_weight",
        "density_restriction", "ids_name", "ids_info", "prev_ring", "next_ring", "tradelane_space_name", "reputation",
        "pilot", "lane_id",
    ];

    /// <summary>The template of a usage, or null.</summary>
    public RouteTemplate? Route(string usage) =>
        Routes.FirstOrDefault(r => r.Usage.Equals(usage, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the <c>sceneBuilders</c> object of the rule data.</summary>
    /// <param name="connectionTypes">The connection types of the rule data. A wreck type cannot be one of them.</param>
    /// <exception cref="FormatException">The data is not valid.</exception>
    public static SceneBuilders Read(JsonElement e, IReadOnlyList<string> connectionTypes)
    {
        WreckRule? wreck = null;
        if (e.TryGetProperty("wreck", out var w))
        {
            wreck = new WreckRule(Text(w, "help"), Strings(w, "types").Select(t => t.ToLowerInvariant()).ToList(),
                Lines(w, "wreck"), Text(w, "name"), Text(w, "info"));
            if (wreck.Types.Count == 0)
                throw new FormatException("The wreck builder needs one or more types.");
            if (wreck.Types.FirstOrDefault(connectionTypes.Contains) is { } connectionType)
                throw new FormatException($"The wreck builder has the connection type '{connectionType}'.");
            if (wreck.Lines.FirstOrDefault(l => l.Value == null) is { } empty)
                throw new FormatException($"The line '{empty.Key}' of the wreck builder needs a value.");
        }
        var routes = new List<RouteTemplate>();
        if (e.TryGetProperty("routes", out var array))
        {
            foreach (var r in array.EnumerateArray())
            {
                var usage = Text(r, "usage");
                var radius = r.GetProperty("radius").GetSingle();
                if (!float.IsFinite(radius) || radius < 1)
                    throw new FormatException($"The route template {usage} needs a radius of 1 or more.");
                var route = new RouteTemplate(usage, Text(r, "help"), radius, Lines(r, $"route template {usage}"));
                if (routes.Any(o => o.Usage.Equals(usage, StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException($"Two route templates have the usage '{usage}'.");
                foreach (var key in new[] { PathLabel, Usage })
                {
                    if (route.Lines.FirstOrDefault(l => Is(l.Key, key)) is not { Value: null })
                        throw new FormatException($"The route template {usage} needs the line '{key}' without a value.");
                }
                if (route.Lines.FirstOrDefault(l => l.Value == null && !Is(l.Key, PathLabel) && !Is(l.Key, Usage)) is
                    { } empty)
                    throw new FormatException($"The line '{empty.Key}' of the route template {usage} needs a value.");
                routes.Add(route);
            }
        }
        SunZoneRule? sunZone = null;
        if (e.TryGetProperty("system", out var system))
        {
            var zone = system.GetProperty("sunDeathZone");
            var size = zone.GetProperty("size").GetSingle();
            var margin = zone.GetProperty("atmosphereMargin").GetSingle();
            if (!float.IsFinite(size) || size < 1 || !float.IsFinite(margin) || margin < 0)
                throw new FormatException("The sun death zone of the system builder needs a size of 1 or more and a " +
                                          "margin of 0 or more.");
            sunZone = new SunZoneRule(size, margin, Lines(zone, "sun death zone"));
            if (sunZone.Lines.FirstOrDefault(l => l.Value == null) is { } empty)
                throw new FormatException($"The line '{empty.Key}' of the sun death zone needs a value.");
        }
        var connection = e.TryGetProperty("connection", out var c) ? ReadConnection(c) : null;
        var lane = e.TryGetProperty("lane", out var l) ? ReadLane(l) : null;
        return new SceneBuilders(wreck, routes, sunZone, connection, lane);
    }

    /// <summary>Reads the rules of a new jump connection.</summary>
    private static ConnectionRule ReadConnection(JsonElement e)
    {
        var tunnel = Text(e, "tunnel");
        if (tunnel.Any(c => c is ',' or ';' || char.IsControl(c)))
            throw new FormatException("The tunnel of the connection builder is not one value.");
        var radius = e.GetProperty("minRadius").GetSingle();
        if (!float.IsFinite(radius) || radius < 1)
            throw new FormatException("The connection builder needs a minRadius of 1 or more.");
        return new ConnectionRule(tunnel, radius, ReadEnd(e.GetProperty("gate"), "jump gate"),
            ReadEnd(e.GetProperty("hole"), "jump hole"), HouseLoadout.Read(e, "connection builder"));
    }

    /// <summary>Reads the rules of a new trade lane.</summary>
    private static LaneRule ReadLane(JsonElement e)
    {
        var name = e.GetProperty("name").GetInt32();
        var infocard = e.GetProperty("infocard").GetInt32();
        if (name < 1 || infocard < 1)
            throw new FormatException("The lane builder needs a name and an infocard of 1 or more.");
        var spacing = e.GetProperty("spacing").GetSingle();
        var range = e.GetProperty("retailRange").EnumerateArray().Select(v => v.GetSingle()).ToList();
        if (range.Count != 2 || !float.IsFinite(spacing) || !float.IsFinite(range[1]) ||
            !(range[0] >= 1 && range[0] <= spacing && spacing <= range[1]))
            throw new FormatException("The lane builder needs a retailRange of two values of 1 or more, with the spacing " +
                                      "between them.");
        var clearance = e.GetProperty("clearance").GetSingle();
        if (!float.IsFinite(clearance) || clearance < 0)
            throw new FormatException("The lane builder needs a clearance of 0 or more.");
        var lines = Lines(e, "lane builder");
        if (lines.FirstOrDefault(l => l.Value == null || !LaneRule.RingKeys.Contains(l.Key, StringComparer.OrdinalIgnoreCase))
            is { } bad)
            throw new FormatException($"The line '{bad.Key}' of the lane builder needs a value and one of the keys " +
                                      $"{string.Join(", ", LaneRule.RingKeys)}.");
        var zone = e.GetProperty("zone");
        var margin = zone.GetProperty("margin").GetSingle();
        var width = zone.GetProperty("width").GetSingle();
        if (!float.IsFinite(margin) || margin < 0 || !float.IsFinite(width) || width < 1)
            throw new FormatException("The lane zone of the lane builder needs a margin of 0 or more and a width of 1 or " +
                                      "more.");
        var zoneLines = Lines(zone, "lane zone");
        if (zoneLines.FirstOrDefault(l => l.Value == null) is { } empty)
            throw new FormatException($"The line '{empty.Key}' of the lane zone needs a value.");
        return new LaneRule(Text(e, "archetype"), name, infocard, spacing, range[0], range[1], clearance, lines, margin,
            width, zoneLines, HouseLoadout.Read(e, "lane builder"));
    }

    /// <summary>Reads the rules of the ends of one kind of a new jump connection.</summary>
    private static JumpEndRule ReadEnd(JsonElement e, string kind)
    {
        var infocard = e.GetProperty("infocard").GetInt32();
        var traffic = e.GetProperty("traffic");
        var size = traffic.GetProperty("size").GetSingle();
        if (infocard < 1 || !float.IsFinite(size) || size < 1)
            throw new FormatException($"The {kind} builder needs an infocard of 1 or more and a traffic zone size of 1 or " +
                                      "more.");
        var name = Text(e, "name");
        if (!name.Contains("{system}", StringComparison.Ordinal))
            throw new FormatException($"The name of the {kind} builder needs the part {{system}}.");
        var lines = Lines(e, $"{kind} builder");
        if (lines.FirstOrDefault(l => l.Value == null || !ConnectionRule.EndKeys.Contains(l.Key, StringComparer.OrdinalIgnoreCase))
            is { } bad)
            throw new FormatException($"The line '{bad.Key}' of the {kind} builder needs a value and one of the keys " +
                                      $"{string.Join(", ", ConnectionRule.EndKeys)}.");
        var trafficLines = Lines(traffic, $"{kind} traffic zone");
        if (trafficLines.FirstOrDefault(l => l.Value == null) is { } empty)
            throw new FormatException($"The line '{empty.Key}' of the {kind} traffic zone needs a value.");
        return new JumpEndRule(Text(e, "archetype"), infocard, name, lines, size, trafficLines);
    }

    /// <summary>The lines of a template. Each key occurs once, and no key is a key that the builder writes
    /// itself.</summary>
    private static List<BuilderLine> Lines(JsonElement e, string owner)
    {
        var lines = e.TryGetProperty("lines", out var array)
            ? array.EnumerateArray().Select(l => new BuilderLine(Text(l, "key"),
                l.TryGetProperty("value", out var v) ? v.GetString() : null)).ToList()
            : [];
        foreach (var line in lines)
        {
            if (BuilderKeys.Contains(line.Key, StringComparer.OrdinalIgnoreCase))
                throw new FormatException($"The {owner} cannot give the line '{line.Key}'. The builder writes it.");
            if (line.Value is { Length: 0 } || line.Value?.Any(c => c is ',' or ';' || char.IsControl(c)) == true)
                throw new FormatException($"The line '{line.Key}' of the {owner} has a value that is not one value.");
        }
        if (lines.GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new FormatException($"The {owner} has the line '{twice.Key}' more than one time.");
        return lines;
    }

    internal static string Text(JsonElement e, string name) =>
        e.GetProperty(name).GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"The builder rule data needs a text '{name}'.");

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : [];

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
