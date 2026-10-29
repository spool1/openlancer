using System.Collections.Generic;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using Xunit;
using static OpenLancer.Tests.SceneWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The origin of a change and the sections of the agent entries since the save (section 8.5 of the MCP1 design). The
/// marks of the editor read them: the pulse follows the origin of each change, and the robot glyph of the Hierarchy
/// follows <see cref="EditSession.AgentSections"/>.
/// </summary>
public sealed class AgentSectionsTests
{
    private static readonly SectionRef Station = Object("Li01_01");
    private static readonly SectionRef Dock = Object("Li01_dock");
    private static readonly SectionRef Hole = Zone("Zone_hole");

    /// <summary>Runs a command as one entry with an origin, as the agent tools and the command runner do.</summary>
    private static void Run(EditSession session, EditCommand command, EditOrigin origin)
    {
        using var batch = session.BeginBatch(command.Label, origin);
        batch.Run(command);
        batch.Commit();
    }

    private static EditCommand Move(SectionRef section, string pos) => EditCommand.SetLinesFromText(section, "pos", [pos]);

    /// <summary>Asserts the agent sections of a session, in any order.</summary>
    private static void Marks(EditSession session, params SectionRef[] sections) =>
        Assert.True(session.AgentSections.SetEquals(sections), string.Join(", ", session.AgentSections));

    [Fact]
    public void EachChangeGivesTheOriginOfItsEntryAndAnUndoIsUndone()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        Run(session, Move(Station, "1, 2, 3"), EditOrigin.Agent);
        Run(session, Move(Dock, "4, 5, 6"), EditOrigin.User);
        session.Undo();
        session.Redo();

        Assert.Equal([(EditOrigin.Agent, false), (EditOrigin.User, false), (EditOrigin.User, true), (EditOrigin.User, false)],
            changes.ConvertAll(c => (c.Origin, c.Undone)));
        Assert.Equal([Station], changes[0].Sections);
    }

    [Fact]
    public void TheAgentSectionsFollowTheUndoTheRedoAndTheSave()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Run(session, Move(Station, "1, 2, 3"), EditOrigin.Agent);
        Run(session, Move(Dock, "4, 5, 6"), EditOrigin.User);
        Marks(session, Station);
        Assert.Equal(1, session.AgentEdits);

        session.Undo();
        session.Undo(EditOrigin.Agent);
        Assert.Empty(session.AgentSections);
        Assert.Equal(0, session.AgentEdits);

        session.Redo();
        Marks(session, Station);

        Assert.True(session.Save().Saved);
        Assert.Empty(session.AgentSections);
        Assert.Equal(0, session.AgentEdits);

        Run(session, Move(Dock, "7, 8, 9"), EditOrigin.Agent);
        Run(session, Move(Station, "9, 9, 9"), EditOrigin.Agent);
        Marks(session, Dock, Station);
        Assert.Equal(2, session.AgentEdits);
    }

    /// <summary>An entry below the saved entry was saved. So after an undo below the save, no agent entry comes after the
    /// save, and a new entry is the only one after it.</summary>
    [Fact]
    public void AnEntryBelowTheSavedEntryHasNoMark()
    {
        using var ws = Create();
        var session = ws.Session("act");
        Run(session, Move(Station, "1, 2, 3"), EditOrigin.Agent);
        Assert.True(session.Save().Saved);

        session.Undo();
        Assert.Empty(session.AgentSections);
        Run(session, Move(Dock, "4, 5, 6"), EditOrigin.Agent);

        Marks(session, Dock);
        Assert.Equal(1, session.AgentEdits);
    }

    /// <summary>A rename by the user of a section that an agent changed keeps the mark under the new nickname.</summary>
    [Fact]
    public void ARenameOfAMarkedSectionKeepsItsMark()
    {
        using var ws = Create();
        var session = ws.Session("act");
        Run(session, Move(Dock, "1, 2, 3"), EditOrigin.Agent);

        Run(session, ScenePlans.Rename(session, Dock, "Li01_new_dock").Command!, EditOrigin.User);

        Assert.Contains(Object("Li01_new_dock"), session.AgentSections);
        Assert.DoesNotContain(Dock, session.AgentSections);
        Assert.Equal(1, session.AgentEdits);
    }

    /// <summary>The new zone of the plan of a create, as the user and the agent tools make it.</summary>
    private static (EditCommand Command, SectionRef Zone) NewZone(EditSession session)
    {
        var plan = ScenePlans.Create(session, "Li01", SceneTemplate.Zone, null, Vector3.Zero, null);
        return (plan.Command!, plan.Adds[0]);
    }

    /// <summary>
    /// A create with a nickname adds the item under a free nickname, then renames it in the same entry, as the Scene
    /// create tools do. Only the new nickname has the mark. So a later create of the user, which gets the free nickname,
    /// has no mark (review finding of MCP1-2, step 8).
    /// </summary>
    [Fact]
    public void ACreateWithANicknameMarksOnlyItsNewNickname()
    {
        using var ws = Create();
        var session = ws.Session("act");
        using (var batch = session.BeginBatch("Create Zone_test_01", EditOrigin.Agent))
        {
            var (create, free) = NewZone(session);
            batch.Run(create);
            batch.Run(ScenePlans.Rename(session, free, "Zone_test_01").Command!);
            batch.Commit();
        }
        Marks(session, Zone("Zone_test_01"));

        var (userCreate, userZone) = NewZone(session);
        Run(session, userCreate, EditOrigin.User);

        Assert.NotNull(session.Get(userZone));
        Marks(session, Zone("Zone_test_01"));
    }

    /// <summary>The old nickname of an agent rename loses the mark. So a later rename of the user to that nickname gives
    /// no mark. A rename needs a nickname that the lower layer does not have, so the user first creates a zone.</summary>
    [Fact]
    public void TheOldNicknameOfAnAgentRenameHasNoMark()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var (create, zone) = NewZone(session);
        Run(session, create, EditOrigin.User);
        Run(session, ScenePlans.Rename(session, zone, "Zone_agent").Command!, EditOrigin.Agent);
        Marks(session, Zone("Zone_agent"));

        Run(session, ScenePlans.Rename(session, Hole, zone.Id.Key!).Command!, EditOrigin.User);

        Assert.NotNull(session.Get(zone));
        Marks(session, Zone("Zone_agent"));
    }

    /// <summary>A section that the agent deletes, or that the user deletes after an agent edit, has no mark. A new item of
    /// the user with its nickname has no mark either. An undo of the delete of the user gives the mark back.</summary>
    [Fact]
    public void ADeletedSectionHasNoMark()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var (create, zone) = NewZone(session);
        Run(session, create, EditOrigin.Agent);
        Run(session, Move(Hole, "1, 2, 3"), EditOrigin.Agent);
        Marks(session, zone, Hole);

        Run(session, ScenePlans.Delete(session, [zone]).Command!, EditOrigin.Agent);
        Run(session, ScenePlans.Delete(session, [Hole]).Command!, EditOrigin.User);
        Assert.Empty(session.AgentSections);
        Assert.Equal(3, session.AgentEdits);

        var (again, sameZone) = NewZone(session);
        Assert.Equal(zone, sameZone);
        Run(session, again, EditOrigin.User);
        Assert.Empty(session.AgentSections);

        session.Undo();
        session.Undo();
        Marks(session, Hole);
    }
}
