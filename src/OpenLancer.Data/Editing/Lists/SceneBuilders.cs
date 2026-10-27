using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>A line of a template of a builder: a key and its value. A line without a value is a line that the builder
/// fills, for example <c>path_label</c> of a route zone.</summary>
internal sealed record BuilderLine(string Key, string? Value);

/// <summary>
/// The rules of a new wreck of the agent tool <c>scene_place_wreck</c> (decision D730 of the MCP1 design): the
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
/// The rule data of the builders of the agent tools (milestone MCP1-2): the wreck and the route templates of
/// <c>list-rules.json</c>. Section 9.3 of the MCP1 design names that file as the source of the builders.
/// </summary>
internal sealed record SceneBuilders(WreckRule? Wreck, IReadOnlyList<RouteTemplate> Routes)
{
    /// <summary>The key of the label and the leg number of a route zone.</summary>
    public const string PathLabel = "path_label";

    /// <summary>The key of the usage of a route zone.</summary>
    public const string Usage = "usage";

    /// <summary>The rule data without builders.</summary>
    public static readonly SceneBuilders None = new(null, []);

    /// <summary>The keys that a builder writes itself. A template cannot give them.</summary>
    private static readonly string[] BuilderKeys =
    [
        "nickname", "pos", "rotate", "shape", "size", "archetype", "loadout", "encounter", "faction", "faction_weight",
        "density_restriction", "ids_name", "ids_info",
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
            if (wreck.Types.FirstOrDefault(connectionTypes.Contains) is { } connection)
                throw new FormatException($"The wreck builder has the connection type '{connection}'.");
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
        return new SceneBuilders(wreck, routes);
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

    private static string Text(JsonElement e, string name) =>
        e.GetProperty(name).GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"The builder rule data needs a text '{name}'.");

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : [];

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
