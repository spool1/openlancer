using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>One entry of a data list: a name and the section that gives it.</summary>
/// <param name="Value">The name, as the section spells it.</param>
/// <param name="Section">The section that gives the name: the last section with the name, as in the engine. Null for
/// an entry of an enum list, and for an entry of a whole file of the active mod that W does not show.</param>
/// <param name="Origin">The layer that gives the entry.</param>
/// <param name="ModId">The mod that gives the entry, or null for the base data.</param>
/// <param name="Detail">A short text of the <c>detail</c> keys of the list, for the footer of the picker.</param>
public sealed record ListEntry(string Value, SectionRef? Section, ValueOrigin Origin, string? ModId, string Detail)
{
    /// <summary>False when the engine cannot use the entry: a key of <c>requires</c> is missing, or a name of a unique
    /// list repeats.</summary>
    public bool Usable { get; init; } = true;

    /// <summary>Why the engine cannot use the entry, or null.</summary>
    public string? Problem { get; init; }

    /// <summary>True for an entry of the active mod that the mod files do not have yet: a section of an edit that is
    /// not saved.</summary>
    public bool Unsaved { get; init; }

    /// <summary>True when the entry is not <see cref="Usable"/> and the active mod made it so. The active mod adds or
    /// changes the section of the entry, and the lower section has each required line. For a repeated name of a unique
    /// list, the active mod adds or changes one of the sections with the name. A section of a whole file of the
    /// active mod counts too.</summary>
    public bool UnusableByActiveMod { get; init; }

    /// <summary>The display name of an entry without a section, for example the file name of a cutscene without its
    /// extension. Null for an entry of a section, whose name comes from the section.</summary>
    public string? Name { get; init; }
}

/// <summary>One line value that names an entry of a list (see <see cref="DataLists.ReferencesTo"/>).</summary>
/// <param name="Section">The section of the line.</param>
/// <param name="Key">The key of the line, as the section spells it.</param>
/// <param name="Line">The position of the line among the lines of the key in the section, from 0.</param>
/// <param name="Index">The index of the value in the line.</param>
/// <param name="Rule">The rule that maps the value to the list. M3 reads its effect for a delete (decision D124).</param>
public sealed record ListReference(SectionRef Section, string Key, int Line, int Index, ListRule Rule);

/// <summary>An entry that the lower layer has and the active mod removes (section 8.5 of the M21 design).</summary>
/// <param name="List">The list id.</param>
/// <param name="Value">The name of the entry.</param>
/// <param name="LowerSection">The section of the lower layer that gives the entry.</param>
/// <param name="ModPath">The file of the active mod that removes it: its patch file or its whole file, or the install
/// path when the edit is not saved.</param>
public sealed record RemovedEntry(string List, string Value, SectionRef LowerSection, string ModPath);

/// <summary>Why the engine cannot use an entry (see <see cref="DataLists.Unusable"/>).</summary>
/// <param name="Reason">What is wrong with the entry, as a clause that ends with a period.</param>
/// <param name="Consequence">What the game does with the entry, or null when the rule of the line that names the
/// entry tells it.</param>
/// <param name="ByActiveMod">True when the active mod made the entry unusable. Then each line of any layer under a
/// closed rule that names the entry is an error.</param>
internal sealed record Unusability(string Reason, string? Consequence, bool ByActiveMod);

/// <summary>
/// The entries of one list for one scope, as W gives them now. A list of a rule with more than one list holds the
/// entries of each list in order, and the first list with a name gives the entry.
/// </summary>
public sealed class ListView
{
    private static readonly ConcurrentDictionary<Type, ListView> EnumLists = new();

    private readonly Dictionary<string, ListEntry> byValue;

    // The key of a value for a look-up, or null to compare the value text without letter case.
    private readonly Func<string, string?>? keyOf;

    /// <param name="keyOf">The key of a value, for a list whose values compare in another way than as text without
    /// letter case, for example as paths. Null for the text.</param>
    internal ListView(string id, string label, string kind, bool closed, bool isEnum, IReadOnlyList<ListEntry> entries,
        IReadOnlyList<IReadOnlyList<string>> collisions, Func<string, string?>? keyOf = null)
    {
        Id = id;
        Label = label;
        Kind = kind;
        Closed = closed;
        IsEnum = isEnum;
        Entries = entries;
        Collisions = collisions;
        this.keyOf = keyOf;
        byValue = new Dictionary<string, ListEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (Key(entry.Value) is { } key)
                byValue.TryAdd(key, entry);
        }
    }

    /// <summary>The list of the names of a C# enum, for example a key of an engine-fixed set. The names of an enum do not
    /// change, so each enum has one list.</summary>
    public static ListView Of(Type enumType) => EnumLists.GetOrAdd(enumType, type =>
        new ListView(type.Name, type.Name, "Enum", true, true,
            Enum.GetNames(type).Select(n => new ListEntry(n, null, ValueOrigin.Base, null, "")).ToList(), []));

    /// <summary>The list id, for example <c>loadout</c>. A view of more than one list joins the ids with '+'.</summary>
    public string Id { get; }

    /// <summary>The label, for example "Loadout".</summary>
    public string Label { get; }

    /// <summary>The kind of the entries, for the glyph of the editor.</summary>
    public string Kind { get; }

    /// <summary>
    /// False when a rule cannot be closed with this list: the engine does not read its files (a data-only role). A
    /// closed rule with such a list gives warnings only.
    /// </summary>
    public bool Closed { get; }

    /// <summary>True for the names of a C# enum. Such a list has no "Missing" state: a value outside it is an error of
    /// the field checks.</summary>
    public bool IsEnum { get; }

    /// <summary>The entries, in load order. A repeated name gives one entry.</summary>
    public IReadOnlyList<ListEntry> Entries { get; }

    /// <summary>The groups of names with one FLHash, for a list whose entries go into an engine collection that throws
    /// for such a group. Empty for the other lists.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Collisions { get; }

    /// <summary>The entry with the name, or null when the name is outside the list. The name compares without letter
    /// case. A list of paths compares the values as paths, so <c>scripts\bases\X.thn</c> and
    /// <c>Scripts/Bases/x.THN</c> name one entry.</summary>
    public ListEntry? Find(string value) => Key(value) is { } key ? byValue.GetValueOrDefault(key) : null;

    /// <summary>The entry with the name as it is written, or null. It is <see cref="Find"/>, but a value with a space
    /// before or after it names no entry. The engine finds a cutscene template and a clip by the text as the script has
    /// it, without a trim.</summary>
    public ListEntry? FindAsWritten(string value) => value.Length == value.Trim().Length ? Find(value) : null;

    private string? Key(string value) => keyOf == null ? value.Trim() : keyOf(value.Trim());

    /// <summary>The groups of names with one FLHash that hold the name.</summary>
    public IEnumerable<IReadOnlyList<string>> CollisionsOf(string value) =>
        Collisions.Where(c => c.Contains(value, StringComparer.OrdinalIgnoreCase));
}
