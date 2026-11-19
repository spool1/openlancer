using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>The kind of a list problem.</summary>
public enum ListProblemKind
{
    /// <summary>The value is outside a closed list: "Missing (Type)".</summary>
    Missing,

    /// <summary>The value is outside an open list, or a value of a lower layer is outside a list.</summary>
    NotListed,

    /// <summary>The value names an entry that the active mod removed.</summary>
    Removed,

    /// <summary>The value names an entry that the engine cannot use, for example a curve without points.</summary>
    Unusable,

    /// <summary>A required key has no valid line.</summary>
    RequiredMissing,

    /// <summary>The name of the section has the FLHash of another name of the list.</summary>
    HashCollision,
}

/// <summary>One result of the list checks of a section.</summary>
/// <param name="Diagnostic">The problem, with the section as its target and the key as its key. Its message is the
/// section, the <see cref="Reason"/>, the <see cref="Consequence"/> and a fix, if the problem has one.</param>
/// <param name="Rule">The rule of the value, or null for a hash collision.</param>
/// <param name="Line">The position of the line among the lines of the key, from 0, or -1 for a missing key.</param>
/// <param name="Value">The value, or null for a missing key.</param>
/// <param name="List">The list of the value, or null when the problem has no list.</param>
public sealed record ListProblem(Diagnostic Diagnostic, ListProblemKind Kind, ListRule? Rule, int Line, string? Value,
    ListView? List)
{
    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;

    /// <summary>What the engine does with the section because of the problem: the effect of the rule, or
    /// <see cref="RuleEffect.LoadBreak"/> for a hash collision.</summary>
    public RuleEffect Effect { get; init; }

    /// <summary>What is wrong, for example "'loadout = lo_foo': no loadout has the name 'lo_foo'."</summary>
    public string Reason { get; init; } = "";

    /// <summary>What the game does with the data, in plain words, for example "The game drops this object."</summary>
    public string Consequence { get; init; } = "";
}

/// <summary>
/// The list checks of one section (layer 2 of roadmap section 5.4, section 11.1 of the M21 design). The checks never
/// refuse an edit command. They report.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A value of the active mod outside a closed list is an error (decision D96). Outside an open list, it is a
/// warning: unlisted values are allowed.</item>
/// <item>A value of any layer under a closed rule that names an entry that the active mod removed is an error
/// (decision D81, D108 amended).</item>
/// <item>A required key without a valid line, in a section that the active mod adds or changes, is an error (D82).</item>
/// <item>A value of the active mod that names an entry that the engine cannot use is an error (D34, D82). A value of
/// any layer under a closed rule is an error too when the active mod made the entry unusable (D82, decided on
/// 2026-10-18; see <see cref="DataLists.Unusable"/>). Only the effects drop and load-break make an entry unusable. A
/// crash error stays an error of its own line only.</item>
/// <item>The name of a section of the active mod with the FLHash of another name of a hash list is an error (D82).</item>
/// <item>A value of a lower layer outside a list is a warning (D108).</item>
/// <item>A value of a rule with the flag exists outside its list is valid when its file exists in the edit view. A value
/// whose file does not exist is "Missing" (decision D160 of the M22 design).</item>
/// </list>
/// <para>Each error tells what the game does with the data, from the effect of the rule.</para>
/// </remarks>
internal static class ListChecks
{
    /// <param name="unusableOnly">True to check only the rules whose effect makes the section unusable (see
    /// <see cref="ListRule.MakesUnusable"/>), and the hash collisions. The result then tells if the section is
    /// unusable. Such a check follows only these rules to other sections, so it reads few of them.</param>
    public static IReadOnlyList<ListProblem> Check(EditSession session, DataLists lists, SectionRef target,
        bool unusableOnly = false)
    {
        if (session.OpenDocument(target.Path) is not { } document || document.IndexOf(target.Id) is not (>= 0 and var index))
            return [];
        // The editor cannot change a whole file of the active mod outside the files of EditDocument.EditsWholeFile, so
        // its results never stop the save (review finding R22). W of such a file has the lower data. A whole mission
        // file and an INI file of the folder of the mod are documents of the kind "whole file", and their edits are
        // checked as the edits of a patch.
        var wholeFile = session.ActiveMod is { } mod && mod.Files.ContainsKey(document.Path) && !document.IsWholeFile;
        if (unusableOnly && wholeFile)
            return [];
        var section = document.Working[index];
        var result = new List<ListProblem>();
        var byKey = session.DataFiles.RolesOf(target.Path)
            .SelectMany(r => lists.RuleData.ByRoleAndSection.GetValueOrDefault((r.ToLowerInvariant(),
                section.Name.ToLowerInvariant())) ?? [])
            .Where(r => !unusableOnly || ListRule.MakesUnusable(r.Effect))
            .Where(r => !r.SkipIntroRooms || !session.Rooms.IsIntroRoom(target.Path))
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase);
        var lower = document.BaseSection(document.IdentityAt(index));
        var changedSection = lower == null || !ReferenceEquals(lower, section);
        foreach (var keyRules in byKey)
        {
            var key = keyRules.Key;
            var lines = section.Where(e => Is(e.Name, key)).ToList();
            var origin = session.Origin(target, key);
            var active = origin.Origin == ValueOrigin.ActiveMod;
            // The engine reads each line of a multiline key, and only the last line with a value of another key.
            var read = session.Schema(target, key) is { Multiline: false }
                ? lines.Select((l, i) => (Line: l, Index: i)).Where(p => p.Line.Count > 0).TakeLast(1).ToList()
                : lines.Select((l, i) => (Line: l, Index: i)).ToList();
            foreach (var rule in keyRules.OrderBy(r => r.Index))
                CheckRule(lists, target, section, key, lines.Count, read, rule, active, origin, changedSection, result, null);
        }
        CheckHash(lists, target, section, document, index, result);
        if (wholeFile)
        {
            return result.Select(p => p.IsError
                ? p with { Diagnostic = p.Diagnostic with { Severity = DiagnosticSeverity.Warning } }
                : p).ToList();
        }
        return result;
    }

    /// <summary>
    /// The list checks of the lines of a section that a plan adds and that W does not have yet (decision D290 of the M23
    /// design): the placement of a prop group checks each planned line before it goes into W. The lines count as lines
    /// of the active mod in a section that the active mod adds. A value that names an object or a zone that the plan
    /// adds to the same file counts as present, also for the scoped lists <c>object_of_system</c> and
    /// <c>zone_of_system</c>. The checks use the rules of <see cref="Check"/>, without the FLHash check of the name.
    /// </summary>
    /// <param name="path">The install path of the file that gets the section.</param>
    /// <param name="nickname">The nickname of the planned section, for the messages.</param>
    /// <param name="planned">The nicknames of the objects and zones that the plan adds to the file.</param>
    public static IReadOnlyList<ListProblem> CheckPlanned(EditSession session, string path, string type, string nickname,
        IReadOnlyList<EntryView> entries, IReadOnlySet<string> planned)
    {
        var normal = VfsPath.Normalize(path) ?? path;
        var lists = session.Lists;
        var target = new SectionRef(normal, new SectionIdentity(type, nickname));
        var section = SectionEdits.Build(type, entries);
        var result = new List<ListProblem>();
        var origin = (ValueOrigin.ActiveMod, session.ActiveMod?.Id);
        var byKey = session.DataFiles.RolesOf(normal)
            .SelectMany(r => lists.RuleData.ByRoleAndSection.GetValueOrDefault((r.ToLowerInvariant(), type.ToLowerInvariant())) ?? [])
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase);

        bool Planned(ListRule rule, IReadOnlyList<ValueBase> values, string value) =>
            planned.Contains(value) && rule.Lists.Any(lists.IsScoped) && lists.ScopeOf(rule, normal, values, out var scope) &&
            VfsPath.Comparer.Equals(scope, normal);

        foreach (var keyRules in byKey)
        {
            var key = keyRules.Key;
            var lines = section.Where(e => Is(e.Name, key)).ToList();
            var read = session.Schema(target, key) is { Multiline: false }
                ? lines.Select((l, i) => (Line: l, Index: i)).Where(p => p.Line.Count > 0).TakeLast(1).ToList()
                : lines.Select((l, i) => (Line: l, Index: i)).ToList();
            foreach (var rule in keyRules.OrderBy(r => r.Index))
                CheckRule(lists, target, section, key, lines.Count, read, rule, true, origin, true, result, Planned);
        }
        return result;
    }

    /// <param name="count">The number of lines of the key.</param>
    /// <param name="read">The lines that the engine reads, with their positions among the lines of the key.</param>
    /// <param name="planned">True when a value outside its list names an item that a plan adds, or null outside a
    /// plan (see <see cref="CheckPlanned"/>).</param>
    private static void CheckRule(DataLists lists, SectionRef target, Section section, string key, int count,
        List<(Entry Line, int Index)> read, ListRule rule, bool active, (ValueOrigin Origin, string? ModId) origin,
        bool changedSection, List<ListProblem> result, Func<ListRule, IReadOnlyList<ValueBase>, string, bool>? planned)
    {
        var required = IsRequired(section, rule);
        var valid = false;
        foreach (var (line, i) in read)
        {
            var value = ValueOf(line, rule);
            if (value.Length == 0)
                continue;
            valid = true;
            var values = line.ToList();
            if (lists.List(rule, target, values) is not { } list)
                continue;
            var prefix = count > 1 ? $"line {i + 1}: " : "";
            var shown = $"{prefix}'{line.Name} = {string.Join(", ", values.Select(v => v.ToString()))}'";
            if (list.Find(value) is { } entry)
            {
                if (lists.Unusable(entry) is not { } unusable)
                    continue;
                // Like a removed entry: when the active mod made the entry unusable, each line that names it is an
                // error, in any layer.
                var severity = rule.Closed && list.Closed && (active || unusable.ByActiveMod)
                    ? DiagnosticSeverity.Error
                    : DiagnosticSeverity.Warning;
                Add(result, severity, target, key, ListProblemKind.Unusable, rule, i, value, list,
                    $"{shown}: {list.Label} '{entry.Value}' is not usable: {unusable.Reason}",
                    unusable.Consequence ?? rule.ConsequenceText, $"Fix '{entry.Value}', or pick another value.");
                continue;
            }
            if (list.IsEnum)
            {
                // A value outside an enum is an error of the field checks.
                continue;
            }
            // An item that the plan adds is not in W yet.
            if (planned != null && planned(rule, values, value))
                continue;
            // The engine reads a file of any folder (decision D160 of the M22 design).
            if (rule.Exists && lists.FileExists(value))
                continue;
            _ = lists.ScopeOf(rule, target.Path, values, out var scope);
            if (rule.Closed && list.Closed &&
                rule.Lists.Select(l => lists.RemovedOf(l, scope, value)).FirstOrDefault(r => r != null) is { } removed)
            {
                Add(result, DiagnosticSeverity.Error, target, key, ListProblemKind.Removed, rule, i, value, list,
                    $"{shown}: the active mod removes the {Lower(list.Label)} '{value}' ({removed.ModPath}).",
                    rule.ConsequenceText);
                continue;
            }
            if (active && rule.Closed && list.Closed && rule.Exists)
            {
                Add(result, DiagnosticSeverity.Error, target, key, ListProblemKind.Missing, rule, i, value, list,
                    $"{shown}: the file '{value}' does not exist.", rule.ConsequenceText,
                    $"Pick {Article(list.Label)} {Lower(list.Label)}, or add the file to the mod.");
            }
            else if (active && rule.Closed && list.Closed)
            {
                Add(result, DiagnosticSeverity.Error, target, key, ListProblemKind.Missing, rule, i, value, list,
                    $"{shown}: no {Lower(list.Label)} has the name '{value}'.", rule.ConsequenceText);
            }
            else if (active)
            {
                Add(result, DiagnosticSeverity.Warning, target, key, ListProblemKind.NotListed, rule, i, value, list,
                    $"{shown}: '{value}' is not in the {Lower(list.Label)} list.", "Unlisted values are allowed.");
            }
            else
            {
                var layer = origin.Origin == ValueOrigin.LowerMod ? $"mod '{origin.ModId}'" : "the base data";
                Add(result, DiagnosticSeverity.Warning, target, key, ListProblemKind.NotListed, rule, i, value, list,
                    $"{shown}: '{value}' is not in the {Lower(list.Label)} list.", $"The value comes from {layer}.");
            }
        }
        if (required && !valid && !(rule.Empty && count > 0))
        {
            var severity = changedSection ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
            var why = rule.Required ? "" : $" A section with '{rule.RequiredWith}' needs it.";
            Add(result, severity, target, key, ListProblemKind.RequiredMissing, rule, -1, null, null,
                $"the key '{key}' is missing.{why}", rule.MissingText);
        }
    }

    /// <summary>
    /// True when a section misses a required key, as <see cref="Check"/> finds it: the rule needs a line of the key, and
    /// no line that the engine reads has a value at the place of the rule. The key rule of the save gate runs it on the
    /// base version of a section (decision D508 of the console filter design).
    /// </summary>
    /// <param name="multiline">True when the engine reads each line of the key, false when it reads only the last line
    /// with a value.</param>
    internal static bool MissesRequired(Section section, string key, ListRule rule, bool multiline)
    {
        var lines = section.Where(e => Is(e.Name, key)).ToList();
        if (!IsRequired(section, rule) || rule.Empty && lines.Count > 0)
            return false;
        var read = multiline ? lines : lines.Where(l => l.Count > 0).TakeLast(1);
        return read.All(line => ValueOf(line, rule).Length == 0);
    }

    /// <summary>True when the rule needs a line of its key in the section.</summary>
    private static bool IsRequired(Section section, ListRule rule) =>
        rule.Required || rule.RequiredWith != null && section.Any(e => Is(e.Name, rule.RequiredWith));

    /// <summary>The value of a line at the place of the rule, or an empty text. A scene line gives the place of its
    /// value (see <see cref="ListRule.Scene"/>).</summary>
    private static string ValueOf(Entry line, ListRule rule) =>
        rule.IndexIn(line) is >= 0 and var at ? line[at].ToString().Trim() : "";

    /// <summary>An error for a section of the active mod whose name has the FLHash of another name of a hash
    /// list.</summary>
    private static void CheckHash(DataLists lists, SectionRef target, Section section, EditDocument document, int index,
        List<ListProblem> result)
    {
        // A section of B keeps its name, unless the active mod renames it.
        if (document.InBase(document.IdentityAt(index)) && !document.IsRenamed(document.IdentityAt(index)))
            return;
        foreach (var definition in lists.SourcesOf(target.Path, section.Name).Where(d => d.Hash))
        {
            if (lists.List(definition.Id) is not { Collisions.Count: > 0 } list)
                continue;
            foreach (var name in DataLists.Names(section, definition.Source!))
            {
                foreach (var group in list.CollisionsOf(name))
                {
                    var others = string.Join(", ", group.Where(n => !Is(n, name)).Select(n => $"'{n}'"));
                    var key = SectionKeys.For(section.Name).KeyEntry ?? SectionKeys.DefaultKey;
                    Add(result, DiagnosticSeverity.Error, target, key, ListProblemKind.HashCollision, null, 0, name, list,
                        $"the name '{name}' has the FLHash of {others}.", "The game stops loading with this name.",
                        "Give the section another name.");
                }
            }
        }
    }

    /// <param name="reason">What is wrong.</param>
    /// <param name="consequence">What the game does with the data.</param>
    /// <param name="fix">What to do, or null.</param>
    private static void Add(List<ListProblem> result, DiagnosticSeverity severity, SectionRef target, string key,
        ListProblemKind kind, ListRule? rule, int line, string? value, ListView? list, string reason, string consequence,
        string? fix = null)
    {
        var message = fix == null ? $"{target.Id}: {reason} {consequence}" : $"{target.Id}: {reason} {consequence} {fix}";
        result.Add(new ListProblem(new Diagnostic(severity, target.Path, message) { Target = target, Key = key },
            kind, rule, line, value, list)
        {
            Effect = rule?.Effect ?? RuleEffect.LoadBreak,
            Reason = reason,
            Consequence = consequence,
        });
    }

    internal static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>"an" before a label that starts with a vowel, else "a".</summary>
    private static string Article(string label) => label.Length > 0 && "aeiouAEIOU".Contains(label[0]) ? "an" : "a";

    private static bool Is(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
