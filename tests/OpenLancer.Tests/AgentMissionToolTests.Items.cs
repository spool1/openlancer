using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The tool <c>mission_add_item</c> of section 7.4 of the MCP1 design (step 4 of MCP1-3). Each call adds one item of a
/// kind with the lines of its fields in one request of the shared batch. So it is one Undo step with the edit origin
/// Agent, it runs in strict mode, and its result gives the reference of the new item and the inverse.
/// </summary>
public sealed partial class AgentMissionToolTests
{
    /// <summary>The ship build file of the test script as JSON text.</summary>
    private static readonly string B = Ships.Replace(@"\", @"\\");

    /// <summary>The names of the world for the items of the tests: the names of the fixture, and the pilots, goods,
    /// faces, bases and comm lines that the items name.</summary>
    private static readonly Dictionary<MissionValueKind, string[]> ItemWorld = new(WorldNames)
    {
        [MissionValueKind.Pilot] = ["ol_pilot"],
        [MissionValueKind.Equipment] = ["ol_good"],
        [MissionValueKind.Good] = ["ol_good"],
        [MissionValueKind.Head] = ["ol_head"],
        [MissionValueKind.Body] = ["ol_body"],
        [MissionValueKind.Base] = ["ol_base"],
        [MissionValueKind.CommLine] = ["dx_ol_0010"],
    };

    /// <summary>The test profile with the names of <see cref="ItemWorld"/>.</summary>
    private static (MissionEdits M, TestAgentHost Host) OpenItems(string? text = null)
    {
        var (m, host) = Open(text);
        m.Missions.UseWorld(new TestMissionWorld(m.Session, ItemWorld));
        return (m, host);
    }

    /// <summary>The arguments of a mission_add_item call on the test script.</summary>
    private static string AddItem(string kind, string? nickname, string fields, string? steps = null) =>
        $$"""
        { "script": "msn:{{S}}", "kind": "{{kind}}"{{(nickname == null ? "" : $", \"nickname\": \"{nickname}\"")}},
          "fields": {{fields}}{{(steps == null ? "" : $", \"steps\": {steps}")}} }
        """;

    /// <summary>One case for each of the ten kinds: the arguments, the reference of the new item, its lines and the
    /// label of the Undo step.</summary>
    public static TheoryData<string, string, string, string[], string> Kinds => new()
    {
        {
            "ship", AddItem("ship", "ol_new", """
                { "npc": "ol_npc_1", "system": "ol_system", "position": [10, 0, -20], "orientation": [1, 0, 0, 0],
                  "label": ["ol_wing", "ol_new_wing"], "init_objectives": "ol_orders", "jumper": true, "hit_points": 900 }
                """),
            $"sec:{Script}#MsnShip/ol_new",
            ["nickname = ol_new", "system = ol_system", "npc = ol_npc_1", "label = ol_wing", "label = ol_new_wing",
                "position = 10, 0, -20", "orientation = 1, 0, 0, 0", "init_objectives = ol_orders", "jumper = true",
                "hit_points = 900"],
            "Add mission ship ol_new"
        },
        {
            "formation", AddItem("formation", "ol_form_2", """
                { "ship": ["ol_ship_3"], "formation": "ol_shape", "npc": "ol_npc_shared", "position": [0, 100, 0] }
                """),
            $"sec:{Script}#MsnFormation/ol_form_2",
            ["nickname = ol_form_2", "npc = ol_npc_shared", "position = 0, 100, 0", "ship = ol_ship_3", "formation = ol_shape"],
            "Add formation ol_form_2"
        },
        {
            "npc", AddItem("npc", "ol_npc_2", """
                { "npc_ship_arch": "ol_build_1", "affiliation": "ol_faction", "voice": "ol_voice",
                  "space_costume": { "head": "ol_head", "body": "ol_body" } }
                """),
            $"sec:{Script}#NPC/ol_npc_2",
            ["nickname = ol_npc_2", "npc_ship_arch = ol_build_1", "affiliation = ol_faction", "voice = ol_voice",
                "space_costume = ol_head, ol_body"],
            "Add NPC ol_npc_2"
        },
        {
            "ship_build", AddItem("ship_build", "ol_build_2", """
                { "ship_archetype": "ol_arch", "loadout": "ol_loadout_2", "level": "d7", "pilot": "ol_pilot",
                  "npc_class": ["lawful", "d7"] }
                """),
            $"sec:{Ships}#NPCShipArch/ol_build_2",
            ["nickname = ol_build_2", "ship_archetype = ol_arch", "loadout = ol_loadout_2", "level = d7", "pilot = ol_pilot",
                "npc_class = lawful, d7"],
            "Add ship build ol_build_2"
        },
        {
            "solar", AddItem("solar", "ol_solar", """
                { "archetype": "ol_arch", "system": "ol_system", "position": [0, 0, 500], "faction": "ol_faction",
                  "base": "ol_base", "label": "ol_targets" }
                """),
            $"sec:{Script}#MsnSolar/ol_solar",
            ["nickname = ol_solar", "archetype = ol_arch", "system = ol_system", "position = 0, 0, 500",
                "faction = ol_faction", "base = ol_base", "label = ol_targets"],
            "Add solar ol_solar"
        },
        {
            "loot", AddItem("loot", "ol_crate", """
                { "archetype": "ol_good", "equip_amount": 1, "rel_pos_obj": "ol_ship_3", "rel_pos_offset": [0, 50, 0],
                  "can_jettison": null }
                """),
            $"sec:{Script}#MsnLoot/ol_crate",
            ["nickname = ol_crate", "archetype = ol_good", "equip_amount = 1", "rel_pos_obj = ol_ship_3",
                "rel_pos_offset = 0, 50, 0"],
            "Add loot ol_crate"
        },
        {
            "order_list", AddItem("order_list", "ol_orders_2", """{ "system": "ol_system" }""", """
                [ { "key": "GotoVec", "values": { "movement": "goto_cruise", "point": [0, 0, 1000], "range": 100 } },
                  { "key": "Delay", "values": { "seconds": 5 } },
                  { "key": "BreakFormation" } ]
                """),
            $"sec:{Script}#ObjList/ol_orders_2",
            ["nickname = ol_orders_2", "system = ol_system", "GotoVec = goto_cruise, 0, 0, 1000, 100", "Delay = 5",
                "BreakFormation = no_params"],
            "Add order list ol_orders_2"
        },
        {
            "objective", AddItem("objective", "ol_goal", """{ "type": { "texts": ["ids", "12345"] }, "state": "HIDDEN" }"""),
            $"sec:{Script}#NNObjective/ol_goal",
            ["nickname = ol_goal", "type = ids, 12345", "state = HIDDEN"],
            "Add objective ol_goal"
        },
        {
            "conversation", AddItem("conversation", "ol_talk", """
                { "system": "ol_system", "line": [ { "speaker": "ol_ship_1", "listener": "ol_ship_2", "line": "dx_ol_0010" } ] }
                """),
            $"sec:{Script}#Dialog/ol_talk",
            ["nickname = ol_talk", "system = ol_system", "line = ol_ship_1, ol_ship_2, dx_ol_0010"],
            "Add conversation ol_talk"
        },
        {
            "random_encounter", AddItem("random_encounter", "ol_ambush_enc", """
                { "encounter_type": "SP_Attack", "attacker_rep_name": "ol_faction", "num_forms": { "fewest": 1, "most": 2 },
                  "ol_extra": 5 }
                """),
            $"sec:{Script}#MsnRandEnc/ol_ambush_enc",
            ["nickname = ol_ambush_enc", "encounter_type = SP_Attack", "attacker_rep_name = ol_faction", "num_forms = 1, 2",
                "ol_extra = 5"],
            "Add random encounter ol_ambush_enc"
        },
    };

    /// <summary>
    /// mission_add_item adds one item of each kind with the lines of its fields, in one entry. The result gives the
    /// canonical reference of the new item, the version and the inverse. A value, an object of values by name, the
    /// texts by position, a list of lines of a key that repeats and null are the forms of a field. The formation and
    /// ship lines of a formation come after its ship keys. The orders of an order list come from steps, and an order
    /// without values gets no_params, as the game data writes it. A ship build goes into the ship build file. The
    /// inverse removes the item after a later edit of a trigger, and one undo puts back the state before.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void AddItemAddsOneItemOfEachKind(string kind, string arguments, string reference, string[] lines, string label)
    {
        var (m, host) = OpenItems();
        using var _ = m;
        var before = m.State();
        var item = ItemRefs.Parse(reference);

        var result = Ok(host, "mission_add_item", arguments);

        Assert.Equal([reference], Texts(result["adds"]));
        Assert.Empty(Texts(result["changes"]));
        Assert.Equal(reference, ItemRefs.Format(item));
        Assert.Equal(lines, m.Lines(new ItemRef(item.Path!, item.Section!)));
        Assert.Equal(("applied", "Agent: " + label), ((string?)result["status"], (string?)result["undo_label"]));
        Assert.Equal((EditOrigin.Agent, "Agent: " + label), (m.Session.UndoOrigin, m.Session.UndoLabel));
        // A ship build is in the ship build file, so the version of the script stays.
        Assert.Equal(kind == "ship_build" ? 1 : 2, (long?)result["versions"]![Script]);
        Assert.Equal([("mission_add_item", "Agent: " + label)], host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));

        Ok(host, "mission_set_condition", $$"""{ "trigger": "{{End}}", "condition": { "key": "Cnd_Timer", "values": { "seconds": 8 } } }""");
        Ok(host, "mission_edit", Edit(result["inverse"]!.ToJsonString()));
        Assert.False(m.Has(new ItemRef(item.Path!, item.Section!)));
        Assert.Equal("Cnd_Timer = 8", m.Lines("ol_end")[1]);
        m.Session.Undo();
        m.Session.Undo();
        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>The automated form of steps 3 to 5 of workflow 3 (section 9.5 of the MCP1 design): an edit_batch adds a
    /// ship build, an NPC that names it and a ship that names the NPC. The batch is one entry, and one undo removes the
    /// ship, its NPC and its build. Without a nickname, the layer makes a free name for the ship. A key in the form of
    /// texts by position comes after the keys of the command of the ship, as a later edit of the key puts it.</summary>
    [Fact]
    public void ABatchOfABuildAnNpcAndAShipIsOneEntry()
    {
        var (m, host) = OpenItems();
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "edit_batch", $$"""
            { "label": "Add the pirate", "steps": [
              { "tool": "mission_add_item", "arguments": {{AddItem("ship_build", "ol_pirate_build",
                  """{ "ship_archetype": "ol_arch", "loadout": "ol_loadout", "pilot": "ol_pilot", "level": "d5" }""")}} },
              { "tool": "mission_add_item", "arguments": {{AddItem("npc", "ol_pirate_npc",
                  """{ "npc_ship_arch": "ol_pirate_build", "affiliation": "ol_faction", "voice": "ol_voice" }""")}} },
              { "tool": "mission_add_item", "arguments": {{AddItem("ship", null,
                  """
                  { "npc": "ol_pirate_npc", "system": "ol_system", "position": [0, 0, 3000], "random_name": true,
                    "label": { "texts": ["ol_pirates"] } }
                  """)}} } ] }
            """);

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal([$"sec:{Ships}#NPCShipArch/ol_pirate_build", $"sec:{Script}#NPC/ol_pirate_npc",
            $"sec:{Script}#MsnShip/ol_test_ship_1"], Texts(result["adds"]));
        Assert.Equal(["nickname = ol_test_ship_1", "system = ol_system", "npc = ol_pirate_npc", "position = 0, 0, 3000",
            "random_name = true", "label = ol_pirates"], m.Lines(Item("MsnShip", "ol_test_ship_1")));
        Assert.Equal((EditOrigin.Agent, "Agent: Add the pirate"), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.Single(host.Edits);

        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>A formation that names a ship before the ship exists takes no ship (rule M08). So a new ship that two
    /// formations name goes before the first of them in the same entry, and the patch places it with ol.before (section
    /// 6.4 of the M6b design). Both formations then come after the ship. With rel_pos, the ship gets the place near an
    /// object. The texts form gives the count of a cargo line. The inverse asks for confirm: true, because the ship line
    /// of each formation then names a ship that is gone.</summary>
    [Fact]
    public void AShipThatAFormationNamesGoesBeforeTheFormation()
    {
        var (m, host) = OpenItems(Text
            .Replace("formation = ol_shape", "formation = ol_shape\nship = ol_new")
            .Replace("[ObjList]", "[MsnFormation]\nnickname = ol_form_b\nformation = ol_shape\nship = ol_new\n\n[ObjList]"));
        using var _ = m;
        var before = m.State();

        var result = Ok(host, "mission_add_item", AddItem("ship", "ol_new", """
            { "npc": "ol_npc_1", "rel_pos": { "heading": 90, "anchor": "ol_station", "range": 500 },
              "cargo": [ { "texts": ["ol_good", "5"] } ] }
            """));

        var names = m.Session.OpenDocument(Script)!.Working.Select(s => s.Name + "/" + SectionIdentity.KeyOf(s)).ToList();
        Assert.Equal(names.IndexOf("MsnFormation/ol_form") - 1, names.IndexOf("MsnShip/ol_new"));
        Assert.True(names.IndexOf("MsnFormation/ol_form_b") > names.IndexOf("MsnFormation/ol_form"));
        Assert.Equal(["nickname = ol_new", "npc = ol_npc_1", "rel_pos = 90, ol_station, 500", "cargo = ol_good, 5"],
            m.Lines(Item("MsnShip", "ol_new")));
        Assert.Contains($"sec:{Script}#MsnShip/ol_new", Texts(result["adds"]));
        Assert.Equal("Agent: Add mission ship ol_new", (string?)result["undo_label"]);
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule == "M08");
        Assert.Equal("ol_form", m.Missions.Model(Script).Analysis.Index.TakenShips["ol_new"].Nickname);
        var patch = m.Session.SaveText(Script)!;
        Assert.Contains("nickname = ol_new", patch);
        Assert.Contains("ol.before", patch);

        var inverse = result["inverse"]!.ToJsonString();
        var asked = Error(host, "mission_edit", Edit(inverse));
        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), (asked.Code, asked.Detail));
        Assert.True(m.Has(Item("MsnShip", "ol_new")));
        Ok(host, "mission_edit", $$"""{ "commands": [ {{inverse}} ], "confirm": true }""");
        Assert.False(m.Has(Item("MsnShip", "ol_new")));
        m.Session.Undo();
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }

    /// <summary>The encounter of a random encounter reads only the keys after its encounter_type line. So the tool writes
    /// that line first, also when fields gives it last. The other lines keep the order of the fields.</summary>
    [Fact]
    public void TheEncounterTypeOfARandomEncounterComesFirst()
    {
        var (m, host) = OpenItems();
        using var _ = m;

        Ok(host, "mission_add_item", AddItem("random_encounter", "ol_enc", """
            { "attacker_rep_name": "ol_faction", "num_forms": { "fewest": 1, "most": 2 }, "encounter_type": "SP_Attack" }
            """));

        Assert.Equal(["nickname = ol_enc", "encounter_type = SP_Attack", "attacker_rep_name = ol_faction", "num_forms = 1, 2"],
            m.Lines(Item("MsnRandEnc", "ol_enc")));
    }

    /// <summary>A list of numbers for formation_position, a key of a random encounter that repeats with a position, is
    /// one line. A list of positions gives one line for each position.</summary>
    [Fact]
    public void AListOfNumbersIsOnePositionOfAKeyThatRepeats()
    {
        var (m, host) = OpenItems();
        using var _ = m;

        Ok(host, "mission_add_item", AddItem("random_encounter", "ol_enc_1", """
            { "encounter_type": "SP_Attack", "formation_position": [0, 0, 100] }
            """));
        Ok(host, "mission_add_item", AddItem("random_encounter", "ol_enc_2", """
            { "encounter_type": "SP_Attack", "formation_position": [ [0, 0, 100], [0, 0, 200] ] }
            """));

        Assert.Equal(["nickname = ol_enc_1", "encounter_type = SP_Attack", "formation_position = 0, 0, 100"],
            m.Lines(Item("MsnRandEnc", "ol_enc_1")));
        Assert.Equal(["nickname = ol_enc_2", "encounter_type = SP_Attack", "formation_position = 0, 0, 100",
            "formation_position = 0, 0, 200"], m.Lines(Item("MsnRandEnc", "ol_enc_2")));
    }

    /// <summary>A random encounter name has at most 31 characters (decision D683 of M6b, MCP1 open alignment item 3). A
    /// name of 31 characters applies. A name of 32 characters gives invalid_value with NameInvalid, although the pattern
    /// of the schema takes 47 characters. A name of 48 characters does not fit the pattern.</summary>
    [Fact]
    public void ARandomEncounterNameHasAtMost31Characters()
    {
        var (m, host) = OpenItems();
        using var _ = m;
        var fits = "ol_" + new string('e', MissionNames.MaxEncounterName - 3);
        var tooLong = fits + "e";
        const string fields = """{ "encounter_type": "SP_Attack" }""";

        var added = Ok(host, "mission_add_item", AddItem("random_encounter", fits, fields));
        var refused = Call(host, "mission_add_item", AddItem("random_encounter", tooLong, fields));
        var pattern = Call(host, "mission_add_item", AddItem("random_encounter", "ol_" + new string('e', 45), fields));

        Assert.Equal([$"sec:{Script}#MsnRandEnc/{fits}"], Texts(added["adds"]));
        Assert.Equal(31, fits.Length);
        Assert.Equal(("invalid_value", MissionErrors.NameInvalid, "nickname"),
            ((string?)refused.Content["code"], (string?)refused.Content["detail"], (string?)refused.Content["field"]));
        Assert.Contains("31", (string?)refused.Content["message"]);
        Assert.Equal(("invalid_argument", ToolInput.BadArgument, "nickname"),
            ((string?)pattern.Content["code"], (string?)pattern.Content["detail"], (string?)pattern.Content["field"]));
        Assert.Equal(1, m.Session.Revision);
    }

    /// <summary>A script that names no ship build file cannot get a ship build: refused with NoShipBuildFile. The inverse
    /// of a new NPC refuses with RequiredBy while a ship names the NPC, as a delete of a component that another one
    /// requires (RequireComponent). Nothing changes.</summary>
    [Fact]
    public void AddItemGivesNoShipBuildFileAndItsInverseGivesRequiredBy()
    {
        var (bare, bareHost) = OpenItems(Text.Replace(@"npc_ship_file = missions\ol_test\npcships.ini", ""));
        using (bare)
        {
            var before = bare.State();
            var error = Call(bareHost, "mission_add_item", AddItem("ship_build", null,
                """{ "ship_archetype": "ol_arch", "loadout": "ol_loadout" }"""));
            Assert.Equal(("refused", MissionErrors.NoShipBuildFile, "script"),
                ((string?)error.Content["code"], (string?)error.Content["detail"], (string?)error.Content["field"]));
            Assert.Equal(before, bare.State());
        }

        var (m, host) = OpenItems();
        using var _ = m;
        var npc = Ok(host, "mission_add_item", AddItem("npc", "ol_npc_new", """{ "npc_ship_arch": "ol_build_1" }"""));
        Ok(host, "mission_add_item", AddItem("ship", "ol_user", """{ "npc": "ol_npc_new" }"""));
        var state = m.State();

        var inverse = Error(host, "mission_edit", Edit(npc["inverse"]!.ToJsonString()));

        Assert.Equal(("refused", MissionErrors.RequiredBy), (inverse.Code, inverse.Detail));
        Assert.Equal(state, m.State());
    }

    /// <summary>The refusals of mission_add_item: a ship without fields.npc or with two places, a key that the section
    /// does not have (with the keys of the section), a value of the wrong kind, a form that does not fit its key, the
    /// nickname or an order in the fields, steps of another kind, an order that is not a directive, a key two times, a
    /// name in use, a number that a patch line cannot hold, and an empty list of texts for a key that needs a value.
    /// Each error names its argument, and nothing changes.</summary>
    [Fact]
    public void AddItemGivesItsRefusals()
    {
        var (m, host) = OpenItems();
        using var _ = m;
        var before = m.State();
        const string goto100 = """[ { "key": "GotoVec", "values": { "movement": "goto_cruise", "point": [0, 0, 1], "range": 1 } } ]""";

        var refusals = new (string Arguments, string Code, string Detail, string Field)[]
        {
            (AddItem("ship", "ol_new", """{ "system": "ol_system" }"""), "invalid_argument", MissionEditTools.NeedsNpc, "fields.npc"),
            (AddItem("ship", "ol_new", """{ "npc": null }"""), "invalid_argument", MissionEditTools.NeedsNpc, "fields.npc"),
            (AddItem("ship", "ol_new", """{ "npc": "ol_npc_1", "pilot": "ol_pilot" }"""), "invalid_value", MissionErrors.NotAKey,
                "fields.pilot"),
            (AddItem("solar", null, """{ "position": "far" }"""), "invalid_value", MissionErrors.ValueKind, "fields.position"),
            (AddItem("ship", "ol_new", """{ "npc": "ol_npc_1", "position": [0, 0, 0], "rel_pos": { "heading": 0, "anchor": "ol_station" } }"""),
                "invalid_argument", ToolInput.BadArgument, "fields.rel_pos"),
            (AddItem("ship", "ol_new", """{ "npc": "ol_npc_1", "hit_points": "many" }"""), "invalid_value",
                MissionErrors.ValueKind, "fields.hit_points"),
            (AddItem("npc", null, """{ "space_costume": "ol_head" }"""), "invalid_argument", ToolInput.BadArgument,
                "fields.space_costume"),
            (AddItem("solar", null, """{ "system": [ { "system": "ol_system" } ] }"""), "invalid_argument", ToolInput.BadArgument,
                "fields.system"),
            (AddItem("loot", null, """{ "label": [ "ol_a", { "lable": "ol_b" } ] }"""), "invalid_argument",
                MissionErrors.NoSuchValue, "fields.label[1]"),
            (AddItem("solar", null, """{ "nickname": "ol_solar" }"""), "invalid_argument", ToolInput.BadArgument, "fields.nickname"),
            (AddItem("order_list", null, """{ "GotoVec": "goto_cruise" }"""), "invalid_argument", ToolInput.BadArgument,
                "fields.GotoVec"),
            (AddItem("solar", null, "{}", goto100), "invalid_argument", ToolInput.BadArgument, "steps"),
            (AddItem("order_list", null, "{}", """[ { "key": "Cnd_True" } ]"""), "invalid_value", MissionErrors.NotADirective,
                "steps[0]"),
            (AddItem("solar", null, """{ "label": "ol_a", "LABEL": "ol_b" }"""), "invalid_argument", ToolInput.BadArgument,
                "fields.LABEL"),
            (AddItem("ship", "OL_SHIP_1", """{ "npc": "ol_npc_1" }"""), "name_taken", MissionErrors.NameTaken, "nickname"),
            (AddItem("solar", "ol_solar", "{}").Replace("\"solar\"", "\"planet\""),
                "invalid_argument", ToolInput.BadArgument, "kind"),
            (AddItem("solar", "ol_bad name", "{}"), "invalid_argument", ToolInput.BadArgument, "nickname"),
            (AddItem("solar", null, """{ "position": [1e300, 0, 0] }"""), "invalid_value", MissionErrors.PatchText,
                "fields.position"),
            (AddItem("ship", "ol_new", """{ "npc": "ol_npc_1", "position": [1e300, 0, 0] }"""), "invalid_value",
                MissionErrors.PatchText, "fields.position"),
            (AddItem("order_list", null, "{}", """[ { "key": "Delay", "values": { "seconds": 1e300 } } ]"""), "invalid_value",
                MissionErrors.PatchText, "steps[0]"),
            (AddItem("solar", null, """{ "system": { "texts": [] } }"""), "invalid_value", MissionErrors.ValueMissing,
                "fields.system"),
        };

        var wrong = new List<string>();
        foreach (var (arguments, code, detail, field) in refusals)
        {
            var result = Call(host, "mission_add_item", arguments);
            var got = ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]);
            if (!result.IsError || got != (code, detail, field))
                wrong.Add($"{arguments}: {got} {result.Content["message"]}");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        var key = Error(host, "mission_add_item", AddItem("ship", "ol_new", """{ "npc": "ol_npc_1", "pilot": "ol_pilot" }"""));
        Assert.StartsWith("The game does not read pilot in a ship.", key.Message);
        Assert.Contains("init_objectives", key.Message);
        Assert.DoesNotContain("nickname", key.Message);
        var npc = Error(host, "mission_add_item", AddItem("ship", "ol_new", "{}"));
        Assert.Contains("add_mission_ship", npc.Message);
        Assert.Equal(before, m.State());
        Assert.Equal(0, m.Session.Revision);
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>
    /// The mission checks check the names of each ship build of the ship build file of a script (final finding 14 of
    /// MCP1-3): the ship archetype, the loadout and the pilot against the game data, and the state graph against its
    /// words. So strict mode refuses a bad build, get_item gives the names of a build, and the check of the script and
    /// the save gate give a bad build that came in without strict mode.
    /// </summary>
    [Fact]
    public void TheChecksCheckTheNamesOfAShipBuild()
    {
        var (m, host) = OpenItems();
        using var _ = m;
        var before = m.State();
        string Build(string fields) => AddItem("ship_build", "ol_bad_build", fields);

        var ship = Error(host, "mission_add_item", Build("""{ "ship_archetype": "no_such_ship_x", "loadout": "ol_loadout" }"""));
        var loadout = Error(host, "mission_add_item", Build("""{ "ship_archetype": "ol_arch", "loadout": "no_such_loadout_x" }"""));
        var pilot = Error(host, "mission_add_item",
            Build("""{ "ship_archetype": "ol_arch", "loadout": "ol_loadout", "pilot": "no_such_pilot_x" }"""));
        var graph = Call(host, "mission_add_item",
            Build("""{ "ship_archetype": "ol_arch", "loadout": "ol_loadout", "state_graph": "NO_GRAPH_X" }"""));

        Assert.All([ship, loadout, pilot], e => Assert.Equal(("new_errors", MissionErrors.NewErrors), (e.Code, e.Detail)));
        Assert.Contains("Ship build 'ol_bad_build' names ship archetype 'no_such_ship_x'. The game data has no ship archetype " +
                        "with this name, so the game drops a mission ship with this build.", ship.Message);
        Assert.Contains("names loadout 'no_such_loadout_x'", loadout.Message);
        Assert.Contains("names pilot 'no_such_pilot_x'", pilot.Message);
        // The state graph has a closed set of words, so the command refuses another word.
        Assert.Equal(("invalid_value", MissionErrors.WordNotInSet, "fields.state_graph"),
            ((string?)graph.Content["code"], (string?)graph.Content["detail"], (string?)graph.Content["field"]));
        Assert.Contains("FIGHTER, TRANSPORT, GUNBOAT, CRUISER, NOTHING, PLAYER", (string?)graph.Content["message"]);
        Assert.Equal(before, m.State());

        var good = Ok(host, "mission_add_item",
            Build("""{ "ship_archetype": "ol_arch", "loadout": "ol_loadout", "pilot": "ol_pilot", "state_graph": "fighter" }"""));
        Assert.Equal("applied", (string?)good["status"]);
        var item = Ok(host, "get_item", $$"""{ "ref": "sec:{{B}}#NPCShipArch/ol_bad_build" }""")["mission_item"]!;
        var names = item["lines"]!.AsArray().SelectMany(l => l!["names"]!.AsArray())
            .Select(n => ((string?)n!["name"], (string?)n["state"])).ToList();
        // The command writes the word of the set, so "fighter" becomes FIGHTER.
        Assert.Equal([("ol_arch", "found"), ("ol_loadout", "found"), ("ol_pilot", "found"), ("FIGHTER", "token")], names);

        // A build that came in without strict mode, for example from a mod, gives an error of the script and of the save.
        var loose = m.Missions.Apply(new SetItemKey(ItemRef.Of(Ships, "NPCShipArch", "ol_build_1"), "loadout",
            [LineSpec.Raw("loadout", "no_such_loadout_x")]));
        Assert.Equal(EditStatus.Applied, loose.Status);
        Assert.Contains(loose.NewProblems, p => p.Severity == DiagnosticSeverity.Error && p.Message.Contains("no_such_loadout_x"));
        var check = Ok(host, "check", $$"""{ "scope": "mission", "ref": "msn:{{S}}" }""");
        Assert.Equal(1, (int?)check["errors"]);
        Assert.Equal($"line:{Ships}#NPCShipArch/ol_build_1/2", (string?)check["problems"]![0]!["ref"]);
        Assert.Contains(m.Session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error && p.Message.Contains("no_such_loadout_x"));
        var problems = Ok(host, "get_item", $$"""{ "ref": "sec:{{B}}#NPCShipArch/ol_build_1" }""")["mission_item"]!["lines"]![2]!
            ["problems"]!.AsArray();
        Assert.Contains("no_such_loadout_x", (string?)Assert.Single(problems)!["message"]);

        // The ship build file is not a script, so get_mission names the tools that read it (final finding 19 of MCP1-3).
        var notScript = Error(host, "get_mission", $$"""{ "script": "msn:{{B}}" }""");
        Assert.Equal(("not_found", MissionErrors.ScriptNotFound), (notScript.Code, notScript.Detail));
        Assert.Equal($"{Ships} is the ship build file of a script, not a mission script. Read a build with get_item and its " +
                     "sec: reference, or read the file with get_raw_ini.", notScript.Message);
    }

    /// <summary>mission_add_item has a preview, and it is in the group Missions, so it refuses while the user hides the
    /// group (decision D725 of the MCP1 design). Pause refuses it too.</summary>
    [Fact]
    public void AddItemHasAPreviewAndFollowsTheGroupMissions()
    {
        var (m, host) = OpenItems();
        using var _ = m;
        var arguments = AddItem("solar", "ol_solar", """{ "archetype": "ol_arch", "system": "ol_system" }""");

        var preview = Ok(host, "mission_add_item", arguments[..^1] + """, "preview": true }""");
        host.Hidden.Add(ToolGroup.Missions);
        var hidden = Error(host, "mission_add_item", arguments);
        host.Hidden.Clear();
        host.TestPaused = true;
        var paused = Error(host, "mission_add_item", arguments);

        Assert.Equal(("previewed", "0"), ((string?)preview["status"], (string?)preview["rev"]));
        Assert.Equal([$"sec:{Script}#MsnSolar/ol_solar"], Texts(preview["adds"]));
        Assert.False(m.Has(Item("MsnSolar", "ol_solar")));
        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Equal(("paused", "Paused"), (paused.Code, paused.Detail));
        Assert.Equal(0, m.Session.Revision);
    }
}
