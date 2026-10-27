using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The asset writes of the prop groups (section 7.3 of the MCP1 design): <c>prop_group_save</c>,
/// <c>prop_group_rename</c> and <c>prop_group_delete</c>. They write the files of a group of the active mod at once,
/// outside the edit history. Unity writes a prefab asset in the same way (decisions D283 and D294 of the M23
/// design).
/// </summary>
/// <remarks>
/// <para>Undo cannot take an asset write back. So each one needs <c>confirm: true</c>, and its result says that the
/// change is not on the undo stack (section 8.3, item 6 of the MCP1 design). A preview gives the result and writes
/// nothing. An asset write is never a step of <c>edit_batch</c>, because no cancel of the batch can take it
/// back.</para>
/// <para>A group of another mod refuses a rename and a delete. A save writes only into the folder of the active mod
/// (see <see cref="PropGroupCommands"/>).</para>
/// </remarks>
internal static class PropGroupTools
{
    /// <summary>The arguments of an asset write: its own arguments, then <c>preview</c>, <c>if_revision</c> and
    /// <c>confirm</c>. An asset write changes no INI file, so it has no <c>with_patch</c>.</summary>
    public static ToolInput Input(params ToolParam[] own) =>
        new([.. own, EditTools.Preview, EditTools.IfRevision, EditTools.Confirm]);

    /// <summary>Saves items of a system with their children as a new prop group of the active mod (decision D291 of the
    /// M23 design). The group takes the lines of the copy builder of the Scene, and the result tells each line that the
    /// group leaves out.</summary>
    public static ToolStep Save(ToolCall call)
    {
        var session = Guarded(call);
        var args = call.Args;
        var items = ItemEditTools.Targets(call);
        var draft = PropGroups.Draft(session, items);
        var nickname = args.Text("nickname") ?? draft.SuggestedNickname;
        var name = args.Text("name") ?? "";
        string Ref(SectionRef section) => ItemRefs.Canonical(session, section);
        var shape = new EditResult(EditStatus.Applied)
        {
            Label = $"Save prop group {nickname}",
            Warnings = draft.LeftOut.Concat(draft.Warnings)
                .Select(d => Problem.From(d, d.Target is { } target ? Ref(target) : null)).ToList(),
            Notes =
            [
                AgentTexts.Error("group_members", ("count", draft.Members.Count),
                    ("members", string.Join(", ", draft.Members.Select(m => m.Member)))),
            ],
        };
        return Write(call, PropGroupCommands.SaveProblem(session, draft, nickname, name), shape,
            () => PropGroupCommands.Save(session, draft, nickname, name),
            group => shape with { Adds = [group == null ? GroupRef(nickname) : ItemQueries.GroupRef(group)] });
    }

    /// <summary>Gives a prop group of the active mod a new nickname (decision D294 of the M23 design). The file of the
    /// group gets the new name. The copies in the systems do not change.</summary>
    public static ToolStep Rename(ToolCall call)
    {
        var session = Guarded(call);
        var group = SceneEditTools.Group(call, "group");
        var nickname = call.Args.Text("nickname")!;
        var from = ItemQueries.GroupRef(group);
        var shape = new EditResult(EditStatus.Applied) { Label = $"Rename prop group {group.Nickname} to {nickname}" };
        return Write(call, PropGroupCommands.RenameProblem(session, group, nickname), shape,
            () => PropGroupCommands.Rename(session, group, nickname),
            renamed => shape with
            {
                Renames = [new ResultRename(from, renamed == null ? GroupRef(nickname) : ItemQueries.GroupRef(renamed))],
            });
    }

    /// <summary>Deletes a prop group of the active mod (decision D294 of the M23 design). The copies in the systems
    /// stay.</summary>
    public static ToolStep Delete(ToolCall call)
    {
        var session = Guarded(call);
        var group = SceneEditTools.Group(call, "group");
        var shape = new EditResult(EditStatus.Applied)
        {
            Label = $"Delete prop group {group.Nickname}",
            Deletes = [new ResultDelete(ItemQueries.GroupRef(group), DeleteReason.Target, null)],
        };
        return Write(call, PropGroupCommands.DeleteProblem(session, group), shape,
            () => PropGroupCommands.Delete(session, group), _ => shape);
    }

    /// <summary>The session of an asset write, after the checks of each edit call: the reason why the host takes no
    /// edits (<c>read_only</c>), then <c>if_revision</c>. The tool table checks Pause.</summary>
    private static EditSession Guarded(ToolCall call)
    {
        EditTools.ReadOnlyGuard(call);
        EditTools.Guard(call);
        return call.Session;
    }

    /// <summary>
    /// Gives the preview of an asset write, or asks for <c>confirm: true</c>, or writes. The host learns each write for
    /// its Console. A problem of the write refuses before the preview, so a preview tells each refusal of the
    /// write.
    /// </summary>
    /// <param name="problem">Why the write cannot run, or null.</param>
    /// <param name="shape">The result without the group, for its label.</param>
    /// <param name="write">Writes the files.</param>
    /// <param name="written">The result after the write, from the group that the write gives.</param>
    /// <exception cref="EditException">The write refuses, or the call has no <c>confirm: true</c>.</exception>
    private static ToolResult Write(ToolCall call, EditError? problem, EditResult shape, Func<PropGroupWrite> write,
        Func<PropGroup?, EditResult> written)
    {
        if (problem != null)
            throw new EditException(problem);
        if (call.Args.Flag("preview"))
        {
            return ToolResult.Of(written(null) with
            {
                Status = EditStatus.Previewed,
                NeedsConfirmation = true,
                Summary = AgentTexts.Error("asset_write_previewed"),
            });
        }
        if (!call.Args.Flag("confirm"))
        {
            throw new EditException(new EditError(EditErrorKind.NeedsConfirmation, EditTools.ConfirmChanges,
                AgentTexts.Error("asset_write_confirm", ("label", shape.Label ?? call.Tool))));
        }
        var done = write();
        if (done.Error is { } error)
            throw new EditException(error);
        var result = written(done.Group) with
        {
            Summary = AgentTexts.Error("asset_write_applied", ("label", shape.Label ?? call.Tool)),
        };
        call.Host.Edited(call.Tool, result);
        return ToolResult.Of(result);
    }

    /// <summary>The reference of a group of the active mod with a nickname.</summary>
    private static string GroupRef(string nickname) => ItemRefs.Format(new ItemReference(ItemRefKind.Group, nickname));
}
