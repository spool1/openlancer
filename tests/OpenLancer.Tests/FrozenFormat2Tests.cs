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
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Export;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The frozen format 2 test mod (issue #63): the promise that each 1.x editor loads, validates and exports a format 2
/// mod of 1.0. The mod in <c>FrozenMods/format2/mod</c> holds each patch directive, text placeholders and a THN patch
/// with each kind of entry. It also holds a whole mission file with a prefab instance and a whole file in the folder of
/// the mod. The editor files of missions, a prop group and the publish settings are in it too. All data is made up.
/// </summary>
/// <remarks>
/// <para>The mod loads over the fake games of the export tests (<see cref="ExportGame"/>). They give the code files
/// and the marker DLL of each game. The files of <c>FrozenMods/format2/base</c> replace the data files of both games.
/// <c>base-hd</c> and <c>base-vanilla</c> hold the <c>freelancer.ini</c> of each game. So the expected packages depend
/// only on the fixture. The content scan accepts a cutscene file only as a THN patch of a mod. So the test writes the
/// base cutscene and the new cutscene of the mod from the constants of this class.</para>
/// <para>The fixture hash covers the mod folder, the three base folders and the two constants. A change of them fails
/// each test with the message <see cref="ChangedFixture"/>, because only the owner can change the promise.
/// <c>packages.txt</c> holds the expected packages. A planned change of the export updates that file in the same
/// commit, with the reason in the commit message.</para>
/// </remarks>
public sealed partial class FrozenFormat2Tests
{
    /// <summary>The SHA-256 of the frozen mod, the base files and the two cutscene constants.</summary>
    private const string FixtureHash = "a857fe7326dbeefa2f756de227de85f44e4870c0d9eb03720c1a1984e1fafb42";

    private const string ChangedFixture =
        "The frozen format 2 mod changed. It holds the format 2 promise of issue #63, and only the owner can " +
        "approve a change of it. Restore the files of FrozenMods/format2 (mod, base, base-hd and base-vanilla), and " +
        "the cutscene constants of FrozenFormat2Tests.";

    private const string ModId = "ol-format2";

    private const string BaseCutscenePath = @"DATA\SCRIPTS\OL_FZ\ol_fz_scene.thn";

    private const string NewCutscenePath = @"DATA\MODS\ol-format2\CUTSCENES\ol_fz_new.thn";

    /// <summary>The whole mission script of the mod. It holds a linked instance of the prefab of the mod.</summary>
    private const string MissionPath = @"DATA\MISSIONS\OL_FZ2\ol_fz2.ini";

    /// <summary>The base cutscene that the THN patch of the mod changes. The key of the last two events repeats, so
    /// the patch names them with <c>ol_nth</c> and <c>ol_count</c>.</summary>
    private const string BaseCutscene = """
        duration = 6
        entities = {
            { entity_name = "Cam_fz", type = CAMERA, cameraprops = { fovh = 35, hvaspect = 1.5 } },
            { entity_name = "Cam_fz_wide", type = CAMERA, cameraprops = { fovh = 50, hvaspect = 1.5 },
              spatialprops = { pos = { 0, 5, 20 }, orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } } },
            { entity_name = "Cam_fz_old", type = CAMERA, cameraprops = { fovh = 60, hvaspect = 1.5 } },
            { entity_name = "Monitor_fz", type = MONITOR }
        }
        events = {
            { 0, SET_CAMERA, { "Monitor_fz", "Cam_fz" } },
            { 2, SET_CAMERA, { "Monitor_fz", "Cam_fz_wide" } },
            { 4, SET_CAMERA, { "Monitor_fz", "Cam_fz" } },
            { 4, SET_CAMERA, { "Monitor_fz", "Cam_fz" } }
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

    /// <summary>The mod loads with its manifest, its patches, its whole files and its text. The THN patch reads with
    /// each kind of entry. The editor files of missions, the prop group and the publish settings read with no problem.
    /// The editor opens the mod with the whole files as editable files, and the prefab instance stays linked to its
    /// prefab.</summary>
    [Fact]
    public void TheFrozenModLoads()
    {
        using var game = Game(hd: false, out var mod);
        var root = mod.Folder;

        Assert.Equal(ModId, mod.Id);
        Assert.Equal(2, mod.Manifest.FormatVersion);
        Assert.Equal(1, mod.Manifest.Tier);
        Assert.Equal([ModTargets.HdEdition, ModTargets.Vanilla, ModTargets.OpenLancer], mod.Manifest.Targets);
        Assert.Equal(["ol-format2-before"], mod.Manifest.LoadAfter);
        Assert.Equal(
            [@"DATA\EQUIPMENT\goods.ini", @"DATA\EQUIPMENT\market_ships.ini", @"DATA\MISSIONS\OL_FZ\ol_fz.ini",
             @"DATA\OL_FZ\ol_fz_items.ini", BaseCutscenePath, @"EXE\freelancer.ini"],
            mod.Patches.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            [MissionPath, NewCutscenePath, @"DATA\MODS\ol-format2\goods.ini"],
            mod.Files.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Test cargo", mod.Strings.Strings["ol_fz_good_name"]);
        Assert.StartsWith("<RDL>", mod.Strings.Infocards["ol_fz_good_info"]);

        var thn = ThnPatch.Read(File.ReadAllBytes(Path.Combine(root, Mod.PatchesFolder, BaseCutscenePath)),
            BaseCutscenePath, ThnScripts.Evaluator);
        Assert.Equal(7, thn.Duration);
        Assert.Equal(["set", "remove", "delete", "new"], thn.Entities.Select(EntryKind));
        Assert.Equal(["set", "delete 1 of 2", "set 2 of 2", "new"], thn.Events.Select(EntryKind));

        var prefab = PrefabTemplate.Read(File.ReadAllText(Path.Combine(root, "prefabs", "missions", "ol_fz_gate.ini")),
            out var prefabProblems);
        Assert.Empty(prefabProblems);
        var graph = GraphFile.Read(File.ReadAllText(Path.Combine(root, "graphs", MissionPath)), out var graphProblems);
        Assert.Empty(graphProblems);
        var record = Assert.Single(graph.Instances);
        Assert.Equal(("ol_fz2_gate", "mod:ol_fz_gate"), (record.Id, record.Prefab));
        Assert.Equal(prefab!.ContentHash, record.PrefabHash);
        Assert.Equal([new InstanceMember("spawn", "ol_fz2_gate_spawn"), new InstanceMember("end", "ol_fz2_gate_end")],
            record.Members);
        Assert.Equal([new InstanceValue("formation", ["ol_fz2_form"])], record.Values);
        Assert.Equal([new InstanceValue("delay", ["7"])], record.Overrides);
        Assert.Equal([new InstancePort("done", "ol_fz2_end")], record.Ports);
        Assert.NotNull(record.LinesHash);

        var props = PropGroupFile.Read(Path.Combine(root, "props", "ol_fz_pair.ini"), null, editable: true);
        Assert.Empty(props.Problems);
        Assert.Equal(["buoy_1", "buoy_2", "zone_buoys"], props.Members.Select(m => m.Member));
        Assert.Equal(["Test buoy"], props.Texts.Values);
        var publish = PublishSettings.Load(root);
        Assert.Empty(publish.Problems);
        Assert.Null(publish.ReadOnly);
        Assert.Equal(["OpenLancer tests"], publish.Settings.Authors);

        using var session = EditSession.Open(Profile(game, mod), ModId, DllStringResourceWriter.Instance,
            ThnScripts.Evaluator);
        Assert.Null(session.ReadOnlyReason);
        Assert.Empty(session.OpenProblems);
        foreach (var path in new[] { MissionPath, @"DATA\MODS\ol-format2\goods.ini" })
        {
            var document = session.OpenDocument(path)!;
            Assert.True(document.IsWholeFile, path);
            Assert.Null(document.ReadOnlyReason);
        }
        var mission = session.Missions.Model(MissionPath);
        var linked = Assert.Single(mission.Prefabs!.Instances, i => i.Linked != null).Linked!;
        Assert.Equal(record, linked.Record);
        Assert.False(linked.Missing || linked.HandEdited || linked.Outdated);
        Assert.DoesNotContain(mission.Problems, p => PrefabLinks.Rules.Contains(p.Rule));
    }

    /// <summary>The kind of a THN patch entry, with its place for an entry with <c>ol_nth</c>.</summary>
    private static string EntryKind(ThnPatchEntry entry)
    {
        var kind = entry.New != null ? "new" : entry.Delete ? "delete" : !entry.Removes.IsEmpty ? "remove" : "set";
        return entry.Nth is { } nth ? $"{kind} {nth} of {entry.Count}" : kind;
    }

    /// <summary>
    /// The validation of each retail target finds no problem. It has three parts. The first part is the merge of the
    /// editor. The second part is the checks of the cutscenes that the mod adds or changes. The third part is the
    /// target checks of <c>oltool validate --target</c>: the merge with the retail rules, the format checks and the
    /// content checks.
    /// </summary>
    /// <remarks>The INI reference checks and the engine load of <c>oltool validate</c> need the data of a full game.
    /// So this test does not run them, and no other test runs them on the frozen mod.</remarks>
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
    /// has the expected SHA-256. The export reads the publish settings, as the editor and <c>oltool export</c> do. The
    /// texts of the packages are not part of the promise.</summary>
    [Fact]
    public void TheFrozenModExportsTheExpectedPackages()
    {
        var actual = new List<ManifestLine>();
        foreach (var hd in new[] { true, false })
        {
            using var game = Game(hd, out var mod);
            foreach (var form in new[] { ExportForms.DropIn, ExportForms.Flmm })
            {
                var result = ModExport.Run(ExportRequest.Of(mod, [game.Target], [game.Report()],
                    Path.Combine(game.Game.Folder, "exports", form.ToString()), form, DllStringResourceWriter.Instance,
                    ThnScripts.Evaluator, out var publishProblems));
                Assert.Empty(publishProblems);
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
    /// gets its SHA-256. <c>script.xml</c> of the <c>.flmod</c> gets the SHA-256 of <see cref="ScriptData"/>. Each
    /// other entry is a text of the package.</summary>
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
                lines.Add(new(package, name,
                    Sha256(Encoding.ASCII.GetBytes(ScriptData(Encoding.ASCII.GetString(stream.ToArray()))))));
            else if (entry.FullName.StartsWith("DATA/", StringComparison.OrdinalIgnoreCase) ||
                     entry.FullName.StartsWith("EXE/", StringComparison.OrdinalIgnoreCase))
                lines.Add(new(package, name, Sha256(stream.ToArray())));
            else
                lines.Add(new(package, name, "text"));
        }
        return lines;
    }

    /// <summary>The data of an FLMM script: the script with its scriptversion element in place of its header element.
    /// FLMM reads the script version as data. The other elements of the header are the texts of the package.</summary>
    private static string ScriptData(string xml) => Header().Replace(xml, m => ScriptVersion().Match(m.Value).Value);

    /// <summary>The header element of an FLMM script, from the line of its tag to the line of its tag end, with the
    /// line end.</summary>
    [GeneratedRegex(@"(?m)^<header [^\n]*\n(?:[^\n]*\n)*?</header>\r?\n")]
    private static partial Regex Header();

    /// <summary>The scriptversion element of the header of an FLMM script: its tag, its value and its tag end, each on
    /// its own line, with the line ends.</summary>
    [GeneratedRegex(@"(?m)^<scriptversion>\r?\n[^<\n]*\n</scriptversion>\r?\n")]
    private static partial Regex ScriptVersion();

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
        Copy(Path.Combine(Fixture, "base"), game.Root);
        Copy(Path.Combine(Fixture, hd ? "base-hd" : "base-vanilla"), game.Root);
        // The engine reads resources.dll and each DLL of [Resources] in the validation. So they must be DLLs. A DLL
        // that the fake game has stays, for example the marker DLL of the HD Edition.
        foreach (var dll in ResourceDlls(game).Where(d => !File.Exists(game.Game.PathOf(Path.Combine("EXE", d))))
                     .Append("resources.dll"))
            game.Game.WriteBytes(Path.Combine("EXE", dll), FakeGame.TextDll([]));
        game.Game.WriteBytes(BaseCutscenePath, Encoding.ASCII.GetBytes(BaseCutscene));
        var folder = Path.Combine(game.ModsFolder, ModId);
        Copy(Path.Combine(Fixture, "mod"), folder);
        var cutscene = Path.Combine(folder, Mod.FilesFolder, NewCutscenePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cutscene)!);
        File.WriteAllBytes(cutscene, Encoding.ASCII.GetBytes(NewCutscene));
        mod = Mod.Load(folder);
        return game;
    }

    /// <summary>The DLLs of <c>[Resources]</c> in <c>freelancer.ini</c> of a fake game.</summary>
    private static IEnumerable<string> ResourceDlls(ExportGame game) =>
        ExportGame.Parse(File.ReadAllText(game.Game.PathOf(@"EXE\freelancer.ini")))
            .Sections.Where(s => s.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s).Where(e => e.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase))
            .Select(e => e[0].ToString()!);

    /// <summary>The text that the fixture hash covers. It has a line for each file of the mod folder and the base
    /// folders, with the path and the SHA-256 of the file. The two cutscene constants follow.</summary>
    private static string FixtureText()
    {
        var text = new StringBuilder();
        foreach (var part in new[] { "mod", "base", "base-hd", "base-vanilla" })
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
