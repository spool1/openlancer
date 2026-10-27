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
    public static PropGroupWrite Save(EditSession session, PropGroupDraft draft, string nickname, string name)
    {
        if (Refusal(session) is { } refused)
            return refused;
        if (draft.Refusals.Count > 0)
            return Refuse(EditErrorKind.Refused, EditPlan.PlanRefused, draft.Refusals[0].Message);
        if (NicknameRefusal(session, nickname, null) is { } bad)
            return bad;
        if (PropGroups.NameProblem(name) is { } nameProblem)
            return Refuse(EditErrorKind.InvalidValue, BadName, nameProblem);
        return Write(() => PropGroups.Write(session, draft, nickname, name));
    }

    /// <summary>Gives a group of the active mod a new nickname (decision D294). See
    /// <see cref="PropGroups.Rename"/>.</summary>
    public static PropGroupWrite Rename(EditSession session, PropGroup group, string nickname)
    {
        if ((Refusal(session) ?? OtherModRefusal(group, "rename")) is { } refused)
            return refused;
        if (!nickname.Equals(group.Nickname, StringComparison.Ordinal) &&
            NicknameRefusal(session, nickname, group) is { } bad)
            return bad;
        return Write(() => PropGroups.Rename(session, group, nickname));
    }

    /// <summary>Deletes a group of the active mod (decision D294). The copies in the systems stay. See
    /// <see cref="PropGroups.Delete"/>.</summary>
    public static PropGroupWrite Delete(EditSession session, PropGroup group)
    {
        if ((Refusal(session) ?? OtherModRefusal(group, "delete")) is { } refused)
            return refused;
        return Write(() =>
        {
            PropGroups.Delete(session, group);
            return null;
        });
    }

    /// <summary>Why no asset write can run now, or null: a batch is open, or the session takes no edits.</summary>
    private static PropGroupWrite? Refusal(EditSession session)
    {
        if (session.OpenBatch != null)
        {
            return Refuse(EditErrorKind.Refused, AssetWriteInBatch,
                "A prop group changes its files at once, so it cannot be a step of a batch. Run it alone.");
        }

        return session.ReadOnlyReason is { } reason
            ? Refuse(EditErrorKind.ReadOnly, MissionErrors.NotEditable, reason)
            : null;
    }

    private static PropGroupWrite? OtherModRefusal(PropGroup group, string verb) => group.Editable
        ? null
        : Refuse(EditErrorKind.Refused, OtherMod,
            $"The prop group {group.Name} is of mod '{group.ModId}', so the editor cannot {verb} it.");

    private static PropGroupWrite? NicknameRefusal(EditSession session, string nickname, PropGroup? except) =>
        PropGroups.NicknameProblem(session, nickname, except) is not { } problem ? null
        : PropGroups.IsValidNickname(nickname) ? Refuse(EditErrorKind.NameTaken, NicknameTaken, problem)
        : Refuse(EditErrorKind.InvalidValue, BadName, problem);

    /// <summary>Runs a write. A write that the disk refuses gives its reason.</summary>
    private static PropGroupWrite Write(Func<PropGroup?> write)
    {
        try
        {
            return new PropGroupWrite(write(), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse(EditErrorKind.Refused, WriteFailed, e.Message);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return Refuse(EditErrorKind.Refused, EditPlan.PlanRefused, ErrorText.Of(e));
        }
    }

    private static PropGroupWrite Refuse(EditErrorKind kind, string detail, string message) =>
        new(null, new EditError(kind, detail, message));
}
