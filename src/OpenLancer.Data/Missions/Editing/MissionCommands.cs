using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// A typed mission command (section 4.9 of the M6b design). Each command checks its inputs against the draft, changes
/// W through the scope of the session, gives its inverse and reports what it added, changed, removed or renamed. JSON
/// names a command with <c>kind</c> (section 4.13).
/// </summary>
[JsonConverter(typeof(MissionCommandConverter))]
public abstract record MissionCommand
{
    /// <summary>The label of a history entry of the command alone.</summary>
    /// <remarks>The property is not virtual, so the JSON form of each command leaves it out.</remarks>
    [JsonIgnore]
    public string HistoryLabel => Describe();

    /// <summary>The label of a history entry of the command alone.</summary>
    protected abstract string Describe();

    /// <summary>Runs the command on the draft. A refusal throws an <see cref="EditException"/>.</summary>
    internal abstract void Run(MissionDraft draft);
}

/// <summary>What a delete does with the lines that name the item.</summary>
public enum ReferencesMode
{
    /// <summary>Each line stays and shows "Missing" (decision D81).</summary>
    Keep,

    /// <summary>Each line goes with the item.</summary>
    Remove,
}

/// <summary>The kind of a link from one trigger to another (section 4.9 of the M6b design).</summary>
public enum LinkKind
{
    /// <summary><c>Act_ActTrig</c>.</summary>
    Start,

    /// <summary><c>Act_DeactTrig</c>.</summary>
    Stop,

    /// <summary><c>Act_DeactTrig</c>, then <c>Act_ActTrig</c> (the order of TR9).</summary>
    Restart,
}

/// <summary>Commands that give one history entry and one validation (section 4.4 of the M6b design).</summary>
public sealed record MissionBatch(string Label, IReadOnlyList<MissionCommand> Commands)
{
    public MissionEditOptions Options { get; init; } = MissionEditOptions.Default;

    /// <summary>The script versions that the caller read, by install path. A version that differs refuses the batch
    /// with <c>ScriptChanged</c>. The paths compare without letter case.</summary>
    public IReadOnlyDictionary<string, long> ExpectVersions { get; init; } = new Dictionary<string, long>();

    /// <summary>The revision that the caller read, or null (section 4.0.5 of the M6b design).</summary>
    [JsonConverter(typeof(NullableLongAsText))]
    public long? IfRevision { get; init; }

    /// <summary>True runs a batch that needs a confirmation (section 4.0.5 of the M6b design).</summary>
    public bool Confirm { get; init; }

    /// <summary>Who makes the batch. A host sets it. A JSON request cannot set it.</summary>
    [JsonIgnore]
    public EditOrigin Origin { get; init; } = EditOrigin.User;

    /// <summary>The label of the history entry: the label, or the label of the one command.</summary>
    internal string EntryLabel => Label.Length > 0 ? Label : Commands.Count == 1 ? Commands[0].HistoryLabel : "Mission edit";
}

/// <summary>The strict options of a batch (section 4.0.5 of the M6b design, decision D681).</summary>
public sealed record MissionEditOptions(bool RejectNewErrors = false, bool RejectNewWarnings = false)
{
    public static readonly MissionEditOptions Default = new();
    public static readonly MissionEditOptions Strict = new(RejectNewErrors: true);
}

/// <summary>The result of a mission request: the shared result with the inverse and the row moves (section 4.4 of
/// the M6b design).</summary>
public sealed record MissionEditResult(EditStatus Status) : EditResult(Status)
{
    /// <summary>The command that reverts the request after later edits, or null when the request changed
    /// nothing.</summary>
    public MissionCommand? Inverse { get; init; }

    /// <summary>The lines whose row index changed, so the studio keeps a selection on the same line.</summary>
    public IReadOnlyList<RowMove> RowMoves { get; init; } = [];

    /// <summary>
    /// The confirmations of a request with <see cref="MissionBatch.Confirm"/> in the batch of a caller, as data. The
    /// request does not refuse for them. The caller asks for them as for the confirmations of its other plans, for
    /// example an agent call (decision D712 of the MCP1 design). Empty for each other request.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<EditError> Asked { get; init; } = [];

    /// <summary>The mission result of a shared result.</summary>
    internal static MissionEditResult From(EditResult result) => new(result.Status)
    {
        Errors = result.Errors,
        Label = result.Label,
        UndoLabel = result.UndoLabel,
        Revision = result.Revision,
        Versions = result.Versions,
        Files = result.Files,
        Adds = result.Adds,
        Changes = result.Changes,
        Deletes = result.Deletes,
        Renames = result.Renames,
        Fixes = result.Fixes,
        Missing = result.Missing,
        Warnings = result.Warnings,
        Refusals = result.Refusals,
        Mentions = result.Mentions,
        NewProblems = result.NewProblems,
        FixedProblems = result.FixedProblems,
        NeedsConfirmation = result.NeedsConfirmation,
        Select = result.Select,
        Notes = result.Notes,
        Patch = result.Patch,
        Summary = result.Summary,
    };
}

/// <summary>A line of a trigger whose row index changed.</summary>
public sealed record RowMove(TriggerRef Trigger, int From, int To);

/// <summary>The scripts that a change of the history changed, their new versions, and the renames of their triggers
/// (section 4.4 of the M6b design).</summary>
public sealed record MissionScriptsChange(IReadOnlyDictionary<string, long> Versions,
    IReadOnlyList<(TriggerRef From, TriggerRef To)> Renames);

/// <summary>A section with its lines by position, for the commands that put a section back.</summary>
/// <param name="Type">The section type, for example <c>MsnShip</c>.</param>
public sealed record RawSection(string Type, IReadOnlyList<RawEntry> Entries);

/// <summary>The lines of a script that name an item, by the reference kinds of the schema (section 4.10 of the M6b
/// design).</summary>
internal static class MissionLinks
{
    /// <summary>The kinds of the values that name a section of a type.</summary>
    public static IReadOnlyList<MissionValueKind> KindsOf(string type) => MissionSection.KindOf(type) switch
    {
        MissionSectionKind.Trigger => [MissionValueKind.Trigger],
        MissionSectionKind.Ship => [MissionValueKind.Ship, MissionValueKind.Group, MissionValueKind.Object],
        MissionSectionKind.Formation => [MissionValueKind.Formation, MissionValueKind.Group, MissionValueKind.Object],
        MissionSectionKind.Solar => [MissionValueKind.Solar, MissionValueKind.Object],
        MissionSectionKind.Loot => [MissionValueKind.Loot, MissionValueKind.Object],
        MissionSectionKind.OrderList => [MissionValueKind.ObjList],
        MissionSectionKind.Objective => [MissionValueKind.Objective],
        MissionSectionKind.Npc => [MissionValueKind.Npc],
        MissionSectionKind.Conversation => [MissionValueKind.Dialog],
        MissionSectionKind.RandomEncounter => [MissionValueKind.RandomEncounter],
        MissionSectionKind.MetaBehavior => [MissionValueKind.MetaBehavior],
        _ when MissionIndex.Is(type, "NPCShipArch") => [MissionValueKind.NpcShipArch],
        _ => [],
    };

    /// <summary>
    /// The values of the lines of sections that name a name of some kinds, without letter case: the index of the
    /// section, the row and the index of the value. A line of a trigger before its nickname names nothing in the game,
    /// but a rename changes it too.
    /// </summary>
    public static List<(int Section, int Row, int Value)> Naming(MissionSchema schema, IReadOnlyList<Section> sections,
        IReadOnlyCollection<MissionValueKind> kinds, string name)
    {
        var result = new List<(int, int, int)>();
        if (kinds.Count == 0)
            return result;
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            var type = section.Name;
            var kind = MissionSection.KindOf(type);
            var spec = MissionIndex.Is(type, "NPCShipArch") ? schema.ShipBuild : schema.Section(type);
            for (var r = 0; r < section.Count; r++)
            {
                var entry = section[r];
                IReadOnlyList<MissionValueSpec>? specs =
                    kind == MissionSectionKind.Trigger && (TriggerRules.IsCondition(entry.Name) || TriggerRules.IsAction(entry.Name))
                        ? schema.Name(entry.Name)?.Values
                        : kind == MissionSectionKind.OrderList && schema.Directive(entry.Name) is { } directive
                            ? directive.Values
                            : spec?.Key(entry.Name)?.ValueSpecs;
                if (specs == null || spec?.Key(entry.Name) is { Unused: true })
                    continue;
                foreach (var valueSpec in specs.Where(v => kinds.Contains(v.Kind)))
                {
                    var end = Math.Min(valueSpec.End, entry.Count);
                    for (var v = valueSpec.Index; v < end; v++)
                    {
                        if (MissionIndex.Text(entry[v]).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                            result.Add((s, r, v));
                    }
                }
            }
        }
        return result;
    }
}

/// <summary>The inverse of a change of the lines of one section: line commands that give the old lines back (section
/// 4.8 of the M6b design).</summary>
internal static class LineDiff
{
    /// <summary>
    /// The commands that turn the new lines of a section into the old lines: the removal of each new line that the old
    /// lines do not have, from the last to the first, then the insert of each old line that the new lines do not have,
    /// from the first to the last. Two lines are the same when their keys and value texts are the same.
    /// </summary>
    /// <param name="item">The section after the change.</param>
    public static MissionCommand Inverse(ItemRef item, IReadOnlyList<Entry> before, IReadOnlyList<Entry> after)
    {
        var (keptOld, keptNew) = CommonOrder.Kept(before.Select(Text).ToList(), after.Select(Text).ToList(),
            StringComparer.Ordinal);
        var commands = new List<MissionCommand>();
        for (var j = after.Count - 1; j >= 0; j--)
        {
            if (!keptNew[j])
                commands.Add(new RemoveLine(new RowRef(item, j) { ExpectKey = after[j].Name }));
        }
        for (var i = 0; i < before.Count; i++)
        {
            if (!keptOld[i])
                commands.Add(new InsertLine(item, MissionDraft.RawLine(before[i])) { Before = i });
        }
        return commands.Count == 1 ? commands[0] : new CommandGroup(commands);
    }

    /// <summary>True when two lists of lines have the same keys and value texts.</summary>
    public static bool Same(IReadOnlyList<Entry> a, IReadOnlyList<Entry> b) =>
        a.Count == b.Count && a.Select(Text).SequenceEqual(b.Select(Text));

    /// <summary>The key and the value texts of a line as one text. Two lines with the same text are the same
    /// line.</summary>
    public static string Text(Entry entry) =>
        entry.Name.ToLowerInvariant() + "\u001f" + string.Join("\u001f", MissionValues.TextsOf(entry));
}

/// <summary>The longest common order of two lists: the items of each list that a longest common subsequence keeps.
/// The inverse of a change of lines and the plan of a raw edit use it.</summary>
internal static class CommonOrder
{
    /// <returns>For each item of each list, true when the common order keeps it.</returns>
    public static (bool[] KeptA, bool[] KeptB) Kept<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T> comparer)
    {
        var lengths = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
                lengths[i, j] = comparer.Equals(a[i], b[j]) ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        }
        var keptA = new bool[a.Count];
        var keptB = new bool[b.Count];
        for (int i = 0, j = 0; i < a.Count && j < b.Count;)
        {
            if (comparer.Equals(a[i], b[j]))
            {
                keptA[i++] = true;
                keptB[j++] = true;
            }
            else if (lengths[i + 1, j] >= lengths[i, j + 1])
                i++;
            else
                j++;
        }
        return (keptA, keptB);
    }
}

/// <summary>The revision as JSON text, or null.</summary>
public sealed class NullableLongAsText : JsonConverter<long?>
{
    private static readonly EditJson.LongAsText Inner = new();

    public override long? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(long), options);

    public override void Write(System.Text.Json.Utf8JsonWriter writer, long? value,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (value is { } number)
            Inner.Write(writer, number, options);
        else
            writer.WriteNullValue();
    }
}
