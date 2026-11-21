using System.Collections.Generic;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>
/// The plan of an edit of a trade lane: a new lane, a new ring, or a repair of the links (see <see cref="LanePlans"/>).
/// It tells what the command adds, changes and warns about, and it names the parts that a caller shows in its result.
/// A delete of rings or of a lane gives a <see cref="Scene.ScenePlan"/>, because it follows the rules of each other
/// delete.
/// </summary>
/// <remarks>A plan reads W when it is made. Its command runs only while W and the text are as the plan read them, so
/// make the plan and run it at once.</remarks>
public sealed class LanePlan : EditPlan
{
    internal LanePlan(string label) : base(label)
    {
    }

    /// <summary>The rings of the lane after the command, from the first ring to the last ring. Empty for a refused
    /// plan.</summary>
    public IReadOnlyList<SectionRef> Rings { get; internal set; } = [];

    /// <summary>The lane zone that the command adds or changes, or null.</summary>
    public SectionRef? Zone { get; internal set; }

    /// <summary>The distance between two rings of a new lane in meters, or null for another edit.</summary>
    public float? Spacing { get; internal set; }

    /// <summary>The ring that an insert adds, or null for another edit.</summary>
    public SectionRef? Ring { get; internal set; }

    /// <summary>The place of the ring of an insert in its lane, from 1, or 0 for another edit.</summary>
    public int Index { get; internal set; }

    /// <summary>The count of the rings of the lane after an insert, or 0 for another edit.</summary>
    public int Count { get; internal set; }

    /// <summary>
    /// The part of the request that the refusal names, or null when the refusal names the request as a whole. The part
    /// is the name of a parameter of the plan as the agent tools name it, for example <c>rings</c>, <c>start_near</c>,
    /// <c>like</c> or <c>before</c>. A caller shows the refusal at that part.
    /// </summary>
    public string? RefusedPart { get; private set; }

    /// <summary>Gives the plan a refusal, so it cannot run.</summary>
    /// <param name="detail">The detail code of the error of the refusal, or null for
    /// <see cref="EditPlan.PlanRefused"/>.</param>
    /// <param name="part">The part of the request that the refusal names, or null (see <see cref="RefusedPart"/>).</param>
    internal LanePlan Refuse(string path, string message, SectionRef? target = null, string? detail = null,
        string? part = null)
    {
        Command = null;
        Refusals = [Diagnostic.Error(path, message) with { Target = target, Rule = detail }];
        RefusedPart = part;
        return this;
    }
}
