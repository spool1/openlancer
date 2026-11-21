using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>The kind of a problem of a trade lane (see <see cref="LaneChecks"/>). The completeness of a lane reads
/// it.</summary>
internal enum LaneProblem
{
    /// <summary>The links of the lane go in a circle.</summary>
    Circle,

    /// <summary>A link does not go both ways.</summary>
    OneWay,

    /// <summary>A link names an object that is not a trade lane ring.</summary>
    NotRing,

    /// <summary>An end ring has no end name.</summary>
    NoEndName,

    /// <summary>A ring with an end name is in no lane.</summary>
    Lone,

    /// <summary>A ring stands inside a zone with damage or inside an object.</summary>
    Placement,

    /// <summary>A lane zone does not cover its lane.</summary>
    Zone,
}

/// <summary>
/// The checks of the trade lanes: the rings of a system file and their lane zones. One error stops the save: a lane that
/// goes in a circle, when the active mod writes a link line of a ring of the circle. The game hangs when the HUD reads
/// such a lane. The other checks are warnings. Each text names a fix that works.
/// </summary>
/// <remarks>
/// <para>Retail facts: the game makes a lane only from objects with an archetype of the type <c>TRADELANE_RING</c>. A
/// ship on the lane follows <c>next_ring</c> forward and <c>prev_ring</c> back. The HUD follows the links to the two end
/// rings, also a stray link, and shows their <c>tradelane_space_name</c>. Lane traffic spawns in the lane zone (see
/// <see cref="LaneZones"/>).</para>
/// <para>The checks walk the lanes only with <see cref="LanePlans.LaneOf"/> and <see cref="LanePlans.CircleOf"/>, as the
/// HUD walks them, with the stray links. A link line that names no object gets the error of the data lists, so these
/// checks skip it. The placement warning comes only for a ring that the active mod places, as the spacing warning of the
/// jump objects.</para>
/// </remarks>
internal static class LaneChecks
{
    private const string Prev = "prev_ring";
    private const string Next = "next_ring";
    private const string EndName = "tradelane_space_name";

    /// <summary>The problems of a ring of a lane or of a lane zone, also of the lower layer. Empty for another
    /// section.</summary>
    public static IReadOnlyList<Diagnostic> Check(EditSession session, SectionRef section)
    {
        if (session.Get(section) is not { } view || section.Id.Key is not { } name)
            return [];
        return SceneSections.KindOf(session, section) switch
        {
            SceneKind.Object when LanePlans.IsRingObject(session, section) =>
                Ring(session, section, view, name).Select(p => p.Problem).ToList(),
            SceneKind.Zone => Zone(session, section).Select(p => p.Problem).ToList(),
            _ => [],
        };
    }

    /// <summary>
    /// The problems of a lane: the problems of its rings, and the problem of each lane zone of its file that does not
    /// cover the lane (see <see cref="Zone"/>). The zone of a lane covers the lane, so it gives no problem. A zone that
    /// misses the lane is not the zone of the lane, so the check reads each zone of the file.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Lane(EditSession session, LaneChain lane) =>
        Problems(session, lane).Select(p => p.Problem).ToList();

    /// <summary>The problems of a lane as <see cref="Lane"/> gives them, each with its kind.</summary>
    public static IReadOnlyList<(LaneProblem Kind, Diagnostic Problem)> Problems(EditSession session, LaneChain lane)
    {
        var rings = lane.Rings.ToHashSet();
        var zones = session.Sections(lane.Path)
            .Where(s => SceneSections.KindOf(session, s) == SceneKind.Zone)
            .SelectMany(z => Zone(session, z))
            .Where(p => p.Lane.Rings.Any(rings.Contains))
            .Select(p => (LaneProblem.Zone, p.Problem));
        return lane.Rings
            .SelectMany(r => session.Get(r) is { } view && r.Id.Key is { } name && LanePlans.IsRingObject(session, r)
                ? Ring(session, r, view, name)
                : [])
            .Concat(zones).ToList();
    }

    /// <summary>
    /// The errors of the trade lanes for the save gate, in a system file that the active mod changes. A lane that goes in
    /// a circle gives an error when the active mod writes a link line of a ring of the circle. Each circle gives one
    /// error. The error goes on the first ring of the circle in the file whose link line the active mod writes.
    /// </summary>
    public static IEnumerable<Diagnostic> SaveErrors(EditSession session, string path)
    {
        var named = new HashSet<SectionRef>();
        foreach (var ring in Rings(session, path))
        {
            if (LanePlans.CircleOf(session, ring) is not { } circle || circle.Rings.Any(named.Contains))
                continue;
            named.UnionWith(circle.Rings);
            if (Written(session, circle) is { } written)
                yield return LoopProblem(session, circle, written.Ring, written.Key, DiagnosticSeverity.Error);
        }
    }

    /// <summary>The problems of a ring, each with its kind.</summary>
    private static IEnumerable<(LaneProblem Kind, Diagnostic Problem)> Ring(EditSession session, SectionRef ring,
        SectionView view, string name)
    {
        // The walk of the HUD, with the stray links. A ring before a circle gets the problem too, because the walk of
        // the HUD from it goes round the circle.
        var walk = LanePlans.LaneOf(session, ring, strays: true)!;
        if (LanePlans.CircleOf(session, ring) is { } circle)
            yield return (LaneProblem.Circle, LoopProblem(session, circle, ring, Written(session, ring) ?? circle.Key,
                Written(session, circle) != null ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning));

        var objects = LanePlans.ObjectsOf(session, ring.Path);
        var links = 0;
        foreach (var (key, back) in new[] { (Prev, Next), (Next, Prev) })
        {
            if (SceneSections.Last(view, key) is not { } other)
                continue;
            links++;
            if (!objects.TryGetValue(other, out var target) || session.Get(target) is not { } targetView)
                continue;
            if (!LanePlans.IsRingObject(session, target))
                yield return (LaneProblem.NotRing, Warning(ring, key,
                    $"{name} links to {other}, but {other} is not a trade lane ring" +
                    (SceneSections.Last(targetView, "archetype") is { } archetype ? $". Its archetype is {archetype}." : ".") +
                    $" The game does not count {other} in the lane. {Fix(session, ring)}"));
            else if (!string.Equals(SceneSections.Last(targetView, back), name, StringComparison.OrdinalIgnoreCase))
                yield return (LaneProblem.OneWay, Warning(ring, key,
                    $"The rings {name} and {other} do not link both ways. Ships on the lane can stop or go the wrong way. " +
                    Fix(session, ring)));
        }

        var hasName = SceneSections.Last(view, EndName) != null;
        if (!walk.Loops && walk.Rings.Count > 1 && !hasName && (walk.Rings[0].Equals(ring) || walk.Rings[^1].Equals(ring)))
            yield return (LaneProblem.NoEndName, Warning(ring, EndName,
                $"The trade lane of {name} has no name at its {(walk.Rings[0].Equals(ring) ? "start" : "end")}. The HUD " +
                $"shows no lane label. Set the name in the Lane name row of {name}. An agent uses set_text with the field " +
                $"{EndName}."));

        if (links == 0 && hasName &&
            !objects.Values.Any(o => LanePlans.IsRingObject(session, o) && LinksTo(session, o, name)))
            yield return (LaneProblem.Lone,
                Warning(ring, EndName, $"{name} is a trade lane ring without a lane. " + Lone(session, ring)));

        if (session.Origin(ring, "pos").Origin == ValueOrigin.ActiveMod && SceneSections.Position(view) is { } at &&
            new LanePlacement(session, ring.Path).Inside(at, ring) is { } inside)
            yield return (LaneProblem.Placement, LanePlans.PlacementWarning(ring, inside));
    }

    /// <summary>
    /// The problem of a lane zone that does not cover its lane. The zone does not cover the first and the last ring of a
    /// lane, but its axis passes one or more rings of that lane. This is the case after a move of an end ring, or after
    /// a raw edit that adds a ring. A zone that covers the ends of a lane near it is the zone of that lane, and it gives
    /// no problem. A lane that has another zone gives no problem either. Each problem comes with the lane that it
    /// names.
    /// </summary>
    private static IEnumerable<(LaneChain Lane, Diagnostic Problem)> Zone(EditSession session, SectionRef zone)
    {
        if (LaneZones.Read(session, zone) is not { } read)
            yield break;
        var lanes = new List<LaneChain>();
        var seen = new HashSet<SectionRef>();
        foreach (var ring in Rings(session, zone.Path))
        {
            if (seen.Contains(ring) || LanePlans.PositionOf(session, ring) is not { } at ||
                LanePlans.AxisDistance(read, at) == null)
                continue;
            var lane = LanePlans.LaneOf(session, ring)!;
            seen.UnionWith(lane.Rings);
            if (lane.Rings.Count > 1 && !lane.Loops)
                lanes.Add(lane);
        }
        var zones = lanes.Select(l => LanePlans.ZoneOf(session, l)).ToList();
        if (zones.Contains(zone))
            yield break;
        for (var i = 0; i < lanes.Count; i++)
        {
            if (zones[i] == null)
                yield return (lanes[i], Warning(zone, "size",
                    $"The lane zone {zone.Id.Key} does not cover the trade lane of {lanes[i].Rings[0].Id.Key}. Lane " +
                    "traffic spawns away from the lane. Change the position, the rotation or the size of the zone so that " +
                    "it covers each ring of the lane. An agent uses set_fields."));
        }
    }

    /// <summary>The problem of a lane that goes in a circle, on a ring. The fix is a repair, or the delete of the ring
    /// that cuts the circle (see <see cref="Cut"/>).</summary>
    private static Diagnostic LoopProblem(EditSession session, LaneCircle circle, SectionRef ring, string key,
        DiagnosticSeverity severity)
    {
        var cut = Cut(session, circle).Id.Key;
        return new Diagnostic(severity, ring.Path,
            $"The trade lane of {ring.Id.Key} goes in a circle. The game hangs when the HUD reads the lane. " +
            (RepairStops(session, ring) is null
                ? $"Repair the lane with the button Repair lane in the Inspector, or delete {cut}. An agent uses " +
                  "trade_lane_repair or delete_items."
                : $"Delete {cut}. An agent uses delete_items."))
        {
            Target = ring, Key = key,
        };
    }

    /// <summary>
    /// The ring whose delete cuts a circle. It is the first ring in the file with a stray link in the circle, because the
    /// relink of a delete does not follow a stray link. Else the delete of each ring of the circle cuts it. Then it is the
    /// ring of the save error, or the first ring of the circle in the file.
    /// </summary>
    private static SectionRef Cut(EditSession session, LaneCircle circle) =>
        InFile(session, circle.Strays).FirstOrDefault() ?? Written(session, circle)?.Ring ??
        InFile(session, circle.Rings).First();

    /// <summary>The fix of a broken link of a ring: the repair of its lane. When a ring stops the repair, the fix is the
    /// move or the delete of that ring.</summary>
    private static string Fix(EditSession session, SectionRef ring) =>
        RepairStops(session, ring) is { } off
            ? $"{off.Id.Key} does not stand on the line of the lane, so a repair stops. Move {off.Id.Key} onto the line, or " +
              "delete it. An agent uses set_fields or delete_items."
            : $"Repair the lane of {ring.Id.Key} with the button Repair lane in the Inspector. An agent uses " +
              "trade_lane_repair.";

    /// <summary>The fix of a ring without a lane: a repair joins it to a lane on its line, or a delete.</summary>
    private static string Lone(EditSession session, SectionRef ring) =>
        LanePlans.Repair(session, ring) is { CanRun: true, Rings.Count: > 1 }
            ? "Join it to the lane on its line with the button Repair lane in the Inspector, or delete it. An agent uses " +
              "trade_lane_repair or delete_items."
            : "Delete it, or move it onto the line of a lane and repair that lane. An agent uses delete_items, set_fields " +
              "and trade_lane_repair.";

    /// <summary>The ring that stops a repair of the lane of a ring, because it does not stand on the line of the lane,
    /// or null. A repair that stops for another reason, for example a profile that takes no edits, gives null.</summary>
    private static SectionRef? RepairStops(EditSession session, SectionRef ring) =>
        LanePlans.Repair(session, ring) is { CanRun: false } plan &&
        plan.Refusals.FirstOrDefault(r => r.Rule == LanePlans.LaneNotLine) is { Target: { } off }
            ? off
            : null;

    /// <summary>The first link line of a ring that the active mod writes: <c>next_ring</c>, then <c>prev_ring</c>, or
    /// null. Only a line of W counts, so a line that the active mod removes does not count.</summary>
    private static string? Written(EditSession session, SectionRef ring) =>
        session.Get(ring) is { } view
            ? new[] { Next, Prev }.FirstOrDefault(k => view.Lines(k).Count > 0 &&
                                                     session.Origin(ring, k).Origin == ValueOrigin.ActiveMod)
            : null;

    /// <summary>The first ring of a circle in the file whose link line the active mod writes, with that line, or
    /// null.</summary>
    private static (SectionRef Ring, string Key)? Written(EditSession session, LaneCircle circle)
    {
        foreach (var ring in InFile(session, circle.Rings))
        {
            if (Written(session, ring) is { } key)
                return (ring, key);
        }
        return null;
    }

    /// <summary>Rings of one system file in file order.</summary>
    private static IEnumerable<SectionRef> InFile(EditSession session, IReadOnlyList<SectionRef> rings)
    {
        if (rings.Count == 0)
            return [];
        var set = rings.ToHashSet();
        return session.Sections(rings[0].Path).Where(set.Contains);
    }

    /// <summary>The rings of a system file in file order.</summary>
    private static IEnumerable<SectionRef> Rings(EditSession session, string path) =>
        session.Sections(path).Where(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) &&
                                          LanePlans.IsRingObject(session, s));

    /// <summary>True when a link line of an object names a ring.</summary>
    private static bool LinksTo(EditSession session, SectionRef section, string ring) =>
        session.Get(section) is { } view && new[] { Prev, Next }.Any(k =>
            string.Equals(SceneSections.Last(view, k), ring, StringComparison.OrdinalIgnoreCase));

    private static Diagnostic Warning(SectionRef section, string key, string message) =>
        new(DiagnosticSeverity.Warning, section.Path, message) { Target = section, Key = key };
}
