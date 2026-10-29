using System.Collections.Generic;
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
}
