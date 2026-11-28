using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The script of the FLMM package (section 7.11 of the M11 design, and the line form that FLMM 1.31
/// reads).</summary>
public sealed partial class FlmmScriptTests
{
    private const string Goods = @"DATA\EQUIPMENT\goods.ini";

    private static FlmmScript Script(ExportGame game, Mod mod, bool probe = false) =>
        FlmmScript.Build(game.Build(mod, keepPlaceholders: true), mod,
            FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), probe);

    private static FlmmScript Script(ExportGame game, string patch, string strings = "{}") =>
        Script(game, game.Mod("m", new() { ["strings.json"] = strings, [@"patches\" + GoodsPath] = patch }));

    /// <summary>The text of one operation: each tag, each tag end and each content line on its own line.</summary>
    private static string Op(string method, string[]? section, string? dest, string[] source, string file = Goods) =>
        $"<data file=\"{file}\" method=\"{method}\">\r\n" +
        (section == null ? "" : Element("section", section)) +
        (dest == null ? "" : Element("dest", [dest])) +
        Element("source", source) + "</data>\r\n";

    private static string Element(string name, string[] lines) =>
        $"<{name}>\r\n" + string.Concat(lines.Select(l => l + "\r\n")) + $"</{name}>\r\n";

    [Fact]
    public void TheScriptHasNoXmlDeclaration()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = g2\nprice = 1\n");

        Assert.StartsWith("<script>\r\n<header name=\"Mod m\">\r\n<scriptversion>\r\n1.31\r\n</scriptversion>\r\n" +
                          "<author>\r\n</author>\r\n<description>\r\nVersion 1.2.0 for Freelancer 1.1.\r\n</description>\r\n" +
                          "</header>\r\n", script.Xml);
        Assert.EndsWith("</script>\r\n", script.Xml);
        Assert.All(script.Xml, c => Assert.True(c < 128));
        Assert.Empty(ScriptFormProblems(script.Xml));
    }

    [Fact]
    public void ANewSectionIsAnAppendWithoutSection()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nol.new = true\nnickname = n\nprice = 5\n");

        Assert.Contains(Op("append", null, null, ["", "[Good]", "nickname = n", "price = 5"]), script.Xml);
        Assert.Empty(script.Problems);
        Assert.Empty(script.Files);
    }

    [Fact]
    public void ANewLineIsASectionAppendWithTheRawAnchor()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = g2\nvolume = 2\n");

        Assert.Contains(Op("sectionappend", ["[Good]", "nickname = g2"], null, ["volume = 2"]), script.Xml);
    }

    [Fact]
    public void AChangedValueIsASectionReplaceWithTheRawBaseLine()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = lf_hull\nprice = 5600\n");

        Assert.Contains(Op("sectionreplace", ["[Good]", "nickname = lf_hull"], "price = 6600 ; the Patriot hull",
            ["price = 5600 ; the Patriot hull"]), script.Xml);
    }

    /// <summary>The content lines keep the indent of the game file and get no indent from the script.</summary>
    [Fact]
    public void AContentLineKeepsTheIndentOfTheGameFile()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nnickname = g2\n\tprice = 200\n");

        var script = Script(game, "[Good]\nnickname = g2\nprice = 5\n");

        Assert.Contains(Op("sectionreplace", ["[Good]", "nickname = g2"], "\tprice = 200", ["\tprice = 5"]), script.Xml);
    }

    [Fact]
    public void AMarketLineIsASectionAppend()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            [@"patches\" + MarketPath] = "[BaseGood]\nbase = li01_01_base\nmarketgood = g2, 0, -1, 1, 1, 0, 1, 1\n",
        });

        var script = Script(game, mod);

        Assert.Contains(Op("sectionappend", ["[BaseGood]", "base = li01_01_base"], null,
            ["marketgood = g2, 0, -1, 1, 1, 0, 1, 1"], @"DATA\EQUIPMENT\market_ships.ini"), script.Xml);
    }

    /// <summary>
    /// The games keep the last line of a good at a base. A line that changes or removes a line of the game must come
    /// after it, so its new section goes to the end of the file with an append. A section of new goods only keeps the
    /// section append after the header of the game section.
    /// </summary>
    [Theory]
    [InlineData("marketgood = lf_hull, 0, -1, 1, 1, 0, 2, 1")]
    [InlineData("marketgood = lf_hull, 0, -1, 0, 0, 1, 1, 1")]
    public void AMarketLineOfAGoodOfTheGameSectionIsAnAppend(string line)
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            [@"patches\" + MarketPath] = $"[BaseGood]\nbase = li01_01_base\n{line}\nmarketgood = g2, 0, -1, 1, 1, 0, 1, 1\n",
        });

        var script = Script(game, mod);

        Assert.Empty(script.Problems);
        Assert.Contains(Op("append", null, null, ["", "[BaseGood]", "base = li01_01_base", line,
            "marketgood = g2, 0, -1, 1, 1, 0, 1, 1"], MarketPath), script.Xml);
        Assert.DoesNotContain("sectionappend", script.Xml);
        Assert.Empty(ScriptFormProblems(script.Xml));
    }

    [Fact]
    public void DataLinesAppendAfterTheLastLineOfTheirKey()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            [@"patches\EXE\freelancer.ini"] = "[Data]\nol.append = true\ngoods = MODS\\m\\goods.ini\n",
            [@"files\DATA\MODS\m\goods.ini"] = "[Good]\nnickname = new_good\nprice = 1\n",
        });

        var script = Script(game, mod);

        Assert.Empty(script.Problems);
        Assert.Contains(Op("sectionappend", [@"goods = equipment\goods.ini"], null, [@"goods = MODS\m\goods.ini"],
            @"EXE\freelancer.ini"), script.Xml);
        Assert.Equal([@"DATA\MODS\m\goods.ini"], script.Files.Select(f => f.Path));
    }

    /// <summary>A comment before the last goods line holds the text of that line. FLMM can take the comment as the
    /// anchor, so the export refuses the new line.</summary>
    [Fact]
    public void ADataLineWhoseAnchorFlmmCanFindTwiceIsRefused()
    {
        using var game = new ExportGame();
        game.Game.Write(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n\n[Data]\n; goods = equipment\\goods.ini " +
                                               "is the list of goods\ngoods = equipment\\goods.ini\n\n[Resources]\n" +
                                               "DLL = InfoCards.dll\n");
        var mod = game.Mod("m", new()
        {
            [@"patches\EXE\freelancer.ini"] = "[Data]\nol.append = true\ngoods = MODS\\m\\goods.ini\n",
            [@"files\DATA\MODS\m\goods.ini"] = "[Good]\nnickname = new_good\nprice = 1\n",
        });

        var script = Script(game, mod);

        var error = Assert.Single(script.Problems);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(@"The FLMM package cannot hold this change in EXE\freelancer.ini: FLMM can find 'goods = " +
                     @"equipment\goods.ini' at more than one place in freelancer.ini. Use the drop-in zip, or change the mod.",
            error.Message);
        Assert.Empty(SteLint.Problems(error.Message));
        Assert.DoesNotContain("<data", script.Xml);
    }

    [Fact]
    public void TextBecomesGenerateMacros()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nol.new = true\nnickname = n\nids_name = $str:name\n",
            """{ "strings": { "name": "Starrunner" } }""");

        Assert.Empty(script.Problems);
        Assert.Contains("\r\nids_name = 0 ;GENERATESTRRES(\"Starrunner\")\r\n", script.Xml);
        // FLMM makes its own text DLL, so the package has no DLL and no freelancer.ini.
        Assert.Empty(script.Files);
        Assert.DoesNotContain("freelancer.ini", script.Xml);
    }

    [Fact]
    public void AnInfocardBecomesAnXmlMacroOfTheSameForm()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "infocards": { "info": "<RDL><TEXT>x</TEXT></RDL>" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_info = $ifc:info\n",
        });

        var script = Script(game, mod);

        Assert.Empty(script.Problems);
        Assert.Contains(Op("sectionappend", ["[Good]", "nickname = g2"], null,
            ["ids_info = 0 ;GENERATEXMLRES(\"&lt;RDL&gt;&lt;TEXT&gt;x&lt;/TEXT&gt;&lt;/RDL&gt;\")"]), script.Xml);
    }

    /// <summary>The macro comes after a semicolon, so a comment after the text of a changed line goes away.</summary>
    [Fact]
    public void AMacroReplacesTheCommentOfAChangedLine()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nnickname = g2\nids_name = 100 ; the old name\n");

        var script = Script(game, "[Good]\nnickname = g2\nids_name = $str:n\n", """{ "strings": { "n": "Name" } }""");

        Assert.Empty(script.Problems);
        Assert.Contains(Op("sectionreplace", ["[Good]", "nickname = g2"], "ids_name = 100 ; the old name",
            ["ids_name = 0 ;GENERATESTRRES(\"Name\")"]), script.Xml);
    }

    /// <summary>The game reads the text after a semicolon as a comment. So a text that is not the last value of its
    /// line, and a second text in one line, refuse the operation.</summary>
    [Theory]
    [InlineData("ids_name = $str:n, 5", "the text '$str:n' is not the last value of the line 'ids_name = $str:n, 5'. " +
                                        "FLMM writes a text after a semicolon, and the game reads the rest of the line " +
                                        "as a comment")]
    [InlineData("ids_name = $str:n, $str:m", "the line 'ids_name = $str:n, $str:m' has more than one text. FLMM writes " +
                                             "a text after a semicolon, so a line can hold only one text, as its last value")]
    public void ATextThatIsNotTheLastValueOfItsLineIsRefused(string line, string reason)
    {
        using var game = new ExportGame();

        var script = Script(game, $"[Good]\nol.new = true\nnickname = n\n{line}\n",
            """{ "strings": { "n": "Name", "m": "More" } }""");

        var error = Assert.Single(script.Problems);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal($"The FLMM package cannot hold this change in {Goods}: {reason}. Use the drop-in zip, or change the " +
                     "mod.", error.Message);
        Assert.Empty(SteLint.Problems(error.Message));
        Assert.DoesNotContain("<data", script.Xml);
    }

    [Fact]
    public void ATextWithALineEndIsRefused()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nol.new = true\nnickname = n\nids_name = $str:n\n",
            """{ "strings": { "n": "One\nTwo" } }""");

        var error = Assert.Single(script.Problems);
        Assert.Equal($"The FLMM package cannot hold this change in {Goods}: the text of '$str:n' has a line end. FLMM reads " +
                     "a text macro only on one line. Use the drop-in zip, or change the mod.", error.Message);
        Assert.Empty(SteLint.Problems(error.Message));
    }

    [Fact]
    public void AQuoteInTextIsRefusedWithoutTheProbe()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "infocards": { "info": "<?xml version=\"1.0\"?><RDL><TEXT>x</TEXT></RDL>" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_info = $ifc:info\n",
        });

        var refused = Script(game, mod);
        var probe = Script(game, mod, probe: true);

        var error = Assert.Single(refused.Problems);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal($"The FLMM package cannot hold this change in {Goods}: the text of '$ifc:info' has a quote, and " +
                     "FLMM has no known escape for it. Use the drop-in zip, or change the mod.", error.Message);
        Assert.Empty(probe.Problems);
        Assert.Contains("\r\nids_info = 0 ;GENERATEXMLRES(\"&lt;?xml version=\"1.0\"?&gt;&lt;RDL&gt;&lt;TEXT&gt;x" +
                        "&lt;/TEXT&gt;&lt;/RDL&gt;\")\r\n", probe.Xml);
    }

    [Fact]
    public void ANonAsciiCharacterIsRefused()
    {
        using var game = new ExportGame();

        // The JSON escape of the character keeps the file ASCII.
        var backslash = (char)0x5C;
        var script = Script(game, "[Good]\nol.new = true\nnickname = n\nids_name = $str:name\n",
            $$"""{ "strings": { "name": "Caf{{backslash}}u00e9" } }""");

        Assert.Equal($"The FLMM package cannot hold the character U+00E9 in {Goods}. Use the drop-in zip.",
            Assert.Single(script.Problems).Message);
        Assert.DoesNotContain("Caf", script.Xml);
    }

    [Fact]
    public void ARemovedSectionFallsBackToAWholeFile()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = g2\nol.delete = true\n");

        var warning = Assert.Single(script.Problems);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal($"The FLMM package holds the whole merged file {Goods}, because the mod removes a section. The file " +
                     "undoes the changes of other mods in it.", warning.Message);
        Assert.Equal([Goods], script.Files.Select(f => f.Path));
        Assert.DoesNotContain("<data", script.Xml);
    }

    [Fact]
    public void AWholeFileWithModTextIsRefused()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = g2\nol.delete = true\n\n[Good]\nnickname = lf_hull\nids_name = $str:n\n",
            """{ "strings": { "n": "Name" } }""");

        var error = Assert.Single(script.Problems);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("the mod removes a section, and the whole file would hold mod text", error.Message);
        Assert.Empty(script.Files);
    }

    /// <summary>The anchor of g2 is also the start of the text of the section of g22. The section of g2 comes first, so
    /// the first text match of its anchor is the anchor itself.</summary>
    [Fact]
    public void AKeyThatStartsALaterKeyAnchorsWithItsLines()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, ExportGame.Goods + "\n[Good]\nnickname = g22\nprice = 1\n");

        var script = Script(game, "[Good]\nnickname = g2\nprice = 5\nvolume = 2\n");

        Assert.Empty(script.Problems);
        Assert.Contains(Op("sectionreplace", ["[Good]", "nickname = g2"], "price = 200", ["price = 5"]), script.Xml);
        Assert.Contains(Op("sectionappend", ["[Good]", "nickname = g2"], null, ["volume = 2"]), script.Xml);
    }

    /// <summary>The anchor of ring_1 occurs once as whole lines. But its first text match is in the section of ring_10,
    /// which comes first, and FLMM takes the first match. So the package holds the whole file.</summary>
    [Fact]
    public void AKeyThatStartsAnEarlierKeyFallsBack()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nnickname = ring_10\nprice = 1\n\n[Good]\nnickname = ring_1\nprice = 2\n");

        var script = Script(game, "[Good]\nnickname = ring_1\nprice = 5\n");

        AssertFallsBack(script, "FLMM can find the anchor of [Good] at more than one place");
        Assert.Equal([Goods], script.Files.Select(f => f.Path), System.StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>FLMM takes the first match of the dest line below the header. A comment before the line holds its text,
    /// so the package holds the whole file.</summary>
    [Fact]
    public void ADestLineWhoseTextComesEarlierInItsSectionFallsBack()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nnickname = g2\n; price = 200 is the old price\nprice = 200\n");

        var script = Script(game, "[Good]\nnickname = g2\nprice = 5\n");

        AssertFallsBack(script, "FLMM can find 'price = 200' at more than one place in its section");
    }

    /// <summary>The changed line occurs twice in its section as a whole line. The test did not show if FLMM changes the
    /// first line or both lines. So the package holds the whole file, also when the first line is the changed
    /// line.</summary>
    [Fact]
    public void ADestLineThatOccursTwiceInItsSectionFallsBack()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nnickname = g2\nprice = 200\nvolume = 1\nprice = 200\n");

        // The lines of a patch replace all lines of their names, so the patch keeps the other two lines.
        var script = Script(game, "[Good]\nnickname = g2\nprice = 5\nvolume = 1\nprice = 200\n");

        AssertFallsBack(script, "FLMM can find 'price = 200' at more than one place in its section");
    }

    /// <summary>FLMM finds the line of dest below the header of its section. The same line in another section does not
    /// matter, also when that section comes later.</summary>
    [Theory]
    [InlineData("g2")]
    [InlineData("g3")]
    public void ADestLineThatAlsoOccursInAnotherSectionIsASectionReplace(string nickname)
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nnickname = g2\nprice = 200\n\n[Good]\nnickname = g3\nprice = 200\n");

        var script = Script(game, $"[Good]\nnickname = {nickname}\nprice = 5\n");

        Assert.Empty(script.Problems);
        Assert.Contains(Op("sectionreplace", ["[Good]", $"nickname = {nickname}"], "price = 200", ["price = 5"]), script.Xml);
    }

    /// <summary>A later text match of the dest line does not matter, because FLMM takes the first match.</summary>
    [Fact]
    public void ADestLineWhoseTextComesLaterInItsSectionIsASectionReplace()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nnickname = g2\nprice = 200\n; price = 2000 is the next price\n");

        var script = Script(game, "[Good]\nnickname = g2\nprice = 5\n");

        Assert.Empty(script.Problems);
        Assert.Contains(Op("sectionreplace", ["[Good]", "nickname = g2"], "price = 200", ["price = 5"]), script.Xml);
    }

    /// <summary>A file with a section and a comment that holds its header.</summary>
    private const string SystemFile = @"DATA\UNIVERSE\SYSTEMS\X\x.ini";

    private static FlmmScript SystemScript(ExportGame game, string comment)
    {
        game.Game.Write(SystemFile, $"; {comment}\n[SystemInfo]\nspace_color = 0, 0, 0\n");
        return Script(game, game.Mod("m", new()
        {
            [@"patches\" + SystemFile] = "[SystemInfo]\nspace_color = 1, 2, 3\n",
        }));
    }

    [Fact]
    public void AnAnchorThatIsNotUniqueFallsBack()
    {
        using var game = new ExportGame();

        var script = SystemScript(game, "[SystemInfo]");

        AssertFallsBack(script, "FLMM can find the anchor of [SystemInfo] at more than one place");
        // The path has the letter case of the folders of the game.
        Assert.Equal([SystemFile], script.Files.Select(f => f.Path), System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAnchorThatIsUniqueOnlyByCaseFallsBack()
    {
        using var game = new ExportGame();

        var script = SystemScript(game, "[SYSTEMINFO]");

        AssertFallsBack(script, "FLMM can find the anchor of [SystemInfo] at more than one place");
    }

    /// <summary>FLMM adds a line after the key line. In a mission file, the game reads the lines of a formation in order,
    /// so the package holds the whole merged file.</summary>
    [Fact]
    public void ANewLineOfAMissionSectionWhoseLineOrderCountsFallsBack()
    {
        using var game = new ExportGame();
        const string script = @"DATA\MISSIONS\OL_TEST\ol_test.ini";
        game.Game.Write(script, "[MsnFormation]\nnickname = ol_form\nship = ol_ship_a\n");

        var flmm = Script(game, game.Mod("m", new()
        {
            [@"patches\" + script] = "[MsnFormation]\nnickname = ol_form\nol.replace = true\nship = ol_ship_a\nship = ol_ship_b\n",
        }));

        Assert.Contains("because the game reads the lines of [MsnFormation] in order, and FLMM adds lines after the key " +
                        "line", Assert.Single(flmm.Problems).Message);
        Assert.Equal([script], flmm.Files.Select(f => f.Path), System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AKeyLineAfterAnotherLineFallsBack()
    {
        using var game = new ExportGame();
        game.Game.Write(GoodsPath, "[Good]\nprice = 1\nnickname = g2\n");

        var script = Script(game, "[Good]\nnickname = g2\nprice = 5\n");

        AssertFallsBack(script, "the key line of [Good] does not directly follow its header");
    }

    /// <summary>The one warning of a script: the package holds the whole merged file for the reason. The warning passes
    /// the STE lint.</summary>
    private static void AssertFallsBack(FlmmScript script, string reason)
    {
        var warning = Assert.Single(script.Problems);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.True(warning.Message.Contains($", because {reason}. The file undoes the changes of other mods in it."),
            warning.Message);
        Assert.Empty(SteLint.Problems(warning.Message));
        Assert.DoesNotContain("<data", script.Xml);
    }

    [Fact]
    public void TheScriptIsValidXmlAndEscapesAnInfocard()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "infocards": { "info": "<RDL><TEXT>A &amp; B</TEXT></RDL>" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_info = $ifc:info\n",
        });

        var script = Script(game, mod);

        Assert.Empty(script.Problems);
        var xml = new XmlDocument();
        xml.LoadXml(script.Xml);
        Assert.Equal("ids_info = 0 ;GENERATEXMLRES(\"<RDL><TEXT>A &amp; B</TEXT></RDL>\")",
            xml.SelectSingleNode("/script/data/source")!.InnerText.Trim());
        Assert.Equal("Mod m", xml.SelectSingleNode("/script/header/@name")!.Value);
    }

    /// <summary>The script of an exported package with each kind of operation and a text macro has the line form of
    /// FLMM 1.31: one tag or tag end on each line, and each attribute value in quotes.</summary>
    [Fact]
    public void TheScriptOfAnExportHasOneTagOnEachLine()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "strings": { "n": "Starrunner" }, "infocards": { "i": "<RDL><TEXT>x</TEXT></RDL>" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = lf_hull\nprice = 5600\n\n[Good]\nnickname = g2\nvolume = 2\n" +
                                       "ids_info = $ifc:i\n\n[Good]\nol.new = true\nnickname = n\nids_name = $str:n\n",
            [@"patches\" + MarketPath] = "[BaseGood]\nbase = li01_01_base\nmarketgood = n, 0, -1, 1, 1, 0, 1, 1\n",
            [@"patches\EXE\freelancer.ini"] = "[Data]\nol.append = true\ngoods = MODS\\m\\goods.ini\n",
            [@"files\DATA\MODS\m\goods.ini"] = "[Good]\nnickname = new_good\nprice = 1\n",
        });

        var result = ModExport.Run(game.Request(mod) with { Publish = PublishSettings.Empty with { Authors = ["Ann"] },
            Forms = ExportForms.Flmm });

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        using var outer = ZipFile.OpenRead(result.Packages.Single().Path!);
        using var flmod = new ZipArchive(outer.GetEntry("m.zip.flmod")!.Open());
        using var reader = new StreamReader(flmod.GetEntry("script.xml")!.Open());
        var script = reader.ReadToEnd();
        Assert.Empty(ScriptFormProblems(script));
        foreach (var method in new[] { "append", "sectionappend", "sectionreplace" })
            Assert.Contains($" method=\"{method}\">\r\n", script);
        Assert.Contains("\r\n<author>\r\nAnn\r\n</author>\r\n", script);
        Assert.Contains("\r\nids_name = 0 ;GENERATESTRRES(\"Starrunner\")\r\n", script);
        Assert.Contains("\r\nids_info = 0 ;GENERATEXMLRES(\"&lt;RDL&gt;&lt;TEXT&gt;x&lt;/TEXT&gt;&lt;/RDL&gt;\")\r\n", script);
        Assert.DoesNotContain("\n ", script);
        new XmlDocument().LoadXml(script);
    }

    /// <summary>The form check of the test finds the two faults of the first FLMM test package.</summary>
    [Fact]
    public void TheFormCheckFindsTwoTagsOnOneLineAndAnAttributeWithoutQuotes()
    {
        Assert.Equal(["line 2: more than one tag"],
            ScriptFormProblems("<script>\r\n<scriptversion>1.31</scriptversion>\r\n</script>\r\n"));
        Assert.Equal(["line 2: an attribute value without quotes"],
            ScriptFormProblems("<script>\r\n<data file=\"a.ini\" numTimes=1>\r\n</data>\r\n</script>\r\n"));
        Assert.Equal(["line 1: text and a tag"], ScriptFormProblems("<script>x\r\n</script>\r\n"));
        Assert.Empty(ScriptFormProblems("<script>\r\n<data file=\"a.ini\" method=\"append\">\r\n<source>\r\n[Good]\r\n" +
                                        "</source>\r\n</data>\r\n</script>\r\n"));
    }

    /// <summary>
    /// The faults of the line form of a script that FLMM 1.31 reads: a line with more than one tag, a line with text and
    /// a tag, and a tag with an attribute value without quotes. Each line that holds <c>&lt;</c> or <c>&gt;</c> must be
    /// one whole tag or tag end.
    /// </summary>
    private static List<string> ScriptFormProblems(string xml)
    {
        var problems = new List<string>();
        var lines = xml.Split("\r\n");
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var tags = line.Count(c => c == '<');
            if (tags == 0 && !line.Contains('>'))
                continue;
            var fault = tags > 1 ? "more than one tag"
                : !line.StartsWith('<') || !line.EndsWith('>') ? "text and a tag"
                : !TagLine().IsMatch(line) ? "an attribute value without quotes"
                : null;
            if (fault != null)
                problems.Add($"line {i + 1}: {fault}");
        }
        return problems;
    }

    /// <summary>A tag with its attributes in quotes, or a tag end.</summary>
    [GeneratedRegex("""^(?:<[A-Za-z]+(?: [A-Za-z]+="[^"<>]*")*>|</[A-Za-z]+>)$""")]
    private static partial Regex TagLine();

    [Fact]
    public void TwoExportsGiveTheSameBytes()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new()
        {
            ["strings.json"] = """{ "strings": { "n": "Name" } }""",
            [@"patches\" + GoodsPath] = "[Good]\nnickname = g2\nids_name = $str:n\n",
            [@"files\DATA\MODS\m\a.txt"] = "a",
        });

        var one = FlmmPackage.Write(game.Build(mod, keepPlaceholders: true), mod, game.Target, false);
        var two = FlmmPackage.Write(game.Build(mod, keepPlaceholders: true), mod, game.Target, false);
        var probe = FlmmPackage.Write(game.Build(mod, keepPlaceholders: true), mod, game.Target, true);

        Assert.Equal(one.Bytes, two.Bytes);
        Assert.Equal("m-1.2.0-vanilla-1.1-flmm.zip", one.FileName);
        Assert.Equal("m-1.2.0-vanilla-1.1-flmm-probe.zip", probe.FileName);
        using var zip = new ZipArchive(new MemoryStream(one.Bytes!));
        Assert.Equal(["m README.txt", "m.zip.flmod"], zip.Entries.Select(e => e.FullName));
        using var flmod = new ZipArchive(zip.GetEntry("m.zip.flmod")!.Open());
        Assert.Equal(["DATA/MODS/m/a.txt", "script.xml"], flmod.Entries.Select(e => e.FullName));
        using (var readme = new StreamReader(zip.GetEntry("m README.txt")!.Open(), Encoding.UTF8))
            Assert.Contains("\r\n1. In the Freelancer Mod Manager, import m.zip.flmod.\r\n", readme.ReadToEnd());
        using var probeZip = new ZipArchive(new MemoryStream(probe.Bytes!));
        using var probeReadme = new StreamReader(probeZip.GetEntry("m README.txt")!.Open(), Encoding.UTF8);
        Assert.Contains("Probe for the FLMM check. Do not publish.", probeReadme.ReadToEnd());
        Assert.False(FlmmPackage.Checked);
    }

    /// <summary>
    /// A new lock of a mod for both games (issue #94) is one sectionappend after the header of [locked_gates], on each
    /// game. The package has no whole file and no warning, and the merged file keeps each lock line of the game. The play
    /// copy of a linked game gets the same merged file.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewLockIsOneSectionAppendOnEachGame(bool hd)
    {
        using var game = new ExportGame(hd);
        var world = hd ? ItemListMergeTests.Hde : ItemListMergeTests.Vanilla;
        game.Game.Write(ItemListMergeTests.WorldPath, world);
        var line = $"locked_gate = {ItemListMergeTests.Hash("Ol03_to_Ol01")}";
        var mod = game.Mod("m", new()
        {
            [@"patches\" + ItemListMergeTests.WorldPath] = $"[locked_gates]\nol.append = true\n{line}\n",
        });
        var files = game.Build(mod, keepPlaceholders: true);

        var script = FlmmScript.Build(files, mod, FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), false);

        Assert.Empty(script.Problems);
        Assert.Empty(script.Files);
        Assert.Contains(Op("sectionappend", ["[locked_gates]"], null, [line], ItemListMergeTests.WorldPath), script.Xml);
        Assert.Single(Regex.Matches(script.Xml, "<data "));
        var merged = Parse(Text(files.File(ItemListMergeTests.WorldPath)!.Bytes)).Sections;
        var before = Parse(world).Sections;
        Assert.Equal([.. ItemListMergeTests.Locks(before, "locked_gate"), ItemListMergeTests.Hash("Ol03_to_Ol01")],
            ItemListMergeTests.Locks(merged, "locked_gate"));
        Assert.Equal(ItemListMergeTests.Locks(before, "npc_locked_gate"), ItemListMergeTests.Locks(merged, "npc_locked_gate"));
    }

    /// <summary>The game has the route tables, but the package copies a generated table as a whole file, with no
    /// operation and no warning.</summary>
    [Fact]
    public void AGeneratedRouteTableIsAPlainCopy()
    {
        using var game = new RouteWorld().System("A", "gate B").System("B", "gate A").WriteInto(new ExportGame());
        var mod = RouteWorld.ConnectionMod(game);
        var files = RouteWorld.Build(game, mod, keepPlaceholders: true);

        var script = FlmmScript.Build(files, mod, FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), false);

        Assert.Empty(script.Problems);
        foreach (var route in RouteWorld.RouteFiles(files))
        {
            Assert.True(route.HasBase);
            var copy = Assert.Single(script.Files, f => f.Path.EndsWith(route.Path[route.Path.LastIndexOf('\\')..]));
            Assert.Equal(route.Bytes, copy.Bytes);
            Assert.DoesNotContain(route.Path[(route.Path.LastIndexOf('\\') + 1)..], script.Xml);
        }
    }
}
