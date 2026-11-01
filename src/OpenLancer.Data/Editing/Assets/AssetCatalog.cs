using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Assets;

/// <summary>The kind of an asset of the Assets panel (milestone M23).</summary>
public enum AssetKind
{
    PropGroup,
    SolarArchetype,
    Ship,
    Asteroid,
    Equipment,
    Effect,
    Loadout,
    Faction,
    Model,

    /// <summary>A mission prefab asset of a mod, <c>prefabs\missions\&lt;id&gt;.ini</c> (section 8.3 of the M6b
    /// design). The id is the file name of the prefab.</summary>
    MissionPrefab,
}

/// <summary>An asset: a list entry, a model file or a prop group.</summary>
/// <param name="Id">The nickname of a list entry or of a prop group, or the path of a model file below the data
/// folder.</param>
/// <param name="Section">The section of a list entry, or null.</param>
/// <param name="File">The full path of the file of a prop group, or null.</param>
public sealed record AssetRef(AssetKind Kind, string Id, SectionRef? Section = null, string? File = null);

/// <summary>A category of the category tree of the Assets panel.</summary>
/// <param name="Id">The id, for example <c>solars/station</c>. The root has the id <see cref="AssetCatalog.AllId"/>.</param>
/// <param name="Label">The label, for example "Station".</param>
/// <param name="ParentId">The id of the parent category, or null for the root.</param>
/// <param name="Count">The number of rows of the category and its children.</param>
public sealed record AssetCategory(string Id, string Label, string? ParentId, int Count)
{
    /// <summary>The kind of the rows, or null for the root.</summary>
    public AssetKind? Kind { get; init; }
}

/// <summary>The marks of a row whose section the active mod adds or changes (decision D245 of the M3 design).</summary>
[Flags]
public enum AssetMark
{
    None = 0,

    /// <summary>The active mod adds the section: a "+" badge.</summary>
    Added = 1,

    /// <summary>The active mod changes the section: a blue bar.</summary>
    Changed = 2,

    /// <summary>The mod files do not have the entry yet: the tooltip says "Not saved yet."</summary>
    Unsaved = 4,
}

/// <summary>
/// One row of the Assets panel (section 5.1 of the M23 design). The display name is not part of the row: the editor
/// gives it from the text of the loaded game data (review finding R7).
/// </summary>
/// <param name="Nickname">The nickname of a list entry or of a prop group, or the file name of a model.</param>
/// <param name="Detail">A short text: the type, the model file, the section type, the folder or the members.</param>
/// <param name="CategoryId">The id of the deepest category of the row, for example <c>solars/station</c>.</param>
/// <param name="Origin">The layer that gives the asset.</param>
/// <param name="ModId">The mod that gives the asset, or null for the base data.</param>
/// <param name="NotPlaceable">Why the Scene cannot place the asset, or null when it can.</param>
public sealed record AssetRow(AssetRef Asset, string Nickname, string Detail, string CategoryId, ValueOrigin Origin,
    string? ModId, AssetMark Mark, string? NotPlaceable)
{
    /// <summary>The list of a list entry, for example <c>loadout</c>, or null for a prop group.</summary>
    public string? List { get; init; }

    /// <summary>The model file of the asset below the data folder, or null.</summary>
    public string? ModelPath { get; init; }

    /// <summary>True for a ship, an asteroid or a model when W has a solar archetype with its model: the "prop"
    /// badge.</summary>
    public bool HasProp { get; init; }

    /// <summary>True for an asset whose file has a problem, for example a mission prefab: the error mark. It cannot place,
    /// and it does not open.</summary>
    public bool HasProblem { get; init; }
}

/// <summary>
/// The assets of the edit view for the Assets panel (milestone M23): the categories of the rule data, and their rows
/// from the data lists of W, the model files and the prop groups. A new or changed item of the active mod shows at once,
/// also before a save (decision D268).
/// </summary>
/// <remarks>
/// The catalog builds the rows of a top category at the first use. It builds them again only when a list that the
/// category reads changes, or when the prop groups change (section 5.3). A drag in the Scene changes W in each frame,
/// but it changes no list of the catalog, so the rows stay.
/// </remarks>
public sealed class AssetCatalog
{
    /// <summary>The id of the root category, "All assets".</summary>
    public const string AllId = "all";

    /// <summary>The id part and the label of the child of the connection types of the solars.</summary>
    private const string ConnectionsPart = "connections";

    /// <summary>The end of the reason of a model that no solar archetype uses.</summary>
    private const string NoPropFromModel = "This version cannot make a prop from such a model.";

    private readonly EditSession session;
    private readonly Dictionary<string, Built> built = new(StringComparer.OrdinalIgnoreCase);

    internal AssetCatalog(EditSession session)
    {
        this.session = session;
        Users = new ModelUsers(session);
    }

    /// <summary>The sections that name each model file.</summary>
    internal ModelUsers Users { get; }

    private IReadOnlyList<AssetCategoryDefinition> Definitions => session.Lists.RuleData.AssetCategories;

    /// <summary>
    /// The categories in the order of the tree: the root "All assets", then each top category of the rule data, each
    /// followed by its children. Each category has the count of its rows and the rows of its children.
    /// </summary>
    public IReadOnlyList<AssetCategory> Categories
    {
        get
        {
            var tops = Definitions.Select(Top).ToList();
            var result = new List<AssetCategory>
            {
                new(AllId, "All assets", null, tops.Sum(t => t.Rows.Count)),
            };
            foreach (var top in tops)
                result.AddRange(top.Categories);
            return result;
        }
    }

    /// <summary>The rows of a category and of its children, in list order. The root gives the rows of all
    /// categories.</summary>
    public IReadOnlyList<AssetRow> Rows(string categoryId)
    {
        if (categoryId.Equals(AllId, StringComparison.OrdinalIgnoreCase))
            return Definitions.SelectMany(d => Top(d).Rows).ToList();
        var topId = categoryId.Split('/')[0];
        if (Definitions.FirstOrDefault(d => d.Id.Equals(topId, StringComparison.OrdinalIgnoreCase)) is not { } definition)
            return [];
        var rows = Top(definition).Rows;
        return topId.Length == categoryId.Length ? rows : rows.Where(r => InCategory(r, categoryId)).ToList();
    }

    /// <summary>
    /// The rows of a category and its children whose display name, nickname, detail or model file holds the text,
    /// without letter case (section 5.4). An empty text gives all rows of the category.
    /// </summary>
    /// <param name="displayName">The display name of a row, from the editor.</param>
    public IReadOnlyList<AssetRow> Search(string categoryId, string text, Func<AssetRow, string> displayName)
    {
        var rows = Rows(categoryId);
        var term = text.Trim();
        if (term.Length == 0)
            return rows;
        bool Holds(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
        return rows.Where(r => Holds(r.Nickname) || Holds(r.Detail) || Holds(r.ModelPath) || Holds(displayName(r))).ToList();
    }

    /// <summary>The row of an asset of a kind with an id, without letter case, or null. "Show in Assets" uses it.</summary>
    public AssetRow? Find(AssetKind kind, string id)
    {
        foreach (var definition in Definitions)
        {
            if (KindOf(definition) != kind)
                continue;
            var match = Top(definition).Rows.FirstOrDefault(r => r.Asset.Kind == kind &&
                                                                  string.Equals(r.Asset.Id, id, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>The kind of the entries of a list, or null when no category of the rule data shows the list.</summary>
    public AssetKind? KindOfList(string list) =>
        Definitions.FirstOrDefault(d => list.Equals(d.List, StringComparison.OrdinalIgnoreCase)) is { } definition
            ? KindOf(definition)
            : null;

    /// <summary>
    /// The model of an asset from W (section 5.5): the model of a solar archetype, a ship, an asteroid or equipment. A
    /// model file takes the lines of its first solar archetype, ship or asteroid (decision D280). Null for a model
    /// without such a user, and for the other kinds.
    /// </summary>
    public ModelSource? ModelOf(AssetRef asset) => asset.Kind switch
    {
        AssetKind.SolarArchetype or AssetKind.Ship or AssetKind.Asteroid or AssetKind.Equipment when asset.Section != null =>
            ModelSource.Of(session, asset.Section),
        AssetKind.Model => Users.Of(asset.Id).FirstOrDefault(u => u.Kind != AssetKind.Equipment) is { } user
            ? ModelSource.Of(session, user.Section)
            : null,
        _ => null,
    };

    /// <summary>The sections that name a model file in <c>DA_archetype</c>: solar archetypes, ships, asteroids and
    /// equipment, in that order.</summary>
    public IReadOnlyList<SectionRef> UsersOf(string modelPath) => Users.Of(modelPath).Select(u => u.Section).ToList();

    /// <summary>The rows and the categories of a top category, built again when what it reads changes.</summary>
    private Built Top(AssetCategoryDefinition definition)
    {
        var key = KeyOf(definition);
        if (built.TryGetValue(definition.Id, out var known) && known.Key.SequenceEqual(key))
            return known;
        known = Build(definition, key);
        built[definition.Id] = known;
        return known;
    }

    /// <summary>What the rows of a top category read: its list, the model users for the prop badge and the models, the
    /// version of the prop groups, and the revision of the session for the mission prefabs.</summary>
    private object?[] KeyOf(AssetCategoryDefinition definition)
    {
        if (definition.Kind == AssetCategoryDefinition.PropGroupsKind)
            return [PropGroups.Version(session)];
        if (definition.Kind == AssetCategoryDefinition.MissionPrefabsKind)
            return [session.Revision, session.IsDirty, session.ActiveMod];
        var list = session.Lists.List(definition.List!);
        return KindOf(definition) is AssetKind.Ship or AssetKind.Asteroid or AssetKind.Model
            ? [list, Users.State]
            : [list];
    }

    private Built Build(AssetCategoryDefinition definition, object?[] key)
    {
        var kind = KindOf(definition);
        var children = new List<(string Id, string Label)>();
        var rows = new List<AssetRow>();
        if (kind == AssetKind.PropGroup)
            rows.AddRange(PropGroups.Of(session).Select(g => GroupRow(definition, g)));
        else if (kind == AssetKind.MissionPrefab)
            rows.AddRange(session.Missions.Prefabs().Where(p => !p.Ref.Builtin).Select(p => PrefabRow(definition, p)));
        else if (kind != null && session.Lists.List(definition.List!) is { } list)
        {
            if (definition.Split == AssetCategoryDefinition.SplitTemplates)
            {
                children.AddRange(session.Lists.RuleData.SceneTemplates.Select(t => ($"{definition.Id}/{t.Id}", t.Label)));
                children.Add(($"{definition.Id}/{ConnectionsPart}", "Jump gates, holes and lanes"));
            }
            foreach (var entry in list.Entries)
                rows.Add(Row(definition, kind.Value, list.Id, entry, children));
        }

        var counts = rows.GroupBy(r => r.CategoryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var categories = new List<AssetCategory> { new(definition.Id, definition.Label, AllId, rows.Count) { Kind = kind } };
        if (definition.Split == AssetCategoryDefinition.SplitSection && session.Lists.RuleData.Lists
                .GetValueOrDefault(definition.List!)?.Source is { } source)
        {
            // One child for each section type with an entry, in the order of the list source.
            children.AddRange(source.Sections.Select(t => ($"{definition.Id}/{t.ToLowerInvariant()}", t))
                .Where(c => counts.ContainsKey(c.Item1)));
        }
        else if (definition.Split == AssetCategoryDefinition.SplitFolder)
        {
            children.AddRange(rows.GroupBy(r => r.CategoryId, StringComparer.OrdinalIgnoreCase)
                .Where(g => !g.Key.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                .Select(g => (g.Key, Label: FolderOf(g.First().Asset.Id)))
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase));
        }
        categories.AddRange(children.Select(c => new AssetCategory(c.Id, c.Label, definition.Id,
            counts.GetValueOrDefault(c.Id)) { Kind = kind }));
        return new Built(key, rows, categories);
    }

    /// <summary>The row of a list entry.</summary>
    private AssetRow Row(AssetCategoryDefinition definition, AssetKind kind, string list, ListEntry entry,
        List<(string Id, string Label)> children)
    {
        var working = entry.Section is { } section ? session.WorkingSection(section) : null;
        var model = Value(working, "DA_archetype");
        var category = definition.Id;
        string? notPlaceable = null;
        var detail = entry.Detail;
        var hasProp = false;
        var name = entry.Value;
        switch (kind)
        {
            case AssetKind.SolarArchetype:
                var type = Value(working, "type");
                detail = type ?? "";
                category = SolarCategory(definition, type?.ToLowerInvariant(), children, out notPlaceable);
                break;
            case AssetKind.Ship or AssetKind.Asteroid:
                detail = kind == AssetKind.Ship ? Value(working, "type") ?? "" : model ?? "";
                hasProp = model != null && Users.Of(model).Any(u => u.Kind == AssetKind.SolarArchetype);
                notPlaceable = model == null ? $"The {(kind == AssetKind.Ship ? "ship" : "asteroid")} has no model." : null;
                break;
            case AssetKind.Equipment:
                detail = entry.Section?.Id.Type ?? "";
                category = entry.Section is { } typed ? $"{definition.Id}/{typed.Id.Type.ToLowerInvariant()}" : category;
                notPlaceable = "Equipment goes on an object through its loadout. This version cannot edit loadouts.";
                break;
            case AssetKind.Effect:
                detail = Value(working, "effect_type") ?? "";
                notPlaceable = "An effect goes on an object through equipment in its loadout. This version cannot edit " +
                               "loadouts.";
                break;
            case AssetKind.Loadout:
                detail = Value(working, "archetype") ?? "";
                notPlaceable = "Drop the loadout on an object.";
                break;
            case AssetKind.Faction:
                detail = "";
                notPlaceable = "Drop the faction on an object.";
                break;
            case AssetKind.Model:
                model = entry.Value;
                name = entry.Name ?? entry.Value;
                detail = VfsPath.Parent(entry.Value.Replace('/', '\\'));
                // A file of the data folder itself has no folder, so it stays in the top category.
                category = FolderOf(entry.Value) is { Length: > 0 } folder
                    ? $"{definition.Id}/{folder.ToLowerInvariant()}"
                    : definition.Id;
                var users = Users.Of(entry.Value);
                hasProp = users.Any(u => u.Kind == AssetKind.SolarArchetype);
                notPlaceable = users.Count == 0
                    ? "No solar archetype, ship or asteroid uses this model, so the editor does not know its materials. " +
                      NoPropFromModel
                    : users.All(u => u.Kind == AssetKind.Equipment)
                        ? "Retail has no solar with an equipment model. " + NoPropFromModel
                        : null;
                break;
        }
        // An entry that the engine cannot use does not spawn (milestone M21).
        if (!entry.Usable)
            notPlaceable = entry.Problem ?? "The game cannot use this entry.";
        var asset = new AssetRef(kind, entry.Value, entry.Section, null);
        return new AssetRow(asset, name, detail, category, entry.Origin, entry.ModId, MarkOf(entry), notPlaceable)
        {
            List = list,
            ModelPath = model,
            HasProp = hasProp,
        };
    }

    /// <summary>The category of a solar archetype (decision D267): the child of the template whose types hold its type,
    /// the child of the connections, or "Other solar" with the reason that it cannot spawn.</summary>
    private string SolarCategory(AssetCategoryDefinition definition, string? type,
        List<(string Id, string Label)> children, out string? notPlaceable)
    {
        notPlaceable = null;
        var rules = session.Lists.RuleData;
        if (type != null && rules.SceneTemplates.FirstOrDefault(t => t.Types.Contains(type)) is { } template)
            return $"{definition.Id}/{template.Id}";
        if (type != null && rules.ConnectionTypes.Contains(type))
        {
            notPlaceable = ScenePlans.NoNewConnections;
            return $"{definition.Id}/{ConnectionsPart}";
        }
        notPlaceable = type == null
            ? "The archetype has no 'type' line, so it is in no Create template of the rule data."
            : ScenePlans.NoTemplateReason(type);
        // The child "Other solar" holds the archetypes of a type in no template (section 5.2).
        var other = $"{definition.Id}/solar";
        return children.Any(c => c.Id.Equals(other, StringComparison.OrdinalIgnoreCase))
            ? other
            : children.Count > 1 ? children[^2].Id : definition.Id;
    }

    /// <summary>The row of a prop group.</summary>
    private AssetRow GroupRow(AssetCategoryDefinition definition, PropGroup group)
    {
        var members = group.Members.Count == 1 ? "1 member" : $"{group.Members.Count} members";
        var problem = group.Problems.FirstOrDefault()?.Message;
        return new AssetRow(new AssetRef(AssetKind.PropGroup, group.Nickname, null, group.File), group.Nickname,
            group.ModId == null ? members : $"{members}, mod {group.ModId}", definition.Id,
            group.ModId == null ? ValueOrigin.ActiveMod : ValueOrigin.LowerMod, group.ModId ?? session.ActiveMod?.Id,
            AssetMark.None, problem == null ? null : $"The prop group file has a problem: {problem}");
    }

    /// <summary>
    /// The row of a mission prefab asset (section 8.3 of the M6b design): its file name, the number of its fields or the
    /// prefab that it copies, and the marks of a new or changed file of the active mod. The Scene does not place a
    /// prefab: a drag into the graph of a mission does.
    /// </summary>
    private AssetRow PrefabRow(AssetCategoryDefinition definition, Missions.Prefabs.PrefabAsset asset)
    {
        var template = asset.Template;
        var fields = template?.Fields.Count ?? 0;
        var detail = template == null ? "The file has a problem"
            : template.VariantOf is { } source ? $"Variant of {Missions.Editing.PrefabRef.TryParse(source)?.Id ?? source}"
            : fields == 1 ? "1 field"
            : $"{fields.ToString(System.Globalization.CultureInfo.InvariantCulture)} fields";
        var mark = AssetMark.None;
        if (asset.Active)
        {
            var path = Missions.Prefabs.PrefabAsset.PathOf(asset.Ref.Id);
            string? saved;
            try
            {
                saved = session.ModFileSavedText(path);
            }
            catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                saved = null;
            }

            mark = saved == null ? AssetMark.Added | AssetMark.Unsaved
                : !string.Equals(saved, asset.Text, StringComparison.Ordinal) ? AssetMark.Changed | AssetMark.Unsaved
                : AssetMark.None;
        }

        var problem = asset.Problems.FirstOrDefault()?.ToString();
        return new AssetRow(new AssetRef(AssetKind.MissionPrefab, asset.Ref.Id, null, Missions.Prefabs.PrefabAsset.PathOf(asset.Ref.Id)),
            asset.Ref.Id, detail, definition.Id, asset.Active ? ValueOrigin.ActiveMod : ValueOrigin.LowerMod, asset.ModId, mark,
            problem == null
                ? "Drag the prefab into the graph of a mission. The Scene does not place it."
                : $"The prefab file has a problem: {problem}")
        {
            HasProblem = problem != null,
        };
    }

    /// <summary>The marks of an entry whose section the active mod adds or changes (decision D245). An entry of a
    /// whole file of the active mod has no section in W, so it gets the mark of a change.</summary>
    private AssetMark MarkOf(ListEntry entry)
    {
        var mark = entry.Section is { } section && session.ChangeState(section) is { } state
            ? state.IsNew ? AssetMark.Added : state.IsChanged ? AssetMark.Changed : AssetMark.None
            : entry.Origin == ValueOrigin.ActiveMod ? AssetMark.Changed : AssetMark.None;
        return entry.Unsaved ? mark | AssetMark.Unsaved : mark;
    }

    private static bool InCategory(AssetRow row, string categoryId) =>
        row.CategoryId.Equals(categoryId, StringComparison.OrdinalIgnoreCase) ||
        row.CategoryId.StartsWith(categoryId + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first folder of a path below the data folder, or the path for a file in the data folder.</summary>
    private static string FolderOf(string path)
    {
        var at = path.IndexOfAny(['\\', '/']);
        return at < 0 ? "" : path[..at];
    }

    /// <summary>The first value of the last line of a key with a value, trimmed, or null.</summary>
    private static string? Value(Section? section, string key) =>
        section?.LastOrDefault(e => SceneSections.Is(e.Name, key) && e.Count > 0)?[0].ToString().Trim() is { Length: > 0 } value
            ? value
            : null;

    private static AssetKind? KindOf(AssetCategoryDefinition definition) =>
        definition.Kind == AssetCategoryDefinition.PropGroupsKind ? AssetKind.PropGroup
        : definition.Kind == AssetCategoryDefinition.MissionPrefabsKind ? AssetKind.MissionPrefab
        : definition.List?.ToLowerInvariant() switch
            {
                "solar_archetype" => AssetKind.SolarArchetype,
                "ship" => AssetKind.Ship,
                "asteroid" => AssetKind.Asteroid,
                "equipment" => AssetKind.Equipment,
                "effect" => AssetKind.Effect,
                "loadout" => AssetKind.Loadout,
                "faction" => AssetKind.Faction,
                "model" => AssetKind.Model,
                _ => null,
            };

    /// <param name="Key">What the rows read when they were built.</param>
    /// <param name="Categories">The top category, then its children.</param>
    private sealed record Built(object?[] Key, IReadOnlyList<AssetRow> Rows, IReadOnlyList<AssetCategory> Categories);
}
