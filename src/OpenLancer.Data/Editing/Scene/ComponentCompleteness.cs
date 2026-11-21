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

    /// <summary>The report of a section from the component models that the page of the section has, so the page makes
    /// each model once (see <see cref="ConnectionComponents"/>). Null for another section.</summary>
    public static CompletenessReport? Of(EditSession session, SectionRef section, JumpConnectionInfo? jump,
        TradeLaneRingInfo? ring) =>
        (jump != null ? JumpConnection(session, section, jump) : null) ??
        (ring != null ? TradeLane(session, section, ring) : null);

    /// <summary>
    /// The completeness of the jump connection of a jump gate or a jump hole: the partner goes back, the kinds of the
    /// two ends match, and the system of the object has at most 10 jump objects. Null for another section, also for an
    /// airlock gate, which has no partner.
    /// </summary>
    public static CompletenessReport? JumpConnection(EditSession session, SectionRef section) =>
        ConnectionComponents.Jump(session, section) is { } jump ? JumpConnection(session, section, jump) : null;

    private static CompletenessReport? JumpConnection(EditSession session, SectionRef section, JumpConnectionInfo jump)
    {
        if (!jump.IsPair || session.Get(section) is not { } view || section.Id.Key is not { } name)
            return null;
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
            Kinds(session, view, name, jump, toward),
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
    public static CompletenessReport? TradeLane(EditSession session, SectionRef section) =>
        ConnectionComponents.Ring(session, section) is { } ring ? TradeLane(session, section, ring) : null;

    private static CompletenessReport? TradeLane(EditSession session, SectionRef section, TradeLaneRingInfo ring)
    {
        if (LanePlans.LaneOf(session, section) is not { } lane)
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
            // A lane with ends goes to its first ring, where the new zone starts.
            return ring.Loops || lane.Rings.Count < 2
                ? new CompletenessStep("Zone covers the lane", CompletenessState.Missing, ZoneReason, NoEnds(ring))
                : new CompletenessStep("Zone covers the lane", CompletenessState.Missing, ZoneReason, NoZone(session, lane),
                    lane.Rings[0]);
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

    /// <summary>
    /// The step "Kinds match" of a jump connection. An object whose archetype has a type that the route tables do not
    /// count gets the text of that type, also when its partner is a jump gate or a jump hole. The step then has no
    /// "Go to", because the page shows the object.
    /// </summary>
    private static CompletenessStep Kinds(EditSession session, SectionView view, string name, JumpConnectionInfo jump,
        SectionRef? toward)
    {
        var type = SceneSections.ArchetypeType(session, view);
        var kind = ConnectionChecks.KindWords(type);
        if (jump.Partner == null)
            return Missing("The connection has no partner to compare.", toward);
        if (kind == null)
            return Missing((type == null ? $"{name} has no archetype with a type."
                : $"{name} has an archetype of the type {type.ToUpperInvariant()}.") +
                $" The route tables count only the types JUMP_GATE and JUMP_HOLE. Give {name} an archetype of one of " +
                "these types.", null);
        if (kind == jump.PartnerKind)
            return new CompletenessStep("Kinds match", CompletenessState.Done, KindReason);
        return Missing(jump.PartnerKind != null
            ? ConnectionChecks.KindText(name, kind, jump.Exit!, jump.PartnerKind)
            : $"{jump.Exit} is not {SceneSections.WithArticle(kind)}.", toward);

        static CompletenessStep Missing(string detail, SectionRef? goTo) =>
            new("Kinds match", CompletenessState.Missing, KindReason, detail, goTo);
    }

    /// <summary>
    /// What is missing in a lane without a lane zone: the steps of Create in the Hierarchy, the lane zone rule (see
    /// <see cref="LaneZones"/>) and the values of a new lane zone, as the create plan of a lane makes it (see
    /// <see cref="LanePlans.NewZoneBox"/>).
    /// </summary>
    private static string NoZone(EditSession session, LaneChain lane)
    {
        var values = LanePlans.NewZoneBox(session, lane) is { } box
            ? $" Set its position to ({box.Position}), its rotation to ({box.Rotation}) and its size to ({box.Size})."
            : " Put it on the line of the rings, with its z axis along the lane.";
        return $"No zone of the file covers the lane from {lane.Rings[0].Id.Key} to {lane.Rings[^1].Id.Key}. In the " +
               $"Hierarchy, use Create > Zone > Box. Give the new zone a nickname with the word {LaneZones.Word}, and " +
               "the letter case counts." + values + $" Its z size must stay more than {LaneZones.Thinness} times its x " +
               "size and its y size. An agent gives the nickname, the position, the rotation and the size to " +
               "scene_create_zone.";
    }

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
