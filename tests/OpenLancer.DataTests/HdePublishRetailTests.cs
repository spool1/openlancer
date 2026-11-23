using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LibreLancer;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The publish history and the FLMM header of an export for HD Edition 0.7.1 (section 18 of the M11c design). The
/// tests need <c>OPENLANCER_HDE</c>, a copy of an HD Edition install, and only read it. They skip without it.
/// </summary>
public sealed class HdePublishRetailTests(ITestOutputHelper output) : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    static HdePublishRetailTests() => AppHandler.ConsoleInit(silent: true);

    private static Mod Sample => Mod.Load(GameDataPaths.SampleMod("ol-sample"));

    [Fact]
    public void TheHdExportRecordsHashesThatMatchTheFiles()
    {
        var root = GameDataPaths.RequireHde();
        var report = GameDetector.CheckRoot(root, cancel: TestContext.Current.CancellationToken);
        var sample = Sample;
        var history = Path.Combine(temp, "exports", "ol-sample");
        var before = FolderState(sample.Folder);

        var result = ModExport.Run(new ExportRequest
        {
            Mod = sample,
            Targets = [ExportTarget.HdEdition, ExportTarget.OpenLancer],
            BaseOf = t => t == ExportTarget.HdEdition ? report : null,
            OutputFolder = ModExport.VersionFolder(history, sample.Manifest),
            Forms = ExportForms.DropIn,
            Publish = PublishSettings.Load(sample.Folder).Settings,
            StringWriter = DllStringResourceWriter.Instance,
            Thn = ThornEvaluator.Instance,
        });
        var write = PublishHistory.Update(new HistoryLocation(history, "ol-sample", sample.Folder, [root]), "export",
            h => h.RecordExport(PublishHistory.EntryOf(result, DateTime.UtcNow)));

        foreach (var problem in result.Problems)
            output.WriteLine(problem.ToString());
        Assert.True(result.Succeeded);
        Assert.True(write.Written);
        var entry = Assert.Single(PublishHistory.Load(history, "ol-sample").History.Exports);
        Assert.Equal(2, entry.Packages.Count);
        Assert.All(entry.Packages, p => Assert.Equal(DataDigest.HashFile(Path.Combine(entry.Folder, p.File)), p.Sha256));
        Assert.Equal(ExportFilesState.InPlace, PublishHistory.StateOf(entry));
        Assert.Equal(before, FolderState(sample.Folder));
    }

    [Fact]
    public void TheFlmmHeaderOfTheHdPackageIsAscii()
    {
        var root = GameDataPaths.RequireHde();
        var sample = Sample;
        var files = TargetBuild.Run(GameBase.Standard(root), [sample], ModExport.DllName("ol-sample"),
            DllStringResourceWriter.Instance, ThornEvaluator.Instance, keepPlaceholders: true);
        var texts = new PublishTexts(sample.Manifest with { Name = $"Starrunner {(char)0x2013} HD" },
            PublishSettings.Load(sample.Folder).Settings with { Authors = [$"Zo{(char)0xEB}"] });

        // The probe holds the infocard with its quote, so the package is not refused.
        var plan = FlmmPackage.Plan(files, sample, texts, ExportTarget.HdEdition, probe: true, null);

        Assert.NotNull(plan.Make);
        var script = Script(plan.Make!());
        Assert.Contains("<header name=\"Starrunner - HD\">", script);
        Assert.Contains("\r\n<author>\r\nZo?\r\n</author>\r\n", script);
        Assert.Contains("\r\n<description>\r\nVersion 1.0.0 for HD Edition 0.7.1.\r\n", script);
        Assert.All(script, c => Assert.True(c < 128));
        Assert.Contains(plan.Problems, p => p.Message == PublishMessages.FlmmCharacter(((char)0xEB).ToString(), "the authors"));
    }

    private static string Script(byte[] package)
    {
        using var outer = new ZipArchive(new MemoryStream(package));
        using var flmod = new ZipArchive(outer.GetEntry("ol-sample.zip.flmod")!.Open());
        using var reader = new StreamReader(flmod.GetEntry("script.xml")!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>The files of a mod folder with their hashes, without the save folder of an editor.</summary>
    private static string[] FolderState(string folder) =>
    [
        .. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(folder, f))
            .Where(f => !f.StartsWith(Data.Editing.SaveTransaction.FolderName, StringComparison.OrdinalIgnoreCase))
            .Select(f => $"{f} {DataDigest.HashFile(Path.Combine(folder, f))}"),
    ];

    public void Dispose()
    {
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
    }
}
