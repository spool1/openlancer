using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Missions;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>What follow mode needs from the editor window. The window gives it, and the tests give a fake.</summary>
internal interface IFollowView
{
    /// <summary>The selected item of the editor.</summary>
    object? Selection { get; set; }

    /// <summary>The section of the edit view that a selected item shows, or null.</summary>
    SectionRef? SectionOf(object? item);

    /// <summary>
    /// Opens a system in the Scene, shows the Scene and moves the camera to the item of a section. With
    /// <paramref name="select"/>, it also selects the item, as a click does. The open of another system clears the
    /// selection, as the system list of the Scene does.
    /// </summary>
    /// <returns>False when the Scene cannot show the item, for example while the game data loads again.</returns>
    bool ShowInScene(SectionRef section, string system, bool select);

    /// <summary>
    /// Shows the save card of a new system in the Scene, and shows the Scene. A new system is a system that the edit view
    /// has and the loaded game data does not have yet. The Scene opens it after the save and the reload of the game
    /// data.
    /// </summary>
    /// <returns>False for a system of the loaded game data, or for a system that the edit view does not have.</returns>
    bool ShowNewSystem(string system);

    /// <summary>True while the Mission tab shows a prefab in prefab mode (section 8.6 of the M6b design). Follow mode does
    /// not leave prefab mode, so an agent edit of a mission then shows only in the Console.</summary>
    bool InPrefabMode { get; }

    /// <summary>
    /// Opens the script of a mission item in the Mission tab, shows the tab and moves the graph to the item. With
    /// <paramref name="select"/>, it also selects the item, as a click does. The open of another script selects the root
    /// of that script, as the open of a mission does.
    /// </summary>
    /// <param name="script">The install path of the script.</param>
    /// <param name="reference">An item reference of a result in the script: the script, a trigger, a section, a line, a
    /// label, an act or a prefab instance.</param>
    /// <returns>False when the Mission tab cannot show the item, for example a section of a script that the mission run
    /// did not read yet.</returns>
    bool ShowInMission(string script, string reference, bool select);

    /// <summary>
    /// Opens a cutscene in the Cutscene tab and shows the tab and the Timeline. With <paramref name="select"/>, it also
    /// makes the selection, or selects the cutscene when <paramref name="selection"/> is null, as a click does. The open
    /// of another cutscene selects that cutscene, as the open of a cutscene does. The Cutscene view keeps its mode and its
    /// camera.
    /// </summary>
    /// <param name="path">The install path of the cutscene, in the form that the session stores.</param>
    /// <param name="selection">An entity, an event or a set of events of the cutscene (<see cref="ThnEventSet"/>), or
    /// null for the cutscene.</param>
    /// <returns>False when the Cutscene tab cannot open the cutscene, for example while the game data loads.</returns>
    bool ShowInCutscene(string path, object? selection, bool select);
}

/// <summary>What the panels did in one frame, for follow mode. The window reads it before and after the panels
/// draw.</summary>
/// <param name="SelectionBefore">The selection before the panels drew.</param>
/// <param name="SelectionAfter">The selection after the panels drew.</param>
/// <param name="CameraBefore">The pose and the fly speed of the Scene camera before the panels drew.</param>
/// <param name="CameraAfter">The pose and the fly speed of the Scene camera after the panels drew.</param>
/// <param name="Input">True when the user clicked or pressed a key that moves the selection in the frame.</param>
/// <param name="GraphMoved">True when the user panned or zoomed the graph of the Mission tab in the frame.</param>
internal readonly record struct FollowFrame(
    object? SelectionBefore,
    object? SelectionAfter,
    (Vector3 Position, Quaternion Orientation, float Speed) CameraBefore,
    (Vector3 Position, Quaternion Orientation, float Speed) CameraAfter,
    bool Input,
    bool GraphMoved = false)
{
    /// <summary>True when the user selected an item, moved the Scene camera or moved the view of the mission graph in
    /// the frame (final finding 10 of MCP1-3). An agent call runs before the panels, so a selection of
    /// <c>select_items</c> or of follow mode is not a change here. The Scene selects the item of a pending section in a
    /// frame without input, so such a change is not a selection of the user.</summary>
    public bool UserActed => CameraBefore != CameraAfter || GraphMoved || Input && !Equals(SelectionBefore, SelectionAfter);
}

/// <summary>
/// Follow mode of the agent edits (section 8.5 of the MCP1 design, decisions D719 and D720). With follow mode on, the
/// editor opens the system of each agent edit and frames its item. An edit of a mission opens its script in the Mission
/// tab and frames the item in the graph. An edit of a cutscene opens its script in the Cutscene tab. The selection then
/// follows Unity (decision D249): a create or a duplicate selects the new item, a delete clears the selection, and a
/// rename keeps it. In a cutscene, the selection is the same as after an edit of the user in the Timeline
/// (<see cref="CutsceneWorkspace.SelectionAfter"/>): the new events, else the last new entity. A delete in a cutscene
/// selects the cutscene. An item without a place in the Scene, for example a loadout, shows in the Inspector, and the
/// view does not change. Follow mode does not leave prefab mode, as Unity keeps prefab mode open when the scene changes.
/// An edit of a mission then shows only in the Console. A camera input, a pan or a zoom of the mission graph, or a
/// selection of the user stops follow mode until the user turns it on again. An edit in a new system that the loaded game
/// data does not have yet shows the save card of the Scene and selects the <c>[system]</c> of the system. With follow
/// mode off, the agent never changes the view or the selection. Only a selection of a section that an agent edit removed
/// goes, as after an undo. A selected item keeps its item when an agent edit renames it or moves its row.
/// </summary>
/// <param name="settings">The setting "Follow agent". It keeps the last value that the user clicked.</param>
internal sealed class AgentFollow(AgentSettings settings, IFollowView view)
{
    /// <summary>The label of the menu item.</summary>
    internal const string Label = "Follow agent";

    /// <summary>The tooltip of the menu item (section 10 of the MCP1 design).</summary>
    internal const string Tip =
        "Opens and frames the item of each agent edit. Follow mode stops when you move the camera of the Scene or the " +
        "mission graph, or select an item.";

    /// <summary>The first removed item of each agent delete, by the id of its entry of the history (see
    /// <see cref="EditSession.UndoEntryId"/>), so an undo of the delete selects the item again (decision D249). Two
    /// cutscene deletes can have one label, for example "Agent: Delete event", so the label cannot name the
    /// entry.</summary>
    private readonly Dictionary<long, string> deleted = [];

    private EditSession? session;

    /// <summary>True when a camera input or a selection of the user stopped follow mode in this run of the
    /// editor.</summary>
    private bool stopped;

    /// <summary>True while follow mode is on. The check box of the menu shows it.</summary>
    public bool On => settings.FollowAgent && !stopped;

    /// <summary>The click of the user on the check box "Follow agent". The setting keeps the value. The caller saves
    /// the settings.</summary>
    public void Set(bool on)
    {
        settings.FollowAgent = on;
        stopped = false;
    }

    /// <summary>Stops follow mode for this run of the editor, at a camera input or a selection of the user. The setting
    /// keeps its value, so the next start of the editor does not surprise the user.</summary>
    public void Stop() => stopped = true;

    /// <summary>Watches the input of the user in one frame, and stops follow mode at a selection or a camera input of the
    /// user. The window calls it after the panels draw.</summary>
    public void Watch(FollowFrame frame)
    {
        if (On && frame.UserActed)
            Stop();
    }

    /// <summary>Follows the changes of the session of the open profile. Call it in each frame before the agent calls
    /// run.</summary>
    public void Track(EditSession? edits)
    {
        if (ReferenceEquals(edits, session))
            return;
        if (session != null)
            session.Changed -= OnChanged;
        session = edits;
        deleted.Clear();
        if (session != null)
            session.Changed += OnChanged;
    }

    /// <summary>
    /// Follows an applied agent edit, after its commit. A delete clears the selection and keeps its first item for an
    /// undo. A create or a duplicate selects and frames the new item. A rename or a change frames the item and keeps the
    /// selection. A selected line of a trigger keeps its line when the edit moves its row, also with follow mode off.
    /// </summary>
    public void Edited(EditResult result)
    {
        if (session == null)
            return;
        // The call committed just before, so its entry is the top entry of the history.
        if (result.UndoLabel != null && session.UndoEntryId is { } entry &&
            (result.Deletes.FirstOrDefault(d => d.Why == DeleteReason.Target) ?? result.Deletes.FirstOrDefault()) is { } first)
        {
            deleted[entry] = first.Ref;
        }
        // The Mission tab moves the selection with a rename of the agent. A row move of the agent moves it here, as a row
        // move of the user does (section 8.10 of the M6b design).
        if (result is EditToolResult { RowMoves: { Count: > 0 } moves } &&
            MissionWorkspace.Followed(view.Selection, [], moves) is { } line)
        {
            view.Selection = line;
        }
        // A write of a prop group changes an asset, not the edit view, so it is not an entry of the history. Its delete
        // keeps the selection, as a delete in the Unity Project window keeps the selection of the Scene.
        if (!On || result.UndoLabel == null)
            return;
        if (result.Adds.Count == 0 && result.Deletes.Count > 0)
        {
            // A delete in a cutscene selects the cutscene, as a delete of the user in the Timeline does
            // (CutsceneWorkspace.SelectionAfter).
            var target = result.Deletes.FirstOrDefault(d => d.Why == DeleteReason.Target) ?? result.Deletes[0];
            if (CutsceneItemOf(target.Ref) is var (path, _) && view.ShowInCutscene(path, null, select: true))
                return;
            // Follow mode does not leave prefab mode, so a delete of items of mission files keeps the selection of prefab
            // mode.
            if (!(view.InPrefabMode && result.Deletes.All(d => MissionFileOf(d.Ref) != null)))
                view.Selection = null;
            return;
        }
        if (result.Adds.Count > 0)
            Show(result.Adds, select: true);
        else
            Show([.. result.Renames.Select(r => r.To), .. result.Changes], select: false);
    }

    /// <summary>Follows an undo or a redo of the agent. With follow mode on, the undo of an agent delete selects its
    /// first item again, as the undo of a delete of the user does (decision D249). Other steps change no
    /// selection.</summary>
    /// <param name="tool"><c>undo</c> or <c>redo</c>. Call it right after the step.</param>
    public void Stepped(string tool)
    {
        if (On && tool == "undo" && UndoneDelete() is { } reference)
            Show([reference], select: true);
    }

    /// <summary>
    /// The first item of the agent delete that the last undo took back, when the edit view has the item again, else
    /// null. The undo of the user selects it, as the undo of a delete of the user does (decision D249). This works also
    /// with follow mode off, because the user asked for the undo. An item of a mission file gives null: the undo of a
    /// mission edit of the user keeps the selection of the Mission tab, so the undo of an agent edit does too.
    /// </summary>
    /// <remarks>Call it right after the undo. The entry that the undo took back is then the top entry of the redo
    /// stack (see <see cref="EditSession.RedoEntryId"/>).</remarks>
    public SectionRef? DeletedItem() =>
        UndoneDelete() is { } reference && MissionFileOf(reference) == null ? SectionOf(reference) : null;

    /// <summary>The first item of the agent delete that the last undo took back, or null when that entry is not an
    /// agent delete.</summary>
    private string? UndoneDelete() =>
        session?.RedoEntryId is { } entry && deleted.TryGetValue(entry, out var reference) ? reference : null;

    /// <summary>
    /// Shows the first item of an edit: in the Cutscene tab for a cutscene or an element of a cutscene, in the Mission
    /// tab for an item of a mission script, in the Scene for an item of a system, else in the Inspector. An item of a new
    /// system that the loaded game data does not have yet shows the save card of the Scene, and the Inspector shows the
    /// <c>[system]</c> of the system. An item that the Mission tab or the Scene cannot show also shows in the Inspector,
    /// for example a section of a ship build file. In prefab mode, an item of a mission file does not show.
    /// </summary>
    /// <param name="select">True to select an item of the Cutscene tab, the Scene or the Mission tab. In the Cutscene
    /// tab, the selection is that of all the elements of the cutscene in <paramref name="references"/> (see
    /// <see cref="SelectionIn"/>). An item that shows only in the Inspector always gets the selection, because the
    /// Inspector shows only the selection.</param>
    private void Show(IReadOnlyList<string> references, bool select)
    {
        SectionRef? inspector = null;
        foreach (var reference in references)
        {
            if (CutsceneItemOf(reference) is var (cutscene, element))
            {
                if (view.ShowInCutscene(cutscene, select ? SelectionIn(cutscene, references) : element, select))
                    return;
                continue;
            }
            if (MissionFileOf(reference) is var (path, script))
            {
                // Follow mode does not leave prefab mode, so the edit shows only in the Console.
                if (view.InPrefabMode)
                    continue;
                if (script && view.ShowInMission(path, reference, select))
                    return;
            }
            if (SectionOf(reference) is not { } section)
                continue;
            if (session!.IsSystemFile(section.Path) && session.SystemNicknameOf(section.Path) is { } system)
            {
                if (view.ShowInScene(section, system, select))
                    return;
                // The Scene opens a new system only after the save, so the Inspector shows its [system].
                if (view.ShowNewSystem(system) && session.SystemSection(system) is { } entry)
                {
                    view.Selection = entry;
                    return;
                }
            }
            inspector ??= section;
        }
        if (inspector != null)
            view.Selection = inspector;
    }

    /// <summary>
    /// Keeps the selection of a section of an agent change: a selected section follows its rename, and a selected
    /// section that the change removed goes (decision D720). The Scene does the same for its items when it applies the
    /// change. This runs also with follow mode off.
    /// </summary>
    private void OnChanged(EditChange change)
    {
        if (change.Origin != EditOrigin.Agent || session == null)
            return;
        if (view.Selection is SectionRef selected)
        {
            foreach (var (from, to) in change.Renames)
            {
                if (from.Equals(selected))
                    view.Selection = selected = to;
            }
        }
        // An item of the Scene keeps its old section until the Scene applies the change, and the Scene keeps the item of
        // a renamed section.
        if (view.SectionOf(view.Selection) is { } section && session.Get(section) == null &&
            !change.Renames.Any(r => r.From.Equals(section)))
        {
            view.Selection = null;
        }
    }

    /// <summary>
    /// The mission file of an item reference of a result, or null for an item outside the mission files. A script, a
    /// trigger, a line, a label, a prefab instance and an act are items of a script. A section is an item of a mission
    /// file when its file is a script or the ship build file of a script.
    /// </summary>
    /// <returns>The install path of the file, and true when the file is a script that the Mission tab opens.</returns>
    private (string Path, bool Script)? MissionFileOf(string reference)
    {
        try
        {
            return ItemRefs.Parse(reference) switch
            {
                {
                    Kind: ItemRefKind.Script or ItemRefKind.Trigger or ItemRefKind.Line or ItemRefKind.Label or ItemRefKind.Instance
                    or ItemRefKind.Act,
                    Path: { } path,
                } => (path, true),
                // The check of the file is fast. So only a section of a mission file reads the list of the scripts, not a
                // section of a system, for example.
                { Kind: ItemRefKind.Section, Path: { } path } when session!.Missions.IsMissionFile(path) =>
                    (path, session.Missions.ScriptPaths().Contains(path, VfsPath.Comparer)),
                _ => null,
            };
        }
        catch (Exception e) when (e is EditException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The cutscene of a <c>thn:</c> reference of a result, with its entity or its event in W now, or null for another
    /// reference and for a cutscene that the edit view does not have. The reference of an entity or an event that W does
    /// not have gives the cutscene. An example is the index of an event that a delete gives: it is the index before the
    /// delete.
    /// </summary>
    private (string Path, ThnRef? Element)? CutsceneItemOf(string reference)
    {
        try
        {
            var parsed = ItemRefs.Parse(reference);
            if (parsed.Kind is not (ItemRefKind.Cutscene or ItemRefKind.CutsceneEntity or ItemRefKind.CutsceneEvent))
                return null;
            var path = ItemRefs.ResolveCutscene(session!, parsed);
            if (parsed.Kind == ItemRefKind.Cutscene)
                return (path, null);
            try
            {
                return (path, ItemRefs.ResolveCutsceneElement(session!, parsed));
            }
            catch (EditException)
            {
                return (path, null);
            }
        }
        catch (Exception e) when (e is EditException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The selection of the elements of a cutscene in the items to select, as the Timeline selects the new elements of
    /// an edit of the user (<see cref="CutsceneWorkspace.SelectionOfAdded"/>). Examples are the two copies of a duplicate
    /// of two events, and the new entity of a batch that makes a script and adds the entity.
    /// </summary>
    /// <returns>The events, else the last entity, else null for the cutscene, for example for a new script.</returns>
    private object? SelectionIn(string path, IEnumerable<string> references) =>
        CutsceneWorkspace.SelectionOfAdded(path, [.. references.Select(CutsceneItemOf)
            .Where(item => item is { Element: not null } && VfsPath.Comparer.Equals(item.Value.Path, path))
            .Select(item => item!.Value.Element!)]);

    /// <summary>The section of an item reference of a result, or null when the edit view has no such section, for
    /// example for a prop group.</summary>
    private SectionRef? SectionOf(string reference)
    {
        try
        {
            return ItemRefs.ResolveSection(session!, ItemRefs.Parse(reference));
        }
        catch (Exception e) when (e is EditException or ArgumentException)
        {
            return null;
        }
    }
}
