using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The place of a new <c>[Base]</c> in <c>universe.ini</c> (<see cref="UniverseOrder"/>) is directly before the first
/// <c>[system]</c>. The tests check W, the merge, the patch that a save writes and the export. The FLMM package refuses
/// a new <c>[Base]</c>. The data is invented.
/// </summary>
public sealed class UniverseOrderTests
{
    private const string Universe = ListWorkspace.UniversePath;

    /// <summary>A <c>universe.ini</c> in the retail order: <c>[Time]</c>, the bases, then the systems.</summary>
    private const string RetailOrder = """
        [Time]
        seconds_per_day = 1800
        [Base]
        nickname = Li01_01_Base
        system = Li01
        [Base]
        nickname = Li01_02_Base
        system = Li01
        [system]
        nickname = Li01
        file = systems\Li01\li01.ini
        [system]
        nickname = Li02
        file = systems\Li02\li02.ini
        """;

    private static readonly string[] RetailIds =
        ["Time", "Base Li01_01_Base", "Base Li01_02_Base", "system Li01", "system Li02"];

    private static EditCommand NewBase(string nickname) => EditCommand.AddSection(Universe, "Base",
        [ListWorkspace.Line("nickname", nickname), ListWorkspace.Line("system", "Li01"), BaseFile(nickname)]);

    private static EntryView BaseFile(string nickname) => ListWorkspace.Line("file", $@"MODS\act\BASES\{nickname}.ini");

    private static EditCommand NewSystem(string nickname) => EditCommand.AddSection(Universe, "system",
        [ListWorkspace.Line("nickname", nickname), ListWorkspace.Line("file", $@"systems\{nickname}\{nickname}.ini")]);

    /// <summary>A workspace whose base data has <paramref name="universe"/> as <c>universe.ini</c>.</summary>
    private static TestWorkspace Workspace(string universe = RetailOrder, Dictionary<string, string>? lowerFiles = null)
    {
        var ws = ListWorkspace.Create(lowerFiles);
        ws.WriteBase(Universe, universe);
        return ws;
    }

    /// <summary>The identities of sections as "type key", for example "Base Li01_01_Base".</summary>
    private static string[] Ids(IEnumerable<SectionIdentity> ids) =>
        ids.Select(id => id.Key == null ? id.Type : $"{id.Type} {id.Key}").ToArray();

    private static string[] Ids(EditSession session) => Ids(session.Sections(Universe).Select(s => s.Id));

    private static string[] Ids(IReadOnlyList<Section> sections) => Ids(SectionIdentity.Identify(sections));

    private static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.DoesNotContain(result.Problems, p => p.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Saves and opens the profile again. Then checks that W of the reopened session and the merged
    /// <c>universe.ini</c> of the edit view have the order of <paramref name="expected"/>.</summary>
    /// <returns>The reopened session.</returns>
    private static EditSession AssertSavedOrder(TestWorkspace ws, EditSession session, string[] expected)
    {
        Assert.Equal(expected, Ids(session));
        AssertSaved(session.Save());
        session.Dispose();

        var reopened = ws.Session("act");
        Assert.Equal(expected, Ids(reopened));
        Assert.Equal(expected, Ids(reopened.EditViewSections(Universe)!));
        Assert.False(reopened.IsDirty);
        return reopened;
    }

    [Theory]
    [InlineData(@"DATA\UNIVERSE\universe.ini", "Base", 2)]
    [InlineData("data/universe/UNIVERSE.INI", "base", 2)]
    [InlineData(@"DATA\UNIVERSE\universe.ini", "system", 4)]
    [InlineData(@"DATA\UNIVERSE\universe.ini", "Time", 4)]
    [InlineData(@"DATA\UNIVERSE\SYSTEMS\LI01\universe.ini", "Base", 4)]
    [InlineData(@"DATA\MODS\act\universe.ini", "Base", 4)]
    public void ThePlaceIsTheFirstSystemOnlyForABaseOfUniverseIni(string path, string type, int expected)
    {
        var sections = IniDocument.Parse(path, Encoding.ASCII.GetBytes(
            "[Time]\n[Base]\nnickname = a\n[system]\nnickname = s1\n[system]\nnickname = s2\n")).Sections;

        Assert.Equal(expected, UniverseOrder.PlaceOf(path, sections, type));
    }

    /// <summary>The game reads a section header in any letter case. So a file of a lower mod with <c>[SYSTEM]</c>
    /// headers still gets the new <c>[Base]</c> before its first system.</summary>
    [Theory]
    [InlineData("SYSTEM", "Base", 2)]
    [InlineData("System", "BASE", 2)]
    [InlineData("System", "SYSTEM", 4)]
    public void ThePlaceIgnoresTheLetterCaseOfTheHeaders(string systemHeader, string type, int expected)
    {
        var sections = IniDocument.Parse(Universe, Encoding.ASCII.GetBytes(
            $"[Time]\n[BASE]\nnickname = a\n[{systemHeader}]\nnickname = s1\n[{systemHeader}]\nnickname = s2\n")).Sections;

        Assert.Equal(2, UniverseOrder.FirstSystem(sections));
        Assert.Equal(expected, UniverseOrder.PlaceOf(Universe, sections, type));
    }

    [Fact]
    public void ANewBaseGoesDirectlyBeforeTheFirstSystemInW()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(NewBase("Ol01_01_Base"));
        session.Execute(NewBase("Ol01_02_Base"));

        string[] expected = ["Time", "Base Li01_01_Base", "Base Li01_02_Base", "Base Ol01_01_Base", "Base Ol01_02_Base",
            "system Li01", "system Li02"];
        Assert.Equal(expected, Ids(session));
        Assert.Equal("Add [Base]", session.UndoLabel);
        session.Undo();
        Assert.Equal(expected.Where(id => id != "Base Ol01_02_Base"), Ids(session));
        session.Redo();
        AssertSavedOrder(ws, session, expected);
        // The patch keeps ol.new, needs no directive for the place, and moves no other section.
        Assert.Equal(PatchWriterTests.Lines(
            "[Base]", "ol.new = true", "nickname = Ol01_01_Base", "system = Li01", @"file = MODS\act\BASES\Ol01_01_Base.ini",
            "",
            "[Base]", "ol.new = true", "nickname = Ol01_02_Base", "system = Li01", @"file = MODS\act\BASES\Ol01_02_Base.ini"),
            ws.ReadModPatch(Universe));
    }

    /// <summary>A whole <c>universe.ini</c> of a lower mod can have a <c>[Base]</c> after a <c>[system]</c>. The new
    /// <c>[Base]</c> still goes before every system.</summary>
    [Fact]
    public void ANewBaseGoesBeforeEverySystemWhenALowerModHasABaseAfterASystem()
    {
        using var ws = Workspace(lowerFiles: new()
        {
            [@"files\" + Universe] = """
                [system]
                nickname = Li01
                file = systems\Li01\li01.ini
                [Base]
                nickname = Li01_01_Base
                system = Li01
                [system]
                nickname = Li02
                file = systems\Li02\li02.ini
                """,
        });
        var session = ws.Session("act");

        session.Execute(NewBase("Ol01_01_Base"));

        AssertSavedOrder(ws, session, ["Base Ol01_01_Base", "system Li01", "Base Li01_01_Base", "system Li02"]);
        Assert.DoesNotContain("[system]", ws.ReadModPatch(Universe));
    }

    [Fact]
    public void AUniverseIniWithoutASystemGetsTheNewBaseAtTheEnd()
    {
        const string noSystem = "[Time]\nseconds_per_day = 1800\n[Base]\nnickname = Li01_01_Base\nsystem = Li01\n";
        using var ws = Workspace(noSystem);
        var session = ws.Session("act");

        session.Execute(NewBase("Ol01_01_Base"));

        // The save gate refuses a base of a system that the file does not have, so the test compares W and the merge.
        Assert.Equal(["Time", "Base Li01_01_Base", "Base Ol01_01_Base"], Ids(session));
        var merged = Merge(Universe, noSystem, "[Base]\nol.new = true\nnickname = Ol01_01_Base\n");
        Assert.Equal(["Time", "Base Li01_01_Base", "Base Ol01_01_Base"], Ids(merged.Sections));
    }

    [Fact]
    public void ANewSystemStillGoesAtTheEnd()
    {
        using var ws = Workspace();
        // The save gate refuses a system whose file does not exist.
        ws.WriteBase(@"DATA\UNIVERSE\SYSTEMS\Ol01\Ol01.ini", "[Object]\nnickname = Ol01_sun\narchetype = station1\n");
        var session = ws.Session("act");

        session.Execute(NewSystem("Ol01"));
        session.Execute(NewBase("Ol01_01_Base"));

        AssertSavedOrder(ws, session,
            ["Time", "Base Li01_01_Base", "Base Li01_02_Base", "Base Ol01_01_Base", "system Li01", "system Li02",
             "system Ol01"]);
    }

    /// <summary>A section of B that W has again directly before the first <c>[system]</c> is deleted and added again.
    /// The merge puts the new section there, so the systems after it stay where they are.</summary>
    [Fact]
    public void ABaseThatWAddsAgainBeforeTheFirstSystemMovesNoSystem()
    {
        using var ws = Workspace();
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(new SectionRef(Universe, new SectionIdentity("Base", "Li01_01_Base"))));
        session.Execute(NewBase("Li01_01_Base"));

        AssertSavedOrder(ws, session, ["Time", "Base Li01_02_Base", "Base Li01_01_Base", "system Li01", "system Li02"]);
        var patch = ws.ReadModPatch(Universe);
        Assert.Contains("ol.delete = true", patch);
        Assert.DoesNotContain("[system]", patch);
    }

    /// <summary>A base of B that W adds again after a new base, in its order of B. The patch deletes the base and adds
    /// it again, so the merge puts it after the new base. No system moves, also when a later edit saves the reopened
    /// patch again.</summary>
    [Fact]
    public void ABaseThatWAddsAgainAfterANewBaseMovesNoSystem()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var li0102 = new SectionRef(Universe, new SectionIdentity("Base", "Li01_02_Base"));

        session.Execute(NewBase("Ol01_01_Base"));
        session.Execute(EditCommand.DeleteSection(li0102));
        session.Execute(NewBase("Li01_02_Base"));

        string[] expected = ["Time", "Base Li01_01_Base", "Base Ol01_01_Base", "Base Li01_02_Base", "system Li01",
            "system Li02"];
        var reopened = AssertSavedOrder(ws, session, expected);
        var patch = ws.ReadModPatch(Universe);
        Assert.Contains("ol.delete = true", patch);
        Assert.DoesNotContain("[system]", patch);

        reopened.Execute(EditCommand.SetLinesFromText(li0102, "system", ["Li02"]));
        AssertSavedOrder(ws, reopened, expected);
        Assert.DoesNotContain("[system]", ws.ReadModPatch(Universe));
    }

    /// <summary>The merge puts a new <c>[Base]</c> after the kept bases. So a base of B after a new <c>[Base]</c> in W
    /// is deleted and added again, and the merge gives W. The systems stay where they are.</summary>
    [Fact]
    public void ABaseOfBAfterANewBaseMovesAndNoSystemMoves()
    {
        var b = IniDocument.Parse(Universe, Ascii(RetailOrder));
        var w = IniDocument.Parse(Universe, Ascii(RetailOrder.Replace("[Base]\nnickname = Li01_02_Base",
            "[Base]\nnickname = Ol01_01_Base\nsystem = Li01\n[Base]\nnickname = Li01_02_Base"))).Sections;

        var patch = PatchWriter.Write(Universe, b, w, _ => []);

        Assert.True(patch.Succeeded, string.Join("\n", patch.Problems));
        Assert.Equal(["Time", "Base Li01_01_Base", "Base Ol01_01_Base", "Base Li01_02_Base", "system Li01", "system Li02"],
            Ids(Merge(Universe, RetailOrder, patch.Text).Sections));
        Assert.Contains("[Base]\r\nnickname = Li01_02_Base\r\nol.delete = true", patch.Text);
        Assert.DoesNotContain("[Base]\r\nnickname = Li01_01_Base", patch.Text);
        Assert.DoesNotContain("[system]", patch.Text);
    }

    /// <summary>The rule is only for <c>universe.ini</c>. A <c>[Base]</c> of another file goes at the end, in W and in
    /// the merge.</summary>
    [Fact]
    public void OtherFilesDoNotChange()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        const string other = @"DATA\MODS\act\SYSTEMS\Ol01\Ol01.ini";
        session.Execute(EditCommand.NewFile(other, ContentPathsTests.Sections("[system]\nnickname = s1\n")));

        session.Execute(EditCommand.AddSection(other, "Base", [ListWorkspace.Line("nickname", "b1")]));

        Assert.Equal(["system s1", "Base b1"], Ids(session.Sections(other).Select(s => s.Id)));
        var merged = Merge(other, "[system]\nnickname = s1\n", "[Base]\nol.new = true\nnickname = b1\n");
        Assert.Equal(["system s1", "Base b1"], Ids(merged.Sections));
    }

    /// <summary>The merge puts each new <c>[Base]</c> of a patch directly before the first <c>[system]</c>, in the
    /// order of the patch. A new <c>[system]</c> goes at the end.</summary>
    [Fact]
    public void TheMergeOfANewBasePutsItAtTheSamePlace()
    {
        var merged = Merge(Universe, RetailOrder, """
            [system]
            ol.new = true
            nickname = Ol01
            file = systems\Ol01\Ol01.ini

            [Base]
            ol.new = true
            nickname = Ol01_01_Base
            system = Ol01

            [Base]
            ol.new = true
            nickname = Ol01_02_Base
            system = Ol01
            """);

        Assert.Equal(["Time", "Base Li01_01_Base", "Base Li01_02_Base", "Base Ol01_01_Base", "Base Ol01_02_Base",
            "system Li01", "system Li02", "system Ol01"], Ids(merged.Sections));
    }

    /// <summary>The overlay of the profile (F5) and a reopened patch give the same order as W gave before the
    /// save.</summary>
    [Fact]
    public void TheOverlayOfAHandWrittenPatchHasTheOrder()
    {
        using var ws = Workspace();
        var mod = ws.Mod("hand", new()
        {
            [@"patches\" + Universe] = "[Base]\nol.new = true\nnickname = Ol01_01_Base\nsystem = Li01\n",
        });

        var overlay = ws.Build(mod);

        Assert.Equal(["Time", "Base Li01_01_Base", "Base Li01_02_Base", "Base Ol01_01_Base", "system Li01",
            "system Li02"], Ids(TestWorkspace.Sections(overlay, Universe)));
    }

    /// <summary>The target build (Ctrl+F5) and the drop-in zip of an export have the same order.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTargetBuildAndTheDropInZipHaveTheOrder(bool hd)
    {
        using var game = new ExportGame(hd);
        game.Game.Write(@"DATA\universe\universe.ini", RetailOrder);
        var mod = UniverseMod(game);

        var files = game.Build(mod);
        var package = DropInPackage.Write(files, mod.Manifest, game.Target);

        string[] expected = [.. RetailIds.Take(3), "Base Ol01_01_Base", .. RetailIds.Skip(3)];
        Assert.Equal(expected, Ids(files.File(Universe)!.Merged!.Sections));
        using var zip = new ZipArchive(new MemoryStream(package.Bytes!));
        using var stream = new MemoryStream();
        using (var entry = zip.GetEntry("DATA/universe/universe.ini")!.Open())
            entry.CopyTo(stream);
        Assert.Equal(expected, Ids(IniDocument.Parse(Universe, stream.ToArray()).Sections));
    }

    [Fact]
    public void TheFlmmPackageRefusesANewBaseAndNamesTheDropInZip()
    {
        using var game = new ExportGame();
        game.Game.Write(@"DATA\universe\universe.ini", RetailOrder);
        var mod = UniverseMod(game);

        var script = FlmmScript.Build(game.Build(mod, keepPlaceholders: true), mod,
            FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), probe: false);

        var error = Assert.Single(script.Problems);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal($"The FLMM package cannot hold this change in {Universe}: the mod adds the new base " +
                     "Ol01_01_Base to universe.ini. The game links a base to its station only when the [Base] comes " +
                     "before the [system] sections. FLMM has no tested operation that puts a section there yet. " +
                     "Use the drop-in zip, or change the mod.", error.Message);
        Assert.DoesNotContain("<data", script.Xml);
        Assert.Empty(script.Files);
    }

    /// <summary>The refusal names each new base by its nickname.</summary>
    [Fact]
    public void TheFlmmRefusalNamesEachNewBase()
    {
        using var game = new ExportGame();
        game.Game.Write(@"DATA\universe\universe.ini", RetailOrder);
        var mod = game.Mod("m", new()
        {
            [@"patches\" + Universe] = "[Base]\nol.new = true\nnickname = Ol01_01_Base\nsystem = Li01\n\n" +
                                       "[Base]\nol.new = true\nnickname = Ol01_02_Base\nsystem = Li01\n",
        });

        var script = FlmmScript.Build(game.Build(mod, keepPlaceholders: true), mod,
            FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), probe: false);

        Assert.Contains("the mod adds the new bases Ol01_01_Base, Ol01_02_Base to universe.ini.",
            Assert.Single(script.Problems).Message);
    }

    /// <summary>A change of a line of a <c>[Base]</c> is no new <c>[Base]</c>, so the package holds it.</summary>
    [Fact]
    public void TheFlmmPackageHoldsAChangeOfABase()
    {
        using var game = new ExportGame();
        game.Game.Write(@"DATA\universe\universe.ini", RetailOrder);
        var mod = game.Mod("m", new() { [@"patches\" + Universe] = "[Base]\nnickname = Li01_02_Base\nsystem = Li02\n" });

        var script = FlmmScript.Build(game.Build(mod, keepPlaceholders: true), mod,
            FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), probe: false);

        Assert.Empty(script.Problems);
        Assert.Contains("method=\"sectionreplace\"", script.Xml);
    }

    /// <summary>The refusal of a whole new file with mod text also names the drop-in zip (shared rule of the FLMM
    /// package).</summary>
    [Fact]
    public void TheFlmmPackageRefusesAWholeFileWithModTextAndNamesTheDropInZip()
    {
        using var game = new ExportGame();
        const string file = @"DATA\MODS\m\SYSTEMS\Ol01\Ol01.ini";
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "strings": { "sun": "Ol01 Sun" } }""",
            [@"files\" + file] = "[Object]\nnickname = Ol01_sun\nids_name = $str:sun\n",
        });

        var script = FlmmScript.Build(game.Build(mod, keepPlaceholders: true), mod,
            FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), probe: false);

        var error = Assert.Single(script.Problems);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal($"The FLMM package cannot hold this change in {file}: the whole file holds mod text. FLMM reads " +
                     "its text macros only in script operations. Use the drop-in zip, or change the mod.", error.Message);
    }

    /// <summary>A mod of the export tests with one new <c>[Base]</c> in its patch of <c>universe.ini</c>.</summary>
    private static Mod UniverseMod(ExportGame game) => game.Mod("m", new()
    {
        [@"patches\" + Universe] = "[Base]\nol.new = true\nnickname = Ol01_01_Base\nsystem = Li01\n",
    });

    /// <summary>The document that the merge of a patch gives from a file. The merge must give no diagnostics.</summary>
    private static IniDocument Merge(string path, string text, string patch)
    {
        var merged = IniDocument.Parse(path, Ascii(text));
        var diagnostics = new List<Diagnostic>();
        IniPatcher.Apply(merged, IniDocument.Parse(path, Ascii(patch)).Sections,
            new PatchContext("act", path, path, null, new PatchLedger(), diagnostics));
        Assert.Empty(diagnostics);
        return merged;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
}
