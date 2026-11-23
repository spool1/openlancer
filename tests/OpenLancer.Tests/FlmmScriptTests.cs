using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The script of the FLMM package (section 7.11 of the M11 design).</summary>
public sealed class FlmmScriptTests
{
    private const string Goods = @"DATA\EQUIPMENT\goods.ini";

    private static FlmmScript Script(ExportGame game, Mod mod, bool probe = false) =>
        FlmmScript.Build(game.Build(mod, keepPlaceholders: true), mod,
            FlmmHeader.Of(PublishTexts.Of(mod.Manifest), game.Target), probe);

    private static FlmmScript Script(ExportGame game, string patch, string strings = "{}") =>
        Script(game, game.Mod("m", new() { ["strings.json"] = strings, [@"patches\" + GoodsPath] = patch }));

    private static string Op(string method, string? section, string? dest, string source, string file = Goods) =>
        $"  <data file=\"{file}\" method=\"{method}\">\r\n" +
        (section == null ? "" : $"    <section>{section}</section>\r\n") +
        (dest == null ? "" : $"    <dest>{dest}</dest>\r\n") +
        $"    <source>{source}</source>\r\n  </data>\r\n";

    [Fact]
    public void TheScriptHasNoXmlDeclaration()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = g2\nprice = 1\n");

        Assert.StartsWith("<script>\r\n  <header name=\"Mod m\">\r\n    <scriptversion>1.31</scriptversion>\r\n", script.Xml);
        Assert.EndsWith("</script>\r\n", script.Xml);
        Assert.All(script.Xml, c => Assert.True(c < 128));
    }

    [Fact]
    public void ANewSectionIsAnAppendWithoutSection()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nol.new = true\nnickname = n\nprice = 5\n");

        Assert.Contains(Op("append", null, null, "\r\n[Good]\r\nnickname = n\r\nprice = 5\r\n"), script.Xml);
        Assert.Empty(script.Problems);
        Assert.Empty(script.Files);
    }

    [Fact]
    public void ANewLineIsASectionAppendWithTheRawAnchor()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = g2\nvolume = 2\n");

        Assert.Contains(Op("sectionappend", "[Good]\r\nnickname = g2", null, "\r\nvolume = 2"), script.Xml);
    }

    [Fact]
    public void AChangedValueIsASectionReplaceWithTheRawBaseLine()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nnickname = lf_hull\nprice = 5600\n");

        Assert.Contains(Op("sectionreplace", "[Good]\r\nnickname = lf_hull", "price = 6600 ; the Patriot hull",
            "price = 5600 ; the Patriot hull"), script.Xml);
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

        Assert.Contains(Op("sectionappend", "[BaseGood]\r\nbase = li01_01_base", null,
            "\r\nmarketgood = g2, 0, -1, 1, 1, 0, 1, 1", @"DATA\EQUIPMENT\market_ships.ini"), script.Xml);
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
        Assert.Contains(Op("sectionappend", @"goods = equipment\goods.ini", null, "\r\ngoods = MODS\\m\\goods.ini",
            @"EXE\freelancer.ini"), script.Xml);
        Assert.Equal([@"DATA\MODS\m\goods.ini"], script.Files.Select(f => f.Path));
    }

    [Fact]
    public void TextBecomesGenerateMacros()
    {
        using var game = new ExportGame();

        var script = Script(game, "[Good]\nol.new = true\nnickname = n\nids_name = $str:name\n",
            """{ "strings": { "name": "Starrunner" } }""");

        Assert.Empty(script.Problems);
        Assert.Contains("ids_name = GENERATESTRRES(\"Starrunner\")", script.Xml);
        // FLMM makes its own text DLL, so the package has no DLL and no freelancer.ini.
        Assert.Empty(script.Files);
        Assert.DoesNotContain("freelancer.ini", script.Xml);
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
        Assert.Contains("ids_info = GENERATEXMLRES(\"&lt;?xml version=\"1.0\"?&gt;&lt;RDL&gt;&lt;TEXT&gt;x&lt;/TEXT&gt;" +
                        "&lt;/RDL&gt;\")", probe.Xml);
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

    [Fact]
    public void AKeyThatStartsAnotherKeyGetsTheLineEndInItsAnchor()
    {
        using var game = new ExportGame();
        // The anchor of g2 is also the start of the anchor of g22. With its line end, it is unique.
        game.Game.Write(GoodsPath, ExportGame.Goods + "\n[Good]\nnickname = g22\nprice = 1\n");

        var script = Script(game, "[Good]\nnickname = g2\nprice = 5\nvolume = 2\n");

        Assert.Empty(script.Problems);
        Assert.Contains(Op("sectionreplace", "[Good]\r\nnickname = g2\r\n", "price = 200", "price = 5"), script.Xml);
        Assert.Contains(Op("sectionappend", "[Good]\r\nnickname = g2\r\n", null, "volume = 2\r\n"), script.Xml);
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

        Assert.Contains("because OpenLancer finds no unique anchor for [SystemInfo]", Assert.Single(script.Problems).Message);
        // The path has the letter case of the folders of the game.
        Assert.Equal([SystemFile], script.Files.Select(f => f.Path), System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAnchorThatIsUniqueOnlyByCaseFallsBack()
    {
        using var game = new ExportGame();

        var script = SystemScript(game, "[SYSTEMINFO]");

        Assert.Contains("because OpenLancer finds no unique anchor for [SystemInfo]", Assert.Single(script.Problems).Message);
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

        Assert.Contains("because OpenLancer finds no unique anchor for [Good]", Assert.Single(script.Problems).Message);
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
        Assert.Equal("ids_info = GENERATEXMLRES(\"<RDL><TEXT>A &amp; B</TEXT></RDL>\")",
            xml.SelectSingleNode("/script/data/source")!.InnerText.Trim());
        Assert.Equal("Mod m", xml.SelectSingleNode("/script/header/@name")!.Value);
    }

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
