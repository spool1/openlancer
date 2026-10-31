using System;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Missions;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The generic edit tools on mission items (section 7.2 and the first item of section 7.4 of the MCP1 design, step 5
/// of MCP1-3): <c>set_fields</c>, <c>set_text</c>, <c>rename_item</c>, <c>delete_items</c> and
/// <c>duplicate_items</c>. Each call runs the commands of the mission studio in one request of the shared batch. So it
/// is one Undo step with the edit origin Agent, it runs in strict mode, and its result gives the versions and the
/// inverse. The other edit tools refuse a mission item.
/// </summary>
public sealed partial class AgentMissionToolTests
{
    private static readonly string Ship1 = $"sec:{S}#MsnShip/ol_ship_1";

    /// <summary>The code, the detail and the field of a tool error.</summary>
    private static (string? Code, string? Detail, string? Field) Refusal(TestAgentHost host, string tool, string arguments)
    {
        var result = Call(host, tool, arguments);
        Assert.True(result.IsError, result.Text);
        return ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]);
    }

    /// <summary>The call is one entry of the agent with its label and gives the new version of the script and an
    /// inverse. One undo puts back the state before the call.</summary>
    private static void AssertOneMissionEntry(MissionEdits m, JsonObject result, string label, string before)
    {
        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal((EditOrigin.Agent, "Agent: " + label), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.Equal(2, (long?)result["versions"]![Script]);
        Assert.NotNull(result["inverse"]);
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>set_fields sets the keys of a mission item with the field forms of mission_add_item, in one entry.
    /// null removes a key. The inverse puts back the keys after a later edit of another item.</summary>
    [Fact]
    public void SetFieldsSetsTheKeysOfAMissionItem()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var shipBefore = m.Lines(Item("MsnShip", "ol_ship_1"));
        var arguments = $$"""
            { "ref": "{{Ship1}}", "fields": { "label": ["ol_wing", "ol_lead"], "position": null, "jumper": true } }
            """;

        var result = Ok(host, "set_fields", arguments);

        Assert.Equal([$"sec:{Script}#MsnShip/ol_ship_1"], Texts(result["changes"]));
        Assert.Equal(["nickname = ol_ship_1", "system = ol_system", "npc = ol_npc_1", "label = ol_wing", "label = ol_lead",
            "jumper = true"], m.Lines(Item("MsnShip", "ol_ship_1")));
        Assert.Equal([("set_fields", "Agent: Set 3 fields of ol_ship_1")], host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));
        AssertOneMissionEntry(m, result, "Set 3 fields of ol_ship_1", before);

        var one = Ok(host, "set_fields", $$"""{ "ref": "{{Ship1}}", "fields": { "system": "ol_system" } }""");
        Assert.Equal("no_change", (string?)one["status"]);
        var again = Ok(host, "set_fields", arguments);
        Ok(host, "set_fields", $$"""{ "ref": "sec:{{S}}#MsnShip/ol_ship_2", "fields": { "jumper": true } }""");
        Ok(host, "mission_edit", Edit(again["inverse"]!.ToJsonString()));
        Assert.Equal(shipBefore, m.Lines(Item("MsnShip", "ol_ship_1")));
        Assert.Contains("jumper = true", m.Lines(Item("MsnShip", "ol_ship_2")));
    }

    /// <summary>set_fields also sets the keys of a ship build in the ship build file of the script, and of the
    /// <c>[Mission]</c> section.</summary>
    [Fact]
    public void SetFieldsSetsAShipBuildAndTheMissionSection()
    {
        var (m, host) = Open();
        using var _ = m;

        var build = Ok(host, "set_fields", $$"""{ "ref": "sec:{{B}}#NPCShipArch/ol_build_1", "fields": { "level": "d7" } }""");
        var header = Ok(host, "set_fields", $$"""{ "ref": "sec:{{S}}#Mission", "fields": { "reward": 500 } }""");

        Assert.Equal("Agent: Set level", (string?)build["undo_label"]);
        Assert.Equal([$"sec:{Ships}#NPCShipArch/ol_build_1"], Texts(build["changes"]));
        Assert.Contains("level = d7", m.Lines(Item("NPCShipArch", "ol_build_1", Ships)));
        Assert.NotNull(build["inverse"]);
        Assert.Equal("Agent: Set reward", (string?)header["undo_label"]);
        Assert.Equal(["npc_ship_file = missions\\ol_test\\npcships.ini", "reward = 500"], m.Lines(Item("Mission", null)));
        Assert.Equal(2, (long?)header["versions"]![Script]);
    }

    /// <summary>set_fields refuses a trigger, the key line and the other kinds of mission references. A key that the
    /// section does not have and a value without a form give their errors. Nothing changes.</summary>
    [Fact]
    public void SetFieldsGivesItsRefusals()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        string Call(string reference, string fields) => $$"""{ "ref": "{{reference}}", "fields": {{fields}} }""";

        (string, string?, string?)[] expected =
        [
            ("refused", EditTools.MissionItem, "ref"),
            ("refused", EditTools.MissionItem, "ref"),
            ("invalid_argument", EditTools.MissionItem, "ref"),
            ("invalid_argument", EditTools.MissionItem, "ref"),
            ("invalid_argument", EditTools.MissionItem, "ref"),
            ("refused", MissionErrors.KeyLine, "fields.nickname"),
            ("invalid_value", MissionErrors.NotAKey, "fields.no_such_key"),
            ("invalid_argument", ToolInput.BadArgument, "fields.label"),
            ("invalid_argument", ToolInput.BadArgument, "fields.GotoVec"),
            ("invalid_value", MissionErrors.ValueKind, "fields.position"),
        ];
        var got = new[]
        {
            Refusal(host, "set_fields", Call(Wave, """{ "Cnd_Timer": 4 }""")),
            Refusal(host, "set_fields", Call($"sec:{S}#Trigger/ol_wave", """{ "Cnd_Timer": 4 }""")),
            Refusal(host, "set_fields", Call(WaveLine(2), """{ "seconds": 4 }""")),
            Refusal(host, "set_fields", Call($"label:{S}#ol_wing", """{ "x": 1 }""")),
            Refusal(host, "set_fields", Call($"inst:{S}#ol_w1", """{ "x": 1 }""")),
            Refusal(host, "set_fields", Call(Ship1, """{ "nickname": "ol_other" }""")),
            Refusal(host, "set_fields", Call(Ship1, """{ "no_such_key": 1 }""")),
            Refusal(host, "set_fields", Call(Ship1, """{ "label": [ { "a": { "b": 1 } } ] }""")),
            Refusal(host, "set_fields", Call($"sec:{S}#ObjList/ol_orders", """{ "GotoVec": [1, 2] }""")),
            Refusal(host, "set_fields", Call(Ship1, """{ "position": "far" }""")),
        };

        Assert.Equal(expected, got);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>set_text on a key of a mission item adds the line with its text id in one entry, as the "Set" button
    /// and the text field of the page do. A second call changes the text of the line. A line: reference names the
    /// value of a line, and a key of a trigger names its first line. The text goes into the strings.json of the active
    /// mod.</summary>
    [Fact]
    public void SetTextSetsTheTextOfAKeyAndOfALine()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var npc = $"sec:{S}#NPC/ol_npc_1";
        string TextOf(string line) => m.Session.Text(new StringPlaceholder(StringKind.String, line[(line.IndexOf("$str:", StringComparison.Ordinal) + 5)..]))!;

        var added = Ok(host, "set_text", $$"""{ "ref": "{{npc}}", "field": "individual_name", "text": "Captain Vale" }""");

        Assert.Equal("Agent: Set the text of individual_name", (string?)added["undo_label"]);
        var line = Assert.Single(m.Lines(Item("NPC", "ol_npc_1")), l => l.StartsWith("individual_name", StringComparison.Ordinal));
        var row = Array.IndexOf(m.Lines(Item("NPC", "ol_npc_1")), line);
        Assert.Equal([$"sec:{Script}#NPC/ol_npc_1", $"line:{Script}#NPC/ol_npc_1/{row}"], Texts(added["changes"]));
        Assert.StartsWith("individual_name = $str:", line);
        Assert.Equal("Captain Vale", TextOf(line));
        AssertOneMissionEntry(m, added, "Set the text of individual_name", before);

        Ok(host, "set_text", $$"""{ "ref": "{{npc}}", "field": "individual_name", "text": "Captain Vale" }""");
        var changed = Ok(host, "set_text", $$"""{ "ref": "{{npc}}", "field": "individual_name", "text": "Captain Rook" }""");
        Assert.Equal(line, Assert.Single(m.Lines(Item("NPC", "ol_npc_1")), l => l.StartsWith("individual_name", StringComparison.Ordinal)));
        Assert.Equal("Captain Rook", TextOf(line));
        Assert.Equal(4, (long?)changed["versions"]![Script]);

        var state = Ok(host, "set_text", $$"""{ "ref": "line:{{S}}#Trigger/ol_end/3", "field": "text", "text": "The station is safe." }""");
        Assert.Equal([$"line:{Script}#Trigger/ol_end/3"], Texts(state["changes"]));
        Assert.Equal("The station is safe.", TextOf(m.Lines("ol_end")[3]));
        Ok(host, "set_text", $$"""{ "ref": "{{End}}", "field": "Act_ChangeState", "text": "Well done." }""");
        Assert.Equal("Well done.", TextOf(m.Lines("ol_end")[3]));
    }

    /// <summary>set_text refuses a key without a text id, a trigger without the line of the key, a value name that the
    /// line does not have and a label. Nothing changes.</summary>
    [Fact]
    public void SetTextGivesItsRefusals()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        Assert.Equal(("invalid_argument", MissionErrors.NotTextId, "field"),
            Refusal(host, "set_text", $$"""{ "ref": "{{Ship1}}", "field": "system", "text": "x" }"""));
        Assert.Equal(("invalid_argument", MissionErrors.NotTextId, "field"),
            Refusal(host, "set_text", $$"""{ "ref": "sec:{{S}}#NPC/ol_npc_1", "field": "name", "text": "x" }"""));
        Assert.Equal(("invalid_argument", MissionErrors.RowNotFound, "field"),
            Refusal(host, "set_text", $$"""{ "ref": "{{End}}", "field": "Act_SetTitle", "text": "x" }"""));
        Assert.Equal(("invalid_argument", MissionErrors.NotTextId, "field"),
            Refusal(host, "set_text", $$"""{ "ref": "{{End}}", "field": "Act_LockDock", "text": "x" }"""));
        Assert.Equal(("invalid_argument", MissionErrors.NoSuchValue, "field"),
            Refusal(host, "set_text", $$"""{ "ref": "{{WaveLine(2)}}", "field": "text", "text": "x" }"""));
        Assert.Equal(("invalid_argument", MissionErrors.NotTextId, "field"),
            Refusal(host, "set_text", $$"""{ "ref": "{{WaveLine(2)}}", "field": "seconds", "text": "x" }"""));
        Assert.Equal(("invalid_argument", EditTools.MissionItem, "ref"),
            Refusal(host, "set_text", $$"""{ "ref": "label:{{S}}#ol_wing", "field": "name", "text": "x" }"""));
        var npc = Call(host, "set_text", $$"""{ "ref": "sec:{{S}}#NPC/ol_npc_1", "field": "name", "text": "x" }""");
        var trigger = Call(host, "set_text", $$"""{ "ref": "{{End}}", "field": "Act_LockDock", "text": "x" }""");
        var ship = Call(host, "set_text", $$"""{ "ref": "{{Ship1}}", "field": "system", "text": "x" }""");
        Assert.EndsWith("Use one of these keys: individual_name.", (string?)npc.Content["message"]);
        Assert.EndsWith("Use one of these keys: Act_ChangeState.", (string?)trigger.Content["message"]);
        Assert.Contains("has no key that holds a text id", (string?)ship.Content["message"]);
        Assert.Equal(before, m.State());
    }

    /// <summary>rename_item renames a trigger and each line that names it, in one entry. The result gives the rename in
    /// the trg: form.</summary>
    [Fact]
    public void RenameItemRenamesATriggerAndEachLineThatNamesIt()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "rename_item", $$"""{ "ref": "{{End}}", "new_name": "ol_finish" }""");

        var rename = Assert.Single(result["renames"]!.AsArray())!;
        Assert.Equal(($"trg:{Script}#ol_end", $"trg:{Script}#ol_finish"), ((string?)rename["from"], (string?)rename["to"]));
        Assert.Equal(["Act_ActTrig = ol_wave", "Act_ActTrig = ol_finish"], m.Lines("ol_start")[3..]);
        Assert.Equal(["Act_DeactTrig = ol_finish", "Act_ActTrig = ol_finish"], m.Lines("ol_wave")[4..]);
        Assert.False(m.Has("ol_end"));
        AssertOneMissionEntry(m, result, "Rename ol_end to ol_finish", before);
    }

    /// <summary>rename_item renames a mission item and the lines that name it. A random encounter has at most 31
    /// characters, so a longer name gives invalid_value with NameInvalid (decision D683 of M6b). A name in use gives
    /// name_taken. A label and a prefab instance refuse.</summary>
    [Fact]
    public void RenameItemRenamesAMissionItemAndChecksTheName()
    {
        var (m, host) = OpenItems();
        using var _ = m;

        var ship = Ok(host, "rename_item", $$"""{ "ref": "{{Ship1}}", "new_name": "ol_lead" }""");
        Ok(host, "mission_add_item", AddItem("random_encounter", "ol_enc", """{ "encounter_type": "TL_Attack" }"""));
        var tooLong = Refusal(host, "rename_item", $$"""{ "ref": "sec:{{S}}#MsnRandEnc/ol_enc", "new_name": "{{new string('e', 32)}}" }""");
        var longest = Ok(host, "rename_item", $$"""{ "ref": "sec:{{S}}#MsnRandEnc/ol_enc", "new_name": "{{new string('e', 31)}}" }""");

        Assert.Equal("Agent: Rename ol_ship_1 to ol_lead", (string?)ship["undo_label"]);
        Assert.Contains("Act_SpawnShip = ol_lead", m.Lines("ol_wave"));
        Assert.Equal("Cnd_Destroyed = ol_lead, -1", m.Lines("ol_end")[1]);
        Assert.Equal(("invalid_value", MissionErrors.NameInvalid, "new_name"), tooLong);
        Assert.Equal("applied", (string?)longest["status"]);
        Assert.Equal(("name_taken", MissionErrors.NameTaken, "new_name"),
            Refusal(host, "rename_item", $$"""{ "ref": "{{Wave}}", "new_name": "ol_start" }"""));
        Assert.Equal(("invalid_argument", EditTools.MissionItem, "ref"),
            Refusal(host, "rename_item", $$"""{ "ref": "label:{{S}}#ol_wing", "new_name": "ol_x" }"""));
        Assert.Equal(("invalid_argument", EditTools.MissionItem, "ref"),
            Refusal(host, "rename_item", $$"""{ "ref": "inst:{{S}}#ol_w1", "new_name": "ol_x" }"""));
        Assert.Equal(("invalid_argument", EditTools.MissionItem, "ref"),
            Refusal(host, "rename_item", $$"""{ "ref": "{{WaveLine(2)}}", "new_name": "ol_x" }"""));
    }

    /// <summary>A delete of a trigger that lines name asks for confirm: true, and a preview tells it. With confirm:
    /// true, the trigger goes in one entry, and the lines that name it stay and show "Missing".</summary>
    [Fact]
    public void DeleteItemsOfATriggerThatLinesNameNeedsAConfirmation()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var arguments = $$"""{ "refs": [ "{{End}}" ] }""";

        var preview = Ok(host, "delete_items", $$"""{ "refs": [ "{{End}}" ], "preview": true }""");
        var asks = Call(host, "delete_items", arguments);
        Assert.Equal(before, m.State());
        var result = Ok(host, "delete_items", $$"""{ "refs": [ "{{End}}" ], "confirm": true }""");

        Assert.True((bool)preview["needs_confirmation"]!);
        var message = (string)asks.Content["message"]!;
        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), ((string?)asks.Content["code"], (string?)asks.Content["detail"]));
        Assert.StartsWith("After this edit, 3 lines name an item that is gone.", message);
        Assert.Contains(preview["warnings"]!.AsArray(), w => message.StartsWith((string)w!["message"]!, StringComparison.Ordinal));
        Assert.Equal([$"trg:{Script}#ol_end"], result["deletes"]!.AsArray().Select(d => (string?)d!["ref"]));
        Assert.False(m.Has("ol_end"));
        Assert.Equal(["Act_ActTrig = ol_wave", "Act_ActTrig = ol_end"], m.Lines("ol_start")[3..]);
        Assert.Contains(result["missing"]!.AsArray(),
            x => (string?)x!["ref"] == $"trg:{Script}#ol_start" && (string?)x["key"] == "Act_ActTrig");
        AssertOneMissionEntry(m, result, "Delete trigger ol_end", before);
    }

    /// <summary>delete_items deletes a mission ship with its unused NPC (decision D669), a ship build and an order list
    /// in one entry. An NPC that a ship names refuses with RequiredBy. A line, a label and the [Mission] section
    /// refuse, and the refusal of a line names remove_action.</summary>
    [Fact]
    public void DeleteItemsDeletesMissionItemsAndGivesItsRefusals()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "delete_items", $$"""
            { "refs": [ "sec:{{S}}#MsnShip/ol_ship_3", "sec:{{S}}#ObjList/ol_orders" ], "confirm": true }
            """);

        Assert.False(m.Has(Item("MsnShip", "ol_ship_3")));
        Assert.False(m.Has(Item("NPC", "ol_npc_shared")));
        Assert.False(m.Has(Item("ObjList", "ol_orders")));
        Assert.True(m.Has(Item("NPCShipArch", "ol_shared_build", SharedShips)));
        AssertOneMissionEntry(m, result, "Delete 2 items", before);

        Assert.Equal(("refused", MissionErrors.RequiredBy, "refs[0]"),
            Refusal(host, "delete_items", $$"""{ "refs": [ "sec:{{S}}#NPC/ol_npc_1" ], "confirm": true }"""));
        Assert.Equal(("refused", EditTools.MissionItem, "refs[0]"),
            Refusal(host, "delete_items", $$"""{ "refs": [ "sec:{{S}}#Mission" ], "confirm": true }"""));
        Assert.Equal(("invalid_argument", EditTools.MissionItem, "refs[1]"),
            Refusal(host, "delete_items", $$"""{ "refs": [ "{{End}}", "{{WaveLine(2)}}" ], "confirm": true }"""));
        Assert.Equal(("invalid_argument", EditTools.MissionItem, "refs[0]"),
            Refusal(host, "delete_items", $$"""{ "refs": [ "label:{{S}}#ol_wing" ], "confirm": true }"""));
        var line = Call(host, "delete_items", $$"""{ "refs": [ "{{WaveLine(2)}}" ], "confirm": true }""");
        Assert.Contains("remove_action", (string?)line.Content["message"]);
        Assert.Equal(before, m.State());
    }

    /// <summary>delete_items deletes a linked prefab instance with its members and its record in one entry, as Delete
    /// on the frame of the graph does. An instance that the script does not have gives not_found.</summary>
    [Fact]
    public void DeleteItemsDeletesAPrefabInstance()
    {
        using var m = WithGate();
        var host = new TestAgentHost(m.Session);
        var before = m.State();

        var result = Ok(host, "delete_items", $$"""{ "refs": [ "inst:{{S}}#ol_w1" ], "confirm": true }""");

        Assert.Null(m.Missions.Graph(Script).Instance("ol_w1"));
        Assert.False(m.Has("ol_w1_end"));
        Assert.Equal((EditOrigin.Agent, "Agent: Delete instance ol_w1"), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.NotNull(result["inverse"]);
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.Equal(("not_found", MissionErrors.ItemNotFound, "refs[0]"),
            Refusal(host, "delete_items", $$"""{ "refs": [ "inst:{{S}}#ol_w2" ], "confirm": true }"""));
    }

    /// <summary>duplicate_items copies a trigger with a free name after its own name, as Ctrl+D does, and an item, in
    /// one entry. An offset does not move a mission copy, and the result warns about each copy. A label refuses.</summary>
    [Fact]
    public void DuplicateItemsCopiesATriggerAndAnItem()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "duplicate_items", $$"""
            { "refs": [ "{{Wave}}", "sec:{{S}}#ObjList/ol_orders" ], "offset": [0, 0, 100] }
            """);

        var adds = Texts(result["adds"]);
        Assert.Equal(2, adds.Length);
        Assert.Equal($"trg:{Script}#ol_wave_2", adds[0]);
        Assert.StartsWith($"sec:{Script}#ObjList/", adds[1]);
        Assert.Equal(["system = ol_system", "nickname = ol_wave_2", "Cnd_Timer = 3", "Act_SpawnShip = ol_ship_1",
            "Act_DeactTrig = ol_end", "Act_ActTrig = ol_end"], m.Lines("ol_wave_2"));
        Assert.Equal(adds, result["warnings"]!.AsArray().Select(w => (string?)w!["ref"]));
        Assert.Contains("is an item of a mission", (string?)result["warnings"]![0]!["message"]);
        AssertOneMissionEntry(m, result, "Duplicate 2 items", before);

        var one = Ok(host, "duplicate_items", $$"""{ "refs": [ "{{Wave}}" ] }""");
        Assert.Equal("Agent: Duplicate trigger ol_wave", (string?)one["undo_label"]);
        Assert.Empty(one["warnings"]!.AsArray());
        Assert.Equal(("invalid_argument", EditTools.MissionItem, "refs[0]"),
            Refusal(host, "duplicate_items", $$"""{ "refs": [ "label:{{S}}#ol_wing" ] }"""));
    }

    /// <summary>Each generic tool on a mission item can be a step of edit_batch. All steps are one entry, and the
    /// result gives the version of the script. Each step gives its own inverse.</summary>
    [Fact]
    public void TheGenericToolsOnMissionItemsAreStepsOfEditBatch()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "edit_batch", $$"""
            { "label": "Rework the wave", "steps": [
                { "tool": "duplicate_items", "arguments": { "refs": [ "{{Wave}}" ] } },
                { "tool": "rename_item", "arguments": { "ref": "trg:{{S}}#ol_wave_2", "new_name": "ol_wave_b" } },
                { "tool": "set_fields", "arguments": { "ref": "{{Ship1}}", "fields": { "jumper": true } } },
                { "tool": "delete_items", "arguments": { "refs": [ "trg:{{S}}#ol_wave_b" ] } } ] }
            """);

        Assert.Equal((EditOrigin.Agent, "Agent: Rework the wave"), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.Equal(2, (long?)result["versions"]![Script]);
        Assert.All(result["steps"]!.AsArray(), step => Assert.NotNull(step!["inverse"]));
        Assert.False(m.Has("ol_wave_2"));
        Assert.False(m.Has("ol_wave_b"));
        Assert.Contains("jumper = true", m.Lines(Item("MsnShip", "ol_ship_1")));
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }

    /// <summary>A rename of a trigger of the template of a prefab works in prefab mode, as F2 in the graph of prefab
    /// mode does.</summary>
    [Fact]
    public void RenameItemRenamesATriggerOfATemplate()
    {
        using var m = PrefabInstanceTests.Create();
        var host = new TestAgentHost(m.Session);

        var result = Ok(host, "rename_item", """{ "ref": "trg:prefab:mod:ol_gate#spawn", "new_name": "spawn_wave" }""");

        var rename = Assert.Single(result["renames"]!.AsArray())!;
        Assert.Equal(("trg:prefab:mod:ol_gate#spawn", "trg:prefab:mod:ol_gate#spawn_wave"),
            ((string?)rename["from"], (string?)rename["to"]));
    }

    /// <summary>Each generic tool on a mission item and the gesture of the Hierarchy or of the graph give the same files
    /// (section 13.1, item 7 and section 8.1 of the MCP1 design): F2 on a trigger and on an item, Ctrl+D on a trigger and
    /// on an item, Delete on a trigger, on a mission ship and on a prefab instance.</summary>
    [Fact]
    public void TheGenericToolsGiveTheFilesOfTheGesturesOfTheMissionStudio()
    {
        static MissionAnalysis AnalysisOf(MissionEdits m) => m.Missions.Model(Script).Analysis;
        static MissionRef TriggerRow(MissionEdits m, string name) =>
            new(Script, MissionItemKind.Trigger, AnalysisOf(m).Index.Trigger(name)!.Section.Ref);
        static MissionRef ItemRow(MissionEdits m, MissionSectionKind kind, string name) =>
            MissionRef.OfSection(AnalysisOf(m).Index.Item(kind, name)!.Ref, -1);

        AssertUiParity(m => MissionTreeEdits.Rename(AnalysisOf(m), TriggerRow(m, "ol_end"), "ol_finish", null),
            "rename_item", $$"""{ "ref": "{{End}}", "new_name": "ol_finish" }""");
        AssertUiParity(m => MissionTreeEdits.Rename(AnalysisOf(m), ItemRow(m, MissionSectionKind.Ship, "ol_ship_1"), "ol_lead", null),
            "rename_item", $$"""{ "ref": "{{Ship1}}", "new_name": "ol_lead" }""");
        AssertUiParity(m => MissionTreeEdits.Duplicate(AnalysisOf(m), TriggerRow(m, "ol_wave")),
            "duplicate_items", $$"""{ "refs": [ "{{Wave}}" ] }""");
        AssertUiParity(m => MissionTreeEdits.Duplicate(AnalysisOf(m), ItemRow(m, MissionSectionKind.Formation, "ol_form")),
            "duplicate_items", $$"""{ "refs": [ "sec:{{S}}#MsnFormation/ol_form" ] }""");
        AssertUiParity(m => MissionTreeEdits.Delete(AnalysisOf(m), TriggerRow(m, "ol_end"), null),
            "delete_items", $$"""{ "refs": [ "{{End}}" ], "confirm": true }""");
        AssertUiParity(m => MissionTreeEdits.Delete(AnalysisOf(m), ItemRow(m, MissionSectionKind.Ship, "ol_ship_1"), null),
            "delete_items", $$"""{ "refs": [ "{{Ship1}}" ], "confirm": true }""");
        AssertUiParity(m => GraphEditModel.DeleteInstance(Script, AnalysisOf(m).Prefabs!.Instances.Single(i => i.Id == "ol_w1")),
            "delete_items", $$"""{ "refs": [ "inst:{{S}}#ol_w1" ], "confirm": true }""", () => WithGate("ol_start"));
        // A matched instance goes with the delete of each trigger that it claims.
        AssertUiParity(m => GraphEditModel.DeleteInstance(Script, AnalysisOf(m).Prefabs!.Instances.Single(i => i.Id == "success/ol_win")),
            "delete_items", $$"""{ "refs": [ "inst:{{S}}#success/ol_win" ], "confirm": true }""",
            () => Create(PrefabStudioTests.MatchedText));
    }

    /// <summary>A mission item of the hidden group Missions gives not_available in each generic tool (decision D725).
    /// An item of a system still takes edits.</summary>
    [Fact]
    public void AMissionItemOfTheHiddenGroupMissionsGivesNotAvailable()
    {
        var (m, host) = Open();
        using var _ = m;
        host.Hidden.Add(ToolGroup.Missions);
        var before = m.State();

        (string Tool, string Arguments)[] calls =
        [
            ("set_fields", $$"""{ "ref": "{{Ship1}}", "fields": { "jumper": true } }"""),
            ("set_text", $$"""{ "ref": "{{WaveLine(2)}}", "field": "seconds", "text": "x" }"""),
            ("rename_item", $$"""{ "ref": "{{End}}", "new_name": "ol_finish" }"""),
            ("delete_items", $$"""{ "refs": [ "inst:{{S}}#ol_w1" ], "confirm": true }"""),
            ("duplicate_items", $$"""{ "refs": [ "sec:{{B}}#NPCShipArch/ol_build_1" ] }"""),
        ];

        foreach (var (tool, arguments) in calls)
        {
            var (code, detail, _) = Refusal(host, tool, arguments);
            Assert.Equal(("not_available", "GroupHidden"), (code, detail));
        }
        Assert.Equal(before, m.State());
    }

    /// <summary>The edit tools that change only sections of INI files refuse a mission item with invalid_argument and
    /// the detail MissionItem. The text names the tools that change mission items. A section of another file under
    /// DATA\MISSIONS, for example the shared ship build file, refuses with WorldFile in each edit tool. Nothing
    /// changes.</summary>
    [Fact]
    public void TheOtherEditToolsRefuseAMissionItem()
    {
        var (m, host) = Open();
        using var _ = m;
        var before = m.State();
        var shared = $"sec:{SharedShips.Replace(@"\", @"\\")}#NPCShipArch/ol_shared_build";

        (string Tool, string Arguments)[] calls =
        [
            ("scene_transform", $$"""{ "moves": [ { "ref": "{{Ship1}}", "position": [1, 2, 3] } ] }"""),
            ("scene_transform", $$"""{ "moves": [ { "ref": "{{Wave}}", "position": [1, 2, 3] } ] }"""),
            ("scene_set_component", $$"""{ "ref": "{{Ship1}}", "component": "loadout_user", "action": "remove" }"""),
            ("scene_set_encounters", $$"""{ "zone": "{{Ship1}}", "encounters": [] }"""),
            ("prop_group_save", $$"""{ "refs": [ "{{End}}" ], "confirm": true }"""),
            ("create_from_template", $$"""{ "kind": "ship", "template": "sec:{{B}}#NPCShipArch/ol_build_1", "nickname": "ol_x" }"""),
        ];

        foreach (var (tool, arguments) in calls)
        {
            var result = Call(host, tool, arguments);
            Assert.True(result.IsError, result.Text);
            Assert.Equal(("invalid_argument", EditTools.MissionItem), ((string?)result.Content["code"], (string?)result.Content["detail"]));
            Assert.Contains("set_fields, set_text, rename_item, delete_items and duplicate_items", (string?)result.Content["message"]);
            Assert.StartsWith(tool, (string?)result.Content["message"]);
        }
        Assert.Equal(("refused", MissionErrors.WorldFile, "ref"),
            Refusal(host, "set_fields", $$"""{ "ref": "{{shared}}", "fields": { "level": "d7" } }"""));
        Assert.Equal(("refused", MissionErrors.WorldFile, "ref"),
            Refusal(host, "scene_set_component", $$"""{ "ref": "{{shared}}", "component": "loadout_user", "action": "remove" }"""));
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }
}
