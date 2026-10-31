using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The result of <c>undo</c> and <c>redo</c>: the step of the history that the tool undid or
/// redid.</summary>
/// <param name="Label">The label of the step, for example "Agent: Delete my_station_1".</param>
internal sealed record HistoryStepView(string Label);

/// <summary>
/// The tools <c>undo</c> and <c>redo</c> of section 7.6 of the MCP1 design (decision D709). The agent undoes only an
/// entry with the edit origin Agent at the top of the history. It redoes only an agent entry that it undid itself. So
/// the agent never undoes an edit of the user, and never does again an edit that the user undid with Ctrl+Z. The
/// user can undo and redo each agent entry, as each other entry.
/// </summary>
internal static class HistoryTools
{
    public static ToolStep Undo(ToolCall call)
    {
        var session = call.Session;
        EditTools.ReadOnlyGuard(call);
        if (session.UndoOrigin is not { } origin)
            throw Refused("NothingToUndo", "undo_nothing");
        if (origin != EditOrigin.Agent)
            throw NotAgent(origin, "undo", session.UndoLabel!);
        var label = session.UndoLabel!;
        Step(() => session.Undo(EditOrigin.Agent));
        call.Host.Stepped(call.Tool, label);
        return ToolResult.Of(new HistoryStepView(label));
    }

    public static ToolStep Redo(ToolCall call)
    {
        var session = call.Session;
        EditTools.ReadOnlyGuard(call);
        if (session.RedoOrigin is not { } origin)
            throw Refused("NothingToRedo", "redo_nothing");
        if (origin != EditOrigin.Agent)
            throw NotAgent(origin, "redo", session.RedoLabel!);
        if (session.RedoUndoneBy != EditOrigin.Agent)
            throw NotAgentEntry("UserUndid", "redo_user_undid", session.RedoLabel!);
        var label = session.RedoLabel!;
        Step(session.Redo);
        call.Host.Stepped(call.Tool, label);
        return ToolResult.Of(new HistoryStepView(label));
    }

    /// <summary>Runs an undo or a redo. The session refuses it while a batch or a transaction is open.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Busy"/> with the reason of the session.</exception>
    private static void Step(Action step)
    {
        try
        {
            step();
        }
        catch (InvalidOperationException e)
        {
            throw new EditException(new EditError(EditErrorKind.Busy, Data.Missions.Editing.MissionErrors.Busy, e.Message));
        }
    }

    private static EditException Refused(string detail, string text) =>
        new(new EditError(EditErrorKind.Refused, detail, AgentTexts.Error(text)));

    /// <summary>The refusal of an entry that is not an agent entry. An entry of the restore of the recovery file has its
    /// own detail and text, because the user did not make it with an edit.</summary>
    /// <param name="tool">undo or redo.</param>
    private static EditException NotAgent(EditOrigin origin, string tool, string label) =>
        origin == EditOrigin.Recovery
            ? NotAgentEntry("RecoveryEntry", tool + "_recovery_entry", label)
            : NotAgentEntry("UserEntry", tool + "_user_entry", label);

    private static EditException NotAgentEntry(string detail, string text, string label) =>
        new(new EditError(EditErrorKind.NotAgentEntry, detail, AgentTexts.Error(text, ("label", label))));
}
