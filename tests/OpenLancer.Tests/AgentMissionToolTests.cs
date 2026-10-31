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
        Assert.Equal(("new_errors", "NewErrors"), (warnings.Code, warnings.Detail));
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
    /// counts as one command, so the section limit bounds it. A result with an inverse above the size limit gives
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
