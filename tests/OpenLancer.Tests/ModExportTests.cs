using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Publishing;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The export run (sections 7.13 to 7.15 of the M11 design): the packages, the texts and the folder rules.</summary>
public sealed class ModExportTests
{
    private const string Patch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    [Fact]
    public void TheExportWritesEachPackageAndTheTexts()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch, "ol-test");

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer));

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(["ol-test-1.2.0-vanilla-1.1.zip", "ol-test-1.2.0-vanilla-1.1-flmm.zip", "ol-test-1.2.0-openlancer.zip"],
            result.Packages.Select(p => p.FileName));
        Assert.All(result.Packages, p => Assert.True(File.Exists(p.Path)));
        var nexus = File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile));
        Assert.Contains("ol-test-1.2.0-vanilla-1.1.zip: Drop-in zip for Freelancer 1.1.", nexus);
        Assert.Contains("Microsoft's \"Game Content Usage Rules\"", nexus);
        var checklist = File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.ChecklistFile));
        Assert.Contains("Target: Freelancer 1.1\r\nDATA\\EQUIPMENT\\goods.ini\r\n- Good 'lf_hull': price 6600 -> 5600.", checklist);
    }

    [Fact]
    public void TheOutputFolderMustNotOverlapTheGameOrTheMod()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var before = FakeGame.Snapshot(game.Root);

        var inGame = ModExport.Run(game.Request(mod) with { OutputFolder = Path.Combine(game.Root, "exports") });
        var inMod = ModExport.Run(game.Request(mod) with { OutputFolder = Path.Combine(mod.Folder, "exports") });

        Assert.StartsWith("The export folder", Assert.Single(inGame.Problems).Message);
        Assert.Contains("OpenLancer does not write into the game folder", inGame.Problems[0].Message);
        Assert.Contains("overlaps the mod folder", Assert.Single(inMod.Problems).Message);
        Assert.Equal(before, FakeGame.Snapshot(game.Root));
        Assert.False(Directory.Exists(Path.Combine(mod.Folder, "exports")));
    }

    /// <summary>A game that is not a base of the export, for example the base of the profile or another install, is a
    /// game folder too.</summary>
    [Fact]
    public void TheOutputFolderMustNotBeInAnotherGameFolder()
    {
        using var game = new ExportGame();
        using var other = new FakeGame();
        var mod = game.GoodsMod(Patch);
        var before = FakeGame.Snapshot(other.Root);

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with
        {
            OutputFolder = Path.Combine(other.Root, "DATA", "exports"),
        });

        Assert.Contains($"is in the game folder {other.Root}. OpenLancer does not write into a game folder.",
            Assert.Single(result.Problems).Message);
        Assert.Empty(result.Packages);
        Assert.Equal(before, FakeGame.Snapshot(other.Root));
    }

    [Fact]
    public void AnExportFolderInAGitWorkTreeGivesAWarning()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        Directory.CreateDirectory(Path.Combine(game.Game.Folder, "repo", ".git"));

        var result = ModExport.Run(game.Request(mod) with { OutputFolder = Path.Combine(game.Game.Folder, "repo", "exports") });

        Assert.Contains(result.Problems, p => p.Severity == DiagnosticSeverity.Warning &&
                                              p.Message == "The export folder is in a git repository. The packages hold " +
                                              "merged game files. Do not commit them.");
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void TheDefaultFolderIsNextToTheProfilesFolder()
    {
        var profile = Path.Combine(Path.GetTempPath(), "w", "profiles", "p.json");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "w", "exports", "m"), ModExport.DefaultFolder(profile, "m"));
    }

    [Fact]
    public void TheChecklistAloneWritesNoFile()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var result = ModExport.Run(game.Request(mod) with { ChecklistOnly = true });

        Assert.Contains("- Good 'lf_hull': price 6600 -> 5600.", result.Checklist);
        Assert.Empty(result.Packages);
        Assert.False(Directory.Exists(result.OutputFolder));
    }

    /// <summary>Publish settings with each field (section 8.1 of the M11c design). The mods of the tests have version
    /// 1.2.0.</summary>
    private static PublishSettings Texts => PublishSettings.Empty with
    {
        Summary = "A fast ship.",
        Description = "# The ship\n- Fast.\n- **Cheap**.\n\nSee [the thread](https://example.org/t).",
        Authors = ["Ann", "Bob"],
        Credits = [new PublishCredit("Cy", "Tests")],
        Permissions = "Ask first.",
        Category = "Ships",
        Tags = ["Ships", "Gameplay"],
        Requirements = [new PublishRequirement("JFLP", "https://example.org/jflp", "Optional")],
        PageUrl = "https://example.org/mods/1",
        Changelog =
        [
            new ChangelogEntry("1.3.0", ["Next."]), new ChangelogEntry("1.2.0", ["Price fix."]),
            new ChangelogEntry("1.0.0", ["First release."]),
        ],
    };

    /// <summary>The text of an entry of a zip file.</summary>
    private static string Entry(string zip, string name)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static string[] Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName)];
    }

    /// <summary>The script of the FLMM package of an export.</summary>
    private static string Script(string zip, string id)
    {
        using var outer = ZipFile.OpenRead(zip);
        using var flmod = new ZipArchive(outer.GetEntry($"{id}.zip.flmod")!.Open());
        using var reader = new StreamReader(flmod.GetEntry("script.xml")!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>The Preview tab of the Mods window shows the README and the FLMM header of the prepare. They are the
    /// texts that the packages hold.</summary>
    [Fact]
    public void PrepareGivesTheReadmeAndTheFlmmHeaderOfEachPackage()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var prepared = ModExport.Prepare(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with { Publish = Texts });
        var result = ModExport.Write(prepared, []);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        foreach (var (plan, package) in prepared.Packages.Zip(result.Packages))
        {
            var name = package.Form == PackageForm.ModFolder ? "m/m README.txt" : "m README.txt";
            Assert.Equal(Entry(package.Path!, name), plan.Readme);
        }

        var flmm = prepared.Packages.Single(p => p.Form == PackageForm.Flmm);
        Assert.Contains(flmm.ScriptHeader!, Script(result.Packages.Single(p => p.Form == PackageForm.Flmm).Path!, "m"));
        Assert.All(prepared.Packages.Where(p => p.Form != PackageForm.Flmm), p => Assert.Null(p.ScriptHeader));
    }

    [Fact]
    public void TheReadmeHasTheSummaryDescriptionAuthorsCreditsAndChanges()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var result = ModExport.Run(game.Request(mod) with { Publish = Texts, Forms = ExportForms.DropIn });
        var readme = Entry(result.Packages[0].Path!, "m README.txt");

        Assert.StartsWith("Mod m\r\nVersion 1.2.0 for Freelancer 1.1. Mod id: m.\r\nBy Ann, Bob.\r\n\r\nA fast ship.\r\n\r\n" +
                          "About:\r\nThe ship\r\n\r\n- Fast.\r\n- Cheap.\r\n\r\nSee the thread (https://example.org/t).\r\n\r\n" +
                          "Requirements:\r\n- Freelancer 1.1, with or without JFLP.\r\n", readme);
        Assert.Contains("- JFLP: Optional (https://example.org/jflp)\r\n", readme);
        Assert.Contains("\r\nChanges:\r\n1.2.0\r\n- Price fix.\r\n1.0.0\r\n- First release.\r\n\r\nCredits:\r\n- Cy: Tests\r\n\r\n" +
                        "Permissions:\r\nAsk first.\r\n\r\nPage: https://example.org/mods/1\r\n\r\nFreelancer", readme);
        Assert.Contains("Mod m was created under Microsoft's", readme);
    }

    [Fact]
    public void TheReadmeLeavesOutHigherChangelogVersions()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with { Publish = Texts });

        foreach (var package in result.Packages)
        {
            var readme = package.Form == PackageForm.ModFolder
                ? Entry(package.Path!, "m/m README.txt")
                : Entry(package.Path!, "m README.txt");
            Assert.DoesNotContain("1.3.0", readme);
            Assert.DoesNotContain("Next.", readme);
        }
        Assert.DoesNotContain("1.3.0", File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile)));
    }

    [Fact]
    public void TheFlmmHeaderHasTheAuthorsAndThePageLink()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var texts = Texts with { Authors = [$"Ann {(char)0x2013} Bob", $"Zo{(char)0xEB}"] };

        var result = ModExport.Run(game.Request(mod) with { Publish = texts, Forms = ExportForms.Flmm });
        var script = Script(result.Packages[0].Path!, "m");

        Assert.Contains("\r\n<author>\r\nAnn - Bob, Zo?\r\n</author>\r\n", script);
        Assert.Contains("\r\n<description>\r\nVersion 1.2.0 for Freelancer 1.1.\r\nA fast ship.\r\n\r\nThe ship\r\n\r\n" +
                        "- Fast.\r\n- Cheap.\r\n\r\nSee the thread (https://example.org/t).\r\n</description>\r\n" +
                        "<modurl>\r\nhttps://example.org/mods/1\r\n</modurl>\r\n</header>\r\n", script);
        Assert.Contains(result.Problems, p => p.Message == PublishMessages.FlmmCharacter(((char)0xEB).ToString(), "the authors"));
        Assert.DoesNotContain("<modurl>", Script(ModExport.Run(game.Request(mod) with { Forms = ExportForms.Flmm }).Packages[0].Path!, "m"));
    }

    [Fact]
    public void TheNexusPageHasEachFieldAndBBCode()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with { Publish = Texts });
        var nexus = File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile));

        Assert.StartsWith("Nexus page of Mod m 1.2.0. Copy each part into the field of the same name.\r\n\r\n" +
                          "== Mod name ==\r\nMod m\r\n\r\n== Version ==\r\n1.2.0\r\n\r\n== Author ==\r\nAnn, Bob\r\n\r\n" +
                          "== Category ==\r\nShips\r\n\r\n== Summary ==\r\nA fast ship.\r\n\r\n== Description (BBCode) ==\r\n" +
                          "[size=5][b]The ship[/b][/size]\r\n[list]\r\n[*]Fast.\r\n[*][b]Cheap[/b].\r\n[/list]\r\n\r\n" +
                          "See [url=https://example.org/t]the thread[/url].\r\n\r\n[size=4][b]Install[/b][/size]\r\n" +
                          "[b]Drop-in zip[/b]\r\n", nexus);
        Assert.Contains("[b]FLMM package[/b]\r\n[list=1]\r\n[*]In the Freelancer Mod Manager, import m.zip.flmod.\r\n", nexus);
        Assert.Contains("== Requirements ==\r\n- Freelancer 1.1: Freelancer 1.1, with or without JFLP.\r\n" +
                        "- OpenLancer: OpenLancer.\r\n- JFLP: Optional (https://example.org/jflp)\r\n", nexus);
        Assert.Contains("== Files ==\r\nm-1.2.0-vanilla-1.1.zip: Drop-in zip for Freelancer 1.1. The README gives the backup " +
                        "and install steps.\r\nm-1.2.0-vanilla-1.1-flmm.zip: FLMM package for Freelancer 1.1 (preview). Import " +
                        "it in the Freelancer Mod Manager.\r\nm-1.2.0-openlancer.zip: Mod folder for OpenLancer.", nexus);
        Assert.Contains("== Changelog ==\r\n1.2.0\r\n- Price fix.\r\n1.0.0\r\n- First release.\r\n", nexus);
        Assert.Contains("== Permissions and credits ==\r\nAsk first.\r\n\r\nCredits:\r\n- Cy: Tests\r\n", nexus);
        Assert.Contains("== Tags ==\r\nShips\r\nGameplay\r\n\r\n== Images ==\r\n", nexus);
        Assert.All(result.Packages, p => Assert.True(PackageTexts.FileDescription(p).Length < PublishLimits.FileDescriptionLength));
    }

    [Fact]
    public void TheNexusPageListsThePackagesOfEarlierExportsOfTheVersion()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var root = Path.Combine(game.Game.Folder, "exports", "m");
        var folder = ModExport.VersionFolder(root, mod.Manifest);
        var location = new HistoryLocation(root, "m", mod.Folder, [game.Root]);
        var first = ModExport.Run(game.Request(mod) with { OutputFolder = folder, Forms = ExportForms.DropIn });
        PublishHistory.Update(location, "export", h => h.RecordExport(PublishHistory.EntryOf(first, DateTime.UtcNow)));
        var history = PublishHistory.Load(root, "m").History;

        var second = ModExport.Write(ModExport.Prepare(game.Request(mod, ExportTarget.OpenLancer) with
        {
            OutputFolder = folder,
            OpenLancerBase = game.Base,
        }), history.ValidPackages("1.2.0", folder, first.Content));

        var nexus = File.ReadAllText(Path.Combine(folder, ModExport.NexusPageFile));
        var checklist = File.ReadAllText(Path.Combine(folder, ModExport.ChecklistFile));
        Assert.Single(second.Packages);
        Assert.Contains("== Files ==\r\nm-1.2.0-vanilla-1.1.zip: Drop-in zip", nexus);
        Assert.Contains("\r\nm-1.2.0-openlancer.zip: Mod folder for OpenLancer.", nexus);
        Assert.StartsWith("Mod m 1.2.0 (m): checklist for testers\r\n\r\nTarget: Freelancer 1.1\r\nDATA\\EQUIPMENT\\goods.ini\r\n" +
                          "- Good 'lf_hull': price 6600 -> 5600.\r\n\r\nTarget: OpenLancer\r\n", checklist);
    }

    [Fact]
    public void ScreenshotsGoToTheOutputFolderAndNotIntoTheRetailZips()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var a = PublishWorkspace.Screenshot(mod.Folder, "a.png", 1);
        var b = PublishWorkspace.Screenshot(mod.Folder, "b.png", 2);
        var texts = PublishSettings.Empty with { Screenshots = [new PublishScreenshot(b, "Second"), new PublishScreenshot(a, "")] };

        var result = ModExport.Run(game.Request(mod) with { Publish = texts });

        var shots = Path.Combine(result.OutputFolder, ModExport.ScreenshotsFolder);
        Assert.Equal(["01-b.png", "02-a.png"], Directory.EnumerateFiles(shots).Select(Path.GetFileName).Order());
        Assert.Equal(File.ReadAllBytes(Path.Combine(mod.Folder, "publish", "screenshots", "b.png")),
            File.ReadAllBytes(Path.Combine(shots, "01-b.png")));
        Assert.All(result.Packages, p => Assert.DoesNotContain(Entries(p.Path!), e => e.Contains("publish")));
        Assert.Contains("== Images ==\r\nscreenshots\\01-b.png: Second\r\nscreenshots\\02-a.png\r\n",
            File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile)));
    }

    [Fact]
    public void TheExportDeletesNoFileOfTheScreenshotsFolder()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var a = PublishWorkspace.Screenshot(mod.Folder, "a.png", 1);
        var output = Path.Combine(game.Game.Folder, "picked");
        // The user can pick any output folder. A numbered image of the user stays.
        PublishWorkspace.Write(Path.Combine(output, ModExport.ScreenshotsFolder, "05-mine.png"), "mine");

        var result = ModExport.Run(game.Request(mod) with
        {
            OutputFolder = output,
            Publish = PublishSettings.Empty with { Screenshots = [new PublishScreenshot(a, "")] },
        });

        var shots = Path.Combine(result.OutputFolder, ModExport.ScreenshotsFolder);
        Assert.Equal(["01-a.png", "05-mine.png"], Directory.EnumerateFiles(shots).Select(Path.GetFileName).Order());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(shots, "05-mine.png")));
        Assert.EndsWith("== Images ==\r\nscreenshots\\01-a.png\r\n",
            File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile)));
    }

    [Fact]
    public void AHandWrittenLinkThatIsNotHttpsStaysOutOfTheTexts()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var texts = Texts with
        {
            PageUrl = "javascript:alert(1)",
            Requirements = [new PublishRequirement("JFLP", "file:///c/x", "Optional")],
        };

        var result = ModExport.Run(game.Request(mod) with { Publish = texts });
        var dropIn = result.Packages.Single(p => p.Form == PackageForm.DropIn).Path!;

        Assert.DoesNotContain("javascript", Entry(dropIn, "m README.txt"));
        Assert.Contains("- JFLP: Optional\r\n", Entry(dropIn, "m README.txt"));
        Assert.DoesNotContain("<modurl>", Script(result.Packages.Single(p => p.Form == PackageForm.Flmm).Path!, "m"));
        Assert.DoesNotContain("file:///", File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile)));
        // The rules of the publish page give warnings, so the export still writes the packages.
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(2, result.Problems.Count(p => p.Severity == DiagnosticSeverity.Warning && p.Message == PublishMessages.PageLink));
    }

    [Fact]
    public void TheOpenLancerZipHoldsPublishJsonAndTheAcceptedScreenshots()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var a = PublishWorkspace.Screenshot(mod.Folder, "a.png");
        PublishWorkspace.Screenshot(mod.Folder, "unlisted.png", 3);
        PublishSettings.Write(mod.Folder, PublishSettings.Empty with { Screenshots = [new PublishScreenshot(a, "A")] });

        var result = ModExport.Run(game.Request(mod, ExportTarget.OpenLancer) with { Publish = PublishSettings.Load(mod.Folder).Settings });

        var entries = Entries(Assert.Single(result.Packages).Path!);
        Assert.Contains("m/publish.json", entries);
        Assert.Contains("m/publish/screenshots/a.png", entries);
        Assert.DoesNotContain("m/publish/screenshots/unlisted.png", entries);
    }

    [Fact]
    public void ABadScreenshotRefusesOnlyTheOpenLancerZip()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var a = PublishWorkspace.Screenshot(mod.Folder, "a.png");
        PublishWorkspace.Write(Path.Combine(mod.Folder, "publish", "screenshots", "bad.png"), "text");
        var texts = PublishSettings.Empty with
        {
            Screenshots = [new PublishScreenshot(a, ""), new PublishScreenshot("publish/screenshots/bad.png", "")],
        };

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with { Publish = texts });

        Assert.Equal([true, true, false], result.Packages.Select(p => p.Path != null));
        Assert.Contains(result.Problems, p => p.Message == PublishMessages.ImageRefused("publish/screenshots/bad.png"));
        Assert.False(Directory.Exists(Path.Combine(result.OutputFolder, ModExport.ScreenshotsFolder)));
        Assert.Equal(1, result.Checks.Single(c => c.Target == ExportTarget.OpenLancer).Errors);
        Assert.Equal(0, result.Checks.Single(c => c.Target == ExportTarget.Vanilla).Errors);
    }

    [Fact]
    public void PackagesGoToAVersionFolder()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var root = Path.Combine(game.Game.Folder, "exports", "m");

        var result = ModExport.Run(game.Request(mod) with { OutputFolder = ModExport.VersionFolder(root, mod.Manifest) });

        Assert.Equal(Path.Combine(root, "1.2.0"), result.OutputFolder);
        Assert.All(result.Packages, p => Assert.Equal(Path.Combine(root, "1.2.0", p.FileName), p.Path));
        Assert.Throws<ArgumentException>(() => ModExport.VersionFolder(root, mod.Manifest with { Version = @"..\x" }));
    }

    [Fact]
    public void AnUnsafeVersionWritesNothing()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new() { [@"patches\" + GoodsPath] = Patch });
        var manifest = Path.Combine(mod.Folder, "mod.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"1.2.0\"", "\"..\\\\..\\\\x\""));
        mod = Data.Mods.Mod.Load(mod.Folder);

        var result = ModExport.Run(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer));

        Assert.Equal(@"..\..\x", mod.Manifest.Version);
        Assert.Equal(PublishMessages.VersionUnsafe(@"..\..\x"), Assert.Single(result.Problems).Message);
        Assert.Empty(result.Packages);
        Assert.False(Directory.Exists(result.OutputFolder));
    }

    [Fact]
    public void APendingSaveRefusesTheCheckAndTheExport()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        PublishWorkspace.Write(Path.Combine(mod.Folder, ".olsave", "journal"), "olsave journal 1\n");

        var prepared = ModExport.Prepare(game.Request(mod));

        Assert.True(prepared.Refused);
        // The check and the export use one text, so it names neither of them.
        Assert.Equal("The last save of this mod is not complete. Open the profile once, so that the editor completes the save.",
            Assert.Single(prepared.Problems).Message);
        Assert.Empty(ModExport.Write(prepared, []).Packages);
    }

    [Fact]
    public void PrepareWritesNoFileAndCompressesNothing()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var prepared = ModExport.Prepare(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with { Publish = Texts });

        Assert.False(Directory.Exists(prepared.OutputFolder));
        Assert.Equal(3, prepared.Packages.Count);
        Assert.All(prepared.Packages, p => Assert.False(p.Refused));
        Assert.Equal(3, prepared.Digests.Count);
        Assert.Equal([ExportTarget.Vanilla, ExportTarget.OpenLancer], prepared.Checks.Select(c => c.Target));
        Assert.Contains("- Good 'lf_hull': price 6600 -> 5600.", prepared.Checklist);
    }

    [Fact]
    public void TwoExportsWithSettingsGiveTheSameBytes()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var request = game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with { Publish = Texts };

        var one = ModExport.Run(request with { OutputFolder = Path.Combine(game.Game.Folder, "one") });
        var two = ModExport.Run(request with { OutputFolder = Path.Combine(game.Game.Folder, "two") });

        var files = Directory.EnumerateFiles(one.OutputFolder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(one.OutputFolder, f)).Order().ToList();
        Assert.Equal(5, files.Count);
        Assert.All(files, f => Assert.Equal(File.ReadAllBytes(Path.Combine(one.OutputFolder, f)),
            File.ReadAllBytes(Path.Combine(two.OutputFolder, f))));
        Assert.Equal(one.Packages.Select(p => p.Sha256), two.Packages.Select(p => p.Sha256));
    }

    [Fact]
    public void TheReadmeHasNoDate()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);

        var result = ModExport.Run(game.Request(mod) with { Publish = Texts, Forms = ExportForms.DropIn });

        foreach (var text in new[]
                 {
                     Entry(result.Packages[0].Path!, "m README.txt"),
                     File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.NexusPageFile)),
                     File.ReadAllText(Path.Combine(result.OutputFolder, ModExport.ChecklistFile)),
                 })
        {
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", text);
            Assert.DoesNotContain(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), text);
        }
    }

    /// <summary>A mod that changes the routes gets the route tables in each package of a retail target. OpenLancer
    /// computes its own routes, so the checklist of the target OpenLancer names no route file.</summary>
    [Fact]
    public void TheRetailPackagesHoldTheRouteTablesAndTheOpenLancerChecklistNamesNone()
    {
        using var game = new RouteWorld().System("A", "gate B").System("B", "gate A").WriteInto(new ExportGame());
        var mod = RouteWorld.ConnectionMod(game);
        // The fake game has the folder DATA\universe, so the entries have that letter case.
        string[] routes = ["DATA/universe/shortest_illegal_path.ini", "DATA/universe/shortest_legal_path.ini",
                           "DATA/universe/systems_shortest_path.ini"];

        var prepared = ModExport.Prepare(game.Request(mod, ExportTarget.Vanilla, ExportTarget.OpenLancer) with
        {
            OpenLancerBase = game.Base,
        });
        var result = ModExport.Write(prepared, []);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        var dropIn = Entries(result.Packages.Single(p => p.Form == PackageForm.DropIn).Path!);
        Assert.Subset(dropIn.ToHashSet(), routes.ToHashSet());
        Assert.Contains("DATA/MODS/conn/SYSTEMS/E/E.ini", dropIn);
        using (var outer = ZipFile.OpenRead(result.Packages.Single(p => p.Form == PackageForm.Flmm).Path!))
        using (var flmod = new ZipArchive(outer.GetEntry("conn.zip.flmod")!.Open()))
            Assert.Subset(flmod.Entries.Select(e => e.FullName).ToHashSet(), routes.ToHashSet());
        var vanilla = prepared.ChecklistBlocks.Single(b => b.Target == ExportTarget.Vanilla).Lines;
        var openLancer = prepared.ChecklistBlocks.Single(b => b.Target == ExportTarget.OpenLancer).Lines;
        Assert.Contains(vanilla, l => l.EndsWith("systems_shortest_path.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(openLancer, l => l.Contains("SYSTEMS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(openLancer, l => l.Contains("_path.ini", StringComparison.OrdinalIgnoreCase) ||
                                               l.Contains("route table", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary><c>oltool export</c> and <c>oltool game --write-manifest</c> on a fake game (section 7.14 of the M11
/// design). The tests run the oltool of the test folder.</summary>
public sealed class OltoolExportTests
{
    /// <summary>Runs the oltool of the test folder. The play copy tests use it too.</summary>
    internal static async Task<(int Exit, string Output, string Errors)> RunAsync(params string[] arguments)
    {
        var tool = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oltool.exe" : "oltool");
        Assert.True(File.Exists(tool), $"{tool} is not in the test folder.");
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndOnOwnThread();
        var errors = process.StandardError.ReadToEndOnOwnThread();
        await process.ExitAsync(TimeSpan.FromMinutes(2), "oltool did not end.");
        return (process.ExitCode, await output, await errors);
    }

    /// <summary>A fake Freelancer 1.0 (by the file version of Common.dll), a mod and a profile. Returns the profile
    /// file. The oltool publish tests use it too.</summary>
    /// <param name="targets">More fields of mod.json, for example the targets.</param>
    internal static string Profile(ExportGame game, string patch = "[Good]\nnickname = lf_hull\nprice = 5600\n",
        string targets = "")
    {
        game.Game.WriteBytes(@"EXE\Common.dll", FakeGame.VersionDll(new Version(1, 0, 1223, 11)));
        var mod = game.Mod("ol-test", new() { [@"patches\" + GoodsPath] = patch }, targets);
        var profile = Path.Combine(game.Game.Folder, "profiles", "test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, JsonSerializer.Serialize(new { @base = game.Root, mods = new[] { mod.Folder } }));
        return profile;
    }

    [Fact]
    public async Task TheUsageNamesTheCommand()
    {
        var (exit, _, errors) = await RunAsync("export");

        Assert.Equal(2, exit);
        Assert.Contains("export <profile.json> --mod <id> --target <hde-0.7.1|vanilla-1.1|openlancer> [--target ...]", errors);
        Assert.Equal(2, (await RunAsync("export", "x.json", "--mod", "m", "--target", "other")).Exit);
        Assert.Equal(2, (await RunAsync("export", "x.json", "--mod", "m", "--target", "openlancer", "--target", "openlancer")).Exit);
    }

    [Fact]
    public async Task TheExitCodesTellTheResult()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var output = Path.Combine(game.Game.Folder, "out");

        var written = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin", "--out", output);
        var noBase = await RunAsync("export", profile, "--mod", "ol-test", "--target", "hde-0.7.1", "--out", output);
        var noMod = await RunAsync("export", profile, "--mod", "none", "--target", "vanilla-1.1");

        Assert.True(written.Exit == 0, written.Errors);
        Assert.True(File.Exists(Path.Combine(output, "ol-test-1.2.0-vanilla-1.1.zip")));
        Assert.False(File.Exists(Path.Combine(output, "ol-test-1.2.0-vanilla-1.1-flmm.zip")));
        Assert.Equal(1, noBase.Exit);
        Assert.Contains("No linked game gives the target 'hde-0.7.1'.", noBase.Errors);
        Assert.Equal(2, noMod.Exit);
    }

    /// <summary>
    /// <c>oltool export</c> refuses a mod that the save gate refuses (#87). It opens the profile with the mod active,
    /// and that session only reads. It prints each error of the gate and the refusal, and it writes no package. The
    /// retail test of the costume also shows that <c>oltool validate</c> keeps its rule.
    /// </summary>
    [Fact]
    public async Task TheExportRefusesAModThatTheSaveGateRefuses()
    {
        using var game = new ExportGame();
        var profile = Profile(game, ExportGateTests.DeletePatch, ", \"targets\": [ \"vanilla-1.1\" ]");
        var output = Path.Combine(game.Game.Folder, "out");

        var export = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin",
            "--out", output);

        Assert.Equal(1, export.Exit);
        Assert.Contains("Error: ", export.Errors);
        Assert.Contains("the active mod removes the good 'lf_hull'", export.Errors);
        Assert.Contains(ExportGate.Refusal("ol-test", 1), export.Errors);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task TheChecklistIsPrintedAndNothingIsWritten()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var output = Path.Combine(game.Game.Folder, "out");

        var (exit, text, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--checklist",
            "--out", output);

        Assert.True(exit == 0, errors);
        Assert.Contains("- Good 'lf_hull': price 6600 -> 5600.", text);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task TheProbeHasItsOwnName()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var output = Path.Combine(game.Game.Folder, "out");

        var (exit, _, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "flmm",
            "--flmm-probe", "--out", output);

        Assert.True(exit == 0, errors);
        Assert.True(File.Exists(Path.Combine(output, "ol-test-1.2.0-vanilla-1.1-flmm-probe.zip")));
    }

    [Fact]
    public async Task WriteManifestWritesTheFileList()
    {
        using var game = new FakeGame();
        var file = Path.Combine(game.Folder, "list.tsv");

        var (exit, output, errors) = await RunAsync("game", game.Root, "--write-manifest", file);
        var inside = await RunAsync("game", game.Root, "--write-manifest", Path.Combine(game.Root, "list.tsv"));

        Assert.True(exit == 0, errors);
        Assert.StartsWith("Wrote ", output);
        Assert.Contains("exe/freelancer.ini\t", File.ReadAllText(file));
        Assert.Equal(2, inside.Exit);
        Assert.False(File.Exists(Path.Combine(game.Root, "list.tsv")));
    }

    [Fact]
    public async Task OltoolTheDefaultFolderIsTheVersionFolder()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var root = Path.Combine(game.Game.Folder, "exports", "ol-test");

        var (exit, output, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin");

        Assert.True(exit == 0, errors);
        Assert.Contains($"Wrote {Path.Combine(root, "1.2.0", "ol-test-1.2.0-vanilla-1.1.zip")}", output);
        var history = PublishHistory.Load(root, "ol-test").History;
        Assert.Equal("1.2.0", Assert.Single(history.Exports).Version);
        Assert.Equal(0, history.CheckOf("vanilla-1.1")!.Errors);
        Assert.True(File.Exists(Path.Combine(root, "1.2.0", "nexus-page.txt")));
    }

    [Fact]
    public async Task OltoolTheSameVersionWithOtherGameFilesExitsOne()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var package = Path.Combine(game.Game.Folder, "exports", "ol-test", "1.2.0", "ol-test-1.2.0-vanilla-1.1.zip");
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin")).Exit);
        var before = File.ReadAllBytes(package);
        ChangePrice(game);

        var (exit, _, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin");

        Assert.Equal(1, exit);
        Assert.Contains("has other game files. Players cannot see which of the two packages they have.", errors);
        Assert.Equal(before, File.ReadAllBytes(package));
    }

    [Fact]
    public async Task OltoolSameVersionReplacesAVersionWithoutTheMark()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var root = Path.Combine(game.Game.Folder, "exports", "ol-test");
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin")).Exit);
        ChangePrice(game);

        var (exit, _, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin",
            "--same-version");

        Assert.True(exit == 0, errors);
        var history = PublishHistory.Load(root, "ol-test").History;
        Assert.Equal(2, history.Exports.Count);
        Assert.Equal(history.Exports[1].Time, history.Exports[0].ReplacedBy);
        // The FLMM package of the same content is no conflict, so it needs no replace.
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "flmm")).Exit);
    }

    [Fact]
    public async Task OltoolSameVersionDoesNotReplaceAPublishedVersion()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var root = Path.Combine(game.Game.Folder, "exports", "ol-test");
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin")).Exit);
        PublishHistory.Update(new HistoryLocation(root, "ol-test", Path.Combine(game.ModsFolder, "ol-test"), []), "mark",
            h => h.Mark("1.2.0", DateTime.UtcNow, ""));
        ChangePrice(game);

        var (exit, _, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin",
            "--same-version");
        var plain = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin");

        Assert.Equal(1, exit);
        Assert.Contains("Version 1.2.0 is on Nexus. Write a higher version into mod.json.", errors);
        Assert.Equal(1, plain.Exit);
        Assert.Contains(PublishMessages.GuardPublished("1.2.0"), plain.Errors);
        Assert.Single(PublishHistory.Load(root, "ol-test").History.Exports);
    }

    [Fact]
    public async Task OltoolSameVersionReplacesAVersionOfTwoTargets()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var root = Path.Combine(game.Game.Folder, "exports", "ol-test");
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin")).Exit);
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "openlancer")).Exit);
        ChangePrice(game);

        var one = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin", "--same-version");
        var (exit, output, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "openlancer", "--target",
            "vanilla-1.1", "--format", "dropin", "--same-version");

        // A replace of one target would leave the old content in the package of the other target.
        Assert.Equal(1, one.Exit);
        Assert.Contains("also has packages for OpenLancer (mod folder zip).", one.Errors);
        Assert.True(exit == 0, errors);
        Assert.Contains("ol-test-1.2.0-vanilla-1.1.zip", output);
        Assert.Contains("ol-test-1.2.0-openlancer.zip", output);
        var history = PublishHistory.Load(root, "ol-test").History;
        Assert.Equal(3, history.Exports.Count);
        Assert.Equal([history.Exports[2].Time, history.Exports[2].Time], history.Exports.Take(2).Select(e => e.ReplacedBy));
        Assert.Equal(["openlancer", "vanilla-1.1"], history.Exports[2].Packages.Select(p => p.Target).Order());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "formatVersion": 2 }""")]
    public async Task OltoolAHistoryThatDoesNotLoadStopsTheExport(string text)
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var root = Path.Combine(game.Game.Folder, "exports", "ol-test");
        var package = Path.Combine(root, "1.2.0", "ol-test-1.2.0-vanilla-1.1.zip");
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin")).Exit);
        var before = File.ReadAllBytes(package);
        File.WriteAllText(Path.Combine(root, PublishHistory.FileName), text);
        ChangePrice(game);

        var (exit, _, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin");

        // Without the history, the guard cannot protect the package, so the export writes nothing.
        Assert.Equal(2, exit);
        Assert.Contains("The export stops, because the same-version guard needs the publish history.", errors);
        Assert.Equal(before, File.ReadAllBytes(package));
        Assert.Equal(text, File.ReadAllText(Path.Combine(root, PublishHistory.FileName)));
        Assert.False(File.Exists(Path.Combine(root, PublishHistory.BadName)));
    }

    [Fact]
    public async Task OltoolTheProbeKeepsTheTextsOfTheVersionFolder()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var folder = Path.Combine(game.Game.Folder, "exports", "ol-test", "1.2.0");
        Assert.Equal(0, (await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "dropin")).Exit);
        var nexus = File.ReadAllText(Path.Combine(folder, ModExport.NexusPageFile));
        var checklist = File.ReadAllText(Path.Combine(folder, ModExport.ChecklistFile));

        var (exit, _, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "vanilla-1.1", "--format", "flmm",
            "--flmm-probe");

        Assert.True(exit == 0, errors);
        Assert.True(File.Exists(Path.Combine(folder, "ol-test-1.2.0-vanilla-1.1-flmm-probe.zip")));
        Assert.Equal(nexus, File.ReadAllText(Path.Combine(folder, ModExport.NexusPageFile)));
        Assert.Equal(checklist, File.ReadAllText(Path.Combine(folder, ModExport.ChecklistFile)));
        Assert.DoesNotContain("probe", nexus);
    }

    [Fact]
    public async Task OltoolAnUnsafeVersionExitsOne()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        var manifest = Path.Combine(game.ModsFolder, "ol-test", "mod.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"1.2.0\"", "\"../x\""));

        var (exit, _, errors) = await RunAsync("export", profile, "--mod", "ol-test", "--target", "openlancer");

        Assert.Equal(1, exit);
        Assert.Contains(PublishMessages.VersionUnsafe("../x"), errors);
        Assert.False(Directory.Exists(Path.Combine(game.Game.Folder, "exports")));
    }

    /// <summary>Changes the price of the patch of the mod, so the merged goods change.</summary>
    private static void ChangePrice(ExportGame game) =>
        File.WriteAllText(Path.Combine(game.ModsFolder, "ol-test", "patches", GoodsPath),
            "[Good]\r\nnickname = lf_hull\r\nprice = 4400\r\n");
}
