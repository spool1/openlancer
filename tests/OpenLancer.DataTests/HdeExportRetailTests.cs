using System;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The export of the sample mod on HD Edition 0.7.1 (section 7.19 of the M11 design). The tests need
/// <c>OPENLANCER_HDE</c>, a copy of an HD Edition install, and only read it. They skip without it.
/// </summary>
public sealed class HdeExportRetailTests(ITestOutputHelper output)
{
    static HdeExportRetailTests() => AppHandler.ConsoleInit(silent: true);

    private static TargetFiles Build(string root, bool keepPlaceholders = false) =>
        TargetBuild.Run(GameBase.Standard(root), [Mod.Load(GameDataPaths.SampleMod("ol-sample"))],
            ModExport.DllName("ol-sample"), DllStringResourceWriter.Instance, ThornEvaluator.Instance, keepPlaceholders);

    [Fact]
    public void TheSampleExportsForHdWithDllIndex8()
    {
        var files = Build(GameDataPaths.RequireHde());

        foreach (var problem in files.Diagnostics)
            output.WriteLine(problem.ToString());
        Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(8, files.StringIds.DllIndex);
        Assert.Empty(FormatChecks(files));
        Assert.Equal(10, files.Files.Count);
    }

    private static Diagnostic[] FormatChecks(TargetFiles files) =>
        [.. Data.Validation.FormatChecks.Run(files).Where(p => p.Severity == DiagnosticSeverity.Error)];

    [Fact]
    public void AnHdTextFileKeepsItsCommentsAfterTheMerge()
    {
        var root = GameDataPaths.RequireHde();
        var files = Build(root);

        foreach (var file in files.Files.Where(f => f is { Kind: TargetFileKind.Ini, HasBase: true }))
        {
            var before = IniDocument.TextEncoding.GetString(File.ReadAllBytes(Path.Combine(root,
                Data.IO.PathRules.WithDiskCase(root, file.Path))));
            var after = IniDocument.TextEncoding.GetString(file.Bytes);
            var comments = before.Split("\r\n").Where(l => l.TrimStart().StartsWith(';')).ToList();
            Assert.All(comments, c => Assert.Contains(c, after));
            Assert.DoesNotContain(files.Diagnostics, d => d.Message.Contains("without the comments of the game file"));
            output.WriteLine($"{file.Path}: {comments.Count} comment lines kept.");
        }
    }

    [Fact]
    public void EachHdTextIniFileSplicesWithoutFallback()
    {
        var root = GameDataPaths.RequireHde();
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            var document = IniDocument.Parse(path, bytes);
            if (document.IsBinary)
                continue;
            count++;
            var merged = document.Clone();
            var result = IniTextEdit.Write(bytes, document, merged, IniSectionDiff.Compare(document.Sections, merged.Sections));
            Assert.True(result.Fallback == null, $"{path}: {result.Fallback}");
            Assert.True(bytes.AsSpan().SequenceEqual(result.Bytes), path);
            // The raw index also matches each file, so a change of any section can splice.
            Assert.NotNull(RawIni.Scan(bytes, out _));
        }
        output.WriteLine($"{count} text INI files.");
        Assert.True(count > 1200);
    }

    [Fact]
    public void TheFlmmAnchorsOfTheSampleAreUniqueInTheHdFiles()
    {
        var files = Build(GameDataPaths.RequireHde(), keepPlaceholders: true);
        var mod = Mod.Load(GameDataPaths.SampleMod("ol-sample"));

        var script = FlmmScript.Build(files, mod, FlmmHeader.Of(PublishTexts.Of(mod.Manifest), ExportTarget.HdEdition),
            probe: true);

        foreach (var problem in script.Problems)
            output.WriteLine(problem.ToString());
        Assert.DoesNotContain(script.Problems, p => p.Message.Contains("anchor") || p.Message.Contains("not unique"));
        Assert.Contains("method=\"sectionreplace\"", script.Xml);
        Assert.Contains("= 0 ;GENERATESTRRES(\"Starrunner\")\r\n", script.Xml);
    }

    [Fact]
    public void TheHdFreelancerIniKeepsItsSevenResourceLines()
    {
        var root = GameDataPaths.RequireHde();
        var files = Build(root);

        var merged = IniDocument.Parse("freelancer.ini", files.File(ModOverlay.FreelancerIniPath)!.Bytes);
        var original = IniDocument.Parse("freelancer.ini", File.ReadAllBytes(Path.Combine(root, "EXE", "freelancer.ini")));
        string[] Dlls(IniDocument d) => [.. d.Sections.Where(s => s.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s).Where(e => e.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase)).Select(e => e[0].ToString())];

        Assert.Equal(7, Dlls(original).Length);
        Assert.Equal([.. Dlls(original), "ol_ol-sample.dll"], Dlls(merged));
    }
}

/// <summary>The cutscenes of HD Edition 0.7.1: text and bytecode (section 7.19 of the M11 design).</summary>
public sealed class HdeThnRetailTests(ITestOutputHelper output)
{
    static HdeThnRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EachHdCutsceneReads()
    {
        var root = GameDataPaths.RequireHde();
        var files = Directory.EnumerateFiles(Path.Combine(root, "DATA"), "*.thn", SearchOption.AllDirectories).ToList();
        var text = 0;

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 0 && bytes[0] != 0x1B)
                text++;
            var tree = ThornEvaluator.Instance.Evaluate(bytes, file);
            Assert.Null(ThnWriter.RoundTripProblem(tree, ThornEvaluator.Instance, file));
            Assert.Empty(ThnRetailNames.Find(tree));
        }
        output.WriteLine($"{files.Count} cutscenes, {text} of them text.");
        Assert.True(files.Count > 1500);
        Assert.True(text > 0);
    }

    [Fact]
    public void APatchOfTheHdCityscapeWritesTextWithRetailNamesOnly()
    {
        var files = TargetBuild.Run(GameBase.Standard(GameDataPaths.RequireHde()),
            [Mod.Load(GameDataPaths.SampleMod("ol-sample"))], ModExport.DllName("ol-sample"),
            DllStringResourceWriter.Instance, ThornEvaluator.Instance);

        var cityscape = files.File(@"DATA\Scripts\Bases\li_01_cityscape_hardpoint_01.thn")!;
        var text = Encoding.ASCII.GetString(cityscape.Bytes);
        Assert.Contains("pos = { 59.658787, 86.31464, 97.57275 }", text);
        Assert.Empty(ThnRetailNames.Find(ThornEvaluator.Instance.Evaluate(cityscape.Bytes, "cityscape")));
    }
}
