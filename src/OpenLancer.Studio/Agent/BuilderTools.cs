using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The builder tools of section 7.3 of the MCP1 design (decisions D730, D731 and D732). They make a new loadout, a
/// wreck, a patrol or trade route, and the encounter groups of a zone. Each tool calls a builder command of the command layer
/// (see <see cref="LoadoutCommands"/>, <see cref="WreckCommands"/>, <see cref="PatrolCommands"/> and
/// <see cref="EncounterCommands"/>) in the shared edit call of <see cref="EditTools"/>. So each call is one Undo step.
/// </summary>
/// <remarks>Each value of a row that names other data must be an entry of its list. A value that its list does not have
/// gives <c>invalid_value</c>, and the error names each such row (section 7.3 of the MCP1 design).</remarks>
internal static class BuilderTools
{
    /// <summary>The detail code of a route with more legs than the limit.</summary>
    public const string RouteLegs = "RouteLegs";

    /// <summary>The largest size of each coordinate of a point of a builder, in meters. The Scene camera sees no farther.
    /// The bound also keeps the length and the angles of a leg finite.</summary>
    public const float MaxCoordinate = ShotCamera.MaxCoordinate;

    /// <summary>The keys that <c>scene_place_patrol_route</c> writes itself on each zone, each with the argument that
    /// gives it, or null. The argument <c>fields</c> cannot give them, because they make the zones a route.</summary>
    private static readonly Dictionary<string, string?> RouteKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nickname"] = null,
        ["shape"] = null,
        ["pos"] = "points",
        ["rotate"] = "points",
        ["size"] = "radius",
        [SceneBuilders.PathLabel] = "name",
        [SceneBuilders.Usage] = "usage",
        ["encounter"] = "encounters",
        ["faction"] = "encounters",
        ["faction_weight"] = "faction_weights",
        ["density_restriction"] = "density_restrictions",
    };

    /// <summary>The usages of the route templates of the rule data, in their order.</summary>
    public static IReadOnlyList<string> Usages { get; } = ListRules.Retail.Builders.Routes.Select(r => r.Usage).ToList();

    /// <summary>The members of a row of equipment: an <c>equip</c> line.</summary>
    public static readonly IReadOnlyList<ToolParam> EquipmentRow =
        [new TextParam("equipment", true, null, null), new TextParam("hardpoint", false, null, null)];

    /// <summary>The members of a row of cargo: a <c>cargo</c> line.</summary>
    public static readonly IReadOnlyList<ToolParam> CargoRow =
        [new TextParam("good", true, null, null), new WholeParam("count", 1, 1)];

    /// <summary>The members of a row of a faction and its weight.</summary>
    private static readonly IReadOnlyList<ToolParam> FactionRow =
        [new TextParam("faction", true, null, null), new NumberParam("weight", true, 0)];

    /// <summary>The members of an encounter group: an <c>encounter</c> line with its <c>faction</c> lines.</summary>
    public static readonly IReadOnlyList<ToolParam> EncounterRow =
    [
        new TextParam("encounter", true, null, null), new WholeParam("level", 1, required: true),
        new NumberParam("chance", true, 0, 1), new RowsParam("factions", true, FactionRow),
    ];

    /// <summary>The arguments of the encounter block of a zone: the encounter groups, the faction weights and the
    /// density restrictions.</summary>
    /// <param name="encountersRequired">True when the call must give the encounter groups.</param>
    public static IEnumerable<ToolParam> EncounterBlock(bool encountersRequired) =>
    [
        new RowsParam("encounters", encountersRequired, EncounterRow, 0),
        new RowsParam("faction_weights", false, FactionRow, 0),
        new RowsParam("density_restrictions", false,
            [new WholeParam("count", 0, required: true), new TextParam("class", true, null, null)], 0),
    ];

    /// <summary>Makes a new loadout with an archetype, equipment rows and cargo rows (section 7.3 of the MCP1
    /// design).</summary>
    public static ToolStep CreateLoadout(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var args = call.Args;
        scope.Run(LoadoutCommands.Create(scope.Session, args.Text("nickname")!, args.Text("archetype")!,
            Equipment(args), Cargo(args, "cargo")));
    });

    /// <summary>
    /// Places a wreck (decision D730 of the MCP1 design). The wreck is a new object of a wreck archetype with a new
    /// loadout of its ship. The same batch gives the object its name and its infocard, from the request or from the
    /// wreck rule of the rule data.
    /// </summary>
    public static ToolStep PlaceWreck(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var args = call.Args;
        var (system, _) = SceneEditTools.System(call);
        var plan = WreckCommands.Place(session, system, args.Point("position")!.Value, args.Point("rotation"),
            args.Text("archetype")!, args.Text("ship_archetype")!, Cargo(args, "loot"), Equipment(args),
            args.Text("nickname"));
        scope.Run(plan);
        var wreck = plan.SelectAfter!;
        var rule = session.Lists.RuleData.Builders.Wreck!;
        EditTools.At("name", () => scope.Run(FieldCommands.SetText(session, wreck,
            ItemEditTools.TextKey(session, wreck, "name"), args.Text("name") ?? rule.Name)));
        scope.Run(FieldCommands.SetText(session, wreck, ItemEditTools.TextKey(session, wreck, "info"), rule.Info));
    });

    /// <summary>
    /// Places a patrol or trade route: one cylinder zone for each leg (decision D731 of the MCP1 design). The tool
    /// refuses a route with more legs than the limit of the settings. The fields of the request go to each zone in the
    /// same batch. They cannot give a key that the tool writes itself (see <see cref="RouteKeys"/>).
    /// </summary>
    public static ToolStep PlaceRoute(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var args = call.Args;
        var fields = args.Node("fields") as JsonObject;
        foreach (var key in fields?.Select(f => f.Key) ?? [])
        {
            if (!RouteKeys.TryGetValue(key, out var argument))
                continue;
            throw ToolInput.Bad($"fields.{key}", argument == null
                ? AgentTexts.Error("route_key", ("key", key))
                : AgentTexts.Error("route_key_argument", ("key", key), ("argument", argument)));
        }
        var points = args.Points("points")!;
        var limit = call.Limits.RouteLegs;
        if (points.Count - 1 > limit)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, RouteLegs,
                AgentTexts.Error("limit_legs", ("count", points.Count - 1), ("max", limit))) { Field = "points" });
        }
        var (system, _) = SceneEditTools.System(call);
        var plan = PatrolCommands.Place(session, system, args.Text("name")!, args.Text("usage")!, points,
            (float?)args.Number("radius"), Encounters(args), Shares(args.Rows("faction_weights")) ?? [],
            Densities(args) ?? []);
        scope.Run(plan);
        if (fields == null)
            return;
        foreach (var zone in plan.Adds.Where(a => SceneSections.Is(a.Id.Type, SceneSections.ZoneType)))
            SceneEditTools.Fields(scope, zone, fields);
    });

    /// <summary>Sets the encounter groups of a zone, with its faction weights and its density restrictions (decision
    /// D732 of the MCP1 design). A list that the call does not give keeps its lines.</summary>
    public static ToolStep SetEncounters(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var args = call.Args;
        var zone = EditTools.Section(call, args.Text("zone")!, "zone");
        scope.Run(EncounterCommands.SetGroups(scope.Session, zone, Encounters(args),
            Shares(args.Rows("faction_weights")), Densities(args)), [zone]);
    });

    private static List<LoadoutEquipment> Equipment(ToolArgs args) =>
        args.Rows("equipment")?.Select(r => new LoadoutEquipment(r.Text("equipment")!, r.Text("hardpoint"))).ToList() ?? [];

    private static List<LoadoutCargo> Cargo(ToolArgs args, string name) =>
        args.Rows(name)?.Select(r => new LoadoutCargo(r.Text("good")!, (int)r.Whole("count")!.Value)).ToList() ?? [];

    private static List<EncounterGroup> Encounters(ToolArgs args) =>
        args.Rows("encounters")?.Select(r => new EncounterGroup(r.Text("encounter")!, (int)r.Whole("level")!.Value,
            r.Number("chance")!.Value, Shares(r.Rows("factions"))!)).ToList() ?? [];

    private static List<FactionShare>? Shares(IReadOnlyList<ToolArgs>? rows) =>
        rows?.Select(r => new FactionShare(r.Text("faction")!, r.Number("weight")!.Value)).ToList();

    private static List<DensityRestriction>? Densities(ToolArgs args) =>
        args.Rows("density_restrictions")?.Select(r => new DensityRestriction((int)r.Whole("count")!.Value, r.Text("class")!))
            .ToList();
}
