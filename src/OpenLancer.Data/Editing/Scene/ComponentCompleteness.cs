using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The completeness of the components that link many parts (AGENTS.md UI rule 12): a jump connection and a trade lane.
/// The Inspector shows the report at the top of the page of a jump gate, a jump hole or a trade lane ring. Each step
/// reads W and the checks of the connections and the lanes, so a missing step shows the text of its check.
/// </summary>
public static class ComponentCompleteness
{
    /// <summary>The fix subject of a lane step that a repair of the lane fixes (see <see cref="LanePlans.Repair"/>). The
    /// Inspector gives it the button Repair lane.</summary>
    public const string RepairFix = "repair";

    /// <summary>The report of a section: the report of a jump gate or a jump hole, or of a trade lane ring. Null for
    /// another section.</summary>
    public static CompletenessReport? Of(EditSession session, SectionRef section) =>
        JumpConnection(session, section) ?? TradeLane(session, section);

    /// <summary>
    /// The completeness of the jump connection of a jump gate or a jump hole: the partner goes back, the kinds of the
    /// two ends match, and the system of the object has at most 10 jump objects. Null for another section, also for an
    /// airlock gate, which has no partner.
    /// </summary>
    public static CompletenessReport? JumpConnection(EditSession session, SectionRef section)
    {
        if (ConnectionComponents.Jump(session, section) is not { IsPair: true } jump || session.Get(section) is not { } view ||
            section.Id.Key is not { } name)
            return null;
        var kind = ConnectionChecks.KindWords(SceneSections.ArchetypeType(session, view));
        // Without a partner, a step goes to the system of the goto line.
        var toward = jump.Partner ?? jump.SystemSection;
        var listError = session.CheckLists(section)
            .FirstOrDefault(p => p.Key is "goto" && p.Severity == DiagnosticSeverity.Error);
        var steps = new List<CompletenessStep>
        {
            jump.Partner != null && jump.GoesBack
                ? new CompletenessStep("Partner goes back", CompletenessState.Done, PartnerReason)
                : new CompletenessStep("Partner goes back", CompletenessState.Missing, PartnerReason,
                    jump.Partner != null
                        ? ConnectionChecks.NoReturnText(name, jump.Exit!, jump.System!)
                        : listError?.Message ?? NoPartner(name, jump),
                    toward, IsError: jump.Partner == null && listError != null),
            jump.Partner != null && kind != null && kind == jump.PartnerKind
                ? new CompletenessStep("Kinds match", CompletenessState.Done, KindReason)
                : new CompletenessStep("Kinds match", CompletenessState.Missing, KindReason,
                    jump.Partner == null ? "The connection has no partner to compare."
                    : kind != null && jump.PartnerKind != null ? ConnectionChecks.KindText(name, kind, jump.Exit!, jump.PartnerKind)
                    : $"{jump.Exit} is not a {kind ?? jump.Kind}.",
                    toward),
        };
        var system = session.SystemNicknameOf(section.Path) ?? SceneSections.FileName(section.Path);
        var used = ConnectionChecks.JumpObjects(session.WorkingSections(section.Path) ?? []);
        var count = ConnectionChecks.CountProblem(session, section);
        steps.Add(new CompletenessStep($"Jump objects of {system}: {used} of {ConnectionChecks.MaxJumpObjects}",
            count == null ? CompletenessState.Done : CompletenessState.Missing,
            $"The game crashes with more than {ConnectionChecks.MaxJumpObjects} jump objects in one system.",
            count?.Message, count == null ? null : session.SystemSection(system),
            IsError: count?.Severity == DiagnosticSeverity.Error));
        return new CompletenessReport("Jump connection", steps);
    }

    /// <summary>
    /// The completeness of the trade lane of a ring: the links go both ways and do not go in a circle, both ends have
    /// names, and a lane zone covers the lane. Null for another section, also for an object of another type with a link
    /// line.
    /// </summary>
    public static CompletenessReport? TradeLane(EditSession session, SectionRef section)
    {
        if (ConnectionComponents.Ring(session, section) is not { } ring || LanePlans.LaneOf(session, section) is not { } lane)
            return null;
        var problems = LaneChecks.Problems(session, lane);
        var name = section.Id.Key;
        var repair = ring.RepairUnavailable == null ? RepairFix : null;

        CompletenessStep Links()
        {
            if (problems.FirstOrDefault(p => p.Kind is LaneProblem.Circle or LaneProblem.OneWay or LaneProblem.NotRing) is
                { Problem: { } link })
                return new CompletenessStep("Links go both ways", CompletenessState.Missing, LinkReason, link.Message,
                    link.Target, repair, link.Severity == DiagnosticSeverity.Error);
            return lane.Rings.Count < 2
                ? new CompletenessStep("Links go both ways", CompletenessState.Missing, LinkReason,
                    $"{name} links to no other ring, so it is in no lane.", null, repair)
                : new CompletenessStep("Links go both ways", CompletenessState.Done, LinkReason);
        }

        CompletenessStep Names()
        {
            if (problems.FirstOrDefault(p => p.Kind == LaneProblem.NoEndName) is { Problem: { } missing })
                return new CompletenessStep("Names at both ends", CompletenessState.Missing, NameReason, missing.Message,
                    missing.Target);
            return ring.Loops || lane.Rings.Count < 2
                ? new CompletenessStep("Names at both ends", CompletenessState.Missing, NameReason, NoEnds(ring))
                : new CompletenessStep("Names at both ends", CompletenessState.Done, NameReason);
        }

        CompletenessStep Zone()
        {
            if (ring.Zone != null)
                return new CompletenessStep("Zone covers the lane", CompletenessState.Done, ZoneReason);
            if (problems.FirstOrDefault(p => p.Kind == LaneProblem.Zone) is { Problem: { } off })
                return new CompletenessStep("Zone covers the lane", CompletenessState.Missing, ZoneReason, off.Message,
                    off.Target);
            return new CompletenessStep("Zone covers the lane", CompletenessState.Missing, ZoneReason,
                ring.Loops || lane.Rings.Count < 2
                    ? NoEnds(ring)
                    : $"No zone of the file covers the lane from {lane.Rings[0].Id.Key} to {lane.Rings[^1].Id.Key}. Add a " +
                      "box zone along the lane: in the Hierarchy, use Create > Zone > Box, then set its position, rotation " +
                      "and size. An agent uses scene_create_object and set_fields.");
        }

        return new CompletenessReport("Trade lane", [Links(), Names(), Zone()]);
    }

    private const string PartnerReason =
        "The player can jump back only when the partner names this object in its goto line.";

    private const string KindReason =
        "The game counts each end in the route table of its kind, so both ends of a pair need the same kind.";

    private const string LinkReason =
        "Ships fly from ring to ring along the links. Each link must go both ways, and the links must not go in a circle.";

    private const string NameReason = "The HUD shows the names of the two end rings as the label of the lane.";

    private const string ZoneReason = "Lane traffic spawns only in a lane zone that covers the lane.";

    /// <summary>What is missing when the <c>goto</c> line names no partner, and the list checks give no error.</summary>
    private static string NoPartner(string name, JumpConnectionInfo jump) =>
        jump.System == null ? $"{name} has no goto line."
        : jump.Exit == null ? $"The goto line of {name} names no exit object."
        : jump.SystemSection == null ? $"The system {jump.System} is not in universe.ini."
        : $"{jump.System} has no object {jump.Exit}.";

    /// <summary>What is missing in a lane without ends: a lane in a circle, or a ring without links.</summary>
    private static string NoEnds(TradeLaneRingInfo ring) =>
        ring.Loops ? "The lane goes in a circle, so it has no ends." : "A ring without links has no lane.";
}
