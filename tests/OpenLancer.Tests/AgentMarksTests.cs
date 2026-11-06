using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Hierarchy;
using Xunit;
using static OpenLancer.Tests.SceneWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The marks of the agent edits (section 8.5 of the MCP1 design, decision D718). The Scene shows the pulse ring for 4
/// seconds, and the Hierarchy shows the robot glyph until the save. The marks follow the changes of the session, with a
/// fake clock.
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
        Assert.Empty(SteLint.Problems(Editor.Hierarchy.HierarchyPanel.AgentMarkTip));
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

    /// <summary>A row of the tree of a mission shows the robot glyph after an agent edit of its section, and loses it at
    /// the undo. A line row has no section of its own, so it never has the glyph (section 8.5 of the MCP1 design, step 2
    /// of MCP1-3).</summary>
    [Fact]
    public void AMissionRowHasTheGlyphAfterAnAgentEditAndLosesItAtTheUndo()
    {
        using var m = MissionEdits.Create();
        var marks = Marks();
        marks.Track(m.Session);
        var host = new TestAgentHost(m.Session);
        var script = MissionEdits.Script.Replace(@"\", @"\\");

        AgentCalls.Ok(host, "mission_edit", $$"""
            { "commands": [ { "kind": "set_condition_value", "trigger": "trg:{{script}}#ol_wave", "name": "seconds", "value": 9 } ] }
            """);
        var edited = MarkedRows(m, marks);
        m.Session.Undo();
        var undone = MarkedRows(m, marks);

        Assert.Equal(["ol_wave"], edited);
        Assert.Empty(undone);
        var tree = Tree(m);
        var wave = new SectionRef(MissionEdits.Script, new SectionIdentity("Trigger", "ol_wave"));
        Assert.Equal(wave, tree.SectionOfMissionRow(new MissionRef(MissionEdits.Script, MissionItemKind.Trigger, wave)));
        Assert.Null(tree.SectionOfMissionRow(new MissionRef(MissionEdits.Script, MissionItemKind.Row, wave, Row: 2)));
        Assert.Null(tree.SectionOfMissionRow(new MissionRef(MissionEdits.Script, MissionItemKind.Label, Key: "ol_wing")));
    }

    /// <summary>The header row of the tree of a mission is the mission. It shows the robot glyph after an agent edit of
    /// the <c>[Mission]</c> section, and loses it at the undo (section 8.5 of the MCP1 design).</summary>
    [Fact]
    public void TheHeaderRowOfAMissionHasTheGlyphAfterAnAgentEditOfTheMissionSection()
    {
        using var m = MissionEdits.Create();
        var marks = Marks();
        marks.Track(m.Session);
        var host = new TestAgentHost(m.Session);
        var script = MissionEdits.Script.Replace(@"\", @"\\");
        var header = Tree(m).Root.Name;

        AgentCalls.Ok(host, "mission_edit", $$"""
            { "commands": [ { "kind": "set_item_key", "item": "sec:{{script}}#Mission", "key": "npc_ship_file",
              "lines": [ { "key": "npc_ship_file", "texts": [ "MISSIONS\\OL_TEST\\NPCSHIPS.INI" ] } ] } ] }
            """);
        var edited = MarkedRows(m, marks);
        m.Session.Undo();

        Assert.Equal([header], edited);
        Assert.Empty(MarkedRows(m, marks));
    }

    /// <summary>The row of a cutscene in the Hierarchy shows the robot glyph after an agent edit of its script, also of
    /// a new script. An undo removes the glyph, a redo puts it back, and a save clears it. An edit of the user gives no
    /// glyph. The history records a cutscene change by the path of its script, so the rows of the entities have no glyph
    /// (section 8.5 of the MCP1 design).</summary>
    [Fact]
    public void TheRowOfACutsceneHasTheGlyphAfterAnAgentEditUntilTheUndoOrTheSave()
    {
        using var ws = ThnSessions.Workspace();
        var session = ws.Session("act");
        var marks = Marks();
        marks.Track(session);
        var host = new TestAgentHost(session);
        var script = AgentCalls.Json("thn:" + ThnSessions.ScriptPath);
        var root = CutsceneTree(session).Root;
        var cutscene = Assert.IsType<CutsceneRoot>(root.Item);

        using (var user = session.BeginBatch("Set duration"))
        {
            user.Run(ThnEdits.SetDuration(ThnSessions.ScriptPath, 11));
            user.Commit();
        }
        Assert.False(marks.Marked(cutscene));

        AgentCalls.Ok(host, "cutscene_set_duration", $$"""{ "cutscene": {{script}}, "seconds": 12 }""");
        Assert.True(marks.Marked(cutscene));
        Assert.True(marks.Marked(new CutsceneRoot(ThnSessions.ScriptPath.ToLowerInvariant())));
        Assert.Empty(marks.Pulses());
        session.Undo();
        Assert.False(marks.Marked(cutscene));
        session.Redo();
        Assert.True(marks.Marked(cutscene));

        AgentCalls.Ok(host, "cutscene_create", """{ "name": "my_intro" }""");
        Assert.True(marks.Marked(new CutsceneRoot(Assert.Single(session.NewCutscenes))));
        Assert.True(session.Save().Saved);
        Assert.False(marks.Marked(cutscene));
        Assert.Empty(session.AgentCutscenes);
    }

    /// <summary>The tree of the cutscene script of <see cref="ThnSessions"/> in the Hierarchy.</summary>
    private static HierarchyModel CutsceneTree(EditSession session) =>
        HierarchyModel.ForCutscene(TimelineModel.Build(ThnSessions.ScriptPath, ThnSessions.W(session),
            ThnScripts.Evaluator.Symbols, OpenLancer.Runtime.Thn.ScriptPlay.Normal), null);

    /// <summary>The tree of the test mission now.</summary>
    private static HierarchyModel Tree(MissionEdits m) =>
        HierarchyModel.ForMission(m.Missions.Model(MissionEdits.Script).Analysis, _ => "", false, null);

    /// <summary>The names of the rows of the tree of the test mission that show the robot glyph, the header row
    /// first.</summary>
    private static string[] MarkedRows(MissionEdits m, AgentMarks marks)
    {
        var tree = Tree(m);
        return Rows(tree.Root).Prepend(tree.Root)
            .Where(n => tree.SectionOfMissionRow(n.Item) is { } section && marks.Marked(section))
            .Select(n => n.Name)
            .ToArray();
    }

    private static IEnumerable<HierarchyNode> Rows(HierarchyNode node) => node.Children.SelectMany(c => Rows(c).Prepend(c));
}
