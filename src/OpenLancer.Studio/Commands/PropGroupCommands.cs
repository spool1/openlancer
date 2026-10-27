using System;
using System.IO;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Commands;

/// <summary>What a write of the files of a prop group did: the group after the write, or why nothing changed.</summary>
/// <param name="Group">The group after a save or a rename, or null after a delete or a refusal.</param>
/// <param name="Error">Why the write did not run, or null when it ran.</param>
public sealed record PropGroupWrite(PropGroup? Group, EditError? Error)
{
    /// <summary>True when the write ran.</summary>
    public bool Written => Error == null;
}

/// <summary>
/// The commands of the prop groups (section 4.0.8 of the M6b design): the placement of a group in a system, and the
/// save, the rename and the delete of a group of the active mod. See <see cref="PropGroups"/>.
/// </summary>
/// <remarks>
/// <para>A placement is a plan with one entry of the edit history (decision D288 of the M23 design).</para>
/// <para>A save, a rename and a delete are asset writes: they change the files of the group at once, outside the edit
/// history, as Unity writes a prefab asset (decisions D283 and D294). No cancel of a batch can take them back, so they
/// refuse while a batch is open (section 4.0.4, item 9 of the M6b design). They write only into the folder of the
/// active mod.</para>
/// </remarks>
public static class PropGroupCommands
{
    /// <summary>The detail code of an asset write in an open batch.</summary>
    public const string AssetWriteInBatch = "AssetWriteInBatch";

    /// <summary>The detail code of a write of a group of another mod.</summary>
    public const string OtherMod = "OtherMod";

    /// <summary>The detail code of a nickname or a name that is not valid.</summary>
    public const string BadName = "BadName";

    /// <summary>The detail code of a write that the disk refused.</summary>
    public const string WriteFailed = "WriteFailed";

    /// <summary>The detail code of a nickname that another group of the active mod has.</summary>
    public const string NicknameTaken = "NicknameTaken";

    /// <summary>
    /// Plans a placement of a group in a system (decisions D288 and D290 of the M23 design). See
    /// <see cref="PropGroups.Place"/>.
    /// </summary>
    /// <param name="anchor">The point of the drop. The anchor of the group goes there.</param>
    /// <param name="parent">The object that gets each root object as a child, or null.</param>
    /// <param name="leaveOutErrorLines">True to leave out each line with an error and place the rest.</param>
    public static ScenePlan Place(EditSession session, string system, PropGroup group, Vector3 anchor,
        SectionRef? parent = null, bool leaveOutErrorLines = false) =>
        PropGroups.Place(session, system, group, anchor, parent, leaveOutErrorLines);

    /// <summary>Writes a draft as a new group of the active mod (decision D283). See
    /// <see cref="PropGroups.Write"/>.</summary>
    public static PropGroupWrite Save(EditSession session, PropGroupDraft draft, string nickname, string name) =>
        SaveProblem(session, draft, nickname, name) is { } problem
            ? new PropGroupWrite(null, problem)
            : Write(() => PropGroups.Write(session, draft, nickname, name));

    /// <summary>Why <see cref="Save"/> cannot write a draft, or null. A preview of an agent reads it and writes
    /// nothing.</summary>
    public static EditError? SaveProblem(EditSession session, PropGroupDraft draft, string nickname, string name)
    {
        if (Refusal(session) is { } refused)
            return refused;
        if (draft.Refusals.Count > 0)
            return Error(EditErrorKind.Refused, EditPlan.PlanRefused, draft.Refusals[0].Message);
        if (NicknameRefusal(session, nickname, null) is { } bad)
            return bad;
        return PropGroups.NameProblem(name) is { } nameProblem
            ? Error(EditErrorKind.InvalidValue, BadName, nameProblem) with { Field = "name" }
            : null;
    }

    /// <summary>Gives a group of the active mod a new nickname (decision D294). See
    /// <see cref="PropGroups.Rename"/>.</summary>
    public static PropGroupWrite Rename(EditSession session, PropGroup group, string nickname) =>
        RenameProblem(session, group, nickname) is { } problem
            ? new PropGroupWrite(null, problem)
            : Write(() => PropGroups.Rename(session, group, nickname));

    /// <summary>Why <see cref="Rename"/> cannot rename a group, or null.</summary>
    public static EditError? RenameProblem(EditSession session, PropGroup group, string nickname) =>
        Refusal(session) ?? OtherModRefusal(group, "rename") ??
        (nickname.Equals(group.Nickname, StringComparison.Ordinal) ? null : NicknameRefusal(session, nickname, group));

    /// <summary>Deletes a group of the active mod (decision D294). The copies in the systems stay. See
    /// <see cref="PropGroups.Delete"/>.</summary>
    public static PropGroupWrite Delete(EditSession session, PropGroup group) =>
        DeleteProblem(session, group) is { } problem
            ? new PropGroupWrite(null, problem)
            : Write(() =>
            {
                PropGroups.Delete(session, group);
                return null;
            });

    /// <summary>Why <see cref="Delete"/> cannot delete a group, or null.</summary>
    public static EditError? DeleteProblem(EditSession session, PropGroup group) =>
        Refusal(session) ?? OtherModRefusal(group, "delete");

    /// <summary>Why no asset write can run now, or null: a batch is open, or the session takes no edits.</summary>
    private static EditError? Refusal(EditSession session)
    {
        if (session.OpenBatch != null)
        {
            return Error(EditErrorKind.Refused, AssetWriteInBatch,
                "A prop group changes its files at once, so it cannot be a step of a batch. Run it alone.");
        }

        return session.ReadOnlyReason is { } reason
            ? Error(EditErrorKind.ReadOnly, MissionErrors.NotEditable, reason)
            : null;
    }

    private static EditError? OtherModRefusal(PropGroup group, string verb) => group.Editable
        ? null
        : Error(EditErrorKind.Refused, OtherMod,
            $"The prop group {group.Name} is of mod '{group.ModId}', so the editor cannot {verb} it.");

    /// <summary>Why a text cannot be the nickname of a group, or null. The error names the argument
    /// <c>nickname</c>.</summary>
    private static EditError? NicknameRefusal(EditSession session, string nickname, PropGroup? except) =>
        PropGroups.NicknameProblem(session, nickname, except) is not { } problem ? null
        : (PropGroups.IsValidNickname(nickname) ? Error(EditErrorKind.NameTaken, NicknameTaken, problem)
            : Error(EditErrorKind.InvalidValue, BadName, problem)) with { Field = "nickname" };

    /// <summary>Runs a write. A write that the disk refuses gives its reason.</summary>
    private static PropGroupWrite Write(Func<PropGroup?> write)
    {
        try
        {
            return new PropGroupWrite(write(), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PropGroupWrite(null, Error(EditErrorKind.Refused, WriteFailed, e.Message));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return new PropGroupWrite(null, Error(EditErrorKind.Refused, EditPlan.PlanRefused, ErrorText.Of(e)));
        }
    }

    private static EditError Error(EditErrorKind kind, string detail, string message) => new(kind, detail, message);
}
