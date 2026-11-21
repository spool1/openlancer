using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The generic edit tools of section 7.2 of the MCP1 design. Each tool takes the reference of an item. It calls the
/// command of the command layer that the editor UI calls for the item, through the shared edit call of
/// <see cref="EditTools"/>. A section of an INI file gets the commands of the Scene, the Inspector and the Database. An
/// item of a mission file gets the mission commands of the mission studio (the first item of section 7.4, see
/// <c>ItemEditTools.Missions.cs</c>). An entity or an event of a cutscene gets the commands of the Cutscene tab
/// (section 7.5 of the MCP1 design, see <c>ItemEditTools.Cutscenes.cs</c>). One call with items of more than one kind
/// runs all commands in one batch, so it is one Undo step.
/// </summary>
internal static partial class ItemEditTools
{
    /// <summary>The keys of the name and of the infocard of a section, in the order that <c>set_text</c> tries
    /// them.</summary>
    private static readonly string[] NameKeys = ["ids_name", "strid_name"];

    private static readonly string[] InfoKeys = ["ids_info"];

    /// <summary>The most characters of the text of <c>set_text</c>. An infocard of the game data can have some thousand
    /// characters.</summary>
    public const int MaxText = 32768;

    /// <summary>The detail code of a rename of a section that the editor cannot rename, for example a loadout.</summary>
    public const string NotRenamable = "NotRenamable";

    public static ToolStep SetFields(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var target = Target(call, call.Args.Text("ref")!, "ref", GuardOf(call));
        var fields = call.Args.Node("fields")!.AsObject();
        if (target.Cutscene is { } element)
        {
            SetCutsceneFields(scope, element, fields);
            return;
        }
        if (target.Mission is { } mission)
        {
            SetMissionFields(call, scope, mission, fields);
            return;
        }
        var section = target.Section!;
        scope.Run(FieldCommands.SetFields(scope.Session, section,
            fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).ToList()), [section]);
    });

    /// <summary>
    /// Sets the text of a text key of a section. Only the named section gets the new text, as the Inspector does when it
    /// does not ask. The result warns about each other section of the item that shows the same text, so the agent can
    /// call the tool on it too. A mission item gets the text of a line (see <see cref="SetMissionText"/>).
    /// </summary>
    public static ToolStep SetText(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var target = Target(call, call.Args.Text("ref")!, "ref");
        if (target.Mission is { } mission)
        {
            SetMissionText(call, scope, mission, call.Args.Text("field")!, call.Args.Text("text")!);
            return;
        }
        var section = target.Section!;
        var name = TextKey(session, section, call.Args.Text("field")!);
        // The shared sections keep the old text, so the check reads them before the edit.
        var shared = ItemEdits.SharedText(session, section, name);
        var plan = FieldCommands.SetText(session, section, name, call.Args.Text("text")!);
        if (!plan.CanRun)
        {
            var refusal = EditTools.RefusalOf(session, plan);
            throw new EditException(refusal with { Field = refusal.Kind == EditErrorKind.InvalidArgument ? "field" : "text" });
        }
        scope.Run(plan, [section]);
        if (shared.Count == 0)
            return;
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Warnings = shared.Select(s => new Problem(DiagnosticSeverity.Warning,
                AgentTexts.Error("text_shared", ("ref", ItemRefs.Canonical(session, s)), ("key", name)))
            {
                Ref = ItemRefs.Canonical(session, s),
            }).ToList(),
        });
    });

    /// <summary>
    /// Gives an item a new nickname with each line that names it. An item of a system calls the rename of the Scene
    /// (decisions D12, D231 and D257). A mission item calls the rename of the mission studio (see
    /// <see cref="RenameMissionItem"/>). A <c>[system]</c> of <c>universe.ini</c> calls the rename of a system (see
    /// <see cref="SystemPlans.Rename"/>), which changes only a system that the active mod adds. Another item calls the
    /// rename of the Database, which changes only an item that the active mod adds (review finding R4 of MCP1-2). The
    /// result gives the old and the new reference.
    /// </summary>
    public static ToolStep RenameItem(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var target = Target(call, call.Args.Text("ref")!, "ref");
        var name = call.Args.Text("new_name")!;
        if (target.Cutscene is { } entity)
        {
            RenameCutsceneEntity(scope, entity, name);
            return;
        }
        if (target.Mission is { } mission)
        {
            RenameMissionItem(call, scope, mission, name);
            return;
        }
        var section = target.Section!;
        if (session.IsSystemSection(section))
        {
            RenameSystem(scope, section, name);
            return;
        }
        var scene = SceneSections.KindOf(session, section) != null;
        var item = scene ? ScenePlans.RenameTarget(session, section) : section;
        var from = ItemRefs.Canonical(session, item);
        // The Database renames only the sections of the ship, equipment and goods files, for example not a loadout.
        if (!scene && session.Items.KindOf(item.Path) == null)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, NotRenamable,
                AgentTexts.Error("rename_kind", ("ref", from))) { Target = from, Field = "ref" });
        }
        var plan = scene ? SceneCommands.Rename(session, section, name) : DatabaseCommands.Rename(session, item, name);
        scope.Run(plan, [item]);
        var renamed = plan.SelectAfter ?? item with { Id = item.Id with { Key = name.Trim() } };
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Renames = [new ResultRename(from, ItemRefs.Canonical(session, renamed))],
        });
    });

    /// <summary>
    /// Deletes items. Items of a system go with the cascade of decision D81: the children, and the lines that a rule
    /// removes. A jump gate or a jump hole goes with its pair. The other lines that name them show "Missing". A
    /// <c>[system]</c> of <c>universe.ini</c> goes with its file and the partners of its jump objects (see
    /// <see cref="SystemPlans.Delete"/>). Each system delete is one plan. The plan of the other items of the system files
    /// runs after them, without the items that went with a deleted system. The editor has no delete of a data item, so
    /// such a reference refuses (review finding R4 of MCP1-2). Mission items go with the deletes of the mission studio
    /// (see <see cref="DeleteMissionItems"/>).
    /// </summary>
    public static ToolStep DeleteItems(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var (scene, missions, cutscene) = Split(call, scope, "Delete");
        foreach (var system in scene.Where(session.IsSystemSection))
            scope.Run(SystemPlans.Delete(session, system.Id.Key!), [system]);
        var items = scene.Where(s => !session.IsSystemSection(s) && session.Get(s) != null).ToList();
        if (items.Count > 0)
            scope.Run(SceneCommands.Delete(session, items), items);
        if (missions.Count > 0)
            DeleteMissionItems(call, scope, missions);
        if (cutscene.Count > 0)
            DeleteCutsceneItems(scope, cutscene);
    });

    /// <summary>
    /// Copies items. Items of a system get their copies with their children (decisions D241 and D259 to D262). The
    /// copies are at the place of their items. With an offset, the same batch moves each copy and each copy of a child by
    /// the offset (review finding R6 of MCP1-2). A copy without a position does not move, and the result warns about it.
    /// So the result tells the agent each copy that is not at the offset. Mission items get the copies of the mission
    /// studio (see <see cref="DuplicateMissionItems"/>), and cutscene events get the copies of the Timeline (see
    /// <see cref="DuplicateCutsceneEvents"/>). The offset does not move these copies either, and the result warns about
    /// them too.
    /// </summary>
    public static ToolStep DuplicateItems(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var (scene, missions, cutscene) = Split(call, scope, "Duplicate");
        var offset = call.Args.Point("offset") is { } point && point != Vector3.Zero ? point : (Vector3?)null;
        var timeOffset = call.Args.Number("time_offset");
        if (timeOffset != null && cutscene.Count == 0)
            throw ToolInput.Bad("time_offset", AgentTexts.Error("cutscene_time_offset"));
        if (scene.Count > 0)
            DuplicateSceneItems(scope, scene, offset);
        if (missions.Count > 0)
            DuplicateMissionItems(call, scope, missions, offset != null);
        if (cutscene.Count > 0)
            DuplicateCutsceneEvents(scope, cutscene, timeOffset, offset != null);
    });

    /// <summary>Gives a system that the active mod adds a new nickname (see <see cref="SystemPlans.Rename"/>). The
    /// result gives the old and the new <c>system:</c> reference.</summary>
    private static void RenameSystem(EditScope scope, SectionRef system, string name)
    {
        var session = scope.Session;
        var from = ItemRefs.Canonical(session, system);
        var plan = SystemPlans.Rename(session, system.Id.Key!, name);
        scope.Run(plan, [system]);
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Renames = [new ResultRename(from, ItemRefs.Canonical(session, plan.System!))],
        });
    }

    /// <summary>Copies items of a system, then moves the copies with a position by the offset.</summary>
    private static void DuplicateSceneItems(EditScope scope, IReadOnlyList<SectionRef> targets, Vector3? offset)
    {
        var session = scope.Session;
        var plan = SceneCommands.Duplicate(session, targets);
        scope.Run(plan, targets);
        if (offset == null)
            return;
        var placed = plan.Adds.ToLookup(s => session.Get(s)?.Lines("pos").Count > 0);
        var moves = placed[true].Select(s => new TransformMove(s, new TransformChange(Position: offset))).ToList();
        if (moves.Count > 0)
            scope.Run(SceneCommands.Transform(session, moves, TransformMode.Relative), moves.Select(m => m.Item));
        if (!placed[false].Any())
            return;
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Warnings = placed[false].Select(s => ItemRefs.Canonical(session, s)).Select(copy =>
                new Problem(DiagnosticSeverity.Warning, AgentTexts.Error("copy_not_moved", ("ref", copy))) { Ref = copy })
                .ToList(),
        });
    }

    /// <summary>
    /// The items of the argument <c>refs</c> of <c>delete_items</c> and <c>duplicate_items</c>, each one time: the
    /// sections of INI files that are not mission files, and the mission items and the cutscene elements with the place
    /// of their reference in the list. The rows of <c>expect_events</c> guard the cutscene events. A call that runs
    /// more than one plan gets the label "{verb} {count} items": a call with items of more than one kind, a call with
    /// items of two cutscenes, and a delete of a cutscene entity with other cutscene items.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/> above the item limit, or the error of a
    /// reference with the place of the reference in the list as the field.</exception>
    private static (IReadOnlyList<SectionRef> Scene, IReadOnlyList<(int Index, MissionTarget Item)> Missions,
        IReadOnlyList<(int Index, ThnRef Element)> Cutscene) Split(ToolCall call, EditScope scope, string verb)
    {
        var targets = Refs(call).Select((r, i) => (Index: i, Target: Target(call, r, $"refs[{i}]"))).ToList();
        var scene = targets.Select(t => t.Target.Section).OfType<SectionRef>().Distinct().ToList();
        var missions = targets.Where(t => t.Target.Mission != null).Select(t => (t.Index, Item: t.Target.Mission!))
            .DistinctBy(t => t.Item.Key).ToList();
        var cutscene = targets.Where(t => t.Target.Cutscene != null).Select(t => (t.Index, Element: t.Target.Cutscene!))
            .DistinctBy(t => t.Element).ToList();
        CheckGuards(call, cutscene);
        var cutscenePlans = cutscene.GroupBy(t => t.Element.Path, OpenLancer.Data.IO.VfsPath.Comparer)
            .Sum(g => verb == "Delete"
                ? (g.Any(t => t.Element.Kind == ThnElementKind.Event) ? 1 : 0) +
                  g.Count(t => t.Element.Kind == ThnElementKind.Entity)
                : 1);
        // A delete runs one plan for each system and one plan for the other items of the system files.
        var systems = verb == "Delete" ? scene.Count(call.Session.IsSystemSection) : 0;
        var scenePlans = systems + (scene.Count > systems ? 1 : 0);
        if (scenePlans + (missions.Count > 0 ? 1 : 0) + cutscenePlans > 1)
            scope.Batch.Label ??= $"{verb} {scene.Count + missions.Count + cutscene.Count} items";
        return (scene, missions, cutscene);
    }

    /// <summary>The sections of the argument <c>refs</c> of a tool that changes only sections of INI files that are not
    /// mission files, each one time. <c>prop_group_save</c> uses it.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/> above the item limit, or the error of a
    /// reference with the place of the reference in the list as the field.</exception>
    internal static IReadOnlyList<SectionRef> Targets(ToolCall call) =>
        Refs(call).Select((r, i) => EditTools.Section(call, r, $"refs[{i}]")).Distinct().ToList();

    /// <summary>The argument <c>refs</c>. A call names at most the item limit of the host.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/> above the limit.</exception>
    private static IReadOnlyList<string> Refs(ToolCall call)
    {
        var refs = call.Args.Texts("refs")!;
        EditTools.RequireItemLimit(call, refs.Count, "limit_items", "refs");
        return refs;
    }

    /// <summary>The INI key of the field argument of <c>set_text</c>. <c>name</c> and <c>info</c> give the key of the
    /// name and of the infocard of the section. Another text is the INI key itself.</summary>
    internal static string TextKey(EditSession session, SectionRef section, string field)
    {
        var keys = field.ToLowerInvariant() switch
        {
            "name" => NameKeys,
            "info" => InfoKeys,
            _ => null,
        };
        if (keys == null)
            return field;
        var view = session.Get(section);
        return keys.FirstOrDefault(k => session.Schema(section, k) != null || view?.Lines(k).Count > 0) ?? keys[0];
    }
}
