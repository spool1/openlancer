using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;

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
}

/// <summary>What the panels did in one frame, for follow mode. The window reads it before and after the panels
/// draw.</summary>
/// <param name="SelectionBefore">The selection before the panels drew.</param>
/// <param name="SelectionAfter">The selection after the panels drew.</param>
/// <param name="CameraBefore">The pose and the fly speed of the Scene camera before the panels drew.</param>
/// <param name="CameraAfter">The pose and the fly speed of the Scene camera after the panels drew.</param>
/// <param name="Input">True when the user clicked or pressed a key that moves the selection in the frame.</param>
internal readonly record struct FollowFrame(
    object? SelectionBefore,
    object? SelectionAfter,
    (Vector3 Position, Quaternion Orientation, float Speed) CameraBefore,
    (Vector3 Position, Quaternion Orientation, float Speed) CameraAfter,
    bool Input)
{
    /// <summary>True when the user selected an item or moved the Scene camera in the frame. An agent call runs before
    /// the panels, so a selection of <c>select_items</c> or of follow mode is not a change here. The Scene selects the
    /// item of a pending section in a frame without input, so such a change is not a selection of the user.</summary>
    public bool UserActed => CameraBefore != CameraAfter || Input && !Equals(SelectionBefore, SelectionAfter);
}

/// <summary>
/// Follow mode of the agent edits (section 8.5 of the MCP1 design, decisions D719 and D720). With follow mode on, the
/// editor opens the system of each agent edit and frames its item. The selection then follows Unity (decision D249): a
/// create or a duplicate selects the new item, a delete clears the selection, and a rename keeps it. An item without a
/// place in the Scene, for example a loadout, shows in the Inspector, and the view does not change. A camera input or a
/// selection of the user stops follow mode until the user turns it on again. With follow mode off, the agent never
/// changes the view or the selection. Only a selection of a section that an agent edit removed goes, as after an undo.
/// </summary>
/// <param name="settings">The setting "Follow agent". It keeps the last value that the user clicked.</param>
internal sealed class AgentFollow(AgentSettings settings, IFollowView view)
{
    /// <summary>The label of the menu item.</summary>
    internal const string Label = "Follow agent";

    /// <summary>The tooltip of the menu item (section 10 of the MCP1 design).</summary>
    internal const string Tip =
        "Opens and frames the item of each agent edit. Follow mode stops when you move the camera or select an item.";

    /// <summary>The first removed item of each agent delete, by the label of its entry, so an undo of the delete selects
    /// the item again (decision D249).</summary>
    private readonly Dictionary<string, string> deleted = [];

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
    /// selection.
    /// </summary>
    public void Edited(EditResult result)
    {
        if (session == null)
            return;
        if (result.UndoLabel is { } label &&
            (result.Deletes.FirstOrDefault(d => d.Why == DeleteReason.Target) ?? result.Deletes.FirstOrDefault()) is { } first)
        {
            deleted[label] = first.Ref;
        }
        // A write of a prop group changes an asset, not the edit view, so it is not an entry of the history. Its delete
        // keeps the selection, as a delete in the Unity Project window keeps the selection of the Scene.
        if (!On || result.UndoLabel == null)
            return;
        if (result.Adds.Count == 0 && result.Deletes.Count > 0)
        {
            view.Selection = null;
            return;
        }
        if (result.Adds.Count > 0)
            Show(result.Adds, select: true);
        else
            Show(result.Renames.Select(r => r.To).Concat(result.Changes), select: false);
    }

    /// <summary>Follows an undo or a redo of the agent. With follow mode on, the undo of an agent delete selects its
    /// first item again, as the undo of a delete of the user does (decision D249). Other steps change no
    /// selection.</summary>
    public void Stepped(string tool, string label)
    {
        if (On && tool == "undo" && deleted.TryGetValue(label, out var reference))
            Show([reference], select: true);
    }

    /// <summary>
    /// The first item of the agent delete with the label of an entry, when the edit view has the item again, else null.
    /// The undo of the user selects it, as the undo of a delete of the user does (decision D249). This works also with
    /// follow mode off, because the user asked for the undo.
    /// </summary>
    /// <param name="label">The label of the entry that the undo reversed.</param>
    public SectionRef? DeletedItem(string label) =>
        session != null && deleted.TryGetValue(label, out var reference) ? SectionOf(reference) : null;

    /// <summary>
    /// Shows the first item of an edit: in the Scene for an item of a system, else in the Inspector. An item of a system
    /// that the Scene cannot show also shows in the Inspector.
    /// </summary>
    /// <param name="select">True to select an item of the Scene. An item without a place in the Scene always gets the
    /// selection, because the Inspector shows only the selection.</param>
    private void Show(IEnumerable<string> references, bool select)
    {
        SectionRef? inspector = null;
        foreach (var reference in references)
        {
            if (SectionOf(reference) is not { } section)
                continue;
            if (session!.IsSystemFile(section.Path) && session.SystemNicknameOf(section.Path) is { } system &&
                view.ShowInScene(section, system, select))
            {
                return;
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
