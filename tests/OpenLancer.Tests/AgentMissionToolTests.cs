using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Games;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The tool <c>mission_edit</c> (section 7.4 of the MCP1 design, step 1 of MCP1-3). A mission edit of an agent runs in
/// the shared batch of the call. So it is one Undo step with the edit origin Agent, it has a preview, confirm,
/// if_revision and the limits, and it runs in strict mode. The tests cover section 13.1, items 1, 5, 6, 8, 9, 10, 12 and
/// 15, and section 13.2, items 6 and 7. The tests of the trigger tools are in <c>AgentMissionToolTests.Triggers.cs</c>.
/// </summary>
public sealed partial class AgentMissionToolTests
{
    /// <summary>The install path of the test script as JSON text.</summary>
    private static readonly string S = Script.Replace(@"\", @"\\");

    private static readonly string Wait9 =
        $$"""{ "kind": "set_condition_value", "trigger": "trg:{{S}}#ol_wave", "name": "seconds", "value": 9 }""";

    private static readonly string AddAgentTrigger = $$"""
        { "kind": "add_trigger", "script": "msn:{{S}}", "nickname": "ol_agent",
          "condition": { "key": "Cnd_Timer", "values": { "seconds": 2 } },
          "actions": [ { "key": "Act_ActTrig", "values": { "trigger": "ol_end" } } ] }
        """;

    private static readonly string StartNowhere =
        $$"""{ "kind": "add_action", "trigger": "trg:{{S}}#ol_wave", "line": { "key": "Act_ActTrig", "values": { "trigger": "ol_nowhere" } } }""";

    private static readonly string RemoveEnd = $$"""{ "kind": "remove_trigger", "trigger": "trg:{{S}}#ol_end" }""";

    private static (MissionEdits M, TestAgentHost Host) Open(string? text = null, Action<TestWorkspace>? setup = null,
        System.Collections.Generic.Dictionary<string, string>? modFiles = null)
    {
        var m = Create(text, setup: setup, modFiles: modFiles);
        return (m, new TestAgentHost(m.Session));
    }

    private static string Edit(params string[] commands) => $$"""{ "commands": [ {{string.Join(", ", commands)}} ] }""";

    private static string[] Texts(JsonNode? array) => array!.AsArray().Select(t => (string)t!).ToArray();

    /// <summary>One call is one entry with the edit origin Agent and the label "Agent: ..." (section 13.1, item 5). The
    /// result gives the new versions and the inverse. One undo puts back W, and the version of the script grows again,
    /// because an undo changes W too (decision D764).</summary>
    [Fact]
    public void AMissionEditIsOneAgentEntryAndOneUndoPutsBackW()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_edit", $$"""
            { "label": "Wait longer", "expect_versions": { "{{S}}": 1 }, "commands": [ {{Wait9}}, {{AddAgentTrigger}} ] }
            """);

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal(("Wait longer", "Agent: Wait longer"), ((string?)result["label"], (string?)result["undo_label"]));
        Assert.Equal((EditOrigin.Agent, "Agent: Wait longer"), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.Equal([$"trg:{Script}#ol_agent"], Texts(result["adds"]));
        Assert.Equal([$"line:{Script}#Trigger/ol_wave/2"], Texts(result["changes"]));
        Assert.Equal(2, (long?)result["versions"]![Script]);
        Assert.Equal("1", (string?)result["rev"]);
        Assert.Equal("command_group", (string?)result["inverse"]!["kind"]);
        Assert.Contains("Cnd_Timer = 9", m.Lines("ol_wave"));
        Assert.Equal([("mission_edit", "Agent: Wait longer")], host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));

        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.Equal(3, m.Missions.Version(Script));
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>A call without a label takes the label of its one command. The tool undo undoes the mission entry of the
    /// agent, as each agent entry (section 13.1, item 9).</summary>
    [Fact]
    public void TheUndoToolUndoesAMissionEntryOfTheAgent()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_edit", Edit(Wait9));
        var undo = Ok(host, "undo");

        Assert.Equal("Agent: Set seconds of the condition of ol_wave", (string?)result["undo_label"]);
        Assert.Equal("Agent: Set seconds of the condition of ol_wave", (string?)undo["label"]);
        Assert.Equal(before, m.State());
        Assert.Equal(3, m.Missions.Version(Script));
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>A preview keeps the revision, the versions, the unsaved state, the edited paths and the history. No
    /// <see cref="EditSession.Changed"/> fires. It gives the adds of the real call (section 13.1, item 6).</summary>
    [Fact]
    public void APreviewChangesNothingAndGivesTheResultOfTheRealCall()
    {
        var (m, host) = Open();
        using var _ = m;
        var changed = 0;
        m.Session.Changed += _ => changed++;
        var paths = m.Session.EditedPaths;

        var preview = Ok(host, "mission_edit", $$"""{ "commands": [ {{AddAgentTrigger}} ], "preview": true, "with_patch": true }""");

        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Null(preview["undo_label"]);
        Assert.Equal("0", (string?)preview["rev"]);
        Assert.Equal(1, (long?)preview["versions"]![Script]);
        Assert.NotNull(preview["inverse"]);
        Assert.Equal((0, false, false), (changed, m.Session.IsDirty, m.Session.CanUndo));
        Assert.Equal(paths, m.Session.EditedPaths);
        Assert.Null(m.Session.OpenBatch);
        Assert.False(m.Has("ol_agent"));
        Assert.Equal(1, m.Missions.Version(Script));
        Assert.Contains("nickname = ol_agent", (string?)preview["patch"]![Script]);
        Assert.Empty(host.Edits);

        var real = Ok(host, "mission_edit", Edit(AddAgentTrigger));
        Assert.Equal(Texts(preview["adds"]), Texts(real["adds"]));
        Assert.Equal(1, changed);
    }

    /// <summary>A call and a preview with no net change give no_change, no entry and no new revision (contract
    /// additions 10 and 11 of the MCP1 design).</summary>
    [Fact]
    public void AnEditWithNoNetChangeGivesNoChange()
    {
        var (m, host) = Open();
        using var _ = m;
        var same = $$"""{ "kind": "set_condition_value", "trigger": "trg:{{S}}#ol_wave", "name": "seconds", "value": 3 }""";

        var preview = Ok(host, "mission_edit", $$"""{ "commands": [ {{same}} ], "preview": true }""");
        var real = Ok(host, "mission_edit", Edit(Wait9, same));

        Assert.Equal("no_change", (string?)preview["status"]);
        Assert.Equal("no_change", (string?)real["status"]);
        Assert.Null(real["inverse"]);
        Assert.Empty(Texts(real["changes"]));
        Assert.Equal(0, m.Session.Revision);
        Assert.False(m.Session.CanUndo);
        Assert.Empty(host.Edits);
    }

    /// <summary>The result of a call names each item as it is after all its commands, as the result of each edit tool
    /// does. A trigger that the call adds and then removes gives no add, no delete and no select. A trigger that the
    /// call adds and then renames is an add with its new name.</summary>
    [Fact]
    public void TheResultOfACallIsTheNetResultOfItsCommands()
    {
        var (m, host) = Open();
        using var _ = m;
        var add = $$"""
            { "kind": "add_trigger", "script": "msn:{{S}}", "nickname": "ol_probe",
              "condition": { "key": "Cnd_True", "values": {} } }
            """;
        var remove = $$"""{ "kind": "remove_trigger", "trigger": "trg:{{S}}#ol_probe" }""";
        var rename = $$"""{ "kind": "rename_trigger", "trigger": "trg:{{S}}#ol_probe", "new_name": "ol_probe_2" }""";

        var gone = Ok(host, "mission_edit", $$"""{ "commands": [ {{add}}, {{remove}} ], "confirm": true }""");
        var renamed = Ok(host, "mission_edit", Edit(add, rename));

        Assert.Empty(Texts(gone["adds"]));
        Assert.Empty(gone["deletes"]!.AsArray());
        Assert.Null((string?)gone["select"]);
        Assert.Equal([$"trg:{Script}#ol_probe_2"], Texts(renamed["adds"]));
        Assert.Empty(renamed["renames"]!.AsArray());
        Assert.Equal($"trg:{Script}#ol_probe_2", (string?)renamed["select"]);
    }

    /// <summary>The tool runs in strict mode: an edit that adds an error gives new_errors, and nothing changes. A
    /// request cannot turn strict mode off. It can also refuse new warnings (section 13.1, item 15).</summary>
    [Fact]
    public void TheToolRunsInStrictMode()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var lonely = $$"""{ "kind": "add_trigger", "script": "msn:{{S}}", "nickname": "ol_lonely", "condition": { "key": "Cnd_True" } }""";

        var plain = Error(host, "mission_edit", Edit(StartNowhere));
        var off = Call(host, "mission_edit", $$"""{ "commands": [ {{StartNowhere}} ], "options": { "reject_new_errors": false } }""");
        var warned = Ok(host, "mission_edit", $$"""{ "commands": [ {{lonely}} ], "preview": true }""");
        var warnings = Error(host, "mission_edit",
            $$"""{ "commands": [ {{lonely}} ], "options": { "reject_new_errors": true, "reject_new_warnings": true } }""");

        Assert.Equal(("new_errors", "NewErrors"), (plain.Code, plain.Detail));
        Assert.StartsWith("The edit adds 1 error, so the editor did not apply it.", plain.Message);
        Assert.True(off.IsError);
        Assert.Equal(("invalid_argument", MissionEditTools.StrictMode, "options.reject_new_errors"),
            ((string?)off.Content["code"], (string?)off.Content["detail"], (string?)off.Content["field"]));
        Assert.Contains(warned["new_problems"]!.AsArray(), p => (string?)p!["severity"] == "warning");
        Assert.DoesNotContain(warned["new_problems"]!.AsArray(), p => (string?)p!["severity"] == "error");
        Assert.Equal(("new_errors", MissionErrors.NewWarnings), (warnings.Code, warnings.Detail));
        Assert.StartsWith("The edit adds 1 warning, so the editor did not apply it.", warnings.Message);
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);
        Assert.Equal(["mission_edit", "mission_edit", "mission_edit"], host.Refusals.Select(r => r.Tool));
    }

    /// <summary>Strict mode compares only the problems of the mission checks. The save gate has more rules. In a mod
    /// that names a retail target, one rule is that the loadout of a ship build has an engine. A manifest of format
    /// version 1 names each target, so the mod of the test names the retail targets. Strict mode applies the build.
    /// check with the scope mission does not give the error. check with the scope all gives it, and save gives
    /// save_blocked. So step 8 of the prompt two_trigger_mission also calls check with the scope all.</summary>
    [Fact]
    public void OnlyTheScopeAllGivesTheGateErrorOfAShipBuildWithoutAnEngine()
    {
        var (m, host) = Open(setup: ws =>
        {
            ws.WriteBase(@"EXE\freelancer.ini", $"""
                [Freelancer]
                data path = ..\data

                {TestWorkspace.ResourcesSection}

                [Data]
                equipment = equipment\engine_equip.ini
                loadouts = ships\loadouts.ini
                """);
            ws.WriteBase(@"DATA\EQUIPMENT\engine_equip.ini", "[Engine]\nnickname = ol_engine\n");
            ws.WriteBase(@"DATA\SHIPS\loadouts.ini", """
                [Loadout]
                nickname = ol_loadout
                equip = ol_engine

                [Loadout]
                nickname = ol_loadout_2
                """);
        });
        using var _ = m;
        var script = Json($"msn:{Script}");

        var added = Ok(host, "mission_add_item", AddItem("ship_build", "ol_build_2",
            """{ "ship_archetype": "ol_arch", "loadout": "ol_loadout_2", "level": "d5" }"""));
        var mission = Ok(host, "check", $$"""{ "scope": "mission", "ref": {{script}} }""");
        var all = Ok(host, "check");
        var saved = Error(host, "save");

        Assert.Equal("applied", (string?)added["status"]);
        Assert.Equal((0, false), ((int)mission["errors"]!, (bool)mission["save_blocked"]!));
        Assert.Equal((1, true), ((int)all["errors"]!, (bool)all["save_blocked"]!));
        var error = all["problems"]!.AsArray().Single(p => (string?)p!["severity"] == "error")!;
        Assert.Equal($"sec:{Ships}#NPCShipArch/ol_build_2", (string?)error["ref"]);
        Assert.StartsWith("NPC ship 'ol_build_2': its loadout 'ol_loadout_2' has no engine.", (string?)error["message"]);
        Assert.Equal(("save_blocked", "Gate"), (saved.Code, saved.Detail));
        Assert.Contains("has no engine", saved.Message);
        Assert.True(m.Session.IsDirty);
    }

    /// <summary>A delete that leaves "Missing" lines asks for confirm: true (decision D712 of the MCP1 design). A
    /// preview tells it with needs_confirmation, and its warnings tell why, as the preview of the mission studio does
    /// (section 4.0.1 of the M6b design). The call with confirm: true applies.</summary>
    [Fact]
    public void ADeleteThatLeavesMissingLinesNeedsAConfirmation()
    {
        var (m, host) = Open();
        using var _ = m;

        var asked = Error(host, "mission_edit", Edit(RemoveEnd));
        var preview = Ok(host, "mission_edit", $$"""{ "commands": [ {{RemoveEnd}} ], "preview": true }""");
        Assert.True(m.Has("ol_end"));
        var confirmed = Ok(host, "mission_edit", $$"""{ "commands": [ {{RemoveEnd}} ], "confirm": true }""");

        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), (asked.Code, asked.Detail));
        Assert.StartsWith("After this edit, 3 lines name an item that is gone.", asked.Message);
        Assert.EndsWith("call the tool again with confirm: true.", asked.Message);
        Assert.Empty(host.Refusals);
        Assert.Equal(("previewed", true), ((string?)preview["status"], (bool?)preview["needs_confirmation"]));
        Assert.Contains(preview["warnings"]!.AsArray(), w =>
            ((string?)w!["message"])!.StartsWith("After this edit, 3 lines name an item that is gone.", StringComparison.Ordinal));
        Assert.Equal("applied", (string?)confirmed["status"]);
        Assert.Equal(3, confirmed["missing"]!.AsArray().Count);
        Assert.False(m.Has("ol_end"));
    }

    /// <summary>The inverse of an edit reverts it after a later edit of another trigger (section 4.8 of the M6b
    /// design). An inverse whose section a later edit changed gives stale_revision with SectionChanged, and nothing
    /// changes.</summary>
    [Fact]
    public void TheInverseRevertsAnEditAfterLaterEdits()
    {
        var (m, host) = Open();
        using var _ = m;
        var start = $$"""{ "kind": "set_trigger_start", "trigger": "trg:{{S}}#ol_end", "starts": true }""";

        var first = Ok(host, "mission_edit", Edit(Wait9));
        Ok(host, "mission_edit", Edit(start));
        var back = Ok(host, "mission_edit", Edit(first["inverse"]!.ToJsonString()));

        Assert.Equal("applied", (string?)back["status"]);
        Assert.Contains("Cnd_Timer = 3", m.Lines("ol_wave"));
        Assert.Contains("InitState = ACTIVE", m.Lines("ol_end"));

        var again = Ok(host, "mission_edit", Edit(Wait9));
        Ok(host, "mission_edit", Edit(Wait9.Replace("\"value\": 9", "\"value\": 7")));
        var before = m.State();
        var stale = Error(host, "mission_edit", Edit(again["inverse"]!.ToJsonString()));
        var batch = Call(host, "edit_batch", $$"""
            { "steps": [ { "tool": "mission_edit", "arguments": { "commands": [ {{again["inverse"]!.ToJsonString()}} ] } } ] }
            """);

        Assert.Equal(("stale_revision", MissionErrors.SectionChanged), (stale.Code, stale.Detail));
        Assert.Equal(("step_failed", MissionErrors.SectionChanged, "stale_revision"),
            ((string?)batch.Content["code"], (string?)batch.Content["detail"], (string?)batch.Content["step_code"]));
        Assert.Equal(before, m.State());
        Assert.Contains("Cnd_Timer = 7", m.Lines("ol_wave"));
    }

    /// <summary>A call that points the npc_ship_file line of the script at the ship build file of another script cannot
    /// change the old ship build file after that, because no script names it (final finding of MCP1-3). The inverse
    /// that the server gives for a call that points the line away reverts the call through mission_edit.</summary>
    [Fact]
    public void TheInverseRevertsACallThatPointsTheShipBuildFileLineAway()
    {
        var (m, host) = Open(setup: ws =>
        {
            ws.WriteBase(@"DATA\MISSIONS\OL_OTHER\ol_other.ini", "[Mission]\nnpc_ship_file = missions\\ol_other\\npcships.ini\n");
            ws.WriteBase(@"DATA\MISSIONS\OL_OTHER\npcships.ini", "[NPCShipArch]\nnickname = ol_other_build\n");
        });
        using var _ = m;
        var ships = Ships.Replace(@"\", @"\\");
        const string otherShips = @"DATA\\MISSIONS\\OL_OTHER\\npcships.ini";
        var otherBuild = $$"""
            { "kind": "add_section", "script": "msn:{{otherShips}}", "type": "NPCShipArch",
              "lines": [ { "key": "nickname", "texts": [ "ol_other_build_2" ] } ] }
            """;
        var away = $$"""
            { "kind": "set_item_key", "item": "sec:{{S}}#Mission", "key": "npc_ship_file",
              "lines": [ { "key": "npc_ship_file", "texts": [ "missions\\ol_other\\npcships.ini" ] } ] }
            """;
        string Old(string type) => $$"""
            { "kind": "add_section", "script": "msn:{{ships}}", "type": "{{type}}",
              "lines": [ { "key": "nickname", "texts": [ "ol_old" ] } ] }
            """;
        var before = m.State();

        foreach (var type in new[] { "NPCShipArch", "Room" })
        {
            var refused = Call(host, "mission_edit", Edit(otherBuild, away, Old(type)));
            Assert.Equal(("refused", MissionErrors.WorldFile, "commands[2]"), ((string?)refused.Content["code"],
                (string?)refused.Content["detail"], (string?)refused.Content["field"]));
        }
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);

        var applied = Ok(host, "mission_edit", Edit(otherBuild, Old("NPCShipArch"), away));
        Assert.Equal([@"npc_ship_file = missions\ol_other\npcships.ini"], m.Lines(Item("Mission", null)));
        Ok(host, "mission_edit", Edit(applied["inverse"]!.ToJsonString()));
        Assert.Equal([@"npc_ship_file = missions\ol_test\npcships.ini"], m.Lines(Item("Mission", null)));
        Assert.Equal(["[NPCShipArch]", "nickname = ol_other_build"], m.Session.OpenDocument(@"DATA\MISSIONS\OL_OTHER\npcships.ini")!
            .Working.SelectMany(s => s.Select(Line).Prepend($"[{s.Name}]")));
        Assert.False(m.Has(Item("NPCShipArch", "ol_old", Ships)));
    }

    /// <summary>The guards of the revision and of the script versions refuse an edit after a change. The error of a
    /// script version names the argument expect_versions.</summary>
    [Fact]
    public void TheRevisionAndTheVersionGuardsRefuse()
    {
        var (m, host) = Open();
        using var _ = m;
        Ok(host, "mission_edit", Edit(Wait9));

        var stale = Error(host, "mission_edit", $$"""{ "commands": [ {{AddAgentTrigger}} ], "if_revision": "0" }""");
        var version = Call(host, "mission_edit", $$"""{ "commands": [ {{AddAgentTrigger}} ], "expect_versions": { "{{S}}": 1 } }""");

        Assert.Equal(("stale_revision", EditSession.RevisionChanged), (stale.Code, stale.Detail));
        Assert.Equal(("stale_revision", MissionErrors.ScriptChanged, "expect_versions"),
            ((string?)version.Content["code"], (string?)version.Content["detail"], (string?)version.Content["field"]));
        Assert.False(m.Has("ol_agent"));
        Assert.Equal(1, m.Session.Revision);
    }

    /// <summary>An unknown argument, an unknown field of the options and an unknown kind give invalid_argument. The error
    /// names the argument, the field of the options or the index of the command.</summary>
    [Fact]
    public void AnUnknownArgumentFieldOrKindGivesInvalidArgument()
    {
        var (m, host) = Open();
        using var _ = m;

        var argument = Call(host, "mission_edit", $$"""{ "commands": [ {{Wait9}} ], "batch": {} }""");
        var field = Call(host, "mission_edit", $$"""{ "commands": [ {{Wait9}} ], "options": { "reject_new_error": true } }""");
        var kind = Call(host, "mission_edit", Edit(Wait9, """{ "kind": "launch_rocket" }"""));
        var nickname = Call(host, "mission_edit", Edit($$"""{ "kind": "remove_trigger", "trigger": "trg:{{S}}#ol_end", "nickame": "x" }"""));

        Assert.Equal(("invalid_argument", ToolInput.BadArgument, "batch"), Of(argument));
        Assert.Equal(("invalid_argument", MissionErrors.UnknownField, "options.reject_new_error"), Of(field));
        Assert.Equal(("invalid_argument", MissionErrors.UnknownCommand, "commands[1]"), Of(kind));
        Assert.Equal(1, (int?)kind.Content["index"]);
        Assert.Equal(("invalid_argument", MissionErrors.UnknownField, "commands[0]"), Of(nickname));
        Assert.Equal(0, m.Session.Revision);

        static (string?, string?, string?) Of(ToolResult result) =>
            ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]);
    }

    /// <summary>The limits of section 8.4 of the MCP1 design: more commands than the limit give limit. A command group
    /// counts as one command of the limit, so the section limit and the budget of the mission commands bound it. A result with an inverse above the size limit gives
    /// limit before the commit. Nothing changes.</summary>
    [Fact]
    public void TheCommandTheSectionAndTheResultLimitsRefuse()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var group = $$"""
            { "kind": "command_group", "commands": [ {{string.Join(", ", Enumerable.Range(1, 4).Select(i =>
                $$"""{ "kind": "add_trigger", "script": "msn:{{S}}", "nickname": "ol_new_{{i}}", "condition": { "key": "Cnd_True" } }"""))}} ] }
            """;

        var commands = Call(host, "mission_edit", Edit(Enumerable.Repeat(Wait9, 101).ToArray()));
        host.TestLimits = new AgentLimits { BatchSteps = 1, EditSections = 3 };
        var sections = Call(host, "mission_edit", Edit(group));

        Assert.Equal(("limit", MissionEditTools.MissionCommands, "commands"),
            ((string?)commands.Content["code"], (string?)commands.Content["detail"], (string?)commands.Content["field"]));
        Assert.Equal("One call of mission_edit can have at most 100 commands. Split the edit.", (string?)commands.Content["message"]);
        Assert.Equal(("limit", "EditSections"), ((string?)sections.Content["code"], (string?)sections.Content["detail"]));
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);
    }

    /// <summary>
    /// One call runs at most 1000 mission commands (user decision of 2026-11-01). The budget counts each command in a
    /// command group at each level, and each command of each mission step of edit_batch. A call above it gives limit
    /// with the field of the commands, and nothing changes. The inverses that a call gives must fit the budget too.
    /// </summary>
    [Fact]
    public void OneCallRunsAtMostTheBudgetOfMissionCommands()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        static string Group(IEnumerable<string> commands) =>
            $$"""{ "kind": "command_group", "commands": [ {{string.Join(", ", commands)}} ] }""";
        static (string?, string?, string?) Of(ToolResult result) =>
            ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]);

        // The outer group, 10 groups in it and 99 commands in each: 1001 commands.
        var nested = Call(host, "mission_edit", Edit(Group(Enumerable.Range(0, 10).Select(_ => Group(Enumerable.Repeat(Wait9, 99))))));
        Assert.Equal(("limit", MissionEditTools.MissionBudget, "commands"), Of(nested));
        Assert.Equal("One call can run at most 1000 mission commands. The count includes each command in a command_group " +
                     "at each level. It also includes each command of each mission step of edit_batch. The call changed " +
                     "nothing. Split the edit into more calls.", (string?)nested.Content["message"]);
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);

        // 100 mission steps of 100 commands: the step that goes above the budget names its commands.
        var step = $$"""{ "tool": "mission_edit", "arguments": {{Edit(Enumerable.Repeat(Wait9, 100).ToArray())}} }""";
        var steps = Call(host, "edit_batch", $$"""{ "steps": [ {{string.Join(", ", Enumerable.Repeat(step, 100))}} ] }""");
        Assert.Equal(("limit", MissionEditTools.MissionBudget, "steps[10].arguments.commands"), Of(steps));
        Assert.Equal(10, (int?)steps.Content["index"]);
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);

        // One command whose inverse removes 1001 new lines and gives back the old line: 1003 commands.
        var labels = string.Join(", ", Enumerable.Range(0, 1001).Select(i => $$"""{ "key": "label", "texts": [ "ol_label_{{i}}" ] }"""));
        var setLabels = Edit($$"""{ "kind": "set_item_key", "item": "sec:{{S}}#MsnShip/ol_ship_1", "key": "label", "lines": [ {{labels}} ] }""");
        foreach (var arguments in new[] { setLabels, setLabels.Insert(1, "\"preview\": true, ") })
        {
            var inverse = Call(host, "mission_edit", arguments);
            Assert.Equal(("limit", MissionEditTools.InverseBudget, "commands"), Of(inverse));
            Assert.StartsWith("The inverse of the call has more than 1000 mission commands", (string?)inverse.Content["message"]);
        }
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);

        // A call in the budget runs: the outer group, 10 groups and 98 commands in each are 991 commands.
        Ok(host, "mission_edit", Edit(Group(Enumerable.Range(0, 10).Select(_ => Group(Enumerable.Repeat(Wait9, 98))))));
        Assert.Equal("Cnd_Timer = 9", m.Lines("ol_wave")[2]);
    }

    /// <summary>
    /// The inverse budget counts each command of the inverse that the call gives, also the command group that joins the
    /// inverses of two requests. mission_add_item adds a ship with one request and its labels in the texts form with a
    /// second request. When the two inverses have 1000 commands, the inverse of the call has 1001. So the call gives
    /// limit and changes nothing. One label less gives an inverse of exactly 1000 commands, and the call applies.
    /// </summary>
    [Fact]
    public void TheInverseBudgetCountsTheGroupThatJoinsTheInversesOfAStep()
    {
        var (m, host) = OpenItems();
        using var _ = m;
        host.TestLimits = new AgentLimits { ResultKilobytes = 4096 };
        var before = m.State();
        static string Ship(int labels, bool preview = false) => AddItem("ship", "ol_new", $$"""
            { "npc": "ol_npc_1", "label": [ {{string.Join(", ", Enumerable.Range(0, labels)
                .Select(i => $$"""{ "texts": [ "ol_label_{{i}}" ] }"""))}} ] }
            """).Insert(1, preview ? "\"preview\": true, " : "");
        static int CountOf(JsonNode command) => 1 + (command["commands"]?.AsArray().Sum(c => CountOf(c!)) ?? 0);

        // The inverse of the call is the group of the two inverses. Each label adds one command to it.
        var two = Ok(host, "mission_add_item", Ship(2, preview: true))["inverse"]!;
        var three = Ok(host, "mission_add_item", Ship(3, preview: true))["inverse"]!;
        Assert.Equal(("command_group", 2), ((string?)two["kind"], two["commands"]!.AsArray().Count));
        Assert.Equal(1, CountOf(three) - CountOf(two));
        var largest = 2 + AgentLimits.MissionCommands - CountOf(two);

        foreach (var preview in new[] { false, true })
        {
            var above = Call(host, "mission_add_item", Ship(largest + 1, preview));
            Assert.Equal(("limit", MissionEditTools.InverseBudget),
                ((string?)above.Content["code"], (string?)above.Content["detail"]));
        }
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);
        var applied = Ok(host, "mission_add_item", Ship(largest));
        Assert.Equal(AgentLimits.MissionCommands, CountOf(applied["inverse"]!));
        Assert.Equal($"label = ol_label_{largest - 1}", m.Lines(Item("MsnShip", "ol_new"))[^1]);
        var inverse = applied["inverse"]!.ToJsonString();
        Ok(host, "mission_edit", $$"""{ "commands": [ {{inverse}} ], "confirm": true }""");
        Assert.Equal(before, m.State());
    }

    /// <summary>A rename without change_references changes each line that names the item, because the default is true.
    /// The input schema and the help give that default. False keeps the lines, so they name the old nickname and need
    /// confirm: true.</summary>
    [Fact]
    public void ARenameChangesTheLinesThatNameTheItemByDefault()
    {
        var (m, host) = Open();
        using var _ = m;

        Ok(host, "mission_edit", Edit($$"""{ "kind": "rename_trigger", "trigger": "trg:{{S}}#ol_end", "new_name": "ol_finish" }"""));
        Ok(host, "mission_edit", Edit($$"""{ "kind": "rename_item", "item": "sec:{{S}}#MsnShip/ol_ship_1", "new_name": "ol_lead" }"""));
        var kept = Edit($$"""{ "kind": "rename_trigger", "trigger": "trg:{{S}}#ol_wave", "new_name": "ol_attack", "change_references": false }""");
        var asked = Error(host, "mission_edit", kept);
        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), (asked.Code, asked.Detail));
        Ok(host, "mission_edit", kept.Insert(1, "\"confirm\": true, "));

        Assert.Equal(["Act_ActTrig = ol_wave", "Act_ActTrig = ol_finish"], m.Lines("ol_start")[3..]);
        Assert.Equal("Act_SpawnShip = ol_lead", m.Lines("ol_attack")[3]);
        var definitions = AgentTools.Find("mission_edit")!.InputSchema["$defs"]!;
        foreach (var (kind, byDefault) in new[]
                 {
                     ("rename_trigger", new RenameTrigger(T("ol_x"), "ol_y").ChangeReferences),
                     ("rename_item", new RenameItem(Item("MsnShip", "ol_x"), "ol_y").ChangeReferences),
                 })
        {
            var field = definitions[kind]!["properties"]!["change_references"]!;
            Assert.True(byDefault);
            Assert.Equal(byDefault, (bool?)field["default"]);
            Assert.Contains("This is the default.", (string?)field["description"]);
        }
    }

    /// <summary>The size check of a result counts the inverse, so an edit whose inverse is too large changes
    /// nothing.</summary>
    [Fact]
    public void TheSizeLimitOfAResultCountsTheInverse()
    {
        var text = Text + "\n\n[Trigger]\nnickname = ol_long\nCnd_True = no_params\n" +
                   string.Concat(Enumerable.Repeat("Act_LockDock = Player, ol_station, lock\n", 60));
        var (m, host) = Open(text);
        using var _ = m;
        var before = m.State();
        var remove = Edit($$"""{ "kind": "remove_trigger", "trigger": "trg:{{S}}#ol_long" }""");

        host.TestLimits = new AgentLimits { ResultKilobytes = 2 };
        var limited = Call(host, "mission_edit", remove);
        Assert.Equal(before, m.State());
        host.TestLimits = new AgentLimits();
        var applied = Ok(host, "mission_edit", remove);

        Assert.Equal(("limit", ToolResult.ResultSize), ((string?)limited.Content["code"], (string?)limited.Content["detail"]));
        // One command can have a large inverse, so the text names the way around the limit (final finding 11 of MCP1-3).
        Assert.EndsWith("So to delete a large script or trigger, first remove its sections or lines in smaller calls. Or ask " +
                        "the user to raise the size of a result in Agent settings.", (string?)limited.Content["message"]);
        var withoutInverse = (JsonObject)applied.DeepClone();
        withoutInverse.Remove("inverse");
        Assert.InRange(withoutInverse.ToJsonString().Length, 0, 2048);
        Assert.InRange(applied.ToJsonString().Length, 2049, int.MaxValue);
        Assert.False(m.Has("ol_long"));
    }

    /// <summary>The write scope of the mission commands (section 8.2 of the MCP1 design, contract addition 6). A world
    /// file of the missions and a whole file of the active mod that replaces a lower file refuse. A new script outside
    /// the folder of the missions gives BadPath, and a path in use gives name_taken. A script of the base game cannot
    /// go, so nothing outside the active mod changes.</summary>
    [Fact]
    public void TheWriteScopeOfTheMissionCommands()
    {
        const string pilots = @"DATA\MISSIONS\pilots_population.ini";
        var (m, host) = Open(setup: ws => ws.WriteBase(pilots, "[Pilot]\nnickname = ol_pilot\n"));
        using var _ = m;
        var baseText = File.ReadAllText(Path.Combine(m.Ws.Base, Script));

        var world = Error(host, "mission_edit",
            Edit("""{ "kind": "remove_section", "section": "sec:DATA\\MISSIONS\\pilots_population.ini#Pilot/ol_pilot" }"""));
        var outside = Error(host, "mission_edit", Edit("""{ "kind": "create_script", "path": "DATA\\SHIPS\\ol_new.ini" }"""));
        var taken = Error(host, "mission_edit", Edit($$"""{ "kind": "create_script", "path": "{{S}}" }"""));
        var baseGame = Error(host, "mission_edit", $$"""{ "commands": [ { "kind": "delete_script", "script": "msn:{{S}}" } ], "confirm": true }""");

        Assert.Equal(("refused", MissionErrors.WorldFile), (world.Code, world.Detail));
        Assert.Equal(("invalid_argument", MissionErrors.BadPath), (outside.Code, outside.Detail));
        Assert.Equal("commands[0].path",
            (string?)Call(host, "mission_edit", Edit("""{ "kind": "create_script", "path": "DATA\\SHIPS\\ol_new.ini" }""")).Content["field"]);
        // A bad ship build file names its own field and tells the install path form (final finding 17 of MCP1-3).
        var ships = Call(host, "mission_edit", Edit("""
            { "kind": "create_script", "path": "DATA\\MISSIONS\\OL_NEW\\ol_new.ini", "ship_build_file": "missions\\ol_new\\npcships.ini" }
            """)).Content;
        Assert.Equal(("invalid_argument", MissionErrors.BadPath, "commands[0].ship_build_file"),
            ((string?)ships["code"], (string?)ships["detail"], (string?)ships["field"]));
        Assert.StartsWith(@"The editor cannot make a ship build file at missions\ol_new\npcships.ini.", (string?)ships["message"]);
        Assert.Equal(("name_taken", MissionErrors.PathInUse), (taken.Code, taken.Detail));
        Assert.Equal(("refused", MissionErrors.NotWholeFile), (baseGame.Code, baseGame.Detail));
        Assert.Equal(baseText, File.ReadAllText(Path.Combine(m.Ws.Base, Script)));
        Assert.False(m.Session.CanUndo);

        var (whole, wholeHost) = Open(modFiles: new() { [@"files\" + Script] = Text });
        using var __ = whole;
        var lower = Error(wholeHost, "mission_edit", $$"""{ "commands": [ { "kind": "delete_script", "script": "msn:{{S}}" } ], "confirm": true }""");
        Assert.Equal(("refused", MissionErrors.ReplacesLowerFile), (lower.Code, lower.Detail));
    }

    /// <summary>mission_edit is a step of edit_batch (section 7.6 of the MCP1 design). The strict check runs once at
    /// the commit, so step 1 can start a trigger that step 2 adds. Each mission step gives its own inverse, and the batch
    /// gives the versions of the scripts. A step that needs a confirmation fails without confirm: true.</summary>
    [Fact]
    public void AMissionEditIsAStepOfABatch()
    {
        var (m, host) = Open();
        using var _ = m;
        var startLater =
            $$"""{ "kind": "add_action", "trigger": "trg:{{S}}#ol_wave", "line": { "key": "Act_ActTrig", "values": { "trigger": "ol_later" } } }""";
        var addLater =
            $$"""{ "kind": "add_trigger", "script": "msn:{{S}}", "nickname": "ol_later", "condition": { "key": "Cnd_Timer", "values": { "seconds": 1 } } }""";

        var unconfirmed = Call(host, "edit_batch", $$"""
            { "steps": [ { "tool": "mission_edit", "arguments": { "commands": [ {{RemoveEnd}} ] } } ] }
            """);
        var result = Ok(host, "edit_batch", $$"""
            { "label": "Add a later step", "steps": [
              { "tool": "mission_edit", "arguments": { "commands": [ {{startLater}} ] } },
              { "tool": "mission_edit", "arguments": { "commands": [ {{addLater}} ] } } ] }
            """);

        Assert.Equal(("step_failed", EditTools.ConfirmChanges), ((string?)unconfirmed.Content["code"], (string?)unconfirmed.Content["detail"]));
        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal((EditOrigin.Agent, "Agent: Add a later step"), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.Null(result["inverse"]);
        Assert.Equal(2, (long?)result["versions"]![Script]);
        var steps = result["steps"]!.AsArray();
        Assert.All(steps, s => Assert.NotNull(s!["inverse"]));
        Assert.All(steps, s => Assert.Equal(2, (long?)s!["versions"]![Script]));
        Assert.Contains("Act_ActTrig = ol_later", m.Lines("ol_wave"));
        Assert.True(m.Has("ol_later"));

        m.Session.Undo();
        Assert.False(m.Has("ol_later"));
        Assert.DoesNotContain("Act_ActTrig = ol_later", m.Lines("ol_wave"));

        // Each mission step keeps its own inverse and row moves: the same requests in their own calls give the same.
        // So an agent that reverts one step does not revert the steps before it.
        var later = Ok(host, "mission_edit", $$"""{ "commands": [ {{addLater}} ] }""");
        var start = Ok(host, "mission_edit", $$"""{ "commands": [ {{startLater}} ] }""");
        Assert.True(JsonNode.DeepEquals(start["inverse"], steps[0]!["inverse"]), steps[0]!["inverse"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(later["inverse"], steps[1]!["inverse"]), steps[1]!["inverse"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(start["row_moves"], steps[0]!["row_moves"]));
        Assert.True(JsonNode.DeepEquals(later["row_moves"], steps[1]!["row_moves"]));
    }

    /// <summary>A batch whose mission steps cancel each other gives no_change and no history entry. No step gives an
    /// inverse or row moves, because nothing changed.</summary>
    [Fact]
    public void ABatchOfMissionStepsWithNoNetChangeGivesNoInverse()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var same = $$"""{ "kind": "set_condition_value", "trigger": "trg:{{S}}#ol_wave", "name": "seconds", "value": 3 }""";

        var result = Ok(host, "edit_batch", $$"""
            { "steps": [
              { "tool": "mission_edit", "arguments": { "commands": [ {{Wait9}} ] } },
              { "tool": "mission_edit", "arguments": { "commands": [ {{same}} ] } } ] }
            """);

        Assert.Equal("no_change", (string?)result["status"]);
        var steps = result["steps"]!.AsArray();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal("no_change", (string?)s!["status"]));
        Assert.All(steps, s => Assert.Null(s!["inverse"]));
        Assert.All(steps, s => Assert.Null(s!["row_moves"]));
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>Pause, a session that another editor has open and a tool group that the user hides refuse the tool
    /// (decisions D725 and D734 of the MCP1 design).</summary>
    [Fact]
    public void PauseAReadOnlySessionAndAHiddenGroupRefuse()
    {
        var (m, host) = Open();
        using var _ = m;
        var second = new TestAgentHost(m.Ws.SecondSession("act"));

        host.TestPaused = true;
        var paused = Error(host, "mission_edit", Edit(Wait9));
        host.TestPaused = false;
        host.Hidden.Add(ToolGroup.Missions);
        var hidden = Error(host, "mission_edit", Edit(Wait9));
        var readOnly = Error(second, "mission_edit", Edit(Wait9));

        Assert.Equal(("paused", "Paused"), (paused.Code, paused.Detail));
        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Equal(("read_only", MissionErrors.NotEditable), (readOnly.Code, readOnly.Detail));
        Assert.Equal(0, m.Session.Revision);
    }

    /// <summary>The host without a window never edits a mod in the OpenLancer repo (section 13.2, item 7 of the MCP1
    /// design).</summary>
    [Fact]
    public void TheHostWithoutAWindowGivesReadOnlyForARepoMod()
    {
        using var m = Create();
        m.Session.Dispose();
        File.WriteAllText(Path.Combine(m.Ws.Root, RepoCheck.MarkerFile), "");

        using var host = HeadlessHost.Open(m.Ws.ProfilePath, "act", new HeadlessOptions());
        var error = Error(host, "mission_edit", Edit(Wait9));

        Assert.Equal(("read_only", "NotEditable"), (error.Code, error.Detail));
        Assert.Contains("part of the OpenLancer repo", error.Message);
    }

    /// <summary>The host without a window writes a mission edit to the recovery file 2 seconds after the edit (section
    /// 13.2, item 6 of the MCP1 design). A new session finds the edit.</summary>
    [Fact]
    public void AMissionEditOfTheHostWithoutAWindowGoesIntoTheRecoveryFile()
    {
        using var m = Create();
        m.Session.Dispose();
        var clock = new FakeClock();
        var host = HeadlessHost.Open(m.Ws.ProfilePath, "act", new HeadlessOptions { Clock = clock });

        Ok(host, "mission_edit", Edit(Wait9));
        clock.Advance(TimeSpan.FromSeconds(2));
        host.Idle();
        var written = File.Exists(RecoveryFile.PathOf(m.Ws.ModFolder("act")));
        host.Dispose();
        var session = m.Ws.Session("act");
        session.RestoreRecovery();

        Assert.True(written);
        var document = session.OpenDocument(Script)!;
        Assert.Contains(document.Working.SelectMany(s => s), e => e.Name == "Cnd_Timer" && MissionValues.TextsOf(e).SequenceEqual(["9"]));
    }

    /// <summary>A trigger rename gives each new problem once, with the rule of the mission checks. The commit check of
    /// the mission editor gives the problems of mission scripts. A rename without the change of the references leaves
    /// "Missing" lines, so it asks for confirm: true.</summary>
    [Fact]
    public void ATriggerRenameGivesEachNewProblemOnce()
    {
        var (m, host) = Open();
        using var _ = m;
        var rename = $$"""{ "kind": "rename_trigger", "trigger": "trg:{{S}}#ol_wave", "new_name": "ol_wave_2", "change_references": false }""";

        var asked = Error(host, "mission_edit", Edit(rename));
        var result = Ok(host, "mission_edit", $$"""{ "commands": [ {{rename}} ], "confirm": true }""");

        Assert.Equal("needs_confirmation", asked.Code);
        Assert.Equal([$"trg:{Script}#ol_wave"], result["renames"]!.AsArray().Select(r => (string?)r!["from"]));
        var problems = result["new_problems"]!.AsArray().Select(p => p!.ToJsonString()).ToList();
        Assert.NotEmpty(problems);
        Assert.Equal(problems.Distinct().Count(), problems.Count);
        Assert.All(result["new_problems"]!.AsArray(), p => Assert.Matches("^[A-Z][0-9]{2}$", (string?)p!["rule"]));
        Assert.Contains(result["new_problems"]!.AsArray(), p => (string?)p!["rule"] == "M05" &&
                                                                 (string?)p["ref"] == $"line:{Script}#Trigger/ol_start/3");
    }

    /// <summary>The audit log keeps the arguments of a large mission edit, and its replay on a fresh copy gives the
    /// same mission files (decision D728 and section 13.3, item 6 of the MCP1 design). The arguments of 100 commands are
    /// larger than 16 KB.</summary>
    [Fact]
    public async Task TheAuditLogKeepsALargeMissionEditAndItsReplayGivesTheSameFiles()
    {
        var token = TestContext.Current.CancellationToken;
        var folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "agent.jsonl");
        var adds = Enumerable.Range(1, 100).Select(i => AddAgentTrigger.Replace("ol_agent", $"ol_agent_{i}")).ToArray();
        var arguments = JsonNode.Parse(Edit(adds))!.AsObject();
        try
        {
            string expected;
            var (m, host) = Open();
            using (m)
            {
                using (var log = new AuditLog(file))
                using (var queue = new ToolQueue(host, new FakeClock(), log))
                {
                    var call = queue.CallAsync("mission_edit", arguments, null, token);
                    queue.RunNext();
                    var result = await call.WaitAsync(TimeSpan.FromSeconds(10), token);
                    Assert.Equal("applied", (string?)result.Content["status"]);
                }
                expected = m.State();
            }
            List<AuditEntry> entries;
            using (var reader = new StreamReader(file))
                entries = AuditLog.Read(reader).ToList();
            var (copy, copyHost) = Open();
            using (copy)
            {
                using (var reader = new StreamReader(file))
                    Assert.Null(AgentReplay.Run(copyHost, reader, new StringWriter()));
                Assert.True(copy.Has("ol_agent_100"));
                Assert.Equal(expected, copy.State());
            }

            var entry = Assert.Single(entries);
            Assert.False(entry.ArgumentsOmitted);
            Assert.Equal(arguments.ToJsonString(), entry.Arguments!.ToJsonString());
            Assert.InRange(Encoding.UTF8.GetByteCount(arguments.ToJsonString()), 16 * 1024 + 1, int.MaxValue);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    /// <summary>The input schema of mission_edit has the fields of a mission batch and the definitions of
    /// <c>MissionJson.Schema()</c> at its root (section 13.1, item 1). Each <c>$ref</c> resolves, the schema has no
    /// problem of the subset, and the arguments of a sample call fit it.</summary>
    [Fact]
    public void TheInputSchemaHasTheDefinitionsOfTheBatchAtItsRoot()
    {
        var input = AgentTools.Find("mission_edit")!.InputSchema;
        var sample = JsonNode.Parse($$"""{ "label": "Wait", "commands": [ {{Wait9}}, {{AddAgentTrigger}} ], "preview": true }""");

        Assert.Empty(SchemaCheck.SchemaProblems(input));
        Assert.Equal(MissionJson.Schema()["$defs"]!.AsObject().Select(d => d.Key), input["$defs"]!.AsObject().Select(d => d.Key));
        Assert.Equal(["commands"], input["required"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(
            ["label", "commands", "options", "expect_versions", "preview", "if_revision", "with_patch", "confirm"],
            input["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal("#/$defs/command", (string?)input["properties"]!["commands"]!["items"]!["$ref"]);
        Assert.True((bool)input["properties"]!["options"]!["properties"]!["reject_new_errors"]!["default"]!);
        Assert.Empty(SchemaCheck.Problems(input, sample));
        Assert.NotEmpty(SchemaCheck.Problems(input, JsonNode.Parse("""{ "commands": [ { "kind": "launch_rocket" } ] }""")));
    }
}
