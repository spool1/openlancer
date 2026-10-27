using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Commands;

/// <summary>The plan of a raw edit, or the problems that stop it. No problem and no plan: nothing changed.</summary>
/// <param name="Edit">The plan of the edit of a section that is not in a mission file, or null. It is one entry of the
/// edit history.</param>
internal sealed record RawEditPlan(EditPlan? Edit, IReadOnlyList<RawProblem> Problems)
{
    /// <summary>The batch of a raw edit of a section of a mission file, or null. The mission editor runs it, because it
    /// is the only write path of mission files (decision D660 of the M6b design).</summary>
    public MissionBatch? Mission { get; init; }

    /// <summary>The notes of the edit for the Console, for example about a comment that the editor does not keep.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A problem of the text of a raw edit.</summary>
/// <param name="Line">The line of the text, from 1, or 0 for the whole text.</param>
internal sealed record RawProblem(int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"Line {Line}: {Message}" : Message;
}

/// <summary>
/// The raw INI block of an Inspector page (section 13 of the M21 design). It gives the text of one section, and the
/// plan of an edit of the text. One raw edit is one entry of the edit history.
/// </summary>
/// <remarks>
/// <para>The text is <c>[Type]</c> and one <c>name = values</c> line for each line of W, in file order. A text that
/// the user does not change gives no edit.</para>
/// <para>The plan is three-way: it changes only the names whose lines differ between the text at the start of the edit
/// and the text at its end. When W changed such a name after the start, the plan stops, so the edit never undoes a
/// change that the user did not see (review finding R16). A name that did not change is not checked again, so a retail
/// section with a repeated single-line key can still take other edits.</para>
/// <para>The order of different names stays the file order. The lines of one name follow the text (decision D116). A
/// new name goes at the end of the section.</para>
/// </remarks>
internal static class RawSectionEdit
{
    /// <summary>The text of a section for the raw block.</summary>
    public static string Format(SectionView view) =>
        string.Join("\n", new[] { $"[{view.Type}]" }.Concat(view.Entries.Select(e => FieldEdit.IniText(e.Name, [e]))));

    /// <summary>
    /// The commands of a raw edit of a section, or the problems that stop it. A list error does not stop the edit: the
    /// value goes into W, and the rows show the error. A problem of the field checks of a changed name stops it, as a row
    /// edit does.
    /// </summary>
    /// <param name="startText">The text of the block at the start of the edit.</param>
    /// <param name="endText">The text of the block at the end of the edit.</param>
    /// <remarks>A section of a mission file gives a batch of mission commands (section 8.8 of the M6b design).</remarks>
    public static RawEditPlan Plan(EditSession session, SectionRef section, string startText, string endText)
    {
        var view = session.Get(section);
        if (view == null)
            return Stop(0, "The edit view does not have this section any more.");
        if (session.Missions.IsMissionFile(section.Path))
        {
            var mission = RawMissionEdit.PlanBlock(session, section.Path, [section.Id], startText, endText);
            return new RawEditPlan(null, [.. mission.Problems.Select(p => new RawProblem(p.Line, p.Message))])
            {
                Mission = mission.Batch,
                Notes = mission.Notes,
            };
        }
        var problems = new List<RawProblem>();
        var start = Parse(startText, view.Type, problems);
        problems.Clear();
        var end = Parse(endText, view.Type, problems);
        if (problems.Count > 0)
            return new RawEditPlan(null, problems);

        // The key entry finds the section, so its lines must stay.
        var keyEntry = session.Schema(section).FirstOrDefault(k => k.IsKeyEntry)?.Name;
        if (keyEntry != null && !SameLines(Lines(start, keyEntry), Lines(end, keyEntry)))
        {
            var line = end.FirstOrDefault(l => Is(l.Name, keyEntry))?.Number ?? 0;
            return Stop(line, "The nickname finds the section, so this block cannot change it. To rename an item of a " +
                              "system, press F2 in the Hierarchy.");
        }

        var names = start.Concat(end).Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = names.Where(n => !SameLines(Lines(start, n), Lines(end, n))).ToList();
        if (changed.Count == 0)
            return new RawEditPlan(null, []);

        var commands = new List<EditCommand>();
        foreach (var name in changed)
        {
            var number = end.FirstOrDefault(l => Is(l.Name, name))?.Number ?? 0;
            // The user typed against the start text. A change of W after the start would be lost without a message.
            if (!SameLines(Lines(start, name), view.Lines(name).Select(e => FieldEdit.FormatLine(e.Values)).ToList()))
                return Stop(number, "The section changed while you typed. Copy your text, and edit again.");
            var key = session.Schema(section, name);
            if (FieldBinding.KeyLock(session, section, view, key, name) is { } locked)
                return Stop(number, $"'{name}': {locked}");
            var lines = Lines(end, name);
            if (lines.Count == 0)
            {
                commands.Add(EditCommand.RemoveName(section, name));
                continue;
            }

            var parse = FieldEdit.Parse(key, string.Join("\n", lines));
            if (parse.Lines == null)
                return Stop(number, parse.Error!);
            // The spelling of the end text, for a new name.
            var spelling = view.Lines(name).FirstOrDefault()?.Name ?? end.First(l => Is(l.Name, name)).Name;
            try
            {
                commands.Add(EditCommand.SetLinesFromText(section, spelling, parse.Lines));
            }
            catch (ArgumentException e)
            {
                return Stop(number, ErrorText.Of(e));
            }
        }

        return new RawEditPlan(new CommandPlan(EditCommand.Group($"Edit INI of {section.Id}", commands), [section]), []);
    }

    /// <summary>The lines of a text. Empty lines do not count. Adds a problem for each line that is not a line of the
    /// section.</summary>
    private static List<RawLine> Parse(string text, string type, List<RawProblem> problems)
    {
        var result = new List<RawLine>();
        var headers = 0;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var number = i + 1;
            if (line.Length == 0)
                continue;
            if (line.StartsWith('['))
            {
                headers++;
                if (headers > 1 || !line.EndsWith(']') || !Is(line[1..^1].Trim(), type))
                    problems.Add(new RawProblem(number, "One block holds one section. Keep its header and add no other."));
                continue;
            }

            if (headers == 0)
            {
                problems.Add(new RawProblem(number, "One block holds one section. The text must start with its header."));
                continue;
            }

            // The editor keeps no comments in W, so it refuses them and loses nothing without a message.
            if (line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal))
            {
                problems.Add(new RawProblem(number, "A comment is not kept. Remove the line."));
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                problems.Add(new RawProblem(number, "A line must be 'name = values' or 'name ='."));
                continue;
            }

            var name = line[..equals].Trim();
            result.Add(new RawLine(number, name, string.Join(", ", FieldEdit.Values(line[(equals + 1)..]))));
        }

        if (headers == 0 && problems.Count == 0)
            problems.Add(new RawProblem(0, "One block holds one section. The text must start with its header."));
        return result;
    }

    /// <summary>The value texts of the lines of a name, in text order.</summary>
    private static List<string> Lines(List<RawLine> lines, string name) =>
        lines.Where(l => Is(l.Name, name)).Select(l => l.Values).ToList();

    private static bool SameLines(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.SequenceEqual(b, StringComparer.Ordinal);

    private static RawEditPlan Stop(int line, string message) => new(null, [new RawProblem(line, message)]);

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <param name="Number">The line of the text, from 1.</param>
    /// <param name="Values">The values, separated by ", ".</param>
    private sealed record RawLine(int Number, string Name, string Values);
}
