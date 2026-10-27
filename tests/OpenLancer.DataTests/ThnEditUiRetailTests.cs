using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using LibreLancer;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Scene;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;
using OpenLancer.Tests;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Each kind of edit of the cutscene editor through its command path, on s002xe in a temporary mod (step 7 of the M8
/// design): the drags of the Timeline, the fields, checkboxes and lists of the Inspector, Add event, paste, duplicate,
/// delete, Move before, the curve editor, the path keys, the gizmo, Set camera from view, and the rename. Each edit is one
/// entry of the history. The save writes a patch that has only the entries of these edits, and every other value of
/// the cutscene stays the same object.
/// </summary>
public sealed class ThnEditUiRetailTests(ITestOutputHelper output)
{
    private const string S002xe = ThnPatchRetailTests.S002xe;
    private static readonly GizmoSnap Off = new(false, 1, 15, 1);

    static ThnEditUiRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EachKindOfUiEditGoesThroughItsCommandAndThePatchHasOnlyTheseEntries()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var baseFile = Path.Combine(baseFolder, S002xe);
        var hashBefore = SHA256.HashData(File.ReadAllBytes(baseFile));
        using var ws = new RetailWorkspace(baseFolder, "m8proof");
        var session = ws.Open();
        var symbols = ThornEvaluator.Instance.Symbols;
        var lower = session.LowerCutscene(S002xe)!;
        var expected = new Expected();
        var labels = new List<string>();

        ThnScriptModel W() => session.Cutscene(S002xe)!;
        TimelineModel Model() => TimelineModel.Build(S002xe, W(), symbols, ScriptPlay.Normal);
        ThnRef Entity(string name) => new(S002xe, ThnElementKind.Entity, W().EntityNamed(name)!.Id);
        ThnRef Event(float time, string type, params string[] targets) => new(S002xe, ThnElementKind.Event, W().Events.Single(e =>
            e.Table.List[0] == new ThnNumber(time) && ThnSchema.TypeOf(ThnElementKind.Event, e.Table, symbols) == type &&
            ((ThnTable)e.Table.List[2]).List.Select(t => ((ThnText)t).Value).SequenceEqual(targets)).Id);
        ThnValue? Value(ThnRef element, string path) =>
            new ThnPath(path).ValueIn(element.Kind, W().Element(element.Kind, element.Id)!.Table);
        void Done(string label)
        {
            Assert.Equal(label, session.UndoLabel);
            labels.Add(label);
        }

        // The Timeline: a move drag, a drag of the start and of the end of a bar, and a drag of the end handle.
        var cut = Event(25.75f, "SET_CAMERA", "Cam_Monitor_s002xe", "Cam_03_ACOH_Trent_Medics_A");
        Drag(TimelineDragKind.Move, cut, 25.75, 27);
        Done("Move event");
        expected.Change(cut);
        var dust = Event(0, "START_PSYS", "vfx_rtc_shipdust_2x");
        Drag(TimelineDragKind.Start, dust, 0, 0.5);
        Done("Change the start of event");
        expected.Change(dust);
        var mix = Event(0.001f, "START_SOUND", "mix_s002xe_2");
        Drag(TimelineDragKind.End, mix, Model().Event(mix.Id)!.End, Model().Event(mix.Id)!.End - 1);
        Done("Change the length of event");
        expected.Change(mix);
        using (var duration = TimelineDrag.BeginDuration(session, S002xe, W().Duration, W().Duration, []))
        {
            duration.Drag(95, false, 0);
            duration.End();
        }

        Done("Set duration");

        // The Inspector: a number, a value of a vector, a rotation as Euler angles, the time of an event, the entity
        // flags, the flags of an event, a list of names, a checkbox and a target.
        var trentA = Entity("Cam_02_Intro_Trent_A");
        Field(trentA, "cameraprops.fovh", ThnKeyKind.Number, _ => "20");
        Done("Set cameraprops.fovh");
        expected.Change(trentA);
        var syd = Entity("Char_syd");
        Field(syd, "spatialprops.pos", ThnKeyKind.Vector, start => PropertyGrid.ComponentText(start, 3, 1, "1.5"));
        Done("Set spatialprops.pos");
        Field(syd, "spatialprops.orient", ThnKeyKind.Orient, _ => "0, 90, 0");
        Done("Set spatialprops.orient");
        expected.Change(syd);
        var news = Event(3.592f, "START_PSYS", "vfx_rtc_libertynewsicon_1");
        Field(news, "time", ThnKeyKind.Number, _ => "3.6004", ThnFieldCommand.Time(news, 3.592f));
        Done("Move event");
        Assert.Equal(new ThnNumber(3.6f), Value(news, "time"));
        expected.Change(news);
        var medic = Entity("Char_medic_1");
        var medicTable = W().Element(ThnElementKind.Entity, medic.Id)!.Table;
        session.Execute(ThnEdits.SetFlags(medic, new HashSet<string>(ThnFlags.On(medicTable[ThnFlags.Key],
            ThnFlags.IsSound(medicTable, symbols), symbols)) { ThnFlags.Hidden }));
        Done("Set flags");
        expected.Change(medic);
        var lonnigan = Event(0, "ATTACH_ENTITY", "Prop_Char_Lonnigan", "Mrk_offscreen_Location");
        var attachFlags = ThnSchema.Find(ThnElementKind.Event, "ATTACH_ENTITY", new ThnPath("props.flags"))!;
        var flags = Value(lonnigan, "props.flags");
        session.Execute(ThnEdits.Set(lonnigan, attachFlags.Path, ThnFlags.WriteBits(flags, attachFlags,
            new HashSet<string>(ThnFlags.BitsOn(flags, attachFlags, symbols)) { "ORIENTATION" }, symbols)!));
        Done("Set props.flags");
        expected.Change(lonnigan);
        // s002xe stores these two values as a name and as Y, so the edits keep a name and a bool. A stored number stays a
        // number (ThnFieldEditTests).
        var maglev = Event(0, "ATTACH_ENTITY", "Prop_Maglev", "Mrk_offscreen_Location");
        Assert.IsType<ThnSymbol>(Value(maglev, "props.target_type"));
        session.Execute(ThnEdits.Set(maglev, new ThnPath("props.target_type"),
            ThnFieldEdit.SymbolValue(Value(maglev, "props.target_type"), "PART", symbols)));
        Done("Set props.target_type");
        Assert.Equal(new ThnSymbol("PART"), Value(maglev, "props.target_type"));
        expected.Change(maglev);
        var sky = Entity("ambi_LtG04_Direct_City_Sky_6ocTransport");
        Assert.Equal(new ThnBool(true), Value(sky, "lightprops.on"));
        session.Execute(ThnEdits.Set(sky, new ThnPath("lightprops.on"),
            ThnFieldEdit.BoolValue(Value(sky, "lightprops.on"), on: false)));
        Done("Set lightprops.on");
        Assert.Equal(new ThnBool(false), Value(sky, "lightprops.on"));
        expected.Change(sky);
        var staticCut = Event(3.281f, "SET_CAMERA", "Cam_Monitor_s002xe_min_spec", "Cam_01_VideoCam_A_static_mon_b");
        session.Execute(ThnEdits.Set(staticCut, new ThnPath("targets"),
            ThnFieldEdit.TargetsValue((ThnTable)Value(staticCut, "targets")!, 1, "Cam_01_VideoCam_A")));
        Done("Set targets");
        expected.Change(staticCut);

        // Add event with a template, the gizmo on the target pose of the new spatial animation, paste, duplicate,
        // delete, the delete of an entity with its events, and Move before.
        session.Execute(Add("Cam_02_Intro_Trent_A", "START_CAMERA_PROP_ANIM", 14));
        Done("Add START_CAMERA_PROP_ANIM");
        session.Execute(Add("Char_Extra_01", "START_SPATIAL_PROP_ANIM", 1));
        Done("Add START_SPATIAL_PROP_ANIM");
        expected.News += 2;
        var anim = new ThnRef(S002xe, ThnElementKind.Event, W().Events[^1].Id);
        Assert.Equal(anim, ThnGizmoTarget.Subject(W(), anim, symbols, out _));
        Gizmo(anim, GizmoTool.Move, Matrix4x4.CreateTranslation(0, 1, 0));
        Done("Move the target pose of Char_Extra_01");
        var clip = TimelineEdits.Copy(W(), [Event(0.312f, "START_PSYS", "gf_ci_smallengine_EXTRA_SHIP")]);
        session.Execute(TimelineEdits.Paste(S002xe, clip, 50, "vfx_TV_Lines_2"));
        Done("Paste event");
        session.Execute(TimelineEdits.Duplicate(S002xe,
            TimelineEdits.Copy(W(), [Event(2.5f, "ATTACH_ENTITY", "vfx_TV_Lines_2", "Cam_01_VideoCam_A")])));
        Done("Duplicate event");
        // The copy goes after its source, so the key of the source still names one event.
        Assert.NotNull(Event(2.5f, "ATTACH_ENTITY", "vfx_TV_Lines_2", "Cam_01_VideoCam_A"));
        expected.News += 2;
        var distortion = Event(4.684f, "START_PSYS", "vfx_TV_Distortion_2");
        session.Execute(ThnEdits.Delete(distortion));
        Done("Delete event");
        expected.Delete(distortion);
        var cigarette = Entity("prop_cigarette_2");
        var cigaretteEvents = TimelineEdits.EventsNaming(S002xe, W(), "prop_cigarette_2");
        session.Execute(TimelineEdits.DeleteWithEvents(cigarette, cigaretteEvents));
        Done("Delete entity and 5 events");
        expected.Delete(cigarette);
        foreach (var ev in cigaretteEvents)
            expected.Delete(ev);
        var fadeStatic = Event(3.092f, "START_PSYS_PROP_ANIM", "vfx_rtc_fadeblack_variable_static_cam");
        var fade = Event(3.092f, "START_PSYS_PROP_ANIM", "vfx_rtc_fadeblack_variable_1");
        Assert.Null(ThnEdits.ReorderRefusal(session, fadeStatic, fade));
        var kept = W().Events.Where(e => e.Origin >= 0).Select(e => e.Id).ToHashSet();
        session.Execute(ThnEdits.Reorder(fadeStatic, fade));
        Done("Move event before");
        var renewed = W().Events.Where(e => e.Origin < 0 && kept.Contains(e.Id)).ToList();
        Assert.Equal([fade.Id], renewed.Select(e => e.Id));
        expected.Delete(fade);
        expected.News += renewed.Count;

        // The curve editor and the path keys.
        var zoom = Event(11.281f, "START_CAMERA_PROP_ANIM", "Cam_01_VideoCam_A", "Cam_01_VideoCam_D_Zoom");
        using (var point = CurvePointDrag.Begin(session, zoom, 1))
        {
            point.Move(0.45f, 0.6f);
            point.End();
        }

        Done("Move point 2 of the curve");
        expected.Change(zoom);
        var pathEntity = Entity("Cam_01_VideoCam_Path");
        var keys = ThnMotionPath.Parse(((ThnText)Value(pathEntity, "pathprops.path_data")!).Value, ThornEvaluator.Instance);
        var pathKey = new FieldKey(pathEntity, ThnMotionPath.Path.Text, 2);
        var keyStart = ThnFieldEdit.Format(ThnKeyKind.Vector, ThnPose.Vector(keys.Keys[1].Point));
        var keyEdit = new ThnFieldGesture(session, pathKey, keyStart, text => ThnPathKeysEditor.PointParse(keys, 1, text),
            ThnFieldCommand.Set(pathEntity, ThnMotionPath.Path));
        keyEdit.Change(PropertyGrid.ComponentText(keyStart, 3, 2, "0.25"));
        Assert.Null(keyEdit.End(keep: true, interactive: true));
        Done("Set pathprops.path_data");
        expected.Change(pathEntity);

        // The gizmo: a start pose, and the second target of a spatial animation. Set camera from view.
        var trent = Entity("Char_trent");
        Gizmo(trent, GizmoTool.Move, Matrix4x4.CreateTranslation(0.5f, 0, 0));
        Done("Move Char_trent");
        expected.Change(trent);
        var intro = Event(13.812f, "START_SPATIAL_PROP_ANIM", "Cam_02_Intro_Trent_A", "Cam_02_Intro_Trent_B");
        var trentB = ThnGizmoTarget.Subject(W(), intro, symbols, out _)!;
        Assert.Equal(Entity("Cam_02_Intro_Trent_B"), trentB);
        Gizmo(trentB, GizmoTool.Rotate, Matrix4x4.CreateFromYawPitchRoll(0.2f, 0, 0));
        Done("Rotate Cam_02_Intro_Trent_B");
        expected.Change(trentB);
        var medics = Entity("Cam_03_ACOH_Trent_Medics_A");
        session.Execute(ThnGizmoTarget.SetStartPose(medics, new ThnPoseValue(new Vector3(52, 1.75f, -12),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2)), "Set camera from view"));
        Done("Set camera from view");
        expected.Change(medics);

        // The rename: no other data uses the name, so it runs at once, and it changes the events that name the entity.
        var extra = Entity("Char_Extra_03");
        Assert.Empty(CutsceneRename.Uses(ThnReferences.Scan(session.EditView.CreateFileSystem()), S002xe, "Char_Extra_03",
            _ => null, symbols));
        var naming = TimelineEdits.EventsNaming(S002xe, W(), "Char_Extra_03");
        session.Execute(ThnEdits.RenameEntity(extra, "Char_Extra_03_renamed"));
        Done("Rename to Char_Extra_03_renamed");
        expected.Change(extra);
        foreach (var ev in naming)
            expected.Change(ev);

        // Each edit is one entry of the history: the undo of each entry gives B, and the redo gives W again.
        var w = W();
        var undone = 0;
        while (session.CanUndo)
        {
            session.Undo();
            undone++;
        }

        Assert.Equal(labels.Count, undone);
        Assert.Equal(lower, W());
        for (var i = 0; i < undone; i++)
            session.Redo();
        Assert.Equal(w, W());

        // Every value that no edit changed is the same object as in B.
        foreach (var kind in new[] { ThnElementKind.Entity, ThnElementKind.Event })
        {
            var byOrigin = w.Elements(kind).Where(e => e.Origin >= 0).ToDictionary(e => e.Origin);
            var list = lower.Elements(kind);
            for (var i = 0; i < list.Count; i++)
            {
                var id = list[i].Id;
                if (expected.Deleted.Contains(id))
                    Assert.False(byOrigin.ContainsKey(i));
                else if (expected.Changed.Contains(id))
                    Assert.NotEqual(list[i].Table, byOrigin[i].Table);
                else
                    Assert.Same(list[i].Table, byOrigin[i].Table);
            }
        }

        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Empty(saved.Problems);
        var patchFile = Assert.Single(saved.Written);
        var bytes = File.ReadAllBytes(patchFile);
        var patch = ThnPatch.Read(bytes, patchFile, ThornEvaluator.Instance);
        output.WriteLine($"{labels.Count} edits, one entry each: {string.Join("; ", labels)}");
        output.WriteLine($"{patchFile} ({bytes.Length} bytes): {patch.Entities.Length} entity entries, " +
                         $"{patch.Events.Length} event entries.");

        // The patch has only the entries of the edits.
        string Key(ThnElementKind kind, ThnElementId id) => ThnKeys.Text(ThnKeys.Match(kind, lower.Element(kind, id)!.Table)!);
        foreach (var kind in new[] { ThnElementKind.Entity, ThnElementKind.Event })
        {
            var entries = kind == ThnElementKind.Entity ? patch.Entities : patch.Events;
            var ids = lower.Elements(kind).Select(e => e.Id).ToHashSet();
            Assert.Equal(expected.Changed.Where(ids.Contains).Select(id => Key(kind, id)).Order(),
                entries.Where(e => !e.Delete && e.New == null).Select(e => ThnKeys.Text(e.Match!)).Order());
            Assert.Equal(expected.Deleted.Where(ids.Contains).Select(id => Key(kind, id)).Order(),
                entries.Where(e => e.Delete).Select(e => ThnKeys.Text(e.Match!)).Order());
            Assert.Equal(kind == ThnElementKind.Event ? expected.News : 0, entries.Count(e => e.New != null));
        }

        Assert.Equal(95f, patch.Duration);
        output.WriteLine($"Changed: {expected.Changed.Count(id => lower.Element(ThnElementKind.Entity, id) != null)} " +
                         $"entities, {expected.Changed.Count(id => lower.Element(ThnElementKind.Event, id) != null)} events. " +
                         $"Deleted: {expected.Deleted.Count} elements. New: {expected.News} events. Duration: 95.");

        // The merged file evaluates to W bit for bit, the engine reads it, and the base file did not change.
        var merged = session.EditView.CreateFileSystem().ReadAllBytes(S002xe);
        Assert.Null(ThnValues.FirstDifference(W().ToGlobals(), ThornEvaluator.Instance.Evaluate(merged, S002xe)));
        var script = new ThnScript(merged, include => throw new FileNotFoundException(include), S002xe);
        Assert.Equal(20, script.Entities["Cam_02_Intro_Trent_A"].FovH);
        Assert.True(script.Entities.ContainsKey("Char_Extra_03_renamed"));
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(baseFile)));

        void Drag(TimelineDragKind kind, ThnRef ev, double grab, double to)
        {
            var model = Model();
            using var drag = TimelineDrag.Begin(session, [model.Event(ev.Id)!], kind, grab,
                TimelineDrag.SnapPoints(model, [ev], 0));
            for (var step = 1; step <= 10; step++)
                drag.Drag(grab + (to - grab) * step / 10, snap: false, tolerance: 0);
            drag.End();
        }

        // A field of the Inspector: the Set of its path, or the edit that the page gives the field.
        void Field(ThnRef target, string path, ThnKeyKind kind, Func<string, string> text, ThnFieldCommand? command = null)
        {
            var start = ThnFieldEdit.Format(kind, Value(target, path));
            var edit = new ThnFieldGesture(session, new FieldKey(target, path, FieldKey.AllLines), start,
                value => ThnFieldEdit.Parse(kind, value, ThornEvaluator.Instance),
                command ?? ThnFieldCommand.Set(target, new ThnPath(path)));
            edit.Change(text(start));
            Assert.Null(edit.End(keep: true, interactive: true));
        }

        EditCommand Add(string entity, string type, double time)
        {
            var choice = TimelineEdits.AddChoices(W(), entity, symbols).Single(c => c.Role.Type == type);
            return TimelineEdits.Add(S002xe, choice, entity, time);
        }

        void Gizmo(ThnRef element, GizmoTool tool, Matrix4x4 change)
        {
            var target = ThnGizmoTarget.Of(session, new ThnGizmoKey(element), null, out var reason) ??
                         throw new InvalidOperationException(reason);
            using var drag = TestGestures.Drag(session, target, tool, GizmoSpace.World, null);
            drag.Update(target.Matrix(tool) * change, Off);
            drag.End();
        }
    }

    /// <summary>The elements of B that the edits change or delete, and the number of new events.</summary>
    private sealed class Expected
    {
        public HashSet<ThnElementId> Changed { get; } = [];

        public HashSet<ThnElementId> Deleted { get; } = [];

        public int News { get; set; }

        public void Change(ThnRef element) => Changed.Add(element.Id);

        public void Delete(ThnRef element)
        {
            Changed.Remove(element.Id);
            Deleted.Add(element.Id);
        }
    }
}
