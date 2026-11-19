using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The search for the cutscenes that a change of the active mod breaks (#81). It gives the items whose answer the
/// active mod makes other than Ready, and the cutscenes of the edit view that name such an item. The fixture script
/// names a costume, equipment, an effect and a room that the script of space does not name. The active mod does not
/// change the script, so only a change of the game data can give the script an error. Each such cutscene is in the
/// result. A change that keeps each answer gives none, and does not start the word index.
/// </summary>
public sealed class ThnReferrerTests
{
    private const string PetalPath = @"DATA\petaldb.ini";

    /// <summary>A whole petalDB file of the active mod without the room "bar1". The editor cannot change an
    /// <c>[ObjectTable]</c> section, because it has no nickname, so only such a file removes a room.</summary>
    private const string PetalWithoutBar = "[ObjectTable]\nroom = bar2, rooms\\bar2.cmp\nprop = cup, props\\cup.3db\n" +
                                           "cart = cart1, carts\\cart1.cmp\n";

    /// <summary>A change that keeps each answer of the game data gives no item and no cutscene, and does not start the
    /// word index: no edit, a new value of the ship, and a new ship from the template of the ship.</summary>
    [Fact]
    public void AChangeThatKeepsEachAnswerGivesNoCutsceneAndDoesNotStartTheIndex()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        var ship = new SectionRef(@"DATA\SHIPS\shiparch.ini", new SectionIdentity("Ship", "ship1"));

        Assert.Empty(session.ReferrerCutscenes());
        session.Execute(EditCommand.SetLinesFromText(ship, "hit_pts", ["900"]));
        Assert.Empty(session.BrokenCutsceneItems());
        Assert.Empty(session.ReferrerCutscenes());
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, ship, "ship2")));

        Assert.Empty(session.BrokenCutsceneItems());
        Assert.Empty(session.ReferrerCutscenes());
        Assert.False(session.Mentions.IsStarted);
        AssertInvariant(session);
    }

    /// <summary>
    /// Each change of the active mod that removes or breaks an item of the script gives the item and the script. The
    /// script of space does not name the item, so it is not in the result. The changes are unsaved edits, a saved patch,
    /// a whole file of the active mod and an animation file of the active mod. A cascade of the search of the referrer
    /// errors counts too: the delete of a body part makes the costume that names it unusable.
    /// </summary>
    [Theory]
    [InlineData("delete costume")]
    [InlineData("rename costume")]
    [InlineData("body without a body part")]
    [InlineData("delete body part")]
    [InlineData("equipment without a model")]
    [InlineData("delete effect")]
    [InlineData("saved delete patch")]
    [InlineData("whole file without the costume")]
    [InlineData("second costume without a body part")]
    [InlineData("remove the room line")]
    [InlineData("remove a clip")]
    public void EachBreakGivesTheItemAndTheScriptThatNamesIt(string change)
    {
        using var ws = change == "remove a clip" ? ClipWorkspace() : ThnDataWorkspace.Create();
        (ThnItemKind, string) expected = change switch
        {
            "equipment without a model" => (ThnItemKind.Equipment, "thruster1"),
            "delete effect" => (ThnItemKind.Effect, "fx1"),
            "remove the room line" => (ThnItemKind.Room, "bar1"),
            "remove a clip" => (ThnItemKind.Animation, "Sc_Sit"),
            _ => (ThnItemKind.Costume, "trent"),
        };
        switch (change)
        {
            case "saved delete patch":
                WriteModFile(ws, @"patches\" + ThnDataWorkspace.CostumesPath, "[Costume]\nnickname = trent\nol.delete = true\n");
                break;
            case "whole file without the costume":
                WriteModFile(ws, @"files\" + ThnDataWorkspace.CostumesPath, "[Costume]\nnickname = other\nbody = body1\n");
                break;
            case "remove the room line":
                WriteModFile(ws, @"files\" + PetalPath, PetalWithoutBar);
                break;
        }
        var session = ws.Session("act");
        switch (change)
        {
            case "delete costume":
                session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));
                break;
            case "rename costume":
                session.Execute(EditCommand.Rename(ThnDataWorkspace.Costume("trent"), "trent_new"));
                break;
            case "body without a body part":
                session.Execute(EditCommand.SetLinesFromText(ThnDataWorkspace.Costume("trent"), "body", ["no_body"]));
                break;
            case "delete body part":
                session.Execute(EditCommand.DeleteSection(
                    new SectionRef(ThnDataWorkspace.BodypartsPath, new SectionIdentity("Body", "body1"))));
                break;
            case "equipment without a model":
                session.Execute(EditCommand.RemoveName(ThnDataWorkspace.Equipment("Thruster", "thruster1"), "DA_archetype"));
                break;
            case "second costume without a body part":
                // The last section with the name gives the costume, as in the engine.
                session.Execute(EditCommand.AddSection(ThnDataWorkspace.CostumesPath, "Costume",
                    [ThnDataWorkspace.Line("nickname", "trent"), ThnDataWorkspace.Line("body", "no_body")]));
                break;
            case "delete effect":
                session.Execute(EditCommand.DeleteSection(new SectionRef(@"DATA\FX\effects.ini",
                    new SectionIdentity("Effect", "fx1"))));
                break;
        }

        Assert.Empty(session.GateCutscenes());
        Assert.Equal([expected], session.BrokenCutsceneItems());
        Assert.Equal([ScriptPath], session.ReferrerCutscenes());
        AssertInvariant(session);
    }

    /// <summary>
    /// The engine stops for a room of petalDB that does not exist. The lists of the removed names do not have the values
    /// of petalDB, so the lists read the lower petalDB files when the active mod changes one. So a petalDB file of the
    /// active mod without the room gives the error in a cutscene that the active mod changes too. Another room heals it,
    /// and an undo brings it back. A room that the lower layer does not have either stays out of the errors and of the
    /// items.
    /// </summary>
    [Fact]
    public void ARoomThatTheActiveModRemovesBlocksAChangedCutscene()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(ScriptPath, ThnDataWorkspace.Script.Replace(
            "{ entity_name = \"Cam\"",
            "{ entity_name = \"Lost\", type = COMPOUND, template_name = \"lost_room\", userprops = { category = \"room\" } },\n" +
            "    { entity_name = \"Cam\"", System.StringComparison.Ordinal));
        WriteModFile(ws, @"files\" + PetalPath, PetalWithoutBar);
        var session = ws.Session("act");
        const string barMissing = "The game crashes when it plays this cutscene. entity \"Bar\", template_name: The game " +
                                  "data has no room of petalDB \"bar1\".";

        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));

        Assert.Equal([barMissing], session.CutsceneErrors(ScriptPath).Select(e => e.Message));
        Assert.Contains(session.SaveProblems(), p => p.Message == barMissing);
        Assert.Equal(["bar1"], session.Lists.RemovedValues("petal_room"));
        Assert.Empty(session.Lists.RemovedValues("petal_prop"));
        Assert.Equal([(ThnItemKind.Room, "bar1")], session.BrokenCutsceneItems());
        ThnDataWorkspace.SetTemplate(session, "Bar", "bar2");
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        session.Undo();
        Assert.Equal([barMissing], session.CutsceneErrors(ScriptPath).Select(e => e.Message));
    }

    /// <summary>The lists read the lower petalDB files only when the active mod changes one of them, also in an open
    /// batch. An edit of another file reads none of them, and a cancel of the batch puts the answer back.</summary>
    [Fact]
    public void TheLowerValuesNeedAChangeOfAPetalFile()
    {
        using var ws = ThnDataWorkspace.Create();
        using (var session = ws.Session("act"))
        {
            session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

            Assert.False(session.Lists.LowerLayerHasValue("petal_room", "bar1"));
            Assert.Empty(session.Lists.RemovedValues("petal_room"));
            Assert.Empty(session.Lists.RemovedClips());
            using (var batch = session.BeginBatch("Add a prop"))
            {
                batch.Run(EditCommand.AddSection(@"DATA\petaldb_more.ini", "ObjectTable",
                    [ThnDataWorkspace.Line("prop", "jug")]));
                Assert.True(session.Lists.LowerLayerHasValue("petal_room", "BAR1"));
                batch.Cancel();
            }
            Assert.False(session.Lists.LowerLayerHasValue("petal_room", "bar1"));
        }

        WriteModFile(ws, @"files\" + PetalPath, PetalWithoutBar);
        var changed = ws.Session("act");
        Assert.True(changed.Lists.LowerLayerHasValue("petal_room", "BAR1"));
        Assert.False(changed.Lists.LowerLayerHasValue("petal_room", "bar2"));
        Assert.False(changed.Lists.LowerLayerHasValue("costume", "trent"));
        Assert.Equal(["bar1"], changed.Lists.RemovedValues("petal_room"));
    }

    /// <summary>
    /// An open transaction and an open batch count too, for example a gesture of the Inspector and an agent call before
    /// its commit checks. A body that names no body part makes the costume unusable at once. A cancel heals it.
    /// </summary>
    [Fact]
    public void AnOpenTransactionOrBatchGivesTheBrokenScriptAtOnce()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        var noBody = EditCommand.SetLinesFromText(ThnDataWorkspace.Costume("trent"), "body", ["no_body"]);

        using (var transaction = session.Begin("Set the body"))
        {
            transaction.Apply(noBody);
            Assert.Equal([ScriptPath], session.ReferrerCutscenes());
            AssertInvariant(session);
            transaction.Cancel();
        }
        Assert.Empty(session.ReferrerCutscenes());

        using (var batch = session.BeginBatch("Set the body"))
        {
            batch.Run(noBody);
            Assert.Equal([ScriptPath], session.ReferrerCutscenes());
            AssertInvariant(session);
            batch.Cancel();
        }
        Assert.Empty(session.ReferrerCutscenes());
        AssertInvariant(session);
    }

    /// <summary>Two calls at one revision search the referrer errors one time and look up the word index one time. A
    /// new revision searches again.</summary>
    [Fact]
    public void TheResultIsCachedForEachRevision()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));
        var walks = session.Lists.Walks;
        var lookups = session.ReferrerLookups;

        var first = session.ReferrerCutscenes();
        Assert.Same(first, session.ReferrerCutscenes());
        Assert.Same(session.Lists.ReferrerErrors(), session.Lists.ReferrerErrors());

        Assert.Equal((walks + 1, lookups + 1), (session.Lists.Walks, session.ReferrerLookups));
        session.Undo();
        Assert.Empty(session.ReferrerCutscenes());
        Assert.Equal((walks + 2, lookups + 1), (session.Lists.Walks, session.ReferrerLookups));
    }

    /// <summary>The Problems section of the Cutscene page reads the errors of a cutscene in each frame. So the errors
    /// of an unchanged script come without the search: the delete of the costume gives the error, and the word index
    /// does not start.</summary>
    [Fact]
    public void TheErrorsOfACutsceneDoNotStartTheSearch()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

        Assert.Equal(["The game crashes when it plays this cutscene. entity \"Man\", template_name: The costume \"trent\" " +
                      "is not in the game data. A DEFORMABLE without userprops.Actor needs it."],
            session.CutsceneErrors(ScriptPath).Select(e => e.Message));
        Assert.NotEmpty(session.CutsceneChecks(ScriptPath));
        Assert.False(session.Mentions.IsStarted);
        Assert.Equal(0, session.ReferrerLookups);
    }

    /// <summary>The fixture of the clip gate: an animation file of the active mod replaces the file of the clip
    /// "Sc_Sit". The script plays "Sc_Sit" and "Sc_Lost", which the lower layer does not have either.</summary>
    private static TestWorkspace ClipWorkspace()
    {
        var ws = AnimationWorkspace.Create((AnimationWorkspace.More, ["Sc_ModSit"]));
        ws.WriteBase(ScriptPath, """
            duration = 10
            entities = {
                { entity_name = "Man", type = DEFORMABLE, template_name = "trent" },
                { entity_name = "Cam", type = CAMERA, cameraprops = { fovh = 10 } }
            }
            events = {
                { 1, START_MOTION, { "Man" }, { animation = "Sc_Sit" } },
                { 2, START_MOTION, { "Man" }, { animation = "Sc_Lost" } }
            }
            """);
        return ws;
    }

    /// <summary>Writes a file into the folder of the active mod, by its path relative to the mod folder.</summary>
    private static void WriteModFile(TestWorkspace ws, string relativePath, string text)
    {
        var file = Path.Combine(ws.ModFolder("act"), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    /// <summary>
    /// The rule of the search: each cutscene of the edit view with an error is a cutscene that the active mod changes,
    /// or a cutscene of the result.
    /// </summary>
    private static void AssertInvariant(EditSession session)
    {
        var found = session.ReferrerCutscenes().Concat(session.GateCutscenes()).ToHashSet(VfsPath.Comparer);
        foreach (var path in VfsFiles.Find(session.EditView.CreateFileSystem(), ".thn"))
        {
            var normal = VfsPath.Normalize(path) ?? path;
            if (session.CutsceneErrors(normal).Count > 0)
                Assert.Contains(normal, found);
        }
    }
}
