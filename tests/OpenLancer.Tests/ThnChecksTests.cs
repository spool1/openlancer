using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The error checks of a cutscene script (test 13.1 item 13 of the M8 design): one fixture for each check of section 9.
/// Each fixture has one problem, and the message names the element and the path. A REFERENCE COMPOUND with a template
/// and no category has no error, also when a name of another group gives the REFERENCE flag. A DEFORMABLE with an Actor
/// and a missing costume has no error, and one without an Actor has an error. A script that the game plays only in space
/// has no COMPOUND errors. <see cref="ThnChecksEngineTests"/> compares the read checks with the engine.
/// </summary>
[Collection(nameof(CompilerCollection))]
public sealed class ThnChecksTests
{
    private const string Script = @"DATA\SCRIPTS\x.thn";

    private const string Marker = """{ entity_name = "Mk", type = MARKER }""";

    /// <summary>The errors of a script with these entities and events.</summary>
    private static IReadOnlyList<Diagnostic> Check(string entities, string events = "", IThnGameData? data = null) =>
        ThnChecks.Errors(Script, Globals(entities, events), Evaluator, data);

    /// <summary>The globals of a script with these entities and events.</summary>
    private static ThnTable Globals(string entities, string events) =>
        Evaluate($"duration = 10\nentities = {{\n{entities}\n}}\nevents = {{\n{events}\n}}\n");

    /// <summary>The message of the one error of a script, or "" when it has no error.</summary>
    private static string Message(string entities, string events = "", IThnGameData? data = null) =>
        Check(entities, events, data) switch
        {
            [] => "",
            [var one] => one.Message,
            var many => string.Join(" | ", many.Select(e => e.Message)),
        };

    [Theory]
    [InlineData("duration = \"10\"\nentities = {}\nevents = {}",
        "duration: The value is \"10\", but the engine reads a number there.")]
    [InlineData("entities = {}\nevents = {}", "duration: The global is missing. The engine reads a number there.")]
    [InlineData("duration = 1\nentities = 5\nevents = {}",
        "entities: The value is 5, but the engine reads a table there.")]
    [InlineData("duration = 1\nentities = {}", "events: The global is missing. The engine reads a table there.")]
    [InlineData("duration = 1\nentities = { 5 }\nevents = {}",
        "entities[1]: The value is 5, but the engine reads a table there.")]
    [InlineData("duration = 1\nentities = {}\nevents = { \"x\" }",
        "events[1]: The value is \"x\", but the engine reads a table there.")]
    public void TheDurationIsANumberAndTheEntitiesAndTheEventsAreTables(string text, string expected)
    {
        var error = Assert.Single(ThnChecks.Errors(Script, Evaluate(text), Evaluator));

        Assert.Equal(expected, error.Message);
        Assert.Equal((DiagnosticSeverity.Error, Script), (error.Severity, error.Source));
    }

    [Theory]
    [InlineData("{ type = MARKER }", "entities[1], entity_name: The key is missing. The engine needs it.")]
    [InlineData("{ entity_name = 5, type = MARKER }",
        "entities[1], entity_name: The value is 5, but the engine reads a text there.")]
    [InlineData("""{ entity_name = "E" }""", "entity \"E\", type: The key is missing. The engine needs it.")]
    [InlineData("""{ entity_name = "E", type = Y }""",
        "entity \"E\", type: The value is Y, but the engine reads a name of EntityTypes or a number there.")]
    [InlineData("""{ entity_name = "E", type = L_POINT }""",
        "entity \"E\", type: The value is L_POINT, but the engine reads a name of EntityTypes or a number there.")]
    // The engine converts a number and a text with a name (ThnTypes.cs:59, :72-77).
    [InlineData("""{ entity_name = "E", type = 7 }""", "")]
    [InlineData("""{ entity_name = "E", type = "MARKER" }""", "")]
    public void EachEntityHasATextNameAndAType(string entity, string expected)
    {
        Assert.Equal(expected, Message(entity));
    }

    [Theory]
    [InlineData("""{ "0", START_PSYS, { "Fx" } }""",
        "event { \"0\", START_PSYS, { \"Fx\" } }, time: The value is \"0\", but the engine reads a number there.")]
    [InlineData("{}", "events[1], time: The value is missing. The engine reads a number there. | events[1], type: The " +
                      "value is missing. The engine reads a name of EventTypes or a number there. | events[1], targets: " +
                      "The value is missing. The engine reads a table there.")]
    [InlineData("""{ 0, 99, { "Fx" } }""",
        "event { 0, 99, { \"Fx\" } }, type: The value is 99. The engine has no event of this type.")]
    [InlineData("""{ 0, USER_EVENT, { "Fx" } }""",
        "event { 0, USER_EVENT, { \"Fx\" } }, type: The value is USER_EVENT. The engine has no event of this type.")]
    [InlineData("""{ 0, CAMERA, { "Fx" } }""",
        "event { 0, CAMERA, { \"Fx\" } }, type: The value is CAMERA, but the engine reads a name of EventTypes or a number there.")]
    [InlineData("""{ 0, START_PSYS, "Fx" }""",
        "event { 0, START_PSYS, \"Fx\" }, targets: The value is \"Fx\", but the engine reads a table there.")]
    [InlineData("""{ 0, START_PSYS, { "Fx", 5 } }""",
        "event { 0, START_PSYS, { \"Fx\", 5 } }, targets: A target is 5, but the engine reads a text for each target.")]
    [InlineData("""{ 0, START_PSYS, { "Fx" }, 5 }""",
        "event { 0, START_PSYS, { \"Fx\" } }, props: The value is 5, but the engine reads a table there.")]
    [InlineData("""{ 0, "START_PSYS", { "Fx" } }""", "")]
    [InlineData("""{ 0, 13, { "Fx" }, { duration = 2 } }""", "")]
    public void EachEventHasANumberTimeATypeWithAClassTextTargetsAndAPropsTable(string ev, string expected)
    {
        Assert.Equal(expected, Message("", ev));
    }

    [Theory]
    [InlineData("template_name = 5", "template_name: The value is 5, but the engine reads a text there.")]
    [InlineData("flags = \"HIDDEN\"", "flags: The value is \"HIDDEN\", but the engine reads a number or a flag name there.")]
    [InlineData("flags = F_EXP", "flags: The value is F_EXP, but the engine reads a number or a flag name there.")]
    // The cast takes a name of each group with the same integer type (ThnScript.cs:301).
    [InlineData("flags = CAMERA", "")]
    [InlineData("flags = LIT_DYNAMIC", "")]
    [InlineData("flags = 6", "")]
    [InlineData("srt_grp = \"1\"", "srt_grp: The value is \"1\", but the engine reads a number there.")]
    [InlineData("cameraprops = 5", "cameraprops: The value is 5, but the engine reads a table there.")]
    [InlineData("cameraprops = { fovh = \"20\" }",
        "cameraprops.fovh: The value is \"20\", but the engine reads a number there.")]
    [InlineData("audioprops = { ain = Y }", "audioprops.ain: The value is Y, but the engine reads a number there.")]
    [InlineData("compoundprops = { floor_height = \"1\" }",
        "compoundprops.floor_height: The value is \"1\", but the engine reads a number there.")]
    [InlineData("spatialprops = { orient = { { 1, 0, 0 }, { 0, 1, 0 } } }",
        "spatialprops.orient: The value is { { 1, 0, 0 }, { 0, 1, 0 } }, but the engine reads three lists of three numbers there.")]
    // TryGetVector3 skips a value of another kind (ThornTable.cs:412-433).
    [InlineData("spatialprops = { pos = { 1, 2 } }", "")]
    [InlineData("userprops = 5", "userprops: The value is 5, but the engine reads a table there.")]
    [InlineData("userprops = { category = 5 }", "userprops.category: The value is 5, but the engine reads a text there.")]
    // The engine reads the user props without letter case (ThnScript.cs:305-314).
    [InlineData("userprops = { NoFog = \"maybe\" }",
        "userprops.nofog: The value is \"maybe\", but the engine reads Y, N or a number there.")]
    [InlineData("userprops = { nofog = \"Y\", Actor = 5, speaker = 6, Priority = 7 }", "")]
    [InlineData("userprops = { TextString = \"abc\" }",
        "userprops.TextString: The value is \"abc\", but the engine reads a number there.")]
    [InlineData("userprops = { TextString = \"12\", TextStart = \"1.5\" }", "")]
    // The engine reads TextStart with its letter case, and only with TextString (ThnScript.cs:336-344).
    [InlineData("userprops = { TextString = 12, textstart = \"x\" }", "")]
    [InlineData("userprops = { TextStart = \"x\" }", "")]
    [InlineData("up = \"abc\"", "up: The value is \"abc\", but the engine reads an axis name or a list of three numbers there.")]
    [InlineData("up = \"Y_AXIS\", front = { 0, 0, 1 }", "")]
    // The engine does not read psysprops and the fog of an entity.
    [InlineData("psysprops = 5, fogcolor = \"red\"", "")]
    public void TheKeysOfAnEntityHaveTheKindThatTheEngineReads(string keys, string expected)
    {
        Assert.Equal(expected.Length == 0 ? "" : "entity \"Mk\", " + expected,
            Message($$"""{ entity_name = "Mk", type = MARKER, {{keys}} }"""));
    }

    /// <summary>The engine reads lightprops from an entity of each type, also from a REFERENCE entity: it needs the
    /// type and the position (ThnScript.cs:433-453).</summary>
    [Theory]
    [InlineData("lightprops = { on = Y }, spatialprops = { pos = { 0, 0, 0 } }",
        "lightprops.type: The key is missing. The engine needs it.")]
    [InlineData("lightprops = { type = L_POINT }",
        "spatialprops.pos: The value is not a list of three numbers. An entity with lightprops needs it.")]
    [InlineData("flags = REFERENCE, lightprops = { type = L_POINT }",
        "spatialprops.pos: The value is not a list of three numbers. An entity with lightprops needs it.")]
    [InlineData("lightprops = { type = L_POINT, range = \"5\" }, spatialprops = { pos = { 0, 0, 0 } }",
        "lightprops.range: The value is \"5\", but the engine reads a number there.")]
    [InlineData("lightprops = { type = \"L_SPOT\", on = \"N\", diffuse = 5 }, spatialprops = { pos = { 0, 0, 0 } }", "")]
    public void AnEntityWithLightpropsHasALightTypeAndAPosition(string keys, string expected)
    {
        Assert.Equal(expected.Length == 0 ? "" : "entity \"Mk\", " + expected,
            Message($$"""{ entity_name = "Mk", type = MARKER, {{keys}} }"""));
    }

    /// <summary>A LIGHT needs lightprops, and a SCENE needs an ambient color (ThnScriptInstance.cs:206-221). The scene
    /// does not make a REFERENCE entity or an entity that a later entity with the same name hides.</summary>
    [Theory]
    [InlineData("""{ entity_name = "L", type = LIGHT }""", "entity \"L\", lightprops: The key is missing. The engine needs it for a LIGHT.")]
    [InlineData("""{ entity_name = "L", type = LIGHT, flags = REFERENCE }""", "")]
    // The cast of a name of another group gives its number (ThnScript.cs:301). CAMERA is 3 and LIGHT is 5, so the
    // REFERENCE bit is on. DEFORMABLE is 2, which is LIT_DYNAMIC.
    [InlineData("""{ entity_name = "L", type = LIGHT, flags = CAMERA }""", "")]
    [InlineData("""{ entity_name = "L", type = LIGHT, flags = LIGHT }""", "")]
    [InlineData("""{ entity_name = "L", type = LIGHT, flags = DEFORMABLE }""",
        "entity \"L\", lightprops: The key is missing. The engine needs it for a LIGHT.")]
    [InlineData("""{ entity_name = "L", type = LIGHT }, { entity_name = "l", type = MARKER }""", "")]
    [InlineData("""{ entity_name = "S", type = SCENE }""",
        "entity \"S\", ambient: The value is not a list of three numbers. The engine needs it for a SCENE.")]
    [InlineData("""{ entity_name = "S", type = SCENE, ambient = { 1, 1 } }""",
        "entity \"S\", ambient: The value is not a list of three numbers. The engine needs it for a SCENE.")]
    [InlineData("""{ entity_name = "S", type = SCENE, ambient = { 0, 0, 0 } }""", "")]
    public void ALightHasLightpropsAndASceneHasAnAmbientColor(string entities, string expected)
    {
        Assert.Equal(expected, Message(entities));
    }

    /// <summary>The engine runs the path text as a Lua table (MotionPath.cs:54-103).</summary>
    [Theory]
    [InlineData("5", "The value is 5, but the engine reads a text there.")]
    [InlineData("\"OPEN {\"", "The value is not a path that the engine reads. A path text starts with OPEN or CLOSED and a comma.")]
    [InlineData("\"OPEN, {\"", "The value is not a path that the engine reads. The path text ends before a table is complete.")]
    [InlineData("\"OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0} } x = { OPEN, {2+3,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}\"",
        "The value is not a path that the engine reads. The path text holds '}', which a path text cannot hold.")]
    [InlineData("\"1, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}\"",
        "The value is not a path that the engine reads. A path text starts with OPEN or CLOSED and a comma.")]
    [InlineData("\"OPEN, {0,0,0}, {1,1,1}\"",
        "The value is not a path that the engine reads. A path text needs an orientation { w, x, y, z } after each point.")]
    [InlineData("\"OPEN, {0,0,0}, {1,0,0,0}\"", "The value is not a path that the engine reads. A path needs at least two points.")]
    [InlineData("\"OPEN, {0,0,0}, {1,0,0,0}, {1,1}\"",
        "The value is not a path that the engine reads. The item 4 of the path is not a list of 3 numbers.")]
    [InlineData("\"OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}, \"", "")]
    [InlineData("\"CLOSED, {0,0,0}, {1,0,0,0}, {1,1,1}\"", "")]
    public void APathTextIsAPathThatTheEngineReads(string text, string expected)
    {
        var message = Message($$"""{ entity_name = "P", type = MOTION_PATH, pathprops = { path_data = {{text}} } }""");

        if (expected.Length == 0)
            Assert.Equal("", message);
        else
            Assert.StartsWith("entity \"P\", pathprops.path_data: " + expected, message);
    }

    /// <summary>
    /// A COMPOUND with a template needs a category that the engine knows (ThnScriptInstance.cs:120-154). The scene does
    /// not make a REFERENCE COMPOUND, so the 15 retail REFERENCE COMPOUNDs with a template and no category have no
    /// error. A COMPOUND without a template, a hidden COMPOUND and the player ship need no category.
    /// </summary>
    [Theory]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "x" }""",
        "entity \"C\", userprops.category: The key is missing. The engine needs it for a COMPOUND with a template.")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "x", flags = REFERENCE }""", "")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "x", flags = 3 }""", "")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "x", flags = MARKER }""", "")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "x", userprops = { category = "Spaceships" } }""",
        "entity \"C\", userprops.category: The value is \"Spaceships\", but the engine knows only the categories asteroid, " +
        "equipment, equipment cart, prop, room, ship, solar, spaceship.")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "x", userprops = { Category = "Prop" } }""", "")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "" }""", "")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "playership" }""", "")]
    [InlineData("""{ entity_name = "C", type = COMPOUND, template_name = "x" }, { entity_name = "c", type = MARKER }""", "")]
    public void ACompoundThatTheSceneMakesHasAKnownCategory(string entities, string expected)
    {
        Assert.Equal(expected, Message(entities));
    }

    /// <summary>The engine stops when a solar, a ship, a room or a cart does not exist, and when the item has no model.
    /// A missing prop, equipment or asteroid gives an object without a model (ThnScriptInstance.cs:120-160).</summary>
    [Theory]
    [InlineData("solar", ThnItemKind.Archetype, ThnItemState.Missing, "The game data has no solar archetype \"x\".")]
    [InlineData("solar", ThnItemKind.Archetype, ThnItemState.NoModel, "The solar archetype \"x\" has no model.")]
    [InlineData("Spaceship", ThnItemKind.Ship, ThnItemState.Missing, "The game data has no ship \"x\".")]
    [InlineData("room", ThnItemKind.Room, ThnItemState.Missing, "The game data has no room of petalDB \"x\".")]
    [InlineData("equipment cart", ThnItemKind.Cart, ThnItemState.Missing, "The game data has no cart of petalDB \"x\".")]
    [InlineData("equipment", ThnItemKind.Equipment, ThnItemState.Missing, "")]
    [InlineData("equipment", ThnItemKind.Equipment, ThnItemState.NoModel, "The equipment \"x\" has no model.")]
    [InlineData("asteroid", ThnItemKind.Asteroid, ThnItemState.Missing, "")]
    [InlineData("asteroid", ThnItemKind.Asteroid, ThnItemState.Ready, "")]
    [InlineData("solar", ThnItemKind.Archetype, ThnItemState.Ready, "")]
    public void TheTemplateOfACompoundExistsForItsCategory(string category, ThnItemKind kind, ThnItemState state,
        string expected)
    {
        var data = new FakeData((kind, "x", state));
        var entity = $$"""{ entity_name = "C", type = COMPOUND, template_name = "x", userprops = { category = "{{category}}" } }""";

        Assert.Equal(expected.Length == 0 ? "" : "entity \"C\", template_name: " + expected, Message(entity, data: data));
        // A prop never stops the engine, and without game data the check does not run.
        Assert.Equal("", Message(entity.Replace(category, "prop"), data: new FakeData()));
        Assert.Equal("", Message(entity));
        Assert.Equal("", Message(entity, data: new FakeData((kind, "x", state)) { Loaded = false }));
    }

    /// <summary>
    /// The game makes no COMPOUND of a script that it plays only in space: the space scene uses the objects of the
    /// mission in their place (Cutscene.cs:101-113, ThnScriptInstance.cs:113). So no check of a COMPOUND applies there.
    /// The other checks of the build still apply.
    /// </summary>
    [Fact]
    public void TheGameMakesNoCompoundOfAScriptThatItPlaysOnlyInSpace()
    {
        const string entities = """
            { entity_name = "C", type = COMPOUND, template_name = "x" },
            { entity_name = "S", type = COMPOUND, template_name = "y", userprops = { category = "solar" } },
            { entity_name = "L", type = LIGHT }
            """;
        const string light = "entity \"L\", lightprops: The key is missing. The engine needs it for a LIGHT.";

        Assert.Equal(["entity \"C\", userprops.category: The key is missing. The engine needs it for a COMPOUND with a template.",
            "entity \"S\", template_name: The game data has no solar archetype \"y\".", light],
            Check(entities, data: new FakeData()).Select(e => e.Message));
        Assert.Equal([light], Check(entities, data: new FakeData { MakesCompounds = false }).Select(e => e.Message));
    }

    /// <summary>
    /// The Actor rule: a DEFORMABLE without <c>userprops.Actor</c> loads its costume, and the engine stops when the
    /// costume does not exist or has no body (ThnScriptInstance.cs:255-264). With an Actor, a room NPC or an RTC
    /// character can play the entity, so a missing costume is not an error.
    /// </summary>
    [Theory]
    [InlineData("", ThnItemState.Missing,
        "The costume \"trent\" is not in the game data. A DEFORMABLE without userprops.Actor needs it.")]
    [InlineData("", ThnItemState.NoModel,
        "The costume \"trent\" has no body. A DEFORMABLE without userprops.Actor needs it.")]
    [InlineData("""userprops = { Actor = "" }, """, ThnItemState.Missing,
        "The costume \"trent\" is not in the game data. A DEFORMABLE without userprops.Actor needs it.")]
    [InlineData("""userprops = { Actor = "player" }, """, ThnItemState.Missing, "")]
    [InlineData("""userprops = { actor = "juni" }, """, ThnItemState.NoModel, "")]
    [InlineData("", ThnItemState.Ready, "")]
    public void ADeformableWithoutAnActorNeedsACostume(string keys, ThnItemState state, string expected)
    {
        var entity = $$"""{ entity_name = "D", type = DEFORMABLE, {{keys}}template_name = "trent" }""";

        Assert.Equal(expected.Length == 0 ? "" : "entity \"D\", template_name: " + expected,
            Message(entity, data: new FakeData((ThnItemKind.Costume, "trent", state))));
        Assert.Equal("", Message(entity, data: new FakeData((ThnItemKind.Costume, "trent", state)) { Loaded = false }));
        // Without a template, the engine finds no costume (GameItemCollection.cs:30-38).
        Assert.Equal("entity \"D\", template_name: The key is missing. A DEFORMABLE without userprops.Actor needs a costume.",
            Message("""{ entity_name = "D", type = DEFORMABLE }"""));
    }

    /// <summary>Each event type reads its props with <c>ThnTypes.Convert</c>, which also takes a text with a number or a
    /// name (ThnTypes.cs:41-78).</summary>
    [Theory]
    [InlineData("START_MOTION", "animation = 5", "props.animation: The value is 5, but the engine reads a text there.")]
    [InlineData("START_MOTION", "time_scale = \"fast\"", "props.time_scale: The value is \"fast\", but the engine reads a number there.")]
    [InlineData("START_MOTION", "time_scale = \"2\", event_flags = 2", "")]
    [InlineData("START_SOUND", "duration = \"long\"", "props.duration: The value is \"long\", but the engine reads a number there.")]
    [InlineData("ATTACH_ENTITY", "flags = \"POSITION\"", "")]
    [InlineData("ATTACH_ENTITY", "flags = LOOP", "props.flags: The value is LOOP, but the engine reads a name of AttachFlags or a number there.")]
    [InlineData("ATTACH_ENTITY", "offset = { 1, 2 }", "props.offset: The value is { 1, 2 }, but the engine reads a list of three numbers there.")]
    [InlineData("ATTACH_ENTITY", "up = \"X\"", "props.up: The value is \"X\", but the engine reads an axis name or a list of three numbers there.")]
    [InlineData("START_SPATIAL_PROP_ANIM", "spatialprops = { q_orient = { 1, 0, 0 } }",
        "props.spatialprops.q_orient: The value is { 1, 0, 0 }, but the engine reads a list of four numbers { w, x, y, z } there.")]
    [InlineData("START_SPATIAL_PROP_ANIM", "spatialprops = { orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } }",
        "props.spatialprops.orient: The engine cannot read this key, because it reads the rows of a matrix from index 0. Use q_orient.")]
    [InlineData("START_SPATIAL_PROP_ANIM", "spatialprops = { axisrot = { 90, \"up\" } }",
        "props.spatialprops.axisrot: The value is { 90, \"up\" }, but the engine reads a list of degrees and an axis there.")]
    [InlineData("START_SPATIAL_PROP_ANIM", "spatialprops = { axisrot = { 90, Y_AXIS } }, target_type = \"ROOT\"", "")]
    [InlineData("START_IK", "point_at = Y, move_to = \"N\", count_to_root = 2", "")]
    [InlineData("START_IK", "point_at = \"yes\"", "props.point_at: The value is \"yes\", but the engine reads Y, N or a number there.")]
    [InlineData("CONNECT_HARDPOINTS", "hardpoint = 5, parent_hardpoint = Y", "")]
    [InlineData("START_PSYS", "param_curve = { points = {} }", "props.param_curve.CLSID: The key is missing. The engine needs it.")]
    [InlineData("START_PSYS", "param_curve = { CLSID = \"FreeFormPCurve\", points = { { 0, 1, 2 } } }",
        "props.param_curve.points: The value is { { 0, 1, 2 } }, but the engine reads a list of points of four numbers there.")]
    // The engine reads pcurve_period only with a curve (ThnEvent.cs:49-53).
    [InlineData("START_PSYS", "pcurve_period = \"x\"", "")]
    [InlineData("START_PSYS", "param_curve = { CLSID = \"x\", points = {} }, pcurve_period = \"x\"",
        "props.pcurve_period: The value is \"x\", but the engine reads a number there.")]
    // FogModes has a byte for each value (FogModes.cs:8), so the engine cannot convert 300 or a name of an int group.
    [InlineData("START_FOG_PROP_ANIM", "fogprops = { fogmode = 300 }",
        "props.fogprops.fogmode: The value is 300, but the engine reads a name of FogModes or a number there.")]
    [InlineData("START_FOG_PROP_ANIM", "fogprops = { fogmode = \"L_POINT\" }",
        "props.fogprops.fogmode: The value is \"L_POINT\", but the engine reads a name of FogModes or a number there.")]
    [InlineData("START_FOG_PROP_ANIM", "fogprops = { fogmode = \"F_EXP\", fogcolor = { 255, 0, 0 } }", "")]
    // A text with a name of a group with the same integer type converts (ThnTypes.cs:59).
    [InlineData("START_SOUND", "flags = \"POSITION\"", "")]
    // The engine reads nearplane only without aspect (StartCameraPropAnimEvent.cs:43).
    [InlineData("START_CAMERA_PROP_ANIM", "cameraprops = { aspect = 1, nearplane = \"x\" }", "")]
    [InlineData("START_CAMERA_PROP_ANIM", "cameraprops = { nearplane = \"x\" }",
        "props.cameraprops.nearplane: The value is \"x\", but the engine reads a number there.")]
    // SET_CAMERA reads only the props of each event.
    [InlineData("SET_CAMERA", "animation = 5", "")]
    public void ThePropsOfAnEventHaveTheKindThatItsTypeReads(string type, string props, string expected)
    {
        var ev = $$"""{ 1, {{type}}, { "A", "B" }, { {{props}} } }""";

        Assert.Equal(expected.Length == 0 ? "" : $$"""event { 1, {{type}}, { "A", "B" } }, {{expected}}""", Message("", ev));
    }

    /// <summary>Most events read their first target without a check, and three types read two
    /// (SetCameraEvent.cs:17-30, AttachEntityEvent.cs:77-83, StartPathAnimationEvent.cs:34-35).</summary>
    [Theory]
    [InlineData("""{ 0, SET_CAMERA, { "M" } }""", "event { 0, SET_CAMERA, { \"M\" } }, targets: The event has 1 targets, but the engine reads 2 for SET_CAMERA.")]
    [InlineData("""{ 0, ATTACH_ENTITY, { "A" } }""", "event { 0, ATTACH_ENTITY, { \"A\" } }, targets: The event has 1 targets, but the engine reads 2 for ATTACH_ENTITY.")]
    [InlineData("""{ 0, START_PATH_ANIMATION, { "A" } }""", "event { 0, START_PATH_ANIMATION, { \"A\" } }, targets: The event has 1 targets, but the engine reads 2 for START_PATH_ANIMATION.")]
    [InlineData("""{ 0, START_MOTION, {} }""", "event { 0, START_MOTION, {} }, targets: The event has 0 targets, but the engine reads 1 for START_MOTION.")]
    [InlineData("""{ 0, START_SPATIAL_PROP_ANIM, {} }""", "")]
    [InlineData("""{ 0, CONNECT_HARDPOINTS, { "A" } }""", "")]
    [InlineData("""{ 0, START_FOG_PROP_ANIM, {} }""", "")]
    public void AnEventHasTheTargetsThatItsTypeReads(string ev, string expected)
    {
        Assert.Equal(expected, Message("", ev));
    }

    /// <summary>The camera update reads the camera props of the second target of SET_CAMERA (Cutscene.cs:492-497,
    /// ThnCamera.cs:71). A target that names no entity that the script makes can be an object of the set script of a
    /// room: a name that the script does not have, or a REFERENCE entity.</summary>
    [Theory]
    [InlineData("Mk", "event { 0, SET_CAMERA, { \"M\", \"Mk\" } }, targets: The second target is entity \"Mk\", a MARKER. " +
                      "SET_CAMERA needs a CAMERA there.")]
    [InlineData("cam", "")]
    [InlineData("Room_Camera", "")]
    [InlineData("Ref", "")]
    public void TheSecondTargetOfSetCameraIsACamera(string target, string expected)
    {
        var entities = $$"""{{Marker}}, { entity_name = "Cam", type = CAMERA }, { entity_name = "M", type = MONITOR },""" +
                       """{ entity_name = "Ref", type = MARKER, flags = REFERENCE }""";

        Assert.Equal(expected, Message(entities, $$"""{ 0, SET_CAMERA, { "M", "{{target}}" } }"""));
    }

    /// <summary>A path animation reads the path of its second target (StartPathAnimationEvent.cs:35-37,
    /// ThnSceneObject.cs:218). An entity of any type gets a path from <c>pathprops.path_data</c> (ThnScript.cs:476-484).
    /// A REFERENCE entity and a name that the script does not have can be an object of the set script of a room.</summary>
    [Theory]
    [InlineData("Mk", "The second target is entity \"Mk\", which has no pathprops.path_data. START_PATH_ANIMATION needs a path there.")]
    [InlineData("Empty", "The second target is entity \"Empty\", which has no pathprops.path_data. START_PATH_ANIMATION needs a path there.")]
    [InlineData("path", "")]
    [InlineData("Marked", "")]
    [InlineData("Ref", "")]
    [InlineData("Room_Path", "")]
    public void TheSecondTargetOfAPathAnimationHasAPath(string target, string expected)
    {
        const string path = "pathprops = { path_data = \"OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}\" }";
        var entities = $$"""{{Marker}}, { entity_name = "Path", type = MOTION_PATH, {{path}} },""" +
                       """{ entity_name = "Empty", type = MOTION_PATH, pathprops = {} },""" +
                       $$"""{ entity_name = "Marked", type = MARKER, {{path}} },""" +
                       """{ entity_name = "Ref", type = MOTION_PATH, flags = REFERENCE }""";

        Assert.Equal(expected.Length == 0 ? "" : $$"""event { 0, START_PATH_ANIMATION, { "Mk", "{{target}}" } }, targets: {{expected}}""",
            Message(entities, $$"""{ 0, START_PATH_ANIMATION, { "Mk", "{{target}}" } }"""));
    }

    /// <summary>
    /// Each error has its place: the element, its key and the path of the value. An element of the record part of the
    /// list has no place in the list, so its place is the script. A place finds its element by the key when an edit
    /// moved the element, and finds no element when the key names more than one element.
    /// </summary>
    [Fact]
    public void AnErrorHasThePlaceOfItsElementAndValue()
    {
        var globals = Globals($$"""{{Marker}}, { entity_name = "Cam", type = CAMERA, cameraprops = { fovh = "x" } }""",
            """{ 0, START_MOTION, { "Mk" } }, { 1, START_MOTION, { "Mk" }, { animation = 5 } }""");
        var errors = ThnChecks.Errors(Script, globals, Evaluator);

        Assert.Equal([new ThnLocation(Script, ThnElementKind.Entity, 1, "cameraprops.fovh", "\"Cam\""),
            new ThnLocation(Script, ThnElementKind.Event, 1, "props.animation", "{ 1, START_MOTION, { \"Mk\" } }")],
            errors.Select(e => e.Cutscene));

        var model = ThnScriptModel.From(globals, ThnElementId.Counter());
        var (camera, motion) = (errors[0].Cutscene!, errors[1].Cutscene!);
        Assert.Same(model.Entities[1], camera.ElementIn(model));
        Assert.Same(model.Events[1], motion.ElementIn(model));
        // An edit deleted the element before each one.
        var moved = model with { Entities = model.Entities.RemoveAt(0), Events = model.Events.RemoveAt(0) };
        Assert.Same(model.Entities[1], camera.ElementIn(moved));
        Assert.Same(model.Events[1], motion.ElementIn(moved));
        // The key names two events, and the event at the place has another key.
        var twice = model with { Events = [model.Events[1], model.Events[0], model.Events[1] with { Id = new ThnElementId(99) }] };
        Assert.Null(motion.ElementIn(twice));
        Assert.Null(new ThnLocation(Script).ElementIn(model));

        var record = ThnChecks.Errors(Script, Evaluate("duration = 1\nentities = { odd = { entity_name = 5, type = MARKER } }\nevents = {}"),
            Evaluator);
        Assert.Equal("entities.odd, entity_name: The value is 5, but the engine reads a text there.", Assert.Single(record).Message);
        Assert.Equal(new ThnLocation(Script), record[0].Cutscene);
    }

    /// <summary>The game data around a script: each listed item has its state, and each other item is missing.</summary>
    private sealed class FakeData(params (ThnItemKind Kind, string Name, ThnItemState State)[] items) : IThnGameData
    {
        /// <summary>False when the game data did not load, so that no item has a state.</summary>
        public bool Loaded { get; init; } = true;

        public bool MakesCompounds { get; init; } = true;

        public ThnItemState? Item(ThnItemKind kind, string template) => Loaded
            ? items.Where(i => i.Kind == kind && i.Name == template).Select(i => i.State).DefaultIfEmpty(ThnItemState.Missing).First()
            : null;
    }
}
