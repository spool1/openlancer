using System;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>What the editor does now, for the waits of the agent calls (section 5.5, item 3 of the MCP1 design).</summary>
/// <param name="Editing">A field edit, a drag of the gizmo or a drag of the Timeline is open. So is a gesture of the
/// mission studio, for example a rename of a graph node or a drag of a node.</param>
/// <param name="Dialog">A dialog of the editor is open.</param>
/// <param name="Loading">The game data loads, or loads again after a save.</param>
/// <param name="UserEdit">A batch or a transaction of the user is open.</param>
/// <param name="Renaming">The rename field of the Hierarchy is open, for example after a create of the user, or the
/// rename popup of a mission row. Its rename applies to the item of its start, so an agent edit must not change that item
/// first.</param>
internal readonly record struct EditorActivity(bool Editing, bool Dialog, bool Loading, bool UserEdit, bool Renaming = false);

/// <summary>The gestures of the user in the mission studio that an agent call waits for (section 5.5, item 3 of the MCP1
/// design).</summary>
/// <param name="TreeRename">The rename popup of a row of a mission in the Hierarchy is open.</param>
/// <param name="Graph">The graph has an open rename of a node or quick add, or the user drags a node or a link.</param>
/// <param name="Drag">The user drags an item of the library, an action row of the graph or a row of the tree of a
/// mission.</param>
/// <param name="Page">The rename popup or quick add of the Mission page in the Inspector is open.</param>
/// <param name="PrefabRename">The rename popup of a prefab in the Assets panel is open.</param>
internal readonly record struct MissionGestures(bool TreeRename, bool Graph, bool Drag, bool Page, bool PrefabRename)
{
    /// <summary>The activity of the editor with these gestures. The rename popup of a row of a mission is a rename in the
    /// Hierarchy. Each other gesture is an edit.</summary>
    public EditorActivity AddTo(EditorActivity activity) => activity with
    {
        Editing = activity.Editing || Graph || Drag || Page || PrefabRename,
        Renaming = activity.Renaming || TreeRename,
    };
}

/// <summary>
/// The UI-thread side of the agent calls of the editor (decision D708): the queue of the calls, the host, and the
/// waits. The HTTP host puts each call into the queue on a worker thread. The editor calls <see cref="Frame"/> once in
/// each frame, before the panels draw, so the session part of at most one call runs in each frame. It never runs inside
/// a draw or during a gesture of the user.
/// </summary>
internal sealed class AgentDispatcher : IDisposable
{
    private readonly Func<EditorActivity> activity;

    /// <param name="activity">What the editor does now. The window gives it.</param>
    /// <param name="audit">The audit log of this run of the editor, or null.</param>
    /// <param name="clock">The clock of the waits. The tests give a fake clock.</param>
    public AgentDispatcher(AgentHost host, Func<EditorActivity> activity, AuditLog? audit, TimeProvider? clock = null)
    {
        this.activity = activity;
        Host = host;
        Queue = new ToolQueue(host, clock, audit);
    }

    public AgentHost Host { get; }

    public ToolQueue Queue { get; }

    /// <summary>Runs the session part of the first call, or ends the calls that waited too long. On the UI
    /// thread.</summary>
    /// <returns>True when a session part ran.</returns>
    public bool Frame() => Queue.RunNext(WaitReason(activity()));

    /// <summary>Why a call waits now, in strict STE, or null when the call can run. The reason is the start of the error
    /// <c>busy</c> after the wait limit. The texts are in <c>agent-tools.json</c> (decision D726).</summary>
    internal static string? WaitReason(EditorActivity now) =>
        now.Editing ? AgentTexts.Error("wait_editing")
        : now.Renaming ? AgentTexts.Error("wait_renaming")
        : now.Dialog ? AgentTexts.Error("wait_dialog")
        : now.Loading ? AgentTexts.Error("wait_loading")
        : now.UserEdit ? AgentTexts.Error("wait_user_edit")
        : null;

    public void Dispose() => Queue.Dispose();
}
