using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>
/// The plan of a command (section 4.0.1 of the M6b design): what the command adds, changes and deletes, what it fixes,
/// what it refuses and what it warns about, and the session command that runs it. <see cref="ScenePlan"/> derives
/// from it.
/// </summary>
/// <remarks>A plan reads the edit view when it is made. Its session command runs only while the edit view and the text
/// are as the plan read them, so make the plan and run it at once.</remarks>
public abstract class EditPlan
{
    protected EditPlan(string label)
    {
        Label = label;
    }

    /// <summary>The label of the entry of the edit history, for example "Delete Li01_14 and 5 more".</summary>
    public string Label { get; protected internal set; }

    /// <summary>The session command, or null when the plan has a refusal.</summary>
    public EditCommand? Command { get; protected internal set; }

    /// <summary>True when the plan has a session command, so it can run. A plan with a refusal cannot run.</summary>
    public bool CanRun => Command != null;

    /// <summary>The sections that the command adds, in order.</summary>
    public IReadOnlyList<SectionRef> Adds { get; protected internal set; } = [];

    /// <summary>The sections that the command changes and keeps.</summary>
    public IReadOnlyList<SectionRef> Changes { get; protected internal set; } = [];

    /// <summary>The sections that the command removes: the targets, then the sections that go with them.</summary>
    public IReadOnlyList<PlannedDelete> Deletes { get; protected internal set; } = [];

    /// <summary>The safe fixes of a delete.</summary>
    public IReadOnlyList<PlannedFix> Fixes { get; protected internal set; } = [];

    /// <summary>The lines that show "Missing" after the command.</summary>
    public IReadOnlyList<MissingReference> Missing { get; protected internal set; } = [];

    /// <summary>The mission lines that show "Missing" after the command (section 4.10 of the M6b design). The mission
    /// editor gives them, because the rules of the lists do not cover the mission lines.</summary>
    public IReadOnlyList<ResultMissing> MissionMissing { get; protected internal set; } = [];

    /// <summary>Why the command cannot run. A plan with a refusal has no session command.</summary>
    public IReadOnlyList<Diagnostic> Refusals { get; protected internal set; } = [];

    /// <summary>The warnings of the plan.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; protected internal set; } = [];

    /// <summary>The files that name an old nickname of a rename, or a nickname of a delete, as text.</summary>
    public IReadOnlyList<NamedMention> Mentions { get; protected internal set; } = [];

    /// <summary>The section to select after the command, or null to clear the selection (decision D249).</summary>
    public SectionRef? SelectAfter { get; protected internal set; }

    /// <summary>True when the editor asks before it runs the command, and an agent must give <c>confirm: true</c>
    /// (decisions D238 and D242).</summary>
    public bool NeedsConfirmation { get; protected internal set; }

    /// <summary>The detail code of the error of a refused plan whose refusal names no rule.</summary>
    public const string PlanRefused = "PlanRefused";

    /// <summary>
    /// The part of the result that the plan gives after its run (section 4.0.8 of the M6b design): the adds, changes,
    /// deletes, fixes, "Missing" lines, warnings, refusals, mentions and the selection, as item references in their
    /// canonical form.
    /// </summary>
    /// <remarks>A plan without a session command did not run. Its result has the status
    /// <see cref="EditStatus.Refused"/>, and each refusal is an error of the kind <see cref="EditErrorKind.Refused"/>
    /// (section 4.0.6 of the M6b design). The refusals of a result are only the parts of a plan that refused while the
    /// rest ran.</remarks>
    public EditResult Report(EditSession session)
    {
        string Ref(SectionRef section) => ItemRefs.Canonical(session, section);
        string? TargetOf(Diagnostic d) => d.Target is { } t ? Ref(t) : null;
        if (Command == null)
        {
            var errors = Refusals.Select(r => new EditError(EditErrorKind.Refused, r.Rule ?? PlanRefused, r.Message)
            {
                Target = TargetOf(r),
            }).ToList();
            if (errors.Count == 0)
            {
                errors.Add(new EditError(EditErrorKind.Refused, PlanRefused,
                    $"The edit '{Label}' did not run. Make it again."));
            }
            return new EditResult(EditStatus.Refused)
            {
                Label = Label,
                Errors = errors,
                Warnings = Warnings.Select(w => Problem.From(w, TargetOf(w))).ToList(),
                NeedsConfirmation = NeedsConfirmation,
            };
        }
        return new EditResult(EditStatus.Applied)
        {
            Label = Label,
            Adds = Adds.Select(Ref).ToList(),
            Changes = Changes.Select(Ref).ToList(),
            Deletes = Deletes.Select(d => new ResultDelete(Ref(d.Section), d.Why, d.Cause)).ToList(),
            Fixes = Fixes.Select(f => new ResultFix(Ref(f.Section), f.Key, f.Text)).ToList(),
            Missing = Missing.Select(m => new ResultMissing(Ref(m.Reference.Section), m.Reference.Key, m.Consequence,
                m.BlocksSave)).Concat(MissionMissing).ToList(),
            Warnings = Warnings.Select(w => Problem.From(w, TargetOf(w))).ToList(),
            Refusals = Refusals.Select(r => Problem.From(r, TargetOf(r))).ToList(),
            Mentions = Mentions,
            NeedsConfirmation = NeedsConfirmation,
            Select = SelectAfter is { } select ? Ref(select) : null,
        };
    }
}
