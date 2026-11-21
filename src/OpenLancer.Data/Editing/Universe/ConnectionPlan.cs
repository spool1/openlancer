using System.Collections.Generic;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>One end of a jump connection that a plan makes or changes.</summary>
/// <param name="Object">The object of the end, with its identity after the command.</param>
/// <param name="Added">True for a new object. False for an existing object that gets a <c>goto</c> line.</param>
/// <param name="Reputation">The faction of a new jump gate, or null.</param>
/// <param name="Loadout">The loadout of a new jump gate, or null.</param>
/// <param name="Pilot">The pilot of a new jump gate, or null.</param>
public sealed record ConnectionEndPlan(SectionRef Object, bool Added, string? Reputation, string? Loadout, string? Pilot);

/// <summary>The count of the jump objects of a system after a plan: the objects with a <c>goto</c> line.</summary>
/// <param name="System">The nickname of the system.</param>
/// <param name="Used">The count after the command.</param>
/// <param name="Max">The most jump objects that one system can have in the game.</param>
public sealed record JumpObjectCount(string System, int Used, int Max);

/// <summary>
/// The plan of an edit of the jump connections: a new pair or a new lock (see <see cref="ConnectionPlans"/>). It tells
/// what the command adds, changes and warns about, and it names the parts that a caller shows in its result.
/// </summary>
/// <remarks>A plan reads W when it is made. Its command runs only while W and the text are as the plan read them, so
/// make the plan and run it at once.</remarks>
public sealed class ConnectionPlan : EditPlan
{
    internal ConnectionPlan(string label) : base(label)
    {
    }

    /// <summary>The two ends of a new pair, in the order of the request. Empty for a lock and for a refused
    /// plan.</summary>
    public IReadOnlyList<ConnectionEndPlan> Ends { get; internal set; } = [];

    /// <summary>The traffic zones that the plan adds at the new ends.</summary>
    public IReadOnlyList<SectionRef> TrafficZones { get; internal set; } = [];

    /// <summary>The counts of the jump objects of the systems of a new pair after the command, in the order of the
    /// ends.</summary>
    public IReadOnlyList<JumpObjectCount> JumpObjects { get; internal set; } = [];

    /// <summary>How the route table of all connections (<c>systems_shortest_path.ini</c>) changes with the command, or
    /// null when the plan refuses.</summary>
    public RouteDiff? Routes { get; internal set; }

    /// <summary>Gives the plan a refusal, so it cannot run.</summary>
    /// <param name="detail">The detail code of the error of the refusal, or null for
    /// <see cref="EditPlan.PlanRefused"/>.</param>
    internal ConnectionPlan Refuse(string path, string message, SectionRef? target = null, string? detail = null)
    {
        Command = null;
        Refusals = [Diagnostic.Error(path, message) with { Target = target, Rule = detail }];
        return this;
    }
}
