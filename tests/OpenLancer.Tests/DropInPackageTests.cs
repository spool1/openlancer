using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OpenLancer.Data.Export;
using OpenLancer.Data.Overlay;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The drop-in zip of a retail target (section 7.10 of the M11 design).</summary>
public sealed class DropInPackageTests
{
    private const string TextPatch = "[Good]\nnickname = lf_hull\nprice = 5600\n\n[Good]\nol.new = true\nnickname = ol_hull\n" +
                                     "ids_name = $str:name\n";

    private static (ExportGame Game, Package Package) Export(bool hd = false, string patch = TextPatch, bool text = true)
    {
        var game = new ExportGame(hd);
        var files = new System.Collections.Generic.Dictionary<string, string> { [@"patches\" + GoodsPath] = patch };
        if (text)
            files["strings.json"] = """{ "strings": { "name": "Name" } }""";
        var mod = game.Mod("ol-test", files);
        return (game, DropInPackage.Write(game.Build(mod), mod.Manifest, game.Target));
    }

    private static ZipArchive Open(Package package) => new(new MemoryStream(package.Bytes!));

    private static string Entry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheZipHasDataAndExeAtItsTop()
    {
        var (game, package) = Export();
        using (game)
        {
            Assert.Equal("ol-test-1.2.0-vanilla-1.1.zip", package.FileName);
            using var zip = Open(package);
            Assert.Equal(["DATA/EQUIPMENT/goods.ini", "EXE/freelancer.ini", "EXE/ol_ol-test.dll", "ol-test README.txt"],
                zip.Entries.Select(e => e.FullName));
        }
    }

    [Fact]
    public void TheEntryNamesUseSlashes()
    {
        var (game, package) = Export();
        using (game)
        {
            using var zip = Open(package);
            Assert.All(zip.Entries, e => Assert.DoesNotContain('\\', e.FullName));
            Assert.All(zip.Entries, e => Assert.Equal(new DateTime(1980, 1, 1), e.LastWriteTime.DateTime));
        }
    }

    [Fact]
    public void TwoExportsGiveTheSameBytes()
    {
        var (first, one) = Export();
        var (second, two) = Export();
        using (first)
        using (second)
        {
            Assert.Equal(one.Bytes, two.Bytes);
            // Each local header of the zip has no extra field (the length at offset 28 is 0).
            var bytes = one.Bytes!;
            for (var i = 0; i + 30 <= bytes.Length; i++)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 3 && bytes[i + 3] == 4)
                    Assert.Equal(0, BitConverter.ToUInt16(bytes, i + 28));
            }
        }
    }

    [Fact]
    public void AnUnchangedFileIsNotInTheZip()
    {
        var (game, package) = Export(patch: "[Good]\nnickname = lf_hull\nprice = 5600\n", text: false);
        using (game)
        {
            using var zip = Open(package);
            // No text, so no DLL and no freelancer.ini. The market file does not change.
            Assert.Equal(["DATA/EQUIPMENT/goods.ini", "ol-test README.txt"], zip.Entries.Select(e => e.FullName));
        }
    }

    [Fact]
    public void TheMergedFileKeepsTheCommentsOfTheGame()
    {
        var (game, package) = Export();
        using (game)
        {
            using var zip = Open(package);
            var goods = Entry(zip, "DATA/EQUIPMENT/goods.ini");
            Assert.StartsWith("; The goods of the test.\r\n[Good]\r\nnickname = lf_hull\r\nprice = 5600 ; the Patriot hull\r\n",
                goods);
            Assert.EndsWith("\r\n[Good]\r\nnickname = ol_hull\r\nids_name = 131073\r\n", goods);
        }
    }

    [Fact]
    public void TheReadmeListsEachFile()
    {
        var (game, package) = Export();
        using (game)
        {
            using var zip = Open(package);
            var readme = Entry(zip, "ol-test README.txt");
            Assert.Contains("- DATA\\EQUIPMENT\\goods.ini\r\n- EXE\\freelancer.ini\r\n- EXE\\ol_ol-test.dll\r\n", readme);
            Assert.Contains("1. Copy the files of the list from your game folder to a backup folder.", readme);
            Assert.Contains("This zip replaces whole files. It does not combine with other mods that change the same files.",
                readme);
            Assert.Contains("Freelancer © Microsoft Corporation. Mod ol-test was created under Microsoft's \"Game Content " +
                            "Usage Rules\"", readme);
            Assert.Contains(PackageTexts.RulesLink, readme);
            Assert.DoesNotContain("Donation", readme);
        }
    }

    [Fact]
    public void TheReadmeGivesTheFreelancerIniLines()
    {
        var (game, package) = Export(hd: true);
        using (game)
        {
            using var zip = Open(package);
            var readme = Entry(zip, "ol-test README.txt");
            Assert.Contains("- In [Resources], add \"DLL = ol_ol-test.dll\" as the 8th DLL line.", readme);
            Assert.Contains("keeps the options that the author picked in the HD Edition installer", readme);
            Assert.Contains("Two OpenLancer mods with text do not work together as drop-in zips", readme);
            Assert.Contains("Freelancer: HD Edition 0.7.1 (Nexus mod 29).", readme);
            Assert.Contains("DLL = Controls.dll\r\nDLL = ol_ol-test.dll\r\n", Entry(zip, "EXE/freelancer.ini"));
        }
    }

    [Fact]
    public void TheFileNamesHaveTheCaseOfTheBase()
    {
        using var game = new ExportGame();
        game.Game.Write(@"DATA\Solar\SolarArch.ini", "[Solar]\nnickname = s1\nmass = 1\n");
        var mod = game.Mod("m", new()
        {
            [@"patches\data\solar\solararch.ini"] = "[Solar]\nnickname = s1\nmass = 2\n",
            [@"files\data\solar\new\x.txt"] = "x",
        });

        var package = DropInPackage.Write(game.Build(mod), mod.Manifest, game.Target);

        using var zip = Open(package);
        Assert.Equal(["DATA/Solar/SolarArch.ini", "DATA/Solar/new/x.txt", "m README.txt"],
            zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void AWholeFreelancerIniIsRefused()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new() { [@"files\" + ModOverlay.FreelancerIniPath] = "[Freelancer]\ndata path = ..\\data\n" });

        var result = ModExport.Run(game.Request(mod) with { Forms = ExportForms.DropIn });

        var package = Assert.Single(result.Packages);
        Assert.Null(package.Path);
        Assert.Contains(result.Problems, p => p.Message.StartsWith(@"A whole EXE\freelancer.ini replaces the lines of the game."));
        Assert.False(Directory.Exists(result.OutputFolder));
    }

    /// <summary>The zip holds the generated route tables in the folder of the game, and the README lists them.</summary>
    [Fact]
    public void TheZipHoldsTheGeneratedRouteTables()
    {
        using var game = new RouteWorld().System("A", "gate B").System("B", "gate A").WriteInto(new ExportGame());
        var mod = RouteWorld.ConnectionMod(game);
        var files = RouteWorld.Build(game, mod);

        var package = DropInPackage.Write(files, mod.Manifest, game.Target);

        using var zip = Open(package);
        // The fake game has the folder DATA\universe, so the entries have that letter case.
        var all = zip.GetEntry("DATA/universe/systems_shortest_path.ini");
        Assert.True(all != null, string.Join(", ", zip.Entries.Select(e => e.FullName)));
        Assert.Equal(files.File(@"DATA\UNIVERSE\systems_shortest_path.ini")!.Bytes, ReadBytes(all!));
        Assert.NotNull(zip.GetEntry("DATA/universe/shortest_legal_path.ini"));
        Assert.NotNull(zip.GetEntry("DATA/universe/shortest_illegal_path.ini"));
        Assert.Contains(@"- DATA\universe\shortest_legal_path.ini", Entry(zip, "conn README.txt"));
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
