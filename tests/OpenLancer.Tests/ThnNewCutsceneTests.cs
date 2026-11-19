using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// New cutscenes (decisions D67 and D134, test 4 of section 8.1 of the M8b design): the absent document, New cutscene
/// with its undo and redo, a look-up before the command, the save, the delete after an undo, the refusals, the recovery
/// of an unsaved new cutscene and of a delete, and the template.
/// </summary>
public sealed class ThnNewCutsceneTests
{
    private const string NewPath = @"DATA\MODS\act\CUTSCENES\bar_shot.thn";

    /// <summary>The 9 names that only Librelancer defines (section 4.6 of the publishing plan).</summary>
    private static readonly string[] LibrelancerOnly =
    [
        "STREAM", "UNKNOWN_ENTITY", "SUB_SCENE", "DELETED", "SUBTITLE", "START_REVERB_PROP_ANIM", "USER_EVENT",
        "UNDEFINED_EVENT", "START_SUB_SCENE",
    ];

    private static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.DoesNotContain(result.Problems, p => p.Severity == Data.DiagnosticSeverity.Error);
    }

    private static string FileOf(TestWorkspace ws) => Path.Combine(ws.ModFolder("act"), "files", NewPath);

    private static EditCommand New() => ThnEdits.NewCutscene(NewPath, ThnTemplates.NewScript());

    [Fact]
    public void NewCutsceneGivesTheTemplateAndUndoAndRedoTakeItBackAndGiveItAgain()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Assert.Null(session.Cutscene(NewPath));
        Assert.Equal(@"DATA\MODS\act\CUTSCENES\bar_shot.thn", session.NewCutscenePath("bar_shot"));

        session.Execute(New());

        var w = session.Cutscene(NewPath);
        Assert.NotNull(w);
        Assert.Equal(ThnWriter.Write(ThnTemplates.NewScript()), ThnWriter.Write(w.ToGlobals()));
        Assert.Equal("New cutscene bar_shot", session.UndoLabel);
        Assert.Contains(NewPath, session.OpenCutscenes);
        Assert.Contains(NewPath, session.NewCutscenes);
        Assert.Equal(ThnFileKind.NewHere, session.CutsceneSource(NewPath).Kind);
        Assert.Equal(ThnFileKind.NewHere, session.CutsceneKind(NewPath));
        Assert.True(session.HasUnsavedCutsceneEdits(NewPath));
        Assert.Null(session.CutsceneReadOnlyReason(NewPath));
        Assert.All(w.Entities.Concat(w.Events), e => Assert.Equal(-1, e.Origin));

        session.Undo();
        Assert.Null(session.Cutscene(NewPath));
        Assert.DoesNotContain(NewPath, session.OpenCutscenes);
        Assert.Empty(session.NewCutscenes);
        Assert.Equal($"The edit view has no cutscene {NewPath}.", session.CutsceneReadOnlyReason(NewPath));
        Assert.Throws<System.ArgumentException>(() => session.Execute(ThnEdits.SetDuration(NewPath, 4)));

        session.Redo();
        Assert.Equal(w, session.Cutscene(NewPath));
        // A new element gets an id that W does not have.
        session.Execute(ThnEdits.AddEntity(NewPath, ThnTable.FromRecord(new ThnField("entity_name", new ThnText("Mk")),
            new ThnField("type", new ThnSymbol("MARKER")))));
        var ids = session.Cutscene(NewPath)!.Entities.Concat(session.Cutscene(NewPath)!.Events).Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>A look-up of the path before New cutscene, for example by the refusal check or a picker, keeps a
    /// problem for the path. The command removes it, so the session gives W after it.</summary>
    [Fact]
    public void ALookUpBeforeNewCutsceneDoesNotStopIt()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        Assert.Null(session.Cutscene(NewPath));
        Assert.Equal($"The edit view has no cutscene {NewPath}.", session.CutsceneReadOnlyReason(NewPath));
        Assert.Null(ThnEdits.NewCutsceneRefusal(session, NewPath));

        session.Execute(New());

        Assert.NotNull(session.Cutscene(NewPath));
        Assert.Null(session.CutsceneReadOnlyReason(NewPath));
    }

    /// <summary>A save writes the whole file into the files folder of the active mod. After an undo, a save deletes it,
    /// and the session keeps the absent document. After a redo, a save writes it again.</summary>
    [Fact]
    public void ASaveWritesTheFileAnUndoAndASaveDeleteItAndARedoWritesItAgain()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var file = FileOf(ws);
        session.Execute(New());

        var written = session.Save();
        AssertSaved(written);
        Assert.Equal([file], written.Written);
        Assert.Equal(ThnWriter.Write(ThnTemplates.NewScript()), File.ReadAllText(file));
        Assert.False(session.HasUnsavedCutsceneEdits(NewPath));
        Assert.Equal(ThnFileKind.NewHere, session.CutsceneSource(NewPath).Kind);

        session.Undo();
        Assert.Null(session.Cutscene(NewPath));
        var deleted = session.Save();
        AssertSaved(deleted);
        Assert.Equal([file], deleted.Deleted);
        Assert.False(File.Exists(file));
        Assert.Null(session.Cutscene(NewPath));
        Assert.Null(ThnEdits.NewCutsceneRefusal(session, NewPath));

        session.Redo();
        var again = session.Save();
        AssertSaved(again);
        Assert.Equal([file], again.Written);
        Assert.True(File.Exists(file));
        Assert.Equal(ThnWriter.Write(ThnTemplates.NewScript()), ThnWriter.Write(session.Cutscene(NewPath)!.ToGlobals()));
    }

    [Fact]
    public void NewCutsceneRefusesEachCaseOfTheDesign()
    {
        using var ws = Workspace(new() { [@"files\DATA\MODS\act\CUTSCENES\old.thn"] = Script });
        var session = ws.Session("act");

        Assert.Null(ThnEdits.NewCutsceneRefusal(session, NewPath));
        Assert.Null(ThnEdits.NewCutsceneRefusal(session, @"data/mods/ACT/cutscenes/A-b_9.thn"));
        // The folder of the active mod only.
        Assert.Equal(@"A new cutscene goes into the folder DATA\MODS\act\CUTSCENES of the active mod.",
            ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\other\CUTSCENES\x.thn"));
        Assert.NotNull(ThnEdits.NewCutsceneRefusal(session, @"DATA\SCRIPTS\BASES\x.thn"));
        Assert.NotNull(ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\act\CUTSCENES\sub\x.thn"));
        Assert.NotNull(ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\act\CUTSCENES\x.ini"));
        // Letters, digits, _ and - only.
        Assert.Equal(ThnEdits.BadName, ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\act\CUTSCENES\bar shot.thn"));
        Assert.Equal(ThnEdits.BadName, ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\act\CUTSCENES\bär.thn"));
        Assert.Equal(ThnEdits.BadName, ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\act\CUTSCENES\a.b.thn"));
        Assert.Equal("Type a name for the cutscene.", ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\act\CUTSCENES\.thn"));
        // A file of the edit view, in any letter case, and a new cutscene of W.
        Assert.Equal(ThnEdits.PathInUse, ThnEdits.NewCutsceneRefusal(session, @"DATA\MODS\act\CUTSCENES\OLD.thn"));
        session.Execute(New());
        Assert.Equal(ThnEdits.PathInUse, ThnEdits.NewCutsceneRefusal(session, NewPath.ToUpperInvariant()));
        Assert.Throws<System.InvalidOperationException>(() => session.Execute(New()));

        // Without an active mod, and in a read-only session.
        Assert.Equal(ThnEdits.NoActiveMod, ThnEdits.NewCutsceneRefusal(ws.SecondSession(null), NewPath));
        var second = ws.SecondSession("act");
        Assert.NotNull(second.ReadOnlyReason);
        Assert.Equal(second.ReadOnlyReason, ThnEdits.NewCutsceneRefusal(second, NewPath));
    }

    /// <summary>The editor writes no whole cutscene file into a mod in the OpenLancer repo (decision D70), so New
    /// cutscene refuses such a mod with the message of the save.</summary>
    [Fact]
    public void NewCutsceneRefusesAModInTheRepo()
    {
        using var ws = Workspace();
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "<Solution />");
        var session = ws.Session("act");

        Assert.Equal($"The mod 'act' is in the OpenLancer repo {ws.Root}. The editor writes no whole cutscene file " +
                     "into it, because the content scan cannot tell such a file from a copy of a retail script.",
                     ThnEdits.NewCutsceneRefusal(session, NewPath));
    }

    /// <summary>The recovery file keeps an unsaved new cutscene. The next session restores it while it has the
    /// recovery file, which makes it read-only.</summary>
    [Fact]
    public void TheRecoveryFileRestoresAnUnsavedNewCutscene()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(New());
        session.Execute(ThnEdits.SetDuration(NewPath, 4));
        Assert.Empty(session.WriteRecovery());
        session.Dispose();

        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        Assert.Empty(restored.RestoreRecovery());

        Assert.Equal(4, restored.Cutscene(NewPath)!.Duration);
        Assert.Equal(ThnFileKind.NewHere, restored.CutsceneKind(NewPath));
        AssertSaved(restored.Save());
        Assert.True(File.Exists(FileOf(ws)));
    }

    /// <summary>The recovery file keeps the delete of a saved new cutscene, after an undo of New cutscene.</summary>
    [Fact]
    public void TheRecoveryFileRestoresTheDeleteOfASavedNewCutscene()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        session.Execute(New());
        AssertSaved(session.Save());
        session.Undo();
        Assert.Empty(session.WriteRecovery());
        session.Dispose();

        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        Assert.NotNull(restored.Cutscene(NewPath));
        Assert.Empty(restored.RestoreRecovery());

        Assert.Null(restored.Cutscene(NewPath));
        var result = restored.Save();
        AssertSaved(result);
        Assert.Equal([FileOf(ws)], result.Deleted);
        Assert.False(File.Exists(FileOf(ws)));
    }

    /// <summary>The template has none of the names that only Librelancer defines, the checks find no error in it,
    /// and it gives the same globals after a write and an evaluation.</summary>
    [Fact]
    public void TheTemplateUsesNoLibrelancerOnlyNameAndHasNoError()
    {
        var globals = ThnTemplates.NewScript();
        var text = ThnWriter.Write(globals);

        foreach (var name in LibrelancerOnly)
            Assert.DoesNotContain(name, text);
        Assert.Empty(ThnChecks.Errors(NewPath, globals, Evaluator));
        Assert.Equal(text, ThnWriter.Write(Evaluate(text)));
        var model = ThnScriptModel.From(globals, ThnElementId.Counter());
        Assert.Equal(10, model.Duration);
        Assert.Equal(["Scene", "Monitor", "Camera_01"], model.Entities.Select(e => ThnScriptModel.NameOf(e.Table)));
        var cut = Assert.Single(model.Events);
        Assert.Equal(new ThnNumber(0), cut.Table.List[0]);
        Assert.Equal(new ThnSymbol("SET_CAMERA"), cut.Table.List[1]);
    }
}
