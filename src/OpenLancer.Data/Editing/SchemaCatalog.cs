using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>The keys that the engine reads in one section type of one kind of INI file.</summary>
/// <param name="Class">The upstream class that the generated parser fills from the section.</param>
/// <param name="Keys">The keys in declaration order: the fields of the base classes first, then the entry
/// handlers.</param>
internal sealed record SectionSchema(Type Class, IReadOnlyList<KeySchema> Keys)
{
    private readonly Dictionary<string, KeySchema> byName =
        Keys.GroupBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the class reads the lines that no key names itself, one by one in file order
    /// (<see cref="IEntryHandler"/>). Then every name is known, and the order of all lines counts.
    /// </summary>
    public bool ReadsAllLines => typeof(IEntryHandler).IsAssignableFrom(Class);

    public KeySchema? Key(string name) => byName.GetValueOrDefault(name);
}

/// <summary>
/// The schema of the INI files that the engine reads with the generated parsers of upstream Librelancer, and the
/// text kind of each key that holds a text id.
/// </summary>
/// <remarks>
/// <para>The catalog reads the attributes of the upstream schema by reflection, as the source generator of the
/// parsers does: <c>[ParsedIni]</c> classes, their <c>[Section]</c> fields, and the <c>[Entry]</c> fields and
/// <c>[EntryHandler]</c> methods of each section class and its base classes. So it follows upstream changes.</para>
/// <para>A section name can have different classes in different files, for example <c>[Explosion]</c> in
/// <c>fx\explosions.ini</c> and in the equipment files. So a schema belongs to a <c>[ParsedIni]</c> class.</para>
/// <para>The catalog never calls a generated parser, because a parser logs its problems through FLLog.</para>
/// </remarks>
internal static class SchemaCatalog
{
    /// <summary>
    /// The keys that hold the id of a text: a string of the string table, or an infocard (RDL XML). The retail test
    /// <c>NameKeyKindTests</c> checks each retail value of these keys against the retail resource DLLs.
    /// </summary>
    /// <remarks>
    /// Freelancer also shows a string id of <c>ids_info</c> as an infocard with plain text, and some retail objects,
    /// solars and commodities use that. Librelancer reads an infocard only, so the editor writes an infocard.
    /// </remarks>
    private static readonly Dictionary<string, StringKind> TextKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ids_name"] = StringKind.String,
        ["ids_short_name"] = StringKind.String,
        ["strid_name"] = StringKind.String,
        ["tradelane_space_name"] = StringKind.String,
        // The name of a bar character of mBases.ini, and of a mission NPC. Retail gives three lines: the faction, the
        // title and the name of the person.
        ["individual_name"] = StringKind.String,
        ["ids_info"] = StringKind.Infocard,
        // The ship dealer shows these three infocards: the description, the stats labels and the stats values.
        ["ids_info1"] = StringKind.Infocard,
        ["ids_info2"] = StringKind.Infocard,
        ["ids_info3"] = StringKind.Infocard,
    };

    /// <summary>The section types in which a key holds another kind of text than <see cref="TextKeys"/> gives.</summary>
    private static readonly Dictionary<(string Section, string Name), StringKind> TextKeyExceptions =
        new(SectionAndName.Instance)
        {
            // The tool tip of a key command in DATA\INTERFACE\keymap.ini.
            [("KeyCmd", "ids_info")] = StringKind.String,
        };

    /// <summary>
    /// The keys that the fork reads but the retail game never reads (decision D513 of the console filter design). The
    /// checks skip their lines, because a value of such a key has no effect in the retail game.
    /// </summary>
    private static readonly HashSet<(string Section, string Name)> RetailUnread = new(SectionAndName.Instance)
    {
        // Retail has no reader for the lane id of a zone. Each retail trade lane zone has a string in it.
        ("Zone", "lane_id"),
    };

    /// <summary>The schema of each section class and section name (in lowercase) that the catalog read.</summary>
    private static readonly ConcurrentDictionary<(Type Class, string Section), SectionSchema> Classes = new();

    private static readonly Lazy<Dictionary<Type, Dictionary<string, SectionSchema>>> Inis = new(ReadInis);

    /// <summary>Each section type of each <c>[ParsedIni]</c> class, with its schema.</summary>
    public static IEnumerable<(Type Ini, string Section, SectionSchema Schema)> All =>
        Inis.Value.SelectMany(ini => ini.Value.Select(s => (ini.Key, s.Key, s.Value)));

    /// <summary>The schema of a section type in the files that a <c>[ParsedIni]</c> class reads, or null.</summary>
    public static SectionSchema? Section(Type ini, string sectionName) =>
        Inis.Value.TryGetValue(ini, out var sections) ? sections.GetValueOrDefault(sectionName) : null;

    /// <summary>
    /// The schema of a section class in a file that no <c>[ParsedIni]</c> class reads, for example a <c>[system]</c>
    /// of <c>universe.ini</c>. The engine reads such a file itself, and gives each section to the generated parser of
    /// its class. The key entry and the text kinds depend on the section name.
    /// </summary>
    public static SectionSchema SectionClass(Type type, string sectionName) => Schema(type, sectionName);

    /// <summary>The kind of text that a key of a section type holds the id of, or null when the key holds no text
    /// id.</summary>
    public static StringKind? TextKind(string sectionName, string name) =>
        TextKeyExceptions.TryGetValue((sectionName, name), out var exception) ? exception
        : TextKeys.TryGetValue(name, out var kind) ? kind
        : null;

    /// <summary>The rows of the name-key table. A row of a section-type exception has its section type. The other
    /// rows have no section type, because they apply to all section types.</summary>
    public static IEnumerable<(string? Section, string Name, StringKind Kind)> TextKeyRows =>
        TextKeys.Select(k => ((string?)null, k.Key, k.Value))
            .Concat(TextKeyExceptions.Select(e => ((string?)e.Key.Section, e.Key.Name, e.Value)));

    private static Dictionary<Type, Dictionary<string, SectionSchema>> ReadInis()
    {
        var result = new Dictionary<Type, Dictionary<string, SectionSchema>>();
        foreach (var ini in typeof(ParsedIniAttribute).Assembly.GetTypes().Where(t => t.IsDefined(typeof(ParsedIniAttribute))))
        {
            var sections = new Dictionary<string, SectionSchema>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in Hierarchy(ini).SelectMany(t => t.GetFields(Members | BindingFlags.DeclaredOnly)))
            {
                foreach (var attribute in field.GetCustomAttributes<SectionAttribute>())
                {
                    var type = attribute.Type ?? ItemType(field.FieldType);
                    // The parser uses the first field that names a section.
                    if (!sections.ContainsKey(attribute.Name))
                        sections[attribute.Name] = Schema(type, attribute.Name);
                }
            }
            result[ini] = sections;
        }
        return result;
    }

    /// <summary>The schema of a section class. Each section name has its own key entry, so its own schema.</summary>
    private static SectionSchema Schema(Type type, string sectionName) =>
        // Two threads can build one schema at the same time, but both get the one that the cache keeps.
        Classes.GetOrAdd((type, sectionName.ToLowerInvariant()), _ => Build(type, sectionName));

    private static SectionSchema Build(Type type, string sectionName)
    {
        var match = SectionKeys.For(sectionName);
        var keyEntry = match.Kind == SectionMatchKind.Keyed ? match.KeyEntry : null;
        var keys = new List<KeySchema>();
        // The generated parser reads the members of the base classes first.
        foreach (var t in Hierarchy(type))
        {
            foreach (var field in t.GetFields(Members | BindingFlags.DeclaredOnly))
            {
                foreach (var entry in field.GetCustomAttributes<EntryAttribute>())
                    keys.Add(FieldKey(entry, field, sectionName, keyEntry));
            }
            foreach (var method in t.GetMethods(Members | BindingFlags.DeclaredOnly))
            {
                foreach (var handler in method.GetCustomAttributes<EntryHandlerAttribute>())
                {
                    keys.Add(new KeySchema(handler.Name, null, handler.Multiline, false, handler.MinComponents,
                        IsKey(handler.Name, keyEntry), TextKind(sectionName, handler.Name)));
                }
            }
        }
        return new SectionSchema(type, keys);
    }

    private static KeySchema FieldKey(EntryAttribute entry, FieldInfo field, string sectionName, string? keyEntry)
    {
        var type = LineType(field.FieldType);
        return new KeySchema(entry.Name, type, entry.Multiline, entry.Required, MinComponents(type, entry),
            IsKey(entry.Name, keyEntry), TextKind(sectionName, entry.Name))
        {
            Presence = entry.Presence,
            MinMax = entry.MinMax,
            FloatColor = entry.FloatColor,
            Mode = entry.Mode,
            Field = field.Name,
            RetailReads = !RetailUnread.Contains((sectionName, entry.Name)),
        };
    }

    /// <summary>The type that one line gives: the field type without <c>List&lt;&gt;</c> and without
    /// <c>Nullable&lt;&gt;</c>. An array stays an array, because one line holds all its values.</summary>
    private static Type LineType(Type fieldType)
    {
        if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
            fieldType = fieldType.GetGenericArguments()[0];
        return Nullable.GetUnderlyingType(fieldType) ?? fieldType;
    }

    /// <summary>The number of values that a line needs, as the generated parser checks it.</summary>
    private static int MinComponents(Type type, EntryAttribute entry)
    {
        if (type.IsArray)
            return 1;
        if (type == typeof(Vector2))
            return entry.MinMax ? 1 : 2;
        if (type == typeof(Vector3))
            return entry.Mode == Vec3Mode.None ? 3 : 1;
        if (type == typeof(Vector4) || type == typeof(Quaternion))
            return 4;
        if (type == typeof(Color4) || type == typeof(Color3f))
            return 3;
        if (type == typeof(ValueRange<int>) || type == typeof(ValueRange<float>))
            return 2;
        if (type == typeof(string) || type == typeof(bool))
            return entry.Presence ? 0 : 1;
        return 1;
    }

    private static bool IsKey(string name, string? keyEntry) =>
        keyEntry != null && name.Equals(keyEntry, StringComparison.OrdinalIgnoreCase);

    /// <summary>The section class of a <c>[Section]</c> field: the field type, or the item type of a list.</summary>
    private static Type ItemType(Type fieldType) =>
        fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>)
            ? fieldType.GetGenericArguments()[0]
            : fieldType;

    /// <summary>The class and its base classes, the most basic class first.</summary>
    private static IEnumerable<Type> Hierarchy(Type type)
    {
        var chain = new List<Type>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            chain.Add(t);
        chain.Reverse();
        return chain;
    }

    /// <summary>Compares (section name, entry name) pairs without letter case.</summary>
    private sealed class SectionAndName : IEqualityComparer<(string Section, string Name)>
    {
        public static readonly SectionAndName Instance = new();

        public bool Equals((string Section, string Name) x, (string Section, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Section, y.Section) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((string Section, string Name) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Section), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                         BindingFlags.Static;
}
