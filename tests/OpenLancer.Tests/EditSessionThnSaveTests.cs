using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The save of cutscene edits: the patch, the rule that an unchanged patch keeps its bytes, one save with INI and THN
/// files, the recovery file of format 2, a changed lower mod, and the refusal of a whole THN file in a mod of the
/// OpenLancer repo (decision D70).
/// </summary>
public sealed class EditSessionThnSaveTests
{
    private static readonly ThnPath Fovh = new("cameraprops.fovh");
    private static readonly ThnPath Hvaspect = new("cameraprops.hvaspect");

    /// <summary>A hand-written patch that is not in canonical form.</summary>
    private const string HandPatch = "ol_thn_patch=1\r\nentities={{ol_match=\"Cam_A\",ol_set={[\"cameraprops.fovh\"]=20}}}\r\n";

    private const string RecoveryChanged = "The mod changed after the editor wrote the recovery file, so the edits do not apply to it.";

    [Fact]
    public void ASaveWritesThePatchAndKeepsTheBytesOfAnUnchangedPatch()
    {
        using var ws = Workspace(new() { [PatchFile] = HandPatch });
        var file = Path.Combine(ws.ModFolder("act"), PatchFile);
        var bytes = File.ReadAllBytes(file);
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");

        // An edit and an edit back: W has the values of the patch, so the file keeps its bytes (rule P2).
        session.Execute(ThnEdits.Set(cam, Fovh, N(30)));
        session.Execute(ThnEdits.Set(cam, Fovh, N(20)));
        var kept = session.Save();
        AssertSaved(kept);
        Assert.Empty(kept.Written);
        Assert.Equal(bytes, File.ReadAllBytes(file));

        session.Execute(ThnEdits.Set(cam, new ThnPath("cameraprops.hvaspect"), N(2)));
        var written = session.Save();
        AssertSaved(written);
        Assert.Equal([file], written.Written);
        Assert.Equal("""
            ol_thn_patch = 1
            entities = {
            	{ ol_match = "Cam_A", ol_set = { ["cameraprops.fovh"] = 20, ["cameraprops.hvaspect"] = 2 } }
            }

            """.Replace("\r", ""), File.ReadAllText(file));
    }

    [Fact]
    public void APatchWithoutChangesIsDeleted()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        session.Execute(ThnEdits.Set(cam, Fovh, N(30)));
        AssertSaved(session.Save());
        Assert.NotNull(PatchText(ws));

        session.Execute(ThnEdits.Set(cam, Fovh, N(10)));
        var result = session.Save();

        AssertSaved(result);
        Assert.EndsWith(PatchFile, Assert.Single(result.Deleted));
        Assert.Null(PatchText(ws));
    }

    [Fact]
    public void AThnEditAndAnIniEditSaveInOneTransaction()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(30)));
        session.Execute(EditSessionTests.Set(EditSessionTests.Good("g1"), "price", new Int32Value(5)));
        var steps = 0;

        var result = session.Save(false, k => steps = k);

        AssertSaved(result);
        Assert.Equal(2, result.Written.Count);
        // One save transaction moves both files into place.
        Assert.Equal(2, steps);
        Assert.Equal(N(30), Assert.Single(Assert.Single(Patch(ws).Entities).Sets).Value);
        Assert.Equal(["5"], EditSessionTests.Values(session, EditSessionTests.Good("g1"), "price"));
    }

    /// <summary>The recovery file of format 2 holds the THN patch and the whole THN file of the edits, with their kinds.
    /// The next session restores them, and a save gives the files of a direct save.</summary>
    [Fact]
    public void RecoveryFormat2RestoresThnEdits()
    {
        const string newPath = @"DATA\MODS\act\CUTSCENES\new.thn";
        static Dictionary<string, string> Files() => new() { [@"files\" + newPath] = Script };
        static void Edit(EditSession session)
        {
            session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(30)));
            session.Execute(ThnEdits.MoveEvents([Event(session, 1)], 0.5f, 0));
            session.Execute(ThnEdits.SetDuration(newPath, 4));
        }

        using var direct = Workspace(Files());
        var directSession = direct.Session("act");
        Edit(directSession);
        AssertSaved(directSession.Save());
        using var ws = Workspace(Files());
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        var session = ws.Session("act");
        Edit(session);
        Assert.Empty(session.WriteRecovery());
        var w = W(session);
        session.Dispose();

        var json = JsonNode.Parse(File.ReadAllText(RecoveryFile.PathOf(ws.ModFolder("act"))))!;
        Assert.Equal(2, (int)json["formatVersion"]!);
        Assert.Equal([("file", newPath), ("patch", ScriptPath)],
            json["files"]!.AsArray().Select(f => ((string)f!["kind"]!, (string)f["path"]!)).OrderBy(f => f.Item1));
        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act"), exclude: SaveTransaction.FolderName));
        Assert.Empty(restored.RestoreRecovery());

        Assert.Equal(ThnWriter.Write(w.ToGlobals()), ThnWriter.Write(W(restored).ToGlobals()));
        Assert.Equal(4, restored.Cutscene(newPath)!.Duration);
        AssertSaved(restored.Save());
        Assert.Equal(TestWorkspace.Files(direct.ModFolder("act")), TestWorkspace.Files(ws.ModFolder("act")));
    }

    /// <summary>
    /// A THN patch of the mod that changed after the editor wrote the recovery file takes no recovered edits (rule M1).
    /// Else the next save would write the old patch over the change without a question. W stays as the changed file
    /// gives it.
    /// </summary>
    [Fact]
    public void ARecoveryFileDoesNotRestoreOverAThnPatchThatChangedAfterIt()
    {
        using var ws = Workspace(new() { [PatchFile] = HandPatch });
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Hvaspect, N(2)));
        Assert.Empty(session.WriteRecovery());
        session.Dispose();
        File.WriteAllText(Path.Combine(ws.ModFolder("act"), PatchFile), HandPatch.Replace("=20", "=25"));

        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        var error = Assert.Single(restored.RestoreRecovery());

        Assert.Equal((ScriptPath, RecoveryChanged), (error.Source, error.Message));
        Assert.Equal(N(25), Value(restored, Entity(restored, "Cam_A"), "cameraprops.fovh"));
        Assert.Equal(N(1.5f), Value(restored, Entity(restored, "Cam_A"), "cameraprops.hvaspect"));
        Assert.False(restored.CanUndo);
        Assert.True(restored.HasRecovery);
    }

    /// <summary>A recovery entry of another kind than the file of the mod takes no edits: here a whole-file entry for a
    /// cutscene that the mod patches.</summary>
    [Fact]
    public void ARecoveryEntryOfAnotherKindOfFileIsNotRestored()
    {
        using var ws = Workspace(new() { [PatchFile] = HandPatch });
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Hvaspect, N(2)));
        Assert.Empty(session.WriteRecovery());
        session.Dispose();
        var file = RecoveryFile.PathOf(ws.ModFolder("act"));
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("patch", (string)json["files"]![0]!["kind"]!);
        json["files"]![0]!["kind"] = "file";
        File.WriteAllText(file, json.ToJsonString());

        var restored = ws.Session("act");
        var error = Assert.Single(restored.RestoreRecovery());

        Assert.Equal((ScriptPath, RecoveryChanged), (error.Source, error.Message));
        Assert.Equal(N(1.5f), Value(restored, Entity(restored, "Cam_A"), "cameraprops.hvaspect"));
        Assert.False(restored.CanUndo);
    }

    /// <summary>
    /// The save check evaluates the text that a save writes, applies a patch to B again, and compares the result with W
    /// bit for bit, also in the key order. Each text that does not give W gives the problem: another value, another key
    /// order, a delete of the patch, a patch that does not apply, and a text that does not run.
    /// </summary>
    [Fact]
    public void TheSaveCheckFindsEachTextThatDoesNotGiveW()
    {
        const string start = "The saved text does not give the edited cutscene";
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(20)));
        var patch = session.CutsceneDocument(ScriptPath)!;
        var good = patch.ChangedFile()!;

        Assert.Null(patch.SaveProblem(good));
        Assert.Equal($"{start}. The first difference is at entities[1].cameraprops.fovh.",
            patch.SaveProblem(new ThnFileText(good.Text!.Replace("= 20", "= 21"), null)));
        Assert.Equal($"{start}. The first difference is at entities[1].cameraprops.fovh.", patch.SaveProblem(new ThnFileText(null, null)));
        Assert.Equal($"{start}: entities entry 1: no entity has the key \"Nobody\".",
            patch.SaveProblem(new ThnFileText(good.Text!.Replace("\"Cam_A\"", "\"Nobody\""), null)));
        Assert.StartsWith($"{start}: ", patch.SaveProblem(new ThnFileText("ol_thn_patch = ", null)));

        using var wholeWs = Workspace(new() { [WholeFile] = Script });
        var wholeSession = wholeWs.Session("act");
        wholeSession.Execute(ThnEdits.Set(Entity(wholeSession, "Cam_A"), Fovh, N(30)));
        var whole = wholeSession.CutsceneDocument(ScriptPath)!;
        var text = whole.ChangedFile()!.Text!;
        Assert.Null(whole.SaveProblem(new ThnFileText(text, null)));
        Assert.Contains("fovh = 30, hvaspect = 1.5", text);
        Assert.Equal($"{start}. The first difference is at entities[1].cameraprops.",
            whole.SaveProblem(new ThnFileText(text.Replace("fovh = 30, hvaspect = 1.5", "hvaspect = 1.5, fovh = 30"), null)));
    }

    /// <summary>A save whose text does not give W stops and writes no file. Here the evaluator reads the saved patch
    /// with a wrong number, as an evaluator with an error would.</summary>
    [Fact]
    public void ASaveWritesNothingWhenTheSaveCheckFails()
    {
        using var ws = Workspace();
        var before = TestWorkspace.Files(ws.ModFolder("act"), exclude: SaveTransaction.FolderName);
        var session = ws.Session("act", new WrongPatchNumbers());
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(20)));

        var result = session.Save();

        Assert.False(result.Saved);
        var error = Assert.Single(result.Problems);
        Assert.Equal(ScriptPath, error.Source);
        Assert.Equal("The saved text does not give the edited cutscene. The first difference is at " +
                     "entities[1].cameraprops.fovh.", error.Message);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act"), exclude: SaveTransaction.FolderName));
        Assert.Empty(TestWorkspace.SaveFiles(ws.ModFolder("act")));
    }

    /// <summary>A save stops when the active mod got the other kind of file for the cutscene after the open: here a
    /// whole file next to the patch that the save would write.</summary>
    [Fact]
    public void ASaveStopsWhenTheActiveModNowHasTheOtherKindOfFile()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(30)));
        var whole = Path.Combine(ws.ModFolder("act"), WholeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(whole)!);
        File.WriteAllText(whole, Script);

        var result = session.Save();

        Assert.False(result.Saved);
        var error = Assert.Single(result.Problems);
        Assert.Equal((ScriptPath, $"The active mod now has another kind of file for {ScriptPath}. Open the profile again."),
            (error.Source, error.Message));
        Assert.Null(PatchText(ws));
    }

    /// <summary>A recovery file of format 1, from an editor before cutscenes, still restores its INI patches.</summary>
    [Fact]
    public void ARecoveryFileOfFormat1Restores()
    {
        using var ws = Workspace();
        var file = RecoveryFile.PathOf(ws.ModFolder("act"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $$"""
            { "formatVersion": 1, "patches": [ { "path": "{{EditSessionTests.GoodsPath.Replace(@"\", @"\\")}}", "knownHash": null,
              "text": "[Good]\nnickname = g1\nprice = 7\n" } ] }
            """);
        var session = ws.Session("act");

        Assert.Empty(session.RestoreRecovery());

        Assert.Equal(["7"], EditSessionTests.Values(session, EditSessionTests.Good("g1"), "price"));
    }

    /// <summary>A lower mod whose THN patch changes after the open stops the save (rule F11), also when the session
    /// opens the cutscene after the change.</summary>
    [Fact]
    public void AChangedLowerModStopsTheSave()
    {
        using var ws = Workspace(lowFiles: new() { [PatchFile] = ModOverlayThnTests.FovPatch });
        var session = ws.Session("act");
        File.WriteAllText(Path.Combine(ws.ModFolder("low"), PatchFile), ModOverlayThnTests.FovPatch.Replace("= 20", "= 25"));
        // The session read the lower patch at the open.
        Assert.Equal(N(20), Value(session, Entity(session, "Cam_A"), "cameraprops.fovh"));
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), new ThnPath("cameraprops.hvaspect"), N(2)));

        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Contains(result.Problems, p => p.Source == ScriptPath && p.Message.StartsWith("The base data, a lower mod or the mod list"));
        Assert.Null(PatchText(ws));
    }

    /// <summary>
    /// The editor writes no whole THN file into a mod in the OpenLancer repo, because the content scan cannot tell it
    /// from a copy of a retail script (decision D70). A THN patch in such a mod saves.
    /// </summary>
    [Fact]
    public void AWholeThnFileInAModInsideTheRepoIsRefused()
    {
        using var ws = Workspace(new() { [WholeFile] = Script });
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "<Solution />");
        var whole = Path.Combine(ws.ModFolder("act"), WholeFile);
        var bytes = File.ReadAllBytes(whole);
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Fovh, N(30)));

        var result = session.Save();

        Assert.False(result.Saved);
        var error = Assert.Single(result.Problems);
        Assert.Equal(ScriptPath, error.Source);
        Assert.Equal($"The mod 'act' is in the OpenLancer repo {ws.Root}. The editor writes no whole cutscene file into " +
                     "it, because the content scan cannot tell such a file from a copy of a retail script (decision D70).",
            error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(whole));

        using var patchWs = Workspace();
        File.WriteAllText(Path.Combine(patchWs.Root, RepoCheck.MarkerFile), "<Solution />");
        var patchSession = patchWs.Session("act");
        patchSession.Execute(ThnEdits.Set(Entity(patchSession, "Cam_A"), Fovh, N(30)));
        AssertSaved(patchSession.Save());
        Assert.NotNull(PatchText(patchWs));
    }

    /// <summary>A text patch file of the active mod reads as ASCII, and the save writes ASCII.</summary>
    [Fact]
    public void ASavedPatchIsAsciiText()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), new ThnPath("userprops.note"), T("a \"b\" \\ c")));
        AssertSaved(session.Save());

        var bytes = File.ReadAllBytes(Path.Combine(ws.ModFolder("act"), PatchFile));
        Assert.All(bytes, b => Assert.True(b < 0x80));
        // The camera has no userprops in B, so the patch sets the new record.
        var set = Assert.Single(Assert.Single(Patch(ws).Entities).Sets);
        Assert.Equal(("userprops", (ThnValue)Record(F("note", T("a \"b\" \\ c")))), (set.Path.Text, set.Value));
    }

    /// <summary>
    /// Decision D137 of the M8b design: an error of the checks of a cutscene that W adds stops the save and Play, with
    /// the message that the game crashes and the place of the error. A fix makes the save pass.
    /// </summary>
    [Fact]
    public void ANewCutsceneErrorStopsTheSaveWithTheCrashMessageAndThePlace()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var cam = Entity(session, "Cam_A");
        session.Execute(ThnEdits.Set(cam, Fovh, T("wide")));

        var error = Assert.Single(session.CutsceneErrors(ScriptPath));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.StartsWith("The game crashes when it plays this cutscene. entity \"Cam_A\", cameraprops.fovh: ",
            error.Message);
        Assert.Equal((ThnElementKind.Entity, "cameraprops.fovh", "\"Cam_A\""),
            (error.Cutscene!.Kind, error.Cutscene.Value, error.Cutscene.Key));
        Assert.Equal(cam.Id, error.Cutscene.ElementIn(W(session))!.Id);
        var result = session.Save();
        Assert.False(result.Saved);
        Assert.Contains(error, result.Problems);
        Assert.Null(PatchText(ws));
        // Play saves first, and the same gate stops it.
        Assert.NotNull(session.GateRefusal());

        session.Execute(ThnEdits.Set(cam, Fovh, N(30)));
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        AssertSaved(session.Save());
    }

    /// <summary>An error that the lower cutscene already has is not an edit of the active mod, so it does not stop the
    /// save (<see cref="Diagnostic.NewIn"/>).</summary>
    [Fact]
    public void AnErrorThatTheLowerCutsceneHasDoesNotStopTheSave()
    {
        using var ws = Workspace(script: Script.Replace("cameraprops = { fovh = 10,", "cameraprops = { fovh = \"wide\","));
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam_A"), Hvaspect, N(2)));

        Assert.Empty(session.CutsceneErrors(ScriptPath));
        AssertSaved(session.Save());
    }

    /// <summary>The gate skips a cutscene that the editor cannot change: here the active mod has a whole file and a
    /// patch for it. Such a file gets the merge error instead.</summary>
    [Fact]
    public void TheGateSkipsAReadOnlyCutscene()
    {
        using var ws = Workspace(new()
        {
            [WholeFile] = Script.Replace("cameraprops = { fovh = 10,", "cameraprops = { fovh = \"wide\","),
            [PatchFile] = "ol_thn_patch = 1\n",
        });
        var session = ws.Session("act");

        Assert.NotNull(session.CutsceneReadOnlyReason(ScriptPath));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.StartsWith(EditSession.CutsceneCrash));
    }

    /// <summary>A whole cutscene file of the active mod that does not evaluate stops the save. The engine reads it with
    /// the same Thorn VM and throws when the game plays it. A modder can write such a file outside the editor.</summary>
    [Fact]
    public void AWholeFileThatDoesNotEvaluateStopsTheSave()
    {
        using var ws = Workspace(new() { [WholeFile] = "duration = (" });
        var session = ws.Session("act");

        var result = session.Save();

        Assert.False(result.Saved);
        var error = Assert.Single(result.Problems);
        Assert.Equal(ScriptPath, error.Source);
        Assert.StartsWith("The game crashes when it plays this cutscene. The editor cannot read the cutscene file of " +
                          "the active mod: ", error.Message);
        Assert.Equal(ScriptPath, error.Cutscene!.Path);
    }

    /// <summary>The engine evaluator, but each number 20 of a THN patch that it reads gives 21.</summary>
    private sealed class WrongPatchNumbers : IThnEvaluator
    {
        public IReadOnlyDictionary<string, ThnSymbolInfo> Symbols => Evaluator.Symbols;

        public ThnTable Evaluate(ReadOnlySpan<byte> script, string name)
        {
            var globals = Evaluator.Evaluate(script, name);
            return globals[ThnPatch.VersionGlobal] == null ? globals : (ThnTable)Wrong(globals);
        }

        private static ThnValue Wrong(ThnValue value) => value switch
        {
            ThnNumber { Value: 20 } => N(21),
            ThnTable table => new ThnTable([.. table.List.Select(Wrong)], [.. table.Record.Select(f => new ThnField(f.Key, Wrong(f.Value)))]),
            _ => value,
        };
    }
}
