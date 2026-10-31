using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Hierarchy;

/// <summary>The mark of a row of the tree of a mission: the glyph of its worst problem that shows (section 9 of the M6a
/// design).</summary>
internal enum RowProblem
{
    None,

    /// <summary>A base-game problem that shows while the base-game messages show: the muted lock glyph.</summary>
    Muted,

    Warning,
    Error,
}

/// <summary>The tree of a mission in the Hierarchy (section 9 of the M6a design).</summary>
internal sealed partial class HierarchyModel
{
    /// <summary>The kinds of the rows of a mission. They have the text color, as the cutscene list has, so they do not
    /// look like the kinds of the Scene.</summary>
    internal static class MissionKinds
    {
        public static readonly ObjectKind Mission = new("Mission", EditorIcons.Route, EditorTheme.Text);
        public static readonly ObjectKind Group = new("Group", EditorIcons.Folder, EditorTheme.Text);
        public static readonly ObjectKind Act = new("Act", EditorIcons.Film, EditorTheme.Text);
        public static readonly ObjectKind Trigger = new("Trigger", EditorIcons.Bolt, EditorTheme.Text);
        public static readonly ObjectKind Prefab = new("Behavior prefab", EditorIcons.Cubes, EditorTheme.Text);
        public static readonly ObjectKind Ship = new("Mission ship", EditorIcons.SpaceShuttle, EditorTheme.Text);
        public static readonly ObjectKind Formation = new("Formation", EditorIcons.Users, EditorTheme.Text);
        public static readonly ObjectKind Solar = new("Solar", EditorIcons.Satellite, EditorTheme.Text);
        public static readonly ObjectKind Loot = new("Loot", EditorIcons.Box, EditorTheme.Text);
        public static readonly ObjectKind Npc = new("NPC", EditorIcons.User, EditorTheme.Text);
        public static readonly ObjectKind Label = new("Label", EditorIcons.Tag, EditorTheme.Text);
        public static readonly ObjectKind Orders = new("Order list", EditorIcons.List, EditorTheme.Text);
        public static readonly ObjectKind Objective = new("Objective", EditorIcons.Bullseye, EditorTheme.Text);
        public static readonly ObjectKind Conversation = new("Conversation", EditorIcons.Comments, EditorTheme.Text);
        public static readonly ObjectKind Link = new("Link", EditorIcons.Link, EditorTheme.Text);
        public static readonly ObjectKind Encounter = new("Random encounter", EditorIcons.Crosshairs, EditorTheme.Text);
        public static readonly ObjectKind Other = new("Mission section", EditorIcons.Th, EditorTheme.Text);

        /// <summary>The kind of a section of the script.</summary>
        public static ObjectKind Of(MissionSectionKind kind) => kind switch
        {
            MissionSectionKind.Trigger => Trigger,
            MissionSectionKind.Ship => Ship,
            MissionSectionKind.Formation => Formation,
            MissionSectionKind.Solar => Solar,
            MissionSectionKind.Loot => Loot,
            MissionSectionKind.Npc => Npc,
            MissionSectionKind.OrderList => Orders,
            MissionSectionKind.Objective => Objective,
            MissionSectionKind.Conversation => Conversation,
            MissionSectionKind.RandomEncounter => Encounter,
            _ => Other,
        };
    }

    /// <summary>The types that a "t:" word selects in the tree of a mission.</summary>
    internal static readonly IReadOnlyDictionary<string, Func<ObjectKind, object?, bool>> MissionTypes =
        new Dictionary<string, Func<ObjectKind, object?, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["trigger"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Trigger },
            ["ship"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Ship },
            ["formation"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Formation },
            ["solar"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Solar },
            ["loot"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Loot },
            ["npc"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Npc },
            ["label"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Label },
            ["orders"] = (_, item) => item is MissionRef { Kind: MissionItemKind.OrderList },
            ["objective"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Objective },
            ["conversation"] = (_, item) => item is MissionRef { Kind: MissionItemKind.Conversation },
            ["encounter"] = (_, item) => item is MissionRef { Kind: MissionItemKind.RandomEncounter },
            ["prefab"] = (_, item) => item is MissionRef { Kind: MissionItemKind.PrefabInstance },
        };

    /// <summary>The analysis that the tree of a mission shows, or null for another tree.</summary>
    public MissionAnalysis? Analysis { get; private init; }

    /// <summary>The item of the row that shows a selection: the item itself when the tree has its row. Else it is the
    /// trigger or the section of a selected line. Most lines have no row, so the row of their section shows them
    /// (section 7.3 of the M6a design).</summary>
    public object? RowItemOf(object? selection) =>
        NodeOf(selection) == null && selection is MissionRef { Kind: MissionItemKind.Row, Section: { } section }
            ? MissionRef.OfSection(section, -1)
            : selection;

    /// <summary>True when the tree of a mission shows the base-game problems.</summary>
    private bool ShowsBaseGame { get; init; }

    /// <summary>The sections of the group "System unknown" of the tree of a mission. Their rows have the menu item "Show
    /// in system...".</summary>
    public IReadOnlySet<Data.Editing.SectionRef> SystemUnknown { get; private init; } = new HashSet<Data.Editing.SectionRef>();

    /// <summary>
    /// The section of a row of this tree of a mission: a trigger or another section of the script. The header row is the
    /// mission, so it gives the <c>[Mission]</c> section. A line row, a label, an act and a prefab instance have no
    /// section of their own, so they give null. The robot glyph of an agent edit uses it (section 8.5 of the MCP1
    /// design).
    /// </summary>
    /// <param name="item">The item of the row.</param>
    public SectionRef? SectionOfMissionRow(object? item) => item switch
    {
        MissionRef { Section: { } section, Row: < 0 } => section,
        MissionRef { Kind: MissionItemKind.Mission, Section: null, Key: null } mission
            when Analysis is { } analysis && VfsPath.Comparer.Equals(mission.Script, analysis.Script.Path) =>
            analysis.Document.OfKind(MissionSectionKind.Mission).FirstOrDefault()?.Ref,
        _ => null,
    };

    /// <summary>
    /// The tree of a mission (section 9 of the M6a design). It has the header row, then the groups Logic, Cast, Labels,
    /// Orders, Objectives, Conversations and Links. Then come the groups Random encounters, Behavior prefabs, Placed
    /// near the player, System unknown and Other sections. A group without items does not show. A tree of the same
    /// analysis and toggle is the tree before. A new tree keeps the open groups and the search of the tree before for
    /// the same script.
    /// </summary>
    /// <param name="text">Gives the text of a text id, or an empty text.</param>
    /// <param name="showBaseGame">True while the base-game messages show. The rows then mark base-game problems with
    /// the lock glyph.</param>
    /// <param name="systemName">Gives the name of a system for the act titles, or null.</param>
    /// <param name="acts">The acts of the graph file, or null for the proposed acts (decision D679).</param>
    public static HierarchyModel ForMission(MissionAnalysis analysis, Func<int, string> text, bool showBaseGame,
        HierarchyModel? previous, Func<string, string?>? systemName = null, MissionActs? acts = null) =>
        previous != null && ReferenceEquals(previous.Analysis, analysis) && previous.ShowsBaseGame == showBaseGame &&
        ReferenceEquals(previous.Acts, acts)
            ? previous
            : new HierarchyModel(MissionTree(analysis, text, showBaseGame, systemName, acts),
                analysis.Script.Path.ToUpperInvariant(), MissionTypes, previous)
            {
                Analysis = analysis,
                ShowsBaseGame = showBaseGame,
                Acts = acts,
                SystemUnknown = MissionPlacements.SystemUnknown(analysis.Index).Select(s => s.Ref).ToHashSet(),
            };

    /// <summary>The acts of the graph file that the tree of a mission shows, or null for the proposed acts.</summary>
    public MissionActs? Acts { get; private init; }

    /// <summary>The kinds of the rows of prefab mode that the kinds of a mission do not have.</summary>
    internal static class PrefabKinds
    {
        public static readonly ObjectKind Field = new("Field", EditorIcons.List, EditorTheme.Text);
        public static readonly ObjectKind Port = new("Port", EditorIcons.Link, EditorTheme.Text);
    }

    /// <summary>
    /// The tree of prefab mode (section 8.6 of the M6b design): the prefab row, then the groups "Fields", "Ports",
    /// "Triggers" and "Other sections" of its template. A field row and a port row select the Prefab page at its
    /// section. A tree of the same template and toggle is the tree before.
    /// </summary>
    public static HierarchyModel ForPrefab(PrefabModeModel prefab, bool showBaseGame, HierarchyModel? previous)
    {
        var analysis = prefab.Analysis!;
        if (previous != null && ReferenceEquals(previous.Analysis, analysis) && previous.ShowsBaseGame == showBaseGame)
        {
            return previous;
        }

        return new HierarchyModel(PrefabTree(prefab, showBaseGame), analysis.Script.Path, MissionTypes, previous)
        {
            Analysis = analysis,
            ShowsBaseGame = showBaseGame,
        };
    }

    /// <summary>The tree of the template of a prefab.</summary>
    internal static HierarchyNode PrefabTree(PrefabModeModel prefab, bool showBaseGame)
    {
        var analysis = prefab.Analysis!;
        var script = analysis.Script.Path;
        var problems = BaseGameMessages.Shown(analysis.Problems, showBaseGame).Where(p => p.Target != null)
            .GroupBy(p => p.Target!).ToDictionary(g => g.Key, g => g.ToList());
        var root = new HierarchyNode("prefab", MissionKinds.Prefab, prefab.Asset.Name, "Prefab", MissionRef.Of(script),
            prefab.ReadOnlyReason ?? script);
        var template = prefab.Template;
        var fields = new HierarchyNode("fields", MissionKinds.Group, "Fields");
        foreach (var field in template?.Fields ?? [])
        {
            fields.Add(new HierarchyNode("field:" + field.Id, PrefabKinds.Field, field.Label, MissionPageModel.KindLabel(field),
                new MissionRef(script, MissionItemKind.Mission, Key: "field:" + field.Id), field.Help.Length > 0 ? field.Help : null,
                field.Id));
        }

        AddGroup(root, fields);
        var ports = new HierarchyNode("ports", MissionKinds.Group, "Ports");
        foreach (var port in template?.Ports ?? [])
        {
            ports.Add(new HierarchyNode("port:" + port.Id, PrefabKinds.Port, port.Label, MissionPageModel.PortText(port),
                new MissionRef(script, MissionItemKind.Mission, Key: "port:" + port.Id), null, port.Id));
        }

        AddGroup(root, ports);
        HierarchyNode Row(MissionSection section, string detail) =>
            new($"s:{section.Index.ToString(CultureInfo.InvariantCulture)}", MissionKinds.Of(section.Kind),
                section.Nickname ?? section.Ref.Id.ToString(), detail, MissionRef.OfSection(section.Ref, -1), null, Values(section))
            {
                Problem = ProblemOf(problems.GetValueOrDefault(section.Ref)),
            };

        var triggers = new HierarchyNode("triggers", MissionKinds.Group, "Triggers");
        foreach (var trigger in analysis.Index.Triggers)
        {
            triggers.Add(Row(trigger.Section, trigger.Condition?.Name?.Plain ?? "No condition"));
        }

        AddGroup(root, triggers);
        var other = new HierarchyNode("other", MissionKinds.Group, "Other sections");
        foreach (var section in analysis.Document.Sections.Where(s => s.Kind != MissionSectionKind.Trigger))
        {
            other.Add(Row(section, section.Type));
        }

        AddGroup(root, other);
        return root;
    }

    /// <summary>The tree of a mission.</summary>
    /// <param name="systemName">Gives the name of a system for the act titles, or null. The graph uses the same
    /// titles.</param>
    /// <param name="acts">The acts of the graph file, or null for the proposed acts.</param>
    internal static HierarchyNode MissionTree(MissionAnalysis analysis, Func<int, string> text, bool showBaseGame,
        Func<string, string?>? systemName = null, MissionActs? acts = null)
    {
        var index = analysis.Index;
        var script = analysis.Script.Path;
        var problems = BaseGameMessages.Shown(analysis.Problems, showBaseGame).Where(p => p.Target != null)
            .GroupBy(p => p.Target!).ToDictionary(g => g.Key, g => g.ToList());
        var slot = analysis.Script.Slot;
        var title = slot != null && text(slot.TitleId) is { Length: > 0 } titleText ? titleText : MissionFiles.Display(script);
        var root = new HierarchyNode("mission", MissionKinds.Mission, slot != null ? $"{slot.Slot}: {title}" : title,
            MissionFiles.Display(script), MissionRef.Of(script), script);

        HierarchyNode Section(string prefix, MissionSection section, string? detail = null, ObjectKind? kind = null)
        {
            var node = new HierarchyNode($"{prefix}:{section.Index.ToString(CultureInfo.InvariantCulture)}",
                kind ?? MissionKinds.Of(section.Kind), section.Nickname ?? section.Ref.Id.ToString(), detail ?? "",
                MissionRef.OfSection(section.Ref, -1), null, Values(section))
            {
                Problem = ProblemOf(problems.GetValueOrDefault(section.Ref)),
            };
            return node;
        }

        // Logic: the acts and the frame "Not started by the mission", with their prefab instances and plain triggers.
        // The order is the layout order: by layer, and in a layer from the top (section 9 of the M6a design).
        var graph = MissionGraph.Build(analysis, new GraphOptions(GraphLens.Logic, SystemName: systemName) { Acts = acts });
        var layout = GraphLayout.Of(graph);
        var logic = new HierarchyNode("logic", MissionKinds.Group, "Logic");
        foreach (var act in graph.Acts)
        {
            var actNode = new HierarchyNode("act:" + act.Key, MissionKinds.Act, act.Title, act.Detail,
                GraphViewState.RefOf(script, act));
            foreach (var block in layout.OrderIn(act).Select(graph.Block).OfType<GraphBlock>())
            {
                if (block is GraphFrame { Instance: { } instance } frame)
                {
                    var prefabNode = new HierarchyNode("pf:" + frame.Key, MissionKinds.Prefab, instance.Definition.Name,
                        instance.Name, GraphViewState.RefOf(script, frame));
                    var order = layout.OrderIn(frame);
                    foreach (var trigger in order.Count > 0
                                 ? order.Select(graph.Block).OfType<GraphNode>().Select(n => n.Trigger).OfType<MissionTrigger>()
                                 : frame.Triggers)
                    {
                        prefabNode.Add(Trigger(trigger));
                    }

                    actNode.Add(prefabNode);
                }
                else if (block is GraphNode { Trigger: { } trigger })
                {
                    actNode.Add(Trigger(trigger));
                }
            }

            AddGroup(logic, actNode);
        }

        HierarchyNode Trigger(MissionTrigger trigger) =>
            Section("t", trigger.Section, trigger.Condition?.Name?.Plain ?? "No condition");

        AddGroup(root, logic);

        // Cast: ships, formations, solars, loot and NPCs. A ship row has the name text of its NPC.
        var cast = new HierarchyNode("cast", MissionKinds.Group, "Cast");
        foreach (var (id, name, kind) in new[]
                 {
                     ("ships", "Ships", MissionSectionKind.Ship), ("formations", "Formations", MissionSectionKind.Formation),
                     ("solars", "Solars", MissionSectionKind.Solar), ("loot", "Loot", MissionSectionKind.Loot),
                     ("npcs", "NPCs", MissionSectionKind.Npc),
                 })
        {
            var group = new HierarchyNode("cast/" + id, MissionKinds.Of(kind), name);
            foreach (var section in index.Document.OfKind(kind))
            {
                group.Add(Section("s", section, kind == MissionSectionKind.Ship ? NpcName(index, section, text) : null));
            }

            AddGroup(cast, group);
        }

        AddGroup(root, cast);

        // Labels: each label with its members.
        var labels = new HierarchyNode("labels", MissionKinds.Group, "Labels");
        foreach (var (label, members) in index.Labels.OrderBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
        {
            var labelNode = new HierarchyNode("label:" + label.ToLowerInvariant(), MissionKinds.Label, label,
                members.Count == 1 ? "1 member" : $"{members.Count.ToString(CultureInfo.InvariantCulture)} members",
                new MissionRef(script, MissionItemKind.Label, Key: label));
            foreach (var member in members)
            {
                labelNode.Add(Section("m:" + label.ToLowerInvariant(), member));
            }

            labels.Add(labelNode);
        }

        AddGroup(root, labels);

        // Orders, objectives, conversations and random encounters.
        var orderTags = analysis.Prefabs?.Instances.Where(i => i.Definition.Kind == MissionPrefabKind.Order && i.CoreSection != null)
            .GroupBy(i => i.CoreSection!).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(i => i.Definition.Name)))
            ?? [];
        AddGroup(root, Group("orders", "Orders", index.Document.OfKind(MissionSectionKind.OrderList)
            .Select(s => Section("s", s, orderTags.GetValueOrDefault(s)))));
        AddGroup(root, Group("objectives", "Objectives", index.Document.OfKind(MissionSectionKind.Objective)
            .Select(s => Section("s", s, s.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "type") && e.Count > 0) is { } type
                ? MissionIndex.Text(type[0])
                : ""))));
        AddGroup(root, Group("conversations", "Conversations", index.Document.OfKind(MissionSectionKind.Conversation)
            .Select(s =>
            {
                var count = s.Section.Count(e => MissionIndex.Is(e.Name, "line"));
                return Section("s", s, count == 1 ? "1 line" : $"{count.ToString(CultureInfo.InvariantCulture)} lines");
            })));

        // Links: the space cutscenes, base scenes and room ambients that the lines name.
        var links = new HierarchyNode("links", MissionKinds.Group, "Links");
        foreach (var (id, name, keys) in new[]
                 {
                     ("cutscenes", "Space cutscenes", new[] { "Act_CallThorn" }),
                     ("scenes", "Base scenes", new[] { "Act_AddRTC" }),
                     ("ambients", "Room ambients", new[] { "Act_AddAmbient" }),
                 })
        {
            var group = new HierarchyNode("links/" + id, MissionKinds.Link, name);
            foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate))
            {
                foreach (var line in trigger.Actions.Where(a => keys.Any(k => MissionIndex.Is(a.Key, k))))
                {
                    group.Add(new HierarchyNode($"link:{trigger.Section.Index}:{line.Entry}", MissionKinds.Link,
                        MissionFiles.Display(line.Value(0) ?? line.Key), trigger.Nickname,
                        new MissionRef(script, MissionItemKind.Row, trigger.Section.Ref, Row: line.Entry))
                    {
                        Problem = ProblemOf(problems.GetValueOrDefault(trigger.Section.Ref)?.Where(p => p.EntryIndex == line.Entry)
                            .ToList()),
                    });
                }
            }

            AddGroup(links, group);
        }

        AddGroup(root, links);
        AddGroup(root, Group("encounters", "Random encounters", index.Document.OfKind(MissionSectionKind.RandomEncounter)
            .Select(s => Section("s", s))));

        // Behavior prefabs: each prefab with its count and its instances. The instances of one prefab share one row, also
        // the linked instances of a prefab of a mod, whose definitions are records of their own.
        var prefabs = new HierarchyNode("prefabs", MissionKinds.Group, "Behavior prefabs");
        foreach (var group in (analysis.Prefabs?.Instances ?? []).GroupBy(i => i.PrefabKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.First().Definition.Order).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var definition = group.First().Definition;
            var kindNode = new HierarchyNode("kind:" + group.Key, MissionKinds.Prefab,
                $"{definition.Name} ({group.Count().ToString(CultureInfo.InvariantCulture)})", "",
                new MissionRef(script, MissionItemKind.PrefabKind, Key: group.Key), definition.Help.FirstSentence);
            foreach (var instance in group)
            {
                // An instance of a prefab that no mod has shows "Missing prefab", as Unity does.
                kindNode.Add(new HierarchyNode("inst:" + instance.Key, MissionKinds.Prefab, instance.Name,
                    instance.Linked is { Asset: null } ? MissionPageModel.MissingPrefabKind : definition.Name,
                    new MissionRef(script, MissionItemKind.PrefabInstance, Key: instance.Key)));
            }

            prefabs.Add(kindNode);
        }

        AddGroup(root, prefabs);
        AddGroup(root, Group("near-player", "Placed near the player", MissionPlacements.NearPlayer(index)
            .Select(s => Section("near", s))));
        AddGroup(root, Group("unknown", "System unknown", MissionPlacements.SystemUnknown(index).Select(s => Section("unknown", s))));
        AddGroup(root, Group("other", "Other sections", index.Document.Sections
            .Where(s => s.Kind is MissionSectionKind.MetaBehavior or MissionSectionKind.Other)
            .Select(s => Section("s", s, s.Type))));
        return root;

        HierarchyNode Group(string id, string name, IEnumerable<HierarchyNode> rows)
        {
            var group = new HierarchyNode(id, MissionKinds.Group, name);
            foreach (var row in rows)
            {
                group.Add(row);
            }

            return group;
        }
    }

    /// <summary>The name text of the NPC of a ship, or an empty text.</summary>
    private static string NpcName(MissionIndex index, MissionSection ship, Func<int, string> text)
    {
        if (ship.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "npc") && e.Count > 0) is not { } npc ||
            index.Item(MissionSectionKind.Npc, MissionIndex.Text(npc[0])) is not { } section ||
            section.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "individual_name") && e.Count > 0) is not { } name ||
            !int.TryParse(MissionIndex.Text(name[0]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return "";
        }

        return text(id);
    }

    /// <summary>The values of the lines of a section, for the search of the tree.</summary>
    private static string Values(MissionSection section) =>
        string.Join(" ", section.Section.SelectMany(e => e.Select(MissionIndex.Text)));

    /// <summary>The mark of the worst problem of a row.</summary>
    private static RowProblem ProblemOf(IReadOnlyList<Diagnostic>? problems)
    {
        if (problems == null || problems.Count == 0)
        {
            return RowProblem.None;
        }

        var shown = problems.Where(p => p.Origin?.IsBaseGame != true).ToList();
        return shown.Any(p => p.Severity == DiagnosticSeverity.Error) ? RowProblem.Error
            : shown.Count > 0 ? RowProblem.Warning
            : RowProblem.Muted;
    }
}
