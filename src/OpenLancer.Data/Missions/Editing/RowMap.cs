using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// The rows of the trigger lines that the commands of a request moved (final finding 1 of MCP1-3). For each row of a
/// trigger now, the map holds the row of the line before the request, or -1 for a new line. So the net moves give
/// each line once, from its row before the request to its row after it. The map of a later request joins the map of
/// an earlier request, so all requests of one call give one net map.
/// </summary>
internal sealed class RowMap
{
    private readonly List<Rows> triggers = [];

    /// <summary>The rows of one trigger.</summary>
    /// <param name="start">The trigger at the start, by its name then.</param>
    private sealed class Rows(TriggerRef start)
    {
        public TriggerRef Start { get; } = start;

        /// <summary>The trigger now, by its name now.</summary>
        public TriggerRef Now { get; set; } = start;

        /// <summary>The number of lines at the start.</summary>
        public int StartCount { get; set; }

        /// <summary>For each row now, the row at the start, or -1 for a new line. Null when no line moved, for
        /// example after a rename only.</summary>
        public List<int>? Origins { get; set; }

        /// <summary>False when an edit that the map does not follow changed the number of lines. Then the map gives
        /// no moves for the trigger.</summary>
        public bool Known { get; set; } = true;

        public Rows Copy() => new(Start) { Now = Now, StartCount = StartCount, Origins = Origins?.ToList(), Known = Known };
    }

    /// <summary>The rows of a trigger by its name now, or null.</summary>
    private Rows? Find(TriggerRef trigger) => triggers.FirstOrDefault(r => Same(r.Now, trigger));

    private static bool Same(TriggerRef a, TriggerRef b) =>
        VfsPath.Comparer.Equals(a.Script, b.Script) && string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The start rows of the lines of a trigger before a change, or null when they are not known.</summary>
    /// <param name="count">The number of lines before the change.</param>
    /// <param name="startCount">Gives the number of lines of a trigger at the start by its name then: -1 when the
    /// trigger did not exist then, or null when the start is not known. Then the map uses the number before the
    /// change.</param>
    private List<int>? Origins(TriggerRef trigger, int count, Func<TriggerRef, int?> startCount)
    {
        var rows = Find(trigger);
        if (rows == null)
            triggers.Add(rows = new Rows(trigger));
        if (!rows.Known)
            return null;
        if (rows.Origins == null)
        {
            // An edit before the first move that the map follows can change the number of lines. Then the rows at the
            // start are not known. A new trigger has only new lines, so it has no moves.
            if (startCount(rows.Start) is { } start && start != count)
            {
                rows.Known = false;
                return null;
            }
            rows.StartCount = count;
            rows.Origins = Enumerable.Range(0, count).ToList();
        }
        else if (rows.Origins.Count != count)
        {
            rows.Known = false;
            return null;
        }
        return rows.Origins;
    }

    /// <summary>Notes a change of the lines of a trigger. Each change of the lines of a trigger comes here, so the map
    /// follows each command (final finding 1 of MCP1-3).</summary>
    /// <param name="origins">For each line after the change, its row before the change, or -1 for a new line.</param>
    /// <param name="count">The number of lines before the change.</param>
    public void Changed(TriggerRef trigger, IReadOnlyList<int> origins, int count, Func<TriggerRef, int?> startCount)
    {
        if (Origins(trigger, count, startCount) is not { } rows)
            return;
        var next = origins.Select(o => o >= 0 ? rows[o] : -1).ToList();
        rows.Clear();
        rows.AddRange(next);
    }

    /// <summary>The origins of an insert of new lines at a row (see <see cref="Changed"/>).</summary>
    /// <param name="count">The number of lines before the insert.</param>
    /// <param name="lines">The number of new lines.</param>
    public static int[] OfInsert(int count, int row, int lines = 1) =>
        [.. Enumerable.Range(0, row), .. Enumerable.Repeat(-1, lines), .. Enumerable.Range(row, count - row)];

    /// <summary>The origins of the removal of some rows (see <see cref="Changed"/>).</summary>
    /// <param name="count">The number of lines before the removal.</param>
    public static int[] OfRemoval(int count, IReadOnlyCollection<int> rows) =>
        [.. Enumerable.Range(0, count).Where(r => !rows.Contains(r))];

    /// <summary>The origins of a move of a line from a row to another row (see <see cref="Changed"/>).</summary>
    /// <param name="to">The row of the line after the move.</param>
    /// <param name="count">The number of lines.</param>
    public static int[] OfMove(int count, int row, int to)
    {
        var origins = Enumerable.Range(0, count).ToList();
        origins.RemoveAt(row);
        origins.Insert(to, row);
        return [.. origins];
    }

    /// <summary>Notes a rename of a trigger, so a later change and the net moves use the new name.</summary>
    public void Renamed(TriggerRef from, TriggerRef to)
    {
        var rows = Find(from);
        if (rows == null)
            triggers.Add(rows = new Rows(from));
        rows.Now = to;
    }

    /// <summary>Drops the moves of each trigger whose number of lines now differs from the map, for example after an
    /// edit that the map does not follow. A trigger that does not exist now gives no moves.</summary>
    /// <param name="countNow">Gives the number of lines of a trigger now, or null when it does not exist.</param>
    public void Check(Func<TriggerRef, int?> countNow)
    {
        foreach (var rows in triggers.Where(r => r is { Known: true, Origins: not null }))
        {
            if (countNow(rows.Now) != rows.Origins!.Count)
                rows.Known = false;
        }
    }

    /// <summary>The net moves: each line whose row changed, once, with its row at the start and its row now, by
    /// trigger and then by the row at the start.</summary>
    public IReadOnlyList<RowMove> Moves =>
    [
        .. triggers.Where(r => r is { Known: true, Origins: not null }).SelectMany(r => r.Origins!
            .Select((from, to) => (From: from, To: to)).Where(m => m.From >= 0 && m.From != m.To).OrderBy(m => m.From)
            .Select(m => new RowMove(r.Now, m.From, m.To))),
    ];

    /// <summary>The map of this request and then a later request: the rows at the start of this request and the rows
    /// at the end of the later request.</summary>
    public RowMap Then(RowMap later)
    {
        var result = new RowMap();
        result.triggers.AddRange(triggers.Select(r => r.Copy()));
        foreach (var next in later.triggers)
        {
            var earlier = result.Find(next.Start);
            if (earlier == null)
            {
                result.triggers.Add(next.Copy());
                continue;
            }
            earlier.Now = next.Now;
            if (!next.Known)
                earlier.Known = false;
            else if (next.Origins != null && earlier.Known)
            {
                if (earlier.Origins == null)
                {
                    earlier.StartCount = next.StartCount;
                    earlier.Origins = next.Origins.ToList();
                }
                else if (earlier.Origins.Count != next.StartCount)
                    earlier.Known = false;
                else
                    earlier.Origins = next.Origins.Select(o => o >= 0 ? earlier.Origins[o] : -1).ToList();
            }
        }
        return result;
    }
}
