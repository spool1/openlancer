using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>mission_place_prefab</c> and <c>mission_start_plan</c> (section 7.4 of the MCP1 design, step 6 of
/// MCP1-3). A place is one Undo step with the edit origin Agent and runs in strict mode. The value of each field has the
/// form of its field kind, with named values (UI rule 3). A start plan that cannot start is data, not a tool error. All
/// names are invented.
/// </summary>
public sealed partial class AgentMissionToolTests
{
    /// <summary>A script in the story slot M02, so a plan of it can start.</summary>
    private const string Story = @"DATA\MISSIONS\M02\m02.ini";

    private static readonly string StoryJson = Story.Replace(@"\", @"\\");

    /// <summary>A prefab of the active mod with each kind of field: a condition, a value, a trigger list, a list with two
    /// columns and lines of one key. Its output starts triggers after the end trigger.</summary>
    private const string Relay = PrefabTemplate.Header + """

        [Prefab]
        id = ol_relay
        name = Relay
        kind = beat
        format = 1

        [Field]
        id = when
        label = Starts when
        kind = condition
        default = Cnd_True = no_params

        [Field]
        id = delay
        label = Wait
        kind = number
        default = 5

        [Field]
        id = targets
        label = Starts
        kind = triggers

        [Field]
        id = marks
        label = Marked ships
        kind = list
        column = ship, object, Ship
        column = mark, wholeNumber, Mark

        [Field]
        id = sides
        label = Sides
        kind = lines
        key = Act_SetVibe

        [Port]
        id = done
        label = When it ends
        role = end

        [Trigger]
        role = start
        nickname = {instance}_start
        {when} =
        Act_MarkObj = {marks.ship}, {marks.mark}
        {sides} =
        Act_ActTrig = {targets}
        Act_ActTrig = {instance}_end

        [Trigger]
        role = end
        nickname = {instance}_end
        Cnd_Timer = {delay}
        Act_ActTrig = {port:done}

        """;

    /// <summary>The test script with the prefabs ol_gate and ol_relay in the active mod and a script in a story
    /// slot.</summary>
    private static (MissionEdits M, TestAgentHost Host) OpenPrefabs()
    {
        var m = Create(setup: ws => ws.WriteBase(Story, StartPlanTests.Script), modFiles: new Dictionary<string, string>
        {
            [@"prefabs\missions\ol_gate.ini"] = PrefabInstanceTests.Gate,
            [@"prefabs\missions\ol_relay.ini"] = Relay,
        });
        return (m, new TestAgentHost(m.Session));
    }

    private static string Place(string prefab, string? id, string fields = "{}", string? outputs = null) =>
        $$"""
        { "script": "msn:{{S}}", "prefab": "{{prefab}}"{{(id == null ? "" : $", \"id\": \"{id}\"")}}, "fields": {{fields}}{{(outputs == null ? "" : $", \"outputs\": {outputs}")}} }
        """;

    private static string Plan(string script, string? start = null, string? more = null) =>
        $$"""{ "script": "msn:{{script.Replace(@"\", @"\\")}}"{{(start == null ? "" : $", \"start\": \"{start}\"")}}{{more ?? ""}} }""";

    private static (string? Code, string? Detail, string? Field) Refusal(ToolResult result) =>
        ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]);

    /// <summary>A built-in prefab places one linked instance with its member triggers in one entry with the label of
    /// the drop on the graph. The result gives the inst: reference and the trg: reference of each member. One undo
    /// removes the members and the record of the graph file, and the version of the script grows.</summary>
    [Fact]
    public void APrefabPlacesOneInstanceWithItsMembersInOneEntry()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;
        var before = m.State();
        var graph = m.Session.ModFileText(GraphFile.PathOf(Script));

        var result = Ok(host, "mission_place_prefab", Place("prefab:builtin:spawnWave", "ol_w1", """{ "ships": "ol_form" }"""));

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal([$"trg:{Script}#ol_w1_spawn", $"trg:{Script}#ol_w1_end", $"inst:{Script}#ol_w1"], Texts(result["adds"]));
        Assert.Equal((EditOrigin.Agent, "Agent: Place prefab Spawn wave"), (m.Session.UndoOrigin, m.Session.UndoLabel));
        Assert.Equal(2, (long?)result["versions"]![Script]);
        Assert.Contains("\"remove_prefab_instance\"", result["inverse"]!.ToJsonString());
        Assert.Equal(["nickname = ol_w1_spawn", "Cnd_True = no_params", "Act_SpawnFormation = ol_form", "Act_ActTrig = ol_w1_end"],
            m.Lines("ol_w1_spawn"));
        var record = m.Missions.Graph(Script).Instance("ol_w1")!;
        Assert.Equal("builtin:spawnWave", record.Prefab);
        Assert.Equal([new InstanceValue("ships", ["ol_form"])], record.Values);
        Assert.Single(host.Edits);

        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.Null(m.Missions.Graph(Script).Instance("ol_w1"));
        Assert.Equal(graph, m.Session.ModFileText(GraphFile.PathOf(Script)));
        Assert.Equal(3, m.Missions.Version(Script));
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>The inverse of a place removes the instance and its record after a later edit of another trigger
    /// (section 4.8 of the M6b design). The later edit stays.</summary>
    [Fact]
    public void TheInverseOfAPlaceRemovesTheInstanceAfterALaterEdit()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;
        var place = Ok(host, "mission_place_prefab", Place("prefab:mod:ol_gate", "ol_w1", """{ "formation": "ol_form" }"""));
        Ok(host, "mission_edit", Edit(Wait9));

        var inverse = Ok(host, "mission_edit", Edit(place["inverse"]!.ToJsonString()));

        Assert.Equal("applied", (string?)inverse["status"]);
        Assert.False(m.Has("ol_w1_spawn") || m.Has("ol_w1_end"));
        Assert.Null(m.Missions.Graph(Script).Instance("ol_w1"));
        Assert.Contains("Cnd_Timer = 9", m.Lines("ol_wave"));
    }

    /// <summary>Without an id, the editor makes a free id, as a drop of a prefab on the graph does.</summary>
    [Fact]
    public void APrefabWithoutAnIdGetsAFreeId()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;

        var first = Ok(host, "mission_place_prefab", Place("prefab:mod:ol_gate", null, """{ "formation": "ol_form" }"""));
        var second = Ok(host, "mission_place_prefab", Place("prefab:mod:ol_gate", null, """{ "formation": "ol_form" }"""));

        var ids = new[] { first, second }
            .Select(r => InstanceRef.Parse(Texts(r["adds"]).Single(a => a.StartsWith("inst:", System.StringComparison.Ordinal))).Id)
            .ToList();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotNull(m.Missions.Graph(Script).Instance(id)));
    }

    /// <summary>Each field kind takes its own form with named values. A condition is a line, a value is one value, a
    /// trigger list is a list of nicknames or trg: references of the script, a list is rows of values by column id, and
    /// lines are a list of lines. An output takes triggers of the script. The record keeps each value in the text form of
    /// the rows, as the Inspector writes it. A field without a value takes the default.</summary>
    [Fact]
    public void EachFieldKindTakesItsFormWithNamedValues()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;

        var result = Ok(host, "mission_place_prefab", Place("prefab:mod:ol_relay", "ol_r1", $$"""
            {
              "when": { "key": "Cnd_Timer", "values": { "seconds": 2 } },
              "delay": 8,
              "targets": [ "ol_end", "trg:{{S}}#ol_wave" ],
              "marks": [ { "ship": "ol_ship_1", "mark": 1 }, { "mark": 0, "ship": "ol_ship_3" } ],
              "sides": [ { "key": "Act_SetVibe", "values": { "from": "ol_ship_1", "to": "ol_ship_3", "attitude": -0.5 } } ]
            }
            """, """{ "done": [ "ol_start" ] }"""));

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal(
        [
            "nickname = ol_r1_start", "Cnd_Timer = 2", "Act_MarkObj = ol_ship_1, 1", "Act_MarkObj = ol_ship_3, 0",
            "Act_SetVibe = ol_ship_1, ol_ship_3, -0.5", "Act_ActTrig = ol_end", "Act_ActTrig = ol_wave", "Act_ActTrig = ol_r1_end",
        ], m.Lines("ol_r1_start"));
        Assert.Equal(["nickname = ol_r1_end", "Cnd_Timer = 8", "Act_ActTrig = ol_start"], m.Lines("ol_r1_end"));
        var record = m.Missions.Graph(Script).Instance("ol_r1")!;
        Assert.Equal(
        [
            new InstanceValue("when", ["Cnd_Timer = 2"]), new InstanceValue("delay", ["8"]),
            new InstanceValue("targets", ["ol_end", "ol_wave"]), new InstanceValue("marks", ["ol_ship_1, 1", "ol_ship_3, 0"]),
            new InstanceValue("sides", ["Act_SetVibe = ol_ship_1, ol_ship_3, -0.5"]),
        ], record.Overrides);
        Assert.Equal([new InstancePort("done", "ol_start")], record.Ports);

        // A condition without values gets no_params, so it is the default of the prefab and no override.
        var plain = Ok(host, "mission_place_prefab", Place("prefab:mod:ol_relay", "ol_r2", """{ "when": { "key": "Cnd_True" } }"""));
        Assert.Equal("applied", (string?)plain["status"]);
        Assert.Empty(m.Missions.Graph(Script).Instance("ol_r2")!.Overrides);
        Assert.Equal(["nickname = ol_r2_start", "Cnd_True = no_params", "Act_ActTrig = ol_r2_end"], m.Lines("ol_r2_start"));
        Assert.Equal(["nickname = ol_r2_end", "Cnd_Timer = 5"], m.Lines("ol_r2_end"));
    }

    /// <summary>A position takes a list of numbers, and a list row of a position column too. The built-in prefab
    /// patrol has a list of points.</summary>
    [Fact]
    public void APositionIsAListOfNumbers()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;

        Ok(host, "mission_place_prefab", Place("prefab:builtin:patrol", "ol_p1",
            """{ "waypoints": [ { "point": [0, 0, 500] }, { "point": [1000, 0, -250] } ] }"""));

        Assert.Equal(["nickname = ol_p1_orders", "GotoVec = goto_cruise, 0, 0, 500, 100", "GotoVec = goto_cruise, 1000, 0, -250, 100"],
            m.Lines(Item("ObjList", "ol_p1_orders")));
    }

    /// <summary>The refusals of mission_place_prefab. A prefab that does not exist gives not_found with the closest
    /// names. An id in use gives name_taken. A value that the field refuses gives invalid_value. A value in the wrong form,
    /// a field, a column or an output that the prefab does not have, and a trigger of another script give
    /// invalid_argument. Each error names its argument, and nothing changes.</summary>
    [Fact]
    public void PlacePrefabGivesItsRefusals()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;
        Ok(host, "mission_place_prefab", Place("prefab:mod:ol_gate", "ol_w1", """{ "formation": "ol_form" }"""));
        var before = m.State();
        var revision = m.Session.Revision;
        const string relay = "prefab:mod:ol_relay";

        var refusals = new (string Arguments, string Code, string Detail, string? Field)[]
        {
            (Place("prefab:mod:ol_none", null), "not_found", MissionErrors.PrefabNotFound, "prefab"),
            (Place("ol_gate", null), "invalid_argument", ItemRefs.BadReference, "prefab"),
            (Place("prefab:mod:ol_gate", "ol_w1", """{ "formation": "ol_form" }"""), "name_taken", MissionErrors.NameTaken, "id"),
            (Place("prefab:mod:ol_gate", "ol w2"), "invalid_argument", ToolInput.BadArgument, "id"),
            (Place(relay, null, """{ "delay": "soon" }"""), "invalid_value", MissionErrors.FieldValue, "fields.delay"),
            (Place(relay, null, """{ "when": null }"""), "invalid_value", MissionErrors.FieldValue, "fields.when"),
            (Place(relay, null, """{ "delay": { "seconds": 5 } }"""), "invalid_argument", ToolInput.BadArgument, "fields.delay"),
            (Place(relay, null, """{ "when": "Cnd_True" }"""), "invalid_argument", ToolInput.BadArgument, "fields.when"),
            (Place(relay, null, """{ "targets": "ol_end" }"""), "invalid_argument", ToolInput.BadArgument, "fields.targets"),
            (Place(relay, null, """{ "marks": [ "ol_ship_1, 1" ] }"""), "invalid_argument", ToolInput.BadArgument, "fields.marks"),
            (Place(relay, null, """{ "sides": { "key": "Act_SetVibe" } }"""), "invalid_argument", ToolInput.BadArgument,
                "fields.sides"),
            (Place(relay, null, """{ "speed": 5 }"""), "invalid_argument", MissionErrors.FieldNotFound, "fields.speed"),
            (Place(relay, null, """{ "marks": [ { "ship": "ol_ship_1", "mark": 1, "color": "red" } ] }"""), "invalid_argument",
                ToolInput.BadArgument, "fields.marks[0].color"),
            (Place(relay, null, """{ "marks": [ { "ship": "ol_ship_1" } ] }"""), "invalid_argument", ToolInput.BadArgument,
                "fields.marks[0].mark"),
            (Place(relay, null, """{ "marks": [ { "ship": "ol_ship_1", "mark": 1.5 } ] }"""), "invalid_value",
                MissionErrors.FieldValue, "fields.marks"),
            (Place(relay, null, """{ "sides": [ { "key": "Act_ChangeState", "values": { "state": "FAIL" } } ] }"""),
                "invalid_value", MissionErrors.FieldValue, "fields.sides"),
            (Place(relay, null, """{ "when": { "key": "Act_ChangeState", "values": { "state": "FAIL" } } }"""), "invalid_value",
                MissionErrors.WrongPlace, "fields.when"),
            (Place(relay, null, """{ "sides": [ { "key": "Act_SetVibe", "values": { "side": "ol_ship_1" } } ] }"""),
                "invalid_argument", MissionErrors.NoSuchValue, "fields.sides[0]"),
            (Place(relay, null, """{ "targets": [ "ol_end", "trg:DATA\\MISSIONS\\M02\\m02.ini#t_kill" ] }"""), "invalid_argument",
                MissionEditTools.OtherItem, "fields.targets[1]"),
            (Place(relay, null, """{ "marks": [ { "ship": "ol_ship_1, ol_ship_3", "mark": 1 } ] }"""), "invalid_value",
                MissionErrors.PatchText, "fields.marks[0]"),
            (Place(relay, null, "{}", """{ "later": [ "ol_end" ] }"""), "invalid_argument", MissionErrors.PortNotFound,
                "outputs.later"),
            (Place(relay, null, "{}", """{ "done": [ "trg:DATA\\MISSIONS\\M02\\m02.ini#t_kill" ] }"""), "invalid_argument",
                MissionEditTools.OtherItem, "outputs.done[0]"),
            // The strict check of the commit gives the new errors of the whole call, so the error names no argument.
            (Place(relay, null, "{}", """{ "done": [ "ol_nowhere" ] }"""), "new_errors", MissionErrors.NewErrors, null),
            (Place("prefab:builtin:patrol", null, """{ "waypoints": [ { "point": [0, 0, 0] }, { "point": [0, 0, 0.5] } ] }"""),
                "invalid_value", MissionErrors.TemplateLine, "fields"),
        };

        var wrong = refusals.Select(r => (r, Got: Refusal(Call(host, "mission_place_prefab", r.Arguments))))
            .Where(x => x.Got != (x.r.Code, x.r.Detail, x.r.Field))
            .Select(x => $"{x.r.Arguments}: {x.Got}").ToList();

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Contains("ol_gate", Error(host, "mission_place_prefab", Place("prefab:mod:ol_gat", null)).Message);
        Assert.Equal(before, m.State());
        Assert.Equal(revision, m.Session.Revision);
    }

    /// <summary>A preview gives the adds of the real call and changes nothing. The tool can be a step of edit_batch, and
    /// the strict check runs at the end of the batch, so the output of the instance can start a trigger of a later
    /// step. The batch is one entry, and one undo removes both.</summary>
    [Fact]
    public void APlaceHasAPreviewAndCanBeAStepOfABatch()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;
        var before = m.State();
        var place = Place("prefab:mod:ol_gate", "ol_w1", """{ "formation": "ol_form" }""", """{ "done": [ "ol_after" ] }""");

        var preview = Ok(host, "mission_place_prefab",
            Place("prefab:mod:ol_gate", "ol_w1", """{ "formation": "ol_form" }""").TrimEnd()[..^1] + """, "preview": true }""");
        var batch = Ok(host, "edit_batch", $$"""
            { "label": "Gate and after",
              "steps": [ { "tool": "mission_place_prefab", "arguments": {{place}} },
                         { "tool": "mission_add_trigger",
                           "arguments": { "script": "msn:{{S}}", "nickname": "ol_after", "condition": { "key": "Cnd_True" } } } ] }
            """);

        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Equal([$"trg:{Script}#ol_w1_spawn", $"trg:{Script}#ol_w1_end", $"inst:{Script}#ol_w1"], Texts(preview["adds"]));
        Assert.Equal("applied", (string?)batch["status"]);
        Assert.Equal(Texts(preview["adds"]), Texts(batch["steps"]![0]!["adds"]));
        Assert.Equal("Agent: Gate and after", m.Session.UndoLabel);
        Assert.Contains("Act_ActTrig = ol_after", m.Lines("ol_w1_end"));

        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>get_item of a prefab gives what mission_place_prefab takes: the kind of each field, the columns of a
    /// list field, the keys of a lines field and the optional flag of a value field.</summary>
    [Fact]
    public void GetItemOfAPrefabGivesTheFormsOfItsFields()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;

        var conversation = Ok(host, "get_item", """{ "ref": "prefab:builtin:conversation" }""")["prefab"]!;
        var wave = Ok(host, "get_item", """{ "ref": "prefab:builtin:spawnWave" }""")["prefab"]!;

        JsonNode Field(JsonNode prefab, string id) => prefab["fields"]!.AsArray().Single(f => (string?)f!["id"] == id)!;
        Assert.Equal(["speaker:object", "listener:object", "line:commLine"], Field(conversation, "lines")["columns"]!.AsArray()
            .Select(c => $"{(string?)c!["id"]}:{(string?)c["kind"]}"));
        Assert.Equal(["Act_SetVibe", "Act_SetVibeLbl", "Act_SetVibeLblToShip", "Act_SetVibeShipToLbl"],
            Texts(Field(wave, "sides")["keys"]));
        Assert.True((bool?)Field(wave, "orders")["optional"]);
        Assert.True(Field(wave, "ships")["columns"] is null or JsonArray { Count: 0 });
    }

    /// <summary>A start plan of a script in a story slot gives the status ready, with the kind and the path of the
    /// start. A plan of the start of the mission is exact. A plan that cannot start is data: the test script has no story
    /// slot, so its plan gives the status refused and the reason. The query changes nothing.</summary>
    [Fact]
    public void AStartPlanGivesItsStatusAsData()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;
        var version = m.Missions.Version(Story);

        var kill = Ok(host, "mission_start_plan", Plan(Story, $"trg:{StoryJson}#t_kill"))["plan"]!;
        var start = Ok(host, "mission_start_plan", Plan(Story))["plan"]!;
        var refused = Ok(host, "mission_start_plan", Plan(Script, $"trg:{S}#ol_wave"))["plan"]!;
        var dock = Ok(host, "mission_start_plan", Plan(Story, $"trg:{StoryJson}#t_kill", """, "start_place": "base:ol_base_a" """))["plan"]!;

        Assert.Equal(("ready", "approximate", "M02", $"msn:{Story}"),
            ((string?)kill["status"], (string?)kill["kind"], (string?)kill["slot"], (string?)kill["script"]));
        Assert.Equal($"trg:{Story}#t_kill", (string?)kill["start"]);
        Assert.Equal("openlancer", (string?)kill["target"]);
        Assert.Equal($"trg:{Story}#t_kill", Texts(kill["path"])[^1]);
        Assert.NotNull(kill["place"]);
        Assert.Equal(("ready", "mission_start", "mission_start"),
            ((string?)start["status"], (string?)start["kind"], (string?)start["start"]));
        Assert.Equal(("refused", "test_script", StartPlan.NoSlotReason),
            ((string?)refused["status"], (string?)refused["kind"], (string?)refused["reason"]));
        Assert.Equal("base", (string?)dock["place"]!["kind"]);
        Assert.Equal((0L, version), (m.Session.Revision, m.Missions.Version(Story)));
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>The refusals of mission_start_plan: a trigger of another script, a start place that is not valid and a
    /// retail target give invalid_argument. A retail target waits for the linked game (M6c-3). A trigger that the script
    /// does not have gives not_found. Each error names its argument.</summary>
    [Fact]
    public void StartPlanGivesItsRefusals()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;

        var refusals = new (string Arguments, string Code, string Field)[]
        {
            (Plan(Script, $"trg:{StoryJson}#t_kill"), "invalid_argument", "start"),
            (Plan(Script, "ol_wave"), "invalid_argument", "start"),
            (Plan(Script, null, """, "target": "vanilla-1.1" """), "invalid_argument", "target"),
            (Plan(Script, null, """, "start_place": "nowhere" """), "invalid_argument", "start_place"),
            (Plan(Script, $"trg:{S}#ol_none"), "not_found", "start"),
            (Plan(@"DATA\MISSIONS\OL_NONE\ol_none.ini"), "not_found", "script"),
            ("""{ "script": "ol_test.ini" }""", "invalid_argument", "script"),
        };

        var wrong = refusals.Select(r => (r, Got: Refusal(Call(host, "mission_start_plan", r.Arguments))))
            .Where(x => (x.Got.Code, x.Got.Field) != (x.r.Code, x.r.Field))
            .Select(x => $"{x.r.Arguments}: {x.Got}").ToList();

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>mission_place_prefab and mission_start_plan are in the group Missions, so they refuse while the user hides
    /// the group (decision D725). Pause refuses the place. The plan changes nothing, so it runs in Pause.</summary>
    [Fact]
    public void AHiddenGroupRefusesBothToolsAndPauseRefusesOnlyThePlace()
    {
        var (m, host) = OpenPrefabs();
        using var _ = m;
        var place = Place("prefab:mod:ol_gate", "ol_w1", """{ "formation": "ol_form" }""");

        host.Hidden.Add(ToolGroup.Missions);
        var hidden = new[] { Error(host, "mission_place_prefab", place), Error(host, "mission_start_plan", Plan(Story)) };
        host.Hidden.Clear();
        host.TestPaused = true;
        var paused = Error(host, "mission_place_prefab", place);
        var plan = Ok(host, "mission_start_plan", Plan(Story));

        Assert.All(hidden, e => Assert.Equal(("not_available", "GroupHidden"), (e.Code, e.Detail)));
        Assert.Equal(("paused", "Paused"), (paused.Code, paused.Detail));
        Assert.Equal("ready", (string?)plan["plan"]!["status"]);
        Assert.Equal(0, m.Session.Revision);
    }
}
