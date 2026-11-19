using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Logs;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The save gate checks each cutscene of the edit view that names an item that a change of the active mod removes or
/// breaks (#81). This is also true when the active mod does not change the cutscene, because the game crashes or shows
/// less when it plays it. The fixture script names a costume, equipment, an effect, a room and a clip. The active mod
/// does not change it, and no INI line names it. Each error has the text, the place, the source and the origin of the
/// Problems section of the Cutscene page. So Save, Play and the Console give one text.
/// </summary>
public sealed class ThnReferenceGateTests
{
    private const string TrentMissing = "The game crashes when it plays this cutscene. entity \"Man\", template_name: " +
                                        "The costume \"trent\" is not in the game data. A DEFORMABLE without " +
                                        "userprops.Actor needs it.";

    private const string LowScript = @"DATA\SCRIPTS\low_scene.thn";

    private static SectionRef Ship1 => new(@"DATA\SHIPS\shiparch.ini", new SectionIdentity("Ship", "ship1"));

    /// <summary>The errors of a result of the save gate that a cutscene gives.</summary>
    private static List<Diagnostic> CutsceneErrors(SaveResult result) =>
        [.. result.Problems.Where(p => p is { Severity: DiagnosticSeverity.Error, Cutscene: not null })];

    /// <summary>The note of the Problems section of the Cutscene page for the fixture script.</summary>
    private static string? Note(EditSession session, string path = ScriptPath) =>
        ThnPage.LowerProblemsNote(session.CutsceneSource(path), session.HasUnsavedCutsceneEdits(path));

    /// <summary>
    /// A delete of a costume that only the unchanged lower script names stops the save. The script is a cutscene that
    /// no INI line names. The error is the error of the Problems section. It has the costume message on the entity
    /// "Man", the script as its source and the origin of the active mod. An undo heals it, and the save then writes
    /// nothing for the script.
    /// </summary>
    [Fact]
    public void ADeleteOfACostumeThatOnlyAnUnchangedCutsceneNamesBlocksTheSave()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        Assert.Empty(session.EditViewReferences.UsedBy(ScriptPath));

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));
        var refusal = session.GateRefusal();
        var save = session.Save();

        Assert.True(ThnBreaks.IsUnchanged(session, ScriptPath));
        Assert.NotNull(refusal);
        var error = Assert.Single(refusal.Problems);
        Assert.Equal((TrentMissing, ScriptPath, DiagnosticSeverity.Error), (error.Message, error.Source, error.Severity));
        Assert.Equal((ScriptPath, ThnElementKind.Entity, "template_name"),
            (error.Cutscene!.Path, error.Cutscene.Kind, error.Cutscene.Value));
        Assert.Equal(MessageOrigin.Mod("act", ModEvidence.ActiveMod), error.Origin);
        Assert.Equal(session.CutsceneErrors(ScriptPath), refusal.Problems);
        Assert.False(save.Saved);
        Assert.Equal(refusal.Problems, save.Problems);

        session.Undo();
        Assert.Null(session.GateRefusal());
        var saved = session.Save();
        AssertSaved(saved);
        Assert.Empty(saved.Written);
        Assert.Null(PatchText(ws));
    }

    /// <summary>
    /// Each change of the active mod that removes or breaks an item of the unchanged script stops the save. The error
    /// is the error of the script, and it names the entity and the item. The save writes no patch of the script. The
    /// changes are edits of the session, files of the active mod, and a patch or a whole file of an earlier session
    /// (see <see cref="ThnBreaks"/>). A rename of the lower costume writes <c>ol.rename</c>.
    /// </summary>
    [Theory]
    [InlineData("rename costume")]
    [InlineData("body without a body part")]
    [InlineData("delete body part")]
    [InlineData("equipment without a model")]
    [InlineData("delete effect")]
    [InlineData("remove the room line")]
    [InlineData("remove a clip")]
    [InlineData("saved delete patch")]
    [InlineData("whole file without the costume")]
    public void EachBreakOfAnUnchangedCutsceneBlocksTheSave(string change)
    {
        using var ws = ThnBreaks.Workspace(change);
        var session = ws.Session("act");
        ThnBreaks.Apply(session, change);

        var refusal = session.GateRefusal();
        var save = session.Save();

        Assert.True(ThnBreaks.IsUnchanged(session, ScriptPath));
        Assert.NotNull(refusal);
        var error = Assert.Single(CutsceneErrors(refusal));
        Assert.Equal(ScriptPath, error.Source);
        Assert.Contains($"\"{ThnBreaks.Entity(change)}\"", error.Message);
        Assert.Contains($"\"{ThnBreaks.Item(change).Name}\"", error.Message);
        Assert.Equal([error], session.CutsceneErrors(ScriptPath));
        Assert.False(save.Saved);
        Assert.Contains(error, save.Problems);
        Assert.Null(PatchText(ws));
    }

    /// <summary>A cutscene of a lower mod that names the costume stops the save too. Its error comes before the error
    /// of the base script, in path order, and the Problems section of each tells the cause.</summary>
    [Fact]
    public void ACutsceneOfALowerModBlocksTheSave()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.Mod("low", new()
        {
            [@"files\" + LowScript] = """
                duration = 5
                entities = { { entity_name = "Guard", type = DEFORMABLE, template_name = "trent" } }
                events = {}
                """,
        });
        ws.WriteProfile("low", "act");
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

        Assert.Equal(new ThnFileSource(ThnFileKind.Lower, true), session.CutsceneSource(LowScript));
        Assert.Equal([(LowScript, TrentMissing.Replace("\"Man\"", "\"Guard\"")), (ScriptPath, TrentMissing)],
            CutsceneErrors(session.GateRefusal()!).Select(e => (e.Source, e.Message)));
        Assert.False(session.Save().Saved);
        Assert.Equal(ThnPage.LowerProblemsText, Note(session, LowScript));
        Assert.Equal(ThnPage.LowerProblemsText, Note(session));
    }

    /// <summary>
    /// The game makes no COMPOUND of a script that only <c>Act_CallThorn</c> lines name. So a delete of the ship stops
    /// the save for the base script only, although the script of space names the ship too. The search finds both
    /// scripts, and the check of each script decides.
    /// </summary>
    [Fact]
    public void AScriptThatOnlyActCallThornNamesKeepsTheSkipOfItsCompounds()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(Ship1));

        Assert.Equal([ThnDataWorkspace.SpaceScript, ScriptPath], session.ReferrerCutscenes());
        Assert.Equal([(ScriptPath, "The game crashes when it plays this cutscene. entity \"Ship\", template_name: The game " +
                                   "data has no ship \"ship1\".")],
            CutsceneErrors(session.GateRefusal()!).Select(e => (e.Source, e.Message)));
        Assert.Empty(session.CutsceneErrors(ThnDataWorkspace.SpaceScript));
    }

    /// <summary>A cutscene that the active mod changes and that names the deleted costume is in the gate one time. So
    /// each of its errors comes one time. It has unsaved edits, so the Problems section gives no note.</summary>
    [Fact]
    public void AChangedCutsceneThatNamesTheItemGivesEachErrorOneTime()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

        Assert.Equal([ScriptPath], session.ReferrerCutscenes());
        Assert.Equal([ScriptPath], session.GateCutscenes());
        Assert.Equal([TrentMissing], session.SaveProblems().Select(p => p.Message));
        Assert.Null(Note(session));
    }

    /// <summary>
    /// The save gate does not start the word index after an edit that keeps each answer of the game data. So the gate
    /// stays cheap when no change of the active mod breaks an item. The index belongs to one edit view, and a save that
    /// writes a file makes a new edit view. So the test reads the index after a gate call before the save, and after a
    /// gate call on the new edit view. The saved patch is then a file of the active mod.
    /// </summary>
    [Fact]
    public void TheSaveGateDoesNotStartTheWordIndexWithoutABrokenItem()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Ship1, "hit_pts", ["900"]));

        Assert.Null(session.GateRefusal());
        Assert.False(session.Mentions.IsStarted);
        var view = session.EditView;
        AssertSaved(session.Save());
        Assert.NotSame(view, session.EditView);
        Assert.Null(session.GateRefusal());
        Assert.False(session.Mentions.IsStarted);
    }

    /// <summary>
    /// The Problems section of the Cutscene page adds a note for a cutscene that the active mod does not change. The
    /// edit view gets such a cutscene from the lower layer, and it has no unsaved edits. A lower cutscene with unsaved
    /// edits keeps the kind Lower until the save writes its patch, so it gets no note. A cutscene of the active mod gets
    /// no note.
    /// </summary>
    [Fact]
    public void TheProblemsSectionTellsTheCauseOnlyForACutsceneThatTheActiveModDoesNotChange()
    {
        Assert.Equal("The active mod does not change this cutscene. A change of the game data in the active mod, for " +
                     "example an INI edit, removes or breaks an item of the cutscene. An animation file of the active " +
                     "mod can also remove a clip. Undo that change, or pick another item.", ThnPage.LowerProblemsText);
        Assert.Equal(ThnPage.LowerProblemsText, ThnPage.LowerProblemsNote(new ThnFileSource(ThnFileKind.Lower, false), false));
        Assert.Equal(ThnPage.LowerProblemsText, ThnPage.LowerProblemsNote(new ThnFileSource(ThnFileKind.Lower, true), false));
        Assert.Null(ThnPage.LowerProblemsNote(new ThnFileSource(ThnFileKind.Lower, false), true));
        foreach (var kind in new[] { ThnFileKind.PatchedHere, ThnFileKind.WholeFileHere, ThnFileKind.NewHere })
        {
            Assert.Null(ThnPage.LowerProblemsNote(new ThnFileSource(kind, false), false));
            Assert.Null(ThnPage.LowerProblemsNote(new ThnFileSource(kind, true), true));
        }

        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));
        Assert.Equal(ThnPage.LowerProblemsText, Note(session));
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));
        Assert.Equal(ThnFileKind.Lower, session.CutsceneSource(ScriptPath).Kind);
        Assert.Null(Note(session));
    }

    /// <summary>The Console line of the refusal names the script as its source and the entity in its message. A click
    /// on it selects the entity, because the line keeps the place.</summary>
    [Fact]
    public void TheConsoleLineOfTheRefusalNamesTheCutsceneAndTheEntity()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

        var line = Assert.Single(SaveReport.Lines(session.GateRefusal()!, ws.ModFolder("act")));

        Assert.Equal((ConsoleSeverity.Error, ScriptPath, TrentMissing), (line.Severity, line.Source, line.Message));
        Assert.Equal((ScriptPath, ThnElementKind.Entity), (line.Cutscene!.Path, line.Cutscene.Kind));
        Assert.Equal("act", line.Origin!.ModId);
    }
}
