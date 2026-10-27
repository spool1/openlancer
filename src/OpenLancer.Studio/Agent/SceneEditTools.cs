using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;
using ZoneShape = LibreLancer.Data.Schema.Universe.ZoneShape;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The Scene tools of section 7.3 of the MCP1 design. They create objects, zones and lights, spawn an asset, transform
/// items, change the components of an object and place a prop group. Each tool calls the command of the command layer
/// that the Scene of the editor calls (see <see cref="SceneCommands"/> and <see cref="PropGroupCommands"/>). It calls it
/// in the shared edit call of <see cref="EditTools"/>.
/// </summary>
/// <remarks>
/// <para>A create runs the Create command of the Scene. Then the same batch gives the new item the nickname, the
/// rotation, the size, the name and the fields of the request. So the request is one Undo step, and the result gives the
/// new item with its last reference. The rename to the nickname of the request never asks for <c>confirm: true</c>,
/// because a Create never asks, as in Unity. Its warnings stay in the result.</para>
/// <para>A station asks for a base only when the Create menu asks for one: an archetype with a docking sphere (review
/// finding R5 of MCP1-2).</para>
/// </remarks>
internal static class SceneEditTools
{
    /// <summary>The detail code of a station archetype with a docking sphere and no base.</summary>
    public const string NeedsBase = "NeedsBase";

    /// <summary>The detail code of a base for an archetype that takes no base.</summary>
    public const string TakesNoBase = "TakesNoBase";

    /// <summary>The detail code of an archetype of another template of Create.</summary>
    public const string ArchetypeTemplate = "ArchetypeTemplate";

    /// <summary>The detail code of a parent that is not an object of the system of the request.</summary>
    public const string ParentNotInSystem = "ParentNotInSystem";

    /// <summary>The detail code of an item without a <c>pos</c> line in <c>scene_transform</c>.</summary>
    public const string NoPosition = "NoPosition";

    /// <summary>The detail code of a placement of a prop group with error lines (decision D290 of the M23 design). Each
    /// such line names an entry that the profile does not have.</summary>
    public const string PlacementErrors = "PlacementErrors";

    /// <summary>The detail code of a scene_transform with more moves than the item limit.</summary>
    public const string EditItems = "EditItems";

    /// <summary>The most characters of the name of a new object (section 7.8 of the MCP1 design).</summary>
    public const int MaxName = 128;

    /// <summary>The words of <c>template</c> of <c>scene_create_object</c>: the object templates of Create.</summary>
    public static readonly IReadOnlyList<string> ObjectTemplates = ["station", "planet", "sun", "satellite", "solar"];

    /// <summary>The words of <c>shape</c> of <c>scene_create_zone</c>.</summary>
    public static readonly IReadOnlyList<string> Shapes = ["sphere", "ellipsoid", "box", "cylinder", "ring"];

    /// <summary>The words of <c>mode</c> of <c>scene_transform</c>.</summary>
    public static readonly IReadOnlyList<string> Modes = ["absolute", "relative"];

    /// <summary>The components of an object that <c>scene_set_component</c> names, in the order of the components
    /// strip of the Inspector.</summary>
    public static readonly IReadOnlyList<ObjectComponent> Components =
    [
        ObjectComponent.Solar, ObjectComponent.LoadoutUser, ObjectComponent.DockableStation, ObjectComponent.BaseObject,
        ObjectComponent.FactionMember, ObjectComponent.JumpConnection, ObjectComponent.TradeLaneRing,
        ObjectComponent.PlanetRing,
    ];

    /// <summary>The words of <c>action</c> of <c>scene_set_component</c>.</summary>
    public static readonly IReadOnlyList<string> Actions = ["add", "remove"];

    /// <summary>
    /// Creates an object with a template of Create (decisions D233 to D235 and D263 of the M3 design). A station archetype
    /// with a docking sphere needs a base, as the Create menu asks for one. An archetype of another template gives
    /// <c>invalid_value</c> with the reason of the template.
    /// </summary>
    public static ToolStep CreateObject(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var args = call.Args;
        var (system, path) = System(call);
        var template = Enum.Parse<SceneTemplate>(args.Text("template")!, ignoreCase: true);
        var text = args.Text("archetype")!;
        // The line gets the nickname as the list gives it, as a pick in the Create menu does.
        var archetype = session.Lists.List("solar_archetype")?.Find(text)?.Value ?? throw new EditException(new EditError(
            EditErrorKind.NotFound, ItemRefs.NotFound, AgentTexts.Error("archetype_none", ("archetype", text)) +
                                                       ItemRefs.Hint(text, session.Lists.List("solar_archetype")?.Entries
                                                           .Select(e => e.Value) ?? [])) { Field = "archetype" });
        if (ScenePlans.TemplateFor(session, archetype) != template)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidValue, ArchetypeTemplate,
                ScenePlans.TemplateProblem(session, archetype, template)) { Field = "archetype" });
        }
        var parent = Parent(call, path);
        var dockWith = Base(call, ScenePlans.AsksForBase(session, template, archetype), archetype);
        var plan = SceneCommands.Create(session, system, template, archetype, args.Point("position")!.Value, parent,
            dockWith);
        Created(scope, call, plan, args.Point("rotation"), null);
    });

    /// <summary>Creates a zone with a shape (decision D250 of the M3 design). A size and a rotation of the request go
    /// into the same batch, with the rules of the gizmo (see <see cref="TransformPlans"/>).</summary>
    public static ToolStep CreateZone(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var args = call.Args;
        var (system, _) = System(call);
        var shape = Enum.Parse<ZoneShape>(args.Text("shape")!, ignoreCase: true);
        var plan = SceneCommands.Create(scope.Session, system, SceneTemplate.Zone, null, args.Point("position")!.Value,
            shape: shape);
        Created(scope, call, plan, args.Point("rotation"), args.Numbers("size"));
    });

    /// <summary>Creates a light. The engine reads no rotation for a light, so the tool takes none.</summary>
    public static ToolStep CreateLight(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var (system, _) = System(call);
        Created(scope, call,
            SceneCommands.Create(scope.Session, system, SceneTemplate.Light, null, call.Args.Point("position")!.Value),
            null, null);
    });

    /// <summary>
    /// Spawns an asset of the Assets panel at a point (section 8 of the M23 design). A solar archetype spawns with the
    /// template of its type. A ship, an asteroid or a model spawns as a new static prop archetype with its object
    /// (decision D279). The Scene shows such an object after the save and the reload (decision D282), and the result
    /// tells it.
    /// </summary>
    public static ToolStep SpawnAsset(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var args = call.Args;
        var (system, path) = System(call);
        var asset = EditTools.At("asset", () =>
        {
            var text = args.Text("asset")!;
            var reference = ItemRefs.Parse(text);
            if (reference.Kind != ItemRefKind.Asset)
                throw ToolInput.Bad("asset", AgentTexts.Error("asset_ref", ("ref", text)));
            return ItemQueries.AssetOf(session, reference).Asset;
        });
        var parent = Parent(call, path);
        var asks = asset.Kind == AssetKind.SolarArchetype && SceneCommands.SpawnAsksForBase(session, asset.Id);
        var dockWith = Base(call, asks, asset.Id);
        var plan = SceneCommands.SpawnAsset(session, system, asset, args.Point("position")!.Value, parent, dockWith);
        var item = Created(scope, call, plan, null, null);
        if (plan.NewArchetype is not { } archetype)
            return;
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Notes =
            [
                AgentTexts.Error("spawn_new_archetype", ("ref", ItemRefs.Canonical(session, item)),
                    ("archetype", archetype.Id.Key ?? "")),
            ],
        });
    });

    /// <summary>
    /// Moves, rotates and sizes objects, zones and lights as one Undo step, with the rules of the gizmo (see
    /// <see cref="SceneCommands.Transform"/>). Each item moves alone, as with the gizmo. An item without a position
    /// refuses, and so does a size of an object: retail objects have no scale key.
    /// </summary>
    public static ToolStep Transform(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var rows = call.Args.Rows("moves")!;
        var limit = call.Limits.EditItems;
        if (rows.Count > limit)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, EditItems,
                AgentTexts.Error("limit_moves", ("max", limit))) { Field = "moves" });
        }
        var moves = rows.Select((row, i) =>
        {
            var field = $"moves[{i}].ref";
            var text = row.Text("ref")!;
            var section = EditTools.Section(call, text, field);
            if (session.Get(section)?.Lines("pos").Count is null or 0)
            {
                throw new EditException(new EditError(EditErrorKind.Refused, NoPosition,
                    AgentTexts.Error("transform_no_position", ("ref", ItemRefs.Canonical(session, section))))
                {
                    Field = field,
                    Target = ItemRefs.Canonical(session, section),
                });
            }
            return new TransformMove(section,
                new TransformChange(row.Point("position"), row.Point("rotation"), row.Numbers("size")));
        }).ToList();
        var mode = call.Args.Text("mode") == "relative" ? TransformMode.Relative : TransformMode.Absolute;
        scope.Run(SceneCommands.Transform(session, moves, mode), moves.Select(m => m.Item));
    });

    /// <summary>
    /// Adds a component with its value to an object, or removes a component (decisions D243, D244 and D258 of the M3
    /// design). The remove of Solar refuses, as Unity refuses to remove a component that another component needs
    /// (RequireComponent).
    /// </summary>
    public static ToolStep SetComponent(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var args = call.Args;
        var section = EditTools.Section(call, args.Text("ref")!, "ref");
        var kind = Components.First(k => AgentTools.Snake(k.ToString()) == args.Text("component"));
        var add = args.Text("action") == "add";
        var value = args.Text("value");
        if (add && value == null)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.ValueMissing,
                AgentTexts.Error("component_value")) { Field = "value" });
        }
        if (!add && value != null)
            throw ToolInput.Bad("value", AgentTexts.Error("component_no_value"));
        if (value != null && (value.Trim().Length == 0 || value.Any(c => c is ',' or ';' || char.IsControl(c))))
        {
            throw new EditException(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue,
                AgentTexts.Error("component_value_text")) { Field = "value" });
        }
        scope.Run(SceneCommands.SetComponent(scope.Session, section, kind, value?.Trim()), [section]);
    });

    /// <summary>
    /// Places a prop group in a system (decisions D288 and D290 of the M23 design). A line of a member that names an entry
    /// that the profile does not have refuses the placement, with each such line. With
    /// <c>leave_out_error_lines</c>, the placement leaves out these lines, as "Place without these lines" of the
    /// editor.
    /// </summary>
    public static ToolStep PlaceGroup(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var args = call.Args;
        var group = Group(call, "group");
        var (system, path) = System(call);
        var parent = Parent(call, path);
        var plan = PropGroupCommands.Place(session, system, group, args.Point("anchor")!.Value, parent,
            args.Flag("leave_out_error_lines"));
        if (!plan.CanRun && plan.PlacementErrors.Count > 0)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, PlacementErrors,
                AgentTexts.Error("place_errors", ("group", ItemQueries.GroupRef(group)), ("lines",
                    string.Join(" ", plan.PlacementErrors.Select(e => $"{e.Member}, {e.Key}: {e.Message}"))))));
        }
        scope.Run(plan);
    });

    /// <summary>The prop group of a <c>group:</c> argument.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for another kind of reference, or
    /// <see cref="EditErrorKind.NotFound"/>.</exception>
    internal static PropGroup Group(ToolCall call, string field) => EditTools.At(field, () =>
    {
        var text = call.Args.Text(field)!;
        var reference = ItemRefs.Parse(text);
        if (reference.Kind != ItemRefKind.Group)
            throw ToolInput.Bad(field, AgentTexts.Error("group_ref", ("name", field), ("ref", text)));
        return ItemQueries.Group(call.Session, reference);
    });

    /// <summary>
    /// Runs the plan of a new item, then the parts of the request on the new item in the same batch. The parts are the
    /// rename to the nickname, the rotation and the size, the name and the fields. Each part gives its errors with its
    /// argument.
    /// </summary>
    /// <returns>The new item with its last nickname.</returns>
    private static SectionRef Created(EditScope scope, ToolCall call, ScenePlan plan, Vector3? rotation,
        IReadOnlyList<float>? size)
    {
        var session = scope.Session;
        var args = call.Args;
        var nickname = args.Text("nickname")?.Trim();
        if (nickname != null)
            scope.Batch.Label ??= $"Create {nickname}";
        scope.Run(plan);
        // The plan of a static prop archetype adds the archetype too, and selects the object.
        var item = plan.SelectAfter ?? plan.Adds[^1];
        if (nickname != null && !nickname.Equals(item.Id.Key, StringComparison.Ordinal))
        {
            var from = ItemRefs.Canonical(session, item);
            var rename = SceneCommands.Rename(session, item, nickname);
            EditTools.At("nickname", () => scope.Run(rename, asks: false));
            item = rename.SelectAfter!;
            scope.Report(new EditResult(EditStatus.Applied)
            {
                Renames = [new ResultRename(from, ItemRefs.Canonical(session, item))],
            });
        }
        if (rotation != null || size != null)
        {
            var change = new TransformChange(Rotation: rotation, Size: size);
            EditTools.At(size != null ? "size" : "rotation", () =>
                scope.Run(SceneCommands.Transform(session, [new TransformMove(item, change)], TransformMode.Absolute)));
        }
        if (args.Text("name") is { } name)
        {
            var key = ItemEditTools.TextKey(session, item, "name");
            EditTools.At("name", () => scope.Run(FieldCommands.SetText(session, item, key, name)));
        }
        if (args.Node("fields") is JsonObject fields)
        {
            try
            {
                scope.Run(FieldCommands.SetFields(session, item,
                    fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).ToList()));
            }
            catch (EditException e)
            {
                throw new EditException(e.Error with { Field = e.Error.Field is { } key ? $"fields.{key}" : "fields" });
            }
        }
        return item;
    }

    /// <summary>The nickname and the file of the system of the argument <c>system</c>.</summary>
    private static (string Nickname, string? Path) System(ToolCall call)
    {
        var (nickname, _, path) = EditTools.At("system", () => SceneQueries.System(call.Session, call.Args.Text("system")!));
        return (nickname, path);
    }

    /// <summary>The object of the argument <c>parent</c>, or null. The parent must be an object of the system of the
    /// request.</summary>
    private static SectionRef? Parent(ToolCall call, string? systemPath)
    {
        if (call.Args.Text("parent") is not { } text)
            return null;
        var session = call.Session;
        var section = EditTools.Section(call, text, "parent");
        if (SceneSections.KindOf(session, section) != SceneKind.Object)
            throw ToolInput.Bad("parent", AgentTexts.Error("parent_object", ("ref", text)));
        if (systemPath == null || !VfsPath.Comparer.Equals(section.Path, systemPath))
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, ParentNotInSystem,
                AgentTexts.Error("parent_other_system", ("ref", ItemRefs.Canonical(session, section)),
                    ("system", call.Args.Text("system")!))) { Field = "parent", Target = text });
        }
        return section;
    }

    /// <summary>
    /// The base of the argument <c>base</c>, or null. A station archetype with a docking sphere needs a base, as the
    /// Create menu asks for one (decision D263). Another archetype takes no base.
    /// </summary>
    /// <param name="asks">True when the Create menu asks for a base for the archetype.</param>
    private static string? Base(ToolCall call, bool asks, string archetype)
    {
        var text = call.Args.Text("base");
        if (!asks)
        {
            if (text == null)
                return null;
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, TakesNoBase,
                AgentTexts.Error("base_not_taken", ("archetype", archetype))) { Field = "base" });
        }
        if (text == null)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, NeedsBase,
                AgentTexts.Error("base_needed", ("archetype", archetype))) { Field = "base" });
        }
        return EditTools.At("base", () =>
        {
            // No nickname holds a ':' (section 5.6 of the MCP1 design), so a text with one is a reference.
            var reference = text.Contains(':') ? ItemRefs.Parse(text) : new ItemReference(ItemRefKind.Base, text);
            if (reference.Kind != ItemRefKind.Base)
                throw ToolInput.Bad("base", AgentTexts.Error("base_ref", ("ref", text)));
            return ItemRefs.ResolveSection(call.Session, reference).Id.Key ?? reference.Name;
        });
    }
}
