using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The sections at the end of an Inspector page that edits sections of the edit view: "More keys" with the keys of the
/// schema that no row above shows, "Other keys" with the keys that the schema does not know, and the editable "Raw INI"
/// block with the lines of the sections (see <see cref="Raw"/>). So the page can edit each key of its sections. A page
/// of more than one section gets a subheading for each section.
/// </summary>
internal sealed class SectionExtras(EditorWindow window, PropertyGrid grid)
{
    private const string MoreKeysHeading = "Keys that no row above shows";
    private const string OtherKeysHeading = "Keys that the engine does not read";

    /// <summary>The name of the field key of a raw block. It is not an INI key, so it cannot clash with one.</summary>
    internal const string RawKey = "Raw INI";

    /// <summary>The most text lines that a raw block shows before it scrolls.</summary>
    private const int RawLines = 18;

    private readonly Dictionary<SectionRef, string> rawTexts = [];
    private readonly Dictionary<SectionRef, IReadOnlyList<(int Line, Diagnostic Problem, bool BaseGame)>> rawProblems = [];
    private (EditorSession? Session, int Version) rawVersion;

    /// <param name="sections">The sections of the page. A section that the edit view does not have shows nothing.</param>
    /// <param name="key">The first part of the settings keys of the sections, "Type".</param>
    /// <param name="moreKeys">True to show "More keys". A page that shows each key of its schema has no such
    /// section.</param>
    public void Draw(IReadOnlyList<SectionRef> sections, string key, bool moreKeys)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        var edits = session.Edits;
        var lists = sections.Select(s => (Section: s, Keys: SchemaPage.Layout(edits, s, grid.OpenEdit))).ToList();
        if (moreKeys)
        {
            // The rows above drew in this frame, so the list has the keys that they did not show.
            Keys(key + ".More", EditorIcons.Plus, "More keys", MoreKeysHeading, false, sections.Count > 1,
                sections.Select(s => (s, NotShown(edits, s))));
        }

        // A section rarely has such keys, so the list is open until the user closes it.
        Keys(key + ".Other", EditorIcons.Th, "Other keys", OtherKeysHeading, true, sections.Count > 1,
            lists.Select(l => (l.Section, l.Keys.Unknown)));
    }

    /// <summary>The keys of "More keys" of a section in this frame (see <see cref="MoreKeys"/>).</summary>
    private IReadOnlyList<string> NotShown(EditSession edits, SectionRef section) =>
        PropertyGrid.LayoutView(edits, section, grid.OpenEdit) is { } layout
            ? MoreKeys(edits.Schema(section), grid.Shown(section), layout)
            : [];

    /// <summary>
    /// The keys of "More keys": the values of the schema that no bound row showed, without the key entry. Two names of
    /// one field set one value, for example <c>ambient_color</c> and <c>ambient</c> of an <c>[Object]</c>. So for
    /// each value, the list has the names that have lines, or else the first name. A line of the other name would set
    /// the value a second time, and a row of the value edits only one of the two lines.
    /// </summary>
    /// <param name="shown">The keys that bound rows showed in this frame.</param>
    /// <param name="layout">The section that the page lays out its rows from, so the list stays while a field edit is
    /// open (see <see cref="PropertyGrid.LayoutView"/>).</param>
    internal static IReadOnlyList<string> MoreKeys(IReadOnlyList<KeySchema> schema, IReadOnlySet<string> shown,
        SectionView layout) =>
        schema.Where(k => !k.IsKeyEntry)
            // A key without a field is its own value, for example a key that an entry handler reads.
            .GroupBy(k => (k.Field, Name: k.Field == null ? k.Name.ToLowerInvariant() : null))
            .Where(names => !names.Any(k => shown.Contains(k.Name)))
            .SelectMany(names => names.Where(k => layout.Lines(k.Name).Count > 0).DefaultIfEmpty(names.First()))
            .Select(k => k.Name)
            .ToList();

    /// <summary>A section with a row for each key of each section, or nothing when no section has a key.</summary>
    /// <param name="heading">The heading at the top of the section, which tells what the keys are.</param>
    /// <param name="defaultOpen">The open state until the user opens or closes the section.</param>
    /// <param name="sectionHeadings">True to give the keys of each section a subheading with the section.</param>
    private void Keys(string key, char icon, string title, string heading, bool defaultOpen, bool sectionHeadings,
        IEnumerable<(SectionRef Section, IReadOnlyList<string> Keys)> lists)
    {
        var shown = lists.Where(l => l.Keys.Count > 0).ToList();
        if (shown.Count == 0 || !grid.BeginSection(key, icon, title, defaultOpen))
        {
            return;
        }

        grid.SubHeading(heading);
        foreach (var (section, keys) in shown)
        {
            using (grid.Bind(section))
            {
                if (sectionHeadings)
                {
                    grid.SubHeading(section.Id.ToString());
                }

                foreach (var name in keys)
                {
                    grid.Text(name, null, name);
                }
            }
        }

        grid.EndSection();
    }

    /// <summary>
    /// The "Raw INI" section, the last section of a page that edits sections (decision D115): one editable block for each
    /// section, with a subheading for each section of a page with more than one. The text is <c>[Type]</c> and one
    /// <c>name = values</c> line for each line of W. One raw edit is one entry of the edit history (see
    /// <see cref="RawSectionEdit"/>). A block is read-only when the session or the section is read-only, or when the
    /// editor does not know the schema of the file (decision D123).
    /// </summary>
    /// <param name="key">The first part of the settings key of the section, "Type". The settings keep the open state
    /// for each page type.</param>
    public void Raw(IReadOnlyList<SectionRef> sections, string key)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        var views = sections.Select(s => (Section: s, View: session.Edits.Get(s))).Where(v => v.View != null).ToList();
        if (views.Count == 0 || !grid.BeginSection(key + ".RawIni", EditorIcons.Th, "Raw INI"))
        {
            return;
        }

        foreach (var (section, view) in views)
        {
            if (views.Count > 1)
            {
                grid.SubHeading(section.Id.ToString());
            }

            grid.Row("INI", () => RawBlock(session, section, view!));
        }

        grid.EndSection();
    }

    /// <summary>The text field of one section, its problems while the user types, and the problems of the section with
    /// their line numbers.</summary>
    private void RawBlock(EditorSession session, SectionRef section, SectionView view)
    {
        var text = RawText(session, section, view);
        var reason = session.NoEditReason ?? FieldBinding.SectionLock(session.Edits, section, view);
        var lines = Math.Clamp(text.Count(c => c == '\n') + 2, 3, RawLines);
        var size = new Vector2(-1, lines * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y);
        var edited = text;
        if (reason != null)
        {
            ImGui.InputTextMultiline("##raw", ref edited, (uint)Encoding.UTF8.GetByteCount(edited) + 1, size,
                ImGuiInputTextFlags.ReadOnly);
            ImGui.SetItemTooltip($"{EditorIcons.Lock} {reason}");
        }
        else
        {
            var changed = ImGui.InputTextMultiline("##raw", ref edited, (uint)Encoding.UTF8.GetByteCount(edited) + 1024,
                size);
            var active = ImGui.IsItemActive();
            grid.CommitField(new FieldKey(section, RawKey, FieldKey.AllLines), changed, text, edited,
                (end, _) => Commit(session, section, text, end));
            ImGui.SetItemTooltip("Edit the lines, then click outside. One edit is one entry of the history. Esc puts " +
                                 "back the text.");
            if (active && !string.Equals(edited, text, StringComparison.Ordinal))
            {
                foreach (var problem in RawSectionEdit.Plan(session.Edits, section, text, edited).Problems)
                {
                    ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.ExclamationTriangle} {problem}");
                }
            }
        }

        foreach (var (line, problem, baseGame) in RawProblems(session, section, view))
        {
            var shown = $"{(line > 0 ? $"Line {line}: " : "")}{Short(problem)}";
            if (baseGame)
            {
                PropertyGrid.BaseGameProblemText(shown);
            }
            else
            {
                ImGui.TextColored(problem.Severity == DiagnosticSeverity.Error ? EditorTheme.Error : EditorTheme.Warning,
                    $"{EditorIcons.ExclamationTriangle} {shown}");
            }
        }
    }

    /// <summary>
    /// Runs a raw edit at the end of the field. An edit with a problem keeps nothing (decision D114): the Console line
    /// holds the problem and the whole draft text, so the user can copy it back.
    /// </summary>
    private string? Commit(EditorSession session, SectionRef section, string start, string end)
    {
        var plan = RawSectionEdit.Plan(session.Edits, section, start, end);
        if (plan.Problems.Count > 0)
        {
            return Missions.MissionWorkspace.RawRefusal(plan.Problems.Select(p => p.ToString()), end);
        }

        foreach (var note in plan.Notes)
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, note);
        }

        if (plan.Mission != null)
        {
            window.Commands.Apply(plan.Mission);
        }
        else if (plan.Command != null)
        {
            window.Commands.Execute(plan.Command);
        }

        return null;
    }

    /// <summary>The text of a section, built once for each version of the edit view.</summary>
    private string RawText(EditorSession session, SectionRef section, SectionView view)
    {
        ForgetOldVersion(session);

        if (!rawTexts.TryGetValue(section, out var text))
        {
            text = RawSectionEdit.Format(view);
            rawTexts[section] = text;
        }

        return text;
    }

    /// <summary>The problems of the raw block of a section, found once for each version of the edit view.</summary>
    private IReadOnlyList<(int Line, Diagnostic Problem, bool BaseGame)> RawProblems(EditorSession session,
        SectionRef section, SectionView view)
    {
        ForgetOldVersion(session);
        if (!rawProblems.TryGetValue(section, out var problems))
        {
            problems = SectionProblems(session.Edits, section, view).ToList();
            rawProblems[section] = problems;
        }

        return problems;
    }

    /// <summary>Forgets the texts and the problems of the raw blocks after an edit, an undo, a redo or a save.</summary>
    private void ForgetOldVersion(EditorSession session)
    {
        if (rawVersion != (session, session.Version))
        {
            rawTexts.Clear();
            rawProblems.Clear();
            rawVersion = (session, session.Version);
        }
    }

    /// <summary>The problems of a section for its raw block: the field checks and the list checks, each with the line of
    /// its key in the text, or 0, and true for a base-game field problem (the key rule, decision D512 of the console
    /// filter design). A list problem keeps its normal look, as on its row (section 6 of the design).</summary>
    internal static IEnumerable<(int Line, Diagnostic Problem, bool BaseGame)> SectionProblems(EditSession edits,
        SectionRef section, SectionView view)
    {
        var problems = edits.Check(section).Select(p => (Problem: p, BaseGame: edits.IsBaseGameProblem(p)))
            .Concat(edits.CheckLists(section).Select(p => (Problem: p, BaseGame: false)));
        foreach (var (problem, baseGame) in problems)
        {
            var index = problem.Key == null
                ? -1
                : view.Entries.ToList().FindIndex(e => e.Name.Equals(problem.Key, StringComparison.OrdinalIgnoreCase));
            // Line 1 of the text is the header.
            yield return (index < 0 ? 0 : index + 2, problem, baseGame);
        }
    }

    /// <summary>The message of a problem without the section at its start.</summary>
    private static string Short(Diagnostic problem)
    {
        var prefix = problem.Target == null ? null : $"{problem.Target.Id}: ";
        return prefix != null && problem.Message.StartsWith(prefix, StringComparison.Ordinal)
            ? problem.Message[prefix.Length..]
            : problem.Message;
    }
}
