using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The directive <c>ol.append</c>: a patch of <c>EXE\freelancer.ini</c> adds <c>[Data]</c> lines for the new data files
/// of a mod (section 7.5 of the M11 design). The game reads the <c>[Data]</c> lines in order, and the order is the load
/// order.
/// </summary>
public sealed class DataAppendTests
{
    private const string FreelancerIni = """
        [Freelancer]
        data path = ..\data

        [Data]
        solar = solar\solararch.ini
        universe = universe\universe.ini
        goods = equipment\goods.ini

        [Resources]
        DLL = InfoCards.dll
        """;

    private static TestWorkspace Workspace()
    {
        var ws = new TestWorkspace();
        ws.WriteBase(ModOverlay.FreelancerIniPath, FreelancerIni);
        ws.WriteBase(@"DATA\EQUIPMENT\goods.ini", "[Good]\nnickname = g1\nprice = 1\n");
        return ws;
    }

    /// <summary>A mod of format version 2 with a patch of freelancer.ini and its new data files.</summary>
    private static Mod AppendMod(TestWorkspace ws, string patch, int formatVersion = 2, string id = "act",
        params string[] newFiles)
    {
        var folder = ws.ModFolder(id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ModManifest.FileName),
            $$"""{ "id": "{{id}}", "formatVersion": {{formatVersion}} }""");
        var patchFile = Path.Combine(folder, Mod.PatchesFolder, "EXE", "freelancer.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(patchFile)!);
        File.WriteAllText(patchFile, patch);
        foreach (var file in newFiles)
        {
            var path = Path.Combine(folder, Mod.FilesFolder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "[Good]\nnickname = new_good\nprice = 5\n");
        }
        return Mod.Load(folder);
    }

    private static List<string> Data(ModOverlay overlay) =>
        TestWorkspace.Sections(overlay, ModOverlay.FreelancerIniPath).Single(s => s.Name == "Data")
            .Select(e => $"{e.Name} = {e[0]}").ToList();

    [Fact]
    public void ALineGoesAfterTheLastLineOfItsKey()
    {
        using var ws = Workspace();
        var mod = AppendMod(ws, "[Data]\nol.append = true\nsolar = MODS\\act\\solar.ini\ngoods = MODS\\act\\goods.ini\n",
            newFiles: [@"DATA\MODS\act\solar.ini", @"DATA\MODS\act\goods.ini"]);

        var overlay = ws.Build(mod);

        Assert.Empty(overlay.Diagnostics);
        // A new solar line stays before universe, as the load order needs.
        Assert.Equal(["solar = solar\\solararch.ini", "solar = MODS\\act\\solar.ini", "universe = universe\\universe.ini",
            "goods = equipment\\goods.ini", "goods = MODS\\act\\goods.ini"], Data(overlay));
    }

    [Fact]
    public void ALineWithANewKeyGoesAtTheEnd()
    {
        using var ws = Workspace();
        var mod = AppendMod(ws, "[Data]\nol.append = true\nequipment = MODS\\act\\equip.ini\n",
            newFiles: [@"DATA\MODS\act\equip.ini"]);

        var overlay = ws.Build(mod);

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal("equipment = MODS\\act\\equip.ini", Data(overlay)[^1]);
    }

    [Fact]
    public void AnExistingPathIsAnError()
    {
        using var ws = Workspace();
        var mod = AppendMod(ws, "[Data]\nol.append = true\ngoods = EQUIPMENT/GOODS.ini\n");

        var overlay = ws.Build(mod);

        var error = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("[Data] goods = EQUIPMENT/GOODS.ini: [Data] already has this line. Remove it from the patch.",
            error.Message);
    }

    [Fact]
    public void APathWithoutAFileIsAnError()
    {
        using var ws = Workspace();
        var mod = AppendMod(ws, "[Data]\nol.append = true\ngoods = MODS\\act\\missing.ini\n");

        var overlay = ws.Build(mod);

        var error = Assert.Single(overlay.Diagnostics);
        Assert.Equal("[Data] goods = MODS\\act\\missing.ini: the data folder of the game and the mods has no such " +
                     "file. Add the file to the files folder of a mod.", error.Message);
        Assert.DoesNotContain("goods = MODS\\act\\missing.ini", Data(overlay));
    }

    [Fact]
    public void AnotherSectionInAFreelancerIniPatchIsAnError()
    {
        using var ws = Workspace();
        var mod = AppendMod(ws, "[Data]\nol.append = true\ngoods = x.ini\n\n[Resources]\nDLL = mine.dll\n");
        var noAppend = AppendMod(ws, "[Data]\ngoods = y.ini\n", id: "other");

        var overlay = ws.Build(mod);
        var withoutDirective = ws.Build(noAppend);

        var message = @"A patch of EXE\freelancer.ini can only add [Data] lines, in [Data] sections with ol.append = " +
                      "true. For other changes, use files/EXE/freelancer.ini.";
        Assert.Equal(message, Assert.Single(overlay.Diagnostics).Message);
        Assert.Equal(message, Assert.Single(withoutDirective.Diagnostics).Message);
    }

    [Fact]
    public void AFormatOneModKeepsTheOldMessage()
    {
        using var ws = Workspace();
        var mod = AppendMod(ws, "[Data]\nol.append = true\ngoods = MODS\\act\\goods.ini\n", formatVersion: 1,
            newFiles: [@"DATA\MODS\act\goods.ini"]);

        var overlay = ws.Build(mod);

        Assert.Equal(@"EXE\freelancer.ini cannot be patched in format v1. Use files/EXE/freelancer.ini.",
            Assert.Single(overlay.Diagnostics).Message);
    }

    [Fact]
    public void TheDirectiveInAnotherFileIsAnError()
    {
        using var ws = Workspace();
        var mod = ws.Mod("m", new()
        {
            [@"patches\DATA\EQUIPMENT\goods.ini"] = "[Good]\nnickname = g1\nol.append = true\nprice = 2\n",
        });

        var overlay = ws.Build(mod);

        Assert.Equal(@"[Good]: ol.append is only for a [Data] section of a patch of EXE\freelancer.ini, and for a " +
                     "section with item lists: [locked_gates].", Assert.Single(overlay.Diagnostics).Message);
    }

    [Fact]
    public void TheEditorLoadsAModWithAppendLines()
    {
        using var ws = Workspace();
        AppendMod(ws, "[Data]\nol.append = true\ngoods = MODS\\act\\goods.ini\n", newFiles: [@"DATA\MODS\act\goods.ini"]);
        ws.WriteProfile("act");

        var session = ws.Session("act");

        Assert.Null(session.ReadOnlyReason);
        Assert.Empty(session.EditView.Diagnostics);
        var fs = session.EditView.CreateFileSystem();
        Assert.Contains(fs.ReadAllText(ModOverlay.FreelancerIniPath).Split("\r\n"), line => line == @"goods = MODS\act\goods.ini");
        Assert.Contains("new_good", fs.ReadAllText(@"DATA\MODS\act\goods.ini"));
        // The goods of the base stay editable.
        var good = new SectionRef(@"DATA\EQUIPMENT\goods.ini", new SectionIdentity("Good", "g1"));
        Assert.Equal(["1"], EditSessionTests.Values(session, good, "price"));
    }
}
