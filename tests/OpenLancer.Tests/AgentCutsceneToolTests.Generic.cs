using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The generic edit tools on cutscene items (section 7.2 of the MCP1 design, step 4 of MCP1-4): <c>set_fields</c> with
/// THN paths, <c>rename_item</c>, <c>delete_items</c> and <c>duplicate_items</c>. The tests cover the value form of
/// each kind of key, the parity with the Inspector, the round trip of <c>get_cutscene</c>, the event guards (decision
/// D767), the questions of the editor and the save of a patch.
/// </summary>
public sealed partial class AgentCutsceneToolTests
{
    /// <summary>A script with a key of each kind of the THN schema: a light, a motion path, a turn, a quaternion, the
    /// bits of a motion, a curve, and a key that the schema does not know.</summary>
    private const string Kinds = """
        duration = 10
        entities = {
            { entity_name = "Cam", type = CAMERA, cameraprops = { fovh = 10, hvaspect = 1.5, nearplane = 1, farplane = 100 }, spatialprops = { pos = { 0, 0, 0 }, orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } } },
            { entity_name = "Cam2", type = CAMERA, flags = REFERENCE },
            { entity_name = "Monitor", type = MONITOR },
            { entity_name = "Lamp", type = LIGHT, lightprops = { type = L_POINT, on = Y } },
            { entity_name = "Path", type = MOTION_PATH, pathprops = { path_type = "CV_CROrientationSpline", path_data = "OPEN, {0,0,0}, {1,0,0,0}, {10,0,0}, {1,0,0,0}, " } },
            { entity_name = "Char", type = DEFORMABLE, userprops = { Actor = "Char", category = "Char" } },
            { entity_name = "Fx", type = PSYS, psysprops = {} }
        }
        events = {
            { 0, SET_CAMERA, { "Monitor", "Cam" } },
            { 1, START_SPATIAL_PROP_ANIM, { "Cam" }, { duration = 2, spatialprops = { pos = { 1, 2, 3 }, q_orient = { 1, 0, 0, 0 }, axisrot = { 90, Y_AXIS } } } },
            { 2, START_MOTION, { "Char" }, { animation = "Sc_X", duration = 2, event_flags = 2 } },
            { 3, START_PSYS, { "Fx" }, { duration = 1, param_curve = { CLSID = "FreeFormPCurve", points = { { 0, 0, 0, 0 }, { 1, 1, 0, 0 } } }, custom = { 1, 2 } } }
        }
        """;

    private const string KindsCam = Script + "#entity/Cam";
    private const string KindsSpatial = Script + "#event/1";

    /// <summary>The text of W of the script, to compare two states.</summary>
    private static string ScriptText(EditSession session) => ThnWriter.Write(W(session).ToGlobals());

    /// <summary>
    /// One change of each kind of key through <c>set_fields</c>, with the edit of the Inspector for the same change.
    /// The UI parses the text of a field or runs the command of its checkbox, list or drag (see
    /// <see cref="ThnFieldEdit"/>).
    /// </summary>
    public static TheoryData<string, string, string, string> FieldKinds => new()
    {
        { "number", KindsCam, """ "cameraprops.fovh": 20.5 """, "number" },
        { "text", Script + "#event/2", """ "props.animation": "Sc_Y" """, "text" },
        { "bool", Script + "#entity/Lamp", """ "lightprops.on": false """, "bool" },
        { "vector", KindsCam, """ "spatialprops.pos": [1, 2, 3] """, "vector" },
        { "quaternion", KindsSpatial, """ "props.spatialprops.q_orient": [0, 1, 0, 0] """, "quaternion" },
        { "orient", KindsCam, """ "spatialprops.orient": [0, 90, 0] """, "orient" },
        { "symbol", Script + "#entity/Lamp", """ "lightprops.type": "L_DIRECT" """, "symbol" },
        { "flags", Script + "#event/2", """ "props.event_flags": ["Loop", "Overlay"] """, "flags" },
        {
            "path", Script + "#entity/Path",
            """ "pathprops.path_data": { "closed": true, "keys": [ { "point": [0, 0, 0], "orientation": [1, 0, 0, 0] }, { "point": [20, 0, 0], "orientation": [1, 0, 0, 0] } ] } """,
            "path"
        },
        {
            "curve", Script + "#event/3",
            """ "props.param_curve": { "clsid": "FreeFormPCurve", "points": [ { "time": 0, "value": 0, "in": 0, "out": 0 }, { "time": 1, "value": 0.5, "in": 0, "out": 0 } ] } """,
            "curve"
        },
        { "axis", KindsSpatial, """ "props.spatialprops.axisrot": { "degrees": 45, "axis": "X_AXIS" } """, "axis" },
        { "value", Script + "#event/3", """ "props.custom": "{ 3, 4 }" """, "value" },
        { "time", Script + "#event/2", """ "time": 1.5 """, "time" },
        { "length", Script + "#event/2", """ "props.duration": 3 """, "length" },
        { "time off the grid", Script + "#event/2", """ "time": 1.2345 """, "time-grid" },
        { "length off the grid", Script + "#event/2", """ "props.duration": 0.33333 """, "length-grid" },
        { "targets", Script + "#event/0", """ "targets": ["Monitor", "Cam2"] """, "targets" },
        { "remove", KindsCam, """ "cameraprops.hvaspect": null """, "remove" },
    };

    /// <summary>The plan of the Inspector for each case of <see cref="FieldKinds"/>.</summary>
    private static EditPlan InspectorPlan(EditSession s, string ui)
    {
        var symbols = s.ThnSymbols;
        var cam = Entity(s, "Cam");
        var lamp = Entity(s, "Lamp");
        EditPlan Text(ThnRef target, string path, ThnKeyKind kind, string text) =>
            ThnFieldCommand.Set(target, new ThnPath(path)).Make(s, ThnFieldEdit.Parse(kind, text, ThnScripts.Evaluator).Value!);
        return ui switch
        {
            "number" => Text(cam, "cameraprops.fovh", ThnKeyKind.Number, "20.5"),
            "text" => Text(Event(s, 2), "props.animation", ThnKeyKind.Text, "Sc_Y"),
            "bool" => CutsceneCommands.SetValue(s, lamp, new ThnPath("lightprops.on"),
                ThnFieldEdit.BoolValue(Value(s, lamp, "lightprops.on"), false)),
            "vector" => Text(cam, "spatialprops.pos", ThnKeyKind.Vector, "1, 2, 3"),
            "quaternion" => Text(Event(s, 1), "props.spatialprops.q_orient", ThnKeyKind.Quaternion, "0, 1, 0, 0"),
            "orient" => Text(cam, "spatialprops.orient", ThnKeyKind.Orient, "0, 90, 0"),
            "symbol" => CutsceneCommands.SetValue(s, lamp, new ThnPath("lightprops.type"),
                ThnFieldEdit.SymbolValue(Value(s, lamp, "lightprops.type"), "L_DIRECT", symbols)),
            "flags" => CutsceneCommands.SetFlagBits(s, Event(s, 2),
                ThnSchema.Find(ThnElementKind.Event, "START_MOTION", new ThnPath("props.event_flags"))!,
                new HashSet<string> { "Loop", "Overlay" }),
            "path" => CutsceneCommands.SetValue(s, Entity(s, "Path"), ThnMotionPath.Path, new ThnText(ThnMotionPath.Write(
                new ThnPathKeys(true, [new ThnPathKey(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity),
                    new ThnPathKey(new System.Numerics.Vector3(20, 0, 0), System.Numerics.Quaternion.Identity)])))),
            "curve" => CutsceneCommands.SetCurvePoint(s, Event(s, 3), 1, new ThnCurvePoint(1, 0.5f, 0, 0)),
            "axis" => Text(Event(s, 1), "props.spatialprops.axisrot", ThnKeyKind.AxisRotation, "{ 45, X_AXIS }"),
            "value" => Text(Event(s, 3), "props.custom", ThnKeyKind.Value, "{ 3, 4 }"),
            "time" => ThnFieldCommand.Time(Event(s, 2), 2).Make(s, new ThnNumber(1.5f)),
            "length" => ThnFieldCommand.Length(Event(s, 2), 2).Make(s, new ThnNumber(3)),
            "time-grid" => ThnFieldCommand.Time(Event(s, 2), 2).Make(s, new ThnNumber(1.2345f)),
            "length-grid" => ThnFieldCommand.Length(Event(s, 2), 2).Make(s, new ThnNumber(0.33333f)),
            "targets" => CutsceneCommands.SetTarget(s, Event(s, 0), 1, "Cam2"),
            _ => CutsceneCommands.RemoveValue(s, cam, new ThnPath("cameraprops.hvaspect")),
        };
    }

    /// <summary>Each kind of key takes its JSON form, and the tool and the Inspector give the same W (section 13.1,
    /// item 7 of the MCP1 design). <c>null</c> removes the key. The call is one entry, and one undo puts back W.</summary>
    [Theory]
    [MemberData(nameof(FieldKinds))]
    public void EachKindOfKeyGivesTheWOfTheInspector(string kind, string reference, string field, string ui)
    {
        var (ws, session, host) = Open(Kinds);
        using var _ = ws;
        using var other = Workspace(script: Kinds);
        var uiSession = other.Session("act");
        var before = ScriptText(session);

        var result = Ok(host, "set_fields", $$"""{ "ref": {{Json(reference)}}, "fields": { {{field}} } }""");
        var applied = FakeCommandHost.Runner(uiSession).Run(InspectorPlan(uiSession, ui));

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal(EditStatus.Applied, applied.Status);
        Assert.Equal(ScriptText(uiSession), ScriptText(session));
        Assert.NotEqual(before, ScriptText(session));
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);
        session.Undo();
        Assert.Equal(before, ScriptText(session));
        Assert.False(session.CanUndo, kind);
    }

    /// <summary><c>get_cutscene</c> gives each field by its THN path in the JSON form of its kind. A record gives the
    /// keys in it, and a curve and a motion path stay one value. An entity gives its name, type, template and flags as
    /// own properties.</summary>
    [Fact]
    public void GetCutsceneGivesTheFieldsInTheFormsOfSetFields()
    {
        var (ws, _, host) = Open(Kinds);
        using var __ = ws;

        var view = Ok(host, "get_cutscene", $$"""{ "ref": {{Json(Script)}} }""");
        JsonObject Fields(string list, int index) => view[list]![index]!["fields"]!.AsObject();
        var cam = Fields("entities", 0);
        var events = view["events"]!.AsArray();

        Assert.Equal(["cameraprops.fovh", "cameraprops.hvaspect", "cameraprops.nearplane", "cameraprops.farplane",
            "spatialprops.pos", "spatialprops.orient"], cam.Select(f => f.Key));
        Assert.Equal(10, (double)cam["cameraprops.fovh"]!);
        Assert.Equal("[0,0,0]", cam["spatialprops.orient"]!.ToJsonString());
        Assert.Equal("""["REFERENCE"]""", view["entities"]![1]!["flags"]!.ToJsonString());
        Assert.Equal("[]", view["entities"]![0]!["flags"]!.ToJsonString());
        Assert.Equal("L_POINT", (string?)Fields("entities", 3)["lightprops.type"]);
        Assert.True((bool)Fields("entities", 3)["lightprops.on"]!);
        Assert.Equal("""{"closed":false,"keys":[{"point":[0,0,0],"orientation":[1,0,0,0]},{"point":[10,0,0],"orientation":[1,0,0,0]}]}""",
            Fields("entities", 4)["pathprops.path_data"]!.ToJsonString());
        Assert.Equal(["userprops.Actor", "userprops.category"], Fields("entities", 5).Select(f => f.Key));
        Assert.Empty(Fields("entities", 6));
        Assert.Empty(events[0]!["fields"]!.AsObject());
        Assert.Equal("""{"degrees":90,"axis":"Y_AXIS"}""", Fields("events", 1)["props.spatialprops.axisrot"]!.ToJsonString());
        Assert.Equal("[1,0,0,0]", Fields("events", 1)["props.spatialprops.q_orient"]!.ToJsonString());
        Assert.Equal("""["Loop"]""", Fields("events", 2)["props.event_flags"]!.ToJsonString());
        Assert.Equal(2, (double)Fields("events", 2)["props.duration"]!);
        Assert.Equal("""{"clsid":"FreeFormPCurve","points":[{"time":0,"value":0,"in":0,"out":0},{"time":1,"value":1,"in":0,"out":0}]}""",
            Fields("events", 3)["props.param_curve"]!.ToJsonString());
        Assert.Equal("{ 1, 2 }", (string?)Fields("events", 3)["props.custom"]);
        Assert.Null(view["entities"]![0]!["fields"]!["entity_name"]);
    }

    /// <summary>A script with two lights of one name in other letter case. The engine uses the last light, so a
    /// reference of the name names the second light.</summary>
    private const string Twins = """
        duration = 10
        entities = {
            { entity_name = "Lamp", type = LIGHT, lightprops = { type = L_POINT, on = Y, range = 10 }, spatialprops = { pos = { 1, 2, 3 } } },
            { entity_name = "Monitor", type = MONITOR },
            { entity_name = "LAMP", type = LIGHT, lightprops = { type = L_POINT, on = N, range = 20 }, spatialprops = { pos = { 4, 5, 6 } } }
        }
        events = {
            { 0, START_LIGHT_PROP_ANIM, { "Lamp" }, { duration = 1 } }
        }
        """;

    /// <summary>Each field that <c>get_cutscene</c> gives goes back into <c>set_fields</c> and changes nothing, also a
    /// rotation, whose degrees do not give the same floats back. An entity that a later entity with the same name
    /// hides gives no fields, because its reference names the later entity.</summary>
    [Theory]
    [InlineData(Kinds)]
    [InlineData(ThnSessions.Script)]
    [InlineData(Twins)]
    public void EachFieldOfGetCutsceneGoesBackIntoSetFieldsAndGivesNoChange(string script)
    {
        var (ws, session, host) = Open(script);
        using var _ = ws;
        var view = Ok(host, "get_cutscene", $$"""{ "ref": {{Json(Script)}} }""");
        var elements = view["entities"]!.AsArray().Concat(view["events"]!.AsArray())
            .Where(e => e!["fields"]!.AsObject().Count > 0).ToList();

        foreach (var element in elements)
        {
            var result = Ok(host, "set_fields",
                $$"""{ "ref": {{element!["ref"]!.ToJsonString()}}, "fields": {{element["fields"]!.ToJsonString()}} }""");
            Assert.True((string?)result["status"] == "no_change", element.ToJsonString());
        }

        Assert.NotEmpty(elements);
        Assert.False(session.CanUndo);
    }

    /// <summary>In <c>get_cutscene</c>, an entity that a later entity with the same name hides gives the reason and no
    /// fields. The game does not use it, and its reference names the later entity, as the edit tools resolve it. The
    /// later entity gives its fields.</summary>
    [Fact]
    public void AnEntityThatALaterEntityHidesGivesTheReasonAndNoFields()
    {
        var (ws, _, host) = Open(Twins);
        using var __ = ws;

        var entities = Ok(host, "get_cutscene", $$"""{ "ref": {{Json(Script)}} }""")["entities"]!.AsArray();

        Assert.Equal([Script + "#entity/Lamp", Script + "#entity/Monitor", Script + "#entity/LAMP"],
            entities.Select(e => (string?)e!["ref"]));
        Assert.Equal(AgentTexts.Error("cutscene_entity_hidden"), (string?)entities[0]!["read_only_reason"]);
        Assert.Empty(entities[0]!["fields"]!.AsObject());
        Assert.Equal("LIGHT", (string?)entities[0]!["type"]);
        Assert.Null(entities[1]!["read_only_reason"]);
        Assert.Null(entities[2]!["read_only_reason"]);
        Assert.Equal(20, (double)entities[2]!["fields"]!["lightprops.range"]!);
    }

    /// <summary>A call with two fields is one entry with the label of the fields, and its result names the element as
    /// it is after the call.</summary>
    [Fact]
    public void TwoFieldsAreOneEntryAndOneUndoPutsBackW()
    {
        var (ws, session, host) = Open(Kinds);
        using var _ = ws;
        var before = ScriptText(session);

        var result = Ok(host, "set_fields",
            $$"""{ "ref": {{Json(KindsCam)}}, "fields": { "cameraprops.fovh": 30, "spatialprops.pos": [5, 0, 0] } }""");

        Assert.Equal("Agent: Set 2 fields of Cam", session.UndoLabel);
        Assert.Equal([KindsCam], AgentEditToolTests.Texts(result["changes"]));
        session.Undo();
        Assert.Equal(before, ScriptText(session));
    }

    /// <summary>Each error of the generic tools on cutscene items, with its code, its detail and its argument. Nothing
    /// changes.</summary>
    [Theory]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "a..b": 1 } }""", "invalid_argument", ItemRefs.BadReference, "fields.a..b")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "fields": { "duration": 1 } }""", "invalid_argument", ItemRefs.BadReference, "fields.duration")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "fields": { "time": null } }""", "invalid_argument", ItemRefs.BadReference, "fields.time")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "entity_name": "x" } }""", "refused", EditTools.CutsceneItem, "fields.entity_name")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "type": "MARKER" } }""", "refused", EditTools.CutsceneItem, "fields.type")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "fields": { "type": "START_SOUND" } }""", "refused", EditTools.CutsceneItem, "fields.type")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "flags": 1 } }""", "refused", EditTools.CutsceneItem, "fields.flags")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "cameraprops": { "fovh": 1 } } }""", "invalid_argument", ItemRefs.BadReference, "fields.cameraprops")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "cameraprops.fovh": "x" } }""", "invalid_value", MissionErrors.FieldValue, "fields.cameraprops.fovh")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "cameraprops.fovh": 1e39 } }""", "invalid_value", MissionErrors.FieldValue, "fields.cameraprops.fovh")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "spatialprops.pos": [1, 2] } }""", "invalid_value", MissionErrors.FieldValue, "fields.spatialprops.pos")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Lamp", "fields": { "lightprops.type": "CAMERA" } }""", "invalid_value", MissionErrors.FieldValue, "fields.lightprops.type")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Lamp", "fields": { "lightprops.type": 99 } }""", "invalid_value", MissionErrors.FieldValue, "fields.lightprops.type")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/2", "fields": { "props.event_flags": ["Fast"] } }""", "invalid_value", MissionErrors.FieldValue, "fields.props.event_flags")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/3", "fields": { "props.custom": "{ 1," } }""", "invalid_value", MissionErrors.FieldValue, "fields.props.custom")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Path", "fields": { "pathprops.path_data": { "closed": true, "keys": [ { "point": [0, 0, 0] }, { "point": [1, 0, 0], "orientation": [1, 0, 0, 0] } ] } } }""", "invalid_value", MissionErrors.FieldValue, "fields.pathprops.path_data")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "fields": { "time": "x" } }""", "invalid_value", MissionErrors.FieldValue, "fields.time")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "fields": { "time": -1 } }""", "invalid_value", MissionErrors.FieldValue, "fields.time")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/0", "fields": { "props.duration": 1 } }""", "invalid_value", MissionErrors.FieldValue, "fields.props.duration")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/0", "fields": { "targets": ["Monitor"] } }""", "invalid_value", MissionErrors.FieldValue, "fields.targets")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/0", "fields": { "targets": ["Monitor", "Lamp"] } }""", "refused", MissionErrors.TargetType, "fields.targets")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/0", "fields": { "targets": ["Monitor", "Nobody"] } }""", "not_found", MissionErrors.ItemNotFound, "fields.targets")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "fields": { "time": 2 }, "expect_type": "START_SOUND" }""", "stale_revision", MissionErrors.RowChanged, "ref")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "fields": { "time": 2 }, "expect_time": 4 }""", "stale_revision", MissionErrors.RowChanged, "ref")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "cameraprops.fovh": 2 }, "expect_type": "CAMERA" }""", "invalid_argument", ItemRefs.BadReference, "expect_type")]
    [InlineData("set_fields", """{ "ref": "obj:Li01_01", "fields": { "x": 2 }, "expect_time": 1 }""", "invalid_argument", ItemRefs.BadReference, "expect_time")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn", "fields": { "duration": 2 } }""", "refused", EditTools.CutsceneItem, "ref")]
    [InlineData("set_fields", """{ "ref": "thn:C:\\x.thn#entity/Cam", "fields": { "a": 2 } }""", "invalid_argument", "NotInstallPath", "ref")]
    [InlineData("set_fields", """{ "ref": "thn:..\\x.thn#event/0", "fields": { "a": 2 } }""", "invalid_argument", "NotInstallPath", "ref")]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Nobody", "fields": { "a": 2 } }""", "not_found", ItemRefs.NotFound, "ref")]
    [InlineData("set_text", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/2", "field": "props.animation", "text": "x" }""", "invalid_argument", EditTools.CutsceneItem, "ref")]
    [InlineData("rename_item", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#event/2", "new_name": "x" }""", "invalid_argument", EditTools.CutsceneItem, "ref")]
    [InlineData("rename_item", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "new_name": "monitor" }""", "name_taken", MissionErrors.NameTaken, "new_name")]
    [InlineData("rename_item", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "new_name": "  " }""", "refused", MissionErrors.NameInvalid, "new_name")]
    [InlineData("rename_item", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "new_name": "Kamera_\u00fc" }""", "refused", MissionErrors.NameInvalid, "new_name")]
    [InlineData("delete_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn"] }""", "invalid_argument", EditTools.CutsceneItem, "refs[0]")]
    [InlineData("delete_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"], "expect_events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/2" } ] }""", "invalid_argument", ItemRefs.BadReference, "expect_events[0].ref")]
    [InlineData("delete_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"], "expect_events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "expect_type": "SET_CAMERA" } ] }""", "stale_revision", MissionErrors.RowChanged, "expect_events[0].ref")]
    [InlineData("delete_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"], "expect_events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam" } ] }""", "invalid_argument", ItemRefs.BadReference, "expect_events[0].ref")]
    [InlineData("duplicate_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#entity/Cam"] }""", "refused", EditTools.CutsceneItem, "refs[0]")]
    [InlineData("duplicate_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn"] }""", "invalid_argument", EditTools.CutsceneItem, "refs[0]")]
    [InlineData("duplicate_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"], "time_offset": 0 }""", "invalid_value", MissionErrors.FieldValue, "time_offset")]
    [InlineData("duplicate_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"], "time_offset": 0.0001 }""", "invalid_value", MissionErrors.FieldValue, "time_offset")]
    [InlineData("duplicate_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"], "time_offset": -5 }""", "invalid_value", MissionErrors.FieldValue, "time_offset")]
    [InlineData("duplicate_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"], "time_offset": 1e300 }""", "invalid_value", MissionErrors.FieldValue, "time_offset")]
    [InlineData("duplicate_items", """{ "refs": ["sec:DATA\\EQUIPMENT\\goods.ini#Good/g1"], "time_offset": 1 }""", "invalid_argument", ToolInput.BadArgument, "time_offset")]
    public void EachCutsceneErrorOfTheGenericToolsHasItsCodeAndChangesNothing(string tool, string arguments, string code,
        string detail, string field)
    {
        var (ws, session, host) = Open(Kinds);
        using var _ = ws;
        var before = ScriptText(session);

        var result = Call(host, tool, arguments);

        Assert.True(result.IsError, result.Text);
        Assert.Equal((code, detail, field),
            ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]));
        Assert.Equal(before, ScriptText(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>The errors give the reasons of the editor and name the tool for the change.</summary>
    [Fact]
    public void TheCutsceneErrorsOfTheGenericToolsNameTheToolForTheChange()
    {
        var (ws, _, host) = Open(Kinds);
        using var __ = ws;

        var script = Error(host, "set_fields", $$"""{ "ref": {{Json(Script)}}, "fields": { "duration": 2 } }""");
        var text = Error(host, "set_text", $$"""{ "ref": {{Json(KindsCam)}}, "field": "x", "text": "y" }""");
        var name = Error(host, "set_fields", $$"""{ "ref": {{Json(KindsCam)}}, "fields": { "entity_name": "x" } }""");
        var flags = Error(host, "set_fields", $$"""{ "ref": {{Json(KindsCam)}}, "fields": { "flags": 1 } }""");
        var target = Error(host, "set_fields",
            $$"""{ "ref": {{Json(Script + "#event/0")}}, "fields": { "targets": ["Monitor", "Lamp"] } }""");
        var vector = Error(host, "set_fields", $$"""{ "ref": {{Json(KindsCam)}}, "fields": { "spatialprops.pos": "x" } }""");
        var offset = Error(host, "duplicate_items", $$"""{ "refs": [{{Json(KindsSpatial)}}], "time_offset": 0 }""");

        Assert.Contains("cutscene_set_duration", script.Message);
        Assert.Contains("Use set_fields", text.Message);
        Assert.Contains("Use rename_item", name.Message);
        Assert.Contains("cutscene_set_flags", flags.Message);
        Assert.Equal("SET_CAMERA needs a CAMERA as target 2. The entity Lamp is a LIGHT.", target.Message);
        Assert.Equal("The value of spatialprops.pos must be three numbers [x, y, z].", vector.Message);
        Assert.Equal(CutsceneCommands.SameKeyCopy, offset.Message);
    }

    /// <summary>The hidden group Cutscenes gives not_available for a cutscene item in each generic tool (decision
    /// D725), and the tools still change the other items.</summary>
    [Theory]
    [InlineData("set_fields", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "fields": { "cameraprops.fovh": 2 } }""")]
    [InlineData("set_text", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "field": "x", "text": "y" }""")]
    [InlineData("rename_item", """{ "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam", "new_name": "x" }""")]
    [InlineData("delete_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"] }""")]
    [InlineData("duplicate_items", """{ "refs": ["thn:DATA\\SCRIPTS\\x.thn#event/1"] }""")]
    public void TheHiddenGroupCutscenesGivesNotAvailable(string tool, string arguments)
    {
        var (ws, session, host) = Open(Kinds);
        using var _ = ws;
        host.Hidden.Add(ToolGroup.Cutscenes);

        var error = Error(host, tool, arguments);

        Assert.Equal(("not_available", "GroupHidden"), (error.Code, error.Detail));
        Assert.False(session.CanUndo);
    }

    /// <summary>A rename of an entity also renames the events and the speakers of the script that name it. The result
    /// gives the rename and the changed events. When room INI lines name the entity, the editor asks first (decision
    /// D242), so the call needs confirm: true and names the uses.</summary>
    [Fact]
    public void ARenameWithUsesAsksAndRenamesTheTargetsOfTheScript()
    {
        using var ws = Workspace();
        ws.WriteBase(@"DATA\UNIVERSE\universe.ini", "[Base]\nnickname = B\nfile = Universe\\b.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\b.ini", "[Room]\nnickname = Bar\nfile = Universe\\bar.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\bar.ini", "[Room_Info]\nset_script = Scripts\\x.thn\n[Camera]\nname = Cam_A\n");
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var arguments = $$"""{ "ref": {{Json(Script + "#entity/Cam_A")}}, "new_name": "My_cam" }""";

        var asked = Call(host, "rename_item", arguments);
        var preview = Ok(host, "rename_item", """{ "preview": true, """ + arguments[2..]);
        var renamed = Ok(host, "rename_item", """{ "confirm": true, """ + arguments[2..]);

        Assert.Equal(("needs_confirmation", "ConfirmChanges"), ((string?)asked.Content["code"], (string?)asked.Content["detail"]));
        Assert.Contains("bar.ini [Camera] name = Cam_A", (string?)asked.Content["message"]);
        Assert.True((bool)preview["needs_confirmation"]!);
        Assert.Equal("Agent: Rename to My_cam", session.UndoLabel);
        Assert.Equal($$"""[{"from":{{Json(Script + "#entity/Cam_A")}},"to":{{Json(Script + "#entity/My_cam")}}}]""",
            renamed["renames"]!.ToJsonString());
        Assert.Equal([Script + "#event/0", Script + "#event/5"], AgentEditToolTests.Texts(renamed["changes"]));
        Assert.Equal(new ThnText("My_cam"), ((ThnTable)Value(session, Event(session, 0), "targets")!).List[1]);

        var other = Ok(host, "rename_item", $$"""{ "ref": {{Json(Script + "#entity/Fx")}}, "new_name": "My_fx" }""");
        Assert.Equal("applied", (string?)other["status"]);
    }

    /// <summary>A delete of an entity that events name asks first, as the editor does. With confirm: true, the entity
    /// goes with its events. The deletes give the index of each event before the call, and the cause of each event.
    /// One undo puts back W.</summary>
    [Fact]
    public void ADeleteOfAnEntityWithEventsAsksThenRemovesTheEvents()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);
        var arguments = $$"""{ "refs": [{{Json(Script + "#entity/Fx")}}] }""";

        var asked = Error(host, "delete_items", arguments);
        var deleted = Ok(host, "delete_items", """{ "confirm": true, """ + arguments[2..]);

        Assert.Equal("needs_confirmation", asked.Code);
        Assert.Contains($"It also removes {Script}#event/1, {Script}#event/2.", asked.Message);
        Assert.Equal("Agent: Delete entity and 2 events", session.UndoLabel);
        Assert.Equal([$"{Script}#event/1 cascade targets = Fx", $"{Script}#event/2 cascade targets = Fx", $"{Script}#entity/Fx target "],
            deleted["deletes"]!.AsArray().Select(d => $"{d!["ref"]} {d["why"]} {d["cause"]}"));
        Assert.DoesNotContain(W(session).Entities, e => ThnScriptModel.NameOf(e.Table) == "Fx");
        Assert.Equal(4, W(session).Events.Count);
        session.Undo();
        Assert.Equal(before, State(session));
    }

    /// <summary>The question of a delete names each event with its index before the call, as the deletes of the result
    /// do. An earlier plan of the call that deletes an event before them does not change the indexes of the question.
    /// The preview gives the same text as a warning.</summary>
    [Fact]
    public void TheQuestionOfADeleteGivesTheIndexesOfTheResult()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var arguments = $$"""{ "refs": [{{Json(Script + "#event/1")}}, {{Json(Script + "#entity/Cam_A")}}] }""";

        var asked = Error(host, "delete_items", arguments);
        var preview = Ok(host, "delete_items", """{ "preview": true, """ + arguments[2..]);
        var deleted = Ok(host, "delete_items", """{ "confirm": true, """ + arguments[2..]);

        var question = $"The edit 'Delete entity and 2 events' changes more than its targets. It also removes " +
                       $"{Script}#event/0, {Script}#event/5.";
        Assert.StartsWith(question + " ", asked.Message);
        Assert.Contains(preview["warnings"]!.AsArray(), w => (string?)w!["message"] == question);
        Assert.Equal([$"{Script}#event/1", $"{Script}#event/0 cascade", $"{Script}#event/5 cascade", $"{Script}#entity/Cam_A"],
            deleted["deletes"]!.AsArray().Select(d => $"{d!["ref"]}{((string?)d["why"] == "cascade" ? " cascade" : "")}"));
    }

    /// <summary>A delete of two entities that events name asks with one text: one intro, then the events of both
    /// entities with their indexes before the call.</summary>
    [Fact]
    public void ADeleteOfTwoEntitiesAsksWithOneText()
    {
        var (ws, _, host) = Open();
        using var __ = ws;
        var arguments = $$"""{ "refs": [{{Json(Script + "#entity/Cam_A")}}, {{Json(Script + "#entity/Fx")}}] }""";

        var asked = Error(host, "delete_items", arguments);
        var deleted = Ok(host, "delete_items", """{ "confirm": true, """ + arguments[2..]);

        Assert.StartsWith("The 2 edits 'Delete entity and 2 events' change more than their targets. They also remove " +
                          $"{Script}#event/0, {Script}#event/5, {Script}#event/1 and 1 more. Use preview: true", asked.Message);
        Assert.Equal([$"{Script}#event/0", $"{Script}#event/5", $"{Script}#event/1", $"{Script}#event/2"],
            deleted["deletes"]!.AsArray().Where(d => (string?)d!["why"] == "cascade").Select(d => (string?)d!["ref"]));
    }

    /// <summary>In <c>edit_batch</c>, the question of a step names each event with its index before the step, as the
    /// deletes of the step do.</summary>
    [Fact]
    public void TheQuestionOfAStepGivesTheIndexesOfTheStep()
    {
        var (ws, _, host) = Open();
        using var __ = ws;
        string Batch(bool confirm) => $$"""
            { "steps": [
                { "tool": "delete_items", "arguments": { "refs": [ {{Json(Script + "#event/1")}} ] } },
                { "tool": "delete_items", "arguments": { "refs": [ {{Json(Script + "#entity/Cam_A")}} ], "confirm": {{(confirm ? "true" : "false")}} } }
            ] }
            """;

        var asked = Error(host, "edit_batch", Batch(false));
        var batch = Ok(host, "edit_batch", Batch(true));

        Assert.Contains($"It also removes {Script}#event/0, {Script}#event/4.", asked.Message);
        Assert.Equal([$"{Script}#event/0", $"{Script}#event/4"], batch["steps"]![1]!["deletes"]!.AsArray()
            .Where(d => (string?)d!["why"] == "cascade").Select(d => (string?)d!["ref"]));
    }

    /// <summary>A delete of events of two cutscenes is one entry with one plan for each script. A batch that copies an
    /// event and deletes an earlier event gives the copy with its index after the call, and the deleted event with its
    /// index before the call.</summary>
    [Fact]
    public void DeletesOfTwoCutscenesAndABatchOfACopyAndADeleteGiveTheReferencesOfTheCall()
    {
        var (ws, session, host) = Open();
        using var _ = ws;

        var two = Ok(host, "edit_batch", $$"""
            { "steps": [
                { "tool": "cutscene_create", "arguments": { "name": "my_intro" } },
                { "tool": "delete_items", "arguments": { "refs": [ {{Json(Script + "#event/1")}}, {{Json("thn:" + NewPath + "#event/0")}} ] } }
            ] }
            """);
        Assert.Equal("Agent: New cutscene my_intro", session.UndoLabel);
        Assert.Equal([$"{Script}#event/1", $"thn:{NewPath}#event/0"],
            two["steps"]![1]!["deletes"]!.AsArray().Select(d => (string?)d!["ref"]));
        Assert.Empty(session.Cutscene(NewPath)!.Events);
        session.Undo();

        var batch = Ok(host, "edit_batch", $$"""
            { "steps": [
                { "tool": "duplicate_items", "arguments": { "refs": [ {{Json(Script + "#event/4")}} ] } },
                { "tool": "delete_items", "arguments": { "refs": [ {{Json(Script + "#event/2")}} ] } }
            ] }
            """);
        Assert.Equal([$"{Script}#event/5"], AgentEditToolTests.Texts(batch["adds"]));
        Assert.Equal([$"{Script}#event/2"], batch["deletes"]!.AsArray().Select(d => (string?)d!["ref"]));
        Assert.Equal($"{Script}#event/5", (string?)batch["select"]);
        Assert.Equal([$"{Script}#event/6"], AgentEditToolTests.Texts(batch["steps"]![0]!["adds"]));
    }

    /// <summary>A copy of events goes after the events, as Duplicate of the Timeline does, and the result gives the
    /// copies and selects the last one. With time_offset, each copy is at the time of its event plus the offset. A
    /// guard of expect_events that matches lets the call run.</summary>
    [Fact]
    public void ADuplicateGivesTheReferencesOfTheCopies()
    {
        var (ws, session, host) = Open();
        using var _ = ws;

        var copies = Ok(host, "duplicate_items", $$"""
            { "refs": [ {{Json(Script + "#event/1")}}, {{Json(Script + "#event/2")}} ],
              "expect_events": [ { "ref": {{Json(Script + "#event/2")}}, "expect_type": "START_PSYS", "expect_time": 5 } ] }
            """);
        var offset = Ok(host, "duplicate_items", $$"""{ "refs": [{{Json(Script + "#event/1")}}], "time_offset": 2.5 }""");

        Assert.Equal([$"{Script}#event/6", $"{Script}#event/7"], AgentEditToolTests.Texts(copies["adds"]));
        Assert.Equal($"{Script}#event/7", (string?)copies["select"]);
        Assert.Equal("Agent: Duplicate event", session.UndoLabel);
        Assert.Equal([$"{Script}#event/8"], AgentEditToolTests.Texts(offset["adds"]));
        Assert.Equal([8f, 8f, 7.5f], W(session).Events.Skip(6).Select(e => ((ThnNumber)e.Table.List[0]).Value));
        Assert.True(copies["warnings"] is null or JsonArray { Count: 0 });
    }

    /// <summary>An event has no position, so <c>offset</c> does not move a copy of an event. The result warns about
    /// each copy, as for a mission copy. The copies start at the end of the events, as without the offset.</summary>
    [Fact]
    public void AnOffsetDoesNotMoveACopyOfAnEventAndTheResultWarns()
    {
        var (ws, session, host) = Open();
        using var _ = ws;

        var result = Ok(host, "duplicate_items",
            $$"""{ "refs": [{{Json(Script + "#event/1")}}, {{Json(Script + "#event/2")}}], "offset": [10, 0, 0] }""");

        Assert.Equal([$"{Script}#event/6", $"{Script}#event/7"], AgentEditToolTests.Texts(result["adds"]));
        Assert.Equal([$"{Script}#event/6", $"{Script}#event/7"],
            result["warnings"]!.AsArray().Select(w => (string?)w!["ref"]));
        Assert.Equal($"The copy {Script}#event/6 is a cutscene event, so the offset did not move it. To move the copy " +
                     "in time, use time_offset.", (string?)result["warnings"]![0]!["message"]);
        Assert.Equal([8f, 8f], W(session).Events.Skip(6).Select(e => ((ThnNumber)e.Table.List[0]).Value));
    }

    /// <summary>A preview of set_fields changes nothing and gives the result of the real call (section 13.1, item 6 of
    /// the MCP1 design). with_patch gives the THN patch.</summary>
    [Fact]
    public void APreviewOfSetFieldsGivesTheResultOfTheRealCall()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var arguments = $$"""{ "ref": {{Json(Script + "#event/1")}}, "fields": { "time": 7, "props.duration": 4 }, "expect_type": "START_PSYS" }""";

        var preview = Ok(host, "set_fields", """{ "preview": true, "with_patch": true, """ + arguments[2..]);
        Assert.False(session.CanUndo);
        Assert.Contains("ol_thn_patch", (string?)preview["patch"]![ScriptPath]);
        var real = Ok(host, "set_fields", arguments);

        Assert.Equal(real["changes"]!.ToJsonString(), preview["changes"]!.ToJsonString());
        Assert.Equal([Script + "#event/1"], AgentEditToolTests.Texts(real["changes"]));
        Assert.Equal([7f, 4f], new[] { Value(session, Event(session, 1), "time"), Value(session, Event(session, 1), "props.duration") }
            .Select(v => ((ThnNumber)v!).Value));
    }

    /// <summary>A save of an edit of a lower cutscene writes only a THN patch into the folder of the active mod (section
    /// 8.2, item 1 of the MCP1 design). The base script does not change.</summary>
    [Fact]
    public void ASaveOfAnEditOfALowerCutsceneWritesOnlyAPatch()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var baseText = File.ReadAllText(Path.Combine(ws.Base, ScriptPath));

        Ok(host, "set_fields", $$"""{ "ref": {{Json(Script + "#entity/Cam_A")}}, "fields": { "cameraprops.fovh": 25 } }""");
        AssertSaved(session.Save());

        Assert.Contains("cameraprops.fovh", PatchText(ws));
        Assert.False(File.Exists(Path.Combine(ws.ModFolder("act"), WholeFile)));
        Assert.Equal(baseText, File.ReadAllText(Path.Combine(ws.Base, ScriptPath)));
        Assert.Equal(25f, ((ThnNumber)Value(session, Entity(session, "Cam_A"), "cameraprops.fovh")!).Value);
    }
}
