using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The layer that gives a cutscene script (tool <c>list_cutscenes</c>).</summary>
internal enum CutsceneLayer
{
    /// <summary>The base data gives the script, and no mod changes it.</summary>
    Base,

    /// <summary>A lower mod changes the script.</summary>
    LowerMod,

    /// <summary>The active mod changes the script.</summary>
    ActiveMod,

    /// <summary>The active mod adds the script.</summary>
    New,
}

/// <summary>A cutscene script of the edit view (tool <c>list_cutscenes</c>).</summary>
/// <param name="Ref">The <c>thn:</c> reference.</param>
/// <param name="Category">The folder group of the Cutscenes tab, for example <c>bases</c> or <c>mods</c>.</param>
/// <param name="Duration">The duration in seconds, or null when the editor cannot read the script.</param>
/// <param name="Unsaved">True when the session has an edit of the script that it did not save.</param>
internal sealed record CutsceneRow(string Ref, string Path, CutsceneFolder Category, CutsceneLayer Kind, float? Duration,
    bool Unsaved);

/// <summary>An entity or an event of a cutscene.</summary>
/// <param name="Ref">The <c>thn:</c> reference: <c>#entity/&lt;name&gt;</c>, or <c>#event/&lt;index&gt;</c>.</param>
/// <param name="Type">The THN name of the type, for example <c>CAMERA</c> or <c>START_SOUND</c>, or null.</param>
internal sealed record CutsceneElementView(string Ref, ThnElementKind Element, string? Type)
{
    /// <summary>The name of an entity, else null.</summary>
    public string? Name { get; init; }

    /// <summary>The template of an entity, else null.</summary>
    public string? Template { get; init; }

    /// <summary>The flags of an entity that are on, as <c>cutscene_set_flags</c> takes them. Null for an event, and for
    /// a flags value that the engine cannot read.</summary>
    public IReadOnlyList<string>? Flags { get; init; }

    /// <summary>The time of an event in seconds, else null.</summary>
    public float? Time { get; init; }

    /// <summary>The length of an event in seconds, or null when it has none.</summary>
    public float? Length { get; init; }

    /// <summary>The entity names that an event targets.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>The value of each key of the element that <c>set_fields</c> sets, by its THN path, in the JSON form of
    /// <see cref="ThnJson"/> (see <see cref="CutsceneQueries.Fields"/>).</summary>
    public IReadOnlyDictionary<string, JsonNode> Fields { get; init; } = new Dictionary<string, JsonNode>();

    /// <summary>Why the edit tools do not change this entity, or null. A later entity with the same name hides it, so
    /// its reference names the later entity, and its fields are empty.</summary>
    public string? ReadOnlyReason { get; init; }
}

/// <summary>A cutscene with its entities, its events and its problems (tool <c>get_cutscene</c>).</summary>
/// <param name="ReadOnlyReason">Why the editor cannot change the cutscene, or null.</param>
internal sealed record CutsceneView(string Ref, float Duration, string? ReadOnlyReason,
    IReadOnlyList<CutsceneElementView> Entities, IReadOnlyList<CutsceneElementView> Events, IReadOnlyList<Problem> Problems);

/// <summary>The cutscene query tools of section 7.1 of the MCP1 design.</summary>
internal static class CutsceneQueries
{
    /// <summary>The install paths of the cutscene scripts of each edit view. The edit view changes only with a save, so
    /// the scan of the files runs once for each one.</summary>
    private static readonly ConditionalWeakTable<ModOverlay, IReadOnlyList<string>> Paths = new();

    public static ToolStep ListCutscenes(ToolCall call)
    {
        var session = call.Session;
        var category = call.Args.Text("category");
        var text = call.Args.Text("text");
        var files = Paths.GetValue(session.EditView,
            view => VfsFiles.Find(view.CreateFileSystem(), ".thn").Select(p => VfsPath.Normalize(p) ?? p).ToList());
        var rows = files.Concat(session.NewCutscenes).Distinct(VfsPath.Comparer)
            .Where(p => category == null || AgentTools.Snake(CutsceneCatalog.FolderOf(p).ToString()) == category)
            .Where(p => ToolCall.Matches(text, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ToolResult.Of(call.Paged(rows, path => new CutsceneRow(Ref(path), path, CutsceneCatalog.FolderOf(path),
            Kind(session.CutsceneSource(path)), session.Cutscene(path)?.Duration, session.HasUnsavedCutsceneEdits(path))));
    }

    public static ToolStep GetCutscene(ToolCall call)
    {
        var session = call.Session;
        var reference = ItemRefs.Parse(call.Args.Text("ref")!);
        var path = ItemRefs.ResolveCutscene(session, reference);
        var model = session.Cutscene(path)!;
        return ToolResult.Of(new CutsceneView(Ref(path), model.Duration, session.CutsceneReadOnlyReason(path),
            model.Entities.Select(e => Element(session, new ThnRef(path, ThnElementKind.Entity, e.Id))).ToList(),
            model.Events.Select(e => Element(session, new ThnRef(path, ThnElementKind.Event, e.Id))).ToList(),
            session.CutsceneErrors(path).Select(d => AgentRows.ProblemOf(session, d) with { Ref = Ref(path) }).ToList()));
    }

    /// <summary>The view of an entity or an event of W of a cutscene.</summary>
    internal static CutsceneElementView Element(EditSession session, ThnRef element)
    {
        var model = session.Cutscene(element.Path)!;
        var table = model.Element(element.Kind, element.Id)!.Table;
        var type = ThnSchema.TypeOf(element.Kind, table, session.ThnSymbols);
        var reference = ItemRefs.Of(session, element);
        var symbols = session.ThnSymbols;
        if (element.Kind == ThnElementKind.Entity)
        {
            var name = ThnScriptModel.NameOf(table);
            // The reference of an entity names the entity that the engine uses for the name. When a later entity has
            // the same name, set_fields with the fields of this entity would change the later entity. So the view
            // gives no fields for it, and tells why.
            var hidden = name != null && model.EntityNamed(name)?.Id != element.Id;
            return new CutsceneElementView(reference, ThnElementKind.Entity, type)
            {
                Name = name,
                Template = (table[TemplateKey] as ThnText)?.Value,
                Flags = FlagsOf(table, symbols),
                Fields = hidden ? new Dictionary<string, JsonNode>() : Fields(session, ThnElementKind.Entity, type, table),
                ReadOnlyReason = hidden ? AgentTexts.Error("cutscene_entity_hidden") : null,
            };
        }
        var props = table.List.Length > 3 ? table.List[3] as ThnTable : null;
        return new CutsceneElementView(reference, ThnElementKind.Event, type)
        {
            Time = table.List.Length > 0 && table.List[0] is ThnNumber time ? time.Value : null,
            Length = props?["duration"] is ThnNumber length ? length.Value : null,
            Targets = table.List.Length > 2 ? Targets(table.List[2]) : [],
            Fields = Fields(session, ThnElementKind.Event, type, table),
        };
    }

    /// <summary>The key of the template of an entity.</summary>
    private const string TemplateKey = "template_name";

    /// <summary>The keys of an entity that the view gives as own properties, not in its fields.</summary>
    private static readonly string[] OwnKeys = [ThnScriptModel.EntityNameKey, "type", TemplateKey, ThnFlags.Key];

    /// <summary>
    /// The value of each key of an element that <c>set_fields</c> sets, by its THN path, in the JSON form of
    /// <see cref="ThnJson"/>. Each value goes back into <c>set_fields</c> without a change. A record gives the keys in
    /// it, at each depth, but a curve stays one value. An entity leaves out its name, its type, its template and its
    /// flags, which the view gives as own properties. An event gives the keys of its props. Its time, type and targets
    /// are own properties.
    /// </summary>
    /// <remarks>A key that a path cannot name, for example a key with a '.', is not in the fields. No retail key has
    /// one.</remarks>
    internal static IReadOnlyDictionary<string, JsonNode> Fields(EditSession session, ThnElementKind kind, string? type,
        ThnTable element)
    {
        var fields = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        void Add(string prefix, ThnTable record)
        {
            foreach (var (key, value) in record.Record)
            {
                var text = prefix.Length == 0 ? key : prefix + ThnPath.Separator + key;
                if (ThnPath.Problem(text) != null || key.Contains(ThnPath.Separator) || prefix.Length == 0 &&
                    kind == ThnElementKind.Entity && OwnKeys.Contains(key, StringComparer.Ordinal))
                    continue;
                var row = ThnSchema.Find(kind, type, new ThnPath(text));
                if (value is ThnTable { IsRecord: true } inner && row?.Kind != ThnKeyKind.Curve)
                    Add(text, inner);
                else
                    fields[text] = ThnJson.Write(row, value, session.ThnSymbols, session.ThnEvaluator);
            }
        }

        if (kind == ThnElementKind.Entity)
            Add("", element);
        else if (element.List.Length > 3 && element.List[3] is ThnTable { IsRecord: true } props)
            Add("props", props);
        return fields;
    }

    /// <summary>The flags of an entity that are on, in the order of the Inspector, or null when the engine cannot read
    /// the value.</summary>
    private static IReadOnlyList<string>? FlagsOf(ThnTable entity, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var sound = ThnFlags.IsSound(entity, symbols);
        try
        {
            var on = ThnFlags.On(entity[ThnFlags.Key], sound, symbols);
            return ThnFlags.Shown(sound).Where(on.Contains).ToList();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The <c>thn:</c> reference of a cutscene script.</summary>
    internal static string Ref(string path) => ItemRefs.Format(new ItemReference(ItemRefKind.Cutscene, "") { Path = path });

    private static IReadOnlyList<string> Targets(ThnValue value) => value switch
    {
        ThnText text => [text.Value],
        ThnTable table => table.List.OfType<ThnText>().Select(t => t.Value).ToList(),
        _ => [],
    };

    private static CutsceneLayer Kind(ThnFileSource source) => source.Kind switch
    {
        ThnFileKind.NewHere => CutsceneLayer.New,
        ThnFileKind.PatchedHere or ThnFileKind.WholeFileHere => CutsceneLayer.ActiveMod,
        _ => source.ChangedByLowerMod ? CutsceneLayer.LowerMod : CutsceneLayer.Base,
    };
}
