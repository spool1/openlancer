using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Editor;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// No UI text names an internal milestone (#56). A modder cannot know the IDs of the roadmap, so a text gives a plain
/// reason instead. The test reads the texts of the editor, of the data layer and of the command layer: each string
/// literal of their IL, each const string, each string argument of a custom attribute, and each string value of their
/// embedded JSON files.
/// </summary>
/// <remarks>The command layer is in the scan because it holds the refusal texts of the Inspector fields. The texts of
/// all three assemblies are in the scan, not only the texts that a view shows. A text that no view shows can still
/// get to the Console or to an agent.</remarks>
public sealed class UiTextTests
{
    private static readonly Assembly[] Scanned =
        [typeof(EditorWindow).Assembly, typeof(MissionChecks).Assembly, typeof(FieldBinding).Assembly];

    /// <summary>The JSON keys whose values are not UI texts: the record of a fact, the story slot of a script and the
    /// path of a file.</summary>
    private static readonly HashSet<string> SkippedKeys = ["evidence", "slot", "path"];

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The texts of the scan. The scan reads the IL of three assemblies, so the facts share one read.</summary>
    private static readonly Lazy<List<Text>> ScannedTexts = new(() => Texts().ToList());

    [Fact]
    public void NoUiTextNamesAMilestone()
    {
        var matcher = MilestoneText.FromRoadmap();
        var found = ScannedTexts.Value
            .Select(t => (Text: t, Hit: matcher.Hit(t.Value)))
            .Where(t => t.Hit != null)
            .Select(t => $"{t.Text.Where}: '{t.Hit}' in \"{t.Text.Value}\"")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(found.Count == 0,
            "UI texts that name a milestone. Give a plain reason instead:\n" + string.Join("\n", found));
    }

    [Theory]
    [InlineData("Jump gates come with milestone M27.", "milestone")]
    [InlineData("The Milestone list", "Milestone")]
    [InlineData("see rule M21 first", "M21")]
    [InlineData("M6c-3", "M6c-3")]
    [InlineData("This comes in MCP1-4.", "MCP1-4")]
    [InlineData("the encounters of a zone (M3b)", "M3b")]
    [InlineData("M27/M28", "M27")]
    [InlineData("a row of GA1", "GA1")]
    [InlineData("after M11", "M11")]
    [InlineData("M34", "M34")]
    public void TheMatcherFindsAMilestone(string text, string hit) =>
        Assert.Equal(hit, MilestoneText.FromRoadmap().Hit(text));

    /// <summary>A rule ID of the checks is data, so a text that is exactly one passes. A story slot name is not a
    /// roadmap ID, and a part of a path is not an ID.</summary>
    [Theory]
    [InlineData("M21")]
    [InlineData("M28")]
    [InlineData("P01")]
    [InlineData("M02")]
    [InlineData(@"DATA\MISSIONS\M11\m11.ini")]
    [InlineData("DATA/MISSIONS/M11/m11.ini")]
    [InlineData("m11.ini")]
    [InlineData("Base game")]
    [InlineData("ID")]
    [InlineData("XM27")]
    public void TheMatcherPassesARuleIdASlotAndAPath(string text) =>
        Assert.Null(MilestoneText.FromRoadmap().Hit(text));

    /// <summary>The IDs are the cells of the first column of the roadmap tables that start with letters and then a
    /// digit. The header and the row of the base work are not IDs.</summary>
    [Fact]
    public void TheIdsAreTheFirstColumnOfTheRoadmap()
    {
        var ids = MilestoneText.RoadmapIds(File.ReadAllLines(RepoFiles.Of("ROADMAP.md")));

        Assert.Superset(new HashSet<string> { "M1", "M3b", "M8c", "M11c", "M27", "MCP1-2", "GA1", "H1", "R1" },
            ids.ToHashSet());
        Assert.DoesNotContain("ID", ids);
        Assert.DoesNotContain("Base", ids);
    }

    /// <summary>The scan reads each kind of text, so <see cref="NoUiTextNamesAMilestone"/> cannot pass on an empty
    /// read.</summary>
    [Fact]
    public void TheScanReadsEachKindOfText()
    {
        var kinds = ScannedTexts.Value.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Count());

        Assert.All(Enum.GetValues<TextKind>(), kind => Assert.True(kinds.GetValueOrDefault(kind) > 0,
            $"The scan reads no text of the kind {kind}."));
    }

    /// <summary>The scan reads each embedded JSON file of the scanned assemblies. These files hold labels and help
    /// texts of the editor, and the texts of the agent tools.</summary>
    [Theory]
    [InlineData("OpenLancer.Data.Editing.Lists.list-rules.json")]
    [InlineData("OpenLancer.Data.Missions.mission-schema.json")]
    [InlineData("OpenLancer.Data.Missions.mission-prefabs.json")]
    [InlineData("OpenLancer.Studio.Agent.agent-tools.json")]
    public void TheScanReadsEachJsonFile(string resource) =>
        Assert.Contains(ScannedTexts.Value,
            t => t.Kind == TextKind.Json && t.Where.StartsWith(resource + ".", StringComparison.Ordinal));

    /// <summary>The JSON scan reads each string value. It leaves out the comment keys and the keys whose values are not
    /// UI texts.</summary>
    [Fact]
    public void TheJsonScanLeavesOutTheCommentsAndTheSkippedKeys()
    {
        using var document = JsonDocument.Parse("""
            {
              "// about": "milestone M27",
              "slots": [ { "slot": "M11", "path": "DATA\\M27\\a.ini", "label": "First" } ],
              "rows": [ { "evidence": "GA1 row 3", "texts": [ "Second", 3 ] } ]
            }
            """);

        Assert.Equal(["First", "Second"], JsonTexts("sample", document.RootElement).Select(t => t.Value));
    }

    /// <summary>The walker finds each form of a string literal of <see cref="LiteralSample"/>, and gives each one to the
    /// type that holds it.</summary>
    [Fact]
    public void TheWalkerFindsEachFormOfALiteral()
    {
        var texts = typeof(UiTextTests).Assembly.GetTypes().SelectMany(IlWalker.Literals)
            .Where(l => l.Owner == typeof(LiteralSample)).Select(l => l.Text).ToList();

        Assert.Contains("a direct text", texts);
        Assert.Contains(texts, t => t.StartsWith("an interpolated part ", StringComparison.Ordinal));
        Assert.Contains("a text in a lambda", texts);
        Assert.Contains("a text of a field", texts);
        Assert.Contains(LiteralSample.Constant, texts);
        Assert.Equal(LiteralSample.Constant, ConstTexts(typeof(LiteralSample)).Single().Value);
        Assert.Contains(AttributeTexts(typeof(LiteralSample)), t => t.Value == "a text of an attribute");
    }

    /// <summary>Each text of the scanned assemblies.</summary>
    private static IEnumerable<Text> Texts()
    {
        foreach (var assembly in Scanned)
        {
            foreach (var text in AttributeTexts(assembly))
                yield return text;
            foreach (var type in assembly.GetTypes())
            {
                foreach (var literal in IlWalker.Literals(type))
                    yield return new Text(TextKind.Literal, $"{literal.Owner.FullName}.{literal.Method.Name}", literal.Text);
                foreach (var text in ConstTexts(type).Concat(AttributeTexts(type)))
                    yield return text;
            }

            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var document = JsonDocument.Parse(stream);
                foreach (var text in JsonTexts(name, document.RootElement))
                    yield return text;
            }
        }
    }

    /// <summary>The value of each const string field of a type.</summary>
    private static IEnumerable<Text> ConstTexts(Type type) =>
        from field in type.GetFields(All)
        where field.IsLiteral && field.FieldType == typeof(string) && field.GetRawConstantValue() is string
        select new Text(TextKind.Const, $"{type.FullName}.{field.Name}", (string)field.GetRawConstantValue()!);

    /// <summary>Each string argument of each custom attribute of an assembly.</summary>
    private static IEnumerable<Text> AttributeTexts(Assembly assembly) =>
        Arguments(assembly.GetCustomAttributesData(), assembly.GetName().Name!);

    /// <summary>Each string argument of each custom attribute of a type, of its members and of their parameters.</summary>
    private static IEnumerable<Text> AttributeTexts(Type type)
    {
        var texts = Arguments(type.GetCustomAttributesData(), type.FullName!);
        foreach (var member in type.GetMembers(All).Where(m => m is not Type))
        {
            var where = $"{type.FullName}.{member.Name}";
            texts = texts.Concat(Arguments(member.GetCustomAttributesData(), where));
            if (member is MethodBase method)
            {
                texts = method.GetParameters().Aggregate(texts,
                    (all, parameter) => all.Concat(Arguments(parameter.GetCustomAttributesData(), where)));
            }
        }
        return texts;
    }

    private static IEnumerable<Text> Arguments(IEnumerable<CustomAttributeData> attributes, string where) =>
        from attribute in attributes
        from argument in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(n => n.TypedValue))
        from value in Values(argument)
        select new Text(TextKind.Attribute, $"{where} [{attribute.AttributeType.Name}]", value);

    /// <summary>The strings of an attribute argument, also of an array argument.</summary>
    private static IEnumerable<string> Values(CustomAttributeTypedArgument argument) => argument.Value switch
    {
        string text => [text],
        IEnumerable<CustomAttributeTypedArgument> items => items.SelectMany(Values),
        _ => [],
    };

    /// <summary>Each string value of a JSON element, without the comment keys ("// ...") and the skipped keys.</summary>
    private static IEnumerable<Text> JsonTexts(string where, JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [new Text(TextKind.Json, where, element.GetString()!)],
        JsonValueKind.Array => element.EnumerateArray().SelectMany((item, i) => JsonTexts($"{where}[{i}]", item)),
        JsonValueKind.Object => element.EnumerateObject()
            .Where(p => !p.Name.StartsWith("// ", StringComparison.Ordinal) && !SkippedKeys.Contains(p.Name))
            .SelectMany(p => JsonTexts($"{where}.{p.Name}", p.Value)),
        _ => [],
    };

    private enum TextKind
    {
        Literal,
        Const,
        Attribute,
        Json,
    }

    /// <summary>A text of the scan: its kind, where it is and its value.</summary>
    private sealed record Text(TextKind Kind, string Where, string Value);
}

/// <summary>
/// Finds an internal milestone in a text: the word "milestone" in any case, or an ID of the roadmap. The IDs are the
/// cells of the first column of ROADMAP.md, and also M11 and M20 to M34. An ID can have a part suffix, for example
/// M6c-3.
/// </summary>
/// <remarks>Each ID is a whole word with the case of the roadmap, so "m11.ini" has no ID. An ID between two path
/// separators is a folder of a path, for example in <c>DATA\MISSIONS\M11\m11.ini</c>. A text that is exactly a rule ID
/// of the mission checks or of the prefab checks passes, because a rule ID is data, not prose.</remarks>
internal sealed partial class MilestoneText
{
    private readonly Regex ids;
    private readonly HashSet<string> rules;

    private MilestoneText(IEnumerable<string> roadmapIds, IEnumerable<string> ruleIds)
    {
        // A part suffix is not a part of the base ID: MCP1-2 and MCP1-5 both have the base MCP1.
        var bases = roadmapIds.Select(id => PartSuffix().Replace(id, ""))
            .Concat(["M11", .. Enumerable.Range(20, 15).Select(n => $"M{n}")])
            .Distinct()
            .OrderByDescending(id => id.Length)
            .Select(Regex.Escape);
        ids = new Regex($@"(?<![\p{{L}}\p{{N}}_])(?:{string.Join("|", bases)})(?:-\d+)?(?![\p{{L}}\p{{N}}_])");
        rules = ruleIds.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The matcher with the IDs of ROADMAP.md and the rule IDs of the checks.</summary>
    public static MilestoneText FromRoadmap() =>
        new(RoadmapIds(File.ReadAllLines(RepoFiles.Of("ROADMAP.md"))), MissionChecks.Rules.Concat(PrefabLinks.Rules));

    /// <summary>The first cell of each table row that starts with letters and then a digit.</summary>
    public static List<string> RoadmapIds(IEnumerable<string> lines) =>
        lines.Where(l => l.StartsWith('|'))
            .Select(l => l.Split('|')[1].Trim())
            .Where(cell => RoadmapId().IsMatch(cell))
            .ToList();

    /// <summary>The first milestone in a text, or null.</summary>
    public string? Hit(string text)
    {
        if (rules.Contains(text))
            return null;
        if (Word().Match(text) is { Success: true } word)
            return word.Value;
        return ids.Matches(text).FirstOrDefault(m => !IsFolderOfAPath(text, m))?.Value;
    }

    private static bool IsFolderOfAPath(string text, Match match) =>
        match.Index > 0 && text[match.Index - 1] is '\\' or '/' &&
        match.Index + match.Length < text.Length && text[match.Index + match.Length] is '\\' or '/';

    [GeneratedRegex("milestone", RegexOptions.IgnoreCase)]
    private static partial Regex Word();

    [GeneratedRegex(@"^[A-Za-z]+\d")]
    private static partial Regex RoadmapId();

    [GeneratedRegex(@"-\d+$")]
    private static partial Regex PartSuffix();
}

/// <summary>
/// A type with a string literal in each form that the walker of <see cref="UiTextTests"/> reads: a direct text, an
/// interpolated text, a text in a lambda, a field initializer, a const and an attribute argument. The test of the walker
/// reads it.
/// </summary>
[System.ComponentModel.Description("a text of an attribute")]
internal static class LiteralSample
{
    public const string Constant = "a const text";

    private static readonly string Field = "a text of a field";

    public static string Direct() => "a direct text";

    public static string Interpolated(int count) => $"an interpolated part {count} and more";

    public static Func<string> Lambda() => () => "a text in a lambda";

    public static string WithConstant() => Constant + Field;
}
