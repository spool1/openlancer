using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.PlayCopies;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A fake source build for the tests (section 8.5 of the M11 design). It has <c>bin</c> with the program, its
/// <c>freelancer.ini</c> with <c>data path = ..\DATA</c> and files that only a build makes. It also has <c>DATA</c> with
/// a few INI files and a binary file, and the folders of the source code. All files are hand-written. Deleted on
/// dispose, after the remove of its play copy.
/// </summary>
internal sealed class SourceBuildGame : IDisposable
{
    public const string FreelancerIni = """
        [Freelancer]
        data path = ..\DATA

        [Data]
        universe = universe\universe.ini
        goods = equipment\goods.ini

        [Resources]
        DLL = InfoCards.dll
        """;

    public SourceBuildGame()
    {
        Folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Folder, "Freelancer-Build");
        Write(@"bin\freelancer.exe", "not a real program");
        Write(@"bin\freelancer.ini", FreelancerIni);
        Write(@"bin\resources.dll", "not a real dll");
        Write(@"bin\FLServer.exe", "not a real server");
        foreach (var name in new[] { "freelancer.pdb", "Common.lib", "Common.exp", "Common.ilk", "x.iobj", "x.ipdb" })
            Write(@"bin\" + name, "a file of the build");
        Write(@"DATA\universe\universe.ini", "[system]\nnickname = li01\n");
        Write(ExportGame.GoodsPath, ExportGame.Goods);
        Write(PlayGame.ModelPath, "a model of the test");
        Write(@"DATA_EXTRA\extra.txt", "extra data");
        Write(@"EXE\gundll.dll", "not a real dll");
        Write("EBUEula.dll", "not a real dll");
        Write("EULA.RTF", "the EULA of the test");
        Write("README.md", "the readme of the source code");
        Write(@"src\main.cpp", "int main() { return 0; }");
        Write(@"docs\notes.md", "notes");
        Write(@".git\HEAD", "ref: refs/heads/main");
    }

    public string Folder { get; }

    public string Root { get; }

    public GameBase Base => new(Root, GameLayout.SourceBuild);

    public string CopyFolder => PlayCopy.DefaultFolder(Root);

    /// <summary>The text of a file list of retail 1.0 with the INI files of <c>DATA</c> as they are now.</summary>
    public string Manifest => string.Concat(
        Directory.EnumerateFiles(Path.Combine(Root, "DATA"), "*.ini", SearchOption.AllDirectories)
            .Select(f => DataDigest.Line(Path.GetRelativePath(Root, f), new FileInfo(f).Length, DataDigest.HashFile(f)) + "\n"));

    /// <summary>The file list <see cref="Manifest"/> as the only known build, <c>vanilla-1.0</c>.</summary>
    public KnownBuilds Builds => FakeGame.Builds(("vanilla-1.0", Manifest));

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public void Write(string relative, string text)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.ASCII);
    }

    public GameReport Detect(string? folder = null, KnownBuilds? builds = null) =>
        GameDetector.Detect(folder ?? Root, builds ?? Builds, TestContext.Current.CancellationToken);

    /// <summary>Removes the play copy, then deletes the folder. A play copy that does not go away keeps the folder,
    /// because no test deletes a folder tree with a play copy in it.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(CopyFolder))
                PlayCopy.Remove(CopyFolder);
        }
        catch (PlayCopyException)
        {
            return;
        }
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, true);
    }
}

/// <summary>The source build layout of the game link and of the play copy (section 8.5 of the M11 design).</summary>
public sealed class SourceBuildTests : IDisposable
{
    private readonly SourceBuildGame build = new();

    public void Dispose() => build.Dispose();

    [Fact]
    public void TheDetectorFindsBinFreelancerExe()
    {
        var report = build.Detect(build.PathOf("bin"));

        Assert.Equal(GameKind.SourceBuild, report.Kind);
        Assert.Equal(GameLayout.SourceBuild, report.Layout);
        Assert.Equal(build.Root, report.Root);
        // A source build is a rebuild of the 1.1 client, and the retail INI files of 1.0 are also those of 1.1.
        Assert.Equal("source-vanilla-1.1", report.Build);
        Assert.Equal(ModTargets.Vanilla, report.Target);
        Assert.Equal(build.PathOf(@"bin\freelancer.exe"), report.ExePath);
        Assert.Equal("Source build (Freelancer-Build)", report.Name);
        Assert.Equal("Source build", report.ShortName);
        Assert.Equal(DataState.NotChanged, report.Data);
        Assert.Equal("INI files: same as retail", report.DataText);
        Assert.Equal(new GameFinding(GameSeverity.Info, GameTexts.SourceBuildData("Freelancer 1.1", changed: false)),
            Assert.Single(report.Findings));
        Assert.Equal("The DATA folder has the retail INI files without changes. OpenLancer plays this source build as " +
                     "Freelancer 1.1.", GameTexts.SourceBuildData("Freelancer 1.1", changed: false));
        // A folder two levels inside the build finds it too.
        Assert.Equal(build.Root, build.Detect(build.PathOf(@"DATA\EQUIPMENT")).Root);
        Assert.Equal(GameKind.SourceBuild,
            GameDetector.CheckRoot(build.Root, build.Builds, TestContext.Current.CancellationToken).Kind);
        Assert.Equal(build.Root, GameRoot.Containing(build.PathOf(@"src\more")));
    }

    [Fact]
    public void ChangedDataIsReportedAndOtherDataGivesNoTarget()
    {
        var builds = build.Builds;
        build.Write(ExportGame.GoodsPath, ExportGame.Goods + "\n[Good]\nnickname = g3\n");

        var changed = build.Detect(builds: builds);
        var other = build.Detect(builds: FakeGame.Builds(("hde-0.7.1", "data/universe/universe.ini\t1\t" + new string('0', 64))));

        Assert.Equal(DataState.Changed, changed.Data);
        Assert.Equal(ModTargets.Vanilla, changed.Target);
        Assert.Equal("source-vanilla-1.1", changed.Build);
        Assert.Equal("INI files: 1 changed", changed.DataText);
        Assert.Equal(
        [
            new GameFinding(GameSeverity.Info, "OpenLancer plays this source build as Freelancer 1.1."),
            new GameFinding(GameSeverity.Warning, GameTexts.ChangedData(1, "retail")),
        ], changed.Findings);
        Assert.Equal(GameTexts.SourceBuildData("Freelancer 1.1", changed: true), changed.Findings[0].Text);
        Assert.Equal("source-hde-0.7.1", other.Build);
        Assert.Null(other.Target);
        Assert.Contains(other.Findings, f => f.Text == GameTexts.SourceBuildUnknownData);
    }

    [Fact]
    public void RetailDataGivesBuild11WithAndWithoutTheFileListOf11()
    {
        // The file list of 1.1 has the code files only, and takes its INI files from the file list of 1.0. So both
        // lists give the same changes, and the source build is 1.1. Only the reference changes.
        var oneOne = $"exe/common.dll\t1\t{new string('1', 64)}\n";
        var tenOnly = build.Builds;
        var both = FakeGame.Builds(("vanilla-1.0", build.Manifest), ("vanilla-1.1", oneOne));

        var withTen = build.Detect(builds: tenOnly);
        var withBoth = build.Detect(builds: both);

        Assert.Equal("source-vanilla-1.1", withTen.Build);
        Assert.Equal("vanilla-1.0", withTen.Reference);
        Assert.Equal("source-vanilla-1.1", withBoth.Build);
        Assert.Equal("vanilla-1.1", withBoth.Reference);
        Assert.Equal(withTen.ComparedFiles, withBoth.ComparedFiles);
        Assert.Equal(withTen.Findings, withBoth.Findings);
        Assert.Equal("INI files: same as retail", withBoth.DataText);
    }

    [Fact]
    public void TheLayoutMapsExeToBin()
    {
        var mod = Mod.Load(WriteMod());
        using var copy = PlayCopy.Make(build.Base, build.CopyFolder, PlayCopyMode.Links, "source-vanilla-1.1");

        var files = TargetBuild.Run(build.Base, [mod], ModOverlay.StringsDllName, TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator);
        copy.Install(files, null);

        Assert.Equal(@"bin\freelancer.ini", build.Base.ToDisk(@"EXE\freelancer.ini"));
        Assert.Equal(@"DATA\x.ini", build.Base.ToDisk(@"DATA\x.ini"));
        Assert.Equal(File.ReadAllBytes(build.PathOf(@"bin\freelancer.ini")),
            build.Base.OpenFileSystem().ReadAllBytes(@"EXE\freelancer.ini"));
        Assert.Equal("[Tool]\r\nx = 1\r\n", File.ReadAllText(Path.Combine(build.CopyFolder, @"bin\modtool.ini")));
        Assert.False(File.Exists(Path.Combine(build.CopyFolder, @"EXE\modtool.ini")));
        Assert.Contains("price = 5600", File.ReadAllText(Path.Combine(build.CopyFolder, ExportGame.GoodsPath)));
        Assert.Equal(Path.Combine(build.CopyFolder, "bin"), copy.ProgramFolder);
        Assert.Equal(Path.Combine(build.CopyFolder, @"bin\freelancer.exe"), copy.ProgramPath);
        Assert.Equal(Path.Combine(build.CopyFolder, @"bin\FLServer.exe"), copy.ServerPath);
        Assert.False(copy.SavesInCopy);
    }

    [Fact]
    public void AnotherDataPathIsRefused()
    {
        build.Write(@"bin\freelancer.ini", SourceBuildGame.FreelancerIni.Replace(@"..\DATA", @"..\GAMEDATA"));

        var report = build.Detect();

        Assert.False(report.IsGame);
        Assert.Equal(GameTexts.SourceBuildDataPath(@"..\GAMEDATA"), report.Error);
        // Another letter case and the other separator are the same path.
        build.Write(@"bin\freelancer.ini", SourceBuildGame.FreelancerIni.Replace(@"..\DATA", "../data/"));
        Assert.True(build.Detect().IsGame);
    }

    [Fact]
    public void BuildOnlyFilesAreNotCopied()
    {
        using var copy = PlayCopy.Make(build.Base, build.CopyFolder, PlayCopyMode.Links, null);

        string[] copied = [@"bin\freelancer.exe", @"bin\freelancer.ini", @"bin\resources.dll", @"bin\FLServer.exe",
            @"DATA_EXTRA\extra.txt", @"EXE\gundll.dll", "EBUEula.dll", "EULA.RTF", ExportGame.GoodsPath,
            @"DATA\universe\universe.ini"];
        foreach (var path in copied)
            Assert.Equal(PlayCopyKind.Copy, copy.Record.Entries[path].Kind);
        Assert.Equal(PlayCopyKind.Link, copy.Record.Entries[PlayGame.ModelPath].Kind);
        Assert.Equal(copied.Length + 1, copy.Record.Entries.Count);
        foreach (var path in new[] { @"bin\freelancer.pdb", @"bin\Common.lib", @"bin\Common.exp", @"bin\Common.ilk",
                     @"bin\x.iobj", @"bin\x.ipdb", "README.md", "src", "docs", ".git" })
            Assert.False(Path.Exists(Path.Combine(build.CopyFolder, path)), path);
    }

    [Fact]
    public void ASourceBuildIsNoProfileBase()
    {
        var report = build.Detect();

        Assert.True(report.IsGame);
        Assert.False(report.CanBeBase);
        Assert.False(ExportTarget.Vanilla.AcceptsBase(report));
        Assert.Null(PlayBuild.Refusal(report, []));
    }

    [Fact]
    public void ThePlayCopyIsNotAGameToLink()
    {
        using var game = new PlayGame();
        using (game.Make())
        {
        }

        var copy = GameDetector.Detect(game.CopyFolder, cancel: TestContext.Current.CancellationToken);
        var inside = GameDetector.Detect(game.CopyPath(@"DATA\SHIPS"), cancel: TestContext.Current.CancellationToken);
        var root = GameDetector.CheckRoot(game.CopyFolder, cancel: TestContext.Current.CancellationToken);

        var text = GameTexts.PlayCopy(GameFolders.Normal(game.Root));
        Assert.Equal(text, copy.Error);
        Assert.Equal(text, inside.Error);
        Assert.Equal(text, root.Error);
        Assert.False(copy.IsGame);
        // A play copy of a source build is refused too.
        using (PlayCopy.Make(build.Base, build.CopyFolder, PlayCopyMode.Links, null))
        {
        }
        Assert.Equal(GameTexts.PlayCopy(build.Root),
            GameDetector.Detect(build.CopyFolder, cancel: TestContext.Current.CancellationToken).Error);
    }

    /// <summary>A mod with a goods patch and a whole file in the EXE folder, which goes to bin in a source
    /// build.</summary>
    private string WriteMod()
    {
        var folder = Path.Combine(build.Folder, "mods", "tool");
        Directory.CreateDirectory(Path.Combine(folder, @"patches\DATA\EQUIPMENT"));
        Directory.CreateDirectory(Path.Combine(folder, @"files\EXE"));
        File.WriteAllText(Path.Combine(folder, ModManifest.FileName), """{ "id": "tool", "formatVersion": 2 }""");
        File.WriteAllText(Path.Combine(folder, @"patches\DATA\EQUIPMENT\goods.ini"), "[Good]\nnickname = lf_hull\nprice = 5600\n");
        File.WriteAllText(Path.Combine(folder, @"files\EXE\modtool.ini"), "[Tool]\r\nx = 1\r\n");
        return folder;
    }
}
