using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>cutscene_add_entity</c> and <c>cutscene_add_event</c> (section 7.5 of the MCP1 design): the references
/// of the new items, each entity type, the fit of each target, the fields in the same Undo step, the errors and a batch.
/// The tests of <see cref="Calls"/> also cover one entry, the preview and the parity with the UI path.
/// </summary>
public sealed partial class AgentCutsceneToolTests
{
    /// <summary>The adds give the reference of the new entity by its name and of the new event by its index. The select
    /// names the new item, as the editor selects it (decision D249).</summary>
    [Fact]
    public void TheAddsGiveTheReferencesOfTheNewItems()
    {
        var (ws, session, host) = Open();
        using var _ = ws;

        var entity = Ok(host, "cutscene_add_entity",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "MARKER", "name": "my_mark", "position": [1, 2, 3] }""");
        var ev = Ok(host, "cutscene_add_event",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "START_SPATIAL_PROP_ANIM", "time": 2, "targets": ["cam_a", "MY_MARK"] }""");

        Assert.Equal([Script + "#entity/my_mark"], AgentEditToolTests.Texts(entity["adds"]));
        Assert.Equal(Script + "#entity/my_mark", (string?)entity["select"]);
        Assert.Empty(entity["changes"]!.AsArray());
        Assert.Equal([Script + "#event/6"], AgentEditToolTests.Texts(ev["adds"]));
        Assert.Equal(Script + "#event/6", (string?)ev["select"]);
        Assert.Equal("Agent: Add START_SPATIAL_PROP_ANIM", (string?)ev["undo_label"]);
        var added = W(session).Events[6].Table;
        Assert.Equal(ThnTable.FromList(new ThnText("Cam_A"), new ThnText("my_mark")), added.List[2]);
        Assert.Equal(new ThnNumber(ThnTemplates.NewLength), ThnElementPaths.Get(ThnElementKind.Event, added, new ThnPath("props.duration")));
    }

    /// <summary>
    /// Each entity type adds an entity that the checks of the session accept. A DEFORMABLE without a costume and a
    /// COMPOUND with a template and no category apply too, and the result gives the new problem: the save stops on it,
    /// not the tool.
    /// </summary>
    [Theory]
    [InlineData("CAMERA", null, null)]
    [InlineData("MARKER", null, null)]
    [InlineData("DEFORMABLE", "my_costume", null)]
    [InlineData("DEFORMABLE", null, "template_name")]
    [InlineData("COMPOUND", null, null)]
    [InlineData("COMPOUND", "my_prop", "userprops.category")]
    [InlineData("LIGHT", null, null)]
    [InlineData("SOUND", "my_sound", null)]
    [InlineData("PSYS", "my_effect", null)]
    [InlineData("MOTION_PATH", null, null)]
    [InlineData("MONITOR", null, null)]
    [InlineData("SCENE", null, null)]
    public void EachEntityTypeAddsAndGivesOnlyTheProblemsOfMissingData(string type, string? template, string? problem)
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var withTemplate = template == null ? "" : $$""", "template": {{Json(template)}}""";

        var result = Ok(host, "cutscene_add_entity",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "{{type}}", "name": "my_entity"{{withTemplate}} }""");

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal([Script + "#entity/my_entity"], AgentEditToolTests.Texts(result["adds"]));
        var problems = result["new_problems"]!.AsArray();
        if (problem == null)
        {
            Assert.Empty(problems);
            AssertSaved(session.Save());
        }
        else
        {
            Assert.Contains($"entity \"my_entity\", {problem}: The key is missing.", (string?)Assert.Single(problems)!["message"]);
        }
        var entity = W(session).EntityNamed("my_entity")!;
        Assert.Equal(type, ThnSchema.TypeOf(ThnElementKind.Entity, entity.Table, session.ThnSymbols));
    }

    /// <summary>A COMPOUND with a template and its category in the fields gives no problem. A motion path gets its keys
    /// from the fields. The fields are part of the one Undo step, and the result gives only the add.</summary>
    [Fact]
    public void TheFieldsOfANewEntityApplyInTheSameEntry()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);

        var box = Ok(host, "cutscene_add_entity", $$"""
            { "cutscene": {{Json(Script)}}, "type": "COMPOUND", "name": "my_box", "template": "my_prop",
              "fields": { "userprops.category": "Prop" } }
            """);
        var path = Ok(host, "cutscene_add_entity", $$"""
            { "cutscene": {{Json(Script)}}, "type": "MOTION_PATH", "name": "my_path",
              "fields": { "pathprops.path_data": { "closed": false, "keys": [ { "point": [0, 0, 0] }, { "point": [10, 0, 0] } ] } } }
            """);

        Assert.Empty(box["new_problems"]!.AsArray());
        Assert.Empty(box["changes"]!.AsArray());
        Assert.Equal("Agent: Add my_box", (string?)box["undo_label"]);
        Assert.Equal(new ThnText("Prop"), Value(session, Entity(session, "my_box"), "userprops.category"));
        Assert.IsType<ThnText>(Value(session, Entity(session, "my_path"), "pathprops.path_data"));
        Assert.Equal([Script + "#entity/my_path"], AgentEditToolTests.Texts(path["adds"]));
        Assert.Equal(2, session.Revision);

        session.Undo();
        session.Undo();
        Assert.Equal(before, State(session));
    }

    /// <summary>The orientation of a new entity is the rotation that set_fields writes for the same Euler degrees. So a
    /// set_fields with the same angles changes nothing.</summary>
    [Fact]
    public void TheOrientationOfANewEntityIsTheRotationOfSetFields()
    {
        var (ws, _, host) = Open();
        using var _ = ws;

        Ok(host, "cutscene_add_entity", $$"""
            { "cutscene": {{Json(Script)}}, "type": "CAMERA", "name": "my_cam_2", "position": [30, 10, 0], "orientation": [10, 90, 0] }
            """);
        var same = Ok(host, "set_fields", $$"""
            { "ref": {{Json(Script + "#entity/my_cam_2")}}, "fields": { "spatialprops.orient": [10, 90, 0], "spatialprops.pos": [30, 10, 0] } }
            """);

        Assert.Equal("no_change", (string?)same["status"]);
    }

    /// <summary>A length and the fields of a new event apply in the same entry. A length rounds to the 1 ms grid. A
    /// field of the time goes through the move of the Inspector.</summary>
    [Fact]
    public void TheLengthAndTheFieldsOfANewEventApply()
    {
        var (ws, session, host) = Open();
        using var _ = ws;

        var result = Ok(host, "cutscene_add_event", $$"""
            { "cutscene": {{Json(Script)}}, "type": "START_SPATIAL_PROP_ANIM", "time": 0, "targets": ["Cam_A"], "length": 4.00049,
              "fields": { "props.spatialprops.pos": [0, 10, -50] } }
            """);

        var ev = Event(session, 6);
        Assert.Equal([Script + "#event/6"], AgentEditToolTests.Texts(result["adds"]));
        Assert.Empty(result["changes"]!.AsArray());
        Assert.Equal(new ThnNumber(4), Value(session, ev, "props.duration"));
        Assert.Equal(ThnPose.Vector(new System.Numerics.Vector3(0, 10, -50)), Value(session, ev, "props.spatialprops.pos"));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>Each error of the two tools, with its code, its detail and its argument. Nothing changes.</summary>
    [Theory]
    [InlineData("cutscene_add_entity", """{ "type": "ROBOT", "name": "r" }""", "invalid_value", MissionErrors.FieldValue, "type")]
    [InlineData("cutscene_add_entity", """{ "type": "MARKER", "name": "  " }""", "refused", MissionErrors.NameInvalid, "name")]
    [InlineData("cutscene_add_entity", """{ "type": "MARKER", "name": "cam_a" }""", "name_taken", MissionErrors.NameTaken, "name")]
    [InlineData("cutscene_add_entity", """{ "type": "MONITOR", "name": "m2", "position": [0, 0, 0] }""", "invalid_argument", MissionErrors.NoPlace, "position")]
    [InlineData("cutscene_add_entity", """{ "type": "SCENE", "name": "s", "orientation": [0, 90, 0] }""", "invalid_argument", MissionErrors.NoPlace, "orientation")]
    [InlineData("cutscene_add_entity", """{ "type": "MARKER", "name": "m", "position": [0, 0, 1e39] }""", "invalid_argument", ToolInput.BadArgument, "position")]
    [InlineData("cutscene_add_entity", """{ "type": "MARKER", "name": "m", "fields": { "spatialprops.pos": "x" } }""", "invalid_value", MissionErrors.FieldValue, "fields.spatialprops.pos")]
    [InlineData("cutscene_add_entity", """{ "type": "MARKER", "name": "m", "fields": { "entity_name": "n" } }""", "refused", EditTools.CutsceneItem, "fields.entity_name")]
    [InlineData("cutscene_add_entity", """{ "type": "MARKER", "name": "m", "fields": { "spatialprops": { "pos": [1, 1, 1] } } }""", "invalid_argument", ItemRefs.BadReference, "fields.spatialprops")]
    [InlineData("cutscene_add_event", """{ "type": "START_DANCE", "time": 1, "targets": ["Fx"] }""", "invalid_value", MissionErrors.FieldValue, "type")]
    [InlineData("cutscene_add_event", """{ "type": "START_PSYS", "time": -1, "targets": ["Fx"] }""", "invalid_value", MissionErrors.FieldValue, "time")]
    [InlineData("cutscene_add_event", """{ "type": "START_PSYS", "time": 1e39, "targets": ["Fx"] }""", "invalid_value", MissionErrors.FieldValue, "time")]
    [InlineData("cutscene_add_event", """{ "type": "START_PSYS", "time": 1, "targets": ["Fx"], "length": -1 }""", "invalid_value", MissionErrors.FieldValue, "length")]
    [InlineData("cutscene_add_event", """{ "type": "SET_CAMERA", "time": 1, "targets": ["Monitor", "Cam_A"], "length": 1 }""", "invalid_value", MissionErrors.FieldValue, "length")]
    [InlineData("cutscene_add_event", """{ "type": "START_MOTION", "time": 1, "targets": ["Cam_A"] }""", "refused", MissionErrors.TargetType, "targets")]
    [InlineData("cutscene_add_event", """{ "type": "SET_CAMERA", "time": 1, "targets": ["Monitor"] }""", "invalid_argument", MissionErrors.TargetCount, "targets")]
    [InlineData("cutscene_add_event", """{ "type": "START_PSYS", "time": 1, "targets": ["Fx", "Cam_A"] }""", "invalid_argument", MissionErrors.TargetCount, "targets")]
    [InlineData("cutscene_add_event", """{ "type": "START_PSYS", "time": 1, "targets": ["Fy"] }""", "not_found", MissionErrors.ItemNotFound, "targets")]
    [InlineData("cutscene_add_event", """{ "type": "START_PSYS", "time": 1, "targets": ["Fx"], "fields": { "props.duration": "x" } }""", "invalid_value", MissionErrors.FieldValue, "fields.props.duration")]
    [InlineData("cutscene_add_event", """{ "type": "START_PSYS", "time": 1, "targets": ["Fx"], "fields": { "targets": ["Cam_A"] } }""", "refused", MissionErrors.TargetType, "fields.targets")]
    public void EachErrorOfTheAddsHasItsCodeAndChangesNothing(string tool, string arguments, string code, string detail,
        string field)
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);

        var result = Call(host, tool, $$"""{ "cutscene": {{Json(Script)}}, """ + arguments[2..]);

        Assert.True(result.IsError, result.Text);
        Assert.Equal((code, detail, field),
            ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]));
        Assert.Equal(before, State(session));
        Assert.Equal(0, session.Revision);
        Assert.False(session.CanUndo);
    }

    /// <summary>The adds take only a reference of a script, and a path of a script that is not an install path gives
    /// <c>invalid_argument</c> (section 8.2, item 4 of the MCP1 design).</summary>
    [Theory]
    [InlineData("cutscene_add_entity", """{ "cutscene": "thn:DATA\\SCRIPTS\\x.thn#entity/Fx", "type": "MARKER", "name": "m" }""", ItemRefs.BadReference)]
    [InlineData("cutscene_add_entity", """{ "cutscene": "thn:C:\\x.thn", "type": "MARKER", "name": "m" }""", "NotInstallPath")]
    [InlineData("cutscene_add_event", """{ "cutscene": "thn:DATA\\SCRIPTS\\x.thn#event/0", "type": "START_PSYS", "time": 1, "targets": ["Fx"] }""", ItemRefs.BadReference)]
    [InlineData("cutscene_add_event", """{ "cutscene": "thn:..\\x.thn", "type": "START_PSYS", "time": 1, "targets": ["Fx"] }""", "NotInstallPath")]
    public void TheAddsTakeOnlyAScriptAtAnInstallPath(string tool, string arguments, string detail)
    {
        var (ws, session, host) = Open();
        using var _ = ws;

        var error = Error(host, tool, arguments);

        Assert.Equal(("invalid_argument", detail), (error.Code, error.Detail));
        Assert.Equal(0, session.Revision);
    }

    /// <summary>The messages give the reason in the words of the editor: the types that fit a target, the closest
    /// names of an unknown target, the event types, and why a second SCENE refuses.</summary>
    [Fact]
    public void TheErrorsOfTheAddsGiveTheReasons()
    {
        var (ws, _, host) = Open();
        using var __ = ws;
        Ok(host, "cutscene_create", """{ "name": "my_intro" }""");

        var motion = Error(host, "cutscene_add_event",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "START_MOTION", "time": 1, "targets": ["Cam_A"] }""");
        var unknown = Error(host, "cutscene_add_event",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "START_PSYS", "time": 1, "targets": ["Fy"] }""");
        var type = Error(host, "cutscene_add_event",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "START_DANCE", "time": 1, "targets": ["Fx"] }""");
        var cut = Error(host, "cutscene_add_event",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "SET_CAMERA", "time": 1, "targets": ["Monitor"] }""");
        var scene = Call(host, "cutscene_add_entity",
            $$"""{ "cutscene": {{Json("thn:" + NewPath)}}, "type": "SCENE", "name": "Scene_2" }""").Content;

        Assert.Equal("START_MOTION needs a DEFORMABLE or COMPOUND as target 1. The entity Cam_A is a CAMERA.", motion.Message);
        Assert.Equal("No entity of this script has the name Fy. The closest names are: Fx.", unknown.Message);
        Assert.StartsWith("START_DANCE is not an event type that the editor can add. The types are SET_CAMERA, START_MOTION,",
            type.Message);
        Assert.Equal("SET_CAMERA needs two targets.", cut.Message);
        Assert.Equal(("refused", MissionErrors.OneScene, "type", Studio.Commands.CutsceneCommands.OneScene),
            ((string?)scene["code"], (string?)scene["detail"], (string?)scene["field"], (string?)scene["message"]));
    }

    /// <summary>A batch that adds a character and an event that names it is one entry. One undo puts back W.</summary>
    [Fact]
    public void ABatchThatAddsAnEntityAndAnEventThatNamesItIsOneEntry()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);

        var result = Ok(host, "edit_batch", $$"""
            { "label": "Add the wave", "steps": [
                { "tool": "cutscene_add_entity", "arguments": { "cutscene": {{Json(Script)}}, "type": "DEFORMABLE", "name": "my_char", "template": "my_costume" } },
                { "tool": "cutscene_add_event", "arguments": { "cutscene": {{Json(Script)}}, "type": "START_MOTION", "time": 1, "targets": ["my_char"], "length": 2,
                  "fields": { "props.animation": "Sc_wave" } } }
            ] }
            """);

        Assert.Equal("Agent: Add the wave", (string?)result["undo_label"]);
        Assert.Equal([Script + "#entity/my_char", Script + "#event/6"], AgentEditToolTests.Texts(result["adds"]));
        Assert.Equal(Script + "#event/6", (string?)result["select"]);
        Assert.Empty(result["new_problems"]!.AsArray());
        Assert.Equal(1, session.Revision);
        Assert.Equal(new ThnText("Sc_wave"), Value(session, Event(session, 6), "props.animation"));

        session.Undo();
        Assert.Equal(before, State(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// The steps 1 to 4 of workflow 4 of section 9.5 of the MCP1 design through the tools, without the editor: a new
    /// cutscene of 8 s, a second camera, a camera move with its end position, and a cut to the second camera. The guards
    /// of the new event match. The check finds no problem, and the save writes the script into the folder of the mod.
    /// </summary>
    [Fact]
    public void TheStepsOfWorkflowFourMakeAScriptThatSaves()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var script = Json("thn:" + NewPath);

        Ok(host, "cutscene_create", """{ "name": "my_intro", "duration": 8 }""");
        var camera = Ok(host, "cutscene_add_entity",
            $$"""{ "cutscene": {{script}}, "type": "CAMERA", "name": "my_cam_2", "position": [30, 10, 0] }""");
        var move = Ok(host, "cutscene_add_event",
            $$"""{ "cutscene": {{script}}, "type": "START_SPATIAL_PROP_ANIM", "time": 0, "targets": ["Camera_01"], "length": 4 }""");
        var moved = (string)move["adds"]!.AsArray()[0]!;
        var end = Ok(host, "set_fields", $$"""
            { "ref": {{Json(moved)}}, "expect_type": "START_SPATIAL_PROP_ANIM", "expect_time": 0,
              "fields": { "props.spatialprops.pos": [0, 5, -20] } }
            """);
        var cut = Ok(host, "cutscene_add_event",
            $$"""{ "cutscene": {{script}}, "type": "SET_CAMERA", "time": 4, "targets": ["Monitor", "my_cam_2"] }""");
        var check = Ok(host, "check", $$"""{ "scope": "cutscene", "ref": {{script}} }""");
        AssertSaved(session.Save());

        Assert.Equal(["thn:" + NewPath + "#entity/my_cam_2"], AgentEditToolTests.Texts(camera["adds"]));
        Assert.Equal("thn:" + NewPath + "#event/1", moved);
        Assert.Equal([moved], AgentEditToolTests.Texts(end["changes"]));
        Assert.Equal(["thn:" + NewPath + "#event/2"], AgentEditToolTests.Texts(cut["adds"]));
        Assert.Empty(check["problems"]!.AsArray());
        var saved = ThnScriptModel.From(Evaluate(File.ReadAllText(Path.Combine(ws.ModFolder("act"), "files", NewPath))),
            ThnElementId.Counter());
        Assert.Equal(8, saved.Duration);
        Assert.Equal(["Scene", "Monitor", "Camera_01", "my_cam_2"], saved.Entities.Select(e => ThnScriptModel.NameOf(e.Table)));
        Assert.Equal(["SET_CAMERA", "START_SPATIAL_PROP_ANIM", "SET_CAMERA"],
            saved.Events.Select(e => ThnSchema.TypeOf(ThnElementKind.Event, e.Table, session.ThnSymbols)));
    }

    private static ThnTable Evaluate(string text) => ThnScripts.Evaluate(text);

    /// <summary>A hidden group Cutscenes gives not_available for the adds too (decision D725).</summary>
    [Fact]
    public void TheAddsGiveNotAvailableForTheHiddenGroup()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        host.Hidden.Add(ToolGroup.Cutscenes);

        var entity = Error(host, "cutscene_add_entity", $$"""{ "cutscene": {{Json(Script)}}, "type": "MARKER", "name": "m" }""");
        var ev = Error(host, "cutscene_add_event",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "START_PSYS", "time": 1, "targets": ["Fx"] }""");

        Assert.Equal(("not_available", "GroupHidden"), (entity.Code, entity.Detail));
        Assert.Equal(("not_available", "GroupHidden"), (ev.Code, ev.Detail));
        Assert.Equal(0, session.Revision);
    }
}
