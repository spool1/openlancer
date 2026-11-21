using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>
/// The plan of an edit of the universe: a new star system, or a rename or a delete of one (see
/// <see cref="SystemPlans"/>). It tells what the command adds, changes, deletes and warns about, and it names the parts
/// that a caller shows in its result.
/// </summary>
/// <remarks>A plan reads W when it is made. Its command runs only while W and the text are as the plan read them, so
/// make the plan and run it at once.</remarks>
public sealed class UniversePlan : EditPlan
{
    internal UniversePlan(string label) : base(label)
    {
    }

    /// <summary>The <c>[system]</c> of <c>universe.ini</c> that the plan adds, renames or deletes, with its identity
    /// after a new system or a rename, or null when the plan refuses.</summary>
    public SectionRef? System { get; internal set; }

    /// <summary>The install path of the file of the system, or null when the plan refuses. For a delete, it is the file
    /// that the command deletes, or null when the system has no file.</summary>
    public string? File { get; internal set; }

    /// <summary>The sun that the plan adds at the center of a new system, or null.</summary>
    public SectionRef? Sun { get; internal set; }

    /// <summary>Gives the plan a refusal, so it cannot run.</summary>
    /// <param name="detail">The detail code of the error of the refusal, or null for
    /// <see cref="EditPlan.PlanRefused"/>.</param>
    internal UniversePlan Refuse(string path, string message, SectionRef? target = null, string? detail = null)
    {
        Command = null;
        Refusals = [Diagnostic.Error(path, message) with { Target = target, Rule = detail }];
        return this;
    }
}
