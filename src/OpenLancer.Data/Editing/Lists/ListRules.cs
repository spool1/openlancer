using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibreLancer.Data;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>What the engine does with a value that is not valid, or with a missing line (section 6.3 of the M21
/// design). Decisions D82 and D124 read it. The values go from the least to the most severe.</summary>
public enum RuleEffect
{
    /// <summary>The engine does not use the value.</summary>
    None,

    /// <summary>The item works with less, for example without its loadout.</summary>
    Degrade,

    /// <summary>The engine drops the item.</summary>
    Drop,

    /// <summary>The game crashes when it uses the item, for example when the player docks. The rule gives the
    /// consequence that tells when. The game loads the item, so the item stays usable for the lines that name it
    /// (decision D82, as decided on 2026-10-18 in the M8b verify).</summary>
    Crash,

    /// <summary>The load of the game data stops.</summary>
    LoadBreak,
}

/// <summary>What a delete does with a line that names the deleted entry (decision D81, milestone M3).</summary>
public enum DeleteAction
{
    /// <summary>The line keeps its value and shows "Missing". A closed rule makes it an error of the save gate.</summary>
    Missing,

    /// <summary>The section of the line goes with the entry, for example a child object with its parent.</summary>
    Cascade,

    /// <summary>The line goes, for example the <c>ring</c> line of a planet.</summary>
    Clear,

    /// <summary>The lines of each excluded zone of the entry go: the <c>exclusion</c> line and the lines after it, up to
    /// the next <c>exclusion</c> or <c>exclude</c> line.</summary>
    ClearHole,

    /// <summary>The delete is refused, as Unity refuses to remove a component that another component requires.</summary>
    Refuse,
}

/// <summary>
/// A rule: one value of one key of one section type, in the files of a role, names an entry of a list.
/// </summary>
/// <param name="Role">The role of the files of the section, for example <c>system</c>.</param>
/// <param name="Section">The section type, for example <c>Object</c>. It compares without letter case.</param>
/// <param name="Key">The key, for example <c>archetype</c>. It compares without letter case.</param>
/// <param name="Index">The index of the value in a line of the key.</param>
/// <param name="Lists">The ids of the lists, in order. The first list that has the name gives the entry.</param>
/// <param name="Closed">True when the engine finds the value only through the list. Then a value of the active mod
/// outside the list is an error (decision D96).</param>
/// <param name="Required">True when the engine drops the item or stops the load without a valid line.</param>
/// <param name="RequiredWith">A key that makes the rule required when the section has a line of it, or null.</param>
/// <param name="Empty">True when an empty value is valid.</param>
/// <param name="Scope">Null, <c>file</c> (the sections of the file of the section), or <c>systemOf:N</c> (the
/// sections of the system file that value N of the line names).</param>
/// <param name="Label">The label of the value in a line with more than one listed value, or null.</param>
/// <param name="Effect">What the engine does with a value that is not valid, or with a missing line.</param>
/// <param name="Consequence">The message about what the engine does, or null.</param>
/// <param name="Evidence">The engine lines that prove the rule.</param>
public sealed record ListRule(string Role, string Section, string Key, int Index, IReadOnlyList<string> Lists,
    bool Closed, bool Required, string? RequiredWith, bool Empty, string? Scope, string? Label,
    RuleEffect Effect, string? Consequence, string? Evidence)
{
    /// <summary>True when no retail line has the key. The retail check then expects no line for the rule.</summary>
    public bool Unused { get; init; }

    /// <summary>
    /// True for the <c>scene</c> line of a room (section 5.6 of the M8b design): the rule reads value 1 of the line, or
    /// value 2 when value 0 is <c>all</c>, whatever its text, as the engine reads it (RoomInfo.cs:31-45). The engine
    /// skips a line with too few values (RoomInfo.cs:50-53), so such a line gets no check.
    /// </summary>
    public bool Scene { get; init; }

    /// <summary>
    /// True when a value outside the list is valid if its file exists in the edit view (milestone M22, decision D160).
    /// Only a rule of a files list can have it. The list holds the files of one folder and one class, for example the
    /// nebula files of <c>SOLAR\NEBULA</c>. But the engine reads a file of any folder.
    /// </summary>
    public bool Exists { get; init; }

    /// <summary>
    /// True when the rule does not apply to the rooms of the intro bases. The fork marks an intro base by the nickname
    /// prefix "intro" (GameItemDb.cs:261). Only the main menu shows these rooms, and the player cannot enter them.
    /// </summary>
    public bool SkipIntroRooms { get; init; }

    /// <summary>The index of the value that the rule reads in a line, or -1 when the line has no such value. Without
    /// <see cref="Scene"/>, it is <see cref="Index"/>.</summary>
    public int IndexIn(Entry line) => IndexIn(line.Count, line.Count > 0 ? line[0].ToString() : null);

    /// <summary>The index of the value that the rule reads in the values of a line (see <see cref="IndexIn(Entry)"/>).
    /// The Inspector uses it to put a picked value at the place of the rule.</summary>
    public int IndexIn(IReadOnlyList<ValueBase> values) =>
        IndexIn(values.Count, values.Count > 0 ? values[0].ToString() : null);

    private int IndexIn(int count, string? first)
    {
        if (!Scene)
            return Index < count ? Index : -1;
        if (count == 0)
            return -1;
        var at = first?.Trim().Equals(SceneAll, StringComparison.OrdinalIgnoreCase) == true ? 2 : 1;
        return at < count ? at : -1;
    }

    /// <summary>The first value of a <c>scene</c> line for the ambient scripts of all times of day.</summary>
    private const string SceneAll = "all";

    /// <summary>
    /// What a delete of the entry does with the line (decision D81, milestone M3). Without an <c>onDelete</c> value in the
    /// rule data, a rule with the effect load-break refuses the delete (decision D124), and each other rule leaves
    /// "Missing".
    /// </summary>
    public DeleteAction OnDelete { get; init; }

    /// <summary>True when the scope is <c>owner</c>: the zones of the system files that name the nebula or field file
    /// of the line (milestone M3).</summary>
    public bool OwnerScope => Scope == OwnerScopeName;

    internal const string OwnerScopeName = "owner";

    /// <summary>The value index of a <c>systemOf:N</c> scope, or -1.</summary>
    public int SystemValue =>
        Scope != null && Scope.StartsWith(SystemOfPrefix, StringComparison.Ordinal) &&
        int.TryParse(Scope.AsSpan(SystemOfPrefix.Length), out var index)
            ? index
            : -1;

    /// <summary>What the game does with a value that is not valid, in plain words: the consequence, or the text of the
    /// effect.</summary>
    public string ConsequenceText => Consequence ?? Effect switch
    {
        RuleEffect.Degrade => "The game ignores this value.",
        RuleEffect.Drop => "The game drops this item.",
        RuleEffect.Crash => "The game crashes with this value.",
        RuleEffect.LoadBreak => "The game stops loading with this value.",
        _ => "The game does not use this value.",
    };

    /// <summary>What the game does when the key has no line, or null when it does the same as with a value that is not
    /// valid. For example, the engine drops a <c>[Nebula]</c> without a <c>file</c> line, but it crashes with a file that
    /// does not exist (milestone M22).</summary>
    public string? MissingConsequence { get; init; }

    /// <summary>What the game does when the key has no valid line, in plain words. It is the consequence for a missing
    /// line, the consequence, or the text of the effect.</summary>
    public string MissingText => MissingConsequence ?? Consequence ?? Effect switch
    {
        RuleEffect.LoadBreak => "The game stops loading without this key.",
        _ => ConsequenceText,
    };

    /// <summary>
    /// True for an effect that makes the item unusable: the engine drops it, or the load stops. A line that names such
    /// an item is then an error (decision D82, as decided on 2026-10-18). A crash is not such an effect: the game loads
    /// the item and crashes only when it uses the bad value, for example when the player docks. So a crash error stays
    /// on its own line, and it still blocks Save. A jump gate whose <c>dock_with</c> names no base is still a valid exit
    /// for the <c>goto</c> line of its partner gate.
    /// </summary>
    internal static bool MakesUnusable(RuleEffect effect) => effect is RuleEffect.Drop or RuleEffect.LoadBreak;

    internal const string SystemOfPrefix = "systemOf:";
}

/// <summary>How the engine finds the files of a role (section 7 of the M21 design).</summary>
/// <param name="Data">The <c>[Data]</c> name of the role in <c>freelancer.ini</c>, or null.</param>
/// <param name="Last">True when the engine reads only the last entry of the name.</param>
/// <param name="Fallback">The path that the engine reads without an entry, relative to the data folder, or null.</param>
/// <param name="Paths">Fixed paths that the engine reads, relative to the data folder.</param>
/// <param name="Engine">False when retail reads the files but the fork does not. A list of such a role is never
/// closed.</param>
/// <param name="Universe">True for <c>universe.ini</c>.</param>
/// <param name="SystemFiles">True for the files that the <c>[system]</c> sections of <c>universe.ini</c> name.</param>
/// <param name="RoomFiles">True for the room files that the <c>[Room]</c> sections of the base files name (decision D135
/// of the M8b design).</param>
/// <param name="FieldFiles">For the nebula or asteroid field files: the section type of the system files whose
/// <c>file</c> lines name the files, <c>Nebula</c> or <c>Asteroids</c> (milestone M3). Null for another role.</param>
/// <param name="MissionFiles">True for the mission scripts of <c>MissionFiles</c> (section 14.1 of the M6a
/// design).</param>
/// <param name="NpcShipFiles">True for the files of the NPC ship builds of <c>NpcShipFiles</c> (section 14.1 of the M6a
/// design).</param>
internal sealed record RoleDefinition(string Name, string? Data, bool Last, string? Fallback,
    IReadOnlyList<string> Paths, bool Engine, bool Universe, bool SystemFiles, bool RoomFiles, string? FieldFiles = null,
    bool MissionFiles = false, bool NpcShipFiles = false)
{
    /// <summary>True when the files of the role change in a session, so the editor finds them at each use.</summary>
    public bool Dynamic => Universe || SystemFiles || RoomFiles || FieldFiles != null || MissionFiles || NpcShipFiles;
}

/// <summary>A template of Create in the Scene (decision D233 of the M3 design).</summary>
/// <param name="Id">The id, for example <c>station</c>.</param>
/// <param name="Label">The label of the Create menu, for example "Station".</param>
/// <param name="Types">The <c>[Solar] type</c> values of the archetypes of the template, in lowercase.</param>
/// <param name="Needs">A key that an archetype of the template must have, for example <c>docking_sphere</c>, or
/// null.</param>
/// <param name="Then">The next pick after the archetype: <c>base</c> for the base picker of a station, or null.</param>
internal sealed record SceneTemplateDefinition(string Id, string Label, IReadOnlyList<string> Types, string? Needs,
    string? Then);

/// <summary>Where the entries of a list come from (section 6.4 of the M21 design).</summary>
/// <param name="Role">The role of the files. A scoped list without a role reads the sections of a system file. A scoped
/// list with a role reads the sections of one file of the role, for example one mission script (section 14.1 of the M6a
/// design).</param>
/// <param name="Sections">The section types whose sections give the entries.</param>
/// <param name="Keys">The keys that give the names, or null for the key entry of the section type.</param>
/// <param name="Where">Key values that a section must have, for example <c>type = FLOAT</c>.</param>
/// <param name="Values">For a values list: the section type, the key and the value index whose distinct values are the
/// entries. Null for a sections list.</param>
/// <param name="Scoped">True when the rule gives the file (a scoped list).</param>
/// <param name="Files">For a files list: the extensions of the files whose paths are the entries, for example <c>.thn</c>
/// (section 5.6 of the M8b design), or <c>.cmp</c>, <c>.3db</c> and <c>.sph</c> for the models (milestone M23). Null for
/// the other lists.</param>
/// <param name="Folder">For a files list: the folder below the data folder that holds the files, for example
/// <c>SOLAR\NEBULA</c> (milestone M22), or <c>.</c> for the data folder itself (milestone M23). Null for all files of
/// the edit view.</param>
/// <param name="FileClass">For a files list: the full name of a <c>[ParsedIni]</c> class of the engine, for example
/// <c>LibreLancer.Data.Schema.Universe.Nebula</c> (milestone M22). A file is an entry only when it has a section and
/// each of its section types is a section of the class. Null for a file of any content.</param>
internal sealed record ListSource(string? Role, IReadOnlyList<string> Sections, IReadOnlyList<string>? Keys,
    IReadOnlyDictionary<string, string> Where, (string Section, string Key, int Index)? Values, bool Scoped,
    IReadOnlyList<string>? Files = null, string? Folder = null, string? FileClass = null)
{
    /// <summary>The engine class of <see cref="FileClass"/>, or null.</summary>
    public Type? FileClassType => FileClass == null ? null : typeof(FLHash).Assembly.GetType(FileClass);
}

/// <summary>A top category of the Assets panel (milestone M23, decision D267).</summary>
/// <param name="Id">The id, for example <c>solars</c>. A child category gets the id of its parent, a slash and a part of
/// its own, for example <c>solars/station</c>.</param>
/// <param name="Label">The label of the category tree, for example "Solars".</param>
/// <param name="Kind">A kind of rows that no list gives: <see cref="PropGroupsKind"/> for the prop groups of the mods,
/// and <see cref="MissionPrefabsKind"/> for the mission prefab assets of the mods. Null for a category of a list.</param>
/// <param name="List">The list whose entries are the rows, or null for a category of a kind.</param>
/// <param name="Split">How the category gets its children: <see cref="SplitTemplates"/> (one child for each template of
/// Create, and one for the connection types), <see cref="SplitSection"/> (one child for each section type) or
/// <see cref="SplitFolder"/> (one child for each top folder below the data folder). Null for no children.</param>
internal sealed record AssetCategoryDefinition(string Id, string Label, string? Kind, string? List, string? Split)
{
    public const string PropGroupsKind = "propGroups";
    public const string MissionPrefabsKind = "missionPrefabs";
    public const string SplitTemplates = "sceneTemplates";
    public const string SplitSection = "section";
    public const string SplitFolder = "folder";
}

/// <summary>One list of the rule data.</summary>
/// <param name="Kind">The kind of the entries, for the glyph of the editor.</param>
/// <param name="Hash">True when the engine adds the entries to a collection that throws when two names have one
/// FLHash.</param>
/// <param name="Unique">True when the engine finds an entry only when exactly one section has the name.</param>
/// <param name="Requires">The keys that an entry needs to be usable.</param>
/// <param name="Detail">The keys of the entry for the footer of the picker.</param>
/// <param name="Source">Where the entries come from, or null for an enum list.</param>
/// <param name="Enum">The full name of the C# enum of an enum list, or null.</param>
internal sealed record ListDefinition(string Id, string Label, string Kind, bool Hash, bool Unique,
    IReadOnlyList<string> Requires, IReadOnlyList<string> Detail, ListSource? Source, string? Enum);

/// <summary>
/// The rule data of the data lists: roles, lists and rules (decision D112). The editor ships the retail rules as an
/// embedded resource. M25 reads mod rule files in the same format.
/// </summary>
internal sealed class ListRules
{
    private const string ResourceName = "OpenLancer.Data.Editing.Lists.list-rules.json";

    private static readonly Lazy<ListRules> RetailRules = new(() =>
    {
        using var stream = typeof(ListRules).Assembly.GetManifestResourceStream(ResourceName) ??
                           throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    private readonly Dictionary<(string Role, string Section, string Key), List<ListRule>> byAddress;

    private ListRules(IReadOnlyDictionary<string, RoleDefinition> roles, IReadOnlyList<ListDefinition> lists,
        IReadOnlyList<ListRule> rules, IReadOnlyList<string> connectionTypes, IReadOnlyList<SceneTemplateDefinition> templates,
        IReadOnlyList<AssetCategoryDefinition> assetCategories, SceneBuilders builders)
    {
        Roles = roles;
        ConnectionTypes = connectionTypes;
        SceneTemplates = templates;
        AssetCategories = assetCategories;
        Builders = builders;
        Lists = lists.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase);
        All = rules;
        byAddress = rules.GroupBy(r => (r.Role.ToLowerInvariant(), r.Section.ToLowerInvariant(), r.Key.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Index).ToList());
        ByRoleAndSection = rules.GroupBy(r => (r.Role.ToLowerInvariant(), r.Section.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ListRule>)g.ToList());
    }

    /// <summary>The rules that ship with the editor.</summary>
    public static ListRules Retail => RetailRules.Value;

    public IReadOnlyDictionary<string, RoleDefinition> Roles { get; }

    public IReadOnlyDictionary<string, ListDefinition> Lists { get; }

    /// <summary>All rules, in the order of the file.</summary>
    public IReadOnlyList<ListRule> All { get; }

    /// <summary>The <c>[Solar] type</c> values of the connection objects, in lowercase: jump gates, jump holes, airlock
    /// gates and trade lane rings (decision D253 of the M3 design).</summary>
    public IReadOnlyList<string> ConnectionTypes { get; }

    /// <summary>The templates of Create in the Scene, in the order of the menu.</summary>
    public IReadOnlyList<SceneTemplateDefinition> SceneTemplates { get; }

    /// <summary>The top categories of the Assets panel, in the order of the category tree (milestone M23).</summary>
    public IReadOnlyList<AssetCategoryDefinition> AssetCategories { get; }

    /// <summary>The rules of the builders of the agent tools: the wreck and the route templates (milestone
    /// MCP1-2).</summary>
    public SceneBuilders Builders { get; }

    /// <summary>The rules of each (role, section type) in lower case.</summary>
    public IReadOnlyDictionary<(string Role, string Section), IReadOnlyList<ListRule>> ByRoleAndSection { get; }

    /// <summary>The rules of a key of a section type in the files of a role, by value index.</summary>
    public IReadOnlyList<ListRule> Rules(string role, string section, string key) =>
        byAddress.TryGetValue((role.ToLowerInvariant(), section.ToLowerInvariant(), key.ToLowerInvariant()),
            out var rules)
            ? rules
            : [];

    /// <summary>Reads rule data.</summary>
    /// <exception cref="InvalidDataException">The data is not valid.</exception>
    public static ListRules Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
            var root = document.RootElement;
            var roles = root.GetProperty("roles").EnumerateObject()
                .Select(p => ReadRole(p.Name, p.Value))
                .ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            var lists = root.GetProperty("lists").EnumerateArray().Select(ReadList).ToList();
            var rules = root.GetProperty("rules").EnumerateArray().SelectMany(ReadRules).ToList();
            var files = lists.Where(l => l.Source?.Files != null).Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules.Where(r => r.Exists && !r.Lists.All(files.Contains)))
                throw new FormatException($"The rule {rule.Section} {rule.Key} has 'exists', which needs a files list.");
            foreach (var rule in rules.Where(r => r.OwnerScope))
            {
                if (!roles.TryGetValue(rule.Role, out var role) || role.FieldFiles == null)
                    throw new FormatException($"The rule {rule.Section} {rule.Key} has the scope 'owner', which needs a " +
                                              "role of nebula or field files.");
            }
            var connections = Strings(root, "connectionTypes").Select(t => t.ToLowerInvariant()).ToList();
            var templates = root.TryGetProperty("sceneTemplates", out var array)
                ? array.EnumerateArray().Select(ReadTemplate).ToList()
                : [];
            foreach (var template in templates)
            {
                if (template.Types.FirstOrDefault(connections.Contains) is { } connection)
                    throw new FormatException($"The scene template {template.Id} has the connection type '{connection}'.");
            }
            var categories = root.TryGetProperty("assetCategories", out var assets)
                ? assets.EnumerateArray().Select(ReadAssetCategory).ToList()
                : [];
            CheckAssetCategories(categories, lists);
            var builders = root.TryGetProperty("sceneBuilders", out var builder)
                ? SceneBuilders.Read(builder, connections)
                : SceneBuilders.None;
            return new ListRules(roles, lists, rules, connections, templates, categories, builders);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or
                                       FormatException or ArgumentException)
        {
            throw new InvalidDataException("The list rules are not valid: " + e.Message, e);
        }
    }

    private static RoleDefinition ReadRole(string name, JsonElement e) => new(name,
        String(e, "data"), Bool(e, "last"), String(e, "fallback"), Strings(e, "paths"),
        !e.TryGetProperty("engine", out var engine) || engine.GetBoolean(), Bool(e, "universe"), Bool(e, "systemFiles"),
        Bool(e, "roomFiles"), String(e, "fieldFiles"), Bool(e, "missionFiles"), Bool(e, "npcShipFiles"));

    private static AssetCategoryDefinition ReadAssetCategory(JsonElement e) => new(e.GetProperty("id").GetString()!,
        e.GetProperty("label").GetString()!, String(e, "kind"), String(e, "list"), String(e, "split"));

    /// <summary>Checks the categories of the Assets panel: an id that is not empty, has no slash and does not repeat,
    /// either a list of the rule data or a known kind, and a known split.</summary>
    private static void CheckAssetCategories(IReadOnlyList<AssetCategoryDefinition> categories,
        IReadOnlyList<ListDefinition> lists)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            if (category.Id.Length == 0 || category.Id.Contains('/') || !ids.Add(category.Id))
                throw new FormatException($"The asset category id '{category.Id}' is empty, has a slash or repeats.");
            if ((category.Kind == null) == (category.List == null))
                throw new FormatException($"The asset category {category.Id} needs a list or a kind, not both.");
            if (category.Kind is { } kind && kind != AssetCategoryDefinition.PropGroupsKind &&
                kind != AssetCategoryDefinition.MissionPrefabsKind)
                throw new FormatException($"The kind '{kind}' of the asset category {category.Id} is not known.");
            if (category.List is { } list && !lists.Any(l => l.Id.Equals(list, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"The asset category {category.Id} names the list '{list}', which the rule " +
                                          "data does not have.");
            if (category.Split is not (null or AssetCategoryDefinition.SplitTemplates or
                AssetCategoryDefinition.SplitSection or AssetCategoryDefinition.SplitFolder))
                throw new FormatException($"The split '{category.Split}' of the asset category {category.Id} is not known.");
        }
    }

    private static SceneTemplateDefinition ReadTemplate(JsonElement e) => new(e.GetProperty("id").GetString()!,
        e.GetProperty("label").GetString()!, Strings(e, "types").Select(t => t.ToLowerInvariant()).ToList(),
        String(e, "needs"), String(e, "then"));

    private static ListDefinition ReadList(JsonElement e)
    {
        ListSource? source = null;
        if (e.TryGetProperty("source", out var s))
        {
            (string, string, int)? values = s.TryGetProperty("values", out var v)
                ? (v.GetProperty("section").GetString()!, v.GetProperty("key").GetString()!, v.GetProperty("index").GetInt32())
                : null;
            var where = s.TryGetProperty("where", out var w)
                ? w.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var id = e.GetProperty("id").GetString();
            // The extensions of a files list: one text, or an array of texts (milestone M23).
            IReadOnlyList<string>? files = !s.TryGetProperty("files", out var f) ? null
                : f.ValueKind == JsonValueKind.Array ? Strings(s, "files")
                : [f.GetString()!];
            if (files != null && (files.Count == 0 || files.Any(x => x.Length < 2 || x[0] != '.')))
                throw new FormatException($"The list {id} needs one or more extensions that start with a dot.");
            source = new ListSource(String(s, "role"), Strings(s, "sections"),
                s.TryGetProperty("keys", out _) ? Strings(s, "keys") : null, where, values,
                String(s, "scope") == "file", files, String(s, "folder"), String(s, "fileClass"));
            if (source.Files == null && (source.Folder != null || source.FileClass != null))
                throw new FormatException($"The list {id} has a folder or a file class, which needs 'files'.");
            if (source.FileClass != null && source.FileClassType?.IsDefined(typeof(ParsedIniAttribute), false) != true)
                throw new FormatException($"The file class {source.FileClass} of the list {id} is not a [ParsedIni] class.");
        }
        return new ListDefinition(e.GetProperty("id").GetString()!, e.GetProperty("label").GetString()!,
            String(e, "kind") ?? "Other", Bool(e, "hash"), Bool(e, "unique"), Strings(e, "requires"),
            Strings(e, "detail"), source, String(e, "enum"));
    }

    /// <summary>The rules of one entry of the rules array: one rule, or one rule for each element of
    /// <c>values</c>.</summary>
    private static IEnumerable<ListRule> ReadRules(JsonElement e)
    {
        var role = e.GetProperty("role").GetString()!;
        var section = e.GetProperty("section").GetString()!;
        var key = e.GetProperty("key").GetString()!;
        if (e.TryGetProperty("values", out var values))
            return values.EnumerateArray().Select(v => ReadRule(role, section, key, v.GetProperty("index").GetInt32(), v))
                .ToList();
        return [ReadRule(role, section, key, 0, e)];
    }

    private static ListRule ReadRule(string role, string section, string key, int index, JsonElement e)
    {
        IReadOnlyList<string> lists = e.TryGetProperty("lists", out _)
            ? Strings(e, "lists")
            : [e.GetProperty("list").GetString()!];
        var effect = String(e, "effect") switch
        {
            null or "none" => RuleEffect.None,
            "degrade" => RuleEffect.Degrade,
            "drop" => RuleEffect.Drop,
            "crash" => RuleEffect.Crash,
            "loadBreak" => RuleEffect.LoadBreak,
            var other => throw new FormatException($"The effect '{other}' of {section} {key} is not known."),
        };
        var onDelete = String(e, "onDelete") switch
        {
            null => effect == RuleEffect.LoadBreak ? DeleteAction.Refuse : DeleteAction.Missing,
            "missing" => DeleteAction.Missing,
            "cascade" => DeleteAction.Cascade,
            "clear" => DeleteAction.Clear,
            "clearHole" => DeleteAction.ClearHole,
            "refuse" => DeleteAction.Refuse,
            var other => throw new FormatException($"The onDelete value '{other}' of {section} {key} is not known."),
        };
        return new ListRule(role, section, key, index, lists, Bool(e, "closed"), Bool(e, "required"),
            String(e, "requiredWith"), Bool(e, "empty"), String(e, "scope"), String(e, "label"), effect,
            String(e, "consequence"), String(e, "evidence"))
        {
            Unused = Bool(e, "unused"),
            Scene = Bool(e, "scene"),
            Exists = Bool(e, "exists"),
            SkipIntroRooms = Bool(e, "skipIntroRooms"),
            MissingConsequence = String(e, "missing"),
            OnDelete = onDelete,
        };
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.GetBoolean();

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : [];
}
