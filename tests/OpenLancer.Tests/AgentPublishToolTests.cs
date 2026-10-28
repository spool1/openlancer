using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.OltoolExportTests;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>publish_checklist</c> and <c>export_mod</c> of section 7.6 of the MCP1 design. They read the saved files
/// of the active mod with the builders of the Mods window, the Export dialog and oltool. The tests run on a fake
/// Freelancer 1.0 game, as the tests of <c>oltool publish</c> and <c>oltool export</c> do.
/// </summary>
public sealed class AgentPublishToolTests
{
    /// <summary>A profile with the mod <c>ol-test</c> for the target Freelancer 1.1 only, with a summary, and an edit
    /// session of the mod.</summary>
    internal static (string Profile, EditSession Session) Ready(ExportGame game)
    {
        var profile = Profile(game, targets: ", \"targets\": [ \"vanilla-1.1\" ]");
        PublishSettings.Write(Path.Combine(game.ModsFolder, "ol-test"), PublishSettings.Empty with { Summary = "A test mod." });
        return (profile, EditSession.Open(ModProfile.Load(profile), "ol-test", TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator));
    }

    /// <summary>The checklist parity test (review finding R12 of MCP1-2): the tool gives the steps and the recommended
    /// items of <c>oltool publish --json</c>.</summary>
    [Fact]
    public void TheChecklistGivesTheStepsOfOltoolPublish()
    {
        using var game = new ExportGame();
        var (profile, session) = Ready(game);
        using var _ = session;

        var tool = Ok(new TestAgentHost(session), "publish_checklist");
        var (exit, output, errors) = Run("publish", profile, "--mod", "ol-test", "--json");

        Assert.True(exit == 1, errors);
        var json = JsonNode.Parse(output)!;
        foreach (var name in new[] { "mod", "name", "version", "complete", "total", "recommended" })
            Assert.True(JsonNode.DeepEquals(json[name], tool[name]), name);
        Assert.True(JsonNode.DeepEquals(json["steps"], tool["steps"]), tool["steps"]!.ToJsonString());
        Assert.Equal(6, (int)tool["total"]!);
        Assert.Null(tool["note"]);
        Assert.Empty(tool["warnings"]!.AsArray());
    }

    /// <summary>The checklist reads the saved files and runs while the user pauses the agent edits, because it changes
    /// nothing. With unsaved edits, the result tells that the checklist does not count them. The editor host gives the
    /// game links of the editor.</summary>
    [Fact]
    public void TheChecklistReadsTheSavedFilesAlsoDuringPauseAndUsesTheGamesOfTheHost()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var report = game.Report();
        var host = new TestAgentHost(session)
        {
            TestGames = new AgentGames([report], [game.Root], [ExportTarget.Vanilla]),
            TestPaused = true,
        };
        session.Execute(EditCommand.SetLinesFromText(new SectionRef(ExportGame.GoodsPath, new SectionIdentity("Good", "lf_hull")),
            "price", ["1"]));

        var tool = Ok(host, "publish_checklist");

        var mod = session.ActiveMod!;
        var expected = PublishChecklist.Of(ChecklistInput.Read(mod, ModExport.DefaultFolder(session.Profile.FilePath, mod.Id),
            [report], [ExportTarget.Vanilla], out _));
        Assert.Equal(expected.Steps.Select(s => (s.Name, s.Complete, s.Missing.ToArray())),
            tool["steps"]!.AsArray().Select(s => ((string)s!["name"]!, (bool)s["complete"]!,
                s["missing"]!.AsArray().Select(m => (string)m!).ToArray())));
        Assert.Equal("The checklist reads the saved files. The active mod has unsaved edits, and the checklist does not " +
                     "count them.", (string?)tool["note"]);
    }

    /// <summary>The default export folder of the mod (decision D714): <c>exports\ol-test</c> next to the folder of the
    /// profile.</summary>
    private static string ExportRoot(ExportGame game) => Path.Combine(game.Game.Folder, "exports", "ol-test");

    private static string ModFolder(ExportGame game) => Path.Combine(game.ModsFolder, "ol-test");

    private static void SetPrice(ExportGame game, int price) =>
        File.WriteAllText(Path.Combine(ModFolder(game), Mod.PatchesFolder, ExportGame.GoodsPath),
            $"[Good]\nnickname = lf_hull\nprice = {price}\n");

    /// <summary>An export writes the packages into the default export folder of the mod and records them in the
    /// publish history. The input has no folder argument (decision D714). The export changes no data, so the revision
    /// stays.</summary>
    [Fact]
    public void AnExportWritesThePackagesIntoTheDefaultFolderAndRecordsThem()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var host = new TestAgentHost(session);

        var result = Ok(host, "export_mod", """{ "formats": ["drop_in"] }""");

        Assert.Equal(["targets", "formats", "replace_version"],
            AgentTools.Find("export_mod")!.InputSchema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(("1.2.0", "exports\\ol-test\\1.2.0"), ((string?)result["version"], (string?)result["folder"]));
        Assert.True((bool)result["succeeded"]!, result.ToJsonString());
        Assert.False((bool)result["replaced"]!);
        var package = Assert.Single(result["packages"]!.AsArray())!;
        Assert.Equal(("vanilla-1.1", "drop_in", true),
            ((string?)package["target"], (string?)package["form"], (bool)package["written"]!));
        Assert.True(File.Exists(Path.Combine(ExportRoot(game), "1.2.0", (string)package["file"]!)));
        Assert.Single(PublishHistory.Load(ExportRoot(game), "ol-test").History.Exports);
        Assert.False(host.Exports.Running);
        Assert.Equal("0", (string?)result["rev"]);
        Assert.Empty(host.Edits);
    }

    /// <summary>The refusals of an export (section 7.6 of the MCP1 design). The export refuses unsaved edits, a running
    /// export and a target without a base. It also refuses a retail target without a package form, an unsafe version and
    /// a history that does not load. The export writes nothing.</summary>
    [Fact]
    public void AnExportRefusesWhatTheExportDialogRefuses()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var host = new TestAgentHost(session);
        var goods = new SectionRef(ExportGame.GoodsPath, new SectionIdentity("Good", "lf_hull"));

        session.Execute(EditCommand.SetLinesFromText(goods, "price", ["1"]));
        var unsaved = Error(host, "export_mod");
        session.Undo();
        host.TestExportBusy = "An export runs now. Wait until it ends.";
        var busy = Error(host, "export_mod");
        host.TestExportBusy = null;
        var noBase = Error(host, "export_mod", """{ "targets": ["hde-0.7.1"] }""");
        var noForm = Error(host, "export_mod", """{ "formats": [] }""");
        var folder = Error(host, "export_mod", """{ "folder": "out" }""");
        var manifest = Path.Combine(ModFolder(game), ModManifest.FileName);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"1.2.0\"", "\"1.2.0/x\""));
        var unsafeVersion = Error(host, "export_mod");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"1.2.0/x\"", "\"1.2.0\""));
        Directory.CreateDirectory(ExportRoot(game));
        File.WriteAllText(Path.Combine(ExportRoot(game), PublishHistory.FileName), "not json");
        var noHistory = Error(host, "export_mod");

        Assert.Equal(("refused", "UnsavedEdits", "The export uses the saved files. Save first."), unsaved);
        Assert.Equal(("refused", "ExportRuns", "An export runs now. Wait until it ends."), busy);
        Assert.Equal(("refused", "TargetRefused"), (noBase.Code, noBase.Detail));
        Assert.Equal("invalid_argument", noForm.Code);
        // The export goes into the default export folder only, so the tool takes no folder (decision D714).
        Assert.Equal("invalid_argument", folder.Code);
        Assert.Equal(("refused", "UnsafeVersion"), (unsafeVersion.Code, unsafeVersion.Detail));
        Assert.Equal(("refused", "HistoryNotLoaded"), (noHistory.Code, noHistory.Detail));
        Assert.False(Directory.Exists(Path.Combine(ExportRoot(game), "1.2.0")));
        Assert.False(host.Exports.Running);
    }

    /// <summary>The same-version guard (decision D568 of the M11c design). An export of the version with other game
    /// files gives <c>guard_conflict</c> without <c>replace_version</c>, and replaces the packages with it. A version
    /// with the mark "published" always gives <c>guard_conflict</c>.</summary>
    [Fact]
    public void TheGuardRefusesAnExportedVersionWithoutReplaceAndAPublishedVersionAlways()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var host = new TestAgentHost(session);
        const string DropIn = """{ "formats": ["drop_in"] }""";
        const string Replace = """{ "formats": ["drop_in"], "replace_version": true }""";

        var first = Ok(host, "export_mod", DropIn);
        var package = Path.Combine(ExportRoot(game), "1.2.0", (string)first["packages"]![0]!["file"]!);
        var before = File.ReadAllBytes(package);
        SetPrice(game, 4400);
        var exported = Error(host, "export_mod", DropIn);
        var unchanged = File.ReadAllBytes(package);
        var replaced = Ok(host, "export_mod", Replace);
        PublishHistory.Update(new HistoryLocation(ExportRoot(game), "ol-test", ModFolder(game), []), "mark",
            h => h.Mark("1.2.0", DateTime.UtcNow, ""));
        SetPrice(game, 3300);
        var published = Error(host, "export_mod", Replace);

        Assert.Equal(("guard_conflict", "Exported"), (exported.Code, exported.Detail));
        Assert.Equal("An earlier export of version 1.2.0 has other game files, so players cannot see which package they " +
                     "have. Call export_mod again with replace_version: true to replace that export. Or ask the user for " +
                     "a higher version.", exported.Message);
        Assert.Equal(before, unchanged);
        Assert.True((bool)replaced["replaced"]!);
        Assert.NotEqual(before, File.ReadAllBytes(package));
        Assert.Equal(("guard_conflict", "Published"), (published.Code, published.Detail));
        Assert.Equal("Version 1.2.0 has the mark published. A new export needs a higher version. Ask the user to " +
                     "increase the version in the Mods window.", published.Message);
    }

    /// <summary>A cancel during the write stops the call, and the write goes on to its end. The history records the
    /// export, as in the Export dialog (sections 5.5, item 6 and 8.5, item 6 of the MCP1 design).</summary>
    [Fact]
    public async Task ACancelDuringTheWriteLetsTheWriteEndAndTheHistoryRecordIt()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var host = new TestAgentHost(session);
        using var cancel = new CancellationTokenSource();
        var progress = new CancelAt("The export writes the packages.", cancel);

        var tail = AgentTools.Run(host, "export_mod", null).Tail!(cancel.Token, progress);
        var result = await tail.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await WaitForTheExport(host);

        Assert.Equal(("limit", "TailTime"), ((string?)result.Content["code"], (string?)result.Content["detail"]));
        Assert.StartsWith("The export took more time than the limit of the call. The write of the packages goes on",
            (string?)result.Content["message"]);
        Assert.Single(PublishHistory.Load(ExportRoot(game), "ol-test").History.Exports);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(ExportRoot(game), "1.2.0")));
        Assert.False(host.Exports.Running);
    }

    /// <summary>A cancel during the prepare stops the export before its write. It writes no package.</summary>
    [Fact]
    public async Task ACancelDuringThePrepareWritesNoPackage()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var host = new TestAgentHost(session);
        using var cancel = new CancellationTokenSource();

        var tail = AgentTools.Run(host, "export_mod", null).Tail!(cancel.Token,
            new CancelAt("The export checks the mod.", cancel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tail);
        await WaitForTheExport(host);

        Assert.False(Directory.Exists(Path.Combine(ExportRoot(game), "1.2.0")));
        Assert.Empty(PublishHistory.Load(ExportRoot(game), "ol-test").History.Exports);
        Assert.False(host.Exports.Running);
    }

    /// <summary>The tail of an export has its own time limit (section 8.4 of the MCP1 design). It is 10 minutes by
    /// default, not the limit of a check or an image. The queue applies it to the tail of the export.</summary>
    [Fact]
    public async Task TheTailOfAnExportHasTheTimeLimitOfAnExport()
    {
        using var game = new ExportGame();
        var (_, session) = Ready(game);
        using var __ = session;
        var clock = new FakeClock();
        var host = new TestAgentHost(session);
        using var queue = new ToolQueue(host, clock);
        using var release = new ManualResetEventSlim();
        var progress = new Holding(release);

        var call = queue.CallAsync("export_mod", null, null, TestContext.Current.CancellationToken, progress);
        Assert.True(queue.RunNext());
        progress.Started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(61));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var afterCheckLimit = call.IsCompleted;
        clock.Advance(TimeSpan.FromMinutes(9));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.Set();
        host.Exports.Wait();

        Assert.False(afterCheckLimit);
        Assert.Equal(("limit", "TailTime"), ((string?)result.Content["code"], (string?)result.Content["detail"]));
        Assert.Equal("The call took more than 600 seconds, so the server stopped it.", (string?)result.Content["message"]);
        Assert.Equal(TimeSpan.FromMinutes(10), AgentTools.Find("export_mod")!.TailLimit(new AgentLimits()));
        Assert.Equal(TimeSpan.FromSeconds(60), AgentTools.Find("check")!.TailLimit(new AgentLimits()));
        Assert.Equal(120, new AgentLimits { ExportMinutes = 1000 }.Clamped().ExportMinutes);
        Assert.False(Directory.Exists(Path.Combine(ExportRoot(game), "1.2.0")));
    }

    /// <summary>Waits for the end of the export on another thread. The cancel of the test runs on the thread of the
    /// export, so the test can continue on that thread. A wait there would wait for itself.</summary>
    private static Task WaitForTheExport(TestAgentHost host) =>
        Task.Run(host.Exports.Wait).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

    /// <summary>A progress that cancels the call at a step of the export, on the thread of the export.</summary>
    private sealed class CancelAt(string message, CancellationTokenSource cancel) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
            if (value.Message == message)
                cancel.Cancel();
        }
    }

    /// <summary>A progress that holds the export at its first step until the test releases it.</summary>
    private sealed class Holding(ManualResetEventSlim release) : IProgress<ToolProgress>
    {
        public ManualResetEventSlim Started { get; } = new();

        public void Report(ToolProgress value)
        {
            if (value.Progress > 0)
                return;
            Started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }
    }
}
