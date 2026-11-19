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
/// The place of a new <c>[Base]</c> in <c>universe.ini</c> (<see cref="UniverseOrder"/>): directly before the first
/// <c>[system]</c>, in W, in the merge, in the patch that a save writes and in the export. The data is invented.
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

    /// <summary>Saves, opens the profile again, and checks that W of the reopened session and the merged
    /// <c>universe.ini</c> of the edit view have the order of <paramref name="expected"/>.</summary>
    private static void AssertSavedOrder(TestWorkspace ws, EditSession session, string[] expected)
    {
        Assert.Equal(expected, Ids(session));
        AssertSaved(session.Save());
        session.Dispose();

        var reopened = ws.Session("act");
        Assert.Equal(expected, Ids(reopened));
        Assert.Equal(expected, Ids(reopened.EditViewSections(Universe)!));
        Assert.False(reopened.IsDirty);
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

    /// <summary>A new <c>[Base]</c> that a kept <c>[Base]</c> separates from the first <c>[system]</c> does not keep
    /// its place in the merge. So the patch deletes and adds again each section after it, and the merge gives
    /// W.</summary>
    [Fact]
    public void ANewBaseThatAKeptBaseSeparatesFromTheSystemsMovesTheSectionsAfterIt()
    {
        var b = IniDocument.Parse(Universe, Ascii(RetailOrder));
        var w = IniDocument.Parse(Universe, Ascii(RetailOrder.Replace("[Base]\nnickname = Li01_02_Base",
            "[Base]\nnickname = Ol01_01_Base\nsystem = Li01\n[Base]\nnickname = Li01_02_Base"))).Sections;

        var patch = PatchWriter.Write(Universe, b, w, _ => []);

        Assert.True(patch.Succeeded, string.Join("\n", patch.Problems));
        Assert.Equal(["Time", "Base Li01_01_Base", "Base Ol01_01_Base", "Base Li01_02_Base", "system Li01", "system Li02"],
            Ids(Merge(Universe, RetailOrder, patch.Text).Sections));
        Assert.Contains("[system]\r\nnickname = Li01\r\nol.delete = true", patch.Text);
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
