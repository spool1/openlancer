using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>What a request did (section 4.0.6 of the M6b design).</summary>
public enum EditStatus
{
    /// <summary>The request changed data, and the history has one new entry.</summary>
    Applied,

    /// <summary>The request ran, but no state changed. The history has no new entry.</summary>
    NoChange,

    /// <summary>A preview: the request ran and gave its result, then the states before came back.</summary>
    Previewed,

    /// <summary>The request did not run. <see cref="EditResult.Errors"/> tells why. Nothing changed.</summary>
    Refused,
}

/// <summary>An item that a request removes.</summary>
/// <param name="Ref">The item reference.</param>
/// <param name="Why">A target of the request, or an item that goes with a target.</param>
/// <param name="Cause">For an item that goes with a target: the line that names the target. Null for a target.</param>
public sealed record ResultDelete(string Ref, DeleteReason Why, string? Cause);

/// <summary>An item that a request gives a new name, with the old and the new reference.</summary>
public sealed record ResultRename(string From, string To);

/// <summary>A safe fix of a delete (decision D81): the lines that go with the item that goes.</summary>
/// <param name="Ref">The reference of the section of the lines.</param>
/// <param name="Key">The key of the first line.</param>
/// <param name="Text">What the fix does.</param>
public sealed record ResultFix(string Ref, string Key, string Text);

/// <summary>A line that shows "Missing" after a request.</summary>
/// <param name="Ref">The reference of the section of the line.</param>
/// <param name="Key">The key of the line.</param>
/// <param name="Consequence">What the game does with the line.</param>
/// <param name="BlocksSave">True when the line stops the save until a fix.</param>
public sealed record ResultMissing(string Ref, string Key, string Consequence, bool BlocksSave);

/// <summary>
/// A problem that a check finds (section 4.0.6 of the M6b design). It is data, not an error. Its message is the
/// message of the check, so the agent and the user read the same text.
/// </summary>
/// <param name="Severity">An error or a warning.</param>
/// <param name="Message">The message of the check.</param>
public sealed record Problem(DiagnosticSeverity Severity, string Message)
{
    /// <summary>The reference of the item or the line, or null.</summary>
    public string? Ref { get; init; }

    /// <summary>The id of the rule, for example <c>M05</c>, or null.</summary>
    public string? Rule { get; init; }

    /// <summary>Who causes the problem (decision D551 of the M6a design).</summary>
    public OriginKind Origin { get; init; } = OriginKind.Unknown;

    /// <summary>The mod of the origin <see cref="OriginKind.Mod"/>, else null.</summary>
    public string? ModId { get; init; }

    /// <summary>The ids of the fixes of the problem.</summary>
    public IReadOnlyList<string> Fixes { get; init; } = [];

    /// <summary>The problem of a diagnostic of a check. An origin that waits for the comparison with the base game
    /// (<see cref="OriginKind.Pending"/>) is not known yet, so the problem gives <see cref="OriginKind.Unknown"/>.
    /// Section 4.0.6 of the M6b design has no pending origin.</summary>
    /// <param name="reference">The reference of the item or the line, or null.</param>
    public static Problem From(Diagnostic diagnostic, string? reference) => new(diagnostic.Severity, diagnostic.Message)
    {
        Ref = reference,
        Rule = diagnostic.Rule,
        Origin = diagnostic.Origin?.Kind is { } kind and not OriginKind.Pending ? kind : OriginKind.Unknown,
        ModId = diagnostic.Origin?.ModId,
    };
}

/// <summary>
/// The one result of a command or a batch of the command layer (section 4.0.6 of the M6b design). Items are item
/// references (see <see cref="ItemRefs"/>), so a caller can pass a result into its next request. Its JSON form uses
/// snake case (see <see cref="EditJson"/>).
/// </summary>
public record EditResult(EditStatus Status)
{
    /// <summary>The errors of a refusal.</summary>
    public IReadOnlyList<EditError> Errors { get; init; } = [];

    /// <summary>The label of the request.</summary>
    public string? Label { get; init; }

    /// <summary>The label of the history entry, or null when the request added no entry.</summary>
    public string? UndoLabel { get; init; }

    /// <summary>The revision of the session after the request (see <see cref="EditSession.Revision"/>). JSON writes it
    /// as text.</summary>
    [JsonPropertyName("rev")]
    [JsonConverter(typeof(EditJson.LongAsText))]
    public long Revision { get; init; }

    /// <summary>The versions of the touched mission scripts, by install path.</summary>
    public IReadOnlyDictionary<string, long> Versions { get; init; } = new Dictionary<string, long>();

    /// <summary>The install paths of the touched files.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The references of the new items.</summary>
    public IReadOnlyList<string> Adds { get; init; } = [];

    /// <summary>The references of the changed items.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>The removed items.</summary>
    public IReadOnlyList<ResultDelete> Deletes { get; init; } = [];

    /// <summary>The renamed items.</summary>
    public IReadOnlyList<ResultRename> Renames { get; init; } = [];

    /// <summary>The safe fixes of a delete.</summary>
    public IReadOnlyList<ResultFix> Fixes { get; init; } = [];

    /// <summary>The lines that show "Missing" after the request.</summary>
    public IReadOnlyList<ResultMissing> Missing { get; init; } = [];

    /// <summary>The warnings of the plan.</summary>
    public IReadOnlyList<Problem> Warnings { get; init; } = [];

    /// <summary>The parts of the plan that refused while the rest ran, for example one item of a delete.</summary>
    public IReadOnlyList<Problem> Refusals { get; init; } = [];

    /// <summary>The files that name a renamed or deleted item as text.</summary>
    public IReadOnlyList<NamedMention> Mentions { get; init; } = [];

    /// <summary>The problems that the request adds.</summary>
    public IReadOnlyList<Problem> NewProblems { get; init; } = [];

    /// <summary>The problems that the request removes.</summary>
    public IReadOnlyList<Problem> FixedProblems { get; init; } = [];

    /// <summary>True when the request needs <c>confirm: true</c>.</summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>The item that the editor selects after the request, or null.</summary>
    public string? Select { get; init; }

    /// <summary>Notes, for example a word whose letter case changed.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The patch text of each changed file, by install path. Only a preview that asks for it has it.</summary>
    public IReadOnlyDictionary<string, string> Patch { get; init; } = new Dictionary<string, string>();

    /// <summary>One sentence that tells what the request did, or null.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// The result of the parts of a batch with a status: the lists of the parts in order. The confirmation is needed
    /// when a part needs it. The selection and the summary are the last that a part gives. A later version of a
    /// script replaces an earlier one.
    /// </summary>
    public static EditResult Merge(EditStatus status, IReadOnlyList<EditResult> parts) => new(status)
    {
        Errors = parts.SelectMany(p => p.Errors).ToList(),
        Versions = parts.SelectMany(p => p.Versions).GroupBy(v => v.Key, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, System.StringComparer.OrdinalIgnoreCase),
        Adds = parts.SelectMany(p => p.Adds).Distinct().ToList(),
        Changes = parts.SelectMany(p => p.Changes).Distinct().ToList(),
        Deletes = parts.SelectMany(p => p.Deletes).ToList(),
        Renames = parts.SelectMany(p => p.Renames).ToList(),
        Fixes = parts.SelectMany(p => p.Fixes).ToList(),
        Missing = parts.SelectMany(p => p.Missing).ToList(),
        Warnings = parts.SelectMany(p => p.Warnings).ToList(),
        Refusals = parts.SelectMany(p => p.Refusals).ToList(),
        Mentions = parts.SelectMany(p => p.Mentions).ToList(),
        NewProblems = parts.SelectMany(p => p.NewProblems).ToList(),
        FixedProblems = parts.SelectMany(p => p.FixedProblems).ToList(),
        NeedsConfirmation = parts.Any(p => p.NeedsConfirmation),
        Select = parts.Select(p => p.Select).LastOrDefault(s => s != null),
        Notes = parts.SelectMany(p => p.Notes).ToList(),
        Patch = parts.SelectMany(p => p.Patch).GroupBy(p => p.Key, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, System.StringComparer.OrdinalIgnoreCase),
        Summary = parts.Select(p => p.Summary).LastOrDefault(s => s != null),
    };

    /// <summary>
    /// The net result of the parts of a request (section 5.6 of the MCP1 design). Each reference of the result resolves
    /// after the request. A part can add an item, and a later part can change, rename or delete it. These are the
    /// rules:
    /// <list type="bullet">
    /// <item>An item that the request adds is an add with its last reference. The changes and the renames leave it out.
    /// An example is a new object with the nickname of its request.</item>
    /// <item>An item that the request adds and then deletes is not in the result, because it did not exist before the
    /// request.</item>
    /// <item>Two renames of one item are one rename from its first to its last reference. A rename back to the first
    /// reference is no rename.</item>
    /// <item>A change gives the last reference of the item. An item that the request deletes is not a change.</item>
    /// <item>A delete of an item that the request renamed gives the reference before the request, and no rename.</item>
    /// <item>The select is the item that the last part with a select selects, with its last reference. A rename of
    /// that item in a later part gives its new reference. A delete of that item gives no select, also when the request
    /// added it.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>The walk reads the parts in order. A rename changes an add only when the add came before it. So an
    /// existing item that a part renames stays a rename. This is also true when a later part adds an item with its old
    /// nickname (review finding of MCP1-2, step 4).</para>
    /// <para>In a part, the renames and the deletes name the items before the part. The adds, the changes and the
    /// select name the items after it. So the renames and the deletes apply first. Then the select of the part replaces
    /// the select of the parts before it. The last select of <see cref="Merge"/> can name an item that a later rename
    /// or delete changed (review finding of MCP1-2).</para>
    /// <para>The agent tools give one part for each plan and for each mission request. A mission request gives the net
    /// result of its commands, so it also follows these rules.</para>
    /// </remarks>
    /// <param name="result">The result with the lists of the other kinds, for example the merge of the parts.</param>
    /// <param name="parts">The parts of the request, in order.</param>
    internal static EditResult Net(EditResult result, IReadOnlyList<EditResult> parts)
    {
        var adds = new List<string>();
        var renames = new List<ResultRename>();
        var changes = new List<string>();
        var deletes = new List<ResultDelete>();
        string? select = null;
        foreach (var part in parts)
        {
            foreach (var rename in part.Renames)
            {
                if (select == rename.From)
                    select = rename.To;
                Replace(changes, rename.From, rename.To);
                if (Replace(adds, rename.From, rename.To))
                    continue;
                var at = renames.FindIndex(r => r.To == rename.From);
                if (at < 0)
                    renames.Add(rename);
                else if (renames[at].From == rename.To)
                    renames.RemoveAt(at);
                else
                    renames[at] = renames[at] with { To = rename.To };
            }
            foreach (var delete in part.Deletes)
            {
                if (select == delete.Ref)
                    select = null;
                changes.Remove(delete.Ref);
                if (adds.Remove(delete.Ref))
                    continue;
                var at = renames.FindIndex(r => r.To == delete.Ref);
                if (at < 0)
                {
                    deletes.Add(delete);
                    continue;
                }
                deletes.Add(delete with { Ref = renames[at].From });
                renames.RemoveAt(at);
            }
            foreach (var add in part.Adds.Where(add => !adds.Contains(add)))
                adds.Add(add);
            foreach (var change in part.Changes.Where(change => !adds.Contains(change) && !changes.Contains(change)))
                changes.Add(change);
            select = part.Select ?? select;
        }
        return result with { Adds = adds, Renames = renames, Changes = changes, Deletes = deletes, Select = select };
    }

    /// <summary>Replaces a reference in a list with its new reference. Gives false when the list does not have
    /// it.</summary>
    private static bool Replace(List<string> refs, string from, string to)
    {
        var at = refs.IndexOf(from);
        if (at < 0)
            return false;
        refs[at] = to;
        return true;
    }
}
