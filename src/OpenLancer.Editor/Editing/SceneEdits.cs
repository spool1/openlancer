using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Assets;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;
using ZoneShape = LibreLancer.Data.Schema.Universe.ZoneShape;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// The actions on the items of the open system (milestone M3): Create, Duplicate, Rename and Delete. Each action makes a
/// plan through a command of the command layer (see <see cref="SceneCommands"/>) and runs it through the command runner
/// as one entry of the edit history. The Hierarchy, the Scene, the Inspector and the Edit menu start them, with the keys
/// of Unity: F2 Rename, Ctrl+D Duplicate and Delete.
/// </summary>
/// <remarks>
/// <para>Create of an object opens the picker of the archetypes of its template, and a station then opens the picker of
/// the bases (decisions D233 and D263). A delete runs at once when it changes nothing else, and asks otherwise (decision
/// D238). A rename starts in the Hierarchy row (decision D242), and asks only when the plan has warnings.</para>
/// <para>The selection follows Unity (decision D249): Create and Duplicate select the new item, Delete clears the
/// selection, and a rename keeps it.</para>
/// <para>The asset browser of milestone M23 adds the spawn of an asset, the static prop archetypes, the components of a
/// dropped loadout or faction, and "Save as prop group". A spawn opens the pickers that its asset needs and runs its
/// plan as a Create. A placement of a prop group selects its first member, as a Duplicate does.</para>
/// </remarks>
internal sealed class SceneEdits(EditorWindow window)
{
    private const string Source = EditController.Source;

    private static readonly (string Label, ZoneShape Shape)[] Shapes =
    [
        ("Sphere", ZoneShape.SPHERE), ("Ellipsoid", ZoneShape.ELLIPSOID), ("Box", ZoneShape.BOX),
        ("Cylinder", ZoneShape.CYLINDER), ("Ring", ZoneShape.RING),
    ];

    private readonly PickDialog picker = new();
    private readonly PlanDialog dialog = new();
    private readonly PropGroupDialog groupDialog = new();

    /// <summary>The section whose Hierarchy row shows the rename field, or null.</summary>
    public SectionRef? Renaming { get; private set; }

    /// <summary>The text of the rename field.</summary>
    public string RenameText = "";

    /// <summary>True in the first frame of a rename, so the field takes the focus.</summary>
    public bool RenameStarted { get; set; }

    /// <summary>True while a dialog of the actions is open.</summary>
    public bool IsOpen => picker.IsOpen || dialog.IsOpen || groupDialog.IsOpen;

    public void Draw()
    {
        picker.Draw();
        dialog.Draw();
        groupDialog.Draw();
    }

    /// <summary>The section of an item of the open system that the actions take: an object, a zone, a light, a nebula or
    /// a field. Null for another item.</summary>
    public SectionRef? SectionOf(object? item) =>
        item is SystemObject or Zone or LightSource or Nebula or AsteroidField && window.Scene.Contains(item!)
            ? window.Scene.SectionOf(item!)
            : null;

    /// <summary>Why an action cannot run on an item now, or null. The menus show it as the tooltip of a disabled
    /// item.</summary>
    public string? Unavailable(SceneAction action, object? item)
    {
        if (window.Session is not { } session)
            return "No profile is open.";
        if (session.NoEditReason is { } reason)
            return reason;
        if (window.IsEditing)
            return "Wait until the field edit ends.";
        if (action == SceneAction.Create)
            return window.Scene.System == null ? "Open a system first." : null;
        if (SectionOf(item) is not { } section)
            return "Select an object, a zone, a light, a nebula or an asteroid field of the open system.";
        return ScenePlans.Unavailable(session.Edits, action, section);
    }

    /// <summary>The keys of the Hierarchy and the Scene: F2 Rename, Ctrl+D Duplicate and Delete. They act only on an
    /// item of the open system, while no text field is active, no field edit is open and no dialog shows (decision D255,
    /// review finding R32).</summary>
    public void Keys(object? selection)
    {
        if (ImGui.GetIO().WantTextInput || window.IsEditing || IsOpen || Renaming != null || SectionOf(selection) is not { } section)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.F2, false))
            BeginRename(section);
        else if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.D)))
            Duplicate(section);
        else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
            Delete(section);
    }

    /// <summary>
    /// The items of the Create menu: the object templates, the zone shapes and the light (decision D255). A connection
    /// template is not in the menu.
    /// </summary>
    /// <param name="row">The object of a Hierarchy row, whose position and parent a new object takes, or null.</param>
    public void CreateMenu(SectionRef? row)
    {
        var unavailable = Unavailable(SceneAction.Create, null);
        if (window.Session is not { } session)
            return;
        foreach (var (template, label) in ScenePlans.ObjectTemplates(session.Edits))
        {
            if (Theme.IconMenuItem(Kind(template).Icon, label + "...", unavailable == null))
                Create(template, row);
        }

        ImGui.Separator();
        if (ImGui.BeginMenu($"{ObjectKinds.Of(ZoneRole.Other).Icon}  Zone", unavailable == null))
        {
            foreach (var (label, shape) in Shapes)
            {
                if (ImGui.MenuItem(label))
                    Create(SceneTemplate.Zone, row, shape);
            }

            ImGui.EndMenu();
        }

        if (Theme.IconMenuItem(ObjectKinds.Light.Icon, "Light", unavailable == null))
            Create(SceneTemplate.Light, row);
        if (unavailable != null)
            ImGui.SetItemTooltip(unavailable);
        ImGui.Separator();
        ImGui.TextDisabled("Jump gates, jump holes and trade lane rings come with milestone M27.");
    }

    /// <summary>The items of the actions of a selected item for a menu: Duplicate, Rename and Delete, each disabled with
    /// its reason when it cannot run.</summary>
    public void ItemMenu(object? item)
    {
        if (MenuItem(EditorIcons.Clone, "Duplicate", "Ctrl+D", SceneAction.Duplicate, item))
            Duplicate(SectionOf(item)!);
        if (MenuItem(EditorIcons.Pen, "Rename", "F2", SceneAction.Rename, item))
            BeginRename(SectionOf(item)!);
        if (MenuItem(EditorIcons.TrashAlt, "Delete", "Del", SceneAction.Delete, item))
            Delete(SectionOf(item)!);
    }

    /// <summary>The menu item "Save as prop group..." of a Hierarchy row (section 6.4 of the M23 design), disabled with its
    /// reason when it cannot run.</summary>
    public void SaveGroupMenuItem(object? item)
    {
        var unavailable = window.Session is not { } session ? "No profile is open."
            : session.NoEditReason ?? (SectionOf(item) == null
                ? "Select an object, a zone or a light of the open system."
                : null);
        if (Theme.IconMenuItem(EditorIcons.Cubes, "Save as prop group...", unavailable == null))
            SaveGroup(SectionOf(item)!);
        ImGui.SetItemTooltip(unavailable ?? "Saves the item and its children as a prop group of the mod.");
    }

    /// <summary>
    /// Opens the dialog "Save as prop group" for an item and its <c>parent</c> children (decision D291 of the M23 design).
    /// The dialog writes the group at once, as Unity writes a prefab asset. Then the Assets panel shows the new tile.
    /// </summary>
    public void SaveGroup(SectionRef section)
    {
        if (window.Session is not { } session)
            return;
        if (session.NoEditReason is { } reason)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, reason, section);
            return;
        }

        var draft = PropGroups.Draft(session.Edits, [section]);
        groupDialog.ShowSave(session.Edits, draft, group =>
        {
            window.Log.Add(ConsoleSeverity.Info, Source,
                $"Saved the prop group {group.Name} with {group.Members.Count} members in {group.File}.");
            window.Assets.Show(new AssetRef(AssetKind.PropGroup, group.Nickname, null, group.File));
        });
    }

    /// <summary>Opens the rename dialog of a prop group of the active mod (decision D294 of the M23 design).</summary>
    public void RenameGroup(PropGroup group)
    {
        if (window.Session is not { } session)
            return;
        groupDialog.ShowRename(session.Edits, group, renamed =>
            window.Assets.Show(new AssetRef(AssetKind.PropGroup, renamed.Nickname, null, renamed.File)));
    }

    /// <summary>Deletes a prop group of the active mod after the question of decision D294. The copies in the systems
    /// stay. Undo does not take the delete back, as in the Unity Project window.</summary>
    public void DeleteGroup(PropGroup group)
    {
        if (window.Session is not { } session)
            return;
        window.Choice.Show("Delete prop group", $"Delete the prop group {group.Name}? You cannot undo this.",
        [
            new Choice("Delete", () =>
            {
                if (PropGroupCommands.Delete(session.Edits, group).Error is { } error)
                    window.Log.Add(ConsoleSeverity.Error, Source, $"The prop group {group.Name} was not deleted: {error.Message}");
                else
                    window.Log.Add(ConsoleSeverity.Info, Source, $"Deleted the prop group {group.Name}.");
            }),
            new Choice("Cancel", () => { }),
        ]);
    }

    /// <summary>
    /// Spawns an asset at a point of the open system (section 8 of the M23 design): a solar archetype with its template,
    /// a ship, an asteroid or a model through a static prop archetype, or a prop group. The pickers of the asset open
    /// first. Each spawn is one entry of the edit history.
    /// </summary>
    /// <param name="parentRow">The object of a Hierarchy row that gets the new object, or each root object of a group,
    /// as a child (decision D234), or null.</param>
    public void Spawn(AssetRow row, Vector3 point, SectionRef? parentRow)
    {
        if (window.Session is not { } session || window.Scene.System is not { } system)
            return;
        if (Unavailable(SceneAction.Create, null) is { } reason)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, reason);
            return;
        }

        var parent = parentRow is { Id.Type: var type } && type.Equals("Object", StringComparison.OrdinalIgnoreCase)
            ? parentRow
            : null;
        switch (row.Asset.Kind)
        {
            case AssetKind.SolarArchetype:
                SpawnArchetype(session, system.Nickname, row.Asset.Id, point, parent);
                break;
            case AssetKind.Ship or AssetKind.Asteroid or AssetKind.Model:
                SpawnProp(session, system.Nickname, row, point, parent);
                break;
            case AssetKind.PropGroup:
                PlaceGroup(session, system.Nickname, row.Asset, point, parent);
                break;
            default:
                window.Log.Add(ConsoleSeverity.Warning, Source, row.NotPlaceable ?? "The Scene does not place this asset.");
                break;
        }
    }

    /// <summary>A solar archetype spawns with the template of its type, without the archetype picker (decision D277). A
    /// station with a docking sphere then opens the base picker.</summary>
    private void SpawnArchetype(EditorSession session, string system, string archetype, Vector3 point, SectionRef? parent)
    {
        var edits = session.Edits;
        // An archetype without a template gives the reason of the plan.
        if (!SceneCommands.SpawnAsksForBase(edits, archetype))
        {
            Run(SceneCommands.SpawnAsset(edits, system, new AssetRef(AssetKind.SolarArchetype, archetype), point, parent));
            return;
        }

        PickBase(session, system, SceneCommands.SpawnTemplate(edits, archetype), archetype, point, parent);
    }

    /// <summary>
    /// A ship, an asteroid or a model spawns through a solar archetype of its model (decision D278): the picker lists the
    /// archetypes of W with the model, and its button "New static prop archetype" makes a new one (decision D279). Esc
    /// makes nothing.
    /// </summary>
    private void SpawnProp(EditorSession session, string system, AssetRow row, Vector3 point, SectionRef? parent)
    {
        var edits = session.Edits;
        if (edits.Assets.ModelOf(row.Asset) is not { } source)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, row.NotPlaceable ?? $"{row.Nickname} has no model.");
            return;
        }

        var name = row.Asset.Section != null && session.SectionName(row.Asset.Section) is { Length: > 0 } text
            ? text
            : row.Nickname;
        var existing = PropArchetypes.Existing(edits, source);
        picker.Show($"Create: prop of {name}",
            existing.Count == 0
                ? "No solar archetype has this model. Make a new static prop archetype, or press Esc to make nothing."
                : "Pick a solar archetype with this model, or make a new static prop archetype. Esc makes nothing.",
            ObjectKinds.OtherObject,
            existing.Select(a => (a.Entry, session.SectionName(a.Entry.Section!) is { Length: > 0 } archetypeName
                ? archetypeName
                : a.Nickname, a.Type)).ToList(),
            picked => SpawnArchetype(session, system, picked.Value, point, parent),
            "Cancel", null,
            "New static prop archetype",
            () => Run(SceneCommands.SpawnAsset(edits, system, row.Asset, point, parent)));
    }

    /// <summary>The menu item "New static prop archetype" of a tile: a new <c>[Solar]</c> of the model of a ship, an
    /// asteroid or a model, and an object of it at a point, without the picker (decision D279).</summary>
    public void NewProp(AssetRow row, Vector3 point)
    {
        if (window.Session is not { } session || window.Scene.System is not { } system)
            return;
        if (Unavailable(SceneAction.Create, null) is { } reason)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, reason);
            return;
        }

        if (session.Edits.Assets.ModelOf(row.Asset) == null)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, row.NotPlaceable ?? $"{row.Nickname} has no model.");
            return;
        }

        Run(SceneCommands.SpawnAsset(session.Edits, system.Nickname, row.Asset, point));
    }

    /// <summary>
    /// Places a prop group (decisions D288 and D290). When a planned line has an error, the dialog lists each such line
    /// and offers "Place without these lines" and Cancel.
    /// </summary>
    private void PlaceGroup(EditorSession session, string system, AssetRef asset, Vector3 point, SectionRef? parent)
    {
        var edits = session.Edits;
        if (PropGroups.Of(edits).FirstOrDefault(g => g.File == asset.File) is not { } group)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, $"The prop group {asset.Id} is not on disk now.");
            return;
        }

        var plan = PropGroupCommands.Place(edits, system, group, point, parent);
        if (plan.CanRun || plan.PlacementErrors.Count == 0)
        {
            Run(plan);
            return;
        }

        var message = "These lines name entries that this profile does not have. A line with an error cannot go into " +
                      "the mod:\n\n" + string.Join("\n", plan.PlacementErrors.Select(e => $"{e.Member}: {e.Message}"));
        window.Choice.Show($"Place {group.Name}", message,
        [
            new Choice("Place without these lines",
                () => Run(PropGroupCommands.Place(edits, system, group, point, parent, leaveOutErrorLines: true))),
            new Choice("Cancel", () => { }),
        ]);
    }

    /// <summary>
    /// Sets the loadout or the faction of a dropped tile on an object (decision D281 of the M23 design), as one entry of
    /// the edit history "Set loadout of &lt;object&gt;". It also replaces a present line (review finding R18).
    /// </summary>
    public void SetComponent(SectionRef section, AssetRow row) =>
        window.Commands.Run(edits => SceneCommands.DropAsset(edits, section, row.Asset));

    private bool MenuItem(char icon, string label, string shortcut, SceneAction action, object? item)
    {
        var unavailable = Unavailable(action, item);
        var clicked = ImGui.MenuItem($"{icon}   {label}", shortcut, false, unavailable == null);
        if (unavailable != null)
            ImGui.SetItemTooltip(unavailable);
        return clicked;
    }

    /// <summary>
    /// Creates an item (decisions D233 to D235): an object after the pick of its archetype, and of its base for a station,
    /// or a zone or a light at once. The item goes to the pivot of the Scene camera, or to the position of the object of
    /// the row. Then the new item is selected and its Hierarchy row starts its rename.
    /// </summary>
    public void Create(SceneTemplate template, SectionRef? row, ZoneShape shape = ZoneShape.SPHERE)
    {
        if (window.Session is not { } session || window.Scene.System is not { } system)
            return;
        var edits = session.Edits;
        var parent = row is { Id.Type: var type } && type.Equals("Object", StringComparison.OrdinalIgnoreCase) ? row : null;
        var position = parent != null && window.Scene.ItemOf(parent) is SystemObject obj ? obj.Position : window.Scene.Pivot;
        if (template is SceneTemplate.Zone or SceneTemplate.Light)
        {
            Run(SceneCommands.Create(edits, system.Nickname, template, null, position, shape: shape));
            return;
        }

        var archetypes = ScenePlans.Archetypes(edits, template);
        picker.Show($"Create: {template}", "Pick the archetype of the new object.", Kind(template),
            archetypes.Select(a => (a.Entry, session.ItemName(a.Entry.Section!) is { Length: > 0 } name ? name : a.Nickname,
                a.Type)).ToList(),
            archetype =>
            {
                if (!ScenePlans.AsksForBase(edits, template))
                {
                    Run(SceneCommands.Create(edits, system.Nickname, template, archetype.Value, position, parent));
                    return;
                }

                PickBase(session, system.Nickname, template, archetype.Value, position, parent);
            });
    }

    /// <summary>The base picker of a station (decision D263): the bases of the system first. Esc makes the station
    /// without a base. The station and its base are one entry of the edit history.</summary>
    private void PickBase(EditorSession session, string system, SceneTemplate template, string archetype, Vector3 position,
        SectionRef? parent)
    {
        var edits = session.Edits;
        var bases = ScenePlans.Bases(edits, system);
        picker.Show("Create: base", $"Pick the base that {archetype} docks with. Esc makes the station without a base.",
            ObjectKinds.Station,
            bases.Select(b => (b, BaseName(b), BaseSystem(edits, b))).ToList(),
            picked => Run(SceneCommands.Create(edits, system, template, archetype, position, parent, picked.Value)),
            "No base",
            () => Run(SceneCommands.Create(edits, system, template, archetype, position, parent)));

        string BaseName(ListEntry entry) =>
            entry.Section != null && session.ItemName(entry.Section) is { Length: > 0 } name ? name : entry.Value;
    }

    /// <summary>
    /// Adds a component to an object after the pick of its value (decision D244): a loadout, a base or a faction. The base
    /// picker lists the bases of the system first. One add is one entry of the edit history.
    /// </summary>
    public void AddComponent(SectionRef section, ObjectComponent kind)
    {
        if (window.Session is not { } session || window.Scene.System is not { } system)
            return;
        var edits = session.Edits;
        var list = ObjectComponents.ListOf(kind);
        var entries = list == "base"
            ? ScenePlans.Bases(edits, system.Nickname)
            : edits.Lists.List(list)?.Entries ?? [];
        picker.Show($"Add {ObjectComponents.Name(kind)}", $"Pick the {list} of {section.Id.Key}.", ObjectKinds.OfList(list),
            entries.Select(e => (e, e.Section != null && session.ItemName(e.Section) is { Length: > 0 } name ? name : e.Value,
                list == "base" ? BaseSystem(edits, e) : e.Detail)).ToList(),
            picked => window.Commands.Run(s => SceneCommands.SetComponent(s, section, kind, picked.Value)));
    }

    /// <summary>Removes a component of an object (decision D244). One remove is one entry of the edit history.</summary>
    public void RemoveComponent(SectionRef section, ObjectComponent kind) =>
        window.Commands.Run(edits => SceneCommands.SetComponent(edits, section, kind, null));

    /// <summary>Copies an item with its children (decisions D241 and D262). Unity does not ask, so the warnings go to the
    /// Console.</summary>
    public void Duplicate(SectionRef section)
    {
        if (window.Session is not { } session)
            return;
        Run(SceneCommands.Duplicate(session.Edits, [section]));
    }

    /// <summary>Deletes an item (decisions D81 and D238): at once when nothing else changes, else after the dialog. A
    /// refused delete shows its reason.</summary>
    public void Delete(SectionRef section)
    {
        if (window.Session is not { } session)
            return;
        var plan = SceneCommands.Delete(session.Edits, [section]);
        if (!plan.CanRun || plan.NeedsConfirmation)
        {
            dialog.Show(plan, () => Run(plan));
            return;
        }

        Run(plan);
    }

    /// <summary>Starts the rename field of the Hierarchy row of an item (decision D242). The Hierarchy shows the
    /// panel.</summary>
    public void BeginRename(SectionRef section)
    {
        if (window.Session is not { } session)
            return;
        // The item of a new section shows after the next flush of the scene, so the check reads the section.
        var reason = session.NoEditReason ?? (window.IsEditing ? "Wait until the field edit ends." : null) ??
                     ScenePlans.Unavailable(session.Edits, SceneAction.Rename, section);
        if (reason != null)
        {
            window.Log.Add(ConsoleSeverity.Warning, Source, reason, section);
            return;
        }

        Renaming = section;
        RenameText = ScenePlans.RenameTarget(session.Edits, section).Id.Key ?? "";
        RenameStarted = true;
        window.ShowPanel(Hierarchy.HierarchyPanel.PanelId);
    }

    /// <summary>Ends the rename field without a rename.</summary>
    public void CancelRename() => Renaming = null;

    /// <summary>Why the text of the rename field cannot be the new nickname, or null.</summary>
    public string? RenameProblem() =>
        Renaming != null && window.Session is { } session
            ? ScenePlans.RenameProblem(session.Edits, Renaming, RenameText)
            : null;

    /// <summary>Runs the rename of the field: at once, or after the dialog of its warnings. A text that is the old
    /// nickname ends the field and changes nothing.</summary>
    public void CommitRename()
    {
        if (Renaming is not { } section || window.Session is not { } session)
            return;
        Renaming = null;
        var target = ScenePlans.RenameTarget(session.Edits, section);
        if (string.Equals(target.Id.Key, RenameText.Trim(), StringComparison.Ordinal))
            return;
        var plan = SceneCommands.Rename(session.Edits, section, RenameText);
        if (plan.CanRun && plan.NeedsConfirmation)
            dialog.Show(plan, () => Run(plan));
        else
            Run(plan);
    }

    /// <summary>Runs a plan through the command runner, which tells why it cannot run and gives its warnings to the
    /// Console. After the run, the selection follows Unity (decision D249).</summary>
    private void Run(ScenePlan plan)
    {
        if (window.Commands.Run(plan).Status is not (EditStatus.Applied or EditStatus.NoChange))
            return;
        switch (plan.Action)
        {
            case SceneAction.Delete:
                window.Selection = null;
                break;
            case SceneAction.Create or SceneAction.Duplicate or SceneAction.Place when plan.SelectAfter is { } added:
                window.Scene.SelectWhenShown(added);
                // A new static prop archetype is not in the loaded game data, so the Hierarchy has no row for its object
                // until the reload (decision D282 of the M23 design).
                if (plan.NewArchetype is { } archetype)
                    window.Log.Add(ConsoleSeverity.Info, Source, $"The Scene shows {added.Id.Key} after the save. The game " +
                                                                 $"data then loads the new archetype {archetype.Id.Key}.",
                        added);
                else if (plan.Action == SceneAction.Create)
                    BeginRename(added);
                break;
            case SceneAction.Rename when plan.SelectAfter is { } renamed && window.Selection is SectionRef:
                window.Selection = renamed;
                break;
        }

        if (plan.Action == SceneAction.Delete && plan.Deletes.Count > 0)
            deleted[plan.Label] = plan.Deletes[0].Section;
    }

    /// <summary>The first target of each delete, by the label of its entry of the edit history, so the undo of a delete
    /// selects the item again (decision D249).</summary>
    private readonly System.Collections.Generic.Dictionary<string, SectionRef> deleted = [];

    /// <summary>Selects the item of a delete again after the undo of the delete, as Unity does. This is also true for an
    /// agent delete, which follow mode keeps (review finding of MCP1-2, step 8). Other undos change no
    /// selection.</summary>
    /// <param name="label">The label of the entry that the undo took back, or null.</param>
    public void Undone(string? label)
    {
        if (label == null)
            return;
        var section = deleted.TryGetValue(label, out var own) ? own : window.AgentFollow.DeletedItem(label);
        if (section != null && window.Session?.Edits.Get(section) != null)
            window.Scene.SelectWhenShown(section);
    }

    /// <summary>The system of a base, for the detail of the base picker.</summary>
    private static string BaseSystem(EditSession edits, ListEntry entry) =>
        entry.Section != null && edits.Get(entry.Section)?.Lines("system").LastOrDefault()?.Values is [var system, ..]
            ? system.ToString()
            : "";

    /// <summary>The kind of an object template, for its glyph.</summary>
    private static ObjectKind Kind(SceneTemplate template) => template switch
    {
        SceneTemplate.Station => ObjectKinds.Station,
        SceneTemplate.Planet => ObjectKinds.Planet,
        SceneTemplate.Sun => ObjectKinds.Sun,
        SceneTemplate.Satellite => ObjectKinds.Satellite,
        _ => ObjectKinds.OtherObject,
    };
}
