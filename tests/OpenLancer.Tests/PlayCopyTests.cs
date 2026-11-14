using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.PlayCopies;
using Xunit;
using static OpenLancer.Tests.PlayGame;

namespace OpenLancer.Tests;

/// <summary>
/// The play copy (section 8 of the M11 design): its kinds of files, the install, the uninstall and the checks of the
/// links. Also the own edit of HD Edition, the lock and the remove. Each test proves that the files of the game keep their
/// bytes, attributes and times. The fake game is in a temporary folder on the volume of the test.
/// </summary>
public sealed class PlayCopyTests : IDisposable
{
    private const string GoodsPatch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    private readonly PlayGame game = new();

    public void Dispose() => game.Dispose();

    private Mod GoodsMod() => game.Export.GoodsMod(GoodsPatch);

    /// <summary>A mod with the goods patch, a model in place of the model of the game, and a new data file.</summary>
    private Mod BigMod() => game.Export.Mod("big", new()
    {
        [@"patches\" + ExportGame.GoodsPath] = GoodsPatch,
        [@"files\" + ModelPath] = "the model of the mod",
        [@"files\DATA\MODS\big\notes.txt"] = "a new file of the mod",
    });

    private static PlayCopyEntry Entry(PlayCopy copy, string path) => copy.Record.Entries[path];

    /// <summary>The editor asks for a full copy when the probe fails in a folder that the user selected, also on the
    /// drive of the game. So the question names the real cause, the hard links, and not another drive.</summary>
    [Fact]
    public void TheFullCopyQuestionNamesTheHardLinksAsTheCause()
    {
        var question = PlayCopyTexts.FullCopyQuestion("2.5 GB");

        Assert.Equal("OpenLancer cannot make hard links in this folder. A play copy there needs a full copy of the game: " +
                     "2.5 GB. Make a full copy?", question);
        Assert.DoesNotContain("drive", question);
    }

    [Fact]
    public void ACopyLinksBinaryDataAndCopiesTextDataAndExe()
    {
        using var copy = game.Make();

        Assert.Equal(PlayCopyState.Complete, copy.Record.State);
        Assert.Equal(PlayCopyMode.Links, copy.Record.Mode);
        foreach (var path in new[] { ModelPath, SoundPath, ContentPath })
        {
            Assert.Equal(PlayCopyKind.Link, Entry(copy, path).Kind);
            Assert.True(SameFile(game.Game.PathOf(path), game.CopyPath(path)), path);
        }
        foreach (var path in new[] { ExportGame.GoodsPath, NotesPath, LayoutPath, @"EXE\freelancer.ini", ProgramPath,
                     @"SAVE\PerfOptions.ini" })
        {
            Assert.Equal(PlayCopyKind.Copy, Entry(copy, path).Kind);
            Assert.False(SameFile(game.Game.PathOf(path), game.CopyPath(path)), path);
            Assert.Equal(File.ReadAllBytes(game.Game.PathOf(path)), File.ReadAllBytes(game.CopyPath(path)));
        }
        // A real copy is a new file, so it is not read-only like the goods file of the game.
        Assert.False(new FileInfo(game.CopyPath(ExportGame.GoodsPath)).IsReadOnly);
        Assert.Contains("hard links to the files of your game", File.ReadAllText(game.CopyPath(PlayCopy.ReadMeName)));
        Assert.Equal(GameFolders.Normal(game.Root), copy.Record.Source);
        Assert.Equal(Path.Combine(game.CopyFolder, "EXE"), copy.ProgramFolder);
        Assert.Equal(game.CopyPath(ProgramPath), copy.ProgramPath);
        Assert.Equal(game.CopyPath(@"EXE\FLServer.exe"), copy.ServerPath);
    }

    [Fact]
    public void TheUninstallerIsNotInTheCopy()
    {
        using var copy = game.Make();

        foreach (var name in new[] { "unins000.exe", "unins000.dat", "UNINSTAL.EXE" })
        {
            Assert.True(File.Exists(game.Game.PathOf(name)));
            Assert.False(File.Exists(game.CopyPath(name)), name);
        }
    }

    [Fact]
    public void TheEulaFilesAreInTheRoot()
    {
        using var copy = game.Make();

        foreach (var name in new[] { "EBUEula.dll", "EULA.RTF" })
        {
            Assert.Equal(PlayCopyKind.Copy, Entry(copy, name).Kind);
            Assert.Equal(File.ReadAllBytes(game.Game.PathOf(name)), File.ReadAllBytes(game.CopyPath(name)));
        }
    }

    [Fact]
    public void AnInstallReplacesALinkAndTheSourceKeepsItsBytes()
    {
        var mod = BigMod();
        using var copy = game.Make();
        var before = Stamps(game.Root);

        var result = copy.Install(game.Build(mod), "test.json");

        Assert.Empty(result.Problems);
        Assert.Equal("the model of the mod", File.ReadAllText(game.CopyPath(ModelPath)));
        Assert.False(SameFile(game.Game.PathOf(ModelPath), game.CopyPath(ModelPath)));
        Assert.Equal("a new file of the mod", File.ReadAllText(game.CopyPath(@"DATA\MODS\big\notes.txt")));
        Assert.Contains("price = 5600", File.ReadAllText(game.CopyPath(ExportGame.GoodsPath)));
        Assert.Equal(before, Stamps(game.Root));
        Assert.True(new FileInfo(game.Game.PathOf(ModelPath)).IsReadOnly);
        Assert.Equal(3, copy.Record.Installed.Count);
        Assert.Equal("test.json", copy.Record.Installed[ModelPath].Profile);
    }

    [Fact]
    public void TheSourceKeepsItsAttributesAndTimes()
    {
        var before = Stamps(game.Root);
        var mod = BigMod();

        using (var copy = game.Make())
        {
            copy.Install(game.Build(mod), null);
            copy.Check();
            copy.Uninstall();
        }
        PlayCopy.Remove(game.CopyFolder);

        Assert.Equal(before, Stamps(game.Root));
        Assert.False(Directory.Exists(game.CopyFolder));
    }

    [Fact]
    public void UninstallGivesEachFileItsMakeTimeKind()
    {
        var mod = BigMod();
        using var copy = game.Make();
        copy.Install(game.Build(mod), null);

        var result = copy.Uninstall();

        Assert.Equal(3, result.Restored);
        Assert.Empty(copy.Record.Installed);
        Assert.True(SameFile(game.Game.PathOf(ModelPath), game.CopyPath(ModelPath)));
        Assert.Equal(PlayCopyKind.Link, Entry(copy, ModelPath).Kind);
        Assert.Equal(File.ReadAllBytes(game.Game.PathOf(ExportGame.GoodsPath)),
            File.ReadAllBytes(game.CopyPath(ExportGame.GoodsPath)));
        Assert.False(SameFile(game.Game.PathOf(ExportGame.GoodsPath), game.CopyPath(ExportGame.GoodsPath)));
        // A new file of the mod goes, and so do the folders that only it needed.
        Assert.False(Directory.Exists(game.CopyPath(@"DATA\MODS")));
        Assert.Empty(copy.Check().Problems);
    }

    [Fact]
    public void ASecondInstallRestoresTheFilesOfTheFirstOnly()
    {
        using var copy = game.Make();
        copy.Install(game.Build(BigMod()), null);
        var other = game.Export.GoodsMod("[Good]\nnickname = lf_hull\nprice = 4400\n", "other");

        var result = copy.Install(game.Build(other), null);

        Assert.Equal(1, result.Written);
        Assert.Equal(2, result.Restored);
        Assert.Contains("price = 4400", File.ReadAllText(game.CopyPath(ExportGame.GoodsPath)));
        Assert.True(SameFile(game.Game.PathOf(ModelPath), game.CopyPath(ModelPath)));
        Assert.False(File.Exists(game.CopyPath(@"DATA\MODS\big\notes.txt")));
        Assert.Equal([ExportGame.GoodsPath], copy.Record.Installed.Keys);
    }

    [Fact]
    public void AFileChangedOutsideIsKeptAndReported()
    {
        using var copy = game.Make();
        var files = game.Build(GoodsMod());
        copy.Install(files, null);
        // Another program saves the installed file of the play copy.
        HardLinks.Instance.WriteNew(game.CopyPath(ExportGame.GoodsPath), Encoding.ASCII.GetBytes("[Good]\nchanged\n"));

        var result = copy.Install(files, null);

        var kept = Path.Combine(game.CopyFolder, PlayCopy.KeepFolder, "changed", ExportGame.GoodsPath);
        var warning = Assert.Single(result.Problems);
        Assert.Equal(PlayCopyTexts.InstalledFileChanged(ExportGame.GoodsPath, kept), warning.Message);
        Assert.Equal("[Good]\nchanged\n", File.ReadAllText(kept));
        Assert.Contains("price = 5600", File.ReadAllText(game.CopyPath(ExportGame.GoodsPath)));
        Assert.Empty(copy.Check().Problems);
    }

    [Fact]
    public void AReplacedLinkIsMadeAgain()
    {
        using var copy = game.Make();
        // A program saves a linked file of the play copy with a new file, as a safe save does.
        HardLinks.Instance.WriteNew(game.CopyPath(SoundPath), Encoding.ASCII.GetBytes("a sound of another program"));
        Assert.Equal(PlayCopyTexts.LinkReplaced(SoundPath), Assert.Single(copy.Check().Problems).Message);

        var result = copy.Install(game.Build(GoodsMod()), null);

        Assert.Equal(PlayCopyTexts.LinkReplaced(SoundPath), Assert.Single(result.Problems).Message);
        Assert.True(SameFile(game.Game.PathOf(SoundPath), game.CopyPath(SoundPath)));
        Assert.Equal("a sound of the test", File.ReadAllText(game.Game.PathOf(SoundPath)));
        Assert.Empty(copy.Check().Problems);
    }

    [Fact]
    public void AReplacedSourceGetsANewLink()
    {
        using var copy = game.Make();
        // An update of the game replaces its file.
        HardLinks.Instance.WriteNew(game.Game.PathOf(SoundPath), Encoding.ASCII.GetBytes("a new version of the sound"));

        var result = copy.Install(game.Build(GoodsMod()), null);

        Assert.Equal(PlayCopyTexts.SourceReplaced(SoundPath), Assert.Single(result.Problems).Message);
        Assert.True(SameFile(game.Game.PathOf(SoundPath), game.CopyPath(SoundPath)));
        Assert.Equal("a new version of the sound", File.ReadAllText(game.CopyPath(SoundPath)));
        Assert.Empty(copy.Check().Problems);
    }

    [Fact]
    public void AChangedLinkedFileIsReported()
    {
        using var copy = game.Make();
        // A program writes into the linked file in place, so the file of the game changes too.
        using (var stream = new FileStream(game.Game.PathOf(SoundPath), FileMode.Append, FileAccess.Write))
            stream.Write(" and more"u8);

        var check = copy.Check();
        var result = copy.Install(game.Build(GoodsMod()), null);

        var expected = PlayCopyTexts.LinkedFileChanged(game.CopyPath(SoundPath), game.Game.PathOf(SoundPath));
        Assert.Equal(expected, Assert.Single(check.Problems).Message);
        Assert.Equal(expected, Assert.Single(result.Problems).Message);
        // The install records the new stamps, so the next check finds no difference.
        Assert.Empty(copy.Check().Problems);
    }

    [Fact]
    public void RemoveDeletesOnlyTheCopy()
    {
        var before = Stamps(game.Root);
        using (var copy = game.Make())
            copy.Install(game.Build(BigMod()), null);

        PlayCopy.Remove(game.CopyFolder);

        Assert.False(Directory.Exists(game.CopyFolder));
        Assert.Equal(before, Stamps(game.Root));
    }

    [Fact]
    public void RemoveNeverFollowsAJunction()
    {
        using (game.Make())
        {
        }
        // A program puts a junction to a folder of the game into the play copy.
        var junction = game.CopyPath(@"DATA\linked-ships");
        var target = game.Game.PathOf(@"DATA\SHIPS");
        MakeJunction(junction, target);
        Assert.True(File.Exists(Path.Combine(junction, "patriot.cmp")));
        var before = Stamps(game.Root);

        PlayCopy.Remove(game.CopyFolder);

        Assert.False(Directory.Exists(game.CopyFolder));
        Assert.Equal(before, Stamps(game.Root));
        Assert.True(File.Exists(game.Game.PathOf(ModelPath)));
    }

    [Fact]
    public void AnInstallNeverWritesThroughAJunction()
    {
        using (game.Make())
        {
        }
        // A program puts a junction to a folder of the game where the install writes a new file of the mod.
        var target = game.Game.PathOf(@"DATA\INTERFACE");
        MakeJunction(game.CopyPath(@"DATA\MODS"), target);
        var before = Stamps(game.Root);

        using var copy = PlayCopy.Open(game.CopyFolder);
        var error = Assert.Throws<PlayCopyException>(() => copy.Install(game.Build(BigMod()), null));

        Assert.Equal(PlayCopyTexts.IsALink(game.CopyPath(@"DATA\MODS")), error.Message);
        Assert.False(Directory.Exists(Path.Combine(target, "big")));
        Assert.Equal(before, Stamps(game.Root));
        copy.Remove();
        Assert.Equal(before, Stamps(game.Root));
    }

    [Fact]
    public void ACopyOnAnotherVolumeNeedsAFullCopy()
    {
        var other = new NoLinks();
        var folder = game.CopyFolder;

        var reason = PlayCopy.Probe(game.Base, folder, other);

        Assert.Contains("The test refuses the link", reason);
        Assert.False(Directory.Exists(folder));
        Assert.Throws<IOException>(() => game.Make(links: other).Dispose());
        var fullFolder = Path.Combine(game.Game.Folder, "full copy");
        using var copy = game.Make(PlayCopyMode.Full, other, fullFolder);
        Assert.Equal(PlayCopyMode.Full, copy.Record.Mode);
        Assert.All(copy.Record.Entries.Values, e => Assert.Equal(PlayCopyKind.Copy, e.Kind));
        Assert.False(SameFile(game.Game.PathOf(ModelPath), Path.Combine(fullFolder, ModelPath)));
        Assert.Contains("It is a full copy", File.ReadAllText(Path.Combine(fullFolder, PlayCopy.ReadMeName)));
        copy.Dispose();
        Assert.Contains("full copy",
            Assert.Throws<PlayCopyException>(() => game.Make(PlayCopyMode.Links, folder: fullFolder)).Message);
    }

    [Fact]
    public void TheProbePassesOnTheVolumeOfTheGame()
    {
        Assert.Null(PlayCopy.Probe(game.Base, game.CopyFolder));

        // The probe leaves the empty folder for the make, and no probe file.
        Assert.Empty(Directory.EnumerateFileSystemEntries(game.CopyFolder));
        Assert.True(new FileInfo(game.Game.PathOf(ModelPath)).IsReadOnly);
        Assert.Equal(1u, HardLinks.Instance.Stamp(game.Game.PathOf(ModelPath)).Links);
    }

    [Fact]
    public void AStoppedMakeCompletesAtTheNextUse()
    {
        Assert.Throws<IOException>(() => game.Make(links: new NoLinks(after: 1)).Dispose());
        var stopped = PlayCopyRecord.Read(game.CopyFolder)!;
        Assert.Equal(PlayCopyState.Incomplete, stopped.State);

        using var copy = game.Make();

        Assert.Equal(PlayCopyState.Complete, copy.Record.State);
        Assert.True(SameFile(game.Game.PathOf(SoundPath), game.CopyPath(SoundPath)));
        Assert.True(SameFile(game.Game.PathOf(ModelPath), game.CopyPath(ModelPath)));
        Assert.Empty(copy.Check().Problems);
    }

    [Fact]
    public void HdSavesGoIntoTheCopy()
    {
        using var hd = new PlayGame(hd: true);

        using var copy = hd.Make();

        var text = File.ReadAllText(hd.CopyPath(FlplusplusPath));
        Assert.Contains("save_in_directory = true", text);
        Assert.Contains("; true puts the saves next to EXE", text);
        Assert.Contains("save_folder_name = Freelancer", text);
        Assert.Contains("save_in_directory = false", File.ReadAllText(hd.Game.PathOf(FlplusplusPath)));
        Assert.Equal(OwnEdits.SaveInDirectoryLine, copy.Record.Own[FlplusplusPath]);
        Assert.True(copy.SavesInCopy);
        Assert.True(Directory.Exists(hd.CopyPath("SAVE")));
        // The vanilla game has no flplusplus, so its saves stay in Documents.
        using var vanilla = game.Make();
        Assert.False(vanilla.SavesInCopy);
    }

    [Fact]
    public void TheOwnEditStaysAfterAnInstall()
    {
        using var hd = new PlayGame(hd: true);
        var mod = hd.Export.Mod("own", new() { [@"files\" + FlplusplusPath] = "[flplusplus]\nsave_in_directory = false\n" });
        using var copy = hd.Make();

        copy.Install(hd.Build(mod), null);
        var installed = File.ReadAllText(hd.CopyPath(FlplusplusPath));
        copy.Uninstall();

        Assert.Contains("save_in_directory = true", installed);
        Assert.Contains("save_in_directory = true", File.ReadAllText(hd.CopyPath(FlplusplusPath)));
        Assert.Contains("; Settings file for the test", File.ReadAllText(hd.CopyPath(FlplusplusPath)));
        Assert.Empty(copy.Check().Problems);
    }

    [Fact]
    public void ASecondWriterIsLockedOut()
    {
        using var copy = game.Make();

        var error = Assert.Throws<PlayCopyException>(() => PlayCopy.Open(game.CopyFolder));

        Assert.Equal(PlayCopyTexts.Locked(game.CopyFolder), error.Message);
        Assert.Throws<PlayCopyException>(() => PlayCopy.Remove(game.CopyFolder));
        copy.Dispose();
        using var again = PlayCopy.Open(game.CopyFolder);
        Assert.Equal(PlayCopyState.Complete, again.Record.State);
    }

    [Fact]
    public void ThePlayCopyOverlapsNoGameOrWorkspace()
    {
        var inside = game.Game.PathOf("play");
        var used = Path.Combine(game.Game.Folder, "used");
        Directory.CreateDirectory(used);
        File.WriteAllText(Path.Combine(used, "notes.txt"), "a file of the user");

        Assert.Equal(PlayCopyTexts.OverlapsGame(inside, game.Root), PlayCopy.FolderProblem(inside, game.Root));
        Assert.Equal(PlayCopyTexts.OverlapsGame(game.Game.Folder, game.Root),
            PlayCopy.FolderProblem(game.Game.Folder, game.Root));
        Assert.Equal(PlayCopyTexts.FolderInUse(used), PlayCopy.FolderProblem(used, game.Root));
        Assert.Null(PlayCopy.FolderProblem(game.CopyFolder, game.Root));
        Assert.Throws<PlayCopyException>(() => game.Make(folder: inside));
        Assert.False(Directory.Exists(inside));
        // A folder in another game folder, for example in another play copy, is refused too.
        using (game.Make())
        {
        }
        var nested = game.CopyPath(@"DATA\play");
        Assert.StartsWith("The play copy folder", PlayCopy.FolderProblem(nested, game.Root));
        Assert.Contains("OpenLancer play copy",
            Assert.Throws<PlayCopyException>(() => PlayCopy.Open(game.Game.Folder)).Message);
    }

    [Fact]
    public void TheInstallRefusesTheFilesOfAnotherGame()
    {
        using var other = new PlayGame();
        using var copy = game.Make();

        var error = Assert.Throws<PlayCopyException>(() => copy.Install(other.Build(GoodsMod()), null));

        Assert.Contains("OpenLancer did not install them", error.Message);
        Assert.Empty(copy.Record.Installed);
    }

    /// <summary>Makes a junction with the command of Windows, which needs no extra right.</summary>
    private static void MakeJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)));
        Assert.True(process.ExitCode == 0, "mklink /J failed.");
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>A volume without links: each link after the first ones fails, as on another drive.</summary>
    private sealed class NoLinks(int after = 0) : HardLinks
    {
        private int links;

        protected override void CreateLink(string source, string newName)
        {
            if (++links > after)
                throw new IOException("The test refuses the link, as for a file on another drive.");
            base.CreateLink(source, newName);
        }
    }
}
