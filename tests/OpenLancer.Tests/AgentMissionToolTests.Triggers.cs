using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The trigger tools of section 7.4 of the MCP1 design (step 3 of MCP1-3): <c>mission_add_trigger</c>,
/// <c>mission_set_condition</c>, <c>mission_add_action</c>, <c>mission_set_action</c>, <c>mission_move_action</c>,
/// <c>mission_link_triggers</c> and <c>mission_insert_trigger</c>. Each call runs the commands of the mission studio in
/// one request of the shared batch. So it is one Undo step with the edit origin Agent, it runs in strict mode, and its
/// result gives the canonical references and the inverse. The parity test of section 13.1, item 7 compares each tool
/// with the gesture of the mission studio.
/// </summary>
public sealed partial class AgentMissionToolTests
{
    /// <summary>The references of two triggers and of the lines of ol_wave, as JSON text.</summary>
    private static readonly string Wave = $"trg:{S}#ol_wave";

    private static readonly string End = $"trg:{S}#ol_end";

    private static string WaveLine(int row) => $"line:{S}#Trigger/ol_wave/{row}";

    /// <summary>Each reference of the adds and the changes of a result is in the canonical form of section 5.6 of the
    /// MCP1 design: a trigger is <c>trg:</c>, a line is <c>line:</c>, and the text round-trips.</summary>
    private static void AssertCanonical(JsonObject result)
    {
        foreach (var reference in Texts(result["adds"]).Concat(Texts(result["changes"])))
        {
            Assert.True(reference.StartsWith("trg:", StringComparison.Ordinal) || reference.StartsWith("line:", StringComparison.Ordinal),
                reference);
            Assert.Equal(reference, ItemRefs.Format(ItemRefs.Parse(reference)));
        }
    }

    /// <summary>The call is one entry of the agent with the label of the tool, the result gives the new version and
    /// an inverse, and one undo puts back the state before the call.</summary>
    private static void AssertOneEntry(MissionEdits m, JsonObject result, string label, string before)
    {
        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal((EditOrigin.Agent, "Agent: " + label), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.Equal("Agent: " + label, (string?)result["undo_label"]);
        Assert.Equal(2, (long?)result["versions"]![Script]);
        Assert.NotNull(result["inverse"]);
        AssertCanonical(result);
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>mission_add_trigger adds the trigger with its lines, and a Start trigger action to each trigger of
    /// started_by, in one entry. The inverse removes both after a later edit of another trigger.</summary>
    [Fact]
    public void AddTriggerAddsTheTriggerAndTheStartsOfItsStarters()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var arguments = $$"""
            { "script": "msn:{{S}}", "nickname": "ol_ambush", "system": "ol_system", "repeats": true,
              "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } },
              "actions": [ { "key": "Act_SpawnShip", "values": { "ship": "ol_ship_3" } },
                           { "key": "Act_ActTrig", "values": { "trigger": "ol_end" } } ],
              "started_by": [ "{{Wave}}" ] }
            """;

        var result = Ok(host, "mission_add_trigger", arguments);

        Assert.Equal([$"trg:{Script}#ol_ambush", $"line:{Script}#Trigger/ol_wave/6"], Texts(result["adds"]));
        Assert.Equal(["nickname = ol_ambush", "system = ol_system", "Cnd_Timer = 3", "repeatable = true",
            "Act_SpawnShip = ol_ship_3", "Act_ActTrig = ol_end"], m.Lines("ol_ambush"));
        Assert.Equal("Act_ActTrig = ol_ambush", m.Lines("ol_wave")[^1]);
        Assert.Equal([("mission_add_trigger", "Agent: Add trigger ol_ambush")], host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));
        AssertOneEntry(m, result, "Add trigger ol_ambush", before);

        var again = Ok(host, "mission_add_trigger", arguments);
        Ok(host, "mission_set_condition", $$"""{ "trigger": "{{End}}", "condition": { "key": "Cnd_Timer", "values": { "seconds": 8 } } }""");
        Ok(host, "mission_edit", Edit(again["inverse"]!.ToJsonString()));
        Assert.False(m.Has("ol_ambush"));
        Assert.Equal("Act_ActTrig = ol_end", m.Lines("ol_wave")[^1]);
        Assert.Equal("Cnd_Timer = 8", m.Lines("ol_end")[1]);
    }

    /// <summary>Without a nickname, the layer makes a free name. A trigger with starters gets the made name before the
    /// request, as the gesture of the graph does, and a starter can be a nickname. A trigger can start with the
    /// mission.</summary>
    [Fact]
    public void AddTriggerWithoutANicknameGetsAFreeName()
    {
        var (m, host) = Open();
        using var _ = m;

        var plain = Ok(host, "mission_add_trigger",
            $$"""{ "script": "msn:{{S}}", "condition": { "key": "Cnd_True" }, "starts_with_mission": true }""");
        var started = Ok(host, "mission_add_trigger",
            $$"""{ "script": "msn:{{S}}", "condition": { "key": "Cnd_True" }, "started_by": [ "ol_end" ] }""");

        Assert.Equal([$"trg:{Script}#ol_test_trigger_1"], Texts(plain["adds"]));
        Assert.Equal(["nickname = ol_test_trigger_1", "InitState = ACTIVE", "Cnd_True ="], m.Lines("ol_test_trigger_1"));
        Assert.Equal([$"trg:{Script}#ol_test_trigger_2", $"line:{Script}#Trigger/ol_end/4"], Texts(started["adds"]));
        Assert.Equal("Act_ActTrig = ol_test_trigger_2", m.Lines("ol_end")[^1]);
    }

    /// <summary>mission_set_condition replaces the condition of a trigger.</summary>
    [Fact]
    public void SetConditionReplacesTheCondition()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_set_condition",
            $$"""{ "trigger": "{{End}}", "condition": { "key": "Cnd_Timer", "values": { "seconds": 5 } } }""");

        Assert.Equal([$"line:{Script}#Trigger/ol_end/1"], Texts(result["changes"]));
        Assert.Equal("Cnd_Timer = 5", m.Lines("ol_end")[1]);
        AssertOneEntry(m, result, "Set the condition of ol_end", before);
    }

    /// <summary>mission_add_action adds an action before a line of the same trigger. The later lines move one row,
    /// and the result tells it. A sec: reference of a trigger section names the trigger too.</summary>
    [Fact]
    public void AddActionAddsTheActionBeforeALine()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_add_action", $$"""
            { "trigger": "sec:{{S}}#Trigger/ol_end", "action": { "key": "Act_SpawnShip", "values": { "ship": "ol_ship_3" } },
              "before": "line:{{S}}#Trigger/ol_end/3" }
            """);

        Assert.Equal([$"line:{Script}#Trigger/ol_end/3"], Texts(result["adds"]));
        Assert.Equal(["nickname = ol_end", "Cnd_Destroyed = ol_ship_1, -1", "Act_LockDock = Player, ol_station, lock",
            "Act_SpawnShip = ol_ship_3", "Act_ChangeState = SUCCEED"], m.Lines("ol_end"));
        var move = Assert.Single(result["row_moves"]!.AsArray());
        Assert.Equal(($"trg:{Script}#ol_end", 3, 4), ((string?)move!["trigger"], (int?)move["from"], (int?)move["to"]));
        AssertOneEntry(m, result, "Add Act_SpawnShip to ol_end", before);

        Ok(host, "mission_add_action", $$"""{ "trigger": "{{End}}", "action": { "key": "Act_ChangeState", "texts": [ "FAIL" ] } }""");
        Assert.Equal("Act_ChangeState = FAIL", m.Lines("ol_end")[^1]);
    }

    /// <summary>mission_set_action sets each value of the request in one entry. expect_key guards the line.</summary>
    [Fact]
    public void SetActionSetsTheValuesOfALine()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_set_action", $$"""
            { "line": "line:{{S}}#Trigger/ol_end/2", "values": { "who": "Everyone", "action": "unlock" }, "expect_key": "Act_LockDock" }
            """);

        Assert.Equal([$"line:{Script}#Trigger/ol_end/2"], Texts(result["changes"]));
        Assert.Equal("Act_LockDock = Everyone, ol_station, unlock", m.Lines("ol_end")[2]);
        AssertOneEntry(m, result, "Set who, action", before);

        var one = Ok(host, "mission_set_action", $$"""{ "line": "{{WaveLine(3)}}", "values": { "ship": "ol_ship_3" } }""");
        Assert.Equal("Agent: Set ship", (string?)one["undo_label"]);
        Assert.Equal("Act_SpawnShip = ol_ship_3", m.Lines("ol_wave")[3]);
    }

    /// <summary>mission_move_action moves an action before another line of its trigger. The result gives the row
    /// move.</summary>
    [Fact]
    public void MoveActionMovesTheLineInItsTrigger()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_move_action", $$"""{ "line": "{{WaveLine(5)}}", "before": "{{WaveLine(3)}}" }""");

        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 3", "Act_ActTrig = ol_end",
            "Act_SpawnShip = ol_ship_1", "Act_DeactTrig = ol_end"], m.Lines("ol_wave"));
        Assert.Equal([$"line:{Script}#Trigger/ol_wave/3"], Texts(result["changes"]));
        var move = Assert.Single(result["row_moves"]!.AsArray());
        Assert.Equal(($"trg:{Script}#ol_wave", 5, 3), ((string?)move!["trigger"], (int?)move["from"], (int?)move["to"]));
        AssertOneEntry(m, result, "Move action", before);

        Ok(host, "mission_move_action", $$"""{ "line": "{{WaveLine(3)}}" }""");
        Assert.Equal("Act_SpawnShip = ol_ship_1", m.Lines("ol_wave")[^1]);
    }

    /// <summary>mission_link_triggers adds the lines of a link, and removes them with remove. A restart writes the stop
    /// first. The target can be a trg: reference of the same script.</summary>
    [Fact]
    public void LinkTriggersAddsAndRemovesALink()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_link_triggers", $$"""{ "from": "{{End}}", "to": "{{Wave}}", "kind": "restart" }""");

        Assert.Equal([$"line:{Script}#Trigger/ol_end/4", $"line:{Script}#Trigger/ol_end/5"], Texts(result["adds"]));
        Assert.Equal(["Act_DeactTrig = ol_wave", "Act_ActTrig = ol_wave"], m.Lines("ol_end")[4..]);
        AssertOneEntry(m, result, "Link ol_end to ol_wave", before);

        var removed = Ok(host, "mission_link_triggers", $$"""{ "from": "{{Wave}}", "to": "ol_end", "kind": "restart", "remove": true }""");
        Assert.Equal("applied", (string?)removed["status"]);
        Assert.Equal(["system = ol_system", "nickname = ol_wave", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1"], m.Lines("ol_wave"));
        var missing = Error(host, "mission_link_triggers", $$"""{ "from": "{{Wave}}", "to": "ol_end", "kind": "stop", "remove": true }""");
        Assert.Equal(("not_found", MissionErrors.LinkNotFound), (missing.Code, missing.Detail));
    }

    /// <summary>mission_insert_trigger puts a new trigger on a start link. Its actions come before its start of the old
    /// target, in their order.</summary>
    [Fact]
    public void InsertTriggerPutsANewTriggerOnALink()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_insert_trigger", $$"""
            { "line": "line:{{S}}#Trigger/ol_start/3", "nickname": "ol_delay",
              "condition": { "key": "Cnd_Timer", "values": { "seconds": 2 } },
              "actions": [ { "key": "Act_SpawnShip", "values": { "ship": "ol_ship_3" } },
                           { "key": "Act_ChangeState", "values": { "state": "FAIL" } } ] }
            """);

        Assert.Equal("Act_ActTrig = ol_delay", m.Lines("ol_start")[3]);
        Assert.Equal(["nickname = ol_delay", "Cnd_Timer = 2", "Act_SpawnShip = ol_ship_3", "Act_ChangeState = FAIL",
            "Act_ActTrig = ol_wave"], m.Lines("ol_delay"));
        Assert.Contains($"trg:{Script}#ol_delay", Texts(result["adds"]));
        Assert.Contains($"line:{Script}#Trigger/ol_start/3", Texts(result["changes"]));
        AssertOneEntry(m, result, "Insert trigger", before);

        var plain = Ok(host, "mission_insert_trigger",
            $$"""{ "line": "line:{{S}}#Trigger/ol_start/4", "condition": { "key": "Cnd_True" } }""");
        Assert.Contains($"trg:{Script}#ol_test_trigger_1", Texts(plain["adds"]));
        Assert.Equal(["nickname = ol_test_trigger_1", "Cnd_True =", "Act_ActTrig = ol_end"], m.Lines("ol_test_trigger_1"));
    }

    /// <summary>The refusals of the trigger tools (section 13.1, item 8 of the MCP1 design): invalid_value for a bad
    /// key or value, not_found with the closest names, name_taken, stale_revision with RowChanged, and invalid_argument
    /// for a line of another trigger, a starter of another script and a nickname that does not fit the pattern. Each
    /// error names its argument, and nothing changes.</summary>
    [Fact]
    public void TheTriggerToolsGiveTheirRefusals()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        const string other = @"trg:DATA\\MISSIONS\\OL_OTHER\\ol_other.ini#ol_start";

        var refusals = new (string Tool, string Arguments, string Code, string Detail, string Field)[]
        {
            ("mission_add_action", $$"""{ "trigger": "{{End}}", "action": { "key": "Act_Nothing" } }""",
                "invalid_value", MissionErrors.UnknownKey, "action"),
            ("mission_add_trigger", $$"""{ "script": "msn:{{S}}", "condition": { "key": "Act_ActTrig", "values": { "trigger": "ol_end" } } }""",
                "invalid_value", MissionErrors.WrongPlace, "condition"),
            ("mission_add_trigger", $$"""
                { "script": "msn:{{S}}", "condition": { "key": "Cnd_True" },
                  "actions": [ { "key": "Act_ActTrig", "values": { "trigger": "ol_end" } }, { "key": "Act_SpawnShip", "values": { "shp": "x" } } ] }
                """, "invalid_argument", MissionErrors.NoSuchValue, "actions[1]"),
            ("mission_set_condition", $$"""{ "trigger": "{{End}}", "condition": { "key": "Cnd_Timer", "values": { "seconds": "soon" } } }""",
                "invalid_value", MissionErrors.ValueKind, "condition"),
            ("mission_set_action", $$"""{ "line": "{{WaveLine(3)}}", "values": { "shp": "ol_ship_3" } }""",
                "invalid_argument", MissionErrors.NoSuchValue, "values.shp"),
            ("mission_set_condition", $$"""{ "trigger": "trg:{{S}}#ol_wav", "condition": { "key": "Cnd_True" } }""",
                "not_found", MissionErrors.TriggerNotFound, "trigger"),
            ("mission_add_trigger", $$"""{ "script": "msn:{{S}}", "condition": { "key": "Cnd_True" }, "started_by": [ "ol_strat" ] }""",
                "not_found", MissionErrors.TriggerNotFound, "started_by[0]"),
            ("mission_move_action", $$"""{ "line": "line:{{S}}#Trigger/ol_wave/9" }""", "not_found", MissionErrors.RowNotFound, "line"),
            ("mission_add_trigger", $$"""{ "script": "msn:{{S}}", "nickname": "OL_WAVE", "condition": { "key": "Cnd_True" } }""",
                "name_taken", MissionErrors.NameTaken, "nickname"),
            ("mission_insert_trigger", $$"""{ "line": "line:{{S}}#Trigger/ol_start/3", "nickname": "123", "condition": { "key": "Cnd_True" } }""",
                "invalid_value", MissionErrors.NameInvalid, "nickname"),
            ("mission_set_action", $$"""{ "line": "{{WaveLine(3)}}", "values": { "ship": "ol_ship_3" }, "expect_key": "Act_ChangeState" }""",
                "stale_revision", MissionErrors.RowChanged, "line"),
            ("mission_add_action", $$"""
                { "trigger": "{{End}}", "action": { "key": "Act_ChangeState", "texts": [ "FAIL" ] }, "before": "{{WaveLine(3)}}" }
                """,
                "invalid_argument", MissionEditTools.OtherItem, "before"),
            ("mission_add_trigger", $$"""{ "script": "msn:{{S}}", "condition": { "key": "Cnd_True" }, "started_by": [ "{{other}}" ] }""",
                "invalid_argument", MissionEditTools.OtherItem, "started_by[0]"),
            ("mission_link_triggers", $$"""{ "from": "{{Wave}}", "to": "{{other}}" }""", "invalid_argument", MissionEditTools.OtherItem, "to"),
            ("mission_add_trigger", $$"""{ "script": "msn:{{S}}", "nickname": "ol bad", "condition": { "key": "Cnd_True" } }""",
                "invalid_argument", ToolInput.BadArgument, "nickname"),
            // In .NET, '$' also matches before a newline at the end. The pattern of JSON Schema does not.
            ("mission_add_trigger", $$"""{ "script": "msn:{{S}}", "nickname": "ol_bad\n", "condition": { "key": "Cnd_True" } }""",
                "invalid_argument", ToolInput.BadArgument, "nickname"),
            ("mission_add_trigger", $$"""{ "script": "msn:{{S}}", "condition": { "key": "Cnd_True", "valus": {} } }""",
                "invalid_argument", ToolInput.BadArgument, "condition"),
            ("mission_set_condition", $$"""{ "trigger": "{{WaveLine(2)}}", "condition": { "key": "Cnd_True" } }""",
                "invalid_argument", "BadReference", "trigger"),
            ("mission_insert_trigger", $$"""{ "line": "{{WaveLine(3)}}", "condition": { "key": "Cnd_True" } }""",
                "invalid_argument", MissionErrors.NotALink, "line"),
        };

        var wrong = new List<string>();
        foreach (var (tool, arguments, code, detail, field) in refusals)
        {
            var result = Call(host, tool, arguments);
            var got = ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]);
            if (!result.IsError || got != (code, detail, field))
                wrong.Add($"{tool} {arguments}: {got} {result.Content["message"]}");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        var closest = Error(host, "mission_set_condition", $$"""{ "trigger": "trg:{{S}}#ol_wav", "condition": { "key": "Cnd_True" } }""");
        Assert.Contains("The closest names are: ol_wave.", closest.Message);
        var actions = Enumerable.Repeat("""{ "key": "Act_ChangeState", "texts": [ "FAIL" ] }""", MissionEditTools.MaxActions + 1);
        var tooMany = Error(host, "mission_add_trigger",
            $$"""{ "script": "msn:{{S}}", "condition": { "key": "Cnd_True" }, "actions": [ {{string.Join(", ", actions)}} ] }""");
        Assert.Equal(("invalid_argument", ToolInput.BadArgument), (tooMany.Code, tooMany.Detail));
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>The automated form of manual step 41 (section 14.4 of the MCP1 design): the pirate ambush of workflow 3
    /// in one edit_batch. The first step names the second trigger and the loot before the later steps add them,
    /// because the strict check runs once at the commit. The batch is one entry, and one undo removes both triggers,
    /// the link and the loot.</summary>
    [Fact]
    public void ThePirateAmbushIsOneEntryAndOneUndoRemovesIt()
    {
        var (m, host) = Open();
        using var _ = m;
        m.Missions.UseWorld(new TestMissionWorld(m.Session, new Dictionary<MissionValueKind, string[]>(WorldNames)
        {
            [MissionValueKind.Equipment] = ["ol_good"],
        }));
        var before = m.State();

        var result = Ok(host, "edit_batch", $$"""
            { "label": "Add the pirate ambush", "steps": [
              { "tool": "mission_add_trigger", "arguments": { "script": "msn:{{S}}", "nickname": "ol_ambush",
                "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } },
                "actions": [ { "key": "Act_SpawnShip", "values": { "ship": "ol_ship_3" } },
                             { "key": "Act_ActTrig", "values": { "trigger": "ol_reward" } } ],
                "started_by": [ "{{Wave}}" ] } },
              { "tool": "mission_add_trigger", "arguments": { "script": "msn:{{S}}", "nickname": "ol_reward",
                "condition": { "key": "Cnd_Destroyed", "values": { "target": "ol_ship_3" } },
                "actions": [ { "key": "Act_SpawnLoot", "values": { "loot": "ol_crate" } } ] } },
              { "tool": "mission_edit", "arguments": { "commands": [ { "kind": "add_item", "script": "msn:{{S}}",
                "item_kind": "loot", "nickname": "ol_crate", "lines": [
                  { "key": "archetype", "texts": [ "ol_good" ] }, { "key": "equip_amount", "texts": [ "1" ] },
                  { "key": "rel_pos_obj", "texts": [ "ol_ship_3" ] } ] } ] } } ] }
            """);

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal((EditOrigin.Agent, "Agent: Add the pirate ambush"), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.True(m.Has("ol_ambush") && m.Has("ol_reward") && m.Has(Item("MsnLoot", "ol_crate")));
        Assert.Equal("Act_ActTrig = ol_ambush", m.Lines("ol_wave")[^1]);
        Assert.Equal([$"trg:{Script}#ol_ambush", $"line:{Script}#Trigger/ol_wave/6", $"trg:{Script}#ol_reward",
            $"sec:{Script}#MsnLoot/ol_crate"], Texts(result["adds"]));
        Assert.Single(host.Edits);

        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>The trigger tools are in the group Missions, so they refuse while the user hides the group (decision
    /// D725 of the MCP1 design). Pause refuses them too.</summary>
    [Fact]
    public void AHiddenGroupAndPauseRefuseTheTriggerTools()
    {
        var (m, host) = Open();
        using var _ = m;
        var tools = AgentTools.All.Where(t => t is { Kind: ToolKind.Edit, Group: ToolGroup.Missions, Name: not "mission_edit" })
            .Select(t => t.Name).ToList();

        host.Hidden.Add(ToolGroup.Missions);
        var hidden = tools.Select(t => Error(host, t, $$"""{ "trigger": "{{End}}", "condition": { "key": "Cnd_True" } }""")).ToList();
        host.Hidden.Clear();
        host.TestPaused = true;
        var paused = tools.Select(t => Error(host, t, "{}")).ToList();

        Assert.Equal(["mission_add_trigger", "mission_set_condition", "mission_add_action", "mission_set_action",
            "mission_move_action", "mission_link_triggers", "mission_insert_trigger"], tools);
        Assert.All(hidden, e => Assert.Equal(("not_available", "GroupHidden"), (e.Code, e.Detail)));
        Assert.All(paused, e => Assert.Equal(("paused", "Paused"), (e.Code, e.Detail)));
        Assert.Equal(0, m.Session.Revision);
    }

    /// <summary>The automated form of manual step 42 (section 14.4 of the MCP1 design): an action that starts a trigger
    /// that does not exist adds an error, so the call gives new_errors. The revision, the version and W stay.</summary>
    [Fact]
    public void AnActionThatStartsATriggerThatDoesNotExistChangesNothing()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var error = Error(host, "mission_add_action",
            $$"""{ "trigger": "{{Wave}}", "action": { "key": "Act_ActTrig", "values": { "trigger": "ol_nowhere" } } }""");

        Assert.Equal(("new_errors", MissionErrors.NewErrors), (error.Code, error.Detail));
        Assert.Contains("ol_nowhere", error.Message);
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);
        Assert.Equal(1, m.Missions.Version(Script));
        Assert.Equal(["mission_add_action"], host.Refusals.Select(r => r.Tool));
    }

    /// <summary>A trigger tool is a step of edit_batch and takes a preview. The preview changes nothing and gives the
    /// adds of the real call.</summary>
    [Fact]
    public void ATriggerToolHasAPreviewAndIsAStepOfABatch()
    {
        var (m, host) = Open();
        using var _ = m;
        var arguments = $$"""{ "trigger": "{{End}}", "action": { "key": "Act_ChangeState", "values": { "state": "FAIL" } } }""";

        var preview = Ok(host, "mission_add_action", arguments[..^1] + """, "preview": true }""");
        Assert.Equal(("previewed", "0"), ((string?)preview["status"], (string?)preview["rev"]));
        Assert.Equal(1, m.Missions.Version(Script));
        Assert.False(m.Session.CanUndo);
        var batch = Ok(host, "edit_batch", $$"""
            { "steps": [ { "tool": "mission_add_action", "arguments": {{arguments}} },
                         { "tool": "mission_move_action",
                           "arguments": { "line": "line:{{S}}#Trigger/ol_end/4", "before": "line:{{S}}#Trigger/ol_end/2" } } ] }
            """);

        Assert.Equal(Texts(preview["adds"]), Texts(batch["steps"]![0]!["adds"]));
        Assert.All(batch["steps"]!.AsArray(), s => Assert.NotNull(s!["inverse"]));
        Assert.Equal("Act_ChangeState = FAIL", m.Lines("ol_end")[2]);
        Assert.Equal(EditOrigin.Agent, m.Session.UndoOrigin);
        m.Session.Undo();
        Assert.False(m.Session.CanUndo);
    }

    // ---- Parity with the mission studio ----

    /// <summary>
    /// The parity of section 13.1, item 7 and section 8.1 of the MCP1 design. The gesture of the mission studio makes
    /// its batch with the graph model, and the mission editor runs it with the edit origin User. The tool runs the same
    /// command with the edit origin Agent. Both give the same text of each file that a save writes, and the same graph
    /// file. The commands are AddTrigger, SetCondition, AddAction, LinkTriggers and InsertTriggerOnLink.
    /// </summary>
    [Fact]
    public void TheTriggerToolsGiveTheTextOfTheGesturesOfTheMissionStudio()
    {
        var items = MissionLibrary.Sections(null).SelectMany(s => s.Items).ToList();
        var delay = items.Single(i => i.Id == "afterDelay");
        var state = items.Single(i => i.Key == "Act_ChangeState" && i.Kind == LibraryKind.Action);
        var stateLine = MissionJson.Write(MissionLibrary.Line(state));

        AssertParity(model => model.DropOnEmpty(delay), "mission_add_trigger",
            $$"""{ "script": "msn:{{S}}", "nickname": "ol_test_trigger_1", "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } } }""");
        AssertParity(model => model.DropOnNode(delay, NodeOf(model, "ol_end")), "mission_set_condition",
            $$"""{ "trigger": "{{End}}", "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } } }""");
        AssertParity(model => model.DropOnNode(state, NodeOf(model, "ol_end"), 3), "mission_add_action",
            $$"""{ "trigger": "{{End}}", "action": {{stateLine}}, "before": "line:{{S}}#Trigger/ol_end/3" }""");
        AssertParity(model => model.Link(new GraphEnd(NodeOf(model, "ol_end"), null),
                new GraphEnd(NodeOf(model, "ol_wave"), NodeOf(model, "ol_wave").Pin(GraphPinKind.Start)!)).Batch,
            "mission_link_triggers", $$"""{ "from": "{{End}}", "to": "ol_wave" }""");
        AssertParity(model => model.InsertOnLink(model.Graph.Links.First(l => l.From == NodeOf(model, "ol_start") &&
                                                                               l.To == NodeOf(model, "ol_wave")),
                GraphEditModel.InsertCondition(InsertEvent.Delay)),
            "mission_insert_trigger", $$"""
                { "line": "line:{{S}}#Trigger/ol_start/3", "nickname": "ol_test_trigger_1",
                  "condition": { "key": "Cnd_Timer", "values": { "seconds": 3 } } }
                """);
    }

    /// <summary>The graph model of the test script, at the lines level, with all frames open.</summary>
    private static GraphEditModel ModelOf(MissionEdits m) =>
        new(MissionGraph.Build(m.Missions.Model(Script).Analysis, new GraphOptions(GraphLens.Logic, ShowLines: true)));

    private static GraphNode NodeOf(GraphEditModel model, string trigger) =>
        model.Graph.TriggerNode(model.Graph.Index.Trigger(trigger)!)!;

    /// <summary>The gesture through the mission editor and the tool give the same files (see
    /// <see cref="TheTriggerToolsGiveTheTextOfTheGesturesOfTheMissionStudio"/>).</summary>
    private static void AssertParity(Func<GraphEditModel, MissionBatch?> gesture, string tool, string arguments)
    {
        using var ui = Create();
        var batch = gesture(ModelOf(ui));
        Assert.NotNull(batch);
        var applied = ui.Missions.Apply(batch);
        Assert.True(applied.Status == EditStatus.Applied, string.Join("\n", applied.Errors.Select(e => e.Message)));

        var (agent, host) = Open();
        using var _ = agent;
        Ok(host, tool, arguments);

        Assert.Equal((EditOrigin.User, EditOrigin.Agent), (ui.Session.UndoOrigin, agent.Session.UndoOrigin));
        Assert.Equal(ui.Session.EditedPaths.Order(), agent.Session.EditedPaths.Order());
        foreach (var path in ui.Session.EditedPaths)
            Assert.Equal(ui.Session.SaveText(path), agent.Session.SaveText(path));
        Assert.Equal(ui.Session.ModFileText(GraphFile.PathOf(Script)), agent.Session.ModFileText(GraphFile.PathOf(Script)));
        Assert.Equal(ui.State(), agent.State());
    }
}
