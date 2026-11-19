using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The new files of the folder of the active mod (decision O2). The editor makes, edits and deletes each INI file below
/// <c>DATA\MODS\&lt;mod id&gt;</c> of the active mod as a whole file. The tests cover the undo, the save, the recovery
/// file, the repo guard and the view of the target checks. A whole file outside that folder and outside
/// <c>DATA\MISSIONS</c> stays read-only. The data is invented.
/// </summary>
public sealed class NewModFileTests
{
    internal const string NewPath = @"DATA\MODS\act\SYSTEMS\Ol01\Ol01.ini";
    private const string FilesPath = @"files\" + NewPath;
    private const string NameKey = "ol01_sun_name";

    internal const string SystemText = """
        [SystemInfo]
        space_color = 0, 0, 0

        [Object]
        nickname = Ol01_sun
        ids_name = $str:ol01_sun_name
        pos = 0, 0, 0
        archetype = station1

        [zone]
        nickname = Zone_Ol01_sun_death
        pos = 0, 0, 0
        shape = SPHERE
        size = 2000
        """;

    internal static readonly SectionRef Sun = new(NewPath, new SectionIdentity("Object", "Ol01_sun"));

    /// <summary>The new system file with the text of its name, as one edit.</summary>
    internal static EditCommand New() => EditCommand.Group("New system file",
    [
        EditCommand.NewFile(NewPath, ContentPathsTests.Sections(SystemText)),
        EditCommand.SetText(StringKind.String, NameKey, "Ol01 Sun"),
    ]);

    private static string FileOf(TestWorkspace ws) => Path.Combine(ws.ModFolder("act"), FilesPath);

    private static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.DoesNotContain(result.Problems, p => p.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NewFileMakesAnEditableWholeFileOfTheMod()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        Assert.Null(session.Get(Sun));

        session.Execute(EditCommand.NewFile(NewPath, ContentPathsTests.Sections(SystemText)));

        Assert.Equal("New file Ol01.ini", session.UndoLabel);
        var document = session.OpenDocument(NewPath)!;
        Assert.True(document.IsWholeFile);
        Assert.True(document.State.Exists);
        Assert.Null(document.ReadOnlyReason);
        Assert.Equal(["SystemInfo", "Object", "zone"], session.Sections(NewPath).Select(s => s.Id.Type));
        var sun = session.Get(Sun)!;
        Assert.Null(sun.ReadOnlyReason);
        Assert.True(sun.IsNew);
        Assert.Equal((ValueOrigin.ActiveMod, "act"), session.Origin(Sun, "pos"));
        session.Execute(EditCommand.SetLinesFromText(Sun, "pos", ["10, 20, 30"]));
        Assert.Equal(["10, 20, 30"], EditSessionTests.Values(session, Sun, "pos"));
    }

    [Fact]
    public void UndoAndRedoTakeANewFileBackAndGiveItAgain()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.NewFile(NewPath, ContentPathsTests.Sections(SystemText)));
        var sections = session.Sections(NewPath);

        session.Undo();
        Assert.Null(session.Get(Sun));
        Assert.Empty(session.Sections(NewPath));
        Assert.False(session.OpenDocument(NewPath)!.State.Exists);
        Assert.False(session.IsDirty);
        Assert.Equal("New file Ol01.ini", session.RedoLabel);

        session.Redo();
        Assert.Equal(sections, session.Sections(NewPath));
        Assert.NotNull(session.Get(Sun));
        Assert.True(session.IsDirty);
    }

    /// <summary>A new file and the delete of a file are structural changes that list each section of the file. A new
    /// file without sections is a structural change too. So the Scene and the marks of the agent edits see each added
    /// and each removed section: the delete of an agent removes the marks of the sections of its file.</summary>
    [Fact]
    public void NewFileAndDeleteFileAreStructuralChangesThatListTheSectionsOfTheFile()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        RunAsAgent(session, EditCommand.NewFile(NewPath, ContentPathsTests.Sections(SystemText)));
        var sections = session.Sections(NewPath);
        Assert.Equal(3, sections.Count);
        Assert.True(changes[^1].Structural);
        Assert.Equal(sections, changes[^1].Sections);
        Assert.True(session.AgentSections.SetEquals(sections));

        session.Execute(EditCommand.NewFile(@"DATA\MODS\act\empty.ini", []));
        Assert.True(changes[^1].Structural);
        Assert.Empty(changes[^1].Sections);

        RunAsAgent(session, EditCommand.DeleteFile(NewPath));
        Assert.True(changes[^1].Structural);
        Assert.Equal(sections, changes[^1].Sections);
        Assert.Empty(session.AgentSections);
        session.Undo();
        Assert.True(session.AgentSections.SetEquals(sections));
        session.Redo();
        Assert.True(changes[^1].Structural);
        Assert.Equal(sections, changes[^1].Sections);
        Assert.Empty(session.AgentSections);
    }

    /// <summary>Runs a command as one entry of an agent, as the agent tools do.</summary>
    private static void RunAsAgent(EditSession session, EditCommand command)
    {
        using var batch = session.BeginBatch(command.Label, EditOrigin.Agent);
        batch.Run(command);
        batch.Commit();
    }

    /// <summary>A save writes the new file into the files folder of the mod as text. Its text placeholders stay, and the
    /// text goes into strings.json. After the save, and after the profile opens again, the file is an editable whole
    /// file.</summary>
    [Fact]
    public void ASaveWritesTheNewFileAsWholeTextWithItsPlaceholders()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(New());

        var result = session.Save();

        AssertSaved(result);
        Assert.Contains(FileOf(ws), result.Written);
        var text = ws.ReadModFile(FilesPath);
        Assert.StartsWith("[SystemInfo]", text);
        Assert.Contains("ids_name = $str:ol01_sun_name", text);
        Assert.Contains("size = 2000", text);
        Assert.Contains("Ol01 Sun", ws.ReadModFile(Mod.StringsFile));
        Assert.False(Directory.Exists(Path.Combine(ws.ModFolder("act"), Mod.PatchesFolder)));
        Assert.True(session.OpenDocument(NewPath)!.IsWholeFile);
        session.Execute(EditCommand.SetLinesFromText(Sun, "pos", ["1, 2, 3"]));
        AssertSaved(session.Save());
        session.Dispose();

        var reopened = ws.Session("act");
        var document = reopened.OpenDocument(NewPath)!;
        Assert.True(document.IsWholeFile);
        Assert.Null(document.ReadOnlyReason);
        Assert.Equal(["1, 2, 3"], EditSessionTests.Values(reopened, Sun, "pos"));
        Assert.Equal(["$str:ol01_sun_name"], EditSessionTests.Values(reopened, Sun, "ids_name"));
        reopened.Execute(EditCommand.SetLinesFromText(Sun, "pos", ["4, 5, 6"]));
        AssertSaved(reopened.Save());
        Assert.Contains("pos = 4, 5, 6", ws.ReadModFile(FilesPath));
    }

    /// <summary>The save checks the text placeholders of a new file (rule S8).</summary>
    [Fact]
    public void TheSaveRefusesANewFileWhoseTextIsMissing()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.NewFile(NewPath, ContentPathsTests.Sections(SystemText)));

        var result = session.Save();

        Assert.False(result.Saved);
        var error = Assert.Single(result.Problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.Equal($"{Sun.Id}: '$str:{NameKey}' is not in the strings.json of mod 'act'. Add the text, or change the value.",
            error.Message);
        Assert.Equal(Sun, error.Target);
        Assert.False(File.Exists(FileOf(ws)));
    }

    /// <summary>A delete saves a removal of the file. An undo gives the file back, a redo deletes it again, and the save
    /// after an undo writes the file again. After a delete, the path is free for a new file before the save.</summary>
    [Fact]
    public void DeleteFileSavesARemovalAndUndoGivesTheFileBack()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(New());
        AssertSaved(session.Save());

        session.Execute(EditCommand.DeleteFile(NewPath));
        Assert.Equal("Delete file Ol01.ini", session.UndoLabel);
        Assert.Null(session.Get(Sun));
        Assert.False(ContentPaths.Taken(session, NewPath));
        session.Undo();
        Assert.NotNull(session.Get(Sun));
        Assert.True(ContentPaths.Taken(session, NewPath));
        Assert.Equal("Delete file Ol01.ini", session.RedoLabel);
        session.Redo();
        Assert.Null(session.Get(Sun));
        Assert.False(ContentPaths.Taken(session, NewPath));
        Assert.Equal("Delete file Ol01.ini", session.UndoLabel);
        var deleted = session.Save();
        AssertSaved(deleted);
        Assert.Equal([FileOf(ws)], deleted.Deleted);
        Assert.False(File.Exists(FileOf(ws)));

        session.Undo();
        Assert.NotNull(session.Get(Sun));
        var written = session.Save();
        AssertSaved(written);
        Assert.Equal([FileOf(ws)], written.Written);

        session.Execute(EditCommand.DeleteFile(NewPath));
        session.Execute(EditCommand.NewFile(NewPath, ContentPathsTests.Sections("[SystemInfo]\nspace_color = 1, 1, 1\n")));
        AssertSaved(session.Save());
        Assert.Contains("space_color = 1, 1, 1", ws.ReadModFile(FilesPath));
        Assert.DoesNotContain("Ol01_sun", ws.ReadModFile(FilesPath));
    }

    [Fact]
    public void NewFileRefusesEachCase()
    {
        using var ws = ListWorkspace.Create(
            lowerFiles: new() { [@"files\DATA\MODS\act\lower.ini"] = "[Thing]\nnickname = a\n" },
            activeFiles: new() { [@"files\DATA\MODS\act\saved.ini"] = "[Thing]\nnickname = b\n" });
        var session = ws.Session("act");
        void Refused<T>(string path, string? message = null) where T : Exception
        {
            var e = Assert.Throws<T>(() => session.Execute(EditCommand.NewFile(path, [])));
            if (message != null)
                Assert.StartsWith(message, e.Message);
        }

        // A path outside the folder of the active mod, and a file that is not an INI file.
        const string outside = @"A new INI file goes into the folder DATA\MODS\act of the active mod.";
        Refused<ArgumentException>(@"DATA\MODS\other\x.ini", outside);
        Refused<ArgumentException>(@"DATA\UNIVERSE\SYSTEMS\Ol01\Ol01.ini", outside);
        Refused<ArgumentException>(@"DATA\MODS\act\x.txt", @"DATA\MODS\act\x.txt is not an INI file.");
        Refused<ArgumentException>(@"C:\DATA\MODS\act\x.ini");
        // A path that the lower layer, the active mod or W has, in any letter case.
        Refused<InvalidOperationException>(@"data\mods\ACT\LOWER.ini", "The game data, a mod or an unsaved edit has a file at");
        Refused<InvalidOperationException>(@"DATA\MODS\act\saved.ini");
        session.Execute(EditCommand.NewFile(@"DATA\MODS\act\new.ini", []));
        Refused<InvalidOperationException>(@"DATA\MODS\ACT\NEW.INI");
        Assert.Equal("New file new.ini", session.UndoLabel);

        // A read-only session.
        var second = ws.SecondSession("act");
        Assert.NotNull(second.ReadOnlyReason);
        Assert.Equal(second.ReadOnlyReason, Assert.Throws<InvalidOperationException>(() =>
            second.Execute(EditCommand.NewFile(@"DATA\MODS\act\other.ini", []))).Message);
        // A name that a patch header cannot hold.
        Assert.Throws<ArgumentException>(() => EditCommand.NewFile(@"DATA\MODS\act\x.ini", [new SectionEntries("a]b", [])]));
    }

    /// <summary>The editor writes no new INI file into a mod in the OpenLancer repo (decision D70), so New file refuses
    /// such a mod with the message of the save.</summary>
    [Fact]
    public void NewFileRefusesAModInTheRepo()
    {
        using var ws = ListWorkspace.Create();
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "<Solution />");
        var session = ws.Session("act");

        var e = Assert.Throws<InvalidOperationException>(() => session.Execute(New()));

        Assert.Equal(RepoMessage(ws), e.Message);
        Assert.False(session.IsDirty);
        Assert.Null(session.Get(Sun));
    }

    /// <summary>The save refuses a new INI file of the folder of the mod when the mod is in the OpenLancer repo. A delete
    /// writes no file, so the save takes it.</summary>
    [Fact]
    public void TheSaveRefusesANewFileInAModInTheRepoButDeletesOne()
    {
        using var ws = ListWorkspace.Create(activeFiles: new() { [@"files\DATA\MODS\act\old.ini"] = "[Thing]\nnickname = a\n" });
        var session = ws.Session("act");
        session.Execute(New());
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "<Solution />");

        var result = session.Save();

        Assert.False(result.Saved);
        Assert.Equal(RepoMessage(ws), Assert.Single(result.Problems, p => p.Severity == DiagnosticSeverity.Error).Message);
        Assert.False(File.Exists(FileOf(ws)));

        session.Undo();
        session.Execute(EditCommand.DeleteFile(@"DATA\MODS\act\old.ini"));
        AssertSaved(session.Save());
        Assert.False(File.Exists(Path.Combine(ws.ModFolder("act"), @"files\DATA\MODS\act\old.ini")));
    }

    [Fact]
    public void DeleteFileRefusesEachCase()
    {
        using var ws = ListWorkspace.Create(
            lowerFiles: new() { [@"files\DATA\MODS\act\shared.ini"] = "[Thing]\nnickname = a\n" },
            activeFiles: new()
            {
                [@"files\DATA\MODS\act\shared.ini"] = "[Thing]\nnickname = b\n",
                [@"files\" + WholeFileScriptTests.ScriptPath] = WholeFileScriptTests.Script,
            });
        var session = ws.Session("act");

        // A file that W does not have, also a new file after the undo of its create.
        Assert.Throws<ArgumentException>(() => session.Execute(EditCommand.DeleteFile(@"DATA\MODS\act\none.ini")));
        session.Execute(EditCommand.NewFile(NewPath, []));
        session.Undo();
        Assert.Throws<ArgumentException>(() => session.Execute(EditCommand.DeleteFile(NewPath)));
        // A file of the game data, and a whole mission file, which the mission commands delete.
        var only = $"The editor deletes only an INI file that the active mod adds in its folder DATA\\MODS\\act.";
        Assert.StartsWith(only, Assert.Throws<InvalidOperationException>(() =>
            session.Execute(EditCommand.DeleteFile(ListWorkspace.Li01Path))).Message);
        Assert.StartsWith(only, Assert.Throws<InvalidOperationException>(() =>
            session.Execute(EditCommand.DeleteFile(WholeFileScriptTests.ScriptPath))).Message);
        // A file of the folder of the mod that the lower layer has too.
        Assert.StartsWith(@"The game data or a lower mod has the file DATA\MODS\act\shared.ini too.",
            Assert.Throws<InvalidOperationException>(() =>
                session.Execute(EditCommand.DeleteFile(@"DATA\MODS\act\shared.ini"))).Message);
        Assert.False(session.IsDirty);
    }

    /// <summary>An INI file of the folder of the active mod stays an editable whole file also when the base data or a
    /// lower mod has a file at the same path, because the rule of the whole files has no test of the lower layer. The
    /// save writes the whole file. The editor does not delete it, because the game then uses the lower file
    /// again.</summary>
    [Fact]
    public void AnIniFileOfTheFolderOfTheModStaysEditableWhenTheLowerLayerHasIt()
    {
        const string inLower = @"DATA\MODS\act\shared.ini";
        const string inBase = @"DATA\MODS\act\inbase.ini";
        using var ws = ListWorkspace.Create(
            lowerFiles: new() { [@"files\" + inLower] = "[Thing]\nnickname = a\nprice = 1\n" },
            activeFiles: new()
            {
                [@"files\" + inLower] = "[Thing]\nnickname = b\nprice = 1\n",
                [@"files\" + inBase] = "[Thing]\nnickname = c\nprice = 1\n",
            });
        ws.WriteBase(inBase, "[Thing]\nnickname = d\nprice = 1\n");
        var session = ws.Session("act");

        foreach (var (path, key) in new[] { (inLower, "b"), (inBase, "c") })
        {
            var document = session.OpenDocument(path)!;
            Assert.True(document.IsWholeFile);
            Assert.Null(document.ReadOnlyReason);
            session.Execute(EditCommand.SetLinesFromText(new SectionRef(path, new SectionIdentity("Thing", key)), "price",
                ["5"]));
            Assert.StartsWith($"The game data or a lower mod has the file {path} too.",
                Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.DeleteFile(path))).Message);
        }
        AssertSaved(session.Save());

        Assert.Equal("[Thing]\nnickname = b\nprice = 5\n", ws.ReadModFile(@"files\" + inLower).ReplaceLineEndings("\n"));
        Assert.Equal("[Thing]\nnickname = c\nprice = 5\n", ws.ReadModFile(@"files\" + inBase).ReplaceLineEndings("\n"));
    }

    /// <summary>A whole file of the active mod outside its folder that replaces a file of the lower layer stays
    /// read-only, except a mission file. A whole file in the folder of another mod stays read-only too.</summary>
    [Fact]
    public void AWholeFileOutsideTheFolderOfTheModStaysReadOnly()
    {
        const string other = @"DATA\MODS\other\x.ini";
        const string otherLower = @"DATA\MODS\other\lower.ini";
        using var ws = ListWorkspace.Create(activeFiles: new()
        {
            [@"files\" + ListWorkspace.GoodsPath] = "[Good]\nnickname = g_whole\nprice = 1\n",
            [@"files\" + other] = "[Thing]\nnickname = x\n",
            [@"files\" + otherLower] = "[Thing]\nnickname = y\n",
            [@"files\" + WholeFileScriptTests.ScriptPath] = WholeFileScriptTests.Script,
        });
        ws.WriteBase(otherLower, "[Thing]\nnickname = y\n");
        ws.WriteBase(WholeFileScriptTests.ScriptPath, WholeFileScriptTests.Script);
        var session = ws.Session("act");

        var goods = session.OpenDocument(ListWorkspace.GoodsPath)!;
        Assert.False(goods.IsWholeFile);
        Assert.Contains("replaces", goods.ReadOnlyReason);
        Assert.Contains("replaces", session.OpenDocument(otherLower)!.ReadOnlyReason);
        // The lower layer does not have the file, so the edit view shows it only through the lists.
        Assert.Null(session.OpenDocument(other));
        Assert.Null(session.Lists.TableOf(other)!.Document);
        // A whole mission file that replaces a lower file stays editable.
        var script = session.OpenDocument(WholeFileScriptTests.ScriptPath)!;
        Assert.True(script.IsWholeFile);
        Assert.Null(script.ReadOnlyReason);
    }

    /// <summary>The view of the target checks gives W of a new file of the folder of the mod with the mods, and no
    /// sections without them. A file that W deleted has no sections.</summary>
    [Fact]
    public void TheTargetCheckViewGivesWOfANewFileAndNoSectionsForADeletedFile()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(New());

        var view = new EditSession.SessionCheckView(session, session.ActiveMod!);
        Assert.Contains(NewPath, view.ChangedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Same(session.OpenDocument(NewPath)!.Working, view.Sections(NewPath, withMods: true));
        Assert.Null(view.Sections(NewPath, withMods: false));

        AssertSaved(session.Save());
        session.Execute(EditCommand.DeleteFile(NewPath));
        var deleted = new EditSession.SessionCheckView(session, session.ActiveMod!);
        Assert.Null(deleted.Sections(NewPath, withMods: true));
        Assert.Null(deleted.Sections(NewPath, withMods: false));
    }

    /// <summary>The recovery file keeps an unsaved new file. The next session restores it while it has the recovery
    /// file, and its save writes the file.</summary>
    [Fact]
    public void TheRecoveryFileRestoresAnUnsavedNewFile()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(New());
        session.Execute(EditCommand.SetLinesFromText(Sun, "pos", ["7, 8, 9"]));
        Assert.Empty(session.WriteRecovery());
        session.Dispose();

        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        Assert.Empty(restored.RestoreRecovery());

        Assert.True(restored.OpenDocument(NewPath)!.IsWholeFile);
        Assert.Equal(["7, 8, 9"], EditSessionTests.Values(restored, Sun, "pos"));
        AssertSaved(restored.Save());
        Assert.Contains("pos = 7, 8, 9", ws.ReadModFile(FilesPath));
    }

    /// <summary>The recovery file keeps the delete of a saved new file.</summary>
    [Fact]
    public void TheRecoveryFileRestoresTheDeleteOfASavedNewFile()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(New());
        AssertSaved(session.Save());
        session.Execute(EditCommand.DeleteFile(NewPath));
        Assert.Empty(session.WriteRecovery());
        session.Dispose();

        var restored = ws.Session("act");
        Assert.True(restored.HasRecovery);
        Assert.NotNull(restored.Get(Sun));
        Assert.Empty(restored.RestoreRecovery());

        Assert.Null(restored.Get(Sun));
        var result = restored.Save();
        AssertSaved(result);
        Assert.Equal([FileOf(ws)], result.Deleted);
        Assert.False(File.Exists(FileOf(ws)));
    }

    /// <summary>The real flow in the host without a window. The test makes a new system file of the folder of the mod
    /// with its <c>[system]</c>, runs the save tool and opens a new host on the same profile. The file is editable after
    /// the profile opens again. The agent tools read and change it, and the merged files of the edit view have
    /// it.</summary>
    [Fact]
    public void ASavedNewFileIsEditableAfterTheProfileOpensAgainInTheHostWithoutAWindow()
    {
        using var ws = ListWorkspace.Create();
        using (var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { WritesRecovery = false }))
        {
            var session = host.Session;
            session.Execute(EditCommand.Group("New system",
            [
                New(),
                EditCommand.AddSection(ListWorkspace.UniversePath, "system",
                [
                    new EntryView("nickname", [new LibreLancer.Data.Ini.StringValue("Ol01")]),
                    new EntryView("file", [new LibreLancer.Data.Ini.StringValue(ContentPaths.UniverseValue(session, NewPath)!)]),
                ]),
            ]));
            Assert.Contains("Ol01_sun", (string?)Ok(host, "get_raw_ini", $$"""{ "file": {{Json(NewPath)}} }""")["text"]);
            Ok(host, "save");
        }
        Assert.Contains("[system]", ws.ReadModPatch(ListWorkspace.UniversePath));
        Assert.Contains("nickname = Ol01_sun", ws.ReadModFile(FilesPath));

        using var again = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { WritesRecovery = false });
        var reopened = again.Session;
        Assert.Contains(reopened.EditViewSections(NewPath)!,
            s => s.Name == "Object" && s.Any(e => e.Name == "nickname" && e[0].ToString() == "Ol01_sun"));
        var document = reopened.OpenDocument(NewPath)!;
        Assert.True(document.IsWholeFile);
        Assert.Null(document.ReadOnlyReason);
        Assert.Equal(NewPath, reopened.SystemPath("Ol01"), ignoreCase: true);
        Ok(again, "set_fields", """{ "ref": "obj:Ol01_sun", "fields": { "pos": [10, 20, 30] } }""");
        Assert.Equal(["10, 20, 30"], EditSessionTests.Values(reopened, Sun, "pos"));
        Ok(again, "save");
        Assert.Contains("pos = 10, 20, 30", ws.ReadModFile(FilesPath));
    }

    private static string RepoMessage(TestWorkspace ws) =>
        $"The mod 'act' is in the OpenLancer repo {ws.Root}. The editor writes no whole INI file into it, because the " +
        "content scan cannot tell such a file from a copy of a retail file (decision D70).";
}
