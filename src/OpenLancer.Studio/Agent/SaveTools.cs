using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>What a save of a host did (tool <c>save</c>, section 7.6 of the MCP1 design).</summary>
/// <param name="Result">The result of the save, or of the save gate when <paramref name="Blocked"/> is true.</param>
/// <param name="Number">The number of the save, or 0 when it changed no file.</param>
/// <param name="Blocked">True when the save gate found errors, so the save did not start.</param>
public sealed record AgentSave(SaveResult Result, int Number, bool Blocked)
{
    /// <summary>The validation of the saved files as the tail of the call, or null when no validation follows the save.
    /// The editor host validates a save that changed files in an oltool process. The tail does not touch the
    /// session.</summary>
    public Func<CancellationToken, Task<SaveValidation>>? Validation { get; init; }
}

/// <summary>The result of the validation of a save.</summary>
/// <param name="Save">The number of the save that the validation checked. A later save replaces the validation of an
/// earlier save, so the number can be larger than the number of the save of the call.</param>
/// <param name="Problems">The problems that the active mod adds, or null when the validation did not give a
/// result.</param>
/// <param name="Failure">Why the validation did not give a result, or null.</param>
public sealed record SaveValidation(int Save, IReadOnlyList<Diagnostic>? Problems, string? Failure);

/// <summary>The result of <c>save</c>.</summary>
/// <param name="Saved">True when the save wrote its files, also when it changed no file.</param>
/// <param name="Number">The number of the save, or 0 when it changed no file.</param>
/// <param name="Written">The files that the save wrote, relative to the folder of the mod.</param>
/// <param name="Deleted">The files that the save deleted, relative to the folder of the mod.</param>
/// <param name="Problems">The first problems of the save, errors first.</param>
/// <param name="ProblemCount">The number of all problems of the save.</param>
/// <param name="Incomplete">True when a file did not move into place. The next save completes the save.</param>
internal sealed record SaveView(bool Saved, int Number, IReadOnlyList<string> Written, IReadOnlyList<string> Deleted,
    IReadOnlyList<Problem> Problems, int ProblemCount, bool Incomplete)
{
    /// <summary>The validation of the saved files, or null when no validation follows the save.</summary>
    public ValidationView? Validation { get; init; }
}

/// <summary>The validation of a save in the result of <c>save</c>.</summary>
/// <param name="Save">The number of the save that the validation checked.</param>
/// <param name="Errors">The errors that the active mod adds.</param>
/// <param name="Warnings">The warnings that the active mod adds.</param>
/// <param name="Problems">The first problems, errors first.</param>
/// <param name="Failure">Why the validation did not give a result, or null.</param>
internal sealed record ValidationView(int Save, int Errors, int Warnings, IReadOnlyList<Problem> Problems, string? Failure);

/// <summary>
/// The tool <c>save</c> of section 7.6 of the MCP1 design (decision D713). The editor host runs the save of the editor:
/// the gate, the report in the Console, the reload and the validation. The validation is the tail of the call. The host
/// without a window runs the gate, then <see cref="EditSession.Save(bool)"/>. A save never replaces a file that changed
/// outside the host: the agent gets <c>changed_on_disk</c>, and the editor asks the user.
/// </summary>
/// <remarks>The result gives at most <see cref="ShownProblems"/> problems, so its size stays below the size limit of a
/// result. The queue replaces a larger result with the error <c>limit</c>, and that error must not hide a save that
/// wrote files.</remarks>
internal static class SaveTools
{
    /// <summary>The most problems of a result. <c>check</c> gives each problem.</summary>
    public const int ShownProblems = 50;

    /// <summary>The most errors that the message of <c>save_blocked</c> names.</summary>
    private const int ShownErrors = 3;

    public static ToolStep Save(ToolCall call)
    {
        var session = call.Session;
        EditTools.ReadOnlyGuard(call);
        var folder = session.ActiveMod!.Folder;
        AgentSave save;
        try
        {
            save = call.Host.Save();
        }
        catch (InvalidOperationException e)
        {
            throw new EditException(new EditError(EditErrorKind.Busy, MissionErrors.Busy, e.Message));
        }
        var result = save.Result;
        if (save.Blocked)
            throw Blocked(session, "Gate", result.Problems);
        if (!result.Saved && result.ChangedOnDisk.Count > 0)
        {
            throw new EditException(new EditError(EditErrorKind.ChangedOnDisk, "ChangedOnDisk", AgentTexts.Error(
                call.Host.Kind == AgentHostKind.Editor ? "changed_on_disk" : "changed_on_disk_headless",
                ("files", string.Join(", ", result.ChangedOnDisk.Select(f => Relative(folder, f)))))));
        }
        if (!result.Saved)
            throw Blocked(session, "SaveFailed", result.Problems);
        var view = new SaveView(true, save.Number, result.Written.Select(f => Relative(folder, f)).ToList(),
            result.Deleted.Select(f => Relative(folder, f)).ToList(),
            Shown(result.Problems).Select(d => AgentRows.ProblemOf(session, d)).ToList(), result.Problems.Count,
            result.Incomplete);
        if (save.Validation is not { } validation)
            return ToolResult.Of(view);
        return ToolStep.Then(async cancel =>
        {
            SaveValidation done;
            try
            {
                done = await validation(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The save wrote its files, so the result tells it also when the validation did not end in time.
                done = new SaveValidation(save.Number, null, AgentTexts.Error("save_validation_waited"));
            }
            return ToolResult.Of(view with { Validation = ValidationOf(done) });
        });
    }

    /// <summary>The error <c>save_blocked</c> with the messages of the first errors. The target is the item of the first
    /// error.</summary>
    /// <param name="detail"><c>Gate</c> for the errors of the save gate, <c>SaveFailed</c> for a save that wrote
    /// nothing for another reason.</param>
    private static EditException Blocked(EditSession session, string detail, IReadOnlyList<Diagnostic> problems)
    {
        var errors = problems.Where(p => p.Severity == DiagnosticSeverity.Error).ToList();
        var shown = string.Join(" ", errors.Take(ShownErrors).Select(e => e.Message));
        var key = detail != "Gate" ? "save_failed" : errors.Count == 1 ? "save_blocked_one" : "save_blocked";
        var message = AgentTexts.Error(key, ("count", errors.Count), ("errors", shown));
        if (errors.Count > ShownErrors)
            message += " " + AgentTexts.Error("save_blocked_more");
        return new EditException(new EditError(EditErrorKind.SaveBlocked, detail, message)
        {
            Target = errors.FirstOrDefault()?.Target is { } target ? ItemRefs.Canonical(session, target) : null,
        });
    }

    private static ValidationView ValidationOf(SaveValidation validation)
    {
        var problems = validation.Problems ?? [];
        return new ValidationView(validation.Save,
            problems.Count(p => p.Severity == DiagnosticSeverity.Error),
            problems.Count(p => p.Severity == DiagnosticSeverity.Warning),
            Shown(problems).Select(p => Problem.From(p, null)).ToList(),
            validation.Failure);
    }

    /// <summary>The first problems of a list, errors first.</summary>
    private static IEnumerable<Diagnostic> Shown(IReadOnlyList<Diagnostic> problems) =>
        problems.OrderBy(p => p.Severity == DiagnosticSeverity.Error ? 0 : 1).Take(ShownProblems);

    /// <summary>A file of the mod relative to its folder, with the separator of the install paths.</summary>
    private static string Relative(string modFolder, string path) =>
        Path.GetRelativePath(modFolder, path).Replace('/', '\\');
}
