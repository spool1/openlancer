using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The lines of one key of a section (tools <c>get_item</c> and <c>get_system</c>).</summary>
/// <param name="Lines">The values of each line of the key, in file order.</param>
/// <param name="Label">The plain label of the list that the key names, or null.</param>
/// <param name="Origin">The layer that gives the lines of the key, and if they have an edit that the session did not
/// save.</param>
/// <param name="Locked">True when <c>set_fields</c> cannot change the key (see <see cref="FieldCommands.IsLocked"/>).
/// Examples are the key line, a key of a group of lines that belong together and the file of a system.</param>
internal sealed record FieldRow(string Key, IReadOnlyList<JsonArray> Lines, string? Label, ItemOrigin Origin, bool Locked,
    IReadOnlyList<Problem> Problems);

/// <summary>A component of an object and its problems.</summary>
internal sealed record ComponentRow(ObjectComponent Kind, string Name, IReadOnlyList<Problem> Problems);

/// <summary>A line of an INI file that names an item.</summary>
/// <param name="Ref">The canonical reference of the section of the line.</param>
/// <param name="KeyLine">The position of the line among the lines of the key in the section, from 0.</param>
/// <param name="Value">The index of the value in the line, from 0.</param>
/// <param name="List">The data list of the value.</param>
internal sealed record ReferenceRow(string Ref, string Key, int KeyLine, int Value, string List);

/// <summary>The text mentions of a nickname in files that the lists do not read.</summary>
/// <param name="Pending">True while the editor builds the text index. Call the tool again in a few seconds.</param>
internal sealed record MentionsView(bool Pending, IReadOnlyList<StoryMention> Files);

/// <summary>A section of an INI file of the edit view (tool <c>get_item</c>).</summary>
/// <param name="File">The install path of the file.</param>
/// <param name="Name">The name that the game shows, or an empty text.</param>
internal sealed record SectionItemView(string Ref, string Type, string File, string Name, ItemOrigin Origin)
{
    /// <summary>Why the editor cannot change the section, or null.</summary>
    public string? ReadOnlyReason { get; init; }

    public IReadOnlyList<FieldRow> Fields { get; init; } = [];

    public IReadOnlyList<ComponentRow> Components { get; init; } = [];

    /// <summary>The objects whose <c>parent</c> line names the object.</summary>
    public IReadOnlyList<string> Children { get; init; } = [];

    /// <summary>The first lines of the INI files that name the item, on request.</summary>
    public IReadOnlyList<ReferenceRow>? UsedBy { get; init; }

    /// <summary>The first lines of the mission scripts that name the item, on request.</summary>
    public IReadOnlyList<MissionReferenceView>? MissionUsedBy { get; init; }

    /// <summary>The number of lines of the INI files and of the mission scripts that name the item, on
    /// request.</summary>
    public int? UsedByTotal { get; init; }

    /// <summary>The text mentions of the nickname, on request.</summary>
    public MentionsView? Mentions { get; init; }

    /// <summary>The canonical INI text of the section, on request.</summary>
    public string? Raw { get; init; }

    /// <summary>The problems of the section that name no key.</summary>
    public IReadOnlyList<Problem> Problems { get; init; } = [];
}

/// <summary>A prop group (tool <c>list_prop_groups</c>).</summary>
/// <param name="Ref">The <c>group:</c> reference.</param>
/// <param name="Mod">The mod that has the group.</param>
/// <param name="Editable">True when the group is a group of the active mod.</param>
internal sealed record PropGroupRow(string Ref, string Nickname, string Name, string Mod, bool Editable, int Members,
    IReadOnlyList<string> Problems);

/// <summary>An asset of the Assets panel (tool <c>search_assets</c>).</summary>
/// <param name="Ref">The <c>asset:</c> reference.</param>
/// <param name="Marks">The marks of the tile.</param>
/// <param name="NotPlaceable">Why the Scene cannot place the asset, or null.</param>
internal sealed record AssetItemRow(string Ref, string Nickname, string Detail, string Category, ItemOrigin Origin,
    IReadOnlyList<AssetMark> Marks, string? NotPlaceable);

/// <summary>The kind of the item of <c>get_item</c>: it names the view that the result has.</summary>
internal enum ItemViewKind
{
    Section,
    MissionScript,
    Trigger,
    MissionItem,
    PrefabInstance,
    Prefab,
    CutsceneElement,
    PropGroup,
    Asset,
}

/// <summary>The result of <c>get_item</c>: the kind of the item and the view of that kind. Exactly one view is not
/// null.</summary>
internal sealed record ItemView(ItemViewKind Kind)
{
    public SectionItemView? Section { get; init; }

    public MissionOverview? MissionScript { get; init; }

    public MissionTriggerView? Trigger { get; init; }

    public MissionItemView? MissionItem { get; init; }

    public MissionInstanceView? PrefabInstance { get; init; }

    public MissionPrefabView? Prefab { get; init; }

    public CutsceneElementView? CutsceneElement { get; init; }

    public PropGroupRow? PropGroup { get; init; }

    public AssetItemRow? Asset { get; init; }
}

/// <summary>The result of <c>find_references</c>: one page of the lines that name an item. The page counts the lines
/// of the INI files first, then the lines of the mission scripts.</summary>
/// <param name="Lines">The lines of the INI files of the page.</param>
/// <param name="MissionLines">The lines of the mission scripts of the page.</param>
/// <param name="Mentions">The text mentions of the nickname, or null for an item without a nickname.</param>
/// <param name="Total">The number of lines of all pages.</param>
/// <param name="NextCursor">The cursor of the next page, or null on the last page.</param>
internal sealed record ReferencesView(IReadOnlyList<ReferenceRow> Lines, IReadOnlyList<MissionReferenceView> MissionLines,
    MentionsView? Mentions, int Total, string? NextCursor);

/// <summary>A value of a data list (tool <c>list_values</c>).</summary>
/// <param name="Value">The name that a line writes.</param>
/// <param name="Label">The name that the game shows, or an empty text.</param>
/// <param name="Detail">The short text of the detail keys of the list.</param>
/// <param name="Ref">The reference of the section of the entry, or null for an enum value.</param>
/// <param name="Problem">Why the engine cannot use the entry, or null.</param>
internal sealed record ValueRow(string Value, string Label, string Detail, ItemOrigin Origin, string? Ref, string? Problem);

/// <summary>One line with problems of a raw text.</summary>
/// <param name="Line">The line in the text, from 1.</param>
/// <param name="BaseGame">True when each problem of the line is a problem of the base game.</param>
/// <param name="Note">True for a note without a problem, for example why the editor cannot change a section.</param>
internal sealed record RawLineMark(int Line, DiagnosticSeverity Severity, string Message, bool BaseGame, bool Note);

/// <summary>The canonical INI text of a file or of a section of W (tool <c>get_raw_ini</c>).</summary>
/// <param name="File">The install path of the file.</param>
/// <param name="Ref">The reference of the section, or null for the whole file.</param>
internal sealed record RawIniView(string File, string? Ref, string Text, IReadOnlyList<RawLineMark> Marks);

/// <summary>The query tools of the items of each kind, the data lists, the assets and the raw INI (section 7.1 of the
/// MCP1 design).</summary>
internal static class ItemQueries
{
    public static ToolStep GetItem(ToolCall call)
    {
        var session = call.Session;
        var text = call.Args.Text("ref")!;
        var reference = ItemRefs.Parse(text);
        var missions = session.Missions.Queries;
        switch (reference.Kind)
        {
            case ItemRefKind.Script:
                return ToolResult.Of(new ItemView(ItemViewKind.MissionScript) { MissionScript = missions.GetMission(ScriptRef.Parse(text)) });
            case ItemRefKind.Trigger:
                return ToolResult.Of(new ItemView(ItemViewKind.Trigger) { Trigger = missions.GetTrigger(TriggerRef.Parse(text)) });
            case ItemRefKind.Label:
                return ToolResult.Of(new ItemView(ItemViewKind.MissionItem) { MissionItem = missions.GetItem(LabelRef.Parse(text)) });
            case ItemRefKind.Instance:
                return ToolResult.Of(new ItemView(ItemViewKind.PrefabInstance) { PrefabInstance = missions.GetPrefabInstance(text) });
            case ItemRefKind.Prefab:
                return ToolResult.Of(new ItemView(ItemViewKind.Prefab) { Prefab = Prefab(missions, reference) });
            case ItemRefKind.Act:
                throw Bad(text, AgentTexts.Error("ref_act"));
            case ItemRefKind.Cutscene:
                throw Bad(text, AgentTexts.Error("ref_cutscene"));
            case ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent:
                return ToolResult.Of(new ItemView(ItemViewKind.CutsceneElement)
                {
                    CutsceneElement = CutsceneQueries.Element(session, ItemRefs.ResolveCutsceneElement(session, reference)),
                });
            case ItemRefKind.Group:
                return ToolResult.Of(new ItemView(ItemViewKind.PropGroup) { PropGroup = GroupRow(session, Group(session, reference)) });
            case ItemRefKind.Asset:
                return ToolResult.Of(new ItemView(ItemViewKind.Asset) { Asset = Asset(session, reference) });
        }

        var section = ItemRefs.ResolveSection(session, reference);
        if (session.Missions.IsMissionFile(section.Path))
        {
            var item = ItemRef.Parse(ItemRefs.Of(section));
            return section.Id.Type.Equals("Trigger", StringComparison.OrdinalIgnoreCase) && section.Id.Key is { } trigger
                ? ToolResult.Of(new ItemView(ItemViewKind.Trigger)
                {
                    Trigger = missions.GetTrigger(new TriggerRef(section.Path, trigger)),
                })
                : ToolResult.Of(new ItemView(ItemViewKind.MissionItem) { MissionItem = missions.GetItem(item) });
        }
        return ToolResult.Of(new ItemView(ItemViewKind.Section)
        {
            Section = SectionItem(call, reference, section, call.Args.Flag("include_raw"), call.Args.Flag("include_used_by")),
        });
    }

    public static ToolStep FindReferences(ToolCall call)
    {
        var session = call.Session;
        var refText = call.Args.Text("ref");
        var name = call.Args.Text("name");
        if (refText == null == (name == null))
            throw Bad(refText ?? name ?? "", AgentTexts.Error("ref_or_name"));
        if (name != null)
        {
            var lines = new List<ReferenceRow>();
            foreach (var list in session.Lists.RuleData.Lists.Values.Where(l => l.Source != null))
            {
                if (session.Lists.IsScoped(list.Id) || session.Lists.List(list.Id)?.Find(name) != null)
                    lines.AddRange(Rows(session, list.Id, session.Lists.ReferencesTo(list.Id, name)));
            }
            return ToolResult.Of(References(call, lines, MissionLinesOfName(session, name), Mentions(session, name)));
        }

        var reference = ItemRefs.Parse(refText!);
        if (reference.Kind is ItemRefKind.Trigger or ItemRefKind.Label ||
            reference.Kind is ItemRefKind.Section or ItemRefKind.Line && session.Missions.IsMissionFile(reference.Path!))
        {
            var script = reference.Kind == ItemRefKind.Line
                ? ItemRefs.Of(new SectionRef(reference.Path!, reference.Section!))
                : refText!;
            return ToolResult.Of(References(call, [], session.Missions.Queries.FindReferences(script), null));
        }
        var section = ItemRefs.ResolveSection(session, reference);
        var key = section.Id.Key ?? throw Bad(refText!, AgentTexts.Error("ref_no_key"));
        return ToolResult.Of(References(call, LinesNaming(session, section, key), MissionLinesOf(session, reference, refText!),
            Mentions(session, key)));
    }

    public static ToolStep ListValues(ToolCall call)
    {
        var session = call.Session;
        var id = call.Args.Text("list")!;
        // The argument takes the ids of the retail rule data. A test session can have other rule data.
        if (!session.Lists.RuleData.Lists.TryGetValue(id, out var definition))
        {
            throw ToolInput.Bad("list", AgentTexts.Error("list_unknown", ("list", id),
                ("lists", string.Join(", ", session.Lists.RuleData.Lists.Keys.Order(StringComparer.Ordinal)))));
        }
        var source = definition.Source;
        var system = call.Args.Text("system");
        var script = call.Args.Text("script");
        ListView? view;
        if (source is { Scoped: true, Role: null })
        {
            if (script != null)
                throw ToolInput.Bad("script", AgentTexts.Error("list_no_script", ("list", id)));
            var nickname = system ?? throw ToolInput.Bad("system", AgentTexts.Error("list_needs_system", ("list", id)));
            var path = SceneQueries.System(session, nickname).Path ?? throw new EditException(new EditError(
                EditErrorKind.NotFound, ItemRefs.NotFound, AgentTexts.Error("system_no_file", ("system", nickname))));
            view = session.Lists.List(id, path);
        }
        else if (source is { Scoped: true })
        {
            if (system != null)
                throw ToolInput.Bad("system", AgentTexts.Error("list_no_system", ("list", id)));
            var text = script ?? throw ToolInput.Bad("script", AgentTexts.Error("list_needs_script", ("list", id)));
            var path = ScriptRef.Parse(text).Path;
            if (!session.Missions.IsMissionFile(path) || session.OpenDocument(path) is not { State.Exists: true })
            {
                throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                    AgentTexts.Error("script_none", ("script", text))) { Field = "script" });
            }
            view = session.Lists.List(id, path);
        }
        else
        {
            if (system != null)
                throw ToolInput.Bad("system", AgentTexts.Error("list_no_system", ("list", id)));
            if (script != null)
                throw ToolInput.Bad("script", AgentTexts.Error("list_no_script", ("list", id)));
            view = session.Lists.List(id);
        }
        var filter = call.Args.Text("text");
        var rows = (view?.Entries ?? [])
            .Select(e => (Entry: e, Label: e.Name ?? (e.Section is { } s ? call.Texts.NameOf(s) : "")))
            .Where(r => ToolCall.Matches(filter, r.Entry.Value, r.Label, r.Entry.Detail))
            .ToList();
        return ToolResult.Of(call.Paged(rows, r => new ValueRow(r.Entry.Value, r.Label, r.Entry.Detail,
            new ItemOrigin(r.Entry.Origin, r.Entry.ModId, r.Entry.Unsaved),
            r.Entry.Section is { } s ? ItemRefs.Canonical(session, s) : null, r.Entry.Problem)));
    }

    public static ToolStep SearchAssets(ToolCall call)
    {
        var session = call.Session;
        var catalog = session.Assets;
        var category = call.Args.Text("category") ?? AssetCatalog.AllId;
        if (catalog.Categories.All(c => !c.Id.Equals(category, StringComparison.OrdinalIgnoreCase)))
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, "UnknownCategory",
                AgentTexts.Error("asset_category", ("category", category),
                    ("categories", string.Join(", ", catalog.Categories.Select(c => c.Id))))) { Field = "category" });
        }
        var kind = call.Args.Text("kind");
        var rows = catalog.Search(category, call.Args.Text("text") ?? "", r => r.Nickname)
            .Where(r => kind == null || AgentTools.Snake(r.Asset.Kind.ToString()) == kind)
            .ToList();
        return ToolResult.Of(call.Paged(rows, AssetRow));
    }

    public static ToolStep ListPropGroups(ToolCall call)
    {
        var session = call.Session;
        var text = call.Args.Text("text");
        return ToolResult.Of(new PropGroupsView(Groups(session)
            .Where(g => ToolCall.Matches(text, g.Nickname, g.Name))
            .Select(g => GroupRow(session, g)).ToList()));
    }

    public static ToolStep GetRawIni(ToolCall call)
    {
        var session = call.Session;
        var refText = call.Args.Text("ref");
        var file = call.Args.Text("file");
        if (refText == null == (file == null))
            throw Bad(refText ?? file ?? "", AgentTexts.Error("ref_or_file"));
        IReadOnlyList<SectionRef> sections;
        string path;
        SectionRef? one = null;
        if (file != null)
        {
            var normal = InstallPaths.Parse(file);
            path = session.OpenDocument(normal)?.Path ?? throw new EditException(new EditError(EditErrorKind.NotFound,
                ItemRefs.NotFound, AgentTexts.Error("file_none", ("file", normal))) { Field = "file" });
            sections = session.Sections(path);
        }
        else
        {
            one = ItemRefs.ResolveSection(session, ItemRefs.Parse(refText!));
            path = one.Path;
            sections = [one];
        }
        var document = session.OpenDocument(path)!;
        var raw = RawText.Canonical(path, sections.Select(s => document.Working[document.IndexOf(s.Id)]).ToList(),
            ids: sections.Select(s => s.Id).ToList());
        var diagnostics = session.Missions.IsMissionFile(path)
            ? session.Missions.Model(path).Problems
            : sections.SelectMany(s => AgentRows.DiagnosticsOf(session, s));
        var marks = IniLint.Marks(raw, diagnostics.Where(d => one == null || one.Equals(d.Target)), showBaseGame: false);
        return ToolResult.Of(new RawIniView(path, one == null ? null : ItemRefs.Canonical(session, one), raw.Text,
            marks.Select(m => new RawLineMark(m.Line + 1, m.Severity, m.Message, m.BaseGame, m.Note)).ToList()));
    }

    /// <summary>The fields of a section: the lines of each key with its label, its layer, its unsaved mark, its lock
    /// and its problems.</summary>
    /// <param name="diagnostics">The diagnostics of the section (see <see cref="AgentRows.DiagnosticsOf"/>).</param>
    internal static IReadOnlyList<FieldRow> Fields(EditSession session, SectionRef section, SectionView view,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        var saved = session.SavedSection(section);
        return view.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var (origin, mod) = session.Origin(section, g.Key);
            var label = session.Lists.Rules(section, g.Key).SelectMany(r => r.Lists)
                .Select(l => session.Lists.RuleData.Lists.TryGetValue(l, out var d) ? d.Label : null).FirstOrDefault(l => l != null);
            var unsaved = saved == null || !SameLines(saved, view, g.Key);
            return new FieldRow(g.Key, g.Select(e => AgentRows.Values(e.Values)).ToList(), label,
                new ItemOrigin(origin, mod, unsaved),
                FieldCommands.IsLocked(session, section, view, g.Key),
                diagnostics.Where(d => g.Key.Equals(d.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(d => AgentRows.ProblemOf(session, d)).ToList());
        }).ToList();
    }

    /// <summary>The view of a section of an INI file that is not a mission file.</summary>
    private static SectionItemView SectionItem(ToolCall call, ItemReference reference, SectionRef section, bool raw,
        bool usedBy)
    {
        var session = call.Session;
        var view = session.Get(section)!;
        var diagnostics = AgentRows.DiagnosticsOf(session, section);
        var isObject = SceneSections.KindOf(session, section) == SceneKind.Object;
        var nickname = section.Id.Key;
        var item = new SectionItemView(ItemRefs.Canonical(session, section), view.Type, section.Path,
            call.Texts.TextOf(section, view.Type.Equals("system", StringComparison.OrdinalIgnoreCase) ? "strid_name" : "ids_name"),
            AgentRows.OriginOf(session, section, view, FieldCommands.KeyLine(session, section, view)))
        {
            ReadOnlyReason = view.ReadOnlyReason,
            Fields = Fields(session, section, view, diagnostics),
            Components = isObject
                ? ObjectComponents.Of(session, section).Select(c => new ComponentRow(c.Kind, ObjectComponents.Name(c.Kind),
                    c.Problems.Select(p => AgentRows.ProblemOf(session, p)).ToList())).ToList()
                : [],
            Children = isObject && nickname != null
                ? session.Sections(section.Path)
                    .Where(s => s.Id.Type.Equals(SceneSections.ObjectType, StringComparison.OrdinalIgnoreCase) &&
                                session.Get(s) is { } child &&
                                string.Equals(SceneSections.Last(child, "parent"), nickname, StringComparison.OrdinalIgnoreCase))
                    .Select(s => ItemRefs.Canonical(session, s)).ToList()
                : [],
            Raw = raw ? RawSectionEdit.Format(view) : null,
            Problems = diagnostics.Where(d => d.Key == null).Select(d => AgentRows.ProblemOf(session, d)).ToList(),
        };
        if (!usedBy || nickname == null)
            return item;
        // A popular item has thousands of users, so the view gives the first rows only. find_references pages them all.
        var lines = LinesNaming(session, section, nickname);
        var missionLines = MissionLinesOf(session, reference, ItemRefs.Canonical(session, section));
        var rows = call.Limits.ListRows;
        return item with
        {
            UsedBy = lines.Take(rows).ToList(),
            MissionUsedBy = missionLines.Take(rows).ToList(),
            UsedByTotal = lines.Count + missionLines.Count,
            Mentions = Mentions(session, nickname),
        };
    }

    /// <summary>True when a key has the same lines in two sections.</summary>
    private static bool SameLines(Section saved, SectionView view, string key)
    {
        var before = saved.Where(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(e => string.Join(",", e.Select(v => v.ToString()))).ToList();
        var now = view.Lines(key).Select(e => string.Join(",", e.Values.Select(v => v.ToString()))).ToList();
        return before.SequenceEqual(now, StringComparer.Ordinal);
    }

    /// <summary>The lines of the INI files that name a section by its key, through each list whose entries the section
    /// gives.</summary>
    private static List<ReferenceRow> LinesNaming(EditSession session, SectionRef section, string key) =>
        session.Lists.SourcesOf(section.Path, section.Id.Type).SelectMany(l => Rows(session, l.Id,
            session.Lists.ReferencesTo(l.Id, key, session.Lists.IsScoped(l.Id) ? section.Path : null))).ToList();

    private static IEnumerable<ReferenceRow> Rows(EditSession session, string list, IEnumerable<ListReference> references) =>
        references.Select(r => new ReferenceRow(ItemRefs.Canonical(session, r.Section), r.Key, r.Line, r.Index, list));

    /// <summary>One page of the lines that name an item: the lines of the INI files first, then the mission
    /// lines.</summary>
    private static ReferencesView References(ToolCall call, IReadOnlyList<ReferenceRow> lines,
        IReadOnlyList<MissionReferenceView> missionLines, MentionsView? mentions)
    {
        var all = lines.Select(l => (Ini: (ReferenceRow?)l, Mission: (MissionReferenceView?)null))
            .Concat(missionLines.Select(m => (Ini: (ReferenceRow?)null, Mission: (MissionReferenceView?)m))).ToList();
        var page = call.Paged(all, r => r);
        return new ReferencesView(page.Rows.Select(r => r.Ini).OfType<ReferenceRow>().ToList(),
            page.Rows.Select(r => r.Mission).OfType<MissionReferenceView>().ToList(), mentions, page.Total, page.NextCursor);
    }

    /// <summary>The text mentions of a nickname. While the text index builds, the result says so and the tool does not
    /// wait for it, because the editor runs the call between two frames.</summary>
    private static MentionsView Mentions(EditSession session, string nickname)
    {
        if (!session.StoryMentionsReady)
        {
            session.StartStoryMentions();
            return new MentionsView(true, []);
        }
        return new MentionsView(false, session.StoryMentions(nickname));
    }

    /// <summary>The mission lines that name an item of the game data, or none when no mission line can name it. A
    /// mission line names an object, a system, a base or an entry of some data lists.</summary>
    private static IReadOnlyList<MissionReferenceView> MissionLinesOf(EditSession session, ItemReference reference,
        string text)
    {
        if (reference.Kind is not (ItemRefKind.Object or ItemRefKind.System or ItemRefKind.Base or ItemRefKind.ListEntry))
            return [];
        try
        {
            return session.Missions.Queries.FindReferences(text);
        }
        catch (EditException e) when (e.Error.Kind == EditErrorKind.InvalidArgument)
        {
            return [];
        }
    }

    /// <summary>
    /// The mission lines that name a name: as a system object, and as a system, a base or an entry of each data list
    /// that mission lines name. A line value counts once. Each search reads all mission scripts, so the method searches
    /// twice and not once for each kind: once for the object kinds, whose search skips a name of an item of the script,
    /// and once for all other kinds.
    /// </summary>
    private static IReadOnlyList<MissionReferenceView> MissionLinesOfName(EditSession session, string name)
    {
        var references = session.Missions.References;
        IReadOnlyCollection<MissionValueKind> world =
        [
            .. session.Lists.RuleData.Lists.Keys.Select(EditSession.MissionKindOfList).OfType<MissionValueKind>()
                .Concat([MissionValueKind.System, MissionValueKind.Base]).Distinct(),
        ];
        var seen = new HashSet<(SectionRef, int, int)>();
        return references.Uses(MissionReferences.ObjectKinds, name).Concat(references.Uses(world, name))
            .Where(u => seen.Add((u.Section, u.Row, u.Value)))
            .Select(u => new MissionReferenceView(new RowRef(new ItemRef(u.Section.Path, u.Section.Id), u.Row).Reference,
                u.Key, u.Value))
            .ToList();
    }

    private static MissionPrefabView Prefab(MissionQueries missions, ItemReference reference)
    {
        var text = reference.ToString();
        return missions.ListPrefabs().FirstOrDefault(p => p.Ref.Equals(text, StringComparison.OrdinalIgnoreCase)) ??
               throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                   AgentTexts.Error("prefab_none", ("ref", text))) { Target = text });
    }

    /// <summary>The prop group of a <c>group:</c> reference. A reference without a mod names a group of the active mod,
    /// and <c>group:&lt;mod id&gt;/&lt;nickname&gt;</c> names a group of a lower mod.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/> with the closest names.</exception>
    internal static PropGroup Group(EditSession session, ItemReference reference)
    {
        var groups = Groups(session);
        return groups.FirstOrDefault(g => g.Nickname.Equals(reference.Name, StringComparison.OrdinalIgnoreCase) &&
                                          (reference.Scope == null ? g.Editable
                                              : string.Equals(g.ModId, reference.Scope, StringComparison.OrdinalIgnoreCase))) ??
               throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                   AgentTexts.Error("group_none", ("ref", reference)) +
                   ItemRefs.Hint(reference.Name, groups.Select(g => g.Nickname))) { Target = reference.ToString() });
    }

    /// <summary>
    /// The prop groups after a new read of their folders when a file changed on disk. The editor reads the folders again
    /// when its window gets the focus, as Unity refreshes its assets. An agent works while the editor has no focus, and
    /// the host without a window has no focus. So each tool reads the folders again before it reads the groups (review
    /// finding of MCP1-2, step 4).
    /// </summary>
    private static IReadOnlyList<PropGroup> Groups(EditSession session)
    {
        PropGroups.Refresh(session);
        return PropGroups.Of(session);
    }

    /// <summary>The <c>group:</c> reference of a prop group. A group of the active mod has its nickname. A group of a
    /// lower mod has the mod id and the nickname.</summary>
    internal static string GroupRef(PropGroup group) =>
        ItemRefs.Format(new ItemReference(ItemRefKind.Group, group.Nickname) { Scope = group.Editable ? null : group.ModId });

    private static PropGroupRow GroupRow(EditSession session, PropGroup group) => new(
        GroupRef(group),
        group.Nickname, group.Name, group.ModId ?? session.ActiveMod?.Id ?? "", group.Editable, group.Members.Count,
        group.Problems.Select(p => p.Message).ToList());

    private static AssetItemRow Asset(EditSession session, ItemReference reference) => AssetRow(AssetOf(session, reference));

    /// <summary>The asset of the Assets panel that an <c>asset:</c> reference names.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>.</exception>
    internal static AssetRow AssetOf(EditSession session, ItemReference reference)
    {
        var kind = Enum.GetValues<AssetKind>().Cast<AssetKind?>()
            .FirstOrDefault(k => AgentTools.Snake(k!.Value.ToString()) == reference.Scope?.ToLowerInvariant());
        return (kind is { } k ? session.Assets.Find(k, reference.Name) : null) ??
               throw new EditException(new EditError(EditErrorKind.NotFound, ItemRefs.NotFound,
                   AgentTexts.Error("asset_none", ("ref", reference))) { Target = reference.ToString() });
    }

    private static AssetItemRow AssetRow(AssetRow row) => new(
        ItemRefs.Format(new ItemReference(ItemRefKind.Asset, row.Asset.Id) { Scope = AgentTools.Snake(row.Asset.Kind.ToString()) }),
        row.Nickname, row.Detail, row.CategoryId,
        new ItemOrigin(row.Origin, row.ModId, row.Mark.HasFlag(AssetMark.Unsaved)),
        Enum.GetValues<AssetMark>().Where(m => m != AssetMark.None && row.Mark.HasFlag(m)).ToList(),
        row.NotPlaceable);

    private static EditException Bad(string target, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference, message) { Target = target });
}

/// <summary>The result of <c>list_prop_groups</c>.</summary>
internal sealed record PropGroupsView(IReadOnlyList<PropGroupRow> Groups);
