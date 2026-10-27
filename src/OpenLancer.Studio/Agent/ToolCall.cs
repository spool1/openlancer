using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>One page of a list result (section 7.0 of the MCP1 design).</summary>
/// <param name="Rows">The rows of the page.</param>
/// <param name="Total">The number of rows of all pages.</param>
/// <param name="NextCursor">The cursor of the next page, or null on the last page.</param>
internal sealed record Page<T>(IReadOnlyList<T> Rows, int Total, string? NextCursor);

/// <summary>
/// The context of the session part of one call: the host, the session and the checked arguments. It runs on the
/// thread that owns the session.
/// </summary>
internal sealed class ToolCall(AgentHost host, string tool, ToolArgs args)
{
    private ItemTexts? texts;

    public AgentHost Host { get; } = host;

    /// <summary>The name of the tool, for example <c>set_fields</c>.</summary>
    public string Tool { get; } = tool;

    /// <summary>The session of the open profile.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotAvailable"/> while no profile is open.</exception>
    public EditSession Session => Host.Session ?? throw new EditException(new EditError(EditErrorKind.NotAvailable,
        "NoProfile", AgentTexts.Error("no_profile")));

    public ToolArgs Args { get; } = args;

    /// <summary>The batch of the <c>edit_batch</c> call whose step this call is, or null for a call of the agent. A step
    /// runs its commands in that batch (section 7.6 of the MCP1 design).</summary>
    public EditScope? Scope { get; init; }

    public AgentLimits Limits => Host.Limits;

    /// <summary>The names and other texts of the items of the session.</summary>
    public ItemTexts Texts => texts ??= new ItemTexts(Session, Host.NameText, Host.InfoText);

    /// <summary>
    /// One page of rows: from the <c>cursor</c> argument, at most the <c>limit</c> argument or the default rows of the
    /// limits. The rows are made only for the page.
    /// </summary>
    /// <param name="rows">All rows, as cheap items. <paramref name="make"/> makes the row of an item of the page.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/> for a <c>limit</c> above the most rows, and
    /// <see cref="EditErrorKind.InvalidArgument"/> for a cursor that this server did not give.</exception>
    public Page<TRow> Paged<TItem, TRow>(IReadOnlyList<TItem> rows, Func<TItem, TRow> make)
    {
        var limit = RowLimit();
        var start = 0;
        if (Args.Text("cursor") is { } cursor &&
            (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start > rows.Count))
            throw ToolInput.Bad("cursor", AgentTexts.Error("cursor_bad"));
        var end = Math.Min(rows.Count, start + limit);
        return new Page<TRow>(rows.Skip(start).Take(end - start).Select(make).ToList(), rows.Count,
            end < rows.Count ? end.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>The most rows of a list result: the <c>limit</c> argument, or the default rows of the limits.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/> for a <c>limit</c> above the most rows of the
    /// limits (section 8.4 of the MCP1 design).</exception>
    public int RowLimit()
    {
        var limit = Args.Whole("limit") ?? Limits.ListRows;
        if (limit > Limits.MaxListRows)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, "ListRows",
                AgentTexts.Error("limit_rows", ("max", Limits.MaxListRows))) { Field = "limit" });
        }
        return (int)limit;
    }

    /// <summary>True when a text filter matches one of the texts, without letter case. A null filter matches each
    /// row.</summary>
    public static bool Matches(string? filter, params string?[] texts) =>
        filter == null || texts.Any(t => t != null && t.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
