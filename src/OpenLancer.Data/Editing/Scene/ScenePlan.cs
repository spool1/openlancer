using System.Collections.Generic;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>A template of Create in the Scene (decisions D233 and D250 of the M3 design).</summary>
public enum SceneTemplate
{
    Station,
    Planet,
    Sun,
    Satellite,
    Solar,
    Zone,
    Light,
}

/// <summary>The action of a <see cref="ScenePlan"/>.</summary>
public enum SceneAction
{
    Create,
    Duplicate,
    Delete,
    Rename,

    /// <summary>A placement of a prop group (milestone M23): several new items, as a Duplicate makes.</summary>
    Place,
}

/// <summary>Why a section goes in a delete.</summary>
public enum DeleteReason
{
    /// <summary>The user deletes it.</summary>
    Target,

    /// <summary>It goes with a target, as a child goes with its parent in Unity.</summary>
    Cascade,
}

/// <summary>A section that a delete removes.</summary>
/// <param name="Section">The section.</param>
/// <param name="Children">The number of child sections that go with it, for example <c>[CollisionGroup]</c>
/// sections.</param>
/// <param name="Why">A target, or a section that goes with a target.</param>
/// <param name="Cause">For a section that goes with a target: the line that names the item that goes, for example
/// <c>parent = Li01_14</c>. Null for a target.</param>
public sealed record PlannedDelete(SectionRef Section, int Children, DeleteReason Why, string? Cause);

/// <summary>A safe fix of a line that names an item that goes (decision D81): the line or the lines of a hole go in the
/// same entry of the edit history.</summary>
/// <param name="Section">The section of the lines.</param>
/// <param name="Key">The key of the first line, for example <c>ring</c> or <c>exclusion</c>.</param>
/// <param name="Text">What the fix does, for example "Bw05_Crow_nebula.ini: the hole of Zone_Bw05_01_exclusion (6
/// lines)".</param>
/// <param name="Part">True for a fix that is a part of the item that goes: a new link of a trade lane, or a gate lock
/// line of a jump connection. Such a fix alone does not make the editor ask.</param>
public sealed record PlannedFix(SectionRef Section, string Key, string Text, bool Part = false);

/// <summary>A line that keeps a name of an item that goes, and shows "Missing" after the delete.</summary>
/// <param name="Reference">The line.</param>
/// <param name="Consequence">What the game does with the line, or why the editor cannot fix it.</param>
/// <param name="BlocksSave">True when the rule of the line is closed, so the save stops until a fix.</param>
public sealed record MissingReference(ListReference Reference, string Consequence, bool BlocksSave);

/// <summary>A line of a placement of a prop group whose list check gives an error (decision D290 of the M23
/// design).</summary>
/// <param name="Member">The member id of the group, for example <c>large_transport_2</c>.</param>
/// <param name="Key">The key of the line, for example <c>loadout</c>.</param>
/// <param name="Value">The value that the check refuses, or null for a missing required line.</param>
/// <param name="Message">What is wrong and what the game does with the line, in plain words, without the member
/// id.</param>
/// <param name="Required">True when the rule of the line is required, so the member cannot go without the line.</param>
public sealed record PlacementError(string Member, string Key, string? Value, string Message, bool Required);

/// <summary>A file that names a nickname as text (see <see cref="StoryMention"/>).</summary>
public sealed record NamedMention(string Nickname, string Path, int Count);

/// <summary>
/// The plan of a Create, a Duplicate, a Delete or a Rename in the Scene (milestone M3): what the action changes, what it
/// fixes, what it refuses and what it warns about, and the command that runs it as one entry of the edit history.
/// </summary>
/// <remarks>
/// <para>A plan reads the edit view when it is made. Its command runs only while the edit view and the text are as the
/// plan read them, so make the plan and run it at once.</para>
/// <para><see cref="EditPlan.NeedsConfirmation"/> is true when the editor asks before it runs the command (decisions
/// D238 and D242): a Delete that removes more than the targets and their child sections, fixes a line, leaves "Missing"
/// or has a warning or a mention, and a Rename with a warning or a mention. A Create and a Duplicate never ask, as in
/// Unity.</para>
/// </remarks>
public sealed class ScenePlan : EditPlan
{
    internal ScenePlan(SceneAction action, string label) : base(label)
    {
        Action = action;
    }

    public SceneAction Action { get; }

    /// <summary>The lines of a placement of a prop group whose list checks give an error (decision D290 of the M23
    /// design). A plan with such lines has no command, unless it leaves them out. Empty for the other actions.</summary>
    public IReadOnlyList<PlacementError> PlacementErrors { get; internal set; } = [];

    /// <summary>The new <c>[Solar]</c> of a static prop archetype that the plan adds with its object (decision D279 of
    /// the M23 design), or null. The loaded game data does not have it, so the Scene shows the object after the save
    /// and the reload of the game data.</summary>
    public SectionRef? NewArchetype { get; internal set; }
}
