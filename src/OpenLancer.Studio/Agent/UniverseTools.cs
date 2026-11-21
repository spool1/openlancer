using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>How a route table changes with an edit: the routes between two systems that the edit adds, makes shorter,
/// makes longer and removes.</summary>
internal sealed record RouteChange(int New, int Shorter, int Longer, int Lost)
{
    public static RouteChange Of(RouteDiff diff) => new(diff.New, diff.Shorter, diff.Longer, diff.Lost);
}

/// <summary>The jump objects of the two systems of a new pair after the edit.</summary>
internal sealed record PairSlots(JumpSlots From, JumpSlots To);

/// <summary>A value of each end of a new pair, for example the loadout of each gate.</summary>
/// <param name="From">The value of the end <c>from</c>, or null.</param>
/// <param name="To">The value of the end <c>to</c>, or null.</param>
internal sealed record PairValues(string? From, string? To);

/// <summary>The result of <c>system_create</c>: the result of the edit, with the new system, its file and its sun. A
/// step of <c>edit_batch</c> gives the result of the edit only.</summary>
internal sealed record SystemEditResult : EditResult
{
    public SystemEditResult(EditResult result) : base(result)
    {
    }

    /// <summary>The <c>system:</c> reference of the new system.</summary>
    public string? System { get; init; }

    /// <summary>The install path of the new system file.</summary>
    public string? File { get; init; }

    /// <summary>The <c>obj:</c> reference of the sun at the center of the new system.</summary>
    public string? Sun { get; init; }
}

/// <summary>The result of <c>connection_create</c>: the result of the edit, with the two ends, their traffic zones,
/// the chosen loadouts and pilots, the change of the route table of all connections and the jump objects of the two
/// systems. A step of <c>edit_batch</c> gives the result of the edit only.</summary>
internal sealed record ConnectionEditResult : EditResult
{
    public ConnectionEditResult(EditResult result) : base(result)
    {
    }

    /// <summary>The <c>obj:</c> references of the two ends, in the order of <c>from</c> and <c>to</c>.</summary>
    public IReadOnlyList<string>? Pair { get; init; }

    /// <summary>The <c>zone:</c> references of the new traffic zones.</summary>
    public IReadOnlyList<string>? TrafficZones { get; init; }

    /// <summary>The loadout of each end. A jump hole and an existing end have none.</summary>
    public PairValues? Loadout { get; init; }

    /// <summary>The pilot of each end. A jump hole and an existing end have none.</summary>
    public PairValues? Pilot { get; init; }

    public RouteChange? Routes { get; init; }

    public PairSlots? JumpObjects { get; init; }
}

/// <summary>The result of <c>connection_set_lock</c>: the result of the edit, with the change of the route table of all
/// connections. A step of <c>edit_batch</c> gives the result of the edit only.</summary>
internal sealed record LockEditResult : EditResult
{
    public LockEditResult(EditResult result) : base(result)
    {
    }

    public RouteChange? Routes { get; init; }
}

/// <summary>
/// The edit tools of the universe: a new star system, a new jump gate pair or jump hole pair, and the gate locks of a
/// gate. Each tool calls the plan of the universe (see <see cref="SystemPlans"/> and <see cref="ConnectionPlans"/>) in
/// the shared edit call of <see cref="EditTools"/>. So each call is one Undo step, also when it changes two system
/// files, the gate locks and the text of the mod.
/// </summary>
/// <remarks>
/// <para>A value that names other data must be an entry of its list, as a picker of the editor gives it (Editor UI rule
/// 2). The tool writes each value as the list spells it. A value that its list does not have gives
/// <c>invalid_value</c> with the closest names.</para>
/// <para>A refusal of a plan keeps the text of the plan, so the editor and the agent read one text. The tool gives it
/// the error kind and the argument of its cause.</para>
/// </remarks>
internal static class UniverseTools
{
    /// <summary>The detail code of an object of another system in an end of <c>connection_create</c>.</summary>
    public const string EndOtherSystem = "EndOtherSystem";

    /// <summary>The words of <c>kind</c> of <c>connection_create</c>.</summary>
    public static readonly IReadOnlyList<string> PairKinds = ["jump_gate", "jump_hole"];

    /// <summary>The words of <c>locked</c>.</summary>
    public static IReadOnlyList<string> Locks { get; } = AgentTools.Names<JumpLock>();

    /// <summary>The words of <c>copy</c> of <c>system_create</c>.</summary>
    public static IReadOnlyList<string> Parts { get; } = AgentTools.Names<SystemPart>();

    /// <summary>The members of an end of <c>connection_create</c> that only a new object takes.</summary>
    private static readonly string[] NewEndMembers = ["position", "rotation", "nickname", "name"];

    /// <summary>The arguments of each refusal of <see cref="SystemPlans.Create"/>, by the detail code of the
    /// refusal.</summary>
    private static readonly Dictionary<string, (EditErrorKind Kind, string Field)> SystemRefusals = new()
    {
        [MissionErrors.NameInvalid] = (EditErrorKind.InvalidValue, "nickname"),
        [MissionErrors.NameTaken] = (EditErrorKind.NameTaken, "nickname"),
        [MissionErrors.PathInUse] = (EditErrorKind.InvalidValue, "nickname"),
        [SystemPlans.MapPosRange] = (EditErrorKind.InvalidValue, "map_pos"),
        [MissionErrors.FieldValue] = (EditErrorKind.InvalidValue, "nav_map_scale"),
        [SystemPlans.LikeNoFile] = (EditErrorKind.InvalidValue, "like"),
        [SystemPlans.LikeNoSun] = (EditErrorKind.InvalidValue, "sun"),
        [SystemPlans.SunType] = (EditErrorKind.InvalidValue, "sun.archetype"),
    };

    /// <summary>The error kind of each refusal of <see cref="ConnectionPlans.CreatePair"/>, by the detail code of the
    /// refusal. The plan names the argument (see <see cref="ConnectionPlan.RefusedPart"/>).</summary>
    private static readonly Dictionary<string, EditErrorKind> PairRefusals = new()
    {
        [ConnectionPlans.JumpLimit] = EditErrorKind.Refused,
        [ConnectionPlans.GateSameSystem] = EditErrorKind.InvalidValue,
        [ConnectionPlans.KindMismatch] = EditErrorKind.InvalidValue,
        [ConnectionPlans.EndPaired] = EditErrorKind.InvalidValue,
        [MissionErrors.NameInvalid] = EditErrorKind.InvalidValue,
        [MissionErrors.FieldValue] = EditErrorKind.InvalidValue,
        [MissionErrors.NameTaken] = EditErrorKind.NameTaken,
        [MissionErrors.ItemNotFound] = EditErrorKind.NotFound,
    };

    /// <summary>
    /// Makes a new star system of the active mod like a template system (see <see cref="SystemPlans.Create"/>). The
    /// result gives the new system, its file and its sun. In the editor, the Scene opens the new system only after the
    /// save and the reload of the game data, so the result tells it.
    /// </summary>
    public static ToolStep SystemCreate(ToolCall call)
    {
        Func<EditResult, EditResult> finish = result => result;
        return EditTools.Apply(call, scope =>
        {
            var session = scope.Session;
            var args = call.Args;
            var like = EditTools.At("like", () => SceneQueries.System(session, args.Text("like")!, "like")).Nickname;
            var values = new RowValues(session);
            var faction = Listed(values, args.Text("local_faction"), "local_faction", "faction");
            SunRow? sunRow = null;
            if (args.Group("sun") is { } row)
            {
                sunRow = new SunRow(Listed(values, row.Text("archetype"), "sun.archetype", "solar_archetype"),
                    Listed(values, row.Text("star"), "sun.star", "star"), row.Text("name"));
            }
            values.ThrowIfAny();
            var place = args.Wholes("map_pos")!;
            var options = new SystemOptions
            {
                OnMapAtStart = args.Flag("on_map_at_start"),
                NavMapScale = (float?)args.Number("nav_map_scale"),
                Copy = args.Texts("copy")?.Select(p => Enum.Parse<SystemPart>(p, ignoreCase: true)).ToHashSet(),
                LocalFaction = faction,
                Sun = sunRow,
            };
            var plan = SystemPlans.Create(session, args.Text("nickname")!, like, (place[0], place[1]), args.Text("name")!,
                args.Text("infocard"), options);
            Run(scope, plan, error => SystemRefusals.TryGetValue(error.Detail ?? "", out var cause)
                ? (cause.Kind, cause.Field)
                : ((EditErrorKind, string?)?)null);
            // The references take the form that they have while the new items exist, also in a preview.
            var system = ItemRefs.Canonical(session, plan.System!);
            var sun = ItemRefs.Canonical(session, plan.Sun!);
            finish = result => new SystemEditResult(result) { System = system, File = plan.File, Sun = sun };
            if (call.Host.Kind != AgentHostKind.Editor)
                return;
            scope.Report(new EditResult(EditStatus.Applied)
            {
                Warnings = [new Problem(DiagnosticSeverity.Warning, AgentTexts.Error("system_save_to_open")) { Ref = system }],
            });
        }, result => finish(result));
    }

    /// <summary>
    /// Makes a new jump gate pair or jump hole pair (see <see cref="ConnectionPlans.CreatePair"/>). An end can name an
    /// existing object without a working pair, and the tool then writes only its <c>goto</c> line. The result gives the
    /// two ends, their traffic zones, the chosen loadouts and pilots, the change of the route table of all connections
    /// and the jump objects of the two systems.
    /// </summary>
    public static ToolStep ConnectionCreate(ToolCall call)
    {
        Func<EditResult, EditResult> finish = result => result;
        return EditTools.Apply(call, scope =>
        {
            var session = scope.Session;
            var args = call.Args;
            var from = End(call, "from");
            var to = End(call, "to");
            var like = args.Text("like") is { } text ? EditTools.Section(call, text, "like") : null;
            var values = new RowValues(session);
            var options = new ConnectionOptions
            {
                Like = like,
                Archetype = Listed(values, args.Text("archetype"), "archetype", "solar_archetype"),
                Faction = Listed(values, args.Text("faction"), "faction", "faction"),
                Loadout = Listed(values, args.Text("loadout"), "loadout", "loadout"),
                Pilot = Listed(values, args.Text("pilot"), "pilot", "pilot"),
                Lock = Lock(args.Text("locked")!),
                TrafficZones = args.Flag("traffic_zone"),
                SystemName = system => session.SystemSection(system) is { } section
                    ? call.Texts.TextOf(section, "strid_name")
                    : null,
            };
            values.ThrowIfAny();
            var kind = args.Text("kind") == "jump_hole" ? ConnectionKind.Hole : ConnectionKind.Gate;
            var plan = ConnectionPlans.CreatePair(session, kind, from, to, options);
            Run(scope, plan, error => (PairRefusals.GetValueOrDefault(error.Detail ?? "", error.Kind), plan.RefusedPart));
            var pair = plan.Ends.Select(e => ItemRefs.Canonical(session, e.Object)).ToList();
            var zones = plan.TrafficZones.Select(z => ItemRefs.Canonical(session, z)).ToList();
            finish = result => new ConnectionEditResult(result)
            {
                Pair = pair,
                TrafficZones = zones,
                Loadout = new PairValues(plan.Ends[0].Loadout, plan.Ends[^1].Loadout),
                Pilot = new PairValues(plan.Ends[0].Pilot, plan.Ends[^1].Pilot),
                Routes = plan.Routes is { } routes ? RouteChange.Of(routes) : null,
                JumpObjects = new PairSlots(Slots(plan.JumpObjects[0]), Slots(plan.JumpObjects[^1])),
            };
        }, result => finish(result));
    }

    /// <summary>Sets the locks of a jump gate or a jump hole at the start of a new game, and of its partner (see
    /// <see cref="ConnectionPlans.SetLock"/>). The result gives the change of the route table of all
    /// connections.</summary>
    public static ToolStep ConnectionSetLock(ToolCall call)
    {
        RouteChange? routes = null;
        return EditTools.Apply(call, scope =>
        {
            var args = call.Args;
            var gate = EditTools.Section(call, args.Text("object")!, "object");
            var plan = ConnectionPlans.SetLock(scope.Session, gate, Lock(args.Text("locked")!), args.Flag("pair"));
            Run(scope, plan, error => error.Detail == ConnectionPlans.NotConnection
                ? (EditErrorKind.InvalidValue, "object")
                : ((EditErrorKind, string?)?)null, [gate]);
            routes = plan.Routes is { } diff ? RouteChange.Of(diff) : null;
        }, result => new LockEditResult(result) { Routes = routes });
    }

    /// <summary>
    /// Runs a plan in the scope. A plan that refuses throws its refusal with the error kind and the argument of its
    /// cause.
    /// </summary>
    /// <param name="cause">Gives the error kind and the argument of a refusal, or null to keep the error as the plan
    /// gives it. A null argument keeps the argument of the error.</param>
    /// <param name="targets">The sections that the request names, for the problems before and after the edit.</param>
    private static void Run(EditScope scope, EditPlan plan, Func<EditError, (EditErrorKind Kind, string? Field)?> cause,
        IEnumerable<SectionRef>? targets = null)
    {
        if (!plan.CanRun)
        {
            var error = EditTools.RefusalOf(scope.Session, plan);
            throw new EditException(cause(error) is { } found
                ? error with { Kind = found.Kind, Field = found.Field ?? error.Field }
                : error);
        }
        scope.Run(plan, targets);
    }

    /// <summary>
    /// One end of <c>connection_create</c>: its system, and the object of an existing end or the parts of a new end. An
    /// <c>obj:</c> reference must name an object of the system of the end. A nickname is an object of that system. An
    /// existing end takes no part of a new end, because the tool writes only its <c>goto</c> line.
    /// </summary>
    /// <param name="name">The argument of the end: <c>from</c> or <c>to</c>.</param>
    private static ConnectionEnd End(ToolCall call, string name)
    {
        var end = call.Args.Group(name)!;
        var field = $"{name}.system";
        var (system, _, path) = EditTools.At(field, () => SceneQueries.System(call.Session, end.Text("system")!, field));
        string? existing = null;
        if (end.Text("object") is { } text)
        {
            if (NewEndMembers.FirstOrDefault(end.Has) is { } member)
            {
                throw ToolInput.Bad($"{name}.{member}",
                    AgentTexts.Error("connection_end_existing", ("end", name), ("member", member)));
            }
            // No nickname holds a ':' (section 5.6 of the MCP1 design), so a text with one is a reference.
            if (!text.Contains(':'))
                existing = text.Trim();
            else
            {
                var section = EditTools.Section(call, text, $"{name}.object");
                if (path == null || !VfsPath.Comparer.Equals(section.Path, path))
                {
                    throw new EditException(new EditError(EditErrorKind.InvalidArgument, EndOtherSystem,
                        AgentTexts.Error("connection_end_system", ("ref", text), ("system", system)))
                    {
                        Field = $"{name}.object",
                        Target = text,
                    });
                }
                existing = section.Id.Key;
            }
        }
        return new ConnectionEnd(system)
        {
            Object = existing,
            Position = end.Point("position"),
            Rotation = end.Point("rotation"),
            Nickname = end.Text("nickname"),
            Name = end.Text("name"),
        };
    }

    /// <summary>The value of a text argument as its list spells it, or null when the call does not give it. A value that
    /// the list does not have adds an error to the values.</summary>
    private static string? Listed(RowValues values, string? text, string field, string list) =>
        text == null ? null : values.Listed(text.Trim(), field, [list]);

    private static JumpLock Lock(string word) => Enum.Parse<JumpLock>(word, ignoreCase: true);

    private static JumpSlots Slots(JumpObjectCount count) => new(count.Used, count.Max);
}
