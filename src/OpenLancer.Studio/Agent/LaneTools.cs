using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The result of <c>trade_lane_create</c>, <c>trade_lane_insert_ring</c> and <c>trade_lane_repair</c>: the
/// result of the edit, with the parts of the lane that the tool tells. A step of <c>edit_batch</c> gives the result of
/// the edit only.</summary>
internal sealed record LaneEditResult : EditResult
{
    public LaneEditResult(EditResult result) : base(result)
    {
    }

    /// <summary>The <c>obj:</c> references of the rings of the lane, from the first ring to the last ring. A new lane and
    /// a repair give them.</summary>
    public IReadOnlyList<string>? Rings { get; init; }

    /// <summary>The <c>zone:</c> reference of the new lane zone, or null. Only a new lane gives it.</summary>
    public string? Zone { get; init; }

    /// <summary>The distance between two rings of a new lane in meters.</summary>
    public float? Spacing { get; init; }

    /// <summary>The <c>obj:</c> reference of the ring that an insert adds.</summary>
    public string? Ring { get; init; }

    /// <summary>The place of the new ring of an insert in its lane, from 1. The name is not <c>index</c>, because a tool
    /// error has a property with that name.</summary>
    public int? Place { get; init; }

    /// <summary>The count of the rings of the lane after an insert.</summary>
    public int? Count { get; init; }
}

/// <summary>A ring of a trade lane (tool <c>get_trade_lane</c>).</summary>
/// <param name="Ref">The <c>obj:</c> reference of the ring.</param>
/// <param name="Position">The position of the ring in meters, or null when it has no <c>pos</c> line.</param>
/// <param name="SpacingToNext">The distance to the next ring in meters, or null for the last ring.</param>
internal sealed record LaneRingRow(string Ref, float[]? Position, float? SpacingToNext);

/// <summary>The name of an end of a trade lane, as the HUD shows it (tool <c>get_trade_lane</c>).</summary>
/// <param name="Text">The text of the name, or an empty text when the text id has no text.</param>
/// <param name="Key">The value of the <c>tradelane_space_name</c> line: a text id, or a text of the active mod.</param>
internal sealed record LaneEndText(string Text, string Key);

/// <summary>A trade lane of W with its rings, its end names, its lane zone and its problems (tool
/// <c>get_trade_lane</c>).</summary>
/// <param name="System">The <c>system:</c> reference of the system of the lane, or null when no <c>[system]</c> names
/// the file.</param>
/// <param name="Rings">The rings from the first ring to the last ring.</param>
/// <param name="StartName">The name of the first ring, or null.</param>
/// <param name="EndName">The name of the last ring, or null.</param>
/// <param name="Zone">The <c>zone:</c> reference of the lane zone, or null when no lane zone covers the first and the
/// last ring (see <see cref="LanePlans.ZoneOf"/>).</param>
/// <param name="Length">The distance from the first ring to the last ring along the rings in meters, or null when a ring
/// has no position.</param>
/// <param name="Problems">The problems of the trade lane checks on the rings, and on each lane zone that does not cover
/// the lane (see <see cref="LaneChecks.Lane"/>).</param>
internal sealed record LaneView(string? System, IReadOnlyList<LaneRingRow> Rings, LaneEndText? StartName,
    LaneEndText? EndName, string? Zone, float? Length, IReadOnlyList<Problem> Problems);

/// <summary>
/// The tools of the trade lanes: a new lane with its lane zone, a new ring, a repair of the links, the delete of a lane
/// and the read of a lane. Each edit tool calls the plan of the lanes (see <see cref="LanePlans"/>) in the shared edit
/// call of <see cref="EditTools"/>, so each call is one Undo step. The ring buttons of the Inspector call the same
/// plans.
/// </summary>
/// <remarks>A refusal of a plan keeps the text of the plan, so the editor and the agent read one text. The tool gives
/// it the error kind of its detail code and the argument that the plan names (see
/// <see cref="LanePlan.RefusedPart"/>).</remarks>
internal static class LaneTools
{
    /// <summary>The error kind of each refusal of the lane plans, by the detail code of the refusal.</summary>
    private static readonly Dictionary<string, EditErrorKind> Refusals = new()
    {
        [LanePlans.LaneRings] = EditErrorKind.InvalidValue,
        [LanePlans.LaneEnd] = EditErrorKind.InvalidArgument,
        [LanePlans.LaneEndName] = EditErrorKind.InvalidArgument,
        [LanePlans.LaneArchetype] = EditErrorKind.InvalidValue,
        [LanePlans.NotLaneRing] = EditErrorKind.InvalidValue,
        [LanePlans.BeforeNotFirst] = EditErrorKind.InvalidValue,
        [LanePlans.LaneNotLine] = EditErrorKind.InvalidValue,
        [MissionErrors.NameInvalid] = EditErrorKind.InvalidValue,
        [MissionErrors.FieldValue] = EditErrorKind.InvalidValue,
        [MissionErrors.NameTaken] = EditErrorKind.NameTaken,
        [MissionErrors.ItemNotFound] = EditErrorKind.NotFound,
    };

    /// <summary>
    /// Makes a new trade lane in a system with its lane zone (see <see cref="LanePlans.Create"/>). Each end is a point,
    /// an object of the system, or both. The result gives the rings in order, the lane zone and the spacing.
    /// </summary>
    public static ToolStep Create(ToolCall call)
    {
        Func<EditResult, EditResult> finish = result => result;
        return EditTools.Apply(call, scope =>
        {
            var session = scope.Session;
            var args = call.Args;
            var (system, _, path) = EditTools.At("system", () => SceneQueries.System(session, args.Text("system")!));
            var start = End(call, "start", system, path);
            var end = End(call, "end", system, path);
            var like = args.Text("like") is { } text ? EditTools.Section(call, text, "like") : null;
            var values = new RowValues(session);
            var faction = UniverseTools.Listed(values, args.Text("faction"), "faction", "faction");
            values.ThrowIfAny();
            var plan = LanePlans.Create(session, system, start, end, new LaneOptions
            {
                // A count above the limit goes to the plan as it is, so the refusal of the plan names the spacing of
                // that count. Only a count above the range of int stops at int.MaxValue.
                Rings = args.Whole("rings") is { } count ? (int)Math.Min(count, int.MaxValue) : null,
                Like = like,
                Faction = faction,
                Zone = args.Flag("zone"),
                Nickname = args.Text("nickname"),
            });
            Run(scope, plan);
            // The references take the form that they have while the new items exist, also in a preview.
            var rings = plan.Rings.Select(r => ItemRefs.Canonical(session, r)).ToList();
            var zone = plan.Zone is { } added ? ItemRefs.Canonical(session, added) : null;
            finish = result => new LaneEditResult(result) { Rings = rings, Zone = zone, Spacing = plan.Spacing };
        }, result => finish(result));
    }

    /// <summary>
    /// Adds a ring after a ring of a lane, or before its first ring (see <see cref="LanePlans.InsertRing"/>). The call
    /// gives <c>after</c> or <c>before</c>, not both. The result gives the new ring, its place and the ring count.
    /// </summary>
    public static ToolStep InsertRing(ToolCall call)
    {
        Func<EditResult, EditResult> finish = result => result;
        return EditTools.Apply(call, scope =>
        {
            var session = scope.Session;
            var args = call.Args;
            var after = args.Text("after");
            var before = args.Text("before");
            if (after == null && before == null)
                throw ToolInput.Bad("after", AgentTexts.Error("lane_after_or_before"));
            if (after != null && before != null)
                throw ToolInput.Bad("before", AgentTexts.Error("lane_after_and_before"));
            var field = after != null ? "after" : "before";
            var ring = EditTools.Section(call, (after ?? before)!, field);
            var plan = LanePlans.InsertRing(session, ring, after != null ? LanePlace.After : LanePlace.Before,
                args.Point("position"), args.Text("nickname"));
            Run(scope, plan, [ring]);
            var added = ItemRefs.Canonical(session, plan.Ring!);
            finish = result => new LaneEditResult(result) { Ring = added, Place = plan.Index, Count = plan.Count };
        }, result => finish(result));
    }

    /// <summary>
    /// Writes the links of the lane of a ring again in the order of the rings on the line, and moves the end names to the
    /// end rings (see <see cref="LanePlans.Repair"/>). A lane with good links gives <c>no_change</c>. The result gives the
    /// rings in the new order.
    /// </summary>
    public static ToolStep Repair(ToolCall call)
    {
        IReadOnlyList<string>? rings = null;
        return EditTools.Apply(call, scope =>
        {
            var session = scope.Session;
            var ring = EditTools.Section(call, call.Args.Text("ring")!, "ring");
            var plan = LanePlans.Repair(session, ring);
            Run(scope, plan, [ring]);
            rings = plan.Rings.Select(r => ItemRefs.Canonical(session, r)).ToList();
        }, result => new LaneEditResult(result) { Rings = rings });
    }

    /// <summary>Deletes the lane of a ring: all its rings and its lane zone (see <see cref="LanePlans.Delete"/>). The
    /// delete always asks for <c>confirm: true</c>.</summary>
    public static ToolStep Delete(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var ring = EditTools.Section(call, call.Args.Text("ring")!, "ring");
        var plan = LanePlans.Delete(scope.Session, ring);
        UniverseTools.Run(scope, plan, error => error.Detail == LanePlans.NotLaneRing
            ? (EditErrorKind.InvalidValue, "ring")
            : ((EditErrorKind, string?)?)null, [ring]);
    });

    /// <summary>
    /// Gives the lane of a ring: its rings in order with their positions and spacings, the end names, the lane zone,
    /// the length and the problems of the trade lane checks. The walk of the links is the walk of the plans (see
    /// <see cref="LanePlans.LaneOf"/>).
    /// </summary>
    public static ToolStep GetTradeLane(ToolCall call)
    {
        var session = call.Session;
        var text = call.Args.Text("ring")!;
        var ring = EditTools.At("ring", () =>
        {
            var reference = ItemRefs.Parse(text);
            if (reference.Kind != ItemRefKind.Object || ItemRefs.ResolveSection(session, reference) is not { } section ||
                LanePlans.RingView(session, section) == null)
            {
                throw new EditException(new EditError(EditErrorKind.InvalidValue, LanePlans.NotLaneRing,
                    AgentTexts.Error("lane_not_ring", ("ref", text))) { Target = text });
            }
            return section;
        });
        var lane = LanePlans.LaneOf(session, ring)!;
        var positions = lane.Rings.Select(r => LanePlans.PositionOf(session, r)).ToList();
        var rows = lane.Rings.Select((r, i) => new LaneRingRow(ItemRefs.Canonical(session, r),
            AgentRows.Numbers(session.Get(r)!, "pos"),
            i + 1 < lane.Rings.Count && positions[i] is { } a && positions[i + 1] is { } b ? Vector3.Distance(a, b) : null))
            .ToList();
        var zone = LanePlans.ZoneOf(session, lane);
        var problems = LaneChecks.Lane(session, lane).Select(d => AgentRows.ProblemOf(session, d)).ToList();
        return ToolResult.Of(new LaneView(
            session.SystemNicknameOf(ring.Path) is { } system ? SceneQueries.SystemRef(system) : null,
            rows,
            lane.Loops ? null : EndName(call, lane.Rings[0]),
            lane.Loops || lane.Rings.Count < 2 ? null : EndName(call, lane.Rings[^1]),
            zone is { } found ? ItemRefs.Canonical(session, found) : null,
            rows.All(r => r.Position != null) ? rows.Sum(r => r.SpacingToNext ?? 0) : null,
            problems));
    }

    /// <summary>Runs a lane plan in the scope. A plan that refuses throws its refusal with the error kind of its detail
    /// code and the argument that the plan names.</summary>
    private static void Run(EditScope scope, LanePlan plan, IEnumerable<SectionRef>? targets = null) =>
        UniverseTools.Run(scope, plan, error => (Refusals.GetValueOrDefault(error.Detail ?? "", error.Kind), plan.RefusedPart),
            targets);

    /// <summary>
    /// One end of <c>trade_lane_create</c>: its point, its object and its name. The object of an end is a nickname or
    /// an <c>obj:</c> reference, as the object of an end of <c>connection_create</c> is. A nickname names an object of
    /// the system of the lane. A reference must name an object of that system. The plan checks the kind of the object.
    /// </summary>
    /// <param name="name">The stem of the arguments of the end: <c>start</c> or <c>end</c>.</param>
    /// <param name="path">The install path of the system file, or null when the system names no file. The plan then
    /// refuses the system before it reads the ends.</param>
    private static TradeLaneEnd End(ToolCall call, string name, string system, string? path)
    {
        var args = call.Args;
        SectionRef? near = null;
        var field = $"{name}_near";
        if (args.Text(field) is { } text)
        {
            // No nickname holds a ':' (section 5.6 of the MCP1 design), so a text with one is a reference.
            if (!text.Contains(':'))
                near = path == null ? null : ObjectOf(call, text.Trim(), system, path, field);
            else
                near = EditTools.Section(call, text, field);
            if (near != null && path != null && !VfsPath.Comparer.Equals(near.Path, path))
            {
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, UniverseTools.EndOtherSystem,
                    AgentTexts.Error("connection_end_system", ("ref", text), ("system", system)))
                {
                    Field = field,
                    Target = text,
                });
            }
        }
        return new TradeLaneEnd { Point = args.Point(name), Near = near, Name = args.Text($"{name}_name") };
    }

    /// <summary>The object of a system file with a nickname, without letter case. A nickname that no object of the file
    /// has gives <c>not_found</c>.</summary>
    private static SectionRef ObjectOf(ToolCall call, string nickname, string system, string path, string field) =>
        call.Session.Sections(path).FirstOrDefault(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) &&
                                                        SceneSections.Is(s.Id.Key ?? "", nickname))
        ?? throw new EditException(new EditError(EditErrorKind.NotFound, MissionErrors.ItemNotFound,
            AgentTexts.Error("system_no_object", ("system", system), ("name", nickname)))
        {
            Field = field,
            Target = nickname,
        });

    /// <summary>The name of an end ring, or null when the ring has no <c>tradelane_space_name</c> line.</summary>
    private static LaneEndText? EndName(ToolCall call, SectionRef ring) =>
        call.Session.Get(ring) is { } view && SceneSections.Last(view, "tradelane_space_name") is { } key
            ? new LaneEndText(call.Texts.TextOf(ring, "tradelane_space_name"), key)
            : null;
}
