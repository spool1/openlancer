using System.Collections.Generic;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>
/// The plan of a new base (see <see cref="BasePlans.Create"/>). It tells what the command adds, changes, fixes and
/// warns about, and it names the parts that a caller shows in its result.
/// </summary>
/// <remarks>A plan reads W when it is made. Its command runs only while W and the text are as the plan read them, so
/// make the plan and run it at once.</remarks>
public sealed class BasePlan : EditPlan
{
    internal BasePlan(string label) : base(label)
    {
    }

    /// <summary>The <c>[Base]</c> of <c>universe.ini</c> that the plan adds, or null when the plan refuses.</summary>
    public SectionRef? Base { get; internal set; }

    /// <summary>The install path of the new base file, or null when the plan refuses.</summary>
    public string? File { get; internal set; }

    /// <summary>The install paths of the new files: the base file, then each copy of a room file.</summary>
    public IReadOnlyList<string> Files { get; internal set; } = [];

    /// <summary>The new <c>[MBase]</c> of <c>mBases.ini</c>, or null when the template has none.</summary>
    public SectionRef? MBase { get; internal set; }

    /// <summary>The new characters, in file order.</summary>
    public IReadOnlyList<SectionRef> Characters { get; internal set; } = [];

    /// <summary>The new news items of <c>news.ini</c>, in file order.</summary>
    public IReadOnlyList<SectionRef> News { get; internal set; } = [];

    /// <summary>The object that the games link to the base: the new station, or the planet. Null without a dock
    /// object.</summary>
    public SectionRef? DockObject { get; internal set; }

    /// <summary>The new docking ring of a planet base, or null.</summary>
    public SectionRef? Ring { get; internal set; }

    /// <summary>
    /// The part of the request that the refusal names, or null when the refusal names the request as a whole. The part
    /// is the name of a member of the request, for example <c>nickname</c>, <c>rooms</c>,
    /// <c>dock_object.archetype</c> or <c>dock_object.ring.position</c>. A caller shows the refusal at that part.
    /// </summary>
    public string? RefusedPart { get; private set; }

    /// <summary>Gives the plan a refusal, so it cannot run.</summary>
    /// <param name="detail">The detail code of the error of the refusal, or null for
    /// <see cref="EditPlan.PlanRefused"/>.</param>
    /// <param name="part">The part of the request that the refusal names, or null (see <see cref="RefusedPart"/>).</param>
    internal BasePlan Refuse(string path, string message, SectionRef? target = null, string? detail = null,
        string? part = null)
    {
        Command = null;
        Refusals = [Diagnostic.Error(path, message) with { Target = target, Rule = detail }];
        RefusedPart = part;
        return this;
    }
}
