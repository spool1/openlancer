using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The plan of a builder of new items: a loadout, a wreck, the zones of a route, or the encounter groups of a zone
/// (section 7.3 of the MCP1 design). One plan adds all its sections in one entry of the edit history.
/// </summary>
internal sealed class BuildPlan : EditPlan
{
    public BuildPlan(string label) : base(label)
    {
    }

    /// <summary>Gives the plan its command and what the command adds, changes and fixes.</summary>
    /// <param name="select">The section to select after the command, or null to keep the selection.</param>
    public BuildPlan Runs(EditCommand command, IReadOnlyList<SectionRef> adds, IReadOnlyList<SectionRef> changes,
        SectionRef? select, IReadOnlyList<Diagnostic> warnings, IReadOnlyList<PlannedFix>? fixes = null)
    {
        Command = command;
        Adds = adds;
        Changes = changes;
        SelectAfter = select;
        Warnings = warnings;
        Fixes = fixes ?? [];
        return this;
    }

    /// <summary>Gives the plan a refusal, so it cannot run.</summary>
    /// <param name="detail">The detail code of the error of the refusal, for example
    /// <see cref="MissionErrors.NameTaken"/>, or null.</param>
    public BuildPlan Refuse(string path, string message, SectionRef? target = null, string? detail = null)
    {
        Command = null;
        Refusals = [Diagnostic.Error(path, message) with { Target = target, Rule = detail }];
        return this;
    }
}

/// <summary>
/// The values of the rows of a builder request, with the errors of each row (section 7.3 of the MCP1 design). A value
/// that names other data must be an entry of its list, as a picker of the editor gives it (Editor UI rule 2). The
/// builder writes each value as the list spells it. Each error names its row as the request form names it, for example
/// <c>encounters[1].factions[0].faction</c>.
/// </summary>
internal sealed class RowValues(EditSession session)
{
    private readonly List<(string Field, string Message)> errors = [];

    /// <summary>
    /// The value as its list spells it, or null with an error when the list does not have it. The error gives the
    /// closest names.
    /// </summary>
    /// <param name="lists">The lists of the value, in order. The first list that has the value gives it.</param>
    /// <param name="scope">The install path of the file of a scoped list, or null.</param>
    public string? Listed(string value, string field, IReadOnlyList<string> lists, string? scope = null)
    {
        var views = lists.Select(id => scope == null ? session.Lists.List(id) : session.Lists.List(id, scope))
            .OfType<ListView>().ToList();
        if (views.Select(v => v.Find(value)).FirstOrDefault(e => e != null) is { } entry)
            return entry.Value;
        var label = ListChecks.Lower(views.FirstOrDefault()?.Label ?? lists[0]);
        Add(field, $"No {label} has the name '{value}'. Pick a value from list_values with the list {lists[0]}." +
                   ItemRefs.Hint(value, views.SelectMany(v => v.Entries.Select(e => e.Value))));
        return null;
    }

    /// <summary>A text that goes into one value of a line, or null with an error: a text that is not empty and that
    /// has no ',', no ';' and no control character.</summary>
    public string? Word(string value, string field)
    {
        var text = value.Trim();
        if (text.Length > 0 && !text.Any(c => c is ',' or ';' || char.IsControl(c)))
            return text;
        Add(field, "Give one value without ',' or ';'.");
        return null;
    }

    /// <summary>Adds the error of a row.</summary>
    public void Add(string field, string message) => errors.Add((field, message));

    /// <summary>Throws the errors of all rows as one error, when there is one or more.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidValue"/> with the detail
    /// <see cref="MissionErrors.FieldValue"/>. The field is the first row with an error.</exception>
    public void ThrowIfAny()
    {
        if (errors.Count == 0)
            return;
        throw new EditException(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue,
            string.Join(" ", errors.Select(e => $"{e.Field}: {e.Message}"))) { Field = errors[0].Field });
    }
}

/// <summary>The texts of the lines of the builders.</summary>
internal static class BuilderText
{
    /// <summary>A number as a line holds it, for example <c>0.4</c>.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The value of a <c>rotate</c> line: each angle from -180 to 180 degrees, rounded to 0.001 degree, as a
    /// drag of the gizmo writes it (see <see cref="TransformValues.Angle"/>).</summary>
    public static string Angles(Vector3 degrees) => string.Join(", ",
        new[] { degrees.X, degrees.Y, degrees.Z }.Select(a => TransformValues.Angle(a).ToString(CultureInfo.InvariantCulture)));

    /// <summary>The lines of a new section from their texts, or null with the reason when the file cannot hold a
    /// line.</summary>
    public static List<EntryView>? Entries(EditSession session, string path, IEnumerable<(string Name, string Text)> lines,
        out string? error) => ScenePlans.Entries(lines, session.IsBinaryFile(path), out error);

    /// <summary>The list check errors of the lines of a planned section, with the messages of the checks of the
    /// editor (decision D290 of the M23 design). The warnings go to <paramref name="warnings"/>.</summary>
    /// <param name="planned">The nicknames of the objects, zones and encounters that the plan adds to the same file.</param>
    /// <param name="skip">True for a problem that the check of the plan does not count, for example the loadout line of
    /// a new object that names the new loadout.</param>
    public static List<ListProblem> ListErrors(EditSession session, string path, string type, string nickname,
        IReadOnlyList<EntryView> lines, IReadOnlySet<string> planned, List<Diagnostic> warnings,
        Func<ListProblem, bool>? skip = null)
    {
        var errors = new List<ListProblem>();
        foreach (var problem in ListChecks.CheckPlanned(session, path, type, nickname, lines, planned))
        {
            if (skip?.Invoke(problem) == true)
                continue;
            if (problem.IsError)
                errors.Add(problem);
            else
                warnings.Add(problem.Diagnostic);
        }
        return errors;
    }

    /// <summary>The error of the list check errors of a planned section.</summary>
    /// <param name="field">The row or the argument of the first error, or null.</param>
    public static EditException ListError(string? field, IReadOnlyList<ListProblem> errors) =>
        new(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue,
            string.Join(" ", errors.Select(e => e.Diagnostic.Message))) { Field = field });
}
