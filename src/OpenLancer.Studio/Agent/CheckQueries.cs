using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>What a check covers (tool <c>check</c>).</summary>
internal enum CheckScope
{
    /// <summary>The errors that stop a save of the active mod.</summary>
    All,

    /// <summary>The problems of one section.</summary>
    Item,

    /// <summary>The problems of one mission script.</summary>
    Mission,

    /// <summary>The problems of one cutscene.</summary>
    Cutscene,

    /// <summary>All mods of the profile merged on the files of a retail game.</summary>
    Target,
}

/// <summary>The result of <c>check</c>: one page of the problems, the counts of all problems, and if the save gate stops
/// a save.</summary>
/// <param name="Errors">The errors of all pages.</param>
/// <param name="Warnings">The warnings of all pages.</param>
/// <param name="SaveBlocked">True when an error of the result stops a save of the active mod.</param>
/// <param name="Problems">The problems of the page.</param>
/// <param name="Total">The number of problems of all pages.</param>
/// <param name="NextCursor">The cursor of the next page, or null on the last page.</param>
internal sealed record CheckView(CheckScope Scope, int Errors, int Warnings, bool SaveBlocked, IReadOnlyList<Problem> Problems,
    int Total, string? NextCursor)
{
    /// <summary>A note about what the check covers, or null.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The tools <c>check</c> and <c>get_console</c> (section 7.1 of the MCP1 design). A data problem is a result, not a tool
/// error.
/// </summary>
internal static class CheckQueries
{
    /// <summary>The detail code of a target check that did not run.</summary>
    public const string TargetCheckFailed = "TargetCheckFailed";

    public static ToolStep Check(ToolCall call)
    {
        var session = call.Session;
        var args = call.Args;
        var baseGame = args.Flag("include_base_game");
        if (args.Text("target") is { } target)
        {
            if (args.Has("scope") && args.Text("scope") != "all" || args.Has("ref"))
                throw ToolInput.Bad("target", AgentTexts.Error("check_target_scope"));
            // The tail pages the problems. It reads only the arguments and the limits, not the session.
            call.RowLimit();
            var run = call.Host.CheckTarget(target);
            return ToolStep.Then(async cancel =>
            {
                var check = await run(cancel).ConfigureAwait(false);
                return ToolResult.Of(Of(call, CheckScope.Target, check.Problems,
                    string.Join(" ", new[] { AgentTexts.Error("check_target_note") }.Concat(check.Notes))));
            });
        }

        var scope = Enum.Parse<CheckScope>(args.Text("scope")!, ignoreCase: true);
        var reference = args.Text("ref");
        if (scope == CheckScope.All ? reference != null : reference == null)
        {
            throw ToolInput.Bad("ref", AgentTexts.Error(scope == CheckScope.All ? "check_all_ref" : "check_needs_ref",
                ("scope", args.Text("scope")!)));
        }
        IReadOnlyList<Problem> problems = scope switch
        {
            CheckScope.All => session.SaveProblems().Select(d => AgentRows.ProblemOf(session, d)).ToList(),
            CheckScope.Item => AgentRows.ProblemsOf(session, ItemRefs.ResolveSection(session, ItemRefs.Parse(reference!))),
            CheckScope.Mission => session.Missions.Queries.GetProblems(ScriptRef.Parse(reference!), baseGame),
            _ => Cutscene(session, reference!),
        };
        return ToolResult.Of(Of(call, scope, problems.Where(p => AgentRows.Shows(p, baseGame)).ToList(),
            scope == CheckScope.All ? AgentTexts.Error("check_all_note") : null));
    }

    /// <summary>The result of a list of problems: one page of the problems of the call, and the counts of all
    /// problems.</summary>
    internal static CheckView Of(ToolCall call, CheckScope scope, IReadOnlyList<Problem> problems, string? note)
    {
        var page = call.Paged(problems, p => p);
        return new CheckView(scope,
            problems.Count(p => p.Severity == DiagnosticSeverity.Error),
            problems.Count(p => p.Severity == DiagnosticSeverity.Warning),
            problems.Any(p => p.Severity == DiagnosticSeverity.Error && p.Origin != OriginKind.BaseGame),
            page.Rows, page.Total, page.NextCursor)
        {
            Note = note,
        };
    }

    /// <summary>
    /// The entries of the Console. Without <c>since</c>, the call gives the newest entries. With <c>since</c>, it gives
    /// the oldest entries after that sequence number, so an agent reads each entry once. <c>more</c> tells that the
    /// filter matches more entries than the result gives.
    /// </summary>
    public static ToolStep Console(ToolCall call)
    {
        var args = call.Args;
        var limit = call.RowLimit();
        var since = args.Whole("since");
        var entries = call.Host.Console(new ConsoleFilter(args.Text("source"),
            Enum.Parse<AgentSeverity>(args.Text("severity")!, ignoreCase: true), args.Flag("include_base_game"), since));
        if (entries == null)
            return ToolResult.Failure(EditErrorKind.NotAvailable, "EditorOnly", AgentTexts.Error("editor_only", ("tool", "get_console")));
        var page = since == null ? entries.TakeLast(limit) : entries.Take(limit);
        return ToolResult.Of(new ConsoleView(page.ToList(), entries.Count > limit));
    }

    /// <summary>The error of a target check that did not run, with the reason. Both hosts give it: the check needs the
    /// profile and the files of the target game, and it does not run without them.</summary>
    internal static EditError TargetCheckError(string reason) =>
        new(EditErrorKind.NotAvailable, TargetCheckFailed, AgentTexts.Error("check_target_failed", ("reason", reason)));

    private static IReadOnlyList<Problem> Cutscene(EditSession session, string reference)
    {
        var path = ItemRefs.ResolveCutscene(session, ItemRefs.Parse(reference));
        return session.CutsceneErrors(path).Select(d => AgentRows.ProblemOf(session, d)).ToList();
    }
}

/// <summary>The result of <c>get_console</c>: the entries, oldest first.</summary>
/// <param name="More">True when the filter matches more entries than the result gives. With <c>since</c>, call the
/// tool again with the <c>sequence</c> of the last entry.</param>
internal sealed record ConsoleView(IReadOnlyList<AgentConsoleEntry> Entries, bool More);
