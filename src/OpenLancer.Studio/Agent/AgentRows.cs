using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Studio.Agent;

/// <summary>Where a section or an entry comes from, for a row of a result.</summary>
/// <param name="Layer">The layer of the edit view that gives the item: the base data, a lower mod or the active
/// mod.</param>
/// <param name="Mod">The mod of the layer <see cref="ValueOrigin.LowerMod"/> or <see cref="ValueOrigin.ActiveMod"/>,
/// else null.</param>
/// <param name="Unsaved">True when the item has an edit that the session did not save.</param>
internal sealed record ItemOrigin(ValueOrigin Layer, string? Mod, bool Unsaved);

/// <summary>The helpers that the query tools share: values of lines, origins of sections and problems.</summary>
internal static class AgentRows
{
    /// <summary>The value of an INI value as JSON: a number when the text is a finite number, else the text.</summary>
    public static JsonNode Value(ValueBase value) => Value(value.ToString().Trim());

    /// <summary>The value of a text as JSON: a whole number or a number when the text is one, else the text.</summary>
    public static JsonNode Value(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            return JsonValue.Create(whole);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number) && text.Any(char.IsDigit))
            return JsonValue.Create(number);
        return JsonValue.Create(text);
    }

    /// <summary>The values of a line as a JSON array.</summary>
    public static JsonArray Values(IReadOnlyList<ValueBase> values) => new(values.Select(v => (JsonNode?)Value(v)).ToArray());

    /// <summary>The numbers of the last line of a key, or null when the section has no such line or a value is not a
    /// finite number. JSON has no value for NaN or an infinity, and the parser reads <c>nan</c> and <c>1e39</c> as
    /// such values.</summary>
    public static float[]? Numbers(SectionView view, string key)
    {
        if (view.Lines(key).LastOrDefault() is not { Values.Count: > 0 } line)
            return null;
        var numbers = new float[line.Values.Count];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!float.TryParse(line.Values[i].ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out numbers[i]) || !float.IsFinite(numbers[i]))
                return null;
        }
        return numbers;
    }

    /// <summary>
    /// Where a section comes from: the active mod when it adds or changes the section, else the layer of its key line,
    /// and if the session has an edit of the section that it did not save.
    /// </summary>
    /// <param name="keyName">The name of the key line, for example <c>nickname</c>.</param>
    public static ItemOrigin OriginOf(EditSession session, SectionRef section, SectionView view, string keyName)
    {
        var unsaved = session.IsUnsaved(section);
        if (view.IsChanged && session.ActiveMod is { } active)
            return new ItemOrigin(ValueOrigin.ActiveMod, active.Id, unsaved);
        var (origin, mod) = session.Origin(section, keyName);
        return new ItemOrigin(origin, mod, unsaved);
    }

    /// <summary>A diagnostic of a check as a problem of a result, with the reference of its section, or of its place in a
    /// cutscene (see <see cref="CutsceneQueries.RefOf"/>). The reference takes the letter case of the file in the
    /// session. An error of a cutscene gets the note of the Problems section of the Cutscene page (see
    /// <see cref="NoteOf"/>).</summary>
    public static Problem ProblemOf(EditSession session, Diagnostic diagnostic) =>
        Problem.From(diagnostic, diagnostic.Target is { } target
            ? ItemRefs.Canonical(session, session.OpenedDocument(target.Path) is { } document ? target with { Path = document.Path } : target)
            : diagnostic.Cutscene is { } place ? CutsceneQueries.RefOf(session, place)
            : null) with { Note = NoteOf(session, diagnostic) };

    /// <summary>A problem that a call corrected, as a fixed problem of a result (see <see cref="ProblemOf"/>). It has no
    /// note, because the note tells how to correct a problem that stays.</summary>
    public static Problem FixedProblemOf(EditSession session, Diagnostic diagnostic) =>
        ProblemOf(session, diagnostic) with { Note = null };

    /// <summary>The note of an error of a cutscene, as the Problems section of the Cutscene page shows it, or null. Only
    /// a cutscene that the active mod does not change has a note (see
    /// <see cref="EditSession.CutsceneProblemsNote"/>).</summary>
    public static string? NoteOf(EditSession session, Diagnostic diagnostic) =>
        diagnostic is { Severity: DiagnosticSeverity.Error, Target: null, Cutscene: { } place }
            ? session.CutsceneProblemsNote(place.Path)
            : null;

    /// <summary>The diagnostics of the field checks and the list checks of a section, each with a target. A
    /// <c>[system]</c> or a <c>[Base]</c> of <c>universe.ini</c> also gets the problems of the universe checks (see
    /// <see cref="UniverseChecks"/>). An object with a <c>goto</c> line and a <c>[locked_gates]</c> section also get
    /// the problems of the jump connection checks (see <see cref="ConnectionChecks"/>).</summary>
    public static IReadOnlyList<Diagnostic> DiagnosticsOf(EditSession session, SectionRef section) =>
        session.Check(section).Concat(session.Lists.Problems(section).Select(p => p.Diagnostic))
            .Concat(UniverseChecks.Check(session, section))
            .Concat(ConnectionChecks.Check(session, section))
            .Select(d => d with { Target = d.Target ?? section }).ToList();

    /// <summary>The problems of the field checks and the list checks of a section.</summary>
    public static IReadOnlyList<Problem> ProblemsOf(EditSession session, SectionRef section) =>
        DiagnosticsOf(session, section).Select(d => ProblemOf(session, d)).ToList();

    /// <summary>True when a problem shows: each problem, or without <paramref name="baseGame"/> each problem whose origin
    /// is not the base game (decision D519).</summary>
    public static bool Shows(Problem problem, bool baseGame) => baseGame || problem.Origin != OriginKind.BaseGame;
}
