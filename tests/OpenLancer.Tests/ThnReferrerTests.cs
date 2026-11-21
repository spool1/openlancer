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
/// active mod makes other than Ready, and the cutscenes of the edit view that use such an item. The fixture script
/// names a costume, equipment, an effect and a room that the script of space does not name. The active mod does not
/// change the script, so only a change of the game data can give the script an error. Each such cutscene is in the
/// result. A change that keeps each answer gives none, and does not start the index of the cutscene uses.
/// </summary>
public sealed class ThnReferrerTests
{
    /// <summary>A change that keeps each answer of the game data gives no item and no cutscene. It does not start the
    /// index of the cutscene uses. The changes are no edit, a new value of the ship, and a new ship from the template
    /// of the ship.</summary>
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
        Assert.False(session.Uses.IsStarted);
        AssertInvariant(session);
    }

    /// <summary>
    /// Each change of the active mod that removes or breaks an item of the script gives the item and the script. The
    /// script of space does not name the item, so it is not in the result. The changes are unsaved edits, a saved
    /// patch and files of the active mod. The files are a whole file, an animation file, and a bodyparts.ini without
    /// the <c>[Animations]</c> line of a file. A cascade of the search of the referrer errors counts too: the
    /// delete of a body part makes the costume that names it unusable. The script gets one error, on the entity or the
    /// event that names the item. A missing effect or clip is a name problem, not a crash.
    /// </summary>
    [Theory]
    [InlineData("delete costume", true)]
    [InlineData("rename costume", true)]
    [InlineData("body without a body part", true)]
    [InlineData("delete body part", true)]
    [InlineData("equipment without a model", true)]
    [InlineData("delete effect", false)]
    [InlineData("saved delete patch", true)]
    [InlineData("whole file without the costume", true)]
    [InlineData("remove the room line", true)]
    [InlineData("remove a clip", false)]
    [InlineData("bodyparts without the clip file", false)]
    public void EachBreakGivesTheItemAndTheScriptThatNamesIt(string change, bool crashes)
    {
        using var ws = ThnBreaks.Workspace(change);
        var session = ws.Session("act");
        var item = ThnBreaks.Item(change);

        ThnBreaks.Apply(session, change);

        // The active mod does not change the script, but the save gate reads it (#81).
        Assert.True(ThnBreaks.IsUnchanged(session, ScriptPath));
        Assert.Equal([ScriptPath], session.GateCutscenes());
        Assert.Equal([item], session.BrokenCutsceneItems());
        Assert.Equal([ScriptPath], session.ReferrerCutscenes());
        string[] clips = item.Kind == ThnItemKind.Animation ? [item.Name] : [];
        Assert.Equal(clips, session.Lists.RemovedClips());
        var error = Assert.Single(session.CutsceneErrors(ScriptPath)).Message;
        Assert.Contains($"\"{ThnBreaks.Entity(change)}\"", error);
        Assert.Contains($"\"{item.Name}\"", error);
        Assert.Equal(crashes, error.StartsWith(EditSession.CutsceneCrash, System.StringComparison.Ordinal));
        AssertInvariant(session);
    }

    /// <summary>
    /// The game crashes on an item without a model in the lower data, with or without the active mod. So an edit of
    /// another line of its section gives no item and no error, also in a cutscene that the active mod changes. The
    /// lower cutscene has the same problem in the lower data. The solar has no DA_archetype line, and the game makes
    /// a [Light] without a model.
    /// </summary>
    [Theory]
    [InlineData("station1", @"DATA\SOLAR\solararch.ini", "Solar", "no_model")]
    [InlineData("thruster1", ThnDataWorkspace.EquipmentPath, "Light", "light1")]
    public void AnEditOfAnotherLineOfAnItemWithoutAModelInTheLowerDataGivesNoItem(string replaced, string file,
        string type, string template)
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(ScriptPath, ThnDataWorkspace.Script.Replace($"\"{replaced}\"", $"\"{template}\"",
            System.StringComparison.Ordinal));
        var session = ws.Session("act");
        Assert.Empty(session.CutsceneErrors(ScriptPath));

        session.Execute(EditCommand.SetLinesFromText(new SectionRef(file, new SectionIdentity(type, template)), "mass",
            ["10"]));

        Assert.Empty(session.BrokenCutsceneItems());
        Assert.Empty(session.ReferrerCutscenes());
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        Assert.Empty(session.SaveProblems());
        AssertInvariant(session);
    }

    /// <summary>
    /// A whole file of the active mod keeps an item without a model, and changes only another line of it. The lists
    /// read the lower section by its identity. The lower section has the same model problem, so the active mod does not
    /// cause it. Thus the change gives no item and no error, also in a cutscene that the active mod changes.
    /// </summary>
    [Theory]
    [InlineData("station1", @"DATA\SOLAR\solararch.ini", "no_model")]
    [InlineData("thruster1", ThnDataWorkspace.EquipmentPath, "light1")]
    public void AWholeFileThatKeepsAnItemWithoutAModelGivesNoItem(string replaced, string file, string template)
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(ScriptPath, ThnDataWorkspace.Script.Replace($"\"{replaced}\"", $"\"{template}\"",
            System.StringComparison.Ordinal));
        var lower = File.ReadAllText(Path.Combine(ws.Base, file)).ReplaceLineEndings("\n");
        var nickname = $"nickname = {template}\n";
        Assert.Contains(nickname, lower);
        ThnBreaks.WriteModFile(ws, @"files\" + file, lower.Replace(nickname, nickname + "mass = 10\n",
            System.StringComparison.Ordinal));
        var session = ws.Session("act");

        Assert.Empty(session.BrokenCutsceneItems());
        Assert.Empty(session.ReferrerCutscenes());
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        Assert.Null(session.GateRefusal());
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        Assert.Empty(session.SaveProblems());
        AssertInvariant(session);
    }

    /// <summary>
    /// The engine stops for a room of petalDB that does not exist. The lists of the removed names do not have the
    /// values of petalDB. So the lists read the lower petalDB files when the active mod changes one. Thus a petalDB
    /// file of the active mod without the room gives the error in a cutscene that the active mod changes too. Another
    /// room heals it, and an undo brings it back. A room that the lower layer does not have either stays out of the
    /// errors and of the items.
    /// </summary>
    [Fact]
    public void ARoomThatTheActiveModRemovesBlocksAChangedCutscene()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(ScriptPath, ThnDataWorkspace.Script.Replace(
            "{ entity_name = \"Cam\"",
            "{ entity_name = \"Lost\", type = COMPOUND, template_name = \"lost_room\", userprops = { category = \"room\" } },\n" +
            "    { entity_name = \"Cam\"", System.StringComparison.Ordinal));
        ThnBreaks.WriteModFile(ws, @"files\" + ThnBreaks.PetalPath, ThnBreaks.PetalWithoutBar);
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

        ThnBreaks.WriteModFile(ws, @"files\" + ThnBreaks.PetalPath, ThnBreaks.PetalWithoutBar);
        var changed = ws.Session("act");
        Assert.True(changed.Lists.LowerLayerHasValue("petal_room", "BAR1"));
        Assert.False(changed.Lists.LowerLayerHasValue("petal_room", "bar2"));
        Assert.False(changed.Lists.LowerLayerHasValue("costume", "trent"));
        Assert.Equal(["bar1"], changed.Lists.RemovedValues("petal_room"));
    }

    /// <summary>
    /// The lists read the clips of the lower layer only when the active mod has an animation file or changes
    /// bodyparts.ini. The retail animation files hold about 100 MB. So the delete of a costume reads no lower clip, and
    /// a change of bodyparts.ini reads them one time for the session.
    /// </summary>
    [Fact]
    public void TheLowerClipsNeedAnAnimationFileOrAChangeOfBodyparts()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

        Assert.Equal([(ThnItemKind.Costume, "trent")], session.BrokenCutsceneItems());
        Assert.Empty(session.Lists.RemovedClips());
        Assert.Equal(0, session.Lists.LowerClipReads);
        session.Execute(AnimationWorkspace.AddLine(@"characters\animations\extra.anm"));
        Assert.Empty(session.Lists.RemovedClips());
        Assert.Equal(1, session.Lists.LowerClipReads);
        session.Undo();
        Assert.Empty(session.Lists.RemovedClips());
        Assert.Equal(1, session.Lists.LowerClipReads);
    }

    /// <summary>
    /// An open transaction and an open batch count too, for example a gesture of the Inspector and an agent call before
    /// its commit checks. A body that names no body part, a delete and a rename of the costume each break the script
    /// at once. A cancel heals it.
    /// </summary>
    [Theory]
    [InlineData("body without a body part")]
    [InlineData("delete costume")]
    [InlineData("rename costume")]
    public void AnOpenTransactionOrBatchGivesTheBrokenScriptAtOnce(string change)
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        EditCommand Change() => change switch
        {
            "delete costume" => EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")),
            "rename costume" => EditCommand.Rename(ThnDataWorkspace.Costume("trent"), "trent_new"),
            _ => EditCommand.SetLinesFromText(ThnDataWorkspace.Costume("trent"), "body", ["no_body"]),
        };

        using (var transaction = session.Begin("Change the costume"))
        {
            transaction.Apply(Change());
            AssertBroken(session);
            transaction.Cancel();
        }
        AssertHealed(session);

        using (var batch = session.BeginBatch("Change the costume"))
        {
            batch.Run(Change());
            AssertBroken(session);
            batch.Cancel();
        }
        AssertHealed(session);

        static void AssertBroken(EditSession session)
        {
            Assert.Equal([(ThnItemKind.Costume, "trent")], session.BrokenCutsceneItems());
            Assert.Equal([ScriptPath], session.ReferrerCutscenes());
            Assert.Contains("\"trent\"", Assert.Single(session.CutsceneErrors(ScriptPath)).Message);
            AssertInvariant(session);
        }

        static void AssertHealed(EditSession session)
        {
            Assert.Empty(session.BrokenCutsceneItems());
            Assert.Empty(session.ReferrerCutscenes());
            Assert.Empty(session.CutsceneErrors(ScriptPath));
            AssertInvariant(session);
        }
    }

    /// <summary>Two calls at one revision search the referrer errors one time and look up the index of the cutscene
    /// uses one time. A new revision searches again.</summary>
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
    /// of an unchanged script come without the search. The delete of the costume gives the error, and the index of
    /// the cutscene uses does not start.</summary>
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
        Assert.False(session.Uses.IsStarted);
        Assert.Equal(0, session.ReferrerLookups);
    }

    private const string WordScript = @"DATA\SCRIPTS\word_trent.thn";
    private const string JoinedScript = @"DATA\SCRIPTS\joined_trent.thn";

    /// <summary>
    /// The search reads the index of the cutscene uses (#87), not the words of the files. A script that holds "trent"
    /// only as a word does not use the costume: the costume of a DEFORMABLE with an Actor, the name of an entity and a
    /// SOUND. So the search leaves it out, and the gate does not open it. A script that joins the costume from parts
    /// uses it, and the search finds it. The word index finds the first script and does not find the second one.
    /// </summary>
    [Fact]
    public void TheSearchFindsTheCutscenesThatUseTheItemAndNotTheCutscenesThatHoldItsName()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(WordScript, """
            duration = 5
            entities = {
                { entity_name = "trent", type = DEFORMABLE, template_name = "trent", userprops = { Actor = "trent" } },
                { entity_name = "Voice", type = SOUND, template_name = "trent" }
            }
            events = {}
            """);
        ws.WriteBase(JoinedScript, """
            duration = 5
            entities = { { entity_name = "Hero", type = DEFORMABLE, template_name = "tr" .. "ent" } }
            events = {}
            """);
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

        Assert.Equal([JoinedScript, ScriptPath], session.ReferrerCutscenes());
        Assert.Equal([JoinedScript, ScriptPath], session.GateCutscenes().Order(System.StringComparer.OrdinalIgnoreCase));
        Assert.Equal([WordScript, ScriptPath], session.Mentions.Cutscenes("trent"));
        Assert.Equal(2, session.GateRefusal()!.Problems.Count);
        Assert.DoesNotContain(WordScript, session.OpenCutscenes, VfsPath.Comparer);
        Assert.Contains("\"Hero\"", Assert.Single(session.CutsceneErrors(JoinedScript)).Message);
        Assert.Empty(session.CutsceneErrors(WordScript));
        AssertInvariant(session);
    }

    /// <summary>
    /// The index reads the files of the edit view, so it does not have an unsaved edit of a cutscene. The gate reads
    /// each cutscene that the active mod changes too. So an edit that makes the script of space use the deleted costume
    /// gives the error of that script, also before the save.
    /// </summary>
    [Fact]
    public void AnEditThatMakesACutsceneUseABrokenItemGetsItsErrorBeforeTheSave()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));
        Assert.Equal([ScriptPath], session.ReferrerCutscenes());

        session.Execute(ThnEdits.AddEntity(ThnDataWorkspace.SpaceScript,
            ThnTemplates.NewEntity("DEFORMABLE", "Pilot", "trent", null, null)));

        Assert.Equal([ScriptPath], session.ReferrerCutscenes());
        Assert.Contains(ThnDataWorkspace.SpaceScript, session.GateCutscenes(), VfsPath.Comparer);
        Assert.Contains(session.GateRefusal()!.Problems,
            p => p.Source.Equals(ThnDataWorkspace.SpaceScript, System.StringComparison.OrdinalIgnoreCase) &&
                 p.Message.Contains("\"Pilot\"", System.StringComparison.Ordinal));
        AssertInvariant(session);
    }

    /// <summary>
    /// The index keeps the uses of each file by its content hash. The first build runs each script of the edit view. A
    /// save makes a new edit view, and the session starts the build for it at once. After a save of an INI edit, that
    /// build runs no script. After a save of a cutscene edit, it runs only the changed script, and the search finds the
    /// new use of that script.
    /// </summary>
    [Fact]
    public void TheIndexKeepsTheUsesOfEachFileAcrossASave()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        var scripts = VfsFiles.Find(session.EditView.CreateFileSystem(), ".thn").Count();
        Assert.False(session.Uses.IsStarted);

        session.StartCutsceneUses();
        Assert.Equal(scripts, session.Uses.LastBuild.Runs);
        Assert.True(session.Uses.IsReady);
        session.Execute(EditCommand.SetLinesFromText(new SectionRef(@"DATA\SHIPS\shiparch.ini",
            new SectionIdentity("Ship", "ship1")), "hit_pts", ["900"]));
        AssertSaved(session.Save());

        Assert.True(session.Uses.IsStarted);
        Assert.Equal(0, session.Uses.LastBuild.Runs);
        session.Execute(ThnEdits.AddEntity(ThnDataWorkspace.SpaceScript,
            ThnTemplates.NewEntity("DEFORMABLE", "Pilot", "trent", null, null)));
        AssertSaved(session.Save());

        Assert.Equal(1, session.Uses.LastBuild.Runs);
        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));
        Assert.Equal([ThnDataWorkspace.SpaceScript, ScriptPath],
            session.ReferrerCutscenes().Order(System.StringComparer.OrdinalIgnoreCase));
        AssertInvariant(session);
    }

    /// <summary>
    /// The editor shows the status line of the wait only when the gate waits (#87): a change of the active mod breaks
    /// an item, and the index of the cutscene uses is not complete. The question starts no build. After the build, the
    /// gate does not wait.
    /// </summary>
    [Fact]
    public void TheGateWaitsOnlyAfterABreakAndBeforeTheIndexIsComplete()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        Assert.False(session.CutsceneGateWaits);

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));

        Assert.True(session.CutsceneGateWaits);
        Assert.False(session.Uses.IsStarted);
        _ = session.Uses.LastBuild;
        Assert.False(session.CutsceneGateWaits);
        session.Undo();
        Assert.False(session.CutsceneGateWaits);
    }

    /// <summary>
    /// The rule of the search and of the save gate: each cutscene of the edit view with an error is a cutscene of the
    /// gate. Such a cutscene is a cutscene that the active mod changes, or a cutscene of the search.
    /// </summary>
    internal static void AssertInvariant(EditSession session)
    {
        var found = session.GateCutscenes().ToHashSet(VfsPath.Comparer);
        foreach (var path in VfsFiles.Find(session.EditView.CreateFileSystem(), ".thn"))
        {
            var normal = VfsPath.Normalize(path) ?? path;
            if (session.CutsceneErrors(normal).Count > 0)
                Assert.Contains(normal, found);
        }
    }
}

/// <summary>
/// The changes of the active mod that remove or break an item of the script of <see cref="ThnDataWorkspace"/>. The
/// tests of the search and of the save gate use them (#81). The active mod does not change the script, and the script
/// of space does not name the item. Some changes are files of the active mod, and the others are edits of the session.
/// </summary>
internal static class ThnBreaks
{
    public const string PetalPath = @"DATA\petaldb.ini";

    /// <summary>A whole petalDB file of the active mod without the room "bar1". The editor cannot change an
    /// <c>[ObjectTable]</c> section, because it has no nickname, so only such a file removes a room.</summary>
    public const string PetalWithoutBar = "[ObjectTable]\nroom = bar2, rooms\\bar2.cmp\nprop = cup, props\\cup.3db\n" +
                                          "cart = cart1, carts\\cart1.cmp\n";

    /// <summary>A whole bodyparts.ini of the active mod without the <c>[Animations]</c> line of more.anm. The script
    /// plays the clip "Sc_Sit" of that file. The editor cannot change an <c>[Animations]</c> line, so only such a file
    /// removes the clip.</summary>
    public const string BodypartsWithoutMore = "[Animations]\nanim = characters\\animations\\body.anm\n" +
                                               "anim = characters\\animations\\gone.anm\n[PetalAnimations]\n" +
                                               "anim = characters\\animations\\body.anm\n[Skeleton]\nsex = male\n" +
                                               "[Body]\nnickname = body1\nmesh = characters\\body1.dfm\n";

    /// <summary>A workspace for a change, with the files of the active mod that the change needs.</summary>
    public static TestWorkspace Workspace(string change)
    {
        var ws = change switch
        {
            "remove a clip" => ClipWorkspace((AnimationWorkspace.More, ["Sc_ModSit"])),
            "bodyparts without the clip file" => ClipWorkspace(),
            _ => ThnDataWorkspace.Create(),
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
            case "bodyparts without the clip file":
                WriteModFile(ws, @"files\" + ThnDataWorkspace.BodypartsPath, BodypartsWithoutMore);
                break;
        }
        return ws;
    }

    /// <summary>Runs the command of a change that is an edit of the session. A change that is a file of the active mod
    /// runs no command.</summary>
    public static void Apply(EditSession session, string change)
    {
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
            case "delete effect":
                session.Execute(EditCommand.DeleteSection(new SectionRef(@"DATA\FX\effects.ini",
                    new SectionIdentity("Effect", "fx1"))));
                break;
        }
    }

    /// <summary>The item that a change removes or breaks.</summary>
    public static (ThnItemKind Kind, string Name) Item(string change) => change switch
    {
        "equipment without a model" => (ThnItemKind.Equipment, "thruster1"),
        "delete effect" => (ThnItemKind.Effect, "fx1"),
        "remove the room line" => (ThnItemKind.Room, "bar1"),
        "remove a clip" or "bodyparts without the clip file" => (ThnItemKind.Animation, "Sc_Sit"),
        _ => (ThnItemKind.Costume, "trent"),
    };

    /// <summary>The entity of the script that names the item, or the target of the event that names the clip.</summary>
    public static string Entity(string change) => change switch
    {
        "equipment without a model" => "Gear",
        "delete effect" => "Fx",
        "remove the room line" => "Bar",
        _ => "Man",
    };

    /// <summary>True when the active mod does not change a cutscene. The edit view then gets it from the lower layer,
    /// and the session has no unsaved edit of it.</summary>
    public static bool IsUnchanged(EditSession session, string path) =>
        session.CutsceneSource(path).Kind == ThnFileKind.Lower && !session.HasUnsavedCutsceneEdits(path);

    /// <summary>The fixture of the clip gate. The script plays "Sc_Sit" of more.anm, and "Sc_Lost", which the lower
    /// layer does not have either.</summary>
    /// <param name="modFiles">The animation files of the active mod (see <see cref="AnimationWorkspace.Create"/>).
    /// A file of the active mod at the path of more.anm without "Sc_Sit" removes the clip.</param>
    public static TestWorkspace ClipWorkspace(params (string Path, string[] Names)[] modFiles)
    {
        var ws = AnimationWorkspace.Create(modFiles);
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
    public static void WriteModFile(TestWorkspace ws, string relativePath, string text)
    {
        var file = Path.Combine(ws.ModFolder("act"), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
}
