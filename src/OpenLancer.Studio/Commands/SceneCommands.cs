using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Runtime.Scene;
using ZoneShape = LibreLancer.Data.Schema.Universe.ZoneShape;

namespace OpenLancer.Studio.Commands;

/// <summary>One item of a transform request: the section and its new values.</summary>
public sealed record TransformMove(SectionRef Item, TransformChange Change);

/// <summary>
/// The commands of the items of a system in the Scene (section 4.0.8 of the M6b design): Create, the spawn of an asset,
/// Duplicate, Delete, Rename, the transform and the components of an object. The editor UI and the agent tools call
/// them, and each one gives the plan that its caller runs.
/// </summary>
/// <remarks>
/// <para>Each command calls the plan class of today (<see cref="ScenePlans"/>, <see cref="ObjectComponents"/> and
/// <see cref="TransformPlans"/>) and does not copy its rules. A plan reads the edit view when it is made, so make it
/// and run it at once.</para>
/// <para>A command never throws for a rule of the edit. It gives a plan that refuses, with the reason.</para>
/// </remarks>
public static class SceneCommands
{
    /// <summary>
    /// Plans a new object, zone or light in a system (decisions D233 to D235, D250 and D263 of the M3 design). See
    /// <see cref="ScenePlans.Create"/>.
    /// </summary>
    /// <param name="system">The nickname of the system.</param>
    /// <param name="archetype">The archetype of an object template, or null for a zone or a light.</param>
    /// <param name="parent">The object that gets the new object as a child, or null.</param>
    /// <param name="dockWith">The base of a station, or null.</param>
    /// <param name="shape">The shape of a zone.</param>
    public static ScenePlan Create(EditSession session, string system, SceneTemplate template, string? archetype,
        Vector3 position, SectionRef? parent = null, string? dockWith = null, ZoneShape shape = ZoneShape.SPHERE) =>
        ScenePlans.Create(session, system, template, archetype, position, parent, dockWith, shape);

    /// <summary>The template of the spawn of a solar archetype (decision D277 of the M23 design): the template whose
    /// types hold the type of the archetype, else <see cref="SceneTemplate.Solar"/>. The plan of a type in no template
    /// then refuses with its reason.</summary>
    public static SceneTemplate SpawnTemplate(EditSession session, string archetype) =>
        ScenePlans.TemplateFor(session, archetype) ?? SceneTemplate.Solar;

    /// <summary>True when the spawn of a solar archetype asks for a base first: a station archetype with a docking sphere
    /// (decisions D263 and D277). A station archetype without a docking sphere spawns without a base.</summary>
    public static bool SpawnAsksForBase(EditSession session, string archetype) =>
        ScenePlans.AsksForBase(session, SpawnTemplate(session, archetype), archetype);

    /// <summary>
    /// Plans the spawn of an asset at a point of a system (section 8 of the M23 design). A solar archetype spawns with
    /// the template of its type. A ship, an asteroid or a model spawns as a new static prop archetype with its object
    /// (decision D279). Each spawn is one entry of the edit history.
    /// </summary>
    /// <param name="parent">The object that gets the new object as a child, or null.</param>
    /// <param name="dockWith">The base of a station, or null (see <see cref="SpawnAsksForBase"/>).</param>
    public static ScenePlan SpawnAsset(EditSession session, string system, AssetRef asset, Vector3 position,
        SectionRef? parent = null, string? dockWith = null)
    {
        switch (asset.Kind)
        {
            case AssetKind.SolarArchetype:
                return Create(session, system, SpawnTemplate(session, asset.Id), asset.Id, position, parent, dockWith);
            case AssetKind.Ship or AssetKind.Asteroid or AssetKind.Model:
                if (session.Assets.ModelOf(asset) is not { } source)
                    return RefusedCreate($"{asset.Id} has no model.");
                var stem = asset.Kind == AssetKind.Model ? Path.GetFileNameWithoutExtension(asset.Id) : asset.Id;
                return ScenePlans.CreateProp(session, system, source, stem, position, parent);
            case AssetKind.PropGroup:
                return RefusedCreate("A prop group places with all its members. Use the placement of the prop group.");
            default:
                return RefusedCreate("The Scene does not place this asset.");
        }
    }

    /// <summary>Plans a copy of items with their children (decisions D241 and D259 to D262). See
    /// <see cref="ScenePlans.Duplicate"/>.</summary>
    public static ScenePlan Duplicate(EditSession session, IReadOnlyList<SectionRef> targets) =>
        ScenePlans.Duplicate(session, targets);

    /// <summary>Plans a delete of items with the cascade of decision D81 (decisions D236 to D238). See
    /// <see cref="ScenePlans.Delete"/>.</summary>
    public static ScenePlan Delete(EditSession session, IReadOnlyList<SectionRef> targets) =>
        ScenePlans.Delete(session, targets);

    /// <summary>Plans a rename of an item with each line that names it (decisions D12, D231 and D257). See
    /// <see cref="ScenePlans.Rename"/>.</summary>
    public static ScenePlan Rename(EditSession session, SectionRef target, string newNickname) =>
        ScenePlans.Rename(session, target, newNickname);

    /// <summary>
    /// Plans a transform of objects, zones and lights as one entry of the edit history, with the rules of
    /// <see cref="TransformPlans"/>. Each item moves alone, as with the gizmo: its children do not move until the move
    /// with children of milestone M3b. When one item refuses, the plan refuses with the reason of each item that
    /// refuses.
    /// </summary>
    /// <param name="moves">The items and their new values. An item can be in the request one time.</param>
    public static EditPlan Transform(EditSession session, IReadOnlyList<TransformMove> moves, TransformMode mode)
    {
        if (moves.Count == 0)
            return CommandPlan.Refused("Transform", "Name at least one item to transform.");
        if (moves.GroupBy(m => m.Item).FirstOrDefault(g => g.Count() > 1) is { Key: var twice })
            return CommandPlan.Refused("Transform", $"{twice.Id} is in the request more than one time. Name each item " +
                                                    "one time.", twice);
        var plans = moves.Select(m => TransformPlans.Plan(session, m.Item, m.Change, mode)).ToList();
        if (plans.Count == 1)
            return plans[0];
        var first = moves[0].Item.Id;
        return new RequestPlan($"Transform {first.Key ?? first.ToString()} and {plans.Count - 1} more", plans);
    }

    /// <summary>
    /// Plans the add of a component with its value, or the remove of a component, of an object (decisions D243, D244 and
    /// D258), as one entry of the edit history. The remove of Solar refuses, as Unity refuses to remove a component that
    /// another component needs (RequireComponent). See <see cref="ObjectComponents.Add"/> and
    /// <see cref="ObjectComponents.Remove"/>.
    /// </summary>
    /// <param name="value">The value of the added component, for example a loadout, or null to remove the
    /// component.</param>
    public static EditPlan SetComponent(EditSession session, SectionRef section, ObjectComponent kind, string? value)
    {
        var label = $"{(value != null ? "Add" : "Remove")} {ObjectComponents.Name(kind)}";
        if (session.Get(section) == null)
            return CommandPlan.Refused(label, $"The edit view has no section {section.Id}.", section);
        try
        {
            return new CommandPlan(value != null
                ? ObjectComponents.Add(section, kind, value)
                : ObjectComponents.Remove(section, kind), [section]);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return CommandPlan.Refused(label, ErrorText.Of(e), section);
        }
    }

    /// <summary>
    /// Plans the drop of a loadout or a faction tile of the Assets panel on an object (decision D281 of the M23 design):
    /// the Loadout user or the Faction member component with the value of the tile, as one entry "Set loadout of
    /// &lt;object&gt;". It also replaces a present line (review finding R18 of the M23 design).
    /// </summary>
    public static EditPlan DropAsset(EditSession session, SectionRef section, AssetRef asset)
    {
        var (kind, what) = asset.Kind switch
        {
            AssetKind.Loadout => (ObjectComponent.LoadoutUser, "loadout"),
            AssetKind.Faction => (ObjectComponent.FactionMember, "faction"),
            _ => ((ObjectComponent?)null, ""),
        };
        var label = $"Set {what} of {section.Id.Key}";
        if (kind is not { } component)
            return CommandPlan.Refused($"Set {asset.Id}", "Drop a loadout or a faction on an object.", section);
        var add = SetComponent(session, section, component, asset.Id);
        return add.CanRun
            ? new CommandPlan(EditCommand.Group(label, [add.Command!]), [section])
            : CommandPlan.Refused(label, add.Refusals[0].Message, section);
    }

    /// <summary>A plan of a create that refuses, with the reason.</summary>
    private static ScenePlan RefusedCreate(string message) =>
        ScenePlans.Refused(new ScenePlan(SceneAction.Create, "Create"), Diagnostic.Error("", message));

    /// <summary>The plan of a request with more items: the plans of the items as one entry.</summary>
    private sealed class RequestPlan : EditPlan
    {
        public RequestPlan(string label, IReadOnlyList<EditPlan> plans) : base(label)
        {
            Refusals = plans.SelectMany(p => p.Refusals).ToList();
            Warnings = plans.SelectMany(p => p.Warnings).ToList();
            if (Refusals.Count > 0)
                return;
            Changes = plans.SelectMany(p => p.Changes).Distinct().ToList();
            Command = EditCommand.Group(label, plans.Select(p => p.Command!).ToList());
        }
    }
}
