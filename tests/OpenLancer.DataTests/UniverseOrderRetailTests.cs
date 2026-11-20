using System;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The merge of a new <c>[Base]</c> over the <c>universe.ini</c> of each game (<see cref="UniverseOrder"/>). The base
/// goes directly before the first <c>[system]</c>, after the bases of the game. The mod is invented and goes to a
/// temporary folder. The tests only read the game data.
/// </summary>
public sealed class UniverseOrderRetailTests(ITestOutputHelper output)
{
    private const string ModId = "ol-order";
    private const string NewBase = "ol_order_base";

    static UniverseOrderRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void ANewBaseGoesBeforeTheFirstSystemOfVanilla()
    {
        var root = GameDataPaths.RequireBase();

        Check(root);

        RetailChecks.AssertBaseIsUnchanged(root);
    }

    [Fact]
    public void ANewBaseGoesBeforeTheFirstSystemOfHdEdition() => Check(GameDataPaths.RequireHde());

    private void Check(string root)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Write(folder, "mod.json", $$"""{ "id": "{{ModId}}", "name": "Order", "version": "1.0.0", "formatVersion": 2 }""");
            Write(folder, @"patches\" + UniverseOrder.UniversePath,
                $"[Base]\r\nol.new = true\r\nnickname = {NewBase}\r\nsystem = Li01\r\nstrid_name = 196609\r\n" +
                $"file = MODS\\{ModId}\\BASES\\{NewBase}.ini\r\n");

            var files = TargetBuild.Run(GameBase.Standard(root), [Mod.Load(folder)], ModExport.DllName(ModId),
                DllStringResourceWriter.Instance, ThornEvaluator.Instance);

            Assert.DoesNotContain(files.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            var file = files.File(UniverseOrder.UniversePath)!;
            var sections = file.Merged!.Sections;
            var first = UniverseOrder.FirstSystem(sections);
            var bases = sections.Count(UniverseOrder.IsBase);
            output.WriteLine($"{bases} bases, the first [system] at section {first + 1} of {sections.Count}.");
            Assert.True(first > 0);
            Assert.Equal(first - 1, sections.FindIndex(s => UniverseOrder.IsBase(s) && IniPatcher.KeyOf(s) == NewBase));
            // The game has each base before its systems, so the new base comes after all of them.
            Assert.DoesNotContain(sections.Skip(first), UniverseOrder.IsBase);
            // The file of the drop-in zip gives the same sections.
            Assert.True(IniTextEdit.SameSections(IniDocument.Parse(file.Path, file.Bytes).Sections, sections));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void Write(string folder, string path, string text)
    {
        var full = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}
