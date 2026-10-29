using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Logs;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// What the user sees of the agent calls in the editor (section 8.5 of the MCP1 design): the Console entries of the edits
/// and the refusals, Pause (decision D734) and the text of the status bar (section 10).
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
    /// has it.</summary>
    [Fact]
    public void ARefusedEditAddsAWarning()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;

        var missing = Error(host, "set_fields", """{ "ref": "obj:none", "fields": { "pos": [1, 2, 3] } }""");
        var asks = Error(host, "delete_items", """{ "refs": ["obj:Li01_01"] }""");

        var entries = Agent(editor);
        Assert.Equal([(ConsoleSeverity.Warning, $"set_fields: {missing.Message}"),
            (ConsoleSeverity.Warning, $"delete_items: {asks.Message}")], entries.Select(e => (e.Severity, e.Message)));
        Assert.Equal("needs_confirmation", asks.Code);
    }

    /// <summary>A preview, a query and a view tool add no entry.</summary>
    [Fact]
    public void APreviewAndAQueryAddNoEntry()
    {
        var (ws, session, editor, host) = Editor();
        using var _ = ws;

        Ok(host, "scene_create_zone", Zone.Replace("}", ", \"preview\": true }"));
        Error(host, "set_fields", """{ "ref": "obj:none", "fields": { "pos": [1, 2, 3] }, "preview": true }""");
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
    /// Section 13.2, item 1 of the MCP1 design: while the user pauses the agent edits, the dispatcher of the editor gives
    /// paused for an edit, and lets a query and play_stop run. The Console gets the refusal.
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
        Assert.Equal("Stops all agent edits until you resume them. The agent can still read the data.",
            AgentMenu.PauseTip(false));
        Assert.Equal("Lets an agent program on this computer read and edit the active mod. Each agent edit is one Undo step.",
            AgentMenu.StartTip);
        foreach (var text in new[] { AgentMenu.PauseTip(true), AgentMenu.PauseTip(false), AgentMenu.StartTip })
            Assert.Empty(SteLint.Problems(text));
    }
}
