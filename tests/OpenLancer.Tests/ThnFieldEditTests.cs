using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Thn;
using LibreLancer.Thn.Events;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The Inspector edits of a cutscene without ImGui (section 7.5 of the M8 design): the text of a value and the value
/// that a text gives, the field edit as one entry that keeps the last valid value, the bits of a flags key of an event,
/// a drag of a curve point, and the path keys. The field edit and the drag are gestures of the command layer (decision
/// D766), so the runner opens them and refuses them when the editor takes no edits.
/// </summary>
public sealed class ThnFieldEditTests
{
    private static readonly ThnPath Fovh = new("cameraprops.fovh");

    private static ThnFieldParse Parse(ThnKeyKind kind, string text) => ThnFieldEdit.Parse(kind, text, Evaluator);

    /// <summary>A number field gives the float of the text and shows the shortest text of the float. A number that is not
    /// finite is an error (section 5.2).</summary>
    [Fact]
    public void ANumberFieldGivesTheFloatOfTheTextAndRefusesANumberThatIsNotFinite()
    {
        Assert.Equal(N(0.1f), Parse(ThnKeyKind.Number, " 0.1 ").Value);
        Assert.Equal(ThnNumber.NotFiniteMessage, Parse(ThnKeyKind.Number, "1e39").Error);
        Assert.Equal(ThnNumber.NotFiniteMessage, Parse(ThnKeyKind.Number, "NaN").Error);
        Assert.Equal("'1,2' is not a number.", Parse(ThnKeyKind.Number, "1,2").Error);
        Assert.Equal("'x' is not a number.", Parse(ThnKeyKind.Number, "x").Error);
        Assert.Equal("16777216", ThnFieldEdit.Format(ThnKeyKind.Number, N(16777216)));
        Assert.Equal("0.1", ThnFieldEdit.Format(ThnKeyKind.Number, N(0.1f)));
        Assert.Equal("0", ThnFieldEdit.Format(ThnKeyKind.Number, N(-0f)));
    }

    /// <summary>A vector, a quaternion and a rotation matrix have one field for each value. The matrix shows as Euler
    /// angles in degrees and gives the matrix of the angles back.</summary>
    [Fact]
    public void AVectorAQuaternionAndARotationGiveTheirValues()
    {
        Assert.Equal(List(N(1), N(2.5f), N(-3)), Parse(ThnKeyKind.Vector, "1, 2.5,-3").Value);
        Assert.Equal("1, 2.5, -3", ThnFieldEdit.Format(ThnKeyKind.Vector, List(N(1), N(2.5f), N(-3))));
        Assert.Equal("'1, 2' is not 3 numbers separated by commas.", Parse(ThnKeyKind.Vector, "1, 2").Error);
        Assert.Equal(ThnNumber.NotFiniteMessage, Parse(ThnKeyKind.Vector, "1, 2, 1e40").Error);
        Assert.Equal(List(N(1), N(0), N(0), N(0)), Parse(ThnKeyKind.Quaternion, "1, 0, 0, 0").Value);

        var matrix = ThnPose.Orient(MathHelper.MatrixFromEulerDegrees(new Vector3(0, 30, 0)));
        Assert.Equal("0, 30, 0", ThnFieldEdit.Format(ThnKeyKind.Orient, matrix));
        Assert.Equal(matrix, Parse(ThnKeyKind.Orient, "0, 30, 0").Value);
    }

    /// <summary>A key that the schema does not know edits as Lua value text, which the evaluator reads. A character
    /// outside ASCII refuses, because the evaluator would read it as '?'.</summary>
    [Fact]
    public void AnUnknownKeyEditsAsLuaText()
    {
        Assert.Equal(List(N(1), T("a"), S("CAMERA")), Parse(ThnKeyKind.Value, "{ 1, \"a\", CAMERA }").Value);
        Assert.Equal(new ThnBool(true), Parse(ThnKeyKind.Value, "Y").Value);
        Assert.StartsWith("field: The script does not compile", Parse(ThnKeyKind.Value, "{ 1,").Error);
        Assert.StartsWith("The field needs a value.", Parse(ThnKeyKind.Value, "  ").Error);
        var outside = Parse(ThnKeyKind.Value, "{ \"ü\" }");
        Assert.Null(outside.Value);
        Assert.Equal(ThnWriter.TextProblem("{ \"ü\" }"), outside.Error);
    }

    /// <summary>A decimal escape in a Lua text that gives a character outside ASCII or U+0000 refuses. The evaluator
    /// would read the first as '?' and drop the second at the end of the text. The scan finds an escape after a
    /// '--' or a quote in another string.</summary>
    [Theory]
    [InlineData("{ \"\\200\" }", "\\200", "00C8")]
    [InlineData("{ 'a\\255b' }", "\\255", "00FF")]
    [InlineData("{ \"a\\0\" }", "\\0", "0000")]
    [InlineData("{ \"\\\"\\000\" }", "\\000", "0000")]
    [InlineData("{ \"x\", \"\\1289\" }", "\\128", "0080")]
    [InlineData("{ \"--\", \"'\", \"\\200\" }", "\\200", "00C8")]
    public void AnEscapeOutsideAsciiInLuaTextRefuses(string text, string escape, string code)
    {
        var parse = Parse(ThnKeyKind.Value, text);

        Assert.Null(parse.Value);
        Assert.Equal($"The escape {escape} gives the character U+{code}. {ThnWriter.TextRule}", parse.Error);
    }

    /// <summary>An escape inside ASCII keeps its value. A long string has no escapes, and a comment is not a part of
    /// the value, so the escape check does not refuse them.</summary>
    [Theory]
    [InlineData("{ \"\\065\\0109\" }", "A\n9")]
    [InlineData("{ \"\\\\200\" }", "\\200")]
    [InlineData("{ [[\\200]] }", "\\200")]
    [InlineData("{ [[a [[\" ]] \"\\200]] }", "a [[\" ]] \"\\200")]
    [InlineData("-- \"\\200\"\n{ \"b\" }", "b")]
    [InlineData("{ \"--\\127\" }", "--\u007f")]
    public void AnEscapeInsideAsciiInLuaTextKeepsItsValue(string text, string value)
    {
        Assert.Equal(List(T(value)), Parse(ThnKeyKind.Value, text).Value);
    }

    /// <summary>
    /// A field edit is one transaction: each text shows at once, a text that is not valid keeps the last valid value
    /// and gives the error, and the end adds one entry. A text back to the start text adds no entry, and an end with a
    /// text that is not valid puts back the start.
    /// </summary>
    [Fact]
    public void AFieldEditKeepsTheLastValidValueAndAddsOneEntry()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        var edit = Gesture(session, cam, "10");
        edit.Change("2");
        Assert.Equal(N(2), Value(session, cam, "cameraprops.fovh"));
        edit.Change("2x");
        Assert.Equal("'2x' is not a number.", edit.Error);
        Assert.Equal(N(2), Value(session, cam, "cameraprops.fovh"));
        edit.Change("25");
        Assert.Null(edit.Error);
        Assert.Null(edit.End(keep: true, interactive: true));
        Assert.Equal(N(25), Value(session, cam, "cameraprops.fovh"));
        Assert.Equal("Set cameraprops.fovh", session.UndoLabel);
        session.Undo();
        Assert.Equal(N(10), Value(session, cam, "cameraprops.fovh"));
        Assert.False(session.CanUndo);

        var back = Gesture(session, cam, "10");
        back.Change("11");
        back.Change("10");
        Assert.Null(back.End(keep: true, interactive: true));
        var bad = Gesture(session, cam, "10");
        bad.Change("12");
        bad.Change("ab");
        Assert.Equal("'ab' is not a number.", bad.End(keep: true, interactive: true));
        Assert.Equal(N(10), Value(session, cam, "cameraprops.fovh"));
        var escape = Gesture(session, cam, "10");
        escape.Change("13");
        Assert.Null(escape.End(keep: false, interactive: true));
        Assert.False(session.CanUndo);
        Assert.Equal(N(10), Value(session, cam, "cameraprops.fovh"));
    }

    /// <summary>
    /// The runner opens a field edit as a gesture of the command layer. The edit becomes one entry with the origin User
    /// and the label of the field. When the editor takes no edits, the edit does not open, and the message is the
    /// reason. No gesture stays open then, so the next edit opens.
    /// </summary>
    [Fact]
    public void AFieldEditIsOneEntryOfTheUserAndDoesNotOpenWhileTheEditorTakesNoEdits()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        const string reason = "The game runs from this profile.";
        var refused = Assert.Throws<InvalidOperationException>(() => Gesture(session, cam, "10", reason));
        Assert.Equal(reason, refused.Message);
        var edit = Gesture(session, cam, "10");
        edit.Change("30");
        Assert.Null(edit.End(keep: true, interactive: true));

        Assert.Equal(N(30), Value(session, cam, "cameraprops.fovh"));
        Assert.Equal(("Set cameraprops.fovh", EditOrigin.User), (session.UndoLabel, session.UndoOrigin));
        session.Undo();
        Assert.False(session.CanUndo);
    }

    /// <summary>A value that a save cannot write, here a text that is not ASCII, is an error of the field.</summary>
    [Fact]
    public void AValueThatASaveCannotWriteIsAnErrorOfTheField()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");
        var path = new ThnPath("userprops.category");

        var key = new FieldKey(trent, path.Text, FieldKey.AllLines);
        var edit = ThnFieldGesture.Open(FakeCommandHost.Runner(session), key, "Char",
            text => ThnFieldParse.Of(new ThnText(text)), ThnFieldCommand.Set(trent, path));
        edit.Change("Chär");

        Assert.NotNull(edit.Error);
        Assert.Contains("ASCII", edit.Error);
        edit.Dispose();
        Assert.False(session.CanUndo);
    }

    /// <summary>The path of a cutscene value compares with letter case in a field key, because the keys of a record of
    /// a cutscene are case-sensitive. An INI key does not.</summary>
    [Fact]
    public void AFieldKeyOfACutsceneComparesItsPathWithLetterCase()
    {
        var element = new ThnRef(ScriptPath, ThnElementKind.Entity, new ThnElementId(1));
        Assert.NotEqual(new FieldKey(element, "userprops.Actor", -1), new FieldKey(element, "userprops.actor", -1));
        Assert.Equal(new FieldKey(element, "userprops.Actor", -1), new FieldKey(element, "userprops.Actor", -1));
        var section = EditSessionTests.Good("g1");
        Assert.Equal(new FieldKey(section, "Price", -1), new FieldKey(section, "price", -1));
    }

    /// <summary>
    /// A checkbox of a flags key of an event writes with the rules of the entity flags: one bit from a name or from no
    /// key is the THN name of the bit when the group has names, a combination is a number, and a stored number stays a
    /// number. The bits of event_flags have no names, so they are numbers.
    /// </summary>
    [Fact]
    public void ACheckboxOfAnEventFlagsKeyWritesANameForOneBitAndElseANumber()
    {
        var attach = ThnSchema.Find(ThnElementKind.Event, "ATTACH_ENTITY", new ThnPath("props.flags"))!;
        var symbols = Evaluator.Symbols;
        HashSet<string> On(params string[] names) => [.. names];

        Assert.Equal(S("POSITION"), ThnFlags.WriteBits(null, attach, On("POSITION"), symbols));
        Assert.Equal(S("LOOK_AT"), ThnFlags.WriteBits(S("POSITION"), attach, On("LOOK_AT"), symbols));
        Assert.Equal(N(6), ThnFlags.WriteBits(S("POSITION"), attach, On("POSITION", "ORIENTATION"), symbols));
        Assert.Equal(N(2), ThnFlags.WriteBits(N(6), attach, On("POSITION"), symbols));
        var same = S("POSITION");
        Assert.Same(same, ThnFlags.WriteBits(same, attach, On("POSITION"), symbols));
        Assert.Equal(["ORIENTATION", "POSITION"], ThnFlags.BitsOn(N(6), attach, symbols).Order());
        // Bits that the row does not list keep their state.
        Assert.Equal(N(1 | 4), ThnFlags.WriteBits(N(1 | 2), attach, On("ORIENTATION"), symbols));
        var motion = ThnSchema.Find(ThnElementKind.Event, "START_MOTION", new ThnPath("props.event_flags"))!;
        Assert.Equal(N(2), ThnFlags.WriteBits(null, motion, On("Loop"), symbols));
        Assert.Equal(N(130), ThnFlags.WriteBits(N(2), motion, On("Loop", "Overlay"), symbols));
        Assert.Throws<ArgumentException>(() => ThnFlags.WriteBits(null, motion, On("LOOP"), symbols));
        Assert.Throws<InvalidOperationException>(() => ThnFlags.BitsOn(S("CAMERA"), attach, symbols));
    }

    /// <summary>
    /// An ATTACH_ENTITY without flags runs with POSITION and ORIENTATION (AttachEntityEvent.cs:60). So the checkboxes of
    /// the missing key show these two bits, and a click adds a bit to them or removes one. The engine reads each written
    /// value as the bits of the checkboxes. The other flags keys have no bit without the key.
    /// </summary>
    [Fact]
    public void TheCheckboxesOfAnAttachmentWithoutFlagsShowTheBitsThatTheEngineUses()
    {
        var attach = ThnSchema.Find(ThnElementKind.Event, "ATTACH_ENTITY", new ThnPath("props.flags"))!;
        var symbols = Evaluator.Symbols;
        HashSet<string> On(params string[] names) => [.. names];

        Assert.Equal(["ORIENTATION", "POSITION"], ThnFlags.BitsOn(null, attach, symbols).Order());
        var lookAt = ThnFlags.WriteBits(null, attach, On("POSITION", "ORIENTATION", "LOOK_AT"), symbols);
        Assert.Equal(N(2 | 4 | 8), lookAt);
        var orientation = ThnFlags.WriteBits(null, attach, On("ORIENTATION"), symbols);
        Assert.Equal(S("ORIENTATION"), orientation);
        var none = ThnFlags.WriteBits(null, attach, On(), symbols);
        Assert.Equal(N(0), none);
        Assert.Null(ThnFlags.WriteBits(null, attach, On("POSITION", "ORIENTATION"), symbols));
        var sound = ThnSchema.Find(ThnElementKind.Event, "START_SOUND", new ThnPath("props.flags"))!;
        Assert.Empty(ThnFlags.BitsOn(null, sound, symbols));

        // The engine reads the same bits.
        foreach (var value in new[] { null, lookAt, orientation, none })
        {
            var props = value == null ? "{ duration = 0 }" : $"{{ flags = {ThnWriter.Value(value)} }}";
            var script = new ThnScript(System.Text.Encoding.ASCII.GetBytes(
                "duration = 1\nentities = { { entity_name = \"A\", type = MARKER }, { entity_name = \"B\", type = MARKER } }\n" +
                $"events = {{ {{ 0, ATTACH_ENTITY, {{ \"A\", \"B\" }}, {props} }} }}\n"), _ => null!, "attach.thn");
            var engine = Assert.IsType<AttachEntityEvent>(Assert.Single(script.Events));
            Assert.Equal(ThnFlags.BitsOn(value, attach, symbols).Order(),
                attach.Bits.Where(b => ((int)engine.Flags & b.Bit) != 0).Select(b => b.Name).Order());
        }
    }

    /// <summary>
    /// A checkbox of <c>Y</c> or <c>N</c> and a list of THN names keep the type of the stored value (design section
    /// 5.2): a stored number stays a number, and a name, a bool or no value gives a name or a bool. The engine reads the
    /// number of a name as the name.
    /// </summary>
    [Fact]
    public void ACheckboxAndAListOfNamesKeepAStoredNumberANumber()
    {
        var symbols = Evaluator.Symbols;
        Assert.Equal(N(0), ThnFieldEdit.BoolValue(N(1), on: false));
        Assert.Equal(N(1), ThnFieldEdit.BoolValue(N(0), on: true));
        Assert.Equal(new ThnBool(false), ThnFieldEdit.BoolValue(new ThnBool(true), on: false));
        Assert.Equal(new ThnBool(true), ThnFieldEdit.BoolValue(null, on: true));

        var part = ThnFieldEdit.SymbolValue(N(symbols["ROOT"].Value), "PART", symbols);
        Assert.Equal(N(symbols["PART"].Value), part);
        Assert.Equal(S("PART"), ThnFieldEdit.SymbolValue(S("ROOT"), "PART", symbols));
        Assert.Equal(S("PART"), ThnFieldEdit.SymbolValue(null, "PART", symbols));
        var script = new ThnScript(System.Text.Encoding.ASCII.GetBytes(
            "duration = 1\nentities = { { entity_name = \"A\", type = MARKER }, { entity_name = \"B\", type = MARKER } }\n" +
            $"events = {{ {{ 0, ATTACH_ENTITY, {{ \"A\", \"B\" }}, {{ target_type = {ThnWriter.Value(part)} }} }} }}\n"),
            _ => null!, "target.thn");
        Assert.Equal(TargetTypes.Part, Assert.IsType<AttachEntityEvent>(Assert.Single(script.Events)).TargetType);
    }

    /// <summary>
    /// The time and the length fields of an event change it as a drag on the Timeline does: one entry with the label of
    /// the drag, on the 1 ms grid. A time or a length below 0 is an error of the field, and the event keeps its values.
    /// </summary>
    [Fact]
    public void TheTimeAndTheLengthFieldsOfAnEventMoveItOnTheGridFrom0()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var psys = Event(session, 1);
        Assert.Equal((N(5), N(2)), (Value(session, psys, "time"), Value(session, psys, "props.duration")));

        var time = NumberField(session, psys, "time", ThnFieldCommand.Time(psys, 5));
        time.Change("13.65666");
        Assert.Equal(N(13.657f), Value(session, psys, "time"));
        time.Change("-1");
        Assert.Equal("An event cannot start before 0.", time.Error);
        Assert.Equal(N(5), Value(session, psys, "time"));
        time.Change("7.25");
        Assert.Null(time.End(keep: true, interactive: true));
        Assert.Equal(N(7.25f), Value(session, psys, "time"));
        Assert.Equal("Move event", session.UndoLabel);

        var length = NumberField(session, psys, "props.duration", ThnFieldCommand.Length(psys, 2));
        length.Change("-0.5");
        Assert.Equal("The length of an event cannot be negative.", length.Error);
        Assert.Equal((N(7.25f), N(2)), (Value(session, psys, "time"), Value(session, psys, "props.duration")));
        length.Change("3.33333");
        Assert.Null(length.End(keep: true, interactive: true));
        Assert.Equal((N(7.25f), N(3.333f)), (Value(session, psys, "time"), Value(session, psys, "props.duration")));
        Assert.Equal("Change the length of event", session.UndoLabel);

        session.Undo();
        session.Undo();
        Assert.Equal((N(5), N(2)), (Value(session, psys, "time"), Value(session, psys, "props.duration")));
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// A drag of a curve point is one entry. The time of the point stays between the times of its neighbours, and the
    /// other points keep their tables. A drag back to the start adds no entry.
    /// </summary>
    [Fact]
    public void ADragOfACurvePointIsOneEntryAndKeepsTheOrderOfThePoints()
    {
        using var ws = Workspace(script: CurveScript());
        var session = ws.Session("act");
        var commands = FakeCommandHost.Runner(session);
        var psys = Event(session, 1);
        var before = (ThnTable)Value(session, psys, "props.param_curve")!;

        using (var drag = CurvePointDrag.Begin(commands, psys, 1))
        {
            for (var i = 0; i < 50; i++)
                Assert.Null(drag.Move(0.5f + i * 0.02f, 0.25f + i * 0.01f));
            Assert.Null(drag.Move(1.5f, 0.75f));
            drag.End();
        }

        var curve = (ThnTable)Value(session, psys, "props.param_curve")!;
        Assert.Equal([new ThnCurvePoint(0, 0, 0, 0), new ThnCurvePoint(1, 0.75f, 0, 0), new ThnCurvePoint(1, 1, 0, 0)],
            ThnCurve.Points(curve));
        var (oldPoints, newPoints) = ((ThnTable)before["points"]!, (ThnTable)curve["points"]!);
        Assert.Same(oldPoints.List[0], newPoints.List[0]);
        Assert.Same(oldPoints.List[2], newPoints.List[2]);
        Assert.Same(before["CLSID"], curve["CLSID"]);
        Assert.Equal("Move point 2 of the curve", session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        session.Undo();
        Assert.Same(before, Value(session, psys, "props.param_curve"));
        Assert.False(session.CanUndo);

        var back = CurvePointDrag.Begin(commands, psys, 0);
        back.Move(0, 0.5f);
        back.Move(0, 0);
        back.End();
        Assert.False(session.CanUndo);
        var escape = CurvePointDrag.Begin(commands, psys, 2);
        escape.Move(1, 3);
        var moved = ThnCurve.Points((ThnTable)Value(session, psys, "props.param_curve")!)!;
        Assert.Equal(new ThnCurvePoint(1, 3, 0, 0), moved[2]);
        escape.Cancel();
        Assert.Same(before, Value(session, psys, "props.param_curve"));
        Assert.False(session.CanUndo);
        Assert.Equal("The curve of the event has no point 4.",
            Assert.Throws<InvalidOperationException>(() => CurvePointDrag.Begin(commands, psys, 3)).Message);
        Assert.Throws<InvalidOperationException>(() => CurvePointDrag.Begin(commands, Event(session, 0), 0));
    }

    /// <summary>
    /// A step of a curve drag that does not apply gives the reason, and W holds the curve of the start of the drag. The
    /// curve editor then stops the drag with an error in the Console. Here a patch cannot change the event, because the
    /// lower event has no key. When the editor takes no edits, the drag does not open, and the message is the reason.
    /// </summary>
    [Fact]
    public void ARefusedStepOfACurveDragLeavesTheCurveOfTheStart()
    {
        using var ws = Workspace(script: CurveScript("{ 7 }"));
        var session = ws.Session("act");
        var psys = Event(session, 1);
        var before = Value(session, psys, "props.param_curve");

        var drag = CurvePointDrag.Begin(FakeCommandHost.Runner(session), psys, 1);
        Assert.StartsWith("A patch cannot change this event of the lower cutscene", drag.Move(0.6f, 0.5f));
        Assert.Same(before, Value(session, psys, "props.param_curve"));
        Assert.Equal(new ThnCurvePoint(0.5f, 0.25f, 0, 0), drag.Applied);
        drag.Cancel();
        Assert.False(session.CanUndo);

        var refused = Assert.Throws<InvalidOperationException>(() =>
            CurvePointDrag.Begin(FakeCommandHost.Runner(session, "The game runs from this profile."), psys, 1));
        Assert.Equal("The game runs from this profile.", refused.Message);
        // No gesture stays open, so the next drag opens.
        CurvePointDrag.Begin(FakeCommandHost.Runner(session), psys, 1).Cancel();
        var viewer = ws.SecondSession(null);
        Assert.Equal("There is no active mod. Select a mod to edit.", Assert.Throws<InvalidOperationException>(() =>
            CurvePointDrag.Begin(FakeCommandHost.Runner(viewer), Event(viewer, 1), 1)).Message);
    }

    /// <summary>A cutscene that is read-only by itself refuses to open a curve drag, also when the editor and the
    /// session take edits. The message is the reason of the cutscene. No gesture stays open, and nothing
    /// changes.</summary>
    [Fact]
    public void ACurveDragDoesNotOpenOnACutsceneThatIsReadOnlyByItself()
    {
        using var ws = Workspace(new() { [PatchFile] = BrokenPatch }, CurveScript());
        var session = ws.Session("act");
        var commands = FakeCommandHost.Runner(session);
        var psys = Event(session, 1);
        var reason = session.CutsceneReadOnlyReason(ScriptPath);
        var before = W(session);
        Assert.Null(session.ReadOnlyReason);
        Assert.NotNull(reason);
        Assert.Equal(3, CutsceneCommands.CurvePoints(session, psys)!.Count);

        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
            CurvePointDrag.Begin(commands, psys, 1)).Message);

        commands.Gesture("No gesture is open").Cancel();
        Assert.Same(before, W(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>A field of a point of the curve table gives the curve with that point.</summary>
    [Fact]
    public void AFieldOfAPointOfTheCurveTableGivesTheCurve()
    {
        var curve = Record(F("CLSID", T("FreeFormPCurve")),
            F("points", List(List(N(0), N(0), N(0), N(0)), List(N(1), N(1), N(0), N(0)))));

        var result = ThnCurveEditor.PointParse(curve, 1, "1, 0.5, 2, 3").Value as ThnTable;

        Assert.Equal([new ThnCurvePoint(0, 0, 0, 0), new ThnCurvePoint(1, 0.5f, 2, 3)], ThnCurve.Points(result!));
        Assert.Equal("'1, 2' is not 4 numbers separated by commas.", ThnCurveEditor.PointParse(curve, 1, "1, 2").Error);
    }

    /// <summary>A field of a key of a motion path gives the whole path text with the new key, and the other key keeps
    /// its values.</summary>
    [Fact]
    public void AFieldOfAKeyOfAPathGivesThePathText()
    {
        var keys = ThnMotionPath.Parse("OPEN, {0,0,0}, {1,0,0,0}, {1,0,0}, {1,0,0,0}, ", Evaluator);

        var point = ThnPathKeysEditor.PointParse(keys, 1, "1, 2.5, 0");
        var turn = ThnPathKeysEditor.OrientationParse(keys, 0, "0, 0, 1, 0");

        Assert.Equal(T("OPEN, {0,0,0}, {1,0,0,0}, {1,2.5,0}, {1,0,0,0}, "), point.Value);
        Assert.Equal(T("OPEN, {0,0,0}, {0,0,1,0}, {1,0,0}, {1,0,0,0}, "), turn.Value);
        Assert.Equal("'1, 2' is not 3 numbers separated by commas.", ThnPathKeysEditor.PointParse(keys, 1, "1, 2").Error);
    }

    /// <summary>
    /// A label drag of a cutscene row changes one value of the text of the row (section 6.5 of the M20 design). A drag
    /// of 0 units gives the start text, so a drag back to its start is no edit. A drag of the yaw of an orientation by
    /// 40 points turns it by 20 degrees and writes the matrix of the new angles.
    /// </summary>
    [Fact]
    public void ALabelDragOfACutsceneRowChangesOneValueOfItsText()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        var pos = new ThnPath("spatialprops.pos");
        var orient = new ThnPath("spatialprops.orient");

        var start = ThnFieldEdit.Format(ThnKeyKind.Vector, Value(session, cam, pos.Text));
        Assert.Equal(start, FieldDrag.DragText(DragKind.Scaled, start, 3, 1, 0));
        Assert.Equal("0.05, 0, 0", FieldDrag.DragText(DragKind.Scaled, start, 3, 0, 5));
        Assert.Equal("12.5, 3.3, -40", FieldDrag.DragText(DragKind.Scaled, "12.5, 3.2, -40", 3, 1, 1));

        var angles = ThnFieldEdit.Format(ThnKeyKind.Orient, Value(session, cam, orient.Text));
        Assert.Equal("0, 0, 0", angles);
        var kind = FieldDrag.KindOf(ThnKeyKind.Orient, orient.Text)!.Value;
        var key = new FieldKey(cam, orient.Text, FieldKey.AllLines);
        var drag = ThnFieldGesture.Open(FakeCommandHost.Runner(session), key, angles,
            text => ThnFieldEdit.Parse(ThnKeyKind.Orient, text, Evaluator), ThnFieldCommand.Set(cam, orient));
        drag.Change(FieldDrag.DragText(kind, drag.StartText, 3, 1, 40));
        Assert.Null(drag.End(keep: true, interactive: true));

        Assert.Equal(ThnPose.Orient(MathHelper.MatrixFromEulerDegrees(new Vector3(0, 20, 0))),
            Value(session, cam, orient.Text));
        Assert.Equal("Set spatialprops.orient", session.UndoLabel);
    }

    private static ThnFieldGesture Gesture(EditSession session, ThnRef target, string start,
        string? noEditReason = null) =>
        ThnFieldGesture.Open(FakeCommandHost.Runner(session, noEditReason),
            new FieldKey(target, Fovh.Text, FieldKey.AllLines), start,
            text => ThnFieldEdit.Parse(ThnKeyKind.Number, text, Evaluator), ThnFieldCommand.Set(target, Fovh));

    /// <summary>A field of a number that the Inspector draws, with an edit.</summary>
    private static ThnFieldGesture NumberField(EditSession session, ThnRef target, string path, ThnFieldCommand command) =>
        ThnFieldGesture.Open(FakeCommandHost.Runner(session), new FieldKey(target, path, FieldKey.AllLines),
            ThnFieldEdit.Format(ThnKeyKind.Number, Value(session, target, path)),
            text => ThnFieldEdit.Parse(ThnKeyKind.Number, text, Evaluator), command);

    /// <summary>The script with a curve of three points on the first START_PSYS of Fx.</summary>
    private static string CurveScript(string targets = "{ \"Fx\" }") => Script.Replace(
        "{ 5, START_PSYS, { \"Fx\" }, { duration = 2 } }",
        $"{{ 5, START_PSYS, {targets}, {{ duration = 2, param_curve = {{ CLSID = \"FreeFormPCurve\", points = {{ " +
        "{ 0, 0, 0, 0 }, { 0.5, 0.25, 0, 0 }, { 1, 1, 0, 0 } } } } }");
}
