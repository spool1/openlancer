using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor;
using OpenLancer.Editor.Export;
using Xunit;
using static OpenLancer.Tests.ExportGame;

namespace OpenLancer.Tests;

/// <summary>The targets of the export, their bases and their refusals (sections 7.1 and 7.9 of the M11 design).</summary>
public sealed class ExportTargetTests
{
    [Fact]
    public void ATier2ModCannotExportToHdEdition()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "tier": 2""");

        Assert.Equal("Mod 'm' is a tier-2 mod. A tier-2 mod runs only in OpenLancer. Pick the target OpenLancer.",
            ExportTarget.HdEdition.Refusal(mod.Manifest));
        Assert.Equal(ExportTarget.HdEdition.Refusal(mod.Manifest), ExportTarget.Vanilla.Refusal(mod.Manifest));
        Assert.Null(ExportTarget.OpenLancer.Refusal(mod.Manifest));
    }

    [Fact]
    public void AModWithoutTheTargetIsRefused()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "targets": [ "hde-0.7.1" ]""");

        Assert.Equal("Mod 'm' does not name the target 'vanilla-1.1'. Add the target to mod.json, or pick another target.",
            ExportTarget.Vanilla.Refusal(mod.Manifest));
        Assert.Null(ExportTarget.HdEdition.Refusal(mod.Manifest));
    }

    [Fact]
    public void ATargetWithoutALinkedBaseIsNotAvailable()
    {
        using var game = new ExportGame();
        var report = game.Report();

        Assert.Equal(ModTargets.Vanilla, report.Target);
        Assert.Null(ExportTarget.Vanilla.BaseRefusal(report));
        Assert.Equal("No linked game gives the target 'hde-0.7.1'. Link a game folder of Freelancer: HD Edition 0.7.1 in " +
                     "File > Game link.", ExportTarget.HdEdition.BaseRefusal(report));
        Assert.Equal("No linked game gives the target 'vanilla-1.1'. Link a game folder of Freelancer 1.0 or 1.1 in " +
                     "File > Game link.", ExportTarget.Vanilla.BaseRefusal(null));
        Assert.Null(ExportTarget.OpenLancer.BaseRefusal(null));
        // Each target finds its base among the linked games.
        Assert.Same(report, ExportTarget.Vanilla.BaseIn([report]));
        Assert.Null(ExportTarget.HdEdition.BaseIn([report]));
        Assert.Null(ExportTarget.OpenLancer.BaseIn([report]));
    }

    [Fact]
    public void TheDialogRequestCarriesThePublishSettings()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n");
        PublishSettings.Write(mod.Folder, PublishSettings.Empty with { Summary = "From publish.json." });

        var request = ExportRequest.Of(mod, [ExportTarget.Vanilla], [game.Report()], game.Game.Folder, ExportForms.DropIn,
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator, out var problems);

        Assert.Equal("From publish.json.", request.Publish.Summary);
        Assert.NotNull(request.BaseOf(ExportTarget.Vanilla));
        Assert.Equal(ExportForms.DropIn, request.Forms);
        Assert.Empty(problems);
    }

    [Fact]
    public void TheDialogRequestGivesTheProblemsOfPublishJson()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n");
        PublishWorkspace.Write(Path.Combine(mod.Folder, PublishSettings.FileName),
            """{ "formatVersion": 1, "summary": "Kept.", "authors": "Ann" }""");

        var request = ExportRequest.Of(mod, [ExportTarget.Vanilla], [game.Report()], game.Game.Folder, ExportForms.DropIn,
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator, out var problems);

        Assert.Equal("Kept.", request.Publish.Summary);
        Assert.Equal("publish.json: 'authors' must be a list of texts. OpenLancer ignores the value.", Assert.Single(problems));
    }

    /// <summary>The one request builder of DA gives the request that the Export dialog and <c>oltool export</c> built
    /// before (review finding R12 of MCP1-2). A report of a folder without a game is never a base. The first report of
    /// a target that can be its base is the base of the target, as in both old builders.</summary>
    [Fact]
    public void TheOneRequestBuilderGivesTheRequestOfTheDialogAndOfOltool()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n");
        PublishSettings.Write(mod.Folder, PublishSettings.Empty with { Summary = "From publish.json." });
        var noGame = GameDetector.CheckRoot(Path.Combine(game.Root, "none"), cancel: TestContext.Current.CancellationToken);
        var report = game.Report();
        IReadOnlyList<GameReport> reports = [noGame, report];
        var folder = Path.Combine(game.Root, "out");

        var request = ExportRequest.Of(mod, ExportTarget.All, reports, folder, ExportForms.Flmm,
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator, out var problems);

        foreach (var target in ExportTarget.All)
        {
            // The rule of the Export dialog before the builder.
            var dialog = target.IsRetail ? reports.FirstOrDefault(r => target.BaseRefusal(r) == null) : null;
            Assert.Same(dialog, request.BaseOf(target));
        }
        // The rule of oltool export before the builder, for the base that the game gives.
        Assert.Same(reports.FirstOrDefault(r => r.Target == ExportTarget.Vanilla.Id), request.BaseOf(ExportTarget.Vanilla));
        Assert.Same(mod, request.Mod);
        Assert.Equal(ExportTarget.All, request.Targets);
        Assert.Equal(folder, request.OutputFolder);
        Assert.Equal(ExportForms.Flmm, request.Forms);
        Assert.Equal("From publish.json.", request.Publish.Summary);
        Assert.Same(TestWorkspace.FakeDllWriter.Instance, request.StringWriter);
        Assert.False(request.ChecklistOnly || request.FlmmProbe);
        Assert.Null(request.OpenLancerBase);
        Assert.Empty(problems);
    }

    /// <summary>The one checklist builder of DA gives the base key of the rule of the Mods window and of <c>oltool
    /// publish</c>. A read of the saved files gives the same input as the parts that the Mods window reads.</summary>
    [Fact]
    public void TheOneChecklistBuilderGivesTheBaseKeyOfTheModsWindowAndOfOltool()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod("[Good]\nnickname = lf_hull\nprice = 5600\n");
        var report = game.Report();
        var history = Path.Combine(game.Root, "history");

        var read = ChecklistInput.Read(mod, history, [report], [ExportTarget.Vanilla], out var problems);
        var fingerprint = new ModFingerprint();
        var parts = ChecklistInput.Of(mod.Manifest, PublishSettings.Empty, new PublishHistory { ModId = mod.Id },
            fingerprint.Content(mod.Manifest, mod.Folder), fingerprint.Page(mod.Manifest, mod.Folder, PublishSettings.Empty),
            [report], [ExportTarget.Vanilla]);

        Assert.Empty(problems);
        Assert.Equal(TargetCheck.BaseKey(report), read.BaseOf(ExportTarget.Vanilla));
        Assert.Null(read.BaseOf(ExportTarget.HdEdition));
        Assert.Null(read.BaseOf(ExportTarget.OpenLancer));
        Assert.Equal(parts.Content, read.Content);
        Assert.Equal(parts.Page, read.Page);
        Assert.Equal(PublishChecklist.Of(parts).Steps.Select(s => (s.Name, s.Complete)),
            PublishChecklist.Of(read).Steps.Select(s => (s.Name, s.Complete)));
    }

    [Fact]
    public void AModWithDependenciesIsRefused()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "dependsOn": [ "other" ]""");

        Assert.Equal("Mod 'm' needs other mods. The export of a mod that needs other mods is not available yet.",
            ExportTarget.Vanilla.Refusal(mod.Manifest));
    }

    [Fact]
    public void AChangedBaseGivesAWarning()
    {
        using var game = new ExportGame();
        var builds = game.Builds;
        game.Game.Write(GoodsPath, "[Good]\nnickname = changed\n");

        var report = game.Game.Detect(builds);

        Assert.Equal(DataState.Changed, report.Data);
        Assert.Equal(["The INI files of Freelancer 1.0 differ from the retail files. Players with a clean install can see " +
                      "other results."], ExportTarget.Vanilla.BaseWarnings(report));
    }

    [Fact]
    public void AJflpBaseGivesAWarning()
    {
        using var game = new ExportGame();
        game.Game.Libraries("jflp.dll");
        game.Game.WriteBytes(@"EXE\jflp.dll", FakeGame.VersionDll(new Version(1, 2, 0, 0)));

        var report = game.Report();

        Assert.True(report.Jflp);
        Assert.Contains("The base has JFLP. The drop-in zip carries the JFLP version of each changed file to each player.",
            ExportTarget.Vanilla.BaseWarnings(report));
    }

    [Fact]
    public void TheExportOfARefusedTargetWritesNoFile()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", [], """, "tier": 2""");

        var result = ModExport.Run(game.Request(mod, ExportTarget.HdEdition, ExportTarget.Vanilla));

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Problems.Count(p => p.Severity == DiagnosticSeverity.Error));
        Assert.All(result.Packages, p => Assert.Null(p.Path));
        Assert.False(Directory.Exists(result.OutputFolder));
    }
}

/// <summary>The choices of the Export dialog in the editor settings (section 7.15 of the M11 design).</summary>
public sealed class ExportSettingsTests
{
    [Fact]
    public void TheChoicesOfEachModStayAfterASaveAndARead()
    {
        var settings = new EditorSettings();
        Assert.Null(settings.ExportOf(@"C:\mods\m"));
        settings.SetExport(@"C:\mods\m", new ExportChoices(["hde-0.7.1"], true, false, @"C:\exports\m"));

        var read = EditorSettings.Parse(settings.ToJson(), DateTime.UtcNow);

        var choices = read.ExportOf(@"C:\mods\m")!;
        Assert.Equal(["hde-0.7.1"], choices.Targets);
        Assert.True(choices.DropIn);
        Assert.False(choices.Flmm);
        Assert.Equal(@"C:\exports\m", choices.Folder);
        if (OperatingSystem.IsWindows())
            Assert.NotNull(read.ExportOf(@"c:\MODS\m"));
    }
}

/// <summary>The files of a game root and its layout (section 7.1 of the M11 design).</summary>
public sealed class GameBaseTests
{
    [Fact]
    public void TheLayoutMapsExeToBin()
    {
        using var game = new FakeGame();
        game.Write(@"bin\freelancer.ini", "[Freelancer]\ndata path = ..\\DATA\n");

        var source = new GameBase(game.Root, GameLayout.SourceBuild);
        var standard = GameBase.Standard(game.Root);

        Assert.Contains("data path = ..\\DATA", source.OpenFileSystem().ReadAllText(@"EXE\freelancer.ini"));
        Assert.DoesNotContain("data path = ..\\DATA", standard.OpenFileSystem().ReadAllText(@"EXE\freelancer.ini"));
        Assert.True(source.OpenFileSystem().FileExists(@"DATA\EQUIPMENT\goods.ini"));
        Assert.Equal(@"bin\freelancer.ini", source.ToDisk(@"EXE\freelancer.ini"));
        Assert.Equal(@"DATA\x.ini", source.ToDisk(@"DATA\x.ini"));
        Assert.Equal(@"EXE\freelancer.ini", standard.ToDisk(@"EXE\freelancer.ini"));
    }
}
