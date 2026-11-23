using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Dump;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Phase 2 proofs: the sample mod changes vanilla data through the overlay, the validator finds broken mods, and
/// the base data never changes.
/// </summary>
public sealed class SampleModTests(ITestOutputHelper output)
{
    private const string SampleId = "ol-sample";

    static SampleModTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheEngineSeesTheSampleMod()
    {
        var overlay = BuildSample(GameDataPaths.RequireBase());

        var db = EngineDataLoader.Load(overlay.CreateFileSystem()).RequireComplete();

        AssertSampleIsResolved(db);
    }

    [Fact]
    public void ANewStoryCharacterCanBuyTheSampleShipAtManhattan()
    {
        var fs = BuildSample(GameDataPaths.RequireBase()).CreateFileSystem();
        var db = EngineDataLoader.Load(fs).RequireComplete();

        RetailChecks.AssertNewPlayerCanBuy(db, fs, "ol_starrunner_package", output);
    }

    [Fact]
    public void TheSampleModAddsNoValidationErrors()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var overlay = BuildSample(baseFolder);

        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(baseFolder), overlay.CreateFileSystem(),
            overlay.CutscenePaths);

        Print(problems);
        Assert.DoesNotContain(problems, p => p.Severity == DiagnosticSeverity.Error);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void TheDeployedFolderShowsTheSameDataAsTheOverlay()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var profile = TestProfile(baseFolder, "sample", GameDataPaths.SampleMod(SampleId));
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);

        var result = ProfileDeployer.Deploy(overlay, profile);
        output.WriteLine(result.ToString());

        var fromOverlay = DataDump.Create(overlay.CreateFileSystem());
        var deployed = FileSystem.FromPath(result.Folder);
        Assert.Equal(fromOverlay, DataDump.Create(deployed));
        // The dump holds text ids only, so check the text and the generated DLL as well.
        AssertSampleIsResolved(EngineDataLoader.Load(deployed).RequireComplete());
        Assert.Equal(overlay.Files.Single(f => f.Path == ModOverlay.StringsDllPath).ReadAllBytes(),
            File.ReadAllBytes(Path.Combine(result.Folder, "EXE", ModOverlay.StringsDllName)));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void AProfileWithoutModsDeploysAnExactCopyOfTheBase()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var profile = TestProfile(baseFolder, "empty");
        var overlay = ModOverlay.Build(profile.BaseFolder, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);

        var result = ProfileDeployer.Deploy(overlay, profile);

        Assert.Equal((DataDigest.RetailV1FileCount, DataDigest.RetailV1),
            DataDigest.Compute(result.Folder, [ProfileDeployer.MarkerFileName]));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    [Fact]
    public void TheValidatorFindsBrokenReferences()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var problems = Validate(baseFolder, "broken", new()
        {
            [@"patches\DATA\EQUIPMENT\goods.ini"] = """
                [Good]
                ol.new = true
                nickname = broken_no_hull
                category = ship
                hull = no_such_hull

                [Good]
                ol.new = true
                nickname = broken_package
                category = ship
                hull = le_hull
                addon = ge_le_engine_01, internal, 1
                addon = no_such_gun, HpWeapon01, 1
                """,
            [@"patches\DATA\EQUIPMENT\market_ships.ini"] = """
                [BaseGood]
                base = no_such_base
                marketgood = le_package, 0, -1, 1, 1, 0, 1, 1

                [BaseGood]
                base = li01_01_base
                marketgood = LE_PACKAGE, 0, -1, 1, 1, 0, 1, 1
                marketgood = no_such_good, 0, -1, 1, 1, 0, 1, 1
                marketgood = le_hull, 0, -1, 1, 1, 0, 1, 1
                """,
        });

        string[] expected =
        [
            "Ship package 'broken_no_hull': hull 'no_such_hull' is not a shiphull good. The engine load stops.",
            "Ship package 'broken_package': addon 'no_such_gun' is not equipment. The engine drops it.",
            "Ship package 'broken_package': the addons have no power plant.",
            "Market for base 'no_such_base': the base is not in universe.ini.",
            "Market for base 'li01_01_base', good 'LE_PACKAGE': the letter case differs from the ship package. The engine will not find it.",
            "Market for base 'li01_01_base', good 'no_such_good': the good does not exist.",
        ];
        foreach (var message in expected)
            Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error && p.Message == message);
        // A line of a hull good sets the price of its ship at the base (fork commit OL-0080).
        Assert.DoesNotContain(problems, p => p.Message.Contains("good 'le_hull'"));
    }

    [Fact]
    public void TheValidatorReportsNewEngineWarnings()
    {
        var problems = Validate(GameDataPaths.RequireBase(), "typo", new()
        {
            [@"patches\DATA\SHIPS\shiparch.ini"] = "[Ship]\nnickname = li_elite\nhit_ptz = 10",
        });

        Assert.Contains(problems, p => p.Source == EngineDataLoader.EngineSource && p.Message.Contains("Unknown entry hit_ptz"));
    }

    [Fact]
    public void TheValidatorFindsAHardpointThatTheModelDoesNotHave()
    {
        var problems = Validate(GameDataPaths.RequireBase(), "bad-hardpoint", new()
        {
            [@"patches\DATA\EQUIPMENT\goods.ini"] = """
                [Good]
                ol.new = true
                nickname = test_le_package
                category = ship
                hull = le_hull
                addon = ge_le_engine_01, internal, 1
                addon = li_elite_power01, internal, 1
                addon = li_gun01_mark01, HpWeapon09, 1
                """,
        });

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error && p.Message.StartsWith(
            "Ship package 'test_le_package': addon 'li_gun01_mark01' uses hardpoint 'HpWeapon09', which model"));
    }

    [Fact]
    public void TheValidatorFindsAShieldThatTheServerRejects()
    {
        var problems = Validate(GameDataPaths.RequireBase(), "bad-mount", new()
        {
            [@"patches\DATA\EQUIPMENT\goods.ini"] = """
                [Good]
                ol.new = true
                nickname = test_lf_package
                category = ship
                hull = lf_hull
                addon = ge_lf_engine_01, internal, 1
                addon = li_fighter_power01, internal, 1
                addon = shield01_mark02_hf, HpShield01, 1
                """,
        });

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error && p.Message.StartsWith(
            "Ship package 'test_lf_package': addon 'shield01_mark02_hf'") && p.Message.EndsWith("The server rejects the purchase."));
    }

    [Fact]
    public void TheValidatorShowsTheParseErrorBehindAReferenceError()
    {
        var problems = Validate(GameDataPaths.RequireBase(), "short-addon", new()
        {
            [@"patches\DATA\EQUIPMENT\goods.ini"] = """
                [Good]
                ol.new = true
                nickname = test_short_package
                category = ship
                hull = le_hull
                addon = ge_le_engine_01
                addon = li_elite_power01, internal, 1
                """,
        });

        Assert.Contains(problems, p => p.Message == "Ship package 'test_short_package': the addons have no engine.");
        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error && p.Message.Contains("Not enough components for addon"));
    }

    [Fact]
    public void TheValidatorFindsAGunThatTheServerRejectsOnInternal()
    {
        var problems = Validate(GameDataPaths.RequireBase(), "internal-gun", new()
        {
            [@"patches\DATA\EQUIPMENT\goods.ini"] = """
                [Good]
                ol.new = true
                nickname = test_internal_gun_package
                category = ship
                hull = le_hull
                addon = ge_le_engine_01, internal, 1
                addon = li_elite_power01, internal, 1
                addon = li_gun01_mark01, internal, 1
                """,
        });

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error &&
                                       p.Message.StartsWith("Ship package 'test_internal_gun_package': addon 'li_gun01_mark01'") &&
                                       p.Message.Contains("'internal'"));
    }

    [Fact]
    public void TheValidatorReportsAModelThatDoesNotLoad()
    {
        var problems = Validate(GameDataPaths.RequireBase(), "bad-model", new()
        {
            [@"files\DATA\MODS\bad-model\broken.cmp"] = "this is not a UTF file",
            [@"patches\DATA\SHIPS\shiparch.ini"] = """
                [Ship]
                ol.new = true
                nickname = test_broken_ship
                type = FIGHTER
                DA_archetype = MODS\bad-model\broken.cmp
                hp_type = hp_gun_special_1, HpWeapon01
                """,
            [@"patches\DATA\EQUIPMENT\goods.ini"] = """
                [Good]
                ol.new = true
                nickname = test_broken_hull
                category = shiphull
                ship = test_broken_ship
                price = 100

                [Good]
                ol.new = true
                nickname = test_broken_package
                category = ship
                hull = test_broken_hull
                addon = ge_le_engine_01, internal, 1
                addon = li_elite_power01, internal, 1
                """,
        });

        Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error &&
                                       p.Message.StartsWith("Ship package 'test_broken_package': model") &&
                                       p.Message.Contains("did not load"));
    }

    private IReadOnlyList<Diagnostic> Validate(string baseFolder, string modId, Dictionary<string, string> files)
    {
        var modFolder = Path.Combine(GameDataPaths.OutFolder(baseFolder), "test-mods", modId);
        if (Directory.Exists(modFolder))
            Directory.Delete(modFolder, true);
        WriteMod(modFolder, modId, files);
        var overlay = ModOverlay.Build(baseFolder, [Mod.Load(modFolder)], DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        Assert.False(overlay.HasErrors, string.Join("\n", overlay.Diagnostics));

        var problems = ProfileValidator.NewProblems(FileSystem.FromPath(baseFolder), overlay.CreateFileSystem(),
            overlay.CutscenePaths);
        Print(problems);
        return problems;
    }

    private static void AssertSampleIsResolved(GameItemDb db)
    {
        Assert.True(db.Ships.TryGetValue("ol_starrunner", out var ship));
        Assert.Equal(5600, db.GetShipPrice(db.Ships.Get("li_fighter")!));
        var manhattan = db.Bases.Get(RetailChecks.Manhattan)!;
        Assert.Contains(manhattan.SoldShips, s => s.Package.Nickname == "ol_starrunner_package" && s.Package.Ship == ship);

        var text = new InfocardManager(db.Ini.Freelancer.Resources);
        Assert.Equal("Starrunner", text.GetStringResource(ship!.IdsName));
        Assert.Contains("light fighter from the OpenLancer sample mod", text.GetXmlResource(ship.IdsInfo));
    }

    /// <summary>The sample changes the cityscape of Manhattan, where a new game starts, and two late story scenes. So
    /// each check of the first real-game test of milestone M11 is at Manhattan.</summary>
    [Fact]
    public void TheSampleChangesTheManhattanCityscape()
    {
        var overlay = BuildSample(GameDataPaths.RequireBase());

        Assert.Equal(
            [
                @"DATA\Scripts\Bases\li_01_cityscape_hardpoint_01.thn",
                @"DATA\Scripts\Story\s052x_nrml_Akira_bw_02_Cityscape_01.thn",
                @"DATA\Scripts\Story\s062x_nrml_vonclaussn_pl_06_Pad2_01.thn",
            ],
            overlay.CutscenePaths.Order(System.StringComparer.OrdinalIgnoreCase), VfsPath.Comparer);
        var text = System.Text.Encoding.ASCII.GetString(overlay.CreateFileSystem()
            .ReadAllBytes(@"DATA\SCRIPTS\BASES\li_01_cityscape_hardpoint_01.thn"));
        Assert.Contains("pos = { 59.658787, 86.31464, 97.57275 }", text);
    }

    private ModOverlay BuildSample(string baseFolder)
    {
        var overlay = ModOverlay.Build(baseFolder, [Mod.Load(GameDataPaths.SampleMod(SampleId))],
            DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        Print(overlay.Diagnostics);
        Assert.False(overlay.HasErrors);
        return overlay;
    }

    private void Print(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
            output.WriteLine(d.ToString());
    }

    /// <summary>A profile file in the local test output folder, with absolute paths.</summary>
    private static ModProfile TestProfile(string baseFolder, string name, params string[] mods)
    {
        var folder = Path.Combine(GameDataPaths.OutFolder(baseFolder), "test-profiles");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { @base = baseFolder, mods }));
        return ModProfile.Load(path);
    }

    private static void WriteMod(string folder, string id, Dictionary<string, string> files)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ModManifest.FileName), $$"""{ "id": "{{id}}", "formatVersion": 1 }""");
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }
}
