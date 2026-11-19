using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The frozen format 2 test mod (issue #63): the promise that each 1.x editor loads, validates and exports a format 2
/// mod of 1.0. The mod in <c>FrozenMods/format2/mod</c> holds each patch directive, text placeholders, a THN patch, a
/// whole mission file, a whole file in the folder of the mod and the editor files of missions. All data is made up.
/// </summary>
/// <remarks>
/// <para>The mod loads over the fake games of the export tests (<see cref="ExportGame"/>) with the files of
/// <c>FrozenMods/format2/base</c>. The content scan accepts a cutscene file only as a THN patch of a mod. So the test
/// writes the base cutscene and the new cutscene of the mod from the constants of this class.</para>
/// <para>The fixture hash covers the two folders and the two constants. A change of them fails each test with the
/// message <see cref="ChangedFixture"/>, because only the owner can change the promise. <c>packages.txt</c> holds the
/// expected packages. A planned change of the export updates that file in the same commit, with the reason in the
/// commit message.</para>
/// </remarks>
public sealed partial class FrozenFormat2Tests
{
    /// <summary>The SHA-256 of the frozen mod, its base files and the two cutscene constants.</summary>
    private const string FixtureHash = "3e243e611f65bdf80ddaf9014d862e137af950fa2d81fe0ef3058a27670696d3";

    private const string ChangedFixture =
        "The frozen format 2 mod changed. It holds the format 2 promise of issue #63, and only the owner can " +
        "approve a change of it. Restore the files of FrozenMods/format2/mod and FrozenMods/format2/base, and the " +
        "cutscene constants of FrozenFormat2Tests.";

    private const string ModId = "ol-format2";

    private const string BaseCutscenePath = @"DATA\SCRIPTS\OL_FZ\ol_fz_scene.thn";

    private const string NewCutscenePath = @"DATA\MODS\ol-format2\CUTSCENES\ol_fz_new.thn";

    /// <summary>The base cutscene that the THN patch of the mod changes.</summary>
    private const string BaseCutscene = """
        duration = 6
        entities = {
            { entity_name = "Cam_fz", type = CAMERA, cameraprops = { fovh = 35, hvaspect = 1.5 } },
            { entity_name = "Cam_fz_wide", type = CAMERA, cameraprops = { fovh = 50, hvaspect = 1.5 } },
            { entity_name = "Monitor_fz", type = MONITOR }
        }
        events = {
            { 0, SET_CAMERA, { "Monitor_fz", "Cam_fz" } },
            { 2, SET_CAMERA, { "Monitor_fz", "Cam_fz_wide" } }
        }
        """;

    /// <summary>The new cutscene of the mod, a whole file below <c>DATA/MODS/ol-format2/CUTSCENES</c>.</summary>
    private const string NewCutscene = """
        -- The frozen format 2 test: a new cutscene of the mod.
        duration = 4
        entities = {
            { entity_name = "Cam_new", type = CAMERA, cameraprops = { fovh = 40, hvaspect = 1.5 } },
            { entity_name = "Monitor_new", type = MONITOR }
        }
        events = { { 0, SET_CAMERA, { "Monitor_new", "Cam_new" } } }
        """;

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "FrozenMods", "format2");

    /// <summary>The mod loads with its manifest, its patches, its whole files and its text. The editor opens it
    /// with the whole files as editable files, and the editor files of missions read with no problem.</summary>
    [Fact]
    public void TheFrozenModLoads()
    {
        using var game = Game(hd: false, out var mod);
        var root = mod.Folder;

        Assert.Equal(ModId, mod.Id);
        Assert.Equal(2, mod.Manifest.FormatVersion);
        Assert.Equal(1, mod.Manifest.Tier);
        Assert.Equal([ModTargets.HdEdition, ModTargets.Vanilla, ModTargets.OpenLancer], mod.Manifest.Targets);
        Assert.Equal(
            [@"DATA\EQUIPMENT\goods.ini", @"DATA\EQUIPMENT\market_ships.ini", @"DATA\MISSIONS\OL_FZ\ol_fz.ini",
             @"DATA\OL_FZ\ol_fz_items.ini", BaseCutscenePath, @"EXE\freelancer.ini"],
            mod.Patches.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            [@"DATA\MISSIONS\OL_FZ2\ol_fz2.ini", NewCutscenePath, @"DATA\MODS\ol-format2\goods.ini"],
            mod.Files.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Test cargo", mod.Strings.Strings["ol_fz_good_name"]);
        Assert.StartsWith("<RDL>", mod.Strings.Infocards["ol_fz_good_info"]);

        GraphFile.Read(File.ReadAllText(Path.Combine(root, "graphs", "DATA", "MISSIONS", "OL_FZ2", "ol_fz2.ini")),
            out var graphProblems);
        Assert.Empty(graphProblems);
        var prefab = File.ReadAllText(Path.Combine(root, "prefabs", "missions", "ol_fz_gate.ini"));
        Assert.NotNull(PrefabTemplate.Read(prefab, out var prefabProblems));
        Assert.Empty(prefabProblems);

        using var session = EditSession.Open(Profile(game, mod), ModId, DllStringResourceWriter.Instance,
            ThnScripts.Evaluator);
        Assert.Null(session.ReadOnlyReason);
        Assert.Empty(session.OpenProblems);
        foreach (var path in new[] { @"DATA\MISSIONS\OL_FZ2\ol_fz2.ini", @"DATA\MODS\ol-format2\goods.ini" })
        {
            var document = session.OpenDocument(path)!;
            Assert.True(document.IsWholeFile, path);
            Assert.Null(document.ReadOnlyReason);
        }
    }

    /// <summary>
    /// The validation of each retail target finds no problem: the merge of the editor, the checks of the cutscenes that
    /// the mod adds or changes, and the target checks of <c>oltool validate --target</c> (the merge with the retail
    /// rules, the format checks and the content checks).
    /// </summary>
    /// <remarks>The INI reference checks and the engine load of <c>oltool validate</c> need the data of a game. The
    /// retail tests run them.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFrozenModValidatesWithoutProblems(bool hd)
    {
        using var game = Game(hd, out var mod);

        var overlay = ModOverlay.Build(game.Root, [mod], DllStringResourceWriter.Instance, ThnScripts.Evaluator);
        var cutscenes = CutsceneCheck.NewProblems(FileSystem.FromPath(game.Root), overlay.CreateFileSystem(),
            overlay.CutscenePaths, null, null);
        var target = PlayBuild.Run(game.Report(), [mod], DllStringResourceWriter.Instance, ThnScripts.Evaluator);

        Assert.Equal([NewCutscenePath, BaseCutscenePath],
            overlay.CutscenePaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(overlay.Diagnostics.Concat(cutscenes).Concat(target.Problems)
            .Select(p => $"{p.Severity}: {p.Message}"));
    }

    /// <summary>The drop-in zip and the FLMM package of each retail target hold the expected entries. A game data entry
    /// has the expected SHA-256. The texts of the packages are not part of the promise.</summary>
    [Fact]
    public void TheFrozenModExportsTheExpectedPackages()
    {
        var actual = new List<ManifestLine>();
        foreach (var hd in new[] { true, false })
        {
            using var game = Game(hd, out var mod);
            foreach (var form in new[] { ExportForms.DropIn, ExportForms.Flmm })
            {
                var result = ModExport.Run(game.Request(mod) with
                {
                    Forms = form,
                    OutputFolder = Path.Combine(game.Game.Folder, "exports", form.ToString()),
                    StringWriter = DllStringResourceWriter.Instance,
                });
                Assert.True(result.Succeeded, $"{game.Target.Id} {form}:\n{string.Join("\n", result.Problems)}");
                foreach (var package in result.Packages)
                    actual.AddRange(Entries(package.FileName, File.ReadAllBytes(package.Path!)));
            }
        }

        var expected = ReadManifest(Path.Combine(Fixture, "packages.txt"));
        var text = string.Concat(actual.Select(l => l + "\n"));
        Assert.True(expected.Select(l => (l.Package, l.Entry)).SequenceEqual(actual.Select(l => (l.Package, l.Entry))),
            $"The packages hold other entries than packages.txt. The packages now hold:\n{text}");
        foreach (var (want, got) in expected.Zip(actual))
        {
            Assert.True(want.Hash == got.Hash,
                $"The entry {got.Entry} of {got.Package} changed. Its SHA-256 is now {got.Hash}. A planned change " +
                $"of the export updates packages.txt. The packages now hold:\n{text}");
        }
    }

    /// <summary>One line of <c>packages.txt</c>: an entry of a package, and the SHA-256 of a game data entry or
    /// <c>text</c>.</summary>
    private sealed record ManifestLine(string Package, string Entry, string Hash)
    {
        public override string ToString() => $"{Package}\t{Entry}\t{Hash}";
    }

    private static List<ManifestLine> ReadManifest(string path) =>
        [.. File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'))
            .Select(p => new ManifestLine(p[0], p[1], p[2]))];

    /// <summary>The lines of the entries of a package, in the order of the zip. A game data entry (below DATA or EXE)
    /// gets its SHA-256. <c>script.xml</c> of the <c>.flmod</c> gets the SHA-256 of its operations, without its header
    /// element, because the header holds the texts of the package. Each other entry is a text of the package.</summary>
    private static IEnumerable<ManifestLine> Entries(string package, byte[] bytes, string prefix = "")
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var lines = new List<ManifestLine>();
        foreach (var entry in zip.Entries)
        {
            using var stream = new MemoryStream();
            using (var open = entry.Open())
                open.CopyTo(stream);
            var name = prefix + entry.FullName;
            if (entry.FullName.EndsWith(".flmod", StringComparison.OrdinalIgnoreCase))
                lines.AddRange(Entries(package, stream.ToArray(), name + "/"));
            else if (entry.FullName == FlmmScript.FileName)
                lines.Add(new(package, name, Sha256(Encoding.ASCII.GetBytes(Header().Replace(
                    Encoding.ASCII.GetString(stream.ToArray()), "")))));
            else if (entry.FullName.StartsWith("DATA/", StringComparison.OrdinalIgnoreCase) ||
                     entry.FullName.StartsWith("EXE/", StringComparison.OrdinalIgnoreCase))
                lines.Add(new(package, name, Sha256(stream.ToArray())));
            else
                lines.Add(new(package, name, "text"));
        }
        return lines;
    }

    /// <summary>The header element of an FLMM script, with its line end.</summary>
    [GeneratedRegex(@"  <header [^\n]*\n(?:[^\n]*\n)*?  </header>\r?\n")]
    private static partial Regex Header();

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// A fake game of the export tests with the base files of the fixture and a copy of the frozen mod in its mods
    /// folder. The fixture hash must match first.
    /// </summary>
    private static ExportGame Game(bool hd, out Mod mod)
    {
        var hash = Sha256(Encoding.UTF8.GetBytes(FixtureText()));
        Assert.True(FixtureHash == hash, $"{ChangedFixture} The fixture hash is now {hash}.");
        var game = new ExportGame(hd);
        // The engine reads resources.dll and each DLL of [Resources] in the validation. So they must be DLLs.
        foreach (var dll in (hd ? ExportGame.HdDlls.SkipLast(1) : ["InfoCards.dll"]).Append("resources.dll"))
            game.Game.WriteBytes(Path.Combine("EXE", dll), FakeGame.TextDll([]));
        Copy(Path.Combine(Fixture, "base"), game.Root);
        game.Game.WriteBytes(BaseCutscenePath, Encoding.ASCII.GetBytes(BaseCutscene));
        var folder = Path.Combine(game.ModsFolder, ModId);
        Copy(Path.Combine(Fixture, "mod"), folder);
        var cutscene = Path.Combine(folder, Mod.FilesFolder, NewCutscenePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cutscene)!);
        File.WriteAllBytes(cutscene, Encoding.ASCII.GetBytes(NewCutscene));
        mod = Mod.Load(folder);
        return game;
    }

    /// <summary>The text that the fixture hash covers: each file of the mod and base folders with its path and the
    /// SHA-256 of its bytes, then the two cutscene constants.</summary>
    private static string FixtureText()
    {
        var text = new StringBuilder();
        foreach (var part in new[] { "mod", "base" })
        {
            var folder = Path.Combine(Fixture, part);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                         .Select(f => (Name: part + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/'), Path: f))
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
                text.Append($"{file.Name}\t{Sha256(File.ReadAllBytes(file.Path))}\n");
        }
        return text.Append(BaseCutscene).Append('\n').Append(NewCutscene).Append('\n').ToString();
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>A profile with the game as its base and the frozen mod.</summary>
    private static ModProfile Profile(ExportGame game, Mod mod)
    {
        var path = Path.Combine(game.Game.Folder, "profiles", "frozen.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{ "base": {{Json(game.Root)}}, "mods": [ {{Json(mod.Folder)}} ] }""");
        return ModProfile.Load(path);
    }

    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);
}
