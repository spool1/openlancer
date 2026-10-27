using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.FieldBindingTests;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The commands of the fields (section 4.0.8 of the M6b design). Each field gesture of the Inspector runs them through
/// the command runner. It gives the same W and the same entry as the session commands of today.
/// </summary>
public sealed class FieldCommandsTests
{
    private static readonly SectionRef Package = Good(GoodsPath, "s1_package");
    private static readonly SectionRef Depot = SystemWorkspace.Li01Section("Object", "Li01_depot");
    private static readonly SectionRef Light = SystemWorkspace.Li01Section("LightSource", "Light_main");

    /// <summary>A typed text and a typed row of a key with more lines give the lines of today, as one entry.</summary>
    [Fact]
    public void ATextAndAListRowGiveTheLinesOfToday()
    {
        AssertGestureParity(() => Create(), ShipsPath, "Set mass",
            _ => EditCommand.SetLinesFromText(Ship("s1"), "mass", ["150"]),
            session =>
            {
                using var gesture = TestGestures.Line(session, Bind(session, Ship("s1"), "mass"));
                gesture.Change("15");
                gesture.Change("150");
                Assert.Null(gesture.End(keep: true, interactive: true));
            });
        AssertGestureParity(() => Create(), GoodsPath, "Set addon",
            _ => EditCommand.SetLinesFromText(Package, "addon", ["eng1, internal, 1", "gun1, HpWeapon02, 1"]),
            session =>
            {
                using var gesture = TestGestures.Line(session, Bind(session, Package, "addon"));
                gesture.Change("eng1, internal, 1\ngun1, HpWeapon02, 1");
                Assert.Null(gesture.End(keep: true, interactive: true));
            });
    }

    /// <summary>A label drag and a color pick write the line of the number rules of a gizmo drag, as one
    /// entry.</summary>
    [Fact]
    public void ALabelDragAndAColorPickGiveTheLinesOfToday()
    {
        var dragged = FieldDrag.DragText(DragKind.Position, "-38675, 0, -27705", 3, 0, 100m);
        Assert.True(FieldDrag.TryParse(dragged, 3, out var numbers));
        Assert.NotEqual(-38675, numbers[0]);
        AssertGestureParity(() => SystemWorkspace.Create(), SystemWorkspace.Li01Path, "Set pos",
            session => EditCommand.SetLines(Depot, "pos",
                [TransformValues.Line(session.Get(Depot)!.Lines("pos")[0].Values, numbers)]),
            session =>
            {
                using var drag = TestGestures.Number(session, Bind(session, Depot, "pos"), 3);
                foreach (var units in new[] { 10m, 55m, 100m })
                    drag.Change(FieldDrag.DragText(DragKind.Position, drag.StartText, 3, 0, units));
                Assert.Null(drag.End(keep: true, interactive: true));
            });
        AssertGestureParity(() => SystemWorkspace.Create(), SystemWorkspace.Li01Path, "Set color",
            session => EditCommand.SetLines(Light, "color",
                [TransformValues.Line(session.Get(Light)!.Lines("color")[0].Values, [255, 128, 0])]),
            session =>
            {
                using var pick = TestGestures.Number(session, Bind(session, Light, "color"), 3);
                pick.Change("200, 100, 0");
                pick.Change("255, 128, 0");
                Assert.Null(pick.End(keep: true, interactive: true));
            });
    }

    /// <summary>A bool field, a list drop, a None and a Revert of a row each give the line of today, as one
    /// entry.</summary>
    [Fact]
    public void TheButtonsOfARowGiveTheLinesOfToday()
    {
        AssertParity(() => Create(), WeaponsPath, s => s.Execute(EditCommand.SetLinesFromText(Gun("gun1"), "lootable", ["true"])),
            s => FieldCommands.SetLines(s, Gun("gun1"), "lootable", ["true"]));
        AssertParity(() => Create(), GoodsPath, s => s.Execute(EditCommand.SetLinesFromText(Good(GoodsPath, "eng1"),
                "equipment", ["gun1"])),
            s => FieldCommands.SetLines(s, Good(GoodsPath, "eng1"), "equipment", ["gun1"]));
        AssertParity(() => Create(), ShipsPath, s => s.Execute(EditCommand.RemoveName(Ship("s1"), "mass")),
            s => FieldCommands.RemoveKey(s, Ship("s1"), "mass"));

        using var ws = Layers();
        var session = ws.Session("act");
        Assert.Equal(["120"], Firsts(session, Ship("s1"), "mass"));
        var result = FakeCommandHost.Runner(session).Run(s => FieldCommands.RevertKey(s, Ship("s1"), "mass"));
        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Revert mass", session.UndoLabel);
        Assert.Equal(["100"], Firsts(session, Ship("s1"), "mass"));
        session.Undo();
        Assert.Equal(EditStatus.Applied, FakeCommandHost.Runner(session).Run(s => FieldCommands.RevertSection(s, Ship("s1")))
            .Status);
        Assert.Equal("Revert [Ship] nickname = s1", session.UndoLabel);
        Assert.Equal(["100"], Firsts(session, Ship("s1"), "mass"));
    }

    /// <summary>A revert of a [system] whose file the active mod sets refuses with the lock of the row, because it
    /// would move the system to another file. Through the runner, nothing changes.</summary>
    [Fact]
    public void ARevertOfASystemWhoseFileTheModSetsRefuses()
    {
        using var ws = SystemWorkspace.Create(SceneLi01);
        ws.Mod("act", new()
        {
            [@"patches\" + SystemWorkspace.UniversePath] = """
                [system]
                nickname = Li01
                file = Systems\Li01\moved.ini
                """,
        });
        var session = ws.Session("act");
        var li01 = SystemWorkspace.System("Li01");

        var plan = FieldCommands.RevertSection(session, li01);

        Assert.False(plan.CanRun);
        var error = Assert.Single(plan.Report(session).Errors);
        Assert.Equal((EditErrorKind.Refused, MissionErrors.FieldNotWritable), (error.Kind, error.Detail));
        Assert.Equal(FieldBinding.RevertLock(session, li01), error.Message);
        Assert.StartsWith("Changing the file of a system moves all its contents.", error.Message);
        var result = FakeCommandHost.Runner(session).Run(s => FieldCommands.RevertSection(s, li01));
        Assert.Equal(EditStatus.Refused, result.Status);
        Assert.False(session.CanUndo);
        Assert.Equal([@"Systems\Li01\moved.ini"], Firsts(session, li01, "file"));
    }

    /// <summary>A text of a text key with the sections that show the same text gives the text and the lines of today
    /// (review finding F12). An empty text and a key without a text id refuse.</summary>
    [Fact]
    public void ATextKeyWithItsSharedSectionsGivesTheTextOfToday()
    {
        var shared = new[] { Good(WeaponGoodsPath, "gun1") };
        AssertParity(() => Create(), WeaponGoodsPath,
            s => s.Execute(ItemEdits.SetText(s, Gun("gun1"), "ids_name", "Ion Gun", shared)),
            s => FieldCommands.SetText(s, Gun("gun1"), "ids_name", "Ion Gun", shared), WeaponsPath);

        using var ws = Create();
        var session = ws.Session("act");
        var plan = FieldCommands.SetText(session, Gun("gun1"), "ids_name", "Ion Gun", shared);
        Assert.Equal([Gun("gun1"), Good(WeaponGoodsPath, "gun1")], plan.Changes);
        FakeCommandHost.Runner(session).Run(plan);
        Assert.Equal("Ion Gun", session.Text(new StringPlaceholder(StringKind.String, "gun1")));

        var info = FakeCommandHost.Runner(session).Run(s => FieldCommands.SetText(s, Gun("gun1"), "ids_info", "Line 1\nLine 2"));
        Assert.Equal(EditStatus.Applied, info.Status);
        Assert.Equal(ItemEdits.ToInfocard("Line 1\nLine 2"),
            session.Text(new StringPlaceholder(StringKind.Infocard, "gun1.info")));

        var empty = FieldCommands.SetText(session, Gun("gun1"), "ids_name", "  ");
        Assert.False(empty.CanRun);
        Assert.Equal(FieldCommands.EmptyText, Assert.Single(empty.Refusals).Message);
        var noText = FieldCommands.SetText(session, Gun("gun1"), "hull_damage", "Gun");
        Assert.Equal("'hull_damage' does not hold the id of a text.", Assert.Single(noText.Refusals).Message);
        Assert.Equal(MissionErrors.NotTextId, Assert.Single(noText.Report(session).Errors).Detail);
    }

    /// <summary>The schema of the key checks a field text. A text that is not valid refuses with the reason, and an
    /// empty text removes the key. A text that a patch line cannot hold refuses in the plan and changes
    /// nothing.</summary>
    [Fact]
    public void AFieldTextGoesThroughTheSchemaOfTheKey()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var heavy = FieldCommands.SetLinesFromText(session, Ship("s1"), "MASS", "heavy");
        Assert.Contains("is not a number", Assert.Single(heavy.Refusals).Message);
        var empty = FieldCommands.SetLinesFromText(session, Ship("s1"), "mass", "");
        Assert.Equal("Remove mass", empty.Label);

        var runner = FakeCommandHost.Runner(session);
        Assert.Equal(EditStatus.Applied, runner.Run(s => FieldCommands.SetLinesFromText(s, Ship("s1"), "MASS", " 150 ")).Status);
        Assert.Equal("Set mass", session.UndoLabel);
        Assert.Equal(["150"], Firsts(session, Ship("s1"), "mass"));
        var semicolon = FieldCommands.SetLines(session, Ship("s1"), "da_archetype", [@"ships\s1;old.cmp"]);
        Assert.False(semicolon.CanRun);
        Assert.Equal(EditStatus.Refused, runner.Run(semicolon).Status);
        Assert.Equal([@"ships\s1.cmp"], Firsts(session, Ship("s1"), "da_archetype"));
        Assert.Equal("Set mass", session.UndoLabel);
    }

    /// <summary>
    /// The request form of an agent: each JSON value form, all keys in one entry, and a key that does not change. The
    /// result names the section that changed.
    /// </summary>
    [Fact]
    public void SetFieldsTakesTheJsonFormsOfAValueAsOneEntry()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = FieldCommands.SetFields(session, Ship("s1"), Fields("""
            { "mass": 250.5, "ids_info": 200, "steering_torque": [1, 2.5, 3], "hold_size": null, "da_archetype": "ships\\s2.cmp" }
            """));
        var result = FakeCommandHost.Runner(session).Run(plan);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Set 5 fields of s1", session.UndoLabel);
        Assert.Single(result.Changes);
        Assert.Equal(["250.5"], Firsts(session, Ship("s1"), "mass"));
        Assert.Equal(["1"], Firsts(session, Ship("s1"), "steering_torque"));
        Assert.Equal("1, 2.5, 3", FieldEdit.FormatLine(session.Get(Ship("s1"))!.Lines("steering_torque")[0].Values));
        Assert.Equal([@"ships\s2.cmp"], Firsts(session, Ship("s1"), "da_archetype"));
        Assert.Equal(["200"], Firsts(session, Ship("s1"), "ids_info"));
        session.Undo();
        Assert.False(session.CanUndo);

        var rows = FieldCommands.SetFields(session, Package, Fields("""
            { "addon": [["eng1", "internal", 1], ["gun1", "HpWeapon02", 1]] }
            """));
        FakeCommandHost.Runner(session).Run(rows);
        Assert.Equal("Set addon", session.UndoLabel);
        Assert.Equal(["eng1, internal, 1", "gun1, HpWeapon02, 1"],
            session.Get(Package)!.Lines("addon").Select(l => FieldEdit.FormatLine(l.Values)));
        var none = FieldCommands.SetFields(session, Package, Fields("""{ "addon": [] }"""));
        FakeCommandHost.Runner(session).Run(none);
        Assert.Empty(session.Get(Package)!.Lines("addon"));

        var same = FakeCommandHost.Runner(session).Run(FieldCommands.SetFields(session, Ship("s1"),
            Fields("""{ "mass": 100 }""")));
        Assert.Equal(EditStatus.NoChange, same.Status);
        Assert.Equal("Remove addon", session.UndoLabel);
    }

    /// <summary>
    /// A value of a form that the tool does not take throws the error of its kind. So do a key that the section and the
    /// schema do not have, and a value that the schema refuses. A comma-separated text refuses (AGENTS.md UI rule 3).
    /// Nothing changes.
    /// </summary>
    [Fact]
    public void SetFieldsRefusesAValueThatTheFieldCannotWrite()
    {
        using var ws = Create();
        var session = ws.Session("act");

        AssertError(session, Ship("s1"), """{ "mass": "heavy" }""", EditErrorKind.InvalidValue, MissionErrors.FieldValue,
            "mass");
        var comma = AssertError(session, Package, """{ "addon": "eng1, internal, 1" }""", EditErrorKind.InvalidValue,
            MissionErrors.FieldValue, "addon");
        Assert.Equal("A value of 'addon' holds ',' or a line break. Give the values of one line as an array, for " +
                     "example [1, 2, 3]. Give more lines as an array of arrays.", comma.Message);
        AssertError(session, Ship("s1"), """{ "mass": "" }""", EditErrorKind.InvalidValue, MissionErrors.FieldValue, "mass");
        AssertError(session, Ship("s1"), """{ "mass": { "value": 1 } }""", EditErrorKind.InvalidArgument,
            MissionErrors.ValueKind, "mass");
        AssertError(session, Ship("s1"), """{ "mass": [1, [2]] }""", EditErrorKind.InvalidArgument,
            MissionErrors.ValueKind, "mass");
        AssertError(session, Ship("s1"), """{ "mass": [[]] }""", EditErrorKind.InvalidArgument,
            MissionErrors.ValueMissing, "mass");
        AssertError(session, Ship("s1"), """{ "mass": [1, 2] }""", EditErrorKind.InvalidValue, MissionErrors.FieldValue,
            "mass");
        AssertError(session, Ship("s1"), """{ "no_such_key": 1 }""", EditErrorKind.InvalidArgument,
            MissionErrors.UnknownField, "no_such_key");
        AssertError(session, Ship("s1"), """{ "mass": 1, "MASS": 2 }""", EditErrorKind.InvalidArgument,
            MissionErrors.ValueKind, "mass");
        AssertError(session, Ship("s1"), "{}", EditErrorKind.InvalidArgument, MissionErrors.ValueMissing, null);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    /// <summary>
    /// A value that a patch line cannot hold refuses before the edit runs: a ';', a character that is not ASCII, and a
    /// control character such as a tab. The plan of the field command refuses with the detail FieldValue, and the
    /// request of an agent throws the error invalid_value. Nothing changes.
    /// </summary>
    [Fact]
    public void AValueThatAPatchLineCannotHoldRefusesInThePlan()
    {
        using var ws = Create();
        var session = ws.Session("act");

        foreach (var json in new[]
                 {
                     """{ "da_archetype": "ships\\s1;old.cmp" }""",
                     """{ "da_archetype": "ships\\sé.cmp" }""",
                     """{ "da_archetype": "ships\\s1\t.cmp" }""",
                 })
        {
            var error = Assert.Throws<EditException>(() => FieldCommands.SetFields(session, Ship("s1"), Fields(json)))
                .Error;
            Assert.Equal((EditErrorKind.InvalidValue, MissionErrors.FieldValue, "da_archetype"),
                (error.Kind, error.Detail, error.Field));
            Assert.Contains("a value must be ASCII text with no ';' and no control character", error.Message);
        }

        var plan = FieldCommands.SetLines(session, Ship("s1"), "da_archetype", [@"ships\s1;old.cmp"]);
        Assert.False(plan.CanRun);
        Assert.Equal(MissionErrors.FieldValue, Assert.Single(plan.Report(session).Errors).Detail);
        Assert.Equal(EditStatus.Refused, FakeCommandHost.Runner(session).Run(plan).Status);
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    /// <summary>
    /// A key that the Inspector cannot change refuses. Examples are the key line (use the rename), a key of a group of
    /// lines that belong together, and the file of a system. The tool get_item marks the same keys as locked.
    /// </summary>
    [Fact]
    public void SetFieldsRefusesTheLockedKeysThatGetItemMarks()
    {
        using var ws = SystemWorkspace.Create();
        var session = ws.Session("act");
        var zone = SystemWorkspace.Li01Section("Zone", "Zone_death");
        var system = SystemWorkspace.System("Li01");

        var nickname = FieldCommands.SetFields(session, Depot, Fields("""{ "nickname": "Li01_store" }"""));
        Assert.False(nickname.CanRun);
        var error = Assert.Single(nickname.Report(session).Errors);
        Assert.Equal((EditErrorKind.Refused, MissionErrors.KeyLine), (error.Kind, error.Detail));
        Assert.Contains("rename_item", error.Message);
        var encounter = FieldCommands.SetFields(session, zone, Fields("""{ "encounter": ["area_scout", 5, 0.5] }"""));
        Assert.Equal(MissionErrors.FieldNotWritable, Assert.Single(encounter.Report(session).Errors).Detail);
        var file = FieldCommands.SetFields(session, system, Fields("""{ "file": "Systems\\Li01\\new.ini" }"""));
        Assert.Equal(MissionErrors.FieldNotWritable, Assert.Single(file.Report(session).Errors).Detail);

        var view = session.Get(Depot)!;
        Assert.True(FieldCommands.IsLocked(session, Depot, view, "nickname"));
        Assert.False(FieldCommands.IsLocked(session, Depot, view, "archetype"));
        Assert.True(FieldCommands.IsLocked(session, zone, session.Get(zone)!, "encounter"));
        Assert.True(FieldCommands.IsLocked(session, system, session.Get(system)!, "file"));
        Assert.False(FieldCommands.IsLocked(session, system, session.Get(system)!, "navmapscale"));
        Assert.Equal(EditStatus.Applied, FakeCommandHost.Runner(session).Run(FieldCommands.SetFields(session, system,
            Fields("""{ "navmapscale": 2 }"""))).Status);
    }

    /// <summary>A session that takes no edits refuses each field of a section with the reason of the session.</summary>
    [Fact]
    public void SetFieldsRefusesEachFieldOfAReadOnlySection()
    {
        using var ws = Layers();
        var session = EditSession.Open(Data.Mods.ModProfile.Load(ws.ProfilePath), null, TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator);

        var plan = FieldCommands.SetFields(session, Ship("s1"), Fields("""{ "mass": 1 }"""));

        var error = Assert.Single(plan.Report(session).Errors);
        Assert.Equal(MissionErrors.ReadOnlySection, error.Detail);
        Assert.Equal(session.ReadOnlyReason, error.Message);
    }

    /// <summary>The section part of the W of a file: each section with its lines, in file order.</summary>
    private static string[] W(EditSession session, string path) =>
        session.Sections(path).SelectMany(s => session.Get(s)!.Entries
            .Select(e => $"{e.Name} = {FieldEdit.FormatLine(e.Values)}").Prepend($"[{s.Id}]")).ToArray();

    /// <summary>
    /// The last step of a gesture of today runs in a transaction of the session. The gesture of the Inspector runs
    /// through the command runner. Both give the same W of a file, and one entry with the same label and the origin
    /// User.
    /// </summary>
    private static void AssertGestureParity(Func<TestWorkspace> create, string path, string label,
        Func<EditSession, EditCommand> today, Action<EditSession> gesture)
    {
        using var a = create();
        var expected = a.Session("act");
        using (var transaction = expected.Begin(label))
        {
            transaction.Apply(today(expected));
            transaction.Commit();
        }

        using var b = create();
        var session = b.Session("act");
        gesture(session);

        Assert.Equal(W(expected, path), W(session, path));
        Assert.Equal(label, session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.False(session.InTransaction);
        session.Undo();
        Assert.False(session.CanUndo);
    }

    /// <summary>A session command of today and the command through the runner give the same W of the files. Both give
    /// one entry with the same label, and the runner gives the edit origin User.</summary>
    private static void AssertParity(Func<TestWorkspace> create, string path, Action<EditSession> today,
        Func<EditSession, EditPlan> command, params string[] paths)
    {
        using var a = create();
        var expected = a.Session("act");
        today(expected);

        using var b = create();
        var session = b.Session("act");
        var host = new FakeCommandHost(session);
        var result = new CommandRunner(host).Run(command);

        Assert.Equal(EditStatus.Applied, result.Status);
        foreach (var file in paths.Prepend(path))
            Assert.Equal(W(expected, file), W(session, file));
        Assert.Equal(expected.UndoLabel, session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.Empty(host.Log.Entries);
    }

    private static EditError AssertError(EditSession session, SectionRef section, string json, EditErrorKind kind,
        string detail, string? field)
    {
        var error = Assert.Throws<EditException>(() => FieldCommands.SetFields(session, section, Fields(json))).Error;
        Assert.Equal((kind, detail, field), (error.Kind, error.Detail, error.Field));
        return error;
    }

    private static IReadOnlyList<KeyValuePair<string, JsonNode?>> Fields(string json) =>
        JsonNode.Parse(json)!.AsObject().ToList();
}
