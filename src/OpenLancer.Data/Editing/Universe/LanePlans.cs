using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;
using UniverseSchema = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>Where a new ring goes in its lane (see <see cref="LanePlans.InsertRing"/>).</summary>
public enum LanePlace
{
    /// <summary>After a ring. After the last ring, the lane gets longer at its end.</summary>
    After,

    /// <summary>Before the first ring. The lane gets longer at its start.</summary>
    Before,
}

/// <summary>One end of a new trade lane (see <see cref="LanePlans.Create"/>). Give a point, an object, or both.</summary>
public sealed record TradeLaneEnd
{
    /// <summary>The center of the end ring in meters, or null for a place near <see cref="Near"/>.</summary>
    public Vector3? Point { get; init; }

    /// <summary>An object of the system at this end, or null. Without a point, the end ring goes at a clear distance from
    /// the object, toward the other end. The end also takes the name of the object when it has no name.</summary>
    public SectionRef? Near { get; init; }

    /// <summary>The name of the end for the HUD, or null for the name of <see cref="Near"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>The options of a new trade lane (see <see cref="LanePlans.Create"/>).</summary>
public sealed record LaneOptions
{
    /// <summary>The count of the rings, or null for one ring each spacing of the rule data.</summary>
    public int? Rings { get; init; }

    /// <summary>A ring whose lines the new rings copy, or null for the rule data: archetype, name, infocard, behavior,
    /// difficulty, faction, loadout and pilot.</summary>
    public SectionRef? Like { get; init; }

    /// <summary>The faction of the rings, or null for the faction of <see cref="Like"/>, else the local faction of the
    /// system.</summary>
    public string? Faction { get; init; }

    /// <summary>True to add the lane zone, where lane traffic spawns.</summary>
    public bool Zone { get; init; } = true;

    /// <summary>The stem of the ring nicknames, or null for <c>&lt;System&gt;_Trade_Lane_Ring</c>.</summary>
    public string? Nickname { get; init; }
}

/// <summary>The rings of one trade lane in W, from the first ring to the last ring (see
/// <see cref="LanePlans.LaneOf"/>).</summary>
/// <param name="Path">The install path of the system file of the lane.</param>
/// <param name="Rings">The rings in order, each ring once. A ring without links is a lane of one ring.</param>
/// <param name="Loops">True when the links go in a circle: the walk in one direction finds a ring of that walk again.
/// The game walks each direction alone, so only such a circle makes the HUD hang. The lane then has no first ring and no
/// last ring. A walk that reaches a ring of the walk in the other direction is no circle. The chain then stops at that
/// ring.</param>
public sealed record LaneChain(string Path, IReadOnlyList<SectionRef> Rings, bool Loops);

/// <summary>A circle of the links of a lane, as the walk of the HUD goes round it (see
/// <see cref="LanePlans.CircleOf"/>).</summary>
/// <param name="Rings">The rings of the circle in the order of the links.</param>
/// <param name="Key">The link line that goes round the circle: <c>next_ring</c> or <c>prev_ring</c>.</param>
/// <param name="Strays">The rings of the circle whose link line <paramref name="Key"/> is a stray link (see
/// <see cref="LanePlans.LaneOf"/>). The relink of a delete does not follow a stray link. So when the circle has such a
/// ring, only the delete of such a ring cuts the circle.</param>
internal sealed record LaneCircle(IReadOnlyList<SectionRef> Rings, string Key, IReadOnlyList<SectionRef> Strays);

/// <summary>
/// The plans of the trade lanes: a new lane with its lane zone, a new ring, a repair of the links, the remove of rings
/// and the delete of a lane. Each plan gives one command, which runs as one entry of the edit history. The agent tools
/// and the ring buttons of the Inspector call the same plans. <see cref="LaneOf"/> is the one walk of the links of a
/// lane. The checks and the Inspector use it too.
/// </summary>
/// <remarks>
/// <para>Retail facts: a lane is a chain of <c>[Object]</c> rings of one system file with the archetype type
/// <c>TRADELANE_RING</c>. Each ring links to the next ring with <c>next_ring</c> and to the previous ring with
/// <c>prev_ring</c>. The first ring has no <c>prev_ring</c>, and the last ring has no <c>next_ring</c>. The two end
/// rings have <c>tradelane_space_name</c>, and the HUD shows "first end name -> last end name". The HUD follows the
/// links to the two ends, so a chain that goes in a circle makes the game hang. Retail rings stand a median 7500 m
/// apart, and their rotation turns their front along the lane. Lane traffic spawns in the lane zone (see
/// <see cref="LaneZones"/>).</para>
/// <para>A delete of a ring goes through <see cref="ScenePlans.Delete(EditSession, IReadOnlyList{SectionRef})"/>, so it
/// follows the rules of each other delete. The neighbors of the ring then link to each other, the end name moves, and
/// the lane zone follows the ends.</para>
/// </remarks>
public static class LanePlans
{
    /// <summary>The detail code of a lane with a ring count or a spacing outside the limits.</summary>
    public const string LaneRings = "LaneRings";

    /// <summary>The detail code of an end without a point and without an object.</summary>
    public const string LaneEnd = "LaneEnd";

    /// <summary>The detail code of an end without a name.</summary>
    public const string LaneEndName = "LaneEndName";

    /// <summary>The detail code of a template or an archetype that is not a trade lane ring.</summary>
    public const string LaneArchetype = "LaneArchetype";

    /// <summary>The detail code of an object that is not a ring of a trade lane.</summary>
    public const string NotLaneRing = "NotLaneRing";

    /// <summary>The detail code of an insert before a ring that is not the first ring.</summary>
    public const string BeforeNotFirst = "BeforeNotFirst";

    /// <summary>The detail code of a repair of rings that do not stand on one line.</summary>
    public const string LaneNotLine = "LaneNotLine";

    /// <summary>The fewest rings of a lane.</summary>
    public const int MinRings = 2;

    /// <summary>The most rings of a lane.</summary>
    public const int MaxRings = 100;

    /// <summary>The smallest distance between two rings of a new lane, in meters.</summary>
    public const float MinSpacing = 1000;

    /// <summary>The largest distance between two rings of a new lane, in meters.</summary>
    public const float MaxSpacing = 20000;

    /// <summary>The distance in meters within which a ring is on the line of a lane, and a lane zone covers a ring.</summary>
    public const float Reach = 1500;

    /// <summary>The <c>[Solar] type</c> of a trade lane ring, in lowercase.</summary>
    internal const string RingType = "tradelane_ring";

    /// <summary>The lines of a new ring in their order.</summary>
    private static readonly string[] RingOrder =
    [
        "nickname", "ids_name", "pos", "rotate", "archetype", "ids_info", "prev_ring", "next_ring", "behavior",
        "tradelane_space_name", "reputation", "difficulty_level", "loadout", "pilot",
    ];

    /// <summary>The lines that a new ring copies from its template ring, in their order.</summary>
    private static readonly string[] CopiedKeys =
        ["ids_name", "archetype", "ids_info", "behavior", "reputation", "difficulty_level", "loadout", "pilot"];

    /// <summary>
    /// Plans a new trade lane in a system, with its lane zone, as one entry of the edit history.
    /// </summary>
    /// <remarks>
    /// <para>An end with an object and no point goes on the line toward the other end. Its distance from the object is
    /// the <c>solar_radius</c> of the archetype of the object plus the clearance of the rule data. A zone with damage
    /// can hold that place, for example the death zone of a sun. The end then goes farther along the line, to the
    /// clearance past the edge of the zone. The ring count is one ring each spacing of the rule data, plus one. The rings get the nicknames
    /// <c>&lt;System&gt;_Trade_Lane_Ring_&lt;n&gt;</c> with the first free numbers, and a rotation whose front looks
    /// along the lane. The end names go into the text of the active mod, or the ends take the name of their object.
    /// The lane zone <c>Zone_&lt;System&gt;_Tradelane_&lt;n&gt;</c> is a box on the lane that meets the lane zone rule.
    /// It holds no encounters.</para>
    /// <para>The plan refuses a system without a file that the editor can change. It refuses an end without a point and
    /// an object, an end without a name, and two ends that pass each other because their objects are too close. It also
    /// refuses a template that is not a ring, and a ring count or a spacing outside the limits. A spacing outside the
    /// retail range and a ring inside a zone with damage or inside an object give warnings.</para>
    /// </remarks>
    public static LanePlan Create(EditSession session, string system, TradeLaneEnd start, TradeLaneEnd end,
        LaneOptions? options = null)
    {
        options ??= new LaneOptions();
        var plan = new LanePlan($"Create trade lane in {system.Trim()}");
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (session.Lists.RuleData.Builders.Lane is not { } rule)
            return plan.Refuse("", "The rule data has no rules for a new trade lane.");
        if (session.SystemSection(system.Trim()) is not { Id.Key: { } nickname } systemSection)
            return plan.Refuse(session.UniversePath ?? "", $"No [system] has the nickname {system.Trim()}.",
                detail: MissionErrors.ItemNotFound, part: "system");
        if (ScenePlans.SystemFileProblem(session, nickname, out var path) is { } fileProblem)
            return plan.Refuse(fileProblem.Source, fileProblem.Message, systemSection, part: "system");
        plan.Label = $"Create trade lane in {nickname}";

        // The ends: the anchor of each end and its name.
        var requests = new[] { start, end };
        var anchors = new Vector3[2];
        var nears = new SectionView?[2];
        foreach (var i in new[] { 0, 1 })
        {
            var part = i == 0 ? "start" : "end";
            var request = requests[i];
            if (request.Point == null && request.Near == null)
                return plan.Refuse(path, "Give start or start_near, and end or end_near.", detail: LaneEnd, part: part);
            if (request.Near is { } near)
            {
                if (SceneSections.KindOf(session, near) != SceneKind.Object ||
                    !VfsPath.Comparer.Equals(near.Path, path) || session.Get(near) is not { } nearView)
                    return plan.Refuse(path, $"{near.Id.Key ?? near.Id.ToString()} is not an object of {nickname}.", near,
                        MissionErrors.ItemNotFound, $"{part}_near");
                if (request.Point == null && SceneSections.Position(nearView) == null)
                    return plan.Refuse(path, $"{near.Id.Key} has no position. Give {part} as a point.", near,
                        MissionErrors.FieldValue, $"{part}_near");
                nears[i] = nearView;
            }
            anchors[i] = request.Point ?? SceneSections.Position(nears[i]!)!.Value;
        }
        var names = new string?[2];
        foreach (var i in new[] { 0, 1 })
        {
            names[i] = requests[i].Name?.Trim() is { Length: > 0 } given ? given : null;
            if (names[i] == null && (nears[i] == null || NameOf(nears[i]!) == null))
                return plan.Refuse(path, "Give start_name or start_near, and end_name or end_near. The HUD shows the two " +
                                         "end names of a lane.", detail: LaneEndName, part: i == 0 ? "start_name" : "end_name");
        }

        // The template ring and the archetype.
        SectionView? like = null;
        if (options.Like is { } likeRef)
        {
            if (SceneSections.KindOf(session, likeRef) != SceneKind.Object || session.Get(likeRef) is not { } likeView ||
                SceneSections.ArchetypeType(session, likeView) != RingType)
                return plan.Refuse(likeRef.Path, $"{likeRef.Id.Key ?? likeRef.Id.ToString()} is not a trade lane ring. Pick " +
                                                 "a ring with the archetype type TRADELANE_RING.", likeRef, LaneArchetype,
                    "like");
            like = likeView;
        }
        var archetype = like != null ? SceneSections.Last(like, "archetype")! : rule.Archetype;
        if (ScenePlans.TypeOf(session, archetype) != RingType)
            return plan.Refuse(path, $"No solar archetype {archetype} of the type TRADELANE_RING is in this profile. Pick a " +
                                     "ring with like.", detail: LaneArchetype, part: "like");

        // The end points, with the clearance at an object.
        var placement = new LanePlacement(session, path);
        var points = new Vector3[2];
        foreach (var i in new[] { 0, 1 })
        {
            if (requests[i].Point is { } point)
            {
                points[i] = point;
                continue;
            }
            var toward = anchors[1 - i] - anchors[i];
            if (toward.LengthSquared() < 1)
            {
                points[i] = anchors[i];
                continue;
            }
            var direction = Vector3.Normalize(toward);
            var clear = (SceneSections.RadiusOf(session, nears[i]!) ?? 0) + rule.Clearance;
            points[i] = placement.Clear(anchors[i] + direction * clear, direction, rule.Clearance,
                toward.Length() - clear);
        }
        // Two objects closer than their two clearances give ends that pass each other. The start name would then show
        // at the far end of the lane.
        if (requests.Any(r => r.Point == null) && Vector3.DistanceSquared(anchors[0], anchors[1]) >= 1 &&
            Vector3.Dot(points[1] - points[0], anchors[1] - anchors[0]) <= 0)
            return plan.Refuse(path, "The ends are too close for the clearance at their objects. The start ring passes the " +
                                     "end ring. Give start and end as points, or pick objects that are farther apart.",
                detail: LaneEnd, part: requests[1].Point == null ? "end_near" : "start_near");

        // The rings.
        var length = Vector3.Distance(points[0], points[1]);
        var count = options.Rings ?? Math.Clamp(
            (int)Math.Round(length / rule.Spacing, MidpointRounding.AwayFromZero) + 1, MinRings, MaxRings);
        var spacing = count > 1 ? length / (count - 1) : length;
        if (count is < MinRings or > MaxRings || spacing is < MinSpacing or > MaxSpacing)
            return plan.Refuse(path, RingsRefusal(length, count, spacing), detail: LaneRings, part: "rings");
        var binary = session.IsBinaryFile(path);
        var stem = options.Nickname?.Trim() is { Length: > 0 } wanted ? wanted : $"{nickname}_Trade_Lane_Ring";
        if (PatchFormatter.KeyError(stem + "_1", binary) is { } stemError)
            return plan.Refuse(path, stemError, detail: MissionErrors.NameInvalid, part: "nickname");
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rings = new List<SectionRef>();
        for (var i = 0; i < count; i++)
        {
            var name = session.Nicknames.FirstFree(stem, 1, reserved);
            reserved.Add(name);
            rings.Add(new SectionRef(path, new SectionIdentity(SceneSections.ObjectType, name)));
        }
        var positions = Enumerable.Range(0, count)
            .Select(i => Round(Vector3.Lerp(points[0], points[1], (float)i / (count - 1)))).ToList();
        var rotation = RotationOf(positions[^1] - positions[0]);
        var reputation = options.Faction?.Trim() is { Length: > 0 } faction ? faction
            : like != null && SceneSections.Last(like, "reputation") is { } likeFaction ? likeFaction
            : ConnectionPlans.LocalFaction(session, path);
        var house = rule.HouseOf(reputation);
        var loadout = like != null && SceneSections.Last(like, "loadout") is { } likeLoadout ? likeLoadout : house?.Loadout;
        var pilot = like != null && SceneSections.Last(like, "pilot") is { } likePilot ? likePilot : house?.Pilot;

        var problems = new List<string>();
        var texts = new SectionCopies(session, problems.Add);
        var commands = new List<EditCommand>();
        var warnings = new List<Diagnostic>();
        for (var i = 0; i < count; i++)
        {
            var id = rings[i].Id;
            var lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nickname"] = id.Key!,
                ["ids_name"] = like?.Lines("ids_name").LastOrDefault(l => l.Values.Count > 0) is { } likeName
                    ? ValueText(likeName.Values)
                    : rule.Name.ToString(CultureInfo.InvariantCulture),
                ["pos"] = ScenePlans.PositionText(positions[i]),
                ["rotate"] = RotationText(rotation),
                ["archetype"] = archetype,
                ["ids_info"] = like?.Lines("ids_info").LastOrDefault(l => l.Values.Count > 0) is { } likeInfo
                    ? ValueText(likeInfo.Values)
                    : rule.Infocard.ToString(CultureInfo.InvariantCulture),
            };
            if (i > 0)
                lines["prev_ring"] = rings[i - 1].Id.Key!;
            if (i < count - 1)
                lines["next_ring"] = rings[i + 1].Id.Key!;
            foreach (var line in rule.Lines)
                lines[line.Key] = line.Value!;
            foreach (var key in LaneRule.RingKeys)
            {
                if (like?.Lines(key).LastOrDefault(l => l.Values.Count > 0) is { } likeLine)
                    lines[key] = ValueText(likeLine.Values);
            }
            foreach (var (side, at) in new[] { (0, 0), (1, count - 1) })
            {
                if (i != at)
                    continue;
                if (names[side] is { } text)
                {
                    if (texts.New(StringKind.String, text, id, "tradelane_space_name") is { } placeholder)
                        lines["tradelane_space_name"] = placeholder.ToString()!;
                }
                else
                    lines["tradelane_space_name"] = NameOf(nears[side]!)!;
            }
            foreach (var (key, value) in new[] { ("reputation", reputation), ("loadout", loadout), ("pilot", pilot) })
            {
                if (value != null)
                    lines[key] = value;
            }
            if (ScenePlans.Entries(RingOrder.Where(lines.ContainsKey).Select(k => (k, lines[k])), binary,
                    out var lineError) is not { } entries)
                return plan.Refuse(path, lineError!);
            commands.Add(EditCommand.AddSection(path, SceneSections.ObjectType, entries));
            if (placement.Inside(positions[i]) is { } inside)
                warnings.Add(PlacementWarning(rings[i], inside));
        }
        if (problems.Count > 0)
            return plan.Refuse(path, problems[0]);
        if (spacing < rule.RetailMin || spacing > rule.RetailMax)
            warnings.Insert(0, Diagnostic.Warning(path, $"The rings of the lane are {ConnectionChecks.Meters(spacing)} m apart. " +
                                                        $"Retail lanes have {rule.RetailMin:0} to {rule.RetailMax:0} m between " +
                                                        "two rings. Give another number of rings, or move an end.") with
            {
                Target = rings[0], Key = "pos",
            });

        // The lane zone.
        SectionRef? zone = null;
        if (options.Zone)
        {
            var zoneName = session.Nicknames.FirstFree($"Zone_{nickname}_{LaneZones.Word}", 1, reserved);
            var box = ZoneBox(positions[0], positions[^1], rule);
            var zoneLines = new List<(string, string)>
            {
                ("nickname", zoneName), ("pos", box.Position), ("rotate", box.Rotation), ("shape", "BOX"),
                ("size", box.Size), ("lane_id", zoneName[(zoneName.LastIndexOf('_') + 1)..]),
            };
            zoneLines.AddRange(rule.Zone.Select(l => (l.Key, l.Value!)));
            if (ScenePlans.Entries(zoneLines, binary, out var zoneError) is not { } zoneEntries)
                return plan.Refuse(path, zoneError!);
            commands.Add(EditCommand.AddSection(path, SceneSections.ZoneType, zoneEntries));
            zone = new SectionRef(path, new SectionIdentity(SceneSections.ZoneType, zoneName));
        }

        plan.Rings = rings;
        plan.Zone = zone;
        plan.Spacing = spacing;
        plan.Adds = zone is { } added ? [.. rings, added] : rings;
        plan.Warnings = warnings;
        plan.SelectAfter = rings[0];
        plan.Command = EditCommand.Group(plan.Label, [.. commands, .. texts.Commands], session.Snapshot());
        return plan;
    }

    /// <summary>
    /// The text of a refusal of a ring count or a spacing outside the limits. It gives the length of the lane, the
    /// spacing of the count and the counts that fit the length. When no count fits, it gives the distances between the
    /// ends that fit.
    /// </summary>
    /// <param name="length">The distance between the first ring and the last ring in meters.</param>
    /// <param name="count">The ring count of the request, or the count that the spacing of the rule data gives for the
    /// length.</param>
    /// <param name="spacing">The distance between two rings of that count in meters.</param>
    private static string RingsRefusal(float length, int count, float spacing)
    {
        var fewest = (int)Math.Clamp(MathF.Ceiling(length / MaxSpacing) + 1, MinRings, MaxRings + 1);
        var most = (int)Math.Clamp(MathF.Floor(length / MinSpacing) + 1, MinRings - 1, MaxRings);
        var fit = fewest <= most
            ? $"Give {fewest} to {most} rings."
            : $"Give two ends that are {MinSpacing:0} to {(MaxRings - 1) * MaxSpacing:0} m apart.";
        return $"A trade lane needs {MinRings} to {MaxRings} rings, with {MinSpacing:0} to {MaxSpacing:0} m between " +
               $"two rings. The lane is {ConnectionChecks.Meters(length)} m long, so {count} rings stand " +
               $"{ConnectionChecks.Meters(spacing)} m apart. {fit}";
    }

    /// <summary>
    /// Plans a new ring after a ring of a lane, or before its first ring, as one entry of the edit history. The new ring
    /// takes the links of its place, and it copies the lines of the ring that it goes next to.
    /// </summary>
    /// <remarks>After the last ring or before the first ring, the lane gets longer. The end name moves to the new end
    /// ring, and the lane zone follows the new ends. The default position is the middle between the two rings, or one
    /// spacing beyond the end. The plan refuses an object that is not a ring of a lane, and a ring before that is not
    /// the first ring. For a ring without a position, the plan needs <paramref name="position"/>.</remarks>
    /// <param name="ring">The ring that the new ring goes after or before.</param>
    /// <param name="position">The position of the new ring, or null for the default position.</param>
    /// <param name="nickname">The nickname of the new ring, or null for the next free ring nickname.</param>
    public static LanePlan InsertRing(EditSession session, SectionRef ring, LanePlace place, Vector3? position = null,
        string? nickname = null)
    {
        var part = place == LanePlace.After ? "after" : "before";
        var plan = new LanePlan($"Insert ring {part} {ring.Id.Key}");
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (RingView(session, ring) is not { } view)
            return NotRing(plan, ring, part);
        if (session.Lists.RuleData.Builders.Lane is not { } rule)
            return plan.Refuse("", "The rule data has no rules for a new trade lane.");
        var lane = LaneOf(session, ring)!;
        var at = IndexOf(lane.Rings, ring);
        if (place == LanePlace.Before && (lane.Loops || at != 0))
            return plan.Refuse(ring.Path, "before takes only the first ring of a lane. Use after for another ring.", ring,
                BeforeNotFirst, "before");
        if (view.ReadOnlyReason is { } reason)
            return plan.Refuse(ring.Path, $"{ring.Id.Key}: {reason}", ring, part: part);
        // The neighbors come from the lane, so a link to an object that is no ring and a stray link do not count.
        var prev = place == LanePlace.After ? ring : null;
        var next = place == LanePlace.Before ? ring
            : at + 1 < lane.Rings.Count ? lane.Rings[at + 1]
            : lane.Loops ? lane.Rings[0]
            : null;
        if (next is { } nextRing && session.Get(nextRing)?.ReadOnlyReason is { } nextReason)
            return plan.Refuse(ring.Path, $"{nextRing.Id.Key}: {nextReason}", nextRing, part: part);
        // A given position needs no position of the ring. The rotation then comes from the neighbors of the place.
        var at0 = SceneSections.Position(view);
        if (position == null && at0 == null)
            return plan.Refuse(ring.Path, $"{ring.Id.Key} has no position. Give it a pos line, then insert the ring. An " +
                                          "agent can also give the position of the new ring.", ring,
                MissionErrors.FieldValue, "position");

        // The position of the new ring.
        Vector3 placeAt;
        if (position is { } given)
            placeAt = given;
        else if (place == LanePlace.After && next != null && PositionOf(session, next) is { } nextAt)
            placeAt = (at0!.Value + nextAt) / 2;
        else
        {
            // One spacing beyond the end, away from the neighbor of the end ring.
            var end = at0!.Value;
            var neighbor = place == LanePlace.After ? at > 0 ? lane.Rings[at - 1] : null
                : lane.Rings.Count > 1 ? lane.Rings[1] : null;
            var outward = neighbor != null && PositionOf(session, neighbor) is { } other && Vector3.Distance(end, other) >= 1
                ? end - other
                : Forward(view) * (place == LanePlace.After ? 1 : -1) * rule.Spacing;
            placeAt = end + outward;
        }
        placeAt = Round(placeAt);

        // The nickname of the new ring.
        string name;
        if (nickname?.Trim() is { Length: > 0 } wanted)
        {
            if (PatchFormatter.KeyError(wanted, session.IsBinaryFile(ring.Path)) is { } keyError)
                return plan.Refuse(ring.Path, keyError, detail: MissionErrors.NameInvalid, part: "nickname");
            if (session.Nicknames.Taken(wanted) is { } taken)
                return plan.Refuse(ring.Path, $"The nickname {wanted} is taken: {taken.Name} has the same nickname or the " +
                                              "same FLHash.", taken.Section, MissionErrors.NameTaken, "nickname");
            name = wanted;
        }
        else
            name = session.Nicknames.CopyName(ring.Id.Key!);
        var added = new SectionRef(ring.Path, new SectionIdentity(SceneSections.ObjectType, name));

        // The rotation looks along the lane at the place of the new ring.
        var from = prev != null ? PositionOf(session, prev) ?? placeAt : placeAt;
        var to = next != null ? PositionOf(session, next) ?? placeAt : placeAt;
        var rotation = Vector3.Distance(from, to) >= 1 ? RotationText(RotationOf(to - from))
            : view.Lines("rotate").LastOrDefault(l => l.Values.Count > 0) is { } rotate ? ValueText(rotate.Values)
            : null;

        // The end name moves to a new end ring.
        var newEnd = !lane.Loops && lane.Rings.Count > 1 &&
                     (place == LanePlace.Before || place == LanePlace.After && next == null);
        var endName = newEnd ? view.Lines("tradelane_space_name").LastOrDefault(l => l.Values.Count > 0) : null;

        var lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nickname"] = name, ["pos"] = ScenePlans.PositionText(placeAt),
        };
        if (rotation != null)
            lines["rotate"] = rotation;
        foreach (var key in CopiedKeys)
        {
            if (view.Lines(key).LastOrDefault(l => l.Values.Count > 0) is { } line)
                lines[key] = ValueText(line.Values);
        }
        if (prev != null)
            lines["prev_ring"] = prev.Id.Key!;
        if (next != null)
            lines["next_ring"] = next.Id.Key!;
        if (endName != null)
            lines["tradelane_space_name"] = ValueText(endName.Values);
        if (ScenePlans.Entries(RingOrder.Where(lines.ContainsKey).Select(k => (k, lines[k])),
                session.IsBinaryFile(ring.Path), out var lineError) is not { } entries)
            return plan.Refuse(ring.Path, lineError!);

        var commands = new List<EditCommand> { EditCommand.AddSection(ring.Path, SceneSections.ObjectType, entries) };
        var changes = new List<SectionRef>();
        if (prev != null)
        {
            commands.Add(EditCommand.SetLinesFromText(prev, "next_ring", [name]));
            changes.Add(prev);
        }
        if (next != null)
        {
            commands.Add(EditCommand.SetLinesFromText(next, "prev_ring", [name]));
            changes.Add(next);
        }
        if (endName != null)
            commands.Add(EditCommand.RemoveName(ring, "tradelane_space_name"));

        var order = lane.Rings.ToList();
        order.Insert(place == LanePlace.After ? at + 1 : 0, added);
        var warnings = new List<Diagnostic>();
        SectionRef? zone = null;
        if (!lane.Loops && lane.Rings.Count > 1 && (place == LanePlace.Before || next == null) &&
            ZoneOf(session, lane) is { } laneZone)
        {
            var first = place == LanePlace.Before ? placeAt : PositionOf(session, lane.Rings[0]);
            var last = place == LanePlace.After ? placeAt : PositionOf(session, lane.Rings[^1]);
            if (first is { } a && last is { } b)
                commands.AddRange(Follow(session, laneZone, a, b, LengthOf(session, lane), rule, changes, warnings));
            zone = changes.Contains(laneZone) ? laneZone : null;
        }
        if (new LanePlacement(session, ring.Path).Inside(placeAt) is { } inside)
            warnings.Add(PlacementWarning(added, inside));

        plan.Ring = added;
        plan.Rings = order;
        plan.Index = order.IndexOf(added) + 1;
        plan.Count = order.Count;
        plan.Zone = zone;
        plan.Adds = [added];
        plan.Changes = changes.Distinct().ToList();
        plan.Warnings = warnings;
        plan.SelectAfter = added;
        plan.Command = EditCommand.Group(plan.Label, commands, session.Snapshot());
        return plan;
    }

    /// <summary>
    /// Plans a repair of the links of a lane, as one entry of the edit history. The plan finds the rings of the lane
    /// from any ring. They are the rings that the links reach in both directions. They are also the rings with a broken
    /// link near the line of the two end rings (see <see cref="LineReach"/>). A ring with a broken link brings the rings
    /// that its own links reach. The plan sorts the rings along the line, writes their <c>prev_ring</c> and <c>next_ring</c> lines
    /// again, and moves the end names to the two end rings. It keeps the positions. A lane with good links gives a
    /// command that changes nothing.
    /// </summary>
    /// <remarks>A ring without links joins the nearest lane whose line passes it. A ring without links and without a
    /// lane near it changes nothing. The plan refuses an object that is not a ring of a lane, and rings that do not
    /// stand on one line within <see cref="Reach"/>.</remarks>
    public static LanePlan Repair(EditSession session, SectionRef ring)
    {
        var plan = new LanePlan($"Repair trade lane of {ring.Id.Key}");
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (RingView(session, ring) is null)
            return NotRing(plan, ring, "ring");
        var objects = ObjectsOf(session, ring.Path);
        var set = Linked(session, objects, ring);
        if (set.Count == 1 && PositionOf(session, ring) is { } alone)
        {
            // A ring without links joins the nearest lane whose line passes it.
            var near = objects.Values.Where(r => !r.Equals(ring) && IsRingObject(session, r))
                .Select(r => LaneOf(session, r)!).Where(l => l.Rings.Count > 1 && !l.Loops).DistinctBy(l => l.Rings[0])
                .Select(l => (Lane: l, A: PositionOf(session, l.Rings[0]), B: PositionOf(session, l.Rings[^1])))
                .Where(c => c is { A: not null, B: not null })
                .Select(c => (c.Lane, Distance: LineReach(alone, c.A!.Value, c.B!.Value),
                    Segment: SegmentDistance(alone, c.A!.Value, c.B!.Value)))
                .Where(c => c.Distance <= Reach).OrderBy(c => c.Distance).ThenBy(c => c.Segment)
                .Select(c => c.Lane).FirstOrDefault();
            if (near != null)
                set = [.. Linked(session, objects, near.Rings[0]), ring];
        }
        var positions = new Dictionary<SectionRef, Vector3>();
        var sorted = new List<SectionRef>();
        // Adds rings with their positions, or gives the refusal of a ring without a position.
        LanePlan? Add(IEnumerable<SectionRef> rings)
        {
            foreach (var item in rings)
            {
                if (PositionOf(session, item) is not { } at)
                    return plan.Refuse(item.Path, $"{item.Id.Key} has no position. Give it a pos line, then repair the lane.",
                        item, MissionErrors.FieldValue, "ring");
                positions[item] = at;
                sorted.Add(item);
            }
            return null;
        }
        if (Add(set) is { } unplaced)
            return unplaced;
        var oldLane = LaneOf(session, ring)!;
        if (sorted.Count > 1)
        {
            // The two rings that stand farthest apart are the ends of the line.
            var (e1, e2) = (sorted[0], sorted[1]);
            foreach (var a in sorted)
            {
                foreach (var b in sorted)
                {
                    if (Vector3.DistanceSquared(positions[a], positions[b]) > Vector3.DistanceSquared(positions[e1], positions[e2]))
                        (e1, e2) = (a, b);
                }
            }
            var p1 = positions[e1];
            var p2 = positions[e2];
            var broken = objects.Values.Where(r => !set.Contains(r) && IsRingObject(session, r) &&
                                                   PositionOf(session, r) is { } at && LineReach(at, p1, p2) <= Reach &&
                                                   Broken(session, objects, r)).ToList();
            foreach (var other in broken)
            {
                if (Add(Linked(session, objects, other).Where(set.Add).ToList()) is { } joinedUnplaced)
                    return joinedUnplaced;
            }
            if (sorted.FirstOrDefault(r => LineDistance(positions[r], p1, p2) > Reach) is { } off)
                return plan.Refuse(ring.Path, $"The rings of the lane of {ring.Id.Key} do not stand on one line. Move the " +
                                              "rings, or delete the lane and make it again.", off, LaneNotLine, "ring");
            var axis = p2 - p1;
            sorted = sorted.OrderBy(r => Vector3.Dot(positions[r] - p1, axis)).ToList();
            if (Direction(session, sorted) < 0)
                sorted.Reverse();
        }

        // The links and the end names in the new order.
        var names = sorted.Select(r => session.Get(r)!).Select(v => v.Lines("tradelane_space_name")
            .LastOrDefault(l => l.Values.Count > 0)).ToList();
        var named = names.Select((n, i) => (Name: n, Index: i)).Where(n => n.Name != null).ToList();
        EntryView? startName = null, endName = null;
        if (named.Count >= 2)
            (startName, endName) = (named[0].Name, named[^1].Name);
        else if (named.Count == 1 && sorted.Count > 1)
        {
            if (named[0].Index * 2 < sorted.Count)
                startName = named[0].Name;
            else
                endName = named[0].Name;
        }
        else if (named.Count == 1)
            startName = named[0].Name;
        var commands = new List<EditCommand>();
        var changes = new List<SectionRef>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var item = sorted[i];
            var itemView = session.Get(item)!;
            var wanted = new List<(string Key, string? Value)>
            {
                ("prev_ring", i > 0 ? sorted[i - 1].Id.Key : null),
                ("next_ring", i < sorted.Count - 1 ? sorted[i + 1].Id.Key : null),
                ("tradelane_space_name", i == 0 && startName != null ? ValueText(startName.Values)
                    : i == sorted.Count - 1 && endName != null ? ValueText(endName.Values)
                    : null),
            };
            var edits = new List<EditCommand>();
            foreach (var (key, value) in wanted)
            {
                var lines = itemView.Lines(key);
                var current = lines.LastOrDefault(l => l.Values.Count > 0) is { } line ? ValueText(line.Values) : null;
                if (value == null ? lines.Count == 0 : lines.Count == 1 && string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
                    continue;
                edits.Add(value == null ? EditCommand.RemoveName(item, key) : EditCommand.SetLinesFromText(item, key, [value]));
            }
            if (edits.Count == 0)
                continue;
            if (itemView.ReadOnlyReason is { } reason)
                return plan.Refuse(item.Path, $"{item.Id.Key}: {reason}", item, part: "ring");
            commands.AddRange(edits);
            changes.Add(item);
        }

        // The lane zone follows the new ends.
        var warnings = new List<Diagnostic>();
        SectionRef? zone = null;
        if (sorted.Count > 1 && session.Lists.RuleData.Builders.Lane is { } rule)
        {
            var newLane = new LaneChain(ring.Path, sorted, false);
            zone = ZoneOf(session, newLane);
            if (zone == null && ZoneOf(session, oldLane) is { } oldZone)
            {
                zone = oldZone;
                commands.AddRange(Follow(session, oldZone, positions[sorted[0]], positions[sorted[^1]],
                    LengthOf(session, oldLane), rule, changes, warnings));
            }
        }

        plan.Rings = sorted;
        plan.Zone = zone;
        plan.Changes = changes;
        plan.Warnings = warnings;
        plan.SelectAfter = ring;
        plan.Command = EditCommand.Group(plan.Label, commands, session.Snapshot());
        return plan;
    }

    /// <summary>
    /// Plans the remove of rings from their lanes, as one entry of the edit history (see
    /// <see cref="ScenePlans.Delete(EditSession, IReadOnlyList{SectionRef})"/>, which runs the same plan for a ring).
    /// The neighbors of each removed ring link to each other. A removed end ring makes its neighbor the new end, and the
    /// end name moves to it. The lane zone follows the new ends. When one ring or none stays, the plan deletes the whole
    /// lane with its zone, and it asks first.
    /// </summary>
    /// <remarks>The plan refuses an object that is not a ring of a lane. A lane zone that the editor cannot change
    /// stays, with a warning.</remarks>
    public static ScenePlan RemoveRings(EditSession session, IReadOnlyList<SectionRef> rings)
    {
        foreach (var ring in rings)
        {
            if (RingView(session, ring) is null)
                return NotRingDelete(ring);
        }
        return ScenePlans.Delete(session, rings);
    }

    /// <summary>
    /// Plans the delete of the lane of a ring: all its rings and its lane zone, as one entry of the edit history. The
    /// plan always asks first. Its question names the lane by its first ring, the ring count, and if no lane zone goes.
    /// The list of the items that go too names the lane zone. The plan refuses an object that is not a ring of a lane.
    /// </summary>
    public static ScenePlan Delete(EditSession session, SectionRef ring)
    {
        if (RingView(session, ring) is null)
            return NotRingDelete(ring);
        var lane = LaneOf(session, ring)!;
        var plan = ScenePlans.Delete(session, lane.Rings);
        if (!plan.CanRun)
            return plan;
        plan.NeedsConfirmation = true;
        var zoneGoes = ZoneOf(session, lane) is { } zone && plan.Deletes.Any(d => d.Section.Equals(zone));
        plan.Question = $"This deletes the whole trade lane of {lane.Rings[0].Id.Key} with " +
                        (lane.Rings.Count == 1 ? "its ring." : $"its {lane.Rings.Count} rings.") +
                        (zoneGoes ? "" : " No lane zone goes with it.");
        return plan;
    }

    /// <summary>
    /// The lane of a ring: the rings that the links reach from it, in order. The walk follows <c>prev_ring</c> back to
    /// the first ring and <c>next_ring</c> on to the last ring, each direction alone, as the HUD does. It stops at a link
    /// to an object that the file does not have or that is no ring. It also stops at a ring of its own walk, and at a
    /// stray link unless <paramref name="strays"/> is true. Null for a section that is no object of a system file.
    /// </summary>
    /// <remarks>
    /// <para>This is the one walk of the links of a lane in W. The plans, the checks and the Inspector use it. A ring is
    /// an object with an archetype of the type <c>TRADELANE_RING</c>, because the game makes lanes only from such
    /// objects. So a link to a jump gate or a sun does not make the gate or the sun a part of the lane.</para>
    /// <para>A stray link names a ring that links both ways with another ring on that side. For example, a stray ring
    /// names the third ring of a lane in its <c>next_ring</c> line. The third ring names the second ring in its
    /// <c>prev_ring</c> line, and the second ring names it back. The stray ring is then no part of that lane, and a plan
    /// for the stray ring does not change the lane.</para>
    /// </remarks>
    /// <param name="strays">True to follow a stray link too, as the HUD does. A check for a lane that goes in a circle
    /// needs it. A stray link can make the walk of the HUD go in a circle.</param>
    public static LaneChain? LaneOf(EditSession session, SectionRef ring, bool strays = false)
    {
        if (SceneSections.KindOf(session, ring) != SceneKind.Object || session.Get(ring) is not { } view)
            return null;
        var objects = ObjectsOf(session, ring.Path);
        var (before, backLoop) = Walk(session, objects, ring, view, "prev_ring", strays);
        var (after, onLoop) = Walk(session, objects, ring, view, "next_ring", strays);
        // Each ring is once in the chain, so the part after the ring stops at a ring of the part before it.
        var behind = before.ToHashSet();
        before.Reverse();
        return new LaneChain(ring.Path, [.. before, .. after.Skip(1).TakeWhile(r => !behind.Contains(r))],
            backLoop >= 0 || onLoop >= 0);
    }

    /// <summary>
    /// The circle that the walk of the HUD goes round from a ring, also with the stray links, or null. The walk to
    /// <c>next_ring</c> counts first, then the walk to <c>prev_ring</c>. A ring before the circle gives the circle that its
    /// walk goes into. Null for a section that is no object of a system file, and for a lane without a circle.
    /// </summary>
    internal static LaneCircle? CircleOf(EditSession session, SectionRef ring)
    {
        if (SceneSections.KindOf(session, ring) != SceneKind.Object || session.Get(ring) is not { } view)
            return null;
        var objects = ObjectsOf(session, ring.Path);
        foreach (var (key, back) in new[] { ("next_ring", "prev_ring"), ("prev_ring", "next_ring") })
        {
            var (walk, loop) = Walk(session, objects, ring, view, key, strays: true);
            if (loop < 0)
                continue;
            var rings = walk.Skip(loop).ToList();
            var stray = rings.Where((r, i) => IsStray(session, objects, r, rings[(i + 1) % rings.Count], key, back)).ToList();
            return new LaneCircle(rings, key, stray);
        }
        return null;
    }

    /// <summary>
    /// The walk of the links of a ring in one direction: the ring, then each ring that its link lines
    /// <paramref name="key"/> reach. The walk stops at a link to an object that is no ring, at a stray link unless
    /// <paramref name="strays"/> is true, and at a ring of its own walk. Loop is the index of that ring in the walk, or -1
    /// when the walk does not go in a circle.
    /// </summary>
    private static (List<SectionRef> Rings, int Loop) Walk(EditSession session,
        IReadOnlyDictionary<string, SectionRef> objects, SectionRef ring, SectionView view, string key, bool strays)
    {
        var back = key == "next_ring" ? "prev_ring" : "next_ring";
        var rings = new List<SectionRef> { ring };
        var index = new Dictionary<SectionRef, int> { [ring] = 0 };
        for (var (at, atView) = (ring, view); RingLink(session, objects, atView, key) is { } target;
             (at, atView) = (target, session.Get(target)!))
        {
            if (!strays && IsStray(session, objects, at, target, key, back))
                break;
            if (index.TryGetValue(target, out var loop))
                return (rings, loop);
            index[target] = rings.Count;
            rings.Add(target);
        }
        return (rings, -1);
    }

    /// <summary>
    /// The lane zone of a lane. It is a zone of its file that meets the lane zone rule (see <see cref="LaneZones"/>).
    /// Its axis covers the first and the last ring within <see cref="Reach"/>. Of two such zones, the zone nearer to the
    /// axis counts. Null for a lane without such a zone, for a lane of one ring and for a lane that goes in a circle. The
    /// plans then change no zone.
    /// </summary>
    public static SectionRef? ZoneOf(EditSession session, LaneChain lane)
    {
        if (lane.Loops || lane.Rings.Count < 2 || PositionOf(session, lane.Rings[0]) is not { } a ||
            PositionOf(session, lane.Rings[^1]) is not { } b)
            return null;
        SectionRef? best = null;
        var nearest = float.MaxValue;
        foreach (var zone in session.Sections(lane.Path))
        {
            if (!SceneSections.Is(zone.Id.Type, SceneSections.ZoneType) || LaneZones.Read(session, zone) is not { } read ||
                Cover(read, a, b) is not { } distance || distance >= nearest)
                continue;
            best = zone;
            nearest = distance;
        }
        return best;
    }

    /// <summary>
    /// The values of the <c>pos</c>, <c>rotate</c> and <c>size</c> lines of a new lane zone for a lane, as
    /// <see cref="Create"/> writes them, or null for a lane without two end rings with positions and for rule data
    /// without lane rules. The completeness of a lane without a zone gives these values.
    /// </summary>
    internal static (string Position, string Rotation, string Size)? NewZoneBox(EditSession session, LaneChain lane) =>
        !lane.Loops && lane.Rings.Count > 1 && PositionOf(session, lane.Rings[0]) is { } first &&
        PositionOf(session, lane.Rings[^1]) is { } last && session.Lists.RuleData.Builders.Lane is { } rule
            ? ZoneBox(first, last, rule)
            : null;

    /// <summary>
    /// The values of the <c>pos</c>, <c>rotate</c> and <c>size</c> lines of a lane zone between two end rings. The box
    /// stands at the middle of the lane, and its z axis goes along the lane. It is longer than the lane by the margin of
    /// the rule. Its x size and its y size are at most the width of the rule and at most one eleventh of its z size, so
    /// it meets the lane zone rule.
    /// </summary>
    private static (string Position, string Rotation, string Size) ZoneBox(Vector3 first, Vector3 last, LaneRule rule)
    {
        var z = MathF.Round(Vector3.Distance(first, last) + rule.ZoneMargin);
        var width = MathF.Min(rule.ZoneWidth, MathF.Floor(z / (LaneZones.Thinness + 1)));
        return (ScenePlans.PositionText((first + last) / 2), RotationText(RotationOf(last - first)),
            SizeText(width, width, z));
    }

    /// <summary>
    /// The refusal of a delete for a one-way link. A link line of an object that stays names a ring that goes, and the
    /// ring does not link back. The relink of the lane does not change such a line. The text names only fixes that work. A repair
    /// works only when the rings of the object stand on one line, so the text names the repair only then.
    /// </summary>
    internal static string OneWayLinkRefusal(EditSession session, SectionRef referrer, string key, string ring)
    {
        var own = referrer.Id.Key ?? referrer.Id.ToString();
        var text = $"{own} links to {ring} in its {key} line, but {ring} does not link back to {own}.";
        if (session.Get(referrer)?.ReadOnlyReason is { } reason)
            return $"{text} Keep {ring}, because the editor cannot change {own}: {reason}";
        return Repair(session, referrer).CanRun
            ? $"{text} Use trade_lane_repair on {own} first, delete {own} in the same delete, or keep {ring}."
            : $"{text} Delete {own} in the same delete, remove its {key} line in the raw INI block, or keep {ring}.";
    }

    /// <summary>The warning of a ring inside a zone with damage or inside an object.</summary>
    internal static Diagnostic PlacementWarning(SectionRef ring, SectionRef other) =>
        Diagnostic.Warning(ring.Path, $"{ring.Id.Key} stands inside {other.Id.Key}. Ships on the lane can take damage or " +
                                      "hit it. Move the ring.") with { Target = ring, Key = "pos" };

    /// <summary>
    /// The total distance of the first and the last ring of a lane from the axis of a box zone (see
    /// <see cref="AxisDistance"/>). Null when the zone does not cover both rings.
    /// </summary>
    internal static float? Cover(UniverseSchema.Zone zone, Vector3 first, Vector3 last) =>
        AxisDistance(zone, first) is { } a && AxisDistance(zone, last) is { } b ? a + b : null;

    /// <summary>
    /// The distance of a point from the axis of a box zone, or null when the zone does not cover the point within
    /// <see cref="Reach"/>. The axis is the z axis of the zone after its rotation. The zone covers a point near the axis
    /// and within the length of the zone plus <see cref="Reach"/>.
    /// </summary>
    internal static float? AxisDistance(UniverseSchema.Zone zone, Vector3 point)
    {
        var inverse = Matrix4x4.Transpose(ZoneShapes.RotationOf(zone.Rotate));
        var local = Vector3.TransformNormal(point - (zone.Pos ?? Vector3.Zero), inverse);
        var off = MathF.Sqrt(local.X * local.X + local.Y * local.Y);
        return off > Reach || MathF.Abs(local.Z) > (zone.Size ?? Vector3.One).Z / 2 + Reach ? null : off;
    }

    /// <summary>
    /// The commands that make a lane zone follow the new ends of its lane. The zone goes to the middle of the end rings,
    /// and its length keeps its margin beyond the lane. Its width and height shrink when the zone would break the lane
    /// zone rule. Its rotation turns to the lane when the lane turns more than 1 degree. A zone that the editor
    /// cannot change gets a warning and no command.
    /// </summary>
    /// <param name="oldLength">The length of the lane before the edit, for the margin of the zone, or null.</param>
    /// <param name="changes">Gets the zone when a command changes it.</param>
    /// <param name="warnings">Gets the warning of a zone that the editor cannot change.</param>
    internal static List<EditCommand> Follow(EditSession session, SectionRef zone, Vector3 first, Vector3 last,
        float? oldLength, LaneRule rule, List<SectionRef> changes, List<Diagnostic> warnings)
    {
        var result = new List<EditCommand>();
        if (session.Get(zone) is not { } view || ZoneShapes.Read(session, zone) is not { } read)
            return result;
        var size = read.Size ?? Vector3.One;
        var length = Vector3.Distance(first, last);
        var margin = oldLength is { } old && size.Z - old >= 0 ? size.Z - old : rule.ZoneMargin;
        var z = MathF.Round(length + margin);
        var cap = MathF.Floor(z / (LaneZones.Thinness + 1));
        var center = Round((first + last) / 2);
        if (Vector3.Distance(read.Pos ?? Vector3.Zero, center) >= 0.5f)
            result.Add(EditCommand.SetLinesFromText(zone, "pos", [ScenePlans.PositionText(center)]));
        var width = MathF.Floor(MathF.Min(size.X, cap));
        var height = MathF.Floor(MathF.Min(size.Y, cap));
        if (MathF.Abs(size.Z - z) >= 0.5f || width < size.X || height < size.Y)
            result.Add(EditCommand.SetLinesFromText(zone, "size", [SizeText(width, height, z)]));
        var axis = Vector3.TransformNormal(Vector3.UnitZ, ZoneShapes.RotationOf(read.Rotate));
        if (length >= 1 && MathF.Abs(Vector3.Dot(axis, Vector3.Normalize(last - first))) < MathF.Cos(MathF.PI / 180))
            result.Add(EditCommand.SetLinesFromText(zone, "rotate", [RotationText(RotationOf(last - first))]));
        if (result.Count == 0)
            return result;
        if (view.ReadOnlyReason is { } reason)
        {
            warnings.Add(Diagnostic.Warning(zone.Path, $"The lane zone {zone.Id.Key} stays as it is. The editor cannot change " +
                                                       $"it: {reason} Lane traffic can spawn away from the lane.") with
            {
                Target = zone,
            });
            return [];
        }
        changes.Add(zone);
        return result;
    }

    /// <summary>The rotation in degrees whose front looks along a direction, by the rule of the engine
    /// (<see cref="MathHelper.MatrixFromEulerDegrees(Vector3)"/>): the pitch, the yaw and no roll. The front of an
    /// object is its negative z axis.</summary>
    internal static Vector3 RotationOf(Vector3 direction)
    {
        var flat = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        var pitch = MathF.Atan2(direction.Y, flat) * 180 / MathF.PI;
        var yaw = MathF.Atan2(-direction.X, -direction.Z) * 180 / MathF.PI;
        return new Vector3(pitch, yaw <= -179.995f ? 180 : yaw, 0);
    }

    /// <summary>The value of a <c>rotate</c> line: each angle with at most two decimals.</summary>
    internal static string RotationText(Vector3 degrees) => string.Join(", ",
        new[] { degrees.X, degrees.Y, degrees.Z }.Select(v => MathF.Round(v, 2) is var r && r == 0 ? "0"
            : r.ToString("0.##", CultureInfo.InvariantCulture)));

    /// <summary>True for a key that links a ring to its neighbor: <c>prev_ring</c> or <c>next_ring</c>.</summary>
    internal static bool IsLink(string key) =>
        key.Equals("prev_ring", StringComparison.OrdinalIgnoreCase) || key.Equals("next_ring", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ring of a lane that a section is, with its view, or null for another section. A ring is an object of
    /// a system file with an archetype of the type <c>TRADELANE_RING</c> (see <see cref="IsRingObject"/>). So an object
    /// of another type with a link line, for example a sun, is no ring, and each plan refuses it.</summary>
    internal static SectionView? RingView(EditSession session, SectionRef ring) =>
        SceneSections.KindOf(session, ring) == SceneKind.Object && session.Get(ring) is { } view &&
        SceneSections.ArchetypeType(session, view) == RingType
            ? view
            : null;

    /// <summary>The objects of a system file by their nickname, without letter case.</summary>
    internal static Dictionary<string, SectionRef> ObjectsOf(EditSession session, string path)
    {
        var result = new Dictionary<string, SectionRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in session.Sections(path))
        {
            if (SceneSections.Is(section.Id.Type, SceneSections.ObjectType) && section.Id.Key is { } key)
                result.TryAdd(key, section);
        }
        return result;
    }

    /// <summary>True when the link of a ring to a target ring is a stray link (see <see cref="LaneOf"/>). The link line
    /// of the target on the other side names another ring, and that ring links back to the target.</summary>
    /// <param name="key">The link line of the ring that names the target.</param>
    /// <param name="back">The link line of the target on the side of the ring.</param>
    private static bool IsStray(EditSession session, IReadOnlyDictionary<string, SectionRef> objects, SectionRef ring,
        SectionRef target, string key, string back) =>
        RingLink(session, objects, session.Get(target)!, back) is { } other &&
        !string.Equals(other.Id.Key, ring.Id.Key, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SceneSections.Last(session.Get(other)!, key), target.Id.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ring that a link line of a ring names, or null when the file has no such object or the object is no
    /// ring.</summary>
    private static SectionRef? RingLink(EditSession session, IReadOnlyDictionary<string, SectionRef> objects,
        SectionView view, string key) =>
        SceneSections.Last(view, key) is { } name && objects.TryGetValue(name, out var target) && IsRingObject(session, target)
            ? target
            : null;

    /// <summary>The position of a section of W, or null.</summary>
    internal static Vector3? PositionOf(EditSession session, SectionRef section) =>
        session.Get(section) is { } view ? SceneSections.Position(view) : null;

    /// <summary>A point with whole numbers, as the <c>pos</c> line of a new item has them.</summary>
    private static Vector3 Round(Vector3 point) => new(MathF.Round(point.X), MathF.Round(point.Y), MathF.Round(point.Z));

    /// <summary>The value of a <c>size</c> line of a box in whole numbers.</summary>
    private static string SizeText(float x, float y, float z) => string.Join(", ",
        new[] { x, y, z }.Select(v => ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture)));

    /// <summary>The text of the values of a line, as a patch line writes it.</summary>
    private static string ValueText(IReadOnlyList<LibreLancer.Data.Ini.ValueBase> values) =>
        string.Join(", ", values.Select(v => v.ToString()!.Trim()));

    /// <summary>The value of the <c>ids_name</c> line of an object as text, or null when it has no name.</summary>
    private static string? NameOf(SectionView view) =>
        SceneSections.Last(view, "ids_name") is { } name && name != "0" ? name : null;

    /// <summary>The length of a lane from its first to its last ring, or null.</summary>
    private static float? LengthOf(EditSession session, LaneChain lane) =>
        PositionOf(session, lane.Rings[0]) is { } a && PositionOf(session, lane.Rings[^1]) is { } b
            ? Vector3.Distance(a, b)
            : null;

    /// <summary>The front of an object from its <c>rotate</c> line, or the negative z axis.</summary>
    private static Vector3 Forward(SectionView view) =>
        view.Lines("rotate").LastOrDefault(l => l.Values.Count >= 3)?.Values is { } r && r[0].TryToSingle(out var x) &&
        r[1].TryToSingle(out var y) && r[2].TryToSingle(out var z)
            ? Vector3.TransformNormal(-Vector3.UnitZ, MathHelper.MatrixFromEulerDegrees(new Vector3(x, y, z)))
            : -Vector3.UnitZ;

    private static int IndexOf(IReadOnlyList<SectionRef> rings, SectionRef ring)
    {
        for (var i = 0; i < rings.Count; i++)
        {
            if (rings[i].Equals(ring))
                return i;
        }
        return -1;
    }

    /// <summary>True for an object with an archetype of the type <c>TRADELANE_RING</c>. The game makes lanes only from
    /// such objects.</summary>
    internal static bool IsRingObject(EditSession session, SectionRef section) =>
        session.Get(section) is { } view && SceneSections.ArchetypeType(session, view) == RingType;

    /// <summary>
    /// The rings that the links reach from a ring, in both directions. A ring joins when a link line of a ring names
    /// it, and when its link line names a ring. Only the first ring and objects with an archetype of the type
    /// <c>TRADELANE_RING</c> join.
    /// </summary>
    private static HashSet<SectionRef> Linked(EditSession session, IReadOnlyDictionary<string, SectionRef> objects,
        SectionRef ring)
    {
        var rings = objects.Values.Where(r => IsRingObject(session, r)).ToList();
        var set = new HashSet<SectionRef> { ring };
        var queue = new Queue<SectionRef>([ring]);
        while (queue.TryDequeue(out var current))
        {
            var view = session.Get(current)!;
            var reached = new List<SectionRef>();
            foreach (var key in new[] { "prev_ring", "next_ring" })
            {
                if (RingLink(session, objects, view, key) is { } target)
                    reached.Add(target);
            }
            reached.AddRange(rings.Where(r => session.Get(r) is { } other &&
                                              new[] { "prev_ring", "next_ring" }.Any(k =>
                                                  string.Equals(SceneSections.Last(other, k), current.Id.Key,
                                                      StringComparison.OrdinalIgnoreCase))));
            foreach (var next in reached.Where(set.Add))
                queue.Enqueue(next);
        }
        return set;
    }

    /// <summary>True for a ring with a broken link. The ring has no link, or a link that does not go both ways. Or a link
    /// names an object that the file does not have or that is no ring.</summary>
    private static bool Broken(EditSession session, IReadOnlyDictionary<string, SectionRef> objects, SectionRef ring)
    {
        var view = session.Get(ring)!;
        var prev = SceneSections.Last(view, "prev_ring");
        var next = SceneSections.Last(view, "next_ring");
        if (prev == null && next == null)
            return true;
        foreach (var (name, back) in new[] { (prev, "next_ring"), (next, "prev_ring") })
        {
            if (name == null)
                continue;
            if (!objects.TryGetValue(name, out var target) || !IsRingObject(session, target) ||
                !string.Equals(SceneSections.Last(session.Get(target)!, back), ring.Id.Key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The count of the links that go forward in an order of rings, less the count that go back.</summary>
    private static int Direction(EditSession session, IReadOnlyList<SectionRef> order)
    {
        var score = 0;
        for (var i = 0; i + 1 < order.Count; i++)
        {
            var a = session.Get(order[i])!;
            var b = session.Get(order[i + 1])!;
            bool Names(SectionView view, string key, SectionRef target) =>
                string.Equals(SceneSections.Last(view, key), target.Id.Key, StringComparison.OrdinalIgnoreCase);
            score += (Names(a, "next_ring", order[i + 1]) ? 1 : 0) + (Names(b, "prev_ring", order[i]) ? 1 : 0) -
                     (Names(b, "next_ring", order[i]) ? 1 : 0) - (Names(a, "prev_ring", order[i + 1]) ? 1 : 0);
        }
        if (score == 0 && order.Count > 1 && SceneSections.Last(session.Get(order[0])!, "prev_ring") != null &&
            SceneSections.Last(session.Get(order[^1])!, "prev_ring") == null)
            return -1;
        return score;
    }

    /// <summary>The distance of a point from the segment between two points.</summary>
    private static float SegmentDistance(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(point - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(point, a + ab * t);
    }

    /// <summary>
    /// The distance of a point from the line through the two end rings of a lane. Null when the point stands more than
    /// <see cref="MaxSpacing"/> beyond an end ring along the line. A ring stands at most that far from its
    /// neighbor, so a ring farther beyond an end is no part of the lane. The limit also keeps a far lane on the same
    /// line apart.
    /// </summary>
    private static float? LineReach(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var length = ab.Length();
        var along = length < 1e-3f ? 0 : Vector3.Dot(point - a, ab) / length;
        return along < -MaxSpacing || along > length + MaxSpacing ? null : LineDistance(point, a, b);
    }

    /// <summary>The distance of a point from the line through two points.</summary>
    private static float LineDistance(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        return ab.LengthSquared() < 1e-6f
            ? Vector3.Distance(point, a)
            : Vector3.Distance(point, a + ab * (Vector3.Dot(point - a, ab) / ab.LengthSquared()));
    }

    private static LanePlan NotRing(LanePlan plan, SectionRef section, string part) =>
        plan.Refuse(section.Path, $"{section.Id.Key ?? section.Id.ToString()} is not a ring of a trade lane.", section,
            NotLaneRing, part);

    private static ScenePlan NotRingDelete(SectionRef section) =>
        ScenePlans.Refused(new ScenePlan(SceneAction.Delete, "Delete"),
            Diagnostic.Error(section.Path, $"{section.Id.Key ?? section.Id.ToString()} is not a ring of a trade lane.") with
            {
                Target = section, Rule = NotLaneRing,
            });
}

/// <summary>
/// The places of a system file where a ring of a lane does not belong. One place is inside a zone with damage, for
/// example the death zone of a sun. The other is inside the <c>solar_radius</c> of an object. The plans of the lanes
/// and the checks use it.
/// </summary>
internal sealed class LanePlacement(EditSession session, string path)
{
    private List<(SectionRef Section, UniverseSchema.Zone Zone)>? zones;
    private List<(SectionRef Section, Vector3 Position, float Radius)>? objects;

    /// <summary>The zones of the file with a <c>damage</c> line above 0.</summary>
    private List<(SectionRef Section, UniverseSchema.Zone Zone)> Zones => zones ??= session.Sections(path)
        .Where(s => SceneSections.Is(s.Id.Type, SceneSections.ZoneType) && session.Get(s) is { } view &&
                    view.Lines("damage").Count > 0)
        .Select(s => (Section: s, Zone: ZoneShapes.Read(session, s)))
        .Where(z => z.Zone is { Damage: > 0 })
        .Select(z => (z.Section, z.Zone!))
        .ToList();

    /// <summary>The objects of the file with a position and a <c>solar_radius</c> above 0.</summary>
    private List<(SectionRef Section, Vector3 Position, float Radius)> Objects => objects ??= session.Sections(path)
        .Where(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType))
        .Select(s => (Section: s, View: session.Get(s)))
        .Where(o => o.View != null)
        .Select(o => (o.Section, Position: SceneSections.Position(o.View!), Radius: SceneSections.RadiusOf(session, o.View!)))
        .Where(o => o.Position != null && o.Radius is > 0)
        .Select(o => (o.Section, o.Position!.Value, o.Radius!.Value))
        .ToList();

    /// <summary>The zone with damage or the object that holds a point, or null. A zone counts first.</summary>
    /// <param name="except">A section that does not count, for example the ring itself.</param>
    public SectionRef? Inside(Vector3 point, SectionRef? except = null)
    {
        foreach (var (section, zone) in Zones)
        {
            if (!section.Equals(except) && ZoneShapes.Contains(zone, point))
                return section;
        }
        foreach (var (section, position, radius) in Objects)
        {
            if (!section.Equals(except) && Vector3.Distance(point, position) < radius)
                return section;
        }
        return null;
    }

    /// <summary>
    /// Moves a point along a direction while a zone with damage holds it: past the edge of the zone, plus the
    /// clearance. The point does not move farther than the limit, so an end does not pass the other end.
    /// </summary>
    /// <param name="limit">The longest move in meters.</param>
    public Vector3 Clear(Vector3 point, Vector3 direction, float clearance, float limit)
    {
        const float step = 100;
        var moved = 0f;
        for (var pass = 0; pass < Zones.Count + 1; pass++)
        {
            if (Zones.FirstOrDefault(z => ZoneShapes.Contains(z.Zone, point)).Zone is not { } zone)
                return point;
            var t = 0f;
            while (ZoneShapes.Contains(zone, point + direction * t) && moved + t <= limit)
                t += step;
            if (moved + t > limit)
                return point;
            // The edge is between t - step and t.
            var (inside, outside) = (MathF.Max(0, t - step), t);
            for (var i = 0; i < 16; i++)
            {
                var middle = (inside + outside) / 2;
                if (ZoneShapes.Contains(zone, point + direction * middle))
                    inside = middle;
                else
                    outside = middle;
            }
            var shift = MathF.Min(outside + clearance, limit - moved);
            point += direction * shift;
            moved += shift;
        }
        return point;
    }
}
