using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Logs;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// What the user sees of the agent calls in the editor (section 8.5 of the MCP1 design). These are the Console entries of
/// the edits and the refusals, Pause (decision D734) and the text of the status bar (section 10).
/// </summary>
public sealed class AgentConsoleTests
{
    private const string Zone = """{ "system": "Li01", "shape": "sphere", "position": [0, 0, 0], "nickname": "Zone_test_01" }""";

    /// <summary>The editor host of a session of the scene workspace.</summary>
    private static (TestWorkspace Ws, EditSession Session, FakeAgentEditor Editor, EditorAgentHost Host) Editor()
    {
        var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var editor = new FakeAgentEditor { Edits = session };
        return (ws, session, editor, new EditorAgentHost(editor));
    }

    private static ConsoleEntry[] Agent(FakeAgentEditor editor) =>
        editor.Log.Entries.Where(e => e.Source == AgentController.Source).ToArray();

    /// <summary>An applied edit adds an Info entry with the source Agent, the label of the request and a link to the new
    /// item. The entry has the origin OpenLancer, so the toggle of the base-game messages never hides it.</summary>
    [Fact]
    public void AnAppliedEditAddsAnInfoEntryWithALinkToItsItem()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;

        Ok(host, "scene_create_zone", Zone);
        Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");

        var entries = Agent(editor);
        Assert.Equal([(ConsoleSeverity.Info, "Create Zone_test_01."), (ConsoleSeverity.Info, "Set pos.")],
            entries.Select(e => (e.Severity, e.Message)));
        Assert.Equal([SceneWorkspace.Zone("Zone_test_01"), SceneWorkspace.Object("Li01_01")], entries.Select(e => e.Target));
        Assert.All(entries, e => Assert.Equal(OriginKind.OpenLancer, e.Origin.Kind));
    }

    /// <summary>A refused edit adds a Warning "{tool}: {message}" with a link to the item of the error, when the edit view
    /// has it. A question of the agent (needs_confirmation) is not a refusal (section 8.1 of the MCP1 design), so it adds
    /// no entry. The confirmed call then adds its Info entry.</summary>
    [Fact]
    public void ARefusedEditAddsAWarningAndAQuestionAddsNoEntry()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;

        var missing = Error(host, "set_fields", """{ "ref": "obj:none", "fields": { "pos": [1, 2, 3] } }""");
        var stale = Error(host, "set_fields",
            """{ "ref": "obj:Li01_dock", "fields": { "pos": [1, 2, 3] }, "if_revision": "7" }""");
        var asks = Error(host, "delete_items", """{ "refs": ["obj:Li01_01"] }""");
        Ok(host, "delete_items", """{ "refs": ["obj:Li01_01"], "confirm": true }""");

        var entries = Agent(editor);
        Assert.Equal([(ConsoleSeverity.Warning, $"set_fields: {missing.Message}"),
                (ConsoleSeverity.Warning, $"set_fields: {stale.Message}"), (ConsoleSeverity.Info, "Delete Li01_01 and 2 more.")],
            entries.Select(e => (e.Severity, e.Message)));
        Assert.Equal(("stale_revision", "needs_confirmation"), (stale.Code, asks.Code));
    }

    /// <summary>The entry of an agent mission edit links to the section of the mission: the new trigger of a create, and
    /// the trigger of a changed line. A click on the entry then opens the mission in the Mission tab at the section
    /// (section 12.4 of the M6a design), because the link names the whole section.</summary>
    [Fact]
    public void TheEntryOfAMissionEditLinksToTheMissionSection()
    {
        using var m = MissionEdits.Create();
        var editor = new FakeAgentEditor { Edits = m.Session };
        var host = new EditorAgentHost(editor);
        var script = MissionEdits.Script.Replace(@"\", @"\\");

        Ok(host, "mission_edit", $$"""
            { "commands": [ { "kind": "add_trigger", "script": "msn:{{script}}", "nickname": "ol_agent",
                              "condition": { "key": "Cnd_Timer", "values": { "seconds": 2 } } } ] }
            """);
        Ok(host, "mission_edit", $$"""
            { "label": "Wait longer",
              "commands": [ { "kind": "set_condition_value", "trigger": "trg:{{script}}#ol_wave", "name": "seconds", "value": 9 } ] }
            """);

        var entries = Agent(editor);
        Assert.Equal([new SectionRef(MissionEdits.Script, new SectionIdentity("Trigger", "ol_agent")),
                new SectionRef(MissionEdits.Script, new SectionIdentity("Trigger", "ol_wave"))],
            entries.Select(e => e.Target));
        Assert.Equal("Wait longer.", entries[1].Message);
        Assert.All(entries, e => Assert.Equal((ConsoleSeverity.Info, -1), (e.Severity, e.EntryIndex)));
    }

    /// <summary>
    /// The entry of an agent cutscene edit links to the place in the cutscene (section 9 of the M8 design): the new event
    /// of an add, and the script of a new duration. A click on the entry then opens the cutscene and selects the element.
    /// A refusal links to the event of the error. A delete links to nothing, because its event is gone. get_console
    /// gives the reference of each link, as the cutscene has its elements now.
    /// </summary>
    [Fact]
    public void TheEntryOfACutsceneEditLinksToThePlaceInTheCutscene()
    {
        using var ws = ThnSessions.Workspace();
        var session = ws.Session("act");
        var editor = new FakeAgentEditor { Edits = session };
        var host = new EditorAgentHost(editor);
        const string script = "thn:" + ThnSessions.ScriptPath;

        Ok(host, "cutscene_add_event", $$"""{ "cutscene": {{Json(script)}}, "type": "START_PSYS", "time": 1.5, "targets": ["Fx"] }""");
        var added = ThnSessions.Event(session, 6);
        var link = Agent(editor)[0].Cutscene!;
        Assert.Equal((ThnSessions.ScriptPath, (ThnElementKind?)ThnElementKind.Event, 6), (link.Path, link.Kind, link.Index));
        Assert.Equal(added.Id, link.ElementIn(ThnSessions.W(session))!.Id);
        var refs = host.Console(new ConsoleFilter(AgentController.Source, AgentSeverity.Info, false, null)).Select(e => e.Ref);
        Assert.Equal([script + "#event/6"], refs);

        Ok(host, "cutscene_set_duration", $$"""{ "cutscene": {{Json(script)}}, "seconds": 12 }""");
        var stale = Error(host, "cutscene_move_events",
            $$"""{ "events": [ { "ref": {{Json(script + "#event/3")}}, "expect_type": "SET_CAMERA" } ], "time_delta": 1 }""");
        Ok(host, "delete_items", $$"""{ "refs": [{{Json(script + "#event/0")}}] }""");

        var entries = Agent(editor);
        Assert.Equal(["Add START_PSYS.", "Set duration.", $"cutscene_move_events: {stale.Message}"],
            entries.Take(3).Select(e => e.Message));
        Assert.Equal(new ThnLocation(ThnSessions.ScriptPath), entries[1].Cutscene);
        Assert.Equal((ThnSessions.ScriptPath, (ThnElementKind?)ThnElementKind.Event, 3),
            (entries[2].Cutscene!.Path, entries[2].Cutscene!.Kind, entries[2].Cutscene!.Index));
        Assert.Null(entries[3].Cutscene);
        Assert.All(entries, e => Assert.Null(e.Target));
        // The delete of event 0 moves each later event one index down, so each link gives the reference of its event now.
        refs = host.Console(new ConsoleFilter(AgentController.Source, AgentSeverity.Info, false, null)).Select(e => e.Ref);
        Assert.Equal([script + "#event/5", script, script + "#event/2", null], refs);
    }

    /// <summary>A preview, a query and a view tool add no entry.</summary>
    [Fact]
    public void APreviewAndAQueryAddNoEntry()
    {
        var (ws, session, editor, host) = Editor();
        using var _ = ws;

        Ok(host, "scene_create_zone", Zone.Replace("}", ", \"preview\": true }"));
        Error(host, "set_fields", """{ "ref": "obj:none", "fields": { "pos": [1, 2, 3] }, "preview": true }""");
        // Arguments that do not pass the input check: the flag preview comes from the JSON.
        Error(host, "set_fields", """{ "ref": 5, "fields": { "pos": [1, 2, 3] }, "preview": true }""");
        Ok(host, "list_items", """{ "system": "Li01" }""");
        Error(host, "get_item", """{ "ref": "obj:none" }""");

        Assert.Empty(Agent(editor));
        Assert.Equal(0, session.Revision);
    }

    /// <summary>An undo and a redo of the agent add an Info entry with the label of the entry.</summary>
    [Fact]
    public void AnUndoAndARedoOfTheAgentAddAnEntry()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        Ok(host, "scene_create_zone", Zone);

        Ok(host, "undo");
        Ok(host, "redo");

        Assert.Equal(["Create Zone_test_01.", "Undo Agent: Create Zone_test_01.", "Redo Agent: Create Zone_test_01."],
            Agent(editor).Select(e => e.Message));
    }

    /// <summary>
    /// Section 13.2, item 1 of the MCP1 design. While the user pauses the agent edits, the dispatcher of the editor gives
    /// paused for an edit. It lets a query and play_stop run. The Console gets the refusal.
    /// </summary>
    [Fact]
    public async Task PauseGivesPausedForAnEditAndLetsAQueryAndPlayStopRun()
    {
        var (ws, session, editor, host) = Editor();
        using var _ = ws;
        editor.Paused = true;
        using var dispatcher = new AgentDispatcher(host, () => default, null);
        var token = TestContext.Current.CancellationToken;

        var edit = dispatcher.Queue.CallAsync("set_fields",
            JsonNode.Parse("""{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""")!.AsObject(), "test", token);
        var query = dispatcher.Queue.CallAsync("studio_status", null, "test", token);
        var stop = dispatcher.Queue.CallAsync("play_stop", null, "test", token);
        for (var i = 0; i < 3; i++)
            Assert.True(dispatcher.Frame());

        var paused = await edit.WaitAsync(TimeSpan.FromSeconds(10), token);
        var status = await query.WaitAsync(TimeSpan.FromSeconds(10), token);
        var stopped = await stop.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal("paused", (string?)paused.Content["code"]);
        Assert.False(status.IsError, status.Text);
        Assert.True((bool)status.Content["paused"]!);
        Assert.False(stopped.IsError, stopped.Text);
        Assert.False(session.CanUndo);
        Assert.Equal([$"set_fields: {(string?)paused.Content["message"]}"], Agent(editor).Select(e => e.Message));
    }

    /// <summary>The status bar gives the agent edits since the last save in the singular and the plural form, and "Agent
    /// paused" (section 10 of the MCP1 design).</summary>
    [Fact]
    public void TheStatusBarTextHasTheCountOfTheAgentEdits()
    {
        Assert.Equal("Agent: Claude Code, 1 edit", AgentController.ClientText("Claude Code", 1));
        Assert.Equal("Agent: Claude Code, 0 edits", AgentController.ClientText("Claude Code", 0));
        Assert.Equal("Agent: Claude Code, 12 edits", AgentController.ClientText("Claude Code", 12));
        Assert.Equal("Agent paused", AgentController.PausedText);
    }

    /// <summary>The texts of the Agent menu for Pause and the start of the server (section 10 of the MCP1 design) pass the
    /// STE lint.</summary>
    [Fact]
    public void TheMenuTextsOfPauseAndTheStart()
    {
        Assert.Equal(("Pause agent edits", "Resume agent edits"), (AgentMenu.PauseLabel(false), AgentMenu.PauseLabel(true)));
        Assert.Equal("Stops all agent edits until you resume them. The agent can still read the data and stop each game of Play.",
            AgentMenu.PauseTip(false));
        Assert.Equal("Lets an agent program on this computer read and edit the active mod. Each agent edit is one Undo step.",
            AgentMenu.StartTip);
        foreach (var text in new[] { AgentMenu.PauseTip(true), AgentMenu.PauseTip(false), AgentMenu.StartTip })
            Assert.Empty(SteLint.Problems(text));
    }
}
