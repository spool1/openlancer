using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;

namespace OpenLancer.Data.Missions;

/// <summary>How one name of a mission line resolves.</summary>
public enum MissionReferenceState
{
    /// <summary>The name resolves: an item of the script, a name of the game data or a file.</summary>
    Found,

    /// <summary>The name does not resolve.</summary>
    Missing,

    /// <summary>The name differs from a name or a word only in letter case, and the game compares with letter case
    /// (TR16).</summary>
    WrongCase,

    /// <summary>The value is a word of the value, for example <c>ANY</c>, <c>Player</c> or <c>no_ol</c>.</summary>
    Token,

    /// <summary>The value is empty.</summary>
    Empty,

    /// <summary>The editor does not know the names of the kind, so it does not check the name.</summary>
    Unknown,
}

/// <summary>One name in a mission line, and how it resolves (section 14.3 of the M6a design).</summary>
/// <param name="ValueIndex">The index of the value in the line.</param>
public sealed record MissionReference(MissionLine Line, int ValueIndex, MissionValueSpec Spec, string Name)
{
    public MissionReferenceState State { get; init; }

    /// <summary>The section of the script that the name resolves to, or null.</summary>
    public MissionSection? Section { get; init; }

    /// <summary>The right text of a <see cref="MissionReferenceState.WrongCase"/> name, or the word of a
    /// <see cref="MissionReferenceState.Token"/> value.</summary>
    public string? Expected { get; init; }

    public MissionValueKind Kind => Spec.Kind;
}

/// <summary>
/// One line of a mission section, with the spec of its key and the names in it.
/// </summary>
public sealed class MissionLine
{
    private readonly List<MissionReference> references = [];

    internal MissionLine(MissionSection section, int entry, MissionName? name, MissionKeySpec? key)
    {
        Section = section;
        Entry = entry;
        Name = name;
        KeySpec = key;
    }

    public MissionSection Section { get; }

    /// <summary>The index of the line in its section.</summary>
    public int Entry { get; }

    public Entry Line => Section.Section[Entry];

    /// <summary>The key as written.</summary>
    public string Key => Line.Name;

    /// <summary>The condition, action or order of the key, or null.</summary>
    public MissionName? Name { get; }

    /// <summary>The section key of the key, or null.</summary>
    public MissionKeySpec? KeySpec { get; }

    /// <summary>The trigger of a line of a <c>[Trigger]</c> section, or null.</summary>
    public MissionTrigger? Trigger { get; internal set; }

    /// <summary>True when the schema knows the key.</summary>
    public bool Known => Name != null || KeySpec != null;

    /// <summary>The specs of the values of the line.</summary>
    public IReadOnlyList<MissionValueSpec> Specs => Name?.Values ?? KeySpec?.ValueSpecsOf(Value(0)) ?? [];

    /// <summary>The names in the line, in value order.</summary>
    public IReadOnlyList<MissionReference> References => references;

    /// <summary>The OpenLancer mark of a condition or an action.</summary>
    public EngineMark Mark { get; internal set; }

    /// <summary>The number of values of the line.</summary>
    public int Count => Line.Count;

    /// <summary>The text of a value as the game reads it, or null when the line has no such value or it is
    /// empty.</summary>
    public string? Value(int index) =>
        index >= 0 && index < Line.Count && RetailText.Of(Line[index]) is { } text && text.Trim().Length > 0
            ? text.Trim()
            : null;

    /// <summary>The text of a value spec of the line: one value, the values of a position joined with ", ", or the
    /// rest of the line. Null when the line has none of its values.</summary>
    public string? ValueOf(MissionValueSpec spec)
    {
        var end = Math.Min(spec.End, Line.Count);
        var parts = new List<string>();
        for (var i = spec.Index; i < end; i++)
        {
            if (Value(i) is { } text)
                parts.Add(text);
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The text of a value of the line by its name in the spec, or null.</summary>
    public string? ValueOf(string name) => Name?.Value(name) is { } spec ? ValueOf(spec) : null;

    /// <summary>The sentence of a condition line (section 13.2 of the M6a design), or the plain name with the values
    /// for another line.</summary>
    public string Sentence() => Name?.Sentence(ValueOf) ?? Key;

    internal void Add(MissionReference reference) => references.Add(reference);

    public override string ToString() => $"{Section} line {Entry}: {Key}";
}

/// <summary>One <c>[Trigger]</c> section (section 14.3 of the M6a design).</summary>
public sealed class MissionTrigger
{
    internal MissionTrigger(MissionSection section, string nickname, int nicknameLine)
    {
        Section = section;
        Nickname = nickname;
        NicknameLine = nicknameLine;
    }

    public MissionSection Section { get; }

    /// <summary>The value of the first <c>nickname</c> line.</summary>
    public string Nickname { get; }

    /// <summary>The index of the first <c>nickname</c> line.</summary>
    public int NicknameLine { get; }

    /// <summary>True when an earlier trigger has the same nickname. The game skips the section (TR4).</summary>
    public bool IsDuplicate { get; internal set; }

    /// <summary>The lines before the nickname. The game ignores them (TR2).</summary>
    public IReadOnlyList<int> LinesBeforeNickname { get; internal set; } = [];

    /// <summary>True for <c>InitState = ACTIVE</c> after the nickname: the trigger starts with the mission.</summary>
    public bool StartsActive { get; internal set; }

    /// <summary>True for an effective <c>repeatable = true</c> after the nickname (TR10).</summary>
    public bool Repeats { get; internal set; }

    /// <summary>The value of the first <c>system</c> line after the nickname, or null. The game does not use it
    /// (TR3).</summary>
    public string? SystemHint { get; internal set; }

    /// <summary>The condition lines after the nickname, in file order. The last one counts (TR1).</summary>
    public IReadOnlyList<MissionLine> Conditions { get; internal set; } = [];

    /// <summary>The condition that the game uses, or null.</summary>
    public MissionLine? Condition => Conditions.Count > 0 ? Conditions[^1] : null;

    /// <summary>The action lines after the nickname, in file order.</summary>
    public IReadOnlyList<MissionLine> Actions { get; internal set; } = [];

    /// <summary>The condition and action lines after the nickname, in file order.</summary>
    public IReadOnlyList<MissionLine> Rows { get; internal set; } = [];

    /// <summary>The action lines of a key, without letter case.</summary>
    public IEnumerable<MissionLine> ActionsOf(string key) =>
        Actions.Where(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the condition key is the key, without letter case.</summary>
    public bool ConditionIs(string key) => Condition?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true;

    public override string ToString() => Nickname;
}

/// <summary>The kind of a link between two triggers (section 6.3 of the M6a design).</summary>
public enum MissionLinkKind
{
    /// <summary>An <c>Act_ActTrig</c> line.</summary>
    Start,

    /// <summary>An <c>Act_DeactTrig</c> line.</summary>
    Stop,

    /// <summary>An <c>Act_Save</c> line: after a load, the mission continues at the target.</summary>
    Load,

    /// <summary>A <c>Cnd_WatchTrigger</c> condition: the source watches the state of the target.</summary>
    Watch,
}

/// <summary>A link from a line of a trigger to a trigger name.</summary>
/// <param name="Target">The trigger of the name, or null when the script has no such trigger.</param>
/// <param name="State">The watched state of a <see cref="MissionLinkKind.Watch"/> link, or null.</param>
public sealed record MissionLink(MissionLinkKind Kind, MissionTrigger Source, MissionLine Line, string TargetName,
    MissionTrigger? Target, string? State = null);

/// <summary>
/// The index layer of one mission script (section 14.3 of the M6a design): the triggers with their lines, the links
/// between them, the cast and the other named items, the names of each line and how they resolve, the systems, and the
/// reverse references. It reads the raw layer once. Names compare without letter case, as the engine hash does.
/// </summary>
public sealed class MissionIndex
{
    private static readonly string[] ShipKeyNames =
        ["nickname", "npc", "random_name", "label", "init_objectives", "init_metabehavior", "jumper", "cargo", "hit_points",
         "radius"];

    private readonly Dictionary<string, MissionTrigger> triggerByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MissionSectionKind, Dictionary<string, List<MissionSection>>> items = [];
    private readonly Dictionary<string, List<MissionSection>> labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MissionSection> takenBy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MissionReference>> reverse = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MissionTrigger, List<MissionLink>> incoming = [];
    private readonly Dictionary<MissionSection, string> itemSystems = [];
    private readonly List<MissionLine> lines = [];
    private readonly Dictionary<MissionSection, List<MissionLine>> linesOf = [];
    private readonly List<MissionTrigger> triggers = [];
    private readonly List<MissionLink> links = [];
    private readonly List<(MissionSection Formation, int Line, string Ship)> laterMembers = [];

    private MissionIndex(MissionDocument document, MissionSchema schema, MissionWorld world, IEngineCoverage coverage,
        bool shipFile = false)
    {
        Document = document;
        Schema = schema;
        World = world;
        Coverage = coverage;
        IsShipFile = shipFile;
    }

    /// <summary>True for the index of the ship build file of a script, not of a script. The game reads only the
    /// <c>[NPCShipArch]</c> sections of such a file.</summary>
    public bool IsShipFile { get; }

    /// <summary>The keys of the type of a section: in a ship build file the keys of a ship build for an
    /// <c>[NPCShipArch]</c> and none for another type, else the keys of the type in a script.</summary>
    public MissionSectionSpec? SpecOf(MissionSection section) => IsShipFile
        ? Is(section.Type, "NPCShipArch") ? Schema.ShipBuild : null
        : Schema.Section(section.Type);

    public MissionDocument Document { get; }

    public MissionSchema Schema { get; }

    public MissionWorld World { get; }

    public IEngineCoverage Coverage { get; }

    /// <summary>Each line of each section, in file order.</summary>
    public IReadOnlyList<MissionLine> Lines => lines;

    /// <summary>Each <c>[Trigger]</c> with a nickname, in file order, duplicates too.</summary>
    public IReadOnlyList<MissionTrigger> Triggers => triggers;

    /// <summary>The links of the triggers, in file order.</summary>
    public IReadOnlyList<MissionLink> Links => links;

    /// <summary>The triggers that start with the mission, in file order, without duplicates.</summary>
    public IReadOnlyList<MissionTrigger> StartTriggers { get; private set; } = [];

    /// <summary>The triggers that a start trigger reaches over Start and Load links, the start triggers included.</summary>
    public IReadOnlySet<MissionTrigger> Reached { get; private set; } = new HashSet<MissionTrigger>();

    /// <summary>The restart lines: an <c>Act_ActTrig</c> line whose target a <c>Act_DeactTrig</c> line before it in
    /// the same trigger stops (TR9).</summary>
    public IReadOnlyList<MissionLink> Restarts { get; private set; } = [];

    /// <summary>The cancel lines: an <c>Act_DeactTrig</c> line whose target an <c>Act_ActTrig</c> line before it in the
    /// same trigger starts, and no <c>Act_ActTrig</c> line after it starts again. The game does not start the target
    /// (TR9).</summary>
    public IReadOnlyList<MissionLink> Cancels { get; private set; } = [];

    /// <summary>The triggers that start themselves.</summary>
    public IReadOnlyList<MissionTrigger> SelfStarts { get; private set; } = [];

    /// <summary>The formations that take a ship, by the nickname of the ship (MSD section 2.6 item 2).</summary>
    public IReadOnlyDictionary<string, MissionSection> TakenShips => takenBy;

    /// <summary>The <c>ship</c> lines of formations that name a ship of a later section (rule M08).</summary>
    public IReadOnlyList<(MissionSection Formation, int Line, string Ship)> LaterMembers => laterMembers;

    /// <summary>The labels with their members.</summary>
    public IReadOnlyDictionary<string, List<MissionSection>> Labels => labels;

    /// <summary>Builds the index of a document.</summary>
    /// <param name="world">The names outside the script, or <see cref="MissionWorld.Empty"/>.</param>
    /// <param name="coverage">The OpenLancer marks, or <see cref="FullCoverage.Instance"/>.</param>
    public static MissionIndex Build(MissionDocument document, MissionSchema schema, MissionWorld world,
        IEngineCoverage coverage) => Build(new MissionIndex(document, schema, world, coverage));

    /// <summary>Builds the index of the ship build file of a script: the names of each line of each
    /// <c>[NPCShipArch]</c>, for example its ship archetype, its loadout and its pilot (final finding 14 of
    /// MCP1-3).</summary>
    public static MissionIndex BuildShipFile(MissionDocument document, MissionSchema schema, MissionWorld world,
        IEngineCoverage coverage) => Build(new MissionIndex(document, schema, world, coverage, shipFile: true));

    private static MissionIndex Build(MissionIndex index)
    {
        index.CollectItems();
        index.CollectLines();
        index.CollectTriggers();
        index.ResolveNames();
        index.CollectLinks();
        index.CollectSystems();
        return index;
    }

    /// <summary>The first trigger with a nickname, without letter case, or null.</summary>
    public MissionTrigger? Trigger(string nickname) => triggerByName.GetValueOrDefault(nickname.Trim());

    /// <summary>The distinct text ids that the script names, in file order: the values of the kind text id, and the text
    /// ids of the <c>type</c> lines of the objectives.</summary>
    public IReadOnlyList<string> TextIds()
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var values = line.Section.Kind == MissionSectionKind.Objective && Is(line.Key, "type")
                ? ObjectiveTextIds(line.Line)
                : line.Specs.Where(s => s.Kind == MissionValueKind.TextId).Select(s => line.ValueOf(s)).OfType<string>();
            foreach (var id in values)
            {
                if (seen.Add(id))
                    ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>The sections of a kind with a nickname, in file order. Kinds whose duplicates the engine skips give the
    /// first section only for a lookup (see <see cref="Item"/>).</summary>
    public IReadOnlyList<MissionSection> Items(MissionSectionKind kind, string nickname) =>
        items.TryGetValue(kind, out var map) && map.TryGetValue(nickname.Trim(), out var list) ? list : [];

    /// <summary>The section that the engine uses for a nickname of a kind: the first one, or null.</summary>
    public MissionSection? Item(MissionSectionKind kind, string nickname) =>
        Items(kind, nickname) is [var first, ..] ? first : null;

    /// <summary>The links that end at a trigger.</summary>
    public IReadOnlyList<MissionLink> LinksTo(MissionTrigger trigger) =>
        incoming.TryGetValue(trigger, out var list) ? list : [];

    /// <summary>The lines whose names resolve to a name of the script or of the game data, without letter case. A name
    /// of a ship, a formation, a solar, a loot, a label or an object shares one list.</summary>
    public IReadOnlyList<MissionReference> ReferencesTo(MissionValueKind kind, string name) =>
        reverse.TryGetValue(ReverseKey(kind, name), out var list) ? list : [];

    /// <summary>The system of an item of the cast (section 7.2 of the M6a design): its <c>system</c> line, else the
    /// system hint of the first trigger that spawns or moves it, else the system of the first conversation where it
    /// speaks. Null when the index finds none.</summary>
    public string? SystemOf(MissionSection item) => itemSystems.GetValueOrDefault(item);

    /// <summary>The lines of a section, in file order.</summary>
    public IReadOnlyList<MissionLine> LinesOf(MissionSection section) => linesOf.GetValueOrDefault(section) ?? [];

    /// <summary>True when the name is an object of the label rule of the script: a ship, a formation, a solar, a loot
    /// or a label.</summary>
    public bool IsScriptObject(string name) =>
        Item(MissionSectionKind.Ship, name) != null || Item(MissionSectionKind.Formation, name) != null ||
        Item(MissionSectionKind.Solar, name) != null || Item(MissionSectionKind.Loot, name) != null ||
        labels.ContainsKey(name.Trim());

    private void CollectItems()
    {
        foreach (var section in Document.Sections)
        {
            if (section.Kind is MissionSectionKind.Other or MissionSectionKind.Mission or MissionSectionKind.Trigger)
                continue;
            // The attackers of a random encounter get the label of its section (MSD section 2.6 item 10).
            if (section.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation or MissionSectionKind.Solar or
                MissionSectionKind.Loot or MissionSectionKind.RandomEncounter)
            {
                foreach (var label in section.Section.Where(e => Is(e.Name, "label") && e.Count > 0))
                {
                    var name = label[0].ToString()!.Trim();
                    if (!labels.TryGetValue(name, out var members))
                        labels[name] = members = [];
                    if (!members.Contains(section))
                        members.Add(section);
                }
            }
            if (section.Nickname == null)
                continue;
            if (!items.TryGetValue(section.Kind, out var map))
                items[section.Kind] = map = new Dictionary<string, List<MissionSection>>(StringComparer.OrdinalIgnoreCase);
            if (!map.TryGetValue(section.Nickname, out var list))
                map[section.Nickname] = list = [];
            list.Add(section);
        }

        // A ship line of a formation takes the first ship of an earlier [MsnShip] group, and the engine deletes that
        // group. A ship of a later section is not found (MSD section 2.6 item 2).
        var seenShips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in Document.Sections)
        {
            if (section.Kind == MissionSectionKind.Ship && section.Nickname != null)
                seenShips.Add(section.Nickname);
            if (section.Kind is not (MissionSectionKind.Ship or MissionSectionKind.Formation))
                continue;
            for (var i = 0; i < section.Section.Count; i++)
            {
                var entry = section.Section[i];
                if (!Is(entry.Name, "ship") || entry.Count == 0)
                    continue;
                var ship = entry[0].ToString()!.Trim();
                if (seenShips.Contains(ship))
                    takenBy.TryAdd(ship, section);
                else if (Item(MissionSectionKind.Ship, ship) != null)
                    laterMembers.Add((section, i, ship));
            }
        }
    }

    private void CollectLines()
    {
        foreach (var section in Document.Sections)
        {
            var spec = SpecOf(section);
            for (var i = 0; i < section.Section.Count; i++)
            {
                var key = section.Section[i].Name;
                MissionName? name = null;
                MissionKeySpec? keySpec = null;
                if (section.Kind == MissionSectionKind.Trigger && (TriggerRules.IsCondition(key) || TriggerRules.IsAction(key)))
                    name = Schema.Name(key);
                else if (section.Kind == MissionSectionKind.OrderList && Schema.Directive(key) is { } directive)
                    name = directive;
                else
                    keySpec = spec?.Key(key);
                var line = new MissionLine(section, i, name, keySpec);
                if (name is { Kind: not MissionNameKind.Directive })
                    line.Mark = Coverage.Of(name.Name);
                lines.Add(line);
                if (!linesOf.TryGetValue(section, out var list))
                    linesOf[section] = list = [];
                list.Add(line);
            }
        }
    }

    private void CollectTriggers()
    {
        foreach (var section in Document.OfKind(MissionSectionKind.Trigger))
        {
            // The game skips a trigger without a nickname. The target checks use the same rule (decision D556).
            if (TriggerRules.Nickname(section.Section) is not { } nickname)
                continue;
            var nicknameLine = TriggerRules.NicknameLine(section.Section);
            var trigger = new MissionTrigger(section, nickname, nicknameLine)
            {
                IsDuplicate = triggerByName.ContainsKey(nickname),
                LinesBeforeNickname = Enumerable.Range(0, nicknameLine).ToList(),
            };
            var sectionLines = LinesOf(section);
            var after = sectionLines.Where(l => l.Entry > nicknameLine).ToList();
            foreach (var line in sectionLines)
                line.Trigger = trigger;
            trigger.Conditions = after.Where(l => TriggerRules.IsCondition(l.Key)).ToList();
            trigger.Actions = after.Where(l => TriggerRules.IsAction(l.Key)).ToList();
            trigger.Rows = after.Where(l => TriggerRules.IsCondition(l.Key) || TriggerRules.IsAction(l.Key)).ToList();
            var initState = after.LastOrDefault(l => Is(l.Key, "InitState"))?.Value(0);
            trigger.StartsActive = initState != null && Is(initState, "ACTIVE");
            // The last line of a key counts, as for InitState (TR10).
            trigger.Repeats = after.LastOrDefault(l => Is(l.Key, "repeatable"))?.Value(0) is { } value &&
                              value.ToLowerInvariant() is "true" or "1";
            trigger.SystemHint = after.FirstOrDefault(l => Is(l.Key, "system") && l.Value(0) != null)?.Value(0);
            triggers.Add(trigger);
            triggerByName.TryAdd(trigger.Nickname, trigger);
            // A condition that has no effect in the engine never becomes true. So the engine never runs the actions of
            // its trigger (section 14.8 of the M6a design).
            if (trigger.Condition?.Mark is EngineMark.NoEffect or EngineMark.CannotLoad)
            {
                foreach (var action in trigger.Actions.Where(a => a.Mark == EngineMark.Runs))
                    action.Mark = EngineMark.NeverRuns;
            }
        }
        StartTriggers = triggers.Where(t => t.StartsActive && !t.IsDuplicate).ToList();
    }

    /// <summary>Resolves the names of each line with a spec.</summary>
    private void ResolveNames()
    {
        foreach (var line in lines)
        {
            // The game ignores the lines of a trigger before its nickname (TR2), the lines of a duplicate trigger (TR4)
            // and a trigger section without a nickname, so their names name nothing.
            if (line.Section.Kind == MissionSectionKind.Trigger &&
                (line.Trigger == null || line.Entry < line.Trigger.NicknameLine || line.Trigger.IsDuplicate))
                continue;
            // The game does not use the value of such a key, for example the system of a trigger (TR3).
            if (line.KeySpec?.Unused == true)
                continue;
            foreach (var spec in line.Specs)
            {
                if (spec.Scope == MissionKindScope.Value && spec.Kind != MissionValueKind.Token)
                    continue;
                var end = Math.Min(spec.End, line.Count);
                for (var i = spec.Index; i < end; i++)
                {
                    if (line.Value(i) is not { } name)
                        continue;
                    var reference = Resolve(line, i, spec, name);
                    line.Add(reference);
                    if (reference.State == MissionReferenceState.Found && spec.Scope != MissionKindScope.Value)
                        Remember(reference);
                }
            }
        }
    }

    private MissionReference Resolve(MissionLine line, int index, MissionValueSpec spec, string name)
    {
        var reference = new MissionReference(line, index, spec, name);
        if (spec.NoneToken != null && name.Equals(spec.NoneToken, StringComparison.OrdinalIgnoreCase))
            return reference with { State = MissionReferenceState.Token, Expected = spec.NoneToken };
        if (spec.Kind == MissionValueKind.Token && spec.Tokens != null)
            return Token(reference, Schema.Tokens[spec.Tokens], name) ?? reference with
            {
                State = Schema.Tokens[spec.Tokens].Open ? MissionReferenceState.Token : MissionReferenceState.Missing,
            };
        if (spec.Also != null && Token(reference, Schema.Tokens[spec.Also], name) is { } word)
            return word;
        var section = FindScriptItem(spec.Kind, name);
        switch (spec.Scope)
        {
            case MissionKindScope.Script:
                if (spec.Kind == MissionValueKind.Object && section == null)
                {
                    if (name.Equals("Player", StringComparison.OrdinalIgnoreCase) || labels.ContainsKey(name))
                        return reference with { State = MissionReferenceState.Found };
                    if (World.Has(MissionValueKind.SystemObject, name))
                        return reference with { State = MissionReferenceState.Found };
                    return reference with
                    {
                        State = World.Knows(MissionValueKind.SystemObject) ? MissionReferenceState.Missing
                            : MissionReferenceState.Unknown,
                    };
                }
                if (spec.Kind == MissionValueKind.Trigger)
                    return reference with { State = Trigger(name) != null ? MissionReferenceState.Found : MissionReferenceState.Missing };
                if (section == null)
                    return reference with { State = MissionReferenceState.Missing };
                if (spec.ExactCase && !string.Equals(section.Nickname, name, StringComparison.Ordinal))
                    return reference with { State = MissionReferenceState.WrongCase, Section = section, Expected = section.Nickname };
                return reference with { State = MissionReferenceState.Found, Section = section };
            case MissionKindScope.World:
                if (!World.Knows(spec.Kind))
                    return reference with { State = MissionReferenceState.Unknown };
                return reference with
                {
                    State = World.Has(spec.Kind, name) ? MissionReferenceState.Found : MissionReferenceState.Missing,
                };
            case MissionKindScope.File:
                if (!World.Knows(spec.Kind))
                    return reference with { State = MissionReferenceState.Unknown };
                return reference with
                {
                    State = MissionFiles.RelativeOf(name) is { } relative && World.FileExists(relative)
                        ? MissionReferenceState.Found
                        : MissionReferenceState.Missing,
                };
            default:
                return reference with { State = MissionReferenceState.Found };
        }
    }

    /// <summary>The token of a set that a value names: the word with the right letter case, or a word that differs in
    /// letter case from a word of a set that compares with letter case. Null when the value is no word of the
    /// set.</summary>
    private static MissionReference? Token(MissionReference reference, MissionTokenSet set, string name)
    {
        if (set.Find(name) is not { } word)
            return null;
        if (set.CaseSensitive && !string.Equals(word, name, StringComparison.Ordinal))
            return reference with { State = MissionReferenceState.WrongCase, Expected = word };
        return reference with { State = MissionReferenceState.Token, Expected = word };
    }

    /// <summary>The section of the script that a name of a kind names, or null.</summary>
    private MissionSection? FindScriptItem(MissionValueKind kind, string name) => kind switch
    {
        MissionValueKind.Object => Item(MissionSectionKind.Ship, name) ?? Item(MissionSectionKind.Formation, name) ??
                                   Item(MissionSectionKind.Solar, name) ?? Item(MissionSectionKind.Loot, name),
        MissionValueKind.Group => Item(MissionSectionKind.Ship, name) ?? Item(MissionSectionKind.Formation, name),
        MissionValueKind.Ship => Item(MissionSectionKind.Ship, name),
        MissionValueKind.Formation => Item(MissionSectionKind.Formation, name),
        MissionValueKind.Solar => Item(MissionSectionKind.Solar, name),
        MissionValueKind.Loot => Item(MissionSectionKind.Loot, name),
        MissionValueKind.ObjList => Item(MissionSectionKind.OrderList, name),
        MissionValueKind.Dialog => Item(MissionSectionKind.Conversation, name),
        MissionValueKind.Objective => Item(MissionSectionKind.Objective, name),
        MissionValueKind.Npc => Item(MissionSectionKind.Npc, name),
        MissionValueKind.RandomEncounter => Item(MissionSectionKind.RandomEncounter, name),
        MissionValueKind.MetaBehavior => Item(MissionSectionKind.MetaBehavior, name),
        _ => null,
    };

    private void Remember(MissionReference reference)
    {
        var key = ReverseKey(reference.Kind, reference.Name);
        if (!reverse.TryGetValue(key, out var list))
            reverse[key] = list = [];
        list.Add(reference);
    }

    /// <summary>The key of the reverse index: one key for each name of the label rule, else the kind and the
    /// name.</summary>
    private static string ReverseKey(MissionValueKind kind, string name)
    {
        var group = kind switch
        {
            MissionValueKind.Object or MissionValueKind.Group or MissionValueKind.Ship or MissionValueKind.Formation or
                MissionValueKind.Solar or MissionValueKind.Loot or MissionValueKind.SystemObject => "object",
            _ => kind.ToString(),
        };
        return group + "|" + name.Trim();
    }

    private void CollectLinks()
    {
        foreach (var trigger in triggers.Where(t => !t.IsDuplicate))
        {
            foreach (var line in trigger.Actions)
            {
                var kind = line.Key.ToLowerInvariant() switch
                {
                    "act_acttrig" => MissionLinkKind.Start,
                    "act_deacttrig" => MissionLinkKind.Stop,
                    "act_save" => MissionLinkKind.Load,
                    _ => (MissionLinkKind?)null,
                };
                if (kind is { } k && line.Value(0) is { } name)
                    AddLink(new MissionLink(k, trigger, line, name, Trigger(name)));
            }
            if (trigger.Condition is { } condition && Is(condition.Key, "Cnd_WatchTrigger") && condition.Value(0) is { } watched)
                AddLink(new MissionLink(MissionLinkKind.Watch, trigger, condition, watched, Trigger(watched),
                    condition.Value(1)?.ToUpperInvariant()));
        }

        var restarts = new List<MissionLink>();
        var cancels = new List<MissionLink>();
        var selfStarts = new List<MissionTrigger>();
        foreach (var group in links.Where(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Stop).GroupBy(l => l.Source))
        {
            var ordered = group.OrderBy(l => l.Line.Entry).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var link = ordered[i];
                var before = ordered.Take(i).Where(l => Is(l.TargetName, link.TargetName)).ToList();
                if (link.Kind == MissionLinkKind.Start && before.Any(l => l.Kind == MissionLinkKind.Stop))
                    restarts.Add(link);
                else if (link.Kind == MissionLinkKind.Stop && before.Any(l => l.Kind == MissionLinkKind.Start) &&
                         !ordered.Skip(i + 1).Any(l => l.Kind == MissionLinkKind.Start && Is(l.TargetName, link.TargetName)))
                    cancels.Add(link);
            }
            if (ordered.Any(l => l.Kind == MissionLinkKind.Start && Is(l.TargetName, group.Key.Nickname)))
                selfStarts.Add(group.Key);
        }
        Restarts = restarts;
        Cancels = cancels;
        SelfStarts = selfStarts;

        // The triggers that a start trigger reaches over Start and Load links (section 6.4 of the M6a design).
        var reached = new HashSet<MissionTrigger>(StartTriggers);
        var queue = new Queue<MissionTrigger>(StartTriggers);
        var outgoing = links.Where(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Load && l.Target != null)
            .GroupBy(l => l.Source).ToDictionary(g => g.Key, g => g.Select(l => l.Target!).ToList());
        while (queue.Count > 0)
        {
            foreach (var next in outgoing.GetValueOrDefault(queue.Dequeue()) ?? [])
            {
                if (reached.Add(next))
                    queue.Enqueue(next);
            }
        }
        Reached = reached;
    }

    private void AddLink(MissionLink link)
    {
        links.Add(link);
        if (link.Target == null)
            return;
        if (!incoming.TryGetValue(link.Target, out var list))
            incoming[link.Target] = list = [];
        list.Add(link);
    }

    /// <summary>True when the trigger is not a start trigger and no Start or Load link ends at it. The game never runs
    /// such a trigger (rule G05, the graph mark "Never starts").</summary>
    public bool NothingStarts(MissionTrigger trigger) =>
        !trigger.StartsActive && !LinksTo(trigger).Any(l => l.Kind is MissionLinkKind.Start or MissionLinkKind.Load);

    /// <summary>The links that start a trigger, from any trigger, itself included.</summary>
    public IEnumerable<MissionLink> StartsOf(MissionTrigger trigger) =>
        LinksTo(trigger).Where(l => l.Kind == MissionLinkKind.Start);

    private void CollectSystems()
    {
        foreach (var kind in new[] { MissionSectionKind.Ship, MissionSectionKind.Formation, MissionSectionKind.Solar,
                     MissionSectionKind.Loot, MissionSectionKind.Conversation, MissionSectionKind.OrderList })
        {
            foreach (var section in Document.OfKind(kind))
            {
                if (section.Section.FirstOrDefault(e => Is(e.Name, "system") && e.Count > 0) is { } line)
                    itemSystems[section] = line[0].ToString()!.Trim();
            }
        }
        // The system hint of the first trigger that spawns or moves an item.
        foreach (var trigger in triggers.Where(t => t.SystemHint != null && !t.IsDuplicate))
        {
            foreach (var line in trigger.Actions)
            {
                if (!line.Key.StartsWith("Act_Spawn", StringComparison.OrdinalIgnoreCase) &&
                    !Is(line.Key, "Act_RelocateShip"))
                    continue;
                foreach (var reference in line.References.Where(r => r.ValueIndex == 0 && r.Section != null))
                    itemSystems.TryAdd(reference.Section!, trigger.SystemHint!);
            }
        }
        // The system of the first conversation where a ship speaks.
        foreach (var dialog in Document.OfKind(MissionSectionKind.Conversation))
        {
            if (!itemSystems.TryGetValue(dialog, out var system))
                continue;
            foreach (var line in dialog.Section.Where(e => Is(e.Name, "line") && e.Count > 0))
            {
                if (FindScriptItem(MissionValueKind.Object, line[0].ToString()!) is { } speaker)
                    itemSystems.TryAdd(speaker, system);
            }
        }
    }

    /// <summary>True for the ship keys of <c>[MsnShip]</c> and <c>[MsnFormation]</c> (rule M09).</summary>
    internal static bool IsShipKey(string key) => ShipKeyNames.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The text of a value as the retail game reads it, without blanks at its ends.</summary>
    public static string Text(ValueBase value) => RetailText.Of(value).Trim();

    /// <summary>The text ids of the <c>type</c> line of an objective. The engine reads the name id from value 1 of an
    /// <c>ids</c> objective and from value 2 of the other types. A <c>navmarker</c> or a <c>rep_inst</c> objective also
    /// has an explanation id in value 3.</summary>
    public static IReadOnlyList<string> ObjectiveTextIds(Entry type)
    {
        if (type.Count < 2)
            return [];
        var kind = Text(type[0]);
        int[] at = Is(kind, "ids") ? [1]
            : Is(kind, "navmarker") || Is(kind, "rep_inst") ? [2, 3]
            : Is(kind, "base") ? [2]
            : [];
        return at.Where(i => i < type.Count).Select(i => Text(type[i])).Where(t => t.Length > 0).ToList();
    }

    /// <summary>True when two names are equal without letter case and without blanks at their ends.</summary>
    public static bool Is(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

    internal static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
