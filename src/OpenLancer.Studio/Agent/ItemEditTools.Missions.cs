using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;

namespace OpenLancer.Studio.Agent;

/// <summary>An item that a generic edit tool names (see <see cref="ItemEditTools.Target"/>): a section of an INI file
/// that is not a mission file, or an item of a mission file. Exactly one of the two is not null.</summary>
internal sealed record ItemTarget(SectionRef? Section, MissionTarget? Mission);

/// <summary>
/// An item of a mission file that a generic edit tool names (section 7.2 and the first item of section 7.4 of the MCP1
/// design).
/// </summary>
/// <param name="Kind"><see cref="ItemRefKind.Trigger"/> for a trigger section with a nickname,
/// <see cref="ItemRefKind.Section"/> for another section, or <see cref="ItemRefKind.Line"/>,
/// <see cref="ItemRefKind.Label"/> or <see cref="ItemRefKind.Instance"/>.</param>
/// <param name="Path">The mission script, the ship build file of a script, or the template of a prefab in prefab mode
/// (<c>prefab:mod:&lt;id&gt;</c>).</param>
/// <param name="Text">The reference as the call gives it, for an error.</param>
internal sealed record MissionTarget(ItemRefKind Kind, string Path, string Text)
{
    /// <summary>The section of a trigger, a section or a line, else null.</summary>
    public SectionIdentity? Section { get; init; }

    /// <summary>The row of a line.</summary>
    public int Row { get; init; }

    /// <summary>The name of a label or the id of a prefab instance, else empty.</summary>
    public string Name { get; init; } = "";

    public ItemRef Item => new(Path, Section!);

    public TriggerRef Trigger => new(Path, Section!.Key!);

    /// <summary>The item without the text of its reference, so two references of one item are equal.</summary>
    public MissionTarget Key => this with { Text = "" };
}

internal static partial class ItemEditTools
{
    /// <summary>
    /// The item of a reference argument of a generic edit tool. A section of an INI file that is not a mission file is a
    /// section. A mission item is an item of a mission script or of the ship build file of a script: a <c>trg:</c>,
    /// <c>sec:</c>, <c>line:</c>, <c>label:</c> or <c>inst:</c> reference. In prefab mode, a <c>trg:</c>,
    /// <c>sec:</c> or <c>line:</c> reference can name the template of a prefab. The mission command then finds the
    /// item and gives its error. Each tool takes some of these kinds and refuses the others with the detail
    /// <see cref="EditTools.MissionItem"/>.
    /// </summary>
    /// <remarks>A section of another file under <c>DATA\MISSIONS</c>, for example a pilot file, refuses with the
    /// detail <c>WorldFile</c>. An item of a tool group that the user hides gives <c>not_available</c> (decision D725):
    /// a mission item is in the group Missions.</remarks>
    /// <param name="field">The argument, for the error.</param>
    /// <exception cref="EditException">The reference is not valid, does not resolve, or names an item that the generic
    /// tools do not change.</exception>
    internal static ItemTarget Target(ToolCall call, string text, string field) => EditTools.At(field, () =>
    {
        var session = call.Session;
        var reference = ItemRefs.Parse(text);
        if (EditTools.IsCutscene(reference.Kind))
            throw EditTools.CutsceneLater(text);
        if (!EditTools.IsMission(reference))
        {
            var section = ItemRefs.ResolveSection(session, reference);
            EditTools.RequireGroup(call, section, text);
            if (!SectionKeys.IsMissionFile(section.Path))
                return new ItemTarget(section, null);
            if (!session.Missions.IsMissionFile(section.Path))
                throw EditTools.WorldFileRefusal(section, text);
            return new ItemTarget(null, new MissionTarget(KindOf(section.Id), section.Path, text) { Section = section.Id });
        }
        if (call.Host.HiddenGroups.Contains(ToolGroup.Missions))
            throw new EditException(AgentTools.GroupHidden(ToolGroup.Missions) with { Target = text });
        return new ItemTarget(null, MissionOf(session, reference, text) ?? throw KindRefusal(call, text));
    });

    /// <summary>The mission item of a mission reference, or null for a kind that no generic tool takes: a
    /// <c>msn:</c>, <c>act:</c> or <c>prefab:</c> reference.</summary>
    /// <exception cref="EditException">The section of a trigger or a line does not resolve, or is not in a mission
    /// script or the ship build file of a script.</exception>
    private static MissionTarget? MissionOf(EditSession session, ItemReference reference, string text)
    {
        var kind = reference.Kind;
        if (kind is ItemRefKind.Label or ItemRefKind.Instance)
            return new MissionTarget(kind, reference.Path!, text) { Name = reference.Name };
        if (kind is not (ItemRefKind.Trigger or ItemRefKind.Section or ItemRefKind.Line))
            return null;
        if (MissionRefs.IsPrefab(reference.Path!))
        {
            var id = kind == ItemRefKind.Trigger ? new SectionIdentity("Trigger", reference.Name) : reference.Section!;
            return new MissionTarget(kind == ItemRefKind.Line ? kind : KindOf(id), reference.Path!, text)
            {
                Section = id, Row = reference.Row ?? 0,
            };
        }
        var section = ItemRefs.ResolveSection(session, reference);
        if (!session.Missions.IsMissionFile(section.Path))
        {
            throw SectionKeys.IsMissionFile(section.Path)
                ? EditTools.WorldFileRefusal(section, text)
                : new EditException(new EditError(EditErrorKind.InvalidArgument, ItemRefs.BadReference,
                    AgentTexts.Error("mission_line_file", ("ref", text))) { Target = text });
        }
        return new MissionTarget(kind == ItemRefKind.Line ? kind : KindOf(section.Id), section.Path, text)
        {
            Section = section.Id, Row = reference.Row ?? 0,
        };
    }

    /// <summary>The kind of the item of a section of a mission file: a trigger with a nickname, or another
    /// section.</summary>
    private static ItemRefKind KindOf(SectionIdentity id) =>
        MissionIndex.Is(id.Type, "Trigger") && id.Key != null ? ItemRefKind.Trigger : ItemRefKind.Section;

    /// <summary>The refusal of a mission reference of a kind that the tool does not take. The text tells the kinds that
    /// the tool takes and the tool for the reference.</summary>
    private static EditException KindRefusal(ToolCall call, string text, string? field = null) =>
        new(new EditError(EditErrorKind.InvalidArgument, EditTools.MissionItem, AgentTexts.Error(call.Tool switch
        {
            "set_fields" => "mission_ref_set_fields",
            "set_text" => "mission_ref_set_text",
            "rename_item" => "mission_ref_rename_item",
            "delete_items" => "mission_ref_delete_items",
            "duplicate_items" => "mission_ref_duplicate_items",
            _ => "edit_mission_item",
        }, ("ref", text), ("tool", call.Tool))) { Target = text, Field = field });

    // ---- set_fields ----

    /// <summary>
    /// Sets fields of a mission item by the keys of its section: <see cref="SetItemKey"/> for each field, in one
    /// request. Each value has a form of <see cref="MissionLineInput.FieldLines"/>, as in <c>mission_add_item</c>.
    /// <c>null</c> or an empty list removes the key. A trigger refuses, because its lines are conditions and actions:
    /// the mission tools change them.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/> for a trigger or the key line.
    /// <see cref="EditErrorKind.InvalidArgument"/> for a line, a label, a prefab instance or a value without a form.
    /// Each error of the request.</exception>
    private static void SetMissionFields(ToolCall call, EditScope scope, MissionTarget item, JsonObject fields)
    {
        if (item.Kind == ItemRefKind.Trigger)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, EditTools.MissionItem,
                AgentTexts.Error("mission_fields_trigger")) { Target = item.Text, Field = "ref" });
        }
        if (item.Kind != ItemRefKind.Section)
            throw KindRefusal(call, item.Text, "ref");
        foreach (var (key, value) in fields.Where(f => !MissionLineInput.IsField(f.Value)))
        {
            throw ToolInput.Bad($"fields.{key}",
                AgentTexts.Error("argument_type", ("name", $"fields.{key}"), ("expected", MissionLineInput.FieldForms)));
        }
        var type = item.Section!.Type;
        var keyLine = new EditError(EditErrorKind.Refused, MissionErrors.KeyLine, AgentTexts.Error("mission_fields_key_line"));
        var lines = MissionEditTools.ItemFields(fields, type, keyLine, AgentTexts.Error("mission_fields_order"));
        var commands = lines.Select(f => (MissionCommand)new SetItemKey(item.Item, f.Key, f.Lines)).ToList();
        var label = commands.Count == 1 ? "" : $"Set {commands.Count} fields of {item.Section.Key ?? type}";
        scope.RunMission(new MissionBatch(label, commands),
            error => error.Index is { } i && i < lines.Count ? lines[i].Field : "ref");
    }

    // ---- set_text ----

    /// <summary>
    /// Sets the text of a text id of a mission item with <see cref="SetLineText"/>: the text goes into the
    /// <c>strings.json</c> of the active mod, and the line gets its placeholder. With a <c>line:</c> reference, the
    /// field is the name of the value. With a trigger or another section, the field is a key of the section, and the
    /// tool sets the first text id of the first line of the key. A section without such a line gets the line in the same
    /// request, as the "Set" button of the page and the text field do in the mission studio. A trigger without such a
    /// line refuses, because the line is an action.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a key without a text id, a label
    /// or a prefab instance. Each error of the request.</exception>
    private static void SetMissionText(ToolCall call, EditScope scope, MissionTarget item, string field, string text)
    {
        static string FieldOf(EditError error) => error.Detail switch
        {
            MissionErrors.NoSuchValue or MissionErrors.NotTextId => "field",
            MissionErrors.ValueKind => "text",
            _ => "ref",
        };

        if (item.Kind == ItemRefKind.Line)
        {
            scope.RunMission(new MissionBatch("", [new SetLineText(new RowRef(item.Item, item.Row), field, text)]), FieldOf);
            return;
        }
        if (item.Kind is not (ItemRefKind.Trigger or ItemRefKind.Section))
            throw KindRefusal(call, item.Text, "ref");
        var type = item.Section!.Type;
        var entries = EntriesOf(scope.Session, item);
        if (TextSpec(type, field) is not { } spec)
        {
            var keys = item.Kind == ItemRefKind.Trigger
                ? entries.Select(e => e.Name).Where(k => TextSpec(type, k) != null).Distinct(StringComparer.OrdinalIgnoreCase)
                : MissionLineInput.SectionSpec(type)?.Keys.Where(k => k.ValueSpecs.Any(s => s.Kind == MissionValueKind.TextId))
                    .Select(k => k.Key) ?? [];
            var names = string.Join(", ", keys);
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.NotTextId, names.Length == 0
                ? AgentTexts.Error("mission_text_none", ("ref", item.Text))
                : AgentTexts.Error("mission_text_key", ("key", field), ("ref", item.Text), ("keys", names)))
            {
                Target = item.Text, Field = "field",
            });
        }
        var label = new SetLineText(new RowRef(item.Item, 0), spec.Name, text).HistoryLabel;
        var row = Row(entries, field);
        if (row < 0)
        {
            if (item.Kind == ItemRefKind.Trigger)
            {
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.RowNotFound,
                    AgentTexts.Error("mission_text_no_line", ("ref", item.Text), ("key", field)))
                {
                    Target = item.Text, Field = "field",
                });
            }
            // The new line has no values yet. The next command gives it the text id, so the strict check at the end of
            // the batch sees a whole line.
            scope.RunMission(new MissionBatch(label, [new SetItemKey(item.Item, field, [LineSpec.Raw(field)])]), FieldOf);
            row = Row(EntriesOf(scope.Session, item), field);
        }
        scope.RunMission(new MissionBatch(label, [new SetLineText(new RowRef(item.Item, row), spec.Name, text)]), FieldOf);
    }

    /// <summary>The first spec of a text id of a key of a section type, or null for a key without a text id or a key
    /// that the section does not have.</summary>
    private static MissionValueSpec? TextSpec(string type, string key)
    {
        try
        {
            return MissionValues.Key(MissionSchema.Retail, type, LinePlace.Section, key).Specs
                .FirstOrDefault(s => s.Kind == MissionValueKind.TextId);
        }
        catch (EditException)
        {
            return null;
        }
    }

    /// <summary>The row of the first line of a key, or -1.</summary>
    private static int Row(IReadOnlyList<Entry> entries, string key)
    {
        for (var row = 0; row < entries.Count; row++)
        {
            if (MissionIndex.Is(entries[row].Name, key.Trim()))
                return row;
        }
        return -1;
    }

    /// <summary>The lines of the section of a mission item now, in the batch of the call. The template of a prefab
    /// comes from its analysis in prefab mode.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/>: the file has no such section.</exception>
    private static IReadOnlyList<Entry> EntriesOf(EditSession session, MissionTarget item)
    {
        var id = item.Section!;
        var section = MissionRefs.IsPrefab(item.Path)
            ? session.Missions.AnalysisOf(item.Path)?.Document.Find(new SectionRef(item.Path, id))?.Section
            : session.OpenDocument(item.Path) is { } document && document.IndexOf(id) is >= 0 and var index
                ? document.Working[index]
                : null;
        return section?.ToList() ?? throw new EditException(MissionErrors.ItemNotFoundError(item.Path,
            MissionNames.KindWord(id.Type), id.Key ?? id.Type) with { Target = item.Text, Field = "ref" });
    }

    // ---- rename_item ----

    /// <summary>Renames a trigger with <see cref="RenameTrigger"/>, or another mission item with
    /// <see cref="RenameItem"/>, as F2 in the graph and the Hierarchy does. Each line that names the item
    /// changes.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a line, a label or a prefab
    /// instance. Each error of the request: a name that is not valid or that the file has names
    /// <c>new_name</c>.</exception>
    private static void RenameMissionItem(ToolCall call, EditScope scope, MissionTarget item, string name)
    {
        MissionCommand command = item.Kind switch
        {
            ItemRefKind.Trigger => new RenameTrigger(item.Trigger, name.Trim()),
            ItemRefKind.Section => new RenameItem(item.Item, name.Trim()),
            _ => throw KindRefusal(call, item.Text, "ref"),
        };
        scope.RunMission(new MissionBatch("", [command]), error =>
            error.Detail is MissionErrors.NameInvalid or MissionErrors.NameTaken or MissionErrors.NameIdTaken ? "new_name" : "ref");
    }

    // ---- delete_items ----

    /// <summary>
    /// Deletes mission items in one request, with the commands of Delete in the graph and the Hierarchy: a trigger with
    /// <see cref="RemoveTrigger"/>, a mission ship with its unused parts with <see cref="RemoveMissionShip"/> (decision
    /// D669), and another item with <see cref="RemoveItem"/>. A prefab instance goes with the commands of
    /// <see cref="PrefabLinks.DeleteCommands"/>. Each line that names a deleted item stays and shows "Missing", so the
    /// call needs <c>confirm: true</c>.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a line or a label.
    /// <see cref="EditErrorKind.Refused"/> for the <c>[Mission]</c> section. Each error of the request names the
    /// reference of its item.</exception>
    private static void DeleteMissionItems(ToolCall call, EditScope scope, IReadOnlyList<(int Index, MissionTarget Item)> items)
    {
        var commands = new List<MissionCommand>();
        var owners = new List<int>();
        var label = "";
        foreach (var (index, item) in items)
        {
            var (made, itemLabel) = EditTools.At($"refs[{index}]", () => DeleteCommandsOf(call, scope.Session, item));
            commands.AddRange(made);
            owners.AddRange(made.Select(_ => index));
            label = items.Count == 1 ? itemLabel : $"Delete {items.Count} items";
        }
        scope.RunMission(new MissionBatch(label, commands),
            error => error.Index is { } i && i < owners.Count ? $"refs[{owners[i]}]" : "refs");
    }

    /// <summary>The commands that delete one mission item, and the label of a call that deletes only this
    /// item.</summary>
    private static (IReadOnlyList<MissionCommand> Commands, string Label) DeleteCommandsOf(ToolCall call, EditSession session,
        MissionTarget item)
    {
        MissionCommand command;
        switch (item.Kind)
        {
            case ItemRefKind.Instance:
                var (script, instance) = InstanceOf(session, item);
                return (PrefabLinks.DeleteCommands(script, instance, instance.Claimed), PrefabLinks.DeleteLabel(instance));
            // A duplicate trigger, which the game never runs (TR4), goes by its section, as in the Hierarchy.
            case ItemRefKind.Trigger when item.Section!.Ordinal == 0:
                command = new RemoveTrigger(item.Trigger);
                break;
            case ItemRefKind.Section when MissionIndex.Is(item.Section!.Type, "Mission"):
                throw new EditException(new EditError(EditErrorKind.Refused, EditTools.MissionItem,
                    AgentTexts.Error("mission_delete_header")) { Target = item.Text });
            case ItemRefKind.Section when MissionIndex.Is(item.Section!.Type, "MsnShip"):
                command = new RemoveMissionShip(item.Item);
                break;
            case ItemRefKind.Trigger or ItemRefKind.Section:
                command = new RemoveItem(item.Item);
                break;
            default:
                throw KindRefusal(call, item.Text);
        }
        return ([command], command.HistoryLabel);
    }

    /// <summary>The prefab instance of an <c>inst:</c> reference in the analysis of its script now, with the path of
    /// the script as the session stores it.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotFound"/> with the closest ids.</exception>
    private static (string Script, MissionPrefabInstance Instance) InstanceOf(EditSession session, MissionTarget item)
    {
        var analysis = session.Missions.AnalysisOf(item.Path) ??
                       throw new EditException(MissionErrors.ScriptNotFoundError(item.Path) with { Target = item.Text });
        var instances = analysis.Prefabs?.Instances ?? [];
        var instance = instances.FirstOrDefault(i => string.Equals(i.Id, item.Name, StringComparison.OrdinalIgnoreCase)) ??
                       throw new EditException(MissionErrors.ItemNotFoundError(analysis.Script.Path, "prefab instance",
                           item.Name, ItemRefs.Hint(item.Name, instances.Select(i => i.Id))) with { Target = item.Text });
        return (analysis.Script.Path, instance);
    }

    // ---- duplicate_items ----

    /// <summary>
    /// Copies mission items in one request, with the commands of Ctrl+D in the graph and the Hierarchy: a trigger with
    /// <see cref="DuplicateTrigger"/> and a free name after its own name, and another item with
    /// <see cref="DuplicateItem"/>. The copies are at the end of their files. An offset does not move a mission copy, so
    /// the result warns about each copy, as for a copy of a system without a position.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a line, a label or a prefab
    /// instance. Each error of the request names the reference of its item.</exception>
    private static void DuplicateMissionItems(ToolCall call, EditScope scope, IReadOnlyList<(int Index, MissionTarget Item)> items,
        bool offset)
    {
        var session = scope.Session;
        var taken = new Dictionary<string, HashSet<string>>(VfsPath.Comparer);
        var commands = items.Select(t => t.Item.Kind switch
        {
            ItemRefKind.Trigger => new DuplicateTrigger(t.Item.Trigger) { NewName = FreeCopyName(session, taken, t.Item) },
            ItemRefKind.Section => (MissionCommand)new DuplicateItem(t.Item.Item),
            _ => throw KindRefusal(call, t.Item.Text, $"refs[{t.Index}]"),
        }).ToList();
        var label = commands.Count == 1 ? "" : $"Duplicate {commands.Count} items";
        var result = scope.RunMission(new MissionBatch(label, commands),
            error => error.Index is { } i && i < items.Count ? $"refs[{items[i].Index}]" : "refs");
        if (!offset || result.Adds.Count == 0)
            return;
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Warnings = result.Adds.Select(copy =>
                new Problem(DiagnosticSeverity.Warning, AgentTexts.Error("mission_copy_not_moved", ("ref", copy))) { Ref = copy })
                .ToList(),
        });
    }

    /// <summary>A free name for the copy of a trigger: its own name with <c>_2</c>, <c>_3</c> and so on, as Ctrl+D
    /// gives it (see <see cref="MissionNaming.Free"/>). The names of the triggers of the script and of the earlier copies
    /// of the call are taken.</summary>
    private static string FreeCopyName(EditSession session, Dictionary<string, HashSet<string>> taken, MissionTarget item)
    {
        if (!taken.TryGetValue(item.Path, out var names))
        {
            names = (session.Missions.AnalysisOf(item.Path)?.Index.Triggers ?? []).Select(t => t.Nickname)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            taken[item.Path] = names;
        }
        var name = MissionNaming.Free(item.Path, "Trigger", names, item.Section!.Key);
        names.Add(name);
        return name;
    }
}
