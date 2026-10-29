using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>undo</c> and <c>redo</c> (section 13.1, item 9 of the MCP1 design, decision D709). The agent undoes only
/// an agent entry at the top of the history. It redoes only an agent entry that it undid itself, so it never does again
/// an edit that the user undid with Ctrl+Z.
/// </summary>
public sealed class AgentUndoTests
{
    private const string Move = """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""";

    [Fact]
    public void UndoTakesBackAnAgentEntryAndRedoDoesItAgain()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var before = session.WorkingSection(Station);
        Ok(host, "set_fields", Move);
        var after = session.WorkingSection(Station);

        var undo = Ok(host, "undo");
        Assert.Equal(("Agent: Set pos", "2"), ((string?)undo["label"], (string?)undo["rev"]));
        Assert.Same(before, session.WorkingSection(Station));
        Assert.Equal((EditOrigin.Agent, EditOrigin.Agent), (session.RedoOrigin, session.RedoUndoneBy));

        var redo = Ok(host, "redo");
        Assert.Equal(("Agent: Set pos", "3"), ((string?)redo["label"], (string?)redo["rev"]));
        Assert.Same(after, session.WorkingSection(Station));
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);
        // The host learns each step for its Console and its follow mode (section 8.5 of the MCP1 design).
        Assert.Equal([("undo", "Agent: Set pos"), ("redo", "Agent: Set pos")], host.Steps);
    }

    /// <summary>The observable result of step 3 of MCP1-2. A preview of a delete gives the cascade and keeps the
    /// revision. The delete with confirm: true gives one entry "Agent: Delete ...". The agent can undo and redo it. After
    /// a Ctrl+Z of the user, the agent cannot redo it.</summary>
    [Fact]
    public void ADeleteOfTheAgentIsOneEntryThatTheAgentCanUndoAndRedo()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var preview = Ok(host, "delete_items", """{ "refs": ["obj:Li01_01"], "preview": true }""");
        Assert.Contains(preview["deletes"]!.AsArray(), d => (string?)d!["why"] == "cascade");
        Assert.Equal(0, session.Revision);
        var delete = Ok(host, "delete_items", """{ "refs": ["obj:Li01_01"], "confirm": true }""");
        var label = (string)delete["undo_label"]!;
        Assert.StartsWith("Agent: Delete Li01_01", label);
        Assert.Equal(label, session.UndoLabel);

        Assert.Equal(label, (string?)Ok(host, "undo")["label"]);
        Assert.NotNull(session.Get(Station));
        Assert.Equal(label, (string?)Ok(host, "redo")["label"]);
        Assert.Null(session.Get(Station));

        session.Undo();
        Assert.Equal(("not_agent_entry", "UserUndid"), (Error(host, "redo").Code, Error(host, "redo").Detail));
        Assert.NotNull(session.Get(Station));
    }

    [Fact]
    public void UndoRefusesAnEditOfTheUserAtTheTop()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        Ok(host, "set_fields", Move);
        session.Execute(EditCommand.SetLinesFromText(Station, "pos", ["4, 5, 6"]));

        var error = Error(host, "undo");

        Assert.Equal(("not_agent_entry", "UserEntry"), (error.Code, error.Detail));
        Assert.Equal("The last edit is an edit of the user: Set pos. The agent can undo only its own edits.", error.Message);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.Equal("4, 5, 6", Line(session, Station, "pos"));
    }

    /// <summary>After the user undid an agent entry with Ctrl+Z, the agent cannot redo it (section 14.3, step 27 of the
    /// MCP1 design). Nor can it redo an edit of the user.</summary>
    [Fact]
    public void RedoRefusesAnAgentEntryThatTheUserUndidAndAnEditOfTheUser()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        Ok(host, "set_fields", Move);
        session.Undo();

        var undid = Error(host, "redo");
        Assert.Equal(("not_agent_entry", "UserUndid", "The user undid this edit: Agent: Set pos. The agent cannot redo it."),
            undid);
        Assert.False(session.CanUndo);

        session.Redo();
        session.Execute(EditCommand.SetLinesFromText(Station, "pos", ["4, 5, 6"]));
        session.Undo();
        var user = Error(host, "redo");
        Assert.Equal(("not_agent_entry", "UserEntry"), (user.Code, user.Detail));
        Assert.Equal("1, 2, 3", Line(session, Station, "pos"));
    }

    [Fact]
    public void AnEmptyHistoryRefusesUndoAndRedo()
    {
        var (ws, _, host) = Scene();
        using var __ = ws;

        Assert.Equal(("refused", "NothingToUndo", "The history has no step to undo."), Error(host, "undo"));
        Assert.Equal(("refused", "NothingToRedo", "The history has no step to redo."), Error(host, "redo"));
    }

    /// <summary>Pause and a host that takes no edits refuse undo and redo too (decision D734, section 8.2 of the MCP1
    /// design).</summary>
    [Fact]
    public void PauseAndAHostThatTakesNoEditsRefuseUndo()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        Ok(host, "set_fields", Move);

        host.TestPaused = true;
        Assert.Equal("paused", Error(host, "undo").Code);
        host.TestPaused = false;
        host.Reason = "The user did not allow the edits.";
        Assert.Equal(("read_only", "NotEditable", "The user did not allow the edits."), Error(host, "undo"));
        Assert.True(session.CanUndo);
    }
}
