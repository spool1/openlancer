using System.Collections.Generic;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>
/// The plan of one session command (section 4.0.8 of the M6b design). A command of the command layer uses it when it
/// wraps an edit that has no plan class of its own. Examples are the add of a component and a value of a cutscene
/// element. The plan adds and deletes no item. It names the sections that it changes when its maker gives them.
/// </summary>
public sealed class CommandPlan : EditPlan
{
    /// <summary>The plan of a session command. Its label is the label of the command.</summary>
    /// <param name="changes">The sections that the command changes and keeps, or null.</param>
    public CommandPlan(EditCommand command, IReadOnlyList<SectionRef>? changes = null) : base(command.Label)
    {
        Command = command;
        Changes = changes ?? [];
    }

    private CommandPlan(string label, Diagnostic refusal) : base(label)
    {
        Refusals = [refusal];
    }

    /// <summary>A plan that cannot run, with the reason.</summary>
    /// <param name="label">The label of the edit that cannot run.</param>
    /// <param name="message">Why the edit cannot run, in the strict form of Simplified Technical English.</param>
    /// <param name="target">The section that the reason is about, or null.</param>
    public static CommandPlan Refused(string label, string message, SectionRef? target = null) =>
        new(label, Diagnostic.Error(target?.Path ?? "", message) with { Target = target });
}
