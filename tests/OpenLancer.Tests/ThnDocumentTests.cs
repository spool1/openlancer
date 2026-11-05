using System;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The cutscene document of an edit session: B, W and the pins of the patch of the active mod, the kinds of file, the
/// read-only reasons, and the state after a save.
/// </summary>
public sealed class ThnDocumentTests
{
    private static readonly ThnPath Fovh = new("cameraprops.fovh");

    /// <summary>A patch that sets the camera to its lower values and the duration to its lower value. So W equals B
    /// there, and only the pins keep these values in the patch.</summary>
    private const string PinPatch = """
        -- Values that a lower mod must not change.
        ol_thn_patch = 1
        duration = 10
        entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 10, ["cameraprops.hvaspect"] = 2 } } }
        """;

    private const string NewEventPatch = """
        ol_thn_patch = 1
        entities = { { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20 }, ol_remove = { "cameraprops.farplane" } } }
        events = { { ol_new = { 7, START_PSYS, { "Fx" }, { duration = 1 } } } }
        """;

    private static ThnDocument Document(EditSession session) => session.CutsceneDocument(ScriptPath)!;

    [Fact]
    public void ACutsceneWithoutAFileOfTheActiveModIsTheLowerCutscene()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        Assert.Same(session.LowerCutscene(ScriptPath), session.Cutscene(@"data/scripts/X.THN"));
        Assert.Null(ThnValues.FirstDifference(Evaluate(Script), W(session).ToGlobals()));
        Assert.Equal(ThnFileKind.Lower, session.CutsceneKind(ScriptPath));
        Assert.Null(session.CutsceneReadOnlyReason(ScriptPath));
        Assert.Equal([ScriptPath], session.OpenCutscenes);
        Assert.Empty(Document(session).State.Pins);
        Assert.Null(session.Cutscene(@"DATA\SCRIPTS\missing.thn"));
        Assert.Equal(@"The edit view has no cutscene DATA\SCRIPTS\missing.thn.", session.CutsceneReadOnlyReason(@"DATA\SCRIPTS\missing.thn"));
        Assert.Null(session.Cutscene(EditSessionTests.GoodsPath));

        // A session without an active mod shows the cutscene and takes no edits.
        var viewer = ws.SecondSession(null);
        Assert.NotNull(viewer.Cutscene(ScriptPath));
        Assert.Equal("There is no active mod. Select a mod to edit.", viewer.CutsceneReadOnlyReason(ScriptPath));
        Assert.Throws<InvalidOperationException>(() => viewer.Execute(ThnEdits.Set(Entity(viewer, "Cam_A"), Fovh, N(1))));
    }

    [Fact]
    public void ACutsceneWithAPatchOpensWithThePatchAndItsPins()
    {
        using var ws = Workspace(new() { [PatchFile] = NewEventPatch });
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        Assert.Equal(ThnFileKind.PatchedHere, session.CutsceneKind(ScriptPath));
        var expected = Script.Replace("fovh = 10", "fovh = 20").Replace(", farplane = 100", "")
            .Replace("{ \"Cam_A\", \"char_trent\" } }", "{ \"Cam_A\", \"char_trent\" } },\n{ 7, START_PSYS, { \"Fx\" }, { duration = 1 } }");
        Assert.Null(ThnValues.FirstDifference(Evaluate(expected), W(session).ToGlobals()));
        Assert.Equal(-1, W(session).Events[^1].Origin);
        Assert.Equal([new ThnPin(cam.Id, new ThnPath("cameraprops.farplane")), new ThnPin(cam.Id, Fovh)],
            Document(session).State.Pins.OrderBy(p => p.Path.Text, StringComparer.Ordinal));
        // The edit view shows the same cutscene.
        Assert.Equal(ThnWriter.Write(W(session).ToGlobals()), ModOverlayThnTests.Merged(session.EditView));
    }

    [Fact]
    public void ACutsceneWithAWholeFileOpensAsTheFileWithoutOrigins()
    {
        var whole = Script.Replace("duration = 10", "duration = 12");
        const string newPath = @"DATA\MODS\act\CUTSCENES\new.thn";
        using var ws = Workspace(new() { [WholeFile] = whole, [@"files\" + newPath] = whole });
        var session = ws.Session("act");

        Assert.Equal(ThnFileKind.WholeFileHere, session.CutsceneKind(ScriptPath));
        Assert.Equal(10, session.LowerCutscene(ScriptPath)!.Duration);
        Assert.Equal(12, W(session).Duration);
        Assert.All(W(session).Entities.Concat(W(session).Events), e => Assert.Equal(-1, e.Origin));
        Assert.Equal(ThnFileKind.NewHere, session.CutsceneKind(newPath));
        Assert.Null(session.LowerCutscene(newPath));

        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(40)));
        session.Execute(ThnEdits.SetDuration(newPath, 3));
        var revert = Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Revert(ScriptPath)));
        Assert.Equal("The active mod has a whole file for this cutscene, so there are no lower values to put back.", revert.Message);
        AssertSaved(session.Save());

        // A save writes the canonical text of W into the whole file.
        Assert.Equal(ThnWriter.Write(W(session).ToGlobals()), File.ReadAllText(Path.Combine(ws.ModFolder("act"), WholeFile)));
        Assert.Equal(ThnWriter.Write(session.Cutscene(newPath)!.ToGlobals()),
            File.ReadAllText(Path.Combine(ws.ModFolder("act"), "files", newPath)));
        Assert.Equal(3, session.Cutscene(newPath)!.Duration);
        Assert.False(File.Exists(Path.Combine(ws.ModFolder("act"), PatchFile)));
    }

    [Fact]
    public void APatchWithAnErrorMakesTheCutsceneReadOnly()
    {
        using var ws = Workspace(new() { [PatchFile] = BrokenPatch });
        var session = ws.Session("act");

        Assert.Equal("The THN patch of the active mod has an error, so the editor cannot apply it: entities entry 1: no " +
                     "entity has the key \"Nobody\". Correct the file, then open the profile again.",
            session.CutsceneReadOnlyReason(ScriptPath));
        Assert.Same(session.LowerCutscene(ScriptPath), session.Cutscene(ScriptPath));
        Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(1))));
        Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Revert(ScriptPath)));

        using var both = Workspace(new() { [PatchFile] = PinPatch, [WholeFile] = Script });
        Assert.Equal("The active mod has a whole file and a THN patch for this cutscene. Remove one of them, then open " +
                     "the profile again.", both.Session("act").CutsceneReadOnlyReason(ScriptPath));
    }

    /// <summary>The paths that the loaded patch sets keep their entries after a save, also when their values equal
    /// the lower values. So the mod keeps its values when a lower mod changes them.</summary>
    [Fact]
    public void PinsSurviveASave()
    {
        using var ws = Workspace(new() { [PatchFile] = PinPatch });
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        session.Execute(ThnEdits.Set(cam, new ThnPath("cameraprops.nearplane"), N(2)));
        AssertSaved(session.Save());

        var patch = Patch(ws);
        Assert.Equal(10, patch.Duration);
        Assert.Equal([("cameraprops.fovh", N(10)), ("cameraprops.hvaspect", N(2)), ("cameraprops.nearplane", N(2))],
            Assert.Single(patch.Entities).Sets.Select(s => (s.Path.Text, s.Value)).OrderBy(s => s.Text, StringComparer.Ordinal));
        Assert.StartsWith("-- Values that a lower mod must not change.\nol_thn_patch = 1\n", PatchText(ws)!.Replace("\r", ""));
        // The pins of the saved patch are the pins of the document after the save.
        Assert.Equal(4, Document(session).State.Pins.Count);
    }

    [Fact]
    public void ARevertRemovesThePinsAndPutsBackTheLowerValues()
    {
        using var ws = Workspace(new() { [PatchFile] = PinPatch });
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        session.Execute(ThnEdits.Revert(ScriptPath, cam, Fovh));
        session.Execute(ThnEdits.Revert(ScriptPath, valuePath: new ThnPath("duration")));
        AssertSaved(session.Save());

        var patch = Patch(ws);
        Assert.Null(patch.Duration);
        Assert.Equal(["cameraprops.hvaspect"], Assert.Single(patch.Entities).Sets.Select(s => s.Path.Text));

        session.Execute(ThnEdits.Revert(ScriptPath, cam));
        Assert.Equal(new ThnNumber(1.5f), Value(session, cam, "cameraprops.hvaspect"));
        var result = session.Save();
        AssertSaved(result);
        Assert.EndsWith(PatchFile, Assert.Single(result.Deleted));
        Assert.Equal(ThnFileKind.Lower, session.CutsceneKind(ScriptPath));
    }

    /// <summary>A revert of a path puts the lower value back at its place in the lower record, so the patch has no
    /// entry for it.</summary>
    [Fact]
    public void ARevertOfARemovedKeyPutsItBackInItsPlace()
    {
        using var ws = Workspace(new() { [PatchFile] = NewEventPatch });
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        session.Execute(ThnEdits.Revert(ScriptPath, cam, new ThnPath("cameraprops.farplane")));
        session.Execute(ThnEdits.Revert(ScriptPath, cam, Fovh));

        Assert.Equal(session.LowerCutscene(ScriptPath)!.Entities[0].Table, Table(session, cam));
        var added = Event(session, 6);
        var e = Assert.Throws<InvalidOperationException>(() => session.Execute(ThnEdits.Revert(ScriptPath, added)));
        Assert.Equal("The active mod adds this event, so the lower cutscene has no values for it. Delete it instead.", e.Message);
        AssertSaved(session.Save());
        Assert.Empty(Patch(ws).Entities);
    }

    /// <summary>
    /// A set into a record that B does not have makes the record: <c>userprops</c> of the camera, and the props of a
    /// SET_CAMERA. A revert of the value removes the record again, so the element is the lower element, and a save
    /// deletes the patch.
    /// </summary>
    [Fact]
    public void ARevertOfAValueInARecordThatASetMadeGivesTheLowerElement()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var (cam, cut) = (Entity(session, "Cam_A"), Event(session, 0));
        var lower = session.LowerCutscene(ScriptPath)!;
        session.Execute(ThnEdits.Set(cam, new ThnPath("userprops.note"), T("x")));
        session.Execute(ThnEdits.Set(cut, new ThnPath("props.duration"), N(1)));
        AssertSaved(session.Save());
        Assert.NotNull(PatchText(ws));

        session.Execute(ThnEdits.Revert(ScriptPath, cam, new ThnPath("userprops.note")));
        session.Execute(ThnEdits.Revert(ScriptPath, cut, new ThnPath("props.duration")));

        Assert.Equal(lower.Entities[0].Table, Table(session, cam));
        Assert.Equal(lower.Events[0].Table, Table(session, cut));
        Assert.Equal("Revert props.duration", session.UndoLabel);
        session.Undo();
        Assert.Equal(N(1), Value(session, cut, "props.duration"));
        session.Redo();
        Assert.Equal(lower.Events[0].Table, Table(session, cut));
        var result = session.Save();
        AssertSaved(result);
        Assert.EndsWith(PatchFile, Assert.Single(result.Deleted));
        Assert.Null(PatchText(ws));
    }

    /// <summary>
    /// The loaded patch sets the whole <c>cameraprops</c> record. An edit of a value in it keeps the pin of the record,
    /// so the patch keeps the whole record. A revert of the value splits the pin into the pins of the other values of
    /// the record: the value follows B, and the other values stay in the patch.
    /// </summary>
    [Fact]
    public void ARevertInAPinnedRecordKeepsTheOtherValuesOfTheRecordInThePatch()
    {
        const string recordPatch = """
            ol_thn_patch = 1
            entities = { { ol_match = "Cam_A", ol_set = { cameraprops = { fovh = 20, hvaspect = 1.5, nearplane = 1, farplane = 100 } } } }
            """;
        using var ws = Workspace(new() { [PatchFile] = recordPatch });
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        var record = new ThnPin(cam.Id, new ThnPath("cameraprops"));
        Assert.Equal([record], Document(session).State.Pins);

        session.Execute(ThnEdits.Set(cam, Fovh, N(30)));
        Assert.Equal([record], Document(session).State.Pins);
        session.Execute(ThnEdits.Revert(ScriptPath, cam, Fovh));

        Assert.Equal(N(10), Value(session, cam, "cameraprops.fovh"));
        Assert.Equal(["cameraprops.farplane", "cameraprops.hvaspect", "cameraprops.nearplane"],
            Document(session).State.Pins.Select(p => p.Path.Text).Order(StringComparer.Ordinal));
        AssertSaved(session.Save());
        Assert.Equal([("cameraprops.farplane", N(100)), ("cameraprops.hvaspect", N(1.5f)), ("cameraprops.nearplane", N(1))],
            Assert.Single(Patch(ws).Entities).Sets.Select(s => (s.Path.Text, s.Value)));
    }

    /// <summary>An edit removes the pin of the value that it changes. So a pinned duration, time and length that go
    /// back to their lower values leave the patch, and at the end the save deletes the patch.</summary>
    [Fact]
    public void AnEditBackToTheLowerValueTakesThePinnedValueOutOfThePatch()
    {
        const string moved = """
            ol_thn_patch = 1
            duration = 12
            events = { { ol_match = { 6, START_SOUND, { "Voice_trent" } }, ol_set = { time = 6.5, ["props.duration"] = 2 } } }
            """;
        using var ws = Workspace(new() { [PatchFile] = moved });
        var session = ws.Session("act");
        var sound = Event(session, 4);

        session.Execute(ThnEdits.SetDuration(ScriptPath, 10));
        AssertSaved(session.Save());
        Assert.Null(Patch(ws).Duration);
        session.Execute(ThnEdits.MoveEvents([sound], -0.5f, 0));
        AssertSaved(session.Save());
        Assert.Equal(["props.duration"], Assert.Single(Patch(ws).Events).Sets.Select(s => s.Path.Text));
        session.Execute(ThnEdits.MoveEvents([sound], 0, -0.5f));
        var result = session.Save();

        AssertSaved(result);
        Assert.EndsWith(PatchFile, Assert.Single(result.Deleted));
    }

    /// <summary>After a save, the document opens again from the saved file (rule F14). Its state is the state that a
    /// new open gives, and the elements keep their ids.</summary>
    [Fact]
    public void ReopenGivesTheStateThatANewOpenGives()
    {
        using var ws = Workspace(new() { [PatchFile] = NewEventPatch });
        var session = ws.Session("act");
        session.Execute(ThnEdits.AddEvent(ScriptPath, List(N(8), S("START_PSYS"), List(T("Fx")))));
        session.Execute(ThnEdits.Set(Event(session, 6), new ThnPath("props.duration"), N(4)));
        var before = W(session);

        AssertSaved(session.Save());

        var reopened = Document(session);
        Assert.Equal(before, reopened.Working);
        Assert.Same(reopened.State, reopened.Initial);
        session.Dispose();
        var fresh = Document(ws.Session("act"));
        Assert.Equal(fresh.Kind, reopened.Kind);
        Assert.Equal(ThnWriter.Write(fresh.Working.ToGlobals()), ThnWriter.Write(reopened.Working.ToGlobals()));
        Assert.Equal(fresh.State.Pins.OrderBy(p => p.ToString()), reopened.State.Pins.OrderBy(p => p.ToString()));
        Assert.Equal(fresh.SourceHash, reopened.SourceHash);
    }

    /// <summary>Undo and redo across a save give the same patch, and an undo of all edits deletes the patch.</summary>
    [Fact]
    public void UndoAndRedoAcrossASaveGiveTheSamePatch()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(20)));
        session.Execute(ThnEdits.MoveEvents([Event(session, 4)], 1.25f, 0));
        AssertSaved(session.Save());
        var saved = PatchText(ws);

        session.Undo();
        session.Undo();
        AssertSaved(session.Save());
        Assert.Null(PatchText(ws));
        session.Redo();
        session.Redo();
        AssertSaved(session.Save());

        Assert.Equal(saved, PatchText(ws));
        Assert.Equal(2, Patch(ws).Entities.Length + Patch(ws).Events.Length);
    }

    /// <summary>A save writes a patch in canonical form. It keeps the comment block at the top and drops the other
    /// comments, with a warning (decision D63).</summary>
    [Fact]
    public void ASaveKeepsTheCommentBlockAndDropsTheOtherCommentsWithAWarning()
    {
        const string commented = """
            -- My mod.
            -- A wider shot.

            ol_thn_patch = 1
            entities = {
                -- The first shot.
                { ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20, ["userprops.note"] = "a -- b" } }
            }
            """;
        using var ws = Workspace(new() { [PatchFile] = commented });
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(25)));

        var result = session.Save();

        Assert.True(result.Saved);
        var warning = Assert.Single(result.Problems);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("The save writes this THN patch in canonical form. It keeps the comments at the top of the file and " +
                     "drops the other comments.", warning.Message);
        var text = PatchText(ws)!.Replace("\r", "");
        Assert.StartsWith("-- My mod.\n-- A wider shot.\nol_thn_patch = 1\n", text);
        Assert.DoesNotContain("The first shot", text);
        Assert.Contains("\"a -- b\"", text);
        Assert.False(ThnPatch.HasInnerComments(text));
        Assert.True(ThnPatch.HasInnerComments(commented));
        Assert.False(ThnPatch.HasInnerComments("-- top\nx = \"--\"\ny = '--'\nz = [[ [[ -- ]] -- ]]\n"));
        Assert.True(ThnPatch.HasInnerComments("-- top\nx = [[ [[ ]] ]] -- end\n"));
    }
}
