using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>Where a new line goes, so which keys it can have.</summary>
internal enum LinePlace
{
    /// <summary>The condition of a trigger.</summary>
    Condition,

    /// <summary>An action of a trigger.</summary>
    Action,

    /// <summary>An order of an order list.</summary>
    Order,

    /// <summary>Any line of a section: a condition, an action, an order or a key of the section type.</summary>
    Section,
}

/// <summary>A line that a command writes: the key with the letter case of the schema, and the value texts.</summary>
/// <param name="Notes">The notes of the line, for example a word whose letter case changed.</param>
internal sealed record BuiltLine(string Key, IReadOnlyList<string> Texts, IReadOnlyList<string> Notes);

/// <summary>
/// The values and the lines of the mission commands (section 4.3 of the M6b design): the key of a line, the named values
/// to their places, the gaps, the checks of the kinds and the words, and the text of each value.
/// </summary>
internal static class MissionValues
{
    /// <summary>The start of the placeholder of a text of the active mod.</summary>
    public const string TextPrefix = "$str:";

    /// <summary>
    /// The key and the value texts of a line. The schema checks the key for its place and each named value by its kind
    /// (section 4.6 of the M6b design). Value texts by position get only the checks of a patch line.
    /// </summary>
    /// <param name="sectionType">The type of the section of the line, for example <c>Trigger</c>.</param>
    /// <param name="old">The line that the new line replaces, or null. A file value keeps the form of its
    /// separators.</param>
    /// <exception cref="EditException">The key or a value is not valid.</exception>
    public static BuiltLine Build(MissionSchema schema, string sectionType, LinePlace place, LineSpec spec,
        Entry? old = null)
    {
        var (key, specs) = Key(schema, sectionType, place, spec.Key);
        if (spec.Texts != null)
        {
            foreach (var text in spec.Texts)
                CheckText(text);
            return new BuiltLine(key, spec.Texts.ToList(), []);
        }
        var notes = new List<string>();
        var texts = Texts(schema, key, FormSpecs(schema, sectionType, key, specs, spec.Values, old), spec.Values, notes, old);
        return new BuiltLine(key, texts, notes);
    }

    /// <summary>The specs of the form of a line of a key with forms: the form of the first named value, else the form
    /// of the old line, else the default form (see <see cref="MissionKeySpec.Forms"/>).</summary>
    private static IReadOnlyList<MissionValueSpec> FormSpecs(MissionSchema schema, string sectionType, string key,
        IReadOnlyList<MissionValueSpec> specs, IReadOnlyDictionary<string, MissionValue> values, Entry? old)
    {
        var section = MissionIndex.Is(sectionType, "NPCShipArch") ? schema.ShipBuild : schema.Section(sectionType);
        if (section?.Key(key) is not { Forms: not null } keySpec || specs.FirstOrDefault(s => s.Index == 0) is not { } first)
            return specs;
        var form = values.FirstOrDefault(v => v.Key.Equals(first.Name, StringComparison.OrdinalIgnoreCase)).Value is
            { Type: MissionValueType.Text } given
            ? given.Text
            : old is { Count: > 0 } ? MissionIndex.Text(old[0]) : null;
        return keySpec.ValueSpecsOf(form);
    }

    /// <summary>The specs of the values of a line, with the form of its first value for a key with forms.</summary>
    public static IReadOnlyList<MissionValueSpec> SpecsOf(MissionSchema schema, string sectionType, Entry entry)
    {
        var specs = Key(schema, sectionType, LinePlace.Section, entry.Name).Specs;
        var section = MissionIndex.Is(sectionType, "NPCShipArch") ? schema.ShipBuild : schema.Section(sectionType);
        return section?.Key(entry.Name) is { Forms: not null } keySpec && entry.Count > 0
            ? keySpec.ValueSpecsOf(MissionIndex.Text(entry[0]))
            : specs;
    }

    /// <summary>The key with the letter case of the schema, and the specs of its values.</summary>
    /// <exception cref="EditException">The key is not valid at the place.</exception>
    public static (string Key, IReadOnlyList<MissionValueSpec> Specs) Key(MissionSchema schema, string sectionType,
        LinePlace place, string key)
    {
        key = key.Trim();
        if (PatchFormatter.NameError(key) != null || key.Length == 0)
            throw Refuse(MissionErrors.UnknownKeyError(key));
        var kind = MissionSection.KindOf(sectionType);
        switch (place)
        {
            case LinePlace.Condition:
            case LinePlace.Action:
            {
                var name = schema.Name(key) ?? throw Refuse(MissionErrors.UnknownKeyError(key));
                if (place == LinePlace.Condition && name.Kind != MissionNameKind.Condition)
                    throw Refuse(MissionErrors.WrongPlaceError(name.Name, isCondition: false));
                if (place == LinePlace.Action && name.Kind != MissionNameKind.Action)
                    throw Refuse(MissionErrors.WrongPlaceError(name.Name, isCondition: true));
                return (name.Name, name.Values);
            }
            case LinePlace.Order:
                return schema.Directive(key) is { } directive
                    ? (directive.Name, directive.Values)
                    : throw Refuse(MissionErrors.NotADirectiveError(key));
            default:
                if (kind == MissionSectionKind.Trigger && (TriggerRules.IsCondition(key) || TriggerRules.IsAction(key)))
                    return schema.Name(key) is { } name ? (name.Name, name.Values) : throw Refuse(MissionErrors.UnknownKeyError(key));
                if (kind == MissionSectionKind.OrderList && schema.Directive(key) is { } order)
                    return (order.Name, order.Values);
                var section = MissionIndex.Is(sectionType, "NPCShipArch") ? schema.ShipBuild : schema.Section(sectionType);
                if (section?.Key(key) is { } keySpec)
                    return (keySpec.Key, keySpec.ValueSpecs);
                // A section type that the schema does not know, and a section that reads keys of another grammar, take
                // any key. The checks then give no problem for it.
                if (section == null || section.OpenKeys)
                    return (key, []);
                throw Refuse(MissionErrors.NotAKeyError(key, $"[{section.Type}]"));
        }
    }

    /// <summary>The texts of the named values in the places of the specs, with the gaps filled.</summary>
    private static List<string> Texts(MissionSchema schema, string key, IReadOnlyList<MissionValueSpec> specs,
        IReadOnlyDictionary<string, MissionValue> values, List<string> notes, Entry? old)
    {
        var ordered = specs.OrderBy(s => s.Index).ToList();
        var given = new Dictionary<MissionValueSpec, MissionValue>();
        foreach (var (name, value) in values)
        {
            var spec = ordered.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                       throw Refuse(MissionErrors.NoSuchValueError(key, name, ordered.Select(s => s.Name)));
            given[spec] = value;
        }
        var last = given.Count == 0 ? -1 : given.Keys.Max(s => s.Index);
        var texts = new List<string>();
        foreach (var spec in ordered)
        {
            if (texts.Count < spec.Index)
                break;
            if (given.TryGetValue(spec, out var value))
            {
                texts.AddRange(Convert(schema, spec, value, notes, old));
                continue;
            }
            var needed = spec.Index < last || !spec.Optional;
            if (!needed)
                break;
            if (spec.Default != null)
                texts.Add(spec.Default);
            else if (spec.NoneToken != null)
                texts.Add(spec.NoneToken);
            else
                throw Refuse(spec.Index < last
                    ? MissionErrors.ValueMissingError(spec.Label)
                    : MissionErrors.ValueMissingError(spec.Label) with
                    {
                        Message = $"{spec.Label} needs a value. Give {spec.Label} a value.",
                    });
        }
        return texts;
    }

    /// <summary>The texts of one named value: one text, the numbers of a position or a rotation, or the items of a
    /// rest value.</summary>
    /// <exception cref="EditException">The value has the wrong kind.</exception>
    public static IReadOnlyList<string> Convert(MissionSchema schema, MissionValueSpec spec, MissionValue value,
        List<string> notes, Entry? old = null)
    {
        if (spec.Width > 1)
        {
            var numbers = value.Type == MissionValueType.List ? value.Items : [];
            var what = spec.Kind == MissionValueKind.Rotation ? $"{spec.Width} numbers" : $"{Words(spec.Width)} numbers";
            if (numbers.Count != spec.Width)
                throw Refuse(MissionErrors.ValueKindError(value.ToString(), spec.Kind == MissionValueKind.Position
                    ? "a position"
                    : "a rotation", spec.Label, what));
            return numbers.Select(n => Number(spec, n, whole: spec.Whole)).ToList();
        }
        if (spec.Rest && value.Type == MissionValueType.List)
            return value.Items.Select(item => Scalar(schema, spec, item, notes, old)).ToList();
        return [Scalar(schema, spec, value, notes, old)];
    }

    /// <summary>The text of a single value by the kind of its spec.</summary>
    private static string Scalar(MissionSchema schema, MissionValueSpec spec, MissionValue value, List<string> notes,
        Entry? old)
    {
        if (value.Type == MissionValueType.List)
            throw Refuse(MissionErrors.ValueKindError(value.ToString(), "one value", spec.Label, "one value"));
        var text = value.Scalar!.Trim();
        if (spec.NoneToken != null && text.Equals(spec.NoneToken, StringComparison.OrdinalIgnoreCase))
            return spec.NoneToken;
        string result;
        switch (spec.Kind)
        {
            // An attitude is a number from -1 to 1, or a REP_ word of the game (the word set 'attitude').
            case MissionValueKind.Attitude when value.Type == MissionValueType.Text &&
                                                schema.Tokens.GetValueOrDefault("attitude")?.Find(text) is { } word:
                if (!string.Equals(word, text, StringComparison.Ordinal))
                    notes.Add($"'{text}' became '{word}', the letter case that the game reads.");
                result = word;
                break;
            case MissionValueKind.Number or MissionValueKind.Attitude:
                result = Number(spec, value, whole: spec.Whole);
                break;
            case MissionValueKind.WholeNumber:
                result = Number(spec, value, whole: true);
                break;
            case MissionValueKind.TextId:
                if (value.Type == MissionValueType.Text && StringPlaceholder.TryParse(text, out var placeholder))
                {
                    if (placeholder.Kind != StringKind.String || !ModStrings.IsValidKey(placeholder.Key))
                        throw Refuse(MissionErrors.ValueKindError(text, "a text id", spec.Label,
                            $"a number, or {TextPrefix} and a key of letters, digits, '_', '-' or '.'"));
                    result = text;
                }
                else
                    result = Number(spec, value, whole: true, "a text id", $"a number, or {TextPrefix} and a key");
                break;
            case MissionValueKind.Bool:
                if (value.Type == MissionValueType.Bool)
                    result = value.Bool ? "true" : "false";
                else if (TriggerRules.ReadsAsBool(text))
                    result = text.ToLowerInvariant();
                else
                    throw Refuse(MissionErrors.ValueKindError(text, "true or false", spec.Label, "true or false"));
                break;
            case MissionValueKind.Token:
                result = Word(schema, spec, text, notes);
                break;
            default:
                if (value.Type == MissionValueType.Bool)
                    throw Refuse(MissionErrors.ValueKindError(text, "a name", spec.Label, "a name"));
                result = spec.Also != null && schema.Tokens.GetValueOrDefault(spec.Also)?.Find(text) is { } also
                    ? also
                    : text;
                if (spec.Scope == MissionKindScope.File)
                    result = FilePath(result, old);
                break;
        }
        CheckText(result);
        return result;
    }

    /// <summary>A word of a closed set with the letter case of the set (TR16, decision D682). A word in another letter
    /// case gets a note. A word of an open set can be any word.</summary>
    private static string Word(MissionSchema schema, MissionValueSpec spec, string text, List<string> notes)
    {
        var set = spec.Tokens != null ? schema.Tokens.GetValueOrDefault(spec.Tokens) : null;
        var word = set?.Find(text) ?? (spec.Also != null ? schema.Tokens.GetValueOrDefault(spec.Also)?.Find(text) : null);
        if (word != null)
        {
            if (!string.Equals(word, text, StringComparison.Ordinal))
                notes.Add($"'{text}' became '{word}', the letter case that the game reads.");
            return word;
        }
        if (set == null || set.Open)
            return text;
        throw Refuse(MissionErrors.WordNotInSetError(text, spec.Label, set.Values));
    }

    /// <summary>The text of a number value. A text that holds a number is valid too.</summary>
    private static string Number(MissionValueSpec spec, MissionValue value, bool whole, string kind = "a number",
        string? what = null)
    {
        double number;
        if (value.Type == MissionValueType.Number)
            number = value.Number;
        else if (value.Type != MissionValueType.Text ||
                 !double.TryParse(value.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            throw Refuse(MissionErrors.ValueKindError(value.ToString(), kind, spec.Label,
                what ?? (whole ? "a whole number" : "a number")));
        if (!double.IsFinite(number))
            throw Refuse(MissionErrors.ValueKindError(value.ToString(), kind, spec.Label, what ?? "a number"));
        if (whole && number != Math.Truncate(number))
            throw Refuse(MissionErrors.WholeNumberError(spec.Label));
        return MissionValue.Format(number);
    }

    /// <summary>A path below the data folder, with the separators of the old line. A new line uses doubled
    /// backslashes, as most retail lines do (section 4.3 of the M6b design).</summary>
    private static string FilePath(string text, Entry? old)
    {
        var single = old != null && old.Select(v => v.ToString()).Any(v => v.Contains('\\') && !v.Contains(@"\\"));
        var parts = text.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(single ? @"\" : @"\\", parts);
    }

    /// <summary>Refuses a value text that a patch line cannot hold.</summary>
    /// <exception cref="EditException">The text holds a comma, a semicolon or a control character.</exception>
    public static void CheckText(string text)
    {
        if (text.IndexOfAny([',', ';']) >= 0)
            throw Refuse(MissionErrors.PatchTextError());
        if (text.Any(char.IsControl) || !System.Text.Ascii.IsValid(text))
            throw Refuse(MissionErrors.PatchTextError() with
            {
                Message = $"'{text}' holds a character that a line of the game cannot hold. Use ASCII text without a " +
                          "line break.",
            });
    }

    /// <summary>
    /// The values of a line as the merge reads them (section 4.3 of the M6b design): the engine text parser reads the
    /// texts, and in a binary file each value takes the type of the line of B that the line replaces.
    /// </summary>
    /// <param name="replaced">The line of B at the same place, or null for a new line.</param>
    /// <exception cref="EditException">A patch line cannot hold the values.</exception>
    public static Entry Entry(Section section, string key, IReadOnlyList<string> texts, Entry? replaced, bool binary)
    {
        if (!PatchFormatter.TryReadLine(key, string.Join(", ", texts), replaced, binary, out var values, out var error))
            throw Refuse(new EditError(EditErrorKind.InvalidValue, MissionErrors.PatchText, error));
        if (values.Count != texts.Count && texts.Count > 0)
            throw Refuse(new EditError(EditErrorKind.InvalidValue, MissionErrors.PatchText,
                $"'{key} = {string.Join(", ", texts)}' does not read back as {texts.Count} values."));
        var entry = new Entry(section, key);
        foreach (var value in values)
            entry.Add(value);
        return entry;
    }

    /// <summary>The value texts of a line. The engine text parser reads each text back as the same value, as a patch
    /// line of a text file writes it: a float has a point or an exponent, and a boolean is <c>true</c> or
    /// <c>false</c>.</summary>
    public static IReadOnlyList<string> TextsOf(Entry entry) => entry.Select(TextOf).ToList();

    /// <summary>The text of one value, as a patch line of a text file writes it.</summary>
    public static string TextOf(ValueBase value)
    {
        switch (value)
        {
            case BooleanValue truth:
                return (bool)truth ? "true" : "false";
            case SingleValue single when BinaryValueTypes.IsLargeInteger(single, out var large):
                return large.ToString(CultureInfo.InvariantCulture);
            case SingleValue single:
                var number = (float)single;
                var text = number.ToString("R", CultureInfo.InvariantCulture);
                return float.IsFinite(number) && text.IndexOfAny(['.', 'E', 'e']) < 0 ? text + ".0" : text;
            default:
                return IniDocument.TextOf(value);
        }
    }

    private static string Words(int count) => count switch
    {
        2 => "two",
        3 => "three",
        4 => "four",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    private static EditException Refuse(EditError error) => new(error);
}

/// <summary>
/// The name rules of the mission commands for a caller that names a new item before its batch runs, for example a
/// gesture that adds a trigger and then links to it in one batch (section 8.2 of the M6b design). The layer checks each
/// name again when the batch runs.
/// </summary>
public static class MissionNaming
{
    /// <summary>True when a new name of a section type has the form rule of section 4.3 of the M6b design.</summary>
    public static bool IsValidNew(string name, string sectionType) =>
        MissionNames.FormOk(name, MissionNames.MaxFor(sectionType));

    /// <summary>
    /// A name of a section type that is not in <paramref name="taken"/>, without letter case. Without a wanted name, it
    /// is the made name <c>{script stem}_{kind}_{n}</c> of decision D683. With a wanted name, it is that name when it is
    /// valid and free, else the name with <c>_2</c>, <c>_3</c> and so on, cut to the length limit.
    /// </summary>
    public static string Free(string script, string sectionType, IReadOnlySet<string> taken, string? wanted = null)
    {
        var max = MissionNames.MaxFor(sectionType);
        bool Ok(string name) => MissionNames.FormOk(name, max) && !taken.Contains(name);
        if (wanted == null)
        {
            // A template in prefab mode has roles, without the name of a file (section 5.7 of the M6b design).
            var stem = MissionRefs.IsPrefab(script)
                ? "role"
                : System.IO.Path.GetFileNameWithoutExtension(script.Replace('\\', '/')).ToLowerInvariant();
            var word = MissionNames.MadeWord(sectionType);
            for (var n = 1;; n++)
            {
                var made = $"{stem}_{word}_{n.ToString(CultureInfo.InvariantCulture)}";
                if (made.Length > max)
                    made = $"{word}_{n.ToString(CultureInfo.InvariantCulture)}";
                if (Ok(made))
                    return made;
            }
        }
        var trimmed = wanted.Trim();
        if (Ok(trimmed))
            return trimmed;
        var stemOfWanted = new string(trimmed.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        if (stemOfWanted.Length == 0)
            stemOfWanted = MissionNames.MadeWord(sectionType);
        for (var n = 2;; n++)
        {
            var suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
            var candidate = stemOfWanted[..Math.Min(stemOfWanted.Length, max - suffix.Length)] + suffix;
            if (Ok(candidate))
                return candidate;
        }
    }
}

/// <summary>The name rules of the mission commands (section 4.3 of the M6b design, decision D683).</summary>
internal static class MissionNames
{
    /// <summary>The most characters of a name: the buffer of object and label names, and the limit of the editor for
    /// the other kinds.</summary>
    public const int MaxName = 47;

    /// <summary>The most characters of a random encounter name (TR15).</summary>
    public const int MaxEncounterName = 31;

    /// <summary>The most characters of a new name of a section type.</summary>
    public static int MaxFor(string sectionType) =>
        MissionIndex.Is(sectionType, "MsnRandEnc") ? MaxEncounterName : MaxName;

    /// <summary>True when a new name has the form rule: letters, digits, <c>_</c> and <c>-</c>, at most
    /// <paramref name="max"/> characters, and not a number. The merge reads a value that is a number as a number.</summary>
    public static bool FormOk(string name, int max) =>
        name.Length > 0 && name.Length <= max && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') &&
        IniDocument.ParseTextLine("v = " + name) is { Count: 1 } line && line[0] is StringValue;

    /// <summary>The word of a section type in made names, for example <c>trigger</c> in <c>m02_trigger_3</c>.</summary>
    public static string MadeWord(string sectionType) => MissionSection.KindOf(sectionType) switch
    {
        MissionSectionKind.Trigger => "trigger",
        MissionSectionKind.Ship => "ship",
        MissionSectionKind.Formation => "formation",
        MissionSectionKind.Solar => "solar",
        MissionSectionKind.Loot => "loot",
        MissionSectionKind.OrderList => "orders",
        MissionSectionKind.Objective => "objective",
        MissionSectionKind.Npc => "npc",
        MissionSectionKind.Conversation => "conversation",
        MissionSectionKind.RandomEncounter => "encounter",
        MissionSectionKind.MetaBehavior => "behavior",
        _ when MissionIndex.Is(sectionType, "NPCShipArch") => "build",
        _ => sectionType.ToLowerInvariant(),
    };

    /// <summary>The word of a section type in the messages, for example "order list".</summary>
    public static string KindWord(string sectionType) => MissionSection.KindOf(sectionType) switch
    {
        MissionSectionKind.Trigger => "trigger",
        MissionSectionKind.Ship => "ship",
        MissionSectionKind.Formation => "formation",
        MissionSectionKind.Solar => "solar",
        MissionSectionKind.Loot => "loot",
        MissionSectionKind.OrderList => "order list",
        MissionSectionKind.Objective => "objective",
        MissionSectionKind.Npc => "NPC",
        MissionSectionKind.Conversation => "conversation",
        MissionSectionKind.RandomEncounter => "random encounter",
        MissionSectionKind.MetaBehavior => "meta behavior",
        MissionSectionKind.Mission => "mission section",
        _ when MissionIndex.Is(sectionType, "NPCShipArch") => "ship build",
        _ => $"[{sectionType}] section",
    };
}
