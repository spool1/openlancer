using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>The Jump connection component of an object, as its section of the Inspector shows it (see
/// <see cref="ConnectionComponents.Jump"/>).</summary>
/// <param name="Kind">The kind in plain words, for example "jump gate" or "jump hole".</param>
/// <param name="System">The system that the <c>goto</c> line names, or null without a <c>goto</c> line.</param>
/// <param name="SystemSection">The <c>[system]</c> of that system in <c>universe.ini</c>, or null when W has
/// none.</param>
/// <param name="Exit">The object where the ship comes out, as the <c>goto</c> line names it, or null.</param>
/// <param name="Partner">The section of that object in W, or null when its system or the object does not exist. A
/// partner in a new system that the mod has not saved has its section too.</param>
/// <param name="PartnerKind">The kind of the partner in plain words, or null without a partner or for a partner of
/// another type.</param>
/// <param name="GoesBack">True when the <c>goto</c> line of the partner names this object back.</param>
/// <param name="TrafficZones">The traffic zones of the object, where NPCs come and go.</param>
/// <param name="Lock">The lock at the start of a new game, or null for an object that cannot take a lock: an object
/// that is no jump gate or jump hole.</param>
/// <param name="LockUnavailable">Why the lock cannot change, or null.</param>
public sealed record JumpConnectionInfo(string Kind, string? System, SectionRef? SystemSection, string? Exit,
    SectionRef? Partner, string? PartnerKind, bool GoesBack, IReadOnlyList<SectionRef> TrafficZones, JumpLock? Lock,
    string? LockUnavailable)
{
    /// <summary>True for a jump gate or a jump hole. A delete of one end deletes the pair.</summary>
    public bool IsPair => SceneSections.IsPairKind(Kind);
}

/// <summary>The Trade lane ring component of a ring, as its section of the Inspector shows it (see
/// <see cref="ConnectionComponents.Ring"/>).</summary>
/// <param name="Index">The place of the ring in its lane, from 1.</param>
/// <param name="Count">The number of rings of the lane. A ring without links is a lane of one ring.</param>
/// <param name="Loops">True when the links of the lane go in a circle. The lane then has no ends.</param>
/// <param name="Previous">The ring before this ring in the lane, or null at the start.</param>
/// <param name="Next">The ring after this ring in the lane, or null at the end.</param>
/// <param name="Zone">The lane zone that covers the lane (see <see cref="LanePlans.ZoneOf"/>), or null.</param>
public sealed record TradeLaneRingInfo(int Index, int Count, bool Loops, SectionRef? Previous, SectionRef? Next,
    SectionRef? Zone)
{
    /// <summary>True for an end ring: the first ring or the last ring of a lane that does not go in a circle. The HUD
    /// shows the end names of the end rings only, so only an end ring shows its end name.</summary>
    public bool IsEnd => !Loops && (Index == 1 || Index == Count);

    /// <summary>The place of the ring in plain words, for example "Ring 3 of 7".</summary>
    public string Place => Loops ? $"Ring {Index} of {Count}, in a circle" : $"Ring {Index} of {Count}";

    /// <summary>Why "Insert ring after" cannot run, or null.</summary>
    public string? InsertUnavailable { get; init; }

    /// <summary>Why "Repair lane" cannot run, or null.</summary>
    public string? RepairUnavailable { get; init; }

    /// <summary>Why "Delete ring" cannot run, or null.</summary>
    public string? DeleteUnavailable { get; init; }
}

/// <summary>
/// The Jump connection and Trade lane ring components of an object of a system file, for their sections of the
/// Inspector. They read W, so they show a new pair and a new lane at once, also in a system that the mod has not saved.
/// The controls of the sections call the same plans as the agent tools: <see cref="ConnectionPlans.SetLock"/> and the
/// plans of <see cref="LanePlans"/>.
/// </summary>
public static class ConnectionComponents
{
    /// <summary>The reason of "Repair lane" when a repair changes nothing.</summary>
    public const string NothingToRepair =
        "The links of the lane go both ways, and the end names stand at the ends. A repair has nothing to change.";

    /// <summary>The text of the info icon of the Jump connection component (AGENTS.md UI rule 10).</summary>
    public static readonly HelpText JumpHelp = new("Jump connection",
        "A jump gate or a jump hole and its partner in the destination system. The player jumps from one end to the " +
        "other end, so each end names the other end in its goto line.",
        "The goto line of each end, the gate lock lines of the initial world file, and a traffic zone at each end. The " +
        "agent tool connection_create makes a pair. A delete of one end deletes the pair with its traffic zones and its " +
        "lock lines.");

    /// <summary>The text of the info icon of the Trade lane ring component (AGENTS.md UI rule 10).</summary>
    public static readonly HelpText RingHelp = new("Trade lane ring",
        "One ring of a trade lane. Ships fly from ring to ring along the lane. The HUD shows the names of the two end " +
        "rings as the label of the lane.",
        "The prev_ring and next_ring lines that link the rings, the tradelane_space_name line of each end ring, and the " +
        "lane zone where lane traffic spawns. The buttons of this component and the agent tools trade_lane_insert_ring, " +
        "trade_lane_repair and delete_items keep the links both ways.");

    /// <summary>
    /// The Jump connection component of an object: an object with a <c>goto</c> line or an archetype of a jump type.
    /// Null for another section.
    /// </summary>
    public static JumpConnectionInfo? Jump(EditSession session, SectionRef section)
    {
        if (SceneSections.KindOf(session, section) != SceneKind.Object || session.Get(section) is not { } view ||
            section.Id.Key is not { } name)
            return null;
        var type = SceneSections.ArchetypeType(session, view);
        if (!ObjectComponents.HasJumpConnection(view, type))
            return null;
        var line = view.Lines("goto").LastOrDefault(l => l.Values.Count > 0)?.Values;
        var system = line is { Count: > 0 } && line[0].ToString().Trim() is { Length: > 0 } s ? s : null;
        var exit = line is { Count: > 1 } && line[1].ToString().Trim() is { Length: > 0 } e ? e : null;
        var partner = ConnectionPlans.PartnerOf(session, view);
        var own = session.SystemNicknameOf(section.Path);
        var locked = type is "jump_gate" or "jump_hole" ? GateLocks.LockOf(session, name) : (JumpLock?)null;
        return new JumpConnectionInfo(SceneSections.ConnectionKind(session, view) ?? "jump gate", system,
            system == null ? null : session.SystemSection(system), exit, partner?.Section,
            partner is { } found ? ConnectionChecks.KindWords(SceneSections.ArchetypeType(session, found.View)) : null,
            partner is { } back && own != null && ConnectionPlans.GoesBack(session, back.View, own, name),
            ConnectionPlans.PairParts(session, section, view)?.Zones ?? [], locked,
            locked == null ? null : session.ReadOnlyReason);
    }

    /// <summary>
    /// The Trade lane ring component of a ring: an object with an archetype of the type <c>TRADELANE_RING</c>, in its
    /// lane as <see cref="LanePlans.LaneOf"/> walks it. Null for another section, also for an object of another type
    /// with a link line, because the game makes no lane from it. A ring that the editor cannot change gives that reason
    /// for each control. Else each "unavailable" reason comes from the plan of its control.
    /// </summary>
    public static TradeLaneRingInfo? Ring(EditSession session, SectionRef section)
    {
        if (SceneSections.KindOf(session, section) != SceneKind.Object || !LanePlans.IsRingObject(session, section) ||
            LanePlans.LaneOf(session, section) is not { } lane)
            return null;
        var rings = lane.Rings;
        var at = rings.ToList().IndexOf(section);
        var info = new TradeLaneRingInfo(at + 1, rings.Count, lane.Loops,
            at > 0 ? rings[at - 1] : lane.Loops ? rings[^1] : null,
            at < rings.Count - 1 ? rings[at + 1] : lane.Loops ? rings[0] : null,
            LanePlans.ZoneOf(session, lane));
        // A ring that the editor cannot change takes no control, also when a plan would change only other rings.
        if ((session.ReadOnlyReason ?? (session.Get(section)?.ReadOnlyReason is { } reason
                ? $"{section.Id.Key}: {reason}"
                : null)) is { } locked)
            return info with { InsertUnavailable = locked, RepairUnavailable = locked, DeleteUnavailable = locked };
        var repair = LanePlans.Repair(session, section);
        return info with
        {
            InsertUnavailable = Unavailable(LanePlans.InsertRing(session, section, LanePlace.After)),
            RepairUnavailable = Unavailable(repair) ?? (repair.Changes.Count == 0 ? NothingToRepair : null),
            DeleteUnavailable = ScenePlans.Unavailable(session, SceneAction.Delete, section),
        };
    }

    /// <summary>The first refusal of a plan, or null for a plan that can run.</summary>
    private static string? Unavailable(EditPlan plan) => plan.CanRun ? null : plan.Refusals.FirstOrDefault()?.Message;
}
