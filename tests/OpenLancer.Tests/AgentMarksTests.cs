using System;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Agent;
using Xunit;
using static OpenLancer.Tests.SceneWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The marks of the agent edits (section 8.5 of the MCP1 design, decision D718): the pulse ring of the Scene for 4
/// seconds and the robot glyph of the Hierarchy until the save. The marks follow the changes of the session, with a fake
/// clock.
/// </summary>
public sealed class AgentMarksTests
{
    private static readonly SectionRef Station = Object("Li01_01");
    private static readonly SectionRef Dock = Object("Li01_dock");

    private DateTime now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private AgentMarks Marks() => new(() => now);

    private static void Run(EditSession session, SectionRef section, string pos, EditOrigin origin)
    {
        using var batch = session.BeginBatch("Set pos", origin);
        batch.Run(EditCommand.SetLinesFromText(section, "pos", [pos]));
        batch.Commit();
    }

    [Fact]
    public void TheMarksFollowTheChangesOfTheAgent()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var marks = Marks();
        marks.Track(session);

        Run(session, Station, "1, 2, 3", EditOrigin.Agent);
        now += TimeSpan.FromSeconds(1);
        Run(session, Dock, "4, 5, 6", EditOrigin.User);

        Assert.Equal([(Station, TimeSpan.FromSeconds(1))], marks.Pulses());
        Assert.True(marks.Marked(Station));
        Assert.False(marks.Marked(Dock));
    }

    [Fact]
    public void AnUndoRemovesTheMarksAndARedoPutsThemBack()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var marks = Marks();
        marks.Track(session);
        Run(session, Station, "1, 2, 3", EditOrigin.Agent);

        session.Undo();
        Assert.Empty(marks.Pulses());
        Assert.False(marks.Marked(Station));

        now += TimeSpan.FromSeconds(2);
        session.Redo();
        Assert.Equal([(Station, TimeSpan.Zero)], marks.Pulses());
        Assert.True(marks.Marked(Station));
    }

    [Fact]
    public void ASaveClearsTheMarks()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var marks = Marks();
        marks.Track(session);
        Run(session, Station, "1, 2, 3", EditOrigin.Agent);

        Assert.True(session.Save().Saved);

        Assert.Empty(marks.Pulses());
        Assert.False(marks.Marked(Station));
    }

    [Fact]
    public void ThePulseEndsAfterFourSecondsAndTheGlyphStays()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var marks = Marks();
        marks.Track(session);
        Run(session, Station, "1, 2, 3", EditOrigin.Agent);

        now += TimeSpan.FromSeconds(3.9);
        Assert.Equal([Station], marks.Pulses().Select(p => p.Section));
        now += TimeSpan.FromSeconds(0.1);
        Assert.Empty(marks.Pulses());
        Assert.True(marks.Marked(Station));
        Assert.Equal(TimeSpan.FromSeconds(4), AgentMarks.PulseTime);
    }

    /// <summary>The marks follow the session of the open profile. A new session starts without pulses.</summary>
    [Fact]
    public void AnotherSessionStartsWithoutMarks()
    {
        using var ws = Create();
        var first = ws.Session("act");
        var marks = Marks();
        marks.Track(first);
        Run(first, Station, "1, 2, 3", EditOrigin.Agent);
        first.Dispose();
        using var second = ws.Session("act");

        marks.Track(second);

        Assert.Empty(marks.Pulses());
        Assert.False(marks.Marked(Station));
        marks.Track(null);
        Assert.False(marks.Marked(Station));
    }
}
