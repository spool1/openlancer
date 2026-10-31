using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenLancer.Data.Editing.Fields;

namespace OpenLancer.Data.Missions;

/// <summary>The kind of one value of a mission line (section 14.4 of the M6a design). A kind tells where a name comes
/// from, or what a number means.</summary>
public enum MissionValueKind
{
    // Names of the script itself.
    Trigger,

    /// <summary>A name of the label rule: Player, a ship, a formation, a solar, a loot, a label of the script, or an
    /// object of a system.</summary>
    Object,

    /// <summary>A <c>[MsnShip]</c> or a <c>[MsnFormation]</c> that a spawn line names.</summary>
    Group,
    Ship,
    Formation,
    Solar,
    Loot,
    ObjList,
    Dialog,
    Objective,
    Npc,
    RandomEncounter,
    MetaBehavior,

    // Names outside the script.
    System,
    Base,
    Room,
    SystemObject,
    Faction,
    ShipArch,
    Loadout,
    Pilot,
    Voice,
    Sound,
    Good,
    Equipment,
    Head,
    Body,
    Accessory,
    NpcShipArch,
    FormationShape,
    CommLine,
    Fuse,

    // Files.
    SpaceCutscene,
    BaseScene,
    AmbientScript,
    File,

    // Values.
    Position,
    Rotation,
    Number,
    WholeNumber,
    Attitude,
    TextId,
    Bool,
    Token,
    Text,
}

/// <summary>Where a <see cref="MissionValueKind"/> finds its names.</summary>
public enum MissionKindScope
{
    /// <summary>A value that names nothing: a number, a text or a token.</summary>
    Value,

    /// <summary>A name of the script itself, for example a trigger or an order list.</summary>
    Script,

    /// <summary>A name of the game data outside the script, for example a system or a faction.</summary>
    World,

    /// <summary>A file of the game data.</summary>
    File,
}

/// <summary>One value of a mission line, or a run of values (a position, or the rest of the line).</summary>
/// <param name="Index">The index of the first value in the line.</param>
/// <param name="Name">The name of the value in the sentences, for example <c>target</c>.</param>
/// <param name="Label">The plain field name, for example "Target".</param>
public sealed record MissionValueSpec(int Index, string Name, string Label, MissionValueKind Kind)
{
    /// <summary>The number of values: 3 for a position, 4 for a rotation, else 1.</summary>
    public int Width { get; init; } = 1;

    /// <summary>True when the line can end before the value.</summary>
    public bool Optional { get; init; }

    /// <summary>True when the game finds the value only through its list, so a name outside the list does
    /// nothing.</summary>
    public bool Closed { get; init; }

    /// <summary>The id of the token set of a <see cref="MissionValueKind.Token"/> value, or null.</summary>
    public string? Tokens { get; init; }

    /// <summary>The id of a token set with more words that the value accepts, for example <c>ANY</c> for a system, or
    /// null.</summary>
    public string? Also { get; init; }

    /// <summary>A word that means "no name", for example <c>no_ol</c>, or null.</summary>
    public string? NoneToken { get; init; }

    /// <summary>True when the value repeats up to the end of the line, for example a list of labels.</summary>
    public bool Rest { get; init; }

    /// <summary>True when the game reads the numbers of the value as whole numbers.</summary>
    public bool Whole { get; init; }

    /// <summary>True when the game compares the name with letter case (TR16), for example the ship of a spawn.</summary>
    public bool ExactCase { get; init; }

    /// <summary>The id of the buffer of the value (TR15), or null.</summary>
    public string? Buffer { get; init; }

    /// <summary>The help text of the field, or null.</summary>
    public string? Help { get; init; }

    /// <summary>False when no reader checked the meaning of the value. The UI then shows its values only.</summary>
    public bool Checked { get; init; } = true;

    /// <summary>True for a field that is hard to understand. Its label gets a help glyph (section 11.3 of the M6a
    /// design).</summary>
    public bool Hard { get; init; }

    /// <summary>The list field of a value that repeats up to the end of the line, or null (decision D540).</summary>
    public MissionListInfo? List { get; init; }

    /// <summary>The text that a new line of a command writes when it does not give the value, but a later value, or
    /// null (section 4.3 of the M6b design). The "none" word (<see cref="NoneToken"/>) also fills such a gap.</summary>
    public string? Default { get; init; }

    /// <summary>The index after the last value of the spec.</summary>
    public int End => Rest ? int.MaxValue : Index + Width;

    /// <summary>True when the spec covers the value index.</summary>
    public bool Covers(int index) => index >= Index && index < End;

    /// <summary>Where the kind finds its names.</summary>
    public MissionKindScope Scope => MissionSchema.ScopeOf(Kind);
}

/// <summary>A sentence of a condition, with the values that select it (section 13.2 of the M6a design).</summary>
/// <param name="When">For each value name, the texts that match: a value text, <c>absent</c> or <c>present</c>. An empty
/// map matches each line.</param>
public sealed record MissionSentence(IReadOnlyDictionary<string, IReadOnlyList<string>> When, string Text);

/// <summary>
/// What a play from a trigger does with an action of the path to the trigger (section 5.3, step 9 of the M6c design,
/// decision D748). The schema gives each action one role.
/// </summary>
public enum StartRole
{
    /// <summary>The action changes the world of space, for example a spawn or an order. It goes into the setup trigger
    /// that runs when the player comes into space.</summary>
    World,

    /// <summary>The action changes a state of the player or of the game, for example a reputation or an objective. It
    /// goes into the setup trigger that runs at the start.</summary>
    State,

    /// <summary>The start save holds the net result of the action, for example the title of the mission.</summary>
    Save,

    /// <summary>The action moves the player. The start place uses it.</summary>
    Place,

    /// <summary>The start does nothing with the action, for example a comm line or a start of a trigger.</summary>
    Skip,
}

/// <summary>What a mission name is: a condition, an action of a trigger, or an order of an order list.</summary>
public enum MissionNameKind
{
    Condition,
    Action,
    Directive,
}

/// <summary>One condition, action or order of the schema (section 14.4 of the M6a design).</summary>
/// <param name="Name">The key, for example <c>Cnd_Destroyed</c>.</param>
/// <param name="Plain">The plain name, for example "On destroyed".</param>
/// <param name="Group">The action group of an action, the event group of a condition, or <c>orders</c> for an order.</param>
public sealed record MissionName(string Name, string Plain, MissionNameKind Kind, string Group,
    IReadOnlyList<MissionValueSpec> Values)
{
    public IReadOnlyList<MissionSentence> Sentences { get; init; } = [];

    public IReadOnlyList<MissionSentence> Suffixes { get; init; } = [];

    /// <summary>False when no reader checked the meaning of the line (section 13.2). The graph then shows the plain name
    /// and the values, and no sentence.</summary>
    public bool Checked { get; init; } = true;

    /// <summary>True for a condition that the game tests at each heartbeat (TR11).</summary>
    public bool Polls { get; init; }

    /// <summary>The events that wake a condition.</summary>
    public IReadOnlyList<string> Wake { get; init; } = [];

    /// <summary>The record of the fact and the fork class.</summary>
    public string? Evidence { get; init; }

    /// <summary>True when no retail mission script has a line of this name (decision D425). Rule M28 warns about such
    /// a line, because its effect is not checked.</summary>
    public bool NoRetailExample { get; init; }

    /// <summary>The role of an action in a play from a trigger (decision D748), or null for a condition and an
    /// order.</summary>
    public StartRole? Start { get; init; }

    /// <summary>True for a condition that writes its state into a save, after the trigger id (section 2.4, item 6 of the
    /// M6c design). A start save holds no such state, so a trigger with this condition starts fresh.</summary>
    public bool SavesState { get; init; }

    /// <summary>The spec of a value name, or null.</summary>
    public MissionValueSpec? Value(string name) =>
        Values.FirstOrDefault(v => v.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>The spec that covers a value index, or null for an extra value.</summary>
    public MissionValueSpec? ValueAt(int index) => Values.FirstOrDefault(v => v.Covers(index));

    /// <summary>The number of values of a line without its optional values.</summary>
    public int RequiredCount => Values.Where(v => !v.Optional && !v.Rest).Select(v => v.End).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The sentence of a line (section 13.2): the first sentence whose values match, with the first suffix that matches,
    /// before the last period. A name with <see cref="Checked"/> false, or without a sentence, gives
    /// "{plain name}: {values}".
    /// </summary>
    /// <param name="text">Gives the text of a value name in the line, or null when the line does not have it.</param>
    public string Sentence(Func<string, string?> text)
    {
        var sentence = Checked ? Sentences.FirstOrDefault(s => Matches(s, text)) : null;
        if (sentence == null)
        {
            var values = Values.Select(v => text(v.Name)).Where(v => v != null).ToList();
            return values.Count == 0 ? Plain : $"{Plain}: {string.Join(", ", values)}";
        }
        var result = Fill(sentence.Text, text);
        var suffix = Suffixes.FirstOrDefault(s => Matches(s, text));
        if (suffix != null)
            result = result.TrimEnd('.') + Fill(suffix.Text, text) + ".";
        return result;
    }

    private static bool Matches(MissionSentence sentence, Func<string, string?> text) =>
        sentence.When.All(pair =>
        {
            var actual = text(pair.Key);
            return pair.Value.Any(expected => expected switch
            {
                "absent" => actual == null,
                "present" => actual != null,
                _ => actual != null && actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            });
        });

    /// <summary>Puts the value texts into the braces of a text. <c>{name%}</c> shows a fraction as a percent.</summary>
    private static string Fill(string template, Func<string, string?> text)
    {
        var result = new StringBuilder();
        var at = 0;
        while (at < template.Length)
        {
            var open = template.IndexOf('{', at);
            if (open < 0)
                break;
            var close = template.IndexOf('}', open);
            if (close < 0)
                break;
            result.Append(template, at, open - at);
            var name = template[(open + 1)..close];
            var percent = name.EndsWith('%');
            var value = text(percent ? name[..^1] : name) ?? "?";
            if (percent && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
                value = (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture);
            result.Append(value);
            at = close + 1;
        }
        result.Append(template, at, template.Length - at);
        return result.ToString();
    }
}

/// <summary>An action group of the chips and the Trigger page (section 13.3 of the M6a design).</summary>
/// <param name="Chip">The short name of the chip, for example "Spawn".</param>
/// <param name="Section">The long name of the section, for example "Spawn and move".</param>
/// <param name="Order">The place of the group in the order of the table.</param>
public sealed record MissionActionGroup(string Id, string Chip, string Section, HelpText Help, int Order);

/// <summary>An event group of the conditions (section 13.2 of the M6a design).</summary>
public sealed record MissionEventGroup(string Id, string Label, int Order);

/// <summary>When the next story mission starts after a success (section 2.2 of the mission studio design).</summary>
public enum NextMissionKind
{
    AtOnce,
    Worth,
    End,
}

/// <param name="Credits">The growth of the worth of a <see cref="NextMissionKind.Worth"/> rule, else 0.</param>
public sealed record NextMission(NextMissionKind Kind, int Credits);

/// <summary>One story slot of the game code (section 14.6 of the M6a design). A mod cannot change it.</summary>
/// <param name="Path">The script path relative to the data folder.</param>
/// <param name="StartStage">The story stage when the mission starts.</param>
/// <param name="AcceptStage">The story stage after the accept: the success code of the slot.</param>
/// <param name="Notes">The ids of the special rules of the slot in <see cref="MissionSchema.SlotNotes"/>.</param>
public sealed record StorySlot(string Slot, string Controller, string Path, int StartStage, int AcceptStage,
    NextMission Next, int TitleId, int MissionId, IReadOnlyList<string> Notes);

/// <summary>One key of a mission section type.</summary>
/// <param name="Values">The values of a key with more than one value, or empty for a key with one value of
/// <see cref="Kind"/>.</param>
public sealed record MissionKeySpec(string Key, string Label, MissionValueKind Kind, IReadOnlyList<MissionValueSpec> Values)
{
    public bool Closed { get; init; }

    /// <summary>True for a ship key of <c>[MsnShip]</c> and <c>[MsnFormation]</c>, which must not follow a
    /// <c>formation</c> or <c>ship</c> line (section 2.6 item 3 of the mission studio design).</summary>
    public bool ShipKey { get; init; }

    /// <summary>True for a key that the game reads but does not use (decision D426).</summary>
    public bool Unused { get; init; }

    public string? Tokens { get; init; }

    public string? Buffer { get; init; }

    public string? Help { get; init; }

    /// <summary>A word that means "no name", for example <c>no_ol</c>, or null.</summary>
    public string? NoneToken { get; init; }

    /// <summary>True for a field that is hard to understand. Its label gets a help glyph (section 11.3 of the M6a
    /// design).</summary>
    public bool Hard { get; init; }

    /// <summary>The list field of the lines of the key, or null for a key with one line (decision D540).</summary>
    public MissionListInfo? List { get; init; }

    /// <summary>The value specs of each form of a key whose first value picks the form, by the form word, or null. An
    /// example is the <c>type</c> of an objective: <c>ids</c>, <c>navmarker</c>, <c>rep_inst</c> or <c>base</c> (final
    /// finding 15 of MCP1-3). <see cref="Values"/> are the values of the default form.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MissionValueSpec>>? Forms { get; init; }

    /// <summary>The specs of the values of a line whose first value is a form word: the specs of the form, or
    /// <see cref="ValueSpecs"/> for a key without forms or a word that is not a form.</summary>
    public IReadOnlyList<MissionValueSpec> ValueSpecsOf(string? form) =>
        Forms != null && form != null && Forms.TryGetValue(form.Trim(), out var specs) ? specs : ValueSpecs;

    /// <summary>The specs of the values of all forms, each name once, in the order of the forms. The input schema of a
    /// line gives these names.</summary>
    public IReadOnlyList<MissionValueSpec> AllValueSpecs => Forms == null
        ? ValueSpecs
        : [.. ValueSpecs.Concat(Forms.Values.SelectMany(f => f)).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The specs of the values of a line of the key.</summary>
    public IReadOnlyList<MissionValueSpec> ValueSpecs => Values.Count > 0
        ? Values
        : [new MissionValueSpec(0, Key, Label, Kind)
        {
            // A position and a rotation of a key have the width of their kind, as a value of a name has.
            Width = Kind switch
            {
                MissionValueKind.Position => 3,
                MissionValueKind.Rotation => 4,
                _ => 1,
            },
            Closed = Closed, Tokens = Tokens, Buffer = Buffer ?? DefaultBuffer(Kind), Help = Help, NoneToken = NoneToken,
            Hard = Hard,
            // The rows of a value-list key are the values of one line, for example the classes of an npc_class line.
            Rest = List?.Values == true,
        }];

    private static string? DefaultBuffer(MissionValueKind kind) => MissionSchema.DefaultBuffer(kind);
}

/// <summary>The keys of one mission section type.</summary>
/// <param name="OpenKeys">True when the section reads keys of another grammar, so an unknown key is not a problem. The
/// encounter of a <c>[MsnRandEnc]</c> reads its own keys.</param>
/// <param name="Directives">True for <c>[ObjList]</c>: the orders are keys too.</param>
public sealed record MissionSectionSpec(string Type, IReadOnlyList<MissionKeySpec> Keys, bool OpenKeys, bool Directives)
{
    /// <summary>True when the game reads the lines of the section in order, so a patch writes all its lines with
    /// <c>ol.replace</c> (section 6.2 of the M6b design, decision D665).</summary>
    public bool OrderSensitive { get; init; }

    /// <summary>The list field of the orders of an <c>[ObjList]</c>, or null.</summary>
    public MissionListInfo? Orders { get; init; }

    public MissionKeySpec? Key(string key) =>
        Keys.FirstOrDefault(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A closed set of words (section 14.4 of the M6a design).</summary>
/// <param name="CaseSensitive">True when the game compares the words with letter case (TR16).</param>
/// <param name="Open">True when the game reads another word as a default, so another word is not a problem.</param>
public sealed record MissionTokenSet(string Id, IReadOnlyList<string> Values, bool CaseSensitive, bool Open)
{
    /// <summary>The word of the set that the text names, without letter case, or null.</summary>
    public string? Find(string text) =>
        Values.FirstOrDefault(v => v.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A fixed buffer of the game for a name (TR15).</summary>
/// <param name="What">The thing that the message names, for example "The object name".</param>
/// <param name="Max">The most characters that the game reads.</param>
public sealed record MissionBuffer(string Id, string What, int Max);

/// <summary>The rule data of a list field (section 11.1 of the M6a design, decision D540).</summary>
/// <param name="Label">The label of the field, or null for the label of its key or value.</param>
/// <param name="Items">The plural word of the rows, for example "ships".</param>
/// <param name="AddLabel">The text of the Add button, or null for "Add".</param>
/// <param name="Values">True for a key whose rows are the values of one line, for example the classes of an
/// <c>npc_class</c> line. Else each line of the key is one row.</param>
public sealed record MissionListInfo(string? Label, string Items, string? AddLabel, bool Values = false);

/// <summary>One step of the completeness panel of a mission item (section 13.5 of the M6a design).</summary>
/// <param name="Id">The id of the step in the rule data.</param>
/// <param name="Name">The name of the step, for example "Ship build".</param>
/// <param name="Rules">The check rules whose problems make the step missing.</param>
/// <param name="Key">The key of the lines of the problems, or null for each line.</param>
/// <param name="Check">The id of a fixed check of the data, or null.</param>
/// <param name="Reason">Why the step is necessary, in one sentence.</param>
public sealed record MissionStepSpec(string Id, string Name, IReadOnlyList<string> Rules, string? Key, string? Check,
    string Reason);

/// <summary>The completeness panel of a kind of mission item: its title and its steps, in order.</summary>
public sealed record MissionItemSteps(string Title, IReadOnlyList<MissionStepSpec> Steps)
{
    /// <summary>The step of an id.</summary>
    public MissionStepSpec Step(string id) => Steps.First(s => s.Id == id);
}

/// <summary>
/// The argument schema of mission scripts (section 14.4 of the M6a design): the conditions, actions and orders with
/// their values, the keys of the section types, the token sets, the buffers, the story slot table and the help texts.
/// The editor ships it as an embedded resource. It holds no retail name.
/// </summary>
public sealed class MissionSchema
{
    private const string ResourceName = "OpenLancer.Data.Missions.mission-schema.json";

    private static readonly Lazy<MissionSchema> RetailSchema = new(() =>
    {
        using var stream = typeof(MissionSchema).Assembly.GetManifestResourceStream(ResourceName) ??
                           throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    private readonly Dictionary<string, MissionName> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MissionName> directives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MissionSectionSpec> sections = new(StringComparer.OrdinalIgnoreCase);

    private MissionSchema()
    {
    }

    /// <summary>The schema that ships with the editor.</summary>
    public static MissionSchema Retail => RetailSchema.Value;

    public IReadOnlyList<StorySlot> StorySlots { get; private set; } = [];

    /// <summary>The special rules of the slots, by note id, in plain words.</summary>
    public IReadOnlyDictionary<string, string> SlotNotes { get; private set; } = new Dictionary<string, string>();

    public IReadOnlyList<MissionEventGroup> Events { get; private set; } = [];

    public IReadOnlyList<MissionActionGroup> Groups { get; private set; } = [];

    /// <summary>The help texts of the views and pages (section 13.4 of the M6a design), by id.</summary>
    public IReadOnlyDictionary<string, HelpText> Help { get; private set; } = new Dictionary<string, HelpText>();

    /// <summary>The notes of the pages in plain words, by id, for example the note of a key that the game does not
    /// use.</summary>
    public IReadOnlyDictionary<string, string> Notes { get; private set; } = new Dictionary<string, string>();

    /// <summary>The steps of the completeness panels of the mission items (section 13.5 of the M6a design). The items
    /// are <c>mission</c>, <c>missionShip</c>, <c>conversation</c>, <c>cutsceneLink</c> and <c>baseSceneLink</c>. The
    /// steps of the behavior prefabs are in the prefab data.</summary>
    public IReadOnlyDictionary<string, MissionItemSteps> Completeness { get; private set; } =
        new Dictionary<string, MissionItemSteps>();

    /// <summary>The keys of a ship build (<c>[NPCShipArch]</c>) of the mission ship files. The game reads them outside the
    /// mission script, so the checks of the script do not read them. The Mission ship page shows them.</summary>
    public MissionSectionSpec ShipBuild { get; private set; } = new("NPCShipArch", [], false, false);

    public IReadOnlyList<MissionName> Conditions { get; private set; } = [];

    public IReadOnlyList<MissionName> Actions { get; private set; } = [];

    public IReadOnlyList<MissionName> Directives { get; private set; } = [];

    public IReadOnlyList<MissionSectionSpec> Sections { get; private set; } = [];

    public IReadOnlyDictionary<string, MissionTokenSet> Tokens { get; private set; } = new Dictionary<string, MissionTokenSet>();

    public IReadOnlyDictionary<string, MissionBuffer> Buffers { get; private set; } = new Dictionary<string, MissionBuffer>();

    /// <summary>The condition or action of a key, without letter case, or null.</summary>
    public MissionName? Name(string key) => names.GetValueOrDefault(key.Trim());

    /// <summary>The order of an <c>[ObjList]</c> key, without letter case, or null.</summary>
    public MissionName? Directive(string key) => directives.GetValueOrDefault(key.Trim());

    /// <summary>The spec of a section type, without letter case, or null for a type that the game does not read in a
    /// mission script.</summary>
    public MissionSectionSpec? Section(string type) => sections.GetValueOrDefault(type.Trim());

    /// <summary>The action group of an id, or null.</summary>
    public MissionActionGroup? Group(string id) => Groups.FirstOrDefault(g => g.Id == id);

    /// <summary>The event group of an id, or null.</summary>
    public MissionEventGroup? Event(string id) => Events.FirstOrDefault(e => e.Id == id);

    /// <summary>The story slot of a script path relative to the data folder, without letter case, or null.</summary>
    public StorySlot? SlotOf(string relativePath) =>
        StorySlots.FirstOrDefault(s => s.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The list fields of the rule data (section 11.1 of the M6a design, decision D540). A key with more than one line
    /// gives a repeated-key field, for example the <c>ship</c> lines of a formation. A value that repeats up to the end
    /// of a line gives a value-list field, for example the ships of <c>Cnd_NPCSystemEnter</c>. The orders of an order
    /// list give a mixed-keys field. The columns come from the value specs.
    /// </summary>
    public IReadOnlyList<ListFieldSpec> Lists => lists ??= BuildLists();

    private IReadOnlyList<ListFieldSpec>? lists;

    /// <summary>The list field of a key of a section type, or null when the key has one line.</summary>
    public ListFieldSpec? KeyList(string sectionType, string key) => ListById($"{sectionType}.{key}");

    /// <summary>The list field of a value of a condition, an action or an order, or null.</summary>
    public ListFieldSpec? ValueList(string name, string value) => ListById($"{name}.{value}");

    /// <summary>The list field of the orders of a section type, or null.</summary>
    public ListFieldSpec? OrderList(string sectionType) => ListById($"{sectionType}.orders");

    private ListFieldSpec? ListById(string id) =>
        Lists.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private List<ListFieldSpec> BuildLists()
    {
        var result = new List<ListFieldSpec>();
        foreach (var section in Sections.Append(ShipBuild))
        {
            foreach (var key in section.Keys.Where(k => k.List != null))
            {
                // A key whose rows are the values of one line has one column of the kind of the key.
                result.Add(key.List!.Values
                    ? new ListFieldSpec($"{section.Type}.{key.Key}", key.List.Label ?? key.Label, key.Help,
                        ListFieldCodec.ValueList, [key.Key], [Column(key.ValueSpecs[0] with { Index = 0, Rest = false })])
                    {
                        Items = key.List.Items,
                        AddLabel = key.List.AddLabel,
                    }
                    : new ListFieldSpec($"{section.Type}.{key.Key}", key.List.Label ?? key.Label, key.Help,
                        ListFieldCodec.RepeatedKey, [key.Key], key.ValueSpecs.Select(v => Column(v)).ToList())
                    {
                        Items = key.List.Items,
                        AddLabel = key.List.AddLabel,
                    });
            }
            if (section.Orders is { } orders)
            {
                result.Add(LinesList($"{section.Type}.orders", orders.Label ?? "Orders",
                    Directives.Select(d => d.Name).ToList(), orders.Items) with { AddLabel = orders.AddLabel });
            }
        }
        foreach (var name in Conditions.Concat(Actions).Concat(Directives))
        {
            foreach (var value in name.Values.Where(v => v.List != null))
            {
                // Each row is one value of the rest of the line, after the values before it.
                result.Add(new ListFieldSpec($"{name.Name}.{value.Name}", value.List!.Label ?? value.Label, value.Help,
                    ListFieldCodec.ValueList, [name.Name], [Column(value with { Index = 0, Rest = false })], value.Index)
                {
                    Items = value.List.Items,
                    AddLabel = value.List.AddLabel,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// A mixed-keys list field of the lines of a set of keys, with one row for each line in file order. Examples are the
    /// orders of an order list and the spawn lines of a spawn wave. Each key has the columns of its values, and its
    /// plain name names its rows.
    /// </summary>
    /// <param name="keys">The condition, action or order names.</param>
    /// <param name="items">The plural word of the rows, for example "orders".</param>
    public ListFieldSpec LinesList(string id, string label, IReadOnlyList<string> keys, string items)
    {
        var columns = new List<ListColumn>();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if ((Name(key) ?? Directive(key)) is not { } name)
                continue;
            labels[name.Name] = name.Plain;
            columns.AddRange(name.Values.Select(v => Column(v, name.Name)));
        }
        return new ListFieldSpec(id, label, null, ListFieldCodec.MixedKeys, keys, columns)
        {
            Items = items,
            KeyLabels = labels,
        };
    }

    /// <summary>The column of a value spec. A name gives a list reference. Another value gives a number, a whole number,
    /// a word of a set, a position, a rotation or a text.</summary>
    /// <param name="key">The key of the column set of a mixed-keys field, or null.</param>
    public ListColumn Column(MissionValueSpec spec, string? key = null)
    {
        var list = ListOf(spec.Kind);
        var kind = spec.Kind switch
        {
            MissionValueKind.Position => ListColumnKind.Position,
            MissionValueKind.Rotation => ListColumnKind.Rotation,
            MissionValueKind.WholeNumber or MissionValueKind.TextId => ListColumnKind.WholeNumber,
            MissionValueKind.Number or MissionValueKind.Attitude =>
                spec.Whole ? ListColumnKind.WholeNumber : ListColumnKind.Number,
            MissionValueKind.Token or MissionValueKind.Bool => ListColumnKind.Token,
            _ when list != null => ListColumnKind.Reference,
            _ => ListColumnKind.Text,
        };
        var set = spec.Tokens is { } id ? Tokens.GetValueOrDefault(id) : null;
        IReadOnlyList<string>? words = spec.Kind == MissionValueKind.Bool ? ["true", "false"] : set?.Values;
        return new ListColumn(spec.Label, spec.Index, kind, kind == ListColumnKind.Reference ? list : null, words,
            set?.CaseSensitive == true, spec.Help, Key: key, Rest: spec.Rest);
    }

    /// <summary>
    /// The ids of the data lists of a kind, or null for a kind without a list. A kind with more than one list joins the
    /// ids with "+" (section 14.1 of the M6a design).
    /// </summary>
    public static string? ListOf(MissionValueKind kind) => kind switch
    {
        MissionValueKind.Trigger => "mission_trigger",
        MissionValueKind.Object => "mission_ship+mission_formation+mission_solar+mission_loot+mission_label",
        MissionValueKind.Group => "mission_ship+mission_formation",
        MissionValueKind.Ship => "mission_ship",
        MissionValueKind.Formation => "mission_formation",
        MissionValueKind.Solar => "mission_solar",
        MissionValueKind.Loot => "mission_loot",
        MissionValueKind.ObjList => "mission_objlist",
        MissionValueKind.Dialog => "mission_dialog",
        MissionValueKind.Objective => "mission_objective",
        MissionValueKind.Npc => "mission_npc",
        MissionValueKind.RandomEncounter => "mission_randenc",
        MissionValueKind.System => "system",
        MissionValueKind.Base => "base",
        MissionValueKind.Faction => "faction",
        MissionValueKind.ShipArch => "ship",
        MissionValueKind.Loadout => "loadout",
        MissionValueKind.Pilot => "pilot",
        MissionValueKind.Voice => "voice",
        MissionValueKind.Good => "good",
        MissionValueKind.Equipment => "equipment",
        MissionValueKind.Head => "head",
        MissionValueKind.Body => "body",
        MissionValueKind.Accessory => "accessory",
        MissionValueKind.NpcShipArch => "npc_ship_arch",
        MissionValueKind.FormationShape => "formation_shape",
        MissionValueKind.CommLine => "comm_line",
        MissionValueKind.Fuse => "fuse",
        MissionValueKind.SpaceCutscene => "cutscene",
        MissionValueKind.BaseScene => "base_scene",
        _ => null,
    };

    /// <summary>Where a kind finds its names.</summary>
    public static MissionKindScope ScopeOf(MissionValueKind kind) => kind switch
    {
        <= MissionValueKind.MetaBehavior => MissionKindScope.Script,
        <= MissionValueKind.Fuse => MissionKindScope.World,
        <= MissionValueKind.File => MissionKindScope.File,
        _ => MissionKindScope.Value,
    };

    /// <summary>Reads schema data.</summary>
    /// <exception cref="InvalidDataException">The data is not valid.</exception>
    public static MissionSchema Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            var root = document.RootElement;
            var schema = new MissionSchema
            {
                StorySlots = root.GetProperty("storySlots").EnumerateArray().Select(ReadSlot).ToList(),
                SlotNotes = root.GetProperty("slotNotes").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!),
                Events = root.GetProperty("events").EnumerateArray()
                    .Select((e, i) => new MissionEventGroup(Text(e, "id"), Text(e, "label"), i)).ToList(),
                Groups = root.GetProperty("groups").EnumerateArray()
                    .Select((e, i) => new MissionActionGroup(Text(e, "id"), Text(e, "chip"), Text(e, "section"),
                        HelpText.Read(e.GetProperty("help")), i)).ToList(),
                Help = root.GetProperty("help").EnumerateObject().ToDictionary(p => p.Name, p => HelpText.Read(p.Value)),
                Notes = root.GetProperty("notes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
                Completeness = root.GetProperty("completeness").EnumerateObject().ToDictionary(p => p.Name,
                    p => new MissionItemSteps(Text(p.Value, "title"),
                        p.Value.GetProperty("steps").EnumerateArray().Select(ReadStep).ToList())),
                Tokens = root.GetProperty("tokens").EnumerateObject().ToDictionary(p => p.Name,
                    p => new MissionTokenSet(p.Name, Strings(p.Value, "values"), Bool(p.Value, "caseSensitive"),
                        Bool(p.Value, "open"))),
                Buffers = root.GetProperty("buffers").EnumerateArray()
                    .Select(e => new MissionBuffer(Text(e, "id"), Text(e, "what"), e.GetProperty("max").GetInt32()))
                    .ToDictionary(b => b.Id),
            };
            schema.Conditions = root.GetProperty("conditions").EnumerateArray()
                .Select(e => ReadName(e, MissionNameKind.Condition, "event")).ToList();
            schema.Actions = root.GetProperty("actions").EnumerateArray()
                .Select(e => ReadName(e, MissionNameKind.Action, "group")).ToList();
            schema.Directives = root.GetProperty("objListDirectives").EnumerateArray()
                .Select(e => ReadName(e, MissionNameKind.Directive, null)).ToList();
            schema.Sections = root.GetProperty("sections").EnumerateArray().Select(ReadSection).ToList();
            schema.ShipBuild = ReadSection(root.GetProperty("shipBuild"));
            foreach (var name in schema.Conditions.Concat(schema.Actions))
            {
                if (!schema.names.TryAdd(name.Name, name))
                    throw new FormatException($"The name {name.Name} repeats.");
            }
            foreach (var directive in schema.Directives)
            {
                if (!schema.directives.TryAdd(directive.Name, directive))
                    throw new FormatException($"The order {directive.Name} repeats.");
            }
            foreach (var section in schema.Sections)
                schema.sections[section.Type] = section;
            schema.Check();
            return schema;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or
                                       FormatException or ArgumentException)
        {
            throw new InvalidDataException("The mission schema is not valid: " + e.Message, e);
        }
    }

    /// <summary>Checks the links inside the data: the groups, events, token sets and buffers that the names use, and
    /// the values that each sentence names.</summary>
    private void Check()
    {
        foreach (var name in Conditions.Where(n => Event(n.Group) == null))
            throw new FormatException($"The condition {name.Name} has the unknown event group '{name.Group}'.");
        foreach (var name in Actions.Where(n => Group(n.Group) == null))
            throw new FormatException($"The action {name.Name} has the unknown group '{name.Group}'.");
        foreach (var name in Actions.Where(n => n.Start == null))
            throw new FormatException($"The action {name.Name} has no start role.");
        foreach (var name in Conditions.Concat(Directives).Where(n => n.Start != null))
            throw new FormatException($"Only an action has a start role, not {name.Name}.");
        foreach (var name in Actions.Concat(Directives).Where(n => n.SavesState))
            throw new FormatException($"Only a condition saves a state, not {name.Name}.");
        var specs = Conditions.Concat(Actions).Concat(Directives).SelectMany(n => n.Values)
            .Concat(Sections.Append(ShipBuild).SelectMany(s => s.Keys).SelectMany(k => k.ValueSpecs));
        foreach (var spec in specs)
        {
            if (spec.Kind == MissionValueKind.Token && (spec.Tokens == null || !Tokens.ContainsKey(spec.Tokens)))
                throw new FormatException($"The token value {spec.Name} names the unknown token set '{spec.Tokens}'.");
            if (spec.Also != null && !Tokens.ContainsKey(spec.Also))
                throw new FormatException($"The value {spec.Name} names the unknown token set '{spec.Also}'.");
            if (spec.Buffer != null && !Buffers.ContainsKey(spec.Buffer))
                throw new FormatException($"The value {spec.Name} names the unknown buffer '{spec.Buffer}'.");
        }
        var rules = MissionChecks.Rules.ToHashSet();
        foreach (var step in Completeness.Values.SelectMany(i => i.Steps))
        {
            foreach (var rule in step.Rules.Where(r => !rules.Contains(r)))
                throw new FormatException($"The step {step.Id} names the unknown rule '{rule}'.");
            if (step.Check != null && !MissionCompleteness.ItemChecks.Contains(step.Check))
                throw new FormatException($"The step {step.Id} names the unknown check '{step.Check}'.");
        }
        foreach (var name in Conditions.Concat(Actions).Concat(Directives))
        {
            foreach (var sentence in name.Sentences.Concat(name.Suffixes))
            {
                foreach (var value in sentence.When.Keys.Concat(Placeholders(sentence.Text)))
                {
                    if (name.Value(value) == null)
                        throw new FormatException($"A sentence of {name.Name} names the unknown value '{value}'.");
                }
            }
        }
    }

    /// <summary>The value names in the braces of a sentence, without a <c>%</c> at the end.</summary>
    internal static IEnumerable<string> Placeholders(string text)
    {
        var at = 0;
        while ((at = text.IndexOf('{', at)) >= 0)
        {
            var close = text.IndexOf('}', at);
            if (close < 0)
                yield break;
            yield return text[(at + 1)..close].TrimEnd('%');
            at = close + 1;
        }
    }

    private static StorySlot ReadSlot(JsonElement e)
    {
        var next = e.GetProperty("next");
        var kind = Text(next, "kind") switch
        {
            "atOnce" => NextMissionKind.AtOnce,
            "worth" => NextMissionKind.Worth,
            "end" => NextMissionKind.End,
            var other => throw new FormatException($"The next rule '{other}' is not known."),
        };
        return new StorySlot(Text(e, "slot"), Text(e, "controller"), Text(e, "path"),
            e.GetProperty("startStage").GetInt32(), e.GetProperty("acceptStage").GetInt32(),
            new NextMission(kind, next.TryGetProperty("credits", out var credits) ? credits.GetInt32() : 0),
            e.GetProperty("titleId").GetInt32(), e.GetProperty("missionId").GetInt32(), Strings(e, "notes"));
    }

    private static MissionName ReadName(JsonElement e, MissionNameKind kind, string? groupKey)
    {
        var name = e.TryGetProperty("name", out var n) ? n.GetString()! : Text(e, "key");
        return new MissionName(name, Text(e, "plain"), kind, groupKey == null ? "orders" : Text(e, groupKey),
            e.GetProperty("values").EnumerateArray().Select(ReadValue).ToList())
        {
            Sentences = Sentences(e, "sentences"),
            Suffixes = Sentences(e, "suffixes"),
            Checked = !e.TryGetProperty("checked", out var c) || c.GetBoolean(),
            Polls = Bool(e, "polls"),
            Wake = Strings(e, "wake"),
            Evidence = Optional(e, "evidence"),
            NoRetailExample = Bool(e, "noRetailExample"),
            Start = Optional(e, "start") is { } role ? Role(role) : null,
            SavesState = Bool(e, "savesState"),
        };
    }

    /// <summary>The start role of a JSON word.</summary>
    private static StartRole Role(string text) => text switch
    {
        "world" => StartRole.World,
        "state" => StartRole.State,
        "save" => StartRole.Save,
        "place" => StartRole.Place,
        "skip" => StartRole.Skip,
        _ => throw new FormatException($"The start role '{text}' is not known."),
    };

    private static IReadOnlyList<MissionSentence> Sentences(JsonElement e, string property) =>
        e.TryGetProperty(property, out var array)
            ? array.EnumerateArray().Select(s => new MissionSentence(
                s.TryGetProperty("when", out var when)
                    ? when.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlyList<string>)(p.Value.ValueKind ==
                        JsonValueKind.Array
                            ? p.Value.EnumerateArray().Select(JsonText).ToList()
                            : [JsonText(p.Value)]))
                    : new Dictionary<string, IReadOnlyList<string>>(),
                Text(s, "text"))).ToList()
            : [];

    private static string JsonText(JsonElement e) => e.ValueKind == JsonValueKind.Number
        ? e.GetRawText()
        : e.GetString()!;

    private static MissionValueSpec ReadValue(JsonElement e)
    {
        var kind = Kind(Text(e, "kind"));
        return new MissionValueSpec(e.GetProperty("index").GetInt32(), Text(e, "name"), Text(e, "label"), kind)
        {
            Width = e.TryGetProperty("width", out var width) ? width.GetInt32()
                : kind == MissionValueKind.Position ? 3
                : kind == MissionValueKind.Rotation ? 4
                : 1,
            Optional = Bool(e, "optional"),
            Closed = Bool(e, "closed"),
            Tokens = Optional(e, "tokens"),
            Also = Optional(e, "also"),
            NoneToken = Optional(e, "noneToken"),
            Rest = Bool(e, "rest"),
            Whole = Bool(e, "whole"),
            ExactCase = Bool(e, "exactCase"),
            Buffer = Optional(e, "buffer") ?? DefaultBuffer(kind),
            Help = Optional(e, "help"),
            Checked = !e.TryGetProperty("checked", out var c) || c.GetBoolean(),
            Hard = Bool(e, "hard"),
            List = ReadList(e, "list"),
            Default = e.TryGetProperty("default", out var fallback) ? JsonText(fallback) : null,
        };
    }

    /// <summary>The list data of a property, or null when the item has none.</summary>
    private static MissionListInfo? ReadList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list)
            ? new MissionListInfo(Optional(list, "label"), Text(list, "items"), Optional(list, "addLabel"),
                Bool(list, "values"))
            : null;

    private static MissionStepSpec ReadStep(JsonElement e) => new(Text(e, "id"), Text(e, "name"), Strings(e, "rules"),
        Optional(e, "key"), Optional(e, "check"), Text(e, "reason"));

    /// <summary>The buffer of a kind without its own buffer: an object name has 47 characters (TR15).</summary>
    internal static string? DefaultBuffer(MissionValueKind kind) => kind is MissionValueKind.Object or
        MissionValueKind.Group or MissionValueKind.Ship or MissionValueKind.Formation or MissionValueKind.Solar or
        MissionValueKind.Loot
        ? "object"
        : null;

    private static MissionSectionSpec ReadSection(JsonElement e) => new(Text(e, "type"),
        e.GetProperty("keys").EnumerateArray().Select(k => new MissionKeySpec(Text(k, "key"), Text(k, "label"),
            Kind(Text(k, "kind")),
            k.TryGetProperty("values", out var values) ? values.EnumerateArray().Select(ReadValue).ToList() : [])
        {
            Closed = Bool(k, "closed"),
            ShipKey = Bool(k, "shipKey"),
            Unused = Bool(k, "unused"),
            Tokens = Optional(k, "tokens"),
            Buffer = Optional(k, "buffer"),
            Help = Optional(k, "help"),
            NoneToken = Optional(k, "noneToken"),
            Hard = Bool(k, "hard"),
            List = ReadList(k, "list"),
            Forms = k.TryGetProperty("forms", out var forms)
                ? forms.EnumerateObject().ToDictionary(f => f.Name,
                    f => (IReadOnlyList<MissionValueSpec>)f.Value.EnumerateArray().Select(ReadValue).ToList(),
                    StringComparer.OrdinalIgnoreCase)
                : null,
        }).ToList(),
        Bool(e, "openKeys"), Bool(e, "directives"))
    {
        Orders = ReadList(e, "orders"),
        OrderSensitive = Bool(e, "orderSensitive"),
    };

    private static MissionValueKind Kind(string text) =>
        Enum.TryParse<MissionValueKind>(text, ignoreCase: true, out var kind)
            ? kind
            : throw new FormatException($"The value kind '{text}' is not known.");

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new FormatException($"An item has no '{name}'.");

    private static string? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.GetBoolean();

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : [];
}
